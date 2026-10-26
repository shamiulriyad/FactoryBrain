namespace FactoryBrain.Application.Production;

/// <summary>An operation as the balancer sees it. <see cref="Smv"/> is standard minutes per piece.</summary>
public sealed record BalanceOperation(Guid Id, int Sequence, string Name, double Smv, string? MachineType, IReadOnlyList<int> Predecessors);

/// <summary>One workstation (one operator) and the operations it performs; an operation on k stations is split evenly.</summary>
public sealed record StationAssignment(int StationNo, IReadOnlyList<Guid> OperationIds);

public sealed record StationLoad(int StationNo, IReadOnlyList<Guid> OperationIds, IReadOnlyList<int> Sequences, string? MachineType, double LoadMinutes, double UtilizationPct);

/// <summary>
/// Workload metrics of one distribution. Definitions:
/// station load = Σ (operation SMV ÷ stations sharing it), minutes per piece;
/// cycle time = the largest station load (the line can't release pieces faster);
/// capacity = 60 ÷ cycle time, pieces/hour at 100 % of standard;
/// balance efficiency = Σ SMV ÷ (stations × cycle time) × 100;
/// balance delay = 100 − balance efficiency;
/// smoothness index = √Σ (cycle time − station load)².
/// </summary>
public sealed record WorkloadMetrics(
    int Stations,
    double TotalSmv,
    double CycleTimeMinutes,
    double CapacityPerHour,
    double BalanceEfficiencyPct,
    double BalanceDelayPct,
    double SmoothnessIndex,
    int BottleneckStationNo,
    IReadOnlyList<StationLoad> StationLoads);

public sealed record LayoutIssue(string Code, string Message);

public sealed record BalanceProposalResult(
    bool Feasible,
    /// <summary><c>ok</c>, <c>missing_inputs</c>, <c>invalid_inputs</c>, <c>infeasible</c>.</summary>
    string Status,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Assumptions,
    WorkloadMetrics? Proposed,
    IReadOnlyList<StationAssignment> ProposedStations,
    double? LowerBoundCycleTimeMinutes,
    double? TargetCycleTimeMinutes,
    bool? TargetMet,
    int? StationsNeededForTarget,
    string Method);

/// <summary>
/// Line balancing with the Ranked Positional Weight heuristic (Helgeson &amp; Birnie).
/// <para>
/// Positional weight = an operation's SMV + the SMV of every operation that
/// (transitively) depends on it. For a trial cycle time C, operations are
/// taken in descending weight; an operation joins the open station when all
/// its predecessors are already placed, the station's load stays ≤ C and the
/// machine type matches (a workstation holds one machine type; operations
/// without a machine type fit anywhere). An operation longer than C gets
/// ⌈SMV ÷ C⌉ dedicated parallel stations. The smallest C (searched upward
/// from the lower bound Σ SMV ÷ stations, in a bounded number of steps)
/// that needs no more than the declared workstations is proposed.
/// </para>
/// <para>
/// RPW is a heuristic: the result is feasible for the stated constraints but
/// is not proven optimal. The lower bound is reported so the gap is visible.
/// </para>
/// </summary>
public static class LineBalancer
{
    public const int MaxOperations = 150;
    public const int MaxWorkstations = 300;
    private const int CoarseSteps = 400;
    private const int FineSteps = 400;
    private const double Eps = 1e-9;

    public const string Method = "Ranked Positional Weight heuristic with parallel stations for operations longer than the cycle time; smallest feasible cycle time found by a bounded two-pass search. Not proven optimal.";

    /// <summary>Structural problems that make any balance meaningless (missing SMV, unknown or cyclic precedence).</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<BalanceOperation> ops)
    {
        var problems = new List<string>();
        if (ops.Count == 0) { problems.Add("The style has no operation bulletin (operations with SMV and sequence)."); return problems; }
        if (ops.Count > MaxOperations) problems.Add($"At most {MaxOperations} operations can be balanced.");
        var seqs = ops.Select(o => o.Sequence).ToHashSet();
        foreach (var dup in ops.GroupBy(o => o.Sequence).Where(g => g.Count() > 1))
            problems.Add($"Sequence {dup.Key} is used by more than one operation.");
        foreach (var o in ops)
        {
            if (!(o.Smv > 0)) problems.Add($"Operation {o.Sequence} ({o.Name}) has no positive SMV.");
            foreach (var p in o.Predecessors)
            {
                if (p == o.Sequence) problems.Add($"Operation {o.Sequence} lists itself as a predecessor.");
                else if (!seqs.Contains(p)) problems.Add($"Operation {o.Sequence} depends on unknown operation {p}.");
            }
        }
        if (problems.Count == 0 && TopologicalOrder(ops) is null)
            problems.Add("The precedence relations contain a cycle, so no valid operation order exists.");
        return problems;
    }

