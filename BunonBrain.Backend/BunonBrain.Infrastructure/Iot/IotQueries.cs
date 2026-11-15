using System.Text.Json;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Iot;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Iot;

/// <summary>
/// Reach and mapping shared by the IoT services. Machines: Super Admin every
/// factory, factory roles their factories, line roles the machines standing
/// at workstations of their lines. Seeing machines needs monitoring.view,
/// devices.view or production.view; production counts need production.view
/// or monitoring.view; configuration needs devices.manage at factory level.
/// </summary>
public static class IotQueries
{
    /// <summary>The service identity the MQTT bridge, device keys and the simulator act as (never a person).</summary>
    public static readonly Guid ServiceId = new("6b1f1c7e-0c2d-4f7a-9d3e-10f0a0b0c0d1");
    public const string ServiceName = "iot-pipeline";

    public static bool CanSeeMachines(AccessContext c) => c.HasAny(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView);
    public static bool CanSeeProduction(AccessContext c) => c.HasAny(Permissions.ProductionView, Permissions.MonitoringView);
    public static bool CanConfigure(AccessContext c, Guid factoryId) =>
        c.Has(Permissions.DevicesManage) && c.Scope >= RoleScope.Factory && c.CanAccessFactory(factoryId);
    public static bool CanCorrect(AccessContext c, Guid factoryId) =>
        c.Has(Permissions.ProductionManage) && c.Scope >= RoleScope.Factory && c.CanAccessFactory(factoryId);

    /// <summary>A service caller limited to one factory, for pipeline steps that reuse services written for callers.</summary>
    public static AccessContext Service(Guid factoryId, string actor) =>
        new(ServiceId, actor, Array.Empty<string>(), new[] { Permissions.DevicesManage, Permissions.DevicesView }, new[] { factoryId }, Array.Empty<string>(),
            serviceName: ServiceName);

