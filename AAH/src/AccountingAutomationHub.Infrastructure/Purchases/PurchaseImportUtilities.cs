using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Infrastructure.Purchases;

internal static partial class PurchaseImportUtilities
{
    private static readonly decimal[] CommonGstRates =
    [
        0m,
        0.1m,
        0.25m,
        1m,
        1.5m,
        3m,
        5m,
        6m,
        7.5m,
        12m,
        18m,
        28m
    ];

    public static string NormalizeHeader(
        string value)
    {
        var builder = new StringBuilder();

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(
                    char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    public static string NormalizeGstin(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(
                value.Where(char.IsLetterOrDigit))
            .ToUpperInvariant();
    }

    public static bool IsValidGstin(
        string value) =>
        GstinRegex().IsMatch(
            NormalizeGstin(value));

    public static string NormalizeDocumentNumber(
        string value) =>
        string.Concat(
                value
                    .Trim()
                    .Where(character =>
                        !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    public static string NormalizeComparisonText(
        string value) =>
        string.Concat(
                value.Where(char.IsLetterOrDigit))
            .ToUpperInvariant();

    public static decimal ParseDecimal(
        string value)
    {
        var normalized = value
            .Trim()
            .Replace(
                ",",
                string.Empty,
                StringComparison.Ordinal);

        if (normalized.Length == 0 ||
            normalized == "-")
        {
            return 0m;
        }

        var isParenthesized =
            normalized.StartsWith(
                "(",
                StringComparison.Ordinal) &&
            normalized.EndsWith(
                ")",
                StringComparison.Ordinal);

        if (isParenthesized)
        {
            normalized = normalized[1..^1];
        }

        if (!decimal.TryParse(
                normalized,
                NumberStyles.Float |
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var result))
        {
            return 0m;
        }

        return isParenthesized
            ? -result
            : result;
    }

    public static bool TryParseDecimal(string? value, out decimal result)
    {
        result = 0m;
        var normalized = (value ?? string.Empty).Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        if (normalized.Length == 0 || normalized == "-") return false;
        var parenthesized = normalized.StartsWith("(", StringComparison.Ordinal) && normalized.EndsWith(")", StringComparison.Ordinal);
        if (parenthesized) normalized = normalized[1..^1];
        if (!decimal.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
            return false;
        if (parenthesized) result = -result;
        return true;
    }

    /// <summary>
    /// Purchase GST Date authority. When a return period is supplied (especially
    /// GSTR-2B), it is the strongest month evidence. If the document belongs to the
    /// same month we retain its actual date; otherwise we use the first day of the
    /// return/filing month, matching the established Purchase-period convention.
    /// </summary>
    public static DateTime CalculateWorkbookGstDate(DateTime documentDate, DateTime? filingDate, string? returnPeriod = null)
    {
        var document = documentDate.Date;
        var period = TryParseReturnPeriod(returnPeriod, out var periodDate)
            ? periodDate
            : filingDate?.Date;
        if (!period.HasValue) return document;
        return period.Value.Year == document.Year && period.Value.Month == document.Month
            ? document
            : new DateTime(period.Value.Year, period.Value.Month, 1);
    }

    /// <summary>
    /// GSTR-2B availability-date rule used by Purchase Auto Import.
    /// Monthly GSTR-1/1A documents filed through day 11 belong to the immediately
    /// preceding GSTR-2B month; filings from day 12 belong to the filing month.
    /// Quarterly/IFF suppliers use day 13 as the cut-off. After the GSTR-2B month
    /// is resolved, an invoice already dated inside that same month keeps its actual
    /// document date; only a cross-month document is normalized to day 1.
    /// </summary>
    public static DateTime CalculateGstr2BGstDate(
        DateTime documentDate,
        DateTime? filingDate,
        bool isQuarterlyFiling,
        string? fallbackReturnPeriod = null)
    {
        var document = documentDate.Date;
        DateTime periodMonth;

        if (filingDate.HasValue)
        {
            var filing = filingDate.Value.Date;
            var cutoffDay = isQuarterlyFiling
                ? 13
                : 11;

            periodMonth = new DateTime(
                filing.Year,
                filing.Month,
                1);

            if (filing.Day <= cutoffDay)
            {
                periodMonth = periodMonth.AddMonths(-1);
            }
        }
        else if (TryParseReturnPeriod(
                     fallbackReturnPeriod,
                     out var fallbackPeriod))
        {
            periodMonth = new DateTime(
                fallbackPeriod.Year,
                fallbackPeriod.Month,
                1);
        }
        else
        {
            return document;
        }

        return periodMonth.Year == document.Year &&
               periodMonth.Month == document.Month
            ? document
            : periodMonth;
    }

    public static bool TryParseReturnPeriod(string? value, out DateTime period)
    {
        period = default;
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        var compact = Regex.Replace(text.ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
        string[] numericFormats = ["MMyyyy", "yyyyMM", "MMyy", "yyMM"];
        foreach (var format in numericFormats)
        {
            if (DateTime.TryParseExact(compact, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                if (parsed.Year < 2000) parsed = parsed.AddYears(2000);
                period = new DateTime(parsed.Year, parsed.Month, 1);
                return true;
            }
        }

        string[] namedFormats = ["MMMyyyy", "MMMMyyyy", "yyyyMMM", "yyyyMMMM"];
        foreach (var format in namedFormats)
        {
            if (DateTime.TryParseExact(compact, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                period = new DateTime(parsed.Year, parsed.Month, 1);
                return true;
            }
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var general))
        {
            period = new DateTime(general.Year, general.Month, 1);
            return true;
        }

        return false;
    }

    public static DateTime? ParseExcelDate(
        string value,
        bool uses1904DateSystem)
    {
        var normalized = value.Trim();

        if (normalized.Length == 0 ||
            normalized == "-")
        {
            return null;
        }

        if (double.TryParse(
                normalized,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var serial))
        {
            try
            {
                return uses1904DateSystem
                    ? new DateTime(
                            1904,
                            1,
                            1)
                        .AddDays(serial)
                        .Date
                    : DateTime
                        .FromOADate(serial)
                        .Date;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        string[] formats =
        [
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "yyyy-MM-dd",
            "d-M-yyyy",
            "d/M/yyyy"
        ];

        if (DateTime.TryParseExact(
                normalized,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exactDate))
        {
            return exactDate.Date;
        }

        return DateTime.TryParse(
            normalized,
            CultureInfo.CurrentCulture,
            DateTimeStyles.None,
            out var parsedDate)
            ? parsedDate.Date
            : null;
    }

    public static bool TryDeriveSupportedRate(
        decimal taxableValue,
        decimal igst,
        decimal cgst,
        decimal sgst,
        out decimal rate)
    {
        rate = 0m;
        if (Math.Abs(taxableValue) <= 0.005m) return false;

        var totalTax = Math.Abs(igst) + Math.Abs(cgst) + Math.Abs(sgst);
        if (totalTax <= 0.005m) return false;

        var derived = Math.Abs(totalTax / taxableValue * 100m);
        var nearest = CommonGstRates.OrderBy(candidate => Math.Abs(candidate - derived)).First();
        if (Math.Abs(nearest - derived) > 0.15m) return false;

        var expectedTotalTax = Math.Abs(taxableValue) * nearest / 100m;
        // Fix54: GSTR-2A/2B invoice summaries can differ from rate * taxable by more
        // than five paise because the portal aggregates tax that was rounded at the
        // underlying line level. Keep the rate-difference proof above as the primary
        // guard and allow only a very small value-scaled rounding envelope here.
        // This repairs rows such as 287,135.74 taxable + 25,842.13 CGST/SGST,
        // which proves 18% but differs from a single invoice-level multiplication
        // by a few paise. Genuine 0% rows still cannot enter this path because they
        // carry no GST amount.
        var allowedTaxVariance = Math.Max(0.50m, Math.Abs(taxableValue) * 0.000005m);
        if (Math.Abs(expectedTotalTax - totalTax) > allowedTaxVariance) return false;

        var hasIgst = Math.Abs(igst) > 0.005m;
        var hasCgst = Math.Abs(cgst) > 0.005m;
        var hasSgst = Math.Abs(sgst) > 0.005m;
        if (hasIgst && (hasCgst || hasSgst)) return false;
        if (!hasIgst && hasCgst != hasSgst) return false;
        if (!hasIgst && hasCgst && Math.Abs(Math.Abs(cgst) - Math.Abs(sgst)) > 0.05m) return false;

        rate = nearest;
        return true;
    }

    public static decimal DeriveRate(
        decimal taxableValue,
        decimal igst,
        decimal cgst,
        decimal sgst)
    {
        if (taxableValue == 0m)
        {
            return 0m;
        }

        var derived = Math.Abs(
            (igst + cgst + sgst) /
            taxableValue *
            100m);

        var nearest = CommonGstRates
            .OrderBy(rate =>
                Math.Abs(rate - derived))
            .First();

        if (Math.Abs(nearest - derived) <= 0.15m)
        {
            return nearest;
        }

        return decimal.Round(
            derived,
            2,
            MidpointRounding.AwayFromZero);
    }

    public static string FormatRate(
        decimal rate) =>
        rate == decimal.Truncate(rate)
            ? decimal
                .Truncate(rate)
                .ToString(
                    "0",
                    CultureInfo.InvariantCulture)
            : rate.ToString(
                "0.##",
                CultureInfo.InvariantCulture);

    public static string ToDisplayName(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return CultureInfo
            .InvariantCulture
            .TextInfo
            .ToTitleCase(
                value
                    .Trim()
                    .ToLowerInvariant());
    }

    [GeneratedRegex(
        @"^[0-9]{2}[A-Z0-9]{13}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GstinRegex();
}