    public static BalanceProposalResult Propose(IReadOnlyList<BalanceOperation> ops, int workstations, double? targetOutputPerHour)
    {
        var assumptions = new List<string>
        {
            "Each workstation is one operator; operation SMVs are the factory-approved standard minutes.",
            "A workstation holds one machine type; operations without a machine type can share any station.",
            "An operation split across k stations is shared evenly between them.",
            "Capacity figures are at 100 % of standard; actual output also depends on operator performance.",
            "The proposal moves workload on paper only: it does not reassign people, change schedules or send machine commands.",
        };
        var problems = Validate(ops).ToList();
        if (workstations < 1) problems.Add("The line needs at least one declared workstation.");
        if (workstations > MaxWorkstations) problems.Add($"At most {MaxWorkstations} workstations can be balanced.");
        if (targetOutputPerHour is { } t && !(t > 0)) problems.Add("Target output per hour must be positive when given.");
        if (problems.Count > 0)
            return new BalanceProposalResult(false, ops.Count == 0 ? "missing_inputs" : "invalid_inputs", problems, assumptions,
                null, Array.Empty<StationAssignment>(), null, null, null, null, Method);

        var total = ops.Sum(o => o.Smv);
        var lowerBound = total / workstations;
        var weights = PositionalWeights(ops);
        double? targetCycle = targetOutputPerHour is > 0 ? 60.0 / targetOutputPerHour.Value : null;

        // Distinct machine types each need a station of their own.
        var machineTypes = ops.Select(o => Norm(o.MachineType)).Where(m => m is not null).Distinct().Count();
        if (machineTypes > workstations)
        {
            problems.Add($"The bulletin needs {machineTypes} machine types but the line declares only {workstations} workstations.");
            return new BalanceProposalResult(false, "infeasible", problems, assumptions, null, Array.Empty<StationAssignment>(),
                Round(lowerBound), targetCycle is null ? null : Round(targetCycle.Value), targetCycle is null ? null : false,
                targetCycle is null ? null : Assign(ops, weights, targetCycle.Value).Count, Method);
        }

        // Bounded search for the smallest feasible cycle time: a coarse pass, then a fine pass inside the first feasible step.
        var upper = Math.Max(total, ops.Max(o => o.Smv));
        List<StationAssignment>? best = null;
        double bestC = upper;
        var step = Math.Max((upper - lowerBound) / CoarseSteps, 1e-4);
        double previous = lowerBound;
        for (var i = 0; i <= CoarseSteps; i++)
        {
            var c = Math.Min(upper, lowerBound + i * step);
            var stations = Assign(ops, weights, c);
            if (stations.Count <= workstations) { best = stations; bestC = c; break; }
            previous = c;
            if (c >= upper) break;
        }
        if (best is null)
        {
            problems.Add("No assignment within the declared workstations satisfies the precedence and machine-type constraints.");
            return new BalanceProposalResult(false, "infeasible", problems, assumptions, null, Array.Empty<StationAssignment>(),
                Round(lowerBound), targetCycle is null ? null : Round(targetCycle.Value), null, null, Method);
        }
        if (bestC > lowerBound)
        {
            var fine = (bestC - previous) / FineSteps;
            for (var i = 1; i < FineSteps && fine > 0; i++)
            {
                var c = previous + i * fine;
                var stations = Assign(ops, weights, c);
                if (stations.Count <= workstations) { best = stations; bestC = c; break; }
            }
        }

        var metrics = Measure(ops, best);
        bool? targetMet = targetCycle is null ? null : metrics.CycleTimeMinutes <= targetCycle.Value + 1e-6;
        int? neededForTarget = targetCycle is null ? null : Assign(ops, weights, targetCycle.Value).Count;
        if (targetMet == false)
            problems.Add($"The target of {targetOutputPerHour:0.#} pieces/hour needs a cycle time of {targetCycle:0.###} min; the best found with {workstations} workstations is {metrics.CycleTimeMinutes:0.###} min (about {neededForTarget} workstations would be needed).");
        if (metrics.CycleTimeMinutes > lowerBound + 1e-6)
            assumptions.Add($"The theoretical lower bound is {lowerBound:0.###} min per piece (Σ SMV ÷ workstations); the proposal is {metrics.CycleTimeMinutes:0.###} min, so it may not be optimal.");
        return new BalanceProposalResult(true, "ok", problems, assumptions, metrics, best, Round(lowerBound),
            targetCycle is null ? null : Round(targetCycle.Value), targetMet, neededForTarget, Method);
    }

