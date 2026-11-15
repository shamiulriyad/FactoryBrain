using System.Text.Json;
using BunonBrain.Application.Dtos.Monitoring;

namespace BunonBrain.Application.Dtos.Iot;

// ─── Ingestion ─────────────────────────────────────────────────────────────

/// <summary>Accepted (stored now), Duplicate (the message id was seen before) or Rejected (stored nowhere; the reason is in Code).</summary>
public sealed record IotIngestResult(
    string Outcome,
    string Type,
    Guid? DeviceId,
    string? DeviceCode,
    bool IsSimulated,
    Guid? MachineId,
    string? MachineState,
    long? TelemetryId,
    long? CounterEventId,
    string? CountStatus,
    int? Pieces,
    IReadOnlyList<string> Notes,
    string? Code = null,
    string? Error = null);

// ─── Configuration ─────────────────────────────────────────────────────────

/// <summary>PUT /api/iot/devices/{id}/config. Placement (line / workstation / machine) stays on the device itself.</summary>
public sealed record DeviceIotConfigRequest(
    string DeviceType,
    string Protocol,
    string? ConnectionVia = null,
    Guid? GatewayDeviceId = null,
    string? MqttClientId = null,
    int? TelemetryIntervalSeconds = null,
    IReadOnlyDictionary<string, string?>? Settings = null,
    int? ExpectedVersion = null);

public sealed record DeviceKeyIssued(Guid DeviceId, string DeviceCode, string Key, string KeyPrefix, DateTimeOffset IssuedAt, string Note);

/// <summary>PUT /api/iot/machines/{id}/profile. Null thresholds switch that rule off.</summary>
public sealed record MachineProfileRequest(
    string MachineKind,
    string CountingMode,
    int PulsesPerPiece,
    double? RunningPowerWatts,
    double? IdlePowerWatts,
    int? StaleAfterSeconds,
    int? StopAlertAfterSeconds,
    int? LongIdleAlertSeconds,
    double? MaxTemperatureC,
    double? MaxVibrationMmS,
    int? CounterSilenceAlertSeconds,
    int? FlapTransitionsPer5Min,
    bool AlertsEnabled,
    int? ExpectedVersion = null);

public sealed record MachineProfileDto(
    Guid MachineId, string MachineKind, string CountingMode, int PulsesPerPiece, double? RunningPowerWatts, double? IdlePowerWatts,
    int? StaleAfterSeconds, int? StopAlertAfterSeconds, int? LongIdleAlertSeconds, double? MaxTemperatureC, double? MaxVibrationMmS,
    int? CounterSilenceAlertSeconds, int? FlapTransitionsPer5Min, bool AlertsEnabled, bool IsDefault, DateTimeOffset? UpdatedAt,
    string? UpdatedByEmail, int Version);

public sealed record MaintenanceRequest(bool InMaintenance, string? Reason);

/// <summary>POST /api/iot/simulator/setup: register simulated sensor (and counter) devices on a machine.</summary>
public sealed record SimulatedMachineSetupRequest(Guid MachineId, bool WithCounter = true);

public sealed record SimulatedMachineSetupResult(Guid MachineId, IReadOnlyList<DeviceIotDto> Devices, IReadOnlyList<string> Notes);

// ─── Production corrections ────────────────────────────────────────────────

public sealed record CounterResetRequest(Guid DeviceId, long? Value, string Reason);

public sealed record ProductionCorrectionRequest(
    Guid FactoryId,
    string? LineId,
    Guid? WorkstationId,
    Guid? MachineId,
    DateOnly ProductionDate,
    Guid? ShiftId,
    DateTimeOffset? HourStart,
    int PiecesDelta,
    string Reason,
    string? SourceType = null);

public sealed record ProductionCorrectionDto(
    Guid Id, Guid FactoryId, MonLine? Line, MonRef? Workstation, MonRef? Machine, DateOnly ProductionDate, string? ShiftCode,
    DateTimeOffset? HourStart, int PiecesDelta, string Reason, string SourceType, string CreatedByEmail, string? CreatedByRole, DateTimeOffset CreatedAt);

// ─── Queries ───────────────────────────────────────────────────────────────

/// <summary>Filters shared by the machine and production views. <c>Source</c>: REAL (default) or SIMULATED — never mixed.</summary>
public sealed record IotFilter(
    Guid? FactoryId = null,
    Guid? DepartmentId = null,
    Guid? FloorId = null,
    string? LineId = null,
    Guid? WorkstationId = null,
    Guid? MachineId = null,
    Guid? DeviceId = null,
    Guid? ShiftId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Source = null,
    string? State = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50);

