using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// The inspection workflow on a real PostgreSQL + pgvector database
/// (migrations applied by the API's own startup). The vision provider is the
/// http provider pointed at <see cref="DbApiFactory.FakeDetectorHost"/>, a
/// deterministic TEST detector answered in-process: these tests verify the
/// workflow, persistence and authorization, not any real model's accuracy.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbQcInspectionTests
{
    private readonly PostgresStack _stack;
    public DbQcInspectionTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Qc;

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static MultipartFormDataContent Form(byte[] image, string product, string stage = "end_of_line", string? line = null,
        string? order = null, string[]? categories = null, string fileName = "shot.png", string contentType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "image", fileName);
        form.Add(new StringContent(product), "productType");
        form.Add(new StringContent(stage), "stage");
        if (line is not null) form.Add(new StringContent(line), "lineId");
        if (order is not null) form.Add(new StringContent(order), "productionOrder");
        foreach (var c in categories ?? Array.Empty<string>()) form.Add(new StringContent(c), "categoryCodes");
        return form;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> Submit(
        HttpClient client, MultipartFormDataContent form, string? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/qc/inspections") { Content = form };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        var res = await client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), res);
    }

    private static async Task<JsonElement> Created(HttpClient client, MultipartFormDataContent form)
    {
        var (status, body, _) = await Submit(client, form);
        Assert.True(status == HttpStatusCode.Created, $"create → {(int)status}: {body}");
        return body;
    }

    private static async Task ProcessAll(DbApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IQcInspectionProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessNextAsync(CancellationToken.None); i++) { }
    }

    private static async Task<JsonElement> Get(HttpClient client, string id)
    {
        using var res = await client.GetAsync($"/api/qc/inspections/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Review(HttpClient client, string id, object body)
    {
        using var res = await client.PostAsync($"/api/qc/inspections/{id}/review", Rag.Json(body));
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static JsonElement Finding(JsonElement detail, string rawLabel) =>
        detail.GetProperty("findings").EnumerateArray().Single(f => f.TryGetProperty("rawLabel", out var l) && l.GetString() == rawLabel);

    private static Task<string[]> AuditActions(DbApiFactory api, string id) =>
        Rag.WithDb(api, db => db.AuditLog.Where(a => a.EntityType == "qc_inspection" && a.EntityId == id)
            .OrderBy(a => a.Id).Select(a => a.Action).ToArrayAsync());

    // ── Submission, analysis, persistence ───────────────────────────────────

    [DockerFact]
    public async Task Operator_submits_an_image_and_the_detector_result_is_persisted_and_returned()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var image = TestImages.Png(32, 24);
        var product = Unique("Polo");
        var created = await Created(op, Form(image, product, "end_of_line", "line-3", "PO-2026/114",
            new[] { "skipped_stitch", "hole", "stain" }, fileName: "../../etc/shot.png"));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("pending", created.GetProperty("status").GetString());
        Assert.Equal("shot.png", created.GetProperty("image").GetProperty("originalFileName").GetString());
        Assert.Equal((32, 24), (created.GetProperty("image").GetProperty("width").GetInt32(), created.GetProperty("image").GetProperty("height").GetInt32()));

        await ProcessAll(Api);
        var detail = await Get(op, id);
        Assert.Equal("completed", detail.GetProperty("status").GetString());
        var analysis = detail.GetProperty("analysis");
        Assert.Equal("http", analysis.GetProperty("provider").GetString());
        Assert.Equal(DbApiFactory.FakeDetectorModel, analysis.GetProperty("model").GetString());
        Assert.Equal("t1", analysis.GetProperty("modelVersion").GetString());
        Assert.Equal(0.25, analysis.GetProperty("confidenceThreshold").GetDouble());
        Assert.True(analysis.GetProperty("localizationSupported").GetBoolean());

        // Exactly what the detector returned, after validation: the 0.10 "stain" is under the threshold.
        var findings = detail.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(3, findings.Count);
        Assert.All(findings, f => Assert.Equal("unreviewed", f.GetProperty("decision").GetString()));
        var stitch = Finding(detail, "skipped_stitch");
        Assert.Equal("skipped_stitch", stitch.GetProperty("categoryCode").GetString());
        Assert.Equal(0.91, stitch.GetProperty("confidence").GetDouble());
        Assert.Equal(0.10, stitch.GetProperty("box").GetProperty("x").GetDouble(), 5);
        Assert.Equal(0.30, stitch.GetProperty("box").GetProperty("width").GetDouble(), 5);
        Assert.Equal(3, stitch.GetProperty("polygon").GetArrayLength());
        Assert.False(Finding(detail, "weird_mark").TryGetProperty("categoryCode", out _));   // unmapped: no invented category
        Assert.False(Finding(detail, "hole").TryGetProperty("box", out _));                  // invalid box dropped, defect kept

        // The detector got the taxonomy, the context and the key; nothing else left the process.
        var (sent, auth) = Api.DetectorRequests.Last(r => r.Body.Contains(product));
        Assert.Equal($"Bearer {DbApiFactory.FakeDetectorKey}", auth);
        using (var doc = JsonDocument.Parse(sent))
        {
            Assert.Equal("end_of_line", doc.RootElement.GetProperty("stage").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("categories").GetArrayLength());
            Assert.Equal(Convert.ToBase64String(image), doc.RootElement.GetProperty("image").GetProperty("base64").GetString());
        }
        Assert.Empty(Api.Http.Blocked);

        // Only a storage reference is in the database; the file is under the store root.
        var row = await Rag.WithDb(Api, db => db.QcInspections.AsNoTracking().FirstAsync(i => i.Id == Guid.Parse(id)));
        Assert.Matches(@"^\d{4}/\d{2}/[0-9a-f]{32}\.png$", row.ImageStorageKey);
        Assert.True(File.Exists(Path.Combine(Api.QcStoragePath, row.ImageStorageKey)));

        Assert.Equal(new[] { "qc_inspection.created", "qc_inspection.analyzed" }, await AuditActions(Api, id));
    }

    [DockerFact]
    public async Task Stored_image_is_served_only_to_signed_in_users_with_safe_headers()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var image = TestImages.Png(16, 16);
        var id = (await Created(op, Form(image, Unique("Tee")))).GetProperty("id").GetString()!;

        using var viewer = Rag.Client(Api, Roles.Viewer);
        using var res = await viewer.GetAsync($"/api/qc/inspections/{id}/image");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(image, await res.Content.ReadAsByteArrayAsync());

        using var anon = Api.RawClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/qc/inspections/{id}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/qc/inspections/{Guid.NewGuid()}/image")).StatusCode);
    }

    [DockerFact]
    public async Task Unsafe_or_invalid_uploads_are_rejected_and_nothing_is_stored()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var product = Unique("Bad");
        var filesBefore = Directory.Exists(Api.QcStoragePath) ? Directory.GetFiles(Api.QcStoragePath, "*", SearchOption.AllDirectories).Length : 0;

        var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        var (status, body, _) = await Submit(op, Form(html, product, fileName: "evil.png", contentType: "image/png"));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("JPEG, PNG and WebP", body.GetProperty("errors").GetProperty("image")[0].GetString());

        (status, body, _) = await Submit(op, Form(TestImages.Png(4, 4), product, categories: new[] { "not_a_category" }));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("categoryCodes", out _));

        (status, body, _) = await Submit(op, Form(TestImages.Png(4, 4), product, stage: "packing", line: "line-1;DROP"));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("stage", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("lineId", out _));

        Assert.False(await Rag.WithDb(Api, db => db.QcInspections.AnyAsync(i => i.ProductType == product)));
        var filesAfter = Directory.Exists(Api.QcStoragePath) ? Directory.GetFiles(Api.QcStoragePath, "*", SearchOption.AllDirectories).Length : 0;
        Assert.Equal(filesBefore, filesAfter);
    }

    [DockerFact]
    public async Task Idempotency_key_replays_the_same_inspection_and_refuses_a_different_image()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var key = Guid.NewGuid().ToString();
        var image = TestImages.Png(10, 10);
        var product = Unique("Idem");

        var first = await Submit(op, Form(image, product), key);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        var replay = await Submit(op, Form(image, product), key);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Equal("true", replay.Response.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(first.Body.GetProperty("id").GetString(), replay.Body.GetProperty("id").GetString());

        var other = await Submit(op, Form(TestImages.Png(11, 10), product), key);
        Assert.Equal(HttpStatusCode.Conflict, other.Status);

        // Concurrent double-submit with a fresh key: still exactly one row.
        var key2 = Guid.NewGuid().ToString();
        var racing = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Submit(op, Form(image, product), key2)));
        Assert.All(racing, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)r.Status}"));
        Assert.Single(racing.Select(r => r.Body.GetProperty("id").GetString()).Distinct());
        Assert.Equal(2, await Rag.WithDb(Api, db => db.QcInspections.CountAsync(i => i.ProductType == product)));
    }

    // ── Authorization ───────────────────────────────────────────────────────

    [DockerFact]
    public async Task Viewer_reads_but_cannot_submit_review_or_retry()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var id = (await Created(op, Form(TestImages.Png(8, 8), Unique("Auth")))).GetProperty("id").GetString()!;
        await ProcessAll(Api);

        using var viewer = Rag.Client(Api, Roles.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/qc/inspections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/qc/inspections")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Submit(viewer, Form(TestImages.Png(8, 8), Unique("Auth")))).Status);
        var version = (await Get(viewer, id)).GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await Review(viewer, id, new { expectedVersion = version, outcome = "pass" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/qc/inspections/{id}/retry", null)).StatusCode);
        Assert.Equal("completed", (await Get(viewer, id)).GetProperty("status").GetString()); // unchanged
    }

    // ── Review ──────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Inspector_review_persists_decisions_corrections_and_additions_without_touching_the_prediction()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var id = (await Created(op, Form(TestImages.Png(20, 20), Unique("Review"), line: Unique("L")))).GetProperty("id").GetString()!;
        await ProcessAll(Api);
        var detail = await Get(op, id);
        var version = detail.GetProperty("version").GetUInt32();
        string F(string label) => Finding(detail, label).GetProperty("id").GetString()!;

        // Leaving a prediction undecided is refused.
        var (status, body) = await Review(op, id, new
        {
            expectedVersion = version, outcome = "rework",
            decisions = new[] { new { findingId = F("skipped_stitch"), decision = "accepted" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Decide every model prediction", body.GetProperty("errors").GetProperty("decisions")[0].GetString());

        // An unmapped prediction can't be accepted as-is.
        (status, _) = await Review(op, id, new
        {
            expectedVersion = version, outcome = "rework",
            decisions = new object[]
            {
                new { findingId = F("skipped_stitch"), decision = "accepted" },
                new { findingId = F("weird_mark"), decision = "accepted" },
                new { findingId = F("hole"), decision = "rejected" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, body) = await Review(op, id, new
        {
            expectedVersion = version, outcome = "rework", notes = "Re-stitch shoulder seam",
            decisions = new object[]
            {
                new { findingId = F("skipped_stitch"), decision = "accepted" },
                new { findingId = F("weird_mark"), decision = "corrected", correctedCategoryCode = "stain", correctedSeverity = "minor", note = "chalk, washable" },
                new { findingId = F("hole"), decision = "rejected", note = "shadow" },
            },
            additions = new[] { new { categoryCode = "loose_thread", severity = "minor", box = new { x = 0.5, y = 0.5, width = 0.1, height = 0.1 } } },
        });
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        Assert.Equal("reviewed", body.GetProperty("status").GetString());
        Assert.Equal("rework", body.GetProperty("review").GetProperty("outcome").GetString());

        var corrected = Finding(body, "weird_mark");
        Assert.Equal("corrected", corrected.GetProperty("decision").GetString());
        Assert.Equal("stain", corrected.GetProperty("effectiveCategoryCode").GetString());
        Assert.Equal("minor", corrected.GetProperty("effectiveSeverity").GetString());
        Assert.Equal(0.60, corrected.GetProperty("confidence").GetDouble());          // the model's score is kept
        Assert.True(corrected.GetProperty("verified").GetBoolean());
        Assert.False(Finding(body, "hole").GetProperty("verified").GetBoolean());
        var added = body.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("source").GetString() == "inspector");
        Assert.Equal("loose_thread", added.GetProperty("categoryCode").GetString());
        Assert.False(added.TryGetProperty("confidence", out _));                      // inspector findings have no model score

        // The stored original prediction is untouched.
        var weirdId = Guid.Parse(F("weird_mark"));
        var stored = await Rag.WithDb(Api, db => db.QcInspectionFindings.AsNoTracking().SingleAsync(f => f.Id == weirdId));
        Assert.Equal(("weird_mark", (string?)null, 0.60), (stored.RawLabel, stored.CategoryCode, stored.Confidence!.Value));

        // The old version is stale now.
        (status, _) = await Review(op, id, new { expectedVersion = version, outcome = "pass" });
        Assert.Equal(HttpStatusCode.Conflict, status);

        // A corrected review with the new version is allowed and audited again.
        (status, body) = await Review(op, id, new
        {
            expectedVersion = body.GetProperty("version").GetUInt32(), outcome = "reject", notes = "Second look: seam open",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("reject", body.GetProperty("review").GetProperty("outcome").GetString());
        Assert.Equal(new[] { "qc_inspection.created", "qc_inspection.analyzed", "qc_inspection.reviewed", "qc_inspection.reviewed" },
            await AuditActions(Api, id));
    }

    [DockerFact]
    public async Task Concurrent_reviews_of_the_same_version_produce_exactly_one_decision()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var id = (await Created(op, Form(TestImages.Png(8, 8), $"{Unique("Race")} {DbApiFactory.ForceDetectorClean}"))).GetProperty("id").GetString()!;
        await ProcessAll(Api);
        var version = (await Get(op, id)).GetProperty("version").GetUInt32();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Review(op, id, new { expectedVersion = version, outcome = "pass", notes = $"reviewer {i}" })));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.Status));
        Assert.Single(await AuditActions(Api, id), a => a == "qc_inspection.reviewed");
    }

    [DockerFact]
    public async Task Review_before_analysis_finishes_is_refused()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var created = await Created(op, Form(TestImages.Png(8, 8), Unique("Early")));
        var (status, _) = await Review(op, created.GetProperty("id").GetString()!,
            new { expectedVersion = created.GetProperty("version").GetUInt32(), outcome = "pass" });
        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    [DockerFact]
    public async Task Follow_up_goes_through_the_existing_approval_queue()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var id = (await Created(op, Form(TestImages.Png(8, 8), $"{Unique("Follow")} {DbApiFactory.ForceDetectorClean}", line: "line-6"))).GetProperty("id").GetString()!;
        await ProcessAll(Api);
        var version = (await Get(op, id)).GetProperty("version").GetUInt32();

        var (status, body) = await Review(op, id, new
        {
            expectedVersion = version, outcome = "reject",
            additions = new[] { new { categoryCode = "hole", severity = "critical" } },
            requestFollowUp = true, followUpNote = "Check needle plate on line 6",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        var followUp = body.GetProperty("followUp");
        Assert.Equal(("pending_approval", "high"), (followUp.GetProperty("status").GetString(), followUp.GetProperty("riskTier").GetString()));

        var approvalId = followUp.GetProperty("approvalId").GetString()!;
        using var approvals = JsonDocument.Parse(await op.GetStringAsync("/api/approvals?status=pending&limit=200"));
        var approval = approvals.RootElement.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("id").GetString() == approvalId);
        Assert.Contains("Check needle plate", approval.GetProperty("insight").GetProperty("finding").GetString());
        Assert.Contains("qc_inspection.follow_up_requested", await AuditActions(Api, id));

        // High risk: an Operator can't approve it, an Admin can (existing approval rules).
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync($"/api/approvals/{approvalId}/approve", Rag.Json(new { }))).StatusCode);
        using var admin = Rag.Client(Api, Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/approvals/{approvalId}/approve", Rag.Json(new { }))).StatusCode);
        Assert.Equal("approved", (await Get(op, id)).GetProperty("followUp").GetProperty("status").GetString());
    }

    // ── Failures and retry ──────────────────────────────────────────────────

    [DockerFact]
    public async Task Provider_failure_is_recorded_without_predictions_and_can_be_retried()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var id = (await Created(op, Form(TestImages.Png(8, 8), Unique("Retry")))).GetProperty("id").GetString()!;
        Api.DetectorDown = true;
        try { await ProcessAll(Api); }
        finally { Api.DetectorDown = false; }

        var failed = await Get(op, id);
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal("provider_error", failed.GetProperty("analysis").GetProperty("failureCode").GetString());
        Assert.Contains("HTTP 503", failed.GetProperty("analysis").GetProperty("failureMessage").GetString());
        Assert.True(failed.GetProperty("analysis").GetProperty("retryable").GetBoolean());
        Assert.Empty(failed.GetProperty("findings").EnumerateArray());

        using var retry = await op.PostAsync($"/api/qc/inspections/{id}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await ProcessAll(Api);
        var done = await Get(op, id);
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal(3, done.GetProperty("findings").GetArrayLength());

        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsync($"/api/qc/inspections/{id}/retry", null)).StatusCode);
        Assert.Equal(new[] { "qc_inspection.created", "qc_inspection.failed", "qc_inspection.retried", "qc_inspection.analyzed" },
            await AuditActions(Api, id));
    }

    [DockerFact]
    public async Task Invalid_provider_output_fails_and_a_clean_result_stores_no_findings()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var invalid = (await Created(op, Form(TestImages.Png(8, 8), $"{Unique("Inv")} {DbApiFactory.ForceDetectorInvalid}"))).GetProperty("id").GetString()!;
        var clean = (await Created(op, Form(TestImages.Png(8, 8), $"{Unique("Clean")} {DbApiFactory.ForceDetectorClean}"))).GetProperty("id").GetString()!;
        await ProcessAll(Api);

        var inv = await Get(op, invalid);
        Assert.Equal(("failed", "invalid_output"), (inv.GetProperty("status").GetString(), inv.GetProperty("analysis").GetProperty("failureCode").GetString()));
        Assert.Empty(inv.GetProperty("findings").EnumerateArray());

        var ok = await Get(op, clean);
        Assert.Equal("completed", ok.GetProperty("status").GetString());
        Assert.Empty(ok.GetProperty("findings").EnumerateArray());
    }

    [DockerFact]
    public async Task Without_a_configured_provider_inspections_fail_honestly_and_can_still_be_inspected_manually()
    {
        var local = _stack.Local; // no QC_VISION_PROVIDER
        using var op = Rag.Client(local, Roles.Operator);
        using var status = JsonDocument.Parse(await op.GetStringAsync("/api/qc/vision/status"));
        Assert.False(status.RootElement.GetProperty("configured").GetBoolean());

        var created = await Created(op, Form(TestImages.Png(8, 8), Unique("NoAi")));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("failed", created.GetProperty("status").GetString());
        Assert.Equal("provider_not_configured", created.GetProperty("analysis").GetProperty("failureCode").GetString());
        Assert.Empty(created.GetProperty("findings").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsync($"/api/qc/inspections/{id}/retry", null)).StatusCode);

        await ProcessAll(local);
        Assert.Empty((await Get(op, id)).GetProperty("findings").EnumerateArray()); // the worker invents nothing either

        var (code, body) = await Review(op, id, new
        {
            expectedVersion = created.GetProperty("version").GetUInt32(), outcome = "rework",
            additions = new[] { new { categoryCode = "stain" } },
        });
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("reviewed", body.GetProperty("status").GetString());
        Assert.DoesNotContain(local.Http.Handled, c => c.Uri.Host != DbApiFactory.FakeRagHost);
    }

    // ── History and metrics ─────────────────────────────────────────────────

    [DockerFact]
    public async Task History_filters_searches_and_paginates_persisted_records()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var line = Unique("LN");
        var order = Unique("PO");
        for (var i = 0; i < 5; i++)
            await Created(op, Form(TestImages.Png(8, 8), $"Hist {DbApiFactory.ForceDetectorClean}", stage: i < 3 ? "inline" : "final", line: line,
                order: i == 4 ? order : null));
        await ProcessAll(Api);

        using var page1 = JsonDocument.Parse(await op.GetStringAsync($"/api/qc/inspections?lineId={line}&pageSize=2&page=1"));
        Assert.Equal((5, 3, 2), (page1.RootElement.GetProperty("total").GetInt32(), page1.RootElement.GetProperty("totalPages").GetInt32(),
            page1.RootElement.GetProperty("items").GetArrayLength()));
        using var page3 = JsonDocument.Parse(await op.GetStringAsync($"/api/qc/inspections?lineId={line}&pageSize=2&page=3"));
        Assert.Equal(1, page3.RootElement.GetProperty("items").GetArrayLength());

        using var inline = JsonDocument.Parse(await op.GetStringAsync($"/api/qc/inspections?lineId={line}&stage=inline&status=completed"));
        Assert.Equal(3, inline.RootElement.GetProperty("total").GetInt32());

        using var search = JsonDocument.Parse(await op.GetStringAsync($"/api/qc/inspections?q={order.ToLowerInvariant()}"));
        var hit = Assert.Single(search.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(order, hit.GetProperty("productionOrder").GetString());

        // LIKE wildcards in the search text are literal.
        using var wildcard = JsonDocument.Parse(await op.GetStringAsync("/api/qc/inspections?q=%25%25%25"));
        Assert.Equal(0, wildcard.RootElement.GetProperty("total").GetInt32());
    }

    [DockerFact]
    public async Task Quality_summary_is_calculated_from_the_persisted_reviews()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var product = Unique("Summary");
        var line = Unique("SL");
        var a = (await Created(op, Form(TestImages.Png(8, 8), product, line: line))).GetProperty("id").GetString()!;
        var b = (await Created(op, Form(TestImages.Png(9, 8), product, line: line))).GetProperty("id").GetString()!;
        await Created(op, Form(TestImages.Png(10, 8), product, line: line)); // stays awaiting review
        await ProcessAll(Api);

        async Task ReviewAll(string id, string outcome, string decision)
        {
            var d = await Get(op, id);
            var decisions = d.GetProperty("findings").EnumerateArray().Select(f => new
            {
                findingId = f.GetProperty("id").GetString(),
                decision = f.GetProperty("rawLabel").GetString() == "weird_mark" ? "rejected" : decision,
            }).ToArray();
            var (status, body) = await Review(op, id, new { expectedVersion = d.GetProperty("version").GetUInt32(), outcome, decisions, notes = "n" });
            Assert.True(status == HttpStatusCode.OK, body.ToString());
        }
        await ReviewAll(a, "rework", "accepted"); // skipped_stitch + hole verified
        await ReviewAll(b, "pass", "rejected");   // nothing verified

        using var doc = JsonDocument.Parse(await op.GetStringAsync($"/api/qc/summary?days=1&productType={product}"));
        var t = doc.RootElement.GetProperty("totals");
        Assert.Equal((3, 2, 1, 2), (t.GetProperty("inspections").GetInt32(), t.GetProperty("reviewed").GetInt32(),
            t.GetProperty("awaitingReview").GetInt32(), t.GetProperty("verifiedDefects").GetInt32()));
        Assert.Equal(50.0, t.GetProperty("defectiveRatePct").GetDouble());
        Assert.Equal(100.0, t.GetProperty("dhu").GetDouble());
        var model = doc.RootElement.GetProperty("model");
        Assert.Equal((6, 2, 4), (model.GetProperty("predictionsReviewed").GetInt32(), model.GetProperty("accepted").GetInt32(),
            model.GetProperty("rejected").GetInt32()));
        var byLine = Assert.Single(doc.RootElement.GetProperty("byLine").EnumerateArray());
        Assert.Equal(line, byLine.GetProperty("lineId").GetString());
        Assert.True(doc.RootElement.GetProperty("definitions").TryGetProperty("dhu", out _));
    }

    // ── Taxonomy and schema ─────────────────────────────────────────────────

    [DockerFact]
    public async Task Admin_manages_the_taxonomy_with_audit_and_concurrency_checks()
    {
        using var admin = Rag.Client(Api, Roles.Admin);
        var code = "t_" + Guid.NewGuid().ToString("N")[..10];
        using var created = await admin.PostAsync("/api/qc/categories", Rag.Json(new { code, name = $"Test {code}", defaultSeverity = "minor" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var version = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetUInt32();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/qc/categories", Rag.Json(new { code, name = "Other" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/qc/categories", Rag.Json(new { code = "Bad Code", name = "x" }))).StatusCode);

        using var deactivated = await admin.PutAsync($"/api/qc/categories/{code}", Rag.Json(new { expectedVersion = version, isActive = false }));
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync($"/api/qc/categories/{code}", Rag.Json(new { expectedVersion = version, name = "Stale" }))).StatusCode);

        using var viewer = Rag.Client(Api, Roles.Viewer);
        Assert.DoesNotContain(code, await viewer.GetStringAsync("/api/qc/categories"));
        Assert.Contains(code, await viewer.GetStringAsync("/api/qc/categories?includeInactive=true"));
        // An inactive category can't be requested for a new inspection.
        using var op = Rag.Client(Api, Roles.Operator);
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(op, Form(TestImages.Png(4, 4), Unique("Inactive"), categories: new[] { code }))).Status);

        Assert.Equal(new[] { "qc_category.created", "qc_category.updated" },
            await Rag.WithDb(Api, db => db.AuditLog.Where(x => x.EntityType == "qc_defect_category" && x.EntityId == code)
                .OrderBy(x => x.Id).Select(x => x.Action).ToArrayAsync()));
    }

    [DockerFact]
    public async Task Migration_creates_the_schema_with_its_constraints_and_seeds_the_taxonomy()
    {
        var migrations = await Rag.Strings(Api, "select \"MigrationId\" from \"__ef_migrations\"");
        Assert.Contains(migrations, m => m.EndsWith("_AddQcInspections"));
        var tables = await Rag.Strings(Api, "select tablename from pg_tables where tablename like 'qc\\_%' escape '\\' order by 1");
        Assert.Equal(new[] { "qc_defect_categories", "qc_defect_weeks", "qc_defects", "qc_inspection_findings", "qc_inspections" }, tables);
        var checks = await Rag.Strings(Api, "select conname from pg_constraint where conname like 'CK_qc%' order by 1");
        Assert.Contains("CK_qc_findings_confidence", checks);
        Assert.Contains("CK_qc_inspections_review", checks);
        Assert.True(await Rag.WithDb(Api, db => db.QcDefectCategories.CountAsync()) >= 13);

        // The database itself rejects an out-of-range confidence, whatever the caller.
        using var op = Rag.Client(Api, Roles.Operator);
        var id = (await Created(op, Form(TestImages.Png(4, 4), $"{Unique("Ck")} {DbApiFactory.ForceDetectorClean}"))).GetProperty("id").GetString()!;
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.Strings(Api,
            $"insert into qc_inspection_findings (\"Id\",\"InspectionId\",\"Source\",\"RawLabel\",\"Confidence\",\"Severity\",\"Decision\",\"CreatedAt\") " +
            $"values (gen_random_uuid(),'{id}','Model','x',1.7,'Minor','Unreviewed',now()) returning 1"));
        Assert.Contains("CK_qc_findings_confidence", ex.Message);
    }
}
