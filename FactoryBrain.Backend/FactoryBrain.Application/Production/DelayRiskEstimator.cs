namespace FactoryBrain.Application.Production;

/// <summary>Good sewing output and scheduled line-minutes of one production date (for the observed rate).</summary>
public sealed record DailyRateSample(DateOnly Date, int Pieces, double ScheduledLineMinutes, int Records);

public sealed record DelayRiskInput(
    int OrderQuantity,
    int ProducedGood,
    DateTime DueUtc,
    DateTime NowUtc,
    /// <summary>Scheduled windows of every active plan for the order (lines run in parallel, so minutes add).</summary>
    IReadOnlyList<WorkWindow> PlanWindows,
    /// <summary>Σ hourly targets of the active plans (pieces per hour when all run), if calculable.</summary>
    double? PlannedRatePerLineHour,
    IReadOnlyList<DailyRateSample> Recent,
    int LookbackDays,
    double SlackPct);

public sealed record DelayRiskResult(
    /// <summary><c>complete</c>, <c>low</c>, <c>medium</c>, <c>high</c>, <c>insufficient_data</c>. A heuristic level, not a probability.</summary>
    string Level,
    int RemainingQuantity,
    double RemainingScheduledMinutes,
    double? RequiredRatePerLineHour,
    double? ObservedRatePerLineHour,
    double? ObservedRateLow,
    double? ObservedRateHigh,
    double? PlannedRatePerLineHour,
    DateTime? ProjectedCompletionUtc,
    DateTime? ProjectedCompletionEarliestUtc,
    DateTime? ProjectedCompletionLatestUtc,
    DateTime? ScheduleEndUtc,
    /// <summary>Pieces still missing at the due time if the observed rate holds (≤ 0 means none).</summary>
    int? ProjectedShortfall,
    double ObservedLineHours,
    IReadOnlyList<string> Observations,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> MissingInputs);

/// <summary>
/// Transparent delay-risk baseline for one order.
/// <list type="bullet">
/// <item>Remaining = order quantity − good sewing output recorded so far.</item>
/// <item>Remaining scheduled time = scheduled minutes of the order's active plans between now and the due time (end of the due date), summed over lines.</item>
/// <item>Required rate = remaining ÷ remaining scheduled line-hours.</item>
/// <item>Observed rate = good output ÷ scheduled line-hours over the last N production days (to now). The earliest/latest projections use the lowest and highest daily rate when at least two days have output.</item>
/// <item>Projected completion = walk the remaining plan schedule forward, consuming the remaining quantity at the observed rate.</item>
/// <item>Level: <c>high</c> when the projection misses the due time (or no scheduled time remains), <c>medium</c> when the required rate is within the configured slack of the observed rate, else <c>low</c>.</item>
/// </list>
/// </summary>
public static class DelayRiskEstimator
{
    /// <summary>Fewer observed line-hours than this make the rate a weak basis.</summary>
    public const double MinLineHoursForRate = 2;

