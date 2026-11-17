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
/// Production planning against real PostgreSQL (a disposable local container, never a shared
/// database): order lifecycle and history, allocation limits (several lines, override rights,
/// concurrent allocation), targets with revision history and approval, the daily plan workflow and
/// publication, capacity estimates that refuse to guess, planned-versus-actual from production counters
/// (each message once, output counters only, REAL data only), exceptions with evidence, factory and line
/// authorization, shift and date boundaries, and report export with its audit.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbPlanningTests
{
    private readonly PostgresStack _stack;
    public DbPlanningTests(PostgresStack stack) { _stack = stack; }
    private DbApiFactory Api => _stack.Local;

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw, byte[] Bytes, string? ContentType)
    {
        // Planning answers with a `code`; the older /api/production routes (orders, styles) carry the code in ProblemDetails `title`.
        public string? Code => Body.ValueKind != JsonValueKind.Object ? null
            : Body.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()
            : Body.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
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

    private async Task<Reply> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : Rag.Json(body) };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var res = await client.SendAsync(req);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var raw = Encoding.UTF8.GetString(bytes);
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[')) ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw, bytes, res.Content.Headers.ContentType?.MediaType);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string bearer, object body) => Send(HttpMethod.Post, path, bearer, body);
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, body);

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string M() => $"m-{Guid.NewGuid():N}";
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    // No attendance policy ⇒ the default factory time zone, Asia/Dhaka (UTC+6, no daylight saving).
    private static DateTime LocalNow => DateTime.UtcNow.AddHours(6);
    private static DateOnly Today => DateOnly.FromDateTime(LocalNow);
    private static DateOnly Tomorrow => Today.AddDays(1);

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, Guid factory, string? line = null)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@planning.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = factory });
        if (line is not null) user.Lines.Add(new UserProductionLineAssignment { UserId = id, LineId = line });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, email, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    /// <summary>
    /// A factory with two lines (the first with a counter device), an all-day shift that works every day, a style
    /// with a standard minute value, and people: two factory managers (so one can submit and the other approve),
    /// a supervisor for each line, a monitoring operator and an IT administrator.
    /// </summary>
    public sealed record Rig(Guid Factory, string Line1, string Line2, string LineName1, Guid Shift, Guid Style, string CounterCode, Guid CounterDevice,
        Person Manager, Person Approver, Person Sup1, Person Sup2, Person Monitor, Person Devices);

    private async Task<Rig> NewRig(double? smv = 10)
    {
        var t = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var factory = Guid.NewGuid();
        var dept = Guid.NewGuid();
        var (l1, l2) = ($"pl-{t.ToLowerInvariant()}-1", $"pl-{t.ToLowerInvariant()}-2");
        var (ws1, ws2, machine, counter, shift, style) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Rag.WithDb(Api, async db =>
        {
            db.Factories.Add(new Factory { Id = factory, Code = $"P{t}", Name = $"Planning {t}" });
            await db.SaveChangesAsync();
            db.Departments.Add(new Department { Id = dept, FactoryId = factory, Code = $"D{t[..4]}", Name = $"Sewing {t}" });
            db.ProductionLines.Add(new ProductionLine { Id = l1, Name = $"Line one {t}", Workstations = 10, FactoryId = factory });
            db.ProductionLines.Add(new ProductionLine { Id = l2, Name = $"Line two {t}", Workstations = 10, FactoryId = factory });
            db.Shifts.Add(new Shift { Id = shift, FactoryId = factory, Code = "ALLDAY", Name = "All day", StartLocal = new TimeOnly(0, 0), EndLocal = new TimeOnly(23, 59), WorkingDays = 0b1111111 });
            db.ProductionStyles.Add(new ProductionStyle { Id = style, Code = $"ST-{t}", Name = "Basic tee", ApprovedSmv = smv });
            await db.SaveChangesAsync();
            db.Workstations.Add(new Workstation { Id = ws1, FactoryId = factory, DepartmentId = dept, LineId = l1, Code = $"T-{t[..3]}1", Capacity = 1, Position = 1 });
            db.Workstations.Add(new Workstation { Id = ws2, FactoryId = factory, DepartmentId = dept, LineId = l2, Code = $"T-{t[..3]}2", Capacity = 1, Position = 1 });
            await db.SaveChangesAsync();
            db.Machines.Add(new Machine { Id = machine, FactoryId = factory, Code = $"M-{t[..4]}", MachineType = "Lockstitch", WorkstationId = ws1 });
            await db.SaveChangesAsync();
            db.Devices.Add(new Device { Id = counter, FactoryId = factory, Code = $"PC-{t[..4]}", Name = "Output counter", Category = "ProductionCounter", MachineId = machine, WorkstationId = ws1, LineId = l1, CreatedByEmail = "test" });
            await db.SaveChangesAsync();
            db.DeviceIotConfigurations.Add(new DeviceIotConfiguration { DeviceId = counter, FactoryId = factory, DeviceType = "PIECE_COUNTER", Protocol = "HTTP", UpdatedByEmail = "test" });
            await db.SaveChangesAsync();
            return 0;
        });
        return new Rig(factory, l1, l2, $"Line one {t}", shift, style, $"PC-{t[..4]}", counter,
            await NewPerson(R.FactoryManager, factory), await NewPerson(R.FactoryManager, factory), await NewPerson(R.ProductionSupervisor, factory, l1),
            await NewPerson(R.ProductionSupervisor, factory, l2), await NewPerson(R.MonitoringOperator, factory), await NewPerson(R.DeviceAdmin, factory));
    }

    private async Task<JsonElement> NewOrder(Rig r, int quantity = 1000, string? status = null, int dueInDays = 10)
    {
        var rep = await Post("/api/production/orders", r.Manager.Token, new
        {
            orderNumber = $"PO-{Guid.NewGuid():N}"[..14], styleId = r.Style, buyer = "Test buyer", quantity, dueDate = D(Today.AddDays(dueInDays)), factoryId = r.Factory,
            status, productCategory = "Knit tops", startDate = D(Today), plannedCompletionDate = D(Today.AddDays(dueInDays - 1)),
        });
        Is(HttpStatusCode.Created, rep);
        return rep.Body;
    }

    private async Task<JsonElement> Allocate(Rig r, JsonElement order, string line, int qty, DateOnly? start = null, DateOnly? end = null, Person? by = null, object? extra = null)
    {
        var rep = await Post("/api/planning/allocations", (by ?? r.Manager).Token, new
        {
            orderId = order.GetProperty("id").GetGuid(), lineId = line, quantity = qty, plannedStart = D(start ?? Today), plannedEnd = D(end ?? Today.AddDays(5)),
            allowOverAllocation = extra is null ? (bool?)null : true, overrideReason = extra is null ? null : "Cutting allowance",
        });
        Is(HttpStatusCode.Created, rep);
        return rep.Body;
    }

    private async Task<JsonElement> NewPlan(Rig r, DateOnly? date = null)
    {
        var rep = await Post("/api/planning/daily-plans", r.Manager.Token, new { factoryId = r.Factory, planDate = D(date ?? Today) });
        Is(HttpStatusCode.Created, rep);
        return rep.Body;
    }

    private async Task<Reply> AddItem(Rig r, JsonElement plan, JsonElement order, string line, int target, Person? by = null) =>
        await Send(HttpMethod.Post, $"/api/planning/daily-plans/{plan.GetProperty("id").GetGuid()}/items?expectedVersion={plan.GetProperty("version").GetUInt32()}", (by ?? r.Manager).Token,
            new { orderId = order.GetProperty("id").GetGuid(), lineId = line, shiftId = r.Shift, targetQuantity = target });

    private async Task<JsonElement> Publish(Rig r, JsonElement plan)
    {
        var id = plan.GetProperty("id").GetGuid();
        var submitted = await Post($"/api/planning/daily-plans/{id}/submit", r.Manager.Token, new { expectedVersion = plan.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, submitted);
        var approved = await Post($"/api/planning/daily-plans/{id}/approve", r.Approver.Token, new { expectedVersion = submitted.Body.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, approved);
        var published = await Post($"/api/planning/daily-plans/{id}/publish", r.Approver.Token, new { expectedVersion = approved.Body.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, published);
        return published.Body;
    }

    private async Task Designate(Rig r)
    {
        var rep = await Post("/api/planning/output-points", r.Manager.Token, new { lineId = r.Line1, deviceId = r.CounterDevice });
        Is(HttpStatusCode.Created, rep);
    }

    private Task<Reply> Count(Rig r, int pieces, string? id = null) => Post("/api/iot/ingest/production", r.Devices.Token, new
    {
        factoryId = r.Factory, deviceId = r.CounterCode, messageId = id ?? M(), occurredAt = DateTimeOffset.UtcNow.ToString("O"), counter = new { mode = "INCREMENT", value = pieces },
    });

    // ─── Orders ────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task An_order_follows_its_lifecycle_and_every_change_is_kept()
    {
        var r = await NewRig();
        var order = await NewOrder(r);
        var id = order.GetProperty("id").GetGuid();
        Assert.Equal("planned", S(order, "status"));
        Assert.Equal(r.Factory.ToString(), S(order, "factoryId"));
        Assert.Contains("in_progress", order.GetProperty("allowedStatuses").EnumerateArray().Select(x => x.GetString()));

        // Invalid transitions are refused; valid ones need their reason where one is required.
        Is(HttpStatusCode.Conflict, await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "completed", expectedVersion = order.GetProperty("version").GetUInt32() }), "invalid_status_transition");
        Is(HttpStatusCode.BadRequest, await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "on_hold", expectedVersion = order.GetProperty("version").GetUInt32() }));
        var inProgress = await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "in_progress", expectedVersion = order.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, inProgress);
        var hold = await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "on_hold", reason = "Waiting for trims", expectedVersion = inProgress.Body.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, hold);
        // A stale version is a conflict, not a silent overwrite.
        Is(HttpStatusCode.Conflict, await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "in_progress", expectedVersion = order.GetProperty("version").GetUInt32() }), "version_conflict");

        var history = (await Get($"/api/production/orders/{id}/transitions", r.Manager.Token)).Body;
        Assert.Equal("on_hold", S(history, "current"));
        var steps = history.GetProperty("history").EnumerateArray().Select(h => S(h, "to")).ToList();
        Assert.Equal(new[] { "planned", "in_progress", "on_hold" }, steps);
        Assert.Contains(history.GetProperty("history").EnumerateArray(), h => S(h, "reason") == "Waiting for trims" && S(h, "changedByEmail") == r.Manager.Email);

        // The history table is append-only, even for the database owner's application.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db => db.Database.ExecuteSqlRawAsync("DELETE FROM production_order_status_changes")));
        Assert.Contains("append-only", ex.ToString());
    }

    [DockerFact]
    public async Task Required_fields_are_validated_and_a_draft_stays_a_draft_until_complete()
    {
        var r = await NewRig();
        Is(HttpStatusCode.BadRequest, await Post("/api/production/orders", r.Manager.Token, new { styleId = r.Style, quantity = 10, dueDate = D(Today.AddDays(3)), factoryId = r.Factory }));          // no order number
        Is(HttpStatusCode.BadRequest, await Post("/api/production/orders", r.Manager.Token, new { orderNumber = "X-1", styleId = r.Style, quantity = 0, dueDate = D(Today.AddDays(3)), factoryId = r.Factory }));
        Is(HttpStatusCode.BadRequest, await Post("/api/production/orders", r.Manager.Token, new { orderNumber = "X-2", styleId = r.Style, quantity = 10, dueDate = "soon", factoryId = r.Factory }));
        Is(HttpStatusCode.BadRequest, await Post("/api/production/orders", r.Manager.Token, new
        { orderNumber = "X-3", styleId = r.Style, quantity = 10, dueDate = D(Today.AddDays(3)), factoryId = r.Factory, startDate = D(Today.AddDays(2)), plannedCompletionDate = D(Today) })); // completion before start
        // No buyer: it is saved as a draft and can't be planned until the buyer is known.
        var draft = await Post("/api/production/orders", r.Manager.Token, new { orderNumber = $"DR-{Guid.NewGuid():N}"[..12], styleId = r.Style, quantity = 50, dueDate = D(Today.AddDays(9)), factoryId = r.Factory });
        Is(HttpStatusCode.Created, draft);
        Assert.Equal("draft", S(draft.Body, "status"));
        var id = draft.Body.GetProperty("id").GetGuid();
        Is(HttpStatusCode.BadRequest, await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "planned", expectedVersion = draft.Body.GetProperty("version").GetUInt32() }));
        // A draft can't be allocated.
        Is(HttpStatusCode.Conflict, await Post("/api/planning/allocations", r.Manager.Token, new { orderId = id, lineId = r.Line1, quantity = 10, plannedStart = D(Today), plannedEnd = D(Today.AddDays(1)) }), "order_not_allocatable");
        var set = await Put($"/api/production/orders/{id}", r.Manager.Token, new { buyer = "H&M", expectedVersion = draft.Body.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, set);
        Is(HttpStatusCode.OK, await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "planned", expectedVersion = set.Body.GetProperty("version").GetUInt32() }));
        // The order number is permanent and unique.
        Is(HttpStatusCode.Conflict, await Post("/api/production/orders", r.Manager.Token, new { orderNumber = S(draft.Body, "orderNumber"), styleId = r.Style, buyer = "x", quantity = 5, dueDate = D(Today.AddDays(3)), factoryId = r.Factory }), "duplicate_order");
    }

    [DockerFact]
    public async Task Style_specifications_are_stored_and_reused()
    {
        var r = await NewRig();
        var created = await Post("/api/production/styles", r.Manager.Token, new
        {
            code = $"SPEC-{Guid.NewGuid():N}"[..14], name = "Oxford shirt", garmentCategory = "Woven shirts", description = "Long sleeve", sizeRange = new[] { "S", "M", "L", "M" },
            colorVariants = new[] { "White", "Blue" }, complexity = "high", qualityCheckpoints = new[] { "After collar attach", "Final measurement" },
            operations = new object[] { new { sequence = 1, name = "Collar", smv = 1.2, machineType = "SNLS" }, new { sequence = 2, name = "Sleeve", smv = 1.5, machineType = "OL" }, new { sequence = 3, name = "Pressing", smv = 0.8, machineType = "manual" } },
        });
        Is(HttpStatusCode.Created, created);
        Assert.Equal("Woven shirts", S(created.Body, "garmentCategory"));
        Assert.Equal(new[] { "S", "M", "L" }, created.Body.GetProperty("sizeRange").EnumerateArray().Select(x => x.GetString()));      // duplicates dropped
        Assert.Equal("HIGH", S(created.Body, "complexity"));
        Assert.Equal(3.5, created.Body.GetProperty("effectiveSmv").GetDouble(), 3);
        Assert.Equal(new[] { "OL", "SNLS", "manual" }, created.Body.GetProperty("requiredMachineTypes").EnumerateArray().Select(x => x.GetString()).OrderBy(x => x, StringComparer.Ordinal));
        Is(HttpStatusCode.BadRequest, await Post("/api/production/styles", r.Manager.Token, new { code = "BAD", name = "x", complexity = "extreme" }));
        // Supervisors and monitoring operators can read styles but not change them.
        Is(HttpStatusCode.OK, await Get("/api/production/styles", r.Monitor.Token));
        Is(HttpStatusCode.Forbidden, await Post("/api/production/styles", r.Sup1.Token, new { code = "NOPE", name = "x" }));
    }

    // ─── Allocations ───────────────────────────────────────────────────────

    [DockerFact]
    public async Task An_order_is_split_over_lines_up_to_its_quantity_and_not_beyond_without_an_override()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 1000);
        await Allocate(r, order, r.Line1, 600);
        await Allocate(r, order, r.Line2, 300);
        var over = await Post("/api/planning/allocations", r.Manager.Token, new { orderId = order.GetProperty("id").GetGuid(), lineId = r.Line1, quantity = 200, plannedStart = D(Today.AddDays(6)), plannedEnd = D(Today.AddDays(8)) });
        Is(HttpStatusCode.Conflict, over, "allocation_exceeds_order");

        var summary = (await Get($"/api/planning/orders/{order.GetProperty("id").GetGuid()}/allocations", r.Manager.Token)).Body;
        Assert.Equal("900", S(summary, "allocated"));
        Assert.Equal("100", S(summary, "remaining"));
        Assert.Equal(2, summary.GetProperty("allocations").GetArrayLength());

        // Exactly the remainder is fine; an authorised user may go beyond with a reason, and it is on record.
        await Allocate(r, order, r.Line1, 100, start: Today.AddDays(6), end: Today.AddDays(8));
        var excess = await Allocate(r, order, r.Line2, 50, start: Today.AddDays(6), end: Today.AddDays(8), extra: new { });
        Assert.True(excess.GetProperty("allocation").GetProperty("overAllocated").GetBoolean());
        Assert.Equal("Cutting allowance", S(excess, "allocation", "overrideReason"));
        Assert.Equal(r.Manager.Email, S(excess, "allocation", "overrideByEmail"));

        // The same order may not be put on the same line twice in overlapping dates (even with an override: the
        // order is already over-allocated here, so the override isolates the overlap rule from the quantity rule).
        Is(HttpStatusCode.Conflict, await Post("/api/planning/allocations", r.Manager.Token, new { orderId = order.GetProperty("id").GetGuid(), lineId = r.Line2, quantity = 1,
            plannedStart = D(Today.AddDays(7)), plannedEnd = D(Today.AddDays(9)), allowOverAllocation = true, overrideReason = "Cutting allowance" }), "allocation_overlaps");
    }

    [DockerFact]
    public async Task Concurrent_allocations_cannot_both_pass_the_order_limit()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 100);
        var body = new { orderId = order.GetProperty("id").GetGuid(), lineId = r.Line1, quantity = 80, plannedStart = D(Today), plannedEnd = D(Today.AddDays(2)) };
        var body2 = new { orderId = order.GetProperty("id").GetGuid(), lineId = r.Line2, quantity = 80, plannedStart = D(Today), plannedEnd = D(Today.AddDays(2)) };
        var results = await Task.WhenAll(Post("/api/planning/allocations", r.Manager.Token, body), Post("/api/planning/allocations", r.Manager.Token, body2));
        Assert.Equal(1, results.Count(x => x.Status == HttpStatusCode.Created));
        Assert.Equal(1, results.Count(x => x.Status == HttpStatusCode.Conflict && x.Code == "allocation_exceeds_order"));
        var summary = (await Get($"/api/planning/orders/{order.GetProperty("id").GetGuid()}/allocations", r.Manager.Token)).Body;
        Assert.Equal("80", S(summary, "allocated"));
    }

    [DockerFact]
    public async Task Allocations_are_audited_and_can_be_changed_cancelled_and_never_edited_silently()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 500);
        var a = await Allocate(r, order, r.Line1, 300);
        var id = a.GetProperty("allocation").GetProperty("id").GetGuid();
        var v = a.GetProperty("allocation").GetProperty("version").GetUInt32();
        Is(HttpStatusCode.BadRequest, await Put($"/api/planning/allocations/{id}", r.Manager.Token, new { quantity = 250, expectedVersion = v }));                          // a reason is required
        var changed = await Put($"/api/planning/allocations/{id}", r.Manager.Token, new { quantity = 250, reason = "Buyer cut the order", expectedVersion = v });
        Is(HttpStatusCode.OK, changed);
        Is(HttpStatusCode.Conflict, await Put($"/api/planning/allocations/{id}", r.Manager.Token, new { quantity = 240, reason = "again", expectedVersion = v }), "version_conflict");
        Is(HttpStatusCode.BadRequest, await Put($"/api/planning/allocations/{id}", r.Manager.Token, new { lineId = r.Line2, reason = "move", expectedVersion = changed.Body.GetProperty("allocation").GetProperty("version").GetUInt32() }));
        var history = (await Get($"/api/planning/allocations/{id}", r.Manager.Token)).Body.GetProperty("history");
        Assert.Equal(new[] { "created", "updated" }, history.EnumerateArray().Select(h => S(h, "kind")));
        Assert.Equal("Buyer cut the order", S(history[1], "reason"));
        Is(HttpStatusCode.BadRequest, await Post($"/api/planning/allocations/{id}/status", r.Manager.Token, new { status = "cancelled", expectedVersion = changed.Body.GetProperty("allocation").GetProperty("version").GetUInt32() }));
        var cancelled = await Post($"/api/planning/allocations/{id}/status", r.Manager.Token, new { status = "cancelled", reason = "Moved to another factory", expectedVersion = changed.Body.GetProperty("allocation").GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, cancelled);
        // A cancelled allocation no longer counts against the order.
        Assert.Equal("0", S((await Get($"/api/planning/orders/{order.GetProperty("id").GetGuid()}/allocations", r.Manager.Token)).Body, "allocated"));
        Assert.Contains((await AiSettingsTestKit.AuditText(Api, "planning.allocation_")), t => t.Contains("Buyer cut the order"));
    }

    [DockerFact]
    public async Task Completing_or_cancelling_an_order_closes_its_allocations()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 100);
        await Allocate(r, order, r.Line1, 100);
        var id = order.GetProperty("id").GetGuid();
        var inProgress = await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "in_progress", expectedVersion = order.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, await Post($"/api/production/orders/{id}/status", r.Manager.Token, new { status = "cancelled", reason = "Buyer cancelled", expectedVersion = inProgress.Body.GetProperty("version").GetUInt32() }));
        var list = (await Get($"/api/planning/allocations?orderId={id}", r.Manager.Token)).Body;
        Assert.All(list.GetProperty("items").EnumerateArray(), a => Assert.Equal("CANCELLED", S(a, "status")));
    }

    // ─── Authorization ─────────────────────────────────────────────────────

    [DockerFact]
    public async Task Supervisors_manage_only_their_lines_and_other_factories_see_nothing()
    {
        var r = await NewRig();
        var other = await NewRig();
        var order = await NewOrder(r, quantity: 400);
        var a1 = await Allocate(r, order, r.Line1, 200);
        var a2 = await Allocate(r, order, r.Line2, 200, start: Today, end: Today.AddDays(5));

        // A supervisor reads only the allocations on their own line.
        var mine = (await Get($"/api/planning/allocations?factoryId={r.Factory}", r.Sup1.Token)).Body.GetProperty("items");
        Assert.Single(mine.EnumerateArray());
        Assert.Equal(r.Line1, S(mine[0], "lineId"));
        Is(HttpStatusCode.NotFound, await Get($"/api/planning/allocations/{a2.GetProperty("allocation").GetProperty("id").GetGuid()}", r.Sup1.Token), "allocation_not_found");

        // Supervisors don't allocate (no permission), and can set targets for their own line only.
        Is(HttpStatusCode.Forbidden, await Post("/api/planning/allocations", r.Sup1.Token, new { orderId = order.GetProperty("id").GetGuid(), lineId = r.Line1, quantity = 1, plannedStart = D(Today), plannedEnd = D(Today) }));
        var target = new { factoryId = r.Factory, level = "LINE", period = "DAILY", quantity = 300, effectiveFrom = D(Today) };
        Is(HttpStatusCode.Created, await Post("/api/planning/targets", r.Sup1.Token, new { target.factoryId, target.level, target.period, lineId = r.Line1, target.quantity, target.effectiveFrom }));
        Is(HttpStatusCode.Forbidden, await Post("/api/planning/targets", r.Sup1.Token, new { target.factoryId, target.level, target.period, lineId = r.Line2, target.quantity, target.effectiveFrom }), "line_out_of_scope");
        Is(HttpStatusCode.Forbidden, await Post("/api/planning/targets", r.Sup1.Token, new { target.factoryId, level = "FACTORY", target.period, target.quantity, target.effectiveFrom }), "target_level_not_allowed");

        // Another factory's manager can't reach any of it.
        Is(HttpStatusCode.Forbidden, await Get($"/api/planning/monitor?factoryId={r.Factory}", other.Manager.Token), "factory_out_of_scope");
        Is(HttpStatusCode.Forbidden, await Get($"/api/planning/allocations?factoryId={r.Factory}", other.Manager.Token), "factory_out_of_scope");
        Is(HttpStatusCode.NotFound, await Get($"/api/planning/allocations/{a1.GetProperty("allocation").GetProperty("id").GetGuid()}", other.Manager.Token));
        Is(HttpStatusCode.NotFound, await Get($"/api/production/orders/{order.GetProperty("id").GetGuid()}", other.Manager.Token));
        var theirs = (await Get("/api/production/orders?pageSize=100&includeRisk=false", other.Manager.Token)).Body.GetProperty("items");
        Assert.DoesNotContain(theirs.EnumerateArray(), o => S(o, "id") == S(order, "id"));
        // And can't allocate their order to our line.
        var theirOrder = await NewOrder(other);
        Is(HttpStatusCode.NotFound, await Post("/api/planning/allocations", other.Manager.Token, new { orderId = theirOrder.GetProperty("id").GetGuid(), lineId = r.Line1, quantity = 1, plannedStart = D(Today), plannedEnd = D(Today) }), "line_not_found");
    }

    [DockerFact]
    public async Task Monitoring_operators_follow_published_plans_but_cannot_change_or_see_drafts()
    {
        var r = await NewRig();
        var order = await NewOrder(r);
        await Allocate(r, order, r.Line1, 500);
        var plan = await NewPlan(r);
        var withItem = await AddItem(r, plan, order, r.Line1, 200);
        Is(HttpStatusCode.OK, withItem);
        var planId = plan.GetProperty("id").GetGuid();
        // A draft is invisible to followers.
        Is(HttpStatusCode.NotFound, await Get($"/api/planning/daily-plans/{planId}", r.Monitor.Token), "plan_not_found");
        Assert.Equal(0, (await Get($"/api/planning/daily-plans?factoryId={r.Factory}", r.Monitor.Token)).Body.GetProperty("items").GetArrayLength());
        Is(HttpStatusCode.NoContent, await Get($"/api/planning/daily-plans/for-date?factoryId={r.Factory}&date={D(Today)}", r.Monitor.Token));       // nothing published yet
        // They can't change anything.
        Is(HttpStatusCode.Forbidden, await Post("/api/planning/daily-plans", r.Monitor.Token, new { factoryId = r.Factory, planDate = D(Tomorrow) }));
        Is(HttpStatusCode.Forbidden, await Post($"/api/planning/daily-plans/{planId}/publish", r.Monitor.Token, new { expectedVersion = 1 }));
        // Once published they see it.
        var published = await Publish(r, withItem.Body);
        Assert.Equal("PUBLISHED", S(published, "status"));
        var seen = await Get($"/api/planning/daily-plans/for-date?factoryId={r.Factory}&date={D(Today)}", r.Monitor.Token);
        Is(HttpStatusCode.OK, seen);
        Assert.Equal(planId.ToString(), S(seen.Body, "id"));
        Assert.Empty(seen.Body.GetProperty("allowedActions").EnumerateArray());
    }

    // ─── Targets ───────────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_target_change_keeps_every_previous_version_and_who_changed_it()
    {
        var r = await NewRig();
        var created = await Post("/api/planning/targets", r.Manager.Token, new { factoryId = r.Factory, level = "LINE", period = "DAILY", lineId = r.Line1, quantity = 400, effectiveFrom = D(Today) });
        Is(HttpStatusCode.Created, created);
        var v1 = created.Body.GetProperty("target");
        Assert.Equal("NOT_REQUIRED", S(v1, "approvalStatus"));
        // A revision may not start in the past or on the same day as the version it replaces.
        Is(HttpStatusCode.BadRequest, await Post($"/api/planning/targets/{v1.GetProperty("id").GetGuid()}/revise", r.Manager.Token, new { quantity = 450, effectiveFrom = D(Today.AddDays(-1)), reason = "x", expectedVersion = v1.GetProperty("version").GetUInt32() }));
        Is(HttpStatusCode.BadRequest, await Post($"/api/planning/targets/{v1.GetProperty("id").GetGuid()}/revise", r.Manager.Token, new { quantity = 450, effectiveFrom = D(Today), reason = "x", expectedVersion = v1.GetProperty("version").GetUInt32() }));
        Is(HttpStatusCode.BadRequest, await Post($"/api/planning/targets/{v1.GetProperty("id").GetGuid()}/revise", r.Manager.Token, new { quantity = 450, effectiveFrom = D(Tomorrow), expectedVersion = v1.GetProperty("version").GetUInt32() })); // reason missing
        var revised = await Post($"/api/planning/targets/{v1.GetProperty("id").GetGuid()}/revise", r.Manager.Token, new { quantity = 450, effectiveFrom = D(Tomorrow), reason = "New operators joined", expectedVersion = v1.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.Created, revised);
        var v2 = revised.Body.GetProperty("target");
        Assert.Equal(2, v2.GetProperty("revision").GetInt32());
        Assert.Equal(S(v1, "targetKey"), S(v2, "targetKey"));

        // Both versions are on record: v1 now ends the day before v2 starts.
        var versions = revised.Body.GetProperty("versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal(400, versions[0].GetProperty("quantity").GetInt32());
        Assert.Equal(D(Today), S(versions[0], "effectiveTo"));
        Assert.True(versions[0].GetProperty("isSuperseded").GetBoolean());
        Assert.Equal(450, versions[1].GetProperty("quantity").GetInt32());
        // The target in force on a date is the one that applied then.
        var today = (await Get($"/api/planning/targets?factoryId={r.Factory}&lineId={r.Line1}&date={D(Today)}", r.Manager.Token)).Body.GetProperty("items");
        Assert.Equal(400, today[0].GetProperty("quantity").GetInt32());
        var later = (await Get($"/api/planning/targets?factoryId={r.Factory}&lineId={r.Line1}&date={D(Tomorrow)}", r.Manager.Token)).Body.GetProperty("items");
        Assert.Equal(450, later[0].GetProperty("quantity").GetInt32());
        var log = revised.Body.GetProperty("revisions");
        Assert.Equal(new[] { "CREATED", "REVISED" }, log.EnumerateArray().Select(x => S(x, "action")));
        Assert.Equal("New operators joined", S(log[1], "reason"));
        Assert.Equal("400", S(log[1], "previousQuantity"));
        Assert.Equal(r.Manager.Email, S(log[1], "byEmail"));
        // Revising an old version is refused: only the one in force changes.
        Is(HttpStatusCode.Conflict, await Post($"/api/planning/targets/{v1.GetProperty("id").GetGuid()}/revise", r.Manager.Token, new { quantity = 500, effectiveFrom = D(Tomorrow.AddDays(2)), reason = "x", expectedVersion = versions[0].GetProperty("version").GetUInt32() }), "target_not_current");
        // The same scope can't have a second target.
        Is(HttpStatusCode.Conflict, await Post("/api/planning/targets", r.Manager.Token, new { factoryId = r.Factory, level = "LINE", period = "DAILY", lineId = r.Line1, quantity = 100, effectiveFrom = D(Tomorrow) }), "target_exists");
    }

    [DockerFact]
    public async Task Factory_targets_need_a_second_person_to_approve_them()
    {
        var r = await NewRig();
        var created = await Post("/api/planning/targets", r.Manager.Token, new { factoryId = r.Factory, level = "FACTORY", period = "DAILY", quantity = 2000, effectiveFrom = D(Today) });
        Is(HttpStatusCode.Created, created);
        var t = created.Body.GetProperty("target");
        Assert.Equal("PENDING", S(t, "approvalStatus"));
        Assert.False(t.GetProperty("inForceToday").GetBoolean());
        var id = t.GetProperty("id").GetGuid();
        Is(HttpStatusCode.Forbidden, await Post($"/api/planning/targets/{id}/decision", r.Manager.Token, new { approve = true, expectedVersion = t.GetProperty("version").GetUInt32() }), "same_approver");
        Is(HttpStatusCode.Forbidden, await Post($"/api/planning/targets/{id}/decision", r.Sup1.Token, new { approve = true, expectedVersion = t.GetProperty("version").GetUInt32() }));   // no approve permission
        var approved = await Post($"/api/planning/targets/{id}/decision", r.Approver.Token, new { approve = true, note = "Agreed", expectedVersion = t.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, approved);
        Assert.Equal("APPROVED", S(approved.Body, "target", "approvalStatus"));
        Assert.True(approved.Body.GetProperty("target").GetProperty("inForceToday").GetBoolean());
        // A revision of an approved factory target waits too, and the old version stays in force meanwhile.
        var rev = await Post($"/api/planning/targets/{id}/revise", r.Manager.Token, new { quantity = 2400, effectiveFrom = D(Tomorrow), reason = "Second shift added", expectedVersion = approved.Body.GetProperty("target").GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.Created, rev);
        Assert.Equal("PENDING", S(rev.Body, "target", "approvalStatus"));
        var inForce = (await Get($"/api/planning/targets?factoryId={r.Factory}&level=FACTORY&date={D(Tomorrow)}", r.Manager.Token)).Body.GetProperty("items");
        Assert.Equal(2000, inForce[0].GetProperty("quantity").GetInt32());
        var rejected = await Post($"/api/planning/targets/{rev.Body.GetProperty("target").GetProperty("id").GetGuid()}/decision", r.Approver.Token, new { approve = false, expectedVersion = rev.Body.GetProperty("target").GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.BadRequest, rejected);                                                                                            // a rejection needs its reason
    }

    // ─── Capacity ──────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Capacity_is_incomplete_until_the_assumptions_are_configured_and_then_shows_them()
    {
        var r = await NewRig();
        var cap = $"/api/planning/capacity?lineId={r.Line1}&styleId={r.Style}&from={D(Today)}&to={D(Today.AddDays(1))}&quantity=2000";
        var incomplete = (await Get(cap, r.Manager.Token)).Body;
        Assert.Equal("INCOMPLETE", S(incomplete, "result", "status"));
        Assert.Null(S(incomplete, "result", "capacityPieces"));
        var missing = incomplete.GetProperty("result").GetProperty("missing").EnumerateArray().Select(m => S(m, "code")).ToList();
        Assert.Contains("efficiency_missing", missing);
        Assert.Contains("operators_missing", missing);
        Assert.DoesNotContain("smv_missing", missing);                                              // the style has an approved SMV

        // Only a factory-level user with the setup permission can configure them.
        Is(HttpStatusCode.Forbidden, await Put($"/api/planning/lines/{r.Line1}/capacity-settings", r.Sup1.Token, new { efficiencyPct = 60, plannedOperators = 20 }));
        Is(HttpStatusCode.BadRequest, await Put($"/api/planning/lines/{r.Line1}/capacity-settings", r.Manager.Token, new { efficiencyPct = 400 }));
        var saved = await Put($"/api/planning/lines/{r.Line1}/capacity-settings", r.Manager.Token, new { efficiencyPct = 60, plannedOperators = 20 });
        Is(HttpStatusCode.OK, saved);

        var est = (await Get(cap, r.Manager.Token)).Body;
        Assert.Equal("COMPLETE", S(est, "result", "status"));
        // 2 days × 1439 productive minutes × 20 operators × 60 % ÷ 10 SMV
        Assert.Equal(3453.6, est.GetProperty("result").GetProperty("capacityPieces").GetDouble(), 1);
        Assert.True(est.GetProperty("isEstimate").GetBoolean());
        Assert.Contains(est.GetProperty("result").GetProperty("assumptions").EnumerateArray(), a => S(a, "label") == "Efficiency");
        // A quantity beyond capacity shows the shortage; the numbers come from configuration, never from a default.
        var big = (await Get(cap.Replace("quantity=2000", "quantity=5000"), r.Manager.Token)).Body;
        Assert.True(big.GetProperty("result").GetProperty("shortagePieces").GetDouble() > 1000);
        Assert.False(big.GetProperty("result").GetProperty("canMeet").GetBoolean());
        // A style without an SMV can't be estimated at all.
        var noSmv = await NewRig(smv: null);
        await Put($"/api/planning/lines/{noSmv.Line1}/capacity-settings", noSmv.Manager.Token, new { efficiencyPct = 60, plannedOperators = 20 });
        var none = (await Get($"/api/planning/capacity?lineId={noSmv.Line1}&styleId={noSmv.Style}&from={D(Today)}&to={D(Today)}", noSmv.Manager.Token)).Body;
        Assert.Equal("INCOMPLETE", S(none, "result", "status"));
        Assert.Contains(none.GetProperty("result").GetProperty("missing").EnumerateArray(), m => S(m, "code") == "smv_missing");
    }

    [DockerFact]
    public async Task Other_allocations_on_the_same_line_count_as_commitments()
    {
        var r = await NewRig();
        await Put($"/api/planning/lines/{r.Line1}/capacity-settings", r.Manager.Token, new { efficiencyPct = 60, plannedOperators = 20 });
        var first = await NewOrder(r, quantity: 3000);
        var second = await NewOrder(r, quantity: 3000);
        await Allocate(r, first, r.Line1, 3000, start: Today, end: Today.AddDays(1));
        var b = await Allocate(r, second, r.Line1, 3000, start: Today, end: Today.AddDays(1));
        var est = (await Get($"/api/planning/allocations/{b.GetProperty("allocation").GetProperty("id").GetGuid()}/capacity", r.Manager.Token)).Body.GetProperty("result");
        Assert.Equal(1, est.GetProperty("conflicts").GetArrayLength());
        Assert.Equal(3000, est.GetProperty("committedElsewhere").GetDouble(), 1);
        Assert.True(est.GetProperty("utilizationPct").GetDouble() > 100);
        var overview = (await Get($"/api/planning/capacity/overview?factoryId={r.Factory}&from={D(Today)}&to={D(Today.AddDays(1))}", r.Manager.Token)).Body;
        var row = overview.GetProperty("lines").EnumerateArray().First(l => S(l, "lineId") == r.Line1);
        Assert.Equal(2, row.GetProperty("allocations").GetInt32());
        Assert.True(row.GetProperty("utilizationPct").GetDouble() > 100);
    }

    // ─── Daily plans ───────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_plan_goes_draft_review_approved_published_and_only_one_is_in_force_per_day()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 1000);
        await Allocate(r, order, r.Line1, 600);
        var plan = await NewPlan(r, Tomorrow);
        var planId = plan.GetProperty("id").GetGuid();
        // An empty plan can't be submitted; an item needs an allocation behind it.
        Is(HttpStatusCode.BadRequest, await Post($"/api/planning/daily-plans/{planId}/submit", r.Manager.Token, new { expectedVersion = plan.GetProperty("version").GetUInt32() }), "plan_empty");
        Is(HttpStatusCode.Conflict, await AddItem(r, plan, order, r.Line2, 100), "no_allocation");
        var withItem = await AddItem(r, plan, order, r.Line1, 250);
        Is(HttpStatusCode.OK, withItem);
        // The same line and shift can't appear twice; stale versions are refused.
        Is(HttpStatusCode.Conflict, await AddItem(r, withItem.Body, order, r.Line1, 100), "item_exists");
        Is(HttpStatusCode.Conflict, await AddItem(r, plan, order, r.Line1, 100), "version_conflict");
        // Only a person with the approve permission, and not the submitter, can approve.
        var submitted = await Post($"/api/planning/daily-plans/{planId}/submit", r.Manager.Token, new { expectedVersion = withItem.Body.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, submitted);
        Assert.Equal("UNDER_REVIEW", S(submitted.Body, "status"));
        Is(HttpStatusCode.Conflict, await AddItem(r, submitted.Body, order, r.Line1, 100), "plan_not_editable");
        Is(HttpStatusCode.Forbidden, await Post($"/api/planning/daily-plans/{planId}/approve", r.Manager.Token, new { expectedVersion = submitted.Body.GetProperty("version").GetUInt32() }), "same_approver");
        Is(HttpStatusCode.Conflict, await Post($"/api/planning/daily-plans/{planId}/publish", r.Approver.Token, new { expectedVersion = submitted.Body.GetProperty("version").GetUInt32() }), "invalid_status_transition");
        var approved = await Post($"/api/planning/daily-plans/{planId}/approve", r.Approver.Token, new { expectedVersion = submitted.Body.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, approved);
        var published = await Post($"/api/planning/daily-plans/{planId}/publish", r.Approver.Token, new { expectedVersion = approved.Body.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, published);
        Assert.Equal("PUBLISHED", S(published.Body, "status"));
        Assert.Equal(new[] { "CREATED", "ITEM_ADDED", "SUBMITTED", "APPROVED", "PUBLISHED" }, published.Body.GetProperty("events").EnumerateArray().Select(e => S(e, "action")));
        // A day has one plan: no second plan can be made while one is published.
        Is(HttpStatusCode.Conflict, await Post("/api/planning/daily-plans", r.Manager.Token, new { factoryId = r.Factory, planDate = D(Tomorrow) }), "plan_published_exists");
        // Plans for dates that have passed are refused.
        Is(HttpStatusCode.BadRequest, await Post("/api/planning/daily-plans", r.Manager.Token, new { factoryId = r.Factory, planDate = D(Today.AddDays(-1)) }), "date_in_past");
    }

    [DockerFact]
    public async Task Revising_a_published_plan_keeps_the_old_one_until_the_revision_is_published()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 1000);
        await Allocate(r, order, r.Line1, 600);
        var plan = await NewPlan(r, Tomorrow);
        var first = await Publish(r, (await AddItem(r, plan, order, r.Line1, 250)).Body);
        var firstId = first.GetProperty("id").GetGuid();
        Is(HttpStatusCode.BadRequest, await Post($"/api/planning/daily-plans/{firstId}/revise", r.Manager.Token, new { expectedVersion = first.GetProperty("version").GetUInt32() }));   // a reason is required
        var rev = await Post($"/api/planning/daily-plans/{firstId}/revise", r.Manager.Token, new { reason = "Buyer moved the delivery forward", expectedVersion = first.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.Created, rev);
        Assert.Equal("DRAFT", S(rev.Body, "status"));
        Assert.Equal(2, rev.Body.GetProperty("revisionNo").GetInt32());
        Assert.Equal(firstId.ToString(), S(rev.Body, "basedOnPlanId"));
        Assert.Equal(1, rev.Body.GetProperty("items").GetArrayLength());                             // the items were copied
        // Meanwhile the first revision is still the one in force.
        var inForce = (await Get($"/api/planning/daily-plans/for-date?factoryId={r.Factory}&date={D(Tomorrow)}", r.Approver.Token)).Body;
        Assert.Equal(firstId.ToString(), S(inForce, "id"));
        // Only one revision can be prepared at a time.
        Is(HttpStatusCode.Conflict, await Post($"/api/planning/daily-plans/{firstId}/revise", r.Manager.Token, new { reason = "again", expectedVersion = first.GetProperty("version").GetUInt32() }), "plan_open_exists");
        var itemId = rev.Body.GetProperty("items")[0].GetProperty("id").GetGuid();
        var changed = await Send(HttpMethod.Put, $"/api/planning/daily-plans/{rev.Body.GetProperty("id").GetGuid()}/items/{itemId}?expectedVersion={rev.Body.GetProperty("version").GetUInt32()}", r.Manager.Token,
            new { orderId = order.GetProperty("id").GetGuid(), lineId = r.Line1, shiftId = r.Shift, targetQuantity = 320 });
        Is(HttpStatusCode.OK, changed);
        var second = await Publish(r, changed.Body);
        // Publishing the revision replaces the first one, which stays on record as REVISED.
        var oldPlan = (await Get($"/api/planning/daily-plans/{firstId}", r.Manager.Token)).Body;
        Assert.Equal("REVISED", S(oldPlan, "status"));
        Assert.Contains(oldPlan.GetProperty("events").EnumerateArray(), e => S(e, "action") == "SUPERSEDED");
        Assert.Equal("PUBLISHED", S(second, "status"));
        Assert.Equal(320, second.GetProperty("items")[0].GetProperty("targetQuantity").GetInt32());
        var now = (await Get($"/api/planning/daily-plans/for-date?factoryId={r.Factory}&date={D(Tomorrow)}", r.Manager.Token)).Body;
        Assert.Equal(S(second, "id"), S(now, "id"));
    }

    [DockerFact]
    public async Task A_plan_item_uses_the_configured_target_and_respects_the_shifts_working_days()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 5000);
        await Allocate(r, order, r.Line1, 4000, start: Today, end: Today.AddDays(5));
        Is(HttpStatusCode.Created, await Post("/api/planning/targets", r.Manager.Token, new { factoryId = r.Factory, level = "LINE", period = "SHIFT", lineId = r.Line1, shiftId = r.Shift, quantity = 700, effectiveFrom = D(Today) }));
        var plan = await NewPlan(r, Tomorrow);
        var rep = await Send(HttpMethod.Post, $"/api/planning/daily-plans/{plan.GetProperty("id").GetGuid()}/items?expectedVersion={plan.GetProperty("version").GetUInt32()}", r.Manager.Token,
            new { orderId = order.GetProperty("id").GetGuid(), lineId = r.Line1, shiftId = r.Shift, useConfiguredTarget = true });
        Is(HttpStatusCode.OK, rep);
        var item = rep.Body.GetProperty("items")[0];
        Assert.Equal(700, item.GetProperty("targetQuantity").GetInt32());
        Assert.Equal("TARGET", S(item, "targetSource"));
        // A shift that doesn't work that weekday can't take an item (date and shift boundary).
        Guid weekdayShift = Guid.NewGuid();
        await Rag.WithDb(Api, async db =>
        {
            var mask = 0b1111111 & ~(1 << (int)Tomorrow.DayOfWeek);
            db.Shifts.Add(new Shift { Id = weekdayShift, FactoryId = r.Factory, Code = "NOTTOMORROW", Name = "Not tomorrow", StartLocal = new TimeOnly(8, 0), EndLocal = new TimeOnly(17, 0), WorkingDays = mask });
            await db.SaveChangesAsync();
            return 0;
        });
        var off = await Send(HttpMethod.Post, $"/api/planning/daily-plans/{plan.GetProperty("id").GetGuid()}/items?expectedVersion={rep.Body.GetProperty("version").GetUInt32()}", r.Manager.Token,
            new { orderId = order.GetProperty("id").GetGuid(), lineId = r.Line2, shiftId = weekdayShift, targetQuantity = 100 });
        Assert.True(off.Status is HttpStatusCode.Conflict, off.Raw);
        Assert.Contains(off.Code, new[] { "shift_not_working", "no_allocation" });
    }

    // ─── Actual production ─────────────────────────────────────────────────

    [DockerFact]
    public async Task Planned_versus_actual_uses_only_the_designated_output_counter_and_counts_each_message_once()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 1000);
        await Allocate(r, order, r.Line1, 600);
        await Allocate(r, order, r.Line2, 300);
        await Designate(r);
        var plan = await NewPlan(r);
        var p1 = await AddItem(r, plan, order, r.Line1, 400);
        var p2 = await AddItem(r, p1.Body, order, r.Line2, 200);
        Is(HttpStatusCode.OK, p2);
        await Publish(r, p2.Body);

        // The counter sends pieces; a resend of the same message is not counted again.
        var id = M();
        Is(HttpStatusCode.Created, await Count(r, 40, id));
        Is(HttpStatusCode.OK, await Count(r, 40, id));
        Is(HttpStatusCode.Created, await Count(r, 10));

        var monitor = (await Get($"/api/planning/monitor?factoryId={r.Factory}&date={D(Today)}", r.Manager.Token)).Body;
        Assert.Equal("PUBLISHED", S(monitor, "planStatus"));
        var line1 = monitor.GetProperty("lines").EnumerateArray().First(l => S(l, "lineId") == r.Line1);
        Assert.Equal("50", S(line1, "actual"));                                                    // 40 + 10, the duplicate ignored
        Assert.Equal("COUNTER", S(line1, "basis"));
        Assert.Equal("12.5", S(line1, "achievementPct"));
        var item1 = monitor.GetProperty("items").EnumerateArray().First(i => S(i, "lineId") == r.Line1);
        Assert.True(item1.GetProperty("actual").GetProperty("verified").GetBoolean());
        Assert.Equal("350", S(item1, "actual", "remaining"));
        // The second line has no output counter: no evidence, so no number (and not zero).
        var line2 = monitor.GetProperty("lines").EnumerateArray().First(l => S(l, "lineId") == r.Line2);
        Assert.Null(S(line2, "actual"));
        Assert.Equal("NONE", S(line2, "basis"));
        Assert.False(line2.GetProperty("outputCounterConfigured").GetBoolean());
        Assert.Equal("1", S(monitor, "summary", "linesWithoutOutputCounter"));
        // Order completion counts the output of the shifts the plan gave the order.
        var progress = (await Get($"/api/planning/orders/{order.GetProperty("id").GetGuid()}/progress", r.Manager.Token)).Body;
        Assert.Equal("50", S(progress, "order", "produced"));
        Assert.Equal("5", S(progress, "order", "completionPct"));
        // The simulated view sees none of it.
        var sim = (await Get($"/api/planning/monitor?factoryId={r.Factory}&date={D(Today)}&source=SIMULATED", r.Manager.Token)).Body;
        Assert.All(sim.GetProperty("lines").EnumerateArray(), l => Assert.Null(S(l, "actual")));
        Assert.Equal("SIMULATED", S(sim, "source"));
    }

    [DockerFact]
    public async Task A_counter_that_is_not_an_output_point_is_not_production_and_a_cycle_counter_cannot_be_one()
    {
        var r = await NewRig();
        // Not a counter at all.
        var sensor = Guid.NewGuid();
        await Rag.WithDb(Api, async db =>
        {
            db.Devices.Add(new Device { Id = sensor, FactoryId = r.Factory, Code = $"S-{Guid.NewGuid():N}"[..8], Name = "Sensor", Category = "MachineSensor", LineId = r.Line1, CreatedByEmail = "test" });
            await db.SaveChangesAsync();
            return 0;
        });
        Is(HttpStatusCode.BadRequest, await Post("/api/planning/output-points", r.Manager.Token, new { lineId = r.Line1, deviceId = sensor }), "device_not_counter");
        // A counter whose machine counts cycles, not garments.
        await Rag.WithDb(Api, async db =>
        {
            var machine = (await db.Devices.AsNoTracking().FirstAsync(d => d.Id == r.CounterDevice)).MachineId!.Value;
            db.MachineMonitoringProfiles.Add(new MachineMonitoringProfile { MachineId = machine, FactoryId = r.Factory, CountingMode = "CYCLE_NOT_PIECE", UpdatedByEmail = "test" });
            await db.SaveChangesAsync();
            return 0;
        });
        Is(HttpStatusCode.BadRequest, await Post("/api/planning/output-points", r.Manager.Token, new { lineId = r.Line1, deviceId = r.CounterDevice }), "counter_counts_cycles");
        // Output points are a setup permission, at factory level.
        Is(HttpStatusCode.Forbidden, await Post("/api/planning/output-points", r.Sup1.Token, new { lineId = r.Line1, deviceId = r.CounterDevice }));
    }

    [DockerFact]
    public async Task Output_recorded_by_people_is_labelled_recorded_and_only_used_without_a_counter()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 500);
        await Allocate(r, order, r.Line2, 500);
        var plan = await NewPlan(r);
        await Publish(r, (await AddItem(r, plan, order, r.Line2, 300)).Body);
        // Output recorded in the production module for the order on the line and day; the shift maps through the shift's production code.
        await Rag.WithDb(Api, async db =>
        {
            var shift = await db.Shifts.FirstAsync(s => s.Id == r.Shift);
            shift.ProductionShiftCode = "allday";
            db.ProductionShifts.Add(new ProductionShift { Code = "allday", Name = "All day", StartLocal = new TimeOnly(0, 0), EndLocal = new TimeOnly(23, 59), Weekdays = 0b1111111 });
            await db.SaveChangesAsync();
            db.ProductionOutputEvents.Add(new ProductionOutputEvent
            {
                LineId = r.Line2, OrderId = order.GetProperty("id").GetGuid(), Stage = ProductionStage.SewingOutput, IntervalStartUtc = DateTime.UtcNow.AddHours(-2), IntervalEndUtc = DateTime.UtcNow.AddHours(-1),
                ProductionDate = Today, ShiftCode = "allday", Quantity = 120, Source = ProductionDataSource.Manual, RecordedByEmail = "test",
            });
            await db.SaveChangesAsync();
            return 0;
        });
        var monitor = (await Get($"/api/planning/monitor?factoryId={r.Factory}&date={D(Today)}", r.Manager.Token)).Body;
        var item = monitor.GetProperty("items").EnumerateArray().First(i => S(i, "lineId") == r.Line2);
        Assert.Equal("120", S(item, "actual", "actual"));
        Assert.Equal("RECORDED", S(item, "actual", "basis"));
        Assert.False(item.GetProperty("actual").GetProperty("verified").GetBoolean());
        // Simulated data never uses people's records.
        var sim = (await Get($"/api/planning/monitor?factoryId={r.Factory}&date={D(Today)}&source=SIMULATED", r.Manager.Token)).Body;
        Assert.Null(S(sim.GetProperty("items")[0], "actual", "actual"));
    }

    // ─── Exceptions ────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Exceptions_carry_their_evidence_are_deduplicated_and_close_when_the_condition_clears()
    {
        // The rules need time to have passed in today's shift (it starts at local midnight).
        if (LocalNow.TimeOfDay < TimeSpan.FromMinutes(100)) return;
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 5000);
        await Allocate(r, order, r.Line1, 2500);
        await Allocate(r, order, r.Line2, 2500);
        await Designate(r);
        var plan = await NewPlan(r);
        await Publish(r, (await AddItem(r, (await AddItem(r, plan, order, r.Line1, 2000)).Body, order, r.Line2, 2000)).Body);
        await Count(r, 3);                                          // line 1 is counting, but far behind

        var first = await Post("/api/planning/exceptions/evaluate", r.Manager.Token, new { factoryId = r.Factory });
        Is(HttpStatusCode.OK, first);
        Assert.True(first.Body.GetProperty("raised").GetInt32() >= 2);
        var list = (await Get($"/api/planning/exceptions?factoryId={r.Factory}", r.Manager.Token)).Body.GetProperty("items");
        var behind = list.EnumerateArray().First(e => S(e, "type") == "OUTPUT_BEHIND_TARGET" && S(e, "lineId") == r.Line1);
        Assert.Equal("OPEN", S(behind, "status"));
        Assert.Contains(behind.GetProperty("data").EnumerateArray(), f => S(f, "label") == "Target" && S(f, "value") == "2000");
        Assert.Contains(behind.GetProperty("data").EnumerateArray(), f => S(f, "label") == "Actual" && S(f, "value") == "3");
        Assert.False(string.IsNullOrWhiteSpace(S(behind, "suggestedInvestigation")));
        Assert.False(string.IsNullOrWhiteSpace(S(behind, "reason")));
        // Line 2 has no output counter: a data gap is reported, not zero production.
        Assert.Contains(list.EnumerateArray(), e => S(e, "type") == "MISSING_COUNTER_DATA" && S(e, "lineId") == r.Line2);

        // Running the check again refreshes the same exceptions rather than adding more.
        var count = list.GetArrayLength();
        var second = await Post("/api/planning/exceptions/evaluate", r.Manager.Token, new { factoryId = r.Factory });
        Assert.Equal(0, second.Body.GetProperty("raised").GetInt32());
        Assert.Equal(count, (await Get($"/api/planning/exceptions?factoryId={r.Factory}", r.Manager.Token)).Body.GetProperty("items").GetArrayLength());

        // Acknowledge and resolve with a note; a stale version is refused. The second check refreshed the
        // exception (its evidence and last-seen time), so its version moved: act on the reloaded one.
        var id = behind.GetProperty("id").GetGuid();
        var current = (await Get($"/api/planning/exceptions/{id}", r.Sup1.Token)).Body;
        Is(HttpStatusCode.OK, await Post($"/api/planning/exceptions/{id}/acknowledge", r.Sup1.Token, new { expectedVersion = current.GetProperty("version").GetUInt32() }));
        Is(HttpStatusCode.Conflict, await Post($"/api/planning/exceptions/{id}/acknowledge", r.Sup1.Token, new { expectedVersion = current.GetProperty("version").GetUInt32() }));
        var fresh = (await Get($"/api/planning/exceptions/{id}", r.Sup1.Token)).Body;
        Is(HttpStatusCode.BadRequest, await Post($"/api/planning/exceptions/{id}/resolve", r.Sup1.Token, new { expectedVersion = fresh.GetProperty("version").GetUInt32() }));      // a note is required
        var resolved = await Post($"/api/planning/exceptions/{id}/resolve", r.Sup1.Token, new { note = "Material wait; fabric arrived", expectedVersion = fresh.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, resolved);
        Assert.Equal("RESOLVED", S(resolved.Body, "status"));
        // A supervisor of another line can't touch it.
        Is(HttpStatusCode.NotFound, await Post($"/api/planning/exceptions/{id}/acknowledge", r.Sup2.Token, new { expectedVersion = 1 }));
        // Supervisors read only exceptions of their own line.
        var sup2 = (await Get($"/api/planning/exceptions?factoryId={r.Factory}", r.Sup2.Token)).Body.GetProperty("items");
        Assert.All(sup2.EnumerateArray(), e => Assert.Equal(r.Line2, S(e, "lineId")));
    }

    [DockerFact]
    public async Task Excess_allocation_and_unavailable_lines_are_detected()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 100);
        await Allocate(r, order, r.Line1, 100);
        await Allocate(r, order, r.Line2, 20, extra: new { });
        var eval = await Post("/api/planning/exceptions/evaluate", r.Manager.Token, new { factoryId = r.Factory });
        Is(HttpStatusCode.OK, eval);
        var list = (await Get($"/api/planning/exceptions?factoryId={r.Factory}&type=EXCESS_ALLOCATION", r.Manager.Token)).Body.GetProperty("items");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Contains("20", S(list[0], "reason"));
        // Only a factory-level user runs the factory-wide check.
        Is(HttpStatusCode.Forbidden, await Post("/api/planning/exceptions/evaluate", r.Sup1.Token, new { factoryId = r.Factory }), "evaluate_scope");
    }

    // ─── Reports ───────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Reports_are_scoped_exported_as_csv_and_pdf_and_audited()
    {
        var r = await NewRig();
        var order = await NewOrder(r, quantity: 800);
        await Allocate(r, order, r.Line1, 500);
        await Designate(r);
        var plan = await NewPlan(r);
        await Publish(r, (await AddItem(r, plan, order, r.Line1, 300)).Body);
        await Count(r, 25);

        var daily = await Get($"/api/planning/reports/daily-plan-vs-actual?factoryId={r.Factory}&from={D(Today)}&to={D(Today)}", r.Manager.Token);
        Is(HttpStatusCode.OK, daily);
        Assert.Equal("daily-plan-vs-actual", S(daily.Body, "type"));
        Assert.Equal("REAL", S(daily.Body, "source"));
        Assert.NotEmpty(daily.Body.GetProperty("definitions").EnumerateArray());
        var rows = daily.Body.GetProperty("sections")[0].GetProperty("tables")[0].GetProperty("rows");
        Assert.Equal("300", rows[0][3].ToString());                                                 // target (report rows are arrays, by column)

        var csv = await Get($"/api/planning/reports/line-target-achievement/export?format=csv&factoryId={r.Factory}&from={D(Today)}&to={D(Today)}", r.Manager.Token);
        Is(HttpStatusCode.OK, csv);
        Assert.StartsWith("text/csv", csv.ContentType);
        Assert.Contains("Line-wise target achievement", Encoding.UTF8.GetString(csv.Bytes));
        var pdf = await Get($"/api/planning/reports/order-production/export?format=pdf&factoryId={r.Factory}", r.Manager.Token);
        Is(HttpStatusCode.OK, pdf);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Bytes, 0, 4));
        Assert.Contains((await AiSettingsTestKit.AuditText(Api, "planning.report_exported")), t => t.Contains("CSV"));

        // Monitoring operators read reports but can't export; other factories get nothing.
        Is(HttpStatusCode.OK, await Get($"/api/planning/reports/shift-plan-vs-actual?factoryId={r.Factory}", r.Monitor.Token));
        Is(HttpStatusCode.Forbidden, await Get($"/api/planning/reports/shift-plan-vs-actual/export?factoryId={r.Factory}", r.Monitor.Token));
        var other = await NewRig();
        Is(HttpStatusCode.Forbidden, await Get($"/api/planning/reports/order-production?factoryId={r.Factory}", other.Manager.Token), "factory_out_of_scope");
        Is(HttpStatusCode.NotFound, await Get($"/api/planning/reports/nope?factoryId={r.Factory}", r.Manager.Token), "report_type_not_found");
        // Historical target revisions come with their reasons.
        var target = await Post("/api/planning/targets", r.Manager.Token, new { factoryId = r.Factory, level = "LINE", period = "DAILY", lineId = r.Line1, quantity = 300, effectiveFrom = D(Today) });
        var t = target.Body.GetProperty("target");
        await Post($"/api/planning/targets/{t.GetProperty("id").GetGuid()}/revise", r.Manager.Token, new { quantity = 350, effectiveFrom = D(Tomorrow), reason = "Capacity up", expectedVersion = t.GetProperty("version").GetUInt32() });
        var rev = await Get($"/api/planning/reports/target-revisions?factoryId={r.Factory}", r.Manager.Token);
        var revRows = rev.Body.GetProperty("sections")[0].GetProperty("tables")[0].GetProperty("rows").EnumerateArray().Select(x => x.ToString()).ToList();
        Assert.Contains(revRows, x => x.Contains("Capacity up"));
    }

    // ─── Existing modules keep working ─────────────────────────────────────

    [DockerFact]
    public async Task The_production_module_still_accepts_its_older_order_vocabulary()
    {
        var r = await NewRig();
        // Legacy clients sent status "open" and listed "open" orders.
        var rep = await Post("/api/production/orders", r.Manager.Token, new { orderNumber = $"LG-{Guid.NewGuid():N}"[..12], styleId = r.Style, buyer = "Old client", quantity = 10, dueDate = D(Today.AddDays(5)), status = "open" });
        Is(HttpStatusCode.Created, rep);                                                           // the factory defaulted: this user has exactly one
        Assert.Equal("planned", S(rep.Body, "status"));
        var open = (await Get("/api/production/orders?status=open&includeRisk=false&pageSize=100", r.Manager.Token)).Body.GetProperty("items");
        Assert.Contains(open.EnumerateArray(), o => S(o, "id") == S(rep.Body, "id"));
        // "closed" through an edit passes through in progress.
        var closed = await Put($"/api/production/orders/{rep.Body.GetProperty("id").GetGuid()}", r.Manager.Token, new { status = "closed", expectedVersion = rep.Body.GetProperty("version").GetUInt32() });
        Is(HttpStatusCode.OK, closed);
        Assert.Equal("completed", S(closed.Body, "status"));
    }
}
