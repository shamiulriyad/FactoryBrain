using System.Collections.Concurrent;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Devices;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Devices;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Infrastructure.Iot;

/// <summary>
/// The IoT pipeline (see <see cref="IIotIngestionService"/>). Order:
/// resolve the factory and device in the registry (ids and codes from topics
/// or payloads are only lookup keys), check transport, simulation and device
/// status rules, rate-limit per device, check the timestamp, then hand the
/// message to the device health pipeline (heartbeat / status), the machine
/// state processor (telemetry) or the production counter (production).
/// </summary>
public sealed class IotIngestionService : IIotIngestionService
{
    private readonly BunonBrainDbContext _db;
    private readonly IDeviceIngestionService _devices;
    private readonly MachineStateProcessor _states;
    private readonly ProductionCounterProcessor _counters;
    private readonly DeviceRateLimiter _limiter;
    private readonly ILogger<IotIngestionService> _log;

    /// <summary>Invalid payloads are recorded on the device at most once a minute (a broken firmware can't flood the event log).</summary>
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastInvalidRecorded = new();

    public IotIngestionService(BunonBrainDbContext db, IDeviceIngestionService devices, MachineStateProcessor states, ProductionCounterProcessor counters,
        DeviceRateLimiter limiter, ILogger<IotIngestionService> log)
    {
        _db = db; _devices = devices; _states = states; _counters = counters; _limiter = limiter; _log = log;
    }

    private static AccessResult<IotIngestResult> Rejected(string kind, string code, string message, AccessOutcome outcome = AccessOutcome.Invalid) =>
        new(outcome, null, code, message);

    // ─── Raw (MQTT) ────────────────────────────────────────────────────────

    public async Task<AccessResult<IotIngestResult>> IngestRawAsync(byte[] payload, IotOrigin origin, CancellationToken ct)
    {
        var parsed = IotMessageParser.Parse(payload, origin.Topic);
        if (parsed.Ok) return await IngestAsync(parsed.Message!, origin, ct);
        // Record the bad message on the device, if the topic names a registered one.
        if (origin.Topic is { Subject: IotTopic.DeviceSubject } topic && await ResolveFactoryAsync(topic.FactoryRef, ct) is { } factoryId
            && await ResolveDeviceAsync(factoryId, topic.SubjectRef, ct) is { } device)
            await RecordInvalidAsync(device, origin, parsed.Code!, parsed.Error!, ct);
        return Rejected("unknown", parsed.Code!, parsed.Error!);
    }

    // ─── Validated messages ────────────────────────────────────────────────

    public async Task<AccessResult<IotIngestResult>> IngestAsync(IotMessage m, IotOrigin o, CancellationToken ct)
    {
        // ── Factory ──
        Guid? factoryId = o.FactoryId;
        if (o.Topic is not null) factoryId = await ResolveFactoryAsync(o.Topic.FactoryRef, ct);
        if (m.FactoryRef is not null && o.Topic is null)
        {
            var named = await ResolveFactoryAsync(m.FactoryRef, ct);
            if (named is null || (factoryId is not null && named != factoryId))
                return Rejected(m.Type, "factory_mismatch", "factoryId in the message doesn't match the factory the device is registered in.");
            factoryId ??= named;
        }

        // ── Device (always looked up; never taken from the message) ──
        Device? device;
        if (o.AuthenticatedDeviceId is { } authenticated)
        {
            device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == authenticated, ct);
            if (device is null) return Rejected(m.Type, "device_not_found", "Device not found.", AccessOutcome.NotFound);
            if (m.DeviceRef is not null && !Matches(device, m.DeviceRef))
                return Rejected(m.Type, "device_mismatch", "The message names another device than the one whose key was presented.", AccessOutcome.Forbidden);
            if (factoryId is not null && factoryId != device.FactoryId)
                return Rejected(m.Type, "factory_mismatch", "The device is registered in another factory.", AccessOutcome.Forbidden);
        }
        else
        {
            if (factoryId is null) return Rejected(m.Type, "factory_required", "The factory is unknown: send factoryId (or use a registered factory's id or code in the topic).", AccessOutcome.NotFound);
            device = m.DeviceRef is null ? null : await ResolveDeviceAsync(factoryId.Value, m.DeviceRef, ct);
            if (device is null) return Rejected(m.Type, "device_not_found", "No device with that id or code is registered in this factory.", AccessOutcome.NotFound);
        }
        if (o.Caller is { } caller)
        {
            if (!await DeviceQueries.InScope(_db, caller).AnyAsync(d => d.Id == device.Id, ct))
                return Rejected(m.Type, "device_not_found", "Device not found.", AccessOutcome.NotFound);
            if (!DeviceQueries.CanConfigure(caller, device.FactoryId))
                return Rejected(m.Type, "factory_scope_required", "Only factory-level device administrators send device messages.", AccessOutcome.Forbidden);
        }
        if (!await _db.Factories.AnyAsync(f => f.Id == device.FactoryId && f.IsActive, ct))
            return Rejected(m.Type, "factory_inactive", "The factory is deactivated; its device messages are not accepted.", AccessOutcome.Conflict);

