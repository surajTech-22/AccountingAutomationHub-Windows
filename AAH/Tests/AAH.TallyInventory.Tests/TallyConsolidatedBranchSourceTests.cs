using System.Reflection;
using System.Text;
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

        // The original branch Live XML timer is link-gated, whereas source
        // branches in consolidated accounting are NOT required to link a
        // second Tally company. The consolidated command must regenerate an
        // absent branch preview from its own saved books, without posting.
        var offlineGujaratXml = PreparedXml.Replace("Working Company", "AAH Prepared Branch CL-GJ");
        var gujaratStatus = "Accounting Automation Hub - Live Tally XML\n" +
            "Source: saved offline branch accounting; no Tally posting\n" +
            "Client: CL-GJ | FY 2025-26\n";
        Check(TallyConsolidatedBranchSourcePolicy.Validate(
            offlineGujaratXml, gujaratStatus, "CL-GJ", "2025-26", Destination, null) ==
            "AAH Prepared Branch CL-GJ",
            "A correctly owned, offline prepared Gujarat branch must not require a branch Tally link.");
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            offlineGujaratXml, gujaratStatus, "CL-KA", "2025-26", Destination, null),
            "Gujarat XML cannot be reused as Karnataka's source.");
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            offlineGujaratXml, gujaratStatus.Replace("CL-GJ", "CL-KA"),
            "CL-KA", "2025-26", Destination, null),
            "An offline XML's synthetic destination must match its own branch ClientCode, not just an altered status file.");
        Check(ReferenceEquals(
            TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
                ownLink, 101, "2025-26", Destination, "AAH Prepared Branch CL-KA"), ownLink),
            "A source-owned offline XML must not discard a distinct verified optional branch Tally master link.");
        Check(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
            staleFy, 101, "2025-26", Destination, "AAH Prepared Branch CL-KA") is null,
            "An offline XML bypassed the saved optional Tally link's Client/FY guard.");
        Check(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
            new TallyClientLink {
                ClientId = 101, CompanyKey = Destination.CompanyKey,
                CompanyName = Destination.Name, FinancialYear = "2025-26"
            }, 101, "2025-26", Destination, "AAH Prepared Branch CL-KA") is null,
            "An offline XML used the writable consolidated Tally destination as an optional branch source.");
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            PreparedXml.Replace("<TALLYMESSAGE><VOUCHER ACTION=\"Create\"><DATE>20250501</DATE></VOUCHER></TALLYMESSAGE>", ""),
            SourceStatus, "CL-KA", "2025-26", Destination, null),
            "An empty linked Live XML must never be treated as prepared branch accounting.");

        // Exercise the actual Tally Excel-parity XML writer used by normal
        // GenerateLiveXml, rather than a hand-written import envelope fixture.
        // Normal AAH intentionally leaves Journal VOUCHERNUMBER empty and
        // preserves its stable AAH-owned REFERENCE for Tally auto-numbering.
        var writer = typeof(TallyAccountingVoucherExportService).Assembly.GetType(
            "AccountingAutomationHub.Infrastructure.Tally.TallyExcelParityXmlWriter", throwOnError: true)!;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var xmlBuilder = new StringBuilder();
        writer.GetMethod("BeginAllMastersImport", flags)!.Invoke(null, [xmlBuilder, "AAH Prepared Branch CL-KA"]);
        writer.GetMethod("BeginVoucher", flags)!.Invoke(null, [
            xmlBuilder, "Journal", "Accounting Voucher View", new DateTime(2025, 5, 1),
            null, "", "AAH|GSTJ|101|2025-26|2025-05", ""
        ]);
        writer.GetMethod("EndVoucher", flags)!.Invoke(null, [xmlBuilder]);
        writer.GetMethod("EndAllMastersImport", flags)!.Invoke(null, [xmlBuilder]);
        var originalWriterXml = xmlBuilder.ToString();
        TallyConsolidatedBranchSourcePolicy.RequirePreparedVoucherCount(
            originalWriterXml, 1, "CL-KA");
        Check(TallyConsolidatedBranchSourcePolicy.Validate(
            originalWriterXml, SourceStatus, "CL-KA", "2025-26", Destination, null) ==
            "AAH Prepared Branch CL-KA",
            "Existing branch XML writer generated a valid source that the consolidated validator rejected.");
        Reject(() => TallyConsolidatedBranchSourcePolicy.RequirePreparedVoucherCount(
            originalWriterXml, 2, "CL-KA"),
            "Consolidation accepted a different number of voucher messages from the original writer.");
        Reject(() => TallyConsolidatedBranchSourcePolicy.RequirePreparedVoucherCount(
            originalWriterXml.Replace("<VOUCHER", "<NOTAVOUCHER")
                .Replace("</VOUCHER>", "</NOTAVOUCHER>"), 1, "CL-KA"),
            "Consolidation accepted a stale source with no valid voucher messages.");
        Reject(() => TallyConsolidatedBranchSourcePolicy.Validate(
            originalWriterXml.Replace("<VOUCHER", "<NOTAVOUCHER")
                .Replace("</VOUCHER>", "</NOTAVOUCHER>"), SourceStatus,
            "CL-KA", "2025-26", Destination, null),
            "Source validator accepted an XML envelope with no original vouchers.");

        var mainWindow = File.ReadAllText(Path.Combine(root.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs"));
        Check(mainWindow.Contains("var (xml, statusText) = await PrepareMissingBranchXmlAsync(", StringComparison.Ordinal) &&
            mainWindow.Contains("child, branch.Name, financialYear);", StringComparison.Ordinal) &&
            !mainWindow.Contains("if (existing.Length == 0)", StringComparison.Ordinal) &&
            !mainWindow.Contains("var candidates = new List<string>();", StringComparison.Ordinal) &&
            mainWindow.Contains("_branchXmlPreparationService.GenerateLiveXml(", StringComparison.Ordinal) &&
            mainWindow.Contains("TallyConsolidatedBranchSourcePolicy.RequirePreparedVoucherCount(", StringComparison.Ordinal) &&
            mainWindow.Contains("return (snapshot.Xml, statusText);", StringComparison.Ordinal) &&
            mainWindow.Contains("snapshot.Unsupported != 0 || snapshot.MissingLedgers != 0", StringComparison.Ordinal) &&
            mainWindow.Contains("Source: saved offline branch accounting; no Tally posting", StringComparison.Ordinal),
            "Consolidation must pass the existing branch writer's actual in-memory XML, not reload a stale copy.");
        Check(mainWindow.Contains("DispatcherPriority.ContextIdle, new Action(() =>", StringComparison.Ordinal) &&
            !mainWindow.Contains("Task.Delay(90)", StringComparison.Ordinal) &&
            mainWindow.Contains("currentClient.Id, currentClient.FinancialYear, module);", StringComparison.Ordinal),
            "A fixed module navigation delay or missing visible-module cache priority was reintroduced.");

        Console.WriteLine("PASS: offline branch XML preparation, owned source checks, fast module opening and no live Tally posting.");
    }
}