    public static DelayRiskResult Estimate(DelayRiskInput i)
    {
        var observations = new List<string>();
        var assumptions = new List<string>
        {
            "Progress counts good pieces at sewing output; rework counts only when it is recorded again as good output.",
            "Only scheduled time in active plans counts as remaining time; unplanned overtime is not assumed.",
            "The level is a rule-based classification, not a calibrated probability of delay.",
        };
        var limitations = new List<string>();
        var missing = new List<string>();
        var remaining = Math.Max(0, i.OrderQuantity - i.ProducedGood);
        var now = ProductionCalendar.AsUtc(i.NowUtc);
        var due = ProductionCalendar.AsUtc(i.DueUtc);
        var future = i.PlanWindows.Where(w => w.EndUtc > now).OrderBy(w => w.StartUtc).ToList();
        var remainingMinutes = ProductionCalendar.MinutesWithin(future, now, due);
        var scheduleEnd = future.Count == 0 ? (DateTime?)null : future.Max(w => w.EndUtc);
        observations.Add($"{i.ProducedGood} of {i.OrderQuantity} pieces recorded as good sewing output; {remaining} remaining.");

        if (remaining == 0)
            return new DelayRiskResult("complete", 0, ProductionMetrics.Round1(remainingMinutes), 0, null, null, null, i.PlannedRatePerLineHour,
                null, null, null, scheduleEnd, 0, 0, observations, assumptions, limitations, missing);

        var required = ProductionMetrics.RequiredRatePerHour(remaining, remainingMinutes);
        observations.Add(remainingMinutes > 0
            ? $"{remainingMinutes / 60:0.#} scheduled line-hours remain before the due time ({due:yyyy-MM-dd HH:mm} UTC)."
            : "No scheduled line time remains before the due time.");

        var samples = i.Recent.Where(s => s.ScheduledLineMinutes > 0).ToList();
        var observedMinutes = samples.Sum(s => s.ScheduledLineMinutes);
        var observedPieces = samples.Sum(s => s.Pieces);
        var records = i.Recent.Sum(s => s.Records);
        double? rate = observedMinutes > 0 && records > 0 ? ProductionMetrics.Round1(observedPieces / (observedMinutes / 60)) : null;
        double? low = null, high = null;
        var withOutput = samples.Where(s => s.Records > 0).ToList();
        if (withOutput.Count >= 2)
        {
            var daily = withOutput.Select(s => s.Pieces / (s.ScheduledLineMinutes / 60)).ToList();
            low = ProductionMetrics.Round1(daily.Min());
            high = ProductionMetrics.Round1(daily.Max());
        }

        if (i.PlanWindows.Count == 0)
        {
            missing.Add("An active production plan for the order (assigned line, shifts and dates).");
        }
        if (rate is null)
        {
            missing.Add($"Recorded sewing output for this order during scheduled plan time in the last {i.LookbackDays} production days.");
        }
        else
        {
            observations.Add($"Observed rate: {rate:0.#} pieces per line-hour ({observedPieces} pieces over {observedMinutes / 60:0.#} scheduled line-hours, last {i.LookbackDays} days).");
            if (observedMinutes / 60 < MinLineHoursForRate)
                limitations.Add($"The observed rate rests on only {observedMinutes / 60:0.#} line-hours of data.");
            if (low is null) limitations.Add("Fewer than two days with output: no range is given for the projection.");
        }
        if (i.PlannedRatePerLineHour is { } planned)
            observations.Add($"Planned target rate: {planned:0.#} pieces per hour across the active plans.");

        if (missing.Count > 0)
            return new DelayRiskResult("insufficient_data", remaining, ProductionMetrics.Round1(remainingMinutes), required, rate, low, high,
                i.PlannedRatePerLineHour, null, null, null, scheduleEnd, null, ProductionMetrics.Round1(observedMinutes / 60),
                observations, assumptions, limitations, missing);

        var projected = Project(future, now, remaining, rate!.Value);
        DateTime? earliest = high is > 0 ? Project(future, now, remaining, high.Value) : null;
        DateTime? latest = low is > 0 ? Project(future, now, remaining, low.Value) : null;
        var shortfall = (int)Math.Ceiling(remaining - rate.Value * remainingMinutes / 60 - 1e-9);
        if (projected is null)
            limitations.Add(rate > 0
                ? "At the observed rate the remaining quantity does not fit in the scheduled plan time, so no completion time is projected."
                : "No good output was recorded in the observed period, so no completion time can be projected.");

        string level;
        if (remainingMinutes <= 0 || projected is null || projected > due) level = "high";
        else if (required is { } req && req >= rate.Value * (1 - i.SlackPct / 100.0)) level = "medium";
        else level = "low";

        return new DelayRiskResult(level, remaining, ProductionMetrics.Round1(remainingMinutes), required, rate, low, high,
            i.PlannedRatePerLineHour, projected, earliest, latest, scheduleEnd, Math.Max(0, shortfall),
            ProductionMetrics.Round1(observedMinutes / 60), observations, assumptions, limitations, missing);
    }

    /// <summary>
    /// The instant at which <paramref name="quantity"/> pieces are done at
    /// <paramref name="ratePerLineHour"/>, consuming scheduled line-minutes
    /// from <paramref name="from"/> (parallel windows add); null if the
    /// schedule ends first.
    /// </summary>
    public static DateTime? Project(IReadOnlyList<WorkWindow> windows, DateTime from, int quantity, double ratePerLineHour)
    {
        if (quantity <= 0) return from;
        if (ratePerLineHour <= 0) return null;
        var needed = quantity / ratePerLineHour * 60.0;
        var points = windows.SelectMany(w => new[] { w.StartUtc, w.EndUtc }).Where(t => t > from).Append(from).Distinct().OrderBy(t => t).ToList();
        double acc = 0;
        for (var k = 0; k + 1 < points.Count; k++)
        {
            var a = points[k];
            var b = points[k + 1];
            var active = windows.Count(w => w.StartUtc <= a && w.EndUtc >= b);
            if (active == 0) continue;
            var segment = (b - a).TotalMinutes * active;
            if (acc + segment >= needed - 1e-9)
                return a.AddMinutes((needed - acc) / active);
            acc += segment;
        }
        return null;
    }
}

/// <summary>Context for recommendations beyond the risk figures themselves.</summary>
public sealed record RecommendationContext(
    int ActiveLines,
    double? ObservedEfficiencyPct,
    double? Smv,
    int? PlannedOperators,
    /// <summary>Per line: the observed constraint operation and its capacity (pieces/hour), if a time study exists.</summary>
    IReadOnlyList<(string LineId, string Operation, double CapacityPerHour, string Basis)> Constraints,
    double DowntimeMinutes,
    IReadOnlyList<string> DowntimeReasons);

