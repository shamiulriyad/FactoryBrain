using System.Globalization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Documents;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BunonBrain.Infrastructure.Documents;

/// <summary>
/// Store items and the stock ledger. Items are created by the store officer
/// or by the first approved GRN that names them; stock only moves through
/// approved GRN and material issue documents (append-only movements), so
/// every balance traces back to an approved document.
/// </summary>
public sealed class InventoryService : IInventoryService
{
    private static readonly string[] Categories = { "FABRIC", "ACCESSORIES", "TRIMS", "PACKING", "CHEMICALS", "OTHER" };
    private readonly BunonBrainDbContext _db;

    public InventoryService(BunonBrainDbContext db) { _db = db; }

    private async Task<Factory?> FactoryAsync(AccessContext c, Guid? factoryId, CancellationToken ct)
    {
        if (factoryId is { } id) return c.CanAccessFactory(id) ? await _db.Factories.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct) : null;
        var mine = c.FactoryIds.ToList();
        var q = _db.Factories.AsNoTracking().Where(f => f.IsActive);
        if (!c.IsPlatform) q = q.Where(f => mine.Contains(f.Id));
        return await q.OrderBy(f => f.Code).FirstOrDefaultAsync(ct);
    }

    private async Task<List<InventoryItemDto>> DtosAsync(IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        var sums = await _db.StockMovements.AsNoTracking().Where(m => ids.Contains(m.ItemId))
            .GroupBy(m => new { m.ItemId, m.Kind }).Select(g => new { g.Key.ItemId, g.Key.Kind, Qty = g.Sum(m => m.Quantity) }).ToListAsync(ct);
        var rolls = await RollBalancesAsync(_db.InventoryRolls.AsNoTracking().Where(r => ids.Contains(r.ItemId)), ct);
        return items.Select(i =>
        {
            var rec = sums.Where(s => s.ItemId == i.Id && s.Kind == "RECEIPT").Sum(s => s.Qty);
            var iss = sums.Where(s => s.ItemId == i.Id && s.Kind == "ISSUE").Sum(s => s.Qty);
            var mine = rolls.Where(r => r.Roll.ItemId == i.Id && r.Remaining > 0).ToList();
            return new InventoryItemDto(i.Id, i.FactoryId, i.Code, i.Name, i.Category, i.Unit, i.ReorderLevel, i.IsActive, rec, iss, rec - iss,
                i.ReorderLevel is { } r && rec - iss <= r, i.UpdatedAt, mine.Count, mine.Sum(x => x.Remaining));
        }).ToList();
    }

    public async Task<AccessResult<IReadOnlyList<InventoryItemDto>>> ItemsAsync(AccessContext caller, Guid? factoryId, string? q, string? category, bool includeInactive, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<IReadOnlyList<InventoryItemDto>>.NotFound("factory_not_found", "No factory in your scope.");
        var query = _db.InventoryItems.AsNoTracking().Where(i => i.FactoryId == factory.Id);
        if (!includeInactive) query = query.Where(i => i.IsActive);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(i => i.Category == category);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(i => EF.Functions.ILike(i.Code, like) || EF.Functions.ILike(i.Name, like));
        }
        var items = await query.OrderBy(i => i.CodeKey).Take(1000).ToListAsync(ct);
        return AccessResult<IReadOnlyList<InventoryItemDto>>.Ok(await DtosAsync(items, ct));
    }

    private static AccessResult<InventoryItemDto>? Check(InventoryItemWrite b)
    {
        if (string.IsNullOrWhiteSpace(b.Code) || b.Code.Trim().Length > 40) return AccessResult<InventoryItemDto>.Invalid("invalid_code", "Item code is required (at most 40 characters).");
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 200) return AccessResult<InventoryItemDto>.Invalid("invalid_name", "Item name is required (at most 200 characters).");
        if (!Categories.Contains(b.Category)) return AccessResult<InventoryItemDto>.Invalid("invalid_category", "Category must be one of " + string.Join(", ", Categories) + ".");
        if (string.IsNullOrWhiteSpace(b.Unit) || b.Unit.Trim().Length > 16) return AccessResult<InventoryItemDto>.Invalid("invalid_unit", "Unit is required (at most 16 characters).");
        if (b.ReorderLevel is < 0) return AccessResult<InventoryItemDto>.Invalid("invalid_reorder", "Reorder level can't be negative.");
        return null;
    }

    public async Task<AccessResult<InventoryItemDto>> CreateAsync(AccessContext caller, InventoryItemWrite body, string? traceId, CancellationToken ct)
    {
        if (Check(body) is { } bad) return bad;
        var factory = await FactoryAsync(caller, body.FactoryId, ct);
        if (factory is null) return AccessResult<InventoryItemDto>.NotFound("factory_not_found", "No factory in your scope.");
        var key = body.Code.Trim().ToUpperInvariant();
        if (await _db.InventoryItems.AnyAsync(i => i.FactoryId == factory.Id && i.CodeKey == key, ct))
            return AccessResult<InventoryItemDto>.Conflict("item_code_taken", $"Item {key} already exists.");
        var item = new InventoryItem
        {
            FactoryId = factory.Id, Code = body.Code.Trim(), CodeKey = key, Name = body.Name.Trim(), Category = body.Category, Unit = body.Unit.Trim(),
            ReorderLevel = body.ReorderLevel, IsActive = body.IsActive ?? true, CreatedByEmail = caller.Email,
        };
        _db.InventoryItems.Add(item);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "inventory.item_created", "inventory_item", item.Id.ToString(), null, item.Code, item.Name, traceId, factory.Id));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DocScope.IsUniqueViolation(ex)) { return AccessResult<InventoryItemDto>.Conflict("item_code_taken", $"Item {key} already exists."); }
        return AccessResult<InventoryItemDto>.Created((await DtosAsync(new[] { item }, ct))[0]);
    }

    public async Task<AccessResult<InventoryItemDto>> UpdateAsync(AccessContext caller, Guid id, InventoryItemWrite body, string? traceId, CancellationToken ct)
    {
        if (Check(body) is { } bad) return bad;
        var item = await _db.InventoryItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null || !caller.CanAccessFactory(item.FactoryId)) return AccessResult<InventoryItemDto>.NotFound("item_not_found", "Item not found.");
        var key = body.Code.Trim().ToUpperInvariant();
        var moved = await _db.StockMovements.AnyAsync(m => m.ItemId == id, ct);
        if (moved && (key != item.CodeKey || !string.Equals(body.Unit.Trim(), item.Unit, StringComparison.Ordinal)))
            return AccessResult<InventoryItemDto>.Conflict("item_has_movements", "The code and unit of an item with stock movements can't change.");
        var before = $"{item.Code} {item.Name} {item.Category} {item.Unit} reorder={item.ReorderLevel} active={item.IsActive}";
        item.Code = body.Code.Trim(); item.CodeKey = key; item.Name = body.Name.Trim(); item.Category = body.Category; item.Unit = body.Unit.Trim();
        item.ReorderLevel = body.ReorderLevel; item.IsActive = body.IsActive ?? item.IsActive; item.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "inventory.item_updated", "inventory_item", item.Id.ToString(), before,
            $"{item.Code} {item.Name} {item.Category} {item.Unit} reorder={item.ReorderLevel} active={item.IsActive}", "Item edited", traceId, item.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<InventoryItemDto>.Conflict("version_conflict", "Someone else changed the item; reload."); }
        catch (DbUpdateException ex) when (DocScope.IsUniqueViolation(ex)) { return AccessResult<InventoryItemDto>.Conflict("item_code_taken", $"Item {key} already exists."); }
        return AccessResult<InventoryItemDto>.Ok((await DtosAsync(new[] { item }, ct))[0]);
    }

    /// <summary>Rolls with what was received, issued and is left on each (from the roll movements).</summary>
    internal async Task<List<(InventoryRoll Roll, decimal Received, decimal Issued, decimal Remaining)>> RollBalancesAsync(IQueryable<InventoryRoll> rolls, CancellationToken ct)
    {
        var list = await rolls.ToListAsync(ct);
        if (list.Count == 0) return new();
        var ids = list.Select(r => r.Id).ToList();
        var sums = await _db.InventoryRollMovements.AsNoTracking().Where(m => ids.Contains(m.RollId))
            .GroupBy(m => new { m.RollId, m.Kind }).Select(g => new { g.Key.RollId, g.Key.Kind, Qty = g.Sum(m => m.Quantity) }).ToListAsync(ct);
        return list.Select(r =>
        {
            var rec = sums.Where(x => x.RollId == r.Id && x.Kind == "RECEIPT").Sum(x => x.Qty);
            var iss = sums.Where(x => x.RollId == r.Id && x.Kind == "ISSUE").Sum(x => x.Qty);
            return (r, rec, iss, rec - iss);
        }).ToList();
    }

    public static string RollStatus(decimal received, decimal remaining) => remaining <= 0 ? "ISSUED" : remaining < received ? "PARTLY_ISSUED" : "IN_STOCK";

    public async Task<AccessResult<DocPaged<InventoryRollDto>>> RollsAsync(AccessContext caller, Guid? factoryId, Guid? itemId, string? status, string? q, int page, int pageSize, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocPaged<InventoryRollDto>>.NotFound("factory_not_found", "No factory in your scope.");
        if (status is not (null or "" or "IN_STOCK" or "PARTLY_ISSUED" or "ISSUED" or "AVAILABLE"))
            return AccessResult<DocPaged<InventoryRollDto>>.Invalid("invalid_status", "Status must be IN_STOCK, PARTLY_ISSUED, ISSUED or AVAILABLE.");
        var query = _db.InventoryRolls.AsNoTracking().Where(r => r.FactoryId == factory.Id);
        if (itemId is { } iid) query = query.Where(r => r.ItemId == iid);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(r => EF.Functions.ILike(r.RollNumber, like) || (r.LotNumber != null && EF.Functions.ILike(r.LotNumber, like))
                                     || (r.Color != null && EF.Functions.ILike(r.Color, like)) || (r.Supplier != null && EF.Functions.ILike(r.Supplier, like)));
        }
        var all = await RollBalancesAsync(query.OrderBy(r => r.ReceivedOn).ThenBy(r => r.RollKey).Take(5000), ct);
        var filtered = all.Where(x => status switch
        {
            "AVAILABLE" => x.Remaining > 0,
            "IN_STOCK" or "PARTLY_ISSUED" or "ISSUED" => RollStatus(x.Received, x.Remaining) == status,
            _ => true,
        }).ToList();
        var itemIds = filtered.Select(x => x.Roll.ItemId).Distinct().ToList();
        var items = await _db.InventoryItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var size = Math.Clamp(pageSize, 1, 200); var p = Math.Max(1, page);
        var rows = filtered.Skip((p - 1) * size).Take(size).Select(x => new InventoryRollDto(x.Roll.Id, x.Roll.ItemId, items.GetValueOrDefault(x.Roll.ItemId)?.Code ?? "",
            items.GetValueOrDefault(x.Roll.ItemId)?.Name ?? "", x.Roll.RollNumber, x.Roll.LotNumber, x.Roll.Supplier, x.Roll.Color, x.Roll.ShadeGroup, x.Roll.WidthCm,
            x.Roll.Warehouse, x.Roll.Location, x.Roll.Unit, x.Received, x.Issued, x.Remaining, RollStatus(x.Received, x.Remaining),
            x.Roll.ReceivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.Roll.GrnDocumentId, x.Roll.GrnReference)).ToList();
        return AccessResult<DocPaged<InventoryRollDto>>.Ok(new DocPaged<InventoryRollDto>(rows, p, size, filtered.Count));
    }

    public async Task<AccessResult<DocPaged<StockMovementDto>>> MovementsAsync(AccessContext caller, Guid? factoryId, Guid? itemId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocPaged<StockMovementDto>>.NotFound("factory_not_found", "No factory in your scope.");
        var q = from m in _db.StockMovements.AsNoTracking()
                join i in _db.InventoryItems.AsNoTracking() on m.ItemId equals i.Id
                where m.FactoryId == factory.Id
                select new { m, i.Code, i.Name };
        if (itemId is { } iid) q = q.Where(x => x.m.ItemId == iid);
        if (from is { } f) q = q.Where(x => x.m.MovementDate >= f);
        if (to is { } t) q = q.Where(x => x.m.MovementDate <= t);
        var size = Math.Clamp(pageSize, 1, 200); var p = Math.Max(1, page);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.m.MovementDate).ThenByDescending(x => x.m.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return AccessResult<DocPaged<StockMovementDto>>.Ok(new DocPaged<StockMovementDto>(rows.Select(x => new StockMovementDto(x.m.Id, x.m.ItemId, x.Code, x.Name, x.m.Kind,
            x.m.Quantity, x.m.UnitCost, x.m.MovementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.m.DocumentId, x.m.DocumentReference, x.m.Counterparty,
            x.m.RecordedAt, x.m.RecordedByEmail)).ToList(), p, size, total));
    }
}

