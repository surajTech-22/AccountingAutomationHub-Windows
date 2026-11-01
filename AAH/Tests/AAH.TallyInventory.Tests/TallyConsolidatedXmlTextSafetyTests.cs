using System.Xml.Linq;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedXmlTextSafetyTests
{
    private static void Check(bool value, string error)
    {
        if (!value) throw new InvalidOperationException("Consolidated XML text: " + error);
    }
    private static void Reject(Action check, string error)
    {
        try { check(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe control character accepted: " + error);
    }
    public static void Run()
    {
        var xml = "<ENVELOPE><TALLYMESSAGE><VOUCHER><NARRATION>NEFT" +
            '\u0004' + " paid</NARRATION><DATE>20250501</DATE><AMOUNT>-100.25</AMOUNT>" +
            "<LEDGERNAME>Karnataka Bank</LEDGERNAME></VOUCHER></TALLYMESSAGE></ENVELOPE>";
        var repaired = TallyConsolidatedXmlTextSafety.Repair(xml);
        var doc = XDocument.Parse(repaired.Xml);
        Check(repaired.RemovedControlCharacters == 1 &&
              doc.Descendants("AMOUNT").Single().Value == "-100.25" &&
              doc.Descendants("NARRATION").Single().Value == "NEFT paid",
              "0x04 narration character repair changed accounting or failed.");
        var again = TallyConsolidatedXmlTextSafety.Repair(repaired.Xml);
        Check(again.RemovedControlCharacters == 0 && again.Xml == repaired.Xml,
            "Valid source XML was modified.");
        Reject(() => TallyConsolidatedXmlTextSafety.Repair(xml.Replace(
            "<AMOUNT>-100.25</AMOUNT>", "<AMOUNT>-100" + '\u0004' + ".25</AMOUNT>")),
            "DR/CR amount was modified");
        Reject(() => TallyConsolidatedXmlTextSafety.Repair(xml.Replace(
            "Karnataka Bank", "Karnataka" + '\u0004' + " Bank")),
            "Tally ledger name was modified");
        Reject(() => TallyConsolidatedXmlTextSafety.Repair(xml.Replace(
            "<VOUCHER>", "<VOUCHER ATTR=\"A" + '\u0004' + "\">")),
            "XML attribute was changed");
        // The reported real-company failure occurs in the LIVE master readback,
        // not necessarily in the already-repaired branch voucher XML. Reuse the
        // repair on that independent path too and keep protected MASTERID strict.
        var liveMasterXml = "<ENVELOPE><TALLYMESSAGE><LEDGER NAME=\\"Bank\\" ACTION=\\"Create\\">" +
            "<NAME>Bank</NAME><PARENT>Bank Accounts</PARENT><MASTERID>42</MASTERID>" +
            "<DESCRIPTION>Account" + '\u0004' + " description</DESCRIPTION>" +
            "</LEDGER></TALLYMESSAGE></ENVELOPE>";
        var liveMasters = TallyBranchConsolidationImportService.ReadLiveMasters(liveMasterXml);
        Check(liveMasters.Count == 1 && liveMasters[0].Name == "Bank" &&
              liveMasters[0].Parent == "Bank Accounts" && liveMasters[0].TallyMasterId == "42",
              "live Tally master readback lost authoritative master identity or could not repair description.");
        Reject(() => TallyBranchConsolidationImportService.ReadLiveMasters(
            liveMasterXml.Replace("<MASTERID>42</MASTERID>", "<MASTERID>4" + '\u0004' + "2</MASTERID>")),
            "live master MASTERID control byte was permitted");

        // Original Dashboard Tally Link and Export/Auto Sync design must be
        // retained for consolidated importing: no master/export strip above it.
        DirectoryInfo? root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "Views",
                   "ConsolidatedTallyImportView.xaml")))
            root = root.Parent;
        Check(root is not null, "source root unavailable for consolidated Tally original-layout regression");
        var viewsDir = Path.Combine(root!.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "Views");
        var groupView = File.ReadAllText(Path.Combine(viewsDir, "ConsolidatedTallyImportView.xaml"));
        var dashboardView = File.ReadAllText(Path.Combine(viewsDir, "DashboardView.xaml"));
        Check(groupView.Contains("ConsolidatedImportWorkspace=", StringComparison.Ordinal) &&
              groupView.Contains("TallyOnlyMode=\\"True\\"", StringComparison.Ordinal) &&
              !groupView.Contains("Branch master import", StringComparison.Ordinal) &&
              dashboardView.IndexOf("ConsolidatedImportWorkspace, ElementName=DashboardRoot", StringComparison.Ordinal) >
              dashboardView.IndexOf("Tally Export / Auto Sync", StringComparison.Ordinal) &&
              dashboardView.Contains("Command=\\"{Binding ImportConsolidatedVouchersCommand}\\"", StringComparison.Ordinal) &&
              dashboardView.Contains("Command=\\"{Binding ExportSelectedToTallyCommand}\\"", StringComparison.Ordinal),
              "Consolidated import displaced the original Tally Export card or altered the normal exporter.");
        Console.WriteLine("PASS: original Tally card reused; consolidated import stays in manual Export location.");

        Console.WriteLine("PASS: 0x04 in narration repaired; amounts, identities and attributes remain immutable.");
    }
}
