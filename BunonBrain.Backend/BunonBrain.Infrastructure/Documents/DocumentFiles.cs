using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Merchandising;
using BunonBrain.Infrastructure.Merchandising;
using BunonBrain.Infrastructure.Storage;

namespace BunonBrain.Infrastructure.Documents;

/// <summary>
/// Document automation settings, read once from the environment at startup.
/// <list type="bullet">
///   <item><c>DOCUMENT_STORAGE_PATH</c> (default <c>&lt;content root&gt;/App_Data/factory-documents</c>): private file store for uploaded originals;
///   files are only ever served through the authenticated API, never by URL.</item>
///   <item><c>DOCUMENT_MAX_MB</c> (default 15, at most 25), <c>DOCUMENT_MAX_PAGES</c> (default 100).</item>
///   <item><c>DOCUMENT_AI_MODEL</c>: model for document AI calls (defaults to the LLM model of Settings → AI / <c>LLM_MODEL</c>;
///   provider and key always come from there and are never sent to the browser).</item>
///   <item><c>DOCUMENT_AI_MAX_CHARS</c> (text sent per call, default 12000), <c>DOCUMENT_AI_TIMEOUT_SECONDS</c> (default 60),
///   <c>DOCUMENT_AI_MAX_ATTEMPTS</c> (default 3, retries only on time-outs, 429 and 5xx), <c>DOCUMENT_AI_MAX_EXTRACTIONS</c> (per file, default 3).</item>
///   <item><c>DOCUMENT_AUTOMATION_ENABLED</c> (default <b>false</b>): scheduled generation, missing-document and overdue checks in this instance.
///   Off until someone turns it on explicitly; per-type schedules are then set in Settings.</item>
/// </list>
/// </summary>
public sealed record DocumentOptions(
    string StoragePath, long MaxFileBytes, int MaxPages, string? AiModel, int AiMaxChars, TimeSpan AiTimeout, int AiMaxAttempts, int MaxExtractionsPerFile, bool AutomationEnabled)
{
    public static DocumentOptions FromEnvironment(Func<string, string?> env, string contentRoot)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var inv = CultureInfo.InvariantCulture;
        var maxMb = double.TryParse(Clean(env("DOCUMENT_MAX_MB")), NumberStyles.Float, inv, out var mb) && mb > 0 ? mb : 15;
        return new DocumentOptions(
            Path.GetFullPath(Clean(env("DOCUMENT_STORAGE_PATH")) ?? Path.Combine(contentRoot, "App_Data", "factory-documents")),
            Math.Min((long)(maxMb * 1024 * 1024), 25L * 1024 * 1024),
            int.TryParse(Clean(env("DOCUMENT_MAX_PAGES")), out var p) && p is >= 1 and <= 1000 ? p : 100,
            Clean(env("DOCUMENT_AI_MODEL")),
            int.TryParse(Clean(env("DOCUMENT_AI_MAX_CHARS")), out var c) && c is >= 500 and <= 200_000 ? c : 12_000,
            TimeSpan.FromSeconds(int.TryParse(Clean(env("DOCUMENT_AI_TIMEOUT_SECONDS")), out var t) && t is >= 5 and <= 600 ? t : 60),
            int.TryParse(Clean(env("DOCUMENT_AI_MAX_ATTEMPTS")), out var a) && a is >= 1 and <= 5 ? a : 3,
            int.TryParse(Clean(env("DOCUMENT_AI_MAX_EXTRACTIONS")), out var e) && e is >= 1 and <= 20 ? e : 3,
            bool.TryParse(Clean(env("DOCUMENT_AUTOMATION_ENABLED")), out var en) && en);
    }
}

/// <summary>Uploaded originals under <see cref="DocumentOptions.StoragePath"/> (server-generated keys, validated on every read, atomic writes).</summary>
public sealed class DocumentFileStore
{
    public static readonly string[] Extensions = { "pdf", "docx", "xlsx", "csv", "txt", "jpg", "png", "webp" };
    private readonly SafeFileStore _files;

