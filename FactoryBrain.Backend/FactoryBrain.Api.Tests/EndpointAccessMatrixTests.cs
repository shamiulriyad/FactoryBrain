using System.Net;
using System.Text;
using FactoryBrain.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryBrain.Api.Tests;

public enum Access { Anonymous, Authenticated, Operator, Admin }

/// <summary>
/// The declared access level of every endpoint the API maps. Adding an
/// endpoint without adding a row here fails
/// <see cref="EndpointAccessMatrixTests.Every_mapped_endpoint_has_a_declared_access_level"/>.
/// </summary>
public static class EndpointMatrix
{
    public static readonly IReadOnlyList<(string Method, string Route, Access Access)> Rows = new[]
    {
        // Intentionally public.
        ("*",     "/health",                    Access.Anonymous),
        ("POST",  "/api/auth/login",            Access.Anonymous),
        ("POST",  "/api/auth/refresh",          Access.Anonymous),
        ("POST",  "/api/auth/logout",           Access.Anonymous),
        ("GET",   "/api/auth/options",          Access.Anonymous),
        ("POST",  "/api/auth/register",         Access.Anonymous),
        ("POST",  "/api/auth/forgot-password",  Access.Anonymous),
        ("POST",  "/api/auth/reset-password",   Access.Anonymous),

        // Any signed-in user (Viewer, Operator, Admin).
        ("GET",   "/",                          Access.Authenticated),
        ("GET",   "/api/auth/me",               Access.Authenticated),
        ("POST",  "/api/ask",                   Access.Authenticated),
        ("GET",   "/api/ask/status",            Access.Authenticated),
        ("GET",   "/api/agents",                Access.Authenticated),
        ("GET",   "/api/approvals",             Access.Authenticated),
        ("GET",   "/api/brief/morning",         Access.Authenticated),
        ("GET",   "/api/floor-alerts",          Access.Authenticated),
        ("GET",   "/api/line-board",            Access.Authenticated),
        ("GET",   "/api/qc/defects",            Access.Authenticated),
        ("GET",   "/api/qc/inspections",        Access.Authenticated),
        ("GET",   "/api/qc/inspections/{id}",   Access.Authenticated),
        ("GET",   "/api/qc/inspections/{id}/image", Access.Authenticated),
        ("GET",   "/api/qc/summary",            Access.Authenticated),
        ("GET",   "/api/qc/vision/status",      Access.Authenticated),
        ("GET",   "/api/qc/categories",         Access.Authenticated),
        ("GET",   "/api/rag/documents",         Access.Authenticated),
        ("GET",   "/api/rag/status",            Access.Authenticated),
        ("GET",   "/api/sensors/ingest",        Access.Authenticated),
        ("GET",   "/api/sensors/latest",        Access.Authenticated),
        ("GET",   "/api/sensors/snapshot",      Access.Authenticated),
        ("GET",   "/api/settings/llm",          Access.Authenticated),
        ("GET",   "/api/settings/embedding",    Access.Authenticated),
        ("GET",   "/api/vision/analyze",        Access.Authenticated),
        ("GET",   "/api/production/config",     Access.Authenticated),
        ("GET",   "/api/production/lines",      Access.Authenticated),
        ("GET",   "/api/production/lines/{id}/detail", Access.Authenticated),
        ("GET",   "/api/production/shifts",     Access.Authenticated),
        ("GET",   "/api/production/styles",     Access.Authenticated),
        ("GET",   "/api/production/styles/{id}", Access.Authenticated),
        ("GET",   "/api/production/orders",     Access.Authenticated),
        ("GET",   "/api/production/orders/{id}", Access.Authenticated),
        ("GET",   "/api/production/orders/{id}/risk", Access.Authenticated),
        ("GET",   "/api/production/plans",      Access.Authenticated),
        ("GET",   "/api/production/plans/{id}", Access.Authenticated),
        ("GET",   "/api/production/output",     Access.Authenticated),
        ("GET",   "/api/production/summary",    Access.Authenticated),
        ("GET",   "/api/production/observations", Access.Authenticated),
        ("GET",   "/api/production/layouts/current", Access.Authenticated),
        ("GET",   "/api/production/balance/proposals", Access.Authenticated),
        ("GET",   "/api/production/balance/proposals/{id}", Access.Authenticated),
        ("GET",   "/api/merchandising/config",  Access.Authenticated),
        ("GET",   "/api/merchandising/overview", Access.Authenticated),
        ("GET",   "/api/merchandising/orders",  Access.Authenticated),
        ("GET",   "/api/merchandising/orders/{id}", Access.Authenticated),
        ("GET",   "/api/merchandising/documents", Access.Authenticated),
        ("GET",   "/api/merchandising/documents/{id}", Access.Authenticated),
        ("GET",   "/api/merchandising/versions/{id}", Access.Authenticated),
        ("GET",   "/api/merchandising/versions/{id}/file", Access.Authenticated),
        ("*",     "/hubs/factory/negotiate",    Access.Authenticated),
        ("*",     "/hubs/factory",              Access.Authenticated),

        // Operational writes: Operator or Admin.
        ("POST",  "/api/agents/{agentId}/run",  Access.Operator),
        ("POST",  "/api/approvals/{id}/approve", Access.Operator),
        ("POST",  "/api/approvals/{id}/reject", Access.Operator),
        ("PATCH", "/api/floor-alerts/{id}",     Access.Operator),
        ("POST",  "/api/line-board/refresh",    Access.Operator),
        ("POST",  "/api/qc/flag",               Access.Operator),
        ("POST",  "/api/qc/inspections",        Access.Operator),
        ("POST",  "/api/qc/inspections/{id}/review", Access.Operator),
        ("POST",  "/api/qc/inspections/{id}/retry",  Access.Operator),
        ("POST",  "/api/sensors/ingest",        Access.Operator),
        ("POST",  "/api/sensors/live",          Access.Operator),
        ("POST",  "/api/vision/analyze",        Access.Operator),
        ("POST",  "/api/production/orders",     Access.Operator),
        ("PUT",   "/api/production/orders/{id}", Access.Operator),
        ("POST",  "/api/production/plans",      Access.Operator),
        ("PUT",   "/api/production/plans/{id}", Access.Operator),
        ("POST",  "/api/production/plans/{id}/submit",   Access.Operator),
        ("POST",  "/api/production/plans/{id}/activate", Access.Operator),
        ("POST",  "/api/production/plans/{id}/changes/{changeId}/resolve", Access.Operator),
        ("POST",  "/api/production/plans/{id}/withdraw", Access.Operator),
        ("POST",  "/api/production/plans/{id}/changes/{changeId}/withdraw", Access.Operator),
        ("POST",  "/api/production/plans/{id}/complete", Access.Operator),
        ("POST",  "/api/production/plans/{id}/cancel",   Access.Operator),
        ("POST",  "/api/production/output",     Access.Operator),
        ("POST",  "/api/production/output/{id}/correct", Access.Operator),
        ("POST",  "/api/production/output/import", Access.Operator),
        ("POST",  "/api/production/observations", Access.Operator),
        ("PUT",   "/api/production/layouts/current", Access.Operator),
        ("POST",  "/api/production/balance/analyze", Access.Operator),
        ("POST",  "/api/production/balance/proposals/{id}/submit", Access.Operator),
        ("POST",  "/api/production/balance/proposals/{id}/apply",  Access.Operator),
        ("POST",  "/api/merchandising/orders",  Access.Operator),
        ("PUT",   "/api/merchandising/orders/{id}", Access.Operator),
        ("POST",  "/api/merchandising/orders/{id}/milestones", Access.Operator),
        ("PUT",   "/api/merchandising/milestones/{id}", Access.Operator),
        ("POST",  "/api/merchandising/conflicts/{id}/resolve", Access.Operator),
        ("POST",  "/api/merchandising/orders/{id}/documents", Access.Operator),
        ("POST",  "/api/merchandising/documents/{id}/versions", Access.Operator),
        ("POST",  "/api/merchandising/versions/{id}/retry", Access.Operator),
        ("POST",  "/api/merchandising/versions/{id}/confirm", Access.Operator),

        // Configuration and index management: Admin only.
        ("GET",   "/api/audit",                 Access.Admin),
        ("POST",  "/api/qc/categories",         Access.Admin),
        ("PUT",   "/api/qc/categories/{code}",  Access.Admin),
        ("POST",  "/api/production/lines",      Access.Admin),
        ("PUT",   "/api/production/lines/{id}", Access.Admin),
        ("POST",  "/api/production/shifts",     Access.Admin),
        ("PUT",   "/api/production/shifts/{code}", Access.Admin),
        ("POST",  "/api/production/styles",     Access.Admin),
        ("PUT",   "/api/production/styles/{id}", Access.Admin),
        ("POST",  "/api/rag/ingest",            Access.Admin),
        ("POST",  "/api/rag/reindex",           Access.Admin),
        ("GET",   "/api/rag/reindex/plan",      Access.Admin),
        ("POST",  "/api/settings/llm",          Access.Admin),
        ("POST",  "/api/settings/embedding",    Access.Admin),
        ("POST",  "/api/settings/embedding/plan", Access.Admin),
        ("GET",   "/api/users",                 Access.Admin),
        ("PATCH", "/api/users/{id}/role",       Access.Admin),
    };

