using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FactoryBrain.Infrastructure.Persistence;

public enum AdminBootstrapOutcome
{
    /// <summary>SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD not both set.</summary>
    Skipped,
    /// <summary>No account had that email; a new Admin was created.</summary>
    Created,
    /// <summary>An existing non-Admin account was promoted (its password matched).</summary>
    Promoted,
    /// <summary>The account is already an Admin; nothing changed.</summary>
    AlreadyAdmin,
    /// <summary>An account exists but SEED_ADMIN_PASSWORD is not its password; nothing changed.</summary>
    PasswordMismatch,
}

/// <summary>
/// Startup bootstrap for the first Admin, driven by <c>SEED_ADMIN_EMAIL</c> +
/// <c>SEED_ADMIN_PASSWORD</c> (server configuration — not reachable through
/// the API).
/// <list type="bullet">
///   <item>No account with that email: create it as Admin.</item>
///   <item>An existing Viewer/Operator account: promote it to Admin only when
///   <c>SEED_ADMIN_PASSWORD</c> is that account's current password, so the
///   server configuration alone can't take over someone else's account. The
///   promotion is written to the audit log; the password is never changed.</item>
///   <item>Already an Admin: nothing to do.</item>
/// </list>
/// Self-registration still always creates a Viewer, and the role API still
/// refuses to change your own role.
/// </summary>
public static class AdminBootstrap
{
    public const string ActorEmail = "system:SEED_ADMIN_EMAIL";
    public const string ActorRole = "System";

    public static async Task<AdminBootstrapOutcome> EnsureAsync(
        FactoryBrainDbContext db, IPasswordHasher hasher, string? email, string? password, ILogger log, CancellationToken ct)
    {
        email = (email ?? string.Empty).Trim();
        password ??= string.Empty;
        if (email.Length == 0 || password.Length == 0) return AdminBootstrapOutcome.Skipped;

        var normalised = email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null)
        {
            var created = new User { Id = Guid.NewGuid(), Email = normalised, Role = "Admin", CreatedAt = DateTimeOffset.UtcNow };
            created.PasswordHash = hasher.Hash(created, password);
            db.Users.Add(created);
            await db.SaveChangesAsync(ct);
            // Never log the password — only the email and what happened.
            log.LogInformation("Seeded admin user {Email} from SEED_ADMIN_EMAIL.", normalised);
            return AdminBootstrapOutcome.Created;
        }

        if (user.Role == "Admin") return AdminBootstrapOutcome.AlreadyAdmin;

        if (!hasher.Verify(user, user.PasswordHash, password))
        {
            log.LogWarning(
                "SEED_ADMIN_EMAIL {Email} belongs to an existing {Role} account, but SEED_ADMIN_PASSWORD is not its password. " +
                "The account was not promoted. Set SEED_ADMIN_PASSWORD to that account's password, or use an email that has no account yet.",
                normalised, user.Role);
            return AdminBootstrapOutcome.PasswordMismatch;
        }

        var from = user.Role;
        user.Role = "Admin";
        db.AuditLog.Add(new AuditLogEntry
        {
            OccurredAt = DateTime.UtcNow,
            ActorUserId = null,
            ActorEmail = ActorEmail,
            ActorRole = ActorRole,
            Action = "user.role_changed",
            EntityType = "user",
            EntityId = user.Id.ToString(),
            FromState = from,
            ToState = "Admin",
            Reason = $"role of {user.Email}: {from} → Admin (startup bootstrap; account password verified)",
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Promoted existing account {Email} from {From} to Admin via SEED_ADMIN_EMAIL.", normalised, from);
        return AdminBootstrapOutcome.Promoted;
    }
}
