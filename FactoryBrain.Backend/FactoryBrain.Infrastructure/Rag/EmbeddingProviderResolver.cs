using FactoryBrain.Application.Settings;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Snapshot of the embedding configuration resolved at one point in time.
/// Safe to return from API endpoints — no key material is ever stored on
/// this record. Only a boolean "is a key present" flag is exposed.
/// </summary>
public sealed record EmbeddingConfig(
    string Provider,
    string Model,
    int Dims,
    bool KeySet)
{
    public bool IsLocal   => string.Equals(Provider, "local",   StringComparison.OrdinalIgnoreCase);
    public bool IsHosted  => !IsLocal;
}

/// <summary>
/// Pair of <see cref="EmbeddingConfig"/> values used to keep "what the
/// settings ask for" separate from "what actually serves requests now".
///
/// <list type="bullet">
///   <item>
///     <see cref="Configured"/> is the effective embedding settings from
///     <see cref="IAiSettingsProvider"/> (saved settings, else the startup
///     configuration, else local/384). It is the "desired" state, compared
///     with the stored vectors to decide whether a reindex is due.
///   </item>
///   <item>
///     <see cref="Active"/> is the live embedder. When <see cref="Degraded"/>
///     is true, <see cref="Active"/> is the local hash embedder because the
///     configured provider has no key, or a probe or runtime call failed.
///   </item>
/// </list>
/// </summary>
public sealed record EmbeddingResolution(
    EmbeddingConfig Configured,
    EmbeddingConfig Active,
    bool Degraded);

/// <summary>
/// Dev-only test switches read on every probe / every build. They are
/// silently ignored (with one startup warning) when the host is not in
/// the Development environment.
/// </summary>
public sealed record DevEmbeddingSwitches(
    bool Enabled,
    bool ForceFail,
    int? FailAfter)
{
    public static DevEmbeddingSwitches Disabled { get; } = new(false, false, null);
}

/// <summary>
/// Decides which <see cref="IEmbeddingService"/> serves requests, from the
/// effective embedding settings (<see cref="IAiSettingsProvider"/>). It never
/// reads <c>LLM_*</c>: an LLM key does not change the embedding provider or
/// dimensions, and embeddings never borrow the LLM key.
///
/// Degraded state belongs to one exact configuration (provider, model,
/// dimensions, key). When the settings change, the new configuration starts
/// healthy (unless it is hosted without a key) and is probed on its own.
///
/// Failure modes that make a configuration degraded:
///   1. A hosted provider is configured without an embedding API key.
///   2. A probe or runtime embedding call fails, or returns the wrong length.
///   3. Building the provider's embedder throws.
///
/// Database errors never set Degraded — those are a different concern.
/// </summary>
public sealed class EmbeddingProviderResolver
{
    public const string DefaultProvider = "local";

    public const string EnvHealthInterval = "RAG_HEALTH_INTERVAL_SECONDS";
    public const string EnvFakeFail       = "RAG_EMBEDDING_FAKE_FAIL";
    public const string EnvFakeFailAfter  = "RAG_EMBEDDING_FAKE_FAIL_AFTER";

    private readonly IAiSettingsProvider _settings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<EmbeddingProviderResolver> _log;

    private readonly object _gate = new();
    private EmbeddingSettings? _degradedFor;
    private (EmbeddingSettings Settings, IEmbeddingService Service)? _built;

    private long _lastProbeTicks; // 0 = no probe yet
    private long _lastProbeOk;    // 1 = ok, 0 = fail

    // The "stub outside Development" warning is logged once per process.
    private static int _stubWarned;

    public EmbeddingProviderResolver(
        IAiSettingsProvider settings,
        IHostEnvironment env,
        ILogger<EmbeddingProviderResolver> log)
    {
        _settings = settings;
        _env = env;
        _log = log;
    }

    /// <summary>The effective embedding settings now (stub mapped to local outside Development).</summary>
    public EmbeddingSettings CurrentSettings() => Normalize(_settings.GetCurrent().Embedding);

