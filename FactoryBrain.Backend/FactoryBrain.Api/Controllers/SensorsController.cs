using FactoryBrain.Application.Dtos.Sensors;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FactoryBrain.Api.Controllers;

[ApiController]
[Route("api/sensors")]
public class SensorsController : ControllerBase
{
    private readonly ISensorService _svc;

    public SensorsController(ISensorService svc)
    { _svc = svc; }

    // Step 51: body validation runs in the global FluentValidationFilter
    // (registered in Program.cs); on failure it short-circuits with a 400
    // ValidationProblemDetails (RFC 7807 with an errors map) before this
    // method runs.
    /// <summary>
    /// Advance the simulation (and persist the generated readings). Operator
    /// only and rate-limited: dashboards read <c>GET /api/sensors/snapshot</c>
    /// instead of calling this on a timer.
    /// </summary>
    [HttpPost("ingest")]
    [Authorize(Policy = AuthPolicies.Operator)]
    [EnableRateLimiting(RateLimitPolicies.SensorSimulation)]
    public async Task<IActionResult> Ingest([FromBody] IngestRequest? body, CancellationToken ct)
    {
        try
        {
            return Ok(await _svc.IngestAsync(body ?? new IngestRequest(null), ct));
        }
        catch (SimulationLimitException ex)
        {
            return SimulationLimitProblem(ex);
        }
    }

    /// <summary>
    /// Accept readings from a floor gateway. After the first successful post,
    /// the overview poll returns this state instead of inventing a tick.
    /// Gateways authenticate as an Operator account.
    /// </summary>
    [HttpPost("live")]
    [Authorize(Policy = AuthPolicies.Operator)]
    [EnableRateLimiting(RateLimitPolicies.SensorLive)]
    public async Task<IActionResult> Live([FromBody] LiveIngestRequest body, CancellationToken ct)
        => Ok(await _svc.AcceptLiveAsync(body, ct));

    /// <summary>Read-only view of the current simulation / live state. Never advances or persists.</summary>
    [HttpGet("snapshot")]
    public IActionResult Snapshot() => Ok(_svc.Snapshot());

    [HttpGet("ingest")]
    public async Task<IActionResult> IngestStatus(CancellationToken ct)
        => Ok(await _svc.StatusAsync(ct));

    [HttpGet("latest")]
    public async Task<IActionResult> Latest(CancellationToken ct)
        => Ok(await _svc.LatestAsync(ct));

    internal static IActionResult SimulationLimitProblem(SimulationLimitException ex)
        => new BadRequestObjectResult(new ValidationProblemDetails(
            new Dictionary<string, string[]> { ["tick"] = new[] { ex.Message } })
            { Status = StatusCodes.Status400BadRequest })
        {
            ContentTypes = { "application/problem+json" },
        };
}
