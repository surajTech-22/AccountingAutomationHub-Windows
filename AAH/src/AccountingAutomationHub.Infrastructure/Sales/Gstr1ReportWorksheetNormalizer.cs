using System.Globalization;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Infrastructure.Sales;

/// <summary>
/// Adapts only the GST portal's "GSTR1 Report" invoice worksheet to the existing
/// Sales Manual Import field catalog. Other tabs (b2b, b2cs, hsn, docs, etc.)
/// are summaries/secondary presentations and must not be imported again.
/// </summary>
internal static class Gstr1ReportWorksheetNormalizer
{
    private static readonly string[] OutputHeaders =
    [
        "Invoice No.", "Invoice Date", "Party Name", "GSTIN/UIN", "Document Type",
        "Invoice Value", "Rate", "Taxable Value", "Integrated Tax", "Central Tax",
        "State/UT Tax", "Cess Amount", "Place of Supply", "Round Off", "Narration"
    ];

    public static bool TryNormalize(
        OpenXmlWorkbookData source,
        out OpenXmlWorkbookData normalized,
        string? selectedClientGstin = null,
        string? financialYear = null)
    {
        normalized = source;
        var report = source.Worksheets.Values.FirstOrDefault(sheet =>
            sheet.Name.Trim().Equals("GSTR1 Report", StringComparison.OrdinalIgnoreCase));
        if (report is null) return false;

        var headers = FindHeader(report);
        if (headers is null)
            throw new InvalidDataException("GSTR1 Report worksheet was found but its invoice/date/tax headers are not complete.");

        var reportGstin = FindReportGstin(report);
        if (reportGstin.Length != 15)
            throw new InvalidDataException("GSTR1 Report has no valid 15-character supplier GSTIN. The import cannot be assigned safely to a branch.");

        if (!string.IsNullOrWhiteSpace(selectedClientGstin) &&
            !reportGstin.Equals(NormalizeGstin(selectedClientGstin), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"GSTR1 Report GSTIN {reportGstin} does not match the selected AAH branch GSTIN. Select the correct branch; no rows were imported.");
        }

        var values = new List<InvoiceLine>();
        for (var row = headers.Row + 1; row <= report.MaxRow; row++)
        {
            var invoice = Cell("INVOICENO", "INVOICENUMBER");
            if (invoice.Length == 0 || invoice.Equals("Total", StringComparison.OrdinalIgnoreCase))
                continue;

            var dateText = Cell("INVOICEDATE");
            if (!TryDate(dateText, source.Uses1904DateSystem, out var date))
                throw new InvalidDataException($"GSTR1 Report invoice {invoice} at row {row} has an unreadable date '{dateText}'.");

            if (!string.IsNullOrWhiteSpace(financialYear) && !IsInFinancialYear(date, financialYear))
                throw new InvalidDataException($"GSTR1 Report invoice {invoice} is dated {date:dd/MM/yyyy} outside selected FY {financialYear}.");

            var invoiceValue = Amount(Cell("INVOICEVALUE"));
            var taxable = Amount(Cell("TAXABLEVALUE"));
            var igst = Amount(Cell("INTEGRATEDTAXAMOUNT", "INTEGRATEDTAX"));
            var cgst = Amount(Cell("CENTRALTAXAMOUNT", "CENTRALTAX"));
            var sgst = Amount(Cell("STATEUTTAXAMOUNT", "STATEUTTAX"));
            var apparentCess = Amount(Cell("CESSAMOUNT"));
            var amountWithoutCess = taxable + igst + cgst + sgst;

            // Some GST portal exports place a calculated effective tax percentage
            // (e.g. 18.000013) under the column "Cess Amount". If the original
            // invoice already balances without it, it is NOT a monetary Cess.
            // Preserve genuine monetary Cess only when it is needed to balance.
            var cess = Math.Abs(invoiceValue - amountWithoutCess) <= 0.05m
                ? 0m
                : apparentCess;

            var customerGstin = NormalizeGstin(Cell("GSTINUIN"));
            var pos = Cell("PLACEOFSUPPLYNAMEOFSTATE", "PLACEOFSUPPLY");
            if (pos.All(char.IsDigit)) pos = string.Empty;

            var type = Cell("TRANSACTIONTYPE");
            if (type.Length == 0) type = "Sale";
            if (!type.Contains("sale", StringComparison.OrdinalIgnoreCase) &&
                !type.Contains("credit", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"GSTR1 Report invoice {invoice} uses unsupported transaction type '{type}'.");

            values.Add(new InvoiceLine(
                invoice, date, Cell("PARTYNAME"), customerGstin, type,
                invoiceValue, Amount(Cell("RATE")), taxable,
                igst, cgst, sgst, cess, pos));
            continue;

            string Cell(params string[] names)
            {
                foreach (var name in names)
                    if (headers.Columns.TryGetValue(name, out var column))
                        return report.GetCell(row, column).Trim();
                return string.Empty;
            }
        }

        // Merge repeated rate lines of the SAME invoice. The portal repeats the
        // invoice gross value on each line; summing it would double-post sales.
        // Mixed GST rates are deliberately rejected rather than silently
        // creating two full-value vouchers for one invoice.
        var output = new List<InvoiceLine>();
        foreach (var invoiceGroup in values.GroupBy(line => (
            Number: line.Number.ToUpperInvariant(), line.Date,
            Party: line.Party.ToUpperInvariant(), line.CustomerGstin)))
        {
            if (invoiceGroup.Select(line => line.Rate).Distinct().Count() > 1)
                throw new InvalidDataException(
                    $"GSTR1 Report invoice {invoiceGroup.Key.Number} has multiple GST rates. Review the original invoice before importing; no rows were posted.");

            if (invoiceGroup.Select(line => line.Value).Distinct().Count() > 1)
                throw new InvalidDataException(
                    $"GSTR1 Report invoice {invoiceGroup.Key.Number} has inconsistent gross values on repeated lines.");

            if (invoiceGroup.Select(line => line.PlaceOfSupply).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidDataException(
                    $"GSTR1 Report invoice {invoiceGroup.Key.Number} has conflicting places of supply.");

            var first = invoiceGroup.First();
            var taxable = invoiceGroup.Sum(line => line.Taxable);
            var igst = invoiceGroup.Sum(line => line.Igst);
            var cgst = invoiceGroup.Sum(line => line.Cgst);
            var sgst = invoiceGroup.Sum(line => line.Sgst);
            var cess = invoiceGroup.Sum(line => line.Cess);
            var roundedDifference = Math.Round(first.Value - taxable - igst - cgst - sgst - cess, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(roundedDifference) > 1m)
                throw new InvalidDataException(
                    $"GSTR1 Report invoice {first.Number} does not balance: gross {first.Value}, tax-inclusive components {taxable + igst + cgst + sgst + cess}. Review the tax columns.");

            output.Add(first with
            {
                Taxable = taxable, Igst = igst, Cgst = cgst, Sgst = sgst,
                Cess = cess, RoundOff = roundedDifference
            });
        }

        var rows = new Dictionary<int, IReadOnlyDictionary<int, string>>
        {
            [1] = OutputHeaders.Select((value, index) => (index, value))
                .ToDictionary(item => item.index + 1, item => item.value)
        };

        var rowIndex = 2;
        foreach (var record in output.OrderBy(item => item.Date).ThenBy(item => item.Number, StringComparer.OrdinalIgnoreCase))
        {
            var rowValues = new[]
            {
                record.Number, record.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                record.Party, record.CustomerGstin, record.DocumentType,
                Numeric(record.Value), Numeric(record.Rate), Numeric(record.Taxable),
                Numeric(record.Igst), Numeric(record.Cgst), Numeric(record.Sgst),
                Numeric(record.Cess), record.PlaceOfSupply, Numeric(record.RoundOff),
                $"GSTR1 Report {record.Date:MMM yyyy}"
            };
            rows[rowIndex++] = rowValues.Select((value, index) => (index, value))
                .ToDictionary(item => item.index + 1, item => item.value);
        }

        normalized = new OpenXmlWorkbookData
        {
            FilePath = source.FilePath,
            Uses1904DateSystem = source.Uses1904DateSystem,
            Worksheets = new Dictionary<string, OpenXmlWorksheetData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sales"] = new OpenXmlWorksheetData
                {
                    Name = "Sales",
                    State = report.State,
                    Rows = rows,
                    MaxRow = rowIndex - 1
                }
            }
        };
        return true;
    }

    private static string Numeric(decimal number) =>
        number.ToString("0.######", CultureInfo.InvariantCulture);

    private static (int Row, Dictionary<string, int> Columns)? FindHeader(OpenXmlWorksheetData sheet)
    {
        for (var row = 1; row <= Math.Min(30, sheet.MaxRow); row++)
        {
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var col = 1; col <= 50; col++)
            {
                var name = Normalize(sheet.GetCell(row, col));
                if (name.Length > 0 && !columns.ContainsKey(name)) columns[name] = col;
            }
            if (columns.ContainsKey("INVOICENO") &&
                columns.ContainsKey("INVOICEDATE") &&
                columns.ContainsKey("INVOICEVALUE") &&
                columns.ContainsKey("TAXABLEVALUE") &&
                columns.ContainsKey("RATE") &&
                columns.ContainsKey("PARTYNAME"))
                return (row, columns);
        }
        return null;
    }

