namespace BunonBrain.Domain.Entities;

// Verified worker identity association: connecting a camera's temporary track
// to a permanent worker only through independently verified evidence (an
// active RFID card read at a reader mapped to the worker's assigned
// workstation, and exactly one track in that workstation's camera zone at that
// time). Nothing here is based on how a person looks.
//
// Reused, not duplicated: workers (worker_profiles), cards (worker_cards, with
// their issue / deactivation history), readers (devices of category
// RfidReader), workstation assignments, cameras, coverage and zones, and the
// camera track observations, which are the monitoring events the
// computer-vision service already sends (PERSON_DETECTED, ZONE_ENTERED, …).

/// <summary>
/// Where an RFID reader is, and how precisely. A reader mapped to one
/// workstation (or to a camera zone of one workstation) can place a worker
/// at that workstation; one mapped to a line, a floor or an area cannot.
/// Changing a mapping closes the current row (<see cref="EffectiveTo"/>) and
/// opens a new one, so every read is judged by the mapping in force when it
/// happened.
/// </summary>
public class RfidReaderMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>The reader: a device of category RfidReader.</summary>
    public Guid DeviceId { get; set; }
    /// <summary>WORKSTATION, ZONE, LINE, FLOOR or AREA (see IdentityRules.Precisions).</summary>
    public string Precision { get; set; } = default!;
    public Guid? DepartmentId { get; set; }
    public Guid? FloorId { get; set; }
    public string? LineId { get; set; }
    public Guid? WorkstationId { get; set; }
    /// <summary>A camera zone the reader covers (precision ZONE: a zone of one workstation).</summary>
    public Guid? CameraZoneId { get; set; }
    /// <summary>Free description of the location ("left of T-025, under the table").</summary>
    public string? Location { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public string? EndedByEmail { get; set; }
}

/// <summary>
/// One RFID card read at a reader, as received, and what the identity
/// engine made of it. The raw card UID is never stored: the card is
/// referenced by id when it is registered, otherwise only by a hash and the
/// last four characters. The read facts never change; only the evaluation
/// columns are filled in once the evidence window has passed.
/// </summary>
public class RfidReadEvent
{
    public long Id { get; set; }
    public Guid FactoryId { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>The reader mapping in force at <see cref="OccurredAt"/>.</summary>
    public Guid? MappingId { get; set; }
    public Guid? CardId { get; set; }
    /// <summary>The card's worker at the time of the read (null when the card is unknown, inactive or unassigned).</summary>
    public Guid? WorkerId { get; set; }
    /// <summary>SHA-256 of the normalised UID: lets repeated unknown cards be recognised without storing the UID.</summary>
    public string CardHash { get; set; } = default!;
    /// <summary>Last four characters of the UID, for people checking a card in hand ("…C3D4").</summary>
    public string CardSuffix { get; set; } = default!;
    /// <summary>Device (a reader / its gateway), Api (an integration) or Simulator.</summary>
    public string Source { get; set; } = default!;
    public bool IsSimulated { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>Unique per factory; a retry returns the first result.</summary>
    public string IdempotencyKey { get; set; } = default!;
    /// <summary>ACCEPTED or REJECTED (the card or reader failed validation).</summary>
    public string Outcome { get; set; } = default!;
    public string? OutcomeCode { get; set; }
    /// <summary>PENDING until evaluated, then VERIFIED, AMBIGUOUS, UNVERIFIED or MANUAL_REVIEW (null when rejected).</summary>
    public string? Evaluation { get; set; }
    public string? EvaluationCode { get; set; }
    public DateTimeOffset? EvaluatedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string RecordedByEmail { get; set; } = default!;
}

/// <summary>
/// The identity of one camera track (or a case about it). <see cref="WorkerId"/>
/// is set only for VERIFIED associations (and kept on later states for the
/// record); AMBIGUOUS and MANUAL_REVIEW cases list their candidates instead.
/// Every status change is written to <see cref="IdentityAssociationHistory"/>.
/// The database allows one VERIFIED association per track and one per worker
/// and camera.
/// </summary>
public class WorkerTrackAssociation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable number (IA-000123).</summary>
    public long Number { get; set; }
    public Guid FactoryId { get; set; }
    public Guid CameraId { get; set; }
    public string TrackId { get; set; } = default!;
    public Guid? WorkerId { get; set; }
    public Guid? WorkstationId { get; set; }
    public Guid? ZoneId { get; set; }
    public string? LineId { get; set; }
    /// <summary>PENDING, VERIFIED, AMBIGUOUS, UNVERIFIED, EXPIRED, REVOKED or MANUAL_REVIEW.</summary>
    public string Status { get; set; } = default!;
    /// <summary>RFID_WORKSTATION (automatic, all evidence agreed) or MANUAL_REVIEW (a person confirmed it).</summary>
    public string? VerificationMethod { get; set; }
    /// <summary>Why it has its status (e.g. MULTIPLE_TRACKS_IN_ZONE, TRACK_LOST).</summary>
    public string ReasonCode { get; set; } = default!;
    public string Summary { get; set; } = default!;
    /// <summary>The RFID read that started it.</summary>
    public long? RfidReadEventId { get; set; }
    /// <summary>The workstation assignment the evidence relied on.</summary>
    public Guid? AssignmentId { get; set; }
    /// <summary>JSON array of candidate worker ids (ambiguous / review cases).</summary>
    public string CandidateWorkers { get; set; } = "[]";
    /// <summary>JSON array of candidate track ids on this camera (ambiguous cases).</summary>
    public string CandidateTracks { get; set; } = "[]";
    /// <summary>True when any of its evidence is simulated: simulated and real evidence never combine.</summary>
    public bool IsSimulated { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    /// <summary>The latest moment it may be treated as valid; it is never valid indefinitely.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewedByEmail { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    /// <summary>Optimistic concurrency for reviews.</summary>
    public int Version { get; set; } = 1;
}

/// <summary>One piece of evidence considered for an association. Append-only.</summary>
public class IdentityAssociationEvidence
{
    public long Id { get; set; }
    public Guid AssociationId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>RFID_READ, CARD_OWNERSHIP, READER_MAPPING, ASSIGNMENT, CAMERA_ZONE, TRACK_OBSERVATION, OTHER_READ, CONFLICT, MANUAL.</summary>
    public string Kind { get; set; } = default!;
    /// <summary>SUPPORTS, CONTRADICTS or CONTEXT.</summary>
    public string Effect { get; set; } = default!;
    /// <summary>What it points at (rfid_read, worker_card, workstation_assignment, camera_zone, monitoring_event, …) and its id.</summary>
    public string? RefType { get; set; }
    public string? RefId { get; set; }
    public string Detail { get; set; } = default!;
    public DateTimeOffset? ObservedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// Every status change of an association, automatic or manual, with who made
/// it and why. Append-only: decisions are never overwritten.
/// </summary>
public class IdentityAssociationHistory
{
    public long Id { get; set; }
    public Guid AssociationId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>CREATED, VERIFIED, AMBIGUOUS, UNVERIFIED, EXPIRED, REVOKED, SUSPENDED, REFRESHED, CONFIRMED, REJECTED, MARKED_AMBIGUOUS.</summary>
    public string Action { get; set; } = default!;
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = default!;
    public Guid? WorkerId { get; set; }
    public string Reason { get; set; } = default!;
    /// <summary>True for a reviewer's decision, false for the engine.</summary>
    public bool IsManual { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorEmail { get; set; } = default!;
    public string? ActorRole { get; set; }
    public DateTimeOffset At { get; set; }
}
