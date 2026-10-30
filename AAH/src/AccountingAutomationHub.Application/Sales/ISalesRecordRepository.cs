using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Application.Sales;

public interface ISalesRecordRepository
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

    int ClearModule(long clientId, string financialYear, string? registrationGstin = null);

    IReadOnlyList<SalesImportRecord> GetAutomaticRecords(
        long clientId,
        string financialYear);

    IReadOnlyList<SalesImportRecord> GetRecords(
        long clientId,
        string financialYear);

    int SaveManualImport(
        long clientId,
        string financialYear,
        SalesManualImportResult result,
        bool replaceExisting,
        string? replaceSourcePath = null);

    // R9 FIXED16: a printed Tally Sales Register is companion rate evidence, not
    // a second set of vouchers. Persist its date/voucher-range GST buckets and enrich
    // matching Tally Voucher XML rows without marking them user-edited.
    int ApplyTallySalesRegisterRateMappings(
        long clientId,
        string financialYear,
        IReadOnlyList<SalesRateSummaryMapping> mappings,
        string sourcePath,
        string sourceHash);

    int ReplaceAutomaticImport(
        long clientId,
        string financialYear,
        SalesAutoImportResult result);

    int ReplaceAutomaticImportMonth(
        long clientId,
        string financialYear,
        SalesAutoImportResult result,
        DateTime month);

    int UpdateRecords(
        long clientId,
        IEnumerable<SalesImportRecord> records);

    // Fix137: detached read + exact source revision for guarded background projection.
    IReadOnlyList<StockProjectionSnapshot<SalesImportRecord>> GetStockProjectionSnapshots(long clientId, string financialYear);
    StockProjectionWriteResult ApplyStockProjections(long clientId, string financialYear, IEnumerable<StockProjectionChange> changes);

    int UpdateStockIdentityReferences(
        long clientId,
        IEnumerable<SalesImportRecord> records);

    // Fix121: targeted migration/write path for the legacy manual-Sales GSTIN
    // carry bug. It changes only CustomerGstin + RecordJson and deliberately does
    // not mark the accounting row as user-edited.
    int UpdateCustomerGstinReferences(
        long clientId,
        IEnumerable<SalesImportRecord> records);

    IReadOnlyDictionary<string, string> GetB2CCustomerNameOverrides(long clientId, string financialYear);
    int SaveB2CCustomerNameOverride(long clientId, string financialYear, string sourceKey, string customerName);

    IReadOnlyDictionary<string, string> GetB2BCustomerNameOverrides(
        long clientId,
        string financialYear);

    IReadOnlyList<SalesB2BCustomerNameOverride> GetB2BCustomerNameOverrideEntries(
        long clientId,
        string financialYear);

    int SaveB2BCustomerNameOverride(
        long clientId,
        string financialYear,
        string customerGstin,
        string customerName);

    int DeleteB2BCustomerNameOverride(
        long clientId,
        string financialYear,
        string customerGstin,
        string replacementName,
        string replacementSource);
}
