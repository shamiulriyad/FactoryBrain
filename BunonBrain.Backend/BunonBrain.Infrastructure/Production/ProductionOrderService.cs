using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Dtos.Production;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Planning;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Production.ProductionSupport;

namespace BunonBrain.Infrastructure.Production;

/// <summary>
/// Production orders: a quantity of one style, for one buyer, produced in one
/// factory and due by a date. They follow a lifecycle (draft → planned → in
/// progress ⇄ on hold → completed, or cancelled) whose every change is kept
/// with who, when and why. Progress, WIP and delay risk are computed from the
/// persisted output on every read.
/// </summary>
public sealed class ProductionOrderService : IProductionOrderService
{
    public const string EntityType = "production_order";

    private readonly BunonBrainDbContext _db;
    private readonly ProductionReadModel _read;

    public ProductionOrderService(BunonBrainDbContext db, ProductionOptions options)
    {
        _db = db; _read = new ProductionReadModel(db, options);
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    public async Task<ProductionResult<Paged<OrderListItem>>> ListAsync(OrderQuery q, AccessContext caller, CancellationToken ct)
    {
        var errors = Errors();
        ProductionOrderStatus[]? statuses = null;
        if (q.Status is not null)
        {
            var key = q.Status.Trim().ToLowerInvariant();
            if (key == "open") statuses = new[] { ProductionOrderStatus.Planned, ProductionOrderStatus.InProgress };
            else if (key == "closed") statuses = new[] { ProductionOrderStatus.Completed };
            else if (ProductionWire.TryParseOrderStatus(key, out var s)) statuses = new[] { s };
            else errors.Add("status", $"status must be one of: {string.Join(", ", ProductionWire.OrderStatuses)} (or open / closed).");
        }
        string? priority = null;
        if (q.Priority is not null)
        {
            priority = OrderPriorities.Parse(q.Priority);
            if (priority is null) errors.Add("priority", $"priority must be one of: {string.Join(", ", OrderPriorities.All)}.");
        }
        if (q.FactoryId is { } ff && !caller.CanAccessFactory(ff)) return ProductionResult<Paged<OrderListItem>>.Forbidden("factory_out_of_scope", "You can only see orders of your own factories.");
        if (q.Page < 1) errors.Add("page", "page must be 1 or more.");
        if (q.PageSize is < 1 or > 100) errors.Add("pageSize", "pageSize must be from 1 to 100.");
        if (q.Q is { Length: > 100 }) errors.Add("q", "Search text is limited to 100 characters.");
        if (q.Sort is not null and not ("due" or "newest" or "priority")) errors.Add("sort", "sort must be due, newest or priority.");
        if (errors.Count > 0) return ProductionResult<Paged<OrderListItem>>.Invalid(errors);

        var query = PlanningScope.Orders(_db, caller);
        if (statuses is not null) query = query.Where(o => statuses.Contains(o.Status));
        if (q.StyleId is { } sid) query = query.Where(o => o.StyleId == sid);
        if (q.FactoryId is { } fid) query = query.Where(o => o.FactoryId == fid);
        if (priority is not null) query = query.Where(o => o.Priority == priority);
        if (Clean(q.Q) is { } text)
        {
            var pattern = "%" + EscapeLike(text) + "%";
            query = query.Where(o => EF.Functions.ILike(o.OrderNumber, pattern, "\\") || (o.Buyer != null && EF.Functions.ILike(o.Buyer, pattern, "\\"))
                                     || (o.OrderReference != null && EF.Functions.ILike(o.OrderReference, pattern, "\\")));
        }
        var total = await query.CountAsync(ct);
        var ordered = q.Sort switch
        {
            "newest" => query.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.OrderNumber),
            "priority" => query.OrderByDescending(o => o.Priority == "URGENT" ? 3 : o.Priority == "HIGH" ? 2 : o.Priority == "NORMAL" ? 1 : 0).ThenBy(o => o.DueDate).ThenBy(o => o.OrderNumber),
            _ => query.OrderBy(o => o.DueDate).ThenBy(o => o.OrderNumber),
        };
        var rows = await ordered.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);

