using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Masters;

/// <summary>Runs explicit mapping work off the UI thread against the existing database.</summary>
public static class StockItemMappingCommitService
{
    public static StockItemMappingResult ApplySales(long clientId, string financialYear, string company,
        IReadOnlyList<StockItemMappingSelection> selections) =>
        Apply("SalesRecords", clientId, financialYear, company, "Manual Sales Item Mapping", selections);

    public static StockItemMappingResult ApplyPurchase(long clientId, string financialYear, string company,
        IReadOnlyList<StockItemMappingSelection> selections) =>
        Apply("PurchaseRecords", clientId, financialYear, company, "Manual Purchase Item Mapping", selections);

    private static StockItemMappingResult Apply(string table, long clientId, string financialYear, string company,
        string sourceLabel, IReadOnlyList<StockItemMappingSelection> selections)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        return StockItemMappingPersistence.Apply(connection, table, clientId, financialYear,
            company, sourceLabel, selections);
    }
}
