using System.Text;

namespace AccountingAutomationHub.Infrastructure.Spreadsheets;

/// <summary>
/// Reads normal OpenXML workbooks plus delimited CSV/TXT/TSV sources into the same
/// in-memory workbook shape used by the Sales/manual mapping engine. Extension is
/// treated only as a container hint; delimiter detection is content based.
/// </summary>
public sealed class FlexibleWorkbookReader
{
    private readonly OpenXmlWorkbookReader _openXml = new();

    public OpenXmlWorkbookData Read(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var inspection = SpreadsheetSourceInspector.Inspect(filePath);
        if (inspection.Kind == SpreadsheetSourceKind.Empty)
            throw new InvalidDataException("The spreadsheet source is empty.");
        if (inspection.Kind == SpreadsheetSourceKind.DelimitedText)
            return ReadDelimited(filePath, cancellationToken);
        if (inspection.Kind == SpreadsheetSourceKind.HtmlTable)
            return ReadHtmlTable(filePath, cancellationToken);

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".csv" or ".txt" or ".tsv"
            ? ReadDelimited(filePath, cancellationToken)
            : _openXml.Read(filePath, cancellationToken);
    }

    public static char DetectDelimiter(IReadOnlyList<string> lines)
    {
        var candidates = new[] { ',', '\t', ';', '|' };
        var sample = lines.Where(line => !string.IsNullOrWhiteSpace(line)).Take(25).ToArray();
        if (sample.Length == 0) return ',';

        return candidates
            .Select(delimiter => new
            {
                Delimiter = delimiter,
                Counts = sample.Select(line => CountUnquoted(line, delimiter)).ToArray()
            })
            .OrderByDescending(item => item.Counts.Count(count => count > 0))
            .ThenByDescending(item => item.Counts.Where(count => count > 0).DefaultIfEmpty(0).Average())
            .First().Delimiter;
    }

    public static IReadOnlyList<string> ParseDelimitedLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
                continue;
            }

            if (!quoted && character == delimiter)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(character);
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }

    public static string ReadTextWithEncodingFallback(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static OpenXmlWorkbookData ReadDelimited(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("The selected delimited source was not found.", filePath);
        var text = ReadTextWithEncodingFallback(filePath);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var delimiter = DetectDelimiter(lines);
        var rows = new Dictionary<int, IReadOnlyDictionary<int, string>>();
        var rowNumber = 0;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseDelimitedLine(line, delimiter);
            var cells = new Dictionary<int, string>();
            for (var index = 0; index < Math.Min(fields.Count, 180); index++)
                if (!string.IsNullOrWhiteSpace(fields[index])) cells[index + 1] = fields[index];
            if (cells.Count > 0) rows[rowNumber] = cells;
        }

        var sheet = new OpenXmlWorksheetData
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            State = "visible",
            Rows = rows,
            MaxRow = rowNumber
        };
        return new OpenXmlWorkbookData
        {
            FilePath = filePath,
            Uses1904DateSystem = false,
            Worksheets = new Dictionary<string, OpenXmlWorksheetData>(StringComparer.OrdinalIgnoreCase)
            {
                [sheet.Name] = sheet
            }
        };
    }


    private static OpenXmlWorkbookData ReadHtmlTable(string filePath, CancellationToken cancellationToken)
    {
        var text = ReadTextWithEncodingFallback(filePath);
        var rowMatches = System.Text.RegularExpressions.Regex.Matches(
            text, "<tr\\b[^>]*>(.*?)</tr>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var rows = new Dictionary<int, IReadOnlyDictionary<int, string>>();
        var rowNumber = 0;
        foreach (System.Text.RegularExpressions.Match rowMatch in rowMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cellMatches = System.Text.RegularExpressions.Regex.Matches(
                rowMatch.Groups[1].Value, "<t[dh]\\b[^>]*>(.*?)</t[dh]>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (cellMatches.Count == 0) continue;
            rowNumber++;
            var cells = new Dictionary<int, string>();
            for (var index = 0; index < Math.Min(cellMatches.Count, 180); index++)
            {
                var value = System.Text.RegularExpressions.Regex.Replace(
                    cellMatches[index].Groups[1].Value, "<[^>]+>", " ");
                value = System.Net.WebUtility.HtmlDecode(value).Replace('\u00A0', ' ').Trim();
                if (value.Length > 0) cells[index + 1] = value;
            }
            if (cells.Count > 0) rows[rowNumber] = cells;
        }

        if (rows.Count == 0)
            throw new InvalidDataException("HTML spreadsheet content was detected but no table rows could be read.");

        var sheetName = Path.GetFileNameWithoutExtension(filePath);
        var sheet = new OpenXmlWorksheetData
        {
            Name = sheetName,
            State = "visible",
            Rows = rows,
            MaxRow = rowNumber
        };
        return new OpenXmlWorkbookData
        {
            FilePath = filePath,
            Uses1904DateSystem = false,
            Worksheets = new Dictionary<string, OpenXmlWorksheetData>(StringComparer.OrdinalIgnoreCase)
            {
                [sheetName] = sheet
            }
        };
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
            else if (!quoted && line[index] == delimiter)
            {
                count++;
            }
        }
        return count;
    }
}
