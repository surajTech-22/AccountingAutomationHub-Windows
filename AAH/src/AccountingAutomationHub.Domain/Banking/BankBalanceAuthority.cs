namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Single Bank Statement balance/coverage authority used by import, reconciliation,
/// dashboard, Banking UI and reports.  All balance values use the AAH business-books
/// convention: positive = Dr, negative = Cr.
/// </summary>
public static class BankBalanceAuthority
{
    public static decimal CalculateClosingBalance(decimal openingBalance, decimal statementDebits, decimal statementCredits) =>
        BankBusinessBalance.CalculateClosing(openingBalance, statementDebits, statementCredits);

    public static decimal CalculateOpeningBalance(decimal closingBalance, decimal statementDebits, decimal statementCredits) =>
        Math.Round(closingBalance - statementCredits + statementDebits, 2, MidpointRounding.AwayFromZero);

    public static BankResolvedBalance ResolveOpeningBalance(
        BankStatementRecord statement,
        IReadOnlyList<BankTransactionRecord> transactions,
        decimal? previousContiguousClosing = null,
        IReadOnlyList<BankTransactionRecord>? movementTransactions = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(transactions);

        if (statement.OpeningBalanceSource.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) && statement.OpeningBalance.HasValue)
            return new(statement.OpeningBalance, "MANUAL");

        // OpeningBalanceSource existed before BR6, so it is the only safe legacy provenance
        // marker we can reuse.  A SOURCE marker proves the old importer detected statement
        // metadata; a RUNNING BALANCE marker proves the old importer derived the stored OB
        // from source running-balance evidence before cache-generated balances existed.
        if (statement.OpeningBalanceSource.Equals("SOURCE", StringComparison.OrdinalIgnoreCase))
        {
            var sourceOpening = statement.SourceOpeningBalance ?? statement.OpeningBalance;
            if (sourceOpening.HasValue) return new(sourceOpening, "SOURCE");
        }
        if (statement.OpeningBalanceSource.Equals("RUNNING BALANCE", StringComparison.OrdinalIgnoreCase) && statement.OpeningBalance.HasValue)
            return new(statement.OpeningBalance, "RUNNING BALANCE");

        var runningOpening = DeriveOpeningFromRunningBalance(transactions);
        if (runningOpening.HasValue) return new(runningOpening, "RUNNING BALANCE");

        if (previousContiguousClosing.HasValue) return new(previousContiguousClosing, "CHAIN");

        var closingEvidence = ResolveIndependentClosingEvidence(statement, transactions);
        if (closingEvidence.HasValue)
        {
            var calculationRows = movementTransactions ?? transactions;
            return new(CalculateOpeningBalance(closingEvidence.Value, calculationRows.Sum(x => x.Debit), calculationRows.Sum(x => x.Credit)), "CLOSING BACK-CALC");
        }

