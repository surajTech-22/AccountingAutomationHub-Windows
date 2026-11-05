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
        var importer = Read(root!, "AccountingAutomationHub.Infrastructure/Tally/TallyConsolidatedVoucherImportService.cs");

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
        var sync = export.IndexOf("await _tallyCoordinator.RefreshNowAsync(", StringComparison.Ordinal);
        var call = export.IndexOf("var result = await importer.ImportAsync(", StringComparison.Ordinal);
        Check(prepare >= 0 && sync > prepare && call > sync &&
              export.Contains("forceLinkedSynchronization: true, forceFullBaseline: false", StringComparison.Ordinal) &&
              export.Contains("produceManualImportFiles: false", StringComparison.Ordinal),
              "direct import must use normal existing coordinator delta authority after preparing source, before unchanged importer");
        Check(export.Contains("if (separateTypes)", StringComparison.Ordinal) &&
              export.IndexOf("await ImportConsolidatedMastersAsync(", StringComparison.Ordinal) < prepare &&
              export.Contains("SaveConsolidatedExistingDiagnosticsAsync(", StringComparison.Ordinal) &&
              export.Contains("tallyAcknowledgedAlterCount", StringComparison.Ordinal),
              "master-first, ALTER and diagnostic flow must remain intact");

        Check(coordinator.Contains("bool forceFullBaseline = true)", StringComparison.Ordinal) &&
              coordinator.Contains("forceFullBaseline: forceLinkedSynchronization && forceFullBaseline", StringComparison.Ordinal) &&
              coordinator.Contains("await SynchronizeLinkedCompanyAsync(", StringComparison.Ordinal),
              "pre-import must reuse original normal Tally coordinator rather than a second snapshot/checker");
        Check(importer.Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) &&
              importer.Contains("BuildBoundConsolidatedVoucherAlterXml(", StringComparison.Ordinal) &&
              importer.Contains("ReadSynchronizedVoucherAuthorityAsync(", StringComparison.Ordinal) &&
              importer.Contains("ReconcileMissingVoucherBindingAuthority(", StringComparison.Ordinal),
              "original bound MASTERID/ALTERID/DRCR/duplicate gates must remain intact");
        Console.WriteLine("PASS: direct consolidated Tally import reuses original FY coordinator and voucher ALTER/CREATE; separate Generate button removed.");
    }
}
