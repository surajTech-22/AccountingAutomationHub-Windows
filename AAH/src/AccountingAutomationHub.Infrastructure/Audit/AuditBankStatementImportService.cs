using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Domain.Audit;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AccountingAutomationHub.Infrastructure.Audit;

/// <summary>
/// Fix97 local Audit statement reader. Spreadsheet/CSV sources use the common AAH
/// workbook reader; native-text PDFs are read locally with PdfPig. Scanned/image-only
/// PDFs fail closed and remain review-required instead of manufacturing transactions.
/// </summary>
public sealed partial class AuditBankStatementImportService
{
    private readonly FlexibleWorkbookReader _workbookReader = new();

    private static readonly string[] DateAliases = ["date", "txn date", "transaction date", "value date", "posting date", "tran date"];
    private static readonly string[] NarrationAliases = ["narration", "description", "particulars", "remarks", "transaction details", "details", "transaction narration"];
    private static readonly string[] ReferenceAliases = ["reference", "ref no", "ref. no", "cheque no", "chq no", "utr", "transaction id", "txn id", "instrument no"];
    private static readonly string[] DebitAliases = ["debit", "debit amount", "withdrawal", "withdrawals", "withdrawal amt", "dr amount", "paid out"];
    private static readonly string[] CreditAliases = ["credit", "credit amount", "deposit", "deposits", "deposit amt", "cr amount", "paid in"];
    private static readonly string[] BalanceAliases = ["balance", "running balance", "closing balance", "available balance"];
    private static readonly string[] AmountAliases = ["amount", "transaction amount", "txn amount"];
    private static readonly string[] DrCrAliases = ["dr/cr", "dr cr", "type", "debit/credit", "cr/dr"];

