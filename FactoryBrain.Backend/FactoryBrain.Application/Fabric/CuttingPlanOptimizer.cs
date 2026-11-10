namespace FactoryBrain.Application.Fabric;

/// <summary>A marker that may be used in a plan (dimensions and size ratio as made by the marker maker).</summary>
public sealed record PlanCandidate(string Name, double LengthM, double WidthCm, double? EfficiencyPct, double EndAllowanceCm, IReadOnlyDictionary<string, int> Ratio);

/// <param name="Requirement">Garments needed per size.</param>
/// <param name="CuttableWidthCm">Narrowest cuttable width of the fabric to be used; markers wider than this can't be cut.</param>
/// <param name="AvailableLengthM">Fabric available for the plan; null when unknown (fit is then not checked).</param>
/// <param name="MaxPliesPerLay">Most plies spread in one lay (spreading-table / knife limit).</param>
/// <param name="MaxMarkerLengthM">Longest marker the cutting table takes; null = no limit given.</param>
public sealed record PlanRequest(
    IReadOnlyDictionary<string, int> Requirement,
    IReadOnlyList<PlanCandidate> Candidates,
    double? CuttableWidthCm,
    double? AvailableLengthM,
    int MaxPliesPerLay,
    double? MaxMarkerLengthM,
    int MaxOptions = 5);

public sealed record PlanOptionMarker(string Name, int Plies, int Lays, double ConsumptionM);

public sealed record PlanOption(
    int Rank,
    IReadOnlyList<PlanOptionMarker> Markers,
    double ConsumptionM,
    int Garments,
    IReadOnlyDictionary<string, int> CutBySize,
    IReadOnlyDictionary<string, int> OverCutBySize,
    int OverCut,
    int Lays,
    double? ConsumptionPerGarmentM,
    double? EstimatedUtilizationPct,
    bool? FitsAvailableFabric,
    double? ShortfallM);

/// <summary><see cref="Status"/>: <c>ok</c>, <c>insufficient_data</c> (see <see cref="MissingInputs"/>) or <c>infeasible</c>.</summary>
public sealed record PlanOptionsResult(
    string Status,
    IReadOnlyList<PlanOption> Options,
    IReadOnlyList<string> MissingInputs,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations,
    int Evaluated,
    bool Truncated);

/// <summary>
/// A bounded baseline for comparing feasible cutting plans: every single
/// marker and every pair of the given markers, with the fewest plies that
/// cover the size requirement, ranked by fabric consumption (then over-cut,
/// then lays). It never makes a marker, never nests patterns and never
/// claims a global optimum; with too little input it says what is missing
/// instead of producing a result.
/// </summary>
public static class CuttingPlanOptimizer
{
    public const int MaxCandidates = 12;
    public const int MaxPliesPerMarker = 2000;
    public const int MaxEvaluations = 250_000;

    public static PlanOptionsResult Compare(PlanRequest r)
    {
        var missing = new List<string>();
        var excluded = new List<string>();
        var requirement = r.Requirement.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (requirement.Count == 0) missing.Add("A size requirement (garments per size) for the batch.");
        if (r.Candidates.Count == 0) missing.Add("At least one marker with its length, width and size ratio.");
        if (r.CuttableWidthCm is not ({ } w0 and > 0)) missing.Add("The fabric's cuttable width: allocate rolls to the batch (their narrowest cuttable width is used).");
        if (r.MaxPliesPerLay < 1) missing.Add("The most plies per lay (spreading limit).");
        var assumptions = new List<string>
        {
            $"Plies of each marker are spread in lays of at most {Math.Max(1, r.MaxPliesPerLay)} plies.",
            "Consumption = plies × (marker length + end allowance); splices, defect cut-outs and end bits are not included.",
            "Markers are used exactly as given (length, width, size ratio, CAD efficiency).",
        };
        var limitations = new List<string>
        {
            "Best of the evaluated whole-ply combinations of up to two of the given markers — not a global optimum.",
            "No new markers are made and patterns are not nested; a marker maker / CAD system does that.",
        };
        if (missing.Count > 0)
            return new PlanOptionsResult("insufficient_data", Array.Empty<PlanOption>(), missing, excluded, assumptions, limitations, 0, false);

        var width = r.CuttableWidthCm!.Value;
        var valid = new List<PlanCandidate>();
        foreach (var c in r.Candidates.Take(MaxCandidates))
        {
            var ratio = c.Ratio.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            if (!(c.LengthM > 0) || !(c.WidthCm > 0) || !FabricUnits.IsFinite(c.LengthM) || !FabricUnits.IsFinite(c.WidthCm))
                excluded.Add($"{c.Name}: length and width must be positive.");
            else if (ratio.Count == 0) excluded.Add($"{c.Name}: the size ratio has no garments.");
            else if (c.WidthCm > width + FabricGeometry.AcrossToleranceCm)
                excluded.Add($"{c.Name}: {c.WidthCm:0.#} cm wide, wider than the fabric's cuttable width ({width:0.#} cm).");
            else if (r.MaxMarkerLengthM is { } maxLen && c.LengthM > maxLen)
                excluded.Add($"{c.Name}: {c.LengthM:0.###} m long, longer than the cutting table ({maxLen:0.###} m).");
            else if (!ratio.Keys.Any(requirement.ContainsKey))
                excluded.Add($"{c.Name}: holds none of the required sizes.");
            else valid.Add(c with { Ratio = ratio });
        }
        if (r.Candidates.Count > MaxCandidates)
            excluded.Add($"Only the first {MaxCandidates} markers are compared.");
        if (valid.Count == 0)
            return new PlanOptionsResult("infeasible", Array.Empty<PlanOption>(), missing, excluded, assumptions, limitations, 0, false);

        var evaluated = 0;
        var truncated = false;
        var found = new Dictionary<string, (int[] Plies, int[] Index)>();

        // Single markers.
        for (var i = 0; i < valid.Count; i++)
        {
            evaluated++;
            if (PliesToCover(requirement, valid[i].Ratio, null) is { } p) found[$"{i}:{p}"] = (new[] { p }, new[] { i });
        }
        // Pairs: every useful ply count of the first marker, the fewest plies of the second for the rest.
        for (var i = 0; i < valid.Count && !truncated; i++)
        for (var j = i + 1; j < valid.Count && !truncated; j++)
        {
            var maxA = Math.Min(MaxPliesPerMarker, Saturation(requirement, valid[i].Ratio));
            for (var pa = 1; pa <= maxA; pa++)
            {
                if (++evaluated > MaxEvaluations) { truncated = true; break; }
                var rest = requirement.ToDictionary(kv => kv.Key, kv => Math.Max(0, kv.Value - pa * valid[i].Ratio.GetValueOrDefault(kv.Key)), StringComparer.OrdinalIgnoreCase);
                if (rest.Values.All(v => v == 0)) break; // A alone covers it from here: already a single-marker option.
                if (PliesToCover(rest, valid[j].Ratio, null) is { } pb && pb > 0) found[$"{i}:{pa}|{j}:{pb}"] = (new[] { pa, pb }, new[] { i, j });
            }
        }

        var options = found.Values.Select(f => Build(f.Plies, f.Index, valid, requirement, r, width))
            .OrderBy(o => o.ConsumptionM).ThenBy(o => o.OverCut).ThenBy(o => o.Lays).ThenBy(o => o.Markers.Count)
            .Take(Math.Max(1, r.MaxOptions))
            .Select((o, k) => o with { Rank = k + 1 })
            .ToList();
        if (options.Count == 0)
        {
            excluded.Add("No combination of up to two of the given markers covers every required size. Add a marker that holds the missing sizes.");
            return new PlanOptionsResult("infeasible", options, missing, excluded, assumptions, limitations, evaluated, truncated);
        }
        if (truncated) limitations.Add($"The search stopped after {MaxEvaluations:N0} combinations; better ones may exist.");
        return new PlanOptionsResult("ok", options, missing, excluded, assumptions, limitations, evaluated, truncated);
    }

