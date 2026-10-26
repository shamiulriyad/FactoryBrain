using System.Globalization;
using FactoryBrain.Application.Dtos.Merchandising;
using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Application.Merchandising;

public sealed record AttentionOrder(
    string Buyer, string BuyerOrderNumber, string? Style, int? Quantity, string? Unit, DateOnly? DeliveryDate,
    MerchOrderStatus Status, IReadOnlyList<BreakdownRow>? Breakdown, IReadOnlyList<string> RequiredDocumentTypes);

public sealed record AttentionMilestone(Guid Id, string Name, DateOnly Due, MerchMilestoneStatus Status);

/// <summary>A document and the state of its current version.</summary>
public sealed record AttentionDocument(
    Guid DocumentId, string Title, string Type, Guid? VersionId, int? VersionNumber, MerchVersionStatus? Status,
    string? FailureCode, string? ExtractionCode, IReadOnlyList<string> UnresolvedKeys);

public sealed record AttentionConflict(Guid Id, string FieldKey, int ValueCount);

public sealed record AttentionStaleField(string FieldKey, string DocumentTitle, int VersionNumber);

public sealed record AttentionInput(
    AttentionOrder Order,
    IReadOnlyList<AttentionMilestone> Milestones,
    IReadOnlyList<AttentionDocument> Documents,
    IReadOnlyList<AttentionConflict> OpenConflicts,
    IReadOnlyList<AttentionStaleField> StaleFields,
    string? ProductionOrderNumber,
    int? GoodSewingOutput,
    IReadOnlyList<string> ProductionDifferences,
    DateOnly Today);

public sealed record AttentionResult(IReadOnlyList<AttentionItemDto> Items, IReadOnlyList<RequiredDocumentDto> RequiredDocuments, MerchSummaryDto Summary);

/// <summary>
/// The prioritised list of missing information and deadlines for one order,
/// and its plain-language summary — both composed from the stored records by
/// fixed rules (no model is involved). Priorities:
/// <list type="bullet">
///   <item>high: an overdue milestone; an active order past its delivery date; an open conflict on
///   quantity, delivery date, style, PO number or breakdown; a required document missing with delivery
///   within <see cref="UrgentDays"/> days (or no delivery date); a confirmed breakdown that doesn't add up
///   to the order quantity.</item>
///   <item>medium: other open conflicts; a missing required document further out; a document awaiting
///   review or whose processing failed; a required order field that is empty; a field left unresolved
///   on a confirmed document; a difference from the linked production order; a milestone due within 2 days.</item>
///   <item>low: a milestone due within <see cref="UpcomingDays"/> days; delivery within 14 days; an order value
///   whose source document version has been replaced.</item>
/// </list>
/// Within a priority, items are ordered by due date.
/// </summary>
public static class MerchAttention
{
    public const int UrgentDays = 30;
    public const int UpcomingDays = 7;

    private static readonly HashSet<string> CriticalConflictFields = new()
    {
        MerchFields.Quantity, MerchFields.DeliveryDate, MerchFields.Style, MerchFields.OrderNumber, MerchFields.Breakdown,
    };