        // Keep a historical stored value visible for report parity, but never silently
        // certify it.  Exact verification still rejects this source until the original
        // statement is reprocessed or the user explicitly verifies the balance.
        return statement.OpeningBalance.HasValue
            ? new(statement.OpeningBalance, "LEGACY UNVERIFIED")
            : new(null, "MISSING EVIDENCE");
    }

    public static BankResolvedClosing ResolveClosingBalance(
        BankStatementRecord statement,
        IReadOnlyList<BankTransactionRecord> transactions,
        decimal? openingBalance,
        IReadOnlyList<BankTransactionRecord>? movementTransactions = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(transactions);

        var independent = ResolveIndependentClosingEvidence(statement, transactions);
        var independentSource = statement.ClosingBalanceSource.Equals("SOURCE", StringComparison.OrdinalIgnoreCase) && statement.SourceClosingBalance.HasValue
            ? "SOURCE"
            : statement.ClosingBalanceSource.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) && statement.ClosingBalance.HasValue
                ? "MANUAL"
                : independent.HasValue ? "RUNNING BALANCE" : string.Empty;
        var calculationRows = movementTransactions ?? transactions;
        var calculated = openingBalance.HasValue
            ? CalculateClosingBalance(openingBalance.Value, calculationRows.Sum(x => x.Debit), calculationRows.Sum(x => x.Credit))
            : (decimal?)null;
        // A legacy stored ClosingBalance without source/running evidence is display-only.
        // Prefer a calculated CB when OB is available so every report uses the same math,
        // but retain the old number as LEGACY UNVERIFIED when calculation is impossible.
        var legacy = statement.ClosingBalance;
        var resolved = independent ?? calculated ?? legacy;
        var source = independent.HasValue
            ? independentSource
            : calculated.HasValue
                ? "CALCULATED"
                : legacy.HasValue ? "LEGACY UNVERIFIED" : "MISSING EVIDENCE";
        return new(resolved, calculated, independent, source);
    }

    public static IReadOnlyList<BankTransactionRecord> OrderTransactionsChronologically(
        IReadOnlyList<BankTransactionRecord> transactions)
    {
        if (transactions.Count <= 1) return transactions.ToArray();

        // Several bank exports (including PNB One) write the newest transaction first.
        // SourceRow therefore cannot be treated as chronological order. Infer the physical
        // direction from dated rows, then keep same-day rows in the matching source direction.
        var bySource = transactions
            .OrderBy(x => x.SourceRow)
            .ThenBy(x => x.Id)
            .ToArray();
        var descendingSource = bySource[0].TransactionDate.Date > bySource[^1].TransactionDate.Date;

        return descendingSource
            ? transactions.OrderBy(x => x.TransactionDate.Date).ThenByDescending(x => x.SourceRow).ThenBy(x => x.Id).ToArray()
            : transactions.OrderBy(x => x.TransactionDate.Date).ThenBy(x => x.SourceRow).ThenBy(x => x.Id).ToArray();
    }

    public static decimal? DeriveOpeningFromRunningBalance(IReadOnlyList<BankTransactionRecord> transactions)
    {
        decimal debit = 0m;
        decimal credit = 0m;
        foreach (var row in OrderTransactionsChronologically(transactions))
        {
            debit += row.Debit;
            credit += row.Credit;
            if (IsIndependentRunningBalanceEvidence(row))
                return Math.Round(row.RunningBalance!.Value - credit + debit, 2, MidpointRounding.AwayFromZero);
        }
        return null;
    }

    public static decimal? ResolveIndependentClosingEvidence(BankStatementRecord statement, IReadOnlyList<BankTransactionRecord> transactions)
    {
        if (statement.ClosingBalanceSource.Equals("SOURCE", StringComparison.OrdinalIgnoreCase) && statement.SourceClosingBalance.HasValue)
            return statement.SourceClosingBalance;
        if (statement.ClosingBalanceSource.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) && statement.ClosingBalance.HasValue)
            return statement.ClosingBalance;

        // A running balance is a Closing Balance only when it belongs to the final
        // chronological imported transaction. An earlier running-balance row cannot be
        // promoted to statement CB when later transactions have no balance evidence.
        var ordered = OrderTransactionsChronologically(transactions);
        var finalRow = ordered.LastOrDefault();
        return finalRow is not null && IsIndependentRunningBalanceEvidence(finalRow)
            ? finalRow.RunningBalance
            : null;
    }

    public static BankAccountBalanceSnapshot BuildAccountSnapshot(
        long bankAccountId,
        IEnumerable<BankStatementRecord> statements,
        IEnumerable<BankTransactionRecord> transactions,
        DateTime? coverageFrom = null,
        DateTime? coverageTo = null)
    {
        // CR4 transaction-date authority: materialize the Bank Account transaction stream
        // first, then order statement evidence by the actual first/last imported Bank
        // Transaction Date. Statement PeriodFrom/PeriodTo is retained only as audit fallback
        // when the original source rows are unavailable.
        var rows = transactions
            .Where(t => t.BankAccountId == bankAccountId)
            .Where(IsSourceStatementTransaction)
            .ToArray();
        var accountStatements = statements
            .Where(s => s.BankAccountId == bankAccountId)
            .OrderBy(s => ResolveStatementTransactionDateRange(s, rows)?.From ?? s.PeriodFrom ?? s.ImportedUtc)
            .ThenBy(s => ResolveStatementTransactionDateRange(s, rows)?.To ?? s.PeriodTo ?? s.ImportedUtc)
            .ThenBy(s => s.Id)
            .ToArray();
        var chainStatements = accountStatements
            .Where(s => !IsBalanceChainExcludedStatement(s))
            .ToArray();
        if (chainStatements.Length == 0) chainStatements = accountStatements;

        if (accountStatements.Length == 0)
        {
            var missingOnly = FindBalanceMissingPeriods(Array.Empty<BankStatementRecord>(), rows, coverageFrom, coverageTo);
            return new BankAccountBalanceSnapshot
            {
                BankAccountId = bankAccountId,
                MissingPeriods = missingOnly,
                Status = missingOnly.Count > 0 ? "Missing Period" : "No Statement"
            };
        }

        decimal? opening = null;
        decimal? closing = null;
        var openingSource = string.Empty;
        var closingSource = string.Empty;
        BankStatementRecord? previousStatement = null;
        decimal? previousClosing = null;
        for (var index = 0; index < chainStatements.Length; index++)
        {
            var statement = chainStatements[index];
            var statementRows = GetStatementSourceTransactions(statement, rows);
            var statementMovementRows = GetStatementPeriodTransactions(statement, rows);
            var chainClosing = previousStatement is not null && CanCarryBalanceForward(previousStatement, statement, rows)
                ? previousClosing
                : null;
            var resolvedOpening = ResolveOpeningBalance(statement, statementRows, chainClosing, statementMovementRows);
            var resolvedClosing = ResolveClosingBalance(statement, statementRows, resolvedOpening.Value, statementMovementRows);

            // Account boundary balances must come from the actual FY/account chain edges.
            // Never substitute a later statement's OB for a missing first OB, or an earlier
            // statement's CB for a missing final CB, because that makes incomplete coverage
            // appear complete and makes totals disagree across reports.
            if (index == 0)
            {
                opening = resolvedOpening.Value;
                openingSource = resolvedOpening.Source;
            }
            if (index == chainStatements.Length - 1)
            {
                closing = resolvedClosing.Value;
                closingSource = resolvedClosing.Source;
            }

            previousStatement = statement;
            previousClosing = resolvedClosing.Value;
        }

        // Fix23: account-wide movement must be a single balance-stitched source stream.
        // Overlapping statements are snapshots of the same physical account period; summing
        // every imported row double-counts the overlap, while deleting only exact matches can
        // leave a partially-deduplicated stream. Stitch at the next source's independently
        // proved opening-balance boundary and keep the later source from that point forward.
        var movementStream = BuildAccountMovementStream(chainStatements, rows);
        var movementRows = movementStream.Rows;
        var totalDebit = movementRows.Sum(x => x.Debit);
        var totalCredit = movementRows.Sum(x => x.Credit);
        var calculatedClosing = opening.HasValue
            ? CalculateClosingBalance(opening.Value, totalDebit, totalCredit)
            : (decimal?)null;
        var hasIndependentAccountClosing = !closingSource.Equals("CALCULATED", StringComparison.OrdinalIgnoreCase) &&
                                           !closingSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase) &&
                                           !closingSource.Equals("MISSING EVIDENCE", StringComparison.OrdinalIgnoreCase);
        var difference = closing.HasValue && calculatedClosing.HasValue && hasIndependentAccountClosing
            ? Math.Round(closing.Value - calculatedClosing.Value, 2, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        var missing = FindBalanceMissingPeriods(accountStatements, rows, coverageFrom, coverageTo);
        var overlaps = FindOverlaps(accountStatements, rows);

        var statusParts = new List<string>();
        var hasUnverifiedBoundary = !opening.HasValue || !closing.HasValue ||
                                    openingSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
                                    closingSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
                                    closingSource.Equals("CALCULATED", StringComparison.OrdinalIgnoreCase);
        if (hasUnverifiedBoundary) statusParts.Add("Missing Balance Evidence");
        if (missing.Count > 0) statusParts.Add("Missing Period");
        if (movementStream.UnresolvedOverlap && overlaps.Count > 0) statusParts.Add("Statement Overlap");
        if (difference.HasValue && difference.Value != 0m) statusParts.Add("Balance Difference");
        if (closingSource.Equals("CALCULATED", StringComparison.OrdinalIgnoreCase)) statusParts.Add("Calculated Closing");
        if (openingSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
            closingSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase)) statusParts.Add("Legacy Balance Review");

        return new BankAccountBalanceSnapshot
        {
            BankAccountId = bankAccountId,
            OpeningBalance = opening,
            OpeningBalanceSource = openingSource,
            ClosingBalance = closing,
            ClosingBalanceSource = closingSource,
            CalculatedClosingBalance = calculatedClosing,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit,
            Difference = difference,
            MissingPeriods = missing,
            OverlapPeriods = overlaps,
            Status = statusParts.Count == 0 ? "Reconciled" : string.Join(" / ", statusParts)
        };
    }


    private static (IReadOnlyList<BankTransactionRecord> Rows, bool UnresolvedOverlap) BuildAccountMovementStream(
        IEnumerable<BankStatementRecord> statements,
        IReadOnlyList<BankTransactionRecord> transactions)
    {
        var statementList = statements
            .Where(statement => !IsBalanceChainExcludedStatement(statement))
            .Where(statement => !IsDuplicateAuditStatement(statement))
            .ToArray();
        if (statementList.Length == 0)
            return (OrderTransactionsChronologically(transactions), false);

        var segments = new List<(BankDateRange Range, string Key, IReadOnlyList<BankTransactionRecord> Rows)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in statementList)
        {
            var sourceRows = GetStatementSourceTransactions(statement, transactions);
            if (sourceRows.Count == 0) continue;
            var range = ResolveStatementTransactionDateRange(statement, transactions);
            if (!range.HasValue) continue;

            var sourceKey = !string.IsNullOrWhiteSpace(statement.SourceFile)
                ? SourceFileKey(statement.BankAccountId, statement.SourceFile, statement.SourceSheet)
                : $"STATEMENT|{statement.BankAccountId}|{statement.Id}";
            if (!seen.Add(sourceKey)) continue;

            segments.Add((range.Value, sourceKey, OrderTransactionsChronologically(sourceRows)));
        }

        if (segments.Count == 0)
            return (OrderTransactionsChronologically(transactions), false);

        var orderedSegments = segments
            .OrderBy(segment => segment.Range.From)
            .ThenBy(segment => segment.Range.To)
            .ThenBy(segment => segment.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new List<BankTransactionRecord>();
        var unresolvedOverlap = false;
        foreach (var segment in orderedSegments)
        {
            var nextRows = segment.Rows;
            if (nextRows.Count == 0) continue;
            if (result.Count == 0)
            {
                result.AddRange(nextRows);
                continue;
            }

            var currentTo = result.Max(row => row.TransactionDate.Date);
            if (segment.Range.From > currentTo)
            {
                result.AddRange(nextRows);
                continue;
            }

            // Preferred stitch: the later overlapping source's first independent running
            // balance proves its opening. Find that exact balance in the earlier source on the
            // overlap boundary, keep rows through that balance, and let the later source own
            // all movement after it. This survives parser wording differences and also repairs
            // databases where Fix22 already removed only some exact duplicate copies.
            var nextOpening = DeriveOpeningFromRunningBalance(nextRows);
            var firstDate = nextRows[0].TransactionDate.Date;
            var balanceAnchorIndex = -1;
            if (nextOpening.HasValue)
            {
                var target = decimal.Round(nextOpening.Value, 2, MidpointRounding.AwayFromZero);
                for (var index = 0; index < result.Count; index++)
                {
                    var row = result[index];
                    if (row.TransactionDate.Date != firstDate) continue;
                    if (!IsIndependentRunningBalanceEvidence(row)) continue;
                    if (decimal.Round(row.RunningBalance!.Value, 2, MidpointRounding.AwayFromZero) != target) continue;
                    balanceAnchorIndex = index;
                }
            }

            if (balanceAnchorIndex >= 0)
            {
                if (balanceAnchorIndex + 1 < result.Count)
                    result.RemoveRange(balanceAnchorIndex + 1, result.Count - (balanceAnchorIndex + 1));
                result.AddRange(nextRows);
                continue;
            }

            // Secondary stitch: if the source opening boundary is not present, replace at the
            // earliest exact common transaction. This is non-destructive; it changes only the
            // arithmetic stream, never the imported audit rows.
            var resultAnchors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < result.Count; index++)
                resultAnchors[MovementStitchIdentity(result[index])] = index;

            var nextAnchorIndex = -1;
            var priorAnchorIndex = -1;
            for (var index = 0; index < nextRows.Count; index++)
            {
                if (!resultAnchors.TryGetValue(MovementStitchIdentity(nextRows[index]), out var priorIndex)) continue;
                nextAnchorIndex = index;
                priorAnchorIndex = priorIndex;
                break;
            }

            if (nextAnchorIndex >= 0)
            {
                if (priorAnchorIndex < result.Count)
                    result.RemoveRange(priorAnchorIndex, result.Count - priorAnchorIndex);
                for (var index = nextAnchorIndex; index < nextRows.Count; index++)
                    result.Add(nextRows[index]);
                continue;
            }

            // No safe balance/transaction anchor: preserve all evidence and keep the overlap
            // in review instead of guessing or deleting transactions.
            unresolvedOverlap = true;
            result.AddRange(nextRows);
        }

        return (result, unresolvedOverlap);
    }

    private static string MovementStitchIdentity(BankTransactionRecord row)
    {
        var narration = string.Join(" ", (row.NormalizedNarration ?? row.Narration ?? string.Empty)
            .Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var reference = (row.ReferenceNumber ?? string.Empty).Trim().ToUpperInvariant();
        var balance = IsIndependentRunningBalanceEvidence(row)
            ? decimal.Round(row.RunningBalance!.Value, 2, MidpointRounding.AwayFromZero).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        return FormattableString.Invariant(
            $"{row.TransactionDate:yyyyMMdd}|{row.Debit:0.00}|{row.Credit:0.00}|{balance}|{reference}|{narration}");
    }

    public static IReadOnlyList<BankDateRange> FindMissingPeriods(
        IEnumerable<BankStatementRecord> statements,
        DateTime? coverageFrom = null,
        DateTime? coverageTo = null)
    {
        var ranges = statements
            .Where(s => s.PeriodFrom.HasValue && s.PeriodTo.HasValue)
            .Select(s => new BankDateRange(s.PeriodFrom!.Value.Date, s.PeriodTo!.Value.Date));
        return FindMissingPeriodsFromRanges(ranges, coverageFrom, coverageTo);
    }

    public static IReadOnlyList<BankDateRange> FindMissingPeriods(
        IEnumerable<BankStatementRecord> statements,
        IEnumerable<BankTransactionRecord> transactions,
        DateTime? coverageFrom = null,
        DateTime? coverageTo = null)
    {
        var sourceRows = transactions
            .Where(IsSourceStatementTransaction)
            .ToArray();
        var ranges = new List<BankDateRange>();

        // CR4: coverage dates come from the original imported Bank Transaction Date.
        // Statement header/file periods are used only as a fallback when source rows are
        // unavailable, and never expand real transaction dates to month boundaries.
        foreach (var statement in statements)
        {
            var range = ResolveStatementTransactionDateRange(statement, sourceRows);
            if (range.HasValue) ranges.Add(range.Value);
        }

        // Retain source rows whose historical statement registry is missing/mis-linked.
        foreach (var group in sourceRows
                     .Where(row => !string.IsNullOrWhiteSpace(row.SourceFile))
                     .GroupBy(
                         row => SourceFileKey(row.BankAccountId, row.SourceFile, row.SourceSheet),
                         StringComparer.OrdinalIgnoreCase))
        {
            var inferred = InferTransactionFileCoverage(group.ToArray());
            if (inferred.HasValue) ranges.Add(inferred.Value);
        }

        return FindMissingPeriodsFromRanges(ranges, coverageFrom, coverageTo);
    }

    public static IReadOnlyList<BankTransactionRecord> GetStatementSourceTransactions(
        BankStatementRecord statement,
        IEnumerable<BankTransactionRecord> transactions)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(transactions);

        var sourceFile = (statement.SourceFile ?? string.Empty).Trim();
        var sourceSheet = (statement.SourceSheet ?? string.Empty).Trim();
        var accountRows = transactions
            .Where(row => row.BankAccountId == statement.BankAccountId)
            .Where(IsSourceStatementTransaction)
            .ToArray();

        // BR10: physical source identity is stronger than the historical StatementId.
        // Older imports could retain the correct Source File/Source Sheet while StatementId
        // still pointed at a neighbouring registry row. Use the physical file first so
        // Statement Metadata and source-evidence repair use the actual imported file.
        // Source rows remain the statement evidence/provenance authority. Whole-account
        // debit/credit arithmetic is stitched separately in Fix23 so overlap is counted once.
        // Registry self-repair later relinks historical StatementIds.
        if (sourceFile.Length > 0)
        {
            var physicalRows = accountRows
                .Where(row => string.Equals((row.SourceFile ?? string.Empty).Trim(), sourceFile, StringComparison.OrdinalIgnoreCase))
                .Where(row => sourceSheet.Length == 0 ||
                              string.IsNullOrWhiteSpace(row.SourceSheet) ||
                              string.Equals((row.SourceSheet ?? string.Empty).Trim(), sourceSheet, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (physicalRows.Length > 0)
                return OrderTransactionsChronologically(physicalRows);
        }

        return OrderTransactionsChronologically(accountRows
            .Where(row => row.StatementId == statement.Id)
            .ToArray());
    }

    public static BankDateRange? ResolveStatementTransactionDateRange(
        BankStatementRecord statement,
        IEnumerable<BankTransactionRecord> transactions)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(transactions);

        // The original imported Bank Transaction Date is the chronology authority.
        // SourceFile/SourceSheet is used only to locate provenance rows belonging to this
        // statement; the file/header period never overrides dates that exist on transactions.
        var sourceRows = GetStatementSourceTransactions(statement, transactions);
        var dates = sourceRows
            .Select(row => row.TransactionDate.Date)
            .Where(date => date != DateTime.MinValue.Date)
            .OrderBy(date => date)
            .ToArray();
        if (dates.Length > 0)
            return new BankDateRange(dates[0], dates[^1]);

        if (statement.PeriodFrom.HasValue && statement.PeriodTo.HasValue)
        {
            var from = statement.PeriodFrom.Value.Date;
            var to = statement.PeriodTo.Value.Date;
            if (to < from) (from, to) = (to, from);
            return new BankDateRange(from, to);
        }
        return null;
    }

    public static IReadOnlyList<BankTransactionRecord> GetStatementPeriodTransactions(
        BankStatementRecord statement,
        IEnumerable<BankTransactionRecord> transactions)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(transactions);

        var materialized = transactions as IReadOnlyList<BankTransactionRecord> ?? transactions.ToArray();
        var range = ResolveStatementTransactionDateRange(statement, materialized);
        if (!range.HasValue)
            return GetStatementSourceTransactions(statement, materialized);

        // CR3 reconciliation movement authority retained. CR4 reconciliation movement authority: Bank Account + ORIGINAL transaction date.
        // Source file, imported time, statement header period and database insertion Id do not
        // decide which dates belong to the balance movement. Within a date, ValueDate and the
        // source row are stable audit tie-breakers; arithmetic totals are date-set invariant.
        return materialized
            .Where(row => row.BankAccountId == statement.BankAccountId)
            .Where(IsSourceStatementTransaction)
            .Where(row => row.TransactionDate.Date >= range.Value.From && row.TransactionDate.Date <= range.Value.To)
            .OrderBy(row => row.TransactionDate.Date)
            .ThenBy(row => row.ValueDate?.Date ?? row.TransactionDate.Date)
            .ThenBy(row => row.SourceRow)
            .ThenBy(row => row.ReferenceNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id)
            .ToArray();
    }

    public static BankDateRange? InferTransactionFileCoverage(IReadOnlyCollection<BankTransactionRecord> transactions)
    {
        if (transactions.Count == 0) return null;
        var dates = transactions
            .Where(IsSourceStatementTransaction)
            .Select(row => row.TransactionDate.Date)
            .Where(date => date != DateTime.MinValue.Date)
            .OrderBy(date => date)
            .ToArray();
        if (dates.Length == 0) return null;

        var from = dates[0];
        var to = dates[^1];

        // CR4: never expand a source to calendar-month boundaries. Coverage dates are
        // exactly the minimum/maximum original Bank Transaction Date observed in the source.

        return new BankDateRange(from, to);
    }

    public static string SourceFileKey(long bankAccountId, string? sourceFile, string? sourceSheet) =>
        $"{bankAccountId}|{(sourceFile ?? string.Empty).Trim()}|{(sourceSheet ?? string.Empty).Trim()}";

    private static IReadOnlyList<BankDateRange> FindMissingPeriodsFromRanges(
        IEnumerable<BankDateRange> sourceRanges,
        DateTime? coverageFrom,
        DateTime? coverageTo)
    {
        var requestedFrom = coverageFrom?.Date;
        var requestedTo = coverageTo?.Date;
        var ranges = sourceRanges
            .Select(source =>
            {
                var from = source.From.Date;
                var to = source.To.Date;
                if (requestedFrom.HasValue && from < requestedFrom.Value) from = requestedFrom.Value;
                if (requestedTo.HasValue && to > requestedTo.Value) to = requestedTo.Value;
                return new BankDateRange(from, to);
            })
            .Where(r => r.To >= r.From)
            .OrderBy(r => r.From)
            .ThenBy(r => r.To)
            .ToArray();
        if (ranges.Length == 0)
        {
            if (coverageFrom.HasValue && coverageTo.HasValue && coverageFrom.Value.Date <= coverageTo.Value.Date)
                return new[] { new BankDateRange(coverageFrom.Value.Date, coverageTo.Value.Date) };
            return Array.Empty<BankDateRange>();
        }

        var merged = new List<BankDateRange>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.From > merged[^1].To.AddDays(1))
            {
                merged.Add(range);
                continue;
            }
            if (range.To > merged[^1].To)
                merged[^1] = new BankDateRange(merged[^1].From, range.To);
        }

        var result = new List<BankDateRange>();
        var start = requestedFrom;
        var end = requestedTo;
        if (start.HasValue && merged[0].From > start.Value)
            result.Add(new BankDateRange(start.Value, merged[0].From.AddDays(-1)));

        for (var i = 1; i < merged.Count; i++)
        {
            var from = merged[i - 1].To.AddDays(1);
            var to = merged[i].From.AddDays(-1);
            if (from <= to) result.Add(new BankDateRange(from, to));
        }

        if (end.HasValue && merged[^1].To < end.Value)
            result.Add(new BankDateRange(merged[^1].To.AddDays(1), end.Value));

        return result;
    }


    /// <summary>
    /// Missing Statement Period authority. A period is complete only when its dates are
    /// represented by statement/source evidence AND its closing-balance arithmetic/chain
    /// verifies. Merely having imported transactions on a date does not certify coverage.
    /// </summary>
    public static IReadOnlyList<BankDateRange> FindBalanceMissingPeriods(
        IEnumerable<BankStatementRecord> statements,
        IEnumerable<BankTransactionRecord> transactions,
        DateTime? coverageFrom = null,
        DateTime? coverageTo = null)
    {
        var rows = transactions
            .Where(IsSourceStatementTransaction)
            .ToArray();
        var statementList = statements
            .Where(statement => !IsDuplicateAuditStatement(statement))
            .Where(statement => !IsBalanceChainExcludedStatement(statement))
            .OrderBy(statement => ResolveStatementTransactionDateRange(statement, rows)?.From ?? statement.PeriodFrom ?? statement.ImportedUtc)
            .ThenBy(statement => ResolveStatementTransactionDateRange(statement, rows)?.To ?? statement.PeriodTo ?? statement.ImportedUtc)
            .ThenBy(statement => statement.Id)
            .ToArray();

        // BR10: Missing Period is a failed BALANCE BRIDGE, not a raw date gap.
        // Between two independent Bank balance anchors, auto-calculate the balance using
        // every unique imported Debit/Credit row for the account. If the calculated balance
        // reaches the next SOURCE/MANUAL/RUNNING-BALANCE anchor exactly, the interval is
        // complete even when no explicit balance existed inside it. If it does not reach the
        // next anchor, only that failed interval is Missing Period. The observed next anchor
        // then becomes the new baseline, so later matching transactions are accepted.
        var anchors = new List<(DateTime Date, bool IsOpening, decimal Balance, long StatementId)>();

        foreach (var statement in statementList)
        {
            var statementRows = GetStatementSourceTransactions(statement, rows);
            var transactionRange = ResolveStatementTransactionDateRange(statement, rows);
            var from = transactionRange?.From ?? statement.PeriodFrom?.Date ?? statement.ImportedUtc.Date;
            var to = transactionRange?.To ?? statement.PeriodTo?.Date ?? from;
            if (to < from) (from, to) = (to, from);

            var opening = ResolveIndependentOpeningEvidence(statement, statementRows);
            if (opening.HasValue)
                anchors.Add((from, true, opening.Value, statement.Id));

            var closing = ResolveIndependentClosingEvidence(statement, statementRows);
            if (closing.HasValue)
                anchors.Add((to, false, closing.Value, statement.Id));
        }

        var orderedAnchors = anchors
            .OrderBy(anchor => anchor.Date)
            .ThenBy(anchor => anchor.IsOpening ? 0 : 1)
            .ThenBy(anchor => anchor.StatementId)
            .ToArray();
        if (orderedAnchors.Length < 2)
            return Array.Empty<BankDateRange>();

        // FIX3: actual statement transaction spans bound Missing Period display. A failed
        // balance check wholly inside one already-present statement is a Balance Difference,
        // not a Missing Period. For a failed bridge into a later statement, Missing Period
        // stops on the day BEFORE that statement's first actual Bank Transaction Date so
        // already-covered dates can never be painted as missing (for example a 27-Feb source
        // clips an earlier unresolved bridge to 26-Feb rather than extending through 31-Mar).
        var actualCoverageRanges = statementList
            .Select(statement => ResolveStatementTransactionDateRange(statement, rows))
            .Where(range => range.HasValue)
            .Select(range => range!.Value)
            .OrderBy(range => range.From)
            .ThenBy(range => range.To)
            .ToArray();

        var bridgeAuthorityRows = statementList.Length > 0
            ? BuildAccountMovementStream(statementList, rows).Rows
            : rows;

        var missing = new List<BankDateRange>();
        var previous = orderedAnchors[0];
        for (var index = 1; index < orderedAnchors.Length; index++)
        {
            var current = orderedAnchors[index];
            var bridgeRows = bridgeAuthorityRows.Where(row =>
            {
                var date = row.TransactionDate.Date;
                var afterPrevious = previous.IsOpening ? date >= previous.Date : date > previous.Date;
                var beforeCurrent = current.IsOpening ? date < current.Date : date <= current.Date;
                return afterPrevious && beforeCurrent;
            });

            var calculated = CalculateClosingBalance(
                previous.Balance,
                bridgeRows.Sum(row => row.Debit),
                bridgeRows.Sum(row => row.Credit));
            var difference = Math.Round(current.Balance - calculated, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(difference) > 0.01m && previous.StatementId != current.StatementId)
            {
                var bridgeFrom = previous.Date <= current.Date ? previous.Date : current.Date;
                var bridgeTo = previous.Date <= current.Date ? current.Date : previous.Date;

                // FIX4: Missing Period means only dates for which there is no actual imported
                // statement transaction coverage. A failed balance bridge can span two real
                // statements, but dates inside either statement are already covered and must
                // remain a Balance Difference/Overlap issue, not be painted as Missing.
                // Subtract every actual transaction-date span from the failed bridge and keep
                // only the uncovered gaps. This also makes the boundaries exact:
                // previous covered end + 1 day -> next covered start - 1 day.
                foreach (var gap in SubtractCoveredRanges(
                             new BankDateRange(bridgeFrom, bridgeTo),
                             actualCoverageRanges))
                {
                    missing.Add(gap);
                }
            }

            // Whether this bridge matched or failed, the actual/manual current balance is an
            // independent new anchor. A same-statement mismatch remains visible through the
            // statement/account Balance Difference instead of manufacturing a Missing Period.
            previous = current;
        }

        // A trailing interval with no next independent balance is Missing Balance Evidence,
        // not a Missing Period. A period becomes Missing only after an actual balance check
        // (including MANUAL closing balance) fails.
        // FIX5R1: Missing Period is range-only. Merge first so adjacent uncovered single days
        // can become one genuine multi-day period, then discard any remaining same-day result.
        // A From == To candidate is a reconciliation/balance issue, not a Missing Period.
        return MergeRanges(missing, coverageFrom, coverageTo)
            .Where(range => range.From.Date < range.To.Date)
            .ToArray();
    }


    private static IReadOnlyList<BankDateRange> SubtractCoveredRanges(
        BankDateRange candidate,
        IReadOnlyList<BankDateRange> coverage)
    {
        var remaining = new List<BankDateRange>
        {
            new(candidate.From.Date, candidate.To.Date)
        };

        foreach (var covered in MergeRanges(coverage))
        {
            if (remaining.Count == 0) break;
            var next = new List<BankDateRange>();
            foreach (var range in remaining)
            {
                if (covered.To < range.From || covered.From > range.To)
                {
                    next.Add(range);
                    continue;
                }

                if (covered.From > range.From)
                {
                    var leftTo = covered.From.AddDays(-1);
                    if (range.From <= leftTo)
                        next.Add(new BankDateRange(range.From, leftTo));
                }

                if (covered.To < range.To)
                {
                    var rightFrom = covered.To.AddDays(1);
                    if (rightFrom <= range.To)
                        next.Add(new BankDateRange(rightFrom, range.To));
                }
            }
            remaining = next;
        }

        return remaining;
    }

    public static decimal? ResolveExplicitClosingEvidence(BankStatementRecord statement)
    {
        if (statement.ClosingBalanceSource.Equals("SOURCE", StringComparison.OrdinalIgnoreCase) && statement.SourceClosingBalance.HasValue)
            return statement.SourceClosingBalance;
        if (statement.ClosingBalanceSource.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) && statement.ClosingBalance.HasValue)
            return statement.ClosingBalance;
        if (statement.ClosingBalanceSource.Equals("RUNNING BALANCE", StringComparison.OrdinalIgnoreCase) && statement.ClosingBalance.HasValue)
            return statement.ClosingBalance;
        return null;
    }

    public static decimal? ResolveIndependentOpeningEvidence(
        BankStatementRecord statement,
        IReadOnlyList<BankTransactionRecord> transactions)
    {
        if (statement.OpeningBalanceSource.Equals("SOURCE", StringComparison.OrdinalIgnoreCase))
            return statement.SourceOpeningBalance ?? statement.OpeningBalance;
        if (statement.OpeningBalanceSource.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) && statement.OpeningBalance.HasValue)
            return statement.OpeningBalance;
        if (statement.OpeningBalanceSource.Equals("RUNNING BALANCE", StringComparison.OrdinalIgnoreCase) && statement.OpeningBalance.HasValue)
            return statement.OpeningBalance;
        return DeriveOpeningFromRunningBalance(transactions);
    }

    public static IReadOnlyList<BankDateRange> MergeRanges(
        IEnumerable<BankDateRange> sourceRanges,
        DateTime? coverageFrom = null,
        DateTime? coverageTo = null)
    {
        var fromLimit = coverageFrom?.Date;
        var toLimit = coverageTo?.Date;
        var ranges = sourceRanges
            .Select(range => new BankDateRange(
                fromLimit.HasValue && range.From.Date < fromLimit.Value ? fromLimit.Value : range.From.Date,
                toLimit.HasValue && range.To.Date > toLimit.Value ? toLimit.Value : range.To.Date))
            .Where(range => range.From <= range.To)
            .OrderBy(range => range.From)
            .ThenBy(range => range.To)
            .ToArray();
        if (ranges.Length == 0) return Array.Empty<BankDateRange>();
        var merged = new List<BankDateRange>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.From > merged[^1].To.AddDays(1))
                merged.Add(range);
            else if (range.To > merged[^1].To)
                merged[^1] = new BankDateRange(merged[^1].From, range.To);
        }
        return merged;
    }

    public static bool IsDuplicateAuditStatement(BankStatementRecord statement) =>
        statement.ReconciliationStatus.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<BankDateRange> FindOverlaps(
        IEnumerable<BankStatementRecord> statements,
        IEnumerable<BankTransactionRecord>? transactions = null)
    {
        var materializedTransactions = transactions?.ToArray();
        var ranges = statements
            .Where(s => !IsBalanceChainExcludedStatement(s) && !IsDuplicateAuditStatement(s))
            .Select(s => materializedTransactions is null
                ? (s.PeriodFrom.HasValue && s.PeriodTo.HasValue
                    ? new BankDateRange?(new BankDateRange(s.PeriodFrom.Value.Date, s.PeriodTo.Value.Date))
                    : null)
                : ResolveStatementTransactionDateRange(s, materializedTransactions))
            .Where(range => range.HasValue)
            .Select(range => range!.Value)
            .OrderBy(range => range.From)
            .ThenBy(range => range.To)
            .ToArray();
        if (ranges.Length < 2) return Array.Empty<BankDateRange>();

        var result = new List<BankDateRange>();
        var maximumCoveredTo = ranges[0].To;
        for (var i = 1; i < ranges.Length; i++)
        {
            var currentFrom = ranges[i].From;
            var currentTo = ranges[i].To;
            if (currentFrom <= maximumCoveredTo)
            {
                var overlapTo = currentTo < maximumCoveredTo ? currentTo : maximumCoveredTo;
                result.Add(new BankDateRange(currentFrom, overlapTo));
            }
            if (currentTo > maximumCoveredTo) maximumCoveredTo = currentTo;
        }

        return MergeRanges(result);
    }

    public static bool ArePeriodsContiguous(BankStatementRecord previous, BankStatementRecord current) =>
        previous.PeriodTo.HasValue && current.PeriodFrom.HasValue &&
        current.PeriodFrom.Value.Date == previous.PeriodTo.Value.Date.AddDays(1);

    public static bool CanCarryBalanceForward(BankStatementRecord previous, BankStatementRecord current) =>
        previous.PeriodTo.HasValue && current.PeriodFrom.HasValue &&
        current.PeriodFrom.Value.Date > previous.PeriodTo.Value.Date;

    public static bool ArePeriodsContiguous(
        BankStatementRecord previous,
        BankStatementRecord current,
        IEnumerable<BankTransactionRecord> transactions)
    {
        var materialized = transactions as IReadOnlyList<BankTransactionRecord> ?? transactions.ToArray();
        var previousRange = ResolveStatementTransactionDateRange(previous, materialized);
        var currentRange = ResolveStatementTransactionDateRange(current, materialized);
        return previousRange.HasValue && currentRange.HasValue &&
               currentRange.Value.From == previousRange.Value.To.AddDays(1);
    }

    public static bool CanCarryBalanceForward(
        BankStatementRecord previous,
        BankStatementRecord current,
        IEnumerable<BankTransactionRecord> transactions)
    {
        var materialized = transactions as IReadOnlyList<BankTransactionRecord> ?? transactions.ToArray();
        var previousRange = ResolveStatementTransactionDateRange(previous, materialized);
        var currentRange = ResolveStatementTransactionDateRange(current, materialized);
        return previousRange.HasValue && currentRange.HasValue &&
               currentRange.Value.From > previousRange.Value.To;
    }


    public static bool IsIndependentRunningBalanceEvidence(BankTransactionRecord row) =>
        row.RunningBalance.HasValue &&
        (row.RunningBalanceSource.Equals("SOURCE", StringComparison.OrdinalIgnoreCase) ||
         row.RunningBalanceSource.Equals("MANUAL", StringComparison.OrdinalIgnoreCase));

    public static bool IsTrustedOpeningEvidence(BankResolvedBalance resolution) =>
        resolution.Value.HasValue &&
        !resolution.Source.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase) &&
        !resolution.Source.Equals("MISSING EVIDENCE", StringComparison.OrdinalIgnoreCase);

    public static bool IsSourceStatementTransaction(BankTransactionRecord row) =>
        !row.RecordStatus.Equals("Generated Transfer Charge", StringComparison.OrdinalIgnoreCase) &&
        !row.RecordStatus.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase);

    public static bool IsBalanceChainExcludedStatement(BankStatementRecord statement) =>
        statement.ReconciliationStatus.Equals("Delta Imported", StringComparison.OrdinalIgnoreCase) ||
        IsDuplicateAuditStatement(statement);

    // Retained for UI labels/backward compatibility only. Coverage and normal balance chains
    // must not discard a complete statement merely because duplicate rows were skipped.
    public static bool IsPartialOrDeltaStatement(BankStatementRecord statement) =>
        IsBalanceChainExcludedStatement(statement) ||
        statement.ReconciliationStatus.Equals("Overlap Review Required", StringComparison.OrdinalIgnoreCase);

}

