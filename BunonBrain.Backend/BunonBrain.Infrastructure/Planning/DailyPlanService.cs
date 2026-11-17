using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Iot;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Planning.PlanningScope;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>
/// Daily production plans: one plan per factory and date, prepared as a draft,
/// reviewed, approved by a second person, then published so supervisors and
/// monitoring operators see it. A published plan is changed by starting a new
/// revision, which replaces it when published; every step is on the plan's
/// audit trail. Each plan item (order · line · shift · target) must be backed
/// by an open allocation of the order on that line.
/// </summary>
public sealed class DailyPlanService : IDailyPlanService
{
    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public DailyPlanService(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    // ── Reads ───────────────────────────────────────────────────────────────

    public async Task<AccessResult<PagedResult<DailyPlanListItem>>> ListAsync(AccessContext caller, DailyPlanQuery q, CancellationToken ct)
    {
        if (q.Page < 1 || q.PageSize is < 1 or > 200) return AccessResult<PagedResult<DailyPlanListItem>>.Invalid("paging_invalid", "page must be 1 or more and pageSize from 1 to 200.");
        string? status = null;
        if (q.Status is not null && (status = PlanStatuses.Parse(q.Status)) is null)
            return AccessResult<PagedResult<DailyPlanListItem>>.Invalid("status_invalid", $"status must be one of: {string.Join(", ", PlanStatuses.All)}.");
        if (q.FactoryId is { } f && !caller.CanAccessFactory(f)) return AccessResult<PagedResult<DailyPlanListItem>>.Forbidden("factory_out_of_scope", "You can only see your own factories.");
        var query = Plans(_db, caller);
        if (q.FactoryId is { } fid) query = query.Where(p => p.FactoryId == fid);
        if (status is not null) query = query.Where(p => p.Status == status);
        if (q.From is { } from) query = query.Where(p => p.PlanDate >= from);
        if (q.To is { } to) query = query.Where(p => p.PlanDate <= to);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(p => p.PlanDate).ThenByDescending(p => p.RevisionNo).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var stats = await _db.DailyPlanItems.AsNoTracking().Where(i => ids.Contains(i.PlanId)).GroupBy(i => i.PlanId)
            .Select(g => new { g.Key, N = g.Count(), T = g.Sum(x => x.TargetQuantity) }).ToDictionaryAsync(x => x.Key, ct);
        var items = rows.Select(p => new DailyPlanListItem(p.Id, p.Reference, p.FactoryId, Date(p.PlanDate), p.RevisionNo, p.Status, stats.GetValueOrDefault(p.Id)?.N ?? 0,
            stats.GetValueOrDefault(p.Id)?.T ?? 0, p.UpdatedAt, p.CreatedByEmail, p.PublishedAt, p.PublishedByEmail)).ToList();
        return AccessResult<PagedResult<DailyPlanListItem>>.Ok(new PagedResult<DailyPlanListItem>(items, q.Page, q.PageSize, total));
    }

    public async Task<AccessResult<DailyPlanDto>> GetAsync(AccessContext caller, Guid id, string? source, CancellationToken ct)
    {
        var src = IotQueries.SourceOf(source);
        if (source is not null && IotSourceTypes.Parse(source) is null) return AccessResult<DailyPlanDto>.Invalid("source_invalid", "source must be REAL or SIMULATED.");
        var p = await Plans(_db, caller).Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return AccessResult<DailyPlanDto>.NotFound("plan_not_found", "Plan not found in your scope.");
        return AccessResult<DailyPlanDto>.Ok(await DtoAsync(p, caller, src, ct));
    }

    public async Task<AccessResult<DailyPlanDto?>> ForDateAsync(AccessContext caller, Guid factoryId, DateOnly date, string? source, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId)) return AccessResult<DailyPlanDto?>.Forbidden("factory_out_of_scope", "You can only see your own factories.");
        if (source is not null && IotSourceTypes.Parse(source) is null) return AccessResult<DailyPlanDto?>.Invalid("source_invalid", "source must be REAL or SIMULATED.");
        var candidates = await Plans(_db, caller).Include(x => x.Items).Where(p => p.FactoryId == factoryId && p.PlanDate == date
                                                                                 && (p.Status == PlanStatuses.Published || p.Status == PlanStatuses.Draft || p.Status == PlanStatuses.UnderReview || p.Status == PlanStatuses.Approved))
            .ToListAsync(ct);
        var plan = candidates.FirstOrDefault(p => p.Status == PlanStatuses.Published) ?? candidates.OrderByDescending(p => p.RevisionNo).FirstOrDefault();
        return AccessResult<DailyPlanDto?>.Ok(plan is null ? null : await DtoAsync(plan, caller, IotQueries.SourceOf(source), ct));
    }

    // ── Create & edit ───────────────────────────────────────────────────────

    public async Task<AccessResult<DailyPlanDto>> CreateAsync(AccessContext caller, DailyPlanWrite body, string? traceId, CancellationToken ct)
    {
        var fr = await PlanningSetupService.ResolveFactoryAsync(_db, caller, body.FactoryId, ct);
        if (fr.Outcome != AccessOutcome.Ok) return new AccessResult<DailyPlanDto>(fr.Outcome, null, fr.Code, fr.Message);
        var factory = fr.Value!;
        if (!factory.IsActive) return Bad("factory_inactive", "The factory is deactivated.");
        if (!TryDate(body.PlanDate, out var date)) return Bad("date_invalid", "planDate must be a date (yyyy-MM-dd).");
        if (body.Notes is { Length: > 1000 }) return Bad("notes_invalid", "Notes are limited to 1000 characters.");
        var clock = await FactoryClock.LoadAsync(_db, factory.Id, ct);
        if (date < clock.Today(DateTimeOffset.UtcNow)) return Bad("date_in_past", "A plan can't be made for a date that has passed.");
        if (date > clock.Today(DateTimeOffset.UtcNow).AddDays(60)) return Bad("date_too_far", "Plans are made at most 60 days ahead.");
        if (caller.IsLineScoped) return AccessResult<DailyPlanDto>.Forbidden("plan_scope", "Daily plans are prepared at factory level.");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockAsync(_db, $"plan:{factory.Id}:{date:yyyyMMdd}", ct);
            if (await _db.DailyProductionPlans.AnyAsync(p => p.FactoryId == factory.Id && p.PlanDate == date && p.Status == PlanStatuses.Published, ct))
            { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.Conflict("plan_published_exists", "A plan is already published for this date. Revise it instead of making another."); }
            if (await _db.DailyProductionPlans.FirstOrDefaultAsync(p => p.FactoryId == factory.Id && p.PlanDate == date
                                                                      && (p.Status == PlanStatuses.Draft || p.Status == PlanStatuses.UnderReview || p.Status == PlanStatuses.Approved), ct) is { } open)
            { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.Conflict("plan_open_exists", $"Plan {open.Reference} for this date is still being prepared ({open.Status.ToLowerInvariant().Replace('_', ' ')}). Continue with it."); }

            var revision = (await _db.DailyProductionPlans.Where(p => p.FactoryId == factory.Id && p.PlanDate == date).MaxAsync(p => (int?)p.RevisionNo, ct) ?? 0) + 1;
            var plan = NewPlan(factory.Id, date, revision, Clean(body.Notes), caller, null, null);
            _db.DailyProductionPlans.Add(plan);
            var warnings = new List<string>();
            if (body.CopyFromPlanId is { } srcId)
            {
                var src = await Plans(_db, caller).Include(x => x.Items).FirstOrDefaultAsync(p => p.Id == srcId && p.FactoryId == factory.Id, ct);
                if (src is null) { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.NotFound("plan_not_found", "The plan to copy from wasn't found."); }
                var orders = await _db.ProductionOrders.AsNoTracking().Where(o => src.Items.Select(i => i.OrderId).Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
                var allocs = await _db.ProductionAllocations.AsNoTracking().Where(a => src.Items.Select(i => i.OrderId).Contains(a.OrderId) && (a.Status == AllocationStatuses.Planned || a.Status == AllocationStatuses.Active)).ToListAsync(ct);
                foreach (var i in src.Items)
                {
                    var alloc = allocs.FirstOrDefault(a => a.OrderId == i.OrderId && a.LineId == i.LineId && a.PlannedStart <= date && a.PlannedEnd >= date);
                    var shift = clock.Shift(i.ShiftId);
                    if (!orders.TryGetValue(i.OrderId, out var o) || !OrderLifecycle.IsLive(o.Status) || alloc is null || shift is null || !shift.WorksOn(date.DayOfWeek))
                    { warnings.Add($"Not copied: {o?.OrderNumber ?? "order"} on {i.LineId} — no longer allocated, live or scheduled for {Date(date)}."); continue; }
                    plan.Items.Add(new DailyPlanItem
                    {
                        PlanId = plan.Id, FactoryId = factory.Id, PlanDate = date, OrderId = i.OrderId, AllocationId = alloc.Id, LineId = i.LineId, ShiftId = i.ShiftId,
                        TargetQuantity = i.TargetQuantity, TargetSource = "MANUAL", SupervisorUserId = alloc.SupervisorUserId ?? i.SupervisorUserId, Notes = i.Notes,
                    });
                }
            }
            _db.DailyPlanEvents.Add(Event(plan, "CREATED", plan.Items.Count == 0 ? null : $"{plan.Items.Count} item(s) copied from {srcReference(body.CopyFromPlanId)}", null, caller));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.plan_created", "daily_plan", plan.Id.ToString(), null, plan.Status, $"{plan.Reference} for {Date(date)}", traceId, factory.Id));
            try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.Conflict("plan_open_exists", "A plan for this date was just created. Open it instead."); }
            var dto = await DtoAsync(plan, caller, IotSourceTypes.Real, ct);
            return AccessResult<DailyPlanDto>.Created(dto with { Warnings = dto.Warnings.Concat(warnings).ToList() });
        });

        static string srcReference(Guid? id) => id?.ToString("N")[..8] ?? "";
    }

    public async Task<AccessResult<DailyPlanDto>> UpdateNotesAsync(AccessContext caller, Guid id, DailyPlanNotesWrite body, string? traceId, CancellationToken ct)
    {
        var p = await LoadForEditAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        if (body.Notes is { Length: > 1000 }) return Bad("notes_invalid", "Notes are limited to 1000 characters.");
        if (body.ExpectedVersion is null) return Bad("version_required", "expectedVersion is required.");
        var plan = p.Plan!;
        if (body.ExpectedVersion != plan.Version) return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again.");
        plan.Notes = Clean(body.Notes);
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(plan, caller, traceId, null, ct);
    }

    public async Task<AccessResult<DailyPlanDto>> AddItemAsync(AccessContext caller, Guid id, PlanItemWrite body, uint? expectedVersion, string? traceId, CancellationToken ct)
    {
        var p = await LoadForEditAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var plan = p.Plan!;
        if (expectedVersion is null) return Bad("version_required", "expectedVersion is required.");
        if (expectedVersion != plan.Version) return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again.");
        var v = await ValidateItemAsync(caller, plan, body, null, ct);
        if (v.Error is not null) return v.Error;
        if (plan.Items.Any(i => i.LineId == v.Item!.LineId && i.ShiftId == v.Item.ShiftId))
            return AccessResult<DailyPlanDto>.Conflict("item_exists", "This line already has an item for this shift in the plan. Change that item instead.");
        plan.Items.Add(v.Item!);
        _db.DailyPlanItems.Add(v.Item!);
        _db.DailyPlanEvents.Add(Event(plan, "ITEM_ADDED", ItemText(v.Item!, v.OrderNumber, v.LineName, v.ShiftName), null, caller));
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(plan, caller, traceId, "planning.plan_item_added", ct);
    }

    public async Task<AccessResult<DailyPlanDto>> UpdateItemAsync(AccessContext caller, Guid id, Guid itemId, PlanItemWrite body, uint? expectedVersion, string? traceId, CancellationToken ct)
    {
        var p = await LoadForEditAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var plan = p.Plan!;
        if (expectedVersion is null) return Bad("version_required", "expectedVersion is required.");
        if (expectedVersion != plan.Version) return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again.");
        var item = plan.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return AccessResult<DailyPlanDto>.NotFound("item_not_found", "Plan item not found.");
        var v = await ValidateItemAsync(caller, plan, body, item, ct);
        if (v.Error is not null) return v.Error;
        var n = v.Item!;
        if (plan.Items.Any(i => i.Id != item.Id && i.LineId == n.LineId && i.ShiftId == n.ShiftId))
            return AccessResult<DailyPlanDto>.Conflict("item_exists", "This line already has an item for this shift in the plan.");
        var before = ItemText(item, v.OrderNumber, v.LineName, v.ShiftName);
        item.OrderId = n.OrderId; item.AllocationId = n.AllocationId; item.LineId = n.LineId; item.ShiftId = n.ShiftId; item.TargetQuantity = n.TargetQuantity;
        item.TargetSource = n.TargetSource; item.TargetId = n.TargetId; item.SupervisorUserId = n.SupervisorUserId; item.Notes = n.Notes;
        _db.DailyPlanEvents.Add(Event(plan, "ITEM_CHANGED", $"{before} → {ItemText(item, v.OrderNumber, v.LineName, v.ShiftName)}", null, caller));
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(plan, caller, traceId, "planning.plan_item_changed", ct);
    }

    public async Task<AccessResult<DailyPlanDto>> RemoveItemAsync(AccessContext caller, Guid id, Guid itemId, uint? expectedVersion, string? traceId, CancellationToken ct)
    {
        var p = await LoadForEditAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var plan = p.Plan!;
        if (expectedVersion is null) return Bad("version_required", "expectedVersion is required.");
        if (expectedVersion != plan.Version) return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again.");
        var item = plan.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return AccessResult<DailyPlanDto>.NotFound("item_not_found", "Plan item not found.");
        _db.DailyPlanEvents.Add(Event(plan, "ITEM_REMOVED", $"{item.LineId}, target {item.TargetQuantity}", null, caller));
        plan.Items.Remove(item);
        _db.DailyPlanItems.Remove(item);
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(plan, caller, traceId, "planning.plan_item_removed", ct);
    }

    // ── Workflow ────────────────────────────────────────────────────────────

    public async Task<AccessResult<DailyPlanDto>> SubmitAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct)
    {
        var p = await LoadAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var plan = p.Plan!;
        var check = Guard(plan, body, PlanStatuses.UnderReview, requireReason: false);
        if (check is not null) return check;
        if (plan.Items.Count == 0) return Bad("plan_empty", "Add at least one item before submitting the plan for review.");
        var problems = await RevalidateAsync(plan, ct);
        if (problems.Count > 0) return Bad("plan_items_invalid", string.Join(" ", problems));
        plan.Status = PlanStatuses.UnderReview;
        plan.SubmittedAt = DateTimeOffset.UtcNow; plan.SubmittedByUserId = caller.UserId; plan.SubmittedByEmail = caller.Email;
        return await MoveAsync(plan, caller, "SUBMITTED", "planning.plan_submitted", Clean(body.Reason), traceId, ct);
    }

    public async Task<AccessResult<DailyPlanDto>> ApproveAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct)
    {
        var p = await LoadAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var plan = p.Plan!;
        var check = Guard(plan, body, PlanStatuses.Approved, requireReason: false);
        if (check is not null) return check;
        if (_options.RequireSeparateApprover && plan.SubmittedByUserId == caller.UserId)
            return AccessResult<DailyPlanDto>.Forbidden("same_approver", "Another person must approve a plan you submitted (PRODUCTION_REQUIRE_SEPARATE_APPROVER).");
        var problems = await RevalidateAsync(plan, ct);
        if (problems.Count > 0) return Bad("plan_items_invalid", string.Join(" ", problems));
        plan.Status = PlanStatuses.Approved;
        plan.ApprovedAt = DateTimeOffset.UtcNow; plan.ApprovedByEmail = caller.Email;
        return await MoveAsync(plan, caller, "APPROVED", "planning.plan_approved", Clean(body.Reason), traceId, ct);
    }

    public async Task<AccessResult<DailyPlanDto>> ReturnAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct)
    {
        var p = await LoadAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var plan = p.Plan!;
        var check = Guard(plan, body, PlanStatuses.Draft, requireReason: true);
        if (check is not null) return check;
        plan.Status = PlanStatuses.Draft;
        plan.SubmittedAt = null; plan.SubmittedByUserId = null; plan.SubmittedByEmail = null; plan.ApprovedAt = null; plan.ApprovedByEmail = null;
        return await MoveAsync(plan, caller, "RETURNED", "planning.plan_returned", Clean(body.Reason), traceId, ct);
    }

    public async Task<AccessResult<DailyPlanDto>> PublishAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct)
    {
        var p = await LoadAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var plan = p.Plan!;
        var check = Guard(plan, body, PlanStatuses.Published, requireReason: false);
        if (check is not null) return check;
        var clock = await FactoryClock.LoadAsync(_db, plan.FactoryId, ct);
        if (plan.PlanDate < clock.Today(DateTimeOffset.UtcNow)) return Bad("date_in_past", "A plan for a date that has passed can't be published.");
        var problems = await RevalidateAsync(plan, ct);
        if (problems.Count > 0) return Bad("plan_items_invalid", string.Join(" ", problems));

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockAsync(_db, $"plan:{plan.FactoryId}:{plan.PlanDate:yyyyMMdd}", ct);
            await _db.Entry(plan).ReloadAsync(ct);
            if (body.ExpectedVersion != plan.Version) { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again."); }
            var now = DateTimeOffset.UtcNow;
            // The revision being replaced steps aside first: only one plan is in force per factory and date.
            var previous = await _db.DailyProductionPlans.FirstOrDefaultAsync(x => x.FactoryId == plan.FactoryId && x.PlanDate == plan.PlanDate && x.Status == PlanStatuses.Published && x.Id != plan.Id, ct);
            if (previous is not null)
            {
                previous.Status = PlanStatuses.Revised; previous.ClosedAt = now; previous.ClosedReason = $"Replaced by revision {plan.RevisionNo} ({plan.Reference})";
                previous.UpdatedAt = now;
                _db.DailyPlanEvents.Add(Event(previous, "SUPERSEDED", $"Replaced by {plan.Reference}", plan.RevisionReason, caller));
                await _db.SaveChangesAsync(ct);
            }
            plan.Status = PlanStatuses.Published; plan.PublishedAt = now; plan.PublishedByEmail = caller.Email; plan.UpdatedAt = now;
            _db.DailyPlanEvents.Add(Event(plan, "PUBLISHED", $"{plan.Items.Count} item(s), target {plan.Items.Sum(i => i.TargetQuantity)}", Clean(body.Reason), caller));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.plan_published", "daily_plan", plan.Id.ToString(), PlanStatuses.Approved, PlanStatuses.Published,
                $"{plan.Reference} for {Date(plan.PlanDate)} (revision {plan.RevisionNo})", traceId, plan.FactoryId));
            try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again."); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.Conflict("plan_published_exists", "Another plan was just published for this date."); }
            return AccessResult<DailyPlanDto>.Ok(await DtoAsync(plan, caller, IotSourceTypes.Real, ct));
        });
    }

    public async Task<AccessResult<DailyPlanDto>> ReviseAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct)
    {
        var p = await LoadAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var src = p.Plan!;
        if (src.Status != PlanStatuses.Published) return AccessResult<DailyPlanDto>.Conflict("plan_not_published", "Only a published plan is revised; edit a draft directly.");
        if (Clean(body.Reason) is null) return Bad("reason_required", "Give the reason for revising the published plan.");
        if (body.Reason is { Length: > 500 }) return Bad("reason_invalid", "The reason is limited to 500 characters.");
        if (body.ExpectedVersion is null) return Bad("version_required", "expectedVersion is required.");
        if (body.ExpectedVersion != src.Version) return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again.");
        var clock = await FactoryClock.LoadAsync(_db, src.FactoryId, ct);
        if (src.PlanDate < clock.Today(DateTimeOffset.UtcNow)) return Bad("date_in_past", "A plan for a date that has passed can't be revised: its record stays as it was.");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockAsync(_db, $"plan:{src.FactoryId}:{src.PlanDate:yyyyMMdd}", ct);
            if (await _db.DailyProductionPlans.AnyAsync(x => x.FactoryId == src.FactoryId && x.PlanDate == src.PlanDate
                                                             && (x.Status == PlanStatuses.Draft || x.Status == PlanStatuses.UnderReview || x.Status == PlanStatuses.Approved), ct))
            { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.Conflict("plan_open_exists", "A revision of this plan is already being prepared."); }
            var revision = (await _db.DailyProductionPlans.Where(x => x.FactoryId == src.FactoryId && x.PlanDate == src.PlanDate).MaxAsync(x => x.RevisionNo, ct)) + 1;
            var plan = NewPlan(src.FactoryId, src.PlanDate, revision, src.Notes, caller, src.Id, Clean(body.Reason));
            foreach (var i in src.Items)
                plan.Items.Add(new DailyPlanItem
                {
                    PlanId = plan.Id, FactoryId = i.FactoryId, PlanDate = i.PlanDate, OrderId = i.OrderId, AllocationId = i.AllocationId, LineId = i.LineId, ShiftId = i.ShiftId,
                    TargetQuantity = i.TargetQuantity, TargetSource = i.TargetSource, TargetId = i.TargetId, SupervisorUserId = i.SupervisorUserId, Notes = i.Notes,
                });
            _db.DailyProductionPlans.Add(plan);
            _db.DailyPlanEvents.Add(Event(plan, "REVISION_STARTED", $"Revision {revision} of {src.Reference}", Clean(body.Reason), caller));
            _db.DailyPlanEvents.Add(Event(src, "REVISION_STARTED", $"Revision {revision} ({plan.Reference}) started; this plan stays in force until it is published", Clean(body.Reason), caller));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.plan_revision_started", "daily_plan", plan.Id.ToString(), src.Status, plan.Status,
                $"{plan.Reference} revises {src.Reference}: {Clean(body.Reason)}", traceId, plan.FactoryId));
            try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { await tx.RollbackAsync(ct); return AccessResult<DailyPlanDto>.Conflict("plan_open_exists", "A revision of this plan was just started."); }
            return AccessResult<DailyPlanDto>.Created(await DtoAsync(plan, caller, IotSourceTypes.Real, ct));
        });
    }

    public async Task<AccessResult<DailyPlanDto>> CancelAsync(AccessContext caller, Guid id, DailyPlanTransition body, string? traceId, CancellationToken ct)
    {
        var p = await LoadAsync(caller, id, ct);
        if (p.Error is not null) return p.Error;
        var plan = p.Plan!;
        var check = Guard(plan, body, PlanStatuses.Cancelled, requireReason: true);
        if (check is not null) return check;
        if (plan.Status == PlanStatuses.Published)
        {
            if (!caller.Has(Permissions.PlanningPlansPublish)) return AccessResult<DailyPlanDto>.Forbidden("publish_permission_required", "Cancelling a published plan needs the publish permission.");
            var clock = await FactoryClock.LoadAsync(_db, plan.FactoryId, ct);
            if (plan.PlanDate < clock.Today(DateTimeOffset.UtcNow)) return Bad("date_in_past", "A plan for a date that has passed can't be cancelled: its record stays as it was.");
        }
        plan.Status = PlanStatuses.Cancelled; plan.ClosedAt = DateTimeOffset.UtcNow; plan.ClosedReason = Clean(body.Reason);
        return await MoveAsync(plan, caller, "CANCELLED", "planning.plan_cancelled", Clean(body.Reason), traceId, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AccessResult<DailyPlanDto> Bad(string code, string message) => AccessResult<DailyPlanDto>.Invalid(code, message);

    private sealed record Loaded(DailyProductionPlan? Plan, AccessResult<DailyPlanDto>? Error);

    private async Task<Loaded> LoadAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var plan = await Plans(_db, caller, tracking: true).Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return new Loaded(null, AccessResult<DailyPlanDto>.NotFound("plan_not_found", "Plan not found in your scope."));
        if (caller.IsLineScoped) return new Loaded(null, AccessResult<DailyPlanDto>.Forbidden("plan_scope", "Daily plans are prepared at factory level."));
        return new Loaded(plan, null);
    }

    private async Task<Loaded> LoadForEditAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var l = await LoadAsync(caller, id, ct);
        if (l.Error is not null) return l;
        if (!PlanStatuses.Editable(l.Plan!.Status))
            return new Loaded(null, AccessResult<DailyPlanDto>.Conflict("plan_not_editable", $"A {l.Plan.Status.ToLowerInvariant().Replace('_', ' ')} plan can't be edited. " +
                (l.Plan.Status == PlanStatuses.Published ? "Start a revision." : "Return it to draft first.")));
        return l;
    }

    private static AccessResult<DailyPlanDto>? Guard(DailyProductionPlan plan, DailyPlanTransition body, string to, bool requireReason)
    {
        if (body.ExpectedVersion is null) return Bad("version_required", "expectedVersion is required.");
        if (body.ExpectedVersion != plan.Version) return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again.");
        if (!PlanStatuses.CanMove(plan.Status, to))
            return AccessResult<DailyPlanDto>.Conflict("invalid_status_transition", $"A {plan.Status.ToLowerInvariant().Replace('_', ' ')} plan can't become {to.ToLowerInvariant().Replace('_', ' ')}.");
        if (requireReason && Clean(body.Reason) is null) return Bad("reason_required", "Give the reason.");
        if (body.Reason is { Length: > 500 }) return Bad("reason_invalid", "The reason is limited to 500 characters.");
        return null;
    }

    private static DailyProductionPlan NewPlan(Guid factoryId, DateOnly date, int revision, string? notes, AccessContext by, Guid? basedOn, string? revisionReason)
    {
        var plan = new DailyProductionPlan
        {
            FactoryId = factoryId, PlanDate = date, RevisionNo = revision, Notes = notes, BasedOnPlanId = basedOn, RevisionReason = revisionReason,
            CreatedByUserId = by.UserId, CreatedByEmail = by.Email,
        };
        plan.Reference = $"DP-{date:yyyyMMdd}-{plan.Id.ToString("N")[..8].ToUpperInvariant()}";
        return plan;
    }

    private static DailyPlanEvent Event(DailyProductionPlan plan, string action, string? details, string? reason, AccessContext by) => new()
    {
        PlanId = plan.Id, FactoryId = plan.FactoryId, Action = action, Details = details, Reason = reason, ByUserId = by.UserId, ByEmail = by.Email,
    };

    private async Task<AccessResult<DailyPlanDto>> MoveAsync(DailyProductionPlan plan, AccessContext caller, string eventAction, string auditAction, string? reason, string? traceId, CancellationToken ct)
    {
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        _db.DailyPlanEvents.Add(Event(plan, eventAction, null, reason, caller));
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, auditAction, "daily_plan", plan.Id.ToString(), null, plan.Status, $"{plan.Reference} for {Date(plan.PlanDate)}{(reason is null ? "" : ": " + reason)}", traceId, plan.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again."); }
        return AccessResult<DailyPlanDto>.Ok(await DtoAsync(plan, caller, IotSourceTypes.Real, ct));
    }

    private async Task<AccessResult<DailyPlanDto>> SaveAsync(DailyProductionPlan plan, AccessContext caller, string? traceId, string? auditAction, CancellationToken ct)
    {
        if (auditAction is not null)
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, auditAction, "daily_plan", plan.Id.ToString(), null, plan.Status, $"{plan.Reference} for {Date(plan.PlanDate)}", traceId, plan.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<DailyPlanDto>.Conflict("version_conflict", "The plan changed since you loaded it. Reload and try again."); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return AccessResult<DailyPlanDto>.Conflict("item_exists", "This line already has an item for this shift in the plan."); }
        return AccessResult<DailyPlanDto>.Ok(await DtoAsync(plan, caller, IotSourceTypes.Real, ct));
    }

    private static string ItemText(DailyPlanItem i, string? order, string? line, string? shift) => $"{order ?? "order"} · {line ?? i.LineId} · {shift ?? "shift"} · target {i.TargetQuantity}";

    private sealed record ItemValidation(DailyPlanItem? Item, AccessResult<DailyPlanDto>? Error, string? OrderNumber, string? LineName, string? ShiftName);

    /// <summary>Checks one item against the order, allocation, line, shift and target configuration and returns the item to store.</summary>
    private async Task<ItemValidation> ValidateItemAsync(AccessContext caller, DailyProductionPlan plan, PlanItemWrite b, DailyPlanItem? existing, CancellationToken ct)
    {
        static ItemValidation Fail(AccessResult<DailyPlanDto> e) => new(null, e, null, null, null);
        var orderId = b.OrderId ?? existing?.OrderId;
        var lineId = Clean(b.LineId) ?? existing?.LineId;
        var shiftId = b.ShiftId ?? existing?.ShiftId;
        if (orderId is null) return Fail(Bad("order_required", "orderId is required."));
        if (lineId is null) return Fail(Bad("line_required", "lineId is required."));
        if (shiftId is null) return Fail(Bad("shift_required", "shiftId is required."));
        if (b.Notes is { Length: > 500 }) return Fail(Bad("notes_invalid", "Notes are limited to 500 characters."));

        var order = await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.FactoryId == plan.FactoryId, ct);
        if (order is null) return Fail(AccessResult<DailyPlanDto>.NotFound("order_not_found", "The order isn't produced in this factory."));
        if (!OrderLifecycle.IsLive(order.Status)) return Fail(AccessResult<DailyPlanDto>.Conflict("order_not_live", $"Only planned or in-progress orders can be scheduled; {order.OrderNumber} is {ProductionWire.OrderStatus(order.Status)}."));
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId && l.FactoryId == plan.FactoryId, ct);
        if (line is null) return Fail(AccessResult<DailyPlanDto>.NotFound("line_not_found", "The line isn't in this factory."));
        if (!caller.CanAccessLine(line.Id, line.FactoryId)) return Fail(AccessResult<DailyPlanDto>.Forbidden("line_out_of_scope", "You can only plan your own lines."));
        if (!line.IsActive) return Fail(AccessResult<DailyPlanDto>.Conflict("line_inactive", "The line is deactivated."));
        var clock = await FactoryClock.LoadAsync(_db, plan.FactoryId, ct);
        var shift = clock.Shift(shiftId.Value);
        if (shift is null) return Fail(AccessResult<DailyPlanDto>.NotFound("shift_not_found", "The shift isn't an active shift of this factory."));
        if (!shift.WorksOn(plan.PlanDate.DayOfWeek)) return Fail(AccessResult<DailyPlanDto>.Conflict("shift_not_working", $"The {shift.Name} shift doesn't work on {plan.PlanDate:dddd}."));

        // The allocation that backs the item.
        var allocs = await _db.ProductionAllocations.AsNoTracking().Where(a => a.OrderId == orderId && a.LineId == lineId && (a.Status == AllocationStatuses.Planned || a.Status == AllocationStatuses.Active)
                                                                               && a.PlannedStart <= plan.PlanDate && a.PlannedEnd >= plan.PlanDate).ToListAsync(ct);
        var alloc = b.AllocationId is { } aid ? allocs.FirstOrDefault(a => a.Id == aid) : allocs.FirstOrDefault();
        if (alloc is null)
            return Fail(AccessResult<DailyPlanDto>.Conflict("no_allocation", $"{order.OrderNumber} isn't allocated to {line.Name} on {Date(plan.PlanDate)}. Allocate the order to the line first (Line allocations)."));

        // The quantity: typed, or taken from the configured target in force.
        int qty; string source = "MANUAL"; Guid? targetId = null;
        if (b.TargetQuantity is { } typed && b.UseConfiguredTarget != true)
        {
            if (typed is < 1 or > 10_000_000) return Fail(Bad("quantity_invalid", "The target must be from 1 to 10,000,000 pieces."));
            qty = typed;
        }
        else
        {
            var t = await new TargetService(_db, _options).ResolveAsync(plan.FactoryId, line.Id, shift.Id, orderId, plan.PlanDate, ct);
            if (t is null) return Fail(Bad("no_configured_target", "No target is configured for this line and shift on this date: enter the quantity."));
            var converted = ConvertTarget(t, shift, clock, plan.PlanDate);
            if (converted is not { } cq) return Fail(Bad("target_not_convertible", "The configured target can't be turned into a shift target; enter the quantity."));
            qty = cq; source = "TARGET"; targetId = t.Id;
        }
        var item = new DailyPlanItem
        {
            Id = existing?.Id ?? Guid.NewGuid(), PlanId = plan.Id, FactoryId = plan.FactoryId, PlanDate = plan.PlanDate, OrderId = order.Id, AllocationId = alloc.Id, LineId = line.Id,
            ShiftId = shift.Id, TargetQuantity = qty, TargetSource = source, TargetId = targetId, SupervisorUserId = b.SupervisorUserId ?? alloc.SupervisorUserId, Notes = Clean(b.Notes),
        };
        return new ItemValidation(item, null, order.OrderNumber, line.Name, shift.Name);
    }

    /// <summary>A line target as a pieces-per-shift quantity: shift targets as they are, daily targets split by productive minutes, hourly targets times the shift's productive hours.</summary>
    internal static int? ConvertTarget(ProductionTarget t, PlanShift shift, FactoryClock clock, DateOnly date)
    {
        switch (t.Period)
        {
            case TargetPeriods.Shift: return t.Quantity;
            case TargetPeriods.Hourly:
                var hours = shift.ProductiveMinutes / 60.0;
                return hours <= 0 ? null : (int)Math.Max(1, Math.Round(t.Quantity * hours, MidpointRounding.AwayFromZero));
            default:
                if (t.ShiftId == shift.Id) return t.Quantity;
                var dayMinutes = clock.ProductiveMinutes(date);
                return dayMinutes <= 0 ? null : (int)Math.Max(1, Math.Round(t.Quantity * (shift.ProductiveMinutes / dayMinutes), MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>Re-checks every item of a plan (orders, allocations, shifts may have changed since they were added).</summary>
    private async Task<IReadOnlyList<string>> RevalidateAsync(DailyProductionPlan plan, CancellationToken ct)
    {
        var problems = new List<string>();
        var clock = await FactoryClock.LoadAsync(_db, plan.FactoryId, ct);
        var orderIds = plan.Items.Select(i => i.OrderId).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == plan.FactoryId).ToDictionaryAsync(l => l.Id, ct);
        var allocs = await _db.ProductionAllocations.AsNoTracking().Where(a => orderIds.Contains(a.OrderId) && (a.Status == AllocationStatuses.Planned || a.Status == AllocationStatuses.Active)
                                                                              && a.PlannedStart <= plan.PlanDate && a.PlannedEnd >= plan.PlanDate).ToListAsync(ct);
        foreach (var i in plan.Items)
        {
            var o = orders.GetValueOrDefault(i.OrderId);
            var label = $"{o?.OrderNumber ?? "order"} on {lines.GetValueOrDefault(i.LineId)?.Name ?? i.LineId}";
            if (o is null || !OrderLifecycle.IsLive(o.Status)) problems.Add($"{label}: the order is no longer planned or in progress.");
            else if (lines.GetValueOrDefault(i.LineId) is not { IsActive: true }) problems.Add($"{label}: the line is deactivated.");
            else if (!allocs.Any(a => a.OrderId == i.OrderId && a.LineId == i.LineId)) problems.Add($"{label}: no open allocation covers {Date(plan.PlanDate)}.");
            else if (clock.Shift(i.ShiftId) is not { } s || !s.WorksOn(plan.PlanDate.DayOfWeek)) problems.Add($"{label}: the shift is inactive or doesn't work on {Date(plan.PlanDate)}.");
        }
        return problems;
    }

    // ── DTO ─────────────────────────────────────────────────────────────────

    private async Task<DailyPlanDto> DtoAsync(DailyProductionPlan plan, AccessContext caller, string source, CancellationToken ct)
    {
        var factory = await _db.Factories.AsNoTracking().FirstAsync(f => f.Id == plan.FactoryId, ct);
        var clock = await FactoryClock.LoadAsync(_db, plan.FactoryId, ct);
        var now = DateTimeOffset.UtcNow;
        var items = plan.Items.AsEnumerable();
        if (caller.IsLineScoped) { var ls = caller.LineIds; items = items.Where(i => ls.Contains(i.LineId)); }
        var itemList = items.OrderBy(i => i.LineId).ThenBy(i => clock.Shift(i.ShiftId)?.Start).ToList();
        var orderIds = itemList.Select(i => i.OrderId).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var styleIds = orders.Values.Select(o => o.StyleId).Distinct().ToList();
        var styles = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).Where(s => styleIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var lineIds = itemList.Select(i => i.LineId).Distinct().ToList();
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var settings = await _db.LineCapacitySettings.AsNoTracking().Where(s => lineIds.Contains(s.LineId)).ToDictionaryAsync(s => s.LineId, ct);
        var assigned = await _db.Assignments.AsNoTracking().Where(a => lineIds.Contains(a.LineId) && a.EndAt == null && a.Status == "Active").GroupBy(a => a.LineId)
            .Select(g => new { g.Key, N = g.Select(x => x.WorkerId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        IReadOnlyDictionary<Guid, ItemEvaluation> eval = new Dictionary<Guid, ItemEvaluation>();
        if (PlanStatuses.VisibleToFollowers(plan.Status))
            eval = (await new PlanProgressCalculator(_db).EvaluateAsync(plan.FactoryId, itemList, clock, source, now, ct)).ToDictionary(e => e.Item.Id);

        var planWarnings = new List<string>();
        var dtos = new List<PlanItemDto>();
        foreach (var i in itemList)
        {
            var order = orders.GetValueOrDefault(i.OrderId);
            var style = order is null ? null : styles.GetValueOrDefault(order.StyleId);
            var shift = clock.Shift(i.ShiftId);
            var warnings = new List<string>();
            if (PlanStatuses.IsOpenDraft(plan.Status) && shift is not null)
                warnings.AddRange(CapacityWarnings(i, shift, style, settings.GetValueOrDefault(i.LineId), assigned.GetValueOrDefault(i.LineId), plan.PlanDate));
            var e = eval.GetValueOrDefault(i.Id);
            dtos.Add(new PlanItemDto(i.Id, i.OrderId, order?.OrderNumber ?? "?", order?.Buyer, style?.Code, i.AllocationId, i.LineId, lines.GetValueOrDefault(i.LineId, i.LineId), i.ShiftId,
                shift?.Name ?? "?", i.TargetQuantity, i.TargetSource, i.TargetId, shift is null ? null : PlanMath.HourlyTargetOf(i.TargetQuantity, shift.ProductiveMinutes), i.SupervisorUserId,
                i.Notes, e?.Dto, warnings));
        }
        if (itemList.Count > 0)
        {
            var allocIds = itemList.Where(i => i.AllocationId != null).Select(i => i.AllocationId!.Value).Distinct().ToList();
            var allocs = await _db.ProductionAllocations.AsNoTracking().Where(a => allocIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            foreach (var g in itemList.Where(i => i.AllocationId != null).GroupBy(i => i.AllocationId!.Value))
            {
                var a = allocs.GetValueOrDefault(g.Key);
                var sum = g.Sum(x => x.TargetQuantity);
                if (a is not null && sum > a.Quantity) planWarnings.Add($"Targets for allocation {a.LineId} total {sum} on this day, more than the allocation's {a.Quantity} pieces.");
            }
            foreach (var g in itemList.GroupBy(i => i.OrderId))
            {
                var o = orders.GetValueOrDefault(g.Key);
                var sum = g.Sum(x => x.TargetQuantity);
                if (o is not null && sum > o.Quantity) planWarnings.Add($"{o.OrderNumber}: today's targets ({sum}) exceed the order quantity ({o.Quantity}).");
            }
        }
        var events = await _db.DailyPlanEvents.AsNoTracking().Where(e2 => e2.PlanId == plan.Id).OrderBy(e2 => e2.At).ToListAsync(ct);
        return new DailyPlanDto(plan.Id, plan.Reference, plan.FactoryId, factory.Name, Date(plan.PlanDate), plan.RevisionNo, plan.Status, plan.BasedOnPlanId, plan.RevisionReason, plan.Notes,
            plan.SubmittedAt, plan.SubmittedByEmail, plan.ApprovedAt, plan.ApprovedByEmail, plan.PublishedAt, plan.PublishedByEmail, plan.ClosedAt, plan.ClosedReason, plan.CreatedAt, plan.CreatedByEmail,
            plan.UpdatedAt, plan.Version, dtos, events.Select(e2 => new DailyPlanEventDto(e2.Id, e2.Action, e2.Details, e2.Reason, e2.At, e2.ByEmail)).ToList(), planWarnings, Actions(plan, caller));
    }

    /// <summary>What the plan's status and the caller's permissions allow, for the UI (the API enforces every one).</summary>
    private static IReadOnlyList<string> Actions(DailyProductionPlan plan, AccessContext c)
    {
        var a = new List<string>();
        if (c.IsLineScoped) return a;
        var manage = c.Has(Permissions.PlanningPlansManage);
        if (manage && plan.Status == PlanStatuses.Draft) { a.Add("edit"); a.Add("submit"); a.Add("cancel"); }
        if (manage && plan.Status == PlanStatuses.UnderReview) a.Add("cancel");
        if (c.Has(Permissions.PlanningPlansApprove) && plan.Status == PlanStatuses.UnderReview) { a.Add("approve"); a.Add("return"); }
        if (c.Has(Permissions.PlanningPlansApprove) && plan.Status == PlanStatuses.Approved) a.Add("return");
        if (c.Has(Permissions.PlanningPlansPublish) && plan.Status == PlanStatuses.Approved) a.Add("publish");
        if (manage && plan.Status == PlanStatuses.Approved) a.Add("cancel");
        if (manage && plan.Status == PlanStatuses.Published) a.Add("revise");
        if (c.Has(Permissions.PlanningPlansPublish) && plan.Status == PlanStatuses.Published) a.Add("cancel");
        return a;
    }

    /// <summary>Whether the item's shift target is within what the line is estimated to produce in that shift (one-day, single-shift estimate).</summary>
    private static IEnumerable<string> CapacityWarnings(DailyPlanItem i, PlanShift shift, ProductionStyle? style, LineCapacitySetting? s, int assigned, DateOnly date)
    {
        if (style is null) yield break;
        var (smv, basis) = StyleSmv.Resolve(style.ApprovedSmv, style.Operations.Select(o => o.Smv));
        int? ops = s?.PlannedOperators ?? (assigned > 0 ? assigned : null);
        var input = new CapacityInput(smv, smv is null ? null : ProductionWire.SmvBasisName(basis), s?.EfficiencyPct, ops, null, null,
            new[] { new CapacityDay(date, shift.ProductiveMinutes) }, i.TargetQuantity, Array.Empty<CapacityCommitment>(), Array.Empty<MachineTypeCheck>());
        var r = CapacityEstimator.Estimate(input);
        if (r.Status == CapacityEstimator.Incomplete)
            yield return $"Capacity not assessed: {string.Join(" ", r.Missing.Select(m => m.Message))}";
        else if (r.ShortagePieces is > 0)
            yield return $"Target {i.TargetQuantity} is above the estimated capacity of {r.CapacityPieces} pieces for this shift (estimate from configured inputs).";
    }
}
