using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private static readonly string[] DateFormats = ["dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy"];

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
    private static readonly Regex AccountRegex1 = new(@"(?:A/?C|ACCOUNT)\s*(?:NO\.?|NUMBER|#)?\s*[:\-]?\s*([0-9X*][0-9X*\-\s]{5,25})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AccountRegex2 = new(@"\bA/C\s*[:\-]?\s*([0-9X*][0-9X*\-\s]{5,25})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageWithTotalRegex = new(@"\bPAGE\s*(?:NO\.?|NUMBER)?\s*[:\-]?\s*(\d{1,4})\s*(?:OF|/)\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageOnlyRegex = new(@"\bPAGE\s*(?:NO\.?)?\s*[:\-]?\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateTextRegex = new(@"\b(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})\b", RegexOptions.Compiled);
    private static readonly Regex HtmlRowRegex = new(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlCellRegex = new(@"<(?:td|th)\b[^>]*>(.*?)</(?:td|th)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
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

        if (profiles.Count == 0)
            return BankVerifiedDocumentBatch.Failed("Raw OCR was produced, but AAH could not parse a Bank Statement page.", parseIssues);

        progress?.Invoke($"Bank OCR • AAH parsed {profiles.Count:N0} OCR page(s); grouping statement documents");
        var groups = GroupPages(profiles);
        var documents = new List<BankVerifiedDocument>();

        for (var i = 0; i < groups.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = OrderPages(groups[i]).Ordered;
            progress?.Invoke($"Bank OCR • verifying document {i + 1:N0} of {groups.Count:N0} ({ordered.Count:N0} page(s))");
            documents.Add(VerifyDocument(i + 1, ordered));
        }

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
            WasSuccessful = documents.Count > 0,
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
        document.StatementFrom,
        document.StatementTo,
        document.OpeningBalance,
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
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            PrintedPage = printedPage,
            PrintedTotal = printedTotal,
            Transactions = tx.Transactions,
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
            HasHeader = NormalizeUpper(nonTableText).Contains("STATEMENT OF ACCOUNT", StringComparison.Ordinal),
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
        var accountType = string.Empty;
        if (Regex.IsMatch(upper, @"\bCURRENT\s+ACCOUNT\b|\bPRODUCT\s*:\s*CA[-\s]", RegexOptions.IgnoreCase)) accountType = "CA";
        else if (Regex.IsMatch(upper, @"\bSAVINGS?\s+(?:BANK\s+)?ACCOUNT\b|\bPRODUCT\s*:\s*SB[-\s]", RegexOptions.IgnoreCase)) accountType = "SB";
        else if (Regex.IsMatch(upper, @"\bOVERDRAFT\b|\bOD\s+ACCOUNT\b", RegexOptions.IgnoreCase)) accountType = "OD";
        else if (Regex.IsMatch(upper, @"\bCASH\s+CREDIT\b|\bCC\s+ACCOUNT\b", RegexOptions.IgnoreCase)) accountType = "CC";

        var accountName = string.Empty;
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
            AccountType = accountType,
            AccountName = accountName
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
            new Regex(@"\bFROM\s*:?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})\s+TO\s+(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})", RegexOptions.IgnoreCase)
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

    private static CanonicalTransactionTable CanonicalTransactions(IReadOnlyList<OcrBlock> blocks)
    {
        var identified = IdentifyTransactionTable(blocks);
        if (identified is null) return new CanonicalTransactionTable();

        var transactions = new List<PageTransaction>();
        BalanceEvidence? broughtForward = null;
        BalanceEvidence? closingRow = null;

        string Get(IReadOnlyList<string> row, string key)
        {
            if (!identified.Mapping.TryGetValue(key, out var index) || index < 0 || index >= row.Count) return string.Empty;
            return Clean(row[index]);
        }

        for (var rowIndex = 1; rowIndex < identified.Rows.Count; rowIndex++)
        {
            var row = identified.Rows[rowIndex];
            var description = Get(row, "description");
            var dateRaw = Get(row, "date");
            var balanceRaw = Get(row, "balance");
            var descriptionUpper = NormalizeUpper(description);

            if (dateRaw.Length == 0 && descriptionUpper.Contains("BROUGHT FORWARD", StringComparison.Ordinal))
            {
                broughtForward = new BalanceEvidence(balanceRaw, ParseSignedMoney(balanceRaw));
                continue;
            }
            if (dateRaw.Length == 0 && (descriptionUpper.Contains("CLOSING BALANCE", StringComparison.Ordinal) || descriptionUpper.Contains("CARRIED FORWARD", StringComparison.Ordinal)))
            {
                closingRow = new BalanceEvidence(balanceRaw, ParseSignedMoney(balanceRaw));
                continue;
            }
            if (dateRaw.Length == 0 && description.Length == 0 && Get(row, "debit").Length == 0 && Get(row, "credit").Length == 0) continue;

            var debitRaw = Get(row, "debit");
            var creditRaw = Get(row, "credit");
            transactions.Add(new PageTransaction
            {
                SourceRow = rowIndex + 1,
                DateRaw = dateRaw,
                Date = ParseDate(dateRaw),
                ValueDateRaw = Get(row, "value_date"),
                ValueDate = ParseDate(Get(row, "value_date")),
                Description = description,
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
            BroughtForward = broughtForward,
            ClosingRow = closingRow
        };
    }

    private static IdentifiedTable? IdentifyTransactionTable(IReadOnlyList<OcrBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (!NormalizeUpper(block.Label).Equals("TABLE", StringComparison.Ordinal)) continue;
            var rows = TableRows(block.Content);
            if (rows.Count == 0) continue;
            var header = rows[0].Select(NormalizeUpper).ToList();
            var mapping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            FindSemantic("date", ["POST DATE", "TRANSACTION DATE", "TXN DATE", "DATE"]);
            FindSemantic("description", ["DESCRIPTION", "NARRATION", "PARTICULARS", "DETAILS"]);
            FindSemantic("debit", ["DEBIT", "WITHDRAWAL", "DR AMOUNT"]);
            FindSemantic("credit", ["CREDIT", "DEPOSIT", "CR AMOUNT"]);
            FindSemantic("balance", ["BALANCE", "RUNNING BALANCE"]);

            for (var i = 0; i < header.Count; i++)
            {
                if (header[i].Contains("VALUE DATE", StringComparison.Ordinal)) mapping["value_date"] = i;
                if (new[] { "REFERENCE", "CHEQUE", "CHQ NO", "REF NO" }.Any(word => header[i].Contains(word, StringComparison.Ordinal))) mapping["reference"] = i;
            }

            if (new[] { "date", "description", "debit", "credit", "balance" }.All(mapping.ContainsKey))
                return new IdentifiedTable(rows, mapping);

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
        return null;
    }

    private static StatementSummary? ExtractSummary(IReadOnlyList<OcrBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (!NormalizeUpper(block.Label).Equals("TABLE", StringComparison.Ordinal)) continue;
            var rows = TableRows(block.Content);
            if (rows.Count < 2) continue;
            var header = rows[0].Select(NormalizeUpper).ToList();

            int? Find(params string[] words)
            {
                for (var i = 0; i < header.Count; i++)
                    if (words.Any(word => header[i].Contains(word, StringComparison.Ordinal))) return i;
                return null;
            }

            var opening = Find("BROUGHT FORWARD", "OPENING BALANCE", "OPENING BAL");
            var drCount = Find("DR COUNT", "DEBIT COUNT");
            var crCount = Find("CR COUNT", "CREDIT COUNT");
            var totalDebits = Find("TOTAL DEBITS", "TOTAL DEBIT");
            var totalCredits = Find("TOTAL CREDITS", "TOTAL CREDIT");
            var closing = Find("CLOSING BALANCE", "CLOSING BAL");
            if (new[] { opening, drCount, crCount, totalDebits, totalCredits, closing }.Count(value => value is not null) < 4) continue;

            var row = rows[1];
            string Cell(int? index) => index is not null && index.Value < row.Count ? Clean(row[index.Value]) : string.Empty;
            return new StatementSummary
            {
                Opening = ParseSignedMoney(Cell(opening)),
                DebitCount = ParseNullableInt(Regex.Replace(Cell(drCount), @"[^0-9]", string.Empty)),
                CreditCount = ParseNullableInt(Regex.Replace(Cell(crCount), @"[^0-9]", string.Empty)),
                TotalDebits = AbsNullable(ParseMoney(Cell(totalDebits))),
                TotalCredits = AbsNullable(ParseMoney(Cell(totalCredits))),
                Closing = ParseSignedMoney(Cell(closing))
            };
        }
        return null;
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
        var (summary, summaryIssues) = UniqueSummary(ordered);
        var (transactions, duplicateCount) = CombineTransactions(ordered);
        var issues = new List<string>(summaryIssues);
        var pageIssues = new List<string>();
        var corrections = new List<BankOcrCorrection>();

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
        if (summary?.Opening is not null) { opening = summary.Opening; openingSource = "STATEMENT_SUMMARY"; }
        else if (firstBroughtForward?.Value is not null) { opening = firstBroughtForward.Value; openingSource = "FIRST_PAGE_BROUGHT_FORWARD"; }
        else if (transactions.FirstOrDefault()?.Balance is not null)
        {
            var first = transactions[0];
            opening = RoundMoney(first.Balance!.Value + first.OriginalDebit - first.OriginalCredit);
            openingSource = "DERIVED_FROM_FIRST_TRANSACTION";
        }

        if (summary?.Opening is not null && firstBroughtForward?.Value is not null && !MoneyEqual(summary.Opening, firstBroughtForward.Value))
            issues.Add("Statement summary opening does not match first-page BROUGHT FORWARD.");

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

        var rawEval = EvaluateSummary(summary, opening, transactions, useRequired: false);
        var correctedEval = EvaluateSummary(summary, opening, transactions, useRequired: true);
        var summaryProvesCandidates = summary is not null && correctedEval.Exact && !rawEval.Exact;

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
            var checks = new[]
            {
                CompareMoney("opening", summary.Opening, opening),
                CompareCount("dr_count", summary.DebitCount, debitCount),
                CompareCount("cr_count", summary.CreditCount, creditCount),
                CompareMoney("total_debits", summary.TotalDebits, totalDebits),
                CompareMoney("total_credits", summary.TotalCredits, totalCredits),
                CompareMoney("closing", summary.Closing, closing)
            };
            foreach (var check in checks.Where(check => check.Status == "MISMATCH")) issues.Add($"Statement summary {check.Name} mismatch.");
            summaryStatus = checks.All(check => check.Status is "MATCH" or "NOT_PRINTED") ? "VERIFIED" : "REVIEW_REQUIRED";
        }

        DateTime? lastDate = null;
        foreach (var row in transactions)
        {
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

        var pageStatus = "VERIFIED";
        if (pageIssues.Count > 0) pageStatus = "REVIEW_REQUIRED";
        else if (pageAudit.Any(page => page.PrintedPage is null))
            pageStatus = boundaries.Values.All(value => value == "VERIFIED") ? "INFERRED_VERIFIED" : "INFERRED";
        else if (boundaries.Values.All(value => value == "VERIFIED")) pageStatus = "VERIFIED";

        if (metadata.BankStatus == "REVIEW_REQUIRED") issues.Add("Conflicting bank evidence in grouped pages.");
        if (metadata.AccountConflict) issues.Add("Conflicting account numbers in grouped pages.");
        if (metadata.PeriodConflict) issues.Add("Conflicting statement periods in grouped pages.");
        if (metadata.IfscConflict) issues.Add("Conflicting IFSC codes in grouped pages.");
        issues.AddRange(pageIssues);
        if (duplicateCount > 0) { /* exact overlap duplicates are safely omitted and kept in the audit count */ }

        var pageVerifiedWithoutSummary = summary is null &&
            (pageStatus.Equals("VERIFIED", StringComparison.Ordinal) || pageStatus.Equals("INFERRED_VERIFIED", StringComparison.Ordinal));
        var overall = issues.Count > 0
            ? "REVIEW_REQUIRED"
            : corrections.Count > 0
                ? "VERIFIED_WITH_CORRECTIONS"
                : summaryStatus == "VERIFIED" || pageVerifiedWithoutSummary
                    ? "VERIFIED"
                    : "REVIEW_REQUIRED";

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
            StatementFrom = metadata.PeriodStart,
            StatementTo = metadata.PeriodEnd,
            OpeningBalance = opening,
            OpeningSource = openingSource,
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

    private static SummaryEvaluation EvaluateSummary(StatementSummary? summary, decimal? opening, IReadOnlyList<BankVerifiedTransaction> rows, bool useRequired)
    {
        var debits = rows.Select(row => useRequired ? row.RequiredDebit : row.OriginalDebit).ToArray();
        var credits = rows.Select(row => useRequired ? row.RequiredCredit : row.OriginalCredit).ToArray();
        var debitCount = debits.Count(value => value > 0.009m);
        var creditCount = credits.Count(value => value > 0.009m);
        var totalDebits = RoundMoney(debits.Sum());
        var totalCredits = RoundMoney(credits.Sum());
        var closing = rows.LastOrDefault(row => row.Balance is not null)?.Balance;
        if (summary is null) return new SummaryEvaluation(false);

        var exact =
            MatchOrNotPrinted(summary.Opening, opening) &&
            MatchOrNotPrinted(summary.DebitCount, debitCount) &&
            MatchOrNotPrinted(summary.CreditCount, creditCount) &&
            MatchOrNotPrinted(summary.TotalDebits, totalDebits) &&
            MatchOrNotPrinted(summary.TotalCredits, totalCredits) &&
            MatchOrNotPrinted(summary.Closing, closing);
        return new SummaryEvaluation(exact);
    }

    private static bool GroupingEdge(PageProfile left, PageProfile right)
    {
        if (IdentityConflict(left, right)) return false;
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
            foreach (Match rowMatch in HtmlRowRegex.Matches(content))
            {
                var cells = new List<string>();
                foreach (Match cellMatch in HtmlCellRegex.Matches(rowMatch.Groups[1].Value))
                {
                    var text = HtmlTagRegex.Replace(cellMatch.Groups[1].Value.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase).Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase).Replace("<br />", " ", StringComparison.OrdinalIgnoreCase), " ");
                    cells.Add(Normalize(WebUtility.HtmlDecode(text)));
                }
                if (cells.Count > 0) rows.Add(cells);
            }
            if (rows.Count > 0)
            {
                var width = rows.Max(row => row.Count);
                return rows.Select(row => PadRow(row, width)).ToList();
            }
        }

        var pipeRows = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains('|'))
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
    private static bool MoneyEqual(decimal? left, decimal? right) => left is not null && right is not null && Math.Abs(RoundMoney(left.Value - right.Value)) <= 0.01m;
    private static bool BothDifferent(decimal? left, decimal? right) => left is not null && right is not null && !MoneyEqual(left, right);
    private static bool BothDifferent(int? left, int? right) => left is not null && right is not null && left != right;
    private static bool MatchOrNotPrinted(decimal? printed, decimal? calculated) => printed is null || MoneyEqual(printed, calculated);
    private static bool MatchOrNotPrinted(int? printed, int calculated) => printed is null || printed == calculated;
    private static string MoneyText(decimal value) => Math.Abs(value).ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    private static int? ParseNullableInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static string MostCommon(IEnumerable<string> values) => values.Where(value => value.Length > 0).GroupBy(value => value, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault() ?? string.Empty;
    private static string JsonText(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString() : string.Empty;

    private static SummaryCheck CompareMoney(string name, decimal? printed, decimal? calculated) =>
        new(name, printed is null ? "NOT_PRINTED" : MoneyEqual(printed, calculated) ? "MATCH" : "MISMATCH");
    private static SummaryCheck CompareCount(string name, int? printed, int calculated) =>
        new(name, printed is null ? "NOT_PRINTED" : printed == calculated ? "MATCH" : "MISMATCH");

    private sealed class OcrBlock { public string Label { get; set; } = string.Empty; public string Content { get; set; } = string.Empty; }
    private sealed record BankNameMatch(int Start, int End, string Bank, string Alias);
    private sealed record BalanceEvidence(string Raw, decimal? Value);
    private sealed record IdentifiedTable(IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyDictionary<string, int> Mapping);
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
        public string PeriodStart { get; set; } = string.Empty;
        public string PeriodEnd { get; set; } = string.Empty;
        public int? PrintedPage { get; set; }
        public int? PrintedTotal { get; set; }
        public IReadOnlyList<PageTransaction> Transactions { get; set; } = [];
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
    public string StatementFrom { get; set; } = string.Empty;
    public string StatementTo { get; set; } = string.Empty;
    public decimal? OpeningBalance { get; set; }
    public string OpeningSource { get; set; } = string.Empty;
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
