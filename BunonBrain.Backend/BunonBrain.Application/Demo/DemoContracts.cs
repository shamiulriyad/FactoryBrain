using BunonBrain.Application.Dtos.Auth;

namespace BunonBrain.Application.Demo;

/// <summary>A role the reviewer can switch to, with the demo identity behind it.</summary>
public sealed record DemoRoleOption(
    string RoleId,
    string Name,
    string Description,
    string Scope,
    string IdentityName,
    string IdentityEmail,
    bool Current);

/// <summary>GET /api/auth/demo/session: the reviewer's session and the roles on offer.</summary>
public sealed record DemoSessionView(
    Guid SessionId,
    DateTimeOffset ExpiresAt,
    string? CurrentRoleId,
    IReadOnlyList<DemoRoleOption> Roles,
    DateTimeOffset? NextResetAt);

/// <summary>GET /api/auth/demo: public facts about the demo (no secrets).</summary>
public sealed record DemoInfoResponse(bool Enabled, bool Ready, int SessionHours, DateTimeOffset? NextResetAt);

/// <summary>POST /api/auth/demo/login body.</summary>
public sealed record DemoLoginRequest(string Email, string Password);

/// <summary>POST /api/auth/demo/switch body: only a role id; the server picks the identity.</summary>
public sealed record DemoSwitchRequest(string RoleId);

/// <summary>Operator view of one session (GET /api/demo/admin/sessions).</summary>
public sealed record DemoSessionAdminView(
    Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt, string? RevokedReason, string? CurrentRoleId, int RoleSwitches, string? ClientIp);

public enum DemoOutcome { Ok, Invalid, Unauthorized, NotFound, Preparing, Busy }

public sealed record DemoResult<T>(DemoOutcome Outcome, T? Value = default, string? RefreshTokenRaw = null, string? Code = null, string? Message = null)
{
    public static DemoResult<T> Ok(T value, string? refresh = null) => new(DemoOutcome.Ok, value, refresh);
    public static DemoResult<T> Fail(DemoOutcome outcome, string code, string message) => new(outcome, default, null, code, message);
}

/// <summary>
/// Reviewer sessions of the interactive demo. Each sign-in with the shared
/// credential is its own session (own refresh token, own expiry, own audit
/// trail); choosing a role maps, on the server, to that role's demo identity.
/// </summary>
public interface IDemoSessionService
{
    Task<DemoInfoResponse> InfoAsync(CancellationToken ct);
    Task<DemoResult<DemoSessionView>> LoginAsync(string email, string password, string? clientIp, string? userAgent, CancellationToken ct);
    Task<DemoResult<DemoSessionView>> GetAsync(string refreshTokenRaw, CancellationToken ct);
    Task<DemoResult<AuthResponse>> SwitchAsync(string refreshTokenRaw, string roleId, CancellationToken ct);
    Task<DemoResult<AuthResponse>> RefreshAsync(string refreshTokenRaw, CancellationToken ct);
    Task LogoutAsync(string refreshTokenRaw, CancellationToken ct);

    /// <summary>True while the session is live and still acting as <paramref name="userId"/>. Cached briefly; fails closed.</summary>
    Task<bool> IsActiveAsync(Guid sessionId, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<DemoSessionAdminView>> ListAsync(bool activeOnly, CancellationToken ct);

    /// <summary>Revoke one session, or every active one when <paramref name="sessionId"/> is null. Returns how many.</summary>
    Task<int> RevokeAsync(Guid? sessionId, string reason, CancellationToken ct);
}

/// <summary>Rebuilds the demo database to its baseline (operator trigger and the daily worker).</summary>
public interface IDemoResetService
{
    /// <param name="resume">Continue an interrupted seed without emptying the database again (the seed is repeatable).</param>
    Task<bool> ResetAsync(string reason, CancellationToken ct, bool resume = false);
}
