using System.Diagnostics;
using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Maps <see cref="AccessResult{T}"/> to HTTP. Failures are RFC 7807 problem
/// details with a stable machine-readable <c>code</c> and the request's
/// <c>traceId</c>: 400 invalid, 403 forbidden, 404 not found (also used for
/// records outside the caller's scope, so their existence isn't revealed),
/// 409 conflict.
/// </summary>
internal static class AccessResults
{
    public static IActionResult From<T>(ControllerBase c, AccessResult<T> r, Func<T, string>? location = null) => r.Outcome switch
    {
        AccessOutcome.Ok => c.Ok(r.Value),
        AccessOutcome.Created => new CreatedResult(location is null ? string.Empty : location(r.Value!), r.Value),
        AccessOutcome.NotFound => Problem(c, StatusCodes.Status404NotFound, "Not found", r.Code, r.Message),
        AccessOutcome.Forbidden => Problem(c, StatusCodes.Status403Forbidden, "Forbidden", r.Code, r.Message),
        AccessOutcome.Conflict => Problem(c, StatusCodes.Status409Conflict, "Conflict", r.Code, r.Message),
        _ => Problem(c, StatusCodes.Status400BadRequest, "Invalid request", r.Code, r.Message),
    };

    public static IActionResult Problem(ControllerBase c, int status, string title, string? code, string? detail)
    {
        var pd = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = status switch
            {
                400 => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1",
                403 => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4",
                404 => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.5",
                _ => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.10",
            },
            Instance = c.HttpContext.Request.Path,
        };
        pd.Extensions["code"] = code;
        pd.Extensions["traceId"] = TraceId(c);
        return new ObjectResult(pd) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    public static string TraceId(ControllerBase c) => Activity.Current?.Id ?? c.HttpContext.TraceIdentifier;

    public static bool TryId(string raw, out Guid id) => Guid.TryParse(raw, out id);
}

