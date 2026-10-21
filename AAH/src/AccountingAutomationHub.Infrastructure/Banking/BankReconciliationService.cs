using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Infrastructure.Banking;

public sealed class BankReconciliationService(IBankingRepository repository)
{
    private static bool IsExactMoneyDifference(decimal difference) =>
        Math.Round(difference, 2, MidpointRounding.AwayFromZero) == 0m;

    /// <summary>
    /// Rebuilds the complete Bank Statement balance authority for the Client/FY.
    /// Order of authority: original Bank Transaction Date + running balance -> source
    /// opening/closing evidence -> chronological Bank Account chain. Physical file order is
    /// provenance only. This method is intentionally
    /// idempotent and is safe to call after import, edit, delete and manual balance review.
    /// </summary>
    public IReadOnlyList<BankReconciliationSummary> Reconcile(long clientId, string financialYear)
    {
        var statements = repository.GetStatements(clientId, financialYear).ToList();
        var transactions = repository.GetTransactions(clientId, financialYear, includeArchived: true)
            .Where(IsSourceStatementTransaction)
            .ToList();
        foreach (var accountGroup in statements.GroupBy(item => item.BankAccountId))
        {
            BankStatementRecord? previous = null;
            foreach (var statement in OrderStatements(accountGroup, transactions))
            {
                if (IsPartialOrDeltaStatement(statement))
                    continue;
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
                var resolvedClosing = BankBalanceAuthority.ResolveClosingBalance(statement, rows, opening, movementRows);
                var sourceClosing = resolvedClosing.IndependentValue;
                var calculatedClosing = resolvedClosing.CalculatedValue;
                var closing = resolvedClosing.Value;
                var closingSource = resolvedClosing.Source;
                var runningCheck = VerifyRunningBalanceSequence(rows, opening);
                var fileDifference = sourceClosing.HasValue && calculatedClosing.HasValue
                    ? Math.Round(sourceClosing.Value - calculatedClosing.Value, 2, MidpointRounding.AwayFromZero)
                    : 0m;

                var hasIndependentClosing = sourceClosing.HasValue;
                var status = BuildFileStatus(resolvedOpening, closing, hasIndependentClosing, fileDifference, runningCheck);

                var storedOpening = statement.OpeningBalance;
                var storedClosing = statement.ClosingBalance;
                var storedOpeningSource = statement.OpeningBalanceSource;
                var storedClosingSource = statement.ClosingBalanceSource;
                var storedStatus = statement.ReconciliationStatus;
                var storedDifference = statement.Difference;

                statement.OpeningBalance = opening;
                statement.OpeningBalanceSource = openingSource;
                statement.ClosingBalance = closing;
                statement.ClosingBalanceSource = closingSource;
                statement.TotalDebit = debit;
                statement.TotalCredit = credit;
                statement.TransactionCount = movementRows.Count;
                statement.ReconciliationStatus = status;
                statement.Difference = fileDifference;

                if (!NullableMoneyEqual(storedOpening, opening) ||
                    !NullableMoneyEqual(storedClosing, closing) ||
                    !storedOpeningSource.Equals(openingSource, StringComparison.OrdinalIgnoreCase) ||
                    !storedClosingSource.Equals(closingSource, StringComparison.OrdinalIgnoreCase) ||
                    !storedStatus.Equals(status, StringComparison.OrdinalIgnoreCase) ||
                    !IsExactMoneyDifference(storedDifference - fileDifference))
                {
                    repository.SaveStatementBalances(statement.Id, opening, closing, status, fileDifference, openingSource, closingSource);
                }
                previous = statement;
            }

            PersistChainVerification(
                OrderStatements(accountGroup, transactions).Where(item => !IsPartialOrDeltaStatement(item)).ToList(),
                transactions.Where(item => item.BankAccountId == accountGroup.Key).ToList());
        }

        var summaries = repository.GetReconciliation(clientId, financialYear).ToList();
        var statementsById = repository.GetStatements(clientId, financialYear).ToDictionary(item => item.Id);
        foreach (var accountGroup in summaries.GroupBy(item => item.BankAccountId))
        {
            BankStatementRecord? previous = null;
            foreach (var summary in accountGroup.OrderBy(item =>
                         BankBalanceAuthority.ResolveStatementTransactionDateRange(statementsById[item.StatementId], transactions)?.From
                         ?? statementsById[item.StatementId].PeriodFrom
                         ?? DateTime.MinValue))
            {
                var statement = statementsById[summary.StatementId];
                if (!statement.OpeningBalance.HasValue && previous?.ClosingBalance is decimal previousClosing &&
                    BankBalanceAuthority.CanCarryBalanceForward(previous, statement, transactions))
                    summary.SuggestedOpeningBalance = previousClosing;
                previous = statement;
            }
        }
        return summaries;
    }

