using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Imports;

public sealed record GlobalHeaderMappingRecord(long Id, string Module, string CanonicalField, string SourceHeader, string NormalizedSourceHeader, DateTime CreatedUtc);
public sealed record GlobalHeaderMappingAddResult(bool Added, bool AlreadyExists, bool Conflict, string ExistingCanonicalField, string Message);

public sealed class SqliteGlobalHeaderMappingRepository
{
    private readonly string _connectionString;

    public SqliteGlobalHeaderMappingRepository()
    {
        ApplicationDataPaths.EnsureFolders();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public void Initialize() => SqliteSchemaInitializationGate.RunOnce(nameof(SqliteGlobalHeaderMappingRepository), _connectionString, InitializeCore);

    private void InitializeCore()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS GlobalImportHeaderMappings
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Module TEXT NOT NULL COLLATE NOCASE,
                CanonicalField TEXT NOT NULL COLLATE NOCASE,
                SourceHeader TEXT NOT NULL,
                NormalizedSourceHeader TEXT NOT NULL COLLATE NOCASE,
                CreatedUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_GlobalImportHeaderMappings_ModuleSource
            ON GlobalImportHeaderMappings(Module COLLATE NOCASE, NormalizedSourceHeader COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_GlobalImportHeaderMappings_ModuleCanonical
            ON GlobalImportHeaderMappings(Module COLLATE NOCASE, CanonicalField COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<GlobalHeaderMappingRecord> GetAll()
    {
        Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Module,CanonicalField,SourceHeader,NormalizedSourceHeader,CreatedUtc FROM GlobalImportHeaderMappings ORDER BY Module,Id;";
        using var reader = command.ExecuteReader();
        var rows = new List<GlobalHeaderMappingRecord>();
        while (reader.Read())
        {
            _ = DateTime.TryParse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind, out var created);
            rows.Add(new GlobalHeaderMappingRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), created));
        }
        return rows;
    }

    public GlobalHeaderMappingAddResult Add(string module, string canonicalField, string sourceHeader)
    {
        Initialize();
        module = ImportHeaderMappingCatalog.NormalizeModule(module);
        canonicalField = canonicalField?.Trim() ?? string.Empty;
        sourceHeader = sourceHeader?.Trim() ?? string.Empty;
        var normalized = ImportHeaderMappingCatalog.NormalizeHeader(sourceHeader);
        if (module.Length == 0 || canonicalField.Length == 0 || normalized.Length == 0)
            return new(false, false, false, string.Empty, "Module, AAH header and source header are required.");
        if (ImportHeaderMappingCatalog.FindField(module, canonicalField) is null)
            return new(false, false, false, string.Empty, $"'{canonicalField}' is not a supported {module} AAH header.");

        using var connection = Open();
        using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT CanonicalField FROM GlobalImportHeaderMappings WHERE Module=$Module AND NormalizedSourceHeader=$Source LIMIT 1;";
            existing.Parameters.AddWithValue("$Module", module);
            existing.Parameters.AddWithValue("$Source", normalized);
            var owner = Convert.ToString(existing.ExecuteScalar()) ?? string.Empty;
            if (owner.Length > 0)
            {
                if (owner.Equals(canonicalField, StringComparison.OrdinalIgnoreCase))
                    return new(false, true, false, owner, $"'{sourceHeader}' is already recognized as {ImportHeaderMappingCatalog.FindField(module, owner)?.DisplayName ?? owner}.");
                return new(false, false, true, owner, $"'{sourceHeader}' is already mapped to {ImportHeaderMappingCatalog.FindField(module, owner)?.DisplayName ?? owner}. Existing global mappings are append-only and are not replaced.");
            }
        }

        var builtInOwner = ImportHeaderMappingCatalog.ResolveBuiltIn(module, sourceHeader);
        if (builtInOwner.Length > 0)
        {
            if (builtInOwner.Equals(canonicalField, StringComparison.OrdinalIgnoreCase))
                return new(false, true, false, builtInOwner, $"'{sourceHeader}' is already a built-in {module} header for {ImportHeaderMappingCatalog.FindField(module, canonicalField)?.DisplayName ?? canonicalField}.");
            return new(false, false, true, builtInOwner, $"'{sourceHeader}' is already a protected built-in header for {ImportHeaderMappingCatalog.FindField(module, builtInOwner)?.DisplayName ?? builtInOwner}; it cannot be remapped.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GlobalImportHeaderMappings(Module,CanonicalField,SourceHeader,NormalizedSourceHeader,CreatedUtc) VALUES($Module,$Canonical,$Source,$Normalized,$Now);";
        command.Parameters.AddWithValue("$Module", module);
        command.Parameters.AddWithValue("$Canonical", canonicalField);
        command.Parameters.AddWithValue("$Source", sourceHeader);
        command.Parameters.AddWithValue("$Normalized", normalized);
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return new(true, false, false, string.Empty, $"Added global mapping: {sourceHeader} → {ImportHeaderMappingCatalog.FindField(module, canonicalField)?.DisplayName ?? canonicalField}.");
    }

    private SqliteConnection Open() => SqliteConnectionPolicy.Open(_connectionString);
}
