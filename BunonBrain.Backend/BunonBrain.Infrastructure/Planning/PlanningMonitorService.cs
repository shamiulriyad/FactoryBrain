using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Iot;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Planning.PlanningScope;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>An order's output as the published plans attribute it, with how it was measured.</summary>
internal sealed record OrderOutput(Guid OrderId, int? Produced, string Basis, IReadOnlyList<(DateOnly Date, int Pieces)> CompletedDays, IReadOnlyList<ItemEvaluation> Items);

internal static class OrderOutputs
{
    public const int LookbackDays = 120;

    /// <summary>
    /// Output per order from the published plans' items (the only place output is tied to an order and a shift).
    /// Counter output at a line's output counter is verified; output recorded by people is used only where the line has no counter.
    /// Output outside any published plan is unattributed and not counted here.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, OrderOutput>> ForOrdersAsync(BunonBrainDbContext db, Guid factoryId, IReadOnlyCollection<Guid> orderIds, FactoryClock clock, string source,
        DateTimeOffset now, CancellationToken ct)
    {
        var result = new Dictionary<Guid, OrderOutput>();
        if (orderIds.Count == 0) return result;
        var today = clock.Today(now);
        var since = today.AddDays(-LookbackDays);
        var items = await (from i in db.DailyPlanItems.AsNoTracking()
                           join p in db.DailyProductionPlans.AsNoTracking() on i.PlanId equals p.Id
                           where p.Status == PlanStatuses.Published && p.FactoryId == factoryId && orderIds.Contains(i.OrderId) && i.PlanDate <= today && i.PlanDate >= since
                           select i).ToListAsync(ct);
        var evals = await new PlanProgressCalculator(db).EvaluateAsync(factoryId, items, clock, source, now, ct);
        foreach (var g in evals.GroupBy(e => e.Item.OrderId))
        {
            var withData = g.Where(e => e.Progress.Actual is not null).ToList();
            var produced = withData.Count == 0 ? (int?)null : withData.Sum(e => e.Progress.Actual!.Value);
            var bases = withData.Select(e => e.Progress.Basis).Distinct().ToList();
            var basis = bases.Count == 0 ? ActualBases.None : bases.Count == 1 ? bases[0] : "MIXED";
            var days = withData.Where(e => e.Item.PlanDate < today).GroupBy(e => e.Item.PlanDate).Select(d => (d.Key, d.Sum(x => x.Progress.Actual!.Value))).OrderBy(d => d.Key).ToList();
            result[g.Key] = new OrderOutput(g.Key, produced, basis, days, g.ToList());
        }
        return result;
    }
}

/// <summary>
/// The planning dashboard: today's published plan against verified output, order progress and
/// delivery forecasts, capacity and exception warnings. Every figure comes from stored records;
/// where evidence is missing the figure is empty and says why.
/// </summary>
public sealed class PlanningMonitorService : IPlanningMonitorService
{
    private readonly BunonBrainDbContext _db;

    public PlanningMonitorService(BunonBrainDbContext db) { _db = db; }

    public async Task<AccessResult<MonitorDto>> MonitorAsync(AccessContext caller, MonitorQuery q, CancellationToken ct)
    {
        var fr = await PlanningSetupService.ResolveFactoryAsync(_db, caller, q.FactoryId, ct);
        if (fr.Outcome != AccessOutcome.Ok) return new AccessResult<MonitorDto>(fr.Outcome, null, fr.Code, fr.Message);
        var factory = fr.Value!;
        var source = IotSourceTypes.Parse(q.Source ?? IotSourceTypes.Real);
        if (source is null) return AccessResult<MonitorDto>.Invalid("source_invalid", "source must be REAL or SIMULATED.");
        var clock = await FactoryClock.LoadAsync(_db, factory.Id, ct);
        var now = DateTimeOffset.UtcNow;
        var today = clock.Today(now);
        var date = q.Date ?? today;
        if (date > today.AddDays(30) || date < today.AddDays(-365)) return AccessResult<MonitorDto>.Invalid("date_invalid", "Choose a date within the last year or the next 30 days.");

        // Lines in scope: the caller's lines, narrowed by the department / floor / line filters.
        var linesQ = _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factory.Id);
        if (caller.IsLineScoped) { var ls = caller.LineIds.ToList(); linesQ = linesQ.Where(l => ls.Contains(l.Id)); }
        if (Clean(q.LineId) is { } lf) linesQ = linesQ.Where(l => l.Id == lf);
        var lines = await linesQ.OrderBy(l => l.Name).ToListAsync(ct);
        var placements = await PlanningSetupService.PlacementsAsync(_db, lines.Select(l => l.Id).ToList(), ct);
        if (q.DepartmentId is { } dep) lines = lines.Where(l => placements.GetValueOrDefault(l.Id)?.DepartmentId == dep).ToList();
        if (q.FloorId is { } fl) lines = lines.Where(l => placements.GetValueOrDefault(l.Id)?.FloorId == fl).ToList();
        var lineIds = lines.Select(l => l.Id).ToHashSet();
        var lineNames = lines.ToDictionary(l => l.Id, l => l.Name);

