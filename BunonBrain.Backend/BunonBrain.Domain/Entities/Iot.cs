namespace BunonBrain.Domain.Entities;

// Machine monitoring, production counters and IoT integration.
//
// Reused, not duplicated: devices (registry, heartbeats, device events and
// health), machines and workstations, production lines, factory shifts and
// the monitoring events / alerts. What is added here:
//
//   * how a device talks to us (protocol, gateway, device key) and which
//     kind of sensor it is: DeviceIotConfiguration;
//   * raw machine telemetry as received (append-only, kept for a retention
//     period): MachineTelemetry;
//   * the machine state derived from valid telemetry and device health
//     (current row + append-only transitions): MachineState,
//     MachineStateTransition;
//   * per-machine counting and alert rules: MachineMonitoringProfile;
//   * production counter events (append-only, idempotent), the counter's
//     running snapshot, and audited corrections: ProductionCounterEvent,
//     ProductionCounterSnapshot, ProductionCorrection.
//
// Raw events never change. Simulated and real data are kept apart: every
// row says SIMULATED or REAL, and simulated devices only accept the simulator.
// Machine running time is machine data: it is never worker working time,
// and a production count is machine output, not an individual's output.

/// <summary>
/// The IoT side of a registered device: which kind of sensor it is, how it
/// connects, and its device key (stored hashed; shown once when issued).
/// One row per device; created the first time the device is configured.
/// </summary>
public class DeviceIotConfiguration
{
    public Guid DeviceId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>One of <c>IotDeviceTypes</c>, e.g. SEWING_MACHINE_SENSOR, PIECE_COUNTER.</summary>
    public string DeviceType { get; set; } = default!;
    /// <summary>MQTT, HTTP or SIMULATOR.</summary>
    public string Protocol { get; set; } = default!;
    /// <summary>DIRECT, ESP32 or GATEWAY: what sits between the sensor and the broker / API.</summary>
    public string ConnectionVia { get; set; } = "DIRECT";
    /// <summary>The gateway device (ESP32 / industrial gateway) this sensor reports through, in the same factory.</summary>
    public Guid? GatewayDeviceId { get; set; }
    /// <summary>The MQTT client id / broker username the device uses (for the broker's ACL; never a password).</summary>
    public string? MqttClientId { get; set; }
    /// <summary>How often the device is expected to send telemetry (informational, and for the dashboard).</summary>
    public int? TelemetryIntervalSeconds { get; set; }
    /// <summary>Non-secret settings (JSON object of strings), e.g. Modbus register, GPIO pin, debounce ms.</summary>
    public string Settings { get; set; } = "{}";
    /// <summary>SHA-256 of the device key (HTTP device authentication). Null until a key is issued.</summary>
    public string? KeyHash { get; set; }
    /// <summary>First characters of the key, to recognise it ("dk_3f9a…").</summary>
    public string? KeyPrefix { get; set; }
    public DateTimeOffset? KeyIssuedAt { get; set; }
    public string? KeyIssuedByEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedByEmail { get; set; } = default!;
    public int Version { get; set; } = 1;
}

/// <summary>
/// Counting and alert rules of one machine. Defaults depend on the kind of
/// machine; every threshold is optional (null switches that rule off).
/// </summary>
public class MachineMonitoringProfile
{
    public Guid MachineId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>SEWING, CUTTING, PRESSING or OTHER.</summary>
    public string MachineKind { get; set; } = "OTHER";
    /// <summary>PIECE (one counted unit = one piece), PULSES_PER_PIECE, or CYCLE_NOT_PIECE (cycles are recorded, never counted as garments).</summary>
    public string CountingMode { get; set; } = "PIECE";
    public int PulsesPerPiece { get; set; } = 1;
    /// <summary>Power at or above this ⇒ RUNNING (when the sensor sends power but no state).</summary>
    public double? RunningPowerWatts { get; set; }
    /// <summary>Power at or above this (and below running) ⇒ IDLE; below ⇒ STOPPED.</summary>
    public double? IdlePowerWatts { get; set; }
    /// <summary>No telemetry for this long while the device is online ⇒ UNKNOWN. Null: the last state holds.</summary>
    public int? StaleAfterSeconds { get; set; } = 900;
    /// <summary>STOPPED this long during a scheduled shift (not in a break) ⇒ unexpected stoppage alert.</summary>
    public int? StopAlertAfterSeconds { get; set; } = 600;
    public int? LongIdleAlertSeconds { get; set; } = 1200;
    public double? MaxTemperatureC { get; set; }
    public double? MaxVibrationMmS { get; set; }
    /// <summary>RUNNING with no production count for this long ⇒ counter disconnected (machines with a counter only).</summary>
    public int? CounterSilenceAlertSeconds { get; set; } = 900;
    /// <summary>This many state changes within 5 minutes ⇒ unusual transitions (flapping sensor or machine).</summary>
    public int? FlapTransitionsPer5Min { get; set; } = 12;
    public bool AlertsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedByEmail { get; set; } = default!;
    public int Version { get; set; } = 1;
}

