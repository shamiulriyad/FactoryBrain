using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// User management for Admins: list accounts and change roles (Viewer /
/// Operator / Admin). Self-registered accounts start as Viewer and are
/// promoted here. You can't change your own role, and the last Admin can't be
/// demoted. Every change is audited. A changed role reaches the user's
/// session at their next token refresh (at most 15 minutes).
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(Policy = AuthPolicies.Admin)]
public class UsersController : ControllerBase
{
    private readonly IAccountService _accounts;
    public UsersController(IAccountService accounts) { _accounts = accounts; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await _accounts.ListUsersAsync(ct));

    [HttpPatch("{id}/role")]
    public async Task<IActionResult> ChangeRole([FromRoute] string id, [FromBody] ChangeRoleRequest body, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var userId)) return NotFound(new { error = "user_not_found" });
        var result = await _accounts.ChangeRoleAsync(userId, body.Role, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct);
        return result.Outcome switch
        {
            RoleChangeOutcome.Changed => Ok(result.User),
            RoleChangeOutcome.NotFound => NotFound(new { error = "user_not_found" }),
            RoleChangeOutcome.InvalidRole => BadRequest(new { error = "invalid_role" }),
            RoleChangeOutcome.OwnRole => Problem(statusCode: StatusCodes.Status409Conflict, title: "Can't change your own role",
                detail: "Ask another Admin to change your role."),
            RoleChangeOutcome.LastAdmin => Problem(statusCode: StatusCodes.Status409Conflict, title: "Last Admin",
                detail: "This is the only Admin. Make someone else an Admin first."),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
