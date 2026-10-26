using System.Text.Json;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Merchandising;
using FactoryBrain.Application.Merchandising;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Merchandising;

/// <summary>Builds the merchandising read shapes from the persisted rows. Nothing here writes.</summary>
internal sealed class MerchReadModel
{
    private readonly FactoryBrainDbContext _db;
    private readonly ProductionOptions _plant;
    private readonly MerchOptions _options;
    private readonly IDocumentExtractionProvider _provider;

    public MerchReadModel(FactoryBrainDbContext db, ProductionOptions plant, MerchOptions options, IDocumentExtractionProvider provider)
    {
        _db = db; _plant = plant; _options = options; _provider = provider;
    }

    public DateOnly Today => MerchSupport.Today(_plant);

    public ExtractionStatusDto Extraction()
    {
        var s = _provider.Describe();
        return new ExtractionStatusDto(s.Configured, s.Mode, s.Provider, s.Model, s.ReportsConfidence, s.Reason);
    }

    public string ConfidenceNote() => _provider.Describe().Mode switch
    {
        MerchOptions.ModeLlm => $"Confidence is the language model's own estimate, not a calibrated probability. Values below {_options.ReviewConfidence:0.##} are flagged.",
        MerchOptions.ModeRules => "The rule-based parser reports no confidence; every value quotes the exact line it was read from.",
        _ => "No automatic extraction: values are entered by the reviewer.",
    };

    // ── Mappings ────────────────────────────────────────────────────────────

    public static MerchOrderDto ToDto(MerchOrder o)
    {
        var rows = MerchFields.ParseBreakdown(o.BreakdownJson);
        return new MerchOrderDto(o.Id, o.Reference, o.Buyer, o.BuyerOrderNumber, o.StyleRef, o.ProductDescription, o.Quantity, o.Unit,
            o.DeliveryDate is { } d ? MerchWire.Date(d) : null, o.Currency, o.UnitPrice, o.Destination,
            rows?.Select(r => new BreakdownRowDto(r.Color, r.Size, r.Quantity)).ToList(), rows is null ? null : MerchFields.BreakdownTotal(rows),
            o.RequiredDocumentTypes, o.ProductionOrderId, MerchWire.OrderStatus(o.Status), o.Notes, o.CreatedAt, o.CreatedByEmail, o.UpdatedAt, o.Version);
    }

    public static MerchMilestoneDto ToDto(MerchMilestone m, DateOnly today) => new(
        m.Id, m.OrderId, m.Name, MerchWire.Date(m.DueDate), MerchWire.MilestoneStatus(m.Status), m.CompletedOn is { } c ? MerchWire.Date(c) : null,
        m.Notes, m.Status == MerchMilestoneStatus.Pending && m.DueDate < today, m.DueDate.DayNumber - today.DayNumber, m.UpdatedAt,
        m.UpdatedByEmail ?? m.CreatedByEmail, m.Version);

    public static MerchConflictDto ToDto(MerchConflict c, string orderReference)
    {
        List<ConflictSource> values;
        try { values = JsonSerializer.Deserialize<List<ConflictSource>>(c.ValuesJson, MerchSupport.Json) ?? new(); }
        catch (JsonException) { values = new(); }
        return new MerchConflictDto(c.Id, c.OrderId, orderReference, c.FieldKey, MerchFields.Get(c.FieldKey).Label, MerchWire.ConflictStatus(c.Status),
            values.Select(v => new MerchConflictValueDto(v.Value, MerchFields.Display(c.FieldKey, v.Value), v.Kind, v.DocumentId, v.DocumentTitle,
                v.DocumentType, v.VersionId, v.VersionNumber, v.VersionStatus, v.FieldId, v.Page, v.Snippet)).ToList(),
            c.DetectedAt, c.ResolvedAt, c.ResolvedByEmail, c.ResolvedValue, c.ResolutionNote, c.Version);
    }

    public static MerchVersionSummary Summary(MerchDocumentVersion v, int fieldCount, int blocking) => new(
        v.Id, v.VersionNumber, v.IsCurrent, MerchWire.VersionStatus(v.Status), v.Format, v.OriginalFileName, v.SizeBytes, v.CreatedAt,
        v.UploadedByEmail, v.ChangeNote, v.FailureCode, v.ExtractionCode, fieldCount, blocking, v.ConfirmedAt, v.ConfirmedByEmail);

