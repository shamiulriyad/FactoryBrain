namespace FactoryBrain.Application.Production;

/// <summary>One shift of the working calendar, in plant-local time.</summary>
/// <param name="Weekdays">Bitmask, bit 0 = Sunday … bit 6 = Saturday.</param>
public sealed record ShiftSpec(string Code, TimeOnly Start, TimeOnly End, TimeOnly? BreakStart, int BreakMinutes, int Weekdays)
{
    /// <summary>A shift whose end is not after its start runs past midnight.</summary>
    public bool Overnight => End <= Start;

    /// <summary>Clock length of the shift, minutes (break included).</summary>
    public double LengthMinutes => Overnight
        ? (TimeSpan.FromDays(1) - (Start - End)).TotalMinutes
        : (End - Start).TotalMinutes;

    public bool WorksOn(DayOfWeek day) => (Weekdays & (1 << (int)day)) != 0;
}

/// <summary>
/// A span of scheduled production time. <see cref="ShiftCode"/> is null for
/// planned overtime. Times are UTC; <see cref="ProductionDate"/> is the
/// plant-local date the shift started.
/// </summary>
public sealed record WorkWindow(DateTime StartUtc, DateTime EndUtc, DateOnly ProductionDate, string? ShiftCode)
{
    public double Minutes => (EndUtc - StartUtc).TotalMinutes;
    public bool Overtime => ShiftCode is null;
}

/// <summary>
/// The plant's working calendar: which UTC instants are scheduled production
/// time. Shifts are defined in plant-local time (one configured time zone);
/// a shift's production date is the local date it starts, so an overnight
/// shift belongs to the day it began. Planned breaks are not production time.
/// Planned overtime runs directly after the last selected shift of the day.
/// </summary>
public sealed class ProductionCalendar
{
    private readonly TimeZoneInfo _tz;
    private readonly IReadOnlyList<ShiftSpec> _shifts;

    public ProductionCalendar(TimeZoneInfo timeZone, IEnumerable<ShiftSpec> shifts)
    {
        _tz = timeZone;
        _shifts = shifts.OrderBy(s => s.Start).ThenBy(s => s.Code, StringComparer.Ordinal).ToList();
    }

    public TimeZoneInfo TimeZone => _tz;
    public IReadOnlyList<ShiftSpec> Shifts => _shifts;

