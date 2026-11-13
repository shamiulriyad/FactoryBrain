using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Worker management end to end, against real PostgreSQL, through the HTTP
/// API: registration and Worker ID generation (sequential, concurrent,
/// never reused), the profile and what each role sees of it, status
/// transitions, factory isolation, the optional login account, cards, the
/// photo, CSV import / export, statistics, and the migration of worker rows
/// that existed before this module.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbWorkerTests
{
    private readonly PostgresStack _stack;
    public DbWorkerTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    // ─── HTTP plumbing ─────────────────────────────────────────────────────

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw, byte[] Bytes, string? ContentType)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public string Id => Body.GetProperty("id").GetString()!;
        public string Str(string name) => Body.GetProperty(name).GetString()!;
        public bool Has(string name) => Body.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;
    }

    private async Task<Reply> Send(HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path);
        if (body is HttpContent content) req.Content = content;
        else if (body is not null) req.Content = Rag.Json(body);
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var res = await client.SendAsync(req);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var type = res.Content.Headers.ContentType?.MediaType;
        var raw = type is not null && (type.Contains("json") || type.StartsWith("text/")) ? Encoding.UTF8.GetString(bytes) : "";
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('['))
            ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw, bytes, type);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string? bearer, object body) => Send(HttpMethod.Post, path, bearer, body);
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, body);
    private Task<Reply> Patch(string path, string bearer, object body) => Send(HttpMethod.Patch, path, bearer, body);

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static string Pw() => "Pw-" + TestEnvironment.RandomSecret(14);
    private static IEnumerable<JsonElement> Items(Reply r) => r.Body.GetProperty("items").EnumerateArray();
    private static long Number(string workerCode) => long.Parse(workerCode[2..]);

    // ─── People and places ─────────────────────────────────────────────────

    public sealed record Person(Guid Id, string Email, string Token);

    /// <summary>
    /// A real account with <paramref name="role"/> in <paramref name="factory"/>
    /// (and on <paramref name="lines"/>), and a token issued from what the
    /// database says about it, exactly as sign-in would.
    /// </summary>
    private async Task<Person> NewPerson(string role, string? factory, params string[] lines)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@workers.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        if (factory is not null) user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = Guid.Parse(factory) });
        foreach (var line in lines) user.Lines.Add(new UserProductionLineAssignment { UserId = id, LineId = line });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _);
        return new Person(id, email, token);
    }

    public sealed record Site(
        string Factory, string Sewing, string Cutting, string Floor, string Line1, string Line2,
        Person Admin, Person Hr, Person Manager, Person Supervisor, Person Monitor, Person Devices);

    private async Task<string> NewLine(string factory, string name)
    {
        var id = $"wl-{Tag()}";
        await Rag.WithDb(Api, db =>
        {
            db.ProductionLines.Add(new ProductionLine { Id = id, Name = $"{name} {id}", Workstations = 20, FactoryId = Guid.Parse(factory) });
            return db.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>A factory with two departments, a floor, two lines and one person per role (the supervisor runs line 1).</summary>
    private async Task<Site> NewSite(Person super)
    {
        var f = await Post("/api/factories", super.Token, new { code = $"W{Tag()[..6]}", name = $"Worker factory {Tag()}" });
        Is(HttpStatusCode.Created, f);
        var admin = await NewPerson(R.FactoryAdmin, f.Id);
        var sewing = await Post($"/api/factories/{f.Id}/departments", admin.Token, new { code = "sew", name = "Sewing" });
        Is(HttpStatusCode.Created, sewing);
        Assert.Equal("SEW", sewing.Str("code"));
        var cutting = await Post($"/api/factories/{f.Id}/departments", admin.Token, new { code = "CUT", name = "Cutting" });
        Is(HttpStatusCode.Created, cutting);
        var floor = await Post($"/api/factories/{f.Id}/floors", admin.Token, new { code = "F2", name = "Second floor" });
        Is(HttpStatusCode.Created, floor);
        var (l1, l2) = (await NewLine(f.Id, "Line 1"), await NewLine(f.Id, "Line 2"));
        return new Site(f.Id, sewing.Id, cutting.Id, floor.Id, l1, l2, admin,
            await NewPerson(R.HrOfficer, f.Id), await NewPerson(R.FactoryManager, f.Id), await NewPerson(R.ProductionSupervisor, f.Id, l1),
            await NewPerson(R.MonitoringOperator, f.Id), await NewPerson(R.DeviceAdmin, f.Id));
    }

    private static Dictionary<string, object?> Worker(Site s, string name, string? line = null, string? department = null) => new()
    {
        ["factoryId"] = s.Factory,
        ["fullName"] = name,
        ["phone"] = "+880 1711-" + Random.Shared.Next(100000, 999999),
        ["joinedOn"] = "2024-03-01",
        ["departmentId"] = department ?? s.Sewing,
        ["designation"] = "Sewing Operator",
        ["lineId"] = line,
    };

    private async Task<Reply> Register(Site s, string name, string? line = null, string? department = null)
    {
        var r = await Post("/api/workers", s.Hr.Token, Worker(s, name, line, department));
        Is(HttpStatusCode.Created, r);
        return r;
    }

    private static MultipartFormDataContent CsvUpload(string csv, string factory, bool? dryRun)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var form = new MultipartFormDataContent { { file, "file", "workers.csv" }, { new StringContent(factory), "factoryId" } };
        if (dryRun is { } d) form.Add(new StringContent(d ? "true" : "false"), "dryRun");
        return form;
    }

    private static MultipartFormDataContent PhotoUpload(byte[] bytes, string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "photo", "photo.png" } };
    }

    // ─── Registration and Worker IDs ───────────────────────────────────────

    [DockerFact]
    public async Task Hr_registers_a_worker_and_the_server_generates_the_permanent_worker_id()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var body = Worker(s, "  Rahima Begum  ", s.Line1);
        body["employeeCode"] = "emp-" + Tag();
        body["gender"] = "female";
        body["dateOfBirth"] = "1996-05-04";
        body["email"] = "Rahima@Example.com";
        body["presentAddress"] = "House 4, Gazipur";
        body["emergencyContactName"] = "Karim";
        body["emergencyContactPhone"] = "01800000001";
        body["emergencyContactRelation"] = "Brother";
        body["floorId"] = s.Floor;
        body["workstation"] = "WS-04";
        body["employmentType"] = "probation";
        // An id sent by the client is ignored: the Worker ID is the server's.
        body["workerCode"] = "W-999999";
        body["status"] = "Terminated";

        var w = await Post("/api/workers", s.Hr.Token, body);
        Is(HttpStatusCode.Created, w);
        Assert.Matches("^W-[0-9]{3,}$", w.Str("workerCode"));
        Assert.NotEqual("W-999999", w.Str("workerCode"));
        Assert.Equal("Rahima Begum", w.Str("fullName"));
        Assert.Equal("Active", w.Str("status"));
        Assert.True(w.Body.GetProperty("isActive").GetBoolean());
        Assert.Equal("Probation", w.Str("employmentType"));
        Assert.Equal("Female", w.Str("gender"));
        Assert.Equal("rahima@example.com", w.Str("email"));
        Assert.Equal(((string)body["employeeCode"]!).ToUpperInvariant(), w.Str("employeeCode"));
        Assert.Equal("Sewing", w.Body.GetProperty("department").GetProperty("name").GetString());
        Assert.Equal("Second floor", w.Body.GetProperty("floor").GetProperty("name").GetString());
        Assert.Equal(s.Line1, w.Body.GetProperty("line").GetProperty("id").GetString());
        Assert.Equal("WS-04", w.Str("workstation"));
        Assert.False(w.Body.GetProperty("hasAccount").GetBoolean());
        Assert.False(w.Body.GetProperty("hasPhoto").GetBoolean());
        Assert.Equal("full", w.Str("detail"));

        // The worker is addressable by id and by Worker ID, in any case.
        Assert.Equal(w.Id, (await Get($"/api/workers/{w.Str("workerCode")}", s.Hr.Token)).Id);
        Assert.Equal(w.Id, (await Get($"/api/workers/{w.Str("workerCode").ToLowerInvariant()}", s.Hr.Token)).Id);

        // History starts at registration, and the change is audited without copying personal values.
        var profile = await Get($"/api/workers/{w.Id}/profile", s.Hr.Token);
        Is(HttpStatusCode.OK, profile);
        var first = Assert.Single(profile.Body.GetProperty("statusHistory").EnumerateArray());
        Assert.Equal("Active", first.GetProperty("toStatus").GetString());
        Assert.False(first.TryGetProperty("fromStatus", out _));
        var audit = await Get($"/api/audit?entityType=worker&entityId={w.Id}", s.Admin.Token);
        var created = Assert.Single(audit.Body.EnumerateArray());
        Assert.Equal("worker.created", created.GetProperty("action").GetString());
        Assert.Equal(s.Hr.Email, created.GetProperty("actorEmail").GetString());
        Assert.DoesNotContain("1711", audit.Raw);
        Assert.DoesNotContain("Gazipur", audit.Raw);
    }

    [DockerFact]
    public async Task Registration_is_validated_and_placement_must_belong_to_the_factory()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var (a, b) = (await NewSite(super), await NewSite(super));

        var empty = await Post("/api/workers", a.Hr.Token, new { factoryId = a.Factory, fullName = "" });
        Is(HttpStatusCode.BadRequest, empty);
        var errors = empty.Body.GetProperty("errors");
        foreach (var field in new[] { "fullName", "phone", "joinedOn", "departmentId", "designation" })
            Assert.True(errors.TryGetProperty(field, out _), $"no error for {field}: {empty.Raw}");

        async Task Refused(string field, object? value, HttpStatusCode status, string? code)
        {
            var body = Worker(a, "Placement test");
            body[field] = value;
            Is(status, await Post("/api/workers", a.Hr.Token, body), code);
        }
        await Refused("departmentId", b.Sewing, HttpStatusCode.BadRequest, "department_not_in_factory");
        await Refused("departmentId", Guid.NewGuid().ToString(), HttpStatusCode.BadRequest, "department_not_in_factory");
        await Refused("floorId", b.Floor, HttpStatusCode.BadRequest, "floor_not_in_factory");
        await Refused("lineId", b.Line1, HttpStatusCode.BadRequest, "line_not_in_factory");
        await Refused("phone", "not a phone", HttpStatusCode.BadRequest, null);
        await Refused("joinedOn", "2099-01-01", HttpStatusCode.BadRequest, null);
        await Refused("employmentType", "Forever", HttpStatusCode.BadRequest, null);

        // A deactivated department can't take new workers; existing ones keep it and can still be edited.
        var kept = await Register(a, "Keeps department", department: a.Cutting);
        Is(HttpStatusCode.OK, await Put($"/api/factories/{a.Factory}/departments/{a.Cutting}", a.Admin.Token, new { name = "Cutting", isActive = false }));
        await Refused("departmentId", a.Cutting, HttpStatusCode.BadRequest, "department_inactive");
        var edit = Worker(a, "Keeps department renamed", department: a.Cutting);
        Is(HttpStatusCode.OK, await Put($"/api/workers/{kept.Id}", a.Hr.Token, edit));

        // The factory's own employee number is unique inside the factory, and free again in another one.
        var code = "EMP-" + Tag();
        var withCode = Worker(a, "Has number");
        withCode["employeeCode"] = code;
        Is(HttpStatusCode.Created, await Post("/api/workers", a.Hr.Token, withCode));
        withCode["employeeCode"] = code.ToLowerInvariant();
        Is(HttpStatusCode.Conflict, await Post("/api/workers", a.Hr.Token, withCode), "employee_code_taken");
        var other = Worker(b, "Same number elsewhere");
        other["employeeCode"] = code;
        Is(HttpStatusCode.Created, await Post("/api/workers", b.Hr.Token, other));
    }

    [DockerFact]
    public async Task Worker_ids_are_sequential_unique_across_factories_and_never_reused()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var (a, b) = (await NewSite(super), await NewSite(super));

        var w1 = await Register(a, "First");
        var w2 = await Register(b, "Second, other factory");
        var w3 = await Register(a, "Third");
        var numbers = new[] { w1, w2, w3 }.Select(w => Number(w.Str("workerCode"))).ToArray();
        Assert.True(numbers[0] < numbers[1] && numbers[1] < numbers[2], string.Join(",", numbers));

        // Terminating a worker frees nothing: the id stays theirs, and the next registration gets a new one.
        Is(HttpStatusCode.OK, await Patch($"/api/workers/{w3.Id}/status", a.Hr.Token, new { status = "Terminated", reason = "absconded" }));
        var w4 = await Register(a, "Fourth");
        Assert.True(Number(w4.Str("workerCode")) > numbers[2]);
        Assert.Equal(w3.Id, (await Get($"/api/workers/{w3.Str("workerCode")}", a.Hr.Token)).Id);

        // A refused registration may consume a number, but never hands one out twice.
        var bad = Worker(a, "Refused");
        bad["departmentId"] = b.Sewing;
        Is(HttpStatusCode.BadRequest, await Post("/api/workers", a.Hr.Token, bad));
        var w5 = await Register(a, "Fifth");
        Assert.True(Number(w5.Str("workerCode")) > Number(w4.Str("workerCode")));

        // The database itself refuses a second row with an existing Worker ID or number.
        var taken = w1.Str("workerCode");
        foreach (var (number, code) in new[] { (9_000_000_000L + Random.Shared.Next(), taken), (Number(taken), "W-" + (8_000_000_000L + Random.Shared.Next())) })
        {
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Rag.WithDb(Api, db =>
            {
                db.Workers.Add(new WorkerProfile { WorkerNumber = number, WorkerCode = code, FactoryId = Guid.Parse(a.Factory), FullName = "Duplicate" });
                return db.SaveChangesAsync();
            }));
            Assert.Equal("23505", Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        }

        // There is no way to delete a worker through the API.
        var delete = await Send(HttpMethod.Delete, $"/api/workers/{w1.Id}", a.Admin.Token);
        Assert.True(delete.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, delete.Status.ToString());
        Is(HttpStatusCode.OK, await Get($"/api/workers/{w1.Id}", a.Hr.Token));
    }

    [DockerFact]
    public async Task Concurrent_registrations_never_share_a_worker_id()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var (a, b) = (await NewSite(super), await NewSite(super));

        const int perFactory = 20;
        var replies = await Task.WhenAll(Enumerable.Range(0, perFactory * 2).Select(i =>
        {
            var site = i % 2 == 0 ? a : b;
            return Post("/api/workers", site.Hr.Token, Worker(site, $"Concurrent {i}"));
        }));

        Assert.All(replies, r => Is(HttpStatusCode.Created, r));
        var codes = replies.Select(r => r.Str("workerCode")).ToList();
        Assert.Equal(perFactory * 2, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^W-[0-9]{3,}$", c));

        // What the database holds agrees: one row per id, numbers and codes in step.
        var ids = replies.Select(r => Guid.Parse(r.Id)).ToList();
        var rows = await Rag.WithDb(Api, db => db.Workers.Where(w => ids.Contains(w.Id)).Select(w => new { w.WorkerNumber, w.WorkerCode }).ToListAsync());
        Assert.Equal(perFactory * 2, rows.Select(r => r.WorkerNumber).Distinct().Count());
        Assert.All(rows, r => Assert.Equal(r.WorkerNumber, Number(r.WorkerCode)));
        Assert.Equal(perFactory, (await Get($"/api/workers?factoryId={a.Factory}&pageSize=100", a.Hr.Token)).Body.GetProperty("total").GetInt32());

        // Two imports and single registrations at the same moment don't collide either.
        string File(string prefix) => "full_name,phone,joining_date,department,designation\n" +
            string.Concat(Enumerable.Range(0, 15).Select(i => $"{prefix} {i},01712{i:D6},2024-03-01,Sewing,Operator\n"));
        var mixed = await Task.WhenAll(
            Post("/api/workers/import", a.Hr.Token, CsvUpload(File("Import A"), a.Factory, false)),
            Post("/api/workers/import", b.Hr.Token, CsvUpload(File("Import B"), b.Factory, false)),
            Post("/api/workers", a.Hr.Token, Worker(a, "Beside the imports")));
        Is(HttpStatusCode.Created, mixed[0]);
        Is(HttpStatusCode.Created, mixed[1]);
        Is(HttpStatusCode.Created, mixed[2]);
        var imported = mixed.Take(2).SelectMany(m => m.Body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("workerCode").GetString()!))
            .Append(mixed[2].Str("workerCode")).ToList();
        Assert.Equal(31, imported.Distinct().Count());
        Assert.Empty(imported.Intersect(codes));
    }

    // ─── Profile and what each role sees ───────────────────────────────────

    [DockerFact]
    public async Task Each_role_sees_only_the_part_of_a_worker_it_needs()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var body = Worker(s, "Tiered Worker", s.Line1);
        body["employeeCode"] = "EMP-" + Tag();
        body["grade"] = "G4";
        body["email"] = "tiered@example.com";
        body["presentAddress"] = "Road 9";
        body["emergencyContactName"] = "Contact";
        var w = await Post("/api/workers", s.Hr.Token, body);
        Is(HttpStatusCode.Created, w);
        var other = await Register(s, "On the other line", s.Line2);

        string[] employment = { "employeeCode", "joinedOn", "grade", "employmentType", "hasAccount", "createdAt" };
        string[] personal = { "phone", "email", "presentAddress", "emergencyContactName" };
        async Task Sees(Person who, string detail, bool seesEmployment, bool seesPersonal)
        {
            var r = await Get($"/api/workers/{w.Id}", who.Token);
            Is(HttpStatusCode.OK, r);
            Assert.Equal(detail, r.Str("detail"));
            foreach (var basic in new[] { "workerCode", "fullName", "status", "designation", "department", "line" }) Assert.True(r.Has(basic), basic);
            Assert.All(employment, f => Assert.Equal(seesEmployment, r.Has(f)));
            Assert.All(personal, f => Assert.Equal(seesPersonal, r.Has(f)));
            // The list and the profile follow the same rule.
            var listed = Items(await Get($"/api/workers?search={w.Str("workerCode")}", who.Token)).Single(x => x.GetProperty("id").GetString() == w.Id);
            Assert.Equal(seesPersonal, listed.TryGetProperty("phone", out _));
            Assert.Equal(seesEmployment, listed.TryGetProperty("joinedOn", out _));
            var profile = await Get($"/api/workers/{w.Id}/profile", who.Token);
            Is(HttpStatusCode.OK, profile);
            Assert.Equal(seesPersonal, profile.Body.GetProperty("worker").TryGetProperty("phone", out _));
            Assert.Equal(seesEmployment, profile.Has("statusHistory"));
            Assert.Equal(seesPersonal, profile.Has("cards"));
        }

        await Sees(super, "full", true, true);                 // Super Admin: everything, everywhere
        await Sees(s.Admin, "full", true, true);               // Factory Admin
        await Sees(s.Hr, "full", true, true);                  // HR
        await Sees(s.Manager, "employment", true, false);      // Factory Manager: employment, no personal contact details
        await Sees(s.Monitor, "basic", false, false);          // Monitoring Operator: what monitoring needs
        await Sees(s.Supervisor, "basic", false, false);       // Supervisor: workers of their line

        // A supervisor reaches only their own line's workers.
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{other.Id}", s.Supervisor.Token), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{other.Id}/profile", s.Supervisor.Token));
        Assert.Equal(new[] { w.Id }, Items(await Get("/api/workers", s.Supervisor.Token)).Select(x => x.GetProperty("id").GetString()));
        // Searching by a field they can't see finds nothing.
        Assert.Empty(Items(await Get($"/api/workers?search={body["employeeCode"]}", s.Monitor.Token)));
        Assert.Single(Items(await Get($"/api/workers?search={body["employeeCode"]}", s.Manager.Token)));

        // IT / device administration has no access to worker records at all.
        foreach (var path in new[] { "/api/workers", $"/api/workers/{w.Id}", $"/api/workers/{w.Id}/profile", "/api/workers/statistics", "/api/workers/export", $"/api/workers/{w.Id}/photo", $"/api/workers/{w.Id}/cards" })
            Is(HttpStatusCode.Forbidden, await Get(path, s.Devices.Token), "permission_denied");

        // The profile: capabilities per caller, and placeholders that hold no invented data.
        var hrProfile = await Get($"/api/workers/{w.Str("workerCode")}/profile", s.Hr.Token);
        var can = hrProfile.Body.GetProperty("can");
        Assert.True(can.GetProperty("edit").GetBoolean() && can.GetProperty("changeStatus").GetBoolean() && can.GetProperty("manageCards").GetBoolean());
        Assert.False(can.GetProperty("manageAccount").GetBoolean()); // login accounts are the Factory Admin's
        Assert.Equal(new[] { "Inactive", "Suspended", "Resigned", "Terminated" }, can.GetProperty("allowedStatuses").EnumerateArray().Select(x => x.GetString()));
        Assert.True((await Get($"/api/workers/{w.Id}/profile", s.Admin.Token)).Body.GetProperty("can").GetProperty("manageAccount").GetBoolean());
        Assert.False((await Get($"/api/workers/{w.Id}/profile", s.Manager.Token)).Body.GetProperty("can").GetProperty("edit").GetBoolean());
        Assert.False(hrProfile.Has("machine"));
        Assert.Equal(s.Factory, hrProfile.Body.GetProperty("factory").GetProperty("id").GetString());
        var sections = hrProfile.Body.GetProperty("activity").EnumerateArray().ToList();
        Assert.Equal(new[] { "attendance", "shifts", "workstationAssignments", "activityTimeline", "breaks", "overtime", "monitoringAlerts" },
            sections.Select(x => x.GetProperty("key").GetString()));
        Assert.All(sections, x =>
        {
            Assert.False(x.GetProperty("available").GetBoolean());
            Assert.Equal(0, x.GetProperty("items").GetArrayLength());
        });
    }

    [DockerFact]
    public async Task Hr_edits_a_worker_and_other_roles_cannot()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var w = await Register(s, "Before Edit", s.Line1);
        Is(HttpStatusCode.OK, await Put($"/api/workers/{w.Id}/workstation", s.Supervisor.Token, new { lineId = s.Line1, workstation = "WS-09" }));

        var edit = Worker(s, "After Edit", s.Line2, s.Cutting);
        edit["phone"] = "01999000111";
        edit["grade"] = "G5";
        edit["floorId"] = s.Floor;
        var updated = await Put($"/api/workers/{w.Id}", s.Hr.Token, edit);
        Is(HttpStatusCode.OK, updated);
        Assert.Equal("After Edit", updated.Str("fullName"));
        Assert.Equal("Cutting", updated.Body.GetProperty("department").GetProperty("name").GetString());
        Assert.Equal(s.Line2, updated.Str("lineId"));
        Assert.False(updated.Has("workstation")); // the workstation belonged to the old line
        Assert.Equal(w.Str("workerCode"), updated.Str("workerCode")); // the Worker ID never changes
        Assert.Equal(s.Factory, updated.Str("factoryId"));

        // The audit entry names the fields, not their values.
        var audit = await Get($"/api/audit?entityType=worker&entityId={w.Id}&action=worker.updated", s.Admin.Token);
        var reason = Assert.Single(audit.Body.EnumerateArray()).GetProperty("reason").GetString()!;
        foreach (var field in new[] { "name", "phone", "grade", "department", "floor", "line" }) Assert.Contains(field, reason);
        Assert.DoesNotContain("01999000111", audit.Raw);

        // An edit that changes nothing writes no audit entry.
        Is(HttpStatusCode.OK, await Put($"/api/workers/{w.Id}", s.Hr.Token, edit));
        Assert.Single((await Get($"/api/audit?entityType=worker&entityId={w.Id}&action=worker.updated", s.Admin.Token)).Body.EnumerateArray());

        foreach (var who in new[] { s.Manager, s.Supervisor, s.Monitor, s.Devices })
        {
            Is(HttpStatusCode.Forbidden, await Put($"/api/workers/{w.Id}", who.Token, edit), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Post("/api/workers", who.Token, Worker(s, "Nope")), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Patch($"/api/workers/{w.Id}/status", who.Token, new { status = "Inactive" }), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Post("/api/workers/import", who.Token, CsvUpload("x", s.Factory, true)), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Post($"/api/workers/{w.Id}/cards", who.Token, new { cardUid = "04A1B2C3" }), "permission_denied");
        }
        Assert.Equal("After Edit", (await Get($"/api/workers/{w.Id}", s.Hr.Token)).Str("fullName"));
    }

    // ─── Status ────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Status_changes_follow_the_transition_rules_and_are_recorded()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var w = await Register(s, "Status Worker");
        var path = $"/api/workers/{w.Id}/status";

        Is(HttpStatusCode.BadRequest, await Patch(path, s.Hr.Token, new { status = "Deleted" }));
        Is(HttpStatusCode.Conflict, await Patch(path, s.Hr.Token, new { status = "Active" }), "status_unchanged");
        Is(HttpStatusCode.BadRequest, await Patch(path, s.Hr.Token, new { status = "Suspended" }), "status_reason_required");

        var suspended = await Patch(path, s.Hr.Token, new { status = "suspended", reason = "Pending inquiry", effectiveOn = "2026-11-01" });
        Is(HttpStatusCode.OK, suspended);
        Assert.Equal("Suspended", suspended.Str("status"));
        Assert.False(suspended.Body.GetProperty("isActive").GetBoolean());
        Assert.Equal("Pending inquiry", suspended.Str("statusReason"));

        Is(HttpStatusCode.Conflict, await Patch(path, s.Hr.Token, new { status = "Inactive" }), "status_transition_not_allowed");
        Is(HttpStatusCode.OK, await Patch(path, s.Hr.Token, new { status = "Active" }));
        Is(HttpStatusCode.OK, await Patch(path, s.Hr.Token, new { status = "Inactive" }));
        Is(HttpStatusCode.OK, await Patch(path, s.Hr.Token, new { status = "Resigned", reason = "Left for family reasons" }));
        Is(HttpStatusCode.Conflict, await Patch(path, s.Hr.Token, new { status = "Terminated", reason = "x" }), "status_transition_not_allowed");
        Is(HttpStatusCode.Conflict, await Patch(path, s.Hr.Token, new { status = "Suspended", reason = "x" }), "status_transition_not_allowed");
        Is(HttpStatusCode.BadRequest, await Patch(path, s.Hr.Token, new { status = "Active" }), "status_reason_required");
        Is(HttpStatusCode.OK, await Patch(path, s.Hr.Token, new { status = "Active", reason = "Re-hired" }));
        Is(HttpStatusCode.OK, await Patch(path, s.Hr.Token, new { status = "Terminated", reason = "Misconduct" }));

        // The profile tells the client what may come next.
        var profile = await Get($"/api/workers/{w.Id}/profile", s.Hr.Token);
        Assert.Equal(new[] { "Active" }, profile.Body.GetProperty("can").GetProperty("allowedStatuses").EnumerateArray().Select(x => x.GetString()));

        // Newest first: every change with who, why and from what.
        var history = profile.Body.GetProperty("statusHistory").EnumerateArray().ToList();
        Assert.Equal(new[] { "Terminated", "Active", "Resigned", "Inactive", "Active", "Suspended", "Active" }, history.Select(h => h.GetProperty("toStatus").GetString()));
        Assert.Equal("Active", history[0].GetProperty("fromStatus").GetString());
        Assert.Equal("Misconduct", history[0].GetProperty("reason").GetString());
        Assert.Equal(s.Hr.Email, history[0].GetProperty("changedByEmail").GetString());
        Assert.Equal("2026-11-01", history[5].GetProperty("effectiveOn").GetString());

        var audit = await Get($"/api/audit?entityType=worker&entityId={w.Id}&action=worker.status_changed&limit=50", s.Admin.Token);
        var entries = audit.Body.EnumerateArray().ToList();
        Assert.Equal(6, entries.Count);
        Assert.Equal(("Active", "Terminated"), (entries[0].GetProperty("fromState").GetString(), entries[0].GetProperty("toState").GetString()));

        // Refused changes left nothing behind.
        Assert.Equal(7, await Rag.WithDb(Api, db => db.WorkerStatusHistory.CountAsync(h => h.WorkerId == Guid.Parse(w.Id))));
    }

    [DockerFact]
    public async Task Two_status_changes_at_the_same_moment_cannot_both_follow_the_same_status()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var w = await Register(s, "Raced Worker");
        var path = $"/api/workers/{w.Id}/status";

        // Resigned and Terminated are both allowed after Active, but not after each other.
        var results = await Task.WhenAll(
            Patch(path, s.Hr.Token, new { status = "Resigned", reason = "resignation letter" }),
            Patch(path, s.Admin.Token, new { status = "Terminated", reason = "termination letter" }));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Is(HttpStatusCode.Conflict, results.Single(r => r.Status != HttpStatusCode.OK), "status_transition_not_allowed");

        var final = (await Get($"/api/workers/{w.Id}", s.Hr.Token)).Str("status");
        Assert.Equal(results.Single(r => r.Status == HttpStatusCode.OK).Str("status"), final);
        var history = await Rag.WithDb(Api, db => db.WorkerStatusHistory.Where(h => h.WorkerId == Guid.Parse(w.Id)).OrderBy(h => h.Id).Select(h => h.ToStatus).ToListAsync());
        Assert.Equal(new[] { "Active", final }, history);
    }

    [DockerFact]
    public async Task Workers_who_leave_are_kept_with_their_history()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var w = await Register(s, "Leaving Worker", s.Line1);
        var id = Guid.Parse(w.Id);
        var card = await Post($"/api/workers/{w.Id}/cards", s.Hr.Token, new { cardUid = "04:" + Tag(), cardType = "RFID", label = "Blue 17" });
        Is(HttpStatusCode.Created, card);

        Is(HttpStatusCode.OK, await Patch($"/api/workers/{w.Id}/status", s.Hr.Token, new { status = "Resigned", reason = "Moved abroad", effectiveOn = "2026-10-31" }));

        // Still there: by id, by Worker ID, in the directory under its status, in the statistics, with its history.
        var after = await Get($"/api/workers/{w.Str("workerCode")}", s.Hr.Token);
        Is(HttpStatusCode.OK, after);
        Assert.Equal(("Resigned", "Leaving Worker", s.Line1), (after.Str("status"), after.Str("fullName"), after.Str("lineId")));
        Assert.Contains(Items(await Get($"/api/workers?status=Resigned&factoryId={s.Factory}", s.Hr.Token)), x => x.GetProperty("id").GetString() == w.Id);
        Assert.DoesNotContain(Items(await Get($"/api/workers?status=Active&factoryId={s.Factory}", s.Hr.Token)), x => x.GetProperty("id").GetString() == w.Id);
        Assert.Equal(1, (await Get($"/api/workers/statistics?factoryId={s.Factory}", s.Hr.Token)).Body.GetProperty("resigned").GetInt32());

        // The card stops working the moment the worker leaves, and its record is kept.
        var cards = await Get($"/api/workers/{w.Id}/cards", s.Hr.Token);
        var kept = Assert.Single(cards.Body.EnumerateArray());
        Assert.False(kept.GetProperty("isActive").GetBoolean());
        Assert.Equal("WorkerLeft", kept.GetProperty("deactivationReason").GetString());

        // No new credential, workstation or login for someone who has left.
        Is(HttpStatusCode.Conflict, await Post($"/api/workers/{w.Id}/cards", s.Hr.Token, new { cardUid = "04" + Tag() }), "worker_separated");
        Is(HttpStatusCode.Conflict, await Put($"/api/workers/{w.Id}/workstation", s.Hr.Token, new { lineId = s.Line1, workstation = "WS-01" }), "worker_separated");
        Is(HttpStatusCode.Conflict, await Post($"/api/workers/{w.Id}/account", s.Admin.Token, new { email = $"left-{Tag()}@workers.test", temporaryPassword = Pw() }), "worker_separated");

        // The database refuses to delete a worker that history points at, or a department that workers point at.
        var workerDelete = await Assert.ThrowsAsync<PostgresException>(() => Rag.WithDb(Api, db => db.Workers.Where(x => x.Id == id).ExecuteDeleteAsync()));
        Assert.Equal("23503", workerDelete.SqlState);
        var departmentDelete = await Assert.ThrowsAsync<PostgresException>(() => Rag.WithDb(Api, db => db.Departments.Where(x => x.Id == Guid.Parse(s.Sewing)).ExecuteDeleteAsync()));
        Assert.Equal("23503", departmentDelete.SqlState);

        // Re-hiring keeps the same Worker ID and adds to the same history.
        var back = await Patch($"/api/workers/{w.Id}/status", s.Hr.Token, new { status = "Active", reason = "Re-hired" });
        Is(HttpStatusCode.OK, back);
        Assert.Equal(w.Str("workerCode"), back.Str("workerCode"));
        Assert.Equal(3, (await Get($"/api/workers/{w.Id}/profile", s.Hr.Token)).Body.GetProperty("statusHistory").GetArrayLength());
    }

    // ─── Factory isolation ─────────────────────────────────────────────────

    [DockerFact]
    public async Task Factories_never_see_or_change_each_others_workers()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var (a, b) = (await NewSite(super), await NewSite(super));
        var mine = await Register(a, "Worker of A", a.Line1);
        var theirs = await Register(b, "Worker of B", b.Line1);
        var card = await Post($"/api/workers/{theirs.Id}/cards", b.Hr.Token, new { cardUid = "0A" + Tag() });
        Is(HttpStatusCode.Created, card);

        foreach (var who in new[] { a.Admin, a.Hr, a.Manager, a.Monitor })
        {
            foreach (var target in new[] { theirs.Id, theirs.Str("workerCode") })
            {
                Is(HttpStatusCode.NotFound, await Get($"/api/workers/{target}", who.Token), "worker_not_found");
                Is(HttpStatusCode.NotFound, await Get($"/api/workers/{target}/profile", who.Token), "worker_not_found");
                Is(HttpStatusCode.NotFound, await Get($"/api/workers/{target}/photo", who.Token), "worker_not_found");
            }
            Assert.DoesNotContain(Items(await Get("/api/workers?pageSize=100", who.Token)), x => x.GetProperty("factoryId").GetString() == b.Factory);
            Assert.Equal(0, (await Get($"/api/workers?factoryId={b.Factory}", who.Token)).Body.GetProperty("total").GetInt32());
            Assert.Empty(Items(await Get($"/api/workers?search={Uri.EscapeDataString("Worker of B")}", who.Token)));
            Assert.Equal(0, (await Get($"/api/workers/statistics?factoryId={b.Factory}", who.Token)).Body.GetProperty("total").GetInt32());
            var stats = await Get("/api/workers/statistics", who.Token);
            Assert.Equal(new[] { a.Factory }, stats.Body.GetProperty("byFactory").EnumerateArray().Select(x => x.GetProperty("key").GetString()));
            Is(HttpStatusCode.NotFound, await Get($"/api/factories/{b.Factory}/departments", who.Token), "factory_not_found");
            Is(HttpStatusCode.NotFound, await Get($"/api/factories/{b.Factory}/floors", who.Token), "factory_not_found");
        }

        // Every write is refused, by id or by Worker ID, and nothing changes.
        var edit = Worker(b, "Hijacked");
        Is(HttpStatusCode.NotFound, await Put($"/api/workers/{theirs.Id}", a.Hr.Token, edit), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Patch($"/api/workers/{theirs.Str("workerCode")}/status", a.Hr.Token, new { status = "Terminated", reason = "x" }), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Put($"/api/workers/{theirs.Id}/workstation", a.Hr.Token, new { lineId = b.Line1 }), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Put($"/api/workers/{theirs.Id}/photo", a.Hr.Token, PhotoUpload(TestImages.Png(8, 8))), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{theirs.Id}/cards", a.Hr.Token), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Post($"/api/workers/{theirs.Id}/cards", a.Hr.Token, new { cardUid = "0B" + Tag() }), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Patch($"/api/workers/{theirs.Id}/cards/{card.Id}", a.Hr.Token, new { reason = "Lost" }), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Put($"/api/workers/{theirs.Id}/account", a.Admin.Token, new { userId = a.Hr.Id }), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Post($"/api/workers/{theirs.Id}/account", a.Admin.Token, new { email = $"x-{Tag()}@workers.test", temporaryPassword = Pw() }), "worker_not_found");
        Is(HttpStatusCode.Forbidden, await Post("/api/workers", a.Hr.Token, Worker(b, "Planted")), "factory_out_of_scope");
        Is(HttpStatusCode.Forbidden, await Post("/api/workers/import", a.Hr.Token, CsvUpload("full_name,phone,joining_date,department,designation\nX,01711000000,2024-01-01,Sewing,Op\n", b.Factory, false)), "factory_out_of_scope");
        Is(HttpStatusCode.NotFound, await Post($"/api/factories/{b.Factory}/departments", a.Admin.Token, new { code = "X", name = "Planted" }), "factory_not_found");
        Is(HttpStatusCode.NotFound, await Put($"/api/factories/{b.Factory}/departments/{b.Sewing}", a.Admin.Token, new { name = "Renamed", isActive = false }), "factory_not_found");
        // A's own worker can't be placed in B's department, floor or line.
        foreach (var (field, value) in new[] { ("departmentId", b.Sewing), ("floorId", b.Floor), ("lineId", b.Line1) })
        {
            var move = Worker(a, "Worker of A", a.Line1);
            move[field] = value;
            Assert.Equal(HttpStatusCode.BadRequest, (await Put($"/api/workers/{mine.Id}", a.Hr.Token, move)).Status);
        }

        var untouched = await Get($"/api/workers/{theirs.Id}", b.Hr.Token);
        Assert.Equal(("Worker of B", "Active", false), (untouched.Str("fullName"), untouched.Str("status"), untouched.Body.GetProperty("hasPhoto").GetBoolean()));
        Assert.Equal(1, (await Get($"/api/workers?factoryId={b.Factory}", b.Hr.Token)).Body.GetProperty("total").GetInt32());

        // A's export contains only A; the Super Admin reaches both.
        var export = await Get("/api/workers/export", a.Hr.Token);
        Is(HttpStatusCode.OK, export);
        Assert.Contains("Worker of A", export.Raw);
        Assert.DoesNotContain("Worker of B", export.Raw);
        Is(HttpStatusCode.OK, await Get($"/api/workers/{theirs.Id}", super.Token));
        Is(HttpStatusCode.OK, await Get($"/api/workers/{mine.Id}", super.Token));
        var both = (await Get("/api/workers/statistics", super.Token)).Body.GetProperty("byFactory").EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList();
        Assert.Contains(a.Factory, both);
        Assert.Contains(b.Factory, both);
    }

    // ─── Login accounts ────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_worker_needs_no_login_and_can_be_given_one_restricted_to_their_own_record()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var w = await Register(s, "Account Worker", s.Line1);
        var other = await Register(s, "Someone Else", s.Line1);
        var usersBefore = await Rag.WithDb(Api, db => db.Users.CountAsync());

        // Registration creates no login.
        Assert.False(w.Body.GetProperty("hasAccount").GetBoolean());
        Assert.Equal(usersBefore, await Rag.WithDb(Api, db => db.Users.CountAsync()));
        Assert.False((await Get($"/api/workers/{w.Id}/profile", s.Admin.Token)).Has("account"));

        // HR manages worker records, not logins.
        var email = $"worker-{Tag()}@workers.test";
        var temp = Pw();
        Is(HttpStatusCode.Forbidden, await Post($"/api/workers/{w.Id}/account", s.Hr.Token, new { email, temporaryPassword = temp }), "permission_denied");
        Is(HttpStatusCode.BadRequest, await Post($"/api/workers/{w.Id}/account", s.Admin.Token, new { email = "nope", temporaryPassword = temp }));
        Is(HttpStatusCode.BadRequest, await Post($"/api/workers/{w.Id}/account", s.Admin.Token, new { email, temporaryPassword = "short" }));

        var created = await Post($"/api/workers/{w.Id}/account", s.Admin.Token, new { email, temporaryPassword = temp });
        Is(HttpStatusCode.Created, created);
        Assert.True(created.Body.GetProperty("hasAccount").GetBoolean());
        Assert.DoesNotContain(temp, created.Raw);
        Is(HttpStatusCode.Conflict, await Post($"/api/workers/{w.Id}/account", s.Admin.Token, new { email = $"second-{Tag()}@workers.test", temporaryPassword = Pw() }), "worker_has_account");
        Is(HttpStatusCode.Conflict, await Post($"/api/workers/{other.Id}/account", s.Admin.Token, new { email, temporaryPassword = Pw() }), "email_taken");
        Assert.False((await Get($"/api/workers/{other.Id}", s.Hr.Token)).Body.GetProperty("hasAccount").GetBoolean());

        // The account: Worker role only, this factory, temporary password, and no copy of the worker's personal data.
        var account = (await Get($"/api/workers/{w.Id}/profile", s.Admin.Token)).Body.GetProperty("account");
        Assert.Equal(email, account.GetProperty("email").GetString());
        Assert.Equal(new[] { R.Worker }, account.GetProperty("roles").EnumerateArray().Select(x => x.GetString()));
        var user = await Rag.WithDb(Api, db => db.Users.Include(u => u.Factories).FirstAsync(u => u.Email == email));
        Assert.True(user.MustChangePassword);
        Assert.Null(user.FullName);
        Assert.Null(user.Phone);
        Assert.Equal(Guid.Parse(s.Factory), Assert.Single(user.Factories).FactoryId);

        // The worker signs in, must replace the temporary password, then reaches exactly their own record.
        var login = await Post("/api/auth/login", null, new { email, password = temp });
        Is(HttpStatusCode.OK, login);
        var first = login.Str("accessToken");
        Is(HttpStatusCode.Forbidden, await Get("/api/workers/me", first), "password_change_required");
        var password = Pw();
        Is(HttpStatusCode.NoContent, await Post("/api/account/password", first, new { currentPassword = temp, newPassword = password }));
        var me = (await Post("/api/auth/login", null, new { email, password })).Str("accessToken");

        var mine = await Get("/api/workers/me", me);
        Is(HttpStatusCode.OK, mine);
        Assert.Equal((w.Id, "full"), (mine.Id, mine.Str("detail")));
        Assert.True(mine.Has("phone"));
        Is(HttpStatusCode.OK, await Get($"/api/workers/{w.Id}", me));
        var own = await Get($"/api/workers/{w.Str("workerCode")}/profile", me);
        Is(HttpStatusCode.OK, own);
        Assert.False(own.Body.GetProperty("can").GetProperty("edit").GetBoolean());
        Assert.Equal(w.Str("workerCode"), (await Get("/api/account/profile", me)).Body.GetProperty("worker").GetProperty("workerCode").GetString());

        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{other.Id}", me), "worker_not_found");
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{other.Id}/profile", me));
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{other.Id}/photo", me));
        foreach (var path in new[] { "/api/workers", "/api/workers/statistics", "/api/workers/export", $"/api/workers/{w.Id}/cards", "/api/users", "/api/audit" })
            Is(HttpStatusCode.Forbidden, await Get(path, me), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Put($"/api/workers/{w.Id}", me, Worker(s, "Self promoted")), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Patch($"/api/workers/{w.Id}/status", me, new { status = "Inactive" }), "permission_denied");

        // Unlinking takes the record away from the account again; the account itself is untouched.
        Is(HttpStatusCode.OK, await Put($"/api/workers/{w.Id}/account", s.Admin.Token, new { userId = (string?)null }));
        Is(HttpStatusCode.NotFound, await Get("/api/workers/me", me), "worker_profile_not_linked");
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{w.Id}", me));
        Assert.True(await Rag.WithDb(Api, db => db.Users.AnyAsync(u => u.Email == email && u.IsActive)));

        var actions = (await Get($"/api/audit?entityType=worker&entityId={w.Id}&limit=50", s.Admin.Token)).Body.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("worker.account_linked", actions);
        Assert.Contains("worker.account_unlinked", actions);
    }

    [DockerFact]
    public async Task Linking_an_existing_account_gives_it_the_worker_role_when_needed_and_one_account_serves_one_worker()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var (s, elsewhere) = (await NewSite(super), await NewSite(super));
        var w1 = await Register(s, "Linked One");
        var w2 = await Register(s, "Linked Two");

        // A Monitoring Operator account has no "own records" permission: linking adds the Worker role.
        var operatorAccount = await NewPerson(R.MonitoringOperator, s.Factory);
        Is(HttpStatusCode.NotFound, await Get("/api/workers/me", operatorAccount.Token) is var before && before.Status == HttpStatusCode.Forbidden
            ? new Reply(HttpStatusCode.NotFound, default, "", Array.Empty<byte>(), null) : before);
        var linked = await Put($"/api/workers/{w1.Id}/account", s.Admin.Token, new { userId = operatorAccount.Id });
        Is(HttpStatusCode.OK, linked);
        Assert.True(linked.Body.GetProperty("hasAccount").GetBoolean());
        var roles = await Rag.WithDb(Api, db => db.UserRoles.Where(r => r.UserId == operatorAccount.Id).Select(r => r.RoleId).ToListAsync());
        Assert.Equal(new[] { R.MonitoringOperator, R.Worker }, roles.OrderBy(r => r));
        // Their old token is stale (roles changed); a fresh one reaches their own record.
        Is(HttpStatusCode.Unauthorized, await Get("/api/workers/me", operatorAccount.Token));
        var fresh = await Rag.WithDb(Api, async db =>
        {
            using var scope = Api.Services.CreateScope();
            var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(operatorAccount.Id, default);
            return scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _);
        });
        Assert.Equal(w1.Id, (await Get("/api/workers/me", fresh)).Id);

        // One account ↔ one worker; accounts of other factories and Super Admins can't be linked.
        Is(HttpStatusCode.Conflict, await Put($"/api/workers/{w2.Id}/account", s.Admin.Token, new { userId = operatorAccount.Id }), "account_already_linked");
        Is(HttpStatusCode.BadRequest, await Put($"/api/workers/{w2.Id}/account", s.Admin.Token, new { userId = elsewhere.Hr.Id }), "user_not_in_factory");
        Is(HttpStatusCode.BadRequest, await Put($"/api/workers/{w2.Id}/account", s.Admin.Token, new { userId = super.Id }), "user_not_in_factory");
        Is(HttpStatusCode.BadRequest, await Put($"/api/workers/{w2.Id}/account", s.Admin.Token, new { userId = Guid.NewGuid() }), "user_not_in_factory");
        Is(HttpStatusCode.Forbidden, await Put($"/api/workers/{w2.Id}/account", s.Hr.Token, new { userId = s.Monitor.Id }), "permission_denied");

        // An account that already holds a role with "own records" (a Factory Admin) is linked as it is.
        var secondAdmin = await NewPerson(R.FactoryAdmin, s.Factory);
        Is(HttpStatusCode.OK, await Put($"/api/workers/{w2.Id}/account", super.Token, new { userId = secondAdmin.Id }));
        Assert.Equal(new[] { R.FactoryAdmin }, await Rag.WithDb(Api, db => db.UserRoles.Where(r => r.UserId == secondAdmin.Id).Select(r => r.RoleId).ToListAsync()));
        Assert.Equal(w2.Id, (await Get("/api/workers/me", secondAdmin.Token)).Id);
    }

    // ─── Cards ─────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_card_is_a_credential_not_an_identity()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var (w1, w2) = (await Register(s, "Card One"), await Register(s, "Card Two"));
        var uid = "04:A1:" + Tag().ToUpperInvariant();
        var normalised = uid.Replace(":", "");

        var card = await Post($"/api/workers/{w1.Id}/cards", s.Hr.Token, new { cardUid = uid.ToLowerInvariant(), cardType = "nfc", label = "Blue 17" });
        Is(HttpStatusCode.Created, card);
        Assert.Equal((normalised, "NFC", true), (card.Str("cardUid"), card.Str("cardType"), card.Body.GetProperty("isActive").GetBoolean()));
        // The card id is not the Worker ID, and the worker's Worker ID is unchanged.
        Assert.NotEqual(card.Str("cardUid"), w1.Str("workerCode"));
        Assert.Equal(w1.Str("workerCode"), (await Get($"/api/workers/{w1.Id}", s.Hr.Token)).Str("workerCode"));

        Is(HttpStatusCode.BadRequest, await Post($"/api/workers/{w1.Id}/cards", s.Hr.Token, new { cardUid = "!!" }));
        Is(HttpStatusCode.Conflict, await Post($"/api/workers/{w1.Id}/cards", s.Hr.Token, new { cardUid = normalised }), "card_in_use");
        Is(HttpStatusCode.Conflict, await Post($"/api/workers/{w2.Id}/cards", s.Hr.Token, new { cardUid = uid }), "card_in_use");

        // Returned, the same physical card can go to someone else; the first assignment stays on record.
        Is(HttpStatusCode.BadRequest, await Patch($"/api/workers/{w1.Id}/cards/{card.Id}", s.Hr.Token, new { reason = "Because" }));
        Is(HttpStatusCode.NotFound, await Patch($"/api/workers/{w2.Id}/cards/{card.Id}", s.Hr.Token, new { reason = "Returned" }), "card_not_found");
        var returned = await Patch($"/api/workers/{w1.Id}/cards/{card.Id}", s.Hr.Token, new { reason = "returned" });
        Is(HttpStatusCode.OK, returned);
        Assert.Equal((false, "Returned"), (returned.Body.GetProperty("isActive").GetBoolean(), returned.Str("deactivationReason")));
        Is(HttpStatusCode.Created, await Post($"/api/workers/{w2.Id}/cards", s.Hr.Token, new { cardUid = uid }));
        Assert.Single((await Get($"/api/workers/{w1.Id}/cards", s.Hr.Token)).Body.EnumerateArray());
        Assert.True(Assert.Single((await Get($"/api/workers/{w2.Id}/cards", s.Hr.Token)).Body.EnumerateArray()).GetProperty("isActive").GetBoolean());
        Assert.Equal(2, await Rag.WithDb(Api, db => db.WorkerCards.CountAsync(c => c.CardUid == normalised)));

        var actions = (await Get($"/api/audit?entityType=worker_card&factoryId={s.Factory}", s.Admin.Token)).Body.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(2, actions.Count(x => x == "worker.card_issued"));
        Assert.Contains("worker.card_deactivated", actions);
    }

    // ─── Photo ─────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_profile_photo_is_checked_stored_and_served_only_to_those_who_can_see_the_worker()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var w = await Register(s, "Photo Worker", s.Line2);
        var path = $"/api/workers/{w.Id}/photo";

        Is(HttpStatusCode.NotFound, await Get(path, s.Hr.Token), "photo_not_found");
        Is(HttpStatusCode.BadRequest, await Put(path, s.Hr.Token, PhotoUpload(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"))), "image_unsupported_format");
        Is(HttpStatusCode.BadRequest, await Put(path, s.Hr.Token, PhotoUpload(TestImages.PngHeader(30_000, 30_000))), "image_dimensions_out_of_range");
        Is(HttpStatusCode.Forbidden, await Put(path, s.Manager.Token, PhotoUpload(TestImages.Png(8, 8))), "permission_denied");

        var png = TestImages.Png(32, 32);
        var set = await Put(path, s.Hr.Token, PhotoUpload(png, "text/html")); // the declared type is ignored
        Is(HttpStatusCode.OK, set);
        Assert.True(set.Body.GetProperty("hasPhoto").GetBoolean());
        Assert.DoesNotContain("photoKey", set.Raw, StringComparison.OrdinalIgnoreCase);

        foreach (var who in new[] { s.Hr, s.Manager, s.Monitor })
        {
            var photo = await Get(path, who.Token);
            Is(HttpStatusCode.OK, photo);
            Assert.Equal("image/png", photo.ContentType);
            Assert.Equal(png, photo.Bytes);
        }
        Is(HttpStatusCode.NotFound, await Get(path, s.Supervisor.Token), "worker_not_found"); // not on their line
        Is(HttpStatusCode.Forbidden, await Get(path, s.Devices.Token), "permission_denied");

        // Replacing removes the old file; removing leaves no photo.
        var firstKey = await Rag.WithDb(Api, db => db.Workers.Where(x => x.Id == Guid.Parse(w.Id)).Select(x => x.PhotoKey).SingleAsync());
        var replacement = TestImages.Png(16, 16, 10, 20, 30);
        Is(HttpStatusCode.OK, await Put(path, s.Hr.Token, PhotoUpload(replacement)));
        Assert.Equal(replacement, (await Get(path, s.Hr.Token)).Bytes);
        var store = Api.Services.GetRequiredService<IWorkerPhotoStore>();
        Assert.Null(await store.OpenReadAsync(firstKey!, default));
        var removed = await Send(HttpMethod.Delete, path, s.Hr.Token);
        Is(HttpStatusCode.OK, removed);
        Assert.False(removed.Body.GetProperty("hasPhoto").GetBoolean());
        Is(HttpStatusCode.NotFound, await Get(path, s.Hr.Token), "photo_not_found");
    }

    // ─── Directory, statistics, export ─────────────────────────────────────

    [DockerFact]
    public async Task The_directory_searches_filters_sorts_and_pages_and_the_statistics_agree()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var names = new[] { "Amina Khatun", "Bilkis Akter", "Chompa Rani", "Dolly Begum", "Eti Akter", "Fatema Khatun", "Golapi Begum" };
        var workers = new List<Reply>();
        for (var i = 0; i < names.Length; i++)
        {
            var body = Worker(s, names[i], i < 4 ? s.Line1 : i < 6 ? s.Line2 : null, i % 2 == 0 ? s.Sewing : s.Cutting);
            body["designation"] = i < 5 ? "Sewing Operator" : "Helper";
            body["joinedOn"] = $"2024-0{i + 1}-10";
            body["employmentType"] = i == 0 ? "Contract" : "Permanent";
            if (i == 3) body["floorId"] = s.Floor;
            var r = await Post("/api/workers", s.Hr.Token, body);
            Is(HttpStatusCode.Created, r);
            workers.Add(r);
        }
        Is(HttpStatusCode.OK, await Patch($"/api/workers/{workers[1].Id}/status", s.Hr.Token, new { status = "Inactive" }));
        Is(HttpStatusCode.OK, await Patch($"/api/workers/{workers[2].Id}/status", s.Hr.Token, new { status = "Suspended", reason = "inquiry" }));
        Is(HttpStatusCode.OK, await Patch($"/api/workers/{workers[6].Id}/status", s.Hr.Token, new { status = "Terminated", reason = "misconduct" }));

        var f = $"factoryId={s.Factory}";
        async Task<List<string>> Names(string query, Person? who = null) =>
            Items(await Get($"/api/workers?{f}&pageSize=100&{query}", (who ?? s.Hr).Token)).Select(x => x.GetProperty("fullName").GetString()!).ToList();

        // Search: Worker ID (exact and partial, any case), name, and literal wildcards.
        Assert.Equal(new[] { names[2] }, await Names($"search={workers[2].Str("workerCode").ToLowerInvariant()}"));
        Assert.Equal(new[] { names[0], names[5] }, await Names("search=khatun&sort=name"));
        Assert.Empty(await Names("search=%25"));
        Assert.Empty(await Names("search=_"));

        // Filters, alone and combined.
        Assert.Equal(new[] { names[0], names[2], names[4], names[6] }, await Names($"departmentId={s.Sewing}&sort=name"));
        Assert.Equal(new[] { names[4], names[5] }, await Names($"lineId={s.Line2}&sort=name"));
        Assert.Equal(new[] { names[6] }, await Names("lineId=none"));
        Assert.Equal(new[] { names[3] }, await Names($"floorId={s.Floor}"));
        Assert.Equal(new[] { names[1] }, await Names("status=inactive"));
        Assert.Equal(new[] { names[0], names[3], names[4], names[5] }, await Names("status=Active&sort=name"));
        Assert.Empty(await Names("status=Deleted"));
        Assert.Equal(new[] { names[5], names[6] }, await Names("designation=helper&sort=name"));
        Assert.Equal(new[] { names[0] }, await Names("employmentType=Contract"));
        Assert.Equal(new[] { names[0] }, await Names($"departmentId={s.Sewing}&lineId={s.Line1}&status=Active&designation={Uri.EscapeDataString("Sewing Operator")}"));

        // Sorting.
        Assert.Equal(names, await Names("sort=workerId"));
        Assert.Equal(names.Reverse(), await Names("sort=-workerId"));
        Assert.Equal(names.Reverse(), await Names("sort=-name"));
        Assert.Equal(names.Reverse(), await Names("sort=-joined"));
        Assert.Equal(names, await Names("sort=nonsense")); // unknown sort falls back to Worker ID
        var byDepartment = await Names("sort=department");
        Assert.Equal(new[] { names[1], names[3], names[5] }, byDepartment.Take(3)); // Cutting before Sewing
        // A caller who can't see joining dates can't order by them either.
        Assert.Equal(names, await Names("sort=-joined", s.Monitor));

        // Pagination.
        var page1 = await Get($"/api/workers?{f}&pageSize=3&page=1&sort=name", s.Hr.Token);
        var page3 = await Get($"/api/workers?{f}&pageSize=3&page=3&sort=name", s.Hr.Token);
        Assert.Equal((7, 3, 3), (page1.Body.GetProperty("total").GetInt32(), page1.Body.GetProperty("pageSize").GetInt32(), Items(page1).Count()));
        Assert.Equal(new[] { names[6] }, Items(page3).Select(x => x.GetProperty("fullName").GetString()));
        Assert.Equal(100, (await Get($"/api/workers?{f}&pageSize=5000", s.Hr.Token)).Body.GetProperty("pageSize").GetInt32());

        // Statistics over the same workers.
        var stats = await Get($"/api/workers/statistics?{f}", s.Manager.Token);
        Is(HttpStatusCode.OK, stats);
        int N(string name) => stats.Body.GetProperty(name).GetInt32();
        Assert.Equal((7, 4, 1, 1, 0, 1), (N("total"), N("active"), N("inactive"), N("suspended"), N("resigned"), N("terminated")));
        (string Label, int Total, int Active)[] Group(string name) => stats.Body.GetProperty(name).EnumerateArray()
            .Select(x => (x.GetProperty("label").GetString()!, x.GetProperty("total").GetInt32(), x.GetProperty("active").GetInt32())).ToArray();
        Assert.Equal(new[] { ("Sewing", 4, 2), ("Cutting", 3, 2) }, Group("byDepartment"));
        Assert.Contains(("Not assigned", 1, 0), Group("byLine"));
        Assert.Equal(new[] { (4, 2), (2, 2) }, Group("byLine").Where(g => g.Label != "Not assigned").Select(g => (g.Total, g.Active)));
        Assert.Equal(new[] { ("Sewing Operator", 5, 3), ("Helper", 2, 1) }, Group("byDesignation"));
        Assert.Equal(new[] { ("Permanent", 6, 3), ("Contract", 1, 1) }, Group("byEmploymentType"));
        Assert.Equal(1, N("withoutLine"));
        // The supervisor's statistics cover their line only, without employment figures.
        var mineOnly = await Get("/api/workers/statistics", s.Supervisor.Token);
        Assert.Equal(4, mineOnly.Body.GetProperty("total").GetInt32());
        Assert.Equal(0, mineOnly.Body.GetProperty("byEmploymentType").GetArrayLength());
    }

    [DockerFact]
    public async Task Export_gives_each_role_its_columns_and_is_audited()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var body = Worker(s, "=cmd|' /C calc'!A0", s.Line1);
        body["phone"] = "+8801711555000";
        body["email"] = "export@example.com";
        body["presentAddress"] = "House 1, \"Lake\" Road";
        var w = await Post("/api/workers", s.Hr.Token, body);
        Is(HttpStatusCode.Created, w);
        await Register(s, "Second Row", s.Line2, s.Cutting);

        var full = await Get($"/api/workers/export?factoryId={s.Factory}&sort=workerId", s.Hr.Token);
        Is(HttpStatusCode.OK, full);
        Assert.Equal("text/csv", full.ContentType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, full.Bytes.Take(3));
        var rows = BunonBrain.Application.Workers.Csv.Read(full.Raw, ',', 100);
        Assert.Equal(3, rows.Count);
        var header = rows[0].Fields;
        foreach (var column in new[] { "worker_id", "full_name", "status", "department", "line", "employee_number", "joining_date", "phone", "email", "present_address" })
            Assert.Contains(column, header);
        string Cell(int row, string column) => rows[row].Fields[Array.IndexOf(header, column)];
        Assert.Equal(w.Str("workerCode"), Cell(1, "worker_id"));
        Assert.Equal("'=cmd|' /C calc'!A0", Cell(1, "full_name"));   // a formula in a name is defused
        Assert.Equal("'+8801711555000", Cell(1, "phone"));
        Assert.Equal("House 1, \"Lake\" Road", Cell(1, "present_address"));
        Assert.Equal(("Sewing", "2024-03-01", "Active"), (Cell(1, "department"), Cell(1, "joining_date"), Cell(1, "status")));

        // Filters apply to the export as to the list.
        Assert.Equal(2, BunonBrain.Application.Workers.Csv.Read((await Get($"/api/workers/export?factoryId={s.Factory}&departmentId={s.Cutting}", s.Hr.Token)).Raw, ',', 100).Count);

        // The Factory Manager's export has employment columns but no personal ones.
        var manager = await Get($"/api/workers/export?factoryId={s.Factory}", s.Manager.Token);
        Is(HttpStatusCode.OK, manager);
        var managerHeader = BunonBrain.Application.Workers.Csv.Read(manager.Raw, ',', 100)[0].Fields;
        Assert.Contains("joining_date", managerHeader);
        foreach (var personal in new[] { "phone", "email", "present_address", "date_of_birth", "emergency_contact_phone" }) Assert.DoesNotContain(personal, managerHeader);
        Assert.DoesNotContain("8801711555000", manager.Raw);
        Assert.DoesNotContain("export@example.com", manager.Raw);

        foreach (var who in new[] { s.Supervisor, s.Monitor, s.Devices })
            Is(HttpStatusCode.Forbidden, await Get("/api/workers/export", who.Token), "permission_denied");

        var audit = (await Get($"/api/audit?action=worker.exported&factoryId={s.Factory}", s.Admin.Token)).Body.EnumerateArray().ToList();
        Assert.Equal(3, audit.Count);
        Assert.Contains(audit, e => e.GetProperty("actorEmail").GetString() == s.Manager.Email && e.GetProperty("reason").GetString()!.Contains("employment details only"));
        Assert.Contains(audit, e => e.GetProperty("actorEmail").GetString() == s.Hr.Email && e.GetProperty("reason").GetString()!.Contains("with personal details"));
    }

    // ─── CSV import ────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Import_previews_validates_and_registers_only_the_valid_rows()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        var existing = Worker(s, "Already Here");
        existing["employeeCode"] = "OLD-1";
        existing["phone"] = "01711-222333";
        Is(HttpStatusCode.Created, await Post("/api/workers", s.Hr.Token, existing));
        var lineName = await Rag.WithDb(Api, db => db.ProductionLines.Where(l => l.Id == s.Line1).Select(l => l.Name).SingleAsync());

        var csv =
            "Full Name,Phone,Joining Date,Department,Designation,Employee Number,Line,Floor,Gender,Notes\r\n" +
            $"Rahima Begum,01711000001,2024-01-15,sewing,Operator,NEW-1,{s.Line1},F2,female,ok\r\n" +     // 2 valid (department by name, line by id, floor by code)
            $"Karim Uddin,01711000002,15/02/2024,CUT,Cutter,,\"{lineName}\",Second floor,,\r\n" +          // 3 valid (department by code, line by name)
            "Nasima,01711000003,2024-01-15,Finishing,Operator,,,,,\r\n" +                                  // 4 unknown department
            "Jamal,01711000004,2024-01-15,Sewing,Operator,old-1,,,,\r\n" +                                 // 5 employee number already used
            "Salma,01711000005,2024-01-15,Sewing,Operator,,line-does-not-exist,,,\r\n" +                   // 6 unknown line
            "Rina,01711000006,2024-01-15,Sewing,Operator,,,F9,,\r\n" +                                     // 7 unknown floor
            ",,not-a-date,,,,,,,\r\n" +                                                                    // 8 several errors
            "Already Here,+880 1711 222333,2024-01-15,Sewing,Operator,,,,,\r\n";                           // 9 valid, with a duplicate warning

        var total = (await Get($"/api/workers?factoryId={s.Factory}", s.Hr.Token)).Body.GetProperty("total").GetInt32();

        // Preview: the default. Nothing is saved, no Worker ID is shown.
        var preview = await Post("/api/workers/import", s.Hr.Token, CsvUpload(csv, s.Factory, null));
        Is(HttpStatusCode.OK, preview);
        Assert.True(preview.Body.GetProperty("dryRun").GetBoolean());
        Assert.Equal((8, 3, 5, 0), (preview.Body.GetProperty("totalRows").GetInt32(), preview.Body.GetProperty("validRows").GetInt32(),
            preview.Body.GetProperty("invalidRows").GetInt32(), preview.Body.GetProperty("imported").GetInt32()));
        Assert.Equal(new[] { "Notes" }, preview.Body.GetProperty("ignoredColumns").EnumerateArray().Select(x => x.GetString()));
        var rows = preview.Body.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("rowNumber").GetInt32());
        string[] Fields(int row) => rows[row].GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()!).ToArray();
        Assert.True(rows[2].GetProperty("valid").GetBoolean() && rows[3].GetProperty("valid").GetBoolean() && rows[9].GetProperty("valid").GetBoolean());
        Assert.Equal(("Sewing", "Second floor"), (rows[2].GetProperty("department").GetString(), rows[2].GetProperty("floor").GetString()));
        Assert.Equal("Cutting", rows[3].GetProperty("department").GetString());
        Assert.Equal(new[] { "department" }, Fields(4));
        Assert.Equal(new[] { "employeeCode" }, Fields(5));
        Assert.Equal(new[] { "line" }, Fields(6));
        Assert.Equal(new[] { "floor" }, Fields(7));
        Assert.Equal(new[] { "fullName", "phone", "joinedOn", "department", "designation" }, Fields(8));
        Assert.Contains("already has this name and phone", rows[9].GetProperty("warnings")[0].GetProperty("message").GetString());
        Assert.All(rows.Values, r => Assert.False(r.TryGetProperty("workerCode", out _)));
        Assert.Equal(total, (await Get($"/api/workers?factoryId={s.Factory}", s.Hr.Token)).Body.GetProperty("total").GetInt32());

        // Commit: the three valid rows are registered with new Worker IDs; the invalid rows are skipped and explained.
        var done = await Post("/api/workers/import", s.Hr.Token, CsvUpload(csv, s.Factory, false));
        Is(HttpStatusCode.Created, done);
        Assert.Equal((false, 3, 5), (done.Body.GetProperty("dryRun").GetBoolean(), done.Body.GetProperty("imported").GetInt32(), done.Body.GetProperty("invalidRows").GetInt32()));
        var committed = done.Body.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("rowNumber").GetInt32());
        var codes = new[] { 2, 3, 9 }.Select(n => committed[n].GetProperty("workerCode").GetString()!).ToList();
        Assert.Equal(3, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^W-[0-9]{3,}$", c));
        Assert.False(committed[4].TryGetProperty("workerCode", out _));
        Assert.Equal(total + 3, (await Get($"/api/workers?factoryId={s.Factory}", s.Hr.Token)).Body.GetProperty("total").GetInt32());

        var rahima = await Get($"/api/workers/{codes[0]}", s.Hr.Token);
        Assert.Equal(("Rahima Begum", "NEW-1", "Female", "Active", s.Line1, "2024-01-15"),
            (rahima.Str("fullName"), rahima.Str("employeeCode"), rahima.Str("gender"), rahima.Str("status"), rahima.Str("lineId"), rahima.Str("joinedOn")));
        Assert.Equal("Second floor", rahima.Body.GetProperty("floor").GetProperty("name").GetString());
        var karim = await Get($"/api/workers/{codes[1]}", s.Hr.Token);
        Assert.Equal(("2024-02-15", s.Line1, "Permanent"), (karim.Str("joinedOn"), karim.Str("lineId"), karim.Str("employmentType")));
        Assert.Equal("Cutting", karim.Body.GetProperty("department").GetProperty("name").GetString());
        Assert.Single((await Get($"/api/workers/{codes[0]}/profile", s.Hr.Token)).Body.GetProperty("statusHistory").EnumerateArray());

        var audit = await Get($"/api/audit?factoryId={s.Factory}&action=worker.imported", s.Admin.Token);
        Assert.Contains("3 worker(s) imported", Assert.Single(audit.Body.EnumerateArray()).GetProperty("reason").GetString());

        // Importing the same file again: the row with an employee number is now refused, nothing is duplicated silently.
        var again = await Post("/api/workers/import", s.Hr.Token, CsvUpload(csv, s.Factory, true));
        var second = again.Body.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("rowNumber").GetInt32());
        Assert.False(second[2].GetProperty("valid").GetBoolean());
        Assert.Contains("NEW-1", second[2].GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.True(second[3].GetProperty("warnings").GetArrayLength() > 0);
    }

    [DockerFact]
    public async Task Import_refuses_unusable_files_and_saves_nothing()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        async Task Refused(HttpContent upload, string code, string? says = null)
        {
            var r = await Post("/api/workers/import", s.Hr.Token, upload);
            Is(HttpStatusCode.BadRequest, r, code);
            if (says is not null) Assert.Contains(says, r.Body.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        await Refused(CsvUpload("full_name,phone\nA,1\n", s.Factory, false), "import_file_invalid", "missing required column");
        await Refused(CsvUpload("full_name,phone,joining_date,department,designation\n", s.Factory, false), "import_file_invalid", "no worker rows");
        await Refused(CsvUpload("full_name,phone,joining_date,department,designation\n\"never closed,1,2,3,4\n", s.Factory, false), "import_file_invalid", "never closed");
        var binary = new ByteArrayContent(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFE, 0x00, 0x00, 0x9C });
        await Refused(new MultipartFormDataContent { { binary, "file", "workers.xlsx" }, { new StringContent(s.Factory), "factoryId" } }, "import_file_invalid", "utf-8");
        await Refused(new MultipartFormDataContent { { new StringContent(s.Factory), "factoryId" } }, "import_file_invalid");
        var noFactory = new ByteArrayContent(Encoding.UTF8.GetBytes("full_name\nA\n"));
        await Refused(new MultipartFormDataContent { { noFactory, "file", "w.csv" } }, "factory_required");
        var tooMany = "full_name,phone,joining_date,department,designation\n" +
            string.Concat(Enumerable.Range(0, 2001).Select(i => $"Worker {i},01713{i:D6},2024-01-15,Sewing,Operator\n"));
        await Refused(CsvUpload(tooMany, s.Factory, false), "import_file_invalid", "2000");

        // A file whose every row is invalid imports nothing and says so.
        var allBad = await Post("/api/workers/import", s.Hr.Token, CsvUpload("full_name,phone,joining_date,department,designation\nA,01711000001,2024-01-15,Nowhere,Op\n", s.Factory, false));
        Is(HttpStatusCode.OK, allBad);
        Assert.Equal((0, 1), (allBad.Body.GetProperty("imported").GetInt32(), allBad.Body.GetProperty("invalidRows").GetInt32()));

        Assert.Equal(0, (await Get($"/api/workers?factoryId={s.Factory}", s.Hr.Token)).Body.GetProperty("total").GetInt32());

        // The template the page offers is accepted as it is (its example department exists here).
        var template = await Get("/api/workers/import/template", s.Hr.Token);
        Is(HttpStatusCode.OK, template);
        var preview = await Post("/api/workers/import", s.Hr.Token, CsvUpload(template.Raw, s.Factory, true));
        Is(HttpStatusCode.OK, preview);
        Assert.Equal(1, preview.Body.GetProperty("validRows").GetInt32());
    }

    // ─── Departments and floors ────────────────────────────────────────────

    [DockerFact]
    public async Task Departments_and_floors_are_managed_by_administrators_and_listed_for_those_who_place_workers()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var s = await NewSite(super);
        await Register(s, "Counted", department: s.Sewing);

        var list = await Get($"/api/factories/{s.Factory}/departments", s.Hr.Token);
        Is(HttpStatusCode.OK, list);
        Assert.Equal(new[] { ("CUT", "Cutting", 0), ("SEW", "Sewing", 1) },
            list.Body.EnumerateArray().Select(d => (d.GetProperty("code").GetString()!, d.GetProperty("name").GetString()!, d.GetProperty("workerCount").GetInt32())));
        foreach (var who in new[] { s.Manager, s.Supervisor, s.Monitor })
        {
            Is(HttpStatusCode.OK, await Get($"/api/factories/{s.Factory}/departments", who.Token));
            Is(HttpStatusCode.OK, await Get($"/api/factories/{s.Factory}/floors", who.Token));
        }

        Is(HttpStatusCode.Conflict, await Post($"/api/factories/{s.Factory}/departments", s.Admin.Token, new { code = "sew", name = "Other" }), "department_code_taken");
        Is(HttpStatusCode.Conflict, await Post($"/api/factories/{s.Factory}/departments", s.Admin.Token, new { code = "SW2", name = "sewing" }), "department_name_taken");
        Is(HttpStatusCode.BadRequest, await Post($"/api/factories/{s.Factory}/departments", s.Admin.Token, new { code = "bad!code", name = "x" }));
        Is(HttpStatusCode.Conflict, await Post($"/api/factories/{s.Factory}/floors", s.Admin.Token, new { code = "f2", name = "Other" }), "floor_code_taken");
        Is(HttpStatusCode.Conflict, await Put($"/api/factories/{s.Factory}/departments/{s.Cutting}", s.Admin.Token, new { name = "Sewing", isActive = true }), "department_name_taken");
        var renamed = await Put($"/api/factories/{s.Factory}/departments/{s.Cutting}", s.Admin.Token, new { name = "Cutting & Spreading", isActive = true });
        Is(HttpStatusCode.OK, renamed);
        Assert.Equal("Cutting & Spreading", renamed.Str("name"));
        Is(HttpStatusCode.OK, await Put($"/api/factories/{s.Factory}/floors/{s.Floor}", s.Admin.Token, new { name = "Floor two", isActive = false }));
        Is(HttpStatusCode.NotFound, await Put($"/api/factories/{s.Factory}/floors/{Guid.NewGuid()}", s.Admin.Token, new { name = "x", isActive = true }), "floor_not_found");

        // HR places workers but doesn't define the organisation.
        Is(HttpStatusCode.Forbidden, await Post($"/api/factories/{s.Factory}/departments", s.Hr.Token, new { code = "HR", name = "HR" }), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Post($"/api/factories/{s.Factory}/floors", s.Hr.Token, new { code = "F3", name = "Third" }), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Get($"/api/factories/{s.Factory}/departments", (await NewPerson(R.Worker, s.Factory)).Token), "permission_denied");

        var actions = (await Get($"/api/audit?factoryId={s.Factory}&limit=100", s.Admin.Token)).Body.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        foreach (var action in new[] { "department.created", "department.updated", "floor.created", "floor.updated" }) Assert.Contains(action, actions);
    }

    // ─── Migration of workers that existed before this module ──────────────

    [DockerFact]
    public async Task Migrating_existing_worker_rows_gives_them_ids_statuses_and_history_and_can_be_rolled_back()
    {
        const string previous = "20261113091028_AddSiteSettings";
        var database = "bunonbrain_test_worker_migration_" + Tag();
        var admin = new NpgsqlConnectionStringBuilder(_stack.ConnectionStringFor("postgres")) { Pooling = false, ApplicationName = "worker-migration-test" };
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", conn);
            await create.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<BunonBrainDbContext>()
            .UseNpgsql(_stack.ConnectionStringFor(database), npg => { npg.MigrationsHistoryTable("__ef_migrations"); npg.UseVector(); })
            .Options;

        await using var db = new BunonBrainDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);

        // The schema as it was: a factory and three workers, one of them inactive, created in a known order.
        var factory = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO factories ("Id", "Code", "Name", "IsActive", "CreatedAt", "UpdatedAt") VALUES ({0}, 'OLD', 'Old factory', true, now(), now());
            INSERT INTO workers ("Id", "FactoryId", "EmployeeCode", "FullName", "IsActive", "Phone", "CreatedAt", "UpdatedAt") VALUES
              ({1}, {0}, 'E-OLDEST', 'Oldest', true,  '01711000001', now() - interval '3 days', now() - interval '1 day'),
              ({2}, {0}, 'E-MIDDLE', 'Middle', false, NULL,          now() - interval '2 days', now() - interval '2 days'),
              ({3}, {0}, 'E-NEWEST', 'Newest', true,  NULL,          now() - interval '1 days', now() - interval '1 day');
            """, factory, ids[0], ids[1], ids[2]);

        await migrator.MigrateAsync();

        var workers = await db.Workers.AsNoTracking().OrderBy(w => w.WorkerNumber).ToListAsync();
        Assert.Equal(new[] { ("W-001", "Oldest", "Active"), ("W-002", "Middle", "Inactive"), ("W-003", "Newest", "Active") },
            workers.Select(w => (w.WorkerCode, w.FullName, w.Status)));
        Assert.Equal(new long[] { 1, 2, 3 }, workers.Select(w => w.WorkerNumber));
        Assert.Equal(new[] { "E-OLDEST", "E-MIDDLE", "E-NEWEST" }, workers.Select(w => w.EmployeeCode));
        Assert.Equal("01711000001", workers[0].Phone);
        Assert.All(workers, w => Assert.Equal(("Permanent", (Guid?)null), (w.EmploymentType, w.DepartmentId)));
        var history = await db.WorkerStatusHistory.AsNoTracking().OrderBy(h => h.Id).ToListAsync();
        Assert.Equal(3, history.Count);
        Assert.All(history, h => Assert.Null(h.FromStatus));
        Assert.Equal("Inactive", history.Single(h => h.WorkerId == ids[1]).ToStatus);

        // The sequence carries on after the migrated workers: the next registration is W-004.
        var next = (await db.Database.SqlQueryRaw<long>("SELECT nextval('worker_number_seq') AS \"Value\"").ToListAsync())[0];
        Assert.Equal(4, next);

        // Rolling back restores the old columns from the new ones.
        await migrator.MigrateAsync(previous);
        var restored = await db.Database.SqlQueryRaw<string>(
            "SELECT \"EmployeeCode\" || ':' || \"IsActive\"::text AS \"Value\" FROM workers ORDER BY \"CreatedAt\"").ToListAsync();
        Assert.Equal(new[] { "E-OLDEST:true", "E-MIDDLE:false", "E-NEWEST:true" }, restored);

        await db.DisposeAsync();
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
