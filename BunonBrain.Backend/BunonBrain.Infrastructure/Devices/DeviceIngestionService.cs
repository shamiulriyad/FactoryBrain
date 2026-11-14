using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Devices;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Devices;

/// <summary>
/// The device ingestion pipeline (see <see cref="IDeviceIngestionService"/>).
/// <para>
/// Contract, the same for every transport (HTTP today; an MQTT bridge would
/// map topic <c>bunonbrain/{factoryCode}/devices/{deviceCode}/{kind}</c> and
/// its JSON payload onto <see cref="DeviceMessage"/>):
/// <c>kind</c> Heartbeat | Online | Offline | Error, <c>occurredAt</c> (UTC,
/// at most 5 minutes ahead and 7 days back; defaults to the receive time),
/// optional <c>messageId</c> (a repeat is recorded once), <c>firmwareVersion</c>,
/// numeric <c>metrics</c>, and for errors <c>code</c> / <c>message</c> / <c>severity</c>.
/// </para>
/// <para>
/// Effects: a heartbeat (and an Online message) adds a heartbeat row; any
/// fresh message other than Offline brings an offline / unknown device
/// online (a ConnectionOnline event); Offline records ConnectionOffline;
/// Error records an Error event and the device's last error. Old buffered
/// messages are stored but never move the device's state backwards.
/// </para>
/// </summary>
public sealed class DeviceIngestionService : IDeviceIngestionService
{
    public const string MonitorEmail = "system:device-health-monitor";
    private readonly BunonBrainDbContext _db;
    private readonly ILogger<DeviceIngestionService> _log;

    private readonly IMonitoringSystemHooks _monitoring;

    public DeviceIngestionService(BunonBrainDbContext db, IMonitoringSystemHooks monitoring, ILogger<DeviceIngestionService> log)
    {
        _db = db; _monitoring = monitoring; _log = log;
    }

    private static AccessResult<T> NotFound<T>() => AccessResult<T>.NotFound("device_not_found", "Device not found.");

    public async Task<AccessResult<DeviceMessageResult>> IngestByCodeAsync(AccessContext caller, Guid factoryId, string deviceCode, DeviceMessage message, CancellationToken ct)
    {
        var code = DeviceRules.NormaliseCode(deviceCode);
        var id = await DeviceQueries.InScope(_db, caller).Where(d => d.FactoryId == factoryId && d.Code == code).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        return id is null ? NotFound<DeviceMessageResult>() : await IngestAsync(caller, id.Value, message, DeviceMessageSources.Device, ct);
    }

