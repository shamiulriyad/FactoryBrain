using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Custom exception thrown by <see cref="EmbeddingColumnAdmin"/> when an
/// embedding call fails mid-reindex. The controller catches this and
/// translates it into a <c>409 Conflict</c> with the canonical error
/// envelope.
/// </summary>
public sealed class EmbeddingProviderUnavailableException : Exception
{
    public EmbeddingProviderUnavailableException(string message) : base(message) { }
    public EmbeddingProviderUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Live column-dim inspector + resize for the pgvector column on
/// <c>document_chunks.Embedding</c>, plus the metadata-aware reindex that
/// keeps every row's embedding consistent with the configured embedder.
///
/// Used by:
///   * Program.cs startup — detects schema drift on every boot.
///   * POST /api/rag/reindex — runs the same pipeline on demand.
///   * RagHealthMonitor — runs after a probe recovers the provider.
///
/// All transactions are wrapped in
/// <c>db.Database.CreateExecutionStrategy().ExecuteAsync(...)</c> so the
/// retry-on-failure policy from the DbContext is honoured.
///
/// <para>
/// The "resize + reindex" pipeline is atomic: embeddings are pre-computed
/// in memory before any DB write. If a single embedding call fails, the
/// DB is never touched. When all embeddings are ready, we run an
/// <c>ALTER COLUMN ... TYPE vector(N) USING NULL</c> (only when the dim
/// changed) plus the per-row metadata + vector writes inside a single
/// transaction; a DB error mid-way rolls the whole batch back so the
/// stored vectors and column size stay exactly as they were.
/// </para>
///
/// <para>Database errors never set Degraded. Only failed embedding calls
/// do. The local hash embedder can never be degraded.</para>
/// </summary>
public sealed class EmbeddingColumnAdmin
{
    private readonly FactoryBrainDbContext _db;
    private readonly IEmbeddingService _embed;
    private readonly EmbeddingProviderResolver _resolver;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<EmbeddingColumnAdmin> _log;

    public EmbeddingColumnAdmin(
        FactoryBrainDbContext db,
        IEmbeddingService embed,
        EmbeddingProviderResolver resolver,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        ILogger<EmbeddingColumnAdmin> log)
    {
        _db = db; _embed = embed; _resolver = resolver;
        _httpFactory = httpFactory; _loggerFactory = loggerFactory;
        _log = log;
    }

    /// <summary>Read the configured dims from the resolver.</summary>
    public int ConfiguredDims => _resolver.Resolve().Configured.Dims;

    /// <summary>
    /// Dimension guard shared by every path that writes to
    /// <c>document_chunks.Embedding</c> (seeder, ingest, reindex). When
    /// the freshly-computed vector does NOT match the live column dim,
    /// the chunk is stored with <c>Embedding = null</c> and the matching
    /// <c>ManualDocument.EmbeddingProvider</c> flipped to
    /// <c>"pending"</c>; a warning is logged that names both the expected
    /// and the actual dimension so an operator can see exactly where the
    /// drift came from. A later reindex fills the gap.
    /// </summary>
    /// <returns>
    /// <c>null</c> on a dim mismatch; the input vector otherwise
    /// (unchanged). When <paramref name="applyToRow"/> is provided and we
    /// dropped the vector, the row's metadata is also flipped to
    /// <c>"pending"</c> so a future reindex picks it up.
    /// </returns>
    public Pgvector.Vector? GuardVectorDims(
        Pgvector.Vector? vector,
        ManualDocument? applyToRow = null)
    {
        if (vector is null) return null;
        int live;
        try
        {
            live = ProbeLiveColumnDim() ?? _resolver.Resolve().Configured.Dims;
        }
        catch
        {
            live = _resolver.Resolve().Configured.Dims;
        }
        var actual = ((float[])vector.ToArray()).Length;
        if (actual == live) return vector;
        var configuredDims = _resolver.Resolve().Configured.Dims;
        _log.LogWarning(
            "Embedding dimension mismatch: vector length={Actual} but live column dim={Live} (configuredProvider={Provider} configuredDims={Configured}). Storing chunk with Embedding=null and provider=pending; a future reindex will fill it in.",
            actual, live, _resolver.Resolve().Configured.Provider, configuredDims);
        if (applyToRow is not null)
        {
            applyToRow.EmbeddingProvider = "pending";
            applyToRow.EmbeddingModel    = "pending";
            applyToRow.Dims              = 0;
        }
        return null;
    }

