using FactoryBrain.Application.Production;

namespace FactoryBrain.Application.Dtos.Production;

// Wire contracts for /api/production/**. Dates without a time are plant-local
// production dates (yyyy-MM-dd); instants are UTC. Quantities are pieces,
// times are minutes, rates are pieces per hour, SMV is minutes per piece.

public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total, int TotalPages);

// ── Setup (master data) ─────────────────────────────────────────────────────

public sealed record ProductionLineDto(string Id, string Name, int Workstations, bool IsActive, uint Version, DateTime UpdatedAt);

public sealed record ProductionLineWrite(string? Id, string? Name, int? Workstations, bool? IsActive, uint? ExpectedVersion);

public sealed record ShiftDto(
    string Code, string Name, string Start, string End, string? BreakStart, int BreakMinutes,
    IReadOnlyList<string> Weekdays, bool Overnight, double WorkingMinutes, bool IsActive, uint Version);

/// <param name="Start">Local time <c>HH:mm</c>.</param>
/// <param name="Weekdays">Day names, e.g. <c>["sat","sun","mon","tue","wed","thu"]</c>.</param>
public sealed record ShiftWrite(
    string? Code, string? Name, string? Start, string? End, string? BreakStart, int? BreakMinutes,
    IReadOnlyList<string>? Weekdays, bool? IsActive, uint? ExpectedVersion);

public sealed record StyleOperationDto(Guid Id, int Sequence, string Name, double Smv, string? MachineType, IReadOnlyList<int> Predecessors);

public sealed record StyleDto(
    Guid Id, string Code, string Name, string? ProductType, double? ApprovedSmv, double? EffectiveSmv,
    /// <summary><c>approved</c>, <c>operation_bulletin</c> or <c>missing</c>.</summary>
    string SmvBasis, double OperationSmvTotal, bool IsActive, uint Version, IReadOnlyList<StyleOperationDto> Operations);

public sealed record StyleOperationWrite(Guid? Id, int Sequence, string? Name, double Smv, string? MachineType, IReadOnlyList<int>? Predecessors);

public sealed record StyleWrite(
    string? Code, string? Name, string? ProductType, double? ApprovedSmv, bool? ClearApprovedSmv, bool? IsActive,
    IReadOnlyList<StyleOperationWrite>? Operations, uint? ExpectedVersion);

public sealed record DataSourceStatus(string Source, string Status, string Description, int RecordsLast24h);

public sealed record ProductionConfigDto(
    string TimeZone, string UtcOffsetNow, bool RequireSeparateApprover, int MaxBackfillDays, int MaxIntervalHours,
    int RateLookbackDays, double RiskSlackPct, int ObservationMaxAgeDays, string? ConfigurationError,
    int ActiveLines, int ActiveShifts, int ActiveStyles, int OpenOrders,
    IReadOnlyList<DataSourceStatus> DataSources, IReadOnlyDictionary<string, string> Definitions);

// ── Orders ──────────────────────────────────────────────────────────────────

public sealed record OrderWrite(
    string? OrderNumber, Guid? StyleId, string? Buyer, int? Quantity, string? DueDate, string? Status, string? Notes, uint? ExpectedVersion);

public sealed record OrderListItem(
    Guid Id, string OrderNumber, Guid StyleId, string StyleCode, string StyleName, string? Buyer, int Quantity, string DueDate,
    string Status, int ProducedGood, double ProgressPct, int ActivePlans, string RiskLevel, DateTime? ProjectedCompletion, uint Version);

public sealed record OrderDto(
    Guid Id, string OrderNumber, Guid StyleId, string? Buyer, int Quantity, string DueDate, DateTime DueAt, string Status, string? Notes,
    DateTime CreatedAt, string CreatedByEmail, uint Version);

public sealed record StageProgressDto(string Stage, int Good, int Reject, int Rework, int Records);

public sealed record WipBufferDto(string Key, string Label, string From, string To, int? Pieces, string State);

public sealed record LineProgressDto(string LineId, int Good, int Rework, int Reject, DateTime? LastIntervalEnd);

