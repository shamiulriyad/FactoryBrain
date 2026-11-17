using BunonBrain.Application.Attendance;
using BunonBrain.Application.Iot;

namespace BunonBrain.Application.Analytics;

// Pure analytics calculations (no database): periods and time zones, KPI
// values with their availability, machine availability, attendance per day,
// overtime and AI confidence bands. The infrastructure layer only gathers
// rows; every formula lives here and is unit-tested.

/// <summary>Whether a KPI can be shown, and why not.</summary>
public static class Availability
{
    public const string Ok = "OK";
    /// <summary>No underlying records: shown as "no data", never as 0 %.</summary>
    public const string NoData = "NO_DATA";
    /// <summary>Some sources are missing or incomplete (see the note).</summary>
    public const string Partial = "PARTIAL";
    /// <summary>The caller's role doesn't include this data.</summary>
    public const string NotPermitted = "NOT_PERMITTED";
}

/// <summary>One KPI value with its definition and denominator.</summary>
public sealed record Kpi(
    string MetricId,
    string Label,
    double? Value,
    string Unit,
    double? Numerator,
    double? Denominator,
    string Kind,
    string Availability,
    string? Note)
{
    public static Kpi Count(string metricId, double value, string? note = null, string? availability = null)
    {
        var m = MetricCatalog.Get(metricId);
        return new Kpi(metricId, m.Name, value, m.Unit, null, null, m.Kind, availability ?? Analytics.Availability.Ok, note);
    }

    /// <summary>A ratio in % — unavailable (not 0) when the denominator is 0.</summary>
    public static Kpi Ratio(string metricId, double numerator, double denominator, string? note = null)
    {
        var m = MetricCatalog.Get(metricId);
        return denominator <= 0
            ? new Kpi(metricId, m.Name, null, m.Unit, numerator, denominator, m.Kind, Analytics.Availability.NoData, note ?? m.MissingData)
            : new Kpi(metricId, m.Name, AnalyticsMath.Pct(numerator, denominator), m.Unit, numerator, denominator, m.Kind, Analytics.Availability.Ok, note);
    }

    public static Kpi Missing(string metricId, string? note = null)
    {
        var m = MetricCatalog.Get(metricId);
        return new Kpi(metricId, m.Name, null, m.Unit, null, null, m.Kind, Analytics.Availability.NoData, note ?? m.MissingData);
    }

    public static Kpi Hidden(string metricId, string permission)
    {
        var m = MetricCatalog.Get(metricId);
        return new Kpi(metricId, m.Name, null, m.Unit, null, null, m.Kind, Analytics.Availability.NotPermitted, $"Needs {permission}.");
    }
}

public static class AnalyticsMath
{
    public static double? Pct(double numerator, double denominator) => denominator <= 0 ? null : Math.Round(numerator / denominator * 100.0, 1);
    public static double Hours(double seconds) => Math.Round(seconds / 3600.0, 2);
    public static double? Per(double value, double per) => per <= 0 ? null : Math.Round(value / per, 2);
}

/// <summary>A resolved analytics period: factory-local days and their UTC bounds.</summary>
/// <param name="FromUtc">Start of the first local day.</param>
/// <param name="ToUtc">End of the last local day, or now when the day is still running (data never reaches into the future).</param>
public sealed record AnalyticsPeriod(DateOnly From, DateOnly To, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string TimeZone, bool Truncated)
{
    public const int MaxDays = 93;

    public int Days => To.DayNumber - From.DayNumber + 1;

    public IEnumerable<DateOnly> Dates()
    {
        for (var d = From; d <= To; d = d.AddDays(1)) yield return d;
    }

    /// <summary>Default: today. A range over <see cref="MaxDays"/> keeps its end and is shortened (Truncated).</summary>
    public static AnalyticsPeriod Resolve(DateOnly? from, DateOnly? to, TimeZoneInfo tz, DateTimeOffset now)
    {
        var today = AttendanceCalculator.LocalDate(now, tz);
        var end = to ?? today;
        if (end > today) end = today;
        var start = from ?? end;
        if (start > end) start = end;
        var truncated = false;
        if (end.DayNumber - start.DayNumber + 1 > MaxDays) { start = end.AddDays(-(MaxDays - 1)); truncated = true; }
        var fromUtc = AttendanceCalculator.ToUtc(start, TimeOnly.MinValue, tz);
        var toUtc = AttendanceCalculator.ToUtc(end.AddDays(1), TimeOnly.MinValue, tz);
        if (toUtc > now) toUtc = now;
        return new AnalyticsPeriod(start, end, fromUtc, toUtc, tz.Id, truncated);
    }

