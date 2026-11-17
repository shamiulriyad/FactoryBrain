using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Planning.PlanningScope;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>
/// Allocations of production orders to lines. An order's counting allocations
/// (planned, active, completed) may not exceed its quantity unless a
/// factory-level user records an explicit, reasoned override; the check and
/// the write happen under a lock on the order, so two people allocating at
/// once can't both pass it. Supervisors reach only their own lines.
/// </summary>
public sealed class AllocationService : IAllocationService
{
    private readonly BunonBrainDbContext _db;

    public AllocationService(BunonBrainDbContext db) { _db = db; }

    // ── Reads ───────────────────────────────────────────────────────────────

    public async Task<AccessResult<PagedResult<AllocationDto>>> ListAsync(AccessContext caller, AllocationQuery q, CancellationToken ct)
    {
        if (q.Page < 1 || q.PageSize is < 1 or > 200) return AccessResult<PagedResult<AllocationDto>>.Invalid("paging_invalid", "page must be 1 or more and pageSize from 1 to 200.");
        string? status = null;
        if (q.Status is not null && (status = AllocationStatuses.Parse(q.Status)) is null)
            return AccessResult<PagedResult<AllocationDto>>.Invalid("status_invalid", $"status must be one of: {string.Join(", ", AllocationStatuses.All)}.");
        if (q.FactoryId is { } f && !caller.CanAccessFactory(f)) return AccessResult<PagedResult<AllocationDto>>.Forbidden("factory_out_of_scope", "You can only see your own factories.");
        var query = Allocations(_db, caller);
        if (q.FactoryId is { } fid) query = query.Where(a => a.FactoryId == fid);
        if (q.OrderId is { } oid) query = query.Where(a => a.OrderId == oid);
        if (Clean(q.LineId) is { } line) query = query.Where(a => a.LineId == line);
        if (status is not null) query = query.Where(a => a.Status == status);
        if (q.From is { } from) query = query.Where(a => a.PlannedEnd >= from);
        if (q.To is { } to) query = query.Where(a => a.PlannedStart <= to);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(a => a.PlannedStart).ThenBy(a => a.LineId).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        return AccessResult<PagedResult<AllocationDto>>.Ok(new PagedResult<AllocationDto>(await DtosAsync(rows, ct), q.Page, q.PageSize, total));
    }

    public async Task<AccessResult<AllocationSummaryDto>> ForOrderAsync(AccessContext caller, Guid orderId, CancellationToken ct)
    {
        var order = await PlanningScope.Orders(_db, caller).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return AccessResult<AllocationSummaryDto>.NotFound("order_not_found", "Order not found.");
        var rows = await Allocations(_db, caller).Where(a => a.OrderId == orderId).OrderBy(a => a.PlannedStart).ThenBy(a => a.LineId).ToListAsync(ct);
        var all = await _db.ProductionAllocations.AsNoTracking().Where(a => a.OrderId == orderId && a.Status != AllocationStatuses.Cancelled).SumAsync(a => (int?)a.Quantity, ct) ?? 0;
        return AccessResult<AllocationSummaryDto>.Ok(new AllocationSummaryDto(order.Id, order.OrderNumber, order.Quantity, all, Math.Max(0, order.Quantity - all),
            Math.Max(0, all - order.Quantity), await DtosAsync(rows, ct)));
    }

    public async Task<AccessResult<AllocationDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var a = await Allocations(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return AccessResult<AllocationDetailDto>.NotFound("allocation_not_found", "Allocation not found in your scope.");
        return AccessResult<AllocationDetailDto>.Ok(await DetailAsync(a, ct));
    }

    // ── Writes ──────────────────────────────────────────────────────────────

