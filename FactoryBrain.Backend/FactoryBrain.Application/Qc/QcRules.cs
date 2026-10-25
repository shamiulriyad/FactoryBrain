using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Application.Qc;

/// <summary>Snake_case wire names for the inspection enums, and their parsers.</summary>
public static class QcWire
{
    public static string Status(QcInspectionStatus s) => s switch
    {
        QcInspectionStatus.Pending    => "pending",
        QcInspectionStatus.Processing => "processing",
        QcInspectionStatus.Completed  => "completed",
        QcInspectionStatus.Failed     => "failed",
        QcInspectionStatus.Reviewed   => "reviewed",
        _ => s.ToString().ToLowerInvariant(),
    };

    public static string Stage(QcInspectionStage s) => s switch
    {
        QcInspectionStage.Fabric    => "fabric",
        QcInspectionStage.Cutting   => "cutting",
        QcInspectionStage.Inline    => "inline",
        QcInspectionStage.EndOfLine => "end_of_line",
        QcInspectionStage.Final     => "final",
        _ => s.ToString().ToLowerInvariant(),
    };

    public static string Severity(QcDefectSeverity s) => s.ToString().ToLowerInvariant();
    public static string Source(QcFindingSource s) => s.ToString().ToLowerInvariant();
    public static string Decision(QcFindingDecision d) => d.ToString().ToLowerInvariant();
    public static string Outcome(QcInspectionOutcome o) => o.ToString().ToLowerInvariant();

    public static readonly IReadOnlyList<string> Stages = Enum.GetValues<QcInspectionStage>().Select(Stage).ToList();
    public static readonly IReadOnlyList<string> Statuses = Enum.GetValues<QcInspectionStatus>().Select(Status).ToList();
    public static readonly IReadOnlyList<string> Severities = Enum.GetValues<QcDefectSeverity>().Select(Severity).ToList();
    public static readonly IReadOnlyList<string> Outcomes = Enum.GetValues<QcInspectionOutcome>().Select(Outcome).ToList();

    public static bool TryParseStatus(string? raw, out QcInspectionStatus value) => TryParse(raw, Status, out value);
    public static bool TryParseStage(string? raw, out QcInspectionStage value) => TryParse(raw, Stage, out value);
    public static bool TryParseSeverity(string? raw, out QcDefectSeverity value) => TryParse(raw, Severity, out value);
    public static bool TryParseOutcome(string? raw, out QcInspectionOutcome value) => TryParse(raw, Outcome, out value);

    /// <summary>Only the decisions an inspector may submit (not <c>unreviewed</c>).</summary>
    public static bool TryParseDecision(string? raw, out QcFindingDecision value)
        => TryParse(raw, Decision, out value) && value != QcFindingDecision.Unreviewed;

    private static bool TryParse<T>(string? raw, Func<T, string> wire, out T value) where T : struct, Enum
    {
        var s = raw?.Trim().ToLowerInvariant();
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (wire(candidate) == s) { value = candidate; return true; }
        }
        value = default;
        return false;
    }
}

/// <summary>
/// Allowed inspection state changes. Every write in the service checks the
/// current state with these rules and applies the change conditionally, so
/// a stale caller can't move an inspection backwards.
/// </summary>
public static class QcLifecycle
{
    public static bool CanTransition(QcInspectionStatus from, QcInspectionStatus to) => (from, to) switch
    {
        (QcInspectionStatus.Pending,    QcInspectionStatus.Processing) => true,
        // A lease that outlived the processing timeout is reclaimed.
        (QcInspectionStatus.Processing, QcInspectionStatus.Processing) => true,
        (QcInspectionStatus.Processing, QcInspectionStatus.Completed)  => true,
        (QcInspectionStatus.Processing, QcInspectionStatus.Failed)     => true,
        (QcInspectionStatus.Pending,    QcInspectionStatus.Failed)     => true,  // no provider configured
        (QcInspectionStatus.Failed,     QcInspectionStatus.Pending)    => true,  // retry
        (QcInspectionStatus.Completed,  QcInspectionStatus.Reviewed)   => true,
        (QcInspectionStatus.Failed,     QcInspectionStatus.Reviewed)   => true,  // manual inspection without AI
        (QcInspectionStatus.Reviewed,   QcInspectionStatus.Reviewed)   => true,  // corrected review
        _ => false,
    };

    public static bool CanReview(QcInspectionStatus s) => CanTransition(s, QcInspectionStatus.Reviewed);
    public static bool CanRetry(QcInspectionStatus s) => CanTransition(s, QcInspectionStatus.Pending);
}

/// <summary>Input limits shared by validators, the service and the API docs.</summary>
public static class QcLimits
{
    public const int ProductTypeMax = 64;
    public const int LineIdMax = 32;
    public const int ProductionOrderMax = 64;
    public const int NotesMax = 1000;
    public const int IdempotencyKeyMax = 128;
    public const int FileNameMax = 255;
    public const int MaxCategoriesPerRequest = 50;
    public const int MaxFindingsPerInspection = 100;
    public const int MaxAdditionsPerReview = 50;
    public const int ReviewNoteMax = 500;
    public const int MaxPageSize = 100;
    public const int MaxSummaryDays = 366;
    public const int DefaultSummaryDays = 30;
    /// <summary>Largest width or height accepted (decompression-bomb guard).</summary>
    public const int MaxImageDimension = 12_000;
    /// <summary>Hard ceiling on the configurable upload limit.</summary>
    public const long MaxImageBytesCeiling = 20L * 1024 * 1024;

    /// <summary>Production line ids, order numbers: letters, digits and <c>- _ . / #</c>.</summary>
    public const string SafeTokenPattern = @"^[A-Za-z0-9][A-Za-z0-9 _./#-]*$";
    /// <summary>Category codes: lowercase slug.</summary>
    public const string CategoryCodePattern = @"^[a-z][a-z0-9_]{1,39}$";
}
