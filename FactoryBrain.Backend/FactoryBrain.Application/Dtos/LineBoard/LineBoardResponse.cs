namespace FactoryBrain.Application.Dtos.LineBoard;

public record LineBoardResponse(
    IReadOnlyList<LineBoardRow> Rows,
    LineBoardMeta Meta
);

public record LineBoardRow(
    string LineId,
    string Name,
    int EfficiencyPct,
    int SahTarget,
    int SahActual,
    int WipBundles,
    string Bottleneck,
    int NptMinutes,
    DateTime UpdatedAt
);

public record LineBoardMeta(
    bool Simulated,
    string Source,
    int Tick,
    DateTime UpdatedAt,
    string Notes
);

public record RefreshRequest(int? Tick);
