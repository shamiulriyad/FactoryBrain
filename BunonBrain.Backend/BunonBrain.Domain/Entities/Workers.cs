namespace BunonBrain.Domain.Entities;

// Worker management: the worker record itself, the organisation units it
// points at (department, production floor) and the physical credentials
// (RFID / NFC cards) issued to a worker.
//
// Three identifiers are kept apart on purpose:
//   * Worker ID  (WorkerProfile.WorkerCode)  permanent business identity.
//   * Card UID   (WorkerCard.CardUid)        a physical credential that can be
//                                            lost, returned and re-issued.
//   * Camera track id                        a temporary identifier made up by
//                                            a tracker for one sighting. It is
//                                            never stored on the worker; a later
//                                            module maps track ids to
//                                            WorkerProfile.Id for a time window.

/// <summary>A department of one factory (Cutting, Sewing, Finishing, …).</summary>
public class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>Short code, upper case, unique within the factory (e.g. <c>SEW</c>).</summary>
    public string Code { get; set; } = default!;
    /// <summary>Unique within the factory.</summary>
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A production floor of one factory (a physical floor or hall that holds lines).</summary>
public class ProductionFloor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>Short code, upper case, unique within the factory (e.g. <c>F2</c>).</summary>
    public string Code { get; set; } = default!;
    /// <summary>Unique within the factory.</summary>
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A worker. Separate from <see cref="User"/>: a worker needs no login
/// account, and at most one account can be linked (<see cref="UserId"/>) so
/// the worker can see their own records. Workers are never deleted; leaving
/// is a <see cref="Status"/> change, so history that points at the worker
/// stays valid.
/// </summary>
public class WorkerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Number behind <see cref="WorkerCode"/>, drawn from the database sequence
    /// <c>worker_number_seq</c>. Unique across the installation and never
    /// handed out twice, whatever happens to the worker later.
    /// </summary>
    public long WorkerNumber { get; set; }

    /// <summary>The permanent Worker ID shown to people, e.g. <c>W-001</c>. Unique; never changes.</summary>
    public string WorkerCode { get; set; } = default!;

    public Guid FactoryId { get; set; }

    /// <summary>Employee / staff number from the factory's own HR records, when it differs from the Worker ID. Unique within the factory.</summary>
    public string? EmployeeCode { get; set; }

    public string FullName { get; set; } = default!;

    /// <summary>Key of the profile photograph in the worker photo store; null when there is none.</summary>
    public string? PhotoKey { get; set; }
    public string? PhotoContentType { get; set; }

    // Personal details: returned only to callers who manage workers and to the worker themself.
    public string? Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? PresentAddress { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactRelation { get; set; }

    // Employment.
    public DateOnly? JoinedOn { get; set; }
    /// <summary>One of <c>WorkerStatuses</c>: Active, Inactive, Suspended, Resigned, Terminated.</summary>
    public string Status { get; set; } = "Active";
    public DateTimeOffset? StatusChangedAt { get; set; }
    public string? StatusReason { get; set; }
    /// <summary>One of <c>EmploymentTypes</c>: Permanent, Probation, Contract, Temporary, Apprentice.</summary>
    public string EmploymentType { get; set; } = "Permanent";
    public string? Designation { get; set; }
    public string? Grade { get; set; }

    // Placement.
    public Guid? DepartmentId { get; set; }
    public Guid? FloorId { get; set; }
    public string? LineId { get; set; }
    /// <summary>Free-text workstation label until the workstation module provides real workstations.</summary>
    public string? Workstation { get; set; }

    /// <summary>The linked login account, if the worker has one.</summary>
    public Guid? UserId { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One status change of a worker (including the first, "registered as …").
/// Append-only: this is the worker's employment-status history.
/// </summary>
public class WorkerStatusChange
{
    public long Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>Null for the entry written at registration.</summary>
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = default!;
    public string? Reason { get; set; }
    /// <summary>The date the change applies from, when the person recording it gave one.</summary>
    public DateOnly? EffectiveOn { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ChangedByUserId { get; set; }
    public string ChangedByEmail { get; set; } = default!;
}

/// <summary>
/// A physical credential (RFID / NFC card) issued to a worker. The card UID
/// identifies the card, not the person: a card can be returned and issued to
/// someone else, so only one <em>active</em> card may carry a given UID, and
/// past assignments are kept.
/// </summary>
public class WorkerCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkerId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>The identifier a reader reports, normalised to upper case without separators.</summary>
    public string CardUid { get; set; } = default!;
    /// <summary><c>RFID</c> or <c>NFC</c>.</summary>
    public string CardType { get; set; } = "RFID";
    /// <summary>What is printed on the card, if anything.</summary>
    public string? Label { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? IssuedByUserId { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public Guid? DeactivatedByUserId { get; set; }
    /// <summary>Returned, Lost, Damaged, Replaced or WorkerLeft.</summary>
    public string? DeactivationReason { get; set; }
}
