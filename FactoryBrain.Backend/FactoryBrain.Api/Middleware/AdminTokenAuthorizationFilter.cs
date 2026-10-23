using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FactoryBrain.Api.Middleware;

/// <summary>
/// Admin-token authorization filter bound to the three write endpoints
/// that mutate server state: <c>POST /api/settings/llm</c>,
/// <c>POST /api/rag/reindex</c>, <c>POST /api/rag/ingest</c>. The check is
/// attached with <see cref="AdminTokenAttribute"/> directly on the action,
/// NOT by URL/path matching in middleware — the filter short-circuits
/// before any controller body runs, no token-matching logic depends on
/// the raw request path, and a path-spelling variant (trailing slash,
/// mixed case, double slash, percent-encoded slash) simply never reaches
/// the action filter because the routing system itself doesn't match it.
///
/// <list type="bullet">
///   <item><b>Development</b>: nothing to do — the host is wide open the way
///   the existing frontend contract expects.</item>
///   <item><b>Anywhere else</b>: requires <c>X-Admin-Token</c> to equal the
///   server-side <c>ADMIN_API_TOKEN</c> env var, compared in constant time so
///   a timing oracle can't be used to learn the secret.</item>
///   <item>If <c>ADMIN_API_TOKEN</c> is unset we return <b>403 Forbidden</b>
///   ("admin token not configured on the server").</item>
///   <item>If the header is missing or wrong we return <b>401 Unauthorized</b>
///   ("admin token missing or invalid"). Both use the canonical
///   <c>{error, details}</c> envelope. The token value itself is
///   never logged.</item>
/// </list>
///
/// GET routes and <c>/api/ask</c> stay open as the spec requires — they
/// are NOT decorated with <see cref="AdminTokenAttribute"/> and pass
/// straight through the pipeline.
/// </summary>
public sealed class AdminTokenAuthorizationFilter : IAsyncAuthorizationFilter
{
    internal const string HeaderName       = "X-Admin-Token";
    internal const string EnvVarName       = "ADMIN_API_TOKEN";
    internal const string ErrorNotConfigured = "AdminTokenNotConfigured";
    internal const string ErrorMissing        = "AdminTokenMissing";
    internal const string ErrorInvalid        = "AdminTokenInvalid";

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AdminTokenAuthorizationFilter> _log;

    public AdminTokenAuthorizationFilter(
        IWebHostEnvironment env,
        ILogger<AdminTokenAuthorizationFilter> log)
    {
        _env = env; _log = log;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        if (_env.IsDevelopment())
            return Task.CompletedTask;

        // If the request carries a valid bearer JWT with role Admin, the
        // modern AdminOrLegacyToken policy will pass on its own. We must
        // NOT also require X-Admin-Token — that would defeat the JWT path.
        // A non-Admin JWT (Viewer, etc.) is left alone: the policy will
        // 403 it, which is the spec-correct outcome.
        if (ctx.HttpContext.User?.Identity?.IsAuthenticated == true
            && ctx.HttpContext.User.IsInRole("Admin"))
        {
            return Task.CompletedTask;
        }

        StampNoStore(ctx);

        var configured = (Environment.GetEnvironmentVariable(EnvVarName)
                          ?? string.Empty).Trim();
        if (configured.Length == 0)
        {
            WriteEnvelope(ctx, StatusCodes.Status403Forbidden,
                ErrorNotConfigured,
                "admin token not configured on the server (set ADMIN_API_TOKEN)");
            return Task.CompletedTask;
        }

        var headers = ctx.HttpContext.Request.Headers;
        if (!headers.TryGetValue(HeaderName, out var supplied)
            || supplied.Count == 0
            || string.IsNullOrEmpty(supplied[0]))
        {
            WriteEnvelope(ctx, StatusCodes.Status401Unauthorized,
                ErrorMissing,
                "admin token missing or invalid");
            return Task.CompletedTask;
        }

        if (!FixedTimeEquals(configured, supplied[0]!))
        {
            WriteEnvelope(ctx, StatusCodes.Status401Unauthorized,
                ErrorInvalid,
                "admin token missing or invalid");
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    /// <summary>Length-stable comparison (constant-time for matching lengths).</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    private static void StampNoStore(AuthorizationFilterContext ctx)
    {
        if (!ctx.HttpContext.Response.Headers.ContainsKey("Cache-Control"))
            ctx.HttpContext.Response.Headers["Cache-Control"] = "no-store";
    }

    private void WriteEnvelope(AuthorizationFilterContext ctx, int status,
                               string error, string details)
    {
        StampNoStore(ctx);
        // Body is wrapped in the canonical { error, details } envelope.
        // The token value (correct or otherwise) never appears here.
        ctx.Result = new ObjectResult(new { error, details })
        {
            StatusCode = status
        };
    }
}

/// <summary>
/// Marks a controller action as protected by the admin-token gate.
/// Apply on every <c>POST /api/{settings/llm,rag/reindex,rag/ingest}</c>
/// action. Register <see cref="AdminTokenAuthorizationFilter"/> in DI via
/// <c>builder.Services.AddScoped&lt;AdminTokenAuthorizationFilter&gt;()</c>.
/// The filter short-circuits the action in non-Development environments,
/// leaving Development requests untouched.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AdminTokenAttribute : ServiceFilterAttribute
{
    public AdminTokenAttribute()
        : base(typeof(AdminTokenAuthorizationFilter))
    { }
}
