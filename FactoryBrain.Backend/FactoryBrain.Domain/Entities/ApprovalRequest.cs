using FactoryBrain.Domain.Enums;

namespace FactoryBrain.Domain.Entities;

public class ApprovalRequest
{
    public string Id { get; set; } = default!;
    public string InsightId { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string TitleBn { get; set; } = default!;
    public string? Reason { get; set; }
    public RiskTier RiskTier { get; set; }
    public Stage Stage { get; set; } = Stage.PendingApproval;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
