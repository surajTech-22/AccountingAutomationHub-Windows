using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>
/// Lightweight SQLite planner and WAL maintenance. Accounting data remains in the
/// authoritative database; these operations only checkpoint SQLite's transient WAL
/// and update planner statistics. They never delete accounting rows.
/// </summary>
public static class SqliteDatabaseOptimizer
{
    private static int _ran;

    public static void OptimizeOnce()
    {
        if (Interlocked.Exchange(ref _ran, 1) != 0) return;
        try
        {
            ApplicationDataPaths.EnsureFolders();
            using var connection = Open(SqliteOpenMode.ReadWriteCreate);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_autocheckpoint=1000; PRAGMA wal_checkpoint(PASSIVE); PRAGMA optimize=0x10002;";
            command.ExecuteNonQuery();
        }
        catch
        {
            // Optional maintenance must never prevent application startup.
        }
    }

    public static void CheckpointAndTruncateWal()
    {
        try
        {
            if (!File.Exists(ApplicationDataPaths.DatabasePath)) return;
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA optimize;";
            command.ExecuteNonQuery();
        }
        catch
        {
            // A busy database can decline a checkpoint. SQLite remains authoritative
            // and the next startup/clean shutdown will try again.
        }
    }

    private static SqliteConnection Open(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private
        }.ToString());
        connection.Open();
        return connection;
    }
}
