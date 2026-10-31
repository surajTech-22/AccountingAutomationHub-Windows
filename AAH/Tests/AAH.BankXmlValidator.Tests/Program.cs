using System.Globalization;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using AccountingAutomationHub.Infrastructure.Tally;

// Exercises the real production writer and validator in isolation. Nothing is
// sent to Tally and no installed application or accounting database is touched.
internal static class Program
{
    private static int _passed;
    private const string Company = "AAH Test Company";
    private static readonly DateTime Date = new(2025, 4, 1);

    private static void Pass(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Reject(string name, Action action, string diagnostic)
    {
        Pass(name, () =>
        {
            try { action(); }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.StartsWith("Tally Bank XML contract validation failed: ", StringComparison.Ordinal) &&
                    ex.Message.Contains(diagnostic, StringComparison.Ordinal)) return;
                throw new Exception(name + ": unexpected diagnostic: " + ex.Message, ex);
            }
            throw new Exception(name + ": invalid XML was accepted.");
        });
    }

    private static string MasterXml(string name = "Bank & Co", string parent = "Bank Accounts")
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(builder, Company);
        TallyExcelParityXmlWriter.AppendErp9MinimalLedgerMaster(builder, name, parent);
        TallyExcelParityXmlWriter.EndAllMastersImport(builder);
        return builder.ToString();
    }

    private static void Master(string xml, string name = "Bank & Co", string parent = "Bank Accounts") =>
        TallyBankXmlContractValidator.ValidateLedgerMasterImport(xml, Company,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = parent });

    private static TallyErp9BankVoucherExpectation Expected(string type = "Payment", decimal amount = 1250.25m) =>
        new(Date, type, "REF-1", "Bank payment",
            new[] {
                new TallyErp9BankLedgerLine("Party", amount, true),
                new TallyErp9BankLedgerLine("Bank", amount, false)
            });

    private static string VoucherXml(TallyErp9BankVoucherExpectation expected)
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(builder, Company, "Vouchers");
        TallyExcelParityXmlWriter.AppendErp9BankVoucher(builder, expected.VoucherType,
            expected.Date, expected.Reference, expected.Narration, expected.Lines);
        TallyExcelParityXmlWriter.EndErp9DataImport(builder);
        return builder.ToString();
    }

    private static void Voucher(string xml, TallyErp9BankVoucherExpectation expected) =>
        TallyBankXmlContractValidator.ValidateVoucherImport(xml, Company, new[] { expected });

    private static string Change(string xml, string from, string to)
    {
        if (!xml.Contains(from, StringComparison.Ordinal)) throw new Exception("Missing mutation target: " + from);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    private static int Main()
    {
        try
        {
            Pass("Fail is annotated and still throws", () =>
            {
                var method = typeof(TallyBankXmlContractValidator).GetMethod("Fail", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new Exception("Throwing helper is missing.");
                if (method.GetCustomAttribute<DoesNotReturnAttribute>() is null)
                    throw new Exception("DoesNotReturn compiler-flow annotation is missing.");
                try { method.Invoke(null, new object[] { "test" }); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException error &&
                    error.Message == "Tally Bank XML contract validation failed: test") { return; }
                throw new Exception("Fail did not throw the original diagnostic.");
            });
            var master = MasterXml();
            Pass("Valid ERP9 master with escaped name", () => Master(master));
            // Use an empty writer-generated envelope rather than relying on an
            // XML string edit for the actual empty-set acceptance test.
            var emptyBuilder = new StringBuilder();
            TallyExcelParityXmlWriter.BeginAllMastersImport(emptyBuilder, Company);
            TallyExcelParityXmlWriter.EndAllMastersImport(emptyBuilder);
            Pass("Empty master set is valid", () => TallyBankXmlContractValidator.ValidateLedgerMasterImport(
                emptyBuilder.ToString(), Company, new Dictionary<string, string>()));
            Reject("Blank master XML", () => Master("  "), "XML is blank");
            Reject("Malformed master XML", () => Master("<ENVELOPE>"), "not well-formed");
            Reject("Wrong master root", () => Master(Change(master, "<ENVELOPE>", "<ROOT>")), "not well-formed");
            Reject("Wrong master company", () => Master(Change(master, Company, "Other Company")), "SVCURRENTCOMPANY");
            Reject("Wrong master report", () => Master(Change(master, "All Masters", "Vouchers")), "REPORTNAME");
            Reject("Master ALTER is forbidden", () => Master(Change(master, "Action=\"Create\"", "Action=\"Alter\"")), "not ACTION=Create");
            Reject("Wrong master parent", () => Master(Change(master, "<PARENT>Bank Accounts</PARENT>", "<PARENT>Current Assets</PARENT>")), "PARENT must be");
            Reject("Master name mismatch", () => Master(Change(master, "<NAME>Bank &amp; Co</NAME>", "<NAME>Different</NAME>")), "NAME attribute");
            Reject("Unexpected master attribute", () => Master(Change(master, "Action=\"Create\"", "Action=\"Create\" EXTRA=\"1\"")), "unexpected attribute");
            Reject("Unexpected master tag", () => Master(Change(master, "</LEDGER>", "<UNSAFE>1</UNSAFE></LEDGER>")), "unexpected tag");
            Reject("Master namespace", () => Master(Change(master, "<ENVELOPE>", "<ENVELOPE xmlns=\"urn:bad\">")), "unnamespaced");
            Reject("Master mixed with voucher", () => Master(Change(master, "</REQUESTDATA>", "<VOUCHER/></REQUESTDATA>")), "unexpected tag");
            Reject("Master currency override", () => Master(Change(master, "</REQUESTDATA>", "<CURRENCYNAME>USD</CURRENCYNAME></REQUESTDATA>")), "unexpected tag");
            Reject("Duplicate master object", () => Master(Change(master, "</REQUESTDATA>", "<TALLYMESSAGE><LEDGER NAME=\"Bank &amp; Co\" Action=\"Create\"><NAME>Bank &amp; Co</NAME><PARENT>Bank Accounts</PARENT></LEDGER></TALLYMESSAGE></REQUESTDATA>")), "unique LEDGER");
            Reject("Missing master HEADER", () => Master(Change(master, "<HEADER>", "<MISSING>")), "not well-formed");
            Reject("Unsupported import duplicates mode", () => Master(Change(master, "@@DUPIGNORE", "@@DUPCOMBINE")), "IMPORTDUPS");
            Reject("Unapproved master name", () => Master(master, "Another Bank"), "not part of the expected missing-master set");

            foreach (var type in new[] { "Payment", "Receipt", "Contra" })
            {
                var expected = Expected(type);
                var xml = VoucherXml(expected);
                Pass("Valid ERP9 " + type, () => Voucher(xml, expected));
            }
            var voucher = Expected();
            var valid = VoucherXml(voucher);
            Reject("Blank voucher XML", () => Voucher(" ", voucher), "XML is blank");
            Reject("Wrong voucher company", () => Voucher(Change(valid, Company, "Other Company"), voucher), "SVCURRENTCOMPANY");
            Reject("Wrong voucher date", () => Voucher(Change(valid, "20250401", "20250402"), voucher), "DATE must be");
            Reject("Invalid voucher date", () => Voucher(Change(valid, "20250401", "20251301"), voucher), "not YYYYMMDD");
            Reject("Wrong voucher reference", () => Voucher(Change(valid, "REF-1", "REF-2"), voucher), "REFERENCE does not match");
            Reject("Wrong voucher narration", () => Voucher(Change(valid, "Bank payment", "Other payment"), voucher), "NARRATION does not match");
            Reject("Unsupported voucher type", () => Voucher(Change(valid, "<VOUCHERTYPENAME>Payment</VOUCHERTYPENAME>", "<VOUCHERTYPENAME>Journal</VOUCHERTYPENAME>"), voucher), "unsupported VOUCHERTYPENAME");
            Reject("Wrong expected voucher type", () => Voucher(valid, Expected("Receipt")), "VOUCHERTYPENAME does not match");
            Reject("Wrong voucher ledger", () => Voucher(Change(valid, "<LEDGERNAME>Party</LEDGERNAME>", "<LEDGERNAME>Other</LEDGERNAME>"), voucher), "LEDGERNAME does not match");
            Reject("Wrong voucher amount", () => Voucher(Change(valid, "-1250.25", "-1250.26"), voucher), "AMOUNT must be");
            Reject("Zero voucher amount", () => Voucher(Change(valid, "-1250.25", "0.00"), voucher), "zero AMOUNT");
            Reject("Invalid voucher amount", () => Voucher(Change(valid, "-1250.25", "invalid"), voucher), "invalid AMOUNT");
            Reject("Incorrect voucher sign", () => Voucher(Change(valid, "-1250.25", "1250.25"), voucher), "AMOUNT must be");
            Reject("Extra voucher scalar", () => Voucher(Change(valid, "</VOUCHER>", "<UNSAFE>1</UNSAFE></VOUCHER>"), voucher), "unexpected tag");
            Reject("Voucher mixed with master", () => Voucher(Change(valid, "</DATA>", "<LEDGER/></DATA>"), voucher), "unexpected tag");
            Reject("Voucher currency override", () => Voucher(Change(valid, "</DATA>", "<CURRENCYNAME>USD</CURRENCYNAME></DATA>"), voucher), "unexpected tag");
            Reject("Duplicate voucher scalar", () => Voucher(Change(valid, "</VOUCHER>", "<DATE>20250401</DATE></VOUCHER>"), voucher), "duplicate/missing scalar");
            Reject("Unexpected voucher attribute", () => Voucher(Change(valid, "<VOUCHER>", "<VOUCHER ACTION=\"Create\">"), voucher), "must not contain attributes");
            Reject("Wrong voucher count", () => TallyBankXmlContractValidator.ValidateVoucherImport(valid, Company, Array.Empty<TallyErp9BankVoucherExpectation>()), "Voucher count mismatch");
            Reject("Missing voucher body", () => Voucher(Change(valid, "<DATA>", "<OTHER>"), voucher), "not well-formed");
            OwnerGroupSampleExportTests.Run();
            Console.WriteLine("PASS: " + _passed.ToString(CultureInfo.InvariantCulture) + "/" + _passed.ToString(CultureInfo.InvariantCulture) + " Bank XML scenarios.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }
}
