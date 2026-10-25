using FactoryBrain.Application.Settings;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Settings;

/// <summary>
/// Database-backed <see cref="IAiSettingsProvider"/>. The <c>ai_settings</c>
/// row is cached for <c>AI_SETTINGS_REFRESH_SECONDS</c> (default 1 s, 0 =
/// read on every call). A write on this instance calls <see cref="Invalidate"/>
/// so the next request here sees it; other instances see it once their cached
/// copy expires. A failed read throws <see cref="AiSettingsUnavailableException"/>
/// rather than falling back to the environment.
/// </summary>
public sealed class AiSettingsProvider : IAiSettingsProvider
{
    public const string EnvRefreshSeconds = "AI_SETTINGS_REFRESH_SECONDS";

    private readonly IServiceScopeFactory _scopes;
    private readonly AiSecretProtector _protector;
    private readonly ILogger<AiSettingsProvider> _log;
    private readonly long _refreshMs;
    private readonly AsyncLocal<AiSettingsOverride?> _override = new();

    private sealed record Cached(PersistedAiSettings Persisted, long LoadedAtMs, long Generation);
    private volatile Cached? _cached;
    private long _generation;
    private long _lastUnreadableWarning = -1;
    private int _missingTableWarned;

    public AiSettingsProvider(
        IServiceScopeFactory scopes,
        AiSecretProtector protector,
        StartupAiConfig startup,
        TimeSpan refreshInterval,
        ILogger<AiSettingsProvider> log)
    {
        _scopes = scopes;
        _protector = protector;
        Startup = startup;
        _refreshMs = (long)Math.Max(0, refreshInterval.TotalMilliseconds);
        _log = log;
    }

    public StartupAiConfig Startup { get; }

    /// <summary>Parse <c>AI_SETTINGS_REFRESH_SECONDS</c> (0–30, default 1).</summary>
    public static TimeSpan RefreshIntervalFrom(string? raw) =>
        int.TryParse(raw, out var s) && s is >= 0 and <= 30 ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(1);

    public async ValueTask<AiSettingsSnapshot> GetAsync(CancellationToken ct = default)
        => AiSettingsResolution.Resolve(_override.Value, await LoadAsync(ct).ConfigureAwait(false), Startup);

    public AiSettingsSnapshot GetCurrent()
        => AiSettingsResolution.Resolve(_override.Value, LoadAsync(CancellationToken.None, sync: true).GetAwaiter().GetResult(), Startup);

    public IDisposable BeginOverride(AiSettingsOverride settingsOverride)
    {
        var previous = _override.Value;
        _override.Value = settingsOverride;
        return new Restore(() => _override.Value = previous);
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _cached = null;
    }

    private async ValueTask<PersistedAiSettings> LoadAsync(CancellationToken ct, bool sync = false)
    {
        var now = Environment.TickCount64;
        var cached = _cached;
        if (cached is not null && _refreshMs > 0 && now - cached.LoadedAtMs < _refreshMs)
            return cached.Persisted;

        var generation = Interlocked.Read(ref _generation);
        AiSettingsRecord? row;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
            var query = db.AiSettings.AsNoTracking().Where(x => x.Id == AiSettingsRecord.SingletonId);
            row = sync ? query.FirstOrDefault() : await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table not created yet (migrations pending). Nothing has been
            // saved, so the startup configuration is the correct answer.
            if (Interlocked.Exchange(ref _missingTableWarned, 1) == 0)
                _log.LogWarning("ai_settings table not found; using startup configuration until migrations are applied.");
            return PersistedAiSettings.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("AI settings read failed ({ErrorType}); refusing to fall back to the environment.", ex.GetType().Name);
            throw new AiSettingsUnavailableException(ex);
        }

        var persisted = Map(row);
        // Only cache if no write landed while we were reading.
        if (Interlocked.Read(ref _generation) == generation)
            _cached = new Cached(persisted, Environment.TickCount64, generation);
        return persisted;
    }

    private PersistedAiSettings Map(AiSettingsRecord? r)
    {
        if (r is null) return PersistedAiSettings.None;

        LlmGroup? llm = null;
        if (r.LlmUpdatedAt is not null)
        {
            var (key, status) = Decrypt(r.LlmApiKeyEncrypted, AiSecretProtector.LlmApiKeyPurpose, "LLM", r.Version);
            llm = new LlmGroup(r.LlmProvider, r.LlmModel, key, status);
        }

        EmbeddingGroup? emb = null;
        if (r.EmbeddingUpdatedAt is not null)
        {
            var (key, status) = Decrypt(r.EmbeddingApiKeyEncrypted, AiSecretProtector.EmbeddingApiKeyPurpose, "embedding", r.Version);
            emb = new EmbeddingGroup(r.EmbeddingProvider, r.EmbeddingModel, r.EmbeddingDimensions, key, status);
        }

        return new PersistedAiSettings(llm, emb, r.Version, r.UpdatedAt, r.UpdatedByEmail);
    }

    private (SecretValue? Key, SecretStatus Status) Decrypt(byte[]? blob, string purpose, string label, long version)
    {
        if (blob is null) return (null, SecretStatus.NotSet);
        if (_protector.TryUnprotect(blob, purpose, out var plain) && SecretValue.From(plain) is { } key)
            return (key, SecretStatus.Set);
        // Once per settings version, so a broken key doesn't flood the log.
        if (Interlocked.Exchange(ref _lastUnreadableWarning, version) != version)
            _log.LogWarning(
                "The stored {Group} API key could not be decrypted (encryption key missing, rotated away, or the value was altered). It is treated as not set.",
                label);
        return (null, SecretStatus.Unreadable);
    }

    private sealed class Restore : IDisposable
    {
        private Action? _undo;
        public Restore(Action undo) { _undo = undo; }
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
