using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly IAgentRunService _svc;
    public AgentsController(IAgentRunService svc) { _svc = svc; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _svc.RosterAsync(ct));

    [HttpPost("{agentId}/run")]
    public async Task<IActionResult> Run([FromRoute] string agentId, CancellationToken ct)
    {
        try
        {
            return Ok(await _svc.RunAsync(agentId, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "agent_not_found", agentId });
        }
    }
}
