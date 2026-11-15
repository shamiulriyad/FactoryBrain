using System.Text.Json;
using BunonBrain.Application.Monitoring;

namespace BunonBrain.Application.Dtos.Monitoring;

// ─── References ────────────────────────────────────────────────────────────

public sealed record MonRef(Guid Id, string Code, string? Name);
public sealed record MonLine(string Id, string Name);
public sealed record MonZone(Guid Id, string Name, string ZoneType);

// ─── Event ingestion contract ──────────────────────────────────────────────

/// <summary>
/// How a worker identity was verified. Only <c>RFID_BADGE</c> with the card's
/// UID is accepted (the server checks the card is an active card of that
/// worker in the event's factory). Visual methods are always refused.
/// </summary>
public sealed record IdentityClaimRequest(string Method, string? CardUid);

/// <summary>
/// POST /api/monitoring/events — one event from a real producer (a gateway,
/// later the Python AI service). <c>sourceType</c> must be REAL here; the
/// simulator has its own endpoints. <c>idempotencyKey</c> is required: the
/// same key is stored once per factory.
/// </summary>
public sealed record IngestMonitoringEventRequest(
    string EventType,
    Guid FactoryId,
    string? IdempotencyKey,
    DateTimeOffset? OccurredAt = null,
    string? SourceType = null,
    Guid? CameraId = null,
    Guid? DeviceId = null,
    string? LineId = null,
    Guid? WorkstationId = null,
    Guid? ZoneId = null,
    string? TrackId = null,
    Guid? WorkerId = null,
    IdentityClaimRequest? Identity = null,
    double? Confidence = null,
    JsonElement? Payload = null,
    string? ModelName = null,
    string? ModelVersion = null);

/// <summary>An event inside the pipeline (from the API, the simulator or a system hook).</summary>
public sealed record MonitoringEventInput(
    string EventType,
    Guid FactoryId,
    string? IdempotencyKey,
    DateTimeOffset? OccurredAt,
    string SourceType,
    Guid? CameraId = null,
    Guid? DeviceId = null,
    string? LineId = null,
    Guid? WorkstationId = null,
    Guid? ZoneId = null,
    string? TrackId = null,
    Guid? WorkerId = null,
    IdentityClaimRequest? Identity = null,
    double? Confidence = null,
    JsonElement? Payload = null,
    string? ModelName = null,
    string? ModelVersion = null,
    Guid? SimulationRunId = null)
{
    public static MonitoringEventInput From(IngestMonitoringEventRequest r) => new(
        r.EventType, r.FactoryId, r.IdempotencyKey, r.OccurredAt, r.SourceType ?? MonitoringSourceTypes.Real, r.CameraId, r.DeviceId, r.LineId,
        r.WorkstationId, r.ZoneId, r.TrackId, r.WorkerId, r.Identity, r.Confidence, r.Payload, r.ModelName, r.ModelVersion);
}

/// <summary>Accepted (stored now) or Duplicate (the key was seen before; the stored event is returned unchanged).</summary>
public sealed record MonitoringIngestResult(string Outcome, MonitoringEventDto Event, MonitoringAlertDto? Alert);

// ─── Events ────────────────────────────────────────────────────────────────

public sealed record MonitoringEventQuery(
    Guid? FactoryId = null, Guid? DepartmentId = null, Guid? FloorId = null, string? LineId = null, Guid? CameraId = null,
    Guid? DeviceId = null, Guid? WorkstationId = null, string? EventType = null, string? Category = null, string? SourceType = null,
    string? TrackId = null, bool? VerifiedWorker = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Search = null,
    Guid? SimulationRunId = null, int Page = 1, int PageSize = 50, string? Activity = null);

public sealed record MonitoringEventDto(
    Guid Id,
    string EventType,
    string EventLabel,
    string Category,
    string SourceType,
    bool IsSimulated,
    string Producer,
    string Severity,
    string Summary,
    Guid FactoryId,
    string FactoryCode,
    string? DepartmentName,
    string? FloorName,
    MonLine? Line,
    MonRef? Workstation,
    MonRef? Camera,
    MonRef? Device,
    MonZone? Zone,
    string? TrackId,
    MonRef? VerifiedWorker,
    string? IdentityMethod,
    double? Confidence,
    JsonElement Payload,
    string? ModelName,
    string? ModelVersion,
    Guid? SimulationRunId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string RecordedByEmail,
    int ReviewCount,
    string? LatestReview);

