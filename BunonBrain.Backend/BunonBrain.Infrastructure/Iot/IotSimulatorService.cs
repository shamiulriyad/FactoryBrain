using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Iot;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Infrastructure.Iot;

/// <summary>
/// The IoT simulator. It writes the same JSON a device would publish, on the
/// same topic, and passes the bytes through the same parser and pipeline as
/// the MQTT bridge — only for simulated devices, so everything it produces is
/// stored and shown as SIMULATED. It never fabricates REAL data.
/// </summary>
public sealed class IotSimulatorService : IIotSimulatorService
{
    public static readonly IReadOnlyList<string> Scenarios = new[]
    {
        "RUNNING", "STOPPED", "IDLE", "ERROR", "OFFLINE", "ONLINE", "HEARTBEAT", "PIECES", "COUNTER_RESET", "TEMPERATURE", "VIBRATION", "DUPLICATE", "DELAYED", "INVALID",
    };

    private readonly BunonBrainDbContext _db;
    private readonly IIotIngestionService _ingest;
    private readonly IotSimulationRunner _runner;
    private readonly IotOptions _options;

    public IotSimulatorService(BunonBrainDbContext db, IIotIngestionService ingest, IotSimulationRunner runner, IOptions<IotOptions> options)
    {
        _db = db; _ingest = ingest; _runner = runner; _options = options.Value;
    }

    /// <summary>A simulated machine: its simulated state sensor and counter (either may be missing).</summary>
    public sealed record SimMachine(Guid MachineId, string MachineCode, Guid FactoryId, string FactoryCode, Device? Sensor, Device? Counter);

