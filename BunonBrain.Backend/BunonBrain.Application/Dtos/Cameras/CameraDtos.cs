using BunonBrain.Application.Cameras;
using BunonBrain.Application.Dtos.Workers;

namespace BunonBrain.Application.Dtos.Cameras;

// ─── Read model ────────────────────────────────────────────────────────────

public sealed record CameraWorkstationRef(Guid Id, string Code, string LineId, bool IsActive);

public sealed record CameraCoverageDto(Guid Id, string Kind, WorkerLineRef Line, CameraWorkstationRef? Workstation, DateTimeOffset CreatedAt, string CreatedByEmail);

/// <summary>
/// One version of a camera's video source. Credentials are never returned:
/// <see cref="HasCredentials"/> only says whether encrypted credentials are
/// stored. <see cref="ConnectionCheck"/> is always <c>NotChecked</c> in this
/// phase: the server does not connect to camera URLs.
/// </summary>
public sealed record CameraSourceDto(
    Guid Id,
    int Version,
    string SourceType,
    string? StreamUrl,
    string? StreamHost,
    bool HasCredentials,
    string? WebcamLabel,
    bool HasVideo,
    string? VideoFileName,
    string? VideoContentType,
    long? VideoSizeBytes,
    string? Notes,
    string ConnectionCheck,
    DateTimeOffset CreatedAt,
    string CreatedByEmail,
    DateTimeOffset? RetiredAt);

public sealed record ReferenceFrameDto(string Source, int Width, int Height, string ContentType, double? FrameSeconds, DateTimeOffset UpdatedAt);

/// <summary>Health of the camera's linked device (null when the camera has none).</summary>
public sealed record CameraHealthDto(
    Guid DeviceId, string DeviceCode, bool IsSimulated, string Status, string Health, string Connection, string Label,
    bool PhysicallyConnected, DateTimeOffset? LastSeenAt, DateTimeOffset? LastErrorAt, string? LastErrorMessage);

/// <summary>
/// Readiness for the future computer-vision service. <see cref="Status"/> is
/// always <c>NotConnected</c> in this phase: no detection, tracking or
/// recognition runs, and nothing here is an AI result.
/// </summary>
public sealed record AiIntegrationDto(string Status, string Message, bool VideoSourceReady, bool ReferenceFrameReady, int ActiveZones);

public sealed record CameraCapabilities(bool Manage, bool? Delete, string? DeleteBlockedReason);