public readonly record struct BankResolvedBalance(decimal? Value, string Source);
public readonly record struct BankResolvedClosing(decimal? Value, decimal? CalculatedValue, decimal? IndependentValue, string Source);
public readonly record struct BankDateRange(DateTime From, DateTime To)
{
    // Missing Period is always presented as a true date range. FindMissingPeriods excludes
    // same-day candidates, so a rendered range can never have identical From/To dates.
    public string Label => $"{From:dd-MM-yyyy} to {To:dd-MM-yyyy}";
}

public sealed class BankAccountBalanceSnapshot
{
    public long BankAccountId { get; init; }
    public decimal? OpeningBalance { get; init; }
    public string OpeningBalanceSource { get; init; } = string.Empty;
    public decimal? ClosingBalance { get; init; }
    public string ClosingBalanceSource { get; init; } = string.Empty;
    public decimal? CalculatedClosingBalance { get; init; }
    public decimal TotalDebit { get; init; }
    public decimal TotalCredit { get; init; }
    public decimal? Difference { get; init; }
    public IReadOnlyList<BankDateRange> MissingPeriods { get; init; } = Array.Empty<BankDateRange>();
    public IReadOnlyList<BankDateRange> OverlapPeriods { get; init; } = Array.Empty<BankDateRange>();
    public string Status { get; init; } = string.Empty;
    public string OpeningBalanceText => BankBusinessBalance.Format(OpeningBalance);
    public string ClosingBalanceText => BankBusinessBalance.Format(ClosingBalance);
    public string MissingPeriodText => string.Join("; ", MissingPeriods.Select(x => x.Label));
}
