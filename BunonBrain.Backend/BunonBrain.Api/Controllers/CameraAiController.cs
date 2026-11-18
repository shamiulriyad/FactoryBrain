using BunonBrain.Api.Authorization;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Access;
using BunonBrain.Application.CameraAi;
using BunonBrain.Infrastructure.CameraAi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Camera AI sessions: a browser webcam (camera source “Local webcam”) whose
/// sampled frames are analysed by the OpenAI vision API on the server. The
/// OpenAI key never reaches the browser. Results are estimates; events
/// (PERSON_DETECTED, PERSON_LOST, ACTIVITY_CHANGED, PPE_VIOLATION,
/// DETECTION_UNAVAILABLE, CAMERA_STATUS_CHANGED) go through the monitoring
/// pipeline only after temporal confirmation, and arrive live on
/// <c>/hubs/monitoring</c>. See CAMERA_AI.md.
/// </summary>
[ApiController]
[Route("api/monitoring/sessions")]
[EnableRateLimiting(RateLimitPolicies.CameraAi)]
public class CameraAiController : ControllerBase
{
    private readonly CameraAiSessionService _sessions;
    private readonly CameraAiOptions _options;

    public CameraAiController(CameraAiSessionService sessions, CameraAiOptions options) { _sessions = sessions; _options = options; }

    /// <summary>GET /api/monitoring/sessions — whether camera AI is configured, its limits, today's usage and the sessions in scope.</summary>
    [HttpGet]
    [RequirePermission(Permissions.MonitoringView, Permissions.CamerasView)]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await _sessions.OverviewAsync(this.Caller(), ct));
    }

    /// <summary>POST /api/monitoring/sessions {cameraId, intervalMs?} — start analysing the caller's browser webcam on a camera. 201.</summary>
    [HttpPost]
    [RequirePermission(Permissions.CamerasView, Permissions.CamerasManage)]
    public async Task<IActionResult> Start([FromBody] StartCameraAiSessionRequest body, CancellationToken ct)
    {
        var r = await _sessions.StartAsync(this.Caller(), body, ct);
        return r.Ok ? new CreatedResult($"/api/monitoring/sessions/{r.Value!.Id}", r.Value) : Fail(r);
    }

    /// <summary>GET /api/monitoring/sessions/{id}</summary>
    [HttpGet("{id}")]
    [RequirePermission(Permissions.MonitoringView, Permissions.CamerasView)]
    public IActionResult Get([FromRoute] string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(id, out var sid)) return NotFoundSession();
        var r = _sessions.Get(this.Caller(), sid);
        return r.Ok ? Ok(r.Value) : Fail(r);
    }

    /// <summary>
    /// POST /api/monitoring/sessions/{id}/frames — one JPEG frame as the raw
    /// body (Content-Type: image/jpeg). 200 with the analysis (or the failure)
    /// and the events it confirmed. 409 analysis_in_progress / 429 frame_too_soon
    /// mean "drop this frame and send the newest one later".
    /// </summary>
    [HttpPost("{id}/frames")]
    [RequirePermission(Permissions.CamerasView, Permissions.CamerasManage)]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Frame([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var sid)) return NotFoundSession();
        var max = _options.MaxFrameBytes;
        if (Request.ContentLength is { } declared && declared > max)
            return AccessResults.Problem(this, StatusCodes.Status413PayloadTooLarge, "Payload too large", "frame_too_large",
                $"The frame is {declared / 1024} KB; the limit is {max / 1024} KB. Scale it down or lower the JPEG quality.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > max)
                return AccessResults.Problem(this, StatusCodes.Status413PayloadTooLarge, "Payload too large", "frame_too_large",
                    $"The frame is larger than {max / 1024} KB. Scale it down or lower the JPEG quality.");
        }
        var r = await _sessions.AnalyzeFrameAsync(this.Caller(), sid, buffer.ToArray(), Request.ContentType, ct);
        return r.Ok ? Ok(r.Value) : Fail(r);
    }

    /// <summary>POST /api/monitoring/sessions/{id}/stop {reason?} — end the session (idempotent). A confirmed person is reported lost.</summary>
    [HttpPost("{id}/stop")]
    [RequirePermission(Permissions.CamerasView, Permissions.CamerasManage)]
    public async Task<IActionResult> Stop([FromRoute] string id, [FromBody] StopCameraAiSessionRequest? body)
    {
        if (!Guid.TryParse(id, out var sid)) return NotFoundSession();
        var r = await _sessions.StopAsync(this.Caller(), sid, body?.Reason);
        return r.Ok ? Ok(r.Value) : Fail(r);
    }

    private IActionResult NotFoundSession() =>
        AccessResults.Problem(this, StatusCodes.Status404NotFound, "Not found", "session_not_found", "Camera AI session not found.");

    private IActionResult Fail<T>(CameraAiResult<T> r)
    {
        if (r.RetryAfterSeconds is { } s) Response.Headers.RetryAfter = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var title = r.Status switch
        {
            400 => "Invalid request", 403 => "Forbidden", 404 => "Not found", 409 => "Conflict", 413 => "Payload too large",
            429 => "Too many requests", 503 => "Service unavailable", _ => "Error",
        };
        return AccessResults.Problem(this, r.Status, title, r.Code, r.Message);
    }
}
