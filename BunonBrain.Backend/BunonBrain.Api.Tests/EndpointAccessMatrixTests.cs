using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BunonBrain.Api.Authorization;
using BunonBrain.Application.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using P = BunonBrain.Application.Access.Permissions;

namespace BunonBrain.Api.Tests;

/// <summary>How an endpoint is guarded: public, any signed-in user, or any one of some permissions.</summary>
public sealed record Gate(string Kind, IReadOnlyList<string> AnyOf)
{
    public static readonly Gate Anonymous = new("anonymous", Array.Empty<string>());
    public static readonly Gate Authenticated = new("authenticated", Array.Empty<string>());
    public static Gate Perm(params string[] anyOf) => new("permission", anyOf.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    public bool IsPermission => Kind == "permission";
    public override string ToString() => IsPermission ? $"perm({string.Join("|", AnyOf)})" : Kind;
    public bool Same(Gate other) => Kind == other.Kind && AnyOf.SequenceEqual(other.AnyOf);
}

/// <summary>
/// The declared guard of every endpoint the API maps. Adding an endpoint
/// without adding a row here fails
/// <see cref="EndpointAccessMatrixTests.Every_mapped_endpoint_has_a_declared_access_level"/>,
/// and a row that disagrees with the endpoint's attributes fails
/// <see cref="EndpointAccessMatrixTests.Endpoint_metadata_matches_the_declared_access_level"/>.
/// </summary>
public static class EndpointMatrix
{
    private static readonly Gate Anon = Gate.Anonymous, Auth = Gate.Authenticated;
    private static Gate G(params string[] p) => Gate.Perm(p);

