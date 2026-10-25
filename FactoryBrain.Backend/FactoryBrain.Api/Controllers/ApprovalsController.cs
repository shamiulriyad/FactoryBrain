using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Approvals;
using FactoryBrain.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// Server-side approval queue.
/// <list type="bullet">
///   <item><c>GET /api/approvals?status=pending|approved|rejected|all</c> — any signed-in user.</item>
///   <item><c>POST /api/approvals/{id}/approve</c>, <c>/reject</c> — Operator or Admin; High-risk items need Admin.
///   Rejecting requires a reason. Each decision is written to the audit log.</item>
/// </list>
/// The reviewer's identity comes from the validated JWT, never from the request body.
/// </summary>
[ApiController]
[Route("api/approvals")]
public class ApprovalsController : ControllerBase
{
    private readonly IApprovalService _svc;
    public ApprovalsController(IApprovalService svc) { _svc = svc; }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int? limit, CancellationToken ct)
    {
        if (!ApprovalService.TryParseStatus(status, out _))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["status"] = new[] { "status must be one of: pending, approved, rejected, all" },
            }) { Status = StatusCodes.Status400BadRequest });
        return Ok(await _svc.ListAsync(status ?? "pending", limit ?? 50, ct));
    }

    [HttpPost("{id}/approve")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public Task<IActionResult> Approve([FromRoute] string id, [FromBody] ApprovalDecisionRequest? body, CancellationToken ct)
        => Decide(id, approve: true, body?.Reason, ct);

    [HttpPost("{id}/reject")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public Task<IActionResult> Reject([FromRoute] string id, [FromBody] ApprovalDecisionRequest? body, CancellationToken ct)
        => Decide(id, approve: false, body?.Reason, ct);

    private async Task<IActionResult> Decide(string id, bool approve, string? reason, CancellationToken ct)
    {
        var result = await _svc.DecideAsync(id, approve, reason, Actor(User), TraceId(), ct);
        return result.Outcome switch
        {
            ApprovalDecisionOutcome.Ok => Ok(result.Approval),
            ApprovalDecisionOutcome.NotFound => NotFound(new { error = "approval_not_found", id }),
            ApprovalDecisionOutcome.AlreadyDecided => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Approval already decided",
                detail: $"This approval is already '{result.Approval?.Status}'. Decisions are final."),
            ApprovalDecisionOutcome.ReasonRequired => ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["reason"] = new[] { "A reason is required to reject an approval." },
            }) { Status = StatusCodes.Status400BadRequest }),
            ApprovalDecisionOutcome.RequiresAdmin => Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Admin approval required",
                detail: "High-risk approvals can only be decided by an Admin."),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    internal static ApprovalActor Actor(ClaimsPrincipal user)
    {
        var sub = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return new ApprovalActor(
            Guid.TryParse(sub, out var id) ? id : null,
            user.FindFirst(JwtRegisteredClaimNames.Email)?.Value ?? user.Identity?.Name ?? "unknown",
            user.FindFirst(ClaimTypes.Role)?.Value ?? "unknown");
    }

    private string TraceId() => Activity.Current?.Id ?? HttpContext.TraceIdentifier;
}

/// <summary>GET /api/audit — read-only view of the append-only audit log. Admin only.</summary>
[ApiController]
[Route("api/audit")]
[Authorize(Policy = AuthPolicies.Admin)]
public class AuditController : ControllerBase
{
    private readonly IApprovalService _svc;
    public AuditController(IApprovalService svc) { _svc = svc; }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? entityType, [FromQuery] string? entityId,
                                          [FromQuery] int? limit, CancellationToken ct)
        => Ok(await _svc.AuditAsync(entityType, entityId, limit ?? 50, ct));
}
