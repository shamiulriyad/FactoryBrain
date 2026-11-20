using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Demo;
using BunonBrain.Application.Dtos.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BunonBrain.Api.Tests;

/// <summary>Interactive demo: settings, demo accounts and the guard's rules (no host, no database).</summary>
public sealed class DemoOptionsTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);
        return k => map.TryGetValue(k, out var v) ? v : null;
    }

    [Fact]
    public void Off_unless_DEMO_MODE_is_true()
    {
        Assert.False(DemoOptions.FromEnvironment(Env()).Enabled);
        Assert.False(DemoOptions.FromEnvironment(Env(("DEMO_MODE", "false"), ("DEMO_LOGIN_EMAIL", "a@b.c"))).Enabled);
        Assert.False(DemoOptions.FromEnvironment(Env(("DEMO_MODE", "yes"))).Enabled);
        Assert.Null(DemoOptions.FromEnvironment(Env()).Problem);
    }

    [Fact]
    public void Demo_mode_needs_a_credential()
    {
        var o = DemoOptions.FromEnvironment(Env(("DEMO_MODE", "true")));
        Assert.True(o.Enabled);
        Assert.Contains("DEMO_BUNONBRAIN_DB", o.Problem);
        Assert.Contains("DEMO_LOGIN_EMAIL", o.Problem);
        Assert.Contains("DEMO_LOGIN_PASSWORD", o.Problem);

        var weak = DemoOptions.FromEnvironment(Env(("DEMO_MODE", "true"), ("DEMO_LOGIN_EMAIL", "judge@x.test"), ("DEMO_LOGIN_PASSWORD", "short")));
        Assert.Contains("DEMO_LOGIN_PASSWORD", weak.Problem);
    }

    [Fact]
    public void Settings_are_parsed_and_validated()
    {
        var o = DemoOptions.FromEnvironment(Env(("DEMO_MODE", "TRUE"), ("DEMO_BUNONBRAIN_DB", "Host=demo.example;Database=postgres;Username=u;Password=p"),
            ("DEMO_LOGIN_EMAIL", " Judge@X.Test "), ("DEMO_LOGIN_PASSWORD", "long-enough-pw"),
            ("DEMO_SESSION_HOURS", "6"), ("DEMO_MAX_SESSIONS", "50"), ("DEMO_RESET_TIME", "04:30"), ("DEMO_TIME_ZONE", "UTC")));
        Assert.Null(o.Problem);
        Assert.StartsWith("Host=demo.example", o.ConnectionString);
        Assert.Equal("judge@x.test", o.LoginEmail);
        Assert.Equal(TimeSpan.FromHours(6), o.SessionLifetime);
        Assert.Equal(50, o.MaxActiveSessions);
        Assert.Equal(new TimeOnly(4, 30), o.ResetTime);

        var bad = DemoOptions.FromEnvironment(Env(("DEMO_MODE", "true"), ("DEMO_LOGIN_EMAIL", "judge@x.test"), ("DEMO_LOGIN_PASSWORD", "long-enough-pw"),
            ("DEMO_SESSION_HOURS", "48"), ("DEMO_RESET_TIME", "25:00"), ("DEMO_TIME_ZONE", "Mars/Olympus"), ("DEMO_ADMIN_KEY", "short")));
        Assert.Contains("DEMO_SESSION_HOURS", bad.Problem);
        Assert.Contains("DEMO_RESET_TIME", bad.Problem);
        Assert.Contains("DEMO_TIME_ZONE", bad.Problem);
        Assert.Contains("DEMO_ADMIN_KEY", bad.Problem);
    }

    [Fact]
    public void Credential_and_admin_key_checks()
    {
        var o = DemoOptions.FromEnvironment(Env(("DEMO_MODE", "true"), ("DEMO_LOGIN_EMAIL", "judge@x.test"), ("DEMO_LOGIN_PASSWORD", "Correct-Horse-1"),
            ("DEMO_ADMIN_KEY", new string('k', 30))));
        Assert.True(o.CredentialMatches("JUDGE@x.test ", "Correct-Horse-1"));
        Assert.False(o.CredentialMatches("judge@x.test", "correct-horse-1"));
        Assert.False(o.CredentialMatches("other@x.test", "Correct-Horse-1"));
        Assert.False(o.CredentialMatches(null, null));
        Assert.True(o.AdminKeyMatches(new string('k', 30)));
        Assert.False(o.AdminKeyMatches(new string('k', 29)));
        Assert.False(DemoOptions.Disabled.CredentialMatches("", ""));
        Assert.False(DemoOptions.Disabled.AdminKeyMatches(""));
    }

    [Fact]
    public void Next_reset_is_the_next_local_reset_time()
    {
        var o = DemoOptions.FromEnvironment(Env(("DEMO_MODE", "true"), ("DEMO_LOGIN_EMAIL", "judge@x.test"), ("DEMO_LOGIN_PASSWORD", "long-enough-pw"),
            ("DEMO_RESET_TIME", "03:00")));
        // Asia/Dhaka is UTC+6: 03:00 local = 21:00 UTC the day before.
        var before = new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 21, 0, 0, TimeSpan.Zero), o.NextResetAfter(before));
        var exactly = new DateTimeOffset(2026, 11, 20, 21, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 21, 0, 0, TimeSpan.Zero), o.NextResetAfter(exactly));
        Assert.Equal(new DateOnly(2026, 11, 21), o.Today(exactly));
    }

    [Fact]
    public void The_demo_database_must_not_be_the_production_one()
    {
        const string prod = "Host=aws-0.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.prodref;Password=a";
        Assert.True(Program.SameDatabase(prod, "Host=AWS-0.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.prodref;Password=other;Maximum Pool Size=10"));
        Assert.False(Program.SameDatabase(prod, "Host=aws-0.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.demoref;Password=a"));
        Assert.False(Program.SameDatabase(prod, "Host=other.example;Port=5432;Database=postgres;Username=postgres.prodref;Password=a"));
    }

    [Fact]
    public async Task The_demo_route_flows_through_awaits_and_ends_with_its_scope()
    {
        Assert.False(DemoRoute.IsDemo);
        using (DemoRoute.Enter())
        {
            await Task.Yield();
            Assert.True(DemoRoute.IsDemo);
            Assert.True(await Task.Run(() => DemoRoute.IsDemo));
        }
        Assert.False(DemoRoute.IsDemo);
        // A route entered inside an async call never leaks back to its caller.
        await EnterInside();
        Assert.False(DemoRoute.IsDemo);
        static async Task EnterInside() { DemoRoute.Enter(); await Task.Yield(); Assert.True(DemoRoute.IsDemo); }
    }

    [Fact]
    public void One_stable_demo_account_per_system_role()
    {
        Assert.Equal(AccessCatalog.AllRoles.Count, DemoIdentities.All.Count);
        Assert.Equal(AccessCatalog.AllRoles.Select(r => r.Id).OrderBy(x => x), DemoIdentities.All.Select(i => i.RoleId).OrderBy(x => x));
        Assert.Equal(DemoIdentities.All.Count, DemoIdentities.All.Select(i => i.UserId).Distinct().Count());
        Assert.Equal(DemoIdentities.All.Count, DemoIdentities.All.Select(i => i.Email).Distinct().Count());
        Assert.All(DemoIdentities.All, i => Assert.EndsWith("@" + DemoIdentities.EmailDomain, i.Email));
        Assert.Equal(DemoIdentities.IdFor(SystemRoles.FactoryManager), DemoIdentities.ForRole(SystemRoles.FactoryManager)!.UserId);
        Assert.Null(DemoIdentities.ForRole("Admin"));
        Assert.Null(DemoIdentities.ForRole(null));
        Assert.True(DemoIdentities.IsDemoIdentity(DemoIdentities.ForRole(SystemRoles.SuperAdmin)!.UserId));
        Assert.False(DemoIdentities.IsDemoIdentity(Guid.NewGuid()));
        // Highest-ranked role first, as the picker shows them.
        Assert.Equal(SystemRoles.SuperAdmin, DemoIdentities.All[0].RoleId);
    }

    [Theory]
    [InlineData("DELETE", "/api/workers/123", "demo_no_delete")]
    [InlineData("POST", "/api/auth/login", "demo_sign_in_only")]
    [InlineData("POST", "/api/auth/register", "demo_sign_in_only")]
    [InlineData("POST", "/api/auth/forgot-password", "demo_sign_in_only")]
    [InlineData("POST", "/api/account/password", "demo_account_protected")]
    [InlineData("PUT", "/api/account/profile", "demo_account_protected")]
    [InlineData("POST", "/api/workers/W-001/account", "demo_account_protected")]
    [InlineData("POST", "/api/factories/abc/status", "demo_factory_locked")]
    [InlineData("POST", "/api/settings/llm", "demo_platform_locked")]
    [InlineData("PUT", "/api/site-settings/hero", "demo_platform_locked")]
    [InlineData("POST", "/api/rag/reindex", "demo_platform_locked")]
    [InlineData("POST", "/api/ask", "demo_ai_off")]
    [InlineData("POST", "/api/agents/morning-brief/run", "demo_ai_off")]
    [InlineData("POST", "/api/vision/analyze", "demo_ai_off")]
    [InlineData("POST", "/api/monitoring/sessions", "demo_ai_off")]
    [InlineData("POST", "/api/monitoring/sessions/abc/frames", "demo_ai_off")]
    [InlineData("POST", "/api/qc/inspections", "demo_ai_off")]
    [InlineData("POST", "/api/documents/classify", "demo_ai_off")]
    [InlineData("POST", "/api/documents/abc/summary", "demo_ai_off")]
    [InlineData("POST", "/api/documents/abc/files/def/extract", "demo_ai_off")]
    [InlineData("POST", "/api/merchandising/orders/abc/documents", "demo_ai_off")]
    public void Guard_refuses(string method, string path, string code) =>
        Assert.Equal(code, DemoGuardMiddleware.Blocked(method, path)?.Code);

    [Theory]
    [InlineData("GET", "/api/workers")]
    [InlineData("POST", "/api/workers")]
    [InlineData("PUT", "/api/workers/W-001")]
    [InlineData("POST", "/api/auth/demo/login")]
    [InlineData("POST", "/api/auth/demo/switch")]
    [InlineData("POST", "/api/auth/refresh")]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("POST", "/api/overtime/request")]
    [InlineData("GET", "/api/settings/llm")]
    [InlineData("DELETE", "/hubs/monitoring")]
    [InlineData("POST", "/api/documents/abc/files")]
    [InlineData("POST", "/api/qc/inspections/abc/review")]
    [InlineData("POST", "/api/documents/abc/submit")]
    public void Guard_allows(string method, string path) => Assert.Null(DemoGuardMiddleware.Blocked(method, path));

    [Fact]
    public void Guard_protects_only_the_demo_accounts_among_users()
    {
        var identity = DemoIdentities.ForRole(SystemRoles.HrOfficer)!.UserId;
        foreach (var suffix in new[] { "", "/roles", "/scope", "/status", "/reset-password" })
        {
            var method = suffix is "/status" or "/reset-password" ? "POST" : "PUT";
            Assert.Equal("demo_account_protected", DemoGuardMiddleware.Blocked(method, $"/api/users/{identity}{suffix}")?.Code);
            Assert.Null(DemoGuardMiddleware.Blocked(method, $"/api/users/{Guid.NewGuid()}{suffix}"));
        }
        Assert.Null(DemoGuardMiddleware.Blocked("POST", "/api/users"));
    }
}

