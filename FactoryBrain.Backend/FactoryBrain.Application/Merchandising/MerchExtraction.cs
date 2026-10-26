using System.Globalization;
using System.Text;
using System.Text.Json;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Application.Merchandising;

/// <summary>A field ready to be stored: the extractor's value, its validation and its checked evidence.</summary>
public sealed record FieldDraft(
    string Key,
    MerchFieldSource Source,
    string? RawValue,
    string? NormalizedValue,
    double? Confidence,
    int? Page,
    string? Snippet,
    string EvidenceStatus,
    IReadOnlyList<string> Issues);

/// <summary>
/// The JSON contract asked of a language model, and the parser for its
/// answer. The answer is schema-checked: unknown field keys are dropped,
/// values must be strings or numbers, a confidence outside 0–1 or a page
/// that isn't a positive integer is discarded (never repaired), and an answer
/// that isn't the contract is refused as <c>invalid_output</c>.
/// </summary>
public static class ExtractionOutputParser
{
    public static string BuildPrompt(ExtractionRequest r, int maxChars, out bool truncated)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Document type: {r.DocumentType}.");
        sb.AppendLine("Extract these fields when the document states them (use exactly these keys):");
        foreach (var f in MerchFields.All.Where(f => f.Type != MerchFieldType.Breakdown))
            sb.AppendLine($"- {f.Key}: {f.Description}");
        sb.AppendLine("Also extract the colour x size quantity breakdown if the document has one.");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only, exactly this shape:");
        sb.AppendLine("{\"fields\":[{\"key\":\"<key>\",\"value\":\"<value as written in the document>\",\"confidence\":<0..1>,\"page\":<page number>,\"evidence\":\"<exact quote from the document, max 200 characters>\"}],");
        sb.AppendLine(" \"breakdown\":{\"rows\":[{\"color\":\"<colour>\",\"size\":\"<size>\",\"quantity\":<integer>}],\"confidence\":<0..1>,\"page\":<page>,\"evidence\":\"<exact quote of the table header or first row>\"}}");
        sb.AppendLine("Rules: copy values and quotes verbatim from the text below; omit a field that the document does not state; never infer or calculate a value;");
        sb.AppendLine("confidence is your own estimate that the value is what the document says; omit \"breakdown\" when there is none.");
        sb.AppendLine();

        truncated = false;
        var budget = maxChars;
        foreach (var p in r.Pages)
        {
            var header = $"=== Page {p.PageNumber} ===";
            if (budget <= header.Length) { truncated = true; break; }
            sb.AppendLine(header);
            budget -= header.Length;
            var text = p.Text;
            if (text.Length > budget) { text = text[..budget]; truncated = true; }
            sb.AppendLine(text);
            budget -= text.Length;
        }
        return sb.ToString();
    }

    public const string SystemPrompt =
        "You extract order data from garment-industry documents (purchase orders, tech packs, spec sheets, buyer instructions). " +
        "You only report values that are written in the document, with an exact quote as evidence, and you never invent values.";

    /// <summary>Candidates from the model's JSON text. Throws <see cref="DocumentExtractionException"/> (<c>invalid_output</c>) when it isn't the contract.</summary>
    public static IReadOnlyList<ExtractedCandidate> Parse(string? content)
    {
        var json = StripFence(content);
        if (string.IsNullOrWhiteSpace(json)) throw Invalid("The model returned an empty answer.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw Invalid("The model's answer was not valid JSON.", ex); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                throw Invalid("The model's answer did not contain a \"fields\" array.");
            var result = new List<ExtractedCandidate>();
            foreach (var f in fields.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) throw Invalid("A field entry was not an object.");
                var key = Str(f, "key")?.Trim().ToLowerInvariant();
                if (!MerchFields.IsKnown(key) || key == MerchFields.Breakdown) continue;
                var value = f.TryGetProperty("value", out var v) ? v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                } : null;
                if (string.IsNullOrWhiteSpace(value)) continue;
                result.Add(new ExtractedCandidate(key!, value, Confidence(f), Page(f), Limit(Str(f, "evidence"))));
            }
            if (root.TryGetProperty("breakdown", out var b) && b.ValueKind == JsonValueKind.Object
                && b.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0)
            {
                result.Add(new ExtractedCandidate(MerchFields.Breakdown, rows.GetRawText(), Confidence(b), Page(b), Limit(Str(b, "evidence"))));
            }
            return result;
        }
    }

    private static double? Confidence(JsonElement e) =>
        e.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var d) && d is >= 0 and <= 1 && !double.IsNaN(d)
            ? d : null;

    private static int? Page(JsonElement e) =>
        e.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && n >= 1 ? n : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Limit(string? s) => s is null ? null : s.Length <= 600 ? s : s[..600];

    private static string? StripFence(string? s)
    {
        if (s is null) return null;
        var t = s.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0) return t;
        t = t[(firstNewline + 1)..];
        var end = t.LastIndexOf("```", StringComparison.Ordinal);
        return end >= 0 ? t[..end].Trim() : t.Trim();
    }

    private static DocumentExtractionException Invalid(string message, Exception? inner = null) =>
        new(DocumentExtractionException.InvalidOutput, message, inner);
}

