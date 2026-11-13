using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Approvals;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Access;

/// <summary>Roles and permissions as the catalog defines them, with user counts in the caller's scope.</summary>
public sealed class RoleCatalogService : IRoleCatalogService
{
    private readonly BunonBrainDbContext _db;
    public RoleCatalogService(BunonBrainDbContext db) { _db = db; }

    public async Task<IReadOnlyList<RoleDto>> RolesAsync(AccessContext caller, CancellationToken ct)
    {
        var counts = await CountsAsync(caller, ct);
        var perms = caller.Permissions.ToArray();
        return AccessCatalog.AllRoles.OrderByDescending(r => r.Rank).ThenBy(r => r.Name)
            .Select(r => ToDto(r, counts.GetValueOrDefault(r.Id), AccessCatalog.CanGrant(caller.Roles, perms, r.Id) && caller.Has(BunonBrain.Application.Access.Permissions.RolesAssign)))
            .ToList();
    }

    public async Task<RoleDto?> RoleAsync(AccessContext caller, string roleId, CancellationToken ct)
    {
        var def = AccessCatalog.Role(roleId);
        if (def is null) return null;
        var counts = await CountsAsync(caller, ct);
        return ToDto(def, counts.GetValueOrDefault(def.Id),
            AccessCatalog.CanGrant(caller.Roles, caller.Permissions.ToArray(), def.Id) && caller.Has(BunonBrain.Application.Access.Permissions.RolesAssign));
    }

    public IReadOnlyList<PermissionModuleDto> Permissions()
    {
        var holders = AccessCatalog.AllPermissions.ToDictionary(p => p.Code,
            p => (IReadOnlyList<string>)AccessCatalog.AllRoles.Where(r => r.Permissions.Contains(p.Code))
                .OrderByDescending(r => r.Rank).Select(r => r.Id).ToList());
        return AccessCatalog.AllPermissions.GroupBy(p => p.Module)
            .Select(g => new PermissionModuleDto(g.Key, g.Select(p => new PermissionDto(
                p.Code, p.Module, p.Name, p.Description, p.PlatformOnly, p.Reserved, holders[p.Code])).ToList()))
            .ToList();
    }

    private static RoleDto ToDto(RoleDefinition r, int users, bool canGrant) => new(
        r.Id, r.Name, r.Description, r.Scope.ToString(), r.Rank, IsSystem: true, r.ManagedBy, r.Permissions, users, canGrant);

    /// <summary>Holders per role among the users the caller can see (same rule as user management).</summary>
    private async Task<Dictionary<string, int>> CountsAsync(AccessContext caller, CancellationToken ct)
    {
        var users = _db.Users.AsNoTracking();
        if (!caller.IsPlatform)
        {
            var mine = caller.FactoryIds.ToList();
            users = users.Where(u => u.Factories.Any() && u.Factories.All(f => mine.Contains(f.FactoryId))
                                     && !u.Roles.Any(r => r.RoleId == SystemRoles.SuperAdmin));
        }
        return await (from u in users
                      join r in _db.UserRoles.AsNoTracking() on u.Id equals r.UserId
                      group r by r.RoleId into g
                      select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }
}

/// <summary>
/// Factories and their lines. Creating, renaming and (de)activating a
/// factory is platform-only (<c>factories.manage</c>). Anyone else sees only
/// their own factories; a Production Supervisor sees only assigned lines.
/// Deactivating a factory removes it from every member's access at once.
/// </summary>
public sealed class FactoryService : IFactoryService
{
    private readonly BunonBrainDbContext _db;
    private readonly ISessionValidator _sessions;
    public FactoryService(BunonBrainDbContext db, ISessionValidator sessions) { _db = db; _sessions = sessions; }

    public async Task<IReadOnlyList<FactoryDto>> ListAsync(AccessContext caller, CancellationToken ct)
    {
        var mine = caller.FactoryIds.ToList();
        var factories = _db.Factories.AsNoTracking().Where(f => caller.IsPlatform || mine.Contains(f.Id));
        return await factories.OrderBy(f => f.Name).Select(f => new FactoryDto(
            f.Id, f.Code, f.Name, f.Location, f.IsActive,
            _db.UserFactories.Count(a => a.FactoryId == f.Id),
            _db.ProductionLines.Count(l => l.FactoryId == f.Id),
            f.CreatedAt, f.UpdatedAt)).ToListAsync(ct);
    }

