namespace FactoryBrain.Application.Dtos.FloorAlerts;

public record FloorAlertListResponse(
    bool Simulated,
    string Source,
    int Count,
    IReadOnlyList<FloorAlertDto> Alerts
);

public record FloorAlertDto(
    string Id,
    string Channel,
    string? ApprovalId,
    string? InsightId,
    string BodyEn,
    string BodyBn,
    string Severity,
    DateTime CreatedAt,
    bool Read
);

public record FloorAlertPatchRequest(bool Read);
public record FloorAlertPatchResponse(bool Ok, FloorAlertDto Alert);
