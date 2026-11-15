using System.Text.Json;
using BunonBrain.Api.Authorization;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Iot;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// HTTP device ingestion — the same contract as the MQTT topics: one route
/// per channel (telemetry, status, heartbeat, production), the same JSON
/// payload. <c>ingest/…</c> is for gateways and integration accounts
/// (<c>devices.manage</c>, body names factoryId and deviceId); <c>device/…</c>
/// is for a device posting with its own key (X-Device-Id + X-Device-Key).
/// </summary>
[ApiController]
[Route("api/iot")]
public class IotIngestionController : ControllerBase
{
    private static readonly string[] Channels = { IotTopics.Telemetry, IotTopics.Status, IotTopics.Heartbeat, IotTopics.Production };
    private readonly IIotIngestionService _ingest;

    public IotIngestionController(IIotIngestionService ingest) { _ingest = ingest; }

    private IActionResult Result(AccessResult<IotIngestResult> r)
    {
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        return r.Value!.Outcome == "Accepted" ? StatusCode(201, r.Value) : Ok(r.Value);
    }

    private IActionResult? BadChannel(string channel) => Channels.Contains(channel)
        ? null
        : AccessResults.Problem(this, 404, "Not found", "channel_not_found", "Channel: telemetry, status, heartbeat or production.");

    /// <summary>POST /api/iot/ingest/{channel} — a message from a gateway or integration account. 201 Accepted, 200 Duplicate.</summary>
    [HttpPost("ingest/{channel}")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Ingest([FromRoute] string channel, [FromBody] JsonElement body, CancellationToken ct)
    {
        channel = channel.ToLowerInvariant();
        if (BadChannel(channel) is { } bad) return bad;
        var parsed = IotMessageParser.Parse(body, null, channel);
        if (!parsed.Ok) return AccessResults.Problem(this, 400, "Invalid message", parsed.Code!, parsed.Error!);
        if (parsed.Message!.FactoryRef is null) return AccessResults.Problem(this, 400, "Invalid message", "factory_required", "factoryId is required.");
        return Result(await _ingest.IngestAsync(parsed.Message, new IotOrigin(IotTransports.Http, this.Caller(), null, null, null), ct));
    }

    /// <summary>
    /// POST /api/iot/device/{channel} — a device posting with its own key
    /// (headers X-Device-Id, X-Device-Key; issued by an administrator, HTTP
    /// devices only). No user session is involved.
    /// </summary>
    [HttpPost("device/{channel}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.IotDevice)]
    public async Task<IActionResult> Device([FromRoute] string channel, [FromBody] JsonElement body, CancellationToken ct)
    {
        channel = channel.ToLowerInvariant();
        if (BadChannel(channel) is { } bad) return bad;
        if (!Guid.TryParse(Request.Headers[DeviceKeys.DeviceHeader].ToString(), out var deviceId)
            || await _ingest.AuthenticateDeviceAsync(deviceId, Request.Headers[DeviceKeys.Header].ToString(), ct) is not { } authenticated)
            return AccessResults.Problem(this, 401, "Unauthorized", "device_key_invalid", "Send the device's id as X-Device-Id and its key as X-Device-Key.");
        var parsed = IotMessageParser.Parse(body, null, channel, authenticated.ToString());
        if (!parsed.Ok) return AccessResults.Problem(this, 400, "Invalid message", parsed.Code!, parsed.Error!);
        return Result(await _ingest.IngestAsync(parsed.Message!, new IotOrigin(IotTransports.Http, null, authenticated, null, null), ct));
    }
}

/// <summary>
/// Machine monitoring and production: machines and their derived states,
/// state history, telemetry, production counts and reports, machine rules,
/// maintenance, counter resets and corrections, IoT device configuration
/// and the MQTT bridge status. Seeing machines needs monitoring.view,
/// devices.view or production.view; counts need production.view or
/// monitoring.view; configuration devices.manage; corrections production.manage.
/// </summary>
[ApiController]
[Route("api/iot")]
public class IotController : ControllerBase
{
    private readonly IIotQueryService _q;
    private readonly IIotConfigService _config;
    private readonly IProductionCorrectionService _corrections;
    private readonly IMachineMonitorService _monitor;
    private readonly IotOptions _options;

    public IotController(IIotQueryService q, IIotConfigService config, IProductionCorrectionService corrections, IMachineMonitorService monitor,
        IOptions<IotOptions> options)
    {
        _q = q; _config = config; _corrections = corrections; _monitor = monitor; _options = options.Value;
    }

    private static IotFilter Filter(Guid? factoryId, Guid? departmentId, Guid? floorId, string? lineId, Guid? workstationId, Guid? machineId, Guid? deviceId,
        Guid? shiftId, DateTimeOffset? from, DateTimeOffset? to, string? source, string? state, string? search, int? page, int? pageSize) =>
        new(factoryId, departmentId, floorId, lineId, workstationId, machineId, deviceId, shiftId, from, to, source, state, search, page ?? 1, pageSize ?? 50);

    private IActionResult NoMachine() => AccessResults.Problem(this, 404, "Not found", "machine_not_found", "Machine not found.");
    private IActionResult NoDevice() => AccessResults.Problem(this, 404, "Not found", "device_not_found", "Device not found.");

    /// <summary>GET /api/iot/overview?factoryId&amp;departmentId&amp;floorId&amp;lineId&amp;workstationId&amp;machineId&amp;deviceId&amp;source — counts, machines, today's production, downtime, device events, machine alerts.</summary>
    [HttpGet("overview")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView)]
    public async Task<IActionResult> Overview([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId, [FromQuery] Guid? deviceId, [FromQuery] string? source, CancellationToken ct)
        => Ok(await _q.OverviewAsync(this.Caller(), Filter(factoryId, departmentId, floorId, lineId, workstationId, machineId, deviceId, null, null, null, source, null, null, 1, 500), ct));

    /// <summary>GET /api/iot/machines?…&amp;state&amp;search&amp;page&amp;pageSize</summary>
    [HttpGet("machines")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView)]
    public async Task<IActionResult> Machines([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] Guid? deviceId, [FromQuery] string? source, [FromQuery] string? state, [FromQuery] string? search,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _q.MachinesAsync(this.Caller(), Filter(factoryId, departmentId, floorId, lineId, workstationId, null, deviceId, null, null, null, source, state, search, page, pageSize), ct));

    [HttpGet("machines/{id}")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView)]
    public async Task<IActionResult> Machine([FromRoute] string id, [FromQuery] string? source, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _q.MachineAsync(this.Caller(), gid, source, ct)) : NoMachine();

    /// <summary>GET /api/iot/machines/{id}/telemetry?deviceId&amp;from&amp;to&amp;page&amp;pageSize — raw telemetry (kept for the retention period).</summary>
    [HttpGet("machines/{id}/telemetry")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView)]
    public async Task<IActionResult> Telemetry([FromRoute] string id, [FromQuery] Guid? deviceId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _q.TelemetryAsync(this.Caller(), gid, Filter(null, null, null, null, null, gid, deviceId, null, from, to, null, null, null, page, pageSize), ct))
            : NoMachine();

