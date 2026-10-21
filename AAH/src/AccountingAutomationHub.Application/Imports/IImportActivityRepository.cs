using AccountingAutomationHub.Domain.Imports;

namespace AccountingAutomationHub.Application.Imports;

public interface IImportActivityRepository
{
    void Initialize();

    ImportActivity Record(
        ImportActivity activity);

    ImportModuleSnapshot GetSnapshot(
        long clientId,
        string importType);

    IReadOnlyList<ImportActivity> GetRecent(
        long clientId,
        int maximumRows = 10);
}
