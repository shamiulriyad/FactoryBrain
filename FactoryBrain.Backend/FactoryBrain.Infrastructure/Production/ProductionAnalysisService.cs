using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Approvals;
using FactoryBrain.Application.Dtos.Production;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static FactoryBrain.Infrastructure.Production.ProductionSupport;

namespace FactoryBrain.Infrastructure.Production;

/// <summary>
/// Read-side analysis (planned vs actual, efficiency, WIP, delay risk,
/// bottlenecks) computed from persisted rows on every request, plus time
/// studies, recorded layouts and the line-balancing proposal workflow.
/// </summary>
public sealed class ProductionAnalysisService : IProductionAnalysisService
{
    public const int MaxSummaryDays = 93;
    private const int MaxRiskOrders = 50;

    private readonly FactoryBrainDbContext _db;
    private readonly ProductionOptions _options;
    private readonly ProductionReadModel _read;
    private readonly ILogger<ProductionAnalysisService> _log;

    public ProductionAnalysisService(FactoryBrainDbContext db, ProductionOptions options, ILogger<ProductionAnalysisService> log)
    {
        _db = db; _options = options; _log = log; _read = new ProductionReadModel(db, options);
    }

    // ── Summary ─────────────────────────────────────────────────────────────

    private sealed record EventRow(Guid Id, string LineId, Guid OrderId, string? ShiftCode, DateOnly ProductionDate, DateTime Start, DateTime End,
        int Quantity, int Rework, int Reject, int? Operators, double? Downtime, ProductionDataSource Source, DateTime RecordedAt, Guid? CorrectionOfId);

    private sealed record PlannedPiece(string LineId, Guid OrderId, string Shift, DateOnly Date, double Pieces);

    public async Task<ProductionResult<ProductionSummary>> SummaryAsync(SummaryQuery q, CancellationToken ct)
    {
        var cal = await _read.CalendarAsync(ct);
        var now = DateTime.UtcNow;
        var today = cal.LocalDate(now);
        var to = q.To ?? today;
        var from = q.From ?? to.AddDays(-6);
        var errors = Errors();
        if (from > to) errors.Add("from", "from must not be after to.");
        else if (to.DayNumber - from.DayNumber + 1 > MaxSummaryDays) errors.Add("to", $"The range is limited to {MaxSummaryDays} days.");
        if (errors.Count > 0) return ProductionResult<ProductionSummary>.Invalid(errors);

        // The comparison cutoff comes from all of a line's records; the shift filter applies after it.
        var allEvents = await EventsAsync(from, to, q.LineId, q.OrderId, ct);
        var events = ForShift(allEvents, q.Shift);
        var plans = await PlansAsync(from, to, q.LineId, q.OrderId, ct);
        var orderIds = events.Select(e => e.OrderId).Concat(plans.Select(p => p.OrderId)).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var smv = await _read.StyleSmvAsync(orders.Values.Select(o => o.StyleId), ct);
        double? Smv(Guid orderId) => orders.TryGetValue(orderId, out var o) && smv.TryGetValue(o.StyleId, out var s) ? s.Smv : null;

        var cutoffs = LineCutoffs(allEvents, plans.Select(p => p.LineId), now);
        var planned = Planned(plans, cal, from, to, q.Shift, cutoffs, Smv);
        var comparedThrough = cutoffs.Count == 0 ? Min(now, cal.ToUtc(to.AddDays(1), TimeOnly.MinValue)) : cutoffs.Values.Max();

        var lines = await _db.ProductionLines.AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
        var totals = await TotalsAsync(events, planned, plans, cal, from, to, q.LineId, Smv, ct);

        SummaryRow Row(string key, string label, IEnumerable<EventRow> ev, double plannedPieces)
        {
            var list = ev.ToList();
            var actual = list.Sum(e => e.Quantity);
            var p = (int)Math.Floor(plannedPieces + 1e-9);
            var eff = Efficiency(list, cal, Smv);
            return new SummaryRow(key, label, actual, p, ProductionMetrics.Variance(actual, p), ProductionMetrics.AchievementPct(actual, p),
                eff.EfficiencyPct, ProductionMetrics.Round1(list.Sum(e => e.Downtime ?? 0)), list.Sum(e => e.Rework), list.Sum(e => e.Reject), list.Count);
        }

        var byLine = events.Select(e => e.LineId).Concat(planned.Select(p => p.LineId)).Distinct().OrderBy(x => x, StringComparer.Ordinal)
            .Select(l => Row(l, lines.TryGetValue(l, out var line) ? line.Name : l, events.Where(e => e.LineId == l), planned.Where(p => p.LineId == l).Sum(p => p.Pieces))).ToList();
        var byOrder = events.Select(e => e.OrderId).Concat(planned.Select(p => p.OrderId)).Distinct()
            .Select(o => Row(o.ToString(), orders.TryGetValue(o, out var order) ? order.OrderNumber : o.ToString(), events.Where(e => e.OrderId == o), planned.Where(p => p.OrderId == o).Sum(p => p.Pieces)))
            .OrderBy(r => r.Label, StringComparer.Ordinal).ToList();
        var shiftNames = (await _db.ProductionShifts.AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.Code, s => s.Name);
        var byShift = events.Select(e => e.ShiftCode ?? "overtime").Concat(planned.Select(p => p.Shift)).Distinct().OrderBy(x => x, StringComparer.Ordinal)
            .Select(s => Row(s, s == "overtime" ? "Overtime / outside shifts" : shiftNames.GetValueOrDefault(s, s), events.Where(e => (e.ShiftCode ?? "overtime") == s), planned.Where(p => p.Shift == s).Sum(p => p.Pieces))).ToList();
        var daily = new List<DailyPoint>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var ev = events.Where(e => e.ProductionDate == d).ToList();
            var p = (int)Math.Floor(planned.Where(x => x.Date == d).Sum(x => x.Pieces) + 1e-9);
            var a = ev.Sum(e => e.Quantity);
            daily.Add(new DailyPoint(ProductionWire.Date(d), a, p, ProductionMetrics.AchievementPct(a, p), Efficiency(ev, cal, Smv).EfficiencyPct,
                ProductionMetrics.Round1(ev.Sum(e => e.Downtime ?? 0))));
        }

        var wip = await WipAsync(q.LineId, q.OrderId, ct);
        var risks = await RisksAsync(q.LineId, q.OrderId, now, ct);
        var bottlenecks = await BottlenecksAsync(q.LineId, q.OrderId, now, ct);
        var quality = Quality(events, plans, cal, Smv, cutoffs, now);
        var sources = events.GroupBy(e => ProductionWire.Source(e.Source)).ToDictionary(g => g.Key, g => g.Count());

