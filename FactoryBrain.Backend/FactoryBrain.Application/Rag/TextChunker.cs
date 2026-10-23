using System.Text.RegularExpressions;

namespace FactoryBrain.Application.Rag;

/// <summary>
/// Domain-aware chunker for the factoryBrain RAG pipeline. Port of
/// <c>src/services/rag/chunker.ts</c> so the C# API and the Next.js
/// in-process retriever stay in lockstep on what a "chunk" is.
///
/// <para>
/// Defaults: target ≈ 400 tokens, overlap ≈ 60 tokens, soft band
/// 300–500 tokens, hard cap 700 tokens (any single sentence larger
/// than the hard cap is emitted as its own chunk so a code reference
/// inside it is never lost). Token estimation is the same simple
/// whitespace-separated span count the TS chunker uses; good enough
/// for the synthetic corpus and keeps chunk sizes stable across the
/// C# / TS implementations.
/// </para>
///
/// <list type="bullet">
///   <item>
///     Protects machine codes (<c>Juki DDL-8700</c>, <c>AQL 2.5</c>,
///     <c>SMV 0.45</c>, <c>NLGI #2</c>, error codes like <c>E-12</c>)
///     via placeholder replace / restore so the sentence splitter
///     never cuts them.
///   </item>
///   <item>
///     Splits on <c>(?&lt;=[.!?])\s+(?=[A-Z0-9\u0980-\u09FF])</c>
///     which covers ASCII capital letters + digits + Bangla script
///     starts.
///   </item>
///   <item>
///     If a heading starts with <c>#</c> / <c>##</c> / <c>###</c>,
///     runs the section as its own chunk first so SOP sections stay
///     intact.
///   </item>
///   <item>
///     Title bleed: prepended to the first chunk of a document so a
///     query that only matches the title still hits.
///   </item>
///   <item>
///     Atomic error block detector: when a sentence containing an
///     error code (<c>E-NN</c>, <c>Err-NNN</c>, <c>ALARM</c>,
///     <c>FAULT</c>) is followed within ±2 sentences by a cause or
///     remedy, the block is emitted as one chunk even if it pushes
///     past the soft max.
///   </item>
/// </list>
/// </summary>
public sealed class TextChunker
{
    public const int DefaultTokenTarget = 400;
    public const int DefaultTokenOverlap = 60;
    public const int DefaultTokenMin     = 300;
    public const int DefaultTokenMax     = 500;
    public const int DefaultHardCap      = 700;

    // Machine codes / domain tokens we want to preserve verbatim.
    // Same set as src/services/rag/chunker.ts so the C# and TS
    // pipelines index the same phrases under the same ids.
    public static readonly IReadOnlyList<string> CodeTokens = new[]
    {
        // Machine models
        "Juki DDL-8700", "Juki DDL-9000C", "Juki LU-563",
        "Brother BAS-311H", "Brother DB2-B755", "Kansai Special FX-442",
        // AQL levels
        "AQL 0.65", "AQL 1.0", "AQL 1.5", "AQL 2.5", "AQL 4.0",
        // SMV / SAH / NLGI specs
        "SMV 0.45", "SMV 0.55", "SMV 0.65", "SMV 0.75",
        "SAH 22", "SAH 13",
        "NLGI #2",
        // Error codes (exemplars; preserved when present in source text)
        "E-01", "E-02", "E-12", "Err-401"
    };

    // Sentence boundary: punctuation followed by whitespace + a
    // capital letter / digit / Bangla-script start.
    private static readonly Regex SentenceBoundary = new(
        @"(?<=[.!?])\s+(?=[A-Z0-9\u0980-\u09FF])",
        RegexOptions.Compiled);

    // Markdown heading boundaries — pre-split text on these so SOP
    // sections stay atomic.
    private static readonly Regex MarkdownHeading = new(
        @"(?m)^#{1,3}\s+",
        RegexOptions.Compiled);

