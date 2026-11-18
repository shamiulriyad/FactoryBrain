using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BunonBrain.Application.Access;
using BunonBrain.Application.CameraAi;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;

namespace BunonBrain.Infrastructure.CameraAi;

/// <summary>A camera as camera AI needs it (in the caller's scope).</summary>
public sealed record CameraAiCamera(Guid Id, Guid FactoryId, string Code, string Name, string Status, string? SourceType);

public interface ICameraAiCameraLookup
{
    Task<CameraAiCamera?> FindInScopeAsync(AccessContext caller, Guid cameraId, CancellationToken ct);
}

/// <summary>Hands a camera AI event to the monitoring pipeline (validation, storage, alerts, SignalR).</summary>
public interface ICameraAiEventSink
{
    Task<CameraAiEventDto> EmitAsync(MonitoringEventInput input, CancellationToken ct);
}

public sealed record CameraAiUsageToday(int Calls, int FailedCalls, long InputTokens, long OutputTokens);

public interface ICameraAiUsageStore
{
    Task<CameraAiUsageToday> TodayAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Live camera AI sessions: a browser webcam on one camera, its frames
/// analysed by OpenAI at most every <c>MinIntervalMs</c>. Sessions live in
/// memory (the history is the monitoring events they raise). Limits that keep
/// the queue and the bill bounded:
/// <list type="bullet">
/// <item>one session per camera (the same user starting again replaces their old session);</item>
/// <item>one frame in flight per session (another one meanwhile → 409, the browser keeps only its latest frame);</item>
/// <item>a minimum interval between frames (429 + Retry-After), a global cap on concurrent OpenAI calls (503),
///       a daily call limit (429) and a maximum session length;</item>
/// <item>no frames for <c>OfflineAfter</c> → CAMERA_STATUS_CHANGED OFFLINE; for <c>IdleTimeout</c> → the session ends.</item>
/// </list>
/// </summary>
public sealed class CameraAiSessionService
{
    private const int RecentEventsKept = 20;
    private static readonly TimeSpan EndedRetention = TimeSpan.FromMinutes(10);
    /// <summary>Browsers' timers jitter: a frame up to this much early still counts as on time.</summary>
    private static readonly TimeSpan IntervalTolerance = TimeSpan.FromMilliseconds(100);

    private readonly CameraAiOptions _options;
    private readonly ICameraVisionAnalyzer _analyzer;
    private readonly ICameraAiCameraLookup _cameras;
    private readonly ICameraAiEventSink _sink;
    private readonly ICameraAiUsageStore _usage;
    private readonly TimeProvider _time;
    private readonly ILogger<CameraAiSessionService> _log;

    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();
    private readonly Dictionary<Guid, Guid> _byCamera = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _calls;
    private DateOnly _day;
    private int _dayCalls;

