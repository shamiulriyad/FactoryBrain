using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Attendance;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>Shifts and worker shift assignments.</summary>
[ApiController]
[Route("api")]
public class ShiftsController : ControllerBase
{
    private readonly IShiftService _shifts;
    public ShiftsController(IShiftService shifts) { _shifts = shifts; }

    [HttpGet("shifts")]
    [RequirePermission(Permissions.AttendanceView, Permissions.ShiftsManage, Permissions.ShiftsAssign)]
    public async Task<IActionResult> List([FromQuery] Guid? factoryId, [FromQuery] bool? includeInactive, CancellationToken ct)
        => AccessResults.From(this, await _shifts.ListAsync(this.Caller(), factoryId, includeInactive ?? true, ct));

    [HttpGet("shifts/{id}")]
    [RequirePermission(Permissions.AttendanceView, Permissions.ShiftsManage, Permissions.ShiftsAssign)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _shifts.GetAsync(this.Caller(), gid, ct)) : NoShift();

    [HttpPost("shifts")]
    [RequirePermission(Permissions.ShiftsManage)]
    public async Task<IActionResult> Create([FromBody] CreateShiftRequest body, CancellationToken ct)
        => AccessResults.From(this, await _shifts.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), s => $"/api/shifts/{s.Id}");

    /// <summary>PUT /api/shifts/{id} — edit, activate (isActive=true) or deactivate (false) a shift.</summary>
    [HttpPut("shifts/{id}")]
    [RequirePermission(Permissions.ShiftsManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateShiftRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _shifts.UpdateAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoShift();

    /// <summary>GET /api/shift-assignments?factoryId&amp;shiftId&amp;workerId&amp;lineId&amp;activeOn&amp;status&amp;search&amp;page&amp;pageSize</summary>
    [HttpGet("shift-assignments")]
    [RequirePermission(Permissions.AttendanceView, Permissions.ShiftsAssign)]
    public async Task<IActionResult> Assignments([FromQuery] Guid? factoryId, [FromQuery] Guid? shiftId, [FromQuery] Guid? workerId, [FromQuery] string? lineId,
        [FromQuery] DateOnly? activeOn, [FromQuery] string? status, [FromQuery] string? search, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _shifts.AssignmentsAsync(this.Caller(), new ShiftAssignmentQuery(factoryId, shiftId, workerId, lineId, activeOn, status, search, page ?? 1, pageSize ?? 50), ct));

    /// <summary>POST /api/shift-assignments — one worker, many workers, a whole line or a department.</summary>
    [HttpPost("shift-assignments")]
    [RequirePermission(Permissions.ShiftsAssign)]
    public async Task<IActionResult> Assign([FromBody] AssignShiftRequest body, CancellationToken ct)
    {
        var r = await _shifts.AssignAsync(this.Caller(), body, AccessResults.TraceId(this), ct);
        return r.Outcome == AccessOutcome.Created ? StatusCode(StatusCodes.Status201Created, r.Value) : AccessResults.From(this, r);
    }

    [HttpPost("shift-assignments/{id}/end")]
    [RequirePermission(Permissions.ShiftsAssign)]
    public async Task<IActionResult> End([FromRoute] string id, [FromBody] EndShiftAssignmentRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _shifts.EndAssignmentAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct))
            : AccessResults.Problem(this, 404, "Not found", "shift_assignment_not_found", "Shift assignment not found.");

    /// <summary>GET /api/workers/{id}/shift-assignments — a worker's shift history (own: "me").</summary>
    [HttpGet("workers/{id}/shift-assignments")]
    [RequirePermission(Permissions.AttendanceView, Permissions.SelfView)]
    public async Task<IActionResult> WorkerHistory([FromRoute] string id, CancellationToken ct)
        => AccessResults.From(this, await _shifts.WorkerAssignmentsAsync(this.Caller(), id, ct));

    private IActionResult NoShift() => AccessResults.Problem(this, 404, "Not found", "shift_not_found", "Shift not found.");
}

/// <summary>
/// Attendance: punches (manual), the session list, a worker's attendance,
/// raw events, corrections, statistics, reports, policy, readers and the
/// device ingestion endpoint. Every punch goes through the same pipeline.
/// </summary>
[ApiController]
[Route("api/attendance")]
public class AttendanceController : ControllerBase
{
    private readonly IAttendanceService _attendance;
    public AttendanceController(IAttendanceService attendance) { _attendance = attendance; }

    [HttpPost("check-in")]
    [RequirePermission(Permissions.AttendanceRecord)]
    public async Task<IActionResult> CheckIn([FromBody] PunchRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.PunchAsync(this.Caller(), AttendanceEventTypes.CheckIn, body, ct));

