using BunonBrain.Application.Dtos.Monitoring;

namespace BunonBrain.Application.Dtos.Identity;

// Identity association API shapes. No raw RFID card UID appears anywhere:
// cards are shown as "…C3D4" (last four characters) and their printed label.

/// <summary>POST /api/identity/rfid-reads — one read from a reader (or its gateway / an integration).</summary>
public sealed record RfidReadRequest(Guid FactoryId, string CardUid, string IdempotencyKey, Guid? DeviceId = null, string? DeviceCode = null,
    DateTimeOffset? OccurredAt = null);

public sealed record RfidReadDto(
    long Id, Guid FactoryId, Guid DeviceId, string ReaderCode, string Card, string? CardLabel, MonRef? Worker, string Source, bool IsSimulated,
    DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string Outcome, string? OutcomeCode, string? Evaluation, string? EvaluationCode,
    string? EvaluationText, DateTimeOffset? EvaluatedAt);

public sealed record RfidReadResult(string Outcome, RfidReadDto Read, IReadOnlyList<AssociationDto> Associations);

/// <summary>PUT /api/identity/readers/{deviceId}/mapping — where the reader is, and how precisely.</summary>
public sealed record ReaderMappingRequest(string Precision, Guid? DepartmentId = null, Guid? FloorId = null, string? LineId = null,
    Guid? WorkstationId = null, Guid? CameraZoneId = null, string? Location = null);

public sealed record ReaderMappingDto(
    Guid Id, Guid DeviceId, string Precision, bool WorkstationLevel, Guid? DepartmentId, string? DepartmentName, Guid? FloorId, string? FloorName,
    MonLine? Line, MonRef? Workstation, Guid? CameraZoneId, string? ZoneName, string? ZoneCameraCode, string? Location,
    DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo, string CreatedByEmail);

/// <summary>An RFID reader (a device of category RfidReader) with its status, mapping and the cameras covering its workstation.</summary>
public sealed record RfidReaderDto(
    Guid DeviceId, string Code, string Name, Guid FactoryId, string FactoryCode, bool IsSimulated, string AdminStatus, string Health,
    DateTimeOffset? LastSeenAt, ReaderMappingDto? Mapping, IReadOnlyList<MonZone> WorkstationZones, IReadOnlyList<string> CameraCodes,
    int ReadsLastHour, DateTimeOffset? LastReadAt, bool CanConfigure);

public sealed record AssociationDto(
    Guid Id, string Code, Guid FactoryId, string FactoryCode, MonRef Camera, string TrackId, string Status, bool IsCurrent,
    // Worker: only when VERIFIED and currently valid; null otherwise, whatever the history says.
    MonRef? Worker, MonRef? Workstation, MonZone? Zone, MonLine? Line, string? VerificationMethod, string ReasonCode, string ReasonText,
    string Summary, bool IsSimulated, bool ReviewRequired, int CandidateCount, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    DateTimeOffset ExpiresAt, DateTimeOffset UpdatedAt, int Version);

public sealed record EvidenceDto(long Id, string Kind, string Effect, string? RefType, string? RefId, string Detail, DateTimeOffset? ObservedAt);

public sealed record AssociationHistoryDto(long Id, string Action, string? FromStatus, string ToStatus, MonRef? Worker, string Reason, bool IsManual,
    string ActorEmail, string? ActorRole, DateTimeOffset At);

/// <summary>A candidate worker of a case, with the assignment that made them plausible (reviewers only).</summary>
public sealed record CandidateWorkerDto(MonRef Worker, string? Workstation, DateTimeOffset? AssignedSince);

public sealed record AssociationDetailDto(
    AssociationDto Association, IReadOnlyList<EvidenceDto> Evidence, IReadOnlyList<AssociationHistoryDto> History, RfidReadDto? Read,
    IReadOnlyList<CandidateWorkerDto> Candidates, IReadOnlyList<string> CandidateTracks, bool TrackActive, DateTimeOffset? TrackLastSeenAt,
    bool CanReview, IReadOnlyList<string> AllowedDecisions);

/// <summary>POST /api/identity/associations/{id}/review — CONFIRM (with workerId), REJECT, AMBIGUOUS or REVOKE, always with a reason.</summary>
public sealed record AssociationReviewRequest(string Decision, string Reason, Guid? WorkerId = null, int? ExpectedVersion = null);

/// <summary>The identity of one track a camera currently sees.</summary>
public sealed record TrackIdentityDto(
    Guid CameraId, string CameraCode, string TrackId, DateTimeOffset LastSeenAt, MonZone? Zone, bool IsSimulated, string Identity,
    MonRef? Worker, MonRef? Workstation, Guid? AssociationId, string? AssociationCode, string Action);

public sealed record IdentityCounts(int VerifiedNow, int UnverifiedTracks, int AmbiguousTracks, int PendingReview, int PendingReads,
    int ReadersOnline, int Readers);

public sealed record IdentityOverviewDto(
    DateTimeOffset AsOf, IdentityCounts Counts, IReadOnlyList<TrackIdentityDto> Tracks, IReadOnlyList<AssociationDto> ReviewQueue,
    IReadOnlyList<AssociationChangeDto> RecentChanges, IReadOnlyList<RfidReaderDto> Readers, IReadOnlyList<RfidReadDto> RecentReads, string Note);

public sealed record AssociationChangeDto(long Id, Guid AssociationId, string AssociationCode, string CameraCode, string TrackId, string Action,
    string? FromStatus, string ToStatus, string Reason, bool IsManual, string ActorEmail, DateTimeOffset At, bool IsSimulated);

public sealed record AssociationQuery(Guid? FactoryId = null, Guid? CameraId = null, string? TrackId = null, Guid? WorkerId = null, string? Status = null,
    string? LineId = null, bool? ReviewOnly = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Page = 1, int PageSize = 50);

/// <summary>POST /api/identity/simulator — development scenarios through the real ingestion pipeline (simulated readers only).</summary>
public sealed record RfidSimulationRequest(Guid FactoryId, Guid DeviceId, string Scenario, Guid? WorkerId = null, Guid? SecondWorkerId = null,
    Guid? OtherDeviceId = null, bool WithSimulatedTrack = false);

public sealed record RfidSimulationResult(string Scenario, IReadOnlyList<RfidReadResult> Reads, IReadOnlyList<string> Notes);
