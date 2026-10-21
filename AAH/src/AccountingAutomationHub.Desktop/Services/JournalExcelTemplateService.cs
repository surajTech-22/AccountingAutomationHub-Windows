using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

public static class JournalExcelTemplateService
{
    private static readonly string[] Headers =
        ["Voucher No", "Date", "Ledger", "Amount", "Dr/Cr", "Narration", "Tax Head", "Role", "Voucher Type"];

    public static void Write(string path, ClientProfile client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        if (File.Exists(path)) File.Delete(path);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Write(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Write(archive, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Journal Register\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"__AAH_JOURNAL_META\" sheetId=\"2\" r:id=\"rId2\" state=\"hidden\"/></sheets></workbook>");
        Write(archive, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Write(archive, "xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"dd.mm.yyyy\"/><numFmt numFmtId=\"165\" formatCode=\"#,##0.00\"/></numFmts><fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FF1F1F1F\"/><sz val=\"12\"/><name val=\"Calibri\"/></font></fonts><fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEAF2F8\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"2\"><border/><border><left style=\"thin\"><color rgb=\"FFD9E2F3\"/></left><right style=\"thin\"><color rgb=\"FFD9E2F3\"/></right><top style=\"thin\"><color rgb=\"FFD9E2F3\"/></top><bottom style=\"thin\"><color rgb=\"FFD9E2F3\"/></bottom><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"7\"><xf xfId=\"0\"/><xf xfId=\"0\" fontId=\"2\" fillId=\"3\" applyFont=\"1\" applyFill=\"1\"/><xf xfId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf><xf xfId=\"0\" numFmtId=\"164\" borderId=\"1\" applyNumberFormat=\"1\" applyBorder=\"1\"/><xf xfId=\"0\" numFmtId=\"165\" borderId=\"1\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\"/></xf><xf xfId=\"0\" borderId=\"1\" applyBorder=\"1\"/><xf xfId=\"0\" borderId=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf></cellXfs></styleSheet>");

        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><dimension ref=\"A1:I202\"/><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"2\" topLeftCell=\"A3\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/><cols><col min=\"1\" max=\"1\" width=\"18\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"14\" customWidth=\"1\"/><col min=\"3\" max=\"3\" width=\"34\" customWidth=\"1\"/><col min=\"4\" max=\"4\" width=\"16\" customWidth=\"1\"/><col min=\"5\" max=\"5\" width=\"10\" customWidth=\"1\"/><col min=\"6\" max=\"6\" width=\"52\" customWidth=\"1\"/><col min=\"7\" max=\"9\" width=\"18\" customWidth=\"1\"/></cols><sheetData>");
        sheet.Append(Row(1, ["JOURNAL VOUCHER IMPORT FORMAT"], 1));
        sheet.Append("<row r=\"2\" ht=\"24\" customHeight=\"1\">");
        for (var c = 0; c < Headers.Length; c++) sheet.Append(Cell(Column(c + 1) + "2", Headers[c], 2));
        sheet.Append("</row>");
        for (var row = 3; row <= 202; row++)
        {
            sheet.Append($"<row r=\"{row}\">");
            sheet.Append(BlankCell(1, row, 5));
            sheet.Append(BlankCell(2, row, 3));
            sheet.Append(BlankCell(3, row, 5));
            sheet.Append(BlankCell(4, row, 4));
            sheet.Append(BlankCell(5, row, 5));
            sheet.Append(BlankCell(6, row, 6));
            sheet.Append(BlankCell(7, row, 5));
            sheet.Append(BlankCell(8, row, 5));
            sheet.Append(BlankCell(9, row, 5));
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData><autoFilter ref=\"A2:I202\"/></worksheet>");
        Write(archive, "xl/worksheets/sheet1.xml", sheet.ToString());

        var meta = new[]
        {
            ("Kind", GeneratedAccountingWorkbookIdentity.JournalWorkbookKind),
            ("SchemaVersion", "2"),
            ("ClientId", client.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("ClientCode", client.ClientCode),
            ("ClientName", client.ClientName),
            ("FinancialYear", client.FinancialYear),
            ("ExportedUtc", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("AutoImportRule", "A blank AAH Journal format is a template, not Action Required. Completed rows are classified directly as Journal and then use the normal Journal importer.")
        };
        var metaSheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var index = 0; index < meta.Length; index++)
        {
            var row = index + 1;
            metaSheet.Append($"<row r=\"{row}\">{Cell("A" + row, meta[index].Item1, 2)}{Cell("B" + row, meta[index].Item2, 0)}</row>");
        }
        metaSheet.Append("</sheetData></worksheet>");
        Write(archive, "xl/worksheets/sheet2.xml", metaSheet.ToString());
    }

    private static string Row(int row, IReadOnlyList<string> values, int style) =>
        $"<row r=\"{row}\">{string.Concat(values.Select((value, index) => Cell(Column(index + 1) + row, value, style)))}</row>";

    private static string Cell(string reference, string value, int style) =>
        $"<c r=\"{reference}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(value) ?? string.Empty}</t></is></c>";

    private static string BlankCell(int column, int row, int style) =>
        $"<c r=\"{Column(column)}{row}\" s=\"{style}\"/>";

    private static string Column(int n)
    {
        var result = string.Empty;
        while (n > 0)
        {
            n--;
            result = (char)('A' + n % 26) + result;
            n /= 26;
        }
        return result;
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
