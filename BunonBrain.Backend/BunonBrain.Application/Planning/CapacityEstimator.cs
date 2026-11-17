namespace BunonBrain.Application.Planning;

// Capacity estimation for one production line over a date range.
//
//   capacity (pieces) = Σ over working days of
//                       productive minutes × operators × efficiency ÷ 100 ÷ SMV
//
// SMV is the style's standard minute value per piece (approved or the
// operation bulletin's sum). Operators come from the line's configured
// planned operators, else the workers currently assigned to its
// workstations. Efficiency is the plant's own planning assumption for the
// line. None of these is invented: when one is missing the estimate is
// INCOMPLETE and says exactly what to configure; no capacity number is shown.

public sealed record CapacityMissing(string Code, string Message, string HowToFix);

public sealed record CapacityDay(DateOnly Date, double ProductiveMinutes);

/// <summary>Another allocation on the same line whose dates overlap the estimate's range.</summary>
public sealed record CapacityCommitment(Guid AllocationId, Guid OrderId, string OrderNumber, int Quantity, DateOnly Start, DateOnly End, string Status, int OverlapDays,
    double OverlappingQuantity);

/// <summary>One required machine type and what the line has of it.</summary>
public sealed record MachineTypeCheck(string MachineType, int Available, int Faulty, string State);

public sealed record CapacityInput(
    double? Smv,
    string? SmvBasis,
    double? EfficiencyPct,
    int? Operators,
    string? OperatorsSource,
    int? WorkstationCount,
    IReadOnlyList<CapacityDay> Days,
    int PlannedQuantity,
    IReadOnlyList<CapacityCommitment> Commitments,
    IReadOnlyList<MachineTypeCheck> Machines);

public sealed record CapacityAssumption(string Label, string Value, string Source);

public sealed record CapacityResult(
    string Status,
    IReadOnlyList<CapacityMissing> Missing,
    double AvailableMinutes,
    int WorkingDays,
    double? CapacityPieces,
    int PlannedQuantity,
    double CommittedElsewhere,
    double? UtilizationPct,
    double? ShortagePieces,
    bool? CanMeet,
    IReadOnlyList<CapacityCommitment> Conflicts,
    IReadOnlyList<MachineTypeCheck> Machines,
    IReadOnlyList<CapacityAssumption> Assumptions,
    IReadOnlyList<string> Warnings,
    string Formula);

public static class CapacityEstimator
{
    public const string Complete = "COMPLETE";
    public const string Incomplete = "INCOMPLETE";

    public const string Formula = "capacity = Σ working days (productive minutes × operators × efficiency ÷ 100) ÷ SMV. An estimate from the configured inputs, not a forecast of actual output.";

    /// <summary>Unusually high efficiency assumptions are called out, not refused.</summary>
    public const double HighEfficiencyWarning = 100;

