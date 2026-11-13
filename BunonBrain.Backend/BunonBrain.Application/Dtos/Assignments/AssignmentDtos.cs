using BunonBrain.Application.Dtos.Workers;

namespace BunonBrain.Application.Dtos.Assignments;

// ─── Workstations & machines ───────────────────────────────────────────────

public sealed record MachineRef(Guid Id, string Code, string? MachineType, bool IsActive);

/// <summary>A worker currently assigned to a workstation (only for callers who may see workers).</summary>
public sealed record AssignedWorkerRef(Guid AssignmentId, Guid WorkerId, string WorkerCode, string FullName, DateTimeOffset StartAt, MachineRef? Machine);

/// <summary>
/// A workstation with its current occupancy. <see cref="Occupancy"/> is one of
/// Available, Occupied, CapacityReached or Inactive, and reflects the recorded
/// assignments only (not attendance or machine activity).
/// <see cref="Workers"/> is null when the caller may not see workers.
/// </summary>
public sealed record WorkstationDto(
    Guid Id,
    string Code,
    string? Name,
    Guid FactoryId,
    string FactoryCode,
    OrgRef Department,
    OrgRef? Floor,
    WorkerLineRef Line,
    int Capacity,
    int Position,
    bool IsActive,
    int AssignedCount,
    string Occupancy,
    IReadOnlyList<MachineRef> Machines,
    IReadOnlyList<AssignedWorkerRef>? Workers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>GET /api/workstations query. <c>occupancy</c>: Available | Occupied | CapacityReached | Inactive.</summary>
public sealed record WorkstationListQuery(
    string? Search = null, Guid? FactoryId = null, Guid? DepartmentId = null, Guid? FloorId = null, string? LineId = null,
    string? Occupancy = null, bool IncludeInactive = true, int Page = 1, int PageSize = 50);

public sealed record CreateWorkstationRequest(
    Guid FactoryId, string LineId, Guid DepartmentId, string Code, Guid? FloorId = null, string? Name = null, int? Capacity = null, int? Position = null);

/// <summary>PUT /api/workstations/{id}. The factory and code can't change.</summary>
public sealed record UpdateWorkstationRequest(
    string LineId, Guid DepartmentId, Guid? FloorId, string? Name, int Capacity, int Position, bool IsActive);

/// <summary>POST /api/workstations/bulk: <c>Prefix</c> + numbers From … From+Count-1, e.g. T-001 … T-030.</summary>
public sealed record BulkCreateWorkstationsRequest(
    Guid FactoryId, string LineId, Guid DepartmentId, string Prefix, int From, int Count, Guid? FloorId = null, int? Capacity = null);

public sealed record BulkCreateWorkstationsResult(int Created, IReadOnlyList<string> Codes, IReadOnlyList<string> Skipped);

public sealed record MachineDto(
    Guid Id, Guid FactoryId, string Code, string? MachineType, string? Brand, string? SerialNumber, bool IsActive,
    Guid? WorkstationId, string? WorkstationCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record MachineListQuery(Guid? FactoryId = null, Guid? WorkstationId = null, bool? Unplaced = null, string? Search = null);

public sealed record CreateMachineRequest(Guid FactoryId, string Code, string? MachineType = null, string? Brand = null, string? SerialNumber = null, Guid? WorkstationId = null);

/// <summary>PUT /api/machines/{id}: details, placement (null = not placed) and status.</summary>
public sealed record UpdateMachineRequest(string? MachineType, string? Brand, string? SerialNumber, Guid? WorkstationId, bool IsActive);

// ─── Assignments ───────────────────────────────────────────────────────────

public sealed record WorkstationRef(Guid Id, string Code, string? Name);

public sealed record AssignmentDto(
    Guid Id,
    Guid WorkerId,
    string WorkerCode,
    string WorkerName,
    Guid FactoryId,
    OrgRef Department,
    OrgRef? Floor,
    WorkerLineRef Line,
    WorkstationRef Workstation,
    MachineRef? Machine,
    bool IsPrimary,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    string Status,
    string? Reason,
    string? Notes,
    string? EndReason,
    Guid? PreviousAssignmentId,
    // The assignment that replaced this one, when it was closed by a transfer.
    Guid? NextAssignmentId,
    string AssignedByEmail,
    string? EndedByEmail,
    DateTimeOffset CreatedAt);

/// <summary>GET /api/assignments query. <c>status</c>: Active | Ended | Transferred.</summary>
public sealed record AssignmentListQuery(
    Guid? FactoryId = null, Guid? DepartmentId = null, Guid? FloorId = null, string? LineId = null, Guid? WorkstationId = null,
    Guid? WorkerId = null, string? Status = null, string? Search = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    int Page = 1, int PageSize = 50);

/// <summary>POST /api/assignments. <c>StartAt</c> defaults to now; it may be up to 31 days in the past.</summary>
public sealed record CreateAssignmentRequest(
    Guid WorkerId, Guid WorkstationId, Guid? MachineId = null, DateTimeOffset? StartAt = null, string? Reason = null, string? Notes = null);

/// <summary>POST /api/assignments/{id}/transfer: close this assignment and open one at the new workstation at the same instant.</summary>
public sealed record TransferAssignmentRequest(
    Guid WorkstationId, string Reason, Guid? MachineId = null, DateTimeOffset? EffectiveAt = null, string? Notes = null);

/// <summary>POST /api/assignments/{id}/end.</summary>
public sealed record EndAssignmentRequest(string Reason, DateTimeOffset? EndAt = null);

public sealed record TransferResult(AssignmentDto Closed, AssignmentDto Opened);

/// <summary>GET /api/assignments/statistics: counts over the caller's scope. Assignments are recorded placements, not presence.</summary>
public sealed record AssignmentStatisticsDto(
    int TotalWorkstations,
    int ActiveWorkstations,
    int OccupiedWorkstations,
    int AvailableWorkstations,
    int FullWorkstations,
    int InactiveWorkstations,
    int TotalCapacity,
    int ActiveAssignments,
    int ActiveWorkers,
    int UnassignedActiveWorkers,
    IReadOnlyList<LineOccupancyDto> ByLine,
    IReadOnlyList<AssignmentDto> RecentTransfers);

public sealed record LineOccupancyDto(string LineId, string LineName, int Workstations, int Occupied, int Available, int Full, int Inactive, int Capacity, int Assigned);