public sealed record MachineDeviceDto(
    Guid Id, string Code, string Name, string Category, string DeviceType, string DeviceTypeLabel, string Protocol, bool IsSimulated,
    string Health, string HealthLabel, DateTimeOffset? LastHeartbeatAt, DateTimeOffset? LastSeenAt, string? FirmwareVersion, bool IsCounter, bool DecidesState);

public sealed record MachineDto(
    Guid Id,
    string Code,
    string? MachineType,
    string MachineKind,
    Guid FactoryId,
    string FactoryCode,
    MonRef? Workstation,
    MonLine? Line,
    string? DepartmentName,
    string? FloorName,
    bool IsActive,
    bool Monitored,
    string State,
    DateTimeOffset? Since,
    string? Reason,
    string? SourceType,
    bool IsSimulated,
    double? PowerWatts,
    double? CurrentAmps,
    double? TemperatureC,
    double? VibrationMmS,
    DateTimeOffset? ReadingsAt,
    DateTimeOffset? LastTelemetryAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastCountAt,
    string? ErrorCode,
    bool InMaintenance,
    string? MaintenanceReason,
    IReadOnlyList<string> ActiveConditions,
    IReadOnlyList<MachineDeviceDto> Devices);

public sealed record MachineCounts(int Total, int Running, int Stopped, int Idle, int Offline, int Error, int Maintenance, int Unknown, int Unmonitored);

public sealed record GroupCount(string Key, string Label, long Pieces, double? PiecesPerHour);

public sealed record BucketCount(DateTimeOffset Start, string Label, long Pieces);

public sealed record MachineMetricsDto(
    Guid MachineId, string Code, MonLine? Line, MonRef? Workstation, long Pieces, double RunningSeconds, double IdleSeconds,
    double DowntimeSeconds, double StoppedSeconds, double ErrorSeconds, double OfflineSeconds, double UnknownSeconds, double MaintenanceSeconds,
    double? PiecesPerRunningHour, double? RunningShare);

public sealed record ProductionReportDto(
    DateTimeOffset From,
    DateTimeOffset To,
    string Source,
    string TimeZone,
    long TotalPieces,
    long CountedPieces,
    long CorrectionPieces,
    double? PiecesPerHour,
    IReadOnlyList<GroupCount> ByLine,
    IReadOnlyList<GroupCount> ByWorkstation,
    IReadOnlyList<GroupCount> ByMachine,
    IReadOnlyList<GroupCount> ByShift,
    IReadOnlyList<BucketCount> Hourly,
    IReadOnlyList<BucketCount> Daily,
    IReadOnlyList<MachineMetricsDto> Machines,
    double RunningSeconds,
    double IdleSeconds,
    double DowntimeSeconds,
    string Note);

public sealed record RecentDeviceEventDto(long Id, Guid DeviceId, string DeviceCode, string EventType, string Severity, string? Message, bool IsSimulated, DateTimeOffset OccurredAt);

public sealed record IotBridgeStatusDto(
    bool Enabled, bool Connected, string? Broker, bool Tls, string ClientId, string TopicPrefix, IReadOnlyList<string> Subscriptions,
    DateTimeOffset? ConnectedSince, DateTimeOffset? LastMessageAt, long Received, long Accepted, long Duplicates, long Rejected,
    string? LastError, DateTimeOffset? LastErrorAt, int Reconnects, string Note);

public sealed record MachineOverviewDto(
    DateTimeOffset AsOf,
    string Source,
    MachineCounts Counts,
    IReadOnlyList<MachineDto> Machines,
    long PiecesToday,
    IReadOnlyList<GroupCount> ProductionByLine,
    IReadOnlyList<GroupCount> ProductionByShift,
    double DowntimeSecondsToday,
    IReadOnlyList<RecentDeviceEventDto> RecentDeviceEvents,
    IReadOnlyList<MonitoringAlertDto> Alerts,
    IotBridgeStatusDto Bridge,
    bool CanSeeProduction,
    string Note);

public sealed record StateTransitionDto(
    long Id, Guid MachineId, string MachineCode, string? FromState, string ToState, string Reason, string Trigger, string SourceType,
    string? DeviceCode, DateTimeOffset OccurredAt, double? DurationSeconds);

