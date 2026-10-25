namespace FactoryBrain.Application.Settings;

/// <summary>Which layer an effective settings group came from.</summary>
public enum AiSettingsSource
{
    /// <summary>Built-in default (no LLM; local 384-d embeddings).</summary>
    Default,
    /// <summary>Environment captured once when the host started.</summary>
    StartupConfig,
    /// <summary>Saved through the settings API (the <c>ai_settings</c> row).</summary>
    Database,
    /// <summary>An <see cref="IAiSettingsProvider.BeginOverride"/> scope.</summary>
    RuntimeOverride,
}

/// <summary>State of a stored API key, safe to report.</summary>
public enum SecretStatus
{
    NotSet,
    Set,
    /// <summary>A ciphertext is stored but can't be decrypted (missing / rotated / wrong key, or tampered).</summary>
    Unreadable,
}

public static class AiSettingsWire
{
    public static string Source(AiSettingsSource s) => s switch
    {
        AiSettingsSource.Database        => "database",
        AiSettingsSource.StartupConfig   => "startup_config",
        AiSettingsSource.RuntimeOverride => "runtime_override",
        _                                => "none",
    };

    public static string KeyStatus(SecretStatus s) => s switch
    {
        SecretStatus.Set        => "set",
        SecretStatus.Unreadable => "unreadable",
        _                       => "not_set",
    };
}

/// <summary>One layer's LLM group. A layer either defines the whole group or none of it.</summary>
public sealed record LlmGroup(string? Provider, string? Model, SecretValue? ApiKey, SecretStatus KeyStatus = SecretStatus.NotSet)
{
    public static LlmGroup Empty { get; } = new(null, null, null);
}

/// <summary>One layer's embedding group. Null fields take the catalogue defaults.</summary>
public sealed record EmbeddingGroup(string? Provider, string? Model, int? Dimensions, SecretValue? ApiKey, SecretStatus KeyStatus = SecretStatus.NotSet)
{
    public static EmbeddingGroup Empty { get; } = new(null, null, null, null);
}

/// <summary>A scoped, never-persisted override. Null groups fall through to the next layer.</summary>
public sealed record AiSettingsOverride(LlmGroup? Llm = null, EmbeddingGroup? Embedding = null);

/// <summary>The persisted row as the resolver sees it. A null group has never been saved.</summary>
public sealed record PersistedAiSettings(
    LlmGroup? Llm,
    EmbeddingGroup? Embedding,
    long Version,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByEmail)
{
    public static PersistedAiSettings None { get; } = new(null, null, 0, null, null);
}

/// <summary>Effective LLM settings for one request.</summary>
public sealed record LlmSettings(string? Provider, string? Model, SecretValue? ApiKey, SecretStatus KeyStatus, AiSettingsSource Source)
{
    public static readonly IReadOnlySet<string> SupportedProviders = new HashSet<string> { "openai", "anthropic", "gemini" };

    /// <summary>Model used for OpenAI when none is configured (kept from the previous behaviour).</summary>
    public const string DefaultOpenAiModel = "gpt-6-luna";

    public string? EffectiveModel =>
        !string.IsNullOrWhiteSpace(Model) ? Model
        : Provider == "openai" ? DefaultOpenAiModel
        : null;

    /// <summary>True only when a live model call can actually be made.</summary>
    public bool IsLive =>
        Provider is not null && SupportedProviders.Contains(Provider)
        && ApiKey is not null
        && !string.IsNullOrWhiteSpace(EffectiveModel);
}

/// <summary>Effective embedding settings for one request (always fully resolved).</summary>
public sealed record EmbeddingSettings(string Provider, string Model, int Dimensions, SecretValue? ApiKey, SecretStatus KeyStatus, AiSettingsSource Source)
{
    public bool IsLocal => Provider == EmbeddingCatalog.Local;
    public bool IsHosted => EmbeddingCatalog.IsHosted(Provider);
}

public sealed record AiSettingsSnapshot(
    LlmSettings Llm,
    EmbeddingSettings Embedding,
    long Version,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByEmail);

/// <summary>
/// Settings read from the environment once, when the host is built. They
/// apply only to a group that has never been saved, and later environment
/// changes are not observed.
/// </summary>
public sealed record StartupAiConfig(
    LlmGroup Llm,
    EmbeddingGroup Embedding,
    string? LlmBaseUrl,
    double? EmbeddingPricePerMillionTokensUsd)
{
    public static StartupAiConfig Empty { get; } = new(LlmGroup.Empty, EmbeddingGroup.Empty, null, null);

    public static StartupAiConfig FromEnvironment(Func<string, string?> read)
    {
        static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        var llmKey = SecretValue.From(read("LLM_API_KEY"));
        var embKey = SecretValue.From(read("RAG_EMBEDDING_API_KEY"));
        int? dims = int.TryParse(Norm(read("RAG_EMBEDDING_DIMENSIONS")), out var d) && d > 0 ? d : null;
        double? price = double.TryParse(Norm(read("RAG_EMBEDDING_PRICE_PER_MILLION_TOKENS_USD")),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) && p >= 0 ? p : null;

        return new StartupAiConfig(
            new LlmGroup(Norm(read("LLM_PROVIDER"))?.ToLowerInvariant(), Norm(read("LLM_MODEL")), llmKey,
                llmKey is null ? SecretStatus.NotSet : SecretStatus.Set),
            new EmbeddingGroup(Norm(read("RAG_EMBEDDING_PROVIDER"))?.ToLowerInvariant(), Norm(read("RAG_EMBEDDING_MODEL")), dims, embKey,
                embKey is null ? SecretStatus.NotSet : SecretStatus.Set),
            Norm(read("LLM_BASE_URL")),
            price);
    }
}

