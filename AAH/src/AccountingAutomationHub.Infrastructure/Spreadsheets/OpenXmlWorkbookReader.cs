using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Spreadsheets;

public sealed class OpenXmlWorkbookReader
{
    private static readonly XNamespace SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly XNamespace OfficeRelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly XNamespace PackageRelationshipNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    public OpenXmlWorkbookData Read(
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                "The selected workbook was not found.",
                filePath);
        }

        using var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var archive = new ZipArchive(
            fileStream,
            ZipArchiveMode.Read,
            leaveOpen: false);

        var sharedStrings = ReadSharedStrings(archive);
        var workbookDocument = LoadXmlPart(
            archive,
            "xl/workbook.xml");

        var relationshipDocument = LoadXmlPart(
            archive,
            "xl/_rels/workbook.xml.rels");

        var relationshipTargets = relationshipDocument
            .Root?
            .Elements(PackageRelationshipNamespace + "Relationship")
            .Where(element =>
                !string.IsNullOrWhiteSpace(
                    (string?)element.Attribute("Id")))
            .ToDictionary(
                element => (string)element.Attribute("Id")!,
                element => (string?)element.Attribute("Target") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var uses1904DateSystem = ParseBoolean(
            (string?)workbookDocument
                .Root?
                .Element(SpreadsheetNamespace + "workbookPr")
                ?.Attribute("date1904"));

        var worksheets = new Dictionary<string, OpenXmlWorksheetData>(
            StringComparer.OrdinalIgnoreCase);

        var sheetElements = workbookDocument
            .Root?
            .Element(SpreadsheetNamespace + "sheets")
            ?.Elements(SpreadsheetNamespace + "sheet")
            ?? Enumerable.Empty<XElement>();

        foreach (var sheetElement in sheetElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheetName =
                (string?)sheetElement.Attribute("name")
                ?? string.Empty;

            var relationshipId =
                (string?)sheetElement.Attribute(
                    OfficeRelationshipNamespace + "id")
                ?? string.Empty;

            if (sheetName.Length == 0 ||
                relationshipId.Length == 0 ||
                !relationshipTargets.TryGetValue(
                    relationshipId,
                    out var target))
            {
                continue;
            }

            var partPath = ResolveWorkbookTarget(target);
            var state =
                (string?)sheetElement.Attribute("state")
                ?? "visible";

            worksheets[sheetName] = ReadWorksheet(
                archive,
                partPath,
                sheetName,
                state,
                sharedStrings,
                cancellationToken);
        }

        return new OpenXmlWorkbookData
        {
            FilePath = filePath,
            Uses1904DateSystem = uses1904DateSystem,
            Worksheets = worksheets
        };
    }

    private static IReadOnlyList<string> ReadSharedStrings(
        ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");

        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(
            stream,
            LoadOptions.None);

        return document
            .Descendants(SpreadsheetNamespace + "si")
            .Select(item =>
                string.Concat(
                    item
                        .Descendants(SpreadsheetNamespace + "t")
                        .Select(text => text.Value)))
            .ToArray();
    }

    private static OpenXmlWorksheetData ReadWorksheet(
        ZipArchive archive,
        string partPath,
        string sheetName,
        string state,
        IReadOnlyList<string> sharedStrings,
        CancellationToken cancellationToken)
    {
        var document = LoadXmlPart(
            archive,
            partPath);

        var rows =
            new Dictionary<int, IReadOnlyDictionary<int, string>>();

        var maxRow = 0;

        var rowElements = document
            .Root?
            .Element(SpreadsheetNamespace + "sheetData")
            ?.Elements(SpreadsheetNamespace + "row")
            ?? Enumerable.Empty<XElement>();

        foreach (var rowElement in rowElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!int.TryParse(
                    (string?)rowElement.Attribute("r"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var rowNumber))
            {
                continue;
            }

            maxRow = Math.Max(
                maxRow,
                rowNumber);

            var cells = new Dictionary<int, string>();

            foreach (var cellElement in
                     rowElement.Elements(
                         SpreadsheetNamespace + "c"))
            {
                var reference =
                    (string?)cellElement.Attribute("r")
                    ?? string.Empty;

                var columnIndex =
                    ParseColumnIndex(reference);

                if (columnIndex <= 0 ||
                    columnIndex > 180)
                {
                    continue;
                }

                var value = ReadCellValue(
                    cellElement,
                    sharedStrings);

                if (value.Length > 0)
                {
                    cells[columnIndex] = value;
                }
            }

            if (cells.Count > 0)
            {
                rows[rowNumber] = cells;
            }
        }

        return new OpenXmlWorksheetData
        {
            Name = sheetName,
            State = state,
            Rows = rows,
            MaxRow = maxRow
        };
    }

    private static string ReadCellValue(
        XElement cellElement,
        IReadOnlyList<string> sharedStrings)
    {
        var type =
            (string?)cellElement.Attribute("t")
            ?? string.Empty;

        if (string.Equals(
                type,
                "inlineStr",
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                cellElement
                    .Descendants(SpreadsheetNamespace + "t")
                    .Select(text => text.Value));
        }

        var rawValue =
            cellElement
                .Element(SpreadsheetNamespace + "v")
                ?.Value
            ?? string.Empty;

        if (rawValue.Length == 0)
        {
            return string.Empty;
        }

        if (string.Equals(
                type,
                "s",
                StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                rawValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var sharedStringIndex) &&
            sharedStringIndex >= 0 &&
            sharedStringIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedStringIndex];
        }

        if (string.Equals(
                type,
                "b",
                StringComparison.OrdinalIgnoreCase))
        {
            return rawValue == "1"
                ? "TRUE"
                : "FALSE";
        }

        return rawValue;
    }

    private static XDocument LoadXmlPart(
        ZipArchive archive,
        string partPath)
    {
        var entry = archive.GetEntry(
            partPath.Replace('\\', '/'));

        if (entry is null)
        {
            throw new InvalidDataException(
                $"Required workbook part '{partPath}' was not found.");
        }

        using var stream = entry.Open();

        return XDocument.Load(
            stream,
            LoadOptions.None);
    }

    private static string ResolveWorkbookTarget(
        string target)
    {
        var normalized = target
            .Replace('\\', '/')
            .TrimStart('/');

        return normalized.StartsWith(
            "xl/",
            StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"xl/{normalized}";
    }

    private static int ParseColumnIndex(
        string cellReference)
    {
        var result = 0;

        foreach (var character in cellReference)
        {
            if (character is < 'A' or > 'Z')
            {
                break;
            }

            result =
                (result * 26) +
                (character - 'A' + 1);
        }

        return result;
    }

    private static bool ParseBoolean(
        string? value) =>
        value is "1" ||
        string.Equals(
            value,
            "true",
            StringComparison.OrdinalIgnoreCase);
}
