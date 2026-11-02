using System.Xml.Linq;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// AAH branch ClientId/FY and its saved prepared XML are source authority.
/// A linked Tally company is OPTIONAL for every source branch; it is mandatory
/// only for the consolidated destination. Never confuse SVCURRENTCOMPANY
/// (the XML's export target) with the branch's accounting ownership.
/// </summary>
public static class TallyConsolidatedBranchSourcePolicy
{
    /// <summary>
    /// The private branch ClientId/FY and prepared XML are authoritative.
    /// A previously saved branch Tally link is merely optional extra master
    /// metadata. Never let an obsolete FY, mismatched company, or link to
    /// the writable consolidated target block source-owned XML or contribute
    /// masters/MASTERIDs to the destination company.
    /// This selection is read-only: keep the original saved link unchanged.
    /// </summary>
    public static TallyClientLink? SelectOptionalLiveSourceLink(
        TallyClientLink? saved, long branchClientId, string financialYear,
        TallyCompanyInfo destination, string preparedXmlCompany)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (saved is null || branchClientId <= 0 ||
            saved.ClientId != branchClientId ||
            string.IsNullOrWhiteSpace(saved.CompanyKey) ||
            string.IsNullOrWhiteSpace(saved.CompanyName) ||
            !HasConfirmedSourceBooksCoverage(saved, financialYear) ||
            saved.CompanyKey.Trim().Equals(destination.CompanyKey.Trim(), StringComparison.OrdinalIgnoreCase) ||
            // A fresh offline AAH branch snapshot deliberately carries a
            // synthetic SVCURRENTCOMPANY, not the branch's optional Tally link
            // name. Preserve a separately verified live source master link
            // when it has the same branch ClientId/FY and is not the writable
            // consolidated destination.
            (!preparedXmlCompany.StartsWith("AAH Prepared Branch ", StringComparison.OrdinalIgnoreCase) &&
             !saved.CompanyName.Trim().Equals(preparedXmlCompany.Trim(), StringComparison.OrdinalIgnoreCase)))
            return null;

