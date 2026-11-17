using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Planning;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Production planning &amp; target management (<c>/api/planning</c>). Orders and styles live under
/// <c>/api/production</c> (this module extends them). Every route checks a planning permission here and
/// the service enforces factory and line scope on every record: a Factory Manager reaches their
/// factories, a Production Supervisor only their own lines, and people who only follow the plan
/// (monitoring operators) see published plans, never drafts.
/// <list type="bullet">
///   <item>View: context, allocations, targets, capacity, published plans, monitoring, exceptions, reports.</item>
///   <item>Allocations: <c>planning.allocations.manage</c>. Targets: <c>planning.targets.manage</c>, decided by <c>planning.plans.approve</c>.</item>
///   <item>Daily plans: prepare <c>planning.plans.manage</c>, approve / return <c>planning.plans.approve</c>, publish <c>planning.plans.publish</c>.</item>
///   <item>Setup (output counters, capacity assumptions): <c>planning.setup</c>. Exceptions: <c>planning.exceptions.review</c>. Export: <c>planning.reports.export</c>.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/planning")]
public class PlanningController : ControllerBase
{
    private readonly IPlanningSetupService _setup;
    private readonly IAllocationService _allocations;
    private readonly ITargetService _targets;
    private readonly ICapacityService _capacity;
    private readonly IDailyPlanService _plans;
    private readonly IPlanningMonitorService _monitor;
    private readonly IProductionExceptionService _exceptions;
    private readonly IPlanningReports _reports;

    public PlanningController(IPlanningSetupService setup, IAllocationService allocations, ITargetService targets, ICapacityService capacity, IDailyPlanService plans,
        IPlanningMonitorService monitor, IProductionExceptionService exceptions, IPlanningReports reports)
    {
        _setup = setup; _allocations = allocations; _targets = targets; _capacity = capacity; _plans = plans; _monitor = monitor; _exceptions = exceptions; _reports = reports;
    }

    private string? Trace => AccessResults.TraceId(this);

    private IActionResult BadId(string what) => AccessResults.Problem(this, StatusCodes.Status404NotFound, "Not found", $"{what}_not_found", $"{what} not found.");

    // ── Context & setup ─────────────────────────────────────────────────────

