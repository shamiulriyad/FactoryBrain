using BunonBrain.Application.Abstractions;
using System.Globalization;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Attendance;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Attendance;

/// <summary>
/// Shared by the shift, attendance and overtime services: who may see what,
/// the factory policy, which shift an instant belongs to, recomputing a
/// session, updating overtime from attendance, and the read models.
/// </summary>
internal sealed class AttendanceSupport
{
    private readonly BunonBrainDbContext _db;
    public AttendanceSupport(BunonBrainDbContext db) { _db = db; }

    public static readonly AuditActor SystemActor = new(null, "system:attendance", "System");

    // ─── Scope ─────────────────────────────────────────────────────────────

    public sealed record WorkerRow(Guid Id, Guid FactoryId, string? LineId, Guid? DepartmentId, Guid? UserId, string Status, string WorkerCode, string FullName);

    public Task<WorkerRow?> WorkerAsync(Guid id, CancellationToken ct) =>
        _db.Workers.AsNoTracking().Where(w => w.Id == id)
            .Select(w => new WorkerRow(w.Id, w.FactoryId, w.LineId, w.DepartmentId, w.UserId, w.Status, w.WorkerCode, w.FullName)).FirstOrDefaultAsync(ct);

    public async Task<WorkerRow?> WorkerByIdOrCodeAsync(AccessContext caller, string idOrCode, CancellationToken ct)
    {
        var q = _db.Workers.AsNoTracking();
        if (idOrCode.Equals("me", StringComparison.OrdinalIgnoreCase)) q = q.Where(w => w.UserId == caller.UserId);
        else if (Guid.TryParse(idOrCode, out var id)) q = q.Where(w => w.Id == id);
        else if (WorkerRules.LooksLikeWorkerCode(idOrCode))
        {
            var code = WorkerRules.NormaliseWorkerCode(idOrCode);
            q = q.Where(w => w.WorkerCode == code);
        }
        else return null;
        return await q.Select(w => new WorkerRow(w.Id, w.FactoryId, w.LineId, w.DepartmentId, w.UserId, w.Status, w.WorkerCode, w.FullName)).FirstOrDefaultAsync(ct);
    }

    /// <summary>The worker is inside the caller's operational scope (a supervisor: on one of their lines).</summary>
    public static bool Reaches(AccessContext caller, Guid factoryId, string? lineId) => caller.Scope switch
    {
        RoleScope.Platform => true,
        RoleScope.Factory => caller.FactoryIds.Contains(factoryId),
        RoleScope.Line => caller.FactoryIds.Contains(factoryId) && lineId is not null && caller.LineIds.Contains(lineId),
        _ => false,
    };

    public static bool IsOwn(AccessContext caller, WorkerRow w) => w.UserId == caller.UserId && caller.Has(Permissions.SelfView);