/// <summary>
/// The current, derived state of one machine. Rebuilt only from valid
/// telemetry, device health and maintenance decisions; the raw telemetry
/// stays in <see cref="MachineTelemetry"/>.
/// </summary>
public class MachineState
{
    public Guid MachineId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>One of <c>MachineStates</c>: RUNNING, STOPPED, IDLE, MAINTENANCE, ERROR, OFFLINE, UNKNOWN.</summary>
    public string State { get; set; } = "UNKNOWN";
    public DateTimeOffset Since { get; set; }
    public string Reason { get; set; } = default!;
    public string Trigger { get; set; } = default!;
    /// <summary>SIMULATED or REAL: where the evidence for the current state came from.</summary>
    public string SourceType { get; set; } = "REAL";
    public Guid? SourceDeviceId { get; set; }
    /// <summary>Time of the newest telemetry that was applied (older messages arriving later never move the state back).</summary>
    public DateTimeOffset? LastStateEventAt { get; set; }
    public DateTimeOffset? LastTelemetryAt { get; set; }
    public DateTimeOffset? LastCountAt { get; set; }
    public double? PowerWatts { get; set; }
    public double? CurrentAmps { get; set; }
    public double? TemperatureC { get; set; }
    public double? VibrationMmS { get; set; }
    public DateTimeOffset? ReadingsAt { get; set; }
    public string? ErrorCode { get; set; }
    public bool InMaintenance { get; set; }
    public string? MaintenanceReason { get; set; }
    public DateTimeOffset? MaintenanceSince { get; set; }
    public string? MaintenanceByEmail { get; set; }
    /// <summary>Conditions currently raised (JSON array of <c>MachineConditions</c>), e.g. ["ABNORMAL_TEMPERATURE"].</summary>
    public string ActiveConditions { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Version { get; set; } = 1;
}

/// <summary>One change of a machine's state. Append-only; a state lasts until the next transition.</summary>
public class MachineStateTransition
{
    public long Id { get; set; }
    public Guid MachineId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>Placement at the time (the machine may move later).</summary>
    public string? LineId { get; set; }
    public Guid? WorkstationId { get; set; }
    public Guid? DeviceId { get; set; }
    public string? FromState { get; set; }
    public string ToState { get; set; } = default!;
    public string Reason { get; set; } = default!;
    /// <summary>TELEMETRY, DEVICE_OFFLINE, DEVICE_ONLINE, NO_TELEMETRY, MAINTENANCE_START, MAINTENANCE_END.</summary>
    public string Trigger { get; set; } = default!;
    public string SourceType { get; set; } = default!;
    public long? TelemetryId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public string RecordedByEmail { get; set; } = default!;
}

/// <summary>
/// A telemetry message as received from a machine sensor (MQTT, HTTP or the
/// simulator). Never updated; deleted only by the retention job after
/// <c>Iot:TelemetryRetentionDays</c>.
/// </summary>
public class MachineTelemetry
{
    public long Id { get; set; }
    public Guid FactoryId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? MachineId { get; set; }
    public Guid? WorkstationId { get; set; }
    public string? LineId { get; set; }
    /// <summary>State the sensor reported, if any.</summary>
    public string? ReportedState { get; set; }
    public double? PowerWatts { get; set; }
    public double? CurrentAmps { get; set; }
    public double? TemperatureC { get; set; }
    public double? VibrationMmS { get; set; }
    public string? ErrorCode { get; set; }
    /// <summary>Other numeric readings (JSON object), e.g. {"rpm":3500}.</summary>
    public string? Metrics { get; set; }
    public string? MessageId { get; set; }
    public string SourceType { get; set; } = default!;
    /// <summary>MQTT, HTTP or SIMULATOR.</summary>
    public string Transport { get; set; } = default!;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>APPLIED (used for the machine state); stored only: LATE (older than the state), NO_MACHINE, MAINTENANCE, SOURCE_MISMATCH (simulated data for a machine with real sensors).</summary>
    public string Outcome { get; set; } = "APPLIED";
}

/// <summary>
/// One message of a production counter, with what it was counted as.
/// Append-only and stored once per idempotency key; corrections are separate
/// rows (<see cref="ProductionCorrection"/>).
/// </summary>
public class ProductionCounterEvent
{
    public long Id { get; set; }
    public Guid FactoryId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? MachineId { get; set; }
    public Guid? WorkstationId { get; set; }
    public string LineId { get; set; } = default!;
    public Guid? DepartmentId { get; set; }
    public Guid? FloorId { get; set; }
    /// <summary>INCREMENT, ABSOLUTE or RESET, as the device sent it.</summary>
    public string CounterMode { get; set; } = default!;
    /// <summary>The value the device sent (an increment, or the counter's absolute value).</summary>
    public long RawValue { get; set; }
    /// <summary>Counter pulses this message added.</summary>
    public long Pulses { get; set; }
    /// <summary>Pieces counted from it under the machine's counting rule.</summary>
    public int Pieces { get; set; }
    /// <summary>COUNTED, BASELINE, RESET, OUT_OF_ORDER or SUSPECT_JUMP.</summary>
    public string Status { get; set; } = default!;
    public string? Note { get; set; }
    /// <summary>The counting rule applied (copied, so history stays true if the rule changes).</summary>
    public string CountingMode { get; set; } = default!;
    public int PulsesPerPiece { get; set; }
    public Guid? ShiftId { get; set; }
    public string? ShiftCode { get; set; }
    /// <summary>Factory-local production date (the date the shift started).</summary>
    public DateOnly ProductionDate { get; set; }
    public string SourceType { get; set; } = default!;
    /// <summary>MQTT, HTTP, SIMULATOR or MANUAL (a counter reset by a person).</summary>
    public string Transport { get; set; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public string? MessageId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string RecordedByEmail { get; set; } = default!;
}

/// <summary>The running state of one counter device: last absolute value, carried pulses, totals.</summary>
public class ProductionCounterSnapshot
{
    public Guid DeviceId { get; set; }
    public Guid FactoryId { get; set; }
    public Guid? MachineId { get; set; }
    public long? LastValue { get; set; }
    public DateTimeOffset? LastOccurredAt { get; set; }
    /// <summary>Pulses not yet making a whole piece (PULSES_PER_PIECE).</summary>
    public int PulseRemainder { get; set; }
    public long TotalPulses { get; set; }
    public long TotalPieces { get; set; }
    public DateTimeOffset? LastResetAt { get; set; }
    public DateTimeOffset? LastCountAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Version { get; set; } = 1;
}

/// <summary>A person's correction of a production count (±pieces), with the reason. Append-only and audited.</summary>
public class ProductionCorrection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public Guid? MachineId { get; set; }
    public Guid? WorkstationId { get; set; }
    public string LineId { get; set; } = default!;
    public DateOnly ProductionDate { get; set; }
    public Guid? ShiftId { get; set; }
    public string? ShiftCode { get; set; }
    /// <summary>Hour (UTC) the correction belongs to, when it corrects a specific hour.</summary>
    public DateTimeOffset? HourStart { get; set; }
    public int PiecesDelta { get; set; }
    public string Reason { get; set; } = default!;
    /// <summary>The data the correction applies to: SIMULATED or REAL.</summary>
    public string SourceType { get; set; } = default!;
    public Guid CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public string? CreatedByRole { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
