using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Readme;

/// <summary>
/// Replaces NuGet-style <c>$token$</c> placeholders (case-insensitive token names).
/// Same algorithm as NuGetizer's readme/license token replacement.
/// Duplicate names keep the last value (never fails) so Readme and NuGetizer can coexist.
/// </summary>
public static class TokenReplacer
{
    /// <summary>
    /// Matches <c>$name$</c> placeholders where <c>name</c> has no whitespace or <c>$</c>.
    /// </summary>
    static readonly Regex PlaceholderRegex = new(
        @"\$([^$\s]+)\$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Replace <c>$name$</c> tokens in <paramref name="text"/> using the given name/value pairs.
    /// Duplicate names keep the last value. Token names match case-insensitively.
    /// </summary>
    public static string Replace(string text, IEnumerable<(string Name, string Value)> tokens)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var map = tokens
            .Where(t => !string.IsNullOrEmpty(t.Name))
            .Select(t => (Name: t.Name.ToLowerInvariant(), t.Value))
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.Last().Value ?? "");

        if (map.Count == 0)
            return text;

        var expr = new Regex(
            @"\$(" + string.Join("|", map.Keys.Select(Regex.Escape)) + @")\$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return expr.Replace(text, match => map[match.Groups[1].Value.ToLowerInvariant()]);
    }

    /// <summary>
    /// Returns distinct placeholder names found in <paramref name="text"/> (original casing of first occurrence).
    /// Used after replacement to surface unknown tokens (e.g. task warning RDM001).
    /// </summary>
    public static IReadOnlyList<string> FindPlaceholders(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (Match match in PlaceholderRegex.Matches(text))
        {
            var name = match.Groups[1].Value;
            if (seen.Add(name))
                names.Add(name);
        }

        return names;
    }
}
