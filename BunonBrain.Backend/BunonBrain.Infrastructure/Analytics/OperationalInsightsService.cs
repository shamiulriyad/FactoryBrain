using BunonBrain.Application.Access;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Production;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// Gathers aggregated facts from the analytics services (only the sections
/// the caller may see) and runs the rule-based <see cref="InsightEngine"/>.
/// A rule whose data the caller can't see is reported as not evaluated.
/// </summary>
public sealed class OperationalInsightsService : IOperationalInsights
{
    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public OperationalInsightsService(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    public async Task<AccessResult<OperationalInsightsDto>> InsightsAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct)
    {
        var c = await AnalyticsContext.CreateAsync(_db, caller, filter, ct);
        if (c.Outcome != AccessOutcome.Ok) return new AccessResult<OperationalInsightsDto>(c.Outcome, null, c.Code, c.Message);
        return AccessResult<OperationalInsightsDto>.Ok(await BuildAsync(c.Value!, ct));
    }

    internal async Task<OperationalInsightsDto> BuildAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var prodSvc = new ProductionAnalyticsService(_db, _options);
        var canProd = ctx.Can(AnalyticsSections.Production);
        var canMach = ctx.Can(AnalyticsSections.Machines);
        var canAtt = ctx.Can(AnalyticsSections.Attendance);
        var canOt = ctx.Can(AnalyticsSections.Overtime);
        var canAssign = ctx.Can(AnalyticsSections.Assignments);
        var canAlerts = ctx.Can(AnalyticsSections.Alerts);
        var canDevices = ctx.Can(AnalyticsSections.Devices);