    public static readonly IReadOnlyList<(string Method, string Route, Gate Gate)> Rows = new (string, string, Gate)[]
    {
        // Intentionally public.
        ("*",     "/health",                    Anon),
        ("POST",  "/api/auth/login",            Anon),
        ("POST",  "/api/auth/refresh",          Anon),
        ("POST",  "/api/auth/logout",           Anon),
        ("GET",   "/api/auth/options",          Anon),
        ("POST",  "/api/auth/register",         Anon),
        ("POST",  "/api/auth/forgot-password",  Anon),
        ("POST",  "/api/auth/reset-password",   Anon),
        ("POST",  "/api/inquiries",             Anon),

        // Any signed-in user: identity and own account only.
        ("GET",   "/",                          Auth),
        ("GET",   "/api/auth/me",               Auth),
        ("GET",   "/api/account/profile",       Auth),
        ("PUT",   "/api/account/profile",       Auth),
        ("POST",  "/api/account/password",      Auth),

        // Users, roles, factories, workers, audit.
        ("GET",   "/api/users",                       G(P.UsersView)),
        ("POST",  "/api/users",                       G(P.UsersCreate)),
        ("GET",   "/api/users/{id}",                  G(P.UsersView)),
        ("PUT",   "/api/users/{id}",                  G(P.UsersUpdate)),
        ("POST",  "/api/users/{id}/status",           G(P.UsersDeactivate)),
        ("PUT",   "/api/users/{id}/roles",            G(P.RolesAssign)),
        ("PUT",   "/api/users/{id}/scope",            G(P.UsersUpdate)),
        ("POST",  "/api/users/{id}/reset-password",   G(P.UsersResetPassword)),
        ("GET",   "/api/users/{id}/activity",         G(P.AuditView)),
        ("GET",   "/api/roles",                       G(P.RolesView)),
        ("GET",   "/api/roles/{id}",                  G(P.RolesView)),
        ("GET",   "/api/permissions",                 G(P.RolesView)),
        ("GET",   "/api/factories",                   G(P.FactoriesView, P.UsersView)),
        ("POST",  "/api/factories",                   G(P.FactoriesManage)),
        ("GET",   "/api/factories/{id}",              G(P.FactoriesView, P.UsersView)),
        ("PUT",   "/api/factories/{id}",              G(P.FactoriesManage)),
        ("POST",  "/api/factories/{id}/status",       G(P.FactoriesManage)),
        ("GET",   "/api/factories/{id}/lines",        G(P.LinesView, P.UsersUpdate)),
        ("GET",   "/api/factories/unassigned-lines",  G(P.LinesManage)),
        ("PUT",   "/api/factories/{id}/lines/{lineId}",    G(P.LinesManage)),
        ("DELETE","/api/factories/{id}/lines/{lineId}",    G(P.LinesManage)),
        ("GET",   "/api/workers",                     G(P.WorkersView)),
        ("POST",  "/api/workers",                     G(P.WorkersManage)),
        ("GET",   "/api/workers/me",                  G(P.SelfView)),
        ("GET",   "/api/workers/{id}",                G(P.WorkersView, P.SelfView)),
        ("PUT",   "/api/workers/{id}",                G(P.WorkersManage)),
        ("PUT",   "/api/workers/{id}/workstation",    G(P.WorkersAssign, P.WorkersManage)),
        ("PUT",   "/api/workers/{id}/account",        G(P.UsersUpdate)),
        ("GET",   "/api/audit",                       G(P.AuditView)),
        ("GET",   "/api/inquiries",                   G(P.InquiriesManage)),
        ("PATCH", "/api/inquiries/{id}",              G(P.InquiriesManage)),

        // Knowledge & AI.
        ("POST",  "/api/ask",                   G(P.KnowledgeView)),
        ("GET",   "/api/ask/status",            G(P.KnowledgeView)),
        ("GET",   "/api/agents",                G(P.KnowledgeView)),
        ("POST",  "/api/agents/{agentId}/run",  G(P.AgentsRun)),
        ("GET",   "/api/rag/documents",         G(P.KnowledgeView)),
        ("GET",   "/api/rag/status",            G(P.KnowledgeView)),
        ("POST",  "/api/rag/ingest",            G(P.KnowledgeManage)),
        ("POST",  "/api/rag/reindex",           G(P.KnowledgeManage)),
        ("GET",   "/api/rag/reindex/plan",      G(P.KnowledgeManage)),
        ("GET",   "/api/settings/llm",          G(P.KnowledgeView, P.PlatformSettingsManage)),
        ("GET",   "/api/settings/embedding",    G(P.KnowledgeView, P.PlatformSettingsManage)),
        ("POST",  "/api/settings/llm",          G(P.PlatformSettingsManage)),
        ("POST",  "/api/settings/embedding",    G(P.PlatformSettingsManage)),
        ("POST",  "/api/settings/embedding/plan", G(P.PlatformSettingsManage)),
        ("GET",   "/api/brief/morning",         G(P.ReportsView)),

        // Monitoring & devices.
        ("GET",   "/api/floor-alerts",          G(P.MonitoringView, P.ProductionView)),
        ("PATCH", "/api/floor-alerts/{id}",     G(P.MonitoringAlertsReview, P.ProductionAlertsReview)),
        ("GET",   "/api/line-board",            G(P.ProductionView, P.MonitoringView)),
        ("POST",  "/api/line-board/refresh",    G(P.ProductionManage)),
        ("GET",   "/api/sensors/ingest",        G(P.DevicesView, P.MonitoringView)),
        ("GET",   "/api/sensors/latest",        G(P.DevicesView, P.MonitoringView)),
        ("GET",   "/api/sensors/snapshot",      G(P.DevicesView, P.MonitoringView)),
        ("POST",  "/api/sensors/ingest",        G(P.DevicesManage)),
        ("POST",  "/api/sensors/live",          G(P.DevicesManage)),
        ("*",     "/hubs/factory/negotiate",    G(P.MonitoringView, P.ProductionView, P.DevicesView)),
        ("*",     "/hubs/factory",              G(P.MonitoringView, P.ProductionView, P.DevicesView)),

        // Approvals.
        ("GET",   "/api/approvals",              G(P.ApprovalsView)),
        ("POST",  "/api/approvals/{id}/approve", G(P.ApprovalsDecide)),
        ("POST",  "/api/approvals/{id}/reject",  G(P.ApprovalsDecide)),

        // Quality.
        ("GET",   "/api/qc/defects",            G(P.QcView)),
        ("POST",  "/api/qc/flag",               G(P.QcManage)),
        ("GET",   "/api/qc/inspections",        G(P.QcView)),
        ("POST",  "/api/qc/inspections",        G(P.QcManage)),
        ("GET",   "/api/qc/inspections/{id}",   G(P.QcView)),
        ("GET",   "/api/qc/inspections/{id}/image", G(P.QcView)),
        ("POST",  "/api/qc/inspections/{id}/review", G(P.QcManage)),
        ("POST",  "/api/qc/inspections/{id}/retry",  G(P.QcManage)),
        ("GET",   "/api/qc/summary",            G(P.QcView)),
        ("GET",   "/api/qc/vision/status",      G(P.QcView)),
        ("GET",   "/api/qc/categories",         G(P.QcView)),
        ("POST",  "/api/qc/categories",         G(P.SettingsManage)),
        ("PUT",   "/api/qc/categories/{code}",  G(P.SettingsManage)),
        ("GET",   "/api/vision/analyze",        G(P.QcView)),
        ("POST",  "/api/vision/analyze",        G(P.QcManage)),

        // Production.
        ("GET",   "/api/production/config",     G(P.ProductionView)),
        ("GET",   "/api/production/lines",      G(P.ProductionView)),
        ("POST",  "/api/production/lines",      G(P.LinesManage)),
        ("PUT",   "/api/production/lines/{id}", G(P.LinesManage)),
        ("GET",   "/api/production/lines/{id}/detail", G(P.ProductionView)),
        ("GET",   "/api/production/shifts",     G(P.ProductionView)),
        ("POST",  "/api/production/shifts",     G(P.ProductionSetup)),
        ("PUT",   "/api/production/shifts/{code}", G(P.ProductionSetup)),
        ("GET",   "/api/production/styles",     G(P.ProductionView)),
        ("GET",   "/api/production/styles/{id}", G(P.ProductionView)),
        ("POST",  "/api/production/styles",     G(P.ProductionSetup)),
        ("PUT",   "/api/production/styles/{id}", G(P.ProductionSetup)),
        ("GET",   "/api/production/orders",     G(P.ProductionView)),
        ("GET",   "/api/production/orders/{id}", G(P.ProductionView)),
        ("GET",   "/api/production/orders/{id}/risk", G(P.ProductionView)),
        ("POST",  "/api/production/orders",     G(P.ProductionManage)),
        ("PUT",   "/api/production/orders/{id}", G(P.ProductionManage)),
        ("GET",   "/api/production/plans",      G(P.ProductionView)),
        ("GET",   "/api/production/plans/{id}", G(P.ProductionView)),
        ("POST",  "/api/production/plans",      G(P.ProductionManage)),
        ("PUT",   "/api/production/plans/{id}", G(P.ProductionManage)),
        ("POST",  "/api/production/plans/{id}/submit",   G(P.ProductionManage)),
        ("POST",  "/api/production/plans/{id}/activate", G(P.ProductionManage)),
        ("POST",  "/api/production/plans/{id}/changes/{changeId}/resolve", G(P.ProductionManage)),
        ("POST",  "/api/production/plans/{id}/withdraw", G(P.ProductionManage)),
        ("POST",  "/api/production/plans/{id}/changes/{changeId}/withdraw", G(P.ProductionManage)),
        ("POST",  "/api/production/plans/{id}/complete", G(P.ProductionManage)),
        ("POST",  "/api/production/plans/{id}/cancel",   G(P.ProductionManage)),
        ("GET",   "/api/production/output",     G(P.ProductionView)),
        ("POST",  "/api/production/output",     G(P.ProductionManage)),
        ("POST",  "/api/production/output/{id}/correct", G(P.ProductionManage)),
        ("POST",  "/api/production/output/import", G(P.ProductionManage)),
        ("GET",   "/api/production/summary",    G(P.ProductionView)),
        ("GET",   "/api/production/observations", G(P.ProductionView)),
        ("POST",  "/api/production/observations", G(P.ProductionManage)),
        ("GET",   "/api/production/layouts/current", G(P.ProductionView)),
        ("PUT",   "/api/production/layouts/current", G(P.ProductionManage)),
        ("POST",  "/api/production/balance/analyze", G(P.ProductionManage)),
        ("GET",   "/api/production/balance/proposals", G(P.ProductionView)),
        ("GET",   "/api/production/balance/proposals/{id}", G(P.ProductionView)),
        ("POST",  "/api/production/balance/proposals/{id}/submit", G(P.ProductionManage)),
        ("POST",  "/api/production/balance/proposals/{id}/apply",  G(P.ProductionManage)),

        // Merchandising.
        ("GET",   "/api/merchandising/config",  G(P.MerchandisingView)),
        ("GET",   "/api/merchandising/overview", G(P.MerchandisingView)),
        ("GET",   "/api/merchandising/orders",  G(P.MerchandisingView)),
        ("GET",   "/api/merchandising/orders/{id}", G(P.MerchandisingView)),
        ("POST",  "/api/merchandising/orders",  G(P.MerchandisingManage)),
        ("PUT",   "/api/merchandising/orders/{id}", G(P.MerchandisingManage)),
        ("POST",  "/api/merchandising/orders/{id}/milestones", G(P.MerchandisingManage)),
        ("PUT",   "/api/merchandising/milestones/{id}", G(P.MerchandisingManage)),
        ("POST",  "/api/merchandising/conflicts/{id}/resolve", G(P.MerchandisingManage)),
        ("GET",   "/api/merchandising/documents", G(P.MerchandisingView)),
        ("GET",   "/api/merchandising/documents/{id}", G(P.MerchandisingView)),
        ("POST",  "/api/merchandising/orders/{id}/documents", G(P.MerchandisingManage)),
        ("POST",  "/api/merchandising/documents/{id}/versions", G(P.MerchandisingManage)),
        ("GET",   "/api/merchandising/versions/{id}", G(P.MerchandisingView)),
        ("GET",   "/api/merchandising/versions/{id}/file", G(P.MerchandisingView)),
        ("POST",  "/api/merchandising/versions/{id}/retry", G(P.MerchandisingManage)),
        ("POST",  "/api/merchandising/versions/{id}/confirm", G(P.MerchandisingManage)),
    };

