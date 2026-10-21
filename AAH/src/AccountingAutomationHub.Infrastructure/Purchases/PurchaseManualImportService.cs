using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Infrastructure.Purchases;

/// <summary>
/// Manual Purchase Register import. This is deliberately separate from the statutory
/// GSTR-2A/2B source-set importer: a user-selected books Purchase sheet is parsed through
/// the same global/per-file header mapping authority and is persisted as Manual Import.
/// It never pretends that the workbook itself is GSTR-2A/2B evidence.
/// </summary>
public sealed class PurchaseManualImportService : IPurchaseManualImportService
{
    private readonly FlexibleWorkbookReader _reader = new();

    public PurchaseManualInspection Inspect(
        string path,
        IReadOnlyDictionary<string, string>? savedMapping = null,
        string? sheetName = null,
        int? headerRow = null,
        int? dataStartRow = null)
    {
        var workbook = _reader.Read(path, CancellationToken.None);
        return Inspect(workbook, path, savedMapping, sheetName, headerRow, dataStartRow);
    }

    public PurchaseManualInspection Inspect(
        OpenXmlWorkbookData workbook,
        string sourcePath,
        IReadOnlyDictionary<string, string>? savedMapping = null,
        string? sheetName = null,
        int? headerRow = null,
        int? dataStartRow = null)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        var path = sourcePath;
        using var scope = ImportHeaderMappingRuntime.BeginSource(path);

        var readableSheets = workbook.Worksheets.Values
            .Where(sheet => sheet.MaxRow > 0)
            .ToArray();
        if (readableSheets.Length == 0)
            throw new InvalidDataException("No readable worksheet rows were found in the selected workbook.");

        HeaderInspection best;
        var explicitSelection = !string.IsNullOrWhiteSpace(sheetName) && headerRow.HasValue;
        if (explicitSelection)
        {
            var selectedSheet = readableSheets.FirstOrDefault(sheet =>
                sheet.Name.Equals(sheetName!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (selectedSheet is null)
                throw new InvalidDataException($"Worksheet '{sheetName}' was not found in the selected workbook.");
            if (headerRow!.Value < 1 || headerRow.Value > selectedSheet.MaxRow)
                throw new InvalidDataException($"Header Row must be between 1 and {selectedSheet.MaxRow:N0} for sheet '{selectedSheet.Name}'.");

            var requestedDataStart = dataStartRow.GetValueOrDefault(headerRow.Value + 1);
            if (requestedDataStart <= headerRow.Value)
                throw new InvalidDataException("Data Start Row must be below the selected Header Row.");

            best = InspectSheet(selectedSheet, headerRow.Value, requestedDataStart);
        }
        else
        {
            // Normal automatic recognition remains conservative: visible worksheets are
            // authoritative. If that fails, do not silently import from a hidden worksheet.
            // Instead use the strongest hidden/all-sheet candidate only to prefill Header
            // Recovery so the user can explicitly confirm the source sheet and row.
            var visibleBest = FindBestHeader(workbook, visibleOnly: true);
            if (visibleBest is not null &&
                visibleBest.Score >= 2 &&
                IsComplete(visibleBest.Mapping.Values))
            {
                best = visibleBest;
            }
            else
            {
                best = FindBestHeader(workbook, visibleOnly: false)
                    ?? throw new InvalidDataException("No readable worksheet rows were found in the selected workbook.");
            }
        }

        var mapping = new Dictionary<string, string>(best.Mapping, StringComparer.OrdinalIgnoreCase);
        if (savedMapping is not null)
        {
            // Fix114 Sales-parity contract: a saved Purchase profile is the complete mapping
            // authority for this exact header signature/layout. Do not merge it underneath
            // built-in/global guesses. A saved blank explicitly means ignore that source
            // column, exactly like the Sales saved-header repository overrides its inspection.
            foreach (var header in best.Headers.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!savedMapping.TryGetValue(header, out var savedTarget))
                {
                    mapping[header] = string.Empty;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(savedTarget))
                {
                    mapping[header] = string.Empty;
                    continue;
                }

                mapping[header] = ImportHeaderMappingCatalog.FindField(ImportHeaderMappingCatalog.Purchase, savedTarget) is null
                    ? string.Empty
                    : savedTarget.Trim();
            }
        }

        var selectedSheetIsHidden = best.Sheet.State.Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
                                    best.Sheet.State.Equals("veryHidden", StringComparison.OrdinalIgnoreCase);
        var autoDetected = explicitSelection
            ? best.Headers.Any(value => !string.IsNullOrWhiteSpace(value))
            : best.Score >= 2 && !selectedSheetIsHidden;
        return new PurchaseManualInspection
        {
            SourcePath = path,
            HeaderSignature = HeaderSignature(best.Headers),
            Headers = best.Headers,
            SuggestedMapping = mapping,
            SampleValues = best.SampleValues,
            IsComplete = IsComplete(mapping.Values),
            HeaderDetected = autoDetected,
            SelectedSheetIsHidden = selectedSheetIsHidden,
            DetectionMessage = autoDetected
                ? $"Purchase headers detected in '{best.Sheet.Name}' row {best.HeaderRow:N0}."
                : selectedSheetIsHidden
                    ? $"No usable visible-sheet header was detected. '{best.Sheet.Name}' is hidden in the workbook and is the strongest Purchase candidate. Confirm the Header Row and Data Start Row, then rescan."
                    : $"Purchase headers were not detected with enough confidence. Confirm the worksheet, Header Row and Data Start Row, then rescan.",
            SheetName = best.Sheet.Name,
            HeaderRow = best.HeaderRow,
            DataStartRow = best.DataStartRow,
            AvailableSheets = readableSheets.Select(sheet => sheet.Name).ToArray(),
            HiddenSheets = readableSheets
                .Where(sheet => sheet.State.Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
                                sheet.State.Equals("veryHidden", StringComparison.OrdinalIgnoreCase))
                .Select(sheet => sheet.Name)
                .ToArray()
        };
    }

