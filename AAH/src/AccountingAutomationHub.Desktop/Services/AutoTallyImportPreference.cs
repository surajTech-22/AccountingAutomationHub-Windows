using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Infrastructure.Clients;

namespace AccountingAutomationHub.Desktop.Services;

public static class AutoTallyImportPreference
{
    private const string Prefix = "Tally.AutoImport.Enabled.";
    private const string OpenPrefix = "Tally.AutoImport.AutoOpen.";
    private const string ClosePrefix = "Tally.AutoImport.AutoClose.";
    private const string CompanyFolderPrefix = "Tally.AutoImport.ExactCompanyFolder.";
    private const string CompanyKeyPrefix = "Tally.AutoImport.ExactCompanyKey.";

    private static string FyKey(string prefix, string year) => prefix + (year ?? string.Empty).Trim();
    public static bool AutoOpen(SqliteClientPreferenceRepository repository, ClientProfile client) =>
        bool.TryParse(repository.Get(client.Id, FyKey(OpenPrefix, client.FinancialYear)), out var enabled) && enabled;
    public static bool AutoClose(SqliteClientPreferenceRepository repository, ClientProfile client) =>
        bool.TryParse(repository.Get(client.Id, FyKey(ClosePrefix, client.FinancialYear)), out var enabled) && enabled;
    public static string ExactCompanyFolder(SqliteClientPreferenceRepository repository, ClientProfile client) =>
        (repository.Get(client.Id, FyKey(CompanyFolderPrefix, client.FinancialYear)) ?? string.Empty).Trim();
    public static void SetAutoOpen(SqliteClientPreferenceRepository repository, long id, string fy, bool enabled) =>
        repository.Set(id, FyKey(OpenPrefix, fy), enabled.ToString());
    public static void SetAutoClose(SqliteClientPreferenceRepository repository, long id, string fy, bool enabled) =>
        repository.Set(id, FyKey(ClosePrefix, fy), enabled.ToString());
    public static string ExactCompanyKey(SqliteClientPreferenceRepository repository, ClientProfile client) =>
        (repository.Get(client.Id, FyKey(CompanyKeyPrefix, client.FinancialYear)) ?? string.Empty).Trim();
    public static void SetExactCompanyKey(SqliteClientPreferenceRepository repository, long id, string fy, string key) =>
        repository.Set(id, FyKey(CompanyKeyPrefix, fy), (key ?? string.Empty).Trim());
    public static void SetExactCompanyFolder(SqliteClientPreferenceRepository repository, long id, string fy, string folder) =>
        repository.Set(id, FyKey(CompanyFolderPrefix, fy), (folder ?? string.Empty).Trim());

    public static string Key(string financialYear) => Prefix + (financialYear ?? string.Empty).Trim();

    public static bool IsEnabled(SqliteClientPreferenceRepository repository, ClientProfile client)
    {
        var raw = repository.Get(client.Id, Key(client.FinancialYear));
        return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out var enabled) || enabled;
    }

    public static void SetEnabled(SqliteClientPreferenceRepository repository, long clientId, string financialYear, bool enabled) =>
        repository.Set(clientId, Key(financialYear), enabled ? "True" : "False");
}