public sealed record RiskAssessmentDto(
    Guid OrderId, string OrderNumber, DateTime EvaluatedAt, DateTime DueAt, IReadOnlyList<string> LineIds,
    string Level, int RemainingQuantity, double RemainingScheduledMinutes,
    double? RequiredRatePerLineHour, double? ObservedRatePerLineHour, double? ObservedRateLow, double? ObservedRateHigh,
    double? PlannedRatePerHour, DateTime? ProjectedCompletion, DateTime? ProjectedCompletionEarliest, DateTime? ProjectedCompletionLatest,
    DateTime? ScheduleEnd, int? ProjectedShortfall, double ObservedLineHours, string Method,
    IReadOnlyList<string> Observations, IReadOnlyList<string> Assumptions, IReadOnlyList<string> Limitations, IReadOnlyList<string> MissingInputs,
    IReadOnlyList<Recommendation> Recommendations);

public sealed record OrderDetail(
    OrderDto Order, StyleDto Style, IReadOnlyList<PlanListItem> Plans, IReadOnlyList<StageProgressDto> Stages,
    IReadOnlyList<WipBufferDto> Wip, IReadOnlyList<LineProgressDto> ByLine, RiskAssessmentDto Risk);

// ── Plans ───────────────────────────────────────────────────────────────────

public sealed record PlanValues(
    string LineId, string StartDate, string EndDate, int PlannedQuantity, int Operators, double TargetEfficiencyPct,
    IReadOnlyList<string> ShiftCodes, int OvertimeMinutesPerDay, string? Notes);

/// <summary>Create (OrderId required) or edit a plan; on an active plan this requests a change that needs approval.</summary>
public sealed record PlanWrite(
    Guid? OrderId, string? LineId, string? StartDate, string? EndDate, int? PlannedQuantity, int? Operators, double? TargetEfficiencyPct,
    IReadOnlyList<string>? ShiftCodes, int? OvertimeMinutesPerDay, string? Notes, string? Reason, uint? ExpectedVersion);

public sealed record PlanTransitionRequest(string? Reason, uint? ExpectedVersion);

public sealed record DailyTargetDto(string Date, double ScheduledMinutes, int Target);

public sealed record PlanTargetsDto(
    double? Smv, string SmvBasis, double? HourlyTarget, double ScheduledMinutes, int WorkingDays, double? CapacityAtTarget,
    string PlannedMethod, IReadOnlyList<DailyTargetDto> Daily, IReadOnlyList<string> Warnings, string Formula);

public sealed record ApprovalRefDto(string Id, string Status, string RiskTier, DateTime CreatedAt, DateTime? DecidedAt, string? DecidedByEmail, string? DecisionReason);

public sealed record FieldDiffDto(string Field, string? From, string? To);

public sealed record PlanChangeDto(
    Guid Id, string Kind, string Status, PlanValues? Previous, PlanValues Proposed, IReadOnlyList<FieldDiffDto> Diff,
    string? Reason, ApprovalRefDto? Approval, DateTime RequestedAt, string RequestedByEmail, DateTime? DecidedAt, string? DecidedByEmail);

public sealed record PlanProgressDto(
    DateTime EvaluatedAt, DateTime? ComparedThrough, int PlannedToDate, int ActualToDate, int Variance, double? AchievementPct,
    int ActualTotal, int RemainingQuantity);

public sealed record PlanListItem(
    Guid Id, string Reference, Guid OrderId, string OrderNumber, string LineId, string LineName, string StartDate, string EndDate,
    int PlannedQuantity, int Operators, double TargetEfficiencyPct, string Status, int Revision, bool HasPendingChange, DateTime UpdatedAt);

public sealed record PlanDetail(
    PlanListItem Summary, PlanValues Values, PlanTargetsDto Targets, PlanProgressDto Progress, ApprovalRefDto? ActivationApproval,
    IReadOnlyList<PlanChangeDto> Changes, string? SubmittedByEmail, DateTime? SubmittedAt, string? ActivatedByEmail, DateTime? ActivatedAt,
    string? ClosedReason, string CreatedByEmail, DateTime CreatedAt, uint Version);

/// <summary>A workflow action's result: what happened and the plan afterwards.</summary>
public sealed record PlanActionResponse(string Outcome, string Message, PlanDetail Plan);

// ── Output ──────────────────────────────────────────────────────────────────

