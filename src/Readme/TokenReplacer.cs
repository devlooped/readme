using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Readme;

/// <summary>
/// Replaces NuGet-style <c>$token$</c> placeholders (case-insensitive token names).
/// Same algorithm as NuGetizer's readme/license token replacement.
/// </summary>
public static class TokenReplacer
{
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
}