    private static string FindReportGstin(OpenXmlWorksheetData sheet)
    {
        for (var row = 1; row <= Math.Min(sheet.MaxRow, 15); row++)
        {
            if (Normalize(sheet.GetCell(row, 1)) == "1GSTIN")
                return NormalizeGstin(sheet.GetCell(row, 2));
        }
        return string.Empty;
    }

    private static string Normalize(string value) =>
        string.Concat((value ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit));

    private static string NormalizeGstin(string value)
    {
        var result = Normalize(value);
        return result.Length == 15 ? result : string.Empty;
    }

    private static decimal Amount(string text)
    {
        var value = (text ?? string.Empty).Trim().Replace(",", "").Replace("₹", "").TrimEnd('%');
        if (value.Length == 0) return 0m;
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;
        throw new InvalidDataException($"Invalid GSTR1 Report tax/amount value '{text}'.");
    }

    private static bool TryDate(string text, bool uses1904, out DateTime date)
    {
        date = default;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 0)
        {
            try
            {
                date = (uses1904 ? new DateTime(1904, 1, 1).AddDays(serial) : DateTime.FromOADate(serial)).Date;
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }
        string[] formats =
        [
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
            "yyyy-MM-dd", "dd.MM.yyyy", "d-MMM-yyyy", "dd-MMM-yyyy"
        ];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date))
        {
            date = date.Date;
            return true;
        }
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool IsInFinancialYear(DateTime date, string financialYear)
    {
        var match = System.Text.RegularExpressions.Regex.Match(financialYear.Trim(), @"^(\d{4})\s*[-/]\s*(\d{2}|\d{4})$");
        if (!match.Success)
            throw new InvalidDataException($"Invalid selected AAH financial year '{financialYear}'.");

        var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var providedEnd = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var expectedEnd = start + 1;
        if (providedEnd != expectedEnd && providedEnd != expectedEnd % 100)
            throw new InvalidDataException($"AAH financial year '{financialYear}' has inconsistent start and end years.");
        return date >= new DateTime(start, 4, 1) && date < new DateTime(expectedEnd, 4, 1);
    }

    private sealed record InvoiceLine(
        string Number, DateTime Date, string Party, string CustomerGstin, string DocumentType,
        decimal Value, decimal Rate, decimal Taxable, decimal Igst, decimal Cgst,
        decimal Sgst, decimal Cess, string PlaceOfSupply)
    {
        public decimal RoundOff { get; init; }
    }
}
