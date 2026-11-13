using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Access;
using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Role-based access control end to end, against real PostgreSQL, through
/// the HTTP API with real sign-ins:
/// Super Admin → factories → Factory Admin → the other roles → workers; then
/// factory isolation, line isolation, worker self-access, privilege
/// escalation, activation / deactivation, role assignment, temporary
/// passwords and the audit trail.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAccessControlTests
{
    private readonly PostgresStack _stack;
    public DbAccessControlTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    // ─── HTTP plumbing ─────────────────────────────────────────────────────

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw, string? RefreshCookie)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public string Id => Body.GetProperty("id").GetString()!;
    }

    private async Task<Reply> Send(HttpMethod method, string path, string? bearer = null, object? body = null, string? cookie = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = Rag.Json(body);
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (cookie is not null) req.Headers.Add("Cookie", $"{AuthCookieSettings.CookieName}={cookie}");
        using var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('['))
            ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        var setCookie = res.Headers.TryGetValues("Set-Cookie", out var v)
            ? v.FirstOrDefault(c => c.StartsWith(AuthCookieSettings.CookieName + "=", StringComparison.Ordinal)) : null;
        var refresh = setCookie?.Split(';')[0][(AuthCookieSettings.CookieName.Length + 1)..];
        return new Reply(res.StatusCode, json, raw, string.IsNullOrEmpty(refresh) ? null : refresh);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string? bearer, object body) => Send(HttpMethod.Post, path, bearer, body);
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, body);

    public sealed record Session(string Token, string Refresh, JsonElement User);

    private async Task<Session> Login(string email, string password)
    {
        var r = await Post("/api/auth/login", null, new { email, password });
        Assert.True(r.Status == HttpStatusCode.OK, $"login {email}: {(int)r.Status} {r.Raw}");
        return new Session(r.Body.GetProperty("accessToken").GetString()!, r.RefreshCookie!, r.Body.GetProperty("user"));
    }

    private async Task<Session> Refresh(Session s)
    {
        var r = await Send(HttpMethod.Post, "/api/auth/refresh", cookie: s.Refresh);
        Assert.True(r.Status == HttpStatusCode.OK, $"refresh: {(int)r.Status} {r.Raw}");
        return new Session(r.Body.GetProperty("accessToken").GetString()!, r.RefreshCookie!, r.Body.GetProperty("user"));
    }

    private static string Pw() => "Pw-" + TestEnvironment.RandomSecret(14);
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!);

    /// <summary>Absent or null (the API omits null fields).</summary>
    private static bool Hidden(JsonElement e, string name) => !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

    private static void AssertEmptyPage(Reply r)
    {
        Is(HttpStatusCode.OK, r);
        Assert.Equal(0, r.Body.GetProperty("total").GetInt32());
    }

    // ─── The world every test builds on ────────────────────────────────────

    public sealed record Account(string Id, string Email, string Password);

    public sealed record World(
        Session Super,
        string FactoryA, string FactoryB,
        string LineA1, string LineA2, string LineB1,
        Account AdminA, Account AdminB,
        Account Hr, Account Supervisor, Account Monitor, Account Devices, Account WorkerUser,
        string Worker1, string Worker2, string WorkerB, string DeptA, string DeptB);

    /// <summary>A complete POST /api/workers body (the Worker ID is generated by the server).</summary>
    private static object NewWorker(string factoryId, string departmentId, string fullName, string? lineId = null,
        string? employeeCode = null, string? workstation = null, string? phone = null, string? grade = null) => new
    {
        factoryId, departmentId, fullName, lineId, workstation, grade,
        employeeCode = employeeCode ?? $"E{Tag()}",
        phone = phone ?? "+8801711000099",
        joinedOn = "2024-01-15",
        designation = "Operator",
    };

    /// <summary>
    /// Creates a user as <paramref name="admin"/>, then signs in and replaces
    /// the temporary password exactly as the person would.
    /// </summary>
    private async Task<Account> CreateUser(string adminToken, string tag, string[] roles, string[] factories, string[]? lines = null)
    {
        var email = $"{tag}-{Tag()}@rbac.test";
        var temp = Pw();
        var created = await Post("/api/users", adminToken, new { email, fullName = $"{tag} user", temporaryPassword = temp, roles, factoryIds = factories, lineIds = lines ?? Array.Empty<string>() });
        Is(HttpStatusCode.Created, created);
        Assert.True(created.Body.GetProperty("mustChangePassword").GetBoolean());
        var first = await Login(email, temp);
        Assert.True(first.User.GetProperty("mustChangePassword").GetBoolean());
        var password = Pw();
        Is(HttpStatusCode.NoContent, await Post("/api/account/password", first.Token, new { currentPassword = temp, newPassword = password }));
        return new Account(created.Id, email, password);
    }

    private static int _lineSeq = Random.Shared.Next(0, 1000);

    private async Task<string> AddLine(string name)
    {
        // "line-N": the shape the legacy QC module accepts. Unique per test run.
        var id = $"line-{Interlocked.Increment(ref _lineSeq) % 1000}";
        await Rag.WithDb(Api, async db =>
        {
            // A previous run may have left this id: reuse it, detached from any factory.
            var existing = await db.ProductionLines.FirstOrDefaultAsync(l => l.Id == id);
            if (existing is null) db.ProductionLines.Add(new ProductionLine { Id = id, Name = $"{name} {id} {Tag()}", Workstations = 20 });
            else
            {
                await db.Workers.Where(w => w.LineId == id).ExecuteUpdateAsync(x => x.SetProperty(w => w.LineId, (string?)null));
                await db.UserLines.Where(a => a.LineId == id).ExecuteDeleteAsync();
                existing.FactoryId = null;
            }
            return await db.SaveChangesAsync();
        });
        return id;
    }

    private async Task<World> BuildWorld()
    {
        var super = await Login(Api.AdminEmail, Api.AdminPassword);
        Assert.Equal(R.SuperAdmin, super.User.GetProperty("role").GetString());
        Assert.Equal("Platform", super.User.GetProperty("scope").GetString());

        // 1. Super Admin creates two factories.
        var a = await Post("/api/factories", super.Token, new { code = $"A{Tag()[..6]}", name = $"Factory A {Tag()}", location = "Gazipur" });
        Is(HttpStatusCode.Created, a);
        var b = await Post("/api/factories", super.Token, new { code = $"B{Tag()[..6]}", name = $"Factory B {Tag()}", location = "Narayanganj" });
        Is(HttpStatusCode.Created, b);
        var (fa, fb) = (a.Id, b.Id);

        // 2. Lines are attached to factories.
        var (la1, la2, lb1) = (await AddLine("A1"), await AddLine("A2"), await AddLine("B1"));
        foreach (var (f, l) in new[] { (fa, la1), (fa, la2), (fb, lb1) })
            Is(HttpStatusCode.OK, await Send(HttpMethod.Put, $"/api/factories/{f}/lines/{l}", super.Token));

        // 3. Super Admin creates a Factory Admin for each factory.
        var adminA = await CreateUser(super.Token, "admin-a", new[] { R.FactoryAdmin }, new[] { fa });
        var adminB = await CreateUser(super.Token, "admin-b", new[] { R.FactoryAdmin }, new[] { fb });

        // 4. The Factory Admin of A creates the other roles in A.
        var sa = (await Login(adminA.Email, adminA.Password)).Token;
        var hr = await CreateUser(sa, "hr", new[] { R.HrOfficer }, new[] { fa });
        var sup = await CreateUser(sa, "supervisor", new[] { R.ProductionSupervisor }, new[] { fa }, new[] { la1 });
        var mon = await CreateUser(sa, "monitor", new[] { R.MonitoringOperator }, new[] { fa });
        var dev = await CreateUser(sa, "devices", new[] { R.DeviceAdmin }, new[] { fa });
        var wu = await CreateUser(sa, "worker", new[] { R.Worker }, new[] { fa });

        // 5. The Factory Admin sets up a department; HR registers workers (no login needed);
        //    the Factory Admin links one to the Worker account.
        var deptA = await Post($"/api/factories/{fa}/departments", sa, new { code = "SEW", name = "Sewing" });
        Is(HttpStatusCode.Created, deptA);
        var hrToken = (await Login(hr.Email, hr.Password)).Token;
        var w1 = await Post("/api/workers", hrToken, NewWorker(fa, deptA.Id, "Rahima Begum", la1, workstation: "WS-01", phone: "+8801711000001", grade: "G3"));
        Is(HttpStatusCode.Created, w1);
        Assert.False(w1.Body.GetProperty("hasAccount").GetBoolean());
        var w2 = await Post("/api/workers", hrToken, NewWorker(fa, deptA.Id, "Karim Uddin", la2, phone: "+8801711000002"));
        Is(HttpStatusCode.Created, w2);
        Is(HttpStatusCode.OK, await Put($"/api/workers/{w1.Id}/account", sa, new { userId = wu.Id }));

        var sb = (await Login(adminB.Email, adminB.Password)).Token;
        var hrB = await CreateUser(sb, "hr-b", new[] { R.HrOfficer }, new[] { fb });
        var deptB = await Post($"/api/factories/{fb}/departments", sb, new { code = "SEW", name = "Sewing" });
        Is(HttpStatusCode.Created, deptB);
        var wb = await Post("/api/workers", (await Login(hrB.Email, hrB.Password)).Token, NewWorker(fb, deptB.Id, "Worker in B", lb1));
        Is(HttpStatusCode.Created, wb);

        return new World(super, fa, fb, la1, la2, lb1, adminA, adminB, hr, sup, mon, dev, wu, w1.Id, w2.Id, wb.Id, deptA.Id, deptB.Id);
    }

    // ─── Tests ─────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Super_admin_builds_a_factory_with_a_factory_admin_who_staffs_every_role()
    {
        var w = await BuildWorld();

        // Each person signs in with exactly their role, scope and permissions.
        var expect = new (Account Who, string Role, string Scope)[]
        {
            (w.AdminA, R.FactoryAdmin, "Factory"), (w.Hr, R.HrOfficer, "Factory"), (w.Supervisor, R.ProductionSupervisor, "Line"),
            (w.Monitor, R.MonitoringOperator, "Factory"), (w.Devices, R.DeviceAdmin, "Factory"), (w.WorkerUser, R.Worker, "Self"),
        };
        foreach (var (who, role, scope) in expect)
        {
            var s = await Login(who.Email, who.Password);
            Assert.Equal(role, s.User.GetProperty("role").GetString());
            Assert.Equal(scope, s.User.GetProperty("scope").GetString());
            Assert.False(s.User.GetProperty("mustChangePassword").GetBoolean());
            Assert.Equal(AccessCatalog.Role(role)!.Permissions.OrderBy(x => x), Strings(s.User.GetProperty("permissions")).OrderBy(x => x));
            Assert.Equal(w.FactoryA, s.User.GetProperty("factories")[0].GetProperty("id").GetString());
        }
        var sup = await Login(w.Supervisor.Email, w.Supervisor.Password);
        Assert.Equal(new[] { w.LineA1 }, Strings(sup.User.GetProperty("lineIds")));

        // The audit log has the whole story.
        var audit = await Get($"/api/audit?factoryId={w.FactoryA}&limit=200", w.Super.Token);
        Is(HttpStatusCode.OK, audit);
        var actions = audit.Body.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        foreach (var action in new[] { "factory.created", "line.attached", "user.created", "worker.created", "worker.account_linked", "auth.login" })
            Assert.Contains(action, actions);
        Assert.DoesNotContain(audit.Raw, Api.AdminPassword);
    }

    [DockerFact]
    public async Task Factory_users_never_see_or_touch_another_factory()
    {
        var w = await BuildWorld();
        var a = (await Login(w.AdminA.Email, w.AdminA.Password)).Token;

        // Only their own factory is listed; the other one is "not found".
        var factories = await Get("/api/factories", a);
        Assert.Equal(new[] { w.FactoryA }, factories.Body.EnumerateArray().Select(f => f.GetProperty("id").GetString()));
        Is(HttpStatusCode.NotFound, await Get($"/api/factories/{w.FactoryB}", a), "factory_not_found");
        Is(HttpStatusCode.NotFound, await Get($"/api/factories/{w.FactoryB}/lines", a));

        // Users: only factory A, never the Super Admin.
        var users = await Get("/api/users?pageSize=100", a);
        var emails = users.Body.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToList();
        Assert.Contains(w.Hr.Email, emails);
        Assert.DoesNotContain(w.AdminB.Email, emails);
        Assert.DoesNotContain(Api.AdminEmail, emails);
        Is(HttpStatusCode.NotFound, await Get($"/api/users/{w.AdminB.Id}", a), "user_not_found");
        Is(HttpStatusCode.NotFound, await Put($"/api/users/{w.AdminB.Id}", a, new { email = w.AdminB.Email, fullName = "hijacked" }));
        Is(HttpStatusCode.NotFound, await Post($"/api/users/{w.AdminB.Id}/status", a, new { isActive = false }));
        AssertEmptyPage(await Get($"/api/users?factoryId={w.FactoryB}", a));

        // Creating users or workers in B, or attaching B's line, is refused.
        Is(HttpStatusCode.Forbidden, await Post("/api/users", a, new { email = $"x-{Tag()}@rbac.test", temporaryPassword = Pw(), roles = new[] { R.Worker }, factoryIds = new[] { w.FactoryB } }), "factory_out_of_scope");
        var hr = (await Login(w.Hr.Email, w.Hr.Password)).Token;
        Is(HttpStatusCode.Forbidden, await Post("/api/workers", hr, NewWorker(w.FactoryB, w.DeptB, "Nope")), "factory_out_of_scope");
        // A department of another factory can't be used to place a worker either.
        Is(HttpStatusCode.BadRequest, await Post("/api/workers", hr, NewWorker(w.FactoryA, w.DeptB, "Nope")), "department_not_in_factory");
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{w.WorkerB}", hr));
        Is(HttpStatusCode.NotFound, await Send(HttpMethod.Put, $"/api/factories/{w.FactoryB}/lines/{w.LineA1}", a));
        Is(HttpStatusCode.Conflict, await Send(HttpMethod.Put, $"/api/factories/{w.FactoryA}/lines/{w.LineB1}", a), "line_in_other_factory");
        var workers = await Get("/api/workers?pageSize=100", hr);
        Assert.DoesNotContain(workers.Body.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetString() == w.WorkerB);

        // Audit: A's admin reads A's entries only.
        var audit = await Get("/api/audit?limit=200", a);
        Is(HttpStatusCode.OK, audit);
        Assert.All(audit.Body.EnumerateArray(), e => Assert.Equal(w.FactoryA, e.GetProperty("factoryId").GetString()));
        Assert.Equal(0, (await Get($"/api/audit?factoryId={w.FactoryB}", a)).Body.GetArrayLength());

        // Forging a factory id into the scope request is refused too.
        Is(HttpStatusCode.Forbidden, await Put($"/api/users/{w.Hr.Id}/scope", a, new { factoryIds = new[] { w.FactoryA, w.FactoryB } }), "factory_out_of_scope");
    }

    [DockerFact]
    public async Task Supervisors_see_and_move_only_workers_on_their_lines()
    {
        var w = await BuildWorld();
        var sup = (await Login(w.Supervisor.Email, w.Supervisor.Password)).Token;

        var list = await Get("/api/workers", sup);
        Is(HttpStatusCode.OK, list);
        Assert.Equal(new[] { w.Worker1 }, list.Body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()));
        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{w.Worker2}", sup));

        var lines = await Get($"/api/factories/{w.FactoryA}/lines", sup);
        Assert.Equal(new[] { w.LineA1 }, lines.Body.EnumerateArray().Select(l => l.GetProperty("id").GetString()));

        // Within the line: allowed. To a line they don't run, or for a worker off their line: refused.
        Is(HttpStatusCode.OK, await Put($"/api/workers/{w.Worker1}/workstation", sup, new { lineId = w.LineA1, workstation = "WS-07" }));
        Is(HttpStatusCode.Forbidden, await Put($"/api/workers/{w.Worker1}/workstation", sup, new { lineId = w.LineA2, workstation = "WS-01" }), "line_out_of_scope");
        Is(HttpStatusCode.NotFound, await Put($"/api/workers/{w.Worker2}/workstation", sup, new { lineId = w.LineA1, workstation = "WS-02" }));

        // Employment details are hidden from a supervisor; no worker or user administration.
        Assert.True(Hidden(list.Body.GetProperty("items")[0], "phone"));
        Is(HttpStatusCode.Forbidden, await Put($"/api/workers/{w.Worker1}", sup, new { fullName = "x", isActive = true }), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Get("/api/users", sup), "permission_denied");

        // QC records for a line they don't run are refused (legacy QC module).
        Is(HttpStatusCode.Forbidden, await Post("/api/qc/flag", sup, new { operation = "sewing", lineId = w.LineA2, defectRatePct = 3.0 }), "line_out_of_scope");
    }

    [DockerFact]
    public async Task A_worker_reaches_only_their_own_profile()
    {
        var w = await BuildWorld();
        var me = (await Login(w.WorkerUser.Email, w.WorkerUser.Password)).Token;

        var mine = await Get("/api/workers/me", me);
        Is(HttpStatusCode.OK, mine);
        Assert.Equal(w.Worker1, mine.Id);
        Assert.Equal("+8801711000001", mine.Body.GetProperty("phone").GetString()); // own details are visible
        Is(HttpStatusCode.OK, await Get($"/api/workers/{w.Worker1}", me));

        Is(HttpStatusCode.NotFound, await Get($"/api/workers/{w.Worker2}", me));
        Is(HttpStatusCode.Forbidden, await Get("/api/workers", me), "permission_denied");
        foreach (var path in new[] { "/api/users", "/api/line-board", "/api/production/orders", "/api/audit", "/api/roles", "/api/qc/inspections" })
            Is(HttpStatusCode.Forbidden, await Get(path, me), "permission_denied");

        var profile = await Get("/api/account/profile", me);
        Is(HttpStatusCode.OK, profile);
        Assert.Equal(w.Worker1, profile.Body.GetProperty("worker").GetProperty("id").GetString());
    }

    [DockerFact]
    public async Task Each_role_is_held_to_its_permissions_on_live_endpoints()
    {
        var w = await BuildWorld();
        var mon = (await Login(w.Monitor.Email, w.Monitor.Password)).Token;
        var dev = (await Login(w.Devices.Email, w.Devices.Password)).Token;
        var hr = (await Login(w.Hr.Email, w.Hr.Password)).Token;

        // Monitoring operator: sees workers and alerts, can't change worker records.
        Is(HttpStatusCode.OK, await Get("/api/floor-alerts", mon));
        var seen = await Get("/api/workers", mon);
        Is(HttpStatusCode.OK, seen);
        Assert.All(seen.Body.GetProperty("items").EnumerateArray(), x => Assert.True(Hidden(x, "phone") && Hidden(x, "grade")));
        Is(HttpStatusCode.Forbidden, await Post("/api/workers", mon, new { factoryId = w.FactoryA, employeeCode = "X1", fullName = "x" }), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Put($"/api/workers/{w.Worker1}", mon, new { fullName = "x", isActive = true }), "permission_denied");

        // Device admin: devices yes, HR records no.
        Is(HttpStatusCode.OK, await Get("/api/sensors/snapshot", dev));
        Is(HttpStatusCode.Forbidden, await Get("/api/workers", dev), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Put($"/api/workers/{w.Worker1}", dev, new { fullName = "x", isActive = true }), "permission_denied");

        // HR: full worker records, no device or user administration.
        var details = await Get($"/api/workers/{w.Worker1}", hr);
        Assert.Equal("G3", details.Body.GetProperty("grade").GetString());
        Is(HttpStatusCode.OK, await Put($"/api/workers/{w.Worker2}", hr, new { fullName = "Karim Uddin", designation = "Helper", phone = "+8801711000002", joinedOn = "2024-01-15", departmentId = w.DeptA, lineId = w.LineA2 }));
        Is(HttpStatusCode.Forbidden, await Post("/api/sensors/ingest", hr, new { }), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Get("/api/users", hr), "permission_denied");

        // Factory Admin: users and roles of the factory; platform settings stay closed.
        var a = (await Login(w.AdminA.Email, w.AdminA.Password)).Token;
        Is(HttpStatusCode.OK, await Get("/api/roles", a));
        Is(HttpStatusCode.Forbidden, await Post("/api/settings/llm", a, new { clearAll = true }), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Post("/api/factories", a, new { code = "ZZ", name = "x" }), "permission_denied");
    }

    [DockerFact]
    public async Task Nobody_can_grant_more_than_their_own_authority()
    {
        var w = await BuildWorld();
        var a = (await Login(w.AdminA.Email, w.AdminA.Password)).Token;

        // A Factory Admin can't create or promote Factory Admins or Super Admins…
        foreach (var role in new[] { R.FactoryAdmin, R.SuperAdmin })
        {
            Is(HttpStatusCode.Forbidden, await Post("/api/users", a, new { email = $"esc-{Tag()}@rbac.test", temporaryPassword = Pw(), roles = new[] { role }, factoryIds = new[] { w.FactoryA } }), "role_not_grantable");
            Is(HttpStatusCode.Forbidden, await Put($"/api/users/{w.Hr.Id}/roles", a, new { roles = new[] { role } }), "role_not_grantable");
        }
        // …can't change their own roles or deactivate themselves…
        Is(HttpStatusCode.Forbidden, await Put($"/api/users/{w.AdminA.Id}/roles", a, new { roles = new[] { R.SuperAdmin } }), "cannot_change_own_roles");
        Is(HttpStatusCode.Forbidden, await Post($"/api/users/{w.AdminA.Id}/status", a, new { isActive = false }), "cannot_change_own_status");
        // …and can't manage a peer Factory Admin (who isn't even in their factory) or the Super Admin.
        var superId = w.Super.User.GetProperty("id").GetString();
        Is(HttpStatusCode.NotFound, await Post($"/api/users/{superId}/reset-password", a, new { temporaryPassword = Pw() }));
        Is(HttpStatusCode.NotFound, await Put($"/api/users/{superId}/roles", a, new { roles = Array.Empty<string>() }));

        // A second Factory Admin in the same factory is out of reach for the first one.
        var peer = await CreateUser(w.Super.Token, "admin-a2", new[] { R.FactoryAdmin }, new[] { w.FactoryA });
        Is(HttpStatusCode.Forbidden, await Post($"/api/users/{peer.Id}/status", a, new { isActive = false }), "insufficient_authority");
        var detail = await Get($"/api/users/{peer.Id}", a);
        Assert.False(detail.Body.GetProperty("can").GetProperty("changeStatus").GetBoolean());

        // Unknown roles are rejected by validation.
        Is(HttpStatusCode.BadRequest, await Put($"/api/users/{w.Hr.Id}/roles", a, new { roles = new[] { "Root" } }));

        // Lower roles have no user administration at all.
        var hr = (await Login(w.Hr.Email, w.Hr.Password)).Token;
        Is(HttpStatusCode.Forbidden, await Post("/api/users", hr, new { email = $"h-{Tag()}@rbac.test", temporaryPassword = Pw() }), "permission_denied");
        Is(HttpStatusCode.Forbidden, await Put($"/api/users/{w.Hr.Id}/roles", hr, new { roles = new[] { R.FactoryAdmin } }), "permission_denied");

        // Nothing changed.
        var hrNow = await Login(w.Hr.Email, w.Hr.Password);
        Assert.Equal(new[] { R.HrOfficer }, Strings(hrNow.User.GetProperty("roles")));
    }

    [DockerFact]
    public async Task Role_changes_and_deactivation_take_effect_immediately_and_are_audited()
    {
        var w = await BuildWorld();
        var a = (await Login(w.AdminA.Email, w.AdminA.Password)).Token;
        var mon = await Login(w.Monitor.Email, w.Monitor.Password);
        Is(HttpStatusCode.Forbidden, await Post("/api/workers", mon.Token, new { factoryId = w.FactoryA, employeeCode = $"E{Tag()}", fullName = "x" }), "permission_denied");

        // Grant HR on top of Monitoring Operator.
        var changed = await Put($"/api/users/{w.Monitor.Id}/roles", a, new { roles = new[] { R.MonitoringOperator, R.HrOfficer } });
        Is(HttpStatusCode.OK, changed);
        Assert.Equal(new[] { R.HrOfficer, R.MonitoringOperator }.OrderBy(x => x), Strings(changed.Body.GetProperty("roles")).OrderBy(x => x));
        // The old token is now stale (401); a refresh carries the new permissions.
        Is(HttpStatusCode.Unauthorized, await Get("/api/floor-alerts", mon.Token));
        var fresh = await Refresh(mon);
        Assert.Contains(Permissions.WorkersManage, Strings(fresh.User.GetProperty("permissions")));
        Is(HttpStatusCode.Created, await Post("/api/workers", fresh.Token, NewWorker(w.FactoryA, w.DeptA, "New hire")));

        // Deactivate: tokens and refresh stop working at once; sign-in is refused with a clear reason.
        Is(HttpStatusCode.OK, await Post($"/api/users/{w.Monitor.Id}/status", a, new { isActive = false, reason = "left the company" }));
        Is(HttpStatusCode.Unauthorized, await Get("/api/floor-alerts", fresh.Token));
        Is(HttpStatusCode.Unauthorized, await Send(HttpMethod.Post, "/api/auth/refresh", cookie: fresh.Refresh));
        Is(HttpStatusCode.Forbidden, await Post("/api/auth/login", null, new { email = w.Monitor.Email, password = w.Monitor.Password }), "account_deactivated");
        // The wrong password still gets the generic 401 (no account discovery).
        Is(HttpStatusCode.Unauthorized, await Post("/api/auth/login", null, new { email = w.Monitor.Email, password = "wrong-" + Pw() }));

        var inactive = await Get("/api/users?status=inactive&pageSize=100", a);
        Assert.Contains(inactive.Body.GetProperty("items").EnumerateArray(), u => u.GetProperty("id").GetString() == w.Monitor.Id);

        // Reactivate: sign-in works again.
        Is(HttpStatusCode.OK, await Post($"/api/users/{w.Monitor.Id}/status", a, new { isActive = true }));
        await Login(w.Monitor.Email, w.Monitor.Password);

        var activity = await Get($"/api/users/{w.Monitor.Id}/activity", a);
        var actions = activity.Body.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        foreach (var action in new[] { "user.created", "user.roles_changed", "user.deactivated", "user.activated", "auth.login", "account.password_changed" })
            Assert.Contains(action, actions);
        var roleChange = activity.Body.EnumerateArray().First(e => e.GetProperty("action").GetString() == "user.roles_changed");
        Assert.Equal(R.MonitoringOperator, roleChange.GetProperty("fromState").GetString());
        Assert.Equal($"{R.HrOfficer},{R.MonitoringOperator}", roleChange.GetProperty("toState").GetString());
        Assert.Equal(w.AdminA.Email, roleChange.GetProperty("actorEmail").GetString());
    }

    [DockerFact]
    public async Task An_administrator_reset_forces_a_password_change_and_ends_sessions()
    {
        var w = await BuildWorld();
        var a = (await Login(w.AdminA.Email, w.AdminA.Password)).Token;
        var dev = await Login(w.Devices.Email, w.Devices.Password);

        var temp = Pw();
        var reset = await Post($"/api/users/{w.Devices.Id}/reset-password", a, new { temporaryPassword = temp });
        Is(HttpStatusCode.OK, reset);
        Assert.DoesNotContain(temp, reset.Raw);
        Is(HttpStatusCode.Unauthorized, await Get("/api/sensors/snapshot", dev.Token));
        Is(HttpStatusCode.Unauthorized, await Send(HttpMethod.Post, "/api/auth/refresh", cookie: dev.Refresh));
        Is(HttpStatusCode.Unauthorized, await Post("/api/auth/login", null, new { email = w.Devices.Email, password = w.Devices.Password }));

        // With the temporary password: signed in, but nothing works until it's changed.
        var t = await Login(w.Devices.Email, temp);
        Assert.True(t.User.GetProperty("mustChangePassword").GetBoolean());
        Is(HttpStatusCode.Forbidden, await Get("/api/sensors/snapshot", t.Token), "password_change_required");
        Is(HttpStatusCode.BadRequest, await Post("/api/account/password", t.Token, new { currentPassword = "wrong-" + temp, newPassword = Pw() }), "current_password_incorrect");
        var mine = Pw();
        Is(HttpStatusCode.NoContent, await Post("/api/account/password", t.Token, new { currentPassword = temp, newPassword = mine }));
        var after = await Refresh(t);
        Assert.False(after.User.GetProperty("mustChangePassword").GetBoolean());
        Is(HttpStatusCode.OK, await Get("/api/sensors/snapshot", after.Token));

        Assert.True(await Rag.WithDb(Api, db => db.AuditLog.AnyAsync(e => e.Action == "user.password_reset_by_admin" && e.EntityId == w.Devices.Id)));
        AiSettingsTestKit.AssertNoSecret(temp, Api.Logs.Lines, "the log");
    }

    [DockerFact]
    public async Task Super_admins_cannot_change_themselves_and_the_last_active_super_admin_is_kept()
    {
        var super = await Login(Api.AdminEmail, Api.AdminPassword);
        var superId = Guid.Parse(super.User.GetProperty("id").GetString()!);
        Is(HttpStatusCode.Forbidden, await Post($"/api/users/{superId}/status", super.Token, new { isActive = false }), "cannot_change_own_status");
        Is(HttpStatusCode.Forbidden, await Put($"/api/users/{superId}/roles", super.Token, new { roles = Array.Empty<string>() }), "cannot_change_own_roles");

        var h1 = await CreateUser(super.Token, "super-1", new[] { R.SuperAdmin }, Array.Empty<string>());
        var h2 = await CreateUser(super.Token, "super-2", new[] { R.SuperAdmin }, Array.Empty<string>());
        var (id1, id2) = (Guid.Parse(h1.Id), Guid.Parse(h2.Id));
        var t1 = (await Login(h1.Email, h1.Password)).Token;
        var t2 = (await Login(h2.Email, h2.Password)).Token;

        // Park every other active Super Admin (restored below), leaving h1 and h2.
        var parked = await Rag.WithDb(Api, db => db.Users
            .Where(u => u.IsActive && u.Id != id1 && u.Id != id2 && u.Roles.Any(r => r.RoleId == R.SuperAdmin))
            .Select(u => u.Id).ToListAsync());
        var sessions = Api.Services.GetRequiredService<BunonBrain.Application.Abstractions.Interfaces.ISessionValidator>();
        try
        {
            await Rag.WithDb(Api, db => db.Users.Where(u => parked.Contains(u.Id)).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false)));

            // Both try to deactivate the other at the same moment: the row lock lets exactly one win,
            // so there is always an active Super Admin left.
            var results = await Task.WhenAll(
                Post($"/api/users/{id2}/status", t1, new { isActive = false }),
                Post($"/api/users/{id1}/status", t2, new { isActive = false }));
            Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
            Assert.All(results.Where(r => r.Status != HttpStatusCode.OK), r =>
                Assert.True(r.Status == HttpStatusCode.Unauthorized || r.Code == "last_super_admin", r.Raw));
            var active = await Rag.WithDb(Api, db => db.Users.CountAsync(u => (u.Id == id1 || u.Id == id2) && u.IsActive));
            Assert.Equal(1, active);
            var survivor = await Rag.WithDb(Api, db => db.Users.Where(u => (u.Id == id1 || u.Id == id2) && u.IsActive).Select(u => u.Id).SingleAsync());

            // Even an administrator acting with a valid platform context can't remove the last one.
            var service = Api.Services.CreateScope().ServiceProvider.GetRequiredService<BunonBrain.Application.Abstractions.Interfaces.IUserAdminService>();
            var platform = new AccessContext(Guid.NewGuid(), "system@tests.local", new[] { R.SuperAdmin },
                AccessCatalog.Role(R.SuperAdmin)!.Permissions, Array.Empty<Guid>(), Array.Empty<string>());
            var deactivate = await service.SetStatusAsync(platform, survivor, new(false, null), null, default);
            Assert.Equal("last_super_admin", deactivate.Code);
            var demote = await service.AssignRolesAsync(platform, survivor, new(new[] { R.Worker }), null, default);
            Assert.Equal("last_super_admin", demote.Code);
            Assert.True(await Rag.WithDb(Api, db => db.UserRoles.AnyAsync(r => r.UserId == survivor && r.RoleId == R.SuperAdmin)));
        }
        finally
        {
            await Rag.WithDb(Api, db => db.Users.Where(u => parked.Contains(u.Id)).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, true)));
            foreach (var id in parked) sessions.Invalidate(id);
            await Rag.WithDb(Api, db => db.UserRoles.Where(r => (r.UserId == id1 || r.UserId == id2) && r.RoleId == R.SuperAdmin).ExecuteDeleteAsync());
        }
    }

    [DockerFact]
    public async Task User_list_supports_search_filter_sort_and_pagination()
    {
        var w = await BuildWorld();
        var a = (await Login(w.AdminA.Email, w.AdminA.Password)).Token;

        var page1 = await Get("/api/users?pageSize=2&page=1&sort=email", a);
        Is(HttpStatusCode.OK, page1);
        Assert.Equal(2, page1.Body.GetProperty("items").GetArrayLength());
        var total = page1.Body.GetProperty("total").GetInt32();
        Assert.True(total >= 6, $"total {total}");
        var page2 = await Get("/api/users?pageSize=2&page=2&sort=email", a);
        var e1 = page1.Body.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToList();
        var e2 = page2.Body.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToList();
        Assert.Empty(e1.Intersect(e2));
        Assert.True(string.CompareOrdinal(e1[^1], e2[0]) < 0);

        var search = await Get($"/api/users?search={Uri.EscapeDataString(w.Hr.Email[..12])}", a);
        Assert.Contains(search.Body.GetProperty("items").EnumerateArray(), u => u.GetProperty("email").GetString() == w.Hr.Email);
        var byRole = await Get($"/api/users?role={R.ProductionSupervisor}&pageSize=100", a);
        Assert.All(byRole.Body.GetProperty("items").EnumerateArray(), u => Assert.Contains(R.ProductionSupervisor, Strings(u.GetProperty("roles"))));
        var desc = await Get("/api/users?sort=-email&pageSize=100", a);
        var emails = desc.Body.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()!).ToList();
        Assert.Equal(emails.OrderByDescending(e => e, StringComparer.Ordinal), emails);
        // Wildcards in the search are literal.
        Assert.Equal(0, (await Get("/api/users?search=%25%25%25", a)).Body.GetProperty("total").GetInt32());
        Assert.DoesNotContain("passwordHash", desc.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", desc.Raw, StringComparison.OrdinalIgnoreCase);
    }

    [DockerFact]
    public async Task Super_admin_manages_factories_and_deactivating_one_removes_its_members_access()
    {
        var super = await Login(Api.AdminEmail, Api.AdminPassword);
        var f = await Post("/api/factories", super.Token, new { code = $"C{Tag()[..6]}", name = $"Factory C {Tag()}" });
        Is(HttpStatusCode.Created, f);
        Is(HttpStatusCode.Conflict, await Post("/api/factories", super.Token, new { code = f.Body.GetProperty("code").GetString(), name = $"Other {Tag()}" }), "factory_code_taken");
        Is(HttpStatusCode.BadRequest, await Post("/api/factories", super.Token, new { code = "bad code!", name = "x" }));
        Is(HttpStatusCode.OK, await Put($"/api/factories/{f.Id}", super.Token, new { name = $"Factory C renamed {Tag()}", location = "Savar" }));

        var admin = await CreateUser(super.Token, "admin-c", new[] { R.FactoryAdmin }, new[] { f.Id });
        var s = await Login(admin.Email, admin.Password);
        Is(HttpStatusCode.OK, await Get("/api/users", s.Token));

        Is(HttpStatusCode.OK, await Post($"/api/factories/{f.Id}/status", super.Token, new { isActive = false }));
        Is(HttpStatusCode.Unauthorized, await Get("/api/users", s.Token));
        var after = await Login(admin.Email, admin.Password);
        Assert.Equal(0, after.User.GetProperty("factories").GetArrayLength());
        Assert.Empty(Strings(after.User.GetProperty("permissions")).Where(p => p != Permissions.SelfView && p != Permissions.NotificationsView));
        Is(HttpStatusCode.Forbidden, await Get("/api/users", after.Token), "permission_denied");

        Is(HttpStatusCode.OK, await Post($"/api/factories/{f.Id}/status", super.Token, new { isActive = true }));
        var restored = await Login(admin.Email, admin.Password);
        Is(HttpStatusCode.OK, await Get("/api/users", restored.Token));
        Assert.True(await Rag.WithDb(Api, db => db.AuditLog.AnyAsync(e => e.Action == "factory.deactivated" && e.EntityId == f.Id)));
    }

    [DockerFact]
    public async Task Users_cannot_be_created_with_bad_input_or_duplicate_email()
    {
        var w = await BuildWorld();
        var a = (await Login(w.AdminA.Email, w.AdminA.Password)).Token;
        Is(HttpStatusCode.BadRequest, await Post("/api/users", a, new { email = "not-an-email", temporaryPassword = Pw(), factoryIds = new[] { w.FactoryA } }));
        Is(HttpStatusCode.BadRequest, await Post("/api/users", a, new { email = $"s-{Tag()}@rbac.test", temporaryPassword = "short", factoryIds = new[] { w.FactoryA } }));
        Is(HttpStatusCode.Conflict, await Post("/api/users", a, new { email = w.Hr.Email.ToUpperInvariant(), temporaryPassword = Pw(), roles = new[] { R.Worker }, factoryIds = new[] { w.FactoryA } }), "email_taken");
        Is(HttpStatusCode.BadRequest, await Post("/api/users", a, new { email = $"l-{Tag()}@rbac.test", temporaryPassword = Pw(), roles = new[] { R.ProductionSupervisor }, factoryIds = new[] { w.FactoryA }, lineIds = new[] { w.LineB1 } }), "line_not_in_factory");
        var dupCode = await Post("/api/workers", (await Login(w.Hr.Email, w.Hr.Password)).Token,
            NewWorker(w.FactoryA, w.DeptA, "dup", employeeCode: (await Get($"/api/workers/{w.Worker1}", a)).Body.GetProperty("employeeCode").GetString()));
        Is(HttpStatusCode.Conflict, dupCode, "employee_code_taken");
    }
}

