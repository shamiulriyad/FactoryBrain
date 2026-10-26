using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactoryBrain.Application.Merchandising;

public enum MerchFieldType { Text, Integer, Decimal, Date, Unit, Currency, Breakdown }

/// <summary>One field the extraction schema knows: its type, limits and whether order documents must state it.</summary>
public sealed record MerchFieldDefinition(
    string Key,
    string Label,
    MerchFieldType Type,
    int MaxLength,
    /// <summary>A purchase order is expected to state it; a missing value is reported.</summary>
    bool RequiredOnPurchaseOrder,
    /// <summary>Compared across documents for conflicts (free-text fields are not).</summary>
    bool Compared,
    string Description);

/// <summary>Result of normalising one value: the canonical form (null when invalid) and the problems found.</summary>
public sealed record FieldNormalization(string? Normalized, IReadOnlyList<string> Issues)
{
    public bool HasBlockingIssue => Issues.Any(MerchFields.IsBlocking);
}

/// <summary>One row of a colour × size breakdown.</summary>
public sealed record BreakdownRow(string Color, string Size, int Quantity);

/// <summary>
/// The extraction schema and its validation rules. Every value — extracted,
/// corrected or typed — passes through <see cref="Normalize"/>; nothing is
/// guessed: an ambiguous date or currency stays unnormalised with an issue
/// the reviewer must settle.
/// </summary>
public static partial class MerchFields
{
    public const string OrderNumber = "order_number";
    public const string Buyer = "buyer";
    public const string Style = "style";
    public const string Product = "product";
    public const string Quantity = "quantity";
    public const string Unit = "unit";
    public const string DeliveryDate = "delivery_date";
    public const string Currency = "currency";
    public const string UnitPrice = "unit_price";
    public const string Destination = "destination";
    public const string Breakdown = "size_color_breakdown";

    public const int MaxBreakdownRows = 500;
    public const int MaxQuantity = 10_000_000;

    public static readonly IReadOnlyList<MerchFieldDefinition> All = new MerchFieldDefinition[]
    {
        new(OrderNumber, "PO / order number", MerchFieldType.Text, 64, true, true, "The buyer's purchase-order or order reference number."),
        new(Buyer, "Buyer", MerchFieldType.Text, 120, true, false, "The buying company (customer) that issued the order."),
        new(Style, "Style", MerchFieldType.Text, 64, true, true, "Style number / article code of the garment."),
        new(Product, "Product", MerchFieldType.Text, 200, false, false, "Product description (e.g. men's knit polo shirt)."),
        new(Quantity, "Order quantity", MerchFieldType.Integer, 32, true, true, "Total ordered quantity as a whole number."),
        new(Unit, "Unit", MerchFieldType.Unit, 16, false, true, "Unit of the quantity: pcs, dozen, sets, packs or pairs."),
        new(DeliveryDate, "Delivery date", MerchFieldType.Date, 32, true, true, "Agreed delivery / ex-factory / ship date."),
        new(Currency, "Currency", MerchFieldType.Currency, 8, false, true, "ISO 4217 currency of the price."),
        new(UnitPrice, "Unit price", MerchFieldType.Decimal, 32, false, true, "Price per unit, without the currency."),
        new(Destination, "Destination", MerchFieldType.Text, 120, false, false, "Destination country / port / warehouse."),
        new(Breakdown, "Colour × size breakdown", MerchFieldType.Breakdown, 200_000, false, true, "Quantity per colour and size."),
    };

    private static readonly Dictionary<string, MerchFieldDefinition> ByKey = All.ToDictionary(f => f.Key);

    public static bool IsKnown(string? key) => key is not null && ByKey.ContainsKey(key);
    public static MerchFieldDefinition Get(string key) => ByKey[key];
    public static IEnumerable<string> RequiredOnPurchaseOrder => All.Where(f => f.RequiredOnPurchaseOrder).Select(f => f.Key);

    // ── Issues ──────────────────────────────────────────────────────────────