    public async Task<AccessResult<FactoryDto>> GetAsync(AccessContext caller, Guid factoryId, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId)) return NotFound();
        var dto = await DtoAsync(factoryId, ct);
        return dto is null ? NotFound() : AccessResult<FactoryDto>.Ok(dto);
    }

    public async Task<AccessResult<FactoryDto>> CreateAsync(AccessContext caller, CreateFactoryRequest request, string? traceId, CancellationToken ct)
    {
        if (!caller.IsPlatform) return AccessResult<FactoryDto>.Forbidden("platform_only", "Only a Super Admin can create factories.");
        var code = request.Code.Trim().ToUpperInvariant();
        var name = request.Name.Trim();
        if (await _db.Factories.AnyAsync(f => f.Code == code, ct))
            return AccessResult<FactoryDto>.Conflict("factory_code_taken", $"A factory with code {code} already exists.");
        if (await _db.Factories.AnyAsync(f => f.Name == name, ct))
            return AccessResult<FactoryDto>.Conflict("factory_name_taken", $"A factory named {name} already exists.");
        var now = DateTimeOffset.UtcNow;
        var factory = new Factory { Code = code, Name = name, Location = Clean(request.Location), CreatedAt = now, UpdatedAt = now };
        _db.Factories.Add(factory);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "factory.created", AccessAudit.EntityFactory, factory.Id.ToString(),
            null, "active", $"factory {code} ({name}) created", traceId, factory.Id));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return AccessResult<FactoryDto>.Conflict("factory_code_taken", "A factory with this code or name already exists.");
        }
        return AccessResult<FactoryDto>.Created((await DtoAsync(factory.Id, ct))!);
    }

    public async Task<AccessResult<FactoryDto>> UpdateAsync(AccessContext caller, Guid factoryId, UpdateFactoryRequest request, string? traceId, CancellationToken ct)
    {
        if (!caller.IsPlatform) return AccessResult<FactoryDto>.Forbidden("platform_only", "Only a Super Admin can edit factories.");
        var factory = await _db.Factories.FirstOrDefaultAsync(f => f.Id == factoryId, ct);
        if (factory is null) return NotFound();
        var name = request.Name.Trim();
        if (name != factory.Name && await _db.Factories.AnyAsync(f => f.Name == name && f.Id != factoryId, ct))
            return AccessResult<FactoryDto>.Conflict("factory_name_taken", $"A factory named {name} already exists.");
        var location = Clean(request.Location);
        if (name != factory.Name || location != factory.Location)
        {
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "factory.updated", AccessAudit.EntityFactory, factory.Id.ToString(),
                null, null, $"factory {factory.Code}: name {factory.Name} → {name}; location {factory.Location ?? "-"} → {location ?? "-"}",
                traceId, factory.Id));
            factory.Name = name;
            factory.Location = location;
            factory.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return AccessResult<FactoryDto>.Ok((await DtoAsync(factoryId, ct))!);
    }

    public async Task<AccessResult<FactoryDto>> SetStatusAsync(AccessContext caller, Guid factoryId, SetFactoryStatusRequest request, string? traceId, CancellationToken ct)
    {
        if (!caller.IsPlatform) return AccessResult<FactoryDto>.Forbidden("platform_only", "Only a Super Admin can activate or deactivate factories.");
        var factory = await _db.Factories.FirstOrDefaultAsync(f => f.Id == factoryId, ct);
        if (factory is null) return NotFound();
        if (factory.IsActive != request.IsActive)
        {
            factory.IsActive = request.IsActive;
            factory.UpdatedAt = DateTimeOffset.UtcNow;
            var members = await _db.UserFactories.Where(a => a.FactoryId == factoryId).Select(a => a.UserId).ToListAsync(ct);
            // Members' access changes: make their current tokens stale.
            await _db.Users.Where(u => members.Contains(u.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, User.NewStamp()), ct);
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, request.IsActive ? "factory.activated" : "factory.deactivated",
                AccessAudit.EntityFactory, factory.Id.ToString(), request.IsActive ? "inactive" : "active", request.IsActive ? "active" : "inactive",
                $"factory {factory.Code} {(request.IsActive ? "reactivated" : "deactivated")}; {members.Count} member(s) affected", traceId, factory.Id));
            await _db.SaveChangesAsync(ct);
            foreach (var m in members) _sessions.Invalidate(m);
        }
        return AccessResult<FactoryDto>.Ok((await DtoAsync(factoryId, ct))!);
    }

    public async Task<AccessResult<IReadOnlyList<FactoryLineDto>>> LinesAsync(AccessContext caller, Guid factoryId, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId) || caller.Scope == RoleScope.Self)
            return AccessResult<IReadOnlyList<FactoryLineDto>>.NotFound("factory_not_found", "Factory not found.");
        var lines = _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factoryId);
        if (caller.IsLineScoped)
        {
            var mine = caller.LineIds.ToList();
            lines = lines.Where(l => mine.Contains(l.Id));
        }
        return AccessResult<IReadOnlyList<FactoryLineDto>>.Ok(await lines.OrderBy(l => l.Id)
            .Select(l => new FactoryLineDto(l.Id, l.Name, l.Workstations, l.IsActive, l.FactoryId)).ToListAsync(ct));
    }

    public async Task<IReadOnlyList<FactoryLineDto>> UnassignedLinesAsync(AccessContext caller, CancellationToken ct)
    {
        if (!caller.Has(Permissions.LinesManage) || caller.Scope < RoleScope.Factory) return Array.Empty<FactoryLineDto>();
        return await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == null).OrderBy(l => l.Id)
            .Select(l => new FactoryLineDto(l.Id, l.Name, l.Workstations, l.IsActive, l.FactoryId)).ToListAsync(ct);
    }

    public async Task<AccessResult<FactoryLineDto>> AttachLineAsync(AccessContext caller, Guid factoryId, string lineId, string? traceId, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId) || caller.Scope < RoleScope.Factory)
            return AccessResult<FactoryLineDto>.NotFound("factory_not_found", "Factory not found.");
        if (!await _db.Factories.AnyAsync(f => f.Id == factoryId && f.IsActive, ct))
            return AccessResult<FactoryLineDto>.NotFound("factory_not_found", "Factory not found or deactivated.");
        var line = await _db.ProductionLines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return AccessResult<FactoryLineDto>.NotFound("line_not_found", "Production line not found.");
        if (line.FactoryId == factoryId) return AccessResult<FactoryLineDto>.Ok(LineDto(line));
        // Only a Super Admin moves a line out of another factory; a Factory Admin can claim unattached lines.
        if (line.FactoryId is not null && !caller.IsPlatform)
            return AccessResult<FactoryLineDto>.Conflict("line_in_other_factory", "This line belongs to another factory.");
        var from = line.FactoryId;
        line.FactoryId = factoryId;
        line.UpdatedAt = DateTime.UtcNow;
        // Supervisors of the previous factory lose the line.
        if (from is not null)
            await _db.UserLines.Where(a => a.LineId == lineId).ExecuteDeleteAsync(ct);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "line.attached", AccessAudit.EntityLine, line.Id,
            from?.ToString() ?? "(none)", factoryId.ToString(), $"line {line.Id} attached to factory", traceId, factoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<FactoryLineDto>.Ok(LineDto(line));
    }

    public async Task<AccessResult<FactoryLineDto>> DetachLineAsync(AccessContext caller, Guid factoryId, string lineId, string? traceId, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId) || caller.Scope < RoleScope.Factory)
            return AccessResult<FactoryLineDto>.NotFound("factory_not_found", "Factory not found.");
        var line = await _db.ProductionLines.FirstOrDefaultAsync(l => l.Id == lineId && l.FactoryId == factoryId, ct);
        if (line is null) return AccessResult<FactoryLineDto>.NotFound("line_not_found", "Production line not found in this factory.");
        if (await _db.Workers.AnyAsync(w => w.LineId == lineId, ct))
            return AccessResult<FactoryLineDto>.Conflict("line_has_workers", "Workers are assigned to this line. Move them first.");
        var holders = await _db.UserLines.Where(a => a.LineId == lineId).Select(a => a.UserId).ToListAsync(ct);
        await _db.UserLines.Where(a => a.LineId == lineId).ExecuteDeleteAsync(ct);
        line.FactoryId = null;
        line.UpdatedAt = DateTime.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "line.detached", AccessAudit.EntityLine, line.Id,
            factoryId.ToString(), "(none)", $"line {line.Id} detached from factory; {holders.Count} supervisor assignment(s) removed", traceId, factoryId));
        await _db.SaveChangesAsync(ct);
        foreach (var h in holders) _sessions.Invalidate(h);
        return AccessResult<FactoryLineDto>.Ok(LineDto(line));
    }

    private static FactoryLineDto LineDto(ProductionLine l) => new(l.Id, l.Name, l.Workstations, l.IsActive, l.FactoryId);

    private Task<FactoryDto?> DtoAsync(Guid id, CancellationToken ct) =>
        _db.Factories.AsNoTracking().Where(f => f.Id == id).Select(f => new FactoryDto(
            f.Id, f.Code, f.Name, f.Location, f.IsActive,
            _db.UserFactories.Count(a => a.FactoryId == f.Id),
            _db.ProductionLines.Count(l => l.FactoryId == f.Id),
            f.CreatedAt, f.UpdatedAt)).FirstOrDefaultAsync(ct);

    private static AccessResult<FactoryDto> NotFound() => AccessResult<FactoryDto>.NotFound("factory_not_found", "Factory not found.");
    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>
