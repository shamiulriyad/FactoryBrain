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
/// Pair of <see cref="EmbeddingConfig"/> values used to keep "what the user
/// asked for" separate from "what actually serves requests this run".
///
/// <list type="bullet">
///   <item>
///     <see cref="Configured"/> is the raw env interpretation
///     (<c>RAG_EMBEDDING_PROVIDER</c> / <c>RAG_EMBEDDING_MODEL</c> /
///     <c>RAG_EMBEDDING_DIMENSIONS</c>, with the LLM_PROVIDER mirror rule
///     only applied when RAG_EMBEDDING_PROVIDER is unset). It is the
///     "desired" state — used to decide whether the stored vectors / column
///     size are stale and need a reindex + resize.
///   </item>
///   <item>
///     <see cref="Active"/> is the live embedder. When <see cref="Degraded"/>
///     is true, <see cref="Active"/> has been swapped to the local hash
///     embedder because either the configured provider's key was missing,
///     a probe embedding call failed, or a runtime call failed.
///   </item>
/// </list>
///
/// <see cref="Degraded"/> is the single source of truth for "do not run
/// startup resize/reindex" and "skip the vector score at query time".
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
/// Reads the embedding configuration from environment / appsettings and
/// decides which <see cref="IEmbeddingService"/> implementation should
/// serve requests. Keeps Configured (raw env) separate from Active (live).
///
/// Failure modes that flip the resolver into <c>Degraded</c>:
///   1. A hosted provider is configured but no key is set in
///      RAG_EMBEDDING_API_KEY / LLM_API_KEY. Caught at resolve time.
///   2. The provider's embedding call (probe, or runtime) fails — auth
///      error, HTTP error, timeout, wrong vector length, fake-fail.
///   3. <see cref="FallbackEmbeddingService"/> catches a hosted-call failure
///      at runtime and calls <see cref="MarkDegraded"/>.
///   4. Building the configured provider's embedder instance throws
///      (constructor error, missing dependency). Caught in
///      <see cref="BuildService"/> / <see cref="BuildPrimaryService"/>.
///
/// Database errors never set Degraded — those are a different concern.
/// </summary>
public sealed class EmbeddingProviderResolver
{
    public const string DefaultProvider = "local";
    public const string DefaultDims = "384";

    public const string EnvProvider       = "RAG_EMBEDDING_PROVIDER";
    public const string EnvModel          = "RAG_EMBEDDING_MODEL";
    public const string EnvDims           = "RAG_EMBEDDING_DIMENSIONS";
    public const string EnvApiKey         = "RAG_EMBEDDING_API_KEY";
    public const string EnvHealthInterval = "RAG_HEALTH_INTERVAL_SECONDS";
    public const string EnvFakeFail       = "RAG_EMBEDDING_FAKE_FAIL";
    public const string EnvFakeFailAfter  = "RAG_EMBEDDING_FAKE_FAIL_AFTER";

    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ILogger<EmbeddingProviderResolver> _log;
    private EmbeddingResolution? _resolved;

    // Probe-state, read by the status endpoint. We don't try to make
    // them strongly consistent — the worst case is a stale millisecond.
    private long _lastProbeTicks; // 0 = no probe yet
    private long _lastProbeOk;    // 1 = ok, 0 = fail

    // Spec (48h): the "stub embedding outside Development, treated as
    // local" warning is process-wide — the operator should see it
    // exactly once even if Resolve() / ReResolve() / Probe() all hit
    // ResolveCore() multiple times in the same lifetime. The flag is
    // process-wide (static) so a second resolver instance (tests, hot
    // reload) still respects "once".
    private static int _stubWarned; // 0 = not yet, 1 = already logged


    public EmbeddingProviderResolver(
        IConfiguration cfg,
        IHostEnvironment env,
        ILogger<EmbeddingProviderResolver> log)
    {
        _cfg = cfg;
        _env = env;
        _log = log;
    }

