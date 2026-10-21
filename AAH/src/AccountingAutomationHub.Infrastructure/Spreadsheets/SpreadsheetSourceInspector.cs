using System.Text;

namespace AccountingAutomationHub.Infrastructure.Spreadsheets;

public enum SpreadsheetSourceKind
{
    Empty,
    OpenXmlZip,
    LegacyOleWorkbook,
    DelimitedText,
    HtmlTable,
    XmlSpreadsheet,
    UnknownBinary
}

public sealed record SpreadsheetSourceInspection(
    SpreadsheetSourceKind Kind,
    string Reason,
    char? Delimiter = null);

/// <summary>
/// Identifies spreadsheet containers by content rather than trusting the file extension.
/// Bank portals frequently download tab-delimited or HTML tables with a legacy .xls suffix;
/// those are valid tabular exports but are not BIFF/OLE Excel workbooks.
/// </summary>
public static class SpreadsheetSourceInspector
{
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static bool IsEncryptedOpenXmlContainer(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            // Encrypted OpenXML workbooks are stored in an OLE compound envelope. Do not
            // trust the file extension here: some bank portals (including PNB ONE) deliver
            // an encrypted OpenXML package with a legacy .xls suffix. In that case forcing
            // Excel's legacy BIFF Format=5 prevents an otherwise-correct password from opening it.
            if (Inspect(path).Kind != SpreadsheetSourceKind.LegacyOleWorkbook) return false;

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".xlsx" or ".xlsm" or ".xltx" or ".xltm") return true;

            return ContainsUtf16OleDirectoryToken(path, "EncryptedPackage") &&
                   ContainsUtf16OleDirectoryToken(path, "EncryptionInfo");
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsUtf16OleDirectoryToken(string path, string token)
    {
        var needle = Encoding.Unicode.GetBytes(token);
        if (needle.Length == 0) return false;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[64 * 1024 + needle.Length];
        var carry = 0;
        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read <= 0) return false;
            var available = carry + read;
            if (buffer.AsSpan(0, available).IndexOf(needle) >= 0) return true;

            carry = Math.Min(needle.Length - 1, available);
            if (carry > 0)
                buffer.AsSpan(available - carry, carry).CopyTo(buffer);
        }
    }

    public static bool IsReadableOpenXmlWorkbook(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            if (Inspect(path).Kind != SpreadsheetSourceKind.OpenXmlZip) return false;
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            return archive.GetEntry("[Content_Types].xml") is not null &&
                   archive.GetEntry("xl/workbook.xml") is not null;
        }
        catch { return false; }
    }

    public static SpreadsheetSourceInspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The spreadsheet source was not found.", path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
            return new(SpreadsheetSourceKind.Empty, "The file is empty.");

        var header = new byte[Math.Min(8192, (int)Math.Min(stream.Length, 8192))];
        var read = stream.Read(header, 0, header.Length);
        if (read >= 8 && header.AsSpan(0, 8).SequenceEqual(OleSignature))
            return new(SpreadsheetSourceKind.LegacyOleWorkbook, "Legacy BIFF/OLE Excel workbook signature detected.");
        if (read >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K' &&
            ((header[2] == 3 && header[3] == 4) || (header[2] == 5 && header[3] == 6) || (header[2] == 7 && header[3] == 8)))
            return new(SpreadsheetSourceKind.OpenXmlZip, "OpenXML ZIP workbook signature detected.");

        var sampleLength = (int)Math.Min(stream.Length, 512_000);
        stream.Position = 0;
        var bytes = new byte[sampleLength];
        _ = stream.Read(bytes, 0, bytes.Length);
        if (!LooksTextual(bytes))
            return new(SpreadsheetSourceKind.UnknownBinary, "The file is binary but is not a recognized OpenXML or OLE workbook signature.");

        var text = DecodeText(bytes);
        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.Contains("<table", StringComparison.OrdinalIgnoreCase) &&
             trimmed.Contains("<tr", StringComparison.OrdinalIgnoreCase)))
            return new(SpreadsheetSourceKind.HtmlTable, "HTML table content detected inside a spreadsheet-named file.");

        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Contains("urn:schemas-microsoft-com:office:spreadsheet", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("<Workbook", StringComparison.OrdinalIgnoreCase)))
            return new(SpreadsheetSourceKind.XmlSpreadsheet, "Excel 2003 XML Spreadsheet content detected.");

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var delimiter = FlexibleWorkbookReader.DetectDelimiter(lines);
        var meaningful = lines.Where(line => !string.IsNullOrWhiteSpace(line)).Take(80).ToArray();
        var delimitedRows = meaningful.Count(line => CountUnquoted(line, delimiter) >= 2);
        var headerEvidence = meaningful.Any(line =>
        {
            var upper = line.ToUpperInvariant();
            return upper.Contains("DATE") &&
                   (upper.Contains("DEBIT") || upper.Contains("CREDIT") || upper.Contains("AMOUNT") || upper.Contains("BALANCE"));
        });

        if (delimitedRows >= 2 || (delimitedRows >= 1 && headerEvidence))
            return new(SpreadsheetSourceKind.DelimitedText,
                $"Delimited text content detected ({DescribeDelimiter(delimiter)}) even though the extension may be Excel.", delimiter);

        // Plain text without enough tabular structure is still classified separately from
        // binary corruption so the caller can issue a useful message or recovery action.
        return new(SpreadsheetSourceKind.DelimitedText,
            "Text content detected. The importer will attempt delimiter/header discovery before treating it as corrupt.", delimiter);
    }

    private static bool LooksTextual(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return true;
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return true;
        if (bytes.IndexOf((byte)0) >= 0)
        {
            // UTF-16 commonly contains NUL every second byte; allow that pattern.
            var nul = 0;
            for (var i = 0; i < bytes.Length; i++) if (bytes[i] == 0) nul++;
            if (nul > bytes.Length / 5) return false;
        }

        var controls = 0;
        foreach (var value in bytes)
        {
            if (value == 0) continue;
            if (value < 0x09 || (value > 0x0D && value < 0x20)) controls++;
        }
        return controls <= Math.Max(4, bytes.Length / 50);
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static int CountUnquoted(string line, char delimiter)
    {
        var quoted = false;
        var count = 0;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') index++;
                else quoted = !quoted;
            }
            else if (!quoted && line[index] == delimiter) count++;
        }
        return count;
    }

    private static string DescribeDelimiter(char delimiter) => delimiter switch
    {
        '\t' => "tab-separated",
        ',' => "comma-separated",
        ';' => "semicolon-separated",
        '|' => "pipe-separated",
        _ => $"delimiter '{delimiter}'"
    };
}