    /// <summary>The resolved embedding configuration for the current settings.</summary>
    public EmbeddingResolution Resolve() => ResolveFor(CurrentSettings());

    public EmbeddingResolution ResolveFor(EmbeddingSettings s)
    {
        var configured = new EmbeddingConfig(s.Provider, s.Model, s.Dimensions, KeySetFor(s));
        bool degraded;
        lock (_gate) degraded = Equals(_degradedFor, s);
        if (configured.IsHosted && !configured.KeySet) degraded = true;
        var active = configured.IsLocal || !degraded
            ? configured
            : new EmbeddingConfig("local", "hash-md5", configured.Dims, false);
        return new EmbeddingResolution(configured, active, degraded && !configured.IsLocal);
    }

    /// <summary>True when Active has fallen back to local for the current settings.</summary>
    public bool IsDegraded => Resolve().Degraded;

    /// <summary>When the most recent probe ran (UTC), or null if none yet.</summary>
    public DateTime? LastProbeAtUtc =>
        Interlocked.Read(ref _lastProbeTicks) == 0
            ? null
            : new DateTime(Interlocked.Read(ref _lastProbeTicks), DateTimeKind.Utc);

    /// <summary>True if the most recent probe completed without error.</summary>
    public bool? LastProbeOk =>
        Interlocked.Read(ref _lastProbeTicks) == 0
            ? (bool?)null
            : Interlocked.Read(ref _lastProbeOk) == 1;

    /// <summary>
    /// Mark one configuration degraded after a hosted-call failure. Only the
    /// exception TYPE is logged: a provider message or URL could carry key
    /// material.
    /// </summary>
    public void MarkDegraded(EmbeddingSettings forSettings, string reason, Exception? ex = null)
    {
        lock (_gate)
        {
            if (Equals(_degradedFor, forSettings)) return;
            _degradedFor = forSettings;
        }
        _log.LogWarning(
            "Embedding provider degraded: {Reason} providerErrorType={ErrorType}. configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims}; activeProvider=local.",
            reason, ex?.GetType().Name ?? "unknown", forSettings.Provider, forSettings.Dimensions);
    }

    /// <summary>Mark the current configuration degraded (runtime failures).</summary>
    public void MarkDegraded(string reason, Exception? ex = null) => MarkDegraded(CurrentSettings(), reason, ex);

    private void ClearDegraded(EmbeddingSettings forSettings, string reason)
    {
        lock (_gate)
        {
            if (!Equals(_degradedFor, forSettings)) return;
            _degradedFor = null;
        }
        _log.LogInformation("Embedding provider recovered: {Reason}. provider={Provider} dims={Dims}.",
            reason, forSettings.Provider, forSettings.Dimensions);
    }

    /// <summary>
    /// Read the dev-only test switches. The values are scanned on every
    /// call so a probe can pick up a newly-set env var.
    /// </summary>
    public DevEmbeddingSwitches ReadDevSwitches()
    {
        if (!_env.IsDevelopment()) return DevEmbeddingSwitches.Disabled;

        var fakeFail = ReadEnv(EnvFakeFail) is { } v1
                       && (v1.Equals("true", StringComparison.OrdinalIgnoreCase)
                           || v1 == "1" || v1.Equals("yes", StringComparison.OrdinalIgnoreCase));
        int? failAfter = null;
        var rawAfter = ReadEnv(EnvFakeFailAfter);
        if (rawAfter is not null && int.TryParse(rawAfter, out var n) && n >= 0)
            failAfter = n;
        return new DevEmbeddingSwitches(true, fakeFail, failAfter);
    }