/// <summary>
/// User administration. Every action checks the permission named on it, and
/// the service additionally enforces factory scope, role authority, self- and
/// Super-Admin protection (see <see cref="IUserAdminService"/>).
/// </summary>
[ApiController]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly IUserAdminService _users;
    public UsersController(IUserAdminService users) { _users = users; }

    /// <summary>GET /api/users?search=&amp;role=&amp;factoryId=&amp;status=active|inactive|must_change_password&amp;sort=email|-email|name|created|lastLogin&amp;page=&amp;pageSize=</summary>
    [HttpGet]
    [RequirePermission(Permissions.UsersView)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] string? role, [FromQuery] Guid? factoryId,
        [FromQuery] string? status, [FromQuery] string? sort, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _users.ListAsync(this.Caller(), new UserListQuery(search, role, factoryId, status, sort, page ?? 1, pageSize ?? 20), ct));

    [HttpGet("{id}")]
    [RequirePermission(Permissions.UsersView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => AccessResults.TryId(id, out var userId)
            ? AccessResults.From(this, await _users.GetAsync(this.Caller(), userId, ct))
            : NotFoundUser();

    [HttpPost]
    [RequirePermission(Permissions.UsersCreate)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest body, CancellationToken ct)
        => AccessResults.From(this, await _users.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), u => $"/api/users/{u.Id}");

    [HttpPut("{id}")]
    [RequirePermission(Permissions.UsersUpdate)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateUserRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var userId)
            ? AccessResults.From(this, await _users.UpdateAsync(this.Caller(), userId, body, AccessResults.TraceId(this), ct))
            : NotFoundUser();

    /// <summary>POST /api/users/{id}/status — activate or deactivate (deactivation ends every session at once).</summary>
    [HttpPost("{id}/status")]
    [RequirePermission(Permissions.UsersDeactivate)]
    public async Task<IActionResult> SetStatus([FromRoute] string id, [FromBody] SetUserStatusRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var userId)
            ? AccessResults.From(this, await _users.SetStatusAsync(this.Caller(), userId, body, AccessResults.TraceId(this), ct))
            : NotFoundUser();

    /// <summary>PUT /api/users/{id}/roles — replace the user's role set.</summary>
    [HttpPut("{id}/roles")]
    [RequirePermission(Permissions.RolesAssign)]
    public async Task<IActionResult> AssignRoles([FromRoute] string id, [FromBody] AssignRolesRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var userId)
            ? AccessResults.From(this, await _users.AssignRolesAsync(this.Caller(), userId, body, AccessResults.TraceId(this), ct))
            : NotFoundUser();

    /// <summary>PUT /api/users/{id}/scope — replace the user's factory and production-line assignment.</summary>
    [HttpPut("{id}/scope")]
    [RequirePermission(Permissions.UsersUpdate)]
    public async Task<IActionResult> AssignScope([FromRoute] string id, [FromBody] AssignScopeRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var userId)
            ? AccessResults.From(this, await _users.AssignScopeAsync(this.Caller(), userId, body, AccessResults.TraceId(this), ct))
            : NotFoundUser();

    /// <summary>POST /api/users/{id}/reset-password — set a temporary password; the user must change it at next sign-in.</summary>
    [HttpPost("{id}/reset-password")]
    [RequirePermission(Permissions.UsersResetPassword)]
    public async Task<IActionResult> ResetPassword([FromRoute] string id, [FromBody] AdminResetPasswordRequest body, CancellationToken ct)
    {
        if (!AccessResults.TryId(id, out var userId)) return NotFoundUser();
        Response.Headers.CacheControl = "no-store";
        return AccessResults.From(this, await _users.ResetPasswordAsync(this.Caller(), userId, body, AccessResults.TraceId(this), ct));
    }

    /// <summary>GET /api/users/{id}/activity — audit entries by or about the user.</summary>
    [HttpGet("{id}/activity")]
    [RequirePermission(Permissions.AuditView)]
    public async Task<IActionResult> Activity([FromRoute] string id, [FromQuery] int? limit, CancellationToken ct)
        => AccessResults.TryId(id, out var userId)
            ? AccessResults.From(this, await _users.ActivityAsync(this.Caller(), userId, limit ?? 50, ct))
            : NotFoundUser();

    private IActionResult NotFoundUser() => AccessResults.Problem(this, 404, "Not found", "user_not_found", "User not found.");
}

/// <summary>The role catalog and the permission overview (read-only; system roles are protected).</summary>
[ApiController]
[Route("api")]
public class RolesController : ControllerBase
{
    private readonly IRoleCatalogService _roles;
    public RolesController(IRoleCatalogService roles) { _roles = roles; }

    [HttpGet("roles")]
    [RequirePermission(Permissions.RolesView)]
    public async Task<IActionResult> Roles(CancellationToken ct) => Ok(await _roles.RolesAsync(this.Caller(), ct));

    [HttpGet("roles/{id}")]
    [RequirePermission(Permissions.RolesView)]
    public async Task<IActionResult> Role([FromRoute] string id, CancellationToken ct)
    {
        var role = await _roles.RoleAsync(this.Caller(), id, ct);
        return role is null ? AccessResults.Problem(this, 404, "Not found", "role_not_found", "Role not found.") : Ok(role);
    }

    [HttpGet("permissions")]
    [RequirePermission(Permissions.RolesView)]
    public IActionResult PermissionList() => Ok(_roles.Permissions());
}

/// <summary>Factories (create / edit / (de)activate: Super Admin) and their production lines.</summary>
[ApiController]
[Route("api/factories")]
public class FactoriesController : ControllerBase
{
    private readonly IFactoryService _factories;
    public FactoriesController(IFactoryService factories) { _factories = factories; }

    [HttpGet]
    [RequirePermission(Permissions.FactoriesView, Permissions.UsersView)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await _factories.ListAsync(this.Caller(), ct));

