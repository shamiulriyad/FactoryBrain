using System.Globalization;
using System.Text;
using BunonBrain.Application.Analytics;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>
/// A plain, printable PDF of a report (A4, Helvetica) with PdfPig's writer:
/// header (scope, period, generation time, data source, filters, warnings),
/// each section's KPIs and tables, then the metric definitions. Standard
/// fonts only cover Latin text: other characters (e.g. Bengali names) are
/// replaced; the CSV export keeps them.
/// </summary>
public static class PdfReportWriter
{
    private const double Left = 40, Right = 555, Top = 800, Bottom = 50;

    private sealed class Writer
    {
        private readonly PdfDocumentBuilder _doc = new();
        private readonly PdfDocumentBuilder.AddedFont _font;
        private readonly PdfDocumentBuilder.AddedFont _bold;
        private PdfPageBuilder _page = null!;
        private double _y;
        private int _pages;
        private readonly string _footer;

        public Writer(string footer)
        {
            _font = _doc.AddStandard14Font(Standard14Font.Helvetica);
            _bold = _doc.AddStandard14Font(Standard14Font.HelveticaBold);
            _footer = footer;
            NewPage();
        }

        private void NewPage()
        {
            _page = _doc.AddPage(PageSize.A4);
            _pages++;
            _y = Top;
            _page.AddText(Clean($"{_footer} - page {_pages}"), 7, new PdfPoint(Left, 30), _font);
        }

        private void Need(double h) { if (_y - h < Bottom) NewPage(); }

        public void Text(string text, double size = 9, bool bold = false, double indent = 0)
        {
            var max = Right - Left - indent;
            foreach (var line in Wrap(Clean(text), size, max, bold ? _bold : _font))
            {
                Need(size + 4);
                _page.AddText(line, size, new PdfPoint(Left + indent, _y), bold ? _bold : _font);
                _y -= size + 4;
            }
        }

        public void Gap(double h = 6) { _y -= h; }

        public void Rule()
        {
            Need(8);
            _page.DrawLine(new PdfPoint(Left, _y + 4), new PdfPoint(Right, _y + 4), 0.5);
            _y -= 6;
        }

        public void Table(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, double size = 7.5)
        {
            var n = Math.Max(1, columns.Count);
            // Width per column from the longest cell (bounded), scaled to the page.
            var want = Enumerable.Range(0, n).Select(i => Math.Clamp(rows.Select(r => i < r.Count ? r[i].Length : 0).Append(columns[i].Length).Max(), 4, 40)).ToArray();
            var scale = (Right - Left) / want.Sum();
            var widths = want.Select(w => w * scale).ToArray();
            void Line(IReadOnlyList<string> cells, bool bold)
            {
                var wrapped = Enumerable.Range(0, n).Select(i => Wrap(Clean(i < cells.Count ? cells[i] : ""), size, widths[i] - 4, bold ? _bold : _font).ToList()).ToList();
                var lines = Math.Max(1, wrapped.Max(w => w.Count));
                Need(lines * (size + 3) + 2);
                var x = Left;
                for (var i = 0; i < n; i++)
                {
                    for (var k = 0; k < wrapped[i].Count; k++)
                        _page.AddText(wrapped[i][k], size, new PdfPoint(x, _y - k * (size + 3)), bold ? _bold : _font);
                    x += widths[i];
                }
                _y -= lines * (size + 3) + 2;
            }
            Line(columns, true);
            foreach (var r in rows) Line(r, false);
            _y -= 4;
        }

        private IEnumerable<string> Wrap(string text, double size, double width, PdfDocumentBuilder.AddedFont font)
        {
            if (text.Length == 0) { yield return ""; yield break; }
            var words = text.Split(' ');
            var line = new StringBuilder();
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Width(candidate, size, font) > width)
                {
                    yield return line.ToString();
                    line.Clear().Append(word);
                }
                else { line.Clear().Append(candidate); }
                // A single word longer than the column: cut it.
                while (Width(line.ToString(), size, font) > width && line.Length > 4)
                {
                    var cut = Math.Max(1, (int)(line.Length * width / Width(line.ToString(), size, font)) - 1);
                    yield return line.ToString(0, cut);
                    line.Remove(0, cut);
                }
            }
            if (line.Length > 0) yield return line.ToString();
        }

        private double Width(string s, double size, PdfDocumentBuilder.AddedFont font)
        {
            var letters = _page.MeasureText(s, size, new PdfPoint(0, 0), font);
            return letters.Count == 0 ? 0 : letters.Max(l => l.BoundingBox.Right) - letters.Min(l => l.BoundingBox.Left);
        }

        public byte[] Build() => _doc.Build();
    }

    /// <summary>Text the standard PDF fonts can show (WinAnsi): common symbols spelled out, anything else replaced.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch switch
            {
                '—' or '–' or '−' => "-",
                '·' or '•' => "-",
                '→' => "->",
                '≥' => ">=",
                '≤' => "<=",
                '×' => "x",
                '÷' => "/",
                '…' => "...",
                'Σ' => "sum ",
                '°' => " deg",
                '‘' or '’' => "'",
                '“' or '”' => "\"",
                '\t' or '\r' or '\n' => " ",
                _ when ch >= 32 && ch < 127 => ch.ToString(),
                _ => "?",
            });
        return sb.ToString();
    }

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
        var w = new Writer($"{r.Title} - {r.Scope} - {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}{(r.Source == "SIMULATED" ? " - SIMULATED DATA" : "")}");
        w.Text(r.Title, 16, true);
        if (r.Source == "SIMULATED") w.Text("SIMULATED DATA - not real production, machines or attendance.", 10, true);
        w.Text($"Scope: {r.Scope}");
        w.Text($"Period: {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd} ({r.TimeZone})");
        w.Text($"Generated: {r.GeneratedAt:yyyy-MM-dd HH:mm} UTC by {r.GeneratedBy}");
        w.Text($"Data: {r.Source}");
        foreach (var f in r.Filters) w.Text($"Filter - {f.Name}: {f.Value}");
        foreach (var x in r.Warnings) w.Text($"Warning: {x}", 8.5);
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
                w.Text(t.Title, 9.5, true);
                if (t.Note is not null) w.Text(t.Note, 7.5);
                if (t.Rows.Count == 0) { w.Text("No data.", 8); continue; }
                w.Table(t.Columns, t.Rows.Select(row => (IReadOnlyList<string>)row.Select(Cell).ToList()).ToList());
            }
        }
        w.Gap();
        w.Rule();
        w.Text("Metric definitions", 12, true);
        foreach (var d in r.Definitions)
        {
            w.Text($"{d.Name} ({d.Kind.ToLowerInvariant().Replace('_', ' ')}, {d.Unit})", 8.5, true);
            w.Text($"Formula: {d.Formula}. Window: {d.Window} Includes: {d.Includes} Excludes: {d.Excludes}" +
                   $"{(d.Denominator is null ? "" : $" Denominator: {d.Denominator}.")} Missing data: {d.MissingData} Sources: {string.Join(", ", d.Sources)}.", 7.5, false, 8);
        }
        return w.Build();
    }
}
