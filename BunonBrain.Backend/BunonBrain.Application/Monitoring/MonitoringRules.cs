using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BunonBrain.Application.Monitoring;

// The monitoring event contract and the rules around it. Everything here is
// pure (no database), so the same checks run for the HTTP ingestion API, the
// simulator, the device pipeline and unit tests.

/// <summary>Event types of the monitoring contract (upper snake case on the wire).</summary>
public static class MonitoringEventTypes
{
    public const string PersonDetected = "PERSON_DETECTED";
    public const string TrackUpdated = "TRACK_UPDATED";
    public const string PersonLost = "PERSON_LOST";
    public const string ZoneEntered = "ZONE_ENTERED";
    public const string ZoneExited = "ZONE_EXITED";
    public const string ActivityClassified = "ACTIVITY_CLASSIFIED";
    public const string UncertainDetection = "UNCERTAIN_DETECTION";
    public const string CameraStatusChanged = "CAMERA_STATUS_CHANGED";
    public const string DeviceStatusChanged = "DEVICE_STATUS_CHANGED";
    public const string ModelStatusChanged = "MODEL_STATUS_CHANGED";

    /// <summary>People, tracks, zones and activity: what a camera AI service would report.</summary>
    public static readonly string[] Vision =
        { PersonDetected, TrackUpdated, PersonLost, ZoneEntered, ZoneExited, ActivityClassified, UncertainDetection };

    /// <summary>Camera, device and model status.</summary>
    public static readonly string[] System = { CameraStatusChanged, DeviceStatusChanged, ModelStatusChanged };

    public static readonly string[] All = Vision.Concat(System).ToArray();

    public static string? Parse(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var key = v.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        return All.FirstOrDefault(t => t == key);
    }

    public static string CategoryOf(string type) => Vision.Contains(type) ? MonitoringCategories.Vision : MonitoringCategories.System;

    public static string Label(string type) => type switch
    {
        PersonDetected => "Person detected",
        TrackUpdated => "Track updated",
        PersonLost => "Person lost",
        ZoneEntered => "Zone entered",
        ZoneExited => "Zone exited",
        ActivityClassified => "Activity classified",
        UncertainDetection => "Uncertain detection",
        CameraStatusChanged => "Camera status changed",
        DeviceStatusChanged => "Device status changed",
        ModelStatusChanged => "Model status changed",
        _ => type,
    };
}

