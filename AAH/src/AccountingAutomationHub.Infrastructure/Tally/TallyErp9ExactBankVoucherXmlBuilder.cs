using System.Globalization;
using System.Text;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Bank voucher CREATE writer that keeps the established minimal ERP 9 contract but
/// preserves exact live Tally Ledger identity. The protected Excel-parity writer trims
/// normal strings by design, so legacy Ledger names with trailing CR/LF must use this
/// Bank-only exact-name writer after live authority has been captured.
/// </summary>
internal static class TallyErp9ExactBankVoucherXmlBuilder
{
    internal static string Build(
        string companyName,
        IReadOnlyCollection<TallyErp9BankVoucherExpectation> vouchers)
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(builder, companyName, "Vouchers");
        foreach (var voucher in vouchers)
        {
            builder.AppendLine("      <TALLYMESSAGE>");
            builder.AppendLine("        <VOUCHER>");
            AppendTag(builder, "DATE", voucher.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 10, exact: false);
            AppendTag(builder, "NARRATION", voucher.Narration, 10, exact: false);
            AppendTag(builder, "VOUCHERTYPENAME", voucher.VoucherType, 10, exact: false);
            AppendTag(builder, "REFERENCE", voucher.Reference, 10, exact: false);
            foreach (var line in voucher.Lines)
            {
                var signed = line.IsDebit ? -Math.Abs(line.Amount) : Math.Abs(line.Amount);
                builder.AppendLine("          <ALLLEDGERENTRIES.LIST>");
                AppendTag(builder, "LEDGERNAME", line.LedgerName, 12, exact: true);
                AppendTag(builder, "ISDEEMEDPOSITIVE", line.IsDebit ? "Yes" : "No", 12, exact: false);
                AppendTag(builder, "AMOUNT", signed.ToString("0.00", CultureInfo.InvariantCulture), 12, exact: false);
                builder.AppendLine("          </ALLLEDGERENTRIES.LIST>");
            }
            builder.AppendLine("        </VOUCHER>");
            builder.AppendLine("      </TALLYMESSAGE>");
        }
        TallyExcelParityXmlWriter.EndErp9DataImport(builder);
        return builder.ToString();
    }

    private static void AppendTag(StringBuilder builder, string tag, string? value, int indent, bool exact)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = exact ? value ?? string.Empty : (value ?? string.Empty).Trim();
        builder.Append(' ', indent).Append('<').Append(tag).Append('>')
            .Append(XmlExact(text))
            .Append("</").Append(tag).AppendLine(">");
    }

    private static string XmlExact(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                '\r' => "&#13;",
                '\n' => "&#10;",
                _ => ch.ToString()
            });
        }
        return builder.ToString();
    }
}
