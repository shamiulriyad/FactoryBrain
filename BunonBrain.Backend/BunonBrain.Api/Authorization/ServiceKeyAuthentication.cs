using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using BunonBrain.Application.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Api.Authorization;

// Service-to-service authentication for the computer-vision service.
//
// A request carrying the X-Service-Key header is authenticated by this scheme
// instead of the JWT bearer scheme (see the "BunonBrain" policy scheme in
// Program.cs). The key comes from CV_SERVICE_KEY (at least 32 characters) and
// is kept only as a SHA-256 hash, compared in constant time. The principal it
// creates is not a person: it has no role, no password and no refresh token;
// its reach is the factories in CV_SERVICE_FACTORIES (factory codes, or "*")
// and it holds exactly one permission, monitoring.events.ingest, plus the
// "svc" claim the camera-configuration endpoint requires.

public static class ServiceKeyDefaults
{
    public const string Scheme = "ServiceKey";
    public const string Header = "X-Service-Key";
    public const string ComputerVision = "computer-vision";
    public const string KeyEnv = "CV_SERVICE_KEY";
    public const string FactoriesEnv = "CV_SERVICE_FACTORIES";
    public const int MinKeyLength = 32;

    /// <summary>Fixed id of the computer-vision service principal (audit, rate limits). Not a user row.</summary>
    public static readonly Guid ComputerVisionId = Guid.Parse("c0e0c0e0-5e7f-4c11-9a00-00000000c001");
    public const string ComputerVisionEmail = "service:computer-vision";

    /// <summary>What the service may do, and nothing else.</summary>
    public static readonly string[] ComputerVisionPermissions = { Permissions.MonitoringEventsIngest };
}

/// <summary>The configured service key (hashed) and factory reach.</summary>
public sealed class ServiceKeySettings
{
    public byte[]? KeyHash { get; init; }
    public bool AllFactories { get; init; }
    public IReadOnlyList<string> FactoryCodes { get; init; } = Array.Empty<string>();
    public string? Problem { get; init; }

    public bool Enabled => KeyHash is not null;

    public static ServiceKeySettings FromEnvironment(Func<string, string?> read)
    {
        var key = read(ServiceKeyDefaults.KeyEnv)?.Trim();
        if (string.IsNullOrEmpty(key)) return new ServiceKeySettings { Problem = $"{ServiceKeyDefaults.KeyEnv} is not set: the computer-vision service can't sign in." };
        if (key.Length < ServiceKeyDefaults.MinKeyLength)
            return new ServiceKeySettings { Problem = $"{ServiceKeyDefaults.KeyEnv} is shorter than {ServiceKeyDefaults.MinKeyLength} characters and was ignored." };
        var factories = (read(ServiceKeyDefaults.FactoriesEnv) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var all = factories.Length == 1 && factories[0] == "*";
        return new ServiceKeySettings
        {
            KeyHash = Hash(key),
            AllFactories = all,
            FactoryCodes = all ? Array.Empty<string>() : factories.Select(f => f.ToUpperInvariant()).Distinct().ToArray(),
            Problem = !all && factories.Length == 0
                ? $"{ServiceKeyDefaults.FactoriesEnv} is empty: the computer-vision service reaches no factory." : null,
        };
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public bool Matches(string presented) =>
        KeyHash is not null && CryptographicOperations.FixedTimeEquals(Hash(presented.Trim()), KeyHash);

    // Factory codes resolved to ids, cached briefly (one host = one settings instance).
    internal (DateTimeOffset At, IReadOnlyList<Guid> Ids)? FactoryCache;
    internal readonly SemaphoreSlim FactoryLock = new(1, 1);
}

public sealed class ServiceKeyAuthenticationOptions : AuthenticationSchemeOptions { }

public sealed class ServiceKeyAuthenticationHandler : AuthenticationHandler<ServiceKeyAuthenticationOptions>
{
    private static readonly TimeSpan FactoryCacheFor = TimeSpan.FromMinutes(1);

    private readonly ServiceKeySettings _settings;
    private readonly IServiceScopeFactory _scopes;

    public ServiceKeyAuthenticationHandler(IOptionsMonitor<ServiceKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        ServiceKeySettings settings, IServiceScopeFactory scopes) : base(options, logger, encoder)
    {
        _settings = settings;
        _scopes = scopes;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ServiceKeyDefaults.Header, out var presented) || string.IsNullOrWhiteSpace(presented))
            return AuthenticateResult.NoResult();
        if (!_settings.Enabled) return AuthenticateResult.Fail("Service keys are not configured on this server.");
        if (!_settings.Matches(presented.ToString())) return AuthenticateResult.Fail("Invalid service key.");

        var factories = _settings.AllFactories ? Array.Empty<Guid>() : await FactoryIdsAsync(Context.RequestAborted);
        var claims = new List<Claim>
        {
            new(AccessClaims.Subject, ServiceKeyDefaults.ComputerVisionId.ToString()),
            new(AccessClaims.Email, ServiceKeyDefaults.ComputerVisionEmail),
            new(AccessClaims.Service, ServiceKeyDefaults.ComputerVision),
        };
        if (_settings.AllFactories) claims.Add(new Claim(AccessClaims.ServiceAllFactories, "1"));
        claims.AddRange(ServiceKeyDefaults.ComputerVisionPermissions.Select(p => new Claim(AccessClaims.Permission, p)));
        claims.AddRange(factories.Select(f => new Claim(AccessClaims.Factory, f.ToString())));
        var identity = new ClaimsIdentity(claims, Scheme.Name, AccessClaims.Email, AccessClaims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private async Task<IReadOnlyList<Guid>> FactoryIdsAsync(CancellationToken ct)
    {
        if (_settings.FactoryCache is { } c && DateTimeOffset.UtcNow - c.At < FactoryCacheFor) return c.Ids;
        await _settings.FactoryLock.WaitAsync(ct);
        try
        {
            if (_settings.FactoryCache is { } again && DateTimeOffset.UtcNow - again.At < FactoryCacheFor) return again.Ids;
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
            var codes = _settings.FactoryCodes.ToList();
            var ids = await db.Factories.AsNoTracking().Where(f => f.IsActive && codes.Contains(f.Code)).Select(f => f.Id).ToListAsync(ct);
            _settings.FactoryCache = (DateTimeOffset.UtcNow, ids);
            return ids;
        }
        finally { _settings.FactoryLock.Release(); }
    }
}

/// <summary>Only the named service may call the endpoint (people never can, whatever their permissions).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireServiceAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "svc:";

    public RequireServiceAttribute(string service)
    {
        Service = service;
        Policy = PolicyPrefix + service;
    }

    public string Service { get; }

    public static string? Parse(string? policy) =>
        policy is not null && policy.StartsWith(PolicyPrefix, StringComparison.Ordinal) ? policy[PolicyPrefix.Length..] : null;
}