/// <summary>The signed-in user's own notifications.</summary>
public sealed class NotificationService : INotificationService
{
    private readonly BunonBrainDbContext _db;
    public NotificationService(BunonBrainDbContext db) { _db = db; }

    public async Task<NotificationList> ListAsync(AccessContext caller, bool unreadOnly, int limit, CancellationToken ct)
    {
        var q = _db.UserNotifications.AsNoTracking().Where(n => n.UserId == caller.UserId);
        var unread = await q.CountAsync(n => n.ReadAt == null, ct);
        if (unreadOnly) q = q.Where(n => n.ReadAt == null);
        var rows = await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(limit, 1, 100))
            .Select(n => new NotificationDto(n.Id, n.Kind, n.Title, n.Body, n.Link, n.EntityType, n.EntityId, n.CreatedAt, n.ReadAt)).ToListAsync(ct);
        return new NotificationList(rows, unread);
    }

    public async Task<int> MarkReadAsync(AccessContext caller, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var q = _db.UserNotifications.Where(n => n.UserId == caller.UserId && n.ReadAt == null);
        if (ids is { Count: > 0 }) { var list = ids.Take(500).ToList(); q = q.Where(n => list.Contains(n.Id)); }
        var now = DateTimeOffset.UtcNow;
        return await q.ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
    }
}