    public DocumentFileStore(DocumentOptions options, ILogger<DocumentFileStore> log)
    {
        _files = new SafeFileStore(options.StoragePath, Extensions, log, "factory document");
    }

    public Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct) => _files.SaveAsync(content, extension, ct);
    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => _files.OpenReadAsync(key, ct);
    public Task DeleteAsync(string key, CancellationToken ct) => _files.DeleteAsync(key, ct);
}

/// <summary>What an uploaded document file is (from its bytes) and its text.</summary>
public sealed record DocumentFileInfo(string Format, string ContentType, string Extension);

public static class DocumentFileReader
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Identify a file by its signature; the extension must agree. Error codes are the merchandising inspector's plus <c>malformed_xlsx</c>.</summary>
    public static bool TryInspect(byte[] content, string? fileName, long maxBytes, out DocumentFileInfo? info, out string? error)
    {
        info = null; error = null;
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (content.Length > 12 && content[0] == (byte)'R' && content[1] == (byte)'I' && content[2] == (byte)'F' && content[3] == (byte)'F'
            && content[8] == (byte)'W' && content[9] == (byte)'E' && content[10] == (byte)'B' && content[11] == (byte)'P')
        {
            if (content.LongLength > maxBytes) { error = MerchFileInspector.ErrTooLarge; return false; }
            if (ext is not ("" or ".webp")) { error = MerchFileInspector.ErrMismatch; return false; }
            info = new DocumentFileInfo("image", "image/webp", "webp");
            return true;
        }
        if (content.Length > 4 && content[0] == 0x50 && content[1] == 0x4B && content[2] == 0x03 && content[3] == 0x04 && IsXlsx(content))
        {
            if (content.LongLength > maxBytes) { error = MerchFileInspector.ErrTooLarge; return false; }
            if (ext is not ("" or ".xlsx")) { error = MerchFileInspector.ErrMismatch; return false; }
            info = new DocumentFileInfo("xlsx", XlsxContentType, "xlsx");
            return true;
        }
        if (!MerchFileInspector.TryInspect(content, fileName, maxBytes, out var file, out error)) return false;
        info = new DocumentFileInfo(file!.Format, file.ContentType, file.StorageExtension);
        return true;
    }

    public static string Describe(string error, long maxBytes) => error switch
    {
        "malformed_xlsx" => "The Excel file could not be read.",
        MerchFileInspector.ErrSpreadsheet => "Only .xlsx workbooks are read (save older .xls files as .xlsx or CSV).",
        _ => MerchFileInspector.Describe(error, maxBytes),
    };

    private static bool IsXlsx(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            return zip.GetEntry("xl/workbook.xml") is not null;
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Text per page (PDF pages, or one page for DOCX / CSV / TXT; one page per sheet for XLSX). Images throw <c>ocr_required</c>.</summary>
    public static ParsedDocument ReadText(byte[] content, string format, int maxPages, int maxChars) =>
        format == "xlsx" ? XlsxReader.Read(content, maxChars) : DocumentTextParser.Parse(content, format, maxPages, maxChars);
}

/// <summary>Cell text of an .xlsx workbook (shared strings and inline values), one tab-separated page per sheet. Formulas give their cached values.</summary>
public static class XlsxReader
{
    private const long MaxPartBytes = 30L * 1024 * 1024;

