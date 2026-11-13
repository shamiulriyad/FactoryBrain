using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Assignments;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Assignments;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Assignments;

/// <summary>
/// Workstations and machines (see <see cref="IWorkstationService"/>).
/// Reading follows the caller's reach (supervisors: their lines). Changing a
/// workstation needs factory-wide reach; a workstation with active
/// assignments can't be deactivated, moved to another line / department /
/// floor, or given less capacity than it has workers (transfer them first).
/// </summary>
public sealed class WorkstationService : IWorkstationService
{
    public const int MaxPageSize = 200;
    private readonly BunonBrainDbContext _db;
    public WorkstationService(BunonBrainDbContext db) { _db = db; }

    // ─── Reach ─────────────────────────────────────────────────────────────

    internal static IQueryable<Workstation> InScope(BunonBrainDbContext db, AccessContext caller)
    {
        var all = db.Workstations.AsNoTracking();
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => all,
            RoleScope.Factory => all.Where(w => factories.Contains(w.FactoryId)),
            RoleScope.Line => all.Where(w => factories.Contains(w.FactoryId) && lines.Contains(w.LineId)),
            _ => all.Where(_ => false),
        };
    }

    private static bool CanConfigure(AccessContext caller, Guid factoryId) =>
        caller.Scope >= RoleScope.Factory && caller.CanAccessFactory(factoryId);

    private static AccessResult<T> NotFound<T>() => AccessResult<T>.NotFound("workstation_not_found", "Workstation not found.");

    // ─── Workstations ──────────────────────────────────────────────────────

    public async Task<PagedResult<WorkstationDto>> ListAsync(AccessContext caller, WorkstationListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var ws = InScope(_db, caller);
        if (q.FactoryId is { } f) ws = ws.Where(w => w.FactoryId == f);
        if (q.DepartmentId is { } d) ws = ws.Where(w => w.DepartmentId == d);
        if (q.FloorId is { } fl) ws = ws.Where(w => w.FloorId == fl);
        if (!string.IsNullOrWhiteSpace(q.LineId)) ws = ws.Where(w => w.LineId == q.LineId.Trim());
        if (!q.IncludeInactive) ws = ws.Where(w => w.IsActive);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            ws = ws.Where(w => EF.Functions.ILike(w.Code, pattern, "\\") || (w.Name != null && EF.Functions.ILike(w.Name, pattern, "\\")));
        }
        if (!string.IsNullOrWhiteSpace(q.Occupancy))
        {
            var occ = WorkstationOccupancy.Parse(q.Occupancy);
            var active = _db.Assignments.Where(a => a.EndAt == null);
            ws = occ switch
            {
                WorkstationOccupancy.Inactive => ws.Where(w => !w.IsActive),
                WorkstationOccupancy.Available => ws.Where(w => w.IsActive && !active.Any(a => a.WorkstationId == w.Id)),
                WorkstationOccupancy.Occupied => ws.Where(w => w.IsActive && active.Any(a => a.WorkstationId == w.Id)
                                                                && active.Count(a => a.WorkstationId == w.Id) < w.Capacity),
                WorkstationOccupancy.CapacityReached => ws.Where(w => w.IsActive && active.Count(a => a.WorkstationId == w.Id) >= w.Capacity),
                _ => ws.Where(_ => false),
            };
        }
        var total = await ws.CountAsync(ct);
        var rows = await ws.OrderBy(w => w.LineId).ThenBy(w => w.Position).ThenBy(w => w.Code)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<WorkstationDto>(await ToDtosAsync(_db, caller, rows, ct), page, size, total);
    }

    public async Task<AccessResult<WorkstationDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var w = await InScope(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        return w is null ? NotFound<WorkstationDto>() : AccessResult<WorkstationDto>.Ok((await ToDtosAsync(_db, caller, new[] { w }, ct))[0]);
    }

    public async Task<AccessResult<WorkstationDto>> CreateAsync(AccessContext caller, CreateWorkstationRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanConfigure(caller, r.FactoryId))
            return AccessResult<WorkstationDto>.Forbidden("factory_out_of_scope", "You can only add workstations to your own factory.");
        if (await CheckPlacementAsync(r.FactoryId, r.LineId, r.DepartmentId, r.FloorId, requireActive: true, ct) is { } error)
            return error.As<WorkstationDto>();
        var code = AssignmentRules.NormaliseCode(r.Code);
        if (await _db.Workstations.AnyAsync(w => w.FactoryId == r.FactoryId && w.Code == code, ct))
            return AccessResult<WorkstationDto>.Conflict("workstation_code_taken", $"This factory already has a workstation {code}.");
        var position = r.Position ?? (await _db.Workstations.Where(w => w.LineId == r.LineId).MaxAsync(w => (int?)w.Position, ct) ?? 0) + 1;
        var now = DateTimeOffset.UtcNow;
        var ws = new Workstation
        {
            FactoryId = r.FactoryId, LineId = r.LineId.Trim(), DepartmentId = r.DepartmentId, FloorId = r.FloorId, Code = code,
            Name = Clean(r.Name), Capacity = r.Capacity ?? 1, Position = position, CreatedAt = now, UpdatedAt = now,
        };
        _db.Workstations.Add(ws);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "workstation.created", AccessAudit.EntityWorkstation, ws.Id.ToString(),
            null, $"capacity {ws.Capacity}", $"workstation {code} created on line {ws.LineId}", traceId, ws.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<WorkstationDto>.Conflict("workstation_code_taken", $"This factory already has a workstation {code}.");
        }
        return AccessResult<WorkstationDto>.Created((await ToDtosAsync(_db, caller, new[] { ws }, ct))[0]);
    }

    public async Task<AccessResult<BulkCreateWorkstationsResult>> BulkCreateAsync(AccessContext caller, BulkCreateWorkstationsRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanConfigure(caller, r.FactoryId))
            return AccessResult<BulkCreateWorkstationsResult>.Forbidden("factory_out_of_scope", "You can only add workstations to your own factory.");
        if (await CheckPlacementAsync(r.FactoryId, r.LineId, r.DepartmentId, r.FloorId, requireActive: true, ct) is { } error)
            return error.As<BulkCreateWorkstationsResult>();
        var codes = AssignmentRules.BulkCodes(r.Prefix, r.From, r.Count);
        if (codes.Any(c => !AssignmentRules.IsCode(c)))
            return AccessResult<BulkCreateWorkstationsResult>.Invalid("invalid_code", "The prefix and numbers don't make valid workstation codes.");
        var existing = (await _db.Workstations.Where(w => w.FactoryId == r.FactoryId && codes.Contains(w.Code)).Select(w => w.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var position = await _db.Workstations.Where(w => w.LineId == r.LineId).MaxAsync(w => (int?)w.Position, ct) ?? 0;
        var now = DateTimeOffset.UtcNow;
        var created = new List<string>();
        foreach (var code in codes.Where(c => !existing.Contains(c)))
        {
            _db.Workstations.Add(new Workstation
            {
                FactoryId = r.FactoryId, LineId = r.LineId.Trim(), DepartmentId = r.DepartmentId, FloorId = r.FloorId, Code = code,
                Capacity = r.Capacity ?? 1, Position = ++position, CreatedAt = now, UpdatedAt = now,
            });
            created.Add(code);
        }
        if (created.Count > 0)
        {
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "workstation.bulk_created", AccessAudit.EntityWorkstation, r.LineId, null, null,
                $"{created.Count} workstation(s) created on line {r.LineId} ({created[0]} … {created[^1]}); {existing.Count} skipped as existing",
                traceId, r.FactoryId));
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                _db.ChangeTracker.Clear();
                return AccessResult<BulkCreateWorkstationsResult>.Conflict("workstation_code_taken", "One of the codes was just taken. Nothing was created; try again.");
            }
        }
        var result = new BulkCreateWorkstationsResult(created.Count, created, codes.Where(existing.Contains).ToList());
        return created.Count > 0 ? AccessResult<BulkCreateWorkstationsResult>.Created(result) : AccessResult<BulkCreateWorkstationsResult>.Ok(result);
    }

    public async Task<AccessResult<WorkstationDto>> UpdateAsync(AccessContext caller, Guid id, UpdateWorkstationRequest r, string? traceId, CancellationToken ct)
    {
        var current = await InScope(_db, caller).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (current is null) return NotFound<WorkstationDto>();
        if (!CanConfigure(caller, current.FactoryId))
            return AccessResult<WorkstationDto>.Forbidden("factory_scope_required", "Only factory-level administrators configure workstations.");

        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // Same lock the assignment service takes: no assignment can slip in while this runs.
            await AssignmentLocks.WorkstationAsync(_db, id, ct);
            var ws = await _db.Workstations.FirstAsync(w => w.Id == id, ct);
            var assigned = await _db.Assignments.CountAsync(a => a.WorkstationId == id && a.EndAt == null, ct);
            var moved = ws.LineId != r.LineId.Trim() || ws.DepartmentId != r.DepartmentId || ws.FloorId != r.FloorId;

            AccessResult<WorkstationDto>? refuse = null;
            if (assigned > 0 && moved)
                refuse = AccessResult<WorkstationDto>.Conflict("workstation_has_assignments",
                    $"{assigned} worker(s) are assigned here. Transfer them before moving the workstation to another line, department or floor.");
            else if (assigned > 0 && !r.IsActive)
                refuse = AccessResult<WorkstationDto>.Conflict("workstation_has_assignments",
                    $"{assigned} worker(s) are assigned here. Transfer them or end their assignments before deactivating it.");
            else if (r.Capacity < assigned)
                refuse = AccessResult<WorkstationDto>.Conflict("capacity_below_assigned",
                    $"{assigned} worker(s) are assigned here; the capacity can't be lower than that.");
            else if (moved && await CheckPlacementAsync(ws.FactoryId, r.LineId, r.DepartmentId, r.FloorId, requireActive: true, ct) is { } error)
                refuse = error.As<WorkstationDto>();
            if (refuse is not null)
            {
                await tx.RollbackAsync(ct);
                return refuse;
            }

            var changes = new List<string>();
            if (moved) changes.Add($"placement → line {r.LineId.Trim()}");
            if (ws.Capacity != r.Capacity) changes.Add($"capacity {ws.Capacity} → {r.Capacity}");
            if (ws.IsActive != r.IsActive) changes.Add(r.IsActive ? "reactivated" : "deactivated");
            if (Clean(r.Name) != ws.Name) changes.Add("name");
            if (ws.Position != r.Position) changes.Add("position");
            if (changes.Count > 0)
            {
                _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "workstation.updated", AccessAudit.EntityWorkstation, ws.Id.ToString(),
                    ws.IsActive ? "active" : "inactive", r.IsActive ? "active" : "inactive",
                    $"workstation {ws.Code}: {string.Join("; ", changes)}", traceId, ws.FactoryId));
                ws.LineId = r.LineId.Trim();
                ws.DepartmentId = r.DepartmentId;
                ws.FloorId = r.FloorId;
                ws.Name = Clean(r.Name);
                ws.Capacity = r.Capacity;
                ws.Position = r.Position;
                ws.IsActive = r.IsActive;
                ws.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return AccessResult<WorkstationDto>.Ok(default!);
        });
        _db.ChangeTracker.Clear();
        return result.Outcome == AccessOutcome.Ok ? await GetAsync(caller, id, ct) : result;
    }

    /// <summary>Line, department and floor must exist in the factory (and be active when <paramref name="requireActive"/>).</summary>
    private async Task<AccessResult<object>?> CheckPlacementAsync(Guid factoryId, string lineId, Guid departmentId, Guid? floorId, bool requireActive, CancellationToken ct)
    {
        if (!await _db.Factories.AnyAsync(f => f.Id == factoryId && f.IsActive, ct))
            return AccessResult<object>.Invalid("factory_not_found", "Factory not found or deactivated.");
        var line = await _db.ProductionLines.AsNoTracking().Where(l => l.Id == lineId.Trim()).Select(l => new { l.FactoryId, l.IsActive }).FirstOrDefaultAsync(ct);
        if (line is null || line.FactoryId != factoryId)
            return AccessResult<object>.Invalid("line_not_in_factory", "That production line doesn't belong to this factory.");
        if (requireActive && !line.IsActive) return AccessResult<object>.Invalid("line_inactive", "That production line is deactivated.");
        var dept = await _db.Departments.AsNoTracking().Where(d => d.Id == departmentId).Select(d => new { d.FactoryId, d.IsActive }).FirstOrDefaultAsync(ct);
        if (dept is null || dept.FactoryId != factoryId)
            return AccessResult<object>.Invalid("department_not_in_factory", "That department doesn't belong to this factory.");
        if (requireActive && !dept.IsActive) return AccessResult<object>.Invalid("department_inactive", "That department is deactivated.");
        if (floorId is { } fl)
        {
            var floor = await _db.ProductionFloors.AsNoTracking().Where(d => d.Id == fl).Select(d => new { d.FactoryId, d.IsActive }).FirstOrDefaultAsync(ct);
            if (floor is null || floor.FactoryId != factoryId)
                return AccessResult<object>.Invalid("floor_not_in_factory", "That production floor doesn't belong to this factory.");
            if (requireActive && !floor.IsActive) return AccessResult<object>.Invalid("floor_inactive", "That production floor is deactivated.");
        }
        return null;
    }

    // ─── Machines ──────────────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<MachineDto>>> MachinesAsync(AccessContext caller, MachineListQuery q, CancellationToken ct)
    {
        if (caller.Scope < RoleScope.Line) return AccessResult<IReadOnlyList<MachineDto>>.Ok(Array.Empty<MachineDto>());
        var machines = _db.Machines.AsNoTracking();
        if (!caller.IsPlatform)
        {
            var factories = caller.FactoryIds.ToList();
            machines = machines.Where(m => factories.Contains(m.FactoryId));
            if (caller.IsLineScoped)
            {
                // Supervisors see the machines standing at their lines' workstations.
                var visible = InScope(_db, caller).Select(w => w.Id);
                machines = machines.Where(m => m.WorkstationId != null && visible.Contains(m.WorkstationId.Value));
            }
        }
        if (q.FactoryId is { } f) machines = machines.Where(m => m.FactoryId == f);
        if (q.WorkstationId is { } w) machines = machines.Where(m => m.WorkstationId == w);
        if (q.Unplaced == true) machines = machines.Where(m => m.WorkstationId == null);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            machines = machines.Where(m => EF.Functions.ILike(m.Code, pattern, "\\") || (m.MachineType != null && EF.Functions.ILike(m.MachineType, pattern, "\\"))
                                           || (m.SerialNumber != null && EF.Functions.ILike(m.SerialNumber, pattern, "\\")));
        }
        var rows = await machines.OrderBy(m => m.Code).Take(500)
            .Select(m => new MachineDto(m.Id, m.FactoryId, m.Code, m.MachineType, m.Brand, m.SerialNumber, m.IsActive, m.WorkstationId,
                _db.Workstations.Where(x => x.Id == m.WorkstationId).Select(x => x.Code).FirstOrDefault(), m.CreatedAt, m.UpdatedAt))
            .ToListAsync(ct);
        return AccessResult<IReadOnlyList<MachineDto>>.Ok(rows);
    }

    public async Task<AccessResult<MachineDto>> CreateMachineAsync(AccessContext caller, CreateMachineRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanConfigure(caller, r.FactoryId))
            return AccessResult<MachineDto>.Forbidden("factory_out_of_scope", "You can only add machines to your own factory.");
        if (!await _db.Factories.AnyAsync(f => f.Id == r.FactoryId && f.IsActive, ct))
            return AccessResult<MachineDto>.Invalid("factory_not_found", "Factory not found or deactivated.");
        if (r.WorkstationId is { } wsId && !await _db.Workstations.AnyAsync(w => w.Id == wsId && w.FactoryId == r.FactoryId, ct))
            return AccessResult<MachineDto>.Invalid("workstation_not_in_factory", "That workstation doesn't belong to this factory.");
        var code = AssignmentRules.NormaliseCode(r.Code);
        if (await _db.Machines.AnyAsync(m => m.FactoryId == r.FactoryId && m.Code == code, ct))
            return AccessResult<MachineDto>.Conflict("machine_code_taken", $"This factory already has a machine {code}.");
        var now = DateTimeOffset.UtcNow;
        var machine = new Machine
        {
            FactoryId = r.FactoryId, Code = code, MachineType = Clean(r.MachineType), Brand = Clean(r.Brand), SerialNumber = Clean(r.SerialNumber),
            WorkstationId = r.WorkstationId, CreatedAt = now, UpdatedAt = now,
        };
        _db.Machines.Add(machine);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "machine.created", AccessAudit.EntityMachine, machine.Id.ToString(), null, "active",
            $"machine {code} registered", traceId, r.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<MachineDto>.Conflict("machine_code_taken", $"This factory already has a machine {code}.");
        }
        return AccessResult<MachineDto>.Created(await MachineDtoAsync(machine.Id, ct));
    }

    public async Task<AccessResult<MachineDto>> UpdateMachineAsync(AccessContext caller, Guid id, UpdateMachineRequest r, string? traceId, CancellationToken ct)
    {
        var machine = await _db.Machines.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (machine is null || !caller.CanAccessFactory(machine.FactoryId))
            return AccessResult<MachineDto>.NotFound("machine_not_found", "Machine not found.");
        if (!CanConfigure(caller, machine.FactoryId))
            return AccessResult<MachineDto>.Forbidden("factory_scope_required", "Only factory-level administrators configure machines.");
        if (r.WorkstationId is { } wsId && !await _db.Workstations.AnyAsync(w => w.Id == wsId && w.FactoryId == machine.FactoryId, ct))
            return AccessResult<MachineDto>.Invalid("workstation_not_in_factory", "That workstation doesn't belong to this machine's factory.");
        var inUse = await _db.Assignments.AnyAsync(a => a.MachineId == id && a.EndAt == null, ct);
        if (inUse && (!r.IsActive || r.WorkstationId != machine.WorkstationId))
            return AccessResult<MachineDto>.Conflict("machine_in_use",
                "A worker is assigned to this machine. Transfer the worker or end the assignment before moving or deactivating it.");

        var changes = new List<string>();
        if (r.WorkstationId != machine.WorkstationId) changes.Add("placement");
        if (r.IsActive != machine.IsActive) changes.Add(r.IsActive ? "reactivated" : "deactivated");
        if (Clean(r.MachineType) != machine.MachineType || Clean(r.Brand) != machine.Brand || Clean(r.SerialNumber) != machine.SerialNumber) changes.Add("details");
        if (changes.Count > 0)
        {
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "machine.updated", AccessAudit.EntityMachine, machine.Id.ToString(),
                machine.WorkstationId?.ToString() ?? "(not placed)", r.WorkstationId?.ToString() ?? "(not placed)",
                $"machine {machine.Code}: {string.Join(", ", changes)}", traceId, machine.FactoryId));
            machine.MachineType = Clean(r.MachineType);
            machine.Brand = Clean(r.Brand);
            machine.SerialNumber = Clean(r.SerialNumber);
            machine.WorkstationId = r.WorkstationId;
            machine.IsActive = r.IsActive;
            machine.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return AccessResult<MachineDto>.Ok(await MachineDtoAsync(id, ct));
    }

    private Task<MachineDto> MachineDtoAsync(Guid id, CancellationToken ct) =>
        _db.Machines.AsNoTracking().Where(m => m.Id == id)
            .Select(m => new MachineDto(m.Id, m.FactoryId, m.Code, m.MachineType, m.Brand, m.SerialNumber, m.IsActive, m.WorkstationId,
                _db.Workstations.Where(x => x.Id == m.WorkstationId).Select(x => x.Code).FirstOrDefault(), m.CreatedAt, m.UpdatedAt))
            .FirstAsync(ct);

    // ─── Read model ────────────────────────────────────────────────────────

    internal static async Task<IReadOnlyList<WorkstationDto>> ToDtosAsync(BunonBrainDbContext db, AccessContext caller, IReadOnlyList<Workstation> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<WorkstationDto>();
        var ids = rows.Select(r => r.Id).ToList();
        var deptIds = rows.Select(r => r.DepartmentId).Distinct().ToList();
        var floorIds = rows.Where(r => r.FloorId != null).Select(r => r.FloorId!.Value).Distinct().ToList();
        var lineIds = rows.Select(r => r.LineId).Distinct().ToList();
        var factoryIds = rows.Select(r => r.FactoryId).Distinct().ToList();

        var depts = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new OrgRef(d.Id, d.Code, d.Name), ct);
        var floors = floorIds.Count == 0 ? new Dictionary<Guid, OrgRef>()
            : await db.ProductionFloors.AsNoTracking().Where(d => floorIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new OrgRef(d.Id, d.Code, d.Name), ct);
        var lines = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new WorkerLineRef(l.Id, l.Name), ct);
        var factories = await db.Factories.AsNoTracking().Where(f => factoryIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var machines = (await db.Machines.AsNoTracking().Where(m => m.WorkstationId != null && ids.Contains(m.WorkstationId.Value))
                .OrderBy(m => m.Code).ToListAsync(ct))
            .GroupBy(m => m.WorkstationId!.Value).ToDictionary(g => g.Key, g => g.Select(m => new MachineRef(m.Id, m.Code, m.MachineType, m.IsActive)).ToList());
        var active = await (from a in db.Assignments.AsNoTracking()
                            join w in db.Workers.AsNoTracking() on a.WorkerId equals w.Id
                            where a.EndAt == null && ids.Contains(a.WorkstationId)
                            orderby a.StartAt
                            select new { a.Id, a.WorkstationId, a.WorkerId, w.WorkerCode, w.FullName, a.StartAt, a.MachineId }).ToListAsync(ct);
        var machineIds = active.Where(a => a.MachineId != null).Select(a => a.MachineId!.Value).Distinct().ToList();
        var usedMachines = machineIds.Count == 0 ? new Dictionary<Guid, MachineRef>()
            : await db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => new MachineRef(m.Id, m.Code, m.MachineType, m.IsActive), ct);
        var byStation = active.GroupBy(a => a.WorkstationId).ToDictionary(g => g.Key, g => g.ToList());
        var showWorkers = caller.Has(Permissions.WorkersView);

        return rows.Select(w =>
        {
            var assigned = byStation.GetValueOrDefault(w.Id) ?? new();
            return new WorkstationDto(
                w.Id, w.Code, w.Name, w.FactoryId, factories.GetValueOrDefault(w.FactoryId, "?"),
                depts.GetValueOrDefault(w.DepartmentId) ?? new OrgRef(w.DepartmentId, "?", "Unknown department"),
                w.FloorId is { } f ? floors.GetValueOrDefault(f) : null,
                lines.GetValueOrDefault(w.LineId) ?? new WorkerLineRef(w.LineId, w.LineId),
                w.Capacity, w.Position, w.IsActive, assigned.Count,
                WorkstationOccupancy.Of(w.IsActive, assigned.Count, w.Capacity),
                machines.GetValueOrDefault(w.Id) ?? new List<MachineRef>(),
                showWorkers
                    ? assigned.Select(a => new AssignedWorkerRef(a.Id, a.WorkerId, a.WorkerCode, a.FullName, a.StartAt,
                        a.MachineId is { } m ? usedMachines.GetValueOrDefault(m) : null)).ToList()
                    : null,
                w.CreatedAt, w.UpdatedAt);
        }).ToList();
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>
/// Row locks shared by every writer of assignments, always taken in the same
/// order (worker, then workstations by id), so concurrent operations queue
/// instead of deadlocking or both passing a capacity check.
/// </summary>
internal static class AssignmentLocks
{
    public static Task WorkerAsync(BunonBrainDbContext db, Guid workerId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workers WHERE \"Id\" = {workerId} FOR UPDATE", ct);

    public static Task WorkstationAsync(BunonBrainDbContext db, Guid workstationId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workstations WHERE \"Id\" = {workstationId} FOR UPDATE", ct);

    public static Task AssignmentAsync(BunonBrainDbContext db, Guid assignmentId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workstation_assignments WHERE \"Id\" = {assignmentId} FOR UPDATE", ct);
}
