using System.Globalization;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Infrastructure.Imports;

public sealed record ImportHeaderColumnInspection(
    int ColumnIndex,
    string ColumnLabel,
    string SourceHeader,
    string CanonicalField,
    string CanonicalDisplay,
    string MatchType,
    bool IsMatched);

public sealed record ImportHeaderInspection(
    string SourcePath,
    string Module,
    string SheetName,
    int HeaderRow,
    bool HeaderWasRecognized,
    IReadOnlyList<ImportHeaderColumnInspection> Columns,
    IReadOnlyList<string> MissingRequired,
    string Detail)
{
    public bool RequiredComplete => MissingRequired.Count == 0;
}

public sealed class ImportHeaderInspectionService
{
    private readonly FlexibleWorkbookReader _reader = new();

    public ImportHeaderInspection Inspect(string sourcePath, string module)
    {
        module = ImportHeaderMappingCatalog.NormalizeModule(module);
        if (!ImportHeaderMappingCatalog.Modules.Contains(module, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a supported accounting module before mapping headers.");
        using var scope = ImportHeaderMappingRuntime.BeginSource(sourcePath);
        var workbook = _reader.Read(sourcePath, CancellationToken.None);
        var best = workbook.Worksheets.Values
            .Select(sheet => FindHeaderCandidate(sheet, module))
            .OrderByDescending(item => item.MatchedCount)
            .ThenByDescending(item => item.HeaderLikelihood)
            .ThenBy(item => item.RowNumber)
            .FirstOrDefault();
        if (best is null || best.Cells.Count == 0)
            return new(sourcePath, module, string.Empty, 0, false, Array.Empty<ImportHeaderColumnInspection>(),
                ImportHeaderMappingCatalog.GetMissingRequiredDescriptions(module, Array.Empty<string>()), "No readable columns were found.");

        var columns = new List<ImportHeaderColumnInspection>();
        foreach (var pair in best.Cells.OrderBy(pair => pair.Key))
        {
            var sourceHeader = pair.Value?.Trim() ?? string.Empty;
            var resolution = ImportHeaderMappingRuntime.ResolveDetailed(module, sourceHeader);
            var field = resolution.IsMatched ? ImportHeaderMappingCatalog.FindField(module, resolution.CanonicalField) : null;
            columns.Add(new ImportHeaderColumnInspection(
                pair.Key,
                ColumnName(pair.Key),
                sourceHeader.Length > 0 ? sourceHeader : ColumnName(pair.Key),
                resolution.CanonicalField,
                field?.DisplayName ?? string.Empty,
                resolution.MatchType,
                resolution.IsMatched));
        }
        var canonical = columns.Where(item => item.IsMatched).Select(item => item.CanonicalField).ToArray();
        var missing = ImportHeaderMappingCatalog.GetMissingRequiredDescriptions(module, canonical);
        var recognized = best.MatchedCount > 0;
        var detail = recognized
            ? $"Detected header row {best.RowNumber:N0} in '{best.SheetName}' with {columns.Count(item => item.IsMatched):N0} recognized column(s)."
            : $"No known {module} header was found. AAH exposed the most likely source-column row ({best.RowNumber:N0}) so the columns can be assigned for this import only.";
        return new(sourcePath, module, best.SheetName, best.RowNumber, recognized, columns, missing, detail);
    }

    private static HeaderCandidate FindHeaderCandidate(OpenXmlWorksheetData sheet, string module)
    {
        HeaderCandidate? bestRecognized = null;
        HeaderCandidate? bestRaw = null;
        foreach (var pair in sheet.Rows.OrderBy(pair => pair.Key).Take(80))
        {
            var cells = pair.Value.Where(cell => !string.IsNullOrWhiteSpace(cell.Value)).ToDictionary(cell => cell.Key, cell => cell.Value);
            if (cells.Count < 2) continue;
            var matched = cells.Values.Count(value => ImportHeaderMappingRuntime.Resolve(module, value).Length > 0);
            var likelihood = HeaderLikelihood(cells.Values);
            var candidate = new HeaderCandidate(sheet.Name, pair.Key, cells, matched, likelihood);
            if (matched > 0 && (bestRecognized is null || matched > bestRecognized.MatchedCount || matched == bestRecognized.MatchedCount && likelihood > bestRecognized.HeaderLikelihood)) bestRecognized = candidate;
            if (bestRaw is null || likelihood > bestRaw.HeaderLikelihood || likelihood == bestRaw.HeaderLikelihood && pair.Key < bestRaw.RowNumber) bestRaw = candidate;
        }
        return bestRecognized ?? bestRaw ?? new HeaderCandidate(sheet.Name, 0, new Dictionary<int, string>(), 0, 0);
    }

    private static int HeaderLikelihood(IEnumerable<string> values)
    {
        var score = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values.Take(80))
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            if (seen.Add(ImportHeaderMappingCatalog.NormalizeHeader(value))) score += 2;
            var hasLetters = value.Any(char.IsLetter);
            if (hasLetters) score += 3;
            if (value.Length is >= 2 and <= 60) score += 1;
            if (decimal.TryParse(value.Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out _)) score -= 3;
            if (DateTime.TryParse(value, out _)) score -= 2;
        }
        return score;
    }

    private static string ColumnName(int column)
    {
        if (column <= 0) return "Column";
        var value = column;
        var name = string.Empty;
        while (value > 0)
        {
            value--;
            name = (char)('A' + value % 26) + name;
            value /= 26;
        }
        return $"Column {name}";
    }

    private sealed record HeaderCandidate(string SheetName, int RowNumber, IReadOnlyDictionary<int, string> Cells, int MatchedCount, int HeaderLikelihood);
}
