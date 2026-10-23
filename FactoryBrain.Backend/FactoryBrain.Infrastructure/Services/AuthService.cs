using System.Security.Cryptography;
using System.Text;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Auth;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

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
/// </summary>
public sealed class AuthService : IAuthService
{
    public const int RefreshTokenLifetimeDays = 7;

    private readonly FactoryBrainDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ITokenService _tokens;

    public AuthService(FactoryBrainDbContext db, IPasswordHasher hasher, ITokenService tokens)
    {
        _db = db; _hasher = hasher; _tokens = tokens;
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

        var (raw, hash, expiry) = await IssueAndStoreRefreshAsync(user.Id, ct);
        var access = _tokens.IssueAccessToken(user, out var expiresInSeconds);
        return new LoginResult(
            AuthOutcome.Success, access, expiresInSeconds,
            new UserInfo(user.Id, user.Email, user.Role), raw);
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

        if (user.RefreshTokenExpiresAt is null || user.RefreshTokenExpiresAt < DateTimeOffset.UtcNow)
        {
            // The token was once valid but has expired. Clear the stale
            // hash so a later attack doesn't get a hit on a dead row.
            user.RefreshTokenHash = null;
            user.RefreshTokenExpiresAt = null;
            await _db.SaveChangesAsync(ct);
            return new RefreshResult(AuthOutcome.Expired, null, 0, null, null);
        }

        var (raw, hash, expiry) = await IssueAndStoreRefreshAsync(user.Id, ct);
        var access = _tokens.IssueAccessToken(user, out var expiresInSeconds);
        return new RefreshResult(
            AuthOutcome.Success, access, expiresInSeconds,
            new UserInfo(user.Id, user.Email, user.Role), raw);
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
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;
        return new UserInfo(user.Id, user.Email, user.Role);
    }

    /// <summary>
    /// Generate a 32-byte random refresh token, hash it, stamp the
    /// user row with the hash + a 7-day expiry, and return the raw
    /// value (base64url-encoded) for the controller to put in the
    /// cookie. Persists the change.
    /// </summary>
    private async Task<(string Raw, string Hash, DateTimeOffset ExpiresAt)>
        IssueAndStoreRefreshAsync(Guid userId, CancellationToken ct)
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        var rawBase64Url = Base64UrlEncode(raw);
        var hash = HashRefresh(rawBase64Url);
        var expiry = DateTimeOffset.UtcNow.AddDays(RefreshTokenLifetimeDays);

        var user = await _db.Users.FirstAsync(u => u.Id == userId, ct);
        user.RefreshTokenHash = hash;
        user.RefreshTokenExpiresAt = expiry;
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
