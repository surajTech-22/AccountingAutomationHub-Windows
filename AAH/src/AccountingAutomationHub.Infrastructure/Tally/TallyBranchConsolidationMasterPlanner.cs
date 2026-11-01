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
        ("Fixed Assets", "Fixed Assets"),
        ("Current Assets", "Current Assets"),
        ("Current Liabilities", "Current Liabilities"),
        ("Loans (Liability)", "Loans"),
        ("Capital Account", "Capital"),
        ("Investments", "Investments"),
        ("Stock-in-Hand", "Stock")
    ];

    public static TallyBranchMasterPlan Build(
        string destinationCompanyName,
        string destinationCompanyKey,
        string financialYear,
        IReadOnlyCollection<TallyBranchMasterIdentity> branches,
        IReadOnlyCollection<MasterRecord> freshDestinationMasters,
        bool freshDestinationMasterAuthorityVerified,
        bool separateVoucherTypes = false,
        bool separateCashLedgers = false,
        IReadOnlyCollection<string>? eligibleVoucherTypes = null)
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
                var cashName = "Cash - " + name;
                var create = NeedsCreate(cashName, "Ledger", "Cash-in-Hand");
                cashToCreate.Add(new(branch.BranchId, cashName, create));
            }
            if (!separateVoucherTypes) continue;

            foreach (var baseType in eligibleVoucherTypes!
                         .Where(x => !string.IsNullOrWhiteSpace(x))
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
        // A preview only: supported Tally versions must confirm the exact
        // VOUCHERTYPE XML inheritance/numbering and live readback before posting.
        var xml = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(xml, companyName);
        foreach (var type in types.Where(x => x.Create))
        {
            xml.AppendLine("      <TALLYMESSAGE>");
            xml.Append("        <VOUCHERTYPE NAME=\"")
                .Append(SecurityElement.Escape(type.Name)).AppendLine("\" ACTION=\"Create\">");
            xml.Append("          <NAME>").Append(SecurityElement.Escape(type.Name)).AppendLine("</NAME>");
            xml.Append("          <PARENT>").Append(SecurityElement.Escape(type.BaseType)).AppendLine("</PARENT>");
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
    string GroupXml, string CashLedgerXml, string VoucherTypeXml)
{
    // Exact Tax Unit/GSTIN + MASTERID evidence from the selected destination
    // is a separate preflight dependency for GST transactions. Never infer it
    // from a branch's source-company Tax Unit display name.
    public IReadOnlyList<TallyConsolidatedTaxUnitAuthority> VerifiedTaxUnits { get; init; } = [];
}
