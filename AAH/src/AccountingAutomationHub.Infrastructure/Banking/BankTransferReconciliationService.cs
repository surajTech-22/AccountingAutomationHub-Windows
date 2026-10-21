using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Infrastructure.Banking;

public sealed class BankTransferReconciliationService(
    IBankingRepository repository,
    SqliteMasterRepository masterRepository)
{
    private const decimal Tolerance = 0.01m;

    /// <summary>
    /// Runs one Inter-Bank reconciliation pipeline over a unified Bank-transaction pool.
    /// Imported Bank Statement rows and converted live-Tally Bank/OD/Secured-Loan postings
    /// use the same Date/Bank/Debit/Credit/Narration/Reference/Amount matching rules.
    /// Tally-to-Tally pairs are ignored. For a proven Imported-to-Tally pair this service
    /// records only the established Inter-Bank accounting decision; the Tally execution
    /// layer applies/verifies that result as Contra and retires the imported duplicate.
    /// Balance Reconciliation then runs as the final, separate account-level control; it
    /// reports a remaining balance difference without undoing a proven Inter-Bank match.
    /// </summary>
    public IReadOnlyList<BankTransferMatch> Reconcile(
        long clientId,
        string financialYear,
        IReadOnlyCollection<TallyConvertedBankTransaction>? tallyTransactions = null)
    {
        var accounts = repository.GetAccounts(clientId, financialYear).ToDictionary(item => item.Id);
        var existingMatches = repository.GetTransferMatches(clientId, financialYear).ToList();
        var manualMatches = existingMatches
            .Where(item => item.Reason.StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var persistedTallyMatches = existingMatches
            .Where(IsTallyMatch)
            .ToList();
        var manualIds = manualMatches
            .SelectMany(item => new[] { item.SourceTransactionId, item.TargetTransactionId })
            .Where(id => id > 0)
            .ToHashSet();
        var preservedTallyIds = persistedTallyMatches
            .Select(item => item.SourceTransactionId)
            .Where(id => id > 0)
            .ToHashSet();

        var importedRows = repository.GetTransactions(clientId, financialYear, includeArchived: true)
            .Where(item => !item.RecordStatus.Equals("Archived Tally Reconciled", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Debit > 0 || item.Credit > 0)
            .Where(item => !item.RecordStatus.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase))
            .Where(item => !item.RecordStatus.Equals("Generated Transfer Charge", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // This is the single candidate pool consumed by Inter-Bank reconciliation.
        // Tally conversion has already reversed the accounting-side Bank DR/CR once;
        // from here onward the matcher sees ordinary Bank-Statement Debit/Credit fields.
        var pool = new List<ReconciliationTransaction>();
        foreach (var row in importedRows)
        {
            if (row.Id <= 0 || manualIds.Contains(row.Id)) continue;
            if (!accounts.TryGetValue(row.BankAccountId, out var account)) continue;
            pool.Add(ReconciliationTransaction.FromImported(row, account));
        }
        if (tallyTransactions is not null)
        {
            foreach (var row in tallyTransactions)
            {
                if (row.TransactionDate == default || row.Amount <= Tolerance) continue;
                var knownAccount = accounts.Values.FirstOrDefault(account => BankAccountMatchesTallyLedger(account, row));
                pool.Add(ReconciliationTransaction.FromTally(row, knownAccount));
            }
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in manualIds) used.Add($"BANK|{id}");
        foreach (var id in preservedTallyIds) used.Add($"BANK|{id}");
        foreach (var conversionId in persistedTallyMatches.Select(item => item.TallyConversionId).Where(value => !string.IsNullOrWhiteSpace(value)))
            used.Add($"TALLY|{conversionId}");
        var normalMatches = new List<BankTransferMatch>(manualMatches);
        var tallyMatches = new List<BankTransferMatch>(persistedTallyMatches);

        foreach (var dateGroup in pool.GroupBy(item => item.TransactionDate.Date).OrderBy(group => group.Key))
        {
            var credits = dateGroup.Where(item => item.Credit > Tolerance).ToList();
            // Tally is existing-book authority, so process protected Tally debit rows first.
            // This prevents a newly imported opposite-side row from being paired to another
            // imported row when the corresponding transfer already exists in Tally.
            foreach (var source in dateGroup.Where(item => item.Debit > Tolerance)
                         .OrderBy(item => item.IsTally ? 0 : 1)
                         .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (used.Contains(source.Key)) continue;

                var sourceMethod = BankNarrationParser.DetectExplicitMethod(source.Narration);
                var sourceReferences = BankNarrationParser.ExtractReferenceGroups(
                    source.ReferenceNumber.Length > 0 ? source.ReferenceNumber + " " + source.Narration : source.Narration,
                    sourceMethod,
                    source.Debit,
                    source.TransactionDate);
                if (sourceReferences.Count == 0) continue;

                var exactAmountCandidates = new List<Candidate>();
                var tariffCandidates = new List<Candidate>();
                foreach (var creditCandidate in credits)
                {
                    if (used.Contains(creditCandidate.Key) || creditCandidate.Key.Equals(source.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    // Two live Tally postings are already accounting entries. Inter-Bank reconciliation
                    // never deletes or rewrites them and therefore has nothing to do with a Tally-Tally pair.
                    if (source.IsTally && creditCandidate.IsTally) continue;
                    if (SameFinancialAccount(source, creditCandidate)) continue;

                    var targetMethod = BankNarrationParser.DetectExplicitMethod(creditCandidate.Narration);
                    if (sourceMethod.Length > 0 && targetMethod.Length > 0 &&
                        !sourceMethod.Equals(targetMethod, StringComparison.OrdinalIgnoreCase)) continue;

                    var targetReferences = BankNarrationParser.ExtractReferenceGroups(
                        creditCandidate.ReferenceNumber.Length > 0 ? creditCandidate.ReferenceNumber + " " + creditCandidate.Narration : creditCandidate.Narration,
                        targetMethod,
                        creditCandidate.Credit,
                        creditCandidate.TransactionDate);
                    var sharedReference = SelectSharedReference(
                        sourceReferences,
                        targetReferences,
                        sourceMethod.Length > 0 ? sourceMethod : targetMethod);
                    if (sharedReference.Length == 0) continue;

                    var difference = Math.Round(source.Debit - creditCandidate.Credit, 2);
                    if (Math.Abs(difference) <= Tolerance)
                    {
                        exactAmountCandidates.Add(new Candidate(creditCandidate, sharedReference, 0m, "EXACT AMOUNT"));
                        continue;
                    }

                    // Existing transfer-charge generation is retained only when BOTH rows came
                    // from imported statements. If Tally is one side, Tally is already the books
                    // authority and AAH must not invent an additional Bank Charges posting.
                    if (!source.IsTally && !creditCandidate.IsTally && difference > Tolerance && source.KnownAccount is not null &&
                        BankTransferChargeRules.TryMatch(
                            source.KnownAccount.BankName,
                            sourceMethod.Length > 0 ? sourceMethod : targetMethod,
                            creditCandidate.Credit,
                            source.Narration,
                            difference,
                            out var charge,
                            out var rule))
                    {
                        tariffCandidates.Add(new Candidate(creditCandidate, sharedReference, charge, rule));
                    }
                }

                Candidate? selected = exactAmountCandidates.Count == 1
                    ? exactAmountCandidates[0]
                    : exactAmountCandidates.Count == 0 && tariffCandidates.Count == 1
                        ? tariffCandidates[0]
                        : null;
                if (selected is null) continue;

                var target = selected.Target;
                used.Add(source.Key);
                used.Add(target.Key);

                if (source.IsTally || target.IsTally)
                {
                    var imported = source.Imported ?? target.Imported;
                    var tally = source.Tally ?? target.Tally;
                    if (imported is null || tally is null) continue;

                    var match = new BankTransferMatch
                    {
                        ClientId = clientId,
                        FinancialYear = financialYear,
                        SourceTransactionId = imported.Id,
                        TargetTransactionId = 0,
                        MatchId = $"TALLYTRF|{tally.ConversionId}|I{imported.Id}",
                        MatchDate = source.TransactionDate.Date,
                        Amount = target.Credit,
                        ChargeAmount = 0m,
                        ChargeRule = "TALLY AUTHORITY",
                        ReferenceNumber = selected.Reference,
                        Status = "Tally Inter-Bank Matched - Awaiting Contra Apply",
                        Reason = $"Exact date, amount, different financial accounts and exact shared reference: {source.DisplayName} -> {target.DisplayName}; existing Tally voucher must be applied/verified as Contra before the imported Bank Statement duplicate can be retired",
                        MatchOrigin = "Tally Converted Bank Transaction",
                        TallyConversionId = tally.ConversionId,
                        TallyMasterId = tally.TallyMasterId,
                        TallyAlterId = tally.TallyAlterId,
                        TallyVoucherType = tally.VoucherType,
                        TallyVoucherNumber = tally.VoucherNumber,
                        TallyBankLedger = tally.BankLedgerName,
                        TallyBankLedgerMasterId = tally.BankLedgerMasterId,
                        ImportedBankLedgerMasterId = (source.Imported ?? target.Imported) is not null
                            ? (source.Imported is not null ? source.KnownAccount?.TallyLedgerMasterId : target.KnownAccount?.TallyLedgerMasterId) ?? string.Empty
                            : string.Empty,
                        SourceBankLedgerMasterId = source.IsTally
                            ? tally.BankLedgerMasterId
                            : (source.KnownAccount?.TallyLedgerMasterId ?? string.Empty),
                        TargetBankLedgerMasterId = target.IsTally
                            ? tally.BankLedgerMasterId
                            : (target.KnownAccount?.TallyLedgerMasterId ?? string.Empty),
                        TallyNarration = tally.Narration,
                        TallyReference = tally.ReferenceNumber,
                        TallySignedAmount = tally.TallySignedAmount,
                        TallyIsDebit = tally.TallyIsDebit,
                        CreatedUtc = DateTime.UtcNow
                    };
                    tallyMatches.Add(match);
                    continue;
                }

                var sourceImported = source.Imported!;
                var targetImported = target.Imported!;
                var matchId = $"TRF-{source.TransactionDate:yyyyMMdd}-{sourceImported.Id}-{targetImported.Id}";
                var reason = selected.ChargeAmount > Tolerance
                    ? $"Exact date, different account, exact shared reference and configured transfer-charge difference ({selected.ChargeRule}): {source.DisplayName} -> {target.DisplayName}"
                    : $"Exact date, amount, different account and exact shared reference: {source.DisplayName} -> {target.DisplayName}";

                normalMatches.Add(new BankTransferMatch
                {
                    ClientId = clientId,
                    FinancialYear = financialYear,
                    SourceTransactionId = sourceImported.Id,
                    TargetTransactionId = targetImported.Id,
                    MatchId = matchId,
                    MatchDate = source.TransactionDate.Date,
                    Amount = target.Credit,
                    ChargeAmount = selected.ChargeAmount,
                    ChargeRule = selected.ChargeRule,
                    ReferenceNumber = selected.Reference,
                    Status = "Matched",
                    Reason = reason,
                    CreatedUtc = DateTime.UtcNow
                });
            }
        }

        var matchesToPersist = normalMatches.Concat(tallyMatches).ToArray();
        EnsureBankChargesLedger(clientId, financialYear, normalMatches);
        repository.ReplaceTransferMatches(clientId, financialYear, matchesToPersist);

        // LAST STEP for the ordinary Imported <-> Imported route: run Balance
        // Reconciliation only AFTER the proven Inter-Bank result has been applied. A Bank
        // account-wide opening/closing mismatch is a separate reconciliation issue; it must
        // never undo an otherwise exact Date/opposite-side/account/amount/reference match.
        // Record the final balance outcome on the completed match and leave Balance Snapshot /
        // Balance Review to surface any remaining account-level difference.
        var persistedRows = repository.GetTransactions(clientId, financialYear, includeArchived: true)
            .ToDictionary(row => row.Id);
        var finalBalanceEvidenceChanged = false;
        foreach (var match in normalMatches)
        {
            if (match.Reason.Contains("FINAL balance ", StringComparison.OrdinalIgnoreCase)) continue;
            if (!persistedRows.TryGetValue(match.SourceTransactionId, out var sourceRow) ||
                !persistedRows.TryGetValue(match.TargetTransactionId, out var targetRow))
            {
                match.Reason += "; FINAL balance review required: matched Bank Account rows could not be reloaded after apply";
                finalBalanceEvidenceChanged = true;
                continue;
            }

            var postApplyGate = VerifyFinalBalanceGate(
                clientId,
                financialYear,
                [sourceRow.BankAccountId, targetRow.BankAccountId]);
            match.Reason += postApplyGate.IsExact
                ? $"; FINAL balance verified: {postApplyGate.Detail}"
                : $"; FINAL balance review required: {postApplyGate.Detail}";
            finalBalanceEvidenceChanged = true;
        }

        if (finalBalanceEvidenceChanged)
            repository.ReplaceTransferMatches(clientId, financialYear, normalMatches.Concat(tallyMatches));

        return repository.GetTransferMatches(clientId, financialYear);
    }

    /// <summary>
    /// Final Inter-Bank accounting control. Statement arithmetic is verified from the
    /// immutable source rows (including rows archived only because reconciliation removed
    /// an accounting duplicate), then the business-books opening convention is applied:
    /// positive = Dr, negative = Cr. The resulting closing must equal the exact source
    /// statement closing as the final account-level reconciliation result. A mismatch is
    /// surfaced for Balance Review and does not redefine an already-proven Inter-Bank pair.
    /// </summary>
    public BankBalanceGateResult VerifyFinalBalanceGate(
        long clientId,
        string financialYear,
        IEnumerable<long> bankAccountIds)
    {
        var ids = bankAccountIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return new BankBalanceGateResult { IsExact = false, Detail = "No affected Bank Account was available for final closing-balance verification." };

        var baseGate = new BankReconciliationService(repository)
            .VerifyExactClosingBalances(clientId, financialYear, ids);
        var accounts = repository.GetAccounts(clientId, financialYear).ToDictionary(item => item.Id);
        var statements = repository.GetStatements(clientId, financialYear);
        var allSourceRows = repository.GetTransactions(clientId, financialYear, includeArchived: true)
            .Where(row => !row.RecordStatus.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase))
            .Where(row => !row.RecordStatus.Equals("Generated Transfer Charge", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var masters = masterRepository.Get(clientId, financialYear, includeArchived: false)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var issues = new List<string>();
        var checkedAccounts = 0;
        var maximumDifference = baseGate.MaximumDifference;
        foreach (var bankAccountId in ids)
        {
            if (!accounts.TryGetValue(bankAccountId, out var account))
            {
                issues.Add($"Bank Account {bankAccountId} is missing.");
                continue;
            }
            var accountStatements = statements
                .Where(statement => statement.BankAccountId == bankAccountId)
                .OrderBy(statement => statement.PeriodFrom ?? statement.ImportedUtc)
                .ThenBy(statement => statement.ImportedUtc)
                .ThenBy(statement => statement.Id)
                .ToArray();
            if (accountStatements.Length == 0)
            {
                issues.Add($"{account.DisplayName}: no Bank Statement balance evidence.");
                continue;
            }

            var statementIds = accountStatements.Select(statement => statement.Id).ToHashSet();
            var rows = allSourceRows
                .Where(row => row.BankAccountId == bankAccountId && statementIds.Contains(row.StatementId))
                .ToArray();
            var authoritySnapshot = BankBalanceAuthority.BuildAccountSnapshot(bankAccountId, accountStatements, rows);
            var sourceOpening = authoritySnapshot.OpeningBalance;
            var sourceClosing = authoritySnapshot.ClosingBalance;
            if (!sourceOpening.HasValue || !sourceClosing.HasValue)
            {
                issues.Add($"{account.DisplayName}: opening/closing balance evidence is incomplete.");
                continue;
            }

            var masterKeys = new[] { account.AuthorityName, account.LedgerName, account.DisplayName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(TallyMasterIdentity.Key)
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var exactAccountMasters = masters
                .Where(master => masterKeys.Contains(TallyMasterIdentity.Key(master.Name)))
                .ToArray();
            var accountDigits = new string((account.AccountNumber ?? string.Empty).Where(char.IsDigit).ToArray());
            var candidateMasters = exactAccountMasters.Length > 0
                ? exactAccountMasters
                : accountDigits.Length < 6
                    ? Array.Empty<MasterRecord>()
                    : masters.Where(master =>
                    {
                        var masterDigits = new string((master.Name ?? string.Empty).Where(char.IsDigit).ToArray());
                        return masterDigits.Contains(accountDigits, StringComparison.Ordinal);
                    }).ToArray();
            var masterOpening = candidateMasters
                .OrderByDescending(master => MasterAuthority.IsTallySource(master.Source))
                .ThenByDescending(master => master.OpeningBalance.HasValue)
                .ThenBy(master => master.PersistentId)
                .Select(master => master.OpeningBalance)
                .FirstOrDefault(value => value.HasValue);
            // A verified statement opening remains the fallback books opening when no
            // live/local Master opening has been captured yet. Both use business sign.
            var booksOpening = masterOpening ?? sourceOpening;
            var openingDifference = Math.Abs(Math.Round(booksOpening.Value - sourceOpening.Value, 2, MidpointRounding.AwayFromZero));

            var statementDebits = Math.Round(rows.Sum(row => row.Debit), 2, MidpointRounding.AwayFromZero);
            var statementCredits = Math.Round(rows.Sum(row => row.Credit), 2, MidpointRounding.AwayFromZero);
            var booksClosing = BankBalanceAuthority.CalculateClosingBalance(booksOpening.Value, statementDebits, statementCredits);
            var closingDifference = Math.Abs(Math.Round(booksClosing - sourceClosing.Value, 2, MidpointRounding.AwayFromZero));
            maximumDifference = Math.Max(maximumDifference, Math.Max(openingDifference, closingDifference));
            checkedAccounts++;

            if (openingDifference != 0m)
                issues.Add($"{account.DisplayName}: books opening {BankBusinessBalance.Format(booksOpening)} does not equal statement opening {BankBusinessBalance.Format(sourceOpening)} (difference {openingDifference:N2}).");
            if (closingDifference != 0m)
                issues.Add($"{account.DisplayName}: books closing {BankBusinessBalance.Format(booksClosing)} does not equal statement closing {BankBusinessBalance.Format(sourceClosing)} (difference {closingDifference:N2}).");
        }

        if (!baseGate.IsExact) issues.Add(baseGate.Detail);
        var exact = checkedAccounts == ids.Length && baseGate.IsExact && issues.Count == 0;
        return new BankBalanceGateResult
        {
            IsExact = exact,
            StatementsChecked = baseGate.StatementsChecked,
            StatementsMissingBalances = baseGate.StatementsMissingBalances,
            MaximumDifference = maximumDifference,
            Detail = exact
                ? $"Final business closing balance matched exactly for {checkedAccounts:N0} affected Bank Account(s)."
                : string.Join(" ", issues.Where(issue => !string.IsNullOrWhiteSpace(issue)).Distinct())
        };
    }

    private static bool IsTallyMatch(BankTransferMatch match) =>
        match.MatchOrigin.Equals("Tally Converted Bank Transaction", StringComparison.OrdinalIgnoreCase) ||
        match.MatchId.StartsWith("TALLYTRF|", StringComparison.OrdinalIgnoreCase);

    private static bool SameFinancialAccount(ReconciliationTransaction first, ReconciliationTransaction second)
    {
        if (first.Imported is not null && second.Imported is not null)
            return first.Imported.BankAccountId == second.Imported.BankAccountId;
        if (first.Tally is not null && second.Tally is not null)
        {
            var firstMasterId = first.Tally.BankLedgerMasterId.Trim();
            var secondMasterId = second.Tally.BankLedgerMasterId.Trim();
            if (firstMasterId.Length > 0 || secondMasterId.Length > 0)
                return firstMasterId.Length > 0 && secondMasterId.Length > 0 &&
                       firstMasterId.Equals(secondMasterId, StringComparison.OrdinalIgnoreCase);
            return NormalizeLedger(first.Tally.BankLedgerName).Equals(NormalizeLedger(second.Tally.BankLedgerName), StringComparison.OrdinalIgnoreCase);
        }

        var imported = first.Imported ?? second.Imported;
        var tally = first.Tally ?? second.Tally;
        var importedAccount = first.Imported is not null ? first.KnownAccount : second.KnownAccount;
        return imported is not null && tally is not null && importedAccount is not null &&
               BankAccountMatchesTallyLedger(importedAccount, tally);
    }

    private static bool BankAccountMatchesTallyLedger(BankAccountRecord account, TallyConvertedBankTransaction tally)
    {
        var accountMasterId = account.TallyLedgerMasterId.Trim();
        var tallyMasterId = tally.BankLedgerMasterId.Trim();
        if (accountMasterId.Length > 0 || tallyMasterId.Length > 0)
            return accountMasterId.Length > 0 && tallyMasterId.Length > 0 &&
                   accountMasterId.Equals(tallyMasterId, StringComparison.OrdinalIgnoreCase);

        // Name/account-number matching is first-binding fallback only. If either side is
        // already MASTERID-bound, a missing ID on the other side is incomplete authority,
        // not permission to fall back to a mutable Ledger label.
        var tallyLedger = tally.BankLedgerName;
        var tallyKey = NormalizeLedger(tallyLedger);
        if (tallyKey.Length == 0) return false;
        foreach (var candidate in new[] { account.AuthorityName, account.LedgerName, account.DisplayName })
        {
            var key = NormalizeLedger(candidate);
            if (key.Length > 0 && key.Equals(tallyKey, StringComparison.OrdinalIgnoreCase)) return true;
        }

        var accountDigits = new string(account.AccountNumber.Where(char.IsDigit).ToArray());
        if (accountDigits.Length >= 6)
        {
            var tallyDigits = new string((tallyLedger ?? string.Empty).Where(char.IsDigit).ToArray());
            if (tallyDigits.Contains(accountDigits, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string NormalizeLedger(string? value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record ReconciliationTransaction(
        string Key,
        BankTransactionRecord? Imported,
        TallyConvertedBankTransaction? Tally,
        BankAccountRecord? KnownAccount,
        DateTime TransactionDate,
        string Narration,
        string ReferenceNumber,
        decimal Debit,
        decimal Credit,
        string DisplayName)
    {
        public bool IsTally => Tally is not null;
        public decimal Amount => Debit > 0m ? Debit : Credit;

        public static ReconciliationTransaction FromImported(BankTransactionRecord row, BankAccountRecord account) => new(
            $"BANK|{row.Id}", row, null, account, row.TransactionDate.Date,
            row.Narration ?? string.Empty, row.ReferenceNumber ?? string.Empty,
            row.Debit, row.Credit, account.DisplayName);

        public static ReconciliationTransaction FromTally(TallyConvertedBankTransaction row, BankAccountRecord? knownAccount) => new(
            $"TALLY|{row.ConversionId}", null, row, knownAccount, row.TransactionDate.Date,
            row.Narration ?? string.Empty, row.ReferenceNumber ?? string.Empty,
            row.Debit, row.Credit, row.BankLedgerName);
    }

    private sealed record Candidate(
        ReconciliationTransaction Target,
        string Reference,
        decimal ChargeAmount,
        string ChargeRule);

    public BankTransferMatch ManualLink(
        long clientId,
        string financialYear,
        BankTransactionRecord first,
        BankTransactionRecord second)
    {
        if (first.Id <= 0 || second.Id <= 0 || first.Id == second.Id)
            throw new InvalidOperationException("Select exactly two different Bank Statement transactions.");
        if (first.BankAccountId == second.BankAccountId)
            throw new InvalidOperationException("Manual inter-bank linking requires two different bank accounts.");
        if (first.TransactionDate.Date != second.TransactionDate.Date)
            throw new InvalidOperationException("Manual inter-bank linking requires the same transaction date.");

        var source = first.Debit > 0 && second.Credit > 0 ? first :
                     second.Debit > 0 && first.Credit > 0 ? second : null;
        var target = ReferenceEquals(source, first) ? second :
                     ReferenceEquals(source, second) ? first : null;
        if (source is null || target is null)
            throw new InvalidOperationException("Select one debit/payment entry and one credit/receipt entry.");

        if (Math.Abs(source.Debit - target.Credit) > Tolerance)
            throw new InvalidOperationException(
                $"Manual inter-bank linking requires the same amount. Selected debit {source.Debit:N2} and credit {target.Credit:N2}.");

        var accounts = repository.GetAccounts(clientId, financialYear).ToDictionary(item => item.Id);
        if (!accounts.TryGetValue(source.BankAccountId, out var sourceAccount) ||
            !accounts.TryGetValue(target.BankAccountId, out var targetAccount))
            throw new InvalidOperationException("One of the selected bank accounts could not be resolved.");

        // Manual mode intentionally ignores narration/reference matching. Date,
        // direction, amount and different-account checks above are the authority.
        // Balance Reconciliation remains the LAST step and never vetoes a proven manual pair.

        var existing = repository.GetTransferMatches(clientId, financialYear)
            .Where(item => item.SourceTransactionId != source.Id &&
                           item.TargetTransactionId != source.Id &&
                           item.SourceTransactionId != target.Id &&
                           item.TargetTransactionId != target.Id)
            .ToList();
        var match = new BankTransferMatch
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            SourceTransactionId = source.Id,
            TargetTransactionId = target.Id,
            MatchId = $"MTRF-{source.TransactionDate:yyyyMMdd}-{source.Id}-{target.Id}",
            MatchDate = source.TransactionDate.Date,
            Amount = target.Credit,
            ChargeAmount = 0m,
            ChargeRule = "MANUAL",
            ReferenceNumber = string.Empty,
            Status = "Matched",
            Reason = $"MANUAL: same date, exact amount, different bank accounts; narration ignored: {sourceAccount.DisplayName} → {targetAccount.DisplayName}",
            CreatedUtc = DateTime.UtcNow
        };
        existing.Add(match);
        EnsureBankChargesLedger(clientId, financialYear, existing);
        repository.ReplaceTransferMatches(clientId, financialYear, existing);

        // Manual is the second Imported <-> Imported route. Balance Reconciliation is
        // deliberately repeated AFTER persistence so it is literally the last step. A
        // remaining account-level difference is recorded for review; the proven manual
        // transaction match itself is not rolled back.
        var postApplyBalanceGate = VerifyFinalBalanceGate(
            clientId,
            financialYear,
            [source.BankAccountId, target.BankAccountId]);
        match.Reason += postApplyBalanceGate.IsExact
            ? $"; FINAL balance verified: {postApplyBalanceGate.Detail}"
            : $"; FINAL balance review required: {postApplyBalanceGate.Detail}";
        repository.ReplaceTransferMatches(clientId, financialYear, existing);

        return repository.GetTransferMatches(clientId, financialYear)
            .First(item => item.MatchId.Equals(match.MatchId, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureBankChargesLedger(long clientId, string financialYear, IReadOnlyCollection<BankTransferMatch> matches)
    {
        if (!matches.Any(item => item.ChargeAmount > Tolerance)) return;
        masterRepository.Upsert(clientId, financialYear,
        [
            new MasterRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                Name = "Bank Charges",
                Parent = "Indirect Expenses",
                Type = "Ledger",
                Source = "Bank Transfer Reconciliation"
            }
        ]);
    }

    private static string SelectSharedReference(
        IReadOnlySet<string> sourceReferences,
        IReadOnlySet<string> targetReferences,
        string method)
    {
        var shared = sourceReferences.Intersect(targetReferences, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => ReferencePriority(value, method))
            .ThenByDescending(value => value.Length)
            .ToList();
        return shared.Count > 0 ? shared[0] : string.Empty;
    }

    private static int ReferencePriority(string value, string method)
    {
        var score = value.Length;
        if (value.Any(char.IsLetter) && value.Any(char.IsDigit)) score += 30;
        if (method.Equals("UPI", StringComparison.OrdinalIgnoreCase) && value.Length == 12 && value.All(char.IsDigit)) score += 40;
        if (method is "NEFT" or "RTGS" && value.Length >= 12) score += 40;
        return score;
    }

}