    [HttpGet("{id}")]
    [RequirePermission(Permissions.FactoriesView, Permissions.UsersView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => AccessResults.TryId(id, out var fid) ? AccessResults.From(this, await _factories.GetAsync(this.Caller(), fid, ct)) : NotFoundFactory();

    [HttpPost]
    [RequirePermission(Permissions.FactoriesManage)]
    public async Task<IActionResult> Create([FromBody] CreateFactoryRequest body, CancellationToken ct)
        => AccessResults.From(this, await _factories.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), f => $"/api/factories/{f.Id}");

    [HttpPut("{id}")]
    [RequirePermission(Permissions.FactoriesManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateFactoryRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var fid)
            ? AccessResults.From(this, await _factories.UpdateAsync(this.Caller(), fid, body, AccessResults.TraceId(this), ct))
            : NotFoundFactory();

    [HttpPost("{id}/status")]
    [RequirePermission(Permissions.FactoriesManage)]
    public async Task<IActionResult> SetStatus([FromRoute] string id, [FromBody] SetFactoryStatusRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var fid)
            ? AccessResults.From(this, await _factories.SetStatusAsync(this.Caller(), fid, body, AccessResults.TraceId(this), ct))
            : NotFoundFactory();

    /// <summary>GET /api/factories/{id}/lines — a Production Supervisor sees only assigned lines.</summary>
    [HttpGet("{id}/lines")]
    [RequirePermission(Permissions.LinesView, Permissions.UsersUpdate)]
    public async Task<IActionResult> Lines([FromRoute] string id, CancellationToken ct)
        => AccessResults.TryId(id, out var fid) ? AccessResults.From(this, await _factories.LinesAsync(this.Caller(), fid, ct)) : NotFoundFactory();

    /// <summary>GET /api/factories/unassigned-lines — lines not yet attached to any factory.</summary>
    [HttpGet("unassigned-lines")]
    [RequirePermission(Permissions.LinesManage)]
    public async Task<IActionResult> UnassignedLines(CancellationToken ct) => Ok(await _factories.UnassignedLinesAsync(this.Caller(), ct));

    [HttpPut("{id}/lines/{lineId}")]
    [RequirePermission(Permissions.LinesManage)]
    public async Task<IActionResult> AttachLine([FromRoute] string id, [FromRoute] string lineId, CancellationToken ct)
        => AccessResults.TryId(id, out var fid)
            ? AccessResults.From(this, await _factories.AttachLineAsync(this.Caller(), fid, lineId, AccessResults.TraceId(this), ct))
            : NotFoundFactory();

    [HttpDelete("{id}/lines/{lineId}")]
    [RequirePermission(Permissions.LinesManage)]
    public async Task<IActionResult> DetachLine([FromRoute] string id, [FromRoute] string lineId, CancellationToken ct)
        => AccessResults.TryId(id, out var fid)
            ? AccessResults.From(this, await _factories.DetachLineAsync(this.Caller(), fid, lineId, AccessResults.TraceId(this), ct))
            : NotFoundFactory();

    private IActionResult NotFoundFactory() => AccessResults.Problem(this, 404, "Not found", "factory_not_found", "Factory not found.");
}

/// <summary>
/// Worker profiles — separate from login accounts. Scoped by factory, by
/// assigned line (Production Supervisor) and to oneself (Worker, via /me).
/// </summary>
[ApiController]
[Route("api/workers")]
public class WorkersController : ControllerBase
{
    private readonly IWorkerService _workers;
    public WorkersController(IWorkerService workers) { _workers = workers; }

    [HttpGet]
    [RequirePermission(Permissions.WorkersView)]
    public async Task<IActionResult> List([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] string? search,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _workers.ListAsync(this.Caller(), new WorkerListQuery(factoryId, lineId, search, page ?? 1, pageSize ?? 25), ct));

    /// <summary>GET /api/workers/me — the caller's own worker profile (404 when the account isn't linked to one).</summary>
    [HttpGet("me")]
    [RequirePermission(Permissions.SelfView)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var mine = await _workers.MineAsync(this.Caller(), ct);
        return mine is null
            ? AccessResults.Problem(this, 404, "Not found", "worker_profile_not_linked", "Your account is not linked to a worker profile.")
            : Ok(mine);
    }

    [HttpGet("{id}")]
    [RequirePermission(Permissions.WorkersView, Permissions.SelfView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => AccessResults.TryId(id, out var wid) ? AccessResults.From(this, await _workers.GetAsync(this.Caller(), wid, ct)) : NotFoundWorker();

    [HttpPost]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> Create([FromBody] CreateWorkerRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workers.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), w => $"/api/workers/{w.Id}");

    [HttpPut("{id}")]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateWorkerRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var wid)
            ? AccessResults.From(this, await _workers.UpdateAsync(this.Caller(), wid, body, AccessResults.TraceId(this), ct))
            : NotFoundWorker();

    [HttpPut("{id}/workstation")]
    [RequirePermission(Permissions.WorkersAssign, Permissions.WorkersManage)]
    public async Task<IActionResult> AssignWorkstation([FromRoute] string id, [FromBody] AssignWorkstationRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var wid)
            ? AccessResults.From(this, await _workers.AssignWorkstationAsync(this.Caller(), wid, body, AccessResults.TraceId(this), ct))
            : NotFoundWorker();

    /// <summary>PUT /api/workers/{id}/account — link or unlink a login account.</summary>
    [HttpPut("{id}/account")]
    [RequirePermission(Permissions.UsersUpdate)]
    public async Task<IActionResult> LinkAccount([FromRoute] string id, [FromBody] LinkWorkerAccountRequest body, CancellationToken ct)
        => AccessResults.TryId(id, out var wid)
            ? AccessResults.From(this, await _workers.LinkAccountAsync(this.Caller(), wid, body, AccessResults.TraceId(this), ct))
            : NotFoundWorker();

    private IActionResult NotFoundWorker() => AccessResults.Problem(this, 404, "Not found", "worker_not_found", "Worker not found.");
}

/// <summary>
/// The signed-in user's own account. Needs only a valid session (no
/// permission), so it works while a temporary password is pending.
/// </summary>
[ApiController]
[Route("api/account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly IAccountService _accounts;
    public AccountController(IAccountService accounts) { _accounts = accounts; }

    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        var profile = await _accounts.GetProfileAsync(this.Caller().UserId, ct);
        return profile is null ? AccessResults.Problem(this, 404, "Not found", "user_not_found", "Your account no longer exists.") : Ok(profile);
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest body, CancellationToken ct)
        => AccessResults.From(this, await _accounts.UpdateProfileAsync(this.Caller().UserId, body, AccessResults.TraceId(this), ct));

    /// <summary>POST /api/account/password — change your own password (current password required). Call /api/auth/refresh afterwards.</summary>
    [HttpPost("password")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(BunonBrain.Api.Middleware.RateLimitPolicies.Account)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest body, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await _accounts.ChangePasswordAsync(this.Caller().UserId, body, AccessResults.TraceId(this), ct);
        return result.Outcome == AccessOutcome.Ok ? NoContent() : AccessResults.From(this, result);
    }
}

/// <summary>GET /api/audit — the append-only audit log, scoped to the caller's factories (Super Admin: everything).</summary>
[ApiController]
[Route("api/audit")]
[RequirePermission(Permissions.AuditView)]
public class AuditController : ControllerBase
{
    private readonly IAuditQueryService _audit;
    public AuditController(IAuditQueryService audit) { _audit = audit; }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? entityType, [FromQuery] string? entityId, [FromQuery] string? action,
        [FromQuery] Guid? actorUserId, [FromQuery] Guid? factoryId, [FromQuery] int? limit, [FromQuery] long? before, CancellationToken ct)
        => Ok(await _audit.ListAsync(this.Caller(), new AuditQuery(entityType, entityId, action, actorUserId, factoryId, limit ?? 50, before), ct));
}
