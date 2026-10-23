using FactoryBrain.Application.Dtos.Sensors;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

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
    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromBody] IngestRequest? body, CancellationToken ct)
        => Ok(await _svc.IngestAsync(body ?? new IngestRequest(null), ct));

    /// <summary>
    /// Accept readings from a floor gateway. After the first successful post,
    /// the overview poll returns this state instead of inventing a tick.
    /// </summary>
    [HttpPost("live")]
    public async Task<IActionResult> Live([FromBody] LiveIngestRequest body, CancellationToken ct)
        => Ok(await _svc.AcceptLiveAsync(body, ct));

    [HttpGet("ingest")]
    public async Task<IActionResult> IngestStatus(CancellationToken ct)
        => Ok(await _svc.StatusAsync(ct));

    [HttpGet("latest")]
    public async Task<IActionResult> Latest(CancellationToken ct)
        => Ok(await _svc.LatestAsync(ct));
}