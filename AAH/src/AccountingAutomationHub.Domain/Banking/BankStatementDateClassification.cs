using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Existing Bank Statement date and financial-year classification rules shared by
/// scanned Bank OCR and the normal workbook importer.  Source OCR must still pass
/// document verification before these dates can authorize any accounting import.
/// </summary>
public static class BankStatementDateClassification
{
    // Keep the bank importer's existing Excel serial, source date formats and
    // en-IN fallback semantics unchanged for ordinary bank workbooks.
    public static bool TryParseImportedDate(string? source, bool uses1904DateSystem, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(source)) return false;
        var text = source.Trim();
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) &&
            serial is > 0 and < 100000)
        {
            date = (uses1904DateSystem
                ? new DateTime(1904, 1, 1).AddDays(serial)
                : DateTime.FromOADate(serial)).Date;
            return true;
        }

        string[] formats =
        [
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
            "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd-MMM-yyyy", "d-MMM-yyyy",
            "MM/dd/yyyy", "dd/MM/yy", "d/M/yy", "dd-MM-yy", "d-M-yy",
            "dd.MM.yy", "d.M.yy"
        ];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces, out date) ||
               DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-IN"),
                   DateTimeStyles.AllowWhiteSpaces, out date);
    }

    /// <summary>
    /// Read a source-visible scanned statement posting/value date using the
    /// same bank-import date authority.  Unlike the Excel importer, OCR date
    /// cells cannot be Excel serials or free-form narration numbers.
    /// </summary>
    public static DateTime? ParseScannedDate(string? source)
    {
        var text = (source ?? string.Empty).Trim();
        // Source OCR sometimes reads a date separator ':' (14:08/26).
        // Only this narrow source-visible date shape may be normalized.
        if (Regex.IsMatch(text, @"^\d{1,2}:\d{1,2}[/.-]\d{2,4}$",
                RegexOptions.CultureInvariant))
            text = text.Replace(':', '/');

        if (!Regex.IsMatch(text,
                @"^(?:\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}|\d{4}-\d{1,2}-\d{1,2}|\d{1,2}-[A-Za-z]{3}-\d{4})$",
                RegexOptions.CultureInvariant))
            return null;
        return TryParseImportedDate(text, false, out var parsed)
            ? parsed.Date : null;
    }

    public static (DateTime? Start, DateTime? End) FinancialYearBounds(string financialYear)
    {
        var first = (financialYear ?? string.Empty)
            .Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length == 0 || !int.TryParse(first[0], out var year) ||
            year is < 1 or > 9998)
            return (null, null);
        return (new DateTime(year, 4, 1), new DateTime(year + 1, 3, 31));
    }

    public static bool IsInsideFinancialYear(DateTime date, string financialYear)
    {
        var (start, end) = FinancialYearBounds(financialYear);
        return !start.HasValue || !end.HasValue ||
               date.Date >= start.Value && date.Date <= end.Value;
    }
}
