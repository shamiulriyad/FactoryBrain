using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace BunonBrain.Infrastructure.Documents;

/// <summary>
/// One embedded font: the Skia typeface that draws it and the HarfBuzz face
/// that shapes text for it (same bytes). Loaded once per process.
/// </summary>
internal sealed class PdfFontFace
{
    public SKTypeface Typeface { get; }
    public int UnitsPerEm { get; }
    public string Name { get; }
    // HarfBuzz reads the font from native memory for as long as the font lives. The bytes are copied to
    // unmanaged memory that is never moved (a managed array can be relocated by a compacting GC, which made
    // hb_shape read freed memory and crash the process); blob, face and font are kept for the process lifetime.
    private readonly Blob _blob;
    private readonly Face _face;
    private readonly HbFont _hb;
    private readonly object _gate = new();

    private PdfFontFace(string name, byte[] bytes)
    {
        Name = name;
        Typeface = SKTypeface.FromData(SKData.CreateCopy(bytes))
                   ?? throw new InvalidOperationException($"Font {name} could not be loaded.");
        var native = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, native, bytes.Length);
        _blob = new Blob(native, bytes.Length, MemoryMode.ReadOnly, () => System.Runtime.InteropServices.Marshal.FreeHGlobal(native));
        _blob.MakeImmutable();
        _face = new Face(_blob, 0);
        _face.MakeImmutable();
        UnitsPerEm = _face.UnitsPerEm;
        _hb = new HbFont(_face);
        _hb.SetScale(UnitsPerEm, UnitsPerEm); // positions in font units
    }

    public static PdfFontFace Load(string file)
    {
        var asm = typeof(PdfFontFace).Assembly;
        using var s = asm.GetManifestResourceStream("BunonBrain.Fonts." + file)
                      ?? throw new InvalidOperationException($"Embedded font {file} is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new PdfFontFace(file, ms.ToArray());
    }

    public bool Has(int codepoint) => Typeface.GetGlyph(codepoint) != 0;

    /// <summary>Glyphs, advances / offsets (font units) and clusters (UTF-16 index into <paramref name="text"/>).</summary>
    public (GlyphInfo[] Infos, GlyphPosition[] Positions) Shape(string text)
    {
        lock (_gate)
        {
            using var buffer = new HbBuffer();
            buffer.AddUtf16(text);
            buffer.GuessSegmentProperties();
            _hb.Shape(buffer);
            return (buffer.GlyphInfos.ToArray(), buffer.GlyphPositions.ToArray());
        }
    }
}

/// <summary>
/// The fonts of document PDFs: Noto Sans (Latin, Greek, Cyrillic, common symbols) and
/// Noto Sans Bengali, regular and bold, embedded in this assembly (SIL OFL 1.1).
/// </summary>
internal static class PdfFonts
{
    private static readonly Lazy<(PdfFontFace Latin, PdfFontFace LatinBold, PdfFontFace Bengali, PdfFontFace BengaliBold)> Set = new(() =>
        (PdfFontFace.Load("NotoSans-Regular.ttf"), PdfFontFace.Load("NotoSans-Bold.ttf"),
         PdfFontFace.Load("NotoSansBengali-Regular.ttf"), PdfFontFace.Load("NotoSansBengali-Bold.ttf")));

    public static PdfFontFace Latin(bool bold) => bold ? Set.Value.LatinBold : Set.Value.Latin;
    public static PdfFontFace Bengali(bool bold) => bold ? Set.Value.BengaliBold : Set.Value.Bengali;

    /// <summary>Bengali block, the dandas and the joiners used inside Bengali words.</summary>
    public static bool IsBengali(int cp) => cp is >= 0x0980 and <= 0x09FF or 0x0964 or 0x0965 or 0x200C or 0x200D;
}

/// <summary>A run of text in one font, shaped: glyph ids, pen positions (points, relative to the run start) and clusters.</summary>
internal sealed record ShapedRun(PdfFontFace Face, string Text, ushort[] Glyphs, SKPoint[] Points, uint[] Clusters, float Width);

/// <summary>A line of text split into font runs and shaped with HarfBuzz (conjuncts, vowel signs and reordering for Bangla).</summary>
internal sealed record ShapedLine(IReadOnlyList<ShapedRun> Runs, float Width)
{
    public static readonly ShapedLine Empty = new(Array.Empty<ShapedRun>(), 0);
}