    public static IEnumerable<object[]> Where(Func<Access, bool> filter) =>
        Rows.Where(r => filter(r.Access)).Select(r => new object[] { r.Method, r.Route, r.Access });

    /// <summary>Concrete request target for a route template.</summary>
    public static string Url(string route) => route
        .Replace("{agentId}", "line-throughput-agent")
        .Replace("{changeId}", "change-1")
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
                var actual = EffectiveAccess(endpoint);
                if (actual != row.Access)
                    mismatches.Add($"{row.Method} {row.Route}: declared {row.Access}, metadata gives {actual}");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
    }

    public static IEnumerable<object[]> ProtectedEndpoints => EndpointMatrix.Where(a => a != Access.Anonymous);
    public static IEnumerable<object[]> RoleEndpoints      => EndpointMatrix.Where(a => a is Access.Operator or Access.Admin);
    public static IEnumerable<object[]> AdminEndpoints     => EndpointMatrix.Where(a => a == Access.Admin);

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Anonymous_requests_to_protected_endpoints_get_401(string method, string route, Access _)
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(EndpointMatrix.Request(method, route));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RoleEndpoints))]
    public async Task Viewer_gets_403_on_operator_and_admin_endpoints(string method, string route, Access _)
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        using var response = await client.SendAsync(EndpointMatrix.Request(method, route));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Operator_gets_403_on_admin_endpoints(string method, string route, Access _)
    {
        using var client = _factory.ClientAs(Roles.Operator);
        using var response = await client.SendAsync(EndpointMatrix.Request(method, route));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Operator")]
    [InlineData("Admin")]
    public async Task Any_signed_in_role_can_read_authenticated_endpoints(string role)
    {
        // Endpoints whose handlers need no database, so the full request
        // completes against the in-process host. (The settings reads now
        // come from the database; DbAiSettingsTests covers them per role.)
        using var client = _factory.ClientAs(role);
        foreach (var path in new[] { "/api/qc/vision/status", "/api/sensors/snapshot", "/api/sensors/ingest", "/api/merchandising/config" })
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{role} GET {path} → {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Admin_passes_authorization_on_an_admin_only_endpoint()
    {
        // Settings now live in the database, which this host doesn't have:
        // the request gets past authentication and the Admin policy, reaches
        // the settings store, and fails closed with 503 (never a fallback to
        // the environment). The 200 path is DbAiSettingsTests'
        // Admin_clear_all_returns_demo_and_viewer_reads_it.
        using var client = _factory.ClientAs(Roles.Admin);
        using var response = await client.SendAsync(
            EndpointMatrix.Request("POST", "/api/settings/llm", """{"clearAll":true}"""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":\"settings_unavailable\"", body);
        Assert.Contains("\"traceId\":", body);
    }

    [Fact]
    public async Task Public_endpoints_do_not_demand_authentication()
    {
        using var client = _factory.CreateClient();

        // Health is reachable; the closed test database makes it Unhealthy.
        using (var health = await client.GetAsync("/health"))
            Assert.True(health.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
                $"/health → {(int)health.StatusCode}");

        // Login reaches validation (no database for an empty body).
        using (var login = await client.SendAsync(EndpointMatrix.Request("POST", "/api/auth/login", "{}")))
            Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);

        // Logout with no cookie is a no-op 204.
        using (var logout = await client.SendAsync(EndpointMatrix.Request("POST", "/api/auth/logout", null)))
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // Refresh with no cookie is the controller's own 401, not the auth challenge.
        using (var refresh = await client.SendAsync(EndpointMatrix.Request("POST", "/api/auth/refresh", null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
            Assert.Contains("Missing refresh token", await refresh.Content.ReadAsStringAsync());
        }
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

    /// <summary>Access level implied by the endpoint's authorization metadata plus the fallback policy.</summary>
    private static Access EffectiveAccess(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) return Access.Anonymous;
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy).Where(p => p is not null).ToList();
        if (policies.Contains(AuthPolicies.Admin)) return Access.Admin;
        if (policies.Contains(AuthPolicies.Operator)) return Access.Operator;
        return Access.Authenticated; // [Authorize] without a policy, or the fallback policy
    }
}
