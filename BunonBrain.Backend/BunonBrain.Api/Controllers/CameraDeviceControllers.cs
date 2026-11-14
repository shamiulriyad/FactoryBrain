using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Cameras;
using BunonBrain.Application.Dtos.Devices;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Cameras: registration, coverage, video sources, reference frames and
/// zones. Reading needs <c>cameras.view</c> (supervisors: cameras covering
/// their lines); changing needs <c>cameras.manage</c> and factory-wide reach.
/// No endpoint returns camera credentials, and the server never connects to
/// a camera URL in this phase. Nothing here produces AI results.
/// </summary>
[ApiController]
[Route("api")]
public class CamerasController : ControllerBase
{
    /// <summary>Whole-request limits for uploads (the store applies its own, lower, configured limits).</summary>
    private const long MaxVideoRequestBytes = 210L * 1024 * 1024;
    private const long MaxImageRequestBytes = 22L * 1024 * 1024;

    private readonly ICameraService _cameras;
    public CamerasController(ICameraService cameras) { _cameras = cameras; }

    /// <summary>GET /api/cameras?search&amp;factoryId&amp;floorId&amp;lineId&amp;workstationId&amp;status&amp;type&amp;source&amp;health&amp;sort&amp;page&amp;pageSize</summary>
    [HttpGet("cameras")]
    [RequirePermission(Permissions.CamerasView)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] Guid? factoryId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] string? status, [FromQuery] string? type, [FromQuery] string? source, [FromQuery] string? health,
        [FromQuery] string? sort, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _cameras.ListAsync(this.Caller(),
            new CameraListQuery(search, factoryId, floorId, lineId, workstationId, status, type, source, health, sort, page ?? 1, pageSize ?? 25), ct));

    /// <summary>GET /api/cameras/overview?factoryId — counts by status, type, source and health.</summary>
    [HttpGet("cameras/overview")]
    [RequirePermission(Permissions.CamerasView)]
    public async Task<IActionResult> Overview([FromQuery] Guid? factoryId, CancellationToken ct) => Ok(await _cameras.OverviewAsync(this.Caller(), factoryId, ct));

    [HttpGet("cameras/{id}")]
    [RequirePermission(Permissions.CamerasView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.GetAsync(this.Caller(), gid, ct)) : NoCamera();

    [HttpPost("cameras")]
    [RequirePermission(Permissions.CamerasManage)]
    public async Task<IActionResult> Create([FromBody] CreateCameraRequest body, CancellationToken ct)
        => AccessResults.From(this, await _cameras.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), c => $"/api/cameras/{c.Id}");

    [HttpPut("cameras/{id}")]
    [RequirePermission(Permissions.CamerasManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateCameraRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.UpdateAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoCamera();

    /// <summary>PATCH /api/cameras/{id}/status — activate, deactivate or put into maintenance.</summary>
    [HttpPatch("cameras/{id}/status")]
    [RequirePermission(Permissions.CamerasManage)]
    public async Task<IActionResult> Status([FromRoute] string id, [FromBody] SetCameraStatusRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.SetStatusAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoCamera();

    /// <summary>DELETE /api/cameras/{id} — only a camera without zones and without device history (409 otherwise).</summary>
    [HttpDelete("cameras/{id}")]
    [RequirePermission(Permissions.CamerasManage)]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NoCamera();
        var r = await _cameras.DeleteAsync(this.Caller(), gid, AccessResults.TraceId(this), ct);
        return r.Outcome == AccessOutcome.Ok ? NoContent() : AccessResults.From(this, r);
    }

    /// <summary>PUT /api/cameras/{id}/coverage — the lines and workstations the camera observes (replaces the set; history kept).</summary>
    [HttpPut("cameras/{id}/coverage")]
    [RequirePermission(Permissions.CamerasManage)]
    public async Task<IActionResult> Coverage([FromRoute] string id, [FromBody] SetCameraCoverageRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.SetCoverageAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoCamera();

    /// <summary>GET /api/cameras/{id}/sources — every source version, newest first (no credentials).</summary>
    [HttpGet("cameras/{id}/sources")]
    [RequirePermission(Permissions.CamerasView)]
    public async Task<IActionResult> Sources([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.SourcesAsync(this.Caller(), gid, ct)) : NoCamera();

    /// <summary>PUT /api/cameras/{id}/source — a new source version (RTSP / HTTP stream, local webcam, none). Credentials are stored encrypted.</summary>
    [HttpPut("cameras/{id}/source")]
    [RequirePermission(Permissions.CamerasManage)]
    public async Task<IActionResult> SetSource([FromRoute] string id, [FromBody] SetCameraSourceRequest body, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.SetSourceAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoCamera();
    }

    /// <summary>PUT /api/cameras/{id}/source/video (multipart: <c>video</c>, optional <c>notes</c>) — MP4 or WebM; becomes the current source.</summary>
    [HttpPut("cameras/{id}/source/video")]
    [RequirePermission(Permissions.CamerasManage)]
    [RequestSizeLimit(MaxVideoRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxVideoRequestBytes)]
    public async Task<IActionResult> SetVideo([FromRoute] string id, [FromForm] CameraVideoForm form, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NoCamera();
        if (form.Video is null || form.Video.Length == 0)
            return AccessResults.Problem(this, 400, "Invalid request", "video_empty", "Choose a video file.");
        await using var stream = form.Video.OpenReadStream();
        return AccessResults.From(this, await _cameras.SetVideoAsync(this.Caller(), gid,
            new UploadedFile(stream, form.Video.Length, form.Video.FileName), form.Notes, AccessResults.TraceId(this), ct));
    }

    /// <summary>GET /api/cameras/{id}/source/video — the recorded video of the current source (supports range requests).</summary>
    [HttpGet("cameras/{id}/source/video")]
    [RequirePermission(Permissions.CamerasView)]
    public async Task<IActionResult> Video([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NoCamera();
        var r = await _cameras.VideoAsync(this.Caller(), gid, ct);
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        SafeMediaHeaders();
        return File(r.Value!.Content, r.Value.ContentType, enableRangeProcessing: true);
    }

    /// <summary>PUT /api/cameras/{id}/reference-frame (multipart: <c>image</c>, <c>source</c> = Upload | VideoFrame, <c>frameSeconds</c>) — JPEG, PNG or WebP.</summary>
    [HttpPut("cameras/{id}/reference-frame")]
    [RequirePermission(Permissions.CamerasManage)]
    [RequestSizeLimit(MaxImageRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImageRequestBytes)]
    public async Task<IActionResult> SetReferenceFrame([FromRoute] string id, [FromForm] CameraFrameForm form, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NoCamera();
        if (form.Image is null || form.Image.Length == 0)
            return AccessResults.Problem(this, 400, "Invalid request", "image_empty", "Choose an image.");
        byte[] bytes;
        using (var ms = new MemoryStream((int)Math.Min(form.Image.Length, MaxImageRequestBytes)))
        {
            await form.Image.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        return AccessResults.From(this, await _cameras.SetReferenceFrameAsync(this.Caller(), gid, bytes, form.Source, form.FrameSeconds, AccessResults.TraceId(this), ct));
    }

    [HttpGet("cameras/{id}/reference-frame")]
    [RequirePermission(Permissions.CamerasView)]
    public async Task<IActionResult> ReferenceFrame([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NoCamera();
        var r = await _cameras.ReferenceFrameAsync(this.Caller(), gid, ct);
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        SafeMediaHeaders();
        return File(r.Value!.Content, r.Value.ContentType);
    }

    /// <summary>GET /api/cameras/{id}/zones?includeInactive</summary>
    [HttpGet("cameras/{id}/zones")]
    [RequirePermission(Permissions.CamerasView)]
    public async Task<IActionResult> Zones([FromRoute] string id, [FromQuery] bool? includeInactive, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.ZonesAsync(this.Caller(), gid, includeInactive ?? false, ct)) : NoCamera();

    /// <summary>POST /api/cameras/{id}/zones — a polygon in normalised coordinates (0–1) over the reference frame.</summary>
    [HttpPost("cameras/{id}/zones")]
    [RequirePermission(Permissions.CamerasManage)]
    public async Task<IActionResult> CreateZone([FromRoute] string id, [FromBody] CreateCameraZoneRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _cameras.CreateZoneAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct), z => $"/api/camera-zones/{z.Id}")
            : NoCamera();

    /// <summary>PUT /api/camera-zones/{id} — edit, deactivate (isActive=false) or reactivate a zone. Every change is a new revision.</summary>
    [HttpPut("camera-zones/{id}")]
    [RequirePermission(Permissions.CamerasManage)]
    public async Task<IActionResult> UpdateZone([FromRoute] string id, [FromBody] UpdateCameraZoneRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.UpdateZoneAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoZone();

    [HttpGet("camera-zones/{id}/history")]
    [RequirePermission(Permissions.CamerasView)]
    public async Task<IActionResult> ZoneHistory([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _cameras.ZoneHistoryAsync(this.Caller(), gid, ct)) : NoZone();

    private void SafeMediaHeaders()
    {
        // Served only with the validated type; never sniffed, never rendered as a document.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.ContentDisposition = "inline";
    }

    private IActionResult NoCamera() => AccessResults.Problem(this, 404, "Not found", "camera_not_found", "Camera not found.");
    private IActionResult NoZone() => AccessResults.Problem(this, 404, "Not found", "zone_not_found", "Zone not found.");
}

/// <summary>multipart/form-data body of PUT /api/cameras/{id}/source/video.</summary>
public sealed class CameraVideoForm
{
    public IFormFile? Video { get; set; }
    public string? Notes { get; set; }
}

/// <summary>multipart/form-data body of PUT /api/cameras/{id}/reference-frame.</summary>
public sealed class CameraFrameForm
{
    public IFormFile? Image { get; set; }
    public string? Source { get; set; }
    public double? FrameSeconds { get; set; }
}

/// <summary>
/// Devices: registration, status, health, history, and the device ingestion
/// endpoints real hardware (or a gateway / MQTT bridge) uses. Reading needs
/// <c>devices.view</c>, history <c>devices.logs.view</c> (or
/// <c>devices.manage</c>), everything else <c>devices.manage</c>.
/// Simulated devices only accept the simulator (<see cref="DeviceSimulatorController"/>).
/// </summary>
[ApiController]
[Route("api/devices")]
public class DevicesController : ControllerBase
{
    private readonly IDeviceService _devices;
    private readonly IDeviceIngestionService _ingest;

    public DevicesController(IDeviceService devices, IDeviceIngestionService ingest) { _devices = devices; _ingest = ingest; }

    /// <summary>GET /api/devices?search&amp;factoryId&amp;category&amp;status&amp;health&amp;simulated&amp;lineId&amp;cameraId&amp;sort&amp;page&amp;pageSize</summary>
    [HttpGet]
    [RequirePermission(Permissions.DevicesView)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] Guid? factoryId, [FromQuery] string? category, [FromQuery] string? status,
        [FromQuery] string? health, [FromQuery] bool? simulated, [FromQuery] string? lineId, [FromQuery] Guid? cameraId, [FromQuery] string? sort,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _devices.ListAsync(this.Caller(),
            new DeviceListQuery(search, factoryId, category, status, health, simulated, lineId, cameraId, sort, page ?? 1, pageSize ?? 25), ct));

    /// <summary>GET /api/devices/health?factoryId — the health overview (counts, simulated vs real, devices needing attention, recent events).</summary>
    [HttpGet("health")]
    [RequirePermission(Permissions.DevicesView)]
    public async Task<IActionResult> Health([FromQuery] Guid? factoryId, CancellationToken ct) => Ok(await _devices.HealthAsync(this.Caller(), factoryId, ct));

    /// <summary>POST /api/devices/health/check — mark devices with stopped heartbeats offline now (also runs every minute).</summary>
    [HttpPost("health/check")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Check(CancellationToken ct) => Ok(await _ingest.CheckHealthAsync(this.Caller(), ct));

    /// <summary>GET /api/devices/events?factoryId&amp;deviceId&amp;type&amp;source&amp;simulated&amp;from&amp;to&amp;page&amp;pageSize — event history of every device in scope.</summary>
    [HttpGet("events")]
    [RequirePermission(Permissions.DevicesLogsView, Permissions.DevicesManage)]
    public async Task<IActionResult> AllEvents([FromQuery] Guid? factoryId, [FromQuery] Guid? deviceId, [FromQuery] string? type, [FromQuery] string? source,
        [FromQuery] bool? simulated, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _devices.EventsAsync(this.Caller(), new DeviceEventQuery(factoryId, deviceId, type, source, simulated, from, to, page ?? 1, pageSize ?? 50), ct));

    /// <summary>POST /api/devices/ingest — a real device message addressed by factory + device code (gateway / MQTT bridge form).</summary>
    [HttpPost("ingest")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Ingest([FromBody] DeviceIngestRequest body, CancellationToken ct)
        => AccessResults.From(this, await _ingest.IngestByCodeAsync(this.Caller(), body.FactoryId, body.DeviceCode,
            new DeviceMessage(body.Kind, body.OccurredAt, body.MessageId, body.FirmwareVersion, body.Metrics, body.Code, body.Message, body.Severity), ct));

    [HttpPost]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Create([FromBody] CreateDeviceRequest body, CancellationToken ct)
        => AccessResults.From(this, await _devices.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), d => $"/api/devices/{d.Id}");

    [HttpGet("{id}")]
    [RequirePermission(Permissions.DevicesView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _devices.GetAsync(this.Caller(), gid, ct)) : NoDevice();

    [HttpPut("{id}")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateDeviceRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _devices.UpdateAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoDevice();

    /// <summary>PATCH /api/devices/{id}/status — Active, Maintenance or Disabled.</summary>
    [HttpPatch("{id}/status")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Status([FromRoute] string id, [FromBody] SetDeviceStatusRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _devices.SetStatusAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoDevice();

    /// <summary>DELETE /api/devices/{id} — only a device that never sent anything (409 otherwise: disable it).</summary>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NoDevice();
        var r = await _devices.DeleteAsync(this.Caller(), gid, AccessResults.TraceId(this), ct);
        return r.Outcome == AccessOutcome.Ok ? NoContent() : AccessResults.From(this, r);
    }

    /// <summary>POST /api/devices/{id}/heartbeat — a heartbeat from a real device.</summary>
    [HttpPost("{id}/heartbeat")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Heartbeat([FromRoute] string id, [FromBody] DeviceHeartbeatRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _ingest.IngestAsync(this.Caller(), gid,
                new DeviceMessage(DeviceMessageKinds.Heartbeat, body.OccurredAt, body.MessageId, body.FirmwareVersion, body.Metrics, null, null, null),
                DeviceMessageSources.Device, ct))
            : NoDevice();

    /// <summary>POST /api/devices/{id}/events — a real device reporting Online, Offline or an Error.</summary>
    [HttpPost("{id}/events")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Report([FromRoute] string id, [FromBody] DeviceReportRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _ingest.IngestAsync(this.Caller(), gid,
                new DeviceMessage(body.Kind, body.OccurredAt, body.MessageId, null, null, body.Code, body.Message, body.Severity), DeviceMessageSources.Device, ct))
            : NoDevice();

    [HttpGet("{id}/events")]
    [RequirePermission(Permissions.DevicesLogsView, Permissions.DevicesManage)]
    public async Task<IActionResult> Events([FromRoute] string id, [FromQuery] string? type, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NoDevice();
        if ((await _devices.GetAsync(this.Caller(), gid, ct)).Outcome != AccessOutcome.Ok) return NoDevice();
        return Ok(await _devices.EventsAsync(this.Caller(), new DeviceEventQuery(null, gid, type, null, null, null, null, page ?? 1, pageSize ?? 50), ct));
    }

    [HttpGet("{id}/heartbeats")]
    [RequirePermission(Permissions.DevicesLogsView, Permissions.DevicesManage)]
    public async Task<IActionResult> Heartbeats([FromRoute] string id, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _devices.HeartbeatsAsync(this.Caller(), gid, page ?? 1, pageSize ?? 50, ct)) : NoDevice();

    private IActionResult NoDevice() => AccessResults.Problem(this, 404, "Not found", "device_not_found", "Device not found.");
}

/// <summary>
/// The software device simulator for demonstrations (<c>devices.simulate</c>).
/// Its messages go through the same ingestion pipeline as real hardware, only
/// for simulated devices, and are stored and shown as SIMULATED — a simulated
/// device is never reported as physically connected.
/// </summary>
[ApiController]
[Route("api/simulator/devices")]
public class DeviceSimulatorController : ControllerBase
{
    private readonly IDeviceIngestionService _ingest;
    private readonly IDeviceService _devices;

    public DeviceSimulatorController(IDeviceIngestionService ingest, IDeviceService devices) { _ingest = ingest; _devices = devices; }

    [HttpGet]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Devices([FromQuery] Guid? factoryId, CancellationToken ct) => AccessResults.From(this, await _ingest.SimulatedDevicesAsync(this.Caller(), factoryId, ct));

    /// <summary>GET /api/simulator/devices/events?factoryId&amp;deviceId&amp;page&amp;pageSize — simulated events only.</summary>
    [HttpGet("events")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Events([FromQuery] Guid? factoryId, [FromQuery] Guid? deviceId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _devices.EventsAsync(this.Caller(), new DeviceEventQuery(factoryId, deviceId, null, null, true, null, null, page ?? 1, pageSize ?? 50), ct));

    [HttpPost("online")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Online([FromBody] SimulatorDeviceRequest body, CancellationToken ct)
        => AccessResults.From(this, await _ingest.SimulateAsync(this.Caller(), body.DeviceId, DeviceMessageKinds.Online, null, body.Reason, null, ct));

    [HttpPost("offline")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Offline([FromBody] SimulatorDeviceRequest body, CancellationToken ct)
        => AccessResults.From(this, await _ingest.SimulateAsync(this.Caller(), body.DeviceId, DeviceMessageKinds.Offline, null, body.Reason, null, ct));

    /// <summary>POST /api/simulator/devices/heartbeat — 1–20 heartbeats, spaced apart and ending now.</summary>
    [HttpPost("heartbeat")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Heartbeat([FromBody] SimulatorHeartbeatRequest body, CancellationToken ct)
        => AccessResults.From(this, await _ingest.SimulateHeartbeatsAsync(this.Caller(), body, ct));

    /// <summary>POST /api/simulator/devices/error — a sample (or the given) error from a simulated device.</summary>
    [HttpPost("error")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Error([FromBody] SimulatorErrorRequest body, CancellationToken ct)
        => AccessResults.From(this, await _ingest.SimulateAsync(this.Caller(), body.DeviceId, DeviceMessageKinds.Error, body.Code, body.Message, body.Severity, ct));
}
