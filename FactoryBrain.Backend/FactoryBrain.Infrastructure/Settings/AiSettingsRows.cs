using FactoryBrain.Application.Abstractions;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Settings;

/// <summary>Shared write-side helpers for the single <c>ai_settings</c> row.</summary>
internal static class AiSettingsRows
{
    /// <summary>
    /// Create the row if it doesn't exist yet, then lock it for the rest of
    /// the current transaction (<c>SELECT … FOR UPDATE</c>). Writers on every
    /// API instance are serialised by this lock, and each applies its change
    /// to the latest committed values. Must run inside a transaction.
    /// </summary>
    public static async Task<AiSettingsRecord> LockAsync(FactoryBrainDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO ai_settings (\"Id\", \"Version\", \"UpdatedAt\") VALUES (1, 0, now()) ON CONFLICT (\"Id\") DO NOTHING", ct);
        // ToListAsync keeps EF from wrapping the locking query in a subquery.
        var rows = await db.AiSettings.FromSqlRaw("SELECT * FROM ai_settings WHERE \"Id\" = 1 FOR UPDATE").ToListAsync(ct);
        return rows.Single();
    }

    public static void Touch(AiSettingsRecord row, AuditActor actor, DateTimeOffset now)
    {
        row.Version++;
        row.UpdatedAt = now;
        row.UpdatedByUserId = actor.UserId;
        row.UpdatedByEmail = Truncate(actor.Email, 256);
    }

    public static AuditLogEntry Audit(AuditActor actor, string action, string entityType, string entityId,
        string? from, string? to, string reason, string? traceId, DateTimeOffset now) => new()
    {
        OccurredAt  = now.UtcDateTime,
        ActorUserId = actor.UserId,
        ActorEmail  = Truncate(actor.Email, 256)!,
        ActorRole   = Truncate(actor.Role, 32)!,
        Action      = action,
        EntityType  = entityType,
        EntityId    = entityId,
        FromState   = Truncate(from, 32),
        ToState     = Truncate(to, 32),
        Reason      = Truncate(reason, 1000),
        TraceId     = Truncate(traceId, 128),
    };

    public static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
