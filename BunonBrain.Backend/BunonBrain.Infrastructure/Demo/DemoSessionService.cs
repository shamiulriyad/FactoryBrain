using System.Security.Cryptography;
using System.Text;
using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Demo;
using BunonBrain.Application.Dtos.Auth;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BunonBrain.Infrastructure.Demo;

/// <summary>
/// Implements <see cref="IDemoSessionService"/>. A reviewer signs in once with
/// the shared credential and gets a <see cref="DemoSession"/> row (refresh
/// token hash, absolute expiry). Choosing a role sets the session's current
/// demo identity, resolved here from the role id; the access token is the
/// identity's normal token (roles, permissions, factory / line scope) plus the
/// session id, so every existing permission and scope check applies unchanged.
/// Sign-in, role switches, sign-out and revocations are written to the audit log.
/// </summary>
public sealed class DemoSessionService : IDemoSessionService
{
    public const string EntityDemoSession = "demo_session";
    public const string ReviewerActor = "demo-reviewer";
    public static readonly TimeSpan ActiveCacheFor = TimeSpan.FromSeconds(15);

    private readonly BunonBrainDbContext _db;
    private readonly DemoOptions _opts;
    private readonly IAccessSnapshotLoader _access;
    private readonly ITokenService _tokens;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DemoSessionService> _log;

    public DemoSessionService(BunonBrainDbContext db, DemoOptions opts, IAccessSnapshotLoader access, ITokenService tokens,
        IMemoryCache cache, ILogger<DemoSessionService> log)
    {
        _db = db; _opts = opts; _access = access; _tokens = tokens; _cache = cache; _log = log;
    }

    private sealed record ActiveEntry(bool Active, Guid? CurrentUserId);

    private static string CacheKey(Guid sessionId) => "demo-session:" + sessionId.ToString("N");

    public async Task<DemoInfoResponse> InfoAsync(CancellationToken ct)
    {
        if (!_opts.Enabled) return new DemoInfoResponse(false, false, 0, null);
        var env = await _db.DemoEnvironment.AsNoTracking().FirstOrDefaultAsync(ct);
        return new DemoInfoResponse(true, env?.Status == DemoDatabase.StatusReady, (int)_opts.SessionLifetime.TotalHours, env?.NextResetAt);
    }

    public async Task<DemoResult<DemoSessionView>> LoginAsync(string email, string password, string? clientIp, string? userAgent, CancellationToken ct)
    {
        // Compare first, so a wrong credential costs the same whatever the state of the demo.
        var ok = _opts.CredentialMatches(email, password);
        if (!ok) return DemoResult<DemoSessionView>.Fail(DemoOutcome.Unauthorized, "invalid_credentials", "Invalid demo email or password.");

        var env = await _db.DemoEnvironment.AsNoTracking().FirstOrDefaultAsync(ct);
        if (env?.Status != DemoDatabase.StatusReady)
            return DemoResult<DemoSessionView>.Fail(DemoOutcome.Preparing, "demo_preparing",
                "The demo data is being prepared. Try again in a minute.");

        var now = DateTimeOffset.UtcNow;
        var active = await _db.DemoSessions.CountAsync(s => s.RevokedAt == null && s.ExpiresAt > now, ct);
        if (active >= _opts.MaxActiveSessions)
            return DemoResult<DemoSessionView>.Fail(DemoOutcome.Busy, "demo_busy",
                "The demo has too many active reviewers right now. Try again later.");

        var raw = NewRefreshToken();
        var session = new DemoSession
        {
            RefreshTokenHash = Hash(raw),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + _opts.SessionLifetime,
            ClientIp = AccessAudit.Clip(clientIp, 64),
            UserAgent = AccessAudit.Clip(userAgent, 256),
        };
        _db.DemoSessions.Add(session);
        _db.AuditLog.Add(AccessAudit.Entry(new AuditActor(null, ReviewerActor, "DemoReviewer"), "demo.login",
            EntityDemoSession, session.Id.ToString(), null, "active",
            $"reviewer signed in to the demo (session ends {session.ExpiresAt:u})", null, null));
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Demo session {SessionId} started.", session.Id);
        return DemoResult<DemoSessionView>.Ok(View(session, env), raw);
    }

