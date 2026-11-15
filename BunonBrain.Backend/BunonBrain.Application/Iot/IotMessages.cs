using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BunonBrain.Application.Devices;

namespace BunonBrain.Application.Iot;

// The device message contract, identical for every transport: an MQTT
// payload, an HTTP device post and a simulator message are parsed by the
// same code into the same IotMessage. Topics:
//
//   [prefix/]factory/{factory}/device/{device}/telemetry   TELEMETRY
//   [prefix/]factory/{factory}/device/{device}/status      STATUS (or HEARTBEAT with "type":"heartbeat")
//   [prefix/]factory/{factory}/device/{device}/heartbeat   HEARTBEAT
//   [prefix/]factory/{factory}/machine/{machine}/production PRODUCTION (payload names the counter device)
//
// {factory}, {device} and {machine} are ids or codes. Nothing in a topic or
// payload is trusted: the backend resolves the device in the registry and
// refuses anything that doesn't match it.

/// <summary>A parsed MQTT topic.</summary>
/// <param name="Subject">DEVICE or MACHINE.</param>
/// <param name="Channel">telemetry, status, heartbeat or production.</param>
public sealed record IotTopic(string FactoryRef, string Subject, string SubjectRef, string Channel)
{
    public const string DeviceSubject = "DEVICE";
    public const string MachineSubject = "MACHINE";
}

public sealed record IotCounterReading(string Mode, long Value);

/// <summary>One validated device message.</summary>
public sealed record IotMessage(
    string Type,
    string MessageId,
    DateTimeOffset? OccurredAt,
    string? DeviceRef,
    string? FactoryRef,
    string? MachineRef,
    string? FirmwareVersion,
    string? State,
    double? PowerWatts,
    double? CurrentAmps,
    double? TemperatureC,
    double? VibrationMmS,
    string? ErrorCode,
    string? Status,
    string? Message,
    IReadOnlyDictionary<string, double>? Metrics,
    IotCounterReading? Counter,
    IReadOnlyList<string> IgnoredFields);

public sealed record IotParseResult(IotMessage? Message, string? Code, string? Error)
{
    public bool Ok => Message is not null;
    public static IotParseResult Fail(string code, string error) => new(null, code, error);
}

public static class IotTopics
{
    public const string Telemetry = "telemetry";
    public const string Status = "status";
    public const string Heartbeat = "heartbeat";
    public const string Production = "production";

    /// <summary>The subscriptions the MQTT bridge makes (QoS 1).</summary>
    public static IReadOnlyList<string> Subscriptions(string? prefix) => new[]
    {
        Join(prefix, "factory/+/device/+/telemetry"),
        Join(prefix, "factory/+/device/+/status"),
        Join(prefix, "factory/+/device/+/heartbeat"),
        Join(prefix, "factory/+/machine/+/production"),
    };

    public static string Device(string? prefix, string factory, string device, string channel) => Join(prefix, $"factory/{factory}/device/{device}/{channel}");
    public static string Machine(string? prefix, string factory, string machine) => Join(prefix, $"factory/{factory}/machine/{machine}/{Production}");

    private static string Join(string? prefix, string rest)
    {
        var p = (prefix ?? "").Trim().Trim('/');
        return p.Length == 0 ? rest : $"{p}/{rest}";
    }

    /// <summary>Parse a topic; null when it isn't one of ours (or carries wildcards / empty levels).</summary>
    public static IotTopic? Parse(string? topic, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Contains('+') || topic.Contains('#')) return null;
        var p = (prefix ?? "").Trim().Trim('/');
        var t = topic.Trim();
        if (p.Length > 0)
        {
            if (!t.StartsWith(p + "/", StringComparison.Ordinal)) return null;
            t = t[(p.Length + 1)..];
        }
        var parts = t.Split('/');
        if (parts.Length != 5 || parts.Any(x => x.Length is 0 or > 64)) return null;
        if (parts[0] != "factory") return null;
        var channel = parts[4].ToLowerInvariant();
        return parts[2] switch
        {
            "device" when channel is Telemetry or Status or Heartbeat => new IotTopic(parts[1], IotTopic.DeviceSubject, parts[3], channel),
            "machine" when channel is Production => new IotTopic(parts[1], IotTopic.MachineSubject, parts[3], channel),
            _ => null,
        };
    }

    /// <summary>The message type a topic channel carries by default.</summary>
    public static string TypeOf(string channel) => channel switch
    {
        Telemetry => IotMessageTypes.Telemetry,
        Status => IotMessageTypes.Status,
        Heartbeat => IotMessageTypes.Heartbeat,
        _ => IotMessageTypes.Production,
    };

    /// <summary>Whether a payload's <c>type</c> may travel on a channel (heartbeats may go on the status topic).</summary>
    public static bool Allows(string channel, string type) => channel switch
    {
        Telemetry => type == IotMessageTypes.Telemetry,
        Status => type is IotMessageTypes.Status or IotMessageTypes.Heartbeat,
        Heartbeat => type == IotMessageTypes.Heartbeat,
        Production => type == IotMessageTypes.Production,
        _ => false,
    };
}

