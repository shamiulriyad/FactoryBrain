using BunonBrain.Domain.Entities;

namespace BunonBrain.Application.Planning;

// Production planning: names, lifecycles and validation rules. Pure (no
// database, no clock); the infrastructure services gather rows and call
// these, and the unit tests exercise them directly.

internal static class Pick
{
    public static string? Parse(IReadOnlyList<string> all, string? raw)
    {
        var key = raw?.Trim().Replace('-', '_').Replace(' ', '_');
        return all.FirstOrDefault(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
    }
}

public static class OrderPriorities
{
    public const string Low = "LOW";
    public const string Normal = "NORMAL";
    public const string High = "HIGH";
    public const string Urgent = "URGENT";
    public static readonly IReadOnlyList<string> All = new[] { Low, Normal, High, Urgent };
    public static string? Parse(string? v) => Pick.Parse(All, v);
    /// <summary>Higher = more urgent.</summary>
    public static int Rank(string p) => p switch { Urgent => 3, High => 2, Normal => 1, _ => 0 };
}

public static class StyleComplexities
{
    public static readonly IReadOnlyList<string> All = new[] { "LOW", "MEDIUM", "HIGH", "VERY_HIGH" };
    public static string? Parse(string? v) => Pick.Parse(All, v);
}

/// <summary>The order lifecycle: which status may follow which, and what each needs.</summary>
public static class OrderLifecycle
{
    private static readonly IReadOnlyDictionary<ProductionOrderStatus, ProductionOrderStatus[]> Next =
        new Dictionary<ProductionOrderStatus, ProductionOrderStatus[]>
        {
            [ProductionOrderStatus.Draft] = new[] { ProductionOrderStatus.Planned, ProductionOrderStatus.Cancelled },
            [ProductionOrderStatus.Planned] = new[] { ProductionOrderStatus.InProgress, ProductionOrderStatus.OnHold, ProductionOrderStatus.Cancelled },
            [ProductionOrderStatus.InProgress] = new[] { ProductionOrderStatus.OnHold, ProductionOrderStatus.Completed, ProductionOrderStatus.Cancelled },
            [ProductionOrderStatus.OnHold] = new[] { ProductionOrderStatus.Planned, ProductionOrderStatus.InProgress, ProductionOrderStatus.Cancelled },
            [ProductionOrderStatus.Completed] = Array.Empty<ProductionOrderStatus>(),
            [ProductionOrderStatus.Cancelled] = Array.Empty<ProductionOrderStatus>(),
        };

    public static IReadOnlyList<ProductionOrderStatus> AllowedFrom(ProductionOrderStatus from) => Next[from];

    public static bool CanMove(ProductionOrderStatus from, ProductionOrderStatus to) => Next[from].Contains(to);

    public static bool IsFinal(ProductionOrderStatus s) => s is ProductionOrderStatus.Completed or ProductionOrderStatus.Cancelled;

    /// <summary>The order can be allocated, planned and produced.</summary>
    public static bool IsLive(ProductionOrderStatus s) => s is ProductionOrderStatus.Planned or ProductionOrderStatus.InProgress;

    /// <summary>Hold, cancel and complete are decisions that need a reason on record.</summary>
    public static bool NeedsReason(ProductionOrderStatus to) => to is ProductionOrderStatus.OnHold or ProductionOrderStatus.Cancelled;

    /// <summary>
    /// Fields an order must have before it can leave <c>Draft</c>: the buyer and the factory.
    /// (Order number, style, quantity and the delivery deadline are required from the start.)
    /// </summary>
    public static IReadOnlyList<string> MissingForPlanned(string? buyer, Guid? factoryId)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(buyer)) missing.Add("buyer");
        if (factoryId is null) missing.Add("factoryId");
        return missing;
    }
}

