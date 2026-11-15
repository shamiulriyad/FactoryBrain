using BunonBrain.Application.Devices;

namespace BunonBrain.Application.Iot;

// The vocabulary of machine monitoring and IoT integration. Pure: no
// database, shared by the MQTT bridge, the HTTP ingestion API, the simulator
// and the tests.

/// <summary>Derived machine states.</summary>
public static class MachineStates
{
    public const string Running = "RUNNING";
    public const string Stopped = "STOPPED";
    public const string Idle = "IDLE";
    public const string Maintenance = "MAINTENANCE";
    public const string Error = "ERROR";
    /// <summary>The machine's sensors can't be reached: never read as STOPPED.</summary>
    public const string Offline = "OFFLINE";
    /// <summary>Reachable, but nothing reliable says what the machine is doing.</summary>
    public const string Unknown = "UNKNOWN";

    public static readonly IReadOnlyList<string> All = new[] { Running, Stopped, Idle, Maintenance, Error, Offline, Unknown };

    /// <summary>States a sensor may report directly.</summary>
    public static readonly IReadOnlyList<string> Reportable = new[] { Running, Stopped, Idle, Error, Maintenance };

    /// <summary>Downtime = stopped or in error (maintenance, offline and unknown are counted separately).</summary>
    public static readonly IReadOnlyList<string> Downtime = new[] { Stopped, Error };

    public static string? Parse(string? v) => Lookup.Parse(All, v);
}

/// <summary>What kind of sensor a device is. Each maps onto an existing device category.</summary>
public static class IotDeviceTypes
{
    public const string SewingMachineSensor = "SEWING_MACHINE_SENSOR";
    public const string RunStopSensor = "RUN_STOP_SENSOR";
    public const string PowerSensor = "POWER_SENSOR";
    public const string PieceCounter = "PIECE_COUNTER";
    public const string CuttingMachineSensor = "CUTTING_MACHINE_SENSOR";
    public const string PressingMachineSensor = "PRESSING_MACHINE_SENSOR";
    public const string RfidReader = "RFID_READER";
    public const string Esp32 = "ESP32";
    public const string IndustrialGateway = "INDUSTRIAL_GATEWAY";
    public const string MqttDevice = "MQTT_DEVICE";
    public const string GenericSensor = "GENERIC_SENSOR";

    public static readonly IReadOnlyList<string> All = new[]
    {
        SewingMachineSensor, RunStopSensor, PowerSensor, PieceCounter, CuttingMachineSensor, PressingMachineSensor,
        RfidReader, Esp32, IndustrialGateway, MqttDevice, GenericSensor,
    };

    /// <summary>Types whose telemetry decides a machine's state.</summary>
    public static readonly IReadOnlyList<string> MachineState = new[]
    {
        SewingMachineSensor, RunStopSensor, PowerSensor, CuttingMachineSensor, PressingMachineSensor, Esp32, IndustrialGateway, MqttDevice, GenericSensor,
    };

    /// <summary>Types that may send production counts.</summary>
    public static readonly IReadOnlyList<string> Counters = new[] { PieceCounter, Esp32, IndustrialGateway, MqttDevice };

    /// <summary>Types that can forward other devices' messages.</summary>
    public static readonly IReadOnlyList<string> Gateways = new[] { Esp32, IndustrialGateway };

    public static string? Parse(string? v) => Lookup.Parse(All, v);

    /// <summary>The device registry category a type belongs to.</summary>
    public static string Category(string type) => type switch
    {
        PieceCounter => DeviceCategories.ProductionCounter,
        RfidReader => DeviceCategories.RfidReader,
        Esp32 => DeviceCategories.Esp32Gateway,
        IndustrialGateway => DeviceCategories.IndustrialGateway,
        MqttDevice or GenericSensor => DeviceCategories.Other,
        _ => DeviceCategories.MachineSensor,
    };

    /// <summary>Whether a device of <paramref name="category"/> can be configured as <paramref name="type"/>.</summary>
    public static bool FitsCategory(string type, string category) =>
        Category(type) == category
        // A generic sensor or MQTT device may also be registered as a machine sensor.
        || (type is GenericSensor or MqttDevice && category == DeviceCategories.MachineSensor);

