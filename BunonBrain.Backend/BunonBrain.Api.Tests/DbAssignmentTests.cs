using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Worker ↔ workstation assignments end to end, against real PostgreSQL,
/// through the HTTP API: creating, the validity rules, one active primary
/// per worker, capacity, overlap prevention (service and database),
/// transfers and their rollback, concurrency, preserved history, factory and
/// line isolation, and what each role may do.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAssignmentTests
{
    private readonly PostgresStack _stack;
    public DbAssignmentTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    // ─── HTTP plumbing ─────────────────────────────────────────────────────

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw)
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
    private Task<Reply> Patch(string path, string bearer, object body) => Send(HttpMethod.Patch, path, bearer, body);

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static string Iso(DateTimeOffset t) => t.ToString("O");
    private static IEnumerable<JsonElement> Items(Reply r) => r.Body.GetProperty("items").EnumerateArray();

    // ─── People and places ─────────────────────────────────────────────────

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, string? factory, params string[] lines)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@assign.test";
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

    /// <summary>A factory with Sewing / Finishing, a floor, two lines with workstations, a machine, and one person per role.</summary>
    public sealed record Site(
        string Factory, string Sewing, string Finishing, string Floor, string Line1, string Line2,
        Person Admin, Person Manager, Person Hr, Person Supervisor, Person Monitor, Person Devices);

    private async Task<Site> NewSite()
    {
        var super = await NewPerson(R.SuperAdmin, null);
        var f = await Post("/api/factories", super.Token, new { code = $"S{Tag()[..6]}", name = $"Assign factory {Tag()}" });
        Is(HttpStatusCode.Created, f);
        var admin = await NewPerson(R.FactoryAdmin, f.Id);
        var sewing = await Post($"/api/factories/{f.Id}/departments", admin.Token, new { code = "SEW", name = "Sewing" });
        var finishing = await Post($"/api/factories/{f.Id}/departments", admin.Token, new { code = "FIN", name = "Finishing" });
        var floor = await Post($"/api/factories/{f.Id}/floors", admin.Token, new { code = "F2", name = "Second floor" });
        foreach (var r in new[] { sewing, finishing, floor }) Is(HttpStatusCode.Created, r);
        string l1 = $"al-{Tag()}", l2 = $"al-{Tag()}";
        await Rag.WithDb(Api, db =>
        {
            db.ProductionLines.Add(new ProductionLine { Id = l1, Name = $"Line 1 {l1}", Workstations = 20, FactoryId = Guid.Parse(f.Id) });
            db.ProductionLines.Add(new ProductionLine { Id = l2, Name = $"Line 2 {l2}", Workstations = 20, FactoryId = Guid.Parse(f.Id) });
            return db.SaveChangesAsync();
        });
        return new Site(f.Id, sewing.Id, finishing.Id, floor.Id, l1, l2, admin,
            await NewPerson(R.FactoryManager, f.Id), await NewPerson(R.HrOfficer, f.Id), await NewPerson(R.ProductionSupervisor, f.Id, l1),
            await NewPerson(R.MonitoringOperator, f.Id), await NewPerson(R.DeviceAdmin, f.Id));
    }

    private async Task<string> Workstation(Site s, string line, string? dept = null, int capacity = 1, string? code = null)
    {
        var r = await Post("/api/workstations", s.Admin.Token, new
        {
            factoryId = s.Factory, lineId = line, departmentId = dept ?? s.Sewing, floorId = s.Floor, code = code ?? $"T-{Tag()[..5]}", capacity,
        });
        Is(HttpStatusCode.Created, r);
        return r.Id;
    }

    private async Task<string> Worker(Site s, string? line = null, string name = "Assign Worker")
    {
        var r = await Post("/api/workers", s.Hr.Token, new
        {
            factoryId = s.Factory, fullName = name, phone = "01711" + Random.Shared.Next(100000, 999999), joinedOn = "2024-03-01",
            departmentId = s.Sewing, designation = "Operator", lineId = line,
        });
        Is(HttpStatusCode.Created, r);
        return r.Id;
    }

    private Task<Reply> Assign(string token, string worker, string ws, DateTimeOffset? at = null, string? machine = null) =>
        Post("/api/assignments", token, new { workerId = worker, workstationId = ws, machineId = machine, startAt = at is { } t ? Iso(t) : null, reason = "Initial placement" });

    // ─── Creating ──────────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_supervisor_assigns_a_worker_and_the_placement_is_copied_from_the_workstation()
    {
        var s = await NewSite();
        var ws = await Workstation(s, s.Line1, code: $"T-{Tag()[..4]}");
        var machine = await Post("/api/machines", s.Admin.Token, new { factoryId = s.Factory, code = $"M-{Tag()[..5]}", machineType = "Single needle lockstitch", workstationId = ws });
        Is(HttpStatusCode.Created, machine);
        var worker = await Worker(s, s.Line1);
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);

        var a = await Assign(s.Supervisor.Token, worker, ws, start, machine.Id);
        Is(HttpStatusCode.Created, a);
        Assert.Equal(("Active", true, worker, ws), (a.Str("status"), a.Body.GetProperty("isPrimary").GetBoolean(), a.Str("workerId"), a.Body.GetProperty("workstation").GetProperty("id").GetString()));
        Assert.Equal(s.Factory, a.Str("factoryId"));
        Assert.Equal(s.Sewing, a.Body.GetProperty("department").GetProperty("id").GetString());
        Assert.Equal(s.Floor, a.Body.GetProperty("floor").GetProperty("id").GetString());
        Assert.Equal(s.Line1, a.Body.GetProperty("line").GetProperty("id").GetString());
        Assert.Equal(machine.Id, a.Body.GetProperty("machine").GetProperty("id").GetString());
        Assert.False(a.Has("endAt"));
        Assert.Equal(s.Supervisor.Email, a.Str("assignedByEmail"));
        Assert.Equal(start.ToUnixTimeMilliseconds(), DateTimeOffset.Parse(a.Str("startAt")).ToUnixTimeMilliseconds());

        // The worker record shows where they are now; the workstation shows who is there.
        var w = await Get($"/api/workers/{worker}", s.Hr.Token);
        Assert.Equal(a.Body.GetProperty("workstation").GetProperty("code").GetString(), w.Str("workstation"));
        Assert.StartsWith(machine.Body.GetProperty("code").GetString()!, (await Get($"/api/workers/{worker}/profile", s.Hr.Token)).Str("machine"));
        var station = await Get($"/api/workstations/{ws}", s.Supervisor.Token);
        Assert.Equal(("CapacityReached", 1), (station.Str("occupancy"), station.Body.GetProperty("assignedCount").GetInt32()));
        Assert.Equal(worker, station.Body.GetProperty("workers")[0].GetProperty("workerId").GetString());
        Assert.Contains("assignment.created", (await Get($"/api/audit?entityType=workstation_assignment&entityId={a.Id}", s.Admin.Token)).Raw);
    }

    [DockerFact]
    public async Task Invalid_worker_workstation_and_machine_relationships_are_refused()
    {
        var (s, other) = (await NewSite(), await NewSite());
        var ws = await Workstation(s, s.Line1);
        var theirs = await Workstation(other, other.Line1);
        var super = await NewPerson(R.SuperAdmin, null);

        // A workstation of another factory: invisible to the Factory Admin, refused for the Super Admin.
        var w1 = await Worker(s, s.Line1);
        Is(HttpStatusCode.NotFound, await Assign(s.Admin.Token, w1, theirs), "workstation_not_found");
        Is(HttpStatusCode.BadRequest, await Assign(super.Token, w1, theirs), "workstation_in_other_factory");

        // Inactive workers can't be assigned.
        var suspended = await Worker(s, s.Line1);
        Is(HttpStatusCode.OK, await Patch($"/api/workers/{suspended}/status", s.Hr.Token, new { status = "Suspended", reason = "inquiry" }));
        Is(HttpStatusCode.Conflict, await Assign(s.Admin.Token, suspended, ws), "worker_not_active");

        // Inactive workstations can't take workers.
        var closed = await Workstation(s, s.Line1);
        var closedDto = await Get($"/api/workstations/{closed}", s.Admin.Token);
        Is(HttpStatusCode.OK, await Put($"/api/workstations/{closed}", s.Admin.Token, new
        {
            lineId = s.Line1, departmentId = s.Sewing, floorId = s.Floor, name = (string?)null, capacity = 1,
            position = closedDto.Body.GetProperty("position").GetInt32(), isActive = false,
        }));
        Is(HttpStatusCode.Conflict, await Assign(s.Admin.Token, w1, closed), "workstation_inactive");

        // Machines: inactive, standing at another workstation, of another factory, or already in use.
        var other1 = await Workstation(s, s.Line1);
        var atOther = await Post("/api/machines", s.Admin.Token, new { factoryId = s.Factory, code = $"M-{Tag()[..5]}", workstationId = other1 });
        var inactive = await Post("/api/machines", s.Admin.Token, new { factoryId = s.Factory, code = $"M-{Tag()[..5]}" });
        Is(HttpStatusCode.OK, await Put($"/api/machines/{inactive.Id}", s.Admin.Token, new { isActive = false, workstationId = (string?)null }));
        var foreign = await Post("/api/machines", other.Admin.Token, new { factoryId = other.Factory, code = $"M-{Tag()[..5]}" });
        Is(HttpStatusCode.Conflict, await Assign(s.Admin.Token, w1, ws, machine: atOther.Id), "machine_at_other_workstation");
        Is(HttpStatusCode.Conflict, await Assign(s.Admin.Token, w1, ws, machine: inactive.Id), "machine_inactive");
        Is(HttpStatusCode.BadRequest, await Assign(s.Admin.Token, w1, ws, machine: foreign.Id), "machine_not_found");
        var shared = await Post("/api/machines", s.Admin.Token, new { factoryId = s.Factory, code = $"M-{Tag()[..5]}" });
        var roomy = await Workstation(s, s.Line1, capacity: 2);
        Is(HttpStatusCode.Created, await Assign(s.Admin.Token, w1, roomy, machine: shared.Id));
        Is(HttpStatusCode.Conflict, await Assign(s.Admin.Token, await Worker(s, s.Line1), roomy, machine: shared.Id), "machine_in_use");

        // Bad input and times.
        Is(HttpStatusCode.BadRequest, await Post("/api/assignments", s.Admin.Token, new { workerId = Guid.Empty, workstationId = ws }));
        var w2 = await Worker(s, s.Line1);
        Is(HttpStatusCode.BadRequest, await Assign(s.Admin.Token, w2, ws, DateTimeOffset.UtcNow.AddHours(2)), "invalid_start_time");
        Is(HttpStatusCode.BadRequest, await Assign(s.Admin.Token, w2, ws, DateTimeOffset.UtcNow.AddDays(-40)), "invalid_start_time");
        Is(HttpStatusCode.NotFound, await Assign(s.Admin.Token, Guid.NewGuid().ToString(), ws), "worker_not_found");

        // Nothing invalid was stored.
        Assert.Equal(1, await Rag.WithDb(Api, db => db.Assignments.CountAsync(a => a.FactoryId == Guid.Parse(s.Factory))));
    }

    [DockerFact]
    public async Task A_worker_has_one_active_primary_assignment_and_periods_never_overlap()
    {
        var s = await NewSite();
        var (t1, t2) = (await Workstation(s, s.Line1), await Workstation(s, s.Line1));
        var worker = await Worker(s, s.Line1);
        var now = DateTimeOffset.UtcNow;

        var first = await Assign(s.Admin.Token, worker, t1, now.AddHours(-5));
        Is(HttpStatusCode.Created, first);
        Is(HttpStatusCode.Conflict, await Assign(s.Admin.Token, worker, t2), "worker_already_assigned");

        // Ended at -3 h: a new assignment may start at -3 h (touching), not at -4 h (overlapping).
        Is(HttpStatusCode.OK, await Post($"/api/assignments/{first.Id}/end", s.Admin.Token, new { reason = "Line stopped", endAt = Iso(now.AddHours(-3)) }));
        Is(HttpStatusCode.Conflict, await Assign(s.Admin.Token, worker, t2, now.AddHours(-4)), "assignment_overlap");
        Is(HttpStatusCode.Created, await Assign(s.Admin.Token, worker, t2, now.AddHours(-3)));

        // The database refuses the same, whatever the application does.
        var wid = Guid.Parse(worker);
        var row = await Rag.WithDb(Api, db => db.Assignments.AsNoTracking().FirstAsync(a => a.WorkerId == wid && a.EndAt == null));
        async Task<string> Insert(DateTimeOffset start, DateTimeOffset? end, string status)
        {
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Rag.WithDb(Api, db =>
            {
                db.Assignments.Add(new WorkstationAssignment
                {
                    WorkerId = wid, FactoryId = row.FactoryId, DepartmentId = row.DepartmentId, LineId = row.LineId, WorkstationId = Guid.Parse(t1),
                    StartAt = start, EndAt = end, Status = status, AssignedByEmail = "test",
                });
                return db.SaveChangesAsync();
            }));
            return Assert.IsType<PostgresException>(ex.InnerException).SqlState;
        }
        Assert.Contains(await Insert(now.AddMinutes(-10), null, "Active"), new[] { "23505", "23P01" });   // a second open primary
        Assert.Equal("23P01", await Insert(now.AddHours(-4.5), now.AddHours(-3.5), "Ended"));              // overlaps the first period
        Assert.Equal("23514", await Insert(now.AddHours(-10), now.AddHours(-11), "Ended"));                // ends before it starts
        Assert.Equal("23514", await Insert(now.AddHours(-10), null, "Ended"));                             // "Ended" without an end
    }

    [DockerFact]
    public async Task Workstation_capacity_is_configurable_and_enforced()
    {
        var s = await NewSite();
        var ws = await Workstation(s, s.Line1, capacity: 2);
        var workers = new[] { await Worker(s, s.Line1), await Worker(s, s.Line1), await Worker(s, s.Line1) };

        Is(HttpStatusCode.Created, await Assign(s.Admin.Token, workers[0], ws));
        Assert.Equal("Occupied", (await Get($"/api/workstations/{ws}", s.Admin.Token)).Str("occupancy"));
        Is(HttpStatusCode.Created, await Assign(s.Admin.Token, workers[1], ws));
        Is(HttpStatusCode.Conflict, await Assign(s.Admin.Token, workers[2], ws), "workstation_full");
        Assert.Equal("CapacityReached", (await Get($"/api/workstations/{ws}", s.Admin.Token)).Str("occupancy"));

        var dto = await Get($"/api/workstations/{ws}", s.Admin.Token);
        object Edit(int capacity, bool active = true, string? line = null) => new
        {
            lineId = line ?? s.Line1, departmentId = s.Sewing, floorId = s.Floor, name = "Collar attach", capacity,
            position = dto.Body.GetProperty("position").GetInt32(), isActive = active,
        };
        Is(HttpStatusCode.Conflict, await Put($"/api/workstations/{ws}", s.Admin.Token, Edit(1)), "capacity_below_assigned");
        Is(HttpStatusCode.Conflict, await Put($"/api/workstations/{ws}", s.Admin.Token, Edit(2, active: false)), "workstation_has_assignments");
        Is(HttpStatusCode.Conflict, await Put($"/api/workstations/{ws}", s.Admin.Token, Edit(2, line: s.Line2)), "workstation_has_assignments");
        var raised = await Put($"/api/workstations/{ws}", s.Admin.Token, Edit(3));
        Is(HttpStatusCode.OK, raised);
        Assert.Equal(("Occupied", 3, "Collar attach"), (raised.Str("occupancy"), raised.Body.GetProperty("capacity").GetInt32(), raised.Str("name")));
        Is(HttpStatusCode.Created, await Assign(s.Admin.Token, workers[2], ws));
        Is(HttpStatusCode.BadRequest, await Put($"/api/workstations/{ws}", s.Admin.Token, Edit(21)));

        // Only administrators configure workstations.
        foreach (var who in new[] { s.Supervisor, s.Manager, s.Hr })
            Is(HttpStatusCode.Forbidden, await Put($"/api/workstations/{ws}", who.Token, Edit(5)), "permission_denied");
    }

    // ─── Transfers ─────────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_transfer_closes_the_old_assignment_and_opens_the_new_one_at_the_same_instant()
    {
        var s = await NewSite();
        var t25 = await Workstation(s, s.Line1, code: $"T25-{Tag()[..4]}");
        var t30 = await Workstation(s, s.Line2, dept: s.Finishing, code: $"T30-{Tag()[..4]}");
        var worker = await Worker(s, s.Line1, "Transfer Worker");
        var now = DateTimeOffset.UtcNow;
        var first = await Assign(s.Manager.Token, worker, t25, now.AddHours(-4));
        Is(HttpStatusCode.Created, first);

        var effective = now.AddHours(-1);
        var t = await Post($"/api/assignments/{first.Id}/transfer", s.Manager.Token, new { workstationId = t30, reason = "Line 2 short of operators", effectiveAt = Iso(effective) });
        Is(HttpStatusCode.OK, t);
        var closed = t.Body.GetProperty("closed");
        var opened = t.Body.GetProperty("opened");
        Assert.Equal("Transferred", closed.GetProperty("status").GetString());
        Assert.Equal("Active", opened.GetProperty("status").GetString());
        // Same instant: no gap and no overlap.
        Assert.Equal(DateTimeOffset.Parse(closed.GetProperty("endAt").GetString()!), DateTimeOffset.Parse(opened.GetProperty("startAt").GetString()!));
        Assert.Equal(effective.ToUnixTimeMilliseconds(), DateTimeOffset.Parse(opened.GetProperty("startAt").GetString()!).ToUnixTimeMilliseconds());
        Assert.Equal(first.Id, opened.GetProperty("previousAssignmentId").GetString());
        Assert.Equal(opened.GetProperty("id").GetString(), closed.GetProperty("nextAssignmentId").GetString());
        Assert.Equal(s.Manager.Email, closed.GetProperty("endedByEmail").GetString());
        Assert.Equal(s.Manager.Email, opened.GetProperty("assignedByEmail").GetString());
        Assert.Equal("Line 2 short of operators", closed.GetProperty("endReason").GetString());
        Assert.Equal("Line 2 short of operators", opened.GetProperty("reason").GetString());
        // Across lines and departments: the new assignment carries the new placement.
        Assert.Equal(s.Line2, opened.GetProperty("line").GetProperty("id").GetString());
        Assert.Equal(s.Finishing, opened.GetProperty("department").GetProperty("id").GetString());
        Assert.Equal(s.Line2, (await Get($"/api/workers/{worker}", s.Hr.Token)).Str("lineId"));

        // The worker's timeline has both, newest first; the workstations agree.
        var history = (await Get($"/api/workers/{worker}/assignments", s.Hr.Token)).Body.EnumerateArray().ToList();
        Assert.Equal(new[] { "Active", "Transferred" }, history.Select(h => h.GetProperty("status").GetString()));
        Assert.Equal("Available", (await Get($"/api/workstations/{t25}", s.Admin.Token)).Str("occupancy"));
        Assert.Single((await Get($"/api/workstations/{t25}/assignments", s.Admin.Token)).Body.EnumerateArray());
        Assert.Contains("assignment.transferred", (await Get($"/api/audit?entityType=workstation_assignment&entityId={opened.GetProperty("id").GetString()}", s.Admin.Token)).Raw);

        // A closed assignment can't be transferred again; nor to the same workstation; nor before it began.
        Is(HttpStatusCode.Conflict, await Post($"/api/assignments/{first.Id}/transfer", s.Manager.Token, new { workstationId = t25, reason = "again" }), "assignment_not_active");
        var current = opened.GetProperty("id").GetString();
        Is(HttpStatusCode.Conflict, await Post($"/api/assignments/{current}/transfer", s.Manager.Token, new { workstationId = t30, reason = "same" }), "same_workstation");
        Is(HttpStatusCode.BadRequest, await Post($"/api/assignments/{current}/transfer", s.Manager.Token, new { workstationId = t25, reason = "too early", effectiveAt = Iso(effective.AddMinutes(-5)) }), "invalid_effective_time");
        Is(HttpStatusCode.BadRequest, await Post($"/api/assignments/{current}/transfer", s.Manager.Token, new { workstationId = t25, reason = "" }));

        // A full target refuses the transfer and nothing changes.
        var full = await Workstation(s, s.Line1);
        Is(HttpStatusCode.Created, await Assign(s.Admin.Token, await Worker(s, s.Line1), full));
        Is(HttpStatusCode.Conflict, await Post($"/api/assignments/{current}/transfer", s.Manager.Token, new { workstationId = full, reason = "full" }), "workstation_full");
        Assert.Equal("Active", (await Get($"/api/assignments/{current}", s.Hr.Token)).Str("status"));
    }

    [DockerFact]
    public async Task A_transfer_that_fails_half_way_leaves_everything_as_it_was()
    {
        var s = await NewSite();
        var (from, to) = (await Workstation(s, s.Line1), await Workstation(s, s.Line1));
        var worker = await Worker(s, s.Line1);
        var a = await Assign(s.Admin.Token, worker, from, DateTimeOffset.UtcNow.AddHours(-2));
        Is(HttpStatusCode.Created, a);
        var before = await Rag.WithDb(Api, db => db.Assignments.CountAsync(x => x.WorkerId == Guid.Parse(worker)));

        // Make the database refuse the new row after the old one has already been closed in the same transaction.
        var fn = $"fail_{Tag()}";
        await Rag.WithDb(Api, db => db.Database.ExecuteSqlRawAsync($"""
            CREATE FUNCTION {fn}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."WorkstationId" = '{to}'::uuid THEN RAISE EXCEPTION 'simulated failure while opening the new assignment'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER {fn} BEFORE INSERT ON workstation_assignments FOR EACH ROW EXECUTE FUNCTION {fn}();
            """));
        try
        {
            var failed = await Post($"/api/assignments/{a.Id}/transfer", s.Admin.Token, new { workstationId = to, reason = "will fail" });
            Assert.Equal(HttpStatusCode.InternalServerError, failed.Status);
        }
        finally
        {
            await Rag.WithDb(Api, db => db.Database.ExecuteSqlRawAsync($"DROP TRIGGER {fn} ON workstation_assignments; DROP FUNCTION {fn}();"));
        }

        // Rolled back: the old assignment is still open, nothing new exists, the worker hasn't moved.
        var old = await Get($"/api/assignments/{a.Id}", s.Admin.Token);
        Assert.Equal("Active", old.Str("status"));
        Assert.False(old.Has("endAt"));
        Assert.Equal(before, await Rag.WithDb(Api, db => db.Assignments.CountAsync(x => x.WorkerId == Guid.Parse(worker))));
        Assert.Equal(a.Body.GetProperty("workstation").GetProperty("code").GetString(), (await Get($"/api/workers/{worker}", s.Hr.Token)).Str("workstation"));
        // And the same transfer works once the failure is gone.
        Is(HttpStatusCode.OK, await Post($"/api/assignments/{a.Id}/transfer", s.Admin.Token, new { workstationId = to, reason = "retry" }));
    }

    // ─── Concurrency ───────────────────────────────────────────────────────

    [DockerFact]
    public async Task Concurrent_assignments_never_exceed_capacity_or_double_assign_a_worker()
    {
        var s = await NewSite();
        var supervisor2 = await NewPerson(R.ProductionSupervisor, s.Factory, s.Line1);

        // Ten workers at once for a three-place workstation, from two supervisors.
        var ws = await Workstation(s, s.Line1, capacity: 3);
        var workers = new List<string>();
        for (var i = 0; i < 10; i++) workers.Add(await Worker(s, s.Line1, $"Race {i}"));
        var results = await Task.WhenAll(workers.Select((w, i) => Assign(i % 2 == 0 ? s.Supervisor.Token : supervisor2.Token, w, ws)));
        Assert.Equal(3, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r => Is(HttpStatusCode.Conflict, r, "workstation_full"));
        Assert.Equal(3, await Rag.WithDb(Api, db => db.Assignments.CountAsync(a => a.WorkstationId == Guid.Parse(ws) && a.EndAt == null)));

        // One worker sent to five workstations at once: exactly one wins.
        var one = await Worker(s, s.Line1, "Wanted everywhere");
        var stations = new List<string>();
        for (var i = 0; i < 5; i++) stations.Add(await Workstation(s, s.Line1));
        var sameWorker = await Task.WhenAll(stations.Select(st => Assign(s.Admin.Token, one, st)));
        Assert.Equal(1, sameWorker.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(sameWorker.Where(r => r.Status != HttpStatusCode.Created), r => Is(HttpStatusCode.Conflict, r, "worker_already_assigned"));

        // The same assignment transferred by two people at once: one transfer, one refusal, one open assignment.
        var winner = sameWorker.Single(r => r.Status == HttpStatusCode.Created);
        var targets = stations.Where(st => st != winner.Body.GetProperty("workstation").GetProperty("id").GetString()).Take(2).ToList();
        var transfers = await Task.WhenAll(
            Post($"/api/assignments/{winner.Id}/transfer", s.Supervisor.Token, new { workstationId = targets[0], reason = "A" }),
            Post($"/api/assignments/{winner.Id}/transfer", supervisor2.Token, new { workstationId = targets[1], reason = "B" }));
        Assert.Equal(1, transfers.Count(r => r.Status == HttpStatusCode.OK));
        Is(HttpStatusCode.Conflict, transfers.Single(r => r.Status != HttpStatusCode.OK), "assignment_not_active");
        Assert.Equal(1, await Rag.WithDb(Api, db => db.Assignments.CountAsync(a => a.WorkerId == Guid.Parse(one) && a.EndAt == null)));
    }

    // ─── History ───────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Assignment_history_is_kept_whole_and_follows_the_worker_status()
    {
        var s = await NewSite();
        var (t1, t2, t3) = (await Workstation(s, s.Line1), await Workstation(s, s.Line1), await Workstation(s, s.Line1));
        var worker = await Worker(s, s.Line1, "History Worker");
        var now = DateTimeOffset.UtcNow;
        var a1 = await Assign(s.Admin.Token, worker, t1, now.AddHours(-6));
        var a2 = (await Post($"/api/assignments/{a1.Id}/transfer", s.Admin.Token, new { workstationId = t2, reason = "balance", effectiveAt = Iso(now.AddHours(-4)) })).Body.GetProperty("opened").GetProperty("id").GetString()!;
        Is(HttpStatusCode.OK, await Post($"/api/assignments/{a2}/end", s.Admin.Token, new { reason = "Training", endAt = Iso(now.AddHours(-2)) }));
        Is(HttpStatusCode.Conflict, await Post($"/api/assignments/{a2}/end", s.Admin.Token, new { reason = "twice" }), "assignment_not_active");
        var a3 = await Assign(s.Admin.Token, worker, t3, now.AddHours(-1));
        Is(HttpStatusCode.Created, a3);
        Assert.True((await Get($"/api/workers/{worker}", s.Hr.Token)).Has("workstation"));

        // The worker leaves: the open assignment ends with the status change, nothing is deleted.
        Is(HttpStatusCode.OK, await Patch($"/api/workers/{worker}/status", s.Hr.Token, new { status = "Resigned", reason = "Moved away" }));
        var history = (await Get($"/api/workers/{worker}/assignments", s.Hr.Token)).Body.EnumerateArray().ToList();
        Assert.Equal(3, history.Count);
        Assert.Equal(new[] { "Ended", "Ended", "Transferred" }, history.Select(h => h.GetProperty("status").GetString()));
        Assert.Equal("Worker status changed to Resigned", history[0].GetProperty("endReason").GetString());
        Assert.All(history, h => Assert.True(h.TryGetProperty("endAt", out _)));
        Assert.False((await Get($"/api/workers/{worker}", s.Hr.Token)).Has("workstation"));
        Assert.Equal("Available", (await Get($"/api/workstations/{t3}", s.Admin.Token)).Str("occupancy"));
        // Periods follow each other without overlap.
        var ordered = history.OrderBy(h => DateTimeOffset.Parse(h.GetProperty("startAt").GetString()!)).ToList();
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(DateTimeOffset.Parse(ordered[i].GetProperty("startAt").GetString()!) >= DateTimeOffset.Parse(ordered[i - 1].GetProperty("endAt").GetString()!));

        // The database won't let history be removed from under the records that point at it.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Rag.WithDb(Api, db => db.Assignments.Where(a => a.Id == Guid.Parse(a1.Id)).ExecuteDeleteAsync()));
        Assert.Equal("23503", ex.SqlState);
        var wex = await Assert.ThrowsAsync<PostgresException>(() => Rag.WithDb(Api, db => db.Workstations.Where(w => w.Id == Guid.Parse(t1)).ExecuteDeleteAsync()));
        Assert.Equal("23503", wex.SqlState);
        // The filtered list returns the same history.
        Assert.Equal(3, (await Get($"/api/assignments?workerId={worker}", s.Admin.Token)).Body.GetProperty("total").GetInt32());
        Assert.Equal(1, (await Get($"/api/assignments?workerId={worker}&status=Transferred", s.Admin.Token)).Body.GetProperty("total").GetInt32());
    }

    // ─── Isolation and roles ───────────────────────────────────────────────

    [DockerFact]
    public async Task Supervisors_manage_only_their_lines_and_factories_stay_apart()
    {
        var (s, other) = (await NewSite(), await NewSite());
        var mine = await Workstation(s, s.Line1);
        var notMine = await Workstation(s, s.Line2);
        var onMyLine = await Worker(s, s.Line1, "On my line");
        var onOtherLine = await Worker(s, s.Line2, "On the other line");

        // Within the line: allowed. Another line's workstation or worker: refused.
        var a = await Assign(s.Supervisor.Token, onMyLine, mine);
        Is(HttpStatusCode.Created, a);
        Is(HttpStatusCode.Forbidden, await Assign(s.Supervisor.Token, await Worker(s, s.Line1), notMine), "line_out_of_scope");
        Is(HttpStatusCode.NotFound, await Assign(s.Supervisor.Token, onOtherLine, mine), "worker_not_found");
        Is(HttpStatusCode.Forbidden, await Post($"/api/assignments/{a.Id}/transfer", s.Supervisor.Token, new { workstationId = notMine, reason = "x" }), "line_out_of_scope");
        var theirs = await Assign(s.Admin.Token, onOtherLine, notMine);
        Is(HttpStatusCode.Created, theirs);
        Is(HttpStatusCode.NotFound, await Post($"/api/assignments/{theirs.Id}/end", s.Supervisor.Token, new { reason = "x" }), "assignment_not_found");
        Is(HttpStatusCode.NotFound, await Get($"/api/assignments/{theirs.Id}", s.Supervisor.Token));
        Is(HttpStatusCode.NotFound, await Get($"/api/workstations/{notMine}", s.Supervisor.Token));
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{onOtherLine}/assignments", s.Supervisor.Token));

        // Lists and statistics show only their line.
        Assert.Equal(new[] { a.Id }, Items(await Get("/api/assignments/active", s.Supervisor.Token)).Select(x => x.GetProperty("id").GetString()));
        Assert.All(Items(await Get("/api/workstations", s.Supervisor.Token)), w => Assert.Equal(s.Line1, w.GetProperty("line").GetProperty("id").GetString()));
        var stats = await Get("/api/assignments/statistics", s.Supervisor.Token);
        Assert.Equal((1, 1), (stats.Body.GetProperty("totalWorkstations").GetInt32(), stats.Body.GetProperty("activeAssignments").GetInt32()));

        // Another factory sees nothing of this one and can't act on it.
        Is(HttpStatusCode.NotFound, await Get($"/api/assignments/{a.Id}", other.Admin.Token));
        Is(HttpStatusCode.NotFound, await Get($"/api/workstations/{mine}", other.Admin.Token));
        Is(HttpStatusCode.NotFound, await Post($"/api/assignments/{a.Id}/end", other.Admin.Token, new { reason = "x" }));
        Is(HttpStatusCode.NotFound, await Assign(other.Admin.Token, onMyLine, await Workstation(other, other.Line1)), "worker_not_found");
        Assert.Equal(0, (await Get($"/api/assignments?factoryId={s.Factory}", other.Admin.Token)).Body.GetProperty("total").GetInt32());
        Assert.Equal(0, (await Get($"/api/workstations?factoryId={s.Factory}", other.Admin.Token)).Body.GetProperty("total").GetInt32());
        Is(HttpStatusCode.Forbidden, await Post("/api/workstations", other.Admin.Token, new { factoryId = s.Factory, lineId = s.Line1, departmentId = s.Sewing, code = "X-1" }), "factory_out_of_scope");
        Assert.Equal("Active", (await Get($"/api/assignments/{a.Id}", s.Admin.Token)).Str("status"));
    }

    [DockerFact]
    public async Task Each_role_gets_exactly_its_assignment_access()
    {
        var s = await NewSite();
        var ws = await Workstation(s, s.Line1);
        var worker = await Worker(s, s.Line1, "Role Worker");
        var a = await Assign(s.Manager.Token, worker, ws); // Factory Manager may assign
        Is(HttpStatusCode.Created, a);

        // Read-only: Monitoring Operator and HR see assignments and history but can't change them.
        foreach (var who in new[] { s.Monitor, s.Hr })
        {
            Is(HttpStatusCode.OK, await Get("/api/assignments", who.Token));
            Is(HttpStatusCode.OK, await Get($"/api/workers/{worker}/assignments", who.Token));
            Assert.True((await Get($"/api/workstations/{ws}", who.Token)).Has("workers"));
            Is(HttpStatusCode.Forbidden, await Assign(who.Token, await Worker(s, s.Line1), ws), "permission_denied");
            Is(HttpStatusCode.Forbidden, await Post($"/api/assignments/{a.Id}/end", who.Token, new { reason = "x" }), "permission_denied");
        }

        // IT / device administration: workstations and machines, never who is assigned.
        Is(HttpStatusCode.Forbidden, await Get("/api/assignments", s.Devices.Token), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Get($"/api/workers/{worker}/assignments", s.Devices.Token), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Assign(s.Devices.Token, worker, ws), "permission_denied");
        var station = await Get($"/api/workstations/{ws}", s.Devices.Token);
        Is(HttpStatusCode.OK, station);
        Assert.False(station.Has("workers"));
        Assert.DoesNotContain("Role Worker", station.Raw);
        Is(HttpStatusCode.Created, await Post("/api/machines", s.Devices.Token, new { factoryId = s.Factory, code = $"M-{Tag()[..5]}", workstationId = ws }));

        // Worker: their own current and past assignments, nothing else.
        var account = await NewPerson(R.Worker, s.Factory);
        Is(HttpStatusCode.OK, await Put($"/api/workers/{worker}/account", s.Admin.Token, new { userId = account.Id }));
        var me = await TokenFor(account.Id);
        var mine = await Get("/api/workers/me/assignments", me);
        Is(HttpStatusCode.OK, mine);
        Assert.Equal(a.Id, mine.Body[0].GetProperty("id").GetString());
        Is(HttpStatusCode.OK, await Get($"/api/assignments/{a.Id}", me));
        var someoneElse = await Worker(s, s.Line1);
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{someoneElse}/assignments", me));
        foreach (var path in new[] { "/api/assignments", "/api/assignments/statistics", "/api/workstations" })
            Is(HttpStatusCode.Forbidden, await Get(path, me), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Post($"/api/assignments/{a.Id}/end", me, new { reason = "x" }), "permission_denied");
    }

    // ─── Workstation setup and the dashboard ───────────────────────────────

    [DockerFact]
    public async Task Workstations_are_created_in_bulk_and_the_dashboard_counts_them()
    {
        var s = await NewSite();
        var prefix = $"B{Tag()[..3]}-";
        var bulk = await Post("/api/workstations/bulk", s.Admin.Token, new { factoryId = s.Factory, lineId = s.Line1, departmentId = s.Sewing, floorId = s.Floor, prefix, from = 1, count = 6 });
        Is(HttpStatusCode.Created, bulk);
        Assert.Equal(6, bulk.Body.GetProperty("created").GetInt32());
        var again = await Post("/api/workstations/bulk", s.Admin.Token, new { factoryId = s.Factory, lineId = s.Line1, departmentId = s.Sewing, prefix, from = 5, count = 4 });
        Is(HttpStatusCode.Created, again);
        Assert.Equal((2, 2), (again.Body.GetProperty("created").GetInt32(), again.Body.GetProperty("skipped").GetArrayLength()));
        Is(HttpStatusCode.Conflict, await Post("/api/workstations", s.Admin.Token, new { factoryId = s.Factory, lineId = s.Line1, departmentId = s.Sewing, code = (prefix + "001").ToLowerInvariant() }), "workstation_code_taken");
        Is(HttpStatusCode.BadRequest, await Post("/api/workstations", s.Admin.Token, new { factoryId = s.Factory, lineId = s.Line1, departmentId = Guid.NewGuid(), code = "Z-1" }), "department_not_in_factory");

        var list = Items(await Get($"/api/workstations?lineId={s.Line1}&pageSize=100", s.Admin.Token)).ToList();
        Assert.Equal(8, list.Count);
        Assert.Equal(list.Select(w => w.GetProperty("position").GetInt32()).OrderBy(p => p), list.Select(w => w.GetProperty("position").GetInt32()));
        var ids = list.Select(w => w.GetProperty("id").GetString()!).ToList();

        var workers = new List<string>();
        for (var i = 0; i < 3; i++) workers.Add(await Worker(s, s.Line1));
        var unassignedWorker = await Worker(s, s.Line1);
        Is(HttpStatusCode.Created, await Assign(s.Admin.Token, workers[0], ids[0], DateTimeOffset.UtcNow.AddHours(-3)));
        Is(HttpStatusCode.Created, await Assign(s.Admin.Token, workers[1], ids[1]));
        var moving = await Assign(s.Admin.Token, workers[2], ids[2], DateTimeOffset.UtcNow.AddHours(-2));
        Is(HttpStatusCode.OK, await Post($"/api/assignments/{moving.Id}/transfer", s.Admin.Token, new { workstationId = ids[3], reason = "Bottleneck at the next operation" }));
        var last = ids[7];
        var lastDto = await Get($"/api/workstations/{last}", s.Admin.Token);
        Is(HttpStatusCode.OK, await Put($"/api/workstations/{last}", s.Admin.Token, new { lineId = s.Line1, departmentId = s.Sewing, floorId = (string?)null, name = (string?)null, capacity = 1, position = lastDto.Body.GetProperty("position").GetInt32(), isActive = false }));

        var stats = await Get($"/api/assignments/statistics?factoryId={s.Factory}&lineId={s.Line1}", s.Admin.Token);
        Is(HttpStatusCode.OK, stats);
        int N(string n) => stats.Body.GetProperty(n).GetInt32();
        Assert.Equal((8, 3, 4, 1, 3), (N("totalWorkstations"), N("occupiedWorkstations"), N("availableWorkstations"), N("inactiveWorkstations"), N("activeAssignments")));
        Assert.Equal((4, 1), (N("activeWorkers"), N("unassignedActiveWorkers")));
        var recent = Assert.Single(stats.Body.GetProperty("recentTransfers").EnumerateArray());
        Assert.Equal("Bottleneck at the next operation", recent.GetProperty("reason").GetString());
        var line = Assert.Single(stats.Body.GetProperty("byLine").EnumerateArray());
        Assert.Equal((3, 4, 1), (line.GetProperty("occupied").GetInt32(), line.GetProperty("available").GetInt32(), line.GetProperty("inactive").GetInt32()));

        // Occupancy filters and the unassigned-workers list used by the assign dialog.
        Assert.Equal(3, (await Get($"/api/workstations?lineId={s.Line1}&occupancy=CapacityReached", s.Admin.Token)).Body.GetProperty("total").GetInt32());
        Assert.Equal(4, (await Get($"/api/workstations?lineId={s.Line1}&occupancy=Available", s.Admin.Token)).Body.GetProperty("total").GetInt32());
        Assert.Equal(1, (await Get($"/api/workstations?lineId={s.Line1}&occupancy=inactive", s.Admin.Token)).Body.GetProperty("total").GetInt32());
        var free = Items(await Get($"/api/workers?factoryId={s.Factory}&status=Active&assigned=false&pageSize=100", s.Admin.Token)).Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { unassignedWorker }, free);
    }

    [DockerFact]
    public async Task The_old_free_text_workstation_field_can_no_longer_bypass_assignments()
    {
        var s = await NewSite();
        var ws = await Workstation(s, s.Line1);
        var worker = await Worker(s, s.Line1);
        Is(HttpStatusCode.BadRequest, await Put($"/api/workers/{worker}/workstation", s.Supervisor.Token, new { lineId = s.Line1, workstation = "T-99" }), "use_assignments");
        Is(HttpStatusCode.Created, await Assign(s.Supervisor.Token, worker, ws));
        Is(HttpStatusCode.Conflict, await Put($"/api/workers/{worker}/workstation", s.Admin.Token, new { lineId = s.Line2 }), "worker_has_assignment");
        var edit = new
        {
            fullName = "Assign Worker", phone = "01711000000", joinedOn = "2024-03-01", departmentId = s.Sewing, designation = "Operator", lineId = s.Line2,
        };
        Is(HttpStatusCode.Conflict, await Put($"/api/workers/{worker}", s.Hr.Token, edit), "worker_has_assignment");
        Assert.Equal(s.Line1, (await Get($"/api/workers/{worker}", s.Hr.Token)).Str("lineId"));
    }
}