/// <summary>A database-free API host with DEMO_MODE=true; demo sessions come from a fake.</summary>
public sealed class DemoApiFactory : WebApplicationFactory<Program>
{
    public FakeDemoSessions Sessions { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Activate();
        return base.CreateHost(builder);
    }

    public void Activate() =>
        TestEnvironment.Apply(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["DOTNET_ENVIRONMENT"] = "Testing",
            ["BUNONBRAIN_DB"] = ApiFactory.UnreachableDatabase,
            ["JWT_SIGNING_KEY"] = ApiFactory.SigningKey,
            ["SIMULATOR_ENABLED"] = "false",
            ["RAG_EMBEDDING_PROVIDER"] = "local",
            ["DEMO_MODE"] = "true",
            ["DEMO_BUNONBRAIN_DB"] = ApiFactory.UnreachableDatabase.Replace("bunonbrain_tests", "bunonbrain_demo_tests"),
            ["DEMO_LOGIN_EMAIL"] = "judge@demo.test",
            ["DEMO_LOGIN_PASSWORD"] = TestEnvironment.RandomSecret(12),
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISessionValidator>();
            services.AddSingleton<ISessionValidator, AlwaysCurrentSessions>();
            services.RemoveAll<IDemoSessionService>();
            services.AddSingleton<IDemoSessionService>(Sessions);
            TestEnvironment.RemoveBackgroundServices(services);
        });
    }

    public string DemoToken(Guid sessionId, Guid userId, params string[] permissions) =>
        Services.GetRequiredService<ITokenService>().IssueDemoAccessToken(
            TestAccess.Snapshot(userId, $"demo-{userId:N}@tests.local", Array.Empty<string>(), permissions, "stamp"), sessionId, out _);
}

