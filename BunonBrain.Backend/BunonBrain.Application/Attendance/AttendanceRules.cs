using System.Text.RegularExpressions;

namespace BunonBrain.Application.Attendance;

public static class AttendanceEventTypes
{
    public const string CheckIn = "CheckIn";
    public const string CheckOut = "CheckOut";
    public const string BreakStart = "BreakStart";
    public const string BreakEnd = "BreakEnd";
    public static readonly IReadOnlyList<string> All = new[] { CheckIn, CheckOut, BreakStart, BreakEnd };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where an event came from. Only <see cref="Device"/> events are hardware events; simulator events are never "real".</summary>
public static class AttendanceSources
{
    public const string Simulator = "Simulator";
    public const string Device = "Device";
    public const string Manual = "Manual";
    public const string Correction = "Correction";
    public const string System = "System";
}

public static class EventOutcomes
{
    public const string Accepted = "Accepted";
    /// <summary>The same punch again within the duplicate window (e.g. a reader double-read): recorded, changes nothing.</summary>
    public const string Duplicate = "Duplicate";
    /// <summary>Recorded but refused (e.g. a check-in while already checked in, a break with no open session).</summary>
    public const string Rejected = "Rejected";
}

public static class SessionStatuses
{
    public const string Open = "Open";
    public const string Completed = "Completed";
    /// <summary>Closed by the review sweep: no check-out arrived in time. Needs a correction.</summary>
    public const string MissingCheckOut = "MissingCheckOut";
    /// <summary>A check-out arrived with no check-in before it. Needs a correction.</summary>
    public const string MissingCheckIn = "MissingCheckIn";
    public static readonly IReadOnlyList<string> All = new[] { Open, Completed, MissingCheckOut, MissingCheckIn };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class BreakTypes
{
    public const string Lunch = "Lunch";
    public const string Tea = "Tea";
    public const string Prayer = "Prayer";
    public const string Personal = "Personal";
    public const string Other = "Other";
    public static readonly IReadOnlyList<string> All = new[] { Lunch, Tea, Prayer, Personal, Other };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class BreakStatuses
{
    public const string Active = "Active";
    public const string Completed = "Completed";
    /// <summary>Ended by the worker's check-out (or the missing-check-out review).</summary>
    public const string AutoClosed = "AutoClosed";
}

public static class OvertimeStatuses
{
    public const string Requested = "Requested";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = new[] { Requested, PendingApproval, Approved, Rejected, InProgress, Completed, Cancelled };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
    public static bool IsOpen(string s) => s is Requested or PendingApproval or Approved or InProgress;
}

public static class CorrectionStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class ShiftAssignmentStatuses
{
    public const string Active = "Active";
    public const string Ended = "Ended";
    public const string Superseded = "Superseded";
}

public static class ReaderKinds
{
    public static readonly IReadOnlyList<string> All = new[] { "Simulated", "Rfid", "Nfc", "Gateway" };
    public static readonly IReadOnlyList<string> Directions = new[] { "Entry", "Exit", "Both" };
}

public static partial class AttendanceRules
{
    public const string DefaultTimeZone = "Asia/Dhaka";
    public const int MaxBulkWorkers = 500;
    public const int ReasonMax = 500;

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-_]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static bool IsCode(string? v) => v is not null && CodePattern().IsMatch(v.Trim().ToUpperInvariant());

    /// <summary>Bit for a day in the working-days mask (bit 0 = Sunday).</summary>
    public static int DayBit(DayOfWeek d) => 1 << (int)d;

    public static bool WorksOn(int mask, DayOfWeek d) => (mask & DayBit(d)) != 0;

    /// <summary>"Sun,Mon,…" ↔ mask, for readable APIs.</summary>
    public static IReadOnlyList<string> DayNames(int mask) =>
        Enum.GetValues<DayOfWeek>().Where(d => WorksOn(mask, d)).Select(d => d.ToString()[..3]).ToList();

    /// <summary>Mask from day names ("Sun", "monday", …); null for an unknown name or an empty list.</summary>
    public static int? MaskFromNames(IEnumerable<string> names)
    {
        var mask = 0;
        foreach (var n in names)
        {
            var key = n.Trim();
            if (key.Length < 3) return null;
            var match = Enum.GetValues<DayOfWeek>().Where(d => d.ToString().StartsWith(key[..3], StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count != 1) return null;
            mask |= DayBit(match[0]);
        }
        return mask == 0 ? null : mask;
    }

    /// <summary>IANA ids → Windows ids, for hosts that can't convert them (Windows with InvariantGlobalization has no ICU).</summary>
    private static readonly Dictionary<string, string> WindowsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Asia/Dhaka"] = "Bangladesh Standard Time",
        ["Asia/Kolkata"] = "India Standard Time",
        ["Asia/Karachi"] = "Pakistan Standard Time",
        ["Asia/Kathmandu"] = "Nepal Standard Time",
        ["Asia/Colombo"] = "Sri Lanka Standard Time",
        ["Asia/Yangon"] = "Myanmar Standard Time",
        ["Asia/Bangkok"] = "SE Asia Standard Time",
        ["Asia/Ho_Chi_Minh"] = "SE Asia Standard Time",
        ["Asia/Jakarta"] = "SE Asia Standard Time",
        ["Asia/Shanghai"] = "China Standard Time",
        ["Asia/Singapore"] = "Singapore Standard Time",
        ["Asia/Dubai"] = "Arabian Standard Time",
        ["Europe/London"] = "GMT Standard Time",
        ["Europe/Istanbul"] = "Turkey Standard Time",
        ["America/New_York"] = "Eastern Standard Time",
    };

    /// <summary>
    /// Resolves an IANA (or Windows) time-zone id, or null if unknown. Asia/Dhaka — the
    /// default — always resolves: Bangladesh has kept a fixed UTC+06:00 with no daylight
    /// saving since 2009, so a fixed zone is exact even where the OS has no tz data.
    /// </summary>
    public static TimeZoneInfo? ResolveTimeZone(string id)
    {
        id = id.Trim();
        if (id.Length == 0) return null;
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "Etc/UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        if (Find(id) is { } zone) return zone;
        if (WindowsIds.TryGetValue(id, out var windows) && Find(windows) is { } mapped) return mapped;
        return string.Equals(id, DefaultTimeZone, StringComparison.OrdinalIgnoreCase) ? DhakaFixed : null;

        static TimeZoneInfo? Find(string zoneId)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
        }
    }

    private static readonly TimeZoneInfo DhakaFixed = TimeZoneInfo.CreateCustomTimeZone(DefaultTimeZone, TimeSpan.FromHours(6), "Bangladesh Standard Time", "Bangladesh Standard Time");
}
