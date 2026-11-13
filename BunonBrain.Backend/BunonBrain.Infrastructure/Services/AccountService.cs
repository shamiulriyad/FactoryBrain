using System.Security.Cryptography;
using System.Text;
using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Auth;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Services;

/// <summary>Startup settings for self-service accounts.</summary>
public sealed record AccountOptions(bool SelfRegistration, TimeSpan ResetLinkLifetime)
{
    /// <summary><c>ALLOW_SELF_REGISTRATION</c>: anything but "false" leaves sign-up on.</summary>
    public static AccountOptions FromEnvironment(Func<string, string?> read) => new(
        !string.Equals(read("ALLOW_SELF_REGISTRATION")?.Trim(), "false", StringComparison.OrdinalIgnoreCase),
        TimeSpan.FromMinutes(30));
}

/// <summary>
/// Implements <see cref="IAccountService"/>.
/// <list type="bullet">
///   <item>Sign-up creates an account with no roles and no factory; only an
///   administrator can grant access.</item>
///   <item>A reset link carries 32 random bytes; only their SHA-256 is stored,
///   it expires after 30 minutes, works once, and a new request voids older
///   links. A successful reset signs the user out everywhere.</item>
///   <item>Your own profile (name, phone) and password; changing the password
///   clears "must change password" and rotates the security stamp.</item>
/// </list>
/// </summary>
public sealed class AccountService : IAccountService
{
    /// <summary>At most this many reset emails per account per 15 minutes (whoever asks).</summary>
    public const int MaxResetRequestsPer15Min = 3;

    private readonly BunonBrainDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IAuthService _auth;
    private readonly IEmailQueue _emails;
    private readonly AccountOptions _opts;
    private readonly EmailOptions _email;
    private readonly ISessionValidator _sessions;
    private readonly IAccessSnapshotLoader _access;
    private readonly ILogger<AccountService> _log;

    public AccountService(BunonBrainDbContext db, IPasswordHasher hasher, IAuthService auth, IEmailQueue emails,
        AccountOptions opts, EmailOptions email, ISessionValidator sessions, IAccessSnapshotLoader access, ILogger<AccountService> log)
    {
        _db = db; _hasher = hasher; _auth = auth; _emails = emails; _opts = opts; _email = email;
        _sessions = sessions; _access = access; _log = log;
    }

    public bool SelfRegistrationEnabled => _opts.SelfRegistration;

