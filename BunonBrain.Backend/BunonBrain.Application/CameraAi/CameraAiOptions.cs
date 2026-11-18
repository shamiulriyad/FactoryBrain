using System.Globalization;

namespace BunonBrain.Application.CameraAi;

/// <summary>
/// Camera AI settings, from <c>CAMERA_AI_*</c> environment variables (see
/// CAMERA_AI.md). The OpenAI key comes from <c>CAMERA_AI_OPENAI_API_KEY</c>
/// or, when Settings → AI uses OpenAI, from that stored key. It never leaves
/// the server.
/// </summary>
public sealed record CameraAiOptions
{
    public bool Enabled { get; init; } = true;
    public string Model { get; init; } = "gpt-5.4-mini";
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    /// <summary>OpenAI image detail: low (cheapest, fixed token cost), high or auto.</summary>
    public string ImageDetail { get; init; } = "low";
    /// <summary>
    /// Sent as reasoning_effort when set. Not sent by default: gpt-5.4-mini's
    /// default is "none", the fastest and cheapest choice for one frame a second.
    /// </summary>
    public string? ReasoningEffort { get; init; }
    public int MaxOutputTokens { get; init; } = 1500;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Attempts per frame (retries only time-outs, rate limits and server errors).</summary>
    public int MaxAttempts { get; init; } = 2;

    /// <summary>Default time between analysed frames (1 frame per second).</summary>
    public int DefaultIntervalMs { get; init; } = 1000;
    /// <summary>The fastest a session may send frames; faster frames get 429.</summary>
    public int MinIntervalMs { get; init; } = 1000;
    public int MaxIntervalMs { get; init; } = 60_000;
    public int MaxFrameBytes { get; init; } = 600 * 1024;
    public int MaxSessions { get; init; } = 10;
    public int MaxConcurrentCalls { get; init; } = 4;
    /// <summary>OpenAI calls per UTC day for all cameras; protects the bill.</summary>
    public int DailyCallLimit { get; init; } = 5000;
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>No frame for this long while a session runs → CAMERA_STATUS_CHANGED OFFLINE (CAMERA_OFFLINE alert).</summary>
    public TimeSpan OfflineAfter { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>No frame for this long → the session ends.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TemporalSettings Temporal { get; init; } = new();

    public decimal? PriceInputPer1M { get; init; }
    public decimal? PriceOutputPer1M { get; init; }

    /// <summary>
    /// OpenAI's standard prices for gpt-5.4-mini (USD per 1M tokens, checked
    /// 2026-11-18 on developers.openai.com). Used only for that model when no
    /// CAMERA_AI_PRICE_* is set; other models show no cost until prices are set.
    /// </summary>
    public const decimal Gpt54MiniInputPer1M = 0.75m, Gpt54MiniOutputPer1M = 4.50m;

    public static CameraAiOptions FromEnvironment(Func<string, string?> env)
    {
        var d = new CameraAiOptions();
        string? S(string k) => string.IsNullOrWhiteSpace(env(k)) ? null : env(k)!.Trim();
        int I(string k, int def, int min, int max) => int.TryParse(S(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : def;
        decimal? M(string k) => decimal.TryParse(S(k), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
        var required = S("CAMERA_AI_PPE_REQUIRED") is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(PpeItems.Parse).OfType<string>().Distinct().ToArray()
            : d.Temporal.PpeRequired;
        var detail = S("CAMERA_AI_IMAGE_DETAIL")?.ToLowerInvariant();
        var minInterval = I("CAMERA_AI_MIN_INTERVAL_MS", d.MinIntervalMs, 250, 60_000);
        var model = S("CAMERA_AI_MODEL") ?? d.Model;
        decimal? priceIn = M("CAMERA_AI_PRICE_INPUT_PER_1M_USD"), priceOut = M("CAMERA_AI_PRICE_OUTPUT_PER_1M_USD");
        if (priceIn is null && priceOut is null && string.Equals(model, "gpt-5.4-mini", StringComparison.OrdinalIgnoreCase))
            (priceIn, priceOut) = (Gpt54MiniInputPer1M, Gpt54MiniOutputPer1M);
        return d with
        {
            Enabled = !string.Equals(S("CAMERA_AI_ENABLED"), "false", StringComparison.OrdinalIgnoreCase),
            Model = model,
            ApiKey = S("CAMERA_AI_OPENAI_API_KEY"),
            BaseUrl = S("CAMERA_AI_BASE_URL")?.TrimEnd('/'),
            ImageDetail = detail is "low" or "high" or "auto" ? detail : d.ImageDetail,
            ReasoningEffort = S("CAMERA_AI_REASONING_EFFORT")?.ToLowerInvariant(),
            MaxOutputTokens = I("CAMERA_AI_MAX_OUTPUT_TOKENS", d.MaxOutputTokens, 200, 8000),
            Timeout = TimeSpan.FromSeconds(I("CAMERA_AI_TIMEOUT_SECONDS", (int)d.Timeout.TotalSeconds, 3, 120)),
            MaxAttempts = I("CAMERA_AI_MAX_ATTEMPTS", d.MaxAttempts, 1, 4),
            MinIntervalMs = minInterval,
            DefaultIntervalMs = Math.Max(minInterval, I("CAMERA_AI_INTERVAL_MS", d.DefaultIntervalMs, 250, 60_000)),
            MaxFrameBytes = I("CAMERA_AI_MAX_FRAME_KB", d.MaxFrameBytes / 1024, 32, 4096) * 1024,
            MaxSessions = I("CAMERA_AI_MAX_SESSIONS", d.MaxSessions, 1, 200),
            MaxConcurrentCalls = I("CAMERA_AI_MAX_CONCURRENT", d.MaxConcurrentCalls, 1, 64),
            DailyCallLimit = I("CAMERA_AI_DAILY_CALL_LIMIT", d.DailyCallLimit, 1, 10_000_000),
            MaxSessionDuration = TimeSpan.FromMinutes(I("CAMERA_AI_MAX_SESSION_MINUTES", (int)d.MaxSessionDuration.TotalMinutes, 1, 24 * 60)),
            OfflineAfter = TimeSpan.FromSeconds(I("CAMERA_AI_OFFLINE_AFTER_SECONDS", (int)d.OfflineAfter.TotalSeconds, 5, 600)),
            IdleTimeout = TimeSpan.FromSeconds(I("CAMERA_AI_IDLE_TIMEOUT_SECONDS", (int)d.IdleTimeout.TotalSeconds, 10, 3600)),
            Temporal = d.Temporal with { PpeRequired = required },
            PriceInputPer1M = priceIn,
            PriceOutputPer1M = priceOut,
        };
    }

    /// <summary>Estimated cost in USD from token counts, or null when no prices are configured.</summary>
    public decimal? EstimateCost(long inputTokens, long outputTokens) =>
        PriceInputPer1M is null && PriceOutputPer1M is null
            ? null
            : Math.Round(inputTokens * (PriceInputPer1M ?? 0) / 1_000_000m + outputTokens * (PriceOutputPer1M ?? 0) / 1_000_000m, 6);
}

/// <summary>How many consecutive conclusive analyses confirm a change (temporal confirmation).</summary>
public sealed record TemporalSettings
{
    public int ConfirmPresent { get; init; } = 2;
    public int ConfirmAbsent { get; init; } = 3;
    public int ConfirmActivity { get; init; } = 3;
    public int ConfirmPpe { get; init; } = 3;
    public int FailuresBeforeUnavailable { get; init; } = 3;
    public string[] PpeRequired { get; init; } = { PpeItems.Helmet, PpeItems.Vest };
}
