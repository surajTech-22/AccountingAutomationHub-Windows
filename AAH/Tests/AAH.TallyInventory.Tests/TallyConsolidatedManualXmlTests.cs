using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedManualXmlTests
{
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException("Manual XML contract: " + reason);
    }

    public static void Run()
    {
        var voucher = XElement.Parse("""
            <VOUCHER DATE="02-May-2025" ACTION="Create" VCHTYPE="Purchase - Karnataka">
                <DATE>20250502</DATE>
                <VOUCHERTYPENAME>Purchase - Karnataka</VOUCHERTYPENAME>
                <VOUCHERNUMBER>B-123-PR-1</VOUCHERNUMBER>
                <REFERENCE>PR-1</REFERENCE>
                <NARRATION>Purchase PR-1</NARRATION>
                <ALLLEDGERENTRIES.LIST><LEDGERNAME>Purchase - Karnataka</LEDGERNAME><AMOUNT>100.00</AMOUNT></ALLLEDGERENTRIES.LIST>
                <ALLLEDGERENTRIES.LIST><LEDGERNAME>Creditor - Karnataka</LEDGERNAME><AMOUNT>-100.00</AMOUNT></ALLLEDGERENTRIES.LIST>
            </VOUCHER>
            """);
        var alter = TallyAccountingVoucherExportService
            .BuildBoundConsolidatedVoucherAlterXml("Combined Books",
                voucher, "457", "19");
        var doc = XDocument.Parse(alter);
        var converted = doc.Descendants("VOUCHER").Single();
        Check((string?)converted.Attribute("ACTION") == "Alter" &&
              (string?)converted.Attribute("TAGNAME") == "MASTER ID" &&
              (string?)converted.Attribute("TAGVALUE") == "457" &&
              (string?)converted.Attribute("VCHTYPE") == "Purchase - Karnataka" &&
              converted.Element("VOUCHERTYPENAME")?.Value == "Purchase - Karnataka" &&
              converted.Element("VOUCHERNUMBER")?.Value == "19",
              "manual ALTER must use the original bound MASTERID, Tally-owned number and desired child voucher type");
        Check(converted.Descendants("LEDGERNAME").Select(x => x.Value)
                  .SequenceEqual(voucher.Descendants("LEDGERNAME").Select(x => x.Value)) &&
              converted.Descendants("AMOUNT").Select(x => x.Value)
                  .SequenceEqual(voucher.Descendants("AMOUNT").Select(x => x.Value)),
              "manual ALTER must preserve the complete original signed DR/CR");
        var baseVoucher = new XElement(voucher);
        baseVoucher.Element("VOUCHERTYPENAME")!.Value = "Purchase";
        baseVoucher.SetAttributeValue("VCHTYPE", "Purchase");
        var reverse = XDocument.Parse(TallyAccountingVoucherExportService
            .BuildBoundConsolidatedVoucherAlterXml("Combined Books",
                baseVoucher, "457", "19"))
            .Descendants("VOUCHER").Single();
        Check((string?)reverse.Attribute("ACTION") == "Alter" &&
              (string?)reverse.Attribute("TAGVALUE") == "457" &&
              reverse.Element("VOUCHERTYPENAME")?.Value == "Purchase" &&
              reverse.Descendants("AMOUNT").Select(x => x.Value)
                .SequenceEqual(voucher.Descendants("AMOUNT").Select(x => x.Value)),
              "unselecting separate voucher types must use same-MASTERID reverse ALTER, not CREATE");

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(
                   root.FullName, "AAH", "src", "AccountingAutomationHub.Desktop",
                   "ViewModels", "MainWindowViewModel.cs")))
            root = root.Parent;
        Check(root is not null, "source tree must be present for read-only release contract");

        // A live Generate XML click must be incapable of reaching the Tally
        // importer, even if another method accidentally calls ImportXmlAsync.
        // Port 65534 deliberately has no Tally server: passing means the
        // import was rejected before transport, not that it failed on a socket.
        using (var tally = new TallyHttpService(port: 65534))
        using (TallyHttpService.BeginReadOnlyTallyScope())
        {
            var blocked = false;
            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                    await tally.ImportXmlAsync(
                        "<ENVELOPE><HEADER><TALLYREQUEST>Import Data</TALLYREQUEST></HEADER></ENVELOPE>"))
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase) &&
                ex.Message.Contains("BEFORE transport", StringComparison.Ordinal))
            {
                blocked = true;
            }
            Check(blocked, "Generate XML guard must reject import on awaited async flow without any network I/O");
        }
        var infra = File.ReadAllText(Path.Combine(root!.FullName, "AAH", "src",
            "AccountingAutomationHub.Infrastructure", "Tally",
            "TallyConsolidatedVoucherImportService.cs"));
        var view = File.ReadAllText(Path.Combine(root.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs"));
        var start = infra.IndexOf("public async Task<TallyConsolidatedManualImportXmlResult> PrepareManualImportXmlAsync(", StringComparison.Ordinal);
        var end = infra.IndexOf("public async Task<int> ReconcileAcceptedReadOnlyAsync(", StringComparison.Ordinal);
        Check(start >= 0 && end > start, "manual materializer is missing");
        var method = infra[start..end];
        Check(method.Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) &&
              method.Contains("BuildBoundConsolidatedVoucherAlterXml(", StringComparison.Ordinal) &&
              method.Contains("ReadSynchronizedVoucherAuthorityAsync(", StringComparison.Ordinal) &&
              method.Contains("RequireUnchangedSynchronizedAuthorityAsync(", StringComparison.Ordinal) &&
              method.Contains("GetSourceBindingVersionState(", StringComparison.Ordinal) &&
              method.Contains("RequireExistingDefaultCashForBankVouchers(", StringComparison.Ordinal),
              "manual XML must use current source binding and live MASTERID/ALTERID/DRCR/master authority");
        // v603-v606 regression: Generate XML inserted a DIFFERENT
        // cross-module MASTERID veto into an otherwise VERIFIED consolidated
        // source. Choosing separate voucher types changes the DESIRED type
        // but leaves the original source anchor and existing MASTERID intact.
        // The already-working automatic Export path uses the saved previous
        // version/full live DRCR/ALTERID and the normal bound ALTER writer.
        // Generate must use exactly that decision rather than blocking because
        // an obsolete normal Purchase Voucher binding says ModifyPending.
        var boundSectionStart = method.IndexOf("var previous = bound[0];",
            StringComparison.Ordinal);
        // The saved PREVIOUS request fingerprint can be different from the
        // exact live destination readback even for an already-correct voucher.
        // A no-op is always based on full live Tally content, not an identical
        // old request hash; changed desired content remains ALTER-gated below.
        var verifiedNoop = method.IndexOf(
            "if (FullVoucherMatches(",
            Math.Max(0, boundSectionStart), StringComparison.Ordinal);
        var fullLiveMatch = verifiedNoop < 0 ? -1 : method.IndexOf(
            "FullVoucherMatches(", verifiedNoop, StringComparison.Ordinal);
        var skipNoop = method.IndexOf("continue;", fullLiveMatch,
            StringComparison.Ordinal);
        var previousVersionGate = method.IndexOf(
            "PreviousConsolidatedVersionMatches(",
            skipNoop, StringComparison.Ordinal);
        var boundAlter = method.IndexOf("BuildBoundConsolidatedVoucherAlterXml(",
            previousVersionGate, StringComparison.Ordinal);
        var verifiedBoundPath = boundSectionStart >= 0 && boundAlter > boundSectionStart
            ? method[boundSectionStart..boundAlter] : string.Empty;
        Check(boundSectionStart >= 0 && verifiedNoop > boundSectionStart &&
              fullLiveMatch > verifiedNoop && skipNoop > fullLiveMatch &&
              previousVersionGate > skipNoop && boundAlter > previousVersionGate &&
              !verifiedBoundPath.Contains("if (tallyRepository.IsTallyMasterIdClaimedByAnotherSource(",
                  StringComparison.Ordinal) &&
              verifiedBoundPath.Contains("boundFull, state", StringComparison.Ordinal) &&
              method.Contains("no XML action", StringComparison.Ordinal) &&
              method.Contains("no claim cleared, rebound or modified", StringComparison.Ordinal),
              "Manual XML verified-bound flow must match original Export: " +
              "exact MASTERID + saved previous full DR/CR/ALTERID then bound " +
              "ALTER, not an extra cross-module ModifyPending veto.");

        // The previous successful import's SAME MASTERID and full signed
        // DR/CR checks remain. Only the normal coordinator collects a complete
        // FY snapshot; Generate reuses its SQLite authority and two tiny markers.
        Check(method.Contains("var fullByMasterId = live.FullVouchers", StringComparison.Ordinal) &&
              method.Contains("fullByMasterId.TryGetValue(previous.MasterId", StringComparison.Ordinal) &&
              method.Contains("previous, boundFull, state", StringComparison.Ordinal) &&
              method.Contains("RequireUnchangedSynchronizedAuthorityAsync(", StringComparison.Ordinal) &&
              !method.Contains("ExportFullCompanySnapshotAsync(", StringComparison.Ordinal) &&
              !method.Contains("ExportVoucherIndexFreshAsync(", StringComparison.Ordinal) &&
              !method.Contains("ExportVoucherAuthorityFreshAsync(", StringComparison.Ordinal),
              "manual XML must use normal synchronized full Voucher XML with tiny ALTERID marker");

        var helperEnd = infra.IndexOf("private static void RequireMatchingSynchronizedMarker(", StringComparison.Ordinal);
        Check(helperEnd > 0 &&
              infra[..helperEnd].Contains("GetBankSyncState(", StringComparison.Ordinal) &&
              infra[..helperEnd].Contains("GetVoucherIndexRange(", StringComparison.Ordinal) &&
              infra[..helperEnd].Contains("GetVoucherXmlAuthorityByMasterIds(", StringComparison.Ordinal) &&
              infra[..helperEnd].Contains("HasCompleteVoucherXmlAuthority(", StringComparison.Ordinal) &&
              infra[..helperEnd].Contains("GetVoucherChangeMarkerAsync(", StringComparison.Ordinal),
              "consolidated must use the original Client/FY/CompanyKey authority");

        var importStart = infra.IndexOf(
            "public async Task<TallyConsolidatedVoucherImportResult> ImportAsync(", StringComparison.Ordinal);
        var importEnd = infra.IndexOf("private static void RequireDestination(", importStart, StringComparison.Ordinal);
        Check(importStart >= 0 && importEnd > importStart &&
              !infra[importStart..importEnd].Contains("ExportFullCompanySnapshotAsync(", StringComparison.Ordinal) &&
              infra[importStart..importEnd].Contains("ReadSynchronizedVoucherAuthorityAsync(", StringComparison.Ordinal) &&
              infra[importStart..importEnd].Contains("createdMasters == 0", StringComparison.Ordinal) &&
              infra[importStart..importEnd].Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) &&
              infra[importStart..importEnd].Contains("BuildBoundConsolidatedVoucherAlterXml(", StringComparison.Ordinal),
              "consolidated Import must reuse normal FY authority and existing verified bound ALTER");

        // No manual action may borrow an unverified or missing source binding;
        // the established normal AAH previous-version gate is still required.
        Check(method.Contains("state.State.Equals(\"Verified\"", StringComparison.Ordinal) &&
              method.Contains("bound.Length != 1 || !claimedIds.Add(state.MasterId)",
                  StringComparison.Ordinal) &&
              method.Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) &&
              method.Contains("ExportVoucherIndexFreshAsync(", StringComparison.Ordinal) &&
              method.Contains("Manual CREATE denied: source has historical Tally import evidence",
                  StringComparison.Ordinal),
              "Manual XML must keep verified-bound-only ALTER, unique MASTERID, " +
              "fresh ALTERID authority, and no duplicate CREATE.");

        foreach (var forbidden in new[] {
                     "ImportWithEvidenceAsync(", "tally.ImportXmlAsync(",
                     "StageSourceBindingChange(", "ConfirmSourceBindingVersion(",
                     "UpsertSourceBinding(", "ReplaceVoucherIndex(",
                     "RecordImportFingerprints(", "ReconcileMissingVoucherBindingAuthority("
                 })
            Check(!method.Contains(forbidden, StringComparison.Ordinal),
                "Generate Consolidated XML must never mutate Tally or its accepted-write database: " + forbidden);
        Check(view.Contains("using var readOnlyTallyScope = TallyHttpService.BeginReadOnlyTallyScope();", StringComparison.Ordinal) &&
              view.Contains("Source preview: {result.FinalVouchers} prepared vouchers", StringComparison.Ordinal),
              "Generate must enforce no Tally writes and retain read-only source counts after a disputed MASTERID");
        // v602 restored original source counts from a cached XML when Tally
        // Import opened. v603-v604 renamed that file and silently displayed
        // zero when the new source-only preview had not been generated yet.
        // Open both consolidated screens using the SAME already-tested
        // branch-to-consolidated builder, with no Tally import, manual-file
        // overwrite, or a new accounting summation method.
        Check(view.Contains("case ConsolidatedBranchDashboardViewModel:", StringComparison.Ordinal) &&
              view.Contains("case ConsolidatedTallyImportViewModel:", StringComparison.Ordinal) &&
              view.Contains("QueueConsolidatedSourcePreviewWarm(client);", StringComparison.Ordinal) &&
              view.Contains("produceManualImportFiles: false, displayOnlyWarm: true", StringComparison.Ordinal) &&
              System.Text.RegularExpressions.Regex.IsMatch(view,
                  @"if\s*\(produceManualImportFiles\)\s*foreach\s*\(var filename",
                  System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
              view.Contains("AAH_CONSOLIDATED_SOURCE_PREVIEW_DO_NOT_IMPORT.txt", StringComparison.Ordinal) &&
              view.Contains("v602 original source-only voucher preview", StringComparison.Ordinal),
              "consolidated original Tally panels must restore missing counts without overwriting valid manual import XML");
        var dashboard = File.ReadAllText(Path.Combine(root.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "ViewModels", "DashboardViewModel.cs"));
        Check(dashboard.Contains("ReportConsolidatedSourcePreviewLoadFailure(", StringComparison.Ordinal) &&
              dashboard.Contains("zero is not a verified count", StringComparison.Ordinal) &&
              dashboard.Contains("RefreshConsolidatedTallySummaryFromPreparedSources();", StringComparison.Ordinal),
              "a missing source preview must remain distinguishable from an actually empty source");
        Check(view.Contains("File.WriteAllText(vouchersPath, manual.VouchersXml", StringComparison.Ordinal) &&
              !view.Contains("File.WriteAllText(vouchersPath, result.VouchersXml", StringComparison.Ordinal) &&
              view.Contains("AAH_CONSOLIDATED_SOURCE_PREVIEW_DO_NOT_IMPORT.txt", StringComparison.Ordinal) &&
              view.Contains("produceManualImportFiles: false", StringComparison.Ordinal),
              "the only manually-importable file must be the materialized XML, not internal CREATE preview");
        Console.WriteLine("PASS: real manual CREATE/ALTER XML, original DR/CR, read-only live identity checks and no Tally posting.");
    }
}
