using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Assignments;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Assignments;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Assignments;

/// <summary>
/// Implements <see cref="IAssignmentService"/>. The rules, all enforced here:
/// <list type="bullet">
///   <item><b>Reach.</b> Reading: assignments on lines the caller can see
///   (Super Admin all, factory roles their factories, supervisors their
///   lines); a worker reads their own. Writing needs <c>workers.assign</c>
///   (on the endpoint) and both the worker and the workstation inside the
///   caller's scope. A worker is never assigned outside their own factory.</item>
///   <item><b>Validity.</b> Only Active workers, active workstations and
///   active machines; a machine must stand at the workstation (or nowhere)
///   and serve one assignment at a time; the workstation's line, department
///   and floor are copied into the assignment.</item>
///   <item><b>One at a time.</b> One active primary assignment per worker,
///   no overlapping periods, at most <c>Capacity</c> workers per workstation.
///   Checked under row locks (worker → workstation) and backed by a unique
///   index and an exclusion constraint in the database.</item>
///   <item><b>Transfer.</b> Closing the old assignment and opening the new one
///   happen in one transaction at the same instant: no gap, no overlap.</item>
///   <item><b>History.</b> Rows are never deleted or reopened; every change is audited.</item>
/// </list>
/// </summary>
public sealed class AssignmentService : IAssignmentService
{
    public const int MaxPageSize = 200;
    private readonly BunonBrainDbContext _db;
    private readonly ILogger<AssignmentService> _log;
    private readonly TimeProvider _clock;

    public AssignmentService(BunonBrainDbContext db, ILogger<AssignmentService> log, TimeProvider? clock = null)
    {
        _db = db; _log = log; _clock = clock ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    // ─── Reach ─────────────────────────────────────────────────────────────

    private IQueryable<WorkstationAssignment> Visible(AccessContext caller)
    {
        var all = _db.Assignments.AsNoTracking();
        if (!caller.Has(Permissions.WorkersView)) return all.Where(_ => false);
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => all,
            RoleScope.Factory => all.Where(a => factories.Contains(a.FactoryId)),
            RoleScope.Line => all.Where(a => factories.Contains(a.FactoryId) && lines.Contains(a.LineId)),
            _ => all.Where(_ => false),
        };
    }

    private sealed record WorkerRow(Guid Id, Guid FactoryId, string? LineId, Guid? UserId, string Status, string WorkerCode);

    private Task<WorkerRow?> WorkerAsync(Guid id, CancellationToken ct) =>
        _db.Workers.AsNoTracking().Where(w => w.Id == id)
            .Select(w => new WorkerRow(w.Id, w.FactoryId, w.LineId, w.UserId, w.Status, w.WorkerCode)).FirstOrDefaultAsync(ct);

    /// <summary>The worker is inside the caller's scope (a supervisor: on one of their lines).</summary>
    private static bool Reaches(AccessContext caller, WorkerRow w) => caller.Scope switch
    {
        RoleScope.Platform => true,
        RoleScope.Factory => caller.FactoryIds.Contains(w.FactoryId),
        RoleScope.Line => caller.FactoryIds.Contains(w.FactoryId) && w.LineId is not null && caller.LineIds.Contains(w.LineId),
        _ => false,
    };

    private static AccessResult<T> WorkerNotFound<T>() => AccessResult<T>.NotFound("worker_not_found", "Worker not found.");
    private static AccessResult<T> AssignmentNotFound<T>() => AccessResult<T>.NotFound("assignment_not_found", "Assignment not found.");

    // ─── Queries ───────────────────────────────────────────────────────────