    public static IQueryable<Machine> MachinesInScope(BunonBrainDbContext db, AccessContext caller)
    {
        var all = db.Machines.AsNoTracking();
        if (!CanSeeMachines(caller)) return all.Where(_ => false);
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => all,
            RoleScope.Factory => all.Where(m => factories.Contains(m.FactoryId)),
            RoleScope.Line => all.Where(m => factories.Contains(m.FactoryId) && m.WorkstationId != null
                                             && db.Workstations.Any(w => w.Id == m.WorkstationId && lines.Contains(w.LineId))),
            _ => all.Where(_ => false),
        };
    }

    public static IQueryable<ProductionCounterEvent> CountsInScope(BunonBrainDbContext db, AccessContext caller)
    {
        var all = db.ProductionCounterEvents.AsNoTracking();
        if (!CanSeeProduction(caller)) return all.Where(_ => false);
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => all,
            RoleScope.Factory => all.Where(e => factories.Contains(e.FactoryId)),
            RoleScope.Line => all.Where(e => factories.Contains(e.FactoryId) && lines.Contains(e.LineId)),
            _ => all.Where(_ => false),
        };
    }

    public static IQueryable<ProductionCorrection> CorrectionsInScope(BunonBrainDbContext db, AccessContext caller)
    {
        var all = db.ProductionCorrections.AsNoTracking();
        if (!CanSeeProduction(caller)) return all.Where(_ => false);
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => all,
            RoleScope.Factory => all.Where(e => factories.Contains(e.FactoryId)),
            RoleScope.Line => all.Where(e => factories.Contains(e.FactoryId) && lines.Contains(e.LineId)),
            _ => all.Where(_ => false),
        };
    }

    public static string SourceOf(string? source) => IotSourceTypes.Parse(source) ?? IotSourceTypes.Real;

    public static MachineRules Rules(MachineMonitoringProfile? p, Machine m) => p is null
        ? MachineRules.Defaults(MachineKinds.Guess(m.MachineType))
        : new MachineRules(p.CountingMode, p.PulsesPerPiece, p.RunningPowerWatts, p.IdlePowerWatts, p.StaleAfterSeconds, p.StopAlertAfterSeconds,
            p.LongIdleAlertSeconds, p.MaxTemperatureC, p.MaxVibrationMmS, p.CounterSilenceAlertSeconds, p.FlapTransitionsPer5Min, p.AlertsEnabled);

    public static MachineProfileDto ProfileDto(Machine m, MachineMonitoringProfile? p)
    {
        if (p is not null)
            return new MachineProfileDto(m.Id, p.MachineKind, p.CountingMode, p.PulsesPerPiece, p.RunningPowerWatts, p.IdlePowerWatts, p.StaleAfterSeconds,
                p.StopAlertAfterSeconds, p.LongIdleAlertSeconds, p.MaxTemperatureC, p.MaxVibrationMmS, p.CounterSilenceAlertSeconds, p.FlapTransitionsPer5Min,
                p.AlertsEnabled, false, p.UpdatedAt, p.UpdatedByEmail, p.Version);
        var kind = MachineKinds.Guess(m.MachineType);
        var r = MachineRules.Defaults(kind);
        return new MachineProfileDto(m.Id, kind, r.CountingMode, r.PulsesPerPiece, r.RunningPowerWatts, r.IdlePowerWatts, r.StaleAfterSeconds,
            r.StopAlertAfterSeconds, r.LongIdleAlertSeconds, r.MaxTemperatureC, r.MaxVibrationMmS, r.CounterSilenceAlertSeconds, r.FlapTransitionsPer5Min,
            r.AlertsEnabled, true, null, null, 0);
    }

    public static string[] Conditions(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    public static string DeviceTypeOf(Device d, DeviceIotConfiguration? c) => c?.DeviceType ?? IotDeviceTypes.Default(d.Category);

    public static string ProtocolOf(Device d, DeviceIotConfiguration? c) => c?.Protocol ?? (d.IsSimulated ? IotProtocols.Simulator : IotProtocols.Http);

    /// <summary>Whether a device's telemetry decides its machine's state.</summary>
    public static bool DecidesState(Device d, DeviceIotConfiguration? c) =>
        d.MachineId is not null && d.Status != DeviceStatuses.Disabled && IotDeviceTypes.MachineState.Contains(DeviceTypeOf(d, c))
        && d.Category is not (DeviceCategories.ProductionCounter or DeviceCategories.RfidReader or DeviceCategories.Camera);

    public static bool IsCounter(Device d, DeviceIotConfiguration? c) =>
        d.Status != DeviceStatuses.Disabled && (d.Category == DeviceCategories.ProductionCounter || DeviceTypeOf(d, c) == IotDeviceTypes.PieceCounter);

    public static DeviceHealth Health(Device d, DateTimeOffset now) =>
        DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, now);

    /// <summary>A machine's data source: REAL as soon as one real device is bound to it; simulated devices then can't move its state.</summary>
    public static string MachineSource(IEnumerable<Device> devices) =>
        devices.Any(d => !d.IsSimulated && d.Status != DeviceStatuses.Disabled) ? IotSourceTypes.Real : IotSourceTypes.Simulated;

    // ─── Machines ──────────────────────────────────────────────────────────

    public static async Task<IReadOnlyList<MachineDto>> MachineDtosAsync(BunonBrainDbContext db, IReadOnlyList<Machine> machines, CancellationToken ct)
    {
        if (machines.Count == 0) return Array.Empty<MachineDto>();
        var now = DateTimeOffset.UtcNow;
        var ids = machines.Select(m => m.Id).ToList();
        var wsIds = machines.Where(m => m.WorkstationId != null).Select(m => m.WorkstationId!.Value).Distinct().ToList();
        var workstations = await db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var lineIds = workstations.Values.Select(w => w.LineId).Distinct().ToList();
        var lines = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var deptIds = workstations.Values.Select(w => w.DepartmentId).Distinct().ToList();
        var departments = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var floorIds = workstations.Values.Where(w => w.FloorId != null).Select(w => w.FloorId!.Value).Distinct().ToList();
        var floors = await db.ProductionFloors.AsNoTracking().Where(f => floorIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var factoryIds = machines.Select(m => m.FactoryId).Distinct().ToList();
        var factories = await db.Factories.AsNoTracking().Where(f => factoryIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var states = await db.MachineStates.AsNoTracking().Where(s => ids.Contains(s.MachineId)).ToDictionaryAsync(s => s.MachineId, ct);
        var profiles = await db.MachineMonitoringProfiles.AsNoTracking().Where(p => ids.Contains(p.MachineId)).ToDictionaryAsync(p => p.MachineId, ct);
        var devices = await db.Devices.AsNoTracking().Where(d => d.MachineId != null && ids.Contains(d.MachineId.Value)).ToListAsync(ct);
        var devIds = devices.Select(d => d.Id).ToList();
        var configs = await db.DeviceIotConfigurations.AsNoTracking().Where(c => devIds.Contains(c.DeviceId)).ToDictionaryAsync(c => c.DeviceId, ct);

        return machines.Select(m =>
        {
            Workstation? ws = m.WorkstationId is { } w ? workstations.GetValueOrDefault(w) : null;
            states.TryGetValue(m.Id, out var s);
            profiles.TryGetValue(m.Id, out var p);
            var mine = devices.Where(d => d.MachineId == m.Id).OrderBy(d => d.Code).ToList();
            var devDtos = mine.Select(d =>
            {
                configs.TryGetValue(d.Id, out var c);
                var h = Health(d, now);
                var type = DeviceTypeOf(d, c);
                return new MachineDeviceDto(d.Id, d.Code, d.Name, d.Category, type, IotDeviceTypes.Label(type), ProtocolOf(d, c), d.IsSimulated, h.Health, h.Label,
                    d.LastHeartbeatAt, d.LastSeenAt, d.FirmwareVersion, IsCounter(d, c), DecidesState(d, c));
            }).ToList();
            var monitored = devDtos.Any(d => d.DecidesState || d.IsCounter);
            var lastHeartbeat = mine.Where(d => d.LastHeartbeatAt != null).Select(d => d.LastHeartbeatAt).DefaultIfEmpty(null).Max();
            var simulated = mine.Count > 0 && MachineSource(mine) == IotSourceTypes.Simulated;
            return new MachineDto(m.Id, m.Code, m.MachineType, p?.MachineKind ?? MachineKinds.Guess(m.MachineType), m.FactoryId,
                factories.GetValueOrDefault(m.FactoryId, "?"),
                ws is null ? null : new MonRef(ws.Id, ws.Code, ws.Name),
                ws is null ? null : new MonLine(ws.LineId, lines.GetValueOrDefault(ws.LineId, ws.LineId)),
                ws is null ? null : departments.GetValueOrDefault(ws.DepartmentId),
                ws?.FloorId is { } fl ? floors.GetValueOrDefault(fl) : null,
                m.IsActive, monitored,
                s?.State ?? MachineStates.Unknown, s?.Since, s?.Reason ?? (monitored ? "No telemetry received yet" : "No machine sensor bound to this machine"),
                s?.SourceType, simulated, s?.PowerWatts, s?.CurrentAmps, s?.TemperatureC, s?.VibrationMmS, s?.ReadingsAt, s?.LastTelemetryAt, lastHeartbeat,
                s?.LastCountAt, s?.ErrorCode, s?.InMaintenance ?? false, s?.MaintenanceReason, Conditions(s?.ActiveConditions), devDtos);
        }).ToList();
    }

    public static async Task<IReadOnlyList<DeviceIotDto>> DeviceDtosAsync(BunonBrainDbContext db, IReadOnlyList<Device> devices, string? topicPrefix,
        CancellationToken ct)
    {
        if (devices.Count == 0) return Array.Empty<DeviceIotDto>();
        var now = DateTimeOffset.UtcNow;
        var ids = devices.Select(d => d.Id).ToList();
        var configs = await db.DeviceIotConfigurations.AsNoTracking().Where(c => ids.Contains(c.DeviceId)).ToDictionaryAsync(c => c.DeviceId, ct);
        var gatewayIds = configs.Values.Where(c => c.GatewayDeviceId != null).Select(c => c.GatewayDeviceId!.Value).Distinct().ToList();
        var gateways = await db.Devices.AsNoTracking().Where(d => gatewayIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => (d.Code, d.Name), ct);
        var machineIds = devices.Where(d => d.MachineId != null).Select(d => d.MachineId!.Value).Distinct().ToList();
        var machines = await db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var wsIds = devices.Where(d => d.WorkstationId != null).Select(d => d.WorkstationId!.Value)
            .Concat(machines.Values.Where(m => m.WorkstationId != null).Select(m => m.WorkstationId!.Value)).Distinct().ToList();
        var workstations = await db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var lineIds = devices.Where(d => d.LineId != null).Select(d => d.LineId!).Concat(workstations.Values.Select(w => w.LineId)).Distinct().ToList();
        var lines = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var factoryIds = devices.Select(d => d.FactoryId).Distinct().ToList();
        var factories = await db.Factories.AsNoTracking().Where(f => factoryIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var since = now.AddHours(-1);
        var telemetry = await db.MachineTelemetry.AsNoTracking().Where(t => ids.Contains(t.DeviceId) && t.ReceivedAt >= since)
            .GroupBy(t => t.DeviceId).Select(g => new { g.Key, N = g.LongCount() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var counts = await db.ProductionCounterEvents.AsNoTracking().Where(t => ids.Contains(t.DeviceId) && t.ReceivedAt >= since)
            .GroupBy(t => t.DeviceId).Select(g => new { g.Key, N = g.LongCount() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        return devices.Select(d =>
        {
            configs.TryGetValue(d.Id, out var c);
            var h = Health(d, now);
            var type = DeviceTypeOf(d, c);
            Machine? m = d.MachineId is { } mid ? machines.GetValueOrDefault(mid) : null;
            var wsId = m?.WorkstationId ?? d.WorkstationId;
            Workstation? ws = wsId is { } w ? workstations.GetValueOrDefault(w) : null;
            var lineId = ws?.LineId ?? d.LineId;
            var factory = factories.GetValueOrDefault(d.FactoryId, "?");
            var topics = ProtocolOf(d, c) == IotProtocols.Mqtt
                ? new List<string>
                {
                    IotTopics.Device(topicPrefix, factory, d.Code, IotTopics.Telemetry),
                    IotTopics.Device(topicPrefix, factory, d.Code, IotTopics.Status),
                    IotTopics.Device(topicPrefix, factory, d.Code, IotTopics.Heartbeat),
                }
                : new List<string>();
            if (topics.Count > 0 && IsCounter(d, c) && m is not null) topics.Add(IotTopics.Machine(topicPrefix, factory, m.Code));
            return new DeviceIotDto(d.Id, d.Code, d.Name, d.Category, d.FactoryId, factory, d.IsSimulated, c is not null, type, IotDeviceTypes.Label(type),
                ProtocolOf(d, c), c?.ConnectionVia ?? IotConnectionVia.Direct,
                c?.GatewayDeviceId is { } g && gateways.TryGetValue(g, out var gw) ? new MonRef(g, gw.Code, gw.Name) : null,
                c?.MqttClientId, c?.TelemetryIntervalSeconds, DeviceRules.ReadMetadata(c?.Settings),
                m is null ? null : new MonRef(m.Id, m.Code, m.MachineType),
                ws is null ? null : new MonRef(ws.Id, ws.Code, ws.Name),
                lineId is null ? null : new MonLine(lineId, lines.GetValueOrDefault(lineId, lineId)),
                d.Status, h.Health, h.Label, d.LastHeartbeatAt, d.LastSeenAt, d.FirmwareVersion, d.CreatedAt,
                c?.KeyHash is not null, c?.KeyPrefix, c?.KeyIssuedAt, topics,
                telemetry.GetValueOrDefault(d.Id) + counts.GetValueOrDefault(d.Id), c?.Version ?? 0);
        }).ToList();
    }

    // ─── Shift calendar ────────────────────────────────────────────────────

    public static async Task<ShiftCalendar> CalendarAsync(BunonBrainDbContext db, Guid factoryId, CancellationToken ct)
    {
        var tzId = await db.AttendancePolicies.AsNoTracking().Where(p => p.FactoryId == factoryId).Select(p => p.TimeZoneId).FirstOrDefaultAsync(ct);
        var tz = AttendanceRules.ResolveTimeZone(tzId ?? "Asia/Dhaka") ?? AttendanceRules.ResolveTimeZone("Asia/Dhaka") ?? TimeZoneInfo.Utc;
        var shifts = await db.Shifts.AsNoTracking().Include(s => s.Breaks).Where(s => s.FactoryId == factoryId && s.IsActive).ToListAsync(ct);
        return new ShiftCalendar(shifts.Select(s => new ShiftWindow(s.Id, s.Code, s.Name, s.StartLocal, s.EndLocal, s.WorkingDays,
            s.Breaks.Select(b => (b.StartLocal, b.DurationMinutes)).ToList())).ToList(), tz);
    }
}
