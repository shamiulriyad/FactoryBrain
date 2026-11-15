using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Devices;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Iot;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Devices;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Infrastructure.Iot;

/// <summary>
/// IoT configuration: how a device connects (protocol, gateway, MQTT client
/// id), its device key, a machine's counting and alert rules, maintenance
/// mode, and a one-click simulated sensor + counter for a machine. Every
/// change is audited. Device placement (line / workstation / machine) stays
/// in the device registry, which already validates it.
/// </summary>
public sealed class IotConfigService : IIotConfigService
{
    private readonly BunonBrainDbContext _db;
    private readonly IDeviceService _devices;
    private readonly MachineStateProcessor _states;
    private readonly IotOptions _options;

    public IotConfigService(BunonBrainDbContext db, IDeviceService devices, MachineStateProcessor states, IOptions<IotOptions> options)
    {
        _db = db; _devices = devices; _states = states; _options = options.Value;
    }

    private async Task<Device?> DeviceAsync(AccessContext caller, Guid id, CancellationToken ct) =>
        await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(d => d.Id == id, ct);

    private async Task<DeviceIotDto> DtoAsync(Device d, CancellationToken ct) =>
        (await IotQueries.DeviceDtosAsync(_db, new[] { d }, _options.Mqtt.TopicPrefix, ct))[0];

    public async Task<AccessResult<DeviceIotDto>> SetDeviceConfigAsync(AccessContext caller, Guid deviceId, DeviceIotConfigRequest r, string? traceId, CancellationToken ct)
    {
        var d = await DeviceAsync(caller, deviceId, ct);
        if (d is null) return AccessResult<DeviceIotDto>.NotFound("device_not_found", "Device not found.");
        if (!IotQueries.CanConfigure(caller, d.FactoryId)) return AccessResult<DeviceIotDto>.Forbidden("factory_scope_required", "Only factory-level device administrators configure devices.");
        var type = IotDeviceTypes.Parse(r.DeviceType);
        if (type is null) return AccessResult<DeviceIotDto>.Invalid("device_type_invalid", $"Device type: one of {string.Join(", ", IotDeviceTypes.All)}.");
        if (!IotDeviceTypes.FitsCategory(type, d.Category))
            return AccessResult<DeviceIotDto>.Invalid("device_type_category",
                $"A {IotDeviceTypes.Label(type).ToLowerInvariant()} is registered with category {IotDeviceTypes.Category(type)}; {d.Code} is {d.Category}.");
        var protocol = IotProtocols.Parse(r.Protocol);
        if (protocol is null) return AccessResult<DeviceIotDto>.Invalid("protocol_invalid", "Protocol: MQTT, HTTP or SIMULATOR.");
        if ((protocol == IotProtocols.Simulator) != d.IsSimulated)
            return AccessResult<DeviceIotDto>.Invalid("protocol_simulation", d.IsSimulated
                ? $"{d.Code} is a simulated device: its protocol is SIMULATOR."
                : $"{d.Code} is a real device: choose MQTT or HTTP (SIMULATOR is only for simulated devices).");
        var via = IotConnectionVia.Parse(r.ConnectionVia ?? IotConnectionVia.Direct);
        if (via is null) return AccessResult<DeviceIotDto>.Invalid("connection_via_invalid", "Connection: DIRECT, ESP32 or GATEWAY.");
        Device? gateway = null;
        if (r.GatewayDeviceId is { } gid)
        {
            if (gid == d.Id) return AccessResult<DeviceIotDto>.Invalid("gateway_self", "A device can't be its own gateway.");
            gateway = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == gid && x.FactoryId == d.FactoryId, ct);
            if (gateway is null) return AccessResult<DeviceIotDto>.Invalid("gateway_not_in_factory", "The gateway is not a device of this factory.");
            if (gateway.Category is not (DeviceCategories.Esp32Gateway or DeviceCategories.IndustrialGateway))
                return AccessResult<DeviceIotDto>.Invalid("gateway_not_gateway", $"{gateway.Code} is not an ESP32 or industrial gateway.");
            if (gateway.IsSimulated != d.IsSimulated) return AccessResult<DeviceIotDto>.Invalid("gateway_simulation", "Simulated and real devices can't share a gateway.");
        }
        if (via == IotConnectionVia.Direct && gateway is not null) return AccessResult<DeviceIotDto>.Invalid("gateway_direct", "A DIRECT device has no gateway.");
        var clientId = string.IsNullOrWhiteSpace(r.MqttClientId) ? null : r.MqttClientId.Trim();
        if (clientId is not null && (clientId.Length > 64 || !DeviceRules.IsMetadataKey(clientId.Replace(":", "-"))))
            return AccessResult<DeviceIotDto>.Invalid("mqtt_client_id_invalid", "MQTT client id: letters, digits, '_', '.', '-', ':' (up to 64).");
        if (clientId is not null && protocol != IotProtocols.Mqtt) return AccessResult<DeviceIotDto>.Invalid("mqtt_client_id_not_mqtt", "Only MQTT devices have an MQTT client id.");
        if (r.TelemetryIntervalSeconds is { } iv && iv is < 1 or > 86_400)
            return AccessResult<DeviceIotDto>.Invalid("interval_invalid", "Telemetry interval: 1–86400 seconds.");
        if (!DeviceRules.TryMetadata(r.Settings, out var settings, out var settingsError)) return AccessResult<DeviceIotDto>.Invalid("settings_invalid", settingsError!);

