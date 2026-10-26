using System.Globalization;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Merchandising;
using FactoryBrain.Application.Dtos.Production;
using FactoryBrain.Application.Merchandising;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Infrastructure.Production;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Merchandising;

/// <summary>
/// Buyer orders (<c>merch_orders</c>), their milestones and conflict
/// resolution. Values typed here are recorded as manual (and become a
/// conflict source); values confirmed from documents or chosen when
/// resolving a conflict keep that provenance. Every write is audited under
/// the order; edits check the row's <c>xmin</c>.
/// </summary>
public sealed class MerchOrderService : IMerchOrderService
{
    public static readonly IReadOnlyList<string> DefaultRequiredDocuments = new[] { "purchase_order", "tech_pack" };
    public const int DeadlineHorizonDays = 14;

    private readonly FactoryBrainDbContext _db;
    private readonly ProductionOptions _plant;
    private readonly MerchReadModel _read;

    public MerchOrderService(FactoryBrainDbContext db, ProductionOptions plant, MerchOptions options, IDocumentExtractionProvider provider)
    {
        _db = db; _plant = plant;
        _read = new MerchReadModel(db, plant, options, provider);
    }

    // ── Overview ────────────────────────────────────────────────────────────

    public async Task<MerchOverviewDto> OverviewAsync(CancellationToken ct)
    {
        var today = _read.Today;
        var horizon = today.AddDays(DeadlineHorizonDays);
        var active = _db.MerchOrders.AsNoTracking().Where(o => o.Status == MerchOrderStatus.Active);
        var activeCount = await active.CountAsync(ct);
        var currentVersions = _db.MerchDocumentVersions.AsNoTracking().Where(v => v.IsCurrent);
        var awaiting = await currentVersions.CountAsync(v => v.Status == MerchVersionStatus.NeedsReview, ct);
        var failed = await currentVersions.CountAsync(v => v.Status == MerchVersionStatus.Failed, ct);
        var conflicts = await _db.MerchConflicts.AsNoTracking().CountAsync(c => c.Status == MerchConflictStatus.Open, ct);
        var overdue = await _db.MerchMilestones.AsNoTracking()
            .CountAsync(m => m.Status == MerchMilestoneStatus.Pending && m.DueDate < today && active.Any(o => o.Id == m.OrderId), ct);

        var required = await active.Select(o => new { o.Id, o.RequiredDocumentTypes }).ToListAsync(ct);
        var present = (await _db.MerchDocuments.AsNoTracking().Where(d => active.Any(o => o.Id == d.OrderId)).Select(d => new { d.OrderId, d.Type }).ToListAsync(ct))
            .Select(d => (d.OrderId, MerchWire.DocumentType(d.Type))).ToHashSet();
        var missing = required.Sum(o => o.RequiredDocumentTypes.Distinct().Count(t => !present.Contains((o.Id, t))));

        var orders = await active.Select(o => new { o.Id, o.Reference, o.Buyer, o.BuyerOrderNumber, o.DeliveryDate }).ToDictionaryAsync(o => o.Id, ct);
        var milestones = await _db.MerchMilestones.AsNoTracking()
            .Where(m => m.Status == MerchMilestoneStatus.Pending && m.DueDate <= horizon && active.Any(o => o.Id == m.OrderId))
            .OrderBy(m => m.DueDate).Take(50).ToListAsync(ct);
        var deadlines = milestones.Select(m =>
            {
                var o = orders[m.OrderId];
                return new MerchDeadlineDto("milestone", o.Id, o.Reference, o.Buyer, o.BuyerOrderNumber, m.Name, MerchWire.Date(m.DueDate),
                    m.DueDate.DayNumber - today.DayNumber, m.DueDate < today, m.Id);
            })
            .Concat(orders.Values.Where(o => o.DeliveryDate is { } d && d <= horizon).Select(o =>
                new MerchDeadlineDto("delivery", o.Id, o.Reference, o.Buyer, o.BuyerOrderNumber, "Delivery", MerchWire.Date(o.DeliveryDate!.Value),
                    o.DeliveryDate!.Value.DayNumber - today.DayNumber, o.DeliveryDate < today, null)))
            .OrderBy(d => d.DueDate, StringComparer.Ordinal).ThenBy(d => d.OrderReference, StringComparer.Ordinal)
            .Take(25).ToList();

        var reviewDocs = await _db.MerchDocuments.AsNoTracking()
            .Where(d => _db.MerchDocumentVersions.Any(v => v.Id == d.CurrentVersionId && (v.Status == MerchVersionStatus.NeedsReview || v.Status == MerchVersionStatus.Failed)))
            .OrderBy(d => d.UpdatedAt).Take(10).ToListAsync(ct);
        var openConflicts = await _db.MerchConflicts.AsNoTracking().Where(c => c.Status == MerchConflictStatus.Open)
            .OrderByDescending(c => c.DetectedAt).Take(10).ToListAsync(ct);
        var refs = await ReferencesAsync(openConflicts.Select(c => c.OrderId), ct);

        return new MerchOverviewDto(activeCount, awaiting, conflicts, overdue, failed, missing, deadlines,
            await _read.DocumentItemsAsync(reviewDocs, ct), openConflicts.Select(c => MerchReadModel.ToDto(c, refs.GetValueOrDefault(c.OrderId, "?"))).ToList(),
            _read.Extraction(), MerchWire.Date(today), DeadlineHorizonDays);
    }

