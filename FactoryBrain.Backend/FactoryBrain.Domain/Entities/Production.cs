namespace FactoryBrain.Domain.Entities;

// Production planning & line efficiency. Master data (lines, shifts, styles
// with their operation bulletin) is configured by the plant; nothing here is
// seeded. Orders, plans, output and time studies are recorded by authorised
// users or imported. Every calculation runs on these persisted rows.

/// <summary>A sewing line, as the plant names it. <see cref="Id"/> is the same slug QC inspections use as <c>LineId</c>.</summary>
public class ProductionLine
{
    /// <summary>Stable slug, e.g. <c>line-3</c> (lowercase letters, digits, hyphens).</summary>
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>Declared number of operator workstations on the line (the capacity a balance may use).</summary>
    public int Workstations { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public uint Version { get; set; }
}

/// <summary>
/// One shift of the working calendar, in the plant's local time
/// (<c>PRODUCTION_TIME_ZONE</c>). A shift whose end is not after its start
/// runs overnight; its production date is the date it starts.
/// </summary>
public class ProductionShift
{
    /// <summary>Stable slug, e.g. <c>day</c> or <c>a</c>.</summary>
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public TimeOnly StartLocal { get; set; }
    public TimeOnly EndLocal { get; set; }
    /// <summary>Planned (unpaid) break inside the shift; not production time.</summary>
    public TimeOnly? BreakStartLocal { get; set; }
    public int BreakMinutes { get; set; }
    /// <summary>Working days as a bitmask: bit 0 = Sunday … bit 6 = Saturday (<see cref="DayOfWeek"/> order).</summary>
    public int Weekdays { get; set; } = 0b0111111;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public uint Version { get; set; }
}

/// <summary>A garment style and its factory-approved standard minutes.</summary>
public class ProductionStyle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? ProductType { get; set; }
    /// <summary>
    /// Factory-approved SMV (standard minute value) per garment, in minutes.
    /// When null, the sum of the operation bulletin's SMVs is used and the
    /// API says so; with neither, efficiency can't be calculated.
    /// </summary>
    public double? ApprovedSmv { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public uint Version { get; set; }
    public List<StyleOperation> Operations { get; set; } = new();
}

/// <summary>One operation of a style's operation bulletin.</summary>
public class StyleOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StyleId { get; set; }
    /// <summary>Bulletin sequence number, unique within the style.</summary>
    public int Sequence { get; set; }
    public string Name { get; set; } = default!;
    /// <summary>Standard minutes per piece for this operation.</summary>
    public double Smv { get; set; }
    /// <summary>Machine type the operation needs (e.g. <c>SNLS</c>, <c>OL</c>, <c>manual</c>). A workstation holds one machine type.</summary>
    public string? MachineType { get; set; }
    /// <summary>Sequence numbers of operations that must be done before this one.</summary>
    public int[] Predecessors { get; set; } = Array.Empty<int>();
}

public enum ProductionOrderStatus { Open, Closed, Cancelled }

/// <summary>A production order: a quantity of one style due by a date. Not a sales order and not an ERP replacement.</summary>
public class ProductionOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = default!;
    public Guid StyleId { get; set; }
    public string? Buyer { get; set; }
    public int Quantity { get; set; }
    /// <summary>Ex-factory due date (plant-local). Due at the end of this day.</summary>
    public DateOnly DueDate { get; set; }
    public ProductionOrderStatus Status { get; set; } = ProductionOrderStatus.Open;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary>
/// <c>Draft</c> (editable, no operational effect) → <c>PendingApproval</c>
/// (submitted to the approval queue) → <c>Active</c> (approved, then
/// activated) → <c>Completed</c>. <c>Cancelled</c> from any non-final state.
/// A rejected approval returns the plan to <c>Draft</c>.
/// </summary>
public enum ProductionPlanStatus { Draft, PendingApproval, Active, Completed, Cancelled }

