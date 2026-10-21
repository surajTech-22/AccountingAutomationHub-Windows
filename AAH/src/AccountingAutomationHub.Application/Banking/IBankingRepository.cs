using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Application.Banking;

public interface IBankingRepository
{
    void Initialize();
    bool HasRecords(long clientId, string financialYear);
    void ClearClientYear(long clientId, string financialYear);
    IReadOnlyList<BankAccountRecord> GetAccounts(long clientId, string financialYear);
    BankAccountRecord UpsertAccount(BankAccountRecord account);
    bool StatementHashExists(long clientId, string financialYear, string fileHash);
    bool StatementPeriodExists(long clientId, string financialYear, long bankAccountId, DateTime? periodFrom, DateTime? periodTo);
    long SaveStatement(BankStatementRecord statement);
    IReadOnlyList<BankStatementRecord> GetStatements(long clientId, string financialYear);
    void SaveStatementImportAttempt(BankStatementImportAttemptRecord attempt);
    IReadOnlyList<BankStatementImportAttemptRecord> GetStatementImportAttempts(long clientId, string financialYear);
    long RecoverStatementRegistry(BankStatementRecord statement, string sourceFile, string sourceSheet);
    void SaveStatementBalances(long statementId, decimal? openingBalance, decimal? closingBalance, string status, decimal difference, string openingBalanceSource = "", string closingBalanceSource = "");
    void RepairStatementBalanceEvidence(
        long statementId,
        DateTime? periodFrom,
        DateTime? periodTo,
        decimal? openingBalance,
        string openingBalanceSource,
        decimal? sourceOpeningBalance,
        decimal? sourceClosingBalance,
        decimal? closingBalance,
        string closingBalanceSource,
        string sourceFile,
        string sourceSheet,
        IReadOnlyDictionary<int, decimal> runningBalances);
    void DeleteStatement(long statementId);
    void SaveAccount(BankAccountRecord account);
    IReadOnlySet<string> GetTransactionFingerprints(long clientId, string financialYear);
    int InsertTransactions(IEnumerable<BankTransactionRecord> transactions);
    BankStatementBatchCommitResult CommitBankCacheBatch(IReadOnlyList<BankStatementBatchCommitItem> items, IReadOnlyList<BankStatementRecord> existingStatementUpdates);
    int FinalizePreImportStaging(long statementId);
    IReadOnlyList<BankTransactionRecord> GetTransactions(long clientId, string financialYear, bool includeArchived = true);
    void SaveTransaction(BankTransactionRecord transaction);
    void SaveStatementReconciliation(long statementId, string status, decimal difference);
    IReadOnlyList<BankReconciliationSummary> GetReconciliation(long clientId, string financialYear);
    int ReplaceTransferMatches(long clientId, string financialYear, IEnumerable<BankTransferMatch> matches);
    IReadOnlyList<BankTransferMatch> GetTransferMatches(long clientId, string financialYear);
    int ArchiveImportedTransactionMatchedToTally(
        long clientId,
        string financialYear,
        long transactionId,
        string matchId,
        string interBankCounterpartLedger);
    int RestoreImportedTransactionAfterTallyBalanceFailure(long clientId, string financialYear, long transactionId, string matchId);
    int ResetLegacyTallyConvertedArchives(long clientId, string financialYear);
    int ResetChangedTallyTransferMatches(long clientId, string financialYear, IEnumerable<string> tallyMasterIds);
    int AdoptBoundTallyVoucherTreatment(long clientId, string financialYear, string companyKey, TallyConvertedBankTransaction changed, string counterpartLedger);
    IReadOnlyList<BankLedgerMapping> GetLedgerMappings(long clientId, string financialYear, bool pendingOnly = false);
    void UpsertLedgerMapping(BankLedgerMapping mapping);
    void ClearLedgerMapping(BankLedgerMapping mapping);
    int ClearLedgerMappingsForTransactions(long clientId, string financialYear, IReadOnlyCollection<long> transactionIds);
    void ReplaceLedgerMappings(long clientId, string financialYear, IEnumerable<BankLedgerMapping> mappings);
    int ApplyLedgerMapping(long clientId, string financialYear, BankLedgerMapping mapping);
    int ApplyLedgerMappings(long clientId, string financialYear, IEnumerable<BankLedgerMapping> mappings);
    CashEntrySettings GetCashSettings(long clientId, string financialYear);
    void SaveCashSettings(CashEntrySettings settings);
    IReadOnlyList<CashEntryRecord> GetCashEntries(long clientId, string financialYear);
    void SaveCashEntry(CashEntryRecord entry);
    void DeleteCashEntry(long entryId);
}
