using System.Security.Cryptography;
using System.Text;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Dtos.Settings;
using FactoryBrain.Application.Settings;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Embedding settings changes and reindexing, as explicit Admin operations:
/// <list type="number">
///   <item><b>Plan</b> (no provider calls): which documents and chunks would be
///   embedded, the column's current and target dimensions, size and cost.</item>
///   <item><b>Confirm</b>: the caller sends back the plan id it reviewed. If
///   anything changed since, the request is refused with the fresh plan.</item>
///   <item><b>Apply</b>: one verification call, all vectors computed in memory
///   (a provider failure stops here with nothing written), then one
///   transaction for the column resize, the vectors, the settings and the
///   audit record. A cross-instance lease prevents two applies at once.</item>
/// </list>
/// Nothing in this class runs automatically.
/// </summary>
public sealed class EmbeddingReindexService
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    private readonly FactoryBrainDbContext _db;
    private readonly IAiSettingsProvider _settings;
    private readonly EmbeddingProviderResolver _resolver;
    private readonly AiSecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILoggerFactory _loggers;
    private readonly IHostEnvironment _env;
    private readonly ILogger<EmbeddingReindexService> _log;

    public EmbeddingReindexService(
        FactoryBrainDbContext db, IAiSettingsProvider settings, EmbeddingProviderResolver resolver,
        AiSecretProtector protector, IHttpClientFactory http, ILoggerFactory loggers, IHostEnvironment env,
        ILogger<EmbeddingReindexService> log)
    {
        _db = db; _settings = settings; _resolver = resolver; _protector = protector;
        _http = http; _loggers = loggers; _env = env; _log = log;
    }

    // ── Status ──────────────────────────────────────────────────────────────

    public async Task<EmbeddingSettingsResponse> StatusAsync(CancellationToken ct = default)
    {
        var snap = await _settings.GetAsync(ct);
        var current = _resolver.CurrentSettings();
        var column = await ColumnDimsAsync(ct);
        var docs = await LoadDocStatesAsync(ct);
        return new EmbeddingSettingsResponse(
            current.Provider, current.Model, current.Dimensions,
            AiSettingsWire.Source(current.Source),
            current.ApiKey is not null,
            AiSettingsWire.KeyStatus(current.KeyStatus),
            column,
            column == current.Dimensions,
            docs.Count,
            docs.Count(d => NeedsReindex(d, current)),
            _resolver.ResolveFor(current).Degraded,
            snap.Version,
            snap.UpdatedAt,
            _protector.CanEncrypt,
            EmbeddingCatalog.Models
                .Where(m => m.Provider != EmbeddingCatalog.Stub || _env.IsDevelopment())
                .Select(m => new EmbeddingModelOption(m.Provider, m.Model, m.DefaultDimensions, m.MinDimensions, m.MaxDimensions))
                .ToList());
    }

    // ── Plan ────────────────────────────────────────────────────────────────

    /// <summary>Plan for a target configuration (null fields: keep the current value). No provider calls.</summary>
    public async Task<SettingsWriteResult<ReindexPlanDto>> PlanAsync(EmbeddingPlanRequest? target, CancellationToken ct = default)
    {
        await _settings.GetAsync(ct);
        var current = _resolver.CurrentSettings();
        var (t, errors) = NormalizeTarget(current, target?.Provider, target?.Model, target?.Dimensions);
        if (errors.Count > 0) return SettingsWriteResult<ReindexPlanDto>.Fail(SettingsWriteOutcome.Invalid, errors);
        var plan = await BuildPlanAsync(current, t, ct);
        return SettingsWriteResult<ReindexPlanDto>.Ok(plan.Dto);
    }

    // ── Apply: reindex with the current settings ────────────────────────────

    public async Task<SettingsWriteResult<ReindexOutcomeDto>> ReindexAsync(
        string? confirmPlanId, AuditActor actor, string? traceId, CancellationToken ct = default)
    {
        await _settings.GetAsync(ct);
        var current = _resolver.CurrentSettings();
        if (string.IsNullOrWhiteSpace(confirmPlanId))
        {
            var plan = await BuildPlanAsync(current, Target(current), ct);
            return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.ConfirmationRequired, detail: plan.Dto);
        }
        return await ApplyAsync(current, settingsChange: null, confirmPlanId, expectedVersion: null, actor, traceId, ct);
    }

    // ── Apply: change the embedding settings ────────────────────────────────

    public async Task<SettingsWriteResult<EmbeddingUpdateResultDto>> UpdateSettingsAsync(
        EmbeddingSettingsRequest req, AuditActor actor, string? traceId, CancellationToken ct = default)
    {
        await _settings.GetAsync(ct);
        var current = _resolver.CurrentSettings();
        var (t, errors) = NormalizeTarget(current, req.Provider, req.Model, req.Dimensions);
        if (errors.Count > 0) return SettingsWriteResult<EmbeddingUpdateResultDto>.Fail(SettingsWriteOutcome.Invalid, errors);

        var newKey = SecretValue.From(req.ApiKey);
        if (newKey is not null && !_protector.CanEncrypt)
            return SettingsWriteResult<EmbeddingUpdateResultDto>.Fail(SettingsWriteOutcome.EncryptionKeyMissing);

        var sameShape = t.Provider == current.Provider && t.Model == current.Model && t.Dimensions == current.Dimensions;
        // A key is only reused for the same provider: one vendor's key is never sent to another.
        var key = newKey ?? (req.ClearKey == true || t.Provider != current.Provider ? null : current.ApiKey);
        if (EmbeddingCatalog.IsHosted(t.Provider) && key is null && !(sameShape && req.ClearKey == true))
            return SettingsWriteResult<EmbeddingUpdateResultDto>.Fail(SettingsWriteOutcome.Invalid,
                new Dictionary<string, string> { ["apiKey"] = $"An API key for {t.Provider} embeddings is required." });
        if (key is not null && !_protector.CanEncrypt)
            return SettingsWriteResult<EmbeddingUpdateResultDto>.Fail(SettingsWriteOutcome.EncryptionKeyMissing);

        var keyChange = newKey is not null ? (current.ApiKey is not null ? "replaced" : "set")
                      : key is null ? (current.ApiKey is not null ? "cleared" : "unchanged")
                      : current.Source == AiSettingsSource.Database ? "unchanged" : "unchanged (moved from startup configuration)";
        var target = new EmbeddingSettings(t.Provider, t.Model, t.Dimensions, key,
            key is null ? SecretStatus.NotSet : SecretStatus.Set, AiSettingsSource.Database);
        var change = new SettingsChange(target, keyChange);

        if (sameShape)
        {
            // Key-only (or no-op) save: the vectors don't change, so no plan is needed.
            var saved = await SaveKeyOnlyAsync(current, change, req.ExpectedVersion, actor, traceId, ct);
            if (saved != SettingsWriteOutcome.Ok) return SettingsWriteResult<EmbeddingUpdateResultDto>.Fail(saved);
            _settings.Invalidate();
            return SettingsWriteResult<EmbeddingUpdateResultDto>.Ok(new(await StatusAsync(ct), null));
        }

        if (string.IsNullOrWhiteSpace(req.ConfirmPlanId))
        {
            var plan = await BuildPlanAsync(current, t, ct);
            return SettingsWriteResult<EmbeddingUpdateResultDto>.Fail(SettingsWriteOutcome.ConfirmationRequired, detail: plan.Dto);
        }

        var applied = await ApplyAsync(target, change, req.ConfirmPlanId, req.ExpectedVersion, actor, traceId, ct);
        return applied.Outcome == SettingsWriteOutcome.Ok
            ? SettingsWriteResult<EmbeddingUpdateResultDto>.Ok(new(await StatusAsync(ct), applied.Value))
            : SettingsWriteResult<EmbeddingUpdateResultDto>.Fail(applied.Outcome, applied.Errors, applied.Detail);
    }

    private sealed record SettingsChange(EmbeddingSettings Target, string KeyChange);

    private async Task<SettingsWriteOutcome> SaveKeyOnlyAsync(
        EmbeddingSettings current, SettingsChange change, long? expectedVersion, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var row = await AiSettingsRows.LockAsync(_db, ct);
            if (expectedVersion is { } ev && ev != row.Version)
            {
                await tx.RollbackAsync(ct);
                return SettingsWriteOutcome.Conflict;
            }
            var now = DateTimeOffset.UtcNow;
            WriteEmbeddingGroup(row, change, now);
            AiSettingsRows.Touch(row, actor, now);
            _db.AuditLog.Add(AiSettingsRows.Audit(actor, "settings.embedding.updated", "settings", "embedding",
                Shape(current.Provider, current.Dimensions), Shape(change.Target.Provider, change.Target.Dimensions),
                $"apiKey: {change.KeyChange}; vectors unchanged; source: {AiSettingsWire.Source(current.Source)} → database; version: {row.Version}",
                traceId, now));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SettingsWriteOutcome.Ok;
        });
    }

    private void WriteEmbeddingGroup(AiSettingsRecord row, SettingsChange change, DateTimeOffset now)
    {
        row.EmbeddingProvider = change.Target.Provider;
        row.EmbeddingModel = change.Target.Model;
        row.EmbeddingDimensions = change.Target.Dimensions;
        row.EmbeddingApiKeyEncrypted = change.Target.ApiKey is { } k
            ? _protector.Protect(k.Reveal(), AiSecretProtector.EmbeddingApiKeyPurpose)
            : null;
        row.EmbeddingUpdatedAt = now;
    }

    // ── Plan internals ──────────────────────────────────────────────────────

    private sealed record DocState(string Id, string Provider, string Model, int Dims, List<ChunkState> Chunks);
    private sealed record ChunkState(string Id, string Title, string Text, bool HasEmbedding);
    private sealed record Plan(ReindexPlanDto Dto, List<DocState> Docs, int? ColumnDims, EmbeddingTargetDto Target, bool SettingsChange);

    private static EmbeddingTargetDto Target(EmbeddingSettings s) => new(s.Provider, s.Model, s.Dimensions);

    private (EmbeddingTargetDto Target, Dictionary<string, string> Errors) NormalizeTarget(
        EmbeddingSettings current, string? provider, string? model, int? dims)
    {
        var p = string.IsNullOrWhiteSpace(provider) ? current.Provider : provider;
        var sameProvider = string.Equals(p?.Trim(), current.Provider, StringComparison.OrdinalIgnoreCase);
        var (np, nm, nd, errors) = EmbeddingCatalog.Normalize(
            p,
            string.IsNullOrWhiteSpace(model) && sameProvider ? current.Model : model,
            dims ?? (sameProvider && string.IsNullOrWhiteSpace(model) ? current.Dimensions : null));
        if (np == EmbeddingCatalog.Stub && !_env.IsDevelopment())
            errors["provider"] = "The stub embedding provider is only available in Development.";
        return (new EmbeddingTargetDto(np, nm, nd), errors);
    }

    private static bool NeedsReindex(DocState d, EmbeddingSettings s) =>
        d.Provider == "pending"
        || d.Provider != s.Provider || d.Model != s.Model || d.Dims != s.Dimensions
        || d.Chunks.Any(c => !c.HasEmbedding);

    private async Task<List<DocState>> LoadDocStatesAsync(CancellationToken ct)
    {
        var rows = await _db.ManualDocuments.AsNoTracking()
            .OrderBy(d => d.Id)
            .Select(d => new
            {
                d.Id, d.EmbeddingProvider, d.EmbeddingModel, d.Dims,
                Chunks = d.Chunks.OrderBy(c => c.Id).Select(c => new { c.Id, c.Title, c.Text, Has = c.Embedding != null }).ToList(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new DocState(r.Id, r.EmbeddingProvider ?? "pending", r.EmbeddingModel ?? "pending", r.Dims,
            r.Chunks.Select(c => new ChunkState(c.Id, c.Title, c.Text, c.Has)).ToList())).ToList();
    }

    private async Task<int?> ColumnDimsAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = EmbeddingColumnAdmin.ColumnDimsSql;
            cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            var v = await cmd.ExecuteScalarAsync(ct);
            return v is string s ? EmbeddingColumnAdmin.ParseDims(s) : null;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    private static string EmbeddedText(ChunkState c) => $"{c.Title} {c.Text}";

    private async Task<Plan> BuildPlanAsync(EmbeddingSettings current, EmbeddingTargetDto target, CancellationToken ct)
    {
        var snap = await _settings.GetAsync(ct);
        var column = await ColumnDimsAsync(ct);
        var docs = await LoadDocStatesAsync(ct);

        var settingsChange = target.Provider != current.Provider || target.Model != current.Model || target.Dimensions != current.Dimensions;
        var resize = column != target.Dimensions;
        var all = settingsChange || resize;
        var targetSettings = new EmbeddingSettings(target.Provider, target.Model, target.Dimensions, null, SecretStatus.NotSet, AiSettingsSource.Database);
        var selected = all ? docs : docs.Where(d => NeedsReindex(d, targetSettings)).ToList();

        var chunks = selected.Sum(d => d.Chunks.Count);
        var bytes = selected.SelectMany(d => d.Chunks).Sum(c => (long)Encoding.UTF8.GetByteCount(EmbeddedText(c)));
        var populated = docs.SelectMany(d => d.Chunks).Count(c => c.HasEmbedding);

        var warnings = new List<string>();
        if (resize && column is not null)
            warnings.Add(populated > 0
                ? $"The vector column changes from vector({column}) to vector({target.Dimensions}); all {populated} stored vectors are replaced."
                : $"The vector column changes from vector({column}) to vector({target.Dimensions}) (no vectors are stored yet).");
        if (EmbeddingCatalog.IsHosted(target.Provider))
            warnings.Add($"Embedding uses {target.Provider} and is billed by the provider: {chunks} chunk(s) plus one verification call.");
        if (target.Provider == current.Provider && EmbeddingCatalog.IsHosted(target.Provider) && current.ApiKey is null)
            warnings.Add($"No {target.Provider} embedding API key is stored; include one when you confirm.");
        if (selected.Count == 0 && !resize)
            warnings.Add("Nothing to re-embed: every document already matches these settings.");

        var tokens = bytes; // a BPE token covers at least one byte, so tokens ≤ bytes
        var cost = Cost(target, tokens);
        var planId = PlanId(current, column, target, all, selected);

        var dto = new ReindexPlanDto(
            planId, Target(current), AiSettingsWire.Source(current.Source), column, target,
            RequiresReindex: selected.Count > 0 || resize || settingsChange,
            RequiresColumnResize: resize,
            ReembedsAllDocuments: all,
            Documents: selected.Count,
            Chunks: chunks,
            Bytes: bytes,
            EstimatedTokensUpperBound: tokens,
            Cost: cost,
            SettingsVersion: snap.Version,
            Warnings: warnings);
        return new Plan(dto, selected, column, target, settingsChange);
    }

    private ReindexCostDto Cost(EmbeddingTargetDto target, long tokens)
    {
        if (!EmbeddingCatalog.IsHosted(target.Provider))
            return new ReindexCostDto(0, false, "USD", "The local embedder makes no provider calls: no cost.");
        var price = _settings.Startup.EmbeddingPricePerMillionTokensUsd;
        if (price is null)
            return new ReindexCostDto(null, false, "USD",
                $"Cost can't be estimated reliably: no price is configured for {target.Provider} {target.Model} " +
                $"(set RAG_EMBEDDING_PRICE_PER_MILLION_TOKENS_USD on the API). At most {tokens:N0} tokens will be sent.");
        var usd = Math.Round(tokens / 1_000_000.0 * price.Value, 6);
        return new ReindexCostDto(usd, true, "USD",
            $"Upper bound: one token per UTF-8 byte ({tokens:N0}) at the configured {price.Value} USD per 1M tokens. " +
            "Real token counts are usually several times lower. The configured price is not verified against the provider.");
    }

    /// <summary>
    /// Identifies exactly what was reviewed: the current embedding settings
    /// (not the global settings version, so an unrelated LLM save doesn't
    /// invalidate an embedding plan), the column size, the target and the
    /// exact document / chunk set.
    /// </summary>
    private static string PlanId(EmbeddingSettings current, int? column, EmbeddingTargetDto t, bool all, List<DocState> docs)
    {
        var sb = new StringBuilder();
        sb.Append(current.Provider).Append('/').Append(current.Model).Append('/').Append(current.Dimensions).Append('/')
          .Append(AiSettingsWire.Source(current.Source)).Append('|').Append(column?.ToString() ?? "-").Append('|')
          .Append(t.Provider).Append('|').Append(t.Model).Append('|').Append(t.Dimensions).Append('|').Append(all);
        foreach (var d in docs) sb.Append('|').Append(d.Id).Append(':').Append(d.Chunks.Count);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    // ── Apply internals ─────────────────────────────────────────────────────

    private async Task<SettingsWriteResult<ReindexOutcomeDto>> ApplyAsync(
        EmbeddingSettings target, SettingsChange? settingsChange, string confirmPlanId, long? expectedVersion,
        AuditActor actor, string? traceId, CancellationToken ct)
    {
        var leaseId = Guid.NewGuid();
        if (!await TryTakeLeaseAsync(leaseId, ct))
            return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.Conflict,
                new Dictionary<string, string> { ["reindex"] = "Another reindex is running. Try again when it has finished." });

        Plan? plan = null;
        try
        {
            var snap = await _settings.GetAsync(ct);
            if (expectedVersion is { } ev && ev != snap.Version)
                return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.Conflict);

            var current = _resolver.CurrentSettings();
            plan = await BuildPlanAsync(current, Target(target), ct);
            if (!string.Equals(plan.Dto.PlanId, confirmPlanId.Trim(), StringComparison.Ordinal))
                return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.PlanChanged, detail: plan.Dto);

            // (1) Verify the provider really returns the requested size.
            if (!target.IsLocal)
            {
                var (ok, actual, error) = await _resolver.VerifyAsync(target, _http, _loggers, ct);
                if (error is not null)
                {
                    await AuditFailureAsync(actor, traceId, plan, "provider_error", error, ct);
                    return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.ProviderFailed);
                }
                if (!ok)
                {
                    await AuditFailureAsync(actor, traceId, plan, "dimension_mismatch", null, ct);
                    return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.DimensionMismatch,
                        new Dictionary<string, string> { ["dimensions"] = $"The provider returned {actual} dimensions; {target.Dimensions} were requested." },
                        new { expected = target.Dimensions, actual });
                }
            }

            // (2) Compute every vector before touching the database.
            var primary = _resolver.BuildPrimary(target, _http, _loggers);
            var vectors = new Dictionary<string, Pgvector.Vector>(StringComparer.Ordinal);
            try
            {
                foreach (var chunk in plan.Docs.SelectMany(d => d.Chunks))
                {
                    var v = await primary.EmbedAsync(EmbeddedText(chunk), ct);
                    var len = v.ToArray().Length;
                    if (len != target.Dimensions)
                    {
                        await AuditFailureAsync(actor, traceId, plan, "dimension_mismatch", null, ct);
                        return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.DimensionMismatch,
                            new Dictionary<string, string> { ["dimensions"] = $"The provider returned {len} dimensions; {target.Dimensions} were requested." },
                            new { expected = target.Dimensions, actual = len });
                    }
                    vectors[chunk.Id] = v;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning("Reindex aborted before any database write: embedding call failed ({ErrorType}). provider={Provider}.",
                    ex.GetType().Name, target.Provider);
                await AuditFailureAsync(actor, traceId, plan, "provider_error", ex, ct);
                return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.ProviderFailed);
            }

            // (3) One transaction: resize, vectors, settings, audit, lease release.
            ReindexOutcomeDto? outcome;
            try
            {
                outcome = await CommitAsync(leaseId, target, settingsChange, plan, vectors, actor, traceId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Reindex transaction failed and was rolled back; no settings, column or vector changes were applied.");
                await AuditFailureAsync(actor, traceId, plan, "database_error", null, ct);
                throw;
            }
            if (outcome is null)
                return SettingsWriteResult<ReindexOutcomeDto>.Fail(SettingsWriteOutcome.PlanChanged,
                    detail: (await BuildPlanAsync(_resolver.CurrentSettings(), Target(target), ct)).Dto);

            _settings.Invalidate();
            _log.LogInformation(
                "Reindex complete: {Docs} docs / {Chunks} chunks, provider={Provider} model={Model} dims={Dims}, column {Before}->{After}.",
                outcome.Documents, outcome.Chunks, target.Provider, target.Model, target.Dimensions,
                outcome.ColumnDimensionsBefore, outcome.ColumnDimensionsAfter);
            return SettingsWriteResult<ReindexOutcomeDto>.Ok(outcome);
        }
        finally
        {
            await ReleaseLeaseAsync(leaseId);
        }
    }

    /// <returns>The outcome, or null when the settings changed after the plan was checked.</returns>
    private async Task<ReindexOutcomeDto?> CommitAsync(
        Guid leaseId, EmbeddingSettings target, SettingsChange? settingsChange, Plan plan,
        Dictionary<string, Pgvector.Vector> vectors, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var row = await AiSettingsRows.LockAsync(_db, ct);
            // Re-check under the row lock: the embedding settings must still be
            // the ones the plan was made for (an LLM change doesn't matter).
            if (row.ReindexLeaseId != leaseId || !SameEmbedding(EffectiveEmbedding(row), plan.Dto.Current))
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var before = await ColumnDimsAsync(ct);
            var resized = before != target.Dimensions;
            if (resized)
            {
                // A type modifier can't be a bind parameter. The value is an
                // int validated against the catalogue, never user text.
                var alter = "ALTER TABLE document_chunks ALTER COLUMN \"Embedding\" DROP NOT NULL, " +
                            "ALTER COLUMN \"Embedding\" TYPE vector(" +
                            target.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") USING NULL;";
                await _db.Database.ExecuteSqlRawAsync(alter, ct);
            }

            var planned = plan.Docs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var docs = resized
                ? await _db.ManualDocuments.Include(d => d.Chunks).ToListAsync(ct)
                : await _db.ManualDocuments.Include(d => d.Chunks).Where(d => planned.Contains(d.Id)).ToListAsync(ct);

            int docsUpdated = 0, chunksUpdated = 0, markedPending = 0;
            foreach (var doc in docs)
            {
                if (planned.Contains(doc.Id))
                {
                    var complete = true;
                    foreach (var c in doc.Chunks)
                    {
                        if (vectors.TryGetValue(c.Id, out var v)) { c.Embedding = v; chunksUpdated++; }
                        else { c.Embedding = null; complete = false; } // added after the plan; left for the next reindex
                    }
                    (doc.EmbeddingProvider, doc.EmbeddingModel, doc.Dims) = complete
                        ? (target.Provider, target.Model, target.Dimensions)
                        : ("pending", "pending", 0);
                    docsUpdated++;
                }
                else
                {
                    // Not in the plan (e.g. ingested meanwhile); the resize nulled its vectors.
                    foreach (var c in doc.Chunks) c.Embedding = null;
                    doc.EmbeddingProvider = "pending"; doc.EmbeddingModel = "pending"; doc.Dims = 0;
                    markedPending++;
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (settingsChange is not null)
            {
                var prev = plan.Dto.Current;
                WriteEmbeddingGroup(row, settingsChange, now);
                AiSettingsRows.Touch(row, actor, now);
                _db.AuditLog.Add(AiSettingsRows.Audit(actor, "settings.embedding.updated", "settings", "embedding",
                    Shape(prev.Provider, prev.Dimensions), Shape(target.Provider, target.Dimensions),
                    $"provider/model/dims: {prev.Provider}/{prev.Model}/{prev.Dimensions} → {target.Provider}/{target.Model}/{target.Dimensions}; " +
                    $"apiKey: {settingsChange.KeyChange}; plan: {plan.Dto.PlanId}; version: {row.Version}",
                    traceId, now));
            }
            else
            {
                AiSettingsRows.Touch(row, actor, now);
            }
            row.ReindexLeaseId = null;
            row.ReindexLeaseExpiresAt = null;

            _db.AuditLog.Add(AiSettingsRows.Audit(actor, "rag.reindex.completed", "rag_index", "document_chunks",
                before?.ToString() ?? "unsized", target.Dimensions.ToString(),
                $"documents: {docsUpdated}; chunks: {chunksUpdated}; marked pending: {markedPending}; " +
                $"embedding: {target.Provider}/{target.Model}/{target.Dimensions}; column resized: {resized}; plan: {plan.Dto.PlanId}",
                traceId, now));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ReindexOutcomeDto(plan.Dto.PlanId, docsUpdated, chunksUpdated, resized, before, target.Dimensions,
                new EmbeddingTargetDto(target.Provider, target.Model, target.Dimensions), row.Version);
        });
    }

    private async Task<bool> TryTakeLeaseAsync(Guid leaseId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO ai_settings (\"Id\", \"Version\", \"UpdatedAt\") VALUES (1, 0, now()) ON CONFLICT (\"Id\") DO NOTHING", ct);
        var expires = DateTimeOffset.UtcNow.Add(LeaseDuration);
        var taken = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ai_settings SET ""ReindexLeaseId"" = {leaseId}, ""ReindexLeaseExpiresAt"" = {expires}
            WHERE ""Id"" = 1 AND (""ReindexLeaseId"" IS NULL OR ""ReindexLeaseExpiresAt"" < now())", ct);
        return taken == 1;
    }

    private async Task ReleaseLeaseAsync(Guid leaseId)
    {
        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ai_settings SET ""ReindexLeaseId"" = NULL, ""ReindexLeaseExpiresAt"" = NULL
                WHERE ""Id"" = 1 AND ""ReindexLeaseId"" = {leaseId}");
        }
        catch (Exception ex)
        {
            // The lease expires on its own; never mask the original outcome.
            _log.LogWarning("Could not release the reindex lease ({ErrorType}); it expires in {Minutes} minutes.",
                ex.GetType().Name, LeaseDuration.TotalMinutes);
        }
    }

    /// <summary>Record a failed apply in its own transaction (the main one never committed). No exception text.</summary>
    private async Task AuditFailureAsync(AuditActor actor, string? traceId, Plan plan, string failure, Exception? ex, CancellationToken ct)
    {
        try
        {
            _db.ChangeTracker.Clear();
            var t = plan.Target;
            _db.AuditLog.Add(AiSettingsRows.Audit(actor, "rag.reindex.failed", "rag_index", "document_chunks",
                plan.ColumnDims?.ToString() ?? "unsized", t.Dimensions.ToString(),
                $"failure: {failure}{(ex is null ? "" : $" ({ex.GetType().Name})")}; embedding: {t.Provider}/{t.Model}/{t.Dimensions}; " +
                $"settings change: {plan.SettingsChange}; plan: {plan.Dto.PlanId}; nothing was written",
                traceId, DateTimeOffset.UtcNow));
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception auditEx)
        {
            _log.LogError("Could not write the reindex failure to the audit log ({ErrorType}).", auditEx.GetType().Name);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    private static string Shape(string provider, int dims) => $"{provider}/{dims}";

    /// <summary>The embedding settings a locked row stands for (startup configuration if never saved).</summary>
    private EmbeddingSettings EffectiveEmbedding(AiSettingsRecord row)
    {
        var persisted = row.EmbeddingUpdatedAt is null
            ? PersistedAiSettings.None
            : new PersistedAiSettings(null, new EmbeddingGroup(row.EmbeddingProvider, row.EmbeddingModel, row.EmbeddingDimensions, null), row.Version, null, null);
        return _resolver.Normalize(AiSettingsResolution.Resolve(null, persisted, _settings.Startup).Embedding);
    }

    private static bool SameEmbedding(EmbeddingSettings s, EmbeddingTargetDto t) =>
        s.Provider == t.Provider && s.Model == t.Model && s.Dimensions == t.Dimensions;
}