/// <summary>Only <see cref="IsActiveAsync"/> matters to the token check; the rest is unused here.</summary>
public sealed class FakeDemoSessions : IDemoSessionService
{
    public HashSet<(Guid Session, Guid User)> Active { get; } = new();
    public Task<bool> IsActiveAsync(Guid sessionId, Guid userId, CancellationToken ct) => Task.FromResult(Active.Contains((sessionId, userId)));
    public Task<DemoInfoResponse> InfoAsync(CancellationToken ct) => Task.FromResult(new DemoInfoResponse(true, true, 4, null));
    public Task<DemoResult<DemoSessionView>> LoginAsync(string e, string p, string? ip, string? ua, CancellationToken ct) => throw new NotSupportedException();
    public Task<DemoResult<DemoSessionView>> GetAsync(string r, CancellationToken ct) =>
        Task.FromResult(DemoResult<DemoSessionView>.Fail(DemoOutcome.Unauthorized, "demo_session_ended", "Sign in again."));
    public Task<DemoResult<AuthResponse>> SwitchAsync(string r, string roleId, CancellationToken ct) =>
        Task.FromResult(DemoResult<AuthResponse>.Fail(DemoOutcome.Unauthorized, "demo_session_ended", "Sign in again."));
    public Task<DemoResult<AuthResponse>> RefreshAsync(string r, CancellationToken ct) =>
        Task.FromResult(DemoResult<AuthResponse>.Fail(DemoOutcome.Unauthorized, "demo_session_ended", "Sign in again."));
    public Task LogoutAsync(string r, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<DemoSessionAdminView>> ListAsync(bool activeOnly, CancellationToken ct) => Task.FromResult<IReadOnlyList<DemoSessionAdminView>>(Array.Empty<DemoSessionAdminView>());
    public Task<int> RevokeAsync(Guid? sessionId, string reason, CancellationToken ct) => Task.FromResult(0);
}

[CollectionDefinition(Name)]
public sealed class DemoApiCollection : ICollectionFixture<DemoApiFactory>
{
    public const string Name = "demo-api";
}

/// <summary>
/// One host serving production and the demo: production requests are unaffected,
/// demo tokens need a live demo session, and the guard applies to demo requests only.
/// </summary>
[Collection(DemoApiCollection.Name)]
public sealed class DemoModeApiTests
{
    private readonly DemoApiFactory _api;
    public DemoModeApiTests(DemoApiFactory api) { _api = api; _api.Activate(); }

