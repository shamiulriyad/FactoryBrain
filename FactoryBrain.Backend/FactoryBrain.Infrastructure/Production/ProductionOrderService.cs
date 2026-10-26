using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Production;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static FactoryBrain.Infrastructure.Production.ProductionSupport;

namespace FactoryBrain.Infrastructure.Production;

/// <summary>
/// Production orders: a quantity of one style due by a date. Progress, WIP
/// and delay risk are computed from the persisted output on every read.
/// </summary>
public sealed class ProductionOrderService : IProductionOrderService
{
    public const string EntityType = "production_order";

    private readonly FactoryBrainDbContext _db;
    private readonly ProductionReadModel _read;

    public ProductionOrderService(FactoryBrainDbContext db, ProductionOptions options)
    {
        _db = db; _read = new ProductionReadModel(db, options);
    }

    public async Task<ProductionResult<Paged<OrderListItem>>> ListAsync(OrderQuery q, CancellationToken ct)
    {
        var errors = Errors();
        ProductionOrderStatus? status = null;
        if (q.Status is not null)
        {
            if (ProductionWire.TryParseOrderStatus(q.Status, out var s)) status = s;
            else errors.Add("status", $"status must be one of: {string.Join(", ", ProductionWire.OrderStatuses)}");
        }
        if (q.Page < 1) errors.Add("page", "page must be 1 or more.");
        if (q.PageSize is < 1 or > 100) errors.Add("pageSize", "pageSize must be from 1 to 100.");
        if (q.Q is { Length: > 100 }) errors.Add("q", "Search text is limited to 100 characters.");
        if (q.Sort is not null and not ("due" or "newest")) errors.Add("sort", "sort must be due or newest.");
        if (errors.Count > 0) return ProductionResult<Paged<OrderListItem>>.Invalid(errors);

        var query = _db.ProductionOrders.AsNoTracking();
        if (status is { } st) query = query.Where(o => o.Status == st);
        if (q.StyleId is { } sid) query = query.Where(o => o.StyleId == sid);
        if (Clean(q.Q) is { } text)
        {
            var pattern = "%" + EscapeLike(text) + "%";
            query = query.Where(o => EF.Functions.ILike(o.OrderNumber, pattern, "\\") || (o.Buyer != null && EF.Functions.ILike(o.Buyer, pattern, "\\")));
        }
        var total = await query.CountAsync(ct);
        var ordered = q.Sort == "newest"
            ? query.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.OrderNumber)
            : query.OrderBy(o => o.DueDate).ThenBy(o => o.OrderNumber);
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
            if (o.Status != ProductionOrderStatus.Open || !q.IncludeRisk) level = "not_applicable";
            else
            {
                var risk = await _read.RiskAsync(o, now, ct, withRecommendations: false);
                level = risk.Level;
                projected = risk.ProjectedCompletion;
            }
            var style = styles.GetValueOrDefault(o.StyleId);
            items.Add(new OrderListItem(o.Id, o.OrderNumber, o.StyleId, style?.Code ?? "?", style?.Name ?? "?", o.Buyer, o.Quantity,
                ProductionWire.Date(o.DueDate), ProductionWire.OrderStatus(o.Status), good, ProductionMetrics.Round1(good * 100.0 / o.Quantity),
                activePlans.GetValueOrDefault(o.Id), level, projected, o.Version));
        }
        return ProductionResult<Paged<OrderListItem>>.Ok(new Paged<OrderListItem>(items, q.Page, q.PageSize, total, (int)Math.Ceiling(total / (double)q.PageSize)));
    }

    public async Task<OrderDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        var o = await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
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
        return new OrderDetail(ToDto(o, _read.DueUtc(cal, o.DueDate)), ProductionSupport.ToDto(style), await ProductionPlanService.ListItemsAsync(_db, plans, ct),
            Enum.GetValues<ProductionStage>().Select(s =>
            {
                var c = stageCounts.FirstOrDefault(x => x.Stage == s);
                return new StageProgressDto(ProductionWire.Stage(s), c?.Good ?? 0, c?.Reject ?? 0, c?.Rework ?? 0, c?.Records ?? 0);
            }).ToList(),
            WipCalculator.Calculate(stageCounts).Select(ProductionSupport.ToDto).ToList(),
            byLine.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new LineProgressDto(l.Key, l.Good, l.Rework, l.Reject, l.Last)).ToList(),
            risk);
    }

    public async Task<ProductionResult<OrderDto>> CreateAsync(OrderWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        var number = Clean(body.OrderNumber);
        if (number is null || number.Length > 64) errors.Add("orderNumber", "Order number is required (at most 64 characters).");
        if (body.StyleId is null) errors.Add("styleId", "styleId is required.");
        else if (!await _db.ProductionStyles.AnyAsync(s => s.Id == body.StyleId && s.IsActive, ct)) errors.Add("styleId", "The style does not exist or is inactive.");
        if (body.Quantity is not (>= 1 and <= 10_000_000)) errors.Add("quantity", "Quantity must be from 1 to 10,000,000 pieces.");
        if (!ProductionWire.TryParseDate(body.DueDate, out var due)) errors.Add("dueDate", "dueDate must be a date (yyyy-MM-dd).");
        ValidateText(errors, body);
        if (body.Status is not null && body.Status != "open") errors.Add("status", "A new order is open.");
        if (errors.Count > 0) return ProductionResult<OrderDto>.Invalid(errors);
        if (await _db.ProductionOrders.AnyAsync(o => o.OrderNumber == number, ct))
            return ProductionResult<OrderDto>.Conflict("duplicate_order", $"Order {number} already exists.");

        var now = DateTime.UtcNow;
        var order = new ProductionOrder
        {
            OrderNumber = number!, StyleId = body.StyleId!.Value, Buyer = Clean(body.Buyer), Quantity = body.Quantity!.Value, DueDate = due,
            Notes = Clean(body.Notes), CreatedAt = now, UpdatedAt = now, CreatedByUserId = actor.UserId, CreatedByEmail = actor.Email,
        };
        _db.ProductionOrders.Add(order);
        _db.AuditLog.Add(Audit(actor, traceId, "production_order.created", EntityType, order.Id.ToString(), null, "open",
            $"{order.OrderNumber} · {order.Quantity} pcs · due {ProductionWire.Date(due)}"));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return ProductionResult<OrderDto>.Conflict("duplicate_order", $"Order {number} already exists."); }
        var cal = await _read.CalendarAsync(ct);
        return ProductionResult<OrderDto>.Created(ToDto(order, _read.DueUtc(cal, order.DueDate)));
    }

    public async Task<ProductionResult<OrderDto>> UpdateAsync(Guid id, OrderWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var order = await _db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return ProductionResult<OrderDto>.NotFound("order_not_found");
        var errors = Errors();
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        if (body.OrderNumber is not null && Clean(body.OrderNumber) != order.OrderNumber) errors.Add("orderNumber", "The order number can't be changed.");
        if (body.Quantity is { } qty && qty is not (>= 1 and <= 10_000_000)) errors.Add("quantity", "Quantity must be from 1 to 10,000,000 pieces.");
        DateOnly? due = null;
        if (body.DueDate is not null)
        {
            if (ProductionWire.TryParseDate(body.DueDate, out var d)) due = d; else errors.Add("dueDate", "dueDate must be a date (yyyy-MM-dd).");
        }
        ProductionOrderStatus? status = null;
        if (body.Status is not null)
        {
            if (ProductionWire.TryParseOrderStatus(body.Status, out var s)) status = s;
            else errors.Add("status", $"status must be one of: {string.Join(", ", ProductionWire.OrderStatuses)}");
        }
        ValidateText(errors, body);
        if (body.StyleId is { } sid && sid != order.StyleId)
        {
            if (await _db.ProductionOutputEvents.AnyAsync(e => e.OrderId == id, ct)) errors.Add("styleId", "Output is already recorded for this order, so its style can't change.");
            else if (!await _db.ProductionStyles.AnyAsync(s => s.Id == sid && s.IsActive, ct)) errors.Add("styleId", "The style does not exist or is inactive.");
        }
        if (errors.Count > 0) return ProductionResult<OrderDto>.Invalid(errors);
        if (body.ExpectedVersion != order.Version) return ProductionResult<OrderDto>.Conflict("version_conflict", "The order changed since you loaded it. Reload and try again.");
        if (order.Status == ProductionOrderStatus.Cancelled && status != ProductionOrderStatus.Cancelled)
            return ProductionResult<OrderDto>.Conflict("order_cancelled", "A cancelled order can't be changed.");
        if (status is (ProductionOrderStatus.Cancelled or ProductionOrderStatus.Closed) && status != order.Status
            && await _db.ProductionPlans.AnyAsync(p => p.OrderId == id && (p.Status == ProductionPlanStatus.Active || p.Status == ProductionPlanStatus.PendingApproval), ct))
            return ProductionResult<OrderDto>.Conflict("order_has_live_plans", "Complete or cancel the order's active and pending plans first.");

        var before = Describe(order);
        if (body.Quantity is { } q) order.Quantity = q;
        if (due is { } dd) order.DueDate = dd;
        if (body.Buyer is not null) order.Buyer = Clean(body.Buyer);
        if (body.Notes is not null) order.Notes = Clean(body.Notes);
        if (body.StyleId is { } style) order.StyleId = style;
        if (status is { } st) order.Status = st;
        order.UpdatedAt = DateTime.UtcNow;
        _db.AuditLog.Add(Audit(actor, traceId, "production_order.updated", EntityType, order.Id.ToString(), before, Describe(order), null));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ProductionResult<OrderDto>.Conflict("version_conflict", "The order changed since you loaded it. Reload and try again."); }
        var cal = await _read.CalendarAsync(ct);
        return ProductionResult<OrderDto>.Ok(ToDto(order, _read.DueUtc(cal, order.DueDate)));
    }

    private static void ValidateText(Dictionary<string, string[]> errors, OrderWrite b)
    {
        if (b.Buyer is { Length: > 120 }) errors.Add("buyer", "Buyer is limited to 120 characters.");
        if (b.Notes is { Length: > 1000 }) errors.Add("notes", "Notes are limited to 1000 characters.");
    }

    private static string Describe(ProductionOrder o) =>
        $"{o.OrderNumber} · {o.Quantity} pcs · due {ProductionWire.Date(o.DueDate)} · {ProductionWire.OrderStatus(o.Status)}";

    private static OrderDto ToDto(ProductionOrder o, DateTime dueAt) => new(o.Id, o.OrderNumber, o.StyleId, o.Buyer, o.Quantity,
        ProductionWire.Date(o.DueDate), dueAt, ProductionWire.OrderStatus(o.Status), o.Notes, o.CreatedAt, o.CreatedByEmail, o.Version);
}
