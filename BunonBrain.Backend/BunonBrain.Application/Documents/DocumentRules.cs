using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BunonBrain.Application.Documents;

/// <summary>One finding of the checks: a missing value, an error (blocks submission) or a warning.</summary>
public sealed record DocumentIssue(string Field, string Code, string Message);

/// <summary>The result of checking a document's content.</summary>
public sealed class DocumentChecks
{
    public List<DocumentIssue> Missing { get; } = new();
    public List<DocumentIssue> Errors { get; } = new();
    public List<DocumentIssue> Warnings { get; } = new();

    public bool CanSubmit => Missing.Count == 0 && Errors.Count == 0;

    public void Miss(string field, string label, string? why = null) => Missing.Add(new DocumentIssue(field, "missing", why ?? $"{label} is required."));
    public void Error(string field, string code, string message) => Errors.Add(new DocumentIssue(field, code, message));
    public void Warn(string field, string code, string message) => Warnings.Add(new DocumentIssue(field, code, message));

    public string ToJson() => JsonSerializer.Serialize(new { missing = Missing, errors = Errors, warnings = Warnings }, DocJson.Options);

    public static DocumentChecks FromJson(string? json)
    {
        var c = new DocumentChecks();
        if (string.IsNullOrWhiteSpace(json)) return c;
        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            void Load(string key, List<DocumentIssue> into)
            {
                if (node?[key] is not JsonArray arr) return;
                foreach (var i in arr.OfType<JsonObject>())
                    into.Add(new DocumentIssue(i["field"]?.GetValue<string>() ?? "", i["code"]?.GetValue<string>() ?? "", i["message"]?.GetValue<string>() ?? ""));
            }
            Load("missing", c.Missing); Load("errors", c.Errors); Load("warnings", c.Warnings);
        }
        catch (JsonException) { }
        return c;
    }
}