        var c = await _db.DeviceIotConfigurations.FirstOrDefaultAsync(x => x.DeviceId == d.Id, ct);
        if (r.ExpectedVersion is { } ev && (c?.Version ?? 0) != ev)
            return AccessResult<DeviceIotDto>.Conflict("config_changed", "The configuration was changed by someone else. Reload and try again.");
        var before = c is null ? null : $"{c.DeviceType}/{c.Protocol}/{c.ConnectionVia}";
        var now = DateTimeOffset.UtcNow;
        if (c is null)
        {
            c = new DeviceIotConfiguration { DeviceId = d.Id, FactoryId = d.FactoryId, CreatedAt = now, UpdatedByEmail = caller.Email };
            _db.DeviceIotConfigurations.Add(c);
        }
        else c.Version++;
        c.DeviceType = type;
        c.Protocol = protocol;
        c.ConnectionVia = via;
        c.GatewayDeviceId = gateway?.Id;
        c.MqttClientId = clientId;
        c.TelemetryIntervalSeconds = r.TelemetryIntervalSeconds;
        c.Settings = settings;
        // A device key only authenticates HTTP; switching away from HTTP revokes it.
        if (protocol != IotProtocols.Http) { c.KeyHash = null; c.KeyPrefix = null; c.KeyIssuedAt = null; c.KeyIssuedByEmail = null; }
        c.UpdatedAt = now;
        c.UpdatedByEmail = caller.Email;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "device_iot.configured", AccessAudit.EntityDeviceIot, d.Id.ToString(), before, $"{type}/{protocol}/{via}",
            $"{d.Code}: {IotDeviceTypes.Label(type)}, {protocol}{(gateway is null ? "" : $" via {gateway.Code}")}", traceId, d.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<DeviceIotDto>.Conflict("config_changed", "The configuration was changed by someone else. Reload and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg && pg.ConstraintName == DeviceIotConfigurationConfiguration.MqttClientIndex)
        {
            return AccessResult<DeviceIotDto>.Conflict("mqtt_client_id_taken", "Another device of this factory already uses that MQTT client id.");
        }
        _db.ChangeTracker.Clear();
        return AccessResult<DeviceIotDto>.Ok(await DtoAsync(d, ct));
    }

