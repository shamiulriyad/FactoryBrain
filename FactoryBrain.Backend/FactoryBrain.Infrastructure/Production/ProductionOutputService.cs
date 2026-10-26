using System.Security.Cryptography;
using System.Text;
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
/// Output records (pieces per line, order, stage and interval).
/// <list type="bullet">
/// <item>Deduplication: a source's <c>sourceRef</c> is accepted once (a repeat with the same values is
/// answered with the stored record; different values are a conflict), and live records for one line,
/// order and stage may not overlap in time. Writes hold a per-line advisory lock, so concurrent
/// submissions can't both pass the overlap check.</item>
/// <item>Corrections never edit a record: a replacement is inserted and the original is marked
/// superseded, with the reason. Summaries are computed at read time from live records, so a
/// correction or a late record changes every summary that covers its interval.</item>
/// </list>
/// </summary>
public sealed class ProductionOutputService : IProductionOutputService
{
    public const string EntityType = "production_output";
    public const int MaxImportRows = 500;

    private readonly FactoryBrainDbContext _db;
    private readonly ProductionOptions _options;
    private readonly ProductionReadModel _read;
    private readonly ILogger<ProductionOutputService> _log;

    public ProductionOutputService(FactoryBrainDbContext db, ProductionOptions options, ILogger<ProductionOutputService> log)
    {
        _db = db; _options = options; _log = log; _read = new ProductionReadModel(db, options);
    }

    // ── History ─────────────────────────────────────────────────────────────

    public async Task<ProductionResult<Paged<OutputEventDto>>> ListAsync(OutputQuery q, CancellationToken ct)
    {
        var errors = Errors();
        ProductionStage? stage = null;
        if (q.Stage is not null)
        {
            if (ProductionWire.TryParseStage(q.Stage, out var s)) stage = s;
            else errors.Add("stage", $"stage must be one of: {string.Join(", ", ProductionWire.Stages)}");
        }
        ProductionDataSource? source = null;
        if (q.Source is not null)
        {
            if (ProductionWire.TryParseSource(q.Source, out var s)) source = s;
            else errors.Add("source", $"source must be one of: {string.Join(", ", ProductionWire.Sources)}");
        }
        if (q.Page < 1) errors.Add("page", "page must be 1 or more.");
        if (q.PageSize is < 1 or > 200) errors.Add("pageSize", "pageSize must be from 1 to 200.");
        if (q.From is { } f && q.To is { } t && f > t) errors.Add("from", "from must not be after to.");
        if (q.Sort is not null and not ("newest" or "oldest")) errors.Add("sort", "sort must be newest or oldest.");
        if (errors.Count > 0) return ProductionResult<Paged<OutputEventDto>>.Invalid(errors);

        var query = _db.ProductionOutputEvents.AsNoTracking();
        if (!q.IncludeSuperseded) query = query.Where(e => !e.IsSuperseded);
        if (q.From is { } from) query = query.Where(e => e.ProductionDate >= from);
        if (q.To is { } to) query = query.Where(e => e.ProductionDate <= to);
        if (q.LineId is not null) query = query.Where(e => e.LineId == q.LineId);
        if (q.OrderId is { } oid) query = query.Where(e => e.OrderId == oid);
        if (q.Shift is not null) query = q.Shift == "overtime" ? query.Where(e => e.ShiftCode == null) : query.Where(e => e.ShiftCode == q.Shift);
        if (stage is { } st) query = query.Where(e => e.Stage == st);
        if (source is { } src) query = query.Where(e => e.Source == src);
        var total = await query.CountAsync(ct);
        var ordered = q.Sort == "oldest"
            ? query.OrderBy(e => e.IntervalStartUtc).ThenBy(e => e.RecordedAt)
            : query.OrderByDescending(e => e.IntervalStartUtc).ThenByDescending(e => e.RecordedAt);
        var rows = await ordered.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        return ProductionResult<Paged<OutputEventDto>>.Ok(new Paged<OutputEventDto>(await ToDtosAsync(_db, rows, ct), q.Page, q.PageSize, total,
            (int)Math.Ceiling(total / (double)q.PageSize)));
    }

    internal static async Task<IReadOnlyList<OutputEventDto>> ToDtosAsync(FactoryBrainDbContext db, IReadOnlyList<ProductionOutputEvent> rows, CancellationToken ct)
    {
        var orderIds = rows.Select(r => r.OrderId).Distinct().ToList();
        var numbers = await db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
        return rows.Select(e => ToDto(e, numbers.GetValueOrDefault(e.OrderId, "?"))).ToList();
    }