        var ids = rows.Select(o => o.Id).ToList();
        var styleIds = rows.Select(o => o.StyleId).Distinct().ToList();
        var styles = await _db.ProductionStyles.AsNoTracking().Where(s => styleIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var produced = await _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => ids.Contains(e.OrderId) && !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput)
            .GroupBy(e => e.OrderId).Select(g => new { g.Key, Good = g.Sum(e => e.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Good, ct);
        var activePlans = await _db.ProductionPlans.AsNoTracking()
            .Where(p => ids.Contains(p.OrderId) && p.Status == ProductionPlanStatus.Active)
            .GroupBy(p => p.OrderId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var now = DateTime.UtcNow;
        var items = new List<OrderListItem>();
        foreach (var o in rows)
        {
            var good = produced.GetValueOrDefault(o.Id);
            string level;
            DateTime? projected = null;
            if (!OrderLifecycle.IsLive(o.Status) || !q.IncludeRisk) level = "not_applicable";
            else
            {
                var risk = await _read.RiskAsync(o, now, ct, withRecommendations: false);
                level = risk.Level;
                projected = risk.ProjectedCompletion;
            }
            var style = styles.GetValueOrDefault(o.StyleId);
            items.Add(new OrderListItem(o.Id, o.OrderNumber, o.StyleId, style?.Code ?? "?", style?.Name ?? "?", o.Buyer, o.Quantity,
                ProductionWire.Date(o.DueDate), ProductionWire.OrderStatus(o.Status), good, ProductionMetrics.Round1(good * 100.0 / o.Quantity),
                activePlans.GetValueOrDefault(o.Id), level, projected, o.Version,
                o.FactoryId, o.OrderReference, o.ProductCategory, o.Priority, o.StartDate is { } sd ? ProductionWire.Date(sd) : null,
                o.PlannedCompletionDate is { } pc ? ProductionWire.Date(pc) : null));
        }
        return ProductionResult<Paged<OrderListItem>>.Ok(new Paged<OrderListItem>(items, q.Page, q.PageSize, total, (int)Math.Ceiling(total / (double)q.PageSize)));
    }

    public async Task<OrderDetail?> GetAsync(Guid id, AccessContext caller, CancellationToken ct)
    {
        var o = await PlanningScope.Orders(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;
        var style = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).FirstAsync(s => s.Id == o.StyleId, ct);
        var plans = await _db.ProductionPlans.AsNoTracking().Where(p => p.OrderId == id).OrderBy(p => p.StartDate).ToListAsync(ct);
        var counts = await _db.ProductionOutputEvents.AsNoTracking().Where(e => e.OrderId == id && !e.IsSuperseded)
            .GroupBy(e => e.Stage)
            .Select(g => new { g.Key, Good = g.Sum(e => e.Quantity), Reject = g.Sum(e => e.RejectQuantity), Rework = g.Sum(e => e.ReworkQuantity), Records = g.Count() })
            .ToListAsync(ct);
        var stageCounts = counts.Select(c => new StageCount(c.Key, c.Good, c.Reject, c.Rework, c.Records)).ToList();
        var byLine = await _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => e.OrderId == id && !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput)
            .GroupBy(e => e.LineId)
            .Select(g => new { g.Key, Good = g.Sum(e => e.Quantity), Rework = g.Sum(e => e.ReworkQuantity), Reject = g.Sum(e => e.RejectQuantity), Last = g.Max(e => e.IntervalEndUtc) })
            .ToListAsync(ct);
        var cal = await _read.CalendarAsync(ct);
        var risk = await _read.RiskAsync(o, DateTime.UtcNow, ct);
        var factoryCode = o.FactoryId is { } f ? await _db.Factories.AsNoTracking().Where(x => x.Id == f).Select(x => x.Code).FirstOrDefaultAsync(ct) : null;
        return new OrderDetail(ToDto(o, _read.DueUtc(cal, o.DueDate), factoryCode), ProductionSupport.ToDto(style), await ProductionPlanService.ListItemsAsync(_db, plans, ct),
            Enum.GetValues<ProductionStage>().Select(s =>
            {
                var c = stageCounts.FirstOrDefault(x => x.Stage == s);
                return new StageProgressDto(ProductionWire.Stage(s), c?.Good ?? 0, c?.Reject ?? 0, c?.Rework ?? 0, c?.Records ?? 0);
            }).ToList(),
            WipCalculator.Calculate(stageCounts).Select(ProductionSupport.ToDto).ToList(),
            byLine.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new LineProgressDto(l.Key, l.Good, l.Rework, l.Reject, l.Last)).ToList(),
            risk);
    }