    public static AttentionResult Build(AttentionInput input)
    {
        var o = input.Order;
        var today = input.Today;
        var items = new List<AttentionItemDto>();
        var active = o.Status == MerchOrderStatus.Active;
        int? daysToDelivery = o.DeliveryDate is { } dd ? dd.DayNumber - today.DayNumber : null;

        void Add(string kind, string priority, string title, string detail, DateOnly? due = null, string? field = null,
            Guid? doc = null, Guid? version = null, Guid? conflict = null, Guid? milestone = null) =>
            items.Add(new AttentionItemDto(kind, priority, title, detail, due is { } d ? MerchWire.Date(d) : null, field, doc, version, conflict, milestone));

        // Deadlines
        if (active && o.DeliveryDate is { } delivery)
        {
            if (delivery < today) Add("delivery_overdue", "high", "Delivery date has passed", $"Delivery was due {MerchWire.Date(delivery)} ({today.DayNumber - delivery.DayNumber} day(s) ago) and the order is still active.", delivery);
            else if (daysToDelivery <= 14) Add("delivery_upcoming", "low", "Delivery is close", $"Delivery is due {MerchWire.Date(delivery)} (in {daysToDelivery} day(s)).", delivery);
        }
        foreach (var m in input.Milestones.Where(m => m.Status == MerchMilestoneStatus.Pending))
        {
            var days = m.Due.DayNumber - today.DayNumber;
            if (!active) continue;
            if (days < 0) Add("milestone_overdue", "high", $"Overdue: {m.Name}", $"Was due {MerchWire.Date(m.Due)} ({-days} day(s) ago).", m.Due, milestone: m.Id);
            else if (days <= UpcomingDays)
                Add("milestone_upcoming", days <= 2 ? "medium" : "low", $"Due soon: {m.Name}", days == 0 ? "Due today." : $"Due {MerchWire.Date(m.Due)} (in {days} day(s)).", m.Due, milestone: m.Id);
        }

        // Required documents
        var required = new List<RequiredDocumentDto>();
        foreach (var type in o.RequiredDocumentTypes.Distinct())
        {
            var label = MerchWire.DocumentTypeLabels.GetValueOrDefault(type, type);
            var docs = input.Documents.Where(d => d.Type == type).ToList();
            var best = docs.OrderBy(d => Rank(d.Status)).FirstOrDefault();
            var state = best?.Status switch
            {
                null => "missing",
                MerchVersionStatus.Confirmed => "confirmed",
                MerchVersionStatus.NeedsReview => "needs_review",
                MerchVersionStatus.Failed => "failed",
                _ => "processing",
            };
            required.Add(new RequiredDocumentDto(type, label, state, best?.DocumentId, best?.VersionId));
            if (state == "missing" && active)
            {
                var urgent = daysToDelivery is null || daysToDelivery <= UrgentDays;
                Add("missing_document", urgent ? "high" : "medium", $"Missing document: {label}",
                    daysToDelivery is null ? "A required document has not been uploaded." : $"Required and not uploaded; delivery is in {daysToDelivery} day(s).");
            }
        }

        // Documents
        foreach (var d in input.Documents)
        {
            if (d.Status == MerchVersionStatus.NeedsReview)
                Add("needs_review", "medium", $"Review extracted values: {d.Title}",
                    d.ExtractionCode is null ? $"Version {d.VersionNumber} is waiting for a person to check and confirm its values."
                        : $"Version {d.VersionNumber}: values were not extracted automatically ({d.ExtractionCode}); enter them from the document.",
                    doc: d.DocumentId, version: d.VersionId);
            else if (d.Status == MerchVersionStatus.Failed)
                Add("processing_failed", "medium", $"Document could not be processed: {d.Title}",
                    $"Version {d.VersionNumber} failed ({d.FailureCode}). Retry it, replace the file, or review it manually.", doc: d.DocumentId, version: d.VersionId);
            else if (d.Status is MerchVersionStatus.Pending or MerchVersionStatus.Processing)
                Add("processing", "low", $"Processing: {d.Title}", $"Version {d.VersionNumber} is being read.", doc: d.DocumentId, version: d.VersionId);
            foreach (var key in d.UnresolvedKeys)
                Add("unresolved_field", "medium", $"Unresolved: {MerchFields.Get(key).Label}", $"Marked unresolved when {d.Title} v{d.VersionNumber} was confirmed; ask the buyer.", field: key, doc: d.DocumentId, version: d.VersionId);
        }

        // Conflicts
        foreach (var c in input.OpenConflicts)
        {
            var label = MerchFields.Get(c.FieldKey).Label;
            Add("conflict", CriticalConflictFields.Contains(c.FieldKey) ? "high" : "medium", $"Conflicting {label.ToLowerInvariant()}",
                $"{c.ValueCount} sources state different values. Choose the correct one.", field: c.FieldKey, conflict: c.Id);
        }

        // Missing order values
        if (active)
        {
            foreach (var (key, empty) in new[]
            {
                (MerchFields.Style, string.IsNullOrEmpty(o.Style)),
                (MerchFields.Quantity, o.Quantity is null),
                (MerchFields.DeliveryDate, o.DeliveryDate is null),
            })
            {
                if (empty) Add("missing_field", "medium", $"Missing: {MerchFields.Get(key).Label}", "The order has no confirmed value for this field.", field: key);
            }
        }

        if (o.Breakdown is { Count: > 0 } rows && o.Quantity is { } qty && MerchFields.BreakdownTotal(rows) != qty)
            Add("breakdown_mismatch", "high", "Breakdown does not add up",
                $"The confirmed colour × size breakdown totals {MerchFields.BreakdownTotal(rows):N0} but the order quantity is {qty:N0}.", field: MerchFields.Breakdown);

        foreach (var diff in input.ProductionDifferences)
            Add("production_mismatch", "medium", "Differs from the production order", diff);

        foreach (var s in input.StaleFields)
            Add("stale_source", "low", $"Re-check: {MerchFields.Get(s.FieldKey).Label}",
                $"The order value was confirmed from {s.DocumentTitle} v{s.VersionNumber}, which has since been replaced.", field: s.FieldKey);

        var ordered = items
            .OrderBy(i => i.Priority switch { "high" => 0, "medium" => 1, _ => 2 })
            .ThenBy(i => i.DueDate ?? "9999-99-99", StringComparer.Ordinal)
            .ThenBy(i => i.Kind, StringComparer.Ordinal)
            .ToList();

        return new AttentionResult(ordered, required, Summary(input, required, daysToDelivery));
    }

