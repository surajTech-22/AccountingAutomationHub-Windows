using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Infrastructure.Purchases;

internal sealed class PurchaseSourceParser
{
    public static readonly string[] SupportedSections =
    [
        "B2B",
        "B2B-RC",
        "B2B Amendment",
        "Dr notes-B2B"
    ];

    public PurchaseSourceKind DetectSourceKind(
        OpenXmlWorkbookData workbook)
    {
        foreach (var section in SupportedSections)
        {
            if (!workbook.TryGetWorksheet(
                    section,
                    out var worksheet))
            {
                continue;
            }

            for (var column = 1;
                 column <= 20;
                 column++)
            {
                var value = worksheet.GetCell(
                    1,
                    column);

                if (value.Contains(
                        "OriginForm$2A",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return PurchaseSourceKind.TwoA;
                }

                if (value.Contains(
                        "OriginForm$2B",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return PurchaseSourceKind.TwoB;
                }
            }
        }

        return Path
            .GetFileNameWithoutExtension(
                workbook.FilePath)
            .Contains(
                "2B",
                StringComparison.OrdinalIgnoreCase)
            ? PurchaseSourceKind.TwoB
            : PurchaseSourceKind.TwoA;
    }

    public string DetectCompanyName(
        OpenXmlWorkbookData workbook)
    {
        if (workbook.TryGetWorksheet(
                "Index",
                out var indexSheet))
        {
            var title = indexSheet
                .GetCell(
                    4,
                    1)
                .Trim();

            if (title.Length > 0)
            {
                var wgstPosition = title.IndexOf(
                    ".wgst",
                    StringComparison.OrdinalIgnoreCase);

                if (wgstPosition > 0)
                {
                    title = title[..wgstPosition];
                }

                var underscorePosition = title.LastIndexOf(
                    '_');

                if (underscorePosition > 0 &&
                    title[(underscorePosition + 1)..]
                        .Contains('-'))
                {
                    title = title[..underscorePosition];
                }

                title = title
                    .Replace(
                        "_",
                        " ",
                        StringComparison.Ordinal)
                    .Trim();

                if (title.Length > 0)
                {
                    return title;
                }
            }
        }

        var fileName = Path.GetFileNameWithoutExtension(
            workbook.FilePath);

        var gstrPosition = fileName.IndexOf(
            "_GSTR",
            StringComparison.OrdinalIgnoreCase);

        if (gstrPosition > 0)
        {
            fileName = fileName[..gstrPosition];
        }

        return fileName
            .Replace(
                "_",
                " ",
                StringComparison.Ordinal)
            .Trim();
    }

    public string DetectClientGstin(
        OpenXmlWorkbookData workbook)
    {
        if (!workbook.TryGetWorksheet(
                "INTER",
                out var interSheet))
        {
            return string.Empty;
        }

        return PurchaseImportUtilities.NormalizeGstin(
            interSheet.GetCell(
                1,
                9));
    }

    public string DetectFinancialYear(
        OpenXmlWorkbookData workbook)
    {
        if (workbook.TryGetWorksheet(
                "Index",
                out var indexSheet))
        {
            var value = indexSheet
                .GetCell(
                    1,
                    2)
                .Trim();

            if (value.Length > 0)
            {
                return value;
            }
        }

        if (workbook.TryGetWorksheet(
                "INTER",
                out var interSheet))
        {
            return interSheet
                .GetCell(
                    1,
                    4)
                .Trim();
        }

        return string.Empty;
    }

    public List<PurchaseSourceRow> ParseWorkbookRows(
        OpenXmlWorkbookData workbook,
        PurchaseSourceKind sourceKind,
        ICollection<PurchaseImportIssue> issues,
        CancellationToken cancellationToken)
    {
        using var headerMappingScope = ImportHeaderMappingRuntime.BeginSource(workbook.FilePath);
        var rows = new List<PurchaseSourceRow>();

        foreach (var section in SupportedSections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!workbook.TryGetWorksheet(
                    section,
                    out var worksheet))
            {
                continue;
            }

            rows.AddRange(
                ParseWorksheetRows(
                    workbook,
                    worksheet,
                    section,
                    sourceKind,
                    issues,
                    cancellationToken));
        }

        return rows;
    }

    private static IEnumerable<PurchaseSourceRow> ParseWorksheetRows(
        OpenXmlWorkbookData workbook,
        OpenXmlWorksheetData worksheet,
        string section,
        PurchaseSourceKind sourceKind,
        ICollection<PurchaseImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var headerMap = BuildHeaderMap(
            worksheet);

        var firstDataRow = FindFirstDataRow(
            worksheet);

        if (firstDataRow <= 0)
        {
            yield break;
        }

        for (var rowNumber = firstDataRow;
             rowNumber <= worksheet.MaxRow;
             rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var supplierName = GetField(
                worksheet,
                rowNumber,
                headerMap,
                "SUPPLIERNAME");

            var gstin = PurchaseImportUtilities.NormalizeGstin(
                GetField(
                    worksheet,
                    rowNumber,
                    headerMap,
                    "GSTIN"));

            var isNote = string.Equals(
                section,
                "Dr notes-B2B",
                StringComparison.OrdinalIgnoreCase);

            var documentNumber = GetField(
                worksheet,
                rowNumber,
                headerMap,
                isNote
                    ? "CRDRNOTENO"
                    : "INVOICENO");

            if (gstin.Length == 0 &&
                documentNumber.Length == 0 &&
                supplierName.Length == 0)
            {
                continue;
            }

            if (documentNumber.Length == 0)
            {
                issues.Add(
                    NewIssue(
                        workbook,
                        worksheet,
                        rowNumber,
                        "Document number is missing."));
                continue;
            }

            if (!PurchaseImportUtilities.IsValidGstin(
                    gstin))
            {
                issues.Add(
                    NewIssue(
                        workbook,
                        worksheet,
                        rowNumber,
                        $"Supplier GSTIN '{gstin}' is invalid."));
                continue;
            }

            var dateText = GetField(
                worksheet,
                rowNumber,
                headerMap,
                isNote
                    ? "CRDRNOTEDATE"
                    : "INVOICEDATE");

            var documentDate =
                PurchaseImportUtilities.ParseExcelDate(
                    dateText,
                    workbook.Uses1904DateSystem);

            if (documentDate is null)
            {
                issues.Add(
                    NewIssue(
                        workbook,
                        worksheet,
                        rowNumber,
                        $"Document date '{dateText}' is invalid."));
                continue;
            }

            var filingDate = PurchaseImportUtilities.ParseExcelDate(
                GetField(worksheet, rowNumber, headerMap, "FILINGDATE", "DATEOFFILING", "GSTR1FILINGDATE"),
                workbook.Uses1904DateSystem);
            var returnPeriod = GetField(
                worksheet, rowNumber, headerMap, "GSTR2APERIOD", "GSTR2BPERIOD", "RETURNPERIOD", "RETURNTAXPERIOD");

            var taxableValue =
                PurchaseImportUtilities.ParseDecimal(
                    GetField(
                        worksheet,
                        rowNumber,
                        headerMap,
                        "TAXVLAUE",
                        "TAXVALUE"));

            var igst =
                PurchaseImportUtilities.ParseDecimal(
                    GetField(
                        worksheet,
                        rowNumber,
                        headerMap,
                        "IGST"));

            var cgst =
                PurchaseImportUtilities.ParseDecimal(
                    GetField(
                        worksheet,
                        rowNumber,
                        headerMap,
                        "CENTRALTAX"));

            var sgst =
                PurchaseImportUtilities.ParseDecimal(
                    GetField(
                        worksheet,
                        rowNumber,
                        headerMap,
                        "STATETAX"));

            var rateText = GetField(
                worksheet,
                rowNumber,
                headerMap,
                "TAXRATE");
            var rateKnown = PurchaseImportUtilities.TryParseDecimal(rateText, out var rate);

            // GSTR-2B frequently omits Rate %. Reverse-calculate only when a standard
            // rate and the IGST/CGST/SGST shape can be proved from the imported values.
            // A supplied but contradictory rate is also treated as unknown; never show
            // a false 0% merely because the source cell/API defaulted to zero.
            if (sourceKind == PurchaseSourceKind.TwoB)
            {
                if (rateKnown && (Math.Abs(igst) + Math.Abs(cgst) + Math.Abs(sgst)) > 0.005m)
                {
                    if (!PurchaseImportUtilities.TryDeriveSupportedRate(
                            taxableValue, igst, cgst, sgst, out var verifiedRate))
                    {
                        rate = 0m;
                        rateKnown = false;
                    }
                    else if (rate == 0m)
                    {
                        // Fix13: GST portal/export files sometimes carry a literal 0 in
                        // Rate even though taxable value + tax amounts prove a standard
                        // GST rate. Treat only that literal zero as a source placeholder.
                        // A genuine 0% line has no IGST/CGST/SGST and never enters here.
                        rate = verifiedRate;
                        rateKnown = true;
                    }
                    else if (Math.Abs(verifiedRate - rate) > 0.001m)
                    {
                        rate = 0m;
                        rateKnown = false;
                    }
                }
                else if (!rateKnown &&
                         PurchaseImportUtilities.TryDeriveSupportedRate(
                             taxableValue, igst, cgst, sgst, out var derivedRate))
                {
                    rate = derivedRate;
                    rateKnown = true;
                }
            }

            // Fix13: apply the same literal-zero fallback to spreadsheet sources that
            // identify as 2A as well. A genuine 0% row has no GST; non-zero GST plus
            // taxable value is the proof required before replacing a source 0.
            if (rateKnown && rate == 0m &&
                (Math.Abs(igst) + Math.Abs(cgst) + Math.Abs(sgst)) > 0.005m &&
                PurchaseImportUtilities.TryDeriveSupportedRate(
                    taxableValue, igst, cgst, sgst, out var zeroFallbackRate))
            {
                rate = zeroFallbackRate;
                rateKnown = true;
            }

            var documentType = isNote
                ? GetField(
                    worksheet,
                    rowNumber,
                    headerMap,
                    "DOCTYPE")
                : section;

            var reverseCharge = GetField(
                worksheet,
                rowNumber,
                headerMap,
                "REVERSECHARGE");

            if (string.Equals(
                    section,
                    "B2B-RC",
                    StringComparison.OrdinalIgnoreCase) &&
                reverseCharge.Length == 0)
            {
                reverseCharge = "Yes";
            }

            yield return new PurchaseSourceRow
            {
                SourceKind = sourceKind,
                SourceFilePath = workbook.FilePath,
                SheetName = worksheet.Name,
                RowNumber = rowNumber,
                Section = section,
                SupplierName = supplierName.Trim(),
                SupplierGstin = gstin,
                DocumentType = documentType.Trim(),
                DocumentNumber = documentNumber.Trim(),
                DocumentDate = documentDate.Value,
                FilingDate = filingDate,
                ReturnPeriod = returnPeriod.Trim(),
                DocumentValue =
                    PurchaseImportUtilities.ParseDecimal(
                        GetField(
                            worksheet,
                            rowNumber,
                            headerMap,
                            isNote
                                ? "CRDRNOTEVALUE"
                                : "INVOICEVALUE")),
                Rate = rate,
                RateKnown = rateKnown,
                TaxableValue = taxableValue,
                Igst = igst,
                Cgst = cgst,
                Sgst = sgst,
                Cess =
                    PurchaseImportUtilities.ParseDecimal(
                        GetField(
                            worksheet,
                            rowNumber,
                            headerMap,
                            "CESS")),
                PlaceOfSupply = GetField(
                    worksheet,
                    rowNumber,
                    headerMap,
                    "SUPPLYSTATE",
                    "PLACESUPPLY",
                    "PLACESUPPLYTYPE"),
                ReverseCharge = reverseCharge.Trim(),
                InvoiceType = GetField(
                    worksheet,
                    rowNumber,
                    headerMap,
                    "SUPPLYTYPE"),
                ItcAvailability = GetField(
                    worksheet,
                    rowNumber,
                    headerMap,
                    "ITCAVAILABILITY"),
                OriginalDocumentNumber = GetField(
                    worksheet,
                    rowNumber,
                    headerMap,
                    isNote
                        ? "ORIGINALCRDRNOTENO"
                        : "ORIGINALINVOICENO"),
                OriginalDocumentDate =
                    PurchaseImportUtilities.ParseExcelDate(
                        GetField(
                            worksheet,
                            rowNumber,
                            headerMap,
                            isNote
                                ? "ORIGINALCRDRNOTEDATE"
                                : "ORIGINALINVOICEDATE"),
                        workbook.Uses1904DateSystem),
                Remarks = GetField(
                    worksheet,
                    rowNumber,
                    headerMap,
                    "REMARKS"),
                ItemName = GetField(worksheet, rowNumber, headerMap, "ITEMNAME", "STOCKITEM"),
                HsnCode = GetField(worksheet, rowNumber, headerMap, "HSNCODE", "HSN"),
                ActualQuantity = PurchaseImportUtilities.ParseDecimal(GetField(worksheet, rowNumber, headerMap, "ACTUALQTY", "QUANTITY", "QTY")),
                Unit = GetField(worksheet, rowNumber, headerMap, "UNIT", "UOM", "UNITS"),
                ItemRate = PurchaseImportUtilities.ParseDecimal(GetField(worksheet, rowNumber, headerMap, "ITEMRATE", "UNITRATE")),
                BilledQuantity = PurchaseImportUtilities.ParseDecimal(GetField(worksheet, rowNumber, headerMap, "BILLEDQTY", "BILLEDQUANTITY")),
                ItemAmount = PurchaseImportUtilities.ParseDecimal(GetField(worksheet, rowNumber, headerMap, "ITEMAMOUNT")),
                ItemPurchaseLedger = GetField(worksheet, rowNumber, headerMap, "ITEMPURCHASELEDGER"),
                Godown = GetField(worksheet, rowNumber, headerMap, "GODOWN", "LOCATION"),
                BatchName = GetField(worksheet, rowNumber, headerMap, "BATCHNAME", "BATCH"),
                DiscountPercent = PurchaseImportUtilities.ParseDecimal(GetField(worksheet, rowNumber, headerMap, "DISCOUNTPERCENT", "DISCOUNT")),
                DiscountAmount = PurchaseImportUtilities.ParseDecimal(GetField(worksheet, rowNumber, headerMap, "DISCOUNTAMOUNT")),
                OrderNumber = GetField(worksheet, rowNumber, headerMap, "ORDERNUMBER", "ORDERNO"),
                TrackingNumber = GetField(worksheet, rowNumber, headerMap, "TRACKINGNUMBER", "TRACKINGNO"),
                ManufacturingDate = PurchaseImportUtilities.ParseExcelDate(GetField(worksheet, rowNumber, headerMap, "MANUFACTURINGDATE", "MFGDATE"), workbook.Uses1904DateSystem),
                ExpiryDate = PurchaseImportUtilities.ParseExcelDate(GetField(worksheet, rowNumber, headerMap, "EXPIRYDATE"), workbook.Uses1904DateSystem),
                ItemDescription = GetField(worksheet, rowNumber, headerMap, "ITEMDESCRIPTION", "DESCRIPTION"),
                CostCentre = GetField(worksheet, rowNumber, headerMap, "COSTCENTRE", "COSTCENTER"),
                FreeQuantity = PurchaseImportUtilities.ParseDecimal(GetField(worksheet, rowNumber, headerMap, "FREEQTY", "FREEQUANTITY"))
            };
        }
    }

    private static Dictionary<string, int> BuildHeaderMap(
        OpenXmlWorksheetData worksheet)
    {
        var result = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);

        for (var column = 1;
             column <= 40;
             column++)
        {
            var rawHeader = worksheet.GetCell(2, column);
            var learned = ImportHeaderMappingRuntime.Resolve(ImportHeaderMappingCatalog.Purchase, rawHeader);
            var effectiveHeader = learned.Length > 0 ? PurchaseParserHeader(learned) : rawHeader;
            var header = PurchaseImportUtilities.NormalizeHeader(effectiveHeader);

            if (header.Length > 0 && !result.ContainsKey(header)) result[header] = column;
        }

        return result;
    }


