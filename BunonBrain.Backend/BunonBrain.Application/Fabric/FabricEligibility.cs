using BunonBrain.Domain.Entities;

namespace BunonBrain.Application.Fabric;

/// <summary>Where an override request for a non-passed roll stands.</summary>
public enum OverrideState { None, Pending, Approved, Rejected, Stale, SameApprover }

/// <summary>A roll allocated to a batch, with everything the checks need (all lengths in metres, widths in cm).</summary>
public sealed record RollCheck(
    Guid RollId,
    string RollNumber,
    string LotColor,
    string FabricType,
    string? ShadeGroup,
    FabricQualityStatus Quality,
    bool InspectionInProgress,
    bool IsClosed,
    double CuttableWidthCm,
    double BookRemainingM,
    double? UsableRemainingM,
    int UnlocatedCutOuts,
    /// <summary>Length other open batches still expect from this roll (planned − already cut).</summary>
    double ReservedElsewhereM,
    double PlannedLengthM,
    double CutInBatchM,
    OverrideState Override);

public sealed record BatchCheck(
    string Color,
    bool OrderOpen,
    IReadOnlyList<MarkerSpec> Markers,
    IReadOnlyDictionary<string, int> Requirement,
    IReadOnlyList<RollCheck> Rolls);

/// <param name="Severity"><c>blocking</c> (nothing proceeds until fixed), <c>warning</c> or <c>info</c>.</param>
public sealed record ValidationIssue(string Code, string Severity, string Message, Guid? RollId = null, Guid? MarkerId = null);

public sealed record RollEligibility(Guid RollId, bool Eligible, bool NeedsOverride, OverrideState Override);

