using BunonBrain.Application.Access;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Monitoring;
using BunonBrain.Application.Production;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// The factory overview: workforce today, production, machines now,
/// overtime, open alerts and data coverage. Sections the caller's role
/// doesn't include are marked NOT_PERMITTED, never computed.
/// </summary>
public sealed class OverviewAnalyticsService : IOverviewAnalytics
{
    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public OverviewAnalyticsService(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    public async Task<AccessResult<FactoryOverviewDto>> OverviewAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct)
    {
        var c = await AnalyticsContext.CreateAsync(_db, caller, filter, ct);
        if (c.Outcome != AccessOutcome.Ok) return new AccessResult<FactoryOverviewDto>(c.Outcome, null, c.Code, c.Message);
        return AccessResult<FactoryOverviewDto>.Ok(await BuildAsync(c.Value!, ct));
    }

    internal async Task<FactoryOverviewDto> BuildAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var groups = new List<KpiGroup>();
        var coverage = new List<DataSourceStatus>();
        var warnings = ctx.CommonWarnings().ToList();

        // ── Workforce (the period; "now" figures are live) ──
        if (ctx.Can(AnalyticsSections.Attendance) || ctx.Can(AnalyticsSections.Overtime) || ctx.Can(AnalyticsSections.Assignments))
        {
            var w = await new WorkforceAnalyticsService(_db, _options).BuildAsync(ctx, ct);
            var kpis = w.Kpis.Where(k => k.MetricId is "workers.registered" or "workers.scheduled" or "attendance.present" or "attendance.rate" or "attendance.absent"
                or "attendance.late" or "assignment.coverage").ToList();
            if (ctx.Can(AnalyticsSections.Attendance))
            {
                var open = ctx.Sessions().Where(s => s.Status == SessionStatuses.Open).Select(s => s.Id);
                var onBreak = await _db.BreakSessions.AsNoTracking().CountAsync(b => b.Status == BreakStatuses.Active && open.Contains(b.AttendanceSessionId), ct);
                kpis.Add(Kpi.Count("attendance.on_break", onBreak));
                var last = await ctx.Sessions().MaxAsync(s => (DateTimeOffset?)s.CheckInAt, ct);
                var n = await ctx.Sessions().CountAsync(ct);
                coverage.Add(new DataSourceStatus("attendance_sessions", "Attendance punches", n, last, n == 0 ? Availability.NoData : Availability.Ok,
                    n == 0 ? "No attendance session in this scope and period." : null));
            }
            groups.Add(new KpiGroup("workforce", "Workforce", kpis, "/app/analytics/workforce", null));
            if (ctx.Can(AnalyticsSections.Overtime))
                groups.Add(new KpiGroup("overtime", "Overtime", w.Kpis.Where(k => k.MetricId.StartsWith("overtime.", StringComparison.Ordinal)).ToList(), "/app/analytics/workforce",
                    "Approved and actual overtime are separate numbers; actual comes from punches."));
            warnings.AddRange(w.Warnings.Where(x => !warnings.Contains(x)));
        }
        else groups.Add(new KpiGroup("workforce", "Workforce", new[] { Kpi.Hidden("attendance.present", "attendance.view") }, null, null));

