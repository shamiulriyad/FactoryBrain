using System.Globalization;
using System.Text.RegularExpressions;

namespace BunonBrain.Application.Workers;

/// <summary>Employment statuses of a worker.</summary>
public static class WorkerStatuses
{
    public const string Active = "Active";
    public const string Inactive = "Inactive";
    public const string Suspended = "Suspended";
    public const string Resigned = "Resigned";
    public const string Terminated = "Terminated";

    public static readonly IReadOnlyList<string> All = new[] { Active, Inactive, Suspended, Resigned, Terminated };

    /// <summary>The canonical spelling of <paramref name="value"/> (case-insensitive), or null.</summary>
    public static string? Parse(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The worker has left the company (kept on record, never deleted).</summary>
    public static bool IsSeparated(string status) => status is Resigned or Terminated;
}

public static class EmploymentTypes
{
    public const string Permanent = "Permanent";
    public const string Probation = "Probation";
    public const string Contract = "Contract";
    public const string Temporary = "Temporary";
    public const string Apprentice = "Apprentice";

    public static readonly IReadOnlyList<string> All = new[] { Permanent, Probation, Contract, Temporary, Apprentice };

    public static string? Parse(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class Genders
{
    public const string Female = "Female";
    public const string Male = "Male";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = new[] { Female, Male, Other };

    public static string? Parse(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class CardTypes
{
    public const string Rfid = "RFID";
    public const string Nfc = "NFC";
    public static readonly IReadOnlyList<string> All = new[] { Rfid, Nfc };

    public static string? Parse(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class CardDeactivationReasons
{
    public const string Returned = "Returned";
    public const string Lost = "Lost";
    public const string Damaged = "Damaged";
    public const string Replaced = "Replaced";
    /// <summary>Set by the system when the worker resigns or is terminated.</summary>
    public const string WorkerLeft = "WorkerLeft";

    /// <summary>Reasons a person may choose.</summary>
    public static readonly IReadOnlyList<string> Selectable = new[] { Returned, Lost, Damaged, Replaced };

    public static string? Parse(string? value) =>
        Selectable.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The outcome of checking a requested status change.</summary>
public sealed record TransitionCheck(bool Allowed, string? Code = null, string? Message = null)
{
    public static readonly TransitionCheck Ok = new(true);
    public static TransitionCheck Refuse(string code, string message) => new(false, code, message);
}

/// <summary>
/// Which worker status may follow which, and when a reason is mandatory.
/// <code>
/// Active     → Inactive, Suspended, Resigned, Terminated
/// Inactive   → Active, Resigned, Terminated
/// Suspended  → Active, Resigned, Terminated
/// Resigned   → Active   (re-hire)
/// Terminated → Active   (reinstatement)
/// </code>
/// A reason is required to suspend, to record a resignation or termination,
/// and to bring back someone who had left.
/// </summary>
public static class WorkerStatusTransitions
{
    private static readonly Dictionary<string, string[]> Next = new(StringComparer.Ordinal)
    {
        [WorkerStatuses.Active] = new[] { WorkerStatuses.Inactive, WorkerStatuses.Suspended, WorkerStatuses.Resigned, WorkerStatuses.Terminated },
        [WorkerStatuses.Inactive] = new[] { WorkerStatuses.Active, WorkerStatuses.Resigned, WorkerStatuses.Terminated },
        [WorkerStatuses.Suspended] = new[] { WorkerStatuses.Active, WorkerStatuses.Resigned, WorkerStatuses.Terminated },
        [WorkerStatuses.Resigned] = new[] { WorkerStatuses.Active },
        [WorkerStatuses.Terminated] = new[] { WorkerStatuses.Active },
    };

    /// <summary>Statuses that may follow <paramref name="from"/> (empty for an unknown status).</summary>
    public static IReadOnlyList<string> AllowedFrom(string from) =>
        Next.TryGetValue(from, out var next) ? next : Array.Empty<string>();

    public static bool ReasonRequired(string from, string to) =>
        to is WorkerStatuses.Suspended or WorkerStatuses.Resigned or WorkerStatuses.Terminated
        || WorkerStatuses.IsSeparated(from);

    public static TransitionCheck Check(string from, string to, string? reason)
    {
        if (WorkerStatuses.Parse(to) is null)
            return TransitionCheck.Refuse("unknown_status", $"Status must be one of: {string.Join(", ", WorkerStatuses.All)}.");
        if (string.Equals(from, to, StringComparison.Ordinal))
            return TransitionCheck.Refuse("status_unchanged", $"The worker is already {to}.");
        if (!AllowedFrom(from).Contains(to))
            return TransitionCheck.Refuse("status_transition_not_allowed",
                $"A worker who is {from} can't be set to {to}. Allowed: {string.Join(", ", AllowedFrom(from))}.");
        if (ReasonRequired(from, to) && string.IsNullOrWhiteSpace(reason))
            return TransitionCheck.Refuse("status_reason_required", "Give a reason for this status change.");
        return TransitionCheck.Ok;
    }
}

/// <summary>Field limits and formats shared by the API validators and the CSV import.</summary>
public static partial class WorkerRules
{
    public const int NameMax = 120;
    public const int EmployeeCodeMax = 32;
    public const int DesignationMax = 80;
    public const int GradeMax = 32;
    public const int PhoneMax = 32;
    public const int EmailMax = 256;
    public const int AddressMax = 500;
    public const int RelationMax = 40;
    public const int WorkstationMax = 32;
    public const int ReasonMax = 500;
    public const int CardUidMax = 64;
    public const int CardLabelMax = 80;
    public const int MinimumAgeYears = 14;

    /// <summary>The first Worker ID has three digits (W-001); longer numbers simply grow (W-1000).</summary>
    public static string FormatWorkerCode(long number)
    {
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
        return "W-" + number.ToString("D3", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^W-[0-9]{3,18}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WorkerCodePattern();

    public static bool LooksLikeWorkerCode(string? value) => value is not null && WorkerCodePattern().IsMatch(value.Trim());

    public static string NormaliseWorkerCode(string value) => value.Trim().ToUpperInvariant();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_/]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EmployeeCodePattern();

    public static bool IsEmployeeCode(string value) => value.Length <= EmployeeCodeMax && EmployeeCodePattern().IsMatch(value);

    public static string? NormaliseEmployeeCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    [GeneratedRegex(@"^[0-9+()\- ]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    /// <summary>Digits, spaces, +, - and parentheses, with at least six digits.</summary>
    public static bool IsPhone(string value) =>
        value.Length <= PhoneMax && PhonePattern().IsMatch(value) && value.Count(char.IsAsciiDigit) >= 6;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    public static bool IsEmail(string value) => value.Length <= EmailMax && EmailPattern().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_ ]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OrgCodePattern();

    public static bool IsOrgCode(string value) => value.Length <= 16 && OrgCodePattern().IsMatch(value);

    /// <summary>Card UIDs are compared without separators and in upper case.</summary>
    public static string NormaliseCardUid(string value) =>
        new string(value.Where(c => !char.IsWhiteSpace(c) && c is not (':' or '-')).ToArray()).ToUpperInvariant();

    [GeneratedRegex(@"^[A-Z0-9]{4,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CardUidPattern();

    public static bool IsCardUid(string normalised) => CardUidPattern().IsMatch(normalised);

    private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy" };

    /// <summary>ISO (2024-03-15) or day-first (15/03/2024, 15-03-2024) dates, as HR spreadsheets write them.</summary>
    public static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value?.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Null when acceptable, else what is wrong with a joining date.</summary>
    public static string? JoiningDateProblem(DateOnly joined, DateOnly today) =>
        joined.Year < 1950 ? "Joining date is too far in the past."
        : joined > today.AddDays(90) ? "Joining date can't be more than 90 days in the future."
        : null;

    /// <summary>Null when acceptable, else what is wrong with a date of birth.</summary>
    public static string? DateOfBirthProblem(DateOnly born, DateOnly today, DateOnly? joined)
    {
        if (born.Year < 1900 || born > today) return "Date of birth is not a real past date.";
        var reference = joined is { } j && j < today ? j : today;
        if (born.AddYears(MinimumAgeYears) > reference)
            return $"A worker must be at least {MinimumAgeYears} years old on the joining date.";
        return null;
    }
}
