using System.Globalization;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Infrastructure.Purchases;

public sealed class PurchaseImportService :
    IPurchaseImportService
{
    private readonly OpenXmlWorkbookReader _reader = new();
    private readonly PurchaseSourceParser _parser = new();
    private readonly PurchaseReconciler _reconciler = new();

    public Task<PurchaseImportResult> ImportAsync(
        PurchaseImportRequest request,
        IProgress<PurchaseImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.Run(
            () => Import(
                request,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private PurchaseImportResult Import(
        PurchaseImportRequest request,
        IProgress<PurchaseImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        progress?.Report(
            new PurchaseImportProgress(
                5,
                "Reading GSTR-2A workbook"));

        var workbook2A = _reader.Read(
            request.Gstr2AFilePath,
            cancellationToken);

        if (_parser.DetectSourceKind(
                workbook2A) ==
            PurchaseSourceKind.TwoB)
        {
            throw new InvalidDataException(
                "The file selected as GSTR-2A appears to be a GSTR-2B workbook.");
        }

        OpenXmlWorkbookData? workbook2B = null;
        var gstr2BFilePath = request.Gstr2BFilePath;

        if (!string.IsNullOrWhiteSpace(
                gstr2BFilePath))
        {
            progress?.Report(
                new PurchaseImportProgress(
                    18,
                    "Reading GSTR-2B workbook"));

            workbook2B = _reader.Read(
                gstr2BFilePath,
                cancellationToken);

            if (_parser.DetectSourceKind(
                    workbook2B) ==
                PurchaseSourceKind.TwoA)
            {
                throw new InvalidDataException(
                    "The file selected as GSTR-2B appears to be a GSTR-2A workbook.");
            }
        }

        progress?.Report(
            new PurchaseImportProgress(
                30,
                "Detecting company and GSTIN"));

        var requestedCompanyName = request.CompanyName;
        var requestedClientGstin = request.ClientGstin;

        var companyName =
            string.IsNullOrWhiteSpace(
                requestedCompanyName)
                ? _parser.DetectCompanyName(
                    workbook2A)
                : requestedCompanyName.Trim();

        var clientGstin =
            string.IsNullOrWhiteSpace(
                requestedClientGstin)
                ? _parser.DetectClientGstin(
                    workbook2A)
                : PurchaseImportUtilities.NormalizeGstin(
                    requestedClientGstin);

        var financialYear =
            _parser.DetectFinancialYear(
                workbook2A);

        var issues =
            new List<PurchaseImportIssue>();

        progress?.Report(
            new PurchaseImportProgress(
                40,
                "Parsing GSTR-2A purchase sections"));

        var rows2A =
            _parser.ParseWorkbookRows(
                workbook2A,
                PurchaseSourceKind.TwoA,
                issues,
                cancellationToken);

        var rows2B =
            new List<PurchaseSourceRow>();

        if (workbook2B is not null)
        {
            progress?.Report(
                new PurchaseImportProgress(
                    55,
                    "Parsing GSTR-2B purchase sections"));

            rows2B =
                _parser.ParseWorkbookRows(
                    workbook2B,
                    PurchaseSourceKind.TwoB,
                    issues,
                    cancellationToken);
        }

        progress?.Report(
            new PurchaseImportProgress(
                66,
                "Checking exact duplicate rows"));

        var duplicateRowsSkipped = 0;

        rows2A = RemoveExactDuplicates(
            rows2A,
            ref duplicateRowsSkipped);

        rows2B = RemoveExactDuplicates(
            rows2B,
            ref duplicateRowsSkipped);

        progress?.Report(
            new PurchaseImportProgress(
                75,
                "Reconciling GSTR-2A against GSTR-2B"));

        var records =
            _reconciler.Reconcile(
                rows2A,
                rows2B,
                cancellationToken);

        progress?.Report(
            new PurchaseImportProgress(
                90,
                "Preparing preview and summary"));

        var warnings = BuildWarnings(
            workbook2B,
            companyName,
            clientGstin,
            rows2A,
            rows2B);

        var summary = BuildSummary(
            records,
            rows2A.Count,
            rows2B.Count,
            duplicateRowsSkipped,
            issues.Count);

        progress?.Report(
            new PurchaseImportProgress(
                100,
                "Purchase import pilot completed"));

        return new PurchaseImportResult
        {
            CompanyName = companyName,
            ClientGstin = clientGstin,
            FinancialYear = financialYear,
            Records = records,
            Issues = issues,
            Warnings = warnings,
            Summary = summary
        };
    }

    private static void ValidateRequest(
        PurchaseImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(
                request.Gstr2AFilePath))
        {
            throw new InvalidOperationException(
                "Select the GSTR-2A purchase workbook.");
        }

        ValidateWorkbookExtension(
            request.Gstr2AFilePath);

        if (!string.IsNullOrWhiteSpace(
                request.Gstr2BFilePath))
        {
            ValidateWorkbookExtension(
                request.Gstr2BFilePath);
        }
    }

    private static void ValidateWorkbookExtension(
        string filePath)
    {
        var extension = Path.GetExtension(
            filePath);

        if (!string.Equals(
                extension,
                ".xlsx",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                extension,
                ".xlsm",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "This pilot supports .xlsx and .xlsm workbooks.");
        }
    }

    private static List<PurchaseSourceRow> RemoveExactDuplicates(
        IEnumerable<PurchaseSourceRow> rows,
        ref int duplicateRowsSkipped)
    {
        var result =
            new List<PurchaseSourceRow>();

        var signatures =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var signature = string.Join(
                "|",
                row.DocumentKey,
                row.Rate.ToString(
                    "0.####",
                    CultureInfo.InvariantCulture),
                row.TaxableValue.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture),
                row.Igst.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture),
                row.Cgst.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture),
                row.Sgst.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture),
                row.Cess.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture));

            if (!signatures.Add(
                    signature))
            {
                duplicateRowsSkipped++;
                continue;
            }

            result.Add(row);
        }

        return result;
    }

    private static PurchaseImportSummary BuildSummary(
        IReadOnlyList<PurchaseImportRecord> records,
        int source2ARows,
        int source2BRows,
        int duplicateRowsSkipped,
        int invalidRowsSkipped) =>
        new()
        {
            Source2ARows = source2ARows,
            Source2BRows = source2BRows,
            PurchaseRows = records.Count(record =>
                record.Destination == "Purchase"),
            RcmRows = records.Count(record =>
                record.Destination == "RCM"),
            DebitNoteRows = records.Count(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteDestination),
            DebitNoteReverseRows = records.Count(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteReverseDestination),
            MatchedRows = records.Count(record =>
                record.ReconciliationStatus == "Matched"),
            TwoAOnlyRows = records.Count(record =>
                record.ReconciliationStatus == "2A Only"),
            TwoBOnlyRows = records.Count(record =>
                record.ReconciliationStatus == "2B Only"),
            DifferenceRows = records.Count(record =>
                record.ReconciliationStatus == "Value Difference"),
            DuplicateRowsSkipped = duplicateRowsSkipped,
            InvalidRowsSkipped = invalidRowsSkipped
        };

    private static IReadOnlyList<string> BuildWarnings(
        OpenXmlWorkbookData? workbook2B,
        string companyName,
        string clientGstin,
        IReadOnlyCollection<PurchaseSourceRow> rows2A,
        IReadOnlyCollection<PurchaseSourceRow> rows2B)
    {
        var warnings =
            new List<string>();

        if (workbook2B is null)
        {
            warnings.Add(
                "No GSTR-2B workbook was selected. All GSTR-2A rows are marked as 2A Only.");
        }

        if (string.IsNullOrWhiteSpace(
                companyName))
        {
            warnings.Add(
                "Company name could not be detected.");
        }

        if (!PurchaseImportUtilities.IsValidGstin(
                clientGstin))
        {
            warnings.Add(
                "Client GSTIN could not be detected or is invalid.");
        }

        if (rows2A.Count == 0)
        {
            warnings.Add(
                "No supported GSTR-2A purchase rows were found.");
        }

        if (workbook2B is not null &&
            rows2B.Count == 0)
        {
            warnings.Add(
                "No supported GSTR-2B purchase rows were found.");
        }

        return warnings;
    }
}
