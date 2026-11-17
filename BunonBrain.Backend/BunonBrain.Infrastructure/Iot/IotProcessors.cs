using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Iot;

/// <summary>
/// Keeps the derived machine state: stores raw telemetry, applies the state
/// engine under a row lock on the machine's state, records transitions and
/// conditions, then reports them to central monitoring (MACHINE_STATE_CHANGED /
/// MACHINE_CONDITION, which raise and recover the machine alerts) and pushes
/// the machine to the dashboard. Monitoring and pushes happen after the
/// commit and never undo it.
/// </summary>
public sealed class MachineStateProcessor
{
    private readonly BunonBrainDbContext _db;
    private readonly IMonitoringEventService _events;
    private readonly IIotPublisher _publisher;
    private readonly ILogger<MachineStateProcessor> _log;

    public MachineStateProcessor(BunonBrainDbContext db, IMonitoringEventService events, IIotPublisher publisher, ILogger<MachineStateProcessor> log)
    {
        _db = db; _events = events; _publisher = publisher; _log = log;
    }

    public sealed record TelemetryOutcome(long TelemetryId, string Outcome, string? State, bool Changed);

    private sealed record Pending(Machine Machine, List<MachineStateTransition> Transitions, List<ConditionChange> Conditions, string SourceType);

    // ─── Telemetry ─────────────────────────────────────────────────────────