    /// <summary>The type assumed for a device that has no IoT configuration yet.</summary>
    public static string Default(string category) => category switch
    {
        DeviceCategories.ProductionCounter => PieceCounter,
        DeviceCategories.RfidReader => RfidReader,
        DeviceCategories.Esp32Gateway => Esp32,
        DeviceCategories.IndustrialGateway => IndustrialGateway,
        DeviceCategories.MachineSensor => SewingMachineSensor,
        _ => GenericSensor,
    };

    public static string Label(string type) => type switch
    {
        SewingMachineSensor => "Sewing machine sensor",
        RunStopSensor => "Machine running / stopped sensor",
        PowerSensor => "Machine power sensor",
        PieceCounter => "Production piece counter",
        CuttingMachineSensor => "Cutting machine sensor",
        PressingMachineSensor => "Ironing / pressing machine sensor",
        RfidReader => "RFID reader",
        Esp32 => "ESP32 device",
        IndustrialGateway => "Industrial gateway",
        MqttDevice => "MQTT-connected device",
        _ => "Generic IoT sensor",
    };
}

/// <summary>How a device is configured to talk to us.</summary>
public static class IotProtocols
{
    /// <summary>Preferred for continuous telemetry: the device publishes to the broker the backend subscribes to.</summary>
    public const string Mqtt = "MQTT";
    /// <summary>The device (or its gateway) posts to the HTTP device API with its device key.</summary>
    public const string Http = "HTTP";
    /// <summary>A simulated device: only the IoT simulator sends for it.</summary>
    public const string Simulator = "SIMULATOR";
    public static readonly IReadOnlyList<string> All = new[] { Mqtt, Http, Simulator };
    public static string? Parse(string? v) => Lookup.Parse(All, v);
}

/// <summary>What sits between the sensor and the broker / API.</summary>
public static class IotConnectionVia
{
    public const string Direct = "DIRECT";
    public const string Esp32 = "ESP32";
    public const string Gateway = "GATEWAY";
    public static readonly IReadOnlyList<string> All = new[] { Direct, Esp32, Gateway };
    public static string? Parse(string? v) => Lookup.Parse(All, v);
}

/// <summary>How one message actually arrived.</summary>
public static class IotTransports
{
    public const string Mqtt = "MQTT";
    public const string Http = "HTTP";
    public const string Simulator = "SIMULATOR";
    /// <summary>A person (counter reset from the dashboard).</summary>
    public const string Manual = "MANUAL";
    public static readonly IReadOnlyList<string> All = new[] { Mqtt, Http, Simulator, Manual };
}

/// <summary>Message kinds of the device contract.</summary>
public static class IotMessageTypes
{
    public const string Telemetry = "TELEMETRY";
    public const string Status = "STATUS";
    public const string Heartbeat = "HEARTBEAT";
    public const string Production = "PRODUCTION";
    public static readonly IReadOnlyList<string> All = new[] { Telemetry, Status, Heartbeat, Production };
    public static string? Parse(string? v) => Lookup.Parse(All, v);
}

/// <summary>Device-reported connection status (STATUS messages; also the device's MQTT last will).</summary>
public static class IotDeviceStatuses
{
    public const string Online = "ONLINE";
    public const string Offline = "OFFLINE";
    public const string Error = "ERROR";
    public static readonly IReadOnlyList<string> All = new[] { Online, Offline, Error };
    public static string? Parse(string? v) => Lookup.Parse(All, v);
}

/// <summary>How a counter reports.</summary>
public static class CounterModes
{
    /// <summary>"This many more pulses" (order-independent).</summary>
    public const string Increment = "INCREMENT";
    /// <summary>"The counter now reads N" (cumulative; the difference is counted).</summary>
    public const string Absolute = "ABSOLUTE";
    /// <summary>"The counter was reset to N (default 0)": counts nothing.</summary>
    public const string Reset = "RESET";
    public static readonly IReadOnlyList<string> All = new[] { Increment, Absolute, Reset };
    public static string? Parse(string? v) => Lookup.Parse(All, v);
}

