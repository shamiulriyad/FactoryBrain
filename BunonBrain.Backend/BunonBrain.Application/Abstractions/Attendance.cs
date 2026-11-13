using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Attendance;

namespace BunonBrain.Application.Abstractions.Interfaces;

/// <summary>Shifts and worker shift assignments (history kept; no overlapping assignments per worker).</summary>
public interface IShiftService
{
    Task<AccessResult<IReadOnlyList<ShiftDto>>> ListAsync(AccessContext caller, Guid? factoryId, bool includeInactive, CancellationToken ct);
    Task<AccessResult<ShiftDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<ShiftDto>> CreateAsync(AccessContext caller, CreateShiftRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<ShiftDto>> UpdateAsync(AccessContext caller, Guid id, UpdateShiftRequest request, string? traceId, CancellationToken ct);

    Task<PagedResult<ShiftAssignmentDto>> AssignmentsAsync(AccessContext caller, ShiftAssignmentQuery query, CancellationToken ct);
    Task<AccessResult<IReadOnlyList<ShiftAssignmentDto>>> WorkerAssignmentsAsync(AccessContext caller, string workerIdOrCode, CancellationToken ct);
    Task<AccessResult<AssignShiftResult>> AssignAsync(AccessContext caller, AssignShiftRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<ShiftAssignmentDto>> EndAssignmentAsync(AccessContext caller, Guid id, EndShiftAssignmentRequest request, string? traceId, CancellationToken ct);
}

/// <summary>
/// Attendance: the one event pipeline (manual punches, the RFID simulator and
/// physical readers all go through <c>ProcessAsync</c>), the derived sessions
/// and breaks, corrections, the review sweep, statistics, reports, policy and readers.
/// </summary>
public interface IAttendanceService
{
    Task<AccessResult<EventResult>> PunchAsync(AccessContext caller, string eventType, PunchRequest request, CancellationToken ct);
    Task<AccessResult<EventResult>> BreakAsync(AccessContext caller, string eventType, BreakPunchRequest request, CancellationToken ct);
    Task<AccessResult<EventResult>> SimulateAsync(AccessContext caller, string eventType, SimulatorPunchRequest request, CancellationToken ct);
    Task<AccessResult<EventResult>> DeviceEventAsync(AccessContext caller, DeviceEventRequest request, CancellationToken ct);

    Task<PagedResult<AttendanceSessionDto>> SessionsAsync(AccessContext caller, AttendanceListQuery query, CancellationToken ct);
    Task<AccessResult<AttendanceSessionDto>> SessionAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<WorkerAttendanceDto>> WorkerAsync(AccessContext caller, string workerIdOrCode, DateOnly? from, DateOnly? to, CancellationToken ct);
    Task<PagedResult<AttendanceEventDto>> EventsAsync(AccessContext caller, EventListQuery query, CancellationToken ct);
    Task<PagedResult<BreakSessionDto>> BreaksAsync(AccessContext caller, BreakListQuery query, CancellationToken ct);

    Task<AccessResult<CorrectionDto>> RequestCorrectionAsync(AccessContext caller, Guid sessionId, CreateCorrectionRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<CorrectionDto>> RequestMissingSessionAsync(AccessContext caller, CreateMissingSessionRequest request, string? traceId, CancellationToken ct);
    Task<PagedResult<CorrectionDto>> CorrectionsAsync(AccessContext caller, Guid? factoryId, string? status, int page, int pageSize, CancellationToken ct);
    Task<AccessResult<CorrectionDto>> DecideCorrectionAsync(AccessContext caller, Guid id, bool approve, DecideCorrectionRequest request, string? traceId, CancellationToken ct);

    /// <summary>Closes stale open sessions and breaks for review and updates overtime progress. Safe to run any time.</summary>
    Task<ReviewSweepResult> ReviewAsync(AccessContext? caller, CancellationToken ct);

    Task<AttendanceStatisticsDto> StatisticsAsync(AccessContext caller, Guid? factoryId, string? lineId, DateOnly? date, CancellationToken ct);
    Task<AccessResult<AttendanceReportDto>> ReportAsync(AccessContext caller, Guid? factoryId, string? lineId, Guid? departmentId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<AccessResult<AttendanceExport>> ExportReportAsync(AccessContext caller, Guid? factoryId, string? lineId, Guid? departmentId, DateOnly from, DateOnly to, string? traceId, CancellationToken ct);

    Task<AccessResult<AttendancePolicyDto>> PolicyAsync(AccessContext caller, Guid factoryId, CancellationToken ct);
    Task<AccessResult<AttendancePolicyDto>> UpdatePolicyAsync(AccessContext caller, Guid factoryId, UpdatePolicyRequest request, string? traceId, CancellationToken ct);

    Task<AccessResult<IReadOnlyList<ReaderDto>>> ReadersAsync(AccessContext caller, Guid? factoryId, bool simulatedOnly, CancellationToken ct);
    Task<AccessResult<ReaderDto>> CreateReaderAsync(AccessContext caller, CreateReaderRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<ReaderDto>> UpdateReaderAsync(AccessContext caller, Guid id, UpdateReaderRequest request, string? traceId, CancellationToken ct);
}

/// <summary>Overtime requests: request → approval → actual (from attendance) → HR-confirmed payable minutes.</summary>
public interface IOvertimeService
{
    Task<PagedResult<OvertimeDto>> ListAsync(AccessContext caller, OvertimeListQuery query, CancellationToken ct);
    Task<AccessResult<OvertimeDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<OvertimeDto>> CreateAsync(AccessContext caller, CreateOvertimeRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<OvertimeDto>> SubmitAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct);
    Task<AccessResult<OvertimeDto>> ApproveAsync(AccessContext caller, Guid id, ApproveOvertimeRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<OvertimeDto>> RejectAsync(AccessContext caller, Guid id, OvertimeReasonRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<OvertimeDto>> CancelAsync(AccessContext caller, Guid id, OvertimeReasonRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<OvertimeDto>> ConfirmPayableAsync(AccessContext caller, Guid id, ConfirmPayableRequest request, string? traceId, CancellationToken ct);
}
