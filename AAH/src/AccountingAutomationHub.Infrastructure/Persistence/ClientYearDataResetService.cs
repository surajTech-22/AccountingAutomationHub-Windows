using AccountingAutomationHub.Infrastructure.Masters;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>
/// Clears authoritative accounting/module data for one Client/FY while deliberately
/// preserving the Client profile, application/client Settings, Automation Folder link,
/// Tally company link, Document Vault/source documents and security/audit configuration.
/// </summary>
public sealed class ClientYearDataResetService
{
    private readonly string _connectionString;

    public ClientYearDataResetService(string? connectionString = null)
    {
        _connectionString = connectionString ?? new SqliteConnectionStringBuilder
    {
        DataSource = ApplicationDataPaths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private
    }.ToString();
    }

    public ClientYearDataResetResult Clear(long clientId, string financialYear)
    {
        if (clientId <= 0) throw new ArgumentOutOfRangeException(nameof(clientId));
        if (string.IsNullOrWhiteSpace(financialYear)) throw new ArgumentException("Financial Year is required.", nameof(financialYear));

        using var connection = SqliteConnectionPolicy.Open(_connectionString);
        using var transaction = connection.BeginTransaction();
        using (var defer = connection.CreateCommand())
        {
            defer.Transaction = transaction;
            defer.CommandText = "PRAGMA defer_foreign_keys=ON;";
            defer.ExecuteNonQuery();
        }

        var deleted = 0;
        var companyKeys = ReadCompanyKeys(connection, transaction, clientId);

        // Child rows without ClientId/FY columns are removed before their parents.
        deleted += Execute(connection, transaction,
            "DELETE FROM BankLedgerMappingMembers WHERE MappingId IN (SELECT Id FROM BankLedgerMappings WHERE ClientId=$c AND FinancialYear=$fy);",
            clientId, financialYear);
        deleted += Execute(connection, transaction,
            "DELETE FROM JournalLines WHERE VoucherId IN (SELECT Id FROM JournalVouchers WHERE ClientId=$c AND FinancialYear=$fy);",
            clientId, financialYear);
        deleted += Execute(connection, transaction,
            "DELETE FROM ImportJobAttempts WHERE JobId IN (SELECT JobId FROM ImportJobs WHERE ClientId=$c AND FinancialYear=$fy);",
            clientId, financialYear);
        deleted += Execute(connection, transaction,
            "DELETE FROM ImportJobEntities WHERE JobId IN (SELECT JobId FROM ImportJobs WHERE ClientId=$c AND FinancialYear=$fy);",
            clientId, financialYear);

        foreach (var companyKey in companyKeys)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM TallyVoucherIndex WHERE CompanyKey=$company AND FinancialYear=$fy;";
            command.Parameters.AddWithValue("$company", companyKey);
            command.Parameters.AddWithValue("$fy", financialYear.Trim());
            deleted += command.ExecuteNonQuery();
        }

        // Module/accounting data. Configuration authorities such as AutomationFolders,
        // TallyClientLinks, ClientPreferences, DocumentVault and FolderLinks are excluded.
        var tables = new[]
        {
            "BankTransferMatches", "BankLedgerMappings", "BankTransactions", "BankStatements", "BankAccounts",
            "CashEntries", "CashEntrySettings",
            "JournalVouchers", "JournalLedgerMappings", "GstJournalSettings",
            "PurchaseRecords", "PurchaseImportBatches",
            "SalesRecords", "SalesImportBatches", "SalesB2BCustomerNameOverrides", "SalesB2CCustomerNameOverrides", "SalesHeaderMappings", "PurchaseHeaderMappings",
            "Gstr3bReturns", "GstLedgerPeriods", "GstPortalSourceState", "GstBootstrapState", "GstReconciliation",
            "MasterMergeAliases", "Masters",
            "StockItemAliases", "StockUnitMappings", "QuantityConversionRules", "StockItems", "StockUnits", "StockSimpleMasters",
            "ActionRequired", "AutomationFileHistory", "TallyImportFingerprints", "TallyBankStatementLinks", "TallyBankSyncState",
            "ImportJobs", "ImportRuns", "EntityVersions", "RecurringTransactionProfiles", "TransactionRelationships",
            "LedgerSuggestionLearning", "AdvancedReconciliationMatches",
            "AccountingPeriodLocks", "FinanceSettlements", "FixedAssets", "BankRules", "FinanceBudgets", "RecurringCashFlowTemplates",
            "FinanceApprovals", "InventoryPolicies"
        };

        foreach (var table in tables)
        {
            if (!TableExists(connection, transaction, table)) continue;
            deleted += Execute(connection, transaction, $"DELETE FROM [{table}] WHERE ClientId=$c AND FinancialYear=$fy;", clientId, financialYear);
        }

        // ImportActivities is keyed only by ClientId. A Client record itself is FY-specific,
        // so clearing its activity does not affect another Client/FY record.
        if (TableExists(connection, transaction, "ImportActivities"))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ImportActivities WHERE ClientId=$c;";
            command.Parameters.AddWithValue("$c", clientId);
            deleted += command.ExecuteNonQuery();
        }

        // Clear and restore are one transaction: a failed seed restores the old data.
        var standard = MasterSynchronizationService.StandardStructure(string.Empty);
        foreach (var master in standard)
        {
            using var seed = connection.CreateCommand();
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO Masters
                    (ClientId,FinancialYear,Name,Parent,Type,Company,Source,OpeningBalance,
                     IsArchived,IsUserEdited,TallyMasterId,TallyGuid,TallyAlterId,CreatedUtc,UpdatedUtc)
                VALUES ($c,$fy,$name,$parent,$type,'','Standard hierarchy',0,
                        0,0,'','',0,$utc,$utc);
                """;
            seed.Parameters.AddWithValue("$c",clientId);
            seed.Parameters.AddWithValue("$fy",financialYear.Trim());
            seed.Parameters.AddWithValue("$name",master.Name);
            seed.Parameters.AddWithValue("$parent",master.Parent);
            seed.Parameters.AddWithValue("$type",master.Type);
            seed.Parameters.AddWithValue("$utc",DateTime.UtcNow.ToString("O"));
            seed.ExecuteNonQuery();
        }
        transaction.Commit();
        return new ClientYearDataResetResult(deleted, companyKeys.Count, standard.Count);
    }

    private static IReadOnlyList<string> ReadCompanyKeys(SqliteConnection connection, SqliteTransaction transaction, long clientId)
    {
        if (!TableExists(connection, transaction, "TallyClientLinks")) return Array.Empty<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CompanyKey FROM TallyClientLinks WHERE ClientId=$c AND TRIM(CompanyKey)<>'';";
        command.Parameters.AddWithValue("$c", clientId);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, long clientId, string financialYear)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$c", clientId);
        command.Parameters.AddWithValue("$fy", financialYear.Trim());
        return command.ExecuteNonQuery();
    }
}

public readonly record struct ClientYearDataResetResult(int DeletedRows, int TallyCompaniesRetained, int StandardMastersRestored = 0);
