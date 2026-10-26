using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Production document-level verifier for scanned Bank Statements.
/// Raw OCR is complete before this class runs. Corrections are never made while a page is
/// being recognized. The verifier first groups pages, establishes logical order, propagates
/// statement identity, then proves transaction amounts from running balances and—when
/// available—the printed statement summary.
/// </summary>
internal sealed class BankImageDocumentVerificationService
{
    private static readonly string[] DateFormats = ["dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy", "dd-MM-yy", "dd/MM/yy", "dd.MM.yy", "d-M-yy", "d/M/yy"];

    private static readonly IReadOnlyDictionary<string, string[]> BankAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["STATE BANK OF INDIA"] = ["STATE BANK OF INDIA"],
            ["HDFC BANK"] = ["HDFC BANK"],
            ["ICICI BANK"] = ["ICICI BANK"],
            ["AXIS BANK"] = ["AXIS BANK"],
            ["CANARA BANK"] = ["CANARA BANK"],
            ["BANK OF BARODA"] = ["BANK OF BARODA"],
            ["BANK OF INDIA"] = ["BANK OF INDIA"],
            ["INDIAN BANK"] = ["INDIAN BANK"],
            ["INDIAN OVERSEAS BANK"] = ["INDIAN OVERSEAS BANK"],
            ["UNION BANK OF INDIA"] = ["UNION BANK OF INDIA", "UNION BANK"],
            ["PUNJAB NATIONAL BANK"] = ["PUNJAB NATIONAL BANK"],
            ["KOTAK MAHINDRA BANK"] = ["KOTAK MAHINDRA BANK", "KOTAK BANK"],
            ["IDFC FIRST BANK"] = ["IDFC FIRST BANK"],
            ["KARNATAKA BANK"] = ["KARNATAKA BANK"],
            ["FEDERAL BANK"] = ["FEDERAL BANK"],
            ["SOUTH INDIAN BANK"] = ["SOUTH INDIAN BANK"],
            ["YES BANK"] = ["YES BANK"],
            ["INDUSIND BANK"] = ["INDUSIND BANK"],
            ["RBL BANK"] = ["RBL BANK"],
            ["BANDHAN BANK"] = ["BANDHAN BANK"],
            ["AU SMALL FINANCE BANK"] = ["AU SMALL FINANCE BANK"],
            ["UJJIVAN SMALL FINANCE BANK"] = ["UJJIVAN SMALL FINANCE BANK"],
            ["EQUITAS SMALL FINANCE BANK"] = ["EQUITAS SMALL FINANCE BANK"],
            ["DBS BANK INDIA"] = ["DBS BANK INDIA", "DBS BANK"],
            ["HSBC"] = ["HSBC"],
            ["STANDARD CHARTERED BANK"] = ["STANDARD CHARTERED BANK", "STANDARD CHARTERED"],
            ["CITI BANK"] = ["CITIBANK", "CITI BANK"],
            ["CENTRAL BANK OF INDIA"] = ["CENTRAL BANK OF INDIA"],
            ["BANK OF MAHARASHTRA"] = ["BANK OF MAHARASHTRA"],
            ["UCO BANK"] = ["UCO BANK"],
            ["PUNJAB & SIND BANK"] = ["PUNJAB & SIND BANK", "PUNJAB AND SIND BANK"],
            ["JAMMU & KASHMIR BANK"] = ["JAMMU & KASHMIR BANK", "JAMMU AND KASHMIR BANK"],
            ["CSB BANK"] = ["CSB BANK", "CATHOLIC SYRIAN BANK"],
            ["DCB BANK"] = ["DCB BANK"],
            ["TAMILNAD MERCANTILE BANK"] = ["TAMILNAD MERCANTILE BANK"],
            ["KARUR VYSYA BANK"] = ["KARUR VYSYA BANK"],
            ["CITY UNION BANK"] = ["CITY UNION BANK"]
        };

    private static readonly IReadOnlyDictionary<string, string> IfscPrefixBank =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SBIN"] = "STATE BANK OF INDIA", ["HDFC"] = "HDFC BANK", ["ICIC"] = "ICICI BANK",
            ["UTIB"] = "AXIS BANK", ["CNRB"] = "CANARA BANK", ["BARB"] = "BANK OF BARODA",
            ["BKID"] = "BANK OF INDIA", ["IDIB"] = "INDIAN BANK", ["IOBA"] = "INDIAN OVERSEAS BANK",
            ["UBIN"] = "UNION BANK OF INDIA", ["PUNB"] = "PUNJAB NATIONAL BANK", ["KKBK"] = "KOTAK MAHINDRA BANK",
            ["IDFB"] = "IDFC FIRST BANK", ["KARB"] = "KARNATAKA BANK", ["FDRL"] = "FEDERAL BANK",
            ["SIBL"] = "SOUTH INDIAN BANK", ["YESB"] = "YES BANK", ["INDB"] = "INDUSIND BANK",
            ["RATN"] = "RBL BANK", ["BDBL"] = "BANDHAN BANK", ["AUBL"] = "AU SMALL FINANCE BANK",
            ["UJVN"] = "UJJIVAN SMALL FINANCE BANK", ["ESFB"] = "EQUITAS SMALL FINANCE BANK", ["DBSS"] = "DBS BANK INDIA",
            ["HSBC"] = "HSBC", ["SCBL"] = "STANDARD CHARTERED BANK", ["CITI"] = "CITI BANK",
            ["CBIN"] = "CENTRAL BANK OF INDIA", ["MAHB"] = "BANK OF MAHARASHTRA", ["UCBA"] = "UCO BANK",
            ["PSIB"] = "PUNJAB & SIND BANK", ["JAKA"] = "JAMMU & KASHMIR BANK", ["CSBK"] = "CSB BANK",
            ["DCBL"] = "DCB BANK", ["TMBL"] = "TAMILNAD MERCANTILE BANK", ["KVBL"] = "KARUR VYSYA BANK",
            ["CIUB"] = "CITY UNION BANK"
        };

    private static readonly Regex IfscRegex = new(@"\b[A-Z]{4}0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Never match AC inside a GSTN/PAN/customer identifier, e.g. a footer
    // containing AAAAC123456H1ZW. Only explicit account-number labels qualify.
    // Page-one OCR may have an unlabeled account number; source-labeled page-two
    // identity can safely supply it after adjacent PDF pages are grouped.
    private static readonly Regex AccountRegex1 = new(
        @"(?<![A-Z0-9])(?:ACCOUNT\s*(?:NO\.?|NUMBER|#)|ACCT\s*(?:NO\.?|NUMBER|#)|A\s*/\s*C\s*(?:NO\.?|NUMBER|#)?|AC\s*(?:NO\.?|NUMBER|#))\s*[:\-]?\s*([0-9X*][0-9X*\-\s]{5,25})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AccountRegex2 = new(
        @"(?<![A-Z0-9])ACCOUNT\s*:\s*([0-9X*][0-9X*\-\s]{5,25})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageWithTotalRegex = new(@"\bPAGE\s*(?:NO\.?|NUMBER)?\s*[:\-]?\s*(\d{1,4})\s*(?:OF|/)\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageOnlyRegex = new(@"\bPAGE\s*(?:NO\.?)?\s*[:\-]?\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateTextRegex = new(@"\b(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})\b", RegexOptions.Compiled);
    private static readonly Regex HtmlRowRegex = new(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlCellRegex = new(@"<(?:td|th)\b(?<attrs>[^>]*)>(?<body>.*?)</(?:td|th)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlRowspanRegex = new(@"\browspan\s*=\s*[""']?(?<count>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlColspanRegex = new(@"\bcolspan\s*=\s*[""']?(?<count>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);

    public BankVerifiedDocumentBatch Verify(
        BankOcrRecognitionBatch recognition,
        string runRoot,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var profiles = new List<PageProfile>();
        var parseIssues = new List<string>();

        foreach (var page in recognition.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                profiles.Add(BuildPageProfile(page));
            }
            catch (Exception exception)
            {
                parseIssues.Add($"{Path.GetFileName(page.SourcePath)} page {page.SourcePage}: {exception.Message}");
            }
        }

        // A PaddleOCR response can look successful even when a PDF page was skipped.
        // Check the original PDF's physical page count before allowing any import.
        // Never infer completeness from a reconciled subset of the statement.
        foreach (var group in recognition.Pages
            .Where(page => Path.GetExtension(page.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .GroupBy(page => page.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var pdf = PdfDocument.Open(group.Key);
                var actualPages = group.Select(page => page.SourcePage).Distinct().ToArray();
                if (actualPages.Length != pdf.NumberOfPages ||
                    actualPages.Any(pageNumber => pageNumber < 1 || pageNumber > pdf.NumberOfPages))
                    parseIssues.Add($"PDF page completeness failure for {Path.GetFileName(group.Key)}: OCR recognized {actualPages.Length} of {pdf.NumberOfPages} physical page(s).");
            }
            catch (Exception exception)
            {
                parseIssues.Add($"Cannot verify PDF page completeness for {Path.GetFileName(group.Key)}: {exception.Message}");
            }
        }

        if (profiles.Count == 0)
            return BankVerifiedDocumentBatch.Failed("Raw OCR was produced, but AAH could not parse a Bank Statement page.", parseIssues);

        var groups = GroupPages(profiles);
        var documents = new List<BankVerifiedDocument>();

        for (var i = 0; i < groups.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = OrderPages(groups[i]).Ordered;
            progress?.Invoke($"Bank OCR • verifying document {i + 1:N0} of {groups.Count:N0} ({ordered.Count:N0} page(s))");
            documents.Add(VerifyDocument(i + 1, ordered));
        }

        // Keep the exact source-backed rectangular transaction candidates on
        // disk including the inherited column mapping from page one. An OCR page
        // containing only free-form text still reports zero tabular candidates.
        // This is a diagnostic artifact, NOT a verified accounting workbook.
        var tabularPath = Path.Combine(runRoot, "AAH_OCR_TABULAR_NORMALIZATION.json");
        try
        {
            var snapshot = new
            {
                stage = "POST_OCR_NORMALIZED_TABULAR_ROWS",
                created_utc = DateTime.UtcNow,
                physical_pages = profiles.Count,
                candidate_rows = profiles.Sum(page => page.Transactions.Count),
                pages = profiles.OrderBy(page => page.SourcePage).Select(page => new
                {
                    page.SourcePage,
                    page.SourcePath,
                    page.Bank,
                    page.AccountNumber,
                    page.BranchName,
                    source_columns = page.TransactionMapping,
                    unresolved_narration = page.NarrationIssues,
                    rows = page.Transactions.Select(row => new
                    {
                        row.SourceRow, row.DateRaw, row.ValueDateRaw,
                        row.Description, row.Reference,
                        row.DebitRaw, row.CreditRaw, row.BalanceRaw,
                        has_posting_date = row.Date is not null,
                        has_running_balance = row.Balance is not null
                    }).ToArray()
                }).ToArray()
            };
            File.WriteAllText(tabularPath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            parseIssues.Add("Could not preserve local pre-verification OCR table evidence: " + exception.Message);
        }
        progress?.Invoke($"Bank OCR • structured OCR into {profiles.Sum(page => page.Transactions.Count):N0} tabular candidate row(s) across {profiles.Count:N0} page(s); verifying documents");
        var auditPath = Path.Combine(runRoot, "AAH_DOCUMENT_VERIFICATION.json");
        try
        {
            var audit = new
            {
                engine = "AAH production Bank OCR document verifier",
                created_utc = DateTime.UtcNow,
                recognition_errors = recognition.SourceErrors,
                parse_issues = parseIssues,
                documents = documents.Select(ToAuditDocument).ToArray()
            };
            File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch
        {
            auditPath = string.Empty;
        }

        return new BankVerifiedDocumentBatch
        {
            // If even one page or source failed, no partial batch may be imported.
            WasSuccessful = documents.Count > 0 && parseIssues.Count == 0 && recognition.SourceErrors.Count == 0,
            Documents = documents,
            ParseIssues = parseIssues,
            RecognitionErrors = recognition.SourceErrors,
            AuditPath = auditPath
        };
    }

    private static object ToAuditDocument(BankVerifiedDocument document) => new
    {
        document.DocumentNumber,
        document.Status,
        document.PageStatus,
        document.SummaryStatus,
        document.BankName,
        document.Ifsc,
        document.AccountNumber,
        document.AccountType,
        document.AccountName,
        document.BranchName,
        document.StatementFrom,
        document.StatementTo,
        document.OpeningBalance,
        document.OpeningSource,
        document.PrintedSummaryOpeningBalance,
        document.BroughtForwardDebit,
        document.BroughtForwardCredit,
        document.ClosingBalance,
        document.DebitCount,
        document.CreditCount,
        document.TotalDebits,
        document.TotalCredits,
        document.Issues,
        document.Corrections,
        pages = document.Pages.Select(page => new
        {
            page.InferredPage,
            page.PrintedPage,
            page.PrintedTotal,
            page.SourcePath,
            page.SourcePage,
            page.PageBankDetected,
            page.DocumentBank,
            page.PageAccountDetected,
            page.DocumentAccount,
            page.FirstDate,
            page.LastDate,
            page.FirstBalance,
            page.LastBalance,
            page.BoundaryToNext
        }).ToArray(),
        transactions = document.Transactions.Select(row => new
        {
            row.Sequence,
            row.InferredPage,
            row.DateRaw,
            row.ValueDateRaw,
            row.Description,
            row.Reference,
            row.OriginalDebit,
            row.OriginalCredit,
            row.FinalDebit,
            row.FinalCredit,
            row.Balance,
            row.VerificationStatus,
            row.CorrectionProof,
            row.SourcePath
        }).ToArray()
    };

    private static PageProfile BuildPageProfile(BankOcrRecognitionPage record)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(record.JsonPath, Encoding.UTF8));
        var root = document.RootElement;
        if (root.TryGetProperty("res", out var inner) && inner.ValueKind == JsonValueKind.Object) root = inner;

        var blocks = ReadBlocks(root);
        var nonTableText = NonTableText(blocks);
        var allText = AllText(blocks);
        var meta = DetectPageMetadata(blocks, nonTableText);
        var (periodStart, periodEnd) = ExtractStatementPeriod(allText);
        var (printedPage, printedTotal) = ExtractPrintedPage(nonTableText);
        var tx = CanonicalTransactions(blocks);
        var summary = ExtractSummary(blocks);
        var first = tx.Transactions.FirstOrDefault();
        var last = tx.Transactions.LastOrDefault();
        decimal? impliedOpening = null;
        if (first?.Balance is not null)
            impliedOpening = RoundMoney(first.Balance.Value + first.Debit - first.Credit);

        return new PageProfile
        {
            Id = record.Id,
            SourcePath = record.SourcePath,
            SourceIndex = record.SourceIndex,
            SourcePage = record.SourcePage,
            Bank = meta.Bank,
            BankStatus = meta.BankStatus,
            BankEvidence = meta.BankEvidence,
            Ifsc = meta.Ifsc,
            AccountNumber = meta.AccountNumber,
            AccountType = meta.AccountType,
            AccountName = meta.AccountName,
            BranchName = meta.BranchName,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            PrintedPage = printedPage,
            PrintedTotal = printedTotal,
            Transactions = tx.Transactions,
            NarrationIssues = tx.NarrationIssues,
            TransactionMapping = tx.Mapping,
            RawBlocks = blocks,
            BroughtForward = tx.BroughtForward,
            ClosingRow = tx.ClosingRow,
            Summary = summary,
            FirstDate = first?.Date,
            LastDate = last?.Date,
            FirstBalance = first?.Balance,
            LastBalance = last?.Balance,
            FirstDebit = first?.Debit ?? 0m,
            FirstCredit = first?.Credit ?? 0m,
            ImpliedOpeningRaw = impliedOpening,
            HasHeader = NormalizeUpper(allText).Contains("STATEMENT OF ACCOUNT", StringComparison.Ordinal),
            HasEnd = NormalizeUpper(nonTableText).Contains("END OF STATEMENT", StringComparison.Ordinal) || summary is not null
        };
    }

    private static IReadOnlyList<OcrBlock> ReadBlocks(JsonElement root)
    {
        var result = new List<OcrBlock>();
        if (!root.TryGetProperty("parsing_res_list", out var blocks) || blocks.ValueKind != JsonValueKind.Array) return result;
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            result.Add(new OcrBlock
            {
                Label = JsonText(block, "block_label"),
                Content = JsonText(block, "block_content")
            });
        }
        return result;
    }

    private static PageMetadata DetectPageMetadata(IReadOnlyList<OcrBlock> blocks, string headerText)
    {
        var upper = NormalizeUpper(headerText);
        var ifscs = IfscRegex.Matches(upper).Select(match => match.Value.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<string>();

        foreach (var ifsc in ifscs)
        {
            if (!IfscPrefixBank.TryGetValue(ifsc[..4], out var bank)) continue;
            scores[bank] = scores.TryGetValue(bank, out var score) ? score + 10 : 10;
            evidence.Add($"IFSC {ifsc} -> {bank}");
        }

        foreach (var match in BankNameMatches(headerText))
        {
            scores[match.Bank] = scores.TryGetValue(match.Bank, out var score) ? score + 5 : 5;
            evidence.Add($"Header text: {match.Alias}");
        }

        var ranked = scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var bankName = ranked.FirstOrDefault().Key ?? string.Empty;
        var bankStatus = "NOT_DETECTED";
        if (ranked.Count > 0)
        {
            bankStatus = ranked.Count > 1 && ranked[0].Value == ranked[1].Value
                ? "REVIEW_REQUIRED"
                : ranked[0].Value >= 10 ? "VERIFIED" : "DETECTED";
        }

        var accountNumber = ExtractAccountNumber(headerText);
        // Source OCR often prints the customer and branch as separate text blocks,
        // rather than ParagraphTitle. Only explicit bank-statement labels or a
        // conventional source-visible name prefix can establish account identity.
        string LabeledValue(string label)
        {
            var match = Regex.Match(headerText,
                @"(?:^|\n)\s*" + label + @"\s*[:\-]\s*([^\r\n]+)",
                RegexOptions.IgnoreCase);
            return match.Success ? Normalize(match.Groups[1].Value) : string.Empty;
        }
        var branchName = LabeledValue(@"(?:ACCOUNT\s+)?BRANCH(?:\s+NAME)?");
        var accountType = string.Empty;
        if (Regex.IsMatch(upper, @"\bCURRENT\s+ACCOUNT\b|\bPRODUCT\s*:\s*CA[-\s]", RegexOptions.IgnoreCase)) accountType = "CA";
        else if (Regex.IsMatch(upper, @"\bSAVINGS?\s+(?:BANK\s+)?ACCOUNT\b|\bPRODUCT\s*:\s*SB[-\s]", RegexOptions.IgnoreCase)) accountType = "SB";
        else if (Regex.IsMatch(upper, @"\bOVERDRAFT\b|\bOD\s+ACCOUNT\b", RegexOptions.IgnoreCase)) accountType = "OD";
        else if (Regex.IsMatch(upper, @"\bCASH\s+CREDIT\b|\bCC\s+ACCOUNT\b", RegexOptions.IgnoreCase)) accountType = "CC";

        var accountName = LabeledValue(@"(?:ACCOUNT\s+HOLDER|ACCOUNT\s+NAME|CUSTOMER\s+NAME)");
        foreach (var block in blocks)
        {
            if (accountName.Length > 0) break;
            // Legal-name prefix in the printed address block is source evidence.
            // Do not invent a customer name from address, narration or filename.
            var printed = Normalize(Regex.Replace(Clean(block.Content), @"^#+\s*", string.Empty));
            if (NormalizeUpper(block.Label) != "TABLE" &&
                Regex.IsMatch(printed, @"^(?:M\s*/\s*S\.?|MS\.?\s+)\s*[A-Z]", RegexOptions.IgnoreCase) &&
                !printed.Contains(':') && printed.Length <= 110)
            {
                accountName = printed;
                break;
            }
        }
        // A generic title is a last resort, never overwrite a source-labeled
        // account holder with a bank/logo title returned by Paddle.
        if (accountName.Length == 0)
        foreach (var block in blocks)
        {
            var label = NormalizeUpper(block.Label);
            if (label is not ("PARAGRAPH_TITLE" or "TITLE")) continue;
            var title = Regex.Replace(Clean(block.Content), @"^#+\s*", string.Empty).Trim();
            var titleUpper = NormalizeUpper(title);
            if (titleUpper.Contains("STATEMENT OF ACCOUNT", StringComparison.Ordinal)) continue;
            if (bankName.Length > 0 && titleUpper.Contains(NormalizeUpper(bankName), StringComparison.Ordinal)) continue;
            if (titleUpper is "STATEMENT" or "ACCOUNT STATEMENT") continue;
            if (title.Length == 0) continue;
            accountName = title;
            break;
        }

        return new PageMetadata
        {
            Bank = bankName,
            BankStatus = bankStatus,
            BankEvidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Ifsc = ifscs.FirstOrDefault() ?? string.Empty,
            AccountNumber = accountNumber,
            AccountType = accountType.Length > 0 ? accountType : LabeledValue(@"ACCOUNT\s+TYPE"),
            AccountName = accountName,
            BranchName = branchName
        };
    }

    private static string ExtractAccountNumber(string text)
    {
        foreach (var regex in new[] { AccountRegex1, AccountRegex2 })
        {
            var match = regex.Match(text);
            if (!match.Success) continue;
            var value = Regex.Replace(match.Groups[1].Value.ToUpperInvariant(), @"[^0-9X*]", string.Empty);
            if (value.Length >= 6) return value;
        }
        return string.Empty;
    }

    private static IReadOnlyList<BankNameMatch> BankNameMatches(string text)
    {
        var upper = NormalizeUpper(text);
        var candidates = new List<BankNameMatch>();
        foreach (var pair in BankAliases)
        {
            foreach (var alias in pair.Value)
            {
                var escaped = Regex.Escape(NormalizeUpper(alias)).Replace("\\ ", @"\s+");
                var regex = new Regex($@"(?<![A-Z0-9]){escaped}(?![A-Z0-9])", RegexOptions.IgnoreCase);
                foreach (Match match in regex.Matches(upper))
                    candidates.Add(new BankNameMatch(match.Index, match.Index + match.Length, pair.Key, alias));
            }
        }

        var selected = new List<BankNameMatch>();
        foreach (var candidate in candidates.OrderByDescending(item => item.End - item.Start).ThenBy(item => item.Start).ThenBy(item => item.Bank, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Any(item => !(candidate.End <= item.Start || candidate.Start >= item.End))) continue;
            selected.Add(candidate);
        }
        return selected.OrderBy(item => item.Start).ToList();
    }

    private static (string Start, string End) ExtractStatementPeriod(string text)
    {
        var patterns = new[]
        {
            new Regex(@"(?:STATEMENT\s+(?:FROM|SUMMARY)\s*:?\s*)(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})\s*(?:TO|-)\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})", RegexOptions.IgnoreCase),
            new Regex(@"\bFROM\s*:?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4})\s+TO\s*:?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4})", RegexOptions.IgnoreCase)
        };
        foreach (var pattern in patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            return (NormalizeDate(match.Groups[1].Value), NormalizeDate(match.Groups[2].Value));
        }
        return (string.Empty, string.Empty);
    }

    private static (int? Page, int? Total) ExtractPrintedPage(string text)
    {
        var matches = PageWithTotalRegex.Matches(text);
        if (matches.Count > 0)
        {
            var match = matches[^1];
            return (ParseNullableInt(match.Groups[1].Value), ParseNullableInt(match.Groups[2].Value));
        }
        matches = PageOnlyRegex.Matches(text);
        if (matches.Count > 0)
            return (ParseNullableInt(matches[^1].Groups[1].Value), null);
        return (null, null);
    }

    private static CanonicalTransactionTable CanonicalTransactions(
        IReadOnlyList<OcrBlock> blocks,
        IReadOnlyDictionary<string, int>? inheritedMapping = null)
    {
        var identified = IdentifyTransactionTable(blocks) ??
            (inheritedMapping is not null ? IdentifyContinuationTable(blocks, inheritedMapping) : null);
        if (identified is null) return new CanonicalTransactionTable();

        var transactions = new List<PageTransaction>();
        var narrationIssues = new List<string>();
        // Normalize OCR source rows into a consistent tabular structure BEFORE
        // date, amount, reference or narration extraction. Source column mapping
        // is inherited from the previous PDF page, never guessed by bank name.
        var tabularRows = NormalizeTransactionRows(identified, narrationIssues);
        identified = new IdentifiedTable(tabularRows, identified.Mapping, identified.HasHeader);
        BalanceEvidence? broughtForward = null;
        BalanceEvidence? closingRow = null;

        string Get(IReadOnlyList<string> row, string key)
        {
            if (!identified.Mapping.TryGetValue(key, out var index) || index < 0 || index >= row.Count) return string.Empty;
            return Clean(row[index]);
        }

        for (var rowIndex = identified.HasHeader ? 1 : 0; rowIndex < identified.Rows.Count; rowIndex++)
        {
            var row = identified.Rows[rowIndex];
            var description = Get(row, "description");
            var dateRaw = Get(row, "date");
            var balanceRaw = Get(row, "balance");
            var descriptionUpper = NormalizeUpper(description);

            // A BALANCE BROUGHT FORWARD control belongs in opening-balance
            // metadata, even when a bank prints it as a dated debit/credit row.
            // Never produce a voucher for this line. Retain the source movement
            // separately so the printed summary (which may count it) reconciles.
            var isBroughtForward = descriptionUpper.Contains("BALANCE BROUGHT FORWARD", StringComparison.Ordinal) ||
                descriptionUpper.StartsWith("BROUGHT FORWARD", StringComparison.Ordinal);
            if (isBroughtForward)
            {
                if (broughtForward is not null)
                    narrationIssues.Add("Multiple BROUGHT FORWARD controls appear on one statement page.");
                var debit = ParseMoney(Get(row, "debit"));
                var credit = ParseMoney(Get(row, "credit"));
                var printedBalance = ParseSignedMoney(balanceRaw);
                if (debit is null || credit is null || (Math.Abs(debit.Value) > 0.009m && Math.Abs(credit.Value) > 0.009m))
                    narrationIssues.Add("BROUGHT FORWARD debit/credit cells are ambiguous; review the original statement.");
                // For a credit-only/debit-only row whose balance OCR is blank,
                // expose its amount in the opening section, but do NOT treat
                // this as independent proof of the original running balance.
                decimal? inferredFromMovement = debit is not null && credit is not null &&
                    (Math.Abs(debit.Value) < 0.01m || Math.Abs(credit.Value) < 0.01m)
                    ? RoundMoney(credit.Value - debit.Value) : null;
                broughtForward = new BalanceEvidence(
                    balanceRaw, printedBalance ?? inferredFromMovement,
                    Math.Abs(debit ?? 0m), Math.Abs(credit ?? 0m),
                    printedBalance is null);
                continue;
            }
            if (dateRaw.Length == 0 && (descriptionUpper.Contains("CLOSING BALANCE", StringComparison.Ordinal) || descriptionUpper.Contains("CARRIED FORWARD", StringComparison.Ordinal)))
            {
                closingRow = new BalanceEvidence(balanceRaw, ParseSignedMoney(balanceRaw));
                continue;
            }
            // Wrapped narrations may repeat the posting date on a visually
            // separate OCR row. When the same date is repeated, no independent
            // reference exists and all three financial cells are empty, retain
            // the text on the previous transaction rather than inventing a
            // zero-amount Bank entry. A DIFFERENT date/standalone reference
            // without financial fields is still retained for review.
            var referenceRaw = Get(row, "reference");
            var noFinancialCells = Get(row, "debit").Length == 0 &&
                Get(row, "credit").Length == 0 && balanceRaw.Length == 0;
            var sameDateNarration = transactions.Count > 0 &&
                dateRaw.Length > 0 &&
                dateRaw.Equals(transactions[^1].DateRaw, StringComparison.Ordinal) &&
                referenceRaw.Length == 0;
            if (noFinancialCells)
            {
                if (transactions.Count == 0)
                {
                    if (description.Length > 0 || referenceRaw.Length > 0)
                        narrationIssues.Add($"Unassigned narration before first transaction at OCR table row {rowIndex + 1}.");
                    continue;
                }
                var previous = transactions[^1];
                var referenceConflict = referenceRaw.Length > 0 && previous.Reference.Length > 0 &&
                    !NormalizeUpper(referenceRaw).Equals(NormalizeUpper(previous.Reference), StringComparison.Ordinal);
                if (dateRaw.Length > 0 && !sameDateNarration ||
                    referenceConflict)
                {
                    narrationIssues.Add($"Narration-only OCR row {rowIndex + 1} has a different posting date or reference and cannot be safely assigned to a transaction.");
                    // Source content remains in raw page JSON; never invent a zero
                    // amount transaction or attach disputed words to the wrong row.
                    continue;
                }
                if (description.Length > 0)
                    previous.Description = string.Join(" ", new[] { previous.Description, description }.Where(part => part.Length > 0));
                if (referenceRaw.Length > 0 && previous.Reference.Length == 0)
                    previous.Reference = referenceRaw;
                continue;
            }

            var debitRaw = Get(row, "debit");
            var creditRaw = Get(row, "credit");
            transactions.Add(new PageTransaction
            {
                SourceRow = rowIndex + 1,
                DateRaw = dateRaw,
                Date = ParseDate(dateRaw),
                ValueDateRaw = Get(row, "value_date"),
                ValueDate = ParseDate(Get(row, "value_date")),
                Description = Normalize(description),
                Reference = Get(row, "reference"),
                DebitRaw = debitRaw,
                CreditRaw = creditRaw,
                BalanceRaw = balanceRaw,
                Debit = Math.Abs(ParseMoney(debitRaw) ?? 0m),
                Credit = Math.Abs(ParseMoney(creditRaw) ?? 0m),
                Balance = ParseSignedMoney(balanceRaw)
            });
        }

        return new CanonicalTransactionTable
        {
            Transactions = transactions,
            NarrationIssues = narrationIssues,
            Mapping = identified.Mapping,
            BroughtForward = broughtForward,
            ClosingRow = closingRow
        };
    }

    /// <summary>
    /// Transform OCR layout rows into a source-backed transaction table BEFORE
    /// interpreting field values. Monetary cells, posting dates and references
    /// remain verbatim. Only narration-only continuation rows with no conflicting
    /// date/reference may be joined to the immediately preceding financial row.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> NormalizeTransactionRows(
        IdentifiedTable table, List<string> issues)
    {
        var rows = new List<IReadOnlyList<string>>();
        var mapping = table.Mapping;
        int Column(string key) => mapping.TryGetValue(key, out var index) ? index : -1;
        int descriptionColumn = Column("description");
        int dateColumn = Column("date");
        int referenceColumn = Column("reference");
        int debitColumn = Column("debit");
        int creditColumn = Column("credit");
        int balanceColumn = Column("balance");
        static string Cell(IReadOnlyList<string> row, int column) =>
            column >= 0 && column < row.Count ? Normalize(row[column]) : string.Empty;
        var startingIndex = table.HasHeader ? 1 : 0;
        if (table.HasHeader && table.Rows.Count > 0)
            rows.Add(table.Rows[0].Select(Normalize).ToArray());

        // Tracks source continuation text that OCR placed on a dated HTML row
        // with no movement, while its source financial cells are on the next
        // row under a shared rowspan date.
        string[]? pendingNextFinancialNarration = null;
        var previousFinancialRowIndex = -1;
        for (var sourceIndex = startingIndex; sourceIndex < table.Rows.Count; sourceIndex++)
        {
            var row = table.Rows[sourceIndex].Select(Normalize).ToArray();
            var description = Cell(row, descriptionColumn);
            var date = Cell(row, dateColumn);
            var reference = Cell(row, referenceColumn);
            var hasMoneyOrBalance = new[] { debitColumn, creditColumn, balanceColumn }
                .Any(column => Cell(row, column).Length > 0);

            if (!hasMoneyOrBalance)
            {
                // Merged PDF page titles/statement periods are not narrations.
                if (NormalizeUpper(string.Join(" ", row)).Contains("STATEMENT OF ACCOUNT",
                        StringComparison.Ordinal) ||
                    NormalizeUpper(string.Join(" ", row)).StartsWith("GENERATED ON",
                        StringComparison.Ordinal))
                    continue;
                if (description.Length == 0 && reference.Length == 0) continue;

                if (previousFinancialRowIndex < 0)
                {
                    if (date.Length > 0)
                        pendingNextFinancialNarration = row;
                    else
                        issues.Add($"Source OCR row {sourceIndex + 1} has unassigned narrative text before the first financial row.");
                    continue;
                }
                var prior = rows[previousFinancialRowIndex].ToArray();
                var priorDate = Cell(prior, dateColumn);
                var priorReference = Cell(prior, referenceColumn);
                var dateConflict = date.Length > 0 &&
                    !date.Equals(priorDate, StringComparison.OrdinalIgnoreCase);
                var referenceConflict = reference.Length > 0 &&
                    priorReference.Length > 0 &&
                    !reference.Equals(priorReference, StringComparison.OrdinalIgnoreCase);
                if (dateConflict || referenceConflict)
                {
                    // This can be a split rowspan transaction beginning on a
                    // new date, not a narration fragment of the previous entry.
                    // Hold it for the following financial row; do not merge it
                    // backwards or invent a zero-amount accounting entry.
                    pendingNextFinancialNarration = date.Length > 0 ? row : null;
                    if (pendingNextFinancialNarration is null)
                        issues.Add($"Source OCR row {sourceIndex + 1} contains an ambiguous narration continuation (posting date or reference differs).");
                    continue;
                }
                if (descriptionColumn >= 0 && descriptionColumn < prior.Length &&
                    description.Length > 0)
                    prior[descriptionColumn] = Normalize(string.Join(" ", new[]
                    { prior[descriptionColumn], description }.Where(part => part.Length > 0)));
                if (referenceColumn >= 0 && referenceColumn < prior.Length &&
                    prior[referenceColumn].Length == 0 && reference.Length > 0)
                    prior[referenceColumn] = reference;
                rows[previousFinancialRowIndex] = prior;
                continue;
            }

            if (pendingNextFinancialNarration is not null)
            {
                var pendingDate = Cell(pendingNextFinancialNarration, dateColumn);
                var pendingReference = Cell(pendingNextFinancialNarration, referenceColumn);
                var financialDate = Cell(row, dateColumn);
                var financialReference = Cell(row, referenceColumn);
                if (pendingDate.Length > 0 &&
                    ParseDate(pendingDate) is not null &&
                    ParseDate(pendingDate) == ParseDate(financialDate) &&
                    (pendingReference.Length == 0 || financialReference.Length == 0 ||
                     NormalizeUpper(pendingReference) == NormalizeUpper(financialReference)))
                {
                    if (descriptionColumn >= 0 && descriptionColumn < row.Length)
                    {
                        var fragment = Cell(pendingNextFinancialNarration, descriptionColumn);
                        if (fragment.Length > 0)
                            row[descriptionColumn] = Normalize(string.Join(" ", new[] {
                                fragment, row[descriptionColumn] }.Where(value => value.Length > 0)));
                    }
                    if (referenceColumn >= 0 && referenceColumn < row.Length &&
                        financialReference.Length == 0 && pendingReference.Length > 0)
                        row[referenceColumn] = pendingReference;
                }
                else
                    issues.Add($"Source OCR row {sourceIndex + 1}: a separate dated narration fragment could not be attached to the following financial transaction.");
                pendingNextFinancialNarration = null;
            }
            rows.Add(row);
            previousFinancialRowIndex = rows.Count - 1;
        }
        if (pendingNextFinancialNarration is not null)
            issues.Add("Trailing dated OCR narration has no matching financial row.");
        return rows;
    }

    private static IdentifiedTable? IdentifyTransactionTable(IReadOnlyList<OcrBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (!NormalizeUpper(block.Label).Equals("TABLE", StringComparison.Ordinal)) continue;
            var rows = TableRows(block.Content);
            if (rows.Count == 0) continue;

            // PaddleOCR may put a title, statement period or a merged colspan
            // row INSIDE the same HTML table before the real column headings.
            // Locate a complete source-visible transaction header, not row zero.
            for (var headerIndex = 0; headerIndex < rows.Count; headerIndex++)
            {
                var header = rows[headerIndex].Select(NormalizeUpper).ToList();
                var mapping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                FindSemantic("date", ["POST DATE", "TRANSACTION DATE", "TXN DATE", "DATE"]);
                FindSemantic("description", ["DESCRIPTION", "NARRATION", "PARTICULARS", "DETAILS"]);
                FindSemantic("debit", ["DEBIT", "WITHDRAWAL", "DR AMOUNT"]);
                FindSemantic("credit", ["CREDIT", "DEPOSIT", "CR AMOUNT"]);
                FindSemantic("balance", ["BALANCE", "RUNNING BALANCE"]);

                for (var i = 0; i < header.Count; i++)
                {
                    if (header[i].Contains("VALUE DATE", StringComparison.Ordinal) ||
                        header[i].Contains("VALUE DT", StringComparison.Ordinal)) mapping["value_date"] = i;
                    if (new[] { "REFERENCE", "CHEQUE", "CHQ", "REF NO", "REF.", "REF/" }
                        .Any(word => header[i].Contains(word, StringComparison.Ordinal))) mapping["reference"] = i;
                }

                if (new[] { "date", "description", "debit", "credit", "balance" }.All(mapping.ContainsKey) &&
                    mapping.Values.Distinct().Count() == mapping.Count)
                    return new IdentifiedTable(rows.Skip(headerIndex).ToList(), mapping, true);

                void FindSemantic(string semantic, IReadOnlyList<string> words)
                {
                    for (var i = 0; i < header.Count; i++)
                    {
                        if (!words.Any(word => header[i].Contains(word, StringComparison.Ordinal))) continue;
                        mapping[semantic] = i;
                        break;
                    }
                }
            }
        }
        return null;
    }

    // Follow the source-visible column mapping from the preceding page only when
    // the next page has a table with no repeated header. Require a valid first
    // transaction, financial columns and a parseable balance to prevent the
    // separate statement-summary table from being mistaken for transactions.
    private static IdentifiedTable? IdentifyContinuationTable(
        IReadOnlyList<OcrBlock> blocks,
        IReadOnlyDictionary<string, int> mapping)
    {
        if (!new[] { "date", "description", "debit", "credit", "balance" }.All(mapping.ContainsKey))
            return null;

        foreach (var block in blocks)
        {
            if (!NormalizeUpper(block.Label).Equals("TABLE", StringComparison.Ordinal)) continue;
            var rows = TableRows(block.Content);
            // A continuation may begin with one or more merged title/period rows.
            // Do not mistake its printed statement metadata for a transaction.
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var first = rows[rowIndex];
                string Cell(string name) =>
                    mapping.TryGetValue(name, out var index) && index >= 0 && index < first.Count
                        ? Clean(first[index]) : string.Empty;
                if (ParseDate(Cell("date")) is null) continue;
                if (Cell("description").Length == 0) continue;
                if (ParseSignedMoney(Cell("balance")) is null) continue;
                if (ParseMoney(Cell("debit")) is null && ParseMoney(Cell("credit")) is null) continue;
                return new IdentifiedTable(rows.Skip(rowIndex).ToList(), mapping, false);
            }
        }
        return null;
    }

    private static StatementSummary? ExtractSummary(IReadOnlyList<OcrBlock> blocks)
    {
        foreach (var block in blocks)
        {
            // PaddleOCR sometimes labels a structurally valid Markdown/HTML
            // statement-summary table as text. Treat source-visible table
            // markup as evidence regardless of its coarse OCR block label.
            // Do not inspect ordinary narrative text as if it were a table.
            if (!NormalizeUpper(block.Label).Equals("TABLE", StringComparison.Ordinal) &&
                !block.Content.Contains('|') &&
                !block.Content.Contains("<table", StringComparison.OrdinalIgnoreCase)) continue;
            var rows = TableRows(block.Content);
            if (rows.Count < 2) continue;
            // A cropped OCR response may retain STATEMENT SUMMARY as the
            // first HTML/Markdown table row. Find the actual column header
            // anywhere in the same source table, not only at index zero.
            for (var headerRow = 0; headerRow + 1 < rows.Count; headerRow++)
            {
            var header = rows[headerRow].Select(NormalizeUpper).ToList();

            int? Find(params string[] words)
            {
                for (var i = 0; i < header.Count; i++)
                    if (words.Any(word => header[i].Contains(word, StringComparison.Ordinal))) return i;
                return null;
            }

            var opening = Find("BROUGHT FORWARD", "OPENING BALANCE", "OPENING BAL");
            var drCount = Find("DR COUNT", "DEBIT COUNT");
            var crCount = Find("CR COUNT", "CREDIT COUNT");
            var totalDebits = Find("TOTAL DEBITS", "TOTAL DEBIT", "DEBITS");
            var totalCredits = Find("TOTAL CREDITS", "TOTAL CREDIT", "CREDITS");
            var closing = Find("CLOSING BALANCE", "CLOSING BAL");
            if (new[] { opening, drCount, crCount, totalDebits, totalCredits, closing }.Count(value => value is not null) < 4) continue;

            var row = rows[headerRow + 1];
            string Cell(int? index) => index is not null && index.Value < row.Count ? Clean(row[index.Value]) : string.Empty;
            // Production Paddle output can merge the printed title, opening
            // label and 0.00 into a single rowspan=2 cell:
            // 'STATEMENT SUMMARY :- Opening Balance 0.00'.
            // The source label and adjacent money are independently visible.
            // Do not take the number from another cell or account arithmetic.
            decimal? printedOpening = ParseSignedMoney(Cell(opening));
            if (printedOpening is null && opening is not null)
            {
                // Paddle's table HTML sometimes stores literal backslash-n
                // characters inside this one source cell instead of line
                // breaks. Normalize those cell delimiters only, never PDF
                // paths, narration, or whole-document OCR content.
                var sourceLabelCell = Clean(header[opening.Value])
                    .Replace(@"\n", " ", StringComparison.Ordinal)
                    .Replace(@"\r", " ", StringComparison.Ordinal);
                var embedded = Regex.Match(sourceLabelCell,
                    @"\bOPENING\s+BAL(?:ANCE)?\s*[:=\-]?\s*(?<amount>-?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?)\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (embedded.Success)
                    printedOpening = ParseSignedMoney(embedded.Groups["amount"].Value);
            }
            return new StatementSummary
            {
                Opening = printedOpening,
                DebitCount = ParseNullableInt(Regex.Replace(Cell(drCount), @"[^0-9]", string.Empty)),
                CreditCount = ParseNullableInt(Regex.Replace(Cell(crCount), @"[^0-9]", string.Empty)),
                TotalDebits = AbsNullable(ParseMoney(Cell(totalDebits))),
                TotalCredits = AbsNullable(ParseMoney(Cell(totalCredits))),
                Closing = ParseSignedMoney(Cell(closing))
            };
            } // Candidate source-table header row
        }
        // Recovered source crops can put printed controls in a two-column
        // label/value table or six separate labeled text rows. Parse only
        // explicitly labeled money/count cells adjacent to STATEMENT SUMMARY.
        // Never infer any financial amount from computed transaction totals.
        var verticalSummary = ExtractLabeledSummaryControls(blocks);
        if (verticalSummary is not null) return verticalSummary;
        // Text-only fallback requires the explicit printed summary title, all
        // six labeled controls in source order, and an adjacent line with
        // exactly six numeric cells. It must NEVER fill missing OCR values
        // from transaction arithmetic or a filename/account-specific guess.
        // A value that cannot be independently parsed leaves REVIEW_REQUIRED.
        var text = string.Join("\n", blocks
            .Where(block => !NormalizeUpper(block.Label).Equals("TABLE", StringComparison.Ordinal))
            .Select(block => block.Content));
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(line => line.Length > 0).ToArray();
        for (var i = 0; i + 2 < lines.Length; i++)
        {
            if (!NormalizeUpper(lines[i]).Contains("STATEMENT SUMMARY", StringComparison.Ordinal))
                continue;
            // The title and table can be split across multiple OCR text blocks.
            // Limit the search window so a transaction row from elsewhere on
            // the page cannot be mistaken for a summary's value row.
            for (var h = i + 1; h < Math.Min(lines.Length - 1, i + 4); h++)
            {
                var headerLine = NormalizeUpper(lines[h]);
                var labels = new[]
                {
                    @"\bOPENING\s+BAL(?:ANCE)?\b",
                    @"\b(?:DR|DEBIT)\s+COUNT\b",
                    @"\b(?:CR|CREDIT)\s+COUNT\b",
                    @"\b(?:TOTAL\s+)?DEBITS?\b",
                    @"\b(?:TOTAL\s+)?CREDITS?\b",
                    @"\bCLOSING\s+BAL(?:ANCE)?\b"
                };
                var positions = labels.Select(label => Regex.Match(headerLine, label)).ToArray();
                if (positions.Any(match => !match.Success)) continue;
                if (!positions.Zip(positions.Skip(1), (left, right) => left.Index < right.Index).All(ok => ok))
                    continue;

                var values = Regex.Split(lines[h + 1].Trim(), @"\s+")
                    .Where(value => value.Length > 0).ToArray();
                if (values.Length != 6 ||
                    values.Any(value => !Regex.IsMatch(value, @"^-?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?$")) ||
                    !Regex.IsMatch(values[1], @"^\d+$") ||
                    !Regex.IsMatch(values[2], @"^\d+$"))
                    continue;
                var opening = ParseSignedMoney(values[0]);
                var dr = ParseNullableInt(values[1]);
                var cr = ParseNullableInt(values[2]);
                var debits = ParseMoney(values[3]);
                var credits = ParseMoney(values[4]);
                var closing = ParseSignedMoney(values[5]);
                if (opening is null || dr is null || cr is null ||
                    debits is null || credits is null || closing is null)
                    continue;
                return new StatementSummary
                {
                    Opening = opening,
                    DebitCount = dr,
                    CreditCount = cr,
                    TotalDebits = Math.Abs(debits.Value),
                    TotalCredits = Math.Abs(credits.Value),
                    Closing = closing
                };
            }
        }
        return null;
    }

    private static StatementSummary? ExtractLabeledSummaryControls(IReadOnlyList<OcrBlock> blocks)
    {
        // Keep the region bounded: old OCR blocks may contain account numbers,
        // transaction dates, unrelated bank fees and other numeric text.
        var start = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!NormalizeUpper(blocks[i].Content).Contains("STATEMENT SUMMARY", StringComparison.Ordinal))
                continue;
            start = i;
            break;
        }
        if (start < 0) return null;

        var labelPatterns = new[]
        {
            @"^OPENING\s+BAL(?:ANCE)?$",
            @"^(?:DR|DEBIT)\s+COUNT$",
            @"^(?:CR|CREDIT)\s+COUNT$",
            @"^(?:TOTAL\s+)?DEBITS?$",
            @"^(?:TOTAL\s+)?CREDITS?$",
            @"^CLOSING\s+BAL(?:ANCE)?$"
        };
        int Field(string label)
        {
            var normalized = NormalizeUpper(label).Trim().Trim(':', '-', ' ');
            for (var index = 0; index < labelPatterns.Length; index++)
                if (Regex.IsMatch(normalized, labelPatterns[index], RegexOptions.CultureInvariant))
                    return index;
            return -1;
        }
        // Require precise printed numeric shapes; do not repair O/0, guess
        // separators, or use arithmetic to invent OCR values.
        const string moneyPattern = @"-?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?";
        var entries = new Dictionary<int, string>();
        var conflict = false;
        void Add(string label, string value)
        {
            var id = Field(label);
            if (id < 0) return;
            value = Normalize(value);
            if (!Regex.IsMatch(value, "^" + moneyPattern + "$", RegexOptions.CultureInvariant))
                return;
            if (id is 1 or 2 && !Regex.IsMatch(value, @"^\d+$")) return;
            if (entries.TryGetValue(id, out var previous) &&
                !previous.Equals(value, StringComparison.Ordinal))
                conflict = true;
            else
                entries[id] = value;
        }

        // The visual summary may be emitted as a single OCR TABLE, a set of
        // separately labeled text blocks, or pipe rows. Only exact label/value
        // associations are accepted, not two unrelated lists of numbers.
        foreach (var block in blocks.Skip(start).Take(12))
        {
            var tableRows = TableRows(block.Content);
            foreach (var row in tableRows)
            {
                var cells = row.Where(cell => Normalize(cell).Length > 0).ToArray();
                if (cells.Length != 2) continue;
                Add(cells[0], cells[1]);
            }
            foreach (var rawLine in block.Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = Normalize(WebUtility.HtmlDecode(HtmlTagRegex.Replace(rawLine, " ")));
                var match = Regex.Match(line, @"^(?<key>[A-Za-z ]{3,36})\s*[:=\-]?\s+(?<value>" + moneyPattern + @")\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                    Add(match.Groups["key"].Value, match.Groups["value"].Value);
            }
        }
        if (conflict || entries.Count != 6) return null;
        var opening = ParseSignedMoney(entries[0]);
        var debitCount = ParseNullableInt(entries[1]);
        var creditCount = ParseNullableInt(entries[2]);
        var debits = ParseMoney(entries[3]);
        var credits = ParseMoney(entries[4]);
        var closing = ParseSignedMoney(entries[5]);
        if (opening is null || debitCount is null || creditCount is null ||
            debits is null || credits is null || closing is null)
            return null;
        return new StatementSummary
        {
            Opening = opening,
            DebitCount = debitCount,
            CreditCount = creditCount,
            TotalDebits = Math.Abs(debits.Value),
            TotalCredits = Math.Abs(credits.Value),
            Closing = closing
        };
    }

    private static List<List<PageProfile>> GroupPages(IReadOnlyList<PageProfile> profiles)
    {
        var n = profiles.Count;
        var adjacency = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            if (!GroupingEdge(profiles[i], profiles[j])) continue;
            adjacency[i].Add(j);
            adjacency[j].Add(i);
        }

        var seen = new HashSet<int>();
        var components = new List<List<PageProfile>>();
        for (var i = 0; i < n; i++)
        {
            if (!seen.Add(i)) continue;
            var stack = new Stack<int>();
            stack.Push(i);
            var component = new List<PageProfile>();
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                component.Add(profiles[current]);
                foreach (var next in adjacency[current]) if (seen.Add(next)) stack.Push(next);
            }
            components.Add(component);
        }

        var changed = true;
        while (changed && components.Count > 1)
        {
            changed = false;
            (double Score, int Left, int Right)? best = null;
            for (var i = 0; i < components.Count; i++)
            for (var j = i + 1; j < components.Count; j++)
            {
                if (!ComponentsCompatible(components[i], components[j])) continue;
                var score = -10000d;
                foreach (var left in components[i])
                foreach (var right in components[j])
                    score = Math.Max(score, Math.Max(TransitionScore(left, right), TransitionScore(right, left)));
                if (score >= 95d && (best is null || score > best.Value.Score)) best = (score, i, j);
            }
            if (best is null) continue;
            components[best.Value.Left].AddRange(components[best.Value.Right]);
            components.RemoveAt(best.Value.Right);
            changed = true;
        }
        return components;
    }

    private static (IReadOnlyList<PageProfile> Ordered, double Score) OrderPages(IReadOnlyList<PageProfile> group)
    {
        var n = group.Count;
        if (n <= 1) return (group.ToList(), 0d);

        if (n <= 15)
        {
            var dp = new Dictionary<(int Mask, int Last), DpState>();
            for (var i = 0; i < n; i++) dp[(1 << i, i)] = new DpState(PositionScore(group[i], 0, n), [i]);
            var full = (1 << n) - 1;

            for (var size = 1; size < n; size++)
            {
                var states = dp.Where(pair => BitOperations.PopCount((uint)pair.Key.Mask) == size).ToArray();
                foreach (var state in states)
                {
                    for (var next = 0; next < n; next++)
                    {
                        if ((state.Key.Mask & (1 << next)) != 0) continue;
                        var newMask = state.Key.Mask | (1 << next);
                        var newScore = state.Value.Score + TransitionScore(group[state.Key.Last], group[next]) + PositionScore(group[next], size, n);
                        var key = (newMask, next);
                        if (!dp.TryGetValue(key, out var existing) || newScore > existing.Score)
                            dp[key] = new DpState(newScore, [.. state.Value.Path, next]);
                    }
                }
            }

            var best = dp.Where(pair => pair.Key.Mask == full).Select(pair => pair.Value).OrderByDescending(value => value.Score).First();
            return (best.Path.Select(index => group[index]).ToList(), best.Score);
        }

        var ordered = group
            .OrderBy(page => page.HasHeader ? 0 : 1)
            .ThenBy(page => page.PrintedPage ?? 99999)
            .ThenBy(page => page.FirstDate ?? DateTime.MaxValue)
            .ThenBy(page => page.SourceIndex)
            .ThenBy(page => page.SourcePage)
            .ToList();
        return (ordered, 0d);
    }

    private static BankVerifiedDocument VerifyDocument(int documentNumber, IReadOnlyList<PageProfile> ordered)
    {
        var metadata = AggregateMetadata(ordered);
        // Some banks print the table headings only on the first statement page.
        // Restore the carried-over mapping after page grouping and ordering,
        // never by assuming a bank-specific column position.
        IReadOnlyDictionary<string, int>? activeMapping =
            ordered.Select(page => page.TransactionMapping).FirstOrDefault(mapping => mapping is not null);
        foreach (var page in ordered)
        {
            if (page.TransactionMapping is not null)
            {
                activeMapping = page.TransactionMapping;
                continue;
            }
            if (page.Transactions.Count > 0 || activeMapping is null) continue;
            var continued = CanonicalTransactions(page.RawBlocks, activeMapping);
            if (continued.Transactions.Count == 0) continue;
            page.Transactions = continued.Transactions;
            page.NarrationIssues = continued.NarrationIssues;
            page.TransactionMapping = continued.Mapping;
            page.BroughtForward = continued.BroughtForward;
            page.ClosingRow = continued.ClosingRow;
            var first = continued.Transactions[0];
            var last = continued.Transactions[^1];
            page.FirstDate = first.Date;
            page.LastDate = last.Date;
            page.FirstDebit = first.Debit;
            page.FirstCredit = first.Credit;
            page.FirstBalance = first.Balance;
            page.LastBalance = last.Balance;
            page.ImpliedOpeningRaw = first.Balance is not null
                ? RoundMoney(first.Balance.Value + first.Debit - first.Credit) : null;
        }

        var (summary, summaryIssues) = UniqueSummary(ordered);
        var (transactions, duplicateCount) = CombineTransactions(ordered);
        var issues = new List<string>(summaryIssues);
        issues.AddRange(ordered.SelectMany(page => page.NarrationIssues.Select(issue =>
            $"Source page {page.SourcePage}: {issue}")));
        var pageIssues = new List<string>();
        var corrections = new List<BankOcrCorrection>();
        if (metadata.Bank.Length == 0)
            issues.Add("Bank metadata missing: bank name or IFSC must be recognized from the original statement.");
        if (metadata.AccountNumber.Length == 0)
            issues.Add("Bank metadata missing: account number must be recognized from a source-labeled field.");
        // Within a single physical statement PDF, tiny OCR spelling shifts
        // must not override bank/account identity or create a false blocker.
        // Preserve the raw names in the audit; unrelated names still block.
        var sourceNames = ordered.Select(page => NormalizeUpper(page.AccountName))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        var samePhysicalPdf = ordered.All(page =>
            Path.GetExtension(page.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            page.SourcePath.Equals(ordered[0].SourcePath, StringComparison.OrdinalIgnoreCase));
        if (sourceNames.Length > 1 &&
            !(samePhysicalPdf && sourceNames.All(name => IsMinorAccountNameOcrVariation(sourceNames[0], name))))
            issues.Add("OCR account-holder name differs across statement pages; verify source spelling before import.");

        var printedSeen = new List<int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var inferred = i + 1;
            var page = ordered[i];
            if (page.PrintedPage is not null) printedSeen.Add(page.PrintedPage.Value);
            if (page.PrintedPage is not null && page.PrintedPage.Value != inferred)
                pageIssues.Add($"{Path.GetFileName(page.SourcePath)}: printed page {page.PrintedPage.Value} conflicts with inferred page {inferred}.");
            if (page.PrintedTotal is not null && page.PrintedTotal.Value != ordered.Count)
                pageIssues.Add($"{Path.GetFileName(page.SourcePath)}: printed total pages {page.PrintedTotal.Value} conflicts with {ordered.Count} grouped page(s).");
        }
        var duplicatePrinted = printedSeen.GroupBy(value => value).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicatePrinted.Length > 0) pageIssues.Add($"Duplicate printed page number(s): {string.Join(", ", duplicatePrinted)}.");

        var firstBroughtForward = ordered.FirstOrDefault()?.BroughtForward;
        decimal? opening = null;
        var openingSource = "UNKNOWN";
        if (firstBroughtForward?.Value is not null)
        {
            opening = firstBroughtForward.Value;
            openingSource = firstBroughtForward.DerivedFromMovement
                ? "BROUGHT_FORWARD_MOVEMENT_PENDING_BALANCE_PROOF"
                : "FIRST_PAGE_BROUGHT_FORWARD";
        }
        else if (summary?.Opening is not null) { opening = summary.Opening; openingSource = "STATEMENT_SUMMARY"; }
        else if (transactions.FirstOrDefault()?.Balance is not null)
        {
            var first = transactions[0];
            opening = RoundMoney(first.Balance!.Value + first.OriginalDebit - first.OriginalCredit);
            openingSource = "DERIVED_FROM_FIRST_TRANSACTION";
        }

        if (firstBroughtForward is not null && firstBroughtForward.Value is null)
            issues.Add("BROUGHT FORWARD opening balance could not be recognized from its balance or unambiguous movement cells.");
        // Printed statement opening is before a dated brought-forward movement.
        // A bank may count that movement in its printed credit/debit totals,
        // while AAH displays the post-movement amount as effective opening.
        var printedOpeningFromControl = firstBroughtForward?.Value is not null
            ? RoundMoney(firstBroughtForward.Value.Value +
                firstBroughtForward.Debit - firstBroughtForward.Credit)
            : (decimal?)null;
        if (summary?.Opening is not null && printedOpeningFromControl is not null &&
            !MoneyEqual(summary.Opening, printedOpeningFromControl))
            issues.Add("Statement summary opening plus BROUGHT FORWARD movement does not match effective opening balance.");

        decimal? previous = opening;
        foreach (var row in transactions)
        {
            row.RequiredDebit = row.OriginalDebit;
            row.RequiredCredit = row.OriginalCredit;
            row.FinalDebit = row.OriginalDebit;
            row.FinalCredit = row.OriginalCredit;
            row.VerificationStatus = "UNVERIFIED";

            if (previous is not null && row.Balance is not null)
            {
                var delta = RoundMoney(row.Balance.Value - previous.Value);
                row.RequiredDebit = Math.Max(0m, -delta);
                row.RequiredCredit = Math.Max(0m, delta);
                if (MoneyEqual(row.OriginalDebit, row.RequiredDebit) && MoneyEqual(row.OriginalCredit, row.RequiredCredit))
                {
                    row.VerificationStatus = "VERIFIED";
                }
                else
                {
                    row.IsCorrectionCandidate = true;
                    row.CorrectionPlausibility = CorrectionPlausibility(row.OriginalDebit, row.OriginalCredit, row.RequiredDebit, row.RequiredCredit);
                    row.CorrectionReason = $"Running balance requires Debit {MoneyText(row.RequiredDebit)} / Credit {MoneyText(row.RequiredCredit)}; OCR read Debit {(row.DebitRaw.Length == 0 ? "blank" : row.DebitRaw)} / Credit {(row.CreditRaw.Length == 0 ? "blank" : row.CreditRaw)}.";
                    row.VerificationStatus = "CANDIDATE";
                }
            }
            else if (row.Balance is null)
            {
                row.VerificationStatus = "REVIEW_REQUIRED";
                issues.Add($"Transaction {row.Sequence}: running balance could not be parsed.");
            }
            if (row.Balance is not null) previous = row.Balance;
        }

        var rawEval = EvaluateSummary(summary, opening, transactions, firstBroughtForward, useRequired: false);
        var correctedEval = EvaluateSummary(summary, opening, transactions, firstBroughtForward, useRequired: true);
        var summaryHasAllControls = summary is not null &&
            summary.Opening is not null && summary.DebitCount is not null &&
            summary.CreditCount is not null && summary.TotalDebits is not null &&
            summary.TotalCredits is not null && summary.Closing is not null;
        var summaryProvesCandidates = summaryHasAllControls && correctedEval.Exact && !rawEval.Exact;

        foreach (var row in transactions.Where(row => row.IsCorrectionCandidate))
        {
            var apply = false;
            var proof = string.Empty;
            if (summaryProvesCandidates)
            {
                apply = true;
                proof = "STATEMENT_SUMMARY_AND_RUNNING_BALANCE";
            }
            else if (summary is null && row.CorrectionPlausibility == "HIGH")
            {
                apply = true;
                proof = "RUNNING_BALANCE_HIGH_CONFIDENCE";
            }

            if (apply)
            {
                row.FinalDebit = row.RequiredDebit;
                row.FinalCredit = row.RequiredCredit;
                row.VerificationStatus = "AUTO_CORRECTED";
                row.CorrectionProof = proof;
                corrections.Add(new BankOcrCorrection
                {
                    Sequence = row.Sequence,
                    InferredPage = row.InferredPage,
                    Date = row.DateRaw,
                    Reference = row.Reference,
                    OriginalDebit = row.OriginalDebit,
                    OriginalCredit = row.OriginalCredit,
                    CorrectedDebit = row.RequiredDebit,
                    CorrectedCredit = row.RequiredCredit,
                    Proof = proof,
                    Reason = row.CorrectionReason,
                    SourcePath = row.SourcePath
                });
            }
            else
            {
                row.VerificationStatus = "REVIEW_REQUIRED";
                issues.Add($"Transaction {row.Sequence} ({row.DateRaw}) has a balance/amount mismatch that was not safely auto-corrected.");
            }
        }

        var debitCount = transactions.Count(row => row.FinalDebit > 0.009m);
        var creditCount = transactions.Count(row => row.FinalCredit > 0.009m);
        var totalDebits = RoundMoney(transactions.Sum(row => row.FinalDebit));
        var totalCredits = RoundMoney(transactions.Sum(row => row.FinalCredit));
        var closing = transactions.LastOrDefault(row => row.Balance is not null)?.Balance;

        var openingRowStatus = "NOT_VISIBLE";
        if (firstBroughtForward?.Value is not null && opening is not null)
        {
            openingRowStatus = MoneyEqual(firstBroughtForward.Value, opening) ? "MATCH" : "MISMATCH";
            if (openingRowStatus == "MISMATCH") issues.Add("First-page BROUGHT FORWARD does not match statement opening.");
        }

        var lastClosingRow = ordered.LastOrDefault()?.ClosingRow;
        var closingRowStatus = "NOT_VISIBLE";
        if (lastClosingRow?.Value is not null && closing is not null)
        {
            closingRowStatus = MoneyEqual(lastClosingRow.Value, closing) ? "MATCH" : "MISMATCH";
            if (closingRowStatus == "MISMATCH") issues.Add("Printed CLOSING BALANCE row does not match final transaction balance.");
        }
        if (summary?.Closing is not null && lastClosingRow?.Value is not null && !MoneyEqual(summary.Closing, lastClosingRow.Value))
            issues.Add("Statement summary closing does not match printed CLOSING BALANCE row.");

        var summaryStatus = "NOT_AVAILABLE";
        if (summary is not null)
        {
            // Do not treat missing OCR summary fields as positive proof.
            // A partially parsed summary requires manual review, even if
            // the subset that happened to be read matches the transaction sums.
            if (summary.Opening is null || summary.DebitCount is null ||
                summary.CreditCount is null || summary.TotalDebits is null ||
                summary.TotalCredits is null || summary.Closing is null)
                issues.Add("Statement summary contains unrecognized financial control fields.");
            var checks = new[]
            {
                CompareMoney("opening", summary.Opening, firstBroughtForward is null
                    ? opening : printedOpeningFromControl),
                CompareCount("dr_count", summary.DebitCount, debitCount +
                    (firstBroughtForward?.Debit > 0.009m ? 1 : 0)),
                CompareCount("cr_count", summary.CreditCount, creditCount +
                    (firstBroughtForward?.Credit > 0.009m ? 1 : 0)),
                CompareMoney("total_debits", summary.TotalDebits, RoundMoney(totalDebits +
                    (firstBroughtForward?.Debit ?? 0m))),
                CompareMoney("total_credits", summary.TotalCredits, RoundMoney(totalCredits +
                    (firstBroughtForward?.Credit ?? 0m))),
                CompareMoney("closing", summary.Closing, closing)
            };
            foreach (var check in checks.Where(check => check.Status == "MISMATCH")) issues.Add($"Statement summary {check.Name} mismatch.");
            summaryStatus = checks.All(check => check.Status is "MATCH" or "NOT_PRINTED") ? "VERIFIED" : "REVIEW_REQUIRED";
        }

        // OCR can balance every amount while still misreading mandatory text.
        // Never silently promote a financial-only match as a verified import when
        // a transaction date, narration, or supplied value-date is unreadable.
        if (transactions.Count == 0)
            issues.Add("No transaction rows were recognized; automatic import is blocked.");
        // BROUGHT FORWARD is an audited opening-balance control, not a transaction.
        // The source amount is retained for printed-summary reconciliation above.

        var statementStart = ParseDate(metadata.PeriodStart);
        var statementEnd = ParseDate(metadata.PeriodEnd);
        if (statementStart is not null && statementEnd is not null && statementStart > statementEnd)
            issues.Add("OCR statement period is reversed.");

        DateTime? lastDate = null;
        foreach (var row in transactions)
        {
            if (row.Date is not null && statementStart is not null && row.Date < statementStart)
                issues.Add($"Transaction {row.Sequence}: posting date is earlier than the printed statement period.");
            if (row.Date is not null && statementEnd is not null && row.Date > statementEnd)
                issues.Add($"Transaction {row.Sequence}: posting date is later than the printed statement period.");
            if (row.Date is null)
                issues.Add($"Transaction {row.Sequence}: posting date was not recognized ({row.DateRaw}).");
            if (string.IsNullOrWhiteSpace(row.Description))
                issues.Add($"Transaction {row.Sequence}: narration is missing.");
            if (!string.IsNullOrWhiteSpace(row.ValueDateRaw) && row.ValueDate is null)
                issues.Add($"Transaction {row.Sequence}: value date was not recognized ({row.ValueDateRaw}).");
            if (row.Date is not null && lastDate is not null && row.Date.Value < lastDate.Value)
                issues.Add($"Transaction dates decrease at sequence {row.Sequence}: {row.DateRaw}.");
            if (row.Date is not null) lastDate = row.Date;
        }

        var boundaries = new Dictionary<int, string>();
        for (var pageNo = 1; pageNo < ordered.Count; pageNo++)
        {
            var leftRows = transactions.Where(row => row.InferredPage == pageNo).ToList();
            var rightRows = transactions.Where(row => row.InferredPage == pageNo + 1).ToList();
            var leftLast = leftRows.LastOrDefault(row => row.Balance is not null)?.Balance;
            var rightFirst = rightRows.FirstOrDefault();
            var status = "NOT_VERIFIED";
            if (leftLast is not null && rightFirst?.Balance is not null)
            {
                var expected = RoundMoney(leftLast.Value - rightFirst.FinalDebit + rightFirst.FinalCredit);
                status = MoneyEqual(expected, rightFirst.Balance) ? "VERIFIED" : "MISMATCH";
                if (status == "MISMATCH") pageIssues.Add($"Inferred page {pageNo} -> {pageNo + 1} balance continuity mismatch.");
            }
            boundaries[pageNo] = status;
        }

        var pageAudit = new List<BankVerifiedPageAudit>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var inferred = i + 1;
            var page = ordered[i];
            var rows = transactions.Where(row => row.InferredPage == inferred).ToList();
            pageAudit.Add(new BankVerifiedPageAudit
            {
                InferredPage = inferred,
                PrintedPage = page.PrintedPage,
                PrintedTotal = page.PrintedTotal,
                SourcePath = page.SourcePath,
                SourcePage = page.SourcePage,
                PageBankDetected = page.Bank,
                DocumentBank = metadata.Bank,
                PageAccountDetected = page.AccountNumber,
                DocumentAccount = metadata.AccountNumber,
                FirstDate = page.FirstDate,
                LastDate = page.LastDate,
                FirstBalance = rows.FirstOrDefault()?.Balance,
                LastBalance = rows.LastOrDefault(row => row.Balance is not null)?.Balance,
                BoundaryToNext = inferred < ordered.Count
                    ? (boundaries.TryGetValue(inferred, out var boundaryStatus) ? boundaryStatus : "NOT_VERIFIED")
                    : "END"
            });
        }

        // An unresolved page boundary must not be promoted to VERIFIED solely
        // because printed page numbers are sequential.
        var boundariesVerified = boundaries.Values.All(value => value == "VERIFIED");
        var pageStatus = "VERIFIED";
        if (pageIssues.Count > 0 || !boundariesVerified) pageStatus = "REVIEW_REQUIRED";
        else if (pageAudit.Any(page => page.PrintedPage is null))
            pageStatus = "INFERRED_VERIFIED";

        if (ordered.Any(page => page.Transactions.Count == 0 && page.Summary is null))
            issues.Add("A statement page has no recognized transaction rows or summary.");
        if (pageStatus == "REVIEW_REQUIRED")
            issues.Add("Statement page sequencing or balance continuity requires review.");
        if (metadata.BankStatus == "REVIEW_REQUIRED") issues.Add("Conflicting bank evidence in grouped pages.");
        if (metadata.AccountConflict) issues.Add("Conflicting account numbers in grouped pages.");
        if (metadata.PeriodConflict) issues.Add("Conflicting statement periods in grouped pages.");
        if (metadata.IfscConflict) issues.Add("Conflicting IFSC codes in grouped pages.");
        issues.AddRange(pageIssues);
        if (duplicateCount > 0) { /* exact overlap duplicates are safely omitted and kept in the audit count */ }

        // If source OCR visibly mentions a printed summary but its financial
        // fields cannot be parsed, this is NOT a summary-free statement. Never
        // bypass an unreadable printed amount through a brought-forward value.
        var unparsedPrintedSummary = summary is null &&
            ordered.Any(page => page.RawBlocks.Any(block =>
                NormalizeUpper(block.Content).Contains("STATEMENT SUMMARY", StringComparison.Ordinal)));
        if (unparsedPrintedSummary)
            issues.Add("Printed statement summary is present in source OCR but its financial controls are unreadable; review original scanned page.");

        // Without a statement summary, the first transaction's inferred
        // opening balance is circular evidence. Require an independently printed
        // BROUGHT FORWARD amount before accepting a genuinely summary-free document.
        var multiPageScannedPdfWithoutSummary = summary is null && ordered.Count > 1 &&
            ordered.Any(page => Path.GetExtension(page.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (multiPageScannedPdfWithoutSummary)
            issues.Add("Multi-page scanned PDF has no readable statement summary: cannot establish whether printed financial controls were omitted by OCR. Review source pages.");
        var pageVerifiedWithoutSummary = summary is null &&
            !multiPageScannedPdfWithoutSummary &&
            !unparsedPrintedSummary &&
            firstBroughtForward?.Value is not null &&
            !firstBroughtForward.DerivedFromMovement &&
            openingSource == "FIRST_PAGE_BROUGHT_FORWARD" &&
            (pageStatus.Equals("VERIFIED", StringComparison.Ordinal) ||
             pageStatus.Equals("INFERRED_VERIFIED", StringComparison.Ordinal));
        // A plausible arithmetic correction never substitutes for independent
        // source evidence. Do not let AUTO_CORRECTED bypass a missing or unusable
        // printed summary unless a printed BROUGHT FORWARD anchors the opening.
        var hasIndependentFinancialProof = summaryStatus == "VERIFIED" || pageVerifiedWithoutSummary;
        var overall = issues.Count > 0 || !hasIndependentFinancialProof
            ? "REVIEW_REQUIRED"
            : corrections.Count > 0 ? "VERIFIED_WITH_CORRECTIONS" : "VERIFIED";

        return new BankVerifiedDocument
        {
            DocumentNumber = documentNumber,
            Status = overall,
            PageStatus = pageStatus,
            SummaryStatus = summaryStatus,
            BankName = metadata.Bank,
            Ifsc = metadata.Ifsc,
            AccountNumber = metadata.AccountNumber,
            AccountType = metadata.AccountType,
            AccountName = metadata.AccountName,
            BranchName = metadata.BranchName,
            StatementFrom = metadata.PeriodStart,
            StatementTo = metadata.PeriodEnd,
            OpeningBalance = opening,
            OpeningSource = openingSource,
            PrintedSummaryOpeningBalance = summary?.Opening,
            BroughtForwardDebit = firstBroughtForward?.Debit ?? 0m,
            BroughtForwardCredit = firstBroughtForward?.Credit ?? 0m,
            OpeningRowStatus = openingRowStatus,
            ClosingBalance = closing,
            ClosingRowStatus = closingRowStatus,
            DebitCount = debitCount,
            CreditCount = creditCount,
            TotalDebits = totalDebits,
            TotalCredits = totalCredits,
            Transactions = transactions,
            Corrections = corrections,
            Issues = issues.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Pages = pageAudit,
            SourcePaths = ordered.Select(page => page.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DuplicateTransactionCount = duplicateCount
        };
    }

    private static AggregatedMetadata AggregateMetadata(IReadOnlyList<PageProfile> ordered)
    {
        var bankVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bankEvidence = new List<string>();
        var ifscs = new List<string>();
        var accounts = new List<string>();
        var accountTypes = new List<string>();
        var accountNames = new List<string>();
        var branchNames = new List<string>();
        var periods = new List<(string Start, string End)>();

        foreach (var page in ordered)
        {
            if (page.Bank.Length > 0)
            {
                var weight = page.BankStatus == "VERIFIED" ? 3 : 1;
                bankVotes[page.Bank] = bankVotes.TryGetValue(page.Bank, out var value) ? value + weight : weight;
            }
            bankEvidence.AddRange(page.BankEvidence);
            if (page.Ifsc.Length > 0) ifscs.Add(page.Ifsc);
            if (page.AccountNumber.Length > 0) accounts.Add(page.AccountNumber);
            if (page.AccountType.Length > 0) accountTypes.Add(page.AccountType);
            if (page.AccountName.Length > 0) accountNames.Add(page.AccountName);
            if (page.BranchName.Length > 0) branchNames.Add(page.BranchName);
            if (page.PeriodStart.Length > 0 || page.PeriodEnd.Length > 0) periods.Add((page.PeriodStart, page.PeriodEnd));
        }

        var banks = bankVotes.Keys.ToList();
        var accountValues = accounts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ifscValues = ifscs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var periodValues = periods.Distinct().ToList();
        var bank = bankVotes.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault().Key ?? string.Empty;

        return new AggregatedMetadata
        {
            Bank = bank,
            BankStatus = banks.Count > 1 ? "REVIEW_REQUIRED" : bank.Length > 0 ? "VERIFIED" : "NOT_DETECTED",
            BankEvidence = bankEvidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Ifsc = ifscValues.FirstOrDefault() ?? string.Empty,
            IfscConflict = ifscValues.Count > 1,
            AccountNumber = accountValues.FirstOrDefault() ?? string.Empty,
            AccountConflict = accountValues.Count > 1,
            AccountType = MostCommon(accountTypes),
            AccountName = MostCommon(accountNames),
            BranchName = MostCommon(branchNames),
            PeriodStart = periodValues.FirstOrDefault().Start ?? string.Empty,
            PeriodEnd = periodValues.FirstOrDefault().End ?? string.Empty,
            PeriodConflict = periodValues.Count > 1
        };
    }

    private static (StatementSummary? Summary, IReadOnlyList<string> Issues) UniqueSummary(IReadOnlyList<PageProfile> ordered)
    {
        var summaries = ordered.Where(page => page.Summary is not null).Select(page => page.Summary!).ToList();
        if (summaries.Count == 0) return (null, []);
        var first = summaries[0];
        var issues = new List<string>();
        foreach (var other in summaries.Skip(1))
        {
            if (BothDifferent(first.Opening, other.Opening)) issues.Add("Conflicting statement summary opening.");
            if (BothDifferent(first.DebitCount, other.DebitCount)) issues.Add("Conflicting statement summary debit count.");
            if (BothDifferent(first.CreditCount, other.CreditCount)) issues.Add("Conflicting statement summary credit count.");
            if (BothDifferent(first.TotalDebits, other.TotalDebits)) issues.Add("Conflicting statement summary total debits.");
            if (BothDifferent(first.TotalCredits, other.TotalCredits)) issues.Add("Conflicting statement summary total credits.");
            if (BothDifferent(first.Closing, other.Closing)) issues.Add("Conflicting statement summary closing.");
        }
        return (first, issues);
    }

    private static (List<BankVerifiedTransaction> Rows, int DuplicateCount) CombineTransactions(IReadOnlyList<PageProfile> ordered)
    {
        var rows = new List<BankVerifiedTransaction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = 0;
        var sequence = 0;

        for (var p = 0; p < ordered.Count; p++)
        {
            var page = ordered[p];
            foreach (var tx in page.Transactions)
            {
                var key = string.Join("|",
                    tx.DateRaw, tx.ValueDateRaw, NormalizeUpper(tx.Description), NormalizeUpper(tx.Reference),
                    tx.Debit.ToString("0.00", CultureInfo.InvariantCulture),
                    tx.Credit.ToString("0.00", CultureInfo.InvariantCulture),
                    tx.Balance?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty);
                if (!seen.Add(key)) { duplicates++; continue; }
                sequence++;
                rows.Add(new BankVerifiedTransaction
                {
                    Sequence = sequence,
                    InferredPage = p + 1,
                    PrintedPage = page.PrintedPage,
                    SourcePath = page.SourcePath,
                    SourcePage = page.SourcePage,
                    DateRaw = tx.DateRaw,
                    Date = tx.Date,
                    ValueDateRaw = tx.ValueDateRaw,
                    ValueDate = tx.ValueDate,
                    Description = tx.Description,
                    Reference = tx.Reference,
                    DebitRaw = tx.DebitRaw,
                    CreditRaw = tx.CreditRaw,
                    BalanceRaw = tx.BalanceRaw,
                    OriginalDebit = tx.Debit,
                    OriginalCredit = tx.Credit,
                    FinalDebit = tx.Debit,
                    FinalCredit = tx.Credit,
                    Balance = tx.Balance
                });
            }
        }
        return (rows, duplicates);
    }

    private static SummaryEvaluation EvaluateSummary(StatementSummary? summary, decimal? opening, IReadOnlyList<BankVerifiedTransaction> rows, BalanceEvidence? broughtForward, bool useRequired)
    {
        var debits = rows.Select(row => useRequired ? row.RequiredDebit : row.OriginalDebit).ToArray();
        var credits = rows.Select(row => useRequired ? row.RequiredCredit : row.OriginalCredit).ToArray();
        var debitCount = debits.Count(value => value > 0.009m);
        var creditCount = credits.Count(value => value > 0.009m);
        var totalDebits = RoundMoney(debits.Sum());
        var totalCredits = RoundMoney(credits.Sum());
        var closing = rows.LastOrDefault(row => row.Balance is not null)?.Balance;
        if (summary is null) return new SummaryEvaluation(false);

        // Printed bank controls may include a dated brought-forward row.
        // Adjust only the printed-summary side; AAH transaction/voucher totals
        // always exclude that opening row.
        var printedOpening = broughtForward is null || opening is null
            ? opening
            : RoundMoney(opening.Value + broughtForward.Debit - broughtForward.Credit);
        var exact =
            MatchOrNotPrinted(summary.Opening, printedOpening) &&
            MatchOrNotPrinted(summary.DebitCount, debitCount + (broughtForward?.Debit > 0.009m ? 1 : 0)) &&
            MatchOrNotPrinted(summary.CreditCount, creditCount + (broughtForward?.Credit > 0.009m ? 1 : 0)) &&
            MatchOrNotPrinted(summary.TotalDebits, RoundMoney(totalDebits + (broughtForward?.Debit ?? 0m))) &&
            MatchOrNotPrinted(summary.TotalCredits, RoundMoney(totalCredits + (broughtForward?.Credit ?? 0m))) &&
            MatchOrNotPrinted(summary.Closing, closing);
        return new SummaryEvaluation(exact);
    }

    private static bool GroupingEdge(PageProfile left, PageProfile right)
    {
        if (IdentityConflict(left, right)) return false;
        // Physical consecutive pages in the SAME PDF are source-backed adjacency.
        // An OCR header, balance or account may be missing on a continuation page.
        // Still reject explicit account, bank or period conflicts above.
        if (Path.GetExtension(left.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            left.SourcePath.Equals(right.SourcePath, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(left.SourcePage - right.SourcePage) == 1)
            return true;
        if (Math.Max(TransitionScore(left, right), TransitionScore(right, left)) >= 150d) return true;
        if (left.AccountNumber.Length > 0 && left.AccountNumber.Equals(right.AccountNumber, StringComparison.OrdinalIgnoreCase) && !PeriodsConflict(left, right)) return true;
        if (left.PeriodStart.Length > 0 && left.PeriodStart == right.PeriodStart && left.PeriodEnd == right.PeriodEnd && left.Bank.Length > 0 && left.Bank.Equals(right.Bank, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool ComponentsCompatible(IReadOnlyList<PageProfile> left, IReadOnlyList<PageProfile> right) =>
        !left.Any(a => right.Any(b => IdentityConflict(a, b)));

    private static bool IdentityConflict(PageProfile left, PageProfile right)
    {
        if (left.AccountNumber.Length > 0 && right.AccountNumber.Length > 0 && !left.AccountNumber.Equals(right.AccountNumber, StringComparison.OrdinalIgnoreCase)) return true;
        if (PeriodsConflict(left, right)) return true;
        return left.Bank.Length > 0 && right.Bank.Length > 0 && left.BankStatus == "VERIFIED" && right.BankStatus == "VERIFIED" && !left.Bank.Equals(right.Bank, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PeriodsConflict(PageProfile left, PageProfile right)
    {
        if (left.PeriodStart.Length > 0 && right.PeriodStart.Length > 0 && left.PeriodStart != right.PeriodStart) return true;
        return left.PeriodEnd.Length > 0 && right.PeriodEnd.Length > 0 && left.PeriodEnd != right.PeriodEnd;
    }

    private static double TransitionScore(PageProfile left, PageProfile right)
    {
        if (IdentityConflict(left, right)) return -10000d;
        var score = 0d;
        if (left.LastBalance is not null && right.ImpliedOpeningRaw is not null)
        {
            if (MoneyEqual(left.LastBalance, right.ImpliedOpeningRaw)) score += 180d;
            else if (right.FirstBalance is not null)
            {
                var delta = RoundMoney(right.FirstBalance.Value - left.LastBalance.Value);
                var requiredDebit = Math.Max(0m, -delta);
                var requiredCredit = Math.Max(0m, delta);
                if (requiredDebit > 0m && right.FirstDebit > 0m && right.FirstCredit == 0m) score += AmountSimilarity(right.FirstDebit, requiredDebit) * 0.8d;
                else if (requiredCredit > 0m && right.FirstCredit > 0m && right.FirstDebit == 0m) score += AmountSimilarity(right.FirstCredit, requiredCredit) * 0.8d;
            }
        }
        if (right.BroughtForward?.Value is not null && left.LastBalance is not null && MoneyEqual(left.LastBalance, right.BroughtForward!.Value)) score += 180d;
        if (left.PrintedPage is not null && right.PrintedPage is not null)
        {
            if (right.PrintedPage == left.PrintedPage + 1) score += 150d;
            else if (right.PrintedPage > left.PrintedPage) score += 20d;
            else score -= 250d;
        }
        if (left.LastDate is not null && right.FirstDate is not null)
        {
            var gap = (right.FirstDate.Value - left.LastDate.Value).TotalDays;
            if (gap < 0d) score -= 160d;
            else score += Math.Max(5d, 40d - Math.Min(gap, 180d) * 0.15d);
        }
        if (left.AccountNumber.Length > 0 && left.AccountNumber.Equals(right.AccountNumber, StringComparison.OrdinalIgnoreCase)) score += 120d;
        if (left.PeriodStart.Length > 0 && left.PeriodStart == right.PeriodStart && left.PeriodEnd == right.PeriodEnd) score += 70d;
        return score;
    }

    private static double PositionScore(PageProfile page, int zeroBasedPosition, int total)
    {
        var score = 0d;
        var inferred = zeroBasedPosition + 1;
        if (page.PrintedPage is not null)
            score += inferred == page.PrintedPage.Value ? 240d : -45d * Math.Abs(inferred - page.PrintedPage.Value);
        if (page.HasHeader || page.BroughtForward is not null)
            score += zeroBasedPosition == 0 ? 180d : -25d * zeroBasedPosition;
        if (page.HasEnd || page.Summary is not null || page.ClosingRow is not null)
            score += zeroBasedPosition == total - 1 ? 180d : -30d * (total - 1 - zeroBasedPosition);
        return score;
    }

    private static int AmountSimilarity(decimal rawAmount, decimal requiredAmount)
    {
        var raw = Math.Abs(rawAmount);
        var required = Math.Abs(requiredAmount);
        if (required <= 0.009m) return 0;
        if (MoneyEqual(raw, required)) return 100;
        if (raw <= 0.009m) return 20;
        var ratio = raw / required;
        foreach (var target in new[] { 10m, 100m, 1000m, 0.1m, 0.01m, 0.001m })
            if (Math.Abs(ratio - target) <= Math.Abs(target) * 0.005m) return 80;
        var rawDigits = Regex.Replace(raw.ToString("0.00", CultureInfo.InvariantCulture), @"\D", string.Empty).TrimEnd('0');
        var reqDigits = Regex.Replace(required.ToString("0.00", CultureInfo.InvariantCulture), @"\D", string.Empty).TrimEnd('0');
        if (rawDigits == reqDigits) return 70;
        if (rawDigits.Contains(reqDigits, StringComparison.Ordinal) || reqDigits.Contains(rawDigits, StringComparison.Ordinal)) return 45;
        return 0;
    }

    private static string CorrectionPlausibility(decimal rawDebit, decimal rawCredit, decimal requiredDebit, decimal requiredCredit)
    {
        if (requiredDebit > 0m && requiredCredit == 0m && rawDebit > 0m && rawCredit == 0m)
        {
            var similarity = AmountSimilarity(rawDebit, requiredDebit);
            if (similarity >= 70) return "HIGH";
            if (similarity >= 40) return "MEDIUM";
        }
        if (requiredCredit > 0m && requiredDebit == 0m && rawCredit > 0m && rawDebit == 0m)
        {
            var similarity = AmountSimilarity(rawCredit, requiredCredit);
            if (similarity >= 70) return "HIGH";
            if (similarity >= 40) return "MEDIUM";
        }
        if (rawDebit == 0m && rawCredit == 0m && (requiredDebit > 0m || requiredCredit > 0m)) return "MEDIUM";
        return "LOW";
    }

    private static IReadOnlyList<IReadOnlyList<string>> TableRows(string content)
    {
        if (content.Contains("<table", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new List<IReadOnlyList<string>>();
            // PaddleOCR HTML can put a DATE in a rowspan=2 cell while the
            // narration, debit, credit and balance arrive on the second <tr>.
            // Expanding the source HTML grid is required BEFORE applying a
            // transaction column map. Never infer a date from adjacent rows.
            var carried = new Dictionary<int, (string Value, int Remaining)>();
            foreach (Match rowMatch in HtmlRowRegex.Matches(content))
            {
                var grid = new SortedDictionary<int, string>();
                foreach (var cell in carried.ToArray())
                {
                    grid[cell.Key] = cell.Value.Value;
                    if (cell.Value.Remaining == 1) carried.Remove(cell.Key);
                    else carried[cell.Key] = (cell.Value.Value, cell.Value.Remaining - 1);
                }
                var column = 0;
                foreach (Match cellMatch in HtmlCellRegex.Matches(rowMatch.Groups[1].Value))
                {
                    while (grid.ContainsKey(column)) column++;
                    var attributes = cellMatch.Groups["attrs"].Value;
                    var text = HtmlTagRegex.Replace(
                        cellMatch.Groups["body"].Value
                            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
                            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
                            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase), " ");
                    var value = Normalize(WebUtility.HtmlDecode(text));
                    int Span(Regex regex)
                    {
                        var match = regex.Match(attributes);
                        return match.Success && int.TryParse(match.Groups["count"].Value, out var count)
                            ? Math.Clamp(count, 1, 32) : 1;
                    }
                    var columnSpan = Span(HtmlColspanRegex);
                    var rowSpan = Span(HtmlRowspanRegex);
                    grid[column] = value;
                    for (var offset = 1; offset < columnSpan; offset++)
                        grid[column + offset] = string.Empty;
                    if (rowSpan > 1)
                        for (var offset = 0; offset < columnSpan; offset++)
                            carried[column + offset] = (offset == 0 ? value : string.Empty, rowSpan - 1);
                    column += columnSpan;
                }
                if (grid.Count > 0)
                    rows.Add(Enumerable.Range(0, grid.Keys.Max() + 1)
                        .Select(index => grid.TryGetValue(index, out var value) ? value : string.Empty)
                        .ToArray());
            }
            if (rows.Count > 0)
            {
                var width = rows.Max(row => row.Count);
                return rows.Select(row => PadRow(row, width)).ToList();
            }
        }

        // Markdown/pipe OCR can wrap one cell across physical text lines.
        // Reassemble a source row up to its closing pipe before splitting cells,
        // rather than converting a narration line into a false transaction.
        var logicalLines = new List<string>();
        var pending = string.Empty;
        foreach (var physicalLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = physicalLine.Trim();
            if (line.Length == 0) continue;
            if (pending.Length == 0)
            {
                if (!line.Contains('|')) continue;
                pending = line;
            }
            else
            {
                pending += " " + line;
            }
            if (!pending.EndsWith('|')) continue;
            logicalLines.Add(pending);
            pending = string.Empty;
        }
        if (pending.Contains('|')) logicalLines.Add(pending);
        var pipeRows = logicalLines
            .Select(line => line.Trim().Trim('|').Split('|').Select(Normalize).ToList())
            .Where(row => !IsSeparatorRow(row))
            .ToList();
        if (pipeRows.Count == 0) return [];
        var maxWidth = pipeRows.Max(row => row.Count);
        return pipeRows.Select(row => PadRow(row, maxWidth)).ToList();
    }


    private static IReadOnlyList<string> PadRow(IReadOnlyList<string> row, int width)
    {
        var result = row.ToList();
        while (result.Count < width) result.Add(string.Empty);
        return result;
    }

    private static bool IsSeparatorRow(IReadOnlyList<string> row)
    {
        var values = row.Where(value => value.Length > 0).Select(value => value.Replace(" ", string.Empty, StringComparison.Ordinal)).ToList();
        return values.Count > 0 && values.All(value => Regex.IsMatch(value, @"^:?-{3,}:?$") );
    }

    private static string NonTableText(IReadOnlyList<OcrBlock> blocks) =>
        string.Join("\n", blocks.Where(block => NormalizeUpper(block.Label) != "TABLE").Select(block => Regex.Replace(Clean(block.Content), @"^#+\s*", string.Empty)).Where(value => value.Length > 0));

    private static string AllText(IReadOnlyList<OcrBlock> blocks)
    {
        var parts = new List<string>();
        foreach (var block in blocks)
        {
            var content = Clean(block.Content);
            if (content.Length == 0) continue;
            if (content.Contains("<table", StringComparison.OrdinalIgnoreCase))
                parts.AddRange(TableRows(content).Select(row => string.Join(" ", row.Where(value => value.Length > 0))));
            else
                parts.Add(Regex.Replace(content, @"^#+\s*", string.Empty));
        }
        return string.Join("\n", parts);
    }

    private static string Clean(string? value) => (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    private static string Normalize(string? value) => Regex.Replace(Clean(value), @"\s+", " ").Trim();
    private static string NormalizeUpper(string? value) => Normalize(value).ToUpperInvariant();

    private static DateTime? ParseDate(string? value)
    {
        var text = Clean(value);
        // Local OCR occasionally reads the source date separator as a colon
        // (14:08/26 instead of 14/08/26). Only repair this strict DD:MM/YY
        // shape; retain the unmodified DateRaw in financial audit evidence.
        if (Regex.IsMatch(text, @"^\d{1,2}:\d{1,2}[/.-]\d{2,4}$"))
            text = text.Replace(':', '/');
        foreach (var format in DateFormats)
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        var match = DateTextRegex.Match(text);
        if (!match.Success) return null;
        return DateTime.TryParseExact($"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}", "d-M-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static string NormalizeDate(string value) => ParseDate(value)?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? Normalize(value);

    private static decimal? ParseMoney(string? value)
    {
        var text = Clean(value);
        if (text.Length == 0) return null;
        var upper = text.ToUpperInvariant();
        var negative = upper.EndsWith("DR", StringComparison.Ordinal) || upper.EndsWith(" DR", StringComparison.Ordinal);
        var stripped = upper.Replace("CR", string.Empty, StringComparison.Ordinal).Replace("DR", string.Empty, StringComparison.Ordinal);
        stripped = Regex.Replace(stripped, @"[^0-9.\-()]", string.Empty);
        if (stripped.Length == 0) return null;
        if (stripped.StartsWith('(') && stripped.EndsWith(')')) { negative = true; stripped = stripped[1..^1]; }
        if (!decimal.TryParse(stripped, NumberStyles.Number | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return null;
        if (number < 0m) return number;
        return negative ? -number : number;
    }

    private static decimal? ParseSignedMoney(string? value) => ParseMoney(value);
    private static decimal? AbsNullable(decimal? value) => value is null ? null : Math.Abs(value.Value);
    private static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    // Bank statements settle at two decimal places. Even a one-paisa difference
    // can indicate an omitted transaction or OCR digit error; no tolerance.
    private static bool MoneyEqual(decimal? left, decimal? right) =>
        left is not null && right is not null &&
        RoundMoney(left.Value) == RoundMoney(right.Value);
    private static bool BothDifferent(decimal? left, decimal? right) => left is not null && right is not null && !MoneyEqual(left, right);
    private static bool BothDifferent(int? left, int? right) => left is not null && right is not null && left != right;
    private static bool MatchOrNotPrinted(decimal? printed, decimal? calculated) => printed is null || MoneyEqual(printed, calculated);
    private static bool MatchOrNotPrinted(int? printed, int calculated) => printed is null || printed == calculated;
    private static string MoneyText(decimal value) => Math.Abs(value).ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    private static int? ParseNullableInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static bool IsMinorAccountNameOcrVariation(string first, string second)
    {
        // Only tolerate <=2 OCR letter errors in a long account-holder label.
        // This is not a fuzzy account-number match, nor a new ledger identity.
        var a = Regex.Replace(NormalizeUpper(first), @"[^A-Z0-9]", "");
        var b = Regex.Replace(NormalizeUpper(second), @"[^A-Z0-9]", "");
        if (a.Equals(b, StringComparison.Ordinal)) return true;
        if (Math.Min(a.Length, b.Length) < 18 || Math.Abs(a.Length - b.Length) > 2) return false;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length] <= 2;
    }

    private static string MostCommon(IEnumerable<string> values) => values.Where(value => value.Length > 0).GroupBy(value => value, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault() ?? string.Empty;
    private static string JsonText(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString() : string.Empty;

    private static SummaryCheck CompareMoney(string name, decimal? printed, decimal? calculated) =>
        new(name, printed is null ? "NOT_PRINTED" : MoneyEqual(printed, calculated) ? "MATCH" : "MISMATCH");
    private static SummaryCheck CompareCount(string name, int? printed, int calculated) =>
        new(name, printed is null ? "NOT_PRINTED" : printed == calculated ? "MATCH" : "MISMATCH");

    private sealed class OcrBlock { public string Label { get; set; } = string.Empty; public string Content { get; set; } = string.Empty; }
    private sealed record BankNameMatch(int Start, int End, string Bank, string Alias);
    private sealed record BalanceEvidence(
        string Raw, decimal? Value, decimal Debit = 0m, decimal Credit = 0m,
        bool DerivedFromMovement = false);
    private sealed record IdentifiedTable(
        IReadOnlyList<IReadOnlyList<string>> Rows,
        IReadOnlyDictionary<string, int> Mapping,
        bool HasHeader);
    private sealed record DpState(double Score, IReadOnlyList<int> Path);
    private sealed record SummaryEvaluation(bool Exact);
    private sealed record SummaryCheck(string Name, string Status);

    private sealed class PageMetadata
    {
        public string Bank { get; set; } = string.Empty;
        public string BankStatus { get; set; } = string.Empty;
        public IReadOnlyList<string> BankEvidence { get; set; } = [];
        public string Ifsc { get; set; } = string.Empty;
        public string AccountNumber { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
    }

    private sealed class PageTransaction
    {
        public int SourceRow { get; set; }
        public string DateRaw { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
        public string ValueDateRaw { get; set; } = string.Empty;
        public DateTime? ValueDate { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public string DebitRaw { get; set; } = string.Empty;
        public string CreditRaw { get; set; } = string.Empty;
        public string BalanceRaw { get; set; } = string.Empty;
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public decimal? Balance { get; set; }
    }

    private sealed class CanonicalTransactionTable
    {
        public IReadOnlyList<PageTransaction> Transactions { get; set; } = [];
        public IReadOnlyList<string> NarrationIssues { get; set; } = [];
        public IReadOnlyDictionary<string, int>? Mapping { get; set; }
        public BalanceEvidence? BroughtForward { get; set; }
        public BalanceEvidence? ClosingRow { get; set; }
    }

    private sealed class StatementSummary
    {
        public decimal? Opening { get; set; }
        public int? DebitCount { get; set; }
        public int? CreditCount { get; set; }
        public decimal? TotalDebits { get; set; }
        public decimal? TotalCredits { get; set; }
        public decimal? Closing { get; set; }
    }

    private sealed class PageProfile
    {
        public string Id { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public int SourceIndex { get; set; }
        public int SourcePage { get; set; }
        public string Bank { get; set; } = string.Empty;
        public string BankStatus { get; set; } = string.Empty;
        public IReadOnlyList<string> BankEvidence { get; set; } = [];
        public string Ifsc { get; set; } = string.Empty;
        public string AccountNumber { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string PeriodStart { get; set; } = string.Empty;
        public string PeriodEnd { get; set; } = string.Empty;
        public int? PrintedPage { get; set; }
        public int? PrintedTotal { get; set; }
        public IReadOnlyList<PageTransaction> Transactions { get; set; } = [];
        public IReadOnlyList<string> NarrationIssues { get; set; } = [];
        public IReadOnlyDictionary<string, int>? TransactionMapping { get; set; }
        public IReadOnlyList<OcrBlock> RawBlocks { get; set; } = [];
        public BalanceEvidence? BroughtForward { get; set; }
        public BalanceEvidence? ClosingRow { get; set; }
        public StatementSummary? Summary { get; set; }
        public DateTime? FirstDate { get; set; }
        public DateTime? LastDate { get; set; }
        public decimal? FirstBalance { get; set; }
        public decimal? LastBalance { get; set; }
        public decimal FirstDebit { get; set; }
        public decimal FirstCredit { get; set; }
        public decimal? ImpliedOpeningRaw { get; set; }
        public bool HasHeader { get; set; }
        public bool HasEnd { get; set; }
    }

    private sealed class AggregatedMetadata
    {
        public string Bank { get; set; } = string.Empty;
        public string BankStatus { get; set; } = string.Empty;
        public IReadOnlyList<string> BankEvidence { get; set; } = [];
        public string Ifsc { get; set; } = string.Empty;
        public bool IfscConflict { get; set; }
        public string AccountNumber { get; set; } = string.Empty;
        public bool AccountConflict { get; set; }
        public string AccountType { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string PeriodStart { get; set; } = string.Empty;
        public string PeriodEnd { get; set; } = string.Empty;
        public bool PeriodConflict { get; set; }
    }
}

internal sealed class BankVerifiedDocumentBatch
{
    public bool WasSuccessful { get; set; }
    public IReadOnlyList<BankVerifiedDocument> Documents { get; set; } = [];
    public IReadOnlyList<string> ParseIssues { get; set; } = [];
    public IReadOnlyList<string> RecognitionErrors { get; set; } = [];
    public string AuditPath { get; set; } = string.Empty;
    public string FailureMessage { get; set; } = string.Empty;

    public static BankVerifiedDocumentBatch Failed(string message, IReadOnlyList<string>? issues = null) =>
        new() { FailureMessage = message, ParseIssues = issues ?? [], WasSuccessful = false };
}

internal sealed class BankVerifiedDocument
{
    public int DocumentNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PageStatus { get; set; } = string.Empty;
    public string SummaryStatus { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string Ifsc { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string StatementFrom { get; set; } = string.Empty;
    public string StatementTo { get; set; } = string.Empty;
    public decimal? OpeningBalance { get; set; }
    public string OpeningSource { get; set; } = string.Empty;
    public decimal? PrintedSummaryOpeningBalance { get; set; }
    public decimal BroughtForwardDebit { get; set; }
    public decimal BroughtForwardCredit { get; set; }
    public string OpeningRowStatus { get; set; } = string.Empty;
    public decimal? ClosingBalance { get; set; }
    public string ClosingRowStatus { get; set; } = string.Empty;
    public int DebitCount { get; set; }
    public int CreditCount { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public IReadOnlyList<BankVerifiedTransaction> Transactions { get; set; } = [];
    public IReadOnlyList<BankOcrCorrection> Corrections { get; set; } = [];
    public IReadOnlyList<string> Issues { get; set; } = [];
    public IReadOnlyList<BankVerifiedPageAudit> Pages { get; set; } = [];
    public IReadOnlyList<string> SourcePaths { get; set; } = [];
    public int DuplicateTransactionCount { get; set; }
}

internal sealed class BankVerifiedTransaction
{
    public int Sequence { get; set; }
    public int InferredPage { get; set; }
    public int? PrintedPage { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public int SourcePage { get; set; }
    public string DateRaw { get; set; } = string.Empty;
    public DateTime? Date { get; set; }
    public string ValueDateRaw { get; set; } = string.Empty;
    public DateTime? ValueDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string DebitRaw { get; set; } = string.Empty;
    public string CreditRaw { get; set; } = string.Empty;
    public string BalanceRaw { get; set; } = string.Empty;
    public decimal OriginalDebit { get; set; }
    public decimal OriginalCredit { get; set; }
    public decimal RequiredDebit { get; set; }
    public decimal RequiredCredit { get; set; }
    public decimal FinalDebit { get; set; }
    public decimal FinalCredit { get; set; }
    public decimal? Balance { get; set; }
    public string VerificationStatus { get; set; } = string.Empty;
    public bool IsCorrectionCandidate { get; set; }
    public string CorrectionPlausibility { get; set; } = string.Empty;
    public string CorrectionReason { get; set; } = string.Empty;
    public string CorrectionProof { get; set; } = string.Empty;
}

internal sealed class BankOcrCorrection
{
    public int Sequence { get; set; }
    public int InferredPage { get; set; }
    public string Date { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public decimal OriginalDebit { get; set; }
    public decimal OriginalCredit { get; set; }
    public decimal CorrectedDebit { get; set; }
    public decimal CorrectedCredit { get; set; }
    public string Proof { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
}

internal sealed class BankVerifiedPageAudit
{
    public int InferredPage { get; set; }
    public int? PrintedPage { get; set; }
    public int? PrintedTotal { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public int SourcePage { get; set; }
    public string PageBankDetected { get; set; } = string.Empty;
    public string DocumentBank { get; set; } = string.Empty;
    public string PageAccountDetected { get; set; } = string.Empty;
    public string DocumentAccount { get; set; } = string.Empty;
    public DateTime? FirstDate { get; set; }
    public DateTime? LastDate { get; set; }
    public decimal? FirstBalance { get; set; }
    public decimal? LastBalance { get; set; }
    public string BoundaryToNext { get; set; } = string.Empty;
}