    public (DateTimeOffset Start, DateTimeOffset End) Day(DateOnly date, TimeZoneInfo tz, DateTimeOffset now)
    {
        var s = AttendanceCalculator.ToUtc(date, TimeOnly.MinValue, tz);
        var e = AttendanceCalculator.ToUtc(date.AddDays(1), TimeOnly.MinValue, tz);
        return (s, e > now ? now : e);
    }
}

/// <summary>Machine availability from its time per state. Missing data is a gap, never downtime.</summary>
public sealed record MachineAvailability(
    double RunningSeconds, double IdleSeconds, double StoppedSeconds, double ErrorSeconds, double MaintenanceSeconds, double OfflineSeconds, double UnknownSeconds)
{
    public double KnownSeconds => RunningSeconds + IdleSeconds + StoppedSeconds + ErrorSeconds;
    public double DowntimeSeconds => StoppedSeconds + ErrorSeconds;
    public double GapSeconds => OfflineSeconds + UnknownSeconds;
    public double PeriodSeconds => KnownSeconds + MaintenanceSeconds + GapSeconds;

    /// <summary>Running ÷ confirmed-state time.</summary>
    public double? UptimePct => AnalyticsMath.Pct(RunningSeconds, KnownSeconds);
    public double? DowntimePct => AnalyticsMath.Pct(DowntimeSeconds, KnownSeconds);
    /// <summary>Confirmed-state time ÷ (period − maintenance).</summary>
    public double? CoveragePct => AnalyticsMath.Pct(KnownSeconds, PeriodSeconds - MaintenanceSeconds);

    public static MachineAvailability From(StateTotals t) => new(t.Of(MachineStates.Running), t.Of(MachineStates.Idle), t.Of(MachineStates.Stopped),
        t.Of(MachineStates.Error), t.Of(MachineStates.Maintenance), t.Of(MachineStates.Offline), t.Of(MachineStates.Unknown));

    public static MachineAvailability Sum(IEnumerable<MachineAvailability> items)
    {
        double r = 0, i = 0, s = 0, e = 0, m = 0, o = 0, u = 0;
        foreach (var x in items) { r += x.RunningSeconds; i += x.IdleSeconds; s += x.StoppedSeconds; e += x.ErrorSeconds; m += x.MaintenanceSeconds; o += x.OfflineSeconds; u += x.UnknownSeconds; }
        return new MachineAvailability(r, i, s, e, m, o, u);
    }
}

/// <summary>A scheduled worker-day.</summary>
public sealed record ScheduledDay(Guid WorkerId, DateOnly Date, Guid ShiftId, DateTimeOffset ShiftStartUtc, int LateGraceMinutes);

/// <summary>A session as the attendance aggregation needs it.</summary>
public sealed record SessionFact(Guid WorkerId, DateOnly Date, bool CheckedIn, bool Late, bool EarlyDeparture, int AttendanceMinutes, int BreakMinutes,
    int OverBreakMinutes, int BeyondShiftMinutes, bool NeedsReview);

public sealed record AttendanceDayStats(DateOnly Date, int Scheduled, int ScheduledStarted, int ScheduledPresent, int Present, int Unscheduled, int Absent,
    int Late, int EarlyDeparture, int AttendanceMinutes, int BreakMinutes, int OverBreakMinutes, int BeyondShiftMinutes, int NeedsReview)
{
    /// <summary>Scheduled workers who checked in ÷ scheduled workers whose shift has started.</summary>
    public double? RatePct => AnalyticsMath.Pct(ScheduledPresent, ScheduledStarted);
}

public static class AttendanceAggregation
{
    /// <summary>
    /// Per-day attendance from schedules and sessions. A worker counts once
    /// per day however many sessions they have; absence needs a schedule and
    /// a started shift (plus grace); unscheduled check-ins count as present
    /// but stay out of the rate.
    /// </summary>
    public static IReadOnlyList<AttendanceDayStats> Daily(IEnumerable<DateOnly> dates, IReadOnlyCollection<ScheduledDay> scheduled,
        IReadOnlyCollection<SessionFact> sessions, DateTimeOffset now)
    {
        var byDay = sessions.GroupBy(s => s.Date).ToDictionary(g => g.Key, g => g.ToList());
        var schedByDay = scheduled.GroupBy(s => s.Date).ToDictionary(g => g.Key, g => g.GroupBy(x => x.WorkerId).Select(x => x.OrderBy(y => y.ShiftStartUtc).First()).ToList());
        var result = new List<AttendanceDayStats>();
        foreach (var d in dates)
        {
            var rows = byDay.GetValueOrDefault(d) ?? new List<SessionFact>();
            var sched = schedByDay.GetValueOrDefault(d) ?? new List<ScheduledDay>();
            var presentIds = rows.Where(r => r.CheckedIn).Select(r => r.WorkerId).ToHashSet();
            var anySession = rows.Select(r => r.WorkerId).ToHashSet();
            var started = sched.Where(s => now > s.ShiftStartUtc.AddMinutes(s.LateGraceMinutes)).ToList();
            var schedIds = sched.Select(s => s.WorkerId).ToHashSet();
            result.Add(new AttendanceDayStats(d, sched.Count, started.Count, started.Count(s => presentIds.Contains(s.WorkerId)), presentIds.Count,
                presentIds.Count(id => !schedIds.Contains(id)), started.Count(s => !anySession.Contains(s.WorkerId)),
                rows.Where(r => r.Late).Select(r => r.WorkerId).Distinct().Count(), rows.Where(r => r.EarlyDeparture).Select(r => r.WorkerId).Distinct().Count(),
                rows.Sum(r => r.AttendanceMinutes), rows.Sum(r => r.BreakMinutes), rows.Sum(r => r.OverBreakMinutes), rows.Sum(r => r.BeyondShiftMinutes),
                rows.Count(r => r.NeedsReview)));
        }
        return result;
    }