    public static MerchExtractedFieldDto ToDto(MerchExtractedField f, MerchVersionStatus status)
    {
        var effective = MerchSupport.EffectiveValue(f, status);
        return new MerchExtractedFieldDto(f.Id, f.FieldKey, MerchFields.Get(f.FieldKey).Label, MerchWire.Source(f.Source), f.RawValue, f.NormalizedValue,
            MerchFields.Display(f.FieldKey, f.NormalizedValue ?? f.RawValue), f.Confidence, f.PageNumber, f.Snippet, f.EvidenceStatus,
            f.Issues.Select(i => new MerchIssueDto(i, MerchFields.DescribeIssue(i), MerchFields.IsBlocking(i))).ToList(),
            MerchWire.Decision(f.Decision), f.CorrectedValue, effective, f.ReviewerNote, f.ReviewedAt, f.ReviewedByEmail);
    }

    /// <summary>The order's values with their provenance (stale when the source version is no longer current).</summary>
    public async Task<List<MerchFieldValueDto>> FieldValuesAsync(MerchOrder o, CancellationToken ct)
    {
        var sources = MerchSupport.Sources(o);
        var versionIds = sources.Values.Where(s => s.VersionId is not null).Select(s => s.VersionId!.Value).Distinct().ToList();
        var current = await _db.MerchDocumentVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.IsCurrent, ct);
        return MerchFields.All.Select(def =>
        {
            var value = MerchSupport.GetValue(o, def.Key);
            MerchFieldSourceDto? src = null;
            if (value is not null && sources.TryGetValue(def.Key, out var s))
                src = new MerchFieldSourceDto(s.Kind, s.DocumentId, s.VersionId, s.VersionNumber, s.DocumentTitle, s.ConflictId, s.ByEmail, s.At,
                    s.VersionId is { } vid && current.TryGetValue(vid, out var isCurrent) && !isCurrent);
            return new MerchFieldValueDto(def.Key, def.Label, value, MerchFields.Display(def.Key, value), src);
        }).ToList();
    }

    /// <summary>Document list items with their current version, field counts and blocking issues.</summary>
    public async Task<List<MerchDocumentListItem>> DocumentItemsAsync(IReadOnlyList<MerchDocument> docs, CancellationToken ct)
    {
        if (docs.Count == 0) return new();
        var orderIds = docs.Select(d => d.OrderId).Distinct().ToList();
        var orders = await _db.MerchOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Reference, o.Buyer, o.BuyerOrderNumber }).ToDictionaryAsync(o => o.Id, ct);
        var currentIds = docs.Where(d => d.CurrentVersionId is not null).Select(d => d.CurrentVersionId!.Value).ToList();
        var versions = await _db.MerchDocumentVersions.AsNoTracking().Where(v => currentIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var counts = await FieldCountsAsync(currentIds, ct);
        return docs.Select(d =>
        {
            var o = orders[d.OrderId];
            MerchVersionSummary? current = null;
            if (d.CurrentVersionId is { } cid && versions.TryGetValue(cid, out var v))
            {
                var (n, blocking) = counts.GetValueOrDefault(cid);
                current = Summary(v, n, blocking);
            }
            var type = MerchWire.DocumentType(d.Type);
            return new MerchDocumentListItem(d.Id, d.OrderId, o.Reference, o.Buyer, o.BuyerOrderNumber, type, MerchWire.DocumentTypeLabels[type], d.Title,
                d.VersionCount, current, d.UpdatedAt);
        }).ToList();
    }

    /// <summary>Per version: number of fields and of unreviewed values with a blocking issue.</summary>
    public async Task<Dictionary<Guid, (int Fields, int Blocking)>> FieldCountsAsync(IReadOnlyCollection<Guid> versionIds, CancellationToken ct)
    {
        var rows = await _db.MerchExtractedFields.AsNoTracking().Where(f => versionIds.Contains(f.VersionId))
            .Select(f => new { f.VersionId, f.Issues, f.Decision }).ToListAsync(ct);
        return rows.GroupBy(r => r.VersionId).ToDictionary(g => g.Key,
            g => (g.Count(), g.Count(r => r.Decision == MerchFieldDecision.Unreviewed && r.Issues.Any(MerchFields.IsBlocking))));
    }

    // ── Order detail ────────────────────────────────────────────────────────

    public async Task<MerchOrderDetail?> OrderDetailAsync(Guid id, CancellationToken ct)
    {
        var o = await _db.MerchOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;
        var today = Today;
        var milestones = await _db.MerchMilestones.AsNoTracking().Where(m => m.OrderId == id).OrderBy(m => m.DueDate).ThenBy(m => m.Name).ToListAsync(ct);
        var docs = await _db.MerchDocuments.AsNoTracking().Where(d => d.OrderId == id).OrderBy(d => d.Type).ThenBy(d => d.CreatedAt).ToListAsync(ct);
        var docItems = await DocumentItemsAsync(docs, ct);
        var conflicts = await _db.MerchConflicts.AsNoTracking().Where(c => c.OrderId == id)
            .OrderBy(c => c.Status).ThenByDescending(c => c.DetectedAt).ToListAsync(ct);
        var fields = await FieldValuesAsync(o, ct);

        // Unresolved fields of confirmed current versions.
        var confirmedIds = docItems.Where(d => d.CurrentVersion is { Status: "confirmed" }).Select(d => d.CurrentVersion!.Id).ToList();
        var unresolved = await _db.MerchExtractedFields.AsNoTracking()
            .Where(f => confirmedIds.Contains(f.VersionId) && f.Decision == MerchFieldDecision.Unresolved)
            .Select(f => new { f.VersionId, f.FieldKey }).ToListAsync(ct);

        var versionsById = await _db.MerchDocumentVersions.AsNoTracking()
            .Where(v => v.OrderId == id && v.IsCurrent).ToDictionaryAsync(v => v.Id, ct);
        var attentionDocs = docs.Select(d =>
        {
            MerchDocumentVersion? v = d.CurrentVersionId is { } cid ? versionsById.GetValueOrDefault(cid) : null;
            return new AttentionDocument(d.Id, d.Title, MerchWire.DocumentType(d.Type), v?.Id, v?.VersionNumber, v?.Status, v?.FailureCode, v?.ExtractionCode,
                v is null ? Array.Empty<string>() : unresolved.Where(u => u.VersionId == v.Id).Select(u => u.FieldKey).ToList());
        }).ToList();

        var linked = await LinkedProductionOrderAsync(o, ct);
        var stale = fields.Where(f => f.Source is { Stale: true }).Select(f => new AttentionStaleField(f.Key, f.Source!.DocumentTitle ?? "a document", f.Source.VersionNumber ?? 0)).ToList();
        var attention = MerchAttention.Build(new AttentionInput(
            new AttentionOrder(o.Buyer, o.BuyerOrderNumber, o.StyleRef, o.Quantity, o.Unit, o.DeliveryDate, o.Status, MerchFields.ParseBreakdown(o.BreakdownJson), o.RequiredDocumentTypes),
            milestones.Select(m => new AttentionMilestone(m.Id, m.Name, m.DueDate, m.Status)).ToList(),
            attentionDocs,
            conflicts.Where(c => c.Status == MerchConflictStatus.Open).Select(c => new AttentionConflict(c.Id, c.FieldKey, ConflictValueCount(c))).ToList(),
            stale, linked?.OrderNumber, linked?.GoodSewingOutput, linked?.Differences ?? Array.Empty<string>(), today));

        return new MerchOrderDetail(ToDto(o), fields, milestones.Select(m => ToDto(m, today)).ToList(), docItems, attention.RequiredDocuments,
            conflicts.Select(c => ToDto(c, o.Reference)).ToList(), attention.Items, attention.Summary, linked, MerchWire.Date(today));
    }

    private static int ConflictValueCount(MerchConflict c)
    {
        try { using var doc = JsonDocument.Parse(c.ValuesJson); return doc.RootElement.GetArrayLength(); }
        catch (JsonException) { return 0; }
    }

    /// <summary>The linked Sector 2 production order (re-read every time, so a removed link shows as none) and how it differs from the buyer order.</summary>
    private async Task<LinkedProductionOrderDto?> LinkedProductionOrderAsync(MerchOrder o, CancellationToken ct)
    {
        if (o.ProductionOrderId is not { } pid) return null;
        var p = await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct);
        if (p is null) return null;
        var style = await _db.ProductionStyles.AsNoTracking().Where(s => s.Id == p.StyleId).Select(s => new { s.Code, s.Name }).FirstOrDefaultAsync(ct);
        var good = await _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => e.OrderId == pid && !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput)
            .SumAsync(e => (int?)e.Quantity, ct) ?? 0;
        var diffs = new List<string>();
        if (o.Quantity is { } q && q != p.Quantity) diffs.Add($"Production order {p.OrderNumber} is for {p.Quantity:N0} pieces; the buyer order is for {q:N0} {o.Unit ?? ""}".TrimEnd() + ".");
        if (o.DeliveryDate is { } d && p.DueDate > d) diffs.Add($"Production order {p.OrderNumber} is due {ProductionWire.Date(p.DueDate)}, after the buyer's delivery date {MerchWire.Date(d)}.");
        if (o.StyleRef is { } sr && style is not null
            && MerchFields.ComparisonKey(MerchFields.Style, sr) != MerchFields.ComparisonKey(MerchFields.Style, style.Code))
            diffs.Add($"Production order {p.OrderNumber} uses style {style.Code}; the buyer order states {sr}.");
        return new LinkedProductionOrderDto(p.Id, p.OrderNumber, ProductionWire.OrderStatus(p.Status), p.Quantity, ProductionWire.Date(p.DueDate),
            style?.Code ?? "?", style?.Name ?? "?", good, diffs);
    }

    // ── Version detail ──────────────────────────────────────────────────────

    public async Task<MerchVersionDetail?> VersionDetailAsync(Guid id, CancellationToken ct)
    {
        var v = await _db.MerchDocumentVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        var doc = await _db.MerchDocuments.AsNoTracking().FirstAsync(d => d.Id == v.DocumentId, ct);
        var order = await _db.MerchOrders.AsNoTracking().FirstAsync(o => o.Id == v.OrderId, ct);
        var pages = await _db.MerchDocumentPages.AsNoTracking().Where(p => p.VersionId == id).OrderBy(p => p.PageNumber)
            .Select(p => new MerchPageDto(p.PageNumber, p.Text)).ToListAsync(ct);
        var fields = await _db.MerchExtractedFields.AsNoTracking().Where(f => f.VersionId == id).ToListAsync(ct);
        var ordered = fields.OrderBy(f => MerchFields.All.ToList().FindIndex(d => d.Key == f.FieldKey)).ToList();
        var others = await _db.MerchDocumentVersions.AsNoTracking().Where(x => x.DocumentId == v.DocumentId && x.Id != id)
            .OrderByDescending(x => x.VersionNumber).ToListAsync(ct);
        var counts = await FieldCountsAsync(others.Select(x => x.Id).ToList(), ct);
        var conflicts = (await _db.MerchConflicts.AsNoTracking().Where(c => c.OrderId == v.OrderId).ToListAsync(ct))
            .Select(c => ToDto(c, order.Reference))
            .Where(c => c.Values.Any(x => x.VersionId == id))
            .ToList();
        var present = fields.Where(f => f.Decision != MerchFieldDecision.Rejected && (f.NormalizedValue ?? f.CorrectedValue ?? f.RawValue) is not null)
            .Select(f => f.FieldKey).ToHashSet();
        var missing = doc.Type == MerchDocumentType.PurchaseOrder ? MerchFields.RequiredOnPurchaseOrder.Where(k => !present.Contains(k)).ToList() : new List<string>();
        var type = MerchWire.DocumentType(doc.Type);

        return new MerchVersionDetail(v.Id, doc.Id, order.Id, order.Reference, order.Buyer, order.BuyerOrderNumber, order.Version, type, doc.Title,
            v.VersionNumber, v.IsCurrent, v.ChangeNote,
            new MerchFileDto($"/api/merchandising/versions/{v.Id}/file", v.ContentType, v.Format, v.SizeBytes, v.OriginalFileName, v.Sha256),
            new MerchProcessingDto(MerchWire.VersionStatus(v.Status), v.Attempts, v.ProcessingStartedAt, v.ProcessedAt, v.FailureCode, v.FailureMessage,
                v.PageCount, v.TextLength, v.TextTruncated, v.ExtractionCode, v.ExtractionMessage, v.Provider, v.Model, v.ModelVersion, v.ExtractionMs,
                MerchLifecycle.CanRetry(v), ConfidenceNote()),
            pages, ordered.Select(f => ToDto(f, v.Status)).ToList(), missing, await FieldValuesAsync(order, ct), conflicts,
            others.Select(x => Summary(x, counts.GetValueOrDefault(x.Id).Fields, counts.GetValueOrDefault(x.Id).Blocking)).ToList(),
            v.ConfirmedAt is { } at ? new MerchReviewDto(at, v.ConfirmedByEmail, v.ReviewNotes) : null,
            MerchLifecycle.CanConfirm(v), v.CreatedAt, v.UploadedByEmail, v.Version);
    }
}

/// <summary>Which actions a version's state allows.</summary>
internal static class MerchLifecycle
{
    /// <summary>Codes a retry can't fix (the file itself must change).</summary>
    private static readonly HashSet<string> Permanent = new()
    {
        DocumentParseException.Malformed, DocumentParseException.Encrypted, DocumentParseException.OcrRequired,
        DocumentParseException.TooManyPages, DocumentParseException.NoText, "file_missing",
    };

    /// <summary>Only the current version can be confirmed: once it has been read (or reading failed, for manual entry).</summary>
    public static bool CanConfirm(MerchDocumentVersion v) => v.IsCurrent && v.Status is MerchVersionStatus.NeedsReview or MerchVersionStatus.Failed;

    /// <summary>A failed run that may succeed again, or a version read without extraction that nobody has reviewed yet.</summary>
    public static bool CanRetry(MerchDocumentVersion v) => v.IsCurrent && v.Status switch
    {
        MerchVersionStatus.Failed => v.FailureCode is null || !Permanent.Contains(v.FailureCode),
        MerchVersionStatus.NeedsReview => v.ExtractionCode is not null,
        _ => false,
    };
}
