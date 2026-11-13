using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Workers;

/// <summary>
/// Departments and production floors of a factory: the reference data a
/// worker record points at. Anyone who works inside the factory (factory or
/// line scope) can list them; creating and editing needs
/// <c>departments.manage</c> / <c>lines.manage</c> (checked on the endpoint)
/// and factory-wide scope in that factory (checked here). They are never
/// deleted, only deactivated, so worker records keep pointing at them.
/// </summary>
public sealed class OrganizationService : IOrganizationService
{
    private readonly BunonBrainDbContext _db;
    public OrganizationService(BunonBrainDbContext db) { _db = db; }

    private static bool CanRead(AccessContext caller, Guid factoryId) =>
        caller.CanAccessFactory(factoryId) && caller.Scope >= RoleScope.Line;

    private static bool CanWrite(AccessContext caller, Guid factoryId) =>
        caller.CanAccessFactory(factoryId) && caller.Scope >= RoleScope.Factory;

    private static AccessResult<T> FactoryNotFound<T>() => AccessResult<T>.NotFound("factory_not_found", "Factory not found.");

    // ─── Departments ───────────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<OrgUnitDto>>> DepartmentsAsync(AccessContext caller, Guid factoryId, CancellationToken ct)
    {
        if (!CanRead(caller, factoryId) || !await _db.Factories.AnyAsync(f => f.Id == factoryId, ct))
            return FactoryNotFound<IReadOnlyList<OrgUnitDto>>();
        var rows = await _db.Departments.AsNoTracking().Where(d => d.FactoryId == factoryId).OrderBy(d => d.Name)
            .Select(d => new OrgUnitDto(d.Id, d.FactoryId, d.Code, d.Name, d.IsActive,
                _db.Workers.Count(w => w.DepartmentId == d.Id), d.CreatedAt, d.UpdatedAt)).ToListAsync(ct);
        return AccessResult<IReadOnlyList<OrgUnitDto>>.Ok(rows);
    }

