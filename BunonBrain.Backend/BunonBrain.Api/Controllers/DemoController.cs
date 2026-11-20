using BunonBrain.Api.Middleware;
using BunonBrain.Application.Demo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Interactive demo sign-in (DEMO_MODE=true deployments; 404 everywhere else).
/// Lives under <c>/api/auth</c> so the HttpOnly refresh cookie, which is
/// scoped to that path, identifies the reviewer's demo session:
/// <list type="number">
///   <item><c>POST /api/auth/demo/login</c> — the shared demo credential starts a reviewer session (own cookie, own expiry).</item>
///   <item><c>GET /api/auth/demo/session</c> — the roles on offer (every system role) and the current one.</item>
///   <item><c>POST /api/auth/demo/switch</c> — choose a role by id; the server picks that role's demo account and returns its access token.</item>
/// </list>
/// Refresh and sign-out use the normal <c>/api/auth/refresh</c> and <c>/api/auth/logout</c>.
/// </summary>
[ApiController]
[Route("api/auth/demo")]
public sealed class DemoController : ControllerBase
{
    private readonly DemoOptions _opts;
    private readonly IDemoSessionService _demo;
    private readonly IHostEnvironment _env;

    public DemoController(DemoOptions opts, IDemoSessionService demo, IHostEnvironment env)
    {
        _opts = opts; _demo = demo; _env = env;
    }

    /// <summary>GET /api/auth/demo — whether this deployment is the demo and whether its data is ready.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Info(CancellationToken ct)
    {
        if (!_opts.Enabled) return NotEnabled();
        Response.Headers["Cache-Control"] = "no-store";
        return Ok(await _demo.InfoAsync(ct));
    }

    /// <summary>POST /api/auth/demo/login — 200 with the role list, 401 wrong credential, 503 data being prepared or too many reviewers.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Login([FromBody] DemoLoginRequest body, CancellationToken ct)
    {
        if (!_opts.Enabled) return NotEnabled();
        var r = await _demo.LoginAsync(body.Email, body.Password, HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), ct);
        if (r.Outcome != DemoOutcome.Ok) return Fail(r);
        AuthCookieSettings.Stamp(Response, r.RefreshTokenRaw!, _env, r.Value!.ExpiresAt);
        return Ok(r.Value);
    }

    /// <summary>GET /api/auth/demo/session — the reviewer's session (from the refresh cookie) and the roles.</summary>
    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<IActionResult> Session(CancellationToken ct)
    {
        if (!_opts.Enabled) return NotEnabled();
        Response.Headers["Cache-Control"] = "no-store";
        var r = await _demo.GetAsync(Cookie(), ct);
        return r.Outcome == DemoOutcome.Ok ? Ok(r.Value) : Fail(r);
    }

    /// <summary>POST /api/auth/demo/switch — act as another role without signing in again. Body: <c>{ "roleId": "FactoryManager" }</c>.</summary>
    [HttpPost("switch")]
    [AllowAnonymous]
    public async Task<IActionResult> Switch([FromBody] DemoSwitchRequest body, CancellationToken ct)
    {
        if (!_opts.Enabled) return NotEnabled();
        var r = await _demo.SwitchAsync(Cookie(), body.RoleId, ct);
        if (r.Outcome != DemoOutcome.Ok)
        {
            if (r.Outcome == DemoOutcome.Unauthorized) AuthCookieSettings.Clear(Response, _env);
            return Fail(r);
        }
        AuthCookieSettings.Stamp(Response, r.RefreshTokenRaw!, _env, r.Value!.User.DemoSessionExpiresAt ?? DateTimeOffset.UtcNow.AddHours(1));
        return Ok(r.Value);
    }

    private string Cookie() =>
        Request.Cookies.TryGetValue(AuthCookieSettings.CookieName, out var raw) ? raw ?? "" : "";

    private IActionResult NotEnabled() =>
        AccessResults.Problem(this, StatusCodes.Status404NotFound, "Not found", "demo_disabled", "There is no interactive demo on this server.");

    private IActionResult Fail<T>(DemoResult<T> r)
    {
        var status = r.Outcome switch
        {
            DemoOutcome.Unauthorized => StatusCodes.Status401Unauthorized,
            DemoOutcome.NotFound => StatusCodes.Status404NotFound,
            DemoOutcome.Preparing or DemoOutcome.Busy => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        if (status == StatusCodes.Status503ServiceUnavailable) Response.Headers["Retry-After"] = "60";
        return AccessResults.Problem(this, status, status switch
        {
            401 => "Unauthorized",
            404 => "Not found",
            503 => "Demo unavailable",
            _ => "Invalid request",
        }, r.Code, r.Message);
    }
}

/// <summary>
/// Demo operator endpoints: list and revoke reviewer sessions, force a reset.
/// They need the <c>X-Demo-Admin-Key</c> header (DEMO_ADMIN_KEY); without a
/// configured key, or outside demo mode, they answer 404. No demo role can
/// reach them, because every demo role is shared with other reviewers.
/// </summary>
[ApiController]
[Route("api/demo/admin")]
[AllowAnonymous]
public sealed class DemoAdminController : ControllerBase
{
    public const string KeyHeader = "X-Demo-Admin-Key";

    private readonly DemoOptions _opts;
    private readonly IDemoSessionService _demo;
    private readonly IDemoResetService _reset;

    public DemoAdminController(DemoOptions opts, IDemoSessionService demo, IDemoResetService reset)
    {
        _opts = opts; _demo = demo; _reset = reset;
    }

    /// <summary>GET /api/demo/admin/sessions?active=true</summary>
    [HttpGet("sessions")]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Sessions([FromQuery] bool active = true, CancellationToken ct = default) =>
        Authorized() ? Ok(await _demo.ListAsync(active, ct)) : NotFoundProblem();

    /// <summary>POST /api/demo/admin/sessions/{id}/revoke</summary>
    [HttpPost("sessions/{id}/revoke")]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (!Authorized()) return NotFoundProblem();
        var n = await _demo.RevokeAsync(id, "revoked by the demo operator", ct);
        return n == 0 ? AccessResults.Problem(this, 404, "Not found", "session_not_found", "No active session with that id.") : Ok(new { revoked = n });
    }

    /// <summary>POST /api/demo/admin/sessions/revoke-all</summary>
    [HttpPost("sessions/revoke-all")]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> RevokeAll(CancellationToken ct) =>
        Authorized() ? Ok(new { revoked = await _demo.RevokeAsync(null, "all sessions revoked by the demo operator", ct) }) : NotFoundProblem();

    /// <summary>POST /api/demo/admin/reset — rebuild the baseline now (ends every session). 202 when started, 409 when a reset is already running.</summary>
    [HttpPost("reset")]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        if (!Authorized()) return NotFoundProblem();
        var ran = await _reset.ResetAsync("forced by the demo operator", ct);
        return ran ? Ok(new { reset = true }) : AccessResults.Problem(this, 409, "Conflict", "reset_running", "A reset is already running.");
    }

    private bool Authorized() =>
        _opts.Enabled && Request.Headers.TryGetValue(KeyHeader, out var key) && _opts.AdminKeyMatches(key.ToString());

    private IActionResult NotFoundProblem() => AccessResults.Problem(this, 404, "Not found", null, null);
}