    public static CapacityResult Estimate(CapacityInput i)
    {
        var missing = new List<CapacityMissing>();
        var warnings = new List<string>();
        var minutes = i.Days.Sum(d => d.ProductiveMinutes);
        var workingDays = i.Days.Count(d => d.ProductiveMinutes > 0);

        if (i.Smv is not > 0)
            missing.Add(new CapacityMissing("smv_missing", "The style has no standard minute value (SMV).",
                "Set the style's approved SMV, or add operations with SMVs to its operation bulletin."));
        if (i.EfficiencyPct is not > 0)
            missing.Add(new CapacityMissing("efficiency_missing", "No planning efficiency is configured for the line.",
                "Set the line's efficiency assumption in the capacity settings."));
        if (i.Operators is not > 0)
            missing.Add(new CapacityMissing("operators_missing", "The number of operators is unknown: no planned operators are configured and no workers are assigned to the line's workstations.",
                "Set the line's planned operators, or assign workers to its workstations."));
        if (workingDays == 0)
            missing.Add(new CapacityMissing("no_working_time", "No scheduled production time falls in this period (no active shift works these days).",
                "Check the factory's shifts and working days."));

        var machines = i.Machines;
        foreach (var m in machines.Where(m => m.State == "UNAVAILABLE"))
            warnings.Add($"Every {m.MachineType} machine on the line is faulty or in maintenance ({m.Faulty}), and the style's operations need one: the line can't produce this style until one is repaired.");
        foreach (var m in machines.Where(m => m.State == "NOT_FOUND"))
            warnings.Add($"No machine of type {m.MachineType} was found on the line. If the machines record their type under another name, align the names; otherwise the line lacks this machine.");
        foreach (var m in machines.Where(m => m.State == "UNVERIFIED"))
            warnings.Add($"Machine type {m.MachineType} can't be checked: none of the line's machines has a type recorded.");
        if (i.EfficiencyPct is > HighEfficiencyWarning) warnings.Add($"The efficiency assumption ({i.EfficiencyPct}%) is above 100%: check it.");
        if (i.WorkstationCount is { } ws && i.Operators is { } ops && ops > ws)
            warnings.Add($"{ops} operators are planned but the line declares {ws} workstations: not every operator has a workstation.");

        var conflicts = i.Commitments.Where(c => c.OverlapDays > 0).OrderByDescending(c => c.OverlappingQuantity).ToList();
        var committed = Math.Round(conflicts.Sum(c => c.OverlappingQuantity), 1);

        var assumptions = new List<CapacityAssumption>
        {
            new("Productive time", $"{Math.Round(minutes / 60.0, 1)} h over {workingDays} working day(s)", "Factory shifts: start to end, minus every scheduled break"),
        };
        if (i.Smv is > 0) assumptions.Add(new("SMV", $"{Math.Round(i.Smv.Value, 2)} min per piece", i.SmvBasis ?? "style"));
        if (i.EfficiencyPct is > 0) assumptions.Add(new("Efficiency", $"{Math.Round(i.EfficiencyPct.Value, 1)} %", "Line capacity settings (the plant's own assumption)"));
        if (i.Operators is > 0) assumptions.Add(new("Operators", i.Operators.Value.ToString(), i.OperatorsSource ?? "line"));
        assumptions.Add(new("Other commitments", $"{committed} pieces of {conflicts.Count} other allocation(s) overlap these days, prorated by overlapping days",
            "Production allocations on the same line (planned and active)"));

        if (missing.Count > 0)
            return new CapacityResult(Incomplete, missing, Math.Round(minutes, 1), workingDays, null, i.PlannedQuantity, committed, null, null, null, conflicts, machines,
                assumptions, warnings, Formula);

        var capacity = minutes * i.Operators!.Value * (i.EfficiencyPct!.Value / 100.0) / i.Smv!.Value;
        capacity = Math.Round(capacity, 1);
        var required = i.PlannedQuantity + committed;
        var utilization = capacity <= 0 ? (double?)null : Math.Round(required / capacity * 100.0, 1);
        var shortage = Math.Round(Math.Max(0, required - capacity), 1);
        var blocked = machines.Any(m => m.State == "UNAVAILABLE");
        return new CapacityResult(Complete, Array.Empty<CapacityMissing>(), Math.Round(minutes, 1), workingDays, capacity, i.PlannedQuantity, committed, utilization,
            shortage, shortage <= 0 && !blocked, conflicts, machines, assumptions, warnings, Formula);
    }

    /// <summary>
    /// A commitment prorated to the estimate's range: the allocation's
    /// quantity spread evenly over its own working days, counted for the days
    /// it shares with the range.
    /// </summary>
    public static CapacityCommitment Commit(Guid allocationId, Guid orderId, string orderNumber, int quantity, DateOnly start, DateOnly end, string status,
        DateOnly rangeStart, DateOnly rangeEnd, Func<DateOnly, bool> isWorkingDay)
    {
        var total = 0;
        var shared = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (!isWorkingDay(d)) continue;
            total++;
            if (d >= rangeStart && d <= rangeEnd) shared++;
        }
        var qty = total == 0 ? 0 : quantity * (shared / (double)total);
        return new CapacityCommitment(allocationId, orderId, orderNumber, quantity, start, end, status, shared, Math.Round(qty, 1));
    }

    /// <summary>
    /// Whether the line has the machine type a style needs. <paramref name="typedMachines"/>
    /// are the line's active machines that have a type recorded. State:
    /// OK (at least one available), UNAVAILABLE (machines of the type exist but all are faulty), NOT_FOUND (the line
    /// has typed machines but none matches the name), UNVERIFIED (no machine on the line has a type recorded).
    /// </summary>
    public static MachineTypeCheck CheckMachineType(string requiredType, IReadOnlyList<(string? Type, bool Faulty)> lineMachines)
    {
        var typed = lineMachines.Where(m => !string.IsNullOrWhiteSpace(m.Type)).ToList();
        if (requiredType.Equals("manual", StringComparison.OrdinalIgnoreCase)) return new MachineTypeCheck(requiredType, 0, 0, "OK");
        if (typed.Count == 0) return new MachineTypeCheck(requiredType, 0, 0, "UNVERIFIED");
        // Exact (ignoring case, spaces and hyphens), or one name contains the other when the shorter is at least four characters
        // ("SNLS" in "Single needle lockstitch (SNLS)"); two-letter codes such as "OL" only match exactly.
        static string Norm(string v) => new string(v.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        bool Matches(string? t)
        {
            if (t is null) return false;
            var a = Norm(t); var b = Norm(requiredType);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
            return shorter.Length >= 4 && longer.Contains(shorter, StringComparison.Ordinal);
        }
        var of = typed.Where(m => Matches(m.Type)).ToList();
        var faulty = of.Count(m => m.Faulty);
        var available = of.Count - faulty;
        return new MachineTypeCheck(requiredType, available, faulty, available > 0 ? "OK" : of.Count == 0 ? "NOT_FOUND" : "UNAVAILABLE");
    }
}
