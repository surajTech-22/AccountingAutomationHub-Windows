using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Application.Masters;

public interface IStockMasterRepository
{
    void Initialize();
    IReadOnlyList<StockItemRecord> GetStockItems(long clientId, string financialYear, bool includeArchived = false, string searchText = "");
    IReadOnlyList<StockUnitRecord> GetUnits(long clientId, string financialYear, bool includeArchived = false, string searchText = "");
    IReadOnlyList<StockSimpleMasterRecord> GetSimpleMasters(long clientId, string financialYear, string type, bool includeArchived = false, string searchText = "");
    int UpsertStockItems(long clientId, string financialYear, IEnumerable<StockItemRecord> records, bool markUserEdited = false);
    StockItemRecord UpsertStockItemAndGet(long clientId, string financialYear, StockItemRecord record, bool markUserEdited = false);
    int UpsertUnits(long clientId, string financialYear, IEnumerable<StockUnitRecord> records, bool markUserEdited = false);
    int UpsertSimpleMasters(long clientId, string financialYear, IEnumerable<StockSimpleMasterRecord> records, bool markUserEdited = false);
    void SaveStockItem(StockItemRecord record);
    // Fix121: explicit Quantity Conversion may change the single AAH Inventory/Transaction
    // Unit for a local (not-yet-created-in-Tally) Stock Item and rewrite its Unit Profiles.
    // A Tally-backed Stock Item remains protected and must use its allowed/alternate profile.
    void ChangeStockItemInventoryUnit(long clientId, string financialYear, string stockItemName, string finalUnit, string importedUnit, decimal factorToFinal, string source);
    void SaveUnit(StockUnitRecord record);
    void SaveSimpleMaster(StockSimpleMasterRecord record);
    void ArchiveStockItem(long persistentId, bool archived);
    void ArchiveUnit(long persistentId, bool archived);
    void ArchiveSimpleMaster(long persistentId, bool archived);

    IReadOnlyList<StockItemAliasRecord> GetItemAliases(long clientId, string financialYear);
    void SaveItemAlias(long clientId, string financialYear, string importedAlias, string stockItemName, string source = "");
    IReadOnlyList<StockUnitMappingRecord> GetUnitMappings(long clientId, string financialYear);
    void SaveUnitMapping(long clientId, string financialYear, string stockItemName, string importedUnit, string finalUnit);
    IReadOnlyList<QuantityConversionRule> GetQuantityConversionRules(long clientId, string financialYear);
    void SaveQuantityConversionRule(QuantityConversionRule rule);

    IReadOnlyList<StockItemUnitProfileRecord> GetStockItemUnitProfiles(long clientId, string financialYear, long stockItemId = 0);
    void SaveStockItemUnitProfile(StockItemUnitProfileRecord profile);
    void DeleteStockItemUnitProfile(long persistentId);
    bool HasStockItemInventoryUsage(long clientId, string financialYear, long stockItemId, string stockItemName);

    IReadOnlyList<GstHsnSacDirectoryRecord> GetGstHsnSacDirectory(string searchText = "", int limit = 5000);
    int ReplaceGstHsnSacDirectory(IEnumerable<GstHsnSacDirectoryRecord> records, string source);
}
