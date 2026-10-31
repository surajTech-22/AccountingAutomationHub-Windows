using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyBranchMasterPlannerTests
{
    private const string Pan = "ABCDE1234F";
    private const string Fy = "2025-26";
    private static readonly TallyBranchMasterIdentity[] Branches =
    [
        new(101, "KA-1", "Karnataka", Pan, "29ABCDE1234F1Z5", Fy),
        new(102, "TN-1", "Tamil Nadu", Pan, "33ABCDE1234F1Z7", Fy)
    ];
    private static readonly string[] VoucherTypes =
        ["Sales", "Purchase", "Payment", "Receipt", "Journal", "Debit Note", "Credit Note", "Contra"];

    private static TallyBranchMasterPlan Build(
        IReadOnlyCollection<TallyBranchMasterIdentity>? branches = null,
        IReadOnlyCollection<MasterRecord>? existing = null,
        bool verified = true, bool voucherTypes = false, bool separateCash = false,
        IReadOnlyCollection<string>? eligible = null) =>
        TallyBranchConsolidationMasterPlanner.Build(
            "ABC Consolidated", "LIVE-DEST-123", Fy,
            branches ?? Branches, existing ?? Array.Empty<MasterRecord>(), verified,
            voucherTypes, separateCash, eligible);

    private static void Check(bool yes, string reason)
    {
        if (!yes) throw new InvalidOperationException("Group Tally master preflight: " + reason);
    }

    private static void Reject(Action action, string reason)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected fail-closed group master preflight: " + reason);
    }

    public static void Run()
    {
        // The branch ledger hierarchy is mandatory even if BOTH optional settings
        // are disabled. Its masters must be produced by the ORIGINAL group writer.
        var required = Build();
        TallyBranchConsolidationMasterPlanner.ValidatePlanXml(required);
        Check(required.Groups.Count > 20 &&
              required.Groups.Select(x => x.BranchId).Distinct().Count() == 2 &&
              required.Groups.All(x => x.Create) &&
              required.CashLedgers.Count == 0 &&
              required.VoucherTypes.Count == 0,
              "disabling optional master types must NOT disable branch groups");
        var groupDoc = XDocument.Parse(required.GroupXml);
        Check(groupDoc.Descendants("GROUP").Count() == required.Groups.Count &&
              !groupDoc.Descendants("VOUCHER").Any() &&
              required.Groups.Any(x => x.Parent == "Sundry Creditors" && x.Name == "Karnataka Creditors") &&
              required.Groups.Any(x => x.Parent == "Sundry Creditors" && x.Name == "Tamil Nadu Creditors") &&
              required.Groups.Any(x => x.Parent == "Bank Accounts" && x.Name == "Karnataka Banks") &&
              required.Groups.Any(x => x.Parent == "Bank Accounts" && x.Name == "Tamil Nadu Banks"),
              "branch grouping must be under proper Tally accounting parents");
        Check(required.Groups.All(x => Branches.Any(b => b.BranchId == x.BranchId && b.Gstin == x.Gstin)),
              "every group must retain the originating branch and GSTIN");
        Check(required.DestinationCompanyKey == "LIVE-DEST-123" &&
              groupDoc.Descendants("SVCURRENTCOMPANY").Single().Value == "ABC Consolidated",
              "Tally company identity must not reuse branch company link");
        Console.WriteLine("PASS: compulsory branch groups independent of optional voucher types or cash ledgers.");

        var optional = Build(voucherTypes: true, separateCash: true, eligible: VoucherTypes);
        TallyBranchConsolidationMasterPlanner.ValidatePlanXml(optional);
        Check(optional.Groups.Count == required.Groups.Count &&
              optional.CashLedgers.Count == 2 &&
              optional.VoucherTypes.Count == VoucherTypes.Length * 2 &&
              optional.VoucherTypes.Any(x => x.BaseType == "Debit Note" && x.Name == "Debit Note - Karnataka") &&
              optional.VoucherTypes.Any(x => x.BaseType == "Journal" && x.Name == "Journal - Tamil Nadu") &&
              optional.VoucherTypes.Any(x => x.BaseType == "Contra" && x.Name == "Contra - Karnataka"),
              "when enabled ALL selected voucher types, not just Sales/Purchase, must be branch-specific");
        Check(XDocument.Parse(optional.CashLedgerXml).Descendants("LEDGER").Count() == 2 &&
              XDocument.Parse(optional.VoucherTypeXml).Descendants("VOUCHERTYPE").Count() == VoucherTypes.Length * 2,
              "optional cash and voucher XML previews must not be coupled to group XML");
        Console.WriteLine("PASS: optional ALL voucher types and optional branch cash ledger XML preview.");

        // A fresh Tally MASTERID under the exact correct parent is reused; a
        // name-only collision, unknown parent or wrong type is NOT acceptable.
        var verifiedExisting = required.Groups.Select((g, i) => new MasterRecord
        {
            Name = g.Name, Parent = g.Parent, Type = "Group",
            TallyMasterId = (50000 + i).ToString()
        }).ToArray();
        var reused = Build(existing: verifiedExisting);
        TallyBranchConsolidationMasterPlanner.ValidatePlanXml(reused);
        Check(reused.Groups.All(x => !x.Create) &&
              !XDocument.Parse(reused.GroupXml).Descendants("GROUP").Any(),
              "verified destination groups must be reused, never created twice");
        Reject(() => Build(existing: [new MasterRecord {
            Name = "Karnataka Banks", Parent = "Sundry Creditors",
            Type = "Group", TallyMasterId = "123"
        }]), "incorrect parent group cannot be silently reused");
        Reject(() => Build(existing: [new MasterRecord {
            Name = "Karnataka Banks", Parent = "Bank Accounts",
            Type = "Group", TallyMasterId = ""
        }]), "unverified destination group name cannot imply MASTERID binding");
        Console.WriteLine("PASS: fresh destination MASTERID idempotency and conflict protection.");

        Reject(() => Build(verified: false), "stale Tally authority");
        Reject(() => Build(branches: [Branches[0]]), "missing second branch");
        Reject(() => Build(branches: [Branches[0], Branches[0] with { BranchId = "KA-2" }]), "duplicate GSTIN");
        Reject(() => Build(branches: [Branches[0], Branches[1] with { Pan = "ABCDE1234G" }]), "different PAN");
        Reject(() => Build(branches: [Branches[0], Branches[1] with { FinancialYear = "2026-27" }]), "cross FY");
        Reject(() => Build(branches: [Branches[0], Branches[1] with { DisplayName = "Karnataka" }]), "duplicate group names");
        Reject(() => Build(voucherTypes: true), "voucher type catalog missing");
        Console.WriteLine("PASS: single PAN/FY/GST identity verification and fail-closed XML preflight.");
    }
}
