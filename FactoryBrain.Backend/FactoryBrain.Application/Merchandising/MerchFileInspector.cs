using System.IO.Compression;
using System.Text;

namespace FactoryBrain.Application.Merchandising;

/// <summary>What an uploaded file really is, decided from its bytes (the extension must agree).</summary>
public sealed record InspectedFile(
    /// <summary><c>pdf</c>, <c>txt</c>, <c>csv</c>, <c>docx</c> or <c>image</c>.</summary>
    string Format,
    string ContentType,
    /// <summary>Extension the store uses (<c>pdf</c>, <c>txt</c>, <c>csv</c>, <c>docx</c>, <c>jpg</c>, <c>png</c>).</summary>
    string StorageExtension);

/// <summary>
/// Server-side content validation for order documents. The format comes
/// from the file's own signature — never from the client's content type —
/// and a file whose extension claims something else is refused. Images are
/// accepted (to keep the original on record) but need OCR to be read.
/// </summary>
public static class MerchFileInspector
{
    public const string ErrEmpty = "empty";
    public const string ErrTooLarge = "too_large";
    public const string ErrUnsupported = "unsupported_format";
    public const string ErrSpreadsheet = "spreadsheet_not_supported";
    public const string ErrLegacyOffice = "legacy_office_format";
    public const string ErrMismatch = "extension_mismatch";
    public const string ErrNotText = "not_text";
    public const string ErrMalformedDocx = "malformed_docx";

    public static readonly string[] AcceptedExtensions = { ".pdf", ".txt", ".csv", ".docx", ".jpg", ".jpeg", ".png" };
    public static readonly string[] SupportedFormats = { "pdf", "txt", "csv", "docx", "image" };

    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static string Describe(string error, long maxBytes) => error switch
    {
        ErrEmpty => "The file is empty.",
        ErrTooLarge => $"The file is larger than {maxBytes / (1024 * 1024.0):0.#} MB.",
        ErrSpreadsheet => "Excel workbooks are not read yet. Save the sheet as CSV (or PDF) and upload that.",
        ErrLegacyOffice => "Legacy .doc / .xls files are not supported. Save the file as DOCX, CSV or PDF.",
        ErrMismatch => "The file's content does not match its extension.",
        ErrNotText => "The file is not readable text (it contains binary data or is not UTF-8 / UTF-16).",
        ErrMalformedDocx => "The DOCX file is damaged or is not a Word document.",
        _ => $"Unsupported file type. Upload {string.Join(", ", AcceptedExtensions)}.",
    };

    public static bool TryInspect(byte[] content, string? fileName, long maxBytes, out InspectedFile? file, out string? error)
    {
        file = null;
        error = null;
        if (content.Length == 0) { error = ErrEmpty; return false; }
        if (content.LongLength > maxBytes) { error = ErrTooLarge; return false; }
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (ext.Length > 0 && !AcceptedExtensions.Contains(ext))
        {
            error = ext is ".xlsx" or ".xlsm" or ".ods" ? ErrSpreadsheet : ext is ".doc" or ".xls" ? ErrLegacyOffice : ErrUnsupported;
            return false;
        }

        if (IsPdf(content))
            return Accept(ext, new[] { ".pdf" }, new InspectedFile("pdf", "application/pdf", "pdf"), out file, out error);
        if (Starts(content, 0xFF, 0xD8, 0xFF))
            return Accept(ext, new[] { ".jpg", ".jpeg" }, new InspectedFile("image", "image/jpeg", "jpg"), out file, out error);
        if (Starts(content, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
            return Accept(ext, new[] { ".png" }, new InspectedFile("image", "image/png", "png"), out file, out error);
        if (Starts(content, 0xD0, 0xCF, 0x11, 0xE0)) { error = ErrLegacyOffice; return false; }
        if (Starts(content, 0x50, 0x4B, 0x03, 0x04))
        {
            var kind = ZipKind(content);
            if (kind == "xlsx") { error = ErrSpreadsheet; return false; }
            if (kind != "docx") { error = ext == ".docx" ? ErrMalformedDocx : ErrUnsupported; return false; }
            return Accept(ext, new[] { ".docx" }, new InspectedFile("docx", DocxContentType, "docx"), out file, out error);
        }

        if (ext is not ("" or ".txt" or ".csv")) { error = ErrMismatch; return false; }
        if (DecodeText(content) is null) { error = ErrNotText; return false; }
        file = ext == ".csv"
            ? new InspectedFile("csv", "text/csv", "csv")
            : new InspectedFile("txt", "text/plain", "txt");
        return true;
    }

    /// <summary>Text of a UTF-8 (BOM optional) or UTF-16 (with BOM) file; null when it isn't text.</summary>
    public static string? DecodeText(byte[] content)
    {
        string text;
        try
        {
            if (Starts(content, 0xFF, 0xFE)) text = new UnicodeEncoding(false, true, true).GetString(content, 2, content.Length - 2);
            else if (Starts(content, 0xFE, 0xFF)) text = new UnicodeEncoding(true, true, true).GetString(content, 2, content.Length - 2);
            else
            {
                var offset = Starts(content, 0xEF, 0xBB, 0xBF) ? 3 : 0;
                text = new UTF8Encoding(false, true).GetString(content, offset, content.Length - offset);
            }
        }
        catch (DecoderFallbackException) { return null; }
        // Control characters other than tab / newline / carriage return / form feed mean binary data.
        return text.Any(c => c < ' ' && c is not ('\t' or '\n' or '\r' or '\f')) ? null : text;
    }

    private static bool Accept(string ext, string[] allowed, InspectedFile result, out InspectedFile? file, out string? error)
    {
        if (ext.Length > 0 && !allowed.Contains(ext)) { file = null; error = ErrMismatch; return false; }
        file = result;
        error = null;
        return true;
    }

    /// <summary>PDF header within the first 1 KB, as the specification allows.</summary>
    private static bool IsPdf(byte[] b)
    {
        var limit = Math.Min(b.Length - 5, 1024);
        for (var i = 0; i <= limit; i++)
            if (b[i] == '%' && b[i + 1] == 'P' && b[i + 2] == 'D' && b[i + 3] == 'F' && b[i + 4] == '-') return true;
        return false;
    }

    private static string? ZipKind(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > 2000) return null;
            if (zip.GetEntry("word/document.xml") is not null) return "docx";
            if (zip.GetEntry("xl/workbook.xml") is not null) return "xlsx";
            return null;
        }
        catch (InvalidDataException) { return null; }
    }

    private static bool Starts(byte[] b, params byte[] prefix) => b.Length >= prefix.Length && prefix.Select((p, i) => b[i] == p).All(x => x);
}