/// <summary>What one counter unit means, per machine (not every pulse is a finished garment).</summary>
public static class CountingModes
{
    public const string Piece = "PIECE";
    public const string PulsesPerPiece = "PULSES_PER_PIECE";
    /// <summary>The counter counts machine cycles (stitch bursts, press strokes): recorded, never counted as pieces.</summary>
    public const string CycleNotPiece = "CYCLE_NOT_PIECE";
    public static readonly IReadOnlyList<string> All = new[] { Piece, PulsesPerPiece, CycleNotPiece };
    public static string? Parse(string? v) => Lookup.Parse(All, v);
}

/// <summary>What a counter message was counted as.</summary>
public static class CountStatuses
{
    public const string Counted = "COUNTED";
    /// <summary>First absolute reading: sets the baseline, counts nothing.</summary>
    public const string Baseline = "BASELINE";
    public const string Reset = "RESET";
    /// <summary>An absolute reading older than the newest one: already included, counts nothing.</summary>
    public const string OutOfOrder = "OUT_OF_ORDER";
    /// <summary>An implausible jump: stored, counts nothing, needs a person to correct.</summary>
    public const string SuspectJump = "SUSPECT_JUMP";
    public static readonly IReadOnlyList<string> All = new[] { Counted, Baseline, Reset, OutOfOrder, SuspectJump };
}

public static class MachineKinds
{
    public const string Sewing = "SEWING";
    public const string Cutting = "CUTTING";
    public const string Pressing = "PRESSING";
    public const string Other = "OTHER";
    public static readonly IReadOnlyList<string> All = new[] { Sewing, Cutting, Pressing, Other };
    public static string? Parse(string? v) => Lookup.Parse(All, v);

    /// <summary>A guess from the machine's free-text type ("Single needle lockstitch" ⇒ SEWING).</summary>
    public static string Guess(string? machineType)
    {
        var t = (machineType ?? "").ToLowerInvariant();
        if (t.Contains("cut")) return Cutting;
        if (t.Contains("iron") || t.Contains("press") || t.Contains("fusing")) return Pressing;
        if (t.Contains("sew") || t.Contains("stitch") || t.Contains("lock") || t.Contains("overlock") || t.Contains("needle") || t.Contains("flat")) return Sewing;
        return Other;
    }
}

/// <summary>Conditions the machine alert rules raise (and clear).</summary>
public static class MachineConditions
{
    public const string UnexpectedStop = "UNEXPECTED_STOP";
    public const string LongIdle = "LONG_IDLE";
    public const string AbnormalTemperature = "ABNORMAL_TEMPERATURE";
    public const string HighVibration = "HIGH_VIBRATION";
    public const string CounterDisconnected = "COUNTER_DISCONNECTED";
    public const string UnusualTransition = "UNUSUAL_TRANSITION";
    public static readonly IReadOnlyList<string> All = new[] { UnexpectedStop, LongIdle, AbnormalTemperature, HighVibration, CounterDisconnected, UnusualTransition };
    public static string? Parse(string? v) => Lookup.Parse(All, v);

    public static string Label(string c) => c switch
    {
        UnexpectedStop => "Unexpected stoppage",
        LongIdle => "Long idle",
        AbnormalTemperature => "Abnormal temperature",
        HighVibration => "High vibration",
        CounterDisconnected => "Production counter disconnected",
        UnusualTransition => "Unusual state transitions",
        _ => c,
    };
}

/// <summary>What changed a machine's state.</summary>
public static class StateTriggers
{
    public const string Telemetry = "TELEMETRY";
    public const string DeviceOffline = "DEVICE_OFFLINE";
    public const string DeviceOnline = "DEVICE_ONLINE";
    public const string NoTelemetry = "NO_TELEMETRY";
    public const string MaintenanceStart = "MAINTENANCE_START";
    public const string MaintenanceEnd = "MAINTENANCE_END";
}

public static class IotSourceTypes
{
    public const string Real = "REAL";
    public const string Simulated = "SIMULATED";
    public static string Of(bool simulated) => simulated ? Simulated : Real;
    public static string? Parse(string? v) => Lookup.Parse(new[] { Real, Simulated }, v);
}

internal static class Lookup
{
    public static string? Parse(IReadOnlyList<string> all, string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var key = v.Trim().Replace('-', '_').Replace(' ', '_');
        return all.FirstOrDefault(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));
    }
}
