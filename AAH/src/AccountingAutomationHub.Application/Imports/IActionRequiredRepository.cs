using AccountingAutomationHub.Domain.Imports;

namespace AccountingAutomationHub.Application.Imports;

public interface IActionRequiredRepository
{
    void Initialize();
    IReadOnlyList<ActionRequiredItem> GetOpen(long clientId, string financialYear, string? module = null);
    IReadOnlyList<ActionRequiredItem> GetAllOpen(string? module = null);
    int CountOpen(long clientId, string financialYear, string? module = null);
    void Upsert(ActionRequiredItem item);
    void Resolve(long persistentId);
    int ResolveAll(long clientId, string financialYear);
    void ResolveBySourceKey(long clientId, string financialYear, string module, string sourceKey);
}
