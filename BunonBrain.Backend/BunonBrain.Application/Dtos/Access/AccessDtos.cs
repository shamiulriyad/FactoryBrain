using BunonBrain.Application.Access;

namespace BunonBrain.Application.Dtos.Access;

// ─── Shared ────────────────────────────────────────────────────────────────

/// <summary>A page of results.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record LineRef(string Id, string Name, Guid? FactoryId);

/// <summary>Short reference to a worker: the permanent Worker ID (e.g. <c>W-001</c>) and name.</summary>
public sealed record WorkerRef(Guid Id, string WorkerCode, string FullName);

/// <summary>Outcome of an access-management command. Controllers map it to HTTP.</summary>
public enum AccessOutcome { Ok, Created, NotFound, Forbidden, Conflict, Invalid }

/// <summary>
/// Result of an access-management command. <see cref="Code"/> is a stable
/// machine-readable reason (e.g. <c>cannot_modify_super_admin</c>) that the
/// API returns in the problem body; <see cref="Message"/> is the human text.
/// </summary>
public sealed record AccessResult<T>(AccessOutcome Outcome, T? Value = default, string? Code = null, string? Message = null)
{
    public static AccessResult<T> Ok(T value) => new(AccessOutcome.Ok, value);
    public static AccessResult<T> Created(T value) => new(AccessOutcome.Created, value);
    public static AccessResult<T> NotFound(string code, string message) => new(AccessOutcome.NotFound, default, code, message);
    public static AccessResult<T> Forbidden(string code, string message) => new(AccessOutcome.Forbidden, default, code, message);
    public static AccessResult<T> Conflict(string code, string message) => new(AccessOutcome.Conflict, default, code, message);
    public static AccessResult<T> Invalid(string code, string message) => new(AccessOutcome.Invalid, default, code, message);
}

// ─── Users ─────────────────────────────────────────────────────────────────

/// <summary>GET /api/users query. <c>sort</c>: email | name | created | lastLogin, prefix "-" for descending.</summary>
public sealed record UserListQuery(
    string? Search = null, string? Role = null, Guid? FactoryId = null, string? Status = null,
    string? Sort = null, int Page = 1, int PageSize = 20);

public sealed record UserSummary(
    Guid Id,
    string Email,
    string? FullName,
    bool IsActive,
    bool MustChangePassword,
    IReadOnlyList<string> Roles,
    IReadOnlyList<FactoryRef> Factories,
    int LineCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    bool CanManage);

/// <summary>What the caller may do to one user (the server enforces the same rules).</summary>
public sealed record UserCapabilities(
    bool Edit, bool ChangeStatus, bool AssignRoles, bool ChangeScope, bool ResetPassword,
    IReadOnlyList<string> GrantableRoles);

public sealed record UserDetail(
    Guid Id,
    string Email,
    string? FullName,
    string? Phone,
    bool IsActive,
    bool MustChangePassword,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string Scope,
    IReadOnlyList<FactoryRef> Factories,
    IReadOnlyList<LineRef> Lines,
    WorkerRef? Worker,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? DeactivatedAt,
    UserCapabilities Can);

/// <summary>POST /api/users. The temporary password must be changed at first sign-in.</summary>
public sealed record CreateUserRequest(
    string Email,
    string? FullName,
    string? Phone,
    string TemporaryPassword,
    IReadOnlyList<string>? Roles,
    IReadOnlyList<Guid>? FactoryIds,
    IReadOnlyList<string>? LineIds);

/// <summary>PUT /api/users/{id}.</summary>
public sealed record UpdateUserRequest(string Email, string? FullName, string? Phone);

/// <summary>POST /api/users/{id}/status.</summary>
public sealed record SetUserStatusRequest(bool IsActive, string? Reason);

/// <summary>PUT /api/users/{id}/roles: the complete new role set.</summary>
public sealed record AssignRolesRequest(IReadOnlyList<string> Roles);

/// <summary>PUT /api/users/{id}/scope: the complete new factory and line assignment.</summary>
public sealed record AssignScopeRequest(IReadOnlyList<Guid> FactoryIds, IReadOnlyList<string>? LineIds);

/// <summary>POST /api/users/{id}/reset-password.</summary>
public sealed record AdminResetPasswordRequest(string TemporaryPassword);

// ─── Account (self-service) ────────────────────────────────────────────────

public sealed record AccountProfile(
    Guid Id,
    string Email,
    string? FullName,
    string? Phone,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string Scope,
    IReadOnlyList<FactoryRef> Factories,
    IReadOnlyList<LineRef> Lines,
    WorkerRef? Worker,
    bool MustChangePassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>PUT /api/account/profile. Email and roles are changed by an administrator, not here.</summary>
public sealed record UpdateProfileRequest(string? FullName, string? Phone);

/// <summary>POST /api/account/password.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// ─── Roles & permissions ───────────────────────────────────────────────────

public sealed record RoleDto(
    string Id,
    string Name,
    string Description,
    string Scope,
    int Rank,
    bool IsSystem,
    IReadOnlyList<string> ManagedBy,
    IReadOnlyList<string> Permissions,
    int UserCount,
    bool CanGrant);

public sealed record PermissionDto(
    string Code, string Module, string Name, string Description, bool PlatformOnly, bool Reserved,
    IReadOnlyList<string> Roles);

public sealed record PermissionModuleDto(string Module, IReadOnlyList<PermissionDto> Permissions);

// ─── Factories & lines ─────────────────────────────────────────────────────

public sealed record FactoryDto(
    Guid Id, string Code, string Name, string? Location, bool IsActive,
    int UserCount, int LineCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateFactoryRequest(string Code, string Name, string? Location);

public sealed record UpdateFactoryRequest(string Name, string? Location);

public sealed record SetFactoryStatusRequest(bool IsActive);

public sealed record FactoryLineDto(string Id, string Name, int Workstations, bool IsActive, Guid? FactoryId);

// ─── Audit ─────────────────────────────────────────────────────────────────

/// <summary>GET /api/audit query.</summary>
public sealed record AuditQuery(
    string? EntityType = null, string? EntityId = null, string? Action = null, Guid? ActorUserId = null,
    Guid? FactoryId = null, int Limit = 50, long? Before = null);
