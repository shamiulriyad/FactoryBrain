using System.Text.RegularExpressions;
using BunonBrain.Application.Demo;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Middleware;

/// <summary>
/// Demo requests only (routed to the demo database by <see cref="DemoRoutingMiddleware"/>):
/// refuses the operations that would break the shared demo for other reviewers
/// or reach outside it. Production requests are never affected. Everything
/// else — creating and updating records, approvals, simulators — works as in
/// production, under the normal permission checks, and the daily reset brings
/// the baseline back.
/// <list type="bullet">
///   <item>Deleting anything (the demo allows create, read and update).</item>
///   <item>Password sign-in, sign-up and password reset (only the shared demo credential signs in).</item>
///   <item>Changing the demo accounts: their profile, password, status, roles, scope or worker link.</item>
///   <item>Deactivating a factory.</item>
///   <item>Platform settings: AI providers and keys, the public website, the knowledge index.</item>
///   <item>Paid AI calls (they would use the production deployment's provider key): the assistant,
///   agent runs, camera AI, image inspection and AI document extraction.</item>
/// </list>
/// </summary>
public sealed partial class DemoGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DemoOptions _opts;

    public DemoGuardMiddleware(RequestDelegate next, DemoOptions opts) { _next = next; _opts = opts; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (_opts.Enabled && DemoRoute.IsDemo && Blocked(ctx.Request.Method, ctx.Request.Path.Value ?? "") is { } why)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/problem+json";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            var pd = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Not available in the demo",
                Detail = why.Message,
                Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4",
                Instance = ctx.Request.Path,
            };
            pd.Extensions["code"] = why.Code;
            await ctx.Response.WriteAsJsonAsync(pd, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ctx.RequestAborted);
            return;
        }
        await _next(ctx);
    }

    public sealed record Refusal(string Code, string Message);

    private static readonly Refusal SignInOnly = new("demo_sign_in_only", "The demo signs in only with the shared demo credential at /explore-demo.");
    private static readonly Refusal NoDelete = new("demo_no_delete", "Deleting is turned off in the demo. The data is restored to its baseline every day.");
    private static readonly Refusal DemoAccount = new("demo_account_protected", "The demo accounts are shared by every reviewer and can't be changed.");
    private static readonly Refusal Platform = new("demo_platform_locked", "Platform settings (AI keys, website, knowledge index) are locked in the demo.");
    private static readonly Refusal Factory = new("demo_factory_locked", "Factories can't be deactivated in the demo.");
    private static readonly Refusal PaidAi = new("demo_ai_off", "AI features that call an external AI provider are switched off in the demo.");

    [GeneratedRegex(@"^/api/users/(?<id>[0-9a-fA-F-]{36})(/(status|roles|scope|reset-password))?/?$")]
    private static partial Regex UserWrite();

    [GeneratedRegex(@"^/api/workers/[^/]+/account/?$")]
    private static partial Regex WorkerAccount();

    [GeneratedRegex(@"^/api/factories/[^/]+/status/?$")]
    private static partial Regex FactoryStatus();

    [GeneratedRegex(@"^/api/(ask(/.*)?|agents/[^/]+/run|vision/analyze|monitoring/sessions(/.*)?|qc/inspections(/[^/]+/retry)?|merchandising/orders/[^/]+/documents|merchandising/versions/[^/]+/retry|documents/classify|documents/[^/]+/(summary|draft-text|files/[^/]+/extract))/?$")]
    private static partial Regex PaidAiRoute();

    /// <summary>Why the request is refused in demo mode, or null when it may proceed.</summary>
    public static Refusal? Blocked(string method, string path)
    {
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return null;
        var p = path.ToLowerInvariant();

        if (HttpMethods.IsDelete(method)) return NoDelete;

        if (p is "/api/auth/login" or "/api/auth/register" or "/api/auth/forgot-password" or "/api/auth/reset-password") return SignInOnly;
        if (p.StartsWith("/api/account/")) return DemoAccount;
        if (WorkerAccount().IsMatch(p)) return DemoAccount;
        if (UserWrite().Match(p) is { Success: true } m && Guid.TryParse(m.Groups["id"].Value, out var userId) && DemoIdentities.IsDemoIdentity(userId))
            return DemoAccount;

        if (FactoryStatus().IsMatch(p)) return Factory;
        if (p.StartsWith("/api/settings/") || p.StartsWith("/api/site-settings/") || p.StartsWith("/api/rag/")) return Platform;
        if (PaidAiRoute().IsMatch(p)) return PaidAi;
        return null;
    }
}
