using System.Globalization;
using System.Text;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Attendance;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Attendance;

// Corrections, statistics, reports, policy and readers.
public sealed partial class AttendanceService
{
    // ─── Corrections ───────────────────────────────────────────────────────

    public async Task<AccessResult<CorrectionDto>> RequestCorrectionAsync(AccessContext caller, Guid sessionId, CreateCorrectionRequest r, string? traceId, CancellationToken ct)
    {
        var s = await _support.Sessions(caller).FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        if (s is null) return SessionNotFound<CorrectionDto>();
        if (await _db.AttendanceCorrections.AnyAsync(c => c.SessionId == sessionId && c.Status == CorrectionStatuses.Pending, ct))
            return AccessResult<CorrectionDto>.Conflict("correction_pending", "A correction of this session is already waiting for approval.");
        var newIn = r.CheckInAt ?? s.CheckInAt;
        var newOut = r.CheckOutAt ?? s.CheckOutAt;
        if (newIn is { } i && newOut is { } o && o <= i)
            return AccessResult<CorrectionDto>.Invalid("checkout_before_checkin", "With this correction the check-out would be before the check-in.");
        var tz = AttendanceSupport.Zone(await _support.PolicyAsync(s.FactoryId, ct));
        foreach (var t in new[] { r.CheckInAt, r.CheckOutAt })
            if (t is { } v && (AttendanceCalculator.LocalDate(v, tz) < s.WorkDate.AddDays(-1) || AttendanceCalculator.LocalDate(v, tz) > s.WorkDate.AddDays(2) || v > Now.AddMinutes(5)))
                return AccessResult<CorrectionDto>.Invalid("correction_out_of_range", "Corrected times must be around the session's work date and not in the future.");
        var c = new AttendanceCorrection
        {
            FactoryId = s.FactoryId, WorkerId = s.WorkerId, SessionId = s.Id, WorkDate = s.WorkDate,
            ProposedCheckIn = r.CheckInAt?.ToUniversalTime(), ProposedCheckOut = r.CheckOutAt?.ToUniversalTime(),
            OriginalCheckIn = s.CheckInAt, OriginalCheckOut = s.CheckOutAt, Reason = r.Reason.Trim(),
            RequestedByUserId = caller.UserId, RequestedByEmail = AccessAudit.Clip(caller.Email, 256)!, RequestedAt = Now,
        };
        _db.AttendanceCorrections.Add(c);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.correction_requested", AccessAudit.EntityAttendanceCorrection, c.Id.ToString(),
            null, CorrectionStatuses.Pending, $"correction of session {s.Id} requested: {c.Reason}", traceId, s.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<CorrectionDto>.Created((await _support.CorrectionDtosAsync(new[] { c }, ct))[0]);
    }