    [HttpGet("factories")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Factories(CancellationToken ct) => AccessResults.From(this, await _setup.FactoriesAsync(this.Caller(), ct));

    [HttpGet("context")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Context([FromQuery] Guid? factoryId, CancellationToken ct) => AccessResults.From(this, await _setup.ContextAsync(this.Caller(), factoryId, ct));

    [HttpGet("lines/{lineId}/capacity-settings")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> GetCapacitySettings([FromRoute] string lineId, CancellationToken ct) => AccessResults.From(this, await _setup.GetCapacitySettingsAsync(this.Caller(), lineId, ct));

    [HttpPut("lines/{lineId}/capacity-settings")]
    [RequirePermission(Permissions.PlanningSetup)]
    public async Task<IActionResult> SaveCapacitySettings([FromRoute] string lineId, [FromBody] CapacitySettingsWrite body, CancellationToken ct) =>
        AccessResults.From(this, await _setup.SaveCapacitySettingsAsync(this.Caller(), lineId, body, Trace, ct));

    [HttpGet("output-points")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> OutputPoints([FromQuery] Guid? factoryId, [FromQuery] string? lineId, CancellationToken ct) =>
        AccessResults.From(this, await _setup.OutputPointsAsync(this.Caller(), factoryId, lineId, ct));

    [HttpPost("output-points")]
    [RequirePermission(Permissions.PlanningSetup)]
    public async Task<IActionResult> AddOutputPoint([FromBody] OutputPointWrite body, CancellationToken ct) =>
        AccessResults.From(this, await _setup.AddOutputPointAsync(this.Caller(), body, Trace, ct), p => $"/api/planning/output-points?factoryId={p.FactoryId}");

    [HttpDelete("output-points/{id}")]
    [RequirePermission(Permissions.PlanningSetup)]
    public async Task<IActionResult> RemoveOutputPoint([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _setup.RemoveOutputPointAsync(this.Caller(), g, Trace, ct)) : BadId("output_point");

    // ── Allocations ─────────────────────────────────────────────────────────

    [HttpGet("allocations")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Allocations([FromQuery] Guid? factoryId, [FromQuery] Guid? orderId, [FromQuery] string? lineId, [FromQuery] string? status,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        AccessResults.From(this, await _allocations.ListAsync(this.Caller(), new AllocationQuery(factoryId, orderId, lineId, status, from, to, page ?? 1, pageSize ?? 50), ct));

    [HttpGet("orders/{orderId}/allocations")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> OrderAllocations([FromRoute] string orderId, CancellationToken ct) =>
        Guid.TryParse(orderId, out var g) ? AccessResults.From(this, await _allocations.ForOrderAsync(this.Caller(), g, ct)) : BadId("order");

    [HttpGet("orders/{orderId}/progress")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> OrderProgress([FromRoute] string orderId, [FromQuery] string? source, CancellationToken ct) =>
        Guid.TryParse(orderId, out var g) ? AccessResults.From(this, await _monitor.OrderProgressAsync(this.Caller(), g, source, ct)) : BadId("order");

    [HttpGet("allocations/{id}")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Allocation([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _allocations.GetAsync(this.Caller(), g, ct)) : BadId("allocation");

    [HttpPost("allocations")]
    [RequirePermission(Permissions.PlanningAllocationsManage)]
    public async Task<IActionResult> CreateAllocation([FromBody] AllocationWrite body, CancellationToken ct) =>
        AccessResults.From(this, await _allocations.CreateAsync(this.Caller(), body, Trace, ct), a => $"/api/planning/allocations/{a.Allocation.Id}");

    [HttpPut("allocations/{id}")]
    [RequirePermission(Permissions.PlanningAllocationsManage)]
    public async Task<IActionResult> UpdateAllocation([FromRoute] string id, [FromBody] AllocationWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _allocations.UpdateAsync(this.Caller(), g, body, Trace, ct)) : BadId("allocation");

    [HttpPost("allocations/{id}/status")]
    [RequirePermission(Permissions.PlanningAllocationsManage)]
    public async Task<IActionResult> SetAllocationStatus([FromRoute] string id, [FromBody] AllocationStatusRequest body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _allocations.SetStatusAsync(this.Caller(), g, body, Trace, ct)) : BadId("allocation");

    // ── Targets ─────────────────────────────────────────────────────────────

    [HttpGet("targets")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Targets([FromQuery] Guid? factoryId, [FromQuery] string? level, [FromQuery] string? lineId, [FromQuery] Guid? orderId, [FromQuery] DateOnly? date,
        [FromQuery] bool? includeHistory, [FromQuery] bool? withPerformance, [FromQuery] string? approval, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        AccessResults.From(this, await _targets.ListAsync(this.Caller(), new TargetQuery(factoryId, level, lineId, orderId, date, includeHistory ?? false, withPerformance ?? false, approval, page ?? 1, pageSize ?? 50), ct));

    [HttpGet("targets/effective")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> EffectiveTarget([FromQuery] Guid factoryId, [FromQuery] string lineId, [FromQuery] Guid shiftId, [FromQuery] Guid? orderId, [FromQuery] DateOnly date, CancellationToken ct) =>
        AccessResults.From(this, await _targets.EffectiveAsync(this.Caller(), factoryId, lineId, shiftId, orderId, date, ct));

    [HttpGet("targets/{id}")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Target([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _targets.GetAsync(this.Caller(), g, ct)) : BadId("target");

    [HttpPost("targets")]
    [RequirePermission(Permissions.PlanningTargetsManage)]
    public async Task<IActionResult> CreateTarget([FromBody] TargetWrite body, CancellationToken ct) =>
        AccessResults.From(this, await _targets.CreateAsync(this.Caller(), body, Trace, ct), t => $"/api/planning/targets/{t.Target.Id}");

    [HttpPost("targets/{id}/revise")]
    [RequirePermission(Permissions.PlanningTargetsManage)]
    public async Task<IActionResult> ReviseTarget([FromRoute] string id, [FromBody] TargetWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _targets.ReviseAsync(this.Caller(), g, body, Trace, ct), t => $"/api/planning/targets/{t.Target.Id}") : BadId("target");

    [HttpPost("targets/{id}/decision")]
    [RequirePermission(Permissions.PlanningPlansApprove)]
    public async Task<IActionResult> DecideTarget([FromRoute] string id, [FromBody] TargetDecision body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _targets.DecideAsync(this.Caller(), g, body, Trace, ct)) : BadId("target");

    [HttpPost("targets/{id}/end")]
    [RequirePermission(Permissions.PlanningTargetsManage)]
    public async Task<IActionResult> EndTarget([FromRoute] string id, [FromBody] TargetEnd body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _targets.EndAsync(this.Caller(), g, body, Trace, ct)) : BadId("target");

    // ── Capacity ────────────────────────────────────────────────────────────

    [HttpGet("capacity")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Capacity([FromQuery] string lineId, [FromQuery] Guid styleId, [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] int? quantity,
        [FromQuery] Guid? excludeAllocationId, CancellationToken ct) =>
        AccessResults.From(this, await _capacity.EstimateAsync(this.Caller(), lineId, styleId, from, to, quantity ?? 0, excludeAllocationId, ct));

    [HttpGet("capacity/overview")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> CapacityOverview([FromQuery] Guid factoryId, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct) =>
        AccessResults.From(this, await _capacity.OverviewAsync(this.Caller(), factoryId, from, to, ct));

    [HttpGet("allocations/{id}/capacity")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> AllocationCapacity([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _capacity.ForAllocationAsync(this.Caller(), g, ct)) : BadId("allocation");

    // ── Daily plans ─────────────────────────────────────────────────────────

    [HttpGet("daily-plans")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Plans([FromQuery] Guid? factoryId, [FromQuery] string? status, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? page,
        [FromQuery] int? pageSize, CancellationToken ct) =>
        AccessResults.From(this, await _plans.ListAsync(this.Caller(), new DailyPlanQuery(factoryId, status, from, to, page ?? 1, pageSize ?? 30), ct));

    [HttpGet("daily-plans/for-date")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> PlanForDate([FromQuery] Guid factoryId, [FromQuery] DateOnly date, [FromQuery] string? source, CancellationToken ct) =>
        AccessResults.From(this, await _plans.ForDateAsync(this.Caller(), factoryId, date, source, ct));

    [HttpGet("daily-plans/{id}")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Plan([FromRoute] string id, [FromQuery] string? source, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.GetAsync(this.Caller(), g, source, ct)) : BadId("plan");

    [HttpPost("daily-plans")]
    [RequirePermission(Permissions.PlanningPlansManage)]
    public async Task<IActionResult> CreatePlan([FromBody] DailyPlanWrite body, CancellationToken ct) =>
        AccessResults.From(this, await _plans.CreateAsync(this.Caller(), body, Trace, ct), p => $"/api/planning/daily-plans/{p.Id}");

    [HttpPut("daily-plans/{id}")]
    [RequirePermission(Permissions.PlanningPlansManage)]
    public async Task<IActionResult> UpdatePlan([FromRoute] string id, [FromBody] DailyPlanNotesWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.UpdateNotesAsync(this.Caller(), g, body, Trace, ct)) : BadId("plan");

    [HttpPost("daily-plans/{id}/items")]
    [RequirePermission(Permissions.PlanningPlansManage)]
    public async Task<IActionResult> AddItem([FromRoute] string id, [FromBody] PlanItemWrite body, [FromQuery] uint? expectedVersion, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.AddItemAsync(this.Caller(), g, body, expectedVersion, Trace, ct)) : BadId("plan");

    [HttpPut("daily-plans/{id}/items/{itemId}")]
    [RequirePermission(Permissions.PlanningPlansManage)]
    public async Task<IActionResult> UpdateItem([FromRoute] string id, [FromRoute] string itemId, [FromBody] PlanItemWrite body, [FromQuery] uint? expectedVersion, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && Guid.TryParse(itemId, out var i) ? AccessResults.From(this, await _plans.UpdateItemAsync(this.Caller(), g, i, body, expectedVersion, Trace, ct)) : BadId("plan_item");

    [HttpDelete("daily-plans/{id}/items/{itemId}")]
    [RequirePermission(Permissions.PlanningPlansManage)]
    public async Task<IActionResult> RemoveItem([FromRoute] string id, [FromRoute] string itemId, [FromQuery] uint? expectedVersion, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && Guid.TryParse(itemId, out var i) ? AccessResults.From(this, await _plans.RemoveItemAsync(this.Caller(), g, i, expectedVersion, Trace, ct)) : BadId("plan_item");

    [HttpPost("daily-plans/{id}/submit")]
    [RequirePermission(Permissions.PlanningPlansManage)]
    public async Task<IActionResult> Submit([FromRoute] string id, [FromBody] DailyPlanTransition body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.SubmitAsync(this.Caller(), g, body, Trace, ct)) : BadId("plan");

    [HttpPost("daily-plans/{id}/approve")]
    [RequirePermission(Permissions.PlanningPlansApprove)]
    public async Task<IActionResult> Approve([FromRoute] string id, [FromBody] DailyPlanTransition body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.ApproveAsync(this.Caller(), g, body, Trace, ct)) : BadId("plan");

    [HttpPost("daily-plans/{id}/return")]
    [RequirePermission(Permissions.PlanningPlansApprove)]
    public async Task<IActionResult> Return([FromRoute] string id, [FromBody] DailyPlanTransition body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.ReturnAsync(this.Caller(), g, body, Trace, ct)) : BadId("plan");

    [HttpPost("daily-plans/{id}/publish")]
    [RequirePermission(Permissions.PlanningPlansPublish)]
    public async Task<IActionResult> Publish([FromRoute] string id, [FromBody] DailyPlanTransition body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.PublishAsync(this.Caller(), g, body, Trace, ct)) : BadId("plan");

    [HttpPost("daily-plans/{id}/revise")]
    [RequirePermission(Permissions.PlanningPlansManage)]
    public async Task<IActionResult> Revise([FromRoute] string id, [FromBody] DailyPlanTransition body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.ReviseAsync(this.Caller(), g, body, Trace, ct), p => $"/api/planning/daily-plans/{p.Id}") : BadId("plan");

    [HttpPost("daily-plans/{id}/cancel")]
    [RequirePermission(Permissions.PlanningPlansManage)]
    public async Task<IActionResult> CancelPlan([FromRoute] string id, [FromBody] DailyPlanTransition body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _plans.CancelAsync(this.Caller(), g, body, Trace, ct)) : BadId("plan");

    // ── Monitoring ──────────────────────────────────────────────────────────

    [HttpGet("monitor")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Monitor([FromQuery] Guid? factoryId, [FromQuery] DateOnly? date, [FromQuery] string? source, [FromQuery] Guid? departmentId,
        [FromQuery] Guid? floorId, [FromQuery] string? lineId, CancellationToken ct) =>
        AccessResults.From(this, await _monitor.MonitorAsync(this.Caller(), new MonitorQuery(factoryId, date, source, departmentId, floorId, lineId), ct));

    // ── Exceptions ──────────────────────────────────────────────────────────

    [HttpGet("exceptions")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Exceptions([FromQuery] Guid? factoryId, [FromQuery] string? status, [FromQuery] string? type, [FromQuery] string? severity, [FromQuery] string? lineId,
        [FromQuery] Guid? orderId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        AccessResults.From(this, await _exceptions.ListAsync(this.Caller(), new ExceptionQuery(factoryId, status, type, severity, lineId, orderId, page ?? 1, pageSize ?? 50), ct));

    [HttpGet("exceptions/{id}")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Exception([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _exceptions.GetAsync(this.Caller(), g, ct)) : BadId("exception");

    [HttpPost("exceptions/evaluate")]
    [RequirePermission(Permissions.PlanningExceptionsReview)]
    public async Task<IActionResult> Evaluate([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ExceptionEvaluateRequest? body, CancellationToken ct) =>
        AccessResults.From(this, await _exceptions.EvaluateAsync(this.Caller(), body ?? new ExceptionEvaluateRequest(null, null), Trace, ct));

    [HttpPost("exceptions/{id}/acknowledge")]
    [RequirePermission(Permissions.PlanningExceptionsReview)]
    public async Task<IActionResult> Acknowledge([FromRoute] string id, [FromBody] ExceptionNote body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _exceptions.AcknowledgeAsync(this.Caller(), g, body, Trace, ct)) : BadId("exception");

    [HttpPost("exceptions/{id}/resolve")]
    [RequirePermission(Permissions.PlanningExceptionsReview)]
    public async Task<IActionResult> Resolve([FromRoute] string id, [FromBody] ExceptionNote body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _exceptions.ResolveAsync(this.Caller(), g, body, Trace, ct)) : BadId("exception");

    // ── Reports ─────────────────────────────────────────────────────────────

    [HttpGet("reports")]
    [RequirePermission(Permissions.PlanningView)]
    public IActionResult ReportTypes() => Ok(_reports.Types());

    [HttpGet("reports/{type}")]
    [RequirePermission(Permissions.PlanningView)]
    public async Task<IActionResult> Report([FromRoute] string type, [FromQuery] Guid factoryId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? source,
        [FromQuery] string? lineId, [FromQuery] Guid? orderId, CancellationToken ct) =>
        AccessResults.From(this, await _reports.GenerateAsync(this.Caller(), type, factoryId, from, to, source, lineId, orderId, ct));

    [HttpGet("reports/{type}/export")]
    [RequirePermission(Permissions.PlanningReportsExport)]
    public async Task<IActionResult> ExportReport([FromRoute] string type, [FromQuery] string? format, [FromQuery] Guid factoryId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? source, [FromQuery] string? lineId, [FromQuery] Guid? orderId, CancellationToken ct)
    {
        var r = await _reports.ExportAsync(this.Caller(), type, format ?? "csv", factoryId, from, to, source, lineId, orderId, Trace, ct);
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        return File(r.Value!.Content, r.Value.ContentType, r.Value.FileName);
    }
}
