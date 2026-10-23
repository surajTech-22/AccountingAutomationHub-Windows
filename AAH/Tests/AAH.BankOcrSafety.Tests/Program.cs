using System.Text.Json;
using AccountingAutomationHub.Desktop.Services;

// Compile the actual production verifier in isolation with the same OCR DTO shape.
// All test fixtures are synthetic: no bank data, passwords or customer identifiers.
var scenarios = new (string Name, bool BadDate, bool MissingNarration, bool MissingSummary, bool Wrapped, bool ExpectedVerified)[]
{
    ("valid two-digit bank dates", false, false, false, false, true),
    ("invalid OCR date blocked", true, false, false, false, false),
    ("missing narration blocked", false, true, false, false, false),
    ("incomplete statement summary blocked", false, false, true, false, false),
    ("multiline continuation preserved", false, false, false, true, true),
    ("out-of-period posting date blocked", false, false, false, false, false),
};
int passed = 0;
var root = Path.Combine(Path.GetTempPath(), "AAH-Bank-Ocr-Safety-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var scene in scenarios)
    {
        var caseDir = Path.Combine(root, passed.ToString("D2"));
        Directory.CreateDirectory(caseDir);
        var date = scene.BadDate ? "not-a-date" : "29/08/25";
        var narration = scene.MissingNarration ? "" : "UPI CUSTOMER";
        var rows = new List<string>
        {
            "| Date | Narration | Chq/Ref.No. | Value Dt | Withdrawal Amt. | Deposit Amt. | Closing Balance |",
            $"| {date} | {narration} | 000000012345 | 29/08/25 | 0.00 | 100.00 | 100.00 |"
        };
        if (scene.Wrapped)
            rows.Add("| | EXTRA NARRATION | | | | | |");
        rows.Add("| 01/09/25 | BANK CHARGES | CDT123456789 | 01/09/25 | 25.00 | 0.00 | 75.00 |");

        var summaryHeader = "| Opening Balance | Dr Count | Cr Count | Debits | Credits | Closing Bal |";
        var summaryData = scene.MissingSummary
            ? "| 0.00 | 1 | 1 | 25.00 | 100.00 | |"
            : "| 0.00 | 1 | 1 | 25.00 | 100.00 | 75.00 |";
        var blocks = new[]
        {
            new { block_label = "text", block_content = scene.Name == "out-of-period posting date blocked"
                ? string.Join(Environment.NewLine, "HDFC BANK", "Statement of Account", "From: 01/08/2025 To: 30/08/2025", "Account No: 9999999999")
                : "HDFC BANK\\nStatement of Account\\nAccount No: 9999999999" },
            new { block_label = "table", block_content = string.Join("\n", rows) },
            new { block_label = "table", block_content = summaryHeader + "\n" + summaryData },
        };
        var jsonPath = Path.Combine(caseDir, "page.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { parsing_res_list = blocks }));
        var batch = new BankOcrRecognitionBatch
        {
            WasSuccessful = true,
            Pages = new[]
            {
                new BankOcrRecognitionPage
                {
                    Id = "S001P001",
                    SourcePath = "synthetic-scan.pdf",
                    SourceIndex = 1,
                    SourcePage = 1,
                    JsonPath = jsonPath
                }
            }
        };
        var verified = new BankImageDocumentVerificationService()
            .Verify(batch, caseDir, null, CancellationToken.None);
        if (verified.Documents.Count != 1)
            throw new Exception(scene.Name + ": expected exactly one statement document.");
        var doc = verified.Documents[0];
        if (scene.Name == "out-of-period posting date blocked" &&
            (doc.StatementFrom != "01-08-2025" || doc.StatementTo != "30-08-2025"))
            throw new Exception(scene.Name + ": statement period was not parsed.");
        var isVerified = doc.Status is "VERIFIED" or "VERIFIED_WITH_CORRECTIONS";
        if (isVerified != scene.ExpectedVerified)
            throw new Exception(scene.Name + ": unexpected status " + doc.Status + ": " + string.Join("; ", doc.Issues));
        if (doc.Transactions.Count != 2)
            throw new Exception(scene.Name + ": transaction rows lost or invented.");
        if (doc.Transactions[0].Reference != "000000012345" || doc.Transactions[1].Reference != "CDT123456789")
            throw new Exception(scene.Name + ": HDFC Chq/Ref.No column was not mapped.");
        if (doc.Transactions[0].ValueDate is null || doc.Transactions[1].ValueDate is null)
            throw new Exception(scene.Name + ": HDFC Value Dt column was not parsed.");
        if (!scene.BadDate && doc.Transactions[0].Date?.Year != 2025)
            throw new Exception(scene.Name + ": two-digit bank date year was not expanded correctly.");
        if (scene.Wrapped && !doc.Transactions[0].Description.Contains("EXTRA NARRATION", StringComparison.Ordinal))
            throw new Exception(scene.Name + ": wrapped narration was not attached to parent transaction.");
        if (scene.ExpectedVerified && (doc.TotalDebits != 25.00m || doc.TotalCredits != 100.00m || doc.ClosingBalance != 75.00m))
            throw new Exception(scene.Name + ": monetary control mismatch.");
        Console.WriteLine("PASS: " + scene.Name + " (" + doc.Status + ")");
        passed++;
    }
    // Regression for the original failure mode: one page recognized from a
    // two-page bank statement must never be treated as a complete import.
    var incompleteDir = Path.Combine(root, "missing-page");
    Directory.CreateDirectory(incompleteDir);
    var twoPagePdf = Path.Combine(incompleteDir, "two-pages.pdf");
    var header = new System.Text.StringBuilder("%PDF-1.4\n");
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"
    };
    var offsets = new List<int> { 0 };
    for (var obj = 0; obj < objects.Length; obj++)
    {
        offsets.Add(System.Text.Encoding.ASCII.GetByteCount(header.ToString()));
        header.Append(obj + 1).Append(" 0 obj\n").Append(objects[obj]).Append("\nendobj\n");
    }
    var xref = System.Text.Encoding.ASCII.GetByteCount(header.ToString());
    header.Append("xref\n0 5\n0000000000 65535 f \n");
    for (var obj = 1; obj <= objects.Length; obj++)
        header.Append(offsets[obj].ToString("D10")).Append(" 00000 n \n");
    header.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
    File.WriteAllText(twoPagePdf, header.ToString(), System.Text.Encoding.ASCII);
    var incompleteJson = Path.Combine(incompleteDir, "page.json");
    File.WriteAllText(incompleteJson, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = "HDFC BANK\\nStatement of Account" },
            new { block_label = "table", block_content = "| Date | Narration | Debit | Credit | Balance |\\n| 29/08/25 | Credit | 0.00 | 10.00 | 10.00 |" },
        }
    }));
    var incomplete = new BankOcrRecognitionBatch
    {
        WasSuccessful = true,
        Pages = new[] { new BankOcrRecognitionPage
        {
            Id = "S001P001", SourcePath = twoPagePdf, SourceIndex = 1,
            SourcePage = 1, JsonPath = incompleteJson
        } }
    };
    var incompleteResult = new BankImageDocumentVerificationService()
        .Verify(incomplete, incompleteDir, null, CancellationToken.None);
    if (incompleteResult.WasSuccessful || !incompleteResult.ParseIssues.Any(issue => issue.Contains("1 of 2 physical page", StringComparison.Ordinal)))
        throw new Exception("Incomplete two-page PDF did not fail the original page count check.");
    Console.WriteLine("PASS: missing scanned PDF page blocked.");
    Console.WriteLine($"PASS: {passed + 1}/{scenarios.Length + 1} Bank OCR verifier safety scenarios.");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

namespace AccountingAutomationHub.Desktop.Services
{
    internal sealed class BankOcrRecognitionBatch
    {
        public bool WasSuccessful { get; set; }
        public IReadOnlyList<BankOcrRecognitionPage> Pages { get; set; } = [];
        public IReadOnlyList<string> SourceErrors { get; set; } = [];
    }
    internal sealed class BankOcrRecognitionPage
    {
        public string Id { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public int SourceIndex { get; set; }
        public int SourcePage { get; set; }
        public string JsonPath { get; set; } = "";
    }
}
