using System.Net;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Production planning on a real PostgreSQL database (migrations applied by
/// the API's own startup), plant time zone Asia/Dhaka (UTC+6). Every test
/// creates its own line, style and orders, so tests don't see each other's
/// rows. Data is created through the API exactly as a user would; nothing is
/// seeded or mocked.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbProductionTests
{
    private readonly PostgresStack _stack;
    public DbProductionTests(PostgresStack stack) { _stack = stack; }

    /// <summary>The API's captured log, so a 500 in a helper shows the server-side exception.</summary>
    private static LogCapture? _logs;

    private DbApiFactory Api { get { var api = _stack.Production; _logs = api.Logs; return api; } }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static readonly TimeSpan Offset = TimeSpan.FromHours(6);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow + Offset);
    private static DateOnly Yesterday => Today.AddDays(-1);
    private static DateTime At(DateOnly d, int hour) => DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(hour, 0)) - Offset, DateTimeKind.Utc);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    private static string U(string p) => $"{p}-{Guid.NewGuid().ToString("N")[..6]}";

    private static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> Send(HttpClient c, HttpMethod m, string url, object? body = null)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = Rag.Json(body);
        var res = await c.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), res);
    }

    private static async Task<JsonElement> Expect(HttpStatusCode expected, HttpClient c, HttpMethod m, string url, object? body = null)
    {
        var (status, json, _) = await Send(c, m, url, body);
        var serverError = status == HttpStatusCode.InternalServerError
            ? Environment.NewLine + string.Join(Environment.NewLine,
                (_logs?.Lines ?? Array.Empty<string>()).Where(l => l.StartsWith("Error", StringComparison.Ordinal)).TakeLast(2))
            : "";
        Assert.True(status == expected, $"{m} {url} → {(int)status}: {json}{serverError}");
        return json;
    }

    private (HttpClient Admin, HttpClient Operator, HttpClient Viewer) Clients()
    {
        var api = Api;
        return (Rag.Client(api, Roles.Admin), Rag.Client(api, Roles.Operator), Rag.Client(api, Roles.Viewer));
    }

    /// <summary>Shifts a (08–16) and b (16–24) every day, created once per database.</summary>
    private static async Task EnsureCalendar(HttpClient admin)
    {
        var shifts = await Expect(HttpStatusCode.OK, admin, HttpMethod.Get, "/api/production/shifts");
        var codes = shifts.EnumerateArray().Select(s => s.GetProperty("code").GetString()).ToHashSet();
        var all = new[] { "sat", "sun", "mon", "tue", "wed", "thu", "fri" };
        if (!codes.Contains("a")) await Expect(HttpStatusCode.Created, admin, HttpMethod.Post, "/api/production/shifts", new { code = "a", name = "Shift A", start = "08:00", end = "16:00", weekdays = all });
        if (!codes.Contains("b")) await Expect(HttpStatusCode.Created, admin, HttpMethod.Post, "/api/production/shifts", new { code = "b", name = "Shift B", start = "16:00", end = "00:00", weekdays = all });
    }

    private static async Task<string> NewLine(HttpClient admin, int workstations = 10)
    {
        var id = U("ln");
        await Expect(HttpStatusCode.Created, admin, HttpMethod.Post, "/api/production/lines", new { id, name = $"Line {id}", workstations });
        return id;
    }

    private static readonly object[] Bulletin =
    {
        new { sequence = 1, name = "Join shoulder", smv = 0.5, machineType = "SNLS", predecessors = Array.Empty<int>() },
        new { sequence = 2, name = "Attach collar", smv = 0.8, machineType = "SNLS", predecessors = new[] { 1 } },
        new { sequence = 3, name = "Overlock side", smv = 0.6, machineType = "OL", predecessors = new[] { 1 } },
        new { sequence = 4, name = "Hem", smv = 0.4, machineType = "SNLS", predecessors = new[] { 2, 3 } },
        new { sequence = 5, name = "Trim", smv = 0.3, machineType = (string?)null, predecessors = new[] { 4 } },
    };

    private static async Task<JsonElement> NewStyle(HttpClient admin, double? approvedSmv = 10, object[]? operations = null)
    {
        var code = U("ST");
        return await Expect(HttpStatusCode.Created, admin, HttpMethod.Post, "/api/production/styles",
            new { code, name = $"Style {code}", productType = "t-shirt", approvedSmv, operations = operations ?? Array.Empty<object>() });
    }

    private static async Task<string> NewOrder(HttpClient op, string styleId, int quantity = 5000, DateOnly? due = null)
    {
        var o = await Expect(HttpStatusCode.Created, op, HttpMethod.Post, "/api/production/orders",
            new { orderNumber = U("PO"), styleId, quantity, dueDate = D(due ?? Today.AddDays(10)), buyer = "Test buyer" });
        return o.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> NewPlan(HttpClient op, string orderId, string lineId, DateOnly start, DateOnly end,
        int quantity = 1000, int operators = 20, double efficiency = 50) =>
        await Expect(HttpStatusCode.Created, op, HttpMethod.Post, "/api/production/plans", new
        {
            orderId, lineId, startDate = D(start), endDate = D(end), plannedQuantity = quantity, operators, targetEfficiencyPct = efficiency,
            shiftCodes = new[] { "a", "b" }, overtimeMinutesPerDay = 0,
        });

    private static string Id(JsonElement plan) => plan.GetProperty("summary").GetProperty("id").GetString()!;

    /// <summary>Operator submits, Admin approves in the approval queue, Operator activates.</summary>
    private static async Task<JsonElement> MakeLive(HttpClient op, HttpClient admin, string planId)
    {
        var submitted = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{planId}/submit", new { });
        var approvalId = submitted.GetProperty("plan").GetProperty("activationApproval").GetProperty("id").GetString()!;
        await Expect(HttpStatusCode.OK, admin, HttpMethod.Post, $"/api/approvals/{approvalId}/approve", new { });
        var activated = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{planId}/activate", new { });
        Assert.Equal("activated", activated.GetProperty("outcome").GetString());
        return activated.GetProperty("plan");
    }

    private static object Output(string lineId, string orderId, DateTime start, DateTime end, int quantity, int? operators = 20,
        string stage = "sewing_output", string? source = null, string? sourceRef = null, double? downtime = null) =>
        new { lineId, orderId, stage, intervalStart = start, intervalEnd = end, quantity, operatorsPresent = operators, source, sourceRef, downtimeMinutes = downtime };

    private Task<string[]> AuditActions(string entityType, string entityId) =>
        Rag.WithDb(Api, db => db.AuditLog.Where(a => a.EntityType == entityType && a.EntityId == entityId).OrderBy(a => a.Id).Select(a => a.Action).ToArrayAsync());

    // ── Setup and permissions ───────────────────────────────────────────────

    [DockerFact]
    public async Task Master_data_is_admin_only_validated_and_audited()
    {
        var (admin, op, viewer) = Clients();
        await EnsureCalendar(admin);
        await Expect(HttpStatusCode.Forbidden, op, HttpMethod.Post, "/api/production/lines", new { id = U("ln"), name = "x", workstations = 5 });
        await Expect(HttpStatusCode.Forbidden, viewer, HttpMethod.Post, "/api/production/styles", new { code = "x", name = "x" });

        var bad = await Expect(HttpStatusCode.BadRequest, admin, HttpMethod.Post, "/api/production/lines", new { id = "Line 1!", name = "", workstations = 0 });
        Assert.True(bad.GetProperty("errors").TryGetProperty("id", out _));
        Assert.True(bad.GetProperty("errors").TryGetProperty("workstations", out _));

        var line = await NewLine(admin);
        var dup = await Expect(HttpStatusCode.Conflict, admin, HttpMethod.Post, "/api/production/lines", new { id = line, name = "Other", workstations = 3 });
        Assert.Equal("duplicate_line", dup.GetProperty("title").GetString());
        Assert.Equal(new[] { "production_line.created" }, await AuditActions("production_line", line));

        // A shift overlapping shift a is rejected; so is a cyclic operation bulletin.
        var overlap = await Expect(HttpStatusCode.BadRequest, admin, HttpMethod.Post, "/api/production/shifts",
            new { code = U("x"), name = U("Overlap"), start = "15:00", end = "17:00", weekdays = new[] { "mon" } });
        Assert.Contains("overlaps", overlap.GetProperty("errors").GetProperty("start")[0].GetString());
        var cyclic = await Expect(HttpStatusCode.BadRequest, admin, HttpMethod.Post, "/api/production/styles", new
        {
            code = U("ST"), name = "Cyclic",
            operations = new object[] { new { sequence = 1, name = "a", smv = 1.0, predecessors = new[] { 2 } }, new { sequence = 2, name = "b", smv = 1.0, predecessors = new[] { 1 } } },
        });
        Assert.Contains("cycle", cyclic.GetProperty("errors").GetProperty("operations")[0].GetString());

        // Swapping two operations' sequence numbers keeps their ids (time studies and layouts stay linked).
        var style = await NewStyle(admin, null, new object[]
        {
            new { sequence = 1, name = "Front", smv = 0.4 }, new { sequence = 2, name = "Back", smv = 0.6 },
        });
        Assert.Equal(("operation_bulletin", 1.0), (style.GetProperty("smvBasis").GetString(), style.GetProperty("effectiveSmv").GetDouble()));
        var ids = style.GetProperty("operations").EnumerateArray().ToDictionary(o => o.GetProperty("name").GetString()!, o => o.GetProperty("id").GetString());
        var swapped = await Expect(HttpStatusCode.OK, admin, HttpMethod.Put, $"/api/production/styles/{style.GetProperty("id").GetString()}", new
        {
            expectedVersion = style.GetProperty("version").GetUInt32(),
            operations = new object[] { new { id = ids["Front"], sequence = 2, name = "Front", smv = 0.4 }, new { id = ids["Back"], sequence = 1, name = "Back", smv = 0.6 } },
        });
        Assert.Equal(new[] { ("Back", ids["Back"]), ("Front", ids["Front"]) },
            swapped.GetProperty("operations").EnumerateArray().Select(o => (o.GetProperty("name").GetString()!, o.GetProperty("id").GetString())));

        // Viewers read; the config never claims a connector that doesn't exist.
        var config = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, "/api/production/config");
        Assert.Equal("Asia/Dhaka", config.GetProperty("timeZone").GetString());
        Assert.Equal("UTC+06:00", config.GetProperty("utcOffsetNow").GetString());
        var integration = config.GetProperty("dataSources").EnumerateArray().Single(s => s.GetProperty("source").GetString() == "integration");
        Assert.Equal("not_connected", integration.GetProperty("status").GetString());
    }

    // ── Plan workflow ───────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_plan_goes_live_only_after_approval_and_activation_by_an_authorised_user()
    {
        var (admin, op, viewer) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var style = (await NewStyle(admin)).GetProperty("id").GetString()!;
        var order = await NewOrder(op, style);

        await Expect(HttpStatusCode.Forbidden, viewer, HttpMethod.Post, "/api/production/plans", new { orderId = order });
        var invalid = await Expect(HttpStatusCode.BadRequest, op, HttpMethod.Post, "/api/production/plans",
            new { orderId = order, lineId = line, startDate = D(Today), endDate = D(Today.AddDays(-1)), plannedQuantity = 0, operators = 20, targetEfficiencyPct = 200, shiftCodes = new[] { "zz" } });
        foreach (var key in new[] { "endDate", "plannedQuantity", "targetEfficiencyPct", "shiftCodes" })
            Assert.True(invalid.GetProperty("errors").TryGetProperty(key, out _), key);

        var plan = await NewPlan(op, order, line, Today, Today.AddDays(2));
        var id = Id(plan);
        Assert.Equal("draft", plan.GetProperty("summary").GetProperty("status").GetString());
        // 20 operators × 60 × 50 % ÷ 10 SMV = 60 pieces/hour; 960 scheduled minutes/day → 960 pieces/day.
        Assert.Equal(60.0, plan.GetProperty("targets").GetProperty("hourlyTarget").GetDouble());
        Assert.Equal(960, plan.GetProperty("targets").GetProperty("daily")[0].GetProperty("target").GetInt32());

        // Drafts are edited directly and audited with before/after values.
        var edited = await Expect(HttpStatusCode.OK, op, HttpMethod.Put, $"/api/production/plans/{id}",
            new { plannedQuantity = 1200, expectedVersion = plan.GetProperty("version").GetUInt32() });
        Assert.Equal("edited", edited.GetProperty("outcome").GetString());
        var stale = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Put, $"/api/production/plans/{id}",
            new { plannedQuantity = 1300, expectedVersion = plan.GetProperty("version").GetUInt32() });
        Assert.Equal("version_conflict", stale.GetProperty("title").GetString());

        var submitted = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{id}/submit", new { reason = "ready" });
        var approval = submitted.GetProperty("plan").GetProperty("activationApproval");
        Assert.Equal(("pending_approval", "medium"), (approval.GetProperty("status").GetString(), approval.GetProperty("riskTier").GetString()));
        await Expect(HttpStatusCode.Conflict, op, HttpMethod.Put, $"/api/production/plans/{id}",
            new { plannedQuantity = 5, expectedVersion = submitted.GetProperty("plan").GetProperty("version").GetUInt32() });

        // Nothing goes live while the approval is pending.
        var pending = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Post, $"/api/production/plans/{id}/activate", new { });
        Assert.Equal("approval_pending", pending.GetProperty("title").GetString());
        var queue = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, "/api/approvals?status=pending&limit=200");
        Assert.Contains(queue.GetProperty("items").EnumerateArray(), a => a.GetProperty("id").GetString() == approval.GetProperty("id").GetString());

        await Expect(HttpStatusCode.OK, admin, HttpMethod.Post, $"/api/approvals/{approval.GetProperty("id").GetString()}/approve", new { });
        await Expect(HttpStatusCode.Forbidden, viewer, HttpMethod.Post, $"/api/production/plans/{id}/activate", new { });
        var live = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{id}/activate", new { });
        Assert.Equal("active", live.GetProperty("plan").GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal(new[] { "production_plan.created", "production_plan.edited", "production_plan.submitted", "production_plan.activated" },
            await AuditActions("production_plan", id));
    }

    [DockerFact]
    public async Task The_submitter_cannot_approve_their_own_plan_and_a_rejection_returns_it_to_draft()
    {
        var (admin, op, _) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var order = await NewOrder(op, (await NewStyle(admin)).GetProperty("id").GetString()!);

        var own = Id(await NewPlan(op, order, line, Today, Today));
        var s1 = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{own}/submit", new { });
        await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/approvals/{s1.GetProperty("plan").GetProperty("activationApproval").GetProperty("id").GetString()}/approve", new { });
        var same = await Expect(HttpStatusCode.Forbidden, op, HttpMethod.Post, $"/api/production/plans/{own}/activate", new { });
        Assert.Equal("same_approver", same.GetProperty("title").GetString());
        // The way out: withdraw it to draft, resubmit, and let someone else approve.
        var withdrawn = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{own}/withdraw", new { });
        Assert.Equal(("withdrawn", "draft"), (withdrawn.GetProperty("outcome").GetString(), withdrawn.GetProperty("plan").GetProperty("summary").GetProperty("status").GetString()));
        Assert.Contains("production_plan.withdrawn", await AuditActions("production_plan", own));

        var rejected = Id(await NewPlan(op, order, line, Today.AddDays(5), Today.AddDays(5)));
        var s2 = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{rejected}/submit", new { });
        await Expect(HttpStatusCode.OK, admin, HttpMethod.Post,
            $"/api/approvals/{s2.GetProperty("plan").GetProperty("activationApproval").GetProperty("id").GetString()}/reject", new { reason = "Line is booked" });
        var back = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{rejected}/activate", new { });
        Assert.Equal("returned_to_draft", back.GetProperty("outcome").GetString());
        Assert.Equal("draft", back.GetProperty("plan").GetProperty("summary").GetProperty("status").GetString());
        Assert.Contains("Line is booked", back.GetProperty("message").GetString());
    }

    [DockerFact]
    public async Task Changes_to_a_live_plan_wait_for_approval_and_two_live_plans_cannot_overlap_on_a_line()
    {
        var (admin, op, _) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var order = await NewOrder(op, (await NewStyle(admin)).GetProperty("id").GetString()!);
        var live = await MakeLive(op, admin, Id(await NewPlan(op, order, line, Today, Today.AddDays(3))));
        var id = Id(live);

        var noReason = await Expect(HttpStatusCode.BadRequest, op, HttpMethod.Put, $"/api/production/plans/{id}",
            new { operators = 25, expectedVersion = live.GetProperty("version").GetUInt32() });
        Assert.True(noReason.GetProperty("errors").TryGetProperty("reason", out _));
        var requested = await Expect(HttpStatusCode.OK, op, HttpMethod.Put, $"/api/production/plans/{id}",
            new { operators = 25, reason = "Absenteeism recovered", expectedVersion = live.GetProperty("version").GetUInt32() });
        Assert.Equal("change_requested", requested.GetProperty("outcome").GetString());
        var plan = requested.GetProperty("plan");
        Assert.Equal(20, plan.GetProperty("values").GetProperty("operators").GetInt32());   // live values unchanged
        var change = plan.GetProperty("changes").EnumerateArray().First(c => c.GetProperty("status").GetString() == "pending");
        Assert.Contains(change.GetProperty("diff").EnumerateArray(), d => d.GetProperty("field").GetString() == "operators" && d.GetProperty("to").GetString() == "25");
        var changeId = change.GetProperty("id").GetString();

        var early = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Post, $"/api/production/plans/{id}/changes/{changeId}/resolve");
        Assert.Equal("approval_pending", early.GetProperty("title").GetString());
        await Expect(HttpStatusCode.OK, admin, HttpMethod.Post, $"/api/approvals/{change.GetProperty("approval").GetProperty("id").GetString()}/approve", new { });
        var applied = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{id}/changes/{changeId}/resolve");
        Assert.Equal("change_applied", applied.GetProperty("outcome").GetString());
        Assert.Equal((25, 1), (applied.GetProperty("plan").GetProperty("values").GetProperty("operators").GetInt32(),
            applied.GetProperty("plan").GetProperty("summary").GetProperty("revision").GetInt32()));

        // A second live plan on the same line and dates can't be activated.
        var other = Id(await NewPlan(op, order, line, Today.AddDays(2), Today.AddDays(4)));
        var s = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/plans/{other}/submit", new { });
        await Expect(HttpStatusCode.OK, admin, HttpMethod.Post, $"/api/approvals/{s.GetProperty("plan").GetProperty("activationApproval").GetProperty("id").GetString()}/approve", new { });
        var clash = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Post, $"/api/production/plans/{other}/activate", new { });
        Assert.Equal("line_overlap", clash.GetProperty("title").GetString());

        Assert.Contains("production_plan.change_applied", await AuditActions("production_plan", id));
    }

    // ── Output, metrics, corrections ────────────────────────────────────────

    [DockerFact]
    public async Task Planned_versus_actual_efficiency_and_wip_are_computed_from_persisted_records()
    {
        var (admin, op, viewer) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var order = await NewOrder(op, (await NewStyle(admin, approvedSmv: 10)).GetProperty("id").GetString()!);
        var plan = Id(await MakeLive(op, admin, Id(await NewPlan(op, order, line, Yesterday, Yesterday))));

        // Yesterday 08:00–12:00 local: 50 + 55 + 70 with 20 operators, then 40 with no operator count.
        await Expect(HttpStatusCode.Created, op, HttpMethod.Post, "/api/production/output", Output(line, order, At(Yesterday, 8), At(Yesterday, 9), 300, null, "sewing_input"));
        foreach (var (h, q, ops) in new[] { (8, 50, (int?)20), (9, 55, 20), (10, 70, 20), (11, 40, null) })
            await Expect(HttpStatusCode.Created, op, HttpMethod.Post, "/api/production/output", Output(line, order, At(Yesterday, h), At(Yesterday, h + 1), q, ops));

        var s = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/summary?from={D(Yesterday)}&to={D(Yesterday)}&lineId={line}");
        var t = s.GetProperty("totals");
        // Planned through the last recorded interval (12:00): 240 scheduled minutes × 1 piece/min.
        Assert.Equal((215, 240, -25, 89.6), (t.GetProperty("actual").GetInt32(), t.GetProperty("planned").GetInt32(), t.GetProperty("variance").GetInt32(), t.GetProperty("achievementPct").GetDouble()));
        // Efficiency on the three records with operators: 175 × 10 ÷ (20 × 180) = 48.6 %.
        Assert.Equal((48.6, 1750.0, 3600.0), (t.GetProperty("efficiencyPct").GetDouble(), t.GetProperty("earnedMinutes").GetDouble(), t.GetProperty("availableMinutes").GetDouble()));
        Assert.Equal(100.0, t.GetProperty("capacityUtilizationPct").GetDouble());
        Assert.Equal(1, s.GetProperty("quality").GetProperty("missingOperators").GetInt32());
        Assert.Equal(At(Yesterday, 12), s.GetProperty("comparedThrough").GetDateTime().ToUniversalTime());
        var shiftA = s.GetProperty("byShift").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "a");
        Assert.Equal((215, 240), (shiftA.GetProperty("actual").GetInt32(), shiftA.GetProperty("planned").GetInt32()));
        var wip = s.GetProperty("wip").EnumerateArray().Single(w => w.GetProperty("lineId").GetString() == line)
            .GetProperty("buffers").EnumerateArray().Single(b => b.GetProperty("key").GetString() == "in_sewing");
        Assert.Equal((85, "ok"), (wip.GetProperty("pieces").GetInt32(), wip.GetProperty("state").GetString()));   // 300 loaded − 215 out
        Assert.True(s.GetProperty("definitions").TryGetProperty("lineEfficiencyPct", out _));

        // The same comparison on the plan and in the hourly line timeline.
        var detail = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/plans/{plan}");
        Assert.Equal((240, 215, 89.6), (detail.GetProperty("progress").GetProperty("plannedToDate").GetInt32(),
            detail.GetProperty("progress").GetProperty("actualToDate").GetInt32(), detail.GetProperty("progress").GetProperty("achievementPct").GetDouble()));
        var lineDetail = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/lines/{line}/detail?date={D(Yesterday)}");
        var first = lineDetail.GetProperty("timeline")[0];
        Assert.Equal(("08:00", 50, 60.0), (first.GetProperty("label").GetString(), first.GetProperty("actual").GetInt32(), first.GetProperty("planned").GetDouble()));
        Assert.Equal(5, lineDetail.GetProperty("events").GetArrayLength());

        // Daily filter by shift b (nothing recorded, nothing planned before the cutoff).
        var shiftB = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/summary?from={D(Yesterday)}&to={D(Yesterday)}&lineId={line}&shift=b");
        Assert.Equal((0, 0), (shiftB.GetProperty("totals").GetProperty("actual").GetInt32(), shiftB.GetProperty("totals").GetProperty("planned").GetInt32()));
        Assert.False(shiftB.GetProperty("totals").TryGetProperty("achievementPct", out _));   // not calculable, omitted — never 0
    }

    [DockerFact]
    public async Task Duplicate_overlapping_and_corrected_records_are_handled_without_double_counting()
    {
        var (admin, op, viewer) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var order = await NewOrder(op, (await NewStyle(admin)).GetProperty("id").GetString()!);
        var start = At(Yesterday, 9);

        var created = await Expect(HttpStatusCode.Created, op, HttpMethod.Post, "/api/production/output",
            Output(line, order, start, start.AddHours(1), 60, source: "integration", sourceRef: U("mes")));
        var id = created.GetProperty("id").GetString()!;
        var sourceRef = created.GetProperty("sourceRef").GetString();
        Assert.Equal(("a", D(Yesterday)), (created.GetProperty("shiftCode").GetString(), created.GetProperty("productionDate").GetString()));

        // Same source id, same values: the stored record comes back. Different values: conflict.
        var (status, replay, res) = await Send(op, HttpMethod.Post, "/api/production/output", Output(line, order, start, start.AddHours(1), 60, source: "integration", sourceRef: sourceRef));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("true", res.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(id, replay.GetProperty("id").GetString());
        var conflict = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Post, "/api/production/output", Output(line, order, start, start.AddHours(1), 61, source: "integration", sourceRef: sourceRef));
        Assert.Equal("duplicate_source_ref", conflict.GetProperty("title").GetString());
        var overlap = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Post, "/api/production/output", Output(line, order, start.AddMinutes(30), start.AddMinutes(90), 30));
        Assert.Equal("overlapping_record", overlap.GetProperty("title").GetString());

        // Invalid values are rejected with field errors.
        var invalid = await Expect(HttpStatusCode.BadRequest, op, HttpMethod.Post, "/api/production/output",
            Output(line, order, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(30), -1, operators: 0, stage: "ironing"));
        foreach (var key in new[] { "intervalEnd", "quantity", "operatorsPresent", "stage" })
            Assert.True(invalid.GetProperty("errors").TryGetProperty(key, out _), key);

        // A correction replaces the record; the original stays in history, superseded.
        await Expect(HttpStatusCode.BadRequest, op, HttpMethod.Post, $"/api/production/output/{id}/correct", new { quantity = 66 });
        var fixedRec = await Expect(HttpStatusCode.Created, op, HttpMethod.Post, $"/api/production/output/{id}/correct", new { quantity = 66, reason = "Recount at end of hour" });
        Assert.Equal(id, fixedRec.GetProperty("correctionOfId").GetString());
        var again = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Post, $"/api/production/output/{id}/correct", new { quantity = 67, reason = "x" });
        Assert.Equal("already_corrected", again.GetProperty("title").GetString());

        var live = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/output?lineId={line}");
        Assert.Equal(66, Assert.Single(live.GetProperty("items").EnumerateArray()).GetProperty("quantity").GetInt32());
        var all = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/output?lineId={line}&includeSuperseded=true");
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.Contains(all.GetProperty("items").EnumerateArray(), e => e.GetProperty("isSuperseded").GetBoolean());
        var summary = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/summary?from={D(Yesterday)}&to={D(Yesterday)}&lineId={line}");
        Assert.Equal(66, summary.GetProperty("totals").GetProperty("actual").GetInt32());
        Assert.Equal(1, summary.GetProperty("quality").GetProperty("correctedRecords").GetInt32());
        Assert.Equal(new[] { "production_output.recorded", "production_output.corrected" }, await AuditActions("production_output", id));
    }

    [DockerFact]
    public async Task Concurrent_submissions_of_the_same_interval_store_exactly_one_record()
    {
        var (admin, op, viewer) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var order = await NewOrder(op, (await NewStyle(admin)).GetProperty("id").GetString()!);
        var start = At(Yesterday, 13);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Send(Rag.Client(Api, Roles.Operator), HttpMethod.Post, "/api/production/output", Output(line, order, start, start.AddHours(1), 40 + i))));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Conflict));
        var list = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/output?lineId={line}");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [DockerFact]
    public async Task Import_is_all_or_nothing_and_a_reimport_is_recognised()
    {
        var (admin, op, viewer) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var order = await NewOrder(op, (await NewStyle(admin)).GetProperty("id").GetString()!);
        var number = (await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/orders/{order}")).GetProperty("order").GetProperty("orderNumber").GetString();
        object Row(int h, int q, string? stage = "sewing_output", string? lineId = null) =>
            new { lineId = lineId ?? line, orderNumber = number, stage, intervalStart = At(Yesterday, h), intervalEnd = At(Yesterday, h + 1), quantity = q, operatorsPresent = 18 };

        var rejected = await Expect(HttpStatusCode.BadRequest, op, HttpMethod.Post, "/api/production/output/import",
            new { fileName = "shift-a.csv", rows = new[] { Row(8, 50), Row(9, 52, "ironing"), Row(10, 51, lineId: "no-such-line") } });
        var report = rejected.GetProperty("result");
        Assert.Equal((0, 2), (report.GetProperty("created").GetInt32(), report.GetProperty("invalid").GetInt32()));
        Assert.Equal("not_imported", report.GetProperty("rows")[0].GetProperty("outcome").GetString());
        Assert.Equal(0, (await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/output?lineId={line}")).GetProperty("total").GetInt32());

        var rows = new[] { Row(8, 50), Row(9, 52), Row(10, 51) };
        var ok = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, "/api/production/output/import", new { fileName = "shift-a.csv", rows });
        Assert.Equal((3, 0), (ok.GetProperty("created").GetInt32(), ok.GetProperty("duplicates").GetInt32()));
        var again = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, "/api/production/output/import", new { fileName = "shift-a.csv", rows });
        Assert.Equal((0, 3), (again.GetProperty("created").GetInt32(), again.GetProperty("duplicates").GetInt32()));
        var list = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/output?lineId={line}&source=import");
        Assert.Equal(3, list.GetProperty("total").GetInt32());
    }

    [DockerFact]
    public async Task Order_risk_explains_its_inputs_or_says_what_is_missing()
    {
        var (admin, op, viewer) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var style = (await NewStyle(admin)).GetProperty("id").GetString()!;

        var unplanned = await NewOrder(op, style, 1000, Today.AddDays(3));
        var none = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/orders/{unplanned}/risk");
        Assert.Equal("insufficient_data", none.GetProperty("level").GetString());
        Assert.Equal(2, none.GetProperty("missingInputs").GetArrayLength());
        Assert.Equal("record_data", none.GetProperty("recommendations")[0].GetProperty("code").GetString());

        var order = await NewOrder(op, style, 3000, Today.AddDays(2));
        await MakeLive(op, admin, Id(await NewPlan(op, order, line, Yesterday, Today.AddDays(4), quantity: 3000)));
        foreach (var h in new[] { 8, 9, 10, 11 })
            await Expect(HttpStatusCode.Created, op, HttpMethod.Post, "/api/production/output", Output(line, order, At(Yesterday, h), At(Yesterday, h + 1), 45));
        var detail = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/orders/{order}");
        var risk = detail.GetProperty("risk");
        Assert.Contains(risk.GetProperty("level").GetString(), new[] { "low", "medium", "high" });
        Assert.Equal(3000 - 180, risk.GetProperty("remainingQuantity").GetInt32());
        var remainingMinutes = risk.GetProperty("remainingScheduledMinutes").GetDouble();
        Assert.True(remainingMinutes > 0);
        Assert.Equal(Math.Round(2820 / (remainingMinutes / 60), 1, MidpointRounding.AwayFromZero), risk.GetProperty("requiredRatePerLineHour").GetDouble(), 1);
        Assert.True(risk.GetProperty("observedRatePerLineHour").GetDouble() > 0);
        Assert.NotEmpty(risk.GetProperty("observations").EnumerateArray());
        Assert.Contains("not a calibrated probability", string.Join(" ", risk.GetProperty("assumptions").EnumerateArray().Select(a => a.GetString())));
        Assert.NotEmpty(risk.GetProperty("recommendations").EnumerateArray());
        Assert.Equal(180, detail.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage").GetString() == "sewing_output").GetProperty("good").GetInt32());
    }

    // ── Time studies, layouts, balancing ────────────────────────────────────

    [DockerFact]
    public async Task Bottleneck_uses_time_studies_and_a_balance_proposal_needs_admin_approval_before_it_becomes_the_layout()
    {
        var (admin, op, viewer) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin, workstations: 4);
        var style = await NewStyle(admin, approvedSmv: 2.6, operations: Bulletin);
        var styleId = style.GetProperty("id").GetString()!;
        var ops = style.GetProperty("operations").EnumerateArray().ToDictionary(o => o.GetProperty("sequence").GetInt32(), o => o.GetProperty("id").GetString()!);
        var order = await NewOrder(op, styleId);
        await MakeLive(op, admin, Id(await NewPlan(op, order, line, Yesterday, Today)));

        // The layout as found on the floor, then time studies of two operations.
        await Expect(HttpStatusCode.BadRequest, op, HttpMethod.Put, "/api/production/layouts/current", new
        {
            lineId = line, styleId, stations = Enumerable.Range(1, 5).Select(i => new { stationNo = i, operationIds = new[] { ops[1] } }),
        });
        var layout = await Expect(HttpStatusCode.Created, op, HttpMethod.Put, "/api/production/layouts/current", new
        {
            lineId = line, styleId,
            stations = new object[]
            {
                new { stationNo = 1, operationIds = new[] { ops[1] } }, new { stationNo = 2, operationIds = new[] { ops[2] } },
                new { stationNo = 3, operationIds = new[] { ops[3] } }, new { stationNo = 4, operationIds = new[] { ops[4], ops[5] } },
            },
        });
        Assert.Equal(0.8, layout.GetProperty("metrics").GetProperty("cycleTimeMinutes").GetDouble());
        await Expect(HttpStatusCode.Created, op, HttpMethod.Post, "/api/production/observations",
            new { lineId = line, styleId, operationId = ops[1], cycleSeconds = 28.0, sampleCount = 10, operatorsAssigned = 1, queueBefore = 4 });
        await Expect(HttpStatusCode.Created, op, HttpMethod.Post, "/api/production/observations",
            new { lineId = line, styleId, operationId = ops[2], cycleSeconds = 60.0, sampleCount = 10, operatorsAssigned = 1, queueBefore = 35 });
        await Expect(HttpStatusCode.BadRequest, op, HttpMethod.Post, "/api/production/observations",
            new { lineId = line, styleId, operationId = Guid.NewGuid(), cycleSeconds = -1, sampleCount = 0, operatorsAssigned = 1 });

        var lineDetail = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/lines/{line}/detail?date={D(Today)}");
        var bottleneck = lineDetail.GetProperty("styles").EnumerateArray().Single(s => s.GetProperty("styleId").GetString() == styleId).GetProperty("bottleneck");
        Assert.Equal(("observed", 2, 60.0), (bottleneck.GetProperty("status").GetString(),
            bottleneck.GetProperty("constraint").GetProperty("sequence").GetInt32(), bottleneck.GetProperty("constraintCapacityPerHour").GetDouble()));

        // Analysis is bounded by the declared workstations and stored separately from the current layout.
        await Expect(HttpStatusCode.BadRequest, op, HttpMethod.Post, "/api/production/balance/analyze", new { lineId = line, styleId, workstations = 9 });
        var analysis = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, "/api/production/balance/analyze", new { lineId = line, styleId, workstations = 4 });
        Assert.True(analysis.GetProperty("result").GetProperty("feasible").GetBoolean());
        var proposal = analysis.GetProperty("proposal");
        var pid = proposal.GetProperty("id").GetString()!;
        Assert.Equal("proposed", proposal.GetProperty("status").GetString());
        Assert.True(proposal.GetProperty("comparison").TryGetProperty("current", out _));
        Assert.True(proposal.GetProperty("result").GetProperty("proposed").GetProperty("stations").GetInt32() <= 4);
        Assert.Equal("manual", (await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/layouts/current?lineId={line}&styleId={styleId}")).GetProperty("origin").GetString());

        // High risk: an Operator can't approve it, an Admin can; then it is applied explicitly.
        var submitted = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/balance/proposals/{pid}/submit", new { });
        var approval = submitted.GetProperty("proposal").GetProperty("approval");
        Assert.Equal("high", approval.GetProperty("riskTier").GetString());
        var notYet = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Post, $"/api/production/balance/proposals/{pid}/apply");
        Assert.Equal("approval_pending", notYet.GetProperty("title").GetString());
        await Expect(HttpStatusCode.Forbidden, op, HttpMethod.Post, $"/api/approvals/{approval.GetProperty("id").GetString()}/approve", new { });
        await Expect(HttpStatusCode.OK, admin, HttpMethod.Post, $"/api/approvals/{approval.GetProperty("id").GetString()}/approve", new { });
        var applied = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, $"/api/production/balance/proposals/{pid}/apply");
        Assert.Equal("applied", applied.GetProperty("outcome").GetString());
        var current = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/layouts/current?lineId={line}&styleId={styleId}");
        Assert.Equal(("proposal", pid), (current.GetProperty("origin").GetString(), current.GetProperty("proposalId").GetString()));
        Assert.Contains("balance_proposal.applied", await AuditActions("balance_proposal", pid));

        // A proposal computed from an outdated bulletin can't be submitted.
        var second = (await Expect(HttpStatusCode.OK, op, HttpMethod.Post, "/api/production/balance/analyze", new { lineId = line, styleId }))
            .GetProperty("proposal").GetProperty("id").GetString();
        var latest = await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/styles/{styleId}");
        var changedOps = latest.GetProperty("operations").EnumerateArray().Select(o => new
        {
            id = o.GetProperty("id").GetString(), sequence = o.GetProperty("sequence").GetInt32(), name = o.GetProperty("name").GetString(),
            smv = o.GetProperty("sequence").GetInt32() == 5 ? 0.35 : o.GetProperty("smv").GetDouble(),
            machineType = o.TryGetProperty("machineType", out var m) ? m.GetString() : null,
            predecessors = o.GetProperty("predecessors").EnumerateArray().Select(p => p.GetInt32()).ToArray(),
        }).ToArray();
        await Expect(HttpStatusCode.OK, admin, HttpMethod.Put, $"/api/production/styles/{styleId}", new { operations = changedOps, expectedVersion = latest.GetProperty("version").GetUInt32() });
        var stale = await Expect(HttpStatusCode.Conflict, op, HttpMethod.Post, $"/api/production/balance/proposals/{second}/submit", new { });
        Assert.Equal("inputs_changed", stale.GetProperty("title").GetString());
    }

    [DockerFact]
    public async Task Balancing_without_an_operation_bulletin_explains_what_is_missing_and_stores_nothing()
    {
        var (admin, op, viewer) = Clients();
        var line = await NewLine(admin);
        var styleId = (await NewStyle(admin)).GetProperty("id").GetString()!;
        var r = await Expect(HttpStatusCode.OK, op, HttpMethod.Post, "/api/production/balance/analyze", new { lineId = line, styleId });
        Assert.False(r.GetProperty("result").GetProperty("feasible").GetBoolean());
        Assert.False(r.TryGetProperty("proposal", out _));
        Assert.Contains("operation bulletin", r.GetProperty("missingInputs")[0].GetString());
        Assert.Equal(0, (await Expect(HttpStatusCode.OK, viewer, HttpMethod.Get, $"/api/production/balance/proposals?styleId={styleId}")).GetProperty("total").GetInt32());
    }

    // ── Database ────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Migration_is_applied_and_the_database_enforces_the_constraints()
    {
        var migrations = await Rag.Strings(Api, "select \"MigrationId\" from \"__ef_migrations\"");
        Assert.Contains(migrations, m => m.EndsWith("_AddProductionPlanning", StringComparison.Ordinal));

        var (admin, op, _) = Clients();
        await EnsureCalendar(admin);
        var line = await NewLine(admin);
        var order = await NewOrder(op, (await NewStyle(admin)).GetProperty("id").GetString()!);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO production_output_events ("Id","LineId","OrderId","Stage","IntervalStartUtc","IntervalEndUtc","ProductionDate","Quantity","ReworkQuantity","RejectQuantity","Source","IsSuperseded","RecordedAt","RecordedByEmail")
            VALUES ({Guid.NewGuid()}, {line}, {Guid.Parse(order)}, 'SewingOutput', now() - interval '1 hour', now(), current_date, -5, 0, 0, 'Manual', false, now(), 'sql@test')
            """)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(ex).SqlState);
    }
}
