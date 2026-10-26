using FactoryBrain.Application.Dtos.Production;

namespace FactoryBrain.Application.Abstractions.Interfaces;

public enum ProductionOutcome { Ok, Created, NotFound, Invalid, Conflict, Forbidden }

/// <summary>
/// Result of a production read or write. <see cref="Code"/> is a stable
/// machine code for conflicts and refusals (e.g. <c>version_conflict</c>,
/// <c>approval_pending</c>, <c>same_approver</c>); <see cref="Message"/> is
/// safe to show.
/// </summary>
public sealed record ProductionResult<T>(
    ProductionOutcome Outcome,
    T? Value = default,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Code = null,
    string? Message = null)
{
    public static ProductionResult<T> Ok(T value) => new(ProductionOutcome.Ok, value);
    public static ProductionResult<T> Created(T value) => new(ProductionOutcome.Created, value);
    public static ProductionResult<T> NotFound(string code) => new(ProductionOutcome.NotFound, Code: code);
    public static ProductionResult<T> Invalid(IReadOnlyDictionary<string, string[]> errors) => new(ProductionOutcome.Invalid, Errors: errors);
    public static ProductionResult<T> Invalid(string field, string message) =>
        new(ProductionOutcome.Invalid, Errors: new Dictionary<string, string[]> { [field] = new[] { message } });
    public static ProductionResult<T> Conflict(string code, string message) => new(ProductionOutcome.Conflict, Code: code, Message: message);
    public static ProductionResult<T> Forbidden(string code, string message) => new(ProductionOutcome.Forbidden, Code: code, Message: message);
}