    /// <summary>Workload metrics of a given distribution (current layout or proposal).</summary>
    public static WorkloadMetrics Measure(IReadOnlyList<BalanceOperation> ops, IReadOnlyList<StationAssignment> stations)
    {
        var byId = ops.ToDictionary(o => o.Id);
        var shares = stations.SelectMany(s => s.OperationIds.Distinct()).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var loads = stations.OrderBy(s => s.StationNo).Select(s =>
        {
            var ids = s.OperationIds.Distinct().Where(byId.ContainsKey).ToList();
            var load = ids.Sum(id => byId[id].Smv / shares[id]);
            var machine = ids.Select(id => Norm(byId[id].MachineType)).FirstOrDefault(m => m is not null);
            return (s.StationNo, ids, load, machine);
        }).ToList();
        var cycle = loads.Count == 0 ? 0 : loads.Max(l => l.load);
        var total = loads.SelectMany(l => l.ids).Distinct().Sum(id => byId[id].Smv);
        var n = loads.Count;
        var eff = n > 0 && cycle > 0 ? total / (n * cycle) * 100 : 0;
        var si = Math.Sqrt(loads.Sum(l => Math.Pow(cycle - l.load, 2)));
        var bottleneck = loads.Count == 0 ? 0 : loads.OrderByDescending(l => l.load).ThenBy(l => l.StationNo).First().StationNo;
        return new WorkloadMetrics(
            n, Round(total), Round(cycle), cycle > 0 ? ProductionMetrics.Round1(60 / cycle) : 0,
            ProductionMetrics.Round1(eff), ProductionMetrics.Round1(100 - eff), Round(si), bottleneck,
            loads.Select(l => new StationLoad(l.StationNo, l.ids, l.ids.Select(id => byId[id].Sequence).OrderBy(x => x).ToList(),
                l.machine, Round(l.load), cycle > 0 ? ProductionMetrics.Round1(l.load / cycle * 100) : 0)).ToList());
    }

    /// <summary>Constraint check of a recorded layout: every operation placed, known operations, one machine type per station, precedence order.</summary>
    public static IReadOnlyList<LayoutIssue> CheckLayout(IReadOnlyList<BalanceOperation> ops, IReadOnlyList<StationAssignment> stations)
    {
        var issues = new List<LayoutIssue>();
        var byId = ops.ToDictionary(o => o.Id);
        var placed = stations.SelectMany(s => s.OperationIds).ToHashSet();
        foreach (var o in ops.Where(o => !placed.Contains(o.Id)).OrderBy(o => o.Sequence))
            issues.Add(new LayoutIssue("operation_unassigned", $"Operation {o.Sequence} ({o.Name}) is not on any workstation."));
        foreach (var id in placed.Where(id => !byId.ContainsKey(id)))
            issues.Add(new LayoutIssue("unknown_operation", $"Workstation lists an operation ({id}) that is not in this style's bulletin."));
        foreach (var s in stations)
        {
            var machines = s.OperationIds.Where(byId.ContainsKey).Select(id => Norm(byId[id].MachineType)).Where(m => m is not null).Distinct().ToList();
            if (machines.Count > 1)
                issues.Add(new LayoutIssue("mixed_machine_types", $"Workstation {s.StationNo} mixes machine types ({string.Join(", ", machines)})."));
        }
        // An operation must not start on an earlier workstation than any of its predecessors.
        var bySeq = ops.ToDictionary(o => o.Sequence);
        var firstStation = new Dictionary<Guid, int>();
        foreach (var s in stations.OrderBy(s => s.StationNo))
            foreach (var id in s.OperationIds)
                firstStation.TryAdd(id, s.StationNo);
        foreach (var o in ops)
        {
            if (!firstStation.TryGetValue(o.Id, out var mine)) continue;
            foreach (var p in o.Predecessors)
                if (bySeq.TryGetValue(p, out var pred) && firstStation.TryGetValue(pred.Id, out var predFirst) && predFirst > mine)
                    issues.Add(new LayoutIssue("precedence_violation",
                        $"Operation {o.Sequence} is on workstation {mine}, before its predecessor {p} (workstation {predFirst})."));
        }
        return issues;
    }

    // ── RPW core ────────────────────────────────────────────────────────────

    internal static Dictionary<Guid, double> PositionalWeights(IReadOnlyList<BalanceOperation> ops)
    {
        var bySeq = ops.ToDictionary(o => o.Sequence);
        var successors = ops.ToDictionary(o => o.Sequence, _ => new List<int>());
        foreach (var o in ops) foreach (var p in o.Predecessors) if (successors.ContainsKey(p)) successors[p].Add(o.Sequence);
        var weights = new Dictionary<Guid, double>();
        foreach (var o in ops)
        {
            var seen = new HashSet<int>();
            var stack = new Stack<int>(successors[o.Sequence]);
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                if (!seen.Add(s)) continue;
                foreach (var n in successors[s]) stack.Push(n);
            }
            weights[o.Id] = o.Smv + seen.Sum(s => bySeq[s].Smv);
        }
        return weights;
    }

    internal static List<StationAssignment> Assign(IReadOnlyList<BalanceOperation> ops, IReadOnlyDictionary<Guid, double> weights, double cycle)
    {
        var ranked = ops.OrderByDescending(o => weights[o.Id]).ThenBy(o => o.Sequence).ToList();
        var placed = new HashSet<int>();
        var stations = new List<StationAssignment>();
        var no = 0;
        while (placed.Count < ops.Count)
        {
            var current = new List<Guid>();
            double load = 0;
            string? machine = null;
            while (true)
            {
                BalanceOperation? pick = null;
                foreach (var o in ranked)
                {
                    if (placed.Contains(o.Sequence)) continue;
                    if (!o.Predecessors.All(placed.Contains)) continue;
                    if (o.Smv > cycle + Eps)
                    {
                        if (current.Count == 0) { pick = o; break; }
                        continue;
                    }
                    var m = Norm(o.MachineType);
                    if (load + o.Smv > cycle + Eps) continue;
                    if (m is not null && machine is not null && m != machine) continue;
                    pick = o;
                    break;
                }
                if (pick is null) break;
                placed.Add(pick.Sequence);
                if (pick.Smv > cycle + Eps)
                {
                    // Parallel stations for a long operation; they hold nothing else.
                    var k = (int)Math.Ceiling(pick.Smv / cycle - Eps);
                    for (var i = 0; i < k; i++) stations.Add(new StationAssignment(++no, new[] { pick.Id }));
                    current.Clear();
                    break;
                }
                current.Add(pick.Id);
                load += pick.Smv;
                machine ??= Norm(pick.MachineType);
            }
            if (current.Count > 0) stations.Add(new StationAssignment(++no, current.ToList()));
            else if (stations.Count == 0 || stations[^1].OperationIds.Count == 0)
            {
                // Nothing could be placed: only possible with an invalid precedence graph (rejected earlier).
                break;
            }
        }
        return stations;
    }

    internal static List<int>? TopologicalOrder(IReadOnlyList<BalanceOperation> ops)
    {
        var indegree = ops.ToDictionary(o => o.Sequence, o => o.Predecessors.Distinct().Count());
        var successors = ops.ToDictionary(o => o.Sequence, _ => new List<int>());
        foreach (var o in ops) foreach (var p in o.Predecessors.Distinct()) if (successors.ContainsKey(p)) successors[p].Add(o.Sequence);
        var queue = new SortedSet<int>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var order = new List<int>();
        while (queue.Count > 0)
        {
            var s = queue.Min;
            queue.Remove(s);
            order.Add(s);
            foreach (var n in successors[s]) if (--indegree[n] == 0) queue.Add(n);
        }
        return order.Count == ops.Count ? order : null;
    }

    private static string? Norm(string? machine) => string.IsNullOrWhiteSpace(machine) ? null : machine.Trim().ToUpperInvariant();

    private static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
}
