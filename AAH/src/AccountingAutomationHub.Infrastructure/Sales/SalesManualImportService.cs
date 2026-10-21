using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Infrastructure.Sales;

public sealed class SalesManualImportService : ISalesManualImportService
{
    private readonly FlexibleWorkbookReader _reader = new();

    public SalesManualInspection Inspect(string path, IReadOnlyDictionary<string, string>? savedMapping = null)
    {
        if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return InspectTallyVoucherXml(path);

        return ReadWorkbookSource(path, workbook => Inspect(workbook, path, savedMapping));
    }

    public SalesManualInspection Inspect(OpenXmlWorkbookData workbook, string sourcePath, IReadOnlyDictionary<string, string>? savedMapping = null)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        using var headerMappingScope = ImportHeaderMappingRuntime.BeginSource(sourcePath);
        var path = sourcePath;
        if (TryBuildTallySalesRegisterRateSummaryInspection(workbook, path, out var rateSummaryInspection))
            return rateSummaryInspection;

        var best = workbook.Worksheets.Values
            .Select(InspectSheet)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.HeaderRow)
            .FirstOrDefault();
        if (best is null || best.Score < 2)
            throw new InvalidDataException("No usable Sales header row was detected in the selected workbook.");

        var mapping = new Dictionary<string, string>(best.Mapping, StringComparer.OrdinalIgnoreCase);
        if (savedMapping is not null)
        {
            foreach (var pair in savedMapping)
                if (best.Headers.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) mapping[pair.Key] = pair.Value;
        }
        foreach (var bucket in best.RateBuckets)
            mapping[best.Headers[bucket.ColumnIndex - 1]] = string.Empty;

        return new SalesManualInspection
        {
            SourcePath = path,
            HeaderSignature = HeaderSignature(best.Headers),
            Headers = best.Headers,
            SuggestedMapping = mapping,
            SampleValues = best.SampleValues,
            IsComplete = IsComplete(mapping.Values, best.RateBuckets, best.IsB2cs),
            DetectedMode = best.DetectedMode,
            IsB2cs = best.IsB2cs,
            HasRateBuckets = best.RateBuckets.Count > 0
        };
    }

    public SalesManualImportResult Import(
        string path,
        long clientId,
        string clientGstin,
        string companyName,
        string financialYear,
        IReadOnlyDictionary<string, string> mapping,
        string gstTreatment,
        string taxColumnMode)
    {
        if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return ImportTallyVoucherXml(path, clientId, clientGstin, companyName, financialYear, gstTreatment, taxColumnMode);

        return ReadWorkbookSource(path, workbook =>
            Import(workbook, path, clientId, clientGstin, companyName, financialYear, mapping, gstTreatment, taxColumnMode));
    }

    public SalesManualImportResult Import(
        OpenXmlWorkbookData workbook,
        string sourcePath,
        long clientId,
        string clientGstin,
        string companyName,
        string financialYear,
        IReadOnlyDictionary<string, string> mapping,
        string gstTreatment,
        string taxColumnMode,
        string sourceHashOverride = "")
    {
        ArgumentNullException.ThrowIfNull(workbook);
        using var headerMappingScope = ImportHeaderMappingRuntime.BeginSource(sourcePath);
        var path = sourcePath;
        var records = new List<SalesImportRecord>();
        var issues = new List<SalesImportIssue>();
        var itemSequence = 0;
        // Auto Import already read the source once for recognition. Reuse its deterministic
        // cache hash so Sales does not reopen/read the physical file merely to hash it again.
        var sourceHash = string.IsNullOrWhiteSpace(sourceHashOverride) ? FileHash(path) : sourceHashOverride.Trim();
        var fullSourcePath = Path.GetFullPath(path);
        var sourceFile = Path.GetFileName(path);

        if (TryParseTallySalesRegisterRateMappings(workbook, out var rateMappings, out _))
        {
            var summaryRecords = rateMappings
                .Select((rateMapping, index) => BuildTallySalesRegisterSummaryRecord(
                    rateMapping, path, sourceHash, clientGstin, companyName, financialYear, index + 1))
                .ToArray();
            return new SalesManualImportResult
            {
                SourcePath = path,
                SourceHash = sourceHash,
                HeaderSignature = $"TALLY-SALES-RATE-SUMMARY-V2:{sourceHash}",
                IsTallyRateSummaryMapping = true,
                RateMappings = rateMappings,
                Records = summaryRecords,
                SalesRows = summaryRecords.Length
            };
        }

        foreach (var sheet in workbook.Worksheets.Values)
        {
            var inspection = InspectSheet(sheet);
            if (inspection.Score < 2) continue;

            var effective = new Dictionary<string, string>(inspection.Mapping, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in mapping)
                if (inspection.Headers.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) effective[pair.Key] = pair.Value;
            ApplyContextAwareHeaderRules(inspection.Headers, effective);
            foreach (var bucket in inspection.RateBuckets)
                effective[inspection.Headers[bucket.ColumnIndex - 1]] = string.Empty;
            if (!IsComplete(effective.Values, inspection.RateBuckets, inspection.IsB2cs))
            {
                issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = sheet.Name, RowNumber = inspection.HeaderRow, Message = $"Required Sales fields could not be mapped in sheet '{sheet.Name}'." });
                continue;
            }

            var columnTargets = new Dictionary<int, string>();
            for (var i = 0; i < inspection.Headers.Count; i++)
            {
                var header = inspection.Headers[i];
                if (effective.TryGetValue(header, out var target) && !string.IsNullOrWhiteSpace(target))
                    columnTargets[i + 1] = target.Trim();
            }

            string carryCustomer = string.Empty, carryGstin = string.Empty, carryDoc = string.Empty, carryPos = string.Empty;
            DateTime? carryDate = null;
            for (var rowNumber = inspection.DataStartRow; rowNumber <= sheet.MaxRow; rowNumber++)
            {
                var cells = sheet.Rows.TryGetValue(rowNumber, out var rowCells) ? rowCells : new Dictionary<int, string>();
                if (cells.Count == 0) continue;
                var rowText = string.Join(" ", cells.OrderBy(pair => pair.Key).Take(8).Select(pair => pair.Value)).Trim();
                if (rowText.Length == 0 || IsTotalRow(rowText)) continue;

                var baseRecord = new SalesImportRecord
                {
                    Destination = IsCreditSheet(sheet.Name) ? "Credit Note" : "Sales",
                    DocumentType = IsCreditSheet(sheet.Name) ? "Credit Note" : "Sales",
                    ClientGstin = NormalizeGstin(clientGstin), CompanyName = companyName.Trim(), ImportMode = "Manual Import",
                    SourceFile = sourceFile, SourcePath = fullSourcePath, SourceOrigin = "Manual Sales Register", SourceBranch = sheet.Name,
                    SourceScope = "Manual", SourceHash = sourceHash
                };

                foreach (var pair in columnTargets)
                {
                    var text = sheet.GetCell(rowNumber, pair.Key).Trim();
                    Assign(baseRecord, pair.Value, text, workbook.Uses1904DateSystem);
                }

                if (inspection.IsB2cs && inspection.MonthColumnIndex > 0)
                    baseRecord.DocumentDate = ParseMonthEnd(sheet.GetCell(rowNumber, inspection.MonthColumnIndex), workbook.Uses1904DateSystem) ?? baseRecord.DocumentDate;

                // Fix121: continuation values are voucher-scoped, not column-scoped.
                // A blank GSTIN on a NEW voucher must stay blank; it must never inherit the
                // previous customer's GSTIN merely because the next row omits TIN/GSTIN.
                // Continuation inventory lines inside the SAME voucher still inherit the
                // current voucher's document/customer/GSTIN/date as expected.
                var explicitDocumentNumber = baseRecord.DocumentNumber.Trim();
                var explicitCustomerName = baseRecord.CustomerName.Trim();
                var explicitCustomerGstin = baseRecord.CustomerGstin.Trim();
                var normalizedExplicitCustomerGstin = NormalizeGstin(explicitCustomerGstin);
                if (explicitCustomerGstin.Length > 0 && normalizedExplicitCustomerGstin.Length != 15)
                {
                    issues.Add(new SalesImportIssue
                    {
                        SourceFile = sourceFile,
                        Section = sheet.Name,
                        RowNumber = rowNumber,
                        Message = $"{(explicitDocumentNumber.Length > 0 ? explicitDocumentNumber : "Sales row")}: Source GSTIN '{explicitCustomerGstin}' is not a valid 15-character GSTIN. AAH kept this voucher GSTIN blank instead of carrying a GSTIN from another customer."
                    });
                    baseRecord.CustomerGstin = string.Empty;
                }
                var startsNewVoucher = !inspection.IsB2cs &&
                    ((explicitDocumentNumber.Length > 0 && !explicitDocumentNumber.Equals(carryDoc, StringComparison.OrdinalIgnoreCase)) ||
                     (explicitCustomerName.Length > 0 && !explicitCustomerName.Equals(carryCustomer, StringComparison.OrdinalIgnoreCase)) ||
                     (baseRecord.DocumentDate is DateTime explicitDate && carryDate is DateTime previousDate && explicitDate.Date != previousDate.Date) ||
                     (carryDoc.Length == 0 && baseRecord.DocumentDate is not null));

                if (startsNewVoucher)
                {
                    // Reset voucher-specific optional evidence even when the source cell is
                    // blank. This is the key protection against cross-voucher GSTIN/POS bleed.
                    carryGstin = baseRecord.CustomerGstin.Trim();
                    carryPos = baseRecord.PlaceOfSupply.Trim();
                }

                if (!inspection.IsB2cs)
                {
                    if (baseRecord.DocumentNumber.Length == 0) baseRecord.DocumentNumber = carryDoc;
                    else carryDoc = baseRecord.DocumentNumber;
                }
                if (baseRecord.DocumentDate is null) baseRecord.DocumentDate = carryDate;
                else carryDate = baseRecord.DocumentDate;
                if (baseRecord.CustomerName.Length == 0) baseRecord.CustomerName = carryCustomer;
                else carryCustomer = baseRecord.CustomerName;
                if (baseRecord.CustomerGstin.Length == 0) baseRecord.CustomerGstin = carryGstin;
                else carryGstin = baseRecord.CustomerGstin;
                if (baseRecord.PlaceOfSupply.Length == 0) baseRecord.PlaceOfSupply = carryPos;
                else carryPos = baseRecord.PlaceOfSupply;

                var expanded = ExpandRateBuckets(baseRecord, inspection.RateBuckets, sheet, rowNumber, gstTreatment, taxColumnMode);
                if (expanded.Count == 0) expanded = [baseRecord];

                foreach (var record in expanded)
                {
                    record.ItemSequence = ++itemSequence;
                    record.CustomerGstin = NormalizeGstin(record.CustomerGstin);
                    if (inspection.IsB2cs && record.DocumentNumber.Length == 0 && record.DocumentDate is DateTime b2csDate)
                    {
                        var stateKey = SalesManualFieldCatalog.Normalize(record.PlaceOfSupply);
                        if (stateKey.Length == 0) stateKey = "STATE";
                        record.DocumentNumber = $"B2CS-{b2csDate:yyyyMM}-{stateKey}-{FormatRate(record.Rate)}-{rowNumber}";
                    }
                    if (record.GstDate is null) record.GstDate = record.DocumentDate;
                    if (inspection.IsB2cs && record.CustomerName.Length == 0)
                        record.CustomerName = record.PlaceOfSupply.Length > 0 ? $"B2C Sales - {record.PlaceOfSupply}" : "B2C Sales";

                    record.OriginalItemName = record.ItemName;
                    record.OriginalUnit = record.Unit;
                    record.OriginalActualQuantity = record.ActualQuantity;
                    record.OriginalBilledQuantity = record.BilledQuantity;
                    record.OriginalItemRate = record.ItemRate;
                    record.OriginalItemAmount = record.ItemAmount;

                    NormalizeAmounts(record, gstTreatment, taxColumnMode);
                    if (!GstRateCatalog.TryNormalize(record.Rate, out var normalizedGstRate))
                    {
                        issues.Add(new SalesImportIssue
                        {
                            SourceFile = sourceFile,
                            Section = sheet.Name,
                            RowNumber = rowNumber,
                            Message = $"{record.DocumentNumber}: GST Rate {record.Rate:0.####}% is not allowed. Allowed rates: {string.Join(", ", GstRateCatalog.AllowedRates.Select(FormatRate))}%."
                        });
                        continue;
                    }
                    record.Rate = normalizedGstRate;
                    ApplyLedgerDefaults(record);

                    if (!HasUsableRow(record)) continue;
                    if (record.DocumentNumber.Length == 0)
                    {
                        issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = sheet.Name, RowNumber = rowNumber, Message = "Sales row has no Invoice/Note number and could not inherit one." });
                        continue;
                    }
                    if (record.DocumentDate is null)
                    {
                        issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = sheet.Name, RowNumber = rowNumber, Message = $"{record.DocumentNumber}: document date is missing." });
                        continue;
                    }
                    if (record.CustomerName.Length == 0)
                        record.CustomerName = record.CustomerGstin.Length == 15 ? record.CustomerGstin : "B2C Sales";
                    // Fix125: keep the source party text immutable so Manual Sales B2C
                    // grouping/overrides survive later customer-name edits and reimport.
                    if (record.OriginalCustomerName.Length == 0)
                        record.OriginalCustomerName = record.CustomerName;
                    records.Add(record);
                }
            }
        }

        return new SalesManualImportResult
        {
            SourcePath = path,
            SourceHash = sourceHash,
            HeaderSignature = records.Count > 0 ? Inspect(workbook, path, mapping).HeaderSignature : string.Empty,
            Records = records,
            Issues = issues,
            SalesRows = records.Count(row => !row.Destination.Contains("Credit", StringComparison.OrdinalIgnoreCase)),
            CreditNoteRows = records.Count(row => row.Destination.Contains("Credit", StringComparison.OrdinalIgnoreCase))
        };
    }

    private T ReadWorkbookSource<T>(string path, Func<OpenXmlWorkbookData, T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var effectivePath = path;
        string? convertedPath = null;
        try
        {
            if (Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase))
            {
                convertedPath = ExcelComWorkbookMaterializer.ConvertToOpenXml(path, string.Empty, 0, "SalesRateMap");
                effectivePath = convertedPath;
            }

            return action(_reader.Read(effectivePath, CancellationToken.None));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(convertedPath))
            {
                try { File.Delete(convertedPath); } catch { }
            }
        }
    }

    private static bool TryBuildTallySalesRegisterRateSummaryInspection(
        OpenXmlWorkbookData workbook,
        string sourcePath,
        out SalesManualInspection inspection)
    {
        inspection = new SalesManualInspection();
        if (!TryParseTallySalesRegisterRateMappings(workbook, out var mappings, out var periodText))
            return false;

        inspection = new SalesManualInspection
        {
            SourcePath = sourcePath,
            HeaderSignature = $"TALLY-SALES-RATE-SUMMARY-V1:{HeaderSignature([periodText, Path.GetFileName(sourcePath)])}",
            Headers = ["Date", "Voucher Range", "GST Type"],
            SuggestedMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            SampleValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Date"] = mappings.FirstOrDefault()?.SalesDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                ["Voucher Range"] = mappings.FirstOrDefault() is { } first && first.VoucherFrom > 0
                    ? $"{first.VoucherFrom} - {first.VoucherTo}"
                    : string.Empty,
                ["GST Type"] = string.Join(", ", mappings.SelectMany(item => item.Rates).Distinct().OrderBy(rate => rate).Select(rate => rate == 0m ? "Exempted" : $"{FormatRate(rate)}%"))
            },
            IsComplete = true,
            DetectedMode = "Tally Sales Register Rate Summary",
            IsTallyRateSummaryMapping = true,
            MappingSummary = $"{periodText} · {mappings.Count:N0} day/range mapping(s)"
        };
        return true;
    }

    private static bool TryParseTallySalesRegisterRateMappings(
        OpenXmlWorkbookData workbook,
        out IReadOnlyList<SalesRateSummaryMapping> mappings,
        out string periodText)
    {
        var result = new List<SalesRateSummaryMapping>();
        periodText = string.Empty;
        var titleFound = false;
        var headerEvidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in workbook.Worksheets.Values)
        {
            foreach (var rowNumber in sheet.Rows.Keys.OrderBy(number => number))
            {
                if (!sheet.Rows.TryGetValue(rowNumber, out var cells)) continue;
                var orderedCells = cells.OrderBy(pair => pair.Key).ToArray();
                var values = orderedCells.Select(pair => pair.Value.Trim()).Where(value => value.Length > 0).ToArray();
                if (values.Length == 0) continue;

                foreach (var value in values)
                {
                    var cleanValue = Clean(value);
                    if (cleanValue.Contains("Sales Register for the period from", StringComparison.OrdinalIgnoreCase))
                    {
                        titleFound = true;
                        periodText = cleanValue;
                    }

                    var normalized = SalesManualFieldCatalog.Normalize(value);
                    if (normalized == "GSTTYPE") headerEvidence.Add("GST Type");
                    if (normalized is "TAXABLE" or "TAXABLEVALUE" or "TAXABLEAMOUNT") headerEvidence.Add("Taxable");
                    if (normalized is "IGST" or "IGSTAMOUNT" or "INTEGRATEDTAX" or "INTEGRATEDTAXAMOUNT") headerEvidence.Add("IGST");
                    if (normalized is "CGST" or "CGSTAMOUNT" or "CENTRALTAX" or "CENTRALTAXAMOUNT") headerEvidence.Add("CGST");
                    if (normalized is "SGST" or "SGSTAMOUNT" or "STATETAX" or "STATEUTTAX" or "UTGST") headerEvidence.Add("SGST");
                }

                var rowText = string.Join(" ", values.Select(Clean));
                var totalMatch = Regex.Match(rowText, @"Totals\s+for\s+(?<date>\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!totalMatch.Success) continue;
                if (!DateTime.TryParseExact(totalMatch.Groups["date"].Value, ["d/M/yyyy", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var salesDate))
                    continue;

                var voucherFrom = 0;
                var voucherTo = 0;
                var rangeMatch = Regex.Match(rowText, @"(?<!\d)(?<from>\d{1,9})\s*-\s*(?<to>\d{1,9})(?!\d)", RegexOptions.CultureInvariant);
                if (rangeMatch.Success)
                {
                    _ = int.TryParse(rangeMatch.Groups["from"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out voucherFrom);
                    _ = int.TryParse(rangeMatch.Groups["to"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out voucherTo);
                }

                var rateLines = orderedCells
                    .Select(pair => SplitLines(pair.Value))
                    .Select(lines => lines.Select(line => TryParseTallyRateLabel(line, out var rate) ? (Ok: true, Rate: rate) : (Ok: false, Rate: 0m)).ToArray())
                    .FirstOrDefault(parsed => parsed.Length > 0 && parsed.All(item => item.Ok));
                if (rateLines is null || rateLines.Length == 0) continue;
                var orderedRates = rateLines.Select(item => item.Rate).ToArray();

                var numericVectors = orderedCells
                    .Select(pair => new { pair.Key, Values = ParseNumericLines(pair.Value) })
                    .Where(item => item.Values.Count == orderedRates.Length && item.Values.Count > 1)
                    .OrderBy(item => item.Key)
                    .ToArray();
                if (numericVectors.Length < 4) continue;

                // Crystal/Tally exports used by the supplied BTC statements print the daily
                // slab vectors in this order after GST Type: Taxable, IGST, CGST, SGST.
                // This is intentionally format-specific and is entered only after the title,
                // Date/Voucher Range and GST Type/Taxable/CGST/SGST evidence gates above pass.
                var taxable = numericVectors[0].Values;
                var igst = numericVectors[1].Values;
                var cgst = numericVectors[2].Values;
                var sgst = numericVectors[3].Values;

                var scalarNumbers = orderedCells
                    .Select(pair => new { pair.Key, Values = ParseNumericLines(pair.Value) })
                    .Where(item => item.Values.Count == 1)
                    .OrderBy(item => item.Key)
                    .ToArray();
                var billAmount = scalarNumbers.Length == 0 ? 0m : scalarNumbers.Max(item => Math.Abs(item.Values[0]));
                var otherAmount = scalarNumbers
                    .Where(item => Math.Abs(item.Values[0]) < billAmount - 0.005m)
                    .Select(item => item.Values[0])
                    .DefaultIfEmpty(0m)
                    .Sum();

                var buckets = new List<SalesRateSummaryBucket>(orderedRates.Length);
                for (var index = 0; index < orderedRates.Length; index++)
                {
                    buckets.Add(new SalesRateSummaryBucket
                    {
                        Rate = orderedRates[index],
                        TaxableValue = taxable[index],
                        Igst = igst[index],
                        Cgst = cgst[index],
                        Sgst = sgst[index]
                    });
                }

                result.Add(new SalesRateSummaryMapping
                {
                    SalesDate = salesDate.Date,
                    VoucherFrom = voucherFrom,
                    VoucherTo = voucherTo,
                    Rates = orderedRates.Distinct().OrderBy(rate => rate).ToArray(),
                    TaxableValue = buckets.Sum(bucket => bucket.TaxableValue),
                    Igst = buckets.Sum(bucket => bucket.Igst),
                    Cgst = buckets.Sum(bucket => bucket.Cgst),
                    Sgst = buckets.Sum(bucket => bucket.Sgst),
                    OtherAmount = otherAmount,
                    BillAmount = billAmount,
                    Buckets = buckets
                });
            }
        }

        mappings = result
            .GroupBy(item => new { Date = item.SalesDate.Date, item.VoucherFrom, item.VoucherTo })
            .Select(group => group.OrderByDescending(item => item.BillAmount).First())
            .OrderBy(item => item.SalesDate)
            .ThenBy(item => item.VoucherFrom)
            .ToArray();

        return titleFound &&
               headerEvidence.Contains("GST Type") &&
               headerEvidence.Contains("Taxable") &&
               headerEvidence.Contains("CGST") &&
               headerEvidence.Contains("SGST") &&
               mappings.Count > 0 &&
               mappings.All(mapping => mapping.Buckets.Count > 0 && mapping.TaxableValue >= 0m);
    }

    private static string[] SplitLines(string value) =>
        (value ?? string.Empty)
        .Replace('\r', '\n')
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<decimal> ParseNumericLines(string value)
    {
        var lines = SplitLines(value);
        if (lines.Length == 0) return [];
        var numbers = new List<decimal>(lines.Length);
        foreach (var line in lines)
        {
            var cleaned = line.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
            if (!decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                return [];
            numbers.Add(number);
        }
        return numbers;
    }

    private static SalesImportRecord BuildTallySalesRegisterSummaryRecord(
        SalesRateSummaryMapping mapping,
        string sourcePath,
        string sourceHash,
        string clientGstin,
        string companyName,
        string financialYear,
        int sequence)
    {
        var taxable = mapping.TaxableValue;
        var totalTax = mapping.Igst + mapping.Cgst + mapping.Sgst;
        var effectiveRate = taxable > 0.005m
            ? decimal.Round(totalTax * 100m / taxable, 4, MidpointRounding.AwayFromZero)
            : 0m;
        var slabEvidence = string.Join("; ", mapping.Buckets.Select(bucket =>
            $"{FormatRate(bucket.Rate)}%: taxable {bucket.TaxableValue:0.00}, IGST {bucket.Igst:0.00}, CGST {bucket.Cgst:0.00}, SGST {bucket.Sgst:0.00}"));
        var voucherRange = mapping.VoucherFrom > 0 && mapping.VoucherTo >= mapping.VoucherFrom
            ? $"{mapping.VoucherFrom}-{mapping.VoucherTo}"
            : mapping.SalesDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        return new SalesImportRecord
        {
            Destination = "Sales",
            DocumentType = "Sales Register Summary",
            DocumentNumber = $"Vouchers {voucherRange}",
            DocumentDate = mapping.SalesDate.Date,
            GstDate = mapping.SalesDate.Date,
            SourceDocumentValue = mapping.BillAmount,
            DocumentValue = mapping.BillAmount,
            Rate = effectiveRate,
            TaxableValue = mapping.TaxableValue,
            Igst = mapping.Igst,
            Cgst = mapping.Cgst,
            Sgst = mapping.Sgst,
            IsEffectiveMixedGstRate = mapping.Rates.Count(rate => rate > 0m) > 1,
            GstRateEvidence = $"Tally Sales Register daily summary; voucher range {voucherRange}; slabs: {slabEvidence}; printed bill amount {mapping.BillAmount:0.00}; other amount {mapping.OtherAmount:0.00}",
            SourceSection = "TALLY_SALES_REGISTER_SUMMARY",
            ReturnPeriod = mapping.SalesDate.ToString("MM-yyyy", CultureInfo.InvariantCulture),
            SourceFile = Path.GetFileName(sourcePath),
            SourcePath = Path.GetFullPath(sourcePath),
            SourceOrigin = "Tally Sales Register Summary",
            SourceBranch = "Crystal/Tally Sales Register",
            SourceScope = "Daily voucher-range summary; non-exportable",
            SourceHash = sourceHash,
            ClientGstin = NormalizeGstin(clientGstin),
            CompanyName = companyName.Trim(),
            ImportMode = "Statement Summary",
            ItemSequence = sequence,
            Narration = $"Tally Sales Register summary {mapping.SalesDate:dd-MM-yyyy} vouchers {voucherRange}"
        };
    }

    private static bool TryParseTallyRateLabel(string value, out decimal rate)
    {
        rate = 0m;
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith("Exempt", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Nil", StringComparison.OrdinalIgnoreCase))
            return true;
        var match = Regex.Match(text, @"^(?<rate>\d+(?:\.\d+)?)\s*%\s*(?:Goods|Services)?\s*%?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && decimal.TryParse(match.Groups["rate"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out rate);
    }

    private static bool TryDeriveEffectiveGstRate(SalesImportRecord record, out decimal rate)
    {
        rate = 0m;
        var taxable = Math.Abs(record.TaxableValue);
        var totalTax = Math.Abs(record.Igst) + Math.Abs(record.Cgst) + Math.Abs(record.Sgst);
        if (taxable <= 0.005m || totalTax <= 0.005m) return false;
        rate = decimal.Round(totalTax * 100m / taxable, 4, MidpointRounding.AwayFromZero);
        return rate > 0m;
    }

    private static SalesManualInspection InspectTallyVoucherXml(string path)
    {
        var document = LoadTallyVoucherXml(path);
        var vouchers = Elements(document, "VOUCHER")
            .Where(IsSupportedSalesVoucher)
            .Take(2)
            .ToArray();
        if (vouchers.Length == 0)
            throw new InvalidDataException("The XML is not a supported Tally Sales/Credit Note voucher import. AAH expects an ENVELOPE with ID=Vouchers and Sales or Credit Note VOUCHER objects.");

        return new SalesManualInspection
        {
            SourcePath = path,
            HeaderSignature = "TALLY-VOUCHER-XML-SALES-V1",
            Headers = ["Tally Voucher XML"],
            SuggestedMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            SampleValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Format"] = "Tally Voucher XML",
                ["VoucherType"] = Value(vouchers[0], "VOUCHERTYPENAME"),
                ["VoucherNumber"] = Value(vouchers[0], "VOUCHERNUMBER")
            },
            IsComplete = true,
            DetectedMode = "Tally Voucher XML",
            IsB2cs = false,
            HasRateBuckets = false
        };
    }

    private static SalesManualImportResult ImportTallyVoucherXml(
        string path,
        long clientId,
        string clientGstin,
        string companyName,
        string financialYear,
        string gstTreatment,
        string taxColumnMode)
    {
        var document = LoadTallyVoucherXml(path);
        var sourceHash = FileHash(path);
        var fullSourcePath = Path.GetFullPath(path);
        var sourceFile = Path.GetFileName(path);
        var issues = new List<SalesImportIssue>();
        var records = new List<SalesImportRecord>();
        var ledgers = ReadTallyLedgerEvidence(document);
        var itemSequence = 0;
        var voucherIndex = 0;
        var unresolvedGstRateCount = 0;

        foreach (var voucher in Elements(document, "VOUCHER"))
        {
            voucherIndex++;
            if (!IsSupportedSalesVoucher(voucher)) continue;

            var voucherType = Value(voucher, "VOUCHERTYPENAME").Trim();
            var destination = voucherType.Contains("credit", StringComparison.OrdinalIgnoreCase) ? "Credit Note" : "Sales";
            var documentNumber = Value(voucher, "VOUCHERNUMBER").Trim();
            if (documentNumber.Length == 0) documentNumber = Value(voucher, "REFERENCE").Trim();
            var dateText = Value(voucher, "DATE").Trim();
            var documentDate = ParseTallyDate(dateText);
            var entries = voucher.Elements()
                .Where(element => element.Name.LocalName.Equals("ALLLEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase))
                .Select(ReadTallyLedgerEntry)
                .Where(entry => entry.Name.Length > 0)
                .ToArray();

            if (documentNumber.Length == 0)
            {
                issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = voucherType, RowNumber = voucherIndex, Message = "Tally voucher has no VOUCHERNUMBER/REFERENCE and was not imported." });
                continue;
            }
            if (documentDate is null)
            {
                issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = voucherType, RowNumber = voucherIndex, Message = $"Voucher {documentNumber}: DATE '{dateText}' is missing or invalid." });
                continue;
            }
            if (!IsInsideFinancialYear(documentDate.Value, financialYear))
            {
                issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = voucherType, RowNumber = voucherIndex, Message = $"Voucher {documentNumber}: date {documentDate:dd-MMM-yyyy} is outside selected FY {financialYear}." });
                continue;
            }
            if (entries.Length < 2)
            {
                issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = voucherType, RowNumber = voucherIndex, Message = $"Voucher {documentNumber}: fewer than two accounting ledger entries were found." });
                continue;
            }

            var signedBalance = entries.Sum(entry => entry.Amount);
            if (Math.Abs(signedBalance) > 0.05m)
            {
                issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = voucherType, RowNumber = voucherIndex, Message = $"Voucher {documentNumber}: ledger entries are not balanced (difference {signedBalance:0.00})." });
                continue;
            }

            var party = (destination.Equals("Credit Note", StringComparison.OrdinalIgnoreCase)
                    ? entries.Where(entry => !entry.IsDeemedPositive || entry.Amount > 0.005m)
                    : entries.Where(entry => entry.IsDeemedPositive || entry.Amount < -0.005m))
                .OrderByDescending(entry => Math.Abs(entry.Amount))
                .FirstOrDefault();
            if (party is null)
            {
                issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = voucherType, RowNumber = voucherIndex, Message = $"Voucher {documentNumber}: AAH could not identify the party/Cash ledger from the debit side." });
                continue;
            }

            var nonParty = entries.Where(entry => !ReferenceEquals(entry, party)).ToArray();
            var cgstEntries = nonParty.Where(entry => IsCentralTaxLedger(entry.Name)).ToArray();
            var sgstEntries = nonParty.Where(entry => IsStateTaxLedger(entry.Name)).ToArray();
            var igstEntries = nonParty.Where(entry => IsIntegratedTaxLedger(entry.Name)).ToArray();
            var cessEntries = nonParty.Where(entry => IsCessLedger(entry.Name)).ToArray();
            var roundingEntries = nonParty.Where(entry => IsRoundingLedger(entry.Name)).ToArray();
            var businessEntries = nonParty
                .Where(entry => !IsTaxLedger(entry.Name) && !IsRoundingLedger(entry.Name))
                .ToArray();

            var taxableValue = businessEntries.Sum(entry => Math.Abs(entry.Amount));
            var documentValue = Math.Abs(party.Amount);
            if (taxableValue <= 0.005m || documentValue <= 0.005m)
            {
                issues.Add(new SalesImportIssue { SourceFile = sourceFile, Section = voucherType, RowNumber = voucherIndex, Message = $"Voucher {documentNumber}: taxable/accounting value or party total is zero." });
                continue;
            }

            var partyName = party.Name.Equals("CASH", StringComparison.OrdinalIgnoreCase) ? "Cash" : party.Name;
            ledgers.TryGetValue(TallyLedgerKey(partyName), out var partyEvidence);
            var record = new SalesImportRecord
            {
                Destination = destination,
                DocumentType = destination,
                DocumentNumber = documentNumber,
                DocumentDate = documentDate,
                GstDate = documentDate,
                CustomerName = partyName,
                OriginalCustomerName = partyName,
                CustomerNameSource = "Tally Voucher XML",
                CustomerGstin = partyEvidence?.Gstin ?? string.Empty,
                PartyState = partyEvidence?.State ?? string.Empty,
                PlaceOfSupply = partyEvidence?.State ?? string.Empty,
                PartyAddressLine1 = partyEvidence?.Address1 ?? string.Empty,
                PartyAddressLine2 = partyEvidence?.Address2 ?? string.Empty,
                PartyAddressLine3 = partyEvidence?.Address3 ?? string.Empty,
                SourceDocumentValue = documentValue,
                DocumentValue = documentValue,
                TaxableValue = taxableValue,
                AccountingAmount = taxableValue,
                Cgst = cgstEntries.Sum(entry => Math.Abs(entry.Amount)),
                Sgst = sgstEntries.Sum(entry => Math.Abs(entry.Amount)),
                Igst = igstEntries.Sum(entry => Math.Abs(entry.Amount)),
                Cess = cessEntries.Sum(entry => Math.Abs(entry.Amount)),
                RoundOff = roundingEntries.Sum(entry => entry.Amount),
                SalesLedger = businessEntries.FirstOrDefault()?.Name ?? string.Empty,
                CgstLedger = cgstEntries.FirstOrDefault()?.Name ?? string.Empty,
                SgstLedger = sgstEntries.FirstOrDefault()?.Name ?? string.Empty,
                IgstLedger = igstEntries.FirstOrDefault()?.Name ?? string.Empty,
                Narration = Value(voucher, "NARRATION").Trim(),
                ClientGstin = NormalizeGstin(clientGstin),
                CompanyName = companyName.Trim(),
                ImportMode = "Manual Import",
                SourceFile = sourceFile,
                SourcePath = fullSourcePath,
                SourceOrigin = "Tally Voucher XML",
                SourceBranch = voucherType,
                SourceSection = "Tally Voucher XML",
                SourceScope = "Manual",
                SourceHash = sourceHash,
                SalesMasterFile = sourceFile,
                ItemSequence = ++itemSequence
            };

            // Tally XML already contains the actual ledger-side GST amounts. Do not rewrite
            // IGST into CGST/SGST (or the reverse) from a saved spreadsheet GST-treatment setting.
            NormalizeAmounts(record, string.Empty, taxColumnMode);
            var xmlTaxTotal = Math.Abs(record.Cgst) + Math.Abs(record.Sgst) + Math.Abs(record.Igst);
            if (xmlTaxTotal > 0.005m &&
                GstRateCatalog.TryDeriveFromTaxAmounts(record.TaxableValue, record.Igst, record.Cgst, record.Sgst, out var provedXmlRate))
            {
                record.Rate = provedXmlRate;
                record.IsEffectiveMixedGstRate = false;
                record.GstRateEvidence = "GST rate proved from exact Tally XML tax ledgers.";
            }
            else if (xmlTaxTotal > 0.005m && TryDeriveEffectiveGstRate(record, out var effectiveXmlRate))
            {
                record.Rate = effectiveXmlRate;
                record.IsEffectiveMixedGstRate = true;
                record.GstRateEvidence = "Weighted GST rate derived from exact Tally XML tax ledgers; companion Sales Register can confirm the day/range slab mix.";
                unresolvedGstRateCount++;
            }
            else if (GstRateCatalog.TryNormalize(record.Rate, out var normalizedRate))
            {
                record.Rate = normalizedRate;
            }
            ApplyLedgerDefaults(record);
            records.Add(record);
        }

        if (records.Count == 0)
            throw new InvalidDataException(issues.Count > 0
                ? $"No Tally Sales/Credit Note voucher could be imported. First issue: {issues[0].Message}"
                : "No Sales/Credit Note VOUCHER objects were found in the Tally XML.");
        if (unresolvedGstRateCount > 0)
            issues.Add(new SalesImportIssue
            {
                SourceFile = sourceFile,
                Section = "Tally Voucher XML",
                RowNumber = 0,
                Message = $"{unresolvedGstRateCount:N0} voucher(s) contain more than one GST slab or otherwise do not prove one statutory slab from the ledger-only XML. AAH retained the exact tax amounts/ledger names and shows a weighted effective Rate for review; a Tally Sales Register rate-summary sheet can be mapped as companion evidence without creating duplicate vouchers."
            });

        return new SalesManualImportResult
        {
            SourcePath = path,
            SourceHash = sourceHash,
            HeaderSignature = $"TALLY-VOUCHER-XML-SALES-V1:{sourceHash}",
            Records = records,
            Issues = issues,
            SalesRows = records.Count(record => record.Destination.Equals("Sales", StringComparison.OrdinalIgnoreCase)),
            CreditNoteRows = records.Count(record => record.Destination.Equals("Credit Note", StringComparison.OrdinalIgnoreCase))
        };
    }

    private static XDocument LoadTallyVoucherXml(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The selected Tally XML was not found.", path);
        var document = XDocument.Load(path, LoadOptions.None);
        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tally voucher XML root must be ENVELOPE.");
        var id = Elements(document, "ID").FirstOrDefault()?.Value.Trim() ?? string.Empty;
        if (!id.Equals("Vouchers", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tally XML is not a Vouchers import (HEADER/ID must be Vouchers).");
        return document;
    }

    private static IEnumerable<XElement> Elements(XContainer container, string localName) =>
        container.Descendants().Where(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static bool IsSupportedSalesVoucher(XElement voucher)
    {
        var type = Value(voucher, "VOUCHERTYPENAME").Trim();
        return type.Equals("Sales", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("Credit Note", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("CreditNote", StringComparison.OrdinalIgnoreCase);
    }

    private static string Value(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    private static DateTime? ParseTallyDate(string value) =>
        DateTime.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.Date
            : null;

    private static bool IsInsideFinancialYear(DateTime date, string financialYear)
    {
        var match = Regex.Match(financialYear ?? string.Empty, @"(?<!\d)(\d{4})\D+(\d{2,4})(?!\d)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var startYear)) return true;
        var start = new DateTime(startYear, 4, 1);
        var end = new DateTime(startYear + 1, 3, 31);
        return date.Date >= start && date.Date <= end;
    }

    private static TallySalesLedgerEntry ReadTallyLedgerEntry(XElement element)
    {
        var name = Value(element, "LEDGERNAME").Trim();
        var amountText = Value(element, "AMOUNT").Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        decimal.TryParse(amountText, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount);
        var deemed = Value(element, "ISDEEMEDPOSITIVE").Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
        return new TallySalesLedgerEntry(name, amount, deemed);
    }

    private static Dictionary<string, TallySalesLedgerEvidence> ReadTallyLedgerEvidence(XDocument document)
    {
        var result = new Dictionary<string, TallySalesLedgerEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in Elements(document, "LEDGER"))
        {
            var nameList = ledger.Elements().FirstOrDefault(child => child.Name.LocalName.Equals("NAME.LIST", StringComparison.OrdinalIgnoreCase));
            var name = ledger.Attribute("NAME")?.Value.Trim() ?? string.Empty;
            if (name.Length == 0)
                name = ledger.Elements().FirstOrDefault(child => child.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
            if (name.Length == 0)
                name = nameList?.Elements().FirstOrDefault(child => child.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
            if (name.Length == 0) continue;
            var addresses = ledger.Elements().FirstOrDefault(child => child.Name.LocalName.Equals("ADDRESS.LIST", StringComparison.OrdinalIgnoreCase))?
                .Elements().Where(child => child.Name.LocalName.Equals("ADDRESS", StringComparison.OrdinalIgnoreCase))
                .Select(child => child.Value.Trim()).Where(text => text.Length > 0).Take(3).ToArray() ?? [];
            result[TallyLedgerKey(name)] = new TallySalesLedgerEvidence(
                NormalizeGstin(Value(ledger, "PARTYGSTIN")),
                Value(ledger, "STATENAME").Trim(),
                addresses.ElementAtOrDefault(0) ?? string.Empty,
                addresses.ElementAtOrDefault(1) ?? string.Empty,
                addresses.ElementAtOrDefault(2) ?? string.Empty);
        }
        return result;
    }

    private static string TallyLedgerKey(string value) => AccountingText.DuplicateKey(value);
    private static bool IsCentralTaxLedger(string name) { var key = name.ToUpperInvariant(); return key.Contains("CGST") || key.Contains("CENTRAL TAX"); }
    private static bool IsStateTaxLedger(string name) { var key = name.ToUpperInvariant(); return key.Contains("SGST") || key.Contains("STATE TAX"); }
    private static bool IsIntegratedTaxLedger(string name) { var key = name.ToUpperInvariant(); return key.Contains("IGST") || key.Contains("INTEGRATED TAX"); }
    private static bool IsCessLedger(string name) => name.Contains("CESS", StringComparison.OrdinalIgnoreCase);
    private static bool IsTaxLedger(string name) => IsCentralTaxLedger(name) || IsStateTaxLedger(name) || IsIntegratedTaxLedger(name) || IsCessLedger(name);
    private static bool IsRoundingLedger(string name)
    {
        var key = name.Replace("-", " ", StringComparison.Ordinal).ToUpperInvariant();
        return key.Contains("ROUNDING") || key.Contains("ROUND OFF") || key.Contains("ROUNDOFF");
    }

    private sealed record TallySalesLedgerEntry(string Name, decimal Amount, bool IsDeemedPositive);
    private sealed record TallySalesLedgerEvidence(string Gstin, string State, string Address1, string Address2, string Address3);

    private static void NormalizeAmounts(SalesImportRecord record, string gstTreatment, string taxColumnMode)
    {
        if (record.BilledQuantity == 0m && record.ActualQuantity != 0m) record.BilledQuantity = record.ActualQuantity;
        if (record.ActualQuantity == 0m && record.BilledQuantity != 0m) record.ActualQuantity = record.BilledQuantity;

        // Fix93: Item Rate is the taxable unit rate for mapped Sales Registers.
        // When quantity + taxable value are available they are authoritative:
        // Item Rate = Taxable Value / Quantity.
        var itemRateQuantity = record.ActualQuantity != 0m ? record.ActualQuantity : record.BilledQuantity;
        if (itemRateQuantity != 0m && record.TaxableValue != 0m)
            record.ItemRate = Math.Round(record.TaxableValue / itemRateQuantity, 6, MidpointRounding.AwayFromZero);

        if (record.ItemAmount == 0m && record.TaxableValue != 0m) record.ItemAmount = record.TaxableValue;
        if (record.ItemAmount == 0m && record.ActualQuantity != 0m && record.ItemRate != 0m)
            record.ItemAmount = Math.Round(record.ActualQuantity * record.ItemRate - record.DiscountAmount, 2, MidpointRounding.AwayFromZero);
        if (record.TaxableValue == 0m && record.ItemAmount != 0m) record.TaxableValue = record.ItemAmount;
        if (record.AccountingAmount == 0m) record.AccountingAmount = record.ItemAmount;
        if (record.ItemRate == 0m && record.BilledQuantity != 0m && record.ItemAmount != 0m)
            record.ItemRate = Math.Round(record.ItemAmount / record.BilledQuantity, 6, MidpointRounding.AwayFromZero);
        if (record.BatchActualQuantity == 0m) record.BatchActualQuantity = record.ActualQuantity;
        if (record.BatchBilledQuantity == 0m) record.BatchBilledQuantity = record.BilledQuantity;
        if (record.BatchAmount == 0m) record.BatchAmount = record.ItemAmount;

        var gstMode = (gstTreatment ?? string.Empty).Trim().ToUpperInvariant();
        if (gstMode.StartsWith("LOCAL") && record.Igst != 0m && record.Cgst == 0m && record.Sgst == 0m)
        {
            record.Cgst = Math.Round(record.Igst / 2m, 2, MidpointRounding.AwayFromZero);
            record.Sgst = record.Igst - record.Cgst;
            record.Igst = 0m;
        }
        else if (gstMode.StartsWith("INTER") && record.Igst == 0m && (record.Cgst != 0m || record.Sgst != 0m))
        {
            record.Igst = record.Cgst + record.Sgst;
            record.Cgst = 0m; record.Sgst = 0m;
        }

        // Fix13 Rate=0 fallback retained. Keep the inherited PurchaseImportUtilities proof path
        // for the legacy GST-rate catalogue so cumulative Fix13 Repair 2 compile/caller safety
        // remains real behavior rather than a marker-only compatibility shim.
        var hasDerivedGstRate = PurchaseImportUtilities.TryDeriveSupportedRate(
            record.TaxableValue, record.Igst, record.Cgst, record.Sgst, out var derivedGstRate);

        // Fix93 GST-rate authority. Sales additionally supports the 40% rate. If the legacy
        // helper cannot prove the rate, the Sales catalogue gets one second proof attempt.
        if (!hasDerivedGstRate)
        {
            hasDerivedGstRate = GstRateCatalog.TryDeriveFromTaxAmounts(
                record.TaxableValue, record.Igst, record.Cgst, record.Sgst, out derivedGstRate);
        }

        // When tax arithmetic proves a supported rate, use that proof if the mapped
        // source Rate is blank, invalid, or conflicts with the tax amounts.
        if (hasDerivedGstRate &&
            (!GstRateCatalog.TryNormalize(record.Rate, out var mappedGstRate) ||
             Math.Abs(mappedGstRate - derivedGstRate) > 0.005m))
        {
            record.Rate = derivedGstRate;
        }

        if (record.DocumentValue == 0m)
            record.DocumentValue = record.TaxableValue + record.Igst + record.Cgst + record.Sgst + record.Cess + record.RoundOff;
        if (record.SourceDocumentValue == 0m) record.SourceDocumentValue = record.DocumentValue;
        if (record.RoundOff == 0m && record.DocumentValue != 0m)
        {
            var calculated = record.TaxableValue + record.Igst + record.Cgst + record.Sgst + record.Cess;
            record.RoundOff = Math.Round(record.DocumentValue - calculated, 2, MidpointRounding.AwayFromZero);
        }
    }

    private static void ApplyLedgerDefaults(SalesImportRecord record)
    {
        // A weighted/effective mixed rate is not a statutory GST slab and must never
        // be used to fabricate ledgers such as Output CGST @ 2.05%. Tally XML already
        // supplies exact ledger names; these generic fallbacks are only for genuinely
        // missing names.
        if (record.IsEffectiveMixedGstRate)
        {
            if (record.SalesLedger.Length == 0) record.SalesLedger = "Sales";
            if (record.ItemSalesLedger.Length == 0) record.ItemSalesLedger = record.SalesLedger;
            if (record.IgstLedger.Length == 0 && Math.Abs(record.Igst) > 0.005m) record.IgstLedger = "Output IGST";
            if (record.CgstLedger.Length == 0 && Math.Abs(record.Cgst) > 0.005m) record.CgstLedger = "Output CGST";
            if (record.SgstLedger.Length == 0 && Math.Abs(record.Sgst) > 0.005m) record.SgstLedger = "Output SGST";
            return;
        }

        var rateText = FormatRate(record.Rate);
        if (record.SalesLedger.Length == 0)
            record.SalesLedger = record.Rate == 0m ? "Exempted Sales" : $"Sales @ {rateText}%";
        if (record.ItemSalesLedger.Length == 0) record.ItemSalesLedger = record.SalesLedger;
        if (Math.Abs(record.Igst) > 0.005m)
        {
            if (record.IgstLedger.Length == 0) record.IgstLedger = $"Output IGST @ {rateText}%";
        }
        else if (record.Rate != 0m)
        {
            var half = FormatRate(record.Rate / 2m);
            if (record.CgstLedger.Length == 0 && Math.Abs(record.Cgst) > 0.005m) record.CgstLedger = $"Output CGST @ {half}%";
            if (record.SgstLedger.Length == 0 && Math.Abs(record.Sgst) > 0.005m) record.SgstLedger = $"Output SGST @ {half}%";
        }
    }

    private static bool IsCreditSheet(string sheetName) =>
        sheetName.Contains("credit", StringComparison.OrdinalIgnoreCase) ||
        sheetName.Contains("cdn", StringComparison.OrdinalIgnoreCase) ||
        sheetName.Contains("cr note", StringComparison.OrdinalIgnoreCase);

    private static void ApplyContextAwareHeaderRules(
        IReadOnlyList<string> headers,
        IDictionary<string, string> mapping)
    {
        var normalizedHeaders = headers
            .Select(SalesManualFieldCatalog.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var quantityTaxableLayout =
            normalizedHeaders.Any(key => key is "QTY" or "QUANTITY" or "ACTUALQTY" or "ACTUALQUANTITY") &&
            normalizedHeaders.Any(key => key is "TAXABLE" or "TAXABLEVALUE" or "TAXABLEAMOUNT");
        var inventoryMode = mapping.Values.Contains("ItemName", StringComparer.OrdinalIgnoreCase) ||
                            normalizedHeaders.Any(key => key is "ITEMNAME" or "STOCKITEM" or "PRODUCTNAME") ||
                            quantityTaxableLayout;
        if (!inventoryMode) return;

        var hasSeparatedTaxAmountColumns =
            normalizedHeaders.Contains("INTEGRATEDTAX") || normalizedHeaders.Contains("INTEGRATEDTAXAMOUNT") ||
            normalizedHeaders.Contains("CENTRALTAX") || normalizedHeaders.Contains("CENTRALTAXAMOUNT") ||
            normalizedHeaders.Contains("STATEUTTAX") || normalizedHeaders.Contains("STATETAX");
        var hasSalesLedgerColumn = normalizedHeaders.Contains("SALES") || normalizedHeaders.Contains("SALESLEDGER");
        var hasExplicitItemRateColumn = normalizedHeaders.Contains("ITEMRATE") || normalizedHeaders.Contains("UNITRATE") || normalizedHeaders.Contains("RATEUNIT");
        var hasExplicitItemAmountColumn = normalizedHeaders.Contains("ITEMAMOUNT") || normalizedHeaders.Contains("LINEAMOUNT") || normalizedHeaders.Contains("ITEMVALUE") || normalizedHeaders.Contains("LINEVALUE");

        foreach (var header in headers)
        {
            var key = SalesManualFieldCatalog.Normalize(header);
            // Legacy inventory sheets sometimes call Item Rate simply "Rate". But when
            // the workbook already has an explicit Item Rate column, bare Rate/Rate(%)
            // remains the GST Rate. The same principle applies to Amount/Value.
            if (!hasExplicitItemRateColumn && (key is "RATE" or "PRICE")) mapping[header] = "ItemRate";
            else if (!hasExplicitItemAmountColumn && (key is "AMOUNT" or "VALUE")) mapping[header] = "ItemAmount";
            // Linked accounting workbook layout: H/I/J already hold the tax amounts and
            // M/N/O/P are Sales/IGST/CGST/SGST ledger-name columns. A bare IGST/CGST/SGST
            // in that layout is therefore a Ledger, not a second tax-amount column.
            else if (hasSalesLedgerColumn && hasSeparatedTaxAmountColumns && key == "IGST") mapping[header] = "IgstLedger";
            else if (hasSalesLedgerColumn && hasSeparatedTaxAmountColumns && key == "CGST") mapping[header] = "CgstLedger";
            else if (hasSalesLedgerColumn && hasSeparatedTaxAmountColumns && key == "SGST") mapping[header] = "SgstLedger";
        }
    }

    private static IReadOnlyList<RateBucketColumn> DetectRateBuckets(IReadOnlyList<string> headers)
    {
        var result = new List<RateBucketColumn>();
        for (var i = 0; i < headers.Count; i++)
            if (TryParseRateBucketHeader(headers[i], out var rate, out var kind))
                result.Add(new RateBucketColumn(i + 1, rate, kind));
        return result;
    }

    private static bool TryParseRateBucketHeader(string header, out decimal rate, out string kind)
    {
        rate = 0m;
        kind = string.Empty;
        if (string.IsNullOrWhiteSpace(header)) return false;

        var match = Regex.Match(header, @"(?:(?:GST|TAX)\s*@?\s*)?(?<rate>\d+(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (!match.Success || !decimal.TryParse(match.Groups["rate"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out rate))
            return false;

        var descriptor = header.Contains('>') ? header[(header.LastIndexOf('>') + 1)..] : header;
        var key = SalesManualFieldCatalog.Normalize(descriptor);
        if (key.Contains("TAXABLE", StringComparison.OrdinalIgnoreCase) || key == "VALUE" || key.EndsWith("VALUE", StringComparison.OrdinalIgnoreCase))
            kind = "TAXABLE";
        else if (key.Contains("IGST", StringComparison.OrdinalIgnoreCase) || key.Contains("INTEGRATEDTAX", StringComparison.OrdinalIgnoreCase))
            kind = "IGST";
        else if (key.Contains("CGST", StringComparison.OrdinalIgnoreCase) || key.Contains("CENTRALTAX", StringComparison.OrdinalIgnoreCase))
            kind = "CGST";
        else if (key.Contains("SGST", StringComparison.OrdinalIgnoreCase) || key.Contains("STATETAX", StringComparison.OrdinalIgnoreCase) || key.Contains("UTGST", StringComparison.OrdinalIgnoreCase))
            kind = "SGST";
        else if (key.Contains("GSTAMT", StringComparison.OrdinalIgnoreCase) || key.Contains("GSTAMOUNT", StringComparison.OrdinalIgnoreCase) ||
                 key.Contains("TAXAMT", StringComparison.OrdinalIgnoreCase) || key.Contains("TAXAMOUNT", StringComparison.OrdinalIgnoreCase) || key == "GST")
            kind = "TAX";
        else
            return false;
        return true;
    }

    private static bool IsB2csSheet(
        string sheetName,
        IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, string> mapping)
    {
        if (sheetName.Contains("B2CS", StringComparison.OrdinalIgnoreCase)) return true;
        var normalized = headers.Select(SalesManualFieldCatalog.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normalized.Contains("MONTH") &&
               mapping.Values.Contains("Rate", StringComparer.OrdinalIgnoreCase) &&
               mapping.Values.Contains("TaxableValue", StringComparer.OrdinalIgnoreCase) &&
               mapping.Values.Contains("PlaceOfSupply", StringComparer.OrdinalIgnoreCase);
    }

    private static int FindHeaderColumn(IReadOnlyList<string> headers, string normalizedHeader)
    {
        for (var i = 0; i < headers.Count; i++)
            if (SalesManualFieldCatalog.Normalize(headers[i]).Equals(normalizedHeader, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        return 0;
    }

    private static IReadOnlyList<SalesImportRecord> ExpandRateBuckets(
        SalesImportRecord source,
        IReadOnlyList<RateBucketColumn> buckets,
        OpenXmlWorksheetData sheet,
        int rowNumber,
        string gstTreatment,
        string taxColumnMode)
    {
        if (buckets.Count == 0) return [];
        var result = new List<SalesImportRecord>();
        var groups = buckets.GroupBy(bucket => bucket.Rate).OrderBy(group => group.Key).ToArray();
        var sourceDocumentValue = source.DocumentValue;
        var sourceCess = source.Cess;
        var firstOutput = true;
        foreach (var group in groups)
        {
            decimal taxable = 0m, igst = 0m, cgst = 0m, sgst = 0m;
            var genericTaxes = new List<decimal>();
            foreach (var bucket in group)
            {
                var amount = ParseDecimal(sheet.GetCell(rowNumber, bucket.ColumnIndex));
                switch (bucket.Kind)
                {
                    case "TAXABLE": taxable += amount; break;
                    case "IGST": igst += amount; break;
                    case "CGST": cgst += amount; break;
                    case "SGST": sgst += amount; break;
                    case "TAX": genericTaxes.Add(amount); break;
                }
            }
            if (Math.Abs(taxable) <= 0.005m && Math.Abs(igst) <= 0.005m && Math.Abs(cgst) <= 0.005m && Math.Abs(sgst) <= 0.005m && genericTaxes.All(value => Math.Abs(value) <= 0.005m))
                continue;

            var record = CloneRecord(source);
            record.Rate = group.Key;
            record.TaxableValue = taxable;
            record.Igst = igst;
            record.Cgst = cgst;
            record.Sgst = sgst;
            if (record.ItemName.Length == 0)
            {
                record.ItemAmount = 0m;
                record.AccountingAmount = 0m;
            }

            var gstMode = (gstTreatment ?? string.Empty).Trim().ToUpperInvariant();
            var doubleTax = (taxColumnMode ?? string.Empty).Trim().ToUpperInvariant().StartsWith("DOUBLE");
            var genericTotal = genericTaxes.Sum();
            if (genericTaxes.Count > 0)
            {
                if (gstMode.StartsWith("INTER"))
                {
                    record.Igst += genericTotal;
                }
                else if (doubleTax && genericTaxes.Count >= 2)
                {
                    record.Cgst += genericTaxes[0];
                    record.Sgst += genericTaxes[1];
                    if (genericTaxes.Count > 2)
                    {
                        var remainder = genericTaxes.Skip(2).Sum();
                        var half = Math.Round(remainder / 2m, 2, MidpointRounding.AwayFromZero);
                        record.Cgst += half;
                        record.Sgst += remainder - half;
                    }
                }
                else
                {
                    var half = Math.Round(genericTotal / 2m, 2, MidpointRounding.AwayFromZero);
                    record.Cgst += half;
                    record.Sgst += genericTotal - half;
                }
            }

            // Invoice-level source values are distributed over the generated
            // rate rows so dashboard totals stay correct and NormalizeAmounts
            // cannot misinterpret the other tax slabs as a huge Round Off.
            record.Cess = firstOutput ? sourceCess : 0m;
            record.RoundOff = 0m;
            record.DocumentValue = record.TaxableValue + record.Igst + record.Cgst + record.Sgst + record.Cess;
            record.SourceDocumentValue = 0m;
            firstOutput = false;
            result.Add(record);
        }
        if (result.Count > 0)
        {
            var calculatedTotal = result.Sum(record => record.DocumentValue);
            if (Math.Abs(sourceDocumentValue) > 0.005m)
            {
                var difference = Math.Round(sourceDocumentValue - calculatedTotal, 2, MidpointRounding.AwayFromZero);
                result[0].RoundOff = difference;
                result[0].DocumentValue += difference;
                result[0].SourceDocumentValue = sourceDocumentValue;
            }
            else
            {
                result[0].SourceDocumentValue = calculatedTotal;
            }
        }
        return result;
    }

    private static SalesImportRecord CloneRecord(SalesImportRecord source)
    {
        var clone = new SalesImportRecord();
        foreach (var property in typeof(SalesImportRecord).GetProperties().Where(property => property.CanRead && property.CanWrite))
            property.SetValue(clone, property.GetValue(source));
        return clone;
    }

    private static DateTime? ParseMonthEnd(string value, bool uses1904)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial > 0)
        {
            try
            {
                var date = uses1904 ? new DateTime(1904,1,1).AddDays(serial) : DateTime.FromOADate(serial);
                return new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
            }
            catch { }
        }
        string[] formats = ["MMM-yy","MMM yy","MMM-yyyy","MMM yyyy","MMMM-yy","MMMM yyyy","MM-yyyy","MM/yyyy","yyyy-MM","yyyy/MM"];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            return new DateTime(exact.Year, exact.Month, DateTime.DaysInMonth(exact.Year, exact.Month));
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return new DateTime(parsed.Year, parsed.Month, DateTime.DaysInMonth(parsed.Year, parsed.Month));
        return null;
    }

    private static void Assign(SalesImportRecord record, string field, string text, bool uses1904)
    {
        switch (field)
        {
            case "CustomerGstin": record.CustomerGstin=text; break; case "CustomerName": record.CustomerName=text; break;
            case "DocumentType": record.DocumentType=text; if(text.Contains("credit",StringComparison.OrdinalIgnoreCase)) record.Destination="Credit Note"; break;
            case "DocumentNumber": record.DocumentNumber=text; break; case "DocumentDate": record.DocumentDate=ParseDate(text,uses1904); break;
            case "DocumentValue": record.DocumentValue=ParseDecimal(text); break; case "Rate": record.Rate=ParseDecimal(text); break;
            case "TaxableValue": record.TaxableValue=ParseDecimal(text); break; case "Igst": record.Igst=ParseDecimal(text); break; case "Cgst": record.Cgst=ParseDecimal(text); break; case "Sgst": record.Sgst=ParseDecimal(text); break; case "Cess": record.Cess=ParseDecimal(text); break; case "CessRate": record.CessRate=ParseDecimal(text); break; case "RoundOff": record.RoundOff=ParseDecimal(text); break;
            case "SalesLedger": record.SalesLedger=text; break; case "IgstLedger": record.IgstLedger=text; break; case "CgstLedger": record.CgstLedger=text; break; case "SgstLedger": record.SgstLedger=text; break;
            case "GstDate": record.GstDate=ParseDate(text,uses1904); break; case "PlaceOfSupply": record.PlaceOfSupply=text; break; case "Narration": record.Narration=text; break; case "ImportSource": record.ImportSource=text; break;
            case "ItemName": record.ItemName=text; break; case "HsnCode": record.HsnCode=text; break; case "ActualQuantity": record.ActualQuantity=ParseDecimal(text); break; case "Unit": record.Unit=text; break;
            case "ItemRate": record.ItemRate=ParseDecimal(text); break; case "BilledQuantity": record.BilledQuantity=ParseDecimal(text); break; case "ItemAmount": record.ItemAmount=ParseDecimal(text); break; case "ItemSalesLedger": record.ItemSalesLedger=text; break; case "AccountingAmount": record.AccountingAmount=ParseDecimal(text); break;
            case "Godown": record.Godown=text; break; case "BatchName": record.BatchName=text; break; case "BatchActualQuantity": record.BatchActualQuantity=ParseDecimal(text); break; case "BatchBilledQuantity": record.BatchBilledQuantity=ParseDecimal(text); break; case "BatchAmount": record.BatchAmount=ParseDecimal(text); break;
            case "DiscountPercent": record.DiscountPercent=ParseDecimal(text); break; case "DiscountAmount": record.DiscountAmount=ParseDecimal(text); break; case "OrderNumber": record.OrderNumber=text; break; case "TrackingNumber": record.TrackingNumber=text; break;
            case "ManufacturingDate": record.ManufacturingDate=ParseDate(text,uses1904); break; case "ExpiryDate": record.ExpiryDate=ParseDate(text,uses1904); break; case "ItemDescription": record.ItemDescription=text; break; case "PartNumberSku": record.PartNumberSku=text; break; case "Mrp": record.Mrp=ParseDecimal(text); break; case "SerialLotNumber": record.SerialLotNumber=text; break;
            case "CostCentre": record.CostCentre=text; break; case "PriceLevel": record.PriceLevel=text; break; case "AlternateQuantity": record.AlternateQuantity=ParseDecimal(text); break; case "AlternateUnit": record.AlternateUnit=text; break; case "FreeQuantity": record.FreeQuantity=ParseDecimal(text); break;
            case "DeliveryNoteNumber": record.DeliveryNoteNumber=text; break; case "BuyerOrderNumber": record.BuyerOrderNumber=text; break; case "BuyerOrderDate": record.BuyerOrderDate=ParseDate(text,uses1904); break;
            case "PartyMailingName": record.PartyMailingName=text; break; case "PartyState": record.PartyState=text; break; case "PartyCountry": record.PartyCountry=text; break; case "PartyPincode": record.PartyPincode=text; break; case "PartyAddressLine1": record.PartyAddressLine1=text; break; case "PartyAddressLine2": record.PartyAddressLine2=text; break; case "PartyAddressLine3": record.PartyAddressLine3=text; break;
            case "BuyerName": record.BuyerName=text; break; case "BuyerAddressLine1": record.BuyerAddressLine1=text; break; case "BuyerAddressLine2": record.BuyerAddressLine2=text; break; case "BuyerAddressLine3": record.BuyerAddressLine3=text; break;
            case "ConsigneeName": record.ConsigneeName=text; break; case "ConsigneeGstin": record.ConsigneeGstin=text; break; case "ConsigneeState": record.ConsigneeState=text; break; case "ConsigneeCountry": record.ConsigneeCountry=text; break; case "ConsigneePincode": record.ConsigneePincode=text; break; case "ConsigneeAddressLine1": record.ConsigneeAddressLine1=text; break; case "ConsigneeAddressLine2": record.ConsigneeAddressLine2=text; break; case "ConsigneeAddressLine3": record.ConsigneeAddressLine3=text; break;
            case "PaymentDueDays": record.PaymentDueDays=(int)ParseDecimal(text); break; case "DeliveryNoteDate": record.DeliveryNoteDate=ParseDate(text,uses1904); break; case "DeliveryNoteNumber2": record.DeliveryNoteNumber2=text; break; case "DeliveryNoteDate2": record.DeliveryNoteDate2=ParseDate(text,uses1904); break; case "BuyerOrderReference": record.BuyerOrderReference=text; break; case "BuyerOrderTerms": record.BuyerOrderTerms=text; break;
            case "DispatchDocumentNumber": record.DispatchDocumentNumber=text; break; case "DispatchThrough": record.DispatchThrough=text; break; case "FinalDestination": record.FinalDestination=text; break; case "DestinationCountry": record.DestinationCountry=text; break; case "VehicleVesselNumber": record.VehicleVesselNumber=text; break; case "LrBillOfLadingNumber": record.LrBillOfLadingNumber=text; break; case "LrBillOfLadingDate": record.LrBillOfLadingDate=ParseDate(text,uses1904); break; case "PlaceOfReceipt": record.PlaceOfReceipt=text; break; case "PortOfLoading": record.PortOfLoading=text; break; case "PortOfDischarge": record.PortOfDischarge=text; break; case "ShippingBillNumber": record.ShippingBillNumber=text; break; case "ShippingBillDate": record.ShippingBillDate=ParseDate(text,uses1904); break; case "PortCode": record.PortCode=text; break; case "CheckPost": record.CheckPost=text; break;
            case "CompanyGstRegistration": record.CompanyGstRegistration=text; break; case "CompanyGstRegistrationType": record.CompanyGstRegistrationType=text; break; case "CompanyGstState": record.CompanyGstState=text; break; case "TaxUnitName": record.TaxUnitName=text; break; case "InvoiceDateTime": record.InvoiceDateTime=ParseDateTime(text,uses1904); break; case "Irn": record.Irn=text; break; case "IrnAcknowledgementNumber": record.IrnAcknowledgementNumber=text; break; case "IrnAcknowledgementDate": record.IrnAcknowledgementDate=ParseDate(text,uses1904); break; case "BillToPlace": record.BillToPlace=text; break; case "ShipToPlace": record.ShipToPlace=text; break;
            case "DispatchFromName": record.DispatchFromName=text; break; case "DispatchFromAddressLine1": record.DispatchFromAddressLine1=text; break; case "DispatchFromAddressLine2": record.DispatchFromAddressLine2=text; break; case "DispatchFromAddressLine3": record.DispatchFromAddressLine3=text; break; case "DispatchFromState": record.DispatchFromState=text; break; case "DispatchFromPincode": record.DispatchFromPincode=text; break; case "DispatchFromPlace": record.DispatchFromPlace=text; break; case "EWayBillNumber": record.EWayBillNumber=text; break; case "EWayBillDate": record.EWayBillDate=ParseDate(text,uses1904); break; case "TransporterId": record.TransporterId=text; break; case "TransporterName": record.TransporterName=text; break; case "TransportMode": record.TransportMode=text; break; case "TransportDocumentNumber": record.TransportDocumentNumber=text; break; case "TransportDocumentDate": record.TransportDocumentDate=ParseDate(text,uses1904); break; case "EWayVehicleNumber": record.EWayVehicleNumber=text; break; case "VehicleType": record.VehicleType=text; break; case "TypeOfGoodsGold": record.TypeOfGoodsGold=text; break; case "ExcludeFromEWayBill": record.ExcludeFromEWayBill=ParseBool(text); break;
        }
    }

    private static SheetInspection InspectSheet(OpenXmlWorksheetData sheet)
    {
        SheetInspection? best = null;
        var maxHeaderRow = Math.Min(12, Math.Max(1, sheet.MaxRow));
        for (var row=1; row<=maxHeaderRow; row++)
        {
            foreach (var depth in new[]{1,2})
            {
                if (row+depth-1>sheet.MaxRow) continue;
                var headers=BuildHeaders(sheet,row,depth);
                if(headers.Count==0) continue;
                var mapping=headers.ToDictionary(
                    header => header,
                    header =>
                    {
                        var builtIn = SalesManualFieldCatalog.ResolveBuiltInOnly(header);
                        return builtIn.Length > 0 ? builtIn : SalesManualFieldCatalog.Resolve(header);
                    },
                    StringComparer.OrdinalIgnoreCase);
                ApplyContextAwareHeaderRules(headers, mapping);
                var rateBuckets=DetectRateBuckets(headers);
                foreach (var bucket in rateBuckets)
                    mapping[headers[bucket.ColumnIndex - 1]] = string.Empty;
                var isB2cs=IsB2csSheet(sheet.Name, headers, mapping);
                var monthColumn=FindHeaderColumn(headers, "MONTH");
                if (isB2cs && monthColumn > 0)
                    mapping[headers[monthColumn - 1]] = "DocumentDate";
                var mapped=mapping.Values.Count(value=>value.Length>0);
                var required=RequiredScore(mapping.Values, rateBuckets, isB2cs);
                var score=mapped+required*3+Math.Min(6, rateBuckets.Count);
                var samples=BuildSamples(sheet,row+depth,headers.Count,headers);
                var candidate=new SheetInspection(sheet.Name,row,row+depth,headers,mapping,samples,score,
                    mapping.Values.Contains("ItemName",StringComparer.OrdinalIgnoreCase)?"Inventory":isB2cs?"B2CS":"Accounting",
                    rateBuckets,isB2cs,monthColumn);
                if(best is null||candidate.Score>best.Score) best=candidate;
            }
        }
        return best??new SheetInspection(sheet.Name,1,2,[],new Dictionary<string,string>(),new Dictionary<string,string>(),0,"Unknown",[],false,0);
    }

    private static IReadOnlyList<string> BuildHeaders(OpenXmlWorksheetData sheet,int startRow,int depth)
    {
        var columns=Enumerable.Range(1,180).Where(col=>Enumerable.Range(startRow,depth).Any(row=>sheet.GetCell(row,col).Trim().Length>0)).ToArray();
        if(columns.Length==0) return [];
        var max=columns.Max(); var result=new List<string>(); var seen=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase); string parent="";
        for(var col=1;col<=max;col++)
        {
            string header;
            if(depth==1) header=Clean(sheet.GetCell(startRow,col));
            else
            {
                var top=Clean(sheet.GetCell(startRow,col)); if(top.Length>0) parent=top;
                var child=Clean(sheet.GetCell(startRow+1,col));
                if(child.Length>0 && parent.Length>0 && !child.Equals(parent,StringComparison.OrdinalIgnoreCase)) header=parent+" > "+child;
                else header=child.Length>0?child:top;
                if(!TryParseRateBucketHeader(header, out _, out _) &&
                   SalesManualFieldCatalog.Resolve(header).Length==0 && child.Length>0 && SalesManualFieldCatalog.Resolve(child).Length>0) header=child;
            }
            if(header.Length==0) header=$"Column {col}";
            if(seen.TryGetValue(header,out var count)){count++;seen[header]=count;header+=$" [{count}]";}else seen[header]=1;
            result.Add(header);
        }
        return result;
    }

    private static IReadOnlyDictionary<string,string> BuildSamples(OpenXmlWorksheetData sheet,int dataStart,int count,IReadOnlyList<string> headers)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for(var col=1;col<=count;col++)
        {
            string sample="";
            for(var row=dataStart;row<=Math.Min(sheet.MaxRow,dataStart+12);row++){sample=Clean(sheet.GetCell(row,col));if(sample.Length>0)break;}
            result[headers[col-1]]=sample;
        }
        return result;
    }

    private static bool IsComplete(IEnumerable<string> targets, IReadOnlyCollection<RateBucketColumn> rateBuckets, bool isB2cs)
    {
        var set=targets.Where(v=>v.Length>0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasDate=set.Contains("DocumentDate");
        var hasNumber=set.Contains("DocumentNumber")||isB2cs;
        var hasAmount=set.Contains("DocumentValue")||set.Contains("TaxableValue")||set.Contains("ItemAmount")||rateBuckets.Any(bucket=>bucket.Kind=="TAXABLE");
        return hasDate&&hasNumber&&hasAmount;
    }

    private static int RequiredScore(IEnumerable<string> targets, IReadOnlyCollection<RateBucketColumn> rateBuckets, bool isB2cs)
    {
        var set=targets.ToHashSet(StringComparer.OrdinalIgnoreCase);var score=0;
        if(set.Contains("DocumentDate"))score++;
        if(set.Contains("DocumentNumber")||isB2cs)score++;
        if(set.Contains("DocumentValue")||set.Contains("TaxableValue")||set.Contains("ItemAmount")||rateBuckets.Any(bucket=>bucket.Kind=="TAXABLE"))score++;
        if(set.Contains("CustomerName")||set.Contains("CustomerGstin")||isB2cs)score++;
        if(set.Contains("ItemName"))score++;
        return score;
    }
    private static bool HasUsableRow(SalesImportRecord r)=>r.DocumentNumber.Length>0||r.DocumentDate is not null||r.CustomerName.Length>0||r.ItemName.Length>0||Math.Abs(r.DocumentValue)>0.005m||Math.Abs(r.TaxableValue)>0.005m||Math.Abs(r.ItemAmount)>0.005m;
    private static bool IsTotalRow(string text)=>text.Trim().StartsWith("TOTAL",StringComparison.OrdinalIgnoreCase)||text.Contains("SUBTOTAL",StringComparison.OrdinalIgnoreCase)||text.Contains("GRAND TOTAL",StringComparison.OrdinalIgnoreCase);
    private static string HeaderSignature(IEnumerable<string> headers){var canonical=string.Join("|",headers.Select(SalesManualFieldCatalog.Normalize));return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));}
    private static string FileHash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    private static string NormalizeGstin(string value){var normalized=new string((value??string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());return normalized.Length==15?normalized:string.Empty;}
    private static string Clean(string value)=>string.Join(" ",(value??string.Empty).Replace('\r',' ').Replace('\n',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static decimal ParseDecimal(string value){var text=(value??string.Empty).Replace(",","").Replace("₹","").Trim();if(text.EndsWith("%"))text=text[..^1];return decimal.TryParse(text,NumberStyles.Any,CultureInfo.InvariantCulture,out var result)?result:decimal.TryParse(text,NumberStyles.Any,CultureInfo.CurrentCulture,out result)?result:0m;}
    private static bool ParseBool(string value)=>value.Equals("YES",StringComparison.OrdinalIgnoreCase)||value.Equals("Y",StringComparison.OrdinalIgnoreCase)||value.Equals("TRUE",StringComparison.OrdinalIgnoreCase)||value=="1";
    private static DateTime? ParseDate(string value,bool uses1904){var text=value.Trim();if(double.TryParse(text,NumberStyles.Any,CultureInfo.InvariantCulture,out var serial)&&serial>0){try{return (uses1904?new DateTime(1904,1,1).AddDays(serial):DateTime.FromOADate(serial)).Date;}catch{}}string[] formats=["dd-MM-yyyy","dd/MM/yyyy","dd.MM.yyyy","yyyy-MM-dd","d-M-yyyy","d/M/yyyy","M/d/yyyy","MM/dd/yyyy","dd-MMM-yyyy","d-MMM-yyyy"];return DateTime.TryParseExact(text,formats,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out var exact)?exact.Date:DateTime.TryParse(text,CultureInfo.CurrentCulture,DateTimeStyles.AllowWhiteSpaces,out var parsed)?parsed.Date:null;}
    private static DateTime? ParseDateTime(string value,bool uses1904)=>ParseDate(value,uses1904)??(DateTime.TryParse(value,CultureInfo.CurrentCulture,DateTimeStyles.AllowWhiteSpaces,out var parsed)?parsed:null);
    private static string FormatRate(decimal rate)=>rate.ToString(rate==decimal.Truncate(rate)?"0":"0.##",CultureInfo.InvariantCulture);

    private sealed record SheetInspection(
        string SheetName,int HeaderRow,int DataStartRow,IReadOnlyList<string> Headers,
        IReadOnlyDictionary<string,string> Mapping,IReadOnlyDictionary<string,string> SampleValues,
        int Score,string DetectedMode,IReadOnlyList<RateBucketColumn> RateBuckets,bool IsB2cs,int MonthColumnIndex);

    private sealed record RateBucketColumn(int ColumnIndex, decimal Rate, string Kind);
}
