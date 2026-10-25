using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Application.Qc;

/// <summary>One inspection, as the summary needs it.</summary>
public sealed record QcMetricInspection(
    Guid Id, DateTime CreatedAt, string? LineId, QcInspectionStatus Status, QcInspectionOutcome? Outcome);

/// <summary>One finding, as the summary needs it (effective category / severity already resolved).</summary>
public sealed record QcMetricFinding(
    Guid InspectionId, QcFindingSource Source, QcFindingDecision Decision, string? CategoryCode, QcDefectSeverity Severity);

/// <summary>
/// Quality metrics from persisted inspections only. Verified numbers count
/// reviewed inspections and the findings an inspector accepted, corrected
/// or added; unreviewed model predictions never count as defects. Days are
/// UTC calendar days of the inspection's submission time.
/// </summary>
public static class QcMetricsCalculator
{
    public const int RecurringThreshold = 3;

    public static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>
    {
        ["sample"] = "Inspections submitted in the selected range (by submission time, UTC days), after filters.",
        ["defectiveRatePct"] = "Reviewed inspections with at least one verified defect ÷ reviewed inspections × 100.",
        ["dhu"] = "Defects per hundred units: verified defects ÷ reviewed inspections × 100.",
        ["rejectOrReworkRatePct"] = "Reviewed inspections with outcome Rework or Reject ÷ reviewed inspections × 100.",
        ["verifiedDefect"] = "A finding an inspector added, or a model prediction an inspector accepted or corrected. Unreviewed and rejected predictions are not defects.",
        ["acceptedPct"] = "Model predictions accepted unchanged ÷ model predictions an inspector reviewed × 100. Reflects agreement with inspectors, not calibrated accuracy.",
        ["recurring"] = $"A line and defect category with at least {RecurringThreshold} verified defects in the range.",
    };

    public static bool IsVerified(QcFindingSource source, QcFindingDecision decision) =>
        source == QcFindingSource.Inspector
            ? decision != QcFindingDecision.Rejected
            : decision is QcFindingDecision.Accepted or QcFindingDecision.Corrected;

