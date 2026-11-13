using System.Globalization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Attendance;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Attendance;

/// <summary>
/// Shifts (configuring them needs <c>shifts.manage</c> and factory-wide reach)
/// and worker shift assignments (<c>shifts.assign</c>; supervisors: workers
/// on their lines). A worker has at most one shift per date: a new
/// assignment ends the current one the day before it starts (kept as
/// Superseded); one that would overlap a later assignment is refused. The
/// database rejects overlapping dates as well.
/// </summary>
public sealed class ShiftService : IShiftService
{
    private readonly BunonBrainDbContext _db;
    private readonly AttendanceSupport _support;
    private readonly TimeProvider _clock;

    public ShiftService(BunonBrainDbContext db, TimeProvider? clock = null)
    {
        _db = db; _support = new AttendanceSupport(db); _clock = clock ?? TimeProvider.System;
    }

    private static TimeOnly Time(string v) => TimeOnly.ParseExact(v.Trim(), new[] { "HH:mm", "H:mm", "HH:mm:ss" }, CultureInfo.InvariantCulture);
    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static AccessResult<T> ShiftNotFound<T>() => AccessResult<T>.NotFound("shift_not_found", "Shift not found.");

    // ─── Shifts ────────────────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<ShiftDto>>> ListAsync(AccessContext caller, Guid? factoryId, bool includeInactive, CancellationToken ct)
    {
        if (caller.Scope < RoleScope.Line) return AccessResult<IReadOnlyList<ShiftDto>>.Ok(Array.Empty<ShiftDto>());
        var shifts = _db.Shifts.AsNoTracking().Include(s => s.Breaks).AsQueryable();
        if (!caller.IsPlatform)
        {
            var mine = caller.FactoryIds.ToList();
            shifts = shifts.Where(s => mine.Contains(s.FactoryId));
        }
        if (factoryId is { } f) shifts = shifts.Where(s => s.FactoryId == f);
        if (!includeInactive) shifts = shifts.Where(s => s.IsActive);
        var rows = await shifts.OrderBy(s => s.StartLocal).ThenBy(s => s.Name).ToListAsync(ct);
        return AccessResult<IReadOnlyList<ShiftDto>>.Ok(await DtosAsync(rows, ct));
    }

