using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FactoryBrain.Api.Middleware;

namespace FactoryBrain.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests
{
    private readonly ApiFactory _factory;
    public AuthenticationTests(ApiFactory factory) { _factory = factory; factory.Activate(); }

    private async Task<HttpStatusCode> SendWithToken(string token, string method, string route, string? body = "{}")
    {
        using var client = _factory.CreateClient();
        using var request = EndpointMatrix.Request(method, route, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Admin_token_signed_with_a_different_key_is_rejected()
    {
        var forged = ApiFactory.ForgeToken(Roles.Admin, RandomNumberGenerator.GetBytes(48));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendWithToken(forged, "POST", "/api/settings/llm", """{"clearAll":true}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendWithToken(forged, "GET", "/api/sensors/snapshot"));
    }

    [Fact]
    public async Task Expired_admin_token_is_rejected()
    {
        var now = DateTime.UtcNow;
        var expired = ApiFactory.ForgeToken(Roles.Admin, ApiFactory.RealSigningKeyBytes,
            expires: now.AddMinutes(-10), notBefore: now.AddMinutes(-30));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendWithToken(expired, "GET", "/api/sensors/snapshot"));
    }

    [Fact]
    public async Task Token_with_the_real_key_is_accepted()
    {
        // Control for the two tests above: same forging path, correct key.
        var valid = ApiFactory.ForgeToken(Roles.Viewer, ApiFactory.RealSigningKeyBytes);
        Assert.Equal(HttpStatusCode.OK, await SendWithToken(valid, "GET", "/api/sensors/snapshot"));
    }

    [Fact]
    public async Task Garbage_bearer_value_is_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await SendWithToken("not-a-jwt", "GET", "/api/sensors/snapshot"));
    }

    [Theory]
    [InlineData("/api/settings/llm")]
    [InlineData("/api/rag/ingest")]
    [InlineData("/api/rag/reindex")]
    public async Task Retired_shared_admin_token_header_grants_nothing(string route)
    {
        using var client = _factory.CreateClient();
        using var request = EndpointMatrix.Request("POST", route);
        request.Headers.Add("X-Admin-Token", ApiFactory.LegacyAdminToken); // the server's configured value
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Retired_admin_token_does_not_upgrade_a_viewer()
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        using var request = EndpointMatrix.Request("POST", "/api/settings/llm", """{"clearAll":true}""");
        request.Headers.Add("X-Admin-Token", ApiFactory.LegacyAdminToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Hub_negotiate_requires_a_token()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsync("/hubs/factory/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public sealed class DatabaseHostGuardTests
{
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=x;Username=u;Password=p", true)]
    [InlineData("Host=127.0.0.1;Database=x", true)]
    [InlineData("Host=::1;Database=x", true)]
    [InlineData("Host=/var/run/postgresql;Database=x", true)]
    [InlineData("Host=localhost,127.0.0.1;Database=x", true)]
    [InlineData("Host=db.example.supabase.co;Database=x", false)]
    [InlineData("Host=aws-0-ap-south-1.pooler.supabase.com;Port=5432;Database=postgres", false)]
    [InlineData("Host=localhost,db.example.com;Database=x", false)]
    [InlineData("Host=10.0.0.5;Database=x", false)]
    [InlineData("Database=x", false)]
    [InlineData("this is not a connection string", false)]
    public void Only_local_hosts_count_as_loopback(string connectionString, bool expected)
    {
        Assert.Equal(expected, Program.IsLoopbackDatabase(connectionString));
    }
}
