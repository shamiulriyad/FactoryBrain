using System.Text.RegularExpressions;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Application.Merchandising;

/// <summary>
/// Deterministic extraction of labelled values ("PO No: 4500123",
/// "Delivery Date   15-Dec-2026", CSV rows "Quantity,12000") and of
/// colour × size tables (long form: colour / size / qty columns; or a
/// matrix: a colour column and one column per size). It reads only what is
/// written next to a known label, quotes the exact line as evidence, and
/// reports no confidence. It is not AI and does not understand free prose:
/// a value that isn't labelled is not found.
/// </summary>
public static partial class RuleBasedFieldExtractor
{
    public const string ProviderName = "rules";
    public const string ModelName = "label-parser";
    public const string ModelVersion = "1";

    // Order matters: the first matching label wins (unit price before unit; PO number before order number).
    private static readonly (string Key, Regex Label)[] Labels =
    {
        (MerchFields.OrderNumber, L(@"(?:(?:buyer'?s?|customer)\s+)?(?:purchase\s+order|p\.?\s?o\.?)\s*(?:no\.?|number|num|#|ref(?:erence)?)?|order\s*(?:no\.?|number|#|ref(?:erence)?)")),
        (MerchFields.UnitPrice, L(@"(?:unit\s+price|fob(?:\s+price)?|price\s*(?:/|per)\s*(?:pc|pcs|piece|unit)|price)")),
        (MerchFields.Currency, L(@"currency")),
        (MerchFields.Quantity, L(@"(?:total\s+)?(?:order(?:ed)?\s+)?(?:quantity|qty)(?:\s*\(\s*[a-z.]+\s*\))?")),
        (MerchFields.Unit, L(@"unit(?:\s+of\s+measure)?|uom")),
        (MerchFields.DeliveryDate, L(@"(?:delivery|shipment|ship|ex[-\s]?factory|ex[-\s]?fty|x[-\s]?factory|etd)(?:\s+date)?")),
        (MerchFields.Destination, L(@"destination|final\s+destination|ship\s+to|deliver\s+to|port\s+of\s+(?:discharge|destination)")),
        (MerchFields.Style, L(@"style\s*(?:no\.?|number|#|code|ref(?:erence)?)?|article\s*(?:no\.?|number|#)?")),
        (MerchFields.Buyer, L(@"buyer(?:\s+name)?|customer(?:\s+name)?|brand")),
        (MerchFields.Product, L(@"(?:product|item|garment)(?:\s+(?:description|name))?|description")),
    };

