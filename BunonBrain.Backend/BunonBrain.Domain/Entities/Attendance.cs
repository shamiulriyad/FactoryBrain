namespace BunonBrain.Domain.Entities;

// Shifts, attendance, breaks and overtime.
//
// Raw events (attendance_events) and the sessions derived from them
// (attendance_sessions, break_sessions) are kept separately: events are
// append-only facts as they arrived (from a reader, the simulator, a person,
// or an approved correction); sessions are recomputed from them. All
// timestamps are stored in UTC; local times (shift start / end) are
// interpreted in the factory's time zone from its attendance policy
// (default Asia/Dhaka). Attendance is not workstation assignment time,
// machine-active time or verified work: those are separate measurements.

/// <summary>
/// A working shift of one factory, e.g. Morning 08:00–17:00. A shift whose
/// end is not after its start runs overnight (ends the next calendar day).
/// Distinct from the plant-wide production calendar (<see cref="ProductionShift"/>),
/// which it may point to through <see cref="ProductionShiftCode"/>.
/// </summary>
public class Shift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>Short code, upper case, unique within the factory (e.g. <c>MORNING</c>).</summary>
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public TimeOnly StartLocal { get; set; }
    public TimeOnly EndLocal { get; set; }
    /// <summary>Minutes after the start before a check-in counts as late.</summary>
    public int LateGraceMinutes { get; set; } = 10;
    /// <summary>Minutes before the end a check-out may happen without counting as early departure.</summary>
    public int EarlyLeaveGraceMinutes { get; set; } = 5;
    /// <summary>Working days as a bitmask: bit 0 = Sunday … bit 6 = Saturday.</summary>
    public int WorkingDays { get; set; } = 0b0111111;
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
    /// <summary>The production-calendar shift this attendance shift corresponds to, if any.</summary>
    public string? ProductionShiftCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ShiftBreak> Breaks { get; set; } = new();
}

