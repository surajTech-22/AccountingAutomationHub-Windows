using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Governance;

/// <summary>
/// Reverses only the exact accounting entity IDs captured when an Import Job reached
/// Verified. Path names are never re-evaluated during Undo, so replacing a source file
/// later cannot make an older Import Run delete newer accounting data.
/// </summary>
public sealed class ImportUndoService(SqliteGovernanceRepository governance)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = ApplicationDataPaths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private
    }.ToString();

    public ImportUndoPreview Preview(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return new ImportUndoPreview { Warning = "Select an Import Run first." };

        using var connection = OpenConnection();
        var preview = new ImportUndoPreview { RunId = runId.Trim() };
        preview.BankStatements = Scalar(connection,
            "SELECT COUNT(*) FROM BankStatements WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='BankStatement');", runId);
        preview.BankTransactions = Scalar(connection,
            "SELECT COUNT(*) FROM BankTransactions WHERE StatementId IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='BankStatement');", runId);
        preview.SalesRecords = Scalar(connection,
            "SELECT COUNT(*) FROM SalesRecords WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='SalesRecord');", runId);
        preview.PurchaseRecords = Scalar(connection,
            "SELECT COUNT(*) FROM PurchaseRecords WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='PurchaseRecord');", runId);
        preview.CashEntries = Scalar(connection,
            "SELECT COUNT(*) FROM CashEntries WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='CashEntry');", runId);
        preview.JournalVouchers = Scalar(connection,
            "SELECT COUNT(*) FROM JournalVouchers WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='JournalVoucher');", runId);
        preview.Warning = preview.TotalRecords == 0
            ? "This run has no captured reversible accounting entities. Nothing will be deleted."
            : "Undo uses the immutable entity-ID snapshot captured when this Import Run was verified. Masters, mappings, Settings, other imports, source documents and external Python files are never deleted.";
        return preview;
    }

    public ImportUndoPreview Undo(string runId, string reason)
    {
        var preview = Preview(runId);
        if (preview.TotalRecords == 0) return preview;
        var run = GetRun(runId) ?? throw new InvalidOperationException("The selected Import Run no longer exists.");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction,
            "DELETE FROM BankStatements WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='BankStatement');", runId);
        Execute(connection, transaction,
            "DELETE FROM SalesRecords WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='SalesRecord');", runId);
        Execute(connection, transaction,
            "DELETE FROM PurchaseRecords WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='PurchaseRecord');", runId);
        Execute(connection, transaction,
            "DELETE FROM CashEntries WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='CashEntry');", runId);
        Execute(connection, transaction,
            "DELETE FROM JournalVouchers WHERE Id IN (SELECT CAST(e.EntityKey AS INTEGER) FROM ImportJobEntities e JOIN ImportJobs j ON j.JobId=e.JobId WHERE j.RunId=$RunId AND e.EntityType='JournalVoucher');", runId);
        ExecuteClientFy(connection, transaction,
            "DELETE FROM SalesImportBatches WHERE ClientId=$ClientId AND FinancialYear=$Fy AND NOT EXISTS(SELECT 1 FROM SalesRecords WHERE SalesRecords.BatchId=SalesImportBatches.Id);",
            run.ClientId, run.FinancialYear);
        ExecuteClientFy(connection, transaction,
            "DELETE FROM PurchaseImportBatches WHERE ClientId=$ClientId AND FinancialYear=$Fy AND NOT EXISTS(SELECT 1 FROM PurchaseRecords WHERE PurchaseRecords.BatchId=PurchaseImportBatches.Id);",
            run.ClientId, run.FinancialYear);
        transaction.Commit();

        governance.RecordVersion(new EntityVersionRecord
        {
            ClientId = run.ClientId,
            FinancialYear = run.FinancialYear,
            Module = "Import Job Center",
            EntityType = "ImportRun",
            EntityKey = runId,
            Operation = "Undo Import",
            BeforeJson = $"{{\"records\":{preview.TotalRecords}}}",
            AfterJson = "{\"state\":\"Rolled Back\"}",
            RunId = runId,
            Reason = reason,
            ChangedBy = Environment.UserName,
            ChangedUtc = DateTime.UtcNow
        });
        governance.MarkRunRolledBack(runId, $"Rolled back {preview.TotalRecords:N0} accounting record(s). {reason}".Trim());
        return preview;
    }

    private ImportRunRecord? GetRun(string runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RunId,ClientId,FinancialYear,SourceKind,RequestedBy,Status,TotalFiles,ImportedFiles,DuplicateFiles,ReviewFiles,FailedFiles,RecordCount,IsUndoable,StartedUtc,UpdatedUtc,CompletedUtc,Summary FROM ImportRuns WHERE RunId=$RunId LIMIT 1;";
        command.Parameters.AddWithValue("$RunId", runId.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ImportRunRecord
        {
            RunId=reader.GetString(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),SourceKind=reader.GetString(3),RequestedBy=reader.GetString(4),Status=reader.GetString(5),TotalFiles=reader.GetInt32(6),ImportedFiles=reader.GetInt32(7),DuplicateFiles=reader.GetInt32(8),ReviewFiles=reader.GetInt32(9),FailedFiles=reader.GetInt32(10),RecordCount=reader.GetInt32(11),IsUndoable=reader.GetInt32(12)==1
        };
    }

    private static int Scalar(SqliteConnection connection, string sql, string runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$RunId", runId.Trim());
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$RunId", runId.Trim());
        command.ExecuteNonQuery();
    }

    private static void ExecuteClientFy(SqliteConnection connection, SqliteTransaction transaction, string sql, long clientId, string financialYear)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }
}
