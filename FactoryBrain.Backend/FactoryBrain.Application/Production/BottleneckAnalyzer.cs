namespace FactoryBrain.Application.Production;

/// <summary>The latest usable time study of one operation.</summary>
public sealed record CycleObservation(Guid OperationId, double CycleSeconds, int OperatorsAssigned, int? QueueBefore, DateTime ObservedAtUtc, int SampleCount);

public sealed record OperationCapacity(
    Guid OperationId,
    int Sequence,
    string Name,
    double Smv,
    string? MachineType,
    double? ObservedCycleSeconds,
    int? OperatorsAssigned,
    /// <summary>Measured: 3600 × operators ÷ cycle seconds, pieces/hour.</summary>
    double? ObservedCapacityPerHour,
    /// <summary>Standard minutes ÷ observed minutes per piece × 100 (operator performance against standard).</summary>
    double? PerformancePct,
    /// <summary>From the recorded layout at 100 % of standard: 60 ÷ the busiest station holding the operation.</summary>
    double? StandardCapacityPerHour,
    int? QueueBefore,
    DateTime? ObservedAtUtc,
    int? SampleCount);

public sealed record BottleneckResult(
    /// <summary><c>observed</c> (time studies), <c>standard_only</c> (SMV + layout, not measured), <c>insufficient_data</c>.</summary>
    string Status,
    OperationCapacity? Constraint,
    int? ConstraintStationNo,
    double? ConstraintCapacityPerHour,
    IReadOnlyList<OperationCapacity> Operations,
    int ObservedOperations,
    int TotalOperations,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> MissingInputs);

/// <summary>
/// Identifies the constraint of a line for one style.
/// <para>
/// Measured basis (preferred): each time-studied operation's capacity is
/// 3600 × operators assigned ÷ mean cycle seconds; the constraint is the
/// operation with the lowest capacity. Queue counts in front of operations
/// and the line's recorded output rate are reported as supporting or
/// contradicting evidence; operations without a time study are listed as a
/// limitation (one of them could be slower).
/// </para>
/// <para>
/// Standard basis (fallback, labelled as such): with no time study but a
/// recorded layout, the busiest station at standard minutes is reported.
/// With neither, no bottleneck is named. The operation with the largest SMV
/// alone is never called the bottleneck: without its staffing it says nothing
/// about capacity.
/// </para>
/// </summary>
public static class BottleneckAnalyzer
{
    /// <summary>Line output within this share of the constraint's capacity is "consistent" with it limiting the line.</summary>
    public const double ConsistencyBand = 0.15;

    public static BottleneckResult Analyze(
        IReadOnlyList<BalanceOperation> ops,
        IReadOnlyList<CycleObservation> observations,
        IReadOnlyList<StationAssignment>? layout,
        double? observedLineRatePerHour,
        int staleObservationsIgnored = 0)
    {
        var missing = new List<string>();
        var limitations = new List<string>();
        var evidence = new List<string>();
        if (ops.Count == 0)
        {
            missing.Add("An operation bulletin (operations with SMV) for the style.");
            return new BottleneckResult("insufficient_data", null, null, null, Array.Empty<OperationCapacity>(), 0, 0,
                evidence, limitations, missing);
        }

        var obsByOp = observations.GroupBy(o => o.OperationId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.ObservedAtUtc).First());
        WorkloadMetrics? standard = layout is { Count: > 0 } ? LineBalancer.Measure(ops, layout) : null;
        var stationLoadByOp = new Dictionary<Guid, double>();
        if (standard is not null)
            foreach (var st in standard.StationLoads)
                foreach (var id in st.OperationIds)
                    stationLoadByOp[id] = Math.Max(stationLoadByOp.GetValueOrDefault(id), st.LoadMinutes);

        var rows = ops.OrderBy(o => o.Sequence).Select(o =>
        {
            obsByOp.TryGetValue(o.Id, out var ob);
            double? cap = ob is { CycleSeconds: > 0, OperatorsAssigned: > 0 } ? ProductionMetrics.Round1(3600.0 * ob.OperatorsAssigned / ob.CycleSeconds) : null;
            double? perf = ob is { CycleSeconds: > 0 } ? ProductionMetrics.Round1(o.Smv * 60 / ob.CycleSeconds * 100) : null;
            double? std = stationLoadByOp.TryGetValue(o.Id, out var load) && load > 0 ? ProductionMetrics.Round1(60 / load) : null;
            return new OperationCapacity(o.Id, o.Sequence, o.Name, o.Smv, o.MachineType, ob?.CycleSeconds, ob?.OperatorsAssigned,
                cap, perf, std, ob?.QueueBefore, ob?.ObservedAtUtc, ob?.SampleCount);
        }).ToList();