        // The plan in force for the date.
        var plan = await _db.DailyProductionPlans.AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.FactoryId == factory.Id && p.PlanDate == date && p.Status == PlanStatuses.Published, ct);
        var items = plan?.Items.Where(i => lineIds.Contains(i.LineId)).ToList() ?? new List<DailyPlanItem>();
        var calc = new PlanProgressCalculator(_db);
        var evals = await calc.EvaluateAsync(factory.Id, items, clock, source, now, ct);

        var orderIds = evals.Select(e => e.Item.OrderId).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => o.FactoryId == factory.Id && (o.Status == ProductionOrderStatus.Planned || o.Status == ProductionOrderStatus.InProgress || orderIds.Contains(o.Id))).ToListAsync(ct);
        var orderOutputs = await OrderOutputs.ForOrdersAsync(_db, factory.Id, orders.Select(o => o.Id).ToList(), clock, source, now, ct);
        var styleIds = orders.Select(o => o.StyleId).Distinct().ToList();
        var styles = await _db.ProductionStyles.AsNoTracking().Where(s => styleIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code, ct);
        var allocatedBy = await _db.ProductionAllocations.AsNoTracking().Where(a => a.FactoryId == factory.Id && a.Status != AllocationStatuses.Cancelled)
            .GroupBy(a => a.OrderId).Select(g => new { g.Key, N = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var orderRows = orders.Where(o => OrderLifecycle.IsLive(o.Status)).Select(o => OrderRow(o, styles, allocatedBy, orderOutputs, clock, today)).OrderBy(r => r.DaysToDeadline ?? int.MaxValue).ToList();
        var upcoming = orderRows.Where(r => r.DaysToDeadline is { } d && d <= 14).OrderBy(r => r.DaysToDeadline).ToList();

        // Items, lines, shifts.
        var orderNumbers = orders.ToDictionary(o => o.Id, o => o.OrderNumber);
        var shiftNames = clock.Shifts.ToDictionary(s => s.Id, s => s.Name);
        var itemRows = evals.Select(e =>
        {
            var pl = placements.GetValueOrDefault(e.Item.LineId);
            var shift = clock.Shift(e.Item.ShiftId);
            return new MonitorItemRow(e.Item.Id, e.Item.OrderId, orderNumbers.GetValueOrDefault(e.Item.OrderId, "?"), e.Item.LineId, lineNames.GetValueOrDefault(e.Item.LineId, e.Item.LineId),
                pl?.FloorId, pl?.FloorName, pl?.DepartmentId, pl?.DepartmentName, e.Item.ShiftId, shiftNames.GetValueOrDefault(e.Item.ShiftId, "?"), e.Item.TargetQuantity, e.Dto,
                shift is null ? null : PlanMath.HourlyTargetOf(e.Item.TargetQuantity, shift.ProductiveMinutes));
        }).ToList();

        var lineRows = evals.GroupBy(e => e.Item.LineId).Select(g =>
        {
            var target = g.Sum(e => e.Item.TargetQuantity);
            var withData = g.Where(e => e.Progress.Actual is not null).ToList();
            int? actual = withData.Count == 0 ? null : withData.Sum(e => e.Progress.Actual!.Value);
            var bases = withData.Select(e => e.Progress.Basis).Distinct().ToList();
            var pl = placements.GetValueOrDefault(g.Key);
            return new MonitorLineRow(g.Key, lineNames.GetValueOrDefault(g.Key, g.Key), pl?.FloorId, pl?.FloorName, pl?.DepartmentId, pl?.DepartmentName, target, actual,
                actual is null ? null : PlanMath.Pct(actual.Value, target), bases.Count == 0 ? ActualBases.None : bases.Count == 1 ? bases[0] : "MIXED",
                g.Any(e => e.LineHasOutputPoint), AggregateState(g.Select(e => e.Progress.State)), g.Select(e => e.Item.Id).ToList());
        }).OrderBy(r => r.LineName).ToList();

        var shiftRows = evals.GroupBy(e => e.Item.ShiftId).Select(g =>
        {
            var withData = g.Where(e => e.Progress.Actual is not null).ToList();
            int? actual = withData.Count == 0 ? null : withData.Sum(e => e.Progress.Actual!.Value);
            var target = g.Sum(e => e.Item.TargetQuantity);
            return new MonitorShiftRow(g.Key, shiftNames.GetValueOrDefault(g.Key, "?"), target, actual, actual is null ? null : PlanMath.Pct(actual.Value, target),
                AggregateState(g.Select(e => e.Progress.State)), g.Count());
        }).OrderBy(r => clock.Shift(r.ShiftId)?.Start).ToList();

        // Hourly verified output at the lines' output counters.
        var hourly = await HourlyAsync(factory.Id, date, source, lineIds, clock, now, ct);

        var exceptions = await ExceptionService.QueryAsync(_db, caller, factory.Id, source, lineIds, ct);
        var allocs = await Allocations(_db, caller).Where(a => a.FactoryId == factory.Id && (a.Status == AllocationStatuses.Planned || a.Status == AllocationStatuses.Active)
                                                              && a.PlannedStart <= date && a.PlannedEnd >= date && (lineIds.Contains(a.LineId))).OrderBy(a => a.LineId).ToListAsync(ct);
        var allocDtos = await AllocationService.MapAsync(_db, allocs, ct);

        var points = await calc.Actuals.PointsAsync(factory.Id, source, ct);
        var withActual = evals.Where(e => e.Progress.Actual is not null).ToList();
        var targetToday = evals.Sum(e => e.Item.TargetQuantity);
        int? actualToday = withActual.Count == 0 ? null : withActual.Sum(e => e.Progress.Actual!.Value);
        // Remaining counts only items with evidence (unknown output isn't "all remaining").
        int? remaining = withActual.Count == 0 ? null : withActual.Sum(e => Math.Max(0, e.Item.TargetQuantity - e.Progress.Actual!.Value)) + evals.Where(e => e.Progress.Actual is null).Sum(e => e.Item.TargetQuantity);
        var summary = new MonitorSummary(orderRows.Count, orderRows.Count(r => r.ForecastStatus is OrderForecasts.AtRisk or OrderForecasts.Overdue),
            orderRows.Count(r => r.DaysToDeadline is >= 0 and <= 7), targetToday, actualToday, actualToday is null ? null : PlanMath.Pct(actualToday.Value, withActual.Sum(e => e.Item.TargetQuantity)),
            remaining, exceptions.Count(e => e.Status != ExceptionStatuses.Resolved), exceptions.Count(e => e.Type == ExceptionTypes.CapacityShortage && e.Status != ExceptionStatuses.Resolved),
            lineRows.Count, lineRows.Count(r => !r.OutputCounterConfigured));

        var warnings = new List<string>();
        if (plan is null) warnings.Add(date < today ? "No plan was published for this date." : $"No plan is published for {Date(date)} yet: targets, actual output and delays can't be compared.");
        if (lineRows.Any(r => !r.OutputCounterConfigured)) warnings.Add($"{lineRows.Count(r => !r.OutputCounterConfigured)} planned line(s) have no output counter configured: their output can't be verified by a counter (set one in Planning setup).");
        if (source == IotSourceTypes.Simulated) warnings.Add("SIMULATED data: not real production. Manual output records are ignored.");
        if (lines.Count == 0) warnings.Add("No production line is in scope for these filters.");
        var definitions = new[]
        {
            "Actual (verified): pieces counted by the counter devices designated as a line's output, plus audited corrections, in the item's shift. Each message is stored once, so duplicates and counter resets are not double counted.",
            "Actual (recorded): output entered by people in the production output records; used only for lines without an output counter, and never called verified.",
            "No data is not zero: where a line has no counter evidence the actual is empty.",
            "Expected by now: the shift target spread evenly over the shift's productive minutes (breaks excluded). A guide, not a promise.",
            "AI activity recognition is never production: only counters (and people's records) count.",
            "Order completion counts output in shifts that a published plan assigned to the order; output outside published plans isn't attributed to any order.",
        };
        return AccessResult<MonitorDto>.Ok(new MonitorDto(factory.Id, factory.Name, Date(date), clock.ZoneId, source, now, summary, plan?.Status, plan?.Reference, plan?.RevisionNo, orderRows, upcoming,
            itemRows, lineRows, shiftRows, hourly, exceptions, allocDtos, warnings, definitions));
    }

    public async Task<AccessResult<OrderProgressDto>> OrderProgressAsync(AccessContext caller, Guid orderId, string? source, CancellationToken ct)
    {
        var src = IotSourceTypes.Parse(source ?? IotSourceTypes.Real);
        if (src is null) return AccessResult<OrderProgressDto>.Invalid("source_invalid", "source must be REAL or SIMULATED.");
        var o = await PlanningScope.Orders(_db, caller).FirstOrDefaultAsync(x => x.Id == orderId, ct);
        if (o is null) return AccessResult<OrderProgressDto>.NotFound("order_not_found", "Order not found in your scope.");
        if (o.FactoryId is not { } fid) return AccessResult<OrderProgressDto>.Conflict("order_without_factory", "The order has no factory yet, so its production can't be tracked.");
        var clock = await FactoryClock.LoadAsync(_db, fid, ct);
        var now = DateTimeOffset.UtcNow;
        var today = clock.Today(now);
        var outputs = await OrderOutputs.ForOrdersAsync(_db, fid, new[] { orderId }, clock, src, now, ct);
        var styleCode = await _db.ProductionStyles.AsNoTracking().Where(s => s.Id == o.StyleId).Select(s => s.Code).FirstAsync(ct);
        var allocated = await _db.ProductionAllocations.AsNoTracking().Where(a => a.OrderId == orderId && a.Status != AllocationStatuses.Cancelled).SumAsync(a => (int?)a.Quantity, ct) ?? 0;
        var row = OrderRow(o, new Dictionary<Guid, string> { [o.StyleId] = styleCode }, new Dictionary<Guid, int> { [orderId] = allocated }, outputs, clock, today);
        var forecast = ForecastOf(o, outputs.GetValueOrDefault(orderId), clock, today);
        var allocs = await Allocations(_db, caller).Where(a => a.OrderId == orderId).OrderBy(a => a.PlannedStart).ToListAsync(ct);
        var days = outputs.TryGetValue(orderId, out var oo)
            ? oo.Items.GroupBy(e => e.Item.PlanDate).OrderBy(g => g.Key).Select(g =>
            {
                var target = g.Sum(e => e.Item.TargetQuantity);
                var data = g.Where(e => e.Progress.Actual is not null).ToList();
                int? actual = data.Count == 0 ? null : data.Sum(e => e.Progress.Actual!.Value);
                var bases = data.Select(e => e.Progress.Basis).Distinct().ToList();
                return new OrderDayRow(Date(g.Key), target, actual, bases.Count == 0 ? ActualBases.None : bases.Count == 1 ? bases[0] : "MIXED", actual is null ? null : PlanMath.Pct(actual.Value, target));
            }).ToList()
            : new List<OrderDayRow>();
        var warnings = new List<string>();
        if (outputs.GetValueOrDefault(orderId) is null) warnings.Add("No published plan has scheduled this order yet, so its output can't be attributed.");
        else if (outputs[orderId].Produced is null) warnings.Add("The scheduled shifts have no output evidence (no counter data and no recorded output).");
        warnings.Add("Output counts only in shifts a published plan assigned to this order.");
        return AccessResult<OrderProgressDto>.Ok(new OrderProgressDto(row, await AllocationService.MapAsync(_db, allocs, ct), days, forecast, warnings));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    internal static OrderForecast ForecastOf(ProductionOrder o, OrderOutput? output, FactoryClock clock, DateOnly today)
    {
        var produced = output?.Produced ?? 0;
        var basis = output?.Basis ?? ActualBases.None;
        if (output?.Produced is null && o.Status != ProductionOrderStatus.Completed)
            return o.DueDate < today
                ? new OrderForecast(OrderForecasts.Overdue, o.Quantity, null, 0, null, 0, null, true, basis,
                    $"The delivery deadline ({Date(o.DueDate)}) has passed and no output is attributed to this order (no verified or recorded output in the published plans).")
                : new OrderForecast(OrderForecasts.Insufficient, o.Quantity, null, 0, null, 0, null, null, basis, "No verified or recorded output is attributed to this order yet, so completion can't be forecast.");
        return OrderForecasts.Forecast(o.Quantity, produced, output?.CompletedDays ?? Array.Empty<(DateOnly, int)>(), today, o.DueDate, clock.IsWorkingDay, basis);
    }

    private static MonitorOrderRow OrderRow(ProductionOrder o, IReadOnlyDictionary<Guid, string> styles, IReadOnlyDictionary<Guid, int> allocated, IReadOnlyDictionary<Guid, OrderOutput> outputs,
        FactoryClock clock, DateOnly today)
    {
        var output = outputs.GetValueOrDefault(o.Id);
        var fc = ForecastOf(o, output, clock, today);
        return new MonitorOrderRow(o.Id, o.OrderNumber, o.Buyer, styles.GetValueOrDefault(o.StyleId), ProductionWire.OrderStatus(o.Status), o.Priority, o.Quantity, Date(o.DueDate),
            o.DueDate.DayNumber - today.DayNumber, allocated.GetValueOrDefault(o.Id), output?.Produced, output?.Produced is { } p ? PlanMath.Pct(p, o.Quantity) : null,
            output?.Basis ?? ActualBases.None, fc.Status, fc.ProjectedCompletion is { } d ? Date(d) : null, fc.Note);
    }

    private static string AggregateState(IEnumerable<string> states)
    {
        var s = states.ToList();
        if (s.Count == 0) return ItemStates.NotStarted;
        if (s.Any(x => x is ItemStates.Behind or ItemStates.Missed)) return ItemStates.Behind;
        if (s.All(x => x == ItemStates.Complete)) return ItemStates.Complete;
        if (s.All(x => x == ItemStates.NotStarted)) return ItemStates.NotStarted;
        if (s.Any(x => x == ItemStates.NoData)) return ItemStates.NoData;
        return s.Any(x => x == ItemStates.Ahead) && s.All(x => x is ItemStates.Ahead or ItemStates.Complete) ? ItemStates.Ahead : ItemStates.OnTrack;
    }

    private async Task<IReadOnlyList<MonitorHourPoint>> HourlyAsync(Guid factoryId, DateOnly date, string source, HashSet<string> lineIds, FactoryClock clock, DateTimeOffset now, CancellationToken ct)
    {
        var actuals = new PlanningActuals(_db);
        var points = await actuals.PointsAsync(factoryId, source, ct);
        var lines = points.Keys.Where(lineIds.Contains).ToList();
        if (lines.Count == 0 || date > clock.Today(now)) return Array.Empty<MonitorHourPoint>();
        var (start, end) = clock.DayWindow(date);
        var events = await (from e in _db.ProductionCounterEvents.AsNoTracking()
                            join p in _db.LineOutputPoints.AsNoTracking() on new { e.DeviceId, e.LineId } equals new { p.DeviceId, p.LineId }
                            where p.IsActive && e.FactoryId == factoryId && e.SourceType == source && e.OccurredAt >= start && e.OccurredAt < end && e.Pieces > 0 && lines.Contains(e.LineId)
                            select new { e.OccurredAt, e.Pieces }).ToListAsync(ct);
        var buckets = new SortedDictionary<DateTimeOffset, int>();
        foreach (var e in events)
        {
            var u = e.OccurredAt.ToUniversalTime();
            var local = TimeZoneInfo.ConvertTime(u, clock.Zone);
            // Bucket by local hour start, as an instant.
            var h = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).ToUniversalTime();
            buckets[h] = buckets.GetValueOrDefault(h) + e.Pieces;
        }
        return buckets.Select(b => new MonitorHourPoint(b.Key.ToString("O"), TimeZoneInfo.ConvertTime(b.Key, clock.Zone).ToString("HH:00", System.Globalization.CultureInfo.InvariantCulture), b.Value)).ToList();
    }
}
