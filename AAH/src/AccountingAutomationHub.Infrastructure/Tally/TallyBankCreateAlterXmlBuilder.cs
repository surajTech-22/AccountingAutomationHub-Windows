using System.Globalization;
using System.Text;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// FIX4 Bank voucher writer for a mixed Create/Alter import. Existing protected ERP 9
/// create-only writer/validator remain byte-identical; this writer is used only after
/// the live pre-sync has positively identified an AAH-owned MASTERID for an Alter.
/// </summary>
internal static class TallyBankCreateAlterXmlBuilder
{
    internal static string Build(
        string companyName,
        IReadOnlyCollection<TallyBankCreateAlterExpectation> vouchers)
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(builder, companyName, "Vouchers");
        foreach (var voucher in vouchers)
        {
            builder.AppendLine("      <TALLYMESSAGE>");
            if (voucher.IsAlter)
            {
                builder.Append("        <VOUCHER DATE=\"")
                    .Append(voucher.Date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture))
                    .Append("\" TAGNAME=\"MASTER ID\" TAGVALUE=\"")
                    .Append(XmlExact(voucher.ExistingTallyMasterId.Trim()))
                    .Append("\" ACTION=\"Alter\" VCHTYPE=\"")
                    .Append(XmlExact(voucher.VoucherType.Trim()))
                    .AppendLine("\" OBJVIEW=\"Accounting Voucher View\">");
            }
            else
            {
                builder.Append("        <VOUCHER VCHTYPE=\"")
                    .Append(XmlExact(voucher.VoucherType.Trim()))
                    .AppendLine("\" ACTION=\"Create\" OBJVIEW=\"Accounting Voucher View\">");
            }

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

internal sealed record TallyBankCreateAlterExpectation(
    DateTime Date,
    string VoucherType,
    string Reference,
    string Narration,
    IReadOnlyList<TallyErp9BankLedgerLine> Lines,
    string ExistingTallyMasterId)
{
    public bool IsAlter => ExistingTallyMasterId.Trim().Length > 0;
}
