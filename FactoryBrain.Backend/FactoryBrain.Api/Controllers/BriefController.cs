using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

[ApiController]
[Route("api/brief")]
public class BriefController : ControllerBase
{
    private readonly IBriefService _svc;
    public BriefController(IBriefService svc) { _svc = svc; }

    /// <summary>GET /api/brief/morning.</summary>
    [HttpGet("morning")]
    public async Task<IActionResult> Morning(CancellationToken ct)
        => Ok(await _svc.BuildMorningBriefAsync(ct));
}
