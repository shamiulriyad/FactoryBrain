namespace FactoryBrain.Domain.Entities;

/// <summary>
/// One append-only audit record. Written in the same transaction as the
/// change it describes; the <c>audit_log</c> table rejects UPDATE, DELETE and
/// TRUNCATE at the database level (see the AddApprovalDecisionsAndAuditLog
/// migration), so recorded history can't be edited through the app.
/// </summary>
public class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>JWT subject of the person who acted, when known.</summary>
    public Guid? ActorUserId { get; set; }
    public string ActorEmail { get; set; } = default!;
    public string ActorRole { get; set; } = default!;

    /// <summary>Dotted verb, e.g. <c>approval.approved</c>.</summary>
    public string Action { get; set; } = default!;
    public string EntityType { get; set; } = default!;
    public string EntityId { get; set; } = default!;
    public string? FromState { get; set; }
    public string? ToState { get; set; }
    public string? Reason { get; set; }
    /// <summary>Request trace id, to correlate with server logs.</summary>
    public string? TraceId { get; set; }
}