/// <summary>
/// Scheduled document work, every 30 minutes per active factory, only when <c>DOCUMENT_AUTOMATION_ENABLED=true</c>:
/// <list type="number">
///   <item>Generate the draft of each report type set to generate automatically, for its latest complete period (yesterday, the previous
///   week or month), for the factory or for each line — only when its source records exist and no document for that period and scope exists —
///   and tell the people who prepare that category. Every attempt is recorded in <c>document_schedule_runs</c> (unique per period and scope):
///   a period is generated at most once, periods without data are retried for a few days, failures are logged with their error and retried
///   up to <see cref="DocumentSchedule.MaxFailedAttempts"/> times, after which document controllers are told once.</item>
///   <item>Report expected daily documents that are missing (to preparers and monitoring operators), once per type and day.</item>
///   <item>Remind approvers of documents waiting longer than the type's decision time, once per document and day.</item>
/// </list>
/// The worker acts as <c>system:document-automation</c>; it never submits or approves anything.
/// </summary>
public sealed class DocumentAutomationWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public const string SystemEmail = "system:document-automation";
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DocumentAutomationWorker> _log;

    public DocumentAutomationWorker(IServiceScopeFactory scopes, ILogger<DocumentAutomationWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Document automation failed; retrying in {Interval}.", Interval); }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<Guid> factories;
        await using (var scope = _scopes.CreateAsyncScope())
            factories = await scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>().Factories.AsNoTracking().Where(f => f.IsActive).Select(f => f.Id).ToListAsync(ct);
        foreach (var factoryId in factories)
        {
            await using var scope = _scopes.CreateAsyncScope();
            try { await RunFactoryAsync(scope.ServiceProvider, factoryId, now, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Document automation failed for factory {FactoryId}.", factoryId); }
        }
    }

    internal static async Task RunFactoryAsync(IServiceProvider sp, Guid factoryId, DateTimeOffset now, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var generators = sp.GetRequiredService<DocumentGenerators>();
        var notifier = sp.GetRequiredService<DocumentNotifier>();
        var enricher = sp.GetRequiredService<DocumentEnricher>();
        var clock = await FactoryClock.LoadAsync(db, factoryId, ct);
        var yesterday = clock.Today(now).AddDays(-1);
        var settings = await db.DocumentTypeSettings.AsNoTracking().Where(s => s.FactoryId == factoryId).ToDictionaryAsync(s => s.TypeCode, ct);
        var actor = new BunonBrain.Application.Abstractions.AuditActor(null, SystemEmail, "System");

        // 1. Scheduled drafts (daily / weekly / monthly, factory or each line).
        await ScheduledDraftsAsync(db, generators, notifier, enricher, clock, factoryId, settings, now, actor, ct);

        // 2. Missing expected documents and 3. overdue decisions.
        var board = await DocumentMonitor.BuildAsync(db, factoryId, now, ct);
        foreach (var m in board.Items.Where(i => i.Kind == "MISSING"))
        {
            var users = (await notifier.ResponsibleAsync(factoryId, m.Category, null, new[] { Permissions.DocumentsCreate }, ct))
                .Concat(await MonitorsAsync(db, factoryId, ct)).Distinct();
            await notifier.NotifyAsync(users, factoryId, "document.missing", $"Missing: {m.TypeName} {m.Period}", m.Message, "/app/documents/monitor",
                "document_type", m.TypeCode, $"missing:{factoryId}:{m.TypeCode}:{m.Period}", ct);
        }
        foreach (var o in board.Items.Where(i => i.Kind == "OVERDUE_DECISION" && i.DocumentId is not null))
        {
            var doc = await db.FactoryDocuments.AsNoTracking().FirstAsync(d => d.Id == o.DocumentId, ct);
            var anyOf = doc.ApprovalLevel == ApprovalLevels.ReviewAndApprove && doc.VerifiedAt is null ? new[] { Permissions.DocumentsReview }
                : doc.Risk == DocumentRisks.High ? new[] { Permissions.DocumentsApproveHigh } : new[] { Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh };
            var users = (await notifier.ResponsibleAsync(factoryId, doc.Category, doc.LineId, anyOf, ct)).Where(u => u != doc.CreatedByUserId && u != doc.SubmittedByUserId);
            await notifier.NotifyAsync(users, factoryId, "document.overdue", $"{doc.Reference} is waiting for a decision", o.Message, $"/app/documents/{doc.Id}",
                "factory_document", doc.Id.ToString(), $"overdue:{doc.Id}:{clock.Today(now):yyyyMMdd}", ct);
        }
    }

    /// <summary>One schedule target: a type, its latest complete period and a scope (null line = factory).</summary>
    internal sealed record ScheduleTarget(DocumentTypeDef Type, DateOnly From, DateOnly To, string? LineId, string? LineName);

    /// <summary>What is due now for a factory: every auto-generated type's latest complete period, for the factory or each active line.</summary>
    internal static async Task<List<ScheduleTarget>> DueAsync(BunonBrainDbContext db, Guid factoryId, DateOnly today, IReadOnlyDictionary<string, DocumentTypeSetting> settings, CancellationToken ct)
    {
        var lines = await db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factoryId && l.IsActive).OrderBy(l => l.Id).Select(l => new { l.Id, l.Name }).ToListAsync(ct);
        var due = new List<ScheduleTarget>();
        foreach (var type in DocumentCatalog.All.Where(t => t.Generated && DocumentSchedule.Allowed(t).Count > 0))
        {
            var s = settings.GetValueOrDefault(type.Code);
            if (!(s?.AutoGenerate ?? type.DefaultAutoGenerate)) continue;
            var (from, to) = DocumentSchedule.LatestCompletePeriod(DocumentSchedule.Frequency(type, s?.ScheduleFrequency), today, s?.WeekStartDay ?? 6);
            if (DocumentSchedule.Scope(type, s?.ScheduleScope) == ScheduleScopes.EachLine)
                due.AddRange(lines.Select(l => new ScheduleTarget(type, from, to, l.Id, l.Name)));
            else due.Add(new ScheduleTarget(type, from, to, null, null));
        }
        return due;
    }

    internal static async Task ScheduledDraftsAsync(BunonBrainDbContext db, DocumentGenerators generators, DocumentNotifier notifier, DocumentEnricher enricher, FactoryClock clock,
        Guid factoryId, IReadOnlyDictionary<string, DocumentTypeSetting> settings, DateTimeOffset now, BunonBrain.Application.Abstractions.AuditActor actor, CancellationToken ct)
    {
        var today = clock.Today(now);
        foreach (var target in await DueAsync(db, factoryId, today, settings, ct))
        {
            ct.ThrowIfCancellationRequested();
            await RunTargetAsync(db, generators, notifier, enricher, clock, factoryId, settings, target, now, actor, ct);
        }
    }

    /// <summary>Generate one period's document if it is due, recording the outcome. Never throws for a generation failure: it is logged on the run.</summary>
    internal static async Task<DocumentScheduleRun?> RunTargetAsync(BunonBrainDbContext db, DocumentGenerators generators, DocumentNotifier notifier, DocumentEnricher enricher,
        FactoryClock clock, Guid factoryId, IReadOnlyDictionary<string, DocumentTypeSetting> settings, ScheduleTarget target, DateTimeOffset now,
        BunonBrain.Application.Abstractions.AuditActor actor, CancellationToken ct)
    {
        var (type, from, to, lineId, lineName) = target;
        var lineKey = lineId ?? "*";
        var today = clock.Today(now);
        db.ChangeTracker.Clear();
        var run = await db.DocumentScheduleRuns.FirstOrDefaultAsync(r => r.FactoryId == factoryId && r.TypeCode == type.Code && r.PeriodFrom == from && r.PeriodTo == to && r.LineKey == lineKey, ct);
        if (run is { Status: ScheduleRunStatuses.Created or ScheduleRunStatuses.Exists }) return run;
        if (run is { Status: ScheduleRunStatuses.Failed } && run.Attempts >= DocumentSchedule.MaxFailedAttempts) return run;
        if (run is { Status: ScheduleRunStatuses.NoData } && today.DayNumber - to.DayNumber > DocumentSchedule.NoDataRetryDays) return run;
        if (run is null)
        {
            run = new DocumentScheduleRun { FactoryId = factoryId, TypeCode = type.Code, PeriodFrom = from, PeriodTo = to, LineKey = lineKey, Status = ScheduleRunStatuses.NoData };
            db.DocumentScheduleRuns.Add(run);
        }
        run.Attempts++;
        run.LastAttemptAt = now;

        // Someone already made this period's document (by hand, or another instance): nothing to generate.
        var key = DocumentRules.DedupKey(type, new System.Text.Json.Nodes.JsonObject(), from, to, lineId);
        var existing = key is null ? null : await db.FactoryDocuments.AsNoTracking().Where(d => d.FactoryId == factoryId && d.DedupKey == key && d.Status != DocumentStatuses.Archived)
            .Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            run.Status = ScheduleRunStatuses.Exists; run.DocumentId = existing; run.ErrorCode = null; run.ErrorMessage = null;
            return await SaveRunAsync(db, run, ct);
        }

        FactoryDocument? doc = null;
        DocumentChecks? checks = null;
        try
        {
            var g = await generators.GenerateAsync(type, factoryId, from, to, lineId, null, ct);
            if (!g.HasSourceData)
            {
                run.Status = ScheduleRunStatuses.NoData; run.ErrorCode = null; run.ErrorMessage = "No source records for the period yet.";
                return await SaveRunAsync(db, run, ct);
            }
            var data = DocumentRules.Sanitize(type, g.Data, keepComputed: true);
            var sources = new System.Text.Json.Nodes.JsonObject();
            foreach (var f in type.Fields.Where(f => f.Computed)) sources[f.Key] = "records";
            sources["$gaps"] = new System.Text.Json.Nodes.JsonArray(g.Gaps.Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                { ["field"] = x.Field, ["code"] = x.Code, ["message"] = x.Message }).ToArray());
            sources["$generatedAt"] = now.ToString("o", CultureInfo.InvariantCulture);
            sources["$schedule"] = DocumentSchedule.Frequency(type, settings.GetValueOrDefault(type.Code)?.ScheduleFrequency);
            var period = from == to ? $"{from:yyyy-MM-dd}" : $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";
            doc = new FactoryDocument
            {
                FactoryId = factoryId, TypeCode = type.Code, Category = type.Category, LineId = lineId,
                Reference = $"DOC-{DocumentCategories.PrefixFor(type.Category)}-{today:yyyyMMdd}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))}",
                Title = $"{type.Name} {period}{(lineName is null ? "" : " · " + lineName)}", PeriodFrom = from, PeriodTo = to, Status = DocumentStatuses.Draft, Risk = type.Risk,
                ApprovalLevel = DocumentCatalog.EffectiveApproval(type, settings.GetValueOrDefault(type.Code)?.ApprovalLevel), Source = DocumentSources.Generated,
                CreatedByEmail = SystemEmail, DedupKey = key,
            };
            checks = DocumentRules.Validate(type, data, today);
            await enricher.EnrichAsync(doc, type, data, checks, ct);
            foreach (var x in g.Gaps) checks.Warn(x.Field, x.Code, x.Message);
            doc.MissingCount = checks.Missing.Count; doc.ErrorCount = checks.Errors.Count; doc.WarningCount = checks.Warnings.Count; doc.CurrentVersion = 1;
            db.FactoryDocuments.Add(doc);
            db.FactoryDocumentVersions.Add(new FactoryDocumentVersion
            {
                DocumentId = doc.Id, Number = 1, DataJson = data.ToJsonString(), SourcesJson = sources.ToJsonString(), ChecksJson = checks.ToJson(),
                MissingCount = checks.Missing.Count, ErrorCount = checks.Errors.Count, WarningCount = checks.Warnings.Count,
                ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data.ToJsonString()))).ToLowerInvariant(),
                ChangeNote = $"Generated automatically from records ({sources["$schedule"]} schedule)", CreatedByEmail = SystemEmail,
            });
            db.FactoryDocumentEvents.Add(new FactoryDocumentEvent { DocumentId = doc.Id, FactoryId = factoryId, Action = "generated_automatically", ToStatus = DocumentStatuses.Draft, VersionNumber = 1, ActorEmail = SystemEmail });
            db.AuditLog.Add(AccessAudit.Entry(actor, "document.generated_automatically", "factory_document", doc.Id.ToString(), null, DocumentStatuses.Draft, doc.Reference, null, factoryId));
            run.Status = ScheduleRunStatuses.Created; run.DocumentId = doc.Id; run.ErrorCode = null; run.ErrorMessage = null;
            await db.SaveChangesAsync(ct); // document, version, event, audit and run together
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateException ex) when (DocScope.IsUniqueViolation(ex))
        {
            // Another instance generated the same period (or recorded the run) at the same moment.
            db.ChangeTracker.Clear();
            return await db.DocumentScheduleRuns.AsNoTracking().FirstOrDefaultAsync(r => r.FactoryId == factoryId && r.TypeCode == type.Code && r.PeriodFrom == from && r.PeriodTo == to && r.LineKey == lineKey, ct);
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            var attempts = run.Attempts;
            run = await db.DocumentScheduleRuns.FirstOrDefaultAsync(r => r.Id == run.Id, ct) ?? new DocumentScheduleRun
            {
                FactoryId = factoryId, TypeCode = type.Code, PeriodFrom = from, PeriodTo = to, LineKey = lineKey, FirstAttemptAt = now,
            };
            if (db.Entry(run).State == EntityState.Detached) db.DocumentScheduleRuns.Add(run);
            run.Attempts = attempts; run.LastAttemptAt = now; run.Status = ScheduleRunStatuses.Failed; run.DocumentId = null;
            run.ErrorCode = ex.GetType().Name.Length > 64 ? ex.GetType().Name[..64] : ex.GetType().Name;
            var msg = ex.Message.ReplaceLineEndings(" ");
            run.ErrorMessage = msg.Length > 500 ? msg[..500] : msg;
            if (attempts >= DocumentSchedule.MaxFailedAttempts)
                db.AuditLog.Add(AccessAudit.Entry(actor, "document.schedule_failed", "document_type", type.Code, null, ScheduleRunStatuses.Failed,
                    $"{type.Name} {from:yyyy-MM-dd}..{to:yyyy-MM-dd} {lineKey}: {run.ErrorMessage}", null, factoryId));
            await SaveRunAsync(db, run, ct);
            if (attempts >= DocumentSchedule.MaxFailedAttempts)
            {
                var controllers = await notifier.ResponsibleAsync(factoryId, type.Category, null, new[] { Permissions.DocumentsControl }, ct);
                await notifier.NotifyAsync(controllers, factoryId, "document.schedule_failed", $"Scheduled {type.Name} could not be generated",
                    $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}{(lineName is null ? "" : " · " + lineName)}: {run.ErrorMessage} (tried {attempts} times; create it by hand).",
                    "/app/documents/settings", "document_type", type.Code, $"schedule_failed:{factoryId}:{type.Code}:{from:yyyyMMdd}:{to:yyyyMMdd}:{lineKey}", ct);
            }
            return run;
        }

        var preparers = await notifier.ResponsibleAsync(factoryId, type.Category, lineId, new[] { Permissions.DocumentsCreate }, ct);
        await notifier.NotifyAsync(preparers, factoryId, "document.draft_generated", $"{doc.Title} is ready to check",
            checks.Missing.Count + checks.Errors.Count > 0 ? $"Generated from records; {checks.Missing.Count} missing value(s) and {checks.Errors.Count} error(s) need you." : "Generated from records; check it and submit.",
            $"/app/documents/{doc.Id}", "factory_document", doc.Id.ToString(), $"draft:{doc.Id}", ct);
        return run;
    }

    private static async Task<DocumentScheduleRun?> SaveRunAsync(BunonBrainDbContext db, DocumentScheduleRun run, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DocScope.IsUniqueViolation(ex) || ex is DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
        return run;
    }

    private static async Task<List<Guid>> MonitorsAsync(BunonBrainDbContext db, Guid factoryId, CancellationToken ct)
    {
        var roles = AccessCatalog.AllRoles.Where(r => r.Scope == RoleScope.Factory && r.Permissions.Contains(Permissions.DocumentsMonitor)
                                                      && !r.Permissions.Contains(Permissions.DocumentsCreate) && !r.Permissions.Contains(Permissions.DocumentsApprove)).Select(r => r.Id).ToList();
        return await (from u in db.Users.AsNoTracking()
                      join f in db.UserFactories.AsNoTracking() on u.Id equals f.UserId
                      join r in db.UserRoles.AsNoTracking() on u.Id equals r.UserId
                      where u.IsActive && f.FactoryId == factoryId && roles.Contains(r.RoleId)
                      select u.Id).Distinct().ToListAsync(ct);
    }
}
