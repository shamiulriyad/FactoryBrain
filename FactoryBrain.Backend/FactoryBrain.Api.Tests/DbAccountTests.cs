using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Infrastructure.Services;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Self-service accounts against real PostgreSQL: sign-up (always Viewer),
/// forgot / reset password with a one-time emailed link (captured, never
/// sent), and Admin role management. Each request uses its own client IP so
/// the per-IP account rate limit doesn't couple the tests.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAccountTests
{
    private readonly PostgresStack _stack;
    public DbAccountTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    private sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw, string? RefreshCookie);

    private static string NewEmail(string tag) => $"{tag}-{Guid.NewGuid():N}@Accounts.Test";
    private static string NewPassword() => "pw-" + TestEnvironment.RandomSecret(12);

    private static async Task<Reply> Send(DbApiFactory api, HttpMethod method, string path, object? body = null,
        string? bearer = null, string? cookie = null, string? ip = null)
    {
        using var client = api.RawClient();
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = Rag.Json(body);
        req.Headers.Add(TestClientIpStartupFilter.Header, ip ?? TestClientIpStartupFilter.NewAddress());
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

    private static Task<Reply> Post(DbApiFactory api, string path, object body, string? bearer = null, string? cookie = null, string? ip = null)
        => Send(api, HttpMethod.Post, path, body, bearer, cookie, ip);

    private static string TokenFromLink(string emailBody)
    {
        var m = Regex.Match(emailBody, @"https://app\.factorybrain\.test/reset-password\?token=([A-Za-z0-9_-]{43})\b");
        Assert.True(m.Success, "reset link not found in the email");
        return m.Groups[1].Value;
    }

    [DockerFact]
    public async Task Sign_up_creates_a_signed_in_viewer_and_is_audited()
    {
        var email = NewEmail("signup");
        var password = NewPassword();

        var created = await Post(Api, "/api/auth/register", new { email, password });
        Assert.True(created.Status == HttpStatusCode.Created, created.Raw);
        Assert.Equal("Viewer", created.Body.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(email.ToLowerInvariant(), created.Body.GetProperty("user").GetProperty("email").GetString());
        Assert.NotNull(created.RefreshCookie);                    // signed in, like login
        Assert.DoesNotContain(password, created.Raw);

        var token = created.Body.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Send(Api, HttpMethod.Get, "/api/auth/me", bearer: token)).Status);
        // A Viewer can't do Operator things.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Api, "/api/qc/flag", new { operation = "sewing", lineId = "line-1", defectRatePct = 4 }, token)).Status);
        // Signing in with the new credentials works.
        Assert.Equal(HttpStatusCode.OK, (await Post(Api, "/api/auth/login", new { email, password })).Status);

        // Same email again (any casing) → 409; nothing new is stored.
        var dupe = await Post(Api, "/api/auth/register", new { email = email.ToUpperInvariant(), password = NewPassword() });
        Assert.Equal(HttpStatusCode.Conflict, dupe.Status);
        var lower = email.ToLowerInvariant();
        Assert.Equal(1, await Rag.WithDb(Api, db => db.Users.CountAsync(u => u.Email == lower)));
        Assert.True(await Rag.WithDb(Api, db => db.AuditLog.AnyAsync(a => a.Action == "user.registered" && a.ActorEmail == lower && a.ToState == "Viewer")));
    }

    [DockerFact]
    public async Task Sign_up_rejects_weak_passwords_and_bad_emails()
    {
        foreach (var body in new object[]
        {
            new { email = NewEmail("weak"), password = "short" },
            new { email = "not-an-email", password = NewPassword() },
            new { email = "same@accounts.test", password = "same@accounts.test" },
        })
        {
            var r = await Post(Api, "/api/auth/register", body);
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        }
    }

    [DockerFact]
    public async Task Sign_up_can_be_switched_off()
    {
        await using var closed = DbApiFactory.Custom(_stack.ConnectionStringFor(PostgresStack.LocalDatabase),
            new() { ["RAG_EMBEDDING_PROVIDER"] = "local", ["ALLOW_SELF_REGISTRATION"] = "false" });
        var options = await Send(closed, HttpMethod.Get, "/api/auth/options");
        Assert.False(options.Body.GetProperty("selfRegistration").GetBoolean());
        Assert.True(options.Body.GetProperty("passwordReset").GetBoolean());

        var email = NewEmail("closed");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(closed, "/api/auth/register", new { email, password = NewPassword() })).Status);
        var lower = email.ToLowerInvariant();
        Assert.False(await Rag.WithDb(closed, db => db.Users.AnyAsync(u => u.Email == lower)));
    }

    [DockerFact]
    public async Task Forgot_password_does_not_reveal_accounts_and_the_emailed_link_works_once()
    {
        var email = NewEmail("reset");
        var oldPassword = NewPassword();
        var reg = await Post(Api, "/api/auth/register", new { email, password = oldPassword });
        Assert.Equal(HttpStatusCode.Created, reg.Status);
        var otherSession = reg.RefreshCookie!;

        // Unknown and known emails get the same answer; only the known one gets mail.
        var unknown = await Post(Api, "/api/auth/forgot-password", new { email = NewEmail("nobody") });
        var known = await Post(Api, "/api/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.Accepted, unknown.Status);
        Assert.Equal((known.Status, known.Raw), (unknown.Status, unknown.Raw));

        var mail = Assert.Single(await Api.Mail.WaitFor(email.ToLowerInvariant()));
        Assert.Contains("30 minutes", mail.TextBody);
        var token = TokenFromLink(mail.TextBody);
        // Only a hash is stored.
        Assert.Equal("0", (await Rag.Strings(Api, $"SELECT count(*) FROM password_reset_tokens WHERE \"TokenHash\" = '{token}'")).Single());

        var newPassword = NewPassword();
        var reset = await Post(Api, "/api/auth/reset-password", new { token, password = newPassword });
        Assert.True(reset.Status == HttpStatusCode.OK, reset.Raw);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Api, "/api/auth/login", new { email, password = oldPassword })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(Api, "/api/auth/login", new { email, password = newPassword })).Status);
        // The session that existed before the reset is signed out.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Api, "/api/auth/refresh", new { }, cookie: otherSession)).Status);
        // The link works once.
        var again = await Post(Api, "/api/auth/reset-password", new { token, password = NewPassword() });
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);

        var lower = email.ToLowerInvariant();
        var actions = await Rag.WithDb(Api, db => db.AuditLog.Where(a => a.ActorEmail == lower).Select(a => a.Action).ToListAsync());
        Assert.Contains("auth.password_reset_requested", actions);
        Assert.Contains("auth.password_reset", actions);
        AiSettingsTestKit.AssertNoSecret(token, Api.Logs.Lines, "the log");
        AiSettingsTestKit.AssertNoSecret(newPassword, Api.Logs.Lines, "the log");
    }

    [DockerFact]
    public async Task A_new_link_voids_the_old_one_expired_links_fail_and_requests_are_capped()
    {
        var email = NewEmail("links");
        Assert.Equal(HttpStatusCode.Created, (await Post(Api, "/api/auth/register", new { email, password = NewPassword() })).Status);
        var lower = email.ToLowerInvariant();

        await Post(Api, "/api/auth/forgot-password", new { email });
        var first = TokenFromLink((await Api.Mail.WaitFor(lower, 1))[0].TextBody);
        await Post(Api, "/api/auth/forgot-password", new { email });
        var second = TokenFromLink((await Api.Mail.WaitFor(lower, 2))[1].TextBody);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Api, "/api/auth/reset-password", new { token = first, password = NewPassword() })).Status);

        await Rag.WithDb(Api, db => db.Database.ExecuteSqlRawAsync(
            "UPDATE password_reset_tokens SET \"ExpiresAt\" = now() - interval '1 minute' WHERE \"UsedAt\" IS NULL"));
        var expired = await Post(Api, "/api/auth/reset-password", new { token = second, password = NewPassword() });
        Assert.Equal(HttpStatusCode.BadRequest, expired.Status);
        Assert.Contains("invalid, has expired, or was already used", expired.Raw);

        // At most 3 emails per account per 15 minutes, however many IPs ask.
        await Post(Api, "/api/auth/forgot-password", new { email });
        await Post(Api, "/api/auth/forgot-password", new { email });
        await Task.Delay(500);
        Assert.Equal(AccountService.MaxResetRequestsPer15Min, (await Api.Mail.WaitFor(lower, 99)).Count);
    }

    [DockerFact]
    public async Task Account_endpoints_are_rate_limited_per_ip()
    {
        var ip = TestClientIpStartupFilter.NewAddress();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await Post(Api, "/api/auth/forgot-password", new { email = NewEmail("rl") }, ip: ip)).Status);
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.Equal((HttpStatusCode)429, statuses.Last());
    }

    [DockerFact]
    public async Task An_admin_promotes_a_new_viewer_but_cannot_change_their_own_role_or_remove_the_last_admin()
    {
        var email = NewEmail("promote");
        var reg = await Post(Api, "/api/auth/register", new { email, password = NewPassword() });
        var userId = reg.Body.GetProperty("user").GetProperty("id").GetString()!;

        // A real Admin from the database (the seeded one).
        var adminLogin = await Post(Api, "/api/auth/login", new { email = Api.AdminEmail, password = Api.AdminPassword });
        var admin = adminLogin.Body.GetProperty("accessToken").GetString()!;
        var adminId = adminLogin.Body.GetProperty("user").GetProperty("id").GetString()!;

        var list = await Send(Api, HttpMethod.Get, "/api/users", bearer: admin);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains(list.Body.EnumerateArray(), u => u.GetProperty("id").GetString() == userId && u.GetProperty("role").GetString() == "Viewer");
        Assert.DoesNotContain("passwordHash", list.Raw, StringComparison.OrdinalIgnoreCase);

        var promoted = await Send(Api, HttpMethod.Patch, $"/api/users/{userId}/role", new { role = "Operator" }, bearer: admin);
        Assert.True(promoted.Status == HttpStatusCode.OK, promoted.Raw);
        Assert.Equal("Operator", promoted.Body.GetProperty("role").GetString());
        // The user's next refresh carries the new role.
        var refreshed = await Post(Api, "/api/auth/refresh", new { }, cookie: reg.RefreshCookie);
        Assert.Equal("Operator", refreshed.Body.GetProperty("user").GetProperty("role").GetString());
        Assert.True(await Rag.WithDb(Api, db => db.AuditLog.AnyAsync(a => a.Action == "user.role_changed" && a.EntityId == userId
            && a.FromState == "Viewer" && a.ToState == "Operator")));

        Assert.Equal(HttpStatusCode.Conflict, (await Send(Api, HttpMethod.Patch, $"/api/users/{adminId}/role", new { role = "Viewer" }, bearer: admin)).Status);
        // Another (token-only) Admin can't demote the only Admin in the database either.
        using var otherAdmin = Rag.Client(Api, Roles.Admin);
        var other = otherAdmin.DefaultRequestHeaders.Authorization!.Parameter!;
        var last = await Send(Api, HttpMethod.Patch, $"/api/users/{adminId}/role", new { role = "Viewer" }, bearer: other);
        Assert.Equal(HttpStatusCode.Conflict, last.Status);
        Assert.Contains("only Admin", last.Raw);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Api, HttpMethod.Patch, $"/api/users/{userId}/role", new { role = "Root" }, bearer: admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(Api, HttpMethod.Patch, $"/api/users/{Guid.NewGuid()}/role", new { role = "Viewer" }, bearer: admin)).Status);
        var operatorToken = refreshed.Body.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Api, HttpMethod.Get, "/api/users", bearer: operatorToken)).Status);
    }

    private Task<AdminBootstrapOutcome> Bootstrap(string? email, string? password) => Rag.WithDb(Api, db =>
        AdminBootstrap.EnsureAsync(db, Api.Services.GetRequiredService<IPasswordHasher>(), email, password, NullLogger.Instance, default));

    private Task<string?> RoleOf(string email) => Rag.WithDb(Api, db =>
        db.Users.Where(u => u.Email == email.ToLowerInvariant()).Select(u => u.Role).FirstOrDefaultAsync());

    [DockerFact]
    public async Task The_admin_bootstrap_promotes_an_existing_account_only_with_its_own_password()
    {
        var email = NewEmail("bootstrap");
        var password = NewPassword();
        var reg = await Post(Api, "/api/auth/register", new { email, password });
        Assert.Equal(HttpStatusCode.Created, reg.Status);
        var userId = reg.Body.GetProperty("user").GetProperty("id").GetString()!;
        var fresh = NewEmail("bootstrap-new");
        try
        {
            Assert.Equal(AdminBootstrapOutcome.Skipped, await Bootstrap(email, ""));
            Assert.Equal(AdminBootstrapOutcome.Skipped, await Bootstrap("", password));

            // The server configuration alone can't take over an account.
            Assert.Equal(AdminBootstrapOutcome.PasswordMismatch, await Bootstrap(email, "not-" + password));
            Assert.Equal("Viewer", await RoleOf(email));

            // With the account's own password it is promoted, audited, and the password still works.
            Assert.Equal(AdminBootstrapOutcome.Promoted, await Bootstrap(email, password));
            Assert.Equal("Admin", await RoleOf(email));
            Assert.True(await Rag.WithDb(Api, db => db.AuditLog.AnyAsync(a => a.Action == "user.role_changed" && a.EntityId == userId
                && a.FromState == "Viewer" && a.ToState == "Admin" && a.ActorEmail == AdminBootstrap.ActorEmail)));
            // The session that already existed picks up Admin at its next refresh…
            var refreshed = await Post(Api, "/api/auth/refresh", new { }, cookie: reg.RefreshCookie);
            Assert.Equal("Admin", refreshed.Body.GetProperty("user").GetProperty("role").GetString());
            // …and the unchanged password still signs in, as Admin.
            var login = await Post(Api, "/api/auth/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.Status);
            Assert.Equal("Admin", login.Body.GetProperty("user").GetProperty("role").GetString());
            var users = await Send(Api, HttpMethod.Get, "/api/users", bearer: login.Body.GetProperty("accessToken").GetString());
            Assert.Equal(HttpStatusCode.OK, users.Status);

            // Idempotent on every restart.
            Assert.Equal(AdminBootstrapOutcome.AlreadyAdmin, await Bootstrap(email, password));

            // An email with no account creates a new Admin that can sign in.
            var freshPassword = NewPassword();
            Assert.Equal(AdminBootstrapOutcome.Created, await Bootstrap(fresh, freshPassword));
            var freshLogin = await Post(Api, "/api/auth/login", new { email = fresh, password = freshPassword });
            Assert.Equal("Admin", freshLogin.Body.GetProperty("user").GetProperty("role").GetString());
        }
        finally
        {
            // Keep a single seeded Admin for the "last Admin" checks elsewhere.
            await Rag.WithDb(Api, db => db.Users.Where(u => u.Email == email.ToLowerInvariant() || u.Email == fresh.ToLowerInvariant())
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Role, "Viewer")));
        }
    }
}
