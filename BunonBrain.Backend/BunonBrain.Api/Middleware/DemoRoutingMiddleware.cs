using System.IdentityModel.Tokens.Jwt;
using BunonBrain.Application.Access;
using BunonBrain.Application.Demo;
using BunonBrain.Infrastructure.Demo;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Api.Middleware;

/// <summary>
/// Decides, before anything reads the database, whether a request belongs to
/// the interactive demo (DEMO_MODE=true). A demo request runs with
/// <see cref="DemoRoute"/> set, so every DbContext it uses (authentication's
/// session check included) connects to the demo database. A request is demo when:
/// <list type="bullet">
///   <item>it is a demo sign-in or operator route (<c>/api/auth/demo/*</c>, <c>/api/demo/*</c>);</item>
///   <item>its access token carries a demo session claim (the token is still fully
///   validated afterwards, and must then belong to a live demo session);</item>
///   <item>it refreshes or signs out with a refresh cookie that belongs to a demo session.</item>
/// </list>
/// Everything else, including every request without a token, is production.
/// Routing on the token's claim before validation is safe: a demo-routed token
/// that is not a valid demo token is rejected, and a production token on a
/// demo route is rejected too (Program.cs, OnTokenValidated).
/// </summary>
public sealed class DemoRoutingMiddleware
{
    public const string ItemKey = "bunonbrain.demo";

    private static readonly JwtSecurityTokenHandler Jwt = new();

    private readonly RequestDelegate _next;
    private readonly DemoOptions _opts;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DemoRoutingMiddleware> _log;

    public DemoRoutingMiddleware(RequestDelegate next, DemoOptions opts, IServiceScopeFactory scopes, ILogger<DemoRoutingMiddleware> log)
    {
        _next = next; _opts = opts; _scopes = scopes; _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (_opts.Enabled && await IsDemoAsync(ctx))
        {
            using (DemoRoute.Enter())
            {
                ctx.Items[ItemKey] = true;
                await _next(ctx);
            }
            return;
        }
        await _next(ctx);
    }

    private async Task<bool> IsDemoAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api/auth/demo", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/demo", StringComparison.OrdinalIgnoreCase))
            return true;

        // A token decides by itself: demo claim → demo, otherwise production.
        if (BearerToken(ctx) is { } token)
            return HasDemoClaim(token);

        if ((path.Equals("/api/auth/refresh", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
            && ctx.Request.Cookies.TryGetValue(AuthCookieSettings.CookieName, out var raw) && !string.IsNullOrWhiteSpace(raw) && raw.Length <= 256)
            return await IsDemoCookieAsync(raw, ctx.RequestAborted);

        return false;
    }

    private static string? BearerToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim();
        if (ctx.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
            && ctx.Request.Query.TryGetValue("access_token", out var q) && !string.IsNullOrWhiteSpace(q.ToString()))
            return q.ToString();
        return null;
    }

    private static bool HasDemoClaim(string token)
    {
        try
        {
            return Jwt.CanReadToken(token) && Jwt.ReadJwtToken(token).Claims.Any(c => c.Type == AccessClaims.DemoSession);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether the refresh cookie belongs to a demo session (looked up in the demo database only).</summary>
    private async Task<bool> IsDemoCookieAsync(string raw, CancellationToken ct)
    {
        try
        {
            using (DemoRoute.Enter())
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
                var hash = DemoSessionService.Hash(raw);
                return await db.DemoSessions.AsNoTracking().AnyAsync(s => s.RefreshTokenHash == hash, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Demo database unreachable: the cookie is treated as a production one.
            _log.LogWarning("Demo session lookup failed ({Error}); treating the request as production.", ex.GetType().Name);
            return false;
        }
    }
}
