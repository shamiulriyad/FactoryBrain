using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// The merchandising workflow on a real PostgreSQL + pgvector database
/// (migrations applied by the API's own startup). Extraction runs through the
/// real LLM provider code against <see cref="DbApiFactory.FakeExtractorHost"/>,
/// a scripted TEST model answered in-process: these tests verify the
/// workflow, validation, evidence checks, persistence and authorization —
/// not any real model's accuracy. <c>MerchManual</c> is a host with
/// automatic extraction turned off.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbMerchandisingTests
{
    private readonly PostgresStack _stack;
    public DbMerchandisingTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Merch;

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> Send(HttpClient c, HttpMethod m, string url, object? body = null)
    {
        using var req = new HttpRequestMessage(m, url) { Content = body is null ? null : Rag.Json(body) };
        var res = await c.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), res);
    }

    private static async Task<JsonElement> Ok(HttpClient c, string url)
    {
        var (status, body, _) = await Send(c, HttpMethod.Get, url);
        Assert.True(status == HttpStatusCode.OK, $"GET {url} → {(int)status}: {body}");
        return body;
    }

    private static async Task<JsonElement> CreateOrder(HttpClient c, string po, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["buyer"] = "Nordic Wear AB", ["buyerOrderNumber"] = po };
        if (extra is not null)
            foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject()) body[p.Name] = p.Value;
        var (status, json, _) = await Send(c, HttpMethod.Post, "/api/merchandising/orders", body);
        Assert.True(status == HttpStatusCode.Created, $"create order → {(int)status}: {json}");
        return json;
    }

    private static MultipartFormDataContent Form(byte[] file, string fileName, string? type = "purchase_order", string? title = null, string? changeNote = null)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        if (type is not null) form.Add(new StringContent(type), "type");
        if (title is not null) form.Add(new StringContent(title), "title");
        if (changeNote is not null) form.Add(new StringContent(changeNote), "changeNote");
        return form;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> Upload(HttpClient c, string url, MultipartFormDataContent form, string? key = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        var res = await c.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), res);
    }

    private static async Task<JsonElement> Uploaded(HttpClient c, string orderId, byte[] file, string fileName, string type = "purchase_order", string? title = null)
    {
        var (status, body, _) = await Upload(c, $"/api/merchandising/orders/{orderId}/documents", Form(file, fileName, type, title));
        Assert.True(status == HttpStatusCode.Created, $"upload → {(int)status}: {body}");
        return body;
    }

    private static async Task ProcessAll(DbApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IMerchDocumentProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessNextAsync(CancellationToken.None); i++) { }
    }

    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;
    private static string Status(JsonElement version) => version.GetProperty("processing").GetProperty("status").GetString()!;
    private static uint Ver(JsonElement e) => e.GetProperty("version").GetUInt32();

    private static JsonElement Field(JsonElement version, string key) =>
        version.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == key);

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>ProblemDetails detail plus every validation message, unescaped.</summary>
    private static string Messages(JsonElement body)
    {
        var parts = new List<string>();
        if (body.ValueKind != JsonValueKind.Object) return "";
        if (body.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) parts.Add(d.GetString()!);
        if (body.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                foreach (var m in p.Value.EnumerateArray()) parts.Add(m.GetString()!);
        return string.Join(" | ", parts);
    }

    private static object Accept(JsonElement field) => new { fieldId = Id(field), decision = "accepted" };

    private static string[] Codes(JsonElement field) => field.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToArray();

    private static byte[] PoText(string po, string quantity = "1,200 pcs", string delivery = "2027-01-15", string extra = "") => TestDocuments.Text($"""
        PURCHASE ORDER
        PO Number: {po}
        Buyer: Nordic Wear AB
        Style: ST-1001
        Product: Men's knit polo shirt
        Quantity: {quantity}
        Delivery Date: {delivery}
        Currency: USD
        Unit Price: 4.25
        BD: Navy/M/600
        BD: Navy/L/600
        {extra}
        """);

    private static Task<string[]> AuditActions(DbApiFactory api, string orderId) =>
        Rag.WithDb(api, db => db.AuditLog.Where(a => a.EntityType == "merch_order" && a.EntityId == orderId).OrderBy(a => a.Id).Select(a => a.Action).ToArrayAsync());

    private static Task<string[]> AuditReasons(DbApiFactory api, string orderId) =>
        Rag.WithDb(api, db => db.AuditLog.Where(a => a.EntityType == "merch_order" && a.EntityId == orderId).OrderBy(a => a.Id).Select(a => a.Action + "|" + a.ActorEmail + "|" + a.Reason).ToArrayAsync());

    // ── Upload → process → review → confirm → summary ───────────────────────

    [DockerFact]
    public async Task Upload_extract_review_correct_confirm_and_the_order_summary_shows_the_confirmed_values()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var po = Unique("PO");
        var order = await CreateOrder(op, po, new
        {
            requiredDocumentTypes = new[] { "purchase_order", "tech_pack" },
            milestones = new[] { new { name = "Lab dip approval", dueDate = "2026-11-10" } },
        });
        var orderId = Id(order.GetProperty("order"));
        Assert.Equal("missing", order.GetProperty("requiredDocuments").EnumerateArray().Single(r => r.GetProperty("type").GetString() == "tech_pack").GetProperty("state").GetString());

        var uploaded = await Uploaded(op, orderId, PoText(po), "../../po-4500.txt");
        var versionId = Id(uploaded);
        Assert.Equal("pending", Status(uploaded));
        Assert.Equal("po-4500.txt", uploaded.GetProperty("file").GetProperty("originalFileName").GetString());
        Assert.Equal("txt", uploaded.GetProperty("file").GetProperty("format").GetString());

        await ProcessAll(Api);
        var v = await Ok(op, $"/api/merchandising/versions/{versionId}");
        Assert.Equal("needs_review", Status(v));
        Assert.Equal("openai", v.GetProperty("processing").GetProperty("provider").GetString());
        Assert.Equal(DbApiFactory.FakeExtractorModel, v.GetProperty("processing").GetProperty("model").GetString());
        Assert.Equal(DbApiFactory.FakeExtractorModel + "-2026-10", v.GetProperty("processing").GetProperty("modelVersion").GetString());
        Assert.Contains($"PO Number: {po}", v.GetProperty("pages")[0].GetProperty("text").GetString());

        var qty = Field(v, "quantity");
        Assert.Equal("model", qty.GetProperty("source").GetString());
        Assert.Equal("1,200 pcs", qty.GetProperty("rawValue").GetString());
        Assert.Equal("1200", qty.GetProperty("normalizedValue").GetString());
        Assert.Equal(0.92, qty.GetProperty("confidence").GetDouble());
        Assert.Equal("verified", qty.GetProperty("evidenceStatus").GetString());
        Assert.Equal("Quantity: 1,200 pcs", qty.GetProperty("snippet").GetString());
        Assert.Equal(1, qty.GetProperty("pageNumber").GetInt32());
        var bd = Field(v, "size_color_breakdown");
        Assert.False(bd.TryGetProperty("confidence", out _));           // the model said 1.7: discarded
        Assert.Empty(Codes(bd));                                       // 600 + 600 = 1,200
        Assert.DoesNotContain(v.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString() == "payment_terms");
        Assert.Empty(Api.Http.Blocked);
        // The scripted model got the document text and the schema; the key went in the header only.
        Assert.Contains(Api.ExtractorPrompts, p => p.Contains(po) && p.Contains("delivery_date"));
        Assert.Contains(Api.Http.Handled, h => h.Authorization == $"Bearer {Api.ProviderKey}");

        // Correct the quantity (and with it the breakdown would not add up) → refused, nothing stored.
        var decisions = v.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("key").GetString() == "quantity" ? (object)new { fieldId = Id(f), decision = "corrected", correctedValue = "1,250", note = "per buyer email" } : Accept(f))
            .ToList();
        var (bad, badBody, _) = await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{versionId}/confirm",
            new { expectedVersion = Ver(v), decisions, applyToOrder = new[] { "quantity" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Contains("breakdown totals 1,200 but the quantity is 1,250", Messages(badBody));

        // Correct the delivery date, reject the price, mark the destination unresolved, apply the rest.
        decisions = v.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString() switch
        {
            "delivery_date" => (object)new { fieldId = Id(f), decision = "corrected", correctedValue = "20 Jan 2027", note = "revised by buyer" },
            "unit_price" => new { fieldId = Id(f), decision = "rejected", note = "price is not tracked here" },
            _ => Accept(f),
        }).ToList();
        var (ok, confirmed, _) = await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{versionId}/confirm", new
        {
            expectedVersion = Ver(v), decisions,
            additions = new object[] { new { fieldKey = "unit", value = "Pieces" }, new { fieldKey = "destination", decision = "unresolved" } },
            applyToOrder = new[] { "style", "quantity", "unit", "delivery_date", "currency", "size_color_breakdown", "product" },
            notes = "checked against the signed PO",
        });
        Assert.True(ok == HttpStatusCode.OK, $"confirm → {(int)ok}: {confirmed}");
        Assert.Equal("confirmed", Status(confirmed));
        var dd = Field(confirmed, "delivery_date");
        Assert.Equal("corrected", dd.GetProperty("decision").GetString());
        Assert.Equal("2027-01-15", dd.GetProperty("normalizedValue").GetString());     // the extracted value is kept
        Assert.Equal("2027-01-20", dd.GetProperty("correctedValue").GetString());
        Assert.Equal("Delivery Date: 2027-01-15", dd.GetProperty("snippet").GetString());
        Assert.Equal("reviewer", Field(confirmed, "unit").GetProperty("source").GetString());
        Assert.Equal("unresolved", Field(confirmed, "destination").GetProperty("decision").GetString());

        var detail = await Ok(op, $"/api/merchandising/orders/{orderId}");
        var o = detail.GetProperty("order");
        Assert.Equal(1200, o.GetProperty("quantity").GetInt32());
        Assert.Equal("pcs", o.GetProperty("unit").GetString());
        Assert.Equal("2027-01-20", o.GetProperty("deliveryDate").GetString());
        Assert.Equal("ST-1001", o.GetProperty("styleRef").GetString());
        Assert.False(o.TryGetProperty("unitPrice", out _));                             // rejected: not applied
        Assert.Equal(1200, o.GetProperty("breakdownTotal").GetInt32());
        var qtySource = detail.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "quantity").GetProperty("source");
        Assert.Equal("document", qtySource.GetProperty("kind").GetString());
        Assert.Equal(versionId, qtySource.GetProperty("versionId").GetString());
        Assert.False(qtySource.GetProperty("stale").GetBoolean());

        var summary = detail.GetProperty("summary");
        Assert.Equal($"Nordic Wear AB PO {po}: 1,200 pcs, style ST-1001, delivery 2027-01-20 (in {new DateOnly(2027, 1, 20).DayNumber - TodayDhaka().DayNumber} days).",
            summary.GetProperty("headline").GetString());
        var attention = detail.GetProperty("attention").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ToList();
        Assert.Contains("missing_document", attention);                                 // tech pack
        Assert.Contains("unresolved_field", attention);                                 // destination
        Assert.Equal("confirmed", detail.GetProperty("requiredDocuments").EnumerateArray().Single(r => r.GetProperty("type").GetString() == "purchase_order").GetProperty("state").GetString());

        var audit = await AuditReasons(Api, orderId);
        Assert.Equal(new[] { "merch_order.created", "merch_document.uploaded", "merch_document.processed", "merch_document.confirmed" },
            audit.Select(a => a.Split('|')[0]));
        Assert.Contains("system:merch-worker", audit[2]);
        Assert.Contains("delivery_date: 2027-01-15 → 2027-01-20", audit[3]);
        Assert.Contains("rejected [unit_price]", audit[3]);
        Assert.Contains("unresolved [destination]", audit[3]);
        Assert.Contains("operator@rag.test", audit[3]);

        // Confirmed is final for the version.
        var (again, _, _) = await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{versionId}/confirm", new { expectedVersion = Ver(confirmed), decisions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, again);

        // Nothing logged the document text or the key.
        Assert.DoesNotContain(Api.Logs.Lines, l => l.Contains("Men's knit polo shirt") || l.Contains(Api.ProviderKey));
    }

    private static DateOnly TodayDhaka() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, FactoryBrain.Application.Production.ProductionOptions.ResolveTimeZone("Asia/Dhaka")!));

    [DockerFact]
    public async Task Pdf_evidence_cites_the_page_it_was_found_on_and_the_original_file_is_served_unchanged()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        var pdf = TestDocuments.Pdf(new[] { "PURCHASE ORDER", $"PO Number: {po}", "Buyer: Nordic Wear AB" }, new[] { "Quantity: 3,000 pcs", "Delivery Date: 2027-02-01" });
        var versionId = Id(await Uploaded(op, orderId, pdf, "po.pdf"));
        await ProcessAll(Api);
        var v = await Ok(op, $"/api/merchandising/versions/{versionId}");
        Assert.Equal(2, v.GetProperty("processing").GetProperty("pageCount").GetInt32());
        Assert.Equal(1, Field(v, "order_number").GetProperty("pageNumber").GetInt32());
        Assert.Equal(2, Field(v, "quantity").GetProperty("pageNumber").GetInt32());
        Assert.Equal("verified", Field(v, "quantity").GetProperty("evidenceStatus").GetString());
        Assert.Contains("style", v.GetProperty("missingRequiredFields").EnumerateArray().Select(x => x.GetString()));

        using var viewer = Rag.Client(Api, Roles.Viewer);
        using var file = await viewer.GetAsync($"/api/merchandising/versions/{versionId}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(pdf, await file.Content.ReadAsByteArrayAsync());
    }

    // ── Conflicts ───────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Conflicting_documents_are_flagged_with_their_sources_and_a_person_resolves_them()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        await Uploaded(op, orderId, PoText(po), "po.txt");
        var techPack = TestDocuments.Text($"TECH PACK\nPO Number: {po}\nStyle: st 1001\nQuantity: 1,180 pcs\nDelivery Date: 2027-01-15\n");
        var tpVersion = Id(await Uploaded(op, orderId, techPack, "techpack.txt", "tech_pack"));
        await ProcessAll(Api);

        var detail = await Ok(op, $"/api/merchandising/orders/{orderId}");
        var open = detail.GetProperty("conflicts").EnumerateArray().Where(c => c.GetProperty("status").GetString() == "open").ToList();
        var conflict = Assert.Single(open);                         // style "ST-1001" = "st 1001": not a conflict
        Assert.Equal("quantity", conflict.GetProperty("fieldKey").GetString());
        var values = conflict.GetProperty("values").EnumerateArray().ToList();
        Assert.Equal(new[] { "1180", "1200" }, values.Select(x => x.GetProperty("value").GetString()).OrderBy(x => x));
        Assert.Contains(values, x => x.GetProperty("documentType").GetString() == "tech_pack" && x.GetProperty("snippet").GetString() == "Quantity: 1,180 pcs");
        Assert.Contains(detail.GetProperty("attention").EnumerateArray(), a => a.GetProperty("kind").GetString() == "conflict" && a.GetProperty("priority").GetString() == "high");
        var listed = await Ok(op, "/api/merchandising/orders?attention=conflicts&pageSize=100");
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), i => Id(i) == orderId);

        // A viewer can't decide; a note is required; the value is validated.
        using var viewer = Rag.Client(Api, Roles.Viewer);
        var url = $"/api/merchandising/conflicts/{Id(conflict)}/resolve";
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Post, url, new { expectedVersion = Ver(conflict), chosenValue = "1200", note = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(op, HttpMethod.Post, url, new { expectedVersion = Ver(conflict), chosenValue = "1200" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(op, HttpMethod.Post, url, new { expectedVersion = Ver(conflict), chosenValue = "lots", note = "buyer said" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(op, HttpMethod.Post, url, new { expectedVersion = Ver(conflict) + 99, chosenValue = "1200", note = "buyer said" })).Status);

        var (status, resolved, _) = await Send(op, HttpMethod.Post, url, new { expectedVersion = Ver(conflict), chosenValue = "1,200", note = "Buyer confirmed 1,200 by email on 27 Oct" });
        Assert.True(status == HttpStatusCode.OK, $"resolve → {(int)status}: {resolved}");
        Assert.Equal("resolved", resolved.GetProperty("status").GetString());
        Assert.Equal("1200", resolved.GetProperty("resolvedValue").GetString());

        detail = await Ok(op, $"/api/merchandising/orders/{orderId}");
        Assert.Equal(1200, detail.GetProperty("order").GetProperty("quantity").GetInt32());
        var source = detail.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "quantity").GetProperty("source");
        Assert.Equal("conflict_resolution", source.GetProperty("kind").GetString());
        Assert.DoesNotContain(detail.GetProperty("conflicts").EnumerateArray(), c => c.GetProperty("status").GetString() == "open");

        // Re-processing the same evidence does not reopen it.
        await Rag.WithDb(Api, db => db.MerchDocumentVersions.Where(x => x.Id == Guid.Parse(tpVersion))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, FactoryBrain.Domain.Entities.MerchVersionStatus.Failed).SetProperty(x => x.FailureCode, "provider_error")));
        Assert.Equal(HttpStatusCode.Accepted, (await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{tpVersion}/retry")).Status);
        await ProcessAll(Api);
        detail = await Ok(op, $"/api/merchandising/orders/{orderId}");
        Assert.DoesNotContain(detail.GetProperty("conflicts").EnumerateArray(), c => c.GetProperty("status").GetString() == "open");

        // A person typing a different quantity on the order is new evidence: a new conflict opens.
        var (put, _, _) = await Send(op, HttpMethod.Put, $"/api/merchandising/orders/{orderId}", new { quantity = 1300, expectedVersion = Ver(detail.GetProperty("order")) });
        Assert.Equal(HttpStatusCode.OK, put);
        detail = await Ok(op, $"/api/merchandising/orders/{orderId}");
        var reopened = detail.GetProperty("conflicts").EnumerateArray().Single(c => c.GetProperty("status").GetString() == "open");
        Assert.Contains(reopened.GetProperty("values").EnumerateArray(), x => x.GetProperty("sourceKind").GetString() == "order" && x.GetProperty("value").GetString() == "1300");

        // A replaced document no longer counts; when everything agrees the conflict clears by itself.
        var docId = detail.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("type").GetString() == "tech_pack").GetProperty("id").GetString();
        var (vs, _, _) = await Upload(op, $"/api/merchandising/documents/{docId}/versions",
            Form(TestDocuments.Text($"TECH PACK\nPO Number: {po}\nQuantity: 1,300 pcs\n"), "techpack-v2.txt", type: null, changeNote: "quantity revised"));
        Assert.Equal(HttpStatusCode.Created, vs);
        await Send(op, HttpMethod.Put, $"/api/merchandising/orders/{orderId}", new { clearFields = new[] { "quantity" }, expectedVersion = Ver((await Ok(op, $"/api/merchandising/orders/{orderId}")).GetProperty("order")) });
        await ProcessAll(Api);
        var actions = await AuditActions(Api, orderId);
        Assert.Contains("merch_conflict.detected", actions);
        Assert.Contains("merch_conflict.resolved", actions);
        Assert.Contains("merch_conflict.updated", actions);
        Assert.Contains("merch_document.version_uploaded", actions);
    }

    // ── Validation of review decisions ──────────────────────────────────────

    [DockerFact]
    public async Task Invalid_values_cannot_be_accepted_every_value_needs_a_decision_and_stale_reviews_are_refused()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        var versionId = Id(await Uploaded(op, orderId, PoText(po, quantity: "12.000", delivery: "03/04/2027", extra: "Destination: Gothenburg"), "po.txt"));
        await ProcessAll(Api);
        var v = await Ok(op, $"/api/merchandising/versions/{versionId}");
        Assert.Contains("low_confidence", Codes(Field(v, "destination")));          // the model said 0.4: flagged, not blocking
        Assert.False(Field(v, "destination").GetProperty("issues")[0].GetProperty("blocking").GetBoolean());
        Assert.Contains("ambiguous_date", Codes(Field(v, "delivery_date")));
        Assert.Contains("ambiguous_quantity", Codes(Field(v, "quantity")));
        Assert.False(Field(v, "delivery_date").TryGetProperty("normalizedValue", out _));
        Assert.True(Field(v, "delivery_date").GetProperty("issues")[0].GetProperty("blocking").GetBoolean());

        var url = $"/api/merchandising/versions/{versionId}/confirm";
        var all = v.GetProperty("fields").EnumerateArray().Select(Accept).ToList();
        var (s1, b1, _) = await Send(op, HttpMethod.Post, url, new { expectedVersion = Ver(v), decisions = all });
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Contains("can't be accepted as extracted", Messages(b1));

        var (s2, b2, _) = await Send(op, HttpMethod.Post, url, new { expectedVersion = Ver(v), decisions = all.Take(2).ToList() });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("Decide every extracted value", Messages(b2));

        var fixedDecisions = v.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString() switch
        {
            "delivery_date" => (object)new { fieldId = Id(f), decision = "corrected", correctedValue = "2027-04-03" },
            "quantity" => new { fieldId = Id(f), decision = "corrected", correctedValue = "12" },
            "size_color_breakdown" => new { fieldId = Id(f), decision = "unresolved" },
            _ => Accept(f),
        }).ToList();
        var (s3, b3, _) = await Send(op, HttpMethod.Post, url, new { expectedVersion = Ver(v), decisions = fixedDecisions, applyToOrder = new[] { "size_color_breakdown" } });
        Assert.Equal(HttpStatusCode.BadRequest, s3);                     // an unresolved value can't become order data
        Assert.Contains("can only be applied to the order when it is accepted or corrected", Messages(b3));
        var (s4, b4, _) = await Send(op, HttpMethod.Post, url, new { expectedVersion = Ver(v) + 5, decisions = fixedDecisions });
        Assert.Equal(HttpStatusCode.Conflict, s4);
        Assert.Equal("version_conflict", b4.GetProperty("title").GetString());
        // Nothing of the refused attempts was stored.
        Assert.Equal("needs_review", Status(await Ok(op, $"/api/merchandising/versions/{versionId}")));
        Assert.DoesNotContain("merch_document.confirmed", await AuditActions(Api, orderId));
    }

    [DockerFact]
    public async Task Concurrent_confirmations_store_exactly_one()
    {
        using var a = Rag.Client(Api, Roles.Operator);
        using var b = Rag.Client(Api, Roles.Admin);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(a, po)).GetProperty("order"));
        var versionId = Id(await Uploaded(a, orderId, PoText(po), "po.txt"));
        await ProcessAll(Api);
        var v = await Ok(a, $"/api/merchandising/versions/{versionId}");
        var body = new { expectedVersion = Ver(v), decisions = v.GetProperty("fields").EnumerateArray().Select(Accept).ToList(), applyToOrder = new[] { "quantity" } };
        var results = await Task.WhenAll(
            Send(a, HttpMethod.Post, $"/api/merchandising/versions/{versionId}/confirm", body),
            Send(b, HttpMethod.Post, $"/api/merchandising/versions/{versionId}/confirm", body));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, results.Select(r => r.Status).OrderBy(s => (int)s));
        Assert.Single((await AuditActions(Api, orderId)).Where(x => x == "merch_document.confirmed"));
    }

    // ── Uploads ─────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Uploads_are_validated_by_content_and_size_and_repeats_never_duplicate()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        var url = $"/api/merchandising/orders/{orderId}/documents";

        async Task<(HttpStatusCode, string)> Try(byte[] file, string name, string? type = "purchase_order")
        {
            var (s, body, _) = await Upload(op, url, Form(file, name, type));
            return (s, Messages(body));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Try(TestDocuments.Xlsx(), "sizes.xlsx")).Item1);
        Assert.Contains("Save the sheet as CSV", (await Try(TestDocuments.Xlsx(), "sizes.xlsx")).Item2);
        Assert.Contains("does not match its extension", (await Try(TestDocuments.Text("hello"), "po.pdf")).Item2);
        Assert.Contains("not readable text", (await Try(new byte[] { 0x4D, 0x5A, 0x90, 0, 3, 0 }, "po.txt")).Item2);
        Assert.Contains("type is required", (await Try(TestDocuments.Text("PO"), "po.txt", null)).Item2);
        Assert.Contains("type must be one of", (await Try(TestDocuments.Text("PO"), "po.txt", "invoice")).Item2);
        Assert.Contains("A file is required", (await Try(Array.Empty<byte>(), "po.txt")).Item2);
        var big = new byte[15 * 1024 * 1024 + 10];
        Array.Fill(big, (byte)'a');
        Assert.Contains("larger than 15 MB", (await Try(big, "big.txt")).Item2);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(op, $"/api/merchandising/orders/{Guid.NewGuid()}/documents", Form(TestDocuments.Text("x"), "x.txt"))).Status);

        // Same key, same file → the stored result; same key, other file → refused; same content again → refused.
        var file = PoText(po);
        var key = "upload-" + Guid.NewGuid().ToString("N");
        var (first, created, _) = await Upload(op, url, Form(file, "po.txt"), key);
        Assert.Equal(HttpStatusCode.Created, first);
        var (second, replay, res) = await Upload(op, url, Form(file, "po.txt"), key);
        Assert.Equal(HttpStatusCode.OK, second);
        Assert.Equal("true", res.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(Id(created), Id(replay));
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(op, url, Form(TestDocuments.Text("other"), "po.txt"), key)).Status);
        var (dup, dupBody, _) = await Upload(op, url, Form(file, "copy.txt"));
        Assert.Equal(HttpStatusCode.Conflict, dup);
        Assert.Equal("duplicate_content", dupBody.GetProperty("title").GetString());
        Assert.Equal(1, await Rag.WithDb(Api, db => db.MerchDocumentVersions.CountAsync(x => x.OrderId == Guid.Parse(orderId))));
        Assert.Single((await AuditActions(Api, orderId)).Where(x => x == "merch_document.uploaded"));

        // Stored under a generated key, with the hash of exactly these bytes.
        var stored = await Rag.WithDb(Api, db => db.MerchDocumentVersions.SingleAsync(x => x.OrderId == Guid.Parse(orderId)));
        Assert.Matches(@"^\d{4}/\d{2}/[0-9a-f]{32}\.txt$", stored.StorageKey);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), stored.Sha256);
        Assert.True(File.Exists(Path.Combine(Api.MerchStoragePath, stored.StorageKey)));
    }

    [DockerFact]
    public async Task New_versions_keep_the_history_and_only_the_current_version_can_be_confirmed()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        var v1Bytes = PoText(po);
        var v1 = await Uploaded(op, orderId, v1Bytes, "po.txt");
        await ProcessAll(Api);
        var docId = v1.GetProperty("documentId").GetString();
        var versionsUrl = $"/api/merchandising/documents/{docId}/versions";
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(op, versionsUrl, Form(v1Bytes, "po-again.txt", null))).Status);
        var (s, v2, _) = await Upload(op, versionsUrl, Form(PoText(po, quantity: "1,500 pcs"), "po-rev1.txt", null, changeNote: "Revision 1: quantity up"));
        Assert.Equal(HttpStatusCode.Created, s);
        Assert.Equal(2, v2.GetProperty("versionNumber").GetInt32());
        Assert.True(v2.GetProperty("isCurrent").GetBoolean());

        var old = await Ok(op, $"/api/merchandising/versions/{Id(v1)}");
        Assert.False(old.GetProperty("isCurrent").GetBoolean());
        Assert.False(old.GetProperty("reviewable").GetBoolean());
        var (c, cb, _) = await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{Id(v1)}/confirm",
            new { expectedVersion = Ver(old), decisions = old.GetProperty("fields").EnumerateArray().Select(Accept).ToList() });
        Assert.Equal(HttpStatusCode.Conflict, c);
        Assert.Equal("not_reviewable", cb.GetProperty("title").GetString());

        var doc = await Ok(op, $"/api/merchandising/documents/{docId}");
        Assert.Equal(new[] { 2, 1 }, doc.GetProperty("versions").EnumerateArray().Select(x => x.GetProperty("versionNumber").GetInt32()));
        Assert.Equal("Revision 1: quantity up", doc.GetProperty("versions")[0].GetProperty("changeNote").GetString());
        using var original = await op.GetAsync($"/api/merchandising/versions/{Id(v1)}/file");
        Assert.Equal(v1Bytes, await original.Content.ReadAsByteArrayAsync());              // the original is preserved
    }

    // ── Provider failures and retries ───────────────────────────────────────

    [DockerFact]
    public async Task Provider_failures_are_stored_honestly_and_a_retry_processes_once()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        var err = Id(await Uploaded(op, orderId, PoText(po, extra: DbApiFactory.ForceExtractorFailure), "a.txt"));
        var limited = Id(await Uploaded(op, orderId, PoText(po, extra: DbApiFactory.ForceExtractorRateLimit), "b.txt", "tech_pack"));
        var invalid = Id(await Uploaded(op, orderId, PoText(po, extra: DbApiFactory.ForceExtractorInvalid), "c.txt", "buyer_instruction"));
        var fabricated = Id(await Uploaded(op, orderId, PoText(po, extra: DbApiFactory.ForceExtractorFabricated), "d.txt", "other"));
        await ProcessAll(Api);

        var e = await Ok(op, $"/api/merchandising/versions/{err}");
        Assert.Equal("failed", Status(e));
        Assert.Equal("provider_error", e.GetProperty("processing").GetProperty("failureCode").GetString());
        Assert.True(e.GetProperty("processing").GetProperty("retryable").GetBoolean());
        Assert.Empty(e.GetProperty("fields").EnumerateArray());                              // nothing invented
        Assert.NotEmpty(e.GetProperty("pages").EnumerateArray());                            // the text was still read
        var r = await Ok(op, $"/api/merchandising/versions/{limited}");
        Assert.Equal("provider_rate_limited", r.GetProperty("processing").GetProperty("failureCode").GetString());
        Assert.Contains("wait 20 s", r.GetProperty("processing").GetProperty("failureMessage").GetString());
        Assert.Equal("invalid_output", (await Ok(op, $"/api/merchandising/versions/{invalid}")).GetProperty("processing").GetProperty("failureCode").GetString());

        var f = await Ok(op, $"/api/merchandising/versions/{fabricated}");
        var price = Field(f, "unit_price");
        Assert.Equal("not_found", price.GetProperty("evidenceStatus").GetString());          // the model's quote isn't in the document
        Assert.False(price.TryGetProperty("snippet", out _));
        Assert.Contains("evidence_not_found", Codes(price));

        // Two concurrent retries queue one job.
        var retries = await Task.WhenAll(
            Send(op, HttpMethod.Post, $"/api/merchandising/versions/{err}/retry"),
            Send(op, HttpMethod.Post, $"/api/merchandising/versions/{err}/retry"));
        Assert.Equal(new[] { HttpStatusCode.Accepted, HttpStatusCode.Conflict }, retries.Select(x => x.Status).OrderBy(x => (int)x));
        await ProcessAll(Api);
        // Still failing: the marker is in the file. Its fields stay empty, and processing twice left no duplicates.
        Assert.Equal("failed", Status(await Ok(op, $"/api/merchandising/versions/{err}")));

        Api.ExtractorDown = true;
        try
        {
            var down = Id(await Uploaded(op, orderId, TestDocuments.Text($"Measurement sheet\nPO Number: {po}\nStyle: ST-1001\n"), "m.txt", "measurement_sheet"));
            await ProcessAll(Api);
            Assert.Equal("failed", Status(await Ok(op, $"/api/merchandising/versions/{down}")));
            Api.ExtractorDown = false;
            Assert.Equal(HttpStatusCode.Accepted, (await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{down}/retry")).Status);
            await ProcessAll(Api);
            var ok = await Ok(op, $"/api/merchandising/versions/{down}");
            Assert.Equal("needs_review", Status(ok));
            Assert.Equal(2, ok.GetProperty("fields").GetArrayLength());
            Assert.Equal(2, await Rag.WithDb(Api, db => db.MerchExtractedFields.CountAsync(x => x.VersionId == Guid.Parse(down))));
            Assert.Contains("merch_document.retried", await AuditActions(Api, orderId));
        }
        finally { Api.ExtractorDown = false; }
    }

    [DockerFact]
    public async Task Scanned_documents_need_ocr_and_can_still_be_reviewed_by_hand()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        var scan = Id(await Uploaded(op, orderId, TestDocuments.ScannedPdf(), "scan.pdf"));
        var photo = Id(await Uploaded(op, orderId, TestImages.Png(16, 16), "instruction.png", "buyer_instruction"));
        await ProcessAll(Api);
        var s = await Ok(op, $"/api/merchandising/versions/{scan}");
        Assert.Equal("failed", Status(s));
        Assert.Equal("ocr_required", s.GetProperty("processing").GetProperty("failureCode").GetString());
        Assert.False(s.GetProperty("processing").GetProperty("retryable").GetBoolean());
        Assert.True(s.GetProperty("reviewable").GetBoolean());
        Assert.Equal("ocr_required", (await Ok(op, $"/api/merchandising/versions/{photo}")).GetProperty("processing").GetProperty("failureCode").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{scan}/retry")).Status);

        var (status, confirmed, _) = await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{scan}/confirm", new
        {
            expectedVersion = Ver(s),
            additions = new[] { new { fieldKey = "quantity", value = "2,400" }, new { fieldKey = "delivery_date", value = "2027-03-01" } },
            applyToOrder = new[] { "quantity", "delivery_date" },
            notes = "Read from the scanned PO by hand",
        });
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {confirmed}");
        var order = (await Ok(op, $"/api/merchandising/orders/{orderId}")).GetProperty("order");
        Assert.Equal(2400, order.GetProperty("quantity").GetInt32());
        Assert.Equal("reviewer", Field(confirmed, "quantity").GetProperty("source").GetString());
        Assert.False(Field(confirmed, "quantity").TryGetProperty("confidence", out _));
    }

    // ── Orders, milestones, search, permissions ─────────────────────────────

    [DockerFact]
    public async Task Orders_and_milestones_persist_and_are_searchable_with_deadlines_from_stored_records()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var buyer = Unique("Buyer");
        var po = Unique("PO");
        var order = await CreateOrder(op, po, new { buyer, styleRef = "ZX-77", quantity = 500, deliveryDate = "2027-06-30", requiredDocumentTypes = new[] { "purchase_order" } });
        var orderId = Id(order.GetProperty("order"));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(op, HttpMethod.Post, "/api/merchandising/orders", new { buyer = buyer.ToLowerInvariant() + " ", buyerOrderNumber = po })).Status);
        var (bad, badBody, _) = await Send(op, HttpMethod.Post, "/api/merchandising/orders",
            new { buyer, buyerOrderNumber = Unique("PO"), deliveryDate = "03/04/2027", unit = "bags", currency = "$", productionOrderId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        foreach (var field in new[] { "deliveryDate", "unit", "currency", "productionOrderId" }) Assert.True(badBody.GetProperty("errors").TryGetProperty(field, out _), field);

        var (ms, milestone, _) = await Send(op, HttpMethod.Post, $"/api/merchandising/orders/{orderId}/milestones", new { name = "PP sample approval", dueDate = "2026-10-01" });
        Assert.Equal(HttpStatusCode.Created, ms);
        Assert.True(milestone.GetProperty("overdue").GetBoolean());
        var detail = await Ok(op, $"/api/merchandising/orders/{orderId}");
        Assert.Equal("milestone_overdue", detail.GetProperty("attention")[0].GetProperty("kind").GetString());
        Assert.Equal("high", detail.GetProperty("attention")[0].GetProperty("priority").GetString());

        Assert.Contains((await Ok(op, $"/api/merchandising/orders?attention=overdue&buyer={buyer}")).GetProperty("items").EnumerateArray(), i => Id(i) == orderId);
        Assert.Contains((await Ok(op, $"/api/merchandising/orders?attention=missing_documents&q={po}")).GetProperty("items").EnumerateArray(), i => Id(i) == orderId);
        var found = await Ok(op, $"/api/merchandising/orders?q=zx-77&deliveryFrom=2027-06-01&deliveryTo=2027-06-30");
        var row = Assert.Single(found.GetProperty("items").EnumerateArray(), i => Id(i) == orderId);
        Assert.Equal(1, row.GetProperty("overdueMilestones").GetInt32());
        Assert.Equal(1, row.GetProperty("missingDocuments").GetInt32());
        Assert.Empty((await Ok(op, $"/api/merchandising/orders?q={po}&deliveryTo=2027-05-01")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(op, HttpMethod.Get, "/api/merchandising/orders?attention=everything")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(op, HttpMethod.Get, "/api/merchandising/orders?deliveryFrom=tomorrow")).Status);

        var overview = await Ok(op, "/api/merchandising/overview");
        Assert.True(overview.GetProperty("overdueMilestones").GetInt32() >= 1);
        Assert.Contains(overview.GetProperty("deadlines").EnumerateArray(), d => d.GetProperty("orderId").GetString() == orderId && d.GetProperty("overdue").GetBoolean());
        Assert.Equal("llm", overview.GetProperty("extraction").GetProperty("mode").GetString());

        // Mark it done; a stale version is refused.
        var (stale, _, _) = await Send(op, HttpMethod.Put, $"/api/merchandising/milestones/{Id(milestone)}", new { status = "done", expectedVersion = Ver(milestone) + 7 });
        Assert.Equal(HttpStatusCode.Conflict, stale);
        var (done, doneBody, _) = await Send(op, HttpMethod.Put, $"/api/merchandising/milestones/{Id(milestone)}", new { status = "done", expectedVersion = Ver(milestone) });
        Assert.Equal(HttpStatusCode.OK, done);
        Assert.False(doneBody.GetProperty("overdue").GetBoolean());
        Assert.NotNull(Str(doneBody, "completedOn"));
        Assert.DoesNotContain((await Ok(op, $"/api/merchandising/orders?attention=overdue&buyer={buyer}")).GetProperty("items").EnumerateArray(), i => Id(i) == orderId);
        var actions = await AuditActions(Api, orderId);
        Assert.Equal(new[] { "merch_order.created", "merch_milestone.created", "merch_milestone.updated" }, actions);
    }

    [DockerFact]
    public async Task Viewers_read_but_cannot_write_and_the_production_link_must_exist()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        using var viewer = Rag.Client(Api, Roles.Viewer);
        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        var versionId = Id(await Uploaded(op, orderId, PoText(po), "po.txt"));
        await ProcessAll(Api);

        Assert.Equal(HttpStatusCode.OK, (await Send(viewer, HttpMethod.Get, $"/api/merchandising/orders/{orderId}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(viewer, HttpMethod.Get, $"/api/merchandising/versions/{versionId}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(viewer, HttpMethod.Get, "/api/merchandising/documents?status=needs_review")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(viewer, $"/api/merchandising/orders/{orderId}/documents", Form(TestDocuments.Text("x"), "x.txt"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Post, $"/api/merchandising/versions/{versionId}/confirm", new { expectedVersion = 1 })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Post, "/api/merchandising/orders", new { buyer = "x", buyerOrderNumber = "y" })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.CreateClient().GetAsync($"/api/merchandising/versions/{versionId}/file")).StatusCode);

        var detail = await Ok(op, $"/api/merchandising/orders/{orderId}");
        var (link, linkBody, _) = await Send(op, HttpMethod.Put, $"/api/merchandising/orders/{orderId}",
            new { productionOrderId = Guid.NewGuid(), expectedVersion = Ver(detail.GetProperty("order")) });
        Assert.Equal(HttpStatusCode.BadRequest, link);
        Assert.Contains("production order does not exist", Messages(linkBody));
        Assert.False(detail.TryGetProperty("productionOrder", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(op, HttpMethod.Get, $"/api/merchandising/orders/{Guid.NewGuid()}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(op, HttpMethod.Get, "/api/merchandising/versions/not-a-guid")).Status);
    }

    [DockerFact]
    public async Task The_schema_enforces_the_evidence_and_confirmation_rules()
    {
        var violations = await Rag.Strings(Api, """
            select string_agg(conname, ',' order by conname) from pg_constraint
             where conrelid in ('merch_extracted_fields'::regclass, 'merch_document_versions'::regclass, 'merch_conflicts'::regclass)
               and contype = 'c'
            """);
        foreach (var name in new[] { "CK_merch_fields_snippet", "CK_merch_fields_confidence", "CK_merch_versions_confirmed", "CK_merch_conflicts_resolved", "CK_merch_versions_format" })
            Assert.Contains(name, violations.Single());
        var indexes = await Rag.Strings(Api, "select string_agg(indexname, ',') from pg_indexes where tablename like 'merch_%'");
        Assert.Contains("IX_merch_document_versions_current", indexes.Single());
        Assert.Contains("IX_merch_conflicts_open", indexes.Single());
        Assert.Contains("IX_merch_extracted_fields_VersionId_FieldKey", indexes.Single());
        var migrations = await Rag.Strings(Api, "select \"MigrationId\" from \"__ef_migrations\" where \"MigrationId\" like '%AddMerchandising'");
        Assert.Single(migrations);
    }

    // ── Extraction turned off ───────────────────────────────────────────────

    [DockerFact]
    public async Task Without_an_extraction_provider_text_is_parsed_and_values_are_entered_by_hand()
    {
        var api = _stack.MerchManual;
        using var op = Rag.Client(api, Roles.Operator);
        var config = await Ok(op, "/api/merchandising/config");
        Assert.False(config.GetProperty("extraction").GetProperty("configured").GetBoolean());
        Assert.Equal("none", config.GetProperty("extraction").GetProperty("mode").GetString());
        Assert.False(config.GetProperty("ocrConfigured").GetBoolean());
        Assert.False(config.GetProperty("emailIntegrationConfigured").GetBoolean());

        var po = Unique("PO");
        var orderId = Id((await CreateOrder(op, po)).GetProperty("order"));
        var versionId = Id(await Uploaded(op, orderId, PoText(po), "po.txt"));
        await ProcessAll(api);
        var v = await Ok(op, $"/api/merchandising/versions/{versionId}");
        Assert.Equal("needs_review", Status(v));
        Assert.Equal("provider_not_configured", v.GetProperty("processing").GetProperty("extractionCode").GetString());
        Assert.Empty(v.GetProperty("fields").EnumerateArray());
        Assert.Contains($"PO Number: {po}", v.GetProperty("pages")[0].GetProperty("text").GetString());
        var (retry, retryBody, _) = await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{versionId}/retry");
        Assert.Equal(HttpStatusCode.Conflict, retry);
        Assert.Equal("provider_not_configured", retryBody.GetProperty("title").GetString());
        Assert.Empty(api.Http.Handled.Where(h => h.Uri.AbsolutePath.Contains("chat")));

        var (status, _, _) = await Send(op, HttpMethod.Post, $"/api/merchandising/versions/{versionId}/confirm", new
        {
            expectedVersion = Ver(v),
            additions = new[] { new { fieldKey = "quantity", value = "1,200" }, new { fieldKey = "style", value = "ST-1001" } },
            applyToOrder = new[] { "quantity", "style" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1200, (await Ok(op, $"/api/merchandising/orders/{orderId}")).GetProperty("order").GetProperty("quantity").GetInt32());
    }
}
