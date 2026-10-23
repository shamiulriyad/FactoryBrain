namespace FactoryBrain.Application.Dtos.Brief;

public record MorningBriefResponse(
    string Date,
    IReadOnlyList<string> BulletsEn,
    IReadOnlyList<string> BulletsBn,
    string? TopBottleneckLineId,
    int RiskCount,
    int PendingApprovals,
    MorningBriefMeta Meta
);

public record MorningBriefMeta(
    bool Simulated,
    string Source,
    int Tick,
    DateTime GeneratedAt,
    string Notes
);
