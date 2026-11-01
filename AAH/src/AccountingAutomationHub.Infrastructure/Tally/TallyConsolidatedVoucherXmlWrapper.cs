using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Converts already-prepared, branch-owned AAH Tally voucher XML into a consolidated
/// PREVIEW while preserving all non-identity GST, inventory and accounting fields.
/// Original module preparation, including saved GST journals, is the sole source:
/// this wrapper never creates or recalculates a GST journal or posts to Tally.
///
/// Every voucher must be mapped to a durable branch and independently resolved
/// destination masters. A live import is NOT authorized by an XML preview alone.
/// </summary>
public static class TallyConsolidatedVoucherXmlWrapper
{
    public static TallyConsolidatedVoucherPreview Prepare(
        TallyBranchMasterPlan masterPlan,
        IReadOnlyCollection<TallyConsolidatedBranchXmlInput> inputs,
        IReadOnlyCollection<MasterRecord> destinationMasters,
        bool destinationMasterAuthorityVerified)
    {
        ArgumentNullException.ThrowIfNull(masterPlan);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(destinationMasters);
        TallyBranchConsolidationMasterPlanner.ValidatePlanXml(masterPlan);
        if (!destinationMasterAuthorityVerified ||
            masterPlan.DestinationCompanyKey.Length == 0 ||
            masterPlan.DestinationCompanyName.Length == 0)
            throw new InvalidOperationException("Fresh destination Tally master authority is required before consolidated voucher XML preparation.");
        if (inputs.Count == 0 || inputs.Count != masterPlan.Groups
                .Select(g => g.BranchId).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidOperationException("One source-owned voucher set for every verified branch is required.");
        var approvedBranches = masterPlan.Groups.Select(g => (g.BranchId, g.Gstin))
            .Distinct().ToDictionary(x => x.BranchId, x => x.Gstin, StringComparer.OrdinalIgnoreCase);
        var seenBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenInterbankTransferMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLedgerNames = new Dictionary<string, (string Parent, string BranchId)>(StringComparer.OrdinalIgnoreCase);
        var mappedLedgers = new List<TallyConsolidatedLedgerMapping>();
        var transformedVouchers = new List<XElement>();
        var companyName = masterPlan.DestinationCompanyName;

        // Only the DESTINATION company's freshly verified masters count as ready.
        var destination = destinationMasters.Where(x => !x.IsArchived && x.Name.Trim().Length > 0)
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in masterPlan.Groups.Where(g => !g.Create))
            RequireExactLiveMaster(destination, group.Name, "Group", group.Parent);

        foreach (var source in inputs.OrderBy(i => i.Branch.BranchId, StringComparer.OrdinalIgnoreCase))
        {
            var branch = source.Branch;
            if (branch.ClientId <= 0 || !seenBranches.Add(branch.BranchId) ||
                !approvedBranches.TryGetValue(branch.BranchId, out var expectedGstin) ||
                !branch.FinancialYear.Equals(masterPlan.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
                !branch.Pan.Equals(masterPlan.Pan, StringComparison.OrdinalIgnoreCase) ||
                !branch.Gstin.Equals(expectedGstin, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Consolidated source branch identity, PAN, GSTIN or financial year is inconsistent.");
            if (source.Vouchers is null || source.Ledgers is null)
                throw new InvalidOperationException("Every branch must provide verified source vouchers and their original ledger parents.");

            var allowedLedgerNames = new Dictionary<string, TallyConsolidatedSourceLedger>(StringComparer.OrdinalIgnoreCase);
            foreach (var ledger in source.Ledgers)
            {
                if (string.IsNullOrWhiteSpace(ledger.Name) ||
                    !allowedLedgerNames.TryAdd(ledger.Name.Trim(), ledger))
                    throw new InvalidOperationException("Source ledger identity is ambiguous or missing.");
            }

            var groups = masterPlan.Groups.Where(x => x.BranchId.Equals(branch.BranchId, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Parent, x => x.Name, StringComparer.OrdinalIgnoreCase);
            var ledgerRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (sourceName, ledger) in allowedLedgerNames)
            {
                if (!groups.TryGetValue(ledger.Parent.Trim(), out var subgroup))
                    throw new InvalidOperationException($"Ledger '{sourceName}' has no mandatory branch group under '{ledger.Parent}'. Review its Tally accounting classification.");
                var sharedCash = ledger.SharedCash && ledger.Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase);
                if (ledger.SharedCash && !sharedCash)
                    throw new InvalidOperationException("Only an explicitly classified Cash-in-Hand ledger can be shared.");
                string targetName;
                string targetParent;
                if (sharedCash && masterPlan.CashLedgers.Count == 0)
                {
                    targetName = sourceName; // a shared cash ledger requires exact live verification
                    targetParent = "Cash-in-Hand";
                    RequireExactLiveMaster(destination, targetName, "Ledger", targetParent);
                }
                else if (sharedCash && masterPlan.CashLedgers.Count > 0)
                {
                    var cash = masterPlan.CashLedgers.SingleOrDefault(x =>
                        x.BranchId.Equals(branch.BranchId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Configured branch cash ledger is missing.");
                    targetName = cash.Name;
                    targetParent = "Cash-in-Hand";
                }
                else
                {
                    targetName = sourceName + " - " + branch.DisplayName;
                    targetParent = subgroup;
                }
                if (seenLedgerNames.TryGetValue(targetName, out var previously) &&
                    (!sharedCash || previously.Parent != targetParent))
                    throw new InvalidOperationException($"Destination ledger '{targetName}' would conflate two independent branch identities.");
                seenLedgerNames[targetName] = (targetParent, branch.BranchId);
                if (destination.ContainsKey(targetName))
                    RequireExactLiveMaster(destination, targetName, "Ledger", targetParent);
                ledgerRemap.Add(sourceName, targetName);
                mappedLedgers.Add(new(branch.BranchId, sourceName, ledger.Parent.Trim(),
                    targetName, targetParent, !destination.ContainsKey(targetName) &&
                                           !(sharedCash && masterPlan.CashLedgers.Any(x => x.Name == targetName)),
                    ledger.Gstin, ledger.State));
            }

            foreach (var item in source.Vouchers)
            {
                if (!item.IsSavedSource || string.IsNullOrWhiteSpace(item.SourceAnchor) ||
                    string.IsNullOrWhiteSpace(item.VoucherXml))
                    throw new InvalidOperationException("Only previously saved, source-owned branch vouchers can be consolidated; do not regenerate missing GST journals.");

                var key = branch.BranchId + "|" + item.SourceType.Trim() + "|" + item.SourceAnchor.Trim();
                if (!seenAnchors.Add(key))
                    throw new InvalidOperationException("Duplicate saved branch source anchor: " + key);
                if (item.Kind is TallyConsolidatedTransferKind.Interbank or TallyConsolidatedTransferKind.CashContra &&
                    !item.TransferMatched)
                    throw new InvalidOperationException("Cash/interbank Contra requires an explicit verified transfer classification.");
                if (item.Kind == TallyConsolidatedTransferKind.Interbank &&
                    (string.IsNullOrWhiteSpace(item.TransferMatchId) ||
                     item.CrossesDifferentPan))
                    throw new InvalidOperationException("Interbank Contra must be matched within the SAME PAN and retain both accounting sides.");
                if (item.Kind == TallyConsolidatedTransferKind.Interbank &&
                    !seenInterbankTransferMatches.Add(item.TransferMatchId.Trim()))
                    throw new InvalidOperationException("An interbank transfer was prepared more than once across branch source sets. Match once and preserve both Dr/Cr sides.");
                if (item.CrossesDifferentPan)
                    throw new InvalidOperationException("Different-PAN or intercompany transfers are out of scope.");

                var sourceDoc = ParseStrict(item.VoucherXml);
                var voucher = sourceDoc.Root?.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase) == true
                    ? new XElement(sourceDoc.Root)
                    : sourceDoc.Descendants().Where(e => e.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))
                        .Select(e => new XElement(e)).SingleOrDefault()
                      ?? throw new InvalidOperationException("Each saved source XML input must contain exactly one voucher.");
                if (sourceDoc.Root?.Name.LocalName != "VOUCHER" &&
                    sourceDoc.Descendants().Count(e => e.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidOperationException("A branch source input cannot contain multiple vouchers.");

                var typeElement = voucher.Elements().FirstOrDefault(e => e.Name.LocalName == "VOUCHERTYPENAME");
                var baseType = typeElement?.Value.Trim() ?? "";
                if (baseType.Length == 0)
                    throw new InvalidOperationException("Voucher type must be explicitly present in the source's original XML.");
                if (baseType.Equals("Contra", StringComparison.OrdinalIgnoreCase) &&
                    item.Kind == TallyConsolidatedTransferKind.Ordinary)
                    throw new InvalidOperationException("Contra vouchers require verified cash-versus-interbank transfer classification.");
                var targetType = baseType;
                if (item.Kind == TallyConsolidatedTransferKind.Interbank)
                    targetType = "Contra"; // one common interbank Contra, never branch-suffixed
                else if (masterPlan.VoucherTypes.Count > 0)
                {
                    var specific = masterPlan.VoucherTypes.SingleOrDefault(t =>
                        t.BranchId.Equals(branch.BranchId, StringComparison.OrdinalIgnoreCase) &&
                        t.BaseType.Equals(baseType, StringComparison.OrdinalIgnoreCase));
                    if (specific is null)
                        throw new InvalidOperationException("Branch-specific voucher type is missing from the configured destination type catalog.");
                    targetType = specific.Name;
                }
                if (typeElement is null) throw new InvalidOperationException("Original Tally voucher type is missing.");
                var voucherDate = voucher.Elements().FirstOrDefault(x => x.Name.LocalName == "DATE")?.Value.Trim() ?? "";
                if (!DateTime.TryParseExact(voucherDate, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) ||
                    date < FinancialYearStart(masterPlan.FinancialYear) ||
                    date >= FinancialYearStart(masterPlan.FinancialYear).AddYears(1))
                    throw new InvalidOperationException("Source voucher date is invalid or outside the selected consolidated financial year.");
                typeElement.Value = targetType;
                voucher.SetAttributeValue("VCHTYPE", targetType);
                voucher.SetAttributeValue("ACTION", "Create");

                // Foreign-company identity fields must NEVER enter the destination
                // as MASTERID/ALTERID/GUID/REMOTEID/VCHKEY/TAGVALUE/OBJGUID.
                foreach (var attr in voucher.Attributes().Where(a =>
                    IsIdentityName(a.Name.LocalName)).ToArray()) attr.Remove();
                foreach (var node in voucher.Descendants().Where(e =>
                    IsIdentityName(e.Name.LocalName)).ToArray()) node.Remove();
                foreach (var attr in voucher.Descendants().Attributes().Where(a =>
                    IsIdentityName(a.Name.LocalName)).ToArray()) attr.Remove();

                var localVoucherNumber = voucher.Elements().FirstOrDefault(e => e.Name.LocalName == "VOUCHERNUMBER");
                if (localVoucherNumber is not null && localVoucherNumber.Value.Trim().Length > 0)
                    localVoucherNumber.Value = branch.DisplayName + "/" + localVoucherNumber.Value.Trim();
                // Deterministic namespace-bound identity prevents same-number source
                // collisions across branches. Never claim that this proves absence
                // in the *destination*; live destination duplicate authority still gates posting.
                var stableGuid = DeterministicGuid(masterPlan.DestinationCompanyKey + "|" + key);
                // Remove the original GUID first. A destination-scoped stable
                // identity must never coexist with a source company's GUID.
                foreach (var oldGuid in voucher.Elements().Where(e => e.Name.LocalName == "GUID").ToArray())
                    oldGuid.Remove();
                voucher.AddFirst(new XElement("GUID", stableGuid));

                var accountingEntries = voucher.Elements().Where(e =>
                    e.Name.LocalName is "ALLLEDGERENTRIES.LIST" or "LEDGERENTRIES.LIST").ToArray();
                if (accountingEntries.Length < 2)
                    throw new InvalidOperationException("Every consolidated voucher must carry both Dr and Cr ledger sides.");
                decimal total = 0m;
                foreach (var entry in accountingEntries)
                {
                    var amount = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "AMOUNT")?.Value;
                    if (!decimal.TryParse(amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var value))
                        throw new InvalidOperationException("A consolidated accounting ledger line has no valid amount.");
                    total += value;
                }
                if (Math.Abs(total) > 0.01m)
                    throw new InvalidOperationException("Consolidated voucher debit and credit amounts do not balance.");

                // Rewrite ALL ledger references, including inventory/accounting
                // allocations and party identity, using one exact ledger map.
                foreach (var element in voucher.Descendants().Where(e =>
                    e.Name.LocalName is "LEDGERNAME" or "PARTYLEDGERNAME" or "PARTYNAME" or "BASICBASEPARTYNAME"))
                {
                    if (element.Value.Trim().Length == 0) continue;
                    if (!ledgerRemap.TryGetValue(element.Value.Trim(), out var mapped))
                        throw new InvalidOperationException($"Ledger '{element.Value}' has no verified source-parent mapping for {branch.DisplayName}.");
                    element.Value = mapped;
                }

                // GST registration is a source-owned choice, not an inferred state
                // from the consolidated destination. Validate rather than overwrite it.
                var taxUnit = voucher.Elements().FirstOrDefault(e => e.Name.LocalName == "TAXUNITNAME")?.Value.Trim() ?? "";
                var requiresGstRegistration = item.IsGstRelevant ||
                    baseType.Equals("Sales", StringComparison.OrdinalIgnoreCase) ||
                    baseType.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ||
                    voucher.Descendants().Any(x => x.Name.LocalName is "TAXUNITNAME" or "CMPGSTSTATE");
                if (requiresGstRegistration && branch.Gstin.Length > 0)
                {
                    if (taxUnit.Length == 0 ||
                        !taxUnit.Equals(item.VerifiedTaxUnitName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"GST source voucher '{key}' has no exact verified Tally tax-unit registration. Import blocked; do not substitute the consolidated default GSTIN.");
                }
                transformedVouchers.Add(voucher);
            }
        }

        if (seenBranches.Count != approvedBranches.Count)
            throw new InvalidOperationException("Not every configured branch was represented in consolidation.");

        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(builder, companyName, "Vouchers");
        foreach (var voucher in transformedVouchers)
        {
            builder.AppendLine("      <TALLYMESSAGE>");
            builder.AppendLine(voucher.ToString(SaveOptions.DisableFormatting));
            builder.AppendLine("      </TALLYMESSAGE>");
        }
        TallyExcelParityXmlWriter.EndErp9DataImport(builder);
        var xml = builder.ToString();
        var verification = ParseStrict(xml);
        if (verification.Descendants("VOUCHER").Count() != transformedVouchers.Count ||
            verification.Descendants("SVCURRENTCOMPANY").Single().Value != companyName ||
            verification.Descendants("VOUCHER").Any(v => v.Attribute("ACTION")?.Value != "Create"))
            throw new InvalidOperationException("Consolidated voucher XML preview did not pass the existing Tally Data/Vouchers envelope contract.");

        // Only build the *missing* accounting ledgers; Tally master verification
        // and create/readback must happen before voucher XML can ever be posted.
        var ledgers = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(ledgers, companyName);
        foreach (var mapped in mappedLedgers.Where(x => x.NeedsCreate)
                     .DistinctBy(x => x.DestinationLedger, StringComparer.OrdinalIgnoreCase))
        {
            var original = inputs.Single(x => x.Branch.BranchId.Equals(mapped.BranchId, StringComparison.OrdinalIgnoreCase))
                .Ledgers.Single(x => x.Name.Equals(mapped.SourceLedger, StringComparison.OrdinalIgnoreCase));
            TallyExcelParityXmlWriter.AppendLedgerMaster(ledgers,
                new TallyExcelLedgerMaster(mapped.DestinationLedger, mapped.DestinationParent,
                    FinancialYearStart(masterPlan.FinancialYear), "Create",
                    original.Gstin, original.State, original.Country, original.Pincode,
                    original.MailingName, original.Address1, original.Address2, original.Address3,
                    original.IsBank, original.BankName, original.BankBranchName,
                    original.AccountNumber, original.IfscCode, original.AccountType,
                    ParentIsPartyGroup: mapped.SourceParent.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase) ||
                                        mapped.SourceParent.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase)));
        }
        TallyExcelParityXmlWriter.EndAllMastersImport(ledgers);
        return new(companyName, masterPlan.DestinationCompanyKey, masterPlan.FinancialYear,
            masterPlan.GroupXml, ledgers.ToString(), masterPlan.CashLedgerXml,
            masterPlan.VoucherTypeXml, xml, transformedVouchers.Count, mappedLedgers);
    }

    private static XDocument ParseStrict(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 20_000_000 });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static void RequireExactLiveMaster(
        Dictionary<string, MasterRecord[]> masters, string name, string type, string parent)
    {
        if (!masters.TryGetValue(name, out var records) || records.Length != 1 ||
            !records[0].Type.Equals(type, StringComparison.OrdinalIgnoreCase) ||
            !records[0].Parent.Trim().Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(records[0].TallyMasterId))
            throw new InvalidOperationException($"'{name}' must be reconciled with a unique LIVE destination Tally {type} MASTERID and correct Parent '{parent}'.");
    }

    private static bool IsIdentityName(string name) => name.ToUpperInvariant() is
        "MASTERID" or "ALTERID" or "REMOTEID" or "VCHKEY" or "OBJGUID" or "GUID" or
        "TAGVALUE" or "TAGNAME" or "LASTVCHID";

    private static string DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }

    private static DateTime FinancialYearStart(string year)
    {
        if (year.Length < 4 || !int.TryParse(year[..4], out var start) || start is < 1900 or > 9998)
            throw new InvalidOperationException("Invalid accounting financial year.");
        return new DateTime(start, 4, 1);
    }
}

public enum TallyConsolidatedTransferKind { Ordinary, CashContra, Interbank }

public sealed record TallyConsolidatedSourceLedger(
    string Name, string Parent, string Gstin = "", string State = "",
    string Country = "", string Pincode = "", string MailingName = "",
    string Address1 = "", string Address2 = "", string Address3 = "",
    bool SharedCash = false, bool IsBank = false,
    string BankName = "", string BankBranchName = "",
    string AccountNumber = "", string IfscCode = "", string AccountType = "");

public sealed record TallyConsolidatedSavedVoucher(
    string SourceType, string SourceAnchor, string VoucherXml,
    bool IsSavedSource = true, bool IsGstRelevant = false,
    string VerifiedTaxUnitName = "",
    TallyConsolidatedTransferKind Kind = TallyConsolidatedTransferKind.Ordinary,
    bool TransferMatched = false, string TransferMatchId = "", bool CrossesDifferentPan = false);

public sealed record TallyConsolidatedBranchXmlInput(
    TallyBranchMasterIdentity Branch,
    IReadOnlyCollection<TallyConsolidatedSourceLedger> Ledgers,
    IReadOnlyCollection<TallyConsolidatedSavedVoucher> Vouchers);

public sealed record TallyConsolidatedLedgerMapping(
    string BranchId, string SourceLedger, string SourceParent,
    string DestinationLedger, string DestinationParent,
    bool NeedsCreate, string Gstin, string State);

/// <summary>Offline-only separate-stage XML. Import execution is not permitted
/// until original Tally connection, MASTERID, voucher authority and readback
/// infrastructure have positively verified the destination.</summary>
public sealed record TallyConsolidatedVoucherPreview(
    string CompanyName, string CompanyKey, string FinancialYear,
    string BranchGroupsXml, string BranchLedgersXml,
    string OptionalCashLedgersXml, string OptionalVoucherTypesXml,
    string VouchersXml, int VoucherCount,
    IReadOnlyList<TallyConsolidatedLedgerMapping> LedgerMappings);
