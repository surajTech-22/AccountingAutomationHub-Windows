using AccountingAutomationHub.Domain.Journals;

namespace AccountingAutomationHub.Application.Journals;

public interface IJournalRepository
{
    void Initialize();
    bool HasRecords(long clientId, string financialYear);
    JournalGenerationSettings GetSettings(long clientId, string financialYear);
    void SaveSettings(JournalGenerationSettings settings);
    IReadOnlyList<JournalVoucher> GetVouchers(long clientId, string financialYear);
    IReadOnlySet<string> GetProtectedPeriods(long clientId, string financialYear);
    int ReplaceGenerated(long clientId, string financialYear, IEnumerable<JournalVoucher> vouchers);
    void SaveVoucher(JournalVoucher voucher);
    void DeleteVoucher(long voucherId);
    IReadOnlyList<JournalLedgerMapping> GetLedgerMappings(long clientId, string financialYear, bool pendingOnly = true);
    void UpsertPendingLedgerMappings(long clientId, string financialYear, IEnumerable<string> ledgerNames);
    void ResolveLedgerMapping(long mappingId, string masterGroup);
    void ReopenLedgerMapping(long mappingId);
}