public sealed record TelemetryDto(
    long Id, Guid DeviceId, string DeviceCode, string? ReportedState, double? PowerWatts, double? CurrentAmps, double? TemperatureC,
    double? VibrationMmS, string? ErrorCode, JsonElement? Metrics, string SourceType, string Transport, string Outcome, string? MessageId,
    DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt);

public sealed record CounterEventDto(
    long Id, Guid DeviceId, string DeviceCode, MonRef? Machine, MonRef? Workstation, MonLine? Line, string CounterMode, long RawValue,
    long Pulses, int Pieces, string Status, string? Note, string? ShiftCode, DateOnly ProductionDate, string SourceType, string Transport,
    string? MessageId, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt);

/// <summary>
/// What else is known about the machine's workstation, kept apart from the
/// machine data: the scheduled assignment and a verified identity (RFID +
/// camera) if there is one. Neither proves who operated the machine.
/// </summary>
public sealed record MachineContextDto(
    MonRef? AssignedWorker, DateTimeOffset? AssignedSince, MonRef? VerifiedWorker, string? VerifiedCamera, string? VerifiedTrack,
    DateTimeOffset? VerifiedAt, string Note);

public sealed record MachineDetailDto(
    MachineDto Machine,
    MachineProfileDto Profile,
    MachineMetricsDto Today,
    IReadOnlyList<StateTransitionDto> RecentTransitions,
    IReadOnlyList<TelemetryDto> RecentTelemetry,
    IReadOnlyList<CounterEventDto> RecentCounts,
    IReadOnlyList<MonitoringAlertDto> Alerts,
    MachineContextDto? Context,
    bool CanConfigure,
    bool CanCorrect,
    bool CanSeeProduction);

public sealed record DeviceIotDto(
    Guid DeviceId,
    string Code,
    string Name,
    string Category,
    Guid FactoryId,
    string FactoryCode,
    bool IsSimulated,
    bool Configured,
    string DeviceType,
    string DeviceTypeLabel,
    string Protocol,
    string ConnectionVia,
    MonRef? Gateway,
    string? MqttClientId,
    int? TelemetryIntervalSeconds,
    IReadOnlyDictionary<string, string> Settings,
    MonRef? Machine,
    MonRef? Workstation,
    MonLine? Line,
    string Status,
    string Health,
    string HealthLabel,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastSeenAt,
    string? FirmwareVersion,
    DateTimeOffset RegisteredAt,
    bool HasKey,
    string? KeyPrefix,
    DateTimeOffset? KeyIssuedAt,
    IReadOnlyList<string> Topics,
    long MessagesLastHour,
    int Version);

// ─── Simulator ─────────────────────────────────────────────────────────────

/// <summary>
/// POST /api/iot/simulator/send. Target: one machine, a workstation, a line
/// or a whole factory (its simulated devices). Scenarios: RUNNING, STOPPED,
/// IDLE, ERROR, OFFLINE, ONLINE, HEARTBEAT, PIECES, COUNTER_RESET,
/// TEMPERATURE, VIBRATION, DUPLICATE, DELAYED, INVALID.
/// </summary>
public sealed record IotSimulationRequest(
    string Scenario,
    Guid FactoryId,
    Guid? MachineId = null,
    Guid? WorkstationId = null,
    string? LineId = null,
    int? Count = null,
    double? Value = null,
    int? DelaySeconds = null);

public sealed record IotSimulatedMessage(string DeviceCode, string? MachineCode, string Channel, string Topic, string Payload, string Outcome, string? Code, string? Error);

public sealed record IotSimulationResult(string Scenario, int Machines, IReadOnlyList<IotSimulatedMessage> Messages, IReadOnlyList<string> Notes);

/// <summary>POST /api/iot/simulator/runs: continuous simulated telemetry and counts for a while (at most 2 hours).</summary>
public sealed record IotSimulationRunRequest(
    Guid FactoryId,
    Guid? MachineId = null,
    Guid? WorkstationId = null,
    string? LineId = null,
    int DurationMinutes = 10,
    int IntervalSeconds = 5,
    string? Profile = null);

public sealed record IotSimulationRunDto(
    Guid Id, Guid FactoryId, string Target, string Profile, string Status, DateTimeOffset StartedAt, DateTimeOffset EndsAt,
    DateTimeOffset? EndedAt, int IntervalSeconds, int Machines, long MessagesSent, long MessagesRejected, string? LastError, string StartedByEmail);
