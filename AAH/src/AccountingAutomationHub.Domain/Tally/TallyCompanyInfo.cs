using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyCompanyInfo
{
    public string CompanyKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CompanyNumber { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public string BooksFrom { get; set; } = string.Empty;
    public string StartingFrom { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int GroupCount { get; set; }
    public int LedgerCount { get; set; }
    public int VoucherTypeCount { get; set; }
    public int StockItemCount { get; set; }
    public int UnitCount { get; set; }
    public int GodownCount { get; set; }
    public int VoucherCount { get; set; }
    public string ChangeToken { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public long LinkedClientId { get; set; }
    public string LinkedClientCode { get; set; } = string.Empty;
    public string LinkedClientName { get; set; } = string.Empty;

    public bool IsLinked => LinkedClientId > 0;

    public string DisplayText
    {
        get
        {
            var identity = Gstin.Length > 0
                ? Gstin
                : FinancialYear.Length > 0
                    ? $"FY {FinancialYear}"
                    : string.Empty;

            var linkedName = LinkedClientName.Trim().Length > 0
                ? LinkedClientName.Trim()
                : LinkedClientCode.Trim();
            var link = IsLinked && linkedName.Length > 0
                ? $" • Linked to {linkedName}"
                : string.Empty;

            return identity.Length > 0
                ? $"{Name} • {identity}{link}"
                : $"{Name}{link}";
        }
    }

    public static string BuildCompanyKey(
        string name,
        string companyNumber,
        string gstin,
        string financialYear,
        string booksFrom)
    {
        var normalizedGstin = NormalizeGstin(gstin);
        var normalizedYear = NormalizeFinancialYear(financialYear, booksFrom);
        var normalizedNumber = NormalizeToken(companyNumber);
        var normalizedName = NormalizeToken(name);
        var normalizedBooks = NormalizeToken(booksFrom);

        var companyDiscriminator = normalizedNumber.Length > 0
            ? $"NO|{normalizedNumber}"
            : $"NAME|{normalizedName}";

        var raw = normalizedGstin.Length > 0
            ? $"GSTIN|{normalizedGstin}|FY|{normalizedYear}|{companyDiscriminator}"
            : $"NAME|{normalizedName}|NO|{normalizedNumber}|FY|{normalizedYear}|BOOKS|{normalizedBooks}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    public static string NormalizeGstin(string value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    public static string NormalizeFinancialYear(
        string value,
        string fallbackDate = "")
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());

        if (digits.Length >= 6)
        {
            var startText = digits[..4];
            var endText = digits[^2..];
            if (int.TryParse(startText, out var start))
            {
                return $"{start:0000}-{endText}";
            }
        }

        var date = ParseTallyDate(fallbackDate);
        if (date is null)
        {
            return (value ?? string.Empty).Trim();
        }

        var startYear = date.Value.Month >= 4
            ? date.Value.Year
            : date.Value.Year - 1;

        return $"{startYear:0000}-{(startYear + 1) % 100:00}";
    }

    public static DateTime? ParseTallyDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var formats = new[]
        {
            "yyyyMMdd",
            "dd-MMM-yyyy",
            "d-MMM-yyyy",
            "dd/MM/yyyy",
            "d/M/yyyy",
            "yyyy-MM-dd",
            "M/d/yyyy",
            "MM/dd/yyyy"
        };

        if (DateTime.TryParseExact(
                text,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var exact))
        {
            return exact;
        }

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeToken(string value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
}
