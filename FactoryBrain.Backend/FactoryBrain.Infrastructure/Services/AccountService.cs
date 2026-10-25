using System.Security.Cryptography;
using System.Text;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Auth;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

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
///   <item>Sign-up always creates a <c>Viewer</c>; only an Admin can grant more.</item>
///   <item>A reset link carries 32 random bytes; only their SHA-256 is stored,
///   it expires after 30 minutes, works once, and a new request voids older
///   links. A successful reset signs the user out everywhere.</item>
///   <item>Role changes can't remove the last Admin or change your own role.</item>
/// </list>
/// </summary>
public sealed class AccountService : IAccountService
{
    public const string RoleAdmin = "Admin", RoleOperator = "Operator", RoleViewer = "Viewer";

    /// <summary>At most this many reset emails per account per 15 minutes (whoever asks).</summary>
    public const int MaxResetRequestsPer15Min = 3;

    private readonly FactoryBrainDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IAuthService _auth;
    private readonly IEmailQueue _emails;
    private readonly AccountOptions _opts;
    private readonly EmailOptions _email;
    private readonly ILogger<AccountService> _log;

    public AccountService(FactoryBrainDbContext db, IPasswordHasher hasher, IAuthService auth, IEmailQueue emails,
        AccountOptions opts, EmailOptions email, ILogger<AccountService> log)
    { _db = db; _hasher = hasher; _auth = auth; _emails = emails; _opts = opts; _email = email; _log = log; }

    public bool SelfRegistrationEnabled => _opts.SelfRegistration;

    public async Task<RegisterResult> RegisterAsync(string email, string password, string? traceId, CancellationToken ct)
    {
        if (!_opts.SelfRegistration) return new RegisterResult(RegisterOutcome.Disabled, null);
        var normalised = email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == normalised, ct))
            return new RegisterResult(RegisterOutcome.EmailTaken, null);

        var user = new User { Id = Guid.NewGuid(), Email = normalised, Role = RoleViewer, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = _hasher.Hash(user, password);
        _db.Users.Add(user);
        _db.AuditLog.Add(Audit(user.Id, normalised, RoleViewer, "user.registered", user.Id, null, RoleViewer,
            "self-service sign-up", traceId));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Someone registered the same email at the same moment.
            return new RegisterResult(RegisterOutcome.EmailTaken, null);
        }
        _log.LogInformation("User {UserId} signed up (Viewer).", user.Id);
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
        _db.AuditLog.Add(Audit(user.Id, user.Email, user.Role, "auth.password_reset_requested", user.Id, null, null,
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
            user.RefreshTokenHash = null;        // signed out everywhere
            user.RefreshTokenExpiresAt = null;
            await _db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
            _db.AuditLog.Add(Audit(user.Id, user.Email, user.Role, "auth.password_reset", user.Id, null, null,
                "password changed with an emailed link; other sessions signed out", traceId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _log.LogInformation("User {UserId} reset their password.", user.Id);
            return ResetOutcome.Reset;
        });
    }

    public async Task<IReadOnlyList<UserListItem>> ListUsersAsync(CancellationToken ct) =>
        await _db.Users.AsNoTracking().OrderBy(u => u.CreatedAt)
            .Select(u => new UserListItem(u.Id, u.Email, u.Role, u.CreatedAt)).ToListAsync(ct);

    public async Task<RoleChangeResult> ChangeRoleAsync(Guid userId, string role, AuditActor actor, string? traceId, CancellationToken ct)
    {
        if (role is not (RoleAdmin or RoleOperator or RoleViewer)) return new(RoleChangeOutcome.InvalidRole, null);
        if (actor.UserId == userId) return new(RoleChangeOutcome.OwnRole, null);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // Lock the Admin rows so two concurrent demotions can't remove the last Admin.
            var admins = await _db.Users.FromSqlRaw("SELECT * FROM users WHERE \"Role\" = 'Admin' FOR UPDATE").ToListAsync(ct);
            var user = admins.FirstOrDefault(u => u.Id == userId) ?? await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) { await tx.RollbackAsync(ct); return new RoleChangeResult(RoleChangeOutcome.NotFound, null); }
            if (user.Role == RoleAdmin && role != RoleAdmin && admins.Count <= 1)
            { await tx.RollbackAsync(ct); return new RoleChangeResult(RoleChangeOutcome.LastAdmin, null); }

            var from = user.Role;
            if (from != role)
            {
                user.Role = role;
                _db.AuditLog.Add(Audit(actor.UserId, actor.Email, actor.Role, "user.role_changed", user.Id, from, role,
                    $"role of {user.Email}: {from} → {role}", traceId));
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return new RoleChangeResult(RoleChangeOutcome.Changed, new UserListItem(user.Id, user.Email, user.Role, user.CreatedAt));
        });
    }

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
