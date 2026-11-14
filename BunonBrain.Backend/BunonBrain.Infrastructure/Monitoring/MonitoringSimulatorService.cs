using System.Collections.Concurrent;
using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Cameras;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Monitoring;

/// <summary>The generator state of running simulations (tracks, offline cameras), kept in memory between ticks.</summary>
public sealed class MonitoringSimulatorState
{
    public ConcurrentDictionary<Guid, SimState> Runs { get; } = new();
}

/// <summary>
/// The monitoring simulator (see <see cref="IMonitoringSimulatorService"/>).
/// Vision and camera-status events go through <see cref="IMonitoringEventService"/>
/// as SIMULATED; simulated devices are driven through the device simulator,
/// whose pipeline reports them like real hardware would, labelled SIMULATED.
/// A run ends by itself after its duration (at most 2 hours).
/// </summary>
public sealed class MonitoringSimulatorService : IMonitoringSimulatorService
{
    public const int DefaultEventsPerMinute = 12;
    public const int DefaultDurationMinutes = 15;
    private const int MaxCatchUpPerTick = 5;

    private readonly BunonBrainDbContext _db;
    private readonly IMonitoringEventService _events;
    private readonly IDeviceIngestionService _devices;
    private readonly MonitoringSimulatorState _state;
    private readonly ILogger<MonitoringSimulatorService> _log;

    public MonitoringSimulatorService(BunonBrainDbContext db, IMonitoringEventService events, IDeviceIngestionService devices,
        MonitoringSimulatorState state, ILogger<MonitoringSimulatorService> log)
    {
        _db = db; _events = events; _devices = devices; _state = state; _log = log;
    }

    private static bool CanRun(AccessContext caller, Guid factoryId) => caller.Scope >= RoleScope.Factory && caller.CanAccessFactory(factoryId);

    /// <summary>
    /// The identity a run acts with: the person who started it, limited to the
    /// run's factory and holding no permissions of its own.
    /// </summary>
    public static AccessContext RunIdentity(MonitoringSimulationRun run) =>
        new(run.StartedByUserId, run.StartedByEmail, new[] { SystemRoles.FactoryAdmin }, Array.Empty<string>(), new[] { run.FactoryId }, Array.Empty<string>());

    // ─── Status, start, stop ───────────────────────────────────────────────

