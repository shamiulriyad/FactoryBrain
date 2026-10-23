using System.Diagnostics;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// Authentication endpoints (batch 52). Two flows are wired here:
/// <list type="number">
///   <item><b>Cookie-based refresh</b> for browser flows. The
///   <c>fb_refresh</c> cookie is HttpOnly, scoped to
///   <c>/api/auth</c>, and stores a 32-byte random token whose SHA-256
///   hash is what lives in the <c>users</c> row. Every successful
///   refresh rotates the hash, so an intercepted cookie is invalidated
///   the moment the legitimate caller rotates it.</item>
///   <item><b>Bearer access tokens</b> (returned in the JSON body only,
///   never as a cookie) for the SPA / CLI flows that need to call the
///   rest of the API. Lifetime is 15 minutes; when it expires the
///   client calls <c>POST /api/auth/refresh</c> to obtain a fresh one
///   using the <c>fb_refresh</c> cookie.</item>
/// </list>
/// The 4 actions below cover both directions. Login is the only one
/// rate-limited (5/min/IP) because it is the only one an unauthenticated
/// attacker can hit; refresh requires possession of the cookie, so a
/// brute-force on the cookie space is what the random 32 bytes defend
/// against, not a rate limit.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly ITokenService _tokens;
    private readonly IHostEnvironment _env;
    private readonly ILogger<AuthController> _log;

    public AuthController(
        IAuthService auth,
        ITokenService tokens,
        IHostEnvironment env,
        ILogger<AuthController> log)
    {
        _auth = auth; _tokens = tokens; _env = env; _log = log;
    }

    /// <summary>
    /// POST /api/auth/login. Body is validated by the global
    /// <see cref="FluentValidationFilter"/>; on success returns the
    /// access token + lifetime + the user view, and stamps a fresh
    /// <c>fb_refresh</c> cookie. Wrong password and unknown email both
    /// return 401 with byte-identical bodies.
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest body, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(body.Email, body.Password, ct);
        if (result.Kind != AuthOutcome.Success)
            return UnauthorizedProblem("Invalid email or password.");

        StampRefreshCookie(result.RefreshTokenRaw!);
        return Ok(new AuthResponse(
            result.AccessToken!,
            result.ExpiresInSeconds,
            result.User!));
    }

    /// <summary>
    /// POST /api/auth/refresh. Reads the <c>fb_refresh</c> cookie,
    /// rotates the stored hash, stamps a new cookie, and returns a new
    /// access token. Any failure (missing / tampered / expired) returns
    /// 401 + clears the cookie.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(AuthCookieSettings.CookieName, out var presented)
            || string.IsNullOrWhiteSpace(presented))
        {
            ClearRefreshCookie();
            return UnauthorizedProblem("Missing refresh token.");
        }

        var result = await _auth.RefreshAsync(presented, ct);
        if (result.Kind != AuthOutcome.Success)
        {
            ClearRefreshCookie();
            return UnauthorizedProblem(result.Kind == AuthOutcome.Expired
                ? "Refresh token expired."
                : "Invalid refresh token.");
        }

        StampRefreshCookie(result.RefreshTokenRaw!);
        return Ok(new AuthResponse(
            result.AccessToken!,
            result.ExpiresInSeconds,
            result.User!));
    }

    /// <summary>
    /// POST /api/auth/logout. Clears the stored refresh hash (if the
    /// cookie matches a user) and deletes the cookie. Always 204 — even
    /// an unknown / missing cookie is a no-op so the caller never has
    /// to special-case it.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(AuthCookieSettings.CookieName, out var presented)
            && !string.IsNullOrWhiteSpace(presented))
        {
            await _auth.LogoutAsync(presented, ct);
        }
        ClearRefreshCookie();
        return NoContent();
    }

    /// <summary>
    /// GET /api/auth/me. Returns the user view derived from the bearer
    /// access token's <c>sub</c> claim. Framework default 401 covers
    /// missing / tampered / expired tokens.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var sub = User.FindFirst("sub")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return UnauthorizedProblem("Token missing sub claim.");
        var info = await _auth.MeAsync(userId, ct);
        if (info is null)
            return UnauthorizedProblem("User no longer exists.");
        return Ok(info);
    }

    // --- helpers ---------------------------------------------------------

    private void StampRefreshCookie(string raw)
    {
        var sameSite = AuthCookieSettings.ParseSameSite(
            Environment.GetEnvironmentVariable("AUTH_COOKIE_SAMESITE"));
        var opts = new CookieOptions
        {
            HttpOnly = true,
            Secure   = AuthCookieSettings.ShouldBeSecure(sameSite, _env),
            SameSite = sameSite,
            Path     = AuthCookieSettings.CookiePath,
            // 7 days matches AuthService.RefreshTokenLifetimeDays.
            Expires  = DateTimeOffset.UtcNow.AddDays(7),
        };
        Response.Cookies.Append(AuthCookieSettings.CookieName, raw, opts);
        Response.Headers["Cache-Control"] = "no-store";
    }

    private void ClearRefreshCookie()
    {
        var sameSite = AuthCookieSettings.ParseSameSite(
            Environment.GetEnvironmentVariable("AUTH_COOKIE_SAMESITE"));
        var opts = new CookieOptions
        {
            HttpOnly = true,
            Secure   = AuthCookieSettings.ShouldBeSecure(sameSite, _env),
            SameSite = sameSite,
            Path     = AuthCookieSettings.CookiePath,
            Expires  = DateTimeOffset.UnixEpoch,
            MaxAge   = null,
        };
        Response.Cookies.Delete(AuthCookieSettings.CookieName, opts);
        Response.Headers["Cache-Control"] = "no-store";
    }

    /// <summary>
    /// Canonical 401 envelope — RFC 7807 ProblemDetails, same shape as
    /// the rest of the API. Detail is deliberately generic so the
    /// response is byte-identical for unknown-email vs wrong-password.
    /// </summary>
    private IActionResult UnauthorizedProblem(string detail)
        => new ObjectResult(new ProblemDetails
        {
            Type   = "https://datatracker.ietf.org/doc/html/rfc7235#section-3.1",
            Title  = "Unauthorized",
            Status = StatusCodes.Status401Unauthorized,
            Detail = detail,
            Instance = HttpContext.Request.Path.HasValue
                ? HttpContext.Request.Path.Value! : string.Empty,
        })
        {
            StatusCode = StatusCodes.Status401Unauthorized,
            ContentTypes = { "application/problem+json" },
        };
}
