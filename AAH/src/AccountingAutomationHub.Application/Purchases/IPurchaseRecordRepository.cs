using System;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Purchases;

namespace AccountingAutomationHub.Application.Purchases;

public interface IPurchaseRecordRepository
{
    void Initialize();

    bool IsAutomaticImportCurrent(
        long clientId,
        string financialYear,
        string sourceFingerprint,
        string? registrationGstin = null);

    string GetLatestAutomaticImportContentFingerprint(
        long clientId,
        string financialYear,
        string? registrationGstin = null);

    bool HasRecords(
        long clientId,
        string financialYear);

    int ClearModule(long clientId, string financialYear);

    IReadOnlyList<PurchaseImportRecord> GetAutomaticRecords(
        long clientId,
        string financialYear);

    IReadOnlyList<PurchaseImportRecord> GetRecords(
        long clientId,
        string financialYear);

    int SaveManualImport(
        long clientId,
        string financialYear,
        PurchaseManualImportResult result,
        bool replaceExisting,
        string? replaceSourcePath = null);

    int ReplaceAutomaticImport(
        long clientId,
        string financialYear,
        PurchaseAutoImportResult result);

    int ReplaceAutomaticImportMonth(
        long clientId,
        string financialYear,
        PurchaseAutoImportResult result,
        DateTime month);

    int UpdateRecords(
        long clientId,
        IEnumerable<PurchaseImportRecord> records);

    // Fix137: detached read + exact source revision for guarded background projection.
    IReadOnlyList<StockProjectionSnapshot<PurchaseImportRecord>> GetStockProjectionSnapshots(long clientId, string financialYear);
    StockProjectionWriteResult ApplyStockProjections(long clientId, string financialYear, IEnumerable<StockProjectionChange> changes);

    int UpdateStockIdentityReferences(
        long clientId,
        IEnumerable<PurchaseImportRecord> records);

    long InsertManualRateLine(
        long clientId,
        string financialYear,
        PurchaseImportRecord record);

    int DeleteRecords(
        long clientId,
        string financialYear,
        IEnumerable<long> recordIds);
}
