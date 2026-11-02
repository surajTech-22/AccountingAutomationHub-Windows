using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Consolidated wire-format GST classification. Reuse the original source
/// XML and original Tally GST duty-head tags. No branch tax, rate, GSTIN,
/// journal or voucher amount is recalculated. The generic workbook parity
/// ledger writer uses TAXTYPE=Others for every class; for unambiguous
/// IGST/CGST/SGST duty ledgers only, the target needs GST + correct Duty Head.
/// Never infer a GST duty head from "GST Payable/Receivable" or other clearing.
/// </summary>
public static class TallyConsolidatedGstMasterPolicy
{
    private static bool Is(XElement x, string name) =>
        x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string Value(XElement x, string name) =>
        x.Elements().FirstOrDefault(c => Is(c, name))?.Value.Trim() ?? "";

    private static void Set(XElement x, string name, string value)
    {
        var field = x.Elements().FirstOrDefault(c => Is(c, name));
        if (field is null) x.Add(new XElement(name, value));
        else field.Value = value;
    }

    // The original individual Excel-parity XML generator writes blank
    // enumerated fields as "&#4; Not Applicable". The source-safe parser
    // decodes this to an ordinary string for the consolidated Ledger copy.
    // Tally treats TAXCLASSIFICATIONNAME as a reference to an EXISTING tax
    // classification master; there is no master literally called
    // "Not Applicable". Posting this placeholder causes Exceptions=1,
    // Created=0 before even the first Ledger can be created.
    //
    // Consolidated master wire-format only: disable placeholder-valued
    // OPTIONAL fields by retaining their XML labels with empty values.
    // Do not ask Tally to create phantom classifications. Genuine original
    // values and GST/party/branch metadata remain unchanged.
    private static readonly HashSet<string> PlaceholderFields = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "TAXCLASSIFICATIONNAME", "GSTTYPE", "APPROPRIATEFOR",
        "SERVICECATEGORY", "EXCISELEDGERCLASSIFICATION",
        "EXCISEDUTYTYPE", "EXCISENATUREOFPURCHASE", "LEDGERFBTCATEGORY"
    };

    public static void RemoveUnresolvedSourcePlaceholders(XElement ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        foreach (var field in ledger.Elements().Where(x =>
            PlaceholderFields.Contains(x.Name.LocalName) &&
            x.Value.Trim().TrimStart('\u0004').Trim()
                .Equals("Not Applicable", StringComparison.OrdinalIgnoreCase)).ToArray())
            field.Value = string.Empty; // Keep the label, disable the nonexistent reference.
    }

    /// <summary>
    /// GST consolidation is not a VAT enrollment request. Keep each existing
    /// VATDEALERTYPE label in the destination copy, but leave its old VAT value
    /// inactive when this source Ledger explicitly carries a GST registration.
    /// The original branch XML and GST fields remain untouched.
    /// </summary>
    public static void DisableLegacyVatOnGstLedger(XElement ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (Value(ledger, "GSTREGISTRATIONTYPE").Length == 0 &&
            Value(ledger, "PARTYGSTIN").Length == 0)
            return;
        foreach (var vat in ledger.Elements().Where(x => Is(x, "VATDEALERTYPE")))
            vat.Value = string.Empty;
    }

    /// <summary>
    /// For explicitly GST-marked vouchers, preserve the ISVATDUTYPAID label
    /// but set its obsolete VAT-only flag to No. Never touch signed DR/CR,
    /// GST registration, GSTIN or other original accounting values.
    /// </summary>
    public static void DisableLegacyVatOnGstVoucher(XElement voucher)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        if (Value(voucher, "GSTREGISTRATIONTYPE").Length == 0 &&
            Value(voucher, "PARTYGSTIN").Length == 0)
            return;
        foreach (var vat in voucher.Elements().Where(x => Is(x, "ISVATDUTYPAID")))
            vat.Value = "No";
    }

    public static void ApplyKnownDutyHead(XElement ledger, string consolidatedParent, string originalName)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        // Mandatory branch Taxes is always a child of Duties & Taxes.
        // Do not reclassify Tax settlement ledgers or arbitrary expenses.
        if (!consolidatedParent.EndsWith(" Taxes", StringComparison.OrdinalIgnoreCase))
            return;

        var matches = Regex.Matches(originalName,
            @"(?<![A-Za-z])(IGST|CGST|SGST|UTGST)(?![A-Za-z])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var heads = matches.Select(x => x.Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (heads.Length == 0)
            return; // e.g. GST Payable/Receivable needs no guessed duty head.
        if (heads.Length != 1)
            throw new InvalidOperationException(
                "Ambiguous GST duty head in original branch Ledger '" + originalName +
                "'. Do not generate a tax Ledger with guessed GST classification.");

        var duty = heads[0] switch
        {
            "IGST" => "Integrated Tax",
            "CGST" => "Central Tax",
            "SGST" => "State Tax",
            "UTGST" => "UT Tax",
            _ => throw new InvalidOperationException("Unrecognised original GST head.")
        };
        var existing = Value(ledger, "GSTDUTYHEAD");
        if (existing.Length > 0 && !existing.Equals(duty, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Original branch GST duty head conflicts with Ledger '" + originalName +
                "': " + existing + " vs " + duty + ". Check source, do not overwrite.");
        var taxType = Value(ledger, "TAXTYPE");
        if (taxType.Length > 0 &&
            !taxType.Equals("Others", StringComparison.OrdinalIgnoreCase) &&
            !taxType.Equals("GST", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Original branch Ledger '" + originalName +
                "' has explicit incompatible duty type '" + taxType + "'.");

        Set(ledger, "TAXTYPE", "GST");
        Set(ledger, "GSTDUTYHEAD", duty);
        Set(ledger, "ISBEHAVEASDUTY", "Yes");
        // GSTTYPE is an unrelated Tally enumeration from the original working
        // workbook; never replace it with "GST" or invent a tax rate here.
    }

    public static void VerifyVoucherPartyRegistration(
        XElement voucher, IReadOnlyDictionary<string, XElement[]> sourceLedgers, string sourceAnchor)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        ArgumentNullException.ThrowIfNull(sourceLedgers);
        var voucherGstin = Value(voucher, "PARTYGSTIN").ToUpperInvariant();
        if (voucherGstin.Length == 0) return;
        if (voucherGstin.Length != 15 || !voucherGstin[..2].All(char.IsAsciiDigit))
            throw new InvalidOperationException(
                "Voucher has malformed original party GSTIN: " + sourceAnchor);
        var party = Value(voucher, "PARTYLEDGERNAME");
        if (party.Length == 0) party = Value(voucher, "PARTYNAME");
        if (party.Length == 0) party = Value(voucher, "BASICBASEPARTYNAME");
        if (party.Length == 0)
        {
            // Journal/legacy vouchers can omit their top-level party tag.
            // Use the original source's explicit party-ledger entry marker,
            // never guess the counterparty from a tax or purchase line.
            var marked = voucher.Descendants().FirstOrDefault(x =>
                (Is(x, "ALLLEDGERENTRIES.LIST") || Is(x, "LEDGERENTRIES.LIST")) &&
                Value(x, "ISPARTYLEDGER").Equals("Yes", StringComparison.OrdinalIgnoreCase));
            if (marked is not null) party = Value(marked, "LEDGERNAME");
        }
        if (party.Length == 0 || !sourceLedgers.TryGetValue(party, out var declarations) ||
            declarations.Length == 0)
            return; // existing missing-master checks remain authoritative.
        var originalGstins = declarations
            .Select(l => Value(l, "PARTYGSTIN").ToUpperInvariant())
            .Where(x => x.Length == 15 && x[..2].All(char.IsAsciiDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var originalGstin in originalGstins)
        {
            // The original voucher may override the ledger's registration
            // *state* for one PAN. A different PAN under the same party
            // account would be a different legal entity and must not merge.
            if (!voucherGstin.Substring(2, 10).Equals(originalGstin.Substring(2, 10),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Voucher " + sourceAnchor + " uses party GSTIN " + voucherGstin +
                    " from a different PAN than original Ledger '" + party +
                    "' (" + originalGstin + "). No GSTIN was changed.");
        }
    }
}
