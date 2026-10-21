using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

internal static class TallySalesXmlImportTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAH-tally-sales-xml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sales.xml");
        File.WriteAllText(path, """
<ENVELOPE>
  <HEADER><VERSION>1</VERSION><TALLYREQUEST>Import</TALLYREQUEST><TYPE>Data</TYPE><ID>Vouchers</ID></HEADER>
  <BODY><DATA><TALLYMESSAGE>
    <LEDGER><NAME.LIST><NAME>KUSHAL STONE CRUSHERS</NAME></NAME.LIST><PARENT>Sundry Debtors</PARENT><ADDRESS.LIST><ADDRESS>TUMKUR</ADDRESS></ADDRESS.LIST><STATENAME>Karnataka</STATENAME><PARTYGSTIN>29AATFK6640D1ZZ</PARTYGSTIN></LEDGER>
    <LEDGER><NAME.LIST><NAME>GST Sales</NAME></NAME.LIST><PARENT>Sales Accounts</PARENT></LEDGER>
    <LEDGER><NAME.LIST><NAME>Output Central Tax</NAME></NAME.LIST><PARENT>Duties &amp; Taxes</PARENT></LEDGER>
    <LEDGER><NAME.LIST><NAME>Output State Tax</NAME></NAME.LIST><PARENT>Duties &amp; Taxes</PARENT></LEDGER>
    <VOUCHER>
      <DATE>20250402</DATE><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME><VOUCHERNUMBER>1</VOUCHERNUMBER><REFERENCE>1</REFERENCE>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>CASH</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-1180</AMOUNT></ALLLEDGERENTRIES.LIST>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>GST Sales</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>1000</AMOUNT></ALLLEDGERENTRIES.LIST>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output Central Tax</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>90</AMOUNT></ALLLEDGERENTRIES.LIST>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output State Tax</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>90</AMOUNT></ALLLEDGERENTRIES.LIST>
    </VOUCHER>
    <VOUCHER>
      <DATE>20250403</DATE><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME><VOUCHERNUMBER>2</VOUCHERNUMBER><REFERENCE>2</REFERENCE>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>UPI SALES</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-1050</AMOUNT></ALLLEDGERENTRIES.LIST>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>GST Sales</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>1000</AMOUNT></ALLLEDGERENTRIES.LIST>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output Central Tax</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>25</AMOUNT></ALLLEDGERENTRIES.LIST>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output State Tax</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>25</AMOUNT></ALLLEDGERENTRIES.LIST>
    </VOUCHER>
    <VOUCHER>
      <DATE>20250404</DATE><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME><VOUCHERNUMBER>3</VOUCHERNUMBER><REFERENCE>3</REFERENCE>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>INTERSTATE CUSTOMER</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-1180</AMOUNT></ALLLEDGERENTRIES.LIST>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>GST Sales</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>1000</AMOUNT></ALLLEDGERENTRIES.LIST>
      <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output IGST</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>180</AMOUNT></ALLLEDGERENTRIES.LIST>
    </VOUCHER>
  </TALLYMESSAGE></DATA></BODY>
</ENVELOPE>
""");

        try
        {
            var service = new SalesManualImportService();
            var inspection = service.Inspect(path);
            Check(inspection.IsComplete, "Tally Sales XML was not recognized as complete.");
            Check(inspection.DetectedMode == "Tally Voucher XML", "Tally XML mode was not reported.");

            var result = service.Import(path, 1, "29ABCDE1234F1Z5", "Fixture", "2025-26",
                new Dictionary<string, string>(), "LOCAL", "SINGLE");
            Check(result.Records.Count == 3, "Expected three imported Tally Sales vouchers.");
            var cash = result.Records.Single(x => x.DocumentNumber == "1");
            Check(cash.CustomerName == "Cash", "CASH party was not normalized to the standard Cash ledger.");
            Check(cash.SalesLedger == "GST Sales", "Sales ledger was not preserved.");
            Check(cash.Cgst == 90m && cash.Sgst == 90m && cash.TaxableValue == 1000m && cash.DocumentValue == 1180m, "Cash Sales amounts were not parsed correctly.");
            Check(cash.Rate == 18m, "GST rate was not derived from Tally tax ledgers.");
            var upi = result.Records.Single(x => x.DocumentNumber == "2");
            Check(upi.CustomerName == "UPI SALES", "UPI SALES party ledger was not preserved.");
            Check(upi.Rate == 5m, "UPI SALES GST rate was not derived correctly.");
            var interstate = result.Records.Single(x => x.DocumentNumber == "3");
            Check(interstate.Igst == 180m && interstate.Cgst == 0m && interstate.Sgst == 0m, "Tally XML IGST authority was rewritten by the saved LOCAL spreadsheet setting.");
            Check(interstate.Rate == 18m, "Tally XML IGST rate was not derived correctly.");

            var masters = new TallyMasterImportService().Parse(path, 1, "2025-26", "Fixture");
            Check(masters.Records.Any(x => x.Type == "Ledger" && x.Name == "KUSHAL STONE CRUSHERS" && x.Parent == "Sundry Debtors"), "Nested NAME.LIST Tally Ledger was not parsed.");
            Check(masters.Records.Any(x => x.Type == "Ledger" && x.Name == "GST Sales" && x.Parent == "Sales Accounts"), "Sales Ledger Master was not parsed from embedded Tally XML.");
            var wrongFyRejected = false;
            try
            {
                service.Import(path, 1, "29ABCDE1234F1Z5", "Fixture", "2024-25", new Dictionary<string, string>(), "LOCAL", "SINGLE");
            }
            catch (InvalidDataException)
            {
                wrongFyRejected = true;
            }
            Check(wrongFyRejected, "Tally Sales XML from another Financial Year was accepted into the selected Client/FY.");

            // R9 FIXED16 R13: match the exact structural shape of the supplied BTC
            // Crystal/Tally Sales Registers. One daily row contains a multiline
            // Totals-for/voucher-range cell and a multiline GST Type cell.
            var btcWorkbook = new OpenXmlWorkbookData
            {
                FilePath = "btc 25-26 fixture.xls",
                Worksheets = new Dictionary<string, OpenXmlWorksheetData>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Sales Register"] = new OpenXmlWorksheetData
                    {
                        Name = "Sales Register",
                        State = "visible",
                        MaxRow = 7,
                        Rows = new Dictionary<int, IReadOnlyDictionary<int, string>>
                        {
                            [1] = new Dictionary<int, string> { [1] = "BALAJI TRADING CO." },
                            [3] = new Dictionary<int, string> { [1] = "Sales Register for the period from 01-04-2025 to 30-06-2025" },
                            [5] = new Dictionary<int, string>
                            {
                                [1] = "Date", [2] = "GST Type", [3] = "Taxable\nValue",
                                [4] = "IGST\nAmount", [5] = "CGST\nAmount", [6] = "SGST\nAmount", [7] = "Bill\nAmount"
                            },
                            [6] = new Dictionary<int, string>
                            {
                                [1] = "Totals for 02/04/2025\n           1 -  10 ",
                                [2] = "18% Goods\n12% Goods\n5% Goods\nExempted",
                                [3] = "1000.00\n2000.00\n3000.00\n400.00",
                                [4] = "0.00\n0.00\n0.00\n0.00",
                                [5] = "90.00\n120.00\n75.00\n0.00",
                                [6] = "90.00\n120.00\n75.00\n0.00",
                                [7] = "6990.00"
                            },
                            [7] = new Dictionary<int, string>
                            {
                                [1] = "Totals for 03/04/2025\n          11 -  18 ",
                                [2] = "18% Goods\n5% Goods\nExempted",
                                [3] = "500.00\n700.00\n100.00",
                                [4] = "0.00\n0.00\n0.00",
                                [5] = "45.00\n17.50\n0.00",
                                [6] = "45.00\n17.50\n0.00",
                                [7] = "1425.00"
                            }
                        }
                    }
                }
            };
            var btcInspection = service.Inspect(btcWorkbook, btcWorkbook.FilePath);
            Check(btcInspection.IsComplete && btcInspection.IsTallyRateSummaryMapping, "BTC multiline Tally Sales Register was sent to generic Header Mapping.");
            Check(btcInspection.DetectedMode == "Tally Sales Register Rate Summary", "BTC Tally rate-summary mode was not reported.");
            var btcResult = service.Import(
                btcWorkbook, btcWorkbook.FilePath, 1, "29ABCDE1234F1Z5", "Fixture", "2025-26",
                new Dictionary<string, string>(), "LOCAL", "SINGLE", "BTC-FIXTURE-HASH");
            Check(btcResult.IsTallyRateSummaryMapping, "BTC import did not remain a rate-evidence mapping.");
            Check(btcResult.Records.Count == 2, "BTC direct statement import did not create one daily non-exportable Sales summary row per Totals range.");
            Check(btcResult.Records.All(row => row.SourceOrigin == "Tally Sales Register Summary"), "BTC direct rows were not tagged as non-exportable statement summaries.");
            Check(btcResult.Records[0].DocumentNumber == "Vouchers 1-10" && btcResult.Records[0].TaxableValue == 6400m && btcResult.Records[0].DocumentValue == 6990m, "BTC first daily statement totals were not preserved exactly.");
            Check(btcResult.RateMappings.Count == 2, "BTC rate-summary did not produce one mapping per daily Totals row.");
            var firstBtc = btcResult.RateMappings[0];
            Check(firstBtc.SalesDate == new DateTime(2025, 4, 2) && firstBtc.VoucherFrom == 1 && firstBtc.VoucherTo == 10, "BTC date/voucher range was not parsed from the multiline Totals cell.");
            Check(firstBtc.Rates.SequenceEqual(new[] { 0m, 5m, 12m, 18m }), "BTC multiline GST Type buckets were not parsed exactly.");
            var secondBtc = btcResult.RateMappings[1];
            Check(secondBtc.VoucherFrom == 11 && secondBtc.VoucherTo == 18 && secondBtc.Rates.SequenceEqual(new[] { 0m, 5m, 18m }), "BTC second daily GST buckets were not kept separate.");

            Console.WriteLine("PASS Tally Sales XML recognition, CASH/UPI party handling, exact IGST authority, GST arithmetic, FY isolation, embedded NAME.LIST Masters and direct BTC multiline statement-to-Sales summary mapping.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