    /// <summary>
    /// One probe embedding call against the current configuration. The
    /// returned vector must have exactly the configured dimensions. Success
    /// clears Degraded for this configuration; failure sets it. Local is
    /// never degraded and makes no network call.
    /// </summary>
    public async Task<bool> ProbeAsync(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory, CancellationToken ct = default)
    {
        var settings = Normalize((await _settings.GetAsync(ct).ConfigureAwait(false)).Embedding);
        Interlocked.Exchange(ref _lastProbeTicks, DateTime.UtcNow.Ticks);
        bool ok;
        if (settings.IsLocal)
        {
            var arr = new HashEmbeddingService(settings.Dimensions).Embed("health-check-probe").ToArray();
            ok = arr.Length == settings.Dimensions;
        }
        else if (!KeySetFor(settings))
        {
            ok = false; // reported as degraded by ResolveFor; nothing to call
        }
        else
        {
            var (probeOk, actual, error) = await VerifyAsync(settings, httpFactory, loggerFactory, ct).ConfigureAwait(false);
            ok = probeOk;
            if (ok) ClearDegraded(settings, "probe succeeded");
            else if (error is not null) MarkDegraded(settings, $"probe failed: {error.GetType().Name}", error);
            else MarkDegraded(settings, $"probe vector length mismatch: expected {settings.Dimensions}, got {actual}");
        }
        Interlocked.Exchange(ref _lastProbeOk, ok ? 1L : 0L);
        return ok;
    }

    /// <summary>
    /// Make one embedding call with <paramref name="settings"/> (which need not
    /// be saved yet) and check that the output length equals the requested
    /// dimensions. Never throws for provider errors; returns them.
    /// </summary>
    public async Task<(bool Ok, int? ActualDims, Exception? Error)> VerifyAsync(
        EmbeddingSettings settings, IHttpClientFactory httpFactory, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var svc = BuildPrimary(settings, httpFactory, loggerFactory);
            var len = (await svc.EmbedAsync("health-check-probe", cts.Token).ConfigureAwait(false)).ToArray().Length;
            return (len == settings.Dimensions, len, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, null, ex);
        }
    }

    /// <summary>
    /// The embedder serving requests for the current settings. NEVER throws:
    /// a degraded or keyless hosted configuration gets the local hash
    /// embedder at the configured dimensions. Instances are reused while the
    /// settings stay the same.
    /// </summary>
    public IEmbeddingService BuildService(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        var settings = CurrentSettings();
        var resolution = ResolveFor(settings);
        if (resolution.Degraded) return new HashEmbeddingService(settings.Dimensions);

        lock (_gate)
        {
            if (_built is { } b && Equals(b.Settings, settings)) return b.Service;
        }
        IEmbeddingService svc;
        try
        {
            var primary = BuildPrimary(settings, httpFactory, loggerFactory);
            svc = primary is HashEmbeddingService ? primary : new FallbackEmbeddingService(this, settings, primary);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "Embedding provider build failed ({ErrorType}); falling back to local hash. configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims}.",
                ex.GetType().Name, settings.Provider, settings.Dimensions);
            MarkDegraded(settings, $"build failed for provider '{settings.Provider}': {ex.GetType().Name}", ex);
            return new HashEmbeddingService(settings.Dimensions);
        }
        lock (_gate) _built = (settings, svc);
        return svc;
    }

    /// <summary>
    /// Build the un-wrapped embedder for explicit settings (the current ones or
    /// a candidate under review). A hosted-call failure surfaces as an
    /// exception so the reindex can abort before any write.
    /// </summary>
    public IEmbeddingService BuildPrimary(EmbeddingSettings settings, IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        settings = Normalize(settings);
        return settings.Provider switch
        {
            EmbeddingCatalog.OpenAi => new OpenAiEmbeddingService(
                httpFactory, loggerFactory.CreateLogger<OpenAiEmbeddingService>(),
                KeyOrThrow(settings), settings.Model, settings.Dimensions),
            EmbeddingCatalog.Gemini => new GeminiEmbeddingService(
                httpFactory, loggerFactory.CreateLogger<GeminiEmbeddingService>(),
                KeyOrThrow(settings), settings.Model),
            EmbeddingCatalog.Stub => new StubEmbeddingService(settings.Dimensions),
            _ => new HashEmbeddingService(settings.Dimensions),
        };
    }

    private static bool KeySetFor(EmbeddingSettings s) =>
        !EmbeddingCatalog.IsHosted(s.Provider) || s.ApiKey is not null;

    private static string KeyOrThrow(EmbeddingSettings s) =>
        s.ApiKey?.Reveal() ?? throw new InvalidOperationException($"Embedding provider '{s.Provider}' requires an embedding API key.");

    /// <summary>The stub embedder is Development-only; elsewhere it is served as local.</summary>
    public EmbeddingSettings Normalize(EmbeddingSettings s)
    {
        if (s.Provider != EmbeddingCatalog.Stub || _env.IsDevelopment()) return s;
        if (Interlocked.Exchange(ref _stubWarned, 1) == 0)
            _log.LogWarning(
                "Embedding provider 'stub' ignored outside Development (ASPNETCORE_ENVIRONMENT={Env}); using the local hash embedder.",
                _env.EnvironmentName);
        return s with { Provider = EmbeddingCatalog.Local, Model = "hash-md5" };
    }

    private static string? ReadEnv(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}