public sealed record EventReviewDto(long Id, string Decision, string? Note, string ReviewedByEmail, string? ReviewedByRole, DateTimeOffset ReviewedAt);

public sealed record AlertLinkDto(Guid Id, string Code, string Title, string Status, string Severity);

public sealed record MonitoringEventDetailDto(MonitoringEventDto Event, IReadOnlyList<EventReviewDto> Reviews, IReadOnlyList<AlertLinkDto> Alerts, bool CanReview);

/// <summary>POST /api/monitoring/events/{id}/reviews — a decision recorded beside the unchanged event.</summary>
public sealed record ReviewMonitoringEventRequest(string Decision, string? Note);

// ─── Alerts ────────────────────────────────────────────────────────────────

/// <summary><c>status</c>: one status, or ACTIVE (open, acknowledged or escalated). <c>sort</c>: newest (default) | oldest | severity | updated.</summary>
public sealed record MonitoringAlertQuery(
    Guid? FactoryId = null, Guid? DepartmentId = null, Guid? FloorId = null, string? LineId = null, Guid? CameraId = null, Guid? DeviceId = null,
    string? Status = null, string? Severity = null, string? AlertType = null, string? SourceType = null, string? Category = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Search = null, string? Sort = null, int Page = 1, int PageSize = 25);

public sealed record AlertCan(bool Acknowledge, bool Escalate, bool Resolve, bool Dismiss);

public sealed record MonitoringAlertDto(
    Guid Id,
    string Code,
    string AlertType,
    string Category,
    string Severity,
    string Title,
    string Description,
    string Status,
    string SourceType,
    bool IsSimulated,
    Guid FactoryId,
    string FactoryCode,
    string? DepartmentName,
    string? FloorName,
    MonLine? Line,
    MonRef? Workstation,
    MonRef? Camera,
    MonRef? Device,
    MonZone? Zone,
    Guid EventId,
    Guid? LastEventId,
    int OccurrenceCount,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    int EscalationLevel,
    string? EscalatedToRole,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedByEmail,
    DateTimeOffset? EscalatedAt,
    string? EscalatedByEmail,
    DateTimeOffset? ResolvedAt,
    string? ResolvedByEmail,
    string? ResolutionCode,
    string? ResolutionNote,
    DateTimeOffset? DismissedAt,
    string? DismissedByEmail,
    string? DismissReason,
    JsonElement Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    AlertCan Can);

public sealed record AlertActionDto(
    long Id, string Action, string? FromStatus, string ToStatus, string? Note, string? EscalatedToRole, string? ResolutionCode,
    Guid? EventId, string ActorEmail, string? ActorRole, DateTimeOffset At);

public sealed record MonitoringAlertDetailDto(MonitoringAlertDto Alert, IReadOnlyList<AlertActionDto> History, MonitoringEventDto? Event, IReadOnlyList<MonitoringEventDto> RelatedEvents);

public sealed record AcknowledgeAlertRequest(string? Note = null, int? ExpectedVersion = null);
public sealed record EscalateAlertRequest(string ToRole, string Reason, int? ExpectedVersion = null);
public sealed record ResolveAlertRequest(string ResolutionCode, string Note, int? ExpectedVersion = null);
public sealed record DismissAlertRequest(string Reason, int? ExpectedVersion = null);

// ─── Monitoring room ───────────────────────────────────────────────────────

