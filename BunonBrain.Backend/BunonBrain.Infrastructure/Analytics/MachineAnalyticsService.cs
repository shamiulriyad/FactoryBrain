using BunonBrain.Application.Access;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Iot;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// Machine performance from the derived machine state history (the IoT
/// module's transitions). Uptime = running ÷ confirmed-state time; OFFLINE
/// and UNKNOWN time is a data gap, never downtime; maintenance is excluded.
/// Machines without sensors are listed as not monitored and kept out of
/// every total.
/// </summary>
public sealed class MachineAnalyticsService : IMachineAnalytics
{
    public const int MaxMachines = 1000;

    private readonly BunonBrainDbContext _db;

    public MachineAnalyticsService(BunonBrainDbContext db) { _db = db; }

    public async Task<AccessResult<MachineAnalyticsDto>> MachinesAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct)
    {
        var c = await AnalyticsContext.CreateAsync(_db, caller, filter, ct);
        if (c.Outcome != AccessOutcome.Ok) return new AccessResult<MachineAnalyticsDto>(c.Outcome, null, c.Code, c.Message);
        if (!c.Value!.Can(AnalyticsSections.Machines))
            return AccessResult<MachineAnalyticsDto>.Forbidden("machine_access_required", "Machine analytics need monitoring.view, devices.view or production.view.");
        return AccessResult<MachineAnalyticsDto>.Ok(await BuildAsync(c.Value, ct));
    }

    public sealed record MachineTime(Guid MachineId, MachineAvailability Total, int Errors, IReadOnlyDictionary<DateOnly, (MachineAvailability Time, int Errors)> Days);

    /// <summary>Per-machine time by state over the period and per day (shared with the overview and insights).</summary>
    internal async Task<(List<BunonBrain.Domain.Entities.Machine> Machines, HashSet<Guid> Monitored, Dictionary<Guid, MachineTime> Times)> TimesAsync(AnalyticsContext ctx,
        bool perDay, CancellationToken ct)
    {
        var machines = await ctx.Machines().OrderBy(m => m.Code).Take(MaxMachines).ToListAsync(ct);
        var ids = machines.Select(m => m.Id).ToList();
        var bound = await _db.Devices.AsNoTracking().Where(d => d.MachineId != null && ids.Contains(d.MachineId.Value) && d.Status != "Disabled" && d.IsSimulated == ctx.Simulated)
            .Select(d => d.MachineId!.Value).Distinct().ToListAsync(ct);
        var monitored = bound.ToHashSet();
        var (from, to) = (ctx.Period.FromUtc, ctx.Period.ToUtc);
        var inRange = await _db.MachineStateTransitions.AsNoTracking()
            .Where(t => ids.Contains(t.MachineId) && t.SourceType == ctx.Source && t.OccurredAt > from && t.OccurredAt < to)
            .Select(t => new { t.MachineId, t.OccurredAt, t.ToState }).ToListAsync(ct);
        var before = await _db.MachineStateTransitions.AsNoTracking()
            .Where(t => ids.Contains(t.MachineId) && t.SourceType == ctx.Source && t.OccurredAt <= from)
            .GroupBy(t => t.MachineId)
            .Select(g => g.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).Select(x => new { x.MachineId, x.ToState }).First())
            .ToListAsync(ct);
        foreach (var m in inRange.Select(x => x.MachineId).Concat(before.Select(x => x.MachineId))) monitored.Add(m);

        var times = new Dictionary<Guid, MachineTime>();
        foreach (var id in monitored)
        {
            var list = inRange.Where(t => t.MachineId == id).OrderBy(t => t.OccurredAt).Select(t => (t.OccurredAt, t.ToState)).ToList();
            var start = before.FirstOrDefault(b => b.MachineId == id)?.ToState;
            var total = MachineAvailability.From(IotMetrics.Durations(start, list, from, to));
            var errors = list.Count(t => t.ToState == MachineStates.Error);
            var days = new Dictionary<DateOnly, (MachineAvailability, int)>();
            if (perDay)
                foreach (var d in ctx.Period.Dates())
                {
                    var (a, b) = ctx.Period.Day(d, ctx.Zone, ctx.Now);
                    if (b <= a) continue;
                    var state = list.LastOrDefault(t => t.OccurredAt <= a) is { ToState: { } s } ? s : start;
                    days[d] = (MachineAvailability.From(IotMetrics.Durations(state, list, a, b)), list.Count(t => t.ToState == MachineStates.Error && t.OccurredAt > a && t.OccurredAt <= b));
                }
            times[id] = new MachineTime(id, total, errors, days);
        }
        return (machines, monitored, times);
    }

    internal async Task<MachineAnalyticsDto> BuildAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var (machines, monitored, times) = await TimesAsync(ctx, true, ct);
        var wsIds = machines.Where(m => m.WorkstationId != null).Select(m => m.WorkstationId!.Value).Distinct().ToList();
        var workstations = await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var lineNames = await ctx.LineNamesAsync(workstations.Values.Select(w => w.LineId), ct);
        var pieces = ctx.Can(AnalyticsSections.Production)
            ? await ctx.Counts().Where(e => e.MachineId != null && e.Pieces > 0).GroupBy(e => e.MachineId!.Value)
                .Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToDictionaryAsync(x => x.Key, x => x.N, ct)
            : new Dictionary<Guid, long>();

        var rows = machines.Select(m =>
        {
            var ws = m.WorkstationId is { } w ? workstations.GetValueOrDefault(w) : null;
            var line = ws is null ? null : new MonLine(ws.LineId, lineNames.GetValueOrDefault(ws.LineId, ws.LineId));
            if (!times.TryGetValue(m.Id, out var t))
                return new MachinePerformanceRow(m.Id, m.Code, line, ws is null ? null : new MonRef(ws.Id, ws.Code, ws.Name), 0, 0, 0, 0, 0, 0, 0, null, null, null, 0,
                    pieces.GetValueOrDefault(m.Id), false);
            var a = t.Total;
            return new MachinePerformanceRow(m.Id, m.Code, line, ws is null ? null : new MonRef(ws.Id, ws.Code, ws.Name), AnalyticsMath.Hours(a.RunningSeconds),
                AnalyticsMath.Hours(a.IdleSeconds), AnalyticsMath.Hours(a.StoppedSeconds), AnalyticsMath.Hours(a.ErrorSeconds), AnalyticsMath.Hours(a.MaintenanceSeconds),
                AnalyticsMath.Hours(a.OfflineSeconds), AnalyticsMath.Hours(a.UnknownSeconds), a.UptimePct, a.DowntimePct, a.CoveragePct, t.Errors,
                pieces.GetValueOrDefault(m.Id), monitored.Contains(m.Id));
        }).ToList();

        var lines = rows.Where(r => r.Line is not null).GroupBy(r => r.Line!.Id).Select(g =>
        {
            var sum = MachineAvailability.Sum(g.Where(r => times.ContainsKey(r.MachineId)).Select(r => times[r.MachineId].Total));
            return new LineAvailabilityRow(g.Key, g.First().Line!.Name, g.Count(), g.Count(r => r.Monitored), AnalyticsMath.Hours(sum.RunningSeconds),
                AnalyticsMath.Hours(sum.DowntimeSeconds), AnalyticsMath.Hours(sum.GapSeconds), sum.UptimePct, sum.DowntimePct, sum.CoveragePct,
                g.Sum(r => r.Errors));
        }).OrderBy(l => l.LineName).ToList();

        var daily = ctx.Period.Dates().Select(d =>
        {
            var items = times.Values.Where(t => t.Days.ContainsKey(d)).Select(t => t.Days[d]).ToList();
            var sum = MachineAvailability.Sum(items.Select(i => i.Time));
            return new MachineDayPoint(d, AnalyticsMath.Hours(sum.RunningSeconds), AnalyticsMath.Hours(sum.DowntimeSeconds), AnalyticsMath.Hours(sum.IdleSeconds),
                AnalyticsMath.Hours(sum.GapSeconds), sum.UptimePct, items.Sum(i => i.Errors));
        }).ToList();

        var total = MachineAvailability.Sum(times.Values.Select(t => t.Total));
        var nowStates = await _db.MachineStates.AsNoTracking().Where(s => monitored.Contains(s.MachineId)).GroupBy(s => s.State)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var kpis = new List<Kpi>
        {
            Kpi.Count("machine.states_now", monitored.Count, $"{nowStates.GetValueOrDefault(MachineStates.Running)} running · {nowStates.GetValueOrDefault(MachineStates.Idle)} idle · " +
                $"{nowStates.GetValueOrDefault(MachineStates.Stopped)} stopped · {nowStates.GetValueOrDefault(MachineStates.Error)} error · " +
                $"{nowStates.GetValueOrDefault(MachineStates.Offline)} offline · {nowStates.GetValueOrDefault(MachineStates.Unknown)} unknown · " +
                $"{machines.Count - monitored.Count} without sensor", monitored.Count == 0 ? Availability.NoData : Availability.Ok),
            Kpi.Count("machine.running", AnalyticsMath.Hours(total.RunningSeconds)),
            Kpi.Ratio("machine.uptime", total.RunningSeconds, total.KnownSeconds),
            Kpi.Count("machine.downtime", AnalyticsMath.Hours(total.DowntimeSeconds),
                total.KnownSeconds > 0 ? $"{AnalyticsMath.Pct(total.DowntimeSeconds, total.KnownSeconds)} % of confirmed-state time" : null),
            Kpi.Count("machine.data_gap", AnalyticsMath.Hours(total.GapSeconds), total.PeriodSeconds - total.MaintenanceSeconds > 0
                ? $"coverage {AnalyticsMath.Pct(total.KnownSeconds, total.PeriodSeconds - total.MaintenanceSeconds)} %" : null),
            Kpi.Count("machine.errors", times.Values.Sum(t => t.Errors)),
        };
        var warnings = ctx.CommonWarnings().ToList();
        if (monitored.Count == 0) warnings.Add("No machine in this scope has sensor data: machine figures are unavailable, not zero.");
        if (total.GapSeconds > 0) warnings.Add($"{AnalyticsMath.Hours(total.GapSeconds)} h of machine time had no reliable data (offline or unknown); it is not counted as downtime.");
        if (ctx.Filter.ShiftId is not null) warnings.Add("Machine time is not split by shift: the shift filter doesn't apply to machine figures.");
        if (machines.Count >= MaxMachines) warnings.Add($"Only the first {MaxMachines} machines are included.");
        warnings.Add("A RUNNING machine doesn't prove anyone is operating it; machine time is never worker working time.");
        return new MachineAnalyticsDto(ctx.Dto(), kpis, rows, lines, daily, warnings);
    }
}
