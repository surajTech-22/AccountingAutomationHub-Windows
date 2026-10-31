using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Infrastructure.Clients;

/// <summary>
/// Read-only snapshot of precisely scoped bank/sales/purchase/cash/journal
/// row counts. A consolidated Dashboard must never show parent historical
/// rows as if they came from one of the private branches. Unassigned legacy
/// parent records remain visible as their own reconciliation bucket.
/// </summary>
public sealed class SqliteConsolidatedBranchReadbackRepository
{
    private readonly string _connectionString;
    public SqliteConsolidatedBranchReadbackRepository() : this(ApplicationDataPaths.DatabasePath) { }
    public SqliteConsolidatedBranchReadbackRepository(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Accounting database required.", nameof(dbPath));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }
    public ConsolidatedBranchReadback Read(long parentClientId, string financialYear)
    {
        if (parentClientId <= 0 || string.IsNullOrWhiteSpace(financialYear))
            throw new InvalidOperationException("Saved parent Client and financial year required.");
        using var db = SqliteConnectionPolicy.Open(_connectionString);
        using var parent = db.CreateCommand();
        parent.CommandText =
            "SELECT FinancialYear FROM Clients WHERE Id=$Id AND IsArchived=0 AND NOT EXISTS (" +
            "SELECT 1 FROM InternalBranchAccounts WHERE ChildClientId=Clients.Id);";
        parent.Parameters.AddWithValue("$Id", parentClientId);
        var parentFy = parent.ExecuteScalar()?.ToString() ?? "";
        if (!parentFy.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Parent Client/FY identity is not valid for consolidation.");

        var children = new List<(long ClientId, string BranchId, string Name)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT m.ChildClientId, m.BranchId, c.ClientName
                FROM InternalBranchAccounts m JOIN Clients c ON c.Id=m.ChildClientId
                WHERE m.ParentClientId=$P AND c.FinancialYear=$FY COLLATE NOCASE AND c.IsArchived=0
                ORDER BY c.ClientName COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("$P", parentClientId);
            cmd.Parameters.AddWithValue("$FY", financialYear.Trim());
            using var read = cmd.ExecuteReader();
            while (read.Read()) children.Add((read.GetInt64(0), read.GetString(1), read.GetString(2)));
        }

        BranchLedgerReadbackRow BuildRow(long clientId, string scopeId, string name, bool legacy) => new(
            clientId, scopeId, name, legacy,
            Count(db, "SalesRecords", clientId, financialYear),
            Count(db, "PurchaseRecords", clientId, financialYear),
            Count(db, "BankTransactions", clientId, financialYear),
            Count(db, "CashEntries", clientId, financialYear),
            Count(db, "JournalVouchers", clientId, financialYear));

        var rows = new List<BranchLedgerReadbackRow>();
        foreach (var child in children)
            rows.Add(BuildRow(child.ClientId, child.BranchId, child.Name, false));
        var unassignedParent = BuildRow(parentClientId, "LEGACY-UNASSIGNED",
            "Parent historical records — not assigned to a branch", true);
        if (unassignedParent.HasAnyRecords || children.Count == 0)
            rows.Add(unassignedParent);

        return new ConsolidatedBranchReadback(parentClientId, financialYear, rows,
            rows.Any(row => row.IsLegacyUnassigned && row.HasAnyRecords)
                ? "Historical parent records remain unassigned. Do not include them in branch totals until reconciled."
                : "Verified saved branch Client/FY ownership only. Counts are not Tally ledger balances or a profit calculation.");
    }

    private static int? Count(SqliteConnection db, string table, long clientId, string financialYear)
    {
        // Fixed internal whitelist. Never accept a table name from user data.
        if (table is not ("SalesRecords" or "PurchaseRecords" or "BankTransactions" or
                         "CashEntries" or "JournalVouchers"))
            throw new InvalidOperationException("Unknown accounting readback table.");
        using (var exists = db.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$T;";
            exists.Parameters.AddWithValue("$T", table);
            if (exists.ExecuteScalar() is null) return null;
        }
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table +
                              " WHERE ClientId=$C AND FinancialYear=$FY COLLATE NOCASE;";
        command.Parameters.AddWithValue("$C", clientId);
        command.Parameters.AddWithValue("$FY", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

public sealed record BranchLedgerReadbackRow(
    long ClientId, string ScopeId, string Name, bool IsLegacyUnassigned,
    int? Sales, int? Purchases, int? Banking, int? Cash, int? Journals)
{
    public bool HasAnyRecords => (Sales ?? 0) + (Purchases ?? 0) + (Banking ?? 0) +
                                 (Cash ?? 0) + (Journals ?? 0) > 0;
    public string SalesDisplay => Sales?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string PurchaseDisplay => Purchases?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string BankDisplay => Banking?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string CashDisplay => Cash?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string JournalDisplay => Journals?.ToString(CultureInfo.InvariantCulture) ?? "—";
}

public sealed record ConsolidatedBranchReadback(
    long ParentClientId, string FinancialYear,
    IReadOnlyList<BranchLedgerReadbackRow> Branches, string Warning);
