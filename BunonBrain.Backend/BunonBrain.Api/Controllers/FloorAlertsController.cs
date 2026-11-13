using BunonBrain.Api.Authorization;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.FloorAlerts;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

[ApiController]
[Route("api/floor-alerts")]
public class FloorAlertsController : ControllerBase
{
    private readonly IFloorAlertService _svc;

    public FloorAlertsController(IFloorAlertService svc)
    { _svc = svc; }

    [HttpGet]
    [RequirePermission(Permissions.MonitoringView, Permissions.ProductionView)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _svc.ListAsync(ct));

    // Step 51: body validation runs in the global FluentValidationFilter
    // (registered in Program.cs); on failure it short-circuits with a 400
    // ValidationProblemDetails (RFC 7807 with an errors map) before this
    // method runs. We only handle the routing/path concerns here.
    [HttpPatch("{id}")]
    [RequirePermission(Permissions.MonitoringAlertsReview, Permissions.ProductionAlertsReview)]
    public async Task<IActionResult> Patch([FromRoute] string id, [FromBody] FloorAlertPatchRequest body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return BadRequest(new { error = "missing_id" });
        var updated = await _svc.MarkReadAsync(id, body.Read, ct);
        if (updated is null) return NotFound(new { error = "alert_not_found", id });
        return Ok(updated);
    }
}
