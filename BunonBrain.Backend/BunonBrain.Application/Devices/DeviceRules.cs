using System.Text.Json;
using System.Text.RegularExpressions;

namespace BunonBrain.Application.Devices;

public static class DeviceCategories
{
    public const string RfidReader = "RfidReader";
    public const string MachineSensor = "MachineSensor";
    public const string ProductionCounter = "ProductionCounter";
    public const string Esp32Gateway = "Esp32Gateway";
    public const string IndustrialGateway = "IndustrialGateway";
    public const string Camera = "Camera";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = new[] { RfidReader, MachineSensor, ProductionCounter, Esp32Gateway, IndustrialGateway, Camera, Other };

    public static string? Parse(string? value) => DeviceLookup.Parse(All, value);
}

/// <summary>Administrative state, set by people.</summary>
public static class DeviceStatuses
{
    public const string Active = "Active";
    /// <summary>Being serviced: messages are still recorded, health shows Maintenance.</summary>
    public const string Maintenance = "Maintenance";
    /// <summary>Out of use: messages are refused.</summary>
    public const string Disabled = "Disabled";

    public static readonly IReadOnlyList<string> All = new[] { Active, Maintenance, Disabled };

    public static string? Parse(string? value) => DeviceLookup.Parse(All, value);
}

/// <summary>Last known connection, from messages and missed heartbeats.</summary>
public static class ConnectionStatuses
{
    public const string Online = "Online";
    public const string Offline = "Offline";
    /// <summary>Never heard from.</summary>
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> All = new[] { Online, Offline, Unknown };
}

/// <summary>The one status the health views show: Online, Offline, Unknown, Maintenance or Disabled.</summary>
public static class HealthStatuses
{
    public const string Online = "Online";
    public const string Offline = "Offline";
    public const string Unknown = "Unknown";
    public const string Maintenance = "Maintenance";
    public const string Disabled = "Disabled";

    public static readonly IReadOnlyList<string> All = new[] { Online, Offline, Unknown, Maintenance, Disabled };

    public static string? Parse(string? value) => DeviceLookup.Parse(All, value);
}

public static class DeviceEventTypes
{
    public const string ConnectionOnline = "ConnectionOnline";
    public const string ConnectionOffline = "ConnectionOffline";
    /// <summary>The health monitor saw no heartbeat within the device's offline window.</summary>
    public const string HeartbeatMissed = "HeartbeatMissed";
    public const string Error = "Error";
    public const string StatusChanged = "StatusChanged";
    public const string FirmwareChanged = "FirmwareChanged";

    public static readonly IReadOnlyList<string> All = new[] { ConnectionOnline, ConnectionOffline, HeartbeatMissed, Error, StatusChanged, FirmwareChanged };

    public static string? Parse(string? value) => DeviceLookup.Parse(All, value);
}

public static class DeviceMessageSources
{
    /// <summary>Real hardware (or its gateway), through the device ingestion endpoints.</summary>
    public const string Device = "Device";
    public const string Simulator = "Simulator";
    /// <summary>The server itself (heartbeat monitor).</summary>
    public const string System = "System";
    /// <summary>A person (status change).</summary>
    public const string Manual = "Manual";

    public static readonly IReadOnlyList<string> All = new[] { Device, Simulator, System, Manual };

    public static string? Parse(string? value) => DeviceLookup.Parse(All, value);
}

public static class DeviceSeverities
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Error = "Error";

    public static readonly IReadOnlyList<string> All = new[] { Info, Warning, Error };

    public static string? Parse(string? value) => DeviceLookup.Parse(All, value);
}

/// <summary>Kinds of message in the shared device ingestion contract.</summary>
public static class DeviceMessageKinds
{
    public const string Heartbeat = "Heartbeat";
    public const string Online = "Online";
    public const string Offline = "Offline";
    public const string Error = "Error";

    public static readonly IReadOnlyList<string> All = new[] { Heartbeat, Online, Offline, Error };

    public static string? Parse(string? value) => DeviceLookup.Parse(All, value);
}

internal static class DeviceLookup
{
    public static string? Parse(IReadOnlyList<string> all, string? value) =>
        all.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A device's health at one instant.</summary>
/// <param name="Health">Online, Offline, Unknown, Maintenance or Disabled.</param>
/// <param name="Connection">The effective connection: a stored Online whose heartbeats stopped is Offline.</param>
/// <param name="Stale">Stored as Online, but no message within the offline window.</param>
/// <param name="PhysicallyConnected">Only a real (not simulated), effectively online device is physically connected.</param>
public sealed record DeviceHealth(string Health, string Connection, bool Stale, bool PhysicallyConnected, string Label);

public static partial class DeviceRules
{
    public const int CodeMax = 32;
    public const int NameMax = 120;
    public const int TextMax = 80;
    public const int ReasonMax = 500;
    public const int MessageMax = 500;
    public const int ErrorCodeMax = 64;
    public const int MessageIdMax = 100;
    public const int MaxMetadataEntries = 30;
    public const int MetadataKeyMax = 40;
    public const int MetadataValueMax = 200;
    public const int MaxMetrics = 20;
    public const int MinHeartbeatSeconds = 5;
    public const int MaxHeartbeatSeconds = 86_400;
    public const int MaxSimulatedHeartbeats = 20;

