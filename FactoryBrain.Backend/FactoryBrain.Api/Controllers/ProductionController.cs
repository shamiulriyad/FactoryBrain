using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Production;
using FactoryBrain.Application.Production;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// Production planning &amp; line efficiency (<c>/api/production</c>).
/// <list type="bullet">
///   <item>Read (any signed-in user): config, lines, shifts, styles, orders, plans, output history,
///   summary, line detail, order risk, time studies, layouts, balance proposals.</item>
///   <item>Operator or Admin: orders, plans and their workflow (submit, withdraw, activate, resolve or
///   withdraw a change, complete, cancel), output (record, correct, import), time studies, layouts, balance analysis,
///   proposal submit / apply.</item>
///   <item>Admin: lines, shifts and styles (the factory-approved standards).</item>
/// </list>
/// Approving a plan or proposal happens in the existing approval queue (<c>/api/approvals</c>);
/// nothing here decides an approval. The acting user always comes from the validated JWT.
/// </summary>
[ApiController]
[Route("api/production")]
public class ProductionController : ControllerBase
{
    private readonly IProductionSetupService _setup;
    private readonly IProductionOrderService _orders;
    private readonly IProductionPlanService _plans;
    private readonly IProductionOutputService _output;
    private readonly IProductionAnalysisService _analysis;

    public ProductionController(IProductionSetupService setup, IProductionOrderService orders, IProductionPlanService plans,
        IProductionOutputService output, IProductionAnalysisService analysis)
    {
        _setup = setup; _orders = orders; _plans = plans; _output = output; _analysis = analysis;
    }

    // ── Setup ───────────────────────────────────────────────────────────────

    [HttpGet("config")]
    public async Task<IActionResult> Config(CancellationToken ct) => Ok(await _setup.ConfigAsync(ct));

    [HttpGet("lines")]
    public async Task<IActionResult> Lines([FromQuery] bool? includeInactive, CancellationToken ct) => Ok(await _setup.ListLinesAsync(includeInactive ?? false, ct));

