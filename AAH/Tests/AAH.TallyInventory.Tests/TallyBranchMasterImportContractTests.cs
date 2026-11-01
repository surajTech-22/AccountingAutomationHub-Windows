using AccountingAutomationHub.Infrastructure.Tally;
using AccountingAutomationHub.Domain.Tally;

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
        // A linked Tally company often reports FY as blank (shown as — in
        // Dashboard). Its saved Client/FY link still supplies the accounting
        // period, but a contradictory reported FY must never be ignored.
        var link = new TallyClientLink
        {
            ClientId = 123,
            CompanyKey = "KEY-ACME",
            CompanyName = "Acme Consolidated",
            FinancialYear = "2025-26"
        };
        var destination = new TallyCompanyInfo
        {
            CompanyKey = "KEY-ACME",
            Name = "Acme Consolidated",
            FinancialYear = "",
            BooksFrom = "01-04-2025"
        };
        Check(TallyBranchConsolidationImportService.RequireConfirmedDestination(link, destination) == "2025-26",
            "Missing live FY metadata prevented already-linked Tally Client/FY import.");
        Reject(() => TallyBranchConsolidationImportService.RequireConfirmedDestination(link,
            new TallyCompanyInfo { CompanyKey = "WRONG", Name = destination.Name }),
            "different Tally company key was accepted");
        Reject(() => TallyBranchConsolidationImportService.RequireConfirmedDestination(link,
            new TallyCompanyInfo { CompanyKey = destination.CompanyKey, Name = destination.Name, FinancialYear = "2024-25" }),
            "contradictory Tally FY was accepted");
        Reject(() => TallyBranchConsolidationImportService.RequireConfirmedDestination(
            new TallyClientLink { ClientId = link.ClientId, CompanyKey = link.CompanyKey, CompanyName = link.CompanyName },
            destination), "blank authoritative Client/FY accepted");

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
            xml.Replace("<GROUP NAME=\"Karnataka Banks\"><PARENT>Bank Accounts</PARENT><MASTERID>101</MASTERID></GROUP>", "<GROUP NAME=\"Karnataka Banks\"><PARENT>Bank Accounts</PARENT><MASTERID>999</MASTERID></GROUP>")),
            "same-name conflicting MASTERIDs");
        Reject(() => TallyBranchConsolidationImportService.ReadLiveMasters(
            xml.Replace("<GROUP NAME=\"Karnataka Banks\"><PARENT>Bank Accounts</PARENT><MASTERID>101</MASTERID></GROUP>",
                        "<GROUP NAME=\"Karnataka Banks\"><PARENT>Sundry Creditors</PARENT><MASTERID>101</MASTERID></GROUP>")),
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
