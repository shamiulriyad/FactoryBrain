using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Login, refresh, logout and role enforcement against real users in the
/// disposable PostgreSQL test database (seeded at startup from random,
/// test-only SEED_ADMIN_* / SEED_VIEWER_* credentials).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAuthTests
{
    private readonly PostgresStack _stack;
    public DbAuthTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    private sealed record Session(HttpStatusCode Status, string? AccessToken, string? Role, string? RefreshCookie, string? SetCookie, string Body);

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<Session> Login(string email, string password, string? clientIp = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = Json(new { email, password }) };
        req.Headers.Add(TestClientIpStartupFilter.Header, clientIp ?? TestClientIpStartupFilter.NewAddress());
        using var res = await client.SendAsync(req);
        return await ReadSession(res);
    }

    private async Task<Session> Refresh(string? cookie)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        if (cookie is not null) req.Headers.Add("Cookie", $"{AuthCookieSettings.CookieName}={cookie}");
        using var res = await client.SendAsync(req);
        return await ReadSession(res);
    }

    private static async Task<Session> ReadSession(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        string? token = null, role = null;
        if (res.IsSuccessStatusCode && body.Length > 0)
        {
            using var doc = JsonDocument.Parse(body);
            token = doc.RootElement.GetProperty("accessToken").GetString();
            role  = doc.RootElement.GetProperty("user").GetProperty("role").GetString();
        }
        var setCookie = res.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(AuthCookieSettings.CookieName + "=", StringComparison.Ordinal))
            : null;
        var cookie = setCookie?.Split(';')[0][(AuthCookieSettings.CookieName.Length + 1)..];
        return new Session(res.StatusCode, token, role, string.IsNullOrEmpty(cookie) ? null : cookie, setCookie, body);
    }

    private HttpClient Bearer(string token)
    {
        var client = Api.RawClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(string Email, string Password)> CreateOperatorAsync()
    {
        var email = $"operator-{Guid.NewGuid():N}@factorybrain.test";
        var password = TestEnvironment.RandomSecret(18);
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User { Email = email, PasswordHash = hasher.Hash(new User { Email = email }, password), Role = Roles.Operator });
        await db.SaveChangesAsync();
        return (email, password);
    }

    [DockerFact]
    public async Task Seeded_admin_logs_in_and_gets_a_hardened_refresh_cookie()
    {
        var s = await Login(Api.AdminEmail, Api.AdminPassword);

        Assert.Equal(HttpStatusCode.OK, s.Status);
        Assert.False(string.IsNullOrEmpty(s.AccessToken));
        Assert.Equal(Roles.Admin, s.Role);
        Assert.NotNull(s.RefreshCookie);
        var attributes = s.SetCookie!.ToLowerInvariant();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("path=/api/auth", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.DoesNotContain(s.AccessToken!, s.SetCookie); // access token is never put in a cookie

        // The stored value is a hash, not the cookie itself.
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
        var admin = await db.Users.AsNoTracking().SingleAsync(u => u.Email == Api.AdminEmail);
        Assert.NotNull(admin.RefreshTokenHash);
        Assert.NotEqual(s.RefreshCookie, admin.RefreshTokenHash);
        Assert.DoesNotContain(Api.AdminPassword, admin.PasswordHash);
    }

    [DockerFact]
    public async Task Seeded_operator_logs_in_with_the_operator_role_and_can_act_as_one()
    {
        var s = await Login(Api.OperatorEmail, Api.OperatorPassword);
        Assert.Equal(HttpStatusCode.OK, s.Status);
        Assert.Equal(Roles.Operator, s.Role);

        using var client = Bearer(s.AccessToken!);
        using var flagged = await client.PostAsync("/api/qc/flag",
            Json(new { operation = "cutting", lineId = "line-2", defectRatePct = 2.0 }));
        Assert.Equal(HttpStatusCode.OK, flagged.StatusCode);
        using var admin = await client.PostAsync("/api/settings/llm", Json(new { clearAll = true }));
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
    }

    [DockerFact]
    public async Task Wrong_password_and_unknown_email_get_the_same_401()
    {
        var wrongPassword = await Login(Api.AdminEmail, Api.AdminPassword + "x");
        var unknownEmail  = await Login("nobody@factorybrain.test", Api.AdminPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.Status);
        Assert.Null(wrongPassword.RefreshCookie);
        Assert.Null(unknownEmail.RefreshCookie);
        // Bodies differ only in nothing that reveals which part was wrong.
        Assert.Equal(StripTraceId(wrongPassword.Body), StripTraceId(unknownEmail.Body));
    }

    private static string StripTraceId(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"\"");

    [DockerFact]
    public async Task Me_returns_the_database_user_for_the_token()
    {
        var s = await Login(Api.ViewerEmail, Api.ViewerPassword);
        using var client = Bearer(s.AccessToken!);
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/auth/me"));
        Assert.Equal(Api.ViewerEmail, doc.RootElement.GetProperty("email").GetString());
        Assert.Equal(Roles.Viewer, doc.RootElement.GetProperty("role").GetString());
    }

    [DockerFact]
    public async Task Refresh_rotates_the_cookie_and_the_previous_cookie_stops_working()
    {
        var login = await Login(Api.ViewerEmail, Api.ViewerPassword);
        var first = await Refresh(login.RefreshCookie);

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.False(string.IsNullOrEmpty(first.AccessToken));
        Assert.NotNull(first.RefreshCookie);
        Assert.NotEqual(login.RefreshCookie, first.RefreshCookie);

        // The new access token works against a DB-backed endpoint.
        using (var client = Bearer(first.AccessToken!))
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        // Replaying the rotated-out cookie fails…
        var replay = await Refresh(login.RefreshCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.Status);
        // …and the current one still works.
        Assert.Equal(HttpStatusCode.OK, (await Refresh(first.RefreshCookie)).Status);
    }

    [DockerFact]
    public async Task Refresh_without_or_with_a_forged_cookie_is_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(TestEnvironment.RandomSecret(32))).Status);
    }

    [DockerFact]
    public async Task Logout_revokes_the_refresh_token_in_the_database()
    {
        var (email, password) = await CreateOperatorAsync();
        var login = await Login(email, password);
        Assert.Equal(HttpStatusCode.OK, login.Status);

        using (var client = Api.RawClient())
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout"))
        {
            req.Headers.Add("Cookie", $"{AuthCookieSettings.CookieName}={login.RefreshCookie}");
            using var res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
            var cleared = res.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(AuthCookieSettings.CookieName + "="));
            Assert.Contains("expires=thu, 01 jan 1970", cleared.ToLowerInvariant());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(login.RefreshCookie)).Status);
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.Null(user.RefreshTokenHash);
        Assert.Null(user.RefreshTokenExpiresAt);
    }

    [DockerFact]
    public async Task Login_is_rate_limited_per_client_ip()
    {
        var ip = TestClientIpStartupFilter.NewAddress();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await Login(Api.ViewerEmail, "wrong-" + i, ip)).Status);

        Assert.All(statuses.Take(5), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
        // A different client is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await Login(Api.ViewerEmail, Api.ViewerPassword)).Status);
    }

    [DockerFact]
    public async Task Roles_stored_in_the_database_are_enforced_end_to_end()
    {
        var viewer = await Login(Api.ViewerEmail, Api.ViewerPassword);
        var (opEmail, opPassword) = await CreateOperatorAsync();
        var op = await Login(opEmail, opPassword);
        var admin = await Login(Api.AdminEmail, Api.AdminPassword);
        Assert.Equal(Roles.Operator, op.Role);

        var flag = new { operation = "sewing", lineId = "line-3", defectRatePct = 4.2, reworkRatePct = 1.1 };

        using (var v = Bearer(viewer.AccessToken!))
        {
            // Reads backed by seeded rows work for every role.
            Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/api/brief/morning")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/api/line-board")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/api/floor-alerts")).StatusCode);
            // Writes do not.
            Assert.Equal(HttpStatusCode.Forbidden, (await v.PostAsync("/api/qc/flag", Json(flag))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await v.PatchAsync("/api/floor-alerts/alert-1", Json(new { read = true }))).StatusCode);
        }

        using (var o = Bearer(op.AccessToken!))
        {
            using var flagged = await o.PostAsync("/api/qc/flag", Json(flag));
            Assert.Equal(HttpStatusCode.OK, flagged.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await o.PatchAsync("/api/floor-alerts/alert-1", Json(new { read = true }))).StatusCode);
            // Admin-only stays closed to an Operator.
            Assert.Equal(HttpStatusCode.Forbidden,
                (await o.PostAsync("/api/rag/ingest", Json(new { title = "x", source = "sop", content = "x" }))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await o.PostAsync("/api/settings/llm", Json(new { clearAll = true }))).StatusCode);
        }

        using (var scope = Api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
            Assert.True(await db.Insights.AnyAsync(i => i.Id == "qc-flag-sewing-line-3"), "QC flag by the Operator was persisted");
            Assert.True((await db.FloorAlerts.SingleAsync(a => a.Id == "alert-1")).Read);
        }

        using (var a = Bearer(admin.AccessToken!))
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/rag/status")).StatusCode);
    }
}