public static class AllocationStatuses
{
    public const string Planned = "PLANNED";
    public const string Active = "ACTIVE";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";
    public static readonly IReadOnlyList<string> All = new[] { Planned, Active, Completed, Cancelled };
    public static string? Parse(string? v) => Pick.Parse(All, v);
    /// <summary>Counts against the order quantity and the line's commitments.</summary>
    public static bool Counts(string s) => s is Planned or Active or Completed;
    public static bool IsOpen(string s) => s is Planned or Active;

    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (Planned, Active) or (Planned, Cancelled) or (Planned, Completed) => true,
        (Active, Completed) or (Active, Cancelled) => true,
        _ => false,
    };
}

/// <summary>Allocation rules that need no database.</summary>
public static class AllocationRules
{
    /// <param name="OrderQuantity">The order's quantity.</param>
    /// <param name="OtherAllocated">Quantity of the order's other counting allocations.</param>
    /// <param name="This">This allocation's quantity.</param>
    public sealed record QuantityCheck(int OrderQuantity, int OtherAllocated, int This)
    {
        public int Total => OtherAllocated + This;
        public int Excess => Math.Max(0, Total - OrderQuantity);
        public int Remaining => Math.Max(0, OrderQuantity - Total);
        public bool Exceeds => Total > OrderQuantity;
    }

    public static QuantityCheck CheckQuantity(int orderQuantity, int otherAllocated, int quantity) => new(orderQuantity, otherAllocated, quantity);

    public static bool DatesValid(DateOnly start, DateOnly end) => end >= start;

    /// <summary>Whether two inclusive date ranges share a day.</summary>
    public static bool Overlaps(DateOnly aStart, DateOnly aEnd, DateOnly bStart, DateOnly bEnd) => aStart <= bEnd && bStart <= aEnd;

    /// <summary>Days (inclusive) two ranges share.</summary>
    public static int OverlapDays(DateOnly aStart, DateOnly aEnd, DateOnly bStart, DateOnly bEnd)
    {
        var s = aStart > bStart ? aStart : bStart;
        var e = aEnd < bEnd ? aEnd : bEnd;
        return e < s ? 0 : e.DayNumber - s.DayNumber + 1;
    }
}

public static class TargetLevels
{
    public const string Factory = "FACTORY";
    public const string Department = "DEPARTMENT";
    public const string Floor = "FLOOR";
    public const string Line = "LINE";
    public const string Shift = "SHIFT";
    public const string Workstation = "WORKSTATION";
    public const string Machine = "MACHINE";
    public static readonly IReadOnlyList<string> All = new[] { Factory, Department, Floor, Line, Shift, Workstation, Machine };
    public static string? Parse(string? v) => Pick.Parse(All, v);

    /// <summary>Targets that set a factory-wide, department-wide or floor-wide goal are agreed by a manager before they apply.</summary>
    public static bool RequiresApproval(string level) => level is Factory or Department or Floor;
}

public static class TargetPeriods
{
    public const string Daily = "DAILY";
    public const string Shift = "SHIFT";
    public const string Hourly = "HOURLY";
    public static readonly IReadOnlyList<string> All = new[] { Daily, Shift, Hourly };
    public static string? Parse(string? v) => Pick.Parse(All, v);
}

public static class TargetApprovals
{
    public const string NotRequired = "NOT_REQUIRED";
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    /// <summary>The version is in force (when its dates cover the day).</summary>
    public static bool InForce(string s) => s is NotRequired or Approved;
}

public static class TargetActions
{
    public const string Created = "CREATED";
    public const string Revised = "REVISED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Ended = "ENDED";
}

/// <summary>Which ids a target of a level must (and must not) carry, and which target wins when several apply.</summary>
public static class TargetRules
{
    public sealed record Scope(Guid? DepartmentId, Guid? FloorId, string? LineId, Guid? ShiftId, Guid? WorkstationId, Guid? MachineId);