    public async Task<AccessResult<IotSimulationResult>> SendAsync(AccessContext caller, IotSimulationRequest r, CancellationToken ct)
    {
        var scenario = Scenarios.FirstOrDefault(s => string.Equals(s, r.Scenario?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scenario is null) return AccessResult<IotSimulationResult>.Invalid("scenario_invalid", $"Scenario: one of {string.Join(", ", Scenarios)}.");
        var targets = await TargetsAsync(_db, caller, r.FactoryId, r.MachineId, r.WorkstationId, r.LineId, ct);
        if (targets.Error is not null) return new AccessResult<IotSimulationResult>(targets.Outcome, null, targets.Code, targets.Error);
        var machines = targets.Machines;
        var messages = new List<IotSimulatedMessage>();
        var notes = new List<string> { "Every message is SIMULATED: it goes through the same parser and pipeline as a real device, for simulated devices only." };
        var now = DateTimeOffset.UtcNow;
        var count = Math.Clamp(r.Count ?? 1, 1, 500);
        var prefix = _options.Mqtt.TopicPrefix;

        foreach (var m in machines)
        {
            async Task Send(Device d, string channel, Dictionary<string, object?> body, string? rawOverride = null)
            {
                var topic = channel == IotTopics.Production ? IotTopics.Machine(prefix, m.FactoryCode, m.MachineCode) : IotTopics.Device(prefix, m.FactoryCode, d.Code, channel);
                if (channel == IotTopics.Production) body["deviceId"] = d.Code;
                var json = rawOverride ?? JsonSerializer.Serialize(body);
                messages.Add(await SendRawAsync(_ingest, caller, topic, prefix, json, d.Code, m.MachineCode, channel, ct));
            }
            Dictionary<string, object?> Body(string type, DateTimeOffset? at = null, string? id = null) => new()
            {
                ["schema"] = IotMessageParser.Schema, ["type"] = type, ["messageId"] = id ?? $"sim-{Guid.NewGuid():N}", ["occurredAt"] = (at ?? now).ToString("O"),
            };

            switch (scenario)
            {
                case "RUNNING" or "STOPPED" or "IDLE" or "ERROR":
                    if (m.Sensor is null) { notes.Add($"{m.MachineCode}: no simulated machine sensor."); break; }
                    var t = Body("telemetry");
                    t["state"] = scenario;
                    t["powerWatts"] = scenario switch { "RUNNING" => 320.0, "IDLE" => 45.0, _ => 3.0 };
                    t["temperatureC"] = scenario == "RUNNING" ? 46.5 : 32.0;
                    if (scenario == "ERROR") t["errorCode"] = "THREAD_BREAK";
                    await Send(m.Sensor, IotTopics.Telemetry, t);
                    break;
                case "TEMPERATURE" or "VIBRATION":
                    if (m.Sensor is null) { notes.Add($"{m.MachineCode}: no simulated machine sensor."); break; }
                    var reading = Body("telemetry");
                    if (scenario == "TEMPERATURE") reading["temperatureC"] = r.Value ?? 78.0;
                    else reading["vibrationMmS"] = r.Value ?? 15.0;
                    await Send(m.Sensor, IotTopics.Telemetry, reading);
                    break;
                case "OFFLINE" or "ONLINE":
                    foreach (var d in new[] { m.Sensor, m.Counter }.Where(x => x is not null))
                    {
                        var s = Body("status");
                        s["status"] = scenario;
                        s["message"] = scenario == "OFFLINE" ? "Simulated: device lost power" : "Simulated: device reconnected";
                        await Send(d!, IotTopics.Status, s);
                    }
                    break;
                case "HEARTBEAT":
                    foreach (var d in new[] { m.Sensor, m.Counter }.Where(x => x is not null))
                    {
                        var h = Body("heartbeat");
                        h["firmwareVersion"] = "sim-1.0.0";
                        h["metrics"] = new Dictionary<string, double> { ["rssi"] = -58, ["uptimeSeconds"] = 3600 };
                        await Send(d!, IotTopics.Heartbeat, h);
                    }
                    break;
                case "PIECES" or "COUNTER_RESET":
                    if (m.Counter is null) { notes.Add($"{m.MachineCode}: no simulated piece counter."); break; }
                    var p = Body("production");
                    p["counter"] = scenario == "PIECES"
                        ? new Dictionary<string, object> { ["mode"] = CounterModes.Increment, ["value"] = count }
                        : new Dictionary<string, object> { ["mode"] = CounterModes.Reset, ["value"] = 0 };
                    await Send(m.Counter, IotTopics.Production, p);
                    break;
                case "DUPLICATE":
                {
                    var target = m.Counter ?? m.Sensor;
                    if (target is null) break;
                    var id = $"sim-dup-{Guid.NewGuid():N}";
                    for (var i = 0; i < 2; i++)
                    {
                        if (target == m.Counter)
                        {
                            var dp = Body("production", now, id);
                            dp["counter"] = new Dictionary<string, object> { ["mode"] = CounterModes.Increment, ["value"] = 1 };
                            await Send(target, IotTopics.Production, dp);
                        }
                        else
                        {
                            var dt = Body("telemetry", now, id);
                            dt["state"] = MachineStates.Running;
                            await Send(target, IotTopics.Telemetry, dt);
                        }
                    }
                    notes.Add($"{m.MachineCode}: the same message id was sent twice; the second is a Duplicate and is not counted again.");
                    break;
                }
                case "DELAYED":
                {
                    var delay = Math.Clamp(r.DelaySeconds ?? 300, 1, 86_400);
                    var at = now.AddSeconds(-delay);
                    if (m.Sensor is not null)
                    {
                        var dt = Body("telemetry", at);
                        dt["state"] = MachineStates.Stopped;
                        await Send(m.Sensor, IotTopics.Telemetry, dt);
                    }
                    if (m.Counter is not null)
                    {
                        var dp = Body("production", at);
                        dp["counter"] = new Dictionary<string, object> { ["mode"] = CounterModes.Increment, ["value"] = 1 };
                        await Send(m.Counter, IotTopics.Production, dp);
                    }
                    notes.Add($"{m.MachineCode}: messages dated {delay} s ago. Telemetry older than the newest is stored but never moves the state back; a late increment is counted in the hour it happened.");
                    break;
                }
                default: // INVALID
                {
                    var target = m.Sensor ?? m.Counter;
                    if (target is null) break;
                    await Send(target, IotTopics.Telemetry, new Dictionary<string, object?>(), """{"type":"telemetry","state":"FLYING","powerWatts":"a lot"}""");
                    notes.Add($"{m.MachineCode}: an invalid payload was refused and recorded on the device's event log.");
                    break;
                }
            }
        }
        return AccessResult<IotSimulationResult>.Ok(new IotSimulationResult(scenario, machines.Count, messages, notes));
    }

    public static async Task<IotSimulatedMessage> SendRawAsync(IIotIngestionService ingest, AccessContext caller, string topic, string? prefix, string json,
        string deviceCode, string? machineCode, string channel, CancellationToken ct)
    {
        var parsedTopic = IotTopics.Parse(topic, prefix);
        var r = await ingest.IngestRawAsync(Encoding.UTF8.GetBytes(json), new IotOrigin(IotTransports.Simulator, caller, null, null, parsedTopic), ct);
        return r.Outcome == AccessOutcome.Ok
            ? new IotSimulatedMessage(deviceCode, machineCode, channel, topic, json, r.Value!.Outcome, null, r.Value.Notes.Count == 0 ? null : string.Join("; ", r.Value.Notes))
            : new IotSimulatedMessage(deviceCode, machineCode, channel, topic, json, "Rejected", r.Code, r.Message);
    }

    public sealed record TargetResult(IReadOnlyList<SimMachine> Machines, AccessOutcome Outcome, string? Code, string? Error);

    /// <summary>The simulated machines a request targets: one machine, a workstation, a line, or the whole factory.</summary>
    public static async Task<TargetResult> TargetsAsync(BunonBrainDbContext db, AccessContext caller, Guid factoryId, Guid? machineId, Guid? workstationId,
        string? lineId, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId) || caller.Scope < RoleScope.Factory)
            return new TargetResult(Array.Empty<SimMachine>(), AccessOutcome.Forbidden, "factory_out_of_scope", "You can only simulate in a factory you administer.");
        var factory = await db.Factories.AsNoTracking().Where(f => f.Id == factoryId).Select(f => new { f.Id, f.Code }).FirstOrDefaultAsync(ct);
        if (factory is null) return new TargetResult(Array.Empty<SimMachine>(), AccessOutcome.NotFound, "factory_not_found", "Factory not found.");
        var q = db.Machines.AsNoTracking().Where(m => m.FactoryId == factoryId && m.IsActive);
        if (machineId is { } mid) q = q.Where(m => m.Id == mid);
        if (workstationId is { } wid) q = q.Where(m => m.WorkstationId == wid);
        if (!string.IsNullOrWhiteSpace(lineId)) { var l = lineId.Trim(); q = q.Where(m => db.Workstations.Any(w => w.Id == m.WorkstationId && w.LineId == l)); }
        var machines = await q.Where(m => db.Devices.Any(d => d.MachineId == m.Id && d.IsSimulated)).OrderBy(m => m.Code).Take(200).ToListAsync(ct);
        if (machines.Count == 0)
            return new TargetResult(Array.Empty<SimMachine>(), AccessOutcome.Invalid, "no_simulated_machines",
                "No machine in that target has simulated devices. Use \"Set up simulated devices\" on a machine first (Machine monitoring → Simulator).");
        var ids = machines.Select(m => m.Id).ToList();
        var devices = await db.Devices.AsNoTracking().Where(d => d.MachineId != null && ids.Contains(d.MachineId.Value) && d.IsSimulated && d.Status != "Disabled")
            .OrderBy(d => d.Code).ToListAsync(ct);
        var devIds = devices.Select(d => d.Id).ToList();
        var configs = await db.DeviceIotConfigurations.AsNoTracking().Where(c => devIds.Contains(c.DeviceId)).ToDictionaryAsync(c => c.DeviceId, ct);
        return new TargetResult(machines.Select(m =>
        {
            var mine = devices.Where(d => d.MachineId == m.Id).ToList();
            return new SimMachine(m.Id, m.Code, factory.Id, factory.Code,
                mine.FirstOrDefault(d => IotQueries.DecidesState(d, configs.GetValueOrDefault(d.Id))),
                mine.FirstOrDefault(d => IotQueries.IsCounter(d, configs.GetValueOrDefault(d.Id))));
        }).ToList(), AccessOutcome.Ok, null, null);
    }

    public async Task<AccessResult<IotSimulationRunDto>> StartRunAsync(AccessContext caller, IotSimulationRunRequest r, CancellationToken ct)
    {
        var targets = await TargetsAsync(_db, caller, r.FactoryId, r.MachineId, r.WorkstationId, r.LineId, ct);
        if (targets.Error is not null) return new AccessResult<IotSimulationRunDto>(targets.Outcome, null, targets.Code, targets.Error);
        var target = r.MachineId is not null ? $"machine {targets.Machines[0].MachineCode}"
            : r.WorkstationId is not null ? "workstation" : !string.IsNullOrWhiteSpace(r.LineId) ? $"line {r.LineId}" : "factory";
        return _runner.Start(caller, r, $"{target} ({targets.Machines.Count} machine{(targets.Machines.Count == 1 ? "" : "s")})", targets.Machines.Count);
    }

    public AccessResult<IotSimulationRunDto> StopRun(AccessContext caller, Guid runId) => _runner.Stop(caller, runId);

    public IReadOnlyList<IotSimulationRunDto> Runs(AccessContext caller) => _runner.List(caller);
}