    public async Task<ProductionResult<OrderTransitionsDto>> TransitionsAsync(Guid id, AccessContext caller, CancellationToken ct)
    {
        var o = await PlanningScope.Orders(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return ProductionResult<OrderTransitionsDto>.NotFound("order_not_found");
        var history = await _db.ProductionOrderStatusChanges.AsNoTracking().Where(h => h.OrderId == id).OrderBy(h => h.ChangedAt).ToListAsync(ct);
        return ProductionResult<OrderTransitionsDto>.Ok(new OrderTransitionsDto(ProductionWire.OrderStatus(o.Status),
            OrderLifecycle.AllowedFrom(o.Status).Select(ProductionWire.OrderStatus).ToList(),
            history.Select(h => new OrderStatusChangeDto(h.Id, h.FromStatus, h.ToStatus, h.Reason, h.ChangedAt, h.ChangedByEmail)).ToList()));
    }

    // ── Writes ──────────────────────────────────────────────────────────────

    public async Task<ProductionResult<OrderDto>> CreateAsync(OrderWrite body, AccessContext caller, string? traceId, CancellationToken ct)
    {
        var actor = caller.Actor;
        var errors = Errors();
        if (caller.Scope < RoleScope.Factory) return ProductionResult<OrderDto>.Forbidden("order_scope", "Creating orders needs factory-level access.");
        var number = Clean(body.OrderNumber);
        if (number is null || number.Length > 64) errors.Add("orderNumber", "Order number is required (at most 64 characters).");
        if (body.StyleId is null) errors.Add("styleId", "styleId is required.");
        else if (!await _db.ProductionStyles.AnyAsync(s => s.Id == body.StyleId && s.IsActive, ct)) errors.Add("styleId", "The style does not exist or is inactive.");
        if (body.Quantity is not (>= 1 and <= 10_000_000)) errors.Add("quantity", "Quantity must be from 1 to 10,000,000 pieces.");
        if (!ProductionWire.TryParseDate(body.DueDate, out var due)) errors.Add("dueDate", "The delivery deadline (dueDate) must be a date (yyyy-MM-dd).");
        DateOnly? start = null, planned = null;
        ParseOptionalDate(errors, "startDate", body.StartDate, ref start);
        ParseOptionalDate(errors, "plannedCompletionDate", body.PlannedCompletionDate, ref planned);
        ValidateText(errors, body);
        var priority = OrderPriorities.Normal;
        if (body.Priority is not null)
        {
            if (OrderPriorities.Parse(body.Priority) is { } p) priority = p; else errors.Add("priority", $"priority must be one of: {string.Join(", ", OrderPriorities.All)}.");
        }
        var (factoryId, factoryError) = await ResolveFactoryAsync(body.FactoryId, caller, ct);
        if (factoryError is not null) errors.Add("factoryId", factoryError);
        ValidateDates(errors, start, planned, due);

        ProductionOrderStatus status;
        if (body.Status is null)
        {
            status = OrderLifecycle.MissingForPlanned(body.Buyer, factoryId).Count == 0 ? ProductionOrderStatus.Planned : ProductionOrderStatus.Draft;
        }
        else if (!ProductionWire.TryParseOrderStatus(body.Status, out status)) errors.Add("status", $"status must be one of: {string.Join(", ", ProductionWire.OrderStatuses)}.");
        else if (status is not (ProductionOrderStatus.Draft or ProductionOrderStatus.Planned))
        {
            // Legacy clients created orders as "open"; an order starts as a draft or planned and moves on through the lifecycle.
            if (body.Status.Trim().Equals("open", StringComparison.OrdinalIgnoreCase)) status = ProductionOrderStatus.Planned;
            else errors.Add("status", "A new order is a draft or planned; move it on afterwards.");
        }
        if (status != ProductionOrderStatus.Draft)
            foreach (var m in OrderLifecycle.MissingForPlanned(body.Buyer, factoryId)) errors.Add(m == "buyer" ? "buyer" : m, $"{m} is required before an order can be planned (keep it a draft until it is known).");
        if (errors.Count > 0) return ProductionResult<OrderDto>.Invalid(errors);
        if (await _db.ProductionOrders.AnyAsync(o => o.OrderNumber == number, ct))
            return ProductionResult<OrderDto>.Conflict("duplicate_order", $"Order {number} already exists.");

        var now = DateTime.UtcNow;
        var order = new ProductionOrder
        {
            OrderNumber = number!, StyleId = body.StyleId!.Value, Buyer = Clean(body.Buyer), Quantity = body.Quantity!.Value, DueDate = due,
            Notes = Clean(body.Notes), CreatedAt = now, UpdatedAt = now, CreatedByUserId = actor.UserId, CreatedByEmail = actor.Email,
            Status = status, FactoryId = factoryId, OrderReference = Clean(body.OrderReference), ProductDescription = Clean(body.ProductDescription),
            ProductCategory = Clean(body.ProductCategory), StartDate = start, PlannedCompletionDate = planned, Priority = priority,
            StatusReason = Clean(body.StatusReason),
        };
        _db.ProductionOrders.Add(order);
        _db.ProductionOrderStatusChanges.Add(new ProductionOrderStatusChange
        {
            OrderId = order.Id, FromStatus = null, ToStatus = ProductionWire.OrderStatus(status), Reason = order.StatusReason ?? "Order created",
            ChangedByUserId = actor.UserId, ChangedByEmail = actor.Email,
        });
        _db.AuditLog.Add(AccessAudit.Entry(actor, "production_order.created", EntityType, order.Id.ToString(), null, ProductionWire.OrderStatus(status),
            $"{order.OrderNumber} · {order.Quantity} pcs · due {ProductionWire.Date(due)}", traceId, factoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return ProductionResult<OrderDto>.Conflict("duplicate_order", $"Order {number} already exists."); }
        return ProductionResult<OrderDto>.Created(await ToDtoAsync(order, ct));
    }

    public async Task<ProductionResult<OrderDto>> UpdateAsync(Guid id, OrderWrite body, AccessContext caller, string? traceId, CancellationToken ct)
    {
        var actor = caller.Actor;
        var order = await PlanningScope.Orders(_db, caller, tracking: true).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return ProductionResult<OrderDto>.NotFound("order_not_found");
        if (caller.Scope < RoleScope.Factory) return ProductionResult<OrderDto>.Forbidden("order_scope", "Editing orders needs factory-level access.");
        var errors = Errors();
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        if (body.OrderNumber is not null && Clean(body.OrderNumber) != order.OrderNumber) errors.Add("orderNumber", "The order number can't be changed.");
        if (body.Quantity is { } qty && qty is not (>= 1 and <= 10_000_000)) errors.Add("quantity", "Quantity must be from 1 to 10,000,000 pieces.");
        DateOnly due = order.DueDate;
        if (body.DueDate is not null)
        {
            if (ProductionWire.TryParseDate(body.DueDate, out var d)) due = d; else errors.Add("dueDate", "The delivery deadline (dueDate) must be a date (yyyy-MM-dd).");
        }
        DateOnly? start = order.StartDate, planned = order.PlannedCompletionDate;
        if (body.StartDate is not null) { start = null; ParseOptionalDate(errors, "startDate", body.StartDate, ref start); }
        if (body.PlannedCompletionDate is not null) { planned = null; ParseOptionalDate(errors, "plannedCompletionDate", body.PlannedCompletionDate, ref planned); }
        string? priority = null;
        if (body.Priority is not null)
        {
            priority = OrderPriorities.Parse(body.Priority);
            if (priority is null) errors.Add("priority", $"priority must be one of: {string.Join(", ", OrderPriorities.All)}.");
        }
        ProductionOrderStatus? status = null;
        var legacyOpen = false;
        if (body.Status is not null)
        {
            if (ProductionWire.TryParseOrderStatus(body.Status, out var s)) { status = s; legacyOpen = body.Status.Trim().Equals("open", StringComparison.OrdinalIgnoreCase); }
            else errors.Add("status", $"status must be one of: {string.Join(", ", ProductionWire.OrderStatuses)}.");
        }
        ValidateText(errors, body);
        if (body.StyleId is { } sid && sid != order.StyleId)
        {
            if (await _db.ProductionOutputEvents.AnyAsync(e => e.OrderId == id, ct)) errors.Add("styleId", "Output is already recorded for this order, so its style can't change.");
            else if (!await _db.ProductionStyles.AnyAsync(s => s.Id == sid && s.IsActive, ct)) errors.Add("styleId", "The style does not exist or is inactive.");
        }
        Guid? newFactory = null;
        if (body.FactoryId is { } fid && fid != order.FactoryId)
        {
            var (f, fe) = await ResolveFactoryAsync(fid, caller, ct);
            if (fe is not null) errors.Add("factoryId", fe);
            else if (await _db.ProductionAllocations.AnyAsync(a => a.OrderId == id, ct)) errors.Add("factoryId", "The order has allocations, so its factory can't change.");
            else newFactory = f;
        }
        ValidateDates(errors, start, planned, due);
        if (body.Quantity is { } newQty && newQty < order.Quantity)
        {
            var allocated = await _db.ProductionAllocations.Where(a => a.OrderId == id && a.Status != AllocationStatuses.Cancelled).SumAsync(a => (int?)a.Quantity, ct) ?? 0;
            if (allocated > newQty) errors.Add("quantity", $"The order's allocations total {allocated} pieces; reduce them first or keep the quantity at {allocated} or more.");
        }
        if (errors.Count > 0) return ProductionResult<OrderDto>.Invalid(errors);
        if (body.ExpectedVersion != order.Version) return ProductionResult<OrderDto>.Conflict("version_conflict", "The order changed since you loaded it. Reload and try again.");
        if (OrderLifecycle.IsFinal(order.Status)) return ProductionResult<OrderDto>.Conflict("order_closed", $"A {ProductionWire.OrderStatus(order.Status)} order can't be changed.");

        var before = Describe(order);
        if (body.Quantity is { } q) order.Quantity = q;
        order.DueDate = due;
        order.StartDate = start;
        order.PlannedCompletionDate = planned;
        if (body.Buyer is not null) order.Buyer = Clean(body.Buyer);
        if (body.Notes is not null) order.Notes = Clean(body.Notes);
        if (body.StyleId is { } style) order.StyleId = style;
        if (priority is not null) order.Priority = priority;
        if (newFactory is { } nf) order.FactoryId = nf;
        if (body.OrderReference is not null) order.OrderReference = Clean(body.OrderReference);
        if (body.ProductDescription is not null) order.ProductDescription = Clean(body.ProductDescription);
        if (body.ProductCategory is not null) order.ProductCategory = Clean(body.ProductCategory);
        order.UpdatedAt = DateTime.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(actor, "production_order.updated", EntityType, order.Id.ToString(), null, null,
            $"before: {before} | after: {Describe(order)}", traceId, order.FactoryId));

        // A status in an ordinary edit goes through the same lifecycle rules (legacy "open" means: keep it live).
        if (status is { } st && st != order.Status && !(legacyOpen && OrderLifecycle.IsLive(order.Status)))
        {
            var moved = await ApplyStatusAsync(order, st, Clean(body.StatusReason), actor, traceId, ct, autoStart: st == ProductionOrderStatus.Completed);
            if (moved is not null) return moved;
        }
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ProductionResult<OrderDto>.Conflict("version_conflict", "The order changed since you loaded it. Reload and try again."); }
        return ProductionResult<OrderDto>.Ok(await ToDtoAsync(order, ct));
    }

