using System.Globalization;
using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Domain.Common;

/// <summary>
/// Shared flexible matching helpers for Banking, Grid, and Ledger Mapping filters.
/// Accepts alternate date separators ('.', '-', '/'), unpadded day/month ('5-11-25' == '05.11.2025'),
/// and unformatted/Indian-grouped numeric amounts ('1000000' == '1,000,000.00').
/// </summary>
public static partial class FlexibleFilterMatcher
{
    private static readonly string[] FullDateFormats =
    [
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy",
        "dd-MM-yyyy", "d-M-yyyy", "dd-MM-yy", "d-M-yy",
        "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy",
        "yyyy-MM-dd", "yyyy.MM.dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy.M.d", "yyyy/M/d",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yy", "d-MMM-yy",
        "dd.MMM.yyyy", "d.MMM.yyyy", "dd.MMM.yy", "d.MMM.yy",
        "dd/MMM/yyyy", "d/MMM/yyyy", "dd/MMM/yy", "d/MMM/yy",
        "dd MMM yyyy", "d MMM yyyy", "dd MMM yy", "d MMM yy"
    ];

    private static CultureInfo CreateTwoDigitYearCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.Calendar.TwoDigitYearMax = 2099;
        return culture;
    }

    private static readonly CultureInfo TwoDigitYearCulture = CreateTwoDigitYearCulture();

    public static bool TryParseFlexibleDate(string? text, out DateTime date)
    {
        date = default;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length < 4 || !trimmed.Any(char.IsDigit)) return false;

        var normalized = MultiSpaceRegex().Replace(trimmed, " ");
        return DateTime.TryParseExact(
            normalized,
            FullDateFormats,
            TwoDigitYearCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out date);
    }

    public static bool MatchesDate(DateTime actualDate, string? rawQuery, bool exactOnly = false)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        if (query.Length == 0) return true;

        if (TryParseFlexibleDate(query, out var parsedQueryDate))
            return actualDate.Date == parsedQueryDate.Date;

        if (exactOnly) return false;

        var normalizedSeparators = query.Replace('.', '-').Replace('/', '-');
        if (normalizedSeparators.Contains('-', StringComparison.Ordinal))
        {
            var rawSegments = normalizedSeparators.Split('-');
            if (rawSegments.Length is >= 2 and <= 3 &&
                rawSegments.Take(rawSegments.Length - 1).All(seg => seg.Length > 0 && seg.All(char.IsDigit)) &&
                rawSegments[^1].All(char.IsDigit))
            {
                return MatchesPartialNumericDateSegments(actualDate, rawSegments);
            }

            // Textual partial month/day such as "5 Nov" / "5-Nov" / "Nov-25"
            var normalizedTokens = MultiSpaceRegex().Replace(query.Replace('-', ' ').Replace('.', ' ').Replace('/', ' '), " ").Trim();
            if (normalizedTokens.Length > 0)
            {
                var unpaddedText = actualDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
                var paddedText = actualDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
                if (unpaddedText.Contains(normalizedTokens, StringComparison.OrdinalIgnoreCase) ||
                    paddedText.Contains(normalizedTokens, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Plain digits without separators: only match month/year/exact formatted substrings
        // so typing "2025" matches year 2025 cleanly.
        if (query.All(char.IsDigit))
        {
            if (query.Length == 4 && int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
                return actualDate.Year == year;
        }

        return actualDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) ||
               actualDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) ||
               actualDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) ||
               actualDate.ToString("d.M.yyyy", CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) ||
               actualDate.ToString("d-M-yy", CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPartialNumericDateSegments(DateTime actualDate, string[] segments)
    {
        if (!int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first))
            return false;

        if (segments.Length == 2)
        {
            if (segments[1].Length == 0)
            {
                // "5-" or "05." -> day matches exactly (5 != 15), or yyyy- prefix
                if (segments[0].Length == 4) return actualDate.Year == first;
                return actualDate.Day == first;
            }

            if (!int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var second))
                return false;

            if (segments[0].Length == 4)
                return actualDate.Year == first && YearOrMonthSegmentMatches(actualDate.Month, segments[1]);

            if (segments[1].Length == 4)
                return actualDate.Month == first && actualDate.Year == second;

            // "5-11" -> 5th November (any year), or "11-25" -> Nov 2025
            if (actualDate.Day == first && actualDate.Month == second)
                return true;

            if (first is >= 1 and <= 12 && segments[1].Length == 2 && actualDate.Month == first && (actualDate.Year % 100) == second)
                return true;

            return false;
        }

        if (!int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var middle))
            return false;

        if (segments[0].Length == 4)
            return actualDate.Year == first && actualDate.Month == middle &&
                   (segments[2].Length == 0 || YearOrMonthSegmentMatches(actualDate.Day, segments[2]));

        if (actualDate.Day != first || actualDate.Month != middle)
            return false;

        if (segments[2].Length == 0) return true;

        var yearFull = actualDate.Year.ToString("0000", CultureInfo.InvariantCulture);
        var yearShort = (actualDate.Year % 100).ToString("00", CultureInfo.InvariantCulture);
        return yearFull.StartsWith(segments[2], StringComparison.Ordinal) ||
               yearShort.StartsWith(segments[2], StringComparison.Ordinal);
    }

    private static bool YearOrMonthSegmentMatches(int actual, string prefixDigits) =>
        prefixDigits.Length == 0 ||
        actual.ToString(CultureInfo.InvariantCulture).StartsWith(prefixDigits, StringComparison.Ordinal) ||
        actual.ToString("00", CultureInfo.InvariantCulture).StartsWith(prefixDigits, StringComparison.Ordinal);

    public static bool TryExtractAmountQuery(string? rawQuery, out string normalizedQuery, out decimal? numericValue)
    {
        normalizedQuery = string.Empty;
        numericValue = null;
        var text = (rawQuery ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        var cleaned = AmountWrapperRegex().Replace(text, string.Empty)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (cleaned.Length == 0 || !cleaned.Any(char.IsDigit)) return false;
        if (!PureDecimalFragmentRegex().IsMatch(cleaned)) return false;

        normalizedQuery = cleaned;
        if (decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            numericValue = parsed;
        return true;
    }

    public static bool TryParseAmount(string? rawQuery, out decimal amount)
    {
        amount = 0m;
        return TryExtractAmountQuery(rawQuery, out _, out var parsed) && parsed is decimal value && (amount = value) == value;
    }

    public static bool MatchesAmount(decimal actualAmount, string? rawQuery, bool exactOnly = false)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        if (query.Length == 0) return true;
        if (!TryExtractAmountQuery(query, out var normalizedQuery, out var parsedQuery))
            return false;

        var hasSign = normalizedQuery.StartsWith('-') || normalizedQuery.StartsWith('+');
        if (parsedQuery.HasValue)
        {
            if (hasSign ? actualAmount == parsedQuery.Value : Math.Abs(actualAmount) == Math.Abs(parsedQuery.Value))
                return true;
        }

        if (exactOnly) return false;

        var invariantTrimmed = actualAmount.ToString("0.##", CultureInfo.InvariantCulture);
        var invariantFixed = actualAmount.ToString("0.00", CultureInfo.InvariantCulture);
        if (invariantTrimmed.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            invariantFixed.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!hasSign && actualAmount < 0m)
        {
            var abs = Math.Abs(actualAmount);
            return abs.ToString("0.##", CultureInfo.InvariantCulture).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                   abs.ToString("0.00", CultureInfo.InvariantCulture).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static bool MatchesAmountText(string? candidateText, string? rawQuery, bool exactOnly = false)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        if (query.Length == 0) return true;
        if (string.IsNullOrWhiteSpace(candidateText)) return false;

        if (TryExtractAmountQuery(candidateText, out var candidateClean, out var candidateNumeric))
        {
            if (candidateNumeric.HasValue)
                return MatchesAmount(candidateNumeric.Value, query, exactOnly);

            if (TryExtractAmountQuery(query, out var normalizedQuery, out _))
            {
                return exactOnly
                    ? string.Equals(candidateClean, normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    : candidateClean.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"([₹$€£]|\b(?:INR|RS\.?|DR|CR)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmountWrapperRegex();

    [GeneratedRegex(@"^[+-]?(?:\d+\.?\d*|\.\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PureDecimalFragmentRegex();
}