    public async Task<AccessResult<AllocationDetailDto>> CreateAsync(AccessContext caller, AllocationWrite body, string? traceId, CancellationToken ct)
    {
        if (body.OrderId is not { } orderId) return Bad("order_required", "orderId is required.");
        var order = await PlanningScope.Orders(_db, caller).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return AccessResult<AllocationDetailDto>.NotFound("order_not_found", "Order not found in your scope.");
        if (order.FactoryId is not { } factoryId) return AccessResult<AllocationDetailDto>.Conflict("order_without_factory", "Set the order's factory before allocating it to lines.");
        if (!OrderLifecycle.IsLive(order.Status))
            return AccessResult<AllocationDetailDto>.Conflict("order_not_allocatable", $"Only planned or in-progress orders can be allocated; this order is {ProductionWire.OrderStatus(order.Status)}.");
        if (Clean(body.LineId) is not { } lineId) return Bad("line_required", "lineId is required.");
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null || line.FactoryId != factoryId) return AccessResult<AllocationDetailDto>.NotFound("line_not_found", "The line doesn't exist in the order's factory.");
        if (!caller.CanAccessLine(line.Id, line.FactoryId)) return AccessResult<AllocationDetailDto>.Forbidden("line_out_of_scope", "You can only allocate to your own lines.");
        if (!line.IsActive) return AccessResult<AllocationDetailDto>.Conflict("line_inactive", "The line is deactivated.");
        if (body.Quantity is not (>= 1 and <= 10_000_000)) return Bad("quantity_invalid", "Quantity must be from 1 to 10,000,000 pieces.");
        if (!TryDate(body.PlannedStart, out var start)) return Bad("start_invalid", "plannedStart must be a date (yyyy-MM-dd).");
        if (!TryDate(body.PlannedEnd, out var end)) return Bad("end_invalid", "plannedEnd must be a date (yyyy-MM-dd).");
        if (!AllocationRules.DatesValid(start, end)) return Bad("dates_invalid", "The end date can't be before the start date.");
        var priority = body.Priority is null ? order.Priority : OrderPriorities.Parse(body.Priority);
        if (priority is null) return Bad("priority_invalid", $"priority must be one of: {string.Join(", ", OrderPriorities.All)}.");
        if (body.Notes is { Length: > 1000 }) return Bad("notes_invalid", "Notes are limited to 1000 characters.");
        var sup = await ResolveSupervisorAsync(body.SupervisorUserId, line, ct);
        if (sup.Error is not null) return Bad("supervisor_invalid", sup.Error);
        var wantsOverride = body.AllowOverAllocation == true;
        if (wantsOverride && (Clean(body.OverrideReason) is null || caller.Scope < RoleScope.Factory))
            return wantsOverride && caller.Scope < RoleScope.Factory
                ? AccessResult<AllocationDetailDto>.Forbidden("override_not_allowed", "Only factory-level users can allocate beyond an order's quantity.")
                : Bad("override_reason_required", "Give the reason for allocating beyond the order quantity.");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockAsync(_db, $"order:{orderId}", ct);
            var others = await _db.ProductionAllocations.AsNoTracking().Where(a => a.OrderId == orderId && a.Status != AllocationStatuses.Cancelled).ToListAsync(ct);
            var check = AllocationRules.CheckQuantity(order.Quantity, others.Sum(a => a.Quantity), body.Quantity!.Value);
            if (check.Exceeds && !wantsOverride)
                return AccessResult<AllocationDetailDto>.Conflict("allocation_exceeds_order",
                    $"Allocations would total {check.Total} pieces for an order of {order.Quantity} ({check.Excess} over). Reduce the quantity, or ask a factory manager to allocate beyond the order quantity with a reason.");
            if (others.Any(a => a.LineId == lineId && AllocationRules.Overlaps(a.PlannedStart, a.PlannedEnd, start, end)))
                return AccessResult<AllocationDetailDto>.Conflict("allocation_overlaps",
                    "This order already has an allocation on this line in these dates. Change that allocation instead.");