    public async Task<AccessResult<DeviceKeyIssued>> IssueKeyAsync(AccessContext caller, Guid deviceId, string? traceId, CancellationToken ct)
    {
        var d = await DeviceAsync(caller, deviceId, ct);
        if (d is null) return AccessResult<DeviceKeyIssued>.NotFound("device_not_found", "Device not found.");
        if (!IotQueries.CanConfigure(caller, d.FactoryId)) return AccessResult<DeviceKeyIssued>.Forbidden("factory_scope_required", "Only factory-level device administrators issue device keys.");
        if (d.IsSimulated) return AccessResult<DeviceKeyIssued>.Invalid("device_is_simulated", "Simulated devices don't authenticate: only the simulator sends their data.");
        var c = await _db.DeviceIotConfigurations.FirstOrDefaultAsync(x => x.DeviceId == d.Id, ct);
        if (c is null || c.Protocol != IotProtocols.Http)
            return AccessResult<DeviceKeyIssued>.Invalid("device_not_http", "Device keys authenticate HTTP devices: set the device's protocol to HTTP first (MQTT devices authenticate at the broker).");
        var key = DeviceKeys.New();
        var now = DateTimeOffset.UtcNow;
        var replaced = c.KeyHash is not null;
        c.KeyHash = DeviceKeys.Hash(key);
        c.KeyPrefix = DeviceKeys.Display(key);
        c.KeyIssuedAt = now;
        c.KeyIssuedByEmail = caller.Email;
        c.UpdatedAt = now;
        c.UpdatedByEmail = caller.Email;
        c.Version++;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, replaced ? "device_iot.key_rotated" : "device_iot.key_issued", AccessAudit.EntityDeviceIot, d.Id.ToString(),
            null, c.KeyPrefix, $"{d.Code}: device key {(replaced ? "rotated (the old key stops working now)" : "issued")}", traceId, d.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<DeviceKeyIssued>.Ok(new DeviceKeyIssued(d.Id, d.Code, key, c.KeyPrefix, now,
            "Copy the key now: it is shown only once. The device sends it as X-Device-Key with X-Device-Id."));
    }

