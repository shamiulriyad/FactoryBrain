namespace FactoryBrain.Application.Fabric;

/// <summary>A marker as the calculations see it. <see cref="Ratio"/>: garments of each size per ply.</summary>
public sealed record MarkerSpec(
    Guid Id, string Name, double LengthM, double WidthCm, double? EfficiencyPct, double EndAllowanceCm, int PlannedPlies,
    IReadOnlyDictionary<string, int> Ratio)
{
    public int GarmentsPerPly => Ratio.Values.Sum();
    /// <summary>Planned fabric per ply: marker length plus the end allowance, metres.</summary>
    public double PlannedPerPlyM => LengthM + EndAllowanceCm / 100;
}

/// <summary>One recorded cut: plies of one marker spread from one roll of the given cuttable width.</summary>
public sealed record CutRecord(Guid RollId, Guid MarkerId, int Plies, double LengthM, double EndBitM, double RollWidthCm);

public sealed record MarkerUsage(
    Guid MarkerId, string Name, int PlannedPlies, int ActualPlies, double PlannedConsumptionM, double StandardForActualM,
    double ActualConsumptionM, double VarianceVsStandardM);

/// <summary>
/// Planned and actual consumption, utilisation, waste and variance of a
/// cutting batch. All lengths in metres, areas in m², percentages to one
/// decimal. A ratio with a non-positive denominator, or one that needs a
/// value nobody supplied (e.g. a marker efficiency), is null — never 0.
/// </summary>
public sealed record CuttingMetricsResult(
    double PlannedConsumptionM,
    int PlannedGarments,
    double? PlannedPerGarmentM,
    int ActualPlies,
    int ActualGarments,
    double ActualConsumptionM,
    double EndBitsM,
    double StandardForActualM,
    double VarianceM,
    double? VariancePct,
    double UsageVarianceM,
    double? UsageVariancePct,
    double? ActualPerGarmentM,
    double MarkerAreaM2,
    double? PatternAreaM2,
    double FabricAreaM2,
    double? UtilizationPct,
    double? MarkerEfficiencyPct,
    double? WasteAreaM2,
    double? WastePct,
    double? MarkerInterstitialAreaM2,
    double WidthExcessAreaM2,
    double EndLossAreaM2,
    double LengthLossM,
    double? LengthLossPct,
    IReadOnlyDictionary<string, int> CutBySize,
    IReadOnlyList<MarkerUsage> Markers,
    IReadOnlyList<string> Notes);

/// <summary>Book balance of one roll from its live movements.</summary>
public sealed record RollBalance(
    double ReferenceLengthM, double CutM, double EndBitsM, double AdjustmentsM, double BookRemainingM, double? RejectedLengthM, double? UsableRemainingM);

/// <summary>
/// The cutting formulas (also returned to the client as text):
/// <list type="bullet">
///   <item>Planned consumption = Σ planned plies × (marker length + end allowance).</item>
///   <item>Actual consumption = Σ (length that left the roll for the plies + end bits) over the batch's live cuts.</item>
///   <item>Material variance = actual − planned (same unit); usage variance = actual − standard for the plies
///   actually cut (Σ actual plies × (marker length + end allowance)).</item>
///   <item>Marker area = Σ actual plies × marker length × marker width; pattern area = marker area × marker
///   efficiency (as reported by CAD).</item>
///   <item>Fabric area consumed = Σ (length + end bit) × the roll's cuttable width.</item>
///   <item>Fabric utilisation % = pattern area ÷ fabric area consumed × 100; waste = fabric area − pattern area,
///   split into width excess (roll wider than the marker), end loss (length beyond the marker lengths) and
///   marker interstices (marker area − pattern area).</item>
/// </list>
/// Defect-free length (inspection), marker efficiency (CAD) and fabric utilisation (this) are different
/// figures and are reported separately.
/// </summary>
public static class CuttingMetrics
{
    public const string Definitions =
        "Planned consumption = Σ planned plies × (marker length + end allowance). " +
        "Actual consumption = Σ fabric taken off the rolls for the batch (ply lengths, end losses and end bits). " +
        "Material variance = actual − planned; usage variance = actual − standard for the plies actually cut. " +
        "Fabric utilisation % = pattern area (marker area × CAD marker efficiency) ÷ fabric area consumed (length × roll cuttable width) × 100. " +
        "Waste = fabric area consumed − pattern area.";

