using BunonBrain.Api.Authorization;
using BunonBrain.Application.Access;
using BunonBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

[ApiController]
[Route("api/brief")]
public class BriefController : ControllerBase
{
    private readonly IBriefService _svc;
    public BriefController(IBriefService svc) { _svc = svc; }

    /// <summary>GET /api/brief/morning.</summary>
    [HttpGet("morning")]
    [RequirePermission(Permissions.ReportsView)]
    public async Task<IActionResult> Morning(CancellationToken ct)
        => Ok(await _svc.BuildMorningBriefAsync(ct));
}
