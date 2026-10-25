using System.Security.Cryptography;
using FactoryBrain.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Process environment for an in-process API host. <c>Program.cs</c> reads
/// its configuration with <see cref="Environment.GetEnvironmentVariable(string)"/>
/// while the host is being built, and every test host lives in this one
/// process, so each factory applies the FULL set of managed variables right
/// before its host is built (test parallelization is off; see
/// <c>AssemblyInfo.cs</c>). Anything a host doesn't set explicitly is cleared,
/// so one host's provider keys or database can never leak into another.
/// </summary>
public static class TestEnvironment
{
    public static readonly string[] ManagedVariables =
    {
        "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT",
        "FACTORYBRAIN_DB", "DOTNET_CONNECTION_STRING", "ConnectionStrings__Postgres",
        "JWT_SIGNING_KEY", "ADMIN_API_TOKEN",
        "SEED_ADMIN_EMAIL", "SEED_ADMIN_PASSWORD", "SEED_VIEWER_EMAIL", "SEED_VIEWER_PASSWORD",
        "SEED_OPERATOR_EMAIL", "SEED_OPERATOR_PASSWORD",
        "SIMULATOR_ENABLED", "SIMULATOR_SEED",
        "LLM_PROVIDER", "LLM_API_KEY", "LLM_MODEL", "LLM_BASE_URL", "GOOGLE_API_KEY",
        "RAG_EMBEDDING_PROVIDER", "RAG_EMBEDDING_MODEL", "RAG_EMBEDDING_API_KEY", "RAG_EMBEDDING_DIMENSIONS",
        "RAG_EMBEDDING_FAKE_FAIL", "RAG_EMBEDDING_FAKE_FAIL_AFTER", "RAG_HEALTH_INTERVAL_SECONDS",
        "RAG_HYBRID_BM25_WEIGHT", "RAG_HYBRID_VECTOR_WEIGHT",
        "FORWARDED_HEADERS_ENABLED", "TRUSTED_PROXIES", "AUTH_COOKIE_SAMESITE", "CORS_ORIGINS",
        "LOGIN_RATE_LIMIT_PER_MINUTE",
        "AI_SETTINGS_ENCRYPTION_KEY", "AI_SETTINGS_ENCRYPTION_KEY_FILE", "AI_SETTINGS_ENCRYPTION_KEY_PREVIOUS",
        "AI_SETTINGS_REFRESH_SECONDS", "RAG_EMBEDDING_PRICE_PER_MILLION_TOKENS_USD",
        "ALLOW_SELF_REGISTRATION", "PUBLIC_APP_URL", "SWAGGER_ENABLED",
        "SMTP_HOST", "SMTP_PORT", "SMTP_USERNAME", "SMTP_PASSWORD", "SMTP_FROM", "SMTP_ENABLE_SSL",
    };

    private static readonly object Gate = new();

    public static void Apply(IReadOnlyDictionary<string, string?> values)
    {
        lock (Gate)
        {
            foreach (var name in ManagedVariables)
                Environment.SetEnvironmentVariable(name, values.TryGetValue(name, out var v) ? v : null);
            foreach (var (name, value) in values)
                if (!ManagedVariables.Contains(name))
                    throw new ArgumentException($"{name} is not a managed test variable; add it to ManagedVariables.");
        }
    }

    /// <summary>
    /// Remove the API's background services from a test host: the simulator
    /// and the RAG health monitor. Both run on timers and read provider
    /// settings from the process environment when they fire, which another
    /// test host in this process may have changed in the meantime. The
    /// startup embedding probe in Program.cs still runs synchronously.
    /// The health monitor is registered through a factory delegate, so the
    /// check below insists on finding exactly one such registration rather
    /// than silently removing something else.
    /// </summary>
    public static void RemoveBackgroundServices(IServiceCollection services)
    {
        var simulator = services.Where(d => d.ServiceType == typeof(IHostedService)
                                            && d.ImplementationType == typeof(SimulatorHostedService)).ToList();
        var factoryRegistered = services.Where(d => d.ServiceType == typeof(IHostedService)
                                                    && d.ImplementationFactory is not null).ToList();
        if (simulator.Count != 1 || factoryRegistered.Count != 1)
            throw new InvalidOperationException(
                $"Expected 1 simulator and 1 factory-registered hosted service (RagHealthMonitor); " +
                $"found {simulator.Count} and {factoryRegistered.Count}. Update RemoveBackgroundServices.");
        foreach (var d in simulator.Concat(factoryRegistered)) services.Remove(d);
    }

    /// <summary>A fresh random 32-byte AI settings encryption key (base64), as the API expects it.</summary>
    public static string RandomEncryptionKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>A random value of the given byte length, base64url-encoded. Never a fixed secret.</summary>
    public static string RandomSecret(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
