using AccountingAutomationHub.Domain.Gst;

namespace AccountingAutomationHub.Application.Gst;

public interface IGstRepository
{
    void Initialize();
    bool HasRecords(long clientId, string financialYear);
    void ReplaceReturn(Gstr3bReturnRecord record);
    void ReplaceLedgerPeriods(long clientId, string financialYear, string ledgerType, IEnumerable<GstLedgerPeriodBalance> periods);
    IReadOnlyList<Gstr3bReturnRecord> GetReturns(long clientId, string financialYear);
    IReadOnlyList<GstLedgerPeriodBalance> GetLedgerPeriods(long clientId, string financialYear);
    void ReplaceReconciliation(long clientId, string financialYear, IEnumerable<GstReconciliationRow> rows);
    IReadOnlyList<GstReconciliationRow> GetReconciliation(long clientId, string financialYear);
}