            var now = DateTimeOffset.UtcNow;
            var a = new ProductionAllocation
            {
                OrderId = orderId, FactoryId = factoryId, LineId = lineId, Quantity = body.Quantity.Value, PlannedStart = start, PlannedEnd = end, Priority = priority,
                Status = AllocationStatuses.Planned, SupervisorUserId = sup.UserId, SupervisorEmail = sup.Email, Notes = Clean(body.Notes), CreatedAt = now, UpdatedAt = now,
                CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email,
                OverAllocated = check.Exceeds, OverrideReason = check.Exceeds ? Clean(body.OverrideReason) : null, OverrideByEmail = check.Exceeds ? caller.Email : null,
            };
            _db.ProductionAllocations.Add(a);
            _db.ProductionAllocationChanges.Add(Change(a, "created", null, Snapshot(a), Clean(body.Reason) ?? "Allocated", caller));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.allocation_created", "production_allocation", a.Id.ToString(), null, a.Status,
                $"{order.OrderNumber} → {line.Name}: {a.Quantity} pcs, {Date(start)}..{Date(end)}{(check.Exceeds ? $" (override: {a.OverrideReason})" : "")}", traceId, factoryId));
            try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { await tx.RollbackAsync(ct); return AccessResult<AllocationDetailDto>.Conflict("allocation_overlaps", "A conflicting allocation was just saved."); }
            return AccessResult<AllocationDetailDto>.Created(await DetailAsync(a, ct));
        });
    }

    public async Task<AccessResult<AllocationDetailDto>> UpdateAsync(AccessContext caller, Guid id, AllocationWrite body, string? traceId, CancellationToken ct)
    {
        var a = await Allocations(_db, caller, tracking: true).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return AccessResult<AllocationDetailDto>.NotFound("allocation_not_found", "Allocation not found in your scope.");
        var line = await _db.ProductionLines.AsNoTracking().FirstAsync(l => l.Id == a.LineId, ct);
        if (!caller.CanAccessLine(a.LineId, a.FactoryId)) return AccessResult<AllocationDetailDto>.Forbidden("line_out_of_scope", "You can only change allocations on your own lines.");
        if (body.ExpectedVersion is null) return Bad("version_required", "expectedVersion is required.");
        if (Clean(body.Reason) is null) return Bad("reason_required", "Give the reason for the change.");
        if (!AllocationStatuses.IsOpen(a.Status)) return AccessResult<AllocationDetailDto>.Conflict("allocation_closed", $"A {a.Status.ToLowerInvariant()} allocation can't be changed.");
        if (body.OrderId is { } o && o != a.OrderId || (Clean(body.LineId) is { } l && l != a.LineId))
            return Bad("order_line_fixed", "An allocation's order and line can't change: cancel it and allocate again.");
        var start = a.PlannedStart; var end = a.PlannedEnd;
        if (body.PlannedStart is not null && !TryDate(body.PlannedStart, out start)) return Bad("start_invalid", "plannedStart must be a date (yyyy-MM-dd).");
        if (body.PlannedEnd is not null && !TryDate(body.PlannedEnd, out end)) return Bad("end_invalid", "plannedEnd must be a date (yyyy-MM-dd).");
        if (!AllocationRules.DatesValid(start, end)) return Bad("dates_invalid", "The end date can't be before the start date.");
        if (body.Quantity is { } q && q is not (>= 1 and <= 10_000_000)) return Bad("quantity_invalid", "Quantity must be from 1 to 10,000,000 pieces.");
        string? priority = null;
        if (body.Priority is not null && (priority = OrderPriorities.Parse(body.Priority)) is null) return Bad("priority_invalid", $"priority must be one of: {string.Join(", ", OrderPriorities.All)}.");
        if (body.Notes is { Length: > 1000 }) return Bad("notes_invalid", "Notes are limited to 1000 characters.");
        var wantsOverride = body.AllowOverAllocation == true;
        if (wantsOverride && caller.Scope < RoleScope.Factory)
            return AccessResult<AllocationDetailDto>.Forbidden("override_not_allowed", "Only factory-level users can allocate beyond an order's quantity.");
        var sup = (UserId: a.SupervisorUserId, Email: a.SupervisorEmail, Error: (string?)null);
        if (body.SupervisorUserId is { } su && su != a.SupervisorUserId)
        {
            sup = await ResolveSupervisorAsync(su, line, ct);
            if (sup.Error is not null) return Bad("supervisor_invalid", sup.Error);
        }
        var order = await _db.ProductionOrders.AsNoTracking().FirstAsync(x => x.Id == a.OrderId, ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockAsync(_db, $"order:{a.OrderId}", ct);
            await _db.Entry(a).ReloadAsync(ct);
            if (body.ExpectedVersion != a.Version) { await tx.RollbackAsync(ct); return AccessResult<AllocationDetailDto>.Conflict("version_conflict", "The allocation changed since you loaded it. Reload and try again."); }
            var qty = body.Quantity ?? a.Quantity;
            var others = await _db.ProductionAllocations.AsNoTracking().Where(x => x.OrderId == a.OrderId && x.Id != a.Id && x.Status != AllocationStatuses.Cancelled).ToListAsync(ct);
            var check = AllocationRules.CheckQuantity(order.Quantity, others.Sum(x => x.Quantity), qty);
            if (check.Exceeds && !wantsOverride && !(a.OverAllocated && qty <= a.Quantity))
                return AccessResult<AllocationDetailDto>.Conflict("allocation_exceeds_order",
                    $"Allocations would total {check.Total} pieces for an order of {order.Quantity} ({check.Excess} over). Reduce the quantity, or ask a factory manager to allocate beyond the order quantity with a reason.");
            if (check.Exceeds && wantsOverride && Clean(body.OverrideReason) is null && !a.OverAllocated)
                return Bad("override_reason_required", "Give the reason for allocating beyond the order quantity.");
            if (others.Any(x => x.LineId == a.LineId && AllocationRules.Overlaps(x.PlannedStart, x.PlannedEnd, start, end)))
                return AccessResult<AllocationDetailDto>.Conflict("allocation_overlaps", "This order already has another allocation on this line in these dates.");

            var before = Snapshot(a);
            a.Quantity = qty; a.PlannedStart = start; a.PlannedEnd = end;
            if (priority is not null) a.Priority = priority;
            if (body.Notes is not null) a.Notes = Clean(body.Notes);
            a.SupervisorUserId = sup.UserId; a.SupervisorEmail = sup.Email;
            a.OverAllocated = check.Exceeds;
            if (check.Exceeds) { a.OverrideReason = Clean(body.OverrideReason) ?? a.OverrideReason; a.OverrideByEmail = wantsOverride ? caller.Email : a.OverrideByEmail ?? caller.Email; }
            else { a.OverrideReason = null; a.OverrideByEmail = null; }
            a.UpdatedAt = DateTimeOffset.UtcNow;
            _db.ProductionAllocationChanges.Add(Change(a, "updated", before, Snapshot(a), Clean(body.Reason), caller));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.allocation_updated", "production_allocation", a.Id.ToString(), null, a.Status,
                $"before: {before} | after: {Snapshot(a)} | {Clean(body.Reason)}", traceId, a.FactoryId));
            try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { await tx.RollbackAsync(ct); return AccessResult<AllocationDetailDto>.Conflict("version_conflict", "The allocation changed since you loaded it. Reload and try again."); }
            return AccessResult<AllocationDetailDto>.Ok(await DetailAsync(a, ct));
        });
    }

    public async Task<AccessResult<AllocationDetailDto>> SetStatusAsync(AccessContext caller, Guid id, AllocationStatusRequest body, string? traceId, CancellationToken ct)
    {
        var a = await Allocations(_db, caller, tracking: true).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return AccessResult<AllocationDetailDto>.NotFound("allocation_not_found", "Allocation not found in your scope.");
        if (!caller.CanAccessLine(a.LineId, a.FactoryId)) return AccessResult<AllocationDetailDto>.Forbidden("line_out_of_scope", "You can only change allocations on your own lines.");
        var to = AllocationStatuses.Parse(body.Status);
        if (to is null) return Bad("status_invalid", $"status must be one of: {string.Join(", ", AllocationStatuses.All)}.");
        if (body.ExpectedVersion is null) return Bad("version_required", "expectedVersion is required.");
        if (body.ExpectedVersion != a.Version) return AccessResult<AllocationDetailDto>.Conflict("version_conflict", "The allocation changed since you loaded it. Reload and try again.");
        if (!AllocationStatuses.CanMove(a.Status, to))
            return AccessResult<AllocationDetailDto>.Conflict("invalid_status_transition", $"An allocation can't go from {a.Status.ToLowerInvariant()} to {to.ToLowerInvariant()}.");
        if (to == AllocationStatuses.Cancelled && Clean(body.Reason) is null) return Bad("reason_required", "Give the reason for cancelling the allocation.");
        if (to == AllocationStatuses.Active)
        {
            var order = await _db.ProductionOrders.AsNoTracking().FirstAsync(x => x.Id == a.OrderId, ct);
            if (!OrderLifecycle.IsLive(order.Status)) return AccessResult<AllocationDetailDto>.Conflict("order_not_allocatable", "The order isn't planned or in progress.");
        }
        var before = a.Status;
        a.Status = to;
        a.UpdatedAt = DateTimeOffset.UtcNow;
        _db.ProductionAllocationChanges.Add(Change(a, "status", before, to, Clean(body.Reason), caller));
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.allocation_status_changed", "production_allocation", a.Id.ToString(), before, to, Clean(body.Reason) ?? "Status changed", traceId, a.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<AllocationDetailDto>.Conflict("version_conflict", "The allocation changed since you loaded it. Reload and try again."); }
        return AccessResult<AllocationDetailDto>.Ok(await DetailAsync(a, ct));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AccessResult<AllocationDetailDto> Bad(string code, string message) => AccessResult<AllocationDetailDto>.Invalid(code, message);

    /// <summary>The responsible supervisor: an active user of the line's factory who reaches the line.</summary>
    private async Task<(Guid? UserId, string? Email, string? Error)> ResolveSupervisorAsync(Guid? userId, ProductionLine line, CancellationToken ct)
    {
        if (userId is not { } id) return (null, null, null);
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
        if (user is null) return (null, null, "The supervisor isn't an active user.");
        if (line.FactoryId is { } f && !await _db.UserFactories.AnyAsync(x => x.UserId == id && x.FactoryId == f, ct))
            return (null, null, "The supervisor doesn't belong to this line's factory.");
        var roles = await _db.UserRoles.AsNoTracking().Where(r => r.UserId == id).Select(r => r.RoleId).ToListAsync(ct);
        var scope = AccessCatalog.ScopeFor(roles);
        if (scope < RoleScope.Factory && !await _db.UserLines.AnyAsync(x => x.UserId == id && x.LineId == line.Id, ct))
            return (null, null, "The supervisor isn't assigned to this line.");
        return (user.Id, user.Email, null);
    }

    private static string Snapshot(ProductionAllocation a) =>
        $"{{\"quantity\":{a.Quantity},\"start\":\"{Date(a.PlannedStart)}\",\"end\":\"{Date(a.PlannedEnd)}\",\"priority\":\"{a.Priority}\",\"status\":\"{a.Status}\",\"supervisor\":\"{a.SupervisorEmail}\"}}";

    private static ProductionAllocationChange Change(ProductionAllocation a, string kind, string? previous, string next, string? reason, AccessContext by) => new()
    {
        AllocationId = a.Id, OrderId = a.OrderId, Kind = kind, PreviousValues = previous, NewValues = next, Reason = reason, ChangedByUserId = by.UserId, ChangedByEmail = by.Email,
    };

    private async Task<AllocationDetailDto> DetailAsync(ProductionAllocation a, CancellationToken ct)
    {
        var history = await _db.ProductionAllocationChanges.AsNoTracking().Where(c => c.AllocationId == a.Id).OrderBy(c => c.ChangedAt).ToListAsync(ct);
        var order = await _db.ProductionOrders.AsNoTracking().FirstAsync(o => o.Id == a.OrderId, ct);
        var warnings = new List<string>();
        if (a.OverAllocated) warnings.Add($"This allocation was saved beyond the order quantity by {a.OverrideByEmail}: {a.OverrideReason}");
        if (a.PlannedEnd > order.DueDate) warnings.Add($"The allocation ends after the delivery deadline ({Date(order.DueDate)}).");
        if (order.StartDate is { } s && a.PlannedStart < s) warnings.Add($"The allocation starts before the order's start date ({Date(s)}).");
        return new AllocationDetailDto((await DtosAsync(new[] { a }, ct))[0],
            history.Select(h => new AllocationChangeDto(h.Id, h.Kind, h.PreviousValues, h.NewValues, h.Reason, h.ChangedAt, h.ChangedByEmail)).ToList(), warnings);
    }

    internal async Task<IReadOnlyList<AllocationDto>> DtosAsync(IReadOnlyList<ProductionAllocation> rows, CancellationToken ct) => await MapAsync(_db, rows, ct);

    internal static async Task<IReadOnlyList<AllocationDto>> MapAsync(BunonBrainDbContext db, IReadOnlyList<ProductionAllocation> rows, CancellationToken ct)
    {
        var orderIds = rows.Select(r => r.OrderId).Distinct().ToList();
        var lineIds = rows.Select(r => r.LineId).Distinct().ToList();
        var orders = await db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
        var lines = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return rows.Select(a => new AllocationDto(a.Id, a.OrderId, orders.GetValueOrDefault(a.OrderId, "?"), a.FactoryId, a.LineId, lines.GetValueOrDefault(a.LineId, a.LineId), a.Quantity,
            Date(a.PlannedStart), Date(a.PlannedEnd), a.Priority, a.Status, a.SupervisorUserId, a.SupervisorEmail, a.OverAllocated, a.OverrideReason, a.OverrideByEmail, a.Notes,
            a.CreatedAt, a.CreatedByEmail, a.Version)).ToList();
    }
}