/// Worker profiles. Reach: Super Admin everything; factory roles their
/// factories; a Production Supervisor only workers on assigned lines; a Worker
/// only their own profile. Employment details are returned only to callers
/// with <c>workers.manage</c> and to the worker themself. Out-of-scope
/// profiles answer 404.
/// </summary>
public sealed class WorkerService : IWorkerService
{
    public const int MaxPageSize = 100;
    private readonly BunonBrainDbContext _db;
    private readonly ISessionValidator _sessions;
    public WorkerService(BunonBrainDbContext db, ISessionValidator sessions) { _db = db; _sessions = sessions; }

    private IQueryable<WorkerProfile> Visible(AccessContext caller)
    {
        var workers = _db.Workers.AsNoTracking();
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => workers,
            RoleScope.Factory => workers.Where(w => factories.Contains(w.FactoryId)),
            RoleScope.Line => workers.Where(w => factories.Contains(w.FactoryId) && w.LineId != null && lines.Contains(w.LineId)),
            _ => workers.Where(w => w.UserId == caller.UserId),
        };
    }

    public async Task<PagedResult<WorkerDto>> ListAsync(AccessContext caller, WorkerListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var workers = caller.Has(Permissions.WorkersView) ? Visible(caller) : _db.Workers.AsNoTracking().Where(_ => false);
        if (q.FactoryId is { } f) workers = workers.Where(w => w.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(q.LineId)) workers = workers.Where(w => w.LineId == q.LineId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + q.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            workers = workers.Where(w => EF.Functions.ILike(w.FullName, pattern, "\\") || EF.Functions.ILike(w.EmployeeCode, pattern, "\\"));
        }
        var total = await workers.CountAsync(ct);
        var rows = await workers.OrderBy(w => w.EmployeeCode).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<WorkerDto>(rows.Select(w => ToDto(caller, w)).ToList(), page, size, total);
    }

    public async Task<AccessResult<WorkerDto>> GetAsync(AccessContext caller, Guid workerId, CancellationToken ct)
    {
        var w = await _db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (w is null) return NotFound();
        var own = w.UserId == caller.UserId && caller.Has(Permissions.SelfView);
        if (!own && !(caller.Has(Permissions.WorkersView) && await Visible(caller).AnyAsync(x => x.Id == workerId, ct))) return NotFound();
        return AccessResult<WorkerDto>.Ok(ToDto(caller, w));
    }

    public async Task<WorkerDto?> MineAsync(AccessContext caller, CancellationToken ct)
    {
        var w = await _db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == caller.UserId, ct);
        return w is null ? null : ToDto(caller, w);
    }

    public async Task<AccessResult<WorkerDto>> CreateAsync(AccessContext caller, CreateWorkerRequest r, string? traceId, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(r.FactoryId) || caller.Scope < RoleScope.Factory)
            return AccessResult<WorkerDto>.Forbidden("factory_out_of_scope", "You can only add workers to your own factory.");
        if (!await _db.Factories.AnyAsync(f => f.Id == r.FactoryId && f.IsActive, ct))
            return AccessResult<WorkerDto>.Invalid("factory_not_found", "Factory not found or deactivated.");
        var lineError = await CheckLineAsync(caller, r.FactoryId, r.LineId, ct);
        if (lineError is not null) return lineError;
        var code = r.EmployeeCode.Trim().ToUpperInvariant();
        if (await _db.Workers.AnyAsync(w => w.FactoryId == r.FactoryId && w.EmployeeCode == code, ct))
            return AccessResult<WorkerDto>.Conflict("employee_code_taken", $"Employee code {code} is already used in this factory.");
        var now = DateTimeOffset.UtcNow;
        var worker = new WorkerProfile
        {
            FactoryId = r.FactoryId, EmployeeCode = code, FullName = r.FullName.Trim(), Designation = Clean(r.Designation),
            LineId = Clean(r.LineId), Workstation = Clean(r.Workstation), Phone = Clean(r.Phone), JoinedOn = r.JoinedOn,
            Grade = Clean(r.Grade), CreatedAt = now, UpdatedAt = now,
        };
        _db.Workers.Add(worker);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.created", AccessAudit.EntityWorker, worker.Id.ToString(),
            null, "active", $"worker {code} ({worker.FullName}) created", traceId, worker.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<WorkerDto>.Created(ToDto(caller, worker));
    }

    public async Task<AccessResult<WorkerDto>> UpdateAsync(AccessContext caller, Guid workerId, UpdateWorkerRequest r, string? traceId, CancellationToken ct)
    {
        if (!await Visible(caller).AnyAsync(w => w.Id == workerId, ct)) return NotFound();
        var w = await _db.Workers.FirstAsync(x => x.Id == workerId, ct);
        w.FullName = r.FullName.Trim();
        w.Designation = Clean(r.Designation);
        w.Phone = Clean(r.Phone);
        w.JoinedOn = r.JoinedOn;
        w.Grade = Clean(r.Grade);
        var statusChanged = w.IsActive != r.IsActive;
        w.IsActive = r.IsActive;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.updated", AccessAudit.EntityWorker, w.Id.ToString(),
            null, statusChanged ? (w.IsActive ? "active" : "inactive") : null, $"worker {w.EmployeeCode} profile updated", traceId, w.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<WorkerDto>.Ok(ToDto(caller, w));
    }

    public async Task<AccessResult<WorkerDto>> AssignWorkstationAsync(AccessContext caller, Guid workerId, AssignWorkstationRequest r, string? traceId, CancellationToken ct)
    {
        if (!await Visible(caller).AnyAsync(w => w.Id == workerId, ct)) return NotFound();
        var w = await _db.Workers.FirstAsync(x => x.Id == workerId, ct);
        var lineId = Clean(r.LineId);
        var lineError = await CheckLineAsync(caller, w.FactoryId, lineId, ct);
        if (lineError is not null) return lineError;
        // A supervisor can't move a worker off their lines to one they don't run.
        if (caller.IsLineScoped && lineId is null)
            return AccessResult<WorkerDto>.Forbidden("line_out_of_scope", "Choose one of your lines.");
        var from = $"{w.LineId ?? "-"}/{w.Workstation ?? "-"}";
        w.LineId = lineId;
        w.Workstation = Clean(r.Workstation);
        w.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.workstation_assigned", AccessAudit.EntityWorker, w.Id.ToString(),
            from, $"{w.LineId ?? "-"}/{w.Workstation ?? "-"}", $"worker {w.EmployeeCode} moved", traceId, w.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<WorkerDto>.Ok(ToDto(caller, w));
    }

    public async Task<AccessResult<WorkerDto>> LinkAccountAsync(AccessContext caller, Guid workerId, LinkWorkerAccountRequest r, string? traceId, CancellationToken ct)
    {
        if (caller.Scope < RoleScope.Factory || !await Visible(caller).AnyAsync(w => w.Id == workerId, ct)) return NotFound();
        var w = await _db.Workers.FirstAsync(x => x.Id == workerId, ct);
        var before = w.UserId;
        if (r.UserId is { } uid)
        {
            var user = await _db.Users.AsNoTracking().Include(u => u.Factories).Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == uid, ct);
            var inFactory = user is not null && user.Factories.Any(f => f.FactoryId == w.FactoryId)
                            && (caller.IsPlatform || user.Factories.All(f => caller.FactoryIds.Contains(f.FactoryId)))
                            && user.Roles.All(x => x.RoleId != SystemRoles.SuperAdmin);
            if (!inFactory) return AccessResult<WorkerDto>.Invalid("user_not_in_factory", "Choose an account that belongs to this worker's factory.");
            if (await _db.Workers.AnyAsync(x => x.UserId == uid && x.Id != workerId, ct))
                return AccessResult<WorkerDto>.Conflict("account_already_linked", "That account is already linked to another worker.");
        }
        w.UserId = r.UserId;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, r.UserId is null ? "worker.account_unlinked" : "worker.account_linked",
            AccessAudit.EntityWorker, w.Id.ToString(), before?.ToString() ?? "(none)", r.UserId?.ToString() ?? "(none)",
            $"login account of worker {w.EmployeeCode} {(r.UserId is null ? "unlinked" : "linked")}", traceId, w.FactoryId));
        await _db.SaveChangesAsync(ct);
        if (before is { } b) _sessions.Invalidate(b);
        return AccessResult<WorkerDto>.Ok(ToDto(caller, w));
    }

    private async Task<AccessResult<WorkerDto>?> CheckLineAsync(AccessContext caller, Guid factoryId, string? lineId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lineId)) return null;
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null || line.FactoryId != factoryId)
            return AccessResult<WorkerDto>.Invalid("line_not_in_factory", "That line doesn't belong to the worker's factory.");
        if (!caller.CanAccessLine(line.Id, line.FactoryId))
            return AccessResult<WorkerDto>.Forbidden("line_out_of_scope", "That line is outside your assignment.");
        return null;
    }

    private static WorkerDto ToDto(AccessContext caller, WorkerProfile w)
    {
        var sensitive = caller.Has(Permissions.WorkersManage) || (w.UserId == caller.UserId && caller.Has(Permissions.SelfView));
        return new WorkerDto(w.Id, w.FactoryId, w.EmployeeCode, w.FullName, w.Designation, w.LineId, w.Workstation, w.IsActive,
            w.UserId is not null, sensitive || caller.Has(Permissions.UsersView) ? w.UserId : null,
            sensitive ? w.Phone : null, sensitive ? w.JoinedOn : null, sensitive ? w.Grade : null, w.CreatedAt, w.UpdatedAt);
    }

    private static AccessResult<WorkerDto> NotFound() => AccessResult<WorkerDto>.NotFound("worker_not_found", "Worker not found.");
    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>