internal static class TextShaper
{
    /// <summary>Characters neither font has: spelled out where common, otherwise replaced by '?'.</summary>
    private static string Substitute(int cp) => cp switch
    {
        0x2192 => "->", 0x2190 => "<-", 0x2265 => ">=", 0x2264 => "<=", 0x2026 => "...", 0x03A3 => "sum ",
        _ => "?",
    };

    /// <summary>Split <paramref name="text"/> into Latin / Bengali runs (control characters become spaces, unknown characters are substituted).</summary>
    private static List<(PdfFontFace Face, string Text)> Runs(string text, bool bold)
    {
        var runs = new List<(PdfFontFace, string)>();
        var latin = PdfFonts.Latin(bold);
        var bengali = PdfFonts.Bengali(bold);
        var sb = new StringBuilder();
        PdfFontFace? current = null;
        void Flush() { if (sb.Length > 0 && current is not null) runs.Add((current, sb.ToString())); sb.Clear(); }
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp < 0x20 || cp is >= 0x7F and < 0xA0) cp = ' ';
            PdfFontFace face;
            string piece;
            if (PdfFonts.IsBengali(cp) && (cp is 0x200C or 0x200D || bengali.Has(cp))) { face = bengali; piece = char.ConvertFromUtf32(cp); }
            else if (cp is 0x200C or 0x200D) { continue; }
            else if (latin.Has(cp)) { face = latin; piece = char.ConvertFromUtf32(cp); }
            else if (bengali.Has(cp)) { face = bengali; piece = char.ConvertFromUtf32(cp); }
            else { face = latin; piece = Substitute(cp); }
            // Spaces and punctuation between Bengali words stay in the Bengali run when that font has them (fewer runs, same look).
            if (current == bengali && face == latin && cp < 0x80 && !char.IsLetter((char)cp) && bengali.Has(cp)) face = bengali;
            if (face != current) { Flush(); current = face; }
            sb.Append(piece);
        }
        Flush();
        return runs;
    }

    public static ShapedLine Shape(string text, float size, bool bold)
    {
        if (string.IsNullOrEmpty(text)) return ShapedLine.Empty;
        var result = new List<ShapedRun>();
        float total = 0;
        foreach (var (face, runText) in Runs(text, bold))
        {
            var (infos, pos) = face.Shape(runText);
            var scale = size / face.UnitsPerEm;
            var glyphs = new ushort[infos.Length];
            var points = new SKPoint[infos.Length];
            var clusters = new uint[infos.Length];
            float pen = 0;
            for (var i = 0; i < infos.Length; i++)
            {
                glyphs[i] = (ushort)infos[i].Codepoint;
                points[i] = new SKPoint(pen + pos[i].XOffset * scale, -pos[i].YOffset * scale);
                clusters[i] = infos[i].Cluster;
                pen += pos[i].XAdvance * scale;
            }
            result.Add(new ShapedRun(face, runText, glyphs, points, clusters, pen));
            total += pen;
        }
        return new ShapedLine(result, total);
    }

    public static float Width(string text, float size, bool bold) => Shape(text, size, bold).Width;

    /// <summary>Draw a shaped line with its baseline at (x, y). Glyph runs carry their text and clusters, so the PDF text can be searched and copied.</summary>
    public static void Draw(SKCanvas canvas, ShapedLine line, float x, float y, float size, SKPaint paint)
    {
        if (line.Runs.Count == 0) return;
        using var builder = new SKTextBlobBuilder();
        float pen = 0;
        foreach (var run in line.Runs)
        {
            using var font = new SKFont(run.Face.Typeface, size) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None, Edging = SKFontEdging.Antialias };
            var utf8 = Encoding.UTF8.GetBytes(run.Text);
            // UTF-16 index → UTF-8 byte offset, for the clusters.
            var byteAt = new int[run.Text.Length + 1];
            var bytes = 0;
            for (var i = 0; i < run.Text.Length; i++)
            {
                byteAt[i] = bytes;
                if (char.IsHighSurrogate(run.Text[i]) && i + 1 < run.Text.Length && char.IsLowSurrogate(run.Text[i + 1]))
                {
                    byteAt[++i] = bytes;
                    bytes += 4;
                }
                else bytes += Encoding.UTF8.GetByteCount(run.Text.AsSpan(i, 1));
            }
            byteAt[run.Text.Length] = bytes;
            var buffer = builder.AllocatePositionedTextRun(font, run.Glyphs.Length, utf8.Length);
            buffer.SetGlyphs(run.Glyphs);
            var pts = new SKPoint[run.Points.Length];
            for (var i = 0; i < pts.Length; i++) pts[i] = new SKPoint(run.Points[i].X + pen, run.Points[i].Y);
            buffer.SetPositions(pts);
            buffer.SetText(utf8);
            buffer.SetClusters(run.Clusters.Select(c => (uint)byteAt[Math.Min((int)c, run.Text.Length)]).ToArray());
            pen += run.Width;
        }
        using var blob = builder.Build();
        if (blob is not null) canvas.DrawText(blob, x, y, paint);
    }
}

