using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Application.Dtos.Rag;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Infrastructure.Rag;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// Admin endpoints for the RAG index.
/// <list type="bullet">
///   <item>
///     <c>POST /api/rag/reindex</c> probes the provider, then runs an
///     atomic resize + reindex cycle. Returns <c>409 Conflict</c> with
///     the canonical error envelope whenever the embedder is in
///     degraded mode OR an embedding call fails mid-batch (the DB is
///     never touched).
///   </item>
///   <item>
///     <c>POST /api/rag/ingest</c> validates the body with
///     <see cref="Validators.RagIngestRequestValidator"/>, chunks with
///     the domain-aware <see cref="TextChunker"/>, embeds with the
///     active configured embedder, and stores the row + chunks under
///     <c>manual_documents</c>. In degraded mode the chunks are stored
///     with <c>EmbeddingProvider = "pending"</c> so a subsequent reindex
///     can fill them in; the response surfaces
///     <c>embedded = false</c> + a <c>warning</c>.
///   </item>
///   <item>
///     <c>GET /api/rag/documents</c> returns the manual-doc index without
///     embedding vectors. Supports <c>?source=</c> filtering.
///   </item>
///   <item>
///     <c>GET /api/rag/status</c> returns
///     <c>{ configuredProvider, activeProvider, model, dims, storedDims, degraded, lastProbeAt, lastProbeOk, pendingCount }</c>.
///     No key or key fragment is ever exposed. <c>activeProvider</c>
///     always reports <c>"local"</c> when <c>degraded</c> is true.
///   </item>
/// </list>
/// </summary>
[ApiController]
[Route("api/rag")]
public class RagController : ControllerBase
{
    private readonly IRagService _rag;
    private readonly EmbeddingProviderResolver _resolver;
    private readonly EmbeddingColumnAdmin _admin;
    private readonly RagHealthMonitor _monitor;
    private readonly IHttpClientFactory _http;
    private readonly ILoggerFactory _loggerFactory;
    private readonly FactoryBrainDbContext _db;
    private readonly ILogger<RagController> _log;

    public RagController(
        IRagService rag,
        EmbeddingProviderResolver resolver,
        EmbeddingColumnAdmin admin,
        RagHealthMonitor monitor,
        IHttpClientFactory http,
        ILoggerFactory loggerFactory,
        FactoryBrainDbContext db,
        ILogger<RagController> log)
    {
        _rag = rag; _resolver = resolver; _admin = admin;
        _monitor = monitor; _http = http; _loggerFactory = loggerFactory;
        _db = db; _log = log;
    }

