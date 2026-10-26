using System.Text.Json;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Approvals;
using FactoryBrain.Application.Dtos.Production;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static FactoryBrain.Infrastructure.Production.ProductionSupport;

namespace FactoryBrain.Infrastructure.Production;

/// <summary>
/// Production plans and their auditable change history.
/// <list type="bullet">
/// <item>Drafts are edited directly; each edit is recorded with the values before and after.</item>
/// <item>Submitting queues an approval in the existing approval workflow. The plan becomes
/// <c>Active</c> only when someone then activates it and the approval is granted (by a different
/// person when <c>PRODUCTION_REQUIRE_SEPARATE_APPROVER</c> is on); a rejection returns it to draft.</item>
/// <item>Changing an active plan's planning inputs records a pending change with its own approval; the
/// live plan keeps its values until the approved change is applied. Notes-only edits apply directly.</item>
/// <item>Two active plans can't overlap on one line; activation and applying changes run under a per-line lock.</item>
/// </list>
/// </summary>
public sealed class ProductionPlanService : IProductionPlanService
{
    public const string EntityType = "production_plan";
    public const int MaxPlanDays = 366;

    private readonly FactoryBrainDbContext _db;
    private readonly ProductionOptions _options;
    private readonly ProductionReadModel _read;
    private readonly ILogger<ProductionPlanService> _log;

    public ProductionPlanService(FactoryBrainDbContext db, ProductionOptions options, ILogger<ProductionPlanService> log)
    {
        _db = db; _options = options; _log = log; _read = new ProductionReadModel(db, options);
    }

    // ── Read ────────────────────────────────────────────────────────────────

