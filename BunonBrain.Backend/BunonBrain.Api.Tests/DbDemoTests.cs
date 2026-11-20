using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Access;
using BunonBrain.Application.Demo;
using BunonBrain.Infrastructure.Demo;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The interactive demo end to end on a disposable PostgreSQL: the demo host
/// claims its empty database, the reset builds the baseline through the real
/// services, and a reviewer signs in once, switches roles, is limited by each
/// role's permissions, and loses access on sign-out and on the next reset.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbDemoTests
{
    private readonly PostgresStack _stack;
    public DbDemoTests(PostgresStack stack) { _stack = stack; }

    private static readonly SemaphoreSlim Baseline = new(1, 1);
    private static bool _seeded;

    private async Task<DbApiFactory> DemoAsync()
    {
        var api = _stack.Demo;
        await Baseline.WaitAsync();
        try
        {
            if (!_seeded)
            {
                Assert.True(await api.Services.GetRequiredService<IDemoResetService>().ResetAsync("test baseline", CancellationToken.None));
                _seeded = true;
            }
        }
        finally { Baseline.Release(); }
        return api;
    }

    /// <summary>Read the demo database (the demo route picks it for this scope only).</summary>
    private static async Task<T> InDemoDb<T>(DbApiFactory api, Func<BunonBrainDbContext, Task<T>> read)
    {
        using (DemoRoute.Enter())
        {
            await using var scope = api.Services.CreateAsyncScope();
            return await read(scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>());
        }
    }

    /// <summary>Read the production database of the same host.</summary>
    private static async Task<T> InProductionDb<T>(DbApiFactory api, Func<BunonBrainDbContext, Task<T>> read)
    {
        Assert.False(DemoRoute.IsDemo);
        await using var scope = api.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>());
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private sealed record Reply(HttpStatusCode Status, JsonElement Body, string? Cookie);

    private static async Task<Reply> Send(DbApiFactory api, HttpMethod method, string path, object? body = null, string? cookie = null, string? token = null)
    {
        using var client = api.RawClient();
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = Json(body);
        if (cookie is not null) req.Headers.Add("Cookie", $"{AuthCookieSettings.CookieName}={cookie}");
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        using var res = await client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        var json = text.Length > 0 && (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
            ? JsonDocument.Parse(text).RootElement.Clone() : default;
        string? newCookie = null;
        if (res.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var v in values)
                if (v.StartsWith(AuthCookieSettings.CookieName + "=", StringComparison.Ordinal))
                {
                    var raw = v[(AuthCookieSettings.CookieName.Length + 1)..].Split(';')[0];
                    newCookie = raw.Length == 0 ? null : raw;
                }
        return new Reply(res.StatusCode, json, newCookie);
    }

    private static async Task<string> SignInAsync(DbApiFactory api)
    {
        var r = await Send(api, HttpMethod.Post, "/api/auth/demo/login", new { email = PostgresStack.DemoLoginEmail, password = PostgresStack.DemoLoginPassword });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Cookie!;
    }

    private static async Task<(string Token, string Cookie, JsonElement User)> SwitchAsync(DbApiFactory api, string cookie, string role)
    {
        var r = await Send(api, HttpMethod.Post, "/api/auth/demo/switch", new { roleId = role }, cookie);
        Assert.True(r.Status == HttpStatusCode.OK, $"switch to {role}: {r.Status} {r.Body}");
        return (r.Body.GetProperty("accessToken").GetString()!, r.Cookie!, r.Body.GetProperty("user"));
    }

    [DockerFact]
    public async Task The_baseline_is_complete_and_connected()
    {
        var api = await DemoAsync();
        using var route = DemoRoute.Enter();   // every read below is on the demo database
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();

        var env = await db.DemoEnvironment.SingleAsync();
        Assert.Equal(DemoDatabase.StatusReady, env.Status);
        Assert.Equal(DemoSeeder.BaselineVersion, env.BaselineVersion);
        Assert.NotNull(env.NextResetAt);

        // One account per role, all active, none able to sign in with a password.
        var ids = DemoIdentities.All.Select(i => i.UserId).ToList();
        Assert.Equal(ids.Count, await db.Users.CountAsync(u => ids.Contains(u.Id) && u.IsActive));
        var factory = await db.Factories.SingleAsync(f => f.Code == DemoSeeder.FactoryCode);

        Assert.Equal(DemoSeeder.Lines.Length, await db.ProductionLines.CountAsync(l => l.FactoryId == factory.Id));
        Assert.True(await db.Workers.CountAsync(w => w.FactoryId == factory.Id) >= 40);
        Assert.True(await db.WorkerCards.CountAsync(c => c.FactoryId == factory.Id) >= 40);
        Assert.True(await db.Assignments.CountAsync() >= 30);
        Assert.True(await db.AttendanceSessions.CountAsync() >= 150, "attendance history");
        Assert.True(await db.ProductionOutputEvents.CountAsync() >= 100, "hourly output history");
        Assert.True(await db.OvertimeRequests.CountAsync(o => o.FactoryId == factory.Id) >= 5);
        Assert.True(await db.ProductionOrders.CountAsync() >= 4);
        Assert.True(await db.ProductionStyles.CountAsync() >= 4);
        Assert.True(await db.ProductionTargets.CountAsync(t => t.FactoryId == factory.Id) >= 1);
        Assert.True(await db.MerchOrders.CountAsync() >= 4);
        Assert.True(await db.InventoryItems.CountAsync(i => i.FactoryId == factory.Id) >= 10);
        Assert.True(await db.Cameras.CountAsync(c => c.FactoryId == factory.Id) >= 4);
        Assert.True(await db.Devices.CountAsync(d => d.FactoryId == factory.Id) >= 2);
        Assert.True(await db.FactoryDocuments.CountAsync(d => d.FactoryId == factory.Id) >= 5, "documents");

        // The Worker account is a real worker; the supervisor has two lines.
        var workerAccount = DemoIdentities.ForRole(SystemRoles.Worker)!.UserId;
        Assert.True(await db.Workers.AnyAsync(w => w.UserId == workerAccount));
        Assert.Equal(2, await db.UserLines.CountAsync(l => l.UserId == DemoIdentities.ForRole(SystemRoles.ProductionSupervisor)!.UserId));

        // Every optional step ran (the log names any that was skipped).
        var skipped = api.Logs.Lines.Where(l => l.Contains("Demo seed step", StringComparison.Ordinal)).ToList();
        Assert.True(skipped.Count == 0, string.Join(Environment.NewLine, skipped));
    }

    [DockerFact]
    public async Task One_sign_in_then_any_role_without_signing_in_again()
    {
        var api = await DemoAsync();

        var wrong = await Send(api, HttpMethod.Post, "/api/auth/demo/login", new { email = PostgresStack.DemoLoginEmail, password = "not-the-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Null(wrong.Cookie);

        var login = await Send(api, HttpMethod.Post, "/api/auth/demo/login", new { email = PostgresStack.DemoLoginEmail, password = PostgresStack.DemoLoginPassword });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var roles = login.Body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("roleId").GetString()).ToList();
        Assert.Equal(AccessCatalog.AllRoles.Select(r => r.Id).OrderBy(x => x), roles.OrderBy(x => x));
        var cookie = login.Cookie!;

        // Before a role is chosen there is no access token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, "/api/auth/refresh", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, "/api/auth/demo/switch", new { roleId = "Admin" }, cookie)).Status);

        // Factory manager: the factory's workers.
        var (managerToken, c1, manager) = await SwitchAsync(api, cookie, SystemRoles.FactoryManager);
        Assert.Equal(SystemRoles.FactoryManager, manager.GetProperty("role").GetString());
        Assert.True(manager.TryGetProperty("demoSessionExpiresAt", out _));
        var workers = await Send(api, HttpMethod.Get, "/api/workers?pageSize=5", token: managerToken);
        Assert.Equal(HttpStatusCode.OK, workers.Status);
        Assert.True(workers.Body.GetProperty("total").GetInt32() >= 40);

        // Switch to Worker with the same session: the manager token stops working at once.
        var (workerToken, c2, _) = await SwitchAsync(api, c1, SystemRoles.Worker);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Get, "/api/workers?pageSize=5", token: managerToken)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, HttpMethod.Get, "/api/workers?pageSize=5", token: workerToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Get, "/api/workers/me", token: workerToken)).Status);

        // The old cookie was rotated away; the new one refreshes as the current role.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, "/api/auth/refresh", cookie: cookie)).Status);
        var refreshed = await Send(api, HttpMethod.Post, "/api/auth/refresh", cookie: c2);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal(SystemRoles.Worker, refreshed.Body.GetProperty("user").GetProperty("role").GetString());
        var c3 = refreshed.Cookie!;

        var session = await Send(api, HttpMethod.Get, "/api/auth/demo/session", cookie: c3);
        Assert.Equal(SystemRoles.Worker, session.Body.GetProperty("currentRoleId").GetString());

        // Sign-out ends the session: refresh and the current token both stop.
        var token = refreshed.Body.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Post, "/api/auth/logout", cookie: c3)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, "/api/auth/refresh", cookie: c3)).Status);
        await Task.Delay(DemoSessionService.ActiveCacheFor + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Get, "/api/workers/me", token: token)).Status);

        var actions = await InDemoDb(api, db => db.AuditLog.Where(a => a.EntityType == DemoSessionService.EntityDemoSession).Select(a => a.Action).ToListAsync());
        Assert.Contains("demo.login", actions);
        Assert.Contains("demo.role_selected", actions);
        Assert.Contains("demo.session_revoked", actions);
    }

    [DockerFact]
    public async Task Reviewers_can_create_and_update_but_not_break_the_demo()
    {
        var api = await DemoAsync();
        var cookie = await SignInAsync(api);
        var (admin, _, user) = await SwitchAsync(api, cookie, SystemRoles.FactoryAdmin);
        var factoryId = user.GetProperty("factories")[0].GetProperty("id").GetString();

        // Create works, through the normal permission checks, in the demo database only.
        var code = "R" + Random.Shared.Next(1000, 9999);
        var created = await Send(api, HttpMethod.Post, $"/api/factories/{factoryId}/departments", new { code, name = "Reviewer dept" }, token: admin);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.True(await InDemoDb(api, db => db.Departments.AnyAsync(d => d.Code == code)));
        Assert.False(await InProductionDb(api, db => db.Departments.AnyAsync(d => d.Code == code)));

        // Destructive and account-level operations are refused.
        var hr = DemoIdentities.ForRole(SystemRoles.HrOfficer)!.UserId;
        var roles = await Send(api, HttpMethod.Put, $"/api/users/{hr}/roles", new { roles = new[] { SystemRoles.Worker } }, token: admin);
        Assert.Equal(HttpStatusCode.Forbidden, roles.Status);
        Assert.Equal("demo_account_protected", roles.Body.GetProperty("code").GetString());
        var password = await Send(api, HttpMethod.Post, "/api/account/password", new { currentPassword = "x", newPassword = "y-long-enough-1" }, token: admin);
        Assert.Equal(HttpStatusCode.Forbidden, password.Status);
        var factory = await Send(api, HttpMethod.Post, $"/api/factories/{factoryId}/status", new { isActive = false, reason = "x" }, token: admin);
        Assert.Equal(HttpStatusCode.Forbidden, factory.Status);
        Assert.Equal("demo_ai_off", (await Send(api, HttpMethod.Post, "/api/ask", new { question = "hi" }, token: admin)).Body.GetProperty("code").GetString());
    }

    [DockerFact]
    public async Task Production_and_the_demo_share_the_site_but_never_the_data()
    {
        var api = await DemoAsync();

        // Real users sign in normally on the same host, against the production database.
        var login = await Send(api, HttpMethod.Post, "/api/auth/login", new { email = api.AdminEmail, password = api.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var prodToken = login.Body.GetProperty("accessToken").GetString()!;
        var factories = await Send(api, HttpMethod.Get, "/api/factories", token: prodToken);
        Assert.Equal(HttpStatusCode.OK, factories.Status);
        Assert.DoesNotContain(DemoSeeder.FactoryCode, factories.Body.ToString());

        // Each database holds only its own accounts and data.
        Assert.False(await InProductionDb(api, db => db.Factories.AnyAsync(f => f.Code == DemoSeeder.FactoryCode)));
        Assert.False(await InProductionDb(api, db => db.DemoEnvironment.AnyAsync()));
        var demoIds = DemoIdentities.All.Select(i => i.UserId).ToList();
        Assert.False(await InProductionDb(api, db => db.Users.AnyAsync(u => demoIds.Contains(u.Id))));
        Assert.False(await InDemoDb(api, db => db.Users.AnyAsync(u => u.Email == api.AdminEmail)));

        // The production cookie still refreshes as production while demo sessions exist.
        var refreshed = await Send(api, HttpMethod.Post, "/api/auth/refresh", cookie: login.Cookie);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.False(refreshed.Body.GetProperty("user").TryGetProperty("demoSessionExpiresAt", out _));

        // A demo token reads the demo database only.
        var cookie = await SignInAsync(api);
        var (demoToken, _, _) = await SwitchAsync(api, cookie, SystemRoles.SuperAdmin);
        var demoFactories = await Send(api, HttpMethod.Get, "/api/factories", token: demoToken);
        Assert.Equal(HttpStatusCode.OK, demoFactories.Status);
        Assert.Contains(DemoSeeder.FactoryCode, demoFactories.Body.ToString());
        Assert.DoesNotContain(api.AdminEmail, (await Send(api, HttpMethod.Get, "/api/users?pageSize=100", token: demoToken)).Body.ToString());
    }

    [DockerFact]
    public async Task Operator_key_lists_and_revokes_sessions()
    {
        var api = await DemoAsync();
        var cookie = await SignInAsync(api);
        var (token, c1, _) = await SwitchAsync(api, cookie, SystemRoles.MonitoringOperator);

        using var client = api.RawClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/demo/admin/sessions")).StatusCode);
        client.DefaultRequestHeaders.Add(BunonBrain.Api.Controllers.DemoAdminController.KeyHeader, PostgresStack.DemoAdminKey);
        var list = JsonDocument.Parse(await client.GetStringAsync("/api/demo/admin/sessions")).RootElement;
        var mine = list.EnumerateArray().First(s => s.GetProperty("currentRoleId").GetString() == SystemRoles.MonitoringOperator).GetProperty("id").GetString();

        var revoke = await client.PostAsync($"/api/demo/admin/sessions/{mine}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, "/api/auth/refresh", cookie: c1)).Status);
        await Task.Delay(DemoSessionService.ActiveCacheFor + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Get, "/api/monitoring/alerts", token: token)).Status);
    }

    [DockerFact]
    public async Task The_daily_reset_restores_the_baseline_and_ends_every_session()
    {
        var api = await DemoAsync();
        var cookie = await SignInAsync(api);
        var (admin, c1, user) = await SwitchAsync(api, cookie, SystemRoles.FactoryAdmin);
        var factoryId = user.GetProperty("factories")[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Created,
            (await Send(api, HttpMethod.Post, $"/api/factories/{factoryId}/floors", new { code = "RX", name = "Reviewer floor" }, token: admin)).Status);

        var workersBefore = await InDemoDb(api, db => db.Workers.CountAsync());
        var productionUsersBefore = await InProductionDb(api, db => db.Users.CountAsync());

        Assert.True(await api.Services.GetRequiredService<IDemoResetService>().ResetAsync("test reset", CancellationToken.None));

        Assert.False(await InDemoDb(api, db => db.ProductionFloors.AnyAsync(f => f.Code == "RX")));
        Assert.Equal(workersBefore, await InDemoDb(api, db => db.Workers.CountAsync()));
        Assert.Equal(0, await InDemoDb(api, db => db.DemoSessions.CountAsync()));
        Assert.Equal("W-001", await InDemoDb(api, async db => (await db.Workers.OrderBy(w => w.WorkerNumber).FirstAsync()).WorkerCode));
        Assert.Equal(DemoDatabase.StatusReady, await InDemoDb(api, async db => (await db.DemoEnvironment.SingleAsync()).Status));
        // The reset never touches production.
        Assert.Equal(productionUsersBefore, await InProductionDb(api, db => db.Users.CountAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, "/api/auth/refresh", cookie: c1)).Status);
    }

    [DockerFact]
    public async Task An_interrupted_seed_resumes_without_emptying_the_database()
    {
        var api = await DemoAsync();
        // Simulate an instance that stopped half-way: a seed in progress, with a record the seed doesn't make.
        await InDemoDb(api, async db =>
        {
            var env = await db.DemoEnvironment.SingleAsync();
            env.Status = DemoDatabase.StatusSeeding;
            env.NextResetAt = DateTimeOffset.UtcNow.AddHours(6);
            db.Departments.Add(new BunonBrain.Domain.Entities.Department
            {
                FactoryId = (await db.Factories.SingleAsync(f => f.Code == DemoSeeder.FactoryCode)).Id, Code = "RESUME", Name = "Kept by a resume",
            });
            return await db.SaveChangesAsync();
        });
        var workers = await InDemoDb(api, db => db.Workers.CountAsync());
        var punches = await InDemoDb(api, db => db.AttendanceEvents.CountAsync());

        Assert.True(await api.Services.GetRequiredService<IDemoResetService>().ResetAsync("test resume", CancellationToken.None, resume: true));

        Assert.True(await InDemoDb(api, db => db.Departments.AnyAsync(d => d.Code == "RESUME")));   // nothing was emptied
        Assert.Equal(workers, await InDemoDb(api, db => db.Workers.CountAsync()));                   // nothing was added twice
        Assert.Equal(punches, await InDemoDb(api, db => db.AttendanceEvents.CountAsync()));
        Assert.Equal(DemoDatabase.StatusReady, await InDemoDb(api, async db => (await db.DemoEnvironment.SingleAsync()).Status));
    }

    [DockerFact]
    public async Task A_database_with_real_accounts_is_never_claimed()
    {
        // The Local test database has the seeded admin/viewer/operator accounts.
        var local = _stack.Local;
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoDatabase.EnsureClaimedAsync(db, NullLogger.Instance, CancellationToken.None));
        Assert.Contains("not a demo database", ex.Message);
        Assert.False(await db.DemoEnvironment.AnyAsync());

        // And the reset refuses a database without the marker.
        var reset = new DemoDatabase(local.Services, DemoOptions.FromEnvironment(k => k switch
        {
            "DEMO_MODE" => "true", "DEMO_LOGIN_EMAIL" => "judge@x.test", "DEMO_LOGIN_PASSWORD" => "long-enough-pw", _ => null,
        }), local.Services.GetRequiredService<BunonBrain.Application.Abstractions.Interfaces.ISessionValidator>(),
            NullLogger<DemoDatabase>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reset.ResetAsync("must refuse", CancellationToken.None));
        Assert.True(await db.Users.AnyAsync());
    }
}
