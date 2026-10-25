using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Production Bank Statement OCR orchestration.
///
/// Correct execution order:
/// 1) PaddleOCR-VL 1.6 raw recognition only (external runtime/models called directly).
/// 2) AAH-owned document grouping and logical page ordering.
/// 3) AAH-owned cross-page/running-balance/statement-summary verification and safe correction.
/// 4) AAH creates the normal Bank Statement workbook.
/// 5) Existing BankStatementImportService remains the accounting/persistence authority.
///
/// No external OCR worker/verifier Python script is executed by this service.
/// </summary>
public sealed class BankOcrExcelConversionService
{
    private readonly AdvancedBankOcrSettings _settings;
    private readonly BankImageOcrRecognitionService _recognition = new();
    private readonly BankImageDocumentVerificationService _verification = new();

    public BankOcrExcelConversionService()
    {
        _settings = AdvancedBankOcrSettings.Load();
    }

    public static bool IsRasterImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".webp";
    }

    public static bool IsSupportedOcrSource(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || IsRasterImage(path);

    /// <summary>
    /// Runs the exact production Bank OCR raw-document recognition stage without invoking
    /// Bank Statement grouping/verification/accounting. Mobile Invoice uses this entry point
    /// so there is one OCR runtime/settings/process authority for both Banking and invoices.
    /// </summary>
    internal async Task<BankOcrRecognitionBatch> RecognizeRawDocumentAsync(
        IReadOnlyList<string> sourcePaths,
        string runRoot,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sources = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sources.Count == 0)
            return BankOcrRecognitionBatch.Failed(
                "No scanned source files were selected for the shared Bank OCR recognition stage.",
                Path.Combine(runRoot, "recognition.log"),
                Path.Combine(runRoot, "recognition_status.json"),
                Path.Combine(runRoot, "recognition_result.json"));

        foreach (var source in sources)
        {
            if (!File.Exists(source))
                return BankOcrRecognitionBatch.Failed(
                    $"The OCR source no longer exists: {source}",
                    Path.Combine(runRoot, "recognition.log"),
                    Path.Combine(runRoot, "recognition_status.json"),
                    Path.Combine(runRoot, "recognition_result.json"));
            if (!IsSupportedOcrSource(source))
                return BankOcrRecognitionBatch.Failed(
                    $"Unsupported OCR source type: {Path.GetExtension(source)}",
                    Path.Combine(runRoot, "recognition.log"),
                    Path.Combine(runRoot, "recognition_status.json"),
                    Path.Combine(runRoot, "recognition_result.json"));
        }

        if (!OperatingSystem.IsWindows())
            return BankOcrRecognitionBatch.Failed(
                "Advanced Bank OCR requires the configured Windows PaddleOCR-VL runtime.",
                Path.Combine(runRoot, "recognition.log"),
                Path.Combine(runRoot, "recognition_status.json"),
                Path.Combine(runRoot, "recognition_result.json"));

        // R46 R34: Bank OCR is intentionally lazy and normally has no live runtime while idle.
        // Invoke the exact same on-demand runtime resolver used by a real Banking image import
        // at the moment this invoice OCR job starts. Mobile Invoice does not maintain a second
        // resolver, hard-coded path, or persistent OCR process; it enters through this Bank OCR
        // service and lets the Bank OCR job resolve/launch its own production runtime directly.
        var runtime = ResolveRuntimeForJob();
        if (!runtime.IsReady)
        {
            return BankOcrRecognitionBatch.Failed(
                runtime.FailureMessage.Length > 0
                    ? runtime.FailureMessage
                    : "PaddleOCR-VL 1.6 production runtime was not found by the existing Bank OCR job resolver.",
                Path.Combine(runRoot, "recognition.log"),
                Path.Combine(runRoot, "recognition_status.json"),
                Path.Combine(runRoot, "recognition_result.json"));
        }

        progress?.Invoke($"Shared Bank OCR • direct on-demand Banking runtime at {runtime.RootFolder}");
        progress?.Invoke("Shared Bank OCR • production raw document recognition starting");
        return await _recognition.RecognizeAsync(
            sources,
            runtime,
            runRoot,
            progress,
            _settings.TimeoutMinutes,
            cancellationToken).ConfigureAwait(false);
    }

    private BankAdvancedOcrRuntimeInfo ResolveRuntimeForJob()
    {
        // Reload the persisted Bank OCR configuration at execution time so a setting changed
        // after service construction is immediately honoured. Runtime discovery remains the
        // existing BankAdvancedOcrRuntimeLocator used by Banking; there is no invoice locator.
        var current = AdvancedBankOcrSettings.Load();
        var configuredRoot = current.ExternalRootFolder.Length > 0
            ? current.ExternalRootFolder
            : _settings.ExternalRootFolder;
        return BankAdvancedOcrRuntimeLocator.Resolve(configuredRoot);
    }

    public async Task<BankOcrConversionResult> ProcessAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var batch = await ProcessBatchAsync([sourcePath], null, cancellationToken);
        if (!batch.WasSuccessful || batch.Documents.Count == 0)
        {
            return BankOcrConversionResult.Failed(
                sourcePath,
                batch.Issue?.ErrorCode ?? BankImportErrorCode.O006_OcrWorkbookFailure,
                batch.Issue?.Message ?? "Advanced Bank OCR did not produce a verified statement workbook.",
                batch.LogPath);
        }

        if (batch.Documents.Count > 1)
        {
            return BankOcrConversionResult.Failed(
                sourcePath,
                BankImportErrorCode.O005_OcrTableDetectionFailure,
                $"The selected source produced {batch.Documents.Count:N0} statement documents. Import the sources as a batch so every document can be handled safely.",
                batch.LogPath);
        }

        var document = batch.Documents[0];
        if (!IsVerified(document.VerificationStatus))
        {
            return BankOcrConversionResult.Failed(
                sourcePath,
                BankImportErrorCode.O005_OcrTableDetectionFailure,
                $"AAH document verification is '{document.VerificationStatus}'. Page status: {document.PageStatus}; summary status: {document.SummaryStatus}. " +
                $"The OCR data was not passed into accounting. Verification audit: {document.VerificationAuditPath}",
                batch.LogPath);
        }

        return new BankOcrConversionResult
        {
            SourcePath = document.PrimarySourcePath,
            SourcePaths = document.SourcePaths,
            GeneratedWorkbookPath = document.GeneratedWorkbookPath,
            VerifiedExternalWorkbookPath = document.VerificationAuditPath,
            VerificationAuditPath = document.VerificationAuditPath,
            ExternalBatchOutputDirectory = batch.ExternalBatchOutputDirectory,
            LogPath = batch.LogPath,
            WasSuccessful = true,
            OcrPageCount = document.PageCount,
            TransactionRowsPrepared = document.TransactionRowsPrepared,
            VerificationStatus = document.VerificationStatus,
            PageStatus = document.PageStatus,
            SummaryStatus = document.SummaryStatus,
            BankName = document.BankName,
            AccountNumber = document.AccountNumber,
            CorrectionsApplied = document.CorrectionsApplied,
            VerificationIssueCount = document.VerificationIssueCount
        };
    }

    public async Task<BankOcrBatchConversionResult> ProcessBatchAsync(
        IReadOnlyList<string> sourcePaths,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sources = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sources.Count == 0)
            return BankOcrBatchConversionResult.Failed(BankImportErrorCode.O002_OcrSourceUnreadable, "No scanned Bank Statement files were selected.");

        foreach (var source in sources)
        {
            if (!File.Exists(source))
                return BankOcrBatchConversionResult.Failed(BankImportErrorCode.O002_OcrSourceUnreadable, $"The selected Bank Statement source no longer exists: {source}");
            if (!IsSupportedOcrSource(source))
                return BankOcrBatchConversionResult.Failed(BankImportErrorCode.O002_OcrSourceUnreadable, $"Unsupported Bank OCR source type: {Path.GetExtension(source)}");
        }

        if (!OperatingSystem.IsWindows())
            return BankOcrBatchConversionResult.Failed(BankImportErrorCode.O001_OcrUnavailable, "Advanced Bank OCR requires the configured Windows PaddleOCR-VL runtime.");

        // Resolve the production runtime only when an OCR job actually starts. The identical
        // on-demand path is used for Banking image imports and Mobile Invoice raw recognition.
        var runtime = ResolveRuntimeForJob();
        if (!runtime.IsReady)
            return BankOcrBatchConversionResult.Failed(
                BankImportErrorCode.O001_OcrUnavailable,
                runtime.FailureMessage.Length > 0
                    ? runtime.FailureMessage
                    : "PaddleOCR-VL 1.6 production runtime was not found. Configure Advanced OCR Engine Folder in Settings > PDF / Banking / Sales.");

        CleanupOldOcrRuns();
        var runRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Accounting Automation Hub",
            "BankOCR",
            "Runs",
            $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        progress?.Invoke(
            sources.Count == 1
                ? "Bank OCR • recognition stage starting"
                : $"Bank OCR • {sources.Count:N0} source files collected; recognition stage starting");

        BankOcrRecognitionBatch recognition;
        try
        {
            recognition = await _recognition.RecognizeAsync(
                sources,
                runtime,
                runRoot,
                progress,
                _settings.TimeoutMinutes,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return BankOcrBatchConversionResult.Failed(
                BankImportErrorCode.O002_OcrSourceUnreadable,
                $"PaddleOCR-VL recognition failed: {exception.Message}",
                Path.Combine(runRoot, "recognition.log"));
        }

        if (!recognition.WasSuccessful)
            return BankOcrBatchConversionResult.Failed(
                BankImportErrorCode.O002_OcrSourceUnreadable,
                recognition.FailureMessage.Length > 0 ? recognition.FailureMessage : "PaddleOCR-VL recognition produced no usable raw page JSON.",
                recognition.LogPath);

        // Raster bursts are atomic. A six-photo statement must never import three pages
        // simply because recognition failed on the other three. Every selected source must
        // produce at least one raw page before document verification/accounting can begin.
        var recognizedSources = recognition.Pages
            .Where(page => !string.IsNullOrWhiteSpace(page.SourcePath))
            .Select(page => Path.GetFullPath(page.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingRecognitionSources = sources
            .Where(source => !recognizedSources.Contains(source))
            .ToArray();
        if (recognition.SourceErrors.Count > 0 || missingRecognitionSources.Length > 0)
        {
            var missingText = missingRecognitionSources.Length == 0
                ? string.Empty
                : $" Missing: {string.Join(", ", missingRecognitionSources.Select(Path.GetFileName))}.";
            var errorText = recognition.SourceErrors.Count == 0
                ? string.Empty
                : $" OCR errors: {string.Join(" | ", recognition.SourceErrors.Take(3))}.";
            return BankOcrBatchConversionResult.Failed(
                BankImportErrorCode.O002_OcrSourceUnreadable,
                $"Bank OCR stopped before accounting because only {recognizedSources.Count:N0} of {sources.Count:N0} source file(s) completed raw recognition. No partial statement was imported.{missingText}{errorText}",
                recognition.LogPath);
        }

        progress?.Invoke("Bank OCR • recognition complete; grouping and ordering pages inside AAH");
        var verified = _verification.Verify(recognition, runRoot, progress, cancellationToken);
        if (!verified.WasSuccessful || verified.Documents.Count == 0)
            return BankOcrBatchConversionResult.Failed(
                BankImportErrorCode.O005_OcrTableDetectionFailure,
                verified.FailureMessage.Length > 0 ? verified.FailureMessage : "AAH could not build a verifiable Bank Statement document from the raw OCR pages.",
                recognition.LogPath);

        var assignedSources = verified.Documents
            .SelectMany(document => document.SourcePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unassignedSources = sources.Where(source => !assignedSources.Contains(source)).ToArray();
        if (unassignedSources.Length > 0)
            return BankOcrBatchConversionResult.Failed(
                BankImportErrorCode.O005_OcrTableDetectionFailure,
                $"AAH document verification did not assign {unassignedSources.Length:N0} of {sources.Count:N0} source file(s) to a statement. No partial statement was imported. Unassigned: {string.Join(", ", unassignedSources.Select(Path.GetFileName))}.",
                recognition.LogPath);

        var rasterBurst = sources.Count > 1 && sources.All(IsRasterImage);
        var unverifiedDocuments = verified.Documents.Where(document => !IsVerified(document.Status)).ToArray();
        if (rasterBurst && unverifiedDocuments.Length > 0)
            return BankOcrBatchConversionResult.Failed(
                BankImportErrorCode.O005_OcrTableDetectionFailure,
                $"The image batch produced {unverifiedDocuments.Length:N0} document(s) that did not pass complete AAH verification. The entire raster batch was held; no verified subset was imported.",
                recognition.LogPath);

        var documents = new List<BankOcrDocumentConversionResult>();
        var warnings = new List<string>();

        foreach (var document in verified.Documents.OrderBy(item => item.DocumentNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primarySource = document.SourcePaths.FirstOrDefault() ?? sources[0];
            var outputPath = CreateNormalizedWorkbookPath(primarySource, document.DocumentNumber);
            try
            {
                WritePipelineWorkbook(outputPath, document, verified.AuditPath);
                // Separate, source-backed metadata record for bank-master
                // discovery and diagnostics. It is NOT an import authorization.
                // The ordinary workbook header retains identical metadata keys.
                var metadataPath = Path.Combine(runRoot,
                    $"AAH_OCR_BANK_METADATA_DOCUMENT_{document.DocumentNumber:000}.json");
                File.WriteAllText(metadataPath,
                    JsonSerializer.Serialize(new
                    {
                        DocumentNumber = document.DocumentNumber,
                        BankName = document.BankName,
                        AccountNumber = document.AccountNumber,
                        AccountName = document.AccountName,
                        AccountTypeAsPrinted = document.AccountType,
                        BranchName = document.BranchName,
                        IFSC = document.Ifsc,
                        StatementFrom = document.StatementFrom,
                        StatementTo = document.StatementTo,
                        OpeningBalance = document.OpeningBalance,
                        OpeningSource = document.OpeningSource,
                        PrintedSummaryOpeningBalance = document.PrintedSummaryOpeningBalance,
                        BroughtForwardDebit = document.BroughtForwardDebit,
                        BroughtForwardCredit = document.BroughtForwardCredit,
                        ClosingBalance = document.ClosingBalance,
                        OCRPageCount = document.Pages.Count,
                        OCRTransactionCandidates = document.Transactions.Count,
                        VerificationStatus = document.Status,
                        ImportAuthorized = IsVerified(document.Status),
                        MetadataEvidence = "AAH source-recognized scanned bank statement"
                    }, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                // Keep the normalized workbook from a blocked document available
                // for source-backed review, even if normal transient workbooks are
                // deleted after the import attempt. This is not an import request.
                if (!IsVerified(document.Status))
                {
                    var reviewCopy = Path.Combine(runRoot,
                        $"AAH_OCR_REVIEW_ONLY_DOCUMENT_{document.DocumentNumber:000}.xlsx");
                    File.Copy(outputPath, reviewCopy, overwrite: true);
                }
                documents.Add(new BankOcrDocumentConversionResult
                {
                    DocumentNumber = document.DocumentNumber,
                    SourcePaths = document.SourcePaths,
                    PrimarySourcePath = primarySource,
                    GeneratedWorkbookPath = outputPath,
                    VerifiedExternalWorkbookPath = verified.AuditPath,
                    VerificationAuditPath = verified.AuditPath,
                    VerificationStatus = document.Status,
                    PageStatus = document.PageStatus,
                    SummaryStatus = document.SummaryStatus,
                    BankName = document.BankName,
                    AccountNumber = document.AccountNumber,
                    PageCount = document.Pages.Count,
                    TransactionRowsPrepared = document.Transactions.Count,
                    CorrectionsApplied = document.Corrections.Count,
                    VerificationIssueCount = document.Issues.Count
                });
            }
            catch (Exception exception)
            {
                warnings.Add($"Document {document.DocumentNumber:N0}: generated Bank Statement workbook failed: {exception.Message}");
            }
        }

        if (documents.Count == 0)
            return BankOcrBatchConversionResult.Failed(
                BankImportErrorCode.O006_OcrWorkbookFailure,
                warnings.Count > 0 ? string.Join(" | ", warnings.Take(4)) : "AAH verification completed but no Bank Statement workbook could be generated.",
                recognition.LogPath);

        var reviewCount = documents.Count(document => !IsVerified(document.VerificationStatus));
        progress?.Invoke(
            reviewCount == 0
                ? $"Bank OCR • AAH verification complete; {documents.Sum(item => item.TransactionRowsPrepared):N0} transaction row(s) ready for normal Bank import"
                : $"Bank OCR • verification complete; {reviewCount:N0} document(s) require review and will not be posted automatically");

        return new BankOcrBatchConversionResult
        {
            WasSuccessful = true,
            LogPath = recognition.LogPath,
            StatusPath = recognition.StatusPath,
            ExternalRuntimeRoot = runtime.RootFolder,
            ExternalBatchOutputDirectory = runRoot,
            VerificationAuditPath = verified.AuditPath,
            Documents = documents,
            SourceErrorCount = recognition.SourceErrors.Count + verified.ParseIssues.Count,
            ReviewCount = reviewCount,
            ConversionWarnings = [.. recognition.SourceErrors, .. verified.ParseIssues, .. warnings]
        };
    }

    public Task<BankStatementImportIssue?> CleanupAsync(BankOcrConversionResult item) =>
        CleanupGeneratedWorkbookAsync(item.GeneratedWorkbookPath, item.SourcePath);

    public async Task<IReadOnlyList<BankStatementImportIssue>> CleanupAsync(BankOcrBatchConversionResult batch)
    {
        var issues = new List<BankStatementImportIssue>();
        foreach (var document in batch.Documents)
        {
            var issue = await CleanupGeneratedWorkbookAsync(document.GeneratedWorkbookPath, document.PrimarySourcePath);
            if (issue is not null) issues.Add(issue);
        }
        return issues;
    }

    private Task<BankStatementImportIssue?> CleanupGeneratedWorkbookAsync(string path, string sourcePath)
    {
        try
        {
            if (_settings.DeleteGeneratedWorkbook && path.Length > 0 && File.Exists(path)) File.Delete(path);
            return Task.FromResult<BankStatementImportIssue?>(null);
        }
        catch (Exception exception)
        {
            return Task.FromResult<BankStatementImportIssue?>(BankStatementImportIssue.Create(
                Path.GetFileName(sourcePath),
                "OCR Temporary Cleanup",
                BankImportErrorCode.P009_TemporaryCleanupFailure,
                exception.Message,
                retryable: false));
        }
    }

    private string CreateNormalizedWorkbookPath(string primarySource, int documentNumber)
    {
        if (_settings.DeleteGeneratedWorkbook)
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "BankOCR");
            Directory.CreateDirectory(tempFolder);
            return Path.Combine(tempFolder, $"AAH_BankOCR_PROD_{Guid.NewGuid():N}_{documentNumber:000}.xlsx");
        }
        var directory = Path.GetDirectoryName(primarySource) ?? Path.GetTempPath();
        var stem = Sanitize(Path.GetFileNameWithoutExtension(primarySource));
        return Path.Combine(directory, $"{stem}_OCR_VERIFIED_{DateTime.Now:yyyyMMdd_HHmmss}_{documentNumber:000}.xlsx");
    }

    private static void WritePipelineWorkbook(string path, BankVerifiedDocument document, string verificationAuditPath)
    {
        var transactions = document.Transactions.Select(row => new[]
        {
            row.Date?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? row.DateRaw,
            row.ValueDate?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? row.ValueDateRaw,
            row.Description,
            row.Reference,
            row.FinalDebit > 0m ? row.FinalDebit.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty,
            row.FinalCredit > 0m ? row.FinalCredit.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty,
            // Keep both conventional layouts available to the existing
            // importer. Amount and Dr/Cr are derived from the same audited
            // debit/credit movement; never re-OCR or invent a direction.
            row.FinalDebit > 0m && row.FinalCredit == 0m
                ? row.FinalDebit.ToString("0.00", CultureInfo.InvariantCulture)
                : row.FinalCredit > 0m && row.FinalDebit == 0m
                    ? row.FinalCredit.ToString("0.00", CultureInfo.InvariantCulture)
                    : string.Empty,
            row.FinalDebit > 0m && row.FinalCredit == 0m ? "DR"
                : row.FinalCredit > 0m && row.FinalDebit == 0m ? "CR" : string.Empty,
            FormatSignedBalance(row.Balance),
            string.Join(" | ", new[]
            {
                row.DateRaw, row.ValueDateRaw, row.Description, row.Reference, row.DebitRaw, row.CreditRaw, row.BalanceRaw,
                $"OCR_STATUS={row.VerificationStatus}",
                row.CorrectionProof.Length > 0 ? $"PROOF={row.CorrectionProof}" : string.Empty,
                $"SOURCE_PAGE={row.InferredPage}"
            }.Where(value => value.Length > 0))
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Path.GetTempPath());
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        WriteEntry(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>");
        WriteEntry(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        WriteEntry(archive, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Bank Statement\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        WriteEntry(archive, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "</Relationships>");

        var rows = new List<string[]>
        {
            new[] { "Source File", string.Join("; ", document.SourcePaths.Select(Path.GetFileName)) },
            new[] { "Bank", document.BankName.Length > 0 ? document.BankName : "Unknown Bank" },
            new[] { "Account Name", document.AccountName },
            new[] { "Account Number", document.AccountNumber.Length > 0 ? document.AccountNumber : "Unknown" },
            new[] { "Account Type", document.AccountType },
            new[] { "Branch", document.BranchName },
            new[] { "IFSC", document.Ifsc },
            new[] { "Statement Start Date", document.StatementFrom },
            new[] { "Statement End Date", document.StatementTo },
            new[] { "Opening Balance", FormatSignedBalance(document.OpeningBalance) },
            new[] { "Opening Source", document.OpeningSource },
            new[] { "Printed Statement Opening", FormatSignedBalance(document.PrintedSummaryOpeningBalance) },
            new[] { "Brought Forward Debit", document.BroughtForwardDebit.ToString("0.00", CultureInfo.InvariantCulture) },
            new[] { "Brought Forward Credit", document.BroughtForwardCredit.ToString("0.00", CultureInfo.InvariantCulture) },
            new[] { "Closing Balance", FormatSignedBalance(document.ClosingBalance) },
            new[] { "Format ID", "AAH-PADDLEOCR-VL16-PRODUCTION" },
            new[] { "Transaction Count", transactions.Count.ToString(CultureInfo.InvariantCulture) },
            new[] { "OCR Engine", "PaddleOCR-VL 1.6 local GGUF + llama.cpp (raw recognition only)" },
            new[] { "OCR Verification", "Accounting Automation Hub production document verifier" },
            new[] { "OCR Overall Status", document.Status },
            new[] { "OCR Import Authorization", IsVerified(document.Status) ? "ELIGIBLE_FOR_NORMAL_BANK_IMPORT" : "REVIEW_ONLY_NO_IMPORT" },
            new[] { "OCR Page Status", document.PageStatus },
            new[] { "OCR Summary Status", document.SummaryStatus },
            new[] { "OCR Corrections Applied", document.Corrections.Count.ToString(CultureInfo.InvariantCulture) },
            new[] { "OCR Verification Issues", document.Issues.Count.ToString(CultureInfo.InvariantCulture) },
            new[] { "OCR Debit Count", document.DebitCount.ToString(CultureInfo.InvariantCulture) },
            new[] { "OCR Credit Count", document.CreditCount.ToString(CultureInfo.InvariantCulture) },
            new[] { "OCR Total Debits", document.TotalDebits.ToString("0.00", CultureInfo.InvariantCulture) },
            new[] { "OCR Total Credits", document.TotalCredits.ToString("0.00", CultureInfo.InvariantCulture) },
            new[] { "Verification Audit", verificationAuditPath },
            new[] { "Fallback OCR", "NONE" },
            new[] { "Cloud OCR/API", "NONE" },
            new[] { "", "" },
            new[] { "Date", "Value Date", "Narration", "Reference", "Debit", "Credit", "Amount", "Dr/Cr", "Balance", "Raw Data" }
        };
        rows.AddRange(transactions);

        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var excelRow = rowIndex + 1;
            sheet.Append("<row r=\"").Append(excelRow.ToString(CultureInfo.InvariantCulture)).Append("\">");
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Length; columnIndex++)
            {
                var value = rows[rowIndex][columnIndex] ?? string.Empty;
                if (value.Length == 0) continue;
                var reference = ExcelColumnName(columnIndex + 1) + excelRow.ToString(CultureInfo.InvariantCulture);
                sheet.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                    .Append(XmlEscape(value))
                    .Append("</t></is></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");
        WriteEntry(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    private static void CleanupOldOcrRuns()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Accounting Automation Hub",
                "BankOCR",
                "Runs");
            if (!Directory.Exists(root)) return;

            var directories = new DirectoryInfo(root).GetDirectories()
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .ToArray();
            var cutoff = DateTime.UtcNow.AddDays(-7);
            for (var index = 0; index < directories.Length; index++)
            {
                var directory = directories[index];
                // Retain the newest 20 runs for diagnostics even if a busy workstation
                // creates many batches; older runs are deleted after seven days. Runtime
                // and model folders are outside Runs and are never touched here.
                if (index < 20 || directory.LastWriteTimeUtc >= cutoff) continue;
                try { directory.Delete(recursive: true); } catch { }
            }
        }
        catch { }
    }

    private static bool IsVerified(string status) =>
        status.Equals("VERIFIED", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("VERIFIED_WITH_CORRECTIONS", StringComparison.OrdinalIgnoreCase);

    private static string FormatSignedBalance(decimal? value)
    {
        if (value is null) return string.Empty;
        var suffix = value.Value < 0m ? "DR" : "CR";
        return $"{Math.Abs(value.Value).ToString("0.00", CultureInfo.InvariantCulture)} {suffix}";
    }

    private static string ExcelColumnName(int column)
    {
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return builder.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string XmlEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!XmlConvert.IsXmlChar(ch)) continue;
            builder.Append(ch switch
            {
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&apos;", _ => ch.ToString()
            });
        }
        return builder.ToString();
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}

public static class BankAdvancedOcrRuntimeLocator
{
    private static readonly object AutoSearchSync = new();
    private static string _cachedAutoRoot = string.Empty;

    public static BankAdvancedOcrRuntimeInfo Resolve(string configuredRoot = "")
    {
        var candidates = new List<string>();
        AddCandidate(candidates, configuredRoot);
        AddCandidate(candidates, Environment.GetEnvironmentVariable("AAH_BANK_OCR_HOME"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Runtime discovery belongs to Banking only. Shared consumers never call Resolve().
        foreach (var candidate in candidates)
        {
            var info = Build(candidate);
            if (info.IsReady) return info;
        }

        BankAdvancedOcrRuntimeInfo? auto = null;
        lock (AutoSearchSync)
        {
            // Cache only a runtime that actually proved ready. A failed discovery is never
            // remembered because Bank OCR is lazy and its external folder may appear or be
            // configured only when the next image job starts.
            if (_cachedAutoRoot.Length > 0)
            {
                auto = Build(_cachedAutoRoot);
                if (!auto.IsReady)
                {
                    _cachedAutoRoot = string.Empty;
                    auto = null;
                }
            }

            if (auto is null)
            {
                auto = TryBoundedDownloadsSearch(userProfile);
                if (auto is not null && auto.IsReady)
                    _cachedAutoRoot = auto.RootFolder;
            }
        }
        if (auto is not null && auto.IsReady) return auto;

        var preferred = candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var invalid = preferred.Length > 0 ? Build(preferred) : null;
        return new BankAdvancedOcrRuntimeInfo
        {
            RootFolder = preferred,
            FailureMessage = invalid?.FailureMessage.Length > 0
                ? invalid.FailureMessage
                : "PaddleOCR-VL 1.6 production runtime was not found. Select the external folder containing _runtime\\Python313\\python.exe, _llama_runtime\\llama-server.exe and the two PaddleOCR-VL 1.6 GGUF model files. External OCR worker/verifier scripts are not required."
        };
    }

    public static string AutoDetectRoot()
    {
        var info = Resolve();
        return info.IsReady ? info.RootFolder : string.Empty;
    }

    public static BankAdvancedOcrRuntimeInfo Inspect(string root) => Build(root);

    private static BankAdvancedOcrRuntimeInfo? TryBoundedDownloadsSearch(string userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile)) return null;
        var downloads = Path.Combine(userProfile, "Downloads");
        if (!Directory.Exists(downloads)) return null;
        try
        {
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((downloads, 0));
            var visited = 0;
            while (queue.Count > 0 && visited < 2500)
            {
                var (path, depth) = queue.Dequeue();
                visited++;
                var info = Build(path);
                if (info.IsReady) return info;
                if (depth >= 4) continue;
                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(path).Take(250).ToArray(); }
                catch { continue; }
                foreach (var child in children) queue.Enqueue((child, depth + 1));
            }
        }
        catch { }
        return null;
    }

    private static BankAdvancedOcrRuntimeInfo Build(string? root)
    {
        root = (root ?? string.Empty).Trim().Trim('"');
        if (root.Length == 0) return new BankAdvancedOcrRuntimeInfo();
        try { root = Path.GetFullPath(root); } catch { }

        var python = Path.Combine(root, "_runtime", "Python313", "python.exe");
        var modelProfile = Path.Combine(root, "_model_profile");
        var llama = Path.Combine(root, "_llama_runtime", "llama-server.exe");
        var model = Path.Combine(root, "_llama_models", "PaddleOCR-VL-1.6-GGUF.gguf");
        var mmproj = Path.Combine(root, "_llama_models", "PaddleOCR-VL-1.6-GGUF-mmproj.gguf");
        var missing = new List<string>();
        if (!File.Exists(python)) missing.Add("_runtime\\Python313\\python.exe");
        if (!File.Exists(llama)) missing.Add("_llama_runtime\\llama-server.exe");
        if (!File.Exists(model)) missing.Add("_llama_models\\PaddleOCR-VL-1.6-GGUF.gguf");
        if (!File.Exists(mmproj)) missing.Add("_llama_models\\PaddleOCR-VL-1.6-GGUF-mmproj.gguf");

        // The standalone Bank OCR worker creates _model_profile itself. Treating an
        // absent cache directory as a missing runtime component made Mobile Invoice
        // reject an otherwise complete, already-working Bank OCR installation.
        if (missing.Count == 0 && !Directory.Exists(modelProfile))
        {
            try { Directory.CreateDirectory(modelProfile); }
            catch (Exception exception)
            {
                missing.Add("_model_profile could not be created: " + exception.Message);
            }
        }

        return new BankAdvancedOcrRuntimeInfo
        {
            RootFolder = root,
            PythonExecutable = python,
            LlamaServer = llama,
            MainModel = model,
            VisionProjector = mmproj,
            IsReady = missing.Count == 0,
            FailureMessage = missing.Count == 0
                ? string.Empty
                : $"Advanced OCR folder is incomplete. Missing production runtime component(s): {string.Join(", ", missing)}. External OCR worker/verifier scripts are intentionally not required."
        };
    }

    private static void AddCandidate(ICollection<string> values, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        var value = candidate.Trim().Trim('"');
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
    }
}

public sealed class BankAdvancedOcrRuntimeInfo
{
    public bool IsReady { get; set; }
    public string RootFolder { get; set; } = string.Empty;
    public string PythonExecutable { get; set; } = string.Empty;
    public string LlamaServer { get; set; } = string.Empty;
    public string MainModel { get; set; } = string.Empty;
    public string VisionProjector { get; set; } = string.Empty;
    public string FailureMessage { get; set; } = string.Empty;
}

internal sealed class AdvancedBankOcrSettings
{
    public string ExternalRootFolder { get; set; } = string.Empty;
    public int TimeoutMinutes { get; set; } = 120;
    public bool DeleteGeneratedWorkbook { get; set; } = true;

    public static AdvancedBankOcrSettings Load()
    {
        var settings = new AdvancedBankOcrSettings();
        try
        {
            var repository = new AccountingAutomationHub.Infrastructure.Settings.SqliteApplicationPreferenceRepository();
            var preferences = new ApplicationPreferenceService(repository);
            settings.ExternalRootFolder = preferences.Get(ApplicationSettingKeys.BankOcrExternalRoot).Trim();
            settings.TimeoutMinutes = Math.Clamp(preferences.GetInt(ApplicationSettingKeys.BankOcrTimeoutMinutes, 120), 5, 720);
            settings.DeleteGeneratedWorkbook = preferences.GetBool(ApplicationSettingKeys.PdfDeleteGeneratedWorkbook);
        }
        catch { }
        return settings;
    }
}

public sealed class BankOcrBatchConversionResult
{
    public bool WasSuccessful { get; set; }
    public string LogPath { get; set; } = string.Empty;
    public string StatusPath { get; set; } = string.Empty;
    public string ExternalRuntimeRoot { get; set; } = string.Empty;
    public string ExternalBatchOutputDirectory { get; set; } = string.Empty;
    public string VerificationAuditPath { get; set; } = string.Empty;
    public IReadOnlyList<BankOcrDocumentConversionResult> Documents { get; set; } = [];
    public int SourceErrorCount { get; set; }
    public int ReviewCount { get; set; }
    public IReadOnlyList<string> ConversionWarnings { get; set; } = [];
    public BankStatementImportIssue? Issue { get; set; }

    public static BankOcrBatchConversionResult Failed(BankImportErrorCode code, string message, string logPath = "") =>
        new()
        {
            LogPath = logPath,
            Issue = BankStatementImportIssue.Create("Advanced Bank OCR", "PaddleOCR-VL 1.6 production recognition + AAH verification", code, message),
            WasSuccessful = false
        };
}

public sealed class BankOcrDocumentConversionResult
{
    public int DocumentNumber { get; set; }
    public IReadOnlyList<string> SourcePaths { get; set; } = [];
    public string PrimarySourcePath { get; set; } = string.Empty;
    public string GeneratedWorkbookPath { get; set; } = string.Empty;
    public string VerifiedExternalWorkbookPath { get; set; } = string.Empty;
    public string VerificationAuditPath { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = string.Empty;
    public string PageStatus { get; set; } = string.Empty;
    public string SummaryStatus { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int TransactionRowsPrepared { get; set; }
    public int CorrectionsApplied { get; set; }
    public int VerificationIssueCount { get; set; }
}

public sealed class BankOcrConversionResult
{
    public string SourcePath { get; set; } = string.Empty;
    public IReadOnlyList<string> SourcePaths { get; set; } = [];
    public string GeneratedWorkbookPath { get; set; } = string.Empty;
    public string VerifiedExternalWorkbookPath { get; set; } = string.Empty;
    public string VerificationAuditPath { get; set; } = string.Empty;
    public string ExternalBatchOutputDirectory { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
    public string ExistingWorkbookBackupPath { get; set; } = string.Empty;
    public bool WasSuccessful { get; set; }
    public int OcrPageCount { get; set; }
    public int OcrLineCount { get; set; }
    public int TransactionRowsPrepared { get; set; }
    public string VerificationStatus { get; set; } = string.Empty;
    public string PageStatus { get; set; } = string.Empty;
    public string SummaryStatus { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public int CorrectionsApplied { get; set; }
    public int VerificationIssueCount { get; set; }
    public BankStatementImportIssue? Issue { get; set; }

    public static BankOcrConversionResult Failed(string sourcePath, BankImportErrorCode code, string message, string logPath = "", string? backupPath = null) =>
        new()
        {
            SourcePath = sourcePath,
            SourcePaths = [sourcePath],
            LogPath = logPath,
            ExistingWorkbookBackupPath = backupPath ?? string.Empty,
            Issue = BankStatementImportIssue.Create(Path.GetFileName(sourcePath), "Advanced Bank OCR -> AAH Verified Excel", code, message),
            WasSuccessful = false
        };
}
