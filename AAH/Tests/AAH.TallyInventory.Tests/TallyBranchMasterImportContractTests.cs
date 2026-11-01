using AccountingAutomationHub.Infrastructure.Tally;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Domain.Masters;

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
        // Real 01-Nov Gujarat screenshot: no branch Tally company exists, so
        // offline XML was incorrectly blocked trying to CREATE Current Assets.
        // Source-only previews can use EXACT AAH standard Tally groups. They
        // must never create built-ins or treat foreign/imported/custom groups
        // as live. The linked consolidated target still verifies its own masters.
        Check(TallyAccountingVoucherExportService.IsCanonicalOfflineTallyBuiltInGroup(
            new MasterRecord { Type = "Group", Name = "Current Assets",
                Parent = "Primary", Source = "Standard hierarchy" }),
            "Offline Gujarat source could not recognize built-in Current Assets.");
        Check(TallyAccountingVoucherExportService.IsCanonicalOfflineTallyBuiltInGroup(
            new MasterRecord { Type = "Group", Name = "Sundry Debtors",
                Parent = "Current Assets", Source = "Standard hierarchy" }),
            "Offline source could not recognize nested predefined Tally groups.");
        Check(!TallyAccountingVoucherExportService.IsCanonicalOfflineTallyBuiltInGroup(
            new MasterRecord { Type = "Group", Name = "Current Assets",
                Parent = "Loans (Liability)", Source = "Standard hierarchy" }),
            "Changed built-in group parent passed an offline preview.");
        Check(!TallyAccountingVoucherExportService.IsCanonicalOfflineTallyBuiltInGroup(
            new MasterRecord { Type = "Group", Name = "Current Assets",
                Parent = "Primary", Source = "Tally imported" }),
            "Foreign Tally-sourced master was incorrectly authorized as a builtin.");
        Check(!TallyAccountingVoucherExportService.IsCanonicalOfflineTallyBuiltInGroup(
            new MasterRecord { Type = "Group", Name = "Custom Assets",
                Parent = "Primary", Source = "Standard hierarchy" }),
            "Arbitrary unverified custom group was accepted as Tally prebuilt.");
        Check(TallyAccountingVoucherExportService.IsCanonicalOfflineTallyBuiltInLedger(
            new MasterRecord { Type = "Ledger", Name = "Cash",
                Parent = "Cash-in-Hand", Source = "Standard hierarchy" }),
            "Offline Cash ledger source template was lost.");
        Check(!TallyAccountingVoucherExportService.IsCanonicalOfflineTallyBuiltInLedger(
            new MasterRecord { Type = "Ledger", Name = "Party Ledger",
                Parent = "Sundry Debtors", Source = "Standard hierarchy" }),
            "Offline party or GST master was invented from a standard label.");
        // Guard against accidentally enabling this exception for normal live
        // Tally export; it is explicitly opted into only from the private
        // saved branch's XML preparation path.
        DirectoryInfo? root = new(Environment.CurrentDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "AAH", "src",
                   "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs")))
            root = root.Parent;
        Check(root is not null, "Missing AAH desktop source for offline branch regression.");
        var source = File.ReadAllText(Path.Combine(root!.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs"));
        Check(source.Contains("offlineConsolidatedBranchSource: true", StringComparison.Ordinal) &&
              source.Contains("_branchXmlPreparationService.GenerateLiveXml(", StringComparison.Ordinal),
            "Offline-only exception not restricted to private branch source preparation.");

        Console.WriteLine("PASS: canonical offline Tally groups, source Cash template and protected real export.");
        Console.WriteLine("PASS: consolidated import live MASTERID, parent, duplicate and Tally-ack contracts.");
    }
}
