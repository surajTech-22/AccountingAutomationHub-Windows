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
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml, SourceStatus, "CL-KA", "2025-26", Destination,
            new TallyClientLink
            {
                ClientId = 101,
                CompanyKey = "consolidated-1",
                CompanyName = "Working Company",
                FinancialYear = "2025-26"
            }), "consolidated target reused as source link");
        Console.WriteLine("PASS: source-owned Karnataka/Gujarat XML needs no separate Tally company link.");
    }
}
