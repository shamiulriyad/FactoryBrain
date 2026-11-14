using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The computer-vision service against real PostgreSQL: it signs in with its
/// service key (not a person's login), reaches only its configured factory,
/// sends REAL, anonymous events, and alone can read camera configuration
/// (zones, recorded video, stream credentials).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbCvServiceTests
{
    private readonly PostgresStack _stack;
    public DbCvServiceTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw, byte[] Bytes, HttpResponseMessage Response)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public string Id => Body.GetProperty("id").GetString()!;
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

    private async Task<Reply> Send(HttpMethod method, string path, string? bearer, HttpContent? content = null, string? serviceKey = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (serviceKey is not null) req.Headers.Add(ServiceKeyDefaults.Header, serviceKey);
        var res = await client.SendAsync(req);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var raw = Encoding.UTF8.GetString(bytes);
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[')) ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw, bytes, res);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string bearer, object body) => Send(HttpMethod.Post, path, bearer, Rag.Json(body));
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, Rag.Json(body));
    private Task<Reply> Patch(string path, string bearer, object body) => Send(HttpMethod.Patch, path, bearer, Rag.Json(body));
    private Task<Reply> SvcGet(string path, string? key = null) => Send(HttpMethod.Get, path, null, null, key ?? Api.CvServiceKey);
    private Task<Reply> SvcPost(string path, object body, string? key = null) => Send(HttpMethod.Post, path, null, Rag.Json(body), key ?? Api.CvServiceKey);

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static string K() => $"cv-{Guid.NewGuid():N}";

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, string? factory)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@cv.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        if (factory is not null) user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = Guid.Parse(factory) });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, email, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    public sealed record Site(string Factory, string Floor, string Sewing, string Line, Person Admin, Person Devices, Person Monitor);

    /// <summary>A site in the factory with <paramref name="code"/> (created once; the service is configured for CVSVC1).</summary>
    private async Task<Site> NewSite(string? code = null)
    {
        var super = await NewPerson(R.SuperAdmin, null);
        code ??= $"X{Tag()[..6]}".ToUpperInvariant();
        var existing = (await Get("/api/factories", super.Token)).Body.EnumerateArray().FirstOrDefault(f => S(f, "code") == code);
        string fid;
        if (existing.ValueKind == JsonValueKind.Object) fid = S(existing, "id")!;
        else
        {
            var f = await Post("/api/factories", super.Token, new { code, name = $"CV factory {code}" });
            Is(HttpStatusCode.Created, f);
            fid = f.Id;
        }
        var admin = await NewPerson(R.FactoryAdmin, fid);
        var dept = await Post($"/api/factories/{fid}/departments", admin.Token, new { code = $"D{Tag()[..4]}", name = $"Sewing {Tag()}" });
        var floor = await Post($"/api/factories/{fid}/floors", admin.Token, new { code = $"F{Tag()[..4]}", name = $"Floor {Tag()}" });
        Is(HttpStatusCode.Created, floor);
        var line = $"cv-{Tag()}";
        await Rag.WithDb(Api, db =>
        {
            db.ProductionLines.Add(new ProductionLine { Id = line, Name = $"Line {line}", Workstations = 20, FactoryId = Guid.Parse(fid) });
            return db.SaveChangesAsync();
        });
        return new Site(fid, floor.Id, dept.Id, line, admin, await NewPerson(R.DeviceAdmin, fid), await NewPerson(R.MonitoringOperator, fid));
    }

    private async Task<string> NewCamera(Site s)
    {
        var r = await Post("/api/cameras", s.Devices.Token, new
        {
            factoryId = s.Factory, name = $"Camera {Tag()}", cameraType = "IpCamera", floorId = s.Floor, departmentId = s.Sewing, lineId = s.Line,
            workstationIds = Array.Empty<string>(), healthDevice = "None",
        });
        Is(HttpStatusCode.Created, r);
        return r.Id;
    }

    private static object Detected(Site s, string camera, string? key = null) => new
    {
        eventType = "PERSON_DETECTED", factoryId = s.Factory, idempotencyKey = key ?? K(), cameraId = camera, trackId = "s1a2-t1", confidence = 0.83,
        modelName = "yolo11n", modelVersion = "ultralytics-8.3.253", payload = new { bbox = new { x = 0.1, y = 0.2, w = 0.1, h = 0.3 }, frameIndex = 42 },
    };

    [DockerFact]
    public async Task The_cv_service_signs_in_with_its_key_and_sends_real_anonymous_events_for_its_factory_only()
    {
        var a = await NewSite(DbApiFactory.CvServiceFactory);
        var b = await NewSite();
        var camA = await NewCamera(a);
        var camB = await NewCamera(b);

        var key = K();
        var ok = await SvcPost("/api/monitoring/events", Detected(a, camA, key));
        Is(HttpStatusCode.Created, ok);
        var ev = ok.Body.GetProperty("event");
        Assert.Equal("CV_SERVICE", S(ev, "producer"));
        Assert.Equal("REAL", S(ev, "sourceType"));
        Assert.Equal("service:computer-vision", S(ev, "recordedByEmail"));
        Assert.Equal("yolo11n", S(ev, "modelName"));
        Assert.Equal("s1a2-t1", S(ev, "trackId"));
        Assert.Null(S(ev, "verifiedWorker"));
        var again = await SvcPost("/api/monitoring/events", Detected(a, camA, key));
        Is(HttpStatusCode.OK, again);
        Assert.Equal("Duplicate", S(again.Body, "outcome"));

        // Wrong key, other factory, simulated, equipment events, identities: all refused.
        Is(HttpStatusCode.Unauthorized, await SvcPost("/api/monitoring/events", Detected(a, camA), key: "wrong-wrong-wrong-wrong-wrong-wrong-wrong"));
        Is(HttpStatusCode.Forbidden, await SvcPost("/api/monitoring/events", Detected(b, camB)), "factory_out_of_scope");
        Is(HttpStatusCode.BadRequest, await SvcPost("/api/monitoring/events",
            new { eventType = "PERSON_DETECTED", factoryId = a.Factory, idempotencyKey = K(), cameraId = camA, sourceType = "SIMULATED" }), "source_type_not_allowed");
        Is(HttpStatusCode.BadRequest, await SvcPost("/api/monitoring/events",
            new { eventType = "DEVICE_STATUS_CHANGED", factoryId = a.Factory, idempotencyKey = K(), payload = new { toStatus = "ONLINE" } }), "event_type_not_allowed_for_service");
        Is(HttpStatusCode.BadRequest, await SvcPost("/api/monitoring/events",
            new { eventType = "ACTIVITY_CLASSIFIED", factoryId = a.Factory, idempotencyKey = K(), cameraId = camA, trackId = "t1", confidence = 0.9, payload = new { activity = "SEWING" } }),
            "event_type_not_allowed_for_service");
        Is(HttpStatusCode.BadRequest, await SvcPost("/api/monitoring/events",
            new { eventType = "PERSON_DETECTED", factoryId = a.Factory, idempotencyKey = K(), cameraId = camA, workerId = Guid.NewGuid(),
                  identity = new { method = "RFID_BADGE", cardUid = "04AABBCCDD" } }), "cv_cannot_identify");

        // A deactivated camera's video isn't analysed.
        var off = await NewCamera(a);
        Is(HttpStatusCode.OK, await Patch($"/api/cameras/{off}/status", a.Devices.Token, new { status = "Inactive", reason = "Removed for repair" }));
        Is(HttpStatusCode.Conflict, await SvcPost("/api/monitoring/events", Detected(a, off)), "camera_not_active");

        // The service can do nothing else.
        Is(HttpStatusCode.Forbidden, await SvcGet("/api/monitoring/events"));
        Is(HttpStatusCode.Forbidden, await SvcGet("/api/users"));
        Is(HttpStatusCode.Forbidden, await SvcPost("/api/monitoring/alerts/00000000-0000-0000-0000-000000000000/acknowledge", new { }));

        // People see the event like any other, labelled with its producer.
        var seen = (await Get($"/api/monitoring/events?factoryId={a.Factory}&cameraId={camA}", a.Monitor.Token)).Body.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(seen, e => S(e, "producer") == "CV_SERVICE");

        // The database refuses a CV event labelled SIMULATED.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db =>
        {
            db.MonitoringEvents.Add(new MonitoringEvent
            {
                FactoryId = Guid.Parse(a.Factory), EventType = "PERSON_DETECTED", Category = "VISION", SourceType = "SIMULATED", Producer = "CV_SERVICE",
                IdempotencyKey = K(), CameraId = Guid.Parse(camA), Summary = "x", RecordedByEmail = "x", OccurredAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            });
            return db.SaveChangesAsync();
        }));
        Assert.Contains("CK_monitoring_events_producer_source", ex.ToString());
    }

    [DockerFact]
    public async Task Only_the_cv_service_reads_camera_configuration_with_zones_videos_and_stream_credentials()
    {
        var a = await NewSite(DbApiFactory.CvServiceFactory);
        var b = await NewSite();
        var rtsp = await NewCamera(a);
        var recorded = await NewCamera(a);
        var other = await NewCamera(b);
        const string password = "Pa55-word-cv";
        Is(HttpStatusCode.OK, await Put($"/api/cameras/{rtsp}/source", a.Devices.Token,
            new { sourceType = "Rtsp", streamUrl = $"rtsp://camadmin:{password}@10.20.30.40:554/Streaming/101" }));
        var zoneId = Guid.NewGuid();
        await Rag.WithDb(Api, db =>
        {
            db.CameraZones.Add(new CameraZone
            {
                Id = zoneId, CameraId = Guid.Parse(rtsp), FactoryId = Guid.Parse(a.Factory), Name = "Store room", ZoneType = "Restricted",
                Polygon = "[{\"x\":0.7,\"y\":0.1},{\"x\":0.95,\"y\":0.1},{\"x\":0.95,\"y\":0.6},{\"x\":0.7,\"y\":0.6}]",
                CreatedByEmail = "test", UpdatedByEmail = "test",
            });
            return db.SaveChangesAsync();
        });
        var mp4 = new byte[] { 0, 0, 0, 0x20 }.Concat("ftypisom"u8.ToArray()).Concat(new byte[2048]).ToArray();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(mp4);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "video", "line demo.mp4");
        Is(HttpStatusCode.OK, await Send(HttpMethod.Put, $"/api/cameras/{recorded}/source/video", a.Devices.Token, form));

        var list = await SvcGet("/api/integrations/cv/cameras");
        Is(HttpStatusCode.OK, list);
        Assert.Equal("no-store", list.Response.Headers.CacheControl?.ToString());
        var cams = list.Body.EnumerateArray().ToList();
        Assert.DoesNotContain(cams, c => S(c, "id") == other);
        var r = cams.Single(c => S(c, "id") == rtsp);
        Assert.Equal("Rtsp", S(r, "source", "sourceType"));
        Assert.Equal($"rtsp://camadmin:{password}@10.20.30.40:554/Streaming/101", S(r, "source", "streamUrl"));
        var zone = r.GetProperty("zones").EnumerateArray().Single();
        Assert.Equal("Restricted", S(zone, "zoneType"));
        Assert.Equal(4, zone.GetProperty("points").GetArrayLength());
        Assert.Equal("0.7", S(zone.GetProperty("points")[0], "x"));
        var v = cams.Single(c => S(c, "id") == recorded);
        Assert.Equal("True", S(v, "source", "hasVideo"), ignoreCase: true);

        Is(HttpStatusCode.NotFound, await SvcGet($"/api/integrations/cv/cameras/{other}"));
        var one = await SvcGet($"/api/integrations/cv/cameras/{rtsp}");
        Is(HttpStatusCode.OK, one);
        var video = await SvcGet($"/api/integrations/cv/cameras/{recorded}/video");
        Is(HttpStatusCode.OK, video);
        Assert.Equal(mp4, video.Bytes);

        // People never get this, and their camera APIs never show the password.
        var super = await NewPerson(R.SuperAdmin, null);
        Is(HttpStatusCode.Forbidden, await Get("/api/integrations/cv/cameras", super.Token));
        Is(HttpStatusCode.Forbidden, await Get($"/api/integrations/cv/cameras/{rtsp}", a.Admin.Token));
        Assert.DoesNotContain(password, (await Get($"/api/cameras/{rtsp}", a.Admin.Token)).Raw);
        Is(HttpStatusCode.Unauthorized, await SvcGet("/api/integrations/cv/cameras", key: "nope-nope-nope-nope-nope-nope-nope-nope"));
    }
}
