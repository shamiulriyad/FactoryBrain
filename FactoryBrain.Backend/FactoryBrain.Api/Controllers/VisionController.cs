using FactoryBrain.Application.Dtos.Vision;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

[ApiController]
[Route("api/vision")]
public class VisionController : ControllerBase
{
    private readonly IVisionService _svc;

    public VisionController(IVisionService svc)
    { _svc = svc; }

    [HttpGet("analyze")]
    public IActionResult Allowed() => Ok(_svc.Allowed());

    // Step 51: body validation runs in the global FluentValidationFilter
    // (registered in Program.cs); on failure it short-circuits with a 400
    // ValidationProblemDetails (RFC 7807 with an errors map) before this
    // method runs. The VisionAnalyzeRequestValidator carries the
    // "unknown_sample_file" rule, so an invalid sample file returns the
    // same envelope as a missing field.
    [HttpPost("analyze")]
    public IActionResult Analyze([FromBody] VisionAnalyzeRequest body, CancellationToken ct)
        => Ok(_svc.Analyze(body, ct));
}