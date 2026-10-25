using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Application.Dtos.Rag;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Settings;
using FactoryBrain.Application.Settings;
using FactoryBrain.Infrastructure.Rag;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// Admin endpoints for the RAG index.
/// <list type="bullet">
///   <item>
///     <c>GET /api/rag/reindex/plan</c> (Admin) reports what a reindex with
///     the current settings would do — documents, chunks, column
///     dimensions, size and cost — without calling any provider.
///     <c>POST /api/rag/reindex</c> (Admin) runs it only with
///     <c>{ confirmPlanId }</c> matching that plan (428 without it, 409 when
///     the plan is stale). A provider failure aborts before any write; a
///     database failure rolls everything back.
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
    private readonly EmbeddingReindexService _reindex;
    private readonly IAiSettingsProvider _settings;
    private readonly FactoryBrainDbContext _db;
    private readonly ILogger<RagController> _log;

    public RagController(
        IRagService rag,
        EmbeddingProviderResolver resolver,
        EmbeddingColumnAdmin admin,
        EmbeddingReindexService reindex,
        IAiSettingsProvider settings,
        FactoryBrainDbContext db,
        ILogger<RagController> log)
    {
        _rag = rag; _resolver = resolver; _admin = admin;
        _reindex = reindex; _settings = settings;
        _db = db; _log = log;
    }

    /// <summary>
    /// GET /api/rag/reindex/plan — what a reindex with the current embedding
    /// settings would embed, and what it would cost. No provider calls.
    /// </summary>
    [HttpGet("reindex/plan")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> ReindexPlan(CancellationToken ct)
        => SettingsResults.ToResult(this, await _reindex.PlanAsync(null, ct));

    /// <summary>
    /// POST /api/rag/reindex — re-embeds the documents in a reviewed plan
    /// with the current settings. Requires <c>{ "confirmPlanId": "…" }</c>.
    /// All-or-nothing: a provider failure stops before any write, and a
    /// database failure rolls the column, vectors and audit record back.
    /// </summary>
    [HttpPost("reindex")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Reindex([FromBody] ReindexRequest? body, CancellationToken ct)
    {
        try
        {
            var result = await _reindex.ReindexAsync(body?.ConfirmPlanId, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct);
            return SettingsResults.ToResult(this, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiSettingsUnavailableException)
        {
            // Database error: logged with the exception; the response carries
            // only a stable message and the trace id to look it up with.
            _log.LogError(ex, "Reindex failed and was rolled back.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "DatabaseError",
                details = "Reindex failed and was rolled back; no stored vectors or settings were changed. See the server log for this trace id.",
                traceId = SettingsResults.TraceId(HttpContext)
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
    /// so a later Admin-confirmed reindex (POST /api/rag/reindex) can fill
    /// them in. The response surfaces <c>embedded = false</c> + a
    /// human-readable <c>warning</c>.
    /// </para>
    /// </summary>
    [HttpPost("ingest")]
    [Authorize(Policy = AuthPolicies.Admin)]
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
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        await _settings.GetAsync(ct);
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