        return ProductionResult<ProductionSummary>.Ok(new ProductionSummary(ProductionWire.Date(from), ProductionWire.Date(to), _options.TimeZoneId,
            cal.ToUtc(from, TimeOnly.MinValue), cal.ToUtc(to.AddDays(1), TimeOnly.MinValue), now, comparedThrough,
            totals, byLine, byOrder, byShift, daily, wip, risks, bottlenecks, quality, sources, ProductionSetupService.Definitions));
    }

    private static List<EventRow> ForShift(List<EventRow> events, string? shift) =>
        shift is null ? events : events.Where(e => (e.ShiftCode ?? "overtime") == shift).ToList();

    /// <summary>Live sewing-output records whose production date is in the range.</summary>
    private async Task<List<EventRow>> EventsAsync(DateOnly from, DateOnly to, string? lineId, Guid? orderId, CancellationToken ct)
    {
        var q = _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput && e.ProductionDate >= from && e.ProductionDate <= to);
        if (lineId is not null) q = q.Where(e => e.LineId == lineId);
        if (orderId is { } oid) q = q.Where(e => e.OrderId == oid);
        return await q.Select(e => new EventRow(e.Id, e.LineId, e.OrderId, e.ShiftCode, e.ProductionDate, e.IntervalStartUtc, e.IntervalEndUtc,
            e.Quantity, e.ReworkQuantity, e.RejectQuantity, e.OperatorsPresent, e.DowntimeMinutes, e.Source, e.RecordedAt, e.CorrectionOfId)).ToListAsync(ct);
    }

    /// <summary>Plans that were live in the range (active, or completed — up to when they were closed).</summary>
    private Task<List<ProductionPlan>> PlansAsync(DateOnly from, DateOnly to, string? lineId, Guid? orderId, CancellationToken ct)
    {
        var q = _db.ProductionPlans.AsNoTracking()
            .Where(p => (p.Status == ProductionPlanStatus.Active || p.Status == ProductionPlanStatus.Completed) && p.StartDate <= to && p.EndDate >= from);
        if (lineId is not null) q = q.Where(p => p.LineId == lineId);
        if (orderId is { } oid) q = q.Where(p => p.OrderId == oid);
        return q.ToListAsync(ct);
    }

    /// <summary>
    /// Per line, how far the comparison runs: the end of the last recorded
    /// interval (so an hour that hasn't been reported yet isn't counted as a
    /// shortfall), or now when nothing is recorded in the window.
    /// </summary>
    private static Dictionary<string, DateTime> LineCutoffs(IReadOnlyList<EventRow> events, IEnumerable<string> planLines, DateTime now)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var line in events.Select(e => e.LineId).Concat(planLines).Distinct())
        {
            var ends = events.Where(e => e.LineId == line).Select(e => e.End).ToList();
            map[line] = ends.Count == 0 ? now : Min(now, ends.Max());
        }
        return map;
    }

    private static List<PlannedPiece> Planned(IReadOnlyList<ProductionPlan> plans, ProductionCalendar cal, DateOnly from, DateOnly to, string? shift,
        IReadOnlyDictionary<string, DateTime> cutoffs, Func<Guid, double?> smvOfOrder)
    {
        var list = new List<PlannedPiece>();
        foreach (var p in plans)
        {
            var schedule = ProductionReadModel.Schedule(p, cal, smvOfOrder(p.OrderId));
            var cutoff = cutoffs.GetValueOrDefault(p.LineId, DateTime.UtcNow);
            if (p.Status == ProductionPlanStatus.Completed && p.ClosedAt is { } closed && closed < cutoff) cutoff = closed;
            foreach (var w in schedule.Windows)
            {
                if (w.ProductionDate < from || w.ProductionDate > to) continue;
                var code = w.ShiftCode ?? "overtime";
                if (shift is not null && shift != code) continue;
                var end = Min(w.EndUtc, cutoff);
                if (end <= w.StartUtc) continue;
                list.Add(new PlannedPiece(p.LineId, p.OrderId, code, w.ProductionDate, schedule.CumulativeAt(end) - schedule.CumulativeAt(w.StartUtc)));
            }
        }
        return list;
    }

    private static EfficiencyResult Efficiency(IEnumerable<EventRow> events, ProductionCalendar cal, Func<Guid, double?> smv) =>
        EfficiencyCalculator.Calculate(events.Select(e => new EfficiencyInput(e.Quantity, smv(e.OrderId), e.Operators, e.Start, e.End, e.Downtime)),
            cal.BreakMinutesWithin);

    private async Task<SummaryTotals> TotalsAsync(IReadOnlyList<EventRow> events, IReadOnlyList<PlannedPiece> planned, IReadOnlyList<ProductionPlan> plans,
        ProductionCalendar cal, DateOnly from, DateOnly to, string? lineId, Func<Guid, double?> smv, CancellationToken ct)
    {
        var actual = events.Sum(e => e.Quantity);
        var plannedPieces = (int)Math.Floor(planned.Sum(p => p.Pieces) + 1e-9);
        var eff = Efficiency(events, cal, smv);
        // Line loading: scheduled plan minutes vs the shift calendar of the active lines in scope.
        var scheduled = plans.Sum(p => cal.Windows(p.StartDate, p.EndDate, p.ShiftCodes, p.OvertimeMinutesPerDay)
            .Where(w => w.ProductionDate >= from && w.ProductionDate <= to).Sum(w => w.Minutes));
        var lineCount = lineId is not null ? (await _db.ProductionLines.AnyAsync(l => l.Id == lineId && l.IsActive, ct) ? 1 : 0)
            : await _db.ProductionLines.CountAsync(l => l.IsActive, ct);
        var calendarMinutes = cal.Windows(from, to).Sum(w => w.Minutes) * lineCount;
        var unplanned = events.Where(e => !plans.Any(p => p.LineId == e.LineId && p.OrderId == e.OrderId && p.StartDate <= e.ProductionDate && p.EndDate >= e.ProductionDate)).ToList();
        return new SummaryTotals(actual, plannedPieces, ProductionMetrics.Variance(actual, plannedPieces), ProductionMetrics.AchievementPct(actual, plannedPieces),
            eff.EfficiencyPct, eff.EfficiencyExcludingDowntimePct, eff.EarnedMinutes, eff.AvailableMinutes,
            ProductionMetrics.Round1(events.Sum(e => e.Downtime ?? 0)), events.Sum(e => e.Rework), events.Sum(e => e.Reject), events.Count,
            ProductionMetrics.Round1(scheduled), ProductionMetrics.Round1(calendarMinutes),
            calendarMinutes > 0 ? ProductionMetrics.Round1(scheduled / calendarMinutes * 100) : null, unplanned.Sum(e => e.Quantity));
    }

    private DataQualityDto Quality(IReadOnlyList<EventRow> events, IReadOnlyList<ProductionPlan> plans, ProductionCalendar cal, Func<Guid, double?> smv,
        IReadOnlyDictionary<string, DateTime> cutoffs, DateTime now)
    {
        var notes = new List<string>();
        var missingOps = events.Count(e => e.Operators is null);
        var missingSmv = events.Count(e => e.Operators is not null && smv(e.OrderId) is null);
        var unplanned = events.Count(e => !plans.Any(p => p.LineId == e.LineId && p.OrderId == e.OrderId && p.StartDate <= e.ProductionDate && p.EndDate >= e.ProductionDate));
        var late = events.Count(e => e.RecordedAt - e.End > OutputRules.LateThreshold);
        var corrected = events.Count(e => e.CorrectionOfId is not null);
        if (missingOps > 0) notes.Add($"{missingOps} output record(s) have no operator count and are left out of efficiency.");
        if (missingSmv > 0) notes.Add($"{missingSmv} output record(s) are for styles without an SMV and are left out of efficiency.");
        if (unplanned > 0) notes.Add($"{unplanned} output record(s) have no live plan for their line and order; they count as actual output with nothing planned.");
        if (late > 0) notes.Add($"{late} record(s) arrived more than {OutputRules.LateThreshold.TotalHours:0} hours after their interval; they are included.");
        if (corrected > 0) notes.Add($"{corrected} record(s) are corrections; the records they replaced are excluded.");
        foreach (var line in plans.Select(p => p.LineId).Distinct().Where(l => events.All(e => e.LineId != l)))
            notes.Add($"No output is recorded for {line} in this window, although it has a live plan: planned output is compared through now.");
        foreach (var (line, cut) in cutoffs.Where(c => c.Value < now.AddMinutes(-90) && events.Any(e => e.LineId == c.Key)))
            if (plans.Any(p => p.LineId == line && p.Status == ProductionPlanStatus.Active))
                notes.Add($"{line}: the comparison runs to the last recorded interval ({cal.ToLocal(cut):yyyy-MM-dd HH:mm} local).");
        return new DataQualityDto(events.Count, missingOps, missingSmv, unplanned, late, corrected, notes);
    }

    private async Task<IReadOnlyList<LineWipDto>> WipAsync(string? lineId, Guid? orderId, CancellationToken ct)
    {
        var q = from e in _db.ProductionOutputEvents.AsNoTracking()
                join o in _db.ProductionOrders.AsNoTracking() on e.OrderId equals o.Id
                where !e.IsSuperseded && o.Status == ProductionOrderStatus.Open
                select e;
        if (lineId is not null) q = q.Where(e => e.LineId == lineId);
        if (orderId is { } oid) q = q.Where(e => e.OrderId == oid);
        var counts = await q.GroupBy(e => new { e.LineId, e.OrderId, e.Stage })
            .Select(g => new { g.Key.LineId, g.Key.OrderId, g.Key.Stage, Good = g.Sum(e => e.Quantity), Reject = g.Sum(e => e.RejectQuantity), Rework = g.Sum(e => e.ReworkQuantity), Records = g.Count() })
            .ToListAsync(ct);
        return counts.GroupBy(c => c.LineId).OrderBy(g => g.Key, StringComparer.Ordinal).Select(line =>
        {
            // Buffers are per order (pieces of different orders are different pieces), then added per line;
            // a buffer with any inconsistent or incomplete order is reported as partial, never netted out.
            var perOrder = line.GroupBy(c => c.OrderId)
                .SelectMany(o => WipCalculator.Calculate(o.Select(c => new StageCount(c.Stage, c.Good, c.Reject, c.Rework, c.Records)).ToList())).ToList();
            var buffers = perOrder.GroupBy(b => b.Key).Select(g =>
            {
                var first = g.First();
                var ok = g.Where(b => b.State == "ok").ToList();
                var state = ok.Count == g.Count() ? "ok" : ok.Count == 0 ? g.First().State : "partial";
                return new WipBufferDto(first.Key, first.Label, ProductionWire.Stage(first.From), ProductionWire.Stage(first.To),
                    ok.Count == 0 ? null : ok.Sum(b => b.Pieces), state);
            }).ToList();
            return new LineWipDto(line.Key, buffers);
        }).ToList();
    }

    private async Task<IReadOnlyList<DelayedOrderDto>> RisksAsync(string? lineId, Guid? orderId, DateTime now, CancellationToken ct)
    {
        var q = _db.ProductionOrders.AsNoTracking().Where(o => o.Status == ProductionOrderStatus.Open
            && _db.ProductionPlans.Any(p => p.OrderId == o.Id && p.Status == ProductionPlanStatus.Active && (lineId == null || p.LineId == lineId)));
        if (orderId is { } oid) q = q.Where(o => o.Id == oid);
        var orders = await q.OrderBy(o => o.DueDate).Take(MaxRiskOrders).ToListAsync(ct);
        var list = new List<DelayedOrderDto>();
        foreach (var o in orders)
        {
            var r = await _read.RiskAsync(o, now, ct, withRecommendations: false);
            list.Add(new DelayedOrderDto(o.Id, o.OrderNumber, ProductionWire.Date(o.DueDate), r.Level, r.RemainingQuantity,
                r.RequiredRatePerLineHour, r.ObservedRatePerLineHour, r.ProjectedCompletion));
        }
        static int Rank(string level) => level switch { "high" => 0, "medium" => 1, "insufficient_data" => 2, "low" => 3, _ => 4 };
        return list.OrderBy(r => Rank(r.Level)).ThenBy(r => r.DueDate, StringComparer.Ordinal).ToList();
    }

    private async Task<IReadOnlyList<LineBottleneckDto>> BottlenecksAsync(string? lineId, Guid? orderId, DateTime now, CancellationToken ct)
    {
        var pairs = await (from p in _db.ProductionPlans.AsNoTracking()
                           join o in _db.ProductionOrders.AsNoTracking() on p.OrderId equals o.Id
                           join s in _db.ProductionStyles.AsNoTracking() on o.StyleId equals s.Id
                           where p.Status == ProductionPlanStatus.Active && (lineId == null || p.LineId == lineId) && (orderId == null || p.OrderId == orderId)
                           select new { p.LineId, StyleId = s.Id, s.Code }).Distinct().ToListAsync(ct);
        var list = new List<LineBottleneckDto>();
        foreach (var pair in pairs.OrderBy(p => p.LineId, StringComparer.Ordinal))
        {
            var b = await _read.BottleneckAsync(pair.LineId, pair.StyleId, now, ct);
            list.Add(new LineBottleneckDto(pair.LineId, pair.StyleId, pair.Code, b.Status,
                b.Constraint is { } c ? $"{c.Sequence}. {c.Name}" + (b.ConstraintStationNo is { } st ? $" (workstation {st})" : "") : null,
                b.ConstraintCapacityPerHour, b.Evidence.FirstOrDefault() ?? b.MissingInputs.FirstOrDefault()));
        }
        return list;
    }

    // ── Line detail ─────────────────────────────────────────────────────────

    public async Task<ProductionResult<LineDetail>> LineDetailAsync(string lineId, DateOnly? date, string? shift, CancellationToken ct)
    {
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return ProductionResult<LineDetail>.NotFound("line_not_found");
        var cal = await _read.CalendarAsync(ct);
        var now = DateTime.UtcNow;
        var day = date ?? cal.LocalDate(now);
        if (shift is not null && shift != "overtime" && cal.Shifts.All(s => s.Code != shift))
            return ProductionResult<LineDetail>.Invalid("shift", "Unknown or inactive shift.");

        var lineEvents = await EventsAsync(day, day, lineId, null, ct);
        var events = ForShift(lineEvents, shift);
        var plans = await PlansAsync(day, day, lineId, null, ct);
        var orderIds = events.Select(e => e.OrderId).Concat(plans.Select(p => p.OrderId)).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var smv = await _read.StyleSmvAsync(orders.Values.Select(o => o.StyleId), ct);
        double? Smv(Guid orderId) => orders.TryGetValue(orderId, out var o) && smv.TryGetValue(o.StyleId, out var s) ? s.Smv : null;
        var cutoffs = LineCutoffs(lineEvents, plans.Select(p => p.LineId), now);
        var cutoff = cutoffs.GetValueOrDefault(lineId, now);
        var planned = Planned(plans, cal, day, day, shift, cutoffs, Smv);
        var totals = await TotalsAsync(events, planned, plans, cal, day, day, lineId, Smv, ct);

        // Hourly buckets (plant-local clock hours) across the day's scheduled time and recorded intervals.
        var dayWindows = cal.WindowsFor(day).Where(w => shift is null || (w.ShiftCode ?? "overtime") == shift).ToList();
        var schedules = plans.Select(p => (Plan: p, Schedule: ProductionReadModel.Schedule(p, cal, Smv(p.OrderId)))).ToList();
        var planWindows = schedules.SelectMany(s => s.Schedule.Windows.Where(w => w.ProductionDate == day && (shift is null || (w.ShiftCode ?? "overtime") == shift))).ToList();
        var starts = dayWindows.Select(w => w.StartUtc).Concat(planWindows.Select(w => w.StartUtc)).Concat(events.Select(e => e.Start)).ToList();
        var ends = dayWindows.Select(w => w.EndUtc).Concat(planWindows.Select(w => w.EndUtc)).Concat(events.Select(e => e.End)).ToList();
        var timeline = new List<TimelinePoint>();
        DateTime dayFrom, dayTo;
        if (starts.Count == 0) { dayFrom = cal.ToUtc(day, TimeOnly.MinValue); dayTo = cal.ToUtc(day.AddDays(1), TimeOnly.MinValue); }
        else
        {
            var firstLocal = cal.ToLocal(starts.Min());
            dayFrom = cal.ToUtc(DateOnly.FromDateTime(firstLocal), new TimeOnly(firstLocal.Hour, 0));
            dayTo = ends.Max();
            for (var b = dayFrom; b < dayTo && timeline.Count < 48; b = b.AddHours(1))
            {
                var e = b.AddHours(1);
                var bucketEvents = events.Where(x => x.Start >= b && x.Start < e).ToList();
                var plannedBucket = schedules.Where(s => s.Plan.Status == ProductionPlanStatus.Active || s.Plan.ClosedAt is null || s.Plan.ClosedAt > b)
                    .Sum(s =>
                    {
                        var end = Min(e, cutoff);
                        if (s.Plan.Status == ProductionPlanStatus.Completed && s.Plan.ClosedAt is { } c) end = Min(end, c);
                        return end <= b ? 0 : s.Schedule.PlannedBetween(b, end, w => w.ProductionDate == day && (shift is null || (w.ShiftCode ?? "overtime") == shift));
                    });
                var local = cal.ToLocal(b);
                var (_, bucketShift) = cal.Classify(b);
                timeline.Add(new TimelinePoint(b, e, $"{local:HH}:00", bucketShift, bucketEvents.Sum(x => x.Quantity), ProductionMetrics.Round1(plannedBucket),
                    Efficiency(bucketEvents, cal, Smv).EfficiencyPct, ProductionMetrics.Round1(bucketEvents.Sum(x => x.Downtime ?? 0)), bucketEvents.Count));
            }
        }

        var planRows = await _db.ProductionPlans.AsNoTracking()
            .Where(p => p.LineId == lineId && p.Status != ProductionPlanStatus.Cancelled && p.StartDate <= day && p.EndDate >= day)
            .OrderBy(p => p.StartDate).ToListAsync(ct);
        var allEvents = await _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => e.LineId == lineId && e.ProductionDate == day && (shift == null || (shift == "overtime" ? e.ShiftCode == null : e.ShiftCode == shift)))
            .OrderBy(e => e.IntervalStartUtc).ThenBy(e => e.RecordedAt).Take(500).ToListAsync(ct);

        // Styles on the line: of the live plans, else of the orders with output that day.
        var styleIds = planRows.Where(p => p.Status == ProductionPlanStatus.Active).Select(p => p.OrderId)
            .Concat(events.Select(e => e.OrderId)).Distinct()
            .Select(o => orders.TryGetValue(o, out var order) ? order.StyleId : (Guid?)null).Where(s => s is not null).Select(s => s!.Value).Distinct().ToList();
        var activeOrderIds = planRows.Select(p => p.OrderId).ToList();
        var extraStyles = await _db.ProductionOrders.AsNoTracking().Where(o => activeOrderIds.Contains(o.Id)).Select(o => o.StyleId).ToListAsync(ct);
        styleIds = styleIds.Concat(extraStyles).Distinct().ToList();
        var styles = await _db.ProductionStyles.AsNoTracking().Where(s => styleIds.Contains(s.Id)).OrderBy(s => s.Code).ToListAsync(ct);
        var analyses = new List<StyleAnalysisDto>();
        foreach (var s in styles)
        {
            var layout = (await CurrentLayoutAsync(lineId, s.Id, ct)).Value;
            analyses.Add(new StyleAnalysisDto(s.Id, s.Code, s.Name, await _read.BottleneckAsync(lineId, s.Id, now, ct), layout));
        }

        return ProductionResult<LineDetail>.Ok(new LineDetail(ProductionSupport.ToDto(line), ProductionWire.Date(day), shift, _options.TimeZoneId, dayFrom, dayTo,
            cutoff, totals, timeline, await ProductionPlanService.ListItemsAsync(_db, planRows, ct),
            await ProductionOutputService.ToDtosAsync(_db, allEvents, ct), await WipAsync(lineId, null, ct), analyses, ProductionSetupService.Definitions));
    }

    public async Task<ProductionResult<RiskAssessmentDto>> OrderRiskAsync(Guid orderId, CancellationToken ct)
    {
        var order = await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        return order is null ? ProductionResult<RiskAssessmentDto>.NotFound("order_not_found")
            : ProductionResult<RiskAssessmentDto>.Ok(await _read.RiskAsync(order, DateTime.UtcNow, ct));
    }

    // ── Time studies ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ObservationDto>> ListObservationsAsync(string? lineId, Guid? styleId, int limit, CancellationToken ct)
    {
        var q = from o in _db.OperationObservations.AsNoTracking()
                join op in _db.StyleOperations.AsNoTracking() on o.OperationId equals op.Id
                select new { o, op.Sequence, op.Name };
        if (lineId is not null) q = q.Where(x => x.o.LineId == lineId);
        if (styleId is { } sid) q = q.Where(x => x.o.StyleId == sid);
        var rows = await q.OrderByDescending(x => x.o.ObservedAtUtc).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return rows.Select(x => ToDto(x.o, x.Sequence, x.Name)).ToList();
    }

    public async Task<ProductionResult<ObservationDto>> RecordObservationAsync(ObservationWrite b, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        var lineId = Clean(b.LineId);
        if (lineId is null || !await _db.ProductionLines.AnyAsync(l => l.Id == lineId, ct)) errors.Add("lineId", "The line does not exist.");
        StyleOperation? op = null;
        if (b.StyleId is null || b.OperationId is null) errors.Add("operationId", "styleId and operationId are required.");
        else
        {
            op = await _db.StyleOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == b.OperationId && o.StyleId == b.StyleId, ct);
            if (op is null) errors.Add("operationId", "The operation is not part of that style's bulletin.");
        }
        var now = DateTime.UtcNow;
        var observedAt = b.ObservedAt is { } at ? ProductionCalendar.AsUtc(at) : now;
        if (observedAt > now + ProductionOptions.FutureTolerance) errors.Add("observedAt", "observedAt can't be in the future.");
        if (observedAt < now.AddDays(-_options.MaxBackfillDays)) errors.Add("observedAt", $"Time studies older than {_options.MaxBackfillDays} days can't be recorded.");
        if (!(b.CycleSeconds is > 0 and <= 7200)) errors.Add("cycleSeconds", "Mean cycle time must be more than 0 and at most 7200 seconds.");
        if (b.SampleCount is not (>= 1 and <= 1000)) errors.Add("sampleCount", "Cycles timed must be from 1 to 1000.");
        if (b.OperatorsAssigned is not (>= 1 and <= 100)) errors.Add("operatorsAssigned", "Operators on the operation must be from 1 to 100.");
        if (b.QueueBefore is < 0 or > 100_000) errors.Add("queueBefore", "Queue must be from 0 to 100,000 pieces.");
        if (b.Notes is { Length: > 500 }) errors.Add("notes", "Notes are limited to 500 characters.");
        if (errors.Count > 0) return ProductionResult<ObservationDto>.Invalid(errors);

        var o = new OperationObservation
        {
            LineId = lineId!, StyleId = b.StyleId!.Value, OperationId = op!.Id, ObservedAtUtc = observedAt, CycleSeconds = Math.Round(b.CycleSeconds!.Value, 2),
            SampleCount = b.SampleCount!.Value, OperatorsAssigned = b.OperatorsAssigned!.Value, QueueBefore = b.QueueBefore, Notes = Clean(b.Notes),
            Source = ProductionDataSource.Manual, RecordedAt = now, RecordedByEmail = actor.Email,
        };
        _db.OperationObservations.Add(o);
        _db.AuditLog.Add(Audit(actor, traceId, "operation_observation.recorded", "operation_observation", o.Id.ToString(), null, null,
            $"{lineId} · op {op.Sequence} ({op.Name}) · {o.CycleSeconds:0.##} s × {o.SampleCount} cycles · {o.OperatorsAssigned} operator(s)" +
            (o.QueueBefore is { } qb ? $" · queue {qb}" : "")));
        await _db.SaveChangesAsync(ct);
        return ProductionResult<ObservationDto>.Created(ToDto(o, op.Sequence, op.Name));
    }

    private static ObservationDto ToDto(OperationObservation o, int seq, string name) => new(o.Id, o.LineId, o.StyleId, o.OperationId, seq, name,
        o.ObservedAtUtc, o.CycleSeconds, o.SampleCount, o.OperatorsAssigned, o.QueueBefore, o.Notes, ProductionWire.Source(o.Source), o.RecordedAt, o.RecordedByEmail);

    // ── Layouts ─────────────────────────────────────────────────────────────

    public async Task<ProductionResult<LayoutDto?>> CurrentLayoutAsync(string lineId, Guid styleId, CancellationToken ct)
    {
        var layout = await _db.LineLayouts.AsNoTracking().Include(l => l.Stations)
            .FirstOrDefaultAsync(l => l.LineId == lineId && l.StyleId == styleId && l.IsCurrent, ct);
        if (layout is null) return ProductionResult<LayoutDto?>.Ok(null);
        var ops = await _db.StyleOperations.AsNoTracking().Where(o => o.StyleId == styleId).ToListAsync(ct);
        return ProductionResult<LayoutDto?>.Ok(ToDto(layout, ops));
    }

    public async Task<ProductionResult<LayoutDto>> RecordLayoutAsync(LayoutWrite b, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        var lineId = Clean(b.LineId);
        var line = lineId is null ? null : await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) errors.Add("lineId", "The line does not exist.");
        var ops = b.StyleId is { } sid ? await _db.StyleOperations.AsNoTracking().Where(o => o.StyleId == sid).ToListAsync(ct) : new List<StyleOperation>();
        if (b.StyleId is null || ops.Count == 0) errors.Add("styleId", "Choose a style that has an operation bulletin.");
        var stations = b.Stations ?? Array.Empty<LayoutStationWrite>();
        if (stations.Count == 0) errors.Add("stations", "Give at least one workstation.");
        if (line is not null && stations.Count > line.Workstations) errors.Add("stations", $"The line declares {line.Workstations} workstations; the layout uses {stations.Count}.");
        if (stations.Select(s => s.StationNo).Distinct().Count() != stations.Count) errors.Add("stations", "Workstation numbers must be unique.");
        if (stations.Any(s => s.StationNo is < 1 or > 300)) errors.Add("stations", "Workstation numbers must be from 1 to 300.");
        var opIds = ops.Select(o => o.Id).ToHashSet();
        if (stations.Any(s => s.OperationIds is null || s.OperationIds.Count == 0)) errors.Add("stations", "Every workstation needs at least one operation.");
        if (stations.SelectMany(s => s.OperationIds ?? Array.Empty<Guid>()).Any(id => !opIds.Contains(id))) errors.Add("stations", "A workstation lists an operation that is not in this style's bulletin.");
        if (errors.Count > 0) return ProductionResult<LayoutDto>.Invalid(errors);

        var layout = new LineLayout
        {
            LineId = lineId!, StyleId = b.StyleId!.Value, Origin = "manual", CreatedAt = DateTime.UtcNow, CreatedByEmail = actor.Email,
            Stations = stations.OrderBy(s => s.StationNo).Select(s => new LineLayoutStation { StationNo = s.StationNo, OperationIds = s.OperationIds!.Distinct().ToArray() }).ToList(),
        };
        foreach (var st in layout.Stations) st.LayoutId = layout.Id;
        var issues = LineBalancer.CheckLayout(ops.Select(ToBalance).ToList(), layout.Stations.Select(s => new StationAssignment(s.StationNo, s.OperationIds)).ToList());
        await ReplaceCurrentLayoutAsync(layout, actor, traceId, "line_layout.recorded",
            $"{layout.Stations.Count} workstations recorded as found on the floor" + (issues.Count > 0 ? $"; {issues.Count} constraint issue(s) noted" : ""), ct);
        return ProductionResult<LayoutDto>.Created(ToDto(layout, ops));
    }

    /// <summary>
    /// Makes <paramref name="layout"/> the current one for its line and style, under the line lock.
    /// With <paramref name="requireBaseline"/>, nothing is written (and false returned) unless the
    /// current layout is still <paramref name="expectedBaseline"/>.
    /// </summary>
    private async Task<bool> ReplaceCurrentLayoutAsync(LineLayout layout, AuditActor actor, string? traceId, string action, string reason, CancellationToken ct,
        Func<Task>? alsoInTransaction = null, bool requireBaseline = false, Guid? expectedBaseline = null)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockLineAsync(_db, layout.LineId, ct);
            var now = DateTime.UtcNow;
            var previous = await _db.LineLayouts.Where(l => l.LineId == layout.LineId && l.StyleId == layout.StyleId && l.IsCurrent).ToListAsync(ct);
            if (requireBaseline && previous.FirstOrDefault()?.Id != expectedBaseline) { await tx.RollbackAsync(ct); return false; }
            foreach (var p in previous) { p.IsCurrent = false; p.SupersededAt = now; }
            await _db.SaveChangesAsync(ct);
            _db.LineLayouts.Add(layout);
            _db.AuditLog.Add(Audit(actor, traceId, action, "line_layout", layout.Id.ToString(),
                previous.Count == 0 ? null : $"layout {previous[0].Id}", $"layout {layout.Id}", $"{layout.LineId} · {reason}"));
            if (alsoInTransaction is not null) await alsoInTransaction();
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
    }

    private static LayoutDto ToDto(LineLayout l, IReadOnlyList<StyleOperation> ops)
    {
        var balanceOps = ops.Select(ToBalance).ToList();
        var assignments = l.Stations.OrderBy(s => s.StationNo).Select(s => new StationAssignment(s.StationNo, s.OperationIds)).ToList();
        var metrics = LineBalancer.Measure(balanceOps, assignments);
        return new LayoutDto(l.Id, l.LineId, l.StyleId, l.Origin, l.ProposalId, l.CreatedAt, l.CreatedByEmail,
            StationDtos(metrics, ops), metrics, LineBalancer.CheckLayout(balanceOps, assignments));
    }

    private static IReadOnlyList<LayoutStationDto> StationDtos(WorkloadMetrics m, IReadOnlyList<StyleOperation> ops)
    {
        var byId = ops.ToDictionary(o => o.Id);
        return m.StationLoads.Select(s => new LayoutStationDto(s.StationNo, s.OperationIds,
            s.OperationIds.Select(id => byId.TryGetValue(id, out var o) ? $"{o.Sequence}. {o.Name}" : "(removed operation)").ToList(),
            s.MachineType, s.LoadMinutes, s.UtilizationPct)).ToList();
    }

    // ── Line balancing ──────────────────────────────────────────────────────

    public async Task<ProductionResult<BalanceAnalysisResponse>> AnalyzeBalanceAsync(BalanceRequest b, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        var lineId = Clean(b.LineId);
        var line = lineId is null ? null : await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) errors.Add("lineId", "The line does not exist.");
        var style = b.StyleId is { } sid ? await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
        if (style is null) errors.Add("styleId", "The style does not exist.");
        var workstations = b.Workstations ?? line?.Workstations ?? 0;
        if (line is not null && (workstations < 1 || workstations > line.Workstations))
            errors.Add("workstations", $"Use from 1 to the line's declared {line.Workstations} workstations.");
        if (b.TargetOutputPerHour is { } t && !(t is > 0 and <= 100_000)) errors.Add("targetOutputPerHour", "Target output must be more than 0 pieces per hour.");
        if (errors.Count > 0) return ProductionResult<BalanceAnalysisResponse>.Invalid(errors);

        var ops = style!.Operations.OrderBy(o => o.Sequence).ToList();
        var result = LineBalancer.Propose(ops.Select(ToBalance).ToList(), workstations, b.TargetOutputPerHour);
        var current = (await CurrentLayoutAsync(line!.Id, style.Id, ct)).Value;
        var missing = new List<string>();
        if (ops.Count == 0) missing.Add("An operation bulletin for the style: each operation's sequence, SMV, machine type and predecessors (Setup → Styles).");
        if (!result.Feasible)
            return ProductionResult<BalanceAnalysisResponse>.Ok(new BalanceAnalysisResponse(result, null, current, missing.Concat(result.Problems).ToList()));

        var now = DateTime.UtcNow;
        var proposal = new LineBalanceProposal
        {
            LineId = line.Id, StyleId = style.Id, Workstations = workstations, TargetOutputPerHour = b.TargetOutputPerHour,
            InputHash = InputHash(ops), BaselineLayoutId = current?.Id, ResultJson = JsonSerializer.Serialize(result, Json),
            CreatedAt = now, CreatedByUserId = actor.UserId, CreatedByEmail = actor.Email,
        };
        proposal.Reference = NewReference("LB", proposal.Id, now);
        _db.BalanceProposals.Add(proposal);
        _db.AuditLog.Add(Audit(actor, traceId, "balance_proposal.created", "balance_proposal", proposal.Id.ToString(), null, "proposed",
            $"{proposal.Reference} · {line.Id} · {style.Code} · {workstations} workstations · cycle {result.Proposed!.CycleTimeMinutes:0.###} min · balance efficiency {result.Proposed.BalanceEfficiencyPct:0.#} %"));
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Balance proposal {ProposalId} created for {LineId}.", proposal.Id, line.Id);
        return ProductionResult<BalanceAnalysisResponse>.Ok(new BalanceAnalysisResponse(result, await GetProposalAsync(proposal.Id, ct), current, missing));
    }

    public async Task<ProductionResult<Paged<BalanceProposalListItem>>> ListProposalsAsync(string? lineId, Guid? styleId, string? status, int page, int pageSize, CancellationToken ct)
    {
        var errors = Errors();
        BalanceProposalStatus? st = null;
        if (status is not null)
        {
            if (ProductionWire.TryParseProposalStatus(status, out var s)) st = s;
            else errors.Add("status", $"status must be one of: {string.Join(", ", ProductionWire.ProposalStatuses)}");
        }
        if (page < 1) errors.Add("page", "page must be 1 or more.");
        if (pageSize is < 1 or > 100) errors.Add("pageSize", "pageSize must be from 1 to 100.");
        if (errors.Count > 0) return ProductionResult<Paged<BalanceProposalListItem>>.Invalid(errors);
        var q = _db.BalanceProposals.AsNoTracking();
        if (lineId is not null) q = q.Where(p => p.LineId == lineId);
        if (styleId is { } sid) q = q.Where(p => p.StyleId == sid);
        if (st is { } x) q = q.Where(p => p.Status == x);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(p => p.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var styleIds = rows.Select(r => r.StyleId).Distinct().ToList();
        var codes = await _db.ProductionStyles.AsNoTracking().Where(s => styleIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code, ct);
        var items = rows.Select(p =>
        {
            var r = JsonSerializer.Deserialize<BalanceProposalResult>(p.ResultJson, Json);
            return new BalanceProposalListItem(p.Id, p.Reference, p.LineId, p.StyleId, codes.GetValueOrDefault(p.StyleId, "?"), p.Workstations,
                ProductionWire.ProposalStatus(p.Status), r?.Proposed?.CycleTimeMinutes, r?.Proposed?.CapacityPerHour, r?.Proposed?.BalanceEfficiencyPct,
                p.CreatedAt, p.CreatedByEmail);
        }).ToList();
        return ProductionResult<Paged<BalanceProposalListItem>>.Ok(new Paged<BalanceProposalListItem>(items, page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize)));
    }

    public async Task<BalanceProposalDetail?> GetProposalAsync(Guid id, CancellationToken ct)
    {
        var p = await _db.BalanceProposals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var line = await _db.ProductionLines.AsNoTracking().FirstAsync(l => l.Id == p.LineId, ct);
        var style = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).FirstAsync(s => s.Id == p.StyleId, ct);
        var ops = style.Operations.OrderBy(o => o.Sequence).ToList();
        var result = JsonSerializer.Deserialize<BalanceProposalResult>(p.ResultJson, Json)!;
        var current = (await CurrentLayoutAsync(p.LineId, p.StyleId, ct)).Value;
        var inputsChanged = InputHash(ops) != p.InputHash;
        var baselineChanged = current?.Id != p.BaselineLayoutId;
        ComparisonDto? comparison = null;
        if (result.Proposed is { } proposed)
        {
            var cur = current?.Metrics;
            static double? Change(double? a, double b) => a is > 0 ? ProductionMetrics.Round1((b - a.Value) / a.Value * 100) : null;
            string Station(WorkloadMetrics m) => m.StationLoads.FirstOrDefault(s => s.StationNo == m.BottleneckStationNo) is { } s
                ? $"Workstation {s.StationNo} ({string.Join(", ", s.Sequences.Select(x => $"op {x}"))}, {s.LoadMinutes:0.###} min)" : "—";
            comparison = new ComparisonDto(cur, proposed, Change(cur?.CycleTimeMinutes, proposed.CycleTimeMinutes), Change(cur?.CapacityPerHour, proposed.CapacityPerHour),
                cur is null ? null : ProductionMetrics.Round1(proposed.BalanceEfficiencyPct - cur.BalanceEfficiencyPct), cur is null ? null : Station(cur), Station(proposed));
        }
        var approval = p.ApprovalId is null ? null : await _db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == p.ApprovalId, ct);
        return new BalanceProposalDetail(p.Id, p.Reference, p.LineId, line.Name, p.StyleId, style.Code, style.Name, p.Workstations, p.TargetOutputPerHour,
            ProductionWire.ProposalStatus(p.Status), p.StatusReason, result, result.Proposed is null ? Array.Empty<LayoutStationDto>() : StationDtos(result.Proposed, ops),
            current, inputsChanged, baselineChanged, comparison, Ref(approval), p.CreatedAt, p.CreatedByEmail, p.SubmittedByEmail, p.SubmittedAt,
            p.AppliedByEmail, p.AppliedAt, p.Version);
    }

    public async Task<ProductionResult<ProposalActionResponse>> SubmitProposalAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var p = await _db.BalanceProposals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return ProductionResult<ProposalActionResponse>.NotFound("proposal_not_found");
        if (p.Status != BalanceProposalStatus.Proposed)
            return ProductionResult<ProposalActionResponse>.Conflict("invalid_state", "Only a new proposal can be submitted for approval.");
        if (body.ExpectedVersion is { } v && v != p.Version)
            return ProductionResult<ProposalActionResponse>.Conflict("version_conflict", "The proposal changed since you loaded it. Reload it.");
        var detail = (await GetProposalAsync(id, ct))!;
        if (detail.InputsChanged)
            return ProductionResult<ProposalActionResponse>.Conflict("inputs_changed", "The style's operation bulletin changed after this proposal was computed. Run the analysis again.");

        var now = DateTime.UtcNow;
        var r = detail.Result.Proposed!;
        var cmp = detail.Comparison;
        p.ApprovalId = QueueApproval(_db, "line-balance", $"Rebalance {detail.LineId} for {detail.StyleCode} ({p.Reference})",
            $"Proposed: {r.Stations} workstations, cycle time {r.CycleTimeMinutes:0.###} min/piece, capacity {r.CapacityPerHour:0.#} pcs/h at 100 % of standard, " +
            $"balance efficiency {r.BalanceEfficiencyPct:0.#} %." +
            (cmp?.Current is { } c ? $" Current layout: cycle {c.CycleTimeMinutes:0.###} min, capacity {c.CapacityPerHour:0.#} pcs/h, balance efficiency {c.BalanceEfficiencyPct:0.#} %." : " No current layout is recorded to compare with.") +
            $" Method: {detail.Result.Method} Submitted by {actor.Email}." + (Clean(body.Reason) is { } note ? $" Note: {note}" : ""),
            "Approve to allow this workload distribution to be applied as the line's recorded layout. Applying it does not move people or machines: the supervisor re-arranges the line.",
            RiskTier.High, "balance_proposals", p.Id.ToString(), actor, now);
        p.Status = BalanceProposalStatus.PendingApproval;
        p.SubmittedAt = now;
        p.SubmittedByUserId = actor.UserId;
        p.SubmittedByEmail = actor.Email;
        _db.AuditLog.Add(Audit(actor, traceId, "balance_proposal.submitted", "balance_proposal", p.Id.ToString(), "proposed", "pending_approval", $"approval {p.ApprovalId}"));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ProductionResult<ProposalActionResponse>.Conflict("version_conflict", "The proposal changed since you loaded it. Reload it."); }
        return ProductionResult<ProposalActionResponse>.Ok(new ProposalActionResponse("submitted",
            "Submitted for approval (high risk: an Admin decides). Once approved, apply it here.", (await GetProposalAsync(id, ct))!));
    }

    public async Task<ProductionResult<ProposalActionResponse>> ApplyProposalAsync(Guid id, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var p = await _db.BalanceProposals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return ProductionResult<ProposalActionResponse>.NotFound("proposal_not_found");
        if (p.Status != BalanceProposalStatus.PendingApproval || p.ApprovalId is null)
            return ProductionResult<ProposalActionResponse>.Conflict("invalid_state", "Only a proposal waiting for approval can be applied.");
        var approval = await _db.Approvals.AsNoTracking().FirstAsync(a => a.Id == p.ApprovalId, ct);
        var now = DateTime.UtcNow;
        string outcome, message;
        switch (approval.Stage)
        {
            case Stage.PendingApproval:
                return ProductionResult<ProposalActionResponse>.Conflict("approval_pending", "The approval hasn't been decided yet.");
            case Stage.Rejected:
                p.Status = BalanceProposalStatus.Rejected;
                p.StatusReason = Truncate($"Rejected by {approval.DecidedByEmail}: {approval.DecisionReason}", 500);
                _db.AuditLog.Add(Audit(actor, traceId, "balance_proposal.rejected", "balance_proposal", p.Id.ToString(), "pending_approval", "rejected", p.StatusReason));
                await _db.SaveChangesAsync(ct);
                (outcome, message) = ("rejected", "The approval was rejected; the current layout is unchanged.");
                break;
            case Stage.Approved:
            {
                if (SeparationProblem(_options, approval, p.SubmittedByUserId, p.SubmittedByEmail) is { } sep)
                    return ProductionResult<ProposalActionResponse>.Forbidden("same_approver", sep);
                var detail = (await GetProposalAsync(id, ct))!;
                if (detail.InputsChanged || detail.BaselineChanged)
                {
                    p.Status = BalanceProposalStatus.Superseded;
                    p.StatusReason = detail.InputsChanged
                        ? "The operation bulletin changed after the analysis; run it again."
                        : "The line's recorded layout changed after the analysis; run it again.";
                    _db.AuditLog.Add(Audit(actor, traceId, "balance_proposal.superseded", "balance_proposal", p.Id.ToString(), "pending_approval", "superseded", p.StatusReason));
                    await _db.SaveChangesAsync(ct);
                    (outcome, message) = ("superseded", p.StatusReason);
                    break;
                }
                var layout = new LineLayout
                {
                    LineId = p.LineId, StyleId = p.StyleId, Origin = "proposal", ProposalId = p.Id, CreatedAt = now, CreatedByEmail = actor.Email,
                    Stations = detail.Result.ProposedStations.Select(s => new LineLayoutStation { StationNo = s.StationNo, OperationIds = s.OperationIds.ToArray() }).ToList(),
                };
                foreach (var st in layout.Stations) st.LayoutId = layout.Id;
                p.Status = BalanceProposalStatus.Applied;
                p.AppliedAt = now;
                p.AppliedByEmail = actor.Email;
                bool applied;
                try
                {
                    applied = await ReplaceCurrentLayoutAsync(layout, actor, traceId, "line_layout.applied_from_proposal",
                        $"{p.Reference} applied; approval {approval.Id} approved by {approval.DecidedByEmail}", ct,
                        () =>
                        {
                            _db.AuditLog.Add(Audit(actor, traceId, "balance_proposal.applied", "balance_proposal", p.Id.ToString(), "pending_approval", "applied",
                                $"layout {layout.Id} · approval {approval.Id}"));
                            return Task.CompletedTask;
                        }, requireBaseline: true, expectedBaseline: p.BaselineLayoutId);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return ProductionResult<ProposalActionResponse>.Conflict("version_conflict", "The proposal changed while it was being applied. Reload it.");
                }
                if (!applied)
                    return ProductionResult<ProposalActionResponse>.Conflict("baseline_changed", "The line's recorded layout changed while applying. Reload the proposal.");
                (outcome, message) = ("applied", "Recorded as the line's current layout. Re-arranging operators and machines on the floor is the supervisor's job; nothing was sent to people or machines.");
                break;
            }
            default:
                return ProductionResult<ProposalActionResponse>.Conflict("approval_unusable", $"The approval is '{StageNames.ToWire(approval.Stage)}'.");
        }
        _db.ChangeTracker.Clear();
        return ProductionResult<ProposalActionResponse>.Ok(new ProposalActionResponse(outcome, message, (await GetProposalAsync(id, ct))!));
    }

    /// <summary>Hash of the inputs a proposal depends on: every operation's id, sequence, SMV, machine type and predecessors.</summary>
    internal static string InputHash(IEnumerable<StyleOperation> ops)
    {
        var text = string.Join("\n", ops.OrderBy(o => o.Sequence).Select(o =>
            $"{o.Id}|{o.Sequence}|{o.Smv:R}|{o.MachineType?.Trim().ToUpperInvariant()}|{string.Join(",", o.Predecessors.OrderBy(x => x))}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
