using System;
using System.Collections.Generic;
using System.Linq;
using AccountingAutomationHub.Application.Settings;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Settings;

public sealed class SqliteApplicationPreferenceRepository :
    IApplicationPreferenceRepository
{
    private readonly string _connectionString;

    public event Action<string, string>? SettingChanged;

    public SqliteApplicationPreferenceRepository()
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

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteApplicationPreferenceRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS AppSettings
            (
                SettingKey      TEXT PRIMARY KEY COLLATE NOCASE,
                SettingValue    TEXT NOT NULL DEFAULT ''
            );
            """;

        command.ExecuteNonQuery();
    }

    public string Get(string settingKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT SettingValue
            FROM AppSettings
            WHERE SettingKey = $SettingKey;
            """;

        command.Parameters.AddWithValue(
            "$SettingKey",
            settingKey.Trim());

        return Convert.ToString(
            command.ExecuteScalar())
            ?? string.Empty;
    }


    public IReadOnlyDictionary<string, string> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettingKey, SettingValue FROM AppSettings;";
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            values[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        return values;
    }

    public void Set(
        string settingKey,
        string settingValue)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO AppSettings
            (
                SettingKey,
                SettingValue
            )
            VALUES
            (
                $SettingKey,
                $SettingValue
            )
            ON CONFLICT(SettingKey)
            DO UPDATE SET
                SettingValue = excluded.SettingValue;
            """;

        command.Parameters.AddWithValue(
            "$SettingKey",
            settingKey.Trim());

        command.Parameters.AddWithValue(
            "$SettingValue",
            settingValue.Trim());

        command.ExecuteNonQuery();
        SettingChanged?.Invoke(settingKey.Trim(), settingValue.Trim());
    }


    public void SetMany(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO AppSettings(SettingKey,SettingValue) VALUES($SettingKey,$SettingValue) " +
            "ON CONFLICT(SettingKey) DO UPDATE SET SettingValue=excluded.SettingValue;";
        var key = command.Parameters.Add("$SettingKey", SqliteType.Text);
        var value = command.Parameters.Add("$SettingValue", SqliteType.Text);
        foreach (var pair in values)
        {
            key.Value = pair.Key.Trim();
            value.Value = (pair.Value ?? string.Empty).Trim();
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        foreach (var pair in values)
            SettingChanged?.Invoke(pair.Key.Trim(), (pair.Value ?? string.Empty).Trim());
    }

    public void EnsureDefaults(IReadOnlyDictionary<string, string> defaults)
    {
        var candidates = defaults.Where(pair => pair.Value.Length > 0).ToArray();
        if (candidates.Length == 0) return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO AppSettings(SettingKey,SettingValue) VALUES($SettingKey,$SettingValue) " +
            "ON CONFLICT(SettingKey) DO UPDATE SET SettingValue=excluded.SettingValue " +
            "WHERE AppSettings.SettingValue='';";
        var key = command.Parameters.Add("$SettingKey", SqliteType.Text);
        var value = command.Parameters.Add("$SettingValue", SqliteType.Text);
        foreach (var pair in candidates)
        {
            key.Value = pair.Key.Trim();
            value.Value = pair.Value.Trim();
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }
}
