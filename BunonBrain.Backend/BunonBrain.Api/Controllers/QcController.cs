using BunonBrain.Api.Authorization;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Qc;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

[ApiController]
[Route("api/qc")]
public class QcController : ControllerBase
{
    private readonly IQcService _svc;

    public QcController(IQcService svc)
    { _svc = svc; }

    [HttpGet("defects")]
    [RequirePermission(Permissions.QcView)]
    public async Task<IActionResult> Defects(CancellationToken ct)
        => Ok(await _svc.BuildAsync(ct));

    // Step 51: body validation runs in the global FluentValidationFilter
    // (registered in Program.cs); on failure it short-circuits with a 400
    // ValidationProblemDetails (RFC 7807 with an errors map) before this
    // method runs.
    [HttpPost("flag")]
    [RequirePermission(Permissions.QcManage)]
    public async Task<IActionResult> Flag([FromBody] QcFlagRequest body, CancellationToken ct)
        => this.LineOutOfScope(body.LineId) ?? Ok(await _svc.FlagAsync(body, ct));
}