    [HttpPost("check-out")]
    [RequirePermission(Permissions.AttendanceRecord)]
    public async Task<IActionResult> CheckOut([FromBody] PunchRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.PunchAsync(this.Caller(), AttendanceEventTypes.CheckOut, body, ct));

    /// <summary>POST /api/attendance/events — a physical reader / gateway event (worker identified by card UID). Never used by the simulator.</summary>
    [HttpPost("events")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> DeviceEvent([FromBody] DeviceEventRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.DeviceEventAsync(this.Caller(), body, ct));

    /// <summary>
    /// GET /api/attendance?factoryId&amp;lineId&amp;departmentId&amp;workerId&amp;from&amp;to&amp;status&amp;needsReview&amp;late&amp;simulated&amp;search
    /// &amp;sort=date|worker|checkIn|late (prefix -)&amp;page&amp;pageSize
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.AttendanceView)]
    public async Task<IActionResult> List([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] Guid? departmentId, [FromQuery] Guid? workerId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? status, [FromQuery] bool? needsReview, [FromQuery] bool? late,
        [FromQuery] bool? simulated, [FromQuery] string? search, [FromQuery] string? sort, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _attendance.SessionsAsync(this.Caller(),
            new AttendanceListQuery(factoryId, lineId, departmentId, workerId, from, to, status, needsReview, late, simulated, search, sort, page ?? 1, pageSize ?? 50), ct));

    /// <summary>GET /api/attendance/statistics?factoryId&amp;lineId&amp;date — the dashboard figures (date: factory-local, default today).</summary>
    [HttpGet("statistics")]
    [RequirePermission(Permissions.AttendanceView)]
    public async Task<IActionResult> Statistics([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] DateOnly? date, CancellationToken ct)
        => Ok(await _attendance.StatisticsAsync(this.Caller(), factoryId, lineId, date, ct));

    [HttpGet("events")]
    [RequirePermission(Permissions.AttendanceView)]
    public async Task<IActionResult> Events([FromQuery] Guid? factoryId, [FromQuery] Guid? workerId, [FromQuery] bool? simulated, [FromQuery] string? source,
        [FromQuery] string? outcome, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _attendance.EventsAsync(this.Caller(), new EventListQuery(factoryId, workerId, simulated, source, outcome, from, to, page ?? 1, pageSize ?? 50), ct));

    [HttpGet("sessions/{id}")]
    [RequirePermission(Permissions.AttendanceView, Permissions.SelfView)]
    public async Task<IActionResult> Session([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _attendance.SessionAsync(this.Caller(), gid, ct)) : NoSession();

    /// <summary>POST /api/attendance/{sessionId}/correction — request a correction (applies only once someone else approves it).</summary>
    [HttpPost("{id}/correction")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> RequestCorrection([FromRoute] string id, [FromBody] CreateCorrectionRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _attendance.RequestCorrectionAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct), c => $"/api/attendance/corrections/{c.Id}")
            : NoSession();

    /// <summary>POST /api/attendance/corrections — request a session for a day without punches.</summary>
    [HttpPost("corrections")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> RequestMissing([FromBody] CreateMissingSessionRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.RequestMissingSessionAsync(this.Caller(), body, AccessResults.TraceId(this), ct), c => $"/api/attendance/corrections/{c.Id}");

    [HttpGet("corrections")]
    [RequirePermission(Permissions.AttendanceManage, Permissions.AttendanceApprove)]
    public async Task<IActionResult> Corrections([FromQuery] Guid? factoryId, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _attendance.CorrectionsAsync(this.Caller(), factoryId, status, page ?? 1, pageSize ?? 50, ct));

    [HttpPost("corrections/{id}/approve")]
    [RequirePermission(Permissions.AttendanceApprove)]
    public async Task<IActionResult> Approve([FromRoute] string id, [FromBody] DecideCorrectionRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _attendance.DecideCorrectionAsync(this.Caller(), gid, true, body, AccessResults.TraceId(this), ct)) : NoCorrection();

    [HttpPost("corrections/{id}/reject")]
    [RequirePermission(Permissions.AttendanceApprove)]
    public async Task<IActionResult> Reject([FromRoute] string id, [FromBody] DecideCorrectionRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _attendance.DecideCorrectionAsync(this.Caller(), gid, false, body, AccessResults.TraceId(this), ct)) : NoCorrection();

    /// <summary>POST /api/attendance/review/run — close stale open sessions for review now (also runs every 5 minutes).</summary>
    [HttpPost("review/run")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> Review(CancellationToken ct) => Ok(await _attendance.ReviewAsync(this.Caller(), ct));

    /// <summary>GET /api/attendance/report?from&amp;to&amp;factoryId&amp;lineId&amp;departmentId — per-worker totals.</summary>
    [HttpGet("report")]
    [RequirePermission(Permissions.AttendanceView)]
    public async Task<IActionResult> Report([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? factoryId, [FromQuery] string? lineId,
        [FromQuery] Guid? departmentId, CancellationToken ct)
        => AccessResults.From(this, await _attendance.ReportAsync(this.Caller(), factoryId, lineId, departmentId, from, to, ct));

    [HttpGet("report/export")]
    [RequirePermission(Permissions.AttendanceView)]
    public async Task<IActionResult> Export([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? factoryId, [FromQuery] string? lineId,
        [FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var r = await _attendance.ExportReportAsync(this.Caller(), factoryId, lineId, departmentId, from, to, AccessResults.TraceId(this), ct);
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Exported-Rows"] = r.Value!.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return File(r.Value.Content, "text/csv; charset=utf-8", r.Value.FileName);
    }

    [HttpGet("policy")]
    [RequirePermission(Permissions.AttendanceView, Permissions.ShiftsManage)]
    public async Task<IActionResult> Policy([FromQuery] Guid factoryId, CancellationToken ct) => AccessResults.From(this, await _attendance.PolicyAsync(this.Caller(), factoryId, ct));

    [HttpPut("policy")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> UpdatePolicy([FromQuery] Guid factoryId, [FromBody] UpdatePolicyRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.UpdatePolicyAsync(this.Caller(), factoryId, body, AccessResults.TraceId(this), ct));

    [HttpGet("readers")]
    [RequirePermission(Permissions.AttendanceManage, Permissions.DevicesView)]
    public async Task<IActionResult> Readers([FromQuery] Guid? factoryId, CancellationToken ct) => AccessResults.From(this, await _attendance.ReadersAsync(this.Caller(), factoryId, false, ct));

    [HttpPost("readers")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> CreateReader([FromBody] CreateReaderRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.CreateReaderAsync(this.Caller(), body, AccessResults.TraceId(this), ct), r => $"/api/attendance/readers/{r.Id}");

    [HttpPut("readers/{id}")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> UpdateReader([FromRoute] string id, [FromBody] UpdateReaderRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _attendance.UpdateReaderAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct))
            : AccessResults.Problem(this, 404, "Not found", "reader_not_found", "Reader not found.");

    /// <summary>GET /api/attendance/{workerId} — one worker's attendance (id, Worker ID or "me"); ?from&amp;to (work dates).</summary>
    [HttpGet("{workerId}")]
    [RequirePermission(Permissions.AttendanceView, Permissions.SelfView)]
    public async Task<IActionResult> Worker([FromRoute] string workerId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => AccessResults.From(this, await _attendance.WorkerAsync(this.Caller(), workerId, from, to, ct));

    private IActionResult NoSession() => AccessResults.Problem(this, 404, "Not found", "session_not_found", "Attendance session not found.");
    private IActionResult NoCorrection() => AccessResults.Problem(this, 404, "Not found", "correction_not_found", "Correction not found.");
}

/// <summary>Breaks: start, end, list (active / overdue / history).</summary>
[ApiController]
[Route("api/breaks")]
public class BreaksController : ControllerBase
{
    private readonly IAttendanceService _attendance;
    public BreaksController(IAttendanceService attendance) { _attendance = attendance; }

    [HttpPost("start")]
    [RequirePermission(Permissions.AttendanceRecord)]
    public async Task<IActionResult> Start([FromBody] BreakPunchRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.BreakAsync(this.Caller(), AttendanceEventTypes.BreakStart, body, ct));

    [HttpPost("end")]
    [RequirePermission(Permissions.AttendanceRecord)]
    public async Task<IActionResult> End([FromBody] BreakPunchRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.BreakAsync(this.Caller(), AttendanceEventTypes.BreakEnd, body, ct));

    /// <summary>GET /api/breaks?factoryId&amp;lineId&amp;workerId&amp;active&amp;overdue&amp;from&amp;to&amp;page&amp;pageSize</summary>
    [HttpGet]
    [RequirePermission(Permissions.AttendanceView)]
    public async Task<IActionResult> List([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] Guid? workerId, [FromQuery] bool? active,
        [FromQuery] bool? overdue, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _attendance.BreaksAsync(this.Caller(), new BreakListQuery(factoryId, lineId, workerId, active, overdue, from, to, page ?? 1, pageSize ?? 50), ct));
}

/// <summary>Overtime requests and their review.</summary>
[ApiController]
[Route("api/overtime")]
public class OvertimeController : ControllerBase
{
    private readonly IOvertimeService _overtime;
    public OvertimeController(IOvertimeService overtime) { _overtime = overtime; }

    [HttpGet]
    [RequirePermission(Permissions.OvertimeView, Permissions.OvertimeApprove)]
    public async Task<IActionResult> List([FromQuery] Guid? factoryId, [FromQuery] string? lineId, [FromQuery] Guid? workerId, [FromQuery] string? status,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? search, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _overtime.ListAsync(this.Caller(), new OvertimeListQuery(factoryId, lineId, workerId, status, from, to, search, page ?? 1, pageSize ?? 50), ct));

    [HttpGet("{id}")]
    [RequirePermission(Permissions.OvertimeView, Permissions.OvertimeApprove, Permissions.SelfView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _overtime.GetAsync(this.Caller(), gid, ct)) : NoRequest();

    [HttpPost("request")]
    [RequirePermission(Permissions.OvertimeManage)]
    public async Task<IActionResult> Create([FromBody] CreateOvertimeRequest body, CancellationToken ct)
        => AccessResults.From(this, await _overtime.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), o => $"/api/overtime/{o.Id}");

    [HttpPost("{id}/submit")]
    [RequirePermission(Permissions.OvertimeManage)]
    public async Task<IActionResult> Submit([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _overtime.SubmitAsync(this.Caller(), gid, AccessResults.TraceId(this), ct)) : NoRequest();

    [HttpPost("{id}/approve")]
    [RequirePermission(Permissions.OvertimeApprove)]
    public async Task<IActionResult> Approve([FromRoute] string id, [FromBody] ApproveOvertimeRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _overtime.ApproveAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoRequest();

    [HttpPost("{id}/reject")]
    [RequirePermission(Permissions.OvertimeApprove)]
    public async Task<IActionResult> Reject([FromRoute] string id, [FromBody] OvertimeReasonRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _overtime.RejectAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoRequest();

    [HttpPost("{id}/cancel")]
    [RequirePermission(Permissions.OvertimeManage)]
    public async Task<IActionResult> Cancel([FromRoute] string id, [FromBody] OvertimeReasonRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _overtime.CancelAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoRequest();

    /// <summary>POST /api/overtime/{id}/confirm-payable — HR review of completed overtime (never automatic).</summary>
    [HttpPost("{id}/confirm-payable")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> ConfirmPayable([FromRoute] string id, [FromBody] ConfirmPayableRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _overtime.ConfirmPayableAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoRequest();

    private IActionResult NoRequest() => AccessResults.Problem(this, 404, "Not found", "overtime_not_found", "Overtime request not found.");
}

/// <summary>
/// The software RFID simulator for demonstrations. Events go through the same
/// pipeline as physical readers but are stored and shown as SIMULATED.
/// </summary>
[ApiController]
[Route("api/simulator/rfid")]
public class RfidSimulatorController : ControllerBase
{
    private readonly IAttendanceService _attendance;
    public RfidSimulatorController(IAttendanceService attendance) { _attendance = attendance; }

    [HttpGet("readers")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> Readers([FromQuery] Guid? factoryId, CancellationToken ct) => AccessResults.From(this, await _attendance.ReadersAsync(this.Caller(), factoryId, true, ct));

    [HttpGet("events")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> Events([FromQuery] Guid? factoryId, [FromQuery] Guid? workerId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _attendance.EventsAsync(this.Caller(), new EventListQuery(factoryId, workerId, true, null, null, null, null, page ?? 1, pageSize ?? 50), ct));

    [HttpPost("check-in")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> CheckIn([FromBody] SimulatorPunchRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.SimulateAsync(this.Caller(), AttendanceEventTypes.CheckIn, body, ct));

    [HttpPost("check-out")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> CheckOut([FromBody] SimulatorPunchRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.SimulateAsync(this.Caller(), AttendanceEventTypes.CheckOut, body, ct));

    [HttpPost("break-start")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> BreakStart([FromBody] SimulatorPunchRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.SimulateAsync(this.Caller(), AttendanceEventTypes.BreakStart, body, ct));

    [HttpPost("break-end")]
    [RequirePermission(Permissions.AttendanceManage)]
    public async Task<IActionResult> BreakEnd([FromBody] SimulatorPunchRequest body, CancellationToken ct)
        => AccessResults.From(this, await _attendance.SimulateAsync(this.Caller(), AttendanceEventTypes.BreakEnd, body, ct));
}
