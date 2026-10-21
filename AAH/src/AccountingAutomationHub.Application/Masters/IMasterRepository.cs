using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Application.Masters;

public interface IMasterRepository
{
    void Initialize();
    bool HasRecords(long clientId, string financialYear);
    IReadOnlyList<MasterRecord> Get(long clientId, string financialYear, bool includeArchived, string searchText = "");
    int Upsert(long clientId, string financialYear, IEnumerable<MasterRecord> records, bool markUserEdited = false);
    void Save(MasterRecord record);
    int ApplyVerifiedLedgerRenameReferences(long clientId, string financialYear, string oldName, string newName);
    MasterRecord? FindNormalizedDuplicate(long clientId, string financialYear, string type, string name, long excludePersistentId = 0, bool ignoreArchivedTally = false);
    IReadOnlyList<IReadOnlyList<MasterRecord>> GetNormalizedDuplicateGroups(long clientId, string financialYear);
    string ResolveCanonicalName(long clientId, string financialYear, string type, string name);
    void Archive(long persistentId, bool archived);
}
