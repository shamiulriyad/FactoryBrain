using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Assignments;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Worker ↔ workstation assignments. Reading needs <c>workers.view</c> (a
/// worker reads their own history with <c>self.view</c>); assigning,
/// transferring and ending need <c>workers.assign</c>. The service enforces
/// factory and production-line scope on every call. An assignment records
/// where a worker is scheduled to work; it is not evidence of presence.
/// </summary>
[ApiController]
[Route("api")]
public class AssignmentsController : ControllerBase
{
    private readonly IAssignmentService _assignments;
    public AssignmentsController(IAssignmentService assignments) { _assignments = assignments; }

    /// <summary>
    /// GET /api/assignments?factoryId&amp;departmentId&amp;floorId&amp;lineId&amp;workstationId&amp;workerId&amp;status=Active|Ended|Transferred
    /// &amp;search&amp;from&amp;to&amp;page&amp;pageSize — newest first.
    /// </summary>
    [HttpGet("assignments")]
    [RequirePermission(Permissions.WorkersView)]
    public async Task<IActionResult> List([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId,
        [FromQuery] string? lineId, [FromQuery] Guid? workstationId, [FromQuery] Guid? workerId, [FromQuery] string? status,
        [FromQuery] string? search, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _assignments.ListAsync(this.Caller(),
            new AssignmentListQuery(factoryId, departmentId, floorId, lineId, workstationId, workerId, status, search, from, to, page ?? 1, pageSize ?? 50), ct));

    /// <summary>GET /api/assignments/active — the current assignments (same filters as the list).</summary>
    [HttpGet("assignments/active")]
    [RequirePermission(Permissions.WorkersView)]
    public async Task<IActionResult> Active([FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId, [FromQuery] Guid? floorId,
        [FromQuery] string? lineId, [FromQuery] Guid? workstationId, [FromQuery] string? search,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _assignments.ListAsync(this.Caller(),
            new AssignmentListQuery(factoryId, departmentId, floorId, lineId, workstationId, null, "Active", search, null, null, page ?? 1, pageSize ?? 200), ct));

    /// <summary>GET /api/assignments/statistics?factoryId&amp;lineId — occupancy, unassigned workers, recent transfers.</summary>
    [HttpGet("assignments/statistics")]
    [RequirePermission(Permissions.WorkersView)]
    public async Task<IActionResult> Statistics([FromQuery] Guid? factoryId, [FromQuery] string? lineId, CancellationToken ct)
        => Ok(await _assignments.StatisticsAsync(this.Caller(), factoryId, lineId, ct));

    [HttpGet("assignments/{id}")]
    [RequirePermission(Permissions.WorkersView, Permissions.SelfView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _assignments.GetAsync(this.Caller(), gid, ct)) : NotFoundAssignment();

    /// <summary>POST /api/assignments — assign an active, currently unassigned worker to a workstation.</summary>
    [HttpPost("assignments")]
    [RequirePermission(Permissions.WorkersAssign)]
    public async Task<IActionResult> Create([FromBody] CreateAssignmentRequest body, CancellationToken ct)
        => AccessResults.From(this, await _assignments.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), a => $"/api/assignments/{a.Id}");

    /// <summary>POST /api/assignments/{id}/transfer — close this assignment and open one at another workstation at the same instant.</summary>
    [HttpPost("assignments/{id}/transfer")]
    [RequirePermission(Permissions.WorkersAssign)]
    public async Task<IActionResult> Transfer([FromRoute] string id, [FromBody] TransferAssignmentRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _assignments.TransferAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct))
            : NotFoundAssignment();

    /// <summary>POST /api/assignments/{id}/end — end an assignment without a successor.</summary>
    [HttpPost("assignments/{id}/end")]
    [RequirePermission(Permissions.WorkersAssign)]
    public async Task<IActionResult> End([FromRoute] string id, [FromBody] EndAssignmentRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _assignments.EndAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct))
            : NotFoundAssignment();

    /// <summary>GET /api/workers/{id}/assignments — a worker's full assignment timeline (id, Worker ID, or "me").</summary>
    [HttpGet("workers/{id}/assignments")]
    [RequirePermission(Permissions.WorkersView, Permissions.SelfView)]
    public async Task<IActionResult> WorkerHistory([FromRoute] string id, CancellationToken ct)
        => AccessResults.From(this, await _assignments.WorkerHistoryAsync(this.Caller(), id, ct));

    /// <summary>GET /api/workstations/{id}/assignments?limit — who has been assigned to this workstation.</summary>
    [HttpGet("workstations/{id}/assignments")]
    [RequirePermission(Permissions.WorkersView)]
    public async Task<IActionResult> WorkstationHistory([FromRoute] string id, [FromQuery] int? limit, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _assignments.WorkstationHistoryAsync(this.Caller(), gid, limit ?? 100, ct))
            : AccessResults.Problem(this, 404, "Not found", "workstation_not_found", "Workstation not found.");

    private IActionResult NotFoundAssignment() => AccessResults.Problem(this, 404, "Not found", "assignment_not_found", "Assignment not found.");
}

/// <summary>
/// Workstations (tables) of the production lines and their capacity. Reading
/// is open to everyone who works with workers or lines in scope (assigned
/// workers are shown only with <c>workers.view</c>); configuring needs
/// <c>lines.manage</c> and factory-wide reach.
/// </summary>
[ApiController]
[Route("api/workstations")]
public class WorkstationsController : ControllerBase
{
    private readonly IWorkstationService _workstations;
    public WorkstationsController(IWorkstationService workstations) { _workstations = workstations; }

    /// <summary>GET /api/workstations?search&amp;factoryId&amp;departmentId&amp;floorId&amp;lineId&amp;occupancy=Available|Occupied|CapacityReached|Inactive&amp;includeInactive&amp;page&amp;pageSize</summary>
    [HttpGet]
    [RequirePermission(Permissions.WorkersView, Permissions.LinesView, Permissions.WorkersAssign)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId,
        [FromQuery] Guid? floorId, [FromQuery] string? lineId, [FromQuery] string? occupancy, [FromQuery] bool? includeInactive,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _workstations.ListAsync(this.Caller(),
            new WorkstationListQuery(search, factoryId, departmentId, floorId, lineId, occupancy, includeInactive ?? true, page ?? 1, pageSize ?? 50), ct));

    [HttpGet("{id}")]
    [RequirePermission(Permissions.WorkersView, Permissions.LinesView, Permissions.WorkersAssign)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _workstations.GetAsync(this.Caller(), gid, ct)) : NotFoundWorkstation();

    [HttpPost]
    [RequirePermission(Permissions.LinesManage)]
    public async Task<IActionResult> Create([FromBody] CreateWorkstationRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workstations.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), w => $"/api/workstations/{w.Id}");

    /// <summary>POST /api/workstations/bulk — create a numbered series (e.g. T-001 … T-030) on one line; existing codes are skipped.</summary>
    [HttpPost("bulk")]
    [RequirePermission(Permissions.LinesManage)]
    public async Task<IActionResult> BulkCreate([FromBody] BulkCreateWorkstationsRequest body, CancellationToken ct)
    {
        var result = await _workstations.BulkCreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct);
        return result.Outcome == Application.Dtos.Access.AccessOutcome.Created ? StatusCode(StatusCodes.Status201Created, result.Value) : AccessResults.From(this, result);
    }

    [HttpPut("{id}")]
    [RequirePermission(Permissions.LinesManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateWorkstationRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _workstations.UpdateAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct))
            : NotFoundWorkstation();

    private IActionResult NotFoundWorkstation() => AccessResults.Problem(this, 404, "Not found", "workstation_not_found", "Workstation not found.");
}

