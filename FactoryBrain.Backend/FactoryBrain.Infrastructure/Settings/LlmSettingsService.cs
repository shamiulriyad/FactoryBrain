using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Settings;
using FactoryBrain.Application.Settings;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Settings;

/// <summary>
/// Reads and writes the LLM settings group. A save turns the effective
/// configuration (whatever layer it came from) plus the requested change into
/// the persisted group, so from then on the database — not the environment —
/// decides; clearing really disables live calls. Keys are stored encrypted,
/// never returned, and audited only as set / replaced / cleared / unchanged.
/// </summary>
public sealed class LlmSettingsService : ILlmSettingsService
{
    private readonly FactoryBrainDbContext _db;
    private readonly IAiSettingsProvider _settings;
    private readonly AiSecretProtector _protector;
    private readonly ILogger<LlmSettingsService> _log;

    public LlmSettingsService(
        FactoryBrainDbContext db, IAiSettingsProvider settings, AiSecretProtector protector, ILogger<LlmSettingsService> log)
    { _db = db; _settings = settings; _protector = protector; _log = log; }

    public async Task<LlmSettingsResponse> ReadStatusAsync(CancellationToken ct = default)
        => ToResponse(await _settings.GetAsync(ct));

    public async Task<SettingsWriteResult<LlmSettingsResponse>> UpdateAsync(
        LlmSettingsRequest req, AuditActor actor, string? traceId, CancellationToken ct = default)
    {
        var requestedProvider = string.IsNullOrWhiteSpace(req.Provider) ? null : req.Provider.Trim().ToLowerInvariant();
        if (requestedProvider is not null && !LlmSettings.SupportedProviders.Contains(requestedProvider))
            return SettingsWriteResult<LlmSettingsResponse>.Fail(SettingsWriteOutcome.Invalid,
                new Dictionary<string, string> { ["provider"] = "invalid_provider" });

        var newKey = req.ClearAll == true ? null : SecretValue.From(req.ApiKey);
        if (newKey is not null && !_protector.CanEncrypt)
            return SettingsWriteResult<LlmSettingsResponse>.Fail(SettingsWriteOutcome.EncryptionKeyMissing);

        // Fails closed (503) when the settings store can't be read.
        await _settings.GetAsync(ct);

        LlmSettingsResponse? committed = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var row = await AiSettingsRows.LockAsync(_db, ct);

            if (req.ExpectedVersion is { } expected && expected != row.Version)
            {
                await tx.RollbackAsync(ct);
                return SettingsWriteOutcome.Conflict;
            }

            // The group as it stands now: the saved one, or (never saved) the startup configuration.
            var saved = row.LlmUpdatedAt is not null;
            var beforeProvider = saved ? row.LlmProvider : _settings.Startup.Llm.Provider;
            var beforeModel    = saved ? row.LlmModel    : _settings.Startup.Llm.Model;
            var beforeBlob     = saved ? row.LlmApiKeyEncrypted : null;
            var startupKey     = saved ? null : _settings.Startup.Llm.ApiKey;
            var beforeHasKey   = beforeBlob is not null || startupKey is not null;
            var beforeLive     = LiveFor(beforeProvider, beforeModel, saved ? CanRead(beforeBlob) : startupKey is not null);

            string? nextProvider, nextModel;
            byte[]? nextBlob;
            string keyChange;

            if (req.ClearAll == true)
            {
                nextProvider = null; nextModel = null; nextBlob = null;
                keyChange = beforeHasKey ? "cleared" : "unchanged";
            }
            else
            {
                nextProvider = requestedProvider ?? beforeProvider ?? "openai";
                nextModel = !string.IsNullOrWhiteSpace(req.Model) ? req.Model.Trim() : beforeModel;
                var providerChanged = beforeProvider is not null && nextProvider != beforeProvider;

                if (newKey is not null)
                {
                    nextBlob = _protector.Protect(newKey.Reveal(), AiSecretProtector.LlmApiKeyPurpose);
                    keyChange = beforeHasKey ? "replaced" : "set";
                }
                else if (req.ClearKey == true)
                {
                    nextBlob = null;
                    keyChange = beforeHasKey ? "cleared" : "unchanged";
                }
                else if (providerChanged && beforeHasKey)
                {
                    // Never send one vendor's key to another vendor.
                    nextBlob = null;
                    keyChange = "cleared (provider changed)";
                }
                else if (startupKey is not null)
                {
                    // Saving makes the database authoritative, so the key in use
                    // (from the startup configuration) is carried over, encrypted.
                    if (!_protector.CanEncrypt)
                    {
                        await tx.RollbackAsync(ct);
                        return SettingsWriteOutcome.EncryptionKeyMissing;
                    }
                    nextBlob = _protector.Protect(startupKey.Reveal(), AiSecretProtector.LlmApiKeyPurpose);
                    keyChange = "unchanged (moved from startup configuration)";
                }
                else
                {
                    nextBlob = ReencryptIfRotated(beforeBlob);
                    keyChange = "unchanged";
                }
            }

            var now = DateTimeOffset.UtcNow;
            row.LlmProvider = nextProvider;
            row.LlmModel = nextModel;
            row.LlmApiKeyEncrypted = nextBlob;
            row.LlmUpdatedAt = now;
            AiSettingsRows.Touch(row, actor, now);

            var afterLive = LiveFor(nextProvider, nextModel, nextBlob is not null);
            var reason = string.Join("; ", new[]
            {
                Change("provider", beforeProvider, nextProvider),
                Change("model", beforeModel, nextModel),
                $"apiKey: {keyChange}",
                $"source: {(saved ? "database" : "startup_config")} → database",
                $"version: {row.Version}",
            }.Where(s => s is not null));
            _db.AuditLog.Add(AiSettingsRows.Audit(actor,
                req.ClearAll == true ? "settings.llm.cleared" : "settings.llm.updated",
                "settings", "llm", beforeLive ? "live" : "demo", afterLive ? "live" : "demo", reason, traceId, now));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _log.LogInformation("LLM settings saved by user {UserId}: version {Version}, mode {Mode}, apiKey {KeyChange}.",
                actor.UserId, row.Version, afterLive ? "live" : "demo", keyChange);

            // Describe exactly what this write committed (a fresh read could
            // already include a later writer's change, and the version is
            // what a client sends back as expectedVersion).
            var effective = new LlmSettings(nextProvider, nextModel,
                nextBlob is null ? null : new SecretValue("x"), SecretStatus.NotSet, AiSettingsSource.Database);
            committed = new LlmSettingsResponse(
                Configured: afterLive, Provider: nextProvider, Model: effective.EffectiveModel,
                Mode: afterLive ? "live" : "demo", Persistence: AiSettingsWire.Source(AiSettingsSource.Database),
                KeySet: nextBlob is not null, KeyStatus: AiSettingsWire.KeyStatus(nextBlob is null ? SecretStatus.NotSet : SecretStatus.Set),
                Version: row.Version, UpdatedAt: now, CanStoreKeys: _protector.CanEncrypt);
            return SettingsWriteOutcome.Ok;
        });

        _settings.Invalidate();
        return outcome == SettingsWriteOutcome.Ok
            ? SettingsWriteResult<LlmSettingsResponse>.Ok(committed!)
            : SettingsWriteResult<LlmSettingsResponse>.Fail(outcome);
    }

    private LlmSettingsResponse ToResponse(AiSettingsSnapshot s) => new(
        Configured: s.Llm.IsLive,
        Provider: s.Llm.Provider,
        Model: s.Llm.EffectiveModel,
        Mode: s.Llm.IsLive ? "live" : "demo",
        Persistence: AiSettingsWire.Source(s.Llm.Source),
        KeySet: s.Llm.ApiKey is not null,
        KeyStatus: AiSettingsWire.KeyStatus(s.Llm.KeyStatus),
        Version: s.Version,
        UpdatedAt: s.UpdatedAt,
        CanStoreKeys: _protector.CanEncrypt);

    private bool CanRead(byte[]? blob) => blob is not null && _protector.TryUnprotect(blob, AiSecretProtector.LlmApiKeyPurpose, out _);

    /// <summary>A blob still under the previous (rotation) key is re-encrypted with the primary key.</summary>
    private byte[]? ReencryptIfRotated(byte[]? blob)
    {
        if (blob is null || !_protector.CanEncrypt || !_protector.UsesPreviousKey(blob)) return blob;
        return _protector.TryUnprotect(blob, AiSecretProtector.LlmApiKeyPurpose, out var plain) && plain is not null
            ? _protector.Protect(plain, AiSecretProtector.LlmApiKeyPurpose)
            : blob;
    }

    private static bool LiveFor(string? provider, string? model, bool hasKey) =>
        new LlmSettings(provider, model, hasKey ? new SecretValue("x") : null, SecretStatus.NotSet, AiSettingsSource.Database).IsLive;

    private static string? Change(string field, string? before, string? after) =>
        before == after ? null : $"{field}: {before ?? "(none)"} → {after ?? "(none)"}";
}
