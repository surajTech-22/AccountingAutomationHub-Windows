using System.Globalization;
using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Validator for duplicate Bank voucher DELETE requests. FIX9 requires a distinct retained
/// MASTERID. Fix24 may delete a positively source-linked later MASTERID before the retained
/// first local source is recreated, but the exact delete MASTERID is always mandatory.
/// </summary>
internal static class TallyBankVoucherDeleteXmlValidator
{
    internal static void Validate(
        string xml,
        string expectedCompanyName,
        IReadOnlyList<TallyBankVoucherDeleteExpectation> expected)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var request = document.Root?.Element("HEADER")?.Element("TALLYREQUEST")?.Value.Trim() ?? string.Empty;
        var id = document.Root?.Element("HEADER")?.Element("ID")?.Value.Trim() ?? string.Empty;
        if (!request.Contains("IMPORT", StringComparison.OrdinalIgnoreCase) ||
            !id.Equals("Vouchers", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tally Bank duplicate DELETE XML must be a Vouchers import.");

        var company = document.Descendants("SVCURRENTCOMPANY").FirstOrDefault()?.Value.Trim() ?? string.Empty;
        if (!company.Equals(expectedCompanyName.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Tally Bank duplicate DELETE XML company does not match the linked live company.");

        var vouchers = document.Descendants("VOUCHER").ToArray();
        if (vouchers.Length != expected.Count)
            throw new InvalidOperationException($"Tally Bank duplicate DELETE count mismatch. Expected {expected.Count}, found {vouchers.Length}.");

        for (var index = 0; index < vouchers.Length; index++)
        {
            var voucher = vouchers[index];
            var item = expected[index];
            if (item.MasterId.Trim().Length == 0)
                throw new InvalidOperationException($"Bank duplicate DELETE {index + 1} has no delete MASTERID.");
            if (!item.AllowMissingRetainedMaster &&
                (item.RetainedMasterId.Trim().Length == 0 ||
                 item.MasterId.Trim().Equals(item.RetainedMasterId.Trim(), StringComparison.Ordinal)))
                throw new InvalidOperationException($"Bank duplicate DELETE {index + 1} does not have distinct delete/retained MASTERIDs.");
            if (item.AllowMissingRetainedMaster &&
                item.RetainedMasterId.Trim().Length > 0 &&
                item.MasterId.Trim().Equals(item.RetainedMasterId.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException($"Bank duplicate DELETE {index + 1} cannot delete its retained MASTERID.");
            if (item.LiveIdentityKey.Trim().Length == 0)
                throw new InvalidOperationException($"Bank duplicate DELETE {index + 1} has no exact live identity key.");

            var action = Attribute(voucher, "ACTION");
            if (!action.Equals("Delete", StringComparison.OrdinalIgnoreCase) ||
                !Attribute(voucher, "TAGNAME").Equals("MASTER ID", StringComparison.OrdinalIgnoreCase) ||
                !Attribute(voucher, "TAGVALUE").Equals(item.MasterId.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException($"Bank duplicate DELETE {index + 1} is not bound to its exact live MASTERID.");

            var date = voucher.Element("DATE")?.Value.Trim() ?? string.Empty;
            if (!date.Equals(item.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidOperationException($"Bank duplicate DELETE {index + 1} DATE changed before send.");
            var voucherType = voucher.Element("VOUCHERTYPENAME")?.Value.Trim() ?? string.Empty;
            if (!voucherType.Equals(item.VoucherType.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Bank duplicate DELETE {index + 1} Voucher Type changed before send.");
        }
    }

    private static string Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
}
