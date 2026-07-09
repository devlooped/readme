using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Readme;

/// <summary>
/// Resolves <c>&lt;!-- include path --&gt;</c> directives in markdown (and similar) files.
/// Supports nested includes, <c>#fragment</c> sections, and HTTP(S) URLs.
/// Detects circular includes via an ancestry chain of canonical resource keys.
/// Ported from NuGetizer for dual use with SDK Pack and NuGetizer.
/// </summary>
public class IncludesResolver
{
    static readonly Regex IncludeRegex = new(@"<!--\s?include\s(.*?)\s?-->", RegexOptions.Compiled);
    static readonly HttpClient http = new();

    /// <summary>
    /// Processes include directives in <paramref name="filePath"/> (local path or http(s) URL).
    /// </summary>
    /// <param name="filePath">Root file or absolute HTTP(S) URL to process.</param>
    /// <param name="logWarning">Optional warning sink (missing includes, fragments, cycles).</param>
    public static string Process(string filePath, Action<string>? logWarning = default)
        => Process(filePath, logWarning, ancestry: null);

    static string Process(string filePath, Action<string>? logWarning, HashSet<string>? ancestry)
    {
        ancestry ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourceKey = ToResourceKey(filePath);

        // Root (or nested) resource is on the ancestry chain while its includes are expanded.
        ancestry.Add(resourceKey);
        try
        {
            return ProcessCore(filePath, logWarning, ancestry);
        }
        finally
        {
            ancestry.Remove(resourceKey);
        }
    }

    static string ProcessCore(string filePath, Action<string>? logWarning, HashSet<string> ancestry)
    {
        string? content = null;

        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            try
            {
                // Synchronous wait keeps MSBuild task model simple; failures are warnings.
                content = http.GetStringAsync(uri).GetAwaiter().GetResult().Trim();
            }
            catch (Exception ex)
            {
                logWarning?.Invoke($"Failed to resolve include URL: {filePath}. {ex.Message}");
                return "";
            }
        }
        else
        {
            content = File.ReadAllText(filePath).Trim();
        }

        // TODO: removing this for now, since this would prevent a consumer of the
        // resolve includes github action (see https://github.com/marketplace/actions/resolve-file-includes)
        // from excluding the readme from CI-based resolution, while still keeping
        // this (100% compatible) pack-time resolution.

        // Allow self-excluding files for processing. Could be useful if the file itself
        // documents the include/exclude mechanism, for example.
        //if (content.StartsWith("<!-- exclude -->") || content.EndsWith("<!-- exclude -->"))
        //    return content;

        var replacements = new Dictionary<Regex, string>();

        foreach (Match match in IncludeRegex.Matches(content!))
        {
            var includedPath = match.Groups[1].Value.Trim();
            string? fragment = default;
            if (includedPath.Contains("#"))
            {
                fragment = "#" + includedPath.Split('#')[1];
                includedPath = includedPath.Split('#')[0];
            }

            var isUri = Uri.IsWellFormedUriString(includedPath, UriKind.Absolute);
            var includedFullPath = isUri
                ? includedPath
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(filePath) ?? "", includedPath));

            if (isUri || File.Exists(includedFullPath))
            {
                var includedKey = ToResourceKey(isUri ? includedPath : includedFullPath);
                if (ancestry.Contains(includedKey))
                {
                    // Circular include: warn and leave the <!-- include ... --> marker unresolved.
                    logWarning?.Invoke(
                        $"Circular include detected: {includedPath}{fragment} resolves to {includedKey}, which is already being processed.");
                    continue;
                }

                // Resolve nested includes (ancestry tracks the chain; diamond re-includes are allowed).
                var includedContent = Process(isUri ? includedPath : includedFullPath, logWarning, ancestry);
                if (fragment != null)
                {
                    var anchor = $"<!-- {fragment} -->";
                    var start = includedContent.IndexOf(anchor, StringComparison.Ordinal);
                    if (start == -1)
                    {
                        logWarning?.Invoke($"Failed to resolve anchor {fragment} in {includedPath}.");
                        continue;
                    }

                    includedContent = includedContent.Substring(start);
                    var end = includedContent.IndexOf(anchor, anchor.Length, StringComparison.Ordinal);
                    if (end != -1)
                        includedContent = includedContent.Substring(0, end + anchor.Length);
                }

                // see if we already have a section we previously replaced
                var existingRegex = new Regex(@$"<!--\s?include {Regex.Escape(includedPath)}{Regex.Escape(fragment ?? "")}\s?-->[\s\S]*<!-- {Regex.Escape(includedPath)}{Regex.Escape(fragment ?? "")} -->");
                var replacement = $"<!-- include {includedPath}{fragment} -->{Environment.NewLine}{includedContent}{Environment.NewLine}<!-- {includedPath}{fragment} -->";
                if (existingRegex.IsMatch(content!))
                    replacements[existingRegex] = replacement;
                else
                    replacements[new Regex(@$"<!--\s?include {Regex.Escape(includedPath)}{Regex.Escape(fragment ?? "")}\s?-->")] = replacement;
            }
            else
            {
                logWarning?.Invoke($"Failed to resolve include: {includedPath}{fragment}. File not found at expected location {includedFullPath}.");
            }
        }

        if (replacements.Count > 0)
        {
            var updated = content!;
            foreach (var replacement in replacements)
                updated = replacement.Key.Replace(updated, replacement.Value);

            return updated.Trim();
        }

        return content!;
    }

    /// <summary>
    /// Canonical identity for cycle detection: full local path or absolute http(s) URL.
    /// </summary>
    static string ToResourceKey(string filePath)
    {
        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            // Normalize URI form (scheme/host casing) while keeping path semantics of the URL.
            return uri.AbsoluteUri;
        }

        return Path.GetFullPath(filePath);
    }
}