        // ── Production ──
        if (ctx.Can(AnalyticsSections.Production))
        {
            var p = await new ProductionAnalyticsService(_db, _options).BuildAsync(ctx, ct);
            var lines = await new ProductionAnalyticsService(_db, _options).ScopeLinesAsync(ctx, ct);
            var kpis = p.Kpis.Where(k => k.MetricId is "production.pieces" or "production.pieces_per_hour" or "production.achievement").ToList();
            kpis.Insert(0, new Kpi("lines.active", "Active production lines", lines.Count, "lines", p.ByLine.Count(x => x.Pieces > 0), lines.Count, MetricKinds.Measured,
                lines.Count == 0 ? Availability.NoData : Availability.Ok, $"{p.ByLine.Count(x => x.Pieces > 0)} reported output in the period"));
            groups.Add(new KpiGroup("production", "Production", kpis, "/app/analytics/production", null));
            var last = await ctx.Counts().MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct);
            coverage.Add(new DataSourceStatus("production_counter_events", "Production counters", p.Quality.CountEvents, last,
                p.Quality.CountEvents == 0 ? Availability.NoData : Availability.Ok, p.Quality.CountEvents == 0 ? "No counter data." : null));
        }
        else groups.Add(new KpiGroup("production", "Production", new[] { Kpi.Hidden("production.pieces", "production.view or monitoring.view") }, null, null));

        // ── Machines now ──
        if (ctx.Can(AnalyticsSections.Machines))
        {
            var machines = ctx.Machines().Select(m => m.Id);
            var monitored = await _db.Devices.AsNoTracking().Where(d => d.MachineId != null && machines.Contains(d.MachineId.Value) && d.Status != "Disabled" && d.IsSimulated == ctx.Simulated)
                .Select(d => d.MachineId!.Value).Distinct().ToListAsync(ct);
            var states = await _db.MachineStates.AsNoTracking().Where(s => monitored.Contains(s.MachineId)).GroupBy(s => s.State)
                .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            Kpi S(string state, string label) => new($"machine.state.{state.ToLowerInvariant()}", label, states.GetValueOrDefault(state), "machines", states.GetValueOrDefault(state),
                monitored.Count, MetricKinds.Measured, monitored.Count == 0 ? Availability.NoData : Availability.Ok, null);
            var total = await machines.CountAsync(ct);
            groups.Add(new KpiGroup("machines", "Machines now", new[]
            {
                new Kpi("machine.states_now", "Monitored machines", monitored.Count, "machines", monitored.Count, total, MetricKinds.Measured,
                    monitored.Count == 0 ? Availability.NoData : Availability.Ok, $"{total - monitored.Count} of {total} without a sensor"),
                S(MachineStates.Running, "Running"), S(MachineStates.Idle, "Idle"), S(MachineStates.Stopped, "Stopped"), S(MachineStates.Error, "Error"),
                S(MachineStates.Offline, "Offline"), S(MachineStates.Unknown, "Unknown"), S(MachineStates.Maintenance, "Maintenance"),
            }, "/app/analytics/machines", "Machine state from sensors; RUNNING doesn't prove anyone is operating the machine."));
            var (from, to) = (ctx.Period.FromUtc, ctx.Period.ToUtc);
            var tq = _db.MachineTelemetry.AsNoTracking().Where(t => t.MachineId != null && machines.Contains(t.MachineId.Value) && t.SourceType == ctx.Source && t.OccurredAt >= from && t.OccurredAt < to);
            var n = await tq.CountAsync(ct);
            coverage.Add(new DataSourceStatus("machine_telemetry", "Machine telemetry", n, await tq.MaxAsync(t => (DateTimeOffset?)t.OccurredAt, ct),
                n == 0 ? Availability.NoData : Availability.Ok, n == 0 ? "No machine telemetry (raw telemetry is kept for the retention period)." : null));
        }

        // ── Devices ──
        if (ctx.Can(AnalyticsSections.Devices))
        {
            var devices = await ctx.Devices().Select(d => new { d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds }).ToListAsync(ct);
            var online = devices.Count(d => BunonBrain.Application.Devices.DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, ctx.Now)
                .Connection == BunonBrain.Application.Devices.ConnectionStatuses.Online);
            var last = devices.Max(d => d.LastSeenAt);
            coverage.Add(new DataSourceStatus("devices", "Devices online", devices.Count, last, devices.Count == 0 ? Availability.NoData : Availability.Ok,
                devices.Count == 0 ? "No devices." : $"{online} of {devices.Count} online now"));
        }

        // ── Alerts ──
        if (ctx.Can(AnalyticsSections.Alerts))
        {
            var open = await ctx.Alerts().Where(a => AlertStatuses.Active.Contains(a.Status)).GroupBy(a => a.Severity).Select(g => new { g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.N, ct);
            groups.Add(new KpiGroup("alerts", "Open operational alerts", new[]
            {
                Kpi.Count("alerts.open", open.Values.Sum(), $"{open.GetValueOrDefault(AlertSeverities.Critical)} critical · {open.GetValueOrDefault(AlertSeverities.High)} high · " +
                    $"{open.GetValueOrDefault(AlertSeverities.Medium)} medium · {open.GetValueOrDefault(AlertSeverities.Low)} low"),
            }, "/app/monitoring/alerts", null));
        }

        // ── Camera AI ──
        if (ctx.Can(AnalyticsSections.Activity))
        {
            var ev = ctx.Events().Where(e => MonitoringEventTypes.Vision.Contains(e.EventType));
            var n = await ev.CountAsync(ct);
            coverage.Add(new DataSourceStatus("monitoring_events", "Camera AI observations", n, await ev.MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct),
                n == 0 ? Availability.NoData : Availability.Ok, n == 0 ? "No camera AI events." : "AI observations: estimates, not facts."));
        }

        return new FactoryOverviewDto(ctx.Dto(), groups, coverage, warnings.Distinct().ToList());
    }
}
