namespace BunonBrain.Application.Attendance;

/// <summary>A shift as the calculator needs it.</summary>
public sealed record ShiftSpec(
    Guid Id, TimeOnly Start, TimeOnly End, int LateGraceMinutes, int EarlyLeaveGraceMinutes, int WorkingDays,
    IReadOnlyList<BreakSpec> Breaks)
{
    public bool IsOvernight => End <= Start;
    public int ScheduledMinutes => IsOvernight ? 24 * 60 - (int)(Start - End).TotalMinutes : (int)(End - Start).TotalMinutes;
    public int UnpaidBreakMinutes => Breaks.Where(b => !b.IsPaid).Sum(b => b.Minutes);
}

public sealed record BreakSpec(TimeOnly? Start, int Minutes, bool IsPaid);

/// <summary>One concrete shift on one work date: its UTC start and end.</summary>
public sealed record ShiftOccurrence(DateOnly WorkDate, DateTimeOffset Start, DateTimeOffset End);

/// <summary>A taken break: start, end (null while ongoing) and the minutes allowed.</summary>
public sealed record TakenBreak(DateTimeOffset Start, DateTimeOffset? End, int AllowedMinutes);

/// <summary>An approved overtime window.</summary>
public sealed record OvertimeWindow(DateTimeOffset Start, DateTimeOffset End, int ApprovedMinutes);

/// <summary>Everything the calculator derives for one session. All values are whole minutes.</summary>
/// <param name="AttendanceMinutes">Check-in to check-out (0 until both exist).</param>
/// <param name="ScheduledMinutes">Shift start to end (0 without a shift).</param>
/// <param name="ScheduledBreakMinutes">Unpaid breaks the shift schedules.</param>
/// <param name="NetScheduledMinutes">Scheduled minus unpaid scheduled breaks.</param>
/// <param name="BreakMinutes">Breaks actually taken inside the session.</param>
/// <param name="OverBreakMinutes">Break minutes beyond what each break allowed.</param>
/// <param name="NetWorkingMinutes">Attendance minus breaks taken (breaks are never working time).</param>
/// <param name="LateMinutes">Minutes after the scheduled start, when beyond the grace period.</param>
/// <param name="EarlyDepartureMinutes">Minutes before the scheduled end, when beyond the early-leave grace.</param>
/// <param name="BeyondShiftMinutes">Attendance after the scheduled end (not overtime by itself).</param>
/// <param name="ApprovedOvertimeMinutes">Minutes approved for overtime on this session's date.</param>
public sealed record SessionFigures(
    int AttendanceMinutes, int ScheduledMinutes, int ScheduledBreakMinutes, int NetScheduledMinutes,
    int BreakMinutes, int OverBreakMinutes, int NetWorkingMinutes, int LateMinutes, int EarlyDepartureMinutes,
    int BeyondShiftMinutes, int ApprovedOvertimeMinutes)
{
    public bool IsLate => LateMinutes > 0;
    public bool IsEarlyDeparture => EarlyDepartureMinutes > 0;
}

