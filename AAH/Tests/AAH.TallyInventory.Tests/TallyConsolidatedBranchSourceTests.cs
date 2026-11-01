using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedBranchSourceTests
{
    private const string PreparedXml = """
        <ENVELOPE><HEADER><TALLYREQUEST>Import Data</TALLYREQUEST></HEADER>
        <BODY><IMPORTDATA><REQUESTDESC>
          <REPORTNAME>All Masters</REPORTNAME>
          <STATICVARIABLES><SVCURRENTCOMPANY>Working Company</SVCURRENTCOMPANY></STATICVARIABLES>
        </REQUESTDESC><REQUESTDATA>
          <TALLYMESSAGE><VOUCHER ACTION="Create"><DATE>20250501</DATE></VOUCHER></TALLYMESSAGE>
        </REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>
        """;
    private const string SourceStatus = "Accounting Automation Hub - Live Tally XML\r\nClient: CL-KA | FY 2025-26\r\n";
    private static readonly TallyCompanyInfo Destination = new()
    {
        CompanyKey = "consolidated-1",
        Name = "Consolidated Books",
        FinancialYear = "2025-26"
    };

    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException("Consolidated branch source: " + reason);
    }

    private static void Reject(Action check, string reason)
    {
        try { check(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unverified branch XML allowed: " + reason);
    }

    public static void Run()
    {
        // Regression for 01-Nov screenshot: Karnataka and Gujarat are private
        // AAH accounting scopes, NOT Tally companies needing independent links.
        var name = TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, SourceStatus, "CL-KA", "2025-26", Destination, null);
        Check(name == "Working Company", "Unlinked branch must still permit owned prepared XML.");
        Check(TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml.Replace("<DATE>20250501</DATE>",
                "<NARRATION>Payment&#x04; memo</NARRATION><DATE>20250501</DATE>"),
            SourceStatus, "CL-KA", "2025-26", Destination, null) == "Working Company",
            "Source-branch validator failed to repair 0x04 in free-text narration.");
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml.Replace("<DATE>20250501</DATE>", "<DATE>2025&#x04;0501</DATE>"),
            SourceStatus, "CL-KA", "2025-26", Destination, null),
            "Source-branch validator altered protected voucher date.");

        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, "Client: CL-GJ | FY 2025-26", "CL-KA", "2025-26", Destination, null),
            "wrong branch's prepared XML must be rejected");
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, SourceStatus, "CL-KA", "2024-25", Destination, null),
            "wrong financial year must be rejected");
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, "", "CL-KA", "2025-26", Destination, null),
            "orphan prepared XML cannot be accepted");

        // Existing optional source Tally linking is still supported, and may
        // yield extra original master metadata, but must never target the
        // writable consolidated company.
        var ownLink = new TallyClientLink
        {
            ClientId = 101, CompanyKey = "ka-branch",
            CompanyName = "Working Company",
            FinancialYear = "2025-26"
        };
        Check(TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, SourceStatus, "CL-KA", "2025-26", Destination, ownLink) == "Working Company",
            "Optional genuine branch Tally link was rejected.");
        Check(ReferenceEquals(
            TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
                ownLink, 101, "2025-26", Destination, "Working Company"), ownLink),
            "A fresh matching branch link was unnecessarily discarded.");

        // The real consolidated screenshot had a verified parent FY 2025-26,
        // but an older optional branch link held a DIFFERENT FY. That link
        // must not block source-owned branch XML and cannot lend old masters.
        var staleFy = new TallyClientLink
        {
            ClientId = 101, CompanyKey = "ka-branch",
            CompanyName = "Working Company", FinancialYear = "2024-25"
        };
        var validSourceCompany = TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, SourceStatus, "CL-KA", "2025-26", Destination, null);
        Check(validSourceCompany == "Working Company" &&
            TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
                staleFy, 101, "2025-26", Destination, validSourceCompany) is null,
            "A stale branch Tally FY must be ignored, not used for master readback or block prepared XML.");
        Check(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
            ownLink, 102, "2025-26", Destination, validSourceCompany) is null,
            "A source Tally link from another private branch was accepted.");
        Check(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
            ownLink, 101, "2025-26", Destination, "Different Company") is null,
            "A source Tally link mismatching the prepared branch XML was accepted.");
        Check(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
            new TallyClientLink
            {
                ClientId = 101, CompanyKey = "consolidated-1",
                CompanyName = "Working Company", FinancialYear = "2025-26"
            }, 101, "2025-26", Destination, validSourceCompany) is null,
            "The writable consolidated Tally company was used as a branch master source.");
        // Ownership still cannot be bypassed by changing the status/FY,
        // even when no optional source Tally link is available.
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, "Client: CL-KA | FY 2024-25", "CL-KA", "2025-26",
            Destination, null), "A stale source-owned branch XML FY was accepted.");
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, SourceStatus, "CL-KA", "2025-26", Destination,
            new TallyClientLink
            {
                ClientId = 101,
                CompanyKey = "consolidated-1",
                CompanyName = "Working Company",
                FinancialYear = "2025-26"
            }), "consolidated target reused as source link");
        // Tally may omit FinancialYear in an otherwise correctly linked live
        // company. The Dashboard should show the saved Client/FY, explicitly
        // labelled as AAH-linked, rather than an unexplained dash.
        DirectoryInfo? root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null &&
            !File.Exists(Path.Combine(root.FullName, "AAH", "src",
                "AccountingAutomationHub.Desktop", "ViewModels", "DashboardViewModel.cs")))
            root = root.Parent;
        Check(root is not null, "Dashboard source missing for linked-FY display regression.");
        var dashboard = File.ReadAllText(Path.Combine(root!.FullName, "AAH",
            "src", "AccountingAutomationHub.Desktop", "ViewModels", "DashboardViewModel.cs"));
        Check(dashboard.Contains("!string.IsNullOrWhiteSpace(openCompany?.FinancialYear)", StringComparison.Ordinal)
            && dashboard.Contains("link.FinancialYear + \" (AAH-linked)\"", StringComparison.Ordinal),
            "Missing safe FY fallback when the live Tally company omits FY metadata.");

        Console.WriteLine("PASS: source-owned Karnataka/Gujarat XML needs no separate Tally company link.");
    }
}