        // ── Transport and simulation ──
        var config = await _db.DeviceIotConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.DeviceId == device.Id, ct);
        if (o.Transport == IotTransports.Simulator && !device.IsSimulated)
            return Rejected(m.Type, "device_not_simulated", $"{device.Code} is a real device; the simulator only sends for simulated devices.", AccessOutcome.Conflict);
        if (o.Transport != IotTransports.Simulator && device.IsSimulated)
            return Rejected(m.Type, "device_is_simulated", $"{device.Code} is a simulated device: only the IoT simulator sends its data.", AccessOutcome.Conflict);
        if (o.Transport == IotTransports.Mqtt && IotQueries.ProtocolOf(device, config) != IotProtocols.Mqtt)
            return Rejected(m.Type, "device_not_mqtt", $"{device.Code} is not configured for MQTT; its MQTT messages are ignored. Set its protocol to MQTT first.", AccessOutcome.Conflict);
        if (device.Status == DeviceStatuses.Disabled)
            return Rejected(m.Type, "device_disabled", $"{device.Code} is disabled; its messages are not accepted.", AccessOutcome.Conflict);

        var now = DateTimeOffset.UtcNow;
        if (!_limiter.TryAcquire(device.Id, now))
            return Rejected(m.Type, "rate_limited", $"{device.Code} is sending too many messages; this one was dropped.", AccessOutcome.Conflict);

        // ── Time ──
        var occurredAt = m.OccurredAt ?? now;
        if (occurredAt > now + MonitoringRules.MaxClockSkew)
            return Rejected(m.Type, "occurred_in_future", "The message is dated in the future. Check the device clock (timestamps are UTC).");
        if (occurredAt < now - MonitoringRules.MaxBackdate)
            return Rejected(m.Type, "occurred_too_old", "The message is more than 7 days old and was not recorded.");

        var type = IotQueries.DeviceTypeOf(device, config);
        var actor = o.Actor;
        var notes = new List<string>();
        if (m.IgnoredFields.Count > 0) notes.Add($"Ignored unknown fields: {string.Join(", ", m.IgnoredFields.Take(10))}");
        if (device.IsSimulated) notes.Add("SIMULATED data");

        Machine? machine = device.MachineId is { } mid ? await _db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mid, ct) : null;
        if (m.MachineRef is not null && (machine is null || !Matches(machine, m.MachineRef)))
            return Rejected(m.Type, "machine_mismatch", machine is null
                ? $"{device.Code} is not bound to a machine; bind it before sending machine data."
                : $"{device.Code} is bound to machine {machine.Code}, not to the machine in the message.", AccessOutcome.Conflict);

        switch (m.Type)
        {
            case IotMessageTypes.Heartbeat:
            case IotMessageTypes.Status:
            {
                var kind = m.Type == IotMessageTypes.Heartbeat ? DeviceMessageKinds.Heartbeat : m.Status switch
                {
                    IotDeviceStatuses.Online => DeviceMessageKinds.Online,
                    IotDeviceStatuses.Offline => DeviceMessageKinds.Offline,
                    _ => DeviceMessageKinds.Error,
                };
                var r = await _devices.IngestAsync(CallerFor(o, device), device.Id,
                    new DeviceMessage(kind, occurredAt, m.MessageId, m.FirmwareVersion, m.Metrics, m.ErrorCode ?? (kind == DeviceMessageKinds.Error ? "DEVICE_ERROR" : null),
                        m.Message, kind == DeviceMessageKinds.Error ? DeviceSeverities.Error : null),
                    o.Transport == IotTransports.Simulator ? DeviceMessageSources.Simulator : DeviceMessageSources.Device, ct);
                if (r.Outcome != AccessOutcome.Ok) return new AccessResult<IotIngestResult>(r.Outcome, null, r.Code, r.Message);
                string? state = null;
                if (machine is not null && r.Value!.Outcome == "Accepted") state = (await _states.RefreshAsync(machine.Id, null, actor, ct)).State;
                return AccessResult<IotIngestResult>.Ok(new IotIngestResult(r.Value!.Outcome, m.Type, device.Id, device.Code, device.IsSimulated, machine?.Id, state,
                    null, null, null, null, notes));
            }

            case IotMessageTypes.Telemetry:
            {
                if (!IotDeviceTypes.MachineState.Contains(type))
                    return Rejected(m.Type, "telemetry_not_supported", $"{device.Code} is a {IotDeviceTypes.Label(type).ToLowerInvariant()}; it doesn't send machine telemetry.", AccessOutcome.Conflict);
                if (await _db.MachineTelemetry.AsNoTracking().AnyAsync(t => t.DeviceId == device.Id && t.MessageId == m.MessageId, ct))
                    return Duplicate(m, device, machine, notes);
                await TouchAsync(device, o, occurredAt, m.FirmwareVersion, ct);
                MachineStateProcessor.TelemetryOutcome outcome;
                try { outcome = await _states.ApplyTelemetryAsync(device, machine, m, occurredAt, o.Transport, actor, ct); }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
                                                   && pg.ConstraintName == MachineTelemetryConfiguration.MessageIndex)
                {
                    _db.ChangeTracker.Clear();
                    return Duplicate(m, device, machine, notes);
                }
                switch (outcome.Outcome)
                {
                    case "LATE": notes.Add("Older than the newest applied telemetry: stored, the machine state was not changed"); break;
                    case "NO_MACHINE": notes.Add($"{device.Code} is not bound to a machine: stored, no machine state"); break;
                    case "MAINTENANCE": notes.Add("The machine is in maintenance: readings stored, state unchanged"); break;
                    case "SOURCE_MISMATCH": notes.Add("The machine has real sensors; simulated telemetry is stored but never changes its state"); break;
                }
                return AccessResult<IotIngestResult>.Ok(new IotIngestResult("Accepted", m.Type, device.Id, device.Code, device.IsSimulated, machine?.Id,
                    outcome.State, outcome.TelemetryId, null, null, null, notes));
            }

            default: // PRODUCTION
            {
                if (!IotDeviceTypes.Counters.Contains(type))
                    return Rejected(m.Type, "counter_not_supported", $"{device.Code} is a {IotDeviceTypes.Label(type).ToLowerInvariant()}; production counts come from a piece counter.", AccessOutcome.Conflict);
                var outcome = await _counters.ApplyAsync(device, machine, m.MessageId, m.Counter!, occurredAt, o.Transport, actor, ct);
                if (outcome.Outcome == "Rejected") return Rejected(m.Type, outcome.Code!, outcome.Error!, AccessOutcome.Conflict);
                if (outcome.Outcome == "Accepted") await TouchAsync(device, o, occurredAt, m.FirmwareVersion, ct);
                var e = outcome.Event!;
                if (e.Note is not null) notes.Add(e.Note);
                return AccessResult<IotIngestResult>.Ok(new IotIngestResult(outcome.Outcome, m.Type, device.Id, device.Code, device.IsSimulated, machine?.Id, null,
                    null, e.Id, e.Status, e.Pieces, notes));
            }
        }
    }

    private static AccessResult<IotIngestResult> Duplicate(IotMessage m, Device d, Machine? machine, List<string> notes)
    {
        notes.Add("This message id was already received from this device: not processed again");
        return AccessResult<IotIngestResult>.Ok(new IotIngestResult("Duplicate", m.Type, d.Id, d.Code, d.IsSimulated, machine?.Id, null, null, null, null, null, notes));
    }

    public async Task<Guid?> AuthenticateDeviceAsync(Guid deviceId, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var hash = await _db.DeviceIotConfigurations.AsNoTracking().Where(c => c.DeviceId == deviceId).Select(c => c.KeyHash).FirstOrDefaultAsync(ct);
        return DeviceKeys.Matches(key, hash) ? deviceId : null;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    /// <summary>The caller the device health pipeline acts as: the signed-in account, or the IoT service limited to the device's factory.</summary>
    private static AccessContext CallerFor(IotOrigin o, Device d) => o.Caller ?? IotQueries.Service(d.FactoryId, o.Actor);

    /// <summary>
    /// Telemetry and counts prove the device is connected: keep its health
    /// fresh through the device pipeline (a heartbeat without a message id),
    /// at most every half heartbeat interval.
    /// </summary>
    private async Task TouchAsync(Device d, IotOrigin o, DateTimeOffset occurredAt, string? firmware, CancellationToken ct)
    {
        var fresh = d.ConnectionStatus == ConnectionStatuses.Online && d.LastSeenAt is { } seen
                    && occurredAt - seen < TimeSpan.FromSeconds(Math.Max(15, d.HeartbeatIntervalSeconds / 2.0));
        if (fresh && (firmware is null || firmware == d.FirmwareVersion)) return;
        try
        {
            var r = await _devices.IngestAsync(CallerFor(o, d), d.Id, new DeviceMessage(DeviceMessageKinds.Heartbeat, occurredAt, null, firmware, null, null, null, null),
                o.Transport == IotTransports.Simulator ? DeviceMessageSources.Simulator : DeviceMessageSources.Device, ct);
            if (r.Outcome != AccessOutcome.Ok) _log.LogDebug("Device {Device} health not refreshed: {Code}", d.Code, r.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Refreshing the health of {Device} failed.", d.Code);
        }
    }

    private async Task<Guid?> ResolveFactoryAsync(string reference, CancellationToken ct)
    {
        if (Guid.TryParse(reference, out var id))
            return await _db.Factories.AsNoTracking().Where(f => f.Id == id).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
        var code = reference.Trim().ToUpperInvariant();
        return await _db.Factories.AsNoTracking().Where(f => f.Code.ToUpper() == code).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<Device?> ResolveDeviceAsync(Guid factoryId, string reference, CancellationToken ct)
    {
        if (Guid.TryParse(reference, out var id))
            return await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.FactoryId == factoryId, ct);
        if (!DeviceRules.IsCode(reference)) return null;
        var code = DeviceRules.NormaliseCode(reference);
        return await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.FactoryId == factoryId && d.Code == code, ct);
    }

    private static bool Matches(Device d, string reference) =>
        Guid.TryParse(reference, out var id) ? id == d.Id : string.Equals(DeviceRules.NormaliseCode(reference), d.Code, StringComparison.Ordinal);

    private static bool Matches(Machine m, string reference) =>
        Guid.TryParse(reference, out var id) ? id == m.Id : string.Equals(reference.Trim().ToUpperInvariant(), m.Code, StringComparison.Ordinal);

    private async Task RecordInvalidAsync(Device device, IotOrigin origin, string code, string error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (LastInvalidRecorded.TryGetValue(device.Id, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        LastInvalidRecorded[device.Id] = now;
        try
        {
            // A device event, not a monitoring alert: the message was ours to refuse.
            _db.DeviceEvents.Add(new DeviceEvent
            {
                DeviceId = device.Id, FactoryId = device.FactoryId, EventType = DeviceEventTypes.Error, Severity = DeviceSeverities.Warning,
                Source = origin.Transport == IotTransports.Simulator ? DeviceMessageSources.Simulator : DeviceMessageSources.System,
                IsSimulated = device.IsSimulated, OccurredAt = now, ReceivedAt = now, Code = "IOT_PAYLOAD_INVALID",
                Message = $"Message refused ({code}): {(error.Length > 400 ? error[..400] : error)}", RecordedByEmail = origin.Actor,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Recording an invalid message of {Device} failed.", device.Code);
        }
        finally { _db.ChangeTracker.Clear(); }
    }
}
