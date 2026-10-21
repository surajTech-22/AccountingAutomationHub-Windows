using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Text;

namespace AccountingAutomationHub.Desktop.Services;

public static class AccountingReportExportService
{
    public sealed record Column(string Header, string Path);

    public static void ExportCsv(string path, IEnumerable<object> rows, IReadOnlyList<Column> columns)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(",", columns.Select(c => Csv(c.Header))));
        foreach (var row in rows)
            writer.WriteLine(string.Join(",", columns.Select(c => Csv(Format(Read(row, c.Path))))));
    }

    public static void ExportXlsx(string path, string title, IEnumerable<object> rows, IReadOnlyList<Column> columns)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "[Content_Types].xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
""");
        Write(archive, "_rels/.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
""");
        Write(archive, "xl/workbook.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Report" sheetId="1" r:id="rId1"/></sheets></workbook>
""");
        Write(archive, "xl/_rels/workbook.xml.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
""");
        Write(archive, "xl/styles.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf fontId="0" fillId="0" borderId="0" xfId="0"/><xf fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>
""");

        var data = rows.ToArray();
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        WriteRow(sheet, 1, new[] { title }, 1);
        WriteRow(sheet, 2, columns.Select(c => c.Header), 1);
        var r = 3;
        foreach (var row in data) WriteRow(sheet, r++, columns.Select(c => Format(Read(row, c.Path))), 0);
        sheet.Append("</sheetData><autoFilter ref=\"A2:").Append(ColumnName(Math.Max(1, columns.Count))).Append(Math.Max(2, r - 1)).Append("\"/></worksheet>");
        Write(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    public static void ExportPdf(string path, string title, IEnumerable<object> rows, IReadOnlyList<Column> columns)
    {
        var lines = new List<string> { title, new string('-', 118) };
        var widths = AllocateWidths(columns.Select(c => c.Header).ToArray(), 118);
        lines.Add(RenderLine(columns.Select(c => c.Header).ToArray(), widths));
        lines.Add(new string('-', 118));
        foreach (var row in rows)
            lines.Add(RenderLine(columns.Select(c => Format(Read(row, c.Path))).ToArray(), widths));

        var pages = lines.Chunk(48).ToArray();
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var objects = new List<byte[]>();
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Array.Empty<byte>()); // pages populated later
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"));
        var pageIds = new List<int>();
        foreach (var pageLines in pages)
        {
            var content = new StringBuilder("BT /F1 8 Tf 28 560 Td 10 TL ");
            foreach (var line in pageLines)
            {
                content.Append('(').Append(PdfEscape(Ascii(line))).Append(") Tj T* ");
            }
            content.Append("ET");
            var contentBytes = Encoding.ASCII.GetBytes(content.ToString());
            var contentId = objects.Count + 1;
            objects.Add(Encoding.ASCII.GetBytes($"<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream"));
            var pageId = objects.Count + 1;
            objects.Add(Encoding.ASCII.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentId} 0 R >>"));
            pageIds.Add(pageId);
        }
        objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>");

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            writer.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
            writer.Write(objects[i]);
            writer.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
        }
        var xref = stream.Position;
        writer.Write(Encoding.ASCII.GetBytes($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets.Skip(1)) writer.Write(Encoding.ASCII.GetBytes($"{offset:0000000000} 00000 n \n"));
        writer.Write(Encoding.ASCII.GetBytes($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"));
    }

    private static object? Read(object row, string path)
    {
        object? value = row;
        foreach (var part in path.Split('.'))
        {
            if (value is null) return null;
            value = value.GetType().GetProperty(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(value);
        }
        return value;
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateTime date => date.ToString("dd.MM.yyyy"),
        DateTimeOffset date => date.ToString("dd.MM.yyyy"),
        decimal n => Math.Abs(n) < 0.0000005m ? string.Empty : n.ToString("N2", CultureInfo.CurrentCulture),
        double n => Math.Abs(n) < 0.0000005 ? string.Empty : n.ToString("N2", CultureInfo.CurrentCulture),
        float n => Math.Abs(n) < 0.0000005 ? string.Empty : n.ToString("N2", CultureInfo.CurrentCulture),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
    };

    private static string Csv(string value) => '"' + (value ?? string.Empty).Replace("\"", "\"\"") + '"';
    private static string Xml(string value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
    private static void Write(ZipArchive archive, string name, string text) { var entry = archive.CreateEntry(name, CompressionLevel.Optimal); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text.TrimStart()); }
    private static void WriteRow(StringBuilder sheet, int row, IEnumerable<string> values, int style)
    {
        sheet.Append("<row r=\"").Append(row).Append("\">");
        var col = 1;
        foreach (var value in values)
        {
            sheet.Append("<c r=\"").Append(ColumnName(col++)).Append(row).Append("\" t=\"inlineStr\" s=\"").Append(style).Append("\"><is><t xml:space=\"preserve\">").Append(Xml(value)).Append("</t></is></c>");
        }
        sheet.Append("</row>");
    }
    private static string ColumnName(int index) { var s = string.Empty; while (index > 0) { index--; s = (char)('A' + index % 26) + s; index /= 26; } return s; }
    private static int[] AllocateWidths(IReadOnlyList<string> headers, int total) { if (headers.Count == 0) return []; var baseWidth = Math.Max(8, total / headers.Count); return Enumerable.Repeat(baseWidth, headers.Count).ToArray(); }
    private static string RenderLine(IReadOnlyList<string> values, IReadOnlyList<int> widths)
    {
        return string.Join(" ", values.Select((value, index) =>
        {
            var text = Ascii(value).Replace("\r", " ").Replace("\n", " ");
            var width = widths[index];
            return text.Length > width ? text[..width] : text.PadRight(width);
        }));
    }
    private static string Ascii(string value) => new(value.Select(ch => ch >= 32 && ch <= 126 ? ch : ' ').ToArray());
    private static string PdfEscape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