public sealed record CameraDto(
    Guid Id,
    string Code,
    string Name,
    string CameraType,
    Guid FactoryId,
    string FactoryCode,
    string FactoryName,
    OrgRef? Department,
    OrgRef Floor,
    WorkerLineRef? Line,
    string? Location,
    string? Description,
    string Status,
    string? StatusReason,
    DateTimeOffset StatusChangedAt,
    DateOnly? InstalledOn,
    CameraSourceDto? Source,
    IReadOnlyList<CameraCoverageDto> Coverage,
    int ActiveZoneCount,
    ReferenceFrameDto? ReferenceFrame,
    CameraHealthDto? Health,
    DateTimeOffset LastStatusUpdateAt,
    AiIntegrationDto Ai,
    CameraCapabilities Can,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// GET /api/cameras. <c>sort</c>: code | name | status | type | updated (prefix "-" for descending).
/// <c>health</c>: Online | Offline | Unknown | Maintenance | Disabled | None (no health device).
/// </summary>
public sealed record CameraListQuery(
    string? Search = null, Guid? FactoryId = null, Guid? FloorId = null, string? LineId = null, Guid? WorkstationId = null,
    string? Status = null, string? CameraType = null, string? SourceType = null, string? Health = null,
    string? Sort = null, int Page = 1, int PageSize = 25);

public sealed record CameraOverviewDto(
    int Total,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByType,
    IReadOnlyDictionary<string, int> BySource,
    int WithoutSource,
    int WithoutReferenceFrame,
    int WithZones,
    int ActiveZones,
    int WithoutHealthDevice,
    IReadOnlyDictionary<string, int> ByHealth,
    int SimulatedHealth);

// ─── Commands ──────────────────────────────────────────────────────────────

/// <summary>
/// POST /api/cameras. <c>Code</c> may be left empty for the next CAM-### of
/// the factory. <c>HealthDevice</c>: None (default), Simulated or Real —
/// registers a linked Camera device that reports the camera's health.
/// </summary>
public sealed record CreateCameraRequest(
    Guid FactoryId,
    string Name,
    string CameraType,
    Guid FloorId,
    string? Code = null,
    Guid? DepartmentId = null,
    string? LineId = null,
    string? Location = null,
    string? Description = null,
    DateOnly? InstalledOn = null,
    IReadOnlyList<string>? CoverageLineIds = null,
    IReadOnlyList<Guid>? WorkstationIds = null,
    string? HealthDevice = null);

/// <summary>PUT /api/cameras/{id}. The factory and code can't change.</summary>
public sealed record UpdateCameraRequest(
    string Name, string CameraType, Guid FloorId, Guid? DepartmentId, string? LineId, string? Location, string? Description, DateOnly? InstalledOn);

/// <summary>PATCH /api/cameras/{id}/status: Active, Inactive or Maintenance. A reason is required for anything but Active.</summary>
public sealed record SetCameraStatusRequest(string Status, string? Reason = null);

/// <summary>PUT /api/cameras/{id}/coverage — the full set of covered lines and workstations (replaces the current set).</summary>
public sealed record SetCameraCoverageRequest(IReadOnlyList<string>? LineIds, IReadOnlyList<Guid>? WorkstationIds);

/// <summary>
/// PUT /api/cameras/{id}/source — adds a new source version. For Rtsp /
/// HttpStream: <c>StreamUrl</c> (credentials in the URL are split off and
/// encrypted), or <c>UserName</c> / <c>Password</c>; <c>KeepCredentials</c>
/// re-uses the current encrypted credentials. RecordedVideo through this
/// endpoint keeps the current video (upload a new one with PUT …/source/video).
/// </summary>
public sealed record SetCameraSourceRequest(
    string SourceType,
    string? StreamUrl = null,
    string? UserName = null,
    string? Password = null,
    bool KeepCredentials = false,
    string? WebcamLabel = null,
    string? Notes = null);

/// <summary>An uploaded file, read through a stream (the API never trusts the declared type or name).</summary>
public sealed record UploadedFile(Stream Content, long Length, string? FileName);

public sealed record CameraFile(Stream Content, string ContentType, string? FileName);

// ─── Zones ─────────────────────────────────────────────────────────────────

public sealed record CameraZoneDto(
    Guid Id,
    Guid CameraId,
    string Name,
    string ZoneType,
    CameraWorkstationRef? Workstation,
    WorkerLineRef? Line,
    IReadOnlyList<ZonePoint> Points,
    double Area,
    string? Color,
    string? Notes,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    string CreatedByEmail,
    DateTimeOffset UpdatedAt,
    string UpdatedByEmail);

public sealed record CreateCameraZoneRequest(
    string Name, string ZoneType, IReadOnlyList<ZonePoint> Points, Guid? WorkstationId = null, string? LineId = null,
    string? Color = null, string? Notes = null);

/// <summary>
/// PUT /api/camera-zones/{id}. <c>ExpectedVersion</c>, when sent, must match
/// the zone's current version (two people editing the same zone don't
/// silently overwrite each other).
/// </summary>
public sealed record UpdateCameraZoneRequest(
    string Name, string ZoneType, IReadOnlyList<ZonePoint> Points, Guid? WorkstationId, string? LineId, string? Color, string? Notes,
    bool IsActive, int? ExpectedVersion = null);

public sealed record CameraZoneRevisionDto(
    long Id, Guid ZoneId, int Version, string Action, string Name, string ZoneType, Guid? WorkstationId, string? LineId,
    IReadOnlyList<ZonePoint> Points, bool IsActive, bool DrawnOnCurrentFrame, DateTimeOffset ChangedAt, string ChangedByEmail);