public sealed record Recommendation(string Code, string Action, string Reason, string Evidence, string Caveat);

/// <summary>
/// Rule-based, explainable recommendations from a delay-risk result. Every
/// item names the observation it rests on and what to verify; none claims a
/// root cause that the data does not show.
/// </summary>
public static class ProductionRecommender
{
    public static IReadOnlyList<Recommendation> For(DelayRiskResult r, RecommendationContext c)
    {
        var list = new List<Recommendation>();
        if (r.Level == "complete") return list;
        if (r.Level == "insufficient_data")
        {
            list.Add(new Recommendation("record_data", "Record the missing inputs before acting on this order's risk.",
                "Without them any delay estimate would be a guess.", string.Join(" ", r.MissingInputs), "No intervention is suggested until data exists."));
            return list;
        }
        var rate = r.ObservedRatePerLineHour!.Value;
        var required = r.RequiredRatePerLineHour;
        if (r.Level == "low")
        {
            list.Add(new Recommendation("monitor", "Keep the current plan and keep recording hourly output.",
                $"The observed rate ({rate:0.#}/line-hour) is above the rate required to finish on time ({required:0.#}/line-hour).",
                string.Join(" ", r.Observations), "The margin can disappear quickly if output drops; re-check after each shift."));
            return list;
        }

        // Extra scheduled time needed at the observed rate.
        if (rate > 0)
        {
            var neededHours = r.RemainingQuantity / rate;
            var extra = neededHours - r.RemainingScheduledMinutes / 60;
            if (extra > 0)
                list.Add(new Recommendation("overtime", $"Plan about {Math.Ceiling(extra * 10) / 10:0.#} extra line-hours (overtime or an extra shift) before the due date.",
                    $"At {rate:0.#} pieces per line-hour the remaining {r.RemainingQuantity} pieces need {neededHours:0.#} line-hours; {r.RemainingScheduledMinutes / 60:0.#} are scheduled.",
                    $"Observed over {r.ObservedLineHours:0.#} scheduled line-hours.",
                    "Assumes overtime runs at the same rate; check labour-law limits, buyer compliance rules and operator fatigue."));
        }

        // More operators, only when efficiency and SMV make the arithmetic meaningful.
        if (required is > 0 && c.ObservedEfficiencyPct is > 0 && c.Smv is > 0 && c.ActiveLines > 0)
        {
            var perLine = required.Value;
            var operatorsNeeded = Math.Ceiling(perLine * c.Smv.Value / (60 * c.ObservedEfficiencyPct.Value / 100));
            if (c.PlannedOperators is { } planned && operatorsNeeded > planned)
                list.Add(new Recommendation("add_operators", $"Consider staffing about {operatorsNeeded:0} operators per line instead of {planned}.",
                    $"{perLine:0.#} pieces/hour × SMV {c.Smv:0.##} ÷ (60 × observed efficiency {c.ObservedEfficiencyPct:0.#} %) ≈ {operatorsNeeded:0} operators.",
                    "Efficiency is measured from recorded output and operators present.",
                    "Only helps if the constraint is labour, not a machine; new operators need the right skills, and balancing must be redone."));
        }

        // An observed constraint below the required per-line rate.
        foreach (var (line, op, cap, basis) in c.Constraints)
        {
            if (required is > 0 && cap < required.Value)
                list.Add(new Recommendation("relieve_constraint", $"Relieve the constraint on {line}: {op}.",
                    $"Its {(basis == "observed" ? "measured" : "standard")} capacity ({cap:0.#} pieces/hour) is below the rate each line needs ({required.Value:0.#}/hour).",
                    basis == "observed" ? "From the latest time study on that line." : "From SMV and the recorded layout, not measured.",
                    "Options include an extra operator or machine on that operation, or a rebalance; verify with a new time study."));
        }

        if (c.DowntimeMinutes > 0)
        {
            var recovered = rate * c.DowntimeMinutes / 60;
            list.Add(new Recommendation("reduce_downtime", "Review the recorded downtime with maintenance and the line supervisor.",
                $"{c.DowntimeMinutes:0} minutes of downtime were recorded in the observed period; at the observed rate that is roughly {recovered:0} pieces.",
                c.DowntimeReasons.Count > 0 ? $"Recorded reasons: {string.Join(", ", c.DowntimeReasons)}." : "No reasons were recorded.",
                "This is an upper estimate; downtime may also have been planned."));
        }

        if (r.Level == "high")
            list.Add(new Recommendation("escalate", "Discuss the ex-factory date or an additional line with merchandising.",
                r.ProjectedCompletionUtc is { } p ? $"At the observed rate the order completes around {p:yyyy-MM-dd HH:mm} UTC, after the due time." : "The remaining quantity does not fit in the scheduled time at the observed rate.",
                string.Join(" ", r.Observations),
                "The projection is a baseline, not a forecast; output variance alone does not show the cause."));
        return list;
    }
}
