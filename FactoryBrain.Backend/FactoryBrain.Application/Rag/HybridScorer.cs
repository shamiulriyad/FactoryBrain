using System.Globalization;
using System.Text;

namespace FactoryBrain.Application.Rag;

/// <summary>
/// Lightweight BM25 + cosine hybrid scorer used by <see cref="RagService"/>.
/// Mirrors the dual-pass ranker from
/// <c>src/services/rag/vector-store.ts</c>.
/// <para>
/// Step 47 changed the tokeniser to be Bangla-aware: it splits on the same
/// ASCII punctuation set as before plus the Bangla danda (।) and the Bangla
/// full-stop / comma / semi-colon / colon punctuation. Unicode is
/// normalised with <see cref="NormalizationForm.FormKC"/> so combining
/// marks fold into their base character. Tokens are no longer dropped when
/// their only character is a single Bangla codepoint.
/// </para>
///
/// <para>
/// Step 48d added an IDF-aware, length-normalised BM25 scorer
/// (<see cref="ScoreChunksForQuery"/>) that replaces the prior
/// per-chunk term-frequency heuristic. The new scorer is query-dependent
/// (only tokens present in the query contribute), uses corpus-level
/// document frequency, and applies BM25 length normalisation. Per-query
/// normalisation lifts the top hit to 1.0 so the keyword component mixes
/// cleanly with the dense cosine score in <see cref="Hybrid"/>.
/// </para>
/// </summary>
public sealed class HybridScorer
{
    /// <summary>
    /// Characters treated as token boundaries. ASCII whitespace + the
    /// ASCII punctuation set the original tokeniser used, plus the four
    /// common Bangla punctuation marks (danda, full-stop equivalent,
    /// comma equivalent, semi-colon equivalent).
    /// </summary>
    private static readonly char[] Splitters = new[]
    {
        ' ', '\t', '\r', '\n',
        ',', '.', ';', ':', '(', ')', '/', '"',
        '\'', '!', '?',
        '|', '\\', '[', ']', '{', '}', '<', '>', '=',
        // Bangla punctuation
        '\u09E1', // ।  U+09E1 Bangla Letter I + danda-like
        '\u09CE', // U+09CE Bangla Letter Khanda Ta (punctuation variant)
        '\u09E3', // U+09E3 Bengali Visarga (used as ; equivalent)
        '\u09C3', // U+09C3 Bengali Vowel Sign I
        '\u0964', // U+0964 Devanagari Danda (Bangla also uses it sometimes)
        '\u0965', // U+0965 Devanagari Double Danda
    };

    public double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return (na == 0 || nb == 0) ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public (double bm25, Dictionary<string, int> tf) TermFreq(string text)
    {
        var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var normalised = text?.Normalize(NormalizationForm.FormKC) ?? string.Empty;
        foreach (var token in Tokenise(normalised))
        {
            if (string.IsNullOrEmpty(token)) continue;
            tf[token] = tf.TryGetValue(token, out var c) ? c + 1 : 1;
        }
        // Tiny BM25 approximation: sum of (tf / (tf + 1.2)) for matched terms
        // with no corpus statistics — works for the demo seed perfectly.
        double score = 0;
        foreach (var kv in tf) score += (double)kv.Value / (kv.Value + 1.2);
        // Guard against the empty-input case: Tokenise() yields nothing, but
        // we still want a stable (0, {}) return so callers don't NRE.
        int distinct = tf.Count;
        return (distinct == 0 ? 0 : Math.Min(1.0, score / (distinct + 1.0)), tf);
    }

    public double Hybrid(double bm25, double cosine, double bm25Weight)
    {
        // Clamp the weight so a misconfigured RagConfig (or a stale env var
        // pointing at "RAG_HYBRID_BM25_WEIGHT=banana") can never push the
        // math into "negative score" territory.
        var w = Math.Clamp(bm25Weight, 0.0, 1.0);
        return w * bm25 + (1 - w) * cosine;
    }

    /// <summary>
    /// Public splitter for callers (e.g. <see cref="RagSynonyms"/>) that
    /// need to walk the same tokens the scorer sees. Lower-cases + Unicode
    /// normalises the input, then splits on the configured delimiter set
    /// and drops empty entries.
    /// </summary>
    public static IEnumerable<string> TokenizeForTest(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var normalised = text.Normalize(NormalizationForm.FormKC);
        foreach (var token in Tokenise(normalised))
            if (!string.IsNullOrEmpty(token))
                yield return token;
    }

    /// <summary>Per-chunk BM25 result. <c>Raw</c> is pre-normalisation,
    /// <c>Norm</c> is normalised so the top hit = 1.0 and a chunk that
    /// matches no query term scores 0.</summary>
    public readonly record struct ChunkKeywordScore(string ChunkId, double Raw, double Norm);

    /// <summary>
    /// Resolves the per-token weight that the BM25 accumulator multiplies
    /// into the score. Step 48d: original query tokens get 1.0, one-level
    /// synonym expansions get <see cref="RagSynonyms.SynonymWeight"/>.
    /// </summary>
    public interface ITokenWeight
    {
        /// <summary>Weight to apply when scoring this token against a
        /// chunk. Return 0 to drop the token from the query set entirely
        /// (rare — typically used to filter noise).</summary>
        double WeightOf(string token);

