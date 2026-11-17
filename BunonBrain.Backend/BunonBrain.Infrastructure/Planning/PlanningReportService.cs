using System.Text;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Analytics;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Planning.PlanningScope;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>
/// Plan-versus-actual reports. They are built as the analytics module's report documents, so they
/// carry the same header (scope, period, generation time, data source, filters, caveats, definitions)
/// and export through the same CSV and PDF writers. Reading needs the planning view permission,
/// exporting needs the export permission, and every generation is limited to the caller's
/// factories (supervisors: their lines). Exports are audited.
/// </summary>
public sealed class PlanningReportService : IPlanningReports
{
    public const int MaxDays = 62;

    public static readonly IReadOnlyList<PlanningReportTypeDto> TypeList = new[]
    {
        new PlanningReportTypeDto("order-production", "Order-wise production report", "Quantity, allocation, verified output and completion of each order."),
        new PlanningReportTypeDto("line-target-achievement", "Line-wise target achievement", "Published target against output for each line."),
        new PlanningReportTypeDto("daily-plan-vs-actual", "Daily plan versus actual", "Each day's published plan against output."),
        new PlanningReportTypeDto("shift-plan-vs-actual", "Shift-wise plan versus actual", "Each shift's published targets against output."),
        new PlanningReportTypeDto("delivery-progress", "Delivery progress", "Remaining quantity, deadline and forecast for each live order."),
        new PlanningReportTypeDto("capacity-utilization", "Capacity utilisation", "Estimated capacity against allocations per line (an estimate; assumptions shown)."),
        new PlanningReportTypeDto("production-delay", "Production delay report", "Exceptions detected in the period: output behind target, delivery risk, interruptions."),
        new PlanningReportTypeDto("target-revisions", "Historical target revisions", "Every creation, revision, approval and ending of targets."),
    };

    private readonly BunonBrainDbContext _db;

    public PlanningReportService(BunonBrainDbContext db) { _db = db; }

    public IReadOnlyList<PlanningReportTypeDto> Types() => TypeList;

