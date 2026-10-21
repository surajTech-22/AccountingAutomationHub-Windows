using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Clients;

public sealed class SqliteClientPreferenceRepository :
    IClientPreferenceRepository
{
    private readonly string _connectionString;

    public SqliteClientPreferenceRepository()
    {
        ApplicationDataPaths.EnsureFolders();

        _connectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource = ApplicationDataPaths.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteClientPreferenceRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ClientPreferences
            (
                ClientId          INTEGER NOT NULL,
                PreferenceKey     TEXT NOT NULL COLLATE NOCASE,
                PreferenceValue   TEXT NOT NULL DEFAULT '',
                UpdatedUtc        TEXT NOT NULL,
                PRIMARY KEY (ClientId, PreferenceKey),
                FOREIGN KEY (ClientId)
                    REFERENCES Clients(Id)
                    ON DELETE CASCADE
            );
            """;

        command.ExecuteNonQuery();
    }

    public string Get(
        long clientId,
        string preferenceKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT PreferenceValue
            FROM ClientPreferences
            WHERE
                ClientId = $ClientId
                AND PreferenceKey = $PreferenceKey;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$PreferenceKey",
            preferenceKey.Trim());

        return Convert.ToString(
            command.ExecuteScalar())
            ?? string.Empty;
    }

    public void Set(
        long clientId,
        string preferenceKey,
        string preferenceValue)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO ClientPreferences
            (
                ClientId,
                PreferenceKey,
                PreferenceValue,
                UpdatedUtc
            )
            VALUES
            (
                $ClientId,
                $PreferenceKey,
                $PreferenceValue,
                $UpdatedUtc
            )
            ON CONFLICT(ClientId, PreferenceKey)
            DO UPDATE SET
                PreferenceValue = excluded.PreferenceValue,
                UpdatedUtc = excluded.UpdatedUtc
            WHERE ClientPreferences.PreferenceValue <> excluded.PreferenceValue;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$PreferenceKey",
            preferenceKey.Trim());

        command.Parameters.AddWithValue(
            "$PreferenceValue",
            preferenceValue.Trim());

        command.Parameters.AddWithValue(
            "$UpdatedUtc",
            DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }
}