/// <summary>VISION events concern people seen by cameras and need <c>monitoring.view</c>; SYSTEM events concern equipment.</summary>
public static class MonitoringCategories
{
    public const string Vision = "VISION";
    public const string System = "SYSTEM";
    public static readonly string[] All = { Vision, System };
    public static string? Parse(string? v) => All.FirstOrDefault(c => string.Equals(c, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class MonitoringSourceTypes
{
    public const string Simulated = "SIMULATED";
    public const string Real = "REAL";
    public static readonly string[] All = { Simulated, Real };
    public static string? Parse(string? v) => All.FirstOrDefault(c => string.Equals(c, v?.Trim(), StringComparison.OrdinalIgnoreCase));
    public static string Of(bool simulated) => simulated ? Simulated : Real;
}

/// <summary>Who handed an event to the pipeline.</summary>
public static class MonitoringProducers
{
    /// <summary>POST /api/monitoring/events: real producers (gateways, the future Python AI service). REAL only.</summary>
    public const string Api = "API";
    /// <summary>The monitoring simulator. SIMULATED only.</summary>
    public const string Simulator = "SIMULATOR";
    /// <summary>Device connection, error and status changes (real or simulated devices).</summary>
    public const string DevicePipeline = "DEVICE_PIPELINE";
    /// <summary>Administrative camera status changes.</summary>
    public const string CameraService = "CAMERA_SERVICE";
    /// <summary>The computer-vision service (service key). REAL only; vision, camera and model events; never a worker identity.</summary>
    public const string CvService = "CV_SERVICE";
    public static readonly string[] All = { Api, Simulator, DevicePipeline, CameraService, CvService };

    /// <summary>Producers that send events through the HTTP ingestion API: REAL only, idempotency key required.</summary>
    public static bool IsExternal(string producer) => producer is Api or CvService;

    /// <summary>What the computer-vision service may report.</summary>
    public static readonly string[] CvEventTypes =
    {
        MonitoringEventTypes.PersonDetected, MonitoringEventTypes.TrackUpdated, MonitoringEventTypes.PersonLost,
        MonitoringEventTypes.ZoneEntered, MonitoringEventTypes.ZoneExited, MonitoringEventTypes.UncertainDetection,
        MonitoringEventTypes.CameraStatusChanged, MonitoringEventTypes.ModelStatusChanged,
    };
}

public static class MonitoringEventSeverities
{
    public const string Info = "INFO";
    public const string Warning = "WARNING";
    public const string Critical = "CRITICAL";
    public static readonly string[] All = { Info, Warning, Critical };
}

/// <summary>
/// How a worker identity on an event was verified. Only methods the server
/// can check are accepted; anything based on how a person looks is refused.
/// </summary>
public static class IdentityMethods
{
    /// <summary>An active RFID card of the worker in this factory, looked up by the server.</summary>
    public const string RfidBadge = "RFID_BADGE";
    public static readonly string[] Verifiable = { RfidBadge };

    /// <summary>Recognised on sight: never enough to name a worker.</summary>
    public static readonly string[] Visual =
        { "VISUAL_SIMILARITY", "FACE_RECOGNITION", "FACE_MATCH", "RE_ID", "REID", "APPEARANCE", "GAIT", "BODY_SHAPE", "CLOTHING" };

    public static string Normalise(string? v) => (v ?? "").Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
}

public static class AlertStatuses
{
    public const string Open = "OPEN";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Escalated = "ESCALATED";
    public const string Resolved = "RESOLVED";
    public const string Dismissed = "DISMISSED";
    public static readonly string[] All = { Open, Acknowledged, Escalated, Resolved, Dismissed };

    /// <summary>Still needs attention.</summary>
    public static readonly string[] Active = { Open, Acknowledged, Escalated };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
    public static bool IsActive(string status) => Active.Contains(status);
}

public static class AlertSeverities
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Critical = "CRITICAL";
    public static readonly string[] All = { Low, Medium, High, Critical };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
    public static int Rank(string? s) => s switch { Critical => 4, High => 3, Medium => 2, Low => 1, _ => 0 };
}

public static class AlertTypes
{
    public const string CameraOffline = "CAMERA_OFFLINE";
    public const string DeviceOffline = "DEVICE_OFFLINE";
    public const string DeviceError = "DEVICE_ERROR";
    public const string ModelUnavailable = "MODEL_UNAVAILABLE";
    public const string UncertainDetection = "UNCERTAIN_DETECTION_REVIEW";
    public const string RestrictedZoneEntry = "RESTRICTED_ZONE_ENTRY";
    public static readonly string[] All = { CameraOffline, DeviceOffline, DeviceError, ModelUnavailable, UncertainDetection, RestrictedZoneEntry };

    public static string CategoryOf(string type) =>
        type is UncertainDetection or RestrictedZoneEntry ? MonitoringCategories.Vision : MonitoringCategories.System;
}

/// <summary>History entries of an alert.</summary>
public static class AlertActions
{
    public const string Created = "CREATED";
    public const string Repeated = "REPEATED";
    public const string SourceRecovered = "SOURCE_RECOVERED";
    public const string Acknowledge = "ACKNOWLEDGED";
    public const string Escalate = "ESCALATED";
    public const string Resolve = "RESOLVED";
    public const string Dismiss = "DISMISSED";
}

public static class AlertResolutionCodes
{
    public const string IssueFixed = "ISSUE_FIXED";
    public const string FalseAlarm = "FALSE_ALARM";
    public const string NoActionNeeded = "NO_ACTION_NEEDED";
    public const string Other = "OTHER";
    public static readonly string[] All = { IssueFixed, FalseAlarm, NoActionNeeded, Other };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Who an alert can be escalated to (role ids from the access catalog).</summary>
public static class AlertEscalationTargets
{
    public static readonly string[] All = { "ProductionSupervisor", "FactoryManager", "FactoryAdmin", "DeviceAdmin" };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
    public const int MaxLevel = 5;
}

public static class EventReviewDecisions
{
    public const string Confirmed = "CONFIRMED";
    public const string FalsePositive = "FALSE_POSITIVE";
    public const string StillUncertain = "STILL_UNCERTAIN";
    public const string NeedsFollowUp = "NEEDS_FOLLOW_UP";
    public static readonly string[] All = { Confirmed, FalsePositive, StillUncertain, NeedsFollowUp };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class SimulationStatuses
{
    public const string Running = "RUNNING";
    public const string Stopped = "STOPPED";
    public const string Completed = "COMPLETED";
}

/// <summary>Status values carried in <c>payload.toStatus</c> of the *_STATUS_CHANGED events.</summary>
public static class StatusValues
{
    public static readonly string[] Camera = { "ONLINE", "OFFLINE", "ACTIVE", "INACTIVE", "MAINTENANCE" };
    public static readonly string[] Device = { "ONLINE", "OFFLINE", "ERROR", "ACTIVE", "MAINTENANCE", "DISABLED" };
    public static readonly string[] Model = { "LOADING", "READY", "DEGRADED", "ERROR", "OFFLINE" };

    public static string[] For(string type) => type switch
    {
        MonitoringEventTypes.CameraStatusChanged => Camera,
        MonitoringEventTypes.DeviceStatusChanged => Device,
        MonitoringEventTypes.ModelStatusChanged => Model,
        _ => Array.Empty<string>(),
    };
}

/// <summary>An alert transition: the new status, or why it isn't allowed.</summary>
public sealed record AlertTransition(bool Allowed, string? ToStatus, string? Code, string? Message)
{
    public static AlertTransition To(string status) => new(true, status, null, null);
    public static AlertTransition No(string code, string message) => new(false, null, code, message);
}

/// <summary>What an event needs, for one event type.</summary>
public sealed record EventRequirements(bool Camera, bool Device, bool Zone, bool Track, bool Confidence, bool ToStatus, bool Model, bool Activity, bool WorkerAllowed);

/// <summary>Facts about an event an alert rule looks at.</summary>
public sealed record AlertContext(
    string EventType, bool Simulated, string? ToStatus, string? FromStatus,
    Guid? CameraId, string? CameraCode, Guid? DeviceId, string? DeviceCode,
    Guid? ZoneId, string? ZoneName, string? ZoneType, string? TrackId, double? Confidence,
    string? ModelName, string? Detail);

/// <summary>An alert to raise (or to update, when one with the same dedup key is still open).</summary>
public sealed record AlertSpec(string AlertType, string Severity, string Title, string Description, string DedupKey);

public static class MonitoringRules
{
    public const int IdempotencyKeyMax = 128;
    public const int TrackIdMax = 64;
    public const int PayloadMaxBytes = 8 * 1024;
    public const int PayloadMaxDepth = 5;
    public const int NoteMax = 1000;
    public const int ModelNameMax = 80;
    public const int CardUidMax = 64;
    public const int SummaryMax = 300;
    public const int MaxPageSize = 200;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxBackdate = TimeSpan.FromDays(7);

    public const string SimulatedNote = "Generated by the monitoring simulator. Not a camera AI result.";

    private static readonly Regex KeyPattern = new("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex TrackPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$", RegexOptions.Compiled);

    public static bool IsIdempotencyKey(string? v) => v is not null && KeyPattern.IsMatch(v);
    public static bool IsTrackId(string? v) => v is not null && TrackPattern.IsMatch(v);

    public static EventRequirements Requirements(string type) => type switch
    {
        MonitoringEventTypes.PersonDetected => new(true, false, false, false, false, false, false, false, true),
        MonitoringEventTypes.TrackUpdated => new(true, false, false, true, false, false, false, false, true),
        MonitoringEventTypes.PersonLost => new(true, false, false, true, false, false, false, false, true),
        MonitoringEventTypes.ZoneEntered => new(true, false, true, true, false, false, false, false, true),
        MonitoringEventTypes.ZoneExited => new(true, false, true, true, false, false, false, false, true),
        MonitoringEventTypes.ActivityClassified => new(true, false, false, true, true, false, false, true, true),
        // Uncertain stays uncertain: it can never name a worker.
        MonitoringEventTypes.UncertainDetection => new(true, false, false, false, true, false, false, false, false),
        MonitoringEventTypes.CameraStatusChanged => new(true, false, false, false, false, true, false, false, false),
        MonitoringEventTypes.DeviceStatusChanged => new(false, true, false, false, false, true, false, false, false),
        MonitoringEventTypes.ModelStatusChanged => new(false, false, false, false, false, true, true, false, false),
        _ => new(false, false, false, false, false, false, false, false, false),
    };

    // ─── Payload ───────────────────────────────────────────────────────────

    /// <summary>Keys that would smuggle a secret or an unverified identity into the payload.</summary>
    private static readonly string[] ForbiddenKeyParts =
        { "password", "passwd", "secret", "token", "apikey", "api_key", "credential", "authorization" };
    private static readonly string[] ForbiddenIdentityKeys =
        { "workerid", "worker_id", "employeeid", "employee_id", "employeecode", "workercode", "worker_code", "faceid", "face_id",
          "faceembedding", "face_embedding", "embedding", "personname", "person_name", "workername", "worker_name" };

    /// <summary>
    /// Validates a payload: a JSON object, at most 8 KB and 5 levels deep, with
    /// no secrets and no identity fields (a worker is named only through the
    /// verified <c>workerId</c> of the event itself).
    /// </summary>
    public static bool TryPayload(JsonElement? payload, out string json, out string? error)
    {
        json = "{}";
        error = null;
        if (payload is null || payload.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return true;
        var p = payload.Value;
        if (p.ValueKind != JsonValueKind.Object) { error = "The payload must be a JSON object."; return false; }
        var raw = p.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > PayloadMaxBytes) { error = $"The payload is larger than {PayloadMaxBytes / 1024} KB."; return false; }
        if (!Walk(p, 1, out error)) return false;
        json = raw;
        return true;
    }

    private static bool Walk(JsonElement e, int depth, out string? error)
    {
        error = null;
        if (depth > PayloadMaxDepth) { error = $"The payload is nested more than {PayloadMaxDepth} levels deep."; return false; }
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in e.EnumerateObject())
            {
                var key = prop.Name.ToLowerInvariant();
                if (ForbiddenKeyParts.Any(key.Contains)) { error = $"The payload can't carry secrets ('{prop.Name}')."; return false; }
                if (ForbiddenIdentityKeys.Contains(key.Replace("-", "_")) || ForbiddenIdentityKeys.Contains(key.Replace("-", "").Replace("_", "")))
                {
                    error = $"The payload can't name a worker ('{prop.Name}'). Use the event's workerId with a verified identity.";
                    return false;
                }
                if (!Walk(prop.Value, depth + 1, out error)) return false;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (!Walk(item, depth + 1, out error)) return false;
        }
        return true;
    }

    /// <summary>A top-level string property of the payload (case-insensitive name).</summary>
    public static string? PayloadString(JsonElement? payload, string name)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p) return null;
        foreach (var prop in p.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString()?.Trim();
        return null;
    }

    public static string? PayloadString(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return PayloadString(doc.RootElement, name);
        }
        catch (JsonException) { return null; }
    }

    // ─── Severity and summary ──────────────────────────────────────────────

    public static string EventSeverity(string type, string? toStatus, string? zoneType) => type switch
    {
        MonitoringEventTypes.UncertainDetection => MonitoringEventSeverities.Warning,
        MonitoringEventTypes.ZoneEntered when zoneType == "Restricted" => MonitoringEventSeverities.Critical,
        MonitoringEventTypes.CameraStatusChanged or MonitoringEventTypes.DeviceStatusChanged or MonitoringEventTypes.ModelStatusChanged
            when toStatus is "OFFLINE" or "ERROR" or "DEGRADED" => MonitoringEventSeverities.Warning,
        _ => MonitoringEventSeverities.Info,
    };

    public static string Summary(string type, bool simulated, string? cameraCode, string? deviceCode, string? zoneName, string? trackId,
        double? confidence, string? toStatus, string? activity, string? modelName, bool hasVerifiedWorker)
    {
        var where = cameraCode is null ? "" : $" on {cameraCode}";
        var track = trackId is null ? "" : $" (track {trackId})";
        var conf = confidence is null ? "" : $", confidence {confidence.Value:0.00}";
        var text = type switch
        {
            MonitoringEventTypes.PersonDetected => $"Person detected{where}{track}{conf}",
            MonitoringEventTypes.TrackUpdated => $"Track {trackId} updated{where}",
            MonitoringEventTypes.PersonLost => $"Track {trackId} lost{where}",
            MonitoringEventTypes.ZoneEntered => $"Track {trackId} entered zone {zoneName}{where}",
            MonitoringEventTypes.ZoneExited => $"Track {trackId} left zone {zoneName}{where}",
            MonitoringEventTypes.ActivityClassified => $"Activity '{activity}' classified{where}{track}{conf}",
            MonitoringEventTypes.UncertainDetection => $"Uncertain detection{where}{conf} (needs human review)",
            MonitoringEventTypes.CameraStatusChanged => $"Camera {cameraCode} is now {toStatus}",
            MonitoringEventTypes.DeviceStatusChanged => $"Device {deviceCode} is now {toStatus}",
            MonitoringEventTypes.ModelStatusChanged => $"Model {modelName} is now {toStatus}",
            _ => type,
        };
        if (hasVerifiedWorker) text += " · worker verified by RFID card";
        if (simulated) text = "SIMULATED · " + text;
        return text.Length > SummaryMax ? text[..SummaryMax] : text;
    }

    // ─── Alert rules ───────────────────────────────────────────────────────

    /// <summary>
    /// The alert an event raises, if any. Rules are about equipment and
    /// safety areas; none of them judges a worker's productivity, and an
    /// alert never changes pay, attendance or discipline by itself.
    /// </summary>
    public static AlertSpec? AlertFor(AlertContext c)
    {
        var sim = c.Simulated ? "SIMULATED · " : "";
        var simNote = c.Simulated ? " This alert comes from SIMULATED data, not from real equipment or camera AI." : "";
        switch (c.EventType)
        {
            case MonitoringEventTypes.CameraStatusChanged when c.ToStatus == "OFFLINE":
                return new AlertSpec(AlertTypes.CameraOffline, AlertSeverities.High, $"{sim}Camera {c.CameraCode} is offline",
                    $"Camera {c.CameraCode} stopped reporting{(c.Detail is null ? "" : $": {c.Detail}")}. Check its power and network.{simNote}",
                    $"{AlertTypes.CameraOffline}:{c.CameraId}");
            case MonitoringEventTypes.DeviceStatusChanged when c.ToStatus == "OFFLINE":
                return new AlertSpec(AlertTypes.DeviceOffline, AlertSeverities.Medium, $"{sim}Device {c.DeviceCode} is offline",
                    $"Device {c.DeviceCode} went offline{(c.Detail is null ? "" : $": {c.Detail}")}.{simNote}",
                    $"{AlertTypes.DeviceOffline}:{c.DeviceId}");
            case MonitoringEventTypes.DeviceStatusChanged when c.ToStatus == "ERROR":
                return new AlertSpec(AlertTypes.DeviceError, AlertSeverities.Medium, $"{sim}Device {c.DeviceCode} reported an error",
                    $"{c.Detail ?? "The device reported an error."}{simNote}",
                    $"{AlertTypes.DeviceError}:{c.DeviceId}");
            case MonitoringEventTypes.ModelStatusChanged when c.ToStatus is "ERROR" or "OFFLINE" or "DEGRADED":
                return new AlertSpec(AlertTypes.ModelUnavailable, c.ToStatus == "DEGRADED" ? AlertSeverities.Low : AlertSeverities.Medium,
                    $"{sim}Model {c.ModelName} is {c.ToStatus}",
                    $"The {c.ModelName} model reported {c.ToStatus}{(c.Detail is null ? "" : $": {c.Detail}")}. Its events may be missing or unreliable.{simNote}",
                    $"{AlertTypes.ModelUnavailable}:{c.ModelName}");
            case MonitoringEventTypes.UncertainDetection:
                return new AlertSpec(AlertTypes.UncertainDetection, AlertSeverities.Low, $"{sim}Uncertain detection on {c.CameraCode} needs review",
                    $"A detection on {c.CameraCode}{(c.Confidence is null ? "" : $" with confidence {c.Confidence:0.00}")} could not be confirmed automatically. " +
                    $"Review it before anyone acts on it; it does not identify anyone.{simNote}",
                    $"{AlertTypes.UncertainDetection}:{c.CameraId}");
            case MonitoringEventTypes.ZoneEntered when c.ZoneType == "Restricted":
                return new AlertSpec(AlertTypes.RestrictedZoneEntry, AlertSeverities.High, $"{sim}Restricted zone {c.ZoneName} entered",
                    $"Track {c.TrackId} entered restricted zone {c.ZoneName} on {c.CameraCode}. A track is a temporary camera number, not a worker identity; " +
                    $"check the area in person.{simNote}",
                    $"{AlertTypes.RestrictedZoneEntry}:{c.ZoneId}:{c.TrackId}");
            default:
                return null;
        }
    }

    /// <summary>The open alert a recovery event should annotate (camera / device / model back to normal).</summary>
    public static string? RecoveryDedupKey(AlertContext c) => c.EventType switch
    {
        MonitoringEventTypes.CameraStatusChanged when c.ToStatus == "ONLINE" => $"{AlertTypes.CameraOffline}:{c.CameraId}",
        MonitoringEventTypes.DeviceStatusChanged when c.ToStatus == "ONLINE" => $"{AlertTypes.DeviceOffline}:{c.DeviceId}",
        MonitoringEventTypes.ModelStatusChanged when c.ToStatus == "READY" => $"{AlertTypes.ModelUnavailable}:{c.ModelName}",
        _ => null,
    };

    // ─── Alert lifecycle ───────────────────────────────────────────────────

    /// <summary>OPEN → ACKNOWLEDGED → ESCALATED → RESOLVED; DISMISSED from any active state. Resolved and dismissed alerts are closed.</summary>
    public static AlertTransition Transition(string status, string action, int escalationLevel = 0)
    {
        if (!AlertStatuses.IsActive(status))
            return AlertTransition.No("alert_closed", $"The alert is {status.ToLowerInvariant()}; closed alerts can't change. A new occurrence raises a new alert.");
        return action switch
        {
            AlertActions.Acknowledge => status == AlertStatuses.Open
                ? AlertTransition.To(AlertStatuses.Acknowledged)
                : AlertTransition.No("alert_already_acknowledged", "The alert has already been acknowledged."),
            AlertActions.Escalate => escalationLevel >= AlertEscalationTargets.MaxLevel
                ? AlertTransition.No("alert_escalation_limit", $"The alert has been escalated {AlertEscalationTargets.MaxLevel} times already.")
                : AlertTransition.To(AlertStatuses.Escalated),
            AlertActions.Resolve => status == AlertStatuses.Open
                ? AlertTransition.No("alert_not_acknowledged", "Acknowledge the alert before resolving it, so someone has looked at it.")
                : AlertTransition.To(AlertStatuses.Resolved),
            AlertActions.Dismiss => AlertTransition.To(AlertStatuses.Dismissed),
            _ => AlertTransition.No("alert_action_invalid", "Unknown action."),
        };
    }
}

/// <summary>SignalR group names. Events go to the factory group of their category and to their line's group.</summary>
public static class MonitoringGroups
{
    /// <summary>Everyone with factory-wide reach who may see this category in this factory.</summary>
    public static string Factory(Guid factoryId, string category) => $"mon:f:{factoryId:N}:{category}";

    /// <summary>Line-scoped monitoring users (supervisors) of this line: every category.</summary>
    public static string Line(string lineId) => $"mon:l:{lineId}";

    /// <summary>Platform users watching every factory.</summary>
    public static string Platform(string category) => $"mon:all:{category}";
}
