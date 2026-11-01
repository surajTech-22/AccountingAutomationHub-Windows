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
        Console.WriteLine("PASS: 0x04 in narration repaired; amounts, identities and attributes remain immutable.");
    }
}