/// <summary>Lines, shifts and styles (with their operation bulletins): plant configuration, Admin-managed.</summary>
public interface IProductionSetupService
{
    Task<ProductionConfigDto> ConfigAsync(CancellationToken ct);
    Task<IReadOnlyList<ProductionLineDto>> ListLinesAsync(bool includeInactive, CancellationToken ct);
    Task<ProductionResult<ProductionLineDto>> CreateLineAsync(ProductionLineWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<ProductionLineDto>> UpdateLineAsync(string id, ProductionLineWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<IReadOnlyList<ShiftDto>> ListShiftsAsync(bool includeInactive, CancellationToken ct);
    Task<ProductionResult<ShiftDto>> CreateShiftAsync(ShiftWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<ShiftDto>> UpdateShiftAsync(string code, ShiftWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<IReadOnlyList<StyleDto>> ListStylesAsync(bool includeInactive, CancellationToken ct);
    Task<StyleDto?> GetStyleAsync(Guid id, CancellationToken ct);
    Task<ProductionResult<StyleDto>> CreateStyleAsync(StyleWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<StyleDto>> UpdateStyleAsync(Guid id, StyleWrite body, AuditActor actor, string? traceId, CancellationToken ct);
}

/// <param name="IncludeRisk">Compute each row's delivery risk (several queries per order); off for pickers.</param>
public sealed record OrderQuery(string? Q, string? Status, Guid? StyleId, int Page, int PageSize, string? Sort, bool IncludeRisk = true);

public interface IProductionOrderService
{
    Task<ProductionResult<Paged<OrderListItem>>> ListAsync(OrderQuery query, CancellationToken ct);
    Task<OrderDetail?> GetAsync(Guid id, CancellationToken ct);
    Task<ProductionResult<OrderDto>> CreateAsync(OrderWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<OrderDto>> UpdateAsync(Guid id, OrderWrite body, AuditActor actor, string? traceId, CancellationToken ct);
}

public sealed record PlanQuery(string? Status, string? LineId, Guid? OrderId, DateOnly? From, DateOnly? To, int Page, int PageSize);

/// <summary>
/// Production plans. Drafts are edited freely (audited); making a plan live,
/// and changing a live plan, go through the existing approval queue and then
/// an explicit activate / apply step by an authorised user.
/// </summary>
public interface IProductionPlanService
{
    Task<ProductionResult<Paged<PlanListItem>>> ListAsync(PlanQuery query, CancellationToken ct);
    Task<PlanDetail?> GetAsync(Guid id, CancellationToken ct);
    Task<ProductionResult<PlanDetail>> CreateAsync(PlanWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    /// <summary>Edits a draft directly, or records a pending change (with an approval request) on an active plan.</summary>
    Task<ProductionResult<PlanActionResponse>> UpdateAsync(Guid id, PlanWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<PlanActionResponse>> SubmitAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct);
    /// <summary>Acts on the activation approval's decision: approved → Active; rejected → back to Draft.</summary>
    Task<ProductionResult<PlanActionResponse>> ActivateAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct);
    /// <summary>Acts on a pending change's approval: approved → applied; rejected → closed.</summary>
    Task<ProductionResult<PlanActionResponse>> ResolveChangeAsync(Guid id, Guid changeId, AuditActor actor, string? traceId, CancellationToken ct);
    /// <summary>Takes a submitted plan back to draft (its approval stays in the queue but no longer affects the plan).</summary>
    Task<ProductionResult<PlanActionResponse>> WithdrawAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct);
    /// <summary>Withdraws a pending change to a live plan (closed as superseded; the live plan is unchanged).</summary>
    Task<ProductionResult<PlanActionResponse>> WithdrawChangeAsync(Guid id, Guid changeId, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<PlanActionResponse>> CompleteAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<PlanActionResponse>> CancelAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct);
}

public sealed record OutputQuery(
    DateOnly? From, DateOnly? To, string? LineId, Guid? OrderId, string? Shift, string? Stage, string? Source,
    bool IncludeSuperseded, int Page, int PageSize, string? Sort);

public interface IProductionOutputService
{
    Task<ProductionResult<Paged<OutputEventDto>>> ListAsync(OutputQuery query, CancellationToken ct);
    Task<ProductionResult<OutputEventDto>> RecordAsync(OutputWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<OutputEventDto>> CorrectAsync(Guid id, OutputCorrection body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<OutputImportResult>> ImportAsync(OutputImportRequest body, AuditActor actor, string? traceId, CancellationToken ct);
}

public sealed record SummaryQuery(DateOnly? From, DateOnly? To, string? LineId, Guid? OrderId, string? Shift);

public interface IProductionAnalysisService
{
    Task<ProductionResult<ProductionSummary>> SummaryAsync(SummaryQuery query, CancellationToken ct);
    Task<ProductionResult<LineDetail>> LineDetailAsync(string lineId, DateOnly? date, string? shift, CancellationToken ct);
    Task<ProductionResult<RiskAssessmentDto>> OrderRiskAsync(Guid orderId, CancellationToken ct);

    Task<IReadOnlyList<ObservationDto>> ListObservationsAsync(string? lineId, Guid? styleId, int limit, CancellationToken ct);
    Task<ProductionResult<ObservationDto>> RecordObservationAsync(ObservationWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<LayoutDto?>> CurrentLayoutAsync(string lineId, Guid styleId, CancellationToken ct);
    Task<ProductionResult<LayoutDto>> RecordLayoutAsync(LayoutWrite body, AuditActor actor, string? traceId, CancellationToken ct);

    Task<ProductionResult<BalanceAnalysisResponse>> AnalyzeBalanceAsync(BalanceRequest body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<ProductionResult<Paged<BalanceProposalListItem>>> ListProposalsAsync(string? lineId, Guid? styleId, string? status, int page, int pageSize, CancellationToken ct);
    Task<BalanceProposalDetail?> GetProposalAsync(Guid id, CancellationToken ct);
    Task<ProductionResult<ProposalActionResponse>> SubmitProposalAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct);
    /// <summary>Acts on the proposal's approval: approved → becomes the current layout; rejected → closed.</summary>
    Task<ProductionResult<ProposalActionResponse>> ApplyProposalAsync(Guid id, AuditActor actor, string? traceId, CancellationToken ct);
}
