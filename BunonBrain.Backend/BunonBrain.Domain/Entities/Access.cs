namespace BunonBrain.Domain.Entities;

// Access control: factories, roles, permissions and the user ↔ role /
// factory / line assignments. The role and permission rows are a projection
// of the code catalog (BunonBrain.Application.Access.AccessCatalog), synced
// at startup; nothing edits them through the API.

/// <summary>A garment factory (tenant). Every factory-level user is assigned to one or more.</summary>
public class Factory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Short unique code, upper case (e.g. <c>DHK-1</c>).</summary>
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Location { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A system role (projection of the catalog).</summary>
public class Role
{
    /// <summary>Stable id, e.g. <c>FactoryAdmin</c>.</summary>
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    /// <summary>Platform, Factory, Line or Self.</summary>
    public string Scope { get; set; } = default!;
    public int Rank { get; set; }
    /// <summary>System roles can't be edited or deleted.</summary>
    public bool IsSystem { get; set; } = true;
    public List<RolePermission> Permissions { get; set; } = new();
}

/// <summary>A permission (projection of the catalog).</summary>
public class Permission
{
    /// <summary>Dotted code, e.g. <c>users.create</c>.</summary>
    public string Code { get; set; } = default!;
    public string Module { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public bool PlatformOnly { get; set; }
    public bool Reserved { get; set; }
}

public class RolePermission
{
    public string RoleId { get; set; } = default!;
    public string PermissionCode { get; set; } = default!;
}

public class UserRole
{
    public Guid UserId { get; set; }
    public string RoleId { get; set; } = default!;
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? AssignedByUserId { get; set; }
}

public class UserFactoryAssignment
{
    public Guid UserId { get; set; }
    public Guid FactoryId { get; set; }
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? AssignedByUserId { get; set; }
}

public class UserProductionLineAssignment
{
    public Guid UserId { get; set; }
    /// <summary><see cref="ProductionLine.Id"/> (slug).</summary>
    public string LineId { get; set; } = default!;
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? AssignedByUserId { get; set; }
}

/// <summary>
/// A worker's employment profile. Separate from <see cref="User"/>: a worker
/// needs no login account, and at most one account can be linked
/// (<see cref="UserId"/>) so the worker can see their own records.
/// </summary>
public class WorkerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>Unique within the factory.</summary>
    public string EmployeeCode { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public string? Designation { get; set; }
    public string? LineId { get; set; }
    public string? Workstation { get; set; }
    public string? Phone { get; set; }
    public DateOnly? JoinedOn { get; set; }
    public string? Grade { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
