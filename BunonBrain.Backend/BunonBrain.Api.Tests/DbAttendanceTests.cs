using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Attendance;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Shifts, attendance, breaks, corrections and overtime end to end, against
/// real PostgreSQL, through the HTTP API. Times are set explicitly (in the
/// factory's Asia/Dhaka time zone) so every expectation is exact.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAttendanceTests
{
    private readonly PostgresStack _stack;
    public DbAttendanceTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;
    private static readonly TimeZoneInfo Dhaka = AttendanceRules.ResolveTimeZone("Asia/Dhaka")!;
    private static DateOnly Today => AttendanceCalculator.LocalDate(DateTimeOffset.UtcNow, Dhaka);
    /// <summary>Two days ago: a whole working day in the past (within the 7-day backdate window).</summary>
    private static DateOnly D => Today.AddDays(-2);
    private static DateTimeOffset At(DateOnly d, int h, int m = 0) => new(d.ToDateTime(new TimeOnly(h, m)), TimeSpan.FromHours(6));
    private static string Iso(DateTimeOffset t) => t.ToString("O");
    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd");

    // ─── HTTP plumbing ─────────────────────────────────────────────────────

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public string Id => Body.GetProperty("id").GetString()!;
        public JsonElement Session => Body.GetProperty("session");
        public string Outcome => Body.GetProperty("event").GetProperty("outcome").GetString()!;
        public int Figure(string name) => Session.GetProperty("figures").GetProperty(name).GetInt32();
    }

    private async Task<Reply> Send(HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = Rag.Json(body);
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[')) ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string bearer, object body) => Send(HttpMethod.Post, path, bearer, body);
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, body);

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static IEnumerable<JsonElement> Items(Reply r) => r.Body.GetProperty("items").EnumerateArray();

    // ─── People, places, shifts ────────────────────────────────────────────

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, string? factory, params string[] lines)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@attendance.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        if (factory is not null) user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = Guid.Parse(factory) });
        foreach (var line in lines) user.Lines.Add(new UserProductionLineAssignment { UserId = id, LineId = line });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Person(id, email, await TokenFor(id));
    }

    private async Task<string> TokenFor(Guid userId)
    {
        using var scope = Api.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(userId, default);
        return scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _);
    }

    public sealed record Site(
        string Factory, string Sewing, string Line1, string Line2, Person Super, Person Admin, Person Hr, Person Hr2, Person Manager,
        Person Supervisor, Person Monitor, Person Devices, string Morning, string Night);

    private async Task<Site> NewSite()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var f = await Post("/api/factories", super.Token, new { code = $"T{Tag()[..6]}", name = $"Attendance factory {Tag()}" });
        Is(HttpStatusCode.Created, f);
        var admin = await NewPerson(R.FactoryAdmin, f.Id);
        var dept = await Post($"/api/factories/{f.Id}/departments", admin.Token, new { code = "SEW", name = "Sewing" });
        string l1 = $"tl-{Tag()}", l2 = $"tl-{Tag()}";
        await Rag.WithDb(Api, db =>
        {
            db.ProductionLines.Add(new ProductionLine { Id = l1, Name = $"Line 1 {l1}", Workstations = 20, FactoryId = Guid.Parse(f.Id) });
            db.ProductionLines.Add(new ProductionLine { Id = l2, Name = $"Line 2 {l2}", Workstations = 20, FactoryId = Guid.Parse(f.Id) });
            return db.SaveChangesAsync();
        });
        var hr = await NewPerson(R.HrOfficer, f.Id);
        var everyDay = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var morning = await Post("/api/shifts", hr.Token, new
        {
            factoryId = f.Id, code = "MORNING", name = "Morning", startLocal = "08:00", endLocal = "17:00", workingDays = everyDay,
            lateGraceMinutes = 10, earlyLeaveGraceMinutes = 5,
            breaks = new object[] { new { breakType = "Lunch", name = "Lunch", startLocal = "13:00", durationMinutes = 60, isPaid = false }, new { breakType = "Tea", name = "Tea", durationMinutes = 15, isPaid = true } },
        });
        Is(HttpStatusCode.Created, morning);
        var night = await Post("/api/shifts", hr.Token, new
        {
            factoryId = f.Id, code = "NIGHT", name = "Night", startLocal = "22:00", endLocal = "06:00", workingDays = everyDay,
            breaks = new object[] { new { breakType = "Lunch", name = "Meal", startLocal = "02:00", durationMinutes = 30, isPaid = false } },
        });
        Is(HttpStatusCode.Created, night);
        return new Site(f.Id, dept.Id, l1, l2, super, admin, hr, await NewPerson(R.HrOfficer, f.Id), await NewPerson(R.FactoryManager, f.Id),
            await NewPerson(R.ProductionSupervisor, f.Id, l1), await NewPerson(R.MonitoringOperator, f.Id), await NewPerson(R.DeviceAdmin, f.Id),
            morning.Id, night.Id);
    }

    private async Task<string> Worker(Site s, string? line = null, string? shift = null, string name = "Attendance Worker")
    {
        var w = await Post("/api/workers", s.Hr.Token, new
        {
            factoryId = s.Factory, fullName = name, phone = "01711" + Random.Shared.Next(100000, 999999), joinedOn = "2024-01-01",
            departmentId = s.Sewing, designation = "Operator", lineId = line ?? s.Line1,
        });
        Is(HttpStatusCode.Created, w);
        if (shift is not null)
            Is(HttpStatusCode.Created, await Post("/api/shift-assignments", s.Hr.Token, new { shiftId = shift, effectiveFrom = Day(Today.AddDays(-10)), workerIds = new[] { w.Id } }));
        return w.Id;
    }

    private Task<Reply> Punch(Person who, string type, string worker, DateTimeOffset at, string? key = null) =>
        Post($"/api/attendance/{type}", who.Token, new { workerId = worker, occurredAt = Iso(at), idempotencyKey = key });

    private Task<Reply> Break(Person who, string action, string worker, DateTimeOffset at, string type = "Lunch") =>
        Post($"/api/breaks/{action}", who.Token, new { workerId = worker, breakType = type, occurredAt = Iso(at) });

    // ─── Shifts and shift assignments ──────────────────────────────────────

    [DockerFact]
    public async Task Shifts_are_configurable_including_overnight_breaks_and_working_days()
    {
        var s = await NewSite();
        var list = await Get($"/api/shifts?factoryId={s.Factory}", s.Hr.Token);
        var shifts = list.Body.EnumerateArray().ToDictionary(x => x.GetProperty("code").GetString()!);
        var morning = shifts["MORNING"];
        Assert.Equal(("08:00", "17:00", false, 540, 60, 480), (morning.GetProperty("startLocal").GetString(), morning.GetProperty("endLocal").GetString(),
            morning.GetProperty("isOvernight").GetBoolean(), morning.GetProperty("scheduledMinutes").GetInt32(), morning.GetProperty("unpaidBreakMinutes").GetInt32(),
            morning.GetProperty("netScheduledMinutes").GetInt32()));
        Assert.Equal(2, morning.GetProperty("breaks").GetArrayLength());
        Assert.Equal((true, 480), (shifts["NIGHT"].GetProperty("isOvernight").GetBoolean(), shifts["NIGHT"].GetProperty("scheduledMinutes").GetInt32()));

        var evening = await Post("/api/shifts", s.Hr.Token, new { factoryId = s.Factory, code = "EVE", name = "Evening", startLocal = "17:00", endLocal = "01:00", workingDays = new[] { "Sat", "Sun", "Mon", "Tue", "Wed", "Thu" }, lateGraceMinutes = 5, description = "Second shift" });
        Is(HttpStatusCode.Created, evening);
        Assert.Equal(new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Sat" }, evening.Body.GetProperty("workingDays").EnumerateArray().Select(x => x.GetString()));
        Is(HttpStatusCode.Conflict, await Post("/api/shifts", s.Hr.Token, new { factoryId = s.Factory, code = "eve", name = "Other", startLocal = "09:00", endLocal = "10:00", workingDays = new[] { "Mon" } }), "shift_taken");
        Is(HttpStatusCode.BadRequest, await Post("/api/shifts", s.Hr.Token, new { factoryId = s.Factory, code = "X", name = "X", startLocal = "09:00", endLocal = "09:00", workingDays = new[] { "Mon" } }));
        Is(HttpStatusCode.BadRequest, await Post("/api/shifts", s.Hr.Token, new { factoryId = s.Factory, code = "Y", name = "Y", startLocal = "09:00", endLocal = "10:00", workingDays = new[] { "Mon" },
            breaks = new[] { new { breakType = "Lunch", name = "too long", durationMinutes = 60, isPaid = false } } }), "breaks_too_long");

        // Edit, deactivate and reactivate an unused shift; a shift in use can't be deactivated.
        var edit = new { name = "Evening", startLocal = "17:30", endLocal = "01:30", workingDays = new[] { "Mon", "Tue" }, lateGraceMinutes = 15, earlyLeaveGraceMinutes = 5, isActive = false };
        var off = await Put($"/api/shifts/{evening.Id}", s.Hr.Token, edit);
        Is(HttpStatusCode.OK, off);
        Assert.Equal(("17:30", false), (off.Body.GetProperty("startLocal").GetString(), off.Body.GetProperty("isActive").GetBoolean()));
        Is(HttpStatusCode.OK, await Put($"/api/shifts/{evening.Id}", s.Hr.Token, edit with { isActive = true }));
        await Worker(s, shift: s.Morning);
        Is(HttpStatusCode.Conflict, await Put($"/api/shifts/{s.Morning}", s.Hr.Token, new { name = "Morning", startLocal = "08:00", endLocal = "17:00", workingDays = new[] { "Mon" }, lateGraceMinutes = 10, earlyLeaveGraceMinutes = 5, isActive = false }), "shift_in_use");

        // Only shift administrators configure shifts; supervisors and managers may read them.
        foreach (var who in new[] { s.Supervisor, s.Manager, s.Monitor })
        {
            Is(HttpStatusCode.OK, await Get($"/api/shifts?factoryId={s.Factory}", who.Token));
            Is(HttpStatusCode.Forbidden, await Post("/api/shifts", who.Token, new { factoryId = s.Factory, code = "Z", name = "Z", startLocal = "09:00", endLocal = "10:00", workingDays = new[] { "Mon" } }), "permission_denied");
        }
        Is(HttpStatusCode.Forbidden, await Get("/api/shifts", s.Devices.Token), "permission_denied");
        Assert.Contains("shift.created", (await Get($"/api/audit?entityType=shift&factoryId={s.Factory}", s.Admin.Token)).Raw);
    }

    [DockerFact]
    public async Task Workers_are_assigned_to_shifts_one_by_one_or_by_line_with_history_and_no_conflicts()
    {
        var s = await NewSite();
        var a = await Worker(s, s.Line1, name: "Shift A");
        var b = await Worker(s, s.Line1, name: "Shift B");
        var c = await Worker(s, s.Line1, name: "Shift C");
        var other = await Worker(s, s.Line2, name: "Other line");

        // Bulk by line: the three workers of line 1, not the one on line 2.
        var bulk = await Post("/api/shift-assignments", s.Hr.Token, new { shiftId = s.Morning, effectiveFrom = Day(Today.AddDays(-10)), lineId = s.Line1, reason = "Line 1 on mornings" });
        Is(HttpStatusCode.Created, bulk);
        Assert.Equal((3, 3), (bulk.Body.GetProperty("requested").GetInt32(), bulk.Body.GetProperty("assigned").GetInt32()));
        Assert.Equal(0, (await Get($"/api/shift-assignments?workerId={other}", s.Hr.Token)).Body.GetProperty("total").GetInt32());

        // Reassigning closes the current assignment the day before; nothing is lost.
        var move = await Post("/api/shift-assignments", s.Hr.Token, new { shiftId = s.Night, effectiveFrom = Day(Today), workerIds = new[] { a } });
        Is(HttpStatusCode.Created, move);
        var history = (await Get($"/api/workers/{a}/shift-assignments", s.Hr.Token)).Body.EnumerateArray().ToList();
        Assert.Equal(new[] { "NIGHT", "MORNING" }, history.Select(h => h.GetProperty("shift").GetProperty("code").GetString()));
        Assert.Equal(("Superseded", Day(Today.AddDays(-1))), (history[1].GetProperty("status").GetString(), history[1].GetProperty("effectiveTo").GetString()));
        Assert.Equal(history[1].GetProperty("id").GetString(), history[0].GetProperty("previousAssignmentId").GetString());
        var current = (await Get($"/api/attendance/{a}", s.Hr.Token)).Body.GetProperty("currentShift");
        Assert.Equal("NIGHT", current.GetProperty("shift").GetProperty("code").GetString());

        // A later assignment makes an earlier-starting one conflict; the same shift again is skipped.
        Is(HttpStatusCode.Created, await Post("/api/shift-assignments", s.Hr.Token, new { shiftId = s.Night, effectiveFrom = Day(Today.AddDays(5)), workerIds = new[] { b } }));
        var conflict = await Post("/api/shift-assignments", s.Hr.Token, new { shiftId = s.Night, effectiveFrom = Day(Today.AddDays(2)), workerIds = new[] { b, c } });
        var outcomes = conflict.Body.GetProperty("results").EnumerateArray().ToDictionary(x => x.GetProperty("workerId").GetString()!, x => x.TryGetProperty("code", out var code) ? code.GetString() : null);
        Assert.Equal("shift_assignment_conflict", outcomes[b]);
        Assert.Null(outcomes[c]); // c was moved from Morning to Night from that date

        // The database refuses overlapping dates whatever the application does.
        var wid = Guid.Parse(c);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Rag.WithDb(Api, db =>
        {
            db.ShiftAssignments.Add(new WorkerShiftAssignment { WorkerId = wid, FactoryId = Guid.Parse(s.Factory), ShiftId = Guid.Parse(s.Morning),
                EffectiveFrom = Today, AssignedByEmail = "test" });
            return db.SaveChangesAsync();
        }));
        Assert.Equal("23P01", Assert.IsType<PostgresException>(ex.InnerException).SqlState);

        // Supervisors assign their own line only; ending an assignment keeps it in the history.
        var sup = await Post("/api/shift-assignments", s.Supervisor.Token, new { shiftId = s.Night, effectiveFrom = Day(Today.AddDays(20)), workerIds = new[] { c, other } });
        var supOutcomes = sup.Body.GetProperty("results").EnumerateArray().ToDictionary(x => x.GetProperty("workerId").GetString()!, x => x.GetProperty("outcome").GetString());
        Assert.Equal(("Skipped", "Skipped"), (supOutcomes[c], supOutcomes[other])); // c: already on Night; other: not their line
        var aNow = (await Get($"/api/shift-assignments?workerId={a}&status=Active", s.Hr.Token)).Body.GetProperty("items")[0].GetProperty("id").GetString();
        Is(HttpStatusCode.OK, await Post($"/api/shift-assignments/{aNow}/end", s.Hr.Token, new { effectiveTo = Day(Today.AddDays(30)), reason = "Seasonal" }));
        Assert.Equal(2, (await Get($"/api/workers/{a}/shift-assignments", s.Hr.Token)).Body.GetArrayLength());
        Is(HttpStatusCode.Forbidden, await Post("/api/shift-assignments", s.Monitor.Token, new { shiftId = s.Night, effectiveFrom = Day(Today), workerIds = new[] { a } }), "permission_denied");
    }

    // ─── Sessions and calculations ─────────────────────────────────────────

    [DockerFact]
    public async Task A_day_shift_session_is_calculated_from_punches_and_breaks()
    {
        var s = await NewSite();
        var w = await Worker(s, shift: s.Morning);
        Is(HttpStatusCode.OK, await Punch(s.Supervisor, "check-in", w, At(D, 8, 25)));
        var lunch = await Break(s.Supervisor, "start", w, At(D, 13));
        Is(HttpStatusCode.OK, lunch);
        Assert.Equal((60, "Active"), (lunch.Body.GetProperty("break").GetProperty("allowedMinutes").GetInt32(), lunch.Body.GetProperty("break").GetProperty("status").GetString()));
        Is(HttpStatusCode.OK, await Break(s.Supervisor, "end", w, At(D, 14, 15)));
        var done = await Punch(s.Supervisor, "check-out", w, At(D, 18, 30));
        Is(HttpStatusCode.OK, done);

        var session = done.Session;
        Assert.Equal(("Completed", Day(D), false), (session.GetProperty("status").GetString(), session.GetProperty("workDate").GetString(), session.GetProperty("needsReview").GetBoolean()));
        Assert.Equal("MORNING", session.GetProperty("shift").GetProperty("code").GetString());
        Assert.Equal(605, done.Figure("attendanceMinutes"));
        Assert.Equal(540, done.Figure("scheduledMinutes"));
        Assert.Equal(480, done.Figure("netScheduledMinutes"));
        Assert.Equal(75, done.Figure("breakMinutes"));
        Assert.Equal(15, done.Figure("overBreakMinutes"));
        Assert.Equal(530, done.Figure("netWorkingMinutes"));
        Assert.Equal(25, done.Figure("lateMinutes"));
        Assert.Equal(0, done.Figure("earlyDepartureMinutes"));
        Assert.Equal(90, done.Figure("beyondShiftMinutes"));
        Assert.Equal(0, done.Figure("approvedOvertimeMinutes"));
        Assert.True(session.GetProperty("isLate").GetBoolean());
        // Stored in UTC: 08:25 Dhaka is 02:25 UTC.
        Assert.Equal(At(D, 8, 25).ToUniversalTime(), DateTimeOffset.Parse(session.GetProperty("checkInAt").GetString()!).ToUniversalTime());

        // The raw events are kept separately from the derived session.
        var events = Items(await Get($"/api/attendance/events?workerId={w}", s.Hr.Token)).ToList();
        Assert.Equal(new[] { "CheckOut", "BreakEnd", "BreakStart", "CheckIn" }, events.Select(e => e.GetProperty("eventType").GetString()));
        Assert.All(events, e => Assert.Equal(("Manual", false, "Accepted"), (e.GetProperty("source").GetString(), e.GetProperty("isSimulated").GetBoolean(), e.GetProperty("outcome").GetString())));
        Assert.Contains("attendance.manual_check_in", (await Get($"/api/audit?entityType=attendance_session&factoryId={s.Factory}", s.Admin.Token)).Raw);
    }

    [DockerFact]
    public async Task Overnight_shifts_and_the_factory_time_zone_decide_the_work_date()
    {
        var s = await NewSite();
        var w = await Worker(s, shift: s.Night);
        // 21:55 on D to 06:20 on D+1: one session, work date D.
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", w, At(D, 21, 55)));
        var r = await Punch(s.Hr, "check-out", w, At(D.AddDays(1), 6, 20));
        Is(HttpStatusCode.OK, r);
        Assert.Equal((Day(D), "NIGHT"), (r.Session.GetProperty("workDate").GetString(), r.Session.GetProperty("shift").GetProperty("code").GetString()));
        Assert.Equal((505, 480, 0, 20), (r.Figure("attendanceMinutes"), r.Figure("scheduledMinutes"), r.Figure("lateMinutes"), r.Figure("beyondShiftMinutes")));
        Assert.Equal(At(D, 22).ToUniversalTime(), DateTimeOffset.Parse(r.Session.GetProperty("scheduledStart").GetString()!).ToUniversalTime());

        // Unscheduled: 00:30 in Dhaka is still the previous day in UTC; the work date follows the factory's time zone.
        var u = await Worker(s, name: "No shift");
        var late = await Punch(s.Hr, "check-in", u, At(D, 0, 30));
        Assert.Equal(Day(D), late.Session.GetProperty("workDate").GetString());
        Assert.Equal(D.AddDays(-1), DateOnly.FromDateTime(At(D, 0, 30).UtcDateTime));
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-out", u, At(D, 4, 30)));

        // With the factory switched to UTC, the same instant falls on the UTC date.
        var policy = new { timeZoneId = "UTC", duplicateWindowSeconds = 120, missingCheckOutAfterMinutes = 240, maxOpenSessionHours = 16, maxBackdateDays = 7,
            earlyCheckInWindowMinutes = 180, overtimeRoundingMinutes = 15, minOvertimeMinutes = 30, maxDailyOvertimeMinutes = 120, maxWeeklyOvertimeMinutes = 720 };
        Is(HttpStatusCode.OK, await Put($"/api/attendance/policy?factoryId={s.Factory}", s.Hr.Token, policy));
        var utcWorker = await Worker(s, name: "UTC check");
        var utc = await Punch(s.Hr, "check-in", utcWorker, At(D, 0, 30));
        Assert.Equal(Day(D.AddDays(-1)), utc.Session.GetProperty("workDate").GetString());
        Is(HttpStatusCode.OK, await Put($"/api/attendance/policy?factoryId={s.Factory}", s.Hr.Token, policy with { timeZoneId = "Asia/Dhaka" }));
        Assert.Equal("Asia/Dhaka", (await Get($"/api/attendance/policy?factoryId={s.Factory}", s.Hr.Token)).Body.GetProperty("timeZoneId").GetString());
        Is(HttpStatusCode.Forbidden, await Put($"/api/attendance/policy?factoryId={s.Factory}", s.Manager.Token, policy), "permission_denied");
    }

    // ─── Duplicates, idempotency, missing punches, concurrency ─────────────

    [DockerFact]
    public async Task Duplicate_check_ins_are_recognised_and_idempotency_keys_replay_the_first_result()
    {
        var s = await NewSite();
        var w = await Worker(s, name: "Duplicates");
        var now = DateTimeOffset.UtcNow;
        var first = await Punch(s.Hr, "check-in", w, now.AddMinutes(-20), "gate-1-0001");
        Is(HttpStatusCode.OK, first);
        Assert.Equal("Accepted", first.Outcome);
        var eventId = first.Body.GetProperty("event").GetProperty("id").GetInt64();

        // The same key again: the first result, nothing applied twice, no new event stored.
        var again = await Punch(s.Hr, "check-in", w, now.AddMinutes(-20), "gate-1-0001");
        Is(HttpStatusCode.OK, again);
        Assert.True(again.Body.GetProperty("replayed").GetBoolean());
        Assert.Equal(eventId, again.Body.GetProperty("event").GetProperty("id").GetInt64());

        // A reader double-read within the duplicate window: recorded as Duplicate, changes nothing.
        var dup = await Punch(s.Hr, "check-in", w, now.AddMinutes(-19));
        Is(HttpStatusCode.OK, dup);
        Assert.Equal("Duplicate", dup.Outcome);
        // A real second check-in while checked in: refused, but the event is kept.
        Is(HttpStatusCode.Conflict, await Punch(s.Hr, "check-in", w, now.AddMinutes(-5)), "already_checked_in");

        var events = Items(await Get($"/api/attendance/events?workerId={w}", s.Hr.Token)).Select(e => e.GetProperty("outcome").GetString()).ToList();
        Assert.Equal(new[] { "Rejected", "Duplicate", "Accepted" }, events);
        Assert.Equal(1, await Rag.WithDb(Api, db => db.AttendanceSessions.CountAsync(x => x.WorkerId == Guid.Parse(w))));
        Assert.Equal(1, (await Get($"/api/attendance?workerId={w}&status=Open", s.Hr.Token)).Body.GetProperty("total").GetInt32());

        // Raw events can't be changed or removed.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Rag.WithDb(Api, db => db.AttendanceEvents.Where(e => e.Id == eventId).ExecuteDeleteAsync()));
        Assert.Contains("append-only", ex.MessageText);
        await Assert.ThrowsAsync<PostgresException>(() => Rag.WithDb(Api, db => db.AttendanceEvents.Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.Outcome, "Rejected"))));
    }

    [DockerFact]
    public async Task Missing_check_ins_and_check_outs_are_flagged_for_review_never_left_open()
    {
        var s = await NewSite();
        // Check-out without a check-in: kept, flagged.
        var orphan = await Worker(s, shift: s.Morning, name: "Orphan check-out");
        var r = await Punch(s.Hr, "check-out", orphan, At(D, 17, 5));
        Is(HttpStatusCode.OK, r);
        Assert.Equal(("MissingCheckIn", true, Day(D)), (r.Session.GetProperty("status").GetString(), r.Session.GetProperty("needsReview").GetBoolean(), r.Session.GetProperty("workDate").GetString()));
        Assert.Equal("missing_check_in", r.Body.GetProperty("event").GetProperty("outcomeCode").GetString());

        // No check-out without a shift: open until the review closes it after the policy's 16 hours.
        var forgetful = await Worker(s, name: "Forgot to check out");
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", forgetful, DateTimeOffset.UtcNow.AddHours(-20)));
        Assert.Equal(1, (await Get($"/api/attendance?workerId={forgetful}&status=Open", s.Hr.Token)).Body.GetProperty("total").GetInt32());
        var review = await Post("/api/attendance/review/run", s.Hr.Token, new { });
        Is(HttpStatusCode.OK, review);
        Assert.True(review.Body.GetProperty("sessionsFlagged").GetInt32() >= 1);
        var flagged = Items(await Get($"/api/attendance?workerId={forgetful}", s.Hr.Token)).Single();
        Assert.Equal(("MissingCheckOut", true), (flagged.GetProperty("status").GetString(), flagged.GetProperty("needsReview").GetBoolean()));
        Assert.False(flagged.TryGetProperty("checkOutAt", out _)); // no check-out time is invented
        Assert.Equal(0, flagged.GetProperty("figures").GetProperty("attendanceMinutes").GetInt32());
        // The next check-in starts a fresh session.
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", forgetful, DateTimeOffset.UtcNow.AddMinutes(-1)));

        // With a shift: a stale session is closed the moment the next punch arrives, even before the review runs.
        var shifted = await Worker(s, shift: s.Morning, name: "Missed check-out");
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", shifted, At(D.AddDays(-1), 8)));
        var next = await Punch(s.Hr, "check-in", shifted, At(D, 7, 58));
        Is(HttpStatusCode.OK, next);
        var sessions = Items(await Get($"/api/attendance?workerId={shifted}&sort=date", s.Hr.Token)).ToList();
        Assert.Equal(new[] { "MissingCheckOut", "Open" }, sessions.Select(x => x.GetProperty("status").GetString()));

        var stats = await Get($"/api/attendance/statistics?factoryId={s.Factory}&date={Day(D)}", s.Hr.Token);
        Assert.True(stats.Body.GetProperty("missingPunches").GetInt32() >= 1);
        // The database never holds two open sessions for a worker.
        Assert.Equal(1, await Rag.WithDb(Api, db => db.AttendanceSessions.CountAsync(x => x.WorkerId == Guid.Parse(shifted) && x.Status == "Open")));
    }

    [DockerFact]
    public async Task Concurrent_punches_for_one_worker_are_processed_one_at_a_time()
    {
        var s = await NewSite();
        var w = await Worker(s, name: "Concurrent");
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        // Ten readers report a check-in at once (different keys, within seconds): one session, one acceptance.
        var replies = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Punch(s.Hr, "check-in", w, start.AddSeconds(i), $"burst-{i}")));
        Assert.Equal(1, replies.Count(r => r.Status == HttpStatusCode.OK && r.Outcome == "Accepted"));
        Assert.Equal(9, replies.Count(r => r.Status == HttpStatusCode.OK && r.Outcome == "Duplicate"));
        Assert.Equal(1, await Rag.WithDb(Api, db => db.AttendanceSessions.CountAsync(x => x.WorkerId == Guid.Parse(w))));
        Assert.Equal(10, await Rag.WithDb(Api, db => db.AttendanceEvents.CountAsync(x => x.WorkerId == Guid.Parse(w))));

        // The same key from five retries at once: stored once, every caller gets that event.
        var same = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Punch(s.Hr, "check-out", w, start.AddMinutes(20), "retry-out")));
        Assert.All(same, r => Is(HttpStatusCode.OK, r));
        Assert.Single(same.Select(r => r.Body.GetProperty("event").GetProperty("id").GetInt64()).Distinct());
        Assert.Equal(4, same.Count(r => r.Body.GetProperty("replayed").GetBoolean()));
        Assert.Equal(1, await Rag.WithDb(Api, db => db.AttendanceEvents.CountAsync(x => x.WorkerId == Guid.Parse(w) && x.EventType == "CheckOut")));
        Assert.Equal("Completed", (await Get($"/api/attendance?workerId={w}", s.Hr.Token)).Body.GetProperty("items")[0].GetProperty("status").GetString());
    }

    // ─── Breaks ────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Breaks_are_tracked_overdue_ones_are_listed_and_check_out_closes_them()
    {
        var s = await NewSite();
        var w = await Worker(s, shift: s.Morning, name: "Breaks");
        Is(HttpStatusCode.Conflict, await Break(s.Hr, "start", w, At(D, 9)), "not_checked_in");
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", w, At(D, 7, 55)));
        var tea = await Break(s.Hr, "start", w, At(D, 10), "Tea");
        Assert.Equal(15, tea.Body.GetProperty("break").GetProperty("allowedMinutes").GetInt32());
        Assert.Equal("Duplicate", (await Break(s.Hr, "start", w, At(D, 10, 1), "Tea")).Outcome);
        Is(HttpStatusCode.Conflict, await Break(s.Hr, "start", w, At(D, 10, 10), "Prayer"), "already_on_break");
        var end = await Break(s.Hr, "end", w, At(D, 10, 20), "Tea");
        Assert.Equal((20, "Completed"), (end.Body.GetProperty("break").GetProperty("actualMinutes").GetInt32(), end.Body.GetProperty("break").GetProperty("status").GetString()));
        Is(HttpStatusCode.Conflict, await Break(s.Hr, "end", w, At(D, 12)), "not_on_break");
        Is(HttpStatusCode.OK, await Break(s.Hr, "start", w, At(D, 13)));
        var co = await Punch(s.Hr, "check-out", w, At(D, 17));
        Is(HttpStatusCode.OK, co);
        // Lunch ran to the check-out: closed with it, and 20 + 240 break minutes are not working time.
        var breaks = Items(await Get($"/api/breaks?workerId={w}", s.Hr.Token)).ToList();
        Assert.Equal(new[] { "AutoClosed", "Completed" }, breaks.Select(b => b.GetProperty("status").GetString()));
        Assert.Equal((260, 545, 285), (co.Figure("breakMinutes"), co.Figure("attendanceMinutes"), co.Figure("netWorkingMinutes")));

        // A break running past its allowance right now is overdue.
        var live = await Worker(s, name: "On a long break");
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", live, DateTimeOffset.UtcNow.AddHours(-2)));
        Is(HttpStatusCode.OK, await Post("/api/breaks/start", s.Hr.Token, new { workerId = live, breakType = "Tea", occurredAt = Iso(DateTimeOffset.UtcNow.AddMinutes(-40)) }));
        var overdue = Items(await Get($"/api/breaks?factoryId={s.Factory}&overdue=true", s.Hr.Token)).ToList();
        var o = Assert.Single(overdue);
        Assert.True(o.GetProperty("isOverdue").GetBoolean());
        Assert.True(o.GetProperty("overMinutes").GetInt32() >= 24);
        var stats = await Get($"/api/attendance/statistics?factoryId={s.Factory}", s.Hr.Token);
        Assert.Equal((1, 1), (stats.Body.GetProperty("onBreak").GetInt32(), stats.Body.GetProperty("overdueBreaks").GetInt32()));
    }

    // ─── Overtime ──────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Overtime_goes_from_request_to_approval_to_actual_to_hr_confirmed_payable()
    {
        var s = await NewSite();
        var w = await Worker(s, shift: s.Morning, name: "Overtime");
        Is(HttpStatusCode.OK, await Punch(s.Supervisor, "check-in", w, At(D, 7, 58)));
        Is(HttpStatusCode.OK, await Punch(s.Supervisor, "check-out", w, At(D, 19)));

        // Time beyond the shift is not overtime by itself.
        var session = Items(await Get($"/api/attendance?workerId={w}", s.Hr.Token)).Single();
        Assert.Equal((120, 0), (session.GetProperty("figures").GetProperty("beyondShiftMinutes").GetInt32(), session.GetProperty("figures").GetProperty("approvedOvertimeMinutes").GetInt32()));

        var req = await Post("/api/overtime/request", s.Supervisor.Token, new { workerId = w, plannedStart = Iso(At(D, 17)), plannedEnd = Iso(At(D, 19)), reason = "Shipment cut-off" });
        Is(HttpStatusCode.Created, req);
        Assert.Equal(("PendingApproval", 120, Day(D)), (req.Body.GetProperty("status").GetString(), req.Body.GetProperty("requestedMinutes").GetInt32(), req.Body.GetProperty("workDate").GetString()));
        Is(HttpStatusCode.Conflict, await Post("/api/overtime/request", s.Hr.Token, new { workerId = w, plannedStart = Iso(At(D, 18)), plannedEnd = Iso(At(D, 20)), reason = "dup" }), "overtime_overlaps");
        Is(HttpStatusCode.Forbidden, await Post($"/api/overtime/{req.Id}/approve", s.Supervisor.Token, new { }), "cannot_decide_own");
        Is(HttpStatusCode.BadRequest, await Post($"/api/overtime/{req.Id}/approve", s.Manager.Token, new { approvedMinutes = 150 }), "approved_exceeds_requested");

        // Approved after the fact: the attendance that already happened is measured at once.
        var approved = await Post($"/api/overtime/{req.Id}/approve", s.Manager.Token, new { approvedMinutes = 90, note = "Only until 18:30" });
        Is(HttpStatusCode.OK, approved);
        var o = approved.Body;
        Assert.Equal("Completed", o.GetProperty("status").GetString());
        Assert.Equal((120, 90, 90, 120), (o.GetProperty("requestedMinutes").GetInt32(), o.GetProperty("approvedMinutes").GetInt32(),
            o.GetProperty("actualMinutes").GetInt32(), o.GetProperty("beyondShiftMinutes").GetInt32()));
        Assert.False(o.TryGetProperty("payableMinutes", out _));            // never automatic
        Assert.Equal(90, o.GetProperty("payableSuggestionMinutes").GetInt32());
        Assert.Equal(new[] { "Requested", "Submitted", "Approved", "Completed" }, o.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("action").GetString()));
        var updated = Items(await Get($"/api/attendance?workerId={w}", s.Hr.Token)).Single();
        Assert.Equal(90, updated.GetProperty("figures").GetProperty("approvedOvertimeMinutes").GetInt32());

        // Payable minutes: only HR, only up to what was approved and attended.
        Is(HttpStatusCode.Forbidden, await Post($"/api/overtime/{req.Id}/confirm-payable", s.Manager.Token, new { payableMinutes = 60 }), "permission_denied");
        Is(HttpStatusCode.BadRequest, await Post($"/api/overtime/{req.Id}/confirm-payable", s.Hr.Token, new { payableMinutes = 100 }), "payable_exceeds_verified");
        var paid = await Post($"/api/overtime/{req.Id}/confirm-payable", s.Hr.Token, new { payableMinutes = 75, note = "Rounded to policy" });
        Is(HttpStatusCode.OK, paid);
        Assert.Equal((75, s.Hr.Email), (paid.Body.GetProperty("payableMinutes").GetInt32(), paid.Body.GetProperty("payableConfirmedByEmail").GetString()));

        // Rejection and cancellation keep the request on record.
        var w2 = await Worker(s, shift: s.Morning, name: "Overtime 2");
        var r2 = await Post("/api/overtime/request", s.Hr.Token, new { workerId = w2, plannedStart = Iso(At(Today.AddDays(1), 17)), plannedEnd = Iso(At(Today.AddDays(1), 18)), reason = "Rework" });
        Is(HttpStatusCode.OK, await Post($"/api/overtime/{r2.Id}/reject", s.Manager.Token, new { reason = "Not needed" }));
        var r3 = await Post("/api/overtime/request", s.Hr.Token, new { workerId = w2, plannedStart = Iso(At(Today.AddDays(2), 17)), plannedEnd = Iso(At(Today.AddDays(2), 18)), reason = "Rework", submit = false });
        Assert.Equal("Requested", r3.Body.GetProperty("status").GetString());
        Is(HttpStatusCode.OK, await Post($"/api/overtime/{r3.Id}/submit", s.Hr.Token, new { }));
        Is(HttpStatusCode.OK, await Post($"/api/overtime/{r3.Id}/cancel", s.Hr.Token, new { reason = "Order moved" }));
        var statuses = Items(await Get($"/api/overtime?workerId={w2}", s.Hr.Token)).Select(x => x.GetProperty("status").GetString()).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "Cancelled", "Rejected" }, statuses);
        Is(HttpStatusCode.Conflict, await Post($"/api/overtime/{r2.Id}/approve", s.Manager.Token, new { }), "overtime_wrong_status");
        Assert.True((await Get($"/api/attendance/statistics?factoryId={s.Factory}&date={Day(D)}", s.Hr.Token)).Body.GetProperty("completedOvertime").GetInt32() >= 1);
    }

    // ─── Corrections ───────────────────────────────────────────────────────

    [DockerFact]
    public async Task Corrections_need_a_second_person_and_keep_the_original_values()
    {
        var s = await NewSite();
        var w = await Worker(s, shift: s.Morning, name: "Corrected");
        var orphan = await Punch(s.Supervisor, "check-out", w, At(D, 17));
        var sessionId = orphan.Session.GetProperty("id").GetString();

        var c = await Post($"/api/attendance/{sessionId}/correction", s.Hr.Token, new { checkInAt = Iso(At(D, 8, 2)), reason = "Card reader at gate 2 was offline" });
        Is(HttpStatusCode.Created, c);
        Is(HttpStatusCode.Conflict, await Post($"/api/attendance/{sessionId}/correction", s.Hr2.Token, new { checkInAt = Iso(At(D, 8)), reason = "again" }), "correction_pending");
        Is(HttpStatusCode.Forbidden, await Post($"/api/attendance/corrections/{c.Id}/approve", s.Hr.Token, new { }), "cannot_decide_own");
        Is(HttpStatusCode.Forbidden, await Post($"/api/attendance/corrections/{c.Id}/approve", s.Supervisor.Token, new { }), "permission_denied");
        var ok = await Post($"/api/attendance/corrections/{c.Id}/approve", s.Manager.Token, new { note = "Checked the gate log" });
        Is(HttpStatusCode.OK, ok);
        Assert.Equal(("Approved", s.Manager.Email), (ok.Body.GetProperty("status").GetString(), ok.Body.GetProperty("decidedByEmail").GetString()));
        Assert.False(ok.Body.TryGetProperty("originalCheckIn", out _));
        Assert.NotNull(ok.Body.GetProperty("originalCheckOut").GetString());

        var fixedSession = (await Get($"/api/attendance/sessions/{sessionId}", s.Hr.Token)).Body;
        Assert.Equal(("Completed", false, true, "Correction"), (fixedSession.GetProperty("status").GetString(), fixedSession.GetProperty("needsReview").GetBoolean(),
            fixedSession.GetProperty("isCorrected").GetBoolean(), fixedSession.GetProperty("checkInSource").GetString()));
        Assert.Equal(538, fixedSession.GetProperty("figures").GetProperty("attendanceMinutes").GetInt32());
        Assert.Contains(Items(await Get($"/api/attendance/events?workerId={w}", s.Hr.Token)), e => e.GetProperty("source").GetString() == "Correction");
        Is(HttpStatusCode.Conflict, await Post($"/api/attendance/corrections/{c.Id}/reject", s.Hr2.Token, new { }), "correction_decided");

        // A day with no punches at all: a session is created only once approved; a rejection changes nothing.
        var missing = await Post("/api/attendance/corrections", s.Hr.Token, new { workerId = w, checkInAt = Iso(At(D.AddDays(-1), 8)), checkOutAt = Iso(At(D.AddDays(-1), 17)), reason = "Worked; badge forgotten" });
        Is(HttpStatusCode.Created, missing);
        Assert.Equal(1, (await Get($"/api/attendance?workerId={w}", s.Hr.Token)).Body.GetProperty("total").GetInt32());
        Is(HttpStatusCode.OK, await Post($"/api/attendance/corrections/{missing.Id}/approve", s.Hr2.Token, new { }));
        var created = Items(await Get($"/api/attendance?workerId={w}&from={Day(D.AddDays(-1))}&to={Day(D.AddDays(-1))}", s.Hr.Token)).Single();
        Assert.Equal((true, 540), (created.GetProperty("isCorrected").GetBoolean(), created.GetProperty("figures").GetProperty("attendanceMinutes").GetInt32()));
        Is(HttpStatusCode.Conflict, await Post("/api/attendance/corrections", s.Hr.Token, new { workerId = w, checkInAt = Iso(At(D.AddDays(-1), 9)), checkOutAt = Iso(At(D.AddDays(-1), 10)), reason = "overlap" }), "correction_overlaps");
        var rejected = await Post("/api/attendance/corrections", s.Hr.Token, new { workerId = w, checkInAt = Iso(At(D.AddDays(-3), 8)), checkOutAt = Iso(At(D.AddDays(-3), 17)), reason = "Claimed" });
        Is(HttpStatusCode.OK, await Post($"/api/attendance/corrections/{rejected.Id}/reject", s.Manager.Token, new { note = "No evidence" }));
        Assert.Equal(2, (await Get($"/api/attendance?workerId={w}", s.Hr.Token)).Body.GetProperty("total").GetInt32());
        var actions = (await Get($"/api/audit?entityType=attendance_correction&factoryId={s.Factory}&limit=50", s.Admin.Token)).Raw;
        foreach (var a in new[] { "attendance.correction_requested", "attendance.correction_approved", "attendance.correction_rejected" }) Assert.Contains(a, actions);
    }

    // ─── Simulator and devices ─────────────────────────────────────────────

    [DockerFact]
    public async Task Simulated_rfid_events_use_the_same_pipeline_but_are_never_shown_as_real()
    {
        var s = await NewSite();
        var w = await Worker(s, shift: s.Morning, name: "Simulated");
        var card = await Post($"/api/workers/{w}/cards", s.Hr.Token, new { cardUid = "04" + Tag().ToUpperInvariant() });
        var readers = (await Get($"/api/simulator/rfid/readers?factoryId={s.Factory}", s.Hr.Token)).Body.EnumerateArray().ToList();
        Assert.Equal(new[] { "SIM-ENTRY", "SIM-EXIT" }, readers.Select(r => r.GetProperty("code").GetString()).OrderBy(x => x));
        Assert.All(readers, r => Assert.True(r.GetProperty("isSimulated").GetBoolean()));
        var entry = readers.Single(r => r.GetProperty("code").GetString() == "SIM-ENTRY").GetProperty("id").GetString();

        var sim = await Post("/api/simulator/rfid/check-in", s.Hr.Token, new { workerId = w, readerId = entry, occurredAt = Iso(At(D, 8)) });
        Is(HttpStatusCode.OK, sim);
        var ev = sim.Body.GetProperty("event");
        Assert.Equal(("Simulator", true, "SIM-ENTRY", card.Body.GetProperty("cardUid").GetString()),
            (ev.GetProperty("source").GetString(), ev.GetProperty("isSimulated").GetBoolean(), ev.GetProperty("readerCode").GetString(), ev.GetProperty("cardUid").GetString()));
        Assert.True(sim.Session.GetProperty("hasSimulatedEvents").GetBoolean());
        Is(HttpStatusCode.OK, await Post("/api/simulator/rfid/check-out", s.Hr.Token, new { workerId = w, occurredAt = Iso(At(D, 17)) }));
        Assert.Equal(2, (await Get($"/api/simulator/rfid/events?factoryId={s.Factory}", s.Hr.Token)).Body.GetProperty("total").GetInt32());

        // The device endpoint only takes real readers; real events are never simulated.
        Is(HttpStatusCode.BadRequest, await Post("/api/attendance/events", s.Devices.Token, new { readerId = entry, cardUid = "x1", eventType = "CheckIn", occurredAt = Iso(At(D, 9)), idempotencyKey = "k" }), "reader_simulated");
        var gate = await Post("/api/attendance/readers", s.Devices.Token, new { factoryId = s.Factory, code = "GATE-1", name = "Main gate", direction = "Both", kind = "Rfid" });
        Is(HttpStatusCode.Created, gate);
        var real = await Post("/api/attendance/events", s.Devices.Token, new { readerId = gate.Id, cardUid = card.Body.GetProperty("cardUid").GetString(), eventType = "CheckIn", occurredAt = Iso(At(D.AddDays(1), 8, 3)), idempotencyKey = "gate1-778" });
        Is(HttpStatusCode.OK, real);
        Assert.Equal(("Device", false), (real.Body.GetProperty("event").GetProperty("source").GetString(), real.Body.GetProperty("event").GetProperty("isSimulated").GetBoolean()));
        Assert.False(real.Session.GetProperty("hasSimulatedEvents").GetBoolean());
        Is(HttpStatusCode.NotFound, await Post("/api/attendance/events", s.Devices.Token, new { readerId = gate.Id, cardUid = "FFFF0000", eventType = "CheckIn", occurredAt = Iso(At(D, 9)), idempotencyKey = "k2" }), "card_not_recognised");
        Is(HttpStatusCode.BadRequest, await Post("/api/simulator/rfid/check-in", s.Hr.Token, new { workerId = w, readerId = gate.Id }), "reader_not_simulated");

        // The database refuses a simulator event that isn't flagged as simulated.
        Assert.Equal(1, (await Get($"/api/attendance?workerId={w}&simulated=true", s.Hr.Token)).Body.GetProperty("total").GetInt32());
        var stats = await Get($"/api/attendance/statistics?factoryId={s.Factory}&date={Day(D)}", s.Hr.Token);
        Assert.Equal((2, 0), (stats.Body.GetProperty("simulatedEvents").GetInt32(), stats.Body.GetProperty("deviceEvents").GetInt32()));
        // IT can register readers but sees no attendance.
        Is(HttpStatusCode.Forbidden, await Get("/api/attendance", s.Devices.Token), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Post("/api/simulator/rfid/check-in", s.Devices.Token, new { workerId = w }), "permission_denied");
    }

    // ─── Isolation, roles, dashboard and reports ───────────────────────────

    [DockerFact]
    public async Task Factories_lines_and_roles_see_only_their_attendance()
    {
        var (s, other) = (await NewSite(), await NewSite());
        var mine = await Worker(s, s.Line1, s.Morning, "Line 1 worker");
        var line2 = await Worker(s, s.Line2, s.Morning, "Line 2 worker");
        foreach (var w in new[] { mine, line2 })
        {
            Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", w, At(D, 8)));
            Is(HttpStatusCode.OK, await Punch(s.Hr, "check-out", w, At(D, 17)));
        }

        // Another factory: nothing visible, nothing writable.
        Assert.Equal(0, (await Get($"/api/attendance?factoryId={s.Factory}", other.Hr.Token)).Body.GetProperty("total").GetInt32());
        Is(HttpStatusCode.NotFound, await Get($"/api/attendance/{mine}", other.Hr.Token), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Punch(other.Hr, "check-in", mine, At(D.AddDays(1), 8)), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Post("/api/shift-assignments", other.Hr.Token, new { shiftId = s.Morning, effectiveFrom = Day(Today), workerIds = new[] { mine } }), "shift_not_found");
        Is(HttpStatusCode.NotFound, await Post("/api/overtime/request", other.Hr.Token, new { workerId = mine, plannedStart = Iso(At(Today, 17)), plannedEnd = Iso(At(Today, 18)), reason = "x" }), "worker_not_found");

        // A supervisor sees and records their own line only.
        Assert.Equal(new[] { mine }, Items(await Get("/api/attendance", s.Supervisor.Token)).Select(x => x.GetProperty("workerId").GetString()));
        Is(HttpStatusCode.NotFound, await Get($"/api/attendance/{line2}", s.Supervisor.Token));
        Is(HttpStatusCode.NotFound, await Punch(s.Supervisor, "check-in", line2, At(D.AddDays(1), 8)));
        Is(HttpStatusCode.Forbidden, await Post("/api/attendance/review/run", s.Supervisor.Token, new { }), "permission_denied");

        // Monitoring operator: read-only.
        Is(HttpStatusCode.OK, await Get("/api/attendance/statistics", s.Monitor.Token));
        Is(HttpStatusCode.Forbidden, await Punch(s.Monitor, "check-in", mine, At(D.AddDays(1), 8)), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Get("/api/overtime", s.Monitor.Token), "permission_denied");

        // A worker: their own attendance, shift and overtime only.
        var account = await NewPerson(R.Worker, s.Factory);
        Is(HttpStatusCode.OK, await Put($"/api/workers/{mine}/account", s.Admin.Token, new { userId = account.Id }));
        var me = await TokenFor(account.Id);
        var own = await Get("/api/attendance/me", me);
        Is(HttpStatusCode.OK, own);
        Assert.Equal(("MORNING", 1), (own.Body.GetProperty("currentShift").GetProperty("shift").GetProperty("code").GetString(), own.Body.GetProperty("sessions").GetArrayLength()));
        Is(HttpStatusCode.OK, await Get("/api/workers/me/shift-assignments", me));
        Is(HttpStatusCode.NotFound, await Get($"/api/attendance/{line2}", me));
        foreach (var path in new[] { "/api/attendance", "/api/attendance/statistics", "/api/breaks", "/api/overtime", "/api/shift-assignments", "/api/attendance/events" })
            Is(HttpStatusCode.Forbidden, await Get(path, me), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Punch(new Person(account.Id, account.Email, me), "check-in", mine, At(Today, 8)), "permission_denied");
    }

    [DockerFact]
    public async Task The_dashboard_and_reports_add_up()
    {
        var s = await NewSite();
        var onTime = await Worker(s, shift: s.Morning, name: "On time");
        var late = await Worker(s, shift: s.Morning, name: "Late");
        var absent = await Worker(s, shift: s.Morning, name: "Absent");
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", onTime, At(D, 7, 55)));
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-out", onTime, At(D, 17, 2)));
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-in", late, At(D, 8, 40)));
        Is(HttpStatusCode.OK, await Punch(s.Hr, "check-out", late, At(D, 16)));

        var stats = await Get($"/api/attendance/statistics?factoryId={s.Factory}&date={Day(D)}", s.Manager.Token);
        Is(HttpStatusCode.OK, stats);
        int N(string n) => stats.Body.GetProperty(n).GetInt32();
        Assert.Equal((3, 2, 1, 1, 0), (N("scheduledWorkers"), N("present"), N("absent"), N("late"), N("checkedIn")));

        var report = await Get($"/api/attendance/report?factoryId={s.Factory}&from={Day(D)}&to={Day(D)}", s.Manager.Token);
        Is(HttpStatusCode.OK, report);
        var rows = report.Body.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("workerId").GetString()!);
        Assert.Equal(2, rows.Count);
        Assert.Equal((1, 40, 1, 60), (rows[late].GetProperty("lateDays").GetInt32(), rows[late].GetProperty("lateMinutes").GetInt32(),
            rows[late].GetProperty("earlyDepartureDays").GetInt32(), rows[late].GetProperty("earlyDepartureMinutes").GetInt32()));
        Assert.Equal(547, rows[onTime].GetProperty("attendanceMinutes").GetInt32());
        Assert.Equal(547 + 440, report.Body.GetProperty("totals").GetProperty("attendanceMinutes").GetInt32());
        Is(HttpStatusCode.BadRequest, await Get($"/api/attendance/report?from={Day(D)}&to={Day(D.AddDays(-1))}", s.Manager.Token), "invalid_period");

        using var client = Api.RawClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.Manager.Token);
        var csv = await client.GetStringAsync($"/api/attendance/report/export?factoryId={s.Factory}&from={Day(D)}&to={Day(D)}");
        Assert.Contains("payable_overtime_minutes_hr_confirmed", csv);
        Assert.Equal(4, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length); // header, two workers, total
        Assert.Contains("attendance.report_exported", (await Get($"/api/audit?factoryId={s.Factory}&action=attendance.report_exported", s.Admin.Token)).Raw);
        _ = absent;
    }
}
