using BunonBrain.Domain.Entities;

namespace BunonBrain.Application.Fabric;

/// <summary>A verified defect as the rules see it (rejected predictions are never passed in).</summary>
public sealed record ScoredDefect(Guid Id, string CategoryCode, FabricDefectSeverity Severity, RollLocation Location, bool CutOut);

/// <summary>Points of one defect; <see cref="Points"/> is null when its size is unknown (not scored).</summary>
public sealed record DefectPoints(Guid Id, int? Points, int? YardIndex);

public sealed record RejectedInterval(double StartM, double EndM);

/// <summary>
/// Usable and rejected length of a roll. Rejected = the union of the along
/// intervals of cut-out defects, clipped to the roll; usable = reference
/// length − rejected. Not calculable (null) while a cut-out defect has no
/// along position or length.
/// </summary>
public sealed record LengthBreakdown(
    double ReferenceLengthM,
    double? RejectedLengthM,
    double? UsableLengthM,
    int CutOutDefects,
    int UnlocatedCutOuts,
    IReadOnlyList<RejectedInterval> RejectedIntervals);

public sealed record QualityInput(
    double ReferenceLengthM,
    double TicketWidthCm,
    double? MeasuredWidthCm,
    double? InspectedLengthM,
    IReadOnlyList<ScoredDefect> Defects);

public sealed record QualityScore(
    int TotalPoints,
    int UncappedPoints,
    int ScoredDefects,
    int UnsizedDefects,
    int DefectsWithoutAlongPosition,
    double InspectedLengthM,
    double WidthCmUsed,
    double InspectedAreaSqYd,
    double? PointsPer100SqYd,
    LengthBreakdown Lengths,
    FabricQualityStatus Recommendation,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<DefectPoints> PerDefect,
    double PassMax,
    double RejectAbove);

/// <summary>
/// The 4-point system (after ASTM D5430) and the roll recommendation.
/// <list type="bullet">
///   <item>Defect size = its largest known extent (along length or across width).
///   Openings (holes): ≤ 1 in → 2 points, larger → 4. Others: ≤ 3 in → 1, ≤ 6 in → 2, ≤ 9 in → 3, larger → 4.</item>
///   <item>No linear yard (counted from the head end) gets more than 4 points. A defect without an along
///   position can't be placed in a yard and is counted in full (never capped).</item>
///   <item>Points per 100 sq yd = total points × 3600 ÷ (inspected yards × cuttable width in inches).</item>
///   <item>Recommendation: above the reject limit → rejected; above the pass limit → restricted; a critical
///   defect left in the usable fabric, a measured width below the ticket by more than the tolerance, or a
///   rejected share above the limit → restricted; otherwise passed.</item>
/// </list>
/// The inspector records the result; the recommendation is advice with its reasons.
/// </summary>
public static class FabricQualityRules
{
    public const double InchesPerYard = 36;

    public static int? Points(double? sizeCm, bool isOpening)
    {
        if (sizeCm is not { } cm || !FabricUnits.IsFinite(cm) || cm <= 0) return null;
        var inches = cm / FabricUnits.CmPerInch;
        if (isOpening) return inches <= 1 + 1e-9 ? 2 : 4;
        if (inches <= 3 + 1e-9) return 1;
        if (inches <= 6 + 1e-9) return 2;
        if (inches <= 9 + 1e-9) return 3;
        return 4;
    }

    public static int? YardIndex(double? alongStartM) =>
        alongStartM is { } m && m >= 0 ? (int)Math.Floor(m / FabricUnits.MetresPerYard + 1e-9) : null;

