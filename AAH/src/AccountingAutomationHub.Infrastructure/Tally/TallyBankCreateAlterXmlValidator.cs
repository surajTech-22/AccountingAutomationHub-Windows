using System.Globalization;
using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Fail-closed validator for FIX4 mixed Bank Create/Alter XML. Alter is permitted only
/// with the exact live Tally MASTERID supplied by the pre-synchronized authority.
/// </summary>
internal static class TallyBankCreateAlterXmlValidator
{
    internal static void Validate(
        string xml,
        string expectedCompanyName,
        IReadOnlyList<TallyBankCreateAlterExpectation> expected)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Tally Bank Create/Alter XML is not well formed.", exception);
        }

        var header = document.Root?.Element("HEADER")
            ?? throw new InvalidOperationException("Tally Bank Create/Alter XML has no HEADER.");
        var request = header.Element("TALLYREQUEST")?.Value.Trim() ?? string.Empty;
        var id = header.Element("ID")?.Value.Trim() ?? string.Empty;
        if (!request.Contains("IMPORT", StringComparison.OrdinalIgnoreCase) ||
            !id.Equals("Vouchers", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tally Bank Create/Alter XML must be a Vouchers import.");

        var company = document.Descendants("SVCURRENTCOMPANY").FirstOrDefault()?.Value.Trim() ?? string.Empty;
        if (!company.Equals(expectedCompanyName.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Tally Bank Create/Alter XML company does not match the linked live company.");

        var vouchers = document.Descendants("VOUCHER").ToArray();
        if (vouchers.Length != expected.Count)
            throw new InvalidOperationException($"Tally Bank Create/Alter voucher count mismatch. Expected {expected.Count}, found {vouchers.Length}.");

        for (var index = 0; index < vouchers.Length; index++)
        {
            var voucher = vouchers[index];
            var item = expected[index];
            var action = Attribute(voucher, "ACTION");
            if (item.IsAlter)
            {
                if (!action.Equals("Alter", StringComparison.OrdinalIgnoreCase) ||
                    !Attribute(voucher, "TAGNAME").Equals("MASTER ID", StringComparison.OrdinalIgnoreCase) ||
                    !Attribute(voucher, "TAGVALUE").Equals(item.ExistingTallyMasterId.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Bank voucher {index + 1} is not bound to the verified live MASTERID for Alter.");
            }
            else if (!action.Equals("Create", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Bank voucher {index + 1} must be ACTION=Create.");
            }

            var date = Required(voucher, "DATE", index);
            if (!date.Equals(item.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidOperationException($"Bank voucher {index + 1} DATE does not match the source transaction.");
            if (!Required(voucher, "VOUCHERTYPENAME", index).Equals(item.VoucherType.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Bank voucher {index + 1} Voucher Type does not match the prepared decision.");

            var lines = voucher.Elements("ALLLEDGERENTRIES.LIST").ToArray();
            if (lines.Length != item.Lines.Count || lines.Length != 2)
                throw new InvalidOperationException($"Bank voucher {index + 1} must contain exactly two ledger lines.");

            decimal total = 0m;
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var actual = lines[lineIndex];
                var wanted = item.Lines[lineIndex];
                var ledger = Required(actual, "LEDGERNAME", index);
                if (!ledger.Equals(wanted.LedgerName.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Bank voucher {index + 1} ledger identity changed before send.");
                var positive = Required(actual, "ISDEEMEDPOSITIVE", index);
                if (!positive.Equals(wanted.IsDebit ? "Yes" : "No", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Bank voucher {index + 1} debit/credit direction changed before send.");
                if (!decimal.TryParse(Required(actual, "AMOUNT", index), NumberStyles.Number | NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var amount))
                    throw new InvalidOperationException($"Bank voucher {index + 1} has an invalid amount.");
                total += amount;
            }
            if (Math.Abs(total) > 0.01m)
                throw new InvalidOperationException($"Bank voucher {index + 1} is not balanced.");
        }
    }

    private static string Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;

    private static string Required(XElement parent, string name, int index)
    {
        var value = parent.Element(name)?.Value.Trim() ?? string.Empty;
        if (value.Length == 0)
            throw new InvalidOperationException($"Bank voucher {index + 1} is missing {name}.");
        return value;
    }
}
