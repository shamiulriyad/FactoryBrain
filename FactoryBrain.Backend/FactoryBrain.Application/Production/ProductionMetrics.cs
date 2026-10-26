using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Application.Production;

/// <summary>
/// The manufacturing formulas, in one place. Units: quantities are whole
/// garments (pieces), time is minutes, rates are pieces per hour, SMV is
/// standard minutes per piece. Percentages are rounded half away from zero
/// to one decimal; a ratio whose denominator is not positive is null
/// ("not calculable"), never 0.
/// </summary>
public static class ProductionMetrics
{
    public static double Round1(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);
    public static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Output variance = actual − planned, pieces, for the same line, order and window.</summary>
    public static int Variance(int actual, int planned) => actual - planned;

    /// <summary>Achievement % = actual ÷ planned × 100; null unless planned &gt; 0. Not line efficiency.</summary>
    public static double? AchievementPct(double actual, double planned) =>
        planned > 0 ? Round1(actual / planned * 100) : null;

    /// <summary>Line efficiency % = earned standard minutes ÷ available production minutes × 100; null unless available &gt; 0.</summary>
    public static double? EfficiencyPct(double earnedMinutes, double availableMinutes) =>
        availableMinutes > 0 ? Round1(earnedMinutes / availableMinutes * 100) : null;

    /// <summary>Required rate, pieces/hour = remaining quantity ÷ remaining scheduled hours; null when no time remains.</summary>
    public static double? RequiredRatePerHour(int remainingQuantity, double remainingMinutes)
    {
        if (remainingQuantity <= 0) return 0;
        return remainingMinutes > 0 ? Round1(remainingQuantity / (remainingMinutes / 60.0)) : null;
    }

    /// <summary>Target output per hour at the planning inputs: operators × 60 × efficiency ÷ SMV.</summary>
    public static double? HourlyTarget(int operators, double targetEfficiencyPct, double? smv) =>
        smv is > 0 && operators > 0 && targetEfficiencyPct > 0
            ? operators * 60.0 * (targetEfficiencyPct / 100.0) / smv.Value
            : null;
}

/// <summary>Where a style's SMV came from.</summary>
public enum SmvBasis { Approved, OperationBulletin, Missing }

public static class StyleSmv
{
    /// <summary>The factory-approved SMV, else the bulletin total, else missing.</summary>
    public static (double? Smv, SmvBasis Basis) Resolve(double? approvedSmv, IEnumerable<double> operationSmvs)
    {
        if (approvedSmv is > 0) return (approvedSmv, SmvBasis.Approved);
        var ops = operationSmvs.ToList();
        if (ops.Count > 0 && ops.All(s => s > 0)) return (Math.Round(ops.Sum(), 4), SmvBasis.OperationBulletin);
        return (null, SmvBasis.Missing);
    }
}

/// <summary>One sewing-output record as the efficiency calculation sees it.</summary>
public sealed record EfficiencyInput(
    int Quantity, double? Smv, int? OperatorsPresent, DateTime StartUtc, DateTime EndUtc, double? DowntimeMinutes);

public sealed record EfficiencyResult(
    double EarnedMinutes,
    double AvailableMinutes,
    double DowntimeOperatorMinutes,
    double? EfficiencyPct,
    double? EfficiencyExcludingDowntimePct,
    int RecordsUsed,
    int RecordsMissingOperators,
    int RecordsMissingSmv);

/// <summary>
/// Line efficiency over sewing-output records:
/// earned = Σ good pieces × style SMV;
/// available = Σ operators present × (interval minutes − planned break minutes inside it).
/// Downtime stays in the available time (it is a loss the line carries); the
/// downtime-excluded figure removes operators × downtime minutes and is
/// reported separately. Records without operators or SMV are left out of
/// both sums and counted, so the figure is never computed on mismatched
/// numerators and denominators.
/// </summary>
public static class EfficiencyCalculator
{
    public static EfficiencyResult Calculate(IEnumerable<EfficiencyInput> records, Func<DateTime, DateTime, double> breakMinutes)
    {
        double earned = 0, available = 0, downtime = 0;
        int used = 0, missingOps = 0, missingSmv = 0;
        foreach (var r in records)
        {
            if (r.OperatorsPresent is not > 0) { missingOps++; continue; }
            if (r.Smv is not > 0) { missingSmv++; continue; }
            var minutes = (r.EndUtc - r.StartUtc).TotalMinutes - breakMinutes(r.StartUtc, r.EndUtc);
            if (minutes <= 0) continue;
            earned += r.Quantity * r.Smv.Value;
            available += r.OperatorsPresent.Value * minutes;
            downtime += r.OperatorsPresent.Value * Math.Min(minutes, Math.Max(0, r.DowntimeMinutes ?? 0));
            used++;
        }
        return new EfficiencyResult(
            ProductionMetrics.Round2(earned), ProductionMetrics.Round2(available), ProductionMetrics.Round2(downtime),
            ProductionMetrics.EfficiencyPct(earned, available),
            ProductionMetrics.EfficiencyPct(earned, available - downtime),
            used, missingOps, missingSmv);
    }
}

public sealed record StageCount(ProductionStage Stage, int Good, int Reject, int Rework, int Records);

public sealed record WipBuffer(
    string Key,
    string Label,
    ProductionStage From,
    ProductionStage To,
    /// <summary>Pieces between the two stages; null when either stage has no records or the counts are inconsistent.</summary>
    int? Pieces,
    /// <summary><c>ok</c>; <c>missing_to</c> (nothing recorded downstream yet: all upstream pieces counted); <c>inconsistent</c>.</summary>
    string State);

