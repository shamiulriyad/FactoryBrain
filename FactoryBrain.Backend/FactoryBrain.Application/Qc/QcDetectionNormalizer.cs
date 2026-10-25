using System.Text.Json;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Application.Qc;

/// <summary>A provider defect after taxonomy mapping and validation, ready to store.</summary>
public sealed record MappedDefect(
    string RawLabel,
    string? CategoryCode,
    double? Confidence,
    NormalizedBox? Box,
    double[]? Polygon,
    string? Severity,
    string? Description);

/// <summary>
/// Turns provider output into the internal contract. Parsing is strict about
/// shape (a response that isn't the documented JSON is <c>invalid_output</c>,
/// never a guess), and per-defect values that are out of range are dropped
/// rather than repaired: an invalid box is removed (the defect is kept
/// without localisation), an invalid confidence removes the defect.
/// </summary>
public static class QcDetectionNormalizer
{
    /// <summary>Tolerance for boxes that overshoot the image edge by rounding.</summary>
    private const double Edge = 0.01;

    /// <summary>
    /// The JSON schema the vision-language model is asked for:
    /// <c>{"defects":[{"label","confidence","box_2d":[ymin,xmin,ymax,xmax] (0–1000),"severity","description"}]}</c>.
    /// </summary>
    public static IReadOnlyList<DetectedDefect> ParseVisionModelJson(string? content)
    {
        var json = StripCodeFence(content);
        if (string.IsNullOrWhiteSpace(json))
            throw new DefectDetectionException(DefectDetectionException.InvalidOutput, "The vision model returned an empty response.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new DefectDetectionException(DefectDetectionException.InvalidOutput, "The vision model's response was not valid JSON.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("defects", out var defects)
                || defects.ValueKind != JsonValueKind.Array)
            {
                throw new DefectDetectionException(DefectDetectionException.InvalidOutput,
                    "The vision model's response did not contain a \"defects\" array.");
            }

            var list = new List<DetectedDefect>();
            foreach (var d in defects.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                var label = String(d, "label");
                if (string.IsNullOrWhiteSpace(label)) continue;
                double? confidence = Number(d, "confidence");
                NormalizedBox? box = null;
                if (d.TryGetProperty("box_2d", out var b) && b.ValueKind == JsonValueKind.Array && b.GetArrayLength() == 4
                    && b.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number))
                {
                    var v = b.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    // [ymin, xmin, ymax, xmax] on a 0–1000 grid.
                    box = new NormalizedBox(v[1] / 1000, v[0] / 1000, (v[3] - v[1]) / 1000, (v[2] - v[0]) / 1000);
                }
                list.Add(new DetectedDefect(label.Trim(), confidence, box, null, String(d, "severity"), String(d, "description")));
            }
            return list;
        }
    }

    /// <summary>
    /// Validate, map to the taxonomy and filter by confidence. A label maps
    /// when it equals a category code or name (case-insensitive); otherwise
    /// the defect is kept with no category so an inspector can correct it.
    /// </summary>
    public static IReadOnlyList<MappedDefect> Map(
        IReadOnlyList<DetectedDefect> defects, IReadOnlyList<DetectionCategory> categories, double threshold)
    {
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in categories)
        {
            byKey.TryAdd(c.Code, c.Code);
            byKey.TryAdd(c.Name, c.Code);
            byKey.TryAdd(c.Code.Replace('_', ' '), c.Code);
        }

        var result = new List<MappedDefect>();
        foreach (var d in defects)
        {
            if (d.Confidence is { } c && (double.IsNaN(c) || c < 0 || c > 1)) continue;
            if (d.Confidence is { } kept && kept < threshold) continue;
            var label = Truncate(d.Label.Trim(), 64);
            if (label.Length == 0) continue;
            result.Add(new MappedDefect(
                label,
                byKey.TryGetValue(label, out var code) ? code : null,
                d.Confidence is { } conf ? Math.Round(conf, 4) : null,
                ValidBox(d.Box),
                ValidPolygon(d.Polygon),
                NormalizeSeverity(d.Severity),
                d.Description is null ? null : Truncate(d.Description.Trim(), 500)));
            if (result.Count == QcLimits.MaxFindingsPerInspection) break;
        }
        return result;
    }

    public static NormalizedBox? ValidBox(NormalizedBox? b)
    {
        if (b is null) return null;
        double[] v = { b.X, b.Y, b.Width, b.Height };
        if (v.Any(x => double.IsNaN(x) || double.IsInfinity(x))) return null;
        if (b.X < -Edge || b.Y < -Edge || b.Width <= 0 || b.Height <= 0) return null;
        if (b.X + b.Width > 1 + Edge || b.Y + b.Height > 1 + Edge) return null;
        var x = Math.Clamp(b.X, 0, 1);
        var y = Math.Clamp(b.Y, 0, 1);
        return new NormalizedBox(Round(x), Round(y), Round(Math.Min(b.Width, 1 - x)), Round(Math.Min(b.Height, 1 - y)));
    }

    public static double[]? ValidPolygon(double[]? p)
    {
        if (p is null || p.Length < 6 || p.Length % 2 != 0 || p.Length > 512) return null;
        if (p.Any(v => double.IsNaN(v) || v < -Edge || v > 1 + Edge)) return null;
        return p.Select(v => Round(Math.Clamp(v, 0, 1))).ToArray();
    }

    public static string? NormalizeSeverity(string? raw) =>
        QcWire.TryParseSeverity(raw, out var s) ? QcWire.Severity(s) : null;

    private static string? StripCodeFence(string? s)
    {
        if (s is null) return null;
        var t = s.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstNewline = t.IndexOf('\n');
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline < 0 || lastFence <= firstNewline) return t;
        return t[(firstNewline + 1)..lastFence].Trim();
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double Round(double v) => Math.Round(v, 5);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