    public static IEnumerable<object[]> Where(Func<Gate, bool> filter) =>
        Rows.Where(r => filter(r.Gate)).Select(r => new object[] { r.Method, r.Route });

    /// <summary>Concrete request target for a route template.</summary>
    public static string Url(string route) => route
        .Replace("{agentId}", "line-throughput-agent")
        .Replace("{changeId}", "change-1")
        .Replace("{lineId}", "line-1")
        .Replace("{id}", "alert-1")
        .Replace("{code}", "stain");

    public static HttpRequestMessage Request(string method, string route, string? body = "{}")
    {
        var verb = method == "*" ? "GET" : method;
        var request = new HttpRequestMessage(new HttpMethod(verb), Url(route));
        if (verb is "POST" or "PATCH" or "PUT" && body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    public static Gate GateOf(string method, string route) => Rows.Single(r => r.Method == method && r.Route == route).Gate;
}

[Collection(ApiCollection.Name)]
public sealed class EndpointAccessMatrixTests
{
    private readonly ApiFactory _factory;
    public EndpointAccessMatrixTests(ApiFactory factory) { _factory = factory; factory.Activate(); }

    [Fact]
    public void Every_mapped_endpoint_has_a_declared_access_level()
    {
        var mapped = MappedEndpoints();
        var declared = EndpointMatrix.Rows.Select(r => Key(r.Method, r.Route)).ToHashSet();

        var undeclared = mapped.Keys.Where(k => !declared.Contains(k)).OrderBy(k => k).ToList();
        var stale      = declared.Where(k => !mapped.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(undeclared.Count == 0, "Endpoints with no declared access level: " + string.Join(", ", undeclared));
        Assert.True(stale.Count == 0, "Declared endpoints that are not mapped: " + string.Join(", ", stale));
    }

    [Fact]
    public void Endpoint_metadata_matches_the_declared_access_level()
    {
        var mapped = MappedEndpoints();
        var mismatches = new List<string>();
        foreach (var row in EndpointMatrix.Rows)
        {
            if (!mapped.TryGetValue(Key(row.Method, row.Route), out var endpoints)) continue;
            foreach (var endpoint in endpoints)
            {
                var actual = EffectiveGate(endpoint);
                if (!actual.Same(row.Gate))
                    mismatches.Add($"{row.Method} {row.Route}: declared {row.Gate}, metadata gives {actual}");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
    }

    [Fact]
    public void Every_permission_named_by_an_endpoint_exists_and_no_endpoint_uses_a_reserved_one()
    {
        foreach (var (method, route, gate) in EndpointMatrix.Rows.Where(r => r.Gate.IsPermission))
            foreach (var p in gate.AnyOf)
            {
                var def = AccessCatalog.Permission(p);
                Assert.True(def is not null, $"{method} {route}: unknown permission {p}");
                Assert.False(def!.Reserved, $"{method} {route} uses {p}, which the catalog marks reserved");
            }
    }

    public static IEnumerable<object[]> ProtectedEndpoints => EndpointMatrix.Where(g => g.Kind != "anonymous");
    public static IEnumerable<object[]> PermissionEndpoints => EndpointMatrix.Where(g => g.IsPermission);

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Anonymous_requests_to_protected_endpoints_get_401(string method, string route)
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(EndpointMatrix.Request(method, route));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PermissionEndpoints))]
    public async Task A_signed_in_user_without_permissions_gets_403_on_every_permission_endpoint(string method, string route)
    {
        using var client = _factory.ClientWithToken(_factory.TokenWith(Array.Empty<string>(), Array.Empty<string>()));
        using var response = await client.SendAsync(EndpointMatrix.Request(method, route));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        if (!route.StartsWith("/hubs", StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(AccessProblem.PermissionDenied, doc.RootElement.GetProperty("code").GetString());
            var required = doc.RootElement.GetProperty("requiredPermissions").EnumerateArray().Select(e => e.GetString()).ToHashSet();
            Assert.True(EndpointMatrix.GateOf(method, route).AnyOf.All(required.Contains));
        }
    }

    [Theory]
    [MemberData(nameof(PermissionEndpoints))]
    public async Task A_token_with_another_modules_permissions_is_still_refused(string method, string route)
    {
        // Every permission except the ones this endpoint accepts.
        var gate = EndpointMatrix.GateOf(method, route);
        var others = AccessCatalog.AllPermissions.Select(p => p.Code).Where(p => !gate.AnyOf.Contains(p));
        using var client = _factory.ClientWithToken(_factory.TokenWith(new[] { SystemRoles.FactoryAdmin }, others));
        using var response = await client.SendAsync(EndpointMatrix.Request(method, route));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_pending_temporary_password_blocks_every_permission_endpoint_but_not_the_account()
    {
        var all = AccessCatalog.AllPermissions.Select(p => p.Code);
        using var client = _factory.ClientWithToken(_factory.TokenWith(new[] { SystemRoles.SuperAdmin }, all, mustChangePassword: true));
        foreach (var path in new[] { "/api/users", "/api/qc/vision/status", "/api/sensors/snapshot", "/api/roles" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(AccessProblem.PasswordChangeRequired, doc.RootElement.GetProperty("code").GetString());
        }
        // The account endpoints stay reachable (they need the database, which this host doesn't have).
        using var profile = await client.GetAsync("/api/account/profile");
        Assert.NotEqual(HttpStatusCode.Unauthorized, profile.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, profile.StatusCode);
    }

    [Fact]
    public async Task Readers_reach_database_free_endpoints()
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        foreach (var path in new[] { "/api/qc/vision/status", "/api/sensors/snapshot", "/api/sensors/ingest", "/api/merchandising/config", "/api/permissions" })
        {
            using var response = await client.GetAsync(path);
            var expected = path == "/api/permissions" ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            Assert.True(response.StatusCode == expected, $"Viewer GET {path} → {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Super_admin_passes_authorization_on_a_platform_endpoint()
    {
        // Settings live in the database, which this host doesn't have: the
        // request gets past authentication and the permission check, reaches
        // the settings store, and fails closed with 503.
        using var client = _factory.ClientAs(SystemRoles.SuperAdmin);
        using var response = await client.SendAsync(
            EndpointMatrix.Request("POST", "/api/settings/llm", """{"clearAll":true}"""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":\"settings_unavailable\"", body);
    }

    [Fact]
    public async Task Public_endpoints_do_not_demand_authentication()
    {
        using var client = _factory.CreateClient();

        using (var health = await client.GetAsync("/health"))
            Assert.True(health.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
                $"/health → {(int)health.StatusCode}");

        using (var login = await client.SendAsync(EndpointMatrix.Request("POST", "/api/auth/login", "{}")))
            Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);

        using (var logout = await client.SendAsync(EndpointMatrix.Request("POST", "/api/auth/logout", null)))
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using (var refresh = await client.SendAsync(EndpointMatrix.Request("POST", "/api/auth/refresh", null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
            Assert.Contains("Missing refresh token", await refresh.Content.ReadAsStringAsync());
        }
    }

    public static IEnumerable<object[]> AllRoles => AccessCatalog.AllRoles.Select(r => new object[] { r.Id });

    /// <summary>
    /// Every predefined role against every endpoint, through the API's real
    /// authorization policies (in-process: no HTTP, no database). A role may
    /// pass exactly the endpoints that accept one of its catalog permissions.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRoles))]
    public async Task Each_role_passes_exactly_the_endpoints_its_permissions_allow(string roleId)
    {
        var role = AccessCatalog.Role(roleId)!;
        var principal = Principal(new[] { roleId }, role.Permissions);
        var authz = _factory.Services.GetRequiredService<IAuthorizationService>();
        var mapped = MappedEndpoints();
        var wrong = new List<string>();
        foreach (var (method, route, gate) in EndpointMatrix.Rows)
        {
            if (gate.Kind == "anonymous") continue;
            foreach (var endpoint in mapped[Key(method, route)])
            {
                var allowed = true;
                foreach (var policy in Policies(endpoint))
                    allowed &= (await authz.AuthorizeAsync(principal, null, policy)).Succeeded;
                var expected = !gate.IsPermission || gate.AnyOf.Any(role.Permissions.Contains);
                if (allowed != expected) wrong.Add($"{method} {route}: {(allowed ? "allowed" : "denied")}, expected {(expected ? "allowed" : "denied")}");
            }
        }
        Assert.True(wrong.Count == 0, $"{roleId}: " + string.Join("; ", wrong));
    }

    [Fact]
    public async Task Role_claims_alone_grant_nothing()
    {
        // A token that names Super Admin but carries no permission claims is
        // refused: authorization reads permissions, never role names.
        var authz = _factory.Services.GetRequiredService<IAuthorizationService>();
        var principal = Principal(new[] { SystemRoles.SuperAdmin }, Array.Empty<string>());
        var policy = await _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(new RequirePermissionAttribute(P.UsersView).Policy!);
        Assert.False((await authz.AuthorizeAsync(principal, null, policy!)).Succeeded);
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static ClaimsPrincipal Principal(IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new(AccessClaims.SecurityStamp, "s") };
        claims.AddRange(roles.Select(r => new Claim(AccessClaims.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim(AccessClaims.Permission, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "email", AccessClaims.Role));
    }

    /// <summary>The policies an endpoint must satisfy (its own, or the fallback "authenticated" policy).</summary>
    private IEnumerable<AuthorizationPolicy> Policies(Endpoint endpoint)
    {
        var provider = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var data = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var named = data.Select(d => d.Policy).Where(p => p is not null).ToList();
        if (named.Count == 0)
        {
            yield return provider.GetFallbackPolicyAsync().GetAwaiter().GetResult()
                         ?? provider.GetDefaultPolicyAsync().GetAwaiter().GetResult();
            yield break;
        }
        foreach (var name in named) yield return provider.GetPolicyAsync(name!).GetAwaiter().GetResult()!;
    }

    private Dictionary<string, List<RouteEndpoint>> MappedEndpoints()
    {
        var sources = _factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        var result = new Dictionary<string, List<RouteEndpoint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            foreach (var method in methods is { Count: > 0 } ? methods : new[] { "*" })
            {
                var key = Key(method, route);
                if (!result.TryGetValue(key, out var list)) result[key] = list = new List<RouteEndpoint>();
                list.Add(endpoint);
            }
        }
        return result;
    }

    private static string Key(string method, string route) => $"{method.ToUpperInvariant()} {route.TrimEnd('/') switch { "" => "/", var r => r }}";

    /// <summary>Guard implied by the endpoint's authorization metadata plus the fallback policy.</summary>
    private static Gate EffectiveGate(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) return Gate.Anonymous;
        var perms = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(a => RequirePermissionAttribute.Parse(a.Policy)).Where(p => p is not null).ToList();
        if (perms.Count == 0) return Gate.Authenticated;
        Assert.True(perms.Count == 1, "An endpoint should carry one permission requirement");
        return Gate.Perm(perms[0]!);
    }
}
