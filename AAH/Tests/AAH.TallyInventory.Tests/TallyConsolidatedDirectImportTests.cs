using System;
using System.IO;

internal static class TallyConsolidatedDirectImportTests
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Direct consolidated Tally import: " + reason);
    }

    public static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
                   "AAH", "src", "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs")))
            root = root.Parent;
        Check(root is not null, "source checkout unavailable");
        static string Read(DirectoryInfo root, string path) =>
            File.ReadAllText(Path.Combine(root.FullName, "AAH", "src", path));

        var dashboard = Read(root!, "AccountingAutomationHub.Desktop/Views/DashboardView.xaml");
        var main = Read(root!, "AccountingAutomationHub.Desktop/ViewModels/MainWindowViewModel.cs");
        var coordinator = Read(root!, "AccountingAutomationHub.Desktop/Services/TallyLiveConnectionCoordinator.cs");
        var wrapper = Read(root!, "AccountingAutomationHub.Desktop/Services/TallyConsolidatedPreImportAuthorityWrapper.cs");
        var importer = Read(root!, "AccountingAutomationHub.Infrastructure/Tally/TallyConsolidatedVoucherImportService.cs");
        var branchMasterImporter = Read(root!, "AccountingAutomationHub.Infrastructure/Tally/TallyBranchConsolidationImportService.cs");
        var branchMasterPlanner = Read(root!, "AccountingAutomationHub.Infrastructure/Tally/TallyBranchConsolidationMasterPlanner.cs");
        var dashboardViewModel = Read(root!, "AccountingAutomationHub.Desktop/ViewModels/DashboardViewModel.cs");
        var consolidatedImportPage = Read(root!, "AccountingAutomationHub.Desktop/ViewModels/ConsolidatedTallyImportViewModel.cs");
        var consolidatedDashboardPage = Read(root!, "AccountingAutomationHub.Desktop/ViewModels/ConsolidatedBranchDashboardViewModel.cs");

        Check(!dashboard.Contains("Content=\"Generate Consolidated XML\"", StringComparison.Ordinal) &&
              !dashboard.Contains("Command=\"{Binding PrepareConsolidatedXmlCommand}\"", StringComparison.Ordinal) &&
              dashboard.Contains("Content=\"Import / Verify Branch Masters\"", StringComparison.Ordinal) &&
              dashboard.Contains("Content=\"Export Consolidated to Tally\"", StringComparison.Ordinal) &&
              dashboard.Contains("IsChecked=\"{Binding SeparateVoucherTypes, Mode=TwoWay}\"", StringComparison.Ordinal),
              "remove only the separate Generate XML button and preserve original master/voucher import UI");

        var action = main.IndexOf("private async Task<TallyConsolidatedVoucherImportResult> ExportConsolidatedVouchersAsync(",
            StringComparison.Ordinal);
        var end = main.IndexOf("private string ", action, StringComparison.Ordinal);
        if (end < 0) end = main.Length;
        Check(action >= 0, "original consolidated exporter missing");
        var export = main[action..end];
        var prepare = export.IndexOf("var prepared = await GenerateConsolidatedPreparedXmlAsync(", StringComparison.Ordinal);
        var sync = export.IndexOf("await authorityWrapper.VerifyAsync(", StringComparison.Ordinal);
        var call = export.IndexOf("var result = await importer.ImportAsync(", StringComparison.Ordinal);
        Check(prepare >= 0 && sync > prepare && call > sync &&
              export.Contains("new TallyConsolidatedPreImportAuthorityWrapper(", StringComparison.Ordinal) &&
              export.Contains("await authorityWrapper.VerifyAsync(current, link, company)", StringComparison.Ordinal) &&
              export.Contains("consolidatedVerifiedMarkerReader: authorityWrapper.ReadVerifiedMarkerAsync", StringComparison.Ordinal) &&
              export.Contains("produceManualImportFiles: false", StringComparison.Ordinal),
              "direct import must use the consolidated wrapper's live-index-verified marker before the unchanged voucher writer");
        Check(export.Contains("if (separateTypes)", StringComparison.Ordinal) &&
              export.IndexOf("await ImportConsolidatedMastersAsync(", StringComparison.Ordinal) < prepare &&
              export.Contains("SaveConsolidatedExistingDiagnosticsAsync(", StringComparison.Ordinal) &&
              export.Contains("tallyAcknowledgedAlterCount", StringComparison.Ordinal),
              "master-first, ALTER and diagnostic flow must remain intact");

        // v633: v632 master CREATE-only remains the first stage. The
        // voucher step now uses only previous->desired ALTER for VERIFIED,
        // already bound vouchers; it cannot create masters or new vouchers.
        // Selecting Generate XML must remain strictly read-only.
        var selectedTypes = export.IndexOf("if (separateTypes)", StringComparison.Ordinal);
        Check(selectedTypes >= 0 &&
              export.IndexOf("await ImportConsolidatedMastersAsync(", selectedTypes,
                  StringComparison.Ordinal) < prepare &&
              export.Contains("voucherTypeOnlyAlter: separateTypes", StringComparison.Ordinal) &&
              main.Contains("createOnlyVoucherTypes: separateVoucherTypes", StringComparison.Ordinal) &&
              main.Contains("if (separateTypes) produceManualImportFiles = false;", StringComparison.Ordinal) &&
              importer.Contains("bool voucherTypeOnlyAlter = false", StringComparison.Ordinal) &&
              importer.Contains("MatchesTypeOnlyPreparedVoucher(", StringComparison.Ordinal) &&
              importer.Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) &&
              importer.Contains("var createCandidates = typeOnlyOperation", StringComparison.Ordinal) &&
              importer.Contains("if (typeOnlyOperation) continue; // No ledger/master creation.", StringComparison.Ordinal) &&
              importer.Contains("var restoreBranchTypes = !voucherTypeOnlyAlter", StringComparison.Ordinal) &&
              importer.Contains("allowExistingNoneNumbering: typeOnlyOperation", StringComparison.Ordinal),
            "selected branch types must use original MASTERID bound type-only ALTER with zero voucher or master CREATE");
        var mastersOnly = branchMasterImporter.IndexOf(
            "public async Task<TallyBranchMasterImportResult> ImportMastersAsync(",
            StringComparison.Ordinal);
        var mastersEnd = branchMasterImporter.IndexOf(
            "public async Task<TallyBranchMasterPlan> PreviewPlanAsync(",
            mastersOnly, StringComparison.Ordinal);
        Check(mastersOnly >= 0 && mastersEnd > mastersOnly,
            "original master-only import entry point missing");
        var masterStage = branchMasterImporter[mastersOnly..mastersEnd];
        Check(masterStage.Contains("bool createOnlyVoucherTypes = false", StringComparison.Ordinal) &&
              masterStage.Contains("plan.Groups.Any(x => x.Create)", StringComparison.Ordinal) &&
              masterStage.Contains("createOnlyVoucherTypes ? Array.Empty<MasterRecord>()", StringComparison.Ordinal) &&
              masterStage.Contains("RequireNoneNumberingForNewVoucherType(", StringComparison.Ordinal) &&
              !masterStage.Contains("RequireManualVoucherTypeNumbering(", StringComparison.Ordinal) &&
              branchMasterPlanner.Contains("<NUMBERINGMETHOD>None</NUMBERINGMETHOD>", StringComparison.Ordinal),
            "new branch type Masters must use None, reuse existing types, and never write Groups/Cash or vouchers in creation-only mode");
        Check(importer.Contains("CanReconcileAcceptedTypeOnlyAlter(", StringComparison.Ordinal) &&
              importer.Contains("PreviousConsolidatedTypeOnlyWithTallyRenumberMatches(", StringComparison.Ordinal) &&
              importer.Contains("IsVerifiedBranchSubtypeParent(", StringComparison.Ordinal) &&
              importer.Contains("var restoreBranchTypes = !voucherTypeOnlyAlter", StringComparison.Ordinal) &&
              importer.Contains("var typeOnlyOperation = voucherTypeOnlyAlter || restoreBranchTypes;", StringComparison.Ordinal) &&
              !importer.Contains("Tally changed the voucher number while altering its type", StringComparison.Ordinal) &&
              importer.Contains("reconciledTypeOnly++", StringComparison.Ordinal),
            "v634 must allow Tally-owned number changes, reconcile accepted ALTER and restore parent type with zero voucher CREATE");
        Console.WriteLine("PASS: v634: master-first type-only ALTER in both directions, Tally-owned numbering, accepted readback recovery, no voucher CREATE.");

        Check(coordinator.Contains("bool forceFullBaseline = true)", StringComparison.Ordinal) &&
              coordinator.Contains("forceFullBaseline: forceLinkedSynchronization && forceFullBaseline", StringComparison.Ordinal) &&
              coordinator.Contains("await SynchronizeLinkedCompanyAsync(", StringComparison.Ordinal) &&
              !coordinator.Contains("requireConsolidatedImportAuthority", StringComparison.Ordinal) &&
              !coordinator.Contains("RequireFreshConsolidatedImportCheckpointAsync(", StringComparison.Ordinal),
              "individual-client normal Tally coordinator must not contain consolidated-specific guards");
        Check(wrapper.Contains("normalCoordinator.RefreshNowAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("forceLinkedSynchronization: true", StringComparison.Ordinal) &&
              wrapper.Contains("forceFullBaseline: false", StringComparison.Ordinal) &&
              wrapper.Contains("ExportVoucherAuthorityFreshAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("FindNativeProjectionRecheckMonths(", StringComparison.Ordinal) &&
              wrapper.Contains("GetBankSyncState(", StringComparison.Ordinal) &&
              wrapper.Contains("GetVoucherChangeMarkerAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("ExportVoucherIndexFreshAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("GetVoucherIndexRange(", StringComparison.Ordinal) &&
              wrapper.Contains("HasCompleteVoucherXmlAuthority(", StringComparison.Ordinal) &&
              wrapper.Contains("RequireSameVoucherIndex(", StringComparison.Ordinal) &&
              wrapper.Contains("liveMarker.HasReliableAlterId && liveMarker.LastAlterId > 0", StringComparison.Ordinal) &&
              wrapper.Contains("liveMarker.VoucherCount == saved.LastVoucherCount", StringComparison.Ordinal) &&
              wrapper.Contains("liveAlterId <= 0 || liveAlterId > synchronizedAlterId", StringComparison.Ordinal) &&
              wrapper.Contains("observed.Add(id)", StringComparison.Ordinal) &&
              wrapper.Contains("RequireSameSavedLink(client, confirmedLink)", StringComparison.Ordinal) &&
              !wrapper.Contains("ExportFullCompanySnapshotAsync(", StringComparison.Ordinal) &&
              !wrapper.Contains("ImportXmlAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("repository.ReplaceVoucherIndex(", StringComparison.Ordinal) &&
              wrapper.Contains("repository.ReplaceVoucherXmlAuthority(", StringComparison.Ordinal) &&
              wrapper.Contains("RunWithLinkedTallyOperationGateAsync(", StringComparison.Ordinal),
              "consolidated explicit import must reuse original Live Tally authority, repair stale native vouchers only with verified index and XML, and never POST a duplicate voucher");
        Check(importer.Contains("consolidatedVerifiedMarkerReader = null", StringComparison.Ordinal) &&
              importer.Contains("ReadTrustedMarkerAsync(", StringComparison.Ordinal) &&
              importer.Contains("consolidatedVerifiedMarkerReader is null", StringComparison.Ordinal) &&
              importer.Contains("? tally.GetVoucherChangeMarkerAsync(", StringComparison.Ordinal) &&
              importer.Contains(": consolidatedVerifiedMarkerReader(", StringComparison.Ordinal),
              "normal consolidated manual/individual callers must keep the original live Tally marker; only direct consolidated import has a verified alternate reader");
        Check(importer.Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) &&
              importer.Contains("BuildBoundConsolidatedVoucherAlterXml(", StringComparison.Ordinal) &&
              importer.Contains("ReadSynchronizedVoucherAuthorityAsync(", StringComparison.Ordinal) &&
              importer.Contains("ReconcileMissingVoucherBindingAuthority(", StringComparison.Ordinal),
              "original bound MASTERID/ALTERID/DRCR/duplicate gates must remain intact");

        // CL019 7 Nov: verified ALTERs must persist their exact native
        // MASTERID/ALTERID and full signed Voucher XML. The wrapper retains
        // original live-link synchronization, while the write importer uses
        // one post-write batch-only delta upsert. Do NOT replace a month's
        // cached index with a filtered set or fabricate a FY checkpoint.
        Check(importer.Contains("tallyRepository.UpsertVoucherIndexRecords(", StringComparison.Ordinal) &&
              importer.Split("tallyRepository.UpsertVoucherXmlAuthority(",
                  StringSplitOptions.None).Length - 1 >= 2 &&
              importer.Contains("afterIndex", StringComparison.Ordinal) &&
              importer.Contains("afterFull", StringComparison.Ordinal) &&
              !importer.Contains("UpsertBankSyncState(", StringComparison.Ordinal) &&
              wrapper.Contains("RefreshVoucherAuthorityFromOriginalLiveLinkAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("ExportVoucherAuthorityFreshAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("PersistVerifiedOriginalLiveVoucherAuthority(", StringComparison.Ordinal) &&
              wrapper.Contains("RunWithLinkedTallyOperationGateAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("RequireSameVoucherIndex(", StringComparison.Ordinal) &&
              wrapper.Contains("forceFullBaseline: false", StringComparison.Ordinal) &&
              !wrapper.Contains("RequireFreshNormalBaseline(", StringComparison.Ordinal) &&
              main.Contains("await authorityWrapper.VerifyAsync(visible, stillLinked, company)", StringComparison.Ordinal) &&
              coordinator.Contains("internal async Task RunWithLinkedTallyOperationGateAsync(", StringComparison.Ordinal),
            "consolidated native readback must retain original live-link synchronization and persist exact batch ID/full XML deltas.");

        var adaptiveWriteStart = importer.IndexOf(
            "var items = writePlan.OrderBy(item => item.Date).ToArray();",
            StringComparison.Ordinal);
        var adaptiveWriteEnd = importer.IndexOf(
            "return new TallyConsolidatedVoucherImportResult(imported, createdMasters,",
            adaptiveWriteStart, StringComparison.Ordinal);
        Check(adaptiveWriteStart >= 0 && adaptiveWriteEnd > adaptiveWriteStart,
            "v637 must write from a single continuous chronological adaptive queue.");
        var adaptiveBody = importer[adaptiveWriteStart..adaptiveWriteEnd];
        var finalReadback = adaptiveBody.IndexOf("// Exactly one final readback phase", StringComparison.Ordinal);
        var writeLoop = adaptiveBody.IndexOf("for (var offset = 0; offset < items.Length;)", StringComparison.Ordinal);
        Check(importer.Contains("var writePlan = source.Where(", StringComparison.Ordinal) &&
              writeLoop >= 0 && finalReadback > writeLoop &&
              adaptiveBody.Contains("Take(batchSize)", StringComparison.Ordinal) &&
              adaptiveBody.Contains("NextAdaptiveVoucherBatchSize(batchSize, watch.ElapsedMilliseconds)", StringComparison.Ordinal) &&
              adaptiveBody.Contains("foreach (var postedBatch in postedBatches)", StringComparison.Ordinal) &&
              adaptiveBody.Contains("ExportVoucherAuthorityForImportBatchAsync(", StringComparison.Ordinal) &&
              adaptiveBody.Contains("UpsertVoucherIndexRecords(", StringComparison.Ordinal) &&
              adaptiveBody.Contains("FinalNativeReadbackPhases=1", StringComparison.Ordinal) &&
              adaptiveBody.Contains("TargetBatchSize=", StringComparison.Ordinal) &&
              adaptiveBody.Contains("ActualBatchSize=", StringComparison.Ordinal) &&
              adaptiveBody.Contains("responseRejected", StringComparison.Ordinal) &&
              adaptiveBody.Contains("FullVoucherMatches(expected, item, afterFull)", StringComparison.Ordinal) &&
              !adaptiveBody.Contains("foreach (var month in writePlan", StringComparison.Ordinal) &&
              !adaptiveBody.Contains("GroupBy(item => (item.Date.Year, item.Date.Month))", StringComparison.Ordinal) &&
              !adaptiveBody.Contains("foreach (var (verifyFrom, verifyTo) in readbackRanges)", StringComparison.Ordinal) &&
              !adaptiveBody.Contains("tallyRepository.ReplaceVoucherIndexRange(", StringComparison.Ordinal) &&
              !adaptiveBody.Contains("knownOriginalMasterIds.UnionWith(", StringComparison.Ordinal) &&
              !adaptiveBody.Contains("Voucher-ALTER-", StringComparison.Ordinal),
            "v637 must POST all adaptive batches before one filtered native readback, preserve original authority and never resend uncertain sources.");
        Console.WriteLine("PASS: v637 continuous 160-to-400 adaptive writes, one final batch-only readback, exact native DRCR.");

        Console.WriteLine(
            "PASS: consolidated native readback reuses Live Tally Link and the individual full Voucher parser, with no mandatory fresh baseline.");

        // Opening Tally Import is a PAGE NAVIGATION, never an implicit full
        // branch XML regeneration. The Dashboard already owns its shared source
        // preview, so initiating it again when selecting Tally Import blocked
        // the WPF dispatcher and could show Windows "Not Responding".
        var importPrepareCase = main.IndexOf("case ConsolidatedTallyImportViewModel:", StringComparison.Ordinal);
        var nextPrepareCase = main.IndexOf("case PurchaseImportViewModel:", importPrepareCase, StringComparison.Ordinal);
        Check(importPrepareCase > 0 && nextPrepareCase > importPrepareCase &&
              !main[importPrepareCase..nextPrepareCase].Contains(
                  "QueueConsolidatedSourcePreviewWarm(client)", StringComparison.Ordinal) &&
              main.Contains("case ConsolidatedBranchDashboardViewModel:" +
                  Environment.NewLine + "                if (client is not null)" +
                  Environment.NewLine + "                    QueueConsolidatedSourcePreviewWarm(client);",
                  StringComparison.Ordinal) &&
              main.Contains("CurrentPage is ConsolidatedBranchDashboardViewModel &&", StringComparison.Ordinal),
              "Tally Import navigation must not run the expensive branch XML preview or start it on a link heartbeat");
        Check(consolidatedImportPage.Contains("_ = Task.Run(() =>", StringComparison.Ordinal) &&
              consolidatedImportPage.Contains("dispatcher.BeginInvoke(", StringComparison.Ordinal) &&
              consolidatedImportPage.Contains("generation == Volatile.Read(ref _progressRefreshGeneration)", StringComparison.Ordinal) &&
              consolidatedDashboardPage.Contains("_ = Task.Run(() =>", StringComparison.Ordinal) &&
              consolidatedDashboardPage.Contains("dispatcher.BeginInvoke(", StringComparison.Ordinal),
              "consolidated progress SQLite read must never block WPF page construction or navigation");
        Check(dashboardViewModel.Contains("_consolidatedManualModuleSelections", StringComparison.Ordinal) &&
              dashboardViewModel.Contains("(true, true, true, true, true)", StringComparison.Ordinal) &&
              dashboardViewModel.Contains("ApplyManualModuleSelection(", StringComparison.Ordinal) &&
              dashboardViewModel.Contains("RememberConsolidatedManualModuleSelection();", StringComparison.Ordinal) &&
              dashboardViewModel.Contains("_normalManualModuleSelectionBeforeConsolidated", StringComparison.Ordinal) &&
              dashboardViewModel.Contains("if (_isConsolidatedTallyScope)", StringComparison.Ordinal),
              "Bank, Purchase, Sales, Cash and Journal must default selected in consolidated Tally Import, preserve deliberate changes and restore normal export choices");
        Check(dashboard.Contains("IsChecked=\"{Binding IncludeBankStatement, Mode=TwoWay}\"", StringComparison.Ordinal) &&
              dashboard.Contains("IsChecked=\"{Binding IncludePurchases, Mode=TwoWay}\"", StringComparison.Ordinal) &&
              dashboard.Contains("IsChecked=\"{Binding IncludeSales, Mode=TwoWay}\"", StringComparison.Ordinal) &&
              dashboard.Contains("IsChecked=\"{Binding IncludeCashEntries, Mode=TwoWay}\"", StringComparison.Ordinal) &&
              dashboard.Contains("IsChecked=\"{Binding IncludeJournals, Mode=TwoWay}\"", StringComparison.Ordinal),
              "original five Tally Import module checkboxes must keep their existing two-way bindings");
        Console.WriteLine("PASS: consolidated Tally Import opens without implicit source XML regeneration or blocking SQLite read; all five import modules default checked, normal import choices preserved.");
        Console.WriteLine("PASS: direct consolidated Tally import reuses original FY coordinator and voucher ALTER/CREATE; separate Generate button removed.");
    }
}