    public const string IssueEmpty = "empty";
    public const string IssueTooLong = "too_long";
    public const string IssueInvalidDate = "invalid_date";
    public const string IssueAmbiguousDate = "ambiguous_date";
    public const string IssueDateInPast = "date_in_past";
    public const string IssueInvalidQuantity = "invalid_quantity";
    public const string IssueAmbiguousQuantity = "ambiguous_quantity";
    public const string IssueQuantityRange = "quantity_out_of_range";
    public const string IssueUnknownUnit = "unknown_unit";
    public const string IssueInvalidCurrency = "invalid_currency";
    public const string IssueAmbiguousCurrency = "ambiguous_currency";
    public const string IssueInvalidPrice = "invalid_price";
    public const string IssueInvalidBreakdown = "invalid_breakdown";
    public const string IssueBreakdownDuplicate = "breakdown_duplicate";
    public const string IssueBreakdownTotal = "breakdown_total_mismatch";
    public const string IssueEvidenceNotFound = "evidence_not_found";
    public const string IssueNoEvidence = "no_evidence";
    public const string IssueLowConfidence = "low_confidence";

    /// <summary>Blocking issues: the value can't be accepted as is; it must be corrected or marked unresolved.</summary>
    public static bool IsBlocking(string issue) => issue is IssueEmpty or IssueTooLong or IssueInvalidDate or IssueAmbiguousDate
        or IssueInvalidQuantity or IssueAmbiguousQuantity or IssueQuantityRange or IssueUnknownUnit or IssueInvalidCurrency
        or IssueAmbiguousCurrency or IssueInvalidPrice or IssueInvalidBreakdown or IssueBreakdownDuplicate;

    public static string DescribeIssue(string issue) => issue switch
    {
        IssueEmpty => "No value.",
        IssueTooLong => "The value is longer than this field allows.",
        IssueInvalidDate => "Not a recognisable date.",
        IssueAmbiguousDate => "Day and month can't be told apart (e.g. 03/04/2027) or the year has two digits; enter it as yyyy-MM-dd.",
        IssueDateInPast => "The date is in the past.",
        IssueInvalidQuantity => "Not a whole-number quantity.",
        IssueAmbiguousQuantity => "'.' could be a decimal point or a thousands separator; enter the quantity without it.",
        IssueQuantityRange => $"The quantity must be from 1 to {MaxQuantity:N0}.",
        IssueUnknownUnit => "Unknown unit; use pcs, dozen, sets, packs or pairs.",
        IssueInvalidCurrency => "Not a known ISO 4217 currency code.",
        IssueAmbiguousCurrency => "The symbol is used by several currencies ($, ¥); enter the ISO code (e.g. USD).",
        IssueInvalidPrice => "Not a valid price (a positive number with at most 4 decimals).",
        IssueInvalidBreakdown => "The breakdown rows could not be read (each needs a colour, a size and a whole-number quantity).",
        IssueBreakdownDuplicate => "The same colour and size appear more than once.",
        IssueBreakdownTotal => "The breakdown does not add up to the order quantity.",
        IssueEvidenceNotFound => "The quoted source text was not found in the document, so it was not kept; check the value against the document.",
        IssueNoEvidence => "The extractor gave no source text for this value.",
        IssueLowConfidence => "The extractor's own confidence is below the review threshold.",
        _ => issue,
    };

    // ── Normalisation ───────────────────────────────────────────────────────

    /// <summary>Validate and canonicalise a value for <paramref name="key"/>. <paramref name="today"/> enables the past-date warning.</summary>
    public static FieldNormalization Normalize(string key, string? raw, DateOnly? today = null)
    {
        var def = Get(key);
        var value = CollapseWhitespace(raw);
        if (string.IsNullOrEmpty(value)) return new(null, new[] { IssueEmpty });
        if (def.Type != MerchFieldType.Breakdown && value.Length > Math.Max(def.MaxLength, 64) * 2)
            return new(null, new[] { IssueTooLong });
        return def.Type switch
        {
            MerchFieldType.Text => value.Length > def.MaxLength ? new(null, new[] { IssueTooLong }) : new(value, Array.Empty<string>()),
            MerchFieldType.Integer => NormalizeQuantity(value),
            MerchFieldType.Decimal => NormalizePrice(value),
            MerchFieldType.Date => NormalizeDate(value, today),
            MerchFieldType.Unit => NormalizeUnit(value) is { } u ? new(u, Array.Empty<string>()) : new(null, new[] { IssueUnknownUnit }),
            MerchFieldType.Currency => NormalizeCurrency(value),
            _ => NormalizeBreakdown(raw!),
        };
    }

