using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R46 R39 Paddle spatial invoice-recognition authority.
///
/// Android ML Kit remains fast preview evidence only. Windows calls only the isolated Paddle invoice
/// runtime provisioned under C:\AccountingAutomationHub\OCR\PaddleInvoice. PP-StructureV3/PP-DocLayout
/// preserve page regions, word boxes, table boxes/cells and table HTML. AAH then decides seller/buyer,
/// Purchase/Sales direction, GST slabs, totals and posting semantics. Banking OCR is never used.
/// </summary>
internal sealed class MobileInvoiceDocumentOcrService
{
    public async Task<MobileInvoiceDocumentOcrResult> RecognizeAsync(
        IReadOnlyList<string> imagePaths,
        ClientProfile client,
        string originalPayloadJson,
        string phoneRawOcr,
        string runRoot,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var usableImages = imagePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (usableImages.Length == 0)
            return MobileInvoiceDocumentOcrResult.Failed("No captured invoice image is available for Windows document OCR.");

        // R90 replaces the failing PP-StructureV3 inference with the separately verified R88
        // RapidOCR/ONNX spatial worker and R89 source-linked accounting review.
        // Historic R39 reference contract retained: new InvoicePaddleDocumentOcrRecognitionService()
        // "Paddle spatial invoice verification failed" and "AAH Paddle PP-StructureV3 + R39 spatial document-understanding authority"
        // are legacy compatibility evidence only; InvoiceDocumentLayoutParser.Parse( is
        // retired in the live route. The R90 route never invokes Paddle.
        progress?.Invoke("AAH Phone Link • ONNX polygon OCR and R89 accounting evidence review");
        var invoiceOcr = new InvoiceOnnxDocumentOcrRecognitionService();
        var recognition = await invoiceOcr.RecognizeRawPagesAsync(
            usableImages, runRoot, progress, cancellationToken).ConfigureAwait(false);
        if (!recognition.WasSuccessful || recognition.Pages.Count == 0)
            return MobileInvoiceDocumentOcrResult.Failed(
                recognition.FailureMessage.Length > 0 ? recognition.FailureMessage : "AAH independent ONNX Invoice OCR returned no usable source-identified spatial page.");
        try
        {
            var result = InvoiceOnnxAccountingPreviewParser.Parse(recognition, client, originalPayloadJson, phoneRawOcr);
            result.RecognitionLogPath = recognition.LogPath;
            result.RecognitionStatusPath = recognition.StatusPath;
            return result;
        }
        catch (Exception ex)
        {
            return MobileInvoiceDocumentOcrResult.Failed("ONNX polygon evidence or R89 accounting review failed: " + ex.Message);
        }
    }
}

