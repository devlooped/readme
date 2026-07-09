using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
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
    static readonly Regex SimpleLinkRegex = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    static readonly Regex SimpleRefLinkRegex = new(@"\[([^\]]+)\]\[[^\]]*\]", RegexOptions.Compiled);
    static readonly Regex InlineCodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);
    static readonly Regex StrongStarRegex = new(@"\*\*([^*]+)\*\*", RegexOptions.Compiled);
    // Underscore emphasis only at identifier boundaries so snake_case (get_user_name) keeps `_`
    // (github-slugger preserves underscores; mid-word `_…_` is not GFM emphasis).
    static readonly Regex StrongUnderRegex = new(@"(?<![A-Za-z0-9_])__([^_\n]+)__(?![A-Za-z0-9_])", RegexOptions.Compiled);
    static readonly Regex EmStarRegex = new(@"\*([^*]+)\*", RegexOptions.Compiled);
    static readonly Regex EmUnderRegex = new(@"(?<![A-Za-z0-9_])_([^_\n]+)_(?![A-Za-z0-9_])", RegexOptions.Compiled);
    static readonly Regex StrikeRegex = new(@"~~([^~]+)~~", RegexOptions.Compiled);
    static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
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
                    if (start != -1)
                    {
                        // Explicit comment anchors win over heading auto-anchors.
                        includedContent = includedContent.Substring(start);
                        var end = includedContent.IndexOf(anchor, anchor.Length, StringComparison.Ordinal);
                        if (end != -1)
                            includedContent = includedContent.Substring(0, end + anchor.Length);
                    }
                    else if (TryExtractHeadingSection(includedContent, fragment.Substring(1), out var headingSection))
                    {
                        includedContent = headingSection;
                    }
                    else
                    {
                        logWarning?.Invoke($"Failed to resolve anchor {fragment} in {includedPath}.");
                        continue;
                    }
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
    /// Generates a GitHub-style heading slug (no uniqueness suffix).
    /// Matches github-slugger: lowercase, strip punctuation, spaces → hyphens.
    /// </summary>
    public static string GitHubHeadingSlug(string headingText)
    {
        if (string.IsNullOrEmpty(headingText))
            return "";

        // GitHub: leading/trailing whitespace removed before slug rules.
        var value = StripHeadingMarkup(headingText).Trim().ToLowerInvariant();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('-');
            else if (c > 127 && char.IsLetterOrDigit(c))
                sb.Append(c);
            // else drop punctuation / symbols (github-slugger style)
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts the ATX heading section whose GitHub auto-anchor matches <paramref name="fragmentName"/>
    /// (without leading <c>#</c>). Section runs from the matching heading through the line before the next
    /// heading of the same or higher level (or EOF). Returns false when no heading matches.
    /// </summary>
    public static bool TryExtractHeadingSection(string content, string fragmentName, out string section)
    {
        section = "";
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(fragmentName))
            return false;

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // github-slugger occurrence map: each assigned slug key → 0 once used; base slug counts for -N.
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var inFence = false;
        int? matchStart = null;
        var matchLevel = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (IsCodeFenceLine(line))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || !TryParseAtxHeading(line, out var level, out var text))
                continue;

            var slug = AssignUniqueSlug(GitHubHeadingSlug(text), occurrences);

            if (matchStart == null)
            {
                if (string.Equals(slug, fragmentName, StringComparison.Ordinal))
                {
                    matchStart = i;
                    matchLevel = level;
                }

                continue;
            }

            // End section at next heading of same or higher level (smaller/equal # count).
            if (level <= matchLevel)
            {
                section = JoinLines(lines, matchStart.Value, i);
                return true;
            }
        }

        if (matchStart != null)
        {
            section = JoinLines(lines, matchStart.Value, lines.Length);
            return true;
        }

        return false;
    }

    static string AssignUniqueSlug(string baseSlug, Dictionary<string, int> occurrences)
    {
        // Port of github-slugger BananaSlug.slug uniqueness.
        var result = baseSlug;
        var originalSlug = baseSlug;

        while (occurrences.ContainsKey(result))
        {
            // originalSlug is always present once the first slug was registered.
            occurrences[originalSlug] = occurrences[originalSlug] + 1;
            result = originalSlug + "-" + occurrences[originalSlug];
        }

        occurrences[result] = 0;
        return result;
    }

    static string StripHeadingMarkup(string text)
    {
        // Best-effort GFM inline markup strip so _italics_ / **bold** / links match GitHub anchors.
        text = SimpleLinkRegex.Replace(text, "$1");
        text = SimpleRefLinkRegex.Replace(text, "$1");
        text = InlineCodeRegex.Replace(text, "$1");
        text = StrongStarRegex.Replace(text, "$1");
        text = StrongUnderRegex.Replace(text, "$1");
        text = EmStarRegex.Replace(text, "$1");
        text = EmUnderRegex.Replace(text, "$1");
        text = StrikeRegex.Replace(text, "$1");
        text = HtmlTagRegex.Replace(text, "");
        return text;
    }

    static bool TryParseAtxHeading(string line, out int level, out string text)
    {
        level = 0;
        text = "";

        var i = 0;
        // CommonMark: up to three leading spaces before the opening #.
        while (i < line.Length && i < 3 && line[i] == ' ')
            i++;

        var hashCount = 0;
        while (i < line.Length && hashCount < 6 && line[i] == '#')
        {
            hashCount++;
            i++;
        }

        if (hashCount == 0)
            return false;

        // Require whitespace after the hashes (distinguishes headings from #tags).
        if (i >= line.Length || (line[i] != ' ' && line[i] != '\t'))
            return false;

        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;

        text = i < line.Length ? line.Substring(i) : "";
        // Strip optional closed-ATX trailing hashes: "Usage ##"
        text = Regex.Replace(text, @"[ \t]+#*[ \t]*$", "");
        text = text.TrimEnd();
        level = hashCount;
        return true;
    }

    static bool IsCodeFenceLine(string line)
    {
        var leading = 0;
        while (leading < line.Length && leading < 4 && line[leading] == ' ')
            leading++;

        if (leading > 3)
            return false;

        if (leading >= line.Length)
            return false;

        var rest = line.Substring(leading);
        return rest.StartsWith("```", StringComparison.Ordinal) ||
               rest.StartsWith("~~~", StringComparison.Ordinal);
    }

    static string JoinLines(string[] lines, int startInclusive, int endExclusive)
    {
        if (startInclusive >= endExclusive)
            return "";

        var sb = new StringBuilder();
        for (var i = startInclusive; i < endExclusive; i++)
        {
            if (i > startInclusive)
                sb.Append('\n');
            sb.Append(lines[i]);
        }

        return sb.ToString();
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