    public async Task<DemoResult<DemoSessionView>> GetAsync(string refreshTokenRaw, CancellationToken ct)
    {
        var session = await FindLiveAsync(refreshTokenRaw, tracking: false, ct);
        if (session is null) return DemoResult<DemoSessionView>.Fail(DemoOutcome.Unauthorized, "demo_session_ended", "Sign in to the demo again.");
        var env = await _db.DemoEnvironment.AsNoTracking().FirstOrDefaultAsync(ct);
        return DemoResult<DemoSessionView>.Ok(View(session, env));
    }

    public async Task<DemoResult<AuthResponse>> SwitchAsync(string refreshTokenRaw, string roleId, CancellationToken ct)
    {
        var session = await FindLiveAsync(refreshTokenRaw, tracking: true, ct);
        if (session is null) return DemoResult<AuthResponse>.Fail(DemoOutcome.Unauthorized, "demo_session_ended", "Sign in to the demo again.");

        // The browser names a role; the identity behind it is decided here.
        var identity = DemoIdentities.ForRole(roleId?.Trim());
        if (identity is null) return DemoResult<AuthResponse>.Fail(DemoOutcome.Invalid, "unknown_role", "That role does not exist.");
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == identity.UserId)
            .Select(u => new { u.Id, u.Email, u.IsActive }).FirstOrDefaultAsync(ct);
        if (user is null || !user.IsActive)
            return DemoResult<AuthResponse>.Fail(DemoOutcome.NotFound, "demo_identity_missing", "This demo role is not available right now.");