    public async Task<AccessResult<DeviceIotDto>> RevokeKeyAsync(AccessContext caller, Guid deviceId, string? traceId, CancellationToken ct)
    {
        var d = await DeviceAsync(caller, deviceId, ct);
        if (d is null) return AccessResult<DeviceIotDto>.NotFound("device_not_found", "Device not found.");
        if (!IotQueries.CanConfigure(caller, d.FactoryId)) return AccessResult<DeviceIotDto>.Forbidden("factory_scope_required", "Only factory-level device administrators revoke device keys.");
        var c = await _db.DeviceIotConfigurations.FirstOrDefaultAsync(x => x.DeviceId == d.Id, ct);
        if (c?.KeyHash is not null)
        {
            c.KeyHash = null;
            c.KeyPrefix = null;
            c.KeyIssuedAt = null;
            c.KeyIssuedByEmail = null;
            c.UpdatedAt = DateTimeOffset.UtcNow;
            c.UpdatedByEmail = caller.Email;
            c.Version++;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "device_iot.key_revoked", AccessAudit.EntityDeviceIot, d.Id.ToString(), null, null,
                $"{d.Code}: device key revoked", traceId, d.FactoryId));
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
        return AccessResult<DeviceIotDto>.Ok(await DtoAsync(d, ct));
    }

    public async Task<AccessResult<MachineProfileDto>> SetProfileAsync(AccessContext caller, Guid machineId, MachineProfileRequest r, string? traceId, CancellationToken ct)
    {
        var m = await IotQueries.MachinesInScope(_db, caller).FirstOrDefaultAsync(x => x.Id == machineId, ct);
        if (m is null) return AccessResult<MachineProfileDto>.NotFound("machine_not_found", "Machine not found.");
        if (!IotQueries.CanConfigure(caller, m.FactoryId)) return AccessResult<MachineProfileDto>.Forbidden("factory_scope_required", "Only factory-level device administrators configure machine rules.");
        var kind = MachineKinds.Parse(r.MachineKind);
        if (kind is null) return AccessResult<MachineProfileDto>.Invalid("machine_kind_invalid", "Machine kind: SEWING, CUTTING, PRESSING or OTHER.");
        var mode = CountingModes.Parse(r.CountingMode);
        if (mode is null) return AccessResult<MachineProfileDto>.Invalid("counting_mode_invalid", "Counting: PIECE, PULSES_PER_PIECE or CYCLE_NOT_PIECE.");
        if (r.PulsesPerPiece is < 1 or > 1000) return AccessResult<MachineProfileDto>.Invalid("pulses_per_piece_invalid", "Pulses per piece: 1–1000.");
        if (mode != CountingModes.PulsesPerPiece && r.PulsesPerPiece != 1)
            return AccessResult<MachineProfileDto>.Invalid("pulses_per_piece_mode", "Pulses per piece only applies to PULSES_PER_PIECE (keep it 1 otherwise).");
        string? Check(double? v, double min, double max, string name) => v is { } x && (!double.IsFinite(x) || x < min || x > max) ? $"{name}: {min}–{max}." : null;
        string? CheckInt(int? v, int min, int max, string name) => v is { } x && (x < min || x > max) ? $"{name}: {min}–{max}." : null;
        var error = Check(r.RunningPowerWatts, 0.1, 1_000_000, "Running power (W)") ?? Check(r.IdlePowerWatts, 0, 1_000_000, "Idle power (W)")
                    ?? Check(r.MaxTemperatureC, -20, 400, "Max temperature (°C)") ?? Check(r.MaxVibrationMmS, 0.1, 10_000, "Max vibration (mm/s)")
                    ?? CheckInt(r.StaleAfterSeconds, 30, 86_400, "Unknown after (s)") ?? CheckInt(r.StopAlertAfterSeconds, 30, 86_400, "Unexpected stop after (s)")
                    ?? CheckInt(r.LongIdleAlertSeconds, 60, 86_400, "Long idle after (s)") ?? CheckInt(r.CounterSilenceAlertSeconds, 60, 86_400, "Counter silent after (s)")
                    ?? CheckInt(r.FlapTransitionsPer5Min, 3, 500, "Transitions per 5 min");
        if (error is not null) return AccessResult<MachineProfileDto>.Invalid("profile_value_invalid", error);
        if (r.IdlePowerWatts is not null && (r.RunningPowerWatts is null || r.IdlePowerWatts >= r.RunningPowerWatts))
            return AccessResult<MachineProfileDto>.Invalid("power_thresholds_invalid", "The idle power threshold must be below the running threshold (and needs one).");

        var p = await _db.MachineMonitoringProfiles.FirstOrDefaultAsync(x => x.MachineId == m.Id, ct);
        if (r.ExpectedVersion is { } ev && (p?.Version ?? 0) != ev)
            return AccessResult<MachineProfileDto>.Conflict("profile_changed", "The machine's rules were changed by someone else. Reload and try again.");
        var now = DateTimeOffset.UtcNow;
        var before = p is null ? "defaults" : $"{p.MachineKind}/{p.CountingMode}x{p.PulsesPerPiece}";
        if (p is null)
        {
            p = new MachineMonitoringProfile { MachineId = m.Id, FactoryId = m.FactoryId, CreatedAt = now, UpdatedByEmail = caller.Email };
            _db.MachineMonitoringProfiles.Add(p);
        }
        else p.Version++;
        p.MachineKind = kind;
        p.CountingMode = mode;
        p.PulsesPerPiece = r.PulsesPerPiece;
        p.RunningPowerWatts = r.RunningPowerWatts;
        p.IdlePowerWatts = r.IdlePowerWatts;
        p.StaleAfterSeconds = r.StaleAfterSeconds;
        p.StopAlertAfterSeconds = r.StopAlertAfterSeconds;
        p.LongIdleAlertSeconds = r.LongIdleAlertSeconds;
        p.MaxTemperatureC = r.MaxTemperatureC;
        p.MaxVibrationMmS = r.MaxVibrationMmS;
        p.CounterSilenceAlertSeconds = r.CounterSilenceAlertSeconds;
        p.FlapTransitionsPer5Min = r.FlapTransitionsPer5Min;
        p.AlertsEnabled = r.AlertsEnabled;
        p.UpdatedAt = now;
        p.UpdatedByEmail = caller.Email;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "machine_profile.updated", AccessAudit.EntityMachineProfile, m.Id.ToString(), before,
            $"{kind}/{mode}x{r.PulsesPerPiece}", $"{m.Code}: monitoring rules updated (counting {mode}{(mode == CountingModes.PulsesPerPiece ? $" ×{r.PulsesPerPiece}" : "")}, alerts {(r.AlertsEnabled ? "on" : "off")})",
            traceId, m.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<MachineProfileDto>.Conflict("profile_changed", "The machine's rules were changed by someone else. Reload and try again."); }
        _db.ChangeTracker.Clear();
        return AccessResult<MachineProfileDto>.Ok(IotQueries.ProfileDto(m, await _db.MachineMonitoringProfiles.AsNoTracking().FirstAsync(x => x.MachineId == m.Id, ct)));
    }

    public async Task<AccessResult<MachineDto>> SetMaintenanceAsync(AccessContext caller, Guid machineId, MaintenanceRequest r, string? traceId, CancellationToken ct)
    {
        var m = await IotQueries.MachinesInScope(_db, caller).FirstOrDefaultAsync(x => x.Id == machineId, ct);
        if (m is null) return AccessResult<MachineDto>.NotFound("machine_not_found", "Machine not found.");
        if (!IotQueries.CanConfigure(caller, m.FactoryId)) return AccessResult<MachineDto>.Forbidden("factory_scope_required", "Only factory-level device administrators set maintenance.");
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? null : r.Reason.Trim();
        if (r.InMaintenance && (reason is null || reason.Length < 3)) return AccessResult<MachineDto>.Invalid("reason_required", "Say why the machine is in maintenance.");
        if (reason is { Length: > 300 }) return AccessResult<MachineDto>.Invalid("reason_too_long", "Reasons are at most 300 characters.");
        var changed = await _states.SetMaintenanceAsync(m, r.InMaintenance, reason, caller.Email, ct);
        if (changed)
        {
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, r.InMaintenance ? "machine.maintenance_started" : "machine.maintenance_ended", AccessAudit.EntityMachine,
                m.Id.ToString(), null, r.InMaintenance ? "MAINTENANCE" : "UNKNOWN", $"{m.Code}: maintenance {(r.InMaintenance ? "started" : "ended")}{(reason is null ? "" : $" — {reason}")}",
                traceId, m.FactoryId));
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
        return AccessResult<MachineDto>.Ok((await IotQueries.MachineDtosAsync(_db, new[] { m }, ct))[0]);
    }

    public async Task<AccessResult<SimulatedMachineSetupResult>> SetUpSimulatedMachineAsync(AccessContext caller, SimulatedMachineSetupRequest r, string? traceId, CancellationToken ct)
    {
        var m = await IotQueries.MachinesInScope(_db, caller).FirstOrDefaultAsync(x => x.Id == r.MachineId, ct);
        if (m is null) return AccessResult<SimulatedMachineSetupResult>.NotFound("machine_not_found", "Machine not found.");
        if (!IotQueries.CanConfigure(caller, m.FactoryId))
            return AccessResult<SimulatedMachineSetupResult>.Forbidden("factory_scope_required", "Only factory-level device administrators register devices.");
        if (m.WorkstationId is null)
            return AccessResult<SimulatedMachineSetupResult>.Invalid("machine_not_placed", $"Place {m.Code} at a workstation first, so its counts belong to a line.");
        var existing = await _db.Devices.AsNoTracking().Where(d => d.MachineId == m.Id && d.Status != DeviceStatuses.Disabled).ToListAsync(ct);
        if (existing.Any(d => !d.IsSimulated))
            return AccessResult<SimulatedMachineSetupResult>.Conflict("machine_has_real_devices",
                $"{m.Code} has real sensors. Simulated devices are only added to machines without real ones, so simulated data can't mix with real data.");
        var notes = new List<string>();
        var created = new List<Device>();
        async Task<string?> Ensure(string suffix, string category, string type, string name)
        {
            var code = DeviceRules.NormaliseCode($"{m.Code}-SIM-{suffix}");
            var dev = existing.FirstOrDefault(d => d.Code == code) ?? await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.FactoryId == m.FactoryId && d.Code == code, ct);
            if (dev is null)
            {
                var res = await _devices.CreateAsync(caller, new CreateDeviceRequest(m.FactoryId, code, name, category, true, null, m.WorkstationId, m.Id,
                    HeartbeatIntervalSeconds: 30, OfflineAfterSeconds: 120), traceId, ct);
                if (res.Outcome != AccessOutcome.Ok) return $"{code}: {res.Message}";
                dev = await _db.Devices.AsNoTracking().FirstAsync(d => d.Id == res.Value!.Id, ct);
                notes.Add($"Registered simulated device {code}.");
            }
            else if (dev.MachineId != m.Id) return $"{code} already exists for another machine.";
            else notes.Add($"{code} already registered.");
            if (!await _db.DeviceIotConfigurations.AnyAsync(c => c.DeviceId == dev.Id, ct))
            {
                _db.DeviceIotConfigurations.Add(new DeviceIotConfiguration
                {
                    DeviceId = dev.Id, FactoryId = dev.FactoryId, DeviceType = type, Protocol = IotProtocols.Simulator, ConnectionVia = IotConnectionVia.Direct,
                    TelemetryIntervalSeconds = 5, UpdatedByEmail = caller.Email,
                });
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }
            created.Add(dev);
            return null;
        }
        var kind = MachineKinds.Guess(m.MachineType);
        var sensorType = kind switch { MachineKinds.Cutting => IotDeviceTypes.CuttingMachineSensor, MachineKinds.Pressing => IotDeviceTypes.PressingMachineSensor, _ => IotDeviceTypes.SewingMachineSensor };
        var e1 = await Ensure("S", DeviceCategories.MachineSensor, sensorType, $"Simulated sensor of {m.Code}");
        if (e1 is not null) return AccessResult<SimulatedMachineSetupResult>.Conflict("setup_failed", e1);
        if (r.WithCounter)
        {
            var e2 = await Ensure("C", DeviceCategories.ProductionCounter, IotDeviceTypes.PieceCounter, $"Simulated piece counter of {m.Code}");
            if (e2 is not null) return AccessResult<SimulatedMachineSetupResult>.Conflict("setup_failed", e2);
        }
        // Simulated power thresholds so power-only scenarios can derive a state (only when no rules are set yet).
        if (!await _db.MachineMonitoringProfiles.AnyAsync(p => p.MachineId == m.Id, ct))
        {
            var d = MachineRules.Defaults(kind);
            _db.MachineMonitoringProfiles.Add(new MachineMonitoringProfile
            {
                MachineId = m.Id, FactoryId = m.FactoryId, MachineKind = kind, CountingMode = d.CountingMode, PulsesPerPiece = d.PulsesPerPiece,
                RunningPowerWatts = 150, IdlePowerWatts = 20, StaleAfterSeconds = d.StaleAfterSeconds, StopAlertAfterSeconds = d.StopAlertAfterSeconds,
                LongIdleAlertSeconds = d.LongIdleAlertSeconds, MaxTemperatureC = 70, MaxVibrationMmS = 12, CounterSilenceAlertSeconds = d.CounterSilenceAlertSeconds,
                FlapTransitionsPer5Min = d.FlapTransitionsPer5Min, AlertsEnabled = true, UpdatedByEmail = caller.Email,
            });
            await _db.SaveChangesAsync(ct);
            notes.Add("Machine rules set for the demo: running ≥ 150 W, idle ≥ 20 W, temperature limit 70 °C, vibration limit 12 mm/s.");
        }
        _db.ChangeTracker.Clear();
        return AccessResult<SimulatedMachineSetupResult>.Ok(new SimulatedMachineSetupResult(m.Id,
            await IotQueries.DeviceDtosAsync(_db, created, _options.Mqtt.TopicPrefix, ct), notes));
    }
}

