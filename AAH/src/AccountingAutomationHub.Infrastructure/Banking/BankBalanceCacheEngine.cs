using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Infrastructure.Banking;

/// <summary>
/// Pure in-memory Bank balance finalizer used by cache-first Auto Import.
/// It performs transaction running-balance completion/verification, file balance
/// reconstruction, adjacent-file coverage/chain checks and whole-account closing
/// verification without touching SQLite.
/// </summary>
public static class BankBalanceCacheEngine
{
    public static void FinalizeAccount(
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlySet<long>? fillMissingRunningBalanceStatementIds = null)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(transactions);
        if (statements.Count == 0) return;

        BankStatementRecord? previous = null;
        var ordered = OrderStatements(statements, transactions).ToList();
        foreach (var statement in ordered)
        {
            if (IsPartialOrDeltaStatement(statement)) continue;
            if (statement.ReconciliationStatus.Equals("Skipped", StringComparison.OrdinalIgnoreCase))
            {
                previous = statement;
                continue;
            }

            var rows = BankBalanceAuthority.GetStatementSourceTransactions(statement, transactions);
            var movementRows = BankBalanceAuthority.GetStatementPeriodTransactions(statement, transactions);
            var debit = movementRows.Sum(item => item.Debit);
            var credit = movementRows.Sum(item => item.Credit);

            var contiguousPreviousClosing = previous is not null && BankBalanceAuthority.CanCarryBalanceForward(previous, statement, transactions)
                ? previous.ClosingBalance
                : null;
            var resolvedOpening = BankBalanceAuthority.ResolveOpeningBalance(statement, rows, contiguousPreviousClosing, movementRows);
            var opening = resolvedOpening.Value;
            var openingSource = resolvedOpening.Source;
            // Capture independent closing evidence before filling any cache-generated running balances.
            var sourceClosing = BankBalanceAuthority.ResolveIndependentClosingEvidence(statement, rows);

            if (fillMissingRunningBalanceStatementIds?.Contains(statement.Id) == true)
                FillMissingRunningBalances(rows, opening);

            // Resolve after cache fill, but independent evidence is still captured from the
            // pre-fill rows because CALCULATED running balances are never authoritative.
            var closingResolution = BankBalanceAuthority.ResolveClosingBalance(statement, rows, opening, movementRows);
            var calculatedClosing = closingResolution.CalculatedValue;
            var closing = closingResolution.Value;
            var closingSource = closingResolution.Source;
            var runningCheck = VerifyRunningBalanceSequence(rows, opening);
            var fileDifference = sourceClosing.HasValue && calculatedClosing.HasValue
                ? Math.Round(sourceClosing.Value - calculatedClosing.Value, 2, MidpointRounding.AwayFromZero)
                : 0m;

            statement.OpeningBalance = opening;
            statement.OpeningBalanceSource = openingSource;
            statement.ClosingBalance = closing;
            statement.ClosingBalanceSource = closingSource;
            statement.TotalDebit = debit;
            statement.TotalCredit = credit;
            statement.TransactionCount = movementRows.Count;
            statement.ReconciliationStatus = BuildFileStatus(resolvedOpening, closing, sourceClosing.HasValue, fileDifference, runningCheck);
            statement.Difference = fileDifference;
            previous = statement;
        }

