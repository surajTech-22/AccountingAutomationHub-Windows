// Regression contract: consolidated import must reuse the ordinary detailed
// Tally report ZIP under the linked parent AutoFolder, never a new report.
internal static class TallyConsolidatedDiagnosticRoutingTests
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(
            "Consolidated Tally diagnostics: " + reason);
    }

    public static void Run()
    {
        DirectoryInfo? root = new(Environment.CurrentDirectory);
        while (root is not null &&
            !File.Exists(Path.Combine(root.FullName, "AAH", "src",
                "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs")))
            root = root.Parent;
        Check(root is not null, "desktop source not available");

        string Source(string area, string filename) => File.ReadAllText(
            Path.Combine(root!.FullName, "AAH", "src", area, filename));

        var main = Source("AccountingAutomationHub.Desktop/ViewModels",
            "MainWindowViewModel.cs");
        var coordinator = Source("AccountingAutomationHub.Desktop/Services",
            "TallyLiveConnectionCoordinator.cs");
        var importer = Source("AccountingAutomationHub.Infrastructure/Tally",
            "TallyConsolidatedVoucherImportService.cs");
        var watcher = Source("AccountingAutomationHub.Desktop/Services",
            "BackgroundAutomationService.cs");

        // CL019 2026-11-03: existing consolidated Purchase ref 2180052410
        // had the correct supplier in the original branch-prepared XML, yet
        // ordinary parent Live XML proposed 61 supplier-to-tax-ledger ALTERs.
        // The normal coordinator must NOT create parent Live XML or send
        // ordinary Voucher Export while saved separate branch books exist.
        // This verifies ROUTING only: never introduce a second DR/CR/GST
        // calculation path or modify the original branch XML builder.
        var parentScopeAttach = main.IndexOf(
            "_tallyCoordinator.AttachConsolidatedParentScopeProvider(client =>",
            StringComparison.Ordinal);
        Check(parentScopeAttach >= 0 &&
              main.Contains("BranchAccountingModePreference.IsEnabled(", StringComparison.Ordinal) &&
              main.Contains("_privateBranchAccounts.FindByChild(client.Id) is null",
                  StringComparison.Ordinal) &&
              main.Contains("_privateBranchAccounts.FindByParent(client.Id).Count > 0",
                  StringComparison.Ordinal),
            "consolidated parent accounting mode/private branch identity is not routed to Tally coordinator");

        var backgroundPreview = coordinator.IndexOf(
            "private async Task GenerateLiveXmlNowAsync(", StringComparison.Ordinal);
        var previewGuard = coordinator.IndexOf(
            "if (IsConsolidatedParentClient(client))", backgroundPreview,
            StringComparison.Ordinal);
        var normalLiveBuilder = coordinator.IndexOf(
            "_accountingExportService.GenerateLiveXml(", backgroundPreview,
            StringComparison.Ordinal);
        Check(backgroundPreview >= 0 && previewGuard > backgroundPreview &&
              normalLiveBuilder > previewGuard &&
              coordinator.Contains("DeleteLiveXmlIfPresent(client, requireDeleted: true);",
                  StringComparison.Ordinal) &&
              coordinator.Contains("AAH_TALLY_LIVE_CURRENT_STATUS.txt", StringComparison.Ordinal) &&
              coordinator.Contains("Cannot remove stale ordinary parent Live XML.",
                  StringComparison.Ordinal) &&
              coordinator.Contains("Consolidated parent activated during ordinary XML preparation;",
                  StringComparison.Ordinal) &&
              coordinator.Contains("LastLiveXmlPath = string.Empty;", StringComparison.Ordinal),
            "ordinary background Live XML still generates wrong consolidated parent ALTERs or leaves stale unsafe files");

        var exportStart = coordinator.IndexOf(
            "public async Task<TallyVoucherExportResult> ExportSelectedAsync(",
            StringComparison.Ordinal);
        var exportGuard = coordinator.IndexOf(
            "if (IsConsolidatedParentClient(client))", exportStart,
            StringComparison.Ordinal);
        var exportBankRanges = coordinator.IndexOf(
            "var bankRanges = CoalesceContiguousRanges(", exportStart,
            StringComparison.Ordinal);
        Check(exportStart >= 0 && exportGuard > exportStart &&
              exportBankRanges > exportGuard &&
              coordinator.Contains(
                  "Ordinary parent Purchase/Sales/Journal XML cannot CREATE or ALTER consolidated vouchers.",
                  StringComparison.Ordinal),
            "normal explicit Tally Export may send parent accounting ALTER to consolidated target");

        var autoReadyStart = coordinator.IndexOf(
            "public void SetOutboundAutoExportReady(", StringComparison.Ordinal);
        var autoReadyGuard = coordinator.IndexOf(
            "if (ready && IsConsolidatedParentClient(client))",
            autoReadyStart, StringComparison.Ordinal);
        var autoReadyArm = coordinator.IndexOf(
            "_outboundReadyClientId = client.Id;", autoReadyStart,
            StringComparison.Ordinal);
        Check(autoReadyGuard > autoReadyStart && autoReadyArm > autoReadyGuard &&
              main.Contains("GenerateConsolidatedPreparedXmlAsync(", StringComparison.Ordinal) &&
              main.Contains("TallyConsolidatedPreparedXmlBuilder.Build(plan, branchSources);",
                  StringComparison.Ordinal),
            "automatic parent Tally export was not disabled or the original consolidated builder was replaced");
        Console.WriteLine("PASS: consolidated parent never generates/exports normal Live XML; source branch XML and destination import remain original.");

        // Selecting separate voucher types on an already populated
        // consolidated Tally must run existing voucher-type master CREATE
        // with fresh destination MASTERID readback BEFORE its normal
        // previous->desired voucher ALTER loop. Never create a second
        // import service or another voucher/GST calculation engine.
        var optionalImportStart = main.IndexOf(
            "private async Task<TallyConsolidatedVoucherImportResult> ExportConsolidatedVouchersAsync(",
            StringComparison.Ordinal);
        var masterFirst = main.IndexOf(
            "if (separateTypes)", optionalImportStart, StringComparison.Ordinal);
        var createTypes = main.IndexOf(
            "await ImportConsolidatedMastersAsync(", masterFirst, StringComparison.Ordinal);
        var prepareTypes = main.IndexOf(
            "var prepared = await GenerateConsolidatedPreparedXmlAsync(",
            createTypes, StringComparison.Ordinal);
        var doImport = main.IndexOf("var result = await importer.ImportAsync(",
            prepareTypes, StringComparison.Ordinal);
        Check(optionalImportStart >= 0 &&
              masterFirst > optionalImportStart &&
              createTypes > masterFirst &&
              prepareTypes > createTypes &&
              doImport > prepareTypes,
            "Separate voucher types do not create/verify destination masters " +
            "before entering the existing consolidated voucher ALTER/CREATE importer.");

        Check(coordinator.Contains("SaveConsolidatedDiagnosticPackAsync(", StringComparison.Ordinal) &&
              coordinator.Contains("await GenerateDiagnosticPackAsync(", StringComparison.Ordinal) &&
              coordinator.Contains("allowAppLogFallback: false, consolidatedEvidence:", StringComparison.Ordinal),
            "consolidated operation bypassed the original detailed diagnostic builder");
        Check(coordinator.Contains("Path.Combine(diagnosticBase, \"AAH_TALLY_REPORTS\")",
                  StringComparison.Ordinal) &&
              coordinator.Contains("AAH_TALLY_DIAGNOSTIC_", StringComparison.Ordinal) &&
              coordinator.Contains("linked.FolderPath", StringComparison.Ordinal),
            "original diagnostic report is not routed to the linked client's AutoFolder");
        Check(main.Contains("ImportConsolidatedMastersAsync(", StringComparison.Ordinal) &&
              main.Contains("GenerateConsolidatedPreparedXmlAsync(", StringComparison.Ordinal) &&
              main.Contains("ExportConsolidatedVouchersAsync(", StringComparison.Ordinal) &&
              main.Contains("SaveConsolidatedExistingDiagnosticsAsync(", StringComparison.Ordinal),
            "master/preview/voucher operations do not use shared diagnostics");
        Check(main.Contains("CONSOLIDATED-TALLY-DIAGNOSTIC-ERROR", StringComparison.Ordinal) &&
              main.Contains("Detailed Tally diagnostics NOT saved in linked AutoFolder:", StringComparison.Ordinal),
            "a ZIP save failure is silently reported as success");
        Check(importer.Contains("ImportWithEvidenceAsync(", StringComparison.Ordinal) &&
              importer.Contains("diagnosticExchange?.Invoke(", StringComparison.Ordinal) &&
              importer.Contains("verifiedVoucher?.Invoke();", StringComparison.Ordinal) &&
              main.Contains("verifiedVoucherCount", StringComparison.Ordinal),
            "actual Tally POST requests/replies and partial readbacks are not retained");
        Check(coordinator.IndexOf("ZipFile.CreateFromDirectory(folder, zipPath", StringComparison.Ordinal) <
              coordinator.IndexOf("CleanupOldDiagnosticArtifacts(root, safeClient, zipPath)", StringComparison.Ordinal),
            "the prior diagnostic ZIP is deleted before the replacement exists");
        Check(watcher.Contains("AAH_TALLY_REPORTS", StringComparison.OrdinalIgnoreCase),
            "Auto Import watcher could ingest its own detailed diagnostic ZIP");
        // v583 CL019: after 333 Tally CREATE acknowledgements, 332
        // exact-live verified and 1 AcceptedAwaitingVerification must be
        // visible from original Client/FY/CompanyKey source-binding rows.
        // Never show the NORMAL parent monthly "677 exportable" selector as
        // if it represented the consolidated destination's imported count.
        var tallyRepository = Source("AccountingAutomationHub.Infrastructure/Tally",
            "SqliteTallyRepository.cs");
        var dashboardVm = Source("AccountingAutomationHub.Desktop/ViewModels",
            "ConsolidatedBranchDashboardViewModel.cs");
        var tallyPageVm = Source("AccountingAutomationHub.Desktop/ViewModels",
            "ConsolidatedTallyImportViewModel.cs");
        var dashboardXaml = Source("AccountingAutomationHub.Desktop/Views",
            "ConsolidatedBranchDashboardView.xaml");
        var tallyPageXaml = Source("AccountingAutomationHub.Desktop/Views",
            "ConsolidatedTallyImportView.xaml");
        var normalDashboardXaml = Source("AccountingAutomationHub.Desktop/Views",
            "DashboardView.xaml");
        Check(tallyRepository.Contains("ReadConsolidatedVoucherProgress(", StringComparison.Ordinal) &&
              tallyRepository.Contains("FROM TallySourceBindings", StringComparison.Ordinal) &&
              tallyRepository.Contains("AcceptedAwaitingVerification", StringComparison.Ordinal),
            "Consolidated destination status is not read from the durable original binding repository");
        Check(main.Contains("ReadConsolidatedTallyProgressStatus(", StringComparison.Ordinal) &&
              main.Contains("RefreshNowAsync(forceLinkedSynchronization: true)", StringComparison.Ordinal) &&
              main.Contains("_consolidatedTallyImportPage?.RefreshTallyImportProgress();", StringComparison.Ordinal) &&
              main.Contains("_consolidatedDashboardPage?.RefreshTallyImportProgress();", StringComparison.Ordinal),
            "Consolidated Tally partial acceptance does not refresh its durable status/shared authority");
        Check(dashboardVm.Contains("TallyImportProgressStatus", StringComparison.Ordinal) &&
              tallyPageVm.Contains("TallyImportProgressStatus", StringComparison.Ordinal) &&
              tallyPageVm.Contains("finally", StringComparison.Ordinal),
            "Consolidated pages do not show current verified / accepted-awaiting status after import");
        Check(dashboardXaml.Contains("ExistingDashboardWorkspace.OpenLinkedFolderCommand", StringComparison.Ordinal) &&
              dashboardXaml.Contains("ExistingDashboardWorkspace.OpenTallyDiagnosticFolderCommand", StringComparison.Ordinal) &&
              dashboardXaml.Contains("RefreshConsolidatedCommand", StringComparison.Ordinal) &&
              dashboardXaml.Contains("Text=\"Client Identity\"", StringComparison.Ordinal) &&
              !dashboardXaml.Contains("Workspace &amp; Tally Actions", StringComparison.Ordinal) &&
              tallyPageXaml.Contains("views:DashboardView TallyOnlyMode=\"True\"", StringComparison.Ordinal) &&
              !tallyPageXaml.Contains("DockPanel", StringComparison.Ordinal),
            "Consolidated original dashboard/Tally design was changed or linked actions are missing.");
        Check(!dashboardXaml.Contains("ClearAllDataCommand", StringComparison.Ordinal) &&
              !dashboardXaml.Contains("ExportDiagnosticExcelCommand", StringComparison.Ordinal),
            "Consolidated dashboard exposed dangerous parent-accounting mutation/export buttons");
        Check(!normalDashboardXaml.Contains("NormalTallyExportSelection", StringComparison.Ordinal) &&
              normalDashboardXaml.Contains("BankTallyExportMonths", StringComparison.Ordinal) &&
              normalDashboardXaml.Contains("PurchaseTallyExportMonths", StringComparison.Ordinal) &&
              normalDashboardXaml.Contains("SalesTallyExportMonths", StringComparison.Ordinal),
            "The original Tally Export module/month design must be restored unchanged.");
        Console.WriteLine("PASS: consolidated dashboard restores scoped folder/diagnostic actions and live Tally status without parent export or duplicate writes.");
        // Original CL019 report counted only Tally CREATED, which would
        // falsely reject any ALTER-only export (verified 1, CREATED 0) and
        // skip the existing Tally coordinator refresh after bound ALTER.
        // The existing detailed ZIP and coordinator must count independent
        // accepted CREATE and ALTER without issuing any additional POST.
        Check(coordinator.Contains("TallyCreated = acceptedCreates", StringComparison.Ordinal) &&
              coordinator.Contains("TallyAltered = acceptedAlters", StringComparison.Ordinal) &&
              coordinator.Contains("acceptedCreates + acceptedAlters", StringComparison.Ordinal) &&
              main.Contains("tallyAcknowledgedAlterCount", StringComparison.Ordinal) &&
              main.Contains("acknowledgedVoucherAlters: tallyAcknowledgedAlters", StringComparison.Ordinal) &&
              main.Contains("tallyAcknowledgedCreateCount > 0 || tallyAcknowledgedAlterCount > 0", StringComparison.Ordinal) &&
              main.Contains("RefreshNowAsync(forceLinkedSynchronization: true)", StringComparison.Ordinal) &&
              importer.Contains("tallyAcknowledgedAlter?.Invoke()", StringComparison.Ordinal),
            "Original Tally diagnostic/linked cache ignores successful bound ALTER acceptance or merges ALTER into CREATE.");
        Console.WriteLine("PASS: original detailed Tally ZIP and linked coordinator count/refresh verified ALTER separately from CREATE.");


        // CL019 v597 (4 Nov): compare FOUR actual consolidated UI behaviors
        // against the already implemented SINGLE-client code. Do not create
        // another Tally router, connection record, or voucher import method.
        var actualDashboard = Source("AccountingAutomationHub.Desktop/ViewModels",
            "DashboardViewModel.cs");
        var actualTallyPage = Source("AccountingAutomationHub.Desktop/ViewModels",
            "TallyClientViewModel.cs");
        var workspaceCatalog = Source("AccountingAutomationHub.Desktop/Services",
            "BranchAccountingWorkspaceCatalog.cs");
        var shellXaml = Source("AccountingAutomationHub.Desktop", "MainWindow.xaml");
        var singularTallyXaml = Source("AccountingAutomationHub.Desktop/Views",
            "TallyClientView.xaml");

        // 1: Top link stages must use the singular module's saved Client/FY
        // link and explicit pending-selection gate. A branch selection cannot
        // be inherited when the active client switches to Consolidated.
        Check(actualTallyPage.Contains("return link is null || !link.CompanyKey.Equals(company.CompanyKey",
                    StringComparison.Ordinal) &&
              actualTallyPage.Contains("public string LinkActionText => HasLinkedCompany ? \"Change Link\" : \"Confirm Link\";",
                    StringComparison.Ordinal) &&
              main.Contains("public string TallyLinkActionLabel", StringComparison.Ordinal) &&
              main.Contains("return _tallyCoordinator.IsCurrentSelectionPending() ? \"Change Link\" : \"Linked\";",
                    StringComparison.Ordinal) &&
              main.Contains("ConfirmTopTallyLinkAsync, CanConfirmTopTallyLink", StringComparison.Ordinal) &&
              main.Contains("_tallyCoordinator.RestoreSelectionForActiveClientScope();", StringComparison.Ordinal) &&
              coordinator.Contains("public void RestoreSelectionForActiveClientScope()", StringComparison.Ordinal) &&
              coordinator.Contains("_repository.GetLinkForClient(current.Id)", StringComparison.Ordinal) &&
              shellXaml.Contains("Content=\"{Binding TallyLinkActionLabel}\"", StringComparison.Ordinal),
            "TOP LINK: normal ClientId/FY Link / Change Link / Linked stages or workspace selection reset missing.");

        // 2: Original single-client panel hides any module whose source
        // period count is zero. Consolidated reuses those EXACT 12 month
        // tiles and must do the same for Cash AND all other modules.
        foreach (var (label, collection) in new[]
                 {
                     ("Bank", "BankTallyExportMonths"), ("Purchase", "PurchaseTallyExportMonths"),
                     ("Sales", "SalesTallyExportMonths"), ("Cash", "CashTallyExportMonths"),
                     ("Journal", "JournalTallyExportMonths")
                 })
            Check(actualDashboard.Contains(
                      $"public bool Has{label}ExportData => {collection}.Any(option => option.SourceCount > 0);",
                      StringComparison.Ordinal),
                $"ZERO MODULE: {label} is forced visible without positive source counts.");
        Check(normalDashboardXaml.Contains(
                  "Visibility=\"{Binding HasCashExportData, Converter={StaticResource BooleanToVisibilityConverter}}\"",
                  StringComparison.Ordinal) &&
              actualDashboard.Contains("Fill(CashTallyExportMonths, \"Cash\");", StringComparison.Ordinal),
            "CASH ENTRY: original single-client source-count visibility or consolidated prepared XML count mapping was bypassed.");

        // 3: Original card's own green/red/neutral template is unchanged.
        // Consolidated must use its same LIVE CompanyKey/connection + error
        // conditions, never turn green on stored 'Verified' states alone.
        Check(normalDashboardXaml.Contains(
                  "DataTrigger Binding=\"{Binding TallyExportHealthState}\" Value=\"Green\"",
                  StringComparison.Ordinal) &&
              actualDashboard.Contains("var liveAuthorityReady = IsConfirmedTallyLinkLive(liveLink, liveCompany)",
                  StringComparison.Ordinal) &&
              actualDashboard.Contains("!_tallyCoordinator.IsBusy && !HasTallyError;",
                  StringComparison.Ordinal) &&
              actualDashboard.Contains("allCurrentVersionsVerified &&", StringComparison.Ordinal) &&
              actualDashboard.Contains("!_consolidatedDisplayFromSavedPreview ? \"Green\" : \"Red\";",
                  StringComparison.Ordinal),
            "PANEL COLOR: saved prepared preview alone incorrectly paints a green live Tally export state.");

        // 4: Add the PRE-EXISTING singular Tally authority / deleted-voucher
        // recovery page as its OWN consolidated navigation item. The original
        // Tally Import screen stays untouched and branch-prepared writes stay
        // in its verified wrapper, never in normal parent's recovery writer.
        Check(main.Contains("choices.Add(_tallyNavigationItem);", StringComparison.Ordinal) &&
              main.Contains("choices.Add(new NavigationItem(\"Tally Import\"", StringComparison.Ordinal) &&
              main.Contains("() => { _tallyClientViewModel.RefreshLocal(); return _tallyClientViewModel; }",
                  StringComparison.Ordinal) &&
              workspaceCatalog.Contains("[\"Dashboard\", \"Tally Import\", \"Tally\"]",
                  StringComparison.Ordinal) &&
              singularTallyXaml.Contains("Deleted / Missing in Tally", StringComparison.Ordinal) &&
              actualTallyPage.Contains("_coordinator.IsCurrentClientConsolidatedParent", StringComparison.Ordinal) &&
              actualTallyPage.Contains("Consolidated parent restore is blocked.", StringComparison.Ordinal) &&
              coordinator.Contains("public bool IsCurrentClientConsolidatedParent =>",
                  StringComparison.Ordinal),
            "SEPARATE TALLY: original recovery module not reused, or unsafe parent legacy source restore allowed.");
        Console.WriteLine("PASS: all FOUR singular-client Tally parity contracts: top link stages, zero-cash visibility, live-status colors, separate recovery module.");

        Console.WriteLine("PASS: consolidated Tally reuses existing full diagnostic ZIP in parent AutoFolder, captures live evidence and preserves previous ZIP.");
    }
}