    private void PersistChainVerification(
        IReadOnlyList<BankStatementRecord> ordered,
        IReadOnlyList<BankTransactionRecord> accountTransactions)
    {
        if (ordered.Count == 0) return;

        // BR10: calendar gaps are not failures by themselves. The account balance bridge
        // decides whether movement between two balance anchors is complete. Retain overlap
        // review, direct independent CB/OB mismatches and whole-account imported-total check.
        for (var index = 1; index < ordered.Count; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var currentStatus = current.ReconciliationStatus;
            var currentDifference = current.Difference;

            var previousRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(previous, accountTransactions);
            var currentRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(current, accountTransactions);
            if (previousRange.HasValue && currentRange.HasValue &&
                currentRange.Value.From <= previousRange.Value.To)
            {
                currentStatus = PromoteIssueStatus(currentStatus, "Statement Overlap");
            }

            if (BankBalanceAuthority.CanCarryBalanceForward(previous, current, accountTransactions) && previous.ClosingBalance.HasValue)
            {
                var currentRows = BankBalanceAuthority.GetStatementSourceTransactions(current, accountTransactions);
                var independentOpening = BankBalanceAuthority.ResolveIndependentOpeningEvidence(current, currentRows);
                if (independentOpening.HasValue)
                {
                    var chainDifference = Math.Round(independentOpening.Value - previous.ClosingBalance.Value, 2, MidpointRounding.AwayFromZero);
                    if (!IsExactMoneyDifference(chainDifference))
                    {
                        currentStatus = PromoteIssueStatus(currentStatus, "Chain Balance Difference");
                        currentDifference = chainDifference;
                    }
                }
            }

            var chainChanged = !current.ReconciliationStatus.Equals(currentStatus, StringComparison.OrdinalIgnoreCase) ||
                               !IsExactMoneyDifference(current.Difference - currentDifference);
            current.ReconciliationStatus = currentStatus;
            current.Difference = currentDifference;
            if (chainChanged)
                repository.SaveStatementReconciliation(current.Id, currentStatus, currentDifference);
        }

        var accountId = ordered[0].BankAccountId;
        var snapshot = BankBalanceAuthority.BuildAccountSnapshot(accountId, ordered, accountTransactions);

        // Mark only statements intersecting a balance bridge that actually failed. A quiet
        // calendar gap with a matching next balance remains valid and is not labelled missing.
        foreach (var range in snapshot.MissingPeriods)
        {
            var affected = ordered
                .Where(statement =>
                {
                    var transactionRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(statement, accountTransactions);
                    var from = transactionRange?.From ?? statement.PeriodFrom?.Date ?? DateTime.MaxValue.Date;
                    var to = transactionRange?.To ?? statement.PeriodTo?.Date ?? DateTime.MinValue.Date;
                    return from <= range.To && to >= range.From;
                })
                .DefaultIfEmpty(ordered[^1]);

            foreach (var statement in affected)
            {
                var status = PromoteIssueStatus(statement.ReconciliationStatus, "Missing Period");
                if (!statement.ReconciliationStatus.Equals(status, StringComparison.OrdinalIgnoreCase))
                {
                    statement.ReconciliationStatus = status;
                    repository.SaveStatementReconciliation(statement.Id, status, statement.Difference);
                }
            }
        }

        if (snapshot.Difference.HasValue && !IsExactMoneyDifference(snapshot.Difference.Value))
        {
            var last = ordered[^1];
            var accountStatus = PromoteIssueStatus(last.ReconciliationStatus, "Account Balance Difference");
            var accountChanged = !last.ReconciliationStatus.Equals(accountStatus, StringComparison.OrdinalIgnoreCase) ||
                                 !IsExactMoneyDifference(last.Difference - snapshot.Difference.Value);
            last.ReconciliationStatus = accountStatus;
            last.Difference = snapshot.Difference.Value;
            if (accountChanged)
                repository.SaveStatementReconciliation(last.Id, accountStatus, snapshot.Difference.Value);
        }
    }

