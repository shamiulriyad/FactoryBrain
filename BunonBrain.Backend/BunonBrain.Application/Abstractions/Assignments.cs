using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Assignments;

namespace BunonBrain.Application.Abstractions.Interfaces;

/// <summary>
/// Workstations and machines. Reach: Super Admin everything; factory roles
/// their factories; a Production Supervisor the workstations of assigned
/// lines. Configuring them needs factory-wide reach (checked here) and
/// <c>lines.manage</c> (machines: or <c>devices.manage</c>), checked on the endpoint.
/// </summary>
public interface IWorkstationService
{
    Task<PagedResult<WorkstationDto>> ListAsync(AccessContext caller, WorkstationListQuery query, CancellationToken ct);
    Task<AccessResult<WorkstationDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<WorkstationDto>> CreateAsync(AccessContext caller, CreateWorkstationRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<BulkCreateWorkstationsResult>> BulkCreateAsync(AccessContext caller, BulkCreateWorkstationsRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<WorkstationDto>> UpdateAsync(AccessContext caller, Guid id, UpdateWorkstationRequest request, string? traceId, CancellationToken ct);

    Task<AccessResult<IReadOnlyList<MachineDto>>> MachinesAsync(AccessContext caller, MachineListQuery query, CancellationToken ct);
    Task<AccessResult<MachineDto>> CreateMachineAsync(AccessContext caller, CreateMachineRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<MachineDto>> UpdateMachineAsync(AccessContext caller, Guid id, UpdateMachineRequest request, string? traceId, CancellationToken ct);
}

/// <summary>
/// Worker ↔ workstation assignments and transfers. Every write runs in one
/// transaction under row locks (worker, workstations, the assignment being
/// closed), so two supervisors acting at once can't exceed a workstation's
/// capacity or give a worker two active primary assignments; a database
/// exclusion constraint also rejects overlapping periods for one worker.
/// History is never deleted or overwritten.
/// </summary>
public interface IAssignmentService
{
    Task<PagedResult<AssignmentDto>> ListAsync(AccessContext caller, AssignmentListQuery query, CancellationToken ct);
    Task<AccessResult<AssignmentDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    /// <summary><paramref name="workerIdOrCode"/>: the worker's id or Worker ID (W-001).</summary>
    Task<AccessResult<IReadOnlyList<AssignmentDto>>> WorkerHistoryAsync(AccessContext caller, string workerIdOrCode, CancellationToken ct);
    Task<AccessResult<IReadOnlyList<AssignmentDto>>> WorkstationHistoryAsync(AccessContext caller, Guid workstationId, int limit, CancellationToken ct);
    Task<AssignmentStatisticsDto> StatisticsAsync(AccessContext caller, Guid? factoryId, string? lineId, CancellationToken ct);

    Task<AccessResult<AssignmentDto>> CreateAsync(AccessContext caller, CreateAssignmentRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<TransferResult>> TransferAsync(AccessContext caller, Guid assignmentId, TransferAssignmentRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<AssignmentDto>> EndAsync(AccessContext caller, Guid assignmentId, EndAssignmentRequest request, string? traceId, CancellationToken ct);
}
