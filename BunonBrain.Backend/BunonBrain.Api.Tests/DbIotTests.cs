using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Machine monitoring and IoT against real PostgreSQL (a disposable local
/// container, never a shared database): HTTP ingestion → machine state →
/// monitoring events and machine alerts with the existing lifecycle, offline
/// handling, duplicates and late messages, production counting, shifts,
/// corrections, device keys, factory isolation and RBAC, the simulator,
/// append-only history and real-time filtering. The MQTT transport itself is
/// covered against an in-process broker in MqttBridgeTests.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbIotTests
{
    private readonly PostgresStack _stack;
    public DbIotTests(PostgresStack stack) { _stack = stack; }
    private DbApiFactory Api => _stack.Local;

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    private static string? S(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out var next)) return null;
            e = next;
        }
        return e.ValueKind switch { JsonValueKind.Null or JsonValueKind.Undefined => null, JsonValueKind.String => e.GetString(), _ => e.ToString() };
    }

    private static long N(JsonElement e, params string[] path) => long.Parse(S(e, path) ?? "0");

    private async Task<Reply> Send(HttpMethod method, string path, string? bearer, object? body = null, IDictionary<string, string>? headers = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : Rag.Json(body) };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) req.Headers.Add(k, v);
        var res = await client.SendAsync(req);
        var raw = Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[')) ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string? bearer, object body, IDictionary<string, string>? headers = null) => Send(HttpMethod.Post, path, bearer, body, headers);
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, body);

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static string M() => $"m-{Guid.NewGuid():N}";

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, Guid factory)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@iot.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = factory });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, email, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    /// <summary>One factory with a line, two workstations, a machine with a real sensor and a real counter, an empty machine and a 24-hour shift.</summary>
    public sealed record Rig(Guid Factory, string FactoryCode, string Line, Guid Ws, Guid Ws2, Guid Machine, string MachineCode, Guid Machine2, Guid Sensor,
        string SensorCode, Guid Counter, string CounterCode, Guid Shift, Person Admin, Person Devices, Person Monitor, Person Manager, Person Hr);

    private async Task<Rig> NewRig()
    {
        var t = Tag().ToUpperInvariant();
        var factory = Guid.NewGuid();
        var dept = Guid.NewGuid();
        var line = $"iot-{t.ToLowerInvariant()}";
        var (ws, ws2, machine, machine2, sensor, counter, shift) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var code = $"F{t[..6]}";
        await Rag.WithDb(Api, async db =>
        {
            db.Factories.Add(new Factory { Id = factory, Code = code, Name = $"IoT {t}" });
            await db.SaveChangesAsync();
            db.Departments.Add(new Department { Id = dept, FactoryId = factory, Code = $"D{t[..4]}", Name = $"Sewing {t}" });
            db.ProductionLines.Add(new ProductionLine { Id = line, Name = $"Line {t}", Workstations = 10, FactoryId = factory });
            // A shift covering the whole day, every day, so the timed rules always see a working shift.
            db.Shifts.Add(new Shift { Id = shift, FactoryId = factory, Code = "ALLDAY", Name = "All day", StartLocal = new TimeOnly(0, 0), EndLocal = new TimeOnly(23, 59), WorkingDays = 0b1111111 });
            await db.SaveChangesAsync();
            db.Workstations.Add(new Workstation { Id = ws, FactoryId = factory, DepartmentId = dept, LineId = line, Code = $"T-{t[..3]}1", Capacity = 1, Position = 1 });
            db.Workstations.Add(new Workstation { Id = ws2, FactoryId = factory, DepartmentId = dept, LineId = line, Code = $"T-{t[..3]}2", Capacity = 1, Position = 2 });
            await db.SaveChangesAsync();
            db.Machines.Add(new Machine { Id = machine, FactoryId = factory, Code = $"M-{t[..4]}", MachineType = "Single needle lockstitch", WorkstationId = ws });
            db.Machines.Add(new Machine { Id = machine2, FactoryId = factory, Code = $"M-{t[..4]}B", MachineType = "Overlock", WorkstationId = ws2 });
            await db.SaveChangesAsync();
            db.Devices.Add(new Device { Id = sensor, FactoryId = factory, Code = $"S-{t[..4]}", Name = "Sensor", Category = "MachineSensor", MachineId = machine, WorkstationId = ws, LineId = line, CreatedByEmail = "test" });
            db.Devices.Add(new Device { Id = counter, FactoryId = factory, Code = $"PC-{t[..4]}", Name = "Counter", Category = "ProductionCounter", MachineId = machine, WorkstationId = ws, LineId = line, CreatedByEmail = "test" });
            await db.SaveChangesAsync();
            return 0;
        });
        var rig = new Rig(factory, code, line, ws, ws2, machine, $"M-{t[..4]}", machine2, sensor, $"S-{t[..4]}", counter, $"PC-{t[..4]}", shift,
            await NewPerson(R.FactoryAdmin, factory), await NewPerson(R.DeviceAdmin, factory), await NewPerson(R.MonitoringOperator, factory),
            await NewPerson(R.FactoryManager, factory), await NewPerson(R.HrOfficer, factory));
        Is(HttpStatusCode.OK, await Put($"/api/iot/devices/{sensor}/config", rig.Devices.Token, new { deviceType = "SEWING_MACHINE_SENSOR", protocol = "HTTP" }));
        Is(HttpStatusCode.OK, await Put($"/api/iot/devices/{counter}/config", rig.Devices.Token, new { deviceType = "PIECE_COUNTER", protocol = "HTTP" }));
        Is(HttpStatusCode.OK, await Put($"/api/iot/machines/{machine}/profile", rig.Devices.Token, Profile()));
        return rig;
    }

    private static object Profile(string counting = "PIECE", int ppp = 1, int? stopAfter = 600) => new
    {
        machineKind = "SEWING", countingMode = counting, pulsesPerPiece = ppp, runningPowerWatts = 150.0, idlePowerWatts = 20.0, staleAfterSeconds = 900,
        stopAlertAfterSeconds = stopAfter, longIdleAlertSeconds = 1200, maxTemperatureC = 70.0, maxVibrationMmS = 12.0, counterSilenceAlertSeconds = 900,
        flapTransitionsPer5Min = 12, alertsEnabled = true,
    };

    private Task<Reply> Telemetry(Rig r, object fields, string? messageId = null, DateTimeOffset? at = null, string? device = null, string? bearer = null)
    {
        var body = JsonSerializer.SerializeToElement(fields).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        body["factoryId"] = r.Factory;
        body["deviceId"] = device ?? r.SensorCode;
        body["messageId"] = messageId ?? M();
        body["occurredAt"] = (at ?? DateTimeOffset.UtcNow).ToString("O");
        return Post("/api/iot/ingest/telemetry", bearer ?? r.Devices.Token, body);
    }

    private Task<Reply> Count(Rig r, object counter, string? messageId = null, DateTimeOffset? at = null) => Post("/api/iot/ingest/production", r.Devices.Token, new
    {
        factoryId = r.Factory, deviceId = r.CounterCode, messageId = messageId ?? M(), occurredAt = (at ?? DateTimeOffset.UtcNow).ToString("O"), counter,
    });

    private Task<Reply> Status(Rig r, string status, string? device = null) => Post("/api/iot/ingest/status", r.Devices.Token, new
    {
        factoryId = r.Factory, deviceId = device ?? r.SensorCode, messageId = M(), status, occurredAt = DateTimeOffset.UtcNow.ToString("O"),
    });

    private async Task<JsonElement> Machine(Rig r, Guid? id = null, string? source = null) =>
        (await Get($"/api/iot/machines/{id ?? r.Machine}{(source is null ? "" : $"?source={source}")}", r.Monitor.Token)).Body;

    private async Task<JsonElement[]> Alerts(Rig r, Guid? machine = null) =>
        (await Get($"/api/monitoring/alerts?machineId={machine ?? r.Machine}&pageSize=100", r.Monitor.Token)).Body.GetProperty("items").EnumerateArray().ToArray();

    // ─── State and alerts ──────────────────────────────────────────────────

    [DockerFact]
    public async Task Telemetry_derives_the_machine_state_and_raises_machine_alerts_handled_by_the_existing_lifecycle()
    {
        var r = await NewRig();
        var running = await Telemetry(r, new { state = "RUNNING", powerWatts = 312.5, temperatureC = 44.0 });
        Is(HttpStatusCode.Created, running);
        Assert.Equal(("Accepted", "RUNNING", "False"), (S(running.Body, "outcome"), S(running.Body, "machineState"), S(running.Body, "isSimulated")));
        var m = await Machine(r);
        Assert.Equal(("RUNNING", "REAL", "312.5"), (S(m, "machine", "state"), S(m, "machine", "sourceType"), S(m, "machine", "powerWatts")));
        Assert.Contains(m.GetProperty("recentTransitions").EnumerateArray(), x => S(x, "toState") == "RUNNING" && S(x, "trigger") == "TELEMETRY");
        Assert.Contains("Neither proves who operated the machine", S(m, "context", "note"));

        // Power alone decides the state when the sensor sends no state.
        Is(HttpStatusCode.Created, await Telemetry(r, new { powerWatts = 35.0 }));
        Assert.Equal("IDLE", S(await Machine(r), "machine", "state"));

        // A temperature above the limit raises an alert that names the machine.
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING", temperatureC = 82.0 }));
        var hot = (await Alerts(r)).Single(a => S(a, "alertType") == "MACHINE_ABNORMAL_TEMPERATURE");
        Assert.Equal((r.MachineCode, "OPEN", "REAL", "SYSTEM"), (S(hot, "machine", "code"), S(hot, "status"), S(hot, "sourceType"), S(hot, "category")));
        Assert.Contains(r.MachineCode, S(hot, "title"));
        Assert.Contains("ABNORMAL_TEMPERATURE", S(await Machine(r), "machine", "activeConditions") ?? "");

        // An error, then the machine runs again: the error alert gets a recovery note and stays open for a person.
        Is(HttpStatusCode.Created, await Telemetry(r, new { errorCode = "THREAD_BREAK" }));
        var error = (await Alerts(r)).Single(a => S(a, "alertType") == "MACHINE_ERROR");
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING", temperatureC = 45.0 }));
        var detail = (await Get($"/api/monitoring/alerts/{S(error, "id")}", r.Monitor.Token)).Body;
        Assert.Contains(detail.GetProperty("history").EnumerateArray(), h => S(h, "action") == "SOURCE_RECOVERED");
        Assert.Equal("OPEN", S(detail, "alert", "status"));
        Assert.DoesNotContain("ABNORMAL_TEMPERATURE", S(await Machine(r), "machine", "activeConditions") ?? "");

        // The existing workflow: acknowledge, then resolve.
        Is(HttpStatusCode.OK, await Post($"/api/monitoring/alerts/{S(error, "id")}/acknowledge", r.Monitor.Token, new { note = "Looking at it" }));
        Is(HttpStatusCode.OK, await Post($"/api/monitoring/alerts/{S(error, "id")}/resolve", r.Monitor.Token, new { resolutionCode = "ISSUE_FIXED", note = "Thread replaced" }));
        // Every state change is a SYSTEM monitoring event about the machine.
        var events = (await Get($"/api/monitoring/events?machineId={r.Machine}&pageSize=100", r.Monitor.Token)).Body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(events, e => S(e, "eventType") == "MACHINE_STATE_CHANGED" && S(e, "machine", "code") == r.MachineCode && S(e, "category") == "SYSTEM");
        Assert.Contains(events, e => S(e, "eventType") == "MACHINE_CONDITION");
        Assert.All(events, e => Assert.Null(S(e, "verifiedWorker")));
    }

    [DockerFact]
    public async Task Duplicates_late_and_invalid_messages_are_handled_without_corrupting_the_state()
    {
        var r = await NewRig();
        var id = M();
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }, id));
        var dup = await Telemetry(r, new { state = "STOPPED" }, id);
        Is(HttpStatusCode.OK, dup);
        Assert.Equal("Duplicate", S(dup.Body, "outcome"));
        var late = await Telemetry(r, new { state = "STOPPED" }, at: DateTimeOffset.UtcNow.AddMinutes(-5));
        Is(HttpStatusCode.Created, late);
        Assert.Contains("Older than the newest", late.Raw);
        Assert.Equal("RUNNING", S(await Machine(r), "machine", "state"));
        var telemetry = (await Get($"/api/iot/machines/{r.Machine}/telemetry", r.Monitor.Token)).Body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, telemetry.Length);
        Assert.Contains(telemetry, x => S(x, "outcome") == "LATE");

        Is(HttpStatusCode.BadRequest, await Telemetry(r, new { state = "FLYING" }), "state_invalid");
        Is(HttpStatusCode.BadRequest, await Telemetry(r, new { state = "RUNNING" }, at: DateTimeOffset.UtcNow.AddMinutes(10)), "occurred_in_future");
        Is(HttpStatusCode.Conflict, await Telemetry(r, new { state = "RUNNING" }, device: r.CounterCode), "telemetry_not_supported");
        Is(HttpStatusCode.NotFound, await Telemetry(r, new { state = "RUNNING" }, device: "NO-SUCH-DEVICE"), "device_not_found");
        Is(HttpStatusCode.BadRequest, await Post("/api/iot/ingest/telemetry", r.Devices.Token, new { factoryId = r.Factory, deviceId = r.SensorCode, state = "RUNNING" }),
            "message_id_required");
    }

    [DockerFact]
    public async Task A_device_going_offline_makes_the_machine_offline_never_stopped()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }));
        Is(HttpStatusCode.Created, await Status(r, "OFFLINE"));
        var m = await Machine(r);
        Assert.Equal("OFFLINE", S(m, "machine", "state"));
        Assert.DoesNotContain(m.GetProperty("recentTransitions").EnumerateArray(), x => S(x, "toState") == "STOPPED");
        var alert = (await Alerts(r)).Single(a => S(a, "alertType") == "MACHINE_OFFLINE");
        Assert.Contains("does not mean it stopped", S(alert, "description"));
        Is(HttpStatusCode.Created, await Status(r, "ONLINE"));
        Assert.Equal("UNKNOWN", S(await Machine(r), "machine", "state"));
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "STOPPED" }));
        Assert.Equal("STOPPED", S(await Machine(r), "machine", "state"));
        var overview = (await Get($"/api/iot/overview?factoryId={r.Factory}", r.Monitor.Token)).Body;
        Assert.Equal(("2", "1"), (S(overview, "counts", "total"), S(overview, "counts", "stopped")));
        Assert.Equal("1", S(overview, "counts", "unmonitored"));
    }

    [DockerFact]
    public async Task Timed_rules_raise_an_unexpected_stoppage_during_a_shift_through_the_machine_monitor()
    {
        var r = await NewRig();
        Is(HttpStatusCode.OK, await Put($"/api/iot/machines/{r.Machine}/profile", r.Devices.Token, Profile(stopAfter: 60)));
        // Two minutes ago: old enough for the 60 s rule, recent enough to count as the sensor being online.
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "STOPPED" }, at: DateTimeOffset.UtcNow.AddMinutes(-2)));
        Is(HttpStatusCode.OK, await Post($"/api/iot/monitor/run?factoryId={r.Factory}", r.Devices.Token, new { }));
        Assert.Contains(await Alerts(r), a => S(a, "alertType") == "MACHINE_UNEXPECTED_STOP");
        // Maintenance stops the rule and telemetry can't leave maintenance.
        Is(HttpStatusCode.BadRequest, await Post($"/api/iot/machines/{r.Machine}/maintenance", r.Devices.Token, new { inMaintenance = true }), "reason_required");
        Is(HttpStatusCode.OK, await Post($"/api/iot/machines/{r.Machine}/maintenance", r.Devices.Token, new { inMaintenance = true, reason = "Needle change" }));
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }));
        Assert.Equal("MAINTENANCE", S(await Machine(r), "machine", "state"));
        Is(HttpStatusCode.OK, await Post($"/api/iot/machines/{r.Machine}/maintenance", r.Devices.Token, new { inMaintenance = false }));
        Assert.Equal("UNKNOWN", S(await Machine(r), "machine", "state"));
        Assert.Contains(await AiSettingsTestKit.AuditText(Api, "machine.maintenance"), x => x.Contains("Needle change"));
    }

    // ─── Production ────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Production_counts_handle_increments_absolute_readings_resets_duplicates_and_corrections()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }, at: DateTimeOffset.UtcNow.AddMinutes(-10)));
        var id = M();
        var three = await Count(r, new { mode = "INCREMENT", value = 3 }, id);
        Is(HttpStatusCode.Created, three);
        Assert.Equal(("COUNTED", "3"), (S(three.Body, "countStatus"), S(three.Body, "pieces")));
        Assert.Equal("Duplicate", S((await Count(r, new { mode = "INCREMENT", value = 3 }, id)).Body, "outcome"));
        Assert.Equal("BASELINE", S((await Count(r, new { mode = "ABSOLUTE", value = 1000 })).Body, "countStatus"));
        Assert.Equal("10", S((await Count(r, new { mode = "ABSOLUTE", value = 1010 })).Body, "pieces"));
        Assert.Equal("OUT_OF_ORDER", S((await Count(r, new { mode = "ABSOLUTE", value = 1005 }, at: DateTimeOffset.UtcNow.AddMinutes(-2))).Body, "countStatus"));

        Is(HttpStatusCode.Forbidden, await Post("/api/iot/production/counter-reset", r.Monitor.Token, new { deviceId = r.Counter, reason = "Shift change reset" }));
        var reset = await Post("/api/iot/production/counter-reset", r.Manager.Token, new { deviceId = r.Counter, reason = "Shift change reset" });
        Is(HttpStatusCode.OK, reset);
        Assert.Equal(("RESET", "MANUAL"), (S(reset.Body, "status"), S(reset.Body, "transport")));
        Assert.Equal("2", S((await Count(r, new { mode = "ABSOLUTE", value = 2 })).Body, "pieces"));

        var report = (await Get($"/api/iot/production?factoryId={r.Factory}&source=REAL", r.Monitor.Token)).Body;
        Assert.Equal(15, N(report, "totalPieces"));
        Assert.Contains(report.GetProperty("byLine").EnumerateArray(), x => S(x, "key") == r.Line && S(x, "pieces") == "15");
        Assert.Contains(report.GetProperty("byShift").EnumerateArray(), x => S(x, "key") == "ALLDAY" && S(x, "pieces") == "15");
        Assert.Contains(report.GetProperty("byMachine").EnumerateArray(), x => S(x, "label") == r.MachineCode && S(x, "pieces") == "15");
        Assert.Equal(15, report.GetProperty("hourly").EnumerateArray().Sum(x => long.Parse(S(x, "pieces")!)));
        Assert.True(report.GetProperty("runningSeconds").GetDouble() > 0);
        Assert.Contains("not a worker's working time", S(report, "note"));
        Assert.Equal(0, N((await Get($"/api/iot/production?factoryId={r.Factory}&source=SIMULATED", r.Monitor.Token)).Body, "totalPieces"));

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).ToString("yyyy-MM-dd");
        Is(HttpStatusCode.Forbidden, await Post("/api/iot/production/corrections", r.Devices.Token,
            new { factoryId = r.Factory, machineId = r.Machine, productionDate = today, piecesDelta = 5, reason = "Counted by hand" }));
        Is(HttpStatusCode.Created, await Post("/api/iot/production/corrections", r.Manager.Token,
            new { factoryId = r.Factory, machineId = r.Machine, productionDate = today, shiftId = r.Shift, piecesDelta = 5, reason = "Counter missed a bundle" }));
        var corrected = (await Get($"/api/iot/production?factoryId={r.Factory}", r.Monitor.Token)).Body;
        Assert.Equal((20, 15, 5), (N(corrected, "totalPieces"), N(corrected, "countedPieces"), N(corrected, "correctionPieces")));
        Assert.Contains(await AiSettingsTestKit.AuditText(Api, "production."), x => x.Contains("missed a bundle"));
        Assert.Contains(await AiSettingsTestKit.AuditText(Api, "production_counter.reset"), x => x.Contains("Shift change reset"));
        var counts = (await Get($"/api/iot/production/counts?machineId={r.Machine}", r.Monitor.Token)).Body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(6, counts.Length); // duplicates are never stored twice
    }

    [DockerFact]
    public async Task A_pulses_per_piece_rule_counts_garments_not_pulses()
    {
        var r = await NewRig();
        Is(HttpStatusCode.OK, await Put($"/api/iot/machines/{r.Machine}/profile", r.Devices.Token, Profile("PULSES_PER_PIECE", 2)));
        Assert.Equal("2", S((await Count(r, new { mode = "INCREMENT", value = 5 })).Body, "pieces"));
        Assert.Equal("1", S((await Count(r, new { mode = "INCREMENT", value = 1 })).Body, "pieces")); // the carried pulse completes a piece
        Is(HttpStatusCode.BadRequest, await Put($"/api/iot/machines/{r.Machine}/profile", r.Devices.Token, Profile("PIECE", 3)), "pulses_per_piece_mode");
    }

    // ─── Device authentication and isolation ───────────────────────────────

    [DockerFact]
    public async Task Device_keys_authenticate_http_devices_and_are_never_shown_again()
    {
        var r = await NewRig();
        var issued = await Post($"/api/iot/devices/{r.Sensor}/key", r.Devices.Token, new { });
        Is(HttpStatusCode.OK, issued);
        var key = S(issued.Body, "key")!;
        Dictionary<string, string> H(Guid device, string k) => new() { ["X-Device-Id"] = device.ToString(), ["X-Device-Key"] = k };
        var ok = await Post("/api/iot/device/telemetry", null, new { messageId = M(), state = "RUNNING" }, H(r.Sensor, key));
        Is(HttpStatusCode.Created, ok);
        Is(HttpStatusCode.Unauthorized, await Post("/api/iot/device/telemetry", null, new { messageId = M(), state = "RUNNING" }, H(r.Sensor, key + "x")));
        Is(HttpStatusCode.Unauthorized, await Post("/api/iot/device/telemetry", null, new { messageId = M(), state = "RUNNING" }, H(r.Counter, key)));
        Is(HttpStatusCode.Forbidden, await Post("/api/iot/device/telemetry", null, new { messageId = M(), deviceId = r.CounterCode, state = "RUNNING" }, H(r.Sensor, key)),
            "device_mismatch");
        var listed = await Get($"/api/iot/devices?factoryId={r.Factory}", r.Devices.Token);
        Assert.DoesNotContain(key, listed.Raw);
        Assert.Contains("\"hasKey\":true", listed.Raw);
        var audit = string.Join("\n", await AiSettingsTestKit.AuditText(Api, "device_iot."));
        Assert.DoesNotContain(key, audit);
        Is(HttpStatusCode.OK, await Send(HttpMethod.Delete, $"/api/iot/devices/{r.Sensor}/key", r.Devices.Token));
        Is(HttpStatusCode.Unauthorized, await Post("/api/iot/device/telemetry", null, new { messageId = M(), state = "RUNNING" }, H(r.Sensor, key)));
        // MQTT devices authenticate at the broker, not with a device key.
        Is(HttpStatusCode.OK, await Put($"/api/iot/devices/{r.Sensor}/config", r.Devices.Token, new { deviceType = "SEWING_MACHINE_SENSOR", protocol = "MQTT", mqttClientId = "s-01" }));
        Is(HttpStatusCode.BadRequest, await Post($"/api/iot/devices/{r.Sensor}/key", r.Devices.Token, new { }), "device_not_http");
        Is(HttpStatusCode.BadRequest, await Put($"/api/iot/devices/{r.Sensor}/config", r.Devices.Token, new { deviceType = "SEWING_MACHINE_SENSOR", protocol = "SIMULATOR" }),
            "protocol_simulation");
    }

    [DockerFact]
    public async Task Factories_are_isolated_and_roles_see_only_what_they_may()
    {
        var r = await NewRig();
        var other = await NewRig();
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }));
        // Another factory's device administrator can't speak for our device, even naming our factory.
        Is(HttpStatusCode.NotFound, await Telemetry(r, new { state = "STOPPED" }, bearer: other.Devices.Token));
        Is(HttpStatusCode.NotFound, await Get($"/api/iot/machines/{r.Machine}", other.Monitor.Token));
        Assert.DoesNotContain(r.MachineCode, (await Get("/api/iot/machines?pageSize=500", other.Monitor.Token)).Raw);
        Assert.Equal(0, N((await Get($"/api/iot/production?factoryId={r.Factory}", other.Monitor.Token)).Body, "totalPieces"));
        // A factory id in the payload that doesn't match the device's factory is refused.
        Is(HttpStatusCode.NotFound, await Post("/api/iot/ingest/telemetry", r.Devices.Token,
            new { factoryId = other.Factory, deviceId = r.SensorCode, messageId = M(), state = "RUNNING" }));
        // Roles.
        Is(HttpStatusCode.Forbidden, await Get($"/api/iot/overview?factoryId={r.Factory}", r.Hr.Token));
        Is(HttpStatusCode.Forbidden, await Get($"/api/iot/production?factoryId={r.Factory}", r.Devices.Token));
        Is(HttpStatusCode.Forbidden, await Put($"/api/iot/machines/{r.Machine}/profile", r.Monitor.Token, Profile()));
        Is(HttpStatusCode.Forbidden, await Telemetry(r, new { state = "RUNNING" }, bearer: r.Monitor.Token));
        var overview = (await Get($"/api/iot/overview?factoryId={r.Factory}", r.Devices.Token)).Body;
        Assert.Equal("False", S(overview, "canSeeProduction"));
        Assert.Equal("0", S(overview, "piecesToday"));
        // Machine events can't be injected by a producer: they are derived from sensor data.
        Is(HttpStatusCode.BadRequest, await Post("/api/monitoring/events", r.Devices.Token, new
        {
            eventType = "MACHINE_STATE_CHANGED", factoryId = r.Factory, idempotencyKey = M(), payload = new { toStatus = "RUNNING" },
        }), "machine_events_are_derived");
    }

    // ─── Simulator ─────────────────────────────────────────────────────────

    [DockerFact]
    public async Task The_simulator_drives_simulated_machines_through_the_same_pipeline_and_never_real_ones()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Conflict, await Post("/api/iot/simulator/setup", r.Devices.Token, new { machineId = r.Machine }), "machine_has_real_devices");
        var setup = await Post("/api/iot/simulator/setup", r.Devices.Token, new { machineId = r.Machine2 });
        Is(HttpStatusCode.OK, setup);
        Assert.Equal(2, setup.Body.GetProperty("devices").GetArrayLength());
        Assert.All(setup.Body.GetProperty("devices").EnumerateArray(), d => Assert.Equal(("True", "SIMULATOR"), (S(d, "isSimulated"), S(d, "protocol"))));

        async Task<JsonElement> Sim(string scenario, object? extra = null)
        {
            var body = new Dictionary<string, object?> { ["scenario"] = scenario, ["factoryId"] = r.Factory, ["machineId"] = r.Machine2 };
            if (extra is not null) foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject()) body[p.Name] = p.Value;
            var res = await Post("/api/iot/simulator/send", r.Admin.Token, body);
            Is(HttpStatusCode.OK, res);
            return res.Body;
        }
        var run = await Sim("RUNNING");
        Assert.All(run.GetProperty("messages").EnumerateArray(), x => Assert.Equal("Accepted", S(x, "outcome")));
        var m = await Machine(r, r.Machine2, "SIMULATED");
        Assert.Equal(("RUNNING", "SIMULATED", "True"), (S(m, "machine", "state"), S(m, "machine", "sourceType"), S(m, "machine", "isSimulated")));
        await Sim("PIECES", new { count = 4 });
        Assert.Equal(new[] { "Accepted", "Duplicate" }, (await Sim("DUPLICATE")).GetProperty("messages").EnumerateArray().Select(x => S(x, "outcome")));
        Assert.Equal("Rejected", S((await Sim("INVALID")).GetProperty("messages")[0], "outcome"));
        await Sim("DELAYED", new { delaySeconds = 120 });
        Assert.Equal(6, N((await Get($"/api/iot/production?factoryId={r.Factory}&source=SIMULATED", r.Monitor.Token)).Body, "totalPieces")); // 4 + duplicate once + delayed 1
        Assert.Equal(0, N((await Get($"/api/iot/production?factoryId={r.Factory}&source=REAL", r.Monitor.Token)).Body, "totalPieces"));
        await Sim("OFFLINE");
        Assert.Equal("OFFLINE", S(await Machine(r, r.Machine2, "SIMULATED"), "machine", "state"));
        var events = (await Get($"/api/monitoring/events?machineId={r.Machine2}&pageSize=100", r.Monitor.Token)).Body.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal("SIMULATED", S(e, "sourceType")));
        Assert.Contains(await Alerts(r, r.Machine2), a => S(a, "alertType") == "MACHINE_OFFLINE" && S(a, "sourceType") == "SIMULATED");
        var deviceEvents = await Rag.WithDb(Api, db => db.DeviceEvents.Where(e => e.FactoryId == r.Factory && e.Code == "IOT_PAYLOAD_INVALID").CountAsync());
        Assert.Equal(1, deviceEvents);
        // Real machine, real devices: the simulator has nothing to drive there.
        Is(HttpStatusCode.BadRequest, await Post("/api/iot/simulator/send", r.Admin.Token, new { scenario = "RUNNING", factoryId = r.Factory, machineId = r.Machine }),
            "no_simulated_machines");
        // Real ingestion refuses simulated devices.
        var simSensor = S(setup.Body.GetProperty("devices")[0], "code")!;
        Is(HttpStatusCode.Conflict, await Telemetry(r, new { state = "RUNNING" }, device: simSensor), "device_is_simulated");
    }

    // ─── History and real time ─────────────────────────────────────────────

    [DockerFact]
    public async Task History_is_append_only_and_live_updates_reach_only_the_machines_factory()
    {
        var r = await NewRig();
        var other = await NewRig();
        var mine = new ConcurrentQueue<JsonElement>();
        var theirs = new ConcurrentQueue<JsonElement>();
        await using var hubMine = Hub(r.Monitor.Token, mine);
        await using var hubTheirs = Hub(other.Monitor.Token, theirs);
        await hubMine.StartAsync();
        await hubTheirs.StartAsync();
        await hubMine.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);
        await hubTheirs.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);

        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }));
        Is(HttpStatusCode.Created, await Count(r, new { mode = "INCREMENT", value = 1 }));
        for (var i = 0; i < 50 && mine.Count < 2; i++) await Task.Delay(100);
        Assert.Contains(mine, x => S(x, "machine", "id") == r.Machine.ToString() && S(x, "machine", "state") == "RUNNING");
        Assert.Contains(mine, x => S(x, "machineId") == r.Machine.ToString() && x.TryGetProperty("sourceType", out _) && !x.TryGetProperty("pieces", out _));
        await Task.Delay(300);
        Assert.DoesNotContain(theirs, x => S(x, "machine", "id") == r.Machine.ToString() || S(x, "machineId") == r.Machine.ToString());

        foreach (var sql in new[]
                 {
                     "UPDATE production_counter_events SET \"Pieces\" = 99", "DELETE FROM production_counter_events",
                     "UPDATE machine_state_transitions SET \"Reason\" = 'x'", "UPDATE machine_telemetry SET \"PowerWatts\" = 1",
                 })
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db => db.Database.ExecuteSqlRawAsync(sql)));
            Assert.Contains("append-only", ex.ToString());
        }
        // Retention may delete raw telemetry (and only telemetry).
        await Rag.WithDb(Api, db => db.Database.ExecuteSqlRawAsync("DELETE FROM machine_telemetry WHERE \"FactoryId\" = {0}", r.Factory));
    }

    private HubConnection Hub(string token, ConcurrentQueue<JsonElement> inbox)
    {
        var server = Api.Server;
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/monitoring"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Headers[TestClientIpStartupFilter.Header] = TestClientIpStartupFilter.NewAddress();
            })
            .Build();
        conn.On<JsonElement>("machineState", m => inbox.Enqueue(m));
        conn.On<JsonElement>("productionChanged", m => inbox.Enqueue(m));
        return conn;
    }
}