        /// <summary>
        /// The full weighted term set the scorer should feed into BM25:
        /// (token, weight) pairs for every original query token plus every
        /// one-level synonym expansion. A token that appears in both the
        /// originals and a synonym expansion is emitted exactly once with
        /// weight 1.0 (originals win). Step 48e — single source of truth so
        /// the scorer doesn't have to re-tokenize the query and can't
        /// drift from <see cref="RagService.RetrieveAsync"/>.
        /// </summary>
        IEnumerable<(string Token, double Weight)> WeightedTerms();
    }

    /// <summary>
    /// Scores each chunk against the user's query using BM25 (Robertson +
    /// Walker / Zaragoza et al.) with corpus-level IDF and BM25 length
    /// normalisation, then divides every chunk's raw score by the corpus
    /// max so the top hit lands at 1.0. Scoring is purely
    /// <em>query-dependent</em>: chunks that share zero query terms (per
    /// <paramref name="tokenWeight"/>) get 0. No per-chunk base score is
    /// added.
    /// </summary>
    /// <param name="query">User query — tokenised the same way chunks are.</param>
    /// <param name="chunks">Candidate chunks to rank.</param>
    /// <param name="tokenWeight">Per-token scoring weights. E.g.
    /// <c>1.0</c> for originals and <see cref="RagSynonyms.SynonymWeight"/>
    /// for one-level synonyms.</param>
    /// <param name="k1">BM25 saturation. Classic value 1.5.</param>
    /// <param name="b">BM25 length normalisation. Classic value 0.75.</param>
    public IReadOnlyList<ChunkKeywordScore> ScoreChunksForQuery(
        string query,
        IReadOnlyList<(string Id, string Text)> chunks,
        ITokenWeight tokenWeight,
        double k1 = 1.5,
        double b  = 0.75)
    {
        if (chunks is null || chunks.Count == 0) return Array.Empty<ChunkKeywordScore>();
        if (tokenWeight is null) throw new ArgumentNullException(nameof(tokenWeight));

        // ---- 1. Tokenise query + chunks uniformly. -------------------
        // We collect (i) the weighted term set from ITokenWeight and (ii)
        // per-chunk TF maps + lengths. The weighted term set is the single
        // source of truth for what counts as a query term — it already
        // contains the original query tokens (weight 1.0) and their one-
        // level synonym expansions (weight RagSynonyms.SynonymWeight),
        // tokenised and Bangla-normalised identically to chunks.
        // Synonym chains never extend past one level (RagService controls
        // the expansion depth). A token present in both the originals and
        // a synonym expansion is collapsed back to 1.0 by the caller.
        // (query is kept in the signature for callers that still want to
        // log it, but it is no longer tokenised here.)
        _ = query; // see comment above
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tok, w) in tokenWeight.WeightedTerms())
        {
            if (string.IsNullOrEmpty(tok)) continue;
            if (w <= 0) continue;
            // Originals win — a synonym expansion that happens to also be
            // a literal query token keeps its 1.0 weight.
            weights[tok] = weights.TryGetValue(tok, out var existing)
                ? Math.Max(existing, w)
                : w;
        }

        var chunkTfList = new Dictionary<string, int>[chunks.Count];
        var chunkLens   = new int[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in TokenizeForTest(chunks[i].Text ?? string.Empty))
            {
                if (string.IsNullOrEmpty(t)) continue;
                tf[t] = tf.TryGetValue(t, out var c) ? c + 1 : 1;
            }
            chunkTfList[i] = tf;
            chunkLens[i]   = tf.Values.Sum();
        }

        // ---- 2. Compute corpus stats once over the chunk set. --------
        int N      = chunks.Count;
        double sum = 0;
        for (int i = 0; i < chunkLens.Length; i++) sum += chunkLens[i];
        double avgDl = N == 0 ? 0 : sum / N;
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in weights.Keys)
        {
            int hits = 0;
            for (int i = 0; i < chunkTfList.Length; i++)
                if (chunkTfList[i].TryGetValue(t, out var c) && c > 0) hits++;
            if (hits > 0) df[t] = hits;
        }

        // ---- 3. Score each chunk. ------------------------------------
        var raws = new double[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            double s = 0;
            var tf = chunkTfList[i];
            int dl = chunkLens[i];
            foreach (var (term, w) in weights)
            {
                if (!df.TryGetValue(term, out var dft) || dft == 0) continue;
                if (!tf.TryGetValue(term, out var f) || f == 0) continue;
                // BM25+ IDF with Laplace smoothing (the +1 inside log is
                // the canonical Robertson–Sparck Jones modification).
                double idf = Math.Log(((N - dft + 0.5) / (dft + 0.5)) + 1.0);
                double denom = f + k1 * (1.0 - b + b * dl / (avgDl <= 0 ? 1.0 : avgDl));
                double tfNorm = f * (k1 + 1.0) / denom;
                s += w * idf * tfNorm;
            }
            raws[i] = s;
        }

        // ---- 4. Per-query normalisation. -----------------------------
        double maxRaw = 0;
        for (int i = 0; i < raws.Length; i++) if (raws[i] > maxRaw) maxRaw = raws[i];
        var norm = new ChunkKeywordScore[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            double n = maxRaw > 0 ? raws[i] / maxRaw : 0;
            norm[i] = new ChunkKeywordScore(chunks[i].Id, raws[i], n);
        }
        return norm;
    }

    private static IEnumerable<string> Tokenise(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var lower = text.ToLower(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (Array.IndexOf(Splitters, ch) >= 0)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(ch);
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
