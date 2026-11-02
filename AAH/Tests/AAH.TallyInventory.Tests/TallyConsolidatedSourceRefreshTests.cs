using System.Text;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedSourceRefreshTests
{
    private static void Check(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException("Consolidated source refresh: " + why);
    }

    private const string Prepared = """
        <ENVELOPE>
          <HEADER><TALLYREQUEST>Import Data</TALLYREQUEST></HEADER>
          <BODY><IMPORTDATA>
            <REQUESTDESC><REPORTNAME>All Masters</REPORTNAME>
              <STATICVARIABLES><SVCURRENTCOMPANY>AAH Prepared Branch CL-KA</SVCURRENTCOMPANY></STATICVARIABLES>
            </REQUESTDESC>
            <REQUESTDATA>
              <TALLYMESSAGE><LEDGER NAME="Supplier" ACTION="Create">
                <NAME>Supplier</NAME><PARENT>Sundry Creditors</PARENT>
              </LEDGER></TALLYMESSAGE>
              <TALLYMESSAGE><VOUCHER ACTION="Create">
                <DATE>20250501</DATE><VOUCHERTYPENAME>Purchase</VOUCHERTYPENAME>
                <REFERENCE>INV-1</REFERENCE>
              </VOUCHER></TALLYMESSAGE>
            </REQUESTDATA>
          </IMPORTDATA></BODY>
        </ENVELOPE>
        """;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "aah-branch-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var xmlPath = Path.Combine(root, "AAH_TALLY_LIVE_CURRENT.xml");
            var statusPath = Path.Combine(root, "AAH_TALLY_LIVE_CURRENT_STATUS.txt");
            const string code = "CL-KA";
            const string fy = "2025-26";
            var sourceHash = TallyConsolidatedBranchXmlSnapshotCache.Sha256("original AAH purchase, master, journal, bank, stock");
            var revisedHash = TallyConsolidatedBranchXmlSnapshotCache.Sha256("changed AAH purchase, master, journal, bank, stock");

            bool Reusable(string fingerprint) =>
                TallyConsolidatedBranchXmlSnapshotCache.TryRead(
                    xmlPath, statusPath, code, fy, fingerprint, out _, out _);
            // Older Live XML without an AAH source fingerprint is never reused.
            File.WriteAllText(xmlPath, Prepared);
            File.WriteAllText(statusPath,
                "Client: CL-KA | FY 2025-26\nReady in XML: 1\n", Encoding.UTF8);
            Check(!Reusable(sourceHash), "legacy unstamped Live XML bypassed source recheck");

            var baseStatus = "Accounting Automation Hub - Live Tally XML\n" +
                             "Source: saved offline branch accounting; no Tally posting\n" +
                             "Client: CL-KA | FY 2025-26\n" +
                             "Tally company: AAH Prepared Branch CL-KA\n" +
                             "Ready in XML: 1\n";
            File.WriteAllText(statusPath,
                TallyConsolidatedBranchXmlSnapshotCache.StampStatus(baseStatus, sourceHash, Prepared));
            Check(Reusable(sourceHash), "unchanged AAH input did not reuse exact prepared XML");
            Check(!Reusable(revisedHash), "changed AAH source incorrectly reused previous XML");
            Check(!TallyConsolidatedBranchXmlSnapshotCache.TryRead(
                xmlPath, statusPath, "CL-GJ", fy, sourceHash, out _, out _),
                "one branch reused a different branch's source");
            Check(!TallyConsolidatedBranchXmlSnapshotCache.TryRead(
                xmlPath, statusPath, code, "2024-25", sourceHash, out _, out _),
                "previous FY XML was accepted");

            File.WriteAllText(xmlPath, Prepared.Replace("INV-1", "INV-2"), Encoding.UTF8);
            Check(!Reusable(sourceHash), "edited source XML passed original SHA256 integrity");
            // An actual regeneration writes the new XML + new verified status.
            var updated = Prepared.Replace("INV-1", "INV-2");
            File.WriteAllText(statusPath,
                TallyConsolidatedBranchXmlSnapshotCache.StampStatus(baseStatus, revisedHash, updated));
            Check(Reusable(revisedHash), "changed AAH data regenerated but new cache was rejected");

            File.WriteAllText(statusPath,
                TallyConsolidatedBranchXmlSnapshotCache.StampStatus(
                    baseStatus.Replace("Ready in XML: 1", "Ready in XML: 2"), revisedHash, updated));
            Check(!Reusable(revisedHash), "incomplete snapshot voucher count reused");

            // Production contract: source fingerprint uses persisted inputs and
            // the original export path, never a new consolidated voucher writer.
            var assemblyRoot = new DirectoryInfo(AppContext.BaseDirectory);
            while (assemblyRoot is not null && !Directory.Exists(Path.Combine(
                       assemblyRoot.FullName, "AAH", "src", "AccountingAutomationHub.Desktop")))
                assemblyRoot = assemblyRoot.Parent;
            Check(assemblyRoot is not null, "source checkout root unavailable");
            var main = File.ReadAllText(Path.Combine(assemblyRoot!.FullName, "AAH", "src",
                "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs"));
            var exporter = File.ReadAllText(Path.Combine(assemblyRoot.FullName, "AAH", "src",
                "AccountingAutomationHub.Infrastructure", "Tally", "TallyAccountingVoucherExportService.cs"));
            Check(main.Contains("TallyConsolidatedBranchXmlSnapshotCache.TryRead(", StringComparison.Ordinal) &&
                  main.Contains("ComputeOfflineConsolidationSourceFingerprint(branchClient)", StringComparison.Ordinal) &&
                  main.Contains("return (cachedXml, cachedStatus);", StringComparison.Ordinal) &&
                  main.Contains("_branchXmlPreparationService.GenerateLiveXml(", StringComparison.Ordinal) &&
                  main.Contains("offlineConsolidatedBranchSource: true", StringComparison.Ordinal) &&
                  main.Contains("TallyConsolidatedBranchSourcePolicy.RequirePreparedVoucherCount(", StringComparison.Ordinal),
                "the consolidated wrapper bypassed the existing individual exporter or the cache recheck");
            foreach (var repository in new[] {
                         "_purchaseRepository.GetRecords(", "_salesRepository.GetRecords(",
                         "_journalRepository.GetVouchers(", "_journalRepository.GetSettings(",
                         "_bankingRepository.GetCashEntries(", "_bankingRepository.GetAccounts(",
                         "_masterRepository.Get(", "_stockMasterRepository.GetStockItems(",
                         "_stockMasterRepository.GetStockItemUnitProfiles(",
                         "_stockMasterRepository.GetUnits(", "_stockMasterRepository.GetSimpleMasters(" })
                Check(exporter.Contains(repository, StringComparison.Ordinal),
                    "source fingerprint or original exporter missing " + repository);
            Console.WriteLine("PASS: consolidated XML reuses unchanged verified branch source, regenerates after updates and rejects stale/tampered/cross-branch XML.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