    /// <summary>Validation messages for the combination of level and scope ids (empty when valid).</summary>
    public static IReadOnlyList<(string Field, string Message)> ValidateScope(string level, Scope s)
    {
        var e = new List<(string, string)>();
        void Need(bool present, string field, string what) { if (!present) e.Add((field, $"{what} is required for a {level.ToLowerInvariant()}-level target.")); }
        void Forbid(bool present, string field, string what) { if (present) e.Add((field, $"{what} doesn't apply to a {level.ToLowerInvariant()}-level target.")); }
        switch (level)
        {
            case TargetLevels.Factory:
                Forbid(s.DepartmentId is not null, "departmentId", "Department"); Forbid(s.FloorId is not null, "floorId", "Floor"); Forbid(s.LineId is not null, "lineId", "Line");
                Forbid(s.WorkstationId is not null, "workstationId", "Workstation"); Forbid(s.MachineId is not null, "machineId", "Machine");
                break;
            case TargetLevels.Department:
                Need(s.DepartmentId is not null, "departmentId", "Department");
                Forbid(s.FloorId is not null, "floorId", "Floor"); Forbid(s.LineId is not null, "lineId", "Line");
                Forbid(s.WorkstationId is not null, "workstationId", "Workstation"); Forbid(s.MachineId is not null, "machineId", "Machine");
                break;
            case TargetLevels.Floor:
                Need(s.FloorId is not null, "floorId", "Floor");
                Forbid(s.DepartmentId is not null, "departmentId", "Department"); Forbid(s.LineId is not null, "lineId", "Line");
                Forbid(s.WorkstationId is not null, "workstationId", "Workstation"); Forbid(s.MachineId is not null, "machineId", "Machine");
                break;
            case TargetLevels.Line:
                Need(!string.IsNullOrWhiteSpace(s.LineId), "lineId", "Line");
                Forbid(s.DepartmentId is not null, "departmentId", "Department"); Forbid(s.FloorId is not null, "floorId", "Floor");
                Forbid(s.WorkstationId is not null, "workstationId", "Workstation"); Forbid(s.MachineId is not null, "machineId", "Machine");
                break;
            case TargetLevels.Shift:
                Need(s.ShiftId is not null, "shiftId", "Shift");
                Forbid(s.DepartmentId is not null, "departmentId", "Department"); Forbid(s.FloorId is not null, "floorId", "Floor"); Forbid(s.LineId is not null, "lineId", "Line");
                Forbid(s.WorkstationId is not null, "workstationId", "Workstation"); Forbid(s.MachineId is not null, "machineId", "Machine");
                break;
            case TargetLevels.Workstation:
                Need(s.WorkstationId is not null, "workstationId", "Workstation");
                Forbid(s.DepartmentId is not null, "departmentId", "Department"); Forbid(s.FloorId is not null, "floorId", "Floor"); Forbid(s.MachineId is not null, "machineId", "Machine");
                break;
            case TargetLevels.Machine:
                Need(s.MachineId is not null, "machineId", "Machine");
                Forbid(s.DepartmentId is not null, "departmentId", "Department"); Forbid(s.FloorId is not null, "floorId", "Floor"); Forbid(s.WorkstationId is not null, "workstationId", "Workstation");
                break;
        }
        return e;
    }

    /// <summary>Quantities must be sensible for their period.</summary>
    public static string? ValidateQuantity(string period, int quantity)
    {
        if (quantity < 1) return "Quantity must be at least 1.";
        var max = period == TargetPeriods.Hourly ? 100_000 : 10_000_000;
        return quantity > max ? $"Quantity can't exceed {max:N0} per {period.ToLowerInvariant()} period." : null;
    }

    /// <summary>When several in-force targets cover a scope, the most specific wins: allocation, then order, then shift-specific, then the general one.</summary>
    public static int Specificity(ProductionTarget t) => (t.AllocationId is not null ? 4 : 0) + (t.OrderId is not null ? 2 : 0) + (t.ShiftId is not null ? 1 : 0);
}

public static class PlanStatuses
{
    public const string Draft = "DRAFT";
    public const string UnderReview = "UNDER_REVIEW";
    public const string Approved = "APPROVED";
    public const string Published = "PUBLISHED";
    public const string Revised = "REVISED";
    public const string Cancelled = "CANCELLED";
    public static readonly IReadOnlyList<string> All = new[] { Draft, UnderReview, Approved, Published, Revised, Cancelled };
    public static string? Parse(string? v) => Pick.Parse(All, v);