    private static int Rank(MerchVersionStatus? s) => s switch
    {
        MerchVersionStatus.Confirmed => 0,
        MerchVersionStatus.NeedsReview => 1,
        MerchVersionStatus.Pending or MerchVersionStatus.Processing => 2,
        MerchVersionStatus.Failed => 3,
        _ => 4,
    };

    private static MerchSummaryDto Summary(AttentionInput input, IReadOnlyList<RequiredDocumentDto> required, int? daysToDelivery)
    {
        var o = input.Order;
        var inv = CultureInfo.InvariantCulture;
        var head = $"{o.Buyer} PO {o.BuyerOrderNumber}";
        var parts = new List<string>();
        if (o.Quantity is { } q) parts.Add(string.Format(inv, "{0:N0} {1}", q, o.Unit ?? "pcs (unit not confirmed)"));
        if (!string.IsNullOrEmpty(o.Style)) parts.Add($"style {o.Style}");
        if (o.DeliveryDate is { } d)
            parts.Add(daysToDelivery switch
            {
                < 0 => $"delivery {MerchWire.Date(d)} ({-daysToDelivery} days ago)",
                0 => $"delivery {MerchWire.Date(d)} (today)",
                _ => $"delivery {MerchWire.Date(d)} (in {daysToDelivery} days)",
            });
        var headline = parts.Count == 0 ? $"{head}: no quantity, style or delivery date confirmed yet." : $"{head}: {string.Join(", ", parts)}.";

        var lines = new List<string>();
        var docs = input.Documents;
        if (docs.Count == 0) lines.Add("No documents uploaded.");
        else
        {
            var confirmed = docs.Count(x => x.Status == MerchVersionStatus.Confirmed);
            var review = docs.Count(x => x.Status == MerchVersionStatus.NeedsReview);
            var failed = docs.Count(x => x.Status == MerchVersionStatus.Failed);
            var processing = docs.Count(x => x.Status is MerchVersionStatus.Pending or MerchVersionStatus.Processing);
            var bits = new List<string> { $"{confirmed} confirmed" };
            if (review > 0) bits.Add($"{review} awaiting review");
            if (processing > 0) bits.Add($"{processing} processing");
            if (failed > 0) bits.Add($"{failed} failed");
            lines.Add($"{docs.Count} document(s): {string.Join(", ", bits)}.");
        }
        var missing = required.Where(r => r.State == "missing").Select(r => r.Label).ToList();
        if (missing.Count > 0) lines.Add($"Missing required documents: {string.Join(", ", missing)}.");

        var ms = input.Milestones;
        var done = ms.Count(m => m.Status is MerchMilestoneStatus.Done or MerchMilestoneStatus.Waived);
        if (ms.Count > 0)
        {
            var next = ms.Where(m => m.Status == MerchMilestoneStatus.Pending).OrderBy(m => m.Due).FirstOrDefault();
            var overdue = ms.Count(m => m.Status == MerchMilestoneStatus.Pending && m.Due < input.Today);
            var text = $"Milestones: {done} of {ms.Count} done";
            if (next is not null) text += $"; next: {next.Name} on {MerchWire.Date(next.Due)}";
            if (overdue > 0) text += $"; {overdue} overdue";
            lines.Add(text + ".");
        }
        else lines.Add("No milestones recorded.");

        if (input.OpenConflicts.Count > 0)
            lines.Add($"{input.OpenConflicts.Count} open conflict(s): {string.Join(", ", input.OpenConflicts.Select(c => MerchFields.Get(c.FieldKey).Label.ToLowerInvariant()))}.");
        if (o.Breakdown is { Count: > 0 } rows)
        {
            var total = MerchFields.BreakdownTotal(rows);
            lines.Add(string.Format(inv, "Breakdown: {0} row(s) totalling {1:N0}{2}.", rows.Count, total,
                o.Quantity is { } qq ? (qq == total ? " (matches the quantity)" : string.Format(inv, " (quantity is {0:N0})", qq)) : ""));
        }
        if (input.ProductionOrderNumber is not null)
            lines.Add(string.Format(inv, "Production order {0}: {1:N0} good pieces sewn{2}.", input.ProductionOrderNumber, input.GoodSewingOutput ?? 0,
                o.Quantity is { } pq ? string.Format(inv, " of {0:N0}", pq) : ""));

        return new MerchSummaryDto(headline, lines, done, ms.Count, daysToDelivery,
            "Composed by fixed rules from the stored order, documents, milestones and conflicts; no AI is involved.");
    }
}
