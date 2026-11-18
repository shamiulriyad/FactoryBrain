using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Insights stored by agent runs, QC inspections and the production rules.
/// <list type="bullet">
///   <item><c>GET /api/insights?agentId=&amp;risk=low|medium|high&amp;stage=&amp;limit=</c></item>
///   <item><c>GET /api/insights/{id}</c></item>
/// </list>
/// </summary>
[ApiController]
[Route("api/insights")]
public class InsightsController : ControllerBase
{
    private readonly IOperationsViewService _svc;
    public InsightsController(IOperationsViewService svc) { _svc = svc; }

    [HttpGet]
    [RequirePermission(Permissions.KnowledgeView)]
    public async Task<IActionResult> List([FromQuery] string? agentId, [FromQuery] string? risk, [FromQuery] string? stage, [FromQuery] int? limit, CancellationToken ct)
        => Ok(await _svc.InsightsAsync(agentId, risk, stage, limit ?? 100, ct));

    [HttpGet("{id}")]
    [RequirePermission(Permissions.KnowledgeView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => await _svc.InsightAsync(id, ct) is { } dto ? Ok(dto) : AccessResults.Problem(this, 404, "Not found", "insight_not_found", "Insight not found.");
}

/// <summary><c>GET /api/brain/graph?factoryId=</c>: the factory's floors, lines, machines, orders, buyers, styles, targets and open risks as a graph.</summary>
[ApiController]
[Route("api/brain")]
public class BrainController : ControllerBase
{
    private readonly IOperationsViewService _svc;
    public BrainController(IOperationsViewService svc) { _svc = svc; }

    [HttpGet("graph")]
    [RequirePermission(Permissions.KnowledgeView)]
    public async Task<IActionResult> Graph([FromQuery] string? factoryId, CancellationToken ct)
    {
        Guid? fid = null;
        if (!string.IsNullOrWhiteSpace(factoryId))
        {
            if (!AccessResults.TryId(factoryId, out var id)) return AccessResults.Problem(this, 404, "Not found", "factory_not_found", "Factory not found.");
            fid = id;
        }
        return AccessResults.From(this, await _svc.BrainGraphAsync(this.Caller(), fid, ct));
    }
}

/// <summary><c>GET /api/integrations/status?factoryId=</c>: every data source of the factory with its devices, record counts and last activity.</summary>
[ApiController]
[Route("api/integrations/status")]
public class IntegrationStatusController : ControllerBase
{
    private readonly IOperationsViewService _svc;
    public IntegrationStatusController(IOperationsViewService svc) { _svc = svc; }

    [HttpGet]
    [RequirePermission(Permissions.DevicesView, Permissions.KnowledgeView)]
    public async Task<IActionResult> Status([FromQuery] string? factoryId, CancellationToken ct)
    {
        Guid? fid = null;
        if (!string.IsNullOrWhiteSpace(factoryId))
        {
            if (!AccessResults.TryId(factoryId, out var id)) return AccessResults.Problem(this, 404, "Not found", "factory_not_found", "Factory not found.");
            fid = id;
        }
        return AccessResults.From(this, await _svc.IntegrationsAsync(this.Caller(), fid, ct));
    }
}

/// <summary><c>GET /api/factories/{factoryId}/floor-status</c>: each production floor's lines, machines, people, alerts and an ok / watch / act status.</summary>
[ApiController]
[Route("api/factories/{factoryId}/floor-status")]
public class FloorStatusController : ControllerBase
{
    private readonly IOperationsViewService _svc;
    public FloorStatusController(IOperationsViewService svc) { _svc = svc; }

    [HttpGet]
    [RequirePermission(Permissions.MonitoringView, Permissions.ProductionView)]
    public async Task<IActionResult> Get([FromRoute] string factoryId, CancellationToken ct)
        => AccessResults.TryId(factoryId, out var fid)
            ? AccessResults.From(this, await _svc.FloorStatusAsync(this.Caller(), fid, ct))
            : AccessResults.Problem(this, 404, "Not found", "factory_not_found", "Factory not found.");
}
