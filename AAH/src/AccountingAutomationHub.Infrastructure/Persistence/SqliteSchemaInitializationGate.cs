using System;
using System.Collections.Concurrent;

namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>
/// Prevents the same repository schema/migration pass from running repeatedly in one
/// process. The key also contains the connection string so alternate/test databases
/// remain independent. A failed initialization is not marked complete and can retry.
/// </summary>
public static class SqliteSchemaInitializationGate
{
    private sealed class GateState
    {
        public object SyncRoot { get; } = new();
        public bool Completed { get; set; }
    }

    private static readonly ConcurrentDictionary<string, GateState> Gates =
        new(StringComparer.Ordinal);

    public static void RunOnce(string schemaName, string connectionString, Action initialize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentNullException.ThrowIfNull(initialize);

        var key = schemaName + "|" + connectionString;
        var state = Gates.GetOrAdd(key, static _ => new GateState());
        if (state.Completed) return;

        lock (state.SyncRoot)
        {
            if (state.Completed) return;
            using var measurement = ApplicationPerformanceTrace.Measure("SQLite.Schema.Initialize", schemaName);
            initialize();
            state.Completed = true;
        }
    }
}