/// <summary>
/// Turns extractor candidates into stored field rows: one per field key
/// (highest reported confidence wins, else the first), validated by
/// <see cref="MerchFields.Normalize"/>, evidence checked against the parsed
/// pages, low confidence flagged, and the breakdown total compared with the
/// quantity of the same document.
/// </summary>
public static class MerchExtractionNormalizer
{
    public static IReadOnlyList<FieldDraft> Build(IReadOnlyList<ExtractedCandidate> candidates, IReadOnlyList<PageText> pages,
        MerchFieldSource source, double minConfidence, DateOnly today)
    {
        var picked = candidates
            .Where(c => MerchFields.IsKnown(c.FieldKey))
            .Select((c, i) => (c, i))
            .GroupBy(x => x.c.FieldKey)
            .Select(g => g.OrderByDescending(x => x.c.Confidence ?? -1).ThenBy(x => x.i).First().c)
            .ToList();

        var drafts = new List<FieldDraft>();
        foreach (var c in picked)
        {
            var norm = MerchFields.Normalize(c.FieldKey, c.Value, c.FieldKey == MerchFields.DeliveryDate ? today : null);
            var issues = norm.Issues.ToList();
            string evidence;
            int? page = null;
            string? snippet = null;
            if (string.IsNullOrWhiteSpace(c.Evidence))
            {
                evidence = MerchEvidence.None;
                issues.Add(MerchFields.IssueNoEvidence);
            }
            else if (MerchEvidence.Locate(c.Evidence, c.Page, pages) is { } found)
            {
                evidence = MerchEvidence.Verified;
                (page, snippet) = found;
            }
            else
            {
                evidence = MerchEvidence.NotFound;
                issues.Add(MerchFields.IssueEvidenceNotFound);
            }
            if (c.Confidence is { } conf && conf < minConfidence) issues.Add(MerchFields.IssueLowConfidence);
            drafts.Add(new FieldDraft(c.FieldKey, source, Truncate(c.Value, 4000), norm.Normalized, c.Confidence, page, snippet, evidence, issues));
        }

        var qty = drafts.FirstOrDefault(d => d.Key == MerchFields.Quantity)?.NormalizedValue;
        var bdIndex = drafts.FindIndex(d => d.Key == MerchFields.Breakdown);
        if (qty is not null && bdIndex >= 0 && MerchFields.ParseBreakdown(drafts[bdIndex].NormalizedValue) is { } rows
            && MerchFields.BreakdownTotal(rows) != int.Parse(qty, CultureInfo.InvariantCulture))
        {
            drafts[bdIndex] = drafts[bdIndex] with { Issues = drafts[bdIndex].Issues.Append(MerchFields.IssueBreakdownTotal).ToList() };
        }
        return drafts;
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