    public IReadOnlyList<AuditBankStatementImportPreview> Parse(
        string path,
        string financialYear,
        string password = "",
        bool recoveryMode = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The selected Audit statement source was not found.", path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var previews = extension == ".pdf"
            ? ParsePdf(path, cancellationToken)
            : ParseWorkbook(path, password, recoveryMode, cancellationToken);
        var (from, to) = FinancialYearRange(financialYear);
        foreach (var preview in previews)
        {
            preview.Transactions.RemoveAll(row => row.TransactionDate.Date < from.Date || row.TransactionDate.Date > to.Date);
            FinalizePreview(preview);
        }
        return previews.Where(preview => preview.Transactions.Count > 0).ToArray();
    }

    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private IReadOnlyList<AuditBankStatementImportPreview> ParseWorkbook(
        string path,
        string password,
        bool recoveryMode,
        CancellationToken cancellationToken)
    {
        var effectivePath = path;
        var ownedTemp = string.Empty;
        try
        {
            var inspection = SpreadsheetSourceInspector.Inspect(path);
            if (inspection.Kind is SpreadsheetSourceKind.LegacyOleWorkbook or SpreadsheetSourceKind.XmlSpreadsheet or SpreadsheetSourceKind.UnknownBinary)
            {
                Exception? last = null;
                var modes = recoveryMode ? new[] { 1, 2, 0 } : new[] { 0 };
                foreach (var corruptLoad in modes)
                {
                    try
                    {
                        effectivePath = ExcelComWorkbookMaterializer.ConvertToOpenXml(path, password ?? string.Empty, corruptLoad, "AuditBank");
                        ownedTemp = effectivePath;
                        last = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }
                if (last is not null) throw new InvalidDataException("Excel could not open/recover the Audit bank statement with the available password/recovery candidates.", last);
            }

            var workbook = _workbookReader.Read(effectivePath, cancellationToken);
            var result = new List<AuditBankStatementImportPreview>();
        foreach (var sheet in workbook.Worksheets.Values.Where(value => !value.State.Equals("hidden", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = DetectHeader(sheet);
            if (analysis is null) continue;
            var preview = new AuditBankStatementImportPreview
            {
                SourcePath = Path.GetFullPath(path),
                SourceFile = Path.GetFileName(path),
                SourceSheet = sheet.Name,
                DetectedAccountIdentity = DetectAccountIdentity(sheet, analysis.HeaderRow),
                ParseStatus = "Spreadsheet structure recognized",
                Notes = $"Header row {analysis.HeaderRow:N0}; local Audit parser."
            };
            DetectWorkbookBalanceMetadata(sheet, analysis.HeaderRow, preview);
            ParseWorkbookRows(sheet, analysis, preview, workbook.Uses1904DateSystem, cancellationToken);
            if (preview.Transactions.Count == 0) continue;
            FinalizePreview(preview);
            result.Add(preview);
        }
            if (result.Count == 0)
                throw new InvalidDataException("No bank-statement transaction table with a recognizable Date and Debit/Credit/Amount structure was found.");
            return result;
        }
        finally
        {
            if (ownedTemp.Length > 0 && !ownedTemp.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(ownedTemp); } catch { }
            }
        }
    }

    private static HeaderAnalysis? DetectHeader(OpenXmlWorksheetData sheet)
    {
        HeaderAnalysis? best = null;
        foreach (var rowNumber in sheet.Rows.Keys.Where(value => value <= 140).OrderBy(value => value))
        {
            var row = sheet.Rows[rowNumber];
            var mapped = new HeaderAnalysis { HeaderRow = rowNumber };
            foreach (var pair in row)
            {
                var value = NormalizeHeader(pair.Value);
                if (value.Length == 0) continue;
                if (mapped.DateColumn == 0 && Matches(value, DateAliases)) mapped.DateColumn = pair.Key;
                else if (mapped.NarrationColumn == 0 && Matches(value, NarrationAliases)) mapped.NarrationColumn = pair.Key;
                else if (mapped.ReferenceColumn == 0 && Matches(value, ReferenceAliases)) mapped.ReferenceColumn = pair.Key;
                else if (mapped.DebitColumn == 0 && Matches(value, DebitAliases)) mapped.DebitColumn = pair.Key;
                else if (mapped.CreditColumn == 0 && Matches(value, CreditAliases)) mapped.CreditColumn = pair.Key;
                else if (mapped.BalanceColumn == 0 && Matches(value, BalanceAliases)) mapped.BalanceColumn = pair.Key;
                else if (mapped.AmountColumn == 0 && Matches(value, AmountAliases)) mapped.AmountColumn = pair.Key;
                else if (mapped.DrCrColumn == 0 && Matches(value, DrCrAliases)) mapped.DrCrColumn = pair.Key;
            }
            if (mapped.DateColumn == 0) continue;
            var hasMovement = mapped.DebitColumn > 0 || mapped.CreditColumn > 0 || mapped.AmountColumn > 0;
            if (!hasMovement) continue;
            mapped.Score = 5 + (mapped.NarrationColumn > 0 ? 2 : 0) + (mapped.DebitColumn > 0 ? 2 : 0) +
                           (mapped.CreditColumn > 0 ? 2 : 0) + (mapped.BalanceColumn > 0 ? 2 : 0) +
                           (mapped.ReferenceColumn > 0 ? 1 : 0) + (mapped.DrCrColumn > 0 ? 1 : 0);
            if (best is null || mapped.Score > best.Score) best = mapped;
        }
        return best;
    }

    private static void ParseWorkbookRows(
        OpenXmlWorksheetData sheet,
        HeaderAnalysis analysis,
        AuditBankStatementImportPreview preview,
        bool uses1904DateSystem,
        CancellationToken cancellationToken)
    {
        var emptyRun = 0;
        foreach (var rowNumber in sheet.Rows.Keys.Where(value => value > analysis.HeaderRow).OrderBy(value => value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dateText = sheet.GetCell(rowNumber, analysis.DateColumn);
            if (!TryParseDate(dateText, uses1904DateSystem, out var date))
            {
                if (++emptyRun > 50 && preview.Transactions.Count > 0) break;
                continue;
            }
            emptyRun = 0;
            var debit = ParseMoney(sheet.GetCell(rowNumber, analysis.DebitColumn));
            var credit = ParseMoney(sheet.GetCell(rowNumber, analysis.CreditColumn));
            var amount = ParseMoney(sheet.GetCell(rowNumber, analysis.AmountColumn));
            if (analysis.AmountColumn > 0 && debit == 0m && credit == 0m && amount != 0m)
            {
                var side = analysis.DrCrColumn > 0 ? sheet.GetCell(rowNumber, analysis.DrCrColumn) : string.Empty;
                ApplySingleAmount(amount, side, out debit, out credit);
            }
            if (debit == 0m && credit == 0m && analysis.BalanceColumn == 0) continue;
            var narration = analysis.NarrationColumn > 0 ? sheet.GetCell(rowNumber, analysis.NarrationColumn).Trim() : string.Empty;
            var reference = analysis.ReferenceColumn > 0 ? sheet.GetCell(rowNumber, analysis.ReferenceColumn).Trim() : string.Empty;
            var balance = analysis.BalanceColumn > 0 ? ParseNullableBalance(sheet.GetCell(rowNumber, analysis.BalanceColumn)) : null;
            var row = new AuditBankStatementTransaction
            {
                TransactionDate = date,
                Reference = reference,
                Narration = narration,
                Debit = Math.Abs(debit),
                Credit = Math.Abs(credit),
                RunningBalance = balance,
                SourceRow = rowNumber,
                SourceLocation = $"{sheet.Name} · row {rowNumber:N0}"
            };
            row.OriginKey = BuildOriginKey(preview.SourceFile, sheet.Name, row);
            preview.Transactions.Add(row);
        }
    }

    private static void DetectWorkbookBalanceMetadata(OpenXmlWorksheetData sheet, int headerRow, AuditBankStatementImportPreview preview)
    {
        foreach (var rowNumber in sheet.Rows.Keys.Where(value => value <= Math.Max(headerRow + 4, 80)).OrderBy(value => value))
        {
            var cells = sheet.Rows[rowNumber].OrderBy(pair => pair.Key).ToArray();
            for (var index = 0; index < cells.Length; index++)
            {
                var label = NormalizeHeader(cells[index].Value);
                if (label.Length == 0) continue;
                if (label.Contains("opening balance", StringComparison.OrdinalIgnoreCase) || label.Equals("opening bal", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = FindNearbyMoney(cells, index);
                    if (amount.HasValue) preview.OpeningBalance = amount;
                }
                if (label.Contains("closing balance", StringComparison.OrdinalIgnoreCase) || label.Equals("closing bal", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = FindNearbyMoney(cells, index);
                    if (amount.HasValue) preview.ClosingBalance = amount;
                }
            }
        }
    }

    private static string DetectAccountIdentity(OpenXmlWorksheetData sheet, int headerRow)
    {
        var builder = new StringBuilder();
        foreach (var rowNumber in sheet.Rows.Keys.Where(value => value < headerRow && value <= 80).OrderBy(value => value))
            foreach (var cell in sheet.Rows[rowNumber].OrderBy(pair => pair.Key))
                if (!string.IsNullOrWhiteSpace(cell.Value)) builder.Append(' ').Append(cell.Value.Trim());
        var text = builder.ToString();
        var match = AccountNumberRegex().Match(text);
        if (match.Success) return Collapse(match.Groups[1].Value);
        var iban = Regex.Match(text, @"\b(?:A/?C|ACCOUNT)\s*(?:NO\.?|NUMBER|#)?\s*[:\-]?\s*([A-Z0-9\-]{5,30})", RegexOptions.IgnoreCase);
        return iban.Success ? Collapse(iban.Groups[1].Value) : string.Empty;
    }

    private static IReadOnlyList<AuditBankStatementImportPreview> ParsePdf(string path, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        var preview = new AuditBankStatementImportPreview
        {
            SourcePath = Path.GetFullPath(path),
            SourceFile = Path.GetFileName(path),
            SourceSheet = "PDF",
            ParseStatus = "Native-text PDF recognized",
            Notes = "PdfPig local embedded-text reader; no OCR or external runtime used."
        };
        var allText = new StringBuilder();
        var pageNumber = 0;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageNumber++;
            var text = ContentOrderTextExtractor.GetText(page);
            if (string.IsNullOrWhiteSpace(text)) continue;
            allText.AppendLine(text);
            ParsePdfPage(text, pageNumber, preview);
        }
        if (allText.Length == 0)
            throw new InvalidDataException("The selected PDF has no readable embedded text. Scanned/image-only statements remain Review Required; Audit does not OCR or invent values.");
        preview.DetectedAccountIdentity = DetectAccountIdentity(allText.ToString());
        DetectPdfBalanceMetadata(allText.ToString(), preview);
        if (preview.Transactions.Count == 0)
            throw new InvalidDataException("Readable PDF text was found, but Audit could not safely recognize a bank transaction table. The source remains Review Required.");
        FinalizePreview(preview);
        return [preview];
    }

    private static void ParsePdfPage(string text, int pageNumber, AuditBankStatementImportPreview preview)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        string header = string.Empty;
        var headerMode = PdfHeaderMode.Unknown;
        foreach (var raw in lines)
        {
            var line = Collapse(raw);
            if (line.Length == 0) continue;
            var normalized = NormalizeHeader(line);
            if (normalized.Contains("date", StringComparison.OrdinalIgnoreCase) &&
                (normalized.Contains("debit", StringComparison.OrdinalIgnoreCase) || normalized.Contains("withdraw", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("credit", StringComparison.OrdinalIgnoreCase) || normalized.Contains("deposit", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("amount", StringComparison.OrdinalIgnoreCase)) &&
                normalized.Contains("balance", StringComparison.OrdinalIgnoreCase))
            {
                header = normalized;
                headerMode = ResolvePdfHeaderMode(normalized);
                continue;
            }

            var dateMatch = PdfLeadingDateRegex().Match(line);
            if (!dateMatch.Success) continue;
            if (!TryParseDate(dateMatch.Groups[1].Value, false, out var date)) continue;
            var remainder = line[dateMatch.Length..].Trim();
            var values = MoneyTokenRegex().Matches(remainder).Cast<Match>().ToArray();
            if (values.Length == 0) continue;

            decimal debit = 0m, credit = 0m;
            decimal? balance = null;
            var numericStart = values[0].Index;
            var narration = Collapse(remainder[..numericStart]);
            var numbers = values.Select(match => ParseMoney(match.Value)).ToArray();
            if (headerMode is PdfHeaderMode.DebitCreditBalance or PdfHeaderMode.WithdrawDepositBalance)
            {
                if (numbers.Length >= 3)
                {
                    debit = Math.Abs(numbers[^3]);
                    credit = Math.Abs(numbers[^2]);
                    balance = ParseNullableBalance(values[^1].Value);
                }
                else if (numbers.Length == 2)
                {
                    var side = DetectSideFromLine(remainder);
                    ApplySingleAmount(numbers[0], side, out debit, out credit);
                    balance = ParseNullableBalance(values[^1].Value);
                }
            }
            else
            {
                if (numbers.Length >= 2)
                {
                    var side = DetectSideFromLine(remainder);
                    ApplySingleAmount(numbers[^2], side, out debit, out credit);
                    balance = ParseNullableBalance(values[^1].Value);
                }
                else
                {
                    var side = DetectSideFromLine(remainder);
                    ApplySingleAmount(numbers[0], side, out debit, out credit);
                }
            }
            if (debit == 0m && credit == 0m && !balance.HasValue) continue;
            var row = new AuditBankStatementTransaction
            {
                TransactionDate = date,
                Narration = narration,
                Debit = Math.Abs(debit),
                Credit = Math.Abs(credit),
                RunningBalance = balance,
                SourcePage = pageNumber,
                SourceLocation = $"PDF · page {pageNumber:N0}"
            };
            row.OriginKey = BuildOriginKey(preview.SourceFile, "PDF", row);
            preview.Transactions.Add(row);
        }
        _ = header; // retained for diagnostic readability and future per-bank column profiles.
    }

    private static void DetectPdfBalanceMetadata(string text, AuditBankStatementImportPreview preview)
    {
        preview.OpeningBalance ??= MatchBalance(text, @"opening\s+balance\s*[:\-]?\s*([\(\)\-+₹RsINR\s\d,\.]+(?:\s*(?:CR|DR))?)");
        preview.ClosingBalance ??= MatchBalance(text, @"closing\s+balance\s*[:\-]?\s*([\(\)\-+₹RsINR\s\d,\.]+(?:\s*(?:CR|DR))?)");
    }

    private static decimal? MatchBalance(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? ParseNullableBalance(match.Groups[1].Value) : null;
    }

    private static void FinalizePreview(AuditBankStatementImportPreview preview)
    {
        preview.Transactions.Sort((left, right) =>
        {
            var date = left.TransactionDate.CompareTo(right.TransactionDate);
            if (date != 0) return date;
            var row = left.SourceRow.CompareTo(right.SourceRow);
            return row != 0 ? row : left.SourcePage.CompareTo(right.SourcePage);
        });
        preview.PeriodFrom = preview.Transactions.Count > 0 ? preview.Transactions.Min(row => row.TransactionDate).Date : null;
        preview.PeriodTo = preview.Transactions.Count > 0 ? preview.Transactions.Max(row => row.TransactionDate).Date : null;
        preview.TotalDebit = Math.Round(preview.Transactions.Sum(row => row.Debit), 2, MidpointRounding.AwayFromZero);
        preview.TotalCredit = Math.Round(preview.Transactions.Sum(row => row.Credit), 2, MidpointRounding.AwayFromZero);
        if (!preview.OpeningBalance.HasValue)
        {
            decimal debit = 0m, credit = 0m;
            foreach (var row in preview.Transactions)
            {
                debit += row.Debit;
                credit += row.Credit;
                if (!row.RunningBalance.HasValue) continue;
                preview.OpeningBalance = Math.Round(row.RunningBalance.Value - credit + debit, 2, MidpointRounding.AwayFromZero);
                break;
            }
        }
        if (!preview.ClosingBalance.HasValue)
        {
            var final = preview.Transactions.LastOrDefault();
            if (final?.RunningBalance.HasValue == true) preview.ClosingBalance = final.RunningBalance;
        }
        if (preview.OpeningBalance.HasValue && preview.ClosingBalance.HasValue)
        {
            var expected = Math.Round(preview.OpeningBalance.Value + preview.TotalCredit - preview.TotalDebit, 2, MidpointRounding.AwayFromZero);
            var diff = Math.Round(preview.ClosingBalance.Value - expected, 2, MidpointRounding.AwayFromZero);
            preview.ParseStatus = Math.Abs(diff) <= 0.01m ? "Source totals verified" : $"Source balance review · difference {diff:N2}";
        }
        else if (preview.Transactions.Count > 0)
        {
            preview.ParseStatus += " · balance edge evidence incomplete";
        }
    }

    private static void ApplySingleAmount(decimal amount, string side, out decimal debit, out decimal credit)
    {
        debit = 0m;
        credit = 0m;
        var normalized = (side ?? string.Empty).Trim();
        if (normalized.Contains("CR", StringComparison.OrdinalIgnoreCase) || normalized.Contains("CREDIT", StringComparison.OrdinalIgnoreCase) || normalized.Contains("DEPOSIT", StringComparison.OrdinalIgnoreCase))
            credit = Math.Abs(amount);
        else if (normalized.Contains("DR", StringComparison.OrdinalIgnoreCase) || normalized.Contains("DEBIT", StringComparison.OrdinalIgnoreCase) || normalized.Contains("WITHDRAW", StringComparison.OrdinalIgnoreCase))
            debit = Math.Abs(amount);
        else if (amount < 0m) debit = Math.Abs(amount);
        else credit = Math.Abs(amount);
    }

    private static string DetectSideFromLine(string line)
    {
        if (Regex.IsMatch(line, @"\b(?:DR|DEBIT|WITHDRAWAL?)\b", RegexOptions.IgnoreCase)) return "DR";
        if (Regex.IsMatch(line, @"\b(?:CR|CREDIT|DEPOSIT)\b", RegexOptions.IgnoreCase)) return "CR";
        return string.Empty;
    }

    private static decimal ParseMoney(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        var text = value.Trim();
        var negative = text.StartsWith('(') && text.EndsWith(')');
        text = Regex.Replace(text, @"(?i)\b(?:INR|RS\.?)\b|₹", string.Empty).Trim();
        text = Regex.Replace(text, @"(?i)\s*(?:CR|DR)\s*$", string.Empty).Trim();
        text = text.Trim('(', ')').Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? (negative ? -Math.Abs(parsed) : parsed)
            : 0m;
    }

    private static decimal? ParseNullableBalance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var dr = Regex.IsMatch(text, @"\bDR\b", RegexOptions.IgnoreCase);
        var cr = Regex.IsMatch(text, @"\bCR\b", RegexOptions.IgnoreCase);
        var amount = ParseMoney(text);
        if (amount == 0m && !Regex.IsMatch(text, @"\d")) return null;
        if (dr) return -Math.Abs(amount);
        if (cr) return Math.Abs(amount);
        return amount;
    }

    private static bool TryParseDate(string? value, bool uses1904DateSystem, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 20_000 && serial < 90_000)
        {
            date = (uses1904DateSystem ? new DateTime(1904, 1, 1).AddDays(serial) : DateTime.FromOADate(serial)).Date;
            return true;
        }
        var formats = new[]
        {
            "dd-MM-yyyy","d-M-yyyy","dd/MM/yyyy","d/M/yyyy","dd.MM.yyyy","d.M.yyyy",
            "dd-MMM-yyyy","d-MMM-yyyy","dd MMM yyyy","d MMM yyyy","yyyy-MM-dd","yyyy/MM/dd",
            "dd-MM-yy","d-M-yy","dd/MM/yy","d/M/yy","dd MMM yy","d MMM yy"
        };
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) ||
               DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-IN"), DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static decimal? FindNearbyMoney(KeyValuePair<int, string>[] cells, int labelIndex)
    {
        foreach (var candidate in cells.Skip(labelIndex + 1).Take(4))
        {
            var value = ParseNullableBalance(candidate.Value);
            if (value.HasValue) return value;
        }
        return null;
    }

    private static string DetectAccountIdentity(string text)
    {
        var match = AccountNumberRegex().Match(text);
        return match.Success ? Collapse(match.Groups[1].Value) : string.Empty;
    }

    private static string BuildOriginKey(string file, string sheet, AuditBankStatementTransaction row)
    {
        var raw = string.Join("|", file.Trim().ToUpperInvariant(), sheet.Trim().ToUpperInvariant(), row.TransactionDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            row.Reference.Trim().ToUpperInvariant(), Collapse(row.Narration).ToUpperInvariant(), row.Debit.ToString("0.00", CultureInfo.InvariantCulture),
            row.Credit.ToString("0.00", CultureInfo.InvariantCulture), row.RunningBalance?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
            row.SourceRow.ToString(CultureInfo.InvariantCulture), row.SourcePage.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static bool Matches(string header, IEnumerable<string> aliases) =>
        aliases.Any(alias => header.Equals(alias, StringComparison.OrdinalIgnoreCase) || header.Contains(alias, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeHeader(string? value) =>
        Collapse(value ?? string.Empty).Trim(' ', ':', '-', '.', '_').ToLowerInvariant();

    private static string Collapse(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static PdfHeaderMode ResolvePdfHeaderMode(string header)
    {
        if ((header.Contains("debit", StringComparison.OrdinalIgnoreCase) && header.Contains("credit", StringComparison.OrdinalIgnoreCase)) ||
            (header.Contains("withdraw", StringComparison.OrdinalIgnoreCase) && header.Contains("deposit", StringComparison.OrdinalIgnoreCase)))
            return header.Contains("withdraw", StringComparison.OrdinalIgnoreCase) ? PdfHeaderMode.WithdrawDepositBalance : PdfHeaderMode.DebitCreditBalance;
        if (header.Contains("amount", StringComparison.OrdinalIgnoreCase)) return PdfHeaderMode.AmountSideBalance;
        return PdfHeaderMode.Unknown;
    }

    private static (DateTime From, DateTime To) FinancialYearRange(string financialYear)
    {
        var match = Regex.Match(financialYear ?? string.Empty, @"(?<year>20\d{2})\s*[-/]\s*(?<end>\d{2,4})");
        if (!match.Success || !int.TryParse(match.Groups["year"].Value, out var year))
            throw new InvalidDataException("Audit Financial Year must use a format such as 2025-26.");
        return (new DateTime(year, 4, 1), new DateTime(year + 1, 3, 31));
    }

    [GeneratedRegex(@"(?i)(?:A/?C|ACCOUNT)\s*(?:NO\.?|NUMBER|#)?\s*[:\-]?\s*([A-Z0-9][A-Z0-9\-\s]{4,29})")]
    private static partial Regex AccountNumberRegex();

    [GeneratedRegex(@"^\s*(\d{1,2}[\-/\.]\d{1,2}[\-/\.]\d{2,4}|\d{1,2}[\-/\s][A-Za-z]{3}[\-/\s]\d{2,4}|\d{4}[\-/]\d{1,2}[\-/]\d{1,2})\b")]
    private static partial Regex PdfLeadingDateRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:₹|Rs\.?|INR)?\s*\(?[-+]?\d[\d,]*(?:\.\d{1,2})?\)?(?:\s*(?:CR|DR))?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyTokenRegex();

    private sealed class HeaderAnalysis
    {
        public int HeaderRow { get; set; }
        public int DateColumn { get; set; }
        public int NarrationColumn { get; set; }
        public int ReferenceColumn { get; set; }
        public int DebitColumn { get; set; }
        public int CreditColumn { get; set; }
        public int BalanceColumn { get; set; }
        public int AmountColumn { get; set; }
        public int DrCrColumn { get; set; }
        public int Score { get; set; }
    }

    private enum PdfHeaderMode
    {
        Unknown,
        DebitCreditBalance,
        WithdrawDepositBalance,
        AmountSideBalance
    }
}