    public async Task<AccessResult<CorrectionDto>> RequestMissingSessionAsync(AccessContext caller, CreateMissingSessionRequest r, string? traceId, CancellationToken ct)
    {
        var w = await _support.WorkerAsync(r.WorkerId, ct);
        if (w is null || !AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId)) return AccessResult<CorrectionDto>.NotFound("worker_not_found", "Worker not found.");
        if (r.CheckOutAt > Now.AddMinutes(5) || r.CheckInAt < Now.AddDays(-62))
            return AccessResult<CorrectionDto>.Invalid("correction_out_of_range", "The session must be in the last 62 days and not in the future.");
        var (i, o) = (r.CheckInAt.ToUniversalTime(), r.CheckOutAt.ToUniversalTime());
        if (await _db.AttendanceSessions.AnyAsync(s => s.WorkerId == w.Id && (s.CheckInAt ?? s.CheckOutAt) < o && (s.CheckOutAt ?? s.CheckInAt) > i, ct))
            return AccessResult<CorrectionDto>.Conflict("correction_overlaps", "The worker already has a session in this period. Correct that session instead.");
        var place = await _support.PlaceAsync(w.Id, i, await _support.PolicyAsync(w.FactoryId, ct), ct);
        var c = new AttendanceCorrection
        {
            FactoryId = w.FactoryId, WorkerId = w.Id, SessionId = null, WorkDate = place.WorkDate, ProposedCheckIn = i, ProposedCheckOut = o,
            Reason = r.Reason.Trim(), RequestedByUserId = caller.UserId, RequestedByEmail = AccessAudit.Clip(caller.Email, 256)!, RequestedAt = Now,
        };
        _db.AttendanceCorrections.Add(c);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.correction_requested", AccessAudit.EntityAttendanceCorrection, c.Id.ToString(),
            null, CorrectionStatuses.Pending, $"missing session for worker {w.WorkerCode} on {place.WorkDate:yyyy-MM-dd} requested: {c.Reason}", traceId, w.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<CorrectionDto>.Created((await _support.CorrectionDtosAsync(new[] { c }, ct))[0]);
    }

    public async Task<PagedResult<CorrectionDto>> CorrectionsAsync(AccessContext caller, Guid? factoryId, string? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var workers = _support.Workers(caller).Select(w => w.Id);
        var rows = _db.AttendanceCorrections.AsNoTracking().Where(c => workers.Contains(c.WorkerId));
        if (factoryId is { } f) rows = rows.Where(c => c.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(c => c.Status == status.Trim());
        var total = await rows.CountAsync(ct);
        var list = await rows.OrderByDescending(c => c.Status == CorrectionStatuses.Pending).ThenByDescending(c => c.RequestedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<CorrectionDto>(await _support.CorrectionDtosAsync(list, ct), page, pageSize, total);
    }

    public async Task<AccessResult<CorrectionDto>> DecideCorrectionAsync(AccessContext caller, Guid id, bool approve, DecideCorrectionRequest r, string? traceId, CancellationToken ct)
    {
        var existing = await _db.AttendanceCorrections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        var worker = existing is null ? null : await _support.WorkerAsync(existing.WorkerId, ct);
        if (existing is null || worker is null || !AttendanceSupport.Reaches(caller, worker.FactoryId, worker.LineId))
            return AccessResult<CorrectionDto>.NotFound("correction_not_found", "Correction not found.");
        if (existing.RequestedByUserId == caller.UserId)
            return AccessResult<CorrectionDto>.Forbidden("cannot_decide_own", "Someone other than the requester must approve or reject a correction.");

        var now = Now;
        AccessResult<CorrectionDto>? refusal = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            refusal = null;
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workers WHERE \"Id\" = {worker.Id} FOR UPDATE", ct);
            var c = await _db.AttendanceCorrections.FirstAsync(x => x.Id == id, ct);
            if (c.Status != CorrectionStatuses.Pending)
            {
                refusal = AccessResult<CorrectionDto>.Conflict("correction_decided", $"This correction is already {c.Status}.");
                await tx.RollbackAsync(ct);
                return;
            }
            c.Status = approve ? CorrectionStatuses.Approved : CorrectionStatuses.Rejected;
            c.DecidedByUserId = caller.UserId;
            c.DecidedByEmail = AccessAudit.Clip(caller.Email, 256);
            c.DecidedAt = now;
            c.DecisionNote = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
            if (approve && await ApplyCorrectionAsync(c, worker, now, ct) is { } error)
            {
                refusal = error;
                await tx.RollbackAsync(ct);
                return;
            }
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, approve ? "attendance.correction_approved" : "attendance.correction_rejected",
                AccessAudit.EntityAttendanceCorrection, c.Id.ToString(), CorrectionStatuses.Pending, c.Status,
                $"correction for worker {worker.WorkerCode}{(c.DecisionNote is null ? "" : $": {c.DecisionNote}")}", traceId, c.FactoryId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
        if (refusal is not null) return refusal;
        return AccessResult<CorrectionDto>.Ok((await _support.CorrectionDtosAsync(await _db.AttendanceCorrections.AsNoTracking().Where(x => x.Id == id).ToListAsync(ct), ct))[0]);
    }

    /// <summary>Applies an approved correction inside the caller's transaction (worker locked). The original values stay on the correction.</summary>
    private async Task<AccessResult<CorrectionDto>?> ApplyCorrectionAsync(AttendanceCorrection c, AttendanceSupport.WorkerRow w, DateTimeOffset now, CancellationToken ct)
    {
        var policy = await _support.PolicyAsync(w.FactoryId, ct);
        AttendanceSession s;
        if (c.SessionId is { } sid)
        {
            s = await _db.AttendanceSessions.FirstAsync(x => x.Id == sid, ct);
            var newIn = c.ProposedCheckIn ?? s.CheckInAt;
            var newOut = c.ProposedCheckOut ?? s.CheckOutAt;
            if (newIn is { } i && newOut is { } o && o <= i)
                return AccessResult<CorrectionDto>.Conflict("checkout_before_checkin", "The session changed since the request: the check-out would now be before the check-in.");
            var from = newIn ?? newOut!.Value;
            var to = newOut ?? newIn!.Value;
            if (await _db.AttendanceSessions.AnyAsync(x => x.WorkerId == w.Id && x.Id != s.Id && (x.CheckInAt ?? x.CheckOutAt) < to && (x.CheckOutAt ?? x.CheckInAt) > from, ct))
                return AccessResult<CorrectionDto>.Conflict("correction_overlaps", "The corrected times overlap another session of the worker.");
            if (c.ProposedCheckIn is { } pin && pin != s.CheckInAt)
            {
                var place = await _support.PlaceAsync(w.Id, pin, policy, ct);
                s.WorkDate = place.WorkDate;
                s.ShiftAssignmentId = place.Context?.Assignment.Id;
                s.ShiftId = place.Context?.Shift.Id;
                s.ScheduledStart = place.Occurrence?.Start;
                s.ScheduledEnd = place.Occurrence?.End;
                s.CheckInAt = pin;
                s.CheckInSource = AttendanceSources.Correction;
            }
            if (c.ProposedCheckOut is { } pout && pout != s.CheckOutAt)
            {
                s.CheckOutAt = pout;
                s.CheckOutSource = AttendanceSources.Correction;
                foreach (var b in await _db.BreakSessions.Where(b => b.AttendanceSessionId == s.Id && b.Status == BreakStatuses.Active).ToListAsync(ct))
                {
                    b.EndAt = pout < b.StartAt ? b.StartAt : pout;
                    b.EndSource = AttendanceSources.Correction;
                    b.Status = BreakStatuses.AutoClosed;
                    b.UpdatedAt = now;
                }
            }
            if (s.CheckInAt is not null && s.CheckOutAt is not null)
            {
                s.Status = SessionStatuses.Completed;
                s.NeedsReview = false;
                s.ReviewReason = "Corrected.";
            }
        }
        else
        {
            if (await _db.AttendanceSessions.AnyAsync(x => x.WorkerId == w.Id && (x.CheckInAt ?? x.CheckOutAt) < c.ProposedCheckOut && (x.CheckOutAt ?? x.CheckInAt) > c.ProposedCheckIn, ct))
                return AccessResult<CorrectionDto>.Conflict("correction_overlaps", "The worker now has a session in this period.");
            var place = await _support.PlaceAsync(w.Id, c.ProposedCheckIn!.Value, policy, ct);
            s = new AttendanceSession
            {
                WorkerId = w.Id, FactoryId = w.FactoryId, LineId = w.LineId, WorkDate = place.WorkDate,
                ShiftAssignmentId = place.Context?.Assignment.Id, ShiftId = place.Context?.Shift.Id,
                ScheduledStart = place.Occurrence?.Start, ScheduledEnd = place.Occurrence?.End,
                CheckInAt = c.ProposedCheckIn, CheckOutAt = c.ProposedCheckOut, CheckInSource = AttendanceSources.Correction,
                CheckOutSource = AttendanceSources.Correction, Status = SessionStatuses.Completed, ReviewReason = "Created by an approved correction.",
                CreatedAt = now, UpdatedAt = now,
            };
            _db.AttendanceSessions.Add(s);
            c.SessionId = s.Id;
        }
        s.IsCorrected = true;
        s.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        // The correction is part of the raw event history too.
        foreach (var (type, at) in new[] { (AttendanceEventTypes.CheckIn, c.ProposedCheckIn), (AttendanceEventTypes.CheckOut, c.ProposedCheckOut) })
        {
            if (at is null) continue;
            _db.AttendanceEvents.Add(new AttendanceEvent
            {
                FactoryId = w.FactoryId, WorkerId = w.Id, EventType = type, OccurredAt = at.Value, ReceivedAt = now, Source = AttendanceSources.Correction,
                IsSimulated = false, IdempotencyKey = $"correction:{c.Id:N}:{type}", Outcome = EventOutcomes.Accepted, SessionId = s.Id,
                OutcomeMessage = AccessAudit.Clip($"Approved correction: {c.Reason}", 300),
                RecordedByUserId = c.DecidedByUserId, RecordedByEmail = c.DecidedByEmail ?? "unknown",
            });
        }
        await _db.SaveChangesAsync(ct);
        await _support.RecomputeAsync(s, ct);
        await _support.RefreshOvertimeAsync(w.Id, now, AttendanceSupport.SystemActor, ct);
        return null;
    }

    // ─── Dashboard statistics ──────────────────────────────────────────────

    public async Task<AttendanceStatisticsDto> StatisticsAsync(AccessContext caller, Guid? factoryId, string? lineId, DateOnly? date, CancellationToken ct)
    {
        var now = Now;
        var tzFactory = factoryId ?? (caller.FactoryIds.Count == 1 ? caller.FactoryIds.First() : (Guid?)null);
        var policy = tzFactory is { } tf ? await _support.PolicyAsync(tf, ct) : new AttendancePolicy();
        var tz = AttendanceSupport.Zone(policy);
        var day = date ?? AttendanceCalculator.LocalDate(now, tz);
        var allowed = caller.Has(Permissions.AttendanceView);

        IQueryable<T> Filter<T>(IQueryable<T> q, Func<IQueryable<T>, Guid, IQueryable<T>> byFactory, Func<IQueryable<T>, string, IQueryable<T>> byLine)
        {
            if (factoryId is { } f) q = byFactory(q, f);
            if (!string.IsNullOrWhiteSpace(lineId)) q = byLine(q, lineId.Trim());
            return q;
        }

        var sessions = Filter(allowed ? _support.Sessions(caller) : _db.AttendanceSessions.Where(_ => false), (q, f) => q.Where(s => s.FactoryId == f), (q, l) => q.Where(s => s.LineId == l));
        var today = sessions.Where(s => s.WorkDate == day);
        var present = await today.Where(s => s.CheckInAt != null).Select(s => s.WorkerId).Distinct().CountAsync(ct);
        var late = await today.Where(s => s.IsLate).Select(s => s.WorkerId).Distinct().CountAsync(ct);
        var missing = await today.Where(s => s.NeedsReview).CountAsync(ct);
        var checkedIn = await sessions.CountAsync(s => s.Status == SessionStatuses.Open, ct);
        var openIds = sessions.Where(s => s.Status == SessionStatuses.Open).Select(s => s.Id);
        var onBreakRows = await _db.BreakSessions.AsNoTracking().Where(b => b.Status == BreakStatuses.Active && openIds.Contains(b.AttendanceSessionId)).ToListAsync(ct);
        var overdue = onBreakRows.Where(b => now > b.StartAt.AddMinutes(b.AllowedMinutes)).ToList();

        // Scheduled: active workers in scope whose shift works on this day; absent: scheduled, shift already started (+ grace), no session.
        var workers = Filter(allowed ? _support.Workers(caller) : _db.Workers.Where(_ => false), (q, f) => q.Where(w => w.FactoryId == f), (q, l) => q.Where(w => w.LineId == l))
            .Where(w => w.Status == WorkerStatuses.Active);
        var assigned = await (from a in _db.ShiftAssignments.AsNoTracking()
                              join w in workers on a.WorkerId equals w.Id
                              join s in _db.Shifts.AsNoTracking() on a.ShiftId equals s.Id
                              where a.EffectiveFrom <= day && (a.EffectiveTo == null || a.EffectiveTo >= day)
                              select new { a.WorkerId, s.StartLocal, s.EndLocal, s.LateGraceMinutes, s.WorkingDays, a.FactoryId }).ToListAsync(ct);
        var scheduled = assigned.Where(a => AttendanceRules.WorksOn(a.WorkingDays, day.DayOfWeek)).ToList();
        var withSession = (await today.Select(s => s.WorkerId).Distinct().ToListAsync(ct)).ToHashSet();
        var absent = scheduled.Count(a => !withSession.Contains(a.WorkerId)
                                          && now > AttendanceCalculator.ToUtc(day, a.StartLocal, tz).AddMinutes(a.LateGraceMinutes));

        var ot = Filter(allowed || caller.Has(Permissions.OvertimeView)
                ? AttendanceSupport.Scoped(_db.OvertimeRequests.AsNoTracking(), caller, (q, f) => q.Where(o => f.Contains(o.FactoryId)),
                    (q, f, l) => q.Where(o => f.Contains(o.FactoryId) && o.LineId != null && l.Contains(o.LineId)))
                : _db.OvertimeRequests.Where(_ => false),
            (q, f) => q.Where(o => o.FactoryId == f), (q, l) => q.Where(o => o.LineId == l));
        var pendingOt = await ot.CountAsync(o => o.Status == OvertimeStatuses.PendingApproval, ct);
        var completedOt = await ot.CountAsync(o => o.Status == OvertimeStatuses.Completed && o.WorkDate == day, ct);
        var workerIds = workers.Select(w => w.Id);
        var pendingCorrections = await _db.AttendanceCorrections.CountAsync(c => c.Status == CorrectionStatuses.Pending && workerIds.Contains(c.WorkerId), ct);

        var (dayStart, dayEnd) = (AttendanceCalculator.ToUtc(day, TimeOnly.MinValue, tz), AttendanceCalculator.ToUtc(day.AddDays(1), TimeOnly.MinValue, tz));
        var sessionIds = today.Select(s => s.Id);
        var bySource = await _db.AttendanceEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= dayStart && e.OccurredAt < dayEnd && e.SessionId != null && sessionIds.Contains(e.SessionId.Value))
            .GroupBy(e => e.Source).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        return new AttendanceStatisticsDto(day, factoryId, scheduled.Count, present, absent, late, checkedIn, missing, onBreakRows.Count, overdue.Count,
            pendingOt, completedOt, pendingCorrections, bySource.GetValueOrDefault(AttendanceSources.Simulator), bySource.GetValueOrDefault(AttendanceSources.Device),
            bySource.GetValueOrDefault(AttendanceSources.Manual), await _support.BreakDtosAsync(overdue, now, ct));
    }

    // ─── Reports ───────────────────────────────────────────────────────────

    public async Task<AccessResult<AttendanceReportDto>> ReportAsync(AccessContext caller, Guid? factoryId, string? lineId, Guid? departmentId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) return AccessResult<AttendanceReportDto>.Invalid("invalid_period", "The end date can't be before the start date.");
        if (to.DayNumber - from.DayNumber > 92) return AccessResult<AttendanceReportDto>.Invalid("period_too_long", "A report covers at most 93 days.");
        var sessions = _support.Sessions(caller).Where(s => s.WorkDate >= from && s.WorkDate <= to);
        if (factoryId is { } f) sessions = sessions.Where(s => s.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(lineId)) sessions = sessions.Where(s => s.LineId == lineId.Trim());
        if (departmentId is { } d) sessions = sessions.Where(s => _db.Workers.Any(w => w.Id == s.WorkerId && w.DepartmentId == d));
        var rows = await sessions.Select(s => new
        {
            s.WorkerId, s.LineId, s.WorkDate, s.CheckInAt, s.IsLate, s.LateMinutes, s.IsEarlyDeparture, s.EarlyDepartureMinutes, s.NeedsReview,
            s.AttendanceMinutes, s.BreakMinutes, s.NetWorkingMinutes, s.BeyondShiftMinutes, s.HasSimulatedEvents,
        }).ToListAsync(ct);
        var workerIds = rows.Select(r => r.WorkerId).Distinct().ToList();
        var ot = await _db.OvertimeRequests.AsNoTracking()
            .Where(o => workerIds.Contains(o.WorkerId) && o.WorkDate >= from && o.WorkDate <= to
                        && (o.Status == OvertimeStatuses.Approved || o.Status == OvertimeStatuses.InProgress || o.Status == OvertimeStatuses.Completed))
            .Select(o => new { o.WorkerId, o.ApprovedMinutes, o.ActualMinutes, o.PayableMinutes }).ToListAsync(ct);
        var names = await _support.WorkerNamesAsync(workerIds, ct);
        var report = rows.GroupBy(r => r.WorkerId).Select(g =>
        {
            var (code, name) = names.GetValueOrDefault(g.Key, ("?", "Unknown worker"));
            var o = ot.Where(x => x.WorkerId == g.Key).ToList();
            return new AttendanceReportRow(g.Key, code, name, g.Select(x => x.LineId).LastOrDefault(x => x is not null), g.Count(),
                g.Where(x => x.CheckInAt != null).Select(x => x.WorkDate).Distinct().Count(),
                g.Count(x => x.IsLate), g.Sum(x => x.LateMinutes), g.Count(x => x.IsEarlyDeparture), g.Sum(x => x.EarlyDepartureMinutes),
                g.Count(x => x.NeedsReview), g.Sum(x => x.AttendanceMinutes), g.Sum(x => x.BreakMinutes), g.Sum(x => x.NetWorkingMinutes),
                g.Sum(x => x.BeyondShiftMinutes), o.Sum(x => x.ApprovedMinutes ?? 0), o.Sum(x => x.ActualMinutes ?? 0), o.Sum(x => x.PayableMinutes ?? 0),
                g.Count(x => x.HasSimulatedEvents));
        }).OrderBy(r => r.WorkerCode, StringComparer.Ordinal).ToList();
        var totals = new AttendanceReportRow(Guid.Empty, "TOTAL", $"{report.Count} worker(s)", null, report.Sum(r => r.Sessions), report.Sum(r => r.DaysPresent),
            report.Sum(r => r.LateDays), report.Sum(r => r.LateMinutes), report.Sum(r => r.EarlyDepartureDays), report.Sum(r => r.EarlyDepartureMinutes),
            report.Sum(r => r.MissingPunches), report.Sum(r => r.AttendanceMinutes), report.Sum(r => r.BreakMinutes), report.Sum(r => r.NetWorkingMinutes),
            report.Sum(r => r.BeyondShiftMinutes), report.Sum(r => r.ApprovedOvertimeMinutes), report.Sum(r => r.ActualOvertimeMinutes),
            report.Sum(r => r.PayableConfirmedMinutes), report.Sum(r => r.SimulatedSessions));
        return AccessResult<AttendanceReportDto>.Ok(new AttendanceReportDto(from, to, factoryId, report, totals));
    }

    public async Task<AccessResult<AttendanceExport>> ExportReportAsync(AccessContext caller, Guid? factoryId, string? lineId, Guid? departmentId, DateOnly from, DateOnly to, string? traceId, CancellationToken ct)
    {
        var report = await ReportAsync(caller, factoryId, lineId, departmentId, from, to, ct);
        if (report.Outcome != AccessOutcome.Ok) return new AccessResult<AttendanceExport>(report.Outcome, null, report.Code, report.Message);
        var sb = new StringBuilder();
        sb.Append(Csv.Line(new[]
        {
            "worker_id", "full_name", "line", "sessions", "days_present", "late_days", "late_minutes", "early_departure_days", "early_departure_minutes",
            "missing_punches", "attendance_minutes", "break_minutes", "net_working_minutes", "beyond_shift_minutes", "approved_overtime_minutes",
            "actual_overtime_minutes", "payable_overtime_minutes_hr_confirmed", "sessions_with_simulated_events",
        })).Append("\r\n");
        foreach (var r in report.Value!.Rows.Append(report.Value.Totals))
            sb.Append(Csv.Line(new[]
            {
                r.WorkerCode, r.WorkerName, r.LineId, I(r.Sessions), I(r.DaysPresent), I(r.LateDays), I(r.LateMinutes), I(r.EarlyDepartureDays),
                I(r.EarlyDepartureMinutes), I(r.MissingPunches), I(r.AttendanceMinutes), I(r.BreakMinutes), I(r.NetWorkingMinutes), I(r.BeyondShiftMinutes),
                I(r.ApprovedOvertimeMinutes), I(r.ActualOvertimeMinutes), I(r.PayableConfirmedMinutes), I(r.SimulatedSessions),
            })).Append("\r\n");
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.report_exported", AccessAudit.EntityAttendanceSession, "report", null, null,
            $"attendance report {from:yyyy-MM-dd}..{to:yyyy-MM-dd} exported ({report.Value.Rows.Count} worker(s))", traceId,
            factoryId ?? (caller.FactoryIds.Count == 1 ? caller.FactoryIds.First() : null)));
        await _db.SaveChangesAsync(ct);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return AccessResult<AttendanceExport>.Ok(new AttendanceExport($"attendance-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", bytes, report.Value.Rows.Count));
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    // ─── Policy ────────────────────────────────────────────────────────────

    public async Task<AccessResult<AttendancePolicyDto>> PolicyAsync(AccessContext caller, Guid factoryId, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId) || caller.Scope < RoleScope.Line || !await _db.Factories.AnyAsync(f => f.Id == factoryId, ct))
            return AccessResult<AttendancePolicyDto>.NotFound("factory_not_found", "Factory not found.");
        var stored = await _db.AttendancePolicies.AsNoTracking().FirstOrDefaultAsync(p => p.FactoryId == factoryId, ct);
        return AccessResult<AttendancePolicyDto>.Ok(PolicyDto(stored ?? new AttendancePolicy { FactoryId = factoryId }, stored is null));
    }

    public async Task<AccessResult<AttendancePolicyDto>> UpdatePolicyAsync(AccessContext caller, Guid factoryId, UpdatePolicyRequest r, string? traceId, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId) || caller.Scope < RoleScope.Factory || !await _db.Factories.AnyAsync(f => f.Id == factoryId, ct))
            return AccessResult<AttendancePolicyDto>.NotFound("factory_not_found", "Factory not found.");
        var p = await _db.AttendancePolicies.FirstOrDefaultAsync(x => x.FactoryId == factoryId, ct);
        if (p is null) { p = new AttendancePolicy { FactoryId = factoryId }; _db.AttendancePolicies.Add(p); }
        p.TimeZoneId = r.TimeZoneId.Trim();
        p.DuplicateWindowSeconds = r.DuplicateWindowSeconds;
        p.MissingCheckOutAfterMinutes = r.MissingCheckOutAfterMinutes;
        p.MaxOpenSessionHours = r.MaxOpenSessionHours;
        p.MaxBackdateDays = r.MaxBackdateDays;
        p.EarlyCheckInWindowMinutes = r.EarlyCheckInWindowMinutes;
        p.OvertimeRoundingMinutes = r.OvertimeRoundingMinutes;
        p.MinOvertimeMinutes = r.MinOvertimeMinutes;
        p.MaxDailyOvertimeMinutes = r.MaxDailyOvertimeMinutes;
        p.MaxWeeklyOvertimeMinutes = r.MaxWeeklyOvertimeMinutes;
        p.UpdatedAt = Now;
        p.UpdatedByEmail = AccessAudit.Clip(caller.Email, 256);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.policy_updated", AccessAudit.EntityAttendancePolicy, factoryId.ToString(), null, null,
            $"attendance policy: time zone {p.TimeZoneId}, duplicate window {p.DuplicateWindowSeconds}s, missing check-out after {p.MissingCheckOutAfterMinutes} min, " +
            $"overtime rounding {p.OvertimeRoundingMinutes} min, daily OT warning {p.MaxDailyOvertimeMinutes} min", traceId, factoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<AttendancePolicyDto>.Ok(PolicyDto(p, false));
    }

    private static AttendancePolicyDto PolicyDto(AttendancePolicy p, bool isDefault) => new(
        p.FactoryId, p.TimeZoneId, p.DuplicateWindowSeconds, p.MissingCheckOutAfterMinutes, p.MaxOpenSessionHours, p.MaxBackdateDays,
        p.EarlyCheckInWindowMinutes, p.OvertimeRoundingMinutes, p.MinOvertimeMinutes, p.MaxDailyOvertimeMinutes, p.MaxWeeklyOvertimeMinutes,
        isDefault, isDefault ? null : p.UpdatedAt, p.UpdatedByEmail);

    // ─── Readers ───────────────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<ReaderDto>>> ReadersAsync(AccessContext caller, Guid? factoryId, bool simulatedOnly, CancellationToken ct)
    {
        if (simulatedOnly)
        {
            // The simulator always has an entry and an exit reader to offer.
            var factories = factoryId is { } only ? new List<Guid> { only } : caller.FactoryIds.ToList();
            foreach (var f in factories.Where(caller.CanAccessFactory))
                if (await _db.Factories.AnyAsync(x => x.Id == f && x.IsActive, ct))
                {
                    await EnsureSimulatedReaderAsync(f, "Entry", ct);
                    await EnsureSimulatedReaderAsync(f, "Exit", ct);
                }
        }
        var readers = _db.AttendanceReaders.AsNoTracking().AsQueryable();
        if (!caller.IsPlatform)
        {
            var mine = caller.FactoryIds.ToList();
            readers = readers.Where(r => mine.Contains(r.FactoryId));
        }
        if (factoryId is { } fid) readers = readers.Where(r => r.FactoryId == fid);
        if (simulatedOnly) readers = readers.Where(r => r.IsSimulated);
        var rows = await readers.OrderBy(r => r.IsSimulated).ThenBy(r => r.Code)
            .Select(r => new ReaderDto(r.Id, r.FactoryId, r.Code, r.Name, r.Direction, r.Kind, r.IsSimulated, r.Location, r.IsActive)).ToListAsync(ct);
        return AccessResult<IReadOnlyList<ReaderDto>>.Ok(rows);
    }

    public async Task<AccessResult<ReaderDto>> CreateReaderAsync(AccessContext caller, CreateReaderRequest r, string? traceId, CancellationToken ct)
    {
        if (caller.Scope < RoleScope.Factory || !caller.CanAccessFactory(r.FactoryId))
            return AccessResult<ReaderDto>.Forbidden("factory_out_of_scope", "You can only add readers to your own factory.");
        var code = r.Code.Trim().ToUpperInvariant();
        if (await _db.AttendanceReaders.AnyAsync(x => x.FactoryId == r.FactoryId && x.Code == code, ct))
            return AccessResult<ReaderDto>.Conflict("reader_code_taken", $"This factory already has a reader {code}.");
        var reader = new AttendanceReader
        {
            FactoryId = r.FactoryId, Code = code, Name = r.Name.Trim(), Direction = r.Direction, Kind = r.Kind, IsSimulated = r.Kind == "Simulated",
            Location = string.IsNullOrWhiteSpace(r.Location) ? null : r.Location.Trim(),
        };
        _db.AttendanceReaders.Add(reader);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.reader_created", AccessAudit.EntityAttendanceReader, reader.Id.ToString(), null, reader.Kind,
            $"{reader.Kind} reader {code} ({reader.Direction}) registered", traceId, r.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<ReaderDto>.Created(new ReaderDto(reader.Id, reader.FactoryId, reader.Code, reader.Name, reader.Direction, reader.Kind, reader.IsSimulated, reader.Location, reader.IsActive));
    }

    public async Task<AccessResult<ReaderDto>> UpdateReaderAsync(AccessContext caller, Guid id, UpdateReaderRequest r, string? traceId, CancellationToken ct)
    {
        var reader = await _db.AttendanceReaders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (reader is null || !caller.CanAccessFactory(reader.FactoryId) || caller.Scope < RoleScope.Factory)
            return AccessResult<ReaderDto>.NotFound("reader_not_found", "Reader not found.");
        reader.Name = r.Name.Trim();
        reader.Direction = r.Direction;
        reader.Location = string.IsNullOrWhiteSpace(r.Location) ? null : r.Location.Trim();
        reader.IsActive = r.IsActive;
        reader.UpdatedAt = Now;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.reader_updated", AccessAudit.EntityAttendanceReader, reader.Id.ToString(), null,
            reader.IsActive ? "active" : "inactive", $"reader {reader.Code} updated", traceId, reader.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<ReaderDto>.Ok(new ReaderDto(reader.Id, reader.FactoryId, reader.Code, reader.Name, reader.Direction, reader.Kind, reader.IsSimulated, reader.Location, reader.IsActive));
    }
}
