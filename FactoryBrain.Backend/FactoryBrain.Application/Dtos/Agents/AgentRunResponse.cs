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

/// <summary>The insight an agent run stored, in the shape the frontend's InsightLite expects.</summary>
public record InsightLiteDto(
    string Id,
    string Title,
    string TitleBn,
    string Finding,
    string FindingBn,
    string RecommendationTitle,
    string RecommendationAction,
    string RiskTier,
    string Stage,
    double Confidence
);
