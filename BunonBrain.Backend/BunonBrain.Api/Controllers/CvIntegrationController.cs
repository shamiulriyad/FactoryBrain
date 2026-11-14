using BunonBrain.Api.Authorization;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Camera configuration for the computer-vision service. Only the service
/// (X-Service-Key) may call these; people get 403 whatever their
/// permissions. Responses are never cached: stream URLs carry credentials.
/// The service sends what it detects to <c>POST /api/monitoring/events</c>.
/// </summary>
[ApiController]
[Route("api/integrations/cv")]
[RequireService(ServiceKeyDefaults.ComputerVision)]
public class CvIntegrationController : ControllerBase
{
    private readonly ICvIntegrationService _cv;
    public CvIntegrationController(ICvIntegrationService cv) { _cv = cv; }

    /// <summary>GET /api/integrations/cv/cameras — every camera in the service's factories, with its source and active zones.</summary>
    [HttpGet("cameras")]
    public async Task<IActionResult> Cameras(CancellationToken ct)
    {
        NoStore();
        return Ok(await _cv.CamerasAsync(this.Caller(), ct));
    }

    [HttpGet("cameras/{id}")]
    public async Task<IActionResult> Camera([FromRoute] string id, CancellationToken ct)
    {
        NoStore();
        return Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _cv.CameraAsync(this.Caller(), gid, ct))
            : AccessResults.Problem(this, 404, "Not found", "camera_not_found", "Camera not found.");
    }

    /// <summary>GET /api/integrations/cv/cameras/{id}/video — the camera's recorded video (current source).</summary>
    [HttpGet("cameras/{id}/video")]
    public async Task<IActionResult> Video([FromRoute] string id, CancellationToken ct)
    {
        NoStore();
        if (!Guid.TryParse(id, out var gid)) return AccessResults.Problem(this, 404, "Not found", "camera_not_found", "Camera not found.");
        var r = await _cv.VideoAsync(this.Caller(), gid, ct);
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        return File(r.Value!.Content, r.Value.ContentType, enableRangeProcessing: true);
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}
