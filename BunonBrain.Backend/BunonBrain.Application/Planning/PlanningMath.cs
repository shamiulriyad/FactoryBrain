using BunonBrain.Application.Attendance;

namespace BunonBrain.Application.Planning;

// Pure planning calculations: shift time, planned-versus-actual, order
// completion forecast. No database, no clock (callers pass "now").

/// <summary>A break inside a shift: timed (has a start) or "when needed" (only a length).</summary>
public sealed record ScheduleBreak(TimeOnly? Start, int Minutes);

/// <summary>A shift of the factory as planning needs it (local times, as configured).</summary>
public sealed record PlanShift(Guid Id, string Code, string Name, TimeOnly Start, TimeOnly End, int WorkingDays, IReadOnlyList<ScheduleBreak> Breaks)
{
    public bool IsOvernight => End <= Start;
    public int GrossMinutes => IsOvernight ? 24 * 60 - (int)(Start - End).TotalMinutes : (int)(End - Start).TotalMinutes;
    /// <summary>Gross minutes minus every break (paid or not: machines are not producing during a break).</summary>
    public int ProductiveMinutes => Math.Max(0, GrossMinutes - Breaks.Sum(b => Math.Max(0, b.Minutes)));
    public bool WorksOn(DayOfWeek d) => AttendanceRules.WorksOn(WorkingDays, d);
}

/// <summary>One shift on one operational date, as UTC instants.</summary>
public sealed class ShiftSchedule
{
    private readonly List<(DateTimeOffset Start, DateTimeOffset End)> _timed = new();
    private readonly int _untimedMinutes;

    public DateOnly Date { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public bool Works { get; }
    public double TotalProductiveMinutes { get; }

    public ShiftSchedule(PlanShift shift, DateOnly date, TimeZoneInfo tz)
    {
        Date = date;
        Works = shift.WorksOn(date.DayOfWeek);
        var occ = AttendanceCalculator.Occurrence(date, new ShiftSpec(shift.Id, shift.Start, shift.End, 0, 0, shift.WorkingDays, Array.Empty<BreakSpec>()), tz);
        Start = occ.Start;
        End = occ.End;
        foreach (var b in shift.Breaks)
        {
            if (b.Minutes <= 0) continue;
            if (b.Start is not { } bs) { _untimedMinutes += b.Minutes; continue; }
            // A break that starts before the shift's start time falls on the next calendar day of an overnight shift.
            var day = shift.IsOvernight && bs < shift.Start ? date.AddDays(1) : date;
            var from = AttendanceCalculator.ToUtc(day, bs, tz);
            _timed.Add((from, from.AddMinutes(b.Minutes)));
        }
        TotalProductiveMinutes = Works ? Math.Max(0, (End - Start).TotalMinutes - _timed.Sum(t => (t.End - t.Start).TotalMinutes) - _untimedMinutes) : 0;
    }

    /// <summary>Productive minutes from the shift start until <paramref name="at"/> (capped to the shift).</summary>
    public double ProductiveMinutesUntil(DateTimeOffset at)
    {
        if (!Works || at <= Start) return 0;
        var until = at < End ? at : End;
        var gross = (until - Start).TotalMinutes;
        var breaks = _timed.Sum(t => Overlap(t.Start, t.End, Start, until));
        var shiftLength = (End - Start).TotalMinutes;
        // "When needed" breaks are spread evenly over the shift.
        var untimed = shiftLength <= 0 ? 0 : _untimedMinutes * ((until - Start).TotalMinutes / shiftLength);
        return Math.Max(0, Math.Min(TotalProductiveMinutes, gross - breaks - untimed));
    }

    public bool HasStarted(DateTimeOffset now) => Works && now >= Start;
    public bool IsOver(DateTimeOffset now) => now >= End;
    public bool InProgress(DateTimeOffset now) => Works && now >= Start && now < End;

    private static double Overlap(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd)
    {
        var s = aStart > bStart ? aStart : bStart;
        var e = aEnd < bEnd ? aEnd : bEnd;
        return e <= s ? 0 : (e - s).TotalMinutes;
    }
}

/// <summary>How a plan item stands against its target. All figures are pieces.</summary>
public sealed record ItemProgress(
    int Target, int? Actual, int? Remaining, double? AchievementPct, int? ExpectedByNow, int? VarianceToExpected, string State, string Basis,
    double ProductiveMinutesElapsed, double ProductiveMinutesTotal);

public static class PlanMath
{
    /// <summary>Output may lag the straight-line expectation by this share before an item counts as behind.</summary>
    public const double DefaultTolerance = 0.10;
    /// <summary>Output this far above the expectation counts as ahead.</summary>
    public const double AheadMargin = 0.05;

    public static double? Pct(double numerator, double denominator) => denominator <= 0 ? null : Math.Round(numerator / denominator * 100.0, 1);

