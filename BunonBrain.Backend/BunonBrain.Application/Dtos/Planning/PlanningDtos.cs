using BunonBrain.Application.Planning;

namespace BunonBrain.Application.Dtos.Planning;

// Wire contracts for /api/planning/** and the order fields of /api/production/orders.
// Dates without a time are factory-local operational dates (yyyy-MM-dd); instants are UTC.
// Quantities are pieces. Status and kind names are upper case (orders keep the production module's snake_case).

// ── Context & setup ─────────────────────────────────────────────────────────

public sealed record PlanningShiftDto(Guid Id, string Code, string Name, string Start, string End, bool Overnight, int ProductiveMinutes, IReadOnlyList<string> Weekdays);

public sealed record PlanningLineDto(
    string Id, string Name, bool IsActive, int Workstations, Guid? FloorId, string? FloorName, Guid? DepartmentId, string? DepartmentName,
    double? EfficiencyPct, int? PlannedOperators, int OutputPoints, bool CanManage);

public sealed record PlanningContextDto(
    Guid FactoryId, string FactoryCode, string FactoryName, string TimeZone, string Today, IReadOnlyList<PlanningShiftDto> Shifts,
    IReadOnlyList<PlanningLineDto> Lines, IReadOnlyList<PlanningFloorDto> Floors, IReadOnlyList<PlanningDepartmentDto> Departments,
    PlanningCapabilitiesDto Capabilities, PlanningLimitsDto Limits);

public sealed record PlanningFloorDto(Guid Id, string Code, string Name);
public sealed record PlanningDepartmentDto(Guid Id, string Code, string Name);

/// <summary>What the caller may do (a convenience for the UI: the API enforces every one of these).</summary>
public sealed record PlanningCapabilitiesDto(
    bool ManageOrders, bool ManageStyles, bool ManageAllocations, bool ManageTargets, bool ManagePlans, bool ApprovePlans, bool PublishPlans,
    bool ReviewExceptions, bool Setup, bool ExportReports, bool SeesDrafts, bool LineScoped);

public sealed record PlanningLimitsDto(int OutputPointsPerLine, double BehindTolerancePct, int InterruptionMinutes, bool RequireSeparateApprover);

public sealed record FactoryRefDto(Guid Id, string Code, string Name);

public sealed record CapacitySettingsDto(string LineId, Guid FactoryId, double? EfficiencyPct, int? PlannedOperators, string? Notes, DateTimeOffset? UpdatedAt, string? UpdatedByEmail, uint Version);
public sealed record CapacitySettingsWrite(double? EfficiencyPct, int? PlannedOperators, string? Notes, bool? ClearEfficiency, bool? ClearOperators, uint? ExpectedVersion);

public sealed record OutputPointDto(
    Guid Id, Guid FactoryId, string LineId, string LineName, Guid DeviceId, string DeviceCode, string DeviceName, bool Simulated, string ConnectionStatus,
    string? CountingMode, string? Note, bool IsActive, DateTimeOffset CreatedAt, string CreatedByEmail);
public sealed record OutputPointWrite(string? LineId, Guid? DeviceId, string? Note);

// ── Orders ──────────────────────────────────────────────────────────────────

public sealed record OrderStatusChangeDto(Guid Id, string? From, string To, string? Reason, DateTimeOffset ChangedAt, string ChangedByEmail);
public sealed record OrderStatusRequest(string? Status, string? Reason, uint? ExpectedVersion);
public sealed record OrderTransitionsDto(string Current, IReadOnlyList<string> Allowed, IReadOnlyList<OrderStatusChangeDto> History);

// ── Allocations ─────────────────────────────────────────────────────────────

public sealed record AllocationDto(
    Guid Id, Guid OrderId, string OrderNumber, Guid FactoryId, string LineId, string LineName, int Quantity, string PlannedStart, string PlannedEnd,
    string Priority, string Status, Guid? SupervisorUserId, string? SupervisorEmail, bool OverAllocated, string? OverrideReason, string? OverrideByEmail, string? Notes,
    DateTimeOffset CreatedAt, string CreatedByEmail, uint Version);

public sealed record AllocationChangeDto(Guid Id, string Kind, string? PreviousValues, string NewValues, string? Reason, DateTimeOffset ChangedAt, string ChangedByEmail);

public sealed record AllocationWrite(
    Guid? OrderId, string? LineId, int? Quantity, string? PlannedStart, string? PlannedEnd, string? Priority, Guid? SupervisorUserId, string? Notes,
    /// <summary>Saves although the order's allocations then exceed its quantity. Needs the allocate permission at factory level and a reason.</summary>
    bool? AllowOverAllocation, string? OverrideReason, string? Reason, uint? ExpectedVersion);

public sealed record AllocationStatusRequest(string? Status, string? Reason, uint? ExpectedVersion);

public sealed record AllocationSummaryDto(
    Guid OrderId, string OrderNumber, int OrderQuantity, int Allocated, int Remaining, int Excess, IReadOnlyList<AllocationDto> Allocations);

