using System.Globalization;
using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Infrastructure.Sales;

/// <summary>
/// Formats existing automatic Sales source evidence for the UI. This class does
/// not discover, repair, parse, import, delete, or replace any source or voucher.
/// </summary>
public static class SalesAutoImportDiagnosticFormatter
{
    public static string Build(
        SalesAutoImportResult result,
        DateTime? requestedMonth = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string>();
        var verification = result.Verification;
        var selectedPeriods = verification.SelectedPeriods
            .Select(NormalizePeriod)
            .Where(period => period.Length == 6)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(period => period)
            .ToArray();

        var selectedMonth = requestedMonth?.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var isMissingRequestedMonth = selectedMonth is not null &&
            !selectedPeriods.Contains(selectedMonth, StringComparer.OrdinalIgnoreCase);

        if (isMissingRequestedMonth || selectedPeriods.Length == 0)
        {
            var subject = requestedMonth.HasValue
                ? requestedMonth.Value.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
                : $"FY {result.FinancialYear}";
            lines.Add($"No verified GSTR-1 source selected for {subject}, GSTIN {result.ClientGstin}.");
            lines.Add("Verified periods in this attempt: " +
                      (selectedPeriods.Length == 0
                        ? "none"
                        : string.Join(", ", selectedPeriods.Select(ReadablePeriod))) + ".");
            lines.Add("Source selection: " +
                      (string.IsNullOrWhiteSpace(verification.Status) ? "not reported" : verification.Status) +
                      "; origin: " +
                      (string.IsNullOrWhiteSpace(verification.SourceOrigin) ? "not reported" : verification.SourceOrigin) +
                      "; branch: " +
                      (string.IsNullOrWhiteSpace(verification.SourceBranch) ? "not reported" : verification.SourceBranch) + ".");

            var selectedFiles = verification.SelectedSources
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(Path.GetFileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
            if (selectedFiles.Length > 0)
                lines.Add("Selected source files: " + string.Join(", ", selectedFiles) + ".");

            lines.Add("No replacement was authorized for the missing month. " +
                      "This message identifies what AAH selected, not whether a file exists elsewhere.");
        }

        foreach (var warning in result.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)))
            lines.Add(warning.Trim());

        // The importer already records parser and source-search exceptions as
        // SalesImportIssue. Make them visible without modifying its behavior.
        var sourceIssues = result.Issues
            .Where(issue =>
                issue.Section.Equals("JSON/ZIP", StringComparison.OrdinalIgnoreCase) ||
                issue.Section.Equals("ZIP JSON", StringComparison.OrdinalIgnoreCase) ||
                issue.Section.Equals("Search", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var issue in sourceIssues.Take(8))
        {
            var file = string.IsNullOrWhiteSpace(issue.SourceFile) ? "unknown source" : issue.SourceFile;
            lines.Add($"Source issue [{issue.Section}] {file}: {issue.Message}");
        }
        if (sourceIssues.Length > 8)
            lines.Add($"{sourceIssues.Length - 8} additional source issue(s) are listed in the Issues tab.");

        return string.Join(Environment.NewLine,
            lines.Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizePeriod(string? value)
    {
        var digits = string.Concat((value ?? string.Empty).Where(char.IsDigit));
        if (digits.Length != 6) return string.Empty;

        if (int.TryParse(digits[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
            int.TryParse(digits[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
            year is >= 2000 and <= 2200 && month is >= 1 and <= 12)
            return $"{year:0000}{month:00}";

        if (int.TryParse(digits[..2], NumberStyles.None, CultureInfo.InvariantCulture, out month) &&
            int.TryParse(digits[2..], NumberStyles.None, CultureInfo.InvariantCulture, out year) &&
            year is >= 2000 and <= 2200 && month is >= 1 and <= 12)
            return $"{year:0000}{month:00}";

        return string.Empty;
    }

    private static string ReadablePeriod(string period)
    {
        if (!DateTime.TryParseExact(period, "yyyyMM", CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var date))
            return period;
        return date.ToString("MMM yyyy", CultureInfo.InvariantCulture);
    }
}
