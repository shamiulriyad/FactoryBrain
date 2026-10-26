using System.Globalization;
using System.Text.Json;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Merchandising;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Infrastructure.Production;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Merchandising;

/// <summary>Where an order value came from, as stored in <see cref="MerchOrder.FieldSourcesJson"/>.</summary>
internal sealed record FieldSourceRecord(
    string Kind, Guid? DocumentId, Guid? VersionId, int? VersionNumber, string? DocumentTitle, Guid? ConflictId, string ByEmail, DateTime At)
{
    public const string Manual = "manual";
    public const string Document = "document";
    public const string ConflictResolution = "conflict_resolution";
}

/// <summary>Helpers shared by the merchandising services.</summary>
internal static class MerchSupport
{
    /// <summary>Every merchandising audit row is filed under the order, so one query returns an order's full history.</summary>
    public const string EntityType = "merch_order";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Audit actor for work done by the background worker.</summary>
    public static readonly AuditActor SystemActor = new(null, "system:merch-worker", "System");

    public static AuditLogEntry Audit(AuditActor actor, string? traceId, string action, Guid orderId, string? from, string? to, string? reason) =>
        ProductionSupport.Audit(actor, traceId, action, EntityType, orderId.ToString(), from, to, reason);

    /// <summary>Serialises merchandising writes to one order (conflict detection, confirmation, resolution) for the transaction.</summary>
    public static Task LockOrderAsync(FactoryBrainDbContext db, Guid orderId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"merch_order:" + orderId}, 0))", ct);

    public static DateOnly Today(ProductionOptions plant, DateTime? nowUtc = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc ?? DateTime.UtcNow, plant.TimeZone));

    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    // ── Order field values ──────────────────────────────────────────────────

    /// <summary>Order fields that can be confirmed from documents (every schema field).</summary>
    public static readonly string[] OrderFieldKeys = MerchFields.All.Select(f => f.Key).ToArray();

    /// <summary>The order's value for a field, in normalised (schema) form.</summary>
    public static string? GetValue(MerchOrder o, string key) => key switch
    {
        MerchFields.OrderNumber => o.BuyerOrderNumber,
        MerchFields.Buyer => o.Buyer,
        MerchFields.Style => o.StyleRef,
        MerchFields.Product => o.ProductDescription,
        MerchFields.Quantity => o.Quantity?.ToString(CultureInfo.InvariantCulture),
        MerchFields.Unit => o.Unit,
        MerchFields.DeliveryDate => o.DeliveryDate is { } d ? MerchWire.Date(d) : null,
        MerchFields.Currency => o.Currency,
        MerchFields.UnitPrice => o.UnitPrice?.ToString("0.####", CultureInfo.InvariantCulture),
        MerchFields.Destination => o.Destination,
        MerchFields.Breakdown => o.BreakdownJson,
        _ => null,
    };

    /// <summary>Set a field from an already-normalised value (null clears; the buyer and PO number can't be cleared).</summary>
    public static void SetValue(MerchOrder o, string key, string? v)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (key)
        {
            case MerchFields.OrderNumber: if (v is not null) o.BuyerOrderNumber = v; break;
            case MerchFields.Buyer: if (v is not null) { o.Buyer = v; o.BuyerKey = BuyerKey(v); } break;
            case MerchFields.Style: o.StyleRef = v; break;
            case MerchFields.Product: o.ProductDescription = v; break;
            case MerchFields.Quantity: o.Quantity = v is null ? null : int.Parse(v, inv); break;
            case MerchFields.Unit: o.Unit = v; break;
            case MerchFields.DeliveryDate: o.DeliveryDate = v is null ? null : DateOnly.ParseExact(v, "yyyy-MM-dd", inv); break;
            case MerchFields.Currency: o.Currency = v; break;
            case MerchFields.UnitPrice: o.UnitPrice = v is null ? null : decimal.Parse(v, NumberStyles.AllowDecimalPoint, inv); break;
            case MerchFields.Destination: o.Destination = v; break;
            case MerchFields.Breakdown: o.BreakdownJson = v; break;
        }
    }

    public static string BuyerKey(string buyer) => MerchFields.CollapseWhitespace(buyer)!.ToLowerInvariant();

    public static Dictionary<string, FieldSourceRecord> Sources(MerchOrder o)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, FieldSourceRecord>>(o.FieldSourcesJson, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static void SetSources(MerchOrder o, Dictionary<string, FieldSourceRecord> sources) =>
        o.FieldSourcesJson = JsonSerializer.Serialize(sources, Json);

    /// <summary>The value a document version currently states for a field: confirmed versions count only accepted / corrected values.</summary>
    public static string? EffectiveValue(MerchExtractedField f, MerchVersionStatus status) => f.Decision switch
    {
        MerchFieldDecision.Corrected => f.CorrectedValue,
        MerchFieldDecision.Accepted => f.NormalizedValue,
        MerchFieldDecision.Unreviewed when status != MerchVersionStatus.Confirmed => f.NormalizedValue,
        _ => null,
    };

    // ── Conflict detection ──────────────────────────────────────────────────

    /// <summary>
    /// Re-runs <see cref="MerchConflictDetector"/> over the order's current
    /// documents (and its typed values) and reconciles the stored conflicts:
    /// new disagreements open a conflict (unless the same evidence was
    /// already resolved), changed ones update the open conflict, and open
    /// conflicts whose sources now agree are cleared. Call inside a
    /// transaction holding <see cref="LockOrderAsync"/>; the caller saves.
    /// </summary>
    public static async Task SyncConflictsAsync(FactoryBrainDbContext db, MerchOrder order, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var detected = await DetectAsync(db, order, ct);
        var stored = await db.MerchConflicts.Where(c => c.OrderId == order.Id && c.Status != MerchConflictStatus.Cleared).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var d in detected)
        {
            var json = JsonSerializer.Serialize(d.Values, Json);
            var open = stored.FirstOrDefault(c => c.FieldKey == d.FieldKey && c.Status == MerchConflictStatus.Open);
            if (open is not null)
            {
                if (open.Fingerprint == d.Fingerprint) continue;
                open.ValuesJson = json;
                open.Fingerprint = d.Fingerprint;
                open.UpdatedAt = now;
                db.AuditLog.Add(Audit(actor, traceId, "merch_conflict.updated", order.Id, "open", "open",
                    $"{MerchFields.Get(d.FieldKey).Label}: {Describe(d)}"));
                continue;
            }
            if (stored.Any(c => c.FieldKey == d.FieldKey && c.Status == MerchConflictStatus.Resolved && c.Fingerprint == d.Fingerprint)) continue;
            var conflict = new MerchConflict
            {
                OrderId = order.Id, FieldKey = d.FieldKey, ValuesJson = json, Fingerprint = d.Fingerprint, DetectedAt = now, UpdatedAt = now,
            };
            db.MerchConflicts.Add(conflict);
            db.AuditLog.Add(Audit(actor, traceId, "merch_conflict.detected", order.Id, null, "open",
                $"conflict {conflict.Id} · {MerchFields.Get(d.FieldKey).Label}: {Describe(d)}"));
        }
        foreach (var open in stored.Where(c => c.Status == MerchConflictStatus.Open && detected.All(d => d.FieldKey != c.FieldKey)))
        {
            open.Status = MerchConflictStatus.Cleared;
            open.UpdatedAt = now;
            db.AuditLog.Add(Audit(actor, traceId, "merch_conflict.cleared", order.Id, "open", "cleared",
                $"conflict {open.Id} · {MerchFields.Get(open.FieldKey).Label}: the sources now agree"));
        }
    }

    /// <summary>The conflicts the order's current documents (and typed values, as held in <paramref name="order"/>) show right now.</summary>
    public static async Task<IReadOnlyList<DetectedConflict>> DetectAsync(FactoryBrainDbContext db, MerchOrder order, CancellationToken ct)
    {
        var docs = await db.MerchDocuments.AsNoTracking().Where(d => d.OrderId == order.Id).ToDictionaryAsync(d => d.Id, ct);
        var versions = await db.MerchDocumentVersions.AsNoTracking()
            .Where(v => v.OrderId == order.Id && v.IsCurrent && (v.Status == MerchVersionStatus.NeedsReview || v.Status == MerchVersionStatus.Confirmed))
            .ToListAsync(ct);
        var versionIds = versions.Select(v => v.Id).ToList();
        var fields = await db.MerchExtractedFields.AsNoTracking().Where(f => versionIds.Contains(f.VersionId)).ToListAsync(ct);

        var sources = new List<ConflictSource>();
        foreach (var v in versions)
        {
            var doc = docs[v.DocumentId];
            foreach (var f in fields.Where(f => f.VersionId == v.Id))
            {
                if (EffectiveValue(f, v.Status) is not { } value) continue;
                sources.Add(new ConflictSource(f.FieldKey, value, "document", doc.Id, doc.Title, MerchWire.DocumentType(doc.Type),
                    v.Id, v.VersionNumber, MerchWire.VersionStatus(v.Status), f.Id, f.PageNumber, f.Snippet));
            }
        }
        var orderSources = Sources(order);
        foreach (var key in OrderFieldKeys)
        {
            // Only values a person typed are an independent source; confirmed and resolved values already come from the documents.
            if (GetValue(order, key) is not { } value) continue;
            if (orderSources.TryGetValue(key, out var src) && src.Kind != FieldSourceRecord.Manual) continue;
            sources.Add(new ConflictSource(key, value, "order"));
        }
        return MerchConflictDetector.Detect(sources);
    }

    private static string Describe(DetectedConflict d) => string.Join(" vs ", d.Values.Select(v =>
        $"{MerchFields.Display(d.FieldKey, v.Value)} ({(v.Kind == "order" ? "order record" : $"{v.DocumentTitle} v{v.VersionNumber}")})"));
}