    /// <summary>The resolved embedding configuration (cached between probes).</summary>
    public EmbeddingResolution Resolve()
    {
        // Re-read .env.local on every resolution so a newly-saved key (or a
        // newly-set RAG_EMBEDDING_FAKE_FAIL=true) takes effect without a
        // restart. Cheap in Development (single small file), no-op elsewhere.
        EnvReloader.Reload(_log);
        if (_resolved is not null) return _resolved;
        _resolved = ResolveCore();
        return _resolved;
    }

    /// <summary>True when Active has fallen back to local — startup or runtime.</summary>
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
    /// Called by <see cref="FallbackEmbeddingService"/> after a hosted-call
    /// failure to flip the resolver into degraded mode. Idempotent. The
    /// "configured vs active" pair is updated so the next <see cref="Resolve"/>
    /// reflects the fall-back state.
    /// </summary>
    public void MarkDegraded(string reason, Exception? ex = null)
    {
        var previous = _resolved;
        if (previous is not null && previous.Degraded) return;

        var cfg = previous?.Configured ?? new EmbeddingConfig(DefaultProvider, "hash-md5", 384, false);
        var active = new EmbeddingConfig("local", "hash-md5", cfg.Dims, false);
        _resolved = new EmbeddingResolution(cfg, active, Degraded: true);

        // Spec (48f): never log the response body or any part of a key.
        // The exception reference argument would carry the provider's
        // HTTP failure message — including the URL for hosted GETs, which
        // for Gemini carries the key in the query string. We log only the
        // exception TYPE NAME (the "error type" the operator wants) and
        // the supplied reason; ex itself is dropped.
        _log.LogWarning(
            "Embedding provider degraded: {Reason} providerErrorType={ErrorType}. configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims}; activeProvider=local.",
            reason,
            ex?.GetType().Name ?? "unknown",
            cfg.Provider, cfg.Dims);
    }

    /// <summary>
    /// Clear degraded state after a successful probe against the configured
    /// provider. Re-runs a fresh resolution against the env so a newly-set
    /// RAG_EMBEDDING_* value can take effect immediately.
    /// </summary>
    public void ClearDegraded(string reason)
    {
        var previous = _resolved;
        if (previous is not null && !previous.Degraded) return;
        // Force a fresh resolve from env so any newly-set env vars take effect.
        var fresh = ResolveCore();
        if (!fresh.Degraded)
        {
            _resolved = fresh;
            _log.LogInformation(
                "Embedding provider recovered: {Reason}. configuredProvider={ConfiguredProvider}; activeProvider={ActiveProvider} dims={Dims}.",
                reason, fresh.Configured.Provider, fresh.Active.Provider, fresh.Active.Dims);
        }
    }

