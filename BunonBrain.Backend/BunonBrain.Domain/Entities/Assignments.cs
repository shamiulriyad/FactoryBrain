namespace BunonBrain.Domain.Entities;

// Workstation assignment: the physical workstations (tables) of a line, the
// machines that stand at them, and the record of which worker is assigned to
// which workstation from when to when.
//
// An assignment is a scheduled / recorded placement made by a person. It is
// not evidence that the worker was present or that the machine ran: those
// come from attendance and machine events in later modules, which will read
// this history to know where a worker was supposed to be.

/// <summary>
/// A workstation (sewing table, cutting table, …) on one production line.
/// <see cref="Code"/> is what is written on the table (e.g. <c>T-025</c>).
/// Never deleted; a workstation that is out of use is deactivated, and
/// assignments keep pointing at it.
/// </summary>
public class Workstation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>The department the workstation belongs to (e.g. Sewing).</summary>
    public Guid DepartmentId { get; set; }
    /// <summary>The production floor, when the factory records floors.</summary>
    public Guid? FloorId { get; set; }
    public string LineId { get; set; } = default!;
    /// <summary>Upper case, unique within the factory (e.g. <c>T-025</c>).</summary>
    public string Code { get; set; } = default!;
    /// <summary>Optional description (operation done here, position on the line).</summary>
    public string? Name { get; set; }
    /// <summary>How many workers may be assigned at the same time (1 for most tables).</summary>
    public int Capacity { get; set; } = 1;
    /// <summary>Order along the line, for the line layout (1 = first).</summary>
    public int Position { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A machine of a factory (sewing machine, overlock, …). It stands at one
/// workstation at a time (<see cref="WorkstationId"/>), or is not placed yet.
/// </summary>
public class Machine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>Asset tag, upper case, unique within the factory (e.g. <c>M-0101</c>).</summary>
    public string Code { get; set; } = default!;
    /// <summary>Kind of machine, e.g. "Single needle lockstitch".</summary>
    public string? MachineType { get; set; }
    public string? Brand { get; set; }
    public string? SerialNumber { get; set; }
    public Guid? WorkstationId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One worker assigned to one workstation for a period. Rows are never
/// deleted or reopened: ending or transferring sets <see cref="EndAt"/>, and a
/// transfer adds a new row that points back at the one it replaced. The
/// placement (factory, department, floor, line, machine) is copied from the
/// workstation at the time, so history stays true when the workstation is
/// later moved or reconfigured.
/// </summary>
public class WorkstationAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkerId { get; set; }
    public Guid FactoryId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? FloorId { get; set; }
    public string LineId { get; set; } = default!;
    public Guid WorkstationId { get; set; }
    public Guid? MachineId { get; set; }
    /// <summary>The worker's main workstation. At most one active primary assignment per worker.</summary>
    public bool IsPrimary { get; set; } = true;
    public DateTimeOffset StartAt { get; set; }
    /// <summary>Null while the assignment is active.</summary>
    public DateTimeOffset? EndAt { get; set; }
    /// <summary>Active, Ended or Transferred.</summary>
    public string Status { get; set; } = "Active";
    /// <summary>Why the worker was assigned here (for a transfer: why they were moved).</summary>
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    /// <summary>Why the assignment ended (or the reason of the transfer that closed it).</summary>
    public string? EndReason { get; set; }
    /// <summary>The assignment this one replaced, when it was created by a transfer.</summary>
    public Guid? PreviousAssignmentId { get; set; }
    public Guid? AssignedByUserId { get; set; }
    public string AssignedByEmail { get; set; } = default!;
    public Guid? EndedByUserId { get; set; }
    public string? EndedByEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