/// <summary>A scheduled break of a shift (lunch, tea, prayer, …).</summary>
public class ShiftBreak
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShiftId { get; set; }
    /// <summary>Lunch, Tea, Prayer, Personal or Other.</summary>
    public string BreakType { get; set; } = "Lunch";
    public string Name { get; set; } = default!;
    /// <summary>When it is scheduled to start (local time), or null for "when needed".</summary>
    public TimeOnly? StartLocal { get; set; }
    public int DurationMinutes { get; set; }
    /// <summary>Paid breaks stay inside net working time; unpaid ones are deducted.</summary>
    public bool IsPaid { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A worker's shift from a date (inclusive) until a date (inclusive, open-ended when null).</summary>
public class WorkerShiftAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkerId { get; set; }
    public Guid FactoryId { get; set; }
    public Guid ShiftId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    /// <summary>Active, Ended or Superseded (replaced by a later assignment).</summary>
    public string Status { get; set; } = "Active";
    public string? Reason { get; set; }
    public string? EndReason { get; set; }
    public Guid? PreviousAssignmentId { get; set; }
    public Guid? AssignedByUserId { get; set; }
    public string AssignedByEmail { get; set; } = default!;
    public Guid? EndedByUserId { get; set; }
    public string? EndedByEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An attendance reader: a real RFID / NFC reader or gateway, or a
/// simulated one. Events from a simulated reader are always marked simulated.
/// </summary>
public class AttendanceReader
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>Entry, Exit or Both.</summary>
    public string Direction { get; set; } = "Both";
    /// <summary>Simulated, Rfid, Nfc or Gateway.</summary>
    public string Kind { get; set; } = "Simulated";
    public bool IsSimulated { get; set; } = true;
    public string? Location { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One raw attendance event exactly as received, with what processing made
/// of it. Append-only: never updated or deleted.
/// </summary>
public class AttendanceEvent
{
    public long Id { get; set; }
    public Guid FactoryId { get; set; }
    public Guid WorkerId { get; set; }
    /// <summary>CheckIn, CheckOut, BreakStart or BreakEnd.</summary>
    public string EventType { get; set; } = default!;
    /// <summary>When it happened (UTC).</summary>
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Simulator, Device, Manual, Correction or System.</summary>
    public string Source { get; set; } = default!;
    public bool IsSimulated { get; set; }
    public Guid? ReaderId { get; set; }
    public string? CardUid { get; set; }
    /// <summary>Unique per factory: the same key is processed once, a retry gets the first result.</summary>
    public string IdempotencyKey { get; set; } = default!;
    /// <summary>Accepted, Duplicate or Rejected.</summary>
    public string Outcome { get; set; } = default!;
    public string? OutcomeCode { get; set; }
    public string? OutcomeMessage { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? BreakSessionId { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string RecordedByEmail { get; set; } = default!;
    public string? Note { get; set; }
}

/// <summary>
/// One attendance session (check-in to check-out) derived from events and
/// approved corrections, with the calculated durations stored for
/// reporting. A session left open past its scheduled end is closed for
/// review (<see cref="Status"/> = MissingCheckOut); it never stays active forever.
/// </summary>
public class AttendanceSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkerId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>The worker's line when the session started (for line-scoped access).</summary>
    public string? LineId { get; set; }
    public Guid? ShiftAssignmentId { get; set; }
    public Guid? ShiftId { get; set; }
    /// <summary>The factory-local date the shift (or the session, without a shift) started on.</summary>
    public DateOnly WorkDate { get; set; }
    public DateTimeOffset? ScheduledStart { get; set; }
    public DateTimeOffset? ScheduledEnd { get; set; }
    public DateTimeOffset? CheckInAt { get; set; }
    public DateTimeOffset? CheckOutAt { get; set; }
    public string? CheckInSource { get; set; }
    public string? CheckOutSource { get; set; }
    /// <summary>Open, Completed, MissingCheckOut or MissingCheckIn.</summary>
    public string Status { get; set; } = "Open";
    public bool NeedsReview { get; set; }
    public string? ReviewReason { get; set; }
    /// <summary>At least one event of the session came from the simulator.</summary>
    public bool HasSimulatedEvents { get; set; }
    public bool IsCorrected { get; set; }

    // Derived minutes (recomputed whenever the session changes).
    public int AttendanceMinutes { get; set; }
    public int ScheduledMinutes { get; set; }
    public int ScheduledBreakMinutes { get; set; }
    public int BreakMinutes { get; set; }
    public int OverBreakMinutes { get; set; }
    public int NetWorkingMinutes { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyDepartureMinutes { get; set; }
    public int BeyondShiftMinutes { get; set; }
    public int ApprovedOvertimeMinutes { get; set; }
    public bool IsLate { get; set; }
    public bool IsEarlyDeparture { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One break taken during an attendance session.</summary>
public class BreakSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkerId { get; set; }
    public Guid FactoryId { get; set; }
    public Guid AttendanceSessionId { get; set; }
    public Guid? ShiftBreakId { get; set; }
    /// <summary>Lunch, Tea, Prayer, Personal or Other.</summary>
    public string BreakType { get; set; } = default!;
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public int AllowedMinutes { get; set; }
    /// <summary>Active, Completed or AutoClosed (ended by the check-out).</summary>
    public string Status { get; set; } = "Active";
    public string StartSource { get; set; } = default!;
    public string? EndSource { get; set; }
    public bool IsSimulated { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An overtime request and its outcome. Requested, approved, actual and
/// payable minutes are kept apart: approval sets the approved window; the
/// actual minutes come from attendance inside it; payable minutes are set
/// only by an HR review. Nothing here computes or deducts pay.
/// </summary>
public class OvertimeRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public Guid WorkerId { get; set; }
    public string? LineId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTimeOffset PlannedStart { get; set; }
    public DateTimeOffset PlannedEnd { get; set; }
    public int RequestedMinutes { get; set; }
    public string Reason { get; set; } = default!;
    /// <summary>Requested, PendingApproval, Approved, Rejected, InProgress, Completed or Cancelled.</summary>
    public string Status { get; set; } = "Requested";
    public int? ApprovedMinutes { get; set; }
    public DateTimeOffset? ApprovedStart { get; set; }
    public DateTimeOffset? ApprovedEnd { get; set; }
    /// <summary>Attendance minutes inside the approved window (from attendance events).</summary>
    public int? ActualMinutes { get; set; }
    /// <summary>Set only by an HR review; never derived automatically.</summary>
    public int? PayableMinutes { get; set; }
    public string? PayableNote { get; set; }
    public Guid? PayableConfirmedByUserId { get; set; }
    public string? PayableConfirmedByEmail { get; set; }
    public DateTimeOffset? PayableConfirmedAt { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByEmail { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One step of an overtime request's review (append-only).</summary>
public class OvertimeApproval
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    /// <summary>Submitted, Approved, Rejected, Cancelled, Started, Completed or PayableConfirmed.</summary>
    public string Action { get; set; } = default!;
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = default!;
    public int? Minutes { get; set; }
    public string? Note { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorEmail { get; set; } = default!;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A manual correction of a session's check-in / check-out (or a session to
/// create for a day with no punches). Takes effect only when approved by
/// someone other than the requester; the original values are kept.
/// </summary>
public class AttendanceCorrection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public Guid WorkerId { get; set; }
    /// <summary>The corrected session; null when the correction creates one.</summary>
    public Guid? SessionId { get; set; }
    public DateOnly? WorkDate { get; set; }
    public DateTimeOffset? ProposedCheckIn { get; set; }
    public DateTimeOffset? ProposedCheckOut { get; set; }
    public DateTimeOffset? OriginalCheckIn { get; set; }
    public DateTimeOffset? OriginalCheckOut { get; set; }
    public string Reason { get; set; } = default!;
    /// <summary>Pending, Approved or Rejected.</summary>
    public string Status { get; set; } = "Pending";
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByEmail { get; set; } = default!;
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? DecidedByUserId { get; set; }
    public string? DecidedByEmail { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>
/// Attendance and overtime settings of one factory. Defaults apply until an
/// administrator saves the factory's own values. Overtime limits are
/// warnings for HR review, not legal advice: they must follow the
/// applicable Bangladesh labour law and the factory's agreements.
/// </summary>
public class AttendancePolicy
{
    public Guid FactoryId { get; set; }
    /// <summary>IANA id; default Asia/Dhaka.</summary>
    public string TimeZoneId { get; set; } = "Asia/Dhaka";
    /// <summary>A repeated punch of the same kind within this many seconds is a duplicate (reader double-read).</summary>
    public int DuplicateWindowSeconds { get; set; } = 120;
    /// <summary>An open session this long after its scheduled end is closed as MissingCheckOut for review.</summary>
    public int MissingCheckOutAfterMinutes { get; set; } = 240;
    /// <summary>Without a shift: an open session this long after check-in is closed for review.</summary>
    public int MaxOpenSessionHours { get; set; } = 16;
    /// <summary>How far back an event may be recorded or simulated.</summary>
    public int MaxBackdateDays { get; set; } = 7;
    /// <summary>How early before the scheduled start a check-in still belongs to that shift.</summary>
    public int EarlyCheckInWindowMinutes { get; set; } = 180;
    /// <summary>Overtime minutes are rounded down to this step when suggesting payable time.</summary>
    public int OvertimeRoundingMinutes { get; set; } = 15;
    public int MinOvertimeMinutes { get; set; } = 30;
    /// <summary>Warn above this many overtime minutes on one day (a review aid, not a hard rule).</summary>
    public int MaxDailyOvertimeMinutes { get; set; } = 120;
    /// <summary>Warn above this many overtime minutes in one week.</summary>
    public int MaxWeeklyOvertimeMinutes { get; set; } = 720;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedByEmail { get; set; }
}
