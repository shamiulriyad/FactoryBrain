namespace BunonBrain.Domain.Entities;

// Production planning & target management.
//
// Extends the production module (orders, styles with their operation
// bulletins, lines, plans, manual output) and reads the IoT production
// counters; it never duplicates or edits them. Planning records (allocations,
// targets, daily plans, exceptions) are kept apart from the append-only
// counter events: a plan says what was intended, the counters say what was
// counted. Status and kind columns are text (see Application/Planning/PlanningCatalog).
// Timestamps are UTC; plan dates are factory-local operational dates.

/// <summary>One status change of a production order. Append-only: the order's lifecycle history.</summary>
public class ProductionOrderStatusChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    /// <summary>Null for the entry written when the order is created.</summary>
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = default!;
    public string? Reason { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ChangedByUserId { get; set; }
    public string ChangedByEmail { get; set; } = default!;
}

/// <summary>
/// Part (or all) of an order assigned to one production line for a date
/// range. An order may have several allocations (one per line, or the same
/// line in different periods); together they may not exceed the order
/// quantity unless an authorised user explicitly records an override.
/// </summary>
public class ProductionAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid FactoryId { get; set; }
    public string LineId { get; set; } = default!;
    public int Quantity { get; set; }
    public DateOnly PlannedStart { get; set; }
    public DateOnly PlannedEnd { get; set; }
    /// <summary><c>LOW</c>, <c>NORMAL</c>, <c>HIGH</c> or <c>URGENT</c>.</summary>
    public string Priority { get; set; } = "NORMAL";
    /// <summary><c>PLANNED</c>, <c>ACTIVE</c>, <c>COMPLETED</c> or <c>CANCELLED</c>.</summary>
    public string Status { get; set; } = "PLANNED";
    /// <summary>The user responsible for the allocation on the line (must reach the line).</summary>
    public Guid? SupervisorUserId { get; set; }
    public string? SupervisorEmail { get; set; }
    /// <summary>True when this allocation was saved although the order's allocations then exceed its quantity.</summary>
    public bool OverAllocated { get; set; }
    public string? OverrideReason { get; set; }
    public string? OverrideByEmail { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary>Who changed an allocation, when, why, and the values before and after. Append-only.</summary>
public class ProductionAllocationChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AllocationId { get; set; }
    public Guid OrderId { get; set; }
    /// <summary><c>created</c>, <c>updated</c>, <c>cancelled</c>, <c>status</c>.</summary>
    public string Kind { get; set; } = default!;
    public string? PreviousValues { get; set; }
    public string NewValues { get; set; } = default!;
    public string? Reason { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ChangedByUserId { get; set; }
    public string ChangedByEmail { get; set; } = default!;
}

/// <summary>
/// A counter device whose pieces measure <em>finished garments leaving a
/// line</em>. A line has many machine counters that count different
/// operations, so summing all of them would overstate output; only the
/// devices designated here count towards a line's verified production.
/// </summary>
public class LineOutputPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public string LineId { get; set; } = default!;
    public Guid DeviceId { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? DeactivatedByEmail { get; set; }
}

/// <summary>
/// The planner's capacity assumptions for one line. Nothing is assumed: a
/// capacity estimate stays incomplete until the efficiency is configured
/// (and the operators, when no workstation assignments exist).
/// </summary>
public class LineCapacitySetting
{
    public string LineId { get; set; } = default!;
    public Guid FactoryId { get; set; }
    /// <summary>Expected line efficiency, % (output × SMV ÷ operator minutes) the plant plans with.</summary>
    public double? EfficiencyPct { get; set; }
    /// <summary>Operators to plan with when this is set; otherwise the workers assigned to the line's workstations.</summary>
    public int? PlannedOperators { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary>
/// One version of a production target. A change never overwrites a version:
/// the revision is a new row (same <see cref="TargetKey"/>, higher
/// <see cref="Revision"/>) and the older row's <see cref="EffectiveTo"/> is
/// closed, so the target in force on any past date can be read back.
/// </summary>
public class ProductionTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Identifies the target across its revisions.</summary>
    public Guid TargetKey { get; set; } = Guid.NewGuid();
    public int Revision { get; set; } = 1;
    /// <summary>The version this one replaces (null for the first).</summary>
    public Guid? SupersedesId { get; set; }
    /// <summary><c>FACTORY</c>, <c>DEPARTMENT</c>, <c>FLOOR</c>, <c>LINE</c>, <c>SHIFT</c>, <c>WORKSTATION</c> or <c>MACHINE</c>.</summary>
    public string Level { get; set; } = default!;
    /// <summary><c>DAILY</c>, <c>SHIFT</c> or <c>HOURLY</c>: what <see cref="Quantity"/> is per.</summary>
    public string Period { get; set; } = "DAILY";
    public Guid FactoryId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? FloorId { get; set; }
    public string? LineId { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid? WorkstationId { get; set; }
    public Guid? MachineId { get; set; }
    /// <summary>Order-specific target (the order's pieces per period on the scope).</summary>
    public Guid? OrderId { get; set; }
    public Guid? AllocationId { get; set; }
    public int Quantity { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    /// <summary>Last day the version applies; null while open-ended.</summary>
    public DateOnly? EffectiveTo { get; set; }
    /// <summary><c>NOT_REQUIRED</c>, <c>PENDING</c>, <c>APPROVED</c> or <c>REJECTED</c>.</summary>
    public string ApprovalStatus { get; set; } = "NOT_REQUIRED";
    /// <summary>True once a later version took over (the row is kept as history).</summary>
    public bool IsSuperseded { get; set; }
    public string? Notes { get; set; }
    public string? ChangeReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedByEmail { get; set; }
    public string? DecisionNote { get; set; }
    public uint Version { get; set; }
}

/// <summary>Everything that happened to a target version: created, revised, approved, rejected, ended. Append-only.</summary>
public class ProductionTargetRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TargetId { get; set; }
    public Guid TargetKey { get; set; }
    public int Revision { get; set; }
    /// <summary><c>CREATED</c>, <c>REVISED</c>, <c>APPROVED</c>, <c>REJECTED</c>, <c>ENDED</c>.</summary>
    public string Action { get; set; } = default!;
    public int? PreviousQuantity { get; set; }
    public int NewQuantity { get; set; }
    public DateOnly? PreviousEffectiveFrom { get; set; }
    public DateOnly NewEffectiveFrom { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ByUserId { get; set; }
    public string ByEmail { get; set; } = default!;
}

/// <summary>
/// The production plan of one factory for one operational date. A published
/// plan is revised by starting a new revision (a copy that is edited,
/// reviewed and published again); the revision it replaces becomes
/// <c>REVISED</c>, so every published version stays on record.
/// </summary>
public class DailyProductionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable, e.g. <c>DP-20261116-3F2A1B9C</c>.</summary>
    public string Reference { get; set; } = default!;
    public Guid FactoryId { get; set; }
    public DateOnly PlanDate { get; set; }
    public int RevisionNo { get; set; } = 1;
    /// <summary><c>DRAFT</c>, <c>UNDER_REVIEW</c>, <c>APPROVED</c>, <c>PUBLISHED</c>, <c>REVISED</c> or <c>CANCELLED</c>.</summary>
    public string Status { get; set; } = "DRAFT";
    /// <summary>The published plan this revision was started from.</summary>
    public Guid? BasedOnPlanId { get; set; }
    public string? RevisionReason { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedByEmail { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedByEmail { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishedByEmail { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
    public List<DailyPlanItem> Items { get; set; } = new();
}

/// <summary>One order on one line in one shift of a daily plan, with its target pieces.</summary>
public class DailyPlanItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public Guid FactoryId { get; set; }
    public DateOnly PlanDate { get; set; }
    public Guid OrderId { get; set; }
    public Guid? AllocationId { get; set; }
    public string LineId { get; set; } = default!;
    public Guid ShiftId { get; set; }
    public int TargetQuantity { get; set; }
    /// <summary><c>MANUAL</c> or <c>TARGET</c> (taken from a configured target, see <see cref="TargetId"/>).</summary>
    public string TargetSource { get; set; } = "MANUAL";
    public Guid? TargetId { get; set; }
    public Guid? SupervisorUserId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Audit trail of a daily plan: who did what, when, with the reason. Append-only.</summary>
public class DailyPlanEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary><c>CREATED</c>, <c>ITEM_ADDED</c>, <c>ITEM_CHANGED</c>, <c>ITEM_REMOVED</c>, <c>SUBMITTED</c>, <c>APPROVED</c>, <c>RETURNED</c>,
    /// <c>PUBLISHED</c>, <c>REVISION_STARTED</c>, <c>SUPERSEDED</c>, <c>CANCELLED</c>.</summary>
    public string Action { get; set; } = default!;
    public string? Details { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ByUserId { get; set; }
    public string ByEmail { get; set; } = default!;
}

/// <summary>
/// A detected production delay, shortfall or data gap, with the evidence it
/// was detected from. Detection is rule-based; an exception names a line,
/// order or counter, never a person.
/// </summary>
public class ProductionException
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>One of <c>PlanningCatalog.ExceptionTypes</c>.</summary>
    public string Type { get; set; } = default!;
    /// <summary><c>LOW</c>, <c>MEDIUM</c> or <c>HIGH</c>.</summary>
    public string Severity { get; set; } = "MEDIUM";
    /// <summary><c>OPEN</c>, <c>ACKNOWLEDGED</c> or <c>RESOLVED</c>.</summary>
    public string Status { get; set; } = "OPEN";
    /// <summary>One open exception per key (type + subject + date), refreshed while the condition lasts.</summary>
    public string DedupeKey { get; set; } = default!;
    public Guid? OrderId { get; set; }
    public Guid? AllocationId { get; set; }
    public Guid? PlanItemId { get; set; }
    public string? LineId { get; set; }
    public Guid? ShiftId { get; set; }
    public DateOnly? ProductionDate { get; set; }
    public string Title { get; set; } = default!;
    public string Reason { get; set; } = default!;
    /// <summary>JSON array of <c>{label, value}</c> facts the rule used.</summary>
    public string SupportingData { get; set; } = "[]";
    public string SuggestedInvestigation { get; set; } = default!;
    /// <summary><c>REAL</c> or <c>SIMULATED</c>: the data the rule read.</summary>
    public string SourceType { get; set; } = "REAL";
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastDetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Occurrences { get; set; } = 1;
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedByEmail { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedByEmail { get; set; }
    public string? ResolutionNote { get; set; }
    public bool AutoResolved { get; set; }
    public uint Version { get; set; }
}