public static class DocJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public static string? Str(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.String:
                var s = v.GetValue<string>();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Number: return v.ToJsonString();
            default: return null;
        }
    }

    /// <summary>A number from a JSON number of any CLR type, or from a numeric string ("1,200.50").</summary>
    public static decimal? Dec(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        var kind = v.GetValueKind();
        var raw = kind switch
        {
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.String => v.GetValue<string>().Replace(",", "").Trim(),
            _ => null,
        };
        return raw is not null && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static DateOnly? Date(JsonNode? n)
    {
        var s = Str(n);
        return s is not null && DateOnly.TryParseExact(s.Length >= 10 ? s[..10] : s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    public static bool Empty(JsonNode? n) => n switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonValue => Str(n) is null,
        _ => false,
    };

    public static JsonArray Rows(JsonObject data, string key) => data[key] as JsonArray ?? new JsonArray();

    public static JsonValue Num(decimal value, int decimals = 3) => JsonValue.Create(Math.Round(value, decimals, MidpointRounding.AwayFromZero));
}

/// <summary>An ISO 2859-1 single sampling plan (normal inspection).</summary>
public sealed record AqlPlan(char CodeLetter, int SampleSize, int Accept, int Reject);

/// <summary>
/// ISO 2859-1 single sampling, normal inspection, general inspection levels
/// I–III and AQL 1.0–6.5 (the values garment buyers use). Arrows in the
/// standard's table are resolved to the first plan below (↓) or above (↑),
/// which can change the sample size for that AQL.
/// </summary>
public static class AqlCalculator
{
    public static readonly string[] Aqls = { "1.0", "1.5", "2.5", "4.0", "6.5" };
    private const int Down = -1, Up = -2;

    private static readonly (int MaxLot, char I, char II, char III)[] Letters =
    {
        (8, 'A', 'A', 'B'), (15, 'A', 'B', 'C'), (25, 'B', 'C', 'D'), (50, 'C', 'D', 'E'), (90, 'C', 'E', 'F'), (150, 'D', 'F', 'G'),
        (280, 'E', 'G', 'H'), (500, 'F', 'H', 'J'), (1200, 'G', 'J', 'K'), (3200, 'H', 'K', 'L'), (10000, 'J', 'L', 'M'), (35000, 'K', 'M', 'N'),
        (150000, 'L', 'N', 'P'), (500000, 'M', 'P', 'Q'), (int.MaxValue, 'N', 'Q', 'R'),
    };

    private static readonly (char Letter, int Sample, int[] Ac)[] Table =
    {
        ('A', 2,    new[] { Down, Down, Down, Down, 0 }),
        ('B', 3,    new[] { Down, Down, Down, 0, Up }),
        ('C', 5,    new[] { Down, Down, 0, Up, Down }),
        ('D', 8,    new[] { Down, 0, Up, Down, 1 }),
        ('E', 13,   new[] { 0, Up, Down, 1, 2 }),
        ('F', 20,   new[] { Up, Down, 1, 2, 3 }),
        ('G', 32,   new[] { Down, 1, 2, 3, 5 }),
        ('H', 50,   new[] { 1, 2, 3, 5, 7 }),
        ('J', 80,   new[] { 2, 3, 5, 7, 10 }),
        ('K', 125,  new[] { 3, 5, 7, 10, 14 }),
        ('L', 200,  new[] { 5, 7, 10, 14, 21 }),
        ('M', 315,  new[] { 7, 10, 14, 21, Up }),
        ('N', 500,  new[] { 10, 14, 21, Up, Up }),
        ('P', 800,  new[] { 14, 21, Up, Up, Up }),
        ('Q', 1250, new[] { 21, Up, Up, Up, Up }),
        ('R', 2000, new[] { Up, Up, Up, Up, Up }),
    };

    public static char? CodeLetter(int lotSize, string level)
    {
        if (lotSize < 2) return null;
        var row = Letters.First(l => lotSize <= l.MaxLot);
        return level switch { "I" => row.I, "II" => row.II, "III" => row.III, _ => null };
    }

    public static int SampleSizeOf(char letter) => Table.First(t => t.Letter == letter).Sample;

    /// <summary>The plan for a lot, level and AQL; null when an input is outside the table.</summary>
    public static AqlPlan? Plan(int lotSize, string level, string aql)
    {
        var letter = CodeLetter(lotSize, level);
        var col = Array.IndexOf(Aqls, aql);
        if (letter is null || col < 0) return null;
        var i = Array.FindIndex(Table, t => t.Letter == letter);
        // Follow the arrows to the first row that has a plan in this column.
        for (var guard = 0; guard < Table.Length; guard++)
        {
            var ac = Table[i].Ac[col];
            if (ac >= 0)
            {
                // The sample can't be larger than the lot: inspect every piece.
                var n = Math.Min(Table[i].Sample, lotSize);
                return new AqlPlan(Table[i].Letter, n, ac, ac + 1);
            }
            i += ac == Down ? 1 : -1;
            if (i < 0 || i >= Table.Length) return null;
        }
        return null;
    }
}

/// <summary>
/// Deterministic calculations and validation of document content. Computed
/// fields are always recalculated here from the entered values (a value
/// typed or extracted into a computed field is overwritten); validation
/// reports missing required values, malformed values and business-rule
/// violations. Nothing is guessed: a total that can't be computed stays empty.
/// </summary>
public static class DocumentRules
{
    /// <summary>Recalculate the computed fields of form types in place.</summary>
    public static void Compute(DocumentTypeDef type, JsonObject d, DocumentChecks? checks = null)
    {
        decimal? D(JsonNode? n) => DocJson.Dec(n);
        void Set(JsonObject o, string k, decimal? v, int dp = 3) { if (v is { } x) o[k] = DocJson.Num(x, dp); else o.Remove(k); }

        switch (type.Code)
        {
            case "store.grn":
            {
                decimal accepted = 0, amount = 0; var anyCost = false;
                foreach (var r in DocJson.Rows(d, "items").OfType<JsonObject>())
                {
                    var acc = D(r["acceptedQty"]); var cost = D(r["unitCost"]);
                    if (acc is { } a) accepted += a;
                    var amt = acc is { } q && cost is { } c ? q * c : (decimal?)null;
                    Set(r, "amount", amt, 2);
                    if (amt is { } m) { amount += m; anyCost = true; }
                }
                Set(d, "totalAccepted", accepted);
                Set(d, "totalAmount", anyCost ? amount : null, 2);
                var rolls = DocJson.Rows(d, "rolls").Count;
                Set(d, "rollCount", rolls > 0 ? rolls : null, 0);
                break;
            }
            case "hr.leave":
            {
                var f = DocJson.Date(d["fromDate"]); var t = DocJson.Date(d["toDate"]);
                Set(d, "days", f is { } a && t is { } b && b >= a ? b.DayNumber - a.DayNumber + 1 : null, 0);
                break;
            }
            case "qc.aql":
                ComputeAql(d, checks);
                break;
            case "merch.po":
            case "merch.order_sheet":
            {
                var rows = DocJson.Rows(d, "breakdown").OfType<JsonObject>().ToList();
                Set(d, "breakdownTotal", rows.Count == 0 ? null : rows.Sum(r => D(r["quantity"]) ?? 0), 0);
                if (type.Code == "merch.po")
                    Set(d, "totalValue", D(d["quantity"]) is { } q && D(d["unitPrice"]) is { } p ? q * p : null, 2);
                break;
            }
            case "merch.bom":
            {
                var qty = D(d["orderQuantity"]); decimal total = 0; var anyCost = false;
                foreach (var r in DocJson.Rows(d, "items").OfType<JsonObject>())
                {
                    var cons = D(r["consumption"]); var w = D(r["wastagePct"]) ?? 0; var cost = D(r["unitCost"]);
                    var req = qty is { } q && cons is { } c ? q * c * (1 + w / 100m) : (decimal?)null;
                    Set(r, "requiredQty", req);
                    var line = req is { } rq && cost is { } uc ? rq * uc : (decimal?)null;
                    Set(r, "totalCost", line, 2);
                    if (line is { } l) { total += l; anyCost = true; }
                }
                Set(d, "materialCost", anyCost ? total : null, 2);
                Set(d, "costPerPiece", anyCost && qty is > 0 ? total / qty.Value : null, 4);
                break;
            }
            case "merch.costing":
            {
                var lines = DocJson.Rows(d, "lines").OfType<JsonObject>().ToList();
                decimal? total = lines.Count == 0 ? null : lines.Sum(r => D(r["amount"]) ?? 0);
                var commercial = total is { } t ? t * (D(d["commercialPct"]) ?? 0) / 100m : (decimal?)null;
                var fob = total is { } t2 ? (t2 + (commercial ?? 0)) * (1 + (D(d["marginPct"]) ?? 0) / 100m) : (decimal?)null;
                Set(d, "totalCost", total, 4);
                Set(d, "commercialCost", commercial, 4);
                Set(d, "fobPrice", fob, 4);
                Set(d, "fobPerPiece", fob is { } f ? (DocJson.Str(d["basis"]) == "PER_DOZEN" ? f / 12m : f) : null, 4);
                break;
            }
            case "compliance.audit":
            {
                var rows = DocJson.Rows(d, "findings").OfType<JsonObject>().ToList();
                Set(d, "critical", rows.Count(r => DocJson.Str(r["severity"]) == "CRITICAL"), 0);
                Set(d, "major", rows.Count(r => DocJson.Str(r["severity"]) == "MAJOR"), 0);
                Set(d, "minor", rows.Count(r => DocJson.Str(r["severity"]) == "MINOR"), 0);
                Set(d, "open", rows.Count(r => DocJson.Str(r["status"]) != "CLOSED"), 0);
                break;
            }
            case "compliance.safety":
                Set(d, "notOk", DocJson.Rows(d, "checklist").OfType<JsonObject>().Count(r => DocJson.Str(r["result"]) == "NOT_OK"), 0);
                break;
            case "compliance.training":
                Set(d, "attendeeCount", DocJson.Rows(d, "attendees").Count, 0);
                break;
            case "commercial.invoice":
            {
                decimal qty = 0, amount = 0;
                foreach (var r in DocJson.Rows(d, "lines").OfType<JsonObject>())
                {
                    var q = D(r["quantity"]); var p = D(r["unitPrice"]);
                    var amt = q is { } a && p is { } b ? a * b : (decimal?)null;
                    Set(r, "amount", amt, 2);
                    qty += q ?? 0; amount += amt ?? 0;
                }
                Set(d, "totalQuantity", qty, 0);
                Set(d, "totalAmount", amount, 2);
                break;
            }
            case "commercial.packing_list":
            {
                decimal cartons = 0, pieces = 0, net = 0, gross = 0;
                foreach (var r in DocJson.Rows(d, "cartons").OfType<JsonObject>())
                {
                    var from = D(r["cartonFrom"]); var to = D(r["cartonTo"]); var ppc = D(r["piecesPerCarton"]);
                    var c = from is { } f && to is { } t && t >= f ? t - f + 1 : (decimal?)null;
                    Set(r, "cartons", c, 0);
                    Set(r, "pieces", c is { } cc && ppc is { } p ? cc * p : null, 0);
                    cartons += c ?? 0; pieces += (c ?? 0) * (ppc ?? 0);
                    net += (c ?? 0) * (D(r["netKg"]) ?? 0); gross += (c ?? 0) * (D(r["grossKg"]) ?? 0);
                }
                Set(d, "totalCartons", cartons, 0); Set(d, "totalPieces", pieces, 0);
                Set(d, "totalNetKg", net, 2); Set(d, "totalGrossKg", gross, 2);
                break;
            }
            case "devices.maintenance":
            {
                var rows = DocJson.Rows(d, "parts").OfType<JsonObject>().ToList();
                Set(d, "partsCost", rows.Count == 0 ? null : rows.Sum(r => D(r["cost"]) ?? 0), 2);
                break;
            }
        }
    }

    private static void ComputeAql(JsonObject d, DocumentChecks? checks)
    {
        foreach (var k in new[] { "codeLetter", "sampleSize", "majorAccept", "majorReject", "minorAccept", "minorReject", "result" }) d.Remove(k);
        var lot = DocJson.Dec(d["lotSize"]); var level = DocJson.Str(d["inspectionLevel"]);
        if (lot is not { } l || l < 2 || level is null) return;
        var lotSize = (int)Math.Min(l, int.MaxValue);
        if (AqlCalculator.CodeLetter(lotSize, level) is not { } letter) return;
        d["codeLetter"] = letter.ToString();
        d["sampleSize"] = Math.Min(AqlCalculator.SampleSizeOf(letter), lotSize);
        AqlPlan? Apply(string aqlKey, string prefix)
        {
            var aql = DocJson.Str(d[aqlKey]);
            if (aql is null || AqlCalculator.Plan(lotSize, level, aql) is not { } plan) return null;
            d[prefix + "Accept"] = plan.Accept;
            d[prefix + "Reject"] = plan.Reject;
            if (plan.CodeLetter != letter)
                checks?.Warn(aqlKey, "aql_arrow", $"For AQL {aql} the standard's arrow moves to code letter {plan.CodeLetter}: inspect {plan.SampleSize} pieces for {prefix} defects.");
            return plan;
        }
        var major = Apply("aqlMajor", "major");
        var minor = Apply("aqlMinor", "minor");
        var critical = DocJson.Dec(d["criticalFound"]); var mj = DocJson.Dec(d["majorFound"]); var mn = DocJson.Dec(d["minorFound"]);
        if (major is null || minor is null || critical is null || mj is null || mn is null) return;
        var fail = critical > 0 || mj >= major.Reject || mn >= minor.Reject;
        d["result"] = fail ? "FAIL" : "PASS";
    }

    /// <summary>Required values, value formats and the business rules of each type.</summary>
    public static DocumentChecks Validate(DocumentTypeDef type, JsonObject d, DateOnly today)
    {
        var c = new DocumentChecks();
        Compute(type, d, c);
        foreach (var f in type.Fields) CheckField(f, d[f.Key], f.Key, c);
        Rules(type, d, today, c);
        return c;
    }

    private static void CheckField(FieldDef f, JsonNode? value, string path, DocumentChecks c)
    {
        if (f.Computed && f.Kind != FieldKind.Table) return;
        if (f.Kind == FieldKind.Table)
        {
            var rows = value as JsonArray;
            if (f.Computed) return;
            if (rows is null || rows.Count == 0)
            {
                if (f.Required) c.Miss(path, f.Label, $"{f.Label}: add at least one row.");
                return;
            }
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] is not JsonObject row) { c.Error($"{path}[{i}]", "invalid_row", $"{f.Label} row {i + 1} is not valid."); continue; }
                foreach (var col in f.Columns ?? Array.Empty<FieldDef>()) CheckField(col with { Label = $"{f.Label} row {i + 1}: {col.Label}" }, row[col.Key], $"{path}[{i}].{col.Key}", c);
            }
            return;
        }
        if (DocJson.Empty(value))
        {
            if (f.Required) c.Miss(path, f.Label);
            return;
        }
        switch (f.Kind)
        {
            case FieldKind.Number:
            case FieldKind.Integer:
                var n = DocJson.Dec(value);
                if (n is null) c.Error(path, "not_a_number", $"{f.Label} must be a number.");
                else if (f.Kind == FieldKind.Integer && n != decimal.Truncate(n.Value)) c.Error(path, "not_an_integer", $"{f.Label} must be a whole number.");
                else if (n < 0) c.Error(path, "negative", $"{f.Label} can't be negative.");
                break;
            case FieldKind.Date:
                if (DocJson.Date(value) is null) c.Error(path, "not_a_date", $"{f.Label} must be a date (yyyy-MM-dd).");
                break;
            case FieldKind.Select:
                if (f.Options is { } opts && !opts.Contains(DocJson.Str(value))) c.Error(path, "not_an_option", $"{f.Label} must be one of: {string.Join(", ", opts)}.");
                break;
            case FieldKind.Text:
            case FieldKind.TextArea:
                if ((DocJson.Str(value)?.Length ?? 0) > (f.Kind == FieldKind.Text ? 300 : 4000)) c.Error(path, "too_long", $"{f.Label} is too long.");
                break;
        }
    }

    private static void Rules(DocumentTypeDef type, JsonObject d, DateOnly today, DocumentChecks c)
    {
        decimal? D(JsonNode? n) => DocJson.Dec(n);
        string? S(JsonNode? n) => DocJson.Str(n);
        switch (type.Code)
        {
            case "store.grn":
            {
                var rows = DocJson.Rows(d, "items").OfType<JsonObject>().ToList();
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    var rec = D(r["receivedQty"]); var acc = D(r["acceptedQty"]); var rej = D(r["rejectedQty"]) ?? 0; var ord = D(r["orderedQty"]);
                    if (rec is { } rv && acc is { } av)
                    {
                        if (av > rv) c.Error($"items[{i}].acceptedQty", "accepted_exceeds_received", $"Row {i + 1}: accepted ({av}) is more than received ({rv}).");
                        else if (av + rej != rv) c.Error($"items[{i}].rejectedQty", "quantities_dont_add_up", $"Row {i + 1}: accepted + rejected ({av + rej}) must equal received ({rv}).");
                    }
                    if (ord is { } o && rec is { } r2 && r2 > o) c.Warn($"items[{i}].receivedQty", "over_receipt", $"Row {i + 1}: received ({r2}) is more than ordered ({o}).");
                    if (acc is 0) c.Warn($"items[{i}].acceptedQty", "nothing_accepted", $"Row {i + 1}: nothing accepted; no stock will be added.");
                }
                DuplicateCodes(rows, "items", "itemCode", c, error: true);
                if (DocJson.Date(d["receivedOn"]) is { } on && on > today) c.Error("receivedOn", "future_date", "Received on can't be in the future.");
                RollRules(d, rows, "acceptedQty", isReceipt: true, c);
                break;
            }
            case "store.requisition":
            case "store.issue":
            {
                var rows = DocJson.Rows(d, "items").OfType<JsonObject>().ToList();
                for (var i = 0; i < rows.Count; i++)
                    if (D(rows[i]["quantity"]) is 0) c.Error($"items[{i}].quantity", "zero_quantity", $"Row {i + 1}: quantity must be more than zero.");
                DuplicateCodes(rows, "items", "itemCode", c, error: type.Code == "store.issue");
                if (type.Code == "store.issue" && DocJson.Date(d["issuedOn"]) is { } on && on > today) c.Error("issuedOn", "future_date", "Issued on can't be in the future.");
                if (type.Code == "store.issue" && S(d["requisitionReference"]) is null)
                    c.Warn("requisitionReference", "no_requisition", "No requisition referenced: the issue can't be checked against a request.");
                if (type.Code == "store.issue") RollRules(d, rows, "quantity", isReceipt: false, c);
                break;
            }
            case "hr.leave":
            {
                if (DocJson.Date(d["fromDate"]) is { } f && DocJson.Date(d["toDate"]) is { } t && t < f) c.Error("toDate", "date_order", "To must be on or after From.");
                break;
            }
            case "qc.aql":
            {
                if (D(d["lotSize"]) is { } lot && lot < 2) c.Error("lotSize", "lot_too_small", "Lot size must be at least 2 pieces.");
                if (S(d["result"]) == "FAIL") c.Warn("result", "aql_failed", "The lot fails the AQL plan.");
                if (D(d["criticalFound"]) is > 0) c.Warn("criticalFound", "critical_found", "Critical defects found: the lot is rejected regardless of the AQL.");
                break;
            }
            case "qc.capa":
            {
                if (DocJson.Date(d["dueDate"]) is { } due && due < today && S(d["status"]) != "CLOSED")
                    c.Warn("dueDate", "overdue", $"The action was due on {due:yyyy-MM-dd} and is not closed.");
                if (S(d["status"]) == "CLOSED" && S(d["verification"]) is null)
                    c.Miss("verification", "Verification of effectiveness", "A closed CAPA needs its verification of effectiveness.");
                break;
            }
            case "merch.po":
            case "merch.order_sheet":
            {
                if (D(d["breakdownTotal"]) is { } bt && D(d["quantity"]) is { } q && bt != q)
                    c.Warn("breakdown", "breakdown_mismatch", $"The colour × size breakdown totals {bt}, the order quantity is {q}.");
                break;
            }
            case "merch.bom":
            {
                var rows = DocJson.Rows(d, "items").OfType<JsonObject>().ToList();
                for (var i = 0; i < rows.Count; i++)
                {
                    if (D(rows[i]["consumption"]) is 0) c.Error($"items[{i}].consumption", "zero_consumption", $"Row {i + 1}: consumption must be more than zero.");
                    if (D(rows[i]["wastagePct"]) is > 100) c.Error($"items[{i}].wastagePct", "wastage_range", $"Row {i + 1}: wastage must be 0–100 %.");
                }
                break;
            }
            case "merch.costing":
            {
                if (D(d["marginPct"]) is > 100) c.Error("marginPct", "margin_range", "Margin must be 0–100 %.");
                if (D(d["commercialPct"]) is > 100) c.Error("commercialPct", "commercial_range", "Commercial cost must be 0–100 %.");
                if (D(d["quotedPrice"]) is { } quoted && D(d["totalCost"]) is { } cost)
                {
                    var costWithCommercial = cost + (D(d["commercialCost"]) ?? 0);
                    if (quoted < costWithCommercial) c.Warn("quotedPrice", "below_cost", $"The quoted price ({quoted}) is below cost ({Math.Round(costWithCommercial, 4)}).");
                    else if (D(d["fobPrice"]) is { } fob && quoted < fob) c.Warn("quotedPrice", "below_target_margin", $"The quoted price ({quoted}) is below the FOB with the target margin ({Math.Round(fob, 4)}).");
                }
                break;
            }
            case "compliance.audit":
            {
                var rows = DocJson.Rows(d, "findings").OfType<JsonObject>().ToList();
                if (S(d["overallResult"]) == "PASS" && rows.Any(r => S(r["severity"]) == "CRITICAL"))
                    c.Warn("overallResult", "pass_with_critical", "The audit is marked PASS but has critical findings.");
                for (var i = 0; i < rows.Count; i++)
                    if (S(rows[i]["severity"]) is "CRITICAL" or "MAJOR" && S(rows[i]["status"]) != "CLOSED" && S(rows[i]["correctiveAction"]) is null)
                        c.Miss($"findings[{i}].correctiveAction", "Corrective action", $"Finding {i + 1} is {S(rows[i]["severity"])?.ToLowerInvariant()} and open: give its corrective action.");
                break;
            }
            case "compliance.safety":
                if (D(d["notOk"]) is > 0 && S(d["actionsRequired"]) is null) c.Miss("actionsRequired", "Actions required", "Items are not OK: describe the actions required.");
                break;
            case "compliance.training":
                DuplicateCodes(DocJson.Rows(d, "attendees").OfType<JsonObject>().ToList(), "attendees", "workerCode", c, error: true);
                if (DocJson.Date(d["trainingDate"]) is { } td && td > today) c.Warn("trainingDate", "future_date", "The training date is in the future: record it after it took place.");
                break;
            case "commercial.invoice":
            {
                var rows = DocJson.Rows(d, "lines").OfType<JsonObject>().ToList();
                for (var i = 0; i < rows.Count; i++)
                    if (D(rows[i]["quantity"]) is 0) c.Error($"lines[{i}].quantity", "zero_quantity", $"Line {i + 1}: quantity must be more than zero.");
                break;
            }
            case "commercial.packing_list":
            {
                var rows = DocJson.Rows(d, "cartons").OfType<JsonObject>().ToList();
                var ranges = new List<(int Row, decimal From, decimal To)>();
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (D(r["cartonFrom"]) is { } f && D(r["cartonTo"]) is { } t)
                    {
                        if (t < f) c.Error($"cartons[{i}].cartonTo", "range_order", $"Row {i + 1}: 'carton to' is before 'carton from'.");
                        else ranges.Add((i, f, t));
                    }
                    if (D(r["netKg"]) is { } net && D(r["grossKg"]) is { } gross && gross < net)
                        c.Error($"cartons[{i}].grossKg", "gross_below_net", $"Row {i + 1}: gross weight is below net weight.");
                }
                foreach (var a in ranges)
                    foreach (var b in ranges.Where(b => b.Row > a.Row && b.From <= a.To && a.From <= b.To))
                        c.Error($"cartons[{b.Row}].cartonFrom", "overlapping_cartons", $"Rows {a.Row + 1} and {b.Row + 1} use the same carton numbers.");
                break;
            }
            case "commercial.shipment":
                if (DocJson.Date(d["etd"]) is { } etd && DocJson.Date(d["eta"]) is { } eta && eta < etd) c.Error("eta", "date_order", "ETA can't be before ETD.");
                break;
            case "devices.maintenance":
                if (S(d["maintenanceType"]) == "BREAKDOWN" && S(d["problem"]) is null) c.Miss("problem", "Problem", "A breakdown needs the problem description.");
                break;
        }
    }

    /// <summary>Normalised roll identity within a document: lot + roll number (upper-case).</summary>
    public static string RollKey(JsonObject r) => $"{(DocJson.Str(r["lotNumber"]) ?? "").ToUpperInvariant()}|{(DocJson.Str(r["rollNumber"]) ?? "").ToUpperInvariant()}";

    /// <summary>
    /// Roll table checks that need no database: positive quantities, a roll listed once, and on a GRN every roll's item
    /// on the note with the rolls of an item adding up to its accepted quantity (the roll stock must equal the item stock it adds).
    /// Issue notes are checked against roll balances when the document is enriched.
    /// </summary>
    private static void RollRules(JsonObject d, List<JsonObject> items, string qtyKey, bool isReceipt, DocumentChecks c)
    {
        var rolls = DocJson.Rows(d, "rolls").OfType<JsonObject>().ToList();
        if (rolls.Count == 0) return;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rolls.Count; i++)
        {
            if (DocJson.Dec(rolls[i]["quantity"]) is { } q && q <= 0) c.Error($"rolls[{i}].quantity", "zero_quantity", $"Roll row {i + 1}: quantity must be more than zero.");
            if (DocJson.Dec(rolls[i]["widthCm"]) is { } w && w <= 0) c.Error($"rolls[{i}].widthCm", "invalid_width", $"Roll row {i + 1}: width must be more than zero.");
            if (DocJson.Str(rolls[i]["rollNumber"]) is null) continue;
            var key = RollKey(rolls[i]);
            if (seen.TryGetValue(key, out var first))
                c.Error($"rolls[{i}].rollNumber", "duplicate_roll", $"Roll rows {first + 1} and {i + 1} are the same roll ({DocJson.Str(rolls[i]["rollNumber"])}).");
            else seen[key] = i;
        }
        if (!isReceipt) return;
        var byItem = items.Where(r => DocJson.Str(r["itemCode"]) is not null)
            .GroupBy(r => DocJson.Str(r["itemCode"])!.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => (Qty: g.Sum(r => DocJson.Dec(r[qtyKey]) ?? 0), Category: DocJson.Str(g.First()["category"])));
        for (var i = 0; i < rolls.Count; i++)
            if (DocJson.Str(rolls[i]["itemCode"])?.ToUpperInvariant() is { } code && !byItem.ContainsKey(code))
                c.Error($"rolls[{i}].itemCode", "roll_item_not_listed", $"Roll row {i + 1}: item {code} is not on this GRN.");
        foreach (var (code, item) in byItem)
        {
            var rolled = rolls.Where(r => string.Equals(DocJson.Str(r["itemCode"]), code, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rolled.Count == 0) continue;
            var sum = rolled.Sum(r => DocJson.Dec(r["quantity"]) ?? 0);
            if (sum != item.Qty)
                c.Error("rolls", "rolls_dont_add_up", $"The rolls of {code} add up to {sum}, but {item.Qty} is accepted: list every accepted roll (and only those).");
            if (item.Category is not null and not "FABRIC")
                c.Warn("rolls", "rolls_not_fabric", $"{code} is not a FABRIC item but is received by roll.");
        }
    }

    private static void DuplicateCodes(List<JsonObject> rows, string table, string key, DocumentChecks c, bool error)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var code = DocJson.Str(rows[i][key]);
            if (code is null) continue;
            if (seen.TryGetValue(code, out var first))
            {
                var msg = $"Rows {first + 1} and {i + 1} both list {code}.";
                if (error) c.Error($"{table}[{i}].{key}", "duplicate_row", msg); else c.Warn($"{table}[{i}].{key}", "duplicate_row", msg);
            }
            else seen[code] = i;
        }
    }

    /// <summary>
    /// The duplicate-prevention key: the type plus its number field (invoice no., challan no. …),
    /// else plus the period and line for period documents; null when the type allows many.
    /// </summary>
    public static string? DedupKey(DocumentTypeDef type, JsonObject d, DateOnly? from, DateOnly? to, string? lineId)
    {
        if (type.NumberField is { } nf)
        {
            var value = DocJson.Str(d[nf]);
            if (value is null) return null;
            var extra = type.Code == "store.grn" ? "|" + (DocJson.Str(d["supplier"]) ?? "").ToUpperInvariant() : "";
            return $"{type.Code}|{value.ToUpperInvariant()}{extra}";
        }
        if (type.Period == PeriodKind.None || from is null) return null;
        var param = type.Parameters is { Count: > 0 } ps ? "|" + string.Join(",", ps.Select(p => DocJson.Str(d[p.Key]) ?? "ALL")) : "";
        return $"{type.Code}|{from:yyyy-MM-dd}|{(to ?? from):yyyy-MM-dd}|{lineId ?? "*"}{param}";
    }

    /// <summary>Keep only the keys the type defines (top level and table columns), converted to the field's kind; report what was dropped.</summary>
    public static JsonObject Sanitize(DocumentTypeDef type, JsonObject input, bool keepComputed, List<string>? dropped = null)
    {
        var result = new JsonObject();
        foreach (var f in type.Fields.Concat(type.Parameters ?? Array.Empty<FieldDef>()))
        {
            if (f.Computed && !keepComputed && f.Kind != FieldKind.Table) continue;
            if (f.Computed && f.Kind == FieldKind.Table && !keepComputed) continue;
            if (!input.TryGetPropertyValue(f.Key, out var v) || v is null) continue;
            var clean = Coerce(f, v, keepComputed, $"{f.Key}", dropped);
            if (clean is not null) result[f.Key] = clean;
        }
        foreach (var k in input.Select(kv => kv.Key).Where(k => type.Fields.All(f => f.Key != k) && (type.Parameters?.All(p => p.Key != k) ?? true)))
            dropped?.Add(k);
        return result;
    }

    private static JsonNode? Coerce(FieldDef f, JsonNode v, bool keepComputed, string path, List<string>? dropped)
    {
        switch (f.Kind)
        {
            case FieldKind.Table:
            {
                if (v is not JsonArray arr) { dropped?.Add(path); return null; }
                var rows = new JsonArray();
                foreach (var item in arr.Take(2000))
                {
                    if (item is not JsonObject o) continue;
                    var row = new JsonObject();
                    foreach (var col in f.Columns ?? Array.Empty<FieldDef>())
                    {
                        if (col.Computed && !keepComputed) continue;
                        if (o.TryGetPropertyValue(col.Key, out var cv) && cv is not null && Coerce(col, cv, keepComputed, $"{path}.{col.Key}", dropped) is { } c) row[col.Key] = c;
                    }
                    if (row.Count > 0) rows.Add(row);
                }
                return rows;
            }
            case FieldKind.Number:
            case FieldKind.Integer:
            {
                var n = DocJson.Dec(v);
                if (n is null) { if (DocJson.Str(v) is { } raw) return JsonValue.Create(raw); return null; } // kept as typed so validation can name it
                return f.Kind == FieldKind.Integer && n == decimal.Truncate(n.Value) ? JsonValue.Create((long)n.Value) : JsonValue.Create(n.Value);
            }
            case FieldKind.Boolean:
                return DocJson.Str(v) is { } bs ? JsonValue.Create(bs.ToLowerInvariant() is "true" or "yes" or "1" or "y") : null;
            case FieldKind.Date:
            {
                var s = DocJson.Str(v);
                if (s is null) return null;
                return DocJson.Date(v) is { } d ? JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : JsonValue.Create(s);
            }
            case FieldKind.Select:
            {
                var s = DocJson.Str(v);
                if (s is null) return null;
                var match = f.Options?.FirstOrDefault(o => string.Equals(o, s, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Replace("_", " "), s, StringComparison.OrdinalIgnoreCase));
                return JsonValue.Create(match ?? s);
            }
            default:
            {
                var s = DocJson.Str(v);
                return s is null ? null : JsonValue.Create(s.Length > 4000 ? s[..4000] : s);
            }
        }
    }
}