public sealed record BatchValidation(
    bool CanSubmit,
    int Blocking,
    int Warnings,
    double PlannedConsumptionM,
    double AllocatedM,
    IReadOnlyDictionary<string, int> PlannedBySize,
    IReadOnlyList<RollEligibility> Rolls,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Pre-cut validation. Only a passed roll is eligible for cutting by itself;
/// an uninspected, restricted or rejected roll is eligible in one batch only
/// with an approved override (requested with a reason, decided in the
/// approval queue). A roll under re-inspection, closed, too narrow for a
/// marker or without enough remaining length is never eligible. Batch-level
/// problems (no markers / rolls, closed order) block submission; the rest
/// are warnings with their reasons.
/// </summary>
public static class FabricEligibility
{
    public const string Blocking = "blocking";
    public const string Warning = "warning";
    public const string Info = "info";
    private const double ToleranceM = 0.005;

    /// <summary>Codes an approved override clears.</summary>
    public static readonly string[] OverridableCodes = { "roll_uninspected", "roll_restricted", "roll_rejected" };

    public static string NormalizeColor(string? c) =>
        new string((c ?? "").Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static bool NeedsOverride(FabricQualityStatus q) => q != FabricQualityStatus.Passed;

    /// <summary>The roll-level issues for one allocated roll (the checks a cut is held to as well).</summary>
    public static List<ValidationIssue> CheckRoll(RollCheck r, double maxMarkerWidthCm, string batchColor)
    {
        var issues = new List<ValidationIssue>();
        void Add(string code, string sev, string msg) => issues.Add(new ValidationIssue(code, sev, msg, r.RollId));
        var label = $"Roll {r.RollNumber}";

        if (NeedsOverride(r.Quality))
        {
            var (code, what) = r.Quality switch
            {
                FabricQualityStatus.Uninspected => ("roll_uninspected", "has not been inspected"),
                FabricQualityStatus.Restricted => ("roll_restricted", "is restricted by its inspection"),
                _ => ("roll_rejected", "was rejected by its inspection"),
            };
            switch (r.Override)
            {
                case OverrideState.Approved:
                    Add("override_approved", Info, $"{label} {what}; an approved override allows it in this batch.");
                    break;
                case OverrideState.Pending:
                    Add(code, Blocking, $"{label} {what}. Its override request is waiting for approval.");
                    break;
                case OverrideState.Rejected:
                    Add(code, Blocking, $"{label} {what}. The override request was rejected; use another roll.");
                    break;
                case OverrideState.Stale:
                    Add(code, Blocking, $"{label} {what}. Its quality changed after the override was requested; request a new override if still needed.");
                    break;
                case OverrideState.SameApprover:
                    Add(code, Blocking, $"{label} {what}. The override was approved by the person who requested it; another reviewer must approve it.");
                    break;
                default:
                    Add(code, Blocking, $"{label} {what}. Inspect it, use another roll, or request an override with a reason.");
                    break;
            }
        }
        if (r.InspectionInProgress)
            Add("inspection_in_progress", Blocking, $"{label} is being (re-)inspected; finish the inspection before cutting it.");
        if (r.IsClosed)
            Add("roll_closed", Blocking, $"{label} is closed (used up or written off).");
        if (maxMarkerWidthCm > 0 && r.CuttableWidthCm + FabricGeometry.AcrossToleranceCm < maxMarkerWidthCm)
            Add("width_too_narrow", Blocking, $"{label} has {r.CuttableWidthCm:0.#} cm cuttable width; the widest marker needs {maxMarkerWidthCm:0.#} cm.");

        var outstanding = Math.Max(0, r.PlannedLengthM - r.CutInBatchM);
        var available = r.BookRemainingM - r.ReservedElsewhereM;
        if (outstanding > available + ToleranceM)
            Add("insufficient_roll_length", Blocking,
                $"{label}: {outstanding:0.###} m still planned but only {Math.Max(0, available):0.###} m is free " +
                $"({r.BookRemainingM:0.###} m on the books{(r.ReservedElsewhereM > 0 ? $", {r.ReservedElsewhereM:0.###} m reserved by other batches" : "")}).");
        else if (r.UsableRemainingM is { } usable && outstanding > usable + ToleranceM)
            Add("exceeds_usable_length", Warning, $"{label}: {outstanding:0.###} m planned, but only {usable:0.###} m remains after the cut-out defect regions.");
        if (r.UnlocatedCutOuts > 0)
            Add("usable_length_unknown", Warning, $"{label} has {r.UnlocatedCutOuts} cut-out defect(s) without an along position, so its usable length is unknown.");
        if (NormalizeColor(r.LotColor) != NormalizeColor(batchColor))
            Add("color_mismatch", Warning, $"{label} is '{r.LotColor}', the batch cuts '{batchColor}'.");
        return issues;
    }

    public static BatchValidation Validate(BatchCheck b)
    {
        var issues = new List<ValidationIssue>();
        var maxWidth = b.Markers.Count == 0 ? 0 : b.Markers.Max(m => m.WidthCm);
        var rolls = new List<RollEligibility>();
        foreach (var r in b.Rolls)
        {
            var rollIssues = CheckRoll(r, maxWidth, b.Color);
            issues.AddRange(rollIssues);
            rolls.Add(new RollEligibility(r.RollId, rollIssues.All(i => i.Severity != Blocking), NeedsOverride(r.Quality), r.Override));
        }

        if (!b.OrderOpen) issues.Add(new ValidationIssue("order_not_open", Blocking, "The production order is not open."));
        if (b.Markers.Count == 0) issues.Add(new ValidationIssue("no_markers", Blocking, "Add at least one marker (length, width, size ratio, plies)."));
        if (b.Rolls.Count == 0) issues.Add(new ValidationIssue("no_rolls", Blocking, "Allocate at least one fabric roll."));

        var shades = b.Rolls.Select(r => r.ShadeGroup?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (shades.Count > 1)
            issues.Add(new ValidationIssue("shade_mix", Warning, $"Allocated rolls come from {shades.Count} shade groups ({string.Join(", ", shades)}); don't mix them in one lay."));
        var types = b.Rolls.Select(r => r.FabricType.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (types.Count > 1)
            issues.Add(new ValidationIssue("fabric_type_mix", Warning, $"Allocated rolls are of {types.Count} fabric types ({string.Join(", ", types)})."));

        var planned = FabricUnits.RoundMm(b.Markers.Sum(m => m.PlannedPlies * m.PlannedPerPlyM));
        var allocated = FabricUnits.RoundMm(b.Rolls.Sum(r => r.PlannedLengthM));
        if (b.Markers.Count > 0 && b.Rolls.Count > 0 && allocated + ToleranceM < planned)
            issues.Add(new ValidationIssue("insufficient_fabric", Warning,
                $"The markers need {planned:0.###} m but only {allocated:0.###} m is allocated (short {planned - allocated:0.###} m)."));

        var bySize = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in b.Markers)
            foreach (var (size, n) in m.Ratio)
                bySize[size] = bySize.GetValueOrDefault(size) + m.PlannedPlies * n;
        var requirement = b.Requirement.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (requirement.Count == 0)
            issues.Add(new ValidationIssue("no_requirement", Warning, "No size requirement is recorded, so coverage can't be checked."));
        else if (b.Markers.Count > 0)
        {
            foreach (var (size, need) in requirement)
            {
                var have = bySize.GetValueOrDefault(size);
                if (have < need) issues.Add(new ValidationIssue("requirement_not_covered", Warning, $"Size {size}: {have} planned, {need} required (short {need - have})."));
            }
            foreach (var (size, have) in bySize)
            {
                var need = requirement.GetValueOrDefault(size);
                if (have > need) issues.Add(new ValidationIssue("over_cut", Info, $"Size {size}: {have} planned, {need} required ({have - need} extra)."));
            }
        }
        foreach (var m in b.Markers.Where(m => m.EfficiencyPct is null))
            issues.Add(new ValidationIssue("marker_efficiency_missing", Info, $"Marker {m.Name} has no CAD efficiency; fabric utilisation can't be calculated for its cuts.", MarkerId: m.Id));

        var blocking = issues.Count(i => i.Severity == Blocking);
        return new BatchValidation(blocking == 0, blocking, issues.Count(i => i.Severity == Warning), planned, allocated, bySize, rolls,
            issues.OrderBy(i => i.Severity switch { Blocking => 0, Warning => 1, _ => 2 }).ToList());
    }
}
