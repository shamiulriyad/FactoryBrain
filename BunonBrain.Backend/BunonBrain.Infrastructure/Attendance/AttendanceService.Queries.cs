using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Attendance;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Attendance;

// Reading attendance: sessions, one worker's page, raw events and breaks.
public sealed partial class AttendanceService
{
    public async Task<PagedResult<AttendanceSessionDto>> SessionsAsync(AccessContext caller, AttendanceListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var rows = caller.Has(Permissions.AttendanceView) ? _support.Sessions(caller) : _db.AttendanceSessions.AsNoTracking().Where(_ => false);
        if (q.FactoryId is { } f) rows = rows.Where(s => s.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(q.LineId)) rows = rows.Where(s => s.LineId == q.LineId.Trim());
        if (q.DepartmentId is { } d) rows = rows.Where(s => _db.Workers.Any(w => w.Id == s.WorkerId && w.DepartmentId == d));
        if (q.WorkerId is { } wk) rows = rows.Where(s => s.WorkerId == wk);
        if (q.From is { } from) rows = rows.Where(s => s.WorkDate >= from);
        if (q.To is { } to) rows = rows.Where(s => s.WorkDate <= to);
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            var status = SessionStatuses.Parse(q.Status);
            rows = status is null ? rows.Where(_ => false) : rows.Where(s => s.Status == status);
        }
        if (q.NeedsReview is { } nr) rows = rows.Where(s => s.NeedsReview == nr);
        if (q.Late is { } late) rows = rows.Where(s => s.IsLate == late);
        if (q.Simulated is { } sim) rows = rows.Where(s => s.HasSimulatedEvents == sim);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            rows = rows.Where(s => _db.Workers.Any(w => w.Id == s.WorkerId && (EF.Functions.ILike(w.WorkerCode, pattern, "\\") || EF.Functions.ILike(w.FullName, pattern, "\\"))));
        }
        rows = (q.Sort?.Trim() ?? "-date") switch
        {
            "date" => rows.OrderBy(s => s.WorkDate).ThenBy(s => s.CheckInAt),
            "checkIn" => rows.OrderBy(s => s.CheckInAt == null).ThenBy(s => s.CheckInAt),
            "-checkIn" => rows.OrderBy(s => s.CheckInAt == null).ThenByDescending(s => s.CheckInAt),
            "late" => rows.OrderByDescending(s => s.LateMinutes).ThenByDescending(s => s.WorkDate),
            "worker" => rows.OrderBy(s => _db.Workers.Where(w => w.Id == s.WorkerId).Select(w => w.WorkerNumber).FirstOrDefault()).ThenByDescending(s => s.WorkDate),
            "-worker" => rows.OrderByDescending(s => _db.Workers.Where(w => w.Id == s.WorkerId).Select(w => w.WorkerNumber).FirstOrDefault()).ThenByDescending(s => s.WorkDate),
            _ => rows.OrderByDescending(s => s.WorkDate).ThenByDescending(s => s.CheckInAt ?? s.CheckOutAt),
        };
        var total = await rows.CountAsync(ct);
        var list = await rows.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<AttendanceSessionDto>(await _support.SessionDtosAsync(list, ct), page, size, total);
    }

    public async Task<AccessResult<AttendanceSessionDto>> SessionAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var s = await _db.AttendanceSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return SessionNotFound<AttendanceSessionDto>();
        var visible = caller.Has(Permissions.AttendanceView) && await _support.Sessions(caller).AnyAsync(x => x.Id == id, ct);
        if (!visible)
        {
            var w = await _support.WorkerAsync(s.WorkerId, ct);
            if (w is null || !AttendanceSupport.IsOwn(caller, w)) return SessionNotFound<AttendanceSessionDto>();
        }
        return AccessResult<AttendanceSessionDto>.Ok((await _support.SessionDtosAsync(new[] { s }, ct))[0]);
    }

    private static AccessResult<T> SessionNotFound<T>() => AccessResult<T>.NotFound("session_not_found", "Attendance session not found.");

    public async Task<AccessResult<WorkerAttendanceDto>> WorkerAsync(AccessContext caller, string workerIdOrCode, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var w = await _support.WorkerByIdOrCodeAsync(caller, workerIdOrCode, ct);
        if (w is null || !(AttendanceSupport.IsOwn(caller, w) || (caller.Has(Permissions.AttendanceView) && AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId))))
            return AccessResult<WorkerAttendanceDto>.NotFound("worker_not_found", "Worker not found.");
        var policy = await _support.PolicyAsync(w.FactoryId, ct);
        var tz = AttendanceSupport.Zone(policy);
        var today = AttendanceCalculator.LocalDate(Now, tz);
        var (f, t) = (from ?? today.AddDays(-30), to ?? today);

        var shifts = new ShiftService(_db, _clock);
        var history = await _db.ShiftAssignments.AsNoTracking().Where(a => a.WorkerId == w.Id).OrderByDescending(a => a.EffectiveFrom).Take(50).ToListAsync(ct);
        var historyDtos = await shifts.AssignmentDtosAsync(history, ct);
        var current = historyDtos.FirstOrDefault(a => a.EffectiveFrom <= today && (a.EffectiveTo is null || a.EffectiveTo >= today));

        var sessions = await _db.AttendanceSessions.AsNoTracking().Where(s => s.WorkerId == w.Id && s.WorkDate >= f && s.WorkDate <= t)
            .OrderByDescending(s => s.WorkDate).ThenByDescending(s => s.CheckInAt ?? s.CheckOutAt).Take(200).ToListAsync(ct);
        var open = await _db.AttendanceSessions.AsNoTracking().FirstOrDefaultAsync(s => s.WorkerId == w.Id && s.Status == SessionStatuses.Open, ct);
        var sessionIds = sessions.Select(s => s.Id).Concat(open is null ? Array.Empty<Guid>() : new[] { open.Id }).ToList();
        var breaks = await _db.BreakSessions.AsNoTracking().Where(b => sessionIds.Contains(b.AttendanceSessionId)).OrderByDescending(b => b.StartAt).ToListAsync(ct);
        var overtime = new OvertimeService(_db, _clock);
        var ot = await _db.OvertimeRequests.AsNoTracking().Where(o => o.WorkerId == w.Id && o.WorkDate >= f.AddDays(-7) && o.WorkDate <= t.AddDays(31))
            .OrderByDescending(o => o.WorkDate).Take(50).ToListAsync(ct);
        var corrections = await _db.AttendanceCorrections.AsNoTracking().Where(c => c.WorkerId == w.Id).OrderByDescending(c => c.RequestedAt).Take(50).ToListAsync(ct);

        return AccessResult<WorkerAttendanceDto>.Ok(new WorkerAttendanceDto(
            w.Id, w.WorkerCode, w.FullName, w.FactoryId, w.LineId, w.Status, policy.TimeZoneId, current, historyDtos,
            open is null ? null : (await _support.SessionDtosAsync(new[] { open }, ct))[0],
            await _support.SessionDtosAsync(sessions, ct),
            await _support.BreakDtosAsync(breaks, Now, ct),
            await overtime.DtosAsync(caller, ot, ct),
            await _support.CorrectionDtosAsync(corrections, ct)));
    }

    public async Task<PagedResult<AttendanceEventDto>> EventsAsync(AccessContext caller, EventListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var events = caller.Has(Permissions.AttendanceView)
            ? AttendanceSupport.Scoped(_db.AttendanceEvents.AsNoTracking(), caller,
                (x, fs) => x.Where(e => fs.Contains(e.FactoryId)),
                (x, fs, ls) => x.Where(e => fs.Contains(e.FactoryId) && _db.Workers.Any(w => w.Id == e.WorkerId && w.LineId != null && ls.Contains(w.LineId))))
            : _db.AttendanceEvents.AsNoTracking().Where(_ => false);
        if (q.FactoryId is { } f) events = events.Where(e => e.FactoryId == f);
        if (q.WorkerId is { } w) events = events.Where(e => e.WorkerId == w);
        if (q.Simulated is { } sim) events = events.Where(e => e.IsSimulated == sim);
        if (!string.IsNullOrWhiteSpace(q.Source)) events = events.Where(e => e.Source == q.Source.Trim());
        if (!string.IsNullOrWhiteSpace(q.Outcome)) events = events.Where(e => e.Outcome == q.Outcome.Trim());
        if (q.From is { } from) events = events.Where(e => e.OccurredAt >= from);
        if (q.To is { } to) events = events.Where(e => e.OccurredAt <= to);
        var total = await events.CountAsync(ct);
        var rows = await events.OrderByDescending(e => e.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<AttendanceEventDto>(await _support.EventDtosAsync(rows, ct), page, size, total);
    }

    public async Task<PagedResult<BreakSessionDto>> BreaksAsync(AccessContext caller, BreakListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var now = Now;
        var visibleSessions = _support.Sessions(caller).Select(s => s.Id);
        var rows = caller.Has(Permissions.AttendanceView)
            ? _db.BreakSessions.AsNoTracking().Where(b => visibleSessions.Contains(b.AttendanceSessionId))
            : _db.BreakSessions.AsNoTracking().Where(_ => false);
        if (q.FactoryId is { } f) rows = rows.Where(b => b.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(q.LineId)) rows = rows.Where(b => _db.AttendanceSessions.Any(s => s.Id == b.AttendanceSessionId && s.LineId == q.LineId.Trim()));
        if (q.WorkerId is { } w) rows = rows.Where(b => b.WorkerId == w);
        if (q.Active is { } a) rows = a ? rows.Where(b => b.Status == BreakStatuses.Active) : rows.Where(b => b.Status != BreakStatuses.Active);
        if (q.Overdue == true)
        {
            // Overdue: still running past its allowance.
            rows = rows.Where(b => b.Status == BreakStatuses.Active && b.StartAt.AddMinutes(b.AllowedMinutes) < now);
        }
        if (q.From is { } from) rows = rows.Where(b => _db.AttendanceSessions.Any(s => s.Id == b.AttendanceSessionId && s.WorkDate >= from));
        if (q.To is { } to) rows = rows.Where(b => _db.AttendanceSessions.Any(s => s.Id == b.AttendanceSessionId && s.WorkDate <= to));
        var total = await rows.CountAsync(ct);
        var list = await rows.OrderByDescending(b => b.Status == BreakStatuses.Active).ThenByDescending(b => b.StartAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<BreakSessionDto>(await _support.BreakDtosAsync(list, now, ct), page, size, total);
    }
}