    /// <summary>Items can be added, changed or removed.</summary>
    public static bool Editable(string s) => s == Draft;
    /// <summary>A plan being prepared: not yet in force and not closed.</summary>
    public static bool IsOpenDraft(string s) => s is Draft or UnderReview or Approved;
    /// <summary>Statuses visible to people who only follow the plan (supervisors, monitoring operators).</summary>
    public static bool VisibleToFollowers(string s) => s is Published or Revised;

    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (Draft, UnderReview) or (Draft, Cancelled) => true,
        (UnderReview, Approved) or (UnderReview, Draft) or (UnderReview, Cancelled) => true,
        (Approved, Published) or (Approved, Draft) or (Approved, Cancelled) => true,
        (Published, Revised) or (Published, Cancelled) => true,
        _ => false,
    };
}

public static class ExceptionTypes
{
    public const string OutputBehindTarget = "OUTPUT_BEHIND_TARGET";
    public const string DeliveryAtRisk = "DELIVERY_AT_RISK";
    public const string LineUnavailable = "LINE_UNAVAILABLE";
    public const string CapacityShortage = "CAPACITY_SHORTAGE";
    public const string MissingCounterData = "MISSING_COUNTER_DATA";
    public const string ExcessAllocation = "EXCESS_ALLOCATION";
    public const string ProductionInterruption = "PRODUCTION_INTERRUPTION";
    public static readonly IReadOnlyList<string> All = new[]
    {
        OutputBehindTarget, DeliveryAtRisk, LineUnavailable, CapacityShortage, MissingCounterData, ExcessAllocation, ProductionInterruption,
    };
    public static string? Parse(string? v) => Pick.Parse(All, v);

    public static string Label(string t) => t switch
    {
        OutputBehindTarget => "Output behind the published target",
        DeliveryAtRisk => "Delivery deadline at risk",
        LineUnavailable => "Production line unavailable",
        CapacityShortage => "Capacity shortage",
        MissingCounterData => "Production counter data missing",
        ExcessAllocation => "Excess production allocation",
        ProductionInterruption => "Unplanned production interruption",
        _ => t,
    };
}

public static class ExceptionStatuses
{
    public const string Open = "OPEN";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Resolved = "RESOLVED";
    public static readonly IReadOnlyList<string> All = new[] { Open, Acknowledged, Resolved };
    public static string? Parse(string? v) => Pick.Parse(All, v);
}

public static class Severities
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public static readonly IReadOnlyList<string> All = new[] { Low, Medium, High };
    public static string? Parse(string? v) => Pick.Parse(All, v);
    public static int Rank(string s) => s switch { High => 3, Medium => 2, _ => 1 };
}

/// <summary>Where a figure comes from. A verified count is never mixed with a recorded or estimated one.</summary>
public static class ActualBases
{
    /// <summary>Counter pieces from the line's designated output devices (plus audited corrections).</summary>
    public const string Counter = "COUNTER";
    /// <summary>Pieces entered by a person or imported (production output records), used only where the line has no output counter.</summary>
    public const string Recorded = "RECORDED";
    /// <summary>No evidence of output exists for the line (no output counter configured, or none reported).</summary>
    public const string None = "NONE";
}

/// <summary>Where the planned-versus-actual calculation stands for one plan item.</summary>
public static class ItemStates
{
    public const string NotStarted = "NOT_STARTED";
    public const string OnTrack = "ON_TRACK";
    public const string Ahead = "AHEAD";
    public const string Behind = "BEHIND";
    public const string Complete = "COMPLETE";
    /// <summary>The shift is over and the target was missed.</summary>
    public const string Missed = "MISSED";
    /// <summary>Output can't be judged: no counter evidence.</summary>
    public const string NoData = "NO_DATA";
}