    /// <summary>Records partitioned by factory and (for supervisors) by the line snapshot.</summary>
    public static IQueryable<T> Scoped<T>(IQueryable<T> rows, AccessContext caller, Func<IQueryable<T>, List<Guid>, IQueryable<T>> byFactory,
        Func<IQueryable<T>, List<Guid>, List<string>, IQueryable<T>> byLine)
    {
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => rows,
            RoleScope.Factory => byFactory(rows, factories),
            RoleScope.Line => byLine(rows, factories, lines),
            _ => rows.Where(_ => false),
        };
    }

    public IQueryable<AttendanceSession> Sessions(AccessContext caller) =>
        Scoped(_db.AttendanceSessions.AsNoTracking(), caller,
            (q, f) => q.Where(s => f.Contains(s.FactoryId)),
            (q, f, l) => q.Where(s => f.Contains(s.FactoryId) && s.LineId != null && l.Contains(s.LineId)));

    public IQueryable<WorkerProfile> Workers(AccessContext caller) =>
        Scoped(_db.Workers.AsNoTracking(), caller,
            (q, f) => q.Where(w => f.Contains(w.FactoryId)),
            (q, f, l) => q.Where(w => f.Contains(w.FactoryId) && w.LineId != null && l.Contains(w.LineId)));

    // ─── Policy and time zone ──────────────────────────────────────────────

    public async Task<AttendancePolicy> PolicyAsync(Guid factoryId, CancellationToken ct) =>
        await _db.AttendancePolicies.AsNoTracking().FirstOrDefaultAsync(p => p.FactoryId == factoryId, ct)
        ?? new AttendancePolicy { FactoryId = factoryId };

    public static TimeZoneInfo Zone(AttendancePolicy p) =>
        AttendanceRules.ResolveTimeZone(p.TimeZoneId) ?? AttendanceRules.ResolveTimeZone(AttendanceRules.DefaultTimeZone) ?? TimeZoneInfo.Utc;

    // ─── Shifts ────────────────────────────────────────────────────────────

    public static ShiftSpec Spec(Shift s) => new(s.Id, s.StartLocal, s.EndLocal, s.LateGraceMinutes, s.EarlyLeaveGraceMinutes, s.WorkingDays,
        s.Breaks.OrderBy(b => b.SortOrder).Select(b => new BreakSpec(b.StartLocal, b.DurationMinutes, b.IsPaid)).ToList());

    public sealed record ShiftContext(WorkerShiftAssignment Assignment, Shift Shift);

    /// <summary>The worker's shift assignments that cover any of the given dates, with their shifts and breaks.</summary>
    public async Task<List<ShiftContext>> AssignmentsAsync(Guid workerId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await _db.ShiftAssignments.AsNoTracking()
            .Where(a => a.WorkerId == workerId && a.EffectiveFrom <= to && (a.EffectiveTo == null || a.EffectiveTo >= from))
            .ToListAsync(ct);
        var shiftIds = rows.Select(r => r.ShiftId).Distinct().ToList();
        var shifts = await _db.Shifts.AsNoTracking().Include(s => s.Breaks).Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        return rows.Where(r => shifts.ContainsKey(r.ShiftId)).Select(r => new ShiftContext(r, shifts[r.ShiftId])).ToList();
    }

    public static ShiftContext? On(List<ShiftContext> contexts, DateOnly date) =>
        contexts.FirstOrDefault(c => c.Assignment.EffectiveFrom <= date && (c.Assignment.EffectiveTo is null || c.Assignment.EffectiveTo >= date));

    public sealed record Placement(DateOnly WorkDate, ShiftContext? Context, ShiftOccurrence? Occurrence);

    /// <summary>Which work date and shift occurrence an instant belongs to (none: the local date, unscheduled).</summary>
    public async Task<Placement> PlaceAsync(Guid workerId, DateTimeOffset at, AttendancePolicy policy, CancellationToken ct)
    {
        var tz = Zone(policy);
        var day = AttendanceCalculator.LocalDate(at, tz);
        var contexts = await AssignmentsAsync(workerId, day.AddDays(-1), day.AddDays(1), ct);
        var occ = AttendanceCalculator.ResolveOccurrence(at, tz, policy.EarlyCheckInWindowMinutes,
            d => On(contexts, d) is { } c && c.Shift.IsActive ? Spec(c.Shift) : null);
        return occ is null ? new Placement(day, null, null) : new Placement(occ.WorkDate, On(contexts, occ.WorkDate), occ);
    }

    // ─── Recomputing a session ─────────────────────────────────────────────

    public async Task RecomputeAsync(AttendanceSession s, CancellationToken ct)
    {
        Shift? shift = null;
        if (s.ShiftId is { } sid) shift = await _db.Shifts.AsNoTracking().Include(x => x.Breaks).FirstOrDefaultAsync(x => x.Id == sid, ct);
        var occurrence = shift is not null && s.ScheduledStart is { } st && s.ScheduledEnd is { } en ? new ShiftOccurrence(s.WorkDate, st, en) : null;
        var breaks = await _db.BreakSessions.Where(b => b.AttendanceSessionId == s.Id)
            .Select(b => new TakenBreak(b.StartAt, b.EndAt, b.AllowedMinutes)).ToListAsync(ct);
        // Tracked breaks added in this unit of work are not in the database yet.
        breaks.AddRange(_db.ChangeTracker.Entries<BreakSession>()
            .Where(e => e.State == EntityState.Added && e.Entity.AttendanceSessionId == s.Id)
            .Select(e => new TakenBreak(e.Entity.StartAt, e.Entity.EndAt, e.Entity.AllowedMinutes)));
        var approved = await _db.OvertimeRequests.AsNoTracking()
            .Where(o => o.WorkerId == s.WorkerId && o.WorkDate == s.WorkDate && o.ApprovedMinutes != null
                        && (o.Status == OvertimeStatuses.Approved || o.Status == OvertimeStatuses.InProgress || o.Status == OvertimeStatuses.Completed))
            .Select(o => new OvertimeWindow(o.ApprovedStart!.Value, o.ApprovedEnd!.Value, o.ApprovedMinutes!.Value)).ToListAsync(ct);

        var f = AttendanceCalculator.Calculate(s.CheckInAt, s.CheckOutAt, shift is null ? null : Spec(shift), occurrence, breaks, approved);
        s.AttendanceMinutes = f.AttendanceMinutes;
        s.ScheduledMinutes = f.ScheduledMinutes;
        s.ScheduledBreakMinutes = f.ScheduledBreakMinutes;
        s.BreakMinutes = f.BreakMinutes;
        s.OverBreakMinutes = f.OverBreakMinutes;
        s.NetWorkingMinutes = f.NetWorkingMinutes;
        s.LateMinutes = f.LateMinutes;
        s.EarlyDepartureMinutes = f.EarlyDepartureMinutes;
        s.BeyondShiftMinutes = f.BeyondShiftMinutes;
        s.ApprovedOvertimeMinutes = f.ApprovedOvertimeMinutes;
        s.IsLate = f.IsLate;
        s.IsEarlyDeparture = f.IsEarlyDeparture;
        s.UpdatedAt = DateTimeOffset.UtcNow;
    }

    // ─── Overtime from attendance ──────────────────────────────────────────

    /// <summary>
    /// Brings the worker's approved overtime up to date with attendance:
    /// actual minutes inside the approved window, Approved → InProgress when
    /// the worker is present inside it, → Completed once it has passed (or
    /// the worker checked out after it began). Never sets payable minutes.
    /// </summary>
    public async Task<int> RefreshOvertimeAsync(Guid workerId, DateTimeOffset now, AuditActor actor, CancellationToken ct)
    {
        var open = await _db.OvertimeRequests
            .Where(o => o.WorkerId == workerId && (o.Status == OvertimeStatuses.Approved || o.Status == OvertimeStatuses.InProgress))
            .ToListAsync(ct);
        if (open.Count == 0) return 0;
        var changed = 0;
        foreach (var o in open)
        {
            var (ws, we) = (o.ApprovedStart!.Value, o.ApprovedEnd!.Value);
            var sessions = await SessionsOverlappingAsync(workerId, ws, we, ct);
            var actual = 0;
            var presentNow = false;
            var checkedOutAfterStart = false;
            foreach (var s in sessions)
            {
                var breaks = await _db.BreakSessions.AsNoTracking().Where(b => b.AttendanceSessionId == s.Id)
                    .Select(b => new TakenBreak(b.StartAt, b.EndAt, b.AllowedMinutes)).ToListAsync(ct);
                actual += AttendanceCalculator.ActualOvertimeMinutes(s.CheckInAt, s.CheckOutAt ?? (s.Status == SessionStatuses.Open ? now : null), ws, we, breaks);
                if (s.Status == SessionStatuses.Open && now >= ws) presentNow = true;
                if (s.CheckOutAt is { } co && co > ws) checkedOutAfterStart = true;
            }
            var before = o.Status;
            var next = before;
            if (now >= we || (checkedOutAfterStart && !presentNow && now >= ws)) next = OvertimeStatuses.Completed;
            else if (presentNow) next = OvertimeStatuses.InProgress;
            if (o.ActualMinutes != actual || next != before)
            {
                o.ActualMinutes = actual;
                o.UpdatedAt = now;
                if (next != before)
                {
                    o.Status = next;
                    _db.OvertimeApprovals.Add(new OvertimeApproval
                    {
                        RequestId = o.Id, Action = next == OvertimeStatuses.Completed ? "Completed" : "Started", FromStatus = before, ToStatus = next,
                        Minutes = actual, Note = "From attendance", ActorUserId = actor.UserId, ActorEmail = actor.Email, OccurredAt = now,
                    });
                }
                changed++;
            }
        }
        return changed;
    }

    private async Task<List<AttendanceSession>> SessionsOverlappingAsync(Guid workerId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var stored = await _db.AttendanceSessions.Where(s => s.WorkerId == workerId && s.CheckInAt != null && s.CheckInAt < to
                                                             && (s.CheckOutAt == null || s.CheckOutAt > from)).ToListAsync(ct);
        // Include sessions added or changed in this unit of work.
        foreach (var e in _db.ChangeTracker.Entries<AttendanceSession>().Where(e => e.Entity.WorkerId == workerId && e.State == EntityState.Added))
            if (e.Entity.CheckInAt < to && (e.Entity.CheckOutAt == null || e.Entity.CheckOutAt > from) && stored.All(x => x.Id != e.Entity.Id))
                stored.Add(e.Entity);
        return stored;
    }

    // ─── Read models ───────────────────────────────────────────────────────

    public static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static ShiftRef Ref(Shift s) => new(s.Id, s.Code, s.Name, Hm(s.StartLocal), Hm(s.EndLocal), s.EndLocal <= s.StartLocal);

    public async Task<Dictionary<Guid, (string Code, string Name)>> WorkerNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new()
            : await _db.Workers.AsNoTracking().Where(w => list.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => (w.WorkerCode, w.FullName), ct);
    }

    public async Task<IReadOnlyList<AttendanceSessionDto>> SessionDtosAsync(IReadOnlyList<AttendanceSession> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<AttendanceSessionDto>();
        var names = await WorkerNamesAsync(rows.Select(r => r.WorkerId), ct);
        var shiftIds = rows.Where(r => r.ShiftId != null).Select(r => r.ShiftId!.Value).Distinct().ToList();
        var shifts = shiftIds.Count == 0 ? new Dictionary<Guid, ShiftRef>()
            : (await _db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToListAsync(ct)).ToDictionary(s => s.Id, Ref);
        var ids = rows.Select(r => r.Id).ToList();
        var onBreak = (await _db.BreakSessions.AsNoTracking().Where(b => ids.Contains(b.AttendanceSessionId) && b.Status == BreakStatuses.Active)
            .Select(b => b.AttendanceSessionId).ToListAsync(ct)).ToHashSet();
        return rows.Select(s =>
        {
            var (code, name) = names.GetValueOrDefault(s.WorkerId, ("?", "Unknown worker"));
            return new AttendanceSessionDto(s.Id, s.WorkerId, code, name, s.FactoryId, s.LineId, s.WorkDate,
                s.ShiftId is { } sid ? shifts.GetValueOrDefault(sid) : null, s.ScheduledStart, s.ScheduledEnd, s.CheckInAt, s.CheckOutAt,
                s.CheckInSource, s.CheckOutSource, s.Status, s.NeedsReview, s.ReviewReason, s.HasSimulatedEvents, s.IsCorrected,
                s.IsLate, s.IsEarlyDeparture, onBreak.Contains(s.Id),
                new SessionFiguresDto(s.AttendanceMinutes, s.ScheduledMinutes, s.ScheduledBreakMinutes, Math.Max(0, s.ScheduledMinutes - s.ScheduledBreakMinutes),
                    s.BreakMinutes, s.OverBreakMinutes, s.NetWorkingMinutes, s.LateMinutes, s.EarlyDepartureMinutes, s.BeyondShiftMinutes, s.ApprovedOvertimeMinutes),
                s.CreatedAt);
        }).ToList();
    }

    public async Task<IReadOnlyList<BreakSessionDto>> BreakDtosAsync(IReadOnlyList<BreakSession> rows, DateTimeOffset now, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<BreakSessionDto>();
        var names = await WorkerNamesAsync(rows.Select(r => r.WorkerId), ct);
        return rows.Select(b =>
        {
            var (code, name) = names.GetValueOrDefault(b.WorkerId, ("?", "Unknown worker"));
            var actual = (int)Math.Floor(((b.EndAt ?? now) - b.StartAt).TotalMinutes);
            var over = Math.Max(0, actual - b.AllowedMinutes);
            return new BreakSessionDto(b.Id, b.WorkerId, code, name, b.AttendanceSessionId, b.BreakType, b.StartAt, b.EndAt, b.AllowedMinutes,
                b.EndAt is null ? null : actual, b.Status, b.Status == BreakStatuses.Active && over > 0, over, b.IsSimulated, b.StartSource, b.EndSource);
        }).ToList();
    }

    public async Task<IReadOnlyList<AttendanceEventDto>> EventDtosAsync(IReadOnlyList<AttendanceEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<AttendanceEventDto>();
        var names = await WorkerNamesAsync(rows.Select(r => r.WorkerId), ct);
        var readerIds = rows.Where(r => r.ReaderId != null).Select(r => r.ReaderId!.Value).Distinct().ToList();
        var readers = readerIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.AttendanceReaders.AsNoTracking().Where(r => readerIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Code, ct);
        return rows.Select(e =>
        {
            var (code, name) = names.GetValueOrDefault(e.WorkerId, ("?", "Unknown worker"));
            return new AttendanceEventDto(e.Id, e.WorkerId, code, name, e.FactoryId, e.EventType, e.OccurredAt, e.ReceivedAt, e.Source, e.IsSimulated,
                e.ReaderId is { } r ? readers.GetValueOrDefault(r) : null, e.CardUid, e.Outcome, e.OutcomeCode, e.OutcomeMessage, e.SessionId,
                e.RecordedByEmail, e.Note);
        }).ToList();
    }

    public async Task<IReadOnlyList<CorrectionDto>> CorrectionDtosAsync(IReadOnlyList<AttendanceCorrection> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<CorrectionDto>();
        var names = await WorkerNamesAsync(rows.Select(r => r.WorkerId), ct);
        return rows.Select(c =>
        {
            var (code, name) = names.GetValueOrDefault(c.WorkerId, ("?", "Unknown worker"));
            return new CorrectionDto(c.Id, c.WorkerId, code, name, c.FactoryId, c.SessionId, c.WorkDate, c.ProposedCheckIn, c.ProposedCheckOut,
                c.OriginalCheckIn, c.OriginalCheckOut, c.Reason, c.Status, c.RequestedByEmail, c.RequestedAt, c.DecidedByEmail, c.DecidedAt, c.DecisionNote);
        }).ToList();
    }
}
