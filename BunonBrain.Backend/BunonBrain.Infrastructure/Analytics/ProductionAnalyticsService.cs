using System.Globalization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Production;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// Production analytics from the IoT production counter events (pieces under
/// each machine's counting rule, stored once per device + message id) and
/// audited corrections; planned vs actual only where active plans exist.
/// All groupings run in the database.
/// </summary>
public sealed class ProductionAnalyticsService : IProductionAnalytics
{
    public const int MaxHourlyDays = 3;

    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public ProductionAnalyticsService(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    public async Task<AccessResult<ProductionAnalyticsDto>> ProductionAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct)
    {
        var c = await AnalyticsContext.CreateAsync(_db, caller, filter, ct);
        if (c.Outcome != AccessOutcome.Ok) return new AccessResult<ProductionAnalyticsDto>(c.Outcome, null, c.Code, c.Message);
        var ctx = c.Value!;
        if (!ctx.Can(AnalyticsSections.Production))
            return AccessResult<ProductionAnalyticsDto>.Forbidden("production_access_required", "Production analytics need production.view or monitoring.view.");
        return AccessResult<ProductionAnalyticsDto>.Ok(await BuildAsync(ctx, ct));
    }

    internal async Task<ProductionAnalyticsDto> BuildAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var all = ctx.Counts();
        var counted = all.Where(e => e.Pieces > 0);
        var byFactory = await counted.GroupBy(e => e.FactoryId).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byDept = await counted.Where(e => e.DepartmentId != null).GroupBy(e => e.DepartmentId!.Value).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byFloor = await counted.Where(e => e.FloorId != null).GroupBy(e => e.FloorId!.Value).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byLine = await counted.GroupBy(e => e.LineId).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byWs = await counted.Where(e => e.WorkstationId != null).GroupBy(e => e.WorkstationId!.Value).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byMachine = await counted.Where(e => e.MachineId != null).GroupBy(e => e.MachineId!.Value).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byShift = await counted.GroupBy(e => e.ShiftCode).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byDay = await counted.GroupBy(e => e.ProductionDate).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var statuses = await all.GroupBy(e => e.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var corrections = await ctx.Corrections().ToListAsync(ct);

        var countedPieces = byLine.Sum(x => x.N);
        var correctionPieces = corrections.Sum(x => (long)x.PiecesDelta);
        var total = countedPieces + correctionPieces;
        var hours = (ctx.Period.ToUtc - ctx.Period.FromUtc).TotalHours;

        static Dictionary<TK, long> Merge<TK>(IEnumerable<(TK Key, long N)> a, IEnumerable<(TK Key, long N)> b) where TK : notnull
        {
            var d = new Dictionary<TK, long>();
            foreach (var (k, n) in a.Concat(b)) d[k] = d.GetValueOrDefault(k) + n;
            return d;
        }
        var lines = Merge(byLine.Select(x => (x.Key, x.N)), corrections.Select(x => (x.LineId, (long)x.PiecesDelta)));
        var wss = Merge(byWs.Select(x => (x.Key, x.N)), corrections.Where(x => x.WorkstationId != null).Select(x => (x.WorkstationId!.Value, (long)x.PiecesDelta)));
        var machines = Merge(byMachine.Select(x => (x.Key, x.N)), corrections.Where(x => x.MachineId != null).Select(x => (x.MachineId!.Value, (long)x.PiecesDelta)));
        var shifts = Merge(byShift.Select(x => (x.Key ?? "", x.N)), corrections.Select(x => (x.ShiftCode ?? "", (long)x.PiecesDelta)));
        var days = Merge(byDay.Select(x => (x.Key, x.N)), corrections.Select(x => (x.ProductionDate, (long)x.PiecesDelta)));
        var factories = Merge(byFactory.Select(x => (x.Key, x.N)), corrections.Select(x => (x.FactoryId, (long)x.PiecesDelta)));

        // Names.
        var lineNames = await ctx.LineNamesAsync(lines.Keys, ct);
        var deptIds = byDept.Select(x => x.Key).ToList();
        var deptNames = await _db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var floorIds = byFloor.Select(x => x.Key).ToList();
        var floorNames = await _db.ProductionFloors.AsNoTracking().Where(d => floorIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var wsIds = wss.Keys.ToList();
        var wsNames = await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var mIds = machines.Keys.ToList();
        var mNames = await _db.Machines.AsNoTracking().Where(m => mIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Code, ct);
        var fIds = factories.Keys.ToList();
        var fNames = await _db.Factories.AsNoTracking().Where(f => fIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var shiftCodes = shifts.Keys.Where(k => k != "").ToList();
        var shiftNames = await _db.Shifts.AsNoTracking().Where(s => shiftCodes.Contains(s.Code) && (ctx.Filter.FactoryId == null || s.FactoryId == ctx.Filter.FactoryId))
            .Select(s => new { s.Code, s.Name }).ToListAsync(ct);

        AnalyticsGroup G(string key, string label, long n) => new(key, label, n, AnalyticsMath.Pct(n, total), hours >= 1d / 60 ? Math.Round(n / hours, 1) : null);
        IReadOnlyList<AnalyticsGroup> Groups<TK>(Dictionary<TK, long> d, Func<TK, string> key, Func<TK, string> label) where TK : notnull =>
            d.OrderByDescending(x => x.Value).Select(x => G(key(x.Key), label(x.Key), x.Value)).ToList();

        // Planned vs actual: REAL data, whole lines (not one machine / workstation / factory shift), only where plans exist.
        var warnings = ctx.CommonWarnings().ToList();
        var planRows = new List<PlanVsActual>();
        Dictionary<DateOnly, double>? plannedByDay = null;
        double plannedTotal = 0;
        var planNote = ctx.Simulated ? "Plans are compared with REAL output only."
            : ctx.Filter.MachineId is not null || ctx.Filter.WorkstationId is not null ? "Plans are per line: choose no machine or workstation to compare."
            : ctx.Filter.ShiftId is not null ? "Plans follow the production calendar's shifts, not factory shifts: clear the shift filter to compare."
            : ctx.EventSource is not null ? "Clear the event source filter to compare with plans." : null;
        if (planNote is null)
        {
            var scopeLines = await ScopeLinesAsync(ctx, ct);
            var targets = await new PlanTargets(_db, _options).ForLinesAsync(scopeLines, ctx.Period, ctx.Now, ct);
            plannedByDay = new Dictionary<DateOnly, double>();
            var names = await ctx.LineNamesAsync(targets.Keys, ct);
            foreach (var t in targets.Values.OrderBy(x => x.LineId))
            {
                var actual = lines.GetValueOrDefault(t.LineId);
                planRows.Add(new PlanVsActual(t.LineId, names.GetValueOrDefault(t.LineId, t.LineId), t.Planned, actual,
                    t.Planned > 0 ? AnalyticsMath.Pct(actual, t.Planned) : null, t.Plans,
                    "Counter pieces under the machines' counting rules vs the plan's pieces; check both count the same unit."));
                plannedTotal += t.Planned;
                foreach (var (d, v) in t.PlannedByDay) plannedByDay[d] = plannedByDay.GetValueOrDefault(d) + v;
            }
            if (targets.Count == 0) planNote = "No active production plan covers these lines and dates: planned vs actual is not shown.";
        }

        // Hourly (short ranges; rows are bucketed here) and daily trends.
        var hourly = new List<TrendPoint>();
        if (ctx.Period.Days <= MaxHourlyDays)
        {
            var points = await counted.Select(e => new { e.OccurredAt, e.Pieces }).ToListAsync(ct);
            var buckets = new SortedDictionary<DateTimeOffset, long>();
            for (var h = Hour(ctx.Period.FromUtc); h < ctx.Period.ToUtc; h = h.AddHours(1)) buckets[h] = 0;
            foreach (var p in points) { var h = Hour(p.OccurredAt); buckets[h] = buckets.GetValueOrDefault(h) + p.Pieces; }
            foreach (var x in corrections.Where(x => x.HourStart is not null)) { var h = Hour(x.HourStart!.Value); if (buckets.ContainsKey(h)) buckets[h] += x.PiecesDelta; }
            hourly = buckets.Select(b => new TrendPoint(b.Key.ToString("O"), TimeZoneInfo.ConvertTime(b.Key, ctx.Zone).ToString("dd MMM HH:00", CultureInfo.InvariantCulture),
                b.Key, b.Value)).ToList();
        }
        var daily = ctx.Period.Dates().Select(d => new TrendPoint(d.ToString("yyyy-MM-dd"), d.ToString("dd MMM", CultureInfo.InvariantCulture),
            ctx.Period.Day(d, ctx.Zone, ctx.Now).Start, days.GetValueOrDefault(d), plannedByDay is null ? null : Math.Round(plannedByDay.GetValueOrDefault(d), 0))).ToList();

        var quality = new CounterQuality(statuses.Values.Sum(), statuses.GetValueOrDefault(CountStatuses.Reset),
            statuses.GetValueOrDefault(CountStatuses.Baseline) + statuses.GetValueOrDefault(CountStatuses.OutOfOrder) + statuses.GetValueOrDefault(CountStatuses.SuspectJump),
            statuses.GetValueOrDefault(CountStatuses.SuspectJump), correctionPieces, corrections.Count,
            "A counter message is stored once per device + message id, so a resend (MQTT redelivery, HTTP retry, both transports) is never counted twice.");

        var kpis = new List<Kpi>
        {
            Kpi.Count("production.pieces", total, countedPieces == 0 && corrections.Count == 0 ? "No production counter data in this scope and period." : null,
                statuses.Count == 0 ? Availability.NoData : Availability.Ok),
            hours >= 1d / 60 && (statuses.Count > 0 || corrections.Count > 0) ? Kpi.Count("production.pieces_per_hour", Math.Round(total / hours, 1)) : Kpi.Missing("production.pieces_per_hour"),
            planRows.Count > 0 ? Kpi.Count("production.planned", plannedTotal) : Kpi.Missing("production.planned", planNote),
            planRows.Count > 0 ? Kpi.Ratio("production.achievement", planRows.Sum(r => r.Actual), plannedTotal) : Kpi.Missing("production.achievement", planNote),
            Kpi.Count("production.corrections", correctionPieces, corrections.Count == 0 ? null : $"{corrections.Count} correction(s) with reasons"),
            Kpi.Count("production.resets", quality.Resets),
            Kpi.Count("production.excluded", quality.Excluded, quality.SuspectJumps > 0 ? $"{quality.SuspectJumps} implausible jump(s) need a person to check" : null),
        };
        if (statuses.Count == 0) warnings.Add("No production counter reported in this scope and period: output figures are empty, not zero production.");
        if (quality.SuspectJumps > 0) warnings.Add($"{quality.SuspectJumps} counter reading(s) were implausible and not counted; correct them if real.");
        if (planNote is not null) warnings.Add(planNote);
        if (ctx.Filter.DepartmentId is not null || ctx.Filter.FloorId is not null)
            warnings.Add("Department / floor come from the counter's workstation; counters placed only on a line are not included.");

        return new ProductionAnalyticsDto(ctx.Dto(), kpis,
            Groups(factories, k => k.ToString(), k => fNames.GetValueOrDefault(k, "?")),
            byDept.OrderByDescending(x => x.N).Select(x => G(x.Key.ToString(), deptNames.GetValueOrDefault(x.Key, "?"), x.N)).ToList(),
            byFloor.OrderByDescending(x => x.N).Select(x => G(x.Key.ToString(), floorNames.GetValueOrDefault(x.Key, "?"), x.N)).ToList(),
            lines.OrderByDescending(x => x.Value).Select(x =>
            {
                var plan = planRows.FirstOrDefault(p => p.LineId == x.Key);
                return G(x.Key, lineNames.GetValueOrDefault(x.Key, x.Key), x.Value) with { Planned = plan?.Planned, AchievementPct = plan?.AchievementPct };
            }).ToList(),
            Groups(wss, k => k.ToString(), k => wsNames.GetValueOrDefault(k, "?")),
            Groups(machines, k => k.ToString(), k => mNames.GetValueOrDefault(k, "?")),
            shifts.OrderBy(x => x.Key).Select(x => G(x.Key, x.Key == "" ? "Outside shifts" : shiftNames.FirstOrDefault(s => s.Code == x.Key)?.Name ?? x.Key, x.Value)).ToList(),
            hourly, daily, planRows, quality, warnings);
    }

    /// <summary>The lines the request covers: the chosen line, or every active line of the factories in reach (supervisors: their lines).</summary>
    internal async Task<IReadOnlyCollection<string>> ScopeLinesAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ctx.Filter.LineId)) return new[] { ctx.Filter.LineId.Trim() };
        var q = _db.ProductionLines.AsNoTracking().Where(l => l.IsActive && l.FactoryId != null);
        var caller = ctx.Caller;
        if (ctx.Filter.FactoryId is { } f) q = q.Where(l => l.FactoryId == f);
        else if (caller.Scope != RoleScope.Platform) { var fs = caller.FactoryIds.ToList(); q = q.Where(l => fs.Contains(l.FactoryId!.Value)); }
        if (caller.Scope == RoleScope.Line) { var ls = caller.LineIds.ToList(); q = q.Where(l => ls.Contains(l.Id)); }
        if (ctx.Filter.DepartmentId is { } d) q = q.Where(l => _db.Workstations.Any(w => w.LineId == l.Id && w.DepartmentId == d));
        if (ctx.Filter.FloorId is { } fl) q = q.Where(l => _db.Workstations.Any(w => w.LineId == l.Id && w.FloorId == fl));
        return await q.Select(l => l.Id).ToListAsync(ct);
    }

    private static DateTimeOffset Hour(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}
