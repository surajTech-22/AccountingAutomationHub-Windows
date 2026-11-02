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
        Console.WriteLine("PASS: consolidated Tally reuses existing full diagnostic ZIP in parent AutoFolder, captures live evidence and preserves previous ZIP.");
    }
}
