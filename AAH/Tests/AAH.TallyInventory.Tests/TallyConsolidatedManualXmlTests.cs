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
              method.Contains("ExportFullCompanySnapshotAsync(", StringComparison.Ordinal) &&
              method.Contains("ExportVoucherAuthorityFreshAsync(", StringComparison.Ordinal) &&
              method.Contains("GetSourceBindingVersionState(", StringComparison.Ordinal) &&
              method.Contains("RequireExistingDefaultCashForBankVouchers(", StringComparison.Ordinal),
              "manual XML must use current source binding and live MASTERID/ALTERID/DRCR/master authority");
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
        Check(view.Contains("File.WriteAllText(vouchersPath, manual.VouchersXml", StringComparison.Ordinal) &&
              !view.Contains("File.WriteAllText(vouchersPath, result.VouchersXml", StringComparison.Ordinal) &&
              view.Contains("AAH_CONSOLIDATED_SOURCE_PREVIEW_DO_NOT_IMPORT.txt", StringComparison.Ordinal) &&
              view.Contains("produceManualImportFiles: false", StringComparison.Ordinal),
              "the only manually-importable file must be the materialized XML, not internal CREATE preview");
        Console.WriteLine("PASS: real manual CREATE/ALTER XML, original DR/CR, read-only live identity checks and no Tally posting.");
    }
}