    public async Task<AccessResult<OrgUnitDto>> CreateDepartmentAsync(AccessContext caller, Guid factoryId, CreateOrgUnitRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanWrite(caller, factoryId) || !await _db.Factories.AnyAsync(f => f.Id == factoryId && f.IsActive, ct))
            return FactoryNotFound<OrgUnitDto>();
        var (code, name) = (r.Code.Trim().ToUpperInvariant(), r.Name.Trim());
        if (await _db.Departments.AnyAsync(d => d.FactoryId == factoryId && d.Code == code, ct))
            return AccessResult<OrgUnitDto>.Conflict("department_code_taken", $"This factory already has a department with code {code}.");
        if (await _db.Departments.AnyAsync(d => d.FactoryId == factoryId && EF.Functions.ILike(d.Name, Like(name), "\\"), ct))
            return AccessResult<OrgUnitDto>.Conflict("department_name_taken", $"This factory already has a department named {name}.");
        var now = DateTimeOffset.UtcNow;
        var dept = new Department { FactoryId = factoryId, Code = code, Name = name, CreatedAt = now, UpdatedAt = now };
        _db.Departments.Add(dept);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "department.created", AccessAudit.EntityDepartment, dept.Id.ToString(),
            null, "active", $"department {code} ({name}) created", traceId, factoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return AccessResult<OrgUnitDto>.Conflict("department_code_taken", "This factory already has a department with this code or name.");
        }
        return AccessResult<OrgUnitDto>.Created(new OrgUnitDto(dept.Id, factoryId, code, name, true, 0, now, now));
    }

    public async Task<AccessResult<OrgUnitDto>> UpdateDepartmentAsync(AccessContext caller, Guid factoryId, Guid departmentId, UpdateOrgUnitRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanWrite(caller, factoryId)) return FactoryNotFound<OrgUnitDto>();
        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Id == departmentId && d.FactoryId == factoryId, ct);
        if (dept is null) return AccessResult<OrgUnitDto>.NotFound("department_not_found", "Department not found.");
        var name = r.Name.Trim();
        if (!string.Equals(name, dept.Name, StringComparison.OrdinalIgnoreCase)
            && await _db.Departments.AnyAsync(d => d.FactoryId == factoryId && d.Id != departmentId && EF.Functions.ILike(d.Name, Like(name), "\\"), ct))
            return AccessResult<OrgUnitDto>.Conflict("department_name_taken", $"This factory already has a department named {name}.");
        if (name != dept.Name || r.IsActive != dept.IsActive)
        {
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "department.updated", AccessAudit.EntityDepartment, dept.Id.ToString(),
                dept.IsActive ? "active" : "inactive", r.IsActive ? "active" : "inactive",
                $"department {dept.Code}: name {dept.Name} → {name}", traceId, factoryId));
            dept.Name = name;
            dept.IsActive = r.IsActive;
            dept.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        var workers = await _db.Workers.CountAsync(w => w.DepartmentId == dept.Id, ct);
        return AccessResult<OrgUnitDto>.Ok(new OrgUnitDto(dept.Id, factoryId, dept.Code, dept.Name, dept.IsActive, workers, dept.CreatedAt, dept.UpdatedAt));
    }

    // ─── Production floors ─────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<OrgUnitDto>>> FloorsAsync(AccessContext caller, Guid factoryId, CancellationToken ct)
    {
        if (!CanRead(caller, factoryId) || !await _db.Factories.AnyAsync(f => f.Id == factoryId, ct))
            return FactoryNotFound<IReadOnlyList<OrgUnitDto>>();
        var rows = await _db.ProductionFloors.AsNoTracking().Where(d => d.FactoryId == factoryId).OrderBy(d => d.Name)
            .Select(d => new OrgUnitDto(d.Id, d.FactoryId, d.Code, d.Name, d.IsActive,
                _db.Workers.Count(w => w.FloorId == d.Id), d.CreatedAt, d.UpdatedAt)).ToListAsync(ct);
        return AccessResult<IReadOnlyList<OrgUnitDto>>.Ok(rows);
    }

    public async Task<AccessResult<OrgUnitDto>> CreateFloorAsync(AccessContext caller, Guid factoryId, CreateOrgUnitRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanWrite(caller, factoryId) || !await _db.Factories.AnyAsync(f => f.Id == factoryId && f.IsActive, ct))
            return FactoryNotFound<OrgUnitDto>();
        var (code, name) = (r.Code.Trim().ToUpperInvariant(), r.Name.Trim());
        if (await _db.ProductionFloors.AnyAsync(d => d.FactoryId == factoryId && d.Code == code, ct))
            return AccessResult<OrgUnitDto>.Conflict("floor_code_taken", $"This factory already has a floor with code {code}.");
        if (await _db.ProductionFloors.AnyAsync(d => d.FactoryId == factoryId && EF.Functions.ILike(d.Name, Like(name), "\\"), ct))
            return AccessResult<OrgUnitDto>.Conflict("floor_name_taken", $"This factory already has a floor named {name}.");
        var now = DateTimeOffset.UtcNow;
        var floor = new ProductionFloor { FactoryId = factoryId, Code = code, Name = name, CreatedAt = now, UpdatedAt = now };
        _db.ProductionFloors.Add(floor);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "floor.created", AccessAudit.EntityFloor, floor.Id.ToString(),
            null, "active", $"production floor {code} ({name}) created", traceId, factoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return AccessResult<OrgUnitDto>.Conflict("floor_code_taken", "This factory already has a floor with this code or name.");
        }
        return AccessResult<OrgUnitDto>.Created(new OrgUnitDto(floor.Id, factoryId, code, name, true, 0, now, now));
    }

    public async Task<AccessResult<OrgUnitDto>> UpdateFloorAsync(AccessContext caller, Guid factoryId, Guid floorId, UpdateOrgUnitRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanWrite(caller, factoryId)) return FactoryNotFound<OrgUnitDto>();
        var floor = await _db.ProductionFloors.FirstOrDefaultAsync(d => d.Id == floorId && d.FactoryId == factoryId, ct);
        if (floor is null) return AccessResult<OrgUnitDto>.NotFound("floor_not_found", "Production floor not found.");
        var name = r.Name.Trim();
        if (!string.Equals(name, floor.Name, StringComparison.OrdinalIgnoreCase)
            && await _db.ProductionFloors.AnyAsync(d => d.FactoryId == factoryId && d.Id != floorId && EF.Functions.ILike(d.Name, Like(name), "\\"), ct))
            return AccessResult<OrgUnitDto>.Conflict("floor_name_taken", $"This factory already has a floor named {name}.");
        if (name != floor.Name || r.IsActive != floor.IsActive)
        {
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "floor.updated", AccessAudit.EntityFloor, floor.Id.ToString(),
                floor.IsActive ? "active" : "inactive", r.IsActive ? "active" : "inactive",
                $"production floor {floor.Code}: name {floor.Name} → {name}", traceId, factoryId));
            floor.Name = name;
            floor.IsActive = r.IsActive;
            floor.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        var workers = await _db.Workers.CountAsync(w => w.FloorId == floor.Id, ct);
        return AccessResult<OrgUnitDto>.Ok(new OrgUnitDto(floor.Id, factoryId, floor.Code, floor.Name, floor.IsActive, workers, floor.CreatedAt, floor.UpdatedAt));
    }

    /// <summary>An ILIKE pattern that matches <paramref name="value"/> exactly (case-insensitively).</summary>
    internal static string Like(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
