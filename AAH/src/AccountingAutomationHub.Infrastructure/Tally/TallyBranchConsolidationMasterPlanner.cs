using System.Security;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Side-effect-free preflight for one PAN / one FY / multiple verified branches.
/// This is a wrapper over the established Tally master XML writer, NOT a second
/// Tally connection, voucher generator or import loop. It never posts anything.
/// </summary>
public static class TallyBranchConsolidationMasterPlanner
{
    // A separate branch subgroup is MANDATORY below every accounting classification
    // used by consolidated vouchers. It is NOT a group under Branch/Divisions.
    // The original Tally accounting parents remain authoritative.
    private static readonly (string Parent, string Label)[] Classes =
    [
        ("Sundry Creditors", "Creditors"),
        ("Sundry Debtors", "Debtors"),
        ("Sales Accounts", "Sales"),
        ("Purchase Accounts", "Purchase"),
        ("Bank Accounts", "Banks"),
        ("Cash-in-Hand", "Cash"),
        ("Direct Expenses", "Direct Expenses"),
        ("Indirect Expenses", "Expenses"),
        ("Direct Incomes", "Direct Incomes"),
        ("Indirect Incomes", "Incomes"),
        ("Duties & Taxes", "Taxes"),
        // The original individual Bank exporter puts unmapped Final Ledger
        // rows into Tally's built-in Suspense A/c. It is a direct child of
        // Primary, so the consolidated wrapper must map it through a
        // VERIFIED branch subgroup under Suspense A/c, not synthesize a
        // custom group beneath Primary or bypass the master-first gate.
        ("Suspense A/c", "Suspense A/c"),
        ("Fixed Assets", "Fixed Assets"),
        ("Current Assets", "Current Assets"),
        ("Current Liabilities", "Current Liabilities"),
        ("Loans (Liability)", "Loans"),
        ("Capital Account", "Capital"),
        ("Investments", "Investments"),
        ("Stock-in-Hand", "Stock")
    ];

