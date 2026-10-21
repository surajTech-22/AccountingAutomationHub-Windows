using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Fix119 official GSTN HSN/SAC directory intake. The GST Portal exposes the full
/// directory through Search HSN/SAC > Download HSN Directory in Excel Format.
/// AAH keeps a local searchable copy and never invents codes/descriptions.
/// </summary>
public sealed class GstHsnSacDirectoryService
{
    public const string OfficialSearchUrl = "https://services.gst.gov.in/services/searchhsnsac";
    public const string OfficialDirectoryUrl = "https://tutorial.gst.gov.in/downloads/HSN_SAC.xlsx";
    private readonly FlexibleWorkbookReader _reader = new();

    public async Task<IReadOnlyList<GstHsnSacDirectoryRecord>> DownloadOfficialAsync(CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"AAH_GSTN_HSN_SAC_{Guid.NewGuid():N}.xlsx");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AAH-GST-HSN-SAC/1.0");
            using var response = await client.GetAsync(OfficialDirectoryUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(temp))
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return ReadDirectory(temp, "GSTN official HSN_SAC.xlsx");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public IReadOnlyList<GstHsnSacDirectoryRecord> ReadDirectory(string filePath, string source = "GSTN HSN/SAC directory")
    {
        var workbook = _reader.Read(filePath, CancellationToken.None);
        var rows = new List<GstHsnSacDirectoryRecord>();
        ReadKind(workbook, "HSN", rows, source);
        ReadKind(workbook, "SAC", rows, source);
        if (rows.Count == 0)
            throw new InvalidDataException("No HSN/SAC rows were found. Select the official GSTN HSN_SAC.xlsx directory.");
        return rows
            .GroupBy(row => row.Kind + "\u001f" + row.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(row => row.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ReadKind(OpenXmlWorkbookData workbook, string kind, List<GstHsnSacDirectoryRecord> target, string source)
    {
        var preferredName = kind + "_MSTR";
        var sheet = workbook.Worksheets.Values.FirstOrDefault(value =>
            value.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase))
            ?? workbook.Worksheets.Values.FirstOrDefault(value =>
                value.Name.Contains(kind, StringComparison.OrdinalIgnoreCase));
        if (sheet is null) return;

        var headerRow = sheet.Rows
            .OrderBy(pair => pair.Key)
            .FirstOrDefault(pair =>
                pair.Value.Values.Any(value =>
                    value.Contains(kind + " Code", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals(kind, StringComparison.OrdinalIgnoreCase)));
        var startRow = headerRow.Key > 0 ? headerRow.Key + 1 : 2;

        for (var rowNumber = startRow; rowNumber <= sheet.MaxRow; rowNumber++)
        {
            var code = CleanCode(sheet.GetCell(rowNumber, 1));
            var description = CleanText(sheet.GetCell(rowNumber, 2));
            if (code.Length == 0 || description.Length == 0) continue;
            target.Add(new GstHsnSacDirectoryRecord
            {
                Code = code,
                Description = description,
                Kind = kind,
                Source = source,
                UpdatedUtc = DateTime.UtcNow
            });
        }
    }

    private static string CleanCode(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).Trim().ToUpperInvariant();

    private static string CleanText(string? value) =>
        string.Join(" ", (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
}