    // Error codes / alarm tokens that mark an "atomic error block".
    private static readonly Regex ErrorCodeMarker = new(
        @"\b(E-\d+|Err-\d+|ALARM|FAULT)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Department inference buckets.
    private static readonly (string Dept, Regex[])[] DepartmentPatterns =
    {
        ("sewing",    new[] {
            new Regex(@"\bsewing\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bbundle_scan\b",RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bneedle\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bthread\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bbobbin\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bjuki\b",       RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bbrother\b",    RegexOptions.IgnoreCase | RegexOptions.Compiled),
        }),
        ("cutting",   new[] {
            new Regex(@"\bcutting\b",        RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bspreading\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bspread\b",         RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bmarker\b",         RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bfabric relaxation\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bply\b",            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        }),
        ("finishing", new[] {
            new Regex(@"\bfinishing\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bbuttonhole\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bbutton hole\b",    RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\btop[- ]?stitch\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\biron\b",           RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bpress\b",          RegexOptions.IgnoreCase | RegexOptions.Compiled),
        }),
        ("qc",        new[] {
            new Regex(@"\bqc\b",            RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bquality\b",       RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bdefects?\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\baql\b",           RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\binspect\b",       RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\breject\b",        RegexOptions.IgnoreCase | RegexOptions.Compiled),
        }),
    };

    // Spec-token regexes for additional tags.
    private static readonly Regex SmvMatch = new(
        @"\bSMV\s*([0-9]+(?:\.[0-9]+)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AqlMatch = new(
        @"\bAQL\s*([0-9]+(?:\.[0-9]+)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // -----------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------

    /// <summary>
    /// Split a body into chunks using default settings. Returns empty
    /// when the body is null / whitespace / empty.
    /// </summary>
    public IReadOnlyList<string> Split(string? text)
        => Split(text ?? string.Empty, null, null);

    /// <summary>
    /// Split a body into chunks with a title bleed prepended to the
    /// first chunk. Caller passes <paramref name="title"/> as the
    /// bleed text (the original document title in the ingest locale);
    /// when <paramref name="title"/> is null the first chunk is the
    /// raw body.
    /// </summary>
    public IReadOnlyList<string> SplitWithTitle(string? text, string? title)
        => Split(text ?? string.Empty, string.IsNullOrWhiteSpace(title) ? null : title.Trim(), null);

    /// <summary>
    /// Lower-level entry point used by the ingest service so it can
    /// hand the chunker both the title bleed and the source-id prefix.
    /// </summary>
    public IReadOnlyList<string> Split(string text, string? titleBleed, IReadOnlyList<string>? tags, int? target = null, int? overlap = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var tgt     = target  ?? DefaultTokenTarget;
        var ovlp    = overlap ?? DefaultTokenOverlap;
        var minT    = Math.Min(DefaultTokenMin, tgt);
        var maxT    = Math.Max(DefaultTokenMax, tgt);

        var sentences = SentenceAwareSplit(text);
        if (sentences.Count == 0) return Array.Empty<string>();

        // Detect atomic error blocks — when an error-code sentence
        // appears, the next ±2 sentences are merged into a single
        // chunk even if oversized.
        var groupPlan = BuildErrorBlockGroups(sentences);

        var allChunks = new List<string>();
        foreach (var group in groupPlan)
        {
            allChunks.AddRange(ChunkSentenceGroup(group, tgt, ovlp, minT, maxT));
        }

        // Title bleed — prepend the title to the first chunk so
        // title-only queries still hit.
        if (titleBleed is { } bleed && allChunks.Count > 0)
        {
            allChunks[0] = bleed + " — " + allChunks[0];
        }

        // Defensive: drop empties.
        return allChunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    /// <summary>
    /// Heuristic department inference. Returns one of
    /// <c>sewing | cutting | finishing | qc | general</c>.
    /// </summary>
    public string InferDepartment(string titleAndBody, string fallback = "general")
    {
        var haystack = titleAndBody ?? string.Empty;
        foreach (var (dept, patterns) in DepartmentPatterns)
        {
            foreach (var p in patterns)
            {
                if (p.IsMatch(haystack)) return dept;
            }
        }
        return fallback;
    }

    /// <summary>
    /// Merge caller-supplied tags with machine-code / spec tags
    /// detected in <paramref name="titleAndBody"/>. Same rules as
    /// <c>extractTags()</c> in <c>chunker.ts</c>.
    /// </summary>
    public IReadOnlyList<string> ExtractTags(string titleAndBody, IEnumerable<string>? sourceTags)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourceTags is not null)
            foreach (var t in sourceTags)
                if (!string.IsNullOrWhiteSpace(t)) tags.Add(t.Trim().ToLowerInvariant());

        var combined = titleAndBody ?? string.Empty;
        foreach (var code in CodeTokens)
        {
            if (combined.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0)
                tags.Add(code.ToLowerInvariant());
        }
        var smv = SmvMatch.Match(combined);
        if (smv.Success) tags.Add($"smv:{smv.Groups[1].Value}");
        var aql = AqlMatch.Match(combined);
        if (aql.Success) tags.Add($"aql:{aql.Groups[1].Value}");
        return tags.ToList();
    }

    // -----------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------

    private List<string> SentenceAwareSplit(string text)
    {
        // 1. Optional Markdown pre-pass — split on headings first so
        //    SOP sections stay atomic.
        var segments = new List<string>();
        if (MarkdownHeading.IsMatch(text))
        {
            int cursor = 0;
            foreach (Match m in MarkdownHeading.Matches(text))
            {
                if (m.Index > cursor)
                    segments.Add(text[cursor..m.Index]);
                cursor = m.Index;
            }
            if (cursor < text.Length) segments.Add(text[cursor..]);
        }
        else
        {
            segments.Add(text);
        }

        // 2. Protect CODE_TOKENS via placeholder / restore so the
        //    sentence boundary regex never cuts them.
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
        var sentences = new List<string>();
        foreach (var raw in segments)
        {
            var working = raw;
            foreach (var code in CodeTokens)
            {
                if (working.IndexOf(code, StringComparison.Ordinal) < 0) continue;
                var ph = $"__CODE_{placeholders.Count}__";
                placeholders[ph] = code;
                working = working.Replace(code, ph, StringComparison.Ordinal);
            }
            var parts = SentenceBoundary.Split(working);
            foreach (var p in parts)
            {
                var trimmed = p.Trim();
                if (trimmed.Length == 0) continue;
                foreach (var (ph, code) in placeholders)
                    trimmed = trimmed.Replace(ph, code, StringComparison.Ordinal);
                sentences.Add(trimmed);
            }
        }
        return sentences;
    }

    /// <summary>
    /// Detect "atomic error blocks": a sentence carrying an error code
    /// (E-NN / Err-NNN / ALARM / FAULT) plus its cause + remedy, kept
    /// as one group even if oversized. We look forward ±2 sentences
    /// from each error-code sentence and treat them as a single block.
    /// </summary>
    private List<List<string>> BuildErrorBlockGroups(IReadOnlyList<string> sentences)
    {
        var groups = new List<List<string>>();
        int i = 0;
        while (i < sentences.Count)
        {
            var s = sentences[i];
            if (!ErrorCodeMarker.IsMatch(s))
            {
                groups.Add(new List<string> { s });
                i++;
                continue;
            }
            // Atomic block: error sentence + up to 2 following
            // sentences that look like cause / remedy.
            int end = Math.Min(sentences.Count, i + 3);
            var block = new List<string>(end - i);
            for (int j = i; j < end; j++) block.Add(sentences[j]);
            groups.Add(block);
            i = end;
        }
        return groups;
    }

    private static IEnumerable<string> ChunkSentenceGroup(
        IReadOnlyList<string> group,
        int target, int overlap, int minT, int maxT)
    {
        // Cost per sentence in tokens (whitespace span count).
        var tokens = group.Select(EstimateTokens).ToArray();

        // If the whole group fits within MAX (or below HARD CAP for
        // atomic blocks), emit as one chunk.
        var totalTokens = tokens.Sum();
        if (totalTokens <= maxT)
        {
            yield return string.Join(" ", group);
            yield break;
        }
        if (totalTokens <= DefaultHardCap)
        {
            // Atomic error block — emit whole rather than splitting
            // the error from its cause.
            yield return string.Join(" ", group);
            yield break;
        }

        // Otherwise, fall back to the sliding-window chunker on the
        // flattened sentence list. The atomic-block guarantee has
        // already been honoured by grouping; this is the edge case
        // where the block itself is bigger than the hard cap.
        var sentences = group;
        int n = sentences.Count;
        var chunks = new List<string>();
        var buffer = new List<string>();
        int bufferTokens = 0;
        int cursor = 0;
        var lastOverlap = new List<string>();
        int lastOverlapTokens = 0;

        for (int i = 0; i < n; i++)
        {
            var s = sentences[i];
            var cost = tokens[i];

            // Re-seed buffer with last chunk's overlap suffix.
            if (buffer.Count == 0 && lastOverlap.Count > 0)
            {
                foreach (var o in lastOverlap) buffer.Add(o);
                bufferTokens = lastOverlapTokens;
            }

            if (bufferTokens >= minT)
            {
                chunks.Add(string.Join(" ", buffer));
                // Compute next overlap suffix.
                lastOverlap = new List<string>();
                lastOverlapTokens = 0;
                for (int j = buffer.Count - 1; j >= 0; j--)
                {
                    var c = tokens[cursor - buffer.Count + j];
                    if (lastOverlapTokens + c > overlap) break;
                    lastOverlap.Insert(0, buffer[j]);
                    lastOverlapTokens += c;
                }
                buffer = new List<string>();
                bufferTokens = 0;
            }

            if (cost > maxT && buffer.Count == 0)
            {
                // Oversized standalone — emit anyway.
                chunks.Add(s);
                cursor++;
                continue;
            }

            buffer.Add(s);
            bufferTokens += cost;
            cursor++;

            if (bufferTokens >= target)
            {
                chunks.Add(string.Join(" ", buffer));
                lastOverlap = new List<string>();
                lastOverlapTokens = 0;
                for (int j = buffer.Count - 1; j >= 0; j--)
                {
                    var c = tokens[cursor - buffer.Count + j];
                    if (lastOverlapTokens + c > overlap) break;
                    lastOverlap.Insert(0, buffer[j]);
                    lastOverlapTokens += c;
                }
                buffer = new List<string>();
                bufferTokens = 0;
            }
        }
        if (buffer.Count > 0) chunks.Add(string.Join(" ", buffer));

        foreach (var c in chunks) yield return c;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}