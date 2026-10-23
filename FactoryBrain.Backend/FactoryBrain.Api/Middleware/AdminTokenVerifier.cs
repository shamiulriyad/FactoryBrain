using System.Security.Cryptography;
using System.Text;

namespace FactoryBrain.Api.Middleware;

/// <summary>
/// Singleton that mirrors the legacy <see cref="AdminTokenAuthorizationFilter"/>
/// check for use by the <c>AdminOrLegacyToken</c> authorization policy.
/// Same constant-time comparison, same <c>ADMIN_API_TOKEN</c> env var,
/// same "missing / invalid / not-configured" semantics — except this
/// returns a structured result so the policy handler can decide
/// whether to pass or 403 instead of writing an envelope itself. The
/// existing <see cref="AdminTokenAuthorizationFilter"/> keeps producing
/// its <c>{error, details}</c> envelope when applied directly via the
/// <c>[AdminToken]</c> attribute, so the two paths are byte-identical
/// for callers using only <c>X-Admin-Token</c>.
/// </summary>
public sealed class AdminTokenVerifier
{
    /// <summary>Outcome of the legacy token check.</summary>
    public enum Outcome
    {
        /// <summary>Development bypass — no token required, no check performed.</summary>
        PassDevelopment,
        /// <summary><c>X-Admin-Token</c> matched <c>ADMIN_API_TOKEN</c>.</summary>
        Pass,
        /// <summary><c>ADMIN_API_TOKEN</c> is not set on the server.</summary>
        NotConfigured,
        /// <summary>Header missing, empty, or didn't match.</summary>
        MissingOrInvalid,
    }

    private readonly IHostEnvironment _env;
    private readonly string _configured;

    public AdminTokenVerifier(IHostEnvironment env)
    {
        _env = env;
        _configured = (Environment.GetEnvironmentVariable("ADMIN_API_TOKEN") ?? string.Empty).Trim();
    }

    public Outcome Verify(HttpContext context)
    {
        if (_env.IsDevelopment())
            return Outcome.PassDevelopment;

        if (_configured.Length == 0)
            return Outcome.NotConfigured;

        var headers = context.Request.Headers;
        if (!headers.TryGetValue("X-Admin-Token", out var supplied)
            || supplied.Count == 0
            || string.IsNullOrEmpty(supplied[0]))
        {
            return Outcome.MissingOrInvalid;
        }
        return FixedTimeEquals(_configured, supplied[0]!)
            ? Outcome.Pass
            : Outcome.MissingOrInvalid;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