    public CameraAiSessionService(CameraAiOptions options, ICameraVisionAnalyzer analyzer, ICameraAiCameraLookup cameras, ICameraAiEventSink sink,
        ICameraAiUsageStore usage, TimeProvider time, ILogger<CameraAiSessionService> log)
    {
        _options = options; _analyzer = analyzer; _cameras = cameras; _sink = sink; _usage = usage; _time = time; _log = log;
        _calls = new SemaphoreSlim(options.MaxConcurrentCalls, options.MaxConcurrentCalls);
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    // ─── Start ─────────────────────────────────────────────────────────────

    public async Task<CameraAiResult<CameraAiSessionDto>> StartAsync(AccessContext caller, StartCameraAiSessionRequest req, CancellationToken ct)
    {
        var status = _analyzer.Status();
        if (!status.Configured) return CameraAiResult<CameraAiSessionDto>.Fail(503, "ai_not_configured", status.Reason ?? "Camera AI is not configured.");
        var cam = await _cameras.FindInScopeAsync(caller, req.CameraId, ct);
        if (cam is null) return CameraAiResult<CameraAiSessionDto>.Fail(404, "camera_not_found", "Camera not found.");
        if (cam.Status != "Active")
            return CameraAiResult<CameraAiSessionDto>.Fail(409, "camera_not_active", $"{cam.Code} is {cam.Status.ToLowerInvariant()}; activate it before analysing it.");
        if (cam.SourceType != "LocalWebcam")
            return CameraAiResult<CameraAiSessionDto>.Fail(409, "camera_not_browser_webcam",
                $"{cam.Code}'s video source is not a browser webcam. Set its source to “Local webcam” to analyse a browser webcam with AI.");
        if (!await HasDailyBudgetAsync(ct))
            return CameraAiResult<CameraAiSessionDto>.Fail(429, "daily_limit_reached",
                $"The daily camera AI limit ({_options.DailyCallLimit} analyses) is used up. It resets at midnight UTC.", SecondsToMidnight());

        var now = Now;
        var interval = Math.Clamp(req.IntervalMs ?? _options.DefaultIntervalMs, _options.MinIntervalMs, _options.MaxIntervalMs);
        LiveSession? replaced = null;
        LiveSession session;
        lock (_lock)
        {
            if (_byCamera.TryGetValue(cam.Id, out var existingId) && _sessions.TryGetValue(existingId, out var existing) && existing.EndedAt is null)
            {
                if (existing.UserId != caller.UserId)
                    return CameraAiResult<CameraAiSessionDto>.Fail(409, "camera_session_active",
                        $"{cam.Code} is already being analysed by {existing.Email} (since {existing.StartedAt:HH:mm} UTC). Stop it there or wait for it to end.");
                replaced = existing;
            }
            var active = _sessions.Values.Count(s => s.EndedAt is null && s != replaced);
            if (active >= _options.MaxSessions)
                return CameraAiResult<CameraAiSessionDto>.Fail(503, "too_many_sessions",
                    $"{active} cameras are already being analysed (limit {_options.MaxSessions}). Try again later.", 30);
            session = new LiveSession(Guid.NewGuid(), cam, caller.UserId, caller.Email, now, now + _options.MaxSessionDuration, interval,
                new CameraAiTemporalState(_options.Temporal));
            _sessions[session.Id] = session;
            _byCamera[cam.Id] = session.Id;
        }
        if (replaced is not null) await EndAsync(replaced, "replaced", "The same user started a new session for this camera.");
        _log.LogInformation("Camera AI session {Session} started on {Camera} by {User} (every {Interval} ms).", session.Id, cam.Code, caller.Email, interval);
        return CameraAiResult<CameraAiSessionDto>.Success(ToDto(session, caller), 201);
    }

    // ─── Frames ────────────────────────────────────────────────────────────

    public async Task<CameraAiResult<CameraAiFrameResult>> AnalyzeFrameAsync(AccessContext caller, Guid sessionId, byte[] frame, string? contentType, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var s) || s.EndedAt is not null)
            return CameraAiResult<CameraAiFrameResult>.Fail(404, "session_not_found", "This camera AI session has ended. Start the camera again.");
        if (s.UserId != caller.UserId)
            return CameraAiResult<CameraAiFrameResult>.Fail(403, "session_not_yours", "Only the person who started this session can send it frames.");
        if (Now >= s.ExpiresAt)
        {
            await EndAsync(s, "max_duration", $"The session reached its maximum length ({_options.MaxSessionDuration.TotalMinutes:0} minutes).");
            return CameraAiResult<CameraAiFrameResult>.Fail(409, "session_expired",
                $"The session reached its maximum length ({_options.MaxSessionDuration.TotalMinutes:0} minutes). Start the camera again to continue.");
        }
        var (w, h, badCode, badMessage) = CameraAiContract.CheckJpeg(frame, contentType, _options.MaxFrameBytes);
        if (badCode is not null)
        {
            Interlocked.Increment(ref s.Rejected);
            return CameraAiResult<CameraAiFrameResult>.Fail(badCode == "frame_too_large" ? 413 : 400, badCode, badMessage!);
        }
        // Latest frame wins: while one frame is analysed, newer ones are refused instead of queued.
        if (!s.Gate.Wait(0))
        {
            Interlocked.Increment(ref s.Rejected);
            return CameraAiResult<CameraAiFrameResult>.Fail(409, "analysis_in_progress", "The previous frame is still being analysed; send the newest frame after it finishes.");
        }
        try
        {
            var now = Now;
            if (s.LastAnalysisStartedAt is { } last && now - last < TimeSpan.FromMilliseconds(_options.MinIntervalMs) - IntervalTolerance)
            {
                Interlocked.Increment(ref s.Rejected);
                var wait = TimeSpan.FromMilliseconds(_options.MinIntervalMs) - (now - last);
                return CameraAiResult<CameraAiFrameResult>.Fail(429, "frame_too_soon",
                    $"Frames are analysed at most every {_options.MinIntervalMs} ms.", Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));
            }
            if (!await TryConsumeDailyAsync(ct))
            {
                Interlocked.Increment(ref s.Rejected);
                return CameraAiResult<CameraAiFrameResult>.Fail(429, "daily_limit_reached",
                    $"The daily camera AI limit ({_options.DailyCallLimit} analyses) is used up. It resets at midnight UTC.", SecondsToMidnight());
            }
            if (!_calls.Wait(0))
            {
                RefundDaily();
                Interlocked.Increment(ref s.Rejected);
                return CameraAiResult<CameraAiFrameResult>.Fail(503, "ai_busy", "The AI service is busy with other cameras; the next frame will be tried.", 1);
            }
            var events = new List<CameraAiEventDto>();
            CameraAiAnalysisDto? analysisDto = null;
            CameraAiErrorDto? error = null;
            try
            {
                s.LastAnalysisStartedAt = now;
                s.LastFrameAt = now;
                Interlocked.Increment(ref s.FramesReceived);
                events.AddRange(await ApplyAsync(s, t => t.OnFrame(), now));

                VisionAnalysis? analysis = null;
                string? failCode = null, failMessage = null;
                var started = Now;
                long latency;
                try
                {
                    var call = await _analyzer.AnalyzeAsync(frame, new VisionCallContext(s.FactoryId, s.Id, s.UserId), ct);
                    latency = call.DurationMs;
                    s.AddTokens(call.InputTokens, call.OutputTokens);
                    var (a, invalid) = CameraAiContract.Validate(call.Output);
                    if (a is null) { failCode = "invalid_output"; failMessage = $"The model's answer did not match the contract: {invalid}"; }
                    else analysis = a;
                }
                catch (CameraVisionException ex)
                {
                    latency = ex.DurationMs > 0 ? ex.DurationMs : (long)(Now - started).TotalMilliseconds;
                    s.AddTokens(ex.InputTokens, ex.OutputTokens);
                    failCode = ex.Code; failMessage = ex.Message;
                }
                var at = Now;
                s.RecordLatency((int)Math.Min(int.MaxValue, latency));
                if (analysis is not null)
                {
                    Interlocked.Increment(ref s.Analysed);
                    analysisDto = new CameraAiAnalysisDto(at, (int)latency, w, h, analysis.PersonPresence, analysis.PersonCount, analysis.SceneActivity,
                        analysis.Helmet, analysis.SafetyVest, analysis.People, analysis.Incident, analysis.ImageQuality, analysis.Notes, analysis.Adjustments);
                    s.LastAnalysis = analysisDto;
                    events.AddRange(await ApplyAsync(s, t => t.OnAnalysis(analysis), at));
                }
                else
                {
                    Interlocked.Increment(ref s.Failed);
                    error = new CameraAiErrorDto(failCode!, failMessage!, at);
                    s.LastError = error;
                    events.AddRange(await ApplyAsync(s, t => t.OnFailure(failCode!, failMessage!), at));
                }
            }
            finally { _calls.Release(); }
            return CameraAiResult<CameraAiFrameResult>.Success(
                new CameraAiFrameResult(analysisDto is not null ? "analysed" : "failed", analysisDto, error, events, ToDto(s, caller)));
        }
        finally { s.Gate.Release(); }
    }

    // ─── Read / stop ───────────────────────────────────────────────────────

    public CameraAiResult<CameraAiSessionDto> Get(AccessContext caller, Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s) || !CanSee(caller, s))
            return CameraAiResult<CameraAiSessionDto>.Fail(404, "session_not_found", "Camera AI session not found.");
        return CameraAiResult<CameraAiSessionDto>.Success(ToDto(s, caller));
    }

    public async Task<CameraAiResult<CameraAiSessionDto>> StopAsync(AccessContext caller, Guid sessionId, string? reason)
    {
        if (!_sessions.TryGetValue(sessionId, out var s) || !CanSee(caller, s))
            return CameraAiResult<CameraAiSessionDto>.Fail(404, "session_not_found", "Camera AI session not found.");
        if (s.UserId != caller.UserId && !(caller.Has(Permissions.CamerasManage) && caller.CanAccessFactory(s.FactoryId)))
            return CameraAiResult<CameraAiSessionDto>.Fail(403, "session_not_yours", "Only the person who started this session, or a camera manager of the factory, can stop it.");
        var note = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(200, reason.Trim().Length)];
        await EndAsync(s, s.UserId == caller.UserId ? "stopped" : "stopped_by_manager",
            note ?? (s.UserId == caller.UserId ? "The camera was stopped." : $"Stopped by {caller.Email}."));
        return CameraAiResult<CameraAiSessionDto>.Success(ToDto(s, caller));
    }

    public async Task<CameraAiOverviewDto> OverviewAsync(AccessContext caller, CancellationToken ct)
    {
        var status = _analyzer.Status();
        CameraAiUsageToday usage;
        try { usage = await _usage.TodayAsync(Now, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Camera AI usage could not be read.");
            usage = new CameraAiUsageToday(_dayCalls, 0, 0, 0);
        }
        var t = _options.Temporal;
        var limits = new CameraAiLimitsDto(_options.DefaultIntervalMs, _options.MinIntervalMs, _options.MaxIntervalMs, _options.MaxFrameBytes, _options.MaxSessions,
            (int)_options.MaxSessionDuration.TotalMinutes, (int)_options.OfflineAfter.TotalSeconds, (int)_options.IdleTimeout.TotalSeconds, _options.ImageDetail,
            t.PpeRequired, t.ConfirmPresent, t.ConfirmAbsent, t.ConfirmActivity, t.ConfirmPpe, t.FailuresBeforeUnavailable);
        var sessions = _sessions.Values.Where(s => CanSee(caller, s) && (s.EndedAt is null || s.UserId == caller.UserId))
            .OrderByDescending(s => s.StartedAt).Select(s => ToDto(s, caller)).ToList();
        var pricesConfigured = _options.PriceInputPer1M is not null || _options.PriceOutputPer1M is not null;
        return new CameraAiOverviewDto(status.Configured, status.Reason, "openai", status.Model, status.KeySource, limits,
            new CameraAiUsageDto(usage.Calls, usage.FailedCalls, usage.InputTokens, usage.OutputTokens,
                _options.EstimateCost(usage.InputTokens, usage.OutputTokens), _options.DailyCallLimit, pricesConfigured),
            sessions, CameraAiText.Disclaimer);
    }

    /// <summary>The watchdog's pass: offline / idle / expired sessions, then forget long-ended ones.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = Now;
        foreach (var s in _sessions.Values.ToList())
        {
            if (s.EndedAt is { } ended)
            {
                if (now - ended > EndedRetention) _sessions.TryRemove(s.Id, out _);
                continue;
            }
            var silent = now - (s.LastFrameAt ?? s.StartedAt);
            if (now >= s.ExpiresAt)
                await EndAsync(s, "max_duration", $"The session reached its maximum length ({_options.MaxSessionDuration.TotalMinutes:0} minutes).");
            else if (silent >= _options.IdleTimeout)
                await EndAsync(s, "idle", $"No frames for {_options.IdleTimeout.TotalSeconds:0} seconds.");
            else if (silent >= _options.OfflineAfter)
                await ApplyAsync(s, t => t.OnOffline(
                    $"No frames from the browser webcam for {silent.TotalSeconds:0} seconds (tab closed, camera stopped or connection lost)."), now);
        }
    }

    // ─── Internals ─────────────────────────────────────────────────────────

    private static bool CanSee(AccessContext caller, LiveSession s) =>
        s.UserId == caller.UserId || (caller.CanAccessFactory(s.FactoryId) && caller.HasAny(Permissions.MonitoringView, Permissions.CamerasView, Permissions.CamerasManage));

    private async Task EndAsync(LiveSession s, string code, string reason)
    {
        await s.State.WaitAsync();
        try
        {
            if (s.EndedAt is not null) return;
            s.EndedAt = Now;
            s.EndReason = code;
            lock (_lock)
            {
                if (_byCamera.TryGetValue(s.CameraId, out var id) && id == s.Id) _byCamera.Remove(s.CameraId);
            }
            await EmitAsync(s, s.Temporal.OnStop(reason), s.EndedAt.Value);
        }
        finally { s.State.Release(); }
        _log.LogInformation("Camera AI session {Session} on {Camera} ended: {Reason}.", s.Id, s.CameraCode, code);
    }

    /// <summary>Runs a temporal-rule step and emits its events, in order, under the session's state lock. Nothing happens after the session ended.</summary>
    private async Task<IReadOnlyList<CameraAiEventDto>> ApplyAsync(LiveSession s, Func<CameraAiTemporalState, IReadOnlyList<PlannedEvent>> step, DateTimeOffset at)
    {
        await s.State.WaitAsync();
        try
        {
            if (s.EndedAt is not null) return Array.Empty<CameraAiEventDto>();
            return await EmitAsync(s, step(s.Temporal), at);
        }
        finally { s.State.Release(); }
    }

    private async Task<IReadOnlyList<CameraAiEventDto>> EmitAsync(LiveSession s, IReadOnlyList<PlannedEvent> planned, DateTimeOffset at)
    {
        if (planned.Count == 0) return Array.Empty<CameraAiEventDto>();
        var results = new List<CameraAiEventDto>();
        foreach (var p in planned)
        {
            var payload = (JsonObject)p.Payload.DeepClone();
            payload["source"] = "openai-vision";
            payload["sessionId"] = s.Id.ToString();
            payload["estimate"] = true;
            payload["note"] = CameraAiText.EventNote;
            var track = p.EventType is MonitoringEventTypes.PersonDetected or MonitoringEventTypes.PersonLost ? s.TrackId : null;
            var input = new MonitoringEventInput(p.EventType, s.FactoryId, $"camera-ai:{s.Id:N}:{Interlocked.Increment(ref s.Sequence):D6}", at,
                MonitoringSourceTypes.Real, CameraId: s.CameraId, TrackId: track, Payload: ToElement(payload), ModelName: _options.Model);
            CameraAiEventDto r;
            try
            {
                // Events are kept even when the browser has gone: the request's cancellation is not passed on.
                r = await _sink.EmitAsync(input, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Camera AI event {Type} for session {Session} could not be stored.", p.EventType, s.Id);
                r = new CameraAiEventDto(null, p.EventType, null, at, false, "The event could not be stored.", null);
            }
            results.Add(r);
            s.AddEvent(r, RecentEventsKept);
        }
        return results;
    }

    /// <summary>A JsonObject as a JsonElement without reflection-based serialization (works with trimming / AOT).</summary>
    private static JsonElement ToElement(JsonObject o)
    {
        using var doc = JsonDocument.Parse(o.ToJsonString());
        return doc.RootElement.Clone();
    }

    private async Task<bool> HasDailyBudgetAsync(CancellationToken ct)
    {
        await EnsureDayAsync(ct);
        lock (_lock) return _dayCalls < _options.DailyCallLimit;
    }

    private async Task<bool> TryConsumeDailyAsync(CancellationToken ct)
    {
        await EnsureDayAsync(ct);
        lock (_lock)
        {
            if (_dayCalls >= _options.DailyCallLimit) return false;
            _dayCalls++;
            return true;
        }
    }

    private void RefundDaily() { lock (_lock) if (_dayCalls > 0) _dayCalls--; }

    /// <summary>On the first call of a UTC day, start from the calls already logged today (they survive restarts in ai_api_usage).</summary>
    private async Task EnsureDayAsync(CancellationToken ct)
    {
        var now = Now;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        lock (_lock) if (_day == today) return;
        var logged = 0;
        try { logged = (await _usage.TodayAsync(now, ct)).Calls; }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Camera AI usage could not be read; the daily count starts at 0."); }
        lock (_lock)
        {
            if (_day == today) return;
            _day = today;
            _dayCalls = logged;
        }
    }

    private int SecondsToMidnight()
    {
        var now = Now.UtcDateTime;
        return Math.Max(1, (int)Math.Ceiling((now.Date.AddDays(1) - now).TotalSeconds));
    }

    private CameraAiSessionDto ToDto(LiveSession s, AccessContext caller)
    {
        var t = s.Temporal;
        var status = s.EndedAt is not null ? "ENDED" : t.Offline ? "OFFLINE" : "ACTIVE";
        var analysed = Volatile.Read(ref s.Analysed);
        var failed = Volatile.Read(ref s.Failed);
        var stats = new CameraAiStatsDto(Volatile.Read(ref s.FramesReceived), analysed, failed, Volatile.Read(ref s.Rejected), s.LastLatencyMs, s.AvgLatencyMs,
            s.InputTokens, s.OutputTokens, _options.EstimateCost(s.InputTokens, s.OutputTokens));
        var confirmed = new CameraAiConfirmedDto(t.PersonPresent, t.Activity, t.PpeViolations.Order().ToList(), t.DetectionUnavailable, t.Offline);
        return new CameraAiSessionDto(s.Id, s.CameraId, s.CameraCode, s.CameraName, s.FactoryId, status, s.UserId == caller.UserId, s.Email,
            s.StartedAt, s.ExpiresAt, s.EndedAt, s.EndReason, s.LastFrameAt, s.IntervalMs, _options.Model, confirmed, stats, s.LastAnalysis, s.LastError,
            s.RecentEvents());
    }

    private sealed class LiveSession
    {
        public LiveSession(Guid id, CameraAiCamera cam, Guid userId, string email, DateTimeOffset startedAt, DateTimeOffset expiresAt, int intervalMs,
            CameraAiTemporalState temporal)
        {
            Id = id; CameraId = cam.Id; CameraCode = cam.Code; CameraName = cam.Name; FactoryId = cam.FactoryId; UserId = userId; Email = email;
            StartedAt = startedAt; ExpiresAt = expiresAt; IntervalMs = intervalMs; Temporal = temporal;
            TrackId = $"ai-{id:N}"[..15];
        }

        public Guid Id { get; }
        public Guid CameraId { get; }
        public string CameraCode { get; }
        public string CameraName { get; }
        public Guid FactoryId { get; }
        public Guid UserId { get; }
        public string Email { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public int IntervalMs { get; }
        /// <summary>Session-scoped presence id for PERSON_DETECTED / PERSON_LOST: "someone was in view during this session", never a person.</summary>
        public string TrackId { get; }
        public CameraAiTemporalState Temporal { get; }
        /// <summary>One frame in flight.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
        /// <summary>Temporal state and event emission.</summary>
        public SemaphoreSlim State { get; } = new(1, 1);

        public DateTimeOffset? LastFrameAt { get; set; }
        public DateTimeOffset? LastAnalysisStartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string? EndReason { get; set; }
        public CameraAiAnalysisDto? LastAnalysis { get; set; }
        public CameraAiErrorDto? LastError { get; set; }

        public int FramesReceived, Analysed, Failed, Rejected;
        public long Sequence;
        private long _inputTokens, _outputTokens, _latencySum;
        private int _latencyCount;
        public int? LastLatencyMs { get; private set; }
        public long InputTokens => Interlocked.Read(ref _inputTokens);
        public long OutputTokens => Interlocked.Read(ref _outputTokens);
        public int? AvgLatencyMs { get { lock (_events) return _latencyCount == 0 ? null : (int)(_latencySum / _latencyCount); } }

        private readonly LinkedList<CameraAiEventDto> _events = new();

        public void AddTokens(int? input, int? output)
        {
            if (input is { } i) Interlocked.Add(ref _inputTokens, i);
            if (output is { } o) Interlocked.Add(ref _outputTokens, o);
        }

        public void RecordLatency(int ms)
        {
            lock (_events) { _latencySum += ms; _latencyCount++; LastLatencyMs = ms; }
        }

        public void AddEvent(CameraAiEventDto e, int keep)
        {
            lock (_events)
            {
                _events.AddFirst(e);
                while (_events.Count > keep) _events.RemoveLast();
            }
        }

        public IReadOnlyList<CameraAiEventDto> RecentEvents() { lock (_events) return _events.ToList(); }
    }
}