public sealed record AllocationDetailDto(AllocationDto Allocation, IReadOnlyList<AllocationChangeDto> History, IReadOnlyList<string> Warnings);

// ── Targets ─────────────────────────────────────────────────────────────────

public sealed record TargetDto(
    Guid Id, Guid TargetKey, int Revision, Guid? SupersedesId, string Level, string Period, Guid FactoryId, Guid? DepartmentId, string? DepartmentName,
    Guid? FloorId, string? FloorName, string? LineId, string? LineName, Guid? ShiftId, string? ShiftName, Guid? WorkstationId, string? WorkstationCode,
    Guid? MachineId, string? MachineCode, Guid? OrderId, string? OrderNumber, Guid? AllocationId, int Quantity, string EffectiveFrom, string? EffectiveTo,
    string ApprovalStatus, bool IsSuperseded, bool InForceToday, string? Notes, string? ChangeReason, DateTimeOffset CreatedAt, string CreatedByEmail,
    DateTimeOffset? DecidedAt, string? DecidedByEmail, string? DecisionNote, uint Version, TargetPerformanceDto? Performance);

/// <summary>Output against a target on one date. <c>Basis</c> says what the actual comes from.</summary>
public sealed record TargetPerformanceDto(string Date, int? Actual, double? AchievementPct, string Basis, string Note);

public sealed record TargetRevisionDto(
    Guid Id, Guid TargetId, int Revision, string Action, int? PreviousQuantity, int NewQuantity, string? PreviousEffectiveFrom, string NewEffectiveFrom,
    string? Reason, DateTimeOffset At, string ByEmail);

public sealed record TargetDetailDto(TargetDto Target, IReadOnlyList<TargetDto> Versions, IReadOnlyList<TargetRevisionDto> Revisions);

public sealed record TargetWrite(
    Guid? FactoryId, string? Level, string? Period, Guid? DepartmentId, Guid? FloorId, string? LineId, Guid? ShiftId, Guid? WorkstationId, Guid? MachineId,
    Guid? OrderId, Guid? AllocationId, int? Quantity, string? EffectiveFrom, string? EffectiveTo, string? Notes, string? Reason, uint? ExpectedVersion);

public sealed record TargetDecision(bool? Approve, string? Note, uint? ExpectedVersion);
public sealed record TargetEnd(string? EndDate, string? Reason, uint? ExpectedVersion);

// ── Capacity ────────────────────────────────────────────────────────────────

public sealed record CapacityEstimateDto(
    string Label, string LineId, string LineName, string? StyleCode, string From, string To, CapacityResult Result, DateTimeOffset GeneratedAt,
    bool IsEstimate, string Disclaimer);

public sealed record LineCapacityRow(
    string LineId, string LineName, bool IsActive, int Allocations, double AvailableHours, double? DemandMinutes, double? CapacityMinutes, double? UtilizationPct,
    string Status, IReadOnlyList<CapacityMissing> Missing, IReadOnlyList<CapacityEstimateDto> Estimates, IReadOnlyList<string> Notes);

public sealed record CapacityOverviewDto(
    Guid FactoryId, string From, string To, IReadOnlyList<LineCapacityRow> Lines, IReadOnlyList<string> Warnings, string Disclaimer);

// ── Daily plans ─────────────────────────────────────────────────────────────

public sealed record PlanItemWrite(Guid? OrderId, Guid? AllocationId, string? LineId, Guid? ShiftId, int? TargetQuantity, bool? UseConfiguredTarget, Guid? SupervisorUserId, string? Notes);

public sealed record DailyPlanWrite(Guid? FactoryId, string? PlanDate, string? Notes, Guid? CopyFromPlanId);
public sealed record DailyPlanNotesWrite(string? Notes, uint? ExpectedVersion);
public sealed record DailyPlanTransition(string? Reason, uint? ExpectedVersion);

public sealed record PlanItemDto(
    Guid Id, Guid OrderId, string OrderNumber, string? Buyer, string? StyleCode, Guid? AllocationId, string LineId, string LineName, Guid ShiftId, string ShiftName,
    int TargetQuantity, string TargetSource, Guid? TargetId, double? HourlyTarget, Guid? SupervisorUserId, string? Notes, PlanItemActualDto? Actual,
    IReadOnlyList<string> Warnings);

/// <summary>A plan item's output against its target. <c>Actual</c> is null when no evidence exists (never shown as zero).</summary>
public sealed record PlanItemActualDto(
    int? Actual, int? Remaining, double? AchievementPct, int? ExpectedByNow, int? VarianceToExpected, string State, string Basis, bool Verified, double? HourlyRate,
    string Source, string Note);

public sealed record DailyPlanEventDto(Guid Id, string Action, string? Details, string? Reason, DateTimeOffset At, string ByEmail);

public sealed record DailyPlanListItem(
    Guid Id, string Reference, Guid FactoryId, string PlanDate, int RevisionNo, string Status, int Items, int TotalTarget, DateTimeOffset UpdatedAt, string CreatedByEmail,
    DateTimeOffset? PublishedAt, string? PublishedByEmail);

