using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Api.Hubs;
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
/// Central monitoring end to end against real PostgreSQL, through the HTTP
/// API and the SignalR hub: the event contract and idempotency, verified
/// identities, factory and line isolation, the alert lifecycle and its
/// history, device / camera status flowing in, the simulator, reviews, and
/// who receives which real-time update.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbMonitoringTests
{
    private readonly PostgresStack _stack;
    public DbMonitoringTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    // ─── HTTP plumbing ─────────────────────────────────────────────────────

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public string Id => Body.GetProperty("id").GetString()!;
    }

    /// <summary>A value at a path; null when any step is missing or null (the API omits null properties).</summary>
    private static string? S(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out var next)) return null;
            e = next;
        }
        return e.ValueKind switch { JsonValueKind.Null or JsonValueKind.Undefined => null, JsonValueKind.String => e.GetString(), _ => e.ToString() };
    }

    private async Task<Reply> Send(HttpMethod method, string path, string? bearer, HttpContent? content = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[')) ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string bearer, object body) => Send(HttpMethod.Post, path, bearer, Rag.Json(body));
    private Task<Reply> Patch(string path, string bearer, object body) => Send(HttpMethod.Patch, path, bearer, Rag.Json(body));

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static string K() => $"k-{Guid.NewGuid():N}";
    private static IEnumerable<JsonElement> Items(Reply r) => r.Body.GetProperty("items").EnumerateArray();
    private static List<string> Ids(Reply r) => Items(r).Select(e => e.GetProperty("id").GetString()!).ToList();

    // ─── People and places ─────────────────────────────────────────────────

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, string? factory, params string[] lines)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@monitoring.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        if (factory is not null) user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = Guid.Parse(factory) });
        foreach (var line in lines) user.Lines.Add(new UserProductionLineAssignment { UserId = id, LineId = line });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, email, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    public sealed record Site(string Factory, string Floor, string Sewing, string Line1, string Line2, string T25, string T101,
        Person Admin, Person Devices, Person Manager, Person Supervisor, Person Monitor, Person Hr, Person Worker);

    private async Task<Site> NewSite()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var f = await Post("/api/factories", super.Token, new { code = $"M{Tag()[..6]}", name = $"Monitoring factory {Tag()}" });
        Is(HttpStatusCode.Created, f);
        var admin = await NewPerson(R.FactoryAdmin, f.Id);
        var dept = await Post($"/api/factories/{f.Id}/departments", admin.Token, new { code = "SEW", name = "Sewing" });
        var floor = await Post($"/api/factories/{f.Id}/floors", admin.Token, new { code = "F1", name = "First floor" });
        Is(HttpStatusCode.Created, floor);
        string l1 = $"ml-{Tag()}", l2 = $"ml-{Tag()}";
        await Rag.WithDb(Api, db =>
        {
            db.ProductionLines.Add(new ProductionLine { Id = l1, Name = $"Line 1 {l1}", Workstations = 40, FactoryId = Guid.Parse(f.Id) });
            db.ProductionLines.Add(new ProductionLine { Id = l2, Name = $"Line 2 {l2}", Workstations = 40, FactoryId = Guid.Parse(f.Id) });
            return db.SaveChangesAsync();
        });
        async Task<string> Ws(string line, string code)
        {
            var w = await Post("/api/workstations", admin.Token, new { factoryId = f.Id, lineId = line, departmentId = dept.Id, code, floorId = floor.Id });
            Is(HttpStatusCode.Created, w);
            return w.Id;
        }
        return new Site(f.Id, floor.Id, dept.Id, l1, l2, await Ws(l1, "T-025"), await Ws(l2, "T-101"),
            admin, await NewPerson(R.DeviceAdmin, f.Id), await NewPerson(R.FactoryManager, f.Id), await NewPerson(R.ProductionSupervisor, f.Id, l1),
            await NewPerson(R.MonitoringOperator, f.Id), await NewPerson(R.HrOfficer, f.Id), await NewPerson(R.Worker, f.Id));
    }

    private async Task<string> NewCamera(Site s, string? line = null, string[]? ws = null, string health = "None")
    {
        var r = await Post("/api/cameras", s.Devices.Token, new
        {
            factoryId = s.Factory, name = $"Camera {Tag()}", cameraType = "IpCamera", floorId = s.Floor, departmentId = s.Sewing,
            lineId = line, workstationIds = ws ?? Array.Empty<string>(), healthDevice = health,
        });
        Is(HttpStatusCode.Created, r);
        return r.Id;
    }

    private async Task<string> Zone(Site s, string camera, string name, string type, string? workstation = null)
    {
        var id = Guid.NewGuid();
        await Rag.WithDb(Api, db =>
        {
            db.CameraZones.Add(new CameraZone
            {
                Id = id, CameraId = Guid.Parse(camera), FactoryId = Guid.Parse(s.Factory), Name = name, ZoneType = type,
                WorkstationId = workstation is null ? null : Guid.Parse(workstation), Polygon = "[{\"x\":0.1,\"y\":0.1},{\"x\":0.4,\"y\":0.1},{\"x\":0.4,\"y\":0.5}]",
                CreatedByEmail = "test", UpdatedByEmail = "test",
            });
            return db.SaveChangesAsync();
        });
        return id.ToString();
    }

    private async Task<string> Worker(Site s)
    {
        var w = await Post("/api/workers", s.Hr.Token, new
        {
            factoryId = s.Factory, fullName = $"Worker {Tag()}", phone = "01711" + Random.Shared.Next(100000, 999999), joinedOn = "2024-01-01",
            departmentId = s.Sewing, designation = "Operator", lineId = s.Line1,
        });
        Is(HttpStatusCode.Created, w);
        return w.Id;
    }

    private async Task<string> RealDevice(Site s)
    {
        var d = await Post("/api/devices", s.Devices.Token, new
        {
            factoryId = s.Factory, code = $"GW-{Tag()}", name = "Gateway", category = "Esp32Gateway", isSimulated = false, lineId = s.Line1,
            heartbeatIntervalSeconds = 30, offlineAfterSeconds = 90,
        });
        Is(HttpStatusCode.Created, d);
        return d.Id;
    }

    private Task<Reply> Ingest(Person who, object body) => Post("/api/monitoring/events", who.Token, body);

    private async Task Bad(Person who, string code, object body) => Is(HttpStatusCode.BadRequest, await Ingest(who, body), code);

    private static object Detected(Site s, string camera, string? key = null, string track = "t-1", double confidence = 0.9) =>
        new { eventType = "PERSON_DETECTED", factoryId = s.Factory, idempotencyKey = key ?? K(), cameraId = camera, trackId = track, confidence };

    private static object CameraStatus(Site s, string camera, string to) =>
        new { eventType = "CAMERA_STATUS_CHANGED", factoryId = s.Factory, idempotencyKey = K(), cameraId = camera, payload = new { toStatus = to, reason = "cable unplugged" } };

    private async Task<List<JsonElement>> Events(Person who, string query) =>
        Items(await Get($"/api/monitoring/events?pageSize=200&{query}", who.Token)).ToList();

    private async Task<SimulatorTickResultView> Tick(DateTimeOffset at)
    {
        using var scope = Api.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<IMonitoringSimulatorService>().TickAsync(at, default);
        return new SimulatorTickResultView(r.Runs, r.Events, r.Completed);
    }

    public sealed record SimulatorTickResultView(int Runs, int Events, int Completed);

    // ─── Ingestion contract ────────────────────────────────────────────────

    [DockerFact]
    public async Task Real_events_are_validated_against_the_contract_and_stored_once_per_key()
    {
        var s = await NewSite();
        var other = await NewSite();
        var cam = await NewCamera(s, line: s.Line1, ws: new[] { s.T25 });
        var cam2 = await NewCamera(s, line: s.Line1);
        var otherCam = await NewCamera(other, line: other.Line1);
        var zoneOfCam2 = await Zone(s, cam2, "Walkway", "Walkway");

        var key = K();
        var first = await Ingest(s.Devices, new
        {
            eventType = "person_detected", factoryId = s.Factory, idempotencyKey = key, cameraId = cam, trackId = "t-1", confidence = 0.91,
            payload = new { bbox = new { x = 0.1, y = 0.2, w = 0.1, h = 0.3 } },
        });
        Is(HttpStatusCode.Created, first);
        Assert.Equal("Accepted", S(first.Body, "outcome"));
        var ev = first.Body.GetProperty("event");
        Assert.Equal("PERSON_DETECTED", S(ev, "eventType"));
        Assert.Equal("REAL", S(ev, "sourceType"));
        Assert.Equal("API", S(ev, "producer"));
        Assert.Equal("VISION", S(ev, "category"));
        Assert.Equal(s.Line1, S(ev, "line", "id"));          // derived from the camera
        Assert.Equal("t-1", S(ev, "trackId"));
        Assert.Null(S(ev, "verifiedWorker"));
        Assert.NotNull(S(ev, "receivedAt"));

        // Resending the same key returns the stored event; reusing it for something else is refused.
        var again = await Ingest(s.Devices, new { eventType = "PERSON_DETECTED", factoryId = s.Factory, idempotencyKey = key, cameraId = cam, trackId = "t-1", confidence = 0.5 });
        Is(HttpStatusCode.OK, again);
        Assert.Equal("Duplicate", S(again.Body, "outcome"));
        Assert.Equal(S(ev, "id"), S(again.Body, "event", "id"));
        Assert.Equal("0.91", S(again.Body, "event", "confidence"));
        Is(HttpStatusCode.Conflict, await Ingest(s.Devices, new { eventType = "PERSON_LOST", factoryId = s.Factory, idempotencyKey = key, cameraId = cam, trackId = "t-1" }),
            "idempotency_key_reused");
        Assert.Equal(1, await Rag.WithDb(Api, db => db.MonitoringEvents.CountAsync(e => e.IdempotencyKey == "api/" + key)));

        var f = s.Factory;
        await Bad(s.Devices, "idempotency_key_required", new { eventType = "PERSON_DETECTED", factoryId = f, cameraId = cam });
        await Bad(s.Devices, "source_type_not_allowed", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = cam, sourceType = "SIMULATED" });
        await Bad(s.Devices, "event_type_invalid", new { eventType = "WORKER_IDLE", factoryId = f, idempotencyKey = K(), cameraId = cam });
        await Bad(s.Devices, "camera_required", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K() });
        await Bad(s.Devices, "track_id_required", new { eventType = "TRACK_UPDATED", factoryId = f, idempotencyKey = K(), cameraId = cam });
        await Bad(s.Devices, "track_id_invalid", new { eventType = "TRACK_UPDATED", factoryId = f, idempotencyKey = K(), cameraId = cam, trackId = "has space" });
        await Bad(s.Devices, "confidence_invalid", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = cam, confidence = 1.5 });
        await Bad(s.Devices, "confidence_required", new { eventType = "UNCERTAIN_DETECTION", factoryId = f, idempotencyKey = K(), cameraId = cam });
        await Bad(s.Devices, "occurred_in_future", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = cam, occurredAt = DateTimeOffset.UtcNow.AddHours(1) });
        await Bad(s.Devices, "occurred_too_old", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = cam, occurredAt = DateTimeOffset.UtcNow.AddDays(-8) });
        await Bad(s.Devices, "payload_invalid", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = cam, payload = new { apiKey = "x" } });
        await Bad(s.Devices, "payload_invalid", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = cam, payload = new { workerId = "W-001" } });
        await Bad(s.Devices, "to_status_invalid", new { eventType = "CAMERA_STATUS_CHANGED", factoryId = f, idempotencyKey = K(), cameraId = cam, payload = new { toStatus = "BROKEN" } });
        await Bad(s.Devices, "zone_required", new { eventType = "ZONE_ENTERED", factoryId = f, idempotencyKey = K(), cameraId = cam, trackId = "t-1" });
        await Bad(s.Devices, "zone_not_on_camera", new { eventType = "ZONE_ENTERED", factoryId = f, idempotencyKey = K(), cameraId = cam, zoneId = zoneOfCam2, trackId = "t-1" });
        await Bad(s.Devices, "workstation_not_on_line", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = cam, workstationId = s.T25, lineId = s.Line2 });
        await Bad(s.Devices, "camera_not_in_factory", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = otherCam });
        await Bad(s.Devices, "line_not_in_factory", new { eventType = "PERSON_DETECTED", factoryId = f, idempotencyKey = K(), cameraId = cam, lineId = other.Line1 });
        await Bad(s.Devices, "activity_required", new { eventType = "ACTIVITY_CLASSIFIED", factoryId = f, idempotencyKey = K(), cameraId = cam, trackId = "t-1", confidence = 0.8 });
        await Bad(s.Devices, "model_required", new { eventType = "MODEL_STATUS_CHANGED", factoryId = f, idempotencyKey = K(), payload = new { toStatus = "READY" } });

        // A zone event takes its camera and workstation from the zone.
        var zoneWs = await Zone(s, cam, "T-025", "Workstation", s.T25);
        var entered = await Ingest(s.Devices, new { eventType = "ZONE_ENTERED", factoryId = f, idempotencyKey = K(), zoneId = zoneWs, trackId = "t-1" });
        Is(HttpStatusCode.Created, entered);
        Assert.Equal(cam, S(entered.Body, "event", "camera", "id"));
        Assert.Equal(s.T25, S(entered.Body, "event", "workstation", "id"));
        Assert.Equal("T-025", S(entered.Body, "event", "zone", "name"));

        // The contract is published for producers.
        var contract = await Get("/api/monitoring/contract", s.Devices.Token);
        Is(HttpStatusCode.OK, contract);
        // 14 VISION + 6 SYSTEM types (the two machine types arrived with IoT machine monitoring; ACTIVITY_CHANGED, PPE_VIOLATION
        // and DETECTION_UNAVAILABLE with the camera AI), as MONITORING_EVENTS.md lists them.
        Assert.Equal(20, contract.Body.GetProperty("eventTypes").GetArrayLength());
        Assert.Contains("PPE_VIOLATION", contract.Raw);
        Assert.Contains("DETECTION_UNAVAILABLE", contract.Raw);
        Assert.Equal(BunonBrain.Application.Monitoring.MonitoringEventTypes.All.Length, contract.Body.GetProperty("eventTypes").GetArrayLength());
        Assert.Contains("MACHINE_STATE_CHANGED", contract.Raw);
        Assert.Contains("MACHINE_CONDITION", contract.Raw);
        Assert.Contains("FACE_RECOGNITION", contract.Raw);
    }

    [DockerFact]
    public async Task Worker_ids_need_a_verified_rfid_card_and_never_come_from_looks_or_uncertainty()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, line: s.Line1);
        var w1 = await Worker(s);
        var w2 = await Worker(s);
        var uid = "04" + Tag().ToUpperInvariant();
        Is(HttpStatusCode.Created, await Post($"/api/workers/{w1}/cards", s.Hr.Token, new { cardUid = uid }));

        object Ev(object? workerId, object? identity, string type = "PERSON_DETECTED") =>
            new { eventType = type, factoryId = s.Factory, idempotencyKey = K(), cameraId = cam, trackId = "t-9", confidence = 0.4, workerId, identity };

        await Bad(s.Devices, "worker_identity_unverified", Ev(w1, null));
        await Bad(s.Devices, "identity_method_not_accepted", Ev(w1, new { method = "FACE_RECOGNITION" }));
        await Bad(s.Devices, "identity_method_not_accepted", Ev(w1, new { method = "visual-similarity", cardUid = uid }));
        await Bad(s.Devices, "worker_identity_unverified", Ev(w1, new { method = "RFID_BADGE", cardUid = "04DEADBEEF00" }));
        await Bad(s.Devices, "worker_identity_mismatch", Ev(w2, new { method = "RFID_BADGE", cardUid = uid }));
        await Bad(s.Devices, "uncertain_detection_cannot_identify", Ev(w1, new { method = "RFID_BADGE", cardUid = uid }, "UNCERTAIN_DETECTION"));
        await Bad(s.Devices, "worker_not_applicable", new
        {
            eventType = "CAMERA_STATUS_CHANGED", factoryId = s.Factory, idempotencyKey = K(), cameraId = cam, payload = new { toStatus = "ONLINE" },
            workerId = w1, identity = new { method = "RFID_BADGE", cardUid = uid },
        });

        var verified = await Ingest(s.Devices, Ev(w1, new { method = "RFID_BADGE", cardUid = uid.ToLowerInvariant() }));
        Is(HttpStatusCode.Created, verified);
        Assert.Equal(w1, S(verified.Body, "event", "verifiedWorker", "id"));
        Assert.Equal("RFID_BADGE", S(verified.Body, "event", "identityMethod"));
        Assert.Contains("verified by RFID card", S(verified.Body, "event", "summary"));
        // The card alone names its worker (the server looked it up).
        var byCard = await Ingest(s.Devices, Ev(null, new { method = "RFID_BADGE", cardUid = uid }));
        Assert.Equal(w1, S(byCard.Body, "event", "verifiedWorker", "id"));
        // Without verification an event stays anonymous: a track, never a person.
        var anonymous = await Ingest(s.Devices, Ev(null, null));
        Is(HttpStatusCode.Created, anonymous);
        Assert.Null(S(anonymous.Body, "event", "verifiedWorker"));
        Assert.Single(await Events(s.Monitor, $"factoryId={s.Factory}&verifiedWorker=false"));

        // The database refuses a worker without a verified identity method.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db =>
        {
            db.MonitoringEvents.Add(new MonitoringEvent
            {
                FactoryId = Guid.Parse(s.Factory), EventType = "PERSON_DETECTED", Category = "VISION", SourceType = "REAL", Producer = "API",
                IdempotencyKey = K(), CameraId = Guid.Parse(cam), WorkerId = Guid.Parse(w1), Summary = "x", RecordedByEmail = "x",
                OccurredAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            });
            return db.SaveChangesAsync();
        }));
        Assert.Contains("CK_monitoring_events_worker_verified", ex.ToString());
    }

    // ─── Isolation ─────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Each_factory_line_and_role_sees_only_its_own_events_and_alerts()
    {
        var a = await NewSite();
        var b = await NewSite();
        var camA1 = await NewCamera(a, line: a.Line1);
        var camA2 = await NewCamera(a, line: a.Line2);
        var camB = await NewCamera(b, line: b.Line1);
        var eA1 = S((await Ingest(a.Devices, Detected(a, camA1))).Body, "event", "id")!;
        var eA2 = S((await Ingest(a.Devices, Detected(a, camA2))).Body, "event", "id")!;
        var sysA = await Ingest(a.Devices, CameraStatus(a, camA2, "OFFLINE"));
        Is(HttpStatusCode.Created, sysA);
        var sysAId = S(sysA.Body, "event", "id")!;
        var alertA = S(sysA.Body, "alert", "id")!;
        var eB = S((await Ingest(b.Devices, Detected(b, camB))).Body, "event", "id")!;

        // Factory isolation.
        var opA = (await Events(a.Monitor, "")).Select(e => S(e, "id")).ToList();
        Assert.Contains(eA1, opA);
        Assert.Contains(eA2, opA);
        Assert.Contains(sysAId, opA);
        Assert.DoesNotContain(eB, opA);
        Is(HttpStatusCode.NotFound, await Get($"/api/monitoring/events/{eB}", a.Monitor.Token));
        Is(HttpStatusCode.Forbidden, await Ingest(a.Devices, Detected(b, camB)), "factory_out_of_scope");

        // Line isolation: the supervisor of line 1 sees line 1 only.
        var sup = (await Events(a.Supervisor, "")).Select(e => S(e, "id")).ToList();
        Assert.Contains(eA1, sup);
        Assert.DoesNotContain(eA2, sup);
        Assert.DoesNotContain(sysAId, sup);
        Is(HttpStatusCode.NotFound, await Get($"/api/monitoring/alerts/{alertA}", a.Supervisor.Token));

        // IT / device administrators see equipment status, not people.
        var it = (await Events(a.Devices, "")).Select(e => S(e, "id")).ToList();
        Assert.Contains(sysAId, it);
        Assert.DoesNotContain(eA1, it);
        Is(HttpStatusCode.NotFound, await Get($"/api/monitoring/events/{eA1}", a.Devices.Token));
        Is(HttpStatusCode.OK, await Get($"/api/monitoring/alerts/{alertA}", a.Devices.Token));

        // HR and workers have no central monitoring; operators don't send events.
        foreach (var who in new[] { a.Hr, a.Worker })
        {
            Is(HttpStatusCode.Forbidden, await Get("/api/monitoring/events", who.Token));
            Is(HttpStatusCode.Forbidden, await Get("/api/monitoring/overview", who.Token));
            Is(HttpStatusCode.Forbidden, await Get("/api/monitoring/alerts", who.Token));
        }
        Is(HttpStatusCode.Forbidden, await Ingest(a.Monitor, Detected(a, camA1)));
        Is(HttpStatusCode.Forbidden, await Ingest(a.Supervisor, Detected(a, camA1)));

        // Alerts follow the same reach.
        Assert.DoesNotContain(alertA, Ids(await Get("/api/monitoring/alerts?status=ACTIVE&pageSize=200", b.Monitor.Token)));
        Is(HttpStatusCode.NotFound, await Post($"/api/monitoring/alerts/{alertA}/acknowledge", b.Monitor.Token, new { }));
        var overviewB = await Get("/api/monitoring/overview", b.Monitor.Token);
        Is(HttpStatusCode.OK, overviewB);
        Assert.Equal("0", S(overviewB.Body, "alerts", "active"));
        var overviewA = await Get($"/api/monitoring/overview?factoryId={a.Factory}", a.Monitor.Token);
        Assert.Equal("1", S(overviewA.Body, "alerts", "active"));
        Assert.Equal("1", S(overviewA.Body, "alerts", "unacknowledged"));
        Assert.Equal("2", S(overviewA.Body, "cameras", "total"));
        Is(HttpStatusCode.Forbidden, await Get($"/api/monitoring/overview?factoryId={b.Factory}&cameraId={camB}", a.Worker.Token));

        // Camera monitoring: one factory's cameras, no stream details.
        var grid = await Get("/api/monitoring/cameras", a.Monitor.Token);
        var gridIds = grid.Body.EnumerateArray().Select(c => S(c, "id")).ToList();
        Assert.Contains(camA1, gridIds);
        Assert.DoesNotContain(camB, gridIds);
        Is(HttpStatusCode.NotFound, await Get($"/api/monitoring/cameras/{camB}", a.Monitor.Token));
        var expanded = await Get($"/api/monitoring/cameras/{camA2}", a.Monitor.Token);
        Is(HttpStatusCode.OK, expanded);
        Assert.Equal("NOT_CONNECTED", S(expanded.Body, "aiStatus"));
        Assert.Equal("UNKNOWN", S(expanded.Body, "camera", "connection", "status"));
        Assert.Equal("1", S(expanded.Body, "camera", "activeAlerts"));
    }

    // ─── Alert lifecycle ───────────────────────────────────────────────────

    [DockerFact]
    public async Task Alerts_follow_their_lifecycle_and_keep_a_complete_history()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, line: s.Line1);

        var first = await Ingest(s.Devices, CameraStatus(s, cam, "OFFLINE"));
        Is(HttpStatusCode.Created, first);
        var alert = first.Body.GetProperty("alert");
        var id = S(alert, "id")!;
        Assert.Equal("OPEN", S(alert, "status"));
        Assert.Equal("HIGH", S(alert, "severity"));
        Assert.Equal("CAMERA_OFFLINE", S(alert, "alertType"));
        Assert.Equal("REAL", S(alert, "sourceType"));
        Assert.StartsWith("ALT-", S(alert, "code"));
        // The same situation again joins the open alert; the camera coming back annotates it.
        var repeat = await Ingest(s.Devices, CameraStatus(s, cam, "OFFLINE"));
        Assert.Equal(id, S(repeat.Body, "alert", "id"));
        Assert.Equal("2", S(repeat.Body, "alert", "occurrenceCount"));
        var back = await Ingest(s.Devices, CameraStatus(s, cam, "ONLINE"));
        Assert.Equal(id, S(back.Body, "alert", "id"));
        Assert.Equal("OPEN", S(back.Body, "alert", "status"));

        string Url(string action) => $"/api/monitoring/alerts/{id}/{action}";
        Is(HttpStatusCode.Conflict, await Post(Url("resolve"), s.Monitor.Token, new { resolutionCode = "ISSUE_FIXED", note = "fixed" }), "alert_not_acknowledged");
        var version = int.Parse(S((await Get($"/api/monitoring/alerts/{id}", s.Monitor.Token)).Body, "alert", "version")!);
        Is(HttpStatusCode.Conflict, await Post(Url("acknowledge"), s.Monitor.Token, new { expectedVersion = version - 1 }), "alert_changed");
        var ack = await Post(Url("acknowledge"), s.Monitor.Token, new { note = "Looking at it", expectedVersion = version });
        Is(HttpStatusCode.OK, ack);
        Assert.Equal("ACKNOWLEDGED", S(ack.Body, "alert", "status"));
        Assert.Equal(s.Monitor.Email, S(ack.Body, "alert", "acknowledgedByEmail"));
        Is(HttpStatusCode.Conflict, await Post(Url("acknowledge"), s.Monitor.Token, new { }), "alert_already_acknowledged");

        Is(HttpStatusCode.BadRequest, await Post(Url("escalate"), s.Monitor.Token, new { toRole = "CEO", reason = "x" }), "escalation_target_invalid");
        Is(HttpStatusCode.BadRequest, await Post(Url("escalate"), s.Monitor.Token, new { toRole = "FactoryManager", reason = " " }), "reason_required");
        var esc = await Post(Url("escalate"), s.Monitor.Token, new { toRole = "FactoryManager", reason = "Camera still dark after a reboot" });
        Is(HttpStatusCode.OK, esc);
        Assert.Equal("ESCALATED", S(esc.Body, "alert", "status"));
        Assert.Equal("1", S(esc.Body, "alert", "escalationLevel"));
        Assert.Equal("FactoryManager", S(esc.Body, "alert", "escalatedToRole"));

        // Permissions per action.
        Is(HttpStatusCode.Forbidden, await Post(Url("dismiss"), s.Monitor.Token, new { reason = "operators escalate, they don't dismiss" }));
        Is(HttpStatusCode.Forbidden, await Post(Url("resolve"), s.Hr.Token, new { resolutionCode = "ISSUE_FIXED", note = "x" }));
        Is(HttpStatusCode.Forbidden, await Post(Url("acknowledge"), s.Devices.Token, new { }));
        Is(HttpStatusCode.BadRequest, await Post(Url("resolve"), s.Manager.Token, new { resolutionCode = "ISSUE_FIXED", note = "" }), "note_required");
        Is(HttpStatusCode.BadRequest, await Post(Url("resolve"), s.Manager.Token, new { resolutionCode = "DOCK_PAY", note = "x" }), "resolution_code_invalid");
        var resolved = await Post(Url("resolve"), s.Manager.Token, new { resolutionCode = "ISSUE_FIXED", note = "Re-seated the network cable" });
        Is(HttpStatusCode.OK, resolved);
        Assert.Equal("RESOLVED", S(resolved.Body, "alert", "status"));
        Assert.Equal(s.Manager.Email, S(resolved.Body, "alert", "resolvedByEmail"));
        Is(HttpStatusCode.Conflict, await Post(Url("acknowledge"), s.Monitor.Token, new { }), "alert_closed");
        Is(HttpStatusCode.Conflict, await Post(Url("dismiss"), s.Manager.Token, new { reason = "too late now" }), "alert_closed");

        // Complete history, in order, with who did what.
        var history = resolved.Body.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(new[] { "CREATED", "REPEATED", "SOURCE_RECOVERED", "ACKNOWLEDGED", "ESCALATED", "RESOLVED" }, history.Select(h => S(h, "action")));
        Assert.Equal(s.Monitor.Email, S(history[3], "actorEmail"));
        Assert.Equal("FactoryManager", S(history[4], "escalatedToRole"));
        Assert.Equal(s.Manager.Email, S(history[5], "actorEmail"));
        Assert.Equal("ISSUE_FIXED", S(history[5], "resolutionCode"));
        var audit = await Rag.WithDb(Api, db => db.AuditLog.AsNoTracking().Where(x => x.EntityId == id).Select(x => x.Action).ToListAsync());
        Assert.Contains("monitoring_alert.acknowledged", audit);
        Assert.Contains("monitoring_alert.escalated", audit);
        Assert.Contains("monitoring_alert.resolved", audit);

        // A closed alert never reopens: the next occurrence raises a new one, which can be dismissed with a reason.
        var next = await Ingest(s.Devices, CameraStatus(s, cam, "OFFLINE"));
        var nextId = S(next.Body, "alert", "id")!;
        Assert.NotEqual(id, nextId);
        Is(HttpStatusCode.BadRequest, await Post($"/api/monitoring/alerts/{nextId}/dismiss", s.Manager.Token, new { reason = "no" }), "reason_required");
        var dismissed = await Post($"/api/monitoring/alerts/{nextId}/dismiss", s.Manager.Token, new { reason = "Camera removed for relocation" });
        Assert.Equal("DISMISSED", S(dismissed.Body, "alert", "status"));
        Assert.Equal("Camera removed for relocation", S(dismissed.Body, "alert", "dismissReason"));

        // Supervisors handle alerts on their own lines.
        var uncertain = await Ingest(s.Devices, new { eventType = "UNCERTAIN_DETECTION", factoryId = s.Factory, idempotencyKey = K(), cameraId = cam, confidence = 0.31 });
        Assert.Equal("UNCERTAIN_DETECTION_REVIEW", S(uncertain.Body, "alert", "alertType"));
        Assert.Equal("LOW", S(uncertain.Body, "alert", "severity"));
        Is(HttpStatusCode.OK, await Post($"/api/monitoring/alerts/{S(uncertain.Body, "alert", "id")}/acknowledge", s.Supervisor.Token, new { }));

        // History and raw events can't be rewritten, even directly in the database.
        var eventId = S(first.Body, "event", "id")!;
        foreach (var sql in new[]
                 {
                     $"UPDATE monitoring_alert_actions SET \"Note\" = 'x' WHERE \"AlertId\" = '{id}'",
                     $"DELETE FROM monitoring_alert_actions WHERE \"AlertId\" = '{id}'",
                     $"UPDATE monitoring_events SET \"Summary\" = 'x' WHERE \"Id\" = '{eventId}'",
                     $"DELETE FROM monitoring_events WHERE \"Id\" = '{eventId}'",
                 })
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db => db.Database.ExecuteSqlRawAsync(sql)));
            Assert.Contains("append-only", ex.ToString());
        }
    }

    // ─── Devices and cameras flowing in ────────────────────────────────────

    [DockerFact]
    public async Task Device_and_camera_status_changes_flow_into_monitoring_with_their_source_type()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, line: s.Line1, health: "Simulated");
        var device = S(Items(await Get($"/api/devices?cameraId={cam}", s.Devices.Token)).Single(), "id")!;

        Is(HttpStatusCode.OK, await Post("/api/simulator/devices/online", s.Devices.Token, new { deviceId = device }));
        Is(HttpStatusCode.OK, await Post("/api/simulator/devices/offline", s.Devices.Token, new { deviceId = device, reason = "demo" }));
        var events = await Events(s.Monitor, $"cameraId={cam}");
        var deviceEvents = events.Where(e => S(e, "eventType") == "DEVICE_STATUS_CHANGED").ToList();
        Assert.Equal(new[] { "OFFLINE", "ONLINE" }, deviceEvents.Select(e => S(e, "payload", "toStatus")));
        Assert.All(deviceEvents, e => { Assert.Equal("SIMULATED", S(e, "sourceType")); Assert.Equal("DEVICE_PIPELINE", S(e, "producer")); });
        var cameraOffline = events.Single(e => S(e, "eventType") == "CAMERA_STATUS_CHANGED" && S(e, "payload", "toStatus") == "OFFLINE");
        Assert.Equal("SIMULATED", S(cameraOffline, "sourceType"));
        Assert.StartsWith("SIMULATED", S(cameraOffline, "summary"));

        var alerts = Items(await Get($"/api/monitoring/alerts?cameraId={cam}&status=ACTIVE", s.Monitor.Token)).ToList();
        var camAlert = alerts.Single(a => S(a, "alertType") == "CAMERA_OFFLINE");
        Assert.Equal("SIMULATED", S(camAlert, "sourceType"));
        Assert.StartsWith("SIMULATED", S(camAlert, "title"));
        Assert.Contains(alerts, a => S(a, "alertType") == "DEVICE_OFFLINE");

        // The camera grid shows the simulated health, never as a physical connection.
        var tile = (await Get($"/api/monitoring/cameras?factoryId={s.Factory}", s.Monitor.Token)).Body.EnumerateArray().Single(c => S(c, "id") == cam);
        Assert.Equal("OFFLINE", S(tile, "connection", "status"));
        Assert.Equal("true", S(tile, "connection", "isSimulated"), ignoreCase: true);
        Assert.Equal("false", S(tile, "connection", "physicallyConnected"), ignoreCase: true);
        Assert.Equal("NONE", S(tile, "feed", "kind"));

        // A simulated device can't report REAL events.
        Is(HttpStatusCode.Conflict, await Ingest(s.Devices, new
        {
            eventType = "DEVICE_STATUS_CHANGED", factoryId = s.Factory, idempotencyKey = K(), deviceId = device, payload = new { toStatus = "ONLINE" },
        }), "device_is_simulated");

        // A real device's heartbeat and its stopped heartbeats are REAL.
        var real = await RealDevice(s);
        Is(HttpStatusCode.OK, await Post($"/api/devices/{real}/heartbeat", s.Devices.Token, new { messageId = "m1" }));
        var online = (await Events(s.Monitor, $"deviceId={real}")).Single();
        Assert.Equal("ONLINE", S(online, "payload", "toStatus"));
        Assert.Equal("REAL", S(online, "sourceType"));
        await Rag.WithDb(Api, db => db.Devices.Where(d => d.Id == Guid.Parse(real))
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.OfflineDeadline, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Is(HttpStatusCode.OK, await Post("/api/devices/health/check", s.Devices.Token, new { }));
        // Dated when the device went silent (its offline deadline), so it sorts before the heartbeat.
        var offline = (await Events(s.Monitor, $"deviceId={real}")).Single(e => S(e, "payload", "toStatus") == "OFFLINE");
        Assert.Equal("REAL", S(offline, "sourceType"));
        Assert.Equal("HeartbeatMissed", S(offline, "payload", "deviceEventType"));
        var realAlert = Items(await Get($"/api/monitoring/alerts?deviceId={real}", s.Monitor.Token)).Single();
        Assert.Equal("DEVICE_OFFLINE", S(realAlert, "alertType"));
        Assert.Equal("REAL", S(realAlert, "sourceType"));

        // An administrative camera status change is recorded (no alert: it's planned).
        Is(HttpStatusCode.OK, await Patch($"/api/cameras/{cam}/status", s.Devices.Token, new { status = "Maintenance", reason = "Lens cleaning" }));
        var admin = (await Events(s.Monitor, $"cameraId={cam}&eventType=CAMERA_STATUS_CHANGED")).First();
        Assert.Equal("CAMERA_SERVICE", S(admin, "producer"));
        Assert.Equal("MAINTENANCE", S(admin, "payload", "toStatus"));
        Assert.Single(Items(await Get($"/api/monitoring/alerts?cameraId={cam}&alertType=CAMERA_OFFLINE", s.Monitor.Token)));

        // Each stored device event is mirrored exactly once.
        var mirrored = await Rag.WithDb(Api, db => db.MonitoringEvents.Where(e => e.DeviceId == Guid.Parse(device) && e.EventType == "DEVICE_STATUS_CHANGED").CountAsync());
        var source = await Rag.WithDb(Api, db => db.DeviceEvents.Where(e => e.DeviceId == Guid.Parse(device) && (e.EventType == "ConnectionOnline" || e.EventType == "ConnectionOffline")).CountAsync());
        Assert.Equal(source, mirrored);
    }

    // ─── Simulator ─────────────────────────────────────────────────────────

    [DockerFact]
    public async Task The_simulator_uses_the_same_pipeline_and_everything_it_makes_is_simulated()
    {
        var s = await NewSite();
        var other = await NewSite();
        var cam = await NewCamera(s, line: s.Line1, ws: new[] { s.T25 });
        var restricted = await Zone(s, cam, "Store room", "Restricted");
        await Zone(s, cam, "T-025", "Workstation", s.T25);

        Is(HttpStatusCode.Forbidden, await Get($"/api/monitoring/simulator?factoryId={s.Factory}", s.Supervisor.Token));
        Is(HttpStatusCode.Forbidden, await Post("/api/monitoring/simulator/start", s.Hr.Token, new { factoryId = s.Factory }));
        Is(HttpStatusCode.Forbidden, await Post("/api/monitoring/simulator/start", other.Monitor.Token, new { factoryId = s.Factory }), "factory_out_of_scope");
        Is(HttpStatusCode.BadRequest, await Post("/api/monitoring/simulator/start", s.Monitor.Token, new { factoryId = s.Factory, eventsPerMinute = 500 }), "rate_invalid");

        var start = await Post("/api/monitoring/simulator/start", s.Monitor.Token, new { factoryId = s.Factory, cameraIds = new[] { cam }, eventsPerMinute = 120, durationMinutes = 1 });
        Is(HttpStatusCode.Created, start);
        var run = start.Id;
        Assert.Equal("RUNNING", S(start.Body, "status"));
        Is(HttpStatusCode.Conflict, await Post("/api/monitoring/simulator/start", s.Monitor.Token, new { factoryId = s.Factory }), "simulation_already_running");

        var startedAt = DateTimeOffset.Parse(S(start.Body, "startedAt")!);
        Assert.True((await Tick(startedAt.AddSeconds(3))).Events > 0);
        await Tick(startedAt.AddSeconds(6));
        var generated = await Events(s.Monitor, $"simulationRunId={run}");
        Assert.True(generated.Count >= 5, $"only {generated.Count} simulated events");
        Assert.All(generated, e =>
        {
            Assert.Equal("SIMULATED", S(e, "sourceType"));
            Assert.Equal("SIMULATOR", S(e, "producer"));
            Assert.Equal("True", S(e, "isSimulated"), ignoreCase: true);
            Assert.StartsWith("SIMULATED", S(e, "summary"));
            Assert.Null(S(e, "verifiedWorker"));
            Assert.Equal("True", S(e, "payload", "simulated"), ignoreCase: true);
        });
        // Real and simulated stay apart.
        var realOnly = (await Events(s.Monitor, $"factoryId={s.Factory}&sourceType=REAL")).Select(e => S(e, "id")).ToHashSet();
        Assert.DoesNotContain(generated, e => realOnly.Contains(S(e, "id")));

        // Single demo events.
        var u = await Post("/api/monitoring/simulator/events", s.Monitor.Token, new { factoryId = s.Factory, eventType = "UNCERTAIN_DETECTION", cameraId = cam });
        Is(HttpStatusCode.OK, u);
        Assert.Equal("UNCERTAIN_DETECTION_REVIEW", S(u.Body, "alert", "alertType"));
        Assert.Equal("SIMULATED", S(u.Body, "alert", "sourceType"));
        var z = await Post("/api/monitoring/simulator/events", s.Monitor.Token, new { factoryId = s.Factory, eventType = "ZONE_ENTERED", cameraId = cam, zoneId = restricted });
        Assert.Equal("RESTRICTED_ZONE_ENTRY", S(z.Body, "alert", "alertType"));
        Assert.StartsWith("SIMULATED", S(z.Body, "alert", "title"));
        var real = await RealDevice(s);
        Is(HttpStatusCode.Conflict, await Post("/api/monitoring/simulator/events", s.Monitor.Token, new { factoryId = s.Factory, eventType = "DEVICE_STATUS_CHANGED", deviceId = real }),
            "device_not_simulated");

        // Stopping stops it; a run that reaches its end completes by itself.
        var stop = await Post($"/api/monitoring/simulator/{run}/stop", s.Monitor.Token, new { reason = "demo over" });
        Is(HttpStatusCode.OK, stop);
        Assert.Equal("STOPPED", S(stop.Body, "status"));
        var before = (await Events(s.Monitor, $"simulationRunId={run}")).Count;
        await Tick(startedAt.AddSeconds(30));
        Assert.Equal(before, (await Events(s.Monitor, $"simulationRunId={run}")).Count);
        var second = await Post("/api/monitoring/simulator/start", s.Monitor.Token, new { factoryId = s.Factory, durationMinutes = 1 });
        Is(HttpStatusCode.Created, second);
        await Tick(DateTimeOffset.Parse(S(second.Body, "startedAt")!).AddMinutes(2));
        var status = await Get($"/api/monitoring/simulator?factoryId={s.Factory}", s.Monitor.Token);
        Assert.Null(S(status.Body, "running"));
        Assert.Equal("COMPLETED", S(status.Body.GetProperty("recent").EnumerateArray().First(), "status"));

        // The database refuses a simulator event labelled REAL.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db =>
        {
            db.MonitoringEvents.Add(new MonitoringEvent
            {
                FactoryId = Guid.Parse(s.Factory), EventType = "PERSON_DETECTED", Category = "VISION", SourceType = "REAL", Producer = "SIMULATOR",
                IdempotencyKey = K(), CameraId = Guid.Parse(cam), Summary = "x", RecordedByEmail = "x", OccurredAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            });
            return db.SaveChangesAsync();
        }));
        Assert.Contains("CK_monitoring_events_producer_source", ex.ToString());
    }

    // ─── Reviews ───────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Event_reviews_are_recorded_beside_the_unchanged_event()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, line: s.Line1);
        var ingest = await Ingest(s.Devices, new { eventType = "UNCERTAIN_DETECTION", factoryId = s.Factory, idempotencyKey = K(), cameraId = cam, confidence = 0.3 });
        var id = S(ingest.Body, "event", "id")!;
        var summary = S(ingest.Body, "event", "summary");
        string Url() => $"/api/monitoring/events/{id}/reviews";

        Is(HttpStatusCode.BadRequest, await Post(Url(), s.Monitor.Token, new { decision = "FALSE_POSITIVE" }), "note_required");
        Is(HttpStatusCode.BadRequest, await Post(Url(), s.Monitor.Token, new { decision = "DELETE_EVENT", note = "x" }), "review_decision_invalid");
        Is(HttpStatusCode.Forbidden, await Post(Url(), s.Hr.Token, new { decision = "CONFIRMED" }));
        Is(HttpStatusCode.Forbidden, await Post(Url(), s.Devices.Token, new { decision = "CONFIRMED" }));
        var first = await Post(Url(), s.Monitor.Token, new { decision = "FALSE_POSITIVE", note = "Shadow of a rack" });
        Is(HttpStatusCode.OK, first);
        Assert.Single(first.Body.GetProperty("reviews").EnumerateArray());
        Is(HttpStatusCode.OK, await Post(Url(), s.Manager.Token, new { decision = "STILL_UNCERTAIN" }));

        var detail = await Get($"/api/monitoring/events/{id}", s.Monitor.Token);
        var reviews = detail.Body.GetProperty("reviews").EnumerateArray().ToList();
        Assert.Equal(new[] { "FALSE_POSITIVE", "STILL_UNCERTAIN" }, reviews.Select(r => S(r, "decision")));
        Assert.Equal(s.Monitor.Email, S(reviews[0], "reviewedByEmail"));
        Assert.Equal(summary, S(detail.Body, "event", "summary"));
        Assert.Equal("2", S(detail.Body, "event", "reviewCount"));
        Assert.Equal("STILL_UNCERTAIN", S(detail.Body, "event", "latestReview"));
        Assert.NotEmpty(detail.Body.GetProperty("alerts").EnumerateArray());
        Assert.Contains("monitoring_event.reviewed", await Rag.WithDb(Api, db => db.AuditLog.AsNoTracking().Where(x => x.EntityId == id).Select(x => x.Action).ToListAsync()));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE monitoring_event_reviews SET \"Decision\" = 'CONFIRMED' WHERE \"EventId\" = {Guid.Parse(id)}")));
        Assert.Contains("append-only", ex.ToString());
    }

    // ─── SignalR ───────────────────────────────────────────────────────────

    private sealed class Inbox
    {
        public ConcurrentQueue<JsonElement> Events { get; } = new();
        public ConcurrentQueue<JsonElement> Alerts { get; } = new();
        public ConcurrentQueue<JsonElement> Cameras { get; } = new();
        public bool HasEvent(string id) => Events.Any(e => S(e, "id") == id);
        public bool HasAlert(string id) => Alerts.Any(a => S(a, "alert", "id") == id);
    }

    private HubConnection Hub(string? token, Inbox? inbox = null)
    {
        var server = Api.Server;
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/monitoring"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                if (token is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Headers[TestClientIpStartupFilter.Header] = TestClientIpStartupFilter.NewAddress();
            })
            .Build();
        if (inbox is not null)
        {
            conn.On<JsonElement>("monitoringEvent", e => inbox.Events.Enqueue(e));
            conn.On<JsonElement>("monitoringAlert", a => inbox.Alerts.Enqueue(a));
            conn.On<JsonElement>("cameraStatus", c => inbox.Cameras.Enqueue(c));
        }
        return conn;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(100);
        }
    }

    [DockerFact]
    public async Task Hub_connections_are_authorized_and_receive_only_their_own_scope()
    {
        var a = await NewSite();
        var b = await NewSite();
        var camLine1 = await NewCamera(a, line: a.Line1);
        var camLine2 = await NewCamera(a, line: a.Line2);

        // Negotiation itself is authorized.
        await using (var anonymous = Hub(null)) await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
        await using (var hr = Hub(a.Hr.Token)) await Assert.ThrowsAnyAsync<Exception>(() => hr.StartAsync());
        await using (var worker = Hub(a.Worker.Token)) await Assert.ThrowsAnyAsync<Exception>(() => worker.StartAsync());

        Inbox opA = new(), opB = new(), sup = new(), it = new();
        await using var cOpA = Hub(a.Monitor.Token, opA);
        await using var cOpB = Hub(b.Monitor.Token, opB);
        await using var cSup = Hub(a.Supervisor.Token, sup);
        await using var cIt = Hub(a.Devices.Token, it);
        foreach (var c in new[] { cOpA, cOpB, cSup, cIt }) await c.StartAsync();

        var subA = await cOpA.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);
        Assert.Equal(a.Factory, subA.GetProperty("factories")[0].GetString());
        Assert.Equal(new[] { "VISION", "SYSTEM" }, subA.GetProperty("categories").EnumerateArray().Select(x => x.GetString()));
        var refused = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => cOpA.InvokeAsync<JsonElement>("Subscribe", (Guid?)Guid.Parse(b.Factory)));
        Assert.Contains("factory_out_of_scope", refused.Message);
        await cOpB.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);
        var subSup = await cSup.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);
        Assert.Equal(new[] { a.Line1 }, subSup.GetProperty("lines").EnumerateArray().Select(x => x.GetString()));
        var subIt = await cIt.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);
        Assert.Equal(new[] { "SYSTEM" }, subIt.GetProperty("categories").EnumerateArray().Select(x => x.GetString()));

        // A person seen on line 1: factory operators and the line-1 supervisor, nobody else.
        var person = S((await Ingest(a.Devices, Detected(a, camLine1))).Body, "event", "id")!;
        await Until(() => opA.HasEvent(person) && sup.HasEvent(person), "the line-1 event");
        Assert.Equal("REAL", S(opA.Events.First(e => S(e, "id") == person), "sourceType"));

        // A camera on line 2 goes offline: equipment people and factory operators; not the line-1 supervisor.
        var offline = await Ingest(a.Devices, CameraStatus(a, camLine2, "OFFLINE"));
        var statusEvent = S(offline.Body, "event", "id")!;
        var alertId = S(offline.Body, "alert", "id")!;
        await Until(() => it.HasEvent(statusEvent) && it.HasAlert(alertId) && opA.HasAlert(alertId)
                          && it.Cameras.Any(c => S(c, "id") == camLine2 && S(c, "status") == "OFFLINE"), "the camera-offline updates");
        Assert.Equal("created", S(it.Alerts.First(x => S(x, "alert", "id") == alertId), "change"));
        // The pushed alert carries no permissions of its own; each client asks for its own.
        Assert.Equal("False", S(it.Alerts.First(x => S(x, "alert", "id") == alertId), "alert", "can", "acknowledge"), ignoreCase: true);

        // Lifecycle changes are pushed too.
        Is(HttpStatusCode.OK, await Post($"/api/monitoring/alerts/{alertId}/acknowledge", a.Monitor.Token, new { }));
        await Until(() => opA.Alerts.Any(x => S(x, "alert", "id") == alertId && S(x, "change") == "acknowledged"), "the acknowledgement");

        await Task.Delay(1500);
        Assert.False(opB.HasEvent(person) || opB.HasEvent(statusEvent) || opB.HasAlert(alertId), "another factory's operator received factory A's updates");
        Assert.False(it.HasEvent(person), "the IT administrator received a people-tracking event");
        Assert.False(sup.HasEvent(statusEvent) || sup.HasAlert(alertId), "the line-1 supervisor received a line-2 update");

        // Unsubscribing stops delivery; closing the connection forgets it.
        await cOpA.InvokeAsync("Unsubscribe");
        var later = S((await Ingest(a.Devices, Detected(a, camLine1))).Body, "event", "id")!;
        await Until(() => sup.HasEvent(later), "the later event");
        await Task.Delay(500);
        Assert.False(opA.HasEvent(later));
        var connections = Api.Services.GetRequiredService<MonitoringConnections>();
        var supId = cSup.ConnectionId!;
        Assert.True(connections.IsSubscribed(supId));
        await cSup.StopAsync();
        await Until(() => !connections.IsSubscribed(supId), "the closed connection to be forgotten");
    }
}