public static class IotMessageParser
{
    public const string Schema = "bunonbrain.iot.v1";
    public const int MaxBytes = 8 * 1024;
    public const int MessageIdMax = 100;
    public const int MaxIncrement = 10_000;
    public const long MaxCounterValue = 9_000_000_000_000_000;

    private static readonly Regex MessageIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]{0,99}$", RegexOptions.Compiled);
    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,63}$", RegexOptions.Compiled);

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "schema", "type", "messageId", "deviceId", "factoryId", "machineId", "occurredAt", "firmwareVersion", "state", "powerWatts",
        "currentAmps", "temperatureC", "vibrationMmS", "errorCode", "status", "message", "metrics", "counter", "count", "counterValue",
    };

    /// <summary>Parse raw bytes (an MQTT payload).</summary>
    public static IotParseResult Parse(ReadOnlySpan<byte> utf8, IotTopic? topic)
    {
        if (utf8.Length == 0) return IotParseResult.Fail("payload_empty", "The message is empty.");
        if (utf8.Length > MaxBytes) return IotParseResult.Fail("payload_too_large", $"Messages are at most {MaxBytes} bytes.");
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            return Parse(doc.RootElement, topic);
        }
        catch (JsonException)
        {
            return IotParseResult.Fail("payload_not_json", "The message is not valid JSON.");
        }
    }

    public static IotParseResult Parse(string json, IotTopic? topic) => Parse(Encoding.UTF8.GetBytes(json ?? ""), topic);

    /// <summary>
    /// Parse an already-decoded JSON object (an HTTP body). Without a topic,
    /// <paramref name="channel"/> (the route's telemetry / status / heartbeat /
    /// production) supplies the default type and <paramref name="defaultDevice"/>
    /// the device (the one whose key authenticated the request).
    /// </summary>
    public static IotParseResult Parse(JsonElement root, IotTopic? topic, string? channel = null, string? defaultDevice = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return IotParseResult.Fail("payload_not_object", "The message must be a JSON object.");
        if (Encoding.UTF8.GetByteCount(root.GetRawText()) > MaxBytes) return IotParseResult.Fail("payload_too_large", $"Messages are at most {MaxBytes} bytes.");

        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var ignored = new List<string>();
        foreach (var p in root.EnumerateObject())
        {
            if (fields.ContainsKey(p.Name)) return IotParseResult.Fail("field_repeated", $"Field '{p.Name}' appears twice.");
            fields[p.Name] = p.Value;
            if (!Known.Contains(p.Name)) ignored.Add(p.Name);
        }

        string? Str(string name, out string? error, int max = 64)
        {
            error = null;
            if (!fields.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.Number && name is "deviceId" or "factoryId" or "machineId") return v.GetRawText();
            if (v.ValueKind != JsonValueKind.String) { error = $"'{name}' must be a string."; return null; }
            var s = v.GetString()!.Trim();
            if (s.Length == 0) return null;
            if (s.Length > max) { error = $"'{name}' is at most {max} characters."; return null; }
            return s;
        }

        (double? Value, string? Error) Num(string name, double min, double max)
        {
            if (!fields.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null) return (null, null);
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d)) return (null, $"'{name}' must be a number.");
            if (d < min || d > max) return (null, $"'{name}' must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
            return (d, null);
        }

        var schema = Str("schema", out var e0);
        if (e0 is not null) return IotParseResult.Fail("schema_invalid", e0);
        if (schema is not null && schema != Schema) return IotParseResult.Fail("schema_unsupported", $"Unsupported schema '{schema}'; this server reads {Schema}.");

        // ── Type (payload and topic must agree) ──
        var typeRaw = Str("type", out var e1);
        if (e1 is not null) return IotParseResult.Fail("type_invalid", e1);
        string type;
        if (typeRaw is not null)
        {
            var t = IotMessageTypes.Parse(typeRaw);
            if (t is null) return IotParseResult.Fail("type_invalid", $"type: one of {string.Join(", ", IotMessageTypes.All)} (lower case is fine).");
            if (topic is not null && !IotTopics.Allows(topic.Channel, t)) return IotParseResult.Fail("type_topic_mismatch", $"A {t} message can't be published on the {topic.Channel} topic.");
            type = t;
        }
        else if (topic is not null) type = IotTopics.TypeOf(topic.Channel);
        else if (channel is not null) type = IotTopics.TypeOf(channel);
        else return IotParseResult.Fail("type_required", "type is required (telemetry, status, heartbeat or production).");
        if (topic is null && channel is not null && !IotTopics.Allows(channel, type))
            return IotParseResult.Fail("type_topic_mismatch", $"A {type} message can't be sent to the {channel} endpoint.");

        // ── Identity of the message ──
        var messageId = Str("messageId", out var e2, MessageIdMax);
        if (e2 is not null) return IotParseResult.Fail("message_id_invalid", e2);
        if (messageId is null || !MessageIdPattern.IsMatch(messageId))
            return IotParseResult.Fail("message_id_required",
                $"messageId is required: 1–{MessageIdMax} characters (letters, digits, '.', '_', ':', '/', '-'), unique per device. Resend the same id when retrying.");

        var deviceRef = Str("deviceId", out var e3);
        var factoryRef = Str("factoryId", out var e4);
        var machineRef = Str("machineId", out var e5);
        if ((e3 ?? e4 ?? e5) is { } refError) return IotParseResult.Fail("reference_invalid", refError);
        if (topic is not null)
        {
            if (factoryRef is not null && !Same(factoryRef, topic.FactoryRef))
                return IotParseResult.Fail("factory_mismatch", "factoryId in the payload doesn't match the topic.");
            if (topic.Subject == IotTopic.DeviceSubject)
            {
                if (deviceRef is not null && !Same(deviceRef, topic.SubjectRef)) return IotParseResult.Fail("device_mismatch", "deviceId in the payload doesn't match the topic.");
                deviceRef = topic.SubjectRef;
            }
            else
            {
                if (machineRef is not null && !Same(machineRef, topic.SubjectRef)) return IotParseResult.Fail("machine_mismatch", "machineId in the payload doesn't match the topic.");
                machineRef = topic.SubjectRef;
                if (deviceRef is null) return IotParseResult.Fail("device_required", "A production message on a machine topic names its counter device (deviceId).");
            }
            factoryRef = topic.FactoryRef;
        }
        deviceRef ??= defaultDevice;
        if (deviceRef is null) return IotParseResult.Fail("device_required", "deviceId is required.");

        // ── Time ──
        DateTimeOffset? occurredAt = null;
        var at = Str("occurredAt", out var e6);
        if (e6 is not null) return IotParseResult.Fail("occurred_at_invalid", e6);
        if (at is not null)
        {
            if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return IotParseResult.Fail("occurred_at_invalid", "occurredAt must be an ISO 8601 timestamp (UTC, e.g. 2026-11-15T08:00:00Z).");
            occurredAt = parsed.ToUniversalTime();
        }

        var firmware = Str("firmwareVersion", out var e7, 80);
        if (e7 is not null) return IotParseResult.Fail("firmware_invalid", e7);

        // ── Readings ──
        string? state = null;
        var stateRaw = Str("state", out var e8);
        if (e8 is not null) return IotParseResult.Fail("state_invalid", e8);
        if (stateRaw is not null)
        {
            state = MachineStates.Parse(stateRaw);
            if (state is null || !MachineStates.Reportable.Contains(state))
                return IotParseResult.Fail("state_invalid", $"state: one of {string.Join(", ", MachineStates.Reportable)}. OFFLINE and UNKNOWN are decided by the server.");
        }
        var power = Num("powerWatts", 0, 1_000_000);
        var current = Num("currentAmps", 0, 10_000);
        var temperature = Num("temperatureC", -50, 400);
        var vibration = Num("vibrationMmS", 0, 10_000);
        if ((power.Error ?? current.Error ?? temperature.Error ?? vibration.Error) is { } numError) return IotParseResult.Fail("reading_invalid", numError);
        var errorCode = Str("errorCode", out var e9);
        if (e9 is not null) return IotParseResult.Fail("error_code_invalid", e9);
        if (errorCode is not null && !CodePattern.IsMatch(errorCode)) return IotParseResult.Fail("error_code_invalid", "errorCode: letters, digits, '.', '_', '-' (up to 64).");
        var message = Str("message", out var e10, 500);
        if (e10 is not null) return IotParseResult.Fail("message_invalid", e10);

        Dictionary<string, double>? metrics = null;
        if (fields.TryGetValue("metrics", out var m) && m.ValueKind != JsonValueKind.Null)
        {
            if (m.ValueKind != JsonValueKind.Object) return IotParseResult.Fail("metrics_invalid", "metrics must be an object of numbers.");
            metrics = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in m.EnumerateObject())
            {
                if (!DeviceRules.IsMetadataKey(p.Name)) return IotParseResult.Fail("metrics_invalid", "Metric names: letters, digits, '_', '.', '-', up to 40 characters.");
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var d) || !double.IsFinite(d))
                    return IotParseResult.Fail("metrics_invalid", $"Metric '{p.Name}' must be a finite number.");
                metrics[p.Name] = d;
            }
            if (metrics.Count > DeviceRules.MaxMetrics) return IotParseResult.Fail("metrics_invalid", $"At most {DeviceRules.MaxMetrics} metrics per message.");
            if (metrics.Count == 0) metrics = null;
        }

        // ── Status ──
        string? status = null;
        var statusRaw = Str("status", out var e11);
        if (e11 is not null) return IotParseResult.Fail("status_invalid", e11);
        if (statusRaw is not null)
        {
            status = IotDeviceStatuses.Parse(statusRaw);
            if (status is null) return IotParseResult.Fail("status_invalid", $"status: one of {string.Join(", ", IotDeviceStatuses.All)}.");
        }
        if (type == IotMessageTypes.Status && status is null) return IotParseResult.Fail("status_required", "A status message needs status ONLINE, OFFLINE or ERROR.");

        // ── Counter ──
        IotCounterReading? counter = null;
        if (type == IotMessageTypes.Production)
        {
            var c = Counter(fields, out var counterError);
            if (counterError is not null) return IotParseResult.Fail("counter_invalid", counterError);
            counter = c;
        }

        if (type == IotMessageTypes.Telemetry && state is null && power.Value is null && current.Value is null && temperature.Value is null
            && vibration.Value is null && errorCode is null && metrics is null)
            return IotParseResult.Fail("telemetry_empty", "A telemetry message carries a state, a reading (powerWatts, currentAmps, temperatureC, vibrationMmS), an errorCode or metrics.");

        return new IotParseResult(new IotMessage(type, messageId, occurredAt, deviceRef, factoryRef, machineRef, firmware, state,
            power.Value, current.Value, temperature.Value, vibration.Value, errorCode, status, message, metrics, counter, ignored), null, null);
    }

    private static IotCounterReading? Counter(Dictionary<string, JsonElement> fields, out string? error)
    {
        error = null;
        static bool Int(JsonElement v, out long n) { n = 0; return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out n); }

        if (fields.TryGetValue("counter", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            string? modeRaw = null;
            long value = 0;
            var hasValue = false;
            foreach (var p in c.EnumerateObject())
            {
                if (string.Equals(p.Name, "mode", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) modeRaw = p.Value.GetString();
                else if (string.Equals(p.Name, "value", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Int(p.Value, out value)) { error = "counter.value must be a whole number."; return null; }
                    hasValue = true;
                }
            }
            var mode = CounterModes.Parse(modeRaw);
            if (mode is null) { error = $"counter.mode: one of {string.Join(", ", CounterModes.All)}."; return null; }
            long? given = hasValue ? value : mode == CounterModes.Reset ? 0L : null;
            return Checked(mode, given, out error);
        }
        if (fields.TryGetValue("count", out var inc))
        {
            if (!Int(inc, out var n)) { error = "count must be a whole number."; return null; }
            return Checked(CounterModes.Increment, n, out error);
        }
        if (fields.TryGetValue("counterValue", out var abs))
        {
            if (!Int(abs, out var n)) { error = "counterValue must be a whole number."; return null; }
            return Checked(CounterModes.Absolute, n, out error);
        }
        error = "A production message needs counter {mode, value}, or count (an increment), or counterValue (the absolute reading).";
        return null;
    }

    private static IotCounterReading? Checked(string mode, long? value, out string? error)
    {
        error = null;
        if (value is null) { error = $"counter.value is required for {mode}."; return null; }
        if (mode == CounterModes.Increment && value is < 1 or > MaxIncrement) { error = $"An increment is 1–{MaxIncrement} pulses."; return null; }
        if (mode != CounterModes.Increment && value is < 0 or > MaxCounterValue) { error = "A counter reading is a whole number from 0."; return null; }
        return new IotCounterReading(mode, value.Value);
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