    public async Task<PagedResult<AssignmentDto>> ListAsync(AccessContext caller, AssignmentListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var rows = Visible(caller);
        if (q.FactoryId is { } f) rows = rows.Where(a => a.FactoryId == f);
        if (q.DepartmentId is { } d) rows = rows.Where(a => a.DepartmentId == d);
        if (q.FloorId is { } fl) rows = rows.Where(a => a.FloorId == fl);
        if (!string.IsNullOrWhiteSpace(q.LineId)) rows = rows.Where(a => a.LineId == q.LineId.Trim());
        if (q.WorkstationId is { } ws) rows = rows.Where(a => a.WorkstationId == ws);
        if (q.WorkerId is { } wk) rows = rows.Where(a => a.WorkerId == wk);
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            var status = AssignmentStatuses.Parse(q.Status);
            rows = status is null ? rows.Where(_ => false) : rows.Where(a => a.Status == status);
        }
        // Assignments in force at some point of [From, To].
        if (q.From is { } from) rows = rows.Where(a => a.EndAt == null || a.EndAt > from);
        if (q.To is { } to) rows = rows.Where(a => a.StartAt <= to);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            rows = rows.Where(a => _db.Workers.Any(w => w.Id == a.WorkerId && (EF.Functions.ILike(w.WorkerCode, pattern, "\\") || EF.Functions.ILike(w.FullName, pattern, "\\")))
                                   || _db.Workstations.Any(w => w.Id == a.WorkstationId && EF.Functions.ILike(w.Code, pattern, "\\")));
        }
        var total = await rows.CountAsync(ct);
        var page_ = await rows.OrderByDescending(a => a.StartAt).ThenByDescending(a => a.CreatedAt)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<AssignmentDto>(await ToDtosAsync(page_, ct), page, size, total);
    }

    public async Task<AccessResult<AssignmentDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var a = await _db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return AssignmentNotFound<AssignmentDto>();
        var visible = await Visible(caller).AnyAsync(x => x.Id == id, ct) || await IsOwnAsync(caller, a.WorkerId, ct);
        return visible ? AccessResult<AssignmentDto>.Ok((await ToDtosAsync(new[] { a }, ct))[0]) : AssignmentNotFound<AssignmentDto>();
    }

    private async Task<bool> IsOwnAsync(AccessContext caller, Guid workerId, CancellationToken ct) =>
        caller.Has(Permissions.SelfView) && await _db.Workers.AnyAsync(w => w.Id == workerId && w.UserId == caller.UserId, ct);

    public async Task<AccessResult<IReadOnlyList<AssignmentDto>>> WorkerHistoryAsync(AccessContext caller, string workerIdOrCode, CancellationToken ct)
    {
        var workers = _db.Workers.AsNoTracking();
        if (workerIdOrCode.Equals("me", StringComparison.OrdinalIgnoreCase)) workers = workers.Where(w => w.UserId == caller.UserId);
        else if (Guid.TryParse(workerIdOrCode, out var gid)) workers = workers.Where(w => w.Id == gid);
        else if (WorkerRules.LooksLikeWorkerCode(workerIdOrCode))
        {
            var code = WorkerRules.NormaliseWorkerCode(workerIdOrCode);
            workers = workers.Where(w => w.WorkerCode == code);
        }
        else return WorkerNotFound<IReadOnlyList<AssignmentDto>>();
        var w = await workers.Select(x => new WorkerRow(x.Id, x.FactoryId, x.LineId, x.UserId, x.Status, x.WorkerCode)).FirstOrDefaultAsync(ct);
        if (w is null) return WorkerNotFound<IReadOnlyList<AssignmentDto>>();
        var own = w.UserId == caller.UserId && caller.Has(Permissions.SelfView);
        if (!own && !(caller.Has(Permissions.WorkersView) && Reaches(caller, w))) return WorkerNotFound<IReadOnlyList<AssignmentDto>>();
        // A worker's whole history: someone who can see the worker sees where they have been.
        var rows = await _db.Assignments.AsNoTracking().Where(a => a.WorkerId == w.Id)
            .OrderByDescending(a => a.StartAt).ThenByDescending(a => a.CreatedAt).Take(500).ToListAsync(ct);
        return AccessResult<IReadOnlyList<AssignmentDto>>.Ok(await ToDtosAsync(rows, ct));
    }

    public async Task<AccessResult<IReadOnlyList<AssignmentDto>>> WorkstationHistoryAsync(AccessContext caller, Guid workstationId, int limit, CancellationToken ct)
    {
        if (!await WorkstationService.InScope(_db, caller).AnyAsync(w => w.Id == workstationId, ct))
            return AccessResult<IReadOnlyList<AssignmentDto>>.NotFound("workstation_not_found", "Workstation not found.");
        var rows = await _db.Assignments.AsNoTracking().Where(a => a.WorkstationId == workstationId)
            .OrderByDescending(a => a.StartAt).ThenByDescending(a => a.CreatedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return AccessResult<IReadOnlyList<AssignmentDto>>.Ok(await ToDtosAsync(rows, ct));
    }

    public async Task<AssignmentStatisticsDto> StatisticsAsync(AccessContext caller, Guid? factoryId, string? lineId, CancellationToken ct)
    {
        var stations = WorkstationService.InScope(_db, caller);
        if (factoryId is { } f) stations = stations.Where(w => w.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(lineId)) stations = stations.Where(w => w.LineId == lineId.Trim());
        var ws = await stations.Select(w => new
        {
            w.Id, w.LineId, w.IsActive, w.Capacity,
            Assigned = _db.Assignments.Count(a => a.WorkstationId == w.Id && a.EndAt == null),
        }).ToListAsync(ct);

        var assignments = Visible(caller).Where(a => a.EndAt == null);
        if (factoryId is { } f2) assignments = assignments.Where(a => a.FactoryId == f2);
        if (!string.IsNullOrWhiteSpace(lineId)) assignments = assignments.Where(a => a.LineId == lineId.Trim());
        var activeAssignments = await assignments.CountAsync(ct);

        // Active workers in scope; for a line filter: workers whose current line is that line.
        var workers = WorkersInScope(caller).Where(w => w.Status == WorkerStatuses.Active);
        if (factoryId is { } f3) workers = workers.Where(w => w.FactoryId == f3);
        if (!string.IsNullOrWhiteSpace(lineId)) workers = workers.Where(w => w.LineId == lineId.Trim());
        var activeWorkers = await workers.CountAsync(ct);
        var unassigned = await workers.CountAsync(w => !_db.Assignments.Any(a => a.WorkerId == w.Id && a.EndAt == null && a.IsPrimary), ct);

        var lineIds = ws.Select(w => w.LineId).Distinct().ToList();
        var lineNames = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var byLine = ws.GroupBy(w => w.LineId).Select(g => new LineOccupancyDto(
                g.Key, lineNames.GetValueOrDefault(g.Key, g.Key), g.Count(),
                Occupied: g.Count(w => w.IsActive && w.Assigned > 0),
                Available: g.Count(w => w.IsActive && w.Assigned == 0),
                Full: g.Count(w => w.IsActive && w.Assigned >= w.Capacity),
                Inactive: g.Count(w => !w.IsActive),
                Capacity: g.Where(w => w.IsActive).Sum(w => w.Capacity),
                Assigned: g.Sum(w => w.Assigned)))
            .OrderBy(l => l.LineName, StringComparer.OrdinalIgnoreCase).ToList();

        var transfers = Visible(caller).Where(a => a.PreviousAssignmentId != null);
        if (factoryId is { } f4) transfers = transfers.Where(a => a.FactoryId == f4);
        if (!string.IsNullOrWhiteSpace(lineId)) transfers = transfers.Where(a => a.LineId == lineId.Trim());
        var recent = await transfers.OrderByDescending(a => a.StartAt).ThenByDescending(a => a.CreatedAt).Take(10).ToListAsync(ct);

        return new AssignmentStatisticsDto(
            TotalWorkstations: ws.Count,
            ActiveWorkstations: ws.Count(w => w.IsActive),
            OccupiedWorkstations: ws.Count(w => w.IsActive && w.Assigned > 0),
            AvailableWorkstations: ws.Count(w => w.IsActive && w.Assigned == 0),
            FullWorkstations: ws.Count(w => w.IsActive && w.Assigned >= w.Capacity),
            InactiveWorkstations: ws.Count(w => !w.IsActive),
            TotalCapacity: ws.Where(w => w.IsActive).Sum(w => w.Capacity),
            ActiveAssignments: activeAssignments,
            ActiveWorkers: activeWorkers,
            UnassignedActiveWorkers: unassigned,
            ByLine: byLine,
            RecentTransfers: await ToDtosAsync(recent, ct));
    }

    private IQueryable<WorkerProfile> WorkersInScope(AccessContext caller)
    {
        var all = _db.Workers.AsNoTracking();
        if (!caller.Has(Permissions.WorkersView)) return all.Where(_ => false);
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => all,
            RoleScope.Factory => all.Where(w => factories.Contains(w.FactoryId)),
            RoleScope.Line => all.Where(w => factories.Contains(w.FactoryId) && w.LineId != null && lines.Contains(w.LineId)),
            _ => all.Where(_ => false),
        };
    }

    // ─── Commands ──────────────────────────────────────────────────────────

    public async Task<AccessResult<AssignmentDto>> CreateAsync(AccessContext caller, CreateAssignmentRequest r, string? traceId, CancellationToken ct)
    {
        var now = Now;
        var startAt = r.StartAt ?? now;
        if (AssignmentRules.EffectiveTimeProblem(startAt, now) is { } timeProblem)
            return AccessResult<AssignmentDto>.Invalid("invalid_start_time", timeProblem);

        Guid newId = Guid.Empty;
        var result = await InTransactionAsync(async () =>
        {
            await AssignmentLocks.WorkerAsync(_db, r.WorkerId, ct);
            var worker = await WorkerAsync(r.WorkerId, ct);
            if (worker is null || !Reaches(caller, worker)) return WorkerNotFound<AssignmentDto>();
            if (worker.Status != WorkerStatuses.Active)
                return AccessResult<AssignmentDto>.Conflict("worker_not_active", $"{worker.WorkerCode} is {worker.Status}; only active workers can be assigned.");

            await AssignmentLocks.WorkstationAsync(_db, r.WorkstationId, ct);
            var target = await CheckTargetAsync(caller, worker, r.WorkstationId, r.MachineId, ignoreAssignmentId: null, ct);
            if (target.Error is not null) return target.Error.As<AssignmentDto>();

            if (await _db.Assignments.AnyAsync(a => a.WorkerId == worker.Id && a.EndAt == null && a.IsPrimary, ct))
                return AccessResult<AssignmentDto>.Conflict("worker_already_assigned",
                    $"{worker.WorkerCode} already has an active workstation assignment. Transfer the worker instead.");
            if (await _db.Assignments.AnyAsync(a => a.WorkerId == worker.Id && a.IsPrimary && (a.EndAt == null || a.EndAt > startAt), ct))
                return AccessResult<AssignmentDto>.Conflict("assignment_overlap",
                    "This start time overlaps an earlier assignment of the worker. Choose a time after it ended.");

            var ws = target.Workstation!;
            var assignment = NewAssignment(caller, worker.Id, ws, r.MachineId, startAt, Clean(r.Reason), Clean(r.Notes), previous: null);
            _db.Assignments.Add(assignment);
            await PlaceWorkerAsync(worker.Id, ws, ct);
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "assignment.created", AccessAudit.EntityAssignment, assignment.Id.ToString(),
                null, ws.Code, $"worker {worker.WorkerCode} assigned to workstation {ws.Code} (line {ws.LineId}) from {startAt:u}", traceId, ws.FactoryId));
            await _db.SaveChangesAsync(ct);
            newId = assignment.Id;
            return AccessResult<AssignmentDto>.Created(default!);
        }, ct);
        if (result.Outcome != AccessOutcome.Created) return result;
        _log.LogInformation("User {ActorId} created assignment {AssignmentId}.", caller.UserId, newId);
        return AccessResult<AssignmentDto>.Created(await DtoAsync(newId, ct));
    }

    public async Task<AccessResult<TransferResult>> TransferAsync(AccessContext caller, Guid assignmentId, TransferAssignmentRequest r, string? traceId, CancellationToken ct)
    {
        var existing = await _db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (existing is null || !await Visible(caller).AnyAsync(a => a.Id == assignmentId, ct)) return AssignmentNotFound<TransferResult>();
        var now = Now;
        var at = r.EffectiveAt ?? now;

        Guid openedId = Guid.Empty;
        var result = await InTransactionAsync(async () =>
        {
            // Lock order: worker → assignment → workstation (the same everywhere).
            await AssignmentLocks.WorkerAsync(_db, existing.WorkerId, ct);
            await AssignmentLocks.AssignmentAsync(_db, assignmentId, ct);
            var old = await _db.Assignments.FirstAsync(a => a.Id == assignmentId, ct);
            if (old.EndAt is not null)
                return AccessResult<TransferResult>.Conflict("assignment_not_active", "This assignment has already ended. Refresh and try again.");
            if (!caller.CanAccessLine(old.LineId, old.FactoryId))
                return AccessResult<TransferResult>.Forbidden("line_out_of_scope", "This worker's current line is outside your assignment.");
            if (old.WorkstationId == r.WorkstationId)
                return AccessResult<TransferResult>.Conflict("same_workstation", "The worker is already assigned to this workstation.");
            if (AssignmentRules.EffectiveTimeProblem(at, now, old.StartAt) is { } timeProblem)
                return AccessResult<TransferResult>.Invalid("invalid_effective_time", timeProblem);

            var worker = await WorkerAsync(old.WorkerId, ct);
            if (worker is null) return WorkerNotFound<TransferResult>();
            if (worker.Status != WorkerStatuses.Active)
                return AccessResult<TransferResult>.Conflict("worker_not_active", $"{worker.WorkerCode} is {worker.Status}; end the assignment instead.");

            await AssignmentLocks.WorkstationAsync(_db, r.WorkstationId, ct);
            var target = await CheckTargetAsync(caller, worker, r.WorkstationId, r.MachineId, ignoreAssignmentId: old.Id, ct);
            if (target.Error is not null) return target.Error.As<TransferResult>();
            var ws = target.Workstation!;
            var reason = r.Reason.Trim();

            // 1. Close the old assignment at the transfer instant…
            var fromCode = await _db.Workstations.Where(w => w.Id == old.WorkstationId).Select(w => w.Code).FirstAsync(ct);
            old.EndAt = at;
            old.Status = AssignmentStatuses.Transferred;
            old.EndReason = reason;
            old.EndedByUserId = caller.UserId;
            old.EndedByEmail = AccessAudit.Clip(caller.Email, 256);
            old.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            // 2. …and open the new one at the same instant (ranges are [start, end), so they touch without overlapping).
            var opened = NewAssignment(caller, worker.Id, ws, r.MachineId, at, reason, Clean(r.Notes), previous: old.Id);
            _db.Assignments.Add(opened);
            await PlaceWorkerAsync(worker.Id, ws, ct);
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "assignment.transferred", AccessAudit.EntityAssignment, opened.Id.ToString(),
                fromCode, ws.Code, $"worker {worker.WorkerCode} transferred {fromCode} → {ws.Code} (line {old.LineId} → {ws.LineId}) at {at:u}: {reason}",
                traceId, ws.FactoryId));
            await _db.SaveChangesAsync(ct);
            openedId = opened.Id;
            return AccessResult<TransferResult>.Ok(default!);
        }, ct);
        if (result.Outcome != AccessOutcome.Ok) return result;
        var dtos = await ToDtosAsync(await _db.Assignments.AsNoTracking().Where(a => a.Id == assignmentId || a.Id == openedId).ToListAsync(ct), ct);
        return AccessResult<TransferResult>.Ok(new TransferResult(dtos.Single(d => d.Id == assignmentId), dtos.Single(d => d.Id == openedId)));
    }

    public async Task<AccessResult<AssignmentDto>> EndAsync(AccessContext caller, Guid assignmentId, EndAssignmentRequest r, string? traceId, CancellationToken ct)
    {
        var existing = await _db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (existing is null || !await Visible(caller).AnyAsync(a => a.Id == assignmentId, ct)) return AssignmentNotFound<AssignmentDto>();
        var now = Now;
        var endAt = r.EndAt ?? now;

        var result = await InTransactionAsync(async () =>
        {
            await AssignmentLocks.WorkerAsync(_db, existing.WorkerId, ct);
            await AssignmentLocks.AssignmentAsync(_db, assignmentId, ct);
            var a = await _db.Assignments.FirstAsync(x => x.Id == assignmentId, ct);
            if (a.EndAt is not null)
                return AccessResult<AssignmentDto>.Conflict("assignment_not_active", "This assignment has already ended.");
            if (!caller.CanAccessLine(a.LineId, a.FactoryId))
                return AccessResult<AssignmentDto>.Forbidden("line_out_of_scope", "This assignment's line is outside your assignment.");
            if (AssignmentRules.EffectiveTimeProblem(endAt, now, a.StartAt) is { } timeProblem)
                return AccessResult<AssignmentDto>.Invalid("invalid_end_time", timeProblem);
            var reason = r.Reason.Trim();
            Close(a, caller, endAt, AssignmentStatuses.Ended, reason, now);
            await ReleaseWorkerAsync(a.WorkerId, ct);
            var info = await (from w in _db.Workers join s in _db.Workstations on a.WorkstationId equals s.Id where w.Id == a.WorkerId select new { w.WorkerCode, s.Code }).FirstAsync(ct);
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "assignment.ended", AccessAudit.EntityAssignment, a.Id.ToString(),
                info.Code, "(none)", $"assignment of worker {info.WorkerCode} at {info.Code} ended at {endAt:u}: {reason}", traceId, a.FactoryId));
            await _db.SaveChangesAsync(ct);
            return AccessResult<AssignmentDto>.Ok(default!);
        }, ct);
        return result.Outcome == AccessOutcome.Ok ? AccessResult<AssignmentDto>.Ok(await DtoAsync(assignmentId, ct)) : result;
    }

    /// <summary>
    /// Ends the worker's active assignments because the worker stopped being
    /// Active. Runs inside the caller's transaction, with the worker row
    /// already locked (see WorkerService.ChangeStatusAsync).
    /// </summary>
    internal static async Task<int> EndForWorkerStatusAsync(BunonBrainDbContext db, AccessContext caller, Guid workerId, string newStatus,
        DateTimeOffset now, string? traceId, CancellationToken ct)
    {
        var open = await db.Assignments.Where(a => a.WorkerId == workerId && a.EndAt == null).ToListAsync(ct);
        foreach (var a in open)
        {
            // An assignment can't end before it started; one that starts "now" ends a moment later.
            var endAt = now > a.StartAt ? now : a.StartAt.AddSeconds(1);
            Close(a, caller, endAt, AssignmentStatuses.Ended, $"Worker status changed to {newStatus}", now);
            db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "assignment.ended", AccessAudit.EntityAssignment, a.Id.ToString(),
                "Active", "Ended", $"assignment ended because the worker became {newStatus}", traceId, a.FactoryId));
        }
        return open.Count;
    }

    private static void Close(WorkstationAssignment a, AccessContext caller, DateTimeOffset endAt, string status, string reason, DateTimeOffset now)
    {
        a.EndAt = endAt;
        a.Status = status;
        a.EndReason = AccessAudit.Clip(reason, 500);
        a.EndedByUserId = caller.UserId;
        a.EndedByEmail = AccessAudit.Clip(caller.Email, 256);
        a.UpdatedAt = now;
    }

    private WorkstationAssignment NewAssignment(AccessContext caller, Guid workerId, Workstation ws, Guid? machineId, DateTimeOffset startAt,
        string? reason, string? notes, Guid? previous) => new()
    {
        WorkerId = workerId, FactoryId = ws.FactoryId, DepartmentId = ws.DepartmentId, FloorId = ws.FloorId, LineId = ws.LineId,
        WorkstationId = ws.Id, MachineId = machineId, IsPrimary = true, StartAt = startAt, Status = AssignmentStatuses.Active,
        Reason = reason, Notes = notes, PreviousAssignmentId = previous,
        AssignedByUserId = caller.UserId, AssignedByEmail = AccessAudit.Clip(caller.Email, 256)!,
        CreatedAt = Now, UpdatedAt = Now,
    };

    /// <summary>The worker record shows the current placement (line, floor, workstation code).</summary>
    private async Task PlaceWorkerAsync(Guid workerId, Workstation ws, CancellationToken ct)
    {
        var w = await _db.Workers.FirstAsync(x => x.Id == workerId, ct);
        w.LineId = ws.LineId;
        w.FloorId = ws.FloorId ?? w.FloorId;
        w.Workstation = ws.Code;
        w.UpdatedAt = Now;
    }

    /// <summary>The worker keeps their line (so their supervisor still sees them) but no longer has a workstation.</summary>
    private async Task ReleaseWorkerAsync(Guid workerId, CancellationToken ct)
    {
        var w = await _db.Workers.FirstAsync(x => x.Id == workerId, ct);
        w.Workstation = null;
        w.UpdatedAt = Now;
    }

    private sealed record Target(Workstation? Workstation, AccessResult<object>? Error);

    /// <summary>Workstation (locked by the caller) and machine checks shared by assign and transfer.</summary>
    private async Task<Target> CheckTargetAsync(AccessContext caller, WorkerRow worker, Guid workstationId, Guid? machineId,
        Guid? ignoreAssignmentId, CancellationToken ct)
    {
        static Target Fail(AccessResult<object> e) => new(null, e);
        var ws = await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workstationId, ct);
        if (ws is null || !caller.CanAccessFactory(ws.FactoryId))
            return Fail(AccessResult<object>.NotFound("workstation_not_found", "Workstation not found."));
        if (ws.FactoryId != worker.FactoryId)
            return Fail(AccessResult<object>.Invalid("workstation_in_other_factory", "The workstation belongs to another factory than the worker."));
        if (!caller.CanAccessLine(ws.LineId, ws.FactoryId))
            return Fail(AccessResult<object>.Forbidden("line_out_of_scope", "That workstation's line is outside your assignment."));
        if (!ws.IsActive)
            return Fail(AccessResult<object>.Conflict("workstation_inactive", $"Workstation {ws.Code} is deactivated."));
        // The workstation's own placement must still be valid (nothing deactivated under it).
        if (!await _db.ProductionLines.AnyAsync(l => l.Id == ws.LineId && l.FactoryId == ws.FactoryId && l.IsActive, ct))
            return Fail(AccessResult<object>.Conflict("line_inactive", "The workstation's production line is deactivated or no longer in this factory."));
        if (!await _db.Departments.AnyAsync(d => d.Id == ws.DepartmentId && d.FactoryId == ws.FactoryId && d.IsActive, ct))
            return Fail(AccessResult<object>.Conflict("department_inactive", "The workstation's department is deactivated."));
        var assigned = await _db.Assignments.CountAsync(a => a.WorkstationId == ws.Id && a.EndAt == null && a.Id != ignoreAssignmentId, ct);
        if (assigned >= ws.Capacity)
            return Fail(AccessResult<object>.Conflict("workstation_full",
                $"Workstation {ws.Code} already has {assigned} of {ws.Capacity} worker(s) assigned."));
        if (machineId is { } mid)
        {
            var m = await _db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mid, ct);
            if (m is null || m.FactoryId != ws.FactoryId)
                return Fail(AccessResult<object>.Invalid("machine_not_found", "That machine doesn't exist in this factory."));
            if (!m.IsActive) return Fail(AccessResult<object>.Conflict("machine_inactive", $"Machine {m.Code} is deactivated."));
            if (m.WorkstationId is { } at && at != ws.Id)
                return Fail(AccessResult<object>.Conflict("machine_at_other_workstation", $"Machine {m.Code} stands at another workstation."));
            if (await _db.Assignments.AnyAsync(a => a.MachineId == mid && a.EndAt == null && a.Id != ignoreAssignmentId, ct))
                return Fail(AccessResult<object>.Conflict("machine_in_use", $"Machine {m.Code} is already assigned to another worker."));
        }
        return new(ws, null);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction that commits only on
    /// success. A conflict the database itself detects (the one-active-primary
    /// index, the overlap exclusion constraint) becomes a 409.
    /// </summary>
    private async Task<AccessResult<T>> InTransactionAsync<T>(Func<Task<AccessResult<T>>> work, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var r = await work();
                if (r.Outcome is AccessOutcome.Ok or AccessOutcome.Created) await tx.CommitAsync(ct);
                else await tx.RollbackAsync(ct);
                return r;
            });
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && (pg.SqlState == "23505" || pg.SqlState == "23P01"))
        {
            _db.ChangeTracker.Clear();
            return pg.ConstraintName == WorkstationAssignmentConfiguration.ExclusionConstraintName
                ? AccessResult<T>.Conflict("assignment_overlap", "This period overlaps another assignment of the worker.")
                : AccessResult<T>.Conflict("worker_already_assigned", "The worker already has an active workstation assignment.");
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    // ─── Read model ────────────────────────────────────────────────────────

    private async Task<AssignmentDto> DtoAsync(Guid id, CancellationToken ct) =>
        (await ToDtosAsync(await _db.Assignments.AsNoTracking().Where(a => a.Id == id).ToListAsync(ct), ct))[0];

    internal async Task<IReadOnlyList<AssignmentDto>> ToDtosAsync(IReadOnlyList<WorkstationAssignment> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<AssignmentDto>();
        var ids = rows.Select(r => r.Id).ToList();
        var workerIds = rows.Select(r => r.WorkerId).Distinct().ToList();
        var wsIds = rows.Select(r => r.WorkstationId).Distinct().ToList();
        var deptIds = rows.Select(r => r.DepartmentId).Distinct().ToList();
        var floorIds = rows.Where(r => r.FloorId != null).Select(r => r.FloorId!.Value).Distinct().ToList();
        var lineIds = rows.Select(r => r.LineId).Distinct().ToList();
        var machineIds = rows.Where(r => r.MachineId != null).Select(r => r.MachineId!.Value).Distinct().ToList();

        var workers = await _db.Workers.AsNoTracking().Where(w => workerIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => (w.WorkerCode, w.FullName), ct);
        var stations = await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => new WorkstationRef(w.Id, w.Code, w.Name), ct);
        var depts = await _db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new OrgRef(d.Id, d.Code, d.Name), ct);
        var floors = floorIds.Count == 0 ? new Dictionary<Guid, OrgRef>()
            : await _db.ProductionFloors.AsNoTracking().Where(d => floorIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new OrgRef(d.Id, d.Code, d.Name), ct);
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new WorkerLineRef(l.Id, l.Name), ct);
        var machines = machineIds.Count == 0 ? new Dictionary<Guid, MachineRef>()
            : await _db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => new MachineRef(m.Id, m.Code, m.MachineType, m.IsActive), ct);
        var next = await _db.Assignments.AsNoTracking().Where(a => a.PreviousAssignmentId != null && ids.Contains(a.PreviousAssignmentId.Value))
            .ToDictionaryAsync(a => a.PreviousAssignmentId!.Value, a => a.Id, ct);

        return rows.Select(a =>
        {
            var (code, name) = workers.GetValueOrDefault(a.WorkerId, ("?", "Unknown worker"));
            return new AssignmentDto(
                a.Id, a.WorkerId, code, name, a.FactoryId,
                depts.GetValueOrDefault(a.DepartmentId) ?? new OrgRef(a.DepartmentId, "?", "Unknown department"),
                a.FloorId is { } f ? floors.GetValueOrDefault(f) : null,
                lines.GetValueOrDefault(a.LineId) ?? new WorkerLineRef(a.LineId, a.LineId),
                stations.GetValueOrDefault(a.WorkstationId) ?? new WorkstationRef(a.WorkstationId, "?", null),
                a.MachineId is { } m ? machines.GetValueOrDefault(m) : null,
                a.IsPrimary, a.StartAt, a.EndAt, a.Status, a.Reason, a.Notes, a.EndReason, a.PreviousAssignmentId,
                next.TryGetValue(a.Id, out var n) ? n : null,
                a.AssignedByEmail, a.EndedByEmail, a.CreatedAt);
        }).ToList();
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