    private static Regex L(string label) =>
        new($@"^\s*(?<label>{label})\s*(?:[:#=|\t]+|\s{{2,}})\s*(?<value>\S.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ExtractedCandidate> Extract(IReadOnlyList<PageText> pages)
    {
        var found = new Dictionary<string, ExtractedCandidate>();
        foreach (var page in pages.OrderBy(p => p.PageNumber))
        {
            var lines = page.Text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length > 400 || string.IsNullOrWhiteSpace(line)) continue;
                foreach (var (key, regex) in Labels)
                {
                    var m = regex.Match(line);
                    if (!m.Success) continue;
                    var value = FirstCell(m.Groups["value"].Value);
                    if (value.Length > 0 && !found.ContainsKey(key))
                    {
                        found[key] = new ExtractedCandidate(key, value, null, page.PageNumber, line.Trim());
                        // "12,000 pcs" or "Qty (pcs): 12,000" also states the unit, on the same line.
                        if (key == MerchFields.Quantity && !found.ContainsKey(MerchFields.Unit))
                        {
                            var unit = MerchFields.SplitQuantity(value).Unit ?? UnitInLabel(m.Groups["label"].Value);
                            if (unit is not null) found[MerchFields.Unit] = new ExtractedCandidate(MerchFields.Unit, unit, null, page.PageNumber, line.Trim());
                        }
                    }
                    break;
                }
            }
            if (!found.ContainsKey(MerchFields.Breakdown) && FindTable(lines) is { } table)
                found[MerchFields.Breakdown] = new ExtractedCandidate(MerchFields.Breakdown, MerchFields.SerializeBreakdown(table.Rows), null, page.PageNumber, table.Evidence);
        }
        return found.Values.ToList();
    }

    private static string? UnitInLabel(string label)
    {
        var m = ParenRegex().Match(label);
        return m.Success ? MerchFields.NormalizeUnit(m.Groups[1].Value) : null;
    }

    /// <summary>The value up to the next cell boundary (tab, pipe, or a run of 3+ spaces), unquoted.</summary>
    private static string FirstCell(string value)
    {
        var cell = CellBoundary().Split(value)[0].Trim().Trim('"').Trim();
        return cell.TrimEnd(',', ';', '|').Trim();
    }

    // ── Tables ──────────────────────────────────────────────────────────────

    private sealed record Table(IReadOnlyList<BreakdownRow> Rows, string Evidence);

    private static string[] Cells(string line) =>
        TableSplit().Split(line.Trim()).Select(c => c.Trim().Trim('"').Trim()).Where(c => c.Length > 0).ToArray();

    private static Table? FindTable(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var header = Cells(lines[i]);
            if (header.Length < 2) continue;
            var colorCol = Array.FindIndex(header, c => ColorHeader().IsMatch(c));
            if (colorCol < 0) continue;
            var sizeCol = Array.FindIndex(header, c => SizeHeader().IsMatch(c));
            var qtyCol = Array.FindIndex(header, c => QtyHeader().IsMatch(c));

            var rows = new List<BreakdownRow>();
            var used = new List<string> { lines[i].Trim() };
            if (sizeCol >= 0 && qtyCol >= 0)
            {
                // Long form: one row per colour and size.
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var cells = Cells(lines[j]);
                    if (cells.Length != header.Length || TotalRow().IsMatch(cells[0])) break;
                    if (MerchFields.Normalize(MerchFields.Quantity, cells[qtyCol]).Normalized is not { } q) break;
                    rows.Add(new BreakdownRow(cells[colorCol], cells[sizeCol], int.Parse(q)));
                    used.Add(lines[j].Trim());
                }
            }
            else
            {
                // Matrix: the colour column, then one column per size (a Total column is ignored).
                var sizeCols = header.Select((c, k) => (c, k)).Where(x => x.k != colorCol && SizeToken().IsMatch(x.c)).ToList();
                if (sizeCols.Count == 0) continue;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var cells = Cells(lines[j]);
                    if (cells.Length != header.Length || TotalRow().IsMatch(cells[colorCol])) break;
                    var parsed = new List<BreakdownRow>();
                    foreach (var (size, k) in sizeCols)
                    {
                        if (MerchFields.Normalize(MerchFields.Quantity, cells[k]).Normalized is not { } q) { parsed = null; break; }
                        parsed.Add(new BreakdownRow(cells[colorCol], size, int.Parse(q)));
                    }
                    if (parsed is null) break;
                    rows.AddRange(parsed);
                    used.Add(lines[j].Trim());
                }
            }
            if (rows.Count == 0) continue;
            var evidence = string.Join("\n", used);
            if (evidence.Length > MerchEvidence.MaxSnippetLength) evidence = lines[i].Trim();
            return new Table(rows, evidence);
        }
        return null;
    }

    [GeneratedRegex(@"\(\s*([a-z.]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenRegex();

    [GeneratedRegex(@"\t|\s*\|\s*|\s{3,}")]
    private static partial Regex CellBoundary();

    [GeneratedRegex(@"\t|\s*[|;]\s*|\s{2,}")]
    private static partial Regex TableSplit();

    [GeneratedRegex(@"^colou?rs?(\s*(name|way))?$", RegexOptions.IgnoreCase)]
    private static partial Regex ColorHeader();

    [GeneratedRegex(@"^sizes?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeHeader();

    [GeneratedRegex(@"^(qty|quantity|pcs|pieces)(\s*\(.*\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex QtyHeader();

    [GeneratedRegex(@"^(xxs|xs|s|m|l|xl|xxl|xxxl|[2-6]xl|\d{1,2}|\d{1,2}[ym]|\d{2}/\d{2}|one\s?size|os)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeToken();

    [GeneratedRegex(@"^(grand\s+)?total", RegexOptions.IgnoreCase)]
    private static partial Regex TotalRow();
}
