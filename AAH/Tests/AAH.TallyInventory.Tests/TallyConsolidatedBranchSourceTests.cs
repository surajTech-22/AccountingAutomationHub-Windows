using System.Reflection;
using System.Text;
using AccountingAutomationHub.Domain.Purchases;
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
        // Tally discovery FY may be its FIRST books FY or empty; the
        // original source Tally must remain available for verified masters.
        var priorBookYearLink = new TallyClientLink
        {
            ClientId = 101, CompanyKey = "ka-branch",
            CompanyName = "Working Company",
            FinancialYear = "2021-22", BooksFrom = "01-04-2021"
        };
        Check(ReferenceEquals(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
                priorBookYearLink, 101, "2025-26", Destination,
                "AAH Prepared Branch CL-KA"), priorBookYearLink) &&
              TallyConsolidatedBranchSourcePolicy.HasConfirmedSourceBooksCoverage(
                  priorBookYearLink, "2025-26"),
            "Original first-books FY caused a valid linked branch master export to be skipped.");
        var blankDiscoveryFy = new TallyClientLink
        {
            ClientId = 101, CompanyKey = "ka-branch",
            CompanyName = "Working Company",
            FinancialYear = "", BooksFrom = "01-04-2021"
        };
        Check(ReferenceEquals(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
                blankDiscoveryFy, 101, "2025-26", Destination,
                "AAH Prepared Branch CL-KA"), blankDiscoveryFy),
            "Blank Tally discovery FY with verified books was incorrectly rejected.");
        Check(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
            new TallyClientLink {
                ClientId = 101, CompanyKey = "ka-branch",
                CompanyName = "Working Company", FinancialYear = "2026-27",
                BooksFrom = "01-04-2026"
            }, 101, "2025-26", Destination, "AAH Prepared Branch CL-KA") is null,
            "Tally branch books beginning after the source FY were accepted.");
        Check(TallyConsolidatedBranchSourcePolicy.SelectOptionalLiveSourceLink(
            new TallyClientLink {
                ClientId = 101, CompanyKey = "ka-branch",
                CompanyName = "Working Company", FinancialYear = "2024-25",
                BooksFrom = "01-04-2021"
            }, 101, "2025-26", Destination, "AAH Prepared Branch CL-KA") is null,
            "Unexplained stale Tally discovery FY was incorrectly treated as first-books provenance.");

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

        // Optional source company lookup must reuse strict identity resolution,
        // not just a stale name or Tally first-books-year key.
        var sourceView = File.ReadAllText(Path.Combine(root!.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs"));
        Check(sourceView.Contains("sourceLink, null, openCompanies, financialYear", StringComparison.Ordinal) &&
              sourceView.Contains("sourceLink, selectedSource, openCompanies, financialYear", StringComparison.Ordinal),
            "The source Tally master path was not wired to the shared verified wrapper.");
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
        Check(mainWindow.Contains("var (xml, statusText, sourceFingerprint) = await PrepareMissingBranchXmlAsync(", StringComparison.Ordinal) &&
            mainWindow.Contains("child, branch.Name, financialYear);", StringComparison.Ordinal) &&
            !mainWindow.Contains("if (existing.Length == 0)", StringComparison.Ordinal) &&
            !mainWindow.Contains("var candidates = new List<string>();", StringComparison.Ordinal) &&
            mainWindow.Contains("_branchXmlPreparationService.GenerateLiveXml(", StringComparison.Ordinal) &&
            mainWindow.Contains("TallyConsolidatedBranchSourcePolicy.RequirePreparedVoucherCount(", StringComparison.Ordinal) &&
            mainWindow.Contains("return (snapshot.Xml, statusText, fingerprint);", StringComparison.Ordinal) &&
            mainWindow.Contains("snapshot.Unsupported != 0 || snapshot.MissingLedgers != 0", StringComparison.Ordinal) &&
            mainWindow.Contains("Source: saved offline branch accounting; no Tally posting", StringComparison.Ordinal),
            "Consolidation must pass the existing branch writer's actual in-memory XML, not reload a stale copy.");
        Check(mainWindow.Contains("DispatcherPriority.ContextIdle, new Action(() =>", StringComparison.Ordinal) &&
            !mainWindow.Contains("Task.Delay(90)", StringComparison.Ordinal) &&
            mainWindow.Contains("currentClient.Id, currentClient.FinancialYear, module);", StringComparison.Ordinal),
            "A fixed module navigation delay or missing visible-module cache priority was reintroduced.");

        // Karnataka 02-Nov regression: exactly 36 GSTR-2B-only rows are
        // reconciliation evidence, NOT 36 missing Purchase ledgers.
        var twoBOnly = Enumerable.Range(1, 36).Select(i => new PurchaseImportRecord
        {
            PersistentId = i,
            ReconciliationStatus = "2B Only",
            Destination = "2B Only",
            GstApplicability = PurchaseTreatmentPolicy.NotIncludedInPurchase,
            DocumentNumber = "2B-" + i,
            GstDate = new DateTime(2026, 2, 1)
        }).ToArray();
        foreach (var row in twoBOnly) PurchaseTreatmentPolicy.ApplyAccountingShape(row);
        Check(twoBOnly.All(row => !PurchaseTreatmentPolicy.IsTallyPurchaseVoucherSource(row)),
            "36 unpromoted 2B-only rows must remain reconciliation-only.");
        var promoted = twoBOnly[0];
        promoted.Destination = "Purchase";
        promoted.GstApplicability = PurchaseTreatmentPolicy.IncludedInPurchase;
        PurchaseTreatmentPolicy.ApplyAccountingShape(promoted);
        Check(PurchaseTreatmentPolicy.IsTallyPurchaseVoucherSource(promoted),
            "Explicitly included 2B invoice must become a valid Purchase voucher source.");
        Check(twoBOnly.Count(PurchaseTreatmentPolicy.IsTallyPurchaseVoucherSource) == 1,
            "Only the promoted 2B invoice should be exported.");
        Check(PurchaseTreatmentPolicy.IsTallyPurchaseVoucherSource(
            new PurchaseImportRecord { Destination = "RCM" }) &&
            PurchaseTreatmentPolicy.IsTallyPurchaseVoucherSource(
                new PurchaseImportRecord { Destination = "Debit Note Reverse" }) &&
            !PurchaseTreatmentPolicy.IsTallyPurchaseVoucherSource(
                new PurchaseImportRecord { Destination = "Reconciliation Only" }),
            "Normal RCM / notes must remain eligible; non-accounting destinations must not.");
        var voucherExporter = File.ReadAllText(Path.Combine(root.FullName, "AAH", "src",
            "AccountingAutomationHub.Infrastructure", "Tally", "TallyAccountingVoucherExportService.cs"));
        Check(voucherExporter.Contains(".Where(PurchaseTreatmentPolicy.IsTallyPurchaseVoucherSource)", StringComparison.Ordinal),
            "Shared Tally branch/normal exporter must filter 2B evidence BEFORE ledger validation.");
        var purchaseUi = File.ReadAllText(Path.Combine(root.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "ViewModels", "PurchaseImportViewModel.cs"));
        Check(purchaseUi.Contains("FilterReconciliationRows(materialized)", StringComparison.Ordinal) &&
            purchaseUi.Contains("_lastWarmPurchaseFingerprint == fingerprint", StringComparison.Ordinal),
            "Private branch 2B filter must survive identical and changed module snapshots.");

        Console.WriteLine("PASS: offline branch XML preparation, owned source checks, fast module opening and no live Tally posting.");
    }
}