/// <summary>Counter resets and production corrections by people: never edit counts, always add an audited row.</summary>
public sealed class ProductionCorrectionService : IProductionCorrectionService
{
    private readonly BunonBrainDbContext _db;
    private readonly ProductionCounterProcessor _counters;

    public ProductionCorrectionService(BunonBrainDbContext db, ProductionCounterProcessor counters) { _db = db; _counters = counters; }

    public async Task<AccessResult<CounterEventDto>> ResetCounterAsync(AccessContext caller, CounterResetRequest r, string? traceId, CancellationToken ct)
    {
        var d = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(x => x.Id == r.DeviceId, ct);
        if (d is null) return AccessResult<CounterEventDto>.NotFound("device_not_found", "Device not found.");
        if (!IotQueries.CanCorrect(caller, d.FactoryId) && !IotQueries.CanConfigure(caller, d.FactoryId))
            return AccessResult<CounterEventDto>.Forbidden("factory_scope_required", "Counter resets need production.manage or devices.manage at factory level.");
        var config = await _db.DeviceIotConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.DeviceId == d.Id, ct);
        if (!IotQueries.IsCounter(d, config)) return AccessResult<CounterEventDto>.Invalid("not_a_counter", $"{d.Code} is not a production counter.");
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length is < 5 or > 500) return AccessResult<CounterEventDto>.Invalid("reason_required", "Give the reason for the reset (5–500 characters).");
        if (r.Value is < 0) return AccessResult<CounterEventDto>.Invalid("value_invalid", "The counter value after the reset is 0 or more.");
        var machine = d.MachineId is { } mid ? await _db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid, ct) : null;
        var outcome = await _counters.ApplyAsync(d, machine, $"manual-reset-{Guid.NewGuid():N}", new IotCounterReading(CounterModes.Reset, r.Value ?? 0),
            DateTimeOffset.UtcNow, IotTransports.Manual, caller.Email, ct);
        if (outcome.Outcome == "Rejected") return AccessResult<CounterEventDto>.Conflict(outcome.Code!, outcome.Error!);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "production_counter.reset", AccessAudit.EntityProductionCounter, d.Id.ToString(), null, (r.Value ?? 0).ToString(),
            $"{d.Code}: counter reset to {r.Value ?? 0} — {reason}", traceId, d.FactoryId));
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        var e = outcome.Event!;
        return AccessResult<CounterEventDto>.Ok(new CounterEventDto(e.Id, d.Id, d.Code, machine is null ? null : new MonRef(machine.Id, machine.Code, machine.MachineType),
            null, new MonLine(e.LineId, e.LineId), e.CounterMode, e.RawValue, e.Pulses, e.Pieces, e.Status, e.Note, e.ShiftCode, e.ProductionDate, e.SourceType,
            e.Transport, e.MessageId, e.OccurredAt, e.ReceivedAt));
    }

    public async Task<AccessResult<ProductionCorrectionDto>> CorrectAsync(AccessContext caller, ProductionCorrectionRequest r, string? traceId, CancellationToken ct)
    {
        if (!IotQueries.CanCorrect(caller, r.FactoryId))
            return AccessResult<ProductionCorrectionDto>.Forbidden("factory_scope_required", "Production corrections need production.manage at factory level.");
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length is < 5 or > 500) return AccessResult<ProductionCorrectionDto>.Invalid("reason_required", "Give the reason for the correction (5–500 characters).");
        if (r.PiecesDelta == 0 || Math.Abs(r.PiecesDelta) > 100_000) return AccessResult<ProductionCorrectionDto>.Invalid("delta_invalid", "Correct by −100000 … 100000 pieces (not 0).");
        var source = IotSourceTypes.Parse(r.SourceType ?? IotSourceTypes.Real);
        if (source is null) return AccessResult<ProductionCorrectionDto>.Invalid("source_invalid", "Source: REAL or SIMULATED.");
        Machine? machine = null;
        Workstation? ws = null;
        if (r.MachineId is { } mid)
        {
            machine = await _db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid && m.FactoryId == r.FactoryId, ct);
            if (machine is null) return AccessResult<ProductionCorrectionDto>.Invalid("machine_not_in_factory", "The machine is not in this factory.");
        }
        var wsId = r.WorkstationId ?? machine?.WorkstationId;
        if (wsId is { } w)
        {
            ws = await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == w && x.FactoryId == r.FactoryId, ct);
            if (ws is null) return AccessResult<ProductionCorrectionDto>.Invalid("workstation_not_in_factory", "The workstation is not in this factory.");
            if (machine?.WorkstationId is { } mw && mw != ws.Id) return AccessResult<ProductionCorrectionDto>.Invalid("machine_not_at_workstation", "The machine stands at another workstation.");
        }
        var lineId = string.IsNullOrWhiteSpace(r.LineId) ? ws?.LineId : r.LineId.Trim();
        if (lineId is null) return AccessResult<ProductionCorrectionDto>.Invalid("line_required", "Name the line (or a workstation / machine on it).");
        if (!await _db.ProductionLines.AnyAsync(l => l.Id == lineId && l.FactoryId == r.FactoryId, ct))
            return AccessResult<ProductionCorrectionDto>.Invalid("line_not_in_factory", "The line is not in this factory.");
        if (ws is not null && ws.LineId != lineId) return AccessResult<ProductionCorrectionDto>.Invalid("workstation_not_on_line", "The workstation is on another line.");
        if (caller.Scope == RoleScope.Line && !caller.LineIds.Contains(lineId))
            return AccessResult<ProductionCorrectionDto>.Forbidden("line_out_of_scope", "You can only correct counts of your own lines.");
        Shift? shift = null;
        if (r.ShiftId is { } sid)
        {
            shift = await _db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.FactoryId == r.FactoryId, ct);
            if (shift is null) return AccessResult<ProductionCorrectionDto>.Invalid("shift_not_in_factory", "The shift is not in this factory.");
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (r.ProductionDate > today.AddDays(1) || r.ProductionDate < today.AddDays(-93))
            return AccessResult<ProductionCorrectionDto>.Invalid("date_invalid", "Corrections are for the last 93 days.");
        var hour = r.HourStart is { } h ? new DateTimeOffset(h.UtcDateTime.Year, h.UtcDateTime.Month, h.UtcDateTime.Day, h.UtcDateTime.Hour, 0, 0, TimeSpan.Zero) : (DateTimeOffset?)null;
        var c = new ProductionCorrection
        {
            FactoryId = r.FactoryId, MachineId = machine?.Id, WorkstationId = ws?.Id, LineId = lineId, ProductionDate = r.ProductionDate, ShiftId = shift?.Id,
            ShiftCode = shift?.Code, HourStart = hour, PiecesDelta = r.PiecesDelta, Reason = reason, SourceType = source, CreatedByUserId = caller.UserId,
            CreatedByEmail = caller.Email, CreatedByRole = caller.PrimaryRole, CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.ProductionCorrections.Add(c);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "production.corrected", AccessAudit.EntityProductionCorrection, c.Id.ToString(), null,
            r.PiecesDelta.ToString("+0;-0"), $"{lineId}{(machine is null ? "" : $" / {machine.Code}")} {r.ProductionDate:yyyy-MM-dd}{(shift is null ? "" : $" {shift.Code}")}: " +
            $"{r.PiecesDelta:+0;-0} pieces ({source.ToLowerInvariant()}) — {reason}", traceId, r.FactoryId));
        await _db.SaveChangesAsync(ct);
        var lineName = await _db.ProductionLines.AsNoTracking().Where(l => l.Id == lineId).Select(l => l.Name).FirstAsync(ct);
        return AccessResult<ProductionCorrectionDto>.Ok(new ProductionCorrectionDto(c.Id, c.FactoryId, new MonLine(lineId, lineName),
            ws is null ? null : new MonRef(ws.Id, ws.Code, ws.Name), machine is null ? null : new MonRef(machine.Id, machine.Code, machine.MachineType), c.ProductionDate,
            c.ShiftCode, c.HourStart, c.PiecesDelta, c.Reason, c.SourceType, c.CreatedByEmail, c.CreatedByRole, c.CreatedAt));
    }
}
