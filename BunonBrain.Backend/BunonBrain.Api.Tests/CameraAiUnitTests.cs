using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BunonBrain.Application.Access;
using BunonBrain.Application.CameraAi;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using BunonBrain.Application.Settings;
using BunonBrain.Infrastructure.CameraAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using P = BunonBrain.Application.Access.Permissions;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Camera AI (OpenAI vision on browser webcam frames) without a database or
/// network: the structured-output schema and the validation of answers, the
/// JPEG checks, temporal confirmation (no event per frame, no duplicates),
/// the OpenAI client against a scripted HTTP handler (request shape, retries,
/// failures, token counts), and the session service with fakes (start rules,
/// one frame in flight, rate limits, events, offline, stop, cost).
/// </summary>
public sealed class CameraAiUnitTests
{
    // ─── Helpers ───────────────────────────────────────────────────────────

    private static readonly Guid FactoryA = Guid.NewGuid();

    private static AccessContext As(string role, Guid? factory = null, Guid? userId = null) =>
        new(userId ?? Guid.NewGuid(), $"{role.ToLowerInvariant()}@x.test", new[] { role }, AccessCatalog.Role(role)!.Permissions,
            new[] { factory ?? FactoryA }, Array.Empty<string>());

    private static JsonObject Answer(string presence = "present", int? count = 1, string activity = "STANDING", string helmet = "present",
        string vest = "present", object? box = null, string quality = "good", string incident = "none")
    {
        var people = new JsonArray();
        if (presence == "present")
            people.Add(new JsonObject
            {
                ["activity"] = activity, ["helmet"] = helmet, ["safety_vest"] = vest,
                ["box"] = box is null ? new JsonObject { ["x"] = 0.2, ["y"] = 0.1, ["width"] = 0.3, ["height"] = 0.8 } : JsonSerializer.SerializeToNode(box),
            });
        return new JsonObject
        {
            ["person_presence"] = presence, ["person_count"] = count, ["people"] = people, ["scene_activity"] = presence == "present" ? activity : "UNKNOWN",
            ["safety_incident"] = new JsonObject { ["status"] = incident, ["type"] = null, ["description"] = null },
            ["image_quality"] = quality, ["notes"] = null,
        };
    }

    private static VisionAnalysis Valid(JsonObject o)
    {
        var (a, error) = CameraAiContract.Validate(o);
        Assert.True(a is not null, error);
        return a!;
    }

    private static byte[] Jpeg(ushort w = 640, ushort h = 480) => TestImages.JpegHeader(w, h);

    // ─── Contract: schema ──────────────────────────────────────────────────