    public BankBalanceGateResult VerifyExactClosingBalances(
        long clientId,
        string financialYear,
        IEnumerable<long> bankAccountIds)
    {
        // Rebuild automatic balances first, then use the same BR10 balance-bridge authority
        // that drives Missing Period and the Banking snapshot. Exact verification is therefore
        // based on imported Debit/Credit movement between independent Bank balance anchors,
        // not on whether every calendar date has a separate statement row.
        Reconcile(clientId, financialYear);

        var accountIds = bankAccountIds.Where(id => id > 0).Distinct().ToHashSet();
        if (accountIds.Count == 0)
        {
            return new BankBalanceGateResult
            {
                IsExact = true,
                Detail = "No Bank account balance gate was required."
            };
        }

        var statements = repository.GetStatements(clientId, financialYear)
            .Where(statement => accountIds.Contains(statement.BankAccountId))
            .Where(statement => !BankBalanceAuthority.IsBalanceChainExcludedStatement(statement))
            .ToArray();
        var transactions = repository.GetTransactions(clientId, financialYear, includeArchived: true)
            .Where(IsSourceStatementTransaction)
            .ToArray();
        var accounts = repository.GetAccounts(clientId, financialYear).ToDictionary(account => account.Id);
        var result = new BankBalanceGateResult();
        var issues = new List<string>();

        foreach (var accountId in accountIds)
        {
            var account = accounts.TryGetValue(accountId, out var found) ? found.DisplayName : $"Account {accountId}";
            var accountStatements = statements.Where(statement => statement.BankAccountId == accountId).ToArray();
            var accountRows = transactions.Where(row => row.BankAccountId == accountId).ToArray();
            if (accountStatements.Length == 0 && accountRows.Length == 0)
            {
                result.StatementsMissingBalances++;
                issues.Add($"{account}: no imported Bank Statement balance evidence is available.");
                continue;
            }

            var snapshot = BankBalanceAuthority.BuildAccountSnapshot(accountId, accountStatements, accountRows);
            result.StatementsChecked += accountStatements.Length;

            var boundaryEvidenceMissing = !snapshot.OpeningBalance.HasValue || !snapshot.ClosingBalance.HasValue ||
                                          snapshot.OpeningBalanceSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
                                          snapshot.OpeningBalanceSource.Equals("MISSING EVIDENCE", StringComparison.OrdinalIgnoreCase) ||
                                          snapshot.ClosingBalanceSource.Equals("CALCULATED", StringComparison.OrdinalIgnoreCase) ||
                                          snapshot.ClosingBalanceSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
                                          snapshot.ClosingBalanceSource.Equals("MISSING EVIDENCE", StringComparison.OrdinalIgnoreCase);
            if (boundaryEvidenceMissing)
            {
                result.StatementsMissingBalances++;
                issues.Add($"{account}: independent opening/final closing balance evidence is incomplete.");
            }

            if (snapshot.MissingPeriods.Count > 0)
            {
                result.StatementsMissingBalances += snapshot.MissingPeriods.Count;
                issues.Add($"{account}: balance bridge failed for {string.Join("; ", snapshot.MissingPeriods.Select(range => range.Label))}.");
            }

            if (snapshot.Difference.HasValue)
            {
                result.MaximumDifference = Math.Max(result.MaximumDifference, Math.Abs(snapshot.Difference.Value));
                if (!IsExactMoneyDifference(snapshot.Difference.Value))
                    issues.Add($"{account}: imported Debit/Credit calculated closing {BankBusinessBalance.Format(snapshot.CalculatedClosingBalance)} does not equal final Bank closing {BankBusinessBalance.Format(snapshot.ClosingBalance)} (difference {snapshot.Difference.Value:N2}).");
            }
            else if (!boundaryEvidenceMissing)
            {
                result.StatementsMissingBalances++;
                issues.Add($"{account}: imported Debit/Credit closing verification could not be completed.");
            }
        }

        result.IsExact = result.StatementsChecked > 0 && result.StatementsMissingBalances == 0 && issues.Count == 0;
        result.Detail = result.IsExact
            ? $"Imported Debit/Credit movement and all balance bridges verified for {result.StatementsChecked:N0} statement(s); maximum difference {result.MaximumDifference:N2}."
            : issues.Count > 0
                ? string.Join(" | ", issues)
                : "No statement with complete balance evidence was available for exact closing verification.";
        return result;
    }

