using System.Text;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Production;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// Reports built from the same analytics services as the pages, so a report
/// always matches its page: scope, period, generation time, filters, metric
/// definitions, REAL / SIMULATED and coverage warnings are always included.
/// A report type needs the permission of its data (operations reports
/// include only the sections the requester may see). Every generation and
/// export is audited.
/// </summary>
public sealed class AnalyticsReportService : IAnalyticsReports
{
    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public AnalyticsReportService(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    private static string? RequiredSection(string type) => type switch
    {
        ReportTypes.Production => AnalyticsSections.Production,
        ReportTypes.Machines => AnalyticsSections.Machines,
        ReportTypes.Attendance => AnalyticsSections.Attendance,
        ReportTypes.Overtime => AnalyticsSections.Overtime,
        ReportTypes.DeviceHealth => AnalyticsSections.Devices,
        ReportTypes.AiActivity => AnalyticsSections.Activity,
        _ => null, // operations reports: any analytics access, sections filtered
    };

    public IReadOnlyList<ReportTypeDto> Types(AccessContext caller)
    {
        var sections = AnalyticsSections.For(caller);
        return ReportTypes.All.Select(t =>
        {
            var need = RequiredSection(t);
            var ok = need is null ? sections.Count > 0 : sections.Contains(need);
            return new ReportTypeDto(t, ReportTypes.Title(t), ok, ok ? null : $"Needs {(need is null ? "an analytics permission" : AnalyticsSections.PermissionFor(need))}.");
        }).ToList();
    }

    public async Task<AccessResult<ReportDocument>> GenerateAsync(AccessContext caller, string type, AnalyticsFilter filter, CancellationToken ct)
    {
        var t = ReportTypes.Parse(type);
        if (t is null) return AccessResult<ReportDocument>.NotFound("report_type_not_found", $"Report type: one of {string.Join(", ", ReportTypes.All)}.");
        // Operations reports cover a fixed number of days ending on the chosen (or today's) date.
        if (ReportTypes.FixedDays(t) is { } days)
        {
            var end = filter.To ?? filter.From;
            filter = filter with { From = end?.AddDays(-(days - 1)), To = end };
            if (end is null)
            {
                var c0 = await AnalyticsContext.CreateAsync(_db, caller, filter with { From = null, To = null }, ct);
                if (c0.Outcome != AccessOutcome.Ok) return new AccessResult<ReportDocument>(c0.Outcome, null, c0.Code, c0.Message);
                filter = filter with { From = c0.Value!.Period.To.AddDays(-(days - 1)), To = c0.Value.Period.To };
            }
        }
        var c = await AnalyticsContext.CreateAsync(_db, caller, filter, ct);
        if (c.Outcome != AccessOutcome.Ok) return new AccessResult<ReportDocument>(c.Outcome, null, c.Code, c.Message);
        var ctx = c.Value!;
        if (RequiredSection(t) is { } need && !ctx.Can(need))
            return AccessResult<ReportDocument>.Forbidden("report_access_required", $"This report needs {AnalyticsSections.PermissionFor(need)}.");

        var sections = new List<ReportSection>();
        var warnings = ctx.CommonWarnings().ToList();
        var metrics = new HashSet<string>();
        void Use(IEnumerable<Kpi> kpis) { foreach (var k in kpis) if (MetricCatalog.Exists(k.MetricId)) metrics.Add(k.MetricId); }
        void Warn(IEnumerable<string> w) { foreach (var x in w) if (!warnings.Contains(x)) warnings.Add(x); }
        var ops = ReportTypes.FixedDays(t) is not null;

        if (ops)
        {
            var o = await new OverviewAnalyticsService(_db, _options).BuildAsync(ctx, ct);
            foreach (var g in o.Groups) { sections.Add(new ReportSection(g.Title, g.Kpis, Array.Empty<ReportTable>(), g.Note)); Use(g.Kpis); }
            sections.Add(new ReportSection("Data coverage", Array.Empty<Kpi>(), new[]
            {
                new ReportTable("Sources", new[] { "Source", "Records", "Last record (UTC)", "Availability", "Note" },
                    o.Coverage.Select(x => (IReadOnlyList<object?>)new object?[] { x.Label, x.Records, x.LastRecordAt, x.Availability, x.Note }).ToList()),
            }));
            Warn(o.Warnings);
        }
        if ((ops || t == ReportTypes.Production) && ctx.Can(AnalyticsSections.Production))
        {
            var p = await new ProductionAnalyticsService(_db, _options).BuildAsync(ctx, ct);
            Use(p.Kpis);
            sections.Add(new ReportSection("Production", t == ReportTypes.Production ? p.Kpis : Array.Empty<Kpi>(), new[]
            {
                Groups("By line", p.ByLine, true),
                Groups("By shift", p.ByShift),
                Groups("By machine", p.ByMachine),
                Groups("By workstation", p.ByWorkstation),
                Groups("By department", p.ByDepartment),
                Groups("By floor", p.ByFloor),
                new ReportTable("Daily", new[] { "Date", "Pieces", "Planned" }, p.Daily.Select(x => (IReadOnlyList<object?>)new object?[] { x.Key, x.Value, x.Planned }).ToList()),
                new ReportTable("Planned vs actual", new[] { "Line", "Planned", "Counted", "Achievement %", "Plans" },
                    p.PlanVsActual.Select(x => (IReadOnlyList<object?>)new object?[] { x.LineName, x.Planned, x.Actual, x.AchievementPct, x.Plans }).ToList(),
                    "Only where active production plans exist."),
                new ReportTable("Counter data quality", new[] { "Counter events", "Resets", "Readings not counted", "Suspect jumps", "Corrections", "Correction pieces" },
                    new[] { (IReadOnlyList<object?>)new object?[] { p.Quality.CountEvents, p.Quality.Resets, p.Quality.Excluded, p.Quality.SuspectJumps, p.Quality.Corrections, p.Quality.CorrectionPieces } },
                    p.Quality.DuplicateHandling),
            }));
            Warn(p.Warnings);
        }
        if ((ops || t == ReportTypes.Machines) && ctx.Can(AnalyticsSections.Machines))
        {
            var m = await new MachineAnalyticsService(_db).BuildAsync(ctx, ct);
            Use(m.Kpis);
            sections.Add(new ReportSection("Machine performance", m.Kpis, new[]
            {
                new ReportTable("By line", new[] { "Line", "Machines", "Monitored", "Running h", "Downtime h", "Data gap h", "Uptime %", "Downtime %", "Coverage %", "Errors" },
                    m.Lines.Select(x => (IReadOnlyList<object?>)new object?[] { x.LineName, x.Machines, x.Monitored, x.RunningHours, x.DowntimeHours, x.GapHours, x.UptimePct, x.DowntimePct, x.CoveragePct, x.Errors }).ToList()),
                new ReportTable("By machine", new[] { "Machine", "Line", "Running h", "Idle h", "Stopped h", "Error h", "Maint. h", "Offline h", "Unknown h", "Uptime %", "Errors", "Pieces" },
                    m.Machines.Where(x => x.Monitored).Select(x => (IReadOnlyList<object?>)new object?[] { x.Code, x.Line?.Name, x.RunningHours, x.IdleHours, x.StoppedHours, x.ErrorHours,
                        x.MaintenanceHours, x.OfflineHours, x.UnknownHours, x.UptimePct, x.Errors, x.Pieces }).ToList(),
                    "Uptime = running ÷ confirmed-state time; offline / unknown time is a data gap, not downtime."),
                new ReportTable("Daily", new[] { "Date", "Running h", "Downtime h", "Idle h", "Data gap h", "Uptime %", "Errors" },
                    m.Daily.Select(x => (IReadOnlyList<object?>)new object?[] { x.Date, x.RunningHours, x.DowntimeHours, x.IdleHours, x.GapHours, x.UptimePct, x.Errors }).ToList()),
            }));
            Warn(m.Warnings);
        }
        if ((ops || t is ReportTypes.Attendance or ReportTypes.Overtime) && (ctx.Can(AnalyticsSections.Attendance) || ctx.Can(AnalyticsSections.Overtime)))
        {
            var w = await new WorkforceAnalyticsService(_db, _options).BuildAsync(ctx, ct);
            Use(w.Kpis);
            if ((ops || t == ReportTypes.Attendance) && ctx.Can(AnalyticsSections.Attendance))
                sections.Add(new ReportSection("Attendance", w.Kpis.Where(k => !k.MetricId.StartsWith("overtime.", StringComparison.Ordinal)).ToList(), new[]
                {
                    new ReportTable("Daily", new[] { "Date", "Scheduled", "Shift started", "Scheduled present", "Present", "Absent", "Late", "Early departure", "Rate %", "Break min", "Needs review" },
                        w.Daily.Select(x => (IReadOnlyList<object?>)new object?[] { x.Date, x.Scheduled, x.ScheduledStarted, x.ScheduledPresent, x.Present, x.Absent, x.Late, x.EarlyDeparture,
                            x.RatePct, x.BreakMinutes, x.NeedsReview }).ToList()),
                    Workforce("By line", w.ByLine), Workforce("By department", w.ByDepartment), Workforce("By shift", w.ByShift),
                    new ReportTable("Separate concepts (never added together)", new[] { "Concept", "Value", "Kind", "Note" },
                        w.Concepts.Select(x => (IReadOnlyList<object?>)new object?[] { x.Label, x.Value, x.Kind, x.Note }).ToList()),
                }));
            if ((ops || t == ReportTypes.Overtime) && ctx.Can(AnalyticsSections.Overtime))
                sections.Add(new ReportSection("Overtime", w.Kpis.Where(k => k.MetricId.StartsWith("overtime.", StringComparison.Ordinal)).ToList(), new[]
                {
                    new ReportTable("Summary", new[] { "Requests", "Approved", "Pending", "Rejected", "Requested min", "Approved min", "Actual min", "Actual ÷ approved %", "Workers" },
                        new[] { (IReadOnlyList<object?>)new object?[] { w.Overtime.Requests, w.Overtime.Approved, w.Overtime.Pending, w.Overtime.Rejected, w.Overtime.RequestedMinutes,
                            w.Overtime.ApprovedMinutes, w.Overtime.ActualMinutes, w.Overtime.ActualVsApprovedPct, w.Overtime.WorkersWithOvertime } }),
                    new ReportTable("Daily", new[] { "Date", "Approved min", "Actual min", "Requests" },
                        w.OvertimeDaily.Select(x => (IReadOnlyList<object?>)new object?[] { x.Date, x.ApprovedMinutes, x.ActualMinutes, x.Requests }).ToList()),
                }, "Overtime limits are warnings for review, not legal advice; payable minutes are confirmed by HR in the attendance module."));
            Warn(w.Warnings);
        }
        if (t == ReportTypes.DeviceHealth) { sections.Add(await DeviceSectionAsync(ctx, ct)); metrics.Add("devices.online"); }
        if ((ops || t == ReportTypes.AiActivity) && ctx.Can(AnalyticsSections.Activity))
        {
            var a = await new ActivityAnalyticsService(_db).BuildAsync(ctx, ct);
            Use(a.Kpis);
            sections.Add(new ReportSection("AI activity observations (estimates)", t == ReportTypes.AiActivity ? a.Kpis : a.Kpis.Take(3).ToList(), new[]
            {
                new ReportTable("Predicted activity", new[] { "Activity", "Predictions", "Share %" }, a.Distribution.Select(x => (IReadOnlyList<object?>)new object?[] { x.Activity, x.Predictions, x.SharePct }).ToList()),
                new ReportTable("Confidence", new[] { "Band", "Predictions", "Share %" }, a.Confidence.Select(x => (IReadOnlyList<object?>)new object?[] { x.Band, x.Predictions, x.SharePct }).ToList()),
                new ReportTable("Cameras", new[] { "Camera", "Hours with data", "Tracks", "Predictions", "Observation h" },
                    a.Cameras.Select(x => (IReadOnlyList<object?>)new object?[] { x.Code, x.HoursWithData, x.Tracks, x.Predictions, x.ObservationHours }).ToList()),
                new ReportTable("Identity", new[] { "Tracks observed", "Verified", "Unverified", "Ambiguous / review", "Verified workers" },
                    new[] { (IReadOnlyList<object?>)new object?[] { a.Identity.TracksObserved, a.Identity.VerifiedTracks, a.Identity.UnverifiedTracks, a.Identity.AmbiguousOrReview, a.Identity.VerifiedWorkers } },
                    a.Identity.Note),
            }, a.Disclaimer));
            Warn(a.Warnings);
        }
        if (ops)
        {
            var i = await new OperationalInsightsService(_db, _options).BuildAsync(ctx, ct);
            sections.Add(new ReportSection("Operational insights", Array.Empty<Kpi>(), new[]
            {
                new ReportTable("Insights", new[] { "Severity", "Insight", "Where", "Evidence", "Reliability", "Look into" },
                    i.Insights.Select(x => (IReadOnlyList<object?>)new object?[] { x.Severity, x.Title, x.LineName ?? x.MachineCode ?? x.FactoryCode,
                        string.Join("; ", x.Evidence.Select(e => $"{e.Label}: {e.Value}")), $"{x.Reliability} - {x.ReliabilityReason}", x.Recommendation }).ToList(), i.Note),
            }));
        }
        var hidden = AnalyticsSections.All.Except(ctx.Sections).ToList();
        if (ops && hidden.Count > 0) warnings.Add($"Sections not included for your role: {string.Join(", ", hidden)}.");

        var doc = new ReportDocument(t, ReportTypes.Title(t), ctx.Now, caller.Email, ctx.ScopeLabel, ctx.Period.From, ctx.Period.To, ctx.Period.TimeZone, ctx.Source,
            ctx.Filters, sections, warnings, metrics.OrderBy(x => x).Select(MetricCatalog.Get).ToList());
        return AccessResult<ReportDocument>.Ok(doc);
    }

    public async Task<AccessResult<ReportFile>> ExportAsync(AccessContext caller, string type, string format, AnalyticsFilter filter, string? traceId, CancellationToken ct)
    {
        var fmt = format?.Trim().ToLowerInvariant();
        if (fmt is not ("csv" or "pdf")) return AccessResult<ReportFile>.Invalid("format_invalid", "Format: csv or pdf.");
        var r = await GenerateAsync(caller, type, filter, ct);
        if (r.Outcome != AccessOutcome.Ok) return new AccessResult<ReportFile>(r.Outcome, null, r.Code, r.Message);
        var doc = r.Value!;
        var file = fmt == "csv"
            ? new ReportFile(ReportCsv.FileName(doc, "csv"), "text/csv; charset=utf-8", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ReportCsv.Write(doc))).ToArray())
            : new ReportFile(ReportCsv.FileName(doc, "pdf"), "application/pdf", PdfReportWriter.Write(doc));
        await AuditAsync(caller, "analytics.report_exported", doc, $"{doc.Title} exported as {fmt.ToUpperInvariant()}", traceId, filter.FactoryId, ct);
        return AccessResult<ReportFile>.Ok(file);
    }

    /// <summary>Records who generated or exported which report, for which scope and filters.</summary>
    public async Task AuditAsync(AccessContext caller, string action, ReportDocument doc, string what, string? traceId, Guid? factoryId, CancellationToken ct)
    {
        var filters = doc.Filters.Count == 0 ? "no filters" : string.Join(", ", doc.Filters.Select(f => $"{f.Name}={f.Value}"));
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, action, "analytics_report", doc.Type, null, doc.Source,
            $"{what}: {doc.Scope}, {doc.From:yyyy-MM-dd}..{doc.To:yyyy-MM-dd}, {doc.Source}, {filters}", traceId, factoryId));
        await _db.SaveChangesAsync(ct);
    }

    private static ReportTable Groups(string title, IReadOnlyList<AnalyticsGroup> rows, bool plan = false) => plan
        ? new ReportTable(title, new[] { "Name", "Pieces", "Share %", "Pieces / h", "Planned", "Achievement %" },
            rows.Select(x => (IReadOnlyList<object?>)new object?[] { x.Label, x.Pieces, x.SharePct, x.PiecesPerHour, x.Planned, x.AchievementPct }).ToList())
        : new ReportTable(title, new[] { "Name", "Pieces", "Share %", "Pieces / h" },
            rows.Select(x => (IReadOnlyList<object?>)new object?[] { x.Label, x.Pieces, x.SharePct, x.PiecesPerHour }).ToList());

    private static ReportTable Workforce(string title, IReadOnlyList<WorkforceGroupRow> rows) =>
        new(title, new[] { "Name", "Workers", "Shift started", "Scheduled present", "Present", "Rate %", "Late", "Early departure", "Assigned", "Assignment %", "Staffing" },
            rows.Select(x => (IReadOnlyList<object?>)new object?[] { x.Label, x.Workers, x.ScheduledStarted, x.ScheduledPresent, x.Present, x.RatePct, x.Late, x.EarlyDeparture,
                x.Assigned, x.AssignmentCoveragePct, x.StaffingNote }).ToList());

    private async Task<ReportSection> DeviceSectionAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var devices = await ctx.Devices().OrderBy(d => d.Code).Take(2000).ToListAsync(ct);
        var ids = devices.Select(d => d.Id).ToList();
        var (from, to) = (ctx.Period.FromUtc, ctx.Period.ToUtc);
        var events = await _db.DeviceEvents.AsNoTracking().Where(e => ids.Contains(e.DeviceId) && e.OccurredAt >= from && e.OccurredAt < to)
            .GroupBy(e => new { e.DeviceId, e.EventType }).Select(g => new { g.Key.DeviceId, g.Key.EventType, N = g.Count() }).ToListAsync(ct);
        var beats = await _db.DeviceHeartbeats.AsNoTracking().Where(h => ids.Contains(h.DeviceId) && h.OccurredAt >= from && h.OccurredAt < to)
            .GroupBy(h => h.DeviceId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var rows = devices.Select(d =>
        {
            var h = DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, ctx.Now);
            int N(string type) => events.Where(e => e.DeviceId == d.Id && e.EventType == type).Sum(e => e.N);
            return (IReadOnlyList<object?>)new object?[]
            {
                d.Code, d.Category, h.Label, d.LastSeenAt, d.FirmwareVersion, beats.GetValueOrDefault(d.Id), N(DeviceEventTypes.ConnectionOffline) + N(DeviceEventTypes.HeartbeatMissed),
                N(DeviceEventTypes.Error),
            };
        }).ToList();
        var online = devices.Count(d => DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, ctx.Now).Connection == ConnectionStatuses.Online);
        return new ReportSection("Device health", new[] { Kpi.Ratio("devices.online", online, devices.Count) }, new[]
        {
            new ReportTable("Devices", new[] { "Device", "Category", "Health now", "Last seen (UTC)", "Firmware", "Heartbeats", "Went offline", "Errors" }, rows),
        });
    }
}
