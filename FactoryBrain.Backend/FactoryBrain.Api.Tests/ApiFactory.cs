using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.HostedServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// In-process API host for the integration tests.
/// <para>
/// Safety: the host runs in the <c>Testing</c> environment, which makes
/// <c>Program.cs</c> skip the repo-root <c>.env</c> and all database
/// initialisation. The connection string points at a closed loopback port,
/// so even an unexpected query cannot reach a real (local or remote)
/// database. Provider keys are cleared so nothing calls an external API.
/// </para>
/// <para>
/// The tests therefore cover authentication, authorization, validation and
/// rate limiting — everything that is decided before a service touches the
/// database. Database-backed behaviour is not exercised here.
/// </para>
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Port 1 on loopback: nothing listens there, connections are refused immediately.</summary>
    public const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=factorybrain_tests;Username=tests;Password=tests;Timeout=1;Command Timeout=1";

    /// <summary>Per-run random HS256 key (never a fixed value).</summary>
    public static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    /// <summary>Per-run random value for the retired shared admin token.</summary>
    public static readonly string LegacyAdminToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Every outbound HTTP call this host makes (none are expected).</summary>
    public OutboundHttpGuard Http { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Program.cs reads these with Environment.GetEnvironmentVariable
        // while the host is built, so they are applied immediately before.
        Activate();
        return base.CreateHost(builder);
    }

    /// <summary>
    /// Re-apply this host's process environment. Parts of the API read
    /// provider settings from the process environment per request (not only
    /// at startup), so every test class activates its host's environment
    /// before sending requests; otherwise another host's settings would leak in.
    /// </summary>
    public void Activate() =>
        TestEnvironment.Apply(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["DOTNET_ENVIRONMENT"]     = "Testing",
            ["FACTORYBRAIN_DB"]        = UnreachableDatabase,
            ["JWT_SIGNING_KEY"]        = SigningKey,
            ["ADMIN_API_TOKEN"]        = LegacyAdminToken,
            ["SIMULATOR_ENABLED"]      = "false",
            ["RAG_EMBEDDING_PROVIDER"] = "local",
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            OutboundHttpGuard.Install(services, Http);

            TestEnvironment.RemoveBackgroundServices(services);
        });
    }

    /// <summary>Client carrying a valid access token for a fresh user with the given role.</summary>
    public HttpClient ClientAs(string role)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(role));
        return client;
    }

    /// <summary>Access token issued by the API's own <see cref="ITokenService"/>.</summary>
    public string TokenFor(string role, Guid? userId = null)
    {
        var tokens = Services.GetRequiredService<ITokenService>();
        var user = new User
        {
            Id = userId ?? Guid.NewGuid(),
            Email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local",
            Role = role,
        };
        return tokens.IssueAccessToken(user, out _);
    }

    /// <summary>A structurally valid token with the given role, signed with <paramref name="key"/>.</summary>
    public static string ForgeToken(string role, byte[] key, DateTime? expires = null, DateTime? notBefore = null)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "factorybrain",
            audience: "factorybrain",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Email, "forged@tests.local"),
                new Claim(ClaimTypes.Role, role),
            },
            notBefore: notBefore ?? now.AddMinutes(-1),
            expires: expires ?? now.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static byte[] RealSigningKeyBytes => Encoding.UTF8.GetBytes(SigningKey);
}

/// <summary>One shared host for every test class (the API keeps static simulation state).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
