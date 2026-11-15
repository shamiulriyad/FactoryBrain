namespace BunonBrain.Domain.Entities;

// Central monitoring: one immutable stream of monitoring events from every
// producer (device pipeline, camera service, the monitoring simulator and,
// later, the Python AI service), the alerts raised from them, and the human
// actions taken on those alerts. Raw events and every history row are
// append-only (database triggers); reviews and alert actions are recorded
// separately instead of editing what was received.

/// <summary>
/// One monitoring event as received. Never updated or deleted. A
/// <see cref="TrackId"/> is a camera-local, temporary track number, never a
/// worker identity; <see cref="WorkerId"/> is set only when the identity was
/// verified (an active RFID card of that worker, checked by the server).
/// </summary>
public class MonitoringEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }

    /// <summary>PERSON_DETECTED, TRACK_UPDATED, ... (see MonitoringEventTypes).</summary>
    public string EventType { get; set; } = default!;

    /// <summary>VISION (people, tracks, zones, activity) or SYSTEM (camera, device and model status).</summary>
    public string Category { get; set; } = default!;

    /// <summary>SIMULATED or REAL.</summary>
    public string SourceType { get; set; } = default!;

    /// <summary>Who handed the event to the pipeline: API, SIMULATOR, DEVICE_PIPELINE, CAMERA_SERVICE.</summary>
    public string Producer { get; set; } = default!;

    /// <summary>The producer's key; one event per key and factory.</summary>
    public string IdempotencyKey { get; set; } = default!;

    public Guid? DepartmentId { get; set; }
    public Guid? FloorId { get; set; }
    public string? LineId { get; set; }
    public Guid? WorkstationId { get; set; }
    public Guid? CameraId { get; set; }
    public Guid? DeviceId { get; set; }
    /// <summary>The machine a machine-monitoring event or alert is about.</summary>
    public Guid? MachineId { get; set; }
    public Guid? ZoneId { get; set; }

    /// <summary>Camera-local temporary track id. Not a worker id.</summary>
    public string? TrackId { get; set; }

    /// <summary>Only with a verified identity association (see <see cref="IdentityMethod"/>).</summary>
    public Guid? WorkerId { get; set; }
    public string? IdentityMethod { get; set; }
    public Guid? WorkerCardId { get; set; }

    public double? Confidence { get; set; }
    public string Severity { get; set; } = "INFO";
    public string Summary { get; set; } = default!;

    /// <summary>The producer's payload (a JSON object; secrets and identity fields are refused).</summary>
    public string Payload { get; set; } = "{}";

    public string? ModelName { get; set; }
    public string? ModelVersion { get; set; }
    public Guid? SimulationRunId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string RecordedByEmail { get; set; } = default!;
}

/// <summary>A human review of an event, kept beside the (unchanged) event. Append-only.</summary>
public class MonitoringEventReview
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid FactoryId { get; set; }

    /// <summary>CONFIRMED, FALSE_POSITIVE, STILL_UNCERTAIN, NEEDS_FOLLOW_UP.</summary>
    public string Decision { get; set; } = default!;
    public string? Note { get; set; }
    public Guid ReviewedByUserId { get; set; }
    public string ReviewedByEmail { get; set; } = default!;
    public string? ReviewedByRole { get; set; }
    public DateTimeOffset ReviewedAt { get; set; }
}

/// <summary>
/// An alert raised from monitoring events. Its state only changes through
/// validated actions (acknowledge, escalate, resolve, dismiss), each written
/// to <see cref="MonitoringAlertAction"/>. Alerts inform people; they never
/// change pay, attendance or discipline.
/// </summary>
public class MonitoringAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Sequential number for people (shown as ALT-000123).</summary>
    public long Number { get; set; }
    public Guid FactoryId { get; set; }
    public string AlertType { get; set; } = default!;
    public string Category { get; set; } = default!;

    /// <summary>LOW, MEDIUM, HIGH, CRITICAL.</summary>
    public string Severity { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Description { get; set; } = default!;

    public Guid? DepartmentId { get; set; }
    public Guid? FloorId { get; set; }
    public string? LineId { get; set; }
    public Guid? WorkstationId { get; set; }
    public Guid? CameraId { get; set; }
    public Guid? DeviceId { get; set; }
    /// <summary>The machine a machine-monitoring event or alert is about.</summary>
    public Guid? MachineId { get; set; }
    public Guid? ZoneId { get; set; }

    /// <summary>The event that raised the alert.</summary>
    public Guid EventId { get; set; }
    public Guid? LastEventId { get; set; }

    /// <summary>SIMULATED or REAL (the source of the raising event).</summary>
    public string SourceType { get; set; } = default!;

    /// <summary>Repeats of the same situation update the open alert instead of raising another.</summary>
    public string DedupKey { get; set; } = default!;
    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset FirstOccurredAt { get; set; }
    public DateTimeOffset LastOccurredAt { get; set; }

    /// <summary>OPEN, ACKNOWLEDGED, ESCALATED, RESOLVED, DISMISSED.</summary>
    public string Status { get; set; } = "OPEN";
    public int EscalationLevel { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public string? AcknowledgedByEmail { get; set; }

    public DateTimeOffset? EscalatedAt { get; set; }
    public string? EscalatedByEmail { get; set; }
    public string? EscalatedToRole { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedByEmail { get; set; }
    public string? ResolutionCode { get; set; }
    public string? ResolutionNote { get; set; }

    public DateTimeOffset? DismissedAt { get; set; }
    public string? DismissedByEmail { get; set; }
    public string? DismissReason { get; set; }

    /// <summary>Evidence metadata only (event ids, confidence, zone, frame time). No images, no identities.</summary>
    public string Evidence { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency: two people acting on one alert can't both win.</summary>
    public int Version { get; set; } = 1;
}

/// <summary>One entry of an alert's history. Append-only.</summary>
public class MonitoringAlertAction
{
    public long Id { get; set; }
    public Guid AlertId { get; set; }
    public Guid FactoryId { get; set; }

    /// <summary>CREATED, REPEATED, SOURCE_RECOVERED, ACKNOWLEDGED, ESCALATED, RESOLVED, DISMISSED.</summary>
    public string Action { get; set; } = default!;
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = default!;
    public string? Note { get; set; }
    public string? EscalatedToRole { get; set; }
    public string? ResolutionCode { get; set; }
    public Guid? EventId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorEmail { get; set; } = default!;
    public string? ActorRole { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A run of the monitoring simulator: which cameras, how often, which event types, for how long.</summary>
public class MonitoringSimulationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }

    /// <summary>RUNNING, STOPPED, COMPLETED.</summary>
    public string Status { get; set; } = "RUNNING";

    /// <summary>JSON array of camera ids.</summary>
    public string CameraIds { get; set; } = "[]";

    /// <summary>JSON array of event types the run may generate.</summary>
    public string EventTypes { get; set; } = "[]";
    public int EventsPerMinute { get; set; }
    public int DurationMinutes { get; set; }
    public int Seed { get; set; }
    public int EventCount { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public Guid StartedByUserId { get; set; }
    public string StartedByEmail { get; set; } = default!;
    public DateTimeOffset EndsAt { get; set; }
    public DateTimeOffset NextEventAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public string? StoppedByEmail { get; set; }
    public string? StopReason { get; set; }
}