    [HttpPost("lines")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> CreateLine([FromBody] ProductionLineWrite body, CancellationToken ct) =>
        Map(await _setup.CreateLineAsync(body, Actor, Trace, ct), l => $"/api/production/lines/{l.Id}");

    [HttpPut("lines/{id}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> UpdateLine([FromRoute] string id, [FromBody] ProductionLineWrite body, CancellationToken ct) =>
        Map(await _setup.UpdateLineAsync(id, body, Actor, Trace, ct));

    [HttpGet("lines/{id}/detail")]
    public async Task<IActionResult> LineDetail([FromRoute] string id, [FromQuery] string? date, [FromQuery] string? shift, CancellationToken ct)
    {
        if (!TryDate(date, "date", out var d, out var bad)) return bad!;
        return Map(await _analysis.LineDetailAsync(id, d, Blank(shift), ct));
    }

    [HttpGet("shifts")]
    public async Task<IActionResult> Shifts([FromQuery] bool? includeInactive, CancellationToken ct) => Ok(await _setup.ListShiftsAsync(includeInactive ?? false, ct));

    [HttpPost("shifts")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> CreateShift([FromBody] ShiftWrite body, CancellationToken ct) =>
        Map(await _setup.CreateShiftAsync(body, Actor, Trace, ct), s => $"/api/production/shifts/{s.Code}");

    [HttpPut("shifts/{code}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> UpdateShift([FromRoute] string code, [FromBody] ShiftWrite body, CancellationToken ct) =>
        Map(await _setup.UpdateShiftAsync(code, body, Actor, Trace, ct));

    [HttpGet("styles")]
    public async Task<IActionResult> Styles([FromQuery] bool? includeInactive, CancellationToken ct) => Ok(await _setup.ListStylesAsync(includeInactive ?? false, ct));

    [HttpGet("styles/{id}")]
    public async Task<IActionResult> Style([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && await _setup.GetStyleAsync(g, ct) is { } s ? Ok(s) : NotFound(new { error = "style_not_found" });

    [HttpPost("styles")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> CreateStyle([FromBody] StyleWrite body, CancellationToken ct) =>
        Map(await _setup.CreateStyleAsync(body, Actor, Trace, ct), s => $"/api/production/styles/{s.Id}");

    [HttpPut("styles/{id}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> UpdateStyle([FromRoute] string id, [FromBody] StyleWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _setup.UpdateStyleAsync(g, body, Actor, Trace, ct)) : NotFound(new { error = "style_not_found" });

    // ── Orders ──────────────────────────────────────────────────────────────

    [HttpGet("orders")]
    public async Task<IActionResult> Orders([FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? styleId,
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? sort, [FromQuery] bool? includeRisk, CancellationToken ct)
    {
        if (!TryGuid(styleId, "styleId", out var sid, out var bad)) return bad!;
        return Map(await _orders.ListAsync(new OrderQuery(Blank(q), Blank(status), sid, page ?? 1, pageSize ?? 20, Blank(sort), includeRisk ?? true), ct));
    }

    [HttpGet("orders/{id}")]
    public async Task<IActionResult> Order([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && await _orders.GetAsync(g, ct) is { } o ? Ok(o) : NotFound(new { error = "order_not_found" });

    [HttpGet("orders/{id}/risk")]
    public async Task<IActionResult> OrderRisk([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _analysis.OrderRiskAsync(g, ct)) : NotFound(new { error = "order_not_found" });

    [HttpPost("orders")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> CreateOrder([FromBody] OrderWrite body, CancellationToken ct) =>
        Map(await _orders.CreateAsync(body, Actor, Trace, ct), o => $"/api/production/orders/{o.Id}");

    [HttpPut("orders/{id}")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> UpdateOrder([FromRoute] string id, [FromBody] OrderWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _orders.UpdateAsync(g, body, Actor, Trace, ct)) : NotFound(new { error = "order_not_found" });

    // ── Plans ───────────────────────────────────────────────────────────────

    [HttpGet("plans")]
    public async Task<IActionResult> Plans([FromQuery] string? status, [FromQuery] string? lineId, [FromQuery] string? orderId,
        [FromQuery] string? from, [FromQuery] string? to, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        if (!TryGuid(orderId, "orderId", out var oid, out var bad)) return bad!;
        if (!TryDate(from, "from", out var f, out bad) || !TryDate(to, "to", out var t, out bad)) return bad!;
        return Map(await _plans.ListAsync(new PlanQuery(Blank(status), Blank(lineId), oid, f, t, page ?? 1, pageSize ?? 20), ct));
    }

    [HttpGet("plans/{id}")]
    public async Task<IActionResult> Plan([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && await _plans.GetAsync(g, ct) is { } p ? Ok(p) : NotFound(new { error = "plan_not_found" });

    [HttpPost("plans")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> CreatePlan([FromBody] PlanWrite body, CancellationToken ct) =>
        Map(await _plans.CreateAsync(body, Actor, Trace, ct), p => $"/api/production/plans/{p.Summary.Id}");

    [HttpPut("plans/{id}")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> UpdatePlan([FromRoute] string id, [FromBody] PlanWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _plans.UpdateAsync(g, body, Actor, Trace, ct)) : NotFound(new { error = "plan_not_found" });

    [HttpPost("plans/{id}/submit")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> SubmitPlan([FromRoute] string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PlanTransitionRequest? body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _plans.SubmitAsync(g, body ?? NoBody, Actor, Trace, ct)) : NotFound(new { error = "plan_not_found" });

    [HttpPost("plans/{id}/activate")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> ActivatePlan([FromRoute] string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PlanTransitionRequest? body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _plans.ActivateAsync(g, body ?? NoBody, Actor, Trace, ct)) : NotFound(new { error = "plan_not_found" });

    [HttpPost("plans/{id}/changes/{changeId}/resolve")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> ResolveChange([FromRoute] string id, [FromRoute] string changeId, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && Guid.TryParse(changeId, out var c)
            ? Map(await _plans.ResolveChangeAsync(g, c, Actor, Trace, ct))
            : NotFound(new { error = "change_not_found" });

    [HttpPost("plans/{id}/withdraw")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> WithdrawPlan([FromRoute] string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PlanTransitionRequest? body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _plans.WithdrawAsync(g, body ?? NoBody, Actor, Trace, ct)) : NotFound(new { error = "plan_not_found" });

    [HttpPost("plans/{id}/changes/{changeId}/withdraw")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> WithdrawChange([FromRoute] string id, [FromRoute] string changeId, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && Guid.TryParse(changeId, out var c)
            ? Map(await _plans.WithdrawChangeAsync(g, c, Actor, Trace, ct))
            : NotFound(new { error = "change_not_found" });

    [HttpPost("plans/{id}/complete")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> CompletePlan([FromRoute] string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PlanTransitionRequest? body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _plans.CompleteAsync(g, body ?? NoBody, Actor, Trace, ct)) : NotFound(new { error = "plan_not_found" });

    [HttpPost("plans/{id}/cancel")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> CancelPlan([FromRoute] string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PlanTransitionRequest? body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _plans.CancelAsync(g, body ?? NoBody, Actor, Trace, ct)) : NotFound(new { error = "plan_not_found" });

    // ── Output ──────────────────────────────────────────────────────────────

    [HttpGet("output")]
    public async Task<IActionResult> Output([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? lineId, [FromQuery] string? orderId,
        [FromQuery] string? shift, [FromQuery] string? stage, [FromQuery] string? source, [FromQuery] bool? includeSuperseded,
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? sort, CancellationToken ct)
    {
        if (!TryGuid(orderId, "orderId", out var oid, out var bad)) return bad!;
        if (!TryDate(from, "from", out var f, out bad) || !TryDate(to, "to", out var t, out bad)) return bad!;
        return Map(await _output.ListAsync(new OutputQuery(f, t, Blank(lineId), oid, Blank(shift), Blank(stage), Blank(source),
            includeSuperseded ?? false, page ?? 1, pageSize ?? 25, Blank(sort)), ct));
    }

    [HttpPost("output")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> RecordOutput([FromBody] OutputWrite body, CancellationToken ct) =>
        Map(await _output.RecordAsync(body, Actor, Trace, ct), _ => "/api/production/output");

    [HttpPost("output/{id}/correct")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> CorrectOutput([FromRoute] string id, [FromBody] OutputCorrection body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _output.CorrectAsync(g, body, Actor, Trace, ct), _ => "/api/production/output") : NotFound(new { error = "output_not_found" });

    [HttpPost("output/import")]
    [Authorize(Policy = AuthPolicies.Operator)]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> ImportOutput([FromBody] OutputImportRequest body, CancellationToken ct)
    {
        var r = await _output.ImportAsync(body, Actor, Trace, ct);
        if (r.Outcome == ProductionOutcome.Invalid && r.Value is not null)
        {
            // Nothing was stored; the per-row report says what to fix.
            var problem = new ValidationProblemDetails(r.Errors!.ToDictionary(kv => kv.Key, kv => kv.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "import_rejected",
                Detail = $"{r.Value.Invalid} row(s) can't be imported, so nothing was stored. Fix them and import the file again.",
            };
            problem.Extensions["result"] = r.Value;
            return BadRequest(problem);
        }
        return Map(r);
    }

    // ── Analysis ────────────────────────────────────────────────────────────

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? lineId,
        [FromQuery] string? orderId, [FromQuery] string? shift, CancellationToken ct)
    {
        if (!TryGuid(orderId, "orderId", out var oid, out var bad)) return bad!;
        if (!TryDate(from, "from", out var f, out bad) || !TryDate(to, "to", out var t, out bad)) return bad!;
        return Map(await _analysis.SummaryAsync(new SummaryQuery(f, t, Blank(lineId), oid, Blank(shift)), ct));
    }

    [HttpGet("observations")]
    public async Task<IActionResult> Observations([FromQuery] string? lineId, [FromQuery] string? styleId, [FromQuery] int? limit, CancellationToken ct)
    {
        if (!TryGuid(styleId, "styleId", out var sid, out var bad)) return bad!;
        return Ok(await _analysis.ListObservationsAsync(Blank(lineId), sid, limit ?? 100, ct));
    }

    [HttpPost("observations")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> RecordObservation([FromBody] ObservationWrite body, CancellationToken ct) =>
        Map(await _analysis.RecordObservationAsync(body, Actor, Trace, ct), _ => "/api/production/observations");

    [HttpGet("layouts/current")]
    public async Task<IActionResult> CurrentLayout([FromQuery] string? lineId, [FromQuery] string? styleId, CancellationToken ct)
    {
        if (Blank(lineId) is not { } line || !Guid.TryParse(styleId, out var sid))
            return Invalid(new Dictionary<string, string[]> { ["lineId"] = new[] { "lineId and styleId are required." } });
        var r = await _analysis.CurrentLayoutAsync(line, sid, ct);
        return r.Value is null ? NotFound(new { error = "layout_not_recorded" }) : Ok(r.Value);
    }

    [HttpPut("layouts/current")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> RecordLayout([FromBody] LayoutWrite body, CancellationToken ct) =>
        Map(await _analysis.RecordLayoutAsync(body, Actor, Trace, ct), _ => "/api/production/layouts/current");

    [HttpPost("balance/analyze")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> AnalyzeBalance([FromBody] BalanceRequest body, CancellationToken ct) =>
        Map(await _analysis.AnalyzeBalanceAsync(body, Actor, Trace, ct));

    [HttpGet("balance/proposals")]
    public async Task<IActionResult> Proposals([FromQuery] string? lineId, [FromQuery] string? styleId, [FromQuery] string? status,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        if (!TryGuid(styleId, "styleId", out var sid, out var bad)) return bad!;
        return Map(await _analysis.ListProposalsAsync(Blank(lineId), sid, Blank(status), page ?? 1, pageSize ?? 20, ct));
    }

    [HttpGet("balance/proposals/{id}")]
    public async Task<IActionResult> Proposal([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && await _analysis.GetProposalAsync(g, ct) is { } p ? Ok(p) : NotFound(new { error = "proposal_not_found" });

    [HttpPost("balance/proposals/{id}/submit")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> SubmitProposal([FromRoute] string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PlanTransitionRequest? body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _analysis.SubmitProposalAsync(g, body ?? NoBody, Actor, Trace, ct)) : NotFound(new { error = "proposal_not_found" });

    [HttpPost("balance/proposals/{id}/apply")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> ApplyProposal([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _analysis.ApplyProposalAsync(g, Actor, Trace, ct)) : NotFound(new { error = "proposal_not_found" });

    // ── Mapping ─────────────────────────────────────────────────────────────

    private static readonly PlanTransitionRequest NoBody = new(null, null);

    private Application.Abstractions.AuditActor Actor => SettingsResults.Actor(User);
    private string Trace => SettingsResults.TraceId(HttpContext);

    private IActionResult Map<T>(ProductionResult<T> r, Func<T, string>? location = null)
    {
        switch (r.Outcome)
        {
            case ProductionOutcome.Ok:
                if (r.Code == "duplicate") Response.Headers["Idempotent-Replayed"] = "true";
                return Ok(r.Value);
            case ProductionOutcome.Created:
                return location is null ? StatusCode(StatusCodes.Status201Created, r.Value) : Created(location(r.Value!), r.Value);
            case ProductionOutcome.NotFound:
                return NotFound(new { error = r.Code });
            case ProductionOutcome.Conflict:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: r.Code, detail: r.Message);
            case ProductionOutcome.Forbidden:
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: r.Code, detail: r.Message);
            default:
                return Invalid(r.Errors ?? new Dictionary<string, string[]>());
        }
    }

    private IActionResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(kv => kv.Key, kv => kv.Value)) { Status = StatusCodes.Status400BadRequest });

    private bool TryDate(string? raw, string field, out DateOnly? value, out IActionResult? bad)
    {
        value = null;
        bad = null;
        if (Blank(raw) is not { } s) return true;
        if (ProductionWire.TryParseDate(s, out var d)) { value = d; return true; }
        bad = Invalid(new Dictionary<string, string[]> { [field] = new[] { $"{field} must be a date (yyyy-MM-dd)." } });
        return false;
    }

    private bool TryGuid(string? raw, string field, out Guid? value, out IActionResult? bad)
    {
        value = null;
        bad = null;
        if (Blank(raw) is not { } s) return true;
        if (Guid.TryParse(s, out var g)) { value = g; return true; }
        bad = Invalid(new Dictionary<string, string[]> { [field] = new[] { $"{field} must be an id." } });
        return false;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
