using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Consolidation is a transport/identity wrapper, NOT another accounting engine.
/// The branch's prepared voucher is the sole authority for each DR/CR, tax, value,
/// inventory and narration field. No source-side journal or GST routine is called.
/// This produces two offline XML stages; it does not bypass the existing live
/// Tally import/readback/duplicate gate or imply that a voucher was imported.
/// </summary>
public static class TallyConsolidatedPreparedXmlBuilder
{
    public const string InterbankReferencePrefix = "AAH-INTERBANK:";

    public static TallyConsolidatedPreparedXmlResult Build(
        TallyBranchMasterPlan plan, IReadOnlyCollection<TallyPreparedBranchXml> prepared)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(prepared);
        var expected = plan.Groups.Select(x => x.BranchId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (expected.Length < 2 || prepared.Count != expected.Length ||
            prepared.GroupBy(x => x.Branch.BranchId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1) ||
            prepared.Any(x => !expected.Contains(x.Branch.BranchId, StringComparer.OrdinalIgnoreCase) ||
                              !x.Branch.FinancialYear.Equals(plan.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
                              !x.Branch.Pan.Equals(plan.Pan, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Every verified same-PAN branch must supply its own prepared XML exactly once.");

        var masterMessages = new List<XElement>();
        var vouchers = new List<(TallyPreparedBranchXml Branch, XElement Voucher)>();
        var explicitlyMatched = new Dictionary<string, List<(TallyPreparedBranchXml Branch, XElement Voucher)>>(StringComparer.OrdinalIgnoreCase);
        var seenVoucherAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappedLedgerMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceVoucherCount = 0;

        foreach (var branch in prepared.OrderBy(x => x.Branch.BranchId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(branch.Xml))
                throw new InvalidOperationException("The branch has no prepared Tally XML: " + branch.Branch.DisplayName);
            var source = XDocument.Parse(branch.Xml, LoadOptions.PreserveWhitespace);
            if (!Is(source.Root, "ENVELOPE") ||
                !source.Descendants().Any(x => Is(x, "TALLYMESSAGE")))
                throw new InvalidOperationException("Invalid original Tally XML envelope for " + branch.Branch.DisplayName);
            var ledgerAuthority = branch.Masters
                .Where(m => m.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Name))
                .GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            var originalLedgerXml = source.Descendants().Where(x => Is(x, "LEDGER") &&
                x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                .GroupBy(x => Field(x, "NAME"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            var customGroups = branch.Masters.Where(x => x.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

            string AccountingParent(string ledger)
            {
                if (!ledgerAuthority.TryGetValue(ledger, out var sources) || sources.Length != 1)
                    throw new InvalidOperationException("Missing or ambiguous branch Ledger authority: " +
                        branch.Branch.DisplayName + "/" + ledger);
                var parent = sources[0].Parent.Trim();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var requirement = plan.Groups.SingleOrDefault(g =>
                        g.BranchId.Equals(branch.Branch.BranchId, StringComparison.OrdinalIgnoreCase) &&
                        g.Parent.Equals(parent, StringComparison.OrdinalIgnoreCase));
                    if (requirement is not null) return requirement.Name;
                    if (!visited.Add(parent) || !customGroups.TryGetValue(parent, out var group) ||
                        group.Length != 1 || string.IsNullOrWhiteSpace(group[0].Parent))
                        throw new InvalidOperationException("Ledger '" + ledger + "' has an unverified accounting parent: " + parent);
                    parent = group[0].Parent.Trim();
                }
            }

            string LedgerName(string sourceName)
            {
                if (!ledgerAuthority.TryGetValue(sourceName.Trim(), out var masters) || masters.Length != 1)
                    throw new InvalidOperationException("Voucher references an unverified branch ledger: " +
                        branch.Branch.DisplayName + "/" + sourceName);
                // Every branch owns its own ledger even when the original names are
                // identical. Never merge taxes, parties, bank accounts or cash by name.
                return sourceName.Trim() + " - " + branch.Branch.DisplayName.Trim();
            }

            foreach (var raw in source.Descendants().Where(x => Is(x, "VOUCHER") &&
                       x.Parent is not null && Is(x.Parent, "TALLYMESSAGE")))
            {
                var v = new XElement(raw);
                if (!Field(v, "ACTION").Equals("Create", StringComparison.OrdinalIgnoreCase) ||
                    Field(v, "TAGNAME").Length > 0 || Field(v, "TAGVALUE").Length > 0 ||
                    v.Descendants().Any(x => Is(x, "MASTERID") || Is(x, "GUID")) ||
                    v.Attributes().Any(x => x.Name.LocalName.Equals("GUID", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("A prepared branch ALTER or Tally-bound voucher cannot be replayed as CREATE. " +
                        "Reconcile its original identity in the existing export gate first.");
                if (v.Descendants().Any(x => Is(x, "ALLINVENTORYENTRIES.LIST") || Is(x, "INVENTORYENTRIES.LIST")))
                    throw new InvalidOperationException("Inventory invoice master/stock identity requires source-preserving mapping before consolidated XML can be released.");
                var originalNumber = Value(v, "VOUCHERNUMBER");
                var date = Value(v, "DATE");
                var type = Value(v, "VOUCHERTYPENAME");
                if (originalNumber.Length == 0 || date.Length != 8 || type.Length == 0)
                    throw new InvalidOperationException("Prepared voucher lacks a stable number, date or type.");
                var anchor = branch.Branch.BranchId + "|" + date + "|" + type + "|" + originalNumber;
                if (!seenVoucherAnchors.Add(anchor))
                    throw new InvalidOperationException("Duplicate voucher within one branch's prepared XML: " + anchor);
                sourceVoucherCount++;
                var refValue = Value(v, "REFERENCE");
                if (refValue.StartsWith(InterbankReferencePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var transferId = refValue[InterbankReferencePrefix.Length..].Trim();
                    if (transferId.Length == 0)
                        throw new InvalidOperationException("Interbank transfer ID is blank.");
                    if (!explicitlyMatched.TryGetValue(transferId, out var pair))
                        explicitlyMatched[transferId] = pair = new();
                    pair.Add((branch, v));
                    continue;
                }

                RewriteVoucher(v, branch.Branch, plan, LedgerName, branch.Masters);
                vouchers.Add((branch, v));
            }

            // Only the ledgers actually used by prepared vouchers are emitted.
            // Reuse original prepared Ledger XML where it exists; refuse to
            // invent GST/party/bank metadata when the source declaration is absent.
            var used = source.Descendants().Where(x => Is(x, "VOUCHER") &&
                x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                .SelectMany(v => v.Descendants().Where(x => Is(x, "LEDGERNAME")))
                .Select(x => x.Value.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var original in used)
            {
                var destinationLedger = LedgerName(original);
                var parent = AccountingParent(original);
                if (!mappedLedgerMasters.Add(destinationLedger))
                    throw new InvalidOperationException("Two branch ledgers map to one consolidated ledger: " + destinationLedger);
                if (!originalLedgerXml.TryGetValue(original, out var sourceDeclarations) || sourceDeclarations.Length != 1)
                    throw new InvalidOperationException("Prepared branch XML does not contain full master details for '" +
                        original + "'. Cannot reconstruct a GST/party/bank ledger from its name.");
                var ledger = new XElement(sourceDeclarations[0]);
                if (Field(ledger, "ACTION").Equals("Alter", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A source Ledger ALTER cannot be replayed in consolidated Tally.");
                ledger.SetAttributeValue("NAME", destinationLedger);
                ledger.SetAttributeValue("ACTION", "Create");
                Set(ledger, "NAME", destinationLedger);
                Set(ledger, "PARENT", parent);
                if (ledger.Descendants().Any(x => Is(x, "MASTERID") || Is(x, "GUID")))
                    throw new InvalidOperationException("Source Ledger has Tally identity fields; cannot reuse foreign MASTERIDs.");
                masterMessages.Add(new XElement("TALLYMESSAGE", ledger));
            }
        }

        // Interbank is the ONE exception: reconcile two explicitly linked branch
        // bank sides into a common Contra without calculating or rewriting values.
        var reconciled = 0;
        foreach (var (transferId, sourcePair) in explicitlyMatched.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (sourcePair.Count != 2 || sourcePair[0].Branch.Branch.BranchId.Equals(
                    sourcePair[1].Branch.Branch.BranchId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Interbank transfer " + transferId +
                    " must be matched by exactly two different branches.");
            var sides = new List<XElement>();
            var amounts = new List<decimal>();
            foreach (var (branch, original) in sourcePair)
            {
                var bankNames = branch.Masters.Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    x.Parent.Equals("Bank Accounts", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var entries = original.Elements().Where(x => Is(x, "ALLLEDGERENTRIES.LIST") || Is(x, "LEDGERENTRIES.LIST")).ToArray();
                if (entries.Length != 2)
                    throw new InvalidOperationException("Interbank transfer must have one bank and one clearing ledger per branch.");
                var candidates = entries.Where(x => bankNames.Contains(Value(x, "LEDGERNAME"))).ToArray();
                if (candidates.Length != 1 ||
                    !decimal.TryParse(Value(candidates[0], "AMOUNT"), NumberStyles.Number | NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var amount) || amount == 0)
                    throw new InvalidOperationException("Interbank bank-side amount or ledger is not verifiable.");
                var side = new XElement(candidates[0]);
                Set(side, "LEDGERNAME", Value(side, "LEDGERNAME") + " - " + branch.Branch.DisplayName.Trim());
                sides.Add(side);
                amounts.Add(amount);
            }
            if (amounts[0] + amounts[1] != 0 ||
                Value(sourcePair[0].Voucher, "DATE") != Value(sourcePair[1].Voucher, "DATE"))
                throw new InvalidOperationException("Interbank matching failed: dates or opposite bank movements differ.");
            var merged = new XElement(sourcePair[0].Voucher);
            foreach (var entry in merged.Elements().Where(x => Is(x, "ALLLEDGERENTRIES.LIST") || Is(x, "LEDGERENTRIES.LIST")).ToArray())
                entry.Remove();
            foreach (var field in merged.Elements().Where(x => Is(x, "PARTYLEDGERNAME") || Is(x, "PARTYNAME") || Is(x, "BASICBASEPARTYNAME")).ToArray())
                field.Remove();
            foreach (var side in sides) merged.Add(side);
            Set(merged, "VOUCHERTYPENAME", "Contra");
            Set(merged, "VOUCHERNUMBER", "IBT-" + transferId);
            merged.SetAttributeValue("VCHTYPE", "Contra");
            vouchers.Add((sourcePair[0].Branch, merged));
            reconciled++;
        }
        if (vouchers.Count == 0)
            throw new InvalidOperationException("No final vouchers were prepared for the consolidated XML.");

        var masterBuilder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(masterBuilder, plan.DestinationCompanyName);
        var groupSource = XDocument.Parse(plan.GroupXml);
        foreach (var m in groupSource.Descendants().Where(x => Is(x, "TALLYMESSAGE")))
            masterBuilder.AppendLine(m.ToString(SaveOptions.DisableFormatting));
        foreach (var m in masterMessages)
            masterBuilder.AppendLine(m.ToString(SaveOptions.DisableFormatting));
        TallyExcelParityXmlWriter.EndAllMastersImport(masterBuilder);

        var voucherBuilder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(voucherBuilder, plan.DestinationCompanyName, "Vouchers");
        foreach (var (_, voucher) in vouchers)
            voucherBuilder.AppendLine(new XElement("TALLYMESSAGE", voucher).ToString(SaveOptions.DisableFormatting));
        TallyExcelParityXmlWriter.EndErp9DataImport(voucherBuilder);
        return new TallyConsolidatedPreparedXmlResult(
            masterBuilder.ToString(), voucherBuilder.ToString(),
            expected.Length, sourceVoucherCount, vouchers.Count, reconciled,
            "Original branch amounts, entries, tax values and journals were not recalculated. " +
            "XML is staged only; live Tally duplicate/readback checks are still mandatory.");
    }

    private static void RewriteVoucher(
        XElement voucher, TallyBranchMasterIdentity branch,
        TallyBranchMasterPlan plan, Func<string, string> ledger, IReadOnlyCollection<MasterRecord> authority)
    {
        foreach (var item in voucher.Descendants().Where(x =>
            Is(x, "LEDGERNAME") || Is(x, "PARTYLEDGERNAME") || Is(x, "PARTYNAME") ||
            Is(x, "BASICBASEPARTYNAME")))
        {
            if (string.IsNullOrWhiteSpace(item.Value)) continue;
            item.Value = ledger(item.Value);
        }
        var original = Value(voucher, "VOUCHERNUMBER");
        Set(voucher, "VOUCHERNUMBER", branch.BranchId + "-" + original);
        var baseType = Value(voucher, "VOUCHERTYPENAME");
        if (plan.VoucherTypes.Count > 0)
        {
            var selected = plan.VoucherTypes.SingleOrDefault(x =>
                x.BranchId.Equals(branch.BranchId, StringComparison.OrdinalIgnoreCase) &&
                x.BaseType.Equals(baseType, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                throw new InvalidOperationException("Branch voucher type is not in the verified optional type catalog: " + baseType);
            Set(voucher, "VOUCHERTYPENAME", selected.Name);
            voucher.SetAttributeValue("VCHTYPE", selected.Name);
        }
    }

    private static bool Is(XElement? x, string name) =>
        x is not null && x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static string Field(XElement element, string field) =>
        element.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(field, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
    private static string Value(XElement element, string name) =>
        element.Elements().FirstOrDefault(x => Is(x, name))?.Value.Trim() ?? "";
    private static void Set(XElement element, string name, string value)
    {
        var match = element.Elements().FirstOrDefault(x => Is(x, name));
        if (match is null) element.Add(new XElement(name, value));
        else match.Value = value;
    }
}

public sealed record TallyPreparedBranchXml(
    TallyBranchMasterIdentity Branch, string Xml, IReadOnlyCollection<MasterRecord> Masters);

public sealed record TallyConsolidatedPreparedXmlResult(
    string MastersXml, string VouchersXml, int BranchCount,
    int SourceVouchers, int FinalVouchers, int InterbankReconciled, string Status);