    private Task<Dictionary<Guid, string>> ReferencesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return _db.MerchOrders.AsNoTracking().Where(o => list.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Reference, ct);
    }

    // ── List ────────────────────────────────────────────────────────────────

    public async Task<MerchResult<Paged<MerchOrderListItem>>> ListAsync(MerchOrderQuery q, CancellationToken ct)
    {
        var errors = ProductionSupport.Errors();
        MerchOrderStatus? status = null;
        if (q.Status is not null)
        {
            if (MerchWire.TryParseOrderStatus(q.Status, out var s)) status = s;
            else errors.Add("status", $"status must be one of: {string.Join(", ", MerchWire.OrderStatuses)}");
        }
        if (q.Attention is not null and not ("needs_review" or "conflicts" or "overdue" or "missing_documents" or "failed"))
            errors.Add("attention", "attention must be needs_review, conflicts, overdue, missing_documents or failed.");
        if (q.Page < 1) errors.Add("page", "page must be 1 or more.");
        if (q.PageSize is < 1 or > 100) errors.Add("pageSize", "pageSize must be from 1 to 100.");
        if (q.Q is { Length: > 100 }) errors.Add("q", "Search text is limited to 100 characters.");
        if (q.Buyer is { Length: > 120 }) errors.Add("buyer", "Buyer is limited to 120 characters.");
        if (q.Sort is not null and not ("delivery" or "newest" or "updated")) errors.Add("sort", "sort must be delivery, newest or updated.");
        if (q.DeliveryFrom is { } f && q.DeliveryTo is { } t && f > t) errors.Add("deliveryFrom", "deliveryFrom must not be after deliveryTo.");
        if (errors.Count > 0) return MerchResult<Paged<MerchOrderListItem>>.Invalid(errors);

        var today = _read.Today;
        var query = _db.MerchOrders.AsNoTracking();
        if (status is { } st) query = query.Where(o => o.Status == st);
        if (MerchSupport.Clean(q.Buyer) is { } buyer)
        {
            var pattern = "%" + ProductionSupport.EscapeLike(buyer) + "%";
            query = query.Where(o => EF.Functions.ILike(o.Buyer, pattern, "\\"));
        }
        if (MerchSupport.Clean(q.Q) is { } text)
        {
            var pattern = "%" + ProductionSupport.EscapeLike(text) + "%";
            query = query.Where(o => EF.Functions.ILike(o.Reference, pattern, "\\") || EF.Functions.ILike(o.Buyer, pattern, "\\")
                || EF.Functions.ILike(o.BuyerOrderNumber, pattern, "\\") || (o.StyleRef != null && EF.Functions.ILike(o.StyleRef, pattern, "\\"))
                || (o.ProductDescription != null && EF.Functions.ILike(o.ProductDescription, pattern, "\\")));
        }
        if (q.DeliveryFrom is { } from) query = query.Where(o => o.DeliveryDate >= from);
        if (q.DeliveryTo is { } to) query = query.Where(o => o.DeliveryDate <= to);
        switch (q.Attention)
        {
            case "needs_review":
                query = query.Where(o => _db.MerchDocumentVersions.Any(v => v.OrderId == o.Id && v.IsCurrent && v.Status == MerchVersionStatus.NeedsReview));
                break;
            case "failed":
                query = query.Where(o => _db.MerchDocumentVersions.Any(v => v.OrderId == o.Id && v.IsCurrent && v.Status == MerchVersionStatus.Failed));
                break;
            case "conflicts":
                query = query.Where(o => _db.MerchConflicts.Any(c => c.OrderId == o.Id && c.Status == MerchConflictStatus.Open));
                break;
            case "overdue":
                query = query.Where(o => o.Status == MerchOrderStatus.Active && (o.DeliveryDate < today
                    || _db.MerchMilestones.Any(m => m.OrderId == o.Id && m.Status == MerchMilestoneStatus.Pending && m.DueDate < today)));
                break;
            case "missing_documents":
                var ids = await MissingDocumentOrderIdsAsync(query, ct);
                query = query.Where(o => ids.Contains(o.Id));
                break;
        }

        var total = await query.CountAsync(ct);
        var ordered = q.Sort switch
        {
            "newest" => query.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id),
            "updated" => query.OrderByDescending(o => o.UpdatedAt).ThenBy(o => o.Id),
            _ => query.OrderBy(o => o.DeliveryDate == null).ThenBy(o => o.DeliveryDate).ThenBy(o => o.Reference),
        };
        var rows = await ordered.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        var rowIds = rows.Select(o => o.Id).ToList();

        var docs = await _db.MerchDocuments.AsNoTracking().Where(d => rowIds.Contains(d.OrderId)).Select(d => new { d.OrderId, d.Type }).ToListAsync(ct);
        var versions = await _db.MerchDocumentVersions.AsNoTracking().Where(v => rowIds.Contains(v.OrderId) && v.IsCurrent)
            .Select(v => new { v.OrderId, v.Status }).ToListAsync(ct);
        var openConflicts = await _db.MerchConflicts.AsNoTracking().Where(c => rowIds.Contains(c.OrderId) && c.Status == MerchConflictStatus.Open)
            .GroupBy(c => c.OrderId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var pending = await _db.MerchMilestones.AsNoTracking().Where(m => rowIds.Contains(m.OrderId) && m.Status == MerchMilestoneStatus.Pending)
            .OrderBy(m => m.DueDate).ToListAsync(ct);

        var items = rows.Select(o =>
        {
            var types = docs.Where(d => d.OrderId == o.Id).Select(d => MerchWire.DocumentType(d.Type)).ToHashSet();
            var ms = pending.Where(m => m.OrderId == o.Id).ToList();
            var next = ms.FirstOrDefault(m => m.DueDate >= today) ?? ms.FirstOrDefault();
            return new MerchOrderListItem(o.Id, o.Reference, o.Buyer, o.BuyerOrderNumber, o.StyleRef, o.Quantity, o.Unit,
                o.DeliveryDate is { } d ? MerchWire.Date(d) : null, o.DeliveryDate is { } dd ? dd.DayNumber - today.DayNumber : null,
                MerchWire.OrderStatus(o.Status), docs.Count(x => x.OrderId == o.Id),
                versions.Count(v => v.OrderId == o.Id && v.Status == MerchVersionStatus.NeedsReview),
                versions.Count(v => v.OrderId == o.Id && v.Status == MerchVersionStatus.Failed),
                openConflicts.GetValueOrDefault(o.Id),
                o.Status == MerchOrderStatus.Active ? ms.Count(m => m.DueDate < today) : 0,
                o.RequiredDocumentTypes.Distinct().Count(t => !types.Contains(t)),
                next is null ? null : new MerchNextMilestoneDto(next.Name, MerchWire.Date(next.DueDate), next.DueDate.DayNumber - today.DayNumber),
                o.UpdatedAt, o.Version);
        }).ToList();
        return MerchResult<Paged<MerchOrderListItem>>.Ok(new Paged<MerchOrderListItem>(items, q.Page, q.PageSize, total, (int)Math.Ceiling(total / (double)q.PageSize)));
    }

    private async Task<List<Guid>> MissingDocumentOrderIdsAsync(IQueryable<MerchOrder> scope, CancellationToken ct)
    {
        var orders = await scope.Select(o => new { o.Id, o.RequiredDocumentTypes }).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var present = (await _db.MerchDocuments.AsNoTracking().Where(d => ids.Contains(d.OrderId)).Select(d => new { d.OrderId, d.Type }).ToListAsync(ct))
            .Select(d => (d.OrderId, MerchWire.DocumentType(d.Type))).ToHashSet();
        return orders.Where(o => o.RequiredDocumentTypes.Any(t => !present.Contains((o.Id, t)))).Select(o => o.Id).ToList();
    }

    public Task<MerchOrderDetail?> GetAsync(Guid id, CancellationToken ct) => _read.OrderDetailAsync(id, ct);

    // ── Create / update ─────────────────────────────────────────────────────

    /// <summary>Validated, normalised field values from a write body (null = not given).</summary>
    private Dictionary<string, string?> ReadFields(MerchOrderWrite b, Dictionary<string, string[]> errors, DateOnly today)
    {
        var values = new Dictionary<string, string?>();
        void Take(string key, string wireName, string? raw)
        {
            if (raw is null) return;
            var norm = MerchFields.Normalize(key, raw, key == MerchFields.DeliveryDate ? today : null);
            if (norm.HasBlockingIssue || norm.Normalized is null)
                errors.Add(wireName, $"{MerchFields.Get(key).Label}: {string.Join("; ", norm.Issues.Where(MerchFields.IsBlocking).Select(MerchFields.DescribeIssue))}");
            else values[key] = norm.Normalized;
        }
        var inv = CultureInfo.InvariantCulture;
        Take(MerchFields.Buyer, "buyer", b.Buyer);
        Take(MerchFields.OrderNumber, "buyerOrderNumber", b.BuyerOrderNumber);
        Take(MerchFields.Style, "styleRef", b.StyleRef);
        Take(MerchFields.Product, "productDescription", b.ProductDescription);
        Take(MerchFields.Quantity, "quantity", b.Quantity?.ToString(inv));
        Take(MerchFields.Unit, "unit", b.Unit);
        Take(MerchFields.DeliveryDate, "deliveryDate", b.DeliveryDate);
        Take(MerchFields.Currency, "currency", b.Currency);
        Take(MerchFields.UnitPrice, "unitPrice", b.UnitPrice?.ToString(inv));
        Take(MerchFields.Destination, "destination", b.Destination);
        return values;
    }

    private static void ValidateCommon(MerchOrderWrite b, Dictionary<string, string[]> errors)
    {
        if (b.Notes is { Length: > 1000 }) errors.Add("notes", "Notes are limited to 1000 characters.");
        if (b.RequiredDocumentTypes is { } types)
        {
            if (types.Count > MerchWire.DocumentTypes.Length) errors.Add("requiredDocumentTypes", "Too many required document types.");
            foreach (var t in types.Where(t => !MerchWire.DocumentTypes.Contains(t)))
                errors.Add("requiredDocumentTypes", $"'{t}' is not a document type ({string.Join(", ", MerchWire.DocumentTypes)}).");
        }
        if (b.Milestones is { Count: > 50 }) errors.Add("milestones", "At most 50 milestones per request.");
    }

    public async Task<MerchResult<MerchOrderDetail>> CreateAsync(MerchOrderWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = ProductionSupport.Errors();
        var today = _read.Today;
        if (string.IsNullOrWhiteSpace(body.Buyer)) errors.Add("buyer", "The buyer is required.");
        if (string.IsNullOrWhiteSpace(body.BuyerOrderNumber)) errors.Add("buyerOrderNumber", "The buyer's PO number is required.");
        var values = ReadFields(body, errors, today);
        ValidateCommon(body, errors);
        if (body.Status is not null && body.Status != "active") errors.Add("status", "A new order is active.");
        if (body.ProductionOrderId is { } pid && !await _db.ProductionOrders.AnyAsync(p => p.Id == pid, ct))
            errors.Add("productionOrderId", "The production order does not exist.");
        var milestones = new List<MerchMilestone>();
        foreach (var (m, i) in (body.Milestones ?? new()).Select((m, i) => (m, i)))
            if (ParseMilestone(m, $"milestones[{i}]", errors, today, isNew: true) is { } parsed) milestones.Add(parsed);
        if (errors.Count > 0) return MerchResult<MerchOrderDetail>.Invalid(errors);

        var buyer = values[MerchFields.Buyer]!;
        var number = values[MerchFields.OrderNumber]!;
        if (await _db.MerchOrders.AnyAsync(o => o.BuyerKey == MerchSupport.BuyerKey(buyer) && o.BuyerOrderNumber == number, ct))
            return MerchResult<MerchOrderDetail>.Conflict("duplicate_order", $"{buyer} PO {number} is already registered.");

        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var order = new MerchOrder
        {
            Id = id, Reference = ProductionSupport.NewReference("MO", id, now), Buyer = buyer, BuyerKey = MerchSupport.BuyerKey(buyer), BuyerOrderNumber = number,
            RequiredDocumentTypes = (body.RequiredDocumentTypes ?? DefaultRequiredDocuments.ToList()).Distinct().ToList(),
            ProductionOrderId = body.ProductionOrderId, Notes = MerchSupport.Clean(body.Notes),
            CreatedAt = now, UpdatedAt = now, CreatedByUserId = actor.UserId, CreatedByEmail = actor.Email,
        };
        var sources = new Dictionary<string, FieldSourceRecord>();
        foreach (var (k, v) in values)
        {
            MerchSupport.SetValue(order, k, v);
            sources[k] = new FieldSourceRecord(FieldSourceRecord.Manual, null, null, null, null, null, actor.Email, now);
        }
        MerchSupport.SetSources(order, sources);
        _db.MerchOrders.Add(order);
        foreach (var m in milestones)
        {
            m.OrderId = id;
            m.CreatedByEmail = actor.Email;
            m.CreatedAt = m.UpdatedAt = now;
            _db.MerchMilestones.Add(m);
        }
        _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_order.created", id, null, "active",
            $"{order.Reference} · {Describe(order)} · {milestones.Count} milestone(s) · requires {string.Join(",", order.RequiredDocumentTypes)}"));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ProductionSupport.IsUniqueViolation(ex))
        {
            return MerchResult<MerchOrderDetail>.Conflict("duplicate_order", $"{buyer} PO {number} is already registered.");
        }
        return MerchResult<MerchOrderDetail>.Created((await _read.OrderDetailAsync(id, ct))!);
    }

    public async Task<MerchResult<MerchOrderDetail>> UpdateAsync(Guid id, MerchOrderWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = ProductionSupport.Errors();
        var today = _read.Today;
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        var values = ReadFields(body, errors, today);
        ValidateCommon(body, errors);
        if (body.Milestones is not null) errors.Add("milestones", "Add or change milestones through the milestone endpoints.");
        MerchOrderStatus? status = null;
        if (body.Status is not null)
        {
            if (MerchWire.TryParseOrderStatus(body.Status, out var s)) status = s;
            else errors.Add("status", $"status must be one of: {string.Join(", ", MerchWire.OrderStatuses)}");
        }
        var clear = (body.ClearFields ?? new()).Distinct().ToList();
        foreach (var k in clear)
        {
            if (!MerchFields.IsKnown(k)) errors.Add("clearFields", $"Unknown field '{k}'.");
            else if (k is MerchFields.Buyer or MerchFields.OrderNumber) errors.Add("clearFields", "The buyer and PO number can't be cleared.");
            else if (values.ContainsKey(k)) errors.Add("clearFields", $"{MerchFields.Get(k).Label} is both set and cleared.");
        }
        if (body.ProductionOrderId is { } pid && !await _db.ProductionOrders.AnyAsync(p => p.Id == pid, ct))
            errors.Add("productionOrderId", "The production order does not exist.");
        if (errors.Count > 0) return MerchResult<MerchOrderDetail>.Invalid(errors);

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            var result = await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await MerchSupport.LockOrderAsync(_db, id, ct);
                var order = await _db.MerchOrders.FirstOrDefaultAsync(o => o.Id == id, ct);
                if (order is null) return MerchResult<bool>.NotFound("order_not_found");
                if (body.ExpectedVersion != order.Version)
                    return MerchResult<bool>.Conflict("version_conflict", "The order changed since you loaded it. Reload and try again.");

                var before = Describe(order);
                var now = DateTime.UtcNow;
                var sources = MerchSupport.Sources(order);
                var changed = new List<string>();
                foreach (var (k, v) in values)
                {
                    if (MerchSupport.GetValue(order, k) == v) continue;
                    MerchSupport.SetValue(order, k, v);
                    sources[k] = new FieldSourceRecord(FieldSourceRecord.Manual, null, null, null, null, null, actor.Email, now);
                    changed.Add(k);
                }
                foreach (var k in clear.Where(k => MerchSupport.GetValue(order, k) is not null))
                {
                    MerchSupport.SetValue(order, k, null);
                    sources.Remove(k);
                    changed.Add($"-{k}");
                }
                MerchSupport.SetSources(order, sources);
                var fromStatus = MerchWire.OrderStatus(order.Status);
                if (status is { } st) order.Status = st;
                if (body.Notes is not null) order.Notes = MerchSupport.Clean(body.Notes);
                if (body.RequiredDocumentTypes is { } types) order.RequiredDocumentTypes = types.Distinct().ToList();
                if (body.UnlinkProductionOrder == true) order.ProductionOrderId = null;
                else if (body.ProductionOrderId is { } link) order.ProductionOrderId = link;
                order.UpdatedAt = now;
                _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_order.updated", id, fromStatus, MerchWire.OrderStatus(order.Status),
                    $"changed [{string.Join(",", changed)}] · before: {before} · after: {Describe(order)}"));
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateConcurrencyException)
                {
                    await tx.RollbackAsync(ct);
                    return MerchResult<bool>.Conflict("version_conflict", "The order changed since you loaded it. Reload and try again.");
                }
                await MerchSupport.SyncConflictsAsync(_db, order, actor, traceId, ct);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return MerchResult<bool>.Ok(true);
            });
            if (result.Outcome != MerchOutcome.Ok) return new MerchResult<MerchOrderDetail>(result.Outcome, null, result.Errors, result.Code, result.Message);
        }
        catch (DbUpdateException ex) when (ProductionSupport.IsUniqueViolation(ex))
        {
            return MerchResult<MerchOrderDetail>.Conflict("duplicate_order", "Another order of this buyer already has that PO number.");
        }
        return MerchResult<MerchOrderDetail>.Ok((await _read.OrderDetailAsync(id, ct))!);
    }

    private static string Describe(MerchOrder o) =>
        $"{o.Buyer} PO {o.BuyerOrderNumber} · style {o.StyleRef ?? "-"} · {(o.Quantity is { } q ? q.ToString("N0", CultureInfo.InvariantCulture) : "-")} {o.Unit ?? ""}".TrimEnd()
        + $" · delivery {(o.DeliveryDate is { } d ? MerchWire.Date(d) : "-")} · {MerchWire.OrderStatus(o.Status)}";

    // ── Milestones ──────────────────────────────────────────────────────────

    private static MerchMilestone? ParseMilestone(MerchMilestoneWrite m, string field, Dictionary<string, string[]> errors, DateOnly today, bool isNew)
    {
        var count = errors.Count;
        var name = MerchFields.CollapseWhitespace(m.Name);
        if (isNew && string.IsNullOrEmpty(name)) errors.Add(field, "A milestone needs a name.");
        if (name is { Length: > 120 }) errors.Add(field, "The milestone name is limited to 120 characters.");
        DateOnly? due = null;
        if (m.DueDate is not null)
        {
            if (MerchWire.TryParseIsoDate(m.DueDate, out var d) && d.Year is >= 2000 and <= 2100) due = d;
            else errors.Add(field, "dueDate must be a date (yyyy-MM-dd).");
        }
        else if (isNew) errors.Add(field, "A milestone needs a due date.");
        MerchMilestoneStatus status = MerchMilestoneStatus.Pending;
        if (m.Status is not null && !MerchWire.TryParseMilestoneStatus(m.Status, out status))
            errors.Add(field, $"status must be one of: {string.Join(", ", MerchWire.MilestoneStatuses)}");
        DateOnly? completed = null;
        if (m.CompletedOn is not null)
        {
            if (MerchWire.TryParseIsoDate(m.CompletedOn, out var c) && c <= today) completed = c;
            else errors.Add(field, "completedOn must be a date (yyyy-MM-dd), not in the future.");
        }
        if (m.Notes is { Length: > 500 }) errors.Add(field, "Milestone notes are limited to 500 characters.");
        if (errors.Count > count) return null;
        return new MerchMilestone
        {
            Name = name ?? "", DueDate = due ?? today, Status = status,
            CompletedOn = status == MerchMilestoneStatus.Done ? completed ?? today : null, Notes = MerchSupport.Clean(m.Notes),
        };
    }

    public async Task<MerchResult<MerchMilestoneDto>> AddMilestoneAsync(Guid orderId, MerchMilestoneWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        if (!await _db.MerchOrders.AnyAsync(o => o.Id == orderId, ct)) return MerchResult<MerchMilestoneDto>.NotFound("order_not_found");
        var errors = ProductionSupport.Errors();
        var today = _read.Today;
        var m = ParseMilestone(body, "milestone", errors, today, isNew: true);
        if (errors.Count > 0) return MerchResult<MerchMilestoneDto>.Invalid(errors);
        if (await _db.MerchMilestones.CountAsync(x => x.OrderId == orderId, ct) >= 100)
            return MerchResult<MerchMilestoneDto>.Conflict("too_many_milestones", "An order holds at most 100 milestones.");
        var now = DateTime.UtcNow;
        m!.OrderId = orderId;
        m.CreatedAt = m.UpdatedAt = now;
        m.CreatedByEmail = actor.Email;
        _db.MerchMilestones.Add(m);
        _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_milestone.created", orderId, null, MerchWire.MilestoneStatus(m.Status),
            $"{m.Name} due {MerchWire.Date(m.DueDate)}"));
        await _db.SaveChangesAsync(ct);
        return MerchResult<MerchMilestoneDto>.Created(MerchReadModel.ToDto(m, today));
    }

    public async Task<MerchResult<MerchMilestoneDto>> UpdateMilestoneAsync(Guid id, MerchMilestoneWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var m = await _db.MerchMilestones.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return MerchResult<MerchMilestoneDto>.NotFound("milestone_not_found");
        var errors = ProductionSupport.Errors();
        var today = _read.Today;
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        var parsed = ParseMilestone(body, "milestone", errors, today, isNew: false);
        if (errors.Count > 0) return MerchResult<MerchMilestoneDto>.Invalid(errors);
        if (body.ExpectedVersion != m.Version) return MerchResult<MerchMilestoneDto>.Conflict("version_conflict", "The milestone changed since you loaded it. Reload and try again.");

        var before = $"{m.Name} due {MerchWire.Date(m.DueDate)} {MerchWire.MilestoneStatus(m.Status)}";
        var from = MerchWire.MilestoneStatus(m.Status);
        if (!string.IsNullOrEmpty(parsed!.Name)) m.Name = parsed.Name;
        if (body.DueDate is not null) m.DueDate = parsed.DueDate;
        if (body.Status is not null)
        {
            m.Status = parsed.Status;
            m.CompletedOn = parsed.Status == MerchMilestoneStatus.Done ? parsed.CompletedOn : null;
        }
        else if (body.CompletedOn is not null && m.Status == MerchMilestoneStatus.Done) m.CompletedOn = parsed.CompletedOn;
        if (body.Notes is not null) m.Notes = MerchSupport.Clean(body.Notes);
        m.UpdatedAt = DateTime.UtcNow;
        m.UpdatedByEmail = actor.Email;
        _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_milestone.updated", m.OrderId, from, MerchWire.MilestoneStatus(m.Status),
            $"before: {before} · after: {m.Name} due {MerchWire.Date(m.DueDate)}{(m.CompletedOn is { } c ? $" done {MerchWire.Date(c)}" : "")}"));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return MerchResult<MerchMilestoneDto>.Conflict("version_conflict", "The milestone changed since you loaded it. Reload and try again."); }
        return MerchResult<MerchMilestoneDto>.Ok(MerchReadModel.ToDto(m, today));
    }

    // ── Conflict resolution ─────────────────────────────────────────────────

    public async Task<MerchResult<MerchConflictDto>> ResolveConflictAsync(Guid id, MerchConflictResolution body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var orderId = await _db.MerchConflicts.AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.OrderId).FirstOrDefaultAsync(ct);
        if (orderId is null) return MerchResult<MerchConflictDto>.NotFound("conflict_not_found");
        var errors = ProductionSupport.Errors();
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        if (string.IsNullOrWhiteSpace(body.ChosenValue)) errors.Add("chosenValue", "Choose or enter the correct value.");
        if (MerchFields.CollapseWhitespace(body.Note) is not { Length: >= 3 }) errors.Add("note", "Explain the decision (for example, who confirmed it).");
        else if (body.Note!.Length > 1000) errors.Add("note", "The note is limited to 1000 characters.");
        if (errors.Count > 0) return MerchResult<MerchConflictDto>.Invalid(errors);
        var today = _read.Today;

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await MerchSupport.LockOrderAsync(_db, orderId.Value, ct);
                var conflict = await _db.MerchConflicts.FirstAsync(c => c.Id == id, ct);
                if (conflict.Status != MerchConflictStatus.Open)
                    return MerchResult<MerchConflictDto>.Conflict("not_open", $"This conflict is already {MerchWire.ConflictStatus(conflict.Status)}.");
                if (body.ExpectedVersion != conflict.Version)
                    return MerchResult<MerchConflictDto>.Conflict("version_conflict", "The conflict changed since you loaded it (a document was added or confirmed). Reload and decide again.");
                var norm = MerchFields.Normalize(conflict.FieldKey, body.ChosenValue, conflict.FieldKey == MerchFields.DeliveryDate ? today : null);
                if (norm.HasBlockingIssue || norm.Normalized is null)
                    return MerchResult<MerchConflictDto>.Invalid("chosenValue",
                        $"{MerchFields.Get(conflict.FieldKey).Label}: {string.Join("; ", norm.Issues.Where(MerchFields.IsBlocking).Select(MerchFields.DescribeIssue))}");

                var order = await _db.MerchOrders.FirstAsync(o => o.Id == conflict.OrderId, ct);
                var now = DateTime.UtcNow;
                var before = MerchSupport.GetValue(order, conflict.FieldKey);
                MerchSupport.SetValue(order, conflict.FieldKey, norm.Normalized);
                var sources = MerchSupport.Sources(order);
                sources[conflict.FieldKey] = new FieldSourceRecord(FieldSourceRecord.ConflictResolution, null, null, null, null, conflict.Id, actor.Email, now);
                MerchSupport.SetSources(order, sources);
                order.UpdatedAt = now;

                // The decision covers the sources as they stand once the order value no longer counts as typed.
                var after = (await MerchSupport.DetectAsync(_db, order, ct)).FirstOrDefault(d => d.FieldKey == conflict.FieldKey);
                conflict.Status = MerchConflictStatus.Resolved;
                conflict.ResolvedValue = norm.Normalized;
                conflict.ResolutionNote = MerchFields.CollapseWhitespace(body.Note);
                conflict.ResolvedAt = now;
                conflict.ResolvedByUserId = actor.UserId;
                conflict.ResolvedByEmail = actor.Email;
                conflict.UpdatedAt = now;
                if (after is not null) conflict.Fingerprint = after.Fingerprint;
                _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_conflict.resolved", order.Id, "open", "resolved",
                    $"conflict {conflict.Id} · {MerchFields.Get(conflict.FieldKey).Label}: {MerchFields.Display(conflict.FieldKey, before)} → " +
                    $"{MerchFields.Display(conflict.FieldKey, norm.Normalized)} · {conflict.ResolutionNote}"));
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateConcurrencyException)
                {
                    await tx.RollbackAsync(ct);
                    return MerchResult<MerchConflictDto>.Conflict("version_conflict", "The conflict or order changed meanwhile. Reload and decide again.");
                }
                await MerchSupport.SyncConflictsAsync(_db, order, actor, traceId, ct);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return MerchResult<MerchConflictDto>.Ok(MerchReadModel.ToDto(conflict, order.Reference));
            });
        }
        catch (DbUpdateException ex) when (ProductionSupport.IsUniqueViolation(ex))
        {
            return MerchResult<MerchConflictDto>.Conflict("duplicate_order", "Another order of this buyer already has that PO number.");
        }
    }
}