    public PurchaseManualImportResult Import(
        string path,
        long clientId,
        string clientGstin,
        string companyName,
        string financialYear,
        IReadOnlyDictionary<string, string> mapping,
        string? selectedSheetName = null,
        int? selectedHeaderRow = null,
        int? selectedDataStartRow = null,
        string defaultGstTreatment = PurchaseTreatmentPolicy.NotShownInTwoB)
    {
        var workbook = _reader.Read(path, CancellationToken.None);
        return Import(
            workbook, path, clientId, clientGstin, companyName, financialYear, mapping,
            selectedSheetName, selectedHeaderRow, selectedDataStartRow, defaultGstTreatment);
    }

    public PurchaseManualImportResult Import(
        OpenXmlWorkbookData workbook,
        string sourcePath,
        long clientId,
        string clientGstin,
        string companyName,
        string financialYear,
        IReadOnlyDictionary<string, string> mapping,
        string? selectedSheetName = null,
        int? selectedHeaderRow = null,
        int? selectedDataStartRow = null,
        string defaultGstTreatment = PurchaseTreatmentPolicy.NotShownInTwoB)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        var path = sourcePath;
        using var scope = ImportHeaderMappingRuntime.BeginSource(path);
        var records = new List<PurchaseImportRecord>();
        var issues = new List<PurchaseImportIssue>();
        var itemSequence = 0;
        var normalizedClientGstin = PurchaseImportUtilities.NormalizeGstin(clientGstin);

        if (!TryFinancialYearRange(financialYear, out var fyStart, out var fyEnd))
            throw new InvalidOperationException("Select a valid Financial Year such as 2025-26 before importing the Purchase Register.");

