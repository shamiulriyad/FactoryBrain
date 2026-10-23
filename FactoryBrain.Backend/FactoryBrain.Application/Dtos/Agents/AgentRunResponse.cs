using FactoryBrain.Application.Dtos.Vision;

namespace FactoryBrain.Application.Dtos.Agents;

public record AgentRunResponse(
    string Source,
    string AgentId,
    InsightLiteDto Insight,
    bool ApprovalPending,
    string? ApprovalReason,
    string? FloorAlertId,
    string? ActivityEventId,
    EnergyInsightDto? EnergyInsight,
    string? Warning
);

public record EnergyInsightDto(
    string InsightId,
    string FloorAlertId,
    double Score
);