/// <summary>
/// Continuous simulations (singleton, in memory): every interval each targeted
/// machine sends plausible telemetry, sometimes changes state, and counts
/// pieces while running. At most 5 runs at once, 2 hours each; runs end when
/// the API stops. A run acts with its starter's identity.
/// </summary>
public sealed class IotSimulationRunner : IDisposable
{
    public const int MaxRuns = 5;
    public const int MaxMinutes = 120;
    public static readonly IReadOnlyList<string> Profiles = new[] { "MIXED", "BUSY", "FAULTY" };

    private sealed class Run
    {
        public required IotSimulationRunDto Dto;
        public required AccessContext Caller;
        public required IotSimulationRunRequest Request;
        public readonly CancellationTokenSource Cts = new();
        public long Sent, Rejected;
        public string? LastError;
        public DateTimeOffset? EndedAt;
        public string Status = "RUNNING";
        public readonly Dictionary<Guid, string> States = new();
    }

    private readonly ConcurrentDictionary<Guid, Run> _runs = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IotSimulationRunner> _log;
    private readonly IotOptions _options;

    public IotSimulationRunner(IServiceScopeFactory scopes, IOptions<IotOptions> options, ILogger<IotSimulationRunner> log)
    {
        _scopes = scopes; _options = options.Value; _log = log;
    }