    public static ParsedDocument Read(byte[] content, int maxChars)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var shared = new List<string>();
            if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
            {
                using var r = OpenXml(ss);
                var sb = new StringBuilder();
                var inSi = false;
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") { inSi = true; sb.Clear(); }
                    else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && inSi) sb.Append(r.ReadElementContentAsString());
                    else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si") { shared.Add(sb.ToString()); inSi = false; }
                }
            }
            var pages = new List<PageText>();
            var total = 0;
            var truncated = false;
            var sheets = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal).ToList();
            foreach (var sheet in sheets)
            {
                var text = new StringBuilder();
                using var r = OpenXml(sheet);
                var row = new SortedDictionary<int, string>();
                string? cellType = null; int col = 0;
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "row") row.Clear();
                    else if (r.NodeType == XmlNodeType.Element && r.LocalName == "c")
                    {
                        cellType = r.GetAttribute("t");
                        col = ColumnIndex(r.GetAttribute("r"), row.Count == 0 ? 0 : row.Keys.Max() + 1);
                    }
                    else if (r.NodeType == XmlNodeType.Element && (r.LocalName == "v" || (r.LocalName == "t" && cellType == "inlineStr")))
                    {
                        var v = r.ReadElementContentAsString();
                        row[col] = cellType == "s" && int.TryParse(v, out var si) && si >= 0 && si < shared.Count ? shared[si] : v;
                    }
                    else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row" && row.Count > 0)
                    {
                        var max = row.Keys.Max();
                        text.AppendJoin('\t', Enumerable.Range(0, max + 1).Select(i => row.TryGetValue(i, out var s) ? s.Replace('\t', ' ').Replace('\n', ' ') : ""));
                        text.Append('\n');
                    }
                }
                var page = text.ToString();
                if (total + page.Length > maxChars) { page = page[..Math.Max(0, maxChars - total)]; truncated = true; }
                total += page.Length;
                if (page.Length > 0) pages.Add(new PageText(pages.Count + 1, page));
                if (truncated) break;
            }
            if (pages.Count == 0) throw new DocumentParseException(DocumentParseException.NoText, "The workbook has no cell values.");
            return new ParsedDocument(pages, pages.Count, truncated);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            throw new DocumentParseException(DocumentParseException.Malformed, "The Excel file could not be read.", ex);
        }
    }

    private static XmlReader OpenXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPartBytes) throw new InvalidDataException("workbook part too large");
        return XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });
    }

    private static int ColumnIndex(string? cellRef, int fallback)
    {
        if (string.IsNullOrEmpty(cellRef)) return fallback;
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (ch is < 'A' or > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n == 0 ? fallback : n - 1;
    }
}

/// <summary>A rendered document: title block, facts, field values and tables. Written as PDF, XLSX or DOCX from the same model.</summary>
public sealed record RenderModel(string Title, string Subtitle, IReadOnlyList<(string Label, string Value)> Facts, IReadOnlyList<(string Label, string Value)> Fields,
    IReadOnlyList<RenderTable> Tables, IReadOnlyList<string> Notes, string Footer);

