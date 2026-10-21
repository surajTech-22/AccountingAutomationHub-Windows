using System.Text.Json;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Purchases;

/// <summary>
/// One Purchase workbook mapping authority shared by Manual Import and Auto Import.
/// The saved profile owns both the header mapping and the exact sheet/header/data-start
/// layout confirmed by the user. This mirrors the Sales saved-header workflow while
/// retaining the Purchase-specific layout coordinates required for hidden/offset sheets.
/// </summary>
public sealed class SqlitePurchaseMappingRepository : IPurchaseMappingRepository
{
    private readonly string _connectionString;

    public SqlitePurchaseMappingRepository()
    {
        ApplicationDataPaths.EnsureFolders();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public void Initialize() =>
        SqliteSchemaInitializationGate.RunOnce(nameof(SqlitePurchaseMappingRepository), _connectionString, InitializeCore);

    private void InitializeCore()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PurchaseHeaderMappings
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId INTEGER NOT NULL,
                FinancialYear TEXT NOT NULL,
                HeaderSignature TEXT NOT NULL COLLATE NOCASE,
                SheetName TEXT NOT NULL DEFAULT '',
                HeaderRow INTEGER NOT NULL DEFAULT 1,
                DataStartRow INTEGER NOT NULL DEFAULT 2,
                GstTreatment TEXT NOT NULL DEFAULT 'Not shown in 2B',
                MappingJson TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PurchaseHeaderMappings_ClientFySignature
            ON PurchaseHeaderMappings(ClientId,FinancialYear,HeaderSignature COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "PurchaseHeaderMappings", "GstTreatment", "TEXT NOT NULL DEFAULT 'Not shown in 2B'");
    }

    public PurchaseMappingProfile? Get(long clientId, string financialYear, string headerSignature)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SheetName,HeaderRow,DataStartRow,GstTreatment,MappingJson
            FROM PurchaseHeaderMappings
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND HeaderSignature=$Signature
            LIMIT 1;
            """;
        Scope(command, clientId, financialYear, headerSignature);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        Dictionary<string, string>? mapping;
        try
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4));
        }
        catch
        {
            return null;
        }
        if (mapping is null) return null;

        var headerRow = reader.IsDBNull(1) ? 1 : Math.Max(1, reader.GetInt32(1));
        var dataStartRow = reader.IsDBNull(2) ? headerRow + 1 : Math.Max(headerRow + 1, reader.GetInt32(2));
        return new PurchaseMappingProfile
        {
            HeaderSignature = headerSignature.Trim(),
            SheetName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            HeaderRow = headerRow,
            DataStartRow = dataStartRow,
            GstTreatment = reader.IsDBNull(3) ? PurchaseTreatmentPolicy.NotShownInTwoB : PurchaseTreatmentPolicy.NormalizePurchaseTreatment(reader.GetString(3)),
            Mapping = new Dictionary<string, string>(mapping, StringComparer.OrdinalIgnoreCase)
        };
    }


    public IReadOnlyList<PurchaseMappingProfile> GetAll(long clientId, string financialYear)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT HeaderSignature,SheetName,HeaderRow,DataStartRow,GstTreatment,MappingJson
            FROM PurchaseHeaderMappings
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY UpdatedUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", (financialYear ?? string.Empty).Trim());

        var profiles = new List<PurchaseMappingProfile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Dictionary<string, string>? mapping;
            try
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5));
            }
            catch
            {
                continue;
            }
            if (mapping is null) continue;

            var headerRow = reader.IsDBNull(2) ? 1 : Math.Max(1, reader.GetInt32(2));
            var dataStartRow = reader.IsDBNull(3) ? headerRow + 1 : Math.Max(headerRow + 1, reader.GetInt32(3));
            profiles.Add(new PurchaseMappingProfile
            {
                HeaderSignature = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                SheetName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                HeaderRow = headerRow,
                DataStartRow = dataStartRow,
                GstTreatment = reader.IsDBNull(4) ? PurchaseTreatmentPolicy.NotShownInTwoB : PurchaseTreatmentPolicy.NormalizePurchaseTreatment(reader.GetString(4)),
                Mapping = new Dictionary<string, string>(mapping, StringComparer.OrdinalIgnoreCase)
            });
        }
        return profiles;
    }

    public void Save(long clientId, string financialYear, PurchaseMappingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PurchaseHeaderMappings
                (ClientId,FinancialYear,HeaderSignature,SheetName,HeaderRow,DataStartRow,GstTreatment,MappingJson,UpdatedUtc)
            VALUES
                ($ClientId,$FinancialYear,$Signature,$SheetName,$HeaderRow,$DataStartRow,$GstTreatment,$Json,$Now)
            ON CONFLICT(ClientId,FinancialYear,HeaderSignature) DO UPDATE SET
                SheetName=excluded.SheetName,
                HeaderRow=excluded.HeaderRow,
                DataStartRow=excluded.DataStartRow,
                GstTreatment=excluded.GstTreatment,
                MappingJson=excluded.MappingJson,
                UpdatedUtc=excluded.UpdatedUtc;
            """;
        Scope(command, clientId, financialYear, profile.HeaderSignature);
        command.Parameters.AddWithValue("$SheetName", (profile.SheetName ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$HeaderRow", Math.Max(1, profile.HeaderRow));
        command.Parameters.AddWithValue("$DataStartRow", Math.Max(Math.Max(1, profile.HeaderRow) + 1, profile.DataStartRow));
        command.Parameters.AddWithValue("$GstTreatment", PurchaseTreatmentPolicy.NormalizePurchaseTreatment(profile.GstTreatment));
        command.Parameters.AddWithValue("$Json", JsonSerializer.Serialize(profile.Mapping));
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Delete(long clientId, string financialYear, string headerSignature)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PurchaseHeaderMappings WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND HeaderSignature=$Signature;";
        Scope(command, clientId, financialYear, headerSignature);
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table});";
        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void Scope(SqliteCommand command, long clientId, string financialYear, string headerSignature)
    {
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Signature", (headerSignature ?? string.Empty).Trim());
    }

    private SqliteConnection Open() => SqliteConnectionPolicy.Open(_connectionString);
}
