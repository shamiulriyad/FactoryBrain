using BunonBrain.Application.Dtos.Workers;

namespace BunonBrain.Application.Dtos.Devices;

public sealed record DeviceLinkRef(Guid Id, string Code, string? Name);

/// <summary>
/// A device with its health. <see cref="ConnectionStatus"/> is the effective
/// connection (a stored Online whose heartbeats stopped is Offline);
/// <see cref="PhysicallyConnected"/> is true only for a real device that is
/// online. A simulated device always says so in <see cref="HealthLabel"/>.
/// </summary>
public sealed record DeviceDto(
    Guid Id,
    string Code,
    string Name,
    string Category,
    bool IsSimulated,
    string Mode,
    string Status,
    string? StatusReason,
    string ConnectionStatus,
    string HealthStatus,
    string HealthLabel,
    bool PhysicallyConnected,
    bool HeartbeatOverdue,
    Guid FactoryId,
    string FactoryCode,
    WorkerLineRef? Line,
    DeviceLinkRef? Workstation,
    DeviceLinkRef? Machine,
    DeviceLinkRef? Camera,
    DeviceLinkRef? AttendanceReader,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    IReadOnlyDictionary<string, string> Metadata,
    DateOnly? InstalledOn,
    int HeartbeatIntervalSeconds,
    int OfflineAfterSeconds,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDisconnectedAt,
    DateTimeOffset? ConnectionChangedAt,
    DateTimeOffset? LastErrorAt,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// GET /api/devices. <c>health</c>: Online | Offline | Unknown | Maintenance | Disabled.
/// <c>sort</c>: code | name | category | lastSeen | health (prefix "-" for descending).
/// </summary>
public sealed record DeviceListQuery(
    string? Search = null, Guid? FactoryId = null, string? Category = null, string? Status = null, string? Health = null,
    bool? Simulated = null, string? LineId = null, Guid? CameraId = null, string? Sort = null, int Page = 1, int PageSize = 25);

public sealed record CreateDeviceRequest(
    Guid FactoryId,
    string Code,
    string Name,
    string Category,
    bool IsSimulated,
    string? LineId = null,
    Guid? WorkstationId = null,
    Guid? MachineId = null,
    Guid? CameraId = null,
    Guid? AttendanceReaderId = null,
    string? Manufacturer = null,
    string? Model = null,
    string? SerialNumber = null,
    string? FirmwareVersion = null,
    IReadOnlyDictionary<string, string?>? Metadata = null,
    DateOnly? InstalledOn = null,
    int? HeartbeatIntervalSeconds = null,
    int? OfflineAfterSeconds = null);

/// <summary>PUT /api/devices/{id}. The factory, code and simulated / real mode can't change.</summary>
public sealed record UpdateDeviceRequest(
    string Name,
    string Category,
    string? LineId,
    Guid? WorkstationId,
    Guid? MachineId,
    Guid? CameraId,
    Guid? AttendanceReaderId,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    IReadOnlyDictionary<string, string?>? Metadata,
    DateOnly? InstalledOn,
    int HeartbeatIntervalSeconds,
    int OfflineAfterSeconds);

/// <summary>PATCH /api/devices/{id}/status: Active, Maintenance or Disabled (a reason is required for the last two).</summary>
public sealed record SetDeviceStatusRequest(string Status, string? Reason = null);

// ─── Ingestion contract (real hardware, gateways, a future MQTT bridge, and the simulator) ───

/// <summary>
/// POST /api/devices/{id}/heartbeat. <c>OccurredAt</c> defaults to the
/// receive time. <c>MessageId</c> makes retries safe: the same id for the
/// same device is recorded once.
/// </summary>
public sealed record DeviceHeartbeatRequest(
    DateTimeOffset? OccurredAt = null, string? MessageId = null, string? FirmwareVersion = null, IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>POST /api/devices/{id}/events — a device reporting it came online, is going offline, or hit an error.</summary>
public sealed record DeviceReportRequest(
    string Kind, DateTimeOffset? OccurredAt = null, string? MessageId = null, string? Code = null, string? Message = null, string? Severity = null);

/// <summary>
/// POST /api/devices/ingest — the gateway form of the contract: the device is
/// named by factory + device code (as an MQTT topic
/// <c>bunonbrain/{factoryCode}/devices/{deviceCode}/{kind}</c> would name it).
/// </summary>
public sealed record DeviceIngestRequest(
    Guid FactoryId, string DeviceCode, string Kind, DateTimeOffset? OccurredAt = null, string? MessageId = null,
    string? FirmwareVersion = null, IReadOnlyDictionary<string, double>? Metrics = null, string? Code = null, string? Message = null, string? Severity = null);

/// <summary>One message of the shared contract, after the endpoint shape is removed.</summary>
public sealed record DeviceMessage(
    string Kind, DateTimeOffset? OccurredAt, string? MessageId, string? FirmwareVersion, IReadOnlyDictionary<string, double>? Metrics,
    string? Code, string? Message, string? Severity);

/// <summary><c>Outcome</c>: Accepted, or Duplicate (a repeated message id; nothing new was recorded).</summary>
public sealed record DeviceMessageResult(string Outcome, bool IsSimulated, long? HeartbeatId, IReadOnlyList<DeviceEventDto> Events, DeviceDto Device);

// ─── Simulator ─────────────────────────────────────────────────────────────

public sealed record SimulatorDeviceRequest(Guid DeviceId, string? Reason = null);

/// <summary><c>Count</c> heartbeats (1–20), spaced <c>IntervalSeconds</c> apart and ending now.</summary>
public sealed record SimulatorHeartbeatRequest(
    Guid DeviceId, int? Count = null, int? IntervalSeconds = null, string? FirmwareVersion = null, IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>An error from a simulated device. Without a code and message, a sample error for the device's category is used.</summary>
public sealed record SimulatorErrorRequest(Guid DeviceId, string? Code = null, string? Message = null, string? Severity = null);

public sealed record SimulatorBatchResult(int Accepted, IReadOnlyList<DeviceEventDto> Events, DeviceDto Device);

// ─── History & health ──────────────────────────────────────────────────────

public sealed record DeviceHeartbeatDto(
    long Id, Guid DeviceId, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string Source, bool IsSimulated, string? MessageId,
    string? FirmwareVersion, IReadOnlyDictionary<string, double>? Metrics, string RecordedByEmail);

public sealed record DeviceEventDto(
    long Id, Guid DeviceId, string DeviceCode, string EventType, string Severity, string Source, bool IsSimulated,
    DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string? Code, string? Message, string? FromState, string? ToState, string RecordedByEmail);

public sealed record DeviceEventQuery(
    Guid? FactoryId = null, Guid? DeviceId = null, string? EventType = null, string? Source = null, bool? Simulated = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int Page = 1, int PageSize = 50);

public sealed record CategoryHealthDto(string Category, int Total, int Online, int Offline, int Unknown, int Maintenance, int Disabled);

public sealed record DeviceHealthOverviewDto(
    DateTimeOffset AsOf,
    int Total,
    IReadOnlyDictionary<string, int> ByHealth,
    IReadOnlyList<CategoryHealthDto> ByCategory,
    int Real,
    int Simulated,
    int PhysicallyConnected,
    int SimulatedOnline,
    int HeartbeatOverdue,
    int ErrorsLast24Hours,
    IReadOnlyList<DeviceDto> NeedsAttention,
    IReadOnlyList<DeviceEventDto> RecentEvents);

public sealed record HealthCheckResult(int Checked, int MarkedOffline);
