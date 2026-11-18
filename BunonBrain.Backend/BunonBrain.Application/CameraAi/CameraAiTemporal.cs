using System.Text.Json.Nodes;
using BunonBrain.Application.Monitoring;

namespace BunonBrain.Application.CameraAi;

/// <summary>An event the temporal rules decided to emit (the session adds camera, model and session context).</summary>
public sealed record PlannedEvent(string EventType, JsonObject Payload);

/// <summary>
/// Temporal confirmation for one camera AI session. A single frame never
/// raises an event: a change must hold for several consecutive conclusive
/// analyses (an unclear answer neither confirms nor clears), and each state
/// is reported once, on its transition, never per frame.
/// <list type="bullet">
/// <item>PERSON_DETECTED after <c>ConfirmPresent</c> "present"; PERSON_LOST after <c>ConfirmAbsent</c> "absent" (or when the session stops).</item>
/// <item>ACTIVITY_CHANGED when a new scene activity (not UNKNOWN) holds for <c>ConfirmActivity</c> analyses while a person is confirmed.</item>
/// <item>PPE_VIOLATION when a required item is "missing" for <c>ConfirmPpe</c> analyses in a row (unclear / unknown break the streak);
///       once per episode, which ends after <c>ConfirmPpe</c> "present" or when the person is lost.</item>
/// <item>DETECTION_UNAVAILABLE after <c>FailuresBeforeUnavailable</c> failed analyses in a row; MODEL_STATUS_CHANGED READY on the next success.</item>
/// <item>CAMERA_STATUS_CHANGED OFFLINE when frames stop; ONLINE when they resume.</item>
/// </list>
/// Not thread-safe: the session serialises calls.
/// </summary>
public sealed class CameraAiTemporalState
{
    private readonly TemporalSettings _s;
    private int _presentStreak, _absentStreak, _activityStreak, _failures;
    private string? _activityCandidate;
    private readonly Dictionary<string, (int Missing, int Clear)> _ppeStreaks = new();

    public CameraAiTemporalState(TemporalSettings settings)
    {
        _s = settings;
        foreach (var item in settings.PpeRequired) _ppeStreaks[item] = (0, 0);
    }

    public bool PersonPresent { get; private set; }
    public string? Activity { get; private set; }
    public IReadOnlySet<string> PpeViolations => _violations;
    private readonly HashSet<string> _violations = new();
    public bool DetectionUnavailable { get; private set; }
    public bool Offline { get; private set; }
    public int ConsecutiveFailures => _failures;

