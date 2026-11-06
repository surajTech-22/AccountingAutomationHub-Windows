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

        Check(coordinator.Contains("bool forceFullBaseline = true)", StringComparison.Ordinal) &&
              coordinator.Contains("forceFullBaseline: forceLinkedSynchronization && forceFullBaseline", StringComparison.Ordinal) &&
              coordinator.Contains("await SynchronizeLinkedCompanyAsync(", StringComparison.Ordinal) &&
              !coordinator.Contains("requireConsolidatedImportAuthority", StringComparison.Ordinal) &&
              !coordinator.Contains("RequireFreshConsolidatedImportCheckpointAsync(", StringComparison.Ordinal),
              "individual-client normal Tally coordinator must not contain consolidated-specific guards");
        Check(wrapper.Contains("normalCoordinator.RefreshNowAsync(", StringComparison.Ordinal) &&
              wrapper.Contains("forceLinkedSynchronization: true", StringComparison.Ordinal) &&
              wrapper.Contains("forceFullBaseline: true", StringComparison.Ordinal) &&
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
              !wrapper.Contains("ReplaceVoucherIndex(", StringComparison.Ordinal),
              "consolidated explicit import must use the individual full-FY baseline and native Voucher proof without independent duplicate snapshots or source-binding writes");
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
        Console.WriteLine("PASS: direct consolidated Tally import reuses original FY coordinator and voucher ALTER/CREATE; separate Generate button removed.");
    }
}
