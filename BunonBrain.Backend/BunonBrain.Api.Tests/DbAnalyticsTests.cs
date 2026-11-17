using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Factory analytics against real PostgreSQL (a disposable local container,
/// never a shared database): real aggregates from attendance, IoT counters,
/// machine state and camera AI events; duplicates; REAL vs SIMULATED; event
/// source; factory and line authorization; sections hidden by role; report
/// generation and CSV / PDF export with their audit.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAnalyticsTests
{
    private readonly PostgresStack _stack;
    public DbAnalyticsTests(PostgresStack stack) { _stack = stack; }
    private DbApiFactory Api => _stack.Local;

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw, byte[] Bytes, string? ContentType)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
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

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string M() => $"m-{Guid.NewGuid():N}";
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)); // Asia/Dhaka (no attendance policy ⇒ default)

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, Guid factory, string? line = null)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@analytics.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = factory });
        if (line is not null) user.Lines.Add(new UserProductionLineAssignment { UserId = id, LineId = line });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, email, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    /// <summary>A factory with two lines; a machine with a real sensor and counter on line 1; three scheduled workers (two checked in); a camera.</summary>
    public sealed record Rig(Guid Factory, string Code, string Line1, string Line2, Guid Machine, string SensorCode, string CounterCode, Guid Camera,
        Person Manager, Person Devices, Person Hr, Person Supervisor2, Person Admin);

    private async Task<Rig> NewRig()
    {
        var t = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var factory = Guid.NewGuid();
        var dept = Guid.NewGuid();
        var (l1, l2) = ($"an-{t.ToLowerInvariant()}-1", $"an-{t.ToLowerInvariant()}-2");
        var (ws1, ws2, machine, sensor, counter, shift, cam) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (w1, w2, w3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        await Rag.WithDb(Api, async db =>
        {
            db.Factories.Add(new Factory { Id = factory, Code = $"A{t}", Name = $"Analytics {t}" });
            await db.SaveChangesAsync();
            db.Departments.Add(new Department { Id = dept, FactoryId = factory, Code = $"D{t[..4]}", Name = $"Sewing {t}" });
            db.ProductionLines.Add(new ProductionLine { Id = l1, Name = $"Line one {t}", Workstations = 10, FactoryId = factory });
            db.ProductionLines.Add(new ProductionLine { Id = l2, Name = $"Line two {t}", Workstations = 10, FactoryId = factory });
            db.Shifts.Add(new Shift { Id = shift, FactoryId = factory, Code = "ALLDAY", Name = "All day", StartLocal = new TimeOnly(0, 0), EndLocal = new TimeOnly(23, 59), WorkingDays = 0b1111111 });
            await db.SaveChangesAsync();
            db.Workstations.Add(new Workstation { Id = ws1, FactoryId = factory, DepartmentId = dept, LineId = l1, Code = $"T-{t[..3]}1", Capacity = 1, Position = 1 });
            db.Workstations.Add(new Workstation { Id = ws2, FactoryId = factory, DepartmentId = dept, LineId = l2, Code = $"T-{t[..3]}2", Capacity = 1, Position = 1 });
            await db.SaveChangesAsync();
            var floor = new ProductionFloor { Id = Guid.NewGuid(), FactoryId = factory, Code = $"F{t[..4]}", Name = $"Floor {t}" };
            db.ProductionFloors.Add(floor);
            await db.SaveChangesAsync();
            db.Machines.Add(new Machine { Id = machine, FactoryId = factory, Code = $"M-{t[..4]}", MachineType = "Lockstitch", WorkstationId = ws1 });
            db.Cameras.Add(new Camera { Id = cam, FactoryId = factory, Code = $"CAM-{t[..4]}", Name = "Line camera", CameraType = "IpCamera", FloorId = floor.Id, DepartmentId = dept, LineId = l1, Status = "Active", CreatedByEmail = "test" });
            await db.SaveChangesAsync();
            db.Devices.Add(new Device { Id = sensor, FactoryId = factory, Code = $"S-{t[..4]}", Name = "Sensor", Category = "MachineSensor", MachineId = machine, WorkstationId = ws1, LineId = l1, CreatedByEmail = "test" });
            db.Devices.Add(new Device { Id = counter, FactoryId = factory, Code = $"PC-{t[..4]}", Name = "Counter", Category = "ProductionCounter", MachineId = machine, WorkstationId = ws1, LineId = l1, CreatedByEmail = "test" });
            await db.SaveChangesAsync();
            db.DeviceIotConfigurations.Add(new DeviceIotConfiguration { DeviceId = sensor, FactoryId = factory, DeviceType = "SEWING_MACHINE_SENSOR", Protocol = "HTTP", UpdatedByEmail = "test" });
            db.DeviceIotConfigurations.Add(new DeviceIotConfiguration { DeviceId = counter, FactoryId = factory, DeviceType = "PIECE_COUNTER", Protocol = "HTTP", UpdatedByEmail = "test" });
            foreach (var (w, line) in new[] { (w1, l1), (w2, l1), (w3, l2) })
                db.Workers.Add(new WorkerProfile { Id = w, WorkerNumber = await TestWorkers.NextNumberAsync(db), FactoryId = factory, WorkerCode = $"W-{t}-{w.ToString()[..4]}", FullName = "Worker", LineId = line, DepartmentId = dept });
            await db.SaveChangesAsync();
            foreach (var w in new[] { w1, w2, w3 })
                db.ShiftAssignments.Add(new WorkerShiftAssignment { WorkerId = w, FactoryId = factory, ShiftId = shift, EffectiveFrom = Today.AddDays(-30), AssignedByEmail = "test" });
            db.AttendanceSessions.Add(new AttendanceSession { WorkerId = w1, FactoryId = factory, LineId = l1, ShiftId = shift, WorkDate = Today, CheckInAt = now.AddHours(-2),
                CheckInSource = "Device", Status = "Open", IsLate = true, LateMinutes = 15, AttendanceMinutes = 120 });
            db.AttendanceSessions.Add(new AttendanceSession { WorkerId = w3, FactoryId = factory, LineId = l2, ShiftId = shift, WorkDate = Today, CheckInAt = now.AddHours(-2),
                CheckInSource = "Device", Status = "Open", AttendanceMinutes = 120 });
            db.Assignments.Add(new WorkstationAssignment { WorkerId = w1, FactoryId = factory, DepartmentId = dept, LineId = l1, WorkstationId = ws1, StartAt = now.AddDays(-1), AssignedByEmail = "test" });
            await db.SaveChangesAsync();
            return 0;
        });
        return new Rig(factory, $"A{t}", l1, l2, machine, $"S-{t[..4]}", $"PC-{t[..4]}", cam,
            await NewPerson(R.FactoryManager, factory), await NewPerson(R.DeviceAdmin, factory), await NewPerson(R.HrOfficer, factory),
            await NewPerson(R.ProductionSupervisor, factory, l2), await NewPerson(R.FactoryAdmin, factory));
    }

    private Task<Reply> Count(Rig r, int pieces, string? id = null) => Post("/api/iot/ingest/production", r.Devices.Token, new
    {
        factoryId = r.Factory, deviceId = r.CounterCode, messageId = id ?? M(), occurredAt = DateTimeOffset.UtcNow.ToString("O"), counter = new { mode = "INCREMENT", value = pieces },
    });

    private Task<Reply> Telemetry(Rig r, object fields)
    {
        var body = JsonSerializer.SerializeToElement(fields).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        body["factoryId"] = r.Factory;
        body["deviceId"] = r.SensorCode;
        body["messageId"] = M();
        body["occurredAt"] = DateTimeOffset.UtcNow.ToString("O");
        return Post("/api/iot/ingest/telemetry", r.Devices.Token, body);
    }

    private static JsonElement Kpi(JsonElement list, string id) => list.EnumerateArray().First(k => S(k, "metricId") == id);
    private static JsonElement GroupKpi(JsonElement overview, string group, string id) =>
        Kpi(overview.GetProperty("groups").EnumerateArray().First(g => S(g, "id") == group).GetProperty("kpis"), id);

    [DockerFact]
    public async Task The_overview_aggregates_real_records_and_hides_what_a_role_may_not_see()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }));
        Is(HttpStatusCode.Created, await Count(r, 7));
        var o = (await Get($"/api/analytics/overview?factoryId={r.Factory}", r.Manager.Token)).Body;
        Assert.Equal("7", S(GroupKpi(o, "production", "production.pieces"), "value"));
        Assert.Equal("2", S(GroupKpi(o, "workforce", "attendance.present"), "value"));
        Assert.Equal("66.7", S(GroupKpi(o, "workforce", "attendance.rate"), "value"));            // 2 of 3 scheduled
        Assert.Equal("3", S(GroupKpi(o, "workforce", "attendance.rate"), "denominator"));
        Assert.Equal("1", S(GroupKpi(o, "machines", "machine.state.running"), "value"));
        Assert.Contains(o.GetProperty("coverage").EnumerateArray(), c => S(c, "source") == "production_counter_events" && S(c, "records") == "1");

        // IT / Device Administrator: machines yes; production and attendance not computed.
        var d = (await Get($"/api/analytics/overview?factoryId={r.Factory}", r.Devices.Token)).Body;
        Assert.Equal("NOT_PERMITTED", S(GroupKpi(d, "production", "production.pieces"), "availability"));
        Assert.Equal("NOT_PERMITTED", S(GroupKpi(d, "workforce", "attendance.present"), "availability"));
        Assert.Null(S(GroupKpi(d, "production", "production.pieces"), "value"));
        // HR: attendance yes, production not.
        var h = (await Get($"/api/analytics/overview?factoryId={r.Factory}", r.Hr.Token)).Body;
        Assert.Equal("2", S(GroupKpi(h, "workforce", "attendance.present"), "value"));
        Assert.Equal("NOT_PERMITTED", S(GroupKpi(h, "production", "production.pieces"), "availability"));
    }

    [DockerFact]
    public async Task Production_analytics_count_each_message_once_and_never_mix_simulated_and_real_data()
    {
        var r = await NewRig();
        var id = M();
        Is(HttpStatusCode.Created, await Count(r, 5, id));
        Is(HttpStatusCode.OK, await Count(r, 5, id));                      // duplicate
        Is(HttpStatusCode.Created, await Count(r, 3));
        Is(HttpStatusCode.OK, await Post("/api/iot/production/counter-reset", r.Manager.Token, new { deviceId = await CounterId(r), reason = "Shift change reset" }));
        var p = (await Get($"/api/analytics/production?factoryId={r.Factory}", r.Manager.Token)).Body;
        Assert.Equal("8", S(Kpi(p.GetProperty("kpis"), "production.pieces"), "value"));
        Assert.Equal("1", S(Kpi(p.GetProperty("kpis"), "production.resets"), "value"));
        Assert.Contains(p.GetProperty("byLine").EnumerateArray(), x => S(x, "key") == r.Line1 && S(x, "pieces") == "8");
        Assert.Contains(p.GetProperty("byShift").EnumerateArray(), x => S(x, "key") == "ALLDAY");
        Assert.Contains(p.GetProperty("hourly").EnumerateArray(), x => S(x, "value") == "8");
        Assert.Contains("once per device + message id", S(p, "quality", "duplicateHandling"));
        Assert.Contains(p.GetProperty("warnings").EnumerateArray(), w => (w.GetString() ?? "").Contains("No active production plan"));

        var sim = (await Get($"/api/analytics/production?factoryId={r.Factory}&source=SIMULATED", r.Manager.Token)).Body;
        Assert.Equal("0", S(Kpi(sim.GetProperty("kpis"), "production.pieces"), "value"));
        Assert.Equal("SIMULATED", S(sim, "scope", "source"));
        var manual = (await Get($"/api/analytics/production?factoryId={r.Factory}&eventSource=MANUAL", r.Manager.Token)).Body;
        Assert.Equal("0", S(Kpi(manual.GetProperty("kpis"), "production.pieces"), "value"));   // device counts aren't manual
        Is(HttpStatusCode.BadRequest, await Get($"/api/analytics/production?factoryId={r.Factory}&source=BOTH", r.Manager.Token), "source_invalid");
    }

    private async Task<Guid> CounterId(Rig r) => await Rag.WithDb(Api, db => Task.FromResult(db.Devices.First(d => d.FactoryId == r.Factory && d.Code == r.CounterCode).Id));

    [DockerFact]
    public async Task Machine_analytics_separate_confirmed_downtime_from_missing_data_and_count_errors()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }));
        Is(HttpStatusCode.Created, await Telemetry(r, new { errorCode = "THREAD_BREAK" }));
        Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }));
        var m = (await Get($"/api/analytics/machines?factoryId={r.Factory}", r.Devices.Token)).Body;
        var row = m.GetProperty("machines").EnumerateArray().Single(x => S(x, "machineId") == r.Machine.ToString());
        Assert.Equal(("True", "1"), (S(row, "monitored"), S(row, "errors")));
        Assert.Contains(m.GetProperty("warnings").EnumerateArray(), w => (w.GetString() ?? "").Contains("never worker working time"));
        Assert.Equal("1", S(Kpi(m.GetProperty("kpis"), "machine.errors"), "value"));
        Assert.Equal("0", S(row, "pieces"));   // device admins see machine data, not production counts
    }

    [DockerFact]
    public async Task Workforce_analytics_keep_scheduled_checked_in_assigned_and_camera_counts_apart()
    {
        var r = await NewRig();
        var w = (await Get($"/api/analytics/workforce?factoryId={r.Factory}", r.Manager.Token)).Body;
        var day = w.GetProperty("daily").EnumerateArray().Single();
        Assert.Equal(("3", "2", "1", "1"), (S(day, "scheduled"), S(day, "present"), S(day, "absent"), S(day, "late")));
        var l1 = w.GetProperty("byLine").EnumerateArray().Single(x => S(x, "key") == r.Line1);
        Assert.Equal(("2", "1", "50"), (S(l1, "scheduledStarted"), S(l1, "scheduledPresent"), S(l1, "ratePct")));
        var concepts = w.GetProperty("concepts").EnumerateArray().ToDictionary(c => S(c, "id")!, c => S(c, "value"));
        Assert.Equal("3", concepts["scheduled"]);
        Assert.Equal("2", concepts["checked_in"]);
        Assert.Equal("1", concepts["assigned"]);
        Assert.Equal("0", concepts["verified_identity"]);
        Assert.Equal("2", S(Kpi(w.GetProperty("kpis"), "assignment.unassigned"), "value")); // two of three unassigned
        Is(HttpStatusCode.Forbidden, await Get($"/api/analytics/workforce?factoryId={r.Factory}", r.Devices.Token));
    }

    [DockerFact]
    public async Task Ai_activity_insights_are_estimates_with_uncertainty_and_identity_coverage()
    {
        var r = await NewRig();
        async Task Ev(string type, string? activity, double? conf, string track) => Is(HttpStatusCode.Created, await Post("/api/monitoring/events", r.Devices.Token, new
        {
            eventType = type, factoryId = r.Factory, idempotencyKey = M(), cameraId = r.Camera, trackId = track, confidence = conf,
            occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1), payload = new { activity = activity ?? "UNKNOWN", reason = "LOW_CONFIDENCE" },
        }));
        await Ev("ACTIVITY_CLASSIFIED", "SITTING", 0.92, "t-1");
        await Ev("ACTIVITY_CLASSIFIED", "SITTING", 0.81, "t-1");
        await Ev("ACTIVITY_CLASSIFIED", "STANDING", 0.6, "t-2");
        await Ev("ACTIVITY_UNCERTAIN", null, null, "t-2");
        var a = (await Get($"/api/analytics/activity?factoryId={r.Factory}", r.Manager.Token)).Body;
        Assert.Equal("4", S(Kpi(a.GetProperty("kpis"), "ai.predictions"), "value"));
        Assert.Equal("25", S(Kpi(a.GetProperty("kpis"), "ai.uncertain_share"), "value"));
        Assert.Equal("AI_ESTIMATE", S(Kpi(a.GetProperty("kpis"), "ai.predictions"), "kind"));
        Assert.Contains(a.GetProperty("distribution").EnumerateArray(), x => S(x, "activity") == "SITTING" && S(x, "predictions") == "2");
        Assert.Equal(("2", "0", "2"), (S(a, "identity", "tracksObserved"), S(a, "identity", "verifiedTracks"), S(a, "identity", "unverifiedTracks")));
        Assert.Contains("not proof of completed work", S(a, "disclaimer"));
        Is(HttpStatusCode.Forbidden, await Get($"/api/analytics/activity?factoryId={r.Factory}", r.Hr.Token));
    }

    [DockerFact]
    public async Task Factory_and_line_scope_are_enforced_for_pages_and_exports()
    {
        var r = await NewRig();
        var other = await NewRig();
        Is(HttpStatusCode.Created, await Count(r, 4));
        Is(HttpStatusCode.Forbidden, await Get($"/api/analytics/production?factoryId={r.Factory}", other.Manager.Token), "factory_out_of_scope");
        Assert.Equal("0", S(Kpi((await Get("/api/analytics/production", other.Manager.Token)).Body.GetProperty("kpis"), "production.pieces"), "value"));
        Is(HttpStatusCode.Forbidden, await Get($"/api/analytics/reports/production/export?format=csv&factoryId={r.Factory}", other.Manager.Token), "factory_out_of_scope");
        var otherCsv = await Get("/api/analytics/reports/production/export?format=csv", other.Manager.Token);
        Is(HttpStatusCode.OK, otherCsv);
        Assert.DoesNotContain(r.Code, otherCsv.Raw);
        // A supervisor of line 2 sees line 2 only and can't ask for line 1.
        var sup = (await Get($"/api/analytics/production?factoryId={r.Factory}", r.Supervisor2.Token)).Body;
        Assert.DoesNotContain(sup.GetProperty("byLine").EnumerateArray(), x => S(x, "key") == r.Line1);
        Is(HttpStatusCode.Forbidden, await Get($"/api/analytics/production?factoryId={r.Factory}&lineId={r.Line1}", r.Supervisor2.Token), "line_out_of_scope");
        Is(HttpStatusCode.BadRequest, await Get($"/api/analytics/production?factoryId={r.Factory}&lineId={other.Line1}", r.Manager.Token), "line_not_in_factory");
        // Report types follow the data's permission.
        Is(HttpStatusCode.Forbidden, await Get($"/api/analytics/reports/production?factoryId={r.Factory}", r.Hr.Token), "report_access_required");
        Is(HttpStatusCode.OK, await Get($"/api/analytics/reports/attendance?factoryId={r.Factory}", r.Hr.Token));
        Is(HttpStatusCode.Forbidden, await Get($"/api/analytics/reports/attendance?factoryId={r.Factory}", r.Devices.Token), "report_access_required");
    }

    [DockerFact]
    public async Task Reports_carry_scope_filters_definitions_and_are_exported_as_csv_and_pdf_with_an_audit()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Count(r, 6));
        var json = await Get($"/api/analytics/reports/daily-operations?factoryId={r.Factory}", r.Manager.Token);
        Is(HttpStatusCode.OK, json);
        Assert.Equal(S(json.Body, "from"), S(json.Body, "to"));
        Assert.NotEmpty(json.Body.GetProperty("definitions").EnumerateArray());
        Assert.Contains(json.Body.GetProperty("sections").EnumerateArray(), s => S(s, "title") == "Production");
        var weekly = (await Get($"/api/analytics/reports/weekly-operations?factoryId={r.Factory}", r.Manager.Token)).Body;
        Assert.Equal(Today.AddDays(-6).ToString("yyyy-MM-dd"), S(weekly, "from"));

        var csv = await Get($"/api/analytics/reports/production/export?format=csv&factoryId={r.Factory}&lineId={r.Line1}", r.Manager.Token);
        Is(HttpStatusCode.OK, csv);
        Assert.Equal("text/csv", csv.ContentType);
        Assert.Contains(r.Code, csv.Raw);
        Assert.Contains("Filter: Line", csv.Raw);
        Assert.Contains("Metric definitions", csv.Raw);
        Assert.Contains("REAL data", csv.Raw);
        var pdf = await Get($"/api/analytics/reports/machines/export?format=pdf&factoryId={r.Factory}", r.Devices.Token);
        Is(HttpStatusCode.OK, pdf);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf.Bytes, 0, 4));
        Is(HttpStatusCode.BadRequest, await Get($"/api/analytics/reports/production/export?format=xlsx&factoryId={r.Factory}", r.Manager.Token), "format_invalid");
        Is(HttpStatusCode.NotFound, await Get($"/api/analytics/reports/salaries?factoryId={r.Factory}", r.Manager.Token), "report_type_not_found");
        var audit = await AiSettingsTestKit.AuditText(Api, "analytics.report_");
        Assert.Contains(audit, x => x.Contains("exported as CSV") && x.Contains(r.Code));
        Assert.Contains(audit, x => x.Contains("exported as PDF"));
        Assert.Contains(audit, x => x.Contains("generated"));
    }

    [DockerFact]
    public async Task Insights_are_evidence_based_and_say_which_rules_could_not_run()
    {
        var r = await NewRig();
        for (var i = 0; i < 3; i++)
        {
            Is(HttpStatusCode.Created, await Telemetry(r, new { state = "RUNNING" }));
            Is(HttpStatusCode.Created, await Telemetry(r, new { errorCode = "NEEDLE_JAM" }));
        }
        var ins = (await Get($"/api/analytics/insights?factoryId={r.Factory}", r.Manager.Token)).Body;
        var errors = ins.GetProperty("insights").EnumerateArray().Single(x => S(x, "type") == "REPEATED_MACHINE_ERRORS");
        Assert.Equal(r.Machine.ToString(), S(errors, "machineId"));
        Assert.NotEmpty(errors.GetProperty("evidence").EnumerateArray());
        Assert.NotEmpty(errors.GetProperty("dataSources").EnumerateArray());
        Assert.Contains("never claim a root cause", S(ins, "note"));
        var hr = (await Get($"/api/analytics/insights?factoryId={r.Factory}", r.Hr.Token)).Body;
        Assert.DoesNotContain(hr.GetProperty("insights").EnumerateArray(), x => S(x, "type") == "REPEATED_MACHINE_ERRORS");
        Assert.Contains(hr.GetProperty("rules").EnumerateArray(), x => S(x, "type") == "HIGH_MACHINE_DOWNTIME" && S(x, "evaluated") == "False");
    }
}
