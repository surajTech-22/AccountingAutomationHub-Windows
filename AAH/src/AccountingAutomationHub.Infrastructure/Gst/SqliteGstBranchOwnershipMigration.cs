using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Gst;

/// <summary>
/// Transactional transfer of existing GSTIN-owned data from the historical
/// Client/FY into the already saved, private branch Client/FY. The caller
/// supplies a confirmed branch identity from InternalBranchAccounts.
/// Never guesses an owner from a filename/state/name and never creates a
/// second GST/Tally import. Repeated calls are idempotent.
/// </summary>
public sealed class SqliteGstBranchOwnershipMigration
{
    private readonly string _connectionString;

    public SqliteGstBranchOwnershipMigration()
        : this(ApplicationDataPaths.DatabasePath) { }

    public SqliteGstBranchOwnershipMigration(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public GstBranchMigrationResult Migrate(
        long parentId, long branchId, string financialYear, string gstin)
    {
        var normalized = new string((gstin ?? "").Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray());
        if (parentId <= 0 || branchId <= 0 || parentId == branchId ||
            normalized.Length != 15 || string.IsNullOrWhiteSpace(financialYear))
            throw new InvalidOperationException("Exact parent, branch, FY and GSTIN authority is required.");

        using var connection = SqliteConnectionPolicy.Open(_connectionString);
        using var transaction = connection.BeginTransaction();
        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText =
                """
                SELECT COUNT(*)
                FROM InternalBranchAccounts a
                JOIN Clients parent ON parent.Id=a.ParentClientId
                JOIN Clients child ON child.Id=a.ChildClientId
                WHERE a.ParentClientId=$Parent AND a.ChildClientId=$Child
                  AND parent.FinancialYear=$Fy AND child.FinancialYear=$Fy
                  AND UPPER(child.Gstin)=$Gstin
                  AND parent.IsArchived=0 AND child.IsArchived=0;
                """;
            AddScope(verify, parentId, branchId, financialYear, normalized);
            if (Convert.ToInt32(verify.ExecuteScalar()) != 1)
                throw new InvalidOperationException(
                    "GST ownership transfer blocked: persisted branch identity, FY or GSTIN does not match.");
        }

        // Retain all source fields, row IDs, edits, fingerprints and imported
        // timestamps. A branch row with the same GSTIN+period/source key is a
        // conflict: never overwrite it or silently delete the historical row.
        var returns = MoveNonconflicting(connection, transaction,
            "Gstr3bReturns",
            "AND NOT EXISTS (SELECT 1 FROM Gstr3bReturns dst WHERE dst.ClientId=$Child AND dst.FinancialYear=$Fy AND UPPER(dst.Gstin)=$Gstin AND dst.PeriodKey=src.PeriodKey)",
            parentId, branchId, financialYear, normalized);
        var ledger = MoveNonconflicting(connection, transaction,
            "GstLedgerPeriods",
            "AND NOT EXISTS (SELECT 1 FROM GstLedgerPeriods dst WHERE dst.ClientId=$Child AND dst.FinancialYear=$Fy AND UPPER(dst.Gstin)=$Gstin AND dst.LedgerType=src.LedgerType AND dst.PeriodKey=src.PeriodKey)",
            parentId, branchId, financialYear, normalized);
        var sources = MoveNonconflicting(connection, transaction,
            "GstPortalSourceState",
            "AND NOT EXISTS (SELECT 1 FROM GstPortalSourceState dst WHERE dst.ClientId=$Child AND dst.FinancialYear=$Fy AND dst.SourceType=src.SourceType AND dst.SourcePath=src.SourcePath COLLATE NOCASE)",
            parentId, branchId, financialYear, normalized);
        var bootstraps = MoveNonconflicting(connection, transaction,
            "GstBootstrapState",
            "AND NOT EXISTS (SELECT 1 FROM GstBootstrapState dst WHERE dst.ClientId=$Child AND dst.FinancialYear=$Fy AND UPPER(dst.Gstin)=$Gstin)",
            parentId, branchId, financialYear, normalized);
        var conflicts = 0;
        foreach (var table in new[] { "Gstr3bReturns", "GstLedgerPeriods", "GstPortalSourceState", "GstBootstrapState" })
        {
            using var remaining = connection.CreateCommand();
            remaining.Transaction = transaction;
            remaining.CommandText = $"SELECT COUNT(*) FROM {table} WHERE ClientId=$Parent AND FinancialYear=$Fy AND UPPER(Gstin)=$Gstin;";
            remaining.Parameters.AddWithValue("$Parent", parentId);
            remaining.Parameters.AddWithValue("$Fy", financialYear);
            remaining.Parameters.AddWithValue("$Gstin", normalized);
            conflicts += Convert.ToInt32(remaining.ExecuteScalar());
        }
        // GST reconciliation is period-level without GSTIN ownership.
        // It cannot safely be moved until recomputed from branch-owned source
        // records. Preserve it at parent instead of double counting.
        using var reconciliation = connection.CreateCommand();
        reconciliation.Transaction = transaction;
        reconciliation.CommandText =
            "SELECT COUNT(*) FROM GstReconciliation WHERE ClientId=$Parent AND FinancialYear=$Fy;";
        reconciliation.Parameters.AddWithValue("$Parent", parentId);
        reconciliation.Parameters.AddWithValue("$Fy", financialYear);
        var pendingReconciliation = Convert.ToInt32(reconciliation.ExecuteScalar());
        transaction.Commit();
        return new GstBranchMigrationResult(returns, ledger, sources, bootstraps, conflicts,
            pendingReconciliation);
    }