    public static LengthBreakdown Lengths(double referenceLengthM, IEnumerable<ScoredDefect> defects)
    {
        var cutOuts = defects.Where(d => d.CutOut).ToList();
        var unlocated = cutOuts.Count(d => d.Location.AlongStartM is null || d.Location.AlongLengthCm is null);
        var intervals = cutOuts
            .Where(d => d.Location.AlongStartM is not null && d.Location.AlongLengthCm is not null)
            .Select(d => (Start: Math.Clamp(d.Location.AlongStartM!.Value, 0, referenceLengthM),
                          End: Math.Clamp(d.Location.AlongStartM!.Value + d.Location.AlongLengthCm!.Value / 100, 0, referenceLengthM)))
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start)
            .ToList();
        var merged = new List<RejectedInterval>();
        foreach (var (s, e) in intervals)
        {
            if (merged.Count > 0 && s <= merged[^1].EndM) merged[^1] = merged[^1] with { EndM = Math.Max(merged[^1].EndM, e) };
            else merged.Add(new RejectedInterval(s, e));
        }
        var rounded = merged.Select(i => new RejectedInterval(FabricUnits.RoundMm(i.StartM), FabricUnits.RoundMm(i.EndM))).ToList();
        if (unlocated > 0) return new LengthBreakdown(referenceLengthM, null, null, cutOuts.Count, unlocated, rounded);
        var rejected = FabricUnits.RoundMm(merged.Sum(i => i.EndM - i.StartM));
        return new LengthBreakdown(referenceLengthM, rejected, FabricUnits.RoundMm(Math.Max(0, referenceLengthM - rejected)), cutOuts.Count, 0, rounded);
    }

    public static QualityScore Score(QualityInput input, FabricOptions options)
    {
        var per = new List<DefectPoints>();
        foreach (var d in input.Defects)
        {
            var category = FabricTaxonomy.Find(d.CategoryCode);
            per.Add(new DefectPoints(d.Id, Points(d.Location.SizeCm, category?.IsOpening ?? false), YardIndex(d.Location.AlongStartM)));
        }
        var scored = per.Where(p => p.Points is not null).ToList();
        var uncapped = scored.Sum(p => p.Points!.Value);
        var capped = scored.Where(p => p.YardIndex is not null).GroupBy(p => p.YardIndex!.Value).Sum(g => Math.Min(4, g.Sum(p => p.Points!.Value)))
                     + scored.Where(p => p.YardIndex is null).Sum(p => p.Points!.Value);

        var inspected = input.InspectedLengthM ?? input.ReferenceLengthM;
        var width = FabricGeometry.CuttableWidth(input.TicketWidthCm, input.MeasuredWidthCm);
        var areaSqYd = FabricUnits.MetresToYards(inspected) * FabricUnits.CmToInches(width) / InchesPerYard;
        double? per100 = areaSqYd > 0 ? FabricUnits.Round(capped * 100 / areaSqYd, 1) : null;

        var lengths = Lengths(input.ReferenceLengthM, input.Defects);
        var reasons = new List<string>();
        var rec = FabricQualityStatus.Passed;
        void Raise(FabricQualityStatus to, string why)
        {
            reasons.Add(why);
            if (to > rec) rec = to;
        }

        if (per100 is { } p)
        {
            if (p > options.RejectAbovePointsPer100SqYd)
                Raise(FabricQualityStatus.Rejected, $"{p:0.#} points per 100 sq yd is above the reject limit ({options.RejectAbovePointsPer100SqYd:0.#}).");
            else if (p > options.PassMaxPointsPer100SqYd)
                Raise(FabricQualityStatus.Restricted, $"{p:0.#} points per 100 sq yd is above the pass limit ({options.PassMaxPointsPer100SqYd:0.#}).");
        }
        var criticalKept = input.Defects.Count(d => d.Severity == FabricDefectSeverity.Critical && !d.CutOut);
        if (criticalKept > 0)
            Raise(FabricQualityStatus.Restricted, $"{criticalKept} critical defect{(criticalKept == 1 ? " is" : "s are")} not marked to be cut out.");
        if (input.MeasuredWidthCm is { } mw && mw < input.TicketWidthCm - options.WidthToleranceCm)
            Raise(FabricQualityStatus.Restricted, $"Measured cuttable width {mw:0.#} cm is more than {options.WidthToleranceCm:0.#} cm below the ticket width ({input.TicketWidthCm:0.#} cm).");
        if (lengths.RejectedLengthM is { } rej && input.ReferenceLengthM > 0)
        {
            var share = rej / input.ReferenceLengthM * 100;
            if (share > options.MaxRejectedPct)
                Raise(FabricQualityStatus.Restricted, $"{share:0.#} % of the length is cut out (limit {options.MaxRejectedPct:0.#} %).");
        }
        var unsized = per.Count(x => x.Points is null);
        if (unsized > 0) reasons.Add($"{unsized} defect{(unsized == 1 ? " has" : "s have")} no size and {(unsized == 1 ? "is" : "are")} not scored.");
        if (lengths.UnlocatedCutOuts > 0)
            reasons.Add($"{lengths.UnlocatedCutOuts} cut-out defect{(lengths.UnlocatedCutOuts == 1 ? " has" : "s have")} no along position or length, so usable length can't be calculated.");
        if (reasons.Count == 0) reasons.Add("Within the pass limit; no restricting finding.");

        return new QualityScore(capped, uncapped, scored.Count, unsized, per.Count(x => x.YardIndex is null && x.Points is not null),
            FabricUnits.RoundMm(inspected), width, FabricUnits.Round(areaSqYd, 2), per100, lengths, rec, reasons, per,
            options.PassMaxPointsPer100SqYd, options.RejectAbovePointsPer100SqYd);
    }
}
