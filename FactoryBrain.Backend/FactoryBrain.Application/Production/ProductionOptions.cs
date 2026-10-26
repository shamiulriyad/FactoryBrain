using System.Globalization;

namespace FactoryBrain.Application.Production;

/// <summary>
/// Production-planning settings, read once from the environment at startup.
/// <list type="bullet">
///   <item><c>PRODUCTION_TIME_ZONE</c>: the plant's time zone (IANA, e.g. <c>Asia/Dhaka</c>, or a Windows id).
///   Shifts, plan dates and due dates are plant-local. Default <c>UTC</c>.</item>
///   <item><c>PRODUCTION_REQUIRE_SEPARATE_APPROVER</c> (default true): the person who submitted a plan or
///   proposal can't be the one whose approval makes it live.</item>
///   <item><c>PRODUCTION_MAX_BACKFILL_DAYS</c> (default 31): how far back output may be recorded or corrected.</item>
///   <item><c>PRODUCTION_MAX_INTERVAL_HOURS</c> (default 12): longest single output interval.</item>
///   <item><c>PRODUCTION_RATE_LOOKBACK_DAYS</c> (default 3): production days used for the observed rate.</item>
///   <item><c>PRODUCTION_RISK_SLACK_PCT</c> (default 10): required rate within this % of the observed rate is medium risk.</item>
///   <item><c>PRODUCTION_OBSERVATION_MAX_AGE_DAYS</c> (default 14): older time studies are ignored by bottleneck analysis.</item>
/// </list>
/// </summary>
public sealed record ProductionOptions(
    TimeZoneInfo TimeZone,
    string TimeZoneId,
    bool RequireSeparateApprover,
    int MaxBackfillDays,
    int MaxIntervalHours,
    int RateLookbackDays,
    double RiskSlackPct,
    int ObservationMaxAgeDays,
    /// <summary>Why a value was rejected (the default was used instead); null when every value is fine.</summary>
    string? ConfigurationError)
{
    /// <summary>Recorded intervals may end at most this far in the future (clock skew between devices).</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static ProductionOptions Default { get; } = FromEnvironment(_ => null);

    public static ProductionOptions FromEnvironment(Func<string, string?> env)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var errors = new List<string>();

        var tzId = Clean(env("PRODUCTION_TIME_ZONE")) ?? "UTC";
        var tz = ResolveTimeZone(tzId);
        if (tz is null)
        {
            // With InvariantGlobalization (the API's setting) Windows has no ICU to map IANA ids, so only
            // Windows ids resolve there; Linux / Docker resolve IANA ids from tzdata.
            errors.Add($"PRODUCTION_TIME_ZONE='{tzId}' is not a time zone this server knows; using UTC. " +
                       "On Windows use the Windows id (e.g. 'Bangladesh Standard Time'); on Linux use the IANA id (e.g. 'Asia/Dhaka').");
            tz = TimeZoneInfo.Utc;
            tzId = "UTC";
        }

        int Int(string name, int def, int min, int max)
        {
            var raw = Clean(env(name));
            if (raw is null) return def;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) return v;
            errors.Add($"{name}='{raw}' must be a whole number from {min} to {max}; using {def}.");
            return def;
        }

        var separate = true;
        var rawSeparate = Clean(env("PRODUCTION_REQUIRE_SEPARATE_APPROVER"));
        if (rawSeparate is not null && !bool.TryParse(rawSeparate, out separate))
        {
            errors.Add($"PRODUCTION_REQUIRE_SEPARATE_APPROVER='{rawSeparate}' must be true or false; using true.");
            separate = true;
        }

        var slack = 10.0;
        var rawSlack = Clean(env("PRODUCTION_RISK_SLACK_PCT"));
        if (rawSlack is not null)
        {
            if (double.TryParse(rawSlack, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s is >= 0 and <= 50) slack = s;
            else errors.Add($"PRODUCTION_RISK_SLACK_PCT='{rawSlack}' must be from 0 to 50; using 10.");
        }

        return new ProductionOptions(tz, tzId, separate,
            Int("PRODUCTION_MAX_BACKFILL_DAYS", 31, 1, 366),
            Int("PRODUCTION_MAX_INTERVAL_HOURS", 12, 1, 24),
            Int("PRODUCTION_RATE_LOOKBACK_DAYS", 3, 1, 30),
            slack,
            Int("PRODUCTION_OBSERVATION_MAX_AGE_DAYS", 14, 1, 365),
            errors.Count == 0 ? null : string.Join(" ", errors));
    }

    /// <summary>Find a zone by IANA or Windows id, converting between the two when the host only knows one form.</summary>
    public static TimeZoneInfo? ResolveTimeZone(string id)
    {
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) return TimeZoneInfo.FindSystemTimeZoneById(win);
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) return TimeZoneInfo.FindSystemTimeZoneById(iana);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return null;
    }
}