    public async Task<AccessResult<DeviceMessageResult>> IngestAsync(AccessContext caller, Guid deviceId, DeviceMessage m, string source, CancellationToken ct)
    {
        var device = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return NotFound<DeviceMessageResult>();
        if (!DeviceQueries.CanConfigure(caller, device.FactoryId))
            return AccessResult<DeviceMessageResult>.Forbidden("factory_scope_required", "Only factory-level device administrators send device messages.");
        if (source == DeviceMessageSources.Simulator && !device.IsSimulated)
            return AccessResult<DeviceMessageResult>.Conflict("device_not_simulated",
                $"{device.Code} is a real device. The simulator only sends messages for simulated devices, so real health is never mixed with simulated data.");
        if (source == DeviceMessageSources.Device && device.IsSimulated)
            return AccessResult<DeviceMessageResult>.Conflict("device_is_simulated",
                $"{device.Code} is a simulated device: it only accepts messages from the device simulator.");
        if (device.Status == DeviceStatuses.Disabled)
            return AccessResult<DeviceMessageResult>.Conflict("device_disabled", $"{device.Code} is disabled; its messages are not accepted.");
        var kind = DeviceMessageKinds.Parse(m.Kind);
        if (kind is null) return AccessResult<DeviceMessageResult>.Invalid("message_kind_invalid", $"Kind: one of {string.Join(", ", DeviceMessageKinds.All)}.");
        if (!DeviceRules.TryMetrics(m.Metrics, out var metrics, out var metricError))
            return AccessResult<DeviceMessageResult>.Invalid("metrics_invalid", metricError!);
        if (kind == DeviceMessageKinds.Error && string.IsNullOrWhiteSpace(m.Code) && string.IsNullOrWhiteSpace(m.Message))
            return AccessResult<DeviceMessageResult>.Invalid("error_details_required", "An error needs a code or a message.");
        var severity = m.Severity is null ? null : DeviceSeverities.Parse(m.Severity);
        if (m.Severity is not null && severity is null)
            return AccessResult<DeviceMessageResult>.Invalid("severity_invalid", "Severity: Info, Warning or Error.");

        var now = DateTimeOffset.UtcNow;
        var occurredAt = m.OccurredAt?.ToUniversalTime() ?? now;
        if (occurredAt > now + DeviceRules.MaxClockSkew)
            return AccessResult<DeviceMessageResult>.Invalid("occurred_in_future", "The message is dated in the future. Check the device clock.");
        if (occurredAt < now - DeviceRules.MaxBackdate)
            return AccessResult<DeviceMessageResult>.Invalid("occurred_too_old", "The message is more than 7 days old and was not recorded.");
        var messageId = string.IsNullOrWhiteSpace(m.MessageId) ? null : m.MessageId.Trim();
        if (messageId is { Length: > DeviceRules.MessageIdMax })
            return AccessResult<DeviceMessageResult>.Invalid("message_id_too_long", $"Message ids are at most {DeviceRules.MessageIdMax} characters.");

        var strategy = _db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await DeviceLocks.DeviceAsync(_db, deviceId, ct);
            var d = await _db.Devices.FirstAsync(x => x.Id == deviceId, ct);
            if (d.Status == DeviceStatuses.Disabled)
            {
                await tx.RollbackAsync(ct);
                return (Outcome: "Disabled", HeartbeatId: (long?)null, Events: new List<DeviceEvent>());
            }
            if (messageId is not null
                && (await _db.DeviceHeartbeats.AnyAsync(h => h.DeviceId == deviceId && h.MessageId == messageId, ct)
                    || await _db.DeviceEvents.AnyAsync(e => e.DeviceId == deviceId && e.MessageId == messageId, ct)))
            {
                await tx.RollbackAsync(ct);
                return (Outcome: "Duplicate", HeartbeatId: (long?)null, Events: new List<DeviceEvent>());
            }

            var events = new List<DeviceEvent>();
            DeviceEvent Event(string type, string sev, string? code, string? text, string? from, string? to) => new()
            {
                DeviceId = d.Id, FactoryId = d.FactoryId, EventType = type, Severity = sev, Source = source, IsSimulated = d.IsSimulated,
                OccurredAt = occurredAt, ReceivedAt = now, MessageId = messageId, Code = code, Message = text, FromState = from, ToState = to,
                RecordedByUserId = caller.UserId, RecordedByEmail = caller.Email,
            };
            // Each event of one message gets the message id once; the unique index allows one row per id.
            void Add(DeviceEvent e)
            {
                if (events.Any(x => x.MessageId != null)) e.MessageId = null;
                events.Add(e);
            }

            var fresh = now - occurredAt <= TimeSpan.FromSeconds(d.OfflineAfterSeconds);
            var newest = d.LastSeenAt is null || occurredAt >= d.LastSeenAt;
            DeviceHeartbeat? heartbeat = null;
            if (kind is DeviceMessageKinds.Heartbeat or DeviceMessageKinds.Online)
            {
                heartbeat = new DeviceHeartbeat
                {
                    DeviceId = d.Id, FactoryId = d.FactoryId, OccurredAt = occurredAt, ReceivedAt = now, Source = source, IsSimulated = d.IsSimulated,
                    MessageId = messageId, FirmwareVersion = Clean(m.FirmwareVersion), Metrics = metrics, RecordedByEmail = caller.Email,
                };
                _db.DeviceHeartbeats.Add(heartbeat);
                if (d.LastHeartbeatAt is null || occurredAt > d.LastHeartbeatAt) d.LastHeartbeatAt = occurredAt;
            }
            if (Clean(m.FirmwareVersion) is { } fw && fw != d.FirmwareVersion && newest)
            {
                Add(Event(DeviceEventTypes.FirmwareChanged, DeviceSeverities.Info, null, $"Firmware {d.FirmwareVersion ?? "(unknown)"} → {fw}", d.FirmwareVersion, fw));
                d.FirmwareVersion = fw;
            }
            if (kind == DeviceMessageKinds.Offline)
            {
                if (d.ConnectionStatus != ConnectionStatuses.Offline && newest)
                {
                    Add(Event(DeviceEventTypes.ConnectionOffline, DeviceSeverities.Warning, Clean(m.Code),
                        Clean(m.Message) ?? (d.IsSimulated ? "Simulated: device went offline" : "Device reported it is going offline"),
                        d.ConnectionStatus, ConnectionStatuses.Offline));
                    d.ConnectionStatus = ConnectionStatuses.Offline;
                    d.LastDisconnectedAt = occurredAt;
                    d.ConnectionChangedAt = occurredAt;
                }
            }
            else
            {
                if (kind == DeviceMessageKinds.Error)
                {
                    var sev = severity ?? DeviceSeverities.Error;
                    Add(Event(DeviceEventTypes.Error, sev, Clean(m.Code), Clean(m.Message), null, null));
                    if (d.LastErrorAt is null || occurredAt >= d.LastErrorAt)
                    {
                        d.LastErrorAt = occurredAt;
                        d.LastErrorCode = Clean(m.Code);
                        d.LastErrorMessage = Clean(m.Message);
                    }
                }
                // Any fresh message proves the device can talk to us.
                if (fresh && newest && d.ConnectionStatus != ConnectionStatuses.Online)
                {
                    Add(Event(DeviceEventTypes.ConnectionOnline, DeviceSeverities.Info, null,
                        kind == DeviceMessageKinds.Online ? Clean(m.Message) ?? (d.IsSimulated ? "Simulated: device came online" : "Device connected") : "First message after being offline",
                        d.ConnectionStatus, ConnectionStatuses.Online));
                    d.ConnectionStatus = ConnectionStatuses.Online;
                    d.LastConnectedAt = occurredAt;
                    d.ConnectionChangedAt = occurredAt;
                }
            }
            if (newest) d.LastSeenAt = occurredAt;
            d.OfflineDeadline = DeviceQueries.Deadline(d.LastSeenAt, d.OfflineAfterSeconds);
            _db.DeviceEvents.AddRange(events);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (Outcome: "Accepted", HeartbeatId: heartbeat?.Id, Events: events);
        });
        _db.ChangeTracker.Clear();
        // Connection, error and status changes reach central monitoring (after the commit; never undoes it).
        if (outcome.Events.Count > 0) await _monitoring.DeviceEventsAsync(outcome.Events.Select(e => e.Id).ToList(), caller, ct);
        if (outcome.Outcome == "Disabled")
            return AccessResult<DeviceMessageResult>.Conflict("device_disabled", $"{device.Code} is disabled; its messages are not accepted.");
        var saved = await _db.Devices.AsNoTracking().FirstAsync(x => x.Id == deviceId, ct);
        var dto = (await DeviceQueries.ToDtosAsync(_db, new[] { saved }, ct))[0];
        return AccessResult<DeviceMessageResult>.Ok(new DeviceMessageResult(outcome.Outcome, saved.IsSimulated, outcome.HeartbeatId,
            outcome.Events.Select(e => DeviceQueries.EventDto(e, saved.Code)).ToList(), dto));
    }

    // ─── Simulator ─────────────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<DeviceDto>>> SimulatedDevicesAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var devices = DeviceQueries.InScope(_db, caller).Where(d => d.IsSimulated);
        if (factoryId is { } f) devices = devices.Where(d => d.FactoryId == f);
        var rows = await devices.OrderBy(d => d.Code).Take(500).ToListAsync(ct);
        return AccessResult<IReadOnlyList<DeviceDto>>.Ok(await DeviceQueries.ToDtosAsync(_db, rows, ct));
    }

    public async Task<AccessResult<SimulatorBatchResult>> SimulateHeartbeatsAsync(AccessContext caller, SimulatorHeartbeatRequest r, CancellationToken ct)
    {
        var device = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(d => d.Id == r.DeviceId, ct);
        if (device is null) return NotFound<SimulatorBatchResult>();
        var count = Math.Clamp(r.Count ?? 1, 1, DeviceRules.MaxSimulatedHeartbeats);
        var interval = Math.Clamp(r.IntervalSeconds ?? Math.Min(device.HeartbeatIntervalSeconds, 3600), 1, 3600);
        var now = DateTimeOffset.UtcNow;
        var events = new List<DeviceEventDto>();
        DeviceDto? last = null;
        var accepted = 0;
        for (var i = 0; i < count; i++)
        {
            var at = now.AddSeconds(-(count - 1 - i) * (double)interval);
            var result = await IngestAsync(caller, r.DeviceId,
                new DeviceMessage(DeviceMessageKinds.Heartbeat, at, $"sim-{Guid.NewGuid():N}", r.FirmwareVersion, r.Metrics ?? SampleMetrics(device.Category, i), null, null, null),
                DeviceMessageSources.Simulator, ct);
            if (result.Outcome != AccessOutcome.Ok) return new AccessResult<SimulatorBatchResult>(result.Outcome, null, result.Code, result.Message);
            accepted++;
            events.AddRange(result.Value!.Events);
            last = result.Value.Device;
        }
        return AccessResult<SimulatorBatchResult>.Ok(new SimulatorBatchResult(accepted, events, last!));
    }

    public async Task<AccessResult<DeviceMessageResult>> SimulateAsync(AccessContext caller, Guid deviceId, string kind, string? code, string? message, string? severity, CancellationToken ct)
    {
        if (kind == DeviceMessageKinds.Error && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message))
        {
            var category = await DeviceQueries.InScope(_db, caller).Where(d => d.Id == deviceId).Select(d => d.Category).FirstOrDefaultAsync(ct);
            if (category is null) return NotFound<DeviceMessageResult>();
            (code, message) = SampleError(category);
            severity ??= DeviceSeverities.Error;
        }
        return await IngestAsync(caller, deviceId,
            new DeviceMessage(kind, null, $"sim-{Guid.NewGuid():N}", null, null, Clean(code), Clean(message), severity), DeviceMessageSources.Simulator, ct);
    }

    /// <summary>A plausible error for the category, labelled as a sample.</summary>
    public static (string Code, string Message) SampleError(string category) => category switch
    {
        DeviceCategories.RfidReader => ("RFID_READ_TIMEOUT", "Sample error: card read timed out (antenna not responding)"),
        DeviceCategories.MachineSensor => ("SENSOR_OUT_OF_RANGE", "Sample error: reading outside the sensor's measuring range"),
        DeviceCategories.ProductionCounter => ("COUNTER_STUCK", "Sample error: counter value unchanged while the line is running"),
        DeviceCategories.Esp32Gateway => ("WIFI_WEAK_SIGNAL", "Sample error: Wi-Fi signal below -80 dBm, messages delayed"),
        DeviceCategories.IndustrialGateway => ("MODBUS_TIMEOUT", "Sample error: Modbus slave 3 did not answer"),
        DeviceCategories.Camera => ("STREAM_LOST", "Sample error: video stream lost"),
        _ => ("DEVICE_FAULT", "Sample error: device reported a fault"),
    };

    private static IReadOnlyDictionary<string, double> SampleMetrics(string category, int i) => category switch
    {
        DeviceCategories.Esp32Gateway or DeviceCategories.IndustrialGateway => new Dictionary<string, double> { ["rssi"] = -55 - i % 7, ["queueLength"] = i % 3 },
        DeviceCategories.MachineSensor => new Dictionary<string, double> { ["batteryPercent"] = 96 - i % 5 },
        _ => new Dictionary<string, double> { ["uptimeSeconds"] = 3600 + i * 60 },
    };

    // ─── Health check ──────────────────────────────────────────────────────

    public async Task<HealthCheckResult> CheckHealthAsync(AccessContext? caller, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var devices = caller is null ? _db.Devices.AsNoTracking() : DeviceQueries.InScope(_db, caller);
        var candidates = await devices.Where(d => d.ConnectionStatus == ConnectionStatuses.Online && (d.OfflineDeadline == null || d.OfflineDeadline < now))
            .Select(d => d.Id).Take(1000).ToListAsync(ct);
        var marked = 0;
        long? missedEventId = null;
        foreach (var id in candidates)
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            var changed = await strategy.ExecuteAsync(async () =>
            {
                missedEventId = null;
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await DeviceLocks.DeviceAsync(_db, id, ct);
                var d = await _db.Devices.FirstAsync(x => x.Id == id, ct);
                // Re-check under the lock: a heartbeat may have arrived meanwhile.
                if (d.ConnectionStatus != ConnectionStatuses.Online || (d.OfflineDeadline is { } dl && dl >= DateTimeOffset.UtcNow))
                {
                    await tx.RollbackAsync(ct);
                    return false;
                }
                var at = d.OfflineDeadline ?? now;
                var missed = new DeviceEvent
                {
                    DeviceId = d.Id, FactoryId = d.FactoryId, EventType = DeviceEventTypes.HeartbeatMissed, Severity = DeviceSeverities.Warning,
                    Source = DeviceMessageSources.System, IsSimulated = d.IsSimulated, OccurredAt = at, ReceivedAt = now,
                    Message = $"No message for {d.OfflineAfterSeconds} s (last seen {d.LastSeenAt:u}); marked offline",
                    FromState = ConnectionStatuses.Online, ToState = ConnectionStatuses.Offline,
                    RecordedByUserId = caller?.UserId, RecordedByEmail = caller?.Email ?? MonitorEmail,
                };
                _db.DeviceEvents.Add(missed);
                d.ConnectionStatus = ConnectionStatuses.Offline;
                d.LastDisconnectedAt = at;
                d.ConnectionChangedAt = at;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                missedEventId = missed.Id;
                return true;
            });
            if (changed)
            {
                marked++;
                _db.ChangeTracker.Clear();
                if (missedEventId is { } eventId) await _monitoring.DeviceEventsAsync(new[] { eventId }, caller, ct);
            }
        }
        _db.ChangeTracker.Clear();
        if (marked > 0) _log.LogInformation("Device health check marked {Count} device(s) offline.", marked);
        return new HealthCheckResult(candidates.Count, marked);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

internal static class DeviceLocks
{
    public static Task DeviceAsync(BunonBrainDbContext db, Guid deviceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE", ct);
}

/// <summary>
/// Marks devices offline when their heartbeats stop (every minute). Not
/// registered in the Testing host; tests run the check through the API.
/// </summary>
public sealed class DeviceHealthMonitor : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DeviceHealthMonitor> _log;

    public DeviceHealthMonitor(IServiceScopeFactory scopes, ILogger<DeviceHealthMonitor> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDeviceIngestionService>().CheckHealthAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Device health check failed; retrying in {Interval}.", Interval);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
