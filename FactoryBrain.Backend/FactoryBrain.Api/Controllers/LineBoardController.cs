using FactoryBrain.Application.Dtos.LineBoard;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

[ApiController]
[Route("api/line-board")]
public class LineBoardController : ControllerBase
{
    private readonly ILineBoardService _svc;

    public LineBoardController(ILineBoardService svc)
    { _svc = svc; }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(await _svc.BuildAsync(ct));

    // Step 51: body validation runs in the global FluentValidationFilter
    // (registered in Program.cs); on failure it short-circuits with a 400
    // ValidationProblemDetails (RFC 7807 with an errors map) before this
    // method runs. RefreshRequestValidator only constrains Tick when
    // supplied, so an empty/missing body still passes through to the
    // service which interprets null Tick as "use current server tick".
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? body, CancellationToken ct)
        => Ok(await _svc.RefreshAsync(body?.Tick, ct));
}