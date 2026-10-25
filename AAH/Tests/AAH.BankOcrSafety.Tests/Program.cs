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
    ("one-paisa printed summary discrepancy blocked", false, false, false, false, false),
    ("no summary and unanchored opening blocked", false, false, false, false, false),
    ("no summary with plausible correction blocked", false, false, false, false, false),
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
        var debitAsRead = scene.Name == "no summary with plausible correction blocked" ? "2.50" : "25.00";
        rows.Add($"| 01/09/25 | BANK CHARGES | CDT123456789 | 01/09/25 | {debitAsRead} | 0.00 | 75.00 |");

        var summaryHeader = "| Opening Balance | Dr Count | Cr Count | Debits | Credits | Closing Bal |";
        var summaryData = scene.MissingSummary
            ? "| 0.00 | 1 | 1 | 25.00 | 100.00 | |"
            : scene.Name == "one-paisa printed summary discrepancy blocked"
                ? "| 0.00 | 1 | 1 | 25.00 | 100.01 | 75.00 |"
                : "| 0.00 | 1 | 1 | 25.00 | 100.00 | 75.00 |";
        var blocks = new[]
        {
            new { block_label = "text", block_content = scene.Name == "out-of-period posting date blocked"
                ? string.Join(Environment.NewLine, "HDFC BANK", "Statement of Account", "From: 01/08/2025 To: 30/08/2025", "Account No: 9999999999")
                : "HDFC BANK\\nStatement of Account\\nAccount No: 9999999999" },
            new { block_label = "table", block_content = string.Join("\n", rows) },
            new {
                block_label = scene.Name.StartsWith("no summary", StringComparison.Ordinal) ? "text" : "table",
                block_content = scene.Name.StartsWith("no summary", StringComparison.Ordinal) ? "" : summaryHeader + "\n" + summaryData
            },
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
    // The real scanned HDFC summary is printed at the bottom of the last page.
    // PaddleOCR may emit the same printed six financial controls as a text block
    // or as prose, not a TABLE block. Parsing must preserve their source values,
    // and a single bad digit must still block accounting.
    var textSummaryCases = new (string Name, string SummaryText, bool ExpectedVerified, string ExpectedSummaryStatus)[]
    {
        ("Markdown summary mislabeled text",
            "STATEMENT SUMMARY\n| Opening Balance | Dr Count | Cr Count | Debits | Credits | Closing Bal |\n| 0.00 | 1 | 1 | 25.00 | 100.00 | 75.00 |",
            true, "VERIFIED"),
        ("Plain printed summary six aligned values",
            "STATEMENT SUMMARY\nOpening Balance Dr Count Cr Count Debits Credits Closing Bal\n0.00 1 1 25.00 100.00 75.00",
            true, "VERIFIED"),
        ("Plain summary one paisa mismatch rejected",
            "STATEMENT SUMMARY\nOpening Balance Dr Count Cr Count Debits Credits Closing Bal\n0.00 1 1 25.00 100.01 75.00",
            false, "REVIEW_REQUIRED"),
        ("Damaged printed amount cannot be inferred",
            "STATEMENT SUMMARY\nOpening Balance Dr Count Cr Count Debits Credits Closing Bal\n0.00 1 1 25.O0 100.00 75.00",
            false, "NOT_AVAILABLE"),
    };
    foreach (var scene in textSummaryCases)
    {
        var directory = Path.Combine(root, "summary-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, "page.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            parsing_res_list = new[]
            {
                new { block_label = "text", block_content = "HDFC BANK\nStatement of Account\nAccount No: 9999999999" },
                new { block_label = "table", block_content =
                    "| Date | Narration | Chq/Ref.No. | Value Dt | Withdrawal Amt. | Deposit Amt. | Closing Balance |\n" +
                    "| 29/08/25 | CREDIT TEST | S123 | 29/08/25 | 0.00 | 100.00 | 100.00 |\n" +
                    "| 01/09/25 | DEBIT TEST | S124 | 01/09/25 | 25.00 | 0.00 | 75.00 |" },
                new { block_label = "text", block_content = scene.SummaryText }
            }
        }));
        var recognition = new BankOcrRecognitionBatch
        {
            WasSuccessful = true,
            Pages = new[] { new BankOcrRecognitionPage
            {
                Id = "S001P001", SourcePath = "synthetic-scan.pdf",
                SourceIndex = 1, SourcePage = 1, JsonPath = jsonPath
            } }
        };
        var verifiedSummary = new BankImageDocumentVerificationService()
            .Verify(recognition, directory, null, CancellationToken.None);
        if (verifiedSummary.Documents.Count != 1)
            throw new Exception(scene.Name + ": statement grouping changed.");
        var doc = verifiedSummary.Documents[0];
        var isVerified = doc.Status is "VERIFIED" or "VERIFIED_WITH_CORRECTIONS";
        if (isVerified != scene.ExpectedVerified || doc.SummaryStatus != scene.ExpectedSummaryStatus)
            throw new Exception(scene.Name + ": actual doc=" + doc.Status +
                                " summary=" + doc.SummaryStatus +
                                " issues=" + string.Join("; ", doc.Issues));
        Console.WriteLine("PASS: " + scene.Name + " (" + doc.SummaryStatus + ")");
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

    // Realistic continuation: page 2 prints transaction rows but does not repeat
    // the transaction-table header from page 1. Mapping MUST be inherited from
    // page 1, not guessed by bank name or hardcoded cell indices.
    var continuationDir = Path.Combine(root, "two-page-continuation");
    Directory.CreateDirectory(continuationDir);
    var identity = "HDFC BANK\nStatement of Account\nFrom: 29/08/2025 To: 03/09/2025\nAccount No: 9999999999\n";
    string P1Table = "| From: 29/08/2025 | To: 03/09/2025 | Statement of account |\n" +
                     "| Date | Narration | Chq/Ref.No. | Value Dt | Withdrawal Amt. | Deposit Amt. | Closing Balance |\n" +
                     "| 29/08/25 | DIGITAL CREDIT | S00001 | 29/08/25 | 0.00 | 100.00 | 100.00 |\n" +
                     "| 01/09/25 | DIGITAL DEBIT | S00002 | 01/09/25 | 25.00 | 0.00 | 75.00 |";
    string P2Table = "| Generated On: 03/09/2025 - Statement of account |\n" +
                     "| 02/09/25 | CONTINUATION CREDIT | S00003 | 02/09/25 | 0.00 | 10.00 | 85.00 |\n" +
                     "| 03/09/25 | CONTINUATION DEBIT | S00004 | 03/09/25 | 20.00 | 0.00 | 65.00 |";
    string P2Summary = "| Opening Balance | Dr Count | Cr Count | Debits | Credits | Closing Bal |\n" +
                       "| 0.00 | 2 | 2 | 45.00 | 110.00 | 65.00 |";
    var page1Json = Path.Combine(continuationDir, "first.json");
    var page2Json = Path.Combine(continuationDir, "second.json");
    File.WriteAllText(page1Json, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = identity + "Page No. 1 of 2" },
            new { block_label = "table", block_content = P1Table }
        }
    }));
    File.WriteAllText(page2Json, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = identity + "Page No. 2 of 2" },
            new { block_label = "table", block_content = P2Table },
            new { block_label = "table", block_content = P2Summary }
        }
    }));
    var complete = new BankOcrRecognitionBatch
    {
        WasSuccessful = true,
        Pages = new[]
        {
            new BankOcrRecognitionPage { Id="S001P001", SourcePath=twoPagePdf, SourceIndex=1, SourcePage=1, JsonPath=page1Json },
            new BankOcrRecognitionPage { Id="S001P002", SourcePath=twoPagePdf, SourceIndex=1, SourcePage=2, JsonPath=page2Json }
        }
    };
    var continued = new BankImageDocumentVerificationService()
        .Verify(complete, continuationDir, null, CancellationToken.None);
    if (!continued.WasSuccessful || continued.Documents.Count != 1)
        throw new Exception("Two-page continuation was rejected or split into separate documents.");
    var combined = continued.Documents[0];
    if (combined.Status != "VERIFIED" || combined.Transactions.Count != 4)
        throw new Exception("Headerless continuation was not fully verified: " + combined.Status + " (" +
                            string.Join("; ", combined.Issues) + ")");
    if (combined.Transactions[2].InferredPage != 2 || combined.Transactions[2].Reference != "S00003" ||
        combined.Transactions[3].Reference != "S00004")
        throw new Exception("Page-two transaction reference mapping or ordering is incorrect.");
    if (combined.TotalDebits != 45.00m || combined.TotalCredits != 110.00m ||
        combined.ClosingBalance != 65.00m || combined.DebitCount != 2 || combined.CreditCount != 2)
        throw new Exception("Page-two financial reconciliation is incorrect.");
    Console.WriteLine("PASS: scanned PDF with metadata before header and metadata before headerless continuation verified.");

    // Synthetic 22-row, two-page bank statement with 16 debits and six credits,
    // no second-page header, a wrapped narration, and a printed summary.
    // The amounts and references are deliberately fictitious.
    var longDir = Path.Combine(root, "twenty-two-rows");
    Directory.CreateDirectory(longDir);
    var credits = new decimal[] { 0.05m, 15000m, 500m, 2500m, 1000m, 6000m };
    var debits = new decimal[] { 9000m, 3000m, 2000m, 1m, 0.05m, 350m, 350m,
        500m, 750m, 1000m, 900m, 600m, 450m, 400m, 500m, 5199m };
    var creditPositions = new HashSet<int> { 0, 1, 8, 11, 16, 18 };
    var p1Rows = new List<string>
    {
        "| Date | Narration | Chq/Ref.No. | Value Dt | Withdrawal Amt. | Deposit Amt. | Closing Balance |",
        // The real-world OCR often puts a date on this printed opening row.
        // It is balance evidence, never transaction 23.
        "| 31/07/25 | BALANCE BROUGHT FORWARD | BF00000 | 31/07/25 | 0.00 | 0.00 | 0.00 |"
    };
    var p2Rows = new List<string>();
    decimal running = 0m;
    int debitIndex = 0, creditIndex = 0;
    for (int i = 0; i < 22; i++)
    {
        var date = new DateTime(2025, 7, 31).AddDays(i * 19).ToString("dd/MM/yy");
        var isCredit = creditPositions.Contains(i);
        decimal debit = isCredit ? 0m : debits[debitIndex++];
        decimal credit = isCredit ? credits[creditIndex++] : 0m;
        running += credit - debit;
        string row = $"| {date} | TEST TRANSACTION {i + 1:D2} | REF{i + 1:D5} | {date} | {debit:0.00} | {credit:0.00} | {running:0.00} |";
        (i < 16 ? p1Rows : p2Rows).Add(row);
        if (i == 5)
        {
            p1Rows.Add("| | SUPPLEMENTAL UPI REFERENCE TEXT | | | | | |");
            p1Rows.Add($"| {date} | SAME DATE CONTINUATION TEXT | | | | | |");
        }
    }
    if (creditIndex != 6 || debitIndex != 16 || running != 0m)
        throw new Exception("Synthetic control construction failed.");
    var longIdentity = string.Join(Environment.NewLine, "HDFC BANK", "Statement of Account", "From: 01/07/2025 To: 30/09/2026", "Account No: 8888888888", "");
    // Real PaddleOCR-VL JSON can return a single HTML table with a merged
    // statement-metadata row before the transaction column headings, and a
    // headerless continuation page with another merged metadata row.
    static string HtmlTableWithStatementMetadata(IEnumerable<string> printedRows, string heading)
    {
        var html = new System.Text.StringBuilder("<table><tr><td colspan=\"7\">")
            .Append(System.Net.WebUtility.HtmlEncode(heading)).Append("</td></tr>");
        foreach (var printed in printedRows)
        {
            html.Append("<tr>");
            foreach (var cell in printed.Trim().Trim('|').Split('|'))
                html.Append("<td>").Append(System.Net.WebUtility.HtmlEncode(cell.Trim())).Append("</td>");
            html.Append("</tr>");
        }
        return html.Append("</table>").ToString();
    }

    var longPage1 = Path.Combine(longDir, "page1.json");
    var longPage2 = Path.Combine(longDir, "page2.json");
    File.WriteAllText(longPage1, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = longIdentity + "Page No. 1 of 2" },
            new { block_label = "table", block_content = HtmlTableWithStatementMetadata(p1Rows, "From: 01/07/2025 To: 30/09/2026 Statement of account") }
        }
    }));
    File.WriteAllText(longPage2, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            // A headerless OCR continuation can also lose the account number,
            // bank name and statement period. Same physical PDF pages must
            // still be grouped unless identity actually conflicts.
            new { block_label = "text", block_content = "Page No. 2 of 2" },
            new { block_label = "table", block_content = HtmlTableWithStatementMetadata(p2Rows, "Generated On: 30/09/2026 Statement of account") },
            new { block_label = "table", block_content = string.Join(Environment.NewLine,
                "| Opening Balance | Dr Count | Cr Count | Debits | Credits | Closing Bal |",
                "| 0.00 | 16 | 6 | 25000.05 | 25000.05 | 0.00 |") }
        }
    }));
    var longBatch = new BankOcrRecognitionBatch
    {
        WasSuccessful = true,
        Pages = new[]
        {
            new BankOcrRecognitionPage { Id = "S001P001", SourcePath = twoPagePdf, SourceIndex = 1, SourcePage = 1, JsonPath = longPage1 },
            new BankOcrRecognitionPage { Id = "S001P002", SourcePath = twoPagePdf, SourceIndex = 1, SourcePage = 2, JsonPath = longPage2 }
        }
    };
    var longResult = new BankImageDocumentVerificationService().Verify(longBatch, longDir, null, CancellationToken.None);
    if (!longResult.WasSuccessful || longResult.Documents.Count != 1)
        throw new Exception("Synthetic 22-row document was not accepted as one complete OCR batch.");
    var longDoc = longResult.Documents[0];
    if (longDoc.Status != "VERIFIED" || longDoc.Transactions.Count != 22 ||
        longDoc.DebitCount != 16 || longDoc.CreditCount != 6 ||
        longDoc.TotalDebits != 25000.05m || longDoc.TotalCredits != 25000.05m ||
        longDoc.ClosingBalance != 0m)
        throw new Exception("Synthetic 22-row financial verification failed: " +
            longDoc.Status + " (summary=" + longDoc.SummaryStatus + ", page=" + longDoc.PageStatus + ", corrections=" + longDoc.Corrections.Count + ", " + string.Join("; ", longDoc.Issues) + ").");
    if (longDoc.Transactions.Count(row => row.InferredPage == 1) != 16 ||
        longDoc.Transactions.Count(row => row.InferredPage == 2) != 6 ||
        !longDoc.Transactions[5].Description.Contains("SUPPLEMENTAL UPI REFERENCE TEXT", StringComparison.Ordinal) ||
        !longDoc.Transactions[5].Description.Contains("SAME DATE CONTINUATION TEXT", StringComparison.Ordinal))
        throw new Exception("22-row page boundary or wrapped narration handling failed.");
    Console.WriteLine("PASS: synthetic 22-row HTML/colspan two-page Bank OCR statement reconciled.");

    // The OCR model can drop an entire printed summary, leaving the raw
    // parsing_res_list without the words STATEMENT SUMMARY. A reconciled
    // two-page subset must not be auto-imported based only on opening balance.
    var p2NoSummaryJson = Path.Combine(longDir, "page2-no-summary.json");
    File.WriteAllText(p2NoSummaryJson, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = "Page No. 2 of 2" },
            new { block_label = "table", block_content = HtmlTableWithStatementMetadata(p2Rows, "Generated On: 30/09/2026 Statement of account") }
        }
    }));
    var missingSummaryBatch = new BankOcrRecognitionBatch
    {
        WasSuccessful = true,
        Pages = new[]
        {
            new BankOcrRecognitionPage { Id = "S001P001", SourcePath = twoPagePdf, SourceIndex = 1, SourcePage = 1, JsonPath = longPage1 },
            new BankOcrRecognitionPage { Id = "S001P002", SourcePath = twoPagePdf, SourceIndex = 1, SourcePage = 2, JsonPath = p2NoSummaryJson }
        }
    };
    var missingSummaryDocument = new BankImageDocumentVerificationService()
        .Verify(missingSummaryBatch, longDir, null, CancellationToken.None).Documents.Single();
    if (missingSummaryDocument.Status != "REVIEW_REQUIRED" ||
        missingSummaryDocument.SummaryStatus != "NOT_AVAILABLE" ||
        missingSummaryDocument.Transactions.Count != 22 ||
        !missingSummaryDocument.Issues.Any(issue => issue.Contains("no readable statement summary", StringComparison.OrdinalIgnoreCase)))
        throw new Exception("Unseen printed summary on multi-page scanned PDF must remain review-required without losing financial rows.");
    Console.WriteLine("PASS: multi-page OCR omitted-summary safety gate and dated brought-forward classification.");
    Console.WriteLine($"PASS: {passed + 7}/{scenarios.Length + 7} Bank OCR verifier safety scenarios.");
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
