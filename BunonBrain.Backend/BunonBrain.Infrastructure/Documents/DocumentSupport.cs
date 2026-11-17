using System.Globalization;
using System.Text.Json.Nodes;
using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Access;
using BunonBrain.Application.Documents;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BunonBrain.Infrastructure.Documents;

/// <summary>Who may see and act on which documents. Every document query starts here.</summary>
internal static class DocScope
{
    /// <summary>Documents in the caller's factories and categories; a line-scoped caller sees documents of their lines (and ones they created).</summary>
    public static IQueryable<FactoryDocument> Visible(BunonBrainDbContext db, AccessContext c, bool tracking = false)
    {
        var q = tracking ? db.FactoryDocuments.AsQueryable() : db.FactoryDocuments.AsNoTracking();
        if (!c.IsPlatform) { var f = c.FactoryIds.ToList(); q = q.Where(d => f.Contains(d.FactoryId)); }
        var cats = DocumentCategories.For(c).ToList();
        q = q.Where(d => cats.Contains(d.Category));
        if (c.IsLineScoped)
        {
            var lines = c.LineIds.ToList();
            var me = c.UserId;
            q = q.Where(d => (d.LineId != null && lines.Contains(d.LineId)) || d.CreatedByUserId == me);
        }
        return q;
    }

    public static bool HasCategory(AccessContext c, string category) =>
        c.Has(Permissions.DocumentsControl) || (DocumentCategories.PermissionFor(category) is { } p && c.Has(p));

    public static bool CanPrepare(AccessContext c, string category) => c.Has(Permissions.DocumentsCreate) && HasCategory(c, category);
    public static bool CanReview(AccessContext c, string category) => c.Has(Permissions.DocumentsReview) && HasCategory(c, category);
    public static bool CanApprove(AccessContext c, string category, string risk) =>
        HasCategory(c, category) && (risk == DocumentRisks.High ? c.Has(Permissions.DocumentsApproveHigh) : c.HasAny(Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh));

    public static Task LockAsync(BunonBrainDbContext db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"documents:" + key}, 0))", ct);

    public static bool IsUniqueViolation(DbUpdateException ex, string? constraint = null) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (constraint is null || (pg.ConstraintName?.Contains(constraint, StringComparison.Ordinal) ?? false));

    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Notifications to the people who must act — never broadcast: users active
/// in the document's factory whose roles give them the needed permission and
/// the document's category (a line-scoped supervisor only for their lines).
/// One notification per user and key, so repeated checks don't repeat it.
/// </summary>
public sealed class DocumentNotifier
{
    private readonly BunonBrainDbContext _db;
    private readonly ILogger<DocumentNotifier> _log;

    public DocumentNotifier(BunonBrainDbContext db, ILogger<DocumentNotifier> log) { _db = db; _log = log; }

    /// <summary>Active users of the factory whose combined roles hold one of <paramref name="anyOf"/> plus the category.</summary>
    public async Task<List<Guid>> ResponsibleAsync(Guid factoryId, string category, string? lineId, IReadOnlyCollection<string> anyOf, CancellationToken ct)
    {
        var users = await (from u in _db.Users.AsNoTracking()
                           join f in _db.UserFactories.AsNoTracking() on u.Id equals f.UserId
                           where u.IsActive && f.FactoryId == factoryId
                           select u.Id).Distinct().ToListAsync(ct);
        if (users.Count == 0) return users;
        var roles = await _db.UserRoles.AsNoTracking().Where(r => users.Contains(r.UserId)).ToListAsync(ct);
        var lines = lineId is null ? new List<UserProductionLineAssignment>()
            : await _db.Set<UserProductionLineAssignment>().AsNoTracking().Where(l => users.Contains(l.UserId) && l.LineId == lineId).ToListAsync(ct);
        var catPerm = DocumentCategories.PermissionFor(category);
        var result = new List<Guid>();
        foreach (var g in roles.GroupBy(r => r.UserId))
        {
            var roleIds = g.Select(r => r.RoleId).ToList();
            var perms = AccessCatalog.PermissionsFor(roleIds).ToHashSet(StringComparer.Ordinal);
            if (!anyOf.Any(perms.Contains)) continue;
            if (!(perms.Contains(Permissions.DocumentsControl) || (catPerm is not null && perms.Contains(catPerm)))) continue;
            var scope = AccessCatalog.ScopeFor(roleIds);
            if (scope == RoleScope.Platform) continue; // platform admins are not on factory duty lists
            if (scope == RoleScope.Line && (lineId is null || !lines.Any(l => l.UserId == g.Key))) continue;
            result.Add(g.Key);
        }
        return result;
    }