        foreach (var sheet in workbook.Worksheets.Values)
        {
            if (!string.IsNullOrWhiteSpace(selectedSheetName) &&
                !sheet.Name.Equals(selectedSheetName.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            var isSelectedOverride = !string.IsNullOrWhiteSpace(selectedSheetName) &&
                                     selectedHeaderRow.HasValue &&
                                     sheet.Name.Equals(selectedSheetName.Trim(), StringComparison.OrdinalIgnoreCase);
            var inspection = isSelectedOverride
                ? InspectSheet(
                    sheet,
                    selectedHeaderRow!.Value,
                    selectedDataStartRow.GetValueOrDefault(selectedHeaderRow.Value + 1))
                : InspectSheet(sheet);
            if (inspection.Score < 2 && !isSelectedOverride) continue;

            var effective = new Dictionary<string, string>(inspection.Mapping, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in mapping)
            {
                if (!inspection.Headers.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) continue;

                // Fix113: the mapping window is the final authority for this manual import.
                // A blank target explicitly means "ignore this source column" and must not
                // be silently replaced by the automatic/built-in mapping during Import.
                if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    effective[pair.Key] = string.Empty;
                    continue;
                }

                if (ImportHeaderMappingCatalog.FindField(ImportHeaderMappingCatalog.Purchase, pair.Value) is not null)
                    effective[pair.Key] = pair.Value;
            }

            if (!IsComplete(effective.Values))
            {
                issues.Add(new PurchaseImportIssue
                {
                    SourceFile = Path.GetFileName(path),
                    SheetName = sheet.Name,
                    RowNumber = inspection.HeaderRow,
                    Message = $"Required Purchase Register fields could not be mapped in sheet '{sheet.Name}'."
                });
                continue;
            }

            var columnTargets = new Dictionary<int, string>();
            for (var index = 0; index < inspection.Headers.Count; index++)
            {
                var header = inspection.Headers[index];
                if (effective.TryGetValue(header, out var target) && !string.IsNullOrWhiteSpace(target))
                    columnTargets[index + 1] = target.Trim();
            }

            string carrySupplier = string.Empty;
            string carryGstin = string.Empty;
            string carryDocument = string.Empty;
            DateTime? carryDate = null;
            string carryDestination = string.Empty;

            for (var rowNumber = inspection.DataStartRow; rowNumber <= sheet.MaxRow; rowNumber++)
            {
                if (!sheet.Rows.TryGetValue(rowNumber, out var rowCells) || rowCells.Count == 0) continue;
                var rowText = string.Join(" ", rowCells.OrderBy(pair => pair.Key).Take(10).Select(pair => pair.Value)).Trim();
                if (rowText.Length == 0 || IsTotalRow(rowText)) continue;

                var row = new MutablePurchaseRow();
                foreach (var pair in columnTargets)
                    Assign(row, pair.Value, sheet.GetCell(rowNumber, pair.Key), workbook.Uses1904DateSystem);
                FinalizeDocumentIdentity(row);

                if (row.SupplierName.Length == 0) row.SupplierName = carrySupplier;
                else carrySupplier = row.SupplierName;
                if (row.SupplierGstin.Length == 0) row.SupplierGstin = carryGstin;
                else carryGstin = row.SupplierGstin;

                var inheritedDocument = false;
                if (row.DocumentNumber.Length == 0 && carryDocument.Length > 0)
                {
                    row.DocumentNumber = carryDocument;
                    row.DocumentDate ??= carryDate;
                    inheritedDocument = true;
                }
                else if (row.DocumentNumber.Length > 0)
                {
                    if (row.DocumentDate is null && row.DocumentNumber.Equals(carryDocument, StringComparison.OrdinalIgnoreCase))
                        row.DocumentDate = carryDate;
                    carryDocument = row.DocumentNumber;
                    if (row.DocumentDate.HasValue) carryDate = row.DocumentDate;
                }

                row.SupplierGstin = PurchaseImportUtilities.NormalizeGstin(row.SupplierGstin);
                if (row.SupplierGstin.Length > 0 && !PurchaseImportUtilities.IsValidGstin(row.SupplierGstin))
                {
                    issues.Add(NewIssue(path, sheet.Name, rowNumber, $"Supplier GSTIN '{row.SupplierGstin}' is invalid."));
                    continue;
                }
                if (row.SupplierGstin.Length == 0 && row.SupplierName.Length == 0) continue;
                if (row.DocumentNumber.Length == 0)
                {
                    issues.Add(NewIssue(path, sheet.Name, rowNumber, "Purchase row has no Invoice / Debit Note number and could not inherit one."));
                    continue;
                }
                if (row.DocumentDate is null)
                {
                    issues.Add(NewIssue(path, sheet.Name, rowNumber, $"{row.DocumentNumber}: document date is missing or invalid."));
                    continue;
                }
                if (row.DocumentDate.Value.Date < fyStart || row.DocumentDate.Value.Date > fyEnd)
                {
                    issues.Add(NewIssue(path, sheet.Name, rowNumber, $"{row.DocumentNumber}: document date {row.DocumentDate:dd-MM-yyyy} is outside FY {financialYear}."));
                    continue;
                }

                NormalizeAmounts(row);
                var destination = inheritedDocument && carryDestination.Length > 0
                    ? carryDestination
                    : ResolveDestination(row, sheet.Name);
                if (!inheritedDocument) carryDestination = destination;
                var isRcm = destination.Equals("RCM", StringComparison.OrdinalIgnoreCase);
                var record = new PurchaseImportRecord
                {
                    ClientGstin = normalizedClientGstin,
                    CompanyName = companyName.Trim(),
                    ImportMode = "Manual Import",
                    Destination = destination,
                    ReconciliationStatus = "Manual Purchase Register - Books",
                    GstApplicability = ResolveManualGstTreatment(row.GstTreatment, defaultGstTreatment, isRcm),
                    SupplierGstin = row.SupplierGstin,
                    SupplierName = row.SupplierName.Trim(),
                    DocumentType = row.DocumentType.Length > 0 ? row.DocumentType.Trim() : destination,
                    DocumentNumber = row.DocumentNumber.Trim(),
                    DocumentDate = row.DocumentDate,
                    GstDate = row.DocumentDate,
                    SourceDocumentValue = row.DocumentValue,
                    DocumentValue = row.DocumentValue,
                    Rate = row.Rate,
                    RateKnown = row.RateKnown,
                    TaxableValue = row.TaxableValue,
                    Igst = row.Igst,
                    Cgst = row.Cgst,
                    Sgst = row.Sgst,
                    Cess = row.Cess,
                    RoundOff = row.RoundOff,
                    PurchaseLedger = row.PurchaseLedger.Trim(),
                    ItemPurchaseLedger = row.ItemPurchaseLedger.Trim(),
                    SourceSection = $"Manual Purchase Register • {sheet.Name}",
                    PlaceOfSupply = row.PlaceOfSupply.Trim(),
                    ReverseCharge = row.ReverseCharge.Trim(),
                    InvoiceType = row.SupplyType.Trim(),
                    ItcAvailability = row.ItcAvailability.Trim(),
                    DifferenceDetails = row.Reason.Trim(),
                    ItemName = row.ItemName.Trim(),
                    HsnCode = row.HsnCode.Trim(),
                    ActualQuantity = row.ActualQuantity,
                    Unit = row.Unit.Trim(),
                    ItemRate = row.ItemRate,
                    BilledQuantity = row.BilledQuantity,
                    ItemAmount = row.ItemAmount,
                    AccountingAmount = row.ItemAmount != 0m ? row.ItemAmount : row.TaxableValue,
                    Godown = row.Godown.Trim(),
                    BatchName = row.BatchName.Trim(),
                    DiscountPercent = row.DiscountPercent,
                    DiscountAmount = row.DiscountAmount,
                    OrderNumber = row.OrderNumber.Trim(),
                    TrackingNumber = row.TrackingNumber.Trim(),
                    ManufacturingDate = row.ManufacturingDate,
                    ExpiryDate = row.ExpiryDate,
                    ItemDescription = row.ItemDescription.Trim(),
                    CostCentre = row.CostCentre.Trim(),
                    FreeQuantity = row.FreeQuantity,
                    OriginalDocumentNumber = row.OriginalInvoiceNo.Trim(),
                    OriginalDocumentDate = row.OriginalInvoiceDate,
                    OriginalItemName = row.ItemName.Trim(),
                    OriginalUnit = row.Unit.Trim(),
                    OriginalActualQuantity = row.ActualQuantity,
                    OriginalBilledQuantity = row.BilledQuantity,
                    OriginalItemRate = row.ItemRate,
                    OriginalItemAmount = row.ItemAmount,
                    BatchActualQuantity = row.ActualQuantity,
                    BatchBilledQuantity = row.BilledQuantity,
                    BatchAmount = row.ItemAmount,
                    ItemSequence = ++itemSequence
                };

                PurchaseTreatmentPolicy.ApplyAccountingShape(record);
                records.Add(record);
            }
        }

        if (records.Count == 0 && issues.Count == 0)
            throw new InvalidDataException("No usable Purchase Register rows were found in the selected workbook.");

        return new PurchaseManualImportResult
        {
            SourcePath = Path.GetFullPath(path),
            HeaderSignature = Inspect(
                workbook,
                path,
                mapping,
                selectedSheetName,
                selectedHeaderRow,
                selectedDataStartRow).HeaderSignature,
            Records = records,
            Issues = issues,
            PurchaseRows = records.Count(row => row.Destination.Equals("Purchase", StringComparison.OrdinalIgnoreCase)),
            RcmRows = records.Count(row => row.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase)),
            DebitNoteRows = records.Count(row => row.Destination.Equals(PurchaseTreatmentPolicy.DebitNoteDestination, StringComparison.OrdinalIgnoreCase))
        };
    }

    private static HeaderInspection? FindBestHeader(
        OpenXmlWorkbookData workbook,
        bool visibleOnly)
    {
        var candidates = workbook.Worksheets.Values
            .Where(sheet => sheet.MaxRow > 0)
            .Where(sheet => !visibleOnly ||
                            (!sheet.State.Equals("hidden", StringComparison.OrdinalIgnoreCase) &&
                             !sheet.State.Equals("veryHidden", StringComparison.OrdinalIgnoreCase)))
            .Select(sheet => InspectSheet(sheet))
            .OrderByDescending(item => IsComplete(item.Mapping.Values))
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.HeaderRow)
            .ToArray();
        return candidates.FirstOrDefault();
    }

    private static HeaderInspection InspectSheet(
        OpenXmlWorksheetData sheet,
        int? forcedHeaderRow = null,
        int? forcedDataStartRow = null)
    {
        HeaderInspection? best = null;
        var firstRow = forcedHeaderRow.GetValueOrDefault(1);
        var lastRow = forcedHeaderRow.HasValue
            ? forcedHeaderRow.Value
            : Math.Min(sheet.MaxRow, 25);

        for (var rowNumber = firstRow; rowNumber <= lastRow; rowNumber++)
        {
            if (!sheet.Rows.TryGetValue(rowNumber, out var cells) || cells.Count == 0)
            {
                if (forcedHeaderRow.HasValue)
                {
                    var emptyDataStart = Math.Max(rowNumber + 1, forcedDataStartRow.GetValueOrDefault(rowNumber + 1));
                    return new HeaderInspection(
                        sheet,
                        rowNumber,
                        emptyDataStart,
                        [],
                        new Dictionary<string, string>(),
                        new Dictionary<string, string>(),
                        0);
                }
                continue;
            }

            var maxColumn = Math.Min(cells.Keys.DefaultIfEmpty(0).Max(), 120);
            var headers = Enumerable.Range(1, maxColumn)
                .Select(column => sheet.GetCell(rowNumber, column).Trim())
                .ToArray();
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var score = 0;
            var dataStart = Math.Max(
                rowNumber + 1,
                forcedDataStartRow.GetValueOrDefault(rowNumber + 1));

            for (var index = 0; index < headers.Length; index++)
            {
                var header = headers[index];
                if (header.Length == 0) continue;
                // Fix113: real header text is the first authority. A built-in Purchase alias
                // always wins over persisted/global aliases. During explicit Header Recovery,
                // unmatched headers stay blank so old global mappings cannot reappear after the
                // user clears them; the user can map those columns explicitly in the window.
                var builtIn = ImportHeaderMappingCatalog.ResolveBuiltIn(ImportHeaderMappingCatalog.Purchase, header);
                var resolved = builtIn.Length > 0
                    ? builtIn
                    : forcedHeaderRow.HasValue
                        ? string.Empty
                        : ImportHeaderMappingRuntime.Resolve(ImportHeaderMappingCatalog.Purchase, header);
                if (resolved.Length > 0 && usedTargets.Add(resolved))
                {
                    mapping[header] = resolved;
                    score++;
                }
                else
                {
                    // Duplicate target suggestions are not guessed. The first stronger header
                    // keeps the target and this source column is left for explicit review.
                    mapping[header] = string.Empty;
                }

                var column = index + 1;
                for (var sampleRow = dataStart; sampleRow <= Math.Min(sheet.MaxRow, dataStart + 4); sampleRow++)
                {
                    var sample = sheet.GetCell(sampleRow, column).Trim();
                    if (sample.Length == 0) continue;
                    samples[header] = sample;
                    break;
                }
            }

            var canonical = mapping.Values.Where(value => value.Length > 0).ToArray();
            if (canonical.Contains("InvoiceDate", StringComparer.OrdinalIgnoreCase) ||
                canonical.Contains("NoteDate", StringComparer.OrdinalIgnoreCase)) score += 2;
            if (canonical.Contains("InvoiceNo", StringComparer.OrdinalIgnoreCase) ||
                canonical.Contains("NoteNo", StringComparer.OrdinalIgnoreCase)) score += 2;
            if (canonical.Contains("SupplierName", StringComparer.OrdinalIgnoreCase) ||
                canonical.Contains("GSTIN", StringComparer.OrdinalIgnoreCase)) score += 2;
            if (canonical.Contains("TaxableValue", StringComparer.OrdinalIgnoreCase) ||
                canonical.Contains("InvoiceValue", StringComparer.OrdinalIgnoreCase) ||
                canonical.Contains("ItemAmount", StringComparer.OrdinalIgnoreCase) ||
                canonical.Contains("NoteValue", StringComparer.OrdinalIgnoreCase)) score += 2;

            var inspection = new HeaderInspection(
                sheet,
                rowNumber,
                dataStart,
                headers,
                mapping,
                samples,
                score);
            if (best is null || inspection.Score > best.Score)
                best = inspection;
        }

        return best ?? new HeaderInspection(
            sheet,
            Math.Max(1, firstRow),
            Math.Max(2, forcedDataStartRow.GetValueOrDefault(firstRow + 1)),
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            0);
    }

    private static bool IsComplete(IEnumerable<string> mapped)
    {
        var set = mapped.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (set.Contains("InvoiceDate") || set.Contains("NoteDate")) &&
               (set.Contains("InvoiceNo") || set.Contains("NoteNo")) &&
               (set.Contains("SupplierName") || set.Contains("GSTIN")) &&
               (set.Contains("TaxableValue") || set.Contains("InvoiceValue") || set.Contains("ItemAmount") || set.Contains("NoteValue"));
    }

    private static void Assign(MutablePurchaseRow row, string target, string text, bool uses1904)
    {
        text = (text ?? string.Empty).Trim();
        switch (target)
        {
            case "GSTIN": row.SupplierGstin = text; break;
            case "SupplierName": row.SupplierName = text; break;
            case "InvoiceNo": row.InvoiceNumber = text; break;
            case "InvoiceDate": row.InvoiceDate = PurchaseImportUtilities.ParseExcelDate(text, uses1904); break;
            case "InvoiceValue": row.InvoiceValue = PurchaseImportUtilities.ParseDecimal(text); break;
            case "TaxableValue": row.TaxableValue = PurchaseImportUtilities.ParseDecimal(text); break;
            case "Rate": row.RateKnown = PurchaseImportUtilities.TryParseDecimal(text, out row.Rate); break;
            case "IGST": row.Igst = PurchaseImportUtilities.ParseDecimal(text); break;
            case "CGST": row.Cgst = PurchaseImportUtilities.ParseDecimal(text); break;
            case "SGST": row.Sgst = PurchaseImportUtilities.ParseDecimal(text); break;
            case "Cess": row.Cess = PurchaseImportUtilities.ParseDecimal(text); break;
            case "PlaceOfSupply": row.PlaceOfSupply = text; break;
            case "ReverseCharge": row.ReverseCharge = text; break;
            case "ITCAvailability": row.ItcAvailability = text; break;
            case "GSTTreatment": row.GstTreatment = text; break;
            case "Reason": row.Reason = text; break;
            case "NoteNo": row.NoteNumber = text; break;
            case "NoteDate": row.NoteDate = PurchaseImportUtilities.ParseExcelDate(text, uses1904); break;
            case "NoteValue": row.NoteValue = PurchaseImportUtilities.ParseDecimal(text); break;
            case "OriginalInvoiceNo": row.OriginalInvoiceNo = text; break;
            case "OriginalInvoiceDate": row.OriginalInvoiceDate = PurchaseImportUtilities.ParseExcelDate(text, uses1904); break;
            case "SupplyType": row.SupplyType = text; break;
            case "DocumentType": row.DocumentType = text; break;
            case "ItemName": row.ItemName = text; break;
            case "HsnCode": row.HsnCode = text; break;
            case "ActualQuantity": row.ActualQuantity = PurchaseImportUtilities.ParseDecimal(text); break;
            case "Unit": row.Unit = text; break;
            case "ItemRate": row.ItemRate = PurchaseImportUtilities.ParseDecimal(text); break;
            case "BilledQuantity": row.BilledQuantity = PurchaseImportUtilities.ParseDecimal(text); break;
            case "ItemAmount": row.ItemAmount = PurchaseImportUtilities.ParseDecimal(text); break;
            case "ItemPurchaseLedger": row.ItemPurchaseLedger = text; row.PurchaseLedger = text; break;
            case "Godown": row.Godown = text; break;
            case "BatchName": row.BatchName = text; break;
            case "DiscountPercent": row.DiscountPercent = PurchaseImportUtilities.ParseDecimal(text); break;
            case "DiscountAmount": row.DiscountAmount = PurchaseImportUtilities.ParseDecimal(text); break;
            case "OrderNumber": row.OrderNumber = text; break;
            case "TrackingNumber": row.TrackingNumber = text; break;
            case "ManufacturingDate": row.ManufacturingDate = PurchaseImportUtilities.ParseExcelDate(text, uses1904); break;
            case "ExpiryDate": row.ExpiryDate = PurchaseImportUtilities.ParseExcelDate(text, uses1904); break;
            case "ItemDescription": row.ItemDescription = text; break;
            case "CostCentre": row.CostCentre = text; break;
            case "FreeQuantity": row.FreeQuantity = PurchaseImportUtilities.ParseDecimal(text); break;
        }
    }


    private static void FinalizeDocumentIdentity(MutablePurchaseRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.NoteNumber))
        {
            row.DocumentNumber = row.NoteNumber.Trim();
            row.DocumentDate = row.NoteDate ?? row.InvoiceDate;
            row.DocumentValue = row.NoteValue != 0m ? row.NoteValue : row.InvoiceValue;
            row.HasDebitNoteEvidence = true;
            if (string.IsNullOrWhiteSpace(row.DocumentType)) row.DocumentType = "Debit Note";
            return;
        }

        row.DocumentNumber = row.InvoiceNumber.Trim();
        row.DocumentDate = row.InvoiceDate;
        row.DocumentValue = row.InvoiceValue;
    }

    private static void NormalizeAmounts(MutablePurchaseRow row)
    {
        if (row.BilledQuantity == 0m && row.ActualQuantity != 0m) row.BilledQuantity = row.ActualQuantity;
        if (row.ActualQuantity == 0m && row.BilledQuantity != 0m) row.ActualQuantity = row.BilledQuantity;
        if (row.ItemAmount == 0m && row.TaxableValue != 0m) row.ItemAmount = row.TaxableValue;
        if (row.TaxableValue == 0m && row.ItemAmount != 0m) row.TaxableValue = row.ItemAmount;
        if (row.ItemRate == 0m && row.BilledQuantity != 0m && row.ItemAmount != 0m)
            row.ItemRate = Math.Round(row.ItemAmount / row.BilledQuantity, 6, MidpointRounding.AwayFromZero);

        if ((!row.RateKnown || row.Rate == 0m) &&
            (Math.Abs(row.Igst) + Math.Abs(row.Cgst) + Math.Abs(row.Sgst)) > 0.005m &&
            PurchaseImportUtilities.TryDeriveSupportedRate(row.TaxableValue, row.Igst, row.Cgst, row.Sgst, out var derived))
        {
            row.Rate = derived;
            row.RateKnown = true;
        }

        var beforeRound = row.TaxableValue + row.Igst + row.Cgst + row.Sgst + row.Cess;
        if (row.DocumentValue == 0m) row.DocumentValue = beforeRound;
        row.RoundOff = Math.Round(row.DocumentValue - beforeRound, 2, MidpointRounding.AwayFromZero);
    }

    private static string ResolveManualGstTreatment(string rowTreatment, string defaultTreatment, bool isRcm)
    {
        if (isRcm)
        {
            var candidate = string.IsNullOrWhiteSpace(rowTreatment) ? defaultTreatment : rowTreatment;
            var normalized = (candidate ?? string.Empty).Trim();
            if (normalized.Contains("CLAIM", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("REVERSE", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("NO ITC", StringComparison.OrdinalIgnoreCase))
                return PurchaseTreatmentPolicy.NormalizeRcmTreatment(normalized);
            return PurchaseTreatmentPolicy.RcmSameMonth;
        }

        // Fix116 R1: non-RCM Manual Purchase is books evidence. GST Treatment is not
        // inferred, defaulted or imported for this path; accounting treats it as eligible
        // normal Purchase through PurchaseTreatmentPolicy.IsManualBooksImport. The shared
        // profile treatment remains available to Auto Import only.
        return string.Empty;
    }

    private static string ResolveDestination(MutablePurchaseRow row, string sheetName)
    {
        var combined = $"{row.DocumentType} {sheetName}";
        if (IsYes(row.ReverseCharge) || combined.Contains("RCM", StringComparison.OrdinalIgnoreCase) || combined.Contains("REVERSE CHARGE", StringComparison.OrdinalIgnoreCase))
            return "RCM";
        if (row.HasDebitNoteEvidence ||
            combined.Contains("DEBIT NOTE", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("PURCHASE RETURN", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("RETURN", StringComparison.OrdinalIgnoreCase))
            return PurchaseTreatmentPolicy.DebitNoteDestination;
        return "Purchase";
    }

    private static bool IsYes(string value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTotalRow(string text)
    {
        var normalized = text.Trim().ToUpperInvariant();
        return normalized is "TOTAL" or "GRAND TOTAL" || normalized.StartsWith("TOTAL ", StringComparison.Ordinal);
    }

    private static PurchaseImportIssue NewIssue(string path, string sheet, int row, string message) => new()
    {
        SourceFile = Path.GetFileName(path),
        SheetName = sheet,
        RowNumber = row,
        Message = message
    };

    private static string HeaderSignature(IEnumerable<string> headers)
    {
        var normalized = string.Join("|", headers.Select(ImportHeaderMappingCatalog.NormalizeHeader));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static bool TryFinancialYearRange(string financialYear, out DateTime start, out DateTime end)
    {
        start = default;
        end = default;
        var first = (financialYear ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)) return false;
        start = new DateTime(year, 4, 1);
        end = new DateTime(year + 1, 3, 31);
        return true;
    }

    private sealed record HeaderInspection(
        OpenXmlWorksheetData Sheet,
        int HeaderRow,
        int DataStartRow,
        IReadOnlyList<string> Headers,
        IReadOnlyDictionary<string, string> Mapping,
        IReadOnlyDictionary<string, string> SampleValues,
        int Score);

    private sealed class MutablePurchaseRow
    {
        public string SupplierGstin = string.Empty;
        public string SupplierName = string.Empty;
        public string DocumentType = string.Empty;
        public string InvoiceNumber = string.Empty;
        public DateTime? InvoiceDate;
        public decimal InvoiceValue;
        public string NoteNumber = string.Empty;
        public DateTime? NoteDate;
        public decimal NoteValue;
        public bool HasDebitNoteEvidence;
        public string DocumentNumber = string.Empty;
        public DateTime? DocumentDate;
        public decimal DocumentValue;
        public decimal Rate;
        public bool RateKnown;
        public decimal TaxableValue;
        public decimal Igst;
        public decimal Cgst;
        public decimal Sgst;
        public decimal Cess;
        public decimal RoundOff;
        public string PlaceOfSupply = string.Empty;
        public string ReverseCharge = string.Empty;
        public string ItcAvailability = string.Empty;
        public string GstTreatment = string.Empty;
        public string Reason = string.Empty;
        public string SupplyType = string.Empty;
        public string OriginalInvoiceNo = string.Empty;
        public DateTime? OriginalInvoiceDate;
        public string PurchaseLedger = string.Empty;
        public string ItemName = string.Empty;
        public string HsnCode = string.Empty;
        public decimal ActualQuantity;
        public string Unit = string.Empty;
        public decimal ItemRate;
        public decimal BilledQuantity;
        public decimal ItemAmount;
        public string ItemPurchaseLedger = string.Empty;
        public string Godown = string.Empty;
        public string BatchName = string.Empty;
        public decimal DiscountPercent;
        public decimal DiscountAmount;
        public string OrderNumber = string.Empty;
        public string TrackingNumber = string.Empty;
        public DateTime? ManufacturingDate;
        public DateTime? ExpiryDate;
        public string ItemDescription = string.Empty;
        public string CostCentre = string.Empty;
        public decimal FreeQuantity;
    }
}