    public void VerifyStatement(BankStatementRecord statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        Reconcile(statement.ClientId, statement.FinancialYear);
    }

    public void SkipStatement(BankStatementRecord statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        repository.SaveStatementBalances(statement.Id, statement.OpeningBalance, statement.ClosingBalance, "Skipped", statement.Difference);
    }

    public IReadOnlyList<string> BuildStatementChainIssues(long clientId, string financialYear)
    {
        var accounts = repository.GetAccounts(clientId, financialYear).ToDictionary(item => item.Id);
        var statements = repository.GetStatements(clientId, financialYear)
            .Where(item => !BankBalanceAuthority.IsBalanceChainExcludedStatement(item))
            .GroupBy(item => item.BankAccountId);
        var transactions = repository.GetTransactions(clientId, financialYear, includeArchived: true)
            .Where(IsSourceStatementTransaction)
            .ToArray();
        var issues = new List<string>();

        foreach (var group in statements)
        {
            var ordered = OrderStatements(group, transactions).ToList();
            var account = accounts.TryGetValue(group.Key, out var found) ? found.DisplayName : $"Account {group.Key}";
            var accountRows = transactions.Where(row => row.BankAccountId == group.Key).ToArray();
            var snapshot = BankBalanceAuthority.BuildAccountSnapshot(group.Key, ordered, accountRows);

            foreach (var range in snapshot.MissingPeriods)
                issues.Add($"{account}: balance-driven missing period {range.Label}; imported Debit/Credit movement did not reconcile to the next independent/manual Bank balance anchor.");

            foreach (var range in snapshot.OverlapPeriods)
                issues.Add($"{account}: statement periods overlap {range.Label}.");

            if (snapshot.Difference.HasValue && !IsExactMoneyDifference(snapshot.Difference.Value))
                issues.Add($"{account}: whole-account imported Debit/Credit closing mismatch — calculated {BankBusinessBalance.Format(snapshot.CalculatedClosingBalance)} vs latest closing {BankBusinessBalance.Format(snapshot.ClosingBalance)} (difference {snapshot.Difference.Value:N2}).");
        }

        return issues;
    }

    private static bool NullableMoneyEqual(decimal? left, decimal? right)
    {
        if (!left.HasValue || !right.HasValue) return left.HasValue == right.HasValue;
        return IsExactMoneyDifference(left.Value - right.Value);
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

    private static RunningBalanceCheck VerifyRunningBalanceSequence(IReadOnlyList<BankTransactionRecord> rows, decimal? opening)
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

    private sealed record RunningBalanceCheck(
        int BalanceCount,
        int MissingBalanceCount,
        int MismatchCount,
        decimal MaximumDifference);
}
