using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Tax;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Sales;

namespace AccountingAutomationHub.Infrastructure.Tax;

public sealed class TaxCreditReconciliationService(
    SqliteTaxCreditRepository taxRepository,
    SqliteSalesRecordRepository salesRepository,
    SqliteJournalRepository journalRepository)
{
    public (IReadOnlyList<TaxCreditReconciliationRow> Rows, TaxCreditReconciliationSummary Summary) Build(
        long clientId,
        string financialYear)
    {
        var statementRows = taxRepository.GetCurrentRows(clientId, financialYear)
            .Where(row => row.TaxKind.Equals("TDS", StringComparison.OrdinalIgnoreCase) ||
                          row.TaxKind.Equals("TCS", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var bookCandidates = BuildBookCandidates(
            salesRepository.GetRecords(clientId, financialYear),
            journalRepository.GetVouchers(clientId, financialYear));
        var used = new HashSet<int>();
        var result = new List<TaxCreditReconciliationRow>(statementRows.Length);

        foreach (var statement in statementRows)
        {
            var ranked = bookCandidates
                .Select((candidate, index) => Score(statement, candidate, index))
                .Where(score => score is not null && !used.Contains(score.Index) && score.Score > 0)
                .Select(score => score!)
                .OrderByDescending(score => score.Score)
                .ThenBy(score => score.TdsDifference)
                .ThenBy(score => score.AmountDifference)
                .ThenBy(score => score.DateDifference)
                .ToArray();

            var best = ranked.FirstOrDefault();
            if (best is null)
            {
                result.Add(ToMissing(statement));
                continue;
            }

            var second = ranked.Skip(1).FirstOrDefault();
            var ambiguous = second is not null && best.Score - second.Score < 8;
            var bookingNeedsReview = !IsFinalBookingStatus(statement.BookingStatus);
            var status = ambiguous || bookingNeedsReview
                ? "Review"
                : best.Score >= 88 ? "Matched" : best.Score >= 65 ? "Probable" : "Review";
            if (!ambiguous && !bookingNeedsReview && status is "Matched" or "Probable") used.Add(best.Index);

            var reason = ambiguous
                ? $"Multiple Books candidates are similarly close. Best candidate: {best.Reason}."
                : bookingNeedsReview
                    ? $"26AS booking status '{statement.BookingStatus}' is not final/matched, so the Books match is held for review. {best.Reason}."
                    : best.Reason;

            result.Add(new TaxCreditReconciliationRow
            {
                StatementRowId = statement.Id,
                Part = statement.Part,
                Status = status,
                Reason = reason,
                Section = statement.Section,
                TaxKind = statement.TaxKind,
                DeductorName = statement.DeductorName,
                DeductorTan = statement.DeductorTan,
                BookingStatus = statement.BookingStatus,
                BookingDate = statement.BookingDate,
                StatementDate = statement.TransactionDate,
                StatementAmount = statement.AmountPaid,
                StatementTds = statement.TaxDeducted,
                StatementDeposited = statement.TaxDeposited,
                BooksSourceId = best.Candidate.SourceId,
                BooksSourceType = best.Candidate.SourceType,
                BooksReference = best.Candidate.Reference,
                BooksParty = best.Candidate.Party,
                BooksDate = best.Candidate.Date,
                BooksAmount = best.MatchedAmount,
                BooksTds = best.Candidate.TdsAmount,
                Confidence = Math.Clamp(best.Score, 0, 100)
            });
        }

        var summary = new TaxCreditReconciliationSummary
        {
            StatementRows = result.Count,
            Matched = result.Count(row => row.Status == "Matched"),
            Probable = result.Count(row => row.Status == "Probable"),
            MissingInBooks = result.Count(row => row.Status == "Missing in Books"),
            Review = result.Count(row => row.Status == "Review"),
            StatementGross = statementRows.Sum(row => row.AmountPaid),
            StatementTds = statementRows.Where(row => row.TaxKind.Equals("TDS", StringComparison.OrdinalIgnoreCase)).Sum(row => row.TaxDeducted),
            StatementTcs = statementRows.Where(row => row.TaxKind.Equals("TCS", StringComparison.OrdinalIgnoreCase)).Sum(row => row.TaxDeducted),
            StatementDeposited = statementRows.Sum(row => row.TaxDeposited),
            MatchedGross = result.Where(row => row.Status is "Matched" or "Probable").Sum(row => row.StatementAmount)
        };
        return (result, summary);
    }

    private static TaxCreditReconciliationRow ToMissing(TaxCreditStatementRow statement) => new()
    {
        StatementRowId = statement.Id,
        Part = statement.Part,
        Status = "Missing in Books",
        Reason = "No sufficiently strong Books tax-credit candidate was found. This may be non-sales income, an unrecorded TDS/TCS receivable, or a Books transaction that needs classification/review.",
        Section = statement.Section,
        TaxKind = statement.TaxKind,
        DeductorName = statement.DeductorName,
        DeductorTan = statement.DeductorTan,
        BookingStatus = statement.BookingStatus,
        BookingDate = statement.BookingDate,
        StatementDate = statement.TransactionDate,
        StatementAmount = statement.AmountPaid,
        StatementTds = statement.TaxDeducted,
        StatementDeposited = statement.TaxDeposited,
        Confidence = 0
    };

    private static IReadOnlyList<BookCandidate> BuildBookCandidates(
        IReadOnlyList<SalesImportRecord> salesRecords,
        IReadOnlyList<JournalVoucher> journals)
    {
        var candidates = new List<BookCandidate>();
        candidates.AddRange(BuildSalesCandidates(salesRecords));
        candidates.AddRange(BuildJournalCandidates(journals));
        return candidates
            .OrderBy(candidate => candidate.Date)
            .ThenBy(candidate => candidate.SourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<BookCandidate> BuildSalesCandidates(IReadOnlyList<SalesImportRecord> records)
    {
        return records
            .Where(record => record.DocumentDate.HasValue || record.GstDate.HasValue)
            .GroupBy(record => string.Join("|",
                record.DocumentNumber.Trim().ToUpperInvariant(),
                (record.GstDate ?? record.DocumentDate)?.ToString("yyyyMMdd") ?? string.Empty,
                record.CustomerGstin.Trim().ToUpperInvariant(),
                record.CustomerName.Trim().ToUpperInvariant(),
                record.DocumentType.Trim().ToUpperInvariant()))
            .Select(group =>
            {
                var first = group.OrderBy(record => record.PersistentId).First();
                var gross = group.Select(record => Math.Abs(record.DocumentValue)).Where(value => value > 0.005m).DefaultIfEmpty(0m).Max();
                if (gross <= 0.005m)
                    gross = group.Select(record => Math.Abs(record.SourceDocumentValue)).Where(value => value > 0.005m).DefaultIfEmpty(0m).Max();
                var taxable = group.Sum(record => Math.Abs(record.TaxableValue));
                if (taxable <= 0.005m) taxable = group.Sum(record => Math.Abs(record.AccountingAmount));
                var party = string.IsNullOrWhiteSpace(first.CustomerName) ? first.PartyMailingName : first.CustomerName;
                var gstin = first.CustomerGstin.Trim().ToUpperInvariant();
                return new BookCandidate(
                    "TDS",
                    first.PersistentId,
                    "Sales",
                    first.GstDate ?? first.DocumentDate,
                    first.DocumentNumber.Trim(),
                    party.Trim(),
                    ExtractPanFromGstin(gstin),
                    gross,
                    taxable,
                    0m,
                    party.Trim());
            })
            .Where(candidate => candidate.Date.HasValue && (candidate.GrossAmount > 0.005m || candidate.TaxableAmount > 0.005m))
            .ToArray();
    }

    private static IReadOnlyList<BookCandidate> BuildJournalCandidates(IReadOnlyList<JournalVoucher> journals)
    {
        return journals
            .SelectMany(voucher =>
            {
                var taxCreditLines = voucher.Lines
                    .Where(IsTaxCreditReceivableLine)
                    .GroupBy(DetectTaxKind, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Key.Length > 0)
                    .ToArray();
                if (taxCreditLines.Length == 0) return Array.Empty<BookCandidate>();

                var totalDebit = voucher.Lines.Sum(line => line.DebitAmount);
                var totalCredit = voucher.Lines.Sum(line => line.CreditAmount);
                var gross = Math.Max(totalDebit, totalCredit);
                var nonTaxLines = voucher.Lines.Where(line => !IsTaxCreditReceivableLine(line) && line.Amount > 0.005m).ToArray();
                var party = string.Join(" / ", nonTaxLines.Select(line => line.LedgerName.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
                var reference = !string.IsNullOrWhiteSpace(voucher.ReferenceNumber)
                    ? voucher.ReferenceNumber.Trim()
                    : voucher.VoucherNumber.Trim();
                var searchText = string.Join(" ", new[] { party, voucher.Narration, reference }.Where(value => !string.IsNullOrWhiteSpace(value)));

                return taxCreditLines
                    .Select(group =>
                    {
                        var tax = group.Sum(line => line.DebitAmount);
                        if (tax <= 0.005m) return null;
                        return new BookCandidate(
                            group.Key,
                            voucher.PersistentId,
                            "Journal",
                            voucher.VoucherDate,
                            reference,
                            party,
                            string.Empty,
                            gross,
                            gross,
                            tax,
                            searchText);
                    })
                    .Where(candidate => candidate is not null)
                    .Select(candidate => candidate!)
                    .ToArray();
            })
            .ToArray();
    }

    private static bool IsTaxCreditReceivableLine(JournalLine line)
    {
        if (line.DebitAmount <= 0.005m) return false;
        var text = $"{line.LedgerName} {line.TaxHead} {line.LineRole}".ToUpperInvariant();
        var hasTaxMarker = text.Contains("TDS", StringComparison.Ordinal) ||
                           text.Contains("TCS", StringComparison.Ordinal) ||
                           text.Contains("TAX DEDUCTED", StringComparison.Ordinal) ||
                           text.Contains("TAX COLLECTED", StringComparison.Ordinal);
        if (!hasTaxMarker) return false;
        if (text.Contains("PAYABLE", StringComparison.Ordinal) || text.Contains("LIABIL", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string DetectTaxKind(JournalLine line)
    {
        var text = $"{line.LedgerName} {line.TaxHead} {line.LineRole}".ToUpperInvariant();
        return text.Contains("TCS", StringComparison.Ordinal) || text.Contains("TAX COLLECTED", StringComparison.Ordinal)
            ? "TCS"
            : text.Contains("TDS", StringComparison.Ordinal) || text.Contains("TAX DEDUCTED", StringComparison.Ordinal)
                ? "TDS"
                : string.Empty;
    }

    private static MatchScore? Score(TaxCreditStatementRow statement, BookCandidate candidate, int index)
    {
        if (!statement.TaxKind.Equals(candidate.TaxKind, StringComparison.OrdinalIgnoreCase)) return null;
        if (!statement.TransactionDate.HasValue || !candidate.Date.HasValue) return null;
        var dateDifference = Math.Abs((statement.TransactionDate.Value.Date - candidate.Date.Value.Date).Days);
        if (dateDifference > 45) return null;

        var grossDiff = Math.Abs(statement.AmountPaid - candidate.GrossAmount);
        var taxableDiff = Math.Abs(statement.AmountPaid - candidate.TaxableAmount);
        var useTaxable = taxableDiff < grossDiff;
        var amountDifference = useTaxable ? taxableDiff : grossDiff;
        var matchedAmount = useTaxable ? candidate.TaxableAmount : candidate.GrossAmount;
        var tdsDifference = candidate.TdsAmount > 0.005m
            ? Math.Abs(statement.TaxDeducted - candidate.TdsAmount)
            : decimal.MaxValue;

        var amountWithinTolerance = amountDifference <= Math.Max(5m, Math.Abs(statement.AmountPaid) * 0.01m);
        var tdsWithinTolerance = candidate.TdsAmount > 0.005m && tdsDifference <= Math.Max(1m, Math.Abs(statement.TaxDeducted) * 0.01m);
        if (!amountWithinTolerance && !tdsWithinTolerance) return null;

        var nameScore = NameSimilarity(statement.DeductorName, candidate.SearchText);
        var panCompatible = statement.DeductorPan.Length > 0 && candidate.Pan.Length > 0 &&
                            statement.DeductorPan.Equals(candidate.Pan, StringComparison.OrdinalIgnoreCase);

        var score = 0;
        if (amountWithinTolerance)
        {
            if (amountDifference <= 0.50m) score += candidate.SourceType == "Journal" ? 30 : 50;
            else if (amountDifference <= 1m) score += candidate.SourceType == "Journal" ? 26 : 44;
            else if (amountDifference <= 2m) score += candidate.SourceType == "Journal" ? 20 : 34;
            else score += candidate.SourceType == "Journal" ? 12 : 20;
        }

        if (tdsWithinTolerance)
        {
            if (tdsDifference <= 0.50m) score += 48;
            else if (tdsDifference <= 1m) score += 42;
            else score += 30;
        }

        if (dateDifference == 0) score += 28;
        else if (dateDifference <= 3) score += 21;
        else if (dateDifference <= 7) score += 14;
        else if (dateDifference <= 31) score += 6;

        if (panCompatible) score += 22;
        else if (nameScore >= 0.80) score += 20;
        else if (nameScore >= 0.55) score += 13;
        else if (nameScore >= 0.30) score += 6;

        if (!panCompatible && nameScore < 0.20 && dateDifference > 7 && !tdsWithinTolerance) score -= 20;

        var amountText = amountWithinTolerance
            ? $"{(useTaxable ? "Taxable value" : "Books amount")} differs by {amountDifference:N2}"
            : $"Books gross differs by {amountDifference:N2}";
        var tdsText = candidate.TdsAmount > 0.005m
            ? $"TDS differs by {tdsDifference:N2}"
            : "Books source has no separate TDS amount";
        var reason = $"{candidate.SourceType}: {amountText}; {tdsText}; date differs by {dateDifference} day(s); deductor/Books text similarity {nameScore:P0}.";
        return score <= 0 ? null : new MatchScore(index, candidate, score, amountDifference, tdsDifference, dateDifference, matchedAmount, reason);
    }

    private static bool IsFinalBookingStatus(string value)
    {
        var status = (value ?? string.Empty).Trim().ToUpperInvariant();
        return status is "F" or "M";
    }

    private static double NameSimilarity(string left, string right)
    {
        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersect = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersect / union;
    }

    private static HashSet<string> Tokens(string value)
    {
        var ignored = new HashSet<string>(new[] { "PVT", "PRIVATE", "LTD", "LIMITED", "LLP", "THE", "AND", "CO", "COMPANY" }, StringComparer.OrdinalIgnoreCase);
        var cleaned = new string((value ?? string.Empty).ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray());
        return cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 && !ignored.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractPanFromGstin(string gstin)
    {
        if (gstin.Length == 15 && gstin.Take(2).All(char.IsDigit))
        {
            var pan = gstin.Substring(2, 10);
            if (pan.Length == 10 && pan.Take(5).All(char.IsLetter) && pan.Skip(5).Take(4).All(char.IsDigit) && char.IsLetter(pan[9]))
                return pan.ToUpperInvariant();
        }
        return string.Empty;
    }

    private sealed record BookCandidate(
        string TaxKind,
        long SourceId,
        string SourceType,
        DateTime? Date,
        string Reference,
        string Party,
        string Pan,
        decimal GrossAmount,
        decimal TaxableAmount,
        decimal TdsAmount,
        string SearchText);

    private sealed record MatchScore(
        int Index,
        BookCandidate Candidate,
        int Score,
        decimal AmountDifference,
        decimal TdsDifference,
        int DateDifference,
        decimal MatchedAmount,
        string Reason);
}