        // Machines and their counters.
        var machineFacts = new List<MachineFacts>();
        var counterMachines = new HashSet<Guid>();
        var machineLine = new Dictionary<Guid, string?>();
        double coveragePct = 100;
        var monitoredCount = 0;
        if (canMach)
        {
            var mSvc = new MachineAnalyticsService(_db);
            var (machines, monitored, times) = await mSvc.TimesAsync(ctx, false, ct);
            monitoredCount = monitored.Count;
            var ids = machines.Select(m => m.Id).ToList();
            var devices = await _db.Devices.AsNoTracking().Where(d => d.MachineId != null && ids.Contains(d.MachineId.Value) && d.Status != "Disabled" && d.IsSimulated == ctx.Simulated)
                .Select(d => new { d.Id, MachineId = d.MachineId!.Value, d.Category }).ToListAsync(ct);
            var devIds = devices.Select(d => d.Id).ToList();
            var types = await _db.DeviceIotConfigurations.AsNoTracking().Where(x => devIds.Contains(x.DeviceId)).ToDictionaryAsync(x => x.DeviceId, x => x.DeviceType, ct);
            counterMachines = devices.Where(d => d.Category == DeviceCategories.ProductionCounter || types.GetValueOrDefault(d.Id) == IotDeviceTypes.PieceCounter)
                .Select(d => d.MachineId).ToHashSet();
            var wsIds = machines.Where(m => m.WorkstationId != null).Select(m => m.WorkstationId!.Value).ToList();
            var ws = await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.LineId, ct);
            var lineNames = await ctx.LineNamesAsync(ws.Values, ct);
            var pieces = canProd
                ? await ctx.Counts().Where(e => e.MachineId != null && e.Pieces > 0).GroupBy(e => e.MachineId!.Value).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) })
                    .ToDictionaryAsync(x => x.Key, x => x.N, ct)
                : new Dictionary<Guid, long>();
            foreach (var m in machines)
            {
                var line = m.WorkstationId is { } w ? ws.GetValueOrDefault(w) : null;
                machineLine[m.Id] = line;
                if (!times.TryGetValue(m.Id, out var t)) continue;
                machineFacts.Add(new MachineFacts(m.Id, m.Code, line, line is null ? null : lineNames.GetValueOrDefault(line, line), t.Total, t.Errors,
                    canProd && counterMachines.Contains(m.Id), pieces.GetValueOrDefault(m.Id)));
            }
            var sum = MachineAvailability.Sum(times.Values.Select(t => t.Total));
            coveragePct = sum.CoveragePct ?? 100;
        }

        // Lines: plans and output, staffing, assignments, overtime.
        var lineFacts = new List<LineFacts>();
        var scopeLines = await prodSvc.ScopeLinesAsync(ctx, ct);
        var lineNamesAll = await ctx.LineNamesAsync(scopeLines, ct);
        var production = canProd ? await prodSvc.BuildAsync(ctx, ct) : null;
        var staff = canAtt || canAssign ? await new WorkforceAnalyticsService(_db, _options).LineStaffAsync(ctx, ct) : Array.Empty<WorkforceAnalyticsService.LineStaff>();
        var ot = canOt
            ? await ctx.Overtime().Where(o => o.LineId != null && (o.Status == "Approved" || o.Status == "InProgress" || o.Status == "Completed"))
                .GroupBy(o => o.LineId!).Select(g => new { g.Key, A = g.Sum(x => x.ApprovedMinutes ?? 0), U = g.Sum(x => x.ActualMinutes ?? 0) }).ToDictionaryAsync(x => x.Key, ct)
            : new();
        foreach (var l in scopeLines)
        {
            var plan = production?.PlanVsActual.FirstOrDefault(p => p.LineId == l);
            var s = staff.FirstOrDefault(x => x.LineId == l);
            var machinesOnLine = machineLine.Where(x => x.Value == l).Select(x => x.Key).ToList();
            lineFacts.Add(new LineFacts(l, lineNamesAll.GetValueOrDefault(l, l), plan?.Planned ?? 0, plan?.Actual ?? 0, machinesOnLine.Count(counterMachines.Contains),
                machinesOnLine.Count, canAtt ? s?.ScheduledStarted ?? 0 : 0, canAtt ? s?.ScheduledPresent ?? 0 : 0, canAtt ? s?.PlannedOperatorsToday ?? 0 : 0,
                canAtt ? s?.PresentToday ?? 0 : 0, s?.EmptyWorkstations ?? 0, s?.ActiveWorkstations ?? 0, canOt && canAtt ? s?.AttendanceMinutes ?? 0 : 0,
                ot.TryGetValue(l, out var o) ? o.U : 0, ot.TryGetValue(l, out var o2) ? o2.A : 0));
        }

        // Alerts raised in the period, by type and subject.
        var alertFacts = new List<AlertFacts>();
        if (canAlerts)
        {
            var (from, to) = (ctx.Period.FromUtc, ctx.Period.ToUtc);
            var rows = await ctx.Alerts().Where(a => a.LastOccurredAt >= from && a.FirstOccurredAt < to)
                .GroupBy(a => new { a.AlertType, a.MachineId, a.CameraId, a.DeviceId })
                .Select(g => new { g.Key.AlertType, g.Key.MachineId, g.Key.CameraId, g.Key.DeviceId, Alerts = g.Count(), Occ = g.Sum(x => x.OccurrenceCount), Title = g.Max(x => x.Title) })
                .ToListAsync(ct);
            alertFacts = rows.Select(r => new AlertFacts(r.AlertType, r.Title ?? r.AlertType, null, r.Alerts, r.Occ)).ToList();
        }

        // Coverage.
        CoverageFacts? coverage = null;
        if (canDevices || canMach || ctx.Can(AnalyticsSections.Activity))
        {
            var devices = canDevices ? await ctx.Devices().Select(d => new { d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds }).ToListAsync(ct) : new();
            var online = devices.Count(d => DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, ctx.Now).Connection == ConnectionStatuses.Online);
            var (active, reporting) = ctx.Can(AnalyticsSections.Activity) ? await new ActivityAnalyticsService(_db).CameraCoverageAsync(ctx, ct) : (0, 0);
            coverage = new CoverageFacts(devices.Count, online, active, reporting, coveragePct, monitoredCount);
        }

        var factory = ctx.Filter.FactoryId is { } fid ? await _db.Factories.AsNoTracking().Where(f => f.Id == fid).Select(f => new { f.Id, f.Code }).FirstOrDefaultAsync(ct) : null;
        var facts = new InsightFacts(factory?.Id ?? Guid.Empty, factory?.Code ?? ctx.ScopeLabel, ctx.Period, ctx.Source, lineFacts, machineFacts, alertFacts, coverage, ctx.Sections);
        var insights = InsightEngine.Evaluate(facts, ctx.Now);

        string? Why(bool ok, string section) => ok ? null : $"Needs {AnalyticsSections.PermissionFor(section)}.";
        var rules = new List<InsightRuleStatus>
        {
            new(InsightTypes.LineBelowTarget, canProd && production?.PlanVsActual.Count > 0, !canProd ? Why(false, AnalyticsSections.Production) : production?.PlanVsActual.Count > 0 ? null : "No active plan covers these lines and dates."),
            new(InsightTypes.HighMachineDowntime, canMach && monitoredCount > 0, canMach ? (monitoredCount == 0 ? "No machine sensor data." : null) : Why(false, AnalyticsSections.Machines)),
            new(InsightTypes.RepeatedMachineErrors, canMach && monitoredCount > 0, canMach ? (monitoredCount == 0 ? "No machine sensor data." : null) : Why(false, AnalyticsSections.Machines)),
            new(InsightTypes.CounterInactivity, canMach && canProd && counterMachines.Count > 0, !canMach || !canProd ? "Needs machine and production access." : counterMachines.Count == 0 ? "No machine has a counter." : null),
            new(InsightTypes.AttendanceShortage, canAtt, Why(canAtt, AnalyticsSections.Attendance)),
            new(InsightTypes.AssignmentGap, canAssign, Why(canAssign, AnalyticsSections.Assignments)),
            new(InsightTypes.HighOvertime, canOt && canAtt, canOt && canAtt ? null : "Needs attendance.view and overtime.view."),
            new(InsightTypes.LowDataCoverage, coverage is not null, coverage is null ? "Needs device, machine or monitoring access." : null),
            new(InsightTypes.RepeatedAlerts, canAlerts, Why(canAlerts, AnalyticsSections.Alerts)),
        };
        var warnings = ctx.CommonWarnings().ToList();
        if (coverage is { } cv && cv.MonitoredMachines > 0 && cv.MachineCoveragePct < InsightThresholds.LowCoveragePct)
            warnings.Add("Machine data coverage is low in this period: machine-based insights are less reliable.");
        return new OperationalInsightsDto(ctx.Dto(), insights, rules, warnings,
            "Insights point to where to look; they are rule-based, show their evidence and never claim a root cause. Low output or downtime is not evidence that workers underperformed.");
    }
}