    private HttpClient Client(string? token)
    {
        var c = _api.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private string ProductionToken(params string[] permissions) =>
        _api.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            TestAccess.Snapshot(Guid.NewGuid(), "real@tests.local", Array.Empty<string>(), permissions, "stamp"), out _);

    private static async Task<string?> Code(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if (!text.TrimStart().StartsWith('{')) return null;
        return JsonDocument.Parse(text).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    [Fact]
    public async Task Sign_in_options_announce_the_demo_and_keep_normal_sign_in()
    {
        var body = await _api.CreateClient().GetFromJsonAsync<JsonElement>("/api/auth/options");
        Assert.True(body.GetProperty("demo").GetBoolean());
        Assert.True(body.GetProperty("passwordReset").GetBoolean());
    }

    [Fact]
    public async Task Production_requests_are_not_touched_by_the_demo_guard()
    {
        var login = await _api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "x@y.test", password = "whatever-1234" });
        Assert.NotEqual("demo_sign_in_only", await Code(login));
        Assert.Equal(HttpStatusCode.OK, (await Client(ProductionToken()).GetAsync("/")).StatusCode);
        var ask = await Client(ProductionToken(Permissions.KnowledgeView)).PostAsJsonAsync("/api/ask", new { question = "hello" });
        Assert.NotEqual("demo_ai_off", await Code(ask));
    }

