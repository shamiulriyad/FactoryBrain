using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Central monitoring: the monitoring room, camera monitoring, the event
/// timeline and ingestion, event reviews and the alert lifecycle. Reading
/// needs <c>monitoring.view</c> (everything in scope) or <c>devices.view</c>
/// (camera / device / model status only). Live updates: <c>/hubs/monitoring</c>.
/// Nothing here is an AI result; computer vision is not connected yet.
/// </summary>
[ApiController]
[Route("api/monitoring")]
public class MonitoringController : ControllerBase
{
    private readonly IMonitoringEventService _events;
    private readonly IMonitoringAlertService _alerts;
    private readonly IMonitoringRoomService _room;

    public MonitoringController(IMonitoringEventService events, IMonitoringAlertService alerts, IMonitoringRoomService room)
    {
        _events = events; _alerts = alerts; _room = room;
    }

    /// <summary>GET /api/monitoring/overview?factoryId&amp;departmentId&amp;floorId&amp;lineId&amp;cameraId&amp;eventType&amp;alertStatus&amp;from&amp;to</summary>
    [HttpGet("overview")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView)]
    public async Task<IActionResult> Overview([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? cameraId, [FromQuery] string? eventType, [FromQuery] string? alertStatus, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
        => Ok(await _room.OverviewAsync(this.Caller(), new MonitoringOverviewQuery(factoryId, departmentId, floorId, lineId, cameraId, eventType, alertStatus, from, to), ct));

    /// <summary>
    /// GET /api/monitoring/activity?kind=generic|garment&amp;factoryId&amp;lineId&amp;cameraId&amp;activity&amp;sourceType&amp;uncertainOnly&amp;from&amp;to&amp;activeSeconds —
    /// predicted posture / movement of temporary tracks: current tracks, distributions by camera and line, recent transitions.
    /// Not identities and not productivity.
    /// </summary>
    [HttpGet("activity")]
    [RequirePermission(Permissions.MonitoringView)]
    public async Task<IActionResult> Activity([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] Guid? cameraId, [FromQuery] string? activity,
        [FromQuery] string? sourceType, [FromQuery] bool? uncertainOnly, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? activeSeconds, [FromQuery] string? kind, CancellationToken ct)
        => Ok(await _room.ActivityAsync(this.Caller(), new MonitoringActivityQuery(factoryId, lineId, cameraId, activity, sourceType, uncertainOnly, from, to,
            activeSeconds, kind), ct));

    /// <summary>GET /api/monitoring/contract — the event contract for producers (types, required fields, identity rules, limits).</summary>
    [HttpGet("contract")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView, Permissions.MonitoringEventsIngest)]
    public IActionResult Contract() => Ok(_events.Contract());

    // ─── Cameras ───────────────────────────────────────────────────────────

    /// <summary>GET /api/monitoring/cameras?factoryId&amp;floorId&amp;lineId&amp;connection&amp;search — the camera grid (no stream URLs or credentials).</summary>
    [HttpGet("cameras")]
    [RequirePermission(Permissions.MonitoringView, Permissions.CamerasView)]
    public async Task<IActionResult> Cameras([FromQuery] Guid? factoryId, [FromQuery] Guid? floorId, [FromQuery] string? lineId, [FromQuery] string? connection,
        [FromQuery] string? search, CancellationToken ct)
        => Ok(await _room.CamerasAsync(this.Caller(), new MonitoringCameraQuery(factoryId, floorId, lineId, connection, search), ct));

    [HttpGet("cameras/{id}")]
    [RequirePermission(Permissions.MonitoringView, Permissions.CamerasView)]
    public async Task<IActionResult> Camera([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _room.CameraAsync(this.Caller(), gid, ct))
            : AccessResults.Problem(this, 404, "Not found", "camera_not_found", "Camera not found.");

    // ─── Events ────────────────────────────────────────────────────────────

    /// <summary>GET /api/monitoring/events?factoryId&amp;departmentId&amp;floorId&amp;lineId&amp;cameraId&amp;deviceId&amp;workstationId&amp;eventType&amp;category&amp;sourceType&amp;trackId&amp;verifiedWorker&amp;from&amp;to&amp;search&amp;simulationRunId&amp;machineId&amp;page&amp;pageSize</summary>
    [HttpGet("events")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView)]
    public async Task<IActionResult> Events([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? cameraId, [FromQuery] Guid? deviceId, [FromQuery] Guid? workstationId, [FromQuery] string? eventType, [FromQuery] string? category,
        [FromQuery] string? sourceType, [FromQuery] string? trackId, [FromQuery] bool? verifiedWorker, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? search, [FromQuery] Guid? simulationRunId, [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? activity,
        [FromQuery] Guid? machineId, CancellationToken ct)
        => Ok(await _events.ListAsync(this.Caller(), new MonitoringEventQuery(factoryId, departmentId, floorId, lineId, cameraId, deviceId, workstationId,
            eventType, category, sourceType, trackId, verifiedWorker, from, to, search, simulationRunId, page ?? 1, pageSize ?? 50, activity, machineId), ct));

    /// <summary>
    /// POST /api/monitoring/events — a REAL event from a producer (gateway, later the Python AI service).
    /// 201 when stored; 200 with outcome Duplicate when the idempotency key was seen before.
    /// </summary>
    [HttpPost("events")]
    [RequirePermission(Permissions.MonitoringEventsIngest)]
    public async Task<IActionResult> Ingest([FromBody] IngestMonitoringEventRequest body, CancellationToken ct)
    {
        var caller = this.Caller();
        // The computer-vision service signs in with its service key; everything else is an API producer.
        var producer = caller.ServiceName == "computer-vision" ? MonitoringProducers.CvService : MonitoringProducers.Api;
        var r = await _events.IngestAsync(caller, MonitoringEventInput.From(body), producer, ct);
        if (r.Outcome == AccessOutcome.Ok && r.Value!.Outcome == "Accepted")
            return new CreatedResult($"/api/monitoring/events/{r.Value.Event.Id}", r.Value);
        return AccessResults.From(this, r);
    }

    [HttpGet("events/{id}")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView)]
    public async Task<IActionResult> Event([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _events.GetAsync(this.Caller(), gid, ct)) : NoEvent();

    /// <summary>POST /api/monitoring/events/{id}/reviews — a review decision kept beside the unchanged event.</summary>
    [HttpPost("events/{id}/reviews")]
    [RequirePermission(Permissions.MonitoringEventsReview)]
    public async Task<IActionResult> Review([FromRoute] string id, [FromBody] ReviewMonitoringEventRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _events.ReviewAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoEvent();

    // ─── Alerts ────────────────────────────────────────────────────────────

    /// <summary>GET /api/monitoring/alerts?factoryId&amp;departmentId&amp;floorId&amp;lineId&amp;cameraId&amp;deviceId&amp;status (or ACTIVE)&amp;severity&amp;alertType&amp;sourceType&amp;category&amp;from&amp;to&amp;search&amp;sort&amp;machineId&amp;page&amp;pageSize</summary>
    [HttpGet("alerts")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView)]
    public async Task<IActionResult> Alerts([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? cameraId, [FromQuery] Guid? deviceId, [FromQuery] string? status, [FromQuery] string? severity, [FromQuery] string? alertType,
        [FromQuery] string? sourceType, [FromQuery] string? category, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? search,
        [FromQuery] string? sort, [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] Guid? machineId, CancellationToken ct)
        => Ok(await _alerts.ListAsync(this.Caller(), new MonitoringAlertQuery(factoryId, departmentId, floorId, lineId, cameraId, deviceId, status, severity,
            alertType, sourceType, category, from, to, search, sort, page ?? 1, pageSize ?? 25, machineId), ct));

    [HttpGet("alerts/{id}")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView)]
    public async Task<IActionResult> Alert([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _alerts.GetAsync(this.Caller(), gid, ct)) : NoAlert();

    [HttpPost("alerts/{id}/acknowledge")]
    [RequirePermission(Permissions.MonitoringAlertsReview)]
    public async Task<IActionResult> Acknowledge([FromRoute] string id, [FromBody] AcknowledgeAlertRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _alerts.AcknowledgeAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoAlert();

    [HttpPost("alerts/{id}/escalate")]
    [RequirePermission(Permissions.MonitoringAlertsEscalate)]
    public async Task<IActionResult> Escalate([FromRoute] string id, [FromBody] EscalateAlertRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _alerts.EscalateAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoAlert();

    [HttpPost("alerts/{id}/resolve")]
    [RequirePermission(Permissions.MonitoringAlertsReview)]
    public async Task<IActionResult> Resolve([FromRoute] string id, [FromBody] ResolveAlertRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _alerts.ResolveAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoAlert();

    /// <summary>POST /api/monitoring/alerts/{id}/dismiss — close without action, with a reason (<c>monitoring.alerts.dismiss</c>).</summary>
    [HttpPost("alerts/{id}/dismiss")]
    [RequirePermission(Permissions.MonitoringAlertsDismiss)]
    public async Task<IActionResult> Dismiss([FromRoute] string id, [FromBody] DismissAlertRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _alerts.DismissAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoAlert();

    private IActionResult NoEvent() => AccessResults.Problem(this, 404, "Not found", "event_not_found", "Event not found.");
    private IActionResult NoAlert() => AccessResults.Problem(this, 404, "Not found", "alert_not_found", "Alert not found.");
}

/// <summary>
/// The monitoring simulator (<c>monitoring.simulate</c>). Its events go through
/// the same pipeline as real producers and are always SIMULATED.
/// </summary>
[ApiController]
[Route("api/monitoring/simulator")]
public class MonitoringSimulatorController : ControllerBase
{
    private readonly IMonitoringSimulatorService _sim;
    public MonitoringSimulatorController(IMonitoringSimulatorService sim) { _sim = sim; }

    /// <summary>GET /api/monitoring/simulator?factoryId — the running and recent runs.</summary>
    [HttpGet]
    [RequirePermission(Permissions.MonitoringSimulate)]
    public async Task<IActionResult> Status([FromQuery] Guid? factoryId, CancellationToken ct) => AccessResults.From(this, await _sim.StatusAsync(this.Caller(), factoryId, ct));

    [HttpPost("start")]
    [RequirePermission(Permissions.MonitoringSimulate)]
    public async Task<IActionResult> Start([FromBody] StartSimulationRequest body, CancellationToken ct)
        => AccessResults.From(this, await _sim.StartAsync(this.Caller(), body, AccessResults.TraceId(this), ct), r => $"/api/monitoring/simulator/{r.Id}");

    [HttpPost("{runId}/stop")]
    [RequirePermission(Permissions.MonitoringSimulate)]
    public async Task<IActionResult> Stop([FromRoute] string runId, [FromBody] StopSimulationRequest body, CancellationToken ct)
        => Guid.TryParse(runId, out var id)
            ? AccessResults.From(this, await _sim.StopAsync(this.Caller(), id, body, AccessResults.TraceId(this), ct))
            : AccessResults.Problem(this, 404, "Not found", "simulation_not_found", "Simulation not found.");

    /// <summary>POST /api/monitoring/simulator/events — one demo event (SIMULATED).</summary>
    [HttpPost("events")]
    [RequirePermission(Permissions.MonitoringSimulate)]
    public async Task<IActionResult> Trigger([FromBody] TriggerSimulatedEventRequest body, CancellationToken ct)
        => AccessResults.From(this, await _sim.TriggerAsync(this.Caller(), body, ct));
}
