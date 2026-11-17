using System.Globalization;
using BunonBrain.Application.Analytics;
using BunonBrain.Infrastructure.Documents;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// A plain, printable PDF of a report (A4) with the shaping PDF writer:
/// header (scope, period, generation time, data source, filters, warnings),
/// each section's KPIs and tables, then the metric definitions. Fonts are
/// embedded Noto Sans / Noto Sans Bengali, so Bangla names print as written.
/// </summary>
public static class PdfReportWriter
{
    private static string Cell(object? v) => v switch
    {
        null => "",
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    public static byte[] Write(ReportDocument r)
    {
        using var w = new ShapedPdfWriter(r.Title, $"{r.Title} - {r.Scope} - {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}{(r.Source == "SIMULATED" ? " - SIMULATED DATA" : "")}");
        w.Text(r.Title, 16, true);
        if (r.Source == "SIMULATED") w.Text("SIMULATED DATA - not real production, machines or attendance.", 10, true);
        w.Text($"Scope: {r.Scope}");
        w.Text($"Period: {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd} ({r.TimeZone})");
        w.Text($"Generated: {r.GeneratedAt:yyyy-MM-dd HH:mm} UTC by {r.GeneratedBy}");
        w.Text($"Data: {r.Source}");
        foreach (var f in r.Filters) w.Text($"Filter - {f.Name}: {f.Value}");
        foreach (var x in r.Warnings) w.Text($"Warning: {x}", 8.5f);
        foreach (var s in r.Sections)
        {
            w.Gap();
            w.Rule();
            w.Text(s.Title, 12, true);
            if (s.Note is not null) w.Text(s.Note, 8);
            if (s.Kpis.Count > 0)
                w.Table(new[] { "Metric", "Value", "Unit", "Kind", "Note" },
                    s.Kpis.Select(k => (IReadOnlyList<string>)new[]
                    {
                        k.Label, k.Availability == Availability.Ok ? Cell(k.Value) : k.Availability.Replace('_', ' ').ToLowerInvariant(), k.Unit, k.Kind, k.Note ?? "",
                    }).ToList());
            foreach (var t in s.Tables)
            {
                w.Text(t.Title, 9.5f, true);
                if (t.Note is not null) w.Text(t.Note, 7.5f);
                if (t.Rows.Count == 0) { w.Text("No data.", 8); continue; }
                w.Table(t.Columns, t.Rows.Select(row => (IReadOnlyList<string>)row.Select(Cell).ToList()).ToList());
            }
        }
        w.Gap();
        w.Rule();
        w.Text("Metric definitions", 12, true);
        foreach (var d in r.Definitions)
        {
            w.Text($"{d.Name} ({d.Kind.ToLowerInvariant().Replace('_', ' ')}, {d.Unit})", 8.5f, true);
            w.Text($"Formula: {d.Formula}. Window: {d.Window} Includes: {d.Includes} Excludes: {d.Excludes}" +
                   $"{(d.Denominator is null ? "" : $" Denominator: {d.Denominator}.")} Missing data: {d.MissingData} Sources: {string.Join(", ", d.Sources)}.", 7.5f, false, 8);
        }
        return w.Build();
    }
}
