using System.Globalization;
using System.Text;

namespace AccountingAutomationHub.Domain.Common;

/// <summary>
/// Shared text rules for accounting names. DuplicateKey mirrors the current Excel
/// ThisWorkbook CleanLedgerText routine: trim, normalize whitespace/NBSP, remove '?', uppercase.
/// DisplayName intentionally runs only at commit/display boundaries so typing is never rewritten.
/// </summary>
public static class AccountingText
{
    private static readonly HashSet<string> UppercaseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "GST", "GSTIN", "CGST", "SGST", "IGST", "UTGST", "HSN", "SAC", "UQC", "IFSC",
        "UPI", "IMPS", "NEFT", "RTGS", "IFT", "ATM", "OD", "CC", "CA", "SB", "RCM", "ITC",
        "TDS", "TCS", "PAN", "CIN", "LLP", "SBI", "HDFC", "ICICI", "IDFC", "AXIS", "KVB",
        "KBL", "BOB", "PNB", "YES", "RBL", "DBS", "DCB", "UCO", "IOB", "BOI", "HSBC",
        "POS", "ECS", "NACH", "ACH", "VPA", "UTR", "RRN", "MICR", "QR", "B2B", "B2C",
        "B2CS", "B2CL", "SEZ", "LUT", "IGST", "CGST", "SGST"
    };

    private static readonly HashSet<string> ConventionalTitleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "PVT", "LTD", "LIMITED", "PRIVATE", "CO", "COMPANY", "AND", "OF", "THE", "FOR", "IN", "ON", "AT", "BY"
    };

    public static string CleanWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Replace('\u00A0', ' ').Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        var builder = new StringBuilder(text.Length);
        var previousSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousSpace) builder.Append(' ');
                previousSpace = true;
            }
            else
            {
                builder.Append(c);
                previousSpace = false;
            }
        }
        return builder.ToString().Trim();
    }

    /// <summary>Exact duplicate identity used by the workbook's CleanLedgerText.</summary>
    public static string DuplicateKey(string? value) =>
        CleanWhitespace(value).Replace("?", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    public static string DisplayName(string? value)
    {
        var clean = CleanWhitespace(value);
        if (clean.Length == 0) return string.Empty;

        var lower = clean.ToLowerInvariant();
        var title = CultureInfo.GetCultureInfo("en-IN").TextInfo.ToTitleCase(lower);
        var tokens = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length; index++)
        {
            tokens[index] = FormatToken(tokens[index], index);
        }
        return string.Join(' ', tokens);
    }

    public static string UnitName(string? value)
    {
        var clean = CleanWhitespace(value);
        if (clean.Length == 0) return string.Empty;
        // Accounting units are normally abbreviations. Preserve punctuation but uppercase short units.
        return clean.Length <= 8 && !clean.Contains(' ') ? clean.ToUpperInvariant() : DisplayName(clean);
    }

    private static string FormatToken(string token, int index)
    {
        if (token.Length == 0) return token;
        var stripped = token.Trim('.', ',', ':', ';', '(', ')', '[', ']', '{', '}');
        var prefixLength = token.IndexOf(stripped, StringComparison.Ordinal);
        var prefix = prefixLength > 0 ? token[..prefixLength] : string.Empty;
        var suffixStart = prefixLength + stripped.Length;
        var suffix = suffixStart < token.Length ? token[suffixStart..] : string.Empty;

        if (UppercaseTokens.Contains(stripped)) return prefix + stripped.ToUpperInvariant() + suffix;
        if (stripped.Any(char.IsDigit) && stripped.Any(char.IsLetter))
        {
            // Product/entity identifiers such as A1, 3M, B2B should not be lower-cased.
            return prefix + stripped.ToUpperInvariant() + suffix;
        }
        if (ConventionalTitleTokens.Contains(stripped))
        {
            if (stripped.Equals("PVT", StringComparison.OrdinalIgnoreCase)) return prefix + "Pvt" + suffix;
            if (stripped.Equals("LTD", StringComparison.OrdinalIgnoreCase)) return prefix + "Ltd" + suffix;
            if (index > 0 && stripped is "And" or "Of" or "The" or "For" or "In" or "On" or "At" or "By")
                return prefix + stripped.ToLowerInvariant() + suffix;
        }
        return prefix + stripped + suffix;
    }
}