    [Fact]
    public async Task A_demo_token_needs_a_live_session_acting_as_that_account()
    {
        var session = Guid.NewGuid();
        var user = Guid.NewGuid();
        var token = _api.DemoToken(session, user);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(token).GetAsync("/")).StatusCode);   // session not active
        _api.Sessions.Active.Add((session, user));
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Client(token).GetAsync("/")).StatusCode);         // active, same account
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await Client(_api.DemoToken(session, Guid.NewGuid())).GetAsync("/")).StatusCode);   // switched away
        }
        finally { _api.Sessions.Active.Clear(); }
    }

    [Fact]
    public async Task Demo_requests_cannot_delete_or_call_paid_ai()
    {
        var session = Guid.NewGuid();
        var user = Guid.NewGuid();
        _api.Sessions.Active.Add((session, user));
        try
        {
            var token = _api.DemoToken(session, user, Permissions.KnowledgeView);
            var delete = await Client(token).DeleteAsync("/api/ask/conversations/" + Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
            Assert.Equal("demo_no_delete", await Code(delete));
            var ask = await Client(token).PostAsJsonAsync("/api/ask", new { question = "hello" });
            Assert.Equal(HttpStatusCode.Forbidden, ask.StatusCode);
            Assert.Equal("demo_ai_off", await Code(ask));
        }
        finally { _api.Sessions.Active.Clear(); }

        // The same delete without a demo token is an ordinary production request.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.CreateClient().DeleteAsync("/api/ask/conversations/" + Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Refresh_and_switch_without_a_session_answer_401()
    {
        var client = _api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/demo/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/demo/switch", new { roleId = SystemRoles.FactoryManager })).StatusCode);
    }

    [Fact]
    public async Task Operator_routes_hide_without_a_configured_key()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Add(BunonBrain.Api.Controllers.DemoAdminController.KeyHeader, "anything-at-all-anything-at-all");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/demo/admin/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/demo/admin/reset", null)).StatusCode);
    }
}

/// <summary>The normal (non-demo) server: demo routes don't exist and demo tokens are refused.</summary>
[Collection(ApiCollection.Name)]
public sealed class DemoOffApiTests
{
    private readonly ApiFactory _api;
    public DemoOffApiTests(ApiFactory api) { _api = api; _api.Activate(); }

    [Fact]
    public async Task Demo_routes_answer_404()
    {
        var client = _api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/demo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/auth/demo/login", new { email = "a@b.test", password = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/demo/session")).StatusCode);
        var options = await client.GetFromJsonAsync<JsonElement>("/api/auth/options");
        Assert.False(options.TryGetProperty("demo", out var d) && d.GetBoolean());
    }

    [Fact]
    public async Task A_demo_token_is_refused()
    {
        var token = _api.Services.GetRequiredService<ITokenService>().IssueDemoAccessToken(
            TestAccess.Snapshot(Guid.NewGuid(), "x@tests.local", Array.Empty<string>(), Array.Empty<string>(), "stamp"), Guid.NewGuid(), out _);
        var res = await _api.ClientWithToken(token).GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var normal = await _api.ClientAs("Viewer").GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
    }

    [Fact]
    public async Task Deletes_still_work_normally_outside_demo_mode()
    {
        // Not refused by the demo guard: the normal permission check answers.
        var res = await _api.CreateClient().DeleteAsync("/api/ask/conversations/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