/// <summary>
/// The precedence rule, as one pure function: for each group independently,
/// runtime override, then the database (once that group has been saved),
/// then the startup configuration, then defaults. The first layer that
/// defines a group supplies the whole group — fields are never mixed across
/// layers, so a key cleared in the database can't reappear from the
/// environment.
/// </summary>
public static class AiSettingsResolution
{
    public static AiSettingsSnapshot Resolve(AiSettingsOverride? runtime, PersistedAiSettings? persisted, StartupAiConfig startup)
    {
        persisted ??= PersistedAiSettings.None;

        var (llm, llmSource) =
            runtime?.Llm is { } ol ? (ol, AiSettingsSource.RuntimeOverride)
            : persisted.Llm is { } pl ? (pl, AiSettingsSource.Database)
            : HasAny(startup.Llm) ? (startup.Llm, AiSettingsSource.StartupConfig)
            : (LlmGroup.Empty, AiSettingsSource.Default);

        var (emb, embSource) =
            runtime?.Embedding is { } oe ? (oe, AiSettingsSource.RuntimeOverride)
            : persisted.Embedding is { } pe ? (pe, AiSettingsSource.Database)
            : HasAny(startup.Embedding) ? (startup.Embedding, AiSettingsSource.StartupConfig)
            : (EmbeddingGroup.Empty, AiSettingsSource.Default);

        var provider = string.IsNullOrWhiteSpace(llm.Provider) ? null : llm.Provider.Trim().ToLowerInvariant();
        var llmSettings = new LlmSettings(
            provider,
            string.IsNullOrWhiteSpace(llm.Model) ? null : llm.Model.Trim(),
            llm.ApiKey,
            llm.ApiKey is not null ? SecretStatus.Set : llm.KeyStatus == SecretStatus.Unreadable ? SecretStatus.Unreadable : SecretStatus.NotSet,
            llmSource);

        var (ep, em, ed, _) = EmbeddingCatalog.Normalize(emb.Provider, emb.Model, emb.Dimensions);
        var embSettings = new EmbeddingSettings(
            ep, em, ed > 0 ? ed : EmbeddingCatalog.LocalDefaultDimensions,
            emb.ApiKey,
            emb.ApiKey is not null ? SecretStatus.Set : emb.KeyStatus == SecretStatus.Unreadable ? SecretStatus.Unreadable : SecretStatus.NotSet,
            embSource);

        return new AiSettingsSnapshot(llmSettings, embSettings, persisted.Version, persisted.UpdatedAt, persisted.UpdatedByEmail);
    }

    private static bool HasAny(LlmGroup g) => g.Provider is not null || g.Model is not null || g.ApiKey is not null;
    private static bool HasAny(EmbeddingGroup g) => g.Provider is not null || g.Model is not null || g.Dimensions is not null || g.ApiKey is not null;
}

/// <summary>
/// The single source of LLM and embedding settings. Read by Ask, agents, the
/// embedding resolver, RAG status and the reindex planner. Never reads the
/// process environment after startup.
/// </summary>
public interface IAiSettingsProvider
{
    /// <summary>Settings for this request: refreshed from the database when the cached copy is older than the refresh interval.</summary>
    ValueTask<AiSettingsSnapshot> GetAsync(CancellationToken ct = default);

    /// <summary>Synchronous variant for code paths that can't await (refreshes synchronously when stale).</summary>
    AiSettingsSnapshot GetCurrent();

    /// <summary>Apply a never-persisted override to the current async flow until the returned scope is disposed.</summary>
    IDisposable BeginOverride(AiSettingsOverride settingsOverride);

    /// <summary>The startup configuration captured when the host was built.</summary>
    StartupAiConfig Startup { get; }

    /// <summary>Drop the cached row so the next read goes to the database (called after every write).</summary>
    void Invalidate();
}

/// <summary>The settings row could not be read; callers must not fall back to the environment.</summary>
public sealed class AiSettingsUnavailableException : Exception
{
    public AiSettingsUnavailableException(Exception inner)
        : base("AI settings could not be read from the database.", inner) { }
}