/// <summary>Plan for one order on one line over a date range, with explicit planning inputs.</summary>
public class ProductionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable, e.g. <c>PP-20261026-1A2B3C4D</c>.</summary>
    public string Reference { get; set; } = default!;
    public Guid OrderId { get; set; }
    public string LineId { get; set; } = default!;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int PlannedQuantity { get; set; }
    /// <summary>Planned sewing operators (manpower) on the line.</summary>
    public int Operators { get; set; }
    /// <summary>Target line efficiency, %, as approved by the plant for this plan.</summary>
    public double TargetEfficiencyPct { get; set; }
    /// <summary>Shift codes the plan works. Empty is invalid.</summary>
    public List<string> ShiftCodes { get; set; } = new();
    /// <summary>Planned overtime per working day, minutes, after the last shift.</summary>
    public int OvertimeMinutesPerDay { get; set; }
    public string? Notes { get; set; }
    public ProductionPlanStatus Status { get; set; } = ProductionPlanStatus.Draft;
    /// <summary>Number of applied changes after creation (0 = as created).</summary>
    public int Revision { get; set; }

    /// <summary>Approval for the pending activation (while <c>PendingApproval</c>).</summary>
    public string? ActivationApprovalId { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedByEmail { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public string? ActivatedByEmail { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? ClosedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

public enum PlanChangeStatus { Pending, Applied, Rejected, Superseded }

/// <summary>
/// One change to a plan: who, when, why, and the values before and after
/// (JSON snapshots of the planning inputs). Changes to an active plan stay
/// <c>Pending</c> until their approval is granted and someone applies them;
/// the live plan is never changed silently.
/// </summary>
public class ProductionPlanChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    /// <summary><c>created</c>, <c>edited</c>, <c>revision</c>.</summary>
    public string Kind { get; set; } = default!;
    public PlanChangeStatus Status { get; set; }
    public string? PreviousValues { get; set; }
    public string ProposedValues { get; set; } = default!;
    public string? Reason { get; set; }
    public string? ApprovalId { get; set; }
    /// <summary>The plan's version when the change was requested (stale changes can't be applied).</summary>
    public uint BaseVersion { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByEmail { get; set; } = default!;
    public DateTime? DecidedAt { get; set; }
    public string? DecidedByEmail { get; set; }
}

/// <summary>Where a production count was recorded along the garment flow.</summary>
public enum ProductionStage { Cutting, SewingInput, SewingOutput, Finishing, Packing }

/// <summary>How a record reached the system. There is no live ERP/MES connector yet.</summary>
public enum ProductionDataSource { Manual, Import, Integration }

/// <summary>
/// Pieces counted at one stage for one order on one line over one time
/// interval. Corrections never edit a record: they insert a replacement and
/// mark the original superseded, so history stays auditable and every
/// summary (computed at read time) reflects the latest values.
/// </summary>
public class ProductionOutputEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LineId { get; set; } = default!;
    public Guid OrderId { get; set; }
    public ProductionStage Stage { get; set; }
    public DateTime IntervalStartUtc { get; set; }
    public DateTime IntervalEndUtc { get; set; }
    /// <summary>Plant-local production date (the date the shift started).</summary>
    public DateOnly ProductionDate { get; set; }
    /// <summary>Shift the interval starts in; null when outside every shift (overtime / unscheduled).</summary>
    public string? ShiftCode { get; set; }
    /// <summary>Good pieces.</summary>
    public int Quantity { get; set; }
    public int ReworkQuantity { get; set; }
    public int RejectQuantity { get; set; }
    /// <summary>Operators working on the line during the interval (needed for efficiency).</summary>
    public int? OperatorsPresent { get; set; }
    public double? DowntimeMinutes { get; set; }
    public string? DowntimeReason { get; set; }
    public ProductionDataSource Source { get; set; }
    /// <summary>Source-assigned id (import row key, device event id); unique per source.</summary>
    public string? SourceRef { get; set; }
    public string? Notes { get; set; }
    public Guid? CorrectionOfId { get; set; }
    public bool IsSuperseded { get; set; }
    public Guid? SupersededById { get; set; }
    public string? CorrectionReason { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public Guid? RecordedByUserId { get; set; }
    public string RecordedByEmail { get; set; } = default!;
}

/// <summary>A time-study observation of one operation on a line: the measured evidence for bottleneck analysis.</summary>
public class OperationObservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LineId { get; set; } = default!;
    public Guid StyleId { get; set; }
    public Guid OperationId { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    /// <summary>Mean observed cycle time per piece, seconds, for one operator.</summary>
    public double CycleSeconds { get; set; }
    public int SampleCount { get; set; }
    /// <summary>Operators working this operation in parallel when observed.</summary>
    public int OperatorsAssigned { get; set; }
    /// <summary>Pieces waiting in front of the operation (queue / WIP), if counted.</summary>
    public int? QueueBefore { get; set; }
    public string? Notes { get; set; }
    public ProductionDataSource Source { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public string RecordedByEmail { get; set; } = default!;
}

/// <summary>
/// How operations are distributed over workstations for one style on one
/// line. Exactly one layout per (line, style) is current; applying an
/// approved balance proposal supersedes it.
/// </summary>
public class LineLayout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LineId { get; set; } = default!;
    public Guid StyleId { get; set; }
    public bool IsCurrent { get; set; } = true;
    /// <summary><c>manual</c> (recorded as found on the floor) or <c>proposal</c> (an applied, approved proposal).</summary>
    public string Origin { get; set; } = "manual";
    public Guid? ProposalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
    public DateTime? SupersededAt { get; set; }
    public List<LineLayoutStation> Stations { get; set; } = new();
}

/// <summary>One workstation (one operator) and the operations it performs. An operation on k stations is split evenly between them.</summary>
public class LineLayoutStation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LayoutId { get; set; }
    public int StationNo { get; set; }
    public Guid[] OperationIds { get; set; } = Array.Empty<Guid>();
}

/// <summary>
/// <c>Proposed</c> → <c>PendingApproval</c> → <c>Applied</c> (approved, then
/// applied by a person). <c>Rejected</c> when the approval is rejected;
/// <c>Superseded</c> when its inputs changed before it was applied.
/// </summary>
public enum BalanceProposalStatus { Proposed, PendingApproval, Applied, Rejected, Superseded }

/// <summary>A line-balancing proposal, kept separately from the current layout until approved and applied.</summary>
public class LineBalanceProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Reference { get; set; } = default!;
    public string LineId { get; set; } = default!;
    public Guid StyleId { get; set; }
    public int Workstations { get; set; }
    public double? TargetOutputPerHour { get; set; }
    /// <summary>Hash of the operation bulletin the proposal was computed from.</summary>
    public string InputHash { get; set; } = default!;
    /// <summary>The current layout it was compared with (null when none was recorded).</summary>
    public Guid? BaselineLayoutId { get; set; }
    /// <summary>Full analysis result (proposed stations, metrics, assumptions), JSON.</summary>
    public string ResultJson { get; set; } = default!;
    public BalanceProposalStatus Status { get; set; } = BalanceProposalStatus.Proposed;
    public string? ApprovalId { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedByEmail { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public string? AppliedByEmail { get; set; }
    public string? StatusReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}