    /// <summary>
    /// Read the live vector dimension of <c>document_chunks.Embedding</c>
    /// directly from <c>pg_attribute</c>. Returns <c>null</c> when the
    /// column is unsized. Throws when the table or column truly cannot be
    /// inspected (e.g. the relation doesn't exist at all).
    /// </summary>
    public int? ProbeLiveColumnDim()
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT format_type(atttypid, atttypmod), atttypid::regtype::text
                FROM pg_attribute
                WHERE attrelid = 'document_chunks'::regclass
                  AND attname  = 'Embedding';";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "Embedding column not found on document_chunks. Run the latest EF migration before starting the API.");
            }
            var formatted = reader.GetString(0);
            var typtype   = reader.GetString(1);
            var digits = new string(formatted.Where(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, out var d) || d <= 0)
            {
                return null;
            }
            return d;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            throw new InvalidOperationException(
                "document_chunks table not found. Run the latest EF migration before starting the API.", ex);
        }
        finally
        {
            if (conn.State == System.Data.ConnectionState.Open) conn.Close();
        }
    }

    /// <summary>Non-throwing variant for the query path.</summary>
    public int? TryProbeLiveColumnDim()
    {
        try { return ProbeLiveColumnDim(); }
        catch (InvalidOperationException) { return null; }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01") { return null; }
    }

    /// <summary>
    /// Atomic "compute outside, write inside one tx" resize + reindex.
    /// Returns the requested work, the work actually done, and the live
    /// dim before / after.
    /// </summary>
    public Task<ReindexOutcome> ResizeAndReindexAsync(CancellationToken ct)
        => RunResizeAndReindexAsync(probeBeforeWrite: true, ct);

    /// <summary>
    /// Reindex-only path: re-embeds any rows whose stored metadata
    /// disagrees with the active config (or whose stored vector is null
    /// / provider is "pending"). Skips the resize step when the dim
    /// hasn't changed. Used by the recovery loop and the controller when
    /// a previous resize already happened.
    /// </summary>
    public Task<ReindexOutcome> ReindexChangedDocsAsync(CancellationToken ct)
        => RunResizeAndReindexAsync(probeBeforeWrite: false, ct);

    private async Task<ReindexOutcome> RunResizeAndReindexAsync(bool probeBeforeWrite, CancellationToken ct)
    {
        // Re-resolve so we pick up any env change since the previous probe.
        EnvReloader.Reload(_log);
        _resolver.ReResolve();
        var r = _resolver.Resolve();

        // Reset the dev-only stub call counter at the START of every
        // reindex/resize so RAG_EMBEDDING_FAKE_FAIL_AFTER=N applies to the
        // current batch (the Nth call within THIS reindex throws).
        StubEmbeddingService.ResetCounter();

        if (_resolver.IsDegraded)
        {
            _log.LogWarning(
                "Reindex skipped — embedding provider degraded (configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims}). Stored vectors left untouched.",
                r.Configured.Provider, r.Configured.Dims);
            return new ReindexOutcome(0, 0, false, null, null, r.Configured);
        }

        // Probe the live dim up-front so we can fail fast when the column
        // is genuinely missing. This is a DB error path; do not call
        // MarkDegraded — surface as 500.
        int? live;
        try
        {
            live = probeBeforeWrite ? ProbeLiveColumnDim() : TryProbeLiveColumnDim();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Embedding column probe failed before reindex.");
            throw;
        }

        var targetDims = r.Configured.Dims;

        // (1) Pre-compute every new vector in memory OUTSIDE any
        // transaction. The configured primary embedder is built ONCE per
        // reindex/resize call (never per text) so the dev-only stub
        // AFTER=N semantics stay stable across the batch: AFTER=1 means
        // the first call in this reindex throws, before any DB write.
        var primary = BuildPrimaryForReindex();
        var pendingDocs = await LoadDocsNeedingReindexAsync(r.Configured, live, targetDims, ct).ConfigureAwait(false);
        var pending = new List<ReindexDocPlan>();
        try
        {
            foreach (var d in pendingDocs)
            {
                var docPlan = new ReindexDocPlan(d.Id, new List<ReindexChunkPlan>(d.Chunks.Count));
                foreach (var chunk in d.Chunks)
                {
                    Pgvector.Vector v;
                    try
                    {
                        v = EmbedChunkVia(primary, $"{chunk.Title} {chunk.Text}");
                    }
                    catch (Exception ex)
                    {
                        _resolver.MarkDegraded(
                            $"reindex pre-compute failed on doc={d.Id} chunk={chunk.Id} ({r.Configured.Provider})",
                            ex);
                        throw new EmbeddingProviderUnavailableException(
                            "embedding provider unavailable; reindex skipped", ex);
                    }
                    // Dimension guard: a freshly-computed vector whose
                    // length disagrees with the live column dim (or the
                    // configured dim when the column is unsized) is
                    // dropped here and the row's metadata is flipped to
                    // "pending" so the next reindex run can re-try with a
                    // different dim. The resize step above already keeps
                    // most paths in sync; this catches the rare case
                    // where a concurrent config flip landed between the
                    // probe and the embed call.
                    var guarded = GuardVectorDims(v, null);
                    if (guarded is null)
                    {
                        docPlan.Chunks.Add(new ReindexChunkPlan(chunk.Id, null));
                        continue;
                    }
                    docPlan.Chunks.Add(new ReindexChunkPlan(chunk.Id, guarded));
                }
                pending.Add(docPlan);
            }
        }
        catch (EmbeddingProviderUnavailableException)
        {
            // No DB writes happened — nothing to roll back.
            throw;
        }

        // (2) Apply resize + writes in one tx, inside the execution
        // strategy. Any DB error rolls back; nothing about Degraded flips.
        var strategy = _db.Database.CreateExecutionStrategy();
        ReindexOutcome outcome;
        try
        {
            outcome = await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    // 2a. Resize the column when needed. USING NULL drops
                    //     every row's old vector in one shot — the reindex
                    //     populates the new ones from the in-memory plan.
                    bool resized = false;
                    if (live != targetDims)
                    {
                        var sql = $@"
                            ALTER TABLE document_chunks
                                ALTER COLUMN ""Embedding"" DROP NOT NULL,
                                ALTER COLUMN ""Embedding"" TYPE vector({targetDims}) USING NULL;";
                        await _db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
                        resized = true;
                    }

                    // 2b. Reload the rows inside the transaction so we
                    //     have fresh EF state to mutate.
                    var docsToUpdate = await LoadDocsForReindexAsync(pendingDocs.Select(d => d.Id), ct).ConfigureAwait(false);

                    // 2c. Apply the pre-computed vectors + metadata.
                    int docsUpdated = 0;
                    int chunksUpdated = 0;
                    foreach (var docId in pendingDocs.Select(d => d.Id))
                    {
                        var d = docsToUpdate.FirstOrDefault(x => x.Id == docId);
                        if (d is null) continue;
                        d.EmbeddingProvider = r.Configured.Provider;
                        d.EmbeddingModel    = r.Configured.Model;
                        d.Dims              = r.Configured.Dims;
                        var docPlan = pending.FirstOrDefault(p => p.DocId == docId);
                        if (docPlan is null) continue;
                        foreach (var plan in docPlan.Chunks)
                        {
                            var chunk = d.Chunks.FirstOrDefault(c => c.Id == plan.ChunkId);
                            if (chunk is null) continue;
                            chunk.Embedding = plan.Vector;
                            chunksUpdated++;
                        }
                        docsUpdated++;
                    }

                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);

                    if (resized)
                    {
                        _log.LogWarning(
                            "Embedding column resized: dim {LiveBefore} -> {After} (provider={Provider} model={Model}).",
                            live, targetDims, r.Configured.Provider, r.Configured.Model);
                    }
                    else
                    {
                        _log.LogInformation(
                            "Embedding column already at dim={Dims}; no resize required.",
                            targetDims);
                    }
                    _log.LogInformation(
                        "Reindex complete: {Docs} docs / {Chunks} chunks (provider={Provider} model={Model} dims={Dims}).",
                        docsUpdated, chunksUpdated, r.Configured.Provider, r.Configured.Model, r.Configured.Dims);

                    return new ReindexOutcome(docsUpdated, chunksUpdated, resized, live, targetDims, r.Configured);
                }
                catch
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch (EmbeddingProviderUnavailableException)
        {
            // Already classified — don't surface as 500.
            throw;
        }
        catch (Exception ex)
        {
            // DB error — log it as a DB error, don't set Degraded, don't
            // mention API keys. The controller / middleware will surface
            // the standard {error, details} envelope via rethrow.
            _log.LogError(ex,
                "Reindex transaction failed; rolled back. No vector or schema changes were applied. configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims}.",
                r.Configured.Provider, r.Configured.Dims);
            throw;
        }
        return outcome;
    }

    /// <summary>
    /// Deprecated public entry — kept for callers that still want to run
    /// only the resize without a reindex. Now a thin wrapper around the
    /// atomic path with no pending docs to write.
    /// </summary>
    public Task<(int? LiveBefore, int LiveAfter)> ResizeColumnToConfiguredAsync(CancellationToken ct)
        => ResizeOnlyAsync(ct);

    private async Task<(int? LiveBefore, int LiveAfter)> ResizeOnlyAsync(CancellationToken ct)
    {
        EnvReloader.Reload(_log);
        _resolver.ReResolve();
        if (_resolver.IsDegraded)
        {
            _log.LogWarning("Resize skipped — embedding provider degraded.");
            var live = TryProbeLiveColumnDim();
            return (live, live ?? _resolver.Resolve().Configured.Dims);
        }
        StubEmbeddingService.ResetCounter();
        int? currentLive;
        try { currentLive = ProbeLiveColumnDim(); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Embedding column probe failed before resize.");
            throw;
        }
        var target = _resolver.Resolve().Configured.Dims;
        if (currentLive == target)
        {
            _log.LogInformation("Embedding column already at dim={Dims}; no resize required.", target);
            return (currentLive, target);
        }
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var sql = $@"
                    ALTER TABLE document_chunks
                        ALTER COLUMN ""Embedding"" DROP NOT NULL,
                        ALTER COLUMN ""Embedding"" TYPE vector({target}) USING NULL;";
                await _db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                _log.LogWarning("Embedding column resized: {LiveBefore} -> {After}.", currentLive, target);
                return (currentLive, target);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve which documents and chunks need re-embedding:
    ///   * stored metadata disagrees with the active configured embedder,
    ///   * OR the row's stored provider is "pending" (forward-compatible
    ///     with the 46 ingest path that creates rows before embedding),
    ///   * OR the row's stored vector is null.
    /// </summary>
    private async Task<List<ManualDocument>> LoadDocsNeedingReindexAsync(
        EmbeddingConfig configured,
        int? liveDims,
        int targetDims,
        CancellationToken ct)
    {
        var docs = await _db.ManualDocuments.AsNoTracking()
            .Include(d => d.Chunks)
            .ToListAsync(ct).ConfigureAwait(false);

        bool dimMatchesLive = liveDims.HasValue && liveDims.Value == targetDims;

        return docs.Where(d =>
        {
            if (d.EmbeddingProvider == "pending") return true;
            if (!dimMatchesLive && d.Chunks.Any(c => c.Embedding is null)) return true;
            if (dimMatchesLive && d.EmbeddingProvider == "local" && configured.IsHosted && d.Chunks.Any(c => c.Embedding is null)) return true;
            if (d.EmbeddingProvider != configured.Provider
                || d.EmbeddingModel    != configured.Model
                || d.Dims              != configured.Dims)
                return true;
            return d.Chunks.Any(c => c.Embedding is null);
        }).ToList();
    }

    private async Task<List<ManualDocument>> LoadDocsForReindexAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var idSet = ids.ToHashSet();
        return await _db.ManualDocuments
            .Where(d => idSet.Contains(d.Id))
            .Include(d => d.Chunks)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the primary embedder once for the duration of a reindex /
    /// resize call. For hosted providers we build the configured
    /// provider (no fallback wrapper); for local we just wrap the
    /// already-injected <see cref="IEmbeddingService"/>. Called from
    /// <see cref="RunResizeAndReindexAsync"/> before the per-chunk loop
    /// so the embedder instance stays stable across the batch — that is
    /// what makes the dev-only <c>RAG_EMBEDDING_FAKE_FAIL_AFTER=N</c>
    /// switch predictable (AFTER=1 means the first call of THIS reindex).
    /// </summary>
    private IEmbeddingService BuildPrimaryForReindex()
    {
        var r = _resolver.Resolve();
        if (r.Configured.IsLocal) return _embed;
        return _resolver.BuildPrimaryService(_httpFactory, _loggerFactory);
    }

    /// <summary>
    /// Embed a single chunk text via the pre-built primary embedder.
    /// Bridges sync / async without ASP.NET sync-over-async deadlocks.
    /// </summary>
    private static Pgvector.Vector EmbedChunkVia(IEmbeddingService primary, string text)
    {
        try
        {
            var task = primary.EmbedAsync(text, CancellationToken.None);
            if (task.IsCompletedSuccessfully) return task.Result;
            return task.GetAwaiter().GetResult();
        }
        catch (EmbeddingProviderUnavailableException) { throw; }
        catch (Exception ex)
        {
            throw new EmbeddingProviderUnavailableException(
                $"configured embedder call failed: {ex.GetType().Name}", ex);
        }
    }

    private sealed class ReindexDocPlan
    {
        public string DocId { get; }
        public List<ReindexChunkPlan> Chunks { get; }
        public ReindexDocPlan(string docId, List<ReindexChunkPlan> chunks)
        {
            DocId = docId; Chunks = chunks;
        }
    }
    private sealed record ReindexChunkPlan(string ChunkId, Pgvector.Vector? Vector);
}

/// <summary>Result of a reindex (and optional resize) cycle.</summary>
public sealed record ReindexOutcome(
    int Documents,
    int Chunks,
    bool Resized,
    int? LiveBefore,
    int? LiveAfter,
    EmbeddingConfig Configured);
