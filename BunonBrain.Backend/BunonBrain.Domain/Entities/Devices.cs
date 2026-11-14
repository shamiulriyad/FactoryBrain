namespace BunonBrain.Domain.Entities;

// Devices: RFID readers, machine sensors, production counters, gateways and
// cameras, registered before (or without) any physical hardware. Health is
// heartbeat-based: a device that stops sending heartbeats is marked offline.
//
// Simulated and real devices are kept apart everywhere: a simulated device
// only accepts simulator messages, a real one only accepts device messages,
// and every heartbeat and event carries the flag. A simulated device is never
// reported as physically connected.
//
// Heartbeats and events are append-only facts (database triggers reject
// updates and deletes), the history the future AI and maintenance modules read.

/// <summary>A device of one factory. Never deleted once it has heartbeats or events; it is disabled instead.</summary>
public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>Upper case, unique within the factory (e.g. <c>RFID-GATE-1</c>).</summary>
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>One of <c>DeviceCategories</c>: RfidReader, MachineSensor, ProductionCounter, Esp32Gateway, IndustrialGateway, Camera, Other.</summary>
    public string Category { get; set; } = default!;
    /// <summary>True for a software-simulated device (demonstrations). Fixed at registration.</summary>
    public bool IsSimulated { get; set; }
    /// <summary>Administrative state, one of <c>DeviceStatuses</c>: Active, Maintenance, Disabled.</summary>
    public string Status { get; set; } = "Active";
    public string? StatusReason { get; set; }
    /// <summary>Last known connection, one of <c>ConnectionStatuses</c>: Online, Offline, Unknown (never heard from).</summary>
    public string ConnectionStatus { get; set; } = "Unknown";

    // Placement (all optional, all inside the device's factory).
    public string? LineId { get; set; }
    public Guid? WorkstationId { get; set; }
    public Guid? MachineId { get; set; }
    /// <summary>The camera this device reports health for (category Camera only). One device per camera.</summary>
    public Guid? CameraId { get; set; }
    /// <summary>The attendance reader this device is (category RfidReader only).</summary>
    public Guid? AttendanceReaderId { get; set; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    /// <summary>Free key → value pairs (JSON object of strings), e.g. MAC address, IP, MQTT client id.</summary>
    public string Metadata { get; set; } = "{}";
    public DateOnly? InstalledOn { get; set; }

    /// <summary>How often the device is expected to send a heartbeat.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    /// <summary>No heartbeat for this long ⇒ offline.</summary>
    public int OfflineAfterSeconds { get; set; } = 180;

    /// <summary>Last message of any kind (heartbeat, status, error).</summary>
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    /// <summary>Last time the device (re)connected: its last transition to online.</summary>
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastDisconnectedAt { get; set; }
    public DateTimeOffset? ConnectionChangedAt { get; set; }
    /// <summary>
    /// <see cref="LastSeenAt"/> + <see cref="OfflineAfterSeconds"/>: an Online
    /// device with nothing newer counts as offline after this instant. Stored
    /// so health filters are plain comparisons in SQL.
    /// </summary>
    public DateTimeOffset? OfflineDeadline { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One heartbeat as received. Append-only.</summary>
public class DeviceHeartbeat
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>When the device says it sent it (UTC).</summary>
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Device (real hardware) or Simulator.</summary>
    public string Source { get; set; } = default!;
    public bool IsSimulated { get; set; }
    /// <summary>Sender's message id; a repeated id for the same device is recorded once.</summary>
    public string? MessageId { get; set; }
    public string? FirmwareVersion { get; set; }
    /// <summary>Numeric readings sent with the heartbeat (JSON object), e.g. {"rssi":-61,"uptimeSeconds":3600}.</summary>
    public string? Metrics { get; set; }
    public string RecordedByEmail { get; set; } = default!;
}

/// <summary>
/// A device event: connection changes, errors, status changes, firmware
/// changes, missed heartbeats. Append-only. Connection history is the
/// ConnectionOnline / ConnectionOffline events.
/// </summary>
public class DeviceEvent
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>One of <c>DeviceEventTypes</c>.</summary>
    public string EventType { get; set; } = default!;
    /// <summary>Info, Warning or Error.</summary>
    public string Severity { get; set; } = "Info";
    /// <summary>Device, Simulator, System (health monitor) or Manual (a person).</summary>
    public string Source { get; set; } = default!;
    /// <summary>The event concerns a simulated device.</summary>
    public bool IsSimulated { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? MessageId { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? FromState { get; set; }
    public string? ToState { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string RecordedByEmail { get; set; } = default!;
}