    public static AttendanceDayStats Total(IReadOnlyList<AttendanceDayStats> days) => new(days.Count == 0 ? default : days[0].Date,
        days.Sum(x => x.Scheduled), days.Sum(x => x.ScheduledStarted), days.Sum(x => x.ScheduledPresent), days.Sum(x => x.Present), days.Sum(x => x.Unscheduled),
        days.Sum(x => x.Absent), days.Sum(x => x.Late), days.Sum(x => x.EarlyDeparture), days.Sum(x => x.AttendanceMinutes), days.Sum(x => x.BreakMinutes),
        days.Sum(x => x.OverBreakMinutes), days.Sum(x => x.BeyondShiftMinutes), days.Sum(x => x.NeedsReview));

    /// <summary>The scheduled worker-days of a period from shift assignments (shift works on the weekday, assignment in force).</summary>
    public static IReadOnlyList<ScheduledDay> Schedule(IEnumerable<DateOnly> dates, IReadOnlyCollection<ShiftAssignmentFact> assignments, TimeZoneInfo tz)
    {
        var list = new List<ScheduledDay>();
        foreach (var d in dates)
            foreach (var a in assignments)
                if (a.From <= d && (a.To is null || a.To >= d) && AttendanceRules.WorksOn(a.WorkingDays, d.DayOfWeek))
                    list.Add(new ScheduledDay(a.WorkerId, d, a.ShiftId, AttendanceCalculator.ToUtc(d, a.Start, tz), a.LateGraceMinutes));
        return list;
    }
}

public sealed record ShiftAssignmentFact(Guid WorkerId, Guid ShiftId, DateOnly From, DateOnly? To, TimeOnly Start, int LateGraceMinutes, int WorkingDays);

public sealed record OvertimeFact(Guid WorkerId, string? LineId, DateOnly Date, string Status, int RequestedMinutes, int? ApprovedMinutes, int? ActualMinutes);

public sealed record OvertimeSummary(int Requests, int Approved, int Pending, int Rejected, int RequestedMinutes, int ApprovedMinutes, int ActualMinutes, int WorkersWithOvertime)
{
    /// <summary>Actual ÷ approved minutes × 100 (over 100 = more worked than approved).</summary>
    public double? ActualVsApprovedPct => AnalyticsMath.Pct(ActualMinutes, ApprovedMinutes);

    private static readonly string[] Granted = { OvertimeStatuses.Approved, OvertimeStatuses.InProgress, OvertimeStatuses.Completed };

    public static OvertimeSummary Of(IReadOnlyCollection<OvertimeFact> rows)
    {
        var granted = rows.Where(r => Granted.Contains(r.Status)).ToList();
        return new OvertimeSummary(rows.Count, granted.Count, rows.Count(r => r.Status is OvertimeStatuses.Requested or OvertimeStatuses.PendingApproval),
            rows.Count(r => r.Status == OvertimeStatuses.Rejected), rows.Sum(r => r.RequestedMinutes), granted.Sum(r => r.ApprovedMinutes ?? 0),
            granted.Sum(r => r.ActualMinutes ?? 0), granted.Select(r => r.WorkerId).Distinct().Count());
    }
}

public static class ConfidenceBands
{
    public static readonly IReadOnlyList<(string Label, double Min, double Max)> All = new[]
    {
        ("< 0.5", 0.0, 0.5), ("0.5 – 0.7", 0.5, 0.7), ("0.7 – 0.9", 0.7, 0.9), ("≥ 0.9", 0.9, 1.0001),
    };

    public static string Of(double c) => All.First(b => c >= b.Min && c < b.Max).Label;
}
