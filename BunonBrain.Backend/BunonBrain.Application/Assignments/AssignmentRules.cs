using System.Text.RegularExpressions;

namespace BunonBrain.Application.Assignments;

public static class AssignmentStatuses
{
    public const string Active = "Active";
    /// <summary>Ended without a successor (worker left the line, workstation released, status change).</summary>
    public const string Ended = "Ended";
    /// <summary>Closed by a transfer; the next assignment points back at it.</summary>
    public const string Transferred = "Transferred";

    public static readonly IReadOnlyList<string> All = new[] { Active, Ended, Transferred };

    public static string? Parse(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Occupancy of a workstation, as the overview and the line layout label it.</summary>
public static class WorkstationOccupancy
{
    public const string Available = "Available";
    /// <summary>At least one worker assigned, and room for more.</summary>
    public const string Occupied = "Occupied";
    public const string CapacityReached = "CapacityReached";
    public const string Inactive = "Inactive";

    public static readonly IReadOnlyList<string> All = new[] { Available, Occupied, CapacityReached, Inactive };

    public static string Of(bool isActive, int assigned, int capacity) =>
        !isActive ? Inactive
        : assigned <= 0 ? Available
        : assigned >= capacity ? CapacityReached
        : Occupied;

    public static string? Parse(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Limits and formats for workstations, machines and assignments.</summary>
public static partial class AssignmentRules
{
    public const int MinCapacity = 1;
    public const int MaxCapacity = 20;
    public const int CodeMax = 24;
    public const int NameMax = 120;
    public const int ReasonMax = 500;
    public const int NotesMax = 1000;
    public const int MaxBulkWorkstations = 200;

    /// <summary>How far back an assignment / transfer may be recorded (the paper log arrives late).</summary>
    public static readonly TimeSpan MaxBackdate = TimeSpan.FromDays(31);
    /// <summary>Clock difference tolerated for "now" sent by a browser.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-_/.]{0,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    /// <summary>Workstation and machine codes: letters, digits, '-', '_', '/', '.'; stored in upper case.</summary>
    public static bool IsCode(string? value) => value is not null && CodePattern().IsMatch(value.Trim().ToUpperInvariant());

    public static string NormaliseCode(string value) => value.Trim().ToUpperInvariant();

    /// <summary>
    /// Null when <paramref name="at"/> may be used as an assignment start or
    /// transfer time: not in the future (beyond clock skew), not older than
    /// <see cref="MaxBackdate"/>, and after <paramref name="notBefore"/>
    /// (the start of the assignment being closed).
    /// </summary>
    public static string? EffectiveTimeProblem(DateTimeOffset at, DateTimeOffset now, DateTimeOffset? notBefore = null)
    {
        if (at > now + MaxClockSkew) return "The time can't be in the future. Record the change when it happens.";
        if (at < now - MaxBackdate) return $"The time can't be more than {MaxBackdate.TotalDays:0} days in the past.";
        if (notBefore is { } nb && at <= nb) return "The time must be after the start of the current assignment.";
        return null;
    }

    /// <summary>Codes for a bulk creation: <c>T-</c> + 1..30 → T-001 … T-030 (at least <paramref name="digits"/> digits).</summary>
    public static IReadOnlyList<string> BulkCodes(string prefix, int from, int count, int digits = 3)
    {
        var p = prefix.Trim().ToUpperInvariant();
        return Enumerable.Range(from, count).Select(n => p + n.ToString("D" + Math.Clamp(digits, 1, 6))).ToList();
    }
}