    /// <summary>A message may be dated at most this far ahead of the server clock.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
    /// <summary>And at most this far back (a gateway that buffered while offline).</summary>
    public static readonly TimeSpan MaxBackdate = TimeSpan.FromDays(7);

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-_/.:]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static bool IsCode(string? value) => value is not null && CodePattern().IsMatch(value.Trim().ToUpperInvariant());

    public static string NormaliseCode(string value) => value.Trim().ToUpperInvariant();

    public static bool IsMetadataKey(string key) => KeyPattern().IsMatch(key);

    /// <summary>
    /// Health = administrative status first (Disabled, Maintenance), then the
    /// connection, where a stored Online with no message for
    /// <paramref name="offlineAfterSeconds"/> counts as Offline. The label
    /// always says "simulated" for a simulated device.
    /// </summary>
    public static DeviceHealth Health(string status, string connection, bool isSimulated, DateTimeOffset? lastSeenAt,
        int offlineAfterSeconds, DateTimeOffset now)
    {
        var stale = connection == ConnectionStatuses.Online
                    && (lastSeenAt is null || now - lastSeenAt.Value > TimeSpan.FromSeconds(offlineAfterSeconds));
        var effective = stale ? ConnectionStatuses.Offline : connection;
        var health = status switch
        {
            DeviceStatuses.Disabled => HealthStatuses.Disabled,
            DeviceStatuses.Maintenance => HealthStatuses.Maintenance,
            _ => effective switch
            {
                ConnectionStatuses.Online => HealthStatuses.Online,
                ConnectionStatuses.Offline => HealthStatuses.Offline,
                _ => HealthStatuses.Unknown,
            },
        };
        var physical = !isSimulated && status != DeviceStatuses.Disabled && effective == ConnectionStatuses.Online;
        var label = health switch
        {
            HealthStatuses.Online => isSimulated ? "Online (simulated)" : "Online",
            HealthStatuses.Offline => isSimulated ? "Offline (simulated)" : stale ? "Offline (no heartbeat)" : "Offline",
            HealthStatuses.Unknown => isSimulated ? "No simulated heartbeat yet" : "Never connected",
            HealthStatuses.Maintenance => "Maintenance",
            _ => "Disabled",
        };
        return new DeviceHealth(health, effective, stale, physical, label);
    }

    /// <summary>Validate free metadata (string → string). Null input is an empty object.</summary>
    public static bool TryMetadata(IReadOnlyDictionary<string, string?>? input, out string json, out string? error)
    {
        json = "{}";
        error = null;
        if (input is null || input.Count == 0) return true;
        if (input.Count > MaxMetadataEntries) { error = $"At most {MaxMetadataEntries} metadata entries."; return false; }
        var clean = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            var k = key?.Trim() ?? "";
            if (!IsMetadataKey(k)) { error = "Metadata keys: letters, digits, '_', '.', '-', up to 40 characters."; return false; }
            var v = value?.Trim() ?? "";
            if (v.Length > MetadataValueMax) { error = $"Metadata values: up to {MetadataValueMax} characters."; return false; }
            if (LooksSecret(k)) { error = $"Don't store secrets in metadata ('{k}')."; return false; }
            clean[k] = v;
        }
        json = JsonSerializer.Serialize(clean);
        return true;
    }

    /// <summary>Validate numeric heartbeat metrics; null when there are none.</summary>
    public static bool TryMetrics(IReadOnlyDictionary<string, double>? input, out string? json, out string? error)
    {
        json = null;
        error = null;
        if (input is null || input.Count == 0) return true;
        if (input.Count > MaxMetrics) { error = $"At most {MaxMetrics} metrics per heartbeat."; return false; }
        var clean = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            var k = key?.Trim() ?? "";
            if (!IsMetadataKey(k)) { error = "Metric names: letters, digits, '_', '.', '-', up to 40 characters."; return false; }
            if (!double.IsFinite(value)) { error = $"Metric '{k}' must be a finite number."; return false; }
            clean[k] = value;
        }
        json = JsonSerializer.Serialize(clean);
        return true;
    }

    public static IReadOnlyDictionary<string, string> ReadMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(); }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    public static IReadOnlyDictionary<string, double>? ReadMetrics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, double>>(json); }
        catch (JsonException) { return null; }
    }

    private static bool LooksSecret(string key)
    {
        var k = key.ToLowerInvariant();
        return k.Contains("password") || k.Contains("passwd") || k.Contains("secret") || k.Contains("token") || k == "pwd" || k.Contains("apikey") || k.Contains("api_key");
    }
}
