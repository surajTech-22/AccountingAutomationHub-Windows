using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Builds one minimal ERP 9-safe ALTER for an existing live Bank voucher after the
/// established Inter-Bank reconciliation engine has already decided source/target.
/// It never decides whether a transaction is Contra and never recomputes direction:
/// target Bank is Debit, source Bank is Credit.
/// </summary>
internal static class TallyBankContraAlterXmlBuilder
{
    internal static string BuildFromCachedVoucher(
        string companyName,
        TallyVoucherXmlSnapshot snapshot,
        string sourceBankExactName,
        string targetBankExactName,
        decimal amount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.MasterId))
            throw new InvalidOperationException("Cached Tally Contra source Voucher has no MASTERID.");
        if (snapshot.VoucherDate is not DateTime date)
            throw new InvalidOperationException($"Cached Tally Voucher MASTERID {snapshot.MasterId} has no readable date.");

        var wrapped = $"<ROOT xmlns:UDF=\"TallyUDF\">{snapshot.OriginalXml}</ROOT>";
        var document = XDocument.Parse(wrapped, LoadOptions.PreserveWhitespace);
        var voucher = document.Root?.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Cached Tally Voucher MASTERID {snapshot.MasterId} has no Voucher XML object.");

        foreach (var attribute in voucher.Attributes().Where(attribute =>
                     attribute.Name.LocalName.Equals("ACTION", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("TAGNAME", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("TAGVALUE", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("VCHTYPE", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("DATE", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("OBJVIEW", StringComparison.OrdinalIgnoreCase)).ToArray())
            attribute.Remove();

        voucher.SetAttributeValue("DATE", date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture));
        voucher.SetAttributeValue("TAGNAME", "MASTER ID");
        voucher.SetAttributeValue("TAGVALUE", snapshot.MasterId.Trim());
        voucher.SetAttributeValue("ACTION", "Alter");
        voucher.SetAttributeValue("VCHTYPE", "Contra");
        voucher.SetAttributeValue("OBJVIEW", "Accounting Voucher View");

        SetOrAddDirect(voucher, "DATE", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        SetOrAddDirect(voucher, "VOUCHERTYPENAME", "Contra");
        SetIfPresent(voucher, "PERSISTEDVIEW", "Accounting Voucher View");
        SetIfPresent(voucher, "VCHENTRYMODE", "Double Entry");

        foreach (var party in voucher.Elements().Where(element =>
                     element.Name.LocalName.Equals("PARTYLEDGERNAME", StringComparison.OrdinalIgnoreCase) ||
                     element.Name.LocalName.Equals("PARTYNAME", StringComparison.OrdinalIgnoreCase) ||
                     element.Name.LocalName.Equals("BASICBASEPARTYNAME", StringComparison.OrdinalIgnoreCase)).ToArray())
            party.Remove();

        foreach (var entry in voucher.Elements().Where(element =>
                     Normalize(element.Name.LocalName) is "ALLLEDGERENTRIESLIST" or "LEDGERENTRIESLIST").ToArray())
            entry.Remove();

        voucher.Add(BuildLedgerElement(targetBankExactName, amount, isDebit: true));
        voucher.Add(BuildLedgerElement(sourceBankExactName, amount, isDebit: false));

        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(builder, companyName, "Vouchers");
        builder.AppendLine("      <TALLYMESSAGE>");
        builder.Append("        ").AppendLine(voucher.ToString(SaveOptions.DisableFormatting));
        builder.AppendLine("      </TALLYMESSAGE>");
        TallyExcelParityXmlWriter.EndErp9DataImport(builder);

        var xml = builder.ToString();
        Validate(xml, snapshot.MasterId.Trim(), sourceBankExactName, targetBankExactName, amount);
        return xml;
    }

    private static XElement BuildLedgerElement(string exactLedgerName, decimal amount, bool isDebit)
    {
        return new XElement("ALLLEDGERENTRIES.LIST",
            new XElement("LEDGERNAME", exactLedgerName),
            new XElement("ISDEEMEDPOSITIVE", isDebit ? "Yes" : "No"),
            new XElement("AMOUNT", (isDebit ? -Math.Abs(amount) : Math.Abs(amount)).ToString("0.00", CultureInfo.InvariantCulture)));
    }

    private static void SetOrAddDirect(XElement voucher, string localName, string value)
    {
        var element = voucher.Elements().FirstOrDefault(item => item.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        if (element is null) voucher.AddFirst(new XElement(localName, value));
        else element.Value = value;
    }

    private static void SetIfPresent(XElement voucher, string localName, string value)
    {
        var element = voucher.Elements().FirstOrDefault(item => item.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        if (element is not null) element.Value = value;
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal static string Build(
        string companyName,
        string masterId,
        DateTime date,
        string voucherNumber,
        string reference,
        string narration,
        string sourceBankExactName,
        string targetBankExactName,
        decimal amount)
    {
        if (string.IsNullOrWhiteSpace(companyName)) throw new ArgumentException("Tally company is required.", nameof(companyName));
        if (string.IsNullOrWhiteSpace(masterId)) throw new ArgumentException("Tally MASTERID is required for ALTER.", nameof(masterId));
        if (string.IsNullOrWhiteSpace(sourceBankExactName)) throw new ArgumentException("Source Bank Ledger is required.", nameof(sourceBankExactName));
        if (string.IsNullOrWhiteSpace(targetBankExactName)) throw new ArgumentException("Target Bank Ledger is required.", nameof(targetBankExactName));
        if (amount <= 0m) throw new ArgumentOutOfRangeException(nameof(amount));

        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(builder, companyName, "Vouchers");
        builder.AppendLine("      <TALLYMESSAGE>");
        builder.Append("        <VOUCHER DATE=\"")
            .Append(date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture))
            .Append("\" TAGNAME=\"MASTER ID\" TAGVALUE=\"")
            .Append(XmlExact(masterId))
            .AppendLine("\" ACTION=\"Alter\" VCHTYPE=\"Contra\" OBJVIEW=\"Accounting Voucher View\">");
        AppendTag(builder, "DATE", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        AppendTag(builder, "NARRATION", narration);
        AppendTag(builder, "VOUCHERTYPENAME", "Contra");
        AppendTag(builder, "VOUCHERNUMBER", voucherNumber);
        AppendTag(builder, "REFERENCE", reference);

        // Existing Inter-Bank result is authoritative: target Bank DR, source Bank CR.
        AppendLedger(builder, targetBankExactName, amount, isDebit: true);
        AppendLedger(builder, sourceBankExactName, amount, isDebit: false);

        builder.AppendLine("        </VOUCHER>");
        builder.AppendLine("      </TALLYMESSAGE>");
        TallyExcelParityXmlWriter.EndErp9DataImport(builder);

        var xml = builder.ToString();
        Validate(xml, masterId, sourceBankExactName, targetBankExactName, amount);
        return xml;
    }

    private static void AppendLedger(StringBuilder builder, string exactLedgerName, decimal amount, bool isDebit)
    {
        builder.AppendLine("          <ALLLEDGERENTRIES.LIST>");
        AppendTag(builder, "LEDGERNAME", exactLedgerName, 12);
        AppendTag(builder, "ISDEEMEDPOSITIVE", isDebit ? "Yes" : "No", 12);
        AppendTag(builder, "AMOUNT", (isDebit ? -Math.Abs(amount) : Math.Abs(amount)).ToString("0.00", CultureInfo.InvariantCulture), 12);
        builder.AppendLine("          </ALLLEDGERENTRIES.LIST>");
    }

    private static void AppendTag(StringBuilder builder, string tag, string? value, int indent = 10)
    {
        if (string.IsNullOrEmpty(value)) return;
        builder.Append(' ', indent).Append('<').Append(tag).Append('>')
            .Append(XmlExact(value))
            .Append("</").Append(tag).AppendLine(">");
    }

    // Preserve hidden ERP 9 Ledger identity. Numeric XML references prevent CR/LF
    // from being normalized away while still producing valid XML text/attributes.
    private static string XmlExact(string? value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value ?? string.Empty)
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

    private static void Validate(string xml, string masterId, string source, string target, decimal amount)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var voucher = document.Descendants().FirstOrDefault(element => element.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Contra ALTER XML does not contain a Voucher.");
        if (!string.Equals(voucher.Attribute("ACTION")?.Value, "Alter", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(voucher.Attribute("TAGVALUE")?.Value, masterId, StringComparison.Ordinal))
            throw new InvalidOperationException("Contra ALTER XML lost the existing Tally MASTERID authority.");
        if (!string.Equals(voucher.Elements().FirstOrDefault(e => e.Name.LocalName == "VOUCHERTYPENAME")?.Value, "Contra", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Contra ALTER XML is not a Contra voucher.");

        var lines = voucher.Descendants().Where(e => e.Name.LocalName.Equals("ALLLEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (lines.Length != 2) throw new InvalidOperationException("Contra ALTER XML must contain exactly two Bank Ledger entries.");
        var debit = lines.SingleOrDefault(line => line.Elements().Any(e => e.Name.LocalName == "ISDEEMEDPOSITIVE" && e.Value.Equals("Yes", StringComparison.OrdinalIgnoreCase)));
        var credit = lines.SingleOrDefault(line => line.Elements().Any(e => e.Name.LocalName == "ISDEEMEDPOSITIVE" && e.Value.Equals("No", StringComparison.OrdinalIgnoreCase)));
        if (debit?.Elements().FirstOrDefault(e => e.Name.LocalName == "LEDGERNAME")?.Value != target ||
            credit?.Elements().FirstOrDefault(e => e.Name.LocalName == "LEDGERNAME")?.Value != source)
            throw new InvalidOperationException("Contra ALTER XML does not preserve Inter-Bank source/target direction.");
        var debitAmount = decimal.Parse(debit.Elements().First(e => e.Name.LocalName == "AMOUNT").Value, CultureInfo.InvariantCulture);
        var creditAmount = decimal.Parse(credit.Elements().First(e => e.Name.LocalName == "AMOUNT").Value, CultureInfo.InvariantCulture);
        if (Math.Abs(Math.Abs(debitAmount) - amount) > 0.01m || Math.Abs(Math.Abs(creditAmount) - amount) > 0.01m)
            throw new InvalidOperationException("Contra ALTER XML amount differs from the Inter-Bank match amount.");
    }
}
