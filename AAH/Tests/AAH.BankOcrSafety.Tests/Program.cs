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
                : "HDFC BANK\nStatement of Account\nAccount No: 9999999999" },
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
    // A bank can print a dated BALANCE BROUGHT FORWARD credit as the first
    // line while including it in the printed bank credit count/total. AAH must
    // display it as opening metadata and create NO voucher for it.
    foreach (var test in new (string Name, string BfBalance, string SummaryCredits,
        string SummaryCreditCount, bool IncludeSummary, bool ExpectVerified)[]
    {
        ("dated opening movement included in printed summary", "0.01", "100.01", "2", true, true),
        ("opening amount is not a voucher even without balance OCR", "", "100.01", "2", false, false),
        ("wrong printed bank credit count remains blocked", "0.01", "100.01", "1", true, false),
        ("one paisa printed bank credit mismatch remains blocked", "0.01", "100.00", "2", true, false)
    })
    {
        var directory = Path.Combine(root, "brought-forward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blocks = new List<object>
        {
            new { block_label = "text", block_content =
                "HDFC BANK\nStatement of Account\nAccount No: 9999999999" },
            new { block_label = "table", block_content =
                "| Date | Narration | Chq/Ref.No. | Value Dt | Withdrawal Amt. | Deposit Amt. | Closing Balance |\n" +
                $"| 31/07/25 | BALANCE BROUGHT FORWARD | BF00001 | 31/07/25 | 0.00 | 0.01 | {test.BfBalance} |\n" +
                "| 29/08/25 | DIGITAL CREDIT | S00001 | 29/08/25 | 0.00 | 100.00 | 100.01 |\n" +
                "| 01/09/25 | DIGITAL DEBIT | S00002 | 01/09/25 | 25.00 | 0.00 | 75.01 |" }
        };
        if (test.IncludeSummary)
            blocks.Add(new { block_label = "table", block_content =
                "| Opening Balance | Dr Count | Cr Count | Debits | Credits | Closing Bal |\n" +
                $"| 0.00 | 1 | {test.SummaryCreditCount} | 25.00 | {test.SummaryCredits} | 75.01 |" });
        var path = Path.Combine(directory, "page.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { parsing_res_list = blocks }));
        var batch = new BankOcrRecognitionBatch
        {
            WasSuccessful = true,
            Pages = new[] { new BankOcrRecognitionPage
            {
                Id = "S001P001", SourcePath = "synthetic-scan.pdf",
                SourceIndex = 1, SourcePage = 1, JsonPath = path
            } }
        };
        var result = new BankImageDocumentVerificationService()
            .Verify(batch, directory, null, CancellationToken.None);
        if (result.Documents.Count != 1)
            throw new Exception(test.Name + ": expected one document.");
        var doc = result.Documents[0];
        if (doc.Transactions.Count != 2 ||
            doc.Transactions.Any(tx => tx.Description.Contains("BROUGHT FORWARD", StringComparison.Ordinal)) ||
            doc.OpeningBalance != 0.01m || doc.BroughtForwardCredit != 0.01m ||
            doc.TotalCredits != 100.00m || doc.CreditCount != 1 ||
            doc.PrintedSummaryOpeningBalance != (test.IncludeSummary ? 0.00m : null))
            throw new Exception(test.Name + ": opening was lost, duplicated, or counted as voucher. " +
                "tx=" + doc.Transactions.Count + ", opening=" + doc.OpeningBalance +
                ", bfCr=" + doc.BroughtForwardCredit + ", totalCr=" + doc.TotalCredits +
                ", crCount=" + doc.CreditCount + ", summaryOpening=" + doc.PrintedSummaryOpeningBalance +
                ", first=" + doc.Transactions.FirstOrDefault()?.Description +
                ", issues=" + string.Join("; ", doc.Issues));
        var verified = doc.Status is "VERIFIED" or "VERIFIED_WITH_CORRECTIONS";
        if (verified != test.ExpectVerified)
            throw new Exception(test.Name + ": unexpected status " + doc.Status +
                " issues: " + string.Join("; ", doc.Issues));
        Console.WriteLine("PASS: " + test.Name + " (" + doc.Status + ")");
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
            new { block_label = "text", block_content = "HDFC BANK\nStatement of Account" },
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
    var identity = "HDFC BANK\nStatement of Account\nFrom: 29/08/2025 To: 03/09/2025\nAccount No: 9999999999\nAccount Branch : TEST CITY\nAccount Type : CURRENT ACCOUNT\n";
    string P1Table = "| From: 29/08/2025 | To: 03/09/2025 | Statement of account |\n" +
                     "| Date | Narration | Chq/Ref.No. | Value Dt | Withdrawal Amt. | Deposit Amt. | Closing Balance |\n" +
                     "| 29/08/25 | DIGITAL CREDIT | S00001 | 29/08/25 | 0.00 | 100.00 | 100.00 |\n" +
                     "| | FIRST TRANSACTION SECOND LINE | | | | | |\n" +
                     "| 01/09/25 | DIGITAL DEBIT | S00002 | 01/09/25 | 25.00 | 0.00 | 75.00 |";
    string P2Table = "| Generated On: 03/09/2025 - Statement of account |\n" +
                     "| 02/09/25 | CONTINUATION CREDIT\nADDITIONAL DETAIL | S00003 | 02/09/25 | 0.00 | 10.00 | 85.00 |\n" +
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
            new { block_label = "text", block_content = "MS TEST TRADERS" },
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
    var tabularAudit = Path.Combine(continuationDir, "AAH_OCR_TABULAR_NORMALIZATION.json");
    if (!File.Exists(tabularAudit))
        throw new Exception("OCR normalization stage did not save pre-import tabular evidence.");
    using (var normalizedEvidence = JsonDocument.Parse(File.ReadAllText(tabularAudit)))
    {
        var evidence = normalizedEvidence.RootElement;
        if (evidence.GetProperty("physical_pages").GetInt32() != 2 ||
            evidence.GetProperty("candidate_rows").GetInt32() != 4 ||
            evidence.GetProperty("pages")[1].GetProperty("rows").GetArrayLength() != 2 ||
            !evidence.GetProperty("pages")[1].GetProperty("source_columns").TryGetProperty("description", out _))
            throw new Exception("Source-backed OCR table normalization lost headerless page-two rows.");
    }
    if (combined.Status != "VERIFIED" || combined.Transactions.Count != 4)
        throw new Exception("Headerless continuation was not fully verified: " + combined.Status + " (" +
                            string.Join("; ", combined.Issues) + ")");
    if (combined.Transactions[2].InferredPage != 2 || combined.Transactions[2].Reference != "S00003" ||
        combined.Transactions[3].Reference != "S00004")
        throw new Exception("Page-two transaction reference mapping or ordering is incorrect.");
    if (combined.BankName != "HDFC BANK" || combined.AccountNumber != "9999999999" ||
        combined.AccountName != "MS TEST TRADERS" || combined.BranchName != "TEST CITY" ||
        combined.AccountType != "CA")
        throw new Exception("Source-labeled account metadata was not preserved in the verified bank OCR document.");
    if (!combined.Transactions[0].Description.Contains("FIRST TRANSACTION SECOND LINE", StringComparison.Ordinal) ||
        !combined.Transactions[2].Description.Contains("CONTINUATION CREDIT ADDITIONAL DETAIL", StringComparison.Ordinal))
        throw new Exception("Wrapped OCR narration was lost between raw table rows or embedded line breaks.");
    if (combined.TotalDebits != 45.00m || combined.TotalCredits != 110.00m ||
        combined.ClosingBalance != 65.00m || combined.DebitCount != 2 || combined.CreditCount != 2)
        throw new Exception("Page-two financial reconciliation is incorrect.");
    Console.WriteLine("PASS: scanned PDF with metadata before header and metadata before headerless continuation verified.");
    // Regression from live Bank OCR: the page-one footer's GSTN has
    // an embedded "AC123456" sequence. A previous permissive account regex
    // treated it as an account number, splitting a two-page scanned statement
    // from the source-labeled account number on page two. No private GSTN or
    // bank account number is stored in this public synthetic fixture.
    var gstnFolder = Path.Combine(root, "gstn-footer-does-not-split-statement");
    Directory.CreateDirectory(gstnFolder);
    var gstnPage1 = Path.Combine(gstnFolder, "first.json");
    var gstnPage2 = Path.Combine(gstnFolder, "second.json");
    File.WriteAllText(gstnPage1, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = "HDFC BANK\nPage No. 1 of 2\n" },
            new { block_label = "text", block_content = "MS NORTHERN ALPHANUMERIC TRADERS" },
            new { block_label = "vision_footnote", block_content = "Account branch GSTN:29AAAAC123456H1ZW" },
            new { block_label = "text", block_content = "50200000000000 OTHER" },
            new { block_label = "table", block_content = P1Table }
        }
    }));
    File.WriteAllText(gstnPage2, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = "HDFC BANK\nPage No. 2 of 2\nAccount No : 50200000000000 OTHER\nAccount Branch : TEST CITY" },
            new { block_label = "text", block_content = "MS NORTHERN ALPHANUMERK TRADERS" },
            new { block_label = "table", block_content = P2Table },
            new { block_label = "table", block_content = P2Summary }
        }
    }));
    var gstnBatch = new BankOcrRecognitionBatch
    {
        WasSuccessful = true,
        Pages = new[]
        {
            new BankOcrRecognitionPage { Id="S001P001", SourcePath=twoPagePdf, SourceIndex=1, SourcePage=1, JsonPath=gstnPage1 },
            new BankOcrRecognitionPage { Id="S001P002", SourcePath=twoPagePdf, SourceIndex=1, SourcePage=2, JsonPath=gstnPage2 }
        }
    };
    var gstnResult = new BankImageDocumentVerificationService().Verify(gstnBatch, gstnFolder, null, CancellationToken.None);
    if (!gstnResult.WasSuccessful || gstnResult.Documents.Count != 1 ||
        gstnResult.Documents[0].Pages.Count != 2 ||
        gstnResult.Documents[0].Transactions.Count != 4 ||
        gstnResult.Documents[0].AccountNumber != "50200000000000" ||
        gstnResult.Documents[0].Transactions[2].InferredPage != 2 ||
        gstnResult.Documents[0].Issues.Any(issue => issue.Contains("Conflicting account", StringComparison.OrdinalIgnoreCase)))
        throw new Exception("An embedded GSTN substring split source-adjacent PDF pages or lost page-two transaction mapping: " +
            string.Join("; ", gstnResult.Documents.SelectMany(document => document.Issues)));
    using (var tabularEvidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(gstnFolder, "AAH_OCR_TABULAR_NORMALIZATION.json"))))
    {
        var pages = tabularEvidence.RootElement.GetProperty("pages");
        if (pages.GetArrayLength() != 2 ||
            pages[0].GetProperty("AccountNumber").GetString() != "" ||
            pages[1].GetProperty("AccountNumber").GetString() != "50200000000000" ||
            pages[1].GetProperty("rows").GetArrayLength() != 2)
            throw new Exception("Source account evidence or second-page rectangular transactions not preserved.");
    }
    Console.WriteLine("PASS: GSTN footer is not a bank account; two-page PDF is grouped with four transaction rows.");
    if (gstnResult.Documents[0].Issues.Any(issue => issue.Contains("account-holder name differs", StringComparison.OrdinalIgnoreCase)))
        throw new Exception("Tiny source-page OCR account-holder spelling variation blocked one physical bank PDF.");

    // A genuinely different holder name remains review-required. Never apply
    // fuzzy matching to unrelated documents, bank accounts or ledger identity.
    var conflictingNamePage = Path.Combine(gstnFolder, "second-conflicting-name.json");
    File.WriteAllText(conflictingNamePage,
        File.ReadAllText(gstnPage2).Replace("MS NORTHERN ALPHANUMERK TRADERS", "MS UNRELATED DIFFERENT PERSON", StringComparison.Ordinal));
    var conflictBatch = new BankOcrRecognitionBatch
    {
        WasSuccessful = true,
        Pages = new[]
        {
            new BankOcrRecognitionPage { Id="S001P001", SourcePath=twoPagePdf, SourceIndex=1, SourcePage=1, JsonPath=gstnPage1 },
            new BankOcrRecognitionPage { Id="S001P002", SourcePath=twoPagePdf, SourceIndex=1, SourcePage=2, JsonPath=conflictingNamePage }
        }
    };
    var nameConflict = new BankImageDocumentVerificationService()
        .Verify(conflictBatch, gstnFolder, null, CancellationToken.None).Documents.Single();
    if (nameConflict.Status != "REVIEW_REQUIRED" ||
        !nameConflict.Issues.Any(issue => issue.Contains("account-holder name differs", StringComparison.OrdinalIgnoreCase)))
        throw new Exception("Unrelated OCR account-holder names must still block automatic import.");
    Console.WriteLine("PASS: source name OCR variants do not block, unrelated holder names still require review.");

    // The real HDFC scan can return a headerless page with date OCR as
    // 14:08/26 and a separate HTML row whose date cell is rowspan=2.
    // The leading date/narration row has no money, the second contains it.
    // Both are one source transaction, never an inferred/duplicate voucher.
    var splitHtmlDir = Path.Combine(root, "ocr-html-rowspan-colon-date");
    Directory.CreateDirectory(splitHtmlDir);
    var splitFirstJson = Path.Combine(splitHtmlDir, "first.json");
    var splitSecondJson = Path.Combine(splitHtmlDir, "second.json");
    File.WriteAllText(splitFirstJson, File.ReadAllText(page1Json));
    var htmlPageTwo = "<table>" +
        "<tr><td colspan='7'>Generated On:03-SEP-2025 Statement of account</td></tr>" +
        "<tr><td>02:09/25</td><td>CONTINUATION CREDIT</td><td>S00003</td><td>02/09/25</td><td>0.00</td><td>10.00</td><td>85.00</td></tr>" +
        "<tr><td rowspan='2'>03:09/25</td><td>CONTINUATION DEBIT</td><td>S00004</td><td></td><td></td><td></td><td></td></tr>" +
        "<tr><td>ADDITIONAL DETAIL</td><td>S00004</td><td>03/09/25</td><td>20.00</td><td>0.00</td><td>65.00</td></tr>" +
        "</table>";
    File.WriteAllText(splitSecondJson, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = identity + "Page No. 2 of 2" },
            new { block_label = "table", block_content = htmlPageTwo },
            new { block_label = "table", block_content = P2Summary }
        }
    }));
    var splitResult = new BankImageDocumentVerificationService().Verify(
        new BankOcrRecognitionBatch
        {
            WasSuccessful = true,
            Pages = new[]
            {
                new BankOcrRecognitionPage { Id = "S001P001", SourcePath = twoPagePdf, SourceIndex = 1, SourcePage = 1, JsonPath = splitFirstJson },
                new BankOcrRecognitionPage { Id = "S001P002", SourcePath = twoPagePdf, SourceIndex = 1, SourcePage = 2, JsonPath = splitSecondJson }
            }
        }, splitHtmlDir, null, CancellationToken.None);
    if (splitResult.Documents.Count != 1 || splitResult.Documents[0].Pages.Count != 2 ||
        splitResult.Documents[0].Transactions.Count != 4 ||
        splitResult.Documents[0].Transactions[2].Date?.Day != 2 ||
        splitResult.Documents[0].Transactions[3].Date?.Day != 3 ||
        splitResult.Documents[0].Transactions[3].FinalDebit != 20.00m ||
        !splitResult.Documents[0].Transactions[3].Description.Contains("ADDITIONAL DETAIL", StringComparison.Ordinal))
        throw new Exception("HTML rowspan or colon-date normalization lost/duplicated a headerless continuation transaction: " +
            string.Join("; ", splitResult.Documents.SelectMany(d => d.Issues)));
    using (var splitEvidence = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(splitHtmlDir, "AAH_OCR_TABULAR_NORMALIZATION.json"))))
    {
        var evidence = splitEvidence.RootElement;
        if (evidence.GetProperty("candidate_rows").GetInt32() != 4 ||
            evidence.GetProperty("pages")[1].GetProperty("rows").GetArrayLength() != 2)
            throw new Exception("HTML shared-date rows did not normalize to one transaction per movement.");
    }
    Console.WriteLine("PASS: HTML rowspans and source OCR colon-date separators preserve four financial rows.");



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

    // In a real scan the opening row may have a printed date and the OCR can
    // shift its balance into the deposit column. The unknown opening must be
    // retained as an audit failure, not invented from the deposit amount.
    var damagedOpeningRows = p1Rows.Select(line => line.Contains("BALANCE BROUGHT FORWARD", StringComparison.Ordinal)
        ? "| 31/07/25 | BALANCE BROUGHT FORWARD | BF00000 | 31/07/25 | 0.00 | 0.01 | |"
        : line);
    var damagedOpeningJson = Path.Combine(longDir, "page1-damaged-opening.json");
    File.WriteAllText(damagedOpeningJson, JsonSerializer.Serialize(new
    {
        parsing_res_list = new[]
        {
            new { block_label = "text", block_content = longIdentity + "Page No. 1 of 2" },
            new { block_label = "table", block_content = HtmlTableWithStatementMetadata(damagedOpeningRows, "From: 01/07/2025 To: 30/09/2026 Statement of account") }
        }
    }));
    var damagedOpeningBatch = new BankOcrRecognitionBatch
    {
        WasSuccessful = true,
        Pages = new[]
        {
            new BankOcrRecognitionPage { Id = "S001P001", SourcePath = twoPagePdf, SourceIndex = 1, SourcePage = 1, JsonPath = damagedOpeningJson },
            new BankOcrRecognitionPage { Id = "S001P002", SourcePath = twoPagePdf, SourceIndex = 1, SourcePage = 2, JsonPath = longPage2 }
        }
    };
    var damagedOpeningDoc = new BankImageDocumentVerificationService()
        .Verify(damagedOpeningBatch, longDir, null, CancellationToken.None).Documents.Single();
    // A source-visible dated brought-forward credit belongs to the opening
    // section, not to a transaction. With an unreadable balance and summary
    // mismatch it must remain review-only; never create a voucher for it.
    if (damagedOpeningDoc.Status != "REVIEW_REQUIRED" ||
        damagedOpeningDoc.Transactions.Count != 22 ||
        damagedOpeningDoc.BroughtForwardCredit != 0.01m ||
        damagedOpeningDoc.OpeningBalance != 0.01m ||
        damagedOpeningDoc.OpeningSource != "BROUGHT_FORWARD_MOVEMENT_PENDING_BALANCE_PROOF" ||
        damagedOpeningDoc.Transactions.Any(tx => tx.Description.Contains("BROUGHT FORWARD", StringComparison.Ordinal)))
        throw new Exception("The dated 0.01 brought-forward credit must be opening metadata, never a voucher or auto-approved without proof.");
    Console.WriteLine("PASS: dated 0.01 brought-forward amount held in opening metadata with missing balance proof.");
    Console.WriteLine($"PASS: {passed + 7}/{scenarios.Length + 11} Bank OCR verifier safety scenarios.");
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
