using BunonBrain.Application.Analytics;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Production;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// Planned output and planned operators per line, from the production
/// module's own plan schedule (plant calendar, target rate or the planned
/// quantity spread over scheduled minutes) — the same numbers the
/// production pages show. Only active and completed plans count.
/// </summary>
public sealed class PlanTargets
{
    public sealed record LineTarget(string LineId, double Planned, int Plans, IReadOnlyDictionary<DateOnly, double> PlannedByDay,
        IReadOnlyDictionary<DateOnly, int> OperatorsByDay);

    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public PlanTargets(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    public async Task<IReadOnlyDictionary<string, LineTarget>> ForLinesAsync(IReadOnlyCollection<string> lineIds, AnalyticsPeriod period, DateTimeOffset now,
        CancellationToken ct)
    {
        if (lineIds.Count == 0) return new Dictionary<string, LineTarget>();
        var plans = await _db.ProductionPlans.AsNoTracking()
            .Where(p => lineIds.Contains(p.LineId) && (p.Status == ProductionPlanStatus.Active || p.Status == ProductionPlanStatus.Completed)
                        && p.StartDate <= period.To && p.EndDate >= period.From)
            .ToListAsync(ct);
        if (plans.Count == 0) return new Dictionary<string, LineTarget>();
        var read = new ProductionReadModel(_db, _options);
        var cal = await read.CalendarAsync(ct);
        var orderIds = plans.Select(p => p.OrderId).Distinct().ToList();
        var styleOf = await _db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.StyleId, ct);
        var smv = await read.StyleSmvAsync(styleOf.Values, ct);
        var result = new Dictionary<string, LineTarget>();
        foreach (var g in plans.GroupBy(p => p.LineId))
        {
            var byDay = new Dictionary<DateOnly, double>();
            var ops = new Dictionary<DateOnly, int>();
            double total = 0;
            foreach (var p in g)
            {
                var s = styleOf.TryGetValue(p.OrderId, out var style) && smv.TryGetValue(style, out var v) ? v.Smv : null;
                var schedule = ProductionReadModel.Schedule(p, cal, s);
                total += schedule.PlannedBetween(period.FromUtc.UtcDateTime, period.ToUtc.UtcDateTime);
                foreach (var d in period.Dates())
                {
                    var (a, b) = period.Day(d, cal.TimeZone, now);
                    var planned = schedule.PlannedBetween(a.UtcDateTime, b.UtcDateTime);
                    if (planned > 0) byDay[d] = byDay.GetValueOrDefault(d) + planned;
                    if (p.StartDate <= d && p.EndDate >= d) ops[d] = ops.GetValueOrDefault(d) + p.Operators;
                }
            }
            result[g.Key] = new LineTarget(g.Key, Math.Floor(total + 1e-9), g.Count(), byDay, ops);
        }
        return result;
    }
}