        FinalizeChain(ordered.Where(item => !IsPartialOrDeltaStatement(item)).ToList(), transactions);
    }

    private static void FillMissingRunningBalances(IReadOnlyList<BankTransactionRecord> rows, decimal? opening)
    {
        if (!opening.HasValue) return;
        var expected = opening.Value;
        foreach (var row in BankBalanceAuthority.OrderTransactionsChronologically(rows))
        {
            expected = BankBalanceAuthority.CalculateClosingBalance(expected, row.Debit, row.Credit);
            if (!row.RunningBalance.HasValue)
            {
                row.RunningBalance = expected;
                row.RunningBalanceSource = "CALCULATED";
            }
        }
    }

    private static void FinalizeChain(
        IReadOnlyList<BankStatementRecord> ordered,
        IReadOnlyList<BankTransactionRecord> transactions)
    {
        if (ordered.Count == 0) return;

        for (var index = 1; index < ordered.Count; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var currentStatus = current.ReconciliationStatus;
            var currentDifference = current.Difference;

            var previousRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(previous, transactions);
            var currentRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(current, transactions);
            if (previousRange.HasValue && currentRange.HasValue &&
                currentRange.Value.From <= previousRange.Value.To)
            {
                currentStatus = PromoteIssueStatus(currentStatus, "Statement Overlap");
            }

            if (BankBalanceAuthority.CanCarryBalanceForward(previous, current, transactions) && previous.ClosingBalance.HasValue)
            {
                var currentRows = BankBalanceAuthority.GetStatementSourceTransactions(current, transactions);
                var independentOpening = BankBalanceAuthority.ResolveIndependentOpeningEvidence(current, currentRows);
                if (independentOpening.HasValue)
                {
                    var chainDifference = Math.Round(
                        independentOpening.Value - previous.ClosingBalance.Value,
                        2,
                        MidpointRounding.AwayFromZero);
                    if (!IsExactMoneyDifference(chainDifference))
                    {
                        currentStatus = PromoteIssueStatus(currentStatus, "Chain Balance Difference");
                        currentDifference = chainDifference;
                    }
                }
            }

            current.ReconciliationStatus = currentStatus;
            current.Difference = currentDifference;
        }

        var accountId = ordered[0].BankAccountId;
        var snapshot = BankBalanceAuthority.BuildAccountSnapshot(accountId, ordered, transactions);
        foreach (var range in snapshot.MissingPeriods)
        {
            var affected = ordered
                .Where(statement =>
                {
                    var transactionRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(statement, transactions);
                    var from = transactionRange?.From ?? statement.PeriodFrom?.Date ?? DateTime.MaxValue.Date;
                    var to = transactionRange?.To ?? statement.PeriodTo?.Date ?? DateTime.MinValue.Date;
                    return from <= range.To && to >= range.From;
                })
                .DefaultIfEmpty(ordered[^1]);
            foreach (var statement in affected)
                statement.ReconciliationStatus = PromoteIssueStatus(statement.ReconciliationStatus, "Missing Period");
        }

        if (snapshot.Difference.HasValue && !IsExactMoneyDifference(snapshot.Difference.Value))
        {
            var last = ordered[^1];
            last.ReconciliationStatus = PromoteIssueStatus(last.ReconciliationStatus, "Account Balance Difference");
            last.Difference = snapshot.Difference.Value;
        }
    }

    private static string BuildFileStatus(
        BankResolvedBalance openingResolution,
        decimal? closing,
        bool hasIndependentClosing,
        decimal fileDifference,
        RunningBalanceCheck runningCheck)
    {
        var opening = openingResolution.Value;
        if (!BankBalanceAuthority.IsTrustedOpeningEvidence(openingResolution) || !closing.HasValue) return "Balance Review Required";
        if (hasIndependentClosing && !IsExactMoneyDifference(fileDifference)) return "Difference";
        if (runningCheck.MismatchCount > 0) return "Running Balance Difference";
        if (runningCheck.BalanceCount == 0)
            return hasIndependentClosing ? "Running Balance Missing" : "Balance Review Required";
        if (runningCheck.MissingBalanceCount > 0) return "Running Balance Incomplete";
        if (!hasIndependentClosing) return "Balance Review Required";
        return "Reconciled";
    }

    private static RunningBalanceCheck VerifyRunningBalanceSequence(
        IReadOnlyList<BankTransactionRecord> rows,
        decimal? opening)
    {
        if (!opening.HasValue || rows.Count == 0)
            return new RunningBalanceCheck(
                rows.Count(BankBalanceAuthority.IsIndependentRunningBalanceEvidence),
                rows.Count(item => !BankBalanceAuthority.IsIndependentRunningBalanceEvidence(item)),
                0,
                0m);

        var expected = opening.Value;
        var balanceCount = 0;
        var missingCount = 0;
        var mismatchCount = 0;
        var maximumDifference = 0m;
        foreach (var row in BankBalanceAuthority.OrderTransactionsChronologically(rows))
        {
            expected = BankBalanceAuthority.CalculateClosingBalance(expected, row.Debit, row.Credit);
            if (!BankBalanceAuthority.IsIndependentRunningBalanceEvidence(row))
            {
                missingCount++;
                continue;
            }

            balanceCount++;
            var difference = Math.Round(row.RunningBalance!.Value - expected, 2, MidpointRounding.AwayFromZero);
            maximumDifference = Math.Max(maximumDifference, Math.Abs(difference));
            if (!IsExactMoneyDifference(difference)) mismatchCount++;
        }

        return new RunningBalanceCheck(balanceCount, missingCount, mismatchCount, maximumDifference);
    }



    private static bool IsSourceStatementTransaction(BankTransactionRecord row) =>
        !row.RecordStatus.Equals("Generated Transfer Charge", StringComparison.OrdinalIgnoreCase) &&
        !row.RecordStatus.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase);

    private static bool IsPartialOrDeltaStatement(BankStatementRecord statement) =>
        BankBalanceAuthority.IsBalanceChainExcludedStatement(statement);

    private static IEnumerable<BankStatementRecord> OrderStatements(
        IEnumerable<BankStatementRecord> source,
        IReadOnlyList<BankTransactionRecord> transactions) =>
        source.OrderBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, transactions)?.From ?? item.PeriodFrom ?? item.ImportedUtc)
            .ThenBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, transactions)?.To ?? item.PeriodTo ?? item.ImportedUtc)
            .ThenBy(item => item.Id);

    private static bool ArePeriodsContiguous(BankStatementRecord previous, BankStatementRecord current) =>
        previous.PeriodTo.HasValue && current.PeriodFrom.HasValue &&
        current.PeriodFrom.Value.Date == previous.PeriodTo.Value.Date.AddDays(1);

    private static string PromoteIssueStatus(string currentStatus, string issueStatus)
    {
        if (currentStatus.Equals("Skipped", StringComparison.OrdinalIgnoreCase) ||
            currentStatus.Equals("Difference", StringComparison.OrdinalIgnoreCase) ||
            currentStatus.Equals("Running Balance Difference", StringComparison.OrdinalIgnoreCase))
            return currentStatus;
        return issueStatus;
    }

    private static bool IsExactMoneyDifference(decimal difference) =>
        Math.Round(difference, 2, MidpointRounding.AwayFromZero) == 0m;

    private sealed record RunningBalanceCheck(
        int BalanceCount,
        int MissingBalanceCount,
        int MismatchCount,
        decimal MaximumDifference);
}