/// <summary>
/// A4 PDF writer for documents and reports: text and tables flowed over pages, with
/// HarfBuzz shaping so Bangla (and mixed Bangla / English) text keeps its conjuncts,
/// vowel signs and order; fonts are embedded (subset) and the text stays copyable.
/// Coordinates are points, origin top-left.
/// </summary>
public sealed class ShapedPdfWriter : IDisposable
{
    private const float PageW = 595.28f, PageH = 841.89f, Left = 40, Right = 555, Top = 46, Bottom = 790;
    private readonly MemoryStream _out = new();
    private readonly SKDocument _doc;
    private readonly string _footer;
    private SKCanvas? _canvas;
    private float _y;
    private int _pages;
    private readonly SKPaint _ink = new() { Color = SKColors.Black, IsAntialias = true };
    private readonly SKPaint _muted = new() { Color = new SKColor(0x55, 0x5B, 0x66), IsAntialias = true };
    private readonly SKPaint _line = new() { Color = new SKColor(0xB8, 0xBD, 0xC6), StrokeWidth = 0.5f, IsStroke = true, IsAntialias = true };
    private readonly SKPaint _headFill = new() { Color = new SKColor(0xEE, 0xF0, 0xF3), IsStroke = false };
    private bool _closed;

    public ShapedPdfWriter(string title, string footer)
    {
        _footer = footer;
        _doc = SKDocument.CreatePdf(_out, new SKDocumentPdfMetadata
        {
            Title = title, Creator = "BunonBrain", Producer = "BunonBrain (Skia PDF)", Creation = DateTime.UtcNow, Modified = DateTime.UtcNow,
            RasterDpi = 150, EncodingQuality = 90,
        }) ?? throw new InvalidOperationException("PDF backend unavailable.");
        NewPage();
    }

    private static float LineHeight(float size) => size * 1.45f;

    private void NewPage()
    {
        if (_canvas is not null) _doc.EndPage();
        _canvas = _doc.BeginPage(PageW, PageH);
        _pages++;
        _y = Top;
        var foot = TextShaper.Shape($"{_footer} - page {_pages}", 7, false);
        TextShaper.Draw(_canvas, Fit(foot, $"{_footer} - page {_pages}", 7, false, Right - Left), Left, PageH - 28, 7, _muted);
    }

    /// <summary>Cut a line that is too wide (footer only) with an ellipsis.</summary>
    private static ShapedLine Fit(ShapedLine line, string text, float size, bool bold, float width)
    {
        if (line.Width <= width) return line;
        var cut = text;
        while (cut.Length > 4 && TextShaper.Width(cut + "...", size, bold) > width) cut = cut[..^Math.Max(1, cut.Length / 10)];
        return TextShaper.Shape(cut + "...", size, bold);
    }

    private void Need(float h) { if (_y + h > Bottom) NewPage(); }

    public void Text(string text, float size = 9, bool bold = false, float indent = 0, bool muted = false)
    {
        foreach (var line in Wrap(text, size, bold, Right - Left - indent))
        {
            var lh = LineHeight(size);
            Need(lh);
            TextShaper.Draw(_canvas!, line, Left + indent, _y + size, size, muted ? _muted : _ink);
            _y += lh;
        }
    }

    public void Gap(float h = 6) => _y += h;

    public void Rule()
    {
        Need(10);
        _canvas!.DrawLine(Left, _y + 3, Right, _y + 3, _line);
        _y += 9;
    }

