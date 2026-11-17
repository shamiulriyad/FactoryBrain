namespace BunonBrain.Application.Documents;

public static class ScheduleFrequencies
{
    public const string Daily = "DAILY";
    public const string Weekly = "WEEKLY";
    public const string Monthly = "MONTHLY";
    public static readonly string[] All = { Daily, Weekly, Monthly };
}

public static class ScheduleScopes
{
    /// <summary>One document for the whole factory.</summary>
    public const string Factory = "FACTORY";
    /// <summary>One document per active production line (types prepared per line).</summary>
    public const string EachLine = "EACH_LINE";
    public static readonly string[] All = { Factory, EachLine };
}

public static class ScheduleRunStatuses
{
    public const string Created = "CREATED";
    public const string Exists = "EXISTS";
    public const string NoData = "NO_DATA";
    public const string Failed = "FAILED";
}

/// <summary>
/// Scheduled generation rules. A report is generated only for a <b>complete</b> period
/// (yesterday, the previous week, the previous month), never for one still running,
/// and at most once per factory, type, period and scope.
/// </summary>
public static class DocumentSchedule
{
    /// <summary>Failed attempts after which a period is no longer retried (document controllers are told once).</summary>
    public const int MaxFailedAttempts = 3;
    /// <summary>Days after a period ends during which a period without source records is still retried (late data entry).</summary>
    public const int NoDataRetryDays = 3;

    /// <summary>Frequencies a type can be scheduled with: day reports daily; period reports weekly or monthly; forms never.</summary>
    public static IReadOnlyList<string> Allowed(DocumentTypeDef t) => !t.Generated ? Array.Empty<string>()
        : t.Period == PeriodKind.Day ? new[] { ScheduleFrequencies.Daily }
        : t.Period == PeriodKind.Range ? new[] { ScheduleFrequencies.Weekly, ScheduleFrequencies.Monthly }
        : Array.Empty<string>();

    public static string DefaultFrequency(DocumentTypeDef t) => t.Period == PeriodKind.Range ? ScheduleFrequencies.Weekly : ScheduleFrequencies.Daily;

    /// <summary>The frequency in effect: the saved one when the type allows it, else the type's default.</summary>
    public static string Frequency(DocumentTypeDef t, string? saved) => saved is not null && Allowed(t).Contains(saved) ? saved : DefaultFrequency(t);

    /// <summary>Types that only exist per line (a line-wise report) are always scheduled per line.</summary>
    public static bool RequiresLine(DocumentTypeDef t) => t.Code == "production.line";

    public static string Scope(DocumentTypeDef t, string? saved) =>
        RequiresLine(t) ? ScheduleScopes.EachLine : t.LineOption && saved == ScheduleScopes.EachLine ? ScheduleScopes.EachLine : ScheduleScopes.Factory;

    /// <summary>The most recent period of this frequency that ended before <paramref name="today"/> (factory-local).</summary>
    public static (DateOnly From, DateOnly To) LatestCompletePeriod(string frequency, DateOnly today, int weekStartDay)
    {
        switch (frequency)
        {
            case ScheduleFrequencies.Weekly:
            {
                var start = (DayOfWeek)Math.Clamp(weekStartDay, 0, 6);
                var thisWeek = today.AddDays(-(((int)today.DayOfWeek - (int)start + 7) % 7));
                return (thisWeek.AddDays(-7), thisWeek.AddDays(-1));
            }
            case ScheduleFrequencies.Monthly:
            {
                var first = new DateOnly(today.Year, today.Month, 1);
                return (first.AddMonths(-1), first.AddDays(-1));
            }
            default:
                return (today.AddDays(-1), today.AddDays(-1));
        }
    }
}
