using System.Diagnostics;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Infrastructure.Banking;

/// <summary>
/// Builds the authority-holding reports using the exact worksheet names and
/// column order used by Import_1_6(29).xlsm. The Bank Statement workspace
/// remains the seven-column accounting view; source evidence and system fields
/// are exposed through these reports instead of being mixed into that grid.
/// </summary>
public sealed class BankingWorkbookReportService(
    IBankingRepository repository,
    SqliteMasterRepository masterRepository)
{
    private const decimal Tolerance = 0.01m;

    public BankingWorkbookReports Build(long clientId, string financialYear, string clientName)
    {
        var reports = new BankingWorkbookReports();
        if (clientId <= 0) return reports;

        var accounts = repository.GetAccounts(clientId, financialYear).ToDictionary(item => item.Id);
        var statements = repository.GetStatements(clientId, financialYear)
            .OrderBy(item => item.BankAccountId)
            .ThenBy(item => item.PeriodFrom ?? DateTime.MinValue)
            .ThenBy(item => item.Id)
            .ToList();
        var transactions = repository.GetTransactions(clientId, financialYear, true)
            .OrderBy(item => item.TransactionDate.Date)
            .ThenBy(item => item.ValueDate?.Date ?? item.TransactionDate.Date)
            .ThenBy(item => item.SourceRow)
            .ThenBy(item => item.ReferenceNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .ToList();
        var matches = repository.GetTransferMatches(clientId, financialYear)
            .OrderBy(item => item.MatchDate)
            .ThenBy(item => item.MatchId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var reconciliation = repository.GetReconciliation(clientId, financialYear)
            .ToDictionary(item => item.StatementId);
        var importAttempts = repository.GetStatementImportAttempts(clientId, financialYear)
            .OrderBy(item => item.ImportedUtc)
            .ThenBy(item => item.Id)
            .ToList();
        var statementBalances = BuildResolvedStatementBalanceMap(statements, transactions);
        var (coverageFrom, coverageTo) = ResolveCoverageWindow(financialYear);
        var accountSnapshots = accounts.Keys.ToDictionary(
            id => id,
            id => BankBalanceAuthority.BuildAccountSnapshot(id, statements, transactions, coverageFrom, coverageTo));

        var postImportTimings = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var stageWatch = Stopwatch.StartNew();
        PopulateStatementMetadata(reports, statements, transactions, importAttempts, accounts, reconciliation, clientName);
        postImportTimings["Reconciliation"] = stageWatch.Elapsed.TotalSeconds;
        postImportTimings["OB Suggestions"] = postImportTimings["Reconciliation"];

        stageWatch.Restart();
        var registryRows = PopulateTransactionRegistry(reports, transactions, accounts, matches);
        postImportTimings["Registry"] = stageWatch.Elapsed.TotalSeconds;

        stageWatch.Restart();
        PopulateTransferMap(reports, matches, transactions, accounts, registryRows);
        PopulateContraDebug(reports, transactions, accounts, matches, registryRows);
        PopulateRegistryReport(reports, transactions, statements, matches);
        PopulateImportLog(reports, statements, importAttempts, accounts);
        postImportTimings["Audit Reports"] = stageWatch.Elapsed.TotalSeconds;

        stageWatch.Restart();
        PopulateChainReport(reports, statements, transactions, accounts, statementBalances, accountSnapshots);
        postImportTimings["Chain Report"] = stageWatch.Elapsed.TotalSeconds;

        stageWatch.Restart();
        PopulateDuplicateReport(reports, transactions, accounts);
        postImportTimings["Duplicate"] = stageWatch.Elapsed.TotalSeconds;

        stageWatch.Restart();
        PopulateIntegrityReport(reports, transactions, statements, accounts, matches);
        postImportTimings["Integrity"] = stageWatch.Elapsed.TotalSeconds;

        PopulateImportPerformanceLog(reports, statements, postImportTimings);
        PopulateCashEntryRegistry(reports, clientId, financialYear);
        ApplyBalanceAuthorityProjection(reports, statements, transactions, accounts, statementBalances, accountSnapshots);
        PopulateBalanceAuthorityReport(reports, statements, accounts, accountSnapshots);

        return reports;
    }

    private static void PopulateStatementMetadata(
        BankingWorkbookReports reports,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyList<BankStatementImportAttemptRecord> importAttempts,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyDictionary<long, BankReconciliationSummary> reconciliation,
        string clientName)
    {
        foreach (var statement in statements)
        {
            accounts.TryGetValue(statement.BankAccountId, out var account);
            reconciliation.TryGetValue(statement.Id, out var recon);
            var verification = NormalizeVerificationStatus(statement.ReconciliationStatus);
            var transactionRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(statement, transactions);
            var notes = statement.Difference == 0m
                ? statement.ReconciliationStatus
                : $"{statement.ReconciliationStatus}; Difference {statement.Difference:0.00}";

            reports.StatementMetadata.Add(new StatementMetadataRow
            {
                StatementDatabaseId = statement.Id,
                BankAccountDatabaseId = statement.BankAccountId,
                FileName = statement.SourceFile,
                AccountNumber = account?.AuthorityName ?? string.Empty,
                AccountType = account?.AccountType ?? string.Empty,
                OpeningBalance = statement.OpeningBalance,
                ClosingBalance = statement.ClosingBalance,
                StartDate = transactionRange?.From ?? statement.PeriodFrom,
                EndDate = transactionRange?.To ?? statement.PeriodTo,
                Status = "SUCCESS",
                ObSource = statement.OpeningBalance.HasValue
                    ? (string.IsNullOrWhiteSpace(statement.OpeningBalanceSource) ? "RECONSTRUCTED" : statement.OpeningBalanceSource.ToUpperInvariant())
                    : "PENDING",
                SuggestedOb = recon?.SuggestedOpeningBalance,
                VerificationStatus = verification,
                TotalDebits = statement.TotalDebit,
                TotalCredits = statement.TotalCredit,
                ClientName = clientName,
                StatementId = $"STMT-{statement.Id:000000}",
                BankLedgerName = account?.LedgerName ?? string.Empty,
                BankName = account?.BankName ?? string.Empty,
                AccountCategory = account?.AccountType ?? string.Empty,
                BankId = statement.BankAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MetadataVersion = 1,
                LastUpdated = statement.ImportedUtc,
                Notes = notes
            });
        }

        // BR8 report parity: Statement Metadata is a physical-source registry, not merely a
        // projection of BankStatements. If older imports retained transactions from a workbook
        // whose registry row was lost/mis-linked, show that source file immediately instead of
        // silently reducing the visible statement count.
        foreach (var group in transactions
                     .Where(BankBalanceAuthority.IsSourceStatementTransaction)
                     .Where(row => row.BankAccountId > 0 && !string.IsNullOrWhiteSpace(row.SourceFile))
                     .GroupBy(
                         row => BankBalanceAuthority.SourceFileKey(row.BankAccountId, row.SourceFile, row.SourceSheet),
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group =>
                     {
                         var sample = group.First();
                         var sourceFile = (sample.SourceFile ?? string.Empty).Trim();
                         var sourceSheet = (sample.SourceSheet ?? string.Empty).Trim();
                         return !statements.Any(statement =>
                             statement.BankAccountId == sample.BankAccountId &&
                             string.Equals((statement.SourceFile ?? string.Empty).Trim(), sourceFile, StringComparison.OrdinalIgnoreCase) &&
                             (sourceSheet.Length == 0 ||
                              string.IsNullOrWhiteSpace(statement.SourceSheet) ||
                              string.Equals((statement.SourceSheet ?? string.Empty).Trim(), sourceSheet, StringComparison.OrdinalIgnoreCase)));
                     })
                     .OrderBy(group => group.Min(row => row.TransactionDate))
                     .ThenBy(group => group.First().SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToArray();
            var sample = rows[0];
            accounts.TryGetValue(sample.BankAccountId, out var account);
            var coverage = BankBalanceAuthority.InferTransactionFileCoverage(rows);
            reports.StatementMetadata.Add(new StatementMetadataRow
            {
                StatementDatabaseId = 0,
                BankAccountDatabaseId = sample.BankAccountId,
                FileName = sample.SourceFile,
                AccountNumber = account?.AuthorityName ?? string.Empty,
                AccountType = account?.AccountType ?? string.Empty,
                OpeningBalance = null,
                ClosingBalance = null,
                CalculatedClosingBalance = null,
                BalanceDifference = null,
                BalanceStatus = "Registry Recovered / Balance Evidence Pending",
                StartDate = coverage?.From,
                EndDate = coverage?.To,
                Status = "RECOVERED FROM IMPORTED TRANSACTIONS",
                ObSource = "PENDING",
                CbSource = "PENDING",
                VerificationStatus = "PENDING",
                TotalDebits = rows.Sum(row => row.Debit),
                TotalCredits = rows.Sum(row => row.Credit),
                ClientName = clientName,
                StatementId = $"TXNFILE-{unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(group.Key)):X8}",
                BankLedgerName = account?.LedgerName ?? string.Empty,
                BankName = account?.BankName ?? string.Empty,
                AccountCategory = account?.AccountType ?? string.Empty,
                BankId = sample.BankAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MetadataVersion = 2,
                LastUpdated = rows.Max(row => row.ImportedUtc),
                Notes = "Imported Bank Statement transactions exist for this source file, but the historical BankStatements registry row is missing or mis-linked. BR8 registry self-repair will recreate source authority without duplicating transactions."
            });
        }

        // BR9: retain every duplicate file import attempt in Statement Metadata. Duplicate
        // accounting rows are still not inserted, but the physical source file/audit event is
        // never discarded from the metadata registry.
        foreach (var attempt in importAttempts.Where(item => item.Status.Equals("DUPLICATE", StringComparison.OrdinalIgnoreCase)))
        {
            BankAccountRecord? account = null;
            if (attempt.BankAccountId > 0) accounts.TryGetValue(attempt.BankAccountId, out account);
            BankStatementRecord? duplicateOf = null;
            if (attempt.DuplicateOfStatementId.HasValue)
                duplicateOf = statements.FirstOrDefault(statement => statement.Id == attempt.DuplicateOfStatementId.Value);
            if (account is null && duplicateOf is not null) accounts.TryGetValue(duplicateOf.BankAccountId, out account);

            reports.StatementMetadata.Add(new StatementMetadataRow
            {
                StatementDatabaseId = 0,
                BankAccountDatabaseId = account?.Id ?? attempt.BankAccountId,
                FileName = attempt.SourceFile,
                AccountNumber = account?.AuthorityName ?? string.Empty,
                AccountType = account?.AccountType ?? string.Empty,
                OpeningBalance = attempt.OpeningBalance ?? duplicateOf?.OpeningBalance,
                ClosingBalance = attempt.ClosingBalance ?? duplicateOf?.ClosingBalance,
                StartDate = attempt.PeriodFrom ?? duplicateOf?.PeriodFrom,
                EndDate = attempt.PeriodTo ?? duplicateOf?.PeriodTo,
                Status = "DUPLICATE",
                ObSource = duplicateOf?.OpeningBalanceSource ?? string.Empty,
                CbSource = duplicateOf?.ClosingBalanceSource ?? string.Empty,
                VerificationStatus = "DUPLICATE - NOT REPOSTED",
                TotalDebits = attempt.TotalDebit,
                TotalCredits = attempt.TotalCredit,
                ClientName = clientName,
                StatementId = attempt.DuplicateOfStatementId.HasValue ? $"DUP-OF-STMT-{attempt.DuplicateOfStatementId.Value:000000}" : $"DUP-{attempt.Id:000000}",
                BankLedgerName = account?.LedgerName ?? string.Empty,
                BankName = account?.BankName ?? string.Empty,
                AccountCategory = account?.AccountType ?? string.Empty,
                BankId = (account?.Id ?? attempt.BankAccountId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                MetadataVersion = 3,
                LastUpdated = attempt.ImportedUtc,
                Notes = attempt.Notes.Length > 0 ? attempt.Notes : "Duplicate source retained for audit; accounting transactions were not inserted again."
            });
        }

        reports.StatementMetadata.Sort((left, right) =>
        {
            var accountCompare = left.BankAccountDatabaseId.CompareTo(right.BankAccountDatabaseId);
            if (accountCompare != 0) return accountCompare;
            var dateCompare = Nullable.Compare(left.StartDate, right.StartDate);
            if (dateCompare != 0) return dateCompare;
            return string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static Dictionary<long, TransactionRegistryRow> PopulateTransactionRegistry(
        BankingWorkbookReports reports,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyList<BankTransferMatch> matches)
    {
        var transactionById = transactions.ToDictionary(item => item.Id);
        var tallyMatchByImportedId = matches
            .Where(BankTransferWorkflowState.IsCompleted)
            .Where(IsTallyMatch)
            .GroupBy(item => item.SourceTransactionId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedUtc).First());
        var rows = new Dictionary<long, TransactionRegistryRow>();
        var currentRow = 2;

        foreach (var transaction in transactions)
        {
            accounts.TryGetValue(transaction.BankAccountId, out var account);
            var matchedTransactionId = string.Empty;
            if (transaction.MatchedTransactionId.HasValue &&
                transactionById.TryGetValue(transaction.MatchedTransactionId.Value, out var matched))
            {
                matchedTransactionId = matched.TransactionId;
            }
            else if (tallyMatchByImportedId.TryGetValue(transaction.Id, out var tallyMatch))
            {
                matchedTransactionId = tallyMatch.TallyConversionId;
            }

            var archived = transaction.RecordStatus.Contains("Archived", StringComparison.OrdinalIgnoreCase);
            var duplicate = transaction.RecordStatus.Contains("Duplicate", StringComparison.OrdinalIgnoreCase);
            var row = new TransactionRegistryRow
            {
                TransactionDatabaseId = transaction.Id,
                TransactionId = transaction.TransactionId,
                FileName = transaction.SourceFile,
                AccountNumber = account?.AuthorityName ?? transaction.BankAccountName,
                SheetRow = currentRow,
                TransactionDate = transaction.TransactionDate,
                Narration = transaction.Narration,
                Debit = transaction.Debit,
                Credit = transaction.Credit,
                Bank = account?.AuthorityName ?? transaction.BankAccountName,
                ImportStatus = duplicate ? "DUPLICATE" : "IMPORTED",
                DuplicateOf = duplicate ? transaction.Fingerprint : string.Empty,
                MatchStatus = transaction.MatchId.Length > 0 ? "MATCHED" : "PENDING",
                MatchedTxnId = matchedTransactionId,
                MatchId = transaction.MatchId,
                ArchiveReason = archived ? transaction.RecordStatus.ToUpperInvariant() : string.Empty,
                ArchivedOn = archived ? transaction.ImportedUtc : null,
                OriginalSheetRow = transaction.SourceRow,
                CurrentSheetRow = currentRow,
                BankStatementLink = transaction.SourceFile
            };
            reports.TransactionRegistry.Add(row);
            rows[transaction.Id] = row;
            currentRow++;
        }

        return rows;
    }

    private static void PopulateTransferMap(
        BankingWorkbookReports reports,
        IReadOnlyList<BankTransferMatch> matches,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyDictionary<long, TransactionRegistryRow> registryRows)
    {
        var transactionById = transactions.ToDictionary(item => item.Id);
        foreach (var match in matches.Where(BankTransferWorkflowState.IsCompleted))
        {
            if (IsTallyMatch(match))
            {
                if (!transactionById.TryGetValue(match.SourceTransactionId, out var imported)) continue;
                accounts.TryGetValue(imported.BankAccountId, out var importedAccount);
                registryRows.TryGetValue(imported.Id, out var importedRegistry);
                var tallyIsConvertedCredit = match.TallyIsDebit;
                reports.TransferMap.Add(new TransferMapRow
                {
                    MatchId = match.MatchId,
                    SourceTxnId = tallyIsConvertedCredit ? imported.TransactionId : match.TallyConversionId,
                    TargetTxnId = tallyIsConvertedCredit ? match.TallyConversionId : imported.TransactionId,
                    SourceAccount = tallyIsConvertedCredit ? (importedAccount?.AuthorityName ?? imported.BankAccountName) : match.TallyBankLedger,
                    TargetAccount = tallyIsConvertedCredit ? match.TallyBankLedger : (importedAccount?.AuthorityName ?? imported.BankAccountName),
                    Amount = match.Amount,
                    Status = match.Status.ToUpperInvariant(),
                    CreatedOn = match.CreatedUtc,
                    SourceRow = tallyIsConvertedCredit ? (importedRegistry?.CurrentSheetRow ?? imported.SourceRow) : 0,
                    TargetRow = tallyIsConvertedCredit ? 0 : (importedRegistry?.CurrentSheetRow ?? imported.SourceRow),
                    Reference = match.ReferenceNumber,
                    MatchMethod = "EXACT DATE + AMOUNT + REFERENCE · TALLY AUTHORITY",
                    SourceLink = tallyIsConvertedCredit ? imported.SourceFile : BuildTallySourceLink(match),
                    TargetLink = tallyIsConvertedCredit ? BuildTallySourceLink(match) : imported.SourceFile,
                    LastUpdated = match.CreatedUtc,
                    LastUpdated2 = match.CreatedUtc
                });
                continue;
            }
            if (!transactionById.TryGetValue(match.SourceTransactionId, out var source) ||
                !transactionById.TryGetValue(match.TargetTransactionId, out var target)) continue;
            accounts.TryGetValue(source.BankAccountId, out var sourceAccount);
            accounts.TryGetValue(target.BankAccountId, out var targetAccount);
            registryRows.TryGetValue(source.Id, out var sourceRegistry);
            registryRows.TryGetValue(target.Id, out var targetRegistry);

            reports.TransferMap.Add(new TransferMapRow
            {
                MatchId = match.MatchId,
                SourceTxnId = source.TransactionId,
                TargetTxnId = target.TransactionId,
                SourceAccount = sourceAccount?.AuthorityName ?? source.BankAccountName,
                TargetAccount = targetAccount?.AuthorityName ?? target.BankAccountName,
                Amount = match.Amount,
                Status = match.Status.ToUpperInvariant(),
                CreatedOn = match.CreatedUtc,
                SourceRow = sourceRegistry?.CurrentSheetRow ?? source.SourceRow,
                TargetRow = targetRegistry?.CurrentSheetRow ?? target.SourceRow,
                Reference = match.ReferenceNumber,
                MatchMethod = match.ChargeAmount > Tolerance
                    ? $"EXACT REFERENCE + {match.ChargeRule}"
                    : "EXACT DATE + AMOUNT + REFERENCE",
                SourceLink = source.SourceFile,
                TargetLink = target.SourceFile,
                LastUpdated = match.CreatedUtc,
                LastUpdated2 = match.CreatedUtc
            });
        }
    }

    private static void PopulateContraDebug(
        BankingWorkbookReports reports,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyList<BankTransferMatch> matches,
        IReadOnlyDictionary<long, TransactionRegistryRow> registryRows)
    {
        var checkedOn = transactions.Count == 0
            ? DateTime.UtcNow
            : transactions.Max(item => item.ImportedUtc == default ? DateTime.UtcNow : item.ImportedUtc);
        var runId = AppTime.ToLocal(checkedOn).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var tallyMatches = matches.Where(IsTallyMatch).ToArray();
        var tallyMatchedImportedIds = tallyMatches.Select(item => item.SourceTransactionId).ToHashSet();
        var matchBySource = matches.Where(item => !IsTallyMatch(item)).ToDictionary(item => item.SourceTransactionId);
        var transactionById = transactions.ToDictionary(item => item.Id);

        reports.ContraDebug.Add(new ContraDebugRow
        {
            RunId = runId,
            Result = "RUN START",
            Reason = "REGISTRY-FIRST DATE-GROUP STAGED RECONCILIATION STARTED",
            CheckedOn = checkedOn
        });

        foreach (var match in tallyMatches.OrderBy(item => item.MatchDate).ThenBy(item => item.MatchId, StringComparer.OrdinalIgnoreCase))
        {
            if (!transactionById.TryGetValue(match.SourceTransactionId, out var imported)) continue;
            accounts.TryGetValue(imported.BankAccountId, out var importedAccount);
            registryRows.TryGetValue(imported.Id, out var importedRegistry);
            var importedMethod = BankNarrationParser.DetectExplicitMethod(imported.Narration);
            var tallyMethod = BankNarrationParser.DetectExplicitMethod(match.TallyNarration);
            var importedReferences = BankNarrationParser.ExtractReferenceGroups(CombineReference(imported), importedMethod, Math.Max(imported.Debit, imported.Credit), imported.TransactionDate);
            var tallyReferenceText = match.TallyReference.Length > 0 ? match.TallyReference + " " + match.TallyNarration : match.TallyNarration;
            var tallyReferences = BankNarrationParser.ExtractReferenceGroups(tallyReferenceText, tallyMethod, match.Amount, match.MatchDate);
            var tallyIsConvertedCredit = match.TallyIsDebit;
            reports.ContraDebug.Add(new ContraDebugRow
            {
                RunId = runId,
                SourceRegistryRow = tallyIsConvertedCredit ? (importedRegistry?.CurrentSheetRow ?? 0) : 0,
                TargetRegistryRow = tallyIsConvertedCredit ? 0 : (importedRegistry?.CurrentSheetRow ?? 0),
                SourceSheetRow = tallyIsConvertedCredit ? (importedRegistry?.CurrentSheetRow ?? imported.SourceRow) : 0,
                TargetSheetRow = tallyIsConvertedCredit ? 0 : (importedRegistry?.CurrentSheetRow ?? imported.SourceRow),
                Date = match.MatchDate,
                Amount = match.Amount,
                SourceBank = tallyIsConvertedCredit ? (importedAccount?.AuthorityName ?? imported.BankAccountName) : match.TallyBankLedger,
                TargetBank = tallyIsConvertedCredit ? match.TallyBankLedger : (importedAccount?.AuthorityName ?? imported.BankAccountName),
                SourceMethod = tallyIsConvertedCredit ? importedMethod : tallyMethod,
                TargetMethod = tallyIsConvertedCredit ? tallyMethod : importedMethod,
                SourceReferenceGroups = string.Join(" | ", (tallyIsConvertedCredit ? importedReferences : tallyReferences).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                TargetReferenceGroups = string.Join(" | ", (tallyIsConvertedCredit ? tallyReferences : importedReferences).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                MatchedReference = match.ReferenceNumber,
                SourceTxn = tallyIsConvertedCredit ? imported.TransactionId : match.TallyConversionId,
                TargetTxn = tallyIsConvertedCredit ? match.TallyConversionId : imported.TransactionId,
                Result = BankTransferWorkflowState.ContraDebugStage(match),
                Reason = $"{match.Status}: {match.Reason}",
                SourceNarration = tallyIsConvertedCredit ? imported.Narration : match.TallyNarration,
                TargetNarration = tallyIsConvertedCredit ? match.TallyNarration : imported.Narration,
                CheckedOn = match.CreatedUtc == default ? checkedOn : match.CreatedUtc
            });
        }

        foreach (var dateGroup in transactions
                     .Where(item => item.Debit > 0m || item.Credit > 0m)
                     .GroupBy(item => item.TransactionDate.Date)
                     .OrderBy(group => group.Key))
        {
            // A persisted Tally workflow row already has its own dedicated staged debug row above.
            // Remove that imported source from the ordinary imported-vs-imported debug pool on
            // BOTH debit and credit sides so it cannot appear again as a false candidate/reject.
            var groupRows = dateGroup.Where(item => !tallyMatchedImportedIds.Contains(item.Id)).ToList();
            if (groupRows.Count == 0) continue;
            var accountCount = groupRows.Select(item => item.BankAccountId).Distinct().Count();
            var debitRows = groupRows.Where(item => item.Debit > 0m).ToList();
            var creditRows = groupRows.Where(item => item.Credit > 0m).ToList();
            var accountRejects = 0;
            var amountRejects = 0;
            var referenceSources = 0;
            var detailRows = new List<ContraDebugRow>();

            foreach (var source in debitRows)
            {
                accounts.TryGetValue(source.BankAccountId, out var sourceAccount);
                var sourceMethod = BankNarrationParser.DetectExplicitMethod(source.Narration);
                var sourceReferences = BankNarrationParser.ExtractReferenceGroups(
                    CombineReference(source), sourceMethod, source.Debit, source.TransactionDate);
                if (sourceReferences.Count > 0) referenceSources++;

                var differentAccountCandidates = creditRows
                    .Where(item => item.Id != source.Id && item.BankAccountId != source.BankAccountId)
                    .ToList();
                if (differentAccountCandidates.Count == 0) accountRejects++;

                var accepted = new List<(BankTransactionRecord Target, string TargetMethod, IReadOnlySet<string> TargetReferences, string SharedReference)>();
                foreach (var target in differentAccountCandidates)
                {
                    var targetMethod = BankNarrationParser.DetectExplicitMethod(target.Narration);
                    if (sourceMethod.Length > 0 && targetMethod.Length > 0 &&
                        !sourceMethod.Equals(targetMethod, StringComparison.OrdinalIgnoreCase)) continue;

                    var difference = Math.Round(source.Debit - target.Credit, 2);
                    var amountAccepted = Math.Abs(difference) <= Tolerance;
                    if (!amountAccepted)
                    {
                        var method = sourceMethod.Length > 0 ? sourceMethod : targetMethod;
                        amountAccepted = sourceAccount is not null && difference > Tolerance &&
                            BankTransferChargeRules.TryMatch(
                                sourceAccount.BankName,
                                method,
                                target.Credit,
                                source.Narration,
                                difference,
                                out _,
                                out _);
                    }
                    if (!amountAccepted) continue;

                    var targetReferences = BankNarrationParser.ExtractReferenceGroups(
                        CombineReference(target), targetMethod, target.Credit, target.TransactionDate);
                    var shared = SelectSharedReference(sourceReferences, targetReferences);
                    if (shared.Length == 0) continue;
                    accepted.Add((target, targetMethod, targetReferences, shared));
                }

                if (differentAccountCandidates.Count > 0 && accepted.Count == 0) amountRejects++;
                matchBySource.TryGetValue(source.Id, out var confirmedMatch);
                BankTransactionRecord? matchedTarget = null;
                if (confirmedMatch is not null)
                    transactionById.TryGetValue(confirmedMatch.TargetTransactionId, out matchedTarget);

                var selectedSharedReference = string.Empty;
                if (matchedTarget is not null)
                {
                    selectedSharedReference = accepted
                        .Where(item => item.Target.Id == matchedTarget.Id)
                        .Select(item => item.SharedReference)
                        .FirstOrDefault() ?? string.Empty;
                }
                else if (accepted.Count == 1)
                {
                    selectedSharedReference = accepted[0].SharedReference;
                }

                var result = "REJECTED";
                var reason = accountCount <= 1
                    ? "DATE GROUP HAS ONLY ONE BANK/ACCOUNT"
                    : differentAccountCandidates.Count == 0
                        ? "NO DIFFERENT BANK/ACCOUNT CREDIT CANDIDATE"
                        : accepted.Count == 0
                            ? "NO EXACT AMOUNT / METHOD / REFERENCE MATCH"
                            : accepted.Count > 1
                                ? "AMBIGUOUS EXACT CANDIDATES"
                                : "MATCH NOT COMMITTED";
                if (confirmedMatch is not null && matchedTarget is not null)
                {
                    result = "MATCHED";
                    reason = confirmedMatch.Reason;
                }

                registryRows.TryGetValue(source.Id, out var sourceRegistry);
                TransactionRegistryRow? targetRegistry = null;
                if (matchedTarget is not null) registryRows.TryGetValue(matchedTarget.Id, out targetRegistry);
                accounts.TryGetValue(matchedTarget?.BankAccountId ?? 0, out var targetAccountRecord);

                detailRows.Add(new ContraDebugRow
                {
                    RunId = runId,
                    SourceRegistryRow = sourceRegistry?.CurrentSheetRow ?? 0,
                    TargetRegistryRow = targetRegistry?.CurrentSheetRow ?? 0,
                    SourceSheetRow = sourceRegistry?.CurrentSheetRow ?? source.SourceRow,
                    TargetSheetRow = targetRegistry?.CurrentSheetRow ?? matchedTarget?.SourceRow ?? 0,
                    Date = source.TransactionDate,
                    Amount = matchedTarget?.Credit ?? source.Debit,
                    SourceBank = sourceAccount?.AuthorityName ?? source.BankAccountName,
                    TargetBank = targetAccountRecord?.AuthorityName ?? matchedTarget?.BankAccountName ?? string.Empty,
                    SourceMethod = sourceMethod,
                    TargetMethod = matchedTarget is null ? string.Empty : BankNarrationParser.DetectExplicitMethod(matchedTarget.Narration),
                    SourceReferenceGroups = string.Join(" | ", sourceReferences.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                    TargetReferenceGroups = matchedTarget is null
                        ? string.Empty
                        : string.Join(" | ", BankNarrationParser.ExtractReferenceGroups(
                            CombineReference(matchedTarget),
                            BankNarrationParser.DetectExplicitMethod(matchedTarget.Narration),
                            matchedTarget.Credit,
                            matchedTarget.TransactionDate).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                    MatchedReference = confirmedMatch?.ReferenceNumber ?? selectedSharedReference,
                    SourceTxn = source.TransactionId,
                    TargetTxn = matchedTarget?.TransactionId ?? string.Empty,
                    Result = result,
                    Reason = reason,
                    SourceNarration = source.Narration,
                    TargetNarration = matchedTarget?.Narration ?? string.Empty,
                    CheckedOn = checkedOn
                });
            }

            reports.ContraDebug.Add(new ContraDebugRow
            {
                RunId = runId,
                Date = dateGroup.Key,
                Result = "DATE GROUP",
                Reason = $"ROWS={groupRows.Count}; BANKS={accountCount}; DATE CHECK={(accountCount > 1 ? "PASSED" : "REJECTED")}; ACCOUNT REJECTS={accountRejects}; AMOUNT REJECTS={amountRejects}; REFERENCE SOURCES={referenceSources}",
                CheckedOn = checkedOn
            });
            reports.ContraDebug.AddRange(detailRows);
        }
    }

    private static void PopulateChainReport(
        BankingWorkbookReports reports,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyDictionary<long, ResolvedStatementBalance> statementBalances,
        IReadOnlyDictionary<long, BankAccountBalanceSnapshot> accountSnapshots)
    {
        foreach (var accountEntry in accounts.OrderBy(item => item.Value.AuthorityName, StringComparer.OrdinalIgnoreCase))
        {
            var accountId = accountEntry.Key;
            var account = accountEntry.Value;
            var ordered = statements
                .Where(item => item.BankAccountId == accountId)
                .Where(item => !BankBalanceAuthority.IsBalanceChainExcludedStatement(item))
                .OrderBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, transactions)?.From ?? item.PeriodFrom ?? DateTime.MinValue)
                .ThenBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, transactions)?.To ?? item.PeriodTo ?? DateTime.MinValue)
                .ThenBy(item => item.Id)
                .ToList();
            var failCount = 0;

            for (var index = 1; index < ordered.Count; index++)
            {
                var previous = ordered[index - 1];
                var next = ordered[index];
                statementBalances.TryGetValue(previous.Id, out var previousBalance);
                statementBalances.TryGetValue(next.Id, out var nextBalance);
                var previousCb = previousBalance?.ClosingBalance;
                var nextOb = nextBalance?.OpeningBalance;
                var difference = previousCb.HasValue && nextOb.HasValue
                    ? Math.Round(nextOb.Value - previousCb.Value, 2, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                var balanceStatus = difference.HasValue && difference.Value == 0m ? "PASS" : "FAIL";
                var previousRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(previous, transactions);
                var nextRange = BankBalanceAuthority.ResolveStatementTransactionDateRange(next, transactions);
                var gapCheck = BuildGapCheck(previousRange?.To ?? previous.PeriodTo, nextRange?.From ?? next.PeriodFrom);
                var coverageStatus = gapCheck.Equals("CONTINUOUS", StringComparison.OrdinalIgnoreCase) ? "PASS" : "FAIL";
                var chainStatus = balanceStatus == "PASS" && coverageStatus == "PASS" ? "PASS" : "FAIL";
                if (chainStatus == "FAIL") failCount++;
                reports.ChainReport.Add(new ChainReportRow
                {
                    Account = account.AuthorityName,
                    PreviousFile = previous.SourceFile,
                    PreviousEndDate = previousRange?.To ?? previous.PeriodTo,
                    NextFile = next.SourceFile,
                    NextStartDate = nextRange?.From ?? next.PeriodFrom,
                    PreviousCb = previousCb,
                    NextOb = nextOb,
                    Difference = difference,
                    Status = chainStatus,
                    GapCheck = !previousCb.HasValue || !nextOb.HasValue
                        ? $"{gapCheck}; MISSING BALANCE EVIDENCE"
                        : gapCheck
                });
            }

            if (!accountSnapshots.TryGetValue(accountId, out var accountSnapshot))
                accountSnapshot = BankBalanceAuthority.BuildAccountSnapshot(accountId, ordered, transactions);
            var missingBalanceEvidence = ordered.Count > 0 &&
                                         (!accountSnapshot.OpeningBalance.HasValue || !accountSnapshot.ClosingBalance.HasValue);
            if (missingBalanceEvidence) failCount++;
            var status = failCount == 0 &&
                         accountSnapshot.MissingPeriods.Count == 0 &&
                         accountSnapshot.OverlapPeriods.Count == 0 &&
                         accountSnapshot.Difference.GetValueOrDefault() == 0m &&
                         !missingBalanceEvidence
                ? "PASS"
                : "FAIL";
            reports.ChainAccountSummary.Add(new ChainAccountSummaryRow
            {
                Account = account.AuthorityName,
                OverallOb = accountSnapshot.OpeningBalance,
                OverallCb = accountSnapshot.ClosingBalance,
                CalculatedCb = accountSnapshot.CalculatedClosingBalance,
                Difference = accountSnapshot.Difference,
                MissingPeriods = accountSnapshot.MissingPeriodText,
                OverlapPeriods = string.Join("; ", accountSnapshot.OverlapPeriods.Select(range => range.Label)),
                Statements = ordered.Count,
                FailCount = failCount,
                Status = status
            });
        }

        if (reports.ChainReport.Count == 0)
            reports.ChainReport.Add(new ChainReportRow { Account = "NO COMPARABLE FULL-STATEMENT CHAINS FOUND" });
    }

    private static void PopulateDuplicateReport(
        BankingWorkbookReports reports,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyDictionary<long, BankAccountRecord> accounts)
    {
        foreach (var group in transactions
                     .Where(item => item.Fingerprint.Length > 0)
                     .GroupBy(item => item.Fingerprint, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var original = group.OrderBy(item => item.Id).First();
            foreach (var duplicate in group.OrderBy(item => item.Id).Skip(1))
            {
                accounts.TryGetValue(duplicate.BankAccountId, out var account);
                reports.DuplicateReport.Add(new DuplicateReportRow
                {
                    TransactionId = duplicate.TransactionId,
                    AccountNumber = account?.AuthorityName ?? duplicate.BankAccountName,
                    DuplicateOf = original.TransactionId,
                    Status = "DUPLICATE"
                });
            }
        }

        foreach (var duplicate in transactions.Where(item => item.RecordStatus.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)))
        {
            if (reports.DuplicateReport.Any(item => item.TransactionId.Equals(duplicate.TransactionId, StringComparison.OrdinalIgnoreCase))) continue;
            accounts.TryGetValue(duplicate.BankAccountId, out var account);
            reports.DuplicateReport.Add(new DuplicateReportRow
            {
                TransactionId = duplicate.TransactionId,
                AccountNumber = account?.AuthorityName ?? duplicate.BankAccountName,
                DuplicateOf = duplicate.Fingerprint,
                Status = "DUPLICATE"
            });
        }
    }

    private static void PopulateRegistryReport(
        BankingWorkbookReports reports,
        IReadOnlyCollection<BankTransactionRecord> transactions,
        IReadOnlyCollection<BankStatementRecord> statements,
        IReadOnlyCollection<BankTransferMatch> matches)
    {
        reports.RegistryReport.Add(new ReportMetricRow { Metric = "Registry Rows", Value = transactions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        reports.RegistryReport.Add(new ReportMetricRow { Metric = "Registry Exists", Value = "TRUE" });
        reports.RegistryReport.Add(new ReportMetricRow { Metric = "Transfer Registry Exists", Value = matches.Count > 0 ? "TRUE" : "FALSE" });
        reports.RegistryReport.Add(new ReportMetricRow { Metric = "Metadata Exists", Value = statements.Count > 0 ? "TRUE" : "FALSE" });
    }

    private static void PopulateIntegrityReport(
        BankingWorkbookReports reports,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyList<BankTransferMatch> matches)
    {
        var duplicateIds = transactions
            .Where(item => item.TransactionId.Length > 0)
            .GroupBy(item => item.TransactionId, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        var pendingOb = statements.Count(item =>
            !item.ReconciliationStatus.Equals("Reconciled", StringComparison.OrdinalIgnoreCase) &&
            !item.ReconciliationStatus.Equals("Skipped", StringComparison.OrdinalIgnoreCase));
        var missingAccounts = statements.Count(item => !accounts.ContainsKey(item.BankAccountId)) +
                              transactions.Count(item => !accounts.ContainsKey(item.BankAccountId));
        var transactionIds = transactions.Select(item => item.Id).ToHashSet();
        var brokenTransferLinks = matches.Count(item =>
            IsTallyMatch(item)
                ? !transactionIds.Contains(item.SourceTransactionId) || string.IsNullOrWhiteSpace(item.TallyConversionId)
                : !transactionIds.Contains(item.SourceTransactionId) || !transactionIds.Contains(item.TargetTransactionId));
        var registryHealthy = duplicateIds == 0 && missingAccounts == 0 && brokenTransferLinks == 0;

        reports.IntegrityReport.Add(new IntegrityReportRow { Check = "Duplicate Transaction IDs", Result = duplicateIds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        reports.IntegrityReport.Add(new IntegrityReportRow { Check = "Pending OB Verifications", Result = pendingOb.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        reports.IntegrityReport.Add(new IntegrityReportRow { Check = "Missing Metadata Accounts", Result = missingAccounts.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        reports.IntegrityReport.Add(new IntegrityReportRow { Check = "Broken Transfer Links", Result = brokenTransferLinks.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        reports.IntegrityReport.Add(new IntegrityReportRow { Check = "Registry Health", Result = registryHealthy ? "OK" : "CHECK" });
    }

    private static void PopulateImportLog(
        BankingWorkbookReports reports,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyList<BankStatementImportAttemptRecord> importAttempts,
        IReadOnlyDictionary<long, BankAccountRecord> accounts)
    {
        foreach (var statement in statements.OrderBy(item => item.ImportedUtc).ThenBy(item => item.Id))
        {
            accounts.TryGetValue(statement.BankAccountId, out var account);
            reports.ImportLog.Add(new ImportLogRow
            {
                StatementDatabaseId = statement.Id,
                Timestamp = statement.ImportedUtc,
                FileName = statement.SourceFile,
                BankName = account?.BankName ?? string.Empty,
                AccountNumber = account?.AccountNumber ?? string.Empty,
                RowsImported = statement.TransactionCount,
                Status = "SUCCESS",
                Notes = string.Join("; ", new[] { statement.SourceSheet, statement.ReconciliationStatus }.Where(value => !string.IsNullOrWhiteSpace(value)))
            });
        }
        foreach (var attempt in importAttempts.Where(item => item.Status.Equals("DUPLICATE", StringComparison.OrdinalIgnoreCase)))
        {
            accounts.TryGetValue(attempt.BankAccountId, out var account);
            reports.ImportLog.Add(new ImportLogRow
            {
                StatementDatabaseId = 0,
                Timestamp = attempt.ImportedUtc,
                FileName = attempt.SourceFile,
                BankName = account?.BankName ?? string.Empty,
                AccountNumber = account?.AccountNumber ?? string.Empty,
                RowsImported = 0,
                Status = "DUPLICATE",
                Notes = attempt.Notes
            });
        }
        reports.ImportLog.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
    }

    private static void PopulateImportPerformanceLog(
        BankingWorkbookReports reports,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyDictionary<string, double> postImportTimings)
    {
        if (statements.Count == 0) return;
        var ordered = statements.OrderBy(item => item.ImportedUtc).ThenBy(item => item.Id).ToList();
        var runStart = ordered[0].ImportedUtc;
        var runEnd = ordered[^1].ImportedUtc;
        reports.ImportPerformanceLog.Add(new ImportPerformanceLogRow { RecordType = "RUN_START", DateTime = runStart });
        foreach (var statement in ordered)
        {
            reports.ImportPerformanceLog.Add(new ImportPerformanceLogRow
            {
                RecordType = "FILE_START",
                DateTime = statement.ImportedUtc.AddSeconds(-(statement.OpenSeconds + statement.ProcessSeconds)),
                Name = statement.SourcePath.Length > 0 ? statement.SourcePath : statement.SourceFile
            });
            reports.ImportPerformanceLog.Add(new ImportPerformanceLogRow
            {
                RecordType = "FILE_END",
                DateTime = statement.ImportedUtc,
                Name = statement.SourceFile,
                Value1 = statement.OpenSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Value2 = statement.ProcessSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Value3 = (statement.OpenSeconds + statement.ProcessSeconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Notes = $"{statement.TransactionCount:N0} row(s); {statement.ReconciliationStatus}"
            });
        }

        foreach (var stage in new[] { "Registry", "Duplicate", "Reconciliation", "OB Suggestions", "Chain Report", "Audit Reports", "Integrity" })
        {
            postImportTimings.TryGetValue(stage, out var elapsed);
            reports.ImportPerformanceLog.Add(new ImportPerformanceLogRow
            {
                RecordType = "POST_IMPORT",
                DateTime = runEnd,
                Name = stage,
                Value1 = elapsed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Notes = "Measured persistent application stage"
            });
        }

        reports.ImportPerformanceLog.Add(new ImportPerformanceLogRow
        {
            RecordType = "RUN",
            DateTime = runEnd,
            Name = "IMPORT",
            Value1 = ordered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value2 = ordered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value3 = "0",
            Value4 = ordered.Sum(item => item.OpenSeconds + item.ProcessSeconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private void PopulateCashEntryRegistry(BankingWorkbookReports reports, long clientId, string financialYear)
    {
        var masters = masterRepository.Get(clientId, financialYear, false, string.Empty);
        var groups = masters.Where(item => item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var ledger in masters.Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isCashOrBank = ledger.Parent.Equals("Cash-in-hand", StringComparison.OrdinalIgnoreCase) ||
                               ledger.Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase) ||
                               ledger.Parent.Equals("Bank Accounts", StringComparison.OrdinalIgnoreCase);
            reports.CashEntryRegistry.Add(new CashEntryRegistryRow
            {
                MasterLedger = ledger.Name,
                MasterGroup = ledger.Parent,
                CashLedger = isCashOrBank ? ledger.Name : string.Empty
            });
        }
        foreach (var group in groups)
        {
            reports.CashEntryRegistry.Add(new CashEntryRegistryRow { ParentGroup = group });
        }
    }

    private static Dictionary<long, ResolvedStatementBalance> BuildResolvedStatementBalanceMap(
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyList<BankTransactionRecord> transactions)
    {
        var result = new Dictionary<long, ResolvedStatementBalance>();

        foreach (var accountGroup in statements.GroupBy(item => item.BankAccountId))
        {
            BankStatementRecord? previousComparable = null;
            decimal? previousComparableClosing = null;
            foreach (var statement in accountGroup
                         .OrderBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, transactions)?.From ?? item.PeriodFrom ?? item.ImportedUtc)
                         .ThenBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, transactions)?.To ?? item.PeriodTo ?? item.ImportedUtc)
                         .ThenBy(item => item.Id))
            {
                var statementRows = BankBalanceAuthority.GetStatementSourceTransactions(statement, transactions);
                var movementRows = BankBalanceAuthority.GetStatementPeriodTransactions(statement, transactions);
                var partialOrDelta = BankBalanceAuthority.IsPartialOrDeltaStatement(statement);
                var balanceChainExcluded = BankBalanceAuthority.IsBalanceChainExcludedStatement(statement);
                var chainClosing = !balanceChainExcluded && previousComparable is not null &&
                                   BankBalanceAuthority.CanCarryBalanceForward(previousComparable, statement, transactions)
                    ? previousComparableClosing
                    : null;
                var opening = BankBalanceAuthority.ResolveOpeningBalance(statement, statementRows, chainClosing, movementRows);
                var closing = BankBalanceAuthority.ResolveClosingBalance(statement, statementRows, opening.Value, movementRows);
                var difference = closing.IndependentValue.HasValue && closing.CalculatedValue.HasValue
                    ? Math.Round(closing.IndependentValue.Value - closing.CalculatedValue.Value, 2, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                var trustedOpening = BankBalanceAuthority.IsTrustedOpeningEvidence(opening);
                var closingNeedsReview = !closing.IndependentValue.HasValue ||
                                         closing.Source.Equals("CALCULATED", StringComparison.OrdinalIgnoreCase) ||
                                         closing.Source.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase);
                var status = !trustedOpening || !closing.Value.HasValue || closingNeedsReview
                    ? closing.Source.Equals("CALCULATED", StringComparison.OrdinalIgnoreCase)
                        ? "Missing Balance Evidence / Calculated Closing"
                        : "Missing Balance Evidence"
                    : difference.HasValue && difference.Value != 0m
                        ? "Balance Difference"
                        : "Reconciled";
                if (partialOrDelta) status = $"Partial / Delta - {status}";

                result[statement.Id] = new ResolvedStatementBalance(
                    opening.Value,
                    closing.Value,
                    closing.CalculatedValue,
                    difference,
                    opening.Source,
                    closing.Source,
                    status);

                if (!balanceChainExcluded)
                {
                    previousComparable = statement;
                    previousComparableClosing = closing.Value;
                }
            }
        }
        return result;
    }

    private static void ApplyBalanceAuthorityProjection(
        BankingWorkbookReports reports,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyDictionary<long, ResolvedStatementBalance> statementBalances,
        IReadOnlyDictionary<long, BankAccountBalanceSnapshot> accountSnapshots)
    {
        var statementById = statements.ToDictionary(item => item.Id);
        var transactionById = transactions.ToDictionary(item => item.Id);
        var snapshotByName = new Dictionary<string, BankAccountBalanceSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts.Values)
        {
            if (!accountSnapshots.TryGetValue(account.Id, out var snapshot)) continue;
            foreach (var name in new[] { account.AuthorityName, account.DisplayName, account.AccountNumber, account.LedgerName })
            {
                var key = (name ?? string.Empty).Trim();
                if (key.Length > 0) snapshotByName[key] = snapshot;
            }
        }

        foreach (var row in reports.StatementMetadata)
        {
            BankStatementRecord? statement = null;
            if (row.StatementDatabaseId > 0)
                statementById.TryGetValue(row.StatementDatabaseId, out statement);

            if (statement is not null && statementBalances.TryGetValue(statement.Id, out var balance))
            {
                var statementRows = BankBalanceAuthority.GetStatementPeriodTransactions(statement, transactions);
                row.TotalDebits = statementRows.Sum(item => item.Debit);
                row.TotalCredits = statementRows.Sum(item => item.Credit);
                row.OpeningBalance = balance.OpeningBalance;
                row.ClosingBalance = balance.ClosingBalance;
                row.CalculatedClosingBalance = balance.CalculatedClosingBalance;
                row.BalanceDifference = balance.Difference;
                row.BalanceStatus = balance.Status;
                row.ObSource = balance.OpeningSource;
                row.CbSource = balance.ClosingSource;
                row.VerificationStatus = balance.Status.Contains("Missing Balance Evidence", StringComparison.OrdinalIgnoreCase)
                    ? "MISSING EVIDENCE"
                    : balance.Status.Contains("Review Required", StringComparison.OrdinalIgnoreCase)
                        ? "PENDING"
                        : balance.Status.Contains("Difference", StringComparison.OrdinalIgnoreCase)
                            ? "PENDING"
                            : "VERIFIED";
            }

            var accountId = statement?.BankAccountId ?? row.BankAccountDatabaseId;
            if (accountSnapshots.TryGetValue(accountId, out var accountSnapshot))
            {
                // CR3: Statement Metadata is a source-file audit grid. Show only the account
                // balance-bridge/overlap ranges intersecting this statement instead of repeating
                // every account-wide Missing Period on every source-file row.
                row.MissingPeriods = FormatRangesForStatement(accountSnapshot.MissingPeriods, row.StartDate, row.EndDate);
                row.OverlapPeriods = FormatRangesForStatement(accountSnapshot.OverlapPeriods, row.StartDate, row.EndDate);
            }
        }

        foreach (var row in reports.TransactionRegistry)
        {
            if (!transactionById.TryGetValue(row.TransactionDatabaseId, out var transaction) ||
                !accountSnapshots.TryGetValue(transaction.BankAccountId, out var snapshot)) continue;
            row.AccountOpeningBalance = snapshot.OpeningBalance;
            row.AccountClosingBalance = snapshot.ClosingBalance;
            row.AccountBalanceStatus = snapshot.Status;
            row.MissingPeriods = snapshot.MissingPeriodText;
        }

        foreach (var row in reports.TransferMap)
        {
            if (snapshotByName.TryGetValue(row.SourceAccount.Trim(), out var sourceSnapshot))
            {
                row.SourceOpeningBalance = sourceSnapshot.OpeningBalance;
                row.SourceClosingBalance = sourceSnapshot.ClosingBalance;
            }
            if (snapshotByName.TryGetValue(row.TargetAccount.Trim(), out var targetSnapshot))
            {
                row.TargetOpeningBalance = targetSnapshot.OpeningBalance;
                row.TargetClosingBalance = targetSnapshot.ClosingBalance;
            }
        }

        foreach (var row in reports.ContraDebug)
        {
            if (snapshotByName.TryGetValue(row.SourceBank.Trim(), out var sourceSnapshot))
            {
                row.SourceOpeningBalance = sourceSnapshot.OpeningBalance;
                row.SourceClosingBalance = sourceSnapshot.ClosingBalance;
            }
            if (snapshotByName.TryGetValue(row.TargetBank.Trim(), out var targetSnapshot))
            {
                row.TargetOpeningBalance = targetSnapshot.OpeningBalance;
                row.TargetClosingBalance = targetSnapshot.ClosingBalance;
            }
        }

        foreach (var row in reports.DuplicateReport)
        {
            if (!snapshotByName.TryGetValue(row.AccountNumber.Trim(), out var snapshot)) continue;
            row.AccountOpeningBalance = snapshot.OpeningBalance;
            row.AccountClosingBalance = snapshot.ClosingBalance;
            row.AccountBalanceStatus = snapshot.Status;
        }

        var missingEvidenceAccounts = 0;
        foreach (var account in accounts.Values.OrderBy(item => item.AuthorityName, StringComparer.OrdinalIgnoreCase))
        {
            if (!accountSnapshots.TryGetValue(account.Id, out var snapshot)) continue;
            var comparableStatements = statements.Count(item => item.BankAccountId == account.Id && !BankBalanceAuthority.IsBalanceChainExcludedStatement(item));
            var missingEvidence = comparableStatements > 0 && snapshot.Status.Contains("Missing Balance Evidence", StringComparison.OrdinalIgnoreCase);
            if (missingEvidence) missingEvidenceAccounts++;
            reports.RegistryReport.Add(new ReportMetricRow { Metric = $"{account.AuthorityName} Opening Balance", Value = BankBusinessBalance.Format(snapshot.OpeningBalance) });
            reports.RegistryReport.Add(new ReportMetricRow { Metric = $"{account.AuthorityName} Closing Balance", Value = BankBusinessBalance.Format(snapshot.ClosingBalance) });
            reports.RegistryReport.Add(new ReportMetricRow { Metric = $"{account.AuthorityName} OB Source", Value = snapshot.OpeningBalanceSource.Length > 0 ? snapshot.OpeningBalanceSource : "MISSING EVIDENCE" });
            reports.RegistryReport.Add(new ReportMetricRow { Metric = $"{account.AuthorityName} CB Source", Value = snapshot.ClosingBalanceSource.Length > 0 ? snapshot.ClosingBalanceSource : "MISSING EVIDENCE" });
            reports.RegistryReport.Add(new ReportMetricRow { Metric = $"{account.AuthorityName} Missing Periods", Value = snapshot.MissingPeriodText.Length > 0 ? snapshot.MissingPeriodText : "NONE" });
            reports.RegistryReport.Add(new ReportMetricRow { Metric = $"{account.AuthorityName} Balance Status", Value = missingEvidence ? "Missing Balance Evidence" : snapshot.Status });
            reports.IntegrityReport.Add(new IntegrityReportRow
            {
                Check = $"Balance Authority - {account.AuthorityName}",
                Result = $"OB {BankBusinessBalance.Format(snapshot.OpeningBalance)} ({(snapshot.OpeningBalanceSource.Length > 0 ? snapshot.OpeningBalanceSource : "MISSING")}) | CB {BankBusinessBalance.Format(snapshot.ClosingBalance)} ({(snapshot.ClosingBalanceSource.Length > 0 ? snapshot.ClosingBalanceSource : "MISSING")}) | Calc {BankBusinessBalance.Format(snapshot.CalculatedClosingBalance)} | Diff {BankBusinessBalance.Format(snapshot.Difference)} | Missing {(snapshot.MissingPeriodText.Length > 0 ? snapshot.MissingPeriodText : "NONE")} | {(missingEvidence ? "Missing Balance Evidence" : snapshot.Status)}"
            });
        }
        reports.IntegrityReport.Add(new IntegrityReportRow { Check = "Balance Authority Missing Evidence Accounts", Result = missingEvidenceAccounts.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        foreach (var row in reports.ImportLog)
        {
            if (row.StatementDatabaseId <= 0 || !statementById.TryGetValue(row.StatementDatabaseId, out var statement)) continue;
            if (statementBalances.TryGetValue(statement.Id, out var balance))
            {
                row.OpeningBalance = balance.OpeningBalance;
                row.ClosingBalance = balance.ClosingBalance;
                row.BalanceStatus = balance.Status;
            }
            if (accountSnapshots.TryGetValue(statement.BankAccountId, out var snapshot))
                row.MissingPeriods = snapshot.MissingPeriodText;
        }
    }

    private static string FormatRangesForStatement(
        IEnumerable<BankDateRange> ranges,
        DateTime? statementFrom,
        DateTime? statementTo)
    {
        if (!statementFrom.HasValue || !statementTo.HasValue) return string.Empty;
        var from = statementFrom.Value.Date;
        var to = statementTo.Value.Date;
        if (to < from) (from, to) = (to, from);
        return string.Join("; ", ranges
            .Where(range => range.From <= to && range.To >= from)
            .Select(range => new BankDateRange(
                range.From < from ? from : range.From,
                range.To > to ? to : range.To).Label));
    }

    private static void PopulateBalanceAuthorityReport(
        BankingWorkbookReports reports,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyDictionary<long, BankAccountBalanceSnapshot> accountSnapshots)
    {
        foreach (var account in accounts.Values.OrderBy(item => item.AuthorityName, StringComparer.OrdinalIgnoreCase))
        {
            if (!accountSnapshots.TryGetValue(account.Id, out var snapshot)) continue;
            var statementCount = statements.Count(item => item.BankAccountId == account.Id && !BankBalanceAuthority.IsBalanceChainExcludedStatement(item));
            var missingEvidence = statementCount > 0 && snapshot.Status.Contains("Missing Balance Evidence", StringComparison.OrdinalIgnoreCase);
            reports.BalanceAuthority.Add(new BalanceAuthorityReportRow
            {
                Account = account.AuthorityName,
                OpeningBalance = snapshot.OpeningBalance,
                OpeningSource = snapshot.OpeningBalanceSource,
                ClosingBalance = snapshot.ClosingBalance,
                ClosingSource = snapshot.ClosingBalanceSource,
                CalculatedClosingBalance = snapshot.CalculatedClosingBalance,
                Difference = snapshot.Difference,
                TotalDebits = snapshot.TotalDebit,
                TotalCredits = snapshot.TotalCredit,
                StatementCount = statementCount,
                MissingPeriods = snapshot.MissingPeriodText,
                OverlapPeriods = string.Join("; ", snapshot.OverlapPeriods.Select(range => range.Label)),
                Status = missingEvidence ? "Missing Balance Evidence" : snapshot.Status
            });
        }
    }

    private static (DateTime From, DateTime To) ResolveCoverageWindow(string financialYear)
    {
        var digits = new string((financialYear ?? string.Empty).Where(char.IsDigit).ToArray());
        DateTime from;
        DateTime fyTo;
        if (digits.Length >= 4 && int.TryParse(digits[..4], out var startYear))
        {
            from = new DateTime(startYear, 4, 1);
            fyTo = new DateTime(startYear + 1, 3, 31);
        }
        else
        {
            var now = AppTime.LocalNow.Date;
            var start = now.Month >= 4 ? now.Year : now.Year - 1;
            from = new DateTime(start, 4, 1);
            fyTo = new DateTime(start + 1, 3, 31);
        }
        var today = AppTime.LocalNow.Date;
        var to = today < from ? from : today < fyTo ? today : fyTo;
        return (from, to);
    }

    private sealed record ResolvedStatementBalance(
        decimal? OpeningBalance,
        decimal? ClosingBalance,
        decimal? CalculatedClosingBalance,
        decimal? Difference,
        string OpeningSource,
        string ClosingSource,
        string Status);

    private static string NormalizeVerificationStatus(string status)
    {
        if (status.Equals("Reconciled", StringComparison.OrdinalIgnoreCase)) return "VERIFIED";
        if (status.Equals("Skipped", StringComparison.OrdinalIgnoreCase)) return "SKIPPED";
        if (status.Contains("Review", StringComparison.OrdinalIgnoreCase) || status.Equals("Difference", StringComparison.OrdinalIgnoreCase)) return "PENDING";
        return status.Trim().ToUpperInvariant();
    }

    private static string BuildGapCheck(DateTime? previousEnd, DateTime? nextStart)
    {
        if (!previousEnd.HasValue || !nextStart.HasValue) return "DATE MISSING";
        var missingFrom = previousEnd.Value.Date.AddDays(1);
        var missingTo = nextStart.Value.Date.AddDays(-1);
        if (missingFrom > missingTo)
        {
            if (nextStart.Value.Date == previousEnd.Value.Date.AddDays(1)) return "CONTINUOUS";
            var overlapTo = previousEnd.Value.Date < nextStart.Value.Date ? nextStart.Value.Date : previousEnd.Value.Date;
            return $"OVERLAP {nextStart.Value:dd-MMM-yyyy} to {overlapTo:dd-MMM-yyyy}";
        }
        return $"MISSING {missingFrom:dd-MMM-yyyy} to {missingTo:dd-MMM-yyyy}";
    }

    private static bool IsTallyMatch(BankTransferMatch match) =>
        match.MatchOrigin.Equals("Tally Converted Bank Transaction", StringComparison.OrdinalIgnoreCase) ||
        match.MatchId.StartsWith("TALLYTRF|", StringComparison.OrdinalIgnoreCase);

    private static string BuildTallySourceLink(BankTransferMatch match)
    {
        var voucher = string.Join(" ", new[] { match.TallyVoucherType, match.TallyVoucherNumber }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return voucher.Length == 0 ? "Live Tally" : $"Live Tally · {voucher}";
    }

    private static string CombineReference(BankTransactionRecord transaction) =>
        transaction.ReferenceNumber.Length > 0
            ? transaction.ReferenceNumber + " " + transaction.Narration
            : transaction.Narration;

    private static string SelectSharedReference(IReadOnlySet<string> source, IReadOnlySet<string> target) =>
        source.Intersect(target, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Any(char.IsLetter) && value.Any(char.IsDigit))
            .ThenByDescending(value => value.Length)
            .FirstOrDefault() ?? string.Empty;
}