    /// <summary>
    /// Plan item progress. <paramref name="actual"/> is null when there is no
    /// evidence of output (basis NONE): the state is then NO_DATA, never zero output.
    /// The expectation is straight-line over productive minutes; it is a guide, not a promise.
    /// </summary>
    public static ItemProgress Progress(int target, int? actual, string basis, ShiftSchedule schedule, DateTimeOffset now, double tolerance = DefaultTolerance)
    {
        var total = schedule.TotalProductiveMinutes;
        var elapsed = schedule.ProductiveMinutesUntil(now);
        var expected = total > 0 ? (int)Math.Round(target * Math.Min(1.0, elapsed / total), MidpointRounding.AwayFromZero) : 0;
        if (actual is null)
            return new ItemProgress(target, null, null, null, expected, null, schedule.HasStarted(now) ? ItemStates.NoData : ItemStates.NotStarted, basis, elapsed, total);
        var remaining = Math.Max(0, target - actual.Value);
        var pct = Pct(actual.Value, target);
        var variance = actual.Value - expected;
        string state;
        if (actual.Value >= target) state = ItemStates.Complete;
        else if (!schedule.HasStarted(now)) state = ItemStates.NotStarted;
        else if (schedule.IsOver(now)) state = ItemStates.Missed;
        else if (expected > 0 && actual.Value < expected * (1 - tolerance)) state = ItemStates.Behind;
        else if (expected > 0 && actual.Value > expected * (1 + AheadMargin)) state = ItemStates.Ahead;
        else state = ItemStates.OnTrack;
        return new ItemProgress(target, actual, remaining, pct, expected, variance, state, basis, elapsed, total);
    }

    /// <summary>Pieces per hour over productive time; null until time has passed.</summary>
    public static double? HourlyRate(int pieces, double productiveMinutes) =>
        productiveMinutes < 1 ? null : Math.Round(pieces / (productiveMinutes / 60.0), 1);

    /// <summary>Hourly target derived from a shift target: target ÷ productive hours.</summary>
    public static double? HourlyTargetOf(int shiftTarget, double productiveMinutes) =>
        productiveMinutes <= 0 ? null : Math.Round(shiftTarget / (productiveMinutes / 60.0), 1);

    public static string SeverityForShortfall(double achievementOfExpectedPct) =>
        achievementOfExpectedPct < 60 ? Severities.High : achievementOfExpectedPct < 80 ? Severities.Medium : Severities.Low;
}

/// <summary>An order's forecast: when it finishes at the observed pace, against its delivery deadline.</summary>
public sealed record OrderForecast(
    string Status, int Remaining, double? DailyRate, int ObservedDays, int? WorkingDaysNeeded, int WorkingDaysLeft, DateOnly? ProjectedCompletion,
    bool? MissesDeadline, string Basis, string Note);

public static class OrderForecasts
{
    public const string Insufficient = "INSUFFICIENT_DATA";
    public const string Complete = "COMPLETE";
    public const string OnTrack = "ON_TRACK";
    public const string AtRisk = "AT_RISK";
    public const string Overdue = "OVERDUE";

    /// <summary>Days of verified output needed before a forecast is offered.</summary>
    public const int MinObservedDays = 2;
    /// <summary>The pace is the mean of at most this many recent production days.</summary>
    public const int MaxObservedDays = 7;

    /// <summary>
    /// Forecast from verified daily output. <paramref name="dailyOutput"/> holds
    /// completed production days (oldest first) with their verified pieces;
    /// days without any verified count are not in it. The pace is the mean of
    /// the most recent <see cref="MaxObservedDays"/> of them. <paramref name="isWorkingDay"/>
    /// says whether the factory produces on a date; working days after
    /// <paramref name="today"/> up to the deadline are the days left.
    /// </summary>
    public static OrderForecast Forecast(int orderQuantity, int produced, IReadOnlyList<(DateOnly Date, int Pieces)> dailyOutput, DateOnly today,
        DateOnly deadline, Func<DateOnly, bool> isWorkingDay, string basis)
    {
        var remaining = Math.Max(0, orderQuantity - produced);
        var left = 0;
        for (var d = today.AddDays(1); d <= deadline; d = d.AddDays(1)) if (isWorkingDay(d)) left++;
        if (remaining == 0)
            return new OrderForecast(Complete, 0, null, dailyOutput.Count, 0, left, null, false, basis, "All ordered pieces are counted.");
        if (deadline < today)
            return new OrderForecast(Overdue, remaining, null, dailyOutput.Count, null, 0, null, true, basis, $"The delivery deadline ({deadline:yyyy-MM-dd}) has passed with {remaining} pieces not yet counted.");
        var recent = dailyOutput.Where(x => x.Pieces > 0).OrderByDescending(x => x.Date).Take(MaxObservedDays).ToList();
        if (recent.Count < MinObservedDays)
            return new OrderForecast(Insufficient, remaining, null, recent.Count, null, left, null, null, basis,
                $"A forecast needs verified output from at least {MinObservedDays} production days; {recent.Count} so far.");
        var rate = recent.Average(x => (double)x.Pieces);
        var needed = (int)Math.Ceiling(remaining / rate);
        // Today still counts if it is a working day: the projected date walks forward from tomorrow.
        var projected = today;
        var counted = 0;
        while (counted < needed && counted < 3650)
        {
            projected = projected.AddDays(1);
            if (isWorkingDay(projected)) counted++;
        }
        var misses = needed > left;
        return new OrderForecast(misses ? AtRisk : OnTrack, remaining, Math.Round(rate, 1), recent.Count, needed, left, projected, misses, basis,
            $"At the average of the last {recent.Count} production day(s) ({Math.Round(rate, 1)} pieces per day), {needed} more working day(s) are needed; {left} are left before the deadline.");
    }
}