    public async Task<ProductionResult<OrderDto>> ChangeStatusAsync(Guid id, OrderStatusRequest body, AccessContext caller, string? traceId, CancellationToken ct)
    {
        var order = await PlanningScope.Orders(_db, caller, tracking: true).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return ProductionResult<OrderDto>.NotFound("order_not_found");
        if (caller.Scope < RoleScope.Factory) return ProductionResult<OrderDto>.Forbidden("order_scope", "Changing an order's status needs factory-level access.");
        if (body.ExpectedVersion is null) return ProductionResult<OrderDto>.Invalid("expectedVersion", "expectedVersion is required.");
        if (!ProductionWire.TryParseOrderStatus(body.Status, out var to))
            return ProductionResult<OrderDto>.Invalid("status", $"status must be one of: {string.Join(", ", ProductionWire.OrderStatuses)}.");
        if (body.ExpectedVersion != order.Version) return ProductionResult<OrderDto>.Conflict("version_conflict", "The order changed since you loaded it. Reload and try again.");
        var moved = await ApplyStatusAsync(order, to, Clean(body.Reason), caller.Actor, traceId, ct, autoStart: false);
        if (moved is not null) return moved;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ProductionResult<OrderDto>.Conflict("version_conflict", "The order changed since you loaded it. Reload and try again."); }
        return ProductionResult<OrderDto>.Ok(await ToDtoAsync(order, ct));
    }

    /// <summary>
    /// Validates and records a status change on the tracked order (history row, audit row, allocation side effects).
    /// Returns an error result, or null when the change was applied (the caller saves).
    /// </summary>
    private async Task<ProductionResult<OrderDto>?> ApplyStatusAsync(ProductionOrder order, ProductionOrderStatus to, string? reason, AuditActor actor,
        string? traceId, CancellationToken ct, bool autoStart)
    {
        var from = order.Status;
        if (from == to) return null;
        var steps = new List<ProductionOrderStatus>();
        // A legacy "close" of a planned order: it was in production, so it passes through in progress.
        if (autoStart && from == ProductionOrderStatus.Planned && to == ProductionOrderStatus.Completed) steps.Add(ProductionOrderStatus.InProgress);
        steps.Add(to);
        var cursor = from;
        foreach (var step in steps)
        {
            if (!OrderLifecycle.CanMove(cursor, step))
                return ProductionResult<OrderDto>.Conflict("invalid_status_transition",
                    $"An order can't go from {ProductionWire.OrderStatus(cursor)} to {ProductionWire.OrderStatus(step)}. Allowed: {(OrderLifecycle.AllowedFrom(cursor).Count == 0 ? "none (final)" : string.Join(", ", OrderLifecycle.AllowedFrom(cursor).Select(ProductionWire.OrderStatus)))}.");
            cursor = step;
        }
        if (OrderLifecycle.NeedsReason(to) && reason is null)
            return ProductionResult<OrderDto>.Invalid("reason", $"A reason is required to set an order {ProductionWire.OrderStatus(to).Replace('_', ' ')}.");
        if (reason is { Length: > 500 }) return ProductionResult<OrderDto>.Invalid("reason", "The reason is limited to 500 characters.");
        if (from == ProductionOrderStatus.Draft && to == ProductionOrderStatus.Planned)
        {
            var missing = OrderLifecycle.MissingForPlanned(order.Buyer, order.FactoryId);
            if (missing.Count > 0)
                return ProductionResult<OrderDto>.Invalid(missing[0], $"These must be set before the order can be planned: {string.Join(", ", missing)}.");
        }
        if (to is ProductionOrderStatus.Cancelled or ProductionOrderStatus.Completed
            && await _db.ProductionPlans.AnyAsync(p => p.OrderId == order.Id && (p.Status == ProductionPlanStatus.Active || p.Status == ProductionPlanStatus.PendingApproval), ct))
            return ProductionResult<OrderDto>.Conflict("order_has_live_plans", "Complete or cancel the order's active and pending plans first.");

        var now = DateTimeOffset.UtcNow;
        foreach (var step in steps)
        {
            _db.ProductionOrderStatusChanges.Add(new ProductionOrderStatusChange
            {
                OrderId = order.Id, FromStatus = ProductionWire.OrderStatus(order.Status), ToStatus = ProductionWire.OrderStatus(step), Reason = reason,
                ChangedAt = now, ChangedByUserId = actor.UserId, ChangedByEmail = actor.Email,
            });
            _db.AuditLog.Add(AccessAudit.Entry(actor, "production_order.status_changed", EntityType, order.Id.ToString(), ProductionWire.OrderStatus(order.Status),
                ProductionWire.OrderStatus(step), $"{order.OrderNumber}{(reason is null ? "" : ": " + reason)}", traceId, order.FactoryId));
            order.Status = step;
        }
        order.StatusReason = reason;
        order.UpdatedAt = now.UtcDateTime;

        // The order's allocations follow it: a finished order's open allocations close, and so do the exceptions about it.
        if (to is ProductionOrderStatus.Completed or ProductionOrderStatus.Cancelled)
        {
            var target = to == ProductionOrderStatus.Completed ? AllocationStatuses.Completed : AllocationStatuses.Cancelled;
            var open = await _db.ProductionAllocations.Where(a => a.OrderId == order.Id && (a.Status == AllocationStatuses.Planned || a.Status == AllocationStatuses.Active)).ToListAsync(ct);
            foreach (var a in open)
            {
                _db.ProductionAllocationChanges.Add(new ProductionAllocationChange
                {
                    AllocationId = a.Id, OrderId = order.Id, Kind = "status", PreviousValues = a.Status, NewValues = target,
                    Reason = $"Order {ProductionWire.OrderStatus(to)}", ChangedByUserId = actor.UserId, ChangedByEmail = actor.Email,
                });
                a.Status = target;
                a.UpdatedAt = now;
            }
            var exceptions = await _db.ProductionExceptions.Where(x => x.OrderId == order.Id && x.Status != ExceptionStatuses.Resolved).ToListAsync(ct);
            foreach (var x in exceptions)
            {
                x.Status = ExceptionStatuses.Resolved; x.ResolvedAt = now; x.ResolvedByEmail = "system"; x.AutoResolved = true;
                x.ResolutionNote = $"The order was {ProductionWire.OrderStatus(to)}.";
            }
        }
        return null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The order's factory: the one asked for (it must be in reach), else the caller's only factory.</summary>
    private async Task<(Guid? Id, string? Error)> ResolveFactoryAsync(Guid? requested, AccessContext caller, CancellationToken ct)
    {
        if (requested is { } r)
        {
            if (!caller.CanAccessFactory(r)) return (null, "You can only use your own factories.");
            var ok = await _db.Factories.AnyAsync(f => f.Id == r && f.IsActive, ct);
            return ok ? (r, null) : (null, "The factory doesn't exist or is inactive.");
        }
        var reach = caller.IsPlatform
            ? await _db.Factories.AsNoTracking().Where(f => f.IsActive).Select(f => f.Id).Take(2).ToListAsync(ct)
            : caller.FactoryIds.ToList();
        if (!caller.IsPlatform)
            reach = await _db.Factories.AsNoTracking().Where(f => f.IsActive && reach.Contains(f.Id)).Select(f => f.Id).Take(2).ToListAsync(ct);
        return reach.Count == 1 ? (reach[0], null) : (null, reach.Count == 0 ? "You have no factory to produce this order in." : "Choose the factory that produces this order.");
    }

    private static void ParseOptionalDate(Dictionary<string, string[]> errors, string field, string? raw, ref DateOnly? value)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (ProductionWire.TryParseDate(raw, out var d)) value = d;
        else errors.Add(field, $"{field} must be a date (yyyy-MM-dd).");
    }

    private static void ValidateDates(Dictionary<string, string[]> errors, DateOnly? start, DateOnly? planned, DateOnly due)
    {
        if (start is { } s && planned is { } p && p < s) errors.Add("plannedCompletionDate", "The planned completion can't be before the start date.");
        if (start is { } s2 && s2 > due) errors.Add("startDate", "The start date can't be after the delivery deadline.");
    }

    private static void ValidateText(Dictionary<string, string[]> errors, OrderWrite b)
    {
        if (b.Buyer is { Length: > 120 }) errors.Add("buyer", "Buyer is limited to 120 characters.");
        if (b.Notes is { Length: > 1000 }) errors.Add("notes", "Notes are limited to 1000 characters.");
        if (b.OrderReference is { Length: > 80 }) errors.Add("orderReference", "Order reference is limited to 80 characters.");
        if (b.ProductDescription is { Length: > 500 }) errors.Add("productDescription", "Product description is limited to 500 characters.");
        if (b.ProductCategory is { Length: > 64 }) errors.Add("productCategory", "Product category is limited to 64 characters.");
        if (b.StatusReason is { Length: > 500 }) errors.Add("statusReason", "The reason is limited to 500 characters.");
    }

    private static string Describe(ProductionOrder o) =>
        $"{o.OrderNumber} · {o.Quantity} pcs · due {ProductionWire.Date(o.DueDate)} · {ProductionWire.OrderStatus(o.Status)} · {o.Priority}";

    private async Task<OrderDto> ToDtoAsync(ProductionOrder o, CancellationToken ct)
    {
        var cal = await _read.CalendarAsync(ct);
        var code = o.FactoryId is { } f ? await _db.Factories.AsNoTracking().Where(x => x.Id == f).Select(x => x.Code).FirstOrDefaultAsync(ct) : null;
        return ToDto(o, _read.DueUtc(cal, o.DueDate), code);
    }

    private static OrderDto ToDto(ProductionOrder o, DateTime dueAt, string? factoryCode) => new(o.Id, o.OrderNumber, o.StyleId, o.Buyer, o.Quantity,
        ProductionWire.Date(o.DueDate), dueAt, ProductionWire.OrderStatus(o.Status), o.Notes, o.CreatedAt, o.CreatedByEmail, o.Version,
        o.FactoryId, factoryCode, o.OrderReference, o.ProductDescription, o.ProductCategory,
        o.StartDate is { } s ? ProductionWire.Date(s) : null, o.PlannedCompletionDate is { } p ? ProductionWire.Date(p) : null, o.Priority, o.StatusReason,
        OrderLifecycle.AllowedFrom(o.Status).Select(ProductionWire.OrderStatus).ToList());
}
