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


        Console.WriteLine("PASS: consolidated Tally reuses existing full diagnostic ZIP in parent AutoFolder, captures live evidence and preserves previous ZIP.");
    }
}