/// <summary>
/// The attendance and time calculation engine. Pure: no database, no clock.
/// Local times (shift start / end) are turned into UTC instants with the
/// factory's time zone; every duration is computed on UTC instants, so an
/// overnight shift or a DST change never produces negative or doubled time.
/// <para>Rules:</para>
/// <list type="bullet">
///   <item>Late: check-in after start + grace counts the whole delay from the start.</item>
///   <item>Early departure: check-out before end − early-leave grace counts the whole time to the end.</item>
///   <item>Breaks taken are clipped to the session and never count as working time.</item>
///   <item>Time after the scheduled end is "beyond shift"; it becomes overtime only through an approved request.</item>
///   <item>Actual overtime = attendance inside the approved window, minus breaks in it.</item>
///   <item>A payable suggestion = min(approved, actual) rounded down; it is only a suggestion for HR.</item>
/// </list>
/// </summary>
public static class AttendanceCalculator
{
    /// <summary>A local wall-clock time on a date → UTC (a time skipped by DST moves forward to the next valid instant).</summary>
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        var offset = tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, tz).DateTime);

    public static ShiftOccurrence Occurrence(DateOnly workDate, ShiftSpec shift, TimeZoneInfo tz)
    {
        var start = ToUtc(workDate, shift.Start, tz);
        var end = ToUtc(shift.IsOvernight ? workDate.AddDays(1) : workDate, shift.End, tz);
        return new ShiftOccurrence(workDate, start, end);
    }

    /// <summary>
    /// The shift occurrence an instant belongs to: the working-day occurrence
    /// (of the day before, the day itself or the day after, so overnight
    /// shifts and early arrivals work) whose window
    /// [start − earlyWindow, end] contains it. Null when none does.
    /// <paramref name="shiftOn"/> gives the worker's shift on a date (or null).
    /// </summary>
    public static ShiftOccurrence? ResolveOccurrence(DateTimeOffset at, TimeZoneInfo tz, int earlyWindowMinutes, Func<DateOnly, ShiftSpec?> shiftOn)
    {
        var day = LocalDate(at, tz);
        ShiftOccurrence? best = null;
        foreach (var d in new[] { day.AddDays(-1), day, day.AddDays(1) })
        {
            var shift = shiftOn(d);
            if (shift is null || !AttendanceRules.WorksOn(shift.WorkingDays, d.DayOfWeek)) continue;
            var occ = Occurrence(d, shift, tz);
            if (at < occ.Start.AddMinutes(-earlyWindowMinutes) || at >= occ.End) continue;
            if (best is null || Math.Abs((occ.Start - at).TotalMinutes) < Math.Abs((best.Start - at).TotalMinutes)) best = occ;
        }
        return best;
    }

    private static int Minutes(TimeSpan span) => span <= TimeSpan.Zero ? 0 : (int)Math.Floor(span.TotalMinutes);

    private static TimeSpan Overlap(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd)
    {
        var s = aStart > bStart ? aStart : bStart;
        var e = aEnd < bEnd ? aEnd : bEnd;
        return e > s ? e - s : TimeSpan.Zero;
    }

    public static SessionFigures Calculate(
        DateTimeOffset? checkIn, DateTimeOffset? checkOut, ShiftSpec? shift, ShiftOccurrence? occurrence,
        IReadOnlyList<TakenBreak> breaks, IReadOnlyList<OvertimeWindow> approvedOvertime)
    {
        var scheduled = occurrence is null || shift is null ? 0 : Minutes(occurrence.End - occurrence.Start);
        var scheduledBreaks = shift is null || occurrence is null ? 0 : shift.UnpaidBreakMinutes;
        var netScheduled = Math.Max(0, scheduled - scheduledBreaks);

        var attendance = checkIn is { } i && checkOut is { } o && o > i ? Minutes(o - i) : 0;

        var breakMinutes = 0;
        var overBreak = 0;
        if (checkIn is { } bi)
        {
            foreach (var b in breaks)
            {
                var end = b.End ?? checkOut;
                if (end is null) continue; // still on break and not checked out: counted once it ends
                var taken = Minutes(Overlap(b.Start, end.Value, bi, checkOut ?? end.Value));
                breakMinutes += taken;
                overBreak += Math.Max(0, taken - b.AllowedMinutes);
            }
        }
        breakMinutes = Math.Min(breakMinutes, attendance);
        var netWorking = Math.Max(0, attendance - breakMinutes);

        int late = 0, early = 0, beyond = 0;
        if (occurrence is not null && shift is not null)
        {
            if (checkIn is { } ci && ci > occurrence.Start.AddMinutes(shift.LateGraceMinutes))
                late = Minutes(ci - occurrence.Start);
            if (checkOut is { } co && co < occurrence.End.AddMinutes(-shift.EarlyLeaveGraceMinutes) && (checkIn is null || co > checkIn))
                early = Minutes(occurrence.End - co);
            if (checkOut is { } co2 && co2 > occurrence.End)
                beyond = Minutes(co2 - (checkIn is { } c3 && c3 > occurrence.End ? c3 : occurrence.End));
        }

        var approved = approvedOvertime.Sum(w => w.ApprovedMinutes);
        return new SessionFigures(attendance, scheduled, scheduledBreaks, netScheduled, breakMinutes, overBreak, netWorking,
            late, early, beyond, approved);
    }

    /// <summary>Attendance minutes inside an approved overtime window, minus breaks taken inside it.</summary>
    public static int ActualOvertimeMinutes(DateTimeOffset? checkIn, DateTimeOffset? checkOut, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        IReadOnlyList<TakenBreak> breaks)
    {
        if (checkIn is not { } i || checkOut is not { } o || o <= i) return 0;
        var present = Overlap(i, o, windowStart, windowEnd);
        if (present <= TimeSpan.Zero) return 0;
        var s = i > windowStart ? i : windowStart;
        var e = o < windowEnd ? o : windowEnd;
        var onBreak = breaks.Where(b => b.End is not null).Aggregate(TimeSpan.Zero, (acc, b) => acc + Overlap(b.Start, b.End!.Value, s, e));
        return Minutes(present - onBreak);
    }

    /// <summary>A suggestion for HR: min(approved, actual), rounded down to the policy step, zero below the minimum.</summary>
    public static int PayableSuggestion(int approvedMinutes, int actualMinutes, int roundingMinutes, int minimumMinutes)
    {
        var raw = Math.Min(approvedMinutes, actualMinutes);
        if (roundingMinutes > 1) raw -= raw % roundingMinutes;
        return raw < minimumMinutes ? 0 : raw;
    }
}