/// <summary>
/// Work in process from cumulative stage counts as of one instant. Each
/// buffer is the pieces that left one stage but have not left the next:
/// good at the upstream stage − (good + rejected) at the downstream stage.
/// Rework stays inside the buffer until it is counted again as good. The
/// buffers are reported separately and never summed, so a piece is counted
/// in exactly one place. A negative balance means records are missing
/// upstream; it is reported as inconsistent, not clipped to zero.
/// </summary>
public static class WipCalculator
{
    private static readonly (string Key, string Label, ProductionStage From, ProductionStage To)[] Buffers =
    {
        ("cut_not_loaded", "Cut, not yet loaded to sewing", ProductionStage.Cutting, ProductionStage.SewingInput),
        ("in_sewing", "In sewing (loaded, not yet output)", ProductionStage.SewingInput, ProductionStage.SewingOutput),
        ("awaiting_finishing", "Sewn, not yet through finishing", ProductionStage.SewingOutput, ProductionStage.Finishing),
        ("awaiting_packing", "Finished, not yet packed", ProductionStage.Finishing, ProductionStage.Packing),
    };

    public static IReadOnlyList<WipBuffer> Calculate(IReadOnlyCollection<StageCount> counts)
    {
        StageCount? Get(ProductionStage s) => counts.FirstOrDefault(c => c.Stage == s && c.Records > 0);
        var list = new List<WipBuffer>();
        foreach (var (key, label, from, to) in Buffers)
        {
            var up = Get(from);
            var down = Get(to);
            // A stage that isn't recorded at all (e.g. cutting on a sewing-only floor) has no buffer.
            if (up is null) continue;
            if (down is null) { list.Add(new WipBuffer(key, label, from, to, up.Good, "missing_to")); continue; }
            var pieces = up.Good - (down.Good + down.Reject);
            list.Add(pieces < 0
                ? new WipBuffer(key, label, from, to, null, "inconsistent")
                : new WipBuffer(key, label, from, to, pieces, "ok"));
        }
        return list;
    }
}

/// <summary>How the planned output curve of a plan was derived.</summary>
public enum PlannedOutputMethod
{
    /// <summary>Target rate from operators × efficiency ÷ SMV, capped at the planned quantity.</summary>
    TargetRate,
    /// <summary>No SMV: the planned quantity spread evenly over the plan's scheduled minutes.</summary>
    EvenSpread,
}

/// <summary>
/// A plan's scheduled windows and its planned (cumulative) output over them.
/// Planned output accrues only during scheduled minutes, at the target rate,
/// and stops at the planned quantity; it is evaluated for exactly the window
/// it is compared with.
/// </summary>
public sealed class PlanSchedule
{
    public IReadOnlyList<WorkWindow> Windows { get; }
    public int PlannedQuantity { get; }
    public double RatePerMinute { get; }
    public PlannedOutputMethod Method { get; }
    public double TotalMinutes { get; }
    /// <summary>Pieces the schedule can produce at the target rate (before the planned-quantity cap).</summary>
    public double Capacity => RatePerMinute * TotalMinutes;
    private readonly double[] _cumulativeMinutesBefore;

    public PlanSchedule(IReadOnlyList<WorkWindow> windows, int plannedQuantity, double? hourlyTarget)
    {
        Windows = windows.OrderBy(w => w.StartUtc).ToList();
        PlannedQuantity = plannedQuantity;
        TotalMinutes = Windows.Sum(w => w.Minutes);
        if (hourlyTarget is > 0)
        {
            RatePerMinute = hourlyTarget.Value / 60.0;
            Method = PlannedOutputMethod.TargetRate;
        }
        else
        {
            RatePerMinute = TotalMinutes > 0 ? plannedQuantity / TotalMinutes : 0;
            Method = PlannedOutputMethod.EvenSpread;
        }
        _cumulativeMinutesBefore = new double[Windows.Count];
        double acc = 0;
        for (var i = 0; i < Windows.Count; i++) { _cumulativeMinutesBefore[i] = acc; acc += Windows[i].Minutes; }
    }

    /// <summary>Scheduled minutes before <paramref name="t"/>.</summary>
    public double MinutesBefore(DateTime t)
    {
        double m = 0;
        for (var i = 0; i < Windows.Count; i++)
        {
            var w = Windows[i];
            if (t <= w.StartUtc) break;
            m = _cumulativeMinutesBefore[i] + (t >= w.EndUtc ? w.Minutes : (t - w.StartUtc).TotalMinutes);
        }
        return m;
    }

    /// <summary>Planned cumulative output at instant <paramref name="t"/>, pieces (fractional).</summary>
    public double CumulativeAt(DateTime t) => Math.Min(PlannedQuantity, RatePerMinute * MinutesBefore(t));

    /// <summary>Planned output in [from, to), pieces (fractional).</summary>
    public double PlannedBetween(DateTime from, DateTime to) => to <= from ? 0 : CumulativeAt(to) - CumulativeAt(from);

    /// <summary>Planned output in [from, to) restricted to windows of one shift (null = overtime).</summary>
    public double PlannedBetween(DateTime from, DateTime to, Func<WorkWindow, bool> windowFilter)
    {
        double total = 0;
        foreach (var w in Windows)
        {
            if (!windowFilter(w)) continue;
            var s = w.StartUtc > from ? w.StartUtc : from;
            var e = w.EndUtc < to ? w.EndUtc : to;
            if (e > s) total += CumulativeAt(e) - CumulativeAt(s);
        }
        return total;
    }

    /// <summary>Scheduled minutes of this plan inside [from, to).</summary>
    public double MinutesBetween(DateTime from, DateTime to) => ProductionCalendar.MinutesWithin(Windows, from, to);
}