    /// <summary>Fewest plies of one marker that cover <paramref name="need"/>; null when it lacks a needed size.</summary>
    private static int? PliesToCover(IReadOnlyDictionary<string, int> need, IReadOnlyDictionary<string, int> ratio, int? cap)
    {
        var plies = 0;
        foreach (var (size, qty) in need)
        {
            if (qty <= 0) continue;
            var per = ratio.GetValueOrDefault(size);
            if (per <= 0) return null;
            plies = Math.Max(plies, (qty + per - 1) / per);
        }
        if (plies > (cap ?? MaxPliesPerMarker)) return null;
        return plies;
    }

    /// <summary>Plies after which more of this marker covers nothing new.</summary>
    private static int Saturation(IReadOnlyDictionary<string, int> need, IReadOnlyDictionary<string, int> ratio) =>
        need.Where(kv => ratio.GetValueOrDefault(kv.Key) > 0)
            .Select(kv => (kv.Value + ratio[kv.Key] - 1) / ratio[kv.Key])
            .DefaultIfEmpty(0).Max();

    private static PlanOption Build(int[] plies, int[] index, List<PlanCandidate> valid, Dictionary<string, int> requirement, PlanRequest r, double width)
    {
        var markers = new List<PlanOptionMarker>();
        var cut = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        double consumption = 0, pattern = 0;
        var effKnown = true;
        var lays = 0;
        for (var k = 0; k < plies.Length; k++)
        {
            var m = valid[index[k]];
            var used = plies[k] * (m.LengthM + m.EndAllowanceCm / 100);
            consumption += used;
            var markerLays = (plies[k] + r.MaxPliesPerLay - 1) / r.MaxPliesPerLay;
            lays += markerLays;
            markers.Add(new PlanOptionMarker(m.Name, plies[k], markerLays, FabricUnits.RoundMm(used)));
            foreach (var (size, n) in m.Ratio) cut[size] = cut.GetValueOrDefault(size) + plies[k] * n;
            if (m.EfficiencyPct is { } eff) pattern += FabricUnits.AreaM2(plies[k] * m.LengthM, m.WidthCm) * eff / 100;
            else effKnown = false;
        }
        var over = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (size, n) in cut)
        {
            var extra = n - requirement.GetValueOrDefault(size);
            if (extra > 0) over[size] = extra;
        }
        var garments = cut.Values.Sum();
        var fabricArea = FabricUnits.AreaM2(consumption, width);
        bool? fits = r.AvailableLengthM is { } avail ? consumption <= avail + 1e-9 : null;
        return new PlanOption(0, markers, FabricUnits.RoundMm(consumption), garments, cut, over, over.Values.Sum(), lays,
            garments > 0 ? FabricUnits.Round(consumption / garments, 4) : null,
            effKnown && fabricArea > 0 ? FabricUnits.Round(pattern / fabricArea * 100, 1) : null,
            fits, r.AvailableLengthM is { } a2 && consumption > a2 ? FabricUnits.RoundMm(consumption - a2) : null);
    }
}