    /// <summary>
    /// Plant-local wall time → UTC. A time skipped by a DST change moves
    /// forward by the gap; an ambiguous time resolves to its first occurrence.
    /// </summary>
    public DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (_tz.IsInvalidTime(local))
        {
            var probe = local;
            for (var i = 0; i < 180 && _tz.IsInvalidTime(probe); i++) probe = probe.AddMinutes(1);
            local = probe;
        }
        if (_tz.IsAmbiguousTime(local))
        {
            var offsets = _tz.GetAmbiguousTimeOffsets(local);
            var first = offsets.Max(); // the earlier UTC instant has the larger offset
            return DateTime.SpecifyKind(local - first, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, _tz);
    }

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), _tz);

    public DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    /// <summary>Start and end (UTC) of one shift occurring on <paramref name="date"/>, break included.</summary>
    public (DateTime Start, DateTime End) Occurrence(ShiftSpec shift, DateOnly date)
    {
        var start = ToUtc(date, shift.Start);
        var end = ToUtc(shift.Overnight ? date.AddDays(1) : date, shift.End);
        return (start, end);
    }

    /// <summary>The planned break of one shift occurrence (UTC), or null.</summary>
    public (DateTime Start, DateTime End)? BreakOf(ShiftSpec shift, DateOnly date)
    {
        if (shift.BreakStart is not { } bs || shift.BreakMinutes <= 0) return null;
        // An overnight shift's break may fall after midnight.
        var breakDate = shift.Overnight && bs < shift.Start ? date.AddDays(1) : date;
        var start = ToUtc(breakDate, bs);
        return (start, start.AddMinutes(shift.BreakMinutes));
    }

    /// <summary>
    /// Scheduled production windows for one production date: the selected
    /// shifts that work on that weekday, minus their breaks, plus
    /// <paramref name="overtimeMinutes"/> after the last of them.
    /// </summary>
    public IReadOnlyList<WorkWindow> WindowsFor(DateOnly date, IReadOnlyCollection<string>? shiftCodes = null, int overtimeMinutes = 0)
    {
        var result = new List<WorkWindow>();
        DateTime? lastEnd = null;
        foreach (var s in _shifts)
        {
            if (shiftCodes is not null && !shiftCodes.Contains(s.Code)) continue;
            if (!s.WorksOn(date.DayOfWeek)) continue;
            var (start, end) = Occurrence(s, date);
            var brk = BreakOf(s, date);
            if (brk is { } b && b.Start < end && b.End > start)
            {
                if (b.Start > start) result.Add(new WorkWindow(start, Min(b.Start, end), date, s.Code));
                if (b.End < end) result.Add(new WorkWindow(Max(b.End, start), end, date, s.Code));
            }
            else
            {
                result.Add(new WorkWindow(start, end, date, s.Code));
            }
            if (lastEnd is null || end > lastEnd) lastEnd = end;
        }
        if (overtimeMinutes > 0 && lastEnd is { } le)
            result.Add(new WorkWindow(le, le.AddMinutes(overtimeMinutes), date, null));
        return result.OrderBy(w => w.StartUtc).ToList();
    }

    /// <summary>Windows for every production date in [<paramref name="from"/>, <paramref name="to"/>], in time order.</summary>
    public IReadOnlyList<WorkWindow> Windows(DateOnly from, DateOnly to, IReadOnlyCollection<string>? shiftCodes = null, int overtimeMinutes = 0)
    {
        var list = new List<WorkWindow>();
        for (var d = from; d <= to; d = d.AddDays(1)) list.AddRange(WindowsFor(d, shiftCodes, overtimeMinutes));
        return list.OrderBy(w => w.StartUtc).ToList();
    }

    /// <summary>
    /// The production date and shift an instant belongs to. The shift is
    /// null when the instant is outside every shift (overtime or
    /// unscheduled work); its date is then the local calendar date.
    /// An instant inside a planned break still belongs to that shift.
    /// </summary>
    public (DateOnly ProductionDate, string? ShiftCode) Classify(DateTime instantUtc)
    {
        var utc = AsUtc(instantUtc);
        var localDate = LocalDate(utc);
        // An overnight shift that started yesterday may still be running.
        foreach (var date in new[] { localDate.AddDays(-1), localDate })
        {
            foreach (var s in _shifts)
            {
                if (!s.WorksOn(date.DayOfWeek)) continue;
                var (start, end) = Occurrence(s, date);
                if (utc >= start && utc < end) return (date, s.Code);
            }
        }
        return (localDate, null);
    }

    /// <summary>Minutes of [start, end) that fall inside a planned break of any shift.</summary>
    public double BreakMinutesWithin(DateTime startUtc, DateTime endUtc)
    {
        var s = AsUtc(startUtc);
        var e = AsUtc(endUtc);
        if (e <= s) return 0;
        double total = 0;
        for (var d = LocalDate(s).AddDays(-1); d <= LocalDate(e); d = d.AddDays(1))
        {
            foreach (var shift in _shifts)
            {
                if (!shift.WorksOn(d.DayOfWeek)) continue;
                if (BreakOf(shift, d) is { } b) total += OverlapMinutes(s, e, b.Start, b.End);
            }
        }
        return total;
    }

    /// <summary>Scheduled minutes of the given windows that fall inside [from, to).</summary>
    public static double MinutesWithin(IEnumerable<WorkWindow> windows, DateTime fromUtc, DateTime toUtc) =>
        windows.Sum(w => OverlapMinutes(w.StartUtc, w.EndUtc, fromUtc, toUtc));

    public static double OverlapMinutes(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        var start = Max(aStart, bStart);
        var end = Min(aEnd, bEnd);
        return end > start ? (end - start).TotalMinutes : 0;
    }

    /// <summary>
    /// Pairs of shifts whose occurrences overlap in time during a week
    /// (an invalid calendar: an instant could belong to two shifts).
    /// </summary>
    public static IReadOnlyList<(string A, string B)> FindOverlaps(IReadOnlyList<ShiftSpec> shifts)
    {
        // Overlap is a property of local clock time; use UTC as the zone so DST can't create or hide one.
        var cal = new ProductionCalendar(TimeZoneInfo.Utc, shifts);
        var monday = new DateOnly(2026, 1, 5);
        var occ = new List<(string Code, DateTime Start, DateTime End)>();
        for (var d = monday.AddDays(-1); d <= monday.AddDays(7); d = d.AddDays(1))
            foreach (var s in cal._shifts)
                if (s.WorksOn(d.DayOfWeek))
                {
                    var (start, end) = cal.Occurrence(s, d);
                    occ.Add((s.Code, start, end));
                }
        var found = new HashSet<(string, string)>();
        for (var i = 0; i < occ.Count; i++)
            for (var j = i + 1; j < occ.Count; j++)
            {
                if (occ[i].Code == occ[j].Code) continue;
                if (occ[i].Start < occ[j].End && occ[j].Start < occ[i].End)
                {
                    var pair = string.CompareOrdinal(occ[i].Code, occ[j].Code) < 0 ? (occ[i].Code, occ[j].Code) : (occ[j].Code, occ[i].Code);
                    found.Add(pair);
                }
            }
        return found.OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal).ToList();
    }

    public static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