/// <summary>
/// Decorates a hosted (or stub) embedder with a warning-and-fallback to the
/// local hash embedder on any failure, and marks that configuration
/// degraded. A later settings change gets a fresh embedder.
/// </summary>
internal sealed class FallbackEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingProviderResolver _resolver;
    private readonly EmbeddingSettings _settings;
    private readonly IEmbeddingService _primary;
    private readonly IEmbeddingService _fallback;
    private int _failed; // 0 = healthy, 1 = failed over

    public FallbackEmbeddingService(EmbeddingProviderResolver resolver, EmbeddingSettings settings, IEmbeddingService primary)
    {
        _resolver = resolver;
        _settings = settings;
        _primary = primary;
        _fallback = new HashEmbeddingService(primary.Dimensions);
    }

    public string ProviderId => _failed == 1 ? _fallback.ProviderId : _primary.ProviderId;
    public string ModelId    => _failed == 1 ? _fallback.ModelId    : _primary.ModelId;
    public int Dimensions     => _failed == 1 ? _fallback.Dimensions : _primary.Dimensions;

    public Pgvector.Vector Embed(string text, int dimensions = 0)
    {
        if (_failed == 1) return _fallback.Embed(text, dimensions);
        try
        {
            return _primary.Embed(text, dimensions);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _failed, 1);
            _resolver.MarkDegraded(_settings, $"runtime provider call failed ({_primary.ProviderId}/{_primary.ModelId})", ex);
            return _fallback.Embed(text, dimensions);
        }
    }

    public async Task<Pgvector.Vector> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (_failed == 1) return await _fallback.EmbedAsync(text, ct).ConfigureAwait(false);
        try
        {
            return await _primary.EmbedAsync(text, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _failed, 1);
            _resolver.MarkDegraded(_settings, $"runtime provider call failed ({_primary.ProviderId}/{_primary.ModelId})", ex);
            return await _fallback.EmbedAsync(text, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The application's <see cref="IEmbeddingService"/>: every call goes to the
/// embedder for the settings in effect at that moment, so a saved change
/// applies from the next request without a restart.
/// </summary>
public sealed class SettingsBoundEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingProviderResolver _resolver;
    private readonly IHttpClientFactory _http;
    private readonly ILoggerFactory _loggers;

    public SettingsBoundEmbeddingService(EmbeddingProviderResolver resolver, IHttpClientFactory http, ILoggerFactory loggers)
    { _resolver = resolver; _http = http; _loggers = loggers; }

    private IEmbeddingService Current => _resolver.BuildService(_http, _loggers);

    public string ProviderId => Current.ProviderId;
    public string ModelId    => Current.ModelId;
    public int Dimensions     => Current.Dimensions;
    public Pgvector.Vector Embed(string text, int dimensions = 0) => Current.Embed(text, dimensions);
    public Task<Pgvector.Vector> EmbedAsync(string text, CancellationToken ct = default) => Current.EmbedAsync(text, ct);
}
