using BunonBrain.Infrastructure.Attendance;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Access;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Monitoring;
using BunonBrain.Application.Production;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// Workforce analytics from attendance sessions, shift assignments, breaks,
/// overtime and workstation assignments. Scheduled, checked in, assigned,
/// verified by camera identity and seen by the camera AI are different
/// things and stay separate; a camera detection is never attendance.
/// </summary>
public sealed class WorkforceAnalyticsService : IWorkforceAnalytics
{
    public const int MaxSessions = 300_000;

    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public WorkforceAnalyticsService(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    public async Task<AccessResult<WorkforceAnalyticsDto>> WorkforceAsync(AccessContext caller, AnalyticsFilter filter, CancellationToken ct)
    {
        var c = await AnalyticsContext.CreateAsync(_db, caller, filter, ct);
        if (c.Outcome != AccessOutcome.Ok) return new AccessResult<WorkforceAnalyticsDto>(c.Outcome, null, c.Code, c.Message);
        var ctx = c.Value!;
        if (!ctx.Can(AnalyticsSections.Attendance) && !ctx.Can(AnalyticsSections.Overtime) && !ctx.Can(AnalyticsSections.Assignments))
            return AccessResult<WorkforceAnalyticsDto>.Forbidden("workforce_access_required", "Workforce analytics need attendance.view, overtime.view or workers.view.");
        return AccessResult<WorkforceAnalyticsDto>.Ok(await BuildAsync(ctx, ct));
    }

    public sealed record LineStaff(string LineId, int ScheduledStarted, int ScheduledPresent, int PresentToday, int PlannedOperatorsToday, int AttendanceMinutes,
        int EmptyWorkstations, int ActiveWorkstations);

    internal async Task<WorkforceAnalyticsDto> BuildAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var warnings = ctx.CommonWarnings().ToList();
        var canAttendance = ctx.Can(AnalyticsSections.Attendance);
        var canOvertime = ctx.Can(AnalyticsSections.Overtime);
        var canAssign = ctx.Can(AnalyticsSections.Assignments);
        var kpis = new List<Kpi>();

        var workersQ = ctx.Workers();
        var workers = canAttendance || canAssign
            ? await workersQ.Select(w => new { w.Id, w.DepartmentId, w.LineId, w.FactoryId }).ToListAsync(ct)
            : new();
        var workerIds = workers.Select(w => w.Id).ToList();

        // ── Attendance ──
        IReadOnlyList<AttendanceDayStats> days = Array.Empty<AttendanceDayStats>();
        List<ScheduledDay> schedule = new();
        List<SessionFact> sessions = new();
        List<ExcusedDay> excusedDays = new();
        List<(Guid WorkerId, DateOnly Date, Guid? ShiftId, string? LineId)> sessionKeys = new();
        if (canAttendance)
        {
            var assignments = await (from a in ctx.ShiftAssignments(workersQ)
                                     join s in _db.Shifts.AsNoTracking() on a.ShiftId equals s.Id
                                     select new ShiftAssignmentFact(a.WorkerId, a.ShiftId, a.EffectiveFrom, a.EffectiveTo, s.StartLocal, s.LateGraceMinutes, s.WorkingDays))
                .ToListAsync(ct);
            schedule = AttendanceAggregation.Schedule(ctx.Period.Dates(), assignments, ctx.Zone).ToList();
            // Factory holidays and approved leave are not expected days: never absence, not in the rate.
            var factoryOf = workers.ToDictionary(w => w.Id, w => w.FactoryId);
            var calendar = await AttendanceCalendarLoader.LoadAsync(_db, workers.Select(w => w.FactoryId).Distinct().ToList(), ctx.Period.From, ctx.Period.To, ct);
            (schedule, excusedDays) = calendar.Apply(schedule, x => x.WorkerId, x => x.Date, w => factoryOf.TryGetValue(w, out var f) ? f : null);
            var rows = await ctx.Sessions().Take(MaxSessions + 1).Select(s => new
            {
                s.WorkerId, s.WorkDate, s.ShiftId, s.LineId, CheckedIn = s.CheckInAt != null, s.IsLate, s.IsEarlyDeparture, s.AttendanceMinutes, s.BreakMinutes,
                s.OverBreakMinutes, s.BeyondShiftMinutes, s.NeedsReview,
            }).ToListAsync(ct);
            if (rows.Count > MaxSessions) { rows = rows.Take(MaxSessions).ToList(); warnings.Add($"More than {MaxSessions} sessions: shorten the range for exact figures."); }
            sessions = rows.Select(r => new SessionFact(r.WorkerId, r.WorkDate, r.CheckedIn, r.IsLate, r.IsEarlyDeparture, r.AttendanceMinutes, r.BreakMinutes,
                r.OverBreakMinutes, r.BeyondShiftMinutes, r.NeedsReview)).ToList();
            sessionKeys = rows.Select(r => (r.WorkerId, r.WorkDate, r.ShiftId, r.LineId)).ToList();
            days = AttendanceAggregation.Daily(ctx.Period.Dates(), schedule, sessions, ctx.Now);
            var t = AttendanceAggregation.Total(days.ToList());
            kpis.Add(Kpi.Count("workers.registered", workers.Count));
            kpis.Add(schedule.Count == 0 ? Kpi.Missing("workers.scheduled") : Kpi.Count("workers.scheduled", t.Scheduled, "worker-days in the period"));
            kpis.Add(Kpi.Count("attendance.present", t.Present, t.Unscheduled > 0 ? $"{t.Unscheduled} without a schedule" : null));
            kpis.Add(schedule.Count == 0 ? Kpi.Missing("attendance.rate") : Kpi.Ratio("attendance.rate", t.ScheduledPresent, t.ScheduledStarted));
            var checkedInDays = sessions.Where(s => s.CheckedIn).Select(s => (s.WorkerId, s.Date)).ToHashSet();
            var leaveDays = excusedDays.Count(e => e.Kind == ExcusalKinds.Leave && !checkedInDays.Contains((e.WorkerId, e.Date)));
            var holidayDays = excusedDays.Count(e => e.Kind == ExcusalKinds.Holiday);
            kpis.Add(schedule.Count == 0 && excusedDays.Count == 0 ? Kpi.Missing("attendance.absent")
                : Kpi.Count("attendance.absent", t.Absent, $"{leaveDays} worker-day(s) on approved leave and {holidayDays} on holidays not counted"));
            kpis.Add(Kpi.Count("attendance.late", t.Late));
            kpis.Add(Kpi.Count("attendance.early_departure", t.EarlyDeparture));
            kpis.Add(Kpi.Count("attendance.break_minutes", t.BreakMinutes, t.OverBreakMinutes > 0 ? $"{t.OverBreakMinutes} min over the allowed breaks" : null));
            kpis.Add(Kpi.Count("overtime.beyond_shift", t.BeyondShiftMinutes));
            if (schedule.Count == 0) warnings.Add("No shift assignments in scope: scheduled workers, absence and attendance rate are unavailable.");
            if (t.NeedsReview > 0) warnings.Add($"{t.NeedsReview} session(s) have a missing punch and need review; their durations are incomplete.");
        }
        else
            foreach (var id in new[] { "workers.registered", "workers.scheduled", "attendance.present", "attendance.rate", "attendance.absent", "attendance.late" })
                kpis.Add(Kpi.Hidden(id, "attendance.view"));

        // ── Overtime ──
        var ot = canOvertime
            ? await ctx.Overtime().Select(o => new OvertimeFact(o.WorkerId, o.LineId, o.WorkDate, o.Status, o.RequestedMinutes, o.ApprovedMinutes, o.ActualMinutes)).ToListAsync(ct)
            : new List<OvertimeFact>();
        var otSummary = OvertimeSummary.Of(ot);
        if (canOvertime)
        {
            kpis.Add(Kpi.Count("overtime.approved", otSummary.ApprovedMinutes, $"{otSummary.Approved} request(s) · {otSummary.Pending} pending"));
            kpis.Add(Kpi.Count("overtime.actual", otSummary.ActualMinutes, otSummary.ActualVsApprovedPct is { } p ? $"{p} % of approved" : null));
        }
        else { kpis.Add(Kpi.Hidden("overtime.approved", "overtime.view")); kpis.Add(Kpi.Hidden("overtime.actual", "overtime.view")); }
        var otDaily = ctx.Period.Dates().Select(d =>
        {
            var r = ot.Where(o => o.Date == d).ToList();
            var g = OvertimeSummary.Of(r);
            return new OvertimeDayRow(d, g.ApprovedMinutes, g.ActualMinutes, r.Count);
        }).ToList();

        // ── Assignments ──
        var assignedIds = new HashSet<Guid>();
        Dictionary<string, (int Active, int Empty)> wsByLine = new();
        if (canAssign)
        {
            assignedIds = (await _db.Assignments.AsNoTracking().Where(a => a.EndAt == null && workerIds.Contains(a.WorkerId)).Select(a => a.WorkerId).Distinct().ToListAsync(ct)).ToHashSet();
            kpis.Add(Kpi.Ratio("assignment.coverage", assignedIds.Count, workers.Count));
            kpis.Add(Kpi.Count("assignment.unassigned", workers.Count - assignedIds.Count));
            var lines = await new ProductionAnalyticsService(_db, _options).ScopeLinesAsync(ctx, ct);
            var ws = await _db.Workstations.AsNoTracking().Where(w => w.IsActive && lines.Contains(w.LineId))
                .Select(w => new { w.LineId, Assigned = _db.Assignments.Any(a => a.WorkstationId == w.Id && a.EndAt == null) }).ToListAsync(ct);
            wsByLine = ws.GroupBy(w => w.LineId).ToDictionary(g => g.Key, g => (g.Count(), g.Count(x => !x.Assigned)));
            kpis.Add(Kpi.Count("assignment.empty_workstations", ws.Count(w => !w.Assigned), $"of {ws.Count} active workstations"));
        }
        else kpis.Add(Kpi.Hidden("assignment.coverage", "workers.view"));

        // ── Groups ──
        var schedStarted = schedule.Where(s => ctx.Now > s.ShiftStartUtc.AddMinutes(s.LateGraceMinutes)).ToList();
        var presentSet = sessionKeys.Where((_, i) => sessions[i].CheckedIn).Select(k => (k.WorkerId, k.Date)).ToHashSet();
        var workerDept = workers.ToDictionary(w => w.Id, w => w.DepartmentId);
        var workerLine = workers.ToDictionary(w => w.Id, w => w.LineId);
        var today = ctx.Period.To;
        var targets = canAttendance
            ? await new PlanTargets(_db, _options).ForLinesAsync(await new ProductionAnalyticsService(_db, _options).ScopeLinesAsync(ctx, ct), ctx.Period, ctx.Now, ct)
            : new Dictionary<string, PlanTargets.LineTarget>();

        WorkforceGroupRow Row(string key, string label, Func<Guid, bool> inGroup, Func<int, (Guid WorkerId, DateOnly Date, Guid? ShiftId, string? LineId), bool> sessionInGroup,
            string? lineId = null)
        {
            var groupWorkers = workers.Where(w => inGroup(w.Id)).Select(w => w.Id).ToList();
            var started = schedStarted.Where(s => inGroup(s.WorkerId)).ToList();
            var idx = Enumerable.Range(0, sessionKeys.Count).Where(i => sessionInGroup(i, sessionKeys[i])).ToList();
            var present = idx.Where(i => sessions[i].CheckedIn).Select(i => (sessionKeys[i].WorkerId, sessionKeys[i].Date)).Distinct().Count();
            var presentToday = idx.Where(i => sessions[i].CheckedIn && sessionKeys[i].Date == today).Select(i => sessionKeys[i].WorkerId).Distinct().Count();
            var assigned = groupWorkers.Count(assignedIds.Contains);
            var planned = lineId is not null && targets.TryGetValue(lineId, out var tg) ? tg.OperatorsByDay.GetValueOrDefault(today) : 0;
            string? note = planned > 0 && presentToday < planned ? $"{presentToday} present today for {planned} planned operators" : null;
            return new WorkforceGroupRow(key, label, groupWorkers.Count, started.Count, started.Count(s => presentSet.Contains((s.WorkerId, s.Date))), present,
                AnalyticsMath.Pct(started.Count(s => presentSet.Contains((s.WorkerId, s.Date))), started.Count),
                idx.Where(i => sessions[i].Late).Select(i => (sessionKeys[i].WorkerId, sessionKeys[i].Date)).Distinct().Count(),
                idx.Where(i => sessions[i].EarlyDeparture).Select(i => (sessionKeys[i].WorkerId, sessionKeys[i].Date)).Distinct().Count(),
                canAssign ? assigned : 0, canAssign ? AnalyticsMath.Pct(assigned, groupWorkers.Count) : null, planned, presentToday, note);
        }

        var byShift = new List<WorkforceGroupRow>();
        var byDept = new List<WorkforceGroupRow>();
        var byLine = new List<WorkforceGroupRow>();
        if (canAttendance)
        {
            var shiftIds = schedule.Select(s => s.ShiftId).Concat(sessionKeys.Where(k => k.ShiftId != null).Select(k => k.ShiftId!.Value)).Distinct().ToList();
            var shiftNames = await _db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            foreach (var sid in shiftIds)
            {
                var scheduledInShift = schedule.Where(s => s.ShiftId == sid).Select(s => s.WorkerId).ToHashSet();
                byShift.Add(Row(sid.ToString(), shiftNames.GetValueOrDefault(sid, "?"), id => scheduledInShift.Contains(id), (_, k) => k.ShiftId == sid) with
                {
                    Workers = scheduledInShift.Count,
                });
            }
            var deptIds = workers.Where(w => w.DepartmentId != null).Select(w => w.DepartmentId!.Value).Distinct().ToList();
            var deptNames = await _db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            foreach (var d in deptIds)
                byDept.Add(Row(d.ToString(), deptNames.GetValueOrDefault(d, "?"), id => workerDept.GetValueOrDefault(id) == d, (_, k) => workerDept.GetValueOrDefault(k.WorkerId) == d));
            var lineIds = workers.Where(w => w.LineId != null).Select(w => w.LineId!).Concat(sessionKeys.Where(k => k.LineId != null).Select(k => k.LineId!)).Distinct().ToList();
            var lineNames = await ctx.LineNamesAsync(lineIds, ct);
            foreach (var l in lineIds)
            {
                var row = Row(l, lineNames.GetValueOrDefault(l, l), id => workerLine.GetValueOrDefault(id) == l, (_, k) => k.LineId == l, l);
                byLine.Add(row);
            }
            byShift = byShift.OrderBy(r => r.Label).ToList();
            byDept = byDept.OrderBy(r => r.Label).ToList();
            byLine = byLine.OrderBy(r => r.Label).ToList();
        }

        // ── Separate concepts ──
        var concepts = new List<WorkforceConcept>();
        if (canAttendance)
        {
            var t = AttendanceAggregation.Total(days.ToList());
            concepts.Add(new("scheduled", "Scheduled worker-days", t.Scheduled, MetricKinds.Derived, "From shift assignments; not proof of presence."));
            concepts.Add(new("checked_in", "Worker-days with a check-in", t.Present, MetricKinds.Measured, "From attendance punches (RFID / manual)."));
            concepts.Add(new("on_leave", "Scheduled worker-days on approved leave", excusedDays.Count(e => e.Kind == ExcusalKinds.Leave), MetricKinds.Derived,
                "From approved Leave Record documents; not expected, never absence."));
            concepts.Add(new("holiday", "Scheduled worker-days on factory holidays", excusedDays.Count(e => e.Kind == ExcusalKinds.Holiday), MetricKinds.Derived,
                "From the factory holiday calendar; nobody is expected."));
        }
        if (canAssign)
            concepts.Add(new("assigned", "Workers assigned to a workstation now", assignedIds.Count, MetricKinds.Derived, "A placement, not proof of presence or work."));
        if (ctx.Can(AnalyticsSections.Activity))
        {
            var verified = await ctx.Associations().Where(a => a.Status == "VERIFIED" && a.WorkerId != null).Select(a => a.WorkerId).Distinct().CountAsync(ct);
            var vision = ctx.Events().Where(e => e.TrackId != null && e.CameraId != null && MonitoringEventTypes.Vision.Contains(e.EventType));
            var tracks = await vision.Select(e => new { e.CameraId, e.TrackId }).Distinct().CountAsync(ct);
            var uncertainTypes = new[] { MonitoringEventTypes.ActivityUncertain, MonitoringEventTypes.GarmentActivityUncertain, MonitoringEventTypes.UncertainDetection };
            var uncertain = await vision.Where(e => uncertainTypes.Contains(e.EventType)).Select(e => new { e.CameraId, e.TrackId }).Distinct().CountAsync(ct);
            concepts.Add(new("verified_identity", "Workers with verified camera identity", verified, MetricKinds.Derived,
                "VERIFIED identity associations only (RFID card + reader location + assignment + camera zone)."));
            concepts.Add(new("cv_tracks", "Camera tracks detected", tracks, MetricKinds.AiEstimate,
                "Temporary camera track numbers, not people and not workers; never attendance."));
            concepts.Add(new("uncertain_tracks", "Tracks with uncertain activity", uncertain, MetricKinds.AiEstimate, "The camera AI could not classify their activity."));
        }

        var daily = days.Select(d => new AttendanceDayRow(d.Date, d.Scheduled, d.ScheduledStarted, d.ScheduledPresent, d.Present, d.Absent, d.Late, d.EarlyDeparture,
            d.RatePct, d.BreakMinutes, d.OverBreakMinutes, d.NeedsReview)).ToList();
        if (ctx.Filter.WorkstationId is not null) warnings.Add("The workstation filter selects workers currently assigned there.");
        return new WorkforceAnalyticsDto(ctx.Dto(), kpis, daily, byShift, byDept, byLine, otSummary, otDaily, concepts, warnings);
    }

