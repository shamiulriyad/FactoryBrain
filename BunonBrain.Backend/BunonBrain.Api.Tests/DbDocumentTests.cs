using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The document engine end to end on PostgreSQL: store documents moving stock only when
/// approved (never below zero), separation of duties, review-then-approve for high-risk
/// documents, category / factory / line isolation, duplicate prevention, generation from
/// production records, append-only history and notifications to the responsible people.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbDocumentTests
{
    private readonly PostgresStack _stack;
    public DbDocumentTests(PostgresStack stack) { _stack = stack; }
    private DbApiFactory Api => _stack.Local;

    private sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    private async Task<Reply> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : Rag.Json(body) };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var res = await client.SendAsync(req);
        var raw = Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());
        var json = raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[') ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw);
    }

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private sealed record Person(Guid Id, string Token);

    private async Task<Person> NewPerson(string role, Guid factory, string? line = null)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var user = new User { Id = id, Email = $"{role.ToLowerInvariant()}-{id:N}@documents.test", PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = factory });
        if (line is not null) user.Lines.Add(new UserProductionLineAssignment { UserId = id, LineId = line });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    private async Task<(Guid Factory, string Line)> NewFactory()
    {
        var t = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var factory = Guid.NewGuid();
        var line = $"dl-{t.ToLowerInvariant()}";
        await Db(async db =>
        {
            db.Factories.Add(new Factory { Id = factory, Code = $"D{t}", Name = $"Docs {t}" });
            await db.SaveChangesAsync();
            db.ProductionLines.Add(new ProductionLine { Id = line, Name = $"Line {t}", Workstations = 10, FactoryId = factory });
            await db.SaveChangesAsync();
        });
        return (factory, line);
    }

    private Task Db(Func<BunonBrainDbContext, Task> work) => Rag.WithDb(Api, async db => { await work(db); return 0; });

    private static string Id(Reply r) => r.Body.GetProperty("document").GetProperty("id").GetString()!;
    private static string Status(Reply r) => r.Body.GetProperty("document").GetProperty("status").GetString()!;
    private static string Today => DateTime.UtcNow.AddHours(6).ToString("yyyy-MM-dd");

    private static object Grn(string challan, decimal accepted) => new
    {
        supplier = "ABC Textiles", challanNo = challan, receivedOn = Today,
        items = new[] { new { itemCode = "FAB-01", itemName = "Single jersey", category = "FABRIC", unit = "kg", receivedQty = accepted, acceptedQty = accepted, rejectedQty = 0, unitCost = 3.5 } },
    };

    [DockerFact]
    public async Task Approved_store_documents_move_stock_and_stock_never_goes_below_zero()
    {
        var (f, _) = await NewFactory();
        var store = await NewPerson(R.StoreOfficer, f);
        var manager = await NewPerson(R.FactoryManager, f);
        var challan = $"CH-{Guid.NewGuid():N}"[..14];

        var grn = await Send(HttpMethod.Post, "/api/documents", store.Token, new { factoryId = f, typeCode = "store.grn", mode = "form", data = Grn(challan, 100) });
        Is(HttpStatusCode.Created, grn);
        var grnId = Id(grn);
        Assert.Equal(350m, grn.Body.GetProperty("data").GetProperty("totalAmount").GetDecimal());
        Is(HttpStatusCode.OK, await Send(HttpMethod.Post, $"/api/documents/{grnId}/submit", store.Token, new { }));
        Is(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, $"/api/documents/{grnId}/approve", store.Token, new { }));   // no approve permission
        var approved = await Send(HttpMethod.Post, $"/api/documents/{grnId}/approve", manager.Token, new { note = "Checked against challan" });
        Is(HttpStatusCode.OK, approved);
        Assert.True(approved.Body.GetProperty("stockPosted").GetBoolean());

        var items = await Send(HttpMethod.Get, $"/api/inventory/items?factoryId={f}", store.Token);
        Assert.Equal(100m, items.Body[0].GetProperty("balance").GetDecimal());

        // A second GRN with the same challan and supplier is a duplicate.
        Is(HttpStatusCode.Conflict, await Send(HttpMethod.Post, "/api/documents", store.Token, new { factoryId = f, typeCode = "store.grn", mode = "form", data = Grn(challan, 5) }), "duplicate_document");

        // Issuing more than the balance can't be submitted.
        var tooMuch = await Send(HttpMethod.Post, "/api/documents", store.Token, new
        {
            factoryId = f, typeCode = "store.issue", mode = "form",
            data = new { issuedTo = "Cutting", issuedOn = Today, items = new[] { new { itemCode = "fab-01", quantity = 150 } } },
        });
        Is(HttpStatusCode.Created, tooMuch);
        Assert.Contains(tooMuch.Body.GetProperty("checks").GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "insufficient_stock");
        Is(HttpStatusCode.BadRequest, await Send(HttpMethod.Post, $"/api/documents/{Id(tooMuch)}/submit", store.Token, new { }), "not_ready");

        var issue = await Send(HttpMethod.Put, $"/api/documents/{Id(tooMuch)}", store.Token, new
        {
            data = new { issuedTo = "Cutting", issuedOn = Today, items = new[] { new { itemCode = "FAB-01", quantity = 40 } } }, changeNote = "Reduced to what is in stock",
        });
        Is(HttpStatusCode.OK, issue);
        Assert.Equal(2, issue.Body.GetProperty("document").GetProperty("currentVersion").GetInt32());
        Is(HttpStatusCode.OK, await Send(HttpMethod.Post, $"/api/documents/{Id(issue)}/submit", store.Token, new { }));
        Is(HttpStatusCode.OK, await Send(HttpMethod.Post, $"/api/documents/{Id(issue)}/approve", manager.Token, new { }));
        items = await Send(HttpMethod.Get, $"/api/inventory/items?factoryId={f}", store.Token);
        Assert.Equal(60m, items.Body[0].GetProperty("balance").GetDecimal());

        // Posted documents can't be revised, and the ledger and versions are append-only.
        Is(HttpStatusCode.Conflict, await Send(HttpMethod.Post, $"/api/documents/{grnId}/revise", store.Token, new { note = "fix" }), "stock_posted");
        await Db(async db =>
        {
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE stock_movements SET \"Quantity\" = 1"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM factory_document_versions WHERE \"DocumentId\" = '{grnId}'"));
        });
    }

    [DockerFact]
    public async Task High_risk_documents_are_verified_then_approved_by_different_people_and_rejections_need_a_reason()
    {
        var (f, _) = await NewFactory();
        var commercial = await NewPerson(R.CommercialOfficer, f);
        var controller = await NewPerson(R.DocumentController, f);
        var manager = await NewPerson(R.FactoryManager, f);
        var inv = await Send(HttpMethod.Post, "/api/documents", commercial.Token, new
        {
            factoryId = f, typeCode = "commercial.invoice", mode = "form",
            data = new { invoiceNo = $"INV-{Guid.NewGuid():N}"[..14], invoiceDate = Today, buyer = "Nordic", poNumber = "PO-X", currency = "USD",
                lines = new[] { new { description = "Tee", quantity = 100, unitPrice = 2 } } },
        });
        Is(HttpStatusCode.Created, inv);
        var id = Id(inv);
        Assert.Equal("REVIEW_AND_APPROVE", inv.Body.GetProperty("document").GetProperty("approvalLevel").GetString());
        Is(HttpStatusCode.OK, await Send(HttpMethod.Post, $"/api/documents/{id}/submit", commercial.Token, new { }));

        // Notified: the reviewer (document controller), not the approver yet, never the preparer.
        await Db(async db =>
        {
            Assert.True(await db.UserNotifications.AnyAsync(n => n.UserId == controller.Id && n.EntityId == id));
            Assert.False(await db.UserNotifications.AnyAsync(n => n.UserId == commercial.Id && n.EntityId == id));
        });

        Is(HttpStatusCode.Conflict, await Send(HttpMethod.Post, $"/api/documents/{id}/approve", manager.Token, new { }), "verification_required");
        Is(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, $"/api/documents/{id}/approve", controller.Token, new { }));   // controller approves nothing
        Is(HttpStatusCode.BadRequest, await Send(HttpMethod.Post, $"/api/documents/{id}/reject", controller.Token, new { }), "reason_required");
        Is(HttpStatusCode.OK, await Send(HttpMethod.Post, $"/api/documents/{id}/verify", controller.Token, new { }));
        var ok = await Send(HttpMethod.Post, $"/api/documents/{id}/approve", manager.Token, new { });
        Is(HttpStatusCode.OK, ok);
        Assert.Equal("APPROVED", Status(ok));
        Assert.Contains(ok.Body.GetProperty("history").EnumerateArray(), h => h.GetProperty("action").GetString() == "verified");
        await Db(async db =>
            Assert.True(await db.AuditLog.AnyAsync(a => a.EntityId == id && a.Action == "document.approved")));
    }

    [DockerFact]
    public async Task Categories_factories_and_lines_isolate_documents()
    {
        var (f, line) = await NewFactory();
        var (other, _) = await NewFactory();
        var hr = await NewPerson(R.HrOfficer, f);
        var store = await NewPerson(R.StoreOfficer, f);
        var foreignManager = await NewPerson(R.FactoryManager, other);
        var sup = await NewPerson(R.ProductionSupervisor, f, line);
        var leave = await Send(HttpMethod.Post, "/api/documents", hr.Token, new { factoryId = f, typeCode = "hr.leave", mode = "form", data = new { leaveType = "SICK" } });
        Is(HttpStatusCode.Created, leave);
        Assert.Contains(leave.Body.GetProperty("checks").GetProperty("missing").EnumerateArray(), m => m.GetProperty("field").GetString() == "workerCode");
        Is(HttpStatusCode.NotFound, await Send(HttpMethod.Get, $"/api/documents/{Id(leave)}", store.Token));            // other category
        Is(HttpStatusCode.NotFound, await Send(HttpMethod.Get, $"/api/documents/{Id(leave)}", foreignManager.Token));   // other factory
        Is(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/documents", store.Token, new { factoryId = f, typeCode = "hr.leave" }), "category_not_permitted");
        // Supervisors prepare only line documents of their own lines.
        Is(HttpStatusCode.BadRequest, await Send(HttpMethod.Post, "/api/documents", sup.Token, new { factoryId = f, typeCode = "production.daily" }), "line_required");
        var mine = await Send(HttpMethod.Post, "/api/documents", sup.Token, new { factoryId = f, typeCode = "production.line", lineId = line });
        Is(HttpStatusCode.Created, mine);
        Assert.Equal("GENERATED", mine.Body.GetProperty("document").GetProperty("source").GetString());
        Assert.Contains(mine.Body.GetProperty("checks").GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "no_output_recorded");
        Is(HttpStatusCode.Conflict, await Send(HttpMethod.Post, "/api/documents", sup.Token, new { factoryId = f, typeCode = "production.line", lineId = line }), "duplicate_document");
    }

    [DockerFact]
    public async Task Daily_production_report_is_generated_from_recorded_output_and_figures_cant_be_typed_over()
    {
        var (f, line) = await NewFactory();
        var manager = await NewPerson(R.FactoryManager, f);
        var admin = await NewPerson(R.FactoryAdmin, f);
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).AddDays(-1);
        await Db(async db =>
        {
            var style = new ProductionStyle { Id = Guid.NewGuid(), Code = $"S-{Guid.NewGuid():N}"[..10], Name = "Tee" };
            db.ProductionStyles.Add(style);
            var order = new ProductionOrder { Id = Guid.NewGuid(), OrderNumber = $"PO-{Guid.NewGuid():N}"[..12], StyleId = style.Id, Quantity = 1000, DueDate = day.AddDays(30), FactoryId = f, CreatedByEmail = "t@t" };
            db.ProductionOrders.Add(order);
            await db.SaveChangesAsync();
            var start = day.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc);
            db.ProductionOutputEvents.Add(new ProductionOutputEvent { LineId = line, OrderId = order.Id, Stage = ProductionStage.SewingOutput, IntervalStartUtc = start, IntervalEndUtc = start.AddHours(1),
                ProductionDate = day, Quantity = 120, ReworkQuantity = 4, RejectQuantity = 1, OperatorsPresent = 20, Source = ProductionDataSource.Manual, RecordedByEmail = "t@t" });
            db.ProductionOutputEvents.Add(new ProductionOutputEvent { LineId = line, OrderId = order.Id, Stage = ProductionStage.Cutting, IntervalStartUtc = start, IntervalEndUtc = start.AddHours(1),
                ProductionDate = day, Quantity = 300, DowntimeMinutes = 15, DowntimeReason = "Power cut", Source = ProductionDataSource.Manual, RecordedByEmail = "t@t" });
            await db.SaveChangesAsync();
        });
        var r = await Send(HttpMethod.Post, "/api/documents", admin.Token, new { factoryId = f, typeCode = "production.daily", periodFrom = day.ToString("yyyy-MM-dd") });
        Is(HttpStatusCode.Created, r);
        var data = r.Body.GetProperty("data");
        Assert.Equal(120, data.GetProperty("sewingOutput").GetInt32());
        Assert.Equal(300, data.GetProperty("cutting").GetInt32());
        Assert.Equal(4, data.GetProperty("rework").GetInt32());
        Assert.Equal(15m, data.GetProperty("downtimeMinutes").GetDecimal());

        // A person can only add remarks: typed figures are ignored.
        var saved = await Send(HttpMethod.Put, $"/api/documents/{Id(r)}", admin.Token, new { data = new { sewingOutput = 999, remarks = "Power cut at 9:00" } });
        Is(HttpStatusCode.OK, saved);
        Assert.Equal(120, saved.Body.GetProperty("data").GetProperty("sewingOutput").GetInt32());
        Assert.Equal("Power cut at 9:00", saved.Body.GetProperty("data").GetProperty("remarks").GetString());
        Is(HttpStatusCode.OK, await Send(HttpMethod.Post, $"/api/documents/{Id(r)}/submit", admin.Token, new { }));
        Is(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, $"/api/documents/{Id(r)}/approve", admin.Token, new { }), "own_document");
        Is(HttpStatusCode.OK, await Send(HttpMethod.Post, $"/api/documents/{Id(r)}/approve", manager.Token, new { }));
        using var client = Api.RawClient();
        using var pdfReq = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{Id(r)}/export?format=pdf");
        pdfReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", manager.Token);
        pdfReq.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        var pdf = await client.SendAsync(pdfReq);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
    }
}