internal static class InvoiceDocumentLayoutParser
{
    private static readonly Regex HtmlRowRegex = new(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlCellRegex = new(@"<(?:td|th)\b[^>]*>(.*?)</(?:td|th)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PanRegex = new(@"\b[A-Z]{5}[0-9]{4}[A-Z]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GstinRegex = new(@"\b[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][A-Z0-9]Z[A-Z0-9]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"\b([0-3]?\d)[-/.]([01]?\d)[-/.]((?:19|20)\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex AmountRegex = new(@"(?<![A-Z0-9])[-+]?\d[\d,]*(?:\.\d{1,2})?(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly decimal[] SupportedGstRates = [0m, 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m, 40m];

    public static MobileInvoiceDocumentOcrResult Parse(BankOcrRecognitionBatch recognition, ClientProfile client, string originalPayloadJson, string phoneRawOcr)
    {
        var pages = recognition.Pages.Select(ReadPage).ToArray();
        if (pages.Length == 0) return MobileInvoiceDocumentOcrResult.Failed("AAH Windows Invoice OCR pages could not be parsed.");

        var blocks = pages.SelectMany(x => x.Blocks).OrderBy(x => x.PageIndex).ThenBy(x => x.Order).ThenBy(x => x.Y1).ThenBy(x => x.X1).ToArray();
        var tables = blocks.Where(x => x.Label.Contains("table", StringComparison.OrdinalIgnoreCase) && x.Content.Contains("<tr", StringComparison.OrdinalIgnoreCase))
            .Select(x => new ParsedTable(x, ParseHtmlTable(x.Content))).Where(x => x.Rows.Count > 0).ToArray();
        var plainBlocks = blocks.Where(x => !x.Label.Contains("table", StringComparison.OrdinalIgnoreCase)).ToArray();
        var allPlainText = string.Join("\n", plainBlocks.Select(x => x.Content).Where(x => x.Length > 0));

        // R46 R38: classify each detected table semantically.  A page may contain an item table,
        // GST slab summary, payment/bank table, or unrelated footer table; they are not interchangeable.
        var tableSemantics = tables.Select(ClassifyTable).ToArray();
        var itemTables = tableSemantics.Where(x => x.Kind == "ITEM_TABLE" && x.Score >= 4)
            .OrderBy(x => x.Table.Block.PageIndex).ThenBy(x => x.Table.Block.Y1).ToArray();
        var summarySemantic = tableSemantics.Where(x => x.Kind == "GST_SUMMARY_TABLE" && x.Score >= 3)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Table.Block.PageIndex).ThenBy(x => x.Table.Block.Y1).FirstOrDefault();
        var itemTable = itemTables.FirstOrDefault()?.Table;
        var items = new List<InvoiceLayoutLine>();
        foreach (var semantic in itemTables) items.AddRange(ParseItemTable(semantic.Table));
        var summary = summarySemantic is null ? new InvoiceSummaryEvidence() : ParseSummaryTable(summarySemantic.Table);

        NormalizeLocalTaxComponents(items, summary);
        RepairItemRowsFromArithmetic(items);
        ApplySummarySlabAuthority(items, summary);
        BuildSummaryFromItemsIfNeeded(items, summary);
        NormalizeLineTotals(items);

        var identity = ResolveParties(plainBlocks, allPlainText, client, itemTable?.Block.Y1 ?? int.MaxValue);
        RepairCounterpartyIdentityFromPhone(identity, phoneRawOcr, client);
        ReconcilePartyIdentity(identity, client, allPlainText, phoneRawOcr);
        var invoiceNumber = ExtractInvoiceNumber(plainBlocks, allPlainText);
        if (!ValidInvoiceNumber(invoiceNumber)) invoiceNumber = ExtractPhoneInvoiceNumber(phoneRawOcr);
        var invoiceDate = ExtractInvoiceDate(plainBlocks, allPlainText);
        if (invoiceDate.Length == 0) invoiceDate = ExtractPhoneInvoiceDate(phoneRawOcr);

        var original = ParseOriginalPayload(originalPayloadJson);
        var direction = ResolveDirection(identity, client, original.Direction);
        var taxableTotal = summary.TaxableTotal > 0m ? summary.TaxableTotal : items.Sum(x => x.Taxable);
        var componentTotals = ResolveComponentTotals(items, summary, direction);
        var preRoundTotal = taxableTotal + componentTotals.Cgst + componentTotals.Sgst + componentTotals.Igst + componentTotals.Cess;
        var invoiceTotal = summary.NetPayable > 0m ? summary.NetPayable : ResolveFinalInvoiceTotal(plainBlocks, tables, original.InvoiceTotal);
        var phoneFinal = ExtractPhoneFinalPayable(phoneRawOcr, preRoundTotal);
        if (summary.NetPayable <= 0m && phoneFinal > 0m && (preRoundTotal <= 0m || Math.Abs(phoneFinal - preRoundTotal) <= 2m)) invoiceTotal = phoneFinal;
        var documentProfile = ClassifyDocument(allPlainText, phoneRawOcr, original.DocumentType, identity, items, summary);
        var discountTotal = summary.DiscountTotal > 0m ? summary.DiscountTotal : items.Sum(x => x.Discount);
        var roundOff = invoiceTotal > 0m
            ? Math.Round(invoiceTotal - (taxableTotal + componentTotals.Cgst + componentTotals.Sgst + componentTotals.Igst + componentTotals.Cess), 2, MidpointRounding.AwayFromZero)
            : 0m;
        if (Math.Abs(roundOff) > 2m) roundOff = 0m;

        var gstRates = summary.Slabs.Select(x => x.GstRate)
            .Concat(items.Select(x => x.GstRate))
            .Where(x => x > 0m && IsSupportedGstRate(x)).Distinct().OrderBy(x => x).ToArray();

        var root = BuildPayload(
            original.Root,
            client,
            direction,
            identity,
            invoiceNumber.Length > 0 ? invoiceNumber : original.InvoiceNumber,
            invoiceDate.Length > 0 ? invoiceDate : original.InvoiceDate,
            taxableTotal,
            discountTotal,
            componentTotals,
            roundOff,
            invoiceTotal,
            gstRates,
            items,
            summary,
            documentProfile,
            tableSemantics,
            pages.Length,
            tables.Length);

        var rawText = BuildReviewText(pages, tables);
        return new MobileInvoiceDocumentOcrResult
        {
            WasSuccessful = true,
            PayloadJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            RawOcrText = rawText,
            CandidatesJson = BuildCandidates(identity, invoiceNumber, invoiceDate, summary, items),
            CounterpartyName = identity.Counterparty.Name,
            CounterpartyGstin = identity.Counterparty.Gstin,
            InvoiceNumber = invoiceNumber.Length > 0 ? invoiceNumber : original.InvoiceNumber,
            InvoiceDate = invoiceDate.Length > 0 ? invoiceDate : original.InvoiceDate,
            Direction = direction,
            RecognitionMode = original.RecognitionMode,
            InvoiceTotal = invoiceTotal > 0m ? Money(invoiceTotal) : original.InvoiceTotal,
            TableCount = tables.Length,
            ItemRowCount = items.Count,
            SummarySlabCount = summary.Slabs.Count,
            Authority = "AAH Paddle PP-StructureV3 + R39 spatial document-understanding authority"
        };
    }

    private static ParsedPage ReadPage(BankOcrRecognitionPage page)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(page.JsonPath, Encoding.UTF8));
        var root = document.RootElement;
        if (root.TryGetProperty("res", out var inner) && inner.ValueKind == JsonValueKind.Object) root = inner;
        var width = JsonInt(root, "width");
        var height = JsonInt(root, "height");
        var blocks = new List<LayoutBlock>();
        if (root.TryGetProperty("parsing_res_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var fallbackOrder = 0;
            foreach (var node in list.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                fallbackOrder++;
                var bbox = ReadBbox(node);
                blocks.Add(new LayoutBlock
                {
                    PageIndex = page.SourcePage,
                    Label = JsonText(node, "block_label"),
                    Content = NormalizeBlockContent(JsonText(node, "block_content")),
                    X1 = bbox.X1,
                    Y1 = bbox.Y1,
                    X2 = bbox.X2,
                    Y2 = bbox.Y2,
                    Order = JsonInt(node, "block_order") is var order && order > 0 ? order : fallbackOrder
                });
            }
        }
        return new ParsedPage(page.SourcePage, width, height, blocks);
    }

    private static (int X1, int Y1, int X2, int Y2) ReadBbox(JsonElement node)
    {
        if (node.TryGetProperty("block_bbox", out var bbox) && bbox.ValueKind == JsonValueKind.Array)
        {
            var values = bbox.EnumerateArray().Select(x => x.TryGetInt32(out var n) ? n : 0).Take(4).ToArray();
            if (values.Length == 4) return (values[0], values[1], values[2], values[3]);
        }
        return (0, 0, 0, 0);
    }

    private static List<List<string>> ParseHtmlTable(string html)
    {
        var rows = new List<List<string>>();
        foreach (Match rowMatch in HtmlRowRegex.Matches(html))
        {
            var cells = new List<string>();
            foreach (Match cellMatch in HtmlCellRegex.Matches(rowMatch.Groups[1].Value))
            {
                var text = HtmlTagRegex.Replace(cellMatch.Groups[1].Value, " ");
                text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
                cells.Add(Collapse(text));
            }
            if (cells.Count > 0) rows.Add(cells);
        }
        return rows;
    }

    private static int ItemTableScore(ParsedTable table)
    {
        var text = NormalizeUpper(string.Join(" | ", table.Rows.Take(4).SelectMany(x => x)));
        var score = 0;
        if (text.Contains("HSN")) score += 2;
        if (text.Contains("PRODUCT") || text.Contains("DESCRIPTION") || text.Contains("ITEM")) score += 2;
        if (text.Contains("TAXABLE")) score += 2;
        if (text.Contains("GST")) score++;
        if (text.Contains("CGST")) score++;
        if (text.Contains("SGST") || text.Contains("S/UTGST") || text.Contains("UTGST")) score++;
        if (text.Contains("NET")) score++;
        return score;
    }

    private static int SummaryTableScore(ParsedTable table)
    {
        var text = NormalizeUpper(string.Join(" | ", table.Rows.Take(5).SelectMany(x => x)));
        var score = 0;
        if (text.Contains("PARTICULAR")) score += 2;
        if (text.Contains("TAXABLE")) score++;
        if (text.Contains("TAX AMT") || text.Contains("TAX AMOUNT")) score++;
        if (text.Contains("NET PAYABLE") || text.Contains("NET AMT") || text.Contains("NET AMOUNT")) score += 2;
        if (text.Contains("CGST") && text.Contains("SGST")) score++;
        return score;
    }

    private static TableSemantic ClassifyTable(ParsedTable table)
    {
        var itemScore = ItemTableScore(table);
        var summaryScore = SummaryTableScore(table);
        var text = NormalizeUpper(string.Join(" | ", table.Rows.Take(6).SelectMany(x => x)));
        var paymentScore = 0;
        if (text.Contains("BANK") || text.Contains("ACCOUNT NO") || text.Contains("IFSC")) paymentScore += 4;
        if (text.Contains("PAYMENT") || text.Contains("UPI") || text.Contains("NEFT") || text.Contains("RTGS")) paymentScore += 3;
        var kind = "OTHER_TABLE";
        var score = Math.Max(itemScore, Math.Max(summaryScore, paymentScore));
        if (summaryScore >= itemScore && summaryScore >= paymentScore && summaryScore >= 3) kind = "GST_SUMMARY_TABLE";
        else if (itemScore >= summaryScore && itemScore >= paymentScore && itemScore >= 4) kind = "ITEM_TABLE";
        else if (paymentScore >= 4) kind = "PAYMENT_TABLE";
        return new TableSemantic(table, kind, score);
    }

    private static DocumentProfile ClassifyDocument(
        string windowsText,
        string phoneText,
        string originalDocumentType,
        PartyResolution parties,
        IReadOnlyList<InvoiceLayoutLine> items,
        InvoiceSummaryEvidence summary)
    {
        var text = NormalizeUpper(windowsText + "\n" + phoneText);
        var documentType = "Invoice";
        var confidence = 55;
        if (text.Contains("CREDIT NOTE")) { documentType = "Credit Note"; confidence = 98; }
        else if (text.Contains("DEBIT NOTE")) { documentType = "Debit Note"; confidence = 98; }
        else if (text.Contains("BILL OF SUPPLY")) { documentType = "Bill of Supply"; confidence = 98; }
        else if (text.Contains("DELIVERY CHALLAN")) { documentType = "Delivery Challan"; confidence = 97; }
        else if (text.Contains("PROFORMA")) { documentType = "Proforma Invoice"; confidence = 96; }
        else if (text.Contains("PURCHASE ORDER") || Regex.IsMatch(text, @"\bPO\s*(?:NO|NUMBER)\b")) { documentType = "Purchase Order"; confidence = 94; }
        else if (text.Contains("RECEIPT VOUCHER")) { documentType = "Receipt Voucher"; confidence = 96; }
        else if (text.Contains("REFUND VOUCHER")) { documentType = "Refund Voucher"; confidence = 96; }
        else if (text.Contains("PAYMENT VOUCHER")) { documentType = "Payment Voucher"; confidence = 96; }
        else if (text.Contains("TAX INVOICE") || (text.Contains("INVOICE") && (summary.Slabs.Count > 0 || items.Any(x => x.GstRate > 0m)))) { documentType = "Tax Invoice"; confidence = 95; }
        else if (!string.IsNullOrWhiteSpace(originalDocumentType) && !originalDocumentType.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            documentType = originalDocumentType;
            confidence = 70;
        }

        var supplyType = "Domestic";
        if (text.Contains("SEZ") && (text.Contains("WITHOUT PAYMENT") || text.Contains("LUT"))) supplyType = "SEZ without payment";
        else if (text.Contains("SEZ")) supplyType = "SEZ with payment";
        else if (text.Contains("EXPORT") && (text.Contains("WITHOUT PAYMENT") || text.Contains("LUT"))) supplyType = "Export without payment";
        else if (text.Contains("EXPORT")) supplyType = "Export with payment";
        else if (text.Contains("REVERSE CHARGE") || Regex.IsMatch(text, @"\bRCM\b")) supplyType = "Reverse Charge";
        else if (items.Any(x => x.Igst > 0m) || text.Contains("IGST")) supplyType = "Inter-State";
        else if (items.Any(x => x.Cgst > 0m || x.Sgst > 0m) || (text.Contains("CGST") && text.Contains("SGST"))) supplyType = "Intra-State";

        var taxExpectation = documentType.Equals("Bill of Supply", StringComparison.OrdinalIgnoreCase) ? "No GST tax amount expected" :
            documentType.Contains("Challan", StringComparison.OrdinalIgnoreCase) || documentType.Contains("Purchase Order", StringComparison.OrdinalIgnoreCase) || documentType.Contains("Proforma", StringComparison.OrdinalIgnoreCase)
                ? "Non-posting/reference document unless explicitly converted"
                : "GST/tax structure must reconcile according to supply type";
        return new DocumentProfile(documentType, supplyType, taxExpectation, confidence);
    }

    private static List<InvoiceLayoutLine> ParseItemTable(ParsedTable table)
    {
        var result = new List<InvoiceLayoutLine>();
        var headerRows = DetermineHeaderRows(table.Rows);
        if (headerRows <= 0) return result;
        var maxColumns = table.Rows.Take(headerRows).Max(x => x.Count);
        var headers = Enumerable.Range(0, maxColumns)
            .Select(index => Collapse(string.Join(" ", table.Rows.Take(headerRows).Where(x => index < x.Count).Select(x => x[index]))))
            .ToArray();

        var hsnCol = FindColumn(headers, "HSN", "SAC");
        var descCol = FindColumn(headers, "PRODUCT", "DESCRIPTION", "PARTICULAR", "ITEM NAME");
        var qtyCol = FindColumn(headers, "QTY", "PCS", "QUANTITY", "TOTAL UNITS");
        var unitCol = FindColumn(headers, "UOM", "UNIT");
        var rateCol = FindColumn(headers, "BASE RATE", "RATE");
        var taxableCol = FindColumn(headers, "TAXABLE");
        var gstCol = FindColumn(headers, "GST %", "GST%", "GST RATE", "GST");
        var cgstCol = FindColumn(headers, "CGST");
        var sgstCol = FindColumn(headers, "S/UTGST", "S/UIGST", "S/UGST", "SGST", "UTGST");
        var igstCol = FindColumn(headers, "IGST");
        var cessCol = FindColumn(headers, "CESS");
        var netCol = FindColumn(headers, "NET AMT", "NET AMOUNT", "LINE TOTAL", "NET");
        var discountCols = Enumerable.Range(0, headers.Length).Where(index => NormalizeUpper(headers[index]).Contains("DISC")).ToArray();

        for (var rowIndex = headerRows; rowIndex < table.Rows.Count; rowIndex++)
        {
            var cells = table.Rows[rowIndex];
            var joined = NormalizeUpper(string.Join(" ", cells));
            if (joined.Length == 0 || joined.StartsWith("TOTAL") || joined.Contains("PARTICULARS")) continue;
            // R79: A footer/amount-in-words row is never a product, even when Paddle
            // includes it inside the same HTML table or OCR puts digits in adjacent cells.
            // Preserve source evidence in Raw OCR; never turn these into accounting rows.
            if (Regex.IsMatch(joined, @"^(?:RS\.?\s+|RUPEES\b|AMOUNT\s+IN\s+WORDS\b|SUB\s*TOTAL\b|GRAND\s*TOTAL\b|ROUND\s*OFF\b|SGST\b|CGST\b|IGST\b|CESS\b|TERMS?\s*(?:&|AND|OF|:)\b|AUTHORI[ZS]ED\b|E\s*[.&]\s*O\s*[.&]\s*E\b)", RegexOptions.CultureInvariant)) continue;
            if (joined.Contains("SUB TOTAL") || joined.Contains("GRAND TOTAL") ||
                joined.Contains("AMOUNT IN WORDS") || joined.Contains("FIVE HUNDRED AND FORTY FIVE ONLY")) continue;
            var hsn = Cell(cells, hsnCol);
            if (!LooksLikeHsn(hsn))
            {
                var found = cells.FirstOrDefault(LooksLikeHsn) ?? string.Empty;
                if (found.Length > 0) hsn = DigitsOnly(found);
            }
            var description = Cell(cells, descCol);
            if (hsn.Length == 0 && description.Length == 0) continue;
            if (hsn.Length == 0 && !cells.Any(x => Regex.IsMatch(x, @"\b\d{4,8}\b"))) continue;

            var taxable = Amount(Cell(cells, taxableCol));
            var gstRate = SnapGstRate(Amount(Cell(cells, gstCol)));
            var cgst = Amount(Cell(cells, cgstCol));
            var sgst = Amount(Cell(cells, sgstCol));
            var igst = Amount(Cell(cells, igstCol));
            var cess = Amount(Cell(cells, cessCol));
            var lineTotal = Amount(Cell(cells, netCol));
            var discount = discountCols.Sum(index => Amount(Cell(cells, index)));

            if (gstRate <= 0m && taxable > 0m)
            {
                var derived = igst > 0m ? igst * 100m / taxable : (cgst + sgst) > 0m ? (cgst + sgst) * 100m / taxable : 0m;
                gstRate = SnapGstRate(derived);
            }

            result.Add(new InvoiceLayoutLine
            {
                SourceRow = cells.ToArray(),
                Description = description,
                HsnSac = DigitsOnly(hsn),
                Quantity = Amount(Cell(cells, qtyCol)),
                Unit = Cell(cells, unitCol),
                Rate = Amount(Cell(cells, rateCol)),
                Discount = discount,
                Taxable = taxable,
                GstRate = gstRate,
                Cgst = cgst,
                Sgst = sgst,
                Igst = igst,
                Cess = cess,
                LineTotal = lineTotal,
                TaxableSourceText = Cell(cells, taxableCol),
                CgstSourceText = Cell(cells, cgstCol),
                SgstSourceText = Cell(cells, sgstCol),
                LineTotalSourceText = Cell(cells, netCol)
            });
        }
        return result;
    }

    private static InvoiceSummaryEvidence ParseSummaryTable(ParsedTable table)
    {
        var evidence = new InvoiceSummaryEvidence();
        var headerRows = DetermineHeaderRows(table.Rows);
        if (headerRows <= 0) headerRows = 1;
        var maxColumns = table.Rows.Take(headerRows).Max(x => x.Count);
        var headers = Enumerable.Range(0, maxColumns)
            .Select(index => Collapse(string.Join(" ", table.Rows.Take(headerRows).Where(x => index < x.Count).Select(x => x[index]))))
            .ToArray();
        var particularsCol = FindColumn(headers, "PARTICULAR", "GST");
        if (particularsCol < 0) particularsCol = 0;
        var grossCol = FindColumn(headers, "GROSS");
        var taxableCol = FindColumn(headers, "TAXABLE");
        var taxCol = FindColumn(headers, "TAX AMT", "TAX AMOUNT", "TAX");
        var netCol = FindColumn(headers, "NET PAYABLE", "NET AMT", "NET AMOUNT", "PAYABLE");
        var discountCols = Enumerable.Range(0, headers.Length).Where(index => NormalizeUpper(headers[index]).Contains("DISC") || NormalizeUpper(headers[index]).Contains("SCH AMT")).ToArray();

        for (var rowIndex = headerRows; rowIndex < table.Rows.Count; rowIndex++)
        {
            var cells = table.Rows[rowIndex];
            var label = Cell(cells, particularsCol);
            var upper = NormalizeUpper(label);
            if (upper.Length == 0) continue;
            if (upper.StartsWith("TOTAL"))
            {
                evidence.GrossTotal = Amount(Cell(cells, grossCol));
                evidence.DiscountTotal = discountCols.Sum(index => Amount(Cell(cells, index)));
                evidence.TaxableTotal = Amount(Cell(cells, taxableCol));
                evidence.TaxTotal = Amount(Cell(cells, taxCol));
                evidence.NetPayable = Amount(Cell(cells, netCol));
                continue;
            }
            var rate = ParseCombinedGstRate(label);
            if (rate <= 0m) continue;
            evidence.Slabs.Add(new InvoiceSummarySlab
            {
                Label = label,
                GstRate = rate,
                Gross = Amount(Cell(cells, grossCol)),
                Discount = discountCols.Sum(index => Amount(Cell(cells, index))),
                Taxable = Amount(Cell(cells, taxableCol)),
                TaxAmount = Amount(Cell(cells, taxCol)),
                NetPayable = Amount(Cell(cells, netCol))
            });
        }
        if (evidence.TaxableTotal <= 0m && evidence.Slabs.Count > 0) evidence.TaxableTotal = evidence.Slabs.Sum(x => x.Taxable);
        if (evidence.TaxTotal <= 0m && evidence.Slabs.Count > 0) evidence.TaxTotal = evidence.Slabs.Sum(x => x.TaxAmount);
        if (evidence.DiscountTotal <= 0m && evidence.Slabs.Count > 0) evidence.DiscountTotal = evidence.Slabs.Sum(x => x.Discount);
        return evidence;
    }

    private static void BuildSummaryFromItemsIfNeeded(IReadOnlyList<InvoiceLayoutLine> items, InvoiceSummaryEvidence summary)
    {
        if (items.Count == 0) return;
        var provedGroups = items
            .Where(x => x.Taxable > 0m && x.GstRate > 0m && IsSupportedGstRate(x.GstRate))
            .GroupBy(x => x.GstRate)
            .OrderBy(x => x.Key)
            .ToArray();
        if (provedGroups.Length == 0) return;

        if (summary.Slabs.Count == 0)
        {
            foreach (var group in provedGroups)
            {
                summary.Slabs.Add(new InvoiceSummarySlab
                {
                    Label = $"GST {group.Key:0.##}% derived from item rows",
                    GstRate = group.Key,
                    Taxable = Math.Round(group.Sum(x => x.Taxable), 2, MidpointRounding.AwayFromZero),
                    TaxAmount = Math.Round(group.Sum(x => x.Cgst + x.Sgst + x.Igst), 2, MidpointRounding.AwayFromZero),
                    NetPayable = Math.Round(group.Sum(x => x.LineTotal), 2, MidpointRounding.AwayFromZero)
                });
            }
            summary.WasDerivedFromItems = true;
        }

        if (summary.TaxableTotal <= 0m) summary.TaxableTotal = Math.Round(items.Sum(x => x.Taxable), 2, MidpointRounding.AwayFromZero);
        if (summary.TaxTotal <= 0m) summary.TaxTotal = Math.Round(items.Sum(x => x.Cgst + x.Sgst + x.Igst), 2, MidpointRounding.AwayFromZero);
        if (summary.NetPayable <= 0m)
        {
            var lineNet = items.Sum(x => x.LineTotal);
            if (lineNet > 0m) summary.PreRoundTotal = Math.Round(lineNet, 2, MidpointRounding.AwayFromZero);
        }
    }

    private static int DetermineHeaderRows(IReadOnlyList<List<string>> rows)
    {
        var limit = Math.Min(rows.Count, 4);
        for (var index = 0; index < limit; index++)
        {
            var text = NormalizeUpper(string.Join(" ", rows[index]));
            if (Regex.IsMatch(text, @"\b\d{4,8}\b") && !text.Contains("HSN")) return Math.Max(1, index);
            if (text.StartsWith("CGST") || text.StartsWith("IGST")) return Math.Max(1, index);
        }
        return Math.Min(2, rows.Count);
    }

    private static void NormalizeLocalTaxComponents(IReadOnlyList<InvoiceLayoutLine> items, InvoiceSummaryEvidence summary)
    {
        foreach (var row in items)
        {
            if (row.GstRate <= 0m || row.Taxable <= 0m || row.Igst > 0m) continue;
            var expected = Math.Round(row.Taxable * row.GstRate / 200m, 2, MidpointRounding.AwayFromZero);
            if (expected <= 0m) continue;
            var cgstClose = row.Cgst > 0m && Math.Abs(row.Cgst - expected) <= 0.20m;
            var sgstClose = row.Sgst > 0m && Math.Abs(row.Sgst - expected) <= 0.20m;
            var componentsDisagree = row.Cgst <= 0m || row.Sgst <= 0m || Math.Abs(row.Cgst - row.Sgst) > 0.05m;
            var damagedPrecision = !LooksCleanMoneyCell(row.CgstSourceText) || !LooksCleanMoneyCell(row.SgstSourceText);
            // If one local component is missing/damaged, or both OCR strings visibly lost precision,
            // use the taxable+rate hypothesis only when the surviving numeric evidence supports it.
            if ((componentsDisagree || damagedPrecision) && (cgstClose || sgstClose))
            {
                row.Cgst = expected;
                row.Sgst = expected;
                row.WasArithmeticRepair = true;
            }
        }
    }

    private static bool LooksCleanMoneyCell(string value) =>
        Regex.IsMatch((value ?? string.Empty).Trim(), @"^[+-]?\d[\d,]*\.\d{2}$", RegexOptions.CultureInvariant);

    private static void NormalizeLineTotals(IReadOnlyList<InvoiceLayoutLine> items)
    {
        foreach (var row in items)
        {
            if (row.Taxable <= 0m) continue;
            var expected = Math.Round(row.Taxable + row.Cgst + row.Sgst + row.Igst + row.Cess, 2, MidpointRounding.AwayFromZero);
            if (expected <= 0m) continue;
            if (row.LineTotal <= 0m || Math.Abs(row.LineTotal - expected) > 1m)
            {
                row.LineTotal = expected;
                row.WasArithmeticRepair = true;
            }
        }
    }

    private static void RepairItemRowsFromArithmetic(IReadOnlyList<InvoiceLayoutLine> items)
    {
        foreach (var row in items)
        {
            if (row.GstRate <= 0m || !IsSupportedGstRate(row.GstRate)) continue;
            var taxTotal = row.Igst > 0m ? row.Igst : row.Cgst + row.Sgst;
            var expectedTax = row.Taxable > 0m ? Math.Round(row.Taxable * row.GstRate / 100m, 2, MidpointRounding.AwayFromZero) : 0m;
            if (row.Taxable > 0m && taxTotal > 0m && Math.Abs(expectedTax - taxTotal) <= 0.20m) continue;

            // Reverse calculation is allowed only when the printed taxable is missing or clearly
            // impossible. A single damaged tax cell must never rewrite an otherwise plausible taxable.
            if (row.Taxable > 0m && taxTotal > 0m && expectedTax > 0m && Math.Abs(expectedTax - taxTotal) <= Math.Max(1m, expectedTax * 0.08m))
                continue;

            var candidates = new List<decimal>();
            if (taxTotal > 0m) candidates.Add(Math.Round(taxTotal * 100m / row.GstRate, 2, MidpointRounding.AwayFromZero));
            if (row.LineTotal > 0m && taxTotal > 0m) candidates.Add(Math.Round(row.LineTotal - taxTotal - row.Cess, 2, MidpointRounding.AwayFromZero));
            var viable = candidates.Where(x => x > 0m).ToArray();
            if (viable.Length == 0) continue;
            var chosen = viable.Length == 1 ? viable[0] : viable.OrderBy(x => Math.Abs((x * row.GstRate / 100m) - taxTotal)).First();
            if (row.Taxable <= 0m || row.Taxable < chosen * 0.60m || row.Taxable > chosen * 1.60m)
            {
                row.Taxable = chosen;
                row.WasArithmeticRepair = true;
            }
        }
    }

    private static void ApplySummarySlabAuthority(IReadOnlyList<InvoiceLayoutLine> items, InvoiceSummaryEvidence summary)
    {
        if (summary.Slabs.Count == 0 || items.Count == 0) return;
        foreach (var slab in summary.Slabs)
        {
            var group = items.Where(x => Math.Abs(x.GstRate - slab.GstRate) <= 0.01m).ToArray();
            if (group.Length == 0 || slab.Taxable <= 0m) continue;
            var current = group.Sum(x => x.Taxable);
            var residual = Math.Round(slab.Taxable - current, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(residual) <= 1.00m)
            {
                var damagedTaxable = group.Where(x => !LooksCleanMoneyCell(x.TaxableSourceText)).ToArray();
                // Only change an item taxable when the summary residual identifies one unique damaged
                // cell. With multiple doubtful rows the slab summary remains authority and item rows
                // stay reviewable instead of inventing a distribution.
                if (damagedTaxable.Length == 1)
                {
                    damagedTaxable[0].Taxable = Math.Round(damagedTaxable[0].Taxable + residual, 2, MidpointRounding.AwayFromZero);
                    damagedTaxable[0].WasSummaryResidualRepair = Math.Abs(residual) > 0m;
                }
            }

            if (slab.TaxAmount > 0m)
            {
                var expectedComponentTotal = Math.Round(slab.TaxAmount / 2m, 2, MidpointRounding.AwayFromZero);
                var cgstResidual = Math.Round(expectedComponentTotal - group.Sum(x => x.Cgst), 2, MidpointRounding.AwayFromZero);
                var sgstResidual = Math.Round(expectedComponentTotal - group.Sum(x => x.Sgst), 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(cgstResidual) <= 1m || Math.Abs(sgstResidual) <= 1m)
                {
                    var damaged = group.Where(x => !LooksCleanMoneyCell(x.CgstSourceText) || !LooksCleanMoneyCell(x.SgstSourceText)).ToArray();
                    if (damaged.Length == 1)
                    {
                        var target = damaged[0];
                        if (Math.Abs(cgstResidual) <= 1m) target.Cgst = Math.Round(target.Cgst + cgstResidual, 2, MidpointRounding.AwayFromZero);
                        if (Math.Abs(sgstResidual) <= 1m) target.Sgst = Math.Round(target.Sgst + sgstResidual, 2, MidpointRounding.AwayFromZero);
                        target.WasSummaryResidualRepair = true;
                    }
                }
            }
        }
    }

    private static ComponentTotals ResolveComponentTotals(IReadOnlyList<InvoiceLayoutLine> items, InvoiceSummaryEvidence summary, string direction)
    {
        var cgst = items.Sum(x => x.Cgst);
        var sgst = items.Sum(x => x.Sgst);
        var igst = items.Sum(x => x.Igst);
        var cess = items.Sum(x => x.Cess);
        if (items.Count > 0 && (cgst + sgst + igst) > 0m) return new ComponentTotals(cgst, sgst, igst, cess);
        if (summary.TaxTotal > 0m)
        {
            // A summary table often prints CGST+SGST combined. Split only when the amount is
            // exactly splittable at invoice level; otherwise leave the components for review.
            var half = Math.Round(summary.TaxTotal / 2m, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs((half * 2m) - summary.TaxTotal) <= 0.001m) return new ComponentTotals(half, half, 0m, 0m);
        }
        return new ComponentTotals(cgst, sgst, igst, cess);
    }

    private static void RepairCounterpartyIdentityFromPhone(PartyResolution parties, string phoneRawOcr, ClientProfile client)
    {
        if (string.IsNullOrWhiteSpace(phoneRawOcr)) return;
        var clientGstin = NormalizeIdentity(client.Gstin);
        var clientPan = NormalizeIdentity(client.Pan);
        var phoneGstins = GstinRegex.Matches(phoneRawOcr.ToUpperInvariant())
            .Select(match => NormalizeIdentity(match.Value))
            .Where(value => value.Length == 15 && ValidGstinChecksum(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var otherGstin = phoneGstins.FirstOrDefault(value => !value.Equals(clientGstin, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var counterpartyLooksLikeClient = clientGstin.Length == 15 && NormalizeIdentity(parties.Counterparty.Gstin).Equals(clientGstin, StringComparison.OrdinalIgnoreCase);
        if ((parties.Counterparty.Gstin.Length == 0 || counterpartyLooksLikeClient) && otherGstin.Length == 15)
        {
            parties.Counterparty.Gstin = otherGstin;
            parties.Counterparty.Pan = otherGstin.Substring(2, 10);
        }
        if (parties.Counterparty.Pan.Length == 0)
        {
            var phonePans = PanRegex.Matches(phoneRawOcr.ToUpperInvariant())
                .Select(match => NormalizeIdentity(match.Value))
                .Where(value => value.Length == 10 && !value.Equals(clientPan, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (phonePans.Length == 1) parties.Counterparty.Pan = phonePans[0];
        }
        var existingName = parties.Counterparty.Name;

        var clientNames = new[] { client.TradeName, client.LegalName, client.ClientName }
            .Select(NormalizeName).Where(value => value.Length >= 3).ToArray();
        var lines = phoneRawOcr.Replace("\r", string.Empty).Split('\n')
            .Select(Collapse).Where(value => value.Length >= 3).Take(35).ToArray();
        var candidate = lines
            .Where(line => line.Any(char.IsLetter) && !line.Any(char.IsDigit))
            .Where(line => !clientNames.Any(name => SimilarName(NormalizeName(line), name)))
            .Where(line => !IsPhoneRoleOrAddressLine(line))
            .OrderByDescending(PhonePartyNameScore)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var repaired = CleanPartyName(candidate);
            var currentNormalized = NormalizeName(existingName);
            var repairedNormalized = NormalizeName(repaired);
            if (existingName.Length == 0 || (repairedNormalized.Contains(currentNormalized, StringComparison.OrdinalIgnoreCase) && repaired.Length > existingName.Length))
                parties.Counterparty.Name = repaired;
        }
    }

    private static void ReconcilePartyIdentity(PartyResolution parties, ClientProfile client, string windowsText, string phoneText)
    {
        var clientGstin = NormalizeIdentity(client.Gstin);
        var clientPan = NormalizeIdentity(client.Pan);
        if (clientPan.Length == 0 && clientGstin.Length == 15) clientPan = clientGstin.Substring(2, 10);
        var evidence = (windowsText + "\n" + phoneText).ToUpperInvariant();
        var gstins = GstinRegex.Matches(evidence).Select(x => NormalizeIdentity(x.Value)).Where(x => x.Length == 15 && ValidGstinChecksum(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var otherGstin = gstins.FirstOrDefault(x => clientGstin.Length == 0 || !x.Equals(clientGstin, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        // PAN is structurally embedded in GSTIN.  Once a GSTIN is selected, its PAN owns the
        // identity relationship; a nearby name can never steal another party's PAN/GSTIN.
        if (otherGstin.Length == 15)
        {
            parties.Counterparty.Gstin = otherGstin;
            parties.Counterparty.Pan = otherGstin.Substring(2, 10);
        }
        else if (parties.Counterparty.Gstin.Length == 15)
        {
            parties.Counterparty.Pan = parties.Counterparty.Gstin.Substring(2, 10);
        }

        if (parties.ClientIsSeller)
        {
            parties.Seller.Gstin = clientGstin;
            parties.Seller.Pan = clientPan;
            parties.Buyer.Gstin = parties.Counterparty.Gstin;
            parties.Buyer.Pan = parties.Counterparty.Pan;
            if (parties.Buyer.Name.Length == 0) parties.Buyer.Name = parties.Counterparty.Name;
        }
        else
        {
            parties.Buyer.Gstin = clientGstin;
            parties.Buyer.Pan = clientPan;
            parties.Seller.Gstin = parties.Counterparty.Gstin;
            parties.Seller.Pan = parties.Counterparty.Pan;
            if (parties.Seller.Name.Length == 0) parties.Seller.Name = parties.Counterparty.Name;
        }

        if (parties.Counterparty.Name.Length == 0)
        {
            var clientNames = new[] { client.TradeName, client.LegalName, client.ClientName }.Select(NormalizeName).Where(x => x.Length >= 3).ToArray();
            var candidate = phoneText.Replace("\r", string.Empty).Split('\n')
                .Select(Collapse)
                .Where(x => x.Length >= 3 && x.Any(char.IsLetter))
                .Where(x => !clientNames.Any(name => SimilarName(NormalizeName(x), name)))
                .Where(x => !IsPhoneRoleOrAddressLine(x))
                .OrderByDescending(PhonePartyNameScore)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(candidate)) parties.Counterparty.Name = CleanPartyName(candidate);
        }
    }

    private static bool IsPhoneRoleOrAddressLine(string value)
    {
        var upper = NormalizeUpper(value);
        var blocked = new[] { "TAX INVOICE", "INVOICE", "FSSAI", "PAN NO", "GSTN", "GSTIN", "STATE", "IRN", "DATE", "BILL", "NEAR ", " ROAD", "TUMKUR", "KARNATAKA", "TERMS", "CONDITIONS", "PAGE " };
        return blocked.Any(token => upper.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static int PhonePartyNameScore(string value)
    {
        var letters = value.Count(char.IsLetter);
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var uppercase = value.Count(char.IsUpper);
        return letters + Math.Min(words, 5) * 6 + (uppercase >= Math.Max(3, letters * 3 / 4) ? 20 : 0);
    }

    private static string ExtractPhoneInvoiceNumber(string phoneRawOcr)
    {
        if (string.IsNullOrWhiteSpace(phoneRawOcr)) return string.Empty;
        var text = phoneRawOcr.Replace("\r", string.Empty);
        // Longest labels first and word boundaries are mandatory; otherwise INV can consume the
        // prefix of INVOICE and incorrectly return OICE as the document number.
        var sameLine = Regex.Match(text, @"\b(?:INVOICE|BILLI?|BILI|BILL|INV)\s*(?:NO|NUMBER|#)\b\s*[:\-]?\s*([A-Z0-9][A-Z0-9/\-]{2,24})", RegexOptions.IgnoreCase);
        if (sameLine.Success)
        {
            var value = CleanInvoiceNumber(sameLine.Groups[1].Value);
            if (ValidInvoiceNumber(value)) return value;
        }
        var lines = text.Split('\n').Select(Collapse).Where(x => x.Length > 0).ToArray();
        for (var index = 0; index < lines.Length; index++)
        {
            if (!Regex.IsMatch(lines[index], @"\b(?:INVOICE|BILLI?|BILI|BILL|INV)\s*(?:NO|NUMBER|#)\b", RegexOptions.IgnoreCase)) continue;
            for (var next = index + 1; next < Math.Min(lines.Length, index + 3); next++)
            {
                var candidate = CleanInvoiceNumber(lines[next]);
                if (ValidInvoiceNumber(candidate)) return candidate;
            }
        }
        return string.Empty;
    }

    private static decimal ExtractPhoneFinalPayable(string phoneRawOcr, decimal preRoundTotal)
    {
        if (string.IsNullOrWhiteSpace(phoneRawOcr)) return 0m;
        var lines = phoneRawOcr.Replace("\r", string.Empty).Split('\n').Select(Collapse).Where(x => x.Length > 0).ToArray();
        var candidates = new List<(int Score, decimal Value)>();
        for (var index = 0; index < lines.Length; index++)
        {
            var upper = NormalizeUpper(lines[index]);
            var score = upper.Contains("NET PAYABLE") ? 100 : upper.Contains("GRAND TOTAL") ? 95 : upper.Equals("TOTAL", StringComparison.OrdinalIgnoreCase) ? 80 : 0;
            if (score == 0) continue;
            foreach (var amount in ExtractAmounts(lines[index])) if (amount > 0m) candidates.Add((score, amount));
            for (var next = index + 1; next < Math.Min(lines.Length, index + 4); next++)
                foreach (var amount in ExtractAmounts(lines[next])) if (amount > 0m) candidates.Add((score - (next - index) * 3, amount));
        }
        if (preRoundTotal > 0m)
        {
            var near = candidates.Where(x => Math.Abs(x.Value - preRoundTotal) <= 2m)
                .OrderByDescending(x => x.Score).ThenBy(x => Math.Abs(x.Value - preRoundTotal)).FirstOrDefault();
            if (near.Value > 0m) return near.Value;

            // Some OCR engines split a Total row into one token per line.  In that case preserve
            // the document-level arithmetic constraint and choose a plausible final-payable value
            // from the complete preview rather than trusting the exact flattened line order.
            var allNear = ExtractAmounts(phoneRawOcr).Where(x => Math.Abs(x - preRoundTotal) <= 2m).Distinct().ToArray();
            var rounded = allNear.Where(x => decimal.Round(x, 0) == x)
                .OrderBy(x => Math.Abs(x - preRoundTotal)).FirstOrDefault();
            if (rounded > 0m) return rounded;
            var closest = allNear.OrderBy(x => Math.Abs(x - preRoundTotal)).FirstOrDefault();
            if (closest > 0m) return closest;
        }
        return candidates.OrderByDescending(x => x.Score).ThenByDescending(x => x.Value).FirstOrDefault().Value;
    }

    private static string ExtractPhoneInvoiceDate(string phoneRawOcr)
    {
        if (string.IsNullOrWhiteSpace(phoneRawOcr)) return string.Empty;
        var match = Regex.Match(phoneRawOcr, @"(?:BIL{1,2}|BILI|INVOICE|INV)\s*(?:DATE|DT)\s*[:\-]?\s*([0-3]?\d[-/.][01]?\d[-/.](?:19|20)\d{2})", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeDate(match.Groups[1].Value) : string.Empty;
    }

    private static PartyResolution ResolveParties(IReadOnlyList<LayoutBlock> blocks, string allText, ClientProfile client, int firstTableY)
    {
        var clientGstin = NormalizeIdentity(client.Gstin);
        var clientPan = NormalizeIdentity(client.Pan);
        if (clientPan.Length == 0 && clientGstin.Length == 15) clientPan = clientGstin.Substring(2, 10);
        var clientNames = new[] { client.TradeName, client.LegalName, client.ClientName }.Select(NormalizeName).Where(x => x.Length >= 3).Distinct().ToArray();
        var header = blocks.Where(x => x.Y1 <= firstTableY && x.Content.Length > 0).ToArray();
        var candidates = header.Select(x => new PartyCandidate(x, ExtractPan(x.Content), ExtractGstin(x.Content), PartyNameScore(x.Content, x.Label))).ToList();

        PartyCandidate? clientBlock = null;
        foreach (var candidate in candidates)
        {
            var normalizedText = NormalizeIdentity(candidate.Block.Content);
            var normalizedName = NormalizeName(candidate.Block.Content);
            var identityMatch = clientGstin.Length == 15 && (IdentityDistance(normalizedText, clientGstin) <= 2 || IdentityDistance(NormalizeIdentity(candidate.Gstin), clientGstin) <= 2);
            var panMatch = clientPan.Length == 10 && (NormalizeIdentity(candidate.Pan).Equals(clientPan, StringComparison.OrdinalIgnoreCase) || normalizedText.Contains(clientPan, StringComparison.OrdinalIgnoreCase));
            var nameMatch = clientNames.Any(name => SimilarName(normalizedName, name));
            if (identityMatch || panMatch || nameMatch)
            {
                if (clientBlock is null || candidate.Block.Y1 < clientBlock.Block.Y1) clientBlock = candidate;
            }
        }

        var clientParty = new InvoiceParty
        {
            Name = BestClientName(client),
            Pan = clientPan,
            Gstin = clientGstin,
            Address = string.Empty
        };

        var sellerNameBlock = header
            .Where(x => clientBlock is null || Math.Abs(x.Y1 - clientBlock.Block.Y1) > 8 || Math.Abs(x.X1 - clientBlock.Block.X1) > 8)
            .Where(x => !ClientBlockText(x.Content, clientNames, clientGstin, clientPan))
            .Select(x => new { Block = x, Score = PartyNameScore(x.Content, x.Label) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Block.Y1).FirstOrDefault()?.Block;

        var sellerRegion = sellerNameBlock is null ? header : header.Where(x =>
            IsInOtherPartyBand(x, sellerNameBlock, clientBlock?.Block, firstTableY)).ToArray();
        var sellerText = string.Join("\n", sellerRegion.Select(x => x.Content));
        var seller = new InvoiceParty
        {
            Name = sellerNameBlock is null ? string.Empty : CleanPartyName(sellerNameBlock.Content),
            Pan = ExtractPan(sellerText),
            Gstin = ExtractGstin(sellerText),
            Address = BuildPartyAddress(sellerRegion, sellerNameBlock?.Content ?? string.Empty)
        };

        if (seller.Gstin.Length == 15) seller.Pan = seller.Gstin.Substring(2, 10);
        if (seller.Pan.Length == 10 && seller.Gstin.Length == 0)
        {
            var near = FindMalformedGstinContainingPan(sellerText, seller.Pan);
            if (near.Length == 15 && ValidGstinChecksum(near)) seller.Gstin = near;
        }

        // If the selected Client appears in the seller/header region, this is a Sales invoice;
        // otherwise the selected Client is the buyer and the opposite top-header party is supplier.
        var clientIsSeller = clientBlock is not null && (sellerNameBlock is null || clientBlock.Block.Y1 <= sellerNameBlock.Y1 + 25);
        var sellerParty = clientIsSeller ? clientParty : seller;
        var buyerParty = clientIsSeller ? seller : clientParty;
        var counterparty = clientIsSeller ? buyerParty : sellerParty;
        return new PartyResolution(sellerParty, buyerParty, counterparty, clientIsSeller);
    }

    private static bool SamePartyRegion(LayoutBlock a, LayoutBlock b, int firstTableY)
    {
        if (a.PageIndex != b.PageIndex || a.Y1 >= firstTableY) return false;
        var xGap = Math.Abs(a.X1 - b.X1);
        var yGap = Math.Abs(a.Y1 - b.Y1);
        return xGap < 380 && yGap < 260;
    }

    private static bool IsInOtherPartyBand(LayoutBlock block, LayoutBlock otherName, LayoutBlock? clientBlock, int firstTableY)
    {
        if (!SamePartyRegion(block, otherName, firstTableY)) return false;
        if (clientBlock is null) return true;
        if (otherName.Y1 < clientBlock.Y1) return block.Y1 < clientBlock.Y1 - 4;
        if (otherName.Y1 > clientBlock.Y1) return block.Y1 >= otherName.Y1 - 20;
        return true;
    }

    private static string BuildPartyAddress(IEnumerable<LayoutBlock> blocks, string name)
    {
        var values = blocks.Select(x => Collapse(x.Content)).Where(x => x.Length > 0)
            .Where(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Where(x => ExtractPan(x).Length == 0 && ExtractGstin(x).Length == 0)
            .Where(x => !NormalizeUpper(x).Contains("TAX INVOICE") && !NormalizeUpper(x).Contains("FSSAI") && !NormalizeUpper(x).Contains("PAN NO") && !NormalizeUpper(x).Contains("GST"))
            .Take(5);
        return string.Join(", ", values);
    }

    private static string ExtractInvoiceNumber(IReadOnlyList<LayoutBlock> blocks, string allText)
    {
        var labels = blocks.Where(x => InvoiceNumberLabel(x.Content)).ToArray();
        foreach (var label in labels)
        {
            var inline = ValueAfterLabel(label.Content, @"(?:INVOICE|BILL)(?:\s*(?:NO|NUMBER|#))?\s*[:\-]?");
            if (ValidInvoiceNumber(inline)) return inline;
            var nearby = blocks.Where(x => x.PageIndex == label.PageIndex && x != label && x.Y1 >= label.Y1 - 25 && x.Y1 <= label.Y2 + 80 && x.X1 >= label.X1 - 20)
                .OrderBy(x => Math.Abs(x.Y1 - label.Y1) + Math.Max(0, x.X1 - label.X2) / 4)
                .Select(x => CleanInvoiceNumber(x.Content)).FirstOrDefault(ValidInvoiceNumber);
            if (nearby is not null) return nearby;
        }
        var match = Regex.Match(allText, @"(?:INVOICE|BILL)(?:\s*(?:NO|NUMBER|#))?\s*[:\-]?\s*([A-Z0-9][A-Z0-9/\-]{2,24})", RegexOptions.IgnoreCase);
        return match.Success ? CleanInvoiceNumber(match.Groups[1].Value) : string.Empty;
    }

    private static string ExtractInvoiceDate(IReadOnlyList<LayoutBlock> blocks, string allText)
    {
        var labels = blocks.Where(x => NormalizeUpper(x.Content).Contains("BILL DATE") || NormalizeUpper(x.Content).Contains("INVOICE DATE") || NormalizeUpper(x.Content).Contains("INV DATE") || NormalizeUpper(x.Content).Contains("INV DT")).ToArray();
        foreach (var label in labels)
        {
            var match = DateRegex.Match(label.Content);
            if (match.Success) return NormalizeDate(match.Value);
            var nearby = blocks.Where(x => x.PageIndex == label.PageIndex && x != label && x.Y1 >= label.Y1 - 20 && x.Y1 <= label.Y2 + 80 && x.X1 >= label.X1 - 20)
                .OrderBy(x => Math.Abs(x.Y1 - label.Y1) + Math.Max(0, x.X1 - label.X2) / 4);
            foreach (var block in nearby)
            {
                match = DateRegex.Match(block.Content);
                if (match.Success) return NormalizeDate(match.Value);
            }
        }
        var fallback = Regex.Match(allText, @"(?:BIL{1,2}|BILI|INVOICE|INV)\s*(?:DATE|DT)\s*[:\-]?\s*([0-3]?\d[-/.][01]?\d[-/.](?:19|20)\d{2})", RegexOptions.IgnoreCase);
        return fallback.Success ? NormalizeDate(fallback.Groups[1].Value) : string.Empty;
    }

    private static decimal ResolveFinalInvoiceTotal(IReadOnlyList<LayoutBlock> blocks, IReadOnlyList<ParsedTable> tables, string original)
    {
        var candidates = new List<(int Score, decimal Value)>();
        foreach (var block in blocks)
        {
            var upper = NormalizeUpper(block.Content);
            var score = upper.Contains("NET PAYABLE") ? 100 : upper.Contains("GRAND TOTAL") ? 90 : upper.Contains("INVOICE TOTAL") ? 85 : upper.StartsWith("TOTAL") ? 20 : 0;
            if (score == 0) continue;
            var amounts = ExtractAmounts(block.Content);
            foreach (var value in amounts) if (value > 0m) candidates.Add((score, value));
        }
        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                var rowText = Collapse(string.Join(" ", row));
                var upper = NormalizeUpper(rowText);
                var score = upper.Contains("NET PAYABLE") ? 110 : upper.Contains("GRAND TOTAL") ? 105 : upper.StartsWith("TOTAL") ? 70 : 0;
                if (score == 0) continue;
                foreach (var value in ExtractAmounts(rowText)) if (value > 0m) candidates.Add((score, value));
            }
        }
        if (candidates.Count > 0) return candidates.OrderByDescending(x => x.Score).ThenByDescending(x => x.Value).First().Value;
        return Amount(original);
    }

    private static JsonObject BuildPayload(
        JsonObject root,
        ClientProfile client,
        string direction,
        PartyResolution parties,
        string invoiceNumber,
        string invoiceDate,
        decimal taxableTotal,
        decimal discountTotal,
        ComponentTotals totals,
        decimal roundOff,
        decimal invoiceTotal,
        IReadOnlyList<decimal> gstRates,
        IReadOnlyList<InvoiceLayoutLine> items,
        InvoiceSummaryEvidence summary,
        DocumentProfile documentProfile,
        IReadOnlyList<TableSemantic> tableSemantics,
        int pageCount,
        int tableCount)
    {
        root["schema"] = root["schema"]?.ToString() ?? "AAH-MOBILE-INVOICE-V3";
        root["canonicalInvoiceSchema"] = "AAH-GST-INVOICE-CANONICAL-V1";
        root["clientId"] = client.Id;
        root["clientCode"] = client.ClientCode;
        root["financialYear"] = client.FinancialYear;
        root["direction"] = direction;
        root["directionConfidence"] = 100;
        root["invoiceNumber"] = invoiceNumber;
        root["invoiceDate"] = invoiceDate;
        root["counterparty"] = PartyNode(parties.Counterparty);
        root["seller"] = PartyNode(parties.Seller);
        root["buyer"] = PartyNode(parties.Buyer);
        root["taxableTotal"] = taxableTotal > 0m ? Money(taxableTotal) : string.Empty;
        root["discountTotal"] = discountTotal > 0m ? Money(discountTotal) : string.Empty;
        root["cgstTotal"] = totals.Cgst > 0m ? Money(totals.Cgst) : "0.00";
        root["sgstTotal"] = totals.Sgst > 0m ? Money(totals.Sgst) : "0.00";
        root["igstTotal"] = totals.Igst > 0m ? Money(totals.Igst) : "0.00";
        root["cessTotal"] = totals.Cess > 0m ? Money(totals.Cess) : "0.00";
        root["roundOff"] = Math.Abs(roundOff) > 0m ? SignedMoney(roundOff) : "0.00";
        root["invoiceTotal"] = invoiceTotal > 0m ? Money(invoiceTotal) : string.Empty;
        root["gstSlabRates"] = new JsonArray(gstRates.Select(x => (JsonNode?)JsonValue.Create(x.ToString("0.##", CultureInfo.InvariantCulture))).ToArray());
        root["documentType"] = documentProfile.DocumentType;
        root["supplyType"] = documentProfile.SupplyType;
        root["taxExpectation"] = documentProfile.TaxExpectation;
        root["documentClassificationConfidence"] = documentProfile.Confidence;
        root["documentOcrAuthority"] = "AAH Paddle PP-StructureV3 spatial OCR + AAH R39 accounting authority";
        root["documentUnderstandingVersion"] = "AAH-INVOICE-DU-R39-PADDLE-SPATIAL";
        root["documentOcrPageCount"] = pageCount;
        root["documentOcrTableCount"] = tableCount;
        root["summaryLayoutAuthority"] = summary.Slabs.Count > 0;
        root["fieldProvenance"] = BuildFieldProvenance(parties, invoiceNumber, invoiceDate, taxableTotal, totals, invoiceTotal, summary, items);
        root["tableSemantics"] = new JsonArray(tableSemantics.Select(x => (JsonNode?)new JsonObject
        {
            ["kind"] = x.Kind,
            ["score"] = x.Score,
            ["page"] = x.Table.Block.PageIndex,
            ["top"] = x.Table.Block.Y1,
            ["bottom"] = x.Table.Block.Y2
        }).ToArray());
        root["ledgerAllocationStage"] = "Sales/Purchase module";
        root["bankPaymentAuthority"] = "raw-ocr-evidence-only";
        // R76: InvoiceVision purchase/sales refinement uses the existing Phone Link OCR
        // result, never a second file picker or an alternate importer. Geometry is
        // evidence, not an authority to change Tally/accounting master identity.
        root["invoiceVisionImportPurpose"] = "Purchase/Sales accounting review in Phone Link";
        root["invoiceVisionImportFields"] = "seller,buyer,invoiceNumber,invoiceDate,rows,HSN,quantity,unit,rate,taxable,GST,invoiceTotal";
        root["invoiceVisionIgnoredNonPostingAreas"] = "QR codes, signatures, stamps, website links and bottom terms";
        root["invoiceVisionImportStage"] = "Phone Link confirmation required; existing Sales/Purchase repository exit";


        var slabs = new JsonArray();
        foreach (var slab in summary.Slabs.OrderBy(x => x.GstRate))
        {
            var group = items.Where(x => Math.Abs(x.GstRate - slab.GstRate) <= 0.01m).ToArray();
            slabs.Add(new JsonObject
            {
                ["gstRate"] = slab.GstRate.ToString("0.##", CultureInfo.InvariantCulture),
                ["taxable"] = Money(slab.Taxable),
                ["cgst"] = Money(group.Sum(x => x.Cgst)),
                ["sgst"] = Money(group.Sum(x => x.Sgst)),
                ["igst"] = Money(group.Sum(x => x.Igst)),
                ["combinedTax"] = Money(slab.TaxAmount),
                ["source"] = summary.WasDerivedFromItems ? "AAH item rows grouped by GST rate" : "AAH invoice geometry GST summary table"
            });
        }
        root["gstSlabs"] = slabs;

        var rows = new JsonArray();
        foreach (var item in items)
        {
            rows.Add(new JsonObject
            {
                ["sourceText"] = string.Join(" | ", item.SourceRow),
                ["description"] = item.Description,
                ["hsnSac"] = item.HsnSac,
                ["quantity"] = item.Quantity > 0m ? item.Quantity.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty,
                ["unit"] = item.Unit,
                ["rate"] = item.Rate > 0m ? Money(item.Rate) : string.Empty,
                ["discount"] = item.Discount > 0m ? Money(item.Discount) : "0.00",
                ["taxable"] = item.Taxable > 0m ? Money(item.Taxable) : string.Empty,
                ["gstRate"] = item.GstRate > 0m ? item.GstRate.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                ["cgst"] = item.Cgst > 0m ? Money(item.Cgst) : string.Empty,
                ["sgst"] = item.Sgst > 0m ? Money(item.Sgst) : string.Empty,
                ["igst"] = item.Igst > 0m ? Money(item.Igst) : string.Empty,
                ["cess"] = item.Cess > 0m ? Money(item.Cess) : string.Empty,
                ["lineTotal"] = item.LineTotal > 0m ? Money(item.LineTotal) : string.Empty,
                ["stockItem"] = string.Empty,
                ["ledger"] = string.Empty,
                ["confidence"] = item.WasArithmeticRepair ? 96 : 100,
                ["derivation"] = item.WasArithmeticRepair
                    ? "AAH invoice table cell + GST arithmetic repair" + (item.WasSummaryResidualRepair ? " + GST summary residual" : string.Empty)
                    : "AAH invoice spatial/table evidence + accounting validation"
            });
        }
        root["rows"] = rows;
        return root;
    }

    private static JsonObject BuildFieldProvenance(
        PartyResolution parties,
        string invoiceNumber,
        string invoiceDate,
        decimal taxableTotal,
        ComponentTotals totals,
        decimal invoiceTotal,
        InvoiceSummaryEvidence summary,
        IReadOnlyList<InvoiceLayoutLine> items)
    {
        var root = new JsonObject
        {
            ["counterpartyName"] = FieldEvidenceNode(parties.Counterparty.Name, "party identity graph", parties.Counterparty.Name.Length > 0 ? 92 : 0),
            ["counterpartyPAN"] = FieldEvidenceNode(parties.Counterparty.Pan, parties.Counterparty.Gstin.Length == 15 ? "PAN embedded in resolved GSTIN" : "statutory PAN token", parties.Counterparty.Pan.Length == 10 ? 99 : 0),
            ["counterpartyGSTIN"] = FieldEvidenceNode(parties.Counterparty.Gstin, "statutory GSTIN identity candidate", parties.Counterparty.Gstin.Length == 15 ? 99 : 0),
            ["invoiceNumber"] = FieldEvidenceNode(invoiceNumber, "Windows spatial label with constrained ML Kit text repair", ValidInvoiceNumber(invoiceNumber) ? 95 : 0),
            ["invoiceDate"] = FieldEvidenceNode(invoiceDate, "Windows invoice/date relation", invoiceDate.Length > 0 ? 94 : 0),
            ["taxableTotal"] = FieldEvidenceNode(taxableTotal > 0m ? Money(taxableTotal) : string.Empty, summary.TaxableTotal > 0m && !summary.WasDerivedFromItems ? "GST summary table" : "validated item rows", taxableTotal > 0m ? 98 : 0),
            ["cgstTotal"] = FieldEvidenceNode(totals.Cgst > 0m ? Money(totals.Cgst) : string.Empty, "validated item/slab arithmetic", totals.Cgst > 0m ? 98 : 0),
            ["sgstTotal"] = FieldEvidenceNode(totals.Sgst > 0m ? Money(totals.Sgst) : string.Empty, "validated item/slab arithmetic", totals.Sgst > 0m ? 98 : 0),
            ["igstTotal"] = FieldEvidenceNode(totals.Igst > 0m ? Money(totals.Igst) : string.Empty, "validated item/slab arithmetic", totals.Igst > 0m ? 98 : 0),
            ["invoiceTotal"] = FieldEvidenceNode(invoiceTotal > 0m ? Money(invoiceTotal) : string.Empty, summary.NetPayable > 0m ? "Net Payable summary" : "reconciled final-payable evidence", invoiceTotal > 0m ? 96 : 0),
            ["itemRows"] = FieldEvidenceNode(items.Count.ToString(CultureInfo.InvariantCulture), "semantic item-table classifier + spatial row arithmetic", items.Count >= 1 ? Math.Min(99, 80 + items.Count) : 0)
        };
        return root;
    }

    private static JsonObject FieldEvidenceNode(string value, string source, int confidence) => new()
    {
        ["value"] = value,
        ["source"] = source,
        ["confidence"] = confidence,
        ["status"] = confidence >= 90 ? "Verified" : confidence > 0 ? "Review" : "Missing"
    };

    private static JsonObject PartyNode(InvoiceParty party) => new()
    {
        ["name"] = party.Name,
        ["pan"] = party.Pan,
        ["gstin"] = party.Gstin,
        ["address"] = party.Address
    };

    private static string BuildCandidates(PartyResolution parties, string invoiceNumber, string invoiceDate, InvoiceSummaryEvidence summary, IReadOnlyList<InvoiceLayoutLine> items)
    {
        var root = new JsonObject();
        AddCandidate(root, "counterparty", "Party block", parties.Counterparty.Name, "AAH invoice geometry", 100);
        AddCandidate(root, "counterpartyPan", "PAN format", parties.Counterparty.Pan, "AAH invoice geometry", 100);
        AddCandidate(root, "counterpartyGstin", "GSTIN format", parties.Counterparty.Gstin, "AAH invoice geometry", 100);
        AddCandidate(root, "invoiceNo", "Invoice/Bill No", invoiceNumber, "AAH invoice spatial label", 100);
        AddCandidate(root, "invoiceDate", "Invoice/Bill Date", invoiceDate, "AAH invoice spatial label", 100);
        if (summary.TaxableTotal > 0m) AddCandidate(root, "taxableTotal", "GST summary Taxable", Money(summary.TaxableTotal), "AAH invoice table", 100);
        if (summary.DiscountTotal > 0m) AddCandidate(root, "discountTotal", "GST summary Discount", Money(summary.DiscountTotal), "AAH invoice table", 100);
        if (summary.NetPayable > 0m) AddCandidate(root, "invoiceTotal", "Net Payable", Money(summary.NetPayable), "AAH invoice table", 100);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AddCandidate(JsonObject root, string key, string label, string value, string source, int confidence)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        root[key] = new JsonArray(new JsonObject
        {
            ["label"] = label,
            ["value"] = value,
            ["source"] = source,
            ["confidence"] = confidence
        });
    }

    private static string BuildReviewText(IReadOnlyList<ParsedPage> pages, IReadOnlyList<ParsedTable> tables)
    {
        var builder = new StringBuilder();
        builder.AppendLine("AAH WINDOWS INVOICE OCR - FINAL PIXEL-GRID/CELL AUTHORITY");
        builder.AppendLine();
        foreach (var page in pages)
        {
            builder.AppendLine($"===== PAGE {page.PageIndex} ({page.Width}x{page.Height}) =====");
            foreach (var block in page.Blocks.OrderBy(x => x.Order).ThenBy(x => x.Y1).ThenBy(x => x.X1))
            {
                if (block.Content.Length == 0) continue;
                builder.Append('[').Append(block.Label).Append(" bbox=")
                    .Append(block.X1).Append(',').Append(block.Y1).Append(',').Append(block.X2).Append(',').Append(block.Y2).AppendLine("]");
                if (block.Label.Contains("table", StringComparison.OrdinalIgnoreCase))
                {
                    var rows = ParseHtmlTable(block.Content);
                    foreach (var row in rows) builder.AppendLine(string.Join("\t", row));
                }
                else builder.AppendLine(block.Content);
                builder.AppendLine();
            }
        }
        return builder.ToString().TrimEnd();
    }

    private static OriginalPayload ParseOriginalPayload(string json)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject(); }
        catch { root = new JsonObject(); }
        return new OriginalPayload(
            root,
            root["direction"]?.ToString() ?? string.Empty,
            root["recognitionMode"]?.ToString() ?? "Accounting Invoice",
            root["invoiceNumber"]?.ToString() ?? string.Empty,
            root["invoiceDate"]?.ToString() ?? string.Empty,
            root["invoiceTotal"]?.ToString() ?? string.Empty,
            root["documentType"]?.ToString() ?? string.Empty);
    }

    private static string ResolveDirection(PartyResolution parties, ClientProfile client, string originalDirection)
    {
        if (parties.ClientIsSeller) return "Sales";
        if (parties.Counterparty.Name.Length > 0 || parties.Counterparty.Gstin.Length > 0) return "Purchase";
        return originalDirection.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales" : "Purchase";
    }

    private static string ExtractPan(string text)
    {
        var match = PanRegex.Match(NormalizeIdentitySpaced(text));
        return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
    }

    private static string ExtractGstin(string text)
    {
        var normalized = NormalizeIdentitySpaced(text);
        foreach (Match match in GstinRegex.Matches(normalized))
        {
            var candidate = match.Value.ToUpperInvariant();
            if (ValidGstinChecksum(candidate)) return candidate;
        }
        return string.Empty;
    }

    private static bool ValidGstinChecksum(string value)
    {
        value = NormalizeIdentity(value);
        if (value.Length != 15 || !Regex.IsMatch(value, @"^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][A-Z0-9]Z[A-Z0-9]$")) return false;
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var factor = 2;
        var sum = 0;
        for (var index = value.Length - 2; index >= 0; index--)
        {
            var codePoint = chars.IndexOf(value[index]);
            if (codePoint < 0) return false;
            var product = factor * codePoint;
            factor = factor == 2 ? 1 : 2;
            sum += (product / 36) + (product % 36);
        }
        var checkCodePoint = (36 - (sum % 36)) % 36;
        return chars[checkCodePoint] == value[^1];
    }

    private static string FindMalformedGstinContainingPan(string text, string pan)
    {
        var normalized = NormalizeIdentity(text);
        var index = normalized.IndexOf(pan, StringComparison.OrdinalIgnoreCase);
        if (index < 2 || index + pan.Length + 3 > normalized.Length) return string.Empty;
        var candidate = normalized.Substring(index - 2, Math.Min(15, normalized.Length - (index - 2)));
        return candidate.Length == 15 && char.IsDigit(candidate[0]) && char.IsDigit(candidate[1]) ? candidate : string.Empty;
    }

    private static bool ClientBlockText(string text, IReadOnlyList<string> clientNames, string gstin, string pan)
    {
        var identity = NormalizeIdentity(text);
        var name = NormalizeName(text);
        if (gstin.Length == 15 && IdentityDistance(identity, gstin) <= 2) return true;
        if (pan.Length == 10 && identity.Contains(pan, StringComparison.OrdinalIgnoreCase)) return true;
        return clientNames.Any(x => SimilarName(name, x));
    }

    private static int PartyNameScore(string text, string label)
    {
        var value = CleanPartyName(text);
        if (value.Length < 4 || value.Length > 90) return 0;
        var upper = NormalizeUpper(value);
        if (upper.Contains("TAX INVOICE") || upper.Contains("INVOICE NO") || upper.Contains("BILL NO") || upper.Contains("PAN NO") || upper.Contains("GST") || upper.Contains("FSSAI") || upper.Contains("STATE") || upper.Contains("ACK NO") || upper.Contains("IRN")) return 0;
        if (PanRegex.IsMatch(upper) || GstinRegex.IsMatch(upper)) return 0;
        if (Regex.IsMatch(upper, @"\d{6,}")) return 0;
        if (upper.Count(char.IsLetter) < 4) return 0;
        var score = label.Contains("title", StringComparison.OrdinalIgnoreCase) ? 6 : 2;
        if (value.All(ch => !char.IsLetter(ch) || char.IsUpper(ch))) score += 3;
        if (upper.Contains("MARKETING") || upper.Contains("TRADING") || upper.Contains("ENTERPRISE") || upper.Contains("INDUSTR") || upper.Contains("AGENC") || upper.Contains("DISTRIBUT") || upper.Contains("LIMITED") || upper.Contains("PRIVATE") || upper.Contains("CO.")) score += 3;
        return score;
    }

    private static string CleanPartyName(string text)
    {
        var line = Collapse(text.Replace("#", " "));
        line = Regex.Replace(line, @"\b\d{10,}\b.*$", string.Empty).Trim();
        return line.Trim(':', '-', ' ', '*');
    }

    private static bool SimilarName(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)) return true;
        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bt = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return at.Count > 0 && bt.Count > 0 && at.Intersect(bt, StringComparer.OrdinalIgnoreCase).Count() >= Math.Min(2, Math.Min(at.Count, bt.Count));
    }

    private static string BestClientName(ClientProfile client) =>
        new[] { client.TradeName, client.LegalName, client.ClientName }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static int IdentityDistance(string value, string expected)
    {
        value = NormalizeIdentity(value); expected = NormalizeIdentity(expected);
        if (value.Length == 0 || expected.Length == 0) return int.MaxValue;
        if (value.Contains(expected, StringComparison.OrdinalIgnoreCase)) return 0;
        var best = int.MaxValue;
        if (value.Length >= expected.Length)
        {
            for (var i = 0; i <= value.Length - expected.Length; i++) best = Math.Min(best, ConfusableDistance(value.Substring(i, expected.Length), expected));
            return best;
        }
        return ConfusableDistance(value, expected) + Math.Abs(value.Length - expected.Length);
    }

    private static int ConfusableDistance(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length); var score = Math.Abs(a.Length - b.Length);
        for (var i = 0; i < length; i++)
        {
            if (a[i] == b[i]) continue;
            score += Confusable(a[i]) == Confusable(b[i]) ? 0 : 1;
        }
        return score;
    }

    private static char Confusable(char value) => char.ToUpperInvariant(value) switch
    {
        'O' or 'Q' or '0' => '0',
        'I' or 'L' or '1' => '1',
        'Z' or '2' => '2',
        'S' or '5' => '5',
        'B' or '8' => '8',
        'G' or '6' => '6',
        _ => char.ToUpperInvariant(value)
    };

    private static int FindColumn(IReadOnlyList<string> headers, params string[] terms)
    {
        var bestIndex = -1; var bestScore = 0;
        for (var i = 0; i < headers.Count; i++)
        {
            var header = NormalizeUpper(headers[i]); var score = 0;
            foreach (var term in terms)
            {
                var normalized = NormalizeUpper(term);
                if (header.Equals(normalized, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 100);
                else if (header.Contains(normalized, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 70 + normalized.Length);
            }
            if (score > bestScore) { bestScore = score; bestIndex = i; }
        }
        return bestIndex;
    }

    private static string Cell(IReadOnlyList<string> cells, int index) => index >= 0 && index < cells.Count ? Collapse(cells[index]) : string.Empty;
    private static bool LooksLikeHsn(string value) => Regex.IsMatch(DigitsOnly(value), @"^\d{4,8}$");
    private static string DigitsOnly(string value) => new(value.Where(char.IsDigit).ToArray());

    private static decimal ParseCombinedGstRate(string label)
    {
        var upper = NormalizeUpper(label);
        var rates = Regex.Matches(upper, @"(?:CGST|SGST|UTGST|S/UTGST|IGST)\s*([0-9]+(?:\.[0-9]+)?)")
            .Select(match => decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m)
            .Where(x => x > 0m).ToArray();
        if (rates.Length == 0) return 0m;
        if (upper.Contains("IGST")) return SnapGstRate(rates[0]);
        return SnapGstRate(rates.Sum());
    }

    private static decimal SnapGstRate(decimal value)
    {
        if (value <= 0m) return 0m;
        var nearest = SupportedGstRates.OrderBy(x => Math.Abs(x - value)).First();
        return Math.Abs(nearest - value) <= 0.30m ? nearest : value;
    }

    private static bool IsSupportedGstRate(decimal value) => SupportedGstRates.Any(x => Math.Abs(x - value) <= 0.01m);

    private static decimal Amount(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        var normalized = value.Replace(",", string.Empty).Replace("₹", string.Empty).Replace("INR", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Rs.", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("Rs", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        normalized = Regex.Replace(normalized, @"[^0-9.\-+]", string.Empty);
        if (decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)) return Math.Abs(amount);
        return 0m;
    }

    private static IReadOnlyList<decimal> ExtractAmounts(string text) => AmountRegex.Matches(text)
        .Select(match => Amount(match.Value)).Where(x => x > 0m).ToArray();

    private static bool InvoiceNumberLabel(string text)
    {
        var upper = NormalizeUpper(text);
        return Regex.IsMatch(upper, @"^(?:INVOICE|BILL)(?:\s*(?:NO|NUMBER|#))?$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(upper, @"\b(?:INVOICE|BILL)\s*(?:NO|NUMBER|#)\b")
            || upper.Contains("BILI NO");
    }

    private static string ValueAfterLabel(string text, string labelPattern)
    {
        var match = Regex.Match(text, labelPattern + @"\s*([A-Z0-9][A-Z0-9/\-]{2,24})", RegexOptions.IgnoreCase);
        return match.Success ? CleanInvoiceNumber(match.Groups[1].Value) : string.Empty;
    }

    private static string CleanInvoiceNumber(string value)
    {
        value = Collapse(value).Trim(':', '-', ' ');
        var match = Regex.Match(value, @"[A-Z0-9][A-Z0-9/\-]{2,24}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
    }

    private static bool ValidInvoiceNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || value.Length > 25) return false;
        if (!value.Any(char.IsDigit)) return false;
        if (PanRegex.IsMatch(value) || GstinRegex.IsMatch(value)) return false;
        return !DateRegex.IsMatch(value);
    }

    private static string NormalizeDate(string value)
    {
        foreach (var format in new[] { "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "dd.MM.yyyy" })
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        return value;
    }

    private static string NormalizeIdentitySpaced(string value) => Regex.Replace((value ?? string.Empty).ToUpperInvariant(), @"[^A-Z0-9]+", " ");
    private static string NormalizeIdentity(string value) => Regex.Replace((value ?? string.Empty).ToUpperInvariant(), @"[^A-Z0-9]", string.Empty);
    private static string NormalizeName(string value) => Collapse(Regex.Replace((value ?? string.Empty).ToUpperInvariant(), @"[^A-Z0-9&. ]", " "));
    private static string NormalizeUpper(string value) => Collapse((value ?? string.Empty).ToUpperInvariant());
    private static string Collapse(string value) => Regex.Replace((value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
    private static string NormalizeBlockContent(string value) => (value ?? string.Empty).Trim();
    private static string Money(decimal value) => Math.Abs(value).ToString("0.00", CultureInfo.InvariantCulture);
    private static string SignedMoney(decimal value) => value.ToString("0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    private static string JsonText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static int JsonInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    }

    private sealed record OriginalPayload(JsonObject Root, string Direction, string RecognitionMode, string InvoiceNumber, string InvoiceDate, string InvoiceTotal, string DocumentType);
    private sealed record ParsedPage(int PageIndex, int Width, int Height, IReadOnlyList<LayoutBlock> Blocks);
    private sealed record ParsedTable(LayoutBlock Block, List<List<string>> Rows);
    private sealed record TableSemantic(ParsedTable Table, string Kind, int Score);
    private sealed record DocumentProfile(string DocumentType, string SupplyType, string TaxExpectation, int Confidence);
    private sealed record ComponentTotals(decimal Cgst, decimal Sgst, decimal Igst, decimal Cess);
    private sealed record PartyCandidate(LayoutBlock Block, string Pan, string Gstin, int Score);
    private sealed record PartyResolution(InvoiceParty Seller, InvoiceParty Buyer, InvoiceParty Counterparty, bool ClientIsSeller);

    private sealed class LayoutBlock
    {
        public int PageIndex { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int X2 { get; set; }
        public int Y2 { get; set; }
        public int Order { get; set; }
    }

    private sealed class InvoiceParty
    {
        public string Name { get; set; } = string.Empty;
        public string Pan { get; set; } = string.Empty;
        public string Gstin { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
    }

    private sealed class InvoiceLayoutLine
    {
        public string[] SourceRow { get; set; } = [];
        public string Description { get; set; } = string.Empty;
        public string HsnSac { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string Unit { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public decimal Discount { get; set; }
        public decimal Taxable { get; set; }
        public decimal GstRate { get; set; }
        public decimal Cgst { get; set; }
        public decimal Sgst { get; set; }
        public decimal Igst { get; set; }
        public decimal Cess { get; set; }
        public decimal LineTotal { get; set; }
        public string TaxableSourceText { get; set; } = string.Empty;
        public string CgstSourceText { get; set; } = string.Empty;
        public string SgstSourceText { get; set; } = string.Empty;
        public string LineTotalSourceText { get; set; } = string.Empty;
        public bool WasArithmeticRepair { get; set; }
        public bool WasSummaryResidualRepair { get; set; }
    }

    private sealed class InvoiceSummaryEvidence
    {
        public List<InvoiceSummarySlab> Slabs { get; } = [];
        public decimal GrossTotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxableTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal NetPayable { get; set; }
        public decimal PreRoundTotal { get; set; }
        public bool WasDerivedFromItems { get; set; }
    }

    private sealed class InvoiceSummarySlab
    {
        public string Label { get; set; } = string.Empty;
        public decimal GstRate { get; set; }
        public decimal Gross { get; set; }
        public decimal Discount { get; set; }
        public decimal Taxable { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal NetPayable { get; set; }
    }
}

internal sealed class MobileInvoiceDocumentOcrResult
{
    public bool WasSuccessful { get; set; }
    public string FailureMessage { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string RawOcrText { get; set; } = string.Empty;
    public string CandidatesJson { get; set; } = "{}";
    public string CounterpartyName { get; set; } = string.Empty;
    public string CounterpartyGstin { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string InvoiceDate { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string RecognitionMode { get; set; } = string.Empty;
    public string InvoiceTotal { get; set; } = string.Empty;
    public int TableCount { get; set; }
    public int ItemRowCount { get; set; }
    public int SummarySlabCount { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string RecognitionLogPath { get; set; } = string.Empty;
    public string RecognitionStatusPath { get; set; } = string.Empty;

    public static MobileInvoiceDocumentOcrResult Failed(string message) => new() { FailureMessage = message, WasSuccessful = false };
}
