namespace AccountingAutomationHub.Infrastructure.Spreadsheets;

public sealed class OpenXmlWorkbookData
{
    public required string FilePath { get; init; }
    public bool Uses1904DateSystem { get; init; }
    public required IReadOnlyDictionary<string, OpenXmlWorksheetData> Worksheets { get; init; }

    public bool TryGetWorksheet(
        string name,
        out OpenXmlWorksheetData worksheet) =>
        Worksheets.TryGetValue(name, out worksheet!);
}

public sealed class OpenXmlWorksheetData
{
    public required string Name { get; init; }
    public required string State { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> Rows { get; init; }
    public int MaxRow { get; init; }

    public string GetCell(
        int row,
        int column)
    {
        if (!Rows.TryGetValue(row, out var values))
        {
            return string.Empty;
        }

        return values.TryGetValue(column, out var value)
            ? value
            : string.Empty;
    }
}