    public IReadOnlyList<PlannedEvent> OnAnalysis(VisionAnalysis a)
    {
        var events = new List<PlannedEvent>();
        _failures = 0;
        if (DetectionUnavailable)
        {
            DetectionUnavailable = false;
            events.Add(new PlannedEvent(MonitoringEventTypes.ModelStatusChanged,
                new JsonObject { ["toStatus"] = "READY", ["fromStatus"] = "ERROR", ["reason"] = "AI analysis is available again." }));
        }

        var presence = a.EffectivePresence;
        if (presence == PresenceStatus.Present) { _presentStreak++; _absentStreak = 0; }
        else if (presence == PresenceStatus.Absent) { _absentStreak++; _presentStreak = 0; }

        if (!PersonPresent && _presentStreak >= _s.ConfirmPresent)
        {
            PersonPresent = true;
            events.Add(new PlannedEvent(MonitoringEventTypes.PersonDetected, new JsonObject
            {
                ["personCount"] = a.PersonCount, ["confirmedAfterFrames"] = _s.ConfirmPresent,
            }));
        }
        else if (PersonPresent && _absentStreak >= _s.ConfirmAbsent)
        {
            events.AddRange(Lost("not_visible", $"Nobody visible in {_s.ConfirmAbsent} consecutive analyses."));
            return events;
        }

        // Activity and PPE evidence counts from the first frame a person is seen; events wait for confirmed presence.
        if (presence != PresenceStatus.Present) return events;

        // ── Activity ──
        if (a.SceneActivity != ActivityLabels.Unknown)
        {
            if (a.SceneActivity == _activityCandidate) _activityStreak++;
            else { _activityCandidate = a.SceneActivity; _activityStreak = 1; }
            if (PersonPresent && _activityStreak >= _s.ConfirmActivity && _activityCandidate != Activity)
            {
                var payload = new JsonObject { ["activity"] = _activityCandidate, ["confirmedAfterFrames"] = _s.ConfirmActivity };
                if (Activity is not null) payload["previousActivity"] = Activity;
                Activity = _activityCandidate;
                events.Add(new PlannedEvent(MonitoringEventTypes.ActivityChanged, payload));
            }
        }

        // ── PPE ──
        var newly = new List<string>();
        foreach (var item in _s.PpeRequired)
        {
            var status = a.PpeOf(item);
            var (missing, clear) = _ppeStreaks[item];
            if (status == PpeStatus.Missing) { missing++; clear = 0; }
            else if (status == PpeStatus.Present) { clear++; missing = 0; }
            else missing = 0;
            _ppeStreaks[item] = (missing, clear);
            if (PersonPresent && !_violations.Contains(item) && missing >= _s.ConfirmPpe) { _violations.Add(item); newly.Add(item); }
            else if (_violations.Contains(item) && clear >= _s.ConfirmPpe) _violations.Remove(item);
        }
        if (newly.Count > 0)
        {
            events.Add(new PlannedEvent(MonitoringEventTypes.PpeViolation, new JsonObject
            {
                ["missing"] = new JsonArray(newly.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["allMissing"] = new JsonArray(_violations.Order().Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["personCount"] = a.PersonCount, ["confirmedAfterFrames"] = _s.ConfirmPpe,
                ["reason"] = Describe(newly) + " not worn",
            }));
        }
        return events;
    }

    public IReadOnlyList<PlannedEvent> OnFailure(string code, string message)
    {
        _failures++;
        if (DetectionUnavailable || _failures < _s.FailuresBeforeUnavailable) return Array.Empty<PlannedEvent>();
        DetectionUnavailable = true;
        return new[]
        {
            new PlannedEvent(MonitoringEventTypes.DetectionUnavailable, new JsonObject
            {
                ["toStatus"] = "UNAVAILABLE", ["reason"] = message, ["errorCode"] = code, ["consecutiveFailures"] = _failures,
            }),
        };
    }

    /// <summary>A frame arrived: if the camera was reported offline, it is back.</summary>
    public IReadOnlyList<PlannedEvent> OnFrame()
    {
        if (!Offline) return Array.Empty<PlannedEvent>();
        Offline = false;
        return new[]
        {
            new PlannedEvent(MonitoringEventTypes.CameraStatusChanged,
                new JsonObject { ["toStatus"] = "ONLINE", ["fromStatus"] = "OFFLINE", ["reason"] = "Frames from the browser webcam resumed." }),
        };
    }

    public IReadOnlyList<PlannedEvent> OnOffline(string reason)
    {
        if (Offline) return Array.Empty<PlannedEvent>();
        Offline = true;
        return new[]
        {
            new PlannedEvent(MonitoringEventTypes.CameraStatusChanged,
                new JsonObject { ["toStatus"] = "OFFLINE", ["fromStatus"] = "ONLINE", ["reason"] = reason }),
        };
    }

    /// <summary>The session ends: a confirmed person is reported lost (stopping is not "camera offline").</summary>
    public IReadOnlyList<PlannedEvent> OnStop(string reason) =>
        PersonPresent ? Lost("session_ended", reason) : Array.Empty<PlannedEvent>();

    /// <summary>"helmet", "helmet and safety vest".</summary>
    public static string Describe(IEnumerable<string> items) =>
        string.Join(" and ", items.Select(i => i == PpeItems.Helmet ? "helmet" : i == PpeItems.Vest ? "safety vest" : i.ToLowerInvariant()));

    private IReadOnlyList<PlannedEvent> Lost(string code, string reason)
    {
        PersonPresent = false;
        Activity = null;
        _activityCandidate = null;
        _activityStreak = _presentStreak = _absentStreak = 0;
        _violations.Clear();
        foreach (var k in _ppeStreaks.Keys.ToList()) _ppeStreaks[k] = (0, 0);
        return new[] { new PlannedEvent(MonitoringEventTypes.PersonLost, new JsonObject { ["lostReason"] = code, ["reason"] = reason }) };
    }
}
