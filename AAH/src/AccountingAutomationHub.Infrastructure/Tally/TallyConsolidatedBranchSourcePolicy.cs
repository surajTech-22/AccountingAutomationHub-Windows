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
                x.Name.LocalName.Equals("TALLYMESSAGE", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Saved branch XML is not a complete original Tally import envelope.");

        var xmlCompany = document.Descendants().FirstOrDefault(x =>
            x.Name.LocalName.Equals("SVCURRENTCOMPANY", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
        if (xmlCompany.Length == 0)
            throw new InvalidOperationException("Saved branch XML has no declared Tally import destination.");

        if (optionalSourceLink is not null)
        {
            // An optional separately-linked branch Tally company can supply
            // richer live source master metadata, never target MASTERIDs.
            if (!optionalSourceLink.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Saved branch Tally link financial year differs from branch books.");
            if (optionalSourceLink.CompanyKey.Equals(destination.CompanyKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A source branch cannot share the consolidated destination's Tally link.");
            if (!xmlCompany.Equals(optionalSourceLink.CompanyName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prepared branch XML does not match its optional source Tally company.");
        }

        return xmlCompany;
    }
}
