namespace BunonBrain.Application.Attendance;

/// <summary>Why a scheduled worker was not expected on a day.</summary>
public static class ExcusalKinds
{
    public const string Holiday = "HOLIDAY";
    public const string Leave = "LEAVE";
}

/// <summary>An approved leave of one worker (inclusive dates), from an approved Leave Record document.</summary>
public sealed record ApprovedLeave(Guid WorkerId, DateOnly From, DateOnly To, string LeaveType, string Reference);

/// <summary>A factory holiday.</summary>
public sealed record HolidayDay(Guid FactoryId, DateOnly Date, string Name, string Kind);

/// <summary>A scheduled worker-day that is not expected (holiday or approved leave), and whether the worker came anyway.</summary>
public sealed record ExcusedDay(Guid WorkerId, DateOnly Date, string Kind, string Detail, string? Reference);

/// <summary>
/// Holidays and approved leave, for attendance calculations. A scheduled worker-day on a factory
/// holiday or inside an approved leave is <b>excused</b>: it is not expected, so it is never absence.
/// A holiday wins over leave (the day would not have been taken from the leave balance). Someone who
/// works on an excused day is still present. Approved leave and holidays never change recorded sessions.
/// </summary>
public sealed class AttendanceCalendar
{
    public static readonly AttendanceCalendar Empty = new(Array.Empty<ApprovedLeave>(), Array.Empty<HolidayDay>());

    private readonly Dictionary<Guid, List<ApprovedLeave>> _leave;
    private readonly Dictionary<(Guid, DateOnly), HolidayDay> _holidays;

    public AttendanceCalendar(IEnumerable<ApprovedLeave> leave, IEnumerable<HolidayDay> holidays)
    {
        _leave = leave.GroupBy(l => l.WorkerId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.From).ToList());
        _holidays = holidays.GroupBy(h => (h.FactoryId, h.Date)).ToDictionary(g => g.Key, g => g.First());
    }

    public IReadOnlyCollection<HolidayDay> Holidays => _holidays.Values;

    public HolidayDay? HolidayOn(Guid factoryId, DateOnly date) => _holidays.GetValueOrDefault((factoryId, date));

    public ApprovedLeave? LeaveOn(Guid workerId, DateOnly date) =>
        _leave.TryGetValue(workerId, out var list) ? list.FirstOrDefault(l => l.From <= date && l.To >= date) : null;

    /// <summary>Holiday first, then approved leave; null when the worker is expected.</summary>
    public ExcusedDay? Excuse(Guid workerId, Guid factoryId, DateOnly date)
    {
        if (HolidayOn(factoryId, date) is { } h) return new ExcusedDay(workerId, date, ExcusalKinds.Holiday, h.Name, null);
        if (LeaveOn(workerId, date) is { } l) return new ExcusedDay(workerId, date, ExcusalKinds.Leave, l.LeaveType, l.Reference);
        return null;
    }

    /// <summary>
    /// Split scheduled worker-days into the expected ones and the excused ones.
    /// <paramref name="factoryOf"/> gives a worker's factory (holidays are per factory).
    /// </summary>
    public (List<T> Expected, List<ExcusedDay> Excused) Apply<T>(IEnumerable<T> scheduled, Func<T, Guid> worker, Func<T, DateOnly> date, Func<Guid, Guid?> factoryOf)
    {
        var expected = new List<T>();
        var excused = new List<ExcusedDay>();
        foreach (var s in scheduled)
        {
            var w = worker(s);
            var d = date(s);
            var e = factoryOf(w) is { } f ? Excuse(w, f, d) : LeaveOn(w, d) is { } l ? new ExcusedDay(w, d, ExcusalKinds.Leave, l.LeaveType, l.Reference) : null;
            if (e is null) expected.Add(s); else excused.Add(e);
        }
        return (expected, excused);
    }
}