    public async Task<ProductionResult<Paged<PlanListItem>>> ListAsync(PlanQuery q, CancellationToken ct)
    {
        var errors = Errors();
        ProductionPlanStatus? status = null;
        if (q.Status is not null)
        {
            if (ProductionWire.TryParsePlanStatus(q.Status, out var s)) status = s;
            else errors.Add("status", $"status must be one of: {string.Join(", ", ProductionWire.PlanStatuses)}");
        }
        if (q.Page < 1) errors.Add("page", "page must be 1 or more.");
        if (q.PageSize is < 1 or > 100) errors.Add("pageSize", "pageSize must be from 1 to 100.");
        if (q.From is { } f && q.To is { } t && f > t) errors.Add("from", "from must not be after to.");
        if (errors.Count > 0) return ProductionResult<Paged<PlanListItem>>.Invalid(errors);

        var query = _db.ProductionPlans.AsNoTracking();
        if (status is { } st) query = query.Where(p => p.Status == st);
        if (q.LineId is not null) query = query.Where(p => p.LineId == q.LineId);
        if (q.OrderId is { } oid) query = query.Where(p => p.OrderId == oid);
        if (q.From is { } from) query = query.Where(p => p.EndDate >= from);
        if (q.To is { } to) query = query.Where(p => p.StartDate <= to);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedAt)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        return ProductionResult<Paged<PlanListItem>>.Ok(new Paged<PlanListItem>(await ListItemsAsync(_db, rows, ct), q.Page, q.PageSize, total,
            (int)Math.Ceiling(total / (double)q.PageSize)));
    }

    internal static async Task<IReadOnlyList<PlanListItem>> ListItemsAsync(FactoryBrainDbContext db, IReadOnlyList<ProductionPlan> plans, CancellationToken ct)
    {
        var orderIds = plans.Select(p => p.OrderId).Distinct().ToList();
        var lineIds = plans.Select(p => p.LineId).Distinct().ToList();
        var planIds = plans.Select(p => p.Id).ToList();
        var orders = await db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
        var lines = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var pending = (await db.ProductionPlanChanges.AsNoTracking().Where(c => planIds.Contains(c.PlanId) && c.Status == PlanChangeStatus.Pending)
            .Select(c => c.PlanId).ToListAsync(ct)).ToHashSet();
        return plans.Select(p => new PlanListItem(p.Id, p.Reference, p.OrderId, orders.GetValueOrDefault(p.OrderId, "?"), p.LineId,
            lines.GetValueOrDefault(p.LineId, p.LineId), ProductionWire.Date(p.StartDate), ProductionWire.Date(p.EndDate), p.PlannedQuantity,
            p.Operators, p.TargetEfficiencyPct, ProductionWire.PlanStatus(p.Status), p.Revision, pending.Contains(p.Id), p.UpdatedAt)).ToList();
    }

    public async Task<PlanDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        var plan = await _db.ProductionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return plan is null ? null : await DetailAsync(plan, ct);
    }

    private async Task<PlanDetail> DetailAsync(ProductionPlan p, CancellationToken ct)
    {
        var item = (await ListItemsAsync(_db, new[] { p }, ct))[0];
        var order = await _db.ProductionOrders.AsNoTracking().FirstAsync(o => o.Id == p.OrderId, ct);
        var cal = await _read.CalendarAsync(ct);
        var (smv, basis) = (await _read.StyleSmvAsync(new[] { order.StyleId }, ct)).GetValueOrDefault(order.StyleId);
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == p.LineId, ct);
        var targets = await TargetsAsync(p, cal, smv, basis, line, order, ct);

        // Planned vs actual to date, over the same window: the plan's dates, up to the last recorded interval (or now).
        var now = DateTime.UtcNow;
        var schedule = ProductionReadModel.Schedule(p, cal, smv);
        var planStart = cal.ToUtc(p.StartDate, TimeOnly.MinValue);
        var planEnd = cal.ToUtc(p.EndDate.AddDays(1), TimeOnly.MinValue);
        var events = await _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => e.LineId == p.LineId && e.OrderId == p.OrderId && !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput
                        && e.ProductionDate >= p.StartDate && e.ProductionDate <= p.EndDate)
            .Select(e => new { e.Quantity, e.IntervalEndUtc }).ToListAsync(ct);
        var lastEnd = events.Count == 0 ? (DateTime?)null : events.Max(e => e.IntervalEndUtc);
        var through = Min(Min(planEnd, now), lastEnd ?? now);
        if (through < planStart) through = planStart;
        var plannedToDate = (int)Math.Floor(schedule.PlannedBetween(planStart, through) + 1e-9);
        var actualToDate = events.Where(e => e.IntervalEndUtc <= through).Sum(e => e.Quantity);
        var actualTotal = events.Sum(e => e.Quantity);
        var progress = new PlanProgressDto(now, events.Count == 0 && now < planStart ? null : through, plannedToDate, actualToDate,
            ProductionMetrics.Variance(actualToDate, plannedToDate), ProductionMetrics.AchievementPct(actualToDate, plannedToDate),
            actualTotal, Math.Max(0, p.PlannedQuantity - actualTotal));

        var changes = await _db.ProductionPlanChanges.AsNoTracking().Where(c => c.PlanId == p.Id).OrderByDescending(c => c.RequestedAt).ToListAsync(ct);
        var approvals = await ApprovalsAsync(_db, changes.Select(c => c.ApprovalId).Append(p.ActivationApprovalId), ct);
        return new PlanDetail(item, Values(p), targets, progress,
            Ref(p.ActivationApprovalId is null ? null : approvals.GetValueOrDefault(p.ActivationApprovalId)),
            changes.Select(c => ToDto(c, c.ApprovalId is null ? null : approvals.GetValueOrDefault(c.ApprovalId))).ToList(),
            p.SubmittedByEmail, p.SubmittedAt, p.ActivatedByEmail, p.ActivatedAt, p.ClosedReason, p.CreatedByEmail, p.CreatedAt, p.Version);
    }

    private async Task<PlanTargetsDto> TargetsAsync(ProductionPlan p, ProductionCalendar cal, double? smv, SmvBasis basis,
        ProductionLine? line, ProductionOrder order, CancellationToken ct)
    {
        var hourly = ProductionMetrics.HourlyTarget(p.Operators, p.TargetEfficiencyPct, smv);
        var schedule = ProductionReadModel.Schedule(p, cal, smv);
        var warnings = new List<string>();
        var daily = schedule.Windows.GroupBy(w => w.ProductionDate).OrderBy(g => g.Key).Select(g =>
        {
            var minutes = g.Sum(w => w.Minutes);
            var start = g.Min(w => w.StartUtc);
            var end = g.Max(w => w.EndUtc);
            return new DailyTargetDto(ProductionWire.Date(g.Key), ProductionMetrics.Round1(minutes), (int)Math.Floor(schedule.PlannedBetween(start, end) + 1e-9));
        }).ToList();
        if (basis == SmvBasis.Missing)
            warnings.Add("The style has no approved SMV or operation bulletin: targets spread the planned quantity evenly and efficiency can't be calculated.");
        else if (basis == SmvBasis.OperationBulletin)
            warnings.Add("No approved SMV is set for the style; the operation bulletin total is used.");
        if (schedule.TotalMinutes <= 0)
            warnings.Add("The selected shifts have no working time in these dates.");
        else if (schedule.Method == PlannedOutputMethod.TargetRate && schedule.Capacity + 1e-6 < p.PlannedQuantity)
            warnings.Add($"At the target rate the scheduled time produces about {Math.Floor(schedule.Capacity)} pieces, fewer than the planned {p.PlannedQuantity}.");
        var missingShifts = p.ShiftCodes.Where(c => cal.Shifts.All(s => s.Code != c)).ToList();
        if (missingShifts.Count > 0)
            warnings.Add($"Shift{(missingShifts.Count == 1 ? "" : "s")} {string.Join(", ", missingShifts)} {(missingShifts.Count == 1 ? "is" : "are")} no longer active and contribute no time.");
        if (line is not null && p.Operators > line.Workstations)
            warnings.Add($"{p.Operators} operators are planned but the line declares {line.Workstations} workstations.");
        if (p.EndDate > order.DueDate)
            warnings.Add($"The plan ends after the order's due date ({ProductionWire.Date(order.DueDate)}).");
        var planned = await _db.ProductionPlans.AsNoTracking()
            .Where(x => x.OrderId == p.OrderId && x.Status != ProductionPlanStatus.Cancelled).SumAsync(x => x.PlannedQuantity, ct);
        if (planned > order.Quantity)
            warnings.Add($"Plans for this order add up to {planned} pieces, more than the order quantity ({order.Quantity}).");
        return new PlanTargetsDto(smv, ProductionWire.SmvBasisName(basis), hourly is null ? null : ProductionMetrics.Round1(hourly.Value),
            ProductionMetrics.Round1(schedule.TotalMinutes), daily.Count, schedule.Method == PlannedOutputMethod.TargetRate ? Math.Floor(schedule.Capacity) : null,
            schedule.Method == PlannedOutputMethod.TargetRate ? "target_rate" : "even_spread", daily, warnings,
            "Hourly target = operators × 60 × target efficiency ÷ SMV; daily target = planned output in that day's scheduled minutes (capped at the planned quantity).");
    }

    // ── Create / edit ───────────────────────────────────────────────────────

    public async Task<ProductionResult<PlanDetail>> CreateAsync(PlanWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        if (body.OrderId is null) errors.Add("orderId", "orderId is required.");
        var values = await ValidateValuesAsync(errors, body, current: null, ct);
        ProductionOrder? order = null;
        if (body.OrderId is { } oid)
        {
            order = await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == oid, ct);
            if (order is null) errors.Add("orderId", "The order does not exist.");
            else if (order.Status != ProductionOrderStatus.Open) errors.Add("orderId", "Plans can only be made for open orders.");
        }
        if (errors.Count > 0) return ProductionResult<PlanDetail>.Invalid(errors);

        var now = DateTime.UtcNow;
        var plan = new ProductionPlan { OrderId = order!.Id, CreatedAt = now, UpdatedAt = now, CreatedByUserId = actor.UserId, CreatedByEmail = actor.Email };
        plan.Reference = NewReference("PP", plan.Id, now);
        Apply(plan, values!);
        _db.ProductionPlans.Add(plan);
        _db.ProductionPlanChanges.Add(new ProductionPlanChange
        {
            PlanId = plan.Id, Kind = "created", Status = PlanChangeStatus.Applied, ProposedValues = Serialize(values!), Reason = Clean(body.Reason),
            RequestedAt = now, RequestedByUserId = actor.UserId, RequestedByEmail = actor.Email, DecidedAt = now, DecidedByEmail = actor.Email,
        });
        _db.AuditLog.Add(Audit(actor, traceId, "production_plan.created", EntityType, plan.Id.ToString(), null, "draft",
            $"{plan.Reference} · order {order.OrderNumber} · {plan.LineId} · {values!.StartDate}→{values.EndDate} · {plan.PlannedQuantity} pcs"));
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Production plan {PlanId} created by user {UserId}.", plan.Id, actor.UserId);
        return ProductionResult<PlanDetail>.Created(await DetailAsync(plan, ct));
    }

    public async Task<ProductionResult<PlanActionResponse>> UpdateAsync(Guid id, PlanWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var plan = await _db.ProductionPlans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return ProductionResult<PlanActionResponse>.NotFound("plan_not_found");
        var errors = Errors();
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        if (body.OrderId is { } oid && oid != plan.OrderId) errors.Add("orderId", "A plan's order can't be changed; cancel it and plan the other order.");
        var current = Values(plan);
        var values = await ValidateValuesAsync(errors, body, current, ct);
        if (errors.Count > 0) return ProductionResult<PlanActionResponse>.Invalid(errors);
        if (body.ExpectedVersion != plan.Version) return VersionConflict();

        var diff = Diff(current, values!);
        if (diff.Count == 0) return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("unchanged", "Nothing changed.", await DetailAsync(plan, ct)));
        var now = DateTime.UtcNow;
        var reason = Clean(body.Reason);

        switch (plan.Status)
        {
            case ProductionPlanStatus.Draft:
            case ProductionPlanStatus.Active when diff.All(d => d.Field == "notes"):
            {
                Apply(plan, values!);
                plan.UpdatedAt = now;
                if (plan.Status == ProductionPlanStatus.Active) plan.Revision++;
                _db.ProductionPlanChanges.Add(new ProductionPlanChange
                {
                    PlanId = plan.Id, Kind = "edited", Status = PlanChangeStatus.Applied, PreviousValues = Serialize(current), ProposedValues = Serialize(values!),
                    Reason = reason, BaseVersion = plan.Version, RequestedAt = now, RequestedByUserId = actor.UserId, RequestedByEmail = actor.Email,
                    DecidedAt = now, DecidedByEmail = actor.Email,
                });
                _db.AuditLog.Add(Audit(actor, traceId, "production_plan.edited", EntityType, plan.Id.ToString(), ProductionWire.PlanStatus(plan.Status),
                    ProductionWire.PlanStatus(plan.Status), DescribeDiff(diff, reason)));
                if (await SaveAsync(ct) is { } conflict) return conflict;
                return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("edited", "The plan was updated.", await DetailAsync(plan, ct)));
            }
            case ProductionPlanStatus.Active:
            {
                if (reason is null) return ProductionResult<PlanActionResponse>.Invalid("reason", "Explain why the live plan needs to change.");
                if (await _db.ProductionPlanChanges.AnyAsync(c => c.PlanId == plan.Id && c.Status == PlanChangeStatus.Pending, ct))
                    return ProductionResult<PlanActionResponse>.Conflict("change_pending", "This plan already has a change waiting for approval. Resolve it first.");
                var change = new ProductionPlanChange
                {
                    PlanId = plan.Id, Kind = "revision", Status = PlanChangeStatus.Pending, PreviousValues = Serialize(current), ProposedValues = Serialize(values!),
                    Reason = reason, BaseVersion = plan.Version, RequestedAt = now, RequestedByUserId = actor.UserId, RequestedByEmail = actor.Email,
                };
                var order = await _db.ProductionOrders.AsNoTracking().FirstAsync(o => o.Id == plan.OrderId, ct);
                change.ApprovalId = QueueApproval(_db, "plan-change", $"Change live plan {plan.Reference} (order {order.OrderNumber}, {plan.LineId})",
                    $"Requested by {actor.Email}: {DescribeDiff(diff, reason)}",
                    "Approve to allow this change to be applied to the live plan. It takes effect only when someone applies it on the plan page.",
                    RiskTier.Medium, "production_plans", plan.Id.ToString(), actor, now);
                _db.ProductionPlanChanges.Add(change);
                _db.AuditLog.Add(Audit(actor, traceId, "production_plan.change_requested", EntityType, plan.Id.ToString(), "active", "active",
                    $"approval {change.ApprovalId} · {DescribeDiff(diff, reason)}"));
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex, "one_pending"))
                {
                    return ProductionResult<PlanActionResponse>.Conflict("change_pending", "This plan already has a change waiting for approval. Resolve it first.");
                }
                return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("change_requested",
                    "The change is waiting for approval. The live plan keeps its current values until the approved change is applied.", await DetailAsync(plan, ct)));
            }
            case ProductionPlanStatus.PendingApproval:
                return ProductionResult<PlanActionResponse>.Conflict("plan_pending_approval", "The plan is waiting for approval and can't be edited. Wait for the decision (a rejection returns it to draft).");
            default:
                return ProductionResult<PlanActionResponse>.Conflict("plan_closed", $"A {ProductionWire.PlanStatus(plan.Status)} plan can't be changed.");
        }
    }

    // ── Workflow ────────────────────────────────────────────────────────────

    public async Task<ProductionResult<PlanActionResponse>> SubmitAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var plan = await _db.ProductionPlans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return ProductionResult<PlanActionResponse>.NotFound("plan_not_found");
        if (plan.Status != ProductionPlanStatus.Draft)
            return ProductionResult<PlanActionResponse>.Conflict("invalid_state", "Only a draft plan can be submitted for approval.");
        if (body.ExpectedVersion is { } v && v != plan.Version) return VersionConflict();

        // The inputs must still be valid now (a shift or line may have been deactivated since).
        var errors = Errors();
        await ValidateValuesAsync(errors, new PlanWrite(null, null, null, null, null, null, null, null, null, null, null, null), Values(plan), ct);
        var order = await _db.ProductionOrders.AsNoTracking().FirstAsync(o => o.Id == plan.OrderId, ct);
        if (order.Status != ProductionOrderStatus.Open) errors.Add("orderId", "The order is no longer open.");
        if (errors.Count > 0) return ProductionResult<PlanActionResponse>.Invalid(errors);

        var now = DateTime.UtcNow;
        var (smv, basis) = (await _read.StyleSmvAsync(new[] { order.StyleId }, ct)).GetValueOrDefault(order.StyleId);
        var hourly = ProductionMetrics.HourlyTarget(plan.Operators, plan.TargetEfficiencyPct, smv);
        plan.ActivationApprovalId = QueueApproval(_db, "plan", $"Activate plan {plan.Reference}: order {order.OrderNumber} on {plan.LineId}",
            $"{plan.PlannedQuantity} pcs from {ProductionWire.Date(plan.StartDate)} to {ProductionWire.Date(plan.EndDate)} on {plan.LineId}; " +
            $"{plan.Operators} operators at {plan.TargetEfficiencyPct:0.#} % target efficiency; shifts {string.Join(", ", plan.ShiftCodes)}" +
            (plan.OvertimeMinutesPerDay > 0 ? $" + {plan.OvertimeMinutesPerDay} min overtime/day" : "") +
            $"; SMV {(smv is { } s ? s.ToString("0.###") : "missing")} ({ProductionWire.SmvBasisName(basis)}); hourly target {(hourly is { } h ? h.ToString("0.#") : "n/a")}. Submitted by {actor.Email}." +
            (Clean(body.Reason) is { } r ? $" Note: {r}" : ""),
            "Approve to allow this plan to go live. It becomes the operational plan only when someone activates it on the plan page.",
            RiskTier.Medium, "production_plans", plan.Id.ToString(), actor, now);
        plan.Status = ProductionPlanStatus.PendingApproval;
        plan.SubmittedAt = now;
        plan.SubmittedByUserId = actor.UserId;
        plan.SubmittedByEmail = actor.Email;
        plan.UpdatedAt = now;
        _db.AuditLog.Add(Audit(actor, traceId, "production_plan.submitted", EntityType, plan.Id.ToString(), "draft", "pending_approval",
            $"approval {plan.ActivationApprovalId}{(Clean(body.Reason) is { } note ? " · " + note : "")}"));
        if (await SaveAsync(ct) is { } conflict) return conflict;
        return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("submitted",
            "Submitted to the approval queue. Once approved, activate it here.", await DetailAsync(plan, ct)));
    }

    public async Task<ProductionResult<PlanActionResponse>> ActivateAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        return await InLineTransactionAsync(id, async plan =>
        {
            if (plan.Status != ProductionPlanStatus.PendingApproval || plan.ActivationApprovalId is null)
                return ProductionResult<PlanActionResponse>.Conflict("invalid_state", "Only a plan waiting for approval can be activated.");
            if (body.ExpectedVersion is { } v && v != plan.Version) return VersionConflict();
            var approval = await _db.Approvals.AsNoTracking().FirstAsync(a => a.Id == plan.ActivationApprovalId, ct);
            var now = DateTime.UtcNow;
            switch (approval.Stage)
            {
                case Stage.PendingApproval:
                    return ProductionResult<PlanActionResponse>.Conflict("approval_pending", "The approval hasn't been decided yet.");
                case Stage.Rejected:
                    plan.Status = ProductionPlanStatus.Draft;
                    plan.ActivationApprovalId = null;
                    plan.UpdatedAt = now;
                    _db.AuditLog.Add(Audit(actor, traceId, "production_plan.approval_rejected", EntityType, plan.Id.ToString(), "pending_approval", "draft",
                        $"approval {approval.Id} rejected by {approval.DecidedByEmail}: {approval.DecisionReason}"));
                    return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("returned_to_draft",
                        $"The approval was rejected ({approval.DecisionReason}). The plan is a draft again.", null!));
                case Stage.Approved:
                    if (SeparationProblem(_options, approval, plan.SubmittedByUserId, plan.SubmittedByEmail) is { } sep)
                        return ProductionResult<PlanActionResponse>.Forbidden("same_approver", sep);
                    if (await OverlapAsync(plan, plan.LineId, plan.StartDate, plan.EndDate, ct) is { } overlap)
                        return ProductionResult<PlanActionResponse>.Conflict("line_overlap", overlap);
                    plan.Status = ProductionPlanStatus.Active;
                    plan.ActivatedAt = now;
                    plan.ActivatedByEmail = actor.Email;
                    plan.UpdatedAt = now;
                    _db.AuditLog.Add(Audit(actor, traceId, "production_plan.activated", EntityType, plan.Id.ToString(), "pending_approval", "active",
                        $"approval {approval.Id} approved by {approval.DecidedByEmail}"));
                    return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("activated", "The plan is live.", null!));
                default:
                    return ProductionResult<PlanActionResponse>.Conflict("approval_unusable", $"The approval is '{StageNames.ToWire(approval.Stage)}' and can't activate the plan.");
            }
        }, ct);
    }

    public async Task<ProductionResult<PlanActionResponse>> ResolveChangeAsync(Guid id, Guid changeId, AuditActor actor, string? traceId, CancellationToken ct)
    {
        return await InLineTransactionAsync(id, async plan =>
        {
            var change = await _db.ProductionPlanChanges.FirstOrDefaultAsync(c => c.Id == changeId && c.PlanId == id, ct);
            if (change is null) return ProductionResult<PlanActionResponse>.NotFound("change_not_found");
            if (change.Status != PlanChangeStatus.Pending || change.ApprovalId is null)
                return ProductionResult<PlanActionResponse>.Conflict("invalid_state", "Only a pending change can be resolved.");
            var approval = await _db.Approvals.AsNoTracking().FirstAsync(a => a.Id == change.ApprovalId, ct);
            var now = DateTime.UtcNow;
            switch (approval.Stage)
            {
                case Stage.PendingApproval:
                    return ProductionResult<PlanActionResponse>.Conflict("approval_pending", "The approval hasn't been decided yet.");
                case Stage.Rejected:
                    change.Status = PlanChangeStatus.Rejected;
                    change.DecidedAt = now;
                    change.DecidedByEmail = approval.DecidedByEmail;
                    _db.AuditLog.Add(Audit(actor, traceId, "production_plan.change_rejected", EntityType, plan.Id.ToString(), "active", "active",
                        $"change {change.Id} · approval {approval.Id} rejected by {approval.DecidedByEmail}: {approval.DecisionReason}"));
                    return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("change_rejected",
                        $"The change was rejected ({approval.DecisionReason}); the live plan is unchanged.", null!));
                case Stage.Approved:
                    if (SeparationProblem(_options, approval, change.RequestedByUserId, change.RequestedByEmail) is { } sep)
                        return ProductionResult<PlanActionResponse>.Forbidden("same_approver", sep);
                    if (plan.Status != ProductionPlanStatus.Active || plan.Version != change.BaseVersion)
                    {
                        change.Status = PlanChangeStatus.Superseded;
                        change.DecidedAt = now;
                        change.DecidedByEmail = actor.Email;
                        _db.AuditLog.Add(Audit(actor, traceId, "production_plan.change_superseded", EntityType, plan.Id.ToString(), null, null,
                            $"change {change.Id}: the plan changed after the request, so it was not applied"));
                        return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("change_superseded",
                            "The plan changed after this change was requested, so it was not applied. Request it again against the current plan.", null!));
                    }
                    var proposed = JsonSerializer.Deserialize<PlanValues>(change.ProposedValues, Json)!;
                    if (!ProductionWire.TryParseDate(proposed.StartDate, out var start) || !ProductionWire.TryParseDate(proposed.EndDate, out var end))
                        return ProductionResult<PlanActionResponse>.Conflict("invalid_change", "The stored change is not readable.");
                    if (proposed.LineId != plan.LineId) await LockLineAsync(_db, proposed.LineId, ct);
                    if (await OverlapAsync(plan, proposed.LineId, start, end, ct) is { } overlap)
                        return ProductionResult<PlanActionResponse>.Conflict("line_overlap", overlap);
                    var before = Values(plan);
                    Apply(plan, proposed);
                    plan.Revision++;
                    plan.UpdatedAt = now;
                    change.Status = PlanChangeStatus.Applied;
                    change.DecidedAt = now;
                    change.DecidedByEmail = actor.Email;
                    _db.AuditLog.Add(Audit(actor, traceId, "production_plan.change_applied", EntityType, plan.Id.ToString(), "active", "active",
                        $"revision {plan.Revision} · approval {approval.Id} approved by {approval.DecidedByEmail} · {DescribeDiff(Diff(before, proposed), change.Reason)}"));
                    return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("change_applied", $"Revision {plan.Revision} is live.", null!));
                default:
                    return ProductionResult<PlanActionResponse>.Conflict("approval_unusable", $"The approval is '{StageNames.ToWire(approval.Stage)}'.");
            }
        }, ct);
    }

    public async Task<ProductionResult<PlanActionResponse>> WithdrawAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        return await InLineTransactionAsync(id, plan =>
        {
            if (plan.Status != ProductionPlanStatus.PendingApproval)
                return Task.FromResult(ProductionResult<PlanActionResponse>.Conflict("invalid_state", "Only a plan waiting for approval can be withdrawn."));
            if (body.ExpectedVersion is { } v && v != plan.Version) return Task.FromResult(VersionConflict());
            var approvalId = plan.ActivationApprovalId;
            plan.Status = ProductionPlanStatus.Draft;
            plan.ActivationApprovalId = null;
            plan.UpdatedAt = DateTime.UtcNow;
            _db.AuditLog.Add(Audit(actor, traceId, "production_plan.withdrawn", EntityType, plan.Id.ToString(), "pending_approval", "draft",
                $"approval {approvalId} no longer affects this plan{(Clean(body.Reason) is { } r ? ". " + r : "")}"));
            return Task.FromResult(ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("withdrawn",
                "The plan is a draft again. Its earlier approval request no longer affects it.", null!)));
        }, ct);
    }

    public async Task<ProductionResult<PlanActionResponse>> WithdrawChangeAsync(Guid id, Guid changeId, AuditActor actor, string? traceId, CancellationToken ct)
    {
        return await InLineTransactionAsync(id, async plan =>
        {
            var change = await _db.ProductionPlanChanges.FirstOrDefaultAsync(c => c.Id == changeId && c.PlanId == id, ct);
            if (change is null) return ProductionResult<PlanActionResponse>.NotFound("change_not_found");
            if (change.Status != PlanChangeStatus.Pending)
                return ProductionResult<PlanActionResponse>.Conflict("invalid_state", "Only a pending change can be withdrawn.");
            change.Status = PlanChangeStatus.Superseded;
            change.DecidedAt = DateTime.UtcNow;
            change.DecidedByEmail = actor.Email;
            _db.AuditLog.Add(Audit(actor, traceId, "production_plan.change_withdrawn", EntityType, plan.Id.ToString(), null, null,
                $"change {change.Id} withdrawn; approval {change.ApprovalId} no longer affects the plan"));
            return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse("change_withdrawn", "The change was withdrawn; the live plan is unchanged.", null!));
        }, ct);
    }

    public Task<ProductionResult<PlanActionResponse>> CompleteAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct) =>
        CloseAsync(id, body, ProductionPlanStatus.Completed, actor, traceId, ct);

    public Task<ProductionResult<PlanActionResponse>> CancelAsync(Guid id, PlanTransitionRequest body, AuditActor actor, string? traceId, CancellationToken ct) =>
        CloseAsync(id, body, ProductionPlanStatus.Cancelled, actor, traceId, ct);

    private async Task<ProductionResult<PlanActionResponse>> CloseAsync(Guid id, PlanTransitionRequest body, ProductionPlanStatus to,
        AuditActor actor, string? traceId, CancellationToken ct)
    {
        var plan = await _db.ProductionPlans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return ProductionResult<PlanActionResponse>.NotFound("plan_not_found");
        if (body.ExpectedVersion is { } v && v != plan.Version) return VersionConflict();
        var reason = Clean(body.Reason);
        var allowed = to == ProductionPlanStatus.Completed
            ? plan.Status == ProductionPlanStatus.Active
            : plan.Status is ProductionPlanStatus.Draft or ProductionPlanStatus.PendingApproval or ProductionPlanStatus.Active;
        if (!allowed)
            return ProductionResult<PlanActionResponse>.Conflict("invalid_state",
                to == ProductionPlanStatus.Completed ? "Only an active plan can be completed." : $"A {ProductionWire.PlanStatus(plan.Status)} plan can't be cancelled.");
        if (to == ProductionPlanStatus.Cancelled && plan.Status != ProductionPlanStatus.Draft && reason is null)
            return ProductionResult<PlanActionResponse>.Invalid("reason", "Explain why the plan is cancelled.");

        var now = DateTime.UtcNow;
        var from = plan.Status;
        var pending = await _db.ProductionPlanChanges.Where(c => c.PlanId == id && c.Status == PlanChangeStatus.Pending).ToListAsync(ct);
        foreach (var c in pending) { c.Status = PlanChangeStatus.Superseded; c.DecidedAt = now; c.DecidedByEmail = actor.Email; }
        plan.Status = to;
        plan.ClosedAt = now;
        plan.ClosedReason = Truncate(reason, 500);
        plan.UpdatedAt = now;
        var note = from == ProductionPlanStatus.PendingApproval
            ? $" Approval {plan.ActivationApprovalId} stays in the queue; its decision no longer affects this plan." : "";
        _db.AuditLog.Add(Audit(actor, traceId, to == ProductionPlanStatus.Completed ? "production_plan.completed" : "production_plan.cancelled",
            EntityType, plan.Id.ToString(), ProductionWire.PlanStatus(from), ProductionWire.PlanStatus(to),
            (reason ?? "") + (pending.Count > 0 ? $" {pending.Count} pending change(s) closed." : "") + note));
        if (await SaveAsync(ct) is { } conflict) return conflict;
        return ProductionResult<PlanActionResponse>.Ok(new PlanActionResponse(to == ProductionPlanStatus.Completed ? "completed" : "cancelled",
            to == ProductionPlanStatus.Completed ? "The plan is completed." : "The plan is cancelled.", await DetailAsync(plan, ct)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="work"/> on the tracked plan inside a transaction
    /// holding the plan's line lock; saves and commits when it returns Ok,
    /// and attaches the plan detail to the response.
    /// </summary>
    private async Task<ProductionResult<PlanActionResponse>> InLineTransactionAsync(Guid id,
        Func<ProductionPlan, Task<ProductionResult<PlanActionResponse>>> work, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var lineId = await _db.ProductionPlans.Where(p => p.Id == id).Select(p => p.LineId).FirstOrDefaultAsync(ct);
            if (lineId is null) return ProductionResult<PlanActionResponse>.NotFound("plan_not_found");
            await LockLineAsync(_db, lineId, ct);
            var plan = await _db.ProductionPlans.FirstAsync(p => p.Id == id, ct);
            var r = await work(plan);
            if (r.Outcome != ProductionOutcome.Ok) { await tx.RollbackAsync(ct); return r; }
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { await tx.RollbackAsync(ct); return VersionConflict(); }
            await tx.CommitAsync(ct);
            return r;
        });
        if (result.Outcome != ProductionOutcome.Ok) return result;
        _db.ChangeTracker.Clear();
        var fresh = await _db.ProductionPlans.AsNoTracking().FirstAsync(p => p.Id == id, ct);
        _log.LogInformation("Production plan {PlanId}: {Outcome}.", id, result.Value!.Outcome);
        return ProductionResult<PlanActionResponse>.Ok(result.Value with { Plan = await DetailAsync(fresh, ct) });
    }

    private async Task<string?> OverlapAsync(ProductionPlan plan, string lineId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var clash = await _db.ProductionPlans.AsNoTracking()
            .Where(p => p.Id != plan.Id && p.LineId == lineId && p.Status == ProductionPlanStatus.Active && p.StartDate <= end && p.EndDate >= start)
            .Select(p => p.Reference).ToListAsync(ct);
        return clash.Count == 0 ? null
            : $"{lineId} already has an active plan in these dates ({string.Join(", ", clash)}). Complete it, change its dates, or plan another line.";
    }

    private async Task<PlanValues?> ValidateValuesAsync(Dictionary<string, string[]> errors, PlanWrite b, PlanValues? current, CancellationToken ct)
    {
        var lineId = Clean(b.LineId) ?? current?.LineId;
        var startRaw = b.StartDate ?? current?.StartDate;
        var endRaw = b.EndDate ?? current?.EndDate;
        var quantity = b.PlannedQuantity ?? current?.PlannedQuantity;
        var operators = b.Operators ?? current?.Operators;
        var efficiency = b.TargetEfficiencyPct ?? current?.TargetEfficiencyPct;
        var shifts = (b.ShiftCodes ?? current?.ShiftCodes)?.Select(s => s.Trim().ToLowerInvariant()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var overtime = b.OvertimeMinutesPerDay ?? current?.OvertimeMinutesPerDay ?? 0;
        var notes = b.Notes is null ? current?.Notes : Clean(b.Notes);

        if (lineId is null) errors.Add("lineId", "lineId is required.");
        else
        {
            var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
            if (line is null) errors.Add("lineId", "The line does not exist.");
            else if (!line.IsActive) errors.Add("lineId", "The line is inactive.");
        }
        var okStart = ProductionWire.TryParseDate(startRaw, out var start);
        var okEnd = ProductionWire.TryParseDate(endRaw, out var end);
        if (!okStart) errors.Add("startDate", "startDate must be a date (yyyy-MM-dd).");
        if (!okEnd) errors.Add("endDate", "endDate must be a date (yyyy-MM-dd).");
        if (okStart && okEnd)
        {
            if (end < start) errors.Add("endDate", "endDate must not be before startDate.");
            else if (end.DayNumber - start.DayNumber + 1 > MaxPlanDays) errors.Add("endDate", $"A plan covers at most {MaxPlanDays} days.");
        }
        if (quantity is not (>= 1 and <= 10_000_000)) errors.Add("plannedQuantity", "Planned quantity must be from 1 to 10,000,000 pieces.");
        if (operators is not (>= 1 and <= 500)) errors.Add("operators", "Operators must be from 1 to 500.");
        if (!(efficiency is > 0 and <= 150)) errors.Add("targetEfficiencyPct", "Target efficiency must be more than 0 and at most 150 %.");
        if (overtime is < 0 or > 480) errors.Add("overtimeMinutesPerDay", "Overtime must be from 0 to 480 minutes per day.");
        if (notes is { Length: > 1000 }) errors.Add("notes", "Notes are limited to 1000 characters.");
        if (shifts is null || shifts.Count == 0) errors.Add("shiftCodes", "Choose at least one shift.");
        else
        {
            var active = await _db.ProductionShifts.AsNoTracking().Where(s => s.IsActive).Select(s => s.Code).ToListAsync(ct);
            var unknown = shifts.Where(s => !active.Contains(s)).ToList();
            if (unknown.Count > 0) errors.Add("shiftCodes", $"Unknown or inactive shifts: {string.Join(", ", unknown)}.");
        }
        if (errors.Count > 0) return null;

        var cal = await _read.CalendarAsync(ct);
        if (cal.Windows(start, end, shifts!, overtime).Count == 0)
        {
            errors.Add("shiftCodes", "The selected shifts have no working days in these dates.");
            return null;
        }
        return new PlanValues(lineId!, ProductionWire.Date(start), ProductionWire.Date(end), quantity!.Value, operators!.Value,
            Math.Round(efficiency!.Value, 2), shifts!, overtime, notes);
    }

    private static void Apply(ProductionPlan p, PlanValues v)
    {
        p.LineId = v.LineId;
        ProductionWire.TryParseDate(v.StartDate, out var s);
        ProductionWire.TryParseDate(v.EndDate, out var e);
        p.StartDate = s;
        p.EndDate = e;
        p.PlannedQuantity = v.PlannedQuantity;
        p.Operators = v.Operators;
        p.TargetEfficiencyPct = v.TargetEfficiencyPct;
        p.ShiftCodes = v.ShiftCodes.ToList();
        p.OvertimeMinutesPerDay = v.OvertimeMinutesPerDay;
        p.Notes = v.Notes;
    }

    internal static List<FieldDiffDto> Diff(PlanValues a, PlanValues b)
    {
        var list = new List<FieldDiffDto>();
        void D(string f, object? x, object? y)
        {
            var sx = x is double dx ? dx.ToString("0.##") : x?.ToString();
            var sy = y is double dy ? dy.ToString("0.##") : y?.ToString();
            if (sx != sy) list.Add(new FieldDiffDto(f, sx, sy));
        }
        D("lineId", a.LineId, b.LineId);
        D("startDate", a.StartDate, b.StartDate);
        D("endDate", a.EndDate, b.EndDate);
        D("plannedQuantity", a.PlannedQuantity, b.PlannedQuantity);
        D("operators", a.Operators, b.Operators);
        D("targetEfficiencyPct", a.TargetEfficiencyPct, b.TargetEfficiencyPct);
        D("shiftCodes", string.Join(",", a.ShiftCodes), string.Join(",", b.ShiftCodes));
        D("overtimeMinutesPerDay", a.OvertimeMinutesPerDay, b.OvertimeMinutesPerDay);
        D("notes", a.Notes, b.Notes);
        return list;
    }

    private static string DescribeDiff(IReadOnlyList<FieldDiffDto> diff, string? reason) =>
        string.Join("; ", diff.Select(d => $"{d.Field}: {d.From ?? "—"} → {d.To ?? "—"}")) + (reason is null ? "" : $". Reason: {reason}");

    private static string Serialize(PlanValues v) => JsonSerializer.Serialize(v, Json);

    private static PlanChangeDto ToDto(ProductionPlanChange c, ApprovalRequest? approval)
    {
        var proposed = JsonSerializer.Deserialize<PlanValues>(c.ProposedValues, Json)!;
        var previous = c.PreviousValues is null ? null : JsonSerializer.Deserialize<PlanValues>(c.PreviousValues, Json);
        return new PlanChangeDto(c.Id, c.Kind, ProductionWire.ChangeStatus(c.Status), previous, proposed,
            previous is null ? Array.Empty<FieldDiffDto>() : Diff(previous, proposed), c.Reason, Ref(approval),
            c.RequestedAt, c.RequestedByEmail, c.DecidedAt, c.DecidedByEmail);
    }

    private async Task<ProductionResult<PlanActionResponse>?> SaveAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); return null; }
        catch (DbUpdateConcurrencyException) { return VersionConflict(); }
    }

    private static ProductionResult<PlanActionResponse> VersionConflict() =>
        ProductionResult<PlanActionResponse>.Conflict("version_conflict", "The plan changed since you loaded it. Reload it and try again.");

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
