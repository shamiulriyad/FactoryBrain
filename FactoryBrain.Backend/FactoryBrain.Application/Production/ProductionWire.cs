using System.Globalization;
using System.Text.RegularExpressions;
using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Application.Production;

/// <summary>Wire names (snake_case) for production enums, and parsing of plant-local dates and times.</summary>
public static partial class ProductionWire
{
    public static readonly string[] Stages = { "cutting", "sewing_input", "sewing_output", "finishing", "packing" };
    public static readonly string[] PlanStatuses = { "draft", "pending_approval", "active", "completed", "cancelled" };
    public static readonly string[] OrderStatuses = { "open", "closed", "cancelled" };
    public static readonly string[] Sources = { "manual", "import", "integration" };
    public static readonly string[] ProposalStatuses = { "proposed", "pending_approval", "applied", "rejected", "superseded" };
    public static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    public static string Stage(ProductionStage s) => s switch
    {
        ProductionStage.Cutting => "cutting",
        ProductionStage.SewingInput => "sewing_input",
        ProductionStage.SewingOutput => "sewing_output",
        ProductionStage.Finishing => "finishing",
        _ => "packing",
    };

    public static bool TryParseStage(string? raw, out ProductionStage stage)
    {
        var i = Array.IndexOf(Stages, raw?.Trim().ToLowerInvariant());
        stage = i < 0 ? default : (ProductionStage)i;
        return i >= 0;
    }

    public static string PlanStatus(ProductionPlanStatus s) => PlanStatuses[(int)s];

    public static bool TryParsePlanStatus(string? raw, out ProductionPlanStatus status)
    {
        var i = Array.IndexOf(PlanStatuses, raw?.Trim().ToLowerInvariant());
        status = i < 0 ? default : (ProductionPlanStatus)i;
        return i >= 0;
    }

    public static string OrderStatus(ProductionOrderStatus s) => OrderStatuses[(int)s];

    public static bool TryParseOrderStatus(string? raw, out ProductionOrderStatus status)
    {
        var i = Array.IndexOf(OrderStatuses, raw?.Trim().ToLowerInvariant());
        status = i < 0 ? default : (ProductionOrderStatus)i;
        return i >= 0;
    }

    public static string Source(ProductionDataSource s) => Sources[(int)s];

    public static bool TryParseSource(string? raw, out ProductionDataSource source)
    {
        var i = Array.IndexOf(Sources, raw?.Trim().ToLowerInvariant());
        source = i < 0 ? default : (ProductionDataSource)i;
        return i >= 0;
    }

    public static string ProposalStatus(BalanceProposalStatus s) => ProposalStatuses[(int)s];

    public static bool TryParseProposalStatus(string? raw, out BalanceProposalStatus status)
    {
        var i = Array.IndexOf(ProposalStatuses, raw?.Trim().ToLowerInvariant());
        status = i < 0 ? default : (BalanceProposalStatus)i;
        return i >= 0;
    }

    public static string ChangeStatus(PlanChangeStatus s) => s.ToString().ToLowerInvariant();

    public static string SmvBasisName(SmvBasis b) => b switch
    {
        SmvBasis.Approved => "approved",
        SmvBasis.OperationBulletin => "operation_bulletin",
        _ => "missing",
    };

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseDate(string? raw, out DateOnly date) =>
        DateOnly.TryParseExact(raw?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static string Time(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static bool TryParseTime(string? raw, out TimeOnly time) =>
        TimeOnly.TryParseExact(raw?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static IReadOnlyList<string> Weekdays(int mask) =>
        Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => DayNames[i]).ToList();

    /// <summary>Day names → bitmask; null if any name is unknown or the list is empty.</summary>
    public static int? WeekdayMask(IEnumerable<string>? names)
    {
        if (names is null) return null;
        var mask = 0;
        foreach (var n in names)
        {
            var i = Array.IndexOf(DayNames, n?.Trim().ToLowerInvariant());
            if (i < 0) return null;
            mask |= 1 << i;
        }
        return mask == 0 ? null : mask;
    }

    /// <summary>Lowercase slug: letters, digits, hyphens, underscores; starts with a letter or digit.</summary>
    public static bool IsSlug(string? s, int max) => s is not null && s.Length <= max && SlugRegex().IsMatch(s);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex SlugRegex();
}
