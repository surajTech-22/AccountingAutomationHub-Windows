using System.Reflection;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyOfflineConsolidationLedgerTests
{
    private static void Check(bool okay, string why)
    {
        if (!okay) throw new InvalidOperationException("Offline source Ledger regression: " + why);
    }

    public static void Run()
    {
        var exporter = typeof(TallyAccountingVoucherExportService);
        var ledgerType = exporter.GetNestedType("RequiredLedger", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original RequiredLedger model was removed.");
        var ctor = ledgerType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var append = exporter.GetMethod("AppendRequiredAccountingMasters",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original branch master writer was removed.");
        var bankType = append.GetParameters()[4].ParameterType.GetGenericArguments().Single();

        string Build(bool offline, MasterRecord[] masters, string ledgerName = "Existing Branch Supplier",
            string parent = "Sundry Creditors")
        {
            var values = ctor.GetParameters().Select(parameter => parameter.Name switch
            {
                "Name" => ledgerName,
                "Parent" => parent,
                "Gstin" => "29ABCDE1234F1Z5",
                "State" => "Karnataka",
                "MailingName" => ledgerName,
                _ => ""
            }).Cast<object>().ToArray();
            var required = Array.CreateInstance(ledgerType, 1);
            required.SetValue(ctor.Invoke(values), 0);
            var builder = new StringBuilder();
            append.Invoke(null, [
                builder, new DateTime(2025, 4, 1), required,
                masters, Array.CreateInstance(bankType, 0),
                null, null, false, offline
            ]);
            return builder.ToString();
        }

        // A source master already bound to the branch's Tally company was
        // skipped by the offline writer, producing the "Neither prepared
        // branch XML nor original-company Tally export" failure.
        var bound = new MasterRecord
        {
            Name = "Existing Branch Supplier",
            Parent = "Sundry Creditors",
            Type = "Ledger",
            Source = "Live Tally",
            TallyMasterId = "3091",
            TallyGuid = "source-company-only"
        };
        var offline = XDocument.Parse("<ROOT>" + Build(true, [bound]) + "</ROOT>");
        var ledger = offline.Descendants("LEDGER").Single();
        Check((string?)ledger.Attribute("ACTION") == "Create" &&
              (string?)ledger.Element("PARENT") == "Sundry Creditors" &&
              ledger.Descendants("GSTIN").Any(x => x.Value == "29ABCDE1234F1Z5"),
            "Bound source party master or its original branch GST metadata is missing.");
        Check(!ledger.Descendants().Any(x => x.Name.LocalName is "MASTERID" or "ALTERID" or "GUID"),
            "A foreign branch company identity was copied into the offline template.");

        // The normal individual-company importer must keep its live Tally
        // MASTERID authority and must NOT try to CREATE the same bound Ledger.
        var online = XDocument.Parse("<ROOT>" + Build(false, [bound]) + "</ROOT>");
        Check(!online.Descendants("LEDGER").Any(),
            "Individual-company Tally CREATE behavior was modified.");

        // No source master means a voucher name alone cannot invent a party.
        var absent = XDocument.Parse("<ROOT>" + Build(true, []) + "</ROOT>");
        Check(!absent.Descendants("LEDGER").Any(),
            "Offline exporter manufactured an unverified party master.");

        try
        {
            Build(true, [bound, new MasterRecord {
                Name = bound.Name, Parent = bound.Parent, Type = "Ledger", Source = "Manual"
            }]);
            throw new InvalidOperationException("Ambiguous source masters were accepted.");
        }
        catch (TargetInvocationException e) when (
            e.InnerException is InvalidOperationException fault &&
            fault.Message.Contains("Ambiguous original branch Ledger masters", StringComparison.Ordinal))
        {
            // Correctly fail closed before consolidated XML generation.
        }

        Console.WriteLine("PASS: offline bound source ledgers retain raw party metadata; ordinary Tally CREATE and missing-master gates remain unchanged.");
    }
}