    public static QcSummaryResponse Calculate(
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<QcMetricInspection> inspections,
        IReadOnlyList<QcMetricFinding> findings,
        IReadOnlyDictionary<string, string> categoryNames,
        DateTime generatedAt)
    {
        var reviewed = inspections.Where(i => i.Status == QcInspectionStatus.Reviewed).ToList();
        var reviewedIds = reviewed.Select(i => i.Id).ToHashSet();

        // Verified defects only count on reviewed inspections.
        var verified = findings
            .Where(f => reviewedIds.Contains(f.InspectionId) && IsVerified(f.Source, f.Decision))
            .ToList();
        var defectsByInspection = verified.GroupBy(f => f.InspectionId).ToDictionary(g => g.Key, g => g.Count());
        var defective = reviewed.Count(i => defectsByInspection.ContainsKey(i.Id));

        var totals = new QcSummaryTotals(
            Inspections: inspections.Count,
            InProgress: inspections.Count(i => i.Status is QcInspectionStatus.Pending or QcInspectionStatus.Processing),
            Failed: inspections.Count(i => i.Status == QcInspectionStatus.Failed),
            AwaitingReview: inspections.Count(i => i.Status == QcInspectionStatus.Completed),
            Reviewed: reviewed.Count,
            Passed: reviewed.Count(i => i.Outcome == QcInspectionOutcome.Pass),
            Rework: reviewed.Count(i => i.Outcome == QcInspectionOutcome.Rework),
            Rejected: reviewed.Count(i => i.Outcome == QcInspectionOutcome.Reject),
            VerifiedDefects: verified.Count,
            DefectiveRatePct: Pct(defective, reviewed.Count),
            Dhu: Pct(verified.Count, reviewed.Count),
            RejectOrReworkRatePct: Pct(reviewed.Count(i => i.Outcome is QcInspectionOutcome.Rework or QcInspectionOutcome.Reject), reviewed.Count));

        var modelOnReviewed = findings.Where(f => f.Source == QcFindingSource.Model && reviewedIds.Contains(f.InspectionId)).ToList();
        var decided = modelOnReviewed.Where(f => f.Decision != QcFindingDecision.Unreviewed).ToList();
        var accepted = decided.Count(f => f.Decision == QcFindingDecision.Accepted);
        var model = new QcModelAgreement(
            PredictionsReviewed: decided.Count,
            Accepted: accepted,
            Corrected: decided.Count(f => f.Decision == QcFindingDecision.Corrected),
            Rejected: decided.Count(f => f.Decision == QcFindingDecision.Rejected),
            AcceptedPct: Pct(accepted, decided.Count),
            InspectorAdded: findings.Count(f => f.Source == QcFindingSource.Inspector && reviewedIds.Contains(f.InspectionId)
                                                && f.Decision != QcFindingDecision.Rejected),
            AwaitingReview: findings.Count(f => f.Source == QcFindingSource.Model && !reviewedIds.Contains(f.InspectionId)));

        string NameOf(string? code) => code is null ? "Unmapped" : categoryNames.TryGetValue(code, out var n) ? n : code;

        var byCategory = verified
            .GroupBy(f => f.CategoryCode)
            .Select(g => new QcCategoryCount(g.Key, NameOf(g.Key), g.Count(), Pct(g.Count(), verified.Count) ?? 0))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var bySeverity = new QcSeverityCounts(
            verified.Count(f => f.Severity == QcDefectSeverity.Minor),
            verified.Count(f => f.Severity == QcDefectSeverity.Major),
            verified.Count(f => f.Severity == QcDefectSeverity.Critical));

        var lineOf = inspections.ToDictionary(i => i.Id, i => string.IsNullOrWhiteSpace(i.LineId) ? "unassigned" : i.LineId!);
        var byLine = reviewed
            .GroupBy(i => lineOf[i.Id])
            .Select(g =>
            {
                var ids = g.Select(i => i.Id).ToList();
                var lineDefective = ids.Count(defectsByInspection.ContainsKey);
                return new QcLineStats(g.Key, ids.Count, lineDefective, Pct(lineDefective, ids.Count),
                    ids.Sum(id => defectsByInspection.GetValueOrDefault(id)));
            })
            .OrderByDescending(l => l.DefectiveRatePct ?? 0).ThenBy(l => l.LineId, StringComparer.Ordinal)
            .ToList();

        var daily = new List<QcDailyPoint>();
        for (var day = fromUtc.Date; day <= toUtc.Date; day = day.AddDays(1))
        {
            var onDay = inspections.Where(i => i.CreatedAt.Date == day).ToList();
            var reviewedOnDay = onDay.Where(i => reviewedIds.Contains(i.Id)).ToList();
            var defectiveOnDay = reviewedOnDay.Count(i => defectsByInspection.ContainsKey(i.Id));
            daily.Add(new QcDailyPoint(
                day.ToString("yyyy-MM-dd"),
                onDay.Count,
                reviewedOnDay.Count,
                defectiveOnDay,
                reviewedOnDay.Sum(i => defectsByInspection.GetValueOrDefault(i.Id)),
                Pct(defectiveOnDay, reviewedOnDay.Count)));
        }

        var recurring = verified
            .GroupBy(f => (Line: lineOf[f.InspectionId], f.CategoryCode))
            .Where(g => g.Count() >= RecurringThreshold)
            .Select(g => new QcRecurringIssue(g.Key.Line, g.Key.CategoryCode, NameOf(g.Key.CategoryCode),
                g.Count(), g.Select(f => f.InspectionId).Distinct().Count()))
            .OrderByDescending(r => r.Defects).ThenBy(r => r.LineId, StringComparer.Ordinal)
            .ToList();

        return new QcSummaryResponse(fromUtc, toUtc, totals, model, byCategory, bySeverity, byLine, daily, recurring,
            RecurringThreshold, Definitions, generatedAt);
    }

    private static double? Pct(int part, int whole) => whole <= 0 ? null : Math.Round(part * 100.0 / whole, 2);
}