/// The audit log, scoped: a Super Admin reads everything; anyone else with
/// <c>audit.view</c> reads only entries tagged with one of their factories.
/// </summary>
public sealed class AuditQueryService : IAuditQueryService
{
    public const int MaxLimit = 200;
    private readonly BunonBrainDbContext _db;
    public AuditQueryService(BunonBrainDbContext db) { _db = db; }

    public async Task<IReadOnlyList<AuditEntryDto>> ListAsync(AccessContext caller, AuditQuery q, CancellationToken ct)
    {
        var entries = _db.AuditLog.AsNoTracking();
        if (!caller.IsPlatform)
        {
            var mine = caller.FactoryIds.Select(f => (Guid?)f).ToList();
            entries = entries.Where(e => e.FactoryId != null && mine.Contains(e.FactoryId));
        }
        if (q.FactoryId is { } fid) entries = entries.Where(e => e.FactoryId == fid);
        if (!string.IsNullOrWhiteSpace(q.EntityType)) entries = entries.Where(e => e.EntityType == q.EntityType);
        if (!string.IsNullOrWhiteSpace(q.EntityId)) entries = entries.Where(e => e.EntityId == q.EntityId);
        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            var action = q.Action.Trim();
            entries = action.EndsWith('.') ? entries.Where(e => e.Action.StartsWith(action)) : entries.Where(e => e.Action == action);
        }
        if (q.ActorUserId is { } actor) entries = entries.Where(e => e.ActorUserId == actor);
        if (q.Before is { } before) entries = entries.Where(e => e.Id < before);
        return await entries.OrderByDescending(e => e.Id).Take(Math.Clamp(q.Limit, 1, MaxLimit))
            .Select(e => new AuditEntryDto(e.Id, e.OccurredAt, e.ActorUserId, e.ActorEmail, e.ActorRole, e.Action,
                e.EntityType, e.EntityId, e.FromState, e.ToState, e.Reason, e.TraceId, e.FactoryId))
            .ToListAsync(ct);
    }
}
