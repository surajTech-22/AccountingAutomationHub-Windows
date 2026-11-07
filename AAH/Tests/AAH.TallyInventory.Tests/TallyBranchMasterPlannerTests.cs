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
    // A real linked Tally master list supplies positive MASTERIDs for the
    // ORIGINAL base types before AAH can create any branch child. The old
    // synthetic test used only a name catalog and was not representative.
    private static readonly MasterRecord[] LiveBaseTypes =
        VoucherTypes.Select((name, i) => new MasterRecord
        {
            Type = "Voucher Type", Name = name, Parent = name,
            TallyMasterId = (700 + i).ToString()
        }).ToArray();

    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> UsedTypes =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["KA-1"] = ["Sales", "Journal", "Debit Note"],
            ["TN-1"] = ["Purchase", "Receipt", "Contra"]
        };

    private static TallyBranchMasterPlan Build(
        IReadOnlyCollection<TallyBranchMasterIdentity>? branches = null,
        IReadOnlyCollection<MasterRecord>? existing = null,
        bool verified = true, bool voucherTypes = false, bool separateCash = false,
        IReadOnlyCollection<string>? eligible = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? used = null) =>
        TallyBranchConsolidationMasterPlanner.Build(
            "ABC Consolidated", "LIVE-DEST-123", Fy,
            branches ?? Branches, existing ?? Array.Empty<MasterRecord>(), verified,
            voucherTypes, separateCash, eligible, used);

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

        var optional = Build(voucherTypes: true, separateCash: true,
            existing: LiveBaseTypes, eligible: VoucherTypes, used: UsedTypes);
        TallyBranchConsolidationMasterPlanner.ValidatePlanXml(optional);
        Check(optional.Groups.Count == required.Groups.Count &&
              optional.CashLedgers.Count == 2 &&
              optional.VoucherTypes.Count == 6 &&
              optional.VoucherTypes.Any(x => x.BaseType == "Debit Note" && x.Name == "Debit Note - Karnataka") &&
              optional.VoucherTypes.Any(x => x.BaseType == "Journal" && x.Name == "Journal - Karnataka") &&
              optional.VoucherTypes.Any(x => x.BaseType == "Contra" && x.Name == "Contra - Tamil Nadu") &&
              !optional.VoucherTypes.Any(x => x.Name == "Payment - Karnataka" ||
                  x.Name == "Sales - Tamil Nadu" || x.Name == "Credit Note - Tamil Nadu"),
              "create only actually used branch voucher subtypes, each under its original parent");
        Check(XDocument.Parse(optional.CashLedgerXml).Descendants("LEDGER").Count() == 2 &&
              XDocument.Parse(optional.VoucherTypeXml).Descendants("VOUCHERTYPE").Count() == 6 &&
              XDocument.Parse(optional.VoucherTypeXml).Descendants("VOUCHERTYPE")
                  .All(v => optional.VoucherTypes.Any(t => t.Name == (string?)v.Attribute("NAME") &&
                      t.BaseType == (string?)v.Element("PARENT") &&
                      (string?)v.Element("NUMBERINGMETHOD") == "None")),
              "only used optional subtype masters under their real parents in the unchanged XML layout");
        Console.WriteLine("PASS: optional used-only branch voucher types and original parent hierarchy.");
        var xmlUsage = TallyBranchConsolidationMasterPlanner.ReadUsedVoucherTypes(
        [
            new TallyPreparedBranchXml(Branches[0], """
                <ENVELOPE><BODY><IMPORTDATA><REQUESTDATA>
                <TALLYMESSAGE><VOUCHER ACTION="Create"><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME></VOUCHER></TALLYMESSAGE>
                <TALLYMESSAGE><VOUCHER ACTION="Alter"><VOUCHERTYPENAME>Journal</VOUCHERTYPENAME></VOUCHER></TALLYMESSAGE>
                <TALLYMESSAGE><VOUCHER ACTION="Create"><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME></VOUCHER></TALLYMESSAGE>
                <TALLYMESSAGE><VOUCHER ACTION="Create"><REFERENCE>AAH-INTERBANK:TRANSFER-X</REFERENCE><VOUCHERTYPENAME>Payment</VOUCHERTYPENAME></VOUCHER></TALLYMESSAGE>
                </REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>
                """, Array.Empty<MasterRecord>()),
            new TallyPreparedBranchXml(Branches[1], """
                <ENVELOPE><BODY><IMPORTDATA><REQUESTDATA>
                <TALLYMESSAGE><VOUCHER ACTION="Create"><VOUCHERTYPENAME>Purchase</VOUCHERTYPENAME></VOUCHER></TALLYMESSAGE>
                <TALLYMESSAGE><VOUCHER ACTION="Create"><REFERENCE>AAH-INTERBANK:TRANSFER-X</REFERENCE><VOUCHERTYPENAME>Receipt</VOUCHERTYPENAME></VOUCHER></TALLYMESSAGE>
                </REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>
                """, Array.Empty<MasterRecord>())
        ]);
        Check(xmlUsage["KA-1"].Count == 2 &&
              xmlUsage["KA-1"].Contains("Sales") &&
              xmlUsage["KA-1"].Contains("Journal") &&
              xmlUsage["TN-1"].SequenceEqual(["Purchase"]),
              "actual prepared XML must drive minimal unique type planning; " +
              "matched interbank source Payment/Receipt must not create unused subtypes");
        Reject(() => Build(voucherTypes: true, eligible: VoucherTypes),
            "full Tally catalog without source usage must not multiply voucher types");
        Reject(() => Build(voucherTypes: true, existing: LiveBaseTypes,
            eligible: VoucherTypes,
            used: new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["KA-1"] = ["Sales"],
                ["TN-1"] = ["Unknown Voucher Type"]
            }), "unknown base type must not become a subtype");
        Console.WriteLine("PASS: original XML usage, no unused/unknown voucher type proliferation.");

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
        var sameRegistration = Build(branches:
        [
            Branches[0],
            Branches[0] with { ClientId = 103, BranchId = "KA-2", DisplayName = "Mysuru" }
        ]);
        Check(sameRegistration.Groups.Any(x => x.Name == "Mysuru Banks") &&
              sameRegistration.Groups.Any(x => x.Name == "Karnataka Banks"),
              "Two physical branches under one GSTIN must retain different ledger groups.");
        var noRegistration = Build(branches:
        [
            Branches[0],
            Branches[1] with { Gstin = "" }
        ]);
        Check(noRegistration.Groups.Any(x => x.BranchId == "TN-1"),
              "Name-only branches under one PAN must also receive mandatory groups.");
        Reject(() => Build(branches: [Branches[0], Branches[0]]), "duplicate branch identity");
        Reject(() => Build(branches: [Branches[0], Branches[1] with { Pan = "ABCDE1234G" }]), "different PAN");
        Reject(() => Build(branches: [Branches[0], Branches[1] with { FinancialYear = "2026-27" }]), "cross FY");
        Reject(() => Build(branches: [Branches[0], Branches[1] with { DisplayName = "Karnataka" }]), "duplicate group names");
        Reject(() => Build(voucherTypes: true), "voucher type catalog missing");
        Console.WriteLine("PASS: single PAN/FY/GST identity verification and fail-closed XML preflight.");
    }
}
