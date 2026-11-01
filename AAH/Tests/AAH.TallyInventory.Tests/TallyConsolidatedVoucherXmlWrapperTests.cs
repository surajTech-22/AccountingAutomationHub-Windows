using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedVoucherXmlWrapperTests
{
    private const string Pan = "ABCDE1234F";
    private const string Fy = "2025-26";
    private static readonly TallyBranchMasterIdentity Ka =
        new(101, "B-KA", "Karnataka", Pan, "29ABCDE1234F1Z5", Fy);
    private static readonly TallyBranchMasterIdentity Tn =
        new(102, "B-TN", "Tamil Nadu", Pan, "33ABCDE1234F1Z7", Fy);

    private static readonly TallyConsolidatedSourceLedger[] OriginalLedgers =
    [
        new("Customer A", "Sundry Debtors", "29ABCDE1234F1Z5", "Karnataka",
            "India", "560001", "Customer A"),
        new("Sales Local", "Sales Accounts")
    ];

    private static string Voucher(string unit, string number = "1", decimal credit = 1180m) => $"""
        <VOUCHER VCHTYPE="Sales" ACTION="Create" REMOTEID="SOURCE-DUPLICATE-1" VCHKEY="SOURCE-COMPANY-IDENTITY">
          <GUID>11111111-1111-1111-1111-111111111111</GUID>
          <MASTERID>9090</MASTERID>
          <ALTERID>88</ALTERID>
          <DATE>20250501</DATE>
          <VOUCHERNUMBER>{number}</VOUCHERNUMBER>
          <VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>
          <PARTYLEDGERNAME>Customer A</PARTYLEDGERNAME>
          <BASICBASEPARTYNAME>Customer A</BASICBASEPARTYNAME>
          <TAXUNITNAME>{unit}</TAXUNITNAME>
          <CMPGSTSTATE>Karnataka</CMPGSTSTATE>
          <ALLLEDGERENTRIES.LIST><LEDGERNAME>Customer A</LEDGERNAME><AMOUNT>-1180.00</AMOUNT></ALLLEDGERENTRIES.LIST>
          <ALLLEDGERENTRIES.LIST><LEDGERNAME>Sales Local</LEDGERNAME><AMOUNT>{credit}.00</AMOUNT></ALLLEDGERENTRIES.LIST>
        </VOUCHER>
        """;

    private static TallyBranchMasterPlan Masters(bool types = false) =>
        TallyBranchConsolidationMasterPlanner.Build(
            "Group Writable Company", "DESTINATION-ID", Fy, [Ka, Tn],
            Array.Empty<MasterRecord>(), true, types, false,
            types ? ["Sales", "Purchase", "Journal", "Contra", "Payment", "Receipt"] : null);

    private static TallyConsolidatedBranchXmlInput Input(
        TallyBranchMasterIdentity branch, string voucher, string unit,
        TallyConsolidatedTransferKind kind = TallyConsolidatedTransferKind.Ordinary,
        bool matched = false) => new(branch, OriginalLedgers,
        [
            new("Sales", "DOCUMENT-1",
                branch.BranchId == Tn.BranchId
                    ? voucher.Replace("<CMPGSTSTATE>Karnataka</CMPGSTSTATE>",
                        "<CMPGSTSTATE>Tamil Nadu</CMPGSTSTATE>", StringComparison.Ordinal)
                    : voucher,
                true, true, unit, kind, matched, "MATCH-001")
        ], branch.BranchId == Tn.BranchId ? "Tamil Nadu" : "Karnataka");

    private static void Check(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException("Consolidation: " + error);
    }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Consolidation did not block " + name);
    }

    public static void Run()
    {
        var first = Input(Ka, Voucher("Karnataka GST", "1"), "Karnataka GST");
        var second = Input(Tn, Voucher("Tamil Nadu GST", "1"), "Tamil Nadu GST");
        var groups = Masters();
        var preview = TallyConsolidatedVoucherXmlWrapper.Prepare(
            groups, [first, second], Array.Empty<MasterRecord>(), true);
        var document = XDocument.Parse(preview.VouchersXml);
        var vouchers = document.Descendants("VOUCHER").ToArray();
        Check(preview.VoucherCount == 2 && vouchers.Length == 2 &&
              document.Descendants("SVCURRENTCOMPANY").Single().Value == "Group Writable Company" &&
              document.Descendants("ID").Single().Value == "Vouchers",
            "Tally voucher import must have the original Data/Vouchers envelope (not All Masters)");
        Check(vouchers.Select(x => x.Element("GUID")!.Value).Distinct().Count() == 2 &&
              vouchers.All(x => !x.Descendants("MASTERID").Any() &&
                                !x.Descendants("ALTERID").Any() &&
                                x.Attribute("VCHKEY") is null && x.Attribute("REMOTEID") is null),
            "Foreign source voucher IDs leaked or branch GUIDs collided");
        Check(vouchers[0].Element("VOUCHERNUMBER")!.Value == "Karnataka/1" &&
              vouchers[1].Element("VOUCHERNUMBER")!.Value == "Tamil Nadu/1" &&
              vouchers[0].Descendants("LEDGERNAME").Any(x => x.Value == "Customer A - Karnataka") &&
              vouchers[1].Descendants("LEDGERNAME").Any(x => x.Value == "Customer A - Tamil Nadu") &&
              vouchers[0].Element("TAXUNITNAME")!.Value == "Karnataka GST" &&
              vouchers[1].Element("TAXUNITNAME")!.Value == "Tamil Nadu GST",
            "Ledger collision, source branch identity or existing GST registration was lost");
        Check(preview.BranchGroupsXml.Contains("Karnataka Debtors", StringComparison.Ordinal) &&
              preview.LedgerMappings.Any(x => x.DestinationLedger == "Customer A - Karnataka" &&
                                                x.DestinationParent == "Karnataka Debtors") &&
              preview.BranchLedgersXml.Contains("PARTYGSTIN", StringComparison.Ordinal) &&
              preview.BranchLedgersXml.Contains("29ABCDE1234F1Z5", StringComparison.Ordinal),
            "Mandatory accounting subgroups and existing rich GST party master writer were not reused");
        Check(TallyConsolidatedVoucherXmlWrapper.Prepare(groups, [first, second], [], true)
                  .VouchersXml == preview.VouchersXml,
            "Stable branch source identity must produce deterministic consolidated previews");
        Console.WriteLine("PASS: two branches, same voucher numbers, master-first ledger mapping and separate GST registrations.");

        var optional = TallyConsolidatedVoucherXmlWrapper.Prepare(Masters(true),
            [first, second], [], true);
        Check(XDocument.Parse(optional.VouchersXml).Descendants("VOUCHERTYPENAME")
            .Select(x => x.Value).SequenceEqual(["Sales - Karnataka", "Sales - Tamil Nadu"]),
            "optional all-type voucher configuration must apply to each branch");
        Console.WriteLine("PASS: optional branch-specific voucher types preserve original type hierarchy.");

        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [first, Input(Tn, Voucher("Tamil Nadu GST", "1", 1100m), "Tamil Nadu GST")], [], true),
            "unbalanced voucher");
        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [first, Input(Tn, Voucher("Tamil Nadu GST"), "Wrong GST unit")], [], true),
            "unverified GST tax-unit registration");
        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [first, Input(Tn, Voucher(""), "")], [], true),
            "missing GST tax-unit registration");
        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [first, second], [], false),
            "stale destination Tally authority");
        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [first, first], [], true),
            "duplicate durable branch identity");
        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [first, Input(Tn, Voucher("Tamil Nadu GST"), "Tamil Nadu GST",
                TallyConsolidatedTransferKind.Interbank)], [], true),
            "unmatched interbank Contra");
        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [first, second with { Ledgers = [new("Customer A", "Primary"), OriginalLedgers[1]] }], [], true),
            "unverified accounting parent classification");
        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [first, second with { Vouchers = [second.Vouchers.First(), second.Vouchers.First()] }], [], true),
            "duplicate source voucher");
        var savedXml = new AccountingAutomationHub.Domain.Tally.TallyLiveAccountingXmlSnapshot
        {
            Xml = "<ENVELOPE><BODY><IMPORTDATA><REQUESTDATA><TALLYMESSAGE>" +
                  Voucher("Karnataka GST") +
                  "</TALLYMESSAGE></REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>",
            Selected = 1, Ready = 1,
            SourceAnchors = ["AAH|GSTJ|101|2025-26|2025-04"],
            SourceEntityTypes = ["GST Journal"]
        };
        var adapted = TallyConsolidatedSourcePreviewAdapter.Extract(Ka, savedXml,
        [
            new MasterRecord { Type = "Ledger", Name = "Customer A", Parent = "Sundry Debtors" },
            new MasterRecord { Type = "Ledger", Name = "Sales Local", Parent = "Sales Accounts" }
        ], "Karnataka");
        Check(adapted.Vouchers.Count == 1 && adapted.Ledgers.Count == 2 &&
              adapted.Vouchers.Single().IsSavedSource &&
              adapted.Vouchers.Single().SourceAnchor == "AAH|GSTJ|101|2025-26|2025-04",
            "Already-prepared source vouchers must be read without generating GST journals.");
        var wrongParent = TallyConsolidatedSourcePreviewAdapter.Extract(Ka,
            savedXml, [new MasterRecord { Type = "Ledger", Name = "Customer A", Parent = "Primary" },
                       new MasterRecord { Type = "Ledger", Name = "Sales Local", Parent = "Sales Accounts" }]);
        Reject(() => TallyConsolidatedVoucherXmlWrapper.Prepare(groups,
            [wrongParent, second], [], true),
            "branch master source-parent conflict");
        Console.WriteLine("PASS: branch adapter reads original saved voucher XML and exact existing ledger parents.");
        Console.WriteLine("PASS: fail-closed master, source, transfer, GST and debit/credit verification.");
    }
}
