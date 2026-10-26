using System.IO.Compression;
using System.Text;
using System.Xml;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Merchandising;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace FactoryBrain.Infrastructure.Merchandising;

public sealed record ParsedDocument(IReadOnlyList<PageText> Pages, int PageCount, bool Truncated)
{
    public int TextLength => Pages.Sum(p => p.Text.Length);
}

/// <summary>The file could not be read. <see cref="Code"/> is stable; the message is safe to show.</summary>
public sealed class DocumentParseException : Exception
{
    public const string Malformed = "malformed_document";
    public const string Encrypted = "encrypted_document";
    public const string OcrRequired = "ocr_required";
    public const string TooManyPages = "too_many_pages";
    public const string NoText = "no_text";

    public string Code { get; }
    public DocumentParseException(string code, string message, Exception? inner = null) : base(message, inner) { Code = code; }
}

/// <summary>
/// Text per page from the supported formats: PDF text layers (PdfPig, one
/// entry per page, in reading order), DOCX (paragraphs, and table rows as
/// tab-separated cells), CSV (records as tab-separated cells) and plain
/// text (one page each). Scanned PDFs and images have no text layer and
/// need OCR, which is not configured; they are reported as such, never
/// guessed. Limits guard against oversized and decompression-bomb files.
/// </summary>
public static class DocumentTextParser
{
    /// <summary>Uncompressed size allowed for a DOCX part.</summary>
    private const long MaxDocxPartBytes = 40L * 1024 * 1024;
    /// <summary>A PDF with fewer non-whitespace characters per page than this has no usable text layer.</summary>
    private const int MinCharsPerPage = 8;

    public static ParsedDocument Parse(byte[] content, string format, int maxPages, int maxChars) => format switch
    {
        "pdf" => ParsePdf(content, maxPages, maxChars),
        "docx" => Single(ParseDocx(content), maxChars),
        "csv" => Single(ParseCsv(Decode(content)), maxChars),
        "txt" => Single(Decode(content), maxChars),
        "image" => throw new DocumentParseException(DocumentParseException.OcrRequired,
            "This is an image. Reading text from images needs OCR, which is not configured on this server. Enter the values by hand or upload a text-based PDF."),
        _ => throw new DocumentParseException(DocumentParseException.Malformed, "Unsupported document format."),
    };

    private static string Decode(byte[] content) =>
        MerchFileInspector.DecodeText(content) ?? throw new DocumentParseException(DocumentParseException.Malformed, "The file is not readable text.");