    private static IotSimulationRunDto Dto(Run r) => r.Dto with
    {
        Status = r.Status, EndedAt = r.EndedAt, MessagesSent = Interlocked.Read(ref r.Sent), MessagesRejected = Interlocked.Read(ref r.Rejected), LastError = r.LastError,
    };

    public AccessResult<IotSimulationRunDto> Start(AccessContext caller, IotSimulationRunRequest req, string target, int machines)
    {
        if (_runs.Values.Count(x => x.Status == "RUNNING") >= MaxRuns)
            return AccessResult<IotSimulationRunDto>.Conflict("too_many_runs", $"At most {MaxRuns} IoT simulations run at once. Stop one first.");
        var profile = Profiles.FirstOrDefault(p => string.Equals(p, req.Profile?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "MIXED";
        var minutes = Math.Clamp(req.DurationMinutes, 1, MaxMinutes);
        var interval = Math.Clamp(req.IntervalSeconds, 2, 60);
        var now = DateTimeOffset.UtcNow;
        var run = new Run
        {
            Caller = caller, Request = req,
            Dto = new IotSimulationRunDto(Guid.NewGuid(), req.FactoryId, target, profile, "RUNNING", now, now.AddMinutes(minutes), null, interval, machines, 0, 0, null, caller.Email),
        };
        _runs[run.Dto.Id] = run;
        _ = Task.Run(() => LoopAsync(run, profile, interval, run.Cts.Token));
        return AccessResult<IotSimulationRunDto>.Ok(Dto(run));
    }

    public AccessResult<IotSimulationRunDto> Stop(AccessContext caller, Guid id)
    {
        if (!_runs.TryGetValue(id, out var run) || !caller.CanAccessFactory(run.Dto.FactoryId))
            return AccessResult<IotSimulationRunDto>.NotFound("run_not_found", "Simulation not found.");
        if (run.Status == "RUNNING") { run.Status = "STOPPED"; run.EndedAt = DateTimeOffset.UtcNow; run.Cts.Cancel(); }
        return AccessResult<IotSimulationRunDto>.Ok(Dto(run));
    }

    public IReadOnlyList<IotSimulationRunDto> List(AccessContext caller) =>
        _runs.Values.Where(r => caller.CanAccessFactory(r.Dto.FactoryId)).OrderByDescending(r => r.Dto.StartedAt).Take(20).Select(Dto).ToList();

    private async Task LoopAsync(Run run, string profile, int interval, CancellationToken ct)
    {
        var rng = new Random();
        var tick = 0;
        try
        {
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < run.Dto.EndsAt)
            {
                await using (var scope = _scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
                    var ingest = scope.ServiceProvider.GetRequiredService<IIotIngestionService>();
                    var t = await IotSimulatorService.TargetsAsync(db, run.Caller, run.Request.FactoryId, run.Request.MachineId, run.Request.WorkstationId, run.Request.LineId, ct);
                    if (t.Error is not null) { run.LastError = t.Error; break; }
                    foreach (var m in t.Machines) await TickAsync(run, ingest, m, profile, tick, rng, ct);
                }
                tick++;
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            }
            if (run.Status == "RUNNING") { run.Status = "COMPLETED"; run.EndedAt = DateTimeOffset.UtcNow; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            run.Status = "FAILED";
            run.EndedAt = DateTimeOffset.UtcNow;
            run.LastError = ex.Message;
            _log.LogWarning(ex, "IoT simulation {Run} failed.", run.Dto.Id);
        }
    }

    private async Task TickAsync(Run run, IIotIngestionService ingest, IotSimulatorService.SimMachine m, string profile, int tick, Random rng, CancellationToken ct)
    {
        var prefix = _options.Mqtt.TopicPrefix;
        var now = DateTimeOffset.UtcNow;
        var state = run.States.GetValueOrDefault(m.MachineId, MachineStates.Running);
        // A state holds for a while; profiles change how often it moves and where to.
        var change = profile switch { "BUSY" => 0.05, "FAULTY" => 0.3, _ => 0.15 };
        if (rng.NextDouble() < change)
        {
            var roll = rng.NextDouble();
            state = profile switch
            {
                "BUSY" => roll < 0.85 ? MachineStates.Running : MachineStates.Idle,
                "FAULTY" => roll < 0.4 ? MachineStates.Running : roll < 0.6 ? MachineStates.Stopped : roll < 0.85 ? MachineStates.Error : MachineStates.Idle,
                _ => roll < 0.65 ? MachineStates.Running : roll < 0.85 ? MachineStates.Idle : roll < 0.97 ? MachineStates.Stopped : MachineStates.Error,
            };
        }
        run.States[m.MachineId] = state;

        async Task Send(Device d, string channel, Dictionary<string, object?> body)
        {
            var topic = channel == IotTopics.Production ? IotTopics.Machine(prefix, m.FactoryCode, m.MachineCode) : IotTopics.Device(prefix, m.FactoryCode, d.Code, channel);
            if (channel == IotTopics.Production) body["deviceId"] = d.Code;
            var r = await IotSimulatorService.SendRawAsync(ingest, run.Caller, topic, prefix, JsonSerializer.Serialize(body), d.Code, m.MachineCode, channel, ct);
            if (r.Outcome == "Rejected") { Interlocked.Increment(ref run.Rejected); run.LastError = $"{r.Code}: {r.Error}"; }
            else Interlocked.Increment(ref run.Sent);
        }
        Dictionary<string, object?> Body(string type) => new()
        {
            ["schema"] = IotMessageParser.Schema, ["type"] = type, ["messageId"] = $"sim-run-{run.Dto.Id:N}-{m.MachineId:N}-{type}-{tick}", ["occurredAt"] = now.ToString("O"),
        };

        if (m.Sensor is not null)
        {
            var t = Body("telemetry");
            t["state"] = state;
            t["powerWatts"] = Math.Round(state switch
            {
                MachineStates.Running => 280 + rng.NextDouble() * 80,
                MachineStates.Idle => 30 + rng.NextDouble() * 20,
                _ => rng.NextDouble() * 5,
            }, 1);
            var hot = profile == "FAULTY" && rng.NextDouble() < 0.1;
            t["temperatureC"] = Math.Round((state == MachineStates.Running ? 44 : 33) + rng.NextDouble() * 4 + (hot ? 35 : 0), 1);
            t["vibrationMmS"] = Math.Round((state == MachineStates.Running ? 4 : 0.5) + rng.NextDouble() * 2, 2);
            if (state == MachineStates.Error) t["errorCode"] = rng.NextDouble() < 0.5 ? "THREAD_BREAK" : "NEEDLE_JAM";
            await Send(m.Sensor, IotTopics.Telemetry, t);
            if (tick % 6 == 0)
            {
                var h = Body("heartbeat");
                h["metrics"] = new Dictionary<string, double> { ["rssi"] = -55 - rng.Next(10), ["uptimeSeconds"] = 600 + tick * run.Dto.IntervalSeconds };
                await Send(m.Sensor, IotTopics.Heartbeat, h);
            }
        }
        if (m.Counter is not null && state == MachineStates.Running && rng.NextDouble() < 0.7)
        {
            var p = Body("production");
            p["counter"] = new Dictionary<string, object> { ["mode"] = CounterModes.Increment, ["value"] = 1 + (rng.NextDouble() < 0.2 ? 1 : 0) };
            await Send(m.Counter, IotTopics.Production, p);
        }
    }

    public void Dispose()
    {
        foreach (var r in _runs.Values) r.Cts.Cancel();
    }
}
