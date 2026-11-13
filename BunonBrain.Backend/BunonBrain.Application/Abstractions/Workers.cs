using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Workers;

namespace BunonBrain.Application.Abstractions.Interfaces;

/// <summary>
/// Worker management. Every method enforces the caller's reach on the server:
/// a Super Admin reaches every factory; factory roles their own factories; a
/// Production Supervisor only workers on assigned lines; a Worker only their
/// own record. A worker outside the caller's reach answers "not found", so
/// its existence isn't revealed. How much of a worker is returned depends on
/// the caller's permissions (see <see cref="WorkerDto"/>). Workers are never
/// deleted; every change is audited in the same transaction.
/// </summary>
public interface IWorkerService
{
    Task<PagedResult<WorkerDto>> ListAsync(AccessContext caller, WorkerListQuery query, CancellationToken ct);

    /// <summary><paramref name="idOrCode"/> is the worker's id (GUID) or Worker ID (<c>W-001</c>).</summary>
    Task<AccessResult<WorkerDto>> GetAsync(AccessContext caller, string idOrCode, CancellationToken ct);

    Task<AccessResult<WorkerProfileDto>> ProfileAsync(AccessContext caller, string idOrCode, CancellationToken ct);

    /// <summary>The caller's own worker record, if their account is linked to one.</summary>
    Task<WorkerDto?> MineAsync(AccessContext caller, CancellationToken ct);

    Task<WorkerStatisticsDto> StatisticsAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);

    Task<AccessResult<WorkerDto>> CreateAsync(AccessContext caller, CreateWorkerRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<WorkerDto>> UpdateAsync(AccessContext caller, string idOrCode, UpdateWorkerRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<WorkerDto>> ChangeStatusAsync(AccessContext caller, string idOrCode, ChangeWorkerStatusRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<WorkerDto>> AssignWorkstationAsync(AccessContext caller, string idOrCode, AssignWorkstationRequest request, string? traceId, CancellationToken ct);

    Task<AccessResult<WorkerDto>> LinkAccountAsync(AccessContext caller, string idOrCode, LinkWorkerAccountRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<WorkerDto>> CreateAccountAsync(AccessContext caller, string idOrCode, CreateWorkerAccountRequest request, string? traceId, CancellationToken ct);

    Task<AccessResult<WorkerPhoto>> PhotoAsync(AccessContext caller, string idOrCode, CancellationToken ct);
    Task<AccessResult<WorkerDto>> SetPhotoAsync(AccessContext caller, string idOrCode, byte[] content, string? traceId, CancellationToken ct);
    Task<AccessResult<WorkerDto>> RemovePhotoAsync(AccessContext caller, string idOrCode, string? traceId, CancellationToken ct);

    Task<AccessResult<IReadOnlyList<WorkerCardDto>>> CardsAsync(AccessContext caller, string idOrCode, CancellationToken ct);
    Task<AccessResult<WorkerCardDto>> IssueCardAsync(AccessContext caller, string idOrCode, IssueWorkerCardRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<WorkerCardDto>> DeactivateCardAsync(AccessContext caller, string idOrCode, Guid cardId, DeactivateWorkerCardRequest request, string? traceId, CancellationToken ct);

    /// <summary>Validate (and, unless <paramref name="dryRun"/>, register) the workers in a CSV file.</summary>
    Task<AccessResult<WorkerImportResultDto>> ImportAsync(AccessContext caller, Guid factoryId, string csv, bool dryRun, string? traceId, CancellationToken ct);

    /// <summary>The workers matching <paramref name="query"/> (paging ignored) as CSV, with the columns the caller may see.</summary>
    Task<AccessResult<WorkerExport>> ExportAsync(AccessContext caller, WorkerListQuery query, string? traceId, CancellationToken ct);
}

/// <summary>Departments and production floors of a factory (reference data for worker records).</summary>
public interface IOrganizationService
{
    Task<AccessResult<IReadOnlyList<OrgUnitDto>>> DepartmentsAsync(AccessContext caller, Guid factoryId, CancellationToken ct);
    Task<AccessResult<OrgUnitDto>> CreateDepartmentAsync(AccessContext caller, Guid factoryId, CreateOrgUnitRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<OrgUnitDto>> UpdateDepartmentAsync(AccessContext caller, Guid factoryId, Guid departmentId, UpdateOrgUnitRequest request, string? traceId, CancellationToken ct);

    Task<AccessResult<IReadOnlyList<OrgUnitDto>>> FloorsAsync(AccessContext caller, Guid factoryId, CancellationToken ct);
    Task<AccessResult<OrgUnitDto>> CreateFloorAsync(AccessContext caller, Guid factoryId, CreateOrgUnitRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<OrgUnitDto>> UpdateFloorAsync(AccessContext caller, Guid factoryId, Guid floorId, UpdateOrgUnitRequest request, string? traceId, CancellationToken ct);
}

/// <summary>Where worker profile photographs are kept (outside the database).</summary>
public interface IWorkerPhotoStore
{
    long MaxBytes { get; }
    Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}