    public async Task<AccessResult<ReportDocument>> GenerateAsync(AccessContext caller, string type, Guid factoryId, DateOnly? from, DateOnly? to, string? source, string? lineId, Guid? orderId, CancellationToken ct)
    {
        var t = TypeList.FirstOrDefault(x => string.Equals(x.Type, type?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (t is null) return AccessResult<ReportDocument>.NotFound("report_type_not_found", $"Report type: one of {string.Join(", ", TypeList.Select(x => x.Type))}.");
        var src = IotSourceTypes.Parse(source ?? IotSourceTypes.Real);
        if (src is null) return AccessResult<ReportDocument>.Invalid("source_invalid", "source must be REAL or SIMULATED.");
        var fr = await PlanningSetupService.ResolveFactoryAsync(_db, caller, factoryId, ct);
        if (fr.Outcome != AccessOutcome.Ok) return new AccessResult<ReportDocument>(fr.Outcome, null, fr.Code, fr.Message);
        var factory = fr.Value!;
        var clock = await FactoryClock.LoadAsync(_db, factory.Id, ct);
        var now = DateTimeOffset.UtcNow;
        var today = clock.Today(now);
        var end = to ?? today;
        var start = from ?? end.AddDays(-6);
        if (end < start) return AccessResult<ReportDocument>.Invalid("invalid_period", "The end date can't be before the start date.");
        var warnings = new List<string>();
        if (end.DayNumber - start.DayNumber + 1 > MaxDays) { start = end.AddDays(-(MaxDays - 1)); warnings.Add($"The range was shortened to the last {MaxDays} days."); }
        var filters = new List<ReportFilter>();
        if (Clean(lineId) is { } lid)
        {
            var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lid && l.FactoryId == factory.Id, ct);
            if (line is null || !caller.CanAccessLine(line.Id, line.FactoryId)) return AccessResult<ReportDocument>.NotFound("line_not_found", "Line not found in your scope.");
            filters.Add(new ReportFilter("Line", line.Name));
        }
        else if (caller.IsLineScoped) { lineId = null; filters.Add(new ReportFilter("Lines", string.Join(", ", caller.LineIds.OrderBy(x => x)))); }
        if (orderId is { } oid)
        {
            var o = await PlanningScope.Orders(_db, caller).FirstOrDefaultAsync(x => x.Id == oid, ct);
            if (o is null) return AccessResult<ReportDocument>.NotFound("order_not_found", "Order not found in your scope.");
            filters.Add(new ReportFilter("Order", o.OrderNumber));
        }
        if (src == IotSourceTypes.Simulated) warnings.Add("SIMULATED data: not real production.");

        var sections = new List<ReportSection>();
        var ctx = new Ctx(_db, caller, factory, clock, now, today, start, end, src, Clean(lineId), orderId);
        switch (t.Type)
        {
            case "order-production": sections.Add(await OrderProductionAsync(ctx, ct)); break;
            case "line-target-achievement": sections.Add(await LineAchievementAsync(ctx, ct)); break;
            case "daily-plan-vs-actual": sections.Add(await DailyAsync(ctx, ct)); break;
            case "shift-plan-vs-actual": sections.Add(await ShiftAsync(ctx, ct)); break;
            case "delivery-progress": sections.Add(await DeliveryAsync(ctx, ct)); break;
            case "capacity-utilization": sections.Add(await CapacityAsync(ctx, ct)); break;
            case "production-delay": sections.Add(await DelayAsync(ctx, ct)); break;
            default: sections.Add(await RevisionsAsync(ctx, ct)); break;
        }
        warnings.Add("Output is verified only where a line's output counter is configured; RECORDED rows come from people's records. Where there is no evidence the cell is empty, not zero.");
        var scope = $"{factory.Code} · {factory.Name}" + (caller.IsLineScoped ? $" · your lines ({string.Join(", ", caller.LineIds.OrderBy(x => x))})" : "");
        return AccessResult<ReportDocument>.Ok(new ReportDocument(t.Type, t.Title, now, caller.Email, scope, start, end, clock.ZoneId, src, filters, sections, warnings, Definitions));
    }

    public async Task<AccessResult<ReportFile>> ExportAsync(AccessContext caller, string type, string format, Guid factoryId, DateOnly? from, DateOnly? to, string? source, string? lineId, Guid? orderId,
        string? traceId, CancellationToken ct)
    {
        var fmt = format?.Trim().ToLowerInvariant();
        if (fmt is not ("csv" or "pdf")) return AccessResult<ReportFile>.Invalid("format_invalid", "Format: csv or pdf.");
        var r = await GenerateAsync(caller, type, factoryId, from, to, source, lineId, orderId, ct);
        if (r.Outcome != AccessOutcome.Ok) return new AccessResult<ReportFile>(r.Outcome, null, r.Code, r.Message);
        var doc = r.Value!;
        var file = fmt == "csv"
            ? new ReportFile(ReportCsv.FileName(doc, "csv"), "text/csv; charset=utf-8", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ReportCsv.Write(doc))).ToArray())
            : new ReportFile(ReportCsv.FileName(doc, "pdf"), "application/pdf", PdfReportWriter.Write(doc));
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.report_exported", "planning_report", doc.Type, null, doc.Source,
            $"{doc.Title} exported as {fmt.ToUpperInvariant()}: {doc.Scope}, {doc.From:yyyy-MM-dd}..{doc.To:yyyy-MM-dd}, {doc.Source}", traceId, factoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<ReportFile>.Ok(file);
    }

    private sealed record Ctx(BunonBrainDbContext Db, AccessContext Caller, Factory Factory, FactoryClock Clock, DateTimeOffset Now, DateOnly Today, DateOnly From, DateOnly To,
        string Source, string? LineId, Guid? OrderId);

    private static readonly IReadOnlyList<MetricDefinition> Definitions = new[]
    {
        new MetricDefinition("planning.target", "Target", "DERIVED", "pieces", new[] { "Published daily plans" }, "Sum of the published plan items' target quantities",
            "Dates in the period", "Items of published plans (the revision in force)", "Drafts, cancelled and replaced revisions", null, "No published plan: no target."),
        new MetricDefinition("planning.actual_verified", "Actual (verified)", "MEASURED", "pieces", new[] { "IoT production counters", "Production corrections" },
            "Pieces counted by the counters designated as the line's output, in the item's shift, plus audited corrections", "Dates in the period",
            "Counter messages stored once per device and message id; counter resets handled as in the production counter rules", "Other machine counters, AI activity, simulated data in a REAL report",
            null, "No output counter or no data: the cell is empty, never zero."),
        new MetricDefinition("planning.actual_recorded", "Actual (recorded)", "MEASURED", "pieces", new[] { "Production output records" },
            "Good pieces recorded by people for the order on the line, date and shift", "Dates in the period", "Lines without an output counter only", "Superseded records", null, "No record: empty."),
        new MetricDefinition("planning.achievement", "Target achievement", "DERIVED", "%", new[] { "Published plan", "Counters" }, "actual ÷ target × 100", "Dates in the period",
            "Items with evidence of output", "Items without evidence", "Target", "Unavailable without a target or evidence."),
        new MetricDefinition("planning.completion", "Order completion", "DERIVED", "%", new[] { "Published plan", "Counters" }, "output in shifts a published plan assigned to the order ÷ order quantity × 100", "All dates with a published plan",
            "Shifts scheduled for the order in published plans", "Output outside published plans", "Order quantity", "Unavailable until a published plan schedules the order and output exists."),
        new MetricDefinition("planning.capacity", "Estimated capacity", "AI_ESTIMATE", "pieces", new[] { "Shifts", "Style SMV", "Line capacity settings", "Workstation assignments" },
            CapacityEstimator.Formula, "Dates in the period", "Configured inputs only", "Anything not configured", null, "Incomplete: no number is shown until SMV, efficiency and operators are configured."),
    };

    // ── Sections ────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<DailyPlanItem>> PublishedItemsAsync(Ctx c, CancellationToken ct)
    {
        var q = from i in c.Db.DailyPlanItems.AsNoTracking()
                join p in c.Db.DailyProductionPlans.AsNoTracking() on i.PlanId equals p.Id
                where p.Status == PlanStatuses.Published && p.FactoryId == c.Factory.Id && i.PlanDate >= c.From && i.PlanDate <= c.To
                select i;
        if (c.Caller.IsLineScoped) { var ls = c.Caller.LineIds.ToList(); q = q.Where(i => ls.Contains(i.LineId)); }
        if (c.LineId is { } l) q = q.Where(i => i.LineId == l);
        if (c.OrderId is { } o) q = q.Where(i => i.OrderId == o);
        return await q.ToListAsync(ct);
    }

    private static async Task<IReadOnlyList<ItemEvaluation>> EvaluateAsync(Ctx c, IReadOnlyList<DailyPlanItem> items, CancellationToken ct) =>
        await new PlanProgressCalculator(c.Db).EvaluateAsync(c.Factory.Id, items, c.Clock, c.Source, c.Now, ct);

    private static object? Basis(IEnumerable<ItemEvaluation> g)
    {
        var b = g.Where(e => e.Progress.Actual is not null).Select(e => e.Progress.Basis).Distinct().ToList();
        return b.Count == 0 ? ActualBases.None : b.Count == 1 ? b[0] : "MIXED";
    }

    private static int? Actual(IEnumerable<ItemEvaluation> g)
    {
        var d = g.Where(e => e.Progress.Actual is not null).ToList();
        return d.Count == 0 ? null : d.Sum(e => e.Progress.Actual!.Value);
    }

    private static async Task<ReportSection> OrderProductionAsync(Ctx c, CancellationToken ct)
    {
        var orders = await PlanningScope.Orders(c.Db, c.Caller).Where(o => o.FactoryId == c.Factory.Id && o.Status != ProductionOrderStatus.Draft && (c.OrderId == null || o.Id == c.OrderId)).OrderBy(o => o.DueDate).Take(500).ToListAsync(ct);
        var outputs = await OrderOutputs.ForOrdersAsync(c.Db, c.Factory.Id, orders.Select(o => o.Id).ToList(), c.Clock, c.Source, c.Now, ct);
        var alloc = await c.Db.ProductionAllocations.AsNoTracking().Where(a => a.FactoryId == c.Factory.Id && a.Status != AllocationStatuses.Cancelled).GroupBy(a => a.OrderId)
            .Select(g => new { g.Key, N = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var rows = orders.Select(o =>
        {
            var out1 = outputs.GetValueOrDefault(o.Id);
            return (IReadOnlyList<object?>)new object?[] { o.OrderNumber, o.Buyer, ProductionWire.OrderStatus(o.Status), o.Priority, o.Quantity, alloc.GetValueOrDefault(o.Id), out1?.Produced,
                out1?.Produced is { } p ? PlanMath.Pct(p, o.Quantity) : null, out1?.Basis ?? ActualBases.None, o.DueDate };
        }).ToList();
        return new ReportSection("Order-wise production", Array.Empty<Kpi>(), new[]
        {
            new ReportTable("Orders", new[] { "Order", "Buyer", "Status", "Priority", "Quantity", "Allocated", "Output", "Completion %", "Basis", "Delivery deadline" }, rows,
                "Output counts only shifts a published plan assigned to the order. Basis: COUNTER = verified, RECORDED = people's records, MIXED, NONE = no evidence."),
        });
    }

    private static async Task<ReportSection> LineAchievementAsync(Ctx c, CancellationToken ct)
    {
        var items = await PublishedItemsAsync(c, ct);
        var evals = await EvaluateAsync(c, items, ct);
        var lines = await c.Db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == c.Factory.Id).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var rows = evals.GroupBy(e => e.Item.LineId).OrderBy(g => lines.GetValueOrDefault(g.Key, g.Key)).Select(g =>
        {
            var started = g.Where(e => e.Progress.State != ItemStates.NotStarted).ToList();
            var target = started.Sum(e => e.Item.TargetQuantity);
            var actual = Actual(started);
            return (IReadOnlyList<object?>)new object?[] { lines.GetValueOrDefault(g.Key, g.Key), g.Count(), target, actual, actual is null ? null : PlanMath.Pct(actual.Value, started.Where(e => e.Progress.Actual != null).Sum(e => e.Item.TargetQuantity)), Basis(started) };
        }).ToList();
        return new ReportSection("Line-wise target achievement", Array.Empty<Kpi>(), new[]
        {
            new ReportTable("Lines", new[] { "Line", "Plan items", "Target (started shifts)", "Actual", "Achievement %", "Basis" }, rows,
                "Only shifts that have started are compared; achievement uses items with evidence of output."),
        });
    }

    private static async Task<ReportSection> DailyAsync(Ctx c, CancellationToken ct)
    {
        var items = await PublishedItemsAsync(c, ct);
        var evals = await EvaluateAsync(c, items, ct);
        var plans = await c.Db.DailyProductionPlans.AsNoTracking().Where(p => p.FactoryId == c.Factory.Id && p.PlanDate >= c.From && p.PlanDate <= c.To && p.Status == PlanStatuses.Published).ToDictionaryAsync(p => p.PlanDate, ct);
        var rows = evals.GroupBy(e => e.Item.PlanDate).OrderBy(g => g.Key).Select(g =>
        {
            var started = g.Where(e => e.Progress.State != ItemStates.NotStarted).ToList();
            var actual = Actual(started);
            return (IReadOnlyList<object?>)new object?[] { g.Key, plans.GetValueOrDefault(g.Key)?.Reference, plans.GetValueOrDefault(g.Key)?.RevisionNo, g.Sum(e => e.Item.TargetQuantity), started.Sum(e => e.Item.TargetQuantity), actual,
                actual is null ? null : PlanMath.Pct(actual.Value, started.Where(e => e.Progress.Actual != null).Sum(e => e.Item.TargetQuantity)), Basis(started) };
        }).ToList();
        return new ReportSection("Daily plan versus actual", Array.Empty<Kpi>(), new[]
        {
            new ReportTable("Days", new[] { "Date", "Plan", "Revision", "Target", "Target (started shifts)", "Actual", "Achievement %", "Basis" }, rows),
        }, rows.Count == 0 ? "No plan was published in this period." : null);
    }

    private static async Task<ReportSection> ShiftAsync(Ctx c, CancellationToken ct)
    {
        var items = await PublishedItemsAsync(c, ct);
        var evals = await EvaluateAsync(c, items, ct);
        var rows = evals.GroupBy(e => (e.Item.PlanDate, e.Item.ShiftId)).OrderBy(g => g.Key.PlanDate).ThenBy(g => c.Clock.Shift(g.Key.ShiftId)?.Start).Select(g =>
        {
            var started = g.Where(e => e.Progress.State != ItemStates.NotStarted).ToList();
            var actual = Actual(started);
            return (IReadOnlyList<object?>)new object?[] { g.Key.PlanDate, c.Clock.Shift(g.Key.ShiftId)?.Name ?? "?", g.Sum(e => e.Item.TargetQuantity), actual,
                actual is null ? null : PlanMath.Pct(actual.Value, started.Where(e => e.Progress.Actual != null).Sum(e => e.Item.TargetQuantity)), Basis(started),
                string.Join(", ", g.Select(e => e.Progress.State).Distinct()) };
        }).ToList();
        return new ReportSection("Shift-wise plan versus actual", Array.Empty<Kpi>(), new[]
        {
            new ReportTable("Shifts", new[] { "Date", "Shift", "Target", "Actual", "Achievement %", "Basis", "State" }, rows),
        });
    }

    private static async Task<ReportSection> DeliveryAsync(Ctx c, CancellationToken ct)
    {
        var orders = await PlanningScope.Orders(c.Db, c.Caller).Where(o => o.FactoryId == c.Factory.Id && (o.Status == ProductionOrderStatus.Planned || o.Status == ProductionOrderStatus.InProgress || o.Status == ProductionOrderStatus.OnHold)
                                                                           && (c.OrderId == null || o.Id == c.OrderId)).OrderBy(o => o.DueDate).Take(500).ToListAsync(ct);
        var outputs = await OrderOutputs.ForOrdersAsync(c.Db, c.Factory.Id, orders.Select(o => o.Id).ToList(), c.Clock, c.Source, c.Now, ct);
        var rows = orders.Select(o =>
        {
            var out1 = outputs.GetValueOrDefault(o.Id);
            var fc = PlanningMonitorService.ForecastOf(o, out1, c.Clock, c.Today);
            return (IReadOnlyList<object?>)new object?[] { o.OrderNumber, o.Buyer, o.DueDate, o.DueDate.DayNumber - c.Today.DayNumber, o.Quantity, out1?.Produced, out1?.Produced is { } p ? Math.Max(0, o.Quantity - p) : null,
                fc.DailyRate, fc.Status, fc.ProjectedCompletion, fc.Note };
        }).ToList();
        return new ReportSection("Delivery progress", Array.Empty<Kpi>(), new[]
        {
            new ReportTable("Orders", new[] { "Order", "Buyer", "Delivery deadline", "Days left", "Quantity", "Output", "Remaining", "Pace / day", "Forecast", "Projected completion", "Note" }, rows,
                $"A forecast needs verified output from at least {OrderForecasts.MinObservedDays} production days; pace is the mean of the last {OrderForecasts.MaxObservedDays}."),
        });
    }

    private static async Task<ReportSection> CapacityAsync(Ctx c, CancellationToken ct)
    {
        var r = await new CapacityService(c.Db).OverviewAsync(c.Caller, c.Factory.Id, c.From, c.To, ct);
        var rows = r.Value?.Lines.Select(l => (IReadOnlyList<object?>)new object?[] { l.LineName, l.Allocations, l.AvailableHours, l.DemandMinutes, l.CapacityMinutes, l.UtilizationPct, l.Status,
            string.Join("; ", l.Missing.Select(m => m.Message)) }).ToList() ?? new List<IReadOnlyList<object?>>();
        return new ReportSection("Capacity utilisation (estimate)", Array.Empty<Kpi>(), new[]
        {
            new ReportTable("Lines", new[] { "Line", "Allocations", "Available hours", "Demand (std min)", "Capacity (std min)", "Utilisation %", "Estimate", "Missing inputs" }, rows,
                "Standard minutes: quantity × SMV against productive minutes × operators × efficiency. INCOMPLETE means an input is not configured and no number is shown."),
        }, CapacityService.Disclaimer);
    }

    private static async Task<ReportSection> DelayAsync(Ctx c, CancellationToken ct)
    {
        var fromUtc = c.Clock.DayWindow(c.From).Start; var toUtc = c.Clock.DayWindow(c.To).End;
        var q = PlanningScope.Exceptions(c.Db, c.Caller).Where(e => e.FactoryId == c.Factory.Id && e.DetectedAt >= fromUtc && e.DetectedAt < toUtc && e.SourceType == c.Source);
        if (c.LineId is { } l) q = q.Where(e => e.LineId == l);
        if (c.OrderId is { } o) q = q.Where(e => e.OrderId == o);
        var rows = await ExceptionService.DtosAsync(c.Db, await q.OrderByDescending(e => e.DetectedAt).Take(1000).ToListAsync(ct), ct);
        return new ReportSection("Production delays and exceptions", Array.Empty<Kpi>(), new[]
        {
            new ReportTable("Exceptions", new[] { "Detected (UTC)", "Type", "Severity", "Status", "Line", "Order", "Reason", "Evidence", "Suggested investigation", "Resolution" },
                rows.Select(e => (IReadOnlyList<object?>)new object?[] { e.DetectedAt, e.TypeLabel, e.Severity, e.Status, e.LineName, e.OrderNumber, e.Reason,
                    string.Join("; ", e.Data.Select(d => $"{d.Label}: {d.Value}")), e.SuggestedInvestigation, e.ResolutionNote }).ToList(),
                "Rule-based detections from counters, machines and capacity; none identifies a person as the cause."),
        });
    }

    private static async Task<ReportSection> RevisionsAsync(Ctx c, CancellationToken ct)
    {
        var fromUtc = c.Clock.DayWindow(c.From).Start; var toUtc = c.Clock.DayWindow(c.To).End;
        var q = from r in c.Db.ProductionTargetRevisions.AsNoTracking()
                join t in PlanningScope.Targets(c.Db, c.Caller) on r.TargetId equals t.Id
                where t.FactoryId == c.Factory.Id && r.At >= fromUtc && r.At < toUtc
                orderby r.At descending
                select new { r, t.Level, t.Period, t.LineId, t.OrderId };
        if (c.LineId is { } l) q = q.Where(x => x.LineId == l);
        var rows = await q.Take(2000).ToListAsync(ct);
        var lines = await c.Db.ProductionLines.AsNoTracking().Where(x => x.FactoryId == c.Factory.Id).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return new ReportSection("Historical target revisions", Array.Empty<Kpi>(), new[]
        {
            new ReportTable("Revisions", new[] { "When (UTC)", "Action", "Level", "Period", "Line", "Version", "Previous quantity", "New quantity", "Effective from", "By", "Reason" },
                rows.Select(x => (IReadOnlyList<object?>)new object?[] { x.r.At, x.r.Action, x.Level, x.Period, x.LineId is { } li ? lines.GetValueOrDefault(li, li) : null, x.r.Revision,
                    x.r.PreviousQuantity, x.r.NewQuantity, x.r.NewEffectiveFrom, x.r.ByEmail, x.r.Reason }).ToList(),
                "Targets are never overwritten: each change is a new version with its own effective date."),
        });
    }
}