    /// <summary>GET /api/iot/state-history?factoryId&amp;lineId&amp;workstationId&amp;machineId&amp;state&amp;source&amp;from&amp;to&amp;page&amp;pageSize</summary>
    [HttpGet("state-history")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView)]
    public async Task<IActionResult> StateHistory([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId,
        [FromQuery] string? state, [FromQuery] string? source, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] int? page,
        [FromQuery] int? pageSize, CancellationToken ct)
        => AccessResults.From(this, await _q.StateHistoryAsync(this.Caller(),
            Filter(factoryId, null, null, lineId, workstationId, machineId, null, null, from, to, source, state, null, page, pageSize), ct));

    /// <summary>PUT /api/iot/machines/{id}/profile — counting rule and alert thresholds.</summary>
    [HttpPut("machines/{id}/profile")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Profile([FromRoute] string id, [FromBody] MachineProfileRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _config.SetProfileAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoMachine();

    /// <summary>POST /api/iot/machines/{id}/maintenance — start ({inMaintenance:true, reason}) or end maintenance.</summary>
    [HttpPost("machines/{id}/maintenance")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Maintenance([FromRoute] string id, [FromBody] MaintenanceRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _config.SetMaintenanceAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoMachine();

    /// <summary>GET /api/iot/production?factoryId&amp;departmentId&amp;floorId&amp;lineId&amp;workstationId&amp;machineId&amp;deviceId&amp;shiftId&amp;from&amp;to&amp;source — totals, by line / workstation / machine / shift, hourly, daily, running time and downtime.</summary>
    [HttpGet("production")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView)]
    public async Task<IActionResult> Production([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId, [FromQuery] string? lineId,
        [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId, [FromQuery] Guid? deviceId, [FromQuery] Guid? shiftId, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, [FromQuery] string? source, CancellationToken ct)
        => Ok(await _q.ProductionAsync(this.Caller(), Filter(factoryId, departmentId, floorId, lineId, workstationId, machineId, deviceId, shiftId, from, to, source, null, null, 1, 50), ct));

    /// <summary>GET /api/iot/production/counts?… — production counter events (what each message was counted as).</summary>
    [HttpGet("production/counts")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView)]
    public async Task<IActionResult> Counts([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId,
        [FromQuery] Guid? deviceId, [FromQuery] Guid? shiftId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? source,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _q.CounterEventsAsync(this.Caller(), Filter(factoryId, null, null, lineId, workstationId, machineId, deviceId, shiftId, from, to, source, null, null, page, pageSize), ct));

    [HttpGet("production/corrections")]
    [RequirePermission(Permissions.ProductionView, Permissions.MonitoringView)]
    public async Task<IActionResult> Corrections([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] Guid? machineId, [FromQuery] string? source,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _q.CorrectionsAsync(this.Caller(), Filter(factoryId, null, null, lineId, null, machineId, null, null, null, null, source, null, null, page, pageSize), ct));

    /// <summary>POST /api/iot/production/corrections — ±pieces with a reason (audited; counts themselves never change).</summary>
    [HttpPost("production/corrections")]
    [RequirePermission(Permissions.ProductionManage)]
    public async Task<IActionResult> Correct([FromBody] ProductionCorrectionRequest body, CancellationToken ct)
        => AccessResults.From(this, await _corrections.CorrectAsync(this.Caller(), body, AccessResults.TraceId(this), ct), c => $"/api/iot/production/corrections/{c.Id}");

    /// <summary>POST /api/iot/production/counter-reset — record that a counter was reset (audited; counts nothing).</summary>
    [HttpPost("production/counter-reset")]
    [RequirePermission(Permissions.ProductionManage, Permissions.DevicesManage)]
    public async Task<IActionResult> Reset([FromBody] CounterResetRequest body, CancellationToken ct)
        => AccessResults.From(this, await _corrections.ResetCounterAsync(this.Caller(), body, AccessResults.TraceId(this), ct));

    /// <summary>GET /api/iot/devices?factoryId&amp;lineId&amp;workstationId&amp;machineId&amp;source&amp;search — devices with their IoT configuration and health.</summary>
    [HttpGet("devices")]
    [RequirePermission(Permissions.DevicesView, Permissions.MonitoringView)]
    public async Task<IActionResult> Devices([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] Guid? workstationId, [FromQuery] Guid? machineId,
        [FromQuery] string? source, [FromQuery] string? search, CancellationToken ct)
        => Ok(await _q.DevicesAsync(this.Caller(), Filter(factoryId, null, null, lineId, workstationId, machineId, null, null, null, null, source, null, search, 1, 500), ct));

    [HttpGet("devices/{id}")]
    [RequirePermission(Permissions.DevicesView, Permissions.MonitoringView)]
    public async Task<IActionResult> Device([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _q.DeviceAsync(this.Caller(), gid, ct)) : NoDevice();

    /// <summary>PUT /api/iot/devices/{id}/config — device type, protocol (MQTT / HTTP / SIMULATOR), gateway, MQTT client id, settings.</summary>
    [HttpPut("devices/{id}/config")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Config([FromRoute] string id, [FromBody] DeviceIotConfigRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _config.SetDeviceConfigAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoDevice();

    /// <summary>POST /api/iot/devices/{id}/key — issue (or rotate) the device key of an HTTP device. The key is in this response only.</summary>
    [HttpPost("devices/{id}/key")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> IssueKey([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _config.IssueKeyAsync(this.Caller(), gid, AccessResults.TraceId(this), ct)) : NoDevice();

    [HttpDelete("devices/{id}/key")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> RevokeKey([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _config.RevokeKeyAsync(this.Caller(), gid, AccessResults.TraceId(this), ct)) : NoDevice();

    /// <summary>GET /api/iot/bridge — MQTT bridge status (never the credentials).</summary>
    [HttpGet("bridge")]
    [RequirePermission(Permissions.DevicesView, Permissions.MonitoringView)]
    public IActionResult BridgeStatus() => Ok(_q.Bridge());

    /// <summary>GET /api/iot/contract — topics, payload schema and limits, for device developers.</summary>
    [HttpGet("contract")]
    [RequirePermission(Permissions.DevicesView, Permissions.MonitoringView)]
    public IActionResult Contract() => Ok(new
    {
        schema = IotMessageParser.Schema,
        topics = IotTopics.Subscriptions(_options.Mqtt.TopicPrefix),
        httpGateway = "POST /api/iot/ingest/{telemetry|status|heartbeat|production} (devices.manage; body names factoryId and deviceId)",
        httpDevice = "POST /api/iot/device/{telemetry|status|heartbeat|production} with headers X-Device-Id and X-Device-Key",
        messageTypes = IotMessageTypes.All,
        reportableStates = MachineStates.Reportable,
        derivedStates = MachineStates.All,
        deviceStatuses = IotDeviceStatuses.All,
        counterModes = CounterModes.All,
        countingModes = CountingModes.All,
        deviceTypes = IotDeviceTypes.All.Select(t => new { id = t, label = IotDeviceTypes.Label(t), category = IotDeviceTypes.Category(t) }),
        protocols = IotProtocols.All,
        maxBytes = IotMessageParser.MaxBytes,
        maxIncrement = IotMessageParser.MaxIncrement,
        rules = new[]
        {
            "messageId is required and unique per device: a repeat is recorded once (Duplicate) and never counted twice.",
            "Timestamps are UTC (ISO 8601), at most 2 minutes ahead and 7 days back.",
            "The topic's factory and device are lookup keys only: the device must be registered in that factory, configured for MQTT, and not disabled.",
            "Telemetry older than the newest applied message is stored but never moves the machine state back.",
            "A missing heartbeat never means STOPPED: the machine becomes OFFLINE (sensor unreachable) or UNKNOWN (no telemetry).",
            "Simulated devices only accept the IoT simulator; everything they produce is SIMULATED.",
        },
    });

    /// <summary>POST /api/iot/monitor/run — re-derive machine states and run the timed alert rules now (also runs every 30 s).</summary>
    [HttpPost("monitor/run")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> RunMonitor([FromQuery] Guid? factoryId, CancellationToken ct) => Ok(await _monitor.RunAsync(this.Caller(), factoryId, ct));
}

/// <summary>
/// The IoT simulator (<c>devices.simulate</c>): one-shot scenarios and
/// continuous runs for simulated devices, through the same parser and
/// pipeline as real devices; everything is labelled SIMULATED.
/// </summary>
[ApiController]
[Route("api/iot/simulator")]
public class IotSimulatorController : ControllerBase
{
    private readonly IIotSimulatorService _sim;
    private readonly IIotConfigService _config;

    public IotSimulatorController(IIotSimulatorService sim, IIotConfigService config) { _sim = sim; _config = config; }

    [HttpGet]
    [RequirePermission(Permissions.DevicesSimulate)]
    public IActionResult Get() => Ok(new { scenarios = BunonBrain.Infrastructure.Iot.IotSimulatorService.Scenarios, profiles = BunonBrain.Infrastructure.Iot.IotSimulationRunner.Profiles,
        runs = _sim.Runs(this.Caller()) });

    /// <summary>POST /api/iot/simulator/send — one scenario for a machine, workstation, line or factory.</summary>
    [HttpPost("send")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Send([FromBody] IotSimulationRequest body, CancellationToken ct) => AccessResults.From(this, await _sim.SendAsync(this.Caller(), body, ct));

    /// <summary>POST /api/iot/simulator/setup — register a simulated sensor (and piece counter) on a machine.</summary>
    [HttpPost("setup")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Setup([FromBody] SimulatedMachineSetupRequest body, CancellationToken ct)
        => AccessResults.From(this, await _config.SetUpSimulatedMachineAsync(this.Caller(), body, AccessResults.TraceId(this), ct));

    [HttpGet("runs")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public IActionResult Runs() => Ok(_sim.Runs(this.Caller()));

    [HttpPost("runs")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Start([FromBody] IotSimulationRunRequest body, CancellationToken ct) => AccessResults.From(this, await _sim.StartRunAsync(this.Caller(), body, ct));

    [HttpPost("runs/{id}/stop")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public IActionResult Stop([FromRoute] string id) =>
        Guid.TryParse(id, out var gid) ? AccessResults.From(this, _sim.StopRun(this.Caller(), gid))
            : AccessResults.Problem(this, 404, "Not found", "run_not_found", "Simulation not found.");
}