    public async Task<int> NotifyAsync(IEnumerable<Guid> userIds, Guid? factoryId, string kind, string title, string body, string? link, string? entityType, string? entityId,
        string? dedupKey, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        var existing = dedupKey is null ? new List<Guid>()
            : await _db.UserNotifications.AsNoTracking().Where(n => ids.Contains(n.UserId) && n.DedupKey == dedupKey).Select(n => n.UserId).ToListAsync(ct);
        var added = 0;
        foreach (var id in ids.Except(existing))
        {
            _db.UserNotifications.Add(new UserNotification
            {
                UserId = id, FactoryId = factoryId, Kind = kind, Title = Clip(title, 200), Body = Clip(body, 1000), Link = link, EntityType = entityType, EntityId = entityId, DedupKey = dedupKey,
            });
            added++;
        }
        if (added == 0) return 0;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DocScope.IsUniqueViolation(ex))
        {
            // Another instance notified the same people at the same moment.
            foreach (var e in _db.ChangeTracker.Entries<UserNotification>().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
            _log.LogDebug("Duplicate notification {Key} skipped.", dedupKey);
            return 0;
        }
        return added;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// Fills looked-up values (item names and balances, worker names, machine
/// details) and runs the checks that need other records: stock available
/// for an issue, the requisition it serves, invoice ↔ packing list ↔ buyer
/// order quantities, shipment references, unknown workers and machines.
/// </summary>
public sealed class DocumentEnricher
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly BunonBrainDbContext _db;

    public DocumentEnricher(BunonBrainDbContext db) { _db = db; }

    public async Task<Dictionary<string, decimal>> BalancesAsync(Guid factoryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct) =>
        (await _db.StockMovements.AsNoTracking().Where(m => m.FactoryId == factoryId && itemIds.Contains(m.ItemId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Bal = g.Sum(m => m.Kind == "RECEIPT" ? m.Quantity : -m.Quantity) }).ToListAsync(ct))
        .ToDictionary(x => x.Key.ToString(), x => x.Bal);

    public async Task EnrichAsync(FactoryDocument doc, DocumentTypeDef type, JsonObject d, DocumentChecks c, CancellationToken ct)
    {
        switch (type.Code)
        {
            case "store.requisition":
            case "store.issue":
                await StoreItemsAsync(doc, type, d, c, ct);
                break;
            case "store.grn":
            {
                var rows = DocJson.Rows(d, "items").OfType<JsonObject>().ToList();
                var codes = rows.Select(r => DocJson.Str(r["itemCode"])?.ToUpperInvariant()).Where(x => x is not null).Distinct().ToList();
                var items = await _db.InventoryItems.AsNoTracking().Where(i => i.FactoryId == doc.FactoryId && codes.Contains(i.CodeKey)).ToDictionaryAsync(i => i.CodeKey, ct);
                for (var i = 0; i < rows.Count; i++)
                    if (DocJson.Str(rows[i]["itemCode"])?.ToUpperInvariant() is { } code && items.TryGetValue(code, out var it))
                    {
                        if (DocJson.Str(rows[i]["unit"]) is { } unit && !string.Equals(unit, it.Unit, StringComparison.OrdinalIgnoreCase))
                            c.Error($"items[{i}].unit", "unit_mismatch", $"Row {i + 1}: {it.Code} is kept in {it.Unit}, not {unit}.");
                        if (rows[i]["category"] is null) rows[i]["category"] = it.Category;
                    }
                await GrnRollsAsync(doc, d, c, ct);
                break;
            }
            case "hr.leave":
            {
                var w = await WorkerAsync(doc.FactoryId, DocJson.Str(d["workerCode"]), ct);
                if (DocJson.Str(d["workerCode"]) is not null && w is null) c.Error("workerCode", "unknown_worker", "No worker with this Worker ID in this factory.");
                d["workerName"] = w?.FullName; d["line"] = w?.LineId;
                if (w is not null && DocJson.Date(d["fromDate"]) is { } f && DocJson.Date(d["toDate"]) is { } t)
                {
                    var candidates = await (from x in _db.FactoryDocuments.AsNoTracking()
                                            join v in _db.FactoryDocumentVersions.AsNoTracking() on new { Id = x.Id, N = x.CurrentVersion } equals new { Id = v.DocumentId, N = v.Number }
                                            where x.FactoryId == doc.FactoryId && x.TypeCode == "hr.leave" && x.Id != doc.Id && x.Status != DocumentStatuses.Archived
                                                  && x.Status != DocumentStatuses.Rejected && x.PeriodFrom <= t && x.PeriodTo >= f
                                            select new { x.Reference, v.DataJson }).ToListAsync(ct);
                    var overlap = candidates.FirstOrDefault(x => string.Equals(DocJson.Str(DocJson.Parse(x.DataJson)["workerCode"]), w.WorkerCode, StringComparison.OrdinalIgnoreCase));
                    if (overlap is not null) c.Error("fromDate", "overlapping_leave", $"This worker already has leave in these dates ({overlap.Reference}).");
                    d["workerCode"] = w.WorkerCode;
                }
                break;
            }
            case "hr.employee":
            {
                var code = DocJson.Str(d["workerCode"]);
                var w = await WorkerAsync(doc.FactoryId, code, ct);
                if (code is not null && w is null) { c.Error("workerCode", "unknown_worker", "No worker with this Worker ID in this factory."); break; }
                if (w is null) break;
                var dept = w.DepartmentId is null ? null : await _db.Departments.AsNoTracking().Where(x => x.Id == w.DepartmentId).Select(x => x.Name).FirstOrDefaultAsync(ct);
                d["fullName"] = w.FullName; d["employeeCode"] = w.EmployeeCode; d["designation"] = w.Designation; d["grade"] = w.Grade; d["department"] = dept;
                d["line"] = w.LineId; d["joinedOn"] = w.JoinedOn?.ToString("yyyy-MM-dd", Inv); d["employmentType"] = w.EmploymentType; d["status"] = w.Status;
                foreach (var k in d.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) d.Remove(k);
                if (w.JoinedOn is null) c.Warn("joinedOn", "not_recorded", "The joining date is not recorded in the worker directory.");
                if (w.Designation is null) c.Warn("designation", "not_recorded", "The designation is not recorded in the worker directory.");
                break;
            }
            case "compliance.training":
            {
                var rows = DocJson.Rows(d, "attendees").OfType<JsonObject>().ToList();
                var codes = rows.Select(r => DocJson.Str(r["workerCode"])?.ToUpperInvariant()).Where(x => x is not null).Distinct().ToList();
                var workers = await _db.Workers.AsNoTracking().Where(w => w.FactoryId == doc.FactoryId && codes.Contains(w.WorkerCode.ToUpper())).ToListAsync(ct);
                for (var i = 0; i < rows.Count; i++)
                {
                    var code = DocJson.Str(rows[i]["workerCode"]);
                    if (code is null) continue;
                    var w = workers.FirstOrDefault(x => string.Equals(x.WorkerCode, code, StringComparison.OrdinalIgnoreCase));
                    if (w is null) { rows[i].Remove("name"); c.Error($"attendees[{i}].workerCode", "unknown_worker", $"Row {i + 1}: no worker {code} in this factory."); }
                    else { rows[i]["name"] = w.FullName; rows[i]["workerCode"] = w.WorkerCode; }
                }
                break;
            }
            case "devices.maintenance":
            {
                var code = DocJson.Str(d["machineCode"])?.ToUpperInvariant();
                var m = code is null ? null : await _db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.FactoryId == doc.FactoryId && x.Code == code, ct);
                if (code is not null && m is null) { c.Error("machineCode", "unknown_machine", "No machine with this asset tag in this factory."); break; }
                if (m is null) break;
                d["machineCode"] = m.Code;
                if (m.MachineType is not null) d["machineType"] = m.MachineType; else d.Remove("machineType");
                if (m.Brand is not null) d["brand"] = m.Brand; else d.Remove("brand");
                if (m.SerialNumber is not null) d["serialNumber"] = m.SerialNumber; else d.Remove("serialNumber");
                break;
            }
            case "commercial.invoice":
            {
                var po = DocJson.Str(d["poNumber"]);
                var order = po is null ? null : await _db.MerchOrders.AsNoTracking().FirstOrDefaultAsync(o => o.BuyerOrderNumber == po, ct);
                if (po is not null && order is null) c.Warn("poNumber", "po_not_found", "No buyer order with this PO number in merchandising: quantities can't be checked.");
                if (order is not null)
                {
                    if (DocJson.Dec(d["totalQuantity"]) is { } q && order.Quantity is { } oq && q > oq)
                        c.Warn("totalQuantity", "over_po_quantity", $"Invoiced quantity ({q}) is more than the PO quantity ({oq}).");
                    if (order.UnitPrice is { } up)
                        foreach (var (row, i) in DocJson.Rows(d, "lines").OfType<JsonObject>().Select((r, i) => (r, i)))
                            if (DocJson.Dec(row["unitPrice"]) is { } p && p != up)
                                c.Warn($"lines[{i}].unitPrice", "price_differs_from_po", $"Line {i + 1}: unit price {p} differs from the PO price {up}.");
                    if (order.Currency is { } cur && DocJson.Str(d["currency"]) is { } dc && !string.Equals(cur, dc, StringComparison.OrdinalIgnoreCase))
                        c.Warn("currency", "currency_differs_from_po", $"The PO is in {cur}.");
                }
                var no = DocJson.Str(d["invoiceNo"]);
                if (no is not null)
                {
                    var pls = await LiveByFieldAsync(doc, "commercial.packing_list", "invoiceNo", no, ct);
                    var pieces = pls.Sum(p => DocJson.Dec(p.Data["totalPieces"]) ?? 0);
                    if (pls.Count > 0 && DocJson.Dec(d["totalQuantity"]) is { } tq && tq != pieces)
                        c.Warn("totalQuantity", "packing_list_mismatch", $"The packing list(s) for this invoice hold {pieces} pieces; the invoice says {tq}.");
                }
                break;
            }
            case "commercial.packing_list":
            {
                var no = DocJson.Str(d["invoiceNo"]);
                if (no is null) break;
                var inv = await LiveByFieldAsync(doc, "commercial.invoice", "invoiceNo", no, ct);
                if (inv.Count == 0) c.Warn("invoiceNo", "invoice_not_found", "No commercial invoice with this number yet: totals can't be checked.");
                else if (DocJson.Dec(inv[0].Data["totalQuantity"]) is { } iq && DocJson.Dec(d["totalPieces"]) is { } tp && iq != tp)
                    c.Warn("totalPieces", "invoice_mismatch", $"Invoice {inv[0].Reference} is for {iq} pieces; this packing list holds {tp}.");
                break;
            }
            case "commercial.shipment":
            {
                foreach (var (field, typeCode, label) in new[] { ("invoiceNo", "commercial.invoice", "commercial invoice"), ("packingListNo", "commercial.packing_list", "packing list") })
                {
                    var no = DocJson.Str(d[field]);
                    if (no is null) continue;
                    var docs = await LiveByFieldAsync(doc, typeCode, field == "packingListNo" ? "packingListNo" : "invoiceNo", no, ct);
                    if (docs.Count == 0) c.Error(field, "reference_not_found", $"No {label} {no} exists in this factory.");
                    else if (docs.All(x => x.Status != DocumentStatuses.Approved)) c.Warn(field, "reference_not_approved", $"The {label} {docs[0].Reference} is not approved yet.");
                }
                break;
            }
            case "qc.aql":
            {
                var po = DocJson.Str(d["orderReference"]);
                if (po is not null && DocJson.Dec(d["lotSize"]) is { } lot && await _db.MerchOrders.AsNoTracking().FirstOrDefaultAsync(o => o.BuyerOrderNumber == po, ct) is { Quantity: { } oq } && lot > oq)
                    c.Warn("lotSize", "lot_exceeds_order", $"The lot ({lot}) is larger than the order quantity ({oq}).");
                break;
            }
        }
    }

