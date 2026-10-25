using FactoryBrain.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// Retired. <c>/api/vision/analyze</c> mapped four staged file names to
/// fixed labels and confidences and queued approvals and floor alerts from
/// them — simulated detections presented next to real data. Image
/// inspection is now <c>/api/qc/inspections</c>, which analyses uploaded
/// images with a configured vision provider (or says it isn't configured).
/// Both verbs answer 410 Gone; access rules are unchanged so an anonymous
/// or Viewer caller still gets 401 / 403 first. Rows already stored in
/// <c>vision_results</c> are kept.
/// </summary>
[ApiController]
[Route("api/vision")]
public class VisionController : ControllerBase
{
    [HttpGet("analyze")]
    public IActionResult Allowed() => Gone();

    [HttpPost("analyze")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public IActionResult Analyze() => Gone();

    private ObjectResult Gone() => StatusCode(StatusCodes.Status410Gone, new
    {
        error = "vision_demo_retired",
        details = "The simulated vision demo was removed. Submit inspection images to POST /api/qc/inspections.",
        replacement = "/api/qc/inspections",
    });
}
