using System.Text;
using System.Text.RegularExpressions;

namespace FactoryBrain.Application.Rag;

/// <summary>
/// Acronym / synonym expansion used to bridge Bangla content and the
/// mostly-English seed corpus. The factory floor mixes English acronyms
/// (DHU, SMV, AQL, Juki / Brother machine codes) with Bangla descriptions,
/// and the dotnet tokeniser splits on whitespace + a small punctuation set
/// (see <see cref="HybridScorer"/>), so a query like <c>needle breakage</c>
/// will not match a chunk that only says <c>সুই ভাঙা</c>. This class adds
/// the bridge by rewriting the query string before it hits the scorer:
/// every recognised key (case-insensitive, ASCII-folded) gets its
/// expansions appended as extra tokens, so BM25 still matches.
///
/// <para>
/// Per the brief, at minimum the map covers:
/// DHU, SMV, AQL, needle, breakage, efficiency, line, plus Juki / Brother
/// machine families and the canonicalised <c>E-NN</c> / <c>ENN</c> error
/// codes both vendors emit.
/// </para>
/// </summary>
public static class RagSynonyms
{
    /// <summary>
    /// The canonical token → list of expansions. Lookups are case-insensitive
    /// on the lowercased key. Empty expansion lists are silently dropped at
    /// query time.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Map =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dhu"] = new[] { "ডিএইচইউ", "defects per hundred units", "ত্রুটি", "ত্রুটি প্রতি শত ইউনিট" },
            ["smv"] = new[] { "এসএমভি", "standard minute value", "স্ট্যান্ডার্ড মিনিট ভ্যালু" },
            ["aql"] = new[] { "একিউএল", "acceptable quality level" },
            ["needle"]      = new[] { "সুই", "needles" },
            ["breakage"]    = new[] { "ভাঙা", "ভাঙ্গা", "broken", "break", "breaks" },
            ["efficiency"]  = new[] { "দক্ষতা", "efficient", "efficiencies" },
            ["line"]        = new[] { "লাইন", "production line", "production lines", "lines" },
            ["juki"]        = new[] { "জুকি", "juki ddl-8700", "juki ddl-9000c", "juki lu-563" },
            ["brother"]     = new[] { "ব্রাদার", "brother bas-311h", "brother s-7300a" },
            // Bangla → English bridges so queries like "অগ্নি নিরাপত্তা"
            // surface the English fire-safety checklist chunks.
            ["অগ্নি"]          = new[] { "fire", "আগুন", "অগ্নিকাণ্ড" },
            ["আগুন"]          = new[] { "fire", "অগ্নি", "অগ্নিকাণ্ড" },
            ["নিরাপত্তা"]      = new[] { "safety", "সুরক্ষা" },
        };

    /// <summary>
    /// Match an error code in any of the common shapes we see in service
    /// manuals: <c>E12</c>, <c>E-12</c>, <c>e 12</c>, <c>ERR12</c>. Captures
    /// the letter prefix and the digit run, and we re-emit it as the
    /// canonical <c>E-12</c> form so the chunker's protected-code list and
    /// our tokeniser both see one shape.
    /// </summary>
    public static readonly Regex ErrorCodeRegex = new(
        @"\b([A-Za-z]{1,3})[\s\-_]*?(\d{2,4})\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Safety cap on the number of tokens we emit. Beyond this we log a
    /// warning and stop appending so a pathological input can't grow the
    /// query without bound.
    /// </summary>
    public const int MaxExpansionTokens = 50;

    /// <summary>
    /// Weight applied to keyword-score contributions that arrived through
    /// a one-level synonym expansion. Synonym hits are real evidence, but
    /// they're noisier than literal matches (e.g. "fire" is a synonym of
    /// both "অগ্নি" and "আগুন" but is more general), so we scale them
    /// below 1.0 inside the BM25 accumulator. Step 48d.
    /// </summary>
    public const double SynonymWeight = 0.7;

    /// <summary>
    /// Returns the input query with each recognised key's expansions
    /// appended (space-separated). Acronyms whose expansions are missing
    /// or empty are skipped. The original query text is preserved verbatim
    /// at the front so existing literal matches still win.
    /// </summary>
    /// <remarks>
    /// Also canonicalises Juki / Brother error codes: any <c>E12</c>,
    /// <c>E-12</c>, <c>e 12</c> shape is re-emitted as <c>E-12</c> so both
    /// the chunker's protected-code list and our tokeniser see one shape.
    /// </remarks>
    public static string ApplyToQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        // 1. Normalise error codes. We don't *append* — we *rewrite* the
        //    matched span to its canonical E-12 form, which is what the
        //    chunker protects. This is the cheapest, most precise
        //    expansion we can do.
        var canonical = ErrorCodeRegex.Replace(query, m =>
            $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}");

        // 2. Walk the canonicalised query, look up every token in the map,
        //    append expansions.
        var sb = new StringBuilder(canonical);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int emitted = 0;
        foreach (var raw in HybridScorer.TokenizeForTest(canonical))
        {
            if (emitted >= MaxExpansionTokens) break;
            if (!seen.Add(raw)) continue;
            if (!Map.TryGetValue(raw, out var expansions)) continue;
            if (expansions is null || expansions.Count == 0) continue;
            foreach (var e in expansions)
            {
                if (emitted >= MaxExpansionTokens) break;
                if (string.IsNullOrWhiteSpace(e)) continue;
                sb.Append(' ').Append(e);
                emitted++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Exposes the map for tests / debug. Do NOT log the result at
    /// startup — it leaks a curated vocabulary that is harmless on its
    /// own but still considered an internal artefact.
    /// </summary>
    public static IEnumerable<(string Canonical, string Alias)> Aliases
    {
        get
        {
            foreach (var kv in Map)
                foreach (var alias in kv.Value)
                    yield return (kv.Key, alias);
        }
    }

    /// <summary>
    /// Returns the one-level synonym expansion of <paramref name="token"/>.
    /// Yields nothing for tokens that are not in <see cref="Map"/> or whose
    /// entry is empty. Expansion is single-hop by design: the caller is
    /// responsible for not chaining further. Step 48d.
    /// </summary>
    public static IEnumerable<string> ExpandToken(string token)
    {
        if (string.IsNullOrEmpty(token)) yield break;
        if (!Map.TryGetValue(token, out var expansions) || expansions is null) yield break;
        foreach (var e in expansions)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            yield return e;
        }
    }
}