    /// <summary>
    /// POST /api/rag/reindex — probes the provider first, then runs the
    /// atomic resize + reindex cycle. Probing right before the reindex
    /// means a key that was set since the last periodic probe is picked
    /// up here.
    ///
    /// In degraded mode the action returns <c>409 Conflict</c> with the
    /// canonical error envelope — stored vectors and the column size are
    /// left untouched. If an embedding call fails mid-batch the same
    /// <c>409</c> is returned and the DB is never written.
    /// </summary>
    [HttpPost("reindex")]
    [AdminToken]
    [Authorize(Policy = AuthPolicies.AdminOrLegacyToken)]
    public async Task<IActionResult> Reindex(CancellationToken ct)
    {
        _log.LogInformation("Manual reindex requested via /api/rag/reindex");

        // Probe first so a newly-set API key (or recovery from a previous
        // failure) is reflected immediately. The health monitor does this
        // on its own interval, but the controller is the user-facing
        // escape hatch — it should not depend on probe timing.
        try
        {
            await _resolver.ProbeAsync(_http, _loggerFactory, ct);
        }
        catch (Exception ex) when (ex is not EmbeddingProviderUnavailableException)
        {
            // Probe is best-effort; a runtime DB error here is logged but
            // the reindex still proceeds if not degraded.
            _log.LogWarning(ex, "Pre-reindex probe threw unexpectedly; continuing.");
        }

        var r = _resolver.Resolve();
        if (_resolver.IsDegraded)
        {
            _log.LogWarning(
                "Reindex request rejected: degraded mode (configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims}).",
                r.Configured.Provider, r.Configured.Dims);
            return Conflict(new
            {
                error = "EmbeddingProviderUnavailable",
                details = "embedding provider unavailable; reindex skipped"
            });
        }

        try
        {
            var outcome = await _admin.ResizeAndReindexAsync(ct);

            return Ok(new
            {
                reindexed        = outcome.Documents,
                chunks           = outcome.Chunks,
                provider         = outcome.Configured.Provider,
                model            = outcome.Configured.Model,
                dims             = outcome.Configured.Dims,
                columnDimBefore  = outcome.LiveBefore,
                columnDimAfter   = outcome.LiveAfter,
                resized          = outcome.Resized
            });
        }
        catch (EmbeddingProviderUnavailableException ex)
        {
            _log.LogWarning(ex,
                "Reindex aborted before any DB write: embedding provider became unavailable mid-batch. configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims}.",
                r.Configured.Provider, r.Configured.Dims);
            return Conflict(new
            {
                error = "EmbeddingProviderUnavailable",
                details = "embedding provider unavailable; reindex skipped"
            });
        }
        catch (Exception ex)
        {
            // Database error — never set Degraded, never log the key.
            _log.LogError(ex,
                "Reindex failed mid-transaction; rolled back. configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims}.",
                r.Configured.Provider, r.Configured.Dims);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "DatabaseError",
                details = ex.Message
            });
        }
    }

    /// <summary>
    /// POST /api/rag/ingest — validate the body, chunk it with the
    /// domain-aware chunker, embed each chunk with the configured
    /// embedder, and persist a new <c>ManualDocument</c> row + its
    /// <c>DocumentChunk</c> children.
    ///
    /// <para>
    /// In degraded mode the chunks are stored with
    /// <c>EmbeddingProvider = "pending"</c> and <c>Embedding = null</c>
    /// so a later reindex (startup or POST /api/rag/reindex) can fill
    /// them in. The response surfaces <c>embedded = false</c> + a
    /// human-readable <c>warning</c>.
    /// </para>
    /// </summary>
    [HttpPost("ingest")]
    [AdminToken]
    [Authorize(Policy = AuthPolicies.AdminOrLegacyToken)]
    public async Task<IActionResult> Ingest([FromBody] RagIngestRequest body, CancellationToken ct)
    {
        // Step 51: body validation runs in the global FluentValidationFilter
        // (registered in Program.cs); on failure it short-circuits with a
        // 400 ValidationProblemDetails (RFC 7807 with an errors map) before
        // this method runs.
        var outcome = await _rag.IngestAsync(new IngestInput(
            Title:   body!.Title.Trim(),
            TitleBn: string.IsNullOrWhiteSpace(body.TitleBn) ? null : body.TitleBn.Trim(),
            Source:  body.Source.Trim().ToLowerInvariant(),
            Tags:    body.Tags ?? new List<string>(),
            Content: body.Content,
            Url:     string.IsNullOrWhiteSpace(body.Url) ? null : body.Url.Trim(),
            IsDemo:  false), ct);

        _log.LogInformation(
            "Ingested {DocId}: {Chunks} chunks (provider={Provider} model={Model} dims={Dims} embedded={Embedded}).",
            outcome.DocumentId, outcome.Chunks, outcome.Provider, outcome.Model, outcome.Dims, outcome.Embedded);

        return StatusCode(StatusCodes.Status201Created, new RagIngestResponse(
            outcome.DocumentId,
            outcome.Chunks,
            outcome.Provider,
            outcome.Model,
            outcome.Dims,
            outcome.Embedded,
            outcome.Warning));
    }

    /// <summary>
    /// GET /api/rag/documents — list every manual document currently in
    /// the index, optionally filtered by <c>?source=</c>. Embedding
    /// vectors are never returned. Newest rows first.
    /// </summary>
    [HttpGet("documents")]
    public async Task<IActionResult> ListDocuments([FromQuery] string? source, CancellationToken ct)
    {
        var docs = await _rag.ListDocumentsAsync(source, ct);
        return Ok(docs);
    }

    /// <summary>
    /// GET /api/rag/status — shows the active and configured embedder
    /// config plus the most-recent probe state and the number of rows
    /// that would be re-embedded by the next reindex. Never includes
    /// the API key or a key fragment.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var r   = _resolver.Resolve();
        var cfg = r.Configured;
        var act = r.Active;
        var isDegraded = _resolver.IsDegraded;
        var activeProviderForStatus = isDegraded ? "local" : act.Provider;
        int storedDims;
        try
        {
            storedDims = _admin.ProbeLiveColumnDim() ?? act.Dims;
        }
        catch
        {
            storedDims = act.Dims;
        }
        int pendingCount = ComputePendingCount(cfg);
        return Ok(new
        {
            configuredProvider = cfg.Provider,
            activeProvider     = activeProviderForStatus,
            model              = act.Model,
            dims               = act.Dims,
            storedDims         = storedDims,
            degraded           = isDegraded,
            lastProbeAt        = _resolver.LastProbeAtUtc,
            lastProbeOk        = _resolver.LastProbeOk,
            pendingCount       = pendingCount
        });
    }

    private int ComputePendingCount(EmbeddingConfig configured)
    {
        // Count every document that the next non-degraded reindex would
        // touch: stored provider is "pending", stored provider/model/dims
        // disagrees with the active configured embedder, OR any chunk has
        // a null Embedding (the reindex pipeline will fill those in).
        //
        // When the resolver is degraded we still emit a count: any doc
        // stamped "pending" or with a null chunk embedding is a backlog
        // item the next healthy reindex will process. Docs whose stored
        // metadata matches the configured embedder (the only thing a
        // healthy run could compare against) simply don't match those
        // first three clauses and fall out of the count — same as before.
        try
        {
            return _db.ManualDocuments.AsNoTracking()
                .Where(d => d.EmbeddingProvider == "pending"
                         || d.EmbeddingProvider != configured.Provider
                         || d.EmbeddingModel    != configured.Model
                         || d.Dims              != configured.Dims
                         || d.Chunks.Any(c => c.Embedding == null))
                .Count();
        }
        catch
        {
            return 0;
        }
    }
}
