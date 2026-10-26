using System.Text.RegularExpressions;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Application.Merchandising;

/// <summary>
/// Checks quoted source text against the parsed document. A quote counts as
/// evidence only when it occurs in the page text (ignoring case and runs of
/// whitespace); a quote that isn't there is never stored as evidence.
/// </summary>
public static partial class MerchEvidence
{
    public const string Verified = "verified";
    public const string NotFound = "not_found";
    public const string None = "none";

    public const int MaxSnippetLength = 500;

    /// <summary>
    /// The page a quote occurs on (the cited page first, then every page) and
    /// the matching text as the document writes it (whitespace collapsed);
    /// null when it occurs nowhere. The stored snippet is always copied from
    /// the document, never from the extractor's answer.
    /// </summary>
    public static (int Page, string Snippet)? Locate(string? quote, int? citedPage, IReadOnlyList<PageText> pages)
    {
        var needle = Normalize(quote);
        if (needle.Length < 2 || needle.Length > MaxSnippetLength) return null;
        var ordered = citedPage is { } p ? pages.OrderBy(x => x.PageNumber == p ? 0 : 1).ThenBy(x => x.PageNumber) : pages.OrderBy(x => x.PageNumber);
        foreach (var page in ordered)
        {
            var (collapsed, map) = CollapseWithMap(page.Text);
            var at = collapsed.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var start = map[at];
            var end = map[at + needle.Length - 1] + 1;
            return (page.PageNumber, Collapse(page.Text[start..end]));
        }
        return null;
    }

    /// <summary>Whitespace-collapsed text (trimmed) and, per character, its index in the original.</summary>
    private static (string Text, List<int> Map) CollapseWithMap(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var map = new List<int>(s.Length);
        var pendingSpace = -1;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                if (sb.Length > 0 && pendingSpace < 0) pendingSpace = i;
                continue;
            }
            if (pendingSpace >= 0) { sb.Append(' '); map.Add(pendingSpace); pendingSpace = -1; }
            sb.Append(s[i]);
            map.Add(i);
        }
        return (sb.ToString(), map);
    }

    public static string Normalize(string? s) => s is null ? "" : Collapse(s).ToLowerInvariant();

    private static string Collapse(string s) => Ws().Replace(s, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Ws();
}
