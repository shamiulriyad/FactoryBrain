namespace FactoryBrain.Api.Middleware;

/// <summary>
/// Rate-limit policy names and budgets, shared by <c>Program.cs</c>, the
/// controllers' <c>[EnableRateLimiting]</c> attributes and the tests.
/// </summary>
public static class RateLimitPolicies
{
    public const string Login            = "login";
    /// <summary>Sign-up, forgot and reset password: same per-IP budget (and override) as login.</summary>
    public const string Account          = "account";
    public const string SensorSimulation = "sensor-sim";
    public const string SensorLive       = "sensor-live";

    public const int DefaultLoginPerMinute     = 5;
    public const int SensorSimulationPerMinute = 20;
    public const int SensorLivePerMinute       = 120;

    /// <summary>
    /// Login attempts per client IP per minute: <c>LOGIN_RATE_LIMIT_PER_MINUTE</c>
    /// when it is an integer in 1–1000, otherwise <see cref="DefaultLoginPerMinute"/>.
    /// </summary>
    public static int LoginPerMinute()
    {
        var raw = Environment.GetEnvironmentVariable("LOGIN_RATE_LIMIT_PER_MINUTE");
        return int.TryParse(raw, out var n) && n is >= 1 and <= 1000 ? n : DefaultLoginPerMinute;
    }

    /// <summary>
    /// Partition key for per-user budgets: the JWT <c>sub</c> claim, or the
    /// client IP when the request is unauthenticated (such requests are
    /// rejected by authorization before reaching these endpoints anyway).
    /// </summary>
    public static string UserPartition(HttpContext ctx)
    {
        var sub = ctx.User.FindFirst("sub")?.Value;
        return sub is { Length: > 0 }
            ? "user:" + sub
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