    /// <summary>
    /// Rebase exact source-evidence paths after a marker/GSTIN-verified
    /// same-volume folder move. This edits only already migrated child
    /// records, never the parent's unassigned records.
    /// </summary>
    public int RebaseMovedEvidencePaths(long parentId, long branchId,
        string financialYear, string gstin, string oldFolder, string newFolder)
    {
        if (string.IsNullOrWhiteSpace(oldFolder) || string.IsNullOrWhiteSpace(newFolder))
            throw new ArgumentException("Both folder paths are required.");
        var oldPrefix = Path.GetFullPath(oldFolder).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var newPrefix = Path.GetFullPath(newFolder).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var normalized = new string(gstin.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray());
        using var connection = SqliteConnectionPolicy.Open(_connectionString);
        using var tx = connection.BeginTransaction();
        using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = """
                SELECT COUNT(*) FROM InternalBranchAccounts a
                JOIN Clients child ON child.Id=a.ChildClientId
                WHERE a.ParentClientId=$Parent AND a.ChildClientId=$Child
                  AND child.FinancialYear=$Fy AND UPPER(child.Gstin)=$Gstin;
                """;
            AddScope(check, parentId, branchId, financialYear, normalized);
            if (Convert.ToInt32(check.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Cannot rebase GST sources without exact saved branch ownership.");
        }
        var updated = 0;
        foreach (var table in new[] { "Gstr3bReturns", "GstLedgerPeriods", "GstPortalSourceState" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $"""
                UPDATE {table}
                SET SourcePath=$NewPrefix || substr(SourcePath,length($OldPrefix)+1)
                WHERE ClientId=$Child AND FinancialYear=$Fy AND UPPER(Gstin)=$Gstin
                  AND substr(SourcePath,1,length($OldPrefix))=$OldPrefix COLLATE NOCASE;
                """;
            AddScope(command, parentId, branchId, financialYear, normalized);
            command.Parameters.AddWithValue("$OldPrefix", oldPrefix);
            command.Parameters.AddWithValue("$NewPrefix", newPrefix);
            updated += command.ExecuteNonQuery();
        }
        tx.Commit();
        return updated;
    }

    private static int MoveNonconflicting(SqliteConnection connection, SqliteTransaction transaction,
        string table, string exclusion, long parentId, long branchId,
        string financialYear, string gstin)
    {
        // Table names and exclusions are internal constant SQL, not user input.
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            UPDATE {table} AS src
            SET ClientId=$Child
            WHERE src.ClientId=$Parent AND src.FinancialYear=$Fy AND UPPER(src.Gstin)=$Gstin
              {exclusion};
            """;
        AddScope(cmd, parentId, branchId, financialYear, gstin);
        return cmd.ExecuteNonQuery();
    }

    private static void AddScope(SqliteCommand cmd, long parentId, long branchId,
        string financialYear, string gstin)
    {
        cmd.Parameters.AddWithValue("$Parent", parentId);
        cmd.Parameters.AddWithValue("$Child", branchId);
        cmd.Parameters.AddWithValue("$Fy", financialYear);
        cmd.Parameters.AddWithValue("$Gstin", gstin);
    }
}

public sealed record GstBranchMigrationResult(
    int ReturnsMoved, int LedgerPeriodsMoved, int PortalSourcesMoved,
    int BootstrapStatesMoved, int UnresolvedConflicts, int ParentReconciliationRows);
