using System.IO.Compression;
using System.Security;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FactoryBrain.Api.Tests;

/// <summary>Real, small documents built in memory for the merchandising tests (no fixtures on disk).</summary>
public static class TestDocuments
{
    /// <summary>A text-layer PDF: one page per entry, one line of text per string.</summary>
    public static byte[] Pdf(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 800.0;
            foreach (var line in lines)
            {
                page.AddText(line, 10, new PdfPoint(40, y), font);
                y -= 16;
            }
        }
        return builder.Build();
    }

    /// <summary>A PDF whose only page has a drawing and no text layer, like a scan.</summary>
    public static byte[] ScannedPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.DrawRectangle(new PdfPoint(40, 400), 300, 200, 2);
        return builder.Build();
    }

    /// <summary>A minimal WordprocessingML document with paragraphs and one table.</summary>
    public static byte[] Docx(IEnumerable<string> paragraphs, IEnumerable<string[]>? tableRows = null)
    {
        var body = new StringBuilder();
        foreach (var p in paragraphs) body.Append($"<w:p><w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(p)}</w:t></w:r></w:p>");
        if (tableRows is not null)
        {
            body.Append("<w:tbl>");
            foreach (var row in tableRows)
            {
                body.Append("<w:tr>");
                foreach (var cell in row) body.Append($"<w:tc><w:p><w:r><w:t>{SecurityElement.Escape(cell)}</w:t></w:r></w:p></w:tc>");
                body.Append("</w:tr>");
            }
            body.Append("</w:tbl>");
        }
        var xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add(zip, "word/document.xml", xml);
        }
        return ms.ToArray();
    }

    /// <summary>A zip that looks like an Excel workbook.</summary>
    public static byte[] Xlsx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            Add(zip, "xl/workbook.xml", "<workbook/>");
        return ms.ToArray();
    }

    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
