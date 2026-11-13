namespace BunonBrain.Application.Dtos.Attendance;

// ─── Shifts ────────────────────────────────────────────────────────────────

public sealed record ShiftBreakDto(Guid? Id, string BreakType, string Name, string? StartLocal, int DurationMinutes, bool IsPaid);

public sealed record ShiftDto(
    Guid Id, Guid FactoryId, string Code, string Name, string StartLocal, string EndLocal, bool IsOvernight,
    int ScheduledMinutes, int UnpaidBreakMinutes, int NetScheduledMinutes, int LateGraceMinutes, int EarlyLeaveGraceMinutes,
    IReadOnlyList<string> WorkingDays, bool IsActive, string? Description, string? ProductionShiftCode,
    IReadOnlyList<ShiftBreakDto> Breaks, int CurrentWorkers, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ShiftRef(Guid Id, string Code, string Name, string StartLocal, string EndLocal, bool IsOvernight);

/// <summary>Times are "HH:mm" in the factory's time zone. An end not after the start means the shift ends the next day.</summary>
public sealed record CreateShiftRequest(
    Guid FactoryId, string Code, string Name, string StartLocal, string EndLocal, IReadOnlyList<string> WorkingDays,
    int LateGraceMinutes = 10, int EarlyLeaveGraceMinutes = 5, string? Description = null, string? ProductionShiftCode = null,
    IReadOnlyList<ShiftBreakDto>? Breaks = null);

public sealed record UpdateShiftRequest(
    string Name, string StartLocal, string EndLocal, IReadOnlyList<string> WorkingDays, int LateGraceMinutes, int EarlyLeaveGraceMinutes,
    bool IsActive, string? Description = null, string? ProductionShiftCode = null, IReadOnlyList<ShiftBreakDto>? Breaks = null);

// ─── Shift assignments ─────────────────────────────────────────────────────

public sealed record ShiftAssignmentDto(
    Guid Id, Guid WorkerId, string WorkerCode, string WorkerName, Guid FactoryId, ShiftRef Shift,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status, string? Reason, string? EndReason,
    Guid? PreviousAssignmentId, string AssignedByEmail, string? EndedByEmail, DateTimeOffset CreatedAt);

/// <summary>
/// POST /api/shift-assignments: give <c>WorkerIds</c>, or a whole
/// <c>LineId</c> or <c>DepartmentId</c> (its active workers). An existing
/// assignment that overlaps is ended the day before <c>EffectiveFrom</c>;
/// one that starts on or after it is refused as a conflict.
/// </summary>
public sealed record AssignShiftRequest(
    Guid ShiftId, DateOnly EffectiveFrom, DateOnly? EffectiveTo = null, IReadOnlyList<Guid>? WorkerIds = null,
    string? LineId = null, Guid? DepartmentId = null, string? Reason = null);

public sealed record AssignShiftOutcome(Guid WorkerId, string WorkerCode, string Outcome, string? Code, string? Message, Guid? AssignmentId);

public sealed record AssignShiftResult(int Requested, int Assigned, int Skipped, IReadOnlyList<AssignShiftOutcome> Results);

public sealed record EndShiftAssignmentRequest(DateOnly EffectiveTo, string Reason);

public sealed record ShiftAssignmentQuery(
    Guid? FactoryId = null, Guid? ShiftId = null, Guid? WorkerId = null, string? LineId = null, DateOnly? ActiveOn = null,
    string? Status = null, string? Search = null, int Page = 1, int PageSize = 50);

// ─── Events, sessions, breaks ──────────────────────────────────────────────

/// <summary>A manual check-in / check-out recorded by a person. <c>OccurredAt</c> defaults to now.</summary>
public sealed record PunchRequest(Guid WorkerId, DateTimeOffset? OccurredAt = null, string? IdempotencyKey = null, string? Note = null);

public sealed record BreakPunchRequest(Guid WorkerId, string? BreakType = null, Guid? ShiftBreakId = null, DateTimeOffset? OccurredAt = null,
    string? IdempotencyKey = null, string? Note = null);

/// <summary>A simulated reader event. Always stored and shown as SIMULATED.</summary>
public sealed record SimulatorPunchRequest(Guid WorkerId, Guid? ReaderId = null, DateTimeOffset? OccurredAt = null, string? IdempotencyKey = null,
    string? BreakType = null);

/// <summary>An event from a physical reader or gateway: the worker is found by the card's UID.</summary>
public sealed record DeviceEventRequest(Guid ReaderId, string CardUid, string EventType, DateTimeOffset OccurredAt, string IdempotencyKey);

public sealed record AttendanceEventDto(
    long Id, Guid WorkerId, string WorkerCode, string WorkerName, Guid FactoryId, string EventType, DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt, string Source, bool IsSimulated, string? ReaderCode, string? CardUid, string Outcome,
    string? OutcomeCode, string? OutcomeMessage, Guid? SessionId, string RecordedByEmail, string? Note);

public sealed record SessionFiguresDto(
    int AttendanceMinutes, int ScheduledMinutes, int ScheduledBreakMinutes, int NetScheduledMinutes, int BreakMinutes,
    int OverBreakMinutes, int NetWorkingMinutes, int LateMinutes, int EarlyDepartureMinutes, int BeyondShiftMinutes,
    int ApprovedOvertimeMinutes);

public sealed record AttendanceSessionDto(
    Guid Id, Guid WorkerId, string WorkerCode, string WorkerName, Guid FactoryId, string? LineId, DateOnly WorkDate,
    ShiftRef? Shift, DateTimeOffset? ScheduledStart, DateTimeOffset? ScheduledEnd, DateTimeOffset? CheckInAt, DateTimeOffset? CheckOutAt,
    string? CheckInSource, string? CheckOutSource, string Status, bool NeedsReview, string? ReviewReason, bool HasSimulatedEvents,
    bool IsCorrected, bool IsLate, bool IsEarlyDeparture, bool OnBreak, SessionFiguresDto Figures, DateTimeOffset CreatedAt);

public sealed record BreakSessionDto(
    Guid Id, Guid WorkerId, string WorkerCode, string WorkerName, Guid AttendanceSessionId, string BreakType,
    DateTimeOffset StartAt, DateTimeOffset? EndAt, int AllowedMinutes, int? ActualMinutes, string Status, bool IsOverdue,
    int OverMinutes, bool IsSimulated, string StartSource, string? EndSource);

/// <summary>What processing an event produced. <c>Replayed</c>: the idempotency key was seen before; this is the first result.</summary>
public sealed record EventResult(AttendanceEventDto Event, AttendanceSessionDto? Session, BreakSessionDto? Break, bool Replayed);

/// <summary>GET /api/attendance. Dates are factory-local work dates. <c>sort</c>: date | worker | checkIn | late (prefix "-" descending).</summary>
public sealed record AttendanceListQuery(
    Guid? FactoryId = null, string? LineId = null, Guid? DepartmentId = null, Guid? WorkerId = null, DateOnly? From = null, DateOnly? To = null,
    string? Status = null, bool? NeedsReview = null, bool? Late = null, bool? Simulated = null, string? Search = null,
    string? Sort = null, int Page = 1, int PageSize = 50);

public sealed record EventListQuery(
    Guid? FactoryId = null, Guid? WorkerId = null, bool? Simulated = null, string? Source = null, string? Outcome = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int Page = 1, int PageSize = 50);

public sealed record BreakListQuery(
    Guid? FactoryId = null, string? LineId = null, Guid? WorkerId = null, bool? Active = null, bool? Overdue = null,
    DateOnly? From = null, DateOnly? To = null, int Page = 1, int PageSize = 50);

// ─── Corrections ───────────────────────────────────────────────────────────

/// <summary>POST /api/attendance/{sessionId}/correction: the values the session should have.</summary>
public sealed record CreateCorrectionRequest(DateTimeOffset? CheckInAt, DateTimeOffset? CheckOutAt, string Reason);

/// <summary>POST /api/attendance/corrections: a day with no punches at all.</summary>
public sealed record CreateMissingSessionRequest(Guid WorkerId, DateTimeOffset CheckInAt, DateTimeOffset CheckOutAt, string Reason);

public sealed record DecideCorrectionRequest(string? Note);

public sealed record CorrectionDto(
    Guid Id, Guid WorkerId, string WorkerCode, string WorkerName, Guid FactoryId, Guid? SessionId, DateOnly? WorkDate,
    DateTimeOffset? ProposedCheckIn, DateTimeOffset? ProposedCheckOut, DateTimeOffset? OriginalCheckIn, DateTimeOffset? OriginalCheckOut,
    string Reason, string Status, string RequestedByEmail, DateTimeOffset RequestedAt, string? DecidedByEmail, DateTimeOffset? DecidedAt,
    string? DecisionNote);

// ─── Overtime ──────────────────────────────────────────────────────────────

public sealed record OvertimeApprovalDto(long Id, string Action, string? FromStatus, string ToStatus, int? Minutes, string? Note, string ActorEmail, DateTimeOffset OccurredAt);

public sealed record OvertimeCapabilities(bool Submit, bool Approve, bool Reject, bool Cancel, bool ConfirmPayable);

public sealed record OvertimeDto(
    Guid Id, Guid WorkerId, string WorkerCode, string WorkerName, Guid FactoryId, string? LineId, DateOnly WorkDate,
    DateTimeOffset PlannedStart, DateTimeOffset PlannedEnd, int RequestedMinutes, string Reason, string Status,
    int? ApprovedMinutes, DateTimeOffset? ApprovedStart, DateTimeOffset? ApprovedEnd, int? ActualMinutes,
    int? BeyondShiftMinutes, int? PayableSuggestionMinutes, int? PayableMinutes, string? PayableNote, string? PayableConfirmedByEmail,
    string RequestedByEmail, IReadOnlyList<string> Warnings, IReadOnlyList<OvertimeApprovalDto> History, OvertimeCapabilities Can,
    DateTimeOffset CreatedAt);

public sealed record CreateOvertimeRequest(Guid WorkerId, DateTimeOffset PlannedStart, DateTimeOffset PlannedEnd, string Reason, bool Submit = true);

/// <summary>Approve all or part of the request: <c>ApprovedMinutes</c> ≤ requested (default: all).</summary>
public sealed record ApproveOvertimeRequest(int? ApprovedMinutes = null, string? Note = null);

public sealed record OvertimeReasonRequest(string Reason);

public sealed record ConfirmPayableRequest(int PayableMinutes, string? Note = null);

public sealed record OvertimeListQuery(
    Guid? FactoryId = null, string? LineId = null, Guid? WorkerId = null, string? Status = null, DateOnly? From = null, DateOnly? To = null,
    string? Search = null, int Page = 1, int PageSize = 50);

// ─── Dashboard, reports, policy, readers ───────────────────────────────────

/// <summary>GET /api/attendance/statistics for one factory-local date (default: today).</summary>
public sealed record AttendanceStatisticsDto(
    DateOnly Date, Guid? FactoryId, int ScheduledWorkers, int Present, int Absent, int Late, int CheckedIn, int MissingPunches,
    int OnBreak, int OverdueBreaks, int PendingOvertimeApprovals, int CompletedOvertime, int PendingCorrections,
    int SimulatedEvents, int DeviceEvents, int ManualEvents, IReadOnlyList<BreakSessionDto> OverdueBreakList);

public sealed record AttendanceReportRow(
    Guid WorkerId, string WorkerCode, string WorkerName, string? LineId, int Sessions, int DaysPresent, int LateDays, int LateMinutes,
    int EarlyDepartureDays, int EarlyDepartureMinutes, int MissingPunches, int AttendanceMinutes, int BreakMinutes, int NetWorkingMinutes,
    int BeyondShiftMinutes, int ApprovedOvertimeMinutes, int ActualOvertimeMinutes, int PayableConfirmedMinutes, int SimulatedSessions);

public sealed record AttendanceReportDto(DateOnly From, DateOnly To, Guid? FactoryId, IReadOnlyList<AttendanceReportRow> Rows, AttendanceReportRow Totals);

public sealed record AttendanceExport(string FileName, byte[] Content, int Rows);

public sealed record AttendancePolicyDto(
    Guid FactoryId, string TimeZoneId, int DuplicateWindowSeconds, int MissingCheckOutAfterMinutes, int MaxOpenSessionHours,
    int MaxBackdateDays, int EarlyCheckInWindowMinutes, int OvertimeRoundingMinutes, int MinOvertimeMinutes,
    int MaxDailyOvertimeMinutes, int MaxWeeklyOvertimeMinutes, bool IsDefault, DateTimeOffset? UpdatedAt, string? UpdatedByEmail);

public sealed record UpdatePolicyRequest(
    string TimeZoneId, int DuplicateWindowSeconds, int MissingCheckOutAfterMinutes, int MaxOpenSessionHours, int MaxBackdateDays,
    int EarlyCheckInWindowMinutes, int OvertimeRoundingMinutes, int MinOvertimeMinutes, int MaxDailyOvertimeMinutes, int MaxWeeklyOvertimeMinutes);

public sealed record ReaderDto(Guid Id, Guid FactoryId, string Code, string Name, string Direction, string Kind, bool IsSimulated, string? Location, bool IsActive);

public sealed record CreateReaderRequest(Guid FactoryId, string Code, string Name, string Direction, string Kind, string? Location = null);

public sealed record UpdateReaderRequest(string Name, string Direction, string? Location, bool IsActive);

/// <summary>What the review sweep closed.</summary>
public sealed record ReviewSweepResult(int SessionsFlagged, int BreaksClosed, int OvertimeUpdated);

/// <summary>One worker's attendance page: shift, open session, recent sessions, breaks, overtime and corrections.</summary>
public sealed record WorkerAttendanceDto(
    Guid WorkerId, string WorkerCode, string WorkerName, Guid FactoryId, string? LineId, string WorkerStatus, string TimeZoneId,
    ShiftAssignmentDto? CurrentShift, IReadOnlyList<ShiftAssignmentDto> ShiftHistory, AttendanceSessionDto? OpenSession,
    IReadOnlyList<AttendanceSessionDto> Sessions, IReadOnlyList<BreakSessionDto> Breaks, IReadOnlyList<OvertimeDto> Overtime,
    IReadOnlyList<CorrectionDto> Corrections);
