using System.Globalization;
using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Application.Merchandising;

/// <summary>Wire names (snake_case) for the merchandising enums.</summary>
public static class MerchWire
{
    public static readonly string[] OrderStatuses = { "active", "shipped", "closed", "cancelled" };
    public static readonly string[] MilestoneStatuses = { "pending", "done", "waived" };
    public static readonly string[] DocumentTypes = { "purchase_order", "tech_pack", "measurement_sheet", "buyer_instruction", "other" };
    public static readonly string[] VersionStatuses = { "pending", "processing", "needs_review", "confirmed", "failed" };
    public static readonly string[] FieldSources = { "model", "rules", "reviewer" };
    public static readonly string[] Decisions = { "unreviewed", "accepted", "corrected", "rejected", "unresolved" };
    public static readonly string[] ConflictStatuses = { "open", "resolved", "cleared" };

    public static readonly IReadOnlyDictionary<string, string> DocumentTypeLabels = new Dictionary<string, string>
    {
        ["purchase_order"] = "Purchase order",
        ["tech_pack"] = "Tech pack",
        ["measurement_sheet"] = "Measurement / spec sheet",
        ["buyer_instruction"] = "Buyer instruction",
        ["other"] = "Other",
    };

    public static string OrderStatus(MerchOrderStatus s) => OrderStatuses[(int)s];
    public static bool TryParseOrderStatus(string? raw, out MerchOrderStatus s) => TryParse(OrderStatuses, raw, out s);

    public static string MilestoneStatus(MerchMilestoneStatus s) => MilestoneStatuses[(int)s];
    public static bool TryParseMilestoneStatus(string? raw, out MerchMilestoneStatus s) => TryParse(MilestoneStatuses, raw, out s);

    public static string DocumentType(MerchDocumentType t) => DocumentTypes[(int)t];
    public static bool TryParseDocumentType(string? raw, out MerchDocumentType t) => TryParse(DocumentTypes, raw, out t);

    public static string VersionStatus(MerchVersionStatus s) => VersionStatuses[(int)s];
    public static bool TryParseVersionStatus(string? raw, out MerchVersionStatus s) => TryParse(VersionStatuses, raw, out s);

    public static string Source(MerchFieldSource s) => FieldSources[(int)s];

    public static string Decision(MerchFieldDecision d) => Decisions[(int)d];
    public static bool TryParseDecision(string? raw, out MerchFieldDecision d) => TryParse(Decisions, raw, out d);

    public static string ConflictStatus(MerchConflictStatus s) => ConflictStatuses[(int)s];
    public static bool TryParseConflictStatus(string? raw, out MerchConflictStatus s) => TryParse(ConflictStatuses, raw, out s);

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseIsoDate(string? raw, out DateOnly date) =>
        DateOnly.TryParseExact(raw?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryParse<T>(string[] names, string? raw, out T value) where T : struct, Enum
    {
        var i = Array.IndexOf(names, raw?.Trim().ToLowerInvariant());
        value = i < 0 ? default : (T)(object)i;
        return i >= 0;
    }
}
