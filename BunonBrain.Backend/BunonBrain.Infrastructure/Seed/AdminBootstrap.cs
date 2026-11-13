using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BunonBrain.Infrastructure.Persistence;

public enum AdminBootstrapOutcome
{
    /// <summary>SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD not both set.</summary>
    Skipped,
    /// <summary>No account had that email; a new Super Admin was created.</summary>
    Created,
    /// <summary>An existing account was granted Super Admin (its password matched).</summary>
    Promoted,
    /// <summary>The account is already a Super Admin; nothing changed.</summary>
    AlreadyAdmin,
    /// <summary>An account exists but SEED_ADMIN_PASSWORD is not its password; nothing changed.</summary>
    PasswordMismatch,
}

/// <summary>
/// Startup bootstrap for the first Super Admin, driven by <c>SEED_ADMIN_EMAIL</c> +
/// <c>SEED_ADMIN_PASSWORD</c> (server configuration — not reachable through
/// the API).
/// <list type="bullet">
///   <item>No account with that email: create it as Super Admin.</item>
///   <item>An existing account without Super Admin: grant it only when
///   <c>SEED_ADMIN_PASSWORD</c> is that account's current password, so the
///   server configuration alone can't take over someone else's account. The
///   grant is written to the audit log; the password is never changed. A
///   deactivated account is reactivated.</item>
///   <item>Already a Super Admin: nothing to do.</item>
/// </list>
/// Self-registration never grants a role, and nobody can change their own roles.
/// </summary>
public static class AdminBootstrap
{
    public const string ActorEmail = "system:SEED_ADMIN_EMAIL";
    public const string ActorRole = "System";

    public static async Task<AdminBootstrapOutcome> EnsureAsync(
        BunonBrainDbContext db, IPasswordHasher hasher, string? email, string? password, ILogger log, CancellationToken ct)
    {
        email = (email ?? string.Empty).Trim();
        password ??= string.Empty;
        if (email.Length == 0 || password.Length == 0) return AdminBootstrapOutcome.Skipped;

        var normalised = email.ToLowerInvariant();
        var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null)
        {
            var created = new User { Id = Guid.NewGuid(), Email = normalised, CreatedAt = DateTimeOffset.UtcNow };
            created.PasswordHash = hasher.Hash(created, password);
            created.Roles.Add(new UserRole { UserId = created.Id, RoleId = SystemRoles.SuperAdmin });
            db.Users.Add(created);
            db.AuditLog.Add(new AuditLogEntry
            {
                OccurredAt = DateTime.UtcNow, ActorUserId = null, ActorEmail = ActorEmail, ActorRole = ActorRole,
                Action = "user.created", EntityType = "user", EntityId = created.Id.ToString(),
                FromState = null, ToState = SystemRoles.SuperAdmin,
                Reason = $"{normalised} created as Super Admin (startup bootstrap)",
            });
            await db.SaveChangesAsync(ct);
            // Never log the password — only the email and what happened.
            log.LogInformation("Seeded Super Admin {Email} from SEED_ADMIN_EMAIL.", normalised);
            return AdminBootstrapOutcome.Created;
        }

        if (user.Roles.Any(r => r.RoleId == SystemRoles.SuperAdmin) && user.IsActive) return AdminBootstrapOutcome.AlreadyAdmin;

        if (!hasher.Verify(user, user.PasswordHash, password))
        {
            log.LogWarning(
                "SEED_ADMIN_EMAIL {Email} belongs to an existing account, but SEED_ADMIN_PASSWORD is not its password. " +
                "The account was not changed. Set SEED_ADMIN_PASSWORD to that account's password, or use an email that has no account yet.",
                normalised);
            return AdminBootstrapOutcome.PasswordMismatch;
        }

        var from = string.Join(",", user.Roles.Select(r => r.RoleId).OrderBy(r => r));
        if (user.Roles.All(r => r.RoleId != SystemRoles.SuperAdmin))
            user.Roles.Add(new UserRole { UserId = user.Id, RoleId = SystemRoles.SuperAdmin });
        user.IsActive = true;
        user.DeactivatedAt = null;
        user.SecurityStamp = User.NewStamp();
        db.AuditLog.Add(new AuditLogEntry
        {
            OccurredAt = DateTime.UtcNow,
            ActorUserId = null,
            ActorEmail = ActorEmail,
            ActorRole = ActorRole,
            Action = "user.roles_changed",
            EntityType = "user",
            EntityId = user.Id.ToString(),
            FromState = from.Length == 0 ? "(none)" : from,
            ToState = SystemRoles.SuperAdmin,
            Reason = $"{user.Email} granted Super Admin (startup bootstrap; account password verified)",
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Granted Super Admin to existing account {Email} via SEED_ADMIN_EMAIL.", normalised);
        return AdminBootstrapOutcome.Promoted;
    }
}
