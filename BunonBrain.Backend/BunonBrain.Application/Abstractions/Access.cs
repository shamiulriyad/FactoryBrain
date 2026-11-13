using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Approvals;

namespace BunonBrain.Application.Abstractions.Interfaces;

/// <summary>Loads a user's current access (roles, permissions, factories, lines, stamp) from the database.</summary>
public interface IAccessSnapshotLoader
{
    /// <summary>Null when the user doesn't exist.</summary>
    Task<AccessSnapshot?> LoadAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Decides, per request, whether a validated access token still reflects the
/// user's account: the user exists, is active, and the token's security stamp
/// matches. Changing roles, scope, status or password rotates the stamp, so
/// older tokens stop working at once (the browser silently refreshes).
/// </summary>
public interface ISessionValidator
{
    Task<bool> IsCurrentAsync(Guid userId, string? securityStamp, CancellationToken ct);

    /// <summary>Forget anything cached about <paramref name="userId"/> (call after changing the account).</summary>
    void Invalidate(Guid userId);
}

/// <summary>
/// Administration of other users' accounts. Every method enforces, on the
/// server, the caller's permission, factory scope and role authority:
/// <list type="bullet">
///   <item>A non-platform caller only sees and manages users whose factories are all inside the caller's factories.</item>
///   <item>A caller may only manage a user whose every role the caller could grant (so a Factory Admin can't touch another Factory Admin or a Super Admin).</item>
///   <item>A caller may only grant roles they are allowed to manage and whose permissions they hold themselves.</item>
///   <item>Nobody changes their own roles or status; the last active Super Admin can't be removed or deactivated.</item>
/// </list>
/// </summary>
public interface IUserAdminService
{
    Task<PagedResult<UserSummary>> ListAsync(AccessContext caller, UserListQuery query, CancellationToken ct);
    Task<AccessResult<UserDetail>> GetAsync(AccessContext caller, Guid userId, CancellationToken ct);
    Task<AccessResult<UserDetail>> CreateAsync(AccessContext caller, CreateUserRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<UserDetail>> UpdateAsync(AccessContext caller, Guid userId, UpdateUserRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<UserDetail>> SetStatusAsync(AccessContext caller, Guid userId, SetUserStatusRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<UserDetail>> AssignRolesAsync(AccessContext caller, Guid userId, AssignRolesRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<UserDetail>> AssignScopeAsync(AccessContext caller, Guid userId, AssignScopeRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<UserDetail>> ResetPasswordAsync(AccessContext caller, Guid userId, AdminResetPasswordRequest request, string? traceId, CancellationToken ct);
    /// <summary>Audit entries made by or about the user (scoped like the user itself).</summary>
    Task<AccessResult<IReadOnlyList<AuditEntryDto>>> ActivityAsync(AccessContext caller, Guid userId, int limit, CancellationToken ct);
}

/// <summary>Read side of the role catalog, with per-role user counts in the caller's scope.</summary>
public interface IRoleCatalogService
{
    Task<IReadOnlyList<RoleDto>> RolesAsync(AccessContext caller, CancellationToken ct);
    Task<RoleDto?> RoleAsync(AccessContext caller, string roleId, CancellationToken ct);
    IReadOnlyList<PermissionModuleDto> Permissions();
}

/// <summary>Factories and their production lines.</summary>
public interface IFactoryService
{
    Task<IReadOnlyList<FactoryDto>> ListAsync(AccessContext caller, CancellationToken ct);
    Task<AccessResult<FactoryDto>> GetAsync(AccessContext caller, Guid factoryId, CancellationToken ct);
    Task<AccessResult<FactoryDto>> CreateAsync(AccessContext caller, CreateFactoryRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<FactoryDto>> UpdateAsync(AccessContext caller, Guid factoryId, UpdateFactoryRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<FactoryDto>> SetStatusAsync(AccessContext caller, Guid factoryId, SetFactoryStatusRequest request, string? traceId, CancellationToken ct);
    /// <summary>Lines of the factory the caller may see (a Production Supervisor sees only assigned lines).</summary>
    Task<AccessResult<IReadOnlyList<FactoryLineDto>>> LinesAsync(AccessContext caller, Guid factoryId, CancellationToken ct);
    /// <summary>Lines with no factory yet (candidates to attach). Platform and factory admins only.</summary>
    Task<IReadOnlyList<FactoryLineDto>> UnassignedLinesAsync(AccessContext caller, CancellationToken ct);
    Task<AccessResult<FactoryLineDto>> AttachLineAsync(AccessContext caller, Guid factoryId, string lineId, string? traceId, CancellationToken ct);
    Task<AccessResult<FactoryLineDto>> DetachLineAsync(AccessContext caller, Guid factoryId, string lineId, string? traceId, CancellationToken ct);
}

/// <summary>The audit log, filtered to what the caller may see.</summary>
public interface IAuditQueryService
{
    Task<IReadOnlyList<AuditEntryDto>> ListAsync(AccessContext caller, AuditQuery query, CancellationToken ct);
}
