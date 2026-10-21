using System.Globalization;
using System.Text;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Bank duplicate DELETE writer. FIX9 uses it for exact live-cardinality replacement; Fix24
/// also uses it for durable first-import-wins cleanup after a later local physical duplicate
/// has been removed. Every DELETE remains bound to an exact live Tally MASTERID.
/// </summary>
internal static class TallyBankVoucherDeleteXmlBuilder
{
    internal static string Build(
        string companyName,
        IReadOnlyCollection<TallyBankVoucherDeleteExpectation> vouchers)
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(builder, companyName, "Vouchers");
        foreach (var voucher in vouchers)
        {
            builder.AppendLine("      <TALLYMESSAGE>");
            builder.Append("        <VOUCHER DATE=\"")
                .Append(voucher.Date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture))
                .Append("\" TAGNAME=\"MASTER ID\" TAGVALUE=\"")
                .Append(XmlExact(voucher.MasterId.Trim()))
                .Append("\" ACTION=\"Delete\" VCHTYPE=\"")
                .Append(XmlExact(voucher.VoucherType.Trim()))
                .AppendLine("\">");
            AppendTag(builder, "DATE", voucher.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            AppendTag(builder, "VOUCHERTYPENAME", voucher.VoucherType);
            builder.AppendLine("        </VOUCHER>");
            builder.AppendLine("      </TALLYMESSAGE>");
        }
        TallyExcelParityXmlWriter.EndErp9DataImport(builder);
        return builder.ToString();
    }

    private static void AppendTag(StringBuilder builder, string tag, string value)
    {
        builder.Append("          <").Append(tag).Append('>')
            .Append(XmlExact(value.Trim()))
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

internal sealed record TallyBankVoucherDeleteExpectation(
    DateTime Date,
    string VoucherType,
    string MasterId,
    string RetainedMasterId,
    string LiveIdentityKey,
    bool AllowMissingRetainedMaster = false);
