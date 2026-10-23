using FactoryBrain.Domain.Enums;

namespace FactoryBrain.Domain.Entities;

/// <summary>
/// Floor alert pushed to a supervisor (simulated WhatsApp channel in the
/// demo). Mirrors <c>src/data/floorAlerts.ts</c>.
/// </summary>
public class FloorAlert
{
    public string Id { get; set; } = default!;            // "alert-1"
    public string Channel { get; set; } = "whatsapp_sim";
    public string? ApprovalId { get; set; }
    public string? InsightId { get; set; }
    public string BodyEn { get; set; } = default!;
    public string BodyBn { get; set; } = default!;
    public FloorAlertSeverity Severity { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Read { get; set; }
}
