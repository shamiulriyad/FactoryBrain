using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Cameras, coverage, sources, zones and devices end to end, against real
/// PostgreSQL, through the HTTP API: registration, factory isolation,
/// relationship checks, polygon validation, credential handling, heartbeats,
/// offline detection, simulated vs real devices, RBAC and history preservation.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbCameraDeviceTests
{
    private readonly PostgresStack _stack;
    public DbCameraDeviceTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    // ─── HTTP plumbing ─────────────────────────────────────────────────────

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw, byte[] Bytes)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public string Id => Body.GetProperty("id").GetString()!;
        public string Str(params string[] path)
        {
            var e = Body;
            foreach (var p in path) e = e.GetProperty(p);
            return e.ValueKind == JsonValueKind.Null ? "" : e.ToString();
        }
    }

    /// <summary>The API omits null properties (WhenWritingNull), so absent and null both mean "no value".</summary>
    private static bool IsNullOrAbsent(JsonElement e, string name) => !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

    private async Task<Reply> Send(HttpMethod method, string path, string? bearer, HttpContent? content = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var res = await client.SendAsync(req);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var raw = Encoding.UTF8.GetString(bytes);
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[')) ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw, bytes);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string bearer, object body) => Send(HttpMethod.Post, path, bearer, Rag.Json(body));
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, Rag.Json(body));
    private Task<Reply> Patch(string path, string bearer, object body) => Send(HttpMethod.Patch, path, bearer, Rag.Json(body));
    private Task<Reply> Delete(string path, string bearer) => Send(HttpMethod.Delete, path, bearer);

    private Task<Reply> Upload(string path, string bearer, string field, byte[] content, string fileName, string contentType, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, field, fileName);
        foreach (var (n, v) in fields) form.Add(new StringContent(v), n);
        return Send(HttpMethod.Put, path, bearer, form);
    }

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static IEnumerable<JsonElement> Items(Reply r) => r.Body.GetProperty("items").EnumerateArray();

    // ─── People and places ─────────────────────────────────────────────────

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, string? factory, params string[] lines)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@cameras.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        if (factory is not null) user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = Guid.Parse(factory) });
        foreach (var line in lines) user.Lines.Add(new UserProductionLineAssignment { UserId = id, LineId = line });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, email, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    public sealed record Site(
        string Factory, string Floor, string Floor2, string Sewing, string Line1, string Line2, string T25, string T26, string T40Floor2, string L2Ws,
        Person Super, Person Admin, Person Devices, Person Manager, Person Supervisor, Person Monitor, Person Hr, Person Worker);

    private async Task<Site> NewSite()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var f = await Post("/api/factories", super.Token, new { code = $"C{Tag()[..6]}", name = $"Camera factory {Tag()}" });
        Is(HttpStatusCode.Created, f);
        var admin = await NewPerson(R.FactoryAdmin, f.Id);
        var dept = await Post($"/api/factories/{f.Id}/departments", admin.Token, new { code = "SEW", name = "Sewing" });
        var floor = await Post($"/api/factories/{f.Id}/floors", admin.Token, new { code = "F1", name = "First floor" });
        var floor2 = await Post($"/api/factories/{f.Id}/floors", admin.Token, new { code = "F2", name = "Second floor" });
        Is(HttpStatusCode.Created, floor);
        string l1 = $"cl-{Tag()}", l2 = $"cl-{Tag()}";
        await Rag.WithDb(Api, db =>
        {
            db.ProductionLines.Add(new ProductionLine { Id = l1, Name = $"Line 1 {l1}", Workstations = 40, FactoryId = Guid.Parse(f.Id) });
            db.ProductionLines.Add(new ProductionLine { Id = l2, Name = $"Line 2 {l2}", Workstations = 40, FactoryId = Guid.Parse(f.Id) });
            return db.SaveChangesAsync();
        });
        async Task<string> Ws(string line, string code, string fl)
        {
            var w = await Post("/api/workstations", admin.Token, new { factoryId = f.Id, lineId = line, departmentId = dept.Id, code, floorId = fl });
            Is(HttpStatusCode.Created, w);
            return w.Id;
        }
        return new Site(f.Id, floor.Id, floor2.Id, dept.Id, l1, l2,
            await Ws(l1, "T-025", floor.Id), await Ws(l1, "T-026", floor.Id), await Ws(l1, "T-040", floor2.Id), await Ws(l2, "T-101", floor.Id),
            super, admin, await NewPerson(R.DeviceAdmin, f.Id), await NewPerson(R.FactoryManager, f.Id), await NewPerson(R.ProductionSupervisor, f.Id, l1),
            await NewPerson(R.MonitoringOperator, f.Id), await NewPerson(R.HrOfficer, f.Id), await NewPerson(R.Worker, f.Id));
    }

    private async Task<Reply> NewCamera(Site s, string? code = null, string? line = null, string[]? ws = null, string health = "None", string type = "IpCamera")
    {
        var r = await Post("/api/cameras", s.Devices.Token, new
        {
            factoryId = s.Factory, code, name = $"Camera {Tag()}", cameraType = type, floorId = s.Floor, departmentId = s.Sewing,
            lineId = line, location = "Ceiling above the line", workstationIds = ws ?? Array.Empty<string>(), healthDevice = health,
        });
        Is(HttpStatusCode.Created, r);
        return r;
    }

    private async Task Frame(Site s, string camera) =>
        Is(HttpStatusCode.OK, await Upload($"/api/cameras/{camera}/reference-frame", s.Devices.Token, "image", TestImages.Png(64, 36), "frame.png", "image/png", ("source", "Upload")));

    private static object Square(double x = 0.1, double y = 0.1, double w = 0.2, double h = 0.3) =>
        new[] { new { x, y }, new { x = x + w, y }, new { x = x + w, y = y + h }, new { x, y = y + h } };

    private async Task<Reply> NewDevice(Site s, bool simulated, string category = "Esp32Gateway", int interval = 30, int offlineAfter = 90, string? line = null)
    {
        var r = await Post("/api/devices", s.Devices.Token, new
        {
            factoryId = s.Factory, code = $"DEV-{Tag()}", name = "Gateway", category, isSimulated = simulated, lineId = line,
            heartbeatIntervalSeconds = interval, offlineAfterSeconds = offlineAfter, metadata = new Dictionary<string, string> { ["mac"] = "AA:BB:CC:00:11:22" },
        });
        Is(HttpStatusCode.Created, r);
        return r;
    }

    // ─── Cameras ───────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Cameras_are_registered_with_coverage_an_automatic_id_and_an_audit_entry()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, line: s.Line1, ws: new[] { s.T25, s.T26 }, health: "Simulated");
        Assert.Equal("CAM-001", cam.Str("code"));
        Assert.Equal(2, cam.Body.GetProperty("coverage").GetArrayLength());
        Assert.Equal("NotConnected", cam.Str("ai", "status"));
        Assert.Equal("Unknown", cam.Str("health", "health"));
        Assert.False(cam.Body.GetProperty("health").GetProperty("physicallyConnected").GetBoolean());
        Assert.True(IsNullOrAbsent(cam.Body, "source"));
        Assert.Equal("CAM-002", (await NewCamera(s)).Str("code"));
        Is(HttpStatusCode.Conflict, await Post("/api/cameras", s.Devices.Token, new { factoryId = s.Factory, code = "cam-001", name = "Dup", cameraType = "Webcam", floorId = s.Floor }), "camera_code_taken");

        // Directory: search, filters, pagination.
        var list = await Get($"/api/cameras?factoryId={s.Factory}&workstationId={s.T25}", s.Manager.Token);
        Is(HttpStatusCode.OK, list);
        Assert.Equal(1, list.Body.GetProperty("total").GetInt32());
        Assert.Equal(2, (await Get($"/api/cameras?factoryId={s.Factory}&pageSize=1", s.Monitor.Token)).Body.GetProperty("total").GetInt32());
        Assert.Equal(1, (await Get($"/api/cameras?factoryId={s.Factory}&health=Unknown", s.Monitor.Token)).Body.GetProperty("total").GetInt32());
        Assert.Equal(1, (await Get($"/api/cameras?factoryId={s.Factory}&health=None", s.Monitor.Token)).Body.GetProperty("total").GetInt32());

        // Status changes need a reason (except back to Active) and are audited.
        Is(HttpStatusCode.BadRequest, await Patch($"/api/cameras/{cam.Id}/status", s.Devices.Token, new { status = "Inactive" }));
        var off = await Patch($"/api/cameras/{cam.Id}/status", s.Devices.Token, new { status = "Maintenance", reason = "Lens cleaning" });
        Is(HttpStatusCode.OK, off);
        Assert.Equal("Maintenance", off.Str("status"));
        var audit = (await Get($"/api/audit?entityType=camera&factoryId={s.Factory}", s.Admin.Token)).Raw;
        Assert.Contains("camera.created", audit);
        Assert.Contains("camera.status_changed", audit);
    }

    [DockerFact]
    public async Task Relationships_are_validated_on_the_server()
    {
        var s = await NewSite();
        var other = await NewSite();
        Is(HttpStatusCode.BadRequest, await Post("/api/cameras", s.Devices.Token, new { factoryId = s.Factory, name = "X", cameraType = "IpCamera", floorId = other.Floor }), "floor_not_in_factory");
        Is(HttpStatusCode.BadRequest, await Post("/api/cameras", s.Devices.Token, new { factoryId = s.Factory, name = "X", cameraType = "IpCamera", floorId = s.Floor, lineId = other.Line1 }), "line_not_in_factory");
        Is(HttpStatusCode.BadRequest, await Post("/api/cameras", s.Devices.Token, new { factoryId = s.Factory, name = "X", cameraType = "IpCamera", floorId = s.Floor, departmentId = other.Sewing }), "department_not_in_factory");
        Is(HttpStatusCode.BadRequest, await Post("/api/cameras", s.Devices.Token, new { factoryId = s.Factory, name = "X", cameraType = "IpCamera", floorId = s.Floor, workstationIds = new[] { other.T25 } }), "workstation_not_in_factory");
        Is(HttpStatusCode.BadRequest, await Post("/api/cameras", s.Devices.Token, new { factoryId = s.Factory, name = "X", cameraType = "IpCamera", floorId = s.Floor, workstationIds = new[] { s.T40Floor2 } }), "workstation_on_other_floor");

        var cam = await NewCamera(s, ws: new[] { s.T25 });
        Is(HttpStatusCode.BadRequest, await Put($"/api/cameras/{cam.Id}/coverage", s.Devices.Token, new { workstationIds = new[] { s.T25, other.T26 } }), "workstation_not_in_factory");
        var covered = await Put($"/api/cameras/{cam.Id}/coverage", s.Devices.Token, new { lineIds = new[] { s.Line2 }, workstationIds = new[] { s.T25, s.T26 } });
        Is(HttpStatusCode.OK, covered);
        Assert.Equal(3, covered.Body.GetProperty("coverage").GetArrayLength());
        // Moving the camera to a floor its workstations aren't on is refused.
        Is(HttpStatusCode.Conflict, await Put($"/api/cameras/{cam.Id}", s.Devices.Token, new { name = "Moved", cameraType = "IpCamera", floorId = s.Floor2 }), "coverage_on_other_floor");

        // Zones must point inside the coverage.
        await Frame(s, cam.Id);
        var cam2 = await NewCamera(s);
        await Frame(s, cam2.Id);
        Is(HttpStatusCode.BadRequest, await Post($"/api/cameras/{cam2.Id}/zones", s.Devices.Token, new { name = "T-025", zoneType = "Workstation", workstationId = s.T25, points = Square() }), "workstation_not_covered");
        Is(HttpStatusCode.BadRequest, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "Other", zoneType = "Workstation", workstationId = other.T25, points = Square() }), "workstation_not_in_factory");
        Is(HttpStatusCode.BadRequest, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "No ws", zoneType = "Workstation", points = Square() }), "workstation_required");
        Is(HttpStatusCode.Created, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "T-101 via line", zoneType = "Workstation", workstationId = s.L2Ws, points = Square() }));

        // Devices: a workstation on another line, a camera link on a non-camera device.
        Is(HttpStatusCode.BadRequest, await Post("/api/devices", s.Devices.Token, new { factoryId = s.Factory, code = "S-1", name = "S", category = "MachineSensor", isSimulated = false, lineId = s.Line2, workstationId = s.T25 }), "workstation_not_on_line");
        Is(HttpStatusCode.BadRequest, await Post("/api/devices", s.Devices.Token, new { factoryId = s.Factory, code = "S-2", name = "S", category = "MachineSensor", isSimulated = false, cameraId = cam.Id }), "camera_link_requires_camera_category");
        Is(HttpStatusCode.BadRequest, await Post("/api/devices", s.Devices.Token, new { factoryId = s.Factory, code = "S-3", name = "S", category = "MachineSensor", isSimulated = false, workstationId = other.T25 }), "workstation_not_in_factory");
        var linked = await Post("/api/devices", s.Devices.Token, new { factoryId = s.Factory, code = "CAMHB-1", name = "Health", category = "Camera", isSimulated = true, cameraId = cam.Id });
        Is(HttpStatusCode.Created, linked);
        Is(HttpStatusCode.Conflict, await Post("/api/devices", s.Devices.Token, new { factoryId = s.Factory, code = "CAMHB-2", name = "Health", category = "Camera", isSimulated = true, cameraId = cam.Id }), "camera_has_device");
    }

    [DockerFact]
    public async Task Each_factory_sees_and_changes_only_its_own_cameras_and_devices()
    {
        var a = await NewSite();
        var b = await NewSite();
        var camA = await NewCamera(a, line: a.Line1);
        var devA = await NewDevice(a, simulated: false);
        await NewCamera(b);

        Is(HttpStatusCode.NotFound, await Get($"/api/cameras/{camA.Id}", b.Devices.Token), "camera_not_found");
        Is(HttpStatusCode.NotFound, await Put($"/api/cameras/{camA.Id}", b.Devices.Token, new { name = "Hijack", cameraType = "IpCamera", floorId = b.Floor }), "camera_not_found");
        Is(HttpStatusCode.NotFound, await Get($"/api/devices/{devA.Id}", b.Devices.Token), "device_not_found");
        Is(HttpStatusCode.NotFound, await Post($"/api/devices/{devA.Id}/heartbeat", b.Devices.Token, new { }), "device_not_found");
        Is(HttpStatusCode.Forbidden, await Post("/api/cameras", b.Devices.Token, new { factoryId = a.Factory, name = "X", cameraType = "Webcam", floorId = a.Floor }), "factory_out_of_scope");
        Is(HttpStatusCode.Forbidden, await Post("/api/devices", b.Devices.Token, new { factoryId = a.Factory, code = "X-1", name = "X", category = "Other", isSimulated = true }), "factory_out_of_scope");
        Assert.All(Items(await Get("/api/cameras", b.Devices.Token)), c => Assert.Equal(b.Factory, c.GetProperty("factoryId").GetString()));
        Assert.All(Items(await Get("/api/devices", b.Devices.Token)), d => Assert.Equal(b.Factory, d.GetProperty("factoryId").GetString()));
        Assert.Equal(0, (await Get($"/api/devices/health?factoryId={a.Factory}", b.Devices.Token)).Body.GetProperty("total").GetInt32());

        // A supervisor sees only cameras covering their line, and changes nothing.
        var onLine2 = await NewCamera(a, line: a.Line2);
        var sup = Items(await Get("/api/cameras", a.Supervisor.Token)).Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Contains(camA.Id, sup);
        Assert.DoesNotContain(onLine2.Id, sup);
        Is(HttpStatusCode.NotFound, await Get($"/api/cameras/{onLine2.Id}", a.Supervisor.Token));
        var covering = await NewCamera(a, ws: new[] { a.T26 });
        Assert.Contains(covering.Id, Items(await Get("/api/cameras", a.Supervisor.Token)).Select(c => c.GetProperty("id").GetString()));
    }

    [DockerFact]
    public async Task Zone_polygons_are_validated_versioned_and_their_history_kept()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, line: s.Line1, ws: new[] { s.T25 });
        Is(HttpStatusCode.BadRequest, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "Early", zoneType = "Walkway", points = Square() }), "reference_frame_required");
        await Frame(s, cam.Id);

        var outOfRange = new[] { new { x = 0.1, y = 0.1 }, new { x = 1.4, y = 0.1 }, new { x = 0.5, y = 0.6 } };
        Is(HttpStatusCode.BadRequest, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "Bad", zoneType = "Walkway", points = outOfRange }), "zone_point_out_of_range");
        var bowTie = new[] { new { x = 0.1, y = 0.1 }, new { x = 0.5, y = 0.5 }, new { x = 0.5, y = 0.1 }, new { x = 0.1, y = 0.5 } };
        Is(HttpStatusCode.BadRequest, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "Bad", zoneType = "Walkway", points = bowTie }), "zone_self_intersecting");
        Is(HttpStatusCode.BadRequest, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "Bad", zoneType = "Walkway", points = outOfRange.Take(2) }), "zone_point_count");

        var zone = await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "T-025 workstation", zoneType = "Workstation", workstationId = s.T25, points = Square(), color = "#22aa88" });
        Is(HttpStatusCode.Created, zone);
        Assert.Equal((1, s.Line1, "#22AA88"), (zone.Body.GetProperty("version").GetInt32(), zone.Str("line", "id"), zone.Str("color")));
        Is(HttpStatusCode.Created, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "Restricted", zoneType = "Restricted", points = Square(0.6, 0.6, 0.3, 0.3) }));
        Is(HttpStatusCode.Conflict, await Post($"/api/cameras/{cam.Id}/zones", s.Devices.Token, new { name = "restricted", zoneType = "Restricted", points = Square(0.6, 0.1) }), "zone_name_taken");

        var edit = new { name = "T-025 workstation", zoneType = "Workstation", workstationId = s.T25, lineId = (string?)null, points = Square(0.12, 0.1, 0.25, 0.3), color = "#22AA88", notes = "Moved after re-aim", isActive = true, expectedVersion = 1 };
        var v2 = await Put($"/api/camera-zones/{zone.Id}", s.Devices.Token, edit);
        Is(HttpStatusCode.OK, v2);
        Assert.Equal(2, v2.Body.GetProperty("version").GetInt32());
        Is(HttpStatusCode.Conflict, await Put($"/api/camera-zones/{zone.Id}", s.Devices.Token, edit with { notes = "stale" }), "zone_version_conflict");
        var off = await Put($"/api/camera-zones/{zone.Id}", s.Devices.Token, edit with { isActive = false, expectedVersion = 2 });
        Assert.False(off.Body.GetProperty("isActive").GetBoolean());

        var history = (await Get($"/api/camera-zones/{zone.Id}/history", s.Monitor.Token)).Body.EnumerateArray().ToList();
        Assert.Equal(new[] { "Deactivated", "Updated", "Created" }, history.Select(h => h.GetProperty("action").GetString()));
        Assert.Equal(0.1, history[2].GetProperty("points")[0].GetProperty("x").GetDouble());
        Assert.True(history[2].GetProperty("drawnOnCurrentFrame").GetBoolean());
        Assert.Single((await Get($"/api/cameras/{cam.Id}/zones", s.Monitor.Token)).Body.EnumerateArray());
        Assert.Equal(2, (await Get($"/api/cameras/{cam.Id}/zones?includeInactive=true", s.Monitor.Token)).Body.GetArrayLength());

        // Revisions are append-only in the database itself.
        var zid = Guid.Parse(zone.Id);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE camera_zone_revisions SET \"Name\" = 'x' WHERE \"ZoneId\" = {zid}")));
        Assert.Contains("append-only", ex.ToString());
    }

    [DockerFact]
    public async Task Camera_credentials_are_encrypted_and_never_returned()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, type: "RtspCamera");
        const string password = "Pa55-word-9f3c";
        var set = await Put($"/api/cameras/{cam.Id}/source", s.Devices.Token, new { sourceType = "Rtsp", streamUrl = $"rtsp://camadmin:{password}@10.20.30.40:554/Streaming/101" });
        Is(HttpStatusCode.OK, set);
        Assert.Equal("rtsp://10.20.30.40:554/Streaming/101", set.Str("source", "streamUrl"));
        Assert.True(set.Body.GetProperty("source").GetProperty("hasCredentials").GetBoolean());
        Assert.Equal("NotChecked", set.Str("source", "connectionCheck"));
        foreach (var r in new[] { set, await Get($"/api/cameras/{cam.Id}", s.Monitor.Token), await Get($"/api/cameras/{cam.Id}/sources", s.Devices.Token),
                     await Get("/api/cameras", s.Devices.Token), await Get($"/api/audit?entityType=camera&factoryId={s.Factory}", s.Admin.Token) })
        {
            Assert.DoesNotContain(password, r.Raw);
            Assert.DoesNotContain("camadmin", r.Raw);
        }

        // Stored only as ciphertext.
        var blob = await Rag.WithDb(Api, db => db.CameraSources.Where(x => x.CameraId == Guid.Parse(cam.Id) && x.RetiredAt == null).Select(x => x.CredentialsCiphertext).FirstAsync());
        Assert.NotNull(blob);
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(blob!));

        // A new version can keep them (re-encrypted for the new row); the old version is retired, not edited.
        var keep = await Put($"/api/cameras/{cam.Id}/source", s.Devices.Token, new { sourceType = "Rtsp", streamUrl = "rtsp://10.20.30.40:554/Streaming/102", keepCredentials = true });
        Assert.True(keep.Body.GetProperty("source").GetProperty("hasCredentials").GetBoolean());
        var versions = (await Get($"/api/cameras/{cam.Id}/sources", s.Devices.Token)).Body.EnumerateArray().ToList();
        Assert.Equal(new[] { 2, 1 }, versions.Select(v => v.GetProperty("version").GetInt32()));
        Assert.True(IsNullOrAbsent(versions[0], "retiredAt"));
        Assert.False(IsNullOrAbsent(versions[1], "retiredAt"));

        Is(HttpStatusCode.BadRequest, await Put($"/api/cameras/{cam.Id}/source", s.Devices.Token, new { sourceType = "Rtsp", streamUrl = "rtsp://10.1.1.1/live?password=x" }), "stream_url_secret_in_query");
        Is(HttpStatusCode.BadRequest, await Put($"/api/cameras/{cam.Id}/source", s.Devices.Token, new { sourceType = "Rtsp", streamUrl = "rtsp://127.0.0.1/live" }), "stream_url_host_not_allowed");
        Is(HttpStatusCode.BadRequest, await Put($"/api/cameras/{cam.Id}/source", s.Devices.Token, new { sourceType = "HttpStream", streamUrl = "http://169.254.169.254/latest" }), "stream_url_host_not_allowed");
        // The server never connected anywhere.
        Assert.DoesNotContain(Api.Http.Blocked.Concat(Api.Http.Handled), c => c.Uri.Host == "10.20.30.40");
    }

    [DockerFact]
    public async Task Recorded_video_and_reference_frames_are_stored_checked_and_served()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, type: "RecordedVideo");
        var mp4 = new byte[] { 0, 0, 0, 0x20 }.Concat("ftypisom"u8.ToArray()).Concat(new byte[4096]).ToArray();
        Is(HttpStatusCode.BadRequest, await Put($"/api/cameras/{cam.Id}/source", s.Devices.Token, new { sourceType = "RecordedVideo" }), "video_required");
        var up = await Upload($"/api/cameras/{cam.Id}/source/video", s.Devices.Token, "video", mp4, "../../line 1 demo.mp4", "application/octet-stream");
        Is(HttpStatusCode.OK, up);
        Assert.Equal(("RecordedVideo", "line 1 demo.mp4", "video/mp4"), (up.Str("source", "sourceType"), up.Str("source", "videoFileName"), up.Str("source", "videoContentType")));
        var video = await Get($"/api/cameras/{cam.Id}/source/video", s.Monitor.Token);
        Is(HttpStatusCode.OK, video);
        Assert.Equal(mp4, video.Bytes);
        Is(HttpStatusCode.BadRequest, await Upload($"/api/cameras/{cam.Id}/source/video", s.Devices.Token, "video", TestImages.Png(8, 8), "fake.mp4", "video/mp4"), "video_unsupported_format");
        var big = new byte[] { 0, 0, 0, 0x20 }.Concat("ftypisom"u8.ToArray()).Concat(new byte[1_200_000]).ToArray(); // the test host allows 1 MB
        Is(HttpStatusCode.BadRequest, await Upload($"/api/cameras/{cam.Id}/source/video", s.Devices.Token, "video", big, "big.mp4", "video/mp4"), "video_too_large");

        var frame = await Upload($"/api/cameras/{cam.Id}/reference-frame", s.Devices.Token, "image", TestImages.Png(320, 180), "f.png", "image/png", ("source", "VideoFrame"), ("frameSeconds", "12.5"));
        Is(HttpStatusCode.OK, frame);
        Assert.Equal(("VideoFrame", "320", "12.5"), (frame.Str("referenceFrame", "source"), frame.Str("referenceFrame", "width"), frame.Str("referenceFrame", "frameSeconds")));
        Is(HttpStatusCode.OK, await Get($"/api/cameras/{cam.Id}/reference-frame", s.Monitor.Token));
        Is(HttpStatusCode.BadRequest, await Upload($"/api/cameras/{cam.Id}/reference-frame", s.Devices.Token, "image", mp4, "x.png", "image/png"));
    }

    // ─── Devices ───────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Real_device_heartbeats_bring_it_online_once_per_message_id()
    {
        var s = await NewSite();
        var dev = await NewDevice(s, simulated: false);
        Assert.Equal(("Unknown", "Real", "Never connected"), (dev.Str("healthStatus"), dev.Str("mode"), dev.Str("healthLabel")));
        var hb = await Post($"/api/devices/{dev.Id}/heartbeat", s.Devices.Token, new { messageId = "m-1", firmwareVersion = "1.4.2", metrics = new { rssi = -61.0 } });
        Is(HttpStatusCode.OK, hb);
        Assert.Equal("Accepted", hb.Str("outcome"));
        Assert.Equal(("Online", "True"), (hb.Str("device", "healthStatus"), hb.Str("device", "physicallyConnected")));
        Assert.Contains(hb.Body.GetProperty("events").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "ConnectionOnline");
        Assert.Equal("1.4.2", hb.Str("device", "firmwareVersion"));

        var again = await Post($"/api/devices/{dev.Id}/heartbeat", s.Devices.Token, new { messageId = "m-1" });
        Assert.Equal("Duplicate", again.Str("outcome"));
        var beats = await Get($"/api/devices/{dev.Id}/heartbeats", s.Devices.Token);
        Assert.Equal(1, beats.Body.GetProperty("total").GetInt32());
        Assert.False(beats.Body.GetProperty("items")[0].GetProperty("isSimulated").GetBoolean());

        Is(HttpStatusCode.BadRequest, await Post($"/api/devices/{dev.Id}/heartbeat", s.Devices.Token, new { occurredAt = DateTimeOffset.UtcNow.AddHours(1) }), "occurred_in_future");
        Is(HttpStatusCode.BadRequest, await Post($"/api/devices/{dev.Id}/heartbeat", s.Devices.Token, new { occurredAt = DateTimeOffset.UtcNow.AddDays(-8) }), "occurred_too_old");

        // The gateway form of the contract finds the device by factory + code.
        var code = dev.Str("code");
        var err = await Post("/api/devices/ingest", s.Devices.Token, new { factoryId = s.Factory, deviceCode = code.ToLowerInvariant(), kind = "Error", code = "MODBUS_TIMEOUT", message = "slave 3" });
        Is(HttpStatusCode.OK, err);
        Assert.Equal("MODBUS_TIMEOUT", err.Str("device", "lastErrorCode"));
        var health = await Get($"/api/devices/health?factoryId={s.Factory}", s.Manager.Token);
        Assert.Equal(1, health.Body.GetProperty("physicallyConnected").GetInt32());
        Assert.Equal(1, health.Body.GetProperty("errorsLast24Hours").GetInt32());
    }

    [DockerFact]
    public async Task Devices_whose_heartbeats_stop_are_reported_and_marked_offline()
    {
        var s = await NewSite();
        var dev = await NewDevice(s, simulated: false, interval: 10, offlineAfter: 30);
        Is(HttpStatusCode.OK, await Post($"/api/devices/{dev.Id}/heartbeat", s.Devices.Token, new { }));
        // A buffered heartbeat older than the offline window is stored but brings nothing online.
        var late = await NewDevice(s, simulated: false, interval: 10, offlineAfter: 30);
        var old = await Post($"/api/devices/{late.Id}/heartbeat", s.Devices.Token, new { occurredAt = DateTimeOffset.UtcNow.AddMinutes(-10) });
        Assert.Equal("Unknown", old.Str("device", "healthStatus"));

        // Pretend the last heartbeat was 31 s ago.
        var id = Guid.Parse(dev.Id);
        await Rag.WithDb(Api, db => db.Devices.Where(d => d.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow.AddSeconds(-31))
            .SetProperty(d => d.OfflineDeadline, DateTimeOffset.UtcNow.AddSeconds(-1))));
        var stale = await Get($"/api/devices/{dev.Id}", s.Devices.Token);
        Assert.Equal(("Offline", "True", "False"), (stale.Str("healthStatus"), stale.Str("heartbeatOverdue"), stale.Str("physicallyConnected")));
        Assert.Equal(1, (await Get($"/api/devices?factoryId={s.Factory}&health=Offline", s.Devices.Token)).Body.GetProperty("total").GetInt32());

        var check = await Post("/api/devices/health/check", s.Devices.Token, new { });
        Is(HttpStatusCode.OK, check);
        Assert.Equal(1, check.Body.GetProperty("markedOffline").GetInt32());
        var events = (await Get($"/api/devices/{dev.Id}/events?type=HeartbeatMissed", s.Devices.Token)).Body.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(events);
        Assert.Equal("HeartbeatMissed", events[0].GetProperty("eventType").GetString());
        Assert.Equal("System", events[0].GetProperty("source").GetString());
        Assert.Equal(0, (await Post("/api/devices/health/check", s.Devices.Token, new { })).Body.GetProperty("markedOffline").GetInt32());

        // A real device saying it goes offline, then coming back.
        var back = await Post($"/api/devices/{dev.Id}/heartbeat", s.Devices.Token, new { });
        Assert.Equal("Online", back.Str("device", "connectionStatus"));
        var bye = await Post($"/api/devices/{dev.Id}/events", s.Devices.Token, new { kind = "Offline", message = "Power off for maintenance" });
        Assert.Equal("Offline", bye.Str("device", "connectionStatus"));
        // Disabled devices refuse messages; maintenance shows as Maintenance.
        Is(HttpStatusCode.OK, await Patch($"/api/devices/{dev.Id}/status", s.Devices.Token, new { status = "Maintenance", reason = "Replacing antenna" }));
        Assert.Equal("Maintenance", (await Get($"/api/devices/{dev.Id}", s.Manager.Token)).Str("healthStatus"));
        Is(HttpStatusCode.OK, await Patch($"/api/devices/{dev.Id}/status", s.Devices.Token, new { status = "Disabled", reason = "Retired" }));
        Is(HttpStatusCode.Conflict, await Post($"/api/devices/{dev.Id}/heartbeat", s.Devices.Token, new { }), "device_disabled");
    }

    [DockerFact]
    public async Task Simulated_and_real_devices_never_mix()
    {
        var s = await NewSite();
        var sim = await NewDevice(s, simulated: true, category: "RfidReader");
        var real = await NewDevice(s, simulated: false);

        var online = await Post("/api/simulator/devices/online", s.Devices.Token, new { deviceId = sim.Id });
        Is(HttpStatusCode.OK, online);
        Assert.Equal(("Online", "False", "True"), (online.Str("device", "healthStatus"), online.Str("device", "physicallyConnected"), online.Str("isSimulated")));
        Assert.Contains("simulated", online.Str("device", "healthLabel"));
        Assert.All(online.Body.GetProperty("events").EnumerateArray(), e => Assert.Equal(("Simulator", true), (e.GetProperty("source").GetString(), e.GetProperty("isSimulated").GetBoolean())));

        var beats = await Post("/api/simulator/devices/heartbeat", s.Devices.Token, new { deviceId = sim.Id, count = 5, intervalSeconds = 10 });
        Assert.Equal(5, beats.Body.GetProperty("accepted").GetInt32());
        var error = await Post("/api/simulator/devices/error", s.Devices.Token, new { deviceId = sim.Id });
        Assert.Equal("RFID_READ_TIMEOUT", error.Str("device", "lastErrorCode"));
        Is(HttpStatusCode.OK, await Post("/api/simulator/devices/offline", s.Devices.Token, new { deviceId = sim.Id, reason = "Demo: cable pulled" }));

        Is(HttpStatusCode.Conflict, await Post("/api/simulator/devices/online", s.Devices.Token, new { deviceId = real.Id }), "device_not_simulated");
        Is(HttpStatusCode.Conflict, await Post("/api/simulator/devices/heartbeat", s.Devices.Token, new { deviceId = real.Id }), "device_not_simulated");
        Is(HttpStatusCode.Conflict, await Post($"/api/devices/{sim.Id}/heartbeat", s.Devices.Token, new { }), "device_is_simulated");

        var simEvents = await Get($"/api/simulator/devices/events?factoryId={s.Factory}", s.Devices.Token);
        Assert.All(Items(simEvents), e => Assert.True(e.GetProperty("isSimulated").GetBoolean()));
        var health = await Get($"/api/devices/health?factoryId={s.Factory}", s.Devices.Token);
        Assert.Equal((1, 1, 0), (health.Body.GetProperty("simulated").GetInt32(), health.Body.GetProperty("real").GetInt32(), health.Body.GetProperty("physicallyConnected").GetInt32()));

        // The database refuses a "device" message marked simulated, and a simulator message that isn't.
        var did = Guid.Parse(sim.Id);
        var fid = Guid.Parse(s.Factory);
        foreach (var (source, simulated) in new[] { ("Device", true), ("Simulator", false) })
        {
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Rag.WithDb(Api, db =>
            {
                db.DeviceHeartbeats.Add(new DeviceHeartbeat { DeviceId = did, FactoryId = fid, OccurredAt = DateTimeOffset.UtcNow, Source = source, IsSimulated = simulated, RecordedByEmail = "t" });
                return db.SaveChangesAsync();
            }));
            Assert.Equal("23514", Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        }
    }

    [DockerFact]
    public async Task Configuration_is_refused_without_the_permission()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, line: s.Line1);
        var dev = await NewDevice(s, simulated: true);
        var newCamera = new { factoryId = s.Factory, name = "X", cameraType = "Webcam", floorId = s.Floor };
        foreach (var who in new[] { s.Monitor, s.Manager, s.Supervisor })
        {
            Is(HttpStatusCode.OK, await Get($"/api/cameras/{cam.Id}", who.Token));
            Is(HttpStatusCode.Forbidden, await Post("/api/cameras", who.Token, newCamera), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Put($"/api/cameras/{cam.Id}/source", who.Token, new { sourceType = "LocalWebcam" }), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Post($"/api/cameras/{cam.Id}/zones", who.Token, new { name = "Z", zoneType = "Walkway", points = Square() }), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Post("/api/simulator/devices/online", who.Token, new { deviceId = dev.Id }), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Patch($"/api/devices/{dev.Id}/status", who.Token, new { status = "Disabled", reason = "x" }), "permission_denied");
        }
        foreach (var who in new[] { s.Hr, s.Worker })
        {
            Is(HttpStatusCode.Forbidden, await Get("/api/cameras", who.Token), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Get("/api/devices", who.Token), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Post("/api/devices", who.Token, new { factoryId = s.Factory, code = "H-1", name = "H", category = "Other", isSimulated = true }), "permission_denied");
        }
        // Readers of cameras still never see credentials, and the manager reads device health but not logs.
        Is(HttpStatusCode.OK, await Get($"/api/devices/health?factoryId={s.Factory}", s.Manager.Token));
        Is(HttpStatusCode.Forbidden, await Get($"/api/devices/{dev.Id}/events", s.Manager.Token), "permission_denied");
        Is(HttpStatusCode.OK, await Get($"/api/devices/{dev.Id}/events", s.Devices.Token));
    }

    [DockerFact]
    public async Task History_is_preserved_and_only_cameras_and_devices_without_it_can_be_deleted()
    {
        var s = await NewSite();
        var empty = await NewCamera(s, ws: new[] { s.T25 }, health: "Simulated");
        Assert.True((await Get($"/api/cameras/{empty.Id}", s.Devices.Token)).Body.GetProperty("can").GetProperty("delete").GetBoolean());
        Is(HttpStatusCode.NoContent, await Delete($"/api/cameras/{empty.Id}", s.Devices.Token));
        Is(HttpStatusCode.NotFound, await Get($"/api/cameras/{empty.Id}", s.Devices.Token));
        Assert.Contains("camera.deleted", (await Get($"/api/audit?entityType=camera&factoryId={s.Factory}", s.Admin.Token)).Raw);

        var zoned = await NewCamera(s);
        await Frame(s, zoned.Id);
        Is(HttpStatusCode.Created, await Post($"/api/cameras/{zoned.Id}/zones", s.Devices.Token, new { name = "Walkway", zoneType = "Walkway", points = Square() }));
        var blocked = await Delete($"/api/cameras/{zoned.Id}", s.Devices.Token);
        Is(HttpStatusCode.Conflict, blocked, "camera_has_history");

        var withHealth = await NewCamera(s, health: "Simulated");
        var healthDevice = (await Get($"/api/cameras/{withHealth.Id}", s.Devices.Token)).Str("health", "deviceId");
        Is(HttpStatusCode.OK, await Post("/api/simulator/devices/heartbeat", s.Devices.Token, new { deviceId = healthDevice }));
        Is(HttpStatusCode.Conflict, await Delete($"/api/cameras/{withHealth.Id}", s.Devices.Token), "camera_has_history");
        Is(HttpStatusCode.Conflict, await Delete($"/api/devices/{healthDevice}", s.Devices.Token), "device_has_history");
        var unused = await NewDevice(s, simulated: true);
        Is(HttpStatusCode.NoContent, await Delete($"/api/devices/{unused.Id}", s.Devices.Token));

        // Heartbeats and events can't be changed or removed, whatever the application does.
        var did = Guid.Parse(healthDevice);
        foreach (var sql in new[] { "UPDATE device_heartbeats SET \"Source\" = 'Simulator' WHERE \"DeviceId\" = {0}", "DELETE FROM device_events WHERE \"DeviceId\" = {0}",
                     "DELETE FROM device_heartbeats WHERE \"DeviceId\" = {0}" })
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db => db.Database.ExecuteSqlRawAsync(sql, did)));
            Assert.Contains("append-only", ex.ToString());
        }
        Assert.True(await Rag.WithDb(Api, db => db.DeviceEvents.CountAsync(e => e.DeviceId == did)) >= 1);
    }
}
