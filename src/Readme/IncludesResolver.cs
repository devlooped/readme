using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Readme;

/// <summary>
/// Resolves <c>&lt;!-- include path --&gt;</c> directives in markdown (and similar) files.
/// Supports nested includes, <c>#fragment</c> sections, and HTTP(S) URLs.
/// Ported from NuGetizer for dual use with SDK Pack and NuGetizer.
/// </summary>
public class IncludesResolver
{
    static readonly Regex IncludeRegex = new(@"<!--\s?include\s(.*?)\s?-->", RegexOptions.Compiled);
    static readonly HttpClient http = new();

    public static string Process(string filePath, Action<string>? logWarning = default)
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
            var includedFullPath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", includedPath)
                .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (isUri || File.Exists(includedFullPath))
            {
                // Resolve nested includes
                var includedContent = Process(isUri ? includedPath : includedFullPath, logWarning);
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
}