public sealed record DailyPlanDto(
    Guid Id, string Reference, Guid FactoryId, string FactoryName, string PlanDate, int RevisionNo, string Status, Guid? BasedOnPlanId, string? RevisionReason, string? Notes,
    DateTimeOffset? SubmittedAt, string? SubmittedByEmail, DateTimeOffset? ApprovedAt, string? ApprovedByEmail, DateTimeOffset? PublishedAt, string? PublishedByEmail,
    DateTimeOffset? ClosedAt, string? ClosedReason, DateTimeOffset CreatedAt, string CreatedByEmail, DateTimeOffset UpdatedAt, uint Version,
    IReadOnlyList<PlanItemDto> Items, IReadOnlyList<DailyPlanEventDto> Events, IReadOnlyList<string> Warnings, IReadOnlyList<string> AllowedActions);

// ── Monitoring ──────────────────────────────────────────────────────────────

public sealed record MonitorOrderRow(
    Guid OrderId, string OrderNumber, string? Buyer, string? StyleCode, string Status, string Priority, int Quantity, string DeliveryDeadline, int? DaysToDeadline,
    int Allocated, int? Produced, double? CompletionPct, string ProducedBasis, string ForecastStatus, string? ProjectedCompletion, string ForecastNote);

public sealed record MonitorItemRow(
    Guid ItemId, Guid OrderId, string OrderNumber, string LineId, string LineName, Guid? FloorId, string? FloorName, Guid? DepartmentId, string? DepartmentName,
    Guid ShiftId, string ShiftName, int Target, PlanItemActualDto Actual, double? HourlyTarget);

public sealed record MonitorLineRow(
    string LineId, string LineName, Guid? FloorId, string? FloorName, Guid? DepartmentId, string? DepartmentName, int Target, int? Actual, double? AchievementPct,
    string Basis, bool OutputCounterConfigured, string State, IReadOnlyList<Guid> ItemIds);

public sealed record MonitorShiftRow(Guid ShiftId, string ShiftName, int Target, int? Actual, double? AchievementPct, string State, int Items);

public sealed record MonitorHourPoint(string Hour, string Label, int Pieces);

public sealed record MonitorSummary(
    int ActiveOrders, int OrdersDelayed, int OrdersDueSoon, int TargetToday, int? ActualToday, double? AchievementPct, int? RemainingToday,
    int OpenExceptions, int CapacityWarnings, int LinesPlanned, int LinesWithoutOutputCounter);

public sealed record MonitorDto(
    Guid FactoryId, string FactoryName, string Date, string TimeZone, string Source, DateTimeOffset GeneratedAt, MonitorSummary Summary,
    string? PlanStatus, string? PlanReference, int? PlanRevision, IReadOnlyList<MonitorOrderRow> Orders, IReadOnlyList<MonitorOrderRow> UpcomingDeadlines,
    IReadOnlyList<MonitorItemRow> Items, IReadOnlyList<MonitorLineRow> Lines, IReadOnlyList<MonitorShiftRow> Shifts, IReadOnlyList<MonitorHourPoint> Hourly,
    IReadOnlyList<ExceptionDto> Exceptions, IReadOnlyList<AllocationDto> Allocations, IReadOnlyList<string> Warnings, IReadOnlyList<string> Definitions);

public sealed record OrderProgressDto(
    MonitorOrderRow Order, IReadOnlyList<AllocationDto> Allocations, IReadOnlyList<OrderDayRow> Days, OrderForecast Forecast, IReadOnlyList<string> Warnings);

public sealed record OrderDayRow(string Date, int Target, int? Actual, string Basis, double? AchievementPct);

// ── Exceptions ──────────────────────────────────────────────────────────────

public sealed record ExceptionDto(
    Guid Id, Guid FactoryId, string Type, string TypeLabel, string Severity, string Status, Guid? OrderId, string? OrderNumber, Guid? AllocationId, Guid? PlanItemId,
    string? LineId, string? LineName, Guid? ShiftId, string? ProductionDate, string Title, string Reason, IReadOnlyList<Fact> Data, string SuggestedInvestigation,
    string SourceType, DateTimeOffset DetectedAt, DateTimeOffset LastDetectedAt, int Occurrences, DateTimeOffset? AcknowledgedAt, string? AcknowledgedByEmail,
    DateTimeOffset? ResolvedAt, string? ResolvedByEmail, string? ResolutionNote, bool AutoResolved, uint Version);

public sealed record ExceptionNote(string? Note, uint? ExpectedVersion);
public sealed record ExceptionEvaluateRequest(Guid? FactoryId, string? Source);
public sealed record ExceptionEvaluationDto(Guid FactoryId, string Source, int Checked, int Raised, int Refreshed, int AutoResolved, IReadOnlyList<string> Notes, DateTimeOffset At);

// ── Reports ─────────────────────────────────────────────────────────────────

public sealed record PlanningReportTypeDto(string Type, string Title, string Description);