public sealed record OutputWrite(
    string? LineId, Guid? OrderId, string? Stage, DateTime? IntervalStart, DateTime? IntervalEnd,
    int? Quantity, int? ReworkQuantity, int? RejectQuantity, int? OperatorsPresent, double? DowntimeMinutes, string? DowntimeReason,
    string? Source, string? SourceRef, string? Notes);

public sealed record OutputCorrection(
    DateTime? IntervalStart, DateTime? IntervalEnd, int? Quantity, int? ReworkQuantity, int? RejectQuantity, int? OperatorsPresent,
    double? DowntimeMinutes, string? DowntimeReason, string? Notes, string? Reason);

public sealed record OutputEventDto(
    Guid Id, string LineId, Guid OrderId, string OrderNumber, string Stage, DateTime IntervalStart, DateTime IntervalEnd,
    string ProductionDate, string? ShiftCode, int Quantity, int ReworkQuantity, int RejectQuantity, int? OperatorsPresent,
    double? DowntimeMinutes, string? DowntimeReason, string Source, string? SourceRef, string? Notes,
    bool IsSuperseded, Guid? SupersededById, Guid? CorrectionOfId, string? CorrectionReason,
    DateTime RecordedAt, string RecordedByEmail, bool Late);

public sealed record OutputImportRow(
    string? SourceRef, string? LineId, string? OrderNumber, string? Stage, DateTime? IntervalStart, DateTime? IntervalEnd,
    int? Quantity, int? ReworkQuantity, int? RejectQuantity, int? OperatorsPresent, double? DowntimeMinutes, string? DowntimeReason, string? Notes);

public sealed record OutputImportRequest(string? FileName, IReadOnlyList<OutputImportRow>? Rows);

public sealed record ImportRowOutcome(int Row, string? SourceRef, string Outcome, Guid? EventId, IReadOnlyList<string>? Errors);

public sealed record OutputImportResult(int Received, int Created, int Duplicates, int Invalid, IReadOnlyList<ImportRowOutcome> Rows);

// ── Summary, line detail ────────────────────────────────────────────────────

public sealed record SummaryTotals(
    int Actual, int Planned, int Variance, double? AchievementPct,
    double? EfficiencyPct, double? EfficiencyExcludingDowntimePct, double EarnedMinutes, double AvailableMinutes,
    double DowntimeMinutes, int Rework, int Reject, int Records,
    double ScheduledPlanLineMinutes, double CalendarLineMinutes, double? CapacityUtilizationPct, int UnplannedActual);

public sealed record SummaryRow(
    string Key, string Label, int Actual, int Planned, int Variance, double? AchievementPct, double? EfficiencyPct,
    double DowntimeMinutes, int Rework, int Reject, int Records);

public sealed record DailyPoint(string Date, int Actual, int Planned, double? AchievementPct, double? EfficiencyPct, double DowntimeMinutes);

public sealed record LineWipDto(string LineId, IReadOnlyList<WipBufferDto> Buffers);

public sealed record DelayedOrderDto(Guid OrderId, string OrderNumber, string DueDate, string Level, int RemainingQuantity,
    double? RequiredRatePerLineHour, double? ObservedRatePerLineHour, DateTime? ProjectedCompletion);

public sealed record LineBottleneckDto(string LineId, Guid StyleId, string StyleCode, string Status, string? Operation, double? CapacityPerHour, string? Evidence);

public sealed record DataQualityDto(
    int OutputRecords, int MissingOperators, int MissingSmv, int UnplannedRecords, int LateRecords, int CorrectedRecords,
    IReadOnlyList<string> Notes);

public sealed record ProductionSummary(
    string FromDate, string ToDate, string TimeZone, DateTime FromUtc, DateTime ToUtc, DateTime EvaluatedAt, DateTime ComparedThrough,
    SummaryTotals Totals, IReadOnlyList<SummaryRow> ByLine, IReadOnlyList<SummaryRow> ByOrder, IReadOnlyList<SummaryRow> ByShift,
    IReadOnlyList<DailyPoint> Daily, IReadOnlyList<LineWipDto> Wip, IReadOnlyList<DelayedOrderDto> OrderRisks,
    IReadOnlyList<LineBottleneckDto> Bottlenecks, DataQualityDto Quality, IReadOnlyDictionary<string, int> Sources,
    IReadOnlyDictionary<string, string> Definitions);