    private static string PurchaseParserHeader(string canonical) => canonical switch
    {
        "GSTIN" => "GSTIN",
        "SupplierName" => "SUPPLIERNAME",
        "InvoiceNo" => "INVOICENO",
        "InvoiceDate" => "INVOICEDATE",
        "InvoiceValue" => "INVOICEVALUE",
        "TaxableValue" => "TAXVALUE",
        "Rate" => "TAXRATE",
        "IGST" => "IGST",
        "CGST" => "CENTRALTAX",
        "SGST" => "STATETAX",
        "Cess" => "CESS",
        "PlaceOfSupply" => "SUPPLYSTATE",
        "ReverseCharge" => "REVERSECHARGE",
        "ITCAvailability" => "ITCAVAILABILITY",
        "Reason" => "REMARKS",
        "NoteNo" => "CRDRNOTENO",
        "NoteDate" => "CRDRNOTEDATE",
        "NoteValue" => "CRDRNOTEVALUE",
        "OriginalInvoiceNo" => "ORIGINALINVOICENO",
        "OriginalInvoiceDate" => "ORIGINALINVOICEDATE",
        "OriginalNoteNo" => "ORIGINALCRDRNOTENO",
        "OriginalNoteDate" => "ORIGINALCRDRNOTEDATE",
        "ReturnPeriod" => "RETURNPERIOD",
        "FilingDate" => "FILINGDATE",
        "SupplyType" => "SUPPLYTYPE",
        "DocumentType" => "DOCTYPE",
        "ItemName" => "ITEMNAME",
        "HsnCode" => "HSNCODE",
        "ActualQuantity" => "ACTUALQTY",
        "Unit" => "UNIT",
        "ItemRate" => "ITEMRATE",
        "BilledQuantity" => "BILLEDQTY",
        "ItemAmount" => "ITEMAMOUNT",
        "ItemPurchaseLedger" => "ITEMPURCHASELEDGER",
        "Godown" => "GODOWN",
        "BatchName" => "BATCHNAME",
        "DiscountPercent" => "DISCOUNTPERCENT",
        "DiscountAmount" => "DISCOUNTAMOUNT",
        "OrderNumber" => "ORDERNUMBER",
        "TrackingNumber" => "TRACKINGNUMBER",
        "ManufacturingDate" => "MANUFACTURINGDATE",
        "ExpiryDate" => "EXPIRYDATE",
        "ItemDescription" => "ITEMDESCRIPTION",
        "CostCentre" => "COSTCENTRE",
        "FreeQuantity" => "FREEQTY",
        _ => canonical
    };

    private static int FindFirstDataRow(
        OpenXmlWorksheetData worksheet)
    {
        for (var row = 1;
             row <= Math.Min(
                 worksheet.MaxRow,
                 20);
             row++)
        {
            if (worksheet
                .GetCell(
                    row,
                    1)
                .Trim() == "-")
            {
                return row + 1;
            }
        }

        return 0;
    }

    private static string GetField(
        OpenXmlWorksheetData worksheet,
        int rowNumber,
        IReadOnlyDictionary<string, int> headerMap,
        params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            var normalized =
                PurchaseImportUtilities.NormalizeHeader(
                    alias);

            if (headerMap.TryGetValue(
                    normalized,
                    out var column))
            {
                return worksheet
                    .GetCell(
                        rowNumber,
                        column)
                    .Trim();
            }
        }

        return string.Empty;
    }

    private static PurchaseImportIssue NewIssue(
        OpenXmlWorkbookData workbook,
        OpenXmlWorksheetData worksheet,
        int rowNumber,
        string message) =>
        new()
        {
            SourceFile = Path.GetFileName(
                workbook.FilePath),
            SheetName = worksheet.Name,
            RowNumber = rowNumber,
            Message = message
        };
}
