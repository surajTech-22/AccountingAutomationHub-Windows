using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>
/// Caches PRAGMA table_info results for the lifetime of one migration connection.
/// Repositories can request many legacy columns without rescanning the same table for
/// every individual column.
/// </summary>
public static class SqliteSchemaColumnCache
{
    private sealed class ConnectionSchemaCache
    {
        public ConnectionSchemaCache() { }

        public Dictionary<string, HashSet<string>> ColumnsByTable { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly ConditionalWeakTable<SqliteConnection, ConnectionSchemaCache> Cache = new();

    public static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition)
    {
        var connectionCache = Cache.GetOrCreateValue(connection);
        if (!connectionCache.ColumnsByTable.TryGetValue(tableName, out var columns))
        {
            columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var inspect = connection.CreateCommand();
            inspect.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = inspect.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
            connectionCache.ColumnsByTable[tableName] = columns;
        }

        if (columns.Contains(columnName)) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
        columns.Add(columnName);
    }
}