public sealed record TimelinePoint(DateTime Start, DateTime End, string Label, string? ShiftCode, int Actual, double Planned,
    double? EfficiencyPct, double DowntimeMinutes, int Records);

public sealed record StyleAnalysisDto(Guid StyleId, string StyleCode, string StyleName, BottleneckResult Bottleneck, LayoutDto? Layout);

public sealed record LineDetail(
    ProductionLineDto Line, string Date, string? Shift, string TimeZone, DateTime FromUtc, DateTime ToUtc, DateTime ComparedThrough,
    SummaryTotals Totals, IReadOnlyList<TimelinePoint> Timeline, IReadOnlyList<PlanListItem> Plans, IReadOnlyList<OutputEventDto> Events,
    IReadOnlyList<LineWipDto> Wip, IReadOnlyList<StyleAnalysisDto> Styles, IReadOnlyDictionary<string, string> Definitions);

// ── Time studies, layouts, balancing ────────────────────────────────────────

public sealed record ObservationWrite(
    string? LineId, Guid? StyleId, Guid? OperationId, DateTime? ObservedAt, double? CycleSeconds, int? SampleCount,
    int? OperatorsAssigned, int? QueueBefore, string? Notes);

public sealed record ObservationDto(
    Guid Id, string LineId, Guid StyleId, Guid OperationId, int OperationSequence, string OperationName, DateTime ObservedAt,
    double CycleSeconds, int SampleCount, int OperatorsAssigned, int? QueueBefore, string? Notes, string Source,
    DateTime RecordedAt, string RecordedByEmail);

public sealed record LayoutStationWrite(int StationNo, IReadOnlyList<Guid>? OperationIds);

public sealed record LayoutWrite(string? LineId, Guid? StyleId, IReadOnlyList<LayoutStationWrite>? Stations);

public sealed record LayoutStationDto(int StationNo, IReadOnlyList<Guid> OperationIds, IReadOnlyList<string> Operations, string? MachineType, double LoadMinutes, double UtilizationPct);

public sealed record LayoutDto(
    Guid Id, string LineId, Guid StyleId, string Origin, Guid? ProposalId, DateTime CreatedAt, string CreatedByEmail,
    IReadOnlyList<LayoutStationDto> Stations, WorkloadMetrics Metrics, IReadOnlyList<LayoutIssue> Issues);

public sealed record BalanceRequest(string? LineId, Guid? StyleId, int? Workstations, double? TargetOutputPerHour);

public sealed record ComparisonDto(
    WorkloadMetrics? Current, WorkloadMetrics Proposed, double? CycleTimeChangePct, double? CapacityChangePct,
    double? BalanceEfficiencyChangePts, string? CurrentBottleneck, string ProposedBottleneck);

public sealed record BalanceProposalListItem(
    Guid Id, string Reference, string LineId, Guid StyleId, string StyleCode, int Workstations, string Status,
    double? CycleTimeMinutes, double? CapacityPerHour, double? BalanceEfficiencyPct, DateTime CreatedAt, string CreatedByEmail);

public sealed record BalanceProposalDetail(
    Guid Id, string Reference, string LineId, string LineName, Guid StyleId, string StyleCode, string StyleName,
    int Workstations, double? TargetOutputPerHour, string Status, string? StatusReason, BalanceProposalResult Result,
    IReadOnlyList<LayoutStationDto> ProposedStations, LayoutDto? Current, bool InputsChanged, bool BaselineChanged,
    ComparisonDto? Comparison, ApprovalRefDto? Approval, DateTime CreatedAt, string CreatedByEmail,
    string? SubmittedByEmail, DateTime? SubmittedAt, string? AppliedByEmail, DateTime? AppliedAt, uint Version);

/// <summary>Result of POST /api/production/balance/analyze. A proposal is stored only when the analysis is feasible.</summary>
public sealed record BalanceAnalysisResponse(BalanceProposalResult Result, BalanceProposalDetail? Proposal, LayoutDto? Current, IReadOnlyList<string> MissingInputs);

public sealed record ProposalActionResponse(string Outcome, string Message, BalanceProposalDetail Proposal);
