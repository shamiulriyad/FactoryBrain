using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Camera AI end to end against real PostgreSQL, through the HTTP API and the
/// SignalR hub, with OpenAI answered by a fake host (no real API call, no
/// key): sessions and frames, authorization and scope, rate limits, events
/// stored once through the monitoring pipeline (producer CAMERA_AI, REAL, no
/// worker), the PPE alert, live pushes, and one ai_api_usage row per call.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbCameraAiTests : IAsyncLifetime
{
    private const string FakeHost = "camera-ai.fake-openai.invalid";
    private readonly PostgresStack _stack;
    private DbApiFactory _api = null!;
    private volatile string _answer = Answer("missing");
    private int _calls;

    public DbCameraAiTests(PostgresStack stack) { _stack = stack; }

    public Task InitializeAsync()
    {
        _api = DbApiFactory.Custom(_stack.ConnectionStringFor(PostgresStack.LocalDatabase), new()
        {
            ["RAG_EMBEDDING_PROVIDER"] = "local",
            ["CAMERA_AI_OPENAI_API_KEY"] = "sk-camera-ai-test",
            ["CAMERA_AI_BASE_URL"] = $"https://{FakeHost}",
            ["CAMERA_AI_MIN_INTERVAL_MS"] = "250",
            ["CAMERA_AI_INTERVAL_MS"] = "250",
            ["CAMERA_AI_MAX_ATTEMPTS"] = "1",
        });
        _api.Http.Fake(FakeHost, "/v1/chat/completions", (req, body) =>
        {
            Interlocked.Increment(ref _calls);
            Assert.Equal("Bearer sk-camera-ai-test", req.Headers.Authorization?.ToString());
            Assert.Contains("data:image/jpeg;base64,", body);
            Assert.Contains("\"json_schema\"", body);
            return OutboundHttpGuard.Json(_answer);
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static string Answer(string helmet) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["finish_reason"] = "stop",
            ["message"] = new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["person_presence"] = "present", ["person_count"] = 1,
                    ["people"] = new JsonArray(new JsonObject
                    {
                        ["activity"] = "STANDING", ["helmet"] = helmet, ["safety_vest"] = "present",
                        ["box"] = new JsonObject { ["x"] = 0.3, ["y"] = 0.1, ["width"] = 0.35, ["height"] = 0.85 },
                    }),
                    ["scene_activity"] = "STANDING",
                    ["safety_incident"] = new JsonObject { ["status"] = "none", ["type"] = null, ["description"] = null },
                    ["image_quality"] = "good", ["notes"] = null,
                }.ToJsonString(),
            },
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 1100, ["completion_tokens"] = 140 },
    }.ToJsonString();

    // ─── Plumbing ──────────────────────────────────────────────────────────

    private sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw, HttpResponseHeaders Headers)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public string Id => Body.GetProperty("id").GetString()!;
    }

    private async Task<Reply> Send(HttpMethod method, string path, string? bearer, HttpContent? content = null)
    {
        using var client = _api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        var json = raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[') ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw, res.Headers);
    }

    private Task<Reply> Post(string path, string bearer, object body) => Send(HttpMethod.Post, path, bearer, Rag.Json(body));
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, Rag.Json(body));

    private Task<Reply> Frame(string session, string bearer, byte[]? jpeg = null, string type = "image/jpeg")
    {
        var content = new ByteArrayContent(jpeg ?? TestImages.JpegHeader(640, 480));
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return Send(HttpMethod.Post, $"/api/monitoring/sessions/{session}/frames", bearer, content);
    }

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, string? factory)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@camera-ai.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        if (factory is not null) user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = Guid.Parse(factory) });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, email, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    private sealed record Site(string Factory, string Floor, Person Devices, Person Monitor, Person Manager, Person Worker);

    private async Task<Site> NewSite()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var f = await Post("/api/factories", super.Token, new { code = $"A{Tag()[..6]}", name = $"Camera AI factory {Tag()}" });
        Is(HttpStatusCode.Created, f);
        var admin = await NewPerson(R.FactoryAdmin, f.Id);
        var floor = await Post($"/api/factories/{f.Id}/floors", admin.Token, new { code = "F1", name = "First floor" });
        Is(HttpStatusCode.Created, floor);
        return new Site(f.Id, floor.Id, await NewPerson(R.DeviceAdmin, f.Id), await NewPerson(R.MonitoringOperator, f.Id), await NewPerson(R.FactoryManager, f.Id),
            await NewPerson(R.Worker, f.Id));
    }

    private async Task<string> NewCamera(Site s, string source)
    {
        var cam = await Post("/api/cameras", s.Devices.Token, new { factoryId = s.Factory, name = $"Webcam {Tag()}", cameraType = "Webcam", floorId = s.Floor });
        Is(HttpStatusCode.Created, cam);
        var body = source == "LocalWebcam" ? (object)new { sourceType = "LocalWebcam", webcamLabel = "Desk webcam" } : new { sourceType = "Rtsp", streamUrl = "rtsp://10.20.30.40:554/live" };
        Is(HttpStatusCode.OK, await Put($"/api/cameras/{cam.Id}/source", s.Devices.Token, body));
        return cam.Id;
    }

    private HubConnection Hub(string token, ConcurrentQueue<JsonElement> events)
    {
        var server = _api.Server;
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/monitoring"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Headers[TestClientIpStartupFilter.Header] = TestClientIpStartupFilter.NewAddress();
            })
            .Build();
        conn.On<JsonElement>("monitoringEvent", e => events.Enqueue(e));
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

    private static string[] EventTypes(Reply frame) =>
        frame.Body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()!).ToArray();

    // ─── Tests ─────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Webcam_frames_become_confirmed_events_an_alert_live_pushes_and_usage_rows()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, "LocalWebcam");
        var live = new ConcurrentQueue<JsonElement>();
        await using var hub = Hub(s.Manager.Token, live);
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);

        var overview = await Send(HttpMethod.Get, "/api/monitoring/sessions", s.Monitor.Token);
        Is(HttpStatusCode.OK, overview);
        Assert.True(overview.Body.GetProperty("configured").GetBoolean());
        Assert.DoesNotContain("sk-camera-ai-test", overview.Raw);              // the key never reaches a browser

        var start = await Post("/api/monitoring/sessions", s.Monitor.Token, new { cameraId = cam });
        Is(HttpStatusCode.Created, start);
        var session = start.Id;

        var f1 = await Frame(session, s.Monitor.Token);
        Is(HttpStatusCode.OK, f1);
        Assert.Equal("analysed", f1.Body.GetProperty("outcome").GetString());
        Assert.Equal("missing", f1.Body.GetProperty("analysis").GetProperty("helmet").GetString());
        Assert.Empty(EventTypes(f1));                                           // one frame proves nothing
        await Task.Delay(300);
        Assert.Equal(new[] { "PERSON_DETECTED" }, EventTypes(await Frame(session, s.Monitor.Token)));
        await Task.Delay(300);
        var f3 = await Frame(session, s.Monitor.Token);
        Assert.Equal(new[] { "ACTIVITY_CHANGED", "PPE_VIOLATION" }, EventTypes(f3));
        Assert.All(f3.Body.GetProperty("events").EnumerateArray(), e => Assert.True(e.GetProperty("stored").GetBoolean(), e.ToString()));
        var alertId = f3.Body.GetProperty("events")[1].GetProperty("alertId").GetString();
        Assert.NotNull(alertId);
        await Task.Delay(300);
        Assert.Empty(EventTypes(await Frame(session, s.Monitor.Token)));       // no duplicate per frame

        var stop = await Post($"/api/monitoring/sessions/{session}/stop", s.Monitor.Token, new { });
        Is(HttpStatusCode.OK, stop);
        Assert.Equal("ENDED", stop.Body.GetProperty("status").GetString());

        var camId = Guid.Parse(cam);
        var stored = await Rag.WithDb(_api, db => db.MonitoringEvents.AsNoTracking().Where(e => e.CameraId == camId).OrderBy(e => e.ReceivedAt).ToListAsync());
        Assert.Equal(new[] { "PERSON_DETECTED", "ACTIVITY_CHANGED", "PPE_VIOLATION", "PERSON_LOST" }, stored.Select(e => e.EventType));
        Assert.All(stored, e =>
        {
            Assert.Equal(("CAMERA_AI", "REAL", "service:camera-ai"), (e.Producer, e.SourceType, e.RecordedByEmail));
            Assert.Null(e.WorkerId);
            Assert.Null(e.Confidence);
            Assert.Contains("openai-vision", e.Payload);
        });
        Assert.StartsWith("AI estimate: PPE missing", stored[2].Summary);
        var alert = await Rag.WithDb(_api, db => db.MonitoringAlerts.AsNoTracking().FirstAsync(a => a.Id == Guid.Parse(alertId!)));
        Assert.Equal(("PPE_VIOLATION", "OPEN", "VISION"), (alert.AlertType, alert.Status, alert.Category));

        var usage = await Rag.WithDb(_api, db => db.AiApiUsage.AsNoTracking().Where(u => u.EntityId == session).ToListAsync());
        Assert.Equal(4, usage.Count);
        Assert.All(usage, u => Assert.Equal(("camera.vision", "openai", "ok", 1100, 140), (u.Purpose, u.Provider, u.Outcome, u.InputTokens!.Value, u.OutputTokens!.Value)));
        Assert.Equal(4, _calls);

        await Until(() => live.Any(e => e.GetProperty("eventType").GetString() == "PPE_VIOLATION"), "the PPE event over SignalR");
        Assert.Contains(live, e => e.GetProperty("eventType").GetString() == "PERSON_LOST");
    }

    [DockerFact]
    public async Task Access_scope_source_and_ownership_are_enforced()
    {
        var s = await NewSite();
        var other = await NewSite();
        var webcam = await NewCamera(s, "LocalWebcam");
        var rtsp = await NewCamera(s, "Rtsp");

        Is(HttpStatusCode.Unauthorized, await Send(HttpMethod.Post, "/api/monitoring/sessions", null, Rag.Json(new { cameraId = webcam })));
        Is(HttpStatusCode.Forbidden, await Post("/api/monitoring/sessions", s.Worker.Token, new { cameraId = webcam }));
        Is(HttpStatusCode.NotFound, await Post("/api/monitoring/sessions", other.Monitor.Token, new { cameraId = webcam }), "camera_not_found");
        Is(HttpStatusCode.Conflict, await Post("/api/monitoring/sessions", s.Monitor.Token, new { cameraId = rtsp }), "camera_not_browser_webcam");

        var start = await Post("/api/monitoring/sessions", s.Monitor.Token, new { cameraId = webcam });
        Is(HttpStatusCode.Created, start);
        Is(HttpStatusCode.Conflict, await Post("/api/monitoring/sessions", s.Manager.Token, new { cameraId = webcam }), "camera_session_active");
        Is(HttpStatusCode.Forbidden, await Frame(start.Id, s.Manager.Token), "session_not_yours");
        Is(HttpStatusCode.NotFound, await Send(HttpMethod.Get, $"/api/monitoring/sessions/{start.Id}", other.Monitor.Token), "session_not_found");
        Is(HttpStatusCode.NotFound, await Frame("not-a-guid", s.Monitor.Token), "session_not_found");
        Is(HttpStatusCode.BadRequest, await Frame(start.Id, s.Monitor.Token, TestImages.PngHeader(640, 480), "image/png"), "frame_type_invalid");
        Is(HttpStatusCode.RequestEntityTooLarge, await Frame(start.Id, s.Monitor.Token, new byte[700 * 1024]), "frame_too_large");
        Assert.Equal(0, _calls);

        // Two frames at once: one is analysed, the other is refused (in progress / too soon), never queued.
        var both = await Task.WhenAll(Frame(start.Id, s.Monitor.Token), Frame(start.Id, s.Monitor.Token));
        Assert.Single(both, r => r.Status == HttpStatusCode.OK);
        var refused = Assert.Single(both, r => r.Status != HttpStatusCode.OK);
        Assert.True((refused.Status, refused.Code) is (HttpStatusCode.Conflict, "analysis_in_progress") or (HttpStatusCode.TooManyRequests, "frame_too_soon"),
            refused.Raw);
        if (refused.Status == HttpStatusCode.TooManyRequests) Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Equal(1, _calls);

        // Camera AI event types can't be sent by anyone else.
        var forged = await Post("/api/monitoring/events", s.Devices.Token, new
        {
            eventType = "PPE_VIOLATION", factoryId = s.Factory, idempotencyKey = $"forged-{Tag()}", sourceType = "REAL", cameraId = webcam,
            payload = new { reason = "helmet not worn" },
        });
        Is(HttpStatusCode.BadRequest, forged, "event_type_reserved_for_camera_ai");
        Is(HttpStatusCode.OK, await Post($"/api/monitoring/sessions/{start.Id}/stop", s.Monitor.Token, new { }));
    }

    [DockerFact]
    public async Task Openai_failures_are_shown_per_frame_and_become_detection_unavailable()
    {
        var s = await NewSite();
        var cam = await NewCamera(s, "LocalWebcam");
        var start = await Post("/api/monitoring/sessions", s.Monitor.Token, new { cameraId = cam });
        Is(HttpStatusCode.Created, start);
        _answer = """{"choices":[{"finish_reason":"stop","message":{"content":"{\"person_presence\":\"maybe\"}"}}]}""";
        string[] last = Array.Empty<string>();
        for (var i = 0; i < 3; i++)
        {
            var f = await Frame(start.Id, s.Monitor.Token);
            Is(HttpStatusCode.OK, f);
            Assert.Equal("invalid_output", f.Body.GetProperty("error").GetProperty("code").GetString());
            last = EventTypes(f);
            await Task.Delay(300);
        }
        Assert.Equal(new[] { "DETECTION_UNAVAILABLE" }, last);
        var camId = Guid.Parse(cam);
        var alert = await Rag.WithDb(_api, db => db.MonitoringAlerts.AsNoTracking().FirstOrDefaultAsync(a => a.CameraId == camId && a.AlertType == "MODEL_UNAVAILABLE"));
        Assert.NotNull(alert);
        var usage = await Rag.WithDb(_api, db => db.AiApiUsage.AsNoTracking().Where(u => u.EntityId == start.Id).Select(u => u.Outcome).ToListAsync());
        Assert.Equal(3, usage.Count);                                            // invalid answers are still billed and logged
        Assert.All(usage, o => Assert.Equal("ok", o));                           // the call succeeded; the contract check failed
        _answer = Answer("present");
        Is(HttpStatusCode.OK, await Post($"/api/monitoring/sessions/{start.Id}/stop", s.Monitor.Token, new { }));
    }
}
