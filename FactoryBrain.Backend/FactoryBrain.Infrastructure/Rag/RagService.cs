using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Application.Dtos.Ask;
using FactoryBrain.Application.Dtos.Rag;
using FactoryBrain.Infrastructure.Configuration;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace FactoryBrain.Infrastructure.Rag;

public sealed class RagService : IRagService
{
    private readonly FactoryBrainDbContext _db;
    private readonly TextChunker _chunker;
    private readonly IEmbeddingService _embed;
    private readonly HybridScorer _scorer;
    private readonly EmbeddingColumnAdmin _admin;
    private readonly EmbeddingProviderResolver _resolver;
    private readonly ILogger<RagService> _log;
    private readonly IOptions<RagConfig> _ragCfg;

    public RagService(
        FactoryBrainDbContext db,
        TextChunker chunker,
        IEmbeddingService embed,
        HybridScorer scorer,
        EmbeddingColumnAdmin admin,
        EmbeddingProviderResolver resolver,
        ILogger<RagService> log,
        IOptions<RagConfig> ragCfg)
    {
        _db = db; _chunker = chunker; _embed = embed; _scorer = scorer;
        _admin = admin; _resolver = resolver; _log = log; _ragCfg = ragCfg;
    }

    public async Task<List<DocumentChunk>> ChunkAsync(
        string docId, string title, string body, IEnumerable<string> tags,
        string department, string category, CancellationToken ct = default)
    {
        await Task.Yield(); // yield to scheduler; the body is CPU-bound embedding work
        var tagList = tags.ToList();
        var pieces = _chunker.SplitWithTitle(body, title);
        var chunks = new List<DocumentChunk>();
        for (int i = 0; i < pieces.Count; i++)
        {
            chunks.Add(new DocumentChunk
            {
                Id = $"{docId}::chunk-{i + 1}",
                DocumentId = docId,
                Title = title,
                Department = department,
                Category = category,
                Tags = tagList,
                Text = pieces[i],
                Ordinal = i,
                Embedding = _embed.Embed($"{title} {pieces[i]}")
            });
        }
        return chunks;
    }

    public async Task<IReadOnlyList<AskRagHit>> RetrieveAsync(
        string query, RagFilter? filter, int topK = 4, double similarityThreshold = 0,
        double bm25Weight = 0.35, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<AskRagHit>();

        var cfg = _ragCfg.Value;

        // Step 47: read provider-mix weights from RagConfig, with an env-var
        // override that an operator can flip in Development without a
        // restart (EnvReloader re-reads RAG_HYBRID_* on every probe tick).
        // The env vars take precedence over the appsettings defaults.
        bool degraded = _resolver.IsDegraded;
        double providerBm25W = degraded ? cfg.LocalBm25Weight : cfg.HostedBm25Weight;
        double providerVecW  = degraded ? cfg.LocalVectorWeight : cfg.HostedVectorWeight;
        var envBm25 = TryReadDouble("RAG_HYBRID_BM25_WEIGHT");
        var envVec  = TryReadDouble("RAG_HYBRID_VECTOR_WEIGHT");
        if (envBm25 is not null) providerBm25W = envBm25.Value;
        if (envVec  is not null) providerVecW  = envVec.Value;

        var q = _db.DocumentChunks.AsNoTracking().AsQueryable();
        if (filter is { } f)
        {
            if (f.Department.HasValue) q = q.Where(c => c.Department == MapDept(f.Department.Value));
            if (f.Category.HasValue)   q = q.Where(c => c.Category  == MapCat(f.Category.Value));
        }
        var chunks = await q.ToListAsync(ct);
        if (chunks.Count == 0) return Array.Empty<AskRagHit>();

        // Decide whether to run the vector score at all. We skip it whenever
        // (a) the active embedder is in degraded mode (hosted provider
        // unavailable, Active has fallen back to local), or (b) the query
        // vector's dims don't match the live column dims (would produce a
        // pgvector comparison error). In both cases we rank by BM25 only —
        // the AskController still gets citations, just without a vector
        // score component.
        bool skipVectorScore = _resolver.IsDegraded;
        int? qVecLen = null;
        float[]? qVec = null;
        int? liveDim = null;
        if (!skipVectorScore)
        {
            qVec = ((float[])_embed.Embed(query).ToArray());
            qVecLen = qVec.Length;
            liveDim = _admin.TryProbeLiveColumnDim();
            if (liveDim is null || liveDim.Value != qVecLen.Value)
            {
                _log.LogWarning(
                    "Vector score skipped at query time: active embedder dims={ActiveDims} but live column dim={LiveDim}. Ranking by BM25 only.",
                    qVecLen, liveDim);
                skipVectorScore = true;
            }
        }

        // Step 48d: query-dependent keyword score.
        //
        // Build the per-token weight lookup the BM25 scorer will multiply
        // into each term's contribution:
        //   * tokens from the user's *original* query → weight 1.0
        //   * tokens reachable through ONE level of RagSynonyms.ExpandToken
        //     → weight RagSynonyms.SynonymWeight (0.7)
        //   * tokens that already appear in the original set are
        //     collapsed back to 1.0 (no double-counting)
        //
        // We intentionally do NOT chain synonym-of-synonym: the spec
        // explicitly limits us to a single expansion. Multi-word synonyms
        // (e.g. "standard minute value") are tokenised through the same
        // HybridScorer pipeline used for chunks so the resulting tokens
        // are FormKC + lowercased + Bangla-punctuation-aware. The output
        // of RagSynonyms.ApplyToQuery is still logged so operators can see
        // what the legacy string-rewrite path produced — the new scorer
        // is independent of it and uses ExpandToken directly.
        var originalTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in HybridScorer.TokenizeForTest(query))
            if (!string.IsNullOrEmpty(tok))
                originalTokens.Add(tok);

