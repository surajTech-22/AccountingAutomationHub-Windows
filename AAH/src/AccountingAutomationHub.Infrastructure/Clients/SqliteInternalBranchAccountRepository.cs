using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Clients;

/// <summary>
/// Durable, private branch ClientId authority for the already existing
/// ClientId/FY-keyed accounting repositories. This is not a second Directory
/// client: SqliteClientRepository.GetAll excludes these records, so ordinary
/// GST-source discovery never imports the same registration twice.
/// </summary>
public sealed class SqliteInternalBranchAccountRepository
{
    private readonly string _connectionString;
    private readonly SqliteClientRepository _clients;

    public SqliteInternalBranchAccountRepository()
        : this(ApplicationDataPaths.DatabasePath) { }

    public SqliteInternalBranchAccountRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Accounting database is required.", nameof(databasePath));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        _clients = new SqliteClientRepository(databasePath);
    }

    public void Initialize() => _clients.Initialize();

    public InternalBranchAccountLink? FindByChild(long clientId)
    {
        if (clientId <= 0) return null;
        Initialize();
        using var connection = SqliteConnectionPolicy.Open(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ParentClientId,BranchId,ChildClientId FROM InternalBranchAccounts WHERE ChildClientId=$Id;";
        cmd.Parameters.AddWithValue("$Id", clientId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetInt64(0),r.GetString(1),r.GetInt64(2)) : null;
    }

    public IReadOnlyList<InternalBranchAccountLink> FindByParent(long parentClientId)
    {
        Initialize();
        using var connection = SqliteConnectionPolicy.Open(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ParentClientId,BranchId,ChildClientId FROM InternalBranchAccounts WHERE ParentClientId=$Id ORDER BY BranchId;";
        cmd.Parameters.AddWithValue("$Id", parentClientId);
        using var r = cmd.ExecuteReader();
        var links = new List<InternalBranchAccountLink>();
        while (r.Read()) links.Add(new(r.GetInt64(0),r.GetString(1),r.GetInt64(2)));
        return links;
    }

    /// <summary>
    /// Explicit, idempotent creation after a saved branch is selected.
    /// Never migrates parent postings, creates a Tally company or maps a bank
    /// transaction merely from a GSTIN. All real accounting rows remain
    /// isolated under the returned ChildClientId; legacy parent rows remain
    /// untouched for explicit source-backed migration later.
    /// </summary>
    public ClientProfile GetOrCreate(long parentClientId, string branchId,
        string branchName, string? registrationGstin)
    {
        if (parentClientId <= 0) throw new ArgumentException("Saved parent Client required.", nameof(parentClientId));
        if (string.IsNullOrWhiteSpace(branchId) || branchId.Length > 80 ||
            branchId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new InvalidOperationException("Invalid or missing durable branch ID.");
        branchName = (branchName ?? "").Trim();
        if (branchName.Length is < 1 or > 100)
            throw new InvalidOperationException("Branch name must be 1 to 100 characters.");
        var gstin = new string((registrationGstin ?? "")
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (gstin.Length != 0 && (gstin.Length != 15 ||
            !char.IsDigit(gstin[0]) || !char.IsDigit(gstin[1])))
            throw new InvalidOperationException("Invalid branch GST registration.");

        Initialize();
        using var connection = SqliteConnectionPolicy.Open(_connectionString);
        using var transaction = connection.BeginTransaction();

        // Read the source parent inside the same transaction so a stale UI
        // cannot attach an unrelated company's tax registration to this branch.
        string parentName, parentPan, parentFy, parentGstin, parentState;
        using (var readParent = connection.CreateCommand())
        {
            readParent.Transaction = transaction;
            readParent.CommandText =
                "SELECT ClientName,Pan,FinancialYear,Gstin,State FROM Clients WHERE Id=$Id AND IsArchived=0 AND NOT EXISTS (" +
                "SELECT 1 FROM InternalBranchAccounts WHERE ChildClientId=Clients.Id);";
            readParent.Parameters.AddWithValue("$Id", parentClientId);
            using var r = readParent.ExecuteReader();
            if (!r.Read())
                throw new InvalidOperationException("Only an active top-level Client can own a branch account.");
            parentName = r.GetString(0);
            parentPan = r.GetString(1).Trim().ToUpperInvariant();
            parentFy = r.GetString(2);
            parentGstin = r.GetString(3).Trim().ToUpperInvariant();
            parentState = r.GetString(4);
        }
        if (string.IsNullOrWhiteSpace(parentFy))
            throw new InvalidOperationException("Parent Client must have a financial year.");

        var effectivePan = parentPan.Length == 10 ? parentPan :
            parentGstin.Length == 15 ? parentGstin.Substring(2, 10) : "";
        if (gstin.Length == 15)
        {
            if (effectivePan.Length != 10 || !gstin.Substring(2, 10).Equals(effectivePan, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Branch GSTIN PAN does not match parent Client.");
            using var registered = connection.CreateCommand();
            registered.Transaction = transaction;
            registered.CommandText =
                "SELECT 1 FROM Clients p WHERE p.Id=$Parent AND (p.Gstin=$G COLLATE NOCASE " +
                "OR EXISTS (SELECT 1 FROM ClientAdditionalGstins x WHERE x.ClientId=p.Id AND x.Gstin=$G COLLATE NOCASE));";
            registered.Parameters.AddWithValue("$Parent", parentClientId);
            registered.Parameters.AddWithValue("$G", gstin);
            if (registered.ExecuteScalar() is null)
                throw new InvalidOperationException("Branch GSTIN is not registered to the parent Client.");
        }
        long childId;
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT ChildClientId FROM InternalBranchAccounts WHERE ParentClientId=$P AND BranchId=$B;";
            existing.Parameters.AddWithValue("$P", parentClientId);
            existing.Parameters.AddWithValue("$B", branchId);
            var value = existing.ExecuteScalar();
            childId = value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (childId != 0)
        {
            using var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandText =
                "SELECT Gstin,FinancialYear,Pan,IsArchived FROM Clients WHERE Id=$Id;";
            verify.Parameters.AddWithValue("$Id", childId);
            using var r = verify.ExecuteReader();
            if (!r.Read() || r.GetInt32(3) != 0 ||
                !r.GetString(0).Equals(gstin, StringComparison.OrdinalIgnoreCase) ||
                !r.GetString(1).Equals(parentFy, StringComparison.OrdinalIgnoreCase) ||
                !r.GetString(2).Equals(effectivePan, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Existing private branch identity has changed; manual reconciliation required, no automatic reassignment.");
        }
        else
        {
            if (gstin.Length != 0)
            {
                using var existingGstin = connection.CreateCommand();
                existingGstin.Transaction = transaction;
                existingGstin.CommandText =
                    "SELECT 1 FROM InternalBranchAccounts b JOIN Clients c ON c.Id=b.ChildClientId " +
                    "WHERE b.ParentClientId=$P AND c.Gstin=$G COLLATE NOCASE LIMIT 1;";
                existingGstin.Parameters.AddWithValue("$P", parentClientId);
                existingGstin.Parameters.AddWithValue("$G", gstin);
                if (existingGstin.ExecuteScalar() is not null)
                    throw new InvalidOperationException("GST registration already assigned to another private branch.");
            }
            var rawIdentity = parentClientId.ToString(CultureInfo.InvariantCulture) + "|" + branchId.ToUpperInvariant();
            var code = "BR" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawIdentity)))[..18];
            var utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO Clients (ClientCode,ClientName,LegalName,TradeName,Gstin,Pan,State,FinancialYear,CreatedUtc,UpdatedUtc) " +
                    "VALUES ($Code,$Name,$Legal,$Trade,$G,$Pan,$State,$FY,$UTC,$UTC); SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("$Code", code);
                insert.Parameters.AddWithValue("$Name", parentName + " / " + branchName);
                insert.Parameters.AddWithValue("$Legal", parentName);
                insert.Parameters.AddWithValue("$Trade", branchName);
                insert.Parameters.AddWithValue("$G", gstin);
                insert.Parameters.AddWithValue("$Pan", effectivePan);
                insert.Parameters.AddWithValue("$State", parentState);
                insert.Parameters.AddWithValue("$FY", parentFy);
                insert.Parameters.AddWithValue("$UTC", utc);
                childId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            using var map = connection.CreateCommand();
            map.Transaction = transaction;
            map.CommandText =
                "INSERT INTO InternalBranchAccounts(ParentClientId,BranchId,ChildClientId,CreatedUtc) " +
                "VALUES($P,$B,$C,$UTC);";
            map.Parameters.AddWithValue("$P", parentClientId);
            map.Parameters.AddWithValue("$B", branchId);
            map.Parameters.AddWithValue("$C", childId);
            map.Parameters.AddWithValue("$UTC", utc);
            map.ExecuteNonQuery();
        }
        transaction.Commit();
        return _clients.GetById(childId) ??
            throw new InvalidOperationException("Saved branch accounting record could not be read back.");
    }
}

public sealed record InternalBranchAccountLink(
    long ParentClientId, string BranchId, long ChildClientId);