    [Fact]
    public void Schema_is_strict_every_object_closes_and_requires_all_its_properties()
    {
        var schema = CameraAiContract.Schema();
        var objects = 0;
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o["type"] is JsonValue t && t.GetValue<string>() == "object")
                {
                    objects++;
                    Assert.False(o["additionalProperties"]!.GetValue<bool>());
                    var props = ((JsonObject)o["properties"]!).Select(p => p.Key).OrderBy(k => k).ToList();
                    var required = ((JsonArray)o["required"]!).Select(r => r!.GetValue<string>()).OrderBy(k => k).ToList();
                    Assert.Equal(props, required);
                }
                foreach (var kv in o) Walk(kv.Value);
            }
            else if (n is JsonArray a) foreach (var x in a) Walk(x);
        }
        Walk(schema);
        Assert.Equal(4, objects);   // root, person, box, incident
        var ppe = schema["properties"]!["people"]!["items"]!["properties"]!["helmet"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>());
        Assert.Equal(new[] { "present", "missing", "unclear", "unknown" }, ppe);
        // The activity vocabulary is the platform's (ActivityLabels), so events stay comparable.
        var acts = schema["properties"]!["scene_activity"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>());
        Assert.Equal(ActivityLabels.All, acts);
        // No numeric confidence is ever asked for.
        Assert.DoesNotContain("confidence", schema.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    // ─── Contract: output validation ───────────────────────────────────────

    [Fact]
    public void A_well_formed_answer_validates_and_aggregates_ppe_per_frame()
    {
        var o = Answer(helmet: "missing", vest: "unclear");
        ((JsonArray)o["people"]!).Add(new JsonObject { ["activity"] = "WALKING", ["helmet"] = "present", ["safety_vest"] = "present", ["box"] = null });
        o["person_count"] = 2;
        var a = Valid(o);
        Assert.Equal("present", a.PersonPresence);
        Assert.Equal(2, a.PersonCount);
        Assert.Equal(PpeStatus.Missing, a.Helmet);        // anyone missing → missing
        Assert.Equal(PpeStatus.Unclear, a.SafetyVest);    // one unclear, one present → unclear
        Assert.Null(a.People[1].Box);
        Assert.Empty(a.Adjustments);
    }

    [Theory]
    [InlineData("person_presence", "\"maybe\"")]
    [InlineData("scene_activity", "\"DANCING\"")]
    [InlineData("image_quality", "\"great\"")]
    [InlineData("person_count", "1.5")]
    [InlineData("person_count", "-1")]
    [InlineData("people", "\"two\"")]
    [InlineData("safety_incident", "null")]
    public void Answers_outside_the_contract_are_rejected(string field, string json)
    {
        var o = Answer();
        o[field] = JsonNode.Parse(json);
        var (a, error) = CameraAiContract.Validate(o);
        Assert.Null(a);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_person_with_an_unknown_ppe_label_rejects_the_answer()
    {
        var o = Answer();
        o["people"]![0]!["helmet"] = "probably";
        Assert.Null(CameraAiContract.Validate(o).Analysis);
    }

    [Fact]
    public void Contradictions_are_corrected_and_listed_never_silently()
    {
        var o = Answer();
        o["person_presence"] = "absent";        // but a person is listed
        var a = Valid(o);
        Assert.Equal(PresenceStatus.Unclear, a.PersonPresence);
        Assert.NotEmpty(a.Adjustments);

        var count = Answer();
        count["person_count"] = 0;
        var c = Valid(count);
        Assert.Null(c.PersonCount);              // present with 0 people: count unknown
        Assert.NotEmpty(c.Adjustments);

        var inc = Answer();
        inc["safety_incident"] = new JsonObject { ["status"] = "none", ["type"] = "FALL", ["description"] = "x" };
        var i = Valid(inc);
        Assert.Null(i.Incident.Type);
        Assert.Null(i.Incident.Description);
    }

    [Fact]
    public void Boxes_are_clamped_into_the_frame_and_pixel_boxes_are_dropped()
    {
        var a = Valid(Answer(box: new { x = 0.8, y = -0.1, width = 0.5, height = 0.5 }));
        var b = a.People[0].Box!;
        Assert.Equal(0.8, b.X); Assert.Equal(0, b.Y);
        Assert.Equal(0.2, b.Width, 4); Assert.Equal(0.4, b.Height, 4);

        var px = Valid(Answer(box: new { x = 120, y = 40, width = 200, height = 300 }));
        Assert.Null(px.People[0].Box);
        Assert.Contains(px.Adjustments, s => s.Contains("box", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Absent_frames_have_no_ppe_and_unusable_images_prove_nothing()
    {
        var absent = Valid(Answer(presence: "absent", count: 0));
        Assert.Equal(PpeStatus.Unknown, absent.Helmet);
        Assert.Equal(0, absent.PersonCount);
        var dark = Valid(Answer(quality: "unusable"));
        Assert.Equal(PresenceStatus.Unclear, dark.EffectivePresence);
    }

    // ─── Frame checks ──────────────────────────────────────────────────────

    [Fact]
    public void Jpeg_frames_are_checked_by_type_size_magic_and_dimensions()
    {
        Assert.Null(CameraAiContract.CheckJpeg(Jpeg(640, 480), "image/jpeg", 600_000).Code);
        Assert.Equal((640, 480), (CameraAiContract.CheckJpeg(Jpeg(640, 480), "image/jpeg", 600_000).Width, CameraAiContract.CheckJpeg(Jpeg(640, 480), "image/jpeg", 600_000).Height));
        Assert.Equal("frame_type_invalid", CameraAiContract.CheckJpeg(Jpeg(), "image/png", 600_000).Code);
        Assert.Equal("frame_type_invalid", CameraAiContract.CheckJpeg(Jpeg(), null, 600_000).Code);
        Assert.Equal("frame_empty", CameraAiContract.CheckJpeg(Array.Empty<byte>(), "image/jpeg", 600_000).Code);
        Assert.Equal("frame_too_large", CameraAiContract.CheckJpeg(Jpeg(), "image/jpeg", 10).Code);
        Assert.Equal("frame_not_jpeg", CameraAiContract.CheckJpeg(TestImages.PngHeader(640, 480), "image/jpeg", 600_000).Code);
        Assert.Equal("frame_size_invalid", CameraAiContract.CheckJpeg(Jpeg(32, 32), "image/jpeg", 600_000).Code);
        Assert.Equal("frame_size_invalid", CameraAiContract.CheckJpeg(Jpeg(4000, 3000), "image/jpeg", 600_000).Code);
        Assert.Equal("frame_not_jpeg", CameraAiContract.CheckJpeg(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, "image/jpeg", 600_000).Code);
    }

    // ─── Options and cost ──────────────────────────────────────────────────

    [Fact]
    public void Options_come_from_the_environment_with_safe_defaults_and_clamps()
    {
        var d = CameraAiOptions.FromEnvironment(_ => null);
        Assert.Equal("gpt-5.4-mini", d.Model);
        Assert.Equal(1000, d.DefaultIntervalMs);          // 1 frame per second
        Assert.Equal(new[] { PpeItems.Helmet, PpeItems.Vest }, d.Temporal.PpeRequired);
        Assert.Null(d.ReasoningEffort);                   // the model's own default ("none"): fastest per frame
        // gpt-5.4-mini's published prices apply by default: $0.75 / $4.50 per 1M tokens.
        Assert.Equal(0.00075m + 0.0045m, d.EstimateCost(1000, 1000));
        // Another model without configured prices: no cost is invented.
        Assert.Null(CameraAiOptions.FromEnvironment(k => k == "CAMERA_AI_MODEL" ? "gpt-other" : null).EstimateCost(1000, 1000));

        var env = new Dictionary<string, string>
        {
            ["CAMERA_AI_MODEL"] = "gpt-x", ["CAMERA_AI_INTERVAL_MS"] = "100", ["CAMERA_AI_MIN_INTERVAL_MS"] = "500",
            ["CAMERA_AI_PPE_REQUIRED"] = "vest, nonsense", ["CAMERA_AI_REASONING_EFFORT"] = "Low", ["CAMERA_AI_IMAGE_DETAIL"] = "ultra",
            ["CAMERA_AI_PRICE_INPUT_PER_1M_USD"] = "0.75", ["CAMERA_AI_PRICE_OUTPUT_PER_1M_USD"] = "4.5", ["CAMERA_AI_ENABLED"] = "false",
        };
        var o = CameraAiOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("gpt-x", o.Model);
        Assert.Equal(500, o.MinIntervalMs);
        Assert.Equal(500, o.DefaultIntervalMs);           // never faster than the minimum
        Assert.Equal(new[] { PpeItems.Vest }, o.Temporal.PpeRequired);
        Assert.Equal("low", o.ReasoningEffort);
        Assert.Equal("low", o.ImageDetail);               // unknown value: default
        Assert.False(o.Enabled);
        Assert.Equal(0.75m + 4.5m, o.EstimateCost(1_000_000, 1_000_000));
    }

    // ─── Temporal confirmation ─────────────────────────────────────────────

    private static CameraAiTemporalState Temporal() => new(new TemporalSettings());
    private static string[] Types(IEnumerable<PlannedEvent> e) => e.Select(x => x.EventType).ToArray();

    [Fact]
    public void Presence_needs_consecutive_frames_and_is_reported_once()
    {
        var t = Temporal();
        Assert.Empty(t.OnAnalysis(Valid(Answer())));                         // 1 frame: nothing yet
        Assert.Equal(new[] { "PERSON_DETECTED" }, Types(t.OnAnalysis(Valid(Answer()))));
        for (var i = 0; i < 5; i++) Assert.DoesNotContain("PERSON_DETECTED", Types(t.OnAnalysis(Valid(Answer()))));
        Assert.Empty(t.OnAnalysis(Valid(Answer(presence: "absent", count: 0))));
        Assert.Empty(t.OnAnalysis(Valid(Answer(presence: "absent", count: 0))));
        Assert.Equal(new[] { "PERSON_LOST" }, Types(t.OnAnalysis(Valid(Answer(presence: "absent", count: 0)))));
        Assert.Empty(t.OnAnalysis(Valid(Answer(presence: "absent", count: 0))));
        Assert.False(t.PersonPresent);
    }

    [Fact]
    public void Unclear_and_unusable_frames_neither_confirm_nor_clear()
    {
        var t = Temporal();
        t.OnAnalysis(Valid(Answer()));
        Assert.Empty(t.OnAnalysis(Valid(Answer(presence: "unclear", count: null))));
        Assert.Empty(t.OnAnalysis(Valid(Answer(quality: "unusable"))));
        Assert.False(t.PersonPresent);
        Assert.Equal(new[] { "PERSON_DETECTED" }, Types(t.OnAnalysis(Valid(Answer()))));
    }

    [Fact]
    public void Activity_changes_only_after_it_holds_and_unknown_is_ignored()
    {
        var t = Temporal();
        t.OnAnalysis(Valid(Answer(activity: "STANDING")));
        var e = t.OnAnalysis(Valid(Answer(activity: "STANDING")));           // person confirmed, standing seen twice
        Assert.Equal(new[] { "PERSON_DETECTED" }, Types(e));
        var first = t.OnAnalysis(Valid(Answer(activity: "STANDING")));
        var changed = Assert.Single(first, x => x.EventType == "ACTIVITY_CHANGED");
        Assert.Equal("STANDING", changed.Payload["activity"]!.GetValue<string>());
        Assert.Null(changed.Payload["previousActivity"]);
        Assert.Empty(t.OnAnalysis(Valid(Answer(activity: "SITTING"))));
        Assert.Empty(t.OnAnalysis(Valid(Answer(activity: "UNKNOWN"))));     // doesn't break or confirm
        Assert.Empty(t.OnAnalysis(Valid(Answer(activity: "SITTING"))));
        var next = Assert.Single(t.OnAnalysis(Valid(Answer(activity: "SITTING"))));
        Assert.Equal("ACTIVITY_CHANGED", next.EventType);
        Assert.Equal("STANDING", next.Payload["previousActivity"]!.GetValue<string>());
        Assert.Empty(t.OnAnalysis(Valid(Answer(activity: "SITTING"))));     // no repeat
    }

    [Fact]
    public void Ppe_violation_needs_consecutive_missing_frames_and_is_raised_once_per_episode()
    {
        var t = Temporal();
        t.OnAnalysis(Valid(Answer(helmet: "missing")));
        t.OnAnalysis(Valid(Answer(helmet: "missing")));                     // person confirmed; 2 missing
        Assert.DoesNotContain("PPE_VIOLATION", Types(t.OnAnalysis(Valid(Answer(helmet: "unclear")))));   // unclear breaks the streak
        t.OnAnalysis(Valid(Answer(helmet: "missing")));
        t.OnAnalysis(Valid(Answer(helmet: "missing")));
        var v = Assert.Single(t.OnAnalysis(Valid(Answer(helmet: "missing"))), x => x.EventType == "PPE_VIOLATION");
        Assert.Equal("HELMET", v.Payload["missing"]![0]!.GetValue<string>());
        Assert.Equal("helmet not worn", v.Payload["reason"]!.GetValue<string>());
        for (var i = 0; i < 4; i++) Assert.DoesNotContain("PPE_VIOLATION", Types(t.OnAnalysis(Valid(Answer(helmet: "missing")))));
        // Episode ends after 3 "present"; a new one may be raised afterwards.
        for (var i = 0; i < 3; i++) t.OnAnalysis(Valid(Answer(helmet: "present")));
        Assert.Empty(t.PpeViolations);
        t.OnAnalysis(Valid(Answer(helmet: "missing"))); t.OnAnalysis(Valid(Answer(helmet: "missing")));
        Assert.Contains("PPE_VIOLATION", Types(t.OnAnalysis(Valid(Answer(helmet: "missing")))));
    }

    [Fact]
    public void Items_that_are_not_required_never_raise_a_violation()
    {
        var t = new CameraAiTemporalState(new TemporalSettings { PpeRequired = new[] { PpeItems.Vest } });
        for (var i = 0; i < 8; i++) Assert.DoesNotContain("PPE_VIOLATION", Types(t.OnAnalysis(Valid(Answer(helmet: "missing")))));
    }

    [Fact]
    public void Failures_raise_detection_unavailable_once_and_success_recovers()
    {
        var t = Temporal();
        Assert.Empty(t.OnFailure("timeout", "slow"));
        Assert.Empty(t.OnFailure("timeout", "slow"));
        var down = Assert.Single(t.OnFailure("unavailable", "OpenAI returned HTTP 503."));
        Assert.Equal("DETECTION_UNAVAILABLE", down.EventType);
        Assert.Equal("UNAVAILABLE", down.Payload["toStatus"]!.GetValue<string>());
        Assert.Empty(t.OnFailure("timeout", "slow"));
        var up = Assert.Single(t.OnAnalysis(Valid(Answer(presence: "absent", count: 0))));
        Assert.Equal("MODEL_STATUS_CHANGED", up.EventType);
        Assert.Equal("READY", up.Payload["toStatus"]!.GetValue<string>());
        Assert.False(t.DetectionUnavailable);
    }

    [Fact]
    public void Offline_and_online_are_reported_once_each_and_stop_reports_a_confirmed_person_lost()
    {
        var t = Temporal();
        Assert.Empty(t.OnFrame());
        Assert.Equal("OFFLINE", t.OnOffline("gone").Single().Payload["toStatus"]!.GetValue<string>());
        Assert.Empty(t.OnOffline("still gone"));
        Assert.Equal("ONLINE", t.OnFrame().Single().Payload["toStatus"]!.GetValue<string>());
        Assert.Empty(t.OnFrame());
        Assert.Empty(t.OnStop("stopped"));                                   // nobody confirmed: nothing to report
        t.OnAnalysis(Valid(Answer())); t.OnAnalysis(Valid(Answer()));
        Assert.Equal(new[] { "PERSON_LOST" }, Types(t.OnStop("stopped")));
    }

    // ─── Monitoring contract additions ─────────────────────────────────────

    [Fact]
    public void Camera_ai_event_types_are_in_the_contract_with_alerts_and_no_identities()
    {
        foreach (var t in new[] { "ACTIVITY_CHANGED", "PPE_VIOLATION", "DETECTION_UNAVAILABLE" }) Assert.Equal(t, MonitoringEventTypes.Parse(t));
        Assert.Contains(MonitoringProducers.CameraAi, MonitoringProducers.All);
        Assert.False(MonitoringProducers.IsExternal(MonitoringProducers.CameraAi));
        foreach (var t in MonitoringEventTypes.CameraAi.Except(new[] { MonitoringEventTypes.PersonDetected, MonitoringEventTypes.PersonLost }))
            Assert.False(MonitoringRules.Requirements(t).WorkerAllowed);
        Assert.True(MonitoringRules.Requirements(MonitoringEventTypes.ActivityChanged).Activity);
        Assert.False(MonitoringRules.Requirements(MonitoringEventTypes.ActivityChanged).Track);

        var cam = Guid.NewGuid();
        AlertContext Ctx(string type, string? to = null) => new(type, false, to, null, cam, "CAM-9", null, null, null, null, null, null, null, "gpt-5.4-mini", "helmet not worn");
        var ppe = MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.PpeViolation))!;
        Assert.Equal(AlertTypes.PpeViolation, ppe.AlertType);
        Assert.Equal(MonitoringCategories.Vision, AlertTypes.CategoryOf(ppe.AlertType));
        Assert.Equal($"PPE_VIOLATION:{cam}", ppe.DedupKey);                   // one open alert per camera
        Assert.Contains("not a confirmed fact", ppe.Description);
        var down = MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.DetectionUnavailable, "UNAVAILABLE"))!;
        Assert.Equal(AlertTypes.ModelUnavailable, down.AlertType);
        // MODEL_STATUS_CHANGED READY for the same model annotates that alert's recovery.
        Assert.Equal(down.DedupKey, MonitoringRules.RecoveryDedupKey(Ctx(MonitoringEventTypes.ModelStatusChanged, "READY")));
        Assert.Null(MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.ActivityChanged)));
        Assert.StartsWith("AI estimate", MonitoringRules.Summary(MonitoringEventTypes.PpeViolation, false, "CAM-9", null, null, null, null, null, null, null, false,
            reason: "helmet not worn"));
    }

    // ─── OpenAI client ─────────────────────────────────────────────────────

    private static string Completion(JsonObject content, string finish = "stop", int inTok = 900, int outTok = 120) =>
        new JsonObject
        {
            ["id"] = "chatcmpl-test", ["model"] = "gpt-5.4-mini",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0, ["finish_reason"] = finish,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content.ToJsonString(), ["refusal"] = null },
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = inTok, ["completion_tokens"] = outTok },
        }.ToJsonString();

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OpenAiVisionClient Client(HttpMessageHandler h, CameraAiOptions? o = null) =>
        new(o ?? new CameraAiOptions { ApiKey = "sk-camera-test", MaxAttempts = 2, Timeout = TimeSpan.FromSeconds(5) }, new NoAiSettings(),
            new SingleClientFactory(h), new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<OpenAiVisionClient>.Instance);

    private static readonly VisionCallContext Ctx = new(FactoryA, Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task The_request_uses_structured_outputs_an_image_input_and_a_server_side_key()
    {
        var h = new ScriptedHandler(_ => Reply(Completion(Answer())));
        var r = await Client(h).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None);
        Assert.Equal(1, r.Attempts);
        Assert.Equal((900, 120), (r.InputTokens, r.OutputTokens));
        Assert.Equal("present", r.Output["person_presence"]!.GetValue<string>());

        var req = Assert.Single(h.Bodies);
        var body = JsonNode.Parse(req)!;
        Assert.Equal("gpt-5.4-mini", body["model"]!.GetValue<string>());
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Null(body["reasoning_effort"]);               // not sent by default
        var withEffort = new ScriptedHandler(_ => Reply(Completion(Answer())));
        await Client(withEffort, new CameraAiOptions { ApiKey = "sk", ReasoningEffort = "low" }).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None);
        Assert.Equal("low", JsonNode.Parse(withEffort.Bodies.Single())!["reasoning_effort"]!.GetValue<string>());
        Assert.NotNull(body["max_completion_tokens"]);
        Assert.Null(body["temperature"]);                    // reasoning models accept only their default
        var image = body["messages"]![1]!["content"]![1]!["image_url"]!;
        Assert.StartsWith("data:image/jpeg;base64,", image["url"]!.GetValue<string>());
        Assert.Equal("low", image["detail"]!.GetValue<string>());
        Assert.Equal("Bearer sk-camera-test", h.Authorizations.Single());
        Assert.EndsWith("/v1/chat/completions", h.Uris.Single());
    }

    [Fact]
    public async Task Transient_failures_are_retried_within_the_limit_and_others_are_not()
    {
        var flaky = new ScriptedHandler(_ => Reply("{}", HttpStatusCode.ServiceUnavailable), _ => Reply(Completion(Answer())));
        Assert.Equal(2, (await Client(flaky).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None)).Attempts);

        var down = new ScriptedHandler(_ => Reply("{}", HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<CameraVisionException>(() => Client(down).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None));
        Assert.Equal(("unavailable", 2), (ex.Code, down.Calls));     // MaxAttempts = 2

        var limited = new ScriptedHandler(_ => Reply("""{"error":{"code":"rate_limit_exceeded","type":"requests"}}""", HttpStatusCode.TooManyRequests));
        Assert.Equal("rate_limited", (await Assert.ThrowsAsync<CameraVisionException>(() => Client(limited).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None))).Code);
        Assert.Equal(2, limited.Calls);

        foreach (var (status, json, code) in new[]
                 {
                     (HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_api_key"}}""", "unauthorized"),
                     (HttpStatusCode.TooManyRequests, """{"error":{"code":"insufficient_quota","type":"insufficient_quota"}}""", "quota_exceeded"),
                     (HttpStatusCode.BadRequest, """{"error":{"code":"unsupported_parameter","param":"reasoning_effort"}}""", "provider_error"),
                     (HttpStatusCode.NotFound, """{"error":{"code":"model_not_found"}}""", "provider_error"),
                 })
        {
            var h = new ScriptedHandler(_ => Reply(json, status));
            var e = await Assert.ThrowsAsync<CameraVisionException>(() => Client(h).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None));
            Assert.Equal(code, e.Code);
            Assert.Equal(1, h.Calls);                                   // never retried
        }
    }

    [Fact]
    public async Task Refusals_cut_off_answers_and_time_outs_are_failures_with_their_tokens_counted()
    {
        var refusal = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["message"] = new JsonObject { ["content"] = null, ["refusal"] = "I can't help with that." } }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 800, ["completion_tokens"] = 5 },
        }.ToJsonString();
        var e1 = await Assert.ThrowsAsync<CameraVisionException>(() => Client(new ScriptedHandler(_ => Reply(refusal))).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None));
        Assert.Equal(("refused", 800, 5), (e1.Code, e1.InputTokens, e1.OutputTokens));

        var cut = Completion(Answer(), finish: "length", inTok: 850, outTok: 1500);
        var e2 = await Assert.ThrowsAsync<CameraVisionException>(() => Client(new ScriptedHandler(_ => Reply(cut))).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None));
        Assert.Equal(("invalid_output", 1500), (e2.Code, e2.OutputTokens));

        var notJson = Reply("""{"choices":[{"finish_reason":"stop","message":{"content":"a person is standing"}}]}""");
        Assert.Equal("invalid_output", (await Assert.ThrowsAsync<CameraVisionException>(() =>
            Client(new ScriptedHandler(_ => notJson)).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None))).Code);

        var slow = new DelayHandler(TimeSpan.FromSeconds(10));
        var o = new CameraAiOptions { ApiKey = "sk", MaxAttempts = 1, Timeout = TimeSpan.FromMilliseconds(200) };
        Assert.Equal("timeout", (await Assert.ThrowsAsync<CameraVisionException>(() => Client(slow, o).AnalyzeAsync(Jpeg(), Ctx, CancellationToken.None))).Code);
    }

    [Fact]
    public void Status_never_reveals_the_key_and_says_why_ai_is_off()
    {
        var on = Client(new ScriptedHandler()).Status();
        Assert.True(on.Configured);
        Assert.Equal("CAMERA_AI_OPENAI_API_KEY", on.KeySource);
        Assert.DoesNotContain("sk-camera-test", JsonSerializer.Serialize(on));
        var off = Client(new ScriptedHandler(), new CameraAiOptions { Enabled = false, ApiKey = "sk" }).Status();
        Assert.False(off.Configured);
        Assert.Contains("CAMERA_AI_ENABLED", off.Reason);
        var noKey = new OpenAiVisionClient(new CameraAiOptions(), new NoAiSettings(), new SingleClientFactory(new ScriptedHandler()),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<OpenAiVisionClient>.Instance).Status();
        Assert.False(noKey.Configured);
        Assert.Contains("OpenAI", noKey.Reason);
    }

    // ─── Session service ───────────────────────────────────────────────────

    private static readonly Guid WebcamCam = Guid.NewGuid(), RtspCam = Guid.NewGuid(), InactiveCam = Guid.NewGuid(), WebcamCam2 = Guid.NewGuid();

    private sealed class Rig
    {
        public ManualTime Time { get; } = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
        public FakeAnalyzer Analyzer { get; } = new();
        public FakeSink Sink { get; } = new();
        public FakeUsage Usage { get; } = new();
        public CameraAiSessionService Service { get; }
        public CameraAiOptions Options { get; }

        public Rig(CameraAiOptions? options = null)
        {
            Options = options ?? new CameraAiOptions { PriceInputPer1M = 1m, PriceOutputPer1M = 2m };
            var cams = new FakeCameras(
                new CameraAiCamera(WebcamCam, FactoryA, "CAM-WEB", "Desk webcam", "Active", "LocalWebcam"),
                new CameraAiCamera(WebcamCam2, FactoryA, "CAM-WEB2", "Second webcam", "Active", "LocalWebcam"),
                new CameraAiCamera(RtspCam, FactoryA, "CAM-RTSP", "Line camera", "Active", "Rtsp"),
                new CameraAiCamera(InactiveCam, FactoryA, "CAM-OFF", "Old webcam", "Inactive", "LocalWebcam"));
            Service = new CameraAiSessionService(Options, Analyzer, cams, Sink, Usage, Time, NullLogger<CameraAiSessionService>.Instance);
        }

        public async Task<CameraAiSessionDto> Start(AccessContext who, Guid? camera = null)
        {
            var r = await Service.StartAsync(who, new StartCameraAiSessionRequest(camera ?? WebcamCam), CancellationToken.None);
            Assert.True(r.Ok, $"{r.Status} {r.Code} {r.Message}");
            Assert.Equal(201, r.Status);
            return r.Value!;
        }

        public Task<CameraAiResult<CameraAiFrameResult>> Frame(AccessContext who, Guid session, byte[]? frame = null, string type = "image/jpeg") =>
            Service.AnalyzeFrameAsync(who, session, frame ?? Jpeg(), type, CancellationToken.None);

        /// <summary>A frame one interval later.</summary>
        public async Task<CameraAiFrameResult> Next(AccessContext who, Guid session)
        {
            Time.Advance(TimeSpan.FromMilliseconds(Options.MinIntervalMs));
            var r = await Frame(who, session);
            Assert.True(r.Ok, $"{r.Status} {r.Code} {r.Message}");
            return r.Value!;
        }
    }

    [Fact]
    public async Task Starting_needs_configured_ai_a_visible_active_browser_webcam_camera()
    {
        var rig = new Rig();
        var op = As(R.MonitoringOperator);
        Assert.Equal((404, "camera_not_found"), Code(await rig.Service.StartAsync(op, new(Guid.NewGuid()), default)));
        Assert.Equal((409, "camera_not_active"), Code(await rig.Service.StartAsync(op, new(InactiveCam), default)));
        Assert.Equal((409, "camera_not_browser_webcam"), Code(await rig.Service.StartAsync(op, new(RtspCam), default)));
        rig.Analyzer.Configured = false;
        Assert.Equal((503, "ai_not_configured"), Code(await rig.Service.StartAsync(op, new(WebcamCam), default)));
        rig.Analyzer.Configured = true;
        var s = await rig.Start(op);
        Assert.Equal(("ACTIVE", 1000, "gpt-5.4-mini"), (s.Status, s.IntervalMs, s.Model));
        Assert.True(s.IsMine);
    }

    [Fact]
    public async Task One_session_per_camera_and_the_same_user_replaces_their_own()
    {
        var rig = new Rig();
        var userId = Guid.NewGuid();
        var me = As(R.MonitoringOperator, userId: userId);
        var first = await rig.Start(me);
        var other = As(R.FactoryManager);
        var busy = await rig.Service.StartAsync(other, new(WebcamCam), default);
        Assert.Equal((409, "camera_session_active"), Code(busy));
        Assert.Contains(me.Email, busy.Message);
        var second = await rig.Start(As(R.MonitoringOperator, userId: userId));   // e.g. a page reload
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("ENDED", rig.Service.Get(me, first.Id).Value!.Status);
        Assert.Equal((404, "session_not_found"), Code(await rig.Frame(me, first.Id)));
    }

    [Fact]
    public async Task Session_count_is_capped()
    {
        var rig = new Rig(new CameraAiOptions { MaxSessions = 1 });
        await rig.Start(As(R.MonitoringOperator));
        Assert.Equal((503, "too_many_sessions"), Code(await rig.Service.StartAsync(As(R.MonitoringOperator), new(WebcamCam2), default)));
    }

    [Fact]
    public async Task Frames_are_validated_and_only_the_owner_may_send_them()
    {
        var rig = new Rig();
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        Assert.Equal((403, "session_not_yours"), Code(await rig.Frame(As(R.MonitoringOperator), s.Id)));
        Assert.Equal((404, "session_not_found"), Code(await rig.Frame(me, Guid.NewGuid())));
        Assert.Equal((400, "frame_type_invalid"), Code(await rig.Frame(me, s.Id, type: "image/png")));
        Assert.Equal((400, "frame_not_jpeg"), Code(await rig.Frame(me, s.Id, TestImages.PngHeader(640, 480))));
        Assert.Equal((413, "frame_too_large"), Code(await rig.Frame(me, s.Id, new byte[rig.Options.MaxFrameBytes + 1])));
        Assert.Equal(0, rig.Analyzer.Calls);                                  // nothing invalid reached OpenAI
        Assert.Equal(3, rig.Service.Get(me, s.Id).Value!.Stats.Rejected);
    }

    [Fact]
    public async Task Frames_faster_than_the_interval_get_429_with_retry_after()
    {
        var rig = new Rig(new CameraAiOptions { MinIntervalMs = 2000, DefaultIntervalMs = 2000 });
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        Assert.True((await rig.Frame(me, s.Id)).Ok);
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        var r = await rig.Frame(me, s.Id);
        Assert.Equal((429, "frame_too_soon"), Code(r));
        Assert.Equal(2, r.RetryAfterSeconds);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1450));                   // within the jitter tolerance
        Assert.True((await rig.Frame(me, s.Id)).Ok);
        Assert.Equal(2, rig.Analyzer.Calls);
    }

    [Fact]
    public async Task Latest_frame_wins_a_frame_sent_while_one_is_analysed_is_refused_not_queued()
    {
        var rig = new Rig();
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        var gate = new TaskCompletionSource();
        rig.Analyzer.Hold = gate.Task;
        var inFlight = rig.Frame(me, s.Id);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal((409, "analysis_in_progress"), Code(await rig.Frame(me, s.Id)));
        gate.SetResult();
        Assert.True((await inFlight).Ok);
        Assert.Equal(1, rig.Analyzer.Calls);
    }

    [Fact]
    public async Task Concurrent_calls_and_the_daily_budget_are_capped()
    {
        var rig = new Rig(new CameraAiOptions { MaxConcurrentCalls = 1, DailyCallLimit = 2 });
        var a = As(R.MonitoringOperator); var b = As(R.MonitoringOperator);
        var sa = await rig.Start(a);
        var sb = await rig.Start(b, WebcamCam2);
        var gate = new TaskCompletionSource();
        rig.Analyzer.Hold = gate.Task;
        var held = rig.Frame(a, sa.Id);
        var busy = await rig.Frame(b, sb.Id);
        Assert.Equal((503, "ai_busy"), Code(busy));
        Assert.Equal(1, busy.RetryAfterSeconds);
        gate.SetResult();
        await held;
        rig.Analyzer.Hold = null;
        await rig.Next(b, sb.Id);                                              // 2nd call of the day
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var over = await rig.Frame(a, sa.Id);
        Assert.Equal((429, "daily_limit_reached"), Code(over));
        Assert.True(over.RetryAfterSeconds > 0);
        Assert.Equal((429, "daily_limit_reached"), Code(await rig.Service.StartAsync(As(R.MonitoringOperator), new(WebcamCam2), default)));
    }

    [Fact]
    public async Task The_daily_budget_starts_from_calls_already_logged_today()
    {
        var rig = new Rig(new CameraAiOptions { DailyCallLimit = 5 });
        rig.Usage.Calls = 5;
        Assert.Equal((429, "daily_limit_reached"), Code(await rig.Service.StartAsync(As(R.MonitoringOperator), new(WebcamCam), default)));
    }

    [Fact]
    public async Task Events_follow_temporal_confirmation_and_carry_ai_provenance()
    {
        var rig = new Rig();
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        rig.Analyzer.Next = Answer(helmet: "missing");
        var f1 = (await rig.Frame(me, s.Id)).Value!;
        Assert.Equal("analysed", f1.Outcome);
        Assert.Empty(f1.Events);
        Assert.Equal(PpeStatus.Missing, f1.Analysis!.Helmet);
        Assert.True(f1.Analysis.BoxesApproximate);
        Assert.Equal((640, 480), (f1.Analysis.FrameWidth, f1.Analysis.FrameHeight));
        var f2 = await rig.Next(me, s.Id);
        var detected = Assert.Single(f2.Events);
        Assert.Equal("PERSON_DETECTED", detected.EventType);
        var f3 = await rig.Next(me, s.Id);                                     // 3rd missing + 3rd STANDING
        Assert.Equal(new[] { "ACTIVITY_CHANGED", "PPE_VIOLATION" }, f3.Events.Select(e => e.EventType));
        for (var i = 0; i < 5; i++) Assert.Empty((await rig.Next(me, s.Id)).Events);   // nothing repeats per frame

        var inputs = rig.Sink.Inputs.ToList();
        Assert.Equal(3, inputs.Count);
        Assert.All(inputs, i =>
        {
            Assert.Equal(("REAL", FactoryA, WebcamCam, "gpt-5.4-mini"), (i.SourceType, i.FactoryId, i.CameraId!.Value, i.ModelName));
            Assert.Null(i.WorkerId);
            Assert.Null(i.Confidence);                                         // no invented confidence
            Assert.StartsWith($"camera-ai:{s.Id:N}:", i.IdempotencyKey);
            var p = i.Payload!.Value;
            Assert.Equal("openai-vision", p.GetProperty("source").GetString());
            Assert.True(p.GetProperty("estimate").GetBoolean());
            Assert.True(MonitoringRules.TryPayload(p, out _, out var err), err);  // passes the pipeline's payload rules
        });
        Assert.Equal(inputs.Count, inputs.Select(i => i.IdempotencyKey).Distinct().Count());
        Assert.Matches("^ai-[0-9a-f]{12}$", inputs[0].TrackId);
        Assert.Equal("helmet not worn", rig.Sink.Inputs.Last().Payload!.Value.GetProperty("reason").GetString());

        var dto = rig.Service.Get(me, s.Id).Value!;
        Assert.True(dto.Confirmed.PersonPresent);
        Assert.Equal("STANDING", dto.Confirmed.Activity);
        Assert.Equal(new[] { "HELMET" }, dto.Confirmed.PpeViolations);
        Assert.Equal(3, dto.RecentEvents.Count);
    }

    [Fact]
    public async Task Failures_are_reported_per_frame_and_become_detection_unavailable_after_three()
    {
        var rig = new Rig();
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        rig.Analyzer.Fail = new CameraVisionException("timeout", "OpenAI did not answer within 20 seconds.", 2, 20_000);
        var f1 = (await rig.Frame(me, s.Id)).Value!;
        Assert.Equal(("failed", "timeout"), (f1.Outcome, f1.Error!.Code));
        await rig.Next(me, s.Id);
        var f3 = await rig.Next(me, s.Id);
        Assert.Equal("DETECTION_UNAVAILABLE", Assert.Single(f3.Events).EventType);
        Assert.Empty((await rig.Next(me, s.Id)).Events);
        rig.Analyzer.Fail = null;
        rig.Analyzer.Next = Answer(presence: "absent", count: 0);
        Assert.Equal("MODEL_STATUS_CHANGED", Assert.Single((await rig.Next(me, s.Id)).Events).EventType);

        rig.Analyzer.Next = new JsonObject { ["person_presence"] = "maybe" };   // outside the contract
        var bad = await rig.Next(me, s.Id);
        Assert.Equal(("failed", "invalid_output"), (bad.Outcome, bad.Error!.Code));
        Assert.Equal(5, bad.Session.Stats.Failed);
    }

    [Fact]
    public async Task Silence_reports_the_camera_offline_once_then_online_and_idle_sessions_end()
    {
        var rig = new Rig();
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        await rig.Frame(me, s.Id);
        await rig.Next(me, s.Id);                                              // person confirmed
        rig.Time.Advance(TimeSpan.FromSeconds(16));
        await rig.Service.SweepAsync(default);
        await rig.Service.SweepAsync(default);
        Assert.Equal("OFFLINE", rig.Service.Get(me, s.Id).Value!.Status);
        var offline = rig.Sink.Inputs.Where(i => i.EventType == "CAMERA_STATUS_CHANGED").ToList();
        Assert.Equal("OFFLINE", Assert.Single(offline).Payload!.Value.GetProperty("toStatus").GetString());
        var back = (await rig.Frame(me, s.Id)).Value!;
        Assert.Equal("CAMERA_STATUS_CHANGED", back.Events.First().EventType);
        Assert.Equal("ONLINE", rig.Sink.Inputs.Last(i => i.EventType == "CAMERA_STATUS_CHANGED").Payload!.Value.GetProperty("toStatus").GetString());
        Assert.Equal("ACTIVE", back.Session.Status);

        rig.Time.Advance(TimeSpan.FromMinutes(3));
        await rig.Service.SweepAsync(default);
        var ended = rig.Service.Get(me, s.Id).Value!;
        Assert.Equal(("ENDED", "idle"), (ended.Status, ended.EndReason));
        Assert.Equal("PERSON_LOST", rig.Sink.Inputs.Last().EventType);
        rig.Time.Advance(TimeSpan.FromMinutes(11));
        await rig.Service.SweepAsync(default);
        Assert.Equal(404, rig.Service.Get(me, s.Id).Status);                   // forgotten after the retention
    }

    [Fact]
    public async Task Sessions_end_at_their_maximum_length()
    {
        var rig = new Rig(new CameraAiOptions { MaxSessionDuration = TimeSpan.FromMinutes(1) });
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal((409, "session_expired"), Code(await rig.Frame(me, s.Id)));
        Assert.Equal("max_duration", rig.Service.Get(me, s.Id).Value!.EndReason);
    }

    [Fact]
    public async Task Stop_reports_a_confirmed_person_lost_is_idempotent_and_limited_to_owner_or_camera_manager()
    {
        var rig = new Rig();
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        await rig.Frame(me, s.Id); await rig.Next(me, s.Id);
        Assert.Equal((403, "session_not_yours"), Code(await rig.Service.StopAsync(As(R.FactoryManager), s.Id, null)));
        Assert.Equal((404, "session_not_found"), Code(await rig.Service.StopAsync(As(R.MonitoringOperator, Guid.NewGuid()), s.Id, null)));
        var stopped = await rig.Service.StopAsync(me, s.Id, null);
        Assert.Equal(("ENDED", "stopped"), (stopped.Value!.Status, stopped.Value.EndReason));
        Assert.Equal("PERSON_LOST", rig.Sink.Inputs.Last().EventType);
        var count = rig.Sink.Inputs.Count;
        Assert.True((await rig.Service.StopAsync(me, s.Id, null)).Ok);
        Assert.Equal(count, rig.Sink.Inputs.Count);                           // no second PERSON_LOST
        Assert.Equal((404, "session_not_found"), Code(await rig.Frame(me, s.Id)));

        var s2 = await rig.Start(As(R.MonitoringOperator));
        Assert.True((await rig.Service.StopAsync(As(R.FactoryAdmin), s2.Id, "Demo over")).Ok);   // camera manager of the factory
    }

    [Fact]
    public async Task Usage_and_cost_are_tracked_per_session_and_today()
    {
        var rig = new Rig();
        var me = As(R.MonitoringOperator);
        var s = await rig.Start(me);
        await rig.Frame(me, s.Id);
        await rig.Next(me, s.Id);
        var stats = rig.Service.Get(me, s.Id).Value!.Stats;
        Assert.Equal((2, 2, 0), (stats.FramesReceived, stats.Analysed, stats.Failed));
        Assert.Equal((2000L, 300L), (stats.InputTokens, stats.OutputTokens));    // 1000 + 150 per call
        Assert.Equal(2000m / 1_000_000m * 1m + 300m / 1_000_000m * 2m, stats.EstimatedCostUsd);
        Assert.Equal(40, stats.LastLatencyMs);

        rig.Usage.Calls = 2; rig.Usage.In = 2000; rig.Usage.Out = 300;
        var overview = await rig.Service.OverviewAsync(me, default);
        Assert.True(overview.Configured);
        Assert.Equal(("openai", "gpt-5.4-mini"), (overview.Provider, overview.Model));
        Assert.Equal(2, overview.UsageToday.Calls);
        Assert.True(overview.UsageToday.PricesConfigured);
        Assert.Single(overview.Sessions);
        Assert.Contains("not", overview.Disclaimer);
        // Someone without access to the factory doesn't see the session.
        Assert.Empty((await rig.Service.OverviewAsync(As(R.MonitoringOperator, Guid.NewGuid()), default)).Sessions);
    }

    private static (int, string?) Code<T>(CameraAiResult<T> r) => (r.Status, r.Code);

    // ─── Fakes ─────────────────────────────────────────────────────────────

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeAnalyzer : ICameraVisionAnalyzer
    {
        public bool Configured { get; set; } = true;
        public JsonObject Next { get; set; } = Answer();
        public CameraVisionException? Fail { get; set; }
        public Task? Hold { get; set; }
        public int Calls;
        public CameraAiStatus Status() => new(Configured, "gpt-5.4-mini", Configured ? "test" : null, Configured ? null : "No OpenAI key.");
        public async Task<VisionCallResult> AnalyzeAsync(byte[] jpeg, VisionCallContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Hold is { } h) await h;
            if (Fail is { } f) throw f;
            return new VisionCallResult((JsonObject)Next.DeepClone(), "gpt-5.4-mini", 1, 1000, 150, 40);
        }
    }

    private sealed class FakeCameras : ICameraAiCameraLookup
    {
        private readonly Dictionary<Guid, CameraAiCamera> _c;
        public FakeCameras(params CameraAiCamera[] cams) { _c = cams.ToDictionary(c => c.Id); }
        public Task<CameraAiCamera?> FindInScopeAsync(AccessContext caller, Guid cameraId, CancellationToken ct) =>
            Task.FromResult(_c.TryGetValue(cameraId, out var c) && caller.CanAccessFactory(c.FactoryId) ? c : null);
    }

    private sealed class FakeSink : ICameraAiEventSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<MonitoringEventInput> Inputs { get; } = new();
        public Task<CameraAiEventDto> EmitAsync(MonitoringEventInput input, CancellationToken ct)
        {
            Inputs.Enqueue(input);
            return Task.FromResult(new CameraAiEventDto(Guid.NewGuid(), input.EventType, input.EventType, input.OccurredAt!.Value, true, null, null));
        }
    }

    private sealed class FakeUsage : ICameraAiUsageStore
    {
        public int Calls; public long In, Out;
        public Task<CameraAiUsageToday> TodayAsync(DateTimeOffset now, CancellationToken ct) => Task.FromResult(new CameraAiUsageToday(Calls, 0, In, Out));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _steps;
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = new();
        public List<string> Authorizations { get; } = new();
        public List<string> Uris { get; } = new();
        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) { _steps = steps; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            Uris.Add(request.RequestUri!.ToString());
            var step = _steps.Length == 0 ? (_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)) : _steps[Math.Min(Calls, _steps.Length - 1)];
            Calls++;
            return step(request);
        }
    }

    private sealed class DelayHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public DelayHandler(TimeSpan delay) { _delay = delay; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public SingleClientFactory(HttpMessageHandler h) { _h = h; }
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    /// <summary>Settings → AI without an OpenAI key (camera AI must then use CAMERA_AI_OPENAI_API_KEY).</summary>
    private sealed class NoAiSettings : IAiSettingsProvider
    {
        private readonly AiSettingsSnapshot _snap = new(new LlmSettings(null, null, null, SecretStatus.NotSet, AiSettingsSource.Default),
            new EmbeddingSettings("local", "hash-md5", 384, null, SecretStatus.NotSet, AiSettingsSource.Default), 1, null, null);
        public ValueTask<AiSettingsSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(_snap);
        public AiSettingsSnapshot GetCurrent() => _snap;
        public IDisposable BeginOverride(AiSettingsOverride settingsOverride) => throw new NotSupportedException();
        public StartupAiConfig Startup => StartupAiConfig.Empty;
        public void Invalidate() { }
    }
}
