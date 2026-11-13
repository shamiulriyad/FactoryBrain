using BunonBrain.Domain.Entities;

namespace BunonBrain.Application.Fabric;

/// <summary>Wire names (snake_case) for the fabric and cutting enums.</summary>
public static class FabricWire
{
    public static readonly string[] Qualities = { "uninspected", "passed", "restricted", "rejected" };
    public static readonly string[] InspectionStatuses = { "in_progress", "completed" };
    public static readonly string[] Severities = { "minor", "major", "critical" };
    public static readonly string[] Decisions = { "unreviewed", "accepted", "rejected", "corrected" };
    public static readonly string[] AnalysisStatuses = { "not_requested", "processing", "completed", "failed" };
    public static readonly string[] BatchStatuses = { "draft", "pending_approval", "released", "completed", "cancelled" };
    public static readonly string[] OverrideStatuses = { "open", "withdrawn", "superseded" };
    public static readonly string[] OverrideStates = { "none", "pending", "approved", "rejected", "stale", "same_approver" };
    public static readonly string[] MovementKinds = { "cut", "adjustment" };

    public static string Quality(FabricQualityStatus s) => Qualities[(int)s];
    public static string InspectionStatus(FabricInspectionStatus s) => InspectionStatuses[(int)s];
    public static string Severity(FabricDefectSeverity s) => Severities[(int)s];
    public static string Decision(FabricDefectDecision d) => Decisions[(int)d];
    public static string Source(FabricDefectSource s) => s == FabricDefectSource.Model ? "model" : "inspector";
    public static string Analysis(FabricImageAnalysisStatus s) => AnalysisStatuses[(int)s];
    public static string BatchStatus(CuttingBatchStatus s) => BatchStatuses[(int)s];
    public static string OverrideStatus(FabricOverrideStatus s) => OverrideStatuses[(int)s];
    public static string Override(OverrideState s) => OverrideStates[(int)s];
    public static string Movement(FabricMovementKind k) => MovementKinds[(int)k];

    public static bool TryParseQuality(string? raw, out FabricQualityStatus v) => TryParse(Qualities, raw, out v);
    public static bool TryParseInspectionStatus(string? raw, out FabricInspectionStatus v) => TryParse(InspectionStatuses, raw, out v);
    public static bool TryParseSeverity(string? raw, out FabricDefectSeverity v) => TryParse(Severities, raw, out v);
    public static bool TryParseDecision(string? raw, out FabricDefectDecision v) => TryParse(Decisions, raw, out v);
    public static bool TryParseBatchStatus(string? raw, out CuttingBatchStatus v) => TryParse(BatchStatuses, raw, out v);

    private static bool TryParse<T>(string[] names, string? raw, out T value) where T : struct, Enum
    {
        var i = Array.IndexOf(names, raw?.Trim().ToLowerInvariant());
        value = i < 0 ? default : (T)(object)i;
        return i >= 0;
    }
}