    public async Task<AccessResult<SimulatorStatusDto>> StatusAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        if (factoryId is { } f && !CanRun(caller, f))
            return AccessResult<SimulatorStatusDto>.Forbidden("factory_out_of_scope", "You can only simulate in your own factory.");
        var runs = _db.MonitoringSimulationRuns.AsNoTracking();
        if (factoryId is { } fid) runs = runs.Where(r => r.FactoryId == fid);
        else if (!caller.IsPlatform) { var mine = caller.FactoryIds.ToList(); runs = runs.Where(r => mine.Contains(r.FactoryId)); }
        var recent = await runs.OrderByDescending(r => r.StartedAt).Take(10).ToListAsync(ct);
        var dtos = await DtosAsync(recent, ct);
        return AccessResult<SimulatorStatusDto>.Ok(new SimulatorStatusDto(dtos.FirstOrDefault(r => r.Status == SimulationStatuses.Running), dtos,
            MonitoringSimulation.Generatable, MonitoringSimulation.MaxEventsPerMinute, MonitoringSimulation.MaxDurationMinutes, MonitoringSimulation.MaxCameras,
            "Everything the simulator produces is labelled SIMULATED and goes through the same ingestion pipeline real producers will use. It never names a worker and is not a camera AI result."));
    }

    public async Task<AccessResult<SimulationRunDto>> StartAsync(AccessContext caller, StartSimulationRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanRun(caller, r.FactoryId)) return AccessResult<SimulationRunDto>.Forbidden("factory_out_of_scope", "You can only simulate in your own factory.");
        var factory = await _db.Factories.AsNoTracking().FirstOrDefaultAsync(f => f.Id == r.FactoryId, ct);
        if (factory is null) return AccessResult<SimulationRunDto>.NotFound("factory_not_found", "Factory not found.");
        if (!factory.IsActive) return AccessResult<SimulationRunDto>.Conflict("factory_inactive", "The factory is deactivated.");
        var rate = r.EventsPerMinute ?? DefaultEventsPerMinute;
        if (rate is < MonitoringSimulation.MinEventsPerMinute or > MonitoringSimulation.MaxEventsPerMinute)
            return AccessResult<SimulationRunDto>.Invalid("rate_invalid", $"Events per minute: {MonitoringSimulation.MinEventsPerMinute}–{MonitoringSimulation.MaxEventsPerMinute}.");
        var duration = r.DurationMinutes ?? DefaultDurationMinutes;
        if (duration is < MonitoringSimulation.MinDurationMinutes or > MonitoringSimulation.MaxDurationMinutes)
            return AccessResult<SimulationRunDto>.Invalid("duration_invalid", $"Duration: {MonitoringSimulation.MinDurationMinutes}–{MonitoringSimulation.MaxDurationMinutes} minutes.");
        var types = (r.EventTypes is { Count: > 0 } ? r.EventTypes : MonitoringSimulation.Generatable.Where(t => t != MonitoringEventTypes.ModelStatusChanged))
            .Select(t => MonitoringEventTypes.Parse(t)).ToList();
        if (types.Any(t => t is null || !MonitoringSimulation.Generatable.Contains(t)))
            return AccessResult<SimulationRunDto>.Invalid("event_types_invalid", $"Event types: any of {string.Join(", ", MonitoringSimulation.Generatable)}.");

        List<Guid> cameraIds;
        var active = _db.Cameras.AsNoTracking().Where(c => c.FactoryId == r.FactoryId && c.Status == CameraStatuses.Active);
        if (r.CameraIds is { Count: > 0 })
        {
            cameraIds = r.CameraIds.Distinct().ToList();
            if (cameraIds.Count > MonitoringSimulation.MaxCameras)
                return AccessResult<SimulationRunDto>.Invalid("too_many_cameras", $"At most {MonitoringSimulation.MaxCameras} cameras per run.");
            var found = await active.Where(c => cameraIds.Contains(c.Id)).CountAsync(ct);
            if (found != cameraIds.Count) return AccessResult<SimulationRunDto>.Invalid("camera_not_in_factory", "Choose active cameras of this factory.");
        }
        else cameraIds = await active.OrderBy(c => c.Code).Take(MonitoringSimulation.MaxCameras).Select(c => c.Id).ToListAsync(ct);
        var needsCamera = types.Any(t => t != MonitoringEventTypes.DeviceStatusChanged && t != MonitoringEventTypes.ModelStatusChanged);
        if (needsCamera && cameraIds.Count == 0)
            return AccessResult<SimulationRunDto>.Conflict("no_cameras", "This factory has no active cameras to simulate. Register a camera first, or simulate device status only.");

        var now = DateTimeOffset.UtcNow;
        var run = new MonitoringSimulationRun
        {
            FactoryId = r.FactoryId, Status = SimulationStatuses.Running, CameraIds = JsonSerializer.Serialize(cameraIds), EventTypes = JsonSerializer.Serialize(types),
            EventsPerMinute = rate, DurationMinutes = duration, Seed = Random.Shared.Next(1, int.MaxValue / 2), StartedAt = now,
            StartedByUserId = caller.UserId, StartedByEmail = caller.Email, EndsAt = now.AddMinutes(duration), NextEventAt = now,
        };
        _db.MonitoringSimulationRuns.Add(run);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "monitoring_simulation.started", AccessAudit.EntityMonitoringSimulation, run.Id.ToString(), null,
            SimulationStatuses.Running, $"monitoring simulation: {cameraIds.Count} camera(s), {rate}/min for {duration} min, types {string.Join(",", types)}",
            traceId, r.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505", ConstraintName: MonitoringSimulationRunConfiguration.OneRunningIndex })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<SimulationRunDto>.Conflict("simulation_already_running", "A simulation is already running in this factory. Stop it first.");
        }
        _db.ChangeTracker.Clear();
        return AccessResult<SimulationRunDto>.Created((await DtosAsync(new[] { run }, ct))[0]);
    }

    public async Task<AccessResult<SimulationRunDto>> StopAsync(AccessContext caller, Guid runId, StopSimulationRequest r, string? traceId, CancellationToken ct)
    {
        var run = await _db.MonitoringSimulationRuns.FirstOrDefaultAsync(x => x.Id == runId, ct);
        if (run is null || !CanRun(caller, run.FactoryId)) return AccessResult<SimulationRunDto>.NotFound("simulation_not_found", "Simulation not found.");
        if (run.Status != SimulationStatuses.Running) return AccessResult<SimulationRunDto>.Conflict("simulation_not_running", "The simulation has already ended.");
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? null : r.Reason.Trim();
        if (reason is { Length: > 500 }) return AccessResult<SimulationRunDto>.Invalid("reason_too_long", "At most 500 characters.");
        run.Status = SimulationStatuses.Stopped;
        run.StoppedAt = DateTimeOffset.UtcNow;
        run.StoppedByEmail = caller.Email;
        run.StopReason = reason ?? "Stopped by a person";
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "monitoring_simulation.stopped", AccessAudit.EntityMonitoringSimulation, run.Id.ToString(),
            SimulationStatuses.Running, SimulationStatuses.Stopped, $"monitoring simulation stopped after {run.EventCount} event(s)" + (reason is null ? "" : $": {reason}"),
            traceId, run.FactoryId));
        await _db.SaveChangesAsync(ct);
        _state.Runs.TryRemove(run.Id, out _);
        _db.ChangeTracker.Clear();
        return AccessResult<SimulationRunDto>.Ok((await DtosAsync(new[] { run }, ct))[0]);
    }

    private async Task<IReadOnlyList<SimulationRunDto>> DtosAsync(IReadOnlyList<MonitoringSimulationRun> runs, CancellationToken ct)
    {
        var camIds = runs.SelectMany(r => JsonSerializer.Deserialize<List<Guid>>(r.CameraIds) ?? new()).Distinct().ToList();
        var cams = await _db.Cameras.AsNoTracking().Where(c => camIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => new MonRef(c.Id, c.Code, c.Name), ct);
        var codes = await _db.Factories.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        return runs.Select(r => new SimulationRunDto(r.Id, r.FactoryId, codes.GetValueOrDefault(r.FactoryId, "?"), r.Status,
            (JsonSerializer.Deserialize<List<Guid>>(r.CameraIds) ?? new()).Where(cams.ContainsKey).Select(id => cams[id]).ToList(),
            JsonSerializer.Deserialize<List<string>>(r.EventTypes) ?? new(), r.EventsPerMinute, r.DurationMinutes, r.EventCount, r.StartedAt,
            r.StartedByEmail, r.EndsAt, r.StoppedAt, r.StoppedByEmail, r.StopReason)).ToList();
    }

    // ─── Generating ────────────────────────────────────────────────────────

    private async Task<(List<SimCamera> Cameras, List<SimDevice> Devices)> ContextAsync(Guid factoryId, IReadOnlyCollection<Guid> cameraIds, CancellationToken ct)
    {
        var cams = await _db.Cameras.AsNoTracking().Where(c => c.FactoryId == factoryId && cameraIds.Contains(c.Id) && c.Status == CameraStatuses.Active)
            .OrderBy(c => c.Code).ToListAsync(ct);
        var ids = cams.Select(c => c.Id).ToList();
        var zones = await _db.CameraZones.AsNoTracking().Where(z => z.IsActive && ids.Contains(z.CameraId)).ToListAsync(ct);
        var health = await _db.Devices.AsNoTracking()
            .Where(d => d.CameraId != null && ids.Contains(d.CameraId.Value) && d.IsSimulated && d.Status != DeviceStatuses.Disabled)
            .ToDictionaryAsync(d => d.CameraId!.Value, d => d.Id, ct);
        var devices = await _db.Devices.AsNoTracking()
            .Where(d => d.FactoryId == factoryId && d.IsSimulated && d.CameraId == null && d.Status != DeviceStatuses.Disabled)
            .OrderBy(d => d.Code).Take(10).Select(d => new SimDevice(d.Id, d.Code)).ToListAsync(ct);
        return (cams.Select(c => new SimCamera(c.Id, c.Code,
            zones.Where(z => z.CameraId == c.Id).OrderBy(z => z.Name).Select(z => new SimZone(z.Id, z.Name, z.ZoneType)).ToList(),
            health.TryGetValue(c.Id, out var dev) ? dev : null)).ToList(), devices);
    }

    /// <summary>Sends one generated event: a device through the device simulator, anything else through the monitoring pipeline.</summary>
    private async Task<AccessResult<SimulatorTriggerResult>> EmitAsync(AccessContext actor, Guid factoryId, SimEvent e, Guid? runId, CancellationToken ct)
    {
        if (e.DeviceId is { } deviceId && e.DeviceKind is { } kind)
        {
            var result = await _devices.SimulateAsync(actor, deviceId, kind, null, kind == DeviceMessageKinds.Offline ? "Simulated disconnect" : "Simulated reconnect", null, ct);
            if (result.Outcome != AccessOutcome.Ok) return new AccessResult<SimulatorTriggerResult>(result.Outcome, null, result.Code, result.Message);
            var latest = await _db.MonitoringEvents.AsNoTracking().Where(x => x.DeviceId == deviceId || (e.CameraId != null && x.CameraId == e.CameraId))
                .OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync(ct);
            var dto = latest is null ? null : (await MonitoringQueries.EventDtosAsync(_db, new[] { latest }, ct))[0];
            return AccessResult<SimulatorTriggerResult>.Ok(new SimulatorTriggerResult("DeviceSimulated", dto, null,
                result.Value!.Events.Count == 0 ? "The device was already in that state; nothing changed." : "Driven through the device simulator; its pipeline reported the change."));
        }
        var payload = JsonSerializer.SerializeToElement(e.Payload);
        var input = new MonitoringEventInput(e.EventType, factoryId, $"{(runId is null ? "manual" : $"run-{runId:N}")}:{Guid.NewGuid():N}", null,
            MonitoringSourceTypes.Simulated, CameraId: e.CameraId, ZoneId: e.ZoneId, TrackId: e.TrackId, Confidence: e.Confidence, Payload: payload,
            SimulationRunId: runId);
        var ingest = await _events.IngestAsync(actor, input, MonitoringProducers.Simulator, ct);
        if (ingest.Outcome != AccessOutcome.Ok) return new AccessResult<SimulatorTriggerResult>(ingest.Outcome, null, ingest.Code, ingest.Message);
        return AccessResult<SimulatorTriggerResult>.Ok(new SimulatorTriggerResult(ingest.Value!.Outcome, ingest.Value.Event, ingest.Value.Alert, null));
    }

    public async Task<AccessResult<SimulatorTriggerResult>> TriggerAsync(AccessContext caller, TriggerSimulatedEventRequest r, CancellationToken ct)
    {
        if (!CanRun(caller, r.FactoryId)) return AccessResult<SimulatorTriggerResult>.Forbidden("factory_out_of_scope", "You can only simulate in your own factory.");
        var type = MonitoringEventTypes.Parse(r.EventType);
        if (type is null || !MonitoringSimulation.Generatable.Contains(type))
            return AccessResult<SimulatorTriggerResult>.Invalid("event_type_invalid", $"Event type: one of {string.Join(", ", MonitoringSimulation.Generatable)}.");
        if (r.Confidence is { } c && (double.IsNaN(c) || c < 0 || c > 1))
            return AccessResult<SimulatorTriggerResult>.Invalid("confidence_invalid", "confidence is a number from 0 to 1.");
        var rng = Random.Shared;
        var state = new SimState();

        if (type == MonitoringEventTypes.DeviceStatusChanged)
        {
            var dev = r.DeviceId is { } did
                ? await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == did && d.FactoryId == r.FactoryId, ct)
                : await _db.Devices.AsNoTracking().Where(d => d.FactoryId == r.FactoryId && d.IsSimulated && d.Status != DeviceStatuses.Disabled)
                    .OrderBy(d => d.Code).FirstOrDefaultAsync(ct);
            if (dev is null) return AccessResult<SimulatorTriggerResult>.Invalid("device_required", "Choose a simulated device of this factory (register one in Cameras & devices).");
            if (!dev.IsSimulated) return AccessResult<SimulatorTriggerResult>.Conflict("device_not_simulated", $"{dev.Code} is a real device; the simulator only drives simulated devices.");
            var goOffline = r.ToStatus is null ? dev.ConnectionStatus == ConnectionStatuses.Online : string.Equals(r.ToStatus, "OFFLINE", StringComparison.OrdinalIgnoreCase);
            if (!goOffline) state.OfflineDevices.Add(dev.Id);
            return await EmitAsync(caller, r.FactoryId, MonitoringSimulation.DeviceStatus(new SimDevice(dev.Id, dev.Code), goOffline, state), null, ct);
        }
        if (type == MonitoringEventTypes.ModelStatusChanged)
        {
            var to = (r.ToStatus ?? "DEGRADED").Trim().ToUpperInvariant();
            if (!StatusValues.Model.Contains(to)) return AccessResult<SimulatorTriggerResult>.Invalid("to_status_invalid", $"Model status: one of {string.Join(", ", StatusValues.Model)}.");
            return await EmitAsync(caller, r.FactoryId, new SimEvent(type, null, null, null, null, new Dictionary<string, object?>
            {
                ["simulated"] = true, ["note"] = MonitoringRules.SimulatedNote, ["model"] = "sim-person-detector", ["toStatus"] = to,
            }), null, ct);
        }

        // Camera events.
        var camQuery = _db.Cameras.AsNoTracking().Where(x => x.FactoryId == r.FactoryId && x.Status == CameraStatuses.Active);
        var cam = r.CameraId is { } cid ? await camQuery.FirstOrDefaultAsync(x => x.Id == cid, ct) : await camQuery.OrderBy(x => x.Code).FirstOrDefaultAsync(ct);
        if (cam is null) return AccessResult<SimulatorTriggerResult>.Invalid("camera_required", "Choose an active camera of this factory.");
        var (cams, _) = await ContextAsync(r.FactoryId, new[] { cam.Id }, ct);
        var sim = cams[0];

        if (type == MonitoringEventTypes.CameraStatusChanged)
        {
            var goOffline = !string.Equals(r.ToStatus, "ONLINE", StringComparison.OrdinalIgnoreCase);
            return await EmitAsync(caller, r.FactoryId, MonitoringSimulation.CameraStatus(sim, goOffline, state), null, ct);
        }

        SimZone? zone = null;
        if (type is MonitoringEventTypes.ZoneEntered or MonitoringEventTypes.ZoneExited || r.ZoneId is not null)
        {
            zone = r.ZoneId is { } zid ? sim.Zones.FirstOrDefault(z => z.Id == zid) : sim.Zones.Count > 0 ? sim.Zones[rng.Next(sim.Zones.Count)] : null;
            if (zone is null)
                return AccessResult<SimulatorTriggerResult>.Invalid("zone_required", r.ZoneId is null ? $"{cam.Code} has no active zones. Configure one first." : "Choose an active zone of this camera.");
        }
        var track = string.IsNullOrWhiteSpace(r.TrackId) ? null : r.TrackId.Trim();
        if (track is not null && !MonitoringRules.IsTrackId(track)) return AccessResult<SimulatorTriggerResult>.Invalid("track_id_invalid", "Track ids: letters, digits, '.', '_', ':', '-'.");
        if (track is null && type != MonitoringEventTypes.UncertainDetection)
        {
            // Continue the camera's latest simulated track, or start a new one.
            track = type == MonitoringEventTypes.PersonDetected ? null : await _db.MonitoringEvents.AsNoTracking()
                .Where(x => x.CameraId == cam.Id && x.SourceType == MonitoringSourceTypes.Simulated && x.TrackId != null)
                .OrderByDescending(x => x.OccurredAt).Select(x => x.TrackId).FirstOrDefaultAsync(ct);
            track ??= $"sim-t{rng.Next(1000, 9999)}";
        }
        var payload = new Dictionary<string, object?> { ["simulated"] = true, ["note"] = MonitoringRules.SimulatedNote, ["trigger"] = "manual" };
        double? confidence = r.Confidence;
        switch (type)
        {
            case MonitoringEventTypes.UncertainDetection:
                confidence ??= Math.Round(0.2 + rng.NextDouble() * 0.29, 2);
                payload["reason"] = "partly occluded";
                break;
            case MonitoringEventTypes.ActivityClassified:
                var activity = string.IsNullOrWhiteSpace(r.Activity) ? MonitoringSimulation.SampleActivities[rng.Next(MonitoringSimulation.SampleActivities.Length)] : r.Activity.Trim().ToUpperInvariant();
                if (activity.Length > 40) return AccessResult<SimulatorTriggerResult>.Invalid("activity_too_long", "Activities are at most 40 characters.");
                payload["activity"] = activity;
                payload["sample"] = true;
                confidence ??= Math.Round(0.55 + rng.NextDouble() * 0.37, 2);
                break;
            case MonitoringEventTypes.PersonDetected:
            case MonitoringEventTypes.TrackUpdated:
                confidence ??= Math.Round(0.75 + rng.NextDouble() * 0.22, 2);
                break;
            case MonitoringEventTypes.ZoneEntered:
                payload["zoneType"] = zone!.ZoneType;
                break;
        }
        return await EmitAsync(caller, r.FactoryId, new SimEvent(type, cam.Id, zone?.Id, type == MonitoringEventTypes.UncertainDetection ? null : track, confidence, payload), null, ct);
    }

    public async Task<SimulatorTickResult> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var runs = await _db.MonitoringSimulationRuns.AsNoTracking().Where(r => r.Status == SimulationStatuses.Running).ToListAsync(ct);
        int emitted = 0, completed = 0;
        foreach (var run in runs)
        {
            if (now >= run.EndsAt || run.EventCount >= MonitoringSimulation.MaxEventsPerRun)
            {
                await _db.MonitoringSimulationRuns.Where(r => r.Id == run.Id && r.Status == SimulationStatuses.Running).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, SimulationStatuses.Completed).SetProperty(r => r.StoppedAt, now)
                    .SetProperty(r => r.StopReason, now >= run.EndsAt ? "Duration reached" : "Event limit reached"), ct);
                _state.Runs.TryRemove(run.Id, out _);
                completed++;
                continue;
            }
            if (run.NextEventAt > now) continue;
            var interval = TimeSpan.FromSeconds(60.0 / run.EventsPerMinute);
            var cameraIds = JsonSerializer.Deserialize<List<Guid>>(run.CameraIds) ?? new();
            var allowed = (JsonSerializer.Deserialize<List<string>>(run.EventTypes) ?? new()).ToHashSet();
            var (cams, devices) = await ContextAsync(run.FactoryId, cameraIds, ct);
            var state = _state.Runs.GetOrAdd(run.Id, _ => new SimState());
            var actor = RunIdentity(run);
            var next = run.NextEventAt;
            var count = run.EventCount;
            var made = 0;
            while (next <= now && made < MaxCatchUpPerTick)
            {
                var rng = new Random(unchecked(run.Seed + count));
                var e = MonitoringSimulation.Next(rng, cams, devices, allowed, state);
                if (e is null) break;
                try
                {
                    var result = await EmitAsync(actor, run.FactoryId, e, run.Id, ct);
                    if (result.Outcome != AccessOutcome.Ok)
                        _log.LogInformation("Simulated {Type} skipped: {Code} {Message}", e.EventType, result.Code, result.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Simulated {Type} failed.", e.EventType);
                }
                count++;
                made++;
                next += interval;
            }
            // A worker that fell behind (paused, restarted) skips ahead instead of flooding.
            if (next <= now) next = now + interval;
            emitted += made;
            await _db.MonitoringSimulationRuns.Where(r => r.Id == run.Id && r.Status == SimulationStatuses.Running).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.EventCount, count).SetProperty(r => r.NextEventAt, next), ct);
        }
        return new SimulatorTickResult(runs.Count, emitted, completed);
    }
}

/// <summary>Runs the monitoring simulator's due events every second. Not registered in the Testing host (tests call TickAsync).</summary>
public sealed class MonitoringSimulatorWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MonitoringSimulatorWorker> _log;

    public MonitoringSimulatorWorker(IServiceScopeFactory scopes, ILogger<MonitoringSimulatorWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IMonitoringSimulatorService>().TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Monitoring simulator tick failed.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