    public static CuttingMetricsResult Calculate(IReadOnlyList<MarkerSpec> markers, IReadOnlyList<CutRecord> cuts)
    {
        var byId = markers.ToDictionary(m => m.Id);
        var notes = new List<string>();
        var planned = markers.Sum(m => m.PlannedPlies * m.PlannedPerPlyM);
        var plannedGarments = markers.Sum(m => m.PlannedPlies * m.GarmentsPerPly);

        var known = cuts.Where(c => byId.ContainsKey(c.MarkerId)).ToList();
        if (known.Count < cuts.Count) notes.Add($"{cuts.Count - known.Count} cut(s) refer to a marker that no longer exists and are left out.");

        var actual = known.Sum(c => c.LengthM + c.EndBitM);
        var endBits = known.Sum(c => c.EndBitM);
        var plies = known.Sum(c => c.Plies);
        var garments = known.Sum(c => c.Plies * byId[c.MarkerId].GarmentsPerPly);
        var standard = known.Sum(c => c.Plies * byId[c.MarkerId].PlannedPerPlyM);
        var plyLength = known.Sum(c => c.Plies * byId[c.MarkerId].LengthM);

        var markerArea = known.Sum(c => FabricUnits.AreaM2(c.Plies * byId[c.MarkerId].LengthM, byId[c.MarkerId].WidthCm));
        var usedMarkers = known.Select(c => byId[c.MarkerId]).DistinctBy(m => m.Id).ToList();
        var missingEff = usedMarkers.Where(m => m.EfficiencyPct is null).Select(m => m.Name).ToList();
        double? patternArea = null;
        if (known.Count > 0 && missingEff.Count == 0)
            patternArea = known.Sum(c => FabricUnits.AreaM2(c.Plies * byId[c.MarkerId].LengthM, byId[c.MarkerId].WidthCm) * byId[c.MarkerId].EfficiencyPct!.Value / 100);
        if (missingEff.Count > 0)
            notes.Add($"Utilisation and waste need the CAD marker efficiency of every marker cut; missing for {string.Join(", ", missingEff)}.");

        var fabricArea = known.Sum(c => FabricUnits.AreaM2(c.LengthM + c.EndBitM, c.RollWidthCm));
        var widthExcess = known.Sum(c => FabricUnits.AreaM2(c.Plies * byId[c.MarkerId].LengthM, Math.Max(0, c.RollWidthCm - byId[c.MarkerId].WidthCm)));
        var endLoss = known.Sum(c => FabricUnits.AreaM2(Math.Max(0, c.LengthM + c.EndBitM - c.Plies * byId[c.MarkerId].LengthM), c.RollWidthCm));
        if (known.Count == 0) notes.Add("No cuts are recorded yet: actual consumption, utilisation and waste start with the first cut.");

        double? Pct(double num, double den) => den > 0 ? FabricUnits.Round(num / den * 100, 1) : null;

        var usage = markers.Select(m =>
        {
            var mc = known.Where(c => c.MarkerId == m.Id).ToList();
            var actualPlies = mc.Sum(c => c.Plies);
            var std = actualPlies * m.PlannedPerPlyM;
            var used = mc.Sum(c => c.LengthM + c.EndBitM);
            return new MarkerUsage(m.Id, m.Name, m.PlannedPlies, actualPlies, R(m.PlannedPlies * m.PlannedPerPlyM), R(std), R(used), R(used - std));
        }).ToList();

        var bySize = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in known)
            foreach (var (size, n) in byId[c.MarkerId].Ratio)
                bySize[size] = bySize.GetValueOrDefault(size) + c.Plies * n;

        return new CuttingMetricsResult(
            R(planned), plannedGarments, plannedGarments > 0 ? FabricUnits.Round(planned / plannedGarments, 4) : null,
            plies, garments, R(actual), R(endBits), R(standard),
            R(actual - planned), Pct(actual - planned, planned),
            R(actual - standard), Pct(actual - standard, standard),
            garments > 0 ? FabricUnits.Round(actual / garments, 4) : null,
            A(markerArea), patternArea is { } pa ? A(pa) : null, A(fabricArea),
            patternArea is { } p1 ? Pct(p1, fabricArea) : null,
            patternArea is { } p2 ? Pct(p2, markerArea) : null,
            patternArea is { } p3 ? A(fabricArea - p3) : null,
            patternArea is { } p4 ? Pct(fabricArea - p4, fabricArea) : null,
            patternArea is { } p5 ? A(markerArea - p5) : null,
            A(widthExcess), A(endLoss),
            R(actual - plyLength), Pct(actual - plyLength, actual),
            bySize, usage, notes);
    }

    /// <summary>
    /// Book balance of a roll: reference length − cut lengths − end bits + adjustments. The usable remainder is
    /// conservative: it assumes every cut-out region is still on the roll (null when rejected length is unknown).
    /// </summary>
    public static RollBalance Balance(double referenceLengthM, double cutM, double endBitsM, double adjustmentsM, double? rejectedLengthM)
    {
        var book = R(referenceLengthM - cutM - endBitsM + adjustmentsM);
        return new RollBalance(R(referenceLengthM), R(cutM), R(endBitsM), R(adjustmentsM), book, rejectedLengthM,
            rejectedLengthM is { } rej ? R(Math.Max(0, book - rej)) : null);
    }

    private static double R(double metres) => FabricUnits.RoundMm(metres);
    private static double A(double m2) => FabricUnits.Round(m2, 3);
}
