namespace FactoryBrain.Application.Dtos.Sensors;

public record IngestRequest(int? Tick, bool? Simulate = null);

/// <summary>One reading from a floor device. Posted to POST /api/sensors/live.</summary>
public record LiveReadingInput(
    string Source,
    string EntityId,
    string Metric,
    double Value,
    string? Unit = null,
    DateTime? Ts = null
);

public record LiveIngestRequest(IReadOnlyList<LiveReadingInput> Readings);

public record IngestResponse(
    bool Simulated,
    string Source,
    int Tick,
    IReadOnlyList<SensorReadingDto> Readings,
    IReadOnlyList<LineSummaryDto> Lines,
    IReadOnlyList<MachineSummaryDto> Machines
);

public record SensorReadingDto(
    string Id,
    string Source,
    string EntityId,
    string Metric,
    double Value,
    string Unit,
    DateTime Ts,
    int SimTick
);

public record LineSummaryDto(string Id, double Efficiency, double Uptime, double EnergyKwh);
public record MachineSummaryDto(string Id, double Vibration, double Temperature, double DutyCycle, string Status);

public record LatestReadingsResponse(
    bool Simulated,
    string Source,
    int Count,
    IReadOnlyList<SensorReadingDto> Readings
);

public record SimStatusResponse(
    bool Simulated,
    string Source,
    int Tick,
    int ReadingsCount,
    int LinesCount,
    int MachinesCount
);
