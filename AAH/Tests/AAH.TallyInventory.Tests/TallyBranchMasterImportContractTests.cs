using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyBranchMasterImportContractTests
{
    private static void Check(bool test, string what)
    {
        if (!test) throw new InvalidOperationException("Consolidated master live contract: " + what);
    }

    private static void Reject(Action action, string what)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected fail-closed result: " + what);
    }

    public static void Run()
    {
        // Tally can repeat masters in multiple exported Collection snapshots.
        // Repeated identical identities must not trigger an overwrite.
        const string xml = """
        <AAHMASTEREXPORT>
          <RESPONSE>
            <GROUP NAME="Karnataka Banks">
              <NAME>Karnataka Banks</NAME><PARENT>Bank Accounts</PARENT><MASTERID>101</MASTERID>
            </GROUP>
            <LEDGER NAME="Cash - Karnataka"><PARENT>Cash-in-Hand</PARENT><MASTERID>102</MASTERID></LEDGER>
            <VOUCHERTYPE NAME="Sales - Karnataka"><PARENT>Sales</PARENT><MASTERID>103</MASTERID></VOUCHERTYPE>
          </RESPONSE>
          <RESPONSE>
            <GROUP NAME="Karnataka Banks"><PARENT>Bank Accounts</PARENT><MASTERID>101</MASTERID></GROUP>
          </RESPONSE>
        </AAHMASTEREXPORT>
        """;
        var parsed = TallyBranchConsolidationImportService.ReadLiveMasters(xml);
        Check(parsed.Count == 3, "Repeated Collection responses duplicated one master.");
        Check(parsed.Single(x => x.Name == "Karnataka Banks").TallyMasterId == "101" &&
              parsed.Single(x => x.Name == "Karnataka Banks").Parent == "Bank Accounts",
              "Parent and MASTERID read-back must survive multiple collections.");
        Check(parsed.Any(x => x.Name == "Sales - Karnataka" && x.Type == "Voucher Type"),
              "Optional voucher type must use live Tally authority.");

        Reject(() => TallyBranchConsolidationImportService.ReadLiveMasters(
            xml.Replace("<GROUP NAME=\\"Karnataka Banks\\"><PARENT>Bank Accounts</PARENT><MASTERID>101</MASTERID></GROUP>", "<GROUP NAME=\\"Karnataka Banks\\"><PARENT>Bank Accounts</PARENT><MASTERID>999</MASTERID></GROUP>")),
            "same-name conflicting MASTERIDs");
        Reject(() => TallyBranchConsolidationImportService.ReadLiveMasters(
            xml.Replace("<GROUP NAME=\\"Karnataka Banks\\"><PARENT>Bank Accounts</PARENT><MASTERID>101</MASTERID></GROUP>",
                        "<GROUP NAME=\\"Karnataka Banks\\"><PARENT>Sundry Creditors</PARENT><MASTERID>101</MASTERID></GROUP>")),
            "same-name conflicting parents");
        Reject(() => TallyBranchConsolidationImportService.ReadLiveMasters("<RESPONSE/>"),
            "empty live master read-back");

        TallyBranchConsolidationImportService.RequireOneCreate(
            TallyHttpService.ParseImportResponse(
                "<RESPONSE><CREATED>1</CREATED><ERRORS>0</ERRORS><IGNORED>0</IGNORED></RESPONSE>"),
            "Karnataka Banks");
        Reject(() => TallyBranchConsolidationImportService.RequireOneCreate(
            TallyHttpService.ParseImportResponse(
                "<RESPONSE><CREATED>1</CREATED><ERRORS>1</ERRORS><LINEERROR>Rejected</LINEERROR></RESPONSE>"),
            "Karnataka Banks"), "partial Tally import");
        Reject(() => TallyBranchConsolidationImportService.RequireOneCreate(
            TallyHttpService.ParseImportResponse(
                "<RESPONSE><CREATED>0</CREATED><IGNORED>1</IGNORED><ERRORS>0</ERRORS></RESPONSE>"),
            "Karnataka Banks"), "duplicate/ignored master cannot be accepted");
        Reject(() => TallyBranchConsolidationImportService.RequireOneCreate(
            TallyHttpService.ParseImportResponse("<RESPONSE>Unknown Request</RESPONSE>"),
            "Karnataka Banks"), "protocol rejection");
        Console.WriteLine("PASS: consolidated import live MASTERID, parent, duplicate and Tally-ack contracts.");
    }
}