        var synTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in originalTokens)
        {
            foreach (var s in RagSynonyms.ExpandToken(t))
            {
                if (string.IsNullOrEmpty(s)) continue;
                // Multi-word synonym expansion — tokenise identically to
                // chunk text so each token lands in synTokens separately.
                foreach (var sub in HybridScorer.TokenizeForTest(s))
                {
                    if (string.IsNullOrEmpty(sub)) continue;
                    if (originalTokens.Contains(sub)) continue; // originals win at 1.0
                    synTokens.Add(sub);
                }
            }
        }

        var expandedQueryForLog = originalTokens.Count > 0 || synTokens.Count > 0
            ? string.Join(' ', originalTokens.Concat(synTokens))
            : "";
        if (!string.IsNullOrEmpty(expandedQueryForLog) &&
            !string.Equals(expandedQueryForLog, query, StringComparison.Ordinal))
        {
            _log.LogDebug("RagSynonyms scoring expansion: {Original} -> {Expanded}", query, expandedQueryForLog);
        }

        var tokenWeight = new RagTokenWeight(originalTokens, synTokens);

        // Run the corpus-aware BM25 in one pass over all chunks. The
        // scorer is query-dependent — every chunk that shares zero
        // weighted terms with the query gets Norm = 0.
        var candidateTuples = chunks
            .Select(c => (c.Id, c.Text))
            .ToList();
        var kwScores = _scorer.ScoreChunksForQuery(query, candidateTuples, tokenWeight)
            .ToDictionary(s => s.ChunkId, s => s.Norm, StringComparer.Ordinal);

        // Pre-lowercase each chunk once for the synonym-aware overlap
        // check below. TermFreq isn't used any more (the BM25 above is
        // the source of truth for the keyword side), so we no longer
        // need its tf map.
        var chunkTextLower = chunks.ToDictionary(c => c.Id, c => c.Text.ToLowerInvariant());

        var ranked = chunks.Select(c =>
        {
            // Null embedding = chunk was ingested in degraded mode or its
            // embed call failed mid-batch. The dense score is skipped for
            // these rows; the BM25 side still ranks them so the user-
            // visible hit list stays useful until reindex fills them in.
            float[]? cVec = c.Embedding is null
                ? null
                : (float[])c.Embedding.ToArray();
            double cos = 0;
            if (!skipVectorScore && qVec is not null && cVec is not null && cVec.Length == qVec.Length)
            {
                cos = _scorer.Cosine(qVec, cVec);
            }
            double bm25Norm = kwScores.TryGetValue(c.Id, out var n) ? n : 0;

            // Synonym-aware overlap gate: a chunk passes if the chunk's
            // text contains at least one token from the original query OR
            // one synonym of an original query token. The check is purely
            // query-dependent (no chunk-side base score). Synonym chains
            // never extend past one level — we only check `originalTokens`
            // and the single-hop `synTokens` set.
            var cText = chunkTextLower[c.Id];
            bool matchOriginal = false;
            foreach (var k in originalTokens)
                if (cText.Contains(k, StringComparison.Ordinal)) { matchOriginal = true; break; }
            bool matchSyn = false;
            if (!matchOriginal)
            {
                foreach (var k in synTokens)
                    if (cText.Contains(k, StringComparison.Ordinal)) { matchSyn = true; break; }
            }

            // Use the *provider-mix* BM25 weight for the hybrid combine.
            // Caller-provided bm25Weight is retained as a legacy fallback
            // for callers that don't yet route through RagConfig.
            double weight = Math.Clamp(providerBm25W, 0, 1);
            if (bm25Weight > 0 && bm25Weight != 0.35) // legacy caller explicitly set a non-default weight
                weight = Math.Clamp(bm25Weight, 0, 1);
            double hybrid = _scorer.Hybrid(bm25Norm, cos, weight);
            return new { c, cos, bm25 = bm25Norm, hybrid, matchOriginal, matchSyn };
        })
        // Step 47 RAG_MIN_SCORE cutoff (kept by 48d).
        //
        // In degraded mode the dense score is 0 for every chunk, so the
        // hybrid score collapses to bm25Weight * bm25. Evaluating BM25
        // alone (bm25Norm, already 0-1 normalised) keeps keyword hits in
        // the answer. The norm-1 top hit at MinScore=0.10 needs
        // bm25Weight=0.10 minimum in degraded mode — the RagConfig
        // already accounts for this (LocalBm25Weight in degraded mode is
        // high enough that bm25Norm clears MinScore for legit hits).
        .Where(r => degraded
            ? r.bm25 >= cfg.MinScore
            : r.hybrid >= cfg.MinScore)
        // Synonym-aware overlap gate (Step 48d): a chunk must share at
        // least one token with the user's original query OR with one of
        // its one-level synonyms. A query whose tokens match nothing —
        // directly or through a synonym — returns no hits and AskService
        // converts that into the fixed "no confident source" payload,
        // which is the existing citeCount = 0 contract for nonsense
        // queries ("zzqx purple volcano tax" / "ফ্লারবার্গ ব্লুমেনভাল্ট").
        .Where(r => r.matchOriginal || r.matchSyn || originalTokens.Count == 0)
        // Honour legacy similarity-threshold parameter alongside MinScore.
        .Where(r => r.hybrid >= similarityThreshold)
        .OrderByDescending(r => r.hybrid)
        .Take(topK > 0 ? topK : cfg.TopK)
        .ToList();

        return ranked.Select(r => new AskRagHit(
            r.c.Id,
            r.c.DocumentId,
            r.c.Title,
            r.c.Department,
            r.c.Category,
            r.c.Tags,
            Math.Round(r.hybrid, 4),
            Math.Round(r.cos, 4),
            Math.Round(r.bm25, 4),
            r.c.Text.Length <= 240 ? r.c.Text : r.c.Text[..240]
        )).ToList();
    }

    private static double? TryReadDouble(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return double.TryParse(raw,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var d) ? d : null;
    }

    /// <summary>
    /// <see cref="HybridScorer.ITokenWeight"/> implementation that knows
    /// about the two-tier synonym weights used in <see cref="RetrieveAsync"/>:
    /// original query tokens score at 1.0; one-level synonym expansions of
    /// those tokens score at <see cref="RagSynonyms.SynonymWeight"/>.
    /// Tokens not in either set score at 0 — the scorer only feeds the
    /// weighted term set, so anything outside the two buckets is invisible
    /// to BM25 (no accidental string-match against an unrelated chunk).
    /// Step 48d. <see cref="WeightedTerms"/> added in 48e so the scorer
    /// can take the term set verbatim without re-tokenising the query.
    /// </summary>
    private sealed class RagTokenWeight : HybridScorer.ITokenWeight
    {
        private readonly HashSet<string> _originals;
        private readonly HashSet<string> _synonyms;

        public RagTokenWeight(HashSet<string> originals, HashSet<string> synonyms)
        {
            _originals = originals;
            _synonyms  = synonyms;
        }

        public double WeightOf(string token)
        {
            if (string.IsNullOrEmpty(token)) return 0;
            if (_originals.Contains(token)) return 1.0;
            if (_synonyms.Contains(token))  return RagSynonyms.SynonymWeight;
            return 0;
        }

        public IEnumerable<(string Token, double Weight)> WeightedTerms()
        {
            // Originals first — a synonym that overlaps with an original
            // is dropped by the scorer's Max() merge (or, equivalently,
            // we skip it here). Emitting originals last would risk the
            // synonym weight leaking through if the scorer ever forgot
            // to collapse duplicates.
            foreach (var t in _originals)
                if (!string.IsNullOrEmpty(t))
                    yield return (t, 1.0);
            foreach (var t in _synonyms)
                if (!string.IsNullOrEmpty(t) && !_originals.Contains(t))
                    yield return (t, RagSynonyms.SynonymWeight);
        }
    }

    /// <summary>
    /// Re-embed every chunk across every <see cref="ManualDocument"/> whose
    /// stored provider / model / dims differ from the active embedder.
    /// Delegates to <see cref="EmbeddingColumnAdmin.ReindexChangedDocsAsync"/>
    /// so the controller, the startup pipeline and this method all share
    /// one reindex implementation.
    /// </summary>
    public async Task<ReindexReport> ReindexAsync(CancellationToken ct = default)
    {
        var outcome = await _admin.ReindexChangedDocsAsync(ct).ConfigureAwait(false);
        return new ReindexReport(
            outcome.Documents,
            outcome.Chunks,
            outcome.Configured.Provider,
            outcome.Configured.Model,
            outcome.Configured.Dims);
    }

    /// <summary>
    /// Decide whether a startup reindex is required — true when the
    /// currently-configured embedder differs from the metadata stored on
    /// any <see cref="ManualDocument"/> row. Returns false in degraded
    /// mode: the actual reindex path is a no-op there, so this method
    /// mirrors that decision.
    /// </summary>
    public async Task<bool> NeedsReindexAsync(CancellationToken ct = default)
    {
        var r = _resolver.Resolve();
        if (r.Degraded) return false;
        var first = await _db.ManualDocuments.AsNoTracking()
            .OrderBy(d => d.Id)
            .Select(d => new { d.EmbeddingProvider, d.EmbeddingModel, d.Dims })
            .FirstOrDefaultAsync(ct);
        if (first is null) return false;
        return first.EmbeddingProvider != r.Configured.Provider
            || first.EmbeddingModel    != r.Configured.Model
            || first.Dims              != r.Configured.Dims;
    }

    /// <summary>
    /// Ingest one document into the RAG index. The body is chunked with
    /// <see cref="TextChunker"/>; each chunk is embedded with the active
    /// configured embedder and stamped with the row's
    /// <c>EmbeddingProvider / Model / Dims</c>.
    ///
    /// <para>
    /// In degraded mode (no hosted key, probe failing, etc.) the chunks
    /// are still stored, but with <c>Embedding = null</c> and
    /// <c>EmbeddingProvider = "pending"</c>. This keeps the corpus
    /// discoverable by BM25 even when no embedder is reachable; the next
    /// non-degraded startup reindex (or POST /api/rag/reindex) walks
    /// these rows via <see cref="EmbeddingColumnAdmin.LoadDocsNeedingReindexAsync"/>
    /// and embeds them. The method returns
    /// <c>embedded = false</c> + a <c>warning</c> string in that case so
    /// the caller knows vectors are pending.
    /// </para>
    /// </summary>
    public async Task<IngestOutcome> IngestAsync(IngestInput input, CancellationToken ct = default)
    {
        var r = _resolver.Resolve();
        var cfg = r.Configured;

        // Chunk with the title bleed so a query that matches the title
        // still surfaces the first chunk. We also let the chunker
        // extend the source-tag list with detected machine-code tags.
        var tagList = _chunker.ExtractTags(input.Title + "\n" + input.Content, input.Tags).ToList();
        var pieces = _chunker.SplitWithTitle(input.Content, input.Title);

        // Department + category inference. The caller (controller /
        // DbInitializer) can override, but in single-content ingest we
        // infer from the body so titles like "QC defect procedure —
        // sewing" still surface in the right bucket.
        var dept = _chunker.InferDepartment(input.Title + " " + input.Content, "general");
        var category = SourceToCategory(input.Source);

        var docId = $"doc-{Guid.NewGuid():N}";
        bool embedded = !r.Degraded;
        string? warning = null;

        var doc = new ManualDocument
        {
            Id = docId,
            TitleEn  = input.Title,
            TitleBn  = input.TitleBn ?? string.Empty,
            Tags     = tagList,
            BodyEn   = input.Content,
            BodyBn   = string.Empty,
            Source   = input.Source,
            Department = dept,
            Category  = category,
            Url       = input.Url,
            IsDemo    = input.IsDemo,
            CreatedAt = DateTime.UtcNow,
            EmbeddingProvider = embedded ? cfg.Provider : "pending",
            EmbeddingModel    = embedded ? cfg.Model    : "pending",
            Dims              = embedded ? cfg.Dims      : 0
        };

        var chunks = new List<DocumentChunk>(pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
        {
            Pgvector.Vector? vec = null;
            if (embedded)
            {
                try
                {
                    vec = _embed.Embed($"{input.Title} {pieces[i]}");
                    // Dimension guard: a freshly-computed vector whose
                    // length disagrees with the live column dim is
                    // dropped (chunk stored with Embedding=null, row
                    // stamped "pending"); a future reindex will fill it in.
                    vec = _admin.GuardVectorDims(vec, doc);
                    if (vec is null)
                    {
                        warning ??= "embedding dimension mismatch; chunks stored as pending until next reindex";
                    }
                }
                catch (Exception ex)
                {
                    _resolver.MarkDegraded(
                        $"ingest pre-embed failed on doc={docId} chunk={i + 1} ({cfg.Provider})", ex);
                    embedded = false;
                    warning = "embedding provider became unavailable mid-ingest; chunks stored as pending";
                    doc.EmbeddingProvider = "pending";
                    doc.EmbeddingModel    = "pending";
                    doc.Dims              = 0;
                    vec = null;
                }
            }
            chunks.Add(new DocumentChunk
            {
                Id = $"{docId}::chunk-{i + 1}",
                DocumentId = docId,
                Title = input.Title,
                Department = dept,
                Category = category,
                Tags = tagList,
                Text = pieces[i],
                Ordinal = i,
                Embedding = vec,
                CreatedAt = DateTime.UtcNow
            });
        }

        // When embedding was lost mid-batch we fall back to storing the
        // remaining chunks as null/pending too — same shape the reindex
        // pipeline emits.
        if (!embedded)
        {
            warning ??= "embedding provider unavailable; chunks stored as pending until next reindex";
            doc.EmbeddingProvider = "pending";
            doc.EmbeddingModel    = "pending";
            doc.Dims              = 0;
        }

        doc.Chunks = chunks;
        _db.ManualDocuments.Add(doc);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new IngestOutcome(
            docId,
            chunks.Count,
            doc.EmbeddingProvider,
            doc.EmbeddingModel,
            doc.Dims,
            embedded,
            warning);
    }

    public async Task<IReadOnlyList<DocumentListItem>> ListDocumentsAsync(string? source, CancellationToken ct = default)
    {
        var q = _db.ManualDocuments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(source))
            q = q.Where(d => d.Source == source);
        var rows = await q
            .OrderBy(d => d.CreatedAt)
            .Select(d => new
            {
                d.Id,
                d.TitleEn,
                d.TitleBn,
                d.Source,
                d.Tags,
                d.EmbeddingProvider,
                d.EmbeddingModel,
                d.Dims,
                d.IsDemo,
                d.CreatedAt,
                ChunkCount = d.Chunks.Count
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new DocumentListItem(
            r.Id,
            string.IsNullOrEmpty(r.TitleEn) ? r.TitleBn : r.TitleEn,
            string.IsNullOrEmpty(r.TitleBn) ? null : r.TitleBn,
            r.Source,
            r.Tags ?? new List<string>(),
            r.ChunkCount,
            r.EmbeddingProvider,
            r.EmbeddingModel,
            r.Dims,
            r.IsDemo,
            r.CreatedAt
        )).ToList();
    }

    private static string SourceToCategory(string source) => source switch
    {
        "compliance" => "compliance",
        "sop"        => "sop",
        "faq"        => "policy",
        "manual"     => "manuals",
        _            => "manuals"
    };

    private static string MapDept(RagDepartment d) => d switch
    {
        RagDepartment.Sewing    => "sewing",
        RagDepartment.Cutting   => "cutting",
        RagDepartment.Finishing => "finishing",
        RagDepartment.Qc        => "qc",
        _ => "general"
    };

    private static string MapCat(RagCategory c) => c switch
    {
        RagCategory.Manuals    => "manuals",
        RagCategory.Compliance => "compliance",
        RagCategory.Qc         => "qc",
        RagCategory.Policy     => "policy",
        _ => "sop"
    };
}