        var observed = rows.Where(r => r.ObservedCapacityPerHour is not null).ToList();
        if (staleObservationsIgnored > 0)
            limitations.Add($"{staleObservationsIgnored} older time stud{(staleObservationsIgnored == 1 ? "y was" : "ies were")} ignored as out of date.");

        if (observed.Count > 0)
        {
            var c = observed.OrderBy(r => r.ObservedCapacityPerHour).ThenBy(r => r.Sequence).First();
            evidence.Add($"Operation {c.Sequence} ({c.Name}) has the lowest measured capacity: {c.ObservedCapacityPerHour:0.#} pieces/hour " +
                         $"({c.OperatorsAssigned} operator{(c.OperatorsAssigned == 1 ? "" : "s")} × 3600 ÷ {c.ObservedCycleSeconds:0.#} s), " +
                         $"time study of {c.ObservedAtUtc:yyyy-MM-dd HH:mm} UTC over {c.SampleCount} cycle{(c.SampleCount == 1 ? "" : "s")}.");
            var withQueue = observed.Where(r => r.QueueBefore is not null).ToList();
            if (withQueue.Count >= 2)
            {
                var maxQueue = withQueue.OrderByDescending(r => r.QueueBefore).ThenBy(r => r.Sequence).First();
                if (maxQueue.OperationId == c.OperationId)
                    evidence.Add($"The largest recorded queue ({maxQueue.QueueBefore} pieces) is in front of this operation, which supports it being the constraint.");
                else
                    limitations.Add($"The largest recorded queue ({maxQueue.QueueBefore} pieces) is in front of operation {maxQueue.Sequence} ({maxQueue.Name}), not the measured constraint: re-check the time studies and queue counts.");
            }
            else
            {
                limitations.Add("Queue counts were not recorded for enough operations to corroborate the cycle times.");
            }
            if (observedLineRatePerHour is { } rate)
            {
                var cap = c.ObservedCapacityPerHour!.Value;
                if (rate > cap * (1 + ConsistencyBand))
                    limitations.Add($"The line's recorded output ({rate:0.#} pieces/hour) is above this capacity, so the time study may be out of date or the staffing has changed.");
                else if (rate >= cap * (1 - ConsistencyBand))
                    evidence.Add($"The line's recorded output ({rate:0.#} pieces/hour) is close to this capacity, consistent with it limiting the line.");
                else
                    evidence.Add($"The line's recorded output ({rate:0.#} pieces/hour) is well below this capacity: losses elsewhere (downtime, absenteeism, balancing, material) may limit the line more than this operation.");
            }
            if (observed.Count < rows.Count)
                limitations.Add($"{rows.Count - observed.Count} of {rows.Count} operations have no recent time study; one of them could be slower.");
            return new BottleneckResult("observed", c, null, c.ObservedCapacityPerHour, rows, observed.Count, rows.Count, evidence, limitations, missing);
        }

        missing.Add("Time studies (observed cycle times and operators) for the operations on this line.");
        if (standard is not null)
        {
            var station = standard.StationLoads.First(s => s.StationNo == standard.BottleneckStationNo);
            var first = rows.Where(r => station.OperationIds.Contains(r.OperationId)).OrderBy(r => r.Sequence).FirstOrDefault();
            evidence.Add($"At standard minutes, workstation {station.StationNo} carries the most work ({station.LoadMinutes:0.###} min per piece), " +
                         $"limiting the line to about {standard.CapacityPerHour:0.#} pieces/hour at 100 % performance.");
            limitations.Add("Based on SMV and the recorded layout only, not on measured cycle times.");
            return new BottleneckResult("standard_only", first, station.StationNo, standard.CapacityPerHour, rows, 0, rows.Count, evidence, limitations, missing);
        }
        missing.Add("The current layout (which operations each workstation performs).");
        return new BottleneckResult("insufficient_data", null, null, null, rows, 0, rows.Count, evidence, limitations, missing);
    }
}