    /// <summary>Per-line staffing facts for the insight engine.</summary>
    internal async Task<IReadOnlyList<LineStaff>> LineStaffAsync(AnalyticsContext ctx, CancellationToken ct)
    {
        var dto = await BuildAsync(ctx, ct);
        var sessions = ctx.Can(AnalyticsSections.Attendance)
            ? await ctx.Sessions().Where(s => s.LineId != null).GroupBy(s => s.LineId!).Select(g => new { g.Key, M = g.Sum(x => x.AttendanceMinutes) }).ToDictionaryAsync(x => x.Key, x => x.M, ct)
            : new Dictionary<string, int>();
        var lines = await new ProductionAnalyticsService(_db, _options).ScopeLinesAsync(ctx, ct);
        var ws = await _db.Workstations.AsNoTracking().Where(w => w.IsActive && lines.Contains(w.LineId))
            .Select(w => new { w.LineId, Assigned = _db.Assignments.Any(a => a.WorkstationId == w.Id && a.EndAt == null) }).ToListAsync(ct);
        return lines.Select(l =>
        {
            var row = dto.ByLine.FirstOrDefault(r => r.Key == l);
            var w = ws.Where(x => x.LineId == l).ToList();
            return new LineStaff(l, row?.ScheduledStarted ?? 0, row?.ScheduledPresent ?? 0, row?.PresentToday ?? 0, row?.PlannedOperators ?? 0,
                sessions.GetValueOrDefault(l), ctx.Can(AnalyticsSections.Assignments) ? w.Count(x => !x.Assigned) : 0, ctx.Can(AnalyticsSections.Assignments) ? w.Count : 0);
        }).ToList();
    }
}
