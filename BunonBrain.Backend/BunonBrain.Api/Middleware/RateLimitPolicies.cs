namespace BunonBrain.Api.Middleware;

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
    /// <summary>Per-user budget on QC image uploads (POST /api/qc/inspections).</summary>
    public const string QcUpload         = "qc-upload";
    /// <summary>Per-IP budget on public website forms (POST /api/inquiries).</summary>
    public const string Inquiry          = "inquiry";
    /// <summary>Per device (X-Device-Id) and IP budget on the device-key IoT endpoint, before the key is checked.</summary>
    public const string IotDevice        = "iot-device";
    /// <summary>Per-user budget on camera AI sessions and frames (/api/monitoring/sessions); the session service also enforces its own frame interval.</summary>
    public const string CameraAi         = "camera-ai";

    public const int DefaultLoginPerMinute     = 5;
    public const int SensorSimulationPerMinute = 20;
    public const int SensorLivePerMinute       = 120;
    public const int QcUploadPerMinute         = 30;
    public const int InquiryPerMinute          = 3;
    public const int IotDevicePerMinute        = 1200;
    /// <summary>1 frame per second plus starts, stops and status reads, with headroom for a second tab.</summary>
    public const int CameraAiPerMinute         = 180;

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
        // Demo reviewers share demo accounts: each reviewer session gets its own budget.
        var demoSession = ctx.User.FindFirst(BunonBrain.Application.Access.AccessClaims.DemoSession)?.Value;
        if (demoSession is { Length: > 0 }) return "demo:" + demoSession;
        var sub = ctx.User.FindFirst("sub")?.Value;
        return sub is { Length: > 0 }
            ? "user:" + sub
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