public sealed record RenderTable(string Title, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Minimal, dependency-free Office Open XML writers (one workbook / one Word document) for document exports.</summary>
public static class OfficeWriters
{
    private static string X(string s) => SecurityElement.Escape(Strip(s)) ?? "";

    /// <summary>XML 1.0 can't carry control characters; drop them.</summary>
    private static string Strip(string s) => new(s.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ').ToArray());

    private static void Add(ZipArchive zip, string path, string content)
    {
        var e = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    public static byte[] Xlsx(RenderModel m)
    {
        var sheets = new List<(string Name, List<IReadOnlyList<string>> Rows)>();
        var main = new List<IReadOnlyList<string>> { new[] { m.Title }, new[] { m.Subtitle }, Array.Empty<string>() };
        main.AddRange(m.Facts.Select(f => (IReadOnlyList<string>)new[] { f.Label, f.Value }));
        main.Add(Array.Empty<string>());
        main.AddRange(m.Fields.Select(f => (IReadOnlyList<string>)new[] { f.Label, f.Value }));
        if (m.Notes.Count > 0) { main.Add(Array.Empty<string>()); main.Add(new[] { "Notes" }); main.AddRange(m.Notes.Select(n => (IReadOnlyList<string>)new[] { n })); }
        sheets.Add(("Document", main));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Document" };
        foreach (var t in m.Tables)
        {
            var name = new string(t.Title.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
            if (name.Length > 28) name = name[..28];
            if (name.Length == 0) name = "Table";
            var unique = name; var i = 2;
            while (!used.Add(unique)) unique = $"{name[..Math.Min(name.Length, 25)]} {i++}";
            var rows = new List<IReadOnlyList<string>> { t.Headers };
            rows.AddRange(t.Rows);
            sheets.Add((unique, rows));
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                string.Concat(sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(sheets.Select((s, i) => $"<sheet name=\"{X(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add(zip, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf fontId=\"0\"/><xf fontId=\"1\" applyFont=\"1\"/></cellXfs></styleSheet>");
            for (var s = 0; s < sheets.Count; s++)
            {
                var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
                var rows = sheets[s].Rows;
                for (var r = 0; r < rows.Count; r++)
                {
                    sb.Append($"<row r=\"{r + 1}\">");
                    var bold = s > 0 ? r == 0 : r == 0;
                    for (var c = 0; c < rows[r].Count; c++)
                    {
                        var v = rows[r][c] ?? "";
                        var cref = Col(c) + (r + 1);
                        var style = bold ? " s=\"1\"" : "";
                        var numeric = v.Length is > 0 and < 16
                            && decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)
                            && !(v.Length > 1 && v[0] == '0' && v[1] != '.'); // keep codes like "007" as text
                        if (numeric)
                            sb.Append($"<c r=\"{cref}\"{style}><v>{X(v)}</v></c>");
                        else if (v.Length > 0)
                            sb.Append($"<c r=\"{cref}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{X(v.Length > 32000 ? v[..32000] : v)}</t></is></c>");
                    }
                    sb.Append("</row>");
                }
                sb.Append("</sheetData></worksheet>");
                Add(zip, $"xl/worksheets/sheet{s + 1}.xml", sb.ToString());
            }
        }
        return ms.ToArray();
    }

    private static string Col(int i)
    {
        var s = "";
        for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }

    public static byte[] Docx(RenderModel m)
    {
        static string P(string text, bool bold = false, int size = 20) =>
            $"<w:p><w:r><w:rPr>{(bold ? "<w:b/>" : "")}<w:sz w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">{X(text)}</w:t></w:r></w:p>";
        static string Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            var sb = new StringBuilder("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>" +
                "<w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/>" +
                "<w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr>");
            void Row(IReadOnlyList<string> cells, bool bold)
            {
                sb.Append("<w:tr>");
                foreach (var c in cells) sb.Append("<w:tc>").Append(P(c, bold, 16)).Append("</w:tc>");
                sb.Append("</w:tr>");
            }
            if (headers.Count > 0) Row(headers, true);
            foreach (var r in rows) Row(r, false);
            return sb.Append("</w:tbl>").ToString();
        }

        var body = new StringBuilder();
        body.Append(P(m.Title, true, 32)).Append(P(m.Subtitle, false, 18));
        body.Append(Table(Array.Empty<string>(), m.Facts.Select(f => (IReadOnlyList<string>)new[] { f.Label, f.Value })));
        if (m.Fields.Count > 0)
        {
            body.Append(P("")).Append(P("Details", true, 24));
            body.Append(Table(Array.Empty<string>(), m.Fields.Select(f => (IReadOnlyList<string>)new[] { f.Label, f.Value })));
        }
        foreach (var t in m.Tables)
        {
            body.Append(P("")).Append(P(t.Title, true, 24));
            body.Append(t.Rows.Count == 0 ? P("No rows.") : Table(t.Headers, t.Rows));
        }
        if (m.Notes.Count > 0)
        {
            body.Append(P("")).Append(P("Notes", true, 24));
            foreach (var n in m.Notes) body.Append(P("• " + n, false, 18));
        }
        body.Append(P("")).Append(P(m.Footer, false, 14));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add(zip, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                body + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1000\" w:right=\"900\" w:bottom=\"1000\" w:left=\"900\"/></w:sectPr></w:body></w:document>");
        }
        return ms.ToArray();
    }
}
