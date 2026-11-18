using BunonBrain.Application.Dtos.Approvals;
using BunonBrain.Application.Dtos.Ask;

namespace BunonBrain.Application.Dtos.Operations;

// Read-only views that pull existing records together for one screen:
// the insight feed, the factory graph, the per-floor rollup and the
// integration (data source) status. None of them stores anything.

/// <summary>An insight written by an agent run, a QC inspection or the production rules, in the frontend's InsightPublic shape.</summary>
public sealed record InsightDto(
    string Id,
    string AgentId,
    string AgentLabel,
    string Title,
    string TitleBn,
    string Finding,
    string FindingBn,
    IReadOnlyList<AskFactor> Factors,
    ApprovalRecommendationDto Recommendation,
    IReadOnlyList<EvidenceRefDto> Evidence,
    string Stage,
    double Confidence,
    bool Pinned,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record InsightListResponse(int Total, IReadOnlyList<InsightDto> Items);

/// <summary>One production floor's live rollup. <see cref="Status"/>: <c>ok</c>, <c>watch</c> or <c>act</c>.</summary>
public sealed record FloorStatusDto(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    string Status,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<FloorLineDto> Lines,
    int Workers,
    int CheckedIn,
    int Workstations,
    int Machines,
    IReadOnlyDictionary<string, int> MachineStates,
    int Cameras,
    int DevicesOnline,
    int DevicesOffline,
    int ActiveAlerts,
    int HighAlerts,
    int OpenExceptions,
    IReadOnlyList<FloorAlertDto> RecentAlerts);

public sealed record FloorLineDto(string Id, string Name, int Workstations, int Machines, int MachinesDown, int ActiveAlerts);

public sealed record FloorAlertDto(Guid Id, long Number, string Title, string Severity, string Status, string? LineId, DateTimeOffset LastOccurredAt);

public sealed record FloorStatusResponse(Guid FactoryId, string FactoryCode, DateTimeOffset GeneratedAt, IReadOnlyList<FloorStatusDto> Floors, int UnplacedLines, string Note);

/// <summary>A node of the factory graph. <see cref="Status"/>: <c>healthy</c>, <c>at_risk</c>, <c>down</c> or null when unknown.</summary>
public sealed record BrainNodeDto(string Id, string Kind, string Label, string LabelBn, double Weight, string? Status, IReadOnlyDictionary<string, string> Meta, string? Href);

public sealed record BrainEdgeDto(string Id, string Source, string Target, double Weight, bool Strong, string Kind, string? Label);

public sealed record BrainGraphResponse(Guid FactoryId, string FactoryCode, DateTimeOffset GeneratedAt, IReadOnlyList<BrainNodeDto> Nodes, IReadOnlyList<BrainEdgeDto> Edges,
    IReadOnlyList<string> Omitted, IReadOnlyList<string> Truncated);

/// <summary>
/// One data source. <see cref="Status"/>: <c>receiving</c> (a record in the
/// last 24 h), <c>connected</c> (configured or has history, nothing recent)
/// or <c>not_connected</c>.
/// </summary>
public sealed record IntegrationStatusDto(
    string Id,
    string Category,
    string Label,
    string Description,
    string Status,
    int Devices,
    int DevicesOnline,
    int DevicesOffline,
    long Records,
    long Records24h,
    long SimulatedRecords,
    DateTimeOffset? LastRecordAt,
    string Href,
    string? Note);

public sealed record IntegrationStatusResponse(Guid FactoryId, string FactoryCode, DateTimeOffset GeneratedAt, IReadOnlyList<IntegrationStatusDto> Sources);