public sealed record MonitoringOverviewQuery(
    Guid? FactoryId = null, Guid? DepartmentId = null, Guid? FloorId = null, string? LineId = null, Guid? CameraId = null,
    string? EventType = null, string? AlertStatus = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record CameraCounts(int Total, int Online, int Offline, int Unknown, int NotActive, int SimulatedHealth);
public sealed record DeviceCounts(int Total, int Online, int Offline, int Unknown, int Maintenance, int Disabled, int Simulated, int Real);
public sealed record AlertCounts(int Active, int Unacknowledged, int Escalated, int Critical, int High, int Simulated, int Real, int InFilter);
public sealed record EventCounts(int LastHour, int Last24Hours, int InRange, int SimulatedInRange, int RealInRange);

public sealed record LineStatusDto(
    string LineId, string LineName, Guid? FactoryId, string? FactoryCode, int Cameras, int CamerasOnline, int CamerasOffline,
    int ActiveAlerts, string? HighestSeverity, DateTimeOffset? LastEventAt, int EventsLastHour, string Status);

public sealed record FactoryStatusDto(
    Guid Id, string Code, string Name, int Cameras, int CamerasOnline, int CamerasOffline, int Devices, int DevicesOffline,
    int ActiveAlerts, string? HighestSeverity, string Status);

public sealed record MonitoringOverviewDto(
    DateTimeOffset AsOf,
    bool CanSeeVision,
    CameraCounts Cameras,
    DeviceCounts Devices,
    AlertCounts Alerts,
    EventCounts Events,
    IReadOnlyList<MonitoringEventDto> RecentEvents,
    IReadOnlyList<MonitoringAlertDto> ActiveAlerts,
    IReadOnlyList<LineStatusDto> Lines,
    IReadOnlyList<FactoryStatusDto> Factories,
    string AiStatus);

// ─── Activity (predicted posture / movement of temporary tracks) ─────────

/// <summary>
/// GET /api/monitoring/activity. <c>From</c>/<c>To</c> default to the last
/// hour; a track is "current" when its latest activity event is no older than
/// <c>ActiveSeconds</c> (default 300) before <c>To</c> and it hasn't been lost.
/// </summary>
public sealed record MonitoringActivityQuery(
    Guid? FactoryId = null, string? LineId = null, Guid? CameraId = null, string? Activity = null, string? SourceType = null,
    bool? UncertainOnly = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int? ActiveSeconds = null, string? Kind = null);

/// <summary>A model whose predictions are in the activity picture, with how it was validated (garment models: EVALUATED or PENDING).</summary>
public sealed record ActivityModelDto(string Name, string? Version, int Events, string? EvaluationStatus, DateTimeOffset LastAt);

/// <summary>
/// One temporary track's predicted activity. <c>TrackId</c> is a camera-local
/// number, never a worker; <c>VerifiedWorker</c> is only set when an RFID card
/// verified the identity on the event (never by the computer-vision service).
/// </summary>
public sealed record ActivityTrackDto(
    Guid CameraId, string CameraCode, MonLine? Line, string TrackId, string Activity, double? Confidence, bool Uncertain, string? Reason,
    string? Candidate, DateTimeOffset Since, DateTimeOffset LastPredictionAt, bool Current, bool Ended, int Changes, string? ModelName,
    string? ModelVersion, string SourceType, MonRef? VerifiedWorker, Guid LastEventId);

/// <summary>Observed seconds and prediction count of one activity (time between a track's consecutive activity events, capped).</summary>
public sealed record ActivityShareDto(string Activity, double Seconds, int Predictions);

public sealed record ActivityDistributionDto(string Key, string Name, IReadOnlyList<ActivityShareDto> Activities, double ObservedSeconds,
    int Tracks, int UncertainPredictions, int Predictions);

public sealed record ActivityCountDto(string Activity, int Tracks);

public sealed record ActivityCountsDto(int CurrentTracks, IReadOnlyList<ActivityCountDto> Current, int Classified, int StateChanges,
    int Uncertain, double? UncertainRate);

public sealed record MonitoringActivityDto(
    DateTimeOffset AsOf, DateTimeOffset From, DateTimeOffset To, int ActiveSeconds, ActivityCountsDto Counts,
    IReadOnlyList<ActivityTrackDto> Tracks, IReadOnlyList<ActivityDistributionDto> ByCamera, IReadOnlyList<ActivityDistributionDto> ByLine,
    IReadOnlyList<MonitoringEventDto> Transitions, IReadOnlyList<string> Activities, bool Truncated, string Note,
    string Kind = "generic", IReadOnlyList<ActivityModelDto>? Models = null);

// ─── Camera monitoring ─────────────────────────────────────────────────────

public sealed record MonitoringCameraQuery(Guid? FactoryId = null, Guid? FloorId = null, string? LineId = null, string? Connection = null, string? Search = null);

/// <summary>
/// What can be shown for a camera's source. Never a URL, host or credential.
/// <c>Kind</c>: RECORDED_VIDEO (playable file), LOCAL_WEBCAM (the viewer's own
/// browser camera), LIVE_STREAM (RTSP / HTTP: configured, not opened by the
/// server in this phase) or NONE.
/// </summary>
public sealed record CameraFeedDto(string SourceType, string Kind, bool Playable, bool LiveStreamAvailable, string? WebcamLabel,
    string? VideoFileName, string ConnectionCheck, string Note);

/// <summary>ONLINE / OFFLINE / UNKNOWN from the camera's health device, or INACTIVE / MAINTENANCE when the camera isn't in service.</summary>
public sealed record CameraConnectionDto(string Status, string Label, bool IsSimulated, bool PhysicallyConnected, DateTimeOffset? LastSeenAt,
    string? HealthDeviceCode, string? LastError);

public sealed record MonitoringCameraDto(
    Guid Id, string Code, string Name, string CameraType, string AdminStatus, Guid FactoryId, string FactoryCode, string? FloorName,
    MonLine? Line, IReadOnlyList<string> CoveredWorkstations, CameraFeedDto Feed, CameraConnectionDto Connection, int ActiveZones,
    int ActiveAlerts, string? HighestSeverity, DateTimeOffset? LastEventAt, bool HasReferenceFrame);

public sealed record MonitoringCameraDetailDto(
    MonitoringCameraDto Camera, IReadOnlyList<MonZone> Zones, IReadOnlyList<MonitoringEventDto> RecentEvents,
    IReadOnlyList<MonitoringAlertDto> ActiveAlerts, string AiStatus, string AiMessage);

// ─── Simulator ─────────────────────────────────────────────────────────────

public sealed record StartSimulationRequest(Guid FactoryId, IReadOnlyList<Guid>? CameraIds = null, int? EventsPerMinute = null,
    int? DurationMinutes = null, IReadOnlyList<string>? EventTypes = null);

public sealed record StopSimulationRequest(string? Reason = null);

/// <summary>One demo event. Missing details are filled in (a new track, a zone of the camera, a sample confidence).</summary>
public sealed record TriggerSimulatedEventRequest(Guid FactoryId, string EventType, Guid? CameraId = null, Guid? ZoneId = null, Guid? DeviceId = null,
    string? TrackId = null, double? Confidence = null, string? Activity = null, string? ToStatus = null);

public sealed record SimulationRunDto(
    Guid Id, Guid FactoryId, string FactoryCode, string Status, IReadOnlyList<MonRef> Cameras, IReadOnlyList<string> EventTypes,
    int EventsPerMinute, int DurationMinutes, int EventCount, DateTimeOffset StartedAt, string StartedByEmail, DateTimeOffset EndsAt,
    DateTimeOffset? StoppedAt, string? StoppedByEmail, string? StopReason);

public sealed record SimulatorStatusDto(SimulationRunDto? Running, IReadOnlyList<SimulationRunDto> Recent, IReadOnlyList<string> EventTypes,
    int MaxEventsPerMinute, int MaxDurationMinutes, int MaxCameras, string Note);

/// <summary>Accepted / Duplicate (an event was ingested), or DeviceSimulated (a simulated device was driven; its pipeline emitted the events).</summary>
public sealed record SimulatorTriggerResult(string Outcome, MonitoringEventDto? Event, MonitoringAlertDto? Alert, string? Note);

public sealed record SimulatorTickResult(int Runs, int Events, int Completed);

// ─── Contract (for producers and the UI) ───────────────────────────────────

public sealed record EventTypeContract(string Type, string Label, string Category, EventRequirements Requires, IReadOnlyList<string> StatusValues);

public sealed record MonitoringContractDto(
    IReadOnlyList<EventTypeContract> EventTypes, IReadOnlyList<string> SourceTypes, IReadOnlyList<string> IdentityMethods,
    IReadOnlyList<string> RefusedIdentityMethods, IReadOnlyList<string> AlertStatuses, IReadOnlyList<string> AlertSeverities,
    IReadOnlyList<string> ResolutionCodes, IReadOnlyList<string> EscalationTargets, IReadOnlyList<string> ReviewDecisions,
    int IdempotencyKeyMax, int TrackIdMax, int PayloadMaxBytes, int PayloadMaxDepth, string Rules);
