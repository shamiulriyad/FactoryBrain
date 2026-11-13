using System.Security.Cryptography;
using System.Text;
using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Auth;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IAuthService"/>. Email lookups are always
/// lowercased so two rows that differ only in casing can never coexist;
/// the password is verified via
/// <see cref="IPasswordHasher"/>; on success a 32-byte random refresh
/// token is generated, stored as SHA-256(raw) on the user with a
/// 7-day expiry, and the raw value is returned so the controller can
/// stamp it in the <c>fb_refresh</c> cookie. <c>RefreshAsync</c>
/// rotates the hash + extends the expiry; the old token stops working
/// the moment the new one is written. <c>LogoutAsync</c> clears the
/// stored hash if the cookie matches.
/// <para>
/// A deactivated account can't sign in or refresh. Access tokens carry the
/// user's roles, permissions, factory / line scope and security stamp,
/// loaded fresh from the database on every login and refresh.
/// </para>
/// </summary>
public sealed class AuthService : IAuthService
{
    public const int RefreshTokenLifetimeDays = 7;

    private readonly BunonBrainDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ITokenService _tokens;
    private readonly IAccessSnapshotLoader _access;

    public AuthService(BunonBrainDbContext db, IPasswordHasher hasher, ITokenService tokens, IAccessSnapshotLoader access)
    {
        _db = db; _hasher = hasher; _tokens = tokens; _access = access;
    }

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
    {
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalised, ct);
        // Single canonical "Invalid" for both unknown email and wrong
        // password — prevents account enumeration.
        //
        // Even when no user matches we still run ONE PasswordHasher
        // verification, against a fixed dummy hash produced by the
        // same PasswordHasher implementation. Without this, an
        // attacker can distinguish "unknown email" from "wrong
        // password" purely by response time (the wrong-password
        // branch does a full PBKDF2 round, the unknown-email branch
        // short-circuits). The dummy exists only so both paths do the
        // same work; its plaintext is never compared to the supplied
        // password and the boolean result is discarded.
        if (user is null)
        {
            _ = _hasher.Verify(new User(), PasswordHasherAdapter.DummyHash, password ?? string.Empty);
            return new LoginResult(AuthOutcome.Invalid, null, 0, null, null);
        }

        var ok = user.PasswordHash is { Length: > 0 } &&
                 _hasher.Verify(user, user.PasswordHash, password ?? string.Empty);
        if (!ok) return new LoginResult(AuthOutcome.Invalid, null, 0, null, null);
        // Only someone who knows the password learns that the account is deactivated.
        if (!user.IsActive) return new LoginResult(AuthOutcome.Deactivated, null, 0, null, null);

        var (raw, _, _) = await IssueAndStoreRefreshAsync(user.Id, markLogin: true, ct);
        var snapshot = await _access.LoadAsync(user.Id, ct);
        if (snapshot is null) return new LoginResult(AuthOutcome.Invalid, null, 0, null, null);
        var access = _tokens.IssueAccessToken(snapshot, out var expiresInSeconds);
        return new LoginResult(AuthOutcome.Success, access, expiresInSeconds, UserInfo.From(snapshot), raw);
    }

    public async Task<RefreshResult> RefreshAsync(string presentedRefreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presentedRefreshToken))
            return new RefreshResult(AuthOutcome.Invalid, null, 0, null, null);

        var presentedHash = HashRefresh(presentedRefreshToken);
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.RefreshTokenHash == presentedHash, ct);
        if (user is null)
            return new RefreshResult(AuthOutcome.Invalid, null, 0, null, null);

        if (!user.IsActive || user.RefreshTokenExpiresAt is null || user.RefreshTokenExpiresAt < DateTimeOffset.UtcNow)
        {
            // Expired, or the account was deactivated: clear the stale hash
            // so a later attack doesn't get a hit on a dead row.
            var deactivated = !user.IsActive;
            user.RefreshTokenHash = null;
            user.RefreshTokenExpiresAt = null;
            await _db.SaveChangesAsync(ct);
            return new RefreshResult(deactivated ? AuthOutcome.Deactivated : AuthOutcome.Expired, null, 0, null, null);
        }

        var (raw, _, _) = await IssueAndStoreRefreshAsync(user.Id, markLogin: false, ct);
        var snapshot = await _access.LoadAsync(user.Id, ct);
        if (snapshot is null) return new RefreshResult(AuthOutcome.Invalid, null, 0, null, null);
        var access = _tokens.IssueAccessToken(snapshot, out var expiresInSeconds);
        return new RefreshResult(AuthOutcome.Success, access, expiresInSeconds, UserInfo.From(snapshot), raw);
    }

    public async Task LogoutAsync(string presentedRefreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presentedRefreshToken)) return;
        var presentedHash = HashRefresh(presentedRefreshToken);
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.RefreshTokenHash == presentedHash, ct);
        if (user is null) return;
        user.RefreshTokenHash = null;
        user.RefreshTokenExpiresAt = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<UserInfo?> MeAsync(Guid userId, CancellationToken ct)
    {
        var snapshot = await _access.LoadAsync(userId, ct);
        return snapshot is null ? null : UserInfo.From(snapshot);
    }

    /// <summary>
    /// Generate a 32-byte random refresh token, hash it, stamp the
    /// user row with the hash + a 7-day expiry, and return the raw
    /// value (base64url-encoded) for the controller to put in the
    /// cookie. A login also records the sign-in time and an audit entry.
    /// </summary>
    private async Task<(string Raw, string Hash, DateTimeOffset ExpiresAt)>
        IssueAndStoreRefreshAsync(Guid userId, bool markLogin, CancellationToken ct)
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        var rawBase64Url = Base64UrlEncode(raw);
        var hash = HashRefresh(rawBase64Url);
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddDays(RefreshTokenLifetimeDays);

        var user = await _db.Users.FirstAsync(u => u.Id == userId, ct);
        user.RefreshTokenHash = hash;
        user.RefreshTokenExpiresAt = expiry;
        if (markLogin)
        {
            user.LastLoginAt = now;
            var roles = await _db.UserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId).ToListAsync(ct);
            var factoryId = await _db.UserFactories.Where(f => f.UserId == userId).Select(f => (Guid?)f.FactoryId).FirstOrDefaultAsync(ct);
            _db.AuditLog.Add(AccessAudit.Entry(new AuditActor(userId, user.Email, AccessCatalog.PrimaryRole(roles)),
                "auth.login", AccessAudit.EntityUser, userId.ToString(), null, null, "signed in", null, factoryId));
        }
        await _db.SaveChangesAsync(ct);
        return (rawBase64Url, hash, expiry);
    }

    /// <summary>SHA-256(raw) base64url. Raw is treated as ASCII bytes (base64url alphabet).</summary>
    private static string HashRefresh(string raw)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.ASCII.GetBytes(raw), digest);
        return Base64UrlEncode(digest);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