    public async Task<TelemetryOutcome> ApplyTelemetryAsync(Device device, Machine? machine, IotMessage msg, DateTimeOffset occurredAt, string transport,
        string actor, CancellationToken ct)
    {
        var source = IotSourceTypes.Of(device.IsSimulated);
        var strategy = _db.Database.CreateExecutionStrategy();
        var (outcome, pending) = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var t = new MachineTelemetry
            {
                FactoryId = device.FactoryId, DeviceId = device.Id, MachineId = machine?.Id, WorkstationId = machine?.WorkstationId ?? device.WorkstationId,
                LineId = device.LineId, ReportedState = msg.State, PowerWatts = msg.PowerWatts, CurrentAmps = msg.CurrentAmps, TemperatureC = msg.TemperatureC,
                VibrationMmS = msg.VibrationMmS, ErrorCode = msg.ErrorCode, Metrics = msg.Metrics is null ? null : JsonSerializer.Serialize(msg.Metrics),
                MessageId = msg.MessageId, SourceType = source, Transport = transport, OccurredAt = occurredAt, ReceivedAt = DateTimeOffset.UtcNow,
            };
            if (t.WorkstationId is { } wsId)
                t.LineId = await _db.Workstations.AsNoTracking().Where(w => w.Id == wsId).Select(w => w.LineId).FirstOrDefaultAsync(ct) ?? t.LineId;
            if (machine is null)
            {
                t.Outcome = "NO_MACHINE";
                _db.MachineTelemetry.Add(t);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return (new TelemetryOutcome(t.Id, t.Outcome, null, false), (Pending?)null);
            }

            var devices = await _db.Devices.AsNoTracking().Where(d => d.MachineId == machine.Id).ToListAsync(ct);
            var machineSource = IotQueries.MachineSource(devices);
            var s = await LockAsync(machine, machineSource, ct);
            if (source != machineSource)
            {
                // Simulated data never moves the state of a machine that has real sensors.
                t.Outcome = "SOURCE_MISMATCH";
                _db.MachineTelemetry.Add(t);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return (new TelemetryOutcome(t.Id, t.Outcome, s.State, false), null);
            }

            var rules = IotQueries.Rules(await _db.MachineMonitoringProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.MachineId == machine.Id, ct), machine);
            var snapshot = Snapshot(s);
            var reading = new TelemetryReading(occurredAt, msg.State, msg.PowerWatts, msg.TemperatureC, msg.VibrationMmS, msg.ErrorCode);
            var (change, late) = MachineStateEngine.OnTelemetry(snapshot, rules, reading);
            var pending = new Pending(machine, new List<MachineStateTransition>(), new List<ConditionChange>(), source);
            if (late) t.Outcome = "LATE";
            else
            {
                if (s.InMaintenance) t.Outcome = "MAINTENANCE";
                if (msg.PowerWatts is not null || msg.CurrentAmps is not null || msg.TemperatureC is not null || msg.VibrationMmS is not null)
                {
                    s.PowerWatts = msg.PowerWatts ?? s.PowerWatts;
                    s.CurrentAmps = msg.CurrentAmps ?? s.CurrentAmps;
                    s.TemperatureC = msg.TemperatureC ?? s.TemperatureC;
                    s.VibrationMmS = msg.VibrationMmS ?? s.VibrationMmS;
                    s.ReadingsAt = occurredAt;
                }
                s.LastStateEventAt = occurredAt;
                if (s.LastTelemetryAt is null || occurredAt > s.LastTelemetryAt) s.LastTelemetryAt = occurredAt;
                if (msg.ErrorCode is not null) s.ErrorCode = msg.ErrorCode;
                else if (change is not null && change.ToState != MachineStates.Error) s.ErrorCode = null;
                var conditions = MachineStateEngine.ReadingConditions(snapshot, rules, reading);
                Conditions(s, conditions);
                pending.Conditions.AddRange(conditions);
                if (change is not null) pending.Transitions.Add(Transition(s, machine, t.LineId, device.Id, change, occurredAt, source, actor));
                s.UpdatedAt = DateTimeOffset.UtcNow;
                s.Version++;
            }
            _db.MachineTelemetry.Add(t);
            // machine_state_transitions is append-only (a trigger rejects UPDATE), and a transition
            // points at the telemetry that caused it: write the telemetry first, then insert each
            // transition once, already complete, in the same transaction.
            foreach (var tr in pending.Transitions) _db.Entry(tr).State = EntityState.Detached;
            await _db.SaveChangesAsync(ct);
            foreach (var tr in pending.Transitions)
            {
                tr.TelemetryId = t.Id;
                _db.MachineStateTransitions.Add(tr);
            }
            if (pending.Transitions.Count > 0) await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (new TelemetryOutcome(t.Id, t.Outcome, s.State, pending.Transitions.Count > 0), pending);
        });
        _db.ChangeTracker.Clear();
        if (pending is not null) await PublishAsync(pending, ct);
        return outcome;
    }

    // ─── Health, silence and timed rules ───────────────────────────────────

    /// <summary>Re-derive a machine's state from device health and run its timed rules (the machine monitor, and after status messages).</summary>
    public async Task<(string State, bool Changed, int Conditions)> RefreshAsync(Guid machineId, ShiftCalendar? calendar, string actor, CancellationToken ct)
    {
        var machine = await _db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null) return (MachineStates.Unknown, false, 0);
        calendar ??= await IotQueries.CalendarAsync(_db, machine.FactoryId, ct);
        var now = DateTimeOffset.UtcNow;
        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var devices = await _db.Devices.AsNoTracking().Where(d => d.MachineId == machine.Id).ToListAsync(ct);
            if (devices.Count == 0 && !await _db.MachineStates.AnyAsync(x => x.MachineId == machine.Id, ct))
                return (State: MachineStates.Unknown, Pending: (Pending?)null);
            var ids = devices.Select(d => d.Id).ToList();
            var configs = await _db.DeviceIotConfigurations.AsNoTracking().Where(c => ids.Contains(c.DeviceId)).ToDictionaryAsync(c => c.DeviceId, ct);
            var machineSource = IotQueries.MachineSource(devices);
            var simulated = machineSource == IotSourceTypes.Simulated;
            var relevant = devices.Where(d => d.IsSimulated == simulated).ToList();
            var stateDevices = relevant.Where(d => IotQueries.DecidesState(d, configs.GetValueOrDefault(d.Id))).ToList();
            var counters = relevant.Where(d => IotQueries.IsCounter(d, configs.GetValueOrDefault(d.Id))).ToList();
            bool Online(Device d) => IotQueries.Health(d, now).Connection == Application.Devices.ConnectionStatuses.Online;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var s = await LockAsync(machine, machineSource, ct);
            var rules = IotQueries.Rules(await _db.MachineMonitoringProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.MachineId == machine.Id, ct), machine);
            var pending = new Pending(machine, new List<MachineStateTransition>(), new List<ConditionChange>(), machineSource);
            var change = MachineStateEngine.OnHealth(Snapshot(s), rules, new HealthFacts(stateDevices.Count, stateDevices.Count(Online)), now);
            var lineId = machine.WorkstationId is { } w ? await _db.Workstations.AsNoTracking().Where(x => x.Id == w).Select(x => x.LineId).FirstOrDefaultAsync(ct) : null;
            if (change is not null) pending.Transitions.Add(Transition(s, machine, lineId, null, change, now, machineSource, actor));

            var slot = calendar.Locate(now);
            var since = now.AddMinutes(-5);
            var recent = await _db.MachineStateTransitions.CountAsync(x => x.MachineId == machine.Id && x.OccurredAt >= since, ct) + pending.Transitions.Count;
            var timed = MachineStateEngine.TimedConditions(Snapshot(s), rules,
                new TimedFacts(slot.ShiftId is not null && !slot.InBreak, counters.Count > 0, counters.Any(Online), s.LastCountAt, recent), now);
            Conditions(s, timed);
            pending.Conditions.AddRange(timed);
            if (pending.Transitions.Count > 0 || pending.Conditions.Count > 0)
            {
                s.UpdatedAt = now;
                s.Version++;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else await tx.RollbackAsync(ct);
            return (State: s.State, Pending: pending);
        });
        _db.ChangeTracker.Clear();
        if (result.Pending is { } p && (p.Transitions.Count > 0 || p.Conditions.Count > 0)) await PublishAsync(p, ct);
        return (result.State, result.Pending?.Transitions.Count > 0, result.Pending?.Conditions.Count ?? 0);
    }

    // ─── Maintenance ───────────────────────────────────────────────────────

    public async Task<bool> SetMaintenanceAsync(Machine machine, bool on, string? reason, string actor, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var pending = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var devices = await _db.Devices.AsNoTracking().Where(d => d.MachineId == machine.Id).ToListAsync(ct);
            var source = IotQueries.MachineSource(devices);
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var s = await LockAsync(machine, source, ct);
            if (s.InMaintenance == on) { await tx.RollbackAsync(ct); return (Pending?)null; }
            var now = DateTimeOffset.UtcNow;
            var lineId = machine.WorkstationId is { } w ? await _db.Workstations.AsNoTracking().Where(x => x.Id == w).Select(x => x.LineId).FirstOrDefaultAsync(ct) : null;
            var p = new Pending(machine, new List<MachineStateTransition>(), new List<ConditionChange>(), source);
            if (on)
            {
                s.InMaintenance = true;
                s.MaintenanceReason = reason;
                s.MaintenanceSince = now;
                s.MaintenanceByEmail = actor;
                p.Transitions.Add(Transition(s, machine, lineId, null,
                    new StateChange(MachineStates.Maintenance, $"Maintenance started by {actor}{(reason is null ? "" : $": {reason}")}", StateTriggers.MaintenanceStart),
                    now, source, actor));
                // Stoppage and idle rules don't apply while the machine is being serviced.
                var cleared = IotQueries.Conditions(s.ActiveConditions).Where(c => c is MachineConditions.UnexpectedStop or MachineConditions.LongIdle)
                    .Select(c => new ConditionChange(c, false, "Machine in maintenance")).ToList();
                Conditions(s, cleared);
                p.Conditions.AddRange(cleared);
            }
            else
            {
                s.InMaintenance = false;
                s.MaintenanceReason = null;
                s.MaintenanceSince = null;
                s.MaintenanceByEmail = null;
                p.Transitions.Add(Transition(s, machine, lineId, null,
                    new StateChange(MachineStates.Unknown, $"Maintenance ended by {actor}; waiting for telemetry", StateTriggers.MaintenanceEnd), now, source, actor));
            }
            s.UpdatedAt = now;
            s.Version++;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return p;
        });
        _db.ChangeTracker.Clear();
        if (pending is null) return false;
        await PublishAsync(pending, ct);
        return true;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Lock the machine's state row (creating it on first use) for the rest of the transaction.</summary>
    private async Task<MachineState> LockAsync(Machine m, string source, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO machine_states ("MachineId", "FactoryId", "State", "Since", "Reason", "Trigger", "SourceType", "InMaintenance", "ActiveConditions", "UpdatedAt", "Version")
            VALUES ({m.Id}, {m.FactoryId}, 'UNKNOWN', {now}, 'No telemetry received yet', 'TELEMETRY', {source}, false, '[]'::jsonb, {now}, 1)
            ON CONFLICT ("MachineId") DO NOTHING
            """, ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM machine_states WHERE \"MachineId\" = {m.Id} FOR UPDATE", ct);
        return await _db.MachineStates.FirstAsync(x => x.MachineId == m.Id, ct);
    }

    private static MachineSnapshot Snapshot(MachineState s) =>
        new(s.State, s.Since, s.LastStateEventAt, s.LastTelemetryAt, s.InMaintenance, IotQueries.Conditions(s.ActiveConditions));

    private MachineStateTransition Transition(MachineState s, Machine m, string? lineId, Guid? deviceId, StateChange c, DateTimeOffset at, string source, string actor)
    {
        // A transition never predates the state it ends (a device clock behind the server).
        // Version 1 is the placeholder row created on first use: there is no earlier state to end.
        if (s.Version > 1 && at < s.Since) at = s.Since;
        var tr = new MachineStateTransition
        {
            MachineId = m.Id, FactoryId = m.FactoryId, LineId = lineId, WorkstationId = m.WorkstationId, DeviceId = deviceId, FromState = s.State,
            ToState = c.ToState, Reason = Trim(c.Reason, 300), Trigger = c.Trigger, SourceType = source, OccurredAt = at, RecordedAt = DateTimeOffset.UtcNow,
            RecordedByEmail = actor,
        };
        _db.MachineStateTransitions.Add(tr);
        s.State = c.ToState;
        s.Since = at;
        s.Reason = Trim(c.Reason, 300);
        s.Trigger = c.Trigger;
        s.SourceType = source;
        if (deviceId is not null) s.SourceDeviceId = deviceId;
        return tr;
    }

    private static void Conditions(MachineState s, IEnumerable<ConditionChange> changes)
    {
        var set = IotQueries.Conditions(s.ActiveConditions).ToHashSet();
        foreach (var c in changes)
        {
            if (c.Active) set.Add(c.Condition);
            else set.Remove(c.Condition);
        }
        s.ActiveConditions = JsonSerializer.Serialize(set.OrderBy(x => x).ToArray());
    }

    private static string Trim(string v, int max) => v.Length > max ? v[..max] : v;

    private async Task PublishAsync(Pending p, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var tr in p.Transitions)
        {
            var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["toStatus"] = tr.ToState, ["fromStatus"] = tr.FromState, ["reason"] = tr.Reason, ["trigger"] = tr.Trigger, ["transitionId"] = tr.Id,
                ["machineCode"] = p.Machine.Code,
                ["note"] = p.SourceType == IotSourceTypes.Simulated ? "Simulated machine data from the IoT simulator, not from a real machine." : null,
            });
            await IngestAsync(new MonitoringEventInput(MonitoringEventTypes.MachineStateChanged, p.Machine.FactoryId, $"machine-transition:{tr.Id}",
                tr.OccurredAt > now ? now : tr.OccurredAt, p.SourceType, DeviceId: tr.DeviceId, Payload: payload, MachineId: p.Machine.Id), ct);
        }
        foreach (var c in p.Conditions)
        {
            var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["toStatus"] = c.Active ? "ACTIVE" : "CLEARED", ["condition"] = c.Condition, ["reason"] = c.Detail, ["value"] = c.Value, ["machineCode"] = p.Machine.Code,
            });
            await IngestAsync(new MonitoringEventInput(MonitoringEventTypes.MachineCondition, p.Machine.FactoryId,
                $"machine-condition:{p.Machine.Id:N}:{c.Condition}:{(c.Active ? "on" : "off")}:{now.UtcTicks}", now, p.SourceType, Payload: payload,
                MachineId: p.Machine.Id), ct);
        }
        try
        {
            var m = await _db.Machines.AsNoTracking().FirstAsync(x => x.Id == p.Machine.Id, ct);
            var dto = (await IotQueries.MachineDtosAsync(_db, new[] { m }, ct))[0];
            await _publisher.MachineAsync(dto, p.Transitions.Count > 0 ? "state" : "condition", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Pushing machine {Machine} to the dashboard failed.", p.Machine.Code);
        }
    }

    private async Task IngestAsync(MonitoringEventInput input, CancellationToken ct)
    {
        try
        {
            var r = await _events.IngestAsync(null, input, MonitoringProducers.IotPipeline, ct);
            if (r.Outcome != AccessOutcome.Ok)
                _log.LogWarning("Machine event {Type} not recorded in monitoring: {Code} {Message}", input.EventType, r.Code, r.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Recording machine event {Type} in monitoring failed.", input.EventType);
        }
    }
}

/// <summary>
/// Production counting: each counter message is stored once (idempotency key
/// = device + message id, whatever the transport), turned into pieces by the
/// machine's counting rule under a lock on the counter's snapshot, and
/// attributed to the shift and production date it happened in.
/// </summary>
public sealed class ProductionCounterProcessor
{
    private readonly BunonBrainDbContext _db;
    private readonly IIotPublisher _publisher;
    private readonly ILogger<ProductionCounterProcessor> _log;

    public ProductionCounterProcessor(BunonBrainDbContext db, IIotPublisher publisher, ILogger<ProductionCounterProcessor> log)
    {
        _db = db; _publisher = publisher; _log = log;
    }

    public sealed record CountOutcome(string Outcome, ProductionCounterEvent? Event, string? Code, string? Error);

    public static string Key(Guid deviceId, string messageId) => $"{deviceId:N}/{messageId}";

    public async Task<CountOutcome> ApplyAsync(Device device, Machine? machine, string messageId, IotCounterReading reading, DateTimeOffset occurredAt,
        string transport, string actor, CancellationToken ct)
    {
        var wsId = machine?.WorkstationId ?? device.WorkstationId;
        var ws = wsId is { } w ? await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == w, ct) : null;
        var lineId = ws?.LineId ?? device.LineId;
        if (lineId is null)
            return new CountOutcome("Rejected", null, "counter_not_placed",
                $"{device.Code} is not placed on a production line, machine or workstation, so its counts can't be attributed. Set its placement first.");
        var profile = machine is null ? null : await _db.MachineMonitoringProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.MachineId == machine.Id, ct);
        var rules = machine is null ? MachineRules.Defaults(MachineKinds.Other) : IotQueries.Rules(profile, machine);
        var rule = new CountingRule(rules.CountingMode, rules.PulsesPerPiece);
        var calendar = await IotQueries.CalendarAsync(_db, device.FactoryId, ct);
        var slot = calendar.Locate(occurredAt);
        var key = Key(device.Id, messageId);
        var source = transport == IotTransports.Manual && !device.IsSimulated ? IotSourceTypes.Real : IotSourceTypes.Of(device.IsSimulated);

        var existing = await _db.ProductionCounterEvents.AsNoTracking().FirstOrDefaultAsync(e => e.FactoryId == device.FactoryId && e.IdempotencyKey == key, ct);
        if (existing is not null) return new CountOutcome("Duplicate", existing, null, null);
        try
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            var ev = await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var now = DateTimeOffset.UtcNow;
                await _db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO production_counter_snapshots ("DeviceId", "FactoryId", "MachineId", "PulseRemainder", "TotalPulses", "TotalPieces", "UpdatedAt", "Version")
                    VALUES ({device.Id}, {device.FactoryId}, {machine?.Id}, 0, 0, 0, {now}, 1)
                    ON CONFLICT ("DeviceId") DO NOTHING
                    """, ct);
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM production_counter_snapshots WHERE \"DeviceId\" = {device.Id} FOR UPDATE", ct);
                var snap = await _db.ProductionCounterSnapshots.FirstAsync(x => x.DeviceId == device.Id, ct);
                var d = ProductionCounting.Apply(new CounterState(snap.LastValue, snap.LastOccurredAt, snap.PulseRemainder), rule, reading, occurredAt);
                var e = new ProductionCounterEvent
                {
                    FactoryId = device.FactoryId, DeviceId = device.Id, MachineId = machine?.Id, WorkstationId = ws?.Id, LineId = lineId,
                    DepartmentId = ws?.DepartmentId, FloorId = ws?.FloorId, CounterMode = reading.Mode, RawValue = reading.Value, Pulses = d.Pulses,
                    Pieces = d.Pieces, Status = d.Status, Note = d.Note is { Length: > 300 } n ? n[..300] : d.Note, CountingMode = rule.Mode,
                    PulsesPerPiece = rule.Divisor, ShiftId = slot.ShiftId, ShiftCode = slot.ShiftCode, ProductionDate = slot.ProductionDate, SourceType = source,
                    Transport = transport, IdempotencyKey = key, MessageId = messageId, OccurredAt = occurredAt, ReceivedAt = now, RecordedByEmail = actor,
                };
                _db.ProductionCounterEvents.Add(e);
                snap.MachineId = machine?.Id ?? snap.MachineId;
                snap.LastValue = d.NewLastValue;
                snap.LastOccurredAt = d.NewLastOccurredAt;
                snap.PulseRemainder = d.NewRemainder;
                snap.TotalPulses += d.Pulses;
                snap.TotalPieces += d.Pieces;
                if (d.ResetDetected) snap.LastResetAt = occurredAt;
                if (d.Pulses > 0 && (snap.LastCountAt is null || occurredAt > snap.LastCountAt)) snap.LastCountAt = occurredAt;
                snap.UpdatedAt = now;
                snap.Version++;
                await _db.SaveChangesAsync(ct);
                if (machine is not null && d.Pulses > 0)
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE machine_states SET \"LastCountAt\" = GREATEST(COALESCE(\"LastCountAt\", {occurredAt}), {occurredAt}) WHERE \"MachineId\" = {machine.Id}", ct);
                await tx.CommitAsync(ct);
                return e;
            });
            _db.ChangeTracker.Clear();
            try { await _publisher.ProductionAsync(device.FactoryId, lineId, machine?.Id, source, occurredAt, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Production update push failed."); }
            return new CountOutcome("Accepted", ev, null, null);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
                                           && pg.ConstraintName == ProductionCounterEventConfiguration.IdempotencyIndex)
        {
            _db.ChangeTracker.Clear();
            var winner = await _db.ProductionCounterEvents.AsNoTracking().FirstAsync(e => e.FactoryId == device.FactoryId && e.IdempotencyKey == key, ct);
            return new CountOutcome("Duplicate", winner, null, null);
        }
    }
}