    public async Task<RegisterResult> RegisterAsync(string email, string password, string? traceId, CancellationToken ct)
    {
        if (!_opts.SelfRegistration) return new RegisterResult(RegisterOutcome.Disabled, null);
        var normalised = email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == normalised, ct))
            return new RegisterResult(RegisterOutcome.EmailTaken, null);

        var user = new User { Id = Guid.NewGuid(), Email = normalised, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = _hasher.Hash(user, password);
        _db.Users.Add(user);
        _db.AuditLog.Add(Audit(user.Id, normalised, "None", "user.registered", user.Id, null, AccessAudit.Roles(Array.Empty<string>()),
            "self-service sign-up; no roles until an administrator grants them", traceId));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Someone registered the same email at the same moment.
            return new RegisterResult(RegisterOutcome.EmailTaken, null);
        }
        _log.LogInformation("User {UserId} signed up (no roles yet).", user.Id);
        return new RegisterResult(RegisterOutcome.Created, await _auth.LoginAsync(normalised, password, ct));
    }

    public async Task RequestPasswordResetAsync(string email, string? traceId, CancellationToken ct)
    {
        var normalised = email.Trim().ToLowerInvariant();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null) return; // same response either way; nothing to send

        var now = DateTimeOffset.UtcNow;
        var recent = await _db.PasswordResetTokens.CountAsync(t => t.UserId == user.Id && t.CreatedAt > now.AddMinutes(-15), ct);
        if (recent >= MaxResetRequestsPer15Min)
        {
            _log.LogWarning("Password reset for user {UserId} not sent: {Max} requests in 15 minutes.", user.Id, MaxResetRequestsPer15Min);
            return;
        }

        // A new link voids every older one.
        await _db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

        var raw = NewToken();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id, TokenHash = Hash(raw), CreatedAt = now, ExpiresAt = now.Add(_opts.ResetLinkLifetime),
        });
        _db.AuditLog.Add(Audit(user.Id, user.Email, "Self", "auth.password_reset_requested", user.Id, null, null,
            $"reset link valid for {(int)_opts.ResetLinkLifetime.TotalMinutes} minutes", traceId));
        await _db.SaveChangesAsync(ct);

        var link = $"{_email.PublicAppUrl}/reset-password?token={raw}";
        _emails.Enqueue(new EmailMessage(user.Email, "Reset your BunonBrain password",
            "Someone (hopefully you) asked to reset the password for this BunonBrain account.\n\n" +
            $"Open this link within {(int)_opts.ResetLinkLifetime.TotalMinutes} minutes to choose a new password:\n{link}\n\n" +
            "The link works once. If you didn't ask for this, ignore this email; your password stays the same."));
    }

    public async Task<ResetOutcome> ResetPasswordAsync(string token, string newPassword, string? traceId, CancellationToken ct)
    {
        var hash = Hash(token);
        var now = DateTimeOffset.UtcNow;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var row = await _db.PasswordResetTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
            if (row is null) return ResetOutcome.InvalidOrExpired;

            // Claim it: a link used twice at the same moment works only once.
            var claimed = await _db.PasswordResetTokens.Where(t => t.Id == row.Id && t.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
            if (claimed == 0) { await tx.RollbackAsync(ct); return ResetOutcome.InvalidOrExpired; }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == row.UserId, ct);
            if (user is null) { await tx.RollbackAsync(ct); return ResetOutcome.InvalidOrExpired; }
            user.PasswordHash = _hasher.Hash(user, newPassword);
            user.MustChangePassword = false;
            user.SecurityStamp = User.NewStamp(); // access tokens stop working
            user.UpdatedAt = now;
            user.RefreshTokenHash = null;        // signed out everywhere
            user.RefreshTokenExpiresAt = null;
            await _db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
            _db.AuditLog.Add(Audit(user.Id, user.Email, "Self", "auth.password_reset", user.Id, null, null,
                "password changed with an emailed link; other sessions signed out", traceId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _sessions.Invalidate(user.Id);
            _log.LogInformation("User {UserId} reset their password.", user.Id);
            return ResetOutcome.Reset;
        });
    }

    public async Task<AccountProfile?> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        var snapshot = await _access.LoadAsync(userId, ct);
        if (user is null || snapshot is null) return null;
        var lineIds = snapshot.LineIds.ToList();
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .OrderBy(l => l.Id).Select(l => new LineRef(l.Id, l.Name, l.FactoryId)).ToListAsync(ct);
        var worker = await _db.Workers.AsNoTracking().Where(w => w.UserId == userId)
            .Select(w => new WorkerRef(w.Id, w.WorkerCode, w.FullName)).FirstOrDefaultAsync(ct);
        return new AccountProfile(user.Id, user.Email, user.FullName, user.Phone, snapshot.Roles, snapshot.Permissions,
            snapshot.Scope.ToString(), snapshot.Factories, lines, worker, user.MustChangePassword, user.CreatedAt, user.LastLoginAt);
    }

    public async Task<AccessResult<AccountProfile>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, string? traceId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return AccessResult<AccountProfile>.NotFound("user_not_found", "Your account no longer exists.");
        var name = Clean(request.FullName);
        var phone = Clean(request.Phone);
        if (name != user.FullName || phone != user.Phone)
        {
            var before = $"name={user.FullName ?? "-"}; phone={(user.Phone is null ? "-" : "set")}";
            user.FullName = name;
            user.Phone = phone;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            _db.AuditLog.Add(Audit(user.Id, user.Email, "Self", "account.profile_updated", user.Id, null, null,
                $"own profile updated ({before} → name={name ?? "-"}; phone={(phone is null ? "-" : "set")})", traceId));
            await _db.SaveChangesAsync(ct);
        }
        return AccessResult<AccountProfile>.Ok((await GetProfileAsync(userId, ct))!);
    }

    public async Task<AccessResult<bool>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, string? traceId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return AccessResult<bool>.NotFound("user_not_found", "Your account no longer exists.");
        if (!_hasher.Verify(user, user.PasswordHash, request.CurrentPassword))
            return AccessResult<bool>.Invalid("current_password_incorrect", "The current password is not correct.");
        if (string.Equals(request.NewPassword.Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
            return AccessResult<bool>.Invalid("password_is_email", "Password must not be your email address.");

        var now = DateTimeOffset.UtcNow;
        user.PasswordHash = _hasher.Hash(user, request.NewPassword);
        var wasTemporary = user.MustChangePassword;
        user.MustChangePassword = false;
        // Old access tokens stop working; this browser's refresh cookie stays
        // valid, so the client refreshes and continues without signing in again.
        user.SecurityStamp = User.NewStamp();
        user.UpdatedAt = now;
        await _db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        _db.AuditLog.Add(Audit(user.Id, user.Email, "Self", "account.password_changed", user.Id, null, null,
            wasTemporary ? "temporary password replaced by the user" : "password changed by the user", traceId));
        await _db.SaveChangesAsync(ct);
        _sessions.Invalidate(user.Id);
        return AccessResult<bool>.Ok(true);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AuditLogEntry Audit(Guid? actorId, string actorEmail, string actorRole, string action, Guid entityId,
        string? from, string? to, string reason, string? traceId) => new()
    {
        OccurredAt = DateTime.UtcNow,
        ActorUserId = actorId,
        ActorEmail = actorEmail.Length <= 256 ? actorEmail : actorEmail[..256],
        ActorRole = actorRole,
        Action = action,
        EntityType = "user",
        EntityId = entityId.ToString(),
        FromState = from,
        ToState = to,
        Reason = reason,
        TraceId = traceId is { Length: > 128 } ? traceId[..128] : traceId,
    };

    private static string NewToken()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Hash(string raw) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(raw))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