    /// <summary>
    /// Re-reads configuration from the environment (.env included) and
    /// rebuilds the resolution. Used by probes so that a recent
    /// RAG_EMBEDDING_API_KEY update is honoured without a restart.
    /// </summary>
    public void ReResolve()
    {
        EnvReloader.Reload(_log);
        var fresh = ResolveCore();
        // Preserve a runtime-flipped Degraded flag: a runtime call failure
        // already marked Degraded and a probe can't unset it (only ClearDegraded
        // can, after a successful probe).
        if (_resolved?.Degraded == true && !fresh.Degraded)
        {
            var active = new EmbeddingConfig("local", "hash-md5", fresh.Configured.Dims, false);
            _resolved = new EmbeddingResolution(fresh.Configured, active, Degraded: true);
        }
        else
        {
            _resolved = fresh;
        }
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
    /// Make one probe embedding call against the active hosted provider
    /// (when Active != local). The configured resolution is RE-READ from
    /// env first so any newly-set vars take effect. A successful probe
    /// returns true; a failure flips Degraded and returns false. The
    /// status timestamps are updated unconditionally.
    /// </summary>
    public async Task<bool> ProbeAsync(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory, CancellationToken ct = default)
    {
        // Always re-read env at the start of a probe.
        ReResolve();

        var switches = ReadDevSwitches();
        var resolution = _resolved ?? Resolve();
        var configured = resolution.Configured;
        var primary    = BuildPrimaryServiceInternal(httpFactory, loggerFactory, switches);
        var activeForProbe = configured.IsLocal
            ? BuildInternalFromConfiguredSafe(configured, httpFactory, loggerFactory, switches)
            : primary;
        var swatches = ReadDevSwitches(); // re-read in case env changed mid-call

        Interlocked.Exchange(ref _lastProbeTicks, DateTime.UtcNow.Ticks);
        bool ok;
        if (configured.IsLocal)
        {
            // Local hash provider can never be degraded. A successful
            // probe here is a no-op; a failing one is itself a bug — log
            // it loudly but keep IsDegraded false.
            try
            {
                var arr = (await activeForProbe.EmbedAsync("health-check-probe", ct).ConfigureAwait(false)).ToArray();
                ok = arr.Length == configured.Dims;
                if (!ok)
                    _log.LogWarning(
                        "Local hash probe returned a vector of unexpected length: expected={Expected} actual={Actual}.",
                        configured.Dims, arr.Length);
            }
            catch (Exception ex)
            {
                ok = false;
                _log.LogError(ex, "Local hash provider failed during probe — this is a bug; treated as degraded-free (DB error, no API key involvement).");
            }
        }
        else
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var arr = (await activeForProbe.EmbedAsync("health-check-probe", cts.Token).ConfigureAwait(false)).ToArray();
                if (arr.Length != configured.Dims)
                {
                    MarkDegraded($"probe vector length mismatch: expected {configured.Dims}, got {arr.Length}");
                    ok = false;
                }
                else
                {
                    ok = true;
                    ClearDegraded("probe succeeded");
                }
            }
            catch (Exception ex)
            {
                MarkDegraded($"probe failed: {ex.GetType().Name}", ex);
                ok = false;
            }
        }
        Interlocked.Exchange(ref _lastProbeOk, ok ? 1L : 0L);
        return ok;
    }

    private EmbeddingResolution ResolveCore()
    {
        // 1. Provider — explicit wins. Otherwise fall back to LLM_PROVIDER
        //    only when a key is present and matches the LLM provider; this
        //    lets a single LLM_API_KEY drive both chat and embeddings when
        //    the user didn't bother to set RAG_EMBEDDING_*.
        var rawProvider = ReadEnv(EnvProvider);
        var llmProvider  = ReadEnv("LLM_PROVIDER");
        var ragKey       = ReadEnv(EnvApiKey);
        var llmKey       = ReadEnv("LLM_API_KEY");

        string provider;
        string? apiKey = null;

        if (!string.IsNullOrWhiteSpace(rawProvider))
        {
            provider = rawProvider.Trim().ToLowerInvariant();
            apiKey = !string.IsNullOrWhiteSpace(ragKey)
                ? ragKey
                : (llmKey ?? string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(llmProvider) && !string.IsNullOrWhiteSpace(llmKey))
        {
            var llm = llmProvider.Trim().ToLowerInvariant();
            if (llm == "openai" || llm == "gemini")
            {
                provider = llm;
                apiKey = llmKey;
            }
            else
            {
                provider = DefaultProvider;
            }
        }
        else
        {
            provider = DefaultProvider;
        }

        // 1a. Stub is dev-only. Outside Development we silently swap it
        //     for the local hash embedder so an operator who set
        //     RAG_EMBEDDING_PROVIDER=stub in Production still gets a
        //     working system. One warning is logged below so the
        //     downgrade is visible in the startup log — but only ONCE
        //     per process (ResolveCore can be hit multiple times by
        //     ReResolve() / ProbeAsync() in a single lifetime).
        if (provider == "stub" && !_env.IsDevelopment())
        {
            if (Interlocked.Exchange(ref _stubWarned, 1) == 0)
            {
                _log.LogWarning(
                    "RAG_EMBEDDING_PROVIDER=stub ignored outside Development (ASPNETCORE_ENVIRONMENT={Env}); falling back to local hash embedder.",
                    _env.EnvironmentName);
            }
            provider = DefaultProvider;
        }

        // 2. Model — explicit wins, then a sensible default per provider.
        var rawModel = ReadEnv(EnvModel);
        string model = !string.IsNullOrWhiteSpace(rawModel)
            ? rawModel.Trim()
            : provider switch
            {
                "openai" => "text-embedding-3-small",
                "gemini" => "text-embedding-004",
                "stub"   => "stub-deterministic",
                _        => "hash-md5"
            };

        // 3. Dimensions — only RAG_EMBEDDING_DIMENSIONS env var may
        //    override the per-provider defaults.
        var rawDims = ReadEnv(EnvDims);
        int dims = int.TryParse(rawDims, out var d) ? d : 0;
        if (dims <= 0)
        {
            dims = provider switch
            {
                "openai" => model.Contains("3-large", StringComparison.OrdinalIgnoreCase) ? 3072 : 1536,
                "gemini" => 768,
                "stub"   => 768,
                _        => 384
            };
        }

        // KeySet is just a boolean — we never log any key material.
        bool keySet = !string.IsNullOrWhiteSpace(apiKey);

        // For "stub" provider, the dev switches take over the call site —
        // there's no real key required; we treat it as configured=set so
        // the resolution doesn't drop to local until a probe tells us to.
        if (provider == "stub") keySet = true;

        var configured = new EmbeddingConfig(provider, model, dims, keySet);

        // Active = what actually serves requests this run.
        EmbeddingConfig active;
        bool degraded = false;
        if (configured.IsLocal)
        {
            // Local can never be degraded, never trips a probe.
            active = configured;
        }
        else if (!keySet)
        {
            active = new EmbeddingConfig("local", "hash-md5", configured.Dims, false);
            degraded = true;
            _log.LogWarning(
                "RAG embedding degraded at resolve: RAG_EMBEDDING_PROVIDER={Provider} selected but apiKey=missing. Active provider is now local hash; configured provider unchanged.",
                configured.Provider);
        }
        else
        {
            active = configured;
        }

        return new EmbeddingResolution(configured, active, degraded);
    }

    /// <summary>
    /// Build the live (runtime) <see cref="IEmbeddingService"/>. NEVER
    /// throws — when the resolution is degraded, when the configured
    /// provider's key is missing, or when the configured provider's
    /// constructor throws, this falls back to the local hash embedder,
    /// logs a warning (no key fragments; apiKey=set|missing only), marks
    /// the resolver Degraded, and returns a working local embedder.
    /// </summary>
    public IEmbeddingService BuildService(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        var resolution = Resolve();
        var switches   = ReadDevSwitches();

        // Fast path: degraded or hosted-without-key → serve local.
        if (resolution.Degraded || (resolution.Configured.IsHosted && !resolution.Configured.KeySet))
        {
            return new HashEmbeddingService(resolution.Configured.Dims);
        }

        // Try to build the configured provider. ANY exception becomes a
        // degraded fallback to local — startup must never throw.
        try
        {
            var primary = BuildInternalFromConfiguredSafe(
                resolution.Configured, httpFactory, loggerFactory, switches);
            return WithFallback(primary, switches);
        }
        catch (Exception ex)
        {
            var cfg = resolution.Configured;
            _log.LogWarning(ex,
                "Embedding provider build failed; falling back to local hash. configuredProvider={ConfiguredProvider} configuredDims={ConfiguredDims} apiKey={ApiKey}.",
                cfg.Provider, cfg.Dims, cfg.KeySet ? "set" : "missing");
            MarkDegraded($"build failed for provider '{cfg.Provider}': {ex.GetType().Name}", ex);
            return new HashEmbeddingService(cfg.Dims);
        }
    }

    /// <summary>
    /// Build the un-wrapped PRIMARY embedder (no <see cref="FallbackEmbeddingService"/>
    /// decoration). Used at startup for the probe call and inside
    /// <see cref="EmbeddingColumnAdmin"/> so a hosted-call failure
    /// surfaces as a throw that the caller can roll back.
    ///
    /// <para>
    /// This method DOES propagate <see cref="InvalidOperationException"/>
    /// for missing-key cases so the reindex path can surface a clean
    /// <see cref="EmbeddingProviderUnavailableException"/> and the caller
    /// can decide to retry / defer. Callers that want the never-throw
    /// behaviour should use <see cref="BuildService"/>.
    /// </para>
    /// </summary>
    public IEmbeddingService BuildPrimaryService(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        var resolution = Resolve();
        var switches   = ReadDevSwitches();
        return BuildInternalFromConfigured(resolution.Configured, httpFactory, loggerFactory, switches);
    }

    private IEmbeddingService BuildPrimaryServiceInternal(
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        DevEmbeddingSwitches switches)
    {
        var resolution = Resolve();
        return BuildInternalFromConfigured(resolution.Configured, httpFactory, loggerFactory, switches);
    }

    /// <summary>
    /// Non-throwing variant of <see cref="BuildInternalFromConfigured"/>
    /// used at probe / startup time. A missing key returns a stub
    /// <see cref="HashEmbeddingService"/> at the configured dims and lets
    /// the caller flip Degraded itself (it has more context for the log
    /// line).
    /// </summary>
    private IEmbeddingService BuildInternalFromConfiguredSafe(
        EmbeddingConfig configured,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        DevEmbeddingSwitches switches)
    {
        // Local / stub don't need a key — go through the regular path.
        if (configured.IsLocal || configured.Provider == "stub")
        {
            return BuildInternalFromConfigured(configured, httpFactory, loggerFactory, switches);
        }
        if (!configured.KeySet)
        {
            return new HashEmbeddingService(configured.Dims);
        }
        return BuildInternalFromConfigured(configured, httpFactory, loggerFactory, switches);
    }

    private IEmbeddingService BuildInternalFromConfigured(
        EmbeddingConfig configured,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        DevEmbeddingSwitches switches)
    {
        return configured.Provider switch
        {
            "openai" => new OpenAiEmbeddingService(
                            httpFactory,
                            loggerFactory.CreateLogger<OpenAiEmbeddingService>(),
                            GetKeyOrThrow("openai"),
                            configured.Model),
            "gemini" => new GeminiEmbeddingService(
                            httpFactory,
                            loggerFactory.CreateLogger<GeminiEmbeddingService>(),
                            GetKeyOrThrow("gemini"),
                            configured.Model),
            "stub"   => new StubEmbeddingService(configured.Dims),
            _        => new HashEmbeddingService(configured.Dims)
        };
    }

    /// <summary>
    /// Wrap a hosted embedder (OpenAI / Gemini / stub) so that an HTTP or
    /// stub failure logs a warning (without the key), flips the resolver
    /// into Degraded, and falls back to the local hash embedder for the
    /// rest of the process lifetime.
    /// </summary>
    public IEmbeddingService WithFallback(IEmbeddingService primary, DevEmbeddingSwitches switches)
    {
        if (primary is HashEmbeddingService) return primary;
        return new FallbackEmbeddingService(this, primary, new HashEmbeddingService(primary.Dimensions), primary.GetType().Name);
    }

    private string GetKeyOrThrow(string provider)
    {
        var key = ReadEnv(EnvApiKey) ?? ReadEnv("LLM_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Embedding provider '{provider}' requires an API key.");
        return key;
    }

    private string? ReadEnv(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}

/// <summary>
/// Decorates a hosted (or stub) embedder with a warning-and-fallback to
/// the local hash embedder on any failure. The fallback is taken for the
/// remainder of the process lifetime so we don't keep hammering a broken
/// provider. On failure it also flips the resolver into Degraded mode.
/// </summary>
internal sealed class FallbackEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingProviderResolver _resolver;
    private readonly IEmbeddingService _primary;
    private readonly IEmbeddingService _fallback;
    private readonly ILogger _log;
    private readonly string _primaryLabel;
    private int _failed; // 0 = healthy, 1 = permanently failed-over

    public FallbackEmbeddingService(
        EmbeddingProviderResolver resolver,
        IEmbeddingService primary,
        IEmbeddingService fallback,
        string primaryLabel)
    {
        _resolver = resolver;
        _primary = primary;
        _fallback = fallback;
        _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _primaryLabel = primaryLabel;
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
            _resolver.MarkDegraded($"runtime provider call failed ({_primary.ProviderId}/{_primary.ModelId})", ex);
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
            _resolver.MarkDegraded($"runtime provider call failed ({_primary.ProviderId}/{_primary.ModelId})", ex);
            return await _fallback.EmbedAsync(text, ct).ConfigureAwait(false);
        }
    }
}