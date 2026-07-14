using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace Readme;

/// <summary>
/// Options for <see cref="IncludesResolver.Process"/> remote-include policy and fetch.
/// </summary>
public sealed class IncludeResolutionOptions
{
    /// <summary>
    /// Allowed URI schemes for remote includes (e.g. <c>https</c>).
    /// Semicolon-separated values in each entry are split. When empty/null, defaults to <c>https</c> only.
    /// </summary>
    public IEnumerable<string>? AllowedSchemes { get; set; }

    /// <summary>
    /// Hosts allowed for absolute remote includes originating from remote content.
    /// Exact host or subdomain match (DNS label boundary). Local-file → remote hops ignore this list.
    /// </summary>
    public IEnumerable<string>? AllowedDomains { get; set; }

    /// <summary>
    /// Optional remote content fetcher (tests / custom transport). Default uses <see cref="HttpClient"/>.
    /// </summary>
    public Func<Uri, string>? FetchRemoteContent { get; set; }
}

/// <summary>
/// Resolves <c>&lt;!-- include path --&gt;</c> directives in markdown (and similar) files.
/// Supports nested includes, <c>#fragment</c> sections, and HTTP(S) URLs.
/// Detects circular includes via an ancestry chain of canonical resource keys.
/// Includes inside fenced code blocks with language <c>exclude</c> are left unresolved
/// so documentation can show the include syntax literally.
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
        => Process(filePath, logWarning, options: null);

    /// <summary>
    /// Processes include directives with scheme/domain policy and optional remote fetch.
    /// </summary>
    public static string Process(string filePath, Action<string>? logWarning, IncludeResolutionOptions? options)
        => Process(filePath, logWarning, options, ancestry: null);

    static string Process(
        string filePath,
        Action<string>? logWarning,
        IncludeResolutionOptions? options,
        HashSet<string>? ancestry)
    {
        ancestry ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourceKey = ToResourceKey(filePath);

        // Root (or nested) resource is on the ancestry chain while its includes are expanded.
        ancestry.Add(resourceKey);
        try
        {
            return ProcessCore(filePath, logWarning, options, ancestry);
        }
        finally
        {
            ancestry.Remove(resourceKey);
        }
    }

    static string ProcessCore(
        string filePath,
        Action<string>? logWarning,
        IncludeResolutionOptions? options,
        HashSet<string> ancestry)
    {
        var schemes = NormalizeSchemes(options?.AllowedSchemes);
        var domains = NormalizeDomains(options?.AllowedDomains);
        var isRemoteResource = TryGetHttpUri(filePath, out var selfUri);

        string? content = null;

        if (isRemoteResource)
        {
            try
            {
                content = FetchRemote(selfUri!, options).Trim();
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

        // Position-based expansions so the same include path outside an ```exclude block
        // can resolve while the literal copy inside the block is left alone.
        var expansions = new List<(int Index, int Length, string Text)>();

        // Context for includes nested inside *this* resource:
        // - local file: nested absolute remotes are always domain-safe
        // - remote file: nested absolute remotes need allowlist / same host / subdomain
        var thisIsRemote = isRemoteResource;
        var thisRemoteHost = isRemoteResource ? selfUri!.Host : null;

        // Includes inside ```exclude / ~~~exclude fenced blocks are left literal
        // so docs can show the include syntax without resolving it.
        var excludeRanges = FindExcludeCodeBlockRanges(content!);

        foreach (Match match in IncludeRegex.Matches(content!))
        {
            if (IsIndexInsideRanges(match.Index, excludeRanges))
                continue;

            // Skip include markers that sit inside a span we already scheduled (re-process of nested expands).
            if (IsIndexInsideExpansion(match.Index, expansions))
                continue;

            var includedPath = match.Groups[1].Value.Trim();
            string? fragment = default;
            if (includedPath.Contains("#"))
            {
                fragment = "#" + includedPath.Split('#')[1];
                includedPath = includedPath.Split('#')[0];
            }

            if (!TryResolveIncludeTarget(
                    filePath,
                    thisIsRemote,
                    selfUri,
                    includedPath,
                    out var targetPath,
                    out var targetIsRemote,
                    out var targetUri,
                    out var resolveError))
            {
                logWarning?.Invoke(resolveError ?? $"Failed to resolve include: {includedPath}{fragment}.");
                continue;
            }

            if (targetIsRemote)
            {
                if (!schemes.Contains(targetUri!.Scheme))
                {
                    logWarning?.Invoke(
                        $"Blocked include URL scheme '{targetUri.Scheme}' for {includedPath}{fragment}. " +
                        $"Allowed schemes: {string.Join(", ", schemes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}.");
                    continue;
                }

                // Local → remote absolute: always domain-safe. Remote → remote: host policy.
                if (thisIsRemote &&
                    !IsHostAllowed(targetUri.Host, domains, thisRemoteHost))
                {
                    logWarning?.Invoke(
                        $"Blocked include URL host '{targetUri.Host}' for {includedPath}{fragment}. " +
                        "Absolute remote includes from remote content require a host in @(ReadmeIncludeDomain), " +
                        "or the same host / a subdomain of the including remote resource.");
                    continue;
                }
            }
            else if (!File.Exists(targetPath))
            {
                logWarning?.Invoke(
                    $"Failed to resolve include: {includedPath}{fragment}. File not found at expected location {targetPath}.");
                continue;
            }

            var includedKey = ToResourceKey(targetPath);
            if (ancestry.Contains(includedKey))
            {
                // Circular include: warn and leave the <!-- include ... --> marker unresolved.
                logWarning?.Invoke(
                    $"Circular include detected: {includedPath}{fragment} resolves to {includedKey}, which is already being processed.");
                continue;
            }

            // Resolve nested includes (ancestry tracks the chain; diamond re-includes are allowed).
            var includedContent = Process(targetPath, logWarning, options, ancestry);

            if (fragment != null)
            {
                // Explicit full-line <!-- #fragment --> HtmlBlocks win over heading auto-anchors.
                // Inline mentions (e.g. inside `code` or table cells) are ignored so docs can
                // describe the syntax without truncating the slice.
                if (TryExtractCommentAnchorSection(includedContent, fragment, out var anchorSection))
                {
                    includedContent = anchorSection;
                }
                else if (TryExtractHeadingSection(includedContent, fragment.Substring(1), out var headingSection))
                {
                    // Auto-anchor slice always includes the matching heading line (e.g. "## Usage").
                    includedContent = headingSection;
                }
                else
                {
                    logWarning?.Invoke($"Failed to resolve anchor {fragment} in {includedPath}.");
                    continue;
                }
            }

            var replacement = $"<!-- include {includedPath}{fragment} -->{Environment.NewLine}{includedContent}{Environment.NewLine}<!-- {includedPath}{fragment} -->";

            // Idempotent re-process: if this open marker already wraps expanded content through a
            // matching close, replace the whole span. Do not span across a *second* open of the
            // same path (bare docs example + real expanded include later in the file).
            var length = GetIncludeReplacementLength(
                content!, match.Index, match.Length, includedPath, fragment);

            expansions.Add((match.Index, length, replacement));
        }

        if (expansions.Count == 0)
            return content!;

        return ApplyExpansions(content!, expansions).Trim();
    }

    static bool IsIndexInsideExpansion(int index, List<(int Index, int Length, string Text)> expansions)
    {
        foreach (var (start, length, _) in expansions)
        {
            if (index >= start && index < start + length)
                return true;
        }

        return false;
    }

    static string ApplyExpansions(string content, List<(int Index, int Length, string Text)> expansions)
    {
        // Matches are collected left-to-right and non-overlapping; splice in one pass.
        var sb = new StringBuilder(content.Length);
        var pos = 0;
        foreach (var (index, length, text) in expansions.OrderBy(e => e.Index))
        {
            if (index < pos)
                continue; // overlapping guard (should not happen)

            sb.Append(content, pos, index - pos);
            sb.Append(text);
            pos = index + length;
        }

        if (pos < content.Length)
            sb.Append(content, pos, content.Length - pos);

        return sb.ToString();
    }

    /// <summary>
    /// Resolves an include path against a local or remote parent into a fetch/read target.
    /// </summary>
    static bool TryResolveIncludeTarget(
        string parentPath,
        bool parentIsRemote,
        Uri? parentUri,
        string includedPath,
        out string targetPath,
        out bool targetIsRemote,
        out Uri? targetUri,
        out string? error)
    {
        targetPath = "";
        targetIsRemote = false;
        targetUri = null;
        error = null;

        var includeIsAbsoluteUri = Uri.IsWellFormedUriString(includedPath, UriKind.Absolute);

        if (parentIsRemote && parentUri != null)
        {
            // Remote parent: relative paths combine against the parent URI base.
            try
            {
                var resolved = includeIsAbsoluteUri
                    ? new Uri(includedPath, UriKind.Absolute)
                    : new Uri(parentUri, includedPath);

                if (resolved.Scheme == "http" || resolved.Scheme == "https")
                {
                    targetUri = resolved;
                    targetPath = resolved.AbsoluteUri;
                    targetIsRemote = true;
                    return true;
                }

                error = $"Unsupported include URI scheme '{resolved.Scheme}' for {includedPath}.";
                return false;
            }
            catch (UriFormatException ex)
            {
                error = $"Failed to resolve include '{includedPath}' against remote base '{parentUri}': {ex.Message}";
                return false;
            }
        }

        // Local parent
        if (includeIsAbsoluteUri &&
            Uri.TryCreate(includedPath, UriKind.Absolute, out var abs) &&
            (abs.Scheme == "http" || abs.Scheme == "https"))
        {
            targetUri = abs;
            targetPath = abs.AbsoluteUri;
            targetIsRemote = true;
            return true;
        }

        if (includeIsAbsoluteUri)
        {
            // Non-http absolute URI: leave as path string (legacy / rare).
            targetPath = includedPath;
            targetIsRemote = false;
            return true;
        }

        targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentPath) ?? "", includedPath));
        targetIsRemote = false;
        return true;
    }

    static string FetchRemote(Uri uri, IncludeResolutionOptions? options)
    {
        if (options?.FetchRemoteContent != null)
            return options.FetchRemoteContent(uri);

        // Synchronous wait keeps MSBuild task model simple; failures are warnings.
        return http.GetStringAsync(uri).GetAwaiter().GetResult();
    }

    /// <summary>
    /// True when <paramref name="host"/> equals <paramref name="allowedRoot"/> or is a DNS subdomain of it.
    /// Also true when <paramref name="host"/> matches any entry in <paramref name="allowedDomains"/> (or is a subdomain of one).
    /// </summary>
    public static bool IsHostAllowed(string host, IEnumerable<string>? allowedDomains, string? includingRemoteHost)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (!string.IsNullOrWhiteSpace(includingRemoteHost) &&
            IsSameHostOrSubdomain(host, includingRemoteHost!))
            return true;

        if (allowedDomains == null)
            return false;

        foreach (var domain in allowedDomains)
        {
            if (string.IsNullOrWhiteSpace(domain))
                continue;
            if (IsSameHostOrSubdomain(host, domain.Trim()))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Case-insensitive host equality or DNS-label subdomain (<c>foo.example.com</c> of <c>example.com</c>).
    /// Rejects string-suffix traps (<c>notexample.com</c> is not under <c>example.com</c>).
    /// </summary>
    public static bool IsSameHostOrSubdomain(string host, string root)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(root))
            return false;

        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        root = root.Trim().TrimEnd('.').ToLowerInvariant();

        if (host.Length == 0 || root.Length == 0)
            return false;

        if (string.Equals(host, root, StringComparison.Ordinal))
            return true;

        // Subdomain requires a dot boundary: host == "*." + root
        return host.Length > root.Length + 1 &&
               host.EndsWith("." + root, StringComparison.Ordinal);
    }

    internal static HashSet<string> NormalizeSchemes(IEnumerable<string>? schemes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (schemes != null)
        {
            foreach (var entry in schemes)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                foreach (var part in entry.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0)
                        set.Add(trimmed);
                }
            }
        }

        if (set.Count == 0)
            set.Add("https");

        return set;
    }

    internal static HashSet<string> NormalizeDomains(IEnumerable<string>? domains)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (domains == null)
            return set;

        foreach (var entry in domains)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var trimmed = entry.Trim().TrimEnd('.');
            if (trimmed.Length > 0)
                set.Add(trimmed);
        }

        return set;
    }

    static bool TryGetHttpUri(string path, out Uri? uri)
    {
        uri = null;
        if (Uri.TryCreate(path, UriKind.Absolute, out var created) &&
            (created.Scheme == "http" || created.Scheme == "https"))
        {
            uri = created;
            return true;
        }

        return false;
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
    /// (without leading <c>#</c>). The matching heading line itself is included (for example
    /// <c>## Usage</c> for <c>usage</c>), through the line before the next heading of the same or
    /// higher level (or EOF). Prefer explicit <c>&lt;!-- #fragment --&gt;</c> markers when callers need
    /// to control whether the section title is part of the slice. Returns false when no heading matches.
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
        => TryParseFenceLine(line, out _, out _, out _);

    /// <summary>
    /// Character ranges of fenced code blocks whose info-string language is <c>exclude</c>
    /// (<c>```exclude</c> / <c>~~~exclude</c>). Includes inside these ranges are not resolved.
    /// Uses Markdig's CommonMark fence parser so edge cases (indent, fence length, nested structure)
    /// match real Markdown rather than a hand-rolled line scanner.
    /// </summary>
    static List<(int Start, int End)> FindExcludeCodeBlockRanges(string content)
    {
        var ranges = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(content))
            return ranges;

        var document = Markdown.Parse(content);
        foreach (var block in document.Descendants<FencedCodeBlock>())
        {
            if (!IsExcludeFenceLanguage(block.Info))
                continue;

            // Markdig SourceSpan is inclusive; convert to half-open [Start, End).
            var start = block.Span.Start;
            var endExclusive = block.Span.End + 1;
            if (start < 0 || endExclusive <= start)
                continue;

            if (endExclusive > content.Length)
                endExclusive = content.Length;

            ranges.Add((start, endExclusive));
        }

        return ranges;
    }

    /// <summary>
    /// Extracts the section between the first two full-line <c>&lt;!-- #fragment --&gt;</c> markers
    /// (Markdig <see cref="HtmlBlock"/>s). Inline occurrences inside paragraphs, tables, or code
    /// spans are not HtmlBlocks and are ignored — so documentation can mention the syntax safely.
    /// When only an opening marker exists, returns from that marker through EOF (same as before).
    /// </summary>
    static bool TryExtractCommentAnchorSection(string content, string fragment, out string section)
    {
        section = "";
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(fragment))
            return false;

        // fragment is "#name" (leading hash from the include path).
        var anchor = $"<!-- {fragment} -->";
        var document = Markdown.Parse(content);
        var anchors = new List<HtmlBlock>();
        foreach (var block in document.Descendants<HtmlBlock>())
        {
            if (IsExactHtmlCommentAnchor(content, block, anchor))
                anchors.Add(block);
        }

        if (anchors.Count == 0)
            return false;

        var start = anchors[0].Span.Start;
        if (start < 0 || start >= content.Length)
            return false;

        if (anchors.Count >= 2)
        {
            // Inclusive Span.End → exclusive slice end; include the closing marker line.
            var endExclusive = anchors[1].Span.End + 1;
            if (endExclusive > content.Length)
                endExclusive = content.Length;
            if (endExclusive < start)
                return false;

            section = content.Substring(start, endExclusive - start);
            return true;
        }

        // Single opening marker: through EOF (caller may still use heading fallback when none).
        section = content.Substring(start);
        return true;
    }

    static bool IsExactHtmlCommentAnchor(string content, HtmlBlock block, string anchor)
    {
        var start = block.Span.Start;
        var endExclusive = block.Span.End + 1;
        if (start < 0 || endExclusive <= start || start >= content.Length)
            return false;
        if (endExclusive > content.Length)
            endExclusive = content.Length;

        var text = content.Substring(start, endExclusive - start).Trim();
        // HtmlBlock span may include a trailing newline; Trim handles it.
        return string.Equals(text, anchor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Length of the span to replace for an include match. When the open marker is already followed
    /// by expanded body and a matching close <c>&lt;!-- path --&gt;</c> with no second open of the
    /// same path in between, returns the full open…close length (idempotent re-process). Otherwise
    /// returns just the open marker length.
    /// </summary>
    static int GetIncludeReplacementLength(
        string content,
        int matchIndex,
        int matchLength,
        string includedPath,
        string? fragment)
    {
        var pathWithFragment = includedPath + (fragment ?? "");
        var closeMarker = $"<!-- {pathWithFragment} -->";
        var searchFrom = matchIndex + matchLength;
        if (searchFrom > content.Length)
            return matchLength;

        var closeIdx = content.IndexOf(closeMarker, searchFrom, StringComparison.Ordinal);
        if (closeIdx < 0)
            return matchLength;

        // Another bare/open include of the same path before this close belongs to a different instance.
        var reopenPattern = $@"<!--\s?include\s{Regex.Escape(pathWithFragment)}\s?-->";
        var reopen = Regex.Match(
            content.Substring(searchFrom, closeIdx - searchFrom),
            reopenPattern);
        if (reopen.Success)
            return matchLength;

        return (closeIdx + closeMarker.Length) - matchIndex;
    }

    static bool IsExcludeFenceLanguage(string? infoString)
    {
        if (string.IsNullOrWhiteSpace(infoString))
            return false;

        // Markdig puts the language in Info (first info-string token); tolerate extra tokens.
        var token = infoString!.Trim();
        var space = token.IndexOfAny(new[] { ' ', '\t' });
        if (space >= 0)
            token = token.Substring(0, space);

        return string.Equals(token, "exclude", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsIndexInsideRanges(int index, List<(int Start, int End)> ranges)
    {
        foreach (var (start, end) in ranges)
        {
            // [Start, End) half-open.
            if (index >= start && index < end)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a CommonMark-style fence line: 0–3 spaces, 3+ <c>`</c> or <c>~</c>, optional info string.
    /// Used when scanning for headings (skip fenced regions) without a full Markdig re-parse per line.
    /// </summary>
    static bool TryParseFenceLine(string line, out char fenceChar, out int fenceLength, out string? infoString)
    {
        fenceChar = default;
        fenceLength = 0;
        infoString = null;

        var leading = 0;
        while (leading < line.Length && leading < 4 && line[leading] == ' ')
            leading++;

        if (leading > 3 || leading >= line.Length)
            return false;

        var c = line[leading];
        if (c != '`' && c != '~')
            return false;

        var i = leading;
        while (i < line.Length && line[i] == c)
            i++;

        fenceLength = i - leading;
        if (fenceLength < 3)
            return false;

        fenceChar = c;
        var rest = i < line.Length ? line.Substring(i) : "";
        // Backtick fences cannot have backticks in the info string (CommonMark).
        if (c == '`' && rest.IndexOf('`') >= 0)
            return false;

        infoString = rest.Trim();
        return true;
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
