namespace BunonBrain.Application.CameraAi;

/// <summary>A camera AI call's outcome with the HTTP status the API answers (429 / 503 carry Retry-After).</summary>
public sealed record CameraAiResult<T>(int Status, T? Value = default, string? Code = null, string? Message = null, int? RetryAfterSeconds = null)
{
    public bool Ok => Status is >= 200 and < 300;
    public static CameraAiResult<T> Success(T value, int status = 200) => new(status, value);
    public static CameraAiResult<T> Fail(int status, string code, string message, int? retryAfterSeconds = null) => new(status, default, code, message, retryAfterSeconds);
}

/// <summary>POST /api/monitoring/sessions. <c>IntervalMs</c>: time between analysed frames (default 1000 = 1 frame per second).</summary>
public sealed record StartCameraAiSessionRequest(Guid CameraId, int? IntervalMs = null);

public sealed record StopCameraAiSessionRequest(string? Reason = null);

public sealed record CameraAiStatsDto(
    int FramesReceived, int Analysed, int Failed, int Rejected, int? LastLatencyMs, int? AvgLatencyMs,
    long InputTokens, long OutputTokens, decimal? EstimatedCostUsd);

/// <summary>What the temporal rules have confirmed (the basis of events), as opposed to the latest single-frame estimate.</summary>
public sealed record CameraAiConfirmedDto(bool PersonPresent, string? Activity, IReadOnlyList<string> PpeViolations, bool DetectionUnavailable, bool Offline);

/// <summary>One frame's validated AI estimate. Boxes are approximate fractions of the frame; there are no numeric confidences.</summary>
public sealed record CameraAiAnalysisDto(
    DateTimeOffset At, int LatencyMs, int FrameWidth, int FrameHeight,
    string PersonPresence, int? PersonCount, string SceneActivity, string Helmet, string SafetyVest,
    IReadOnlyList<AiPerson> People, AiIncident Incident, string ImageQuality, string? Notes, IReadOnlyList<string> Adjustments,
    bool BoxesApproximate = true);

public sealed record CameraAiErrorDto(string Code, string Message, DateTimeOffset At);

/// <summary>An event the frame produced: stored (with its id) or, if the pipeline refused it, the reason.</summary>
public sealed record CameraAiEventDto(Guid? Id, string EventType, string? Summary, DateTimeOffset OccurredAt, bool Stored, string? Error, Guid? AlertId);

public sealed record CameraAiSessionDto(
    Guid Id, Guid CameraId, string CameraCode, string CameraName, Guid FactoryId,
    string Status,   // ACTIVE, OFFLINE (no frames lately) or ENDED
    bool IsMine, string StartedByEmail, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, DateTimeOffset? EndedAt, string? EndReason,
    DateTimeOffset? LastFrameAt, int IntervalMs, string Model,
    CameraAiConfirmedDto Confirmed, CameraAiStatsDto Stats, CameraAiAnalysisDto? LastAnalysis, CameraAiErrorDto? LastError,
    IReadOnlyList<CameraAiEventDto> RecentEvents);

/// <summary>POST …/frames: <c>Outcome</c> analysed (Analysis set) or failed (Error set); events are those this frame confirmed.</summary>
public sealed record CameraAiFrameResult(string Outcome, CameraAiAnalysisDto? Analysis, CameraAiErrorDto? Error, IReadOnlyList<CameraAiEventDto> Events, CameraAiSessionDto Session);

public sealed record CameraAiLimitsDto(
    int DefaultIntervalMs, int MinIntervalMs, int MaxIntervalMs, int MaxFrameBytes, int MaxSessions, int MaxSessionMinutes,
    int OfflineAfterSeconds, int IdleTimeoutSeconds, string ImageDetail, IReadOnlyList<string> PpeRequired,
    int ConfirmPresent, int ConfirmAbsent, int ConfirmActivity, int ConfirmPpe, int FailuresBeforeUnavailable);

public sealed record CameraAiUsageDto(int Calls, int FailedCalls, long InputTokens, long OutputTokens, decimal? EstimatedCostUsd, int DailyCallLimit, bool PricesConfigured);

public sealed record CameraAiOverviewDto(
    bool Configured, string? Reason, string Provider, string Model, string? KeySource,
    CameraAiLimitsDto Limits, CameraAiUsageDto UsageToday, IReadOnlyList<CameraAiSessionDto> Sessions, string Disclaimer);

public static class CameraAiText
{
    public const string Disclaimer =
        "AI estimates from still webcam frames analysed by OpenAI. They can be wrong, boxes are approximate, and nothing here identifies anyone. " +
        "Events and alerts are raised only after several consecutive frames agree; confirm them in person before acting.";

    public const string EventNote = "AI estimate from webcam frames analysed by OpenAI: not a confirmed fact and not an identity.";
}