    /// <summary>A table with a shaded header row (repeated on each new page) and wrapped cells.</summary>
    public void Table(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, float size = 7.5f)
    {
        var n = Math.Max(1, columns.Count);
        const float pad = 3;
        var hasHeader = columns.Any(c => !string.IsNullOrWhiteSpace(c));
        // Natural widths (bounded), then scaled to the page.
        var want = new float[n];
        for (var i = 0; i < n; i++)
        {
            var w = TextShaper.Width(i < columns.Count ? columns[i] : "", size, true);
            foreach (var r in rows.Take(300)) if (i < r.Count) w = Math.Max(w, TextShaper.Width(r[i] ?? "", size, false));
            want[i] = Math.Clamp(w + 2 * pad, 28, 230);
        }
        var scale = (Right - Left) / want.Sum();
        var widths = want.Select(w => w * scale).ToArray();
        var lh = LineHeight(size);

        List<List<ShapedLine>> Cells(IReadOnlyList<string> cells, bool bold) =>
            Enumerable.Range(0, n).Select(i => Wrap(i < cells.Count ? cells[i] ?? "" : "", size, bold, widths[i] - 2 * pad).ToList()).ToList();

        void Row(List<List<ShapedLine>> cells, bool header)
        {
            var h = Math.Max(1, cells.Max(c => c.Count)) * lh + 2 * pad - 2;
            var x = Left;
            if (header) _canvas!.DrawRect(Left, _y, Right - Left, h, _headFill);
            for (var i = 0; i < n; i++)
            {
                for (var k = 0; k < cells[i].Count; k++)
                    TextShaper.Draw(_canvas!, cells[i][k], x + pad, _y + pad + k * lh + size, size, _ink);
                x += widths[i];
            }
            _canvas!.DrawLine(Left, _y + h, Right, _y + h, _line);
            _y += h;
        }

        var head = hasHeader ? Cells(columns, true) : null;
        var headH = head is null ? 0 : Math.Max(1, head.Max(c => c.Count)) * lh + 2 * pad;
        if (head is not null) { Need(headH + lh + 2 * pad); _canvas!.DrawLine(Left, _y, Right, _y, _line); Row(head, true); }
        foreach (var r in rows)
        {
            var cells = Cells(r, false);
            var h = Math.Max(1, cells.Max(c => c.Count)) * lh + 2 * pad;
            if (_y + h > Bottom)
            {
                NewPage();
                if (head is not null) { _canvas!.DrawLine(Left, _y, Right, _y, _line); Row(head, true); }
            }
            Row(cells, false);
        }
        _y += 6;
    }

    /// <summary>Greedy word wrap on shaped widths; a word longer than the line is cut between text elements (never inside a Bangla cluster).</summary>
    private static IEnumerable<ShapedLine> Wrap(string text, float size, bool bold, float width)
    {
        text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        var any = false;
        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && TextShaper.Width(candidate, size, bold) > width)
                {
                    any = true;
                    yield return TextShaper.Shape(line.ToString(), size, bold);
                    line.Clear().Append(word);
                }
                else line.Clear().Append(candidate);
                while (line.Length > 1 && TextShaper.Width(line.ToString(), size, bold) > width)
                {
                    var elements = StringInfo.GetTextElementEnumerator(line.ToString());
                    var piece = new StringBuilder();
                    while (elements.MoveNext())
                    {
                        var next = piece + (string)elements.Current;
                        if (piece.Length > 0 && TextShaper.Width(next, size, bold) > width) break;
                        piece.Clear().Append(next);
                    }
                    if (piece.Length == 0 || piece.Length >= line.Length) break;
                    any = true;
                    yield return TextShaper.Shape(piece.ToString(), size, bold);
                    line.Remove(0, piece.Length);
                }
            }
            any = true;
            yield return TextShaper.Shape(line.ToString(), size, bold);
        }
        if (!any) yield return ShapedLine.Empty;
    }

    public byte[] Build()
    {
        if (!_closed)
        {
            _doc.EndPage();
            _doc.Close();
            _closed = true;
        }
        return _out.ToArray();
    }

    public void Dispose()
    {
        _doc.Dispose();
        _ink.Dispose(); _muted.Dispose(); _line.Dispose(); _headFill.Dispose();
        _out.Dispose();
    }
}
