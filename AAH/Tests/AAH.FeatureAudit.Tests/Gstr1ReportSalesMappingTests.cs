using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

internal static class Gstr1ReportSalesMappingTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        const string registration = "29AAQCM7513B1ZC";
        var rows = new Dictionary<int, IReadOnlyDictionary<int, string>>
        {
            [1] = new Dictionary<int, string> { [1] = "Period", [2] = "April 2025 - April 2025" },
            [3] = new Dictionary<int, string> { [1] = "1. GSTIN", [2] = registration },
            [9] = Indexed(
                "GSTIN/UIN", "Party Name", "Transaction Type", "Invoice No.", "Invoice Date",
                "Invoice Value", "Rate", "Cess Rate", "Taxable value",
                "Integrated Tax Amount", "Central Tax Amount", "State/UT Tax Amount",
                "Cess Amount", "Place of Supply(Name of state)"),
            // The portal's April "Cess Amount" is an effective GST% formula.
            [11] = Indexed(
                "24", "Customer A", "Sale", "MTIPL-1", "23/04/2025",
                "1000", "18", "0", "847.46", "0", "76.27", "76.27", "18.000013", "Karnataka"),
            // One invoice represented by two same-rate tax lines: one voucher.
            [12] = Indexed(
                "24", "Customer B", "Sale", "MTIPL-2", "24/04/2025",
                "1000", "18", "0", "600", "0", "54", "54", "0", "Karnataka"),
            [13] = Indexed(
                "24", "Customer B", "Sale", "MTIPL-2", "24/04/2025",
                "1000", "18", "0", "247.46", "0", "22.27", "22.27", "0", "Karnataka"),
            // Verify that the generic UI's LOCAL setting does not rewrite an
            // interstate IGST invoice into local CGST/SGST.
            [14] = Indexed(
                "24", "Customer C", "Sale", "MTIPL-3", "25/04/2025",
                "1180", "18", "0", "1000", "180", "0", "0", "0", "23"),
            [15] = new Dictionary<int, string> { [1] = "Total", [6] = "3180" }
        };
        var workbook = new OpenXmlWorkbookData
        {
            FilePath = "fixture-GSTR1-Report.xlsx",
            Uses1904DateSystem = false,
            Worksheets = new Dictionary<string, OpenXmlWorksheetData>
            {
                ["GSTR1 Report"] = new OpenXmlWorksheetData
                {
                    Name = "GSTR1 Report", State = "visible", MaxRow = 15, Rows = rows
                },
                // Must NOT be imported as a second Sales document stream.
                ["b2cs"] = new OpenXmlWorksheetData
                {
                    Name = "b2cs", State = "visible", MaxRow = 3,
                    Rows = new Dictionary<int, IReadOnlyDictionary<int, string>>
                    {
                        [1] = Indexed("Month", "Rate", "Taxable Value", "Place of Supply"),
                        [2] = Indexed("Apr-25", "18", "1000", "Karnataka")
                    }
                }
            }
        };

        var service = new SalesManualImportService();
        var inspection = service.Inspect(workbook, workbook.FilePath);
        Check(inspection.IsComplete, "GSTR1 Report did not auto-map to complete Sales fields.");
        Check(inspection.Headers.Contains("Invoice No."), "Normalized invoice identity header missing.");
        Check(inspection.Headers.Contains("Integrated Tax"), "Normalized tax header missing.");

        var result = service.Import(workbook, workbook.FilePath, 1,
            registration, "Fixture Co", "2025-26",
            new Dictionary<string, string>(), "LOCAL", "SINGLE", "fixturehash");

        Check(result.Records.Count == 3, "Repeated invoice/secondary tabs created duplicate vouchers.");
        var first = result.Records.Single(record => record.DocumentNumber == "MTIPL-1");
        Check(first.Cess == 0m && first.TaxableValue == 847.46m &&
              first.Cgst == 76.27m && first.Sgst == 76.27m &&
              first.DocumentValue == 1000m, "Effective GST% was incorrectly used as monetary Cess.");
        Check(first.CustomerGstin.Length == 0, "Portal's '24' placeholder became a customer GSTIN.");

        var second = result.Records.Single(record => record.DocumentNumber == "MTIPL-2");
        Check(second.TaxableValue == 847.46m && second.Cgst == 76.27m &&
              second.Sgst == 76.27m && second.DocumentValue == 1000m,
            "Repeated tax rows were not folded into one original invoice value.");

        var interstate = result.Records.Single(record => record.DocumentNumber == "MTIPL-3");
        Check(interstate.Igst == 180m && interstate.Cgst == 0m && interstate.Sgst == 0m &&
              interstate.PlaceOfSupply == "23",
              "Portal's inter-state IGST or numeric POS was overwritten by the generic local tax setting.");

        var refused = false;
        try
        {
            service.Import(workbook, workbook.FilePath, 1, "27AAQCM7513B1ZC",
                "Wrong Branch", "2025-26", new Dictionary<string, string>(),
                string.Empty, string.Empty, "fixturehash");
        }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "A GSTR1 Report from another GSTIN was imported into the selected branch.");

        refused = false;
        try
        {
            service.Import(workbook, workbook.FilePath, 1, registration,
                "Fixture Co", "2026-27", new Dictionary<string, string>(),
                string.Empty, string.Empty, "fixturehash");
        }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "A GSTR1 Report from another FY was imported.");

        Console.WriteLine("PASS GSTR1 Report-only Sales mapping, false Cess protection, line aggregation, GSTIN/FY guards.");
    }

    private static IReadOnlyDictionary<int, string> Indexed(params string[] values) =>
        values.Select((value, index) => (value, column: index + 1))
            .ToDictionary(item => item.column, item => item.value);
}