    private static ParsedDocument Single(string text, int maxChars)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var truncated = normalized.Length > maxChars;
        if (truncated) normalized = normalized[..maxChars];
        if (string.IsNullOrWhiteSpace(normalized)) throw new DocumentParseException(DocumentParseException.NoText, "The document contains no text.");
        return new ParsedDocument(new[] { new PageText(1, normalized) }, 1, truncated);
    }

    // ── PDF ─────────────────────────────────────────────────────────────────

    private static ParsedDocument ParsePdf(byte[] content, int maxPages, int maxChars)
    {
        try
        {
            // Files with only an owner password (print / copy restrictions) open and are readable;
            // a user password raises PdfDocumentEncryptedException.
            using var pdf = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var count = pdf.NumberOfPages;
            if (count == 0) throw new DocumentParseException(DocumentParseException.Malformed, "The PDF has no pages.");
            if (count > maxPages) throw new DocumentParseException(DocumentParseException.TooManyPages, $"The PDF has {count} pages; at most {maxPages} are read.");

            var pages = new List<PageText>();
            var total = 0;
            var truncated = false;
            var meaningful = 0;
            for (var n = 1; n <= count; n++)
            {
                var page = pdf.GetPage(n);
                var text = ContentOrderTextExtractor.GetText(page) ?? "";
                text = text.Replace("\r\n", "\n").Replace('\r', '\n');
                if (text.Count(c => !char.IsWhiteSpace(c)) >= MinCharsPerPage) meaningful++;
                if (total + text.Length > maxChars)
                {
                    text = text[..Math.Max(0, maxChars - total)];
                    truncated = true;
                }
                pages.Add(new PageText(n, text));
                total += text.Length;
                if (truncated) break;
            }
            if (meaningful == 0)
                throw new DocumentParseException(DocumentParseException.OcrRequired,
                    "The PDF has no text layer (it is probably a scan). Reading scanned documents needs OCR, which is not configured on this server. Enter the values by hand or upload a text-based PDF.");
            return new ParsedDocument(pages, count, truncated);
        }
        catch (DocumentParseException) { throw; }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentParseException(DocumentParseException.Encrypted, "The PDF is password-protected. Upload an unprotected copy.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new DocumentParseException(DocumentParseException.Malformed, "The PDF is damaged or could not be read.", ex);
        }
    }

    // ── DOCX ────────────────────────────────────────────────────────────────

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string ParseDocx(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml") ?? throw new DocumentParseException(DocumentParseException.Malformed, "The DOCX has no document body.");
            if (entry.Length > MaxDocxPartBytes || (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 200))
                throw new DocumentParseException(DocumentParseException.Malformed, "The DOCX body is too large to read.");
            using var stream = new LimitedStream(entry.Open(), MaxDocxPartBytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });

            var sb = new StringBuilder();
            var cells = new List<string>();
            var cell = new StringBuilder();
            var tableDepth = 0;
            var inCell = false;
            while (reader.Read())
            {
                if (reader.NamespaceURI != W) continue;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "tbl": tableDepth++; break;
                        case "tc": inCell = true; cell.Clear(); break;
                        case "t": var t = reader.ReadElementContentAsString(); (inCell ? cell : sb).Append(t); break;
                        case "tab": (inCell ? cell : sb).Append(inCell ? ' ' : '\t'); break;
                        case "br" or "cr": (inCell ? cell : sb).Append(inCell ? ' ' : '\n'); break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    switch (reader.LocalName)
                    {
                        case "p": if (inCell) cell.Append(' '); else sb.Append('\n'); break;
                        case "tc": cells.Add(MerchFields.CollapseWhitespace(cell.ToString()) ?? ""); inCell = false; break;
                        case "tr": sb.Append(string.Join('\t', cells)).Append('\n'); cells.Clear(); break;
                        case "tbl": tableDepth = Math.Max(0, tableDepth - 1); break;
                    }
                }
            }
            return sb.ToString();
        }
        catch (DocumentParseException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            throw new DocumentParseException(DocumentParseException.Malformed, "The DOCX file is damaged or could not be read.", ex);
        }
    }

    // ── CSV ─────────────────────────────────────────────────────────────────

    /// <summary>RFC 4180-style records (quoted fields, doubled quotes, newlines inside quotes), as tab-separated lines.</summary>
    public static string ParseCsv(string text)
    {
        var delimiter = DetectDelimiter(text);
        var sb = new StringBuilder();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c is '\r' or '\n' ? ' ' : c);
                continue;
            }
            if (c == '"' && field.Length == 0) { quoted = true; continue; }
            if (c == delimiter) { record.Add(field.ToString().Trim()); field.Clear(); continue; }
            if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString().Trim());
                field.Clear();
                AppendRecord(sb, record);
                continue;
            }
            field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString().Trim()); AppendRecord(sb, record); }
        return sb.ToString();
    }

    private static void AppendRecord(StringBuilder sb, List<string> record)
    {
        while (record.Count > 0 && record[^1].Length == 0) record.RemoveAt(record.Count - 1);
        sb.Append(string.Join('\t', record.Select(f => f.Replace('\t', ' ')))).Append('\n');
        record.Clear();
    }

    private static char DetectDelimiter(string text)
    {
        var firstLines = string.Join("\n", text.Split('\n').Take(10));
        var counts = new[] { ',', ';', '\t' }.Select(d => (d, n: firstLines.Count(c => c == d))).OrderByDescending(x => x.n).First();
        return counts.n == 0 ? ',' : counts.d;
    }

    /// <summary>Throws once more than the limit has been read (the zip's declared size can lie).</summary>
    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;
        public LimitedStream(Stream inner, long limit) { _inner = inner; _limit = limit; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            _read += n;
            if (_read > _limit) throw new InvalidDataException("decompressed part exceeds the limit");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