    public async Task<AccessResult<ShiftDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var s = await _db.Shifts.AsNoTracking().Include(x => x.Breaks).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || caller.Scope < RoleScope.Line || !caller.CanAccessFactory(s.FactoryId)) return ShiftNotFound<ShiftDto>();
        return AccessResult<ShiftDto>.Ok((await DtosAsync(new[] { s }, ct))[0]);
    }

    public async Task<AccessResult<ShiftDto>> CreateAsync(AccessContext caller, CreateShiftRequest r, string? traceId, CancellationToken ct)
    {
        if (caller.Scope < RoleScope.Factory || !caller.CanAccessFactory(r.FactoryId))
            return AccessResult<ShiftDto>.Forbidden("factory_out_of_scope", "You can only create shifts for your own factory.");
        if (!await _db.Factories.AnyAsync(f => f.Id == r.FactoryId && f.IsActive, ct))
            return AccessResult<ShiftDto>.Invalid("factory_not_found", "Factory not found or deactivated.");
        if (await CheckProductionShiftAsync(r.ProductionShiftCode, ct) is { } bad) return bad;
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        if (await _db.Shifts.AnyAsync(s => s.FactoryId == r.FactoryId && (s.Code == code || s.Name == name), ct))
            return AccessResult<ShiftDto>.Conflict("shift_taken", "This factory already has a shift with this code or name.");
        var now = _clock.GetUtcNow();
        var shift = new Shift
        {
            FactoryId = r.FactoryId, Code = code, Name = name, StartLocal = Time(r.StartLocal), EndLocal = Time(r.EndLocal),
            LateGraceMinutes = r.LateGraceMinutes, EarlyLeaveGraceMinutes = r.EarlyLeaveGraceMinutes,
            WorkingDays = AttendanceRules.MaskFromNames(r.WorkingDays)!.Value, Description = Clean(r.Description),
            ProductionShiftCode = Clean(r.ProductionShiftCode)?.ToLowerInvariant(), CreatedAt = now, UpdatedAt = now,
        };
        if (SetBreaks(shift, r.Breaks) is { } breakError) return breakError;
        _db.Shifts.Add(shift);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "shift.created", AccessAudit.EntityShift, shift.Id.ToString(), null, "active",
            $"shift {code} ({name}) {r.StartLocal}–{r.EndLocal} created", traceId, r.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<ShiftDto>.Conflict("shift_taken", "This factory already has a shift with this code or name.");
        }
        return AccessResult<ShiftDto>.Created((await DtosAsync(new[] { shift }, ct))[0]);
    }

    public async Task<AccessResult<ShiftDto>> UpdateAsync(AccessContext caller, Guid id, UpdateShiftRequest r, string? traceId, CancellationToken ct)
    {
        var shift = await _db.Shifts.Include(s => s.Breaks).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (shift is null || !caller.CanAccessFactory(shift.FactoryId)) return ShiftNotFound<ShiftDto>();
        if (caller.Scope < RoleScope.Factory) return AccessResult<ShiftDto>.Forbidden("factory_scope_required", "Only factory-level users configure shifts.");
        if (await CheckProductionShiftAsync(r.ProductionShiftCode, ct) is { } bad) return bad;
        var name = r.Name.Trim();
        if (name != shift.Name && await _db.Shifts.AnyAsync(s => s.FactoryId == shift.FactoryId && s.Name == name && s.Id != id, ct))
            return AccessResult<ShiftDto>.Conflict("shift_taken", $"This factory already has a shift named {name}.");
        if (shift.IsActive && !r.IsActive)
        {
            var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            var inUse = await _db.ShiftAssignments.CountAsync(a => a.ShiftId == id && (a.EffectiveTo == null || a.EffectiveTo >= today), ct);
            if (inUse > 0)
                return AccessResult<ShiftDto>.Conflict("shift_in_use", $"{inUse} current or future assignment(s) use this shift. Reassign those workers before deactivating it.");
        }
        var changes = new List<string>();
        void Track(string what, bool changed) { if (changed) changes.Add(what); }
        var (start, end) = (Time(r.StartLocal), Time(r.EndLocal));
        Track("times", start != shift.StartLocal || end != shift.EndLocal);
        Track("grace", r.LateGraceMinutes != shift.LateGraceMinutes || r.EarlyLeaveGraceMinutes != shift.EarlyLeaveGraceMinutes);
        var days = AttendanceRules.MaskFromNames(r.WorkingDays)!.Value;
        Track("working days", days != shift.WorkingDays);
        Track(r.IsActive ? "activated" : "deactivated", r.IsActive != shift.IsActive);
        Track("name", name != shift.Name);
        shift.Name = name;
        shift.StartLocal = start;
        shift.EndLocal = end;
        shift.LateGraceMinutes = r.LateGraceMinutes;
        shift.EarlyLeaveGraceMinutes = r.EarlyLeaveGraceMinutes;
        shift.WorkingDays = days;
        shift.IsActive = r.IsActive;
        shift.Description = Clean(r.Description);
        shift.ProductionShiftCode = Clean(r.ProductionShiftCode)?.ToLowerInvariant();
        if (r.Breaks is not null)
        {
            Track("breaks", true);
            if (SetBreaks(shift, r.Breaks) is { } breakError) return breakError;
        }
        shift.UpdatedAt = _clock.GetUtcNow();
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "shift.updated", AccessAudit.EntityShift, shift.Id.ToString(), null, shift.IsActive ? "active" : "inactive",
            $"shift {shift.Code}: {(changes.Count == 0 ? "details" : string.Join(", ", changes))}", traceId, shift.FactoryId));
        await _db.SaveChangesAsync(ct);
        // Changed times apply to new sessions; existing sessions keep the schedule they were recorded with.
        return AccessResult<ShiftDto>.Ok((await DtosAsync(new[] { shift }, ct))[0]);
    }

    /// <summary>Replace the shift's breaks (ids are kept for breaks that still exist, so break history keeps its link).</summary>
    private AccessResult<ShiftDto>? SetBreaks(Shift shift, IReadOnlyList<ShiftBreakDto>? breaks)
    {
        var wanted = breaks ?? Array.Empty<ShiftBreakDto>();
        var unpaid = wanted.Where(b => !b.IsPaid).Sum(b => b.DurationMinutes);
        var spec = AttendanceSupport.Spec(new Shift { StartLocal = shift.StartLocal, EndLocal = shift.EndLocal });
        if (unpaid >= spec.ScheduledMinutes)
            return AccessResult<ShiftDto>.Invalid("breaks_too_long", "Unpaid breaks can't take up the whole shift.");
        var keep = wanted.Where(b => b.Id is not null).Select(b => b.Id!.Value).ToHashSet();
        shift.Breaks.RemoveAll(b => !keep.Contains(b.Id));
        var order = 0;
        foreach (var b in wanted)
        {
            var row = b.Id is { } bid ? shift.Breaks.FirstOrDefault(x => x.Id == bid) : null;
            if (row is null) { row = new ShiftBreak { ShiftId = shift.Id }; shift.Breaks.Add(row); }
            row.BreakType = BreakTypes.Parse(b.BreakType)!;
            row.Name = b.Name.Trim();
            row.StartLocal = string.IsNullOrWhiteSpace(b.StartLocal) ? null : Time(b.StartLocal);
            row.DurationMinutes = b.DurationMinutes;
            row.IsPaid = b.IsPaid;
            row.SortOrder = order++;
        }
        return null;
    }

    private async Task<AccessResult<ShiftDto>?> CheckProductionShiftAsync(string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        return await _db.ProductionShifts.AnyAsync(p => p.Code == c, ct)
            ? null
            : AccessResult<ShiftDto>.Invalid("production_shift_not_found", $"There is no production-calendar shift '{c}'.");
    }

    private async Task<IReadOnlyList<ShiftDto>> DtosAsync(IReadOnlyList<Shift> rows, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.ShiftAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.ShiftId) && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today))
            .GroupBy(a => a.ShiftId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return rows.Select(s =>
        {
            var spec = AttendanceSupport.Spec(s);
            return new ShiftDto(s.Id, s.FactoryId, s.Code, s.Name, AttendanceSupport.Hm(s.StartLocal), AttendanceSupport.Hm(s.EndLocal), spec.IsOvernight,
                spec.ScheduledMinutes, spec.UnpaidBreakMinutes, Math.Max(0, spec.ScheduledMinutes - spec.UnpaidBreakMinutes),
                s.LateGraceMinutes, s.EarlyLeaveGraceMinutes, AttendanceRules.DayNames(s.WorkingDays), s.IsActive, s.Description, s.ProductionShiftCode,
                s.Breaks.OrderBy(b => b.SortOrder).Select(b => new ShiftBreakDto(b.Id, b.BreakType, b.Name, b.StartLocal is { } t ? AttendanceSupport.Hm(t) : null,
                    b.DurationMinutes, b.IsPaid)).ToList(),
                counts.GetValueOrDefault(s.Id), s.CreatedAt, s.UpdatedAt);
        }).ToList();
    }

    // ─── Assignments ───────────────────────────────────────────────────────

    public async Task<PagedResult<ShiftAssignmentDto>> AssignmentsAsync(AccessContext caller, ShiftAssignmentQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var workers = _support.Workers(caller);
        var rows = _db.ShiftAssignments.AsNoTracking().Where(a => workers.Any(w => w.Id == a.WorkerId));
        if (q.FactoryId is { } f) rows = rows.Where(a => a.FactoryId == f);
        if (q.ShiftId is { } s) rows = rows.Where(a => a.ShiftId == s);
        if (q.WorkerId is { } w) rows = rows.Where(a => a.WorkerId == w);
        if (!string.IsNullOrWhiteSpace(q.LineId)) rows = rows.Where(a => workers.Any(x => x.Id == a.WorkerId && x.LineId == q.LineId.Trim()));
        if (q.ActiveOn is { } d) rows = rows.Where(a => a.EffectiveFrom <= d && (a.EffectiveTo == null || a.EffectiveTo >= d));
        if (!string.IsNullOrWhiteSpace(q.Status)) rows = rows.Where(a => a.Status == q.Status.Trim());
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            rows = rows.Where(a => _db.Workers.Any(x => x.Id == a.WorkerId && (EF.Functions.ILike(x.WorkerCode, pattern, "\\") || EF.Functions.ILike(x.FullName, pattern, "\\"))));
        }
        var total = await rows.CountAsync(ct);
        var list = await rows.OrderByDescending(a => a.EffectiveFrom).ThenByDescending(a => a.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ShiftAssignmentDto>(await AssignmentDtosAsync(list, ct), page, size, total);
    }

    public async Task<AccessResult<IReadOnlyList<ShiftAssignmentDto>>> WorkerAssignmentsAsync(AccessContext caller, string workerIdOrCode, CancellationToken ct)
    {
        var w = await _support.WorkerByIdOrCodeAsync(caller, workerIdOrCode, ct);
        if (w is null || !(AttendanceSupport.IsOwn(caller, w) || (caller.Has(Permissions.AttendanceView) && AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId))))
            return AccessResult<IReadOnlyList<ShiftAssignmentDto>>.NotFound("worker_not_found", "Worker not found.");
        var rows = await _db.ShiftAssignments.AsNoTracking().Where(a => a.WorkerId == w.Id).OrderByDescending(a => a.EffectiveFrom).ToListAsync(ct);
        return AccessResult<IReadOnlyList<ShiftAssignmentDto>>.Ok(await AssignmentDtosAsync(rows, ct));
    }

    public async Task<AccessResult<AssignShiftResult>> AssignAsync(AccessContext caller, AssignShiftRequest r, string? traceId, CancellationToken ct)
    {
        var shift = await _db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.ShiftId, ct);
        if (shift is null || !caller.CanAccessFactory(shift.FactoryId)) return ShiftNotFound<AssignShiftResult>();
        if (!shift.IsActive) return AccessResult<AssignShiftResult>.Conflict("shift_inactive", $"Shift {shift.Code} is deactivated.");

        // Who: explicit workers, or everyone active on a line / in a department of the shift's factory (within the caller's scope).
        var scope = _support.Workers(caller).Where(w => w.FactoryId == shift.FactoryId);
        List<AttendanceSupport.WorkerRow> targets;
        if (r.WorkerIds is { Count: > 0 } ids)
        {
            var found = await _db.Workers.AsNoTracking().Where(w => ids.Contains(w.Id))
                .Select(w => new AttendanceSupport.WorkerRow(w.Id, w.FactoryId, w.LineId, w.DepartmentId, w.UserId, w.Status, w.WorkerCode, w.FullName)).ToListAsync(ct);
            targets = ids.Distinct().Select(id => found.FirstOrDefault(f => f.Id == id) ?? new AttendanceSupport.WorkerRow(id, Guid.Empty, null, null, null, "?", "?", "?")).ToList();
        }
        else
        {
            var group = scope.Where(w => w.Status == WorkerStatuses.Active);
            if (!string.IsNullOrWhiteSpace(r.LineId)) group = group.Where(w => w.LineId == r.LineId.Trim());
            if (r.DepartmentId is { } d) group = group.Where(w => w.DepartmentId == d);
            targets = await group.OrderBy(w => w.WorkerNumber)
                .Select(w => new AttendanceSupport.WorkerRow(w.Id, w.FactoryId, w.LineId, w.DepartmentId, w.UserId, w.Status, w.WorkerCode, w.FullName))
                .Take(AttendanceRules.MaxBulkWorkers + 1).ToListAsync(ct);
            if (targets.Count == 0)
                return AccessResult<AssignShiftResult>.Invalid("no_workers", "No active workers in your scope match that line / department.");
            if (targets.Count > AttendanceRules.MaxBulkWorkers)
                return AccessResult<AssignShiftResult>.Invalid("too_many_workers", $"More than {AttendanceRules.MaxBulkWorkers} workers match. Narrow it down.");
        }

        var results = new List<AssignShiftOutcome>();
        foreach (var t in targets)
            results.Add(await AssignOneAsync(caller, shift, t, r, traceId, ct));
        var assigned = results.Count(x => x.Outcome == "Assigned");
        var result = new AssignShiftResult(results.Count, assigned, results.Count - assigned, results);
        return assigned > 0 ? AccessResult<AssignShiftResult>.Created(result) : AccessResult<AssignShiftResult>.Ok(result);
    }

    /// <summary>One worker, in its own transaction under the worker's row lock.</summary>
    private async Task<AssignShiftOutcome> AssignOneAsync(AccessContext caller, Shift shift, AttendanceSupport.WorkerRow w, AssignShiftRequest r, string? traceId, CancellationToken ct)
    {
        AssignShiftOutcome Skip(string code, string message) => new(w.Id, w.WorkerCode, "Skipped", code, message, null);
        if (w.Status == "?" || !AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId)) return Skip("worker_not_found", "Worker not found in your scope.");
        if (w.FactoryId != shift.FactoryId) return Skip("worker_in_other_factory", "The worker belongs to another factory than the shift.");
        if (w.Status != WorkerStatuses.Active) return Skip("worker_not_active", $"The worker is {w.Status}.");

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workers WHERE \"Id\" = {w.Id} FOR UPDATE", ct);
                var overlapping = await _db.ShiftAssignments
                    .Where(a => a.WorkerId == w.Id && (a.EffectiveTo == null || a.EffectiveTo >= r.EffectiveFrom)
                                && (r.EffectiveTo == null || a.EffectiveFrom <= r.EffectiveTo))
                    .OrderBy(a => a.EffectiveFrom).ToListAsync(ct);
                if (overlapping.Any(a => a.ShiftId == shift.Id && a.EffectiveFrom <= r.EffectiveFrom && a.EffectiveTo == r.EffectiveTo))
                {
                    await tx.RollbackAsync(ct);
                    return Skip("already_on_shift", $"Already on {shift.Code} for these dates.");
                }
                var later = overlapping.FirstOrDefault(a => a.EffectiveFrom >= r.EffectiveFrom);
                if (later is not null)
                {
                    await tx.RollbackAsync(ct);
                    return Skip("shift_assignment_conflict", $"A shift assignment from {later.EffectiveFrom:yyyy-MM-dd} already exists. End or change it first.");
                }
                var now = _clock.GetUtcNow();
                var previous = overlapping.LastOrDefault();
                if (previous is not null)
                {
                    // Close the current assignment the day before the new one starts; it stays in the history.
                    previous.EffectiveTo = r.EffectiveFrom.AddDays(-1);
                    previous.Status = ShiftAssignmentStatuses.Superseded;
                    previous.EndReason = $"Replaced by {shift.Code} from {r.EffectiveFrom:yyyy-MM-dd}";
                    previous.EndedByUserId = caller.UserId;
                    previous.EndedByEmail = AccessAudit.Clip(caller.Email, 256);
                    previous.UpdatedAt = now;
                    await _db.SaveChangesAsync(ct);
                }
                var a = new WorkerShiftAssignment
                {
                    WorkerId = w.Id, FactoryId = shift.FactoryId, ShiftId = shift.Id, EffectiveFrom = r.EffectiveFrom, EffectiveTo = r.EffectiveTo,
                    Reason = Clean(r.Reason), PreviousAssignmentId = previous?.Id,
                    AssignedByUserId = caller.UserId, AssignedByEmail = AccessAudit.Clip(caller.Email, 256)!, CreatedAt = now, UpdatedAt = now,
                };
                _db.ShiftAssignments.Add(a);
                _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "shift.assigned", AccessAudit.EntityShiftAssignment, a.Id.ToString(),
                    previous is null ? null : previous.ShiftId.ToString(), shift.Code,
                    $"worker {w.WorkerCode} on shift {shift.Code} from {r.EffectiveFrom:yyyy-MM-dd}{(r.EffectiveTo is { } to ? $" to {to:yyyy-MM-dd}" : "")}", traceId, shift.FactoryId));
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new AssignShiftOutcome(w.Id, w.WorkerCode, "Assigned", null, null, a.Id);
            });
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23P01" })
        {
            _db.ChangeTracker.Clear();
            return Skip("shift_assignment_conflict", "These dates overlap another shift assignment of the worker.");
        }
    }

    public async Task<AccessResult<ShiftAssignmentDto>> EndAssignmentAsync(AccessContext caller, Guid id, EndShiftAssignmentRequest r, string? traceId, CancellationToken ct)
    {
        var a = await _db.ShiftAssignments.FirstOrDefaultAsync(x => x.Id == id, ct);
        var w = a is null ? null : await _support.WorkerAsync(a.WorkerId, ct);
        if (a is null || w is null || !AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId))
            return AccessResult<ShiftAssignmentDto>.NotFound("shift_assignment_not_found", "Shift assignment not found.");
        if (a.Status != ShiftAssignmentStatuses.Active)
            return AccessResult<ShiftAssignmentDto>.Conflict("shift_assignment_closed", "This shift assignment has already ended.");
        if (r.EffectiveTo < a.EffectiveFrom)
            return AccessResult<ShiftAssignmentDto>.Invalid("invalid_end_date", "The end date can't be before the start date.");
        if (a.EffectiveTo is { } to && r.EffectiveTo > to)
            return AccessResult<ShiftAssignmentDto>.Invalid("invalid_end_date", "An assignment can only be ended earlier than planned.");
        a.EffectiveTo = r.EffectiveTo;
        a.Status = ShiftAssignmentStatuses.Ended;
        a.EndReason = r.Reason.Trim();
        a.EndedByUserId = caller.UserId;
        a.EndedByEmail = AccessAudit.Clip(caller.Email, 256);
        a.UpdatedAt = _clock.GetUtcNow();
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "shift.assignment_ended", AccessAudit.EntityShiftAssignment, a.Id.ToString(), "Active", "Ended",
            $"shift assignment of worker {w.WorkerCode} ends {r.EffectiveTo:yyyy-MM-dd}: {a.EndReason}", traceId, a.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<ShiftAssignmentDto>.Ok((await AssignmentDtosAsync(new[] { a }, ct))[0]);
    }

    internal async Task<IReadOnlyList<ShiftAssignmentDto>> AssignmentDtosAsync(IReadOnlyList<WorkerShiftAssignment> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<ShiftAssignmentDto>();
        var names = await _support.WorkerNamesAsync(rows.Select(r => r.WorkerId), ct);
        var shiftIds = rows.Select(r => r.ShiftId).Distinct().ToList();
        var shifts = (await _db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToListAsync(ct)).ToDictionary(s => s.Id, AttendanceSupport.Ref);
        return rows.Select(a =>
        {
            var (code, name) = names.GetValueOrDefault(a.WorkerId, ("?", "Unknown worker"));
            return new ShiftAssignmentDto(a.Id, a.WorkerId, code, name, a.FactoryId, shifts[a.ShiftId], a.EffectiveFrom, a.EffectiveTo, a.Status,
                a.Reason, a.EndReason, a.PreviousAssignmentId, a.AssignedByEmail, a.EndedByEmail, a.CreatedAt);
        }).ToList();
    }
}