    /// <summary>
    /// Read only the voucher type names that ACTUALLY occur in each original
    /// branch's already-prepared XML. Do not expand every available Tally type
    /// into a new branch subtype. This is planning metadata, not accounting
    /// generation and never carries branch Tally MASTERIDs into the parent.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> ReadUsedVoucherTypes(
        IReadOnlyCollection<TallyPreparedBranchXml> branchSources)
    {
        ArgumentNullException.ThrowIfNull(branchSources);
        var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in branchSources)
        {
            if (string.IsNullOrWhiteSpace(source.Branch.BranchId) ||
                string.IsNullOrWhiteSpace(source.Xml) ||
                result.ContainsKey(source.Branch.BranchId))
                throw new InvalidOperationException(
                    "Every branch needs one current, identifiable prepared XML for voucher type planning.");
            var doc = TallyConsolidatedXmlTextSafety.ParseDocument(
                source.Xml, "original branch voucher type catalog");
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var voucher in doc.Descendants().Where(x =>
                x.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase) &&
                x.Parent is not null &&
                x.Parent.Name.LocalName.Equals("TALLYMESSAGE", StringComparison.OrdinalIgnoreCase)))
            {
                // The consolidated wrapper replaces explicitly matched
                // interbank source Payment/Receipt entries with ONE common
                // Contra. They do not need branch-specific destination types.
                var sourceReference = voucher.Elements().FirstOrDefault(x =>
                    x.Name.LocalName.Equals("REFERENCE", StringComparison.OrdinalIgnoreCase))
                    ?.Value.Trim() ?? string.Empty;
                if (sourceReference.StartsWith(
                        TallyConsolidatedPreparedXmlBuilder.InterbankReferencePrefix,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var names = voucher.Elements().Where(x =>
                    x.Name.LocalName.Equals("VOUCHERTYPENAME", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Value.Trim()).ToArray();
                if (names.Length != 1 || names[0].Length == 0)
                    throw new InvalidOperationException(
                        "Prepared branch voucher has no single original Tally Voucher Type: " +
                        source.Branch.DisplayName);
                types.Add(names[0]);
            }
            result.Add(source.Branch.BranchId, types.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        return result;
    }

    public static TallyBranchMasterPlan Build(
        string destinationCompanyName,
        string destinationCompanyKey,
        string financialYear,
        IReadOnlyCollection<TallyBranchMasterIdentity> branches,
        IReadOnlyCollection<MasterRecord> freshDestinationMasters,
        bool freshDestinationMasterAuthorityVerified,
        bool separateVoucherTypes = false,
        bool separateCashLedgers = false,
        IReadOnlyCollection<string>? eligibleVoucherTypes = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? usedVoucherTypesByBranch = null)
    {
        if (string.IsNullOrWhiteSpace(destinationCompanyName) ||
            string.IsNullOrWhiteSpace(destinationCompanyKey))
            throw new InvalidOperationException("Consolidated Tally must have a verified writable destination company link.");
        if (string.IsNullOrWhiteSpace(financialYear))
            throw new InvalidOperationException("Consolidated financial year is required.");
        if (!freshDestinationMasterAuthorityVerified || freshDestinationMasters is null)
            throw new InvalidOperationException("Fresh verified destination Tally master authority is required before planning writes.");
        if (branches is null || branches.Count < 2)
            throw new InvalidOperationException("At least two verified same-PAN branches are required.");
        if (separateVoucherTypes && (eligibleVoucherTypes is null || eligibleVoucherTypes.Count == 0))
            throw new InvalidOperationException("The destination's applicable voucher type catalog is required when branch voucher types are enabled.");

        var normalized = branches.OrderBy(x => x.BranchId, StringComparer.OrdinalIgnoreCase).ToArray();
        if (separateVoucherTypes)
        {
            // Creating a subtype under a name-only or stale original Tally
            // Voucher Type is NOT permitted. The live master must have its
            // own positive destination MASTERID before any branch GROUP
            // or Voucher Type is posted.
            foreach (var original in usedVoucherTypesByBranch?.Values
                         .SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase)
                         ?? Enumerable.Empty<string>())
            {
                var matches = freshDestinationMasters.Where(x =>
                    x.Type.Equals("Voucher Type", StringComparison.OrdinalIgnoreCase) &&
                    x.Name.Equals(original, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 ||
                    !long.TryParse(matches[0].TallyMasterId,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var originalId) || originalId <= 0)
                    throw new InvalidOperationException(
                        "Original parent Tally Voucher Type '" + original +
                        "' has no unique verified destination MASTERID. " +
                        "No branch master creation or voucher ALTER is allowed.");
            }
            if (usedVoucherTypesByBranch is null ||
                usedVoucherTypesByBranch.Count != normalized.Length ||
                normalized.Any(branch =>
                    !usedVoucherTypesByBranch.TryGetValue(branch.BranchId, out var used) || used is null))
                throw new InvalidOperationException(
                    "Current original branch voucher usage is required before creating separate voucher types.");
            var catalog = eligibleVoucherTypes!.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (branchId, used) in usedVoucherTypesByBranch)
            {
                if (!normalized.Any(b => b.BranchId.Equals(branchId, StringComparison.OrdinalIgnoreCase)) ||
                    used.Any(name => string.IsNullOrWhiteSpace(name) ||
                        !catalog.Contains(name.Trim()) ||
                        normalized.Any(b => name.Trim().EndsWith(" - " + b.DisplayName,
                            StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException(
                        "Original branch voucher type is not a verified base type in linked Tally: " + branchId);
            }
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commonPan = "";
        foreach (var branch in normalized)
        {
            if (branch.ClientId <= 0 || string.IsNullOrWhiteSpace(branch.BranchId) ||
                string.IsNullOrWhiteSpace(branch.DisplayName) ||
                branch.DisplayName.Trim().Length > 80 ||
                branch.DisplayName.Any(char.IsControl) ||
                !ids.Add(branch.BranchId.Trim()) ||
                !names.Add(branch.DisplayName.Trim()))
                throw new InvalidOperationException("Each branch requires a unique durable ID, Client ID and display name.");
            var pan = branch.Pan.Trim().ToUpperInvariant();
            if (pan.Length != 10 || !pan.Take(5).All(char.IsLetter) ||
                !pan.Skip(5).Take(4).All(char.IsDigit) || !char.IsLetter(pan[9]))
                throw new InvalidOperationException("A verified ten-character PAN is required for each branch.");
            if (commonPan.Length == 0) commonPan = pan;
            if (!commonPan.Equals(pan, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Different-PAN entities are outside same-PAN branch consolidation.");
            if (!branch.FinancialYear.Trim().Equals(financialYear.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Branch and consolidated financial years must match.");
            var gstin = (branch.Gstin ?? string.Empty).Trim().ToUpperInvariant();
            // Multiple branches can share a registration within the same state,
            // and non-GST branches can be name-only. The authoritative branch ID
            // (not GSTIN) separates their ledger and voucher identities.
            // When a GSTIN IS supplied, it must belong to this one PAN.
            if (gstin.Length > 0 &&
                (gstin.Length != 15 || !gstin.Take(2).All(char.IsDigit) ||
                 !gstin.Substring(2, 10).Equals(commonPan, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A branch's GSTIN must belong to the consolidated PAN.");
        }

        // Name/parent checks are based on the FRESH selected destination company;
        // no master is borrowed from individual-branch Tally databases.
        var existing = freshDestinationMasters
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var groups = new List<TallyBranchGroupRequirement>();
        var toCreate = new List<MasterRecord>();
        var cashToCreate = new List<TallyBranchCashLedgerRequirement>();
        var types = new List<TallyBranchVoucherTypeRequirement>();
        var generatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool NeedsCreate(string name, string type, string parent)
        {
            if (!generatedNames.Add(name))
                throw new InvalidOperationException($"Two branch masters resolve to the same Tally name '{name}'.");
            if (!existing.TryGetValue(name, out var matches)) return true;
            if (matches.Length != 1 || !matches[0].Type.Equals(type, StringComparison.OrdinalIgnoreCase) ||
                !matches[0].Parent.Trim().Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                // Imported local/unbound rows are not evidence that Tally owns this master.
                string.IsNullOrWhiteSpace(matches[0].TallyMasterId))
                throw new InvalidOperationException(
                    $"Tally master '{name}' is conflicting or unverified; reconcile MASTERID and parent before import.");
            return false;
        }

        foreach (var branch in normalized)
        {
            var name = branch.DisplayName.Trim();
            foreach (var (parent, label) in Classes)
            {
                var groupName = name + " " + label;
                var create = NeedsCreate(groupName, "Group", parent);
                groups.Add(new(branch.BranchId, branch.Gstin, parent, groupName, create));
                if (create)
                    toCreate.Add(new MasterRecord
                    {
                        Type = "Group", Name = groupName, Parent = parent
                    });
            }
            // Cash ledger is OPTIONAL. Do not conflate its option with compulsory groups.
            if (separateCashLedgers)
            {
                // A separate optional Cash ledger still needs a unique Tally name.
                // Use the durable non-geographical branch ID, never its state name.
                var cashName = "Cash (" + branch.BranchId.Trim() + ")";
                var create = NeedsCreate(cashName, "Ledger", "Cash-in-Hand");
                cashToCreate.Add(new(branch.BranchId, cashName, create));
            }
            if (!separateVoucherTypes) continue;

            foreach (var baseType in usedVoucherTypesByBranch![branch.BranchId]
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var original = baseType.Trim();
                // Common interbank Contra still uses the original 'Contra'.
                // Branch Contra is the cash contra type when the option is enabled.
                var selected = original + " - " + name;
                var create = NeedsCreate(selected, "Voucher Type", original);
                types.Add(new(branch.BranchId, original, selected, create));
            }
        }

        // Reuse the EXISTING group master serializer and its XML contract validator.
        var groupXml = TallyErp9ManualGroupImport.Build(destinationCompanyName, toCreate);
        TallyErp9ManualGroupImport.Validate(groupXml, destinationCompanyName, toCreate);
        var ledgerXml = BuildCashLedgerXml(destinationCompanyName, cashToCreate);
        var voucherTypeXml = BuildVoucherTypeXml(destinationCompanyName, types);
        return new(
            destinationCompanyKey.Trim(), destinationCompanyName.Trim(),
            commonPan, financialYear.Trim(), groups, cashToCreate, types,
            groupXml, ledgerXml, voucherTypeXml);
    }

    private static string BuildCashLedgerXml(string companyName,
        IEnumerable<TallyBranchCashLedgerRequirement> ledgers)
    {
        var xml = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(xml, companyName);
        foreach (var ledger in ledgers.Where(x => x.Create))
            TallyExcelParityXmlWriter.AppendErp9MinimalLedgerMaster(
                xml, ledger.Name, "Cash-in-Hand");
        TallyExcelParityXmlWriter.EndAllMastersImport(xml);
        return xml.ToString();
    }

    private static string BuildVoucherTypeXml(string companyName,
        IEnumerable<TallyBranchVoucherTypeRequirement> types)
    {
        // Create only genuinely missing branch voucher-type Masters.
        // Do not change an existing Tally voucher-type Master or any voucher.
        // Native numbering remains None as requested; no Manual/Automatic
        // numbering migration and no voucher ALTER is part of this stage.
        var xml = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(xml, companyName);
        foreach (var type in types.Where(x => x.Create))
        {
            xml.AppendLine("      <TALLYMESSAGE>");
            xml.Append("        <VOUCHERTYPE NAME=\"")
                .Append(SecurityElement.Escape(type.Name)).AppendLine("\" ACTION=\"Create\">");
            xml.Append("          <NAME>").Append(SecurityElement.Escape(type.Name)).AppendLine("</NAME>");
            xml.Append("          <PARENT>").Append(SecurityElement.Escape(type.BaseType)).AppendLine("</PARENT>");
            // New masters use the requested None setting. Existing masters
            // are never ALTERed, and this creation-only action never posts
            // or reclassifies existing vouchers.
            xml.AppendLine("          <NUMBERINGMETHOD>None</NUMBERINGMETHOD>");
            xml.AppendLine("        </VOUCHERTYPE>");
            xml.AppendLine("      </TALLYMESSAGE>");
        }
        TallyExcelParityXmlWriter.EndAllMastersImport(xml);
        return xml.ToString();
    }

    public static void ValidatePlanXml(TallyBranchMasterPlan plan)
    {
        if (plan.Groups.Count == 0 || plan.Groups
            .GroupBy(g => g.BranchId, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Count() != Classes.Length))
            throw new InvalidOperationException("Mandatory branch accounting groups are incomplete.");

        foreach (var (xml, elementName, count) in new[]
                 {
                     (plan.GroupXml, "GROUP", plan.Groups.Count(x => x.Create)),
                     (plan.CashLedgerXml, "LEDGER", plan.CashLedgers.Count(x => x.Create)),
                     (plan.VoucherTypeXml, "VOUCHERTYPE", plan.VoucherTypes.Count(x => x.Create))
                 })
        {
            var doc = XDocument.Parse(xml);
            if (doc.Root?.Name.LocalName != "ENVELOPE" ||
                doc.Descendants("SVCURRENTCOMPANY").SingleOrDefault()?.Value != plan.DestinationCompanyName ||
                doc.Descendants(elementName).Count() != count ||
                doc.Descendants("VOUCHER").Any())
                throw new InvalidOperationException("Branch master XML preview violates the expected Tally import envelope.");
        }
    }
}

public sealed record TallyBranchMasterIdentity(
    long ClientId, string BranchId, string DisplayName,
    string Pan, string Gstin, string FinancialYear);

public sealed record TallyBranchGroupRequirement(
    string BranchId, string Gstin, string Parent, string Name, bool Create);

public sealed record TallyBranchCashLedgerRequirement(
    string BranchId, string Name, bool Create);

public sealed record TallyBranchVoucherTypeRequirement(
    string BranchId, string BaseType, string Name, bool Create);

/// <summary>
/// Master-first planning output. This preview is NOT permission to post to Tally.
/// The original coordinator must verify/write/read-back masters before vouchers.
/// </summary>
public sealed record TallyBranchMasterPlan(
    string DestinationCompanyKey, string DestinationCompanyName,
    string Pan, string FinancialYear,
    IReadOnlyList<TallyBranchGroupRequirement> Groups,
    IReadOnlyList<TallyBranchCashLedgerRequirement> CashLedgers,
    IReadOnlyList<TallyBranchVoucherTypeRequirement> VoucherTypes,
    string GroupXml, string CashLedgerXml, string VoucherTypeXml);
