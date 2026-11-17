using BunonBrain.Application.Access;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Dtos.Planning;

namespace BunonBrain.Application.Abstractions.Interfaces;

// Production planning & target management. Every method takes the caller's
// AccessContext (built from the validated token) and enforces factory and
// line scope itself; the controller only checks the permission.

public interface IPlanningSetupService
{
    Task<AccessResult<IReadOnlyList<FactoryRefDto>>> FactoriesAsync(AccessContext caller, CancellationToken ct);
    Task<AccessResult<PlanningContextDto>> ContextAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);
    Task<AccessResult<CapacitySettingsDto>> GetCapacitySettingsAsync(AccessContext caller, string lineId, CancellationToken ct);
    Task<AccessResult<CapacitySettingsDto>> SaveCapacitySettingsAsync(AccessContext caller, string lineId, CapacitySettingsWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<IReadOnlyList<OutputPointDto>>> OutputPointsAsync(AccessContext caller, Guid? factoryId, string? lineId, CancellationToken ct);
    Task<AccessResult<OutputPointDto>> AddOutputPointAsync(AccessContext caller, OutputPointWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<OutputPointDto>> RemoveOutputPointAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct);
}

public sealed record AllocationQuery(Guid? FactoryId, Guid? OrderId, string? LineId, string? Status, DateOnly? From, DateOnly? To, int Page, int PageSize);

public interface IAllocationService
{
    Task<AccessResult<PagedResult<AllocationDto>>> ListAsync(AccessContext caller, AllocationQuery query, CancellationToken ct);
    Task<AccessResult<AllocationSummaryDto>> ForOrderAsync(AccessContext caller, Guid orderId, CancellationToken ct);
    Task<AccessResult<AllocationDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<AllocationDetailDto>> CreateAsync(AccessContext caller, AllocationWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<AllocationDetailDto>> UpdateAsync(AccessContext caller, Guid id, AllocationWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<AllocationDetailDto>> SetStatusAsync(AccessContext caller, Guid id, AllocationStatusRequest body, string? traceId, CancellationToken ct);
}

public sealed record TargetQuery(Guid? FactoryId, string? Level, string? LineId, Guid? OrderId, DateOnly? Date, bool IncludeHistory, bool WithPerformance, string? Approval, int Page, int PageSize);

public interface ITargetService
{
    Task<AccessResult<PagedResult<TargetDto>>> ListAsync(AccessContext caller, TargetQuery query, CancellationToken ct);
    Task<AccessResult<TargetDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<TargetDto?>> EffectiveAsync(AccessContext caller, Guid factoryId, string lineId, Guid shiftId, Guid? orderId, DateOnly date, CancellationToken ct);
    Task<AccessResult<TargetDetailDto>> CreateAsync(AccessContext caller, TargetWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<TargetDetailDto>> ReviseAsync(AccessContext caller, Guid id, TargetWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<TargetDetailDto>> DecideAsync(AccessContext caller, Guid id, TargetDecision body, string? traceId, CancellationToken ct);
    Task<AccessResult<TargetDetailDto>> EndAsync(AccessContext caller, Guid id, TargetEnd body, string? traceId, CancellationToken ct);
}

public interface ICapacityService
{
    /// <summary>An estimate for a line and style over a date range (what-if), optionally for a quantity.</summary>
    Task<AccessResult<CapacityEstimateDto>> EstimateAsync(AccessContext caller, string lineId, Guid styleId, DateOnly from, DateOnly to, int quantity, Guid? excludeAllocationId,
        CancellationToken ct);
    Task<AccessResult<CapacityEstimateDto>> ForAllocationAsync(AccessContext caller, Guid allocationId, CancellationToken ct);
    Task<AccessResult<CapacityOverviewDto>> OverviewAsync(AccessContext caller, Guid factoryId, DateOnly from, DateOnly to, CancellationToken ct);
}

public sealed record DailyPlanQuery(Guid? FactoryId, string? Status, DateOnly? From, DateOnly? To, int Page, int PageSize);

public interface IDailyPlanService
{
    Task<AccessResult<PagedResult<DailyPlanListItem>>> ListAsync(AccessContext caller, DailyPlanQuery query, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> GetAsync(AccessContext caller, Guid id, string? source, CancellationToken ct);
    /// <summary>The plan in force for a factory and date: the published one; managers also see the open draft when nothing is published.</summary>
    Task<AccessResult<DailyPlanDto?>> ForDateAsync(AccessContext caller, Guid factoryId, DateOnly date, string? source, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> CreateAsync(AccessContext caller, DailyPlanWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> UpdateNotesAsync(AccessContext caller, Guid id, DailyPlanNotesWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> AddItemAsync(AccessContext caller, Guid id, PlanItemWrite body, uint? expectedVersion, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> UpdateItemAsync(AccessContext caller, Guid id, Guid itemId, PlanItemWrite body, uint? expectedVersion, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> RemoveItemAsync(AccessContext caller, Guid id, Guid itemId, uint? expectedVersion, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> SubmitAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> ApproveAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> ReturnAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> PublishAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct);
    /// <summary>Starts a new revision (a draft copy) of a published plan.</summary>
    Task<AccessResult<DailyPlanDto>> ReviseAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct);
    Task<AccessResult<DailyPlanDto>> CancelAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct);
}

public sealed record MonitorQuery(Guid? FactoryId, DateOnly? Date, string? Source, Guid? DepartmentId, Guid? FloorId, string? LineId);

public interface IPlanningMonitorService
{
    Task<AccessResult<MonitorDto>> MonitorAsync(AccessContext caller, MonitorQuery query, CancellationToken ct);
    Task<AccessResult<OrderProgressDto>> OrderProgressAsync(AccessContext caller, Guid orderId, string? source, CancellationToken ct);
}

public sealed record ExceptionQuery(Guid? FactoryId, string? Status, string? Type, string? Severity, string? LineId, Guid? OrderId, int Page, int PageSize);

public interface IProductionExceptionService
{
    Task<AccessResult<PagedResult<ExceptionDto>>> ListAsync(AccessContext caller, ExceptionQuery query, CancellationToken ct);
    Task<AccessResult<ExceptionDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<ExceptionEvaluationDto>> EvaluateAsync(AccessContext caller, ExceptionEvaluateRequest body, string? traceId, CancellationToken ct);
    Task<AccessResult<ExceptionDto>> AcknowledgeAsync(AccessContext caller, Guid id, ExceptionNote body, string? traceId, CancellationToken ct);
    Task<AccessResult<ExceptionDto>> ResolveAsync(AccessContext caller, Guid id, ExceptionNote body, string? traceId, CancellationToken ct);
}

public interface IPlanningReports
{
    IReadOnlyList<PlanningReportTypeDto> Types();
    Task<AccessResult<ReportDocument>> GenerateAsync(AccessContext caller, string type, Guid factoryId, DateOnly? from, DateOnly? to, string? source, string? lineId, Guid? orderId,
        CancellationToken ct);
    Task<AccessResult<ReportFile>> ExportAsync(AccessContext caller, string type, string format, Guid factoryId, DateOnly? from, DateOnly? to, string? source, string? lineId,
        Guid? orderId, string? traceId, CancellationToken ct);
}