    internal static OutputEventDto ToDto(ProductionOutputEvent e, string orderNumber) => new(
        e.Id, e.LineId, e.OrderId, orderNumber, ProductionWire.Stage(e.Stage), e.IntervalStartUtc, e.IntervalEndUtc,
        ProductionWire.Date(e.ProductionDate), e.ShiftCode, e.Quantity, e.ReworkQuantity, e.RejectQuantity, e.OperatorsPresent,
        e.DowntimeMinutes, e.DowntimeReason, ProductionWire.Source(e.Source), e.SourceRef, e.Notes, e.IsSuperseded, e.SupersededById,
        e.CorrectionOfId, e.CorrectionReason, e.RecordedAt, e.RecordedByEmail, e.RecordedAt - e.IntervalEndUtc > OutputRules.LateThreshold);

    // ── Record ──────────────────────────────────────────────────────────────

    public async Task<ProductionResult<OutputEventDto>> RecordAsync(OutputWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        var (line, order, stage, source, sourceRef) = await ReferencesAsync(errors, body.LineId, body.OrderId, null, body.Stage, body.Source, body.SourceRef, ct);
        var cal = await _read.CalendarAsync(ct);
        var now = DateTime.UtcNow;
        var check = OutputRules.Check(Values(body), now, _options, cal, errors);
        if (errors.Count > 0) return ProductionResult<OutputEventDto>.Invalid(errors);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockLineAsync(_db, line!.Id, ct);
            var refKey = sourceRef;
            if (refKey is not null)
            {
                var existing = await _db.ProductionOutputEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Source == source && e.SourceRef == refKey, ct);
                if (existing is not null)
                {
                    await tx.RollbackAsync(ct);
                    return existing.LineId == line.Id && existing.OrderId == order!.Id && existing.Stage == stage && OutputRules.SameRecord(existing, check!)
                        ? new ProductionResult<OutputEventDto>(ProductionOutcome.Ok, ToDto(existing, order.OrderNumber), Code: "duplicate",
                            Message: "This record was already stored; the stored record is returned.")
                        : ProductionResult<OutputEventDto>.Conflict("duplicate_source_ref",
                            $"sourceRef '{refKey}' was already used for a different record ({existing.Id}). Correct that record instead.");
                }
            }
            if (await OverlappingAsync(line.Id, order!.Id, stage, check!.StartUtc, check.EndUtc, null, ct) is { } clash)
            {
                await tx.RollbackAsync(ct);
                return ProductionResult<OutputEventDto>.Conflict("overlapping_record",
                    $"{ProductionWire.Stage(stage)} output for this order on {line.Id} is already recorded for {clash.IntervalStartUtc:yyyy-MM-dd HH:mm}–{clash.IntervalEndUtc:HH:mm} UTC (record {clash.Id}). Correct that record instead of adding another.");
            }
            var e = NewEvent(line.Id, order.Id, stage, check, source, refKey, actor, now);
            _db.ProductionOutputEvents.Add(e);
            _db.AuditLog.Add(Audit(actor, traceId, "production_output.recorded", EntityType, e.Id.ToString(), null, "live", Describe(e, order.OrderNumber)));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _log.LogInformation("Output {EventId} recorded on {LineId} by user {UserId}.", e.Id, line.Id, actor.UserId);
            return ProductionResult<OutputEventDto>.Created(ToDto(e, order.OrderNumber));
        });
    }

    // ── Correct ─────────────────────────────────────────────────────────────

    public async Task<ProductionResult<OutputEventDto>> CorrectAsync(Guid id, OutputCorrection body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var original = await _db.ProductionOutputEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (original is null) return ProductionResult<OutputEventDto>.NotFound("output_not_found");
        var errors = Errors();
        var reason = Clean(body.Reason);
        if (reason is null || reason.Length > 500) errors.Add("reason", "Explain the correction (at most 500 characters).");
        var cal = await _read.CalendarAsync(ct);
        var now = DateTime.UtcNow;
        var merged = new OutputValues(
            body.IntervalStart ?? original.IntervalStartUtc, body.IntervalEnd ?? original.IntervalEndUtc,
            body.Quantity ?? original.Quantity, body.ReworkQuantity ?? original.ReworkQuantity, body.RejectQuantity ?? original.RejectQuantity,
            body.OperatorsPresent ?? original.OperatorsPresent, body.DowntimeMinutes ?? original.DowntimeMinutes,
            body.DowntimeReason ?? original.DowntimeReason, body.Notes ?? original.Notes);
        var check = OutputRules.Check(merged, now, _options, cal, errors);
        if (original.IntervalStartUtc < now.AddDays(-_options.MaxBackfillDays))
            errors.Add("intervalStart", $"Records older than {_options.MaxBackfillDays} days can't be corrected.");
        if (errors.Count > 0) return ProductionResult<OutputEventDto>.Invalid(errors);
        var orderNumber = await _db.ProductionOrders.Where(o => o.Id == original.OrderId).Select(o => o.OrderNumber).FirstAsync(ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockLineAsync(_db, original.LineId, ct);
            var current = await _db.ProductionOutputEvents.FirstAsync(e => e.Id == id, ct);
            if (current.IsSuperseded)
            {
                await tx.RollbackAsync(ct);
                return ProductionResult<OutputEventDto>.Conflict("already_corrected",
                    $"This record was already corrected (by {current.SupersededById}). Correct the latest record instead.");
            }
            if (await OverlappingAsync(current.LineId, current.OrderId, current.Stage, check!.StartUtc, check.EndUtc, current.Id, ct) is { } clash)
            {
                await tx.RollbackAsync(ct);
                return ProductionResult<OutputEventDto>.Conflict("overlapping_record",
                    $"The corrected interval overlaps record {clash.Id} ({clash.IntervalStartUtc:yyyy-MM-dd HH:mm}–{clash.IntervalEndUtc:HH:mm} UTC).");
            }
            var replacement = NewEvent(current.LineId, current.OrderId, current.Stage, check, current.Source, null, actor, now);
            replacement.CorrectionOfId = current.Id;
            replacement.CorrectionReason = reason;
            // Supersede first: the live-interval unique index allows one live record per interval.
            current.IsSuperseded = true;
            current.SupersededById = replacement.Id;
            await _db.SaveChangesAsync(ct);
            _db.ProductionOutputEvents.Add(replacement);
            _db.AuditLog.Add(Audit(actor, traceId, "production_output.corrected", EntityType, current.Id.ToString(), "live", "superseded",
                $"replaced by {replacement.Id}: {Describe(current, orderNumber)} → {Describe(replacement, orderNumber)}. Reason: {reason}"));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ProductionResult<OutputEventDto>.Created(ToDto(replacement, orderNumber));
        });
    }

    // ── Import ──────────────────────────────────────────────────────────────

    public async Task<ProductionResult<OutputImportResult>> ImportAsync(OutputImportRequest body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var rows = body.Rows ?? Array.Empty<OutputImportRow>();
        if (rows.Count == 0) return ProductionResult<OutputImportResult>.Invalid("rows", "The file has no data rows.");
        if (rows.Count > MaxImportRows) return ProductionResult<OutputImportResult>.Invalid("rows", $"Import at most {MaxImportRows} rows at a time.");
        var fileName = Truncate(Clean(body.FileName), 120);

        var cal = await _read.CalendarAsync(ct);
        var now = DateTime.UtcNow;
        var numbers = rows.Select(r => Clean(r.OrderNumber)).Where(n => n is not null).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => numbers.Contains(o.OrderNumber)).ToDictionaryAsync(o => o.OrderNumber, ct);
        var lineIds = rows.Select(r => Clean(r.LineId)).Where(n => n is not null).Distinct().ToList();
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);

        var outcomes = new List<ImportRowOutcome>();
        var accepted = new List<(int Row, string Ref, string LineId, ProductionOrder Order, ProductionStage Stage, CheckedOutput Check)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var errors = Errors();
            var lineId = Clean(r.LineId);
            var number = Clean(r.OrderNumber);
            if (lineId is null || !lines.ContainsKey(lineId)) errors.Add("lineId", $"Unknown line '{lineId}'.");
            if (number is null || !orders.TryGetValue(number, out var order)) { errors.Add("orderNumber", $"Unknown order '{number}'."); order = null; }
            else if (order.Status == ProductionOrderStatus.Cancelled) errors.Add("orderNumber", $"Order {number} is cancelled.");
            if (!ProductionWire.TryParseStage(r.Stage, out var stage)) errors.Add("stage", $"Stage must be one of: {string.Join(", ", ProductionWire.Stages)}.");
            if (r.SourceRef is { Length: > 128 }) errors.Add("sourceRef", "sourceRef is at most 128 characters.");
            var check = OutputRules.Check(new OutputValues(r.IntervalStart, r.IntervalEnd, r.Quantity, r.ReworkQuantity, r.RejectQuantity,
                r.OperatorsPresent, r.DowntimeMinutes, r.DowntimeReason, r.Notes), now, _options, cal, errors);
            if (errors.Count > 0 || check is null)
            {
                outcomes.Add(new ImportRowOutcome(i + 1, r.SourceRef, "invalid", null, errors.SelectMany(kv => kv.Value).ToList()));
                continue;
            }
            // Without a sourceRef the natural key (line, order, stage, interval) identifies the row, so a re-import is recognised.
            var refKey = Clean(r.SourceRef) ?? "auto:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{lineId}|{order!.Id}|{ProductionWire.Stage(stage)}|{check.StartUtc:O}|{check.EndUtc:O}"))).ToLowerInvariant()[..40];
            var dup = accepted.FirstOrDefault(a => a.Ref == refKey);
            if (dup.Ref is not null)
            {
                outcomes.Add(new ImportRowOutcome(i + 1, refKey, "invalid", null, new[] { $"Same sourceRef as row {dup.Row}." }));
                continue;
            }
            var inFile = accepted.FirstOrDefault(a => a.LineId == lineId && a.Order.Id == order!.Id && a.Stage == stage
                                                      && OutputRules.Overlaps(a.Check.StartUtc, a.Check.EndUtc, check.StartUtc, check.EndUtc));
            if (inFile.Ref is not null)
            {
                outcomes.Add(new ImportRowOutcome(i + 1, refKey, "invalid", null, new[] { $"Overlaps row {inFile.Row} for the same line, order and stage." }));
                continue;
            }
            accepted.Add((i + 1, refKey, lineId!, order!, stage, check));
            outcomes.Add(new ImportRowOutcome(i + 1, refKey, "pending", null, null));
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var result = outcomes.ToList();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            foreach (var id in accepted.Select(a => a.LineId).Distinct().OrderBy(x => x, StringComparer.Ordinal)) await LockLineAsync(_db, id, ct);

            var refs = accepted.Select(a => a.Ref).ToList();
            var existingByRef = await _db.ProductionOutputEvents.AsNoTracking()
                .Where(e => e.Source == ProductionDataSource.Import && e.SourceRef != null && refs.Contains(e.SourceRef)).ToDictionaryAsync(e => e.SourceRef!, ct);
            var created = new List<ProductionOutputEvent>();
            foreach (var a in accepted)
            {
                var idx = a.Row - 1;
                if (existingByRef.TryGetValue(a.Ref, out var existing))
                {
                    result[idx] = existing.LineId == a.LineId && existing.OrderId == a.Order.Id && existing.Stage == a.Stage && OutputRules.SameRecord(existing, a.Check)
                        ? new ImportRowOutcome(a.Row, a.Ref, "duplicate", existing.Id, null)
                        : new ImportRowOutcome(a.Row, a.Ref, "invalid", existing.Id, new[] { "This sourceRef was imported before with different values. Correct the stored record instead." });
                    continue;
                }
                if (await OverlappingAsync(a.LineId, a.Order.Id, a.Stage, a.Check.StartUtc, a.Check.EndUtc, null, ct) is { } clash)
                {
                    result[idx] = new ImportRowOutcome(a.Row, a.Ref, "invalid", clash.Id,
                        new[] { $"Output is already recorded for this line, order and stage at {clash.IntervalStartUtc:yyyy-MM-dd HH:mm}–{clash.IntervalEndUtc:HH:mm} UTC." });
                    continue;
                }
                var e = NewEvent(a.LineId, a.Order.Id, a.Stage, a.Check, ProductionDataSource.Import, a.Ref, actor, now);
                created.Add(e);
                result[idx] = new ImportRowOutcome(a.Row, a.Ref, "created", e.Id, null);
            }

            var invalid = result.Count(r => r.Outcome == "invalid");
            if (invalid > 0)
            {
                // All or nothing: nothing is stored while any row is rejected, so a fixed file can simply be imported again.
                await tx.RollbackAsync(ct);
                var report = result.Select(r => r.Outcome == "created" || r.Outcome == "pending" ? r with { Outcome = "not_imported", EventId = null } : r).ToList();
                return new ProductionResult<OutputImportResult>(ProductionOutcome.Invalid, new OutputImportResult(rows.Count, 0,
                    report.Count(r => r.Outcome == "duplicate"), invalid, report),
                    report.Where(r => r.Outcome == "invalid").Take(50).ToDictionary(r => $"rows[{r.Row}]", r => (r.Errors ?? Array.Empty<string>()).ToArray()));
            }
            _db.ProductionOutputEvents.AddRange(created);
            if (created.Count > 0)
                _db.AuditLog.Add(Audit(actor, traceId, "production_output.imported", EntityType, created[0].Id.ToString(), null, "live",
                    $"{created.Count} record(s) imported from {fileName ?? "an upload"}; {result.Count(r => r.Outcome == "duplicate")} duplicate row(s) skipped. First record id shown; each record keeps its sourceRef."));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _log.LogInformation("Output import by user {UserId}: {Created} created, {Duplicates} duplicates.", actor.UserId, created.Count,
                result.Count(r => r.Outcome == "duplicate"));
            return ProductionResult<OutputImportResult>.Ok(new OutputImportResult(rows.Count, created.Count, result.Count(r => r.Outcome == "duplicate"), 0, result));
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(ProductionLine?, ProductionOrder?, ProductionStage, ProductionDataSource, string?)> ReferencesAsync(
        Dictionary<string, string[]> errors, string? lineId, Guid? orderId, string? orderNumber, string? stageRaw, string? sourceRaw, string? sourceRefRaw, CancellationToken ct)
    {
        ProductionLine? line = null;
        ProductionOrder? order = null;
        var id = Clean(lineId);
        if (id is null) errors.Add("lineId", "lineId is required.");
        else if ((line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)) is null) errors.Add("lineId", "The line does not exist.");
        if (orderId is null && orderNumber is null) errors.Add("orderId", "orderId is required.");
        else
        {
            order = orderId is { } oid
                ? await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == oid, ct)
                : await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) errors.Add("orderId", "The order does not exist.");
            else if (order.Status == ProductionOrderStatus.Cancelled) errors.Add("orderId", "The order is cancelled.");
        }
        if (!ProductionWire.TryParseStage(stageRaw, out var stage)) errors.Add("stage", $"stage must be one of: {string.Join(", ", ProductionWire.Stages)}");
        var source = ProductionDataSource.Manual;
        if (sourceRaw is not null && !ProductionWire.TryParseSource(sourceRaw, out source))
            errors.Add("source", $"source must be one of: {string.Join(", ", ProductionWire.Sources)}");
        if (source == ProductionDataSource.Import) errors.Add("source", "Use POST /api/production/output/import for imported files.");
        var sourceRef = Clean(sourceRefRaw);
        if (sourceRef is { Length: > 128 }) errors.Add("sourceRef", "sourceRef is at most 128 characters.");
        if (source == ProductionDataSource.Integration && sourceRef is null) errors.Add("sourceRef", "Integration records need the source system's own id as sourceRef.");
        return (line, order, stage, source, sourceRef);
    }

    private Task<ProductionOutputEvent?> OverlappingAsync(string lineId, Guid orderId, ProductionStage stage, DateTime start, DateTime end, Guid? except, CancellationToken ct) =>
        _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => e.LineId == lineId && e.OrderId == orderId && e.Stage == stage && !e.IsSuperseded
                        && e.IntervalStartUtc < end && start < e.IntervalEndUtc && (except == null || e.Id != except))
            .OrderBy(e => e.IntervalStartUtc).FirstOrDefaultAsync(ct);

    private static ProductionOutputEvent NewEvent(string lineId, Guid orderId, ProductionStage stage, CheckedOutput c, ProductionDataSource source,
        string? sourceRef, AuditActor actor, DateTime now) => new()
    {
        LineId = lineId, OrderId = orderId, Stage = stage, IntervalStartUtc = c.StartUtc, IntervalEndUtc = c.EndUtc,
        ProductionDate = c.ProductionDate, ShiftCode = c.ShiftCode, Quantity = c.Quantity, ReworkQuantity = c.Rework, RejectQuantity = c.Reject,
        OperatorsPresent = c.OperatorsPresent, DowntimeMinutes = c.DowntimeMinutes, DowntimeReason = c.DowntimeReason, Notes = c.Notes,
        Source = source, SourceRef = sourceRef, RecordedAt = now, RecordedByUserId = actor.UserId, RecordedByEmail = actor.Email,
    };

    private static OutputValues Values(OutputWrite b) => new(b.IntervalStart, b.IntervalEnd, b.Quantity, b.ReworkQuantity, b.RejectQuantity,
        b.OperatorsPresent, b.DowntimeMinutes, b.DowntimeReason, b.Notes);

    private static string Describe(ProductionOutputEvent e, string orderNumber) =>
        $"{e.LineId} · {orderNumber} · {ProductionWire.Stage(e.Stage)} · {e.IntervalStartUtc:yyyy-MM-dd HH:mm}–{e.IntervalEndUtc:HH:mm} UTC · " +
        $"good {e.Quantity}, rework {e.ReworkQuantity}, reject {e.RejectQuantity}" +
        (e.OperatorsPresent is { } o ? $", {o} operators" : "") + (e.DowntimeMinutes is > 0 ? $", downtime {e.DowntimeMinutes} min" : "");
}
