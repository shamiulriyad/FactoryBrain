namespace FactoryBrain.Application.Dtos.Qc;

public record QcDefectsResponse(
    IReadOnlyList<QcOperationStats> Operations,
    IReadOnlyList<QcTopOperation> TopByDefectRate,
    IReadOnlyList<QcTopOperation> TopByReworkRate,
    QcMeta Meta
);

public record QcOperationStats(
    string Operation,
    string LineId,
    IReadOnlyList<QcWeekBucket> Weeks,
    QcTotals Totals
);

public record QcWeekBucket(
    string WeekStart,
    int Inspected,
    int Defects,
    int Major,
    int Minor,
    int Rework
);

public record QcTotals(int Inspected, int Defects, int Major, int Minor, int Rework);

public record QcTopOperation(
    string Operation,
    string LineId,
    double DefectRatePct,
    double ReworkRatePct
);

public record QcMeta(
    bool Simulated,
    string Source,
    string DefectSource,
    DateTime GeneratedAt,
    string Notes
);

public record QcFlagRequest(
    string Operation,
    string LineId,
    double? DefectRatePct,
    double? ReworkRatePct,
    string? Note
);

public record QcFlagResponse(string InsightId, DateTime FlaggedAt);
