namespace FactoryBrain.Application.Dtos.Vision;

public record VisionAnalyzeRequest(string SampleFile);

public record VisionAnalyzeResponse(
    bool Simulated,
    string Source,
    VisionResultDto Result,
    InsightLiteDto? Insight,
    bool? ApprovalPending,
    string? ApprovalReason,
    string? FloorAlertId,
    string? ActivityEventId
);

public record VisionResultDto(
    string Id,
    string SampleFile,
    string AnomalyLabelEn,
    string AnomalyLabelBn,
    IReadOnlyList<string> RepairStepsEn,
    IReadOnlyList<string> RepairStepsBn,
    double Confidence,
    string? MachineId,
    DateTime CreatedAt
);

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

public record VisionAllowedResponse(
    bool Simulated,
    string Source,
    IReadOnlyList<string> AllowedSampleFiles,
    string Method
);
