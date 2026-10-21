using System;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>
/// Shared SQLite connection policy for the authoritative AAH database.
/// Fix81 keeps each connection on a private page cache while all connections still share
/// the same WAL-backed database file. SQLite shared-cache mode adds table-level in-process
/// locks (SQLITE_LOCKED) that busy_timeout cannot resolve; private caches + WAL preserve
/// concurrent readers while the normal SQLite single-writer/busy-timeout policy arbitrates writes.
/// WAL is a database-level setting and is requested once per database path;
/// connection-local foreign-key enforcement and busy timeout are applied on every open.
/// Financial durability mode is intentionally not weakened here: synchronous remains
/// at the existing/default policy unless a repository explicitly owns a different policy.
/// </summary>
public static class SqliteConnectionPolicy
{
    public const int BusyTimeoutMilliseconds = 8000;

    private sealed class DatabaseModeState
    {
        public object SyncRoot { get; } = new();
        public bool Completed { get; set; }
    }

    private static readonly ConcurrentDictionary<string, DatabaseModeState> DatabaseModes =
        new(StringComparer.OrdinalIgnoreCase);

    public static SqliteConnection Open(string connectionString, bool enableForeignKeys = true)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ConfigureConnection(connection, enableForeignKeys);
        EnsureDatabaseMode(connection, connectionString);
        return connection;
    }

    public static void ConfigureConnection(SqliteConnection connection, bool enableForeignKeys = true)
    {
        using var command = connection.CreateCommand();
        command.CommandText = enableForeignKeys
            ? $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};"
            : $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }

    private static void EnsureDatabaseMode(SqliteConnection connection, string connectionString)
    {
        var state = DatabaseModes.GetOrAdd(connectionString, static _ => new DatabaseModeState());
        if (state.Completed) return;

        lock (state.SyncRoot)
        {
            if (state.Completed) return;

            // journal_mode is persistent/database-wide. Request it once rather than on every
            // high-traffic repository connection. Do not change synchronous here.
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=1000;";
            command.ExecuteNonQuery();
            state.Completed = true;
        }
    }
}
