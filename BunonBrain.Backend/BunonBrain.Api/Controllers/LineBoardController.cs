using BunonBrain.Api.Authorization;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.LineBoard;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BunonBrain.Api.Controllers;

[ApiController]
[Route("api/line-board")]
public class LineBoardController : ControllerBase
{
    private readonly ILineBoardService _svc;

    public LineBoardController(ILineBoardService svc)
    { _svc = svc; }

    [HttpGet]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView)]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(await _svc.BuildAsync(ct));

    // Step 51: body validation runs in the global FluentValidationFilter
    // (registered in Program.cs); on failure it short-circuits with a 400
    // ValidationProblemDetails (RFC 7807 with an errors map) before this
    // method runs. RefreshRequestValidator only constrains Tick when
    // supplied, so an empty/missing body still passes through to the
    // service which interprets null Tick as "use current server tick".
    // Advancing the simulation is an Operator action and shares the
    // sensor-sim rate limit with POST /api/sensors/ingest.
    [HttpPost("refresh")]
    [RequirePermission(Permissions.ProductionManage)]
    [EnableRateLimiting(RateLimitPolicies.SensorSimulation)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? body, CancellationToken ct)
    {
        try
        {
            return Ok(await _svc.RefreshAsync(body?.Tick, ct));
        }
        catch (SimulationLimitException ex)
        {
            return SensorsController.SimulationLimitProblem(ex);
        }
    }
}