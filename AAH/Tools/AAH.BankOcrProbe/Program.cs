using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AccountingAutomationHub.Desktop.Services;

// Real Windows-only production Bank OCR test. No importer, transaction persistence,
// accounting database updates, model installation, or external upload.
if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine("Usage: AAH.BankOcrProbe.exe <original HDFC scanned PDF>");
    return 0;
}
if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Specify the original two-page HDFC scanned statement PDF as the only argument.");
    return 64;
}
const string expectedSha = "AED20FBB1DD74FED469AA8683DBD8F603907F2164B0331333C36B94DBA07DE2D";
var input = Path.GetFullPath(args[0]);
if (!Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Refused: scanned PDF only.");
    return 64;
}
var actualSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Refused: this is not the exact HDFC source PDF verified in the user Library.");
    Console.Error.WriteLine("Actual source SHA256: " + actualSha);
    return 65;
}
if (Process.GetProcessesByName("llama-server").Any())
{
    Console.Error.WriteLine("Another llama OCR runtime appears active. No parallel OCR test was started.");
    return 66;
}
var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
if (!Directory.Exists(downloads)) downloads = Path.GetTempPath();
var output = Path.Combine(downloads, "AAH_BankOCR_Probe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
Directory.CreateDirectory(output);
Console.WriteLine("Only local artifacts are written to: " + output);
var reportFile = Path.Combine(output, "PRIVATE_PRODUCTION_OCR_RESULT.json");
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try
{
    var configuredRoot = AdvancedBankOcrSettings.Load().ExternalRootFolder;
    var runtime = BankAdvancedOcrRuntimeLocator.Resolve(configuredRoot);
    if (!runtime.IsReady)
    {
        Console.Error.WriteLine("Production OCR runtime is unavailable: " + runtime.FailureMessage);
        File.WriteAllText(Path.Combine(output, "OCR_RUNTIME_MISSING.txt"),
            "PaddleOCR-VL 1.6 installed runtime missing or misconfigured. No test performed." +
            Environment.NewLine + runtime.FailureMessage);
        return 10;
    }
    var progressFile = Path.Combine(output, "STAGES.txt");
    void Progress(string message)
    {
        Console.WriteLine(message);
        File.AppendAllText(progressFile, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
    }
    var recognized = await new BankImageOcrRecognitionService().RecognizeAsync(
        new[] { input }, runtime, output, Progress, 120, cts.Token);
    if (!recognized.WasSuccessful || recognized.SourceErrors.Count != 0 || recognized.Pages.Count != 2)
    {
        var reason = "Production PaddleOCR failed, skipped a PDF page, or returned source errors. No accounting import.";
        File.WriteAllText(Path.Combine(output, "RECOGNITION_FAILED.txt"), reason + Environment.NewLine +
            recognized.FailureMessage + Environment.NewLine + string.Join(Environment.NewLine, recognized.SourceErrors));
        Console.Error.WriteLine(reason);
        return 11;
    }
    // Exact production bank document grouping, page-order, row, balance and summary verifier.
    var verified = new BankImageDocumentVerificationService().Verify(recognized, output, Progress, cts.Token);
    var summaries = verified.Documents.Select(d => new
    {
        d.Status, d.PageStatus, d.SummaryStatus, d.StatementFrom, d.StatementTo,
        d.DebitCount, d.CreditCount, d.TotalDebits, d.TotalCredits, d.ClosingBalance,
        d.DuplicateTransactionCount, d.Issues, CorrectionCount = d.Corrections.Count,
        Transactions = d.Transactions.Select(t => new
        {
            t.Sequence, t.SourcePage, t.DateRaw, t.Date, t.ValueDateRaw, t.ValueDate,
            t.Description, t.Reference, t.DebitRaw, t.CreditRaw, t.BalanceRaw,
            t.OriginalDebit, t.OriginalCredit, t.FinalDebit, t.FinalCredit,
            t.Balance, t.VerificationStatus, t.IsCorrectionCandidate,
            t.CorrectionReason, t.CorrectionProof
        }).ToArray()
    }).ToArray();
    var financialMatch = verified.Documents.Count == 1 &&
        verified.Documents[0].Transactions.Count == 22 &&
        verified.Documents[0].DebitCount == 16 &&
        verified.Documents[0].CreditCount == 6 &&
        verified.Documents[0].TotalDebits == 18970.83m &&
        verified.Documents[0].TotalCredits == 18970.83m &&
        verified.Documents[0].ClosingBalance == 0m;
    var report = new
    {
        Schema = "AAH-BANK-PDF-REAL-OCR-PROBE-V1",
        SourceSha256 = actualSha,
        OriginalPdfPageCount = 2,
        RecognizedPages = recognized.Pages.Count,
        GroupingSucceeded = verified.WasSuccessful,
        VerifiedDocumentCount = verified.Documents.Count,
        FinancialControlsReconcile = financialMatch,
        AllFieldsCertified = false, // Narrative/date/reference source-image comparison is still required.
        ProductionImportApproved = false,
        Validation = "SOURCE_REVIEW_REQUIRED_NO_ACCOUNTING_WRITES",
        ParseIssues = verified.ParseIssues,
        RecognitionErrors = verified.RecognitionErrors,
        Documents = summaries
    };
    File.WriteAllText(reportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Original PDF pages recognized: " + recognized.Pages.Count);
    Console.WriteLine("Documents: " + verified.Documents.Count);
    Console.WriteLine("Financial controls: " + (financialMatch ? "MATCH (provisional)" : "FAILED"));
    Console.WriteLine("Full OCR field accuracy: NOT CERTIFIED; review JSON against original images.");
    Console.WriteLine("PRIVATE RESULT: " + reportFile);
    Console.WriteLine("No banking/Tally/import records were created or modified.");
    return financialMatch && verified.WasSuccessful ? 0 : 12;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Test cancelled; no import occurred.");
    return 13;
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(output, "OCR_TEST_EXCEPTION.txt"), ex.ToString());
    Console.Error.WriteLine("OCR test failed: " + ex.Message);
    return 14;
}
