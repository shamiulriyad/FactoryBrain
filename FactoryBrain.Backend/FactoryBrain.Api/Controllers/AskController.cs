using FactoryBrain.Application.Dtos.Ask;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

[ApiController]
[Route("api/ask")]
public class AskController : ControllerBase
{
    private readonly IAskService _svc;

    public AskController(IAskService svc)
    { _svc = svc; }

    /// <summary>POST /api/ask — body matches <c>src/app/api/ask/route.ts</c>.</summary>
    /// <remarks>
    /// Step 51: body validation runs in the global
    /// <c>FluentValidationFilter</c> (registered in <c>Program.cs</c>); on
    /// failure it short-circuits with a 400 ValidationProblemDetails (RFC
    /// 7807 with an errors map) before this method runs.
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] AskRequest body, CancellationToken ct)
    {
        var answer = await _svc.AskAsync(body, ct);
        return Ok(answer);
    }
}
