using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Planning.PlanningScope;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>
/// Production targets with their history. A target is a chain of versions
/// (same target key): changing one adds a version that takes over on its
/// effective date and closes the older one, so the target in force on any
/// earlier date can always be read back. Factory-, department- and
/// floor-level targets are agreed by a second person before they apply.
/// Supervisors set targets only for their own lines.
/// </summary>
public sealed class TargetService : ITargetService
{
    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public TargetService(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    // ── Reads ───────────────────────────────────────────────────────────────

    public async Task<AccessResult<PagedResult<TargetDto>>> ListAsync(AccessContext caller, TargetQuery q, CancellationToken ct)
    {
        if (q.Page < 1 || q.PageSize is < 1 or > 200) return AccessResult<PagedResult<TargetDto>>.Invalid("paging_invalid", "page must be 1 or more and pageSize from 1 to 200.");
        string? level = null;
        if (q.Level is not null && (level = TargetLevels.Parse(q.Level)) is null)
            return AccessResult<PagedResult<TargetDto>>.Invalid("level_invalid", $"level must be one of: {string.Join(", ", TargetLevels.All)}.");
        if (q.FactoryId is { } f && !caller.CanAccessFactory(f)) return AccessResult<PagedResult<TargetDto>>.Forbidden("factory_out_of_scope", "You can only see your own factories.");
        if (q.WithPerformance && q.Date is null) return AccessResult<PagedResult<TargetDto>>.Invalid("date_required", "Performance needs a date.");
        var query = Targets(_db, caller);
        if (q.FactoryId is { } fid) query = query.Where(t => t.FactoryId == fid);
        if (level is not null) query = query.Where(t => t.Level == level);
        if (Clean(q.LineId) is { } line) query = query.Where(t => t.LineId == line);
        if (q.OrderId is { } oid) query = query.Where(t => t.OrderId == oid);
        if (Clean(q.Approval) is { } ap) query = query.Where(t => t.ApprovalStatus == ap.ToUpperInvariant());
        if (q.Date is { } d)
            query = query.Where(t => t.EffectiveFrom <= d && (t.EffectiveTo == null || t.EffectiveTo >= d) && (t.ApprovalStatus == TargetApprovals.NotRequired || t.ApprovalStatus == TargetApprovals.Approved));
        else if (!q.IncludeHistory) query = query.Where(t => !t.IsSuperseded && t.ApprovalStatus != TargetApprovals.Rejected);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(t => t.Level).ThenBy(t => t.LineId).ThenBy(t => t.TargetKey).ThenByDescending(t => t.Revision)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        var dtos = await DtosAsync(rows, ct);
        if (q.WithPerformance && q.Date is { } date)
            dtos = await WithPerformanceAsync(rows, dtos, date, ct);
        return AccessResult<PagedResult<TargetDto>>.Ok(new PagedResult<TargetDto>(dtos, q.Page, q.PageSize, total));
    }

    public async Task<AccessResult<TargetDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var t = await Targets(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return AccessResult<TargetDetailDto>.NotFound("target_not_found", "Target not found in your scope.");
        return AccessResult<TargetDetailDto>.Ok(await DetailAsync(t, ct));
    }

    public async Task<AccessResult<TargetDto?>> EffectiveAsync(AccessContext caller, Guid factoryId, string lineId, Guid shiftId, Guid? orderId, DateOnly date, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId)) return AccessResult<TargetDto?>.Forbidden("factory_out_of_scope", "You can only see your own factories.");
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId && l.FactoryId == factoryId, ct);
        if (line is null || !caller.CanAccessLine(line.Id, line.FactoryId)) return AccessResult<TargetDto?>.NotFound("line_not_found", "Line not found in your scope.");
        var t = await ResolveAsync(factoryId, lineId, shiftId, orderId, date, ct);
        return AccessResult<TargetDto?>.Ok(t is null ? null : (await DtosAsync(new[] { t }, ct))[0]);
    }

    /// <summary>The most specific line target in force on a date for a shift (and order), or null.</summary>
    internal async Task<ProductionTarget?> ResolveAsync(Guid factoryId, string lineId, Guid shiftId, Guid? orderId, DateOnly date, CancellationToken ct)
    {
        var candidates = await _db.ProductionTargets.AsNoTracking()
            .Where(t => t.FactoryId == factoryId && t.Level == TargetLevels.Line && t.LineId == lineId && t.EffectiveFrom <= date && (t.EffectiveTo == null || t.EffectiveTo >= date)
                        && (t.ApprovalStatus == TargetApprovals.NotRequired || t.ApprovalStatus == TargetApprovals.Approved)
                        && (t.ShiftId == null || t.ShiftId == shiftId) && (t.OrderId == null || t.OrderId == orderId))
            .ToListAsync(ct);
        return candidates.OrderByDescending(TargetRules.Specificity).ThenByDescending(t => t.Period == TargetPeriods.Shift ? 1 : 0).ThenByDescending(t => t.CreatedAt).FirstOrDefault();
    }

    // ── Writes ──────────────────────────────────────────────────────────────

    public async Task<AccessResult<TargetDetailDto>> CreateAsync(AccessContext caller, TargetWrite body, string? traceId, CancellationToken ct)
    {
        var v = await ValidateAsync(caller, body, existing: null, ct);
        if (v.Error is not null) return v.Error;
        var t = v.Target!;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockAsync(_db, $"targets:{t.FactoryId}", ct);
            if (await FindDuplicateAsync(t, null, ct) is { } dup)
                return AccessResult<TargetDetailDto>.Conflict("target_exists", $"A target for this scope already covers these dates (version {dup.Revision}). Revise it instead of creating another.");
            t.ApprovalStatus = TargetLevels.RequiresApproval(t.Level) ? TargetApprovals.Pending : TargetApprovals.NotRequired;
            _db.ProductionTargets.Add(t);
            _db.ProductionTargetRevisions.Add(Revision(t, TargetActions.Created, null, null, Clean(body.Reason) ?? "Target created", caller));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.target_created", "production_target", t.Id.ToString(), null, t.ApprovalStatus, Describe(t), traceId, t.FactoryId));
            try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("target_exists", "A conflicting target was just saved."); }
            return AccessResult<TargetDetailDto>.Created(await DetailAsync(t, ct));
        });
    }

    public async Task<AccessResult<TargetDetailDto>> ReviseAsync(AccessContext caller, Guid id, TargetWrite body, string? traceId, CancellationToken ct)
    {
        var current = await Targets(_db, caller, tracking: true).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (current is null) return AccessResult<TargetDetailDto>.NotFound("target_not_found", "Target not found in your scope.");
        var scopeError = CheckLevelAccess(caller, current);
        if (scopeError is not null) return scopeError;
        if (body.ExpectedVersion is null) return AccessResult<TargetDetailDto>.Invalid("version_required", "expectedVersion is required.");
        if (Clean(body.Reason) is null) return AccessResult<TargetDetailDto>.Invalid("reason_required", "Give the reason for the revision: the previous version is kept in the history.");
        if (current.IsSuperseded || current.ApprovalStatus is TargetApprovals.Rejected or TargetApprovals.Pending)
            return AccessResult<TargetDetailDto>.Conflict("target_not_current", "Only the version in force can be revised. Open the target's latest version.");
        if (body.Quantity is null) return AccessResult<TargetDetailDto>.Invalid("quantity_required", "The new quantity is required.");
        if (TargetRules.ValidateQuantity(current.Period, body.Quantity.Value) is { } qe) return AccessResult<TargetDetailDto>.Invalid("quantity_invalid", qe);
        if (!TryDate(body.EffectiveFrom, out var from)) return AccessResult<TargetDetailDto>.Invalid("effective_from_invalid", "effectiveFrom must be a date (yyyy-MM-dd).");
        var clock = await FactoryClock.LoadAsync(_db, current.FactoryId, ct);
        var today = clock.Today(DateTimeOffset.UtcNow);
        if (from < today) return AccessResult<TargetDetailDto>.Invalid("effective_from_past", "A revision can't take effect in the past: history is kept as it was. Choose today or a later date.");
        if (from <= current.EffectiveFrom) return AccessResult<TargetDetailDto>.Invalid("effective_from_order", $"The revision must start after the current version's start ({Date(current.EffectiveFrom)}).");
        DateOnly? to = current.EffectiveTo;
        if (body.EffectiveTo is not null && !TryDate(body.EffectiveTo, out var parsedTo)) return AccessResult<TargetDetailDto>.Invalid("effective_to_invalid", "effectiveTo must be a date (yyyy-MM-dd).");
        else if (body.EffectiveTo is not null) { TryDate(body.EffectiveTo, out var p); to = p; }
        if (to is { } end && end < from) return AccessResult<TargetDetailDto>.Invalid("dates_invalid", "The end date can't be before the start date.");
        if (body.Notes is { Length: > 500 }) return AccessResult<TargetDetailDto>.Invalid("notes_invalid", "Notes are limited to 500 characters.");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockAsync(_db, $"targets:{current.FactoryId}", ct);
            await _db.Entry(current).ReloadAsync(ct);
            if (body.ExpectedVersion != current.Version) { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("version_conflict", "The target changed since you loaded it. Reload and try again."); }
            if (current.IsSuperseded) { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("target_not_current", "This version was just replaced. Open the latest version."); }
            if (await _db.ProductionTargets.AnyAsync(x => x.TargetKey == current.TargetKey && x.ApprovalStatus == TargetApprovals.Pending, ct))
            { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("revision_pending", "A revision of this target is already waiting for approval."); }

            var next = new ProductionTarget
            {
                TargetKey = current.TargetKey, Revision = current.Revision + 1, SupersedesId = current.Id, Level = current.Level, Period = current.Period, FactoryId = current.FactoryId,
                DepartmentId = current.DepartmentId, FloorId = current.FloorId, LineId = current.LineId, ShiftId = current.ShiftId, WorkstationId = current.WorkstationId,
                MachineId = current.MachineId, OrderId = current.OrderId, AllocationId = current.AllocationId, Quantity = body.Quantity.Value, EffectiveFrom = from, EffectiveTo = to,
                Notes = Clean(body.Notes) ?? current.Notes, ChangeReason = Clean(body.Reason), CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email,
                ApprovalStatus = TargetLevels.RequiresApproval(current.Level) ? TargetApprovals.Pending : TargetApprovals.NotRequired,
            };
            if (next.ApprovalStatus == TargetApprovals.NotRequired)
            {
                // The older version stops the day before; saved first so the "one version in force" index never sees two.
                Supersede(current, from);
                await _db.SaveChangesAsync(ct);
            }
            _db.ProductionTargets.Add(next);
            _db.ProductionTargetRevisions.Add(Revision(next, TargetActions.Revised, current.Quantity, current.EffectiveFrom, Clean(body.Reason), caller));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.target_revised", "production_target", next.Id.ToString(), $"{current.Quantity}", $"{next.Quantity}",
                $"{Describe(next)} | {Clean(body.Reason)}", traceId, next.FactoryId));
            try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("version_conflict", "The target changed since you loaded it. Reload and try again."); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("revision_pending", "Another revision was just saved. Reload and try again."); }
            return AccessResult<TargetDetailDto>.Created(await DetailAsync(next, ct));
        });
    }

    public async Task<AccessResult<TargetDetailDto>> DecideAsync(AccessContext caller, Guid id, TargetDecision body, string? traceId, CancellationToken ct)
    {
        var t = await Targets(_db, caller, tracking: true).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return AccessResult<TargetDetailDto>.NotFound("target_not_found", "Target not found in your scope.");
        if (body.Approve is null) return AccessResult<TargetDetailDto>.Invalid("decision_required", "approve (true or false) is required.");
        if (body.ExpectedVersion is null) return AccessResult<TargetDetailDto>.Invalid("version_required", "expectedVersion is required.");
        if (body.Approve == false && Clean(body.Note) is null) return AccessResult<TargetDetailDto>.Invalid("note_required", "Give the reason for rejecting the target.");
        if (body.Note is { Length: > 500 }) return AccessResult<TargetDetailDto>.Invalid("note_invalid", "The note is limited to 500 characters.");
        if (t.ApprovalStatus != TargetApprovals.Pending) return AccessResult<TargetDetailDto>.Conflict("target_not_pending", "Only a target waiting for approval can be decided.");
        if (_options.RequireSeparateApprover && t.CreatedByUserId == caller.UserId)
            return AccessResult<TargetDetailDto>.Forbidden("same_approver", "Another person must approve a target you created (PRODUCTION_REQUIRE_SEPARATE_APPROVER).");
        if (body.ExpectedVersion != t.Version) return AccessResult<TargetDetailDto>.Conflict("version_conflict", "The target changed since you loaded it. Reload and try again.");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockAsync(_db, $"targets:{t.FactoryId}", ct);
            var now = DateTimeOffset.UtcNow;
            if (body.Approve == true)
            {
                if (t.SupersedesId is { } prevId && await _db.ProductionTargets.FirstOrDefaultAsync(x => x.Id == prevId, ct) is { } prev && !prev.IsSuperseded)
                {
                    Supersede(prev, t.EffectiveFrom);
                    await _db.SaveChangesAsync(ct);
                }
                else if (t.SupersedesId is null && await FindDuplicateAsync(t, t.Id, ct) is { } dup)
                { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("target_exists", $"Another target for this scope now covers these dates (version {dup.Revision})."); }
                t.ApprovalStatus = TargetApprovals.Approved;
            }
            else t.ApprovalStatus = TargetApprovals.Rejected;
            t.DecidedAt = now; t.DecidedByEmail = caller.Email; t.DecisionNote = Clean(body.Note);
            _db.ProductionTargetRevisions.Add(Revision(t, body.Approve == true ? TargetActions.Approved : TargetActions.Rejected, null, null, Clean(body.Note), caller));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, body.Approve == true ? "planning.target_approved" : "planning.target_rejected", "production_target", t.Id.ToString(),
                TargetApprovals.Pending, t.ApprovalStatus, $"{Describe(t)} | {Clean(body.Note)}", traceId, t.FactoryId));
            try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("version_conflict", "The target changed since you loaded it. Reload and try again."); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { await tx.RollbackAsync(ct); return AccessResult<TargetDetailDto>.Conflict("target_exists", "A conflicting target version is in force."); }
            return AccessResult<TargetDetailDto>.Ok(await DetailAsync(t, ct));
        });
    }

    public async Task<AccessResult<TargetDetailDto>> EndAsync(AccessContext caller, Guid id, TargetEnd body, string? traceId, CancellationToken ct)
    {
        var t = await Targets(_db, caller, tracking: true).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return AccessResult<TargetDetailDto>.NotFound("target_not_found", "Target not found in your scope.");
        var scopeError = CheckLevelAccess(caller, t);
        if (scopeError is not null) return scopeError;
        if (body.ExpectedVersion is null) return AccessResult<TargetDetailDto>.Invalid("version_required", "expectedVersion is required.");
        if (Clean(body.Reason) is null) return AccessResult<TargetDetailDto>.Invalid("reason_required", "Give the reason for ending the target.");
        if (!TryDate(body.EndDate, out var end)) return AccessResult<TargetDetailDto>.Invalid("end_invalid", "endDate must be a date (yyyy-MM-dd).");
        if (t.IsSuperseded || t.ApprovalStatus is not (TargetApprovals.NotRequired or TargetApprovals.Approved))
            return AccessResult<TargetDetailDto>.Conflict("target_not_current", "Only the version in force can be ended.");
        var clock = await FactoryClock.LoadAsync(_db, t.FactoryId, ct);
        if (end < clock.Today(DateTimeOffset.UtcNow)) return AccessResult<TargetDetailDto>.Invalid("end_past", "A target can't be ended in the past: history is kept as it was.");
        if (end < t.EffectiveFrom) return AccessResult<TargetDetailDto>.Invalid("dates_invalid", "The end date can't be before the target's start date.");
        if (body.ExpectedVersion != t.Version) return AccessResult<TargetDetailDto>.Conflict("version_conflict", "The target changed since you loaded it. Reload and try again.");
        t.EffectiveTo = end;
        _db.ProductionTargetRevisions.Add(Revision(t, TargetActions.Ended, null, null, Clean(body.Reason), caller));
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.target_ended", "production_target", t.Id.ToString(), null, Date(end), $"{Describe(t)} | {Clean(body.Reason)}", traceId, t.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<TargetDetailDto>.Conflict("version_conflict", "The target changed since you loaded it. Reload and try again."); }
        return AccessResult<TargetDetailDto>.Ok(await DetailAsync(t, ct));
    }

    // ── Validation & helpers ────────────────────────────────────────────────

    private sealed record Validated(ProductionTarget? Target, AccessResult<TargetDetailDto>? Error);

    private async Task<Validated> ValidateAsync(AccessContext caller, TargetWrite b, ProductionTarget? existing, CancellationToken ct)
    {
        static Validated Bad(string code, string message) => new(null, AccessResult<TargetDetailDto>.Invalid(code, message));
        var level = TargetLevels.Parse(b.Level);
        if (level is null) return Bad("level_invalid", $"level must be one of: {string.Join(", ", TargetLevels.All)}.");
        var period = TargetPeriods.Parse(b.Period ?? TargetPeriods.Daily);
        if (period is null) return Bad("period_invalid", $"period must be one of: {string.Join(", ", TargetPeriods.All)}.");
        if (b.Quantity is null) return Bad("quantity_required", "Quantity is required.");
        if (TargetRules.ValidateQuantity(period, b.Quantity.Value) is { } qe) return Bad("quantity_invalid", qe);
        if (!TryDate(b.EffectiveFrom, out var from)) return Bad("effective_from_invalid", "effectiveFrom must be a date (yyyy-MM-dd).");
        DateOnly? to = null;
        if (Clean(b.EffectiveTo) is not null) { if (!TryDate(b.EffectiveTo, out var tt)) return Bad("effective_to_invalid", "effectiveTo must be a date (yyyy-MM-dd)."); to = tt; }
        if (to is { } e && e < from) return Bad("dates_invalid", "The end date can't be before the start date.");
        if (b.Notes is { Length: > 500 }) return Bad("notes_invalid", "Notes are limited to 500 characters.");

        var fr = await PlanningSetupService.ResolveFactoryAsync(_db, caller, b.FactoryId, ct);
        if (fr.Outcome != AccessOutcome.Ok) return new Validated(null, new AccessResult<TargetDetailDto>(fr.Outcome, null, fr.Code, fr.Message));
        var factory = fr.Value!;
        var lineId = Clean(b.LineId);
        foreach (var (field, message) in TargetRules.ValidateScope(level, new TargetRules.Scope(b.DepartmentId, b.FloorId, lineId, b.ShiftId, b.WorkstationId, b.MachineId)))
            return Bad($"{field}_invalid", message);

        // Line-scoped callers (supervisors) set targets for their own lines only.
        if (caller.IsLineScoped && level is TargetLevels.Factory or TargetLevels.Department or TargetLevels.Floor or TargetLevels.Shift)
            return new Validated(null, AccessResult<TargetDetailDto>.Forbidden("target_level_not_allowed", $"Targets at {level.ToLowerInvariant()} level are set by a factory manager."));

        // Every referenced record must belong to the factory.
        string? lineForScope = lineId;
        if (b.DepartmentId is { } dep && !await _db.Departments.AnyAsync(x => x.Id == dep && x.FactoryId == factory.Id, ct)) return Bad("department_invalid", "The department isn't in this factory.");
        if (b.FloorId is { } fl && !await _db.ProductionFloors.AnyAsync(x => x.Id == fl && x.FactoryId == factory.Id, ct)) return Bad("floor_invalid", "The floor isn't in this factory.");
        if (b.ShiftId is { } sh && !await _db.Shifts.AnyAsync(x => x.Id == sh && x.FactoryId == factory.Id && x.IsActive, ct)) return Bad("shift_invalid", "The shift isn't an active shift of this factory.");
        if (b.WorkstationId is { } ws)
        {
            var w = await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ws && x.FactoryId == factory.Id, ct);
            if (w is null) return Bad("workstation_invalid", "The workstation isn't in this factory.");
            lineForScope = w.LineId;
        }
        if (b.MachineId is { } mc)
        {
            var m = await _db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mc && x.FactoryId == factory.Id, ct);
            if (m is null) return Bad("machine_invalid", "The machine isn't in this factory.");
            if (m.WorkstationId is { } mw) lineForScope = (await _db.Workstations.AsNoTracking().Where(x => x.Id == mw).Select(x => x.LineId).FirstOrDefaultAsync(ct)) ?? lineForScope;
        }
        if (lineId is not null)
        {
            var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == lineId && x.FactoryId == factory.Id, ct);
            if (line is null) return Bad("line_invalid", "The line isn't in this factory.");
        }
        if (lineForScope is not null)
        {
            var lf = await _db.ProductionLines.AsNoTracking().Where(x => x.Id == lineForScope).Select(x => x.FactoryId).FirstOrDefaultAsync(ct);
            if (!caller.CanAccessLine(lineForScope, lf)) return new Validated(null, AccessResult<TargetDetailDto>.Forbidden("line_out_of_scope", "You can only set targets for your own lines."));
        }
        else if (caller.IsLineScoped) return new Validated(null, AccessResult<TargetDetailDto>.Forbidden("target_level_not_allowed", "Choose one of your lines."));

        Guid? orderId = b.OrderId, allocationId = b.AllocationId;
        if (allocationId is { } aid)
        {
            var a = await _db.ProductionAllocations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == aid && x.FactoryId == factory.Id, ct);
            if (a is null) return Bad("allocation_invalid", "The allocation isn't in this factory.");
            if (orderId is not null && orderId != a.OrderId) return Bad("allocation_invalid", "The allocation belongs to another order.");
            if (level != TargetLevels.Line || lineId != a.LineId) return Bad("allocation_invalid", "An allocation target is a line target for the allocation's own line.");
            orderId = a.OrderId;
        }
        if (orderId is { } oid)
        {
            var o = await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == oid && x.FactoryId == factory.Id, ct);
            if (o is null) return Bad("order_invalid", "The order isn't produced in this factory.");
            if (!OrderLifecycle.IsLive(o.Status)) return Bad("order_invalid", "Order-specific targets need a planned or in-progress order.");
            if (level is TargetLevels.Factory or TargetLevels.Department or TargetLevels.Floor or TargetLevels.Shift) return Bad("order_invalid", "An order-specific target is set on a line (or lower).");
        }
        var t = new ProductionTarget
        {
            Level = level, Period = period, FactoryId = factory.Id, DepartmentId = b.DepartmentId, FloorId = b.FloorId, LineId = lineId, ShiftId = b.ShiftId,
            WorkstationId = b.WorkstationId, MachineId = b.MachineId, OrderId = orderId, AllocationId = allocationId, Quantity = b.Quantity.Value, EffectiveFrom = from,
            EffectiveTo = to, Notes = Clean(b.Notes), ChangeReason = Clean(b.Reason), CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email,
        };
        return new Validated(t, null);
    }

    /// <summary>Whether the caller may change a target of this level (supervisors: only line-or-lower targets on their lines).</summary>
    private static AccessResult<TargetDetailDto>? CheckLevelAccess(AccessContext caller, ProductionTarget t)
    {
        if (caller.IsLineScoped && (t.LineId is null || !caller.LineIds.Contains(t.LineId)))
            return AccessResult<TargetDetailDto>.Forbidden("line_out_of_scope", "You can only change targets of your own lines.");
        return null;
    }

    /// <summary>A different target key with the same scope whose dates overlap (and that is in force or pending).</summary>
    private async Task<ProductionTarget?> FindDuplicateAsync(ProductionTarget t, Guid? exceptId, CancellationToken ct)
    {
        var to = t.EffectiveTo ?? DateOnly.MaxValue;
        return await _db.ProductionTargets.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id != (exceptId ?? Guid.Empty) && x.TargetKey != t.TargetKey && !x.IsSuperseded && x.ApprovalStatus != TargetApprovals.Rejected
            && x.FactoryId == t.FactoryId && x.Level == t.Level && x.Period == t.Period && x.DepartmentId == t.DepartmentId && x.FloorId == t.FloorId && x.LineId == t.LineId
            && x.ShiftId == t.ShiftId && x.WorkstationId == t.WorkstationId && x.MachineId == t.MachineId && x.OrderId == t.OrderId && x.AllocationId == t.AllocationId
            && x.EffectiveFrom <= to && (x.EffectiveTo == null || x.EffectiveTo >= t.EffectiveFrom), ct);
    }

    private static void Supersede(ProductionTarget old, DateOnly newFrom)
    {
        old.IsSuperseded = true;
        var lastDay = newFrom.AddDays(-1);
        if (old.EffectiveTo is null || old.EffectiveTo > lastDay) old.EffectiveTo = lastDay;
    }

    private static ProductionTargetRevision Revision(ProductionTarget t, string action, int? previousQuantity, DateOnly? previousFrom, string? reason, AccessContext by) => new()
    {
        TargetId = t.Id, TargetKey = t.TargetKey, Revision = t.Revision, Action = action, PreviousQuantity = previousQuantity, NewQuantity = t.Quantity,
        PreviousEffectiveFrom = previousFrom, NewEffectiveFrom = t.EffectiveFrom, Reason = reason, ByUserId = by.UserId, ByEmail = by.Email,
    };

    private static string Describe(ProductionTarget t) =>
        $"{t.Level.ToLowerInvariant()} target v{t.Revision}: {t.Quantity} per {t.Period.ToLowerInvariant()}, from {Date(t.EffectiveFrom)}{(t.EffectiveTo is { } e ? " to " + Date(e) : "")}";

    private async Task<TargetDetailDto> DetailAsync(ProductionTarget t, CancellationToken ct)
    {
        var versions = await _db.ProductionTargets.AsNoTracking().Where(x => x.TargetKey == t.TargetKey).OrderBy(x => x.Revision).ToListAsync(ct);
        var revisions = await _db.ProductionTargetRevisions.AsNoTracking().Where(r => r.TargetKey == t.TargetKey).OrderBy(r => r.At).ToListAsync(ct);
        var dtos = await DtosAsync(versions, ct);
        return new TargetDetailDto(dtos.First(d => d.Id == t.Id), dtos,
            revisions.Select(r => new TargetRevisionDto(r.Id, r.TargetId, r.Revision, r.Action, r.PreviousQuantity, r.NewQuantity, r.PreviousEffectiveFrom is { } p ? Date(p) : null,
                Date(r.NewEffectiveFrom), r.Reason, r.At, r.ByEmail)).ToList());
    }

    internal async Task<IReadOnlyList<TargetDto>> DtosAsync(IReadOnlyList<ProductionTarget> rows, CancellationToken ct)
    {
        var deptIds = rows.Where(r => r.DepartmentId != null).Select(r => r.DepartmentId!.Value).Distinct().ToList();
        var floorIds = rows.Where(r => r.FloorId != null).Select(r => r.FloorId!.Value).Distinct().ToList();
        var lineIds = rows.Where(r => r.LineId != null).Select(r => r.LineId!).Distinct().ToList();
        var shiftIds = rows.Where(r => r.ShiftId != null).Select(r => r.ShiftId!.Value).Distinct().ToList();
        var wsIds = rows.Where(r => r.WorkstationId != null).Select(r => r.WorkstationId!.Value).Distinct().ToList();
        var mIds = rows.Where(r => r.MachineId != null).Select(r => r.MachineId!.Value).Distinct().ToList();
        var oIds = rows.Where(r => r.OrderId != null).Select(r => r.OrderId!.Value).Distinct().ToList();
        var depts = await _db.Departments.AsNoTracking().Where(x => deptIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var floors = await _db.ProductionFloors.AsNoTracking().Where(x => floorIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var lines = await _db.ProductionLines.AsNoTracking().Where(x => lineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var shifts = await _db.Shifts.AsNoTracking().Where(x => shiftIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var wss = await _db.Workstations.AsNoTracking().Where(x => wsIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var ms = await _db.Machines.AsNoTracking().Where(x => mIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var os = await _db.ProductionOrders.AsNoTracking().Where(x => oIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.OrderNumber, ct);
        var todayByFactory = new Dictionary<Guid, DateOnly>();
        foreach (var f in rows.Select(r => r.FactoryId).Distinct()) todayByFactory[f] = (await FactoryClock.LoadAsync(_db, f, ct)).Today(DateTimeOffset.UtcNow);
        return rows.Select(t =>
        {
            var today = todayByFactory[t.FactoryId];
            var inForce = TargetApprovals.InForce(t.ApprovalStatus) && t.EffectiveFrom <= today && (t.EffectiveTo is null || t.EffectiveTo >= today);
            return new TargetDto(t.Id, t.TargetKey, t.Revision, t.SupersedesId, t.Level, t.Period, t.FactoryId,
                t.DepartmentId, t.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null, t.FloorId, t.FloorId is { } f ? floors.GetValueOrDefault(f) : null,
                t.LineId, t.LineId is { } l ? lines.GetValueOrDefault(l) : null, t.ShiftId, t.ShiftId is { } s ? shifts.GetValueOrDefault(s) : null,
                t.WorkstationId, t.WorkstationId is { } w ? wss.GetValueOrDefault(w) : null, t.MachineId, t.MachineId is { } m ? ms.GetValueOrDefault(m) : null,
                t.OrderId, t.OrderId is { } o ? os.GetValueOrDefault(o) : null, t.AllocationId, t.Quantity, Date(t.EffectiveFrom), t.EffectiveTo is { } to ? Date(to) : null,
                t.ApprovalStatus, t.IsSuperseded, inForce, t.Notes, t.ChangeReason, t.CreatedAt, t.CreatedByEmail, t.DecidedAt, t.DecidedByEmail, t.DecisionNote, t.Version, null);
        }).ToList();
    }

    // ── Performance of a target on a date ───────────────────────────────────

    private async Task<IReadOnlyList<TargetDto>> WithPerformanceAsync(IReadOnlyList<ProductionTarget> rows, IReadOnlyList<TargetDto> dtos, DateOnly date, CancellationToken ct)
    {
        var result = new List<TargetDto>();
        var actuals = new PlanningActuals(_db);
        var now = DateTimeOffset.UtcNow;
        var cache = new Dictionary<Guid, (FactoryClock Clock, IReadOnlyDictionary<string, IReadOnlyList<PlanningActuals.Point>> Points,
            IReadOnlyDictionary<(string Line, DateOnly Date, Guid? Shift), CounterReading> Readings, IReadOnlyDictionary<string, LinePlacement> Placements, IReadOnlyList<string> Lines)>();
        foreach (var (t, dto) in rows.Zip(dtos))
        {
            if (!cache.TryGetValue(t.FactoryId, out var c))
            {
                var clock = await FactoryClock.LoadAsync(_db, t.FactoryId, ct);
                var points = await actuals.PointsAsync(t.FactoryId, IotSourceTypes.Real, ct);
                var readings = await actuals.ReadAsync(t.FactoryId, date, date, IotSourceTypes.Real, points, clock, now, null, ct);
                var lineIds = await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == t.FactoryId).Select(l => l.Id).ToListAsync(ct);
                var placements = await PlanningSetupService.PlacementsAsync(_db, lineIds, ct);
                cache[t.FactoryId] = c = (clock, points, readings, placements, lineIds);
            }
            result.Add(dto with { Performance = await PerformanceAsync(t, date, c.Clock, c.Points, c.Readings, c.Placements, c.Lines, now, ct) });
        }
        return result;
    }

    private async Task<TargetPerformanceDto> PerformanceAsync(ProductionTarget t, DateOnly date, FactoryClock clock, IReadOnlyDictionary<string, IReadOnlyList<PlanningActuals.Point>> points,
        IReadOnlyDictionary<(string Line, DateOnly Date, Guid? Shift), CounterReading> readings, IReadOnlyDictionary<string, LinePlacement> placements, IReadOnlyList<string> factoryLines,
        DateTimeOffset now, CancellationToken ct)
    {
        string d = Date(date);
        if (date > clock.Today(now)) return new TargetPerformanceDto(d, null, null, ActualBases.None, "The date is in the future.");
        int? actual;
        string basis, note;
        if (t.Level is TargetLevels.Workstation or TargetLevels.Machine)
        {
            // A machine's or workstation's own counter: operations done there, not finished garments.
            var q = _db.ProductionCounterEvents.AsNoTracking().Where(e => e.FactoryId == t.FactoryId && e.SourceType == IotSourceTypes.Real && e.ProductionDate == date);
            q = t.Level == TargetLevels.Machine ? q.Where(e => e.MachineId == t.MachineId) : q.Where(e => e.WorkstationId == t.WorkstationId);
            if (t.ShiftId is { } sid) q = q.Where(e => e.ShiftId == sid);
            var n = await q.CountAsync(ct);
            if (n == 0) return new TargetPerformanceDto(d, null, null, ActualBases.None, "No counter message from this machine or workstation on this date.");
            actual = await q.SumAsync(e => e.Pieces, ct);
            basis = "MACHINE_COUNTER";
            note = "Pieces counted at this machine or workstation (an operation count, not finished garments).";
        }
        else
        {
            var scopeLines = t.Level switch
            {
                TargetLevels.Line => new[] { t.LineId! },
                TargetLevels.Department => factoryLines.Where(l => placements.GetValueOrDefault(l)?.DepartmentId == t.DepartmentId).ToArray(),
                TargetLevels.Floor => factoryLines.Where(l => placements.GetValueOrDefault(l)?.FloorId == t.FloorId).ToArray(),
                _ => factoryLines.ToArray(),
            };
            var withCounter = scopeLines.Where(points.ContainsKey).ToArray();
            if (withCounter.Length == 0) return new TargetPerformanceDto(d, null, null, ActualBases.None, "No line in this scope has an output counter configured, so output can't be verified.");
            var sum = 0; var evidence = false;
            foreach (var line in withCounter)
                foreach (var ((l, dt, sh), r) in readings)
                    if (l == line && dt == date && (t.ShiftId is null || sh == t.ShiftId)) { sum += r.Total; evidence |= r.HasEvidence; }
            if (!evidence) return new TargetPerformanceDto(d, null, null, ActualBases.None, "The output counters sent no data for this date.");
            actual = sum;
            basis = ActualBases.Counter;
            note = withCounter.Length < scopeLines.Length ? $"Only {withCounter.Length} of {scopeLines.Length} lines have an output counter; the rest are not included." : "Verified counter pieces at the lines' output counters.";
        }
        double? pct;
        if (t.Period == TargetPeriods.Hourly)
        {
            var productive = clock.ProductiveMinutes(date);
            var elapsed = date == clock.Today(now) ? clock.Shifts.Where(s => s.WorksOn(date.DayOfWeek)).Sum(s => new ShiftSchedule(s, date, clock.Zone).ProductiveMinutesUntil(now)) : productive;
            var rate = PlanMath.HourlyRate(actual!.Value, elapsed);
            return new TargetPerformanceDto(d, actual, rate is null ? null : PlanMath.Pct(rate.Value, t.Quantity), basis, note + $" Hourly pace over {Math.Round(elapsed / 60, 1)} productive hours: {rate?.ToString() ?? "—"} per hour.");
        }
        pct = PlanMath.Pct(actual!.Value, t.Quantity);
        return new TargetPerformanceDto(d, actual, pct, basis, note);
    }
}