/// <summary>Machines and where they stand. Configuring: <c>lines.manage</c> or <c>devices.manage</c>, with factory-wide reach.</summary>
[ApiController]
[Route("api/machines")]
public class MachinesController : ControllerBase
{
    private readonly IWorkstationService _workstations;
    public MachinesController(IWorkstationService workstations) { _workstations = workstations; }

    /// <summary>GET /api/machines?factoryId&amp;workstationId&amp;unplaced&amp;search</summary>
    [HttpGet]
    [RequirePermission(Permissions.LinesView, Permissions.DevicesView, Permissions.WorkersAssign)]
    public async Task<IActionResult> List([FromQuery] Guid? factoryId, [FromQuery] Guid? workstationId, [FromQuery] bool? unplaced,
        [FromQuery] string? search, CancellationToken ct)
        => AccessResults.From(this, await _workstations.MachinesAsync(this.Caller(), new MachineListQuery(factoryId, workstationId, unplaced, search), ct));

    [HttpPost]
    [RequirePermission(Permissions.LinesManage, Permissions.DevicesManage)]
    public async Task<IActionResult> Create([FromBody] CreateMachineRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workstations.CreateMachineAsync(this.Caller(), body, AccessResults.TraceId(this), ct), m => $"/api/machines/{m.Id}");

    [HttpPut("{id}")]
    [RequirePermission(Permissions.LinesManage, Permissions.DevicesManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateMachineRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _workstations.UpdateMachineAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct))
            : AccessResults.Problem(this, 404, "Not found", "machine_not_found", "Machine not found.");
}