        var previous = session.CurrentUserId;
        var raw = NewRefreshToken();
        session.RefreshTokenHash = Hash(raw);
        session.CurrentUserId = identity.UserId;
        session.LastSeenAt = DateTimeOffset.UtcNow;
        session.RoleSwitches++;
        var fromRole = previous is { } p ? DemoIdentities.All.FirstOrDefault(i => i.UserId == p)?.RoleId : null;
        _db.AuditLog.Add(AccessAudit.Entry(new AuditActor(identity.UserId, user.Email, identity.RoleId), "demo.role_selected",
            EntityDemoSession, session.Id.ToString(), fromRole, identity.RoleId,
            $"demo reviewer now acting as {identity.RoleId}", null, null));
        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey(session.Id));

        var issued = await IssueAsync(session, ct);
        return issued is null
            ? DemoResult<AuthResponse>.Fail(DemoOutcome.NotFound, "demo_identity_missing", "This demo role is not available right now.")
            : DemoResult<AuthResponse>.Ok(issued, raw);
    }

    public async Task<DemoResult<AuthResponse>> RefreshAsync(string refreshTokenRaw, CancellationToken ct)
    {
        var session = await FindLiveAsync(refreshTokenRaw, tracking: true, ct);
        if (session is null) return DemoResult<AuthResponse>.Fail(DemoOutcome.Unauthorized, "demo_session_ended", "Sign in to the demo again.");
        if (session.CurrentUserId is null)
            return DemoResult<AuthResponse>.Fail(DemoOutcome.Unauthorized, "demo_role_required", "Choose a role first.");

        var raw = NewRefreshToken();
        session.RefreshTokenHash = Hash(raw);
        session.LastSeenAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        var issued = await IssueAsync(session, ct);
        return issued is null
            ? DemoResult<AuthResponse>.Fail(DemoOutcome.Unauthorized, "demo_identity_missing", "Choose a role again.")
            : DemoResult<AuthResponse>.Ok(issued, raw);
    }

    public async Task LogoutAsync(string refreshTokenRaw, CancellationToken ct)
    {
        var session = await FindLiveAsync(refreshTokenRaw, tracking: true, ct);
        if (session is null) return;
        Revoke(session, "signed out", actor: CurrentActor(session));
        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey(session.Id));
    }

    public async Task<bool> IsActiveAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        if (!_cache.TryGetValue(CacheKey(sessionId), out ActiveEntry? entry) || entry is null)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var row = await _db.DemoSessions.AsNoTracking().Where(s => s.Id == sessionId)
                    .Select(s => new { s.RevokedAt, s.ExpiresAt, s.CurrentUserId }).FirstOrDefaultAsync(ct);
                entry = row is null ? new ActiveEntry(false, null)
                    : new ActiveEntry(row.RevokedAt is null && row.ExpiresAt > now, row.CurrentUserId);
                // Never cache past the session's own end.
                var ttl = row is null ? ActiveCacheFor : TimeSpan.FromTicks(Math.Max(0, Math.Min(ActiveCacheFor.Ticks, (row.ExpiresAt - now).Ticks)));
                if (ttl > TimeSpan.Zero) _cache.Set(CacheKey(sessionId), entry, ttl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Demo session check for {SessionId} failed; rejecting the token.", sessionId);
                return false;
            }
        }
        // A token for the previous role stops working as soon as the reviewer switches.
        return entry.Active && entry.CurrentUserId == userId;
    }

    public async Task<IReadOnlyList<DemoSessionAdminView>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var q = _db.DemoSessions.AsNoTracking();
        if (activeOnly) q = q.Where(s => s.RevokedAt == null && s.ExpiresAt > now);
        var rows = await q.OrderByDescending(s => s.CreatedAt).Take(500).ToListAsync(ct);
        return rows.Select(s => new DemoSessionAdminView(s.Id, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.RevokedAt, s.RevokedReason,
            RoleOf(s.CurrentUserId), s.RoleSwitches, s.ClientIp)).ToList();
    }

    public async Task<int> RevokeAsync(Guid? sessionId, string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var q = _db.DemoSessions.Where(s => s.RevokedAt == null && s.ExpiresAt > now);
        if (sessionId is { } id) q = q.Where(s => s.Id == id);
        var rows = await q.ToListAsync(ct);
        var why = string.IsNullOrWhiteSpace(reason) ? "revoked by the demo operator" : reason.Trim();
        foreach (var s in rows) Revoke(s, why, new AuditActor(null, "demo-operator", "DemoOperator"));
        await _db.SaveChangesAsync(ct);
        foreach (var s in rows) _cache.Remove(CacheKey(s.Id));
        return rows.Count;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private void Revoke(DemoSession s, string reason, AuditActor actor)
    {
        s.RevokedAt = DateTimeOffset.UtcNow;
        s.RevokedReason = AccessAudit.Clip(reason, 200);
        _db.AuditLog.Add(AccessAudit.Entry(actor, "demo.session_revoked", EntityDemoSession, s.Id.ToString(), "active", "revoked", reason, null, null));
    }

    private static AuditActor CurrentActor(DemoSession s)
    {
        var identity = s.CurrentUserId is { } id ? DemoIdentities.All.FirstOrDefault(i => i.UserId == id) : null;
        return identity is null ? new AuditActor(null, ReviewerActor, "DemoReviewer") : new AuditActor(identity.UserId, identity.Email, identity.RoleId);
    }

    private async Task<DemoSession?> FindLiveAsync(string? raw, bool tracking, CancellationToken ct)
    {
        if (!_opts.Enabled || string.IsNullOrWhiteSpace(raw) || raw.Length > 256) return null;
        var hash = Hash(raw);
        var q = tracking ? _db.DemoSessions : _db.DemoSessions.AsNoTracking();
        var s = await q.FirstOrDefaultAsync(x => x.RefreshTokenHash == hash, ct);
        if (s is null || s.RevokedAt is not null || s.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        return s;
    }

    private async Task<AuthResponse?> IssueAsync(DemoSession session, CancellationToken ct)
    {
        if (session.CurrentUserId is not { } userId) return null;
        var snapshot = await _access.LoadAsync(userId, ct);
        if (snapshot is null) return null;
        var token = _tokens.IssueDemoAccessToken(snapshot, session.Id, out var expires);
        return new AuthResponse(token, expires, UserInfo.From(snapshot) with { DemoSessionExpiresAt = session.ExpiresAt });
    }

    private DemoSessionView View(DemoSession s, DemoEnvironment? env)
    {
        var current = RoleOf(s.CurrentUserId);
        var roles = DemoIdentities.All.Select(i =>
        {
            var def = AccessCatalog.Role(i.RoleId)!;
            return new DemoRoleOption(def.Id, def.Name, def.Description, def.Scope.ToString(), i.FullName, i.Email, def.Id == current);
        }).ToList();
        return new DemoSessionView(s.Id, s.ExpiresAt, current, roles, env?.NextResetAt);
    }

    private static string? RoleOf(Guid? userId) =>
        userId is { } id ? DemoIdentities.All.FirstOrDefault(i => i.UserId == id)?.RoleId : null;

    private static string NewRefreshToken()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return Base64Url(raw);
    }

    public static string Hash(string raw)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.ASCII.GetBytes(raw), digest);
        return Base64Url(digest);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
