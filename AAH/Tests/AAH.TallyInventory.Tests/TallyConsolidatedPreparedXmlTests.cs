using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedPreparedXmlTests
{
    private const string Pan = "ABCDE1234F";
    private static readonly TallyBranchMasterIdentity Karnataka =
        new(101, "KA", "Karnataka", Pan, "29ABCDE1234F1Z5", "2025-26");
    private static readonly TallyBranchMasterIdentity TamilNadu =
        new(102, "TN", "Tamil Nadu", Pan, "33ABCDE1234F1Z5", "2025-26");

    private static MasterRecord Ledger(string name, string parent) =>
        new() { Name = name, Parent = parent, Type = "Ledger", Source = "Manual" };

    private static string Xml(string type, string number, string reference,
        string debitName, string creditName, string debit, string credit,
        string debitParent, string creditParent, bool altered = false) => $"""
        <ENVELOPE><HEADER><TALLYREQUEST>Import Data</TALLYREQUEST></HEADER>
        <BODY><IMPORTDATA><REQUESTDESC><REPORTNAME>All Masters</REPORTNAME></REQUESTDESC>
        <REQUESTDATA>
          <TALLYMESSAGE><LEDGER NAME="{debitName}" ACTION="Create">
            <NAME>{debitName}</NAME><PARENT>{debitParent}</PARENT></LEDGER></TALLYMESSAGE>
          <TALLYMESSAGE><LEDGER NAME="{creditName}" ACTION="Create">
            <NAME>{creditName}</NAME><PARENT>{creditParent}</PARENT></LEDGER></TALLYMESSAGE>
          <TALLYMESSAGE><VOUCHER ACTION="{(altered ? "Alter" : "Create")}" VCHTYPE="{type}">
            <DATE>20250505</DATE><VOUCHERNUMBER>{number}</VOUCHERNUMBER>
            <VOUCHERTYPENAME>{type}</VOUCHERTYPENAME><REFERENCE>{reference}</REFERENCE>
            <ALLLEDGERENTRIES.LIST><LEDGERNAME>{debitName}</LEDGERNAME><AMOUNT>{debit}</AMOUNT></ALLLEDGERENTRIES.LIST>
            <ALLLEDGERENTRIES.LIST><LEDGERNAME>{creditName}</LEDGERNAME><AMOUNT>{credit}</AMOUNT></ALLLEDGERENTRIES.LIST>
          </VOUCHER></TALLYMESSAGE>
        </REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>
        """;

    private static TallyBranchMasterPlan Plan() =>
        TallyBranchConsolidationMasterPlanner.Build(
            "Consolidated Books", "test-company", "2025-26", [Karnataka, TamilNadu],
            [], true);

    private static TallyPreparedBranchXml Prepared(TallyBranchMasterIdentity branch, string xml,
        MasterRecord a, MasterRecord b) => new(branch, xml, [a, b]);

    private static void Check(bool value, string why)
    {
        if (!value) throw new InvalidOperationException("Consolidated prepared XML: " + why);
    }
    private static void Reject(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe XML accepted: " + why);
    }
    public static void Run()
    {
        var a = Prepared(Karnataka,
            Xml("Sales", "1", "KA-INV-1", "Buyer", "Local Sales", "-100.25", "100.25", "Sundry Debtors", "Sales Accounts"),
            Ledger("Buyer", "Sundry Debtors"), Ledger("Local Sales", "Sales Accounts"));
        var b = Prepared(TamilNadu,
            Xml("Sales", "1", "TN-INV-1", "Buyer", "Local Sales", "-200.10", "200.10", "Sundry Debtors", "Sales Accounts"),
            Ledger("Buyer", "Sundry Debtors"), Ledger("Local Sales", "Sales Accounts"));

        var result = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [a, b]);
        Check(result.BranchCount == 2 && result.SourceVouchers == 2 && result.FinalVouchers == 2,
            "Voucher quantity was changed.");
        var sourceAmounts = new[] { "-100.25", "100.25", "-200.10", "200.10" }.Order().ToArray();
        var finalDoc = XDocument.Parse(result.VouchersXml);
        var amounts = finalDoc.Descendants("AMOUNT").Select(x => x.Value).Order().ToArray();
        Check(sourceAmounts.SequenceEqual(amounts), "One branch amount was recalculated or replaced.");
        Check(finalDoc.Descendants("VOUCHERNUMBER").Select(x => x.Value).ToArray()
            .SequenceEqual(new[] { "KA-1", "TN-1" }), "Branch voucher identity collided.");
        Check(finalDoc.Descendants("LEDGERNAME").Select(x => x.Value).ToHashSet()
            .SetEquals(new[] { "Buyer - Karnataka", "Local Sales - Karnataka",
                "Buyer - Tamil Nadu", "Local Sales - Tamil Nadu" }), "Branch ledger identity was lost.");
        var masterDoc = XDocument.Parse(result.MastersXml);
        Check(masterDoc.Descendants("LEDGER").Count() == 4 &&
              masterDoc.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Buyer - Karnataka" &&
                  (string?)x.Element("PARENT") == "Karnataka Debtors"),
              "Existing ledger grouping was not carried into master XML.");

        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [a]),
            "missing branch must stop the import");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a, b with { Branch = Karnataka }]), "wrong branch identity");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = a.Xml.Replace("<LEDGER NAME=\\\"Buyer\\\" ACTION=\\\"Create\\\">",
                "<LEDGER NAME=\\\"Not Buyer\\\" ACTION=\\\"Create\\\">") }, b ]), "missing source ledger master");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = Xml("Sales", "1", "KA-INV-1", "Buyer", "Local Sales",
                "-100.25", "100.25", "Sundry Debtors", "Sales Accounts", altered: true) }, b]),
            "foreign Tally Alter must never be replayed");

        var outgoing = Prepared(Karnataka,
            Xml("Payment", "21", "AAH-INTERBANK:TRANSFER-10", "Bank", "Clearing", "-500.00", "500.00",
                "Bank Accounts", "Current Liabilities"),
            Ledger("Bank", "Bank Accounts"), Ledger("Clearing", "Current Liabilities"));
        var incoming = Prepared(TamilNadu,
            Xml("Receipt", "22", "AAH-INTERBANK:TRANSFER-10", "Bank", "Clearing", "500.00", "-500.00",
                "Bank Accounts", "Current Liabilities"),
            Ledger("Bank", "Bank Accounts"), Ledger("Clearing", "Current Liabilities"));
        var transfer = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [outgoing, incoming]);
        Check(transfer.InterbankReconciled == 1 && transfer.SourceVouchers == 2 && transfer.FinalVouchers == 1,
            "Explicit interbank pair did not reconcile to one common Contra.");
        var bankDoc = XDocument.Parse(transfer.VouchersXml);
        Check(bankDoc.Descendants("AMOUNT").Select(x => x.Value).Order().SequenceEqual(
                  new[] { "-500.00", "500.00" }.Order()) &&
              bankDoc.Descendants("VOUCHERTYPENAME").Single().Value == "Contra",
            "Interbank reconciliation changed bank-side values or Contra type.");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            outgoing, incoming with { Xml = incoming.Xml.Replace("500.00", "499.00") }]),
            "mismatched interbank amount");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [outgoing, b]),
            "unmatched interbank reference");
        Console.WriteLine("PASS: branch-prepared XML, DR/CR immutability, group ledgers, explicit interbank and fail-closed identities.");
    }
}