    public static string? CollapseWhitespace(string? s) =>
        s is null ? null : WhitespaceRegex().Replace(s, " ").Trim();

    // Quantity ---------------------------------------------------------------

    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pcs"] = "pcs", ["pc"] = "pcs", ["pce"] = "pcs", ["pces"] = "pcs", ["piece"] = "pcs", ["pieces"] = "pcs", ["units"] = "pcs", ["unit"] = "pcs", ["ea"] = "pcs", ["each"] = "pcs", ["nos"] = "pcs",
        ["dozen"] = "dozen", ["dozens"] = "dozen", ["dz"] = "dozen", ["doz"] = "dozen",
        ["set"] = "sets", ["sets"] = "sets",
        ["pack"] = "packs", ["packs"] = "packs", ["pk"] = "packs", ["pks"] = "packs",
        ["pair"] = "pairs", ["pairs"] = "pairs", ["pr"] = "pairs", ["prs"] = "pairs",
    };

    public static string? NormalizeUnit(string? raw)
    {
        var s = raw?.Trim().TrimEnd('.').Trim();
        return s is not null && Units.TryGetValue(s, out var u) ? u : null;
    }

    /// <summary>Splits "12,000 pcs" into the number part and a recognised unit (null when none or unknown).</summary>
    public static (string Number, string? Unit, bool UnknownSuffix) SplitQuantity(string value)
    {
        var m = QuantityRegex().Match(value);
        if (!m.Success) return (value, null, false);
        var suffix = m.Groups["unit"].Value;
        if (suffix.Length == 0) return (m.Groups["num"].Value, null, false);
        var unit = NormalizeUnit(suffix);
        return (m.Groups["num"].Value, unit, unit is null);
    }

    private static FieldNormalization NormalizeQuantity(string value)
    {
        var (number, _, unknownSuffix) = SplitQuantity(value);
        if (unknownSuffix) return new(null, new[] { IssueInvalidQuantity });
        var digits = number.Replace(",", "").Replace(" ", "").Replace("'", "").Replace(" ", "");
        if (digits.Length == 0 || digits.Any(c => c is not ('.' or (>= '0' and <= '9')))) return new(null, new[] { IssueInvalidQuantity });
        if (digits.Contains('.'))
        {
            var parts = digits.Split('.');
            if (parts.Length == 2 && parts[1].Length == 3 && !number.Contains(',')) return new(null, new[] { IssueAmbiguousQuantity });
            if (parts.Length != 2 || parts[1].Trim('0').Length > 0) return new(null, new[] { IssueInvalidQuantity });
            digits = parts[0];
        }
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var q)) return new(null, new[] { IssueInvalidQuantity });
        if (q is < 1 or > MaxQuantity) return new(null, new[] { IssueQuantityRange });
        return new(q.ToString(CultureInfo.InvariantCulture), Array.Empty<string>());
    }

    // Price ------------------------------------------------------------------

    private static FieldNormalization NormalizePrice(string value)
    {
        var s = PriceNoiseRegex().Replace(value, "").Trim();
        if (s.Contains(',') && s.Contains('.')) s = s.Replace(",", "");               // 1,234.50
        else if (s.Contains(',') && CommaDecimalRegex().IsMatch(s)) s = s.Replace(',', '.'); // 4,25 → 4.25
        else if (s.Contains(',')) s = s.Replace(",", "");                            // 1,234
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var p)) return new(null, new[] { IssueInvalidPrice });
        if (p <= 0 || p > 1_000_000 || decimal.Round(p, 4) != p) return new(null, new[] { IssueInvalidPrice });
        return new(p.ToString("0.####", CultureInfo.InvariantCulture), Array.Empty<string>());
    }

    // Currency ---------------------------------------------------------------

    public static readonly HashSet<string> IsoCurrencies = new(StringComparer.Ordinal)
    {
        "USD", "EUR", "GBP", "BDT", "JPY", "CNY", "INR", "CAD", "AUD", "NZD", "SEK", "NOK", "DKK", "CHF", "HKD", "SGD", "AED", "SAR",
        "TRY", "PKR", "LKR", "VND", "KRW", "MXN", "BRL", "ZAR", "PLN", "CZK", "HUF", "RUB", "IDR", "MYR", "THB", "PHP", "KHR", "MMK", "EGP",
    };

    private static FieldNormalization NormalizeCurrency(string value)
    {
        var s = value.Trim().ToUpperInvariant().Replace(" ", "");
        if (s is "US$" or "USD$" or "U.S.$" or "US-DOLLAR" or "USDOLLAR" or "USDOLLARS") return new("USD", Array.Empty<string>());
        if (s is "€" or "EURO" or "EUROS") return new("EUR", Array.Empty<string>());
        if (s is "£" or "GBP£") return new("GBP", Array.Empty<string>());
        if (s is "৳" or "TK" or "TAKA") return new("BDT", Array.Empty<string>());
        if (s is "$" or "¥" or "DOLLAR" or "DOLLARS" or "YEN") return new(null, new[] { IssueAmbiguousCurrency });
        return s.Length == 3 && IsoCurrencies.Contains(s) ? new(s, Array.Empty<string>()) : new(null, new[] { IssueInvalidCurrency });
    }

    // Date -------------------------------------------------------------------

    private static readonly string[] NamedMonthFormats =
    {
        "d MMM yyyy", "d MMMM yyyy", "d-MMM-yyyy", "d-MMMM-yyyy", "d/MMM/yyyy", "d.MMM.yyyy",
        "MMM d yyyy", "MMMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy", "MMM-d-yyyy", "yyyy-MMM-d", "d MMM, yyyy", "d MMMM, yyyy",
    };

    /// <summary>
    /// Dates: ISO <c>yyyy-MM-dd</c> / <c>yyyy/MM/dd</c>; named months
    /// (<c>12 Dec 2026</c>, <c>December 12, 2026</c>); <c>dd.MM.yyyy</c> is
    /// day-first; <c>a/b/yyyy</c> or <c>a-b-yyyy</c> only when one part is
    /// over 12 (or both are equal). Two-digit years and genuinely ambiguous
    /// numeric dates are not guessed.
    /// </summary>
    private static FieldNormalization NormalizeDate(string value, DateOnly? today)
    {
        var s = OrdinalRegex().Replace(value.Trim().TrimEnd('.'), "$1");
        var date = ParseDate(s, out var ambiguous);
        if (ambiguous) return new(null, new[] { IssueAmbiguousDate });
        if (date is not { } d) return new(null, new[] { IssueInvalidDate });
        if (d.Year is < 2000 or > 2100) return new(null, new[] { IssueInvalidDate });
        var issues = today is { } t && d < t ? new[] { IssueDateInPast } : Array.Empty<string>();
        return new(MerchWire.Date(d), issues);
    }

    private static DateOnly? ParseDate(string s, out bool ambiguous)
    {
        ambiguous = false;
        var inv = CultureInfo.InvariantCulture;
        if (DateOnly.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy/M/d", "yyyy.MM.dd" }, inv, DateTimeStyles.None, out var iso)) return iso;
        if (DateOnly.TryParseExact(s, NamedMonthFormats, inv, DateTimeStyles.AllowWhiteSpaces, out var named)) return named;
        var m = NumericDateRegex().Match(s);
        if (!m.Success)
        {
            if (TwoDigitYearRegex().IsMatch(s)) ambiguous = true;
            return null;
        }
        int a = int.Parse(m.Groups["a"].Value, inv), b = int.Parse(m.Groups["b"].Value, inv), y = int.Parse(m.Groups["y"].Value, inv);
        int day, month;
        if (m.Groups["sep"].Value == ".") { day = a; month = b; }
        else if (a > 12 && b <= 12) { day = a; month = b; }
        else if (b > 12 && a <= 12) { day = b; month = a; }
        else if (a == b) { day = a; month = b; }
        else { ambiguous = a <= 12 && b <= 12; return null; }
        try { return new DateOnly(y, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // Breakdown --------------------------------------------------------------

    public static FieldNormalization NormalizeBreakdown(string raw)
    {
        var rows = ParseBreakdown(raw);
        if (rows is null) return new(null, new[] { IssueInvalidBreakdown });
        var dup = rows.GroupBy(r => (Key(r.Color), Key(r.Size))).Any(g => g.Count() > 1);
        var json = SerializeBreakdown(rows);
        return dup ? new(null, new[] { IssueBreakdownDuplicate }) : new(json, Array.Empty<string>());
    }

    /// <summary>Rows from breakdown JSON (<c>[{color,size,quantity}]</c>, quantity a number or numeric string); null when unreadable.</summary>
    public static IReadOnlyList<BreakdownRow>? ParseBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var rows = new List<BreakdownRow>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                var color = CollapseWhitespace(Str(e, "color") ?? Str(e, "colour"));
                var size = CollapseWhitespace(Str(e, "size"));
                if (string.IsNullOrEmpty(color) || string.IsNullOrEmpty(size) || color.Length > 60 || size.Length > 30) return null;
                if (!e.TryGetProperty("quantity", out var q)) return null;
                int qty;
                if (q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var n)) qty = n;
                else if (q.ValueKind == JsonValueKind.String && NormalizeQuantity(q.GetString()!).Normalized is { } ns) qty = int.Parse(ns, CultureInfo.InvariantCulture);
                else return null;
                if (qty is < 1 or > MaxQuantity) return null;
                rows.Add(new BreakdownRow(color, size, qty));
                if (rows.Count > MaxBreakdownRows) return null;
            }
            return rows.Count == 0 ? null : rows;
        }
        catch (JsonException) { return null; }
    }

    public static string SerializeBreakdown(IEnumerable<BreakdownRow> rows) =>
        JsonSerializer.Serialize(rows.Select(r => new { color = r.Color, size = r.Size, quantity = r.Quantity }));

    public static int BreakdownTotal(IEnumerable<BreakdownRow> rows) => rows.Sum(r => r.Quantity);

    // ── Comparison (conflict rules) ─────────────────────────────────────────

    /// <summary>
    /// The comparison key of a normalised value: order numbers and styles
    /// ignore case, spaces and punctuation (<c>ST-1001</c> = <c>st 1001</c>);
    /// quantities, units, dates, currencies and prices compare by their
    /// canonical form; a breakdown compares as the set of (colour, size,
    /// quantity) rows, ignoring order, case and spacing.
    /// </summary>
    public static string ComparisonKey(string key, string normalized)
    {
        switch (Get(key).Type)
        {
            case MerchFieldType.Text:
                return AlnumRegex().Replace(normalized, "").ToUpperInvariant();
            case MerchFieldType.Decimal:
                return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString("0.####", CultureInfo.InvariantCulture) : normalized;
            case MerchFieldType.Breakdown:
                var rows = ParseBreakdown(normalized);
                return rows is null ? normalized
                    : string.Join(";", rows.Select(r => $"{Key(r.Color)}|{Key(r.Size)}|{r.Quantity}").OrderBy(x => x, StringComparer.Ordinal));
            default:
                return normalized;
        }
    }

    private static string Key(string s) => AlnumRegex().Replace(s, "").ToUpperInvariant();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    /// <summary>Human-readable form of a normalised value (breakdowns summarised).</summary>
    public static string Display(string key, string? normalized)
    {
        if (normalized is null) return "—";
        if (Get(key).Type != MerchFieldType.Breakdown) return normalized;
        var rows = ParseBreakdown(normalized);
        if (rows is null) return normalized;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{rows.Count} row(s), total {BreakdownTotal(rows):N0}");
        return sb.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(?<num>[0-9][0-9.,'  ]*?)\s*(?<unit>[A-Za-z]+\.?)?$")]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"(?i)(US\$|USD|EUR|GBP|BDT|[A-Z]{3}|[$€£৳¥]|\s)")]
    private static partial Regex PriceNoiseRegex();

    [GeneratedRegex(@"^\d+,\d{1,2}$")]
    private static partial Regex CommaDecimalRegex();

    [GeneratedRegex(@"(?i)\b(\d{1,2})(st|nd|rd|th)\b")]
    private static partial Regex OrdinalRegex();

    [GeneratedRegex(@"^(?<a>\d{1,2})(?<sep>[/.\-])(?<b>\d{1,2})\k<sep>(?<y>\d{4})$")]
    private static partial Regex NumericDateRegex();

    [GeneratedRegex(@"^\d{1,2}[/.\-](\d{1,2}|[A-Za-z]{3,9})[/.\-]\d{2}$")]
    private static partial Regex TwoDigitYearRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]")]
    private static partial Regex AlnumRegex();
}
