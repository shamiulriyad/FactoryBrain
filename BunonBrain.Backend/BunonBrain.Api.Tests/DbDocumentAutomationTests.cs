using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Documents;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Documents;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The document automation completion on PostgreSQL: fabric rolls through GRN → issue → stock balance (no duplicate
/// rolls, no roll or item below zero, append-only roll ledger); approved leave and factory holidays in attendance
/// documents, payroll input and the attendance statistics (never absence); scheduled generation (complete periods only,
/// one document per period and scope, failures logged and capped); the workflow and access rules end to end
/// (separation of duties, scope, status-only monitoring, append-only history, authenticated downloads, exports incl.
/// Bangla); and the AI features with the fake provider (suggestions only, never a decision, usage logged).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbDocumentAutomationTests
{
    private readonly PostgresStack _stack;
    public DbDocumentAutomationTests(PostgresStack stack) { _stack = stack; }

    private sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public JsonElement Data => Body.GetProperty("data");
        public JsonElement Checks => Body.GetProperty("checks");
        public bool HasError(string code) => Checks.GetProperty("errors").EnumerateArray().Any(e => e.GetProperty("code").GetString() == code);
        public string Id => Body.GetProperty("document").GetProperty("id").GetString()!;
        public string Status_ => Body.GetProperty("document").GetProperty("status").GetString()!;
    }

    private sealed record Person(Guid Id, string Token);

    private static async Task<Reply> Send(DbApiFactory api, HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var client = api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : Rag.Json(body) };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var res = await client.SendAsync(req);
        var raw = Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());
        var json = raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[') ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw);
    }

    private static async Task<(HttpStatusCode Status, byte[] Bytes, HttpResponseMessage Res)> Raw(DbApiFactory api, string path, string? bearer)
    {
        using var client = api.RawClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var res = await client.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsByteArrayAsync(), res);
    }

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static async Task<Person> NewPerson(DbApiFactory api, string role, Guid factory, string? line = null)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var user = new User { Id = id, Email = $"{role.ToLowerInvariant()}-{id:N}@automation.test", PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = factory });
        if (line is not null) user.Lines.Add(new UserProductionLineAssignment { UserId = id, LineId = line });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    private static async Task<(Guid Factory, string Line1, string Line2)> NewFactory(DbApiFactory api)
    {
        var t = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var factory = Guid.NewGuid();
        await Db(api, async db =>
        {
            db.Factories.Add(new Factory { Id = factory, Code = $"X{t}", Name = $"Automation {t}" });
            await db.SaveChangesAsync();
            db.ProductionLines.Add(new ProductionLine { Id = $"a1-{t.ToLowerInvariant()}", Name = $"Line one {t}", Workstations = 10, FactoryId = factory });
            db.ProductionLines.Add(new ProductionLine { Id = $"a2-{t.ToLowerInvariant()}", Name = $"Line two {t}", Workstations = 10, FactoryId = factory });
            await db.SaveChangesAsync();
        });
        return (factory, $"a1-{t.ToLowerInvariant()}", $"a2-{t.ToLowerInvariant()}");
    }

    private static Task Db(DbApiFactory api, Func<BunonBrainDbContext, Task> work) => Rag.WithDb(api, async db => { await work(db); return 0; });

    /// <summary>Factory-local (Asia/Dhaka, the default policy) today.</summary>
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6));
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    private static async Task<Reply> CreateSubmitApprove(DbApiFactory api, Person preparer, Person approver, Guid factory, string type, object data)
    {
        var r = await Send(api, HttpMethod.Post, "/api/documents", preparer.Token, new { factoryId = factory, typeCode = type, mode = "form", data });
        Is(HttpStatusCode.Created, r);
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Post, $"/api/documents/{r.Id}/submit", preparer.Token, new { }));
        var ok = await Send(api, HttpMethod.Post, $"/api/documents/{r.Id}/approve", approver.Token, new { });
        Is(HttpStatusCode.OK, ok);
        return ok;
    }

    // ─── Fabric rolls ──────────────────────────────────────────────────────

    [DockerFact]
    public async Task Fabric_rolls_are_received_by_grn_issued_roll_by_roll_and_never_go_below_zero()
    {
        var api = _stack.Local;
        var (f, _, _) = await NewFactory(api);
        var store = await NewPerson(api, R.StoreOfficer, f);
        var manager = await NewPerson(api, R.FactoryManager, f);
        var day = Iso(Today);
        object Grn(string challan, params object[] rolls) => new
        {
            supplier = "Padma Knit", challanNo = challan, receivedOn = day,
            items = new[] { new { itemCode = "FAB-SJ", itemName = "Single jersey navy", category = "FABRIC", unit = "m", receivedQty = 100, acceptedQty = 100, rejectedQty = 0, unitCost = 2 } },
            rolls,
        };

        var grn = await CreateSubmitApprove(api, store, manager, f, "store.grn", Grn($"CH-{Guid.NewGuid():N}"[..12],
            new { itemCode = "FAB-SJ", rollNumber = "R-01", lotNumber = "L7", color = "Navy", widthCm = 180, quantity = 60, warehouse = "WH-A", location = "Rack 3" },
            new { itemCode = "FAB-SJ", rollNumber = "R-02", lotNumber = "L7", color = "Navy", quantity = 40, warehouse = "WH-A" }));
        Assert.True(grn.Body.GetProperty("stockPosted").GetBoolean());
        Assert.Equal(2, grn.Data.GetProperty("rollCount").GetInt32());

        var rolls = await Send(api, HttpMethod.Get, $"/api/inventory/rolls?factoryId={f}", store.Token);
        Is(HttpStatusCode.OK, rolls);
        var items = rolls.Body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, r => Assert.Equal("IN_STOCK", r.GetProperty("status").GetString()));
        Assert.Contains(items, r => r.GetProperty("rollNumber").GetString() == "R-01" && r.GetProperty("remaining").GetDecimal() == 60m
                                    && r.GetProperty("warehouse").GetString() == "WH-A" && r.GetProperty("supplier").GetString() == "Padma Knit");
        var item = (await Send(api, HttpMethod.Get, $"/api/inventory/items?factoryId={f}", store.Token)).Body[0];
        Assert.Equal(100m, item.GetProperty("balance").GetDecimal());
        Assert.Equal(2, item.GetProperty("rollsInStock").GetInt32());

        // The same roll can't be received again (same supplier, lot and roll number).
        var again = await Send(api, HttpMethod.Post, "/api/documents", store.Token, new
        {
            factoryId = f, typeCode = "store.grn", mode = "form",
            data = Grn($"CH-{Guid.NewGuid():N}"[..12], new { itemCode = "FAB-SJ", rollNumber = "r-01", lotNumber = "l7", quantity = 100 }),
        });
        Is(HttpStatusCode.Created, again);
        Assert.True(again.HasError("duplicate_roll"), again.Raw);
        Is(HttpStatusCode.BadRequest, await Send(api, HttpMethod.Post, $"/api/documents/{again.Id}/submit", store.Token, new { }), "not_ready");

        // An item stocked by roll is issued roll by roll; a roll can't give more than it holds.
        object Issue(decimal qty, params object[] rr) => new { issuedTo = "Cutting", issuedOn = day, items = new[] { new { itemCode = "FAB-SJ", quantity = qty } }, rolls = rr };
        var noRolls = await Send(api, HttpMethod.Post, "/api/documents", store.Token, new { factoryId = f, typeCode = "store.issue", mode = "form", data = Issue(30) });
        Assert.True(noRolls.HasError("rolls_required"), noRolls.Raw);
        var tooMuch = await Send(api, HttpMethod.Post, "/api/documents", store.Token, new { factoryId = f, typeCode = "store.issue", mode = "form", data = Issue(50, new { rollNumber = "R-02", quantity = 50 }) });
        Assert.True(tooMuch.HasError("roll_insufficient"), tooMuch.Raw);
        var unknown = await Send(api, HttpMethod.Post, "/api/documents", store.Token, new { factoryId = f, typeCode = "store.issue", mode = "form", data = Issue(5, new { rollNumber = "R-99", quantity = 5 }) });
        Assert.True(unknown.HasError("unknown_roll"), unknown.Raw);

        var first = await CreateSubmitApprove(api, store, manager, f, "store.issue", Issue(30, new { rollNumber = "R-01", quantity = 30 }));
        var row = first.Data.GetProperty("rolls")[0];
        Assert.Equal("FAB-SJ", row.GetProperty("itemCode").GetString());   // filled from the roll
        Assert.Equal(60m, row.GetProperty("remaining").GetDecimal());       // before the issue
        await CreateSubmitApprove(api, store, manager, f, "store.issue", Issue(40, new { rollNumber = "R-02", lotNumber = "L7", quantity = 40 }));

        rolls = await Send(api, HttpMethod.Get, $"/api/inventory/rolls?factoryId={f}", store.Token);
        var byNo = rolls.Body.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("rollNumber").GetString()!);
        Assert.Equal("PARTLY_ISSUED", byNo["R-01"].GetProperty("status").GetString());
        Assert.Equal(30m, byNo["R-01"].GetProperty("remaining").GetDecimal());
        Assert.Equal("ISSUED", byNo["R-02"].GetProperty("status").GetString());
        Assert.Equal(1, (await Send(api, HttpMethod.Get, $"/api/inventory/rolls?factoryId={f}&status=AVAILABLE", store.Token)).Body.GetProperty("total").GetInt32());
        item = (await Send(api, HttpMethod.Get, $"/api/inventory/items?factoryId={f}", store.Token)).Body[0];
        Assert.Equal(30m, item.GetProperty("balance").GetDecimal());
        Assert.Equal(1, item.GetProperty("rollsInStock").GetInt32());

        // A used-up roll can't be issued again.
        var used = await Send(api, HttpMethod.Post, "/api/documents", store.Token, new { factoryId = f, typeCode = "store.issue", mode = "form", data = Issue(1, new { rollNumber = "R-02", quantity = 1 }) });
        Assert.True(used.HasError("roll_used_up"), used.Raw);

        // The stock balance report counts rolls; the roll ledger adds up to the item ledger and is append-only.
        var balance = await Send(api, HttpMethod.Post, "/api/documents", store.Token, new { factoryId = f, typeCode = "store.stock_balance", periodFrom = day });
        Is(HttpStatusCode.Created, balance);
        Assert.Equal(1, balance.Data.GetProperty("rows")[0].GetProperty("rolls").GetInt32());
        await Db(api, async db =>
        {
            var itemIssued = await db.StockMovements.Where(m => m.FactoryId == f && m.Kind == "ISSUE").SumAsync(m => m.Quantity);
            var rollIssued = await db.InventoryRollMovements.Where(m => m.FactoryId == f && m.Kind == "ISSUE").SumAsync(m => m.Quantity);
            Assert.Equal(itemIssued, rollIssued);
            Assert.Equal(70m, rollIssued);
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync($"UPDATE inventory_roll_movements SET \"Quantity\" = 1 WHERE \"FactoryId\" = '{f}'"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM inventory_rolls WHERE \"FactoryId\" = '{f}'"));
            Assert.True(await db.AuditLog.AnyAsync(a => a.FactoryId == f && a.Action == "inventory.rolls_issued"));
        });
    }

    // ─── Leave and holidays ────────────────────────────────────────────────

    [DockerFact]
    public async Task Approved_leave_and_holidays_are_not_absence_in_reports_payroll_and_statistics()
    {
        var api = _stack.Local;
        var (f, line, _) = await NewFactory(api);
        var hr = await NewPerson(api, R.HrOfficer, f);
        var manager = await NewPerson(api, R.FactoryManager, f);
        var controller = await NewPerson(api, R.DocumentController, f);
        var t = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var shift = Guid.NewGuid();
        var d1 = Today.AddDays(-6); var d2 = d1.AddDays(1);
        await Db(api, async db =>
        {
            db.Shifts.Add(new Shift { Id = shift, FactoryId = f, Code = "ALL", Name = "All day", StartLocal = new TimeOnly(0, 0), EndLocal = new TimeOnly(23, 59), WorkingDays = 0b1111111 });
            await db.SaveChangesAsync();
            foreach (var (w, code) in new[] { (a, $"LA-{t}"), (b, $"LB-{t}"), (c, $"LC-{t}") })
                db.Workers.Add(new WorkerProfile { Id = w, WorkerNumber = await TestWorkers.NextNumberAsync(db), FactoryId = f, WorkerCode = code, FullName = "Worker " + code, LineId = line });
            await db.SaveChangesAsync();
            foreach (var w in new[] { a, b, c })
                db.ShiftAssignments.Add(new WorkerShiftAssignment { WorkerId = w, FactoryId = f, ShiftId = shift, EffectiveFrom = d1.AddDays(-10), AssignedByEmail = "test" });
            var checkIn = new DateTimeOffset(d1.ToDateTime(new TimeOnly(3, 0)), TimeSpan.Zero);
            db.AttendanceSessions.Add(new AttendanceSession { WorkerId = b, FactoryId = f, LineId = line, ShiftId = shift, WorkDate = d1, CheckInAt = checkIn, CheckOutAt = checkIn.AddHours(8),
                CheckInSource = "Device", Status = "Completed", AttendanceMinutes = 480, NetWorkingMinutes = 480 });
            await db.SaveChangesAsync();
        });

        // A approved for sick leave on d1; C's leave is rejected (still absent).
        var leave = await CreateSubmitApprove(api, hr, manager, f, "hr.leave", new { workerCode = $"la-{t}", leaveType = "SICK", fromDate = Iso(d1), toDate = Iso(d1), reason = "Fever" });
        Assert.Equal(1, leave.Data.GetProperty("days").GetInt32());
        var rejected = await Send(api, HttpMethod.Post, "/api/documents", hr.Token, new
        {
            factoryId = f, typeCode = "hr.leave", mode = "form", data = new { workerCode = $"LC-{t}", leaveType = "CASUAL", fromDate = Iso(d1), toDate = Iso(d1), reason = "Family" },
        });
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Post, $"/api/documents/{rejected.Id}/submit", hr.Token, new { }));
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Post, $"/api/documents/{rejected.Id}/reject", manager.Token, new { note = "No cover on the line" }));

        // d2 is a holiday (added afterwards for a past day); past holidays can't be removed, future ones can.
        var hol = await Send(api, HttpMethod.Post, "/api/attendance/holidays", hr.Token, new { factoryId = f, date = Iso(d2), name = "Victory Day", kind = "PUBLIC" });
        Is(HttpStatusCode.Created, hol);
        Is(HttpStatusCode.Conflict, await Send(api, HttpMethod.Post, "/api/attendance/holidays", hr.Token, new { factoryId = f, date = Iso(d2), name = "Again" }), "holiday_exists");
        Is(HttpStatusCode.Conflict, await Send(api, HttpMethod.Delete, $"/api/attendance/holidays/{hol.Body.GetProperty("id").GetString()}", hr.Token), "holiday_in_past");
        Is(HttpStatusCode.Forbidden, await Send(api, HttpMethod.Post, "/api/attendance/holidays", manager.Token, new { factoryId = f, date = Iso(Today.AddDays(30)), name = "X" }));
        var future = await Send(api, HttpMethod.Post, "/api/attendance/holidays", hr.Token, new { factoryId = f, date = Iso(Today.AddDays(30)), name = "Eid", kind = "PUBLIC" });
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Delete, $"/api/attendance/holidays/{future.Body.GetProperty("id").GetString()}", hr.Token));

        // Attendance report of d1: B present, A on leave (not absent), C absent.
        var att = await Send(api, HttpMethod.Post, "/api/documents", hr.Token, new { factoryId = f, typeCode = "hr.attendance", periodFrom = Iso(d1) });
        Is(HttpStatusCode.Created, att);
        Assert.Equal(1, att.Data.GetProperty("present").GetInt32());
        Assert.Equal(1, att.Data.GetProperty("onLeave").GetInt32());
        Assert.Equal(1, att.Data.GetProperty("absent").GetInt32());
        var rows = att.Data.GetProperty("rows").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("workerCode").GetString() == $"LA-{t}" && r.GetProperty("status").GetString()!.StartsWith("ON LEAVE (SICK"));
        Assert.Contains(rows, r => r.GetProperty("workerCode").GetString() == $"LC-{t}" && r.GetProperty("status").GetString() == "ABSENT");
        Assert.DoesNotContain(rows, r => r.GetProperty("workerCode").GetString() == $"LA-{t}" && r.GetProperty("status").GetString() == "ABSENT");

        var exc = await Send(api, HttpMethod.Post, "/api/documents", hr.Token, new { factoryId = f, typeCode = "hr.late_absent", periodFrom = Iso(d1) });
        Assert.Equal(1, exc.Data.GetProperty("absent").GetInt32());
        Assert.Equal(1, exc.Data.GetProperty("onLeave").GetInt32());

        // Holiday d2: nobody expected, nobody absent.
        var holAtt = await Send(api, HttpMethod.Post, "/api/documents", hr.Token, new { factoryId = f, typeCode = "hr.attendance", periodFrom = Iso(d2) });
        Assert.Equal(0, holAtt.Data.GetProperty("absent").GetInt32());
        Assert.Equal(0, holAtt.Data.GetProperty("scheduled").GetInt32());
        Assert.StartsWith("Victory Day", holAtt.Data.GetProperty("holiday").GetString());

        // Payroll input d1..d2.
        var pay = await Send(api, HttpMethod.Post, "/api/documents", hr.Token, new { factoryId = f, typeCode = "hr.payroll_input", periodFrom = Iso(d1), periodTo = Iso(d2) });
        Is(HttpStatusCode.Created, pay);
        var pr = pay.Data.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("workerCode").GetString()!);
        Assert.Equal(1, pr[$"LA-{t}"].GetProperty("leaveDays").GetInt32());
        Assert.Equal(0, pr[$"LA-{t}"].GetProperty("absentDays").GetInt32());
        Assert.Equal(1, pr[$"LB-{t}"].GetProperty("presentDays").GetInt32());
        Assert.Equal(0, pr[$"LB-{t}"].GetProperty("absentDays").GetInt32());
        Assert.Equal(1, pr[$"LC-{t}"].GetProperty("absentDays").GetInt32());
        Assert.Equal(1, pay.Data.GetProperty("holidays").GetInt32());

        // Live statistics agree.
        var stats = await Send(api, HttpMethod.Get, $"/api/attendance/statistics?factoryId={f}&date={Iso(d1)}", hr.Token);
        Is(HttpStatusCode.OK, stats);
        Assert.Equal(1, stats.Body.GetProperty("absent").GetInt32());
        Assert.Equal(1, stats.Body.GetProperty("onLeave").GetInt32());
        var holStats = await Send(api, HttpMethod.Get, $"/api/attendance/statistics?factoryId={f}&date={Iso(d2)}", hr.Token);
        Assert.Equal(0, holStats.Body.GetProperty("absent").GetInt32());
        Assert.Equal("Victory Day", holStats.Body.GetProperty("holiday").GetString());

        // Archiving the approved leave record keeps the leave; recorded sessions never change.
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Post, $"/api/documents/{leave.Id}/archive", controller.Token, new { note = "Filed" }));
        stats = await Send(api, HttpMethod.Get, $"/api/attendance/statistics?factoryId={f}&date={Iso(d1)}", hr.Token);
        Assert.Equal(1, stats.Body.GetProperty("onLeave").GetInt32());
        await Db(api, async db => Assert.Equal(1, await db.AttendanceSessions.CountAsync(s => s.FactoryId == f)));
    }

    // ─── Scheduling ────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Scheduled_generation_makes_each_period_once_per_scope_and_logs_and_caps_failures()
    {
        var api = _stack.Local;
        var (f, l1, l2) = await NewFactory(api);
        var controller = await NewPerson(api, R.DocumentController, f);
        var yesterday = Today.AddDays(-1);
        await Db(api, async db =>
        {
            var style = new ProductionStyle { Id = Guid.NewGuid(), Code = $"S-{Guid.NewGuid():N}"[..10], Name = "Tee" };
            db.ProductionStyles.Add(style);
            var order = new ProductionOrder { Id = Guid.NewGuid(), OrderNumber = $"PO-{Guid.NewGuid():N}"[..12], StyleId = style.Id, Quantity = 1000, DueDate = Today.AddDays(30), FactoryId = f, CreatedByEmail = "t@t" };
            db.ProductionOrders.Add(order);
            await db.SaveChangesAsync();
            var start = yesterday.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc);
            db.ProductionOutputEvents.Add(new ProductionOutputEvent { LineId = l1, OrderId = order.Id, Stage = ProductionStage.SewingOutput, IntervalStartUtc = start, IntervalEndUtc = start.AddHours(1),
                ProductionDate = yesterday, Quantity = 80, Source = ProductionDataSource.Manual, RecordedByEmail = "t@t" });
            await db.SaveChangesAsync();
        });

        // Settings validation: frequencies the type allows, line scope only for line types.
        object Setting(string approval, bool auto, string? freq = null, string? scope = null) =>
            new { approvalLevel = approval, autoGenerate = auto, expectedDaily = false, decisionDueHours = 24, scheduleFrequency = freq, scheduleScope = scope };
        Is(HttpStatusCode.BadRequest, await Send(api, HttpMethod.Put, $"/api/documents/types/hr.payroll_input/settings?factoryId={f}", controller.Token, Setting("REVIEW_AND_APPROVE", true, "DAILY")), "invalid_frequency");
        Is(HttpStatusCode.BadRequest, await Send(api, HttpMethod.Put, $"/api/documents/types/production.daily/settings?factoryId={f}", controller.Token, Setting("SINGLE", true, "DAILY", "EACH_LINE")), "invalid_scope");
        var monthly = await Send(api, HttpMethod.Put, $"/api/documents/types/hr.overtime/settings?factoryId={f}", controller.Token, Setting("SINGLE", true, "MONTHLY"));
        Is(HttpStatusCode.OK, monthly);
        Assert.Equal("MONTHLY", monthly.Body.GetProperty("scheduleFrequency").GetString());
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Put, $"/api/documents/types/production.line/settings?factoryId={f}", controller.Token, Setting("SINGLE", true, "DAILY", "EACH_LINE")));
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Put, $"/api/documents/types/hr.attendance/settings?factoryId={f}", controller.Token, Setting("SINGLE", false)));

        async Task RunAsync()
        {
            await using var scope = api.Services.CreateAsyncScope();
            await DocumentAutomationWorker.RunFactoryAsync(scope.ServiceProvider, f, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        await RunAsync();
        await RunAsync(); // a second run must not create anything again

        await Db(api, async db =>
        {
            var docs = await db.FactoryDocuments.Where(d => d.FactoryId == f).ToListAsync();
            Assert.Single(docs, d => d.TypeCode == "production.daily" && d.PeriodFrom == yesterday && d.CreatedByEmail == DocumentAutomationWorker.SystemEmail);
            Assert.Single(docs, d => d.TypeCode == "production.line" && d.LineId == l1);
            Assert.DoesNotContain(docs, d => d.TypeCode == "production.line" && d.LineId == l2);   // no output on line two
            Assert.DoesNotContain(docs, d => d.TypeCode == "hr.attendance");                      // turned off
            Assert.All(docs, d => Assert.Equal(DocumentStatuses.Draft, d.Status));                // never submitted or approved by the system
            var runs = await db.DocumentScheduleRuns.Where(r => r.FactoryId == f).ToListAsync();
            Assert.Contains(runs, r => r.TypeCode == "production.line" && r.LineKey == l2 && r.Status == ScheduleRunStatuses.NoData && r.Attempts == 2);
            Assert.Contains(runs, r => r.TypeCode == "production.daily" && r.Status == ScheduleRunStatuses.Created && r.Attempts == 1);
            var (mf, mt) = DocumentSchedule.LatestCompletePeriod(ScheduleFrequencies.Monthly, Today, 6);
            Assert.Contains(runs, r => r.TypeCode == "hr.overtime" && r.PeriodFrom == mf && r.PeriodTo == mt && r.Status == ScheduleRunStatuses.NoData);
        });

        // A failing generation is logged with its error, retried up to the limit, then left alone (controllers told once).
        for (var i = 0; i < DocumentSchedule.MaxFailedAttempts + 2; i++)
        {
            await using var scope = api.Services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<BunonBrainDbContext>();
            await DocumentAutomationWorker.RunTargetAsync(db, sp.GetRequiredService<DocumentGenerators>(), sp.GetRequiredService<DocumentNotifier>(), sp.GetRequiredService<DocumentEnricher>(),
                await FactoryClock.LoadAsync(db, f, CancellationToken.None), f, new Dictionary<string, DocumentTypeSetting>(),
                new DocumentAutomationWorker.ScheduleTarget(DocumentCatalog.Get("store.grn")!, yesterday, yesterday, null, null), DateTimeOffset.UtcNow,
                new AuditActor(null, DocumentAutomationWorker.SystemEmail, "System"), CancellationToken.None);
        }
        await Db(api, async db =>
        {
            var failed = await db.DocumentScheduleRuns.SingleAsync(r => r.FactoryId == f && r.TypeCode == "store.grn");
            Assert.Equal(ScheduleRunStatuses.Failed, failed.Status);
            Assert.Equal(DocumentSchedule.MaxFailedAttempts, failed.Attempts);
            Assert.Equal("InvalidOperationException", failed.ErrorCode);
            Assert.Contains("No generator", failed.ErrorMessage);
            Assert.Equal(1, await db.AuditLog.CountAsync(a => a.FactoryId == f && a.Action == "document.schedule_failed"));
            Assert.True(await db.UserNotifications.AnyAsync(n => n.UserId == controller.Id && n.Kind == "document.schedule_failed"));
        });

        var log = await Send(api, HttpMethod.Get, $"/api/documents/schedule-runs?factoryId={f}", controller.Token);
        Is(HttpStatusCode.OK, log);
        Assert.False(log.Body.GetProperty("automationEnabled").GetBoolean());   // off unless DOCUMENT_AUTOMATION_ENABLED=true
        Assert.Contains(log.Body.GetProperty("runs").EnumerateArray(), r => r.GetProperty("status").GetString() == "CREATED" && r.TryGetProperty("documentReference", out _));
    }

    // ─── Workflow, access, history, files and exports ──────────────────────

    [DockerFact]
    public async Task Workflow_access_history_downloads_and_exports_follow_the_rules()
    {
        var api = _stack.Local;
        var (f, line1, line2) = await NewFactory(api);
        var hr = await NewPerson(api, R.HrOfficer, f);
        var manager = await NewPerson(api, R.FactoryManager, f);
        var store = await NewPerson(api, R.StoreOfficer, f);
        var monitor = await NewPerson(api, R.MonitoringOperator, f);
        var worker = await NewPerson(api, R.Worker, f);
        var sup = await NewPerson(api, R.ProductionSupervisor, f, line1);
        var admin = await NewPerson(api, R.FactoryAdmin, f);
        var t = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        await Db(api, async db =>
        {
            db.Workers.Add(new WorkerProfile { Id = Guid.NewGuid(), WorkerNumber = await TestWorkers.NextNumberAsync(db), FactoryId = f, WorkerCode = $"WF-{t}", FullName = "রহিমা খাতুন", LineId = line1 });
            await db.SaveChangesAsync();
        });

        var doc = await Send(api, HttpMethod.Post, "/api/documents", hr.Token, new
        {
            factoryId = f, typeCode = "hr.leave", mode = "form",
            data = new { workerCode = $"WF-{t}", leaveType = "ANNUAL", fromDate = Iso(Today.AddDays(3)), toDate = Iso(Today.AddDays(4)), reason = "Family visit" },
        });
        Is(HttpStatusCode.Created, doc);
        var id = doc.Id;
        var saved = await Send(api, HttpMethod.Put, $"/api/documents/{id}", hr.Token, new
        {
            data = new { workerCode = $"WF-{t}", leaveType = "ANNUAL", fromDate = Iso(Today.AddDays(3)), toDate = Iso(Today.AddDays(5)), reason = "পারিবারিক কারণে ছুটি (family visit)" },
            changeNote = "Three days",
        });
        Is(HttpStatusCode.OK, saved);
        Assert.Equal(2, saved.Body.GetProperty("document").GetProperty("currentVersion").GetInt32());
        var v1 = await Send(api, HttpMethod.Get, $"/api/documents/{id}/versions/1", hr.Token);
        var v2 = await Send(api, HttpMethod.Get, $"/api/documents/{id}/versions/2", hr.Token);
        Is(HttpStatusCode.OK, v1); Is(HttpStatusCode.OK, v2);
        Assert.NotEqual(v1.Raw, v2.Raw);

        // Access: other category, status-only monitoring, workers and supervisors out of scope.
        Is(HttpStatusCode.NotFound, await Send(api, HttpMethod.Get, $"/api/documents/{id}", store.Token));
        Is(HttpStatusCode.Forbidden, await Send(api, HttpMethod.Get, $"/api/documents/{id}", monitor.Token));
        Is(HttpStatusCode.Forbidden, await Send(api, HttpMethod.Get, $"/api/documents/{id}/export?format=pdf", monitor.Token));
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Get, $"/api/documents/monitor?factoryId={f}", monitor.Token));
        Is(HttpStatusCode.Forbidden, await Send(api, HttpMethod.Get, "/api/documents", worker.Token));
        Is(HttpStatusCode.Forbidden, await Send(api, HttpMethod.Get, $"/api/documents/{id}", worker.Token));
        Is(HttpStatusCode.Forbidden, await Send(api, HttpMethod.Get, $"/api/documents/monitor?factoryId={f}", worker.Token));
        Is(HttpStatusCode.Forbidden, await Send(api, HttpMethod.Post, "/api/documents", sup.Token, new { factoryId = f, typeCode = "production.line", lineId = line2 }), "line_out_of_scope");
        var otherLine = await Send(api, HttpMethod.Post, "/api/documents", admin.Token, new { factoryId = f, typeCode = "production.line", lineId = line2 });
        Is(HttpStatusCode.Created, otherLine);
        Is(HttpStatusCode.NotFound, await Send(api, HttpMethod.Get, $"/api/documents/{otherLine.Id}", sup.Token));

        // Files: authenticated and authorised downloads only.
        using (var client = api.RawClient())
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Leave application\nWorker ID: WF-1")) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } }, "file", "leave.txt" } };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/documents/{id}/files") { Content = form };
            req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hr.Token);
            var up = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Created, up.StatusCode);
            var fileId = JsonDocument.Parse(await up.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.Unauthorized, (await Raw(api, $"/api/documents/{id}/files/{fileId}", null)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await Raw(api, $"/api/documents/{id}/files/{fileId}", store.Token)).Status);
            var dl = await Raw(api, $"/api/documents/{id}/files/{fileId}", hr.Token);
            Assert.Equal(HttpStatusCode.OK, dl.Status);
            Assert.Equal("nosniff", dl.Res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("no-store", dl.Res.Headers.CacheControl!.ToString());
        }

        // Separation of duties: the preparer can't approve; an approver can.
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Post, $"/api/documents/{id}/submit", hr.Token, new { }));
        Is(HttpStatusCode.Forbidden, await Send(api, HttpMethod.Post, $"/api/documents/{id}/approve", hr.Token, new { }));
        Is(HttpStatusCode.Conflict, await Send(api, HttpMethod.Put, $"/api/documents/{id}", hr.Token, new { data = new { reason = "late edit" } }), "not_editable");
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Post, $"/api/documents/{id}/approve", manager.Token, new { note = "OK" }));

        // Exports: PDF (Bangla shaped with the embedded font), Excel and Word, each needing a signed-in, authorised user.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Raw(api, $"/api/documents/{id}/export?format=pdf", null)).Status);
        var pdf = await Raw(api, $"/api/documents/{id}/export?format=pdf", hr.Token);
        Assert.Equal(HttpStatusCode.OK, pdf.Status);
        Assert.Equal("application/pdf", pdf.Res.Content.Headers.ContentType!.MediaType);
        using (var p = UglyToad.PdfPig.PdfDocument.Open(pdf.Bytes))
            Assert.Contains(p.GetPage(1).Letters, l => l.FontName.Contains("NotoSansBengali", StringComparison.Ordinal));
        var xlsx = await Raw(api, $"/api/documents/{id}/export?format=xlsx", hr.Token);
        Assert.Equal(HttpStatusCode.OK, xlsx.Status);
        Assert.Contains("পারিবারিক", string.Join("\n", XlsxReader.Read(xlsx.Bytes, 100_000).Pages.Select(pg => pg.Text)));
        var docx = await Raw(api, $"/api/documents/{id}/export?format=docx", hr.Token);
        Assert.Equal(HttpStatusCode.OK, docx.Status);
        using (var zip = new ZipArchive(new MemoryStream(docx.Bytes)))
            Assert.Contains("পারিবারিক", new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd());
        Assert.Equal(HttpStatusCode.NotFound, (await Raw(api, $"/api/documents/{id}/export?format=pdf", store.Token)).Status);

        // History is append-only: versions, events and the audit log can't be changed.
        await Db(api, async db =>
        {
            Assert.True(await db.AuditLog.CountAsync(a => a.EntityId == id) >= 4);   // created, saved, submitted, approved
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync($"UPDATE factory_document_versions SET \"DataJson\" = '{{}}' WHERE \"DocumentId\" = '{id}'"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM factory_document_events WHERE \"DocumentId\" = '{id}'"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM audit_log WHERE \"EntityId\" = '{id}'"));
        });
    }

    // ─── AI ────────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Ai_classifies_summarises_and_drafts_through_the_configured_provider_but_never_decides()
    {
        var api = _stack.Mocked;
        var (f, _, _) = await NewFactory(api);
        var commercial = await NewPerson(api, R.CommercialOfficer, f);
        var manager = await NewPerson(api, R.FactoryManager, f);
        var admin = await NewPerson(api, R.FactoryAdmin, f);

        // Classification: no keyword matches, so the provider is asked; its answer is a suggestion.
        using (var client = api.RawClient())
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Cartons 1-12, 24 pcs each, 18.5 kg net")) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } }, "file", "scan.txt" } };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/documents/classify?factoryId={f}") { Content = form };
            req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", commercial.Token);
            var res = await client.SendAsync(req);
            var raw = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode == HttpStatusCode.OK, raw);
            var cls = JsonDocument.Parse(raw).RootElement;
            if (cls.GetProperty("method").GetString() == "ai")
            {
                Assert.Equal("commercial.packing_list", cls.GetProperty("typeCode").GetString());
                Assert.DoesNotContain(api.ProviderKey, raw);
            }
        }

        var inv = await Send(api, HttpMethod.Post, "/api/documents", commercial.Token, new
        {
            factoryId = f, typeCode = "commercial.invoice", mode = "form",
            data = new { invoiceNo = $"INV-{Guid.NewGuid():N}"[..14], invoiceDate = Iso(Today), buyer = "Nordic", poNumber = "PO-AI-1", currency = "USD", lines = new[] { new { description = "Tee", quantity = 10, unitPrice = 2 } } },
        });
        Is(HttpStatusCode.Created, inv);
        Is(HttpStatusCode.OK, await Send(api, HttpMethod.Post, $"/api/documents/{inv.Id}/submit", commercial.Token, new { }));

        // Summary: stored as advice; even an answer that says "approve" changes nothing.
        var sum = await Send(api, HttpMethod.Post, $"/api/documents/{inv.Id}/summary", manager.Token);
        Is(HttpStatusCode.OK, sum);
        Assert.Contains(DbApiFactory.FakeDocumentSummary, sum.Body.GetProperty("aiSummary").GetString());
        Assert.Equal("SUBMITTED", sum.Status_);
        Assert.DoesNotContain(api.ProviderKey, sum.Raw);
        Assert.DoesNotContain(sum.Body.GetProperty("history").EnumerateArray(), h => h.GetProperty("action").GetString() is "approved" or "verified" or "rejected");

        // Draft text: missing facts are marked for a person, never invented.
        var draftDoc = await Send(api, HttpMethod.Post, "/api/documents", admin.Token, new { factoryId = f, typeCode = "production.daily", periodFrom = Iso(Today.AddDays(-1)) });
        Is(HttpStatusCode.Created, draftDoc);
        var draft = await Send(api, HttpMethod.Post, $"/api/documents/{draftDoc.Id}/draft-text", admin.Token, new { field = "remarks" });
        Is(HttpStatusCode.OK, draft);
        Assert.Contains("[needs input:", draft.Raw);
        Is(HttpStatusCode.BadRequest, await Send(api, HttpMethod.Post, $"/api/documents/{draftDoc.Id}/draft-text", admin.Token, new { field = "sewingOutput" }), "not_a_text_field");

        // Every call is logged without prompts or keys.
        await Db(api, async db =>
        {
            var usage = await db.AiApiUsage.Where(u => u.FactoryId == f).ToListAsync();
            Assert.Contains(usage, u => u.Purpose == "document.summarize" && u.Outcome == "ok" && u.InputChars > 0);
            Assert.Contains(usage, u => u.Purpose == "document.draft_text" && u.Outcome == "ok");
            var doc = await db.FactoryDocuments.SingleAsync(d => d.Id == Guid.Parse(inv.Id));
            Assert.Equal(DocumentStatuses.Submitted, doc.Status);
            Assert.Null(doc.DecidedAt);
        });
    }
}