        return saved;
    }

    /// <summary>
    /// SqliteTallyRepository.ConfirmLink saves the company's discovery FY, which
    /// can be blank or its FIRST books year. Do not confuse that metadata with
    /// the private branch's current accounting FY. Preserve an older-year link
    /// only when BooksFrom independently proves that its books include the
    /// beginning of the selected branch FY; never use a stale/foreign link.
    /// </summary>
    public static bool HasConfirmedSourceBooksCoverage(TallyClientLink link, string financialYear)
    {
        ArgumentNullException.ThrowIfNull(link);
        var requested = TallyCompanyInfo.NormalizeFinancialYear(financialYear);
        if (requested.Length != 7 || requested[4] != '-' ||
            !int.TryParse(requested[..4], out var year) || year < 1900 || year > 9998 ||
            !requested.Equals($"{year:0000}-{(year + 1) % 100:00}", StringComparison.OrdinalIgnoreCase))
            return false;
        var from = new DateTime(year, 4, 1);
        var books = TallyCompanyInfo.ParseTallyDate(link.BooksFrom);
        var started = TallyCompanyInfo.ParseTallyDate(link.StartingFrom);
        if (books is not null && books.Value.Date > from ||
            started is not null && started.Value.Date > from)
            return false;
        var discoveredYear = TallyCompanyInfo.NormalizeFinancialYear(link.FinancialYear);
        if (discoveredYear.Equals(requested, StringComparison.OrdinalIgnoreCase))
            return true;
        if (books is null)
            return false; // no independent evidence that old books span this FY
        var firstBookYear = TallyCompanyInfo.NormalizeFinancialYear("", link.BooksFrom);
        return discoveredYear.Length == 0 ||
               discoveredYear.Equals(firstBookYear, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validate the original accounting exporter's actual voucher collection
    /// before consolidated staging. This checks the existing Tally serializer,
    /// not a second builder, and never changes DR/CR, GST or source identities.
    /// </summary>
    public static void RequirePreparedVoucherCount(string xml, int ready, string branchClientCode)
    {
        if (ready <= 0)
            throw new InvalidOperationException("Branch '" + branchClientCode +
                "' has no ready vouchers in the original accounting exporter.");
        var document = TallyConsolidatedXmlTextSafety.ParseDocument(
            xml, "original exporter output for branch " + branchClientCode);
        var requestData = document.Descendants().Where(x =>
            x.Name.LocalName.Equals("REQUESTDATA", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (document.Root is null ||
            !document.Root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase) ||
            requestData.Length != 1)
            throw new InvalidOperationException("Branch '" + branchClientCode +
                "' has no verified original Tally All Masters envelope.");
        var messages = requestData[0].Elements().Where(x =>
            x.Name.LocalName.Equals("TALLYMESSAGE", StringComparison.OrdinalIgnoreCase)).ToArray();
        var vouchers = messages.SelectMany(message => message.Elements()).Where(x =>
            x.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (vouchers.Length != ready)
            throw new InvalidOperationException("Branch '" + branchClientCode +
                "' original accounting exporter reported " + ready +
                " ready voucher(s), but its XML contains " + vouchers.Length +
                " voucher(s). Empty/stale source XML must not be consolidated.");
    }

    public static string Validate(
        string preparedXml, string? statusText, string branchClientCode,
        string financialYear, TallyCompanyInfo destination,
        TallyClientLink? optionalSourceLink)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (string.IsNullOrWhiteSpace(preparedXml))
            throw new InvalidOperationException("Branch-prepared Tally XML is empty.");
        if (string.IsNullOrWhiteSpace(branchClientCode) ||
            string.IsNullOrWhiteSpace(financialYear))
            throw new InvalidOperationException("Saved branch Client/FY identity is required.");

        // The original normal AAH Live XML generator writes this status next
        // to its prepared XML; it proves that the file was generated from the
        // branch accounting scope, rather than a stale parent/consolidated file.
        // Do not require branch-specific Tally linking as a proxy for identity.
        if (string.IsNullOrWhiteSpace(statusText))
            throw new InvalidOperationException("Branch '" + branchClientCode +
                "' has no source-owned Live XML status file. Rebuild its prepared XML from the branch workspace.");
        var expected = "Client: " + branchClientCode + " | FY " + financialYear;
        if (!statusText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Any(line => line.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Prepared XML is not owned by the saved branch Client/FY: " +
                branchClientCode + " / " + financialYear);

        var document = TallyConsolidatedXmlTextSafety.ParseDocument(
            preparedXml, "saved source branch " + branchClientCode);
        if (document.Root is null || !document.Root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase) ||
            !document.Descendants().Any(x =>
                x.Name.LocalName.Equals("REQUESTDATA", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Branch '" + branchClientCode +
                "' does not contain the original Tally All Masters import envelope.");
        if (!document.Descendants().Where(x =>
                x.Name.LocalName.Equals("TALLYMESSAGE", StringComparison.OrdinalIgnoreCase))
                .Any(x => x.Elements().Any(y =>
                    y.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Branch '" + branchClientCode +
                "' contains no prepared voucher messages. Never consolidate an empty/stale XML snapshot.");

        var xmlCompany = document.Descendants().FirstOrDefault(x =>
            x.Name.LocalName.Equals("SVCURRENTCOMPANY", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
        if (xmlCompany.Length == 0)
            throw new InvalidOperationException("Saved branch XML has no declared Tally import destination.");
        if (xmlCompany.StartsWith("AAH Prepared Branch ", StringComparison.OrdinalIgnoreCase) &&
            !xmlCompany.Equals("AAH Prepared Branch " + branchClientCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Offline prepared branch XML belongs to a different ClientId/FY: " + branchClientCode);

        if (optionalSourceLink is not null)
        {
            // An optional separately-linked branch Tally company can supply
            // richer live source master metadata, never target MASTERIDs.
            if (!HasConfirmedSourceBooksCoverage(optionalSourceLink, financialYear))
                throw new InvalidOperationException(
                    "Optional source branch Tally link has no verified books coverage for the branch's accounting FY.");
            if (optionalSourceLink.CompanyKey.Equals(destination.CompanyKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A source branch cannot share the consolidated destination's Tally link.");
            if (!xmlCompany.Equals(optionalSourceLink.CompanyName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prepared branch XML does not match its optional source Tally company.");
        }

        return xmlCompany;
    }
}