    private async Task StoreItemsAsync(FactoryDocument doc, DocumentTypeDef type, JsonObject d, DocumentChecks c, CancellationToken ct)
    {
        var rows = DocJson.Rows(d, "items").OfType<JsonObject>().ToList();
        var codes = rows.Select(r => DocJson.Str(r["itemCode"])?.ToUpperInvariant()).Where(x => x is not null).Distinct().ToList();
        var items = await _db.InventoryItems.AsNoTracking().Where(i => i.FactoryId == doc.FactoryId && codes.Contains(i.CodeKey)).ToDictionaryAsync(i => i.CodeKey, ct);
        var balances = await BalancesAsync(doc.FactoryId, items.Values.Select(i => i.Id).ToList(), ct);
        var requested = new Dictionary<string, decimal>();
        if (type.Code == "store.issue" && DocJson.Str(d["requisitionReference"]) is { } reqRef)
        {
            var req = await _db.FactoryDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.FactoryId == doc.FactoryId && x.Reference == reqRef.ToUpperInvariant(), ct);
            if (req is null || req.TypeCode != "store.requisition") c.Error("requisitionReference", "requisition_not_found", $"No material requisition {reqRef} in this factory.");
            else
            {
                if (req.Status != DocumentStatuses.Approved) c.Warn("requisitionReference", "requisition_not_approved", $"Requisition {req.Reference} is {req.Status.ToLowerInvariant().Replace('_', ' ')}, not approved.");
                var reqData = DocJson.Parse(await _db.FactoryDocumentVersions.AsNoTracking().Where(v => v.DocumentId == req.Id && v.Number == req.CurrentVersion).Select(v => v.DataJson).FirstAsync(ct));
                foreach (var r in DocJson.Rows(reqData, "items").OfType<JsonObject>())
                    if (DocJson.Str(r["itemCode"])?.ToUpperInvariant() is { } code) requested[code] = requested.GetValueOrDefault(code) + (DocJson.Dec(r["quantity"]) ?? 0);
                // What earlier approved issues already gave against this requisition.
                var issued = await (from x in _db.FactoryDocuments.AsNoTracking()
                                    join m in _db.StockMovements.AsNoTracking() on x.Id equals m.DocumentId
                                    join it in _db.InventoryItems.AsNoTracking() on m.ItemId equals it.Id
                                    where x.FactoryId == doc.FactoryId && x.TypeCode == "store.issue" && x.Id != doc.Id && m.Kind == "ISSUE"
                                    select new { x.Id, it.CodeKey, m.Quantity }).ToListAsync(ct);
                var issueIds = issued.Select(i => i.Id).Distinct().ToList();
                var refs = await _db.FactoryDocumentVersions.AsNoTracking().Where(v => issueIds.Contains(v.DocumentId)).Select(v => new { v.DocumentId, v.DataJson }).ToListAsync(ct);
                var sameReq = refs.Where(r => string.Equals(DocJson.Str(DocJson.Parse(r.DataJson)["requisitionReference"]), req.Reference, StringComparison.OrdinalIgnoreCase)).Select(r => r.DocumentId).ToHashSet();
                foreach (var i in issued.Where(i => sameReq.Contains(i.Id))) requested[i.CodeKey] = requested.GetValueOrDefault(i.CodeKey) - i.Quantity;
            }
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var code = DocJson.Str(rows[i]["itemCode"])?.ToUpperInvariant();
            if (code is null) continue;
            if (!items.TryGetValue(code, out var it))
            {
                rows[i].Remove("itemName"); rows[i].Remove("unit"); rows[i].Remove("balance");
                c.Error($"items[{i}].itemCode", "unknown_item", $"Row {i + 1}: no store item {code} (items are created by approved GRNs or by the store officer).");
                continue;
            }
            var bal = balances.GetValueOrDefault(it.Id.ToString());
            rows[i]["itemCode"] = it.Code; rows[i]["itemName"] = it.Name; rows[i]["unit"] = it.Unit; rows[i]["balance"] = bal;
            var qty = DocJson.Dec(rows[i]["quantity"]);
            if (type.Code == "store.issue" && qty is { } q && q > bal)
                c.Error($"items[{i}].quantity", "insufficient_stock", $"Row {i + 1}: only {bal} {it.Unit} of {it.Code} in stock.");
            if (type.Code == "store.requisition" && qty is { } rq && rq > bal)
                c.Warn($"items[{i}].quantity", "exceeds_stock", $"Row {i + 1}: {rq} requested, {bal} {it.Unit} in stock.");
            if (type.Code == "store.issue" && requested.Count > 0 && qty is { } iq)
            {
                if (!requested.TryGetValue(code, out var left)) c.Warn($"items[{i}].itemCode", "not_requested", $"Row {i + 1}: {it.Code} is not on the requisition.");
                else if (iq > left) c.Warn($"items[{i}].quantity", "exceeds_requisition", $"Row {i + 1}: {iq} issued, {Math.Max(0, left)} left on the requisition.");
            }
        }
        if (type.Code == "store.issue") await IssueRollsAsync(doc, d, rows, items, balances, c, ct);
    }

    /// <summary>What is left on each roll: received minus issued (roll movements).</summary>
    public async Task<Dictionary<Guid, decimal>> RollRemainingAsync(Guid factoryId, IReadOnlyCollection<Guid> rollIds, CancellationToken ct) =>
        await _db.InventoryRollMovements.AsNoTracking().Where(m => m.FactoryId == factoryId && rollIds.Contains(m.RollId))
            .GroupBy(m => m.RollId).Select(g => new { g.Key, Left = g.Sum(m => m.Kind == "RECEIPT" ? m.Quantity : -m.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Left, ct);

    /// <summary>Per item: the quantity held on rolls (sum of what is left on its rolls).</summary>
    public async Task<Dictionary<Guid, decimal>> RolledStockAsync(Guid factoryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct) =>
        await (from m in _db.InventoryRollMovements.AsNoTracking()
               join r in _db.InventoryRolls.AsNoTracking() on m.RollId equals r.Id
               where m.FactoryId == factoryId && itemIds.Contains(r.ItemId)
               group m by r.ItemId into g
               select new { g.Key, Left = g.Sum(m => m.Kind == "RECEIPT" ? m.Quantity : -m.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Left, ct);

    public static string SupplierKey(string? supplier) => (supplier ?? "").Trim().ToLowerInvariant();

    /// <summary>A GRN may not receive a roll the store already has (same supplier, lot and roll number).</summary>
    private async Task GrnRollsAsync(FactoryDocument doc, JsonObject d, DocumentChecks c, CancellationToken ct)
    {
        var rolls = DocJson.Rows(d, "rolls").OfType<JsonObject>().ToList();
        if (rolls.Count == 0) return;
        var supplier = SupplierKey(DocJson.Str(d["supplier"]));
        var numbers = rolls.Select(r => DocJson.Str(r["rollNumber"])?.ToUpperInvariant()).Where(x => x is not null).Distinct().ToList();
        var existing = await _db.InventoryRolls.AsNoTracking()
            .Where(r => r.FactoryId == doc.FactoryId && r.SupplierKey == supplier && numbers.Contains(r.RollKey) && r.GrnDocumentId != doc.Id).ToListAsync(ct);
        for (var i = 0; i < rolls.Count; i++)
        {
            var key = DocJson.Str(rolls[i]["rollNumber"])?.ToUpperInvariant();
            var lot = (DocJson.Str(rolls[i]["lotNumber"]) ?? "").ToUpperInvariant();
            if (existing.FirstOrDefault(r => r.RollKey == key && r.LotKey == lot) is { } dup)
                c.Error($"rolls[{i}].rollNumber", "duplicate_roll",
                    $"Roll row {i + 1}: roll {dup.RollNumber}{(dup.LotNumber is null ? "" : $" (lot {dup.LotNumber})")} from this supplier is already in the store ({dup.GrnReference}).");
        }
    }

    /// <summary>
    /// Issue note rolls: each roll must exist (lot given when the roll number alone is ambiguous), belong to an item on the
    /// note and hold enough; the rolls of an item can't add up to more than its line, and what is issued without rolls must
    /// come from stock that is not on rolls (an item stocked by roll is issued roll by roll).
    /// </summary>
    private async Task IssueRollsAsync(FactoryDocument doc, JsonObject d, List<JsonObject> itemRows, Dictionary<string, InventoryItem> items,
        Dictionary<string, decimal> balances, DocumentChecks c, CancellationToken ct)
    {
        var rolls = DocJson.Rows(d, "rolls").OfType<JsonObject>().ToList();
        var numbers = rolls.Select(r => DocJson.Str(r["rollNumber"])?.ToUpperInvariant()).Where(x => x is not null).Distinct().ToList();
        var candidates = numbers.Count == 0 ? new List<InventoryRoll>()
            : await _db.InventoryRolls.AsNoTracking().Where(r => r.FactoryId == doc.FactoryId && numbers.Contains(r.RollKey)).ToListAsync(ct);
        var remaining = await RollRemainingAsync(doc.FactoryId, candidates.Select(r => r.Id).ToList(), ct);
        var itemById = items.Values.ToDictionary(i => i.Id);
        var otherItemIds = candidates.Select(r => r.ItemId).Where(id => !itemById.ContainsKey(id)).Distinct().ToList();
        var otherItems = otherItemIds.Count == 0 ? new Dictionary<Guid, InventoryItem>()
            : await _db.InventoryItems.AsNoTracking().Where(i => otherItemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var rolledPerItem = new Dictionary<Guid, decimal>();
        for (var i = 0; i < rolls.Count; i++)
        {
            var row = rolls[i];
            var key = DocJson.Str(row["rollNumber"])?.ToUpperInvariant();
            if (key is null) continue;
            var lot = DocJson.Str(row["lotNumber"])?.ToUpperInvariant();
            var matches = candidates.Where(r => r.RollKey == key && (lot is null || r.LotKey == lot)).ToList();
            row.Remove("itemCode"); row.Remove("color"); row.Remove("remaining");
            if (matches.Count == 0)
            {
                c.Error($"rolls[{i}].rollNumber", "unknown_roll", $"Roll row {i + 1}: no roll {key}{(lot is null ? "" : $" in lot {lot}")} in this store.");
                continue;
            }
            if (matches.Count > 1)
            {
                c.Error($"rolls[{i}].lotNumber", "ambiguous_roll", $"Roll row {i + 1}: {matches.Count} rolls are numbered {key}; give the lot number.");
                continue;
            }
            var roll = matches[0];
            var item = itemById.GetValueOrDefault(roll.ItemId) ?? otherItems.GetValueOrDefault(roll.ItemId);
            var left = remaining.GetValueOrDefault(roll.Id);
            row["rollNumber"] = roll.RollNumber;
            if (roll.LotNumber is not null) row["lotNumber"] = roll.LotNumber;
            if (item is not null) row["itemCode"] = item.Code;
            if (roll.Color is not null) row["color"] = roll.Color;
            row["remaining"] = DocJson.Num(left);
            if (!itemById.ContainsKey(roll.ItemId))
            {
                c.Error($"rolls[{i}].rollNumber", "roll_item_not_listed", $"Roll row {i + 1}: roll {roll.RollNumber} is {item?.Code ?? "another item"}, which is not on this note.");
                continue;
            }
            var qty = DocJson.Dec(row["quantity"]);
            if (left <= 0) c.Error($"rolls[{i}].rollNumber", "roll_used_up", $"Roll row {i + 1}: roll {roll.RollNumber} was already issued completely.");
            else if (qty is { } q && q > left) c.Error($"rolls[{i}].quantity", "roll_insufficient", $"Roll row {i + 1}: only {left} {roll.Unit} left on roll {roll.RollNumber}.");
            if (qty is { } qq) rolledPerItem[roll.ItemId] = rolledPerItem.GetValueOrDefault(roll.ItemId) + qq;
        }
        if (items.Count == 0) return;
        var onRolls = await RolledStockAsync(doc.FactoryId, items.Values.Select(i => i.Id).ToList(), ct);
        for (var i = 0; i < itemRows.Count; i++)
        {
            if (DocJson.Str(itemRows[i]["itemCode"])?.ToUpperInvariant() is not { } code || !items.TryGetValue(code, out var it)) continue;
            var lineQty = DocJson.Dec(itemRows[i]["quantity"]) ?? 0;
            var rolled = rolledPerItem.GetValueOrDefault(it.Id);
            if (rolled > lineQty)
            {
                c.Error($"items[{i}].quantity", "rolls_exceed_line", $"Row {i + 1}: the rolls listed for {it.Code} add up to {rolled}, more than the {lineQty} issued.");
                continue;
            }
            var loose = balances.GetValueOrDefault(it.Id.ToString()) - onRolls.GetValueOrDefault(it.Id);
            if (lineQty - rolled > Math.Max(0, loose))
                c.Error($"items[{i}].quantity", "rolls_required",
                    $"Row {i + 1}: {it.Code} is stocked by roll; list the rolls issued ({lineQty - rolled} {it.Unit} not on a roll row{(loose > 0 ? $", only {loose} is loose stock" : "")}).");
        }
    }

    private async Task<WorkerProfile?> WorkerAsync(Guid factoryId, string? code, CancellationToken ct)
    {
        if (code is null) return null;
        var upper = code.Trim().ToUpperInvariant();
        return await _db.Workers.AsNoTracking().FirstOrDefaultAsync(w => w.FactoryId == factoryId && w.WorkerCode.ToUpper() == upper, ct);
    }

    internal sealed record LiveDoc(Guid Id, string Reference, string Status, JsonObject Data);

    /// <summary>Live (not rejected / archived) documents of a type in the same factory whose field equals a value.</summary>
    private async Task<List<LiveDoc>> LiveByFieldAsync(FactoryDocument self, string typeCode, string field, string value, CancellationToken ct)
    {
        var docs = await (from x in _db.FactoryDocuments.AsNoTracking()
                          join v in _db.FactoryDocumentVersions.AsNoTracking() on new { Id = x.Id, N = x.CurrentVersion } equals new { Id = v.DocumentId, N = v.Number }
                          where x.FactoryId == self.FactoryId && x.TypeCode == typeCode && x.Id != self.Id && x.Status != DocumentStatuses.Archived && x.Status != DocumentStatuses.Rejected
                          select new { x.Id, x.Reference, x.Status, v.DataJson }).Take(200).ToListAsync(ct);
        return docs.Select(x => new LiveDoc(x.Id, x.Reference, x.Status, DocJson.Parse(x.DataJson)))
            .Where(x => string.Equals(DocJson.Str(x.Data[field]), value, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}

/// <summary>Turns a document into the render model shared by the PDF, XLSX and DOCX writers.</summary>
public static class DocumentRender
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A4 PDF with embedded Noto Sans / Noto Sans Bengali and HarfBuzz shaping: Bangla, English and mixed text print as written.</summary>
    public static byte[] Pdf(RenderModel m)
    {
        using var w = new ShapedPdfWriter(m.Title, $"{m.Title} - {m.Subtitle}");
        w.Text(m.Title, 16, true);
        w.Text(m.Subtitle, 9, muted: true);
        w.Gap();
        w.Table(new[] { "", "" }, m.Facts.Select(f => (IReadOnlyList<string>)new[] { f.Label, f.Value }).ToList(), 8);
        if (m.Fields.Count > 0)
        {
            w.Rule();
            w.Text("Details", 12, true);
            w.Table(new[] { "Field", "Value" }, m.Fields.Select(f => (IReadOnlyList<string>)new[] { f.Label, f.Value }).ToList(), 8);
        }
        foreach (var t in m.Tables)
        {
            w.Rule();
            w.Text(t.Title, 11, true);
            if (t.Rows.Count == 0) w.Text("No rows.", 8);
            else w.Table(t.Headers, t.Rows);
        }
        if (m.Notes.Count > 0)
        {
            w.Rule();
            w.Text("Notes", 11, true);
            foreach (var n in m.Notes) w.Text("- " + n, 8);
        }
        w.Gap();
        w.Text(m.Footer, 7);
        return w.Build();
    }

    public static string Value(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString("0.####", Inv),
        JsonValue v when v.TryGetValue<double>(out var dbl) => dbl.ToString("0.####", Inv),
        _ => DocJson.Str(n) ?? "",
    };

    public static RenderModel Build(FactoryDocument doc, DocumentTypeDef type, string factoryName, string? lineName, FactoryDocumentVersion version, JsonObject data, DocumentChecks checks)
    {
        string Ts(DateTimeOffset? t) => t is null ? "" : t.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv);
        var facts = new List<(string, string)>
        {
            ("Reference", doc.Reference), ("Factory", factoryName), ("Document type", type.Name), ("Status", doc.Status.Replace('_', ' ')),
            ("Version", $"{version.Number} ({Ts(version.CreatedAt)}, {version.CreatedByEmail})"),
        };
        if (doc.PeriodFrom is { } f) facts.Add(("Period", doc.PeriodTo is { } t && t != f ? $"{f:yyyy-MM-dd} to {t:yyyy-MM-dd}" : f.ToString("yyyy-MM-dd", Inv)));
        if (lineName is not null) facts.Add(("Line", lineName));
        facts.Add(("Prepared by", $"{doc.CreatedByEmail}, {Ts(doc.CreatedAt)}"));
        if (doc.SubmittedAt is not null) facts.Add(("Submitted by", $"{doc.SubmittedByEmail}, {Ts(doc.SubmittedAt)}"));
        if (doc.VerifiedAt is not null) facts.Add(("Verified by", $"{doc.VerifiedByEmail}, {Ts(doc.VerifiedAt)}"));
        if (doc.DecidedAt is not null) facts.Add((doc.Status == DocumentStatuses.Rejected ? "Rejected by" : "Approved by", $"{doc.DecidedByEmail}, {Ts(doc.DecidedAt)}"));
        if (doc.Status == DocumentStatuses.Approved && doc.ApprovalLevel == ApprovalLevels.None) facts.Add(("Approval", "Filed without approval (document type setting)"));
        facts.Add(("Source", doc.Source switch { "GENERATED" => "Generated from operational records", "UPLOAD" => "Uploaded document", "TEMPLATE" => "From an approved template", _ => "Form" }));

        var fields = new List<(string, string)>();
        var tables = new List<RenderTable>();
        foreach (var fd in type.Fields)
        {
            if (fd.Kind == FieldKind.Table)
            {
                var cols = fd.Columns ?? Array.Empty<FieldDef>();
                var rows = DocJson.Rows(data, fd.Key).OfType<JsonObject>()
                    .Select(r => (IReadOnlyList<string>)cols.Select(c => Value(r[c.Key])).ToList()).ToList();
                tables.Add(new RenderTable(fd.Label, cols.Select(c => c.Label).ToList(), rows));
            }
            else
            {
                var v = Value(data[fd.Key]);
                fields.Add((fd.Label + (fd.Unit is null ? "" : $" ({fd.Unit})"), v.Length == 0 ? (fd.Required ? "[MISSING]" : "") : v));
            }
        }
        var notes = new List<string>();
        notes.AddRange(checks.Missing.Select(m => "MISSING: " + m.Message));
        notes.AddRange(checks.Errors.Select(e => "ERROR: " + e.Message));
        notes.AddRange(checks.Warnings.Select(w => "Note: " + w.Message));
        if (doc.DecisionNote is { } dn) notes.Add((doc.Status == DocumentStatuses.Rejected ? "Rejection reason: " : "Decision note: ") + dn);
        return new RenderModel($"{type.Name}", $"{factoryName} · {doc.Reference}", facts, fields, tables, notes,
            $"Generated by BunonBrain {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · content SHA-256 {version.ContentSha256[..16]}");
    }
}
