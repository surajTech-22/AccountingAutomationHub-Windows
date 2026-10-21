using System.Globalization;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Tally;

public sealed class SqliteTallyRepository
{
    private readonly string _connectionString;
    private readonly string _auditConnectionString;

    public SqliteTallyRepository()
    {
        ApplicationDataPaths.EnsureFolders();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        _auditConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.AuditDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteTallyRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS TallyClientLinks
            (
                Id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId                    INTEGER NOT NULL UNIQUE,
                CompanyKey                  TEXT NOT NULL UNIQUE COLLATE NOCASE,
                CompanyName                 TEXT NOT NULL DEFAULT '',
                CompanyNumber               TEXT NOT NULL DEFAULT '',
                Gstin                       TEXT NOT NULL DEFAULT '',
                StateName                   TEXT NOT NULL DEFAULT '',
                BooksFrom                   TEXT NOT NULL DEFAULT '',
                StartingFrom                TEXT NOT NULL DEFAULT '',
                FinancialYear               TEXT NOT NULL DEFAULT '',
                LinkedUtc                   TEXT NOT NULL,
                UpdatedUtc                  TEXT NOT NULL,
                LastSeenUtc                 TEXT NOT NULL DEFAULT '',
                LastMasterSyncUtc            TEXT NOT NULL DEFAULT '',
                LastReportRefreshUtc         TEXT NOT NULL DEFAULT '',
                LastChangeToken              TEXT NOT NULL DEFAULT '',
                GroupCount                   INTEGER NOT NULL DEFAULT 0,
                LedgerCount                  INTEGER NOT NULL DEFAULT 0,
                VoucherTypeCount             INTEGER NOT NULL DEFAULT 0,
                StockItemCount               INTEGER NOT NULL DEFAULT 0,
                UnitCount                    INTEGER NOT NULL DEFAULT 0,
                GodownCount                  INTEGER NOT NULL DEFAULT 0,
                VoucherCount                 INTEGER NOT NULL DEFAULT 0,
                MasterCount                  INTEGER NOT NULL DEFAULT 0,
                DuplicateMasterCount         INTEGER NOT NULL DEFAULT 0,
                ConflictingMasterCount       INTEGER NOT NULL DEFAULT 0,
                DuplicateVoucherCount        INTEGER NOT NULL DEFAULT 0,
                ConflictingVoucherCount      INTEGER NOT NULL DEFAULT 0,
                BankImportFrom               TEXT NOT NULL DEFAULT '',
                BankImportTo                 TEXT NOT NULL DEFAULT '',
                LastSyncStatus               TEXT NOT NULL DEFAULT '',
                LastError                    TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_TallyClientLinks_GstinFy
            ON TallyClientLinks(Gstin COLLATE NOCASE, FinancialYear COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS TallyVoucherIndex
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                CompanyKey          TEXT NOT NULL COLLATE NOCASE,
                FinancialYear       TEXT NOT NULL DEFAULT '',
                MasterId            TEXT NOT NULL DEFAULT '',
                AlterId             TEXT NOT NULL DEFAULT '',
                Guid                TEXT NOT NULL DEFAULT '',
                VoucherDate         TEXT NOT NULL DEFAULT '',
                VoucherType         TEXT NOT NULL DEFAULT '',
                VoucherNumber       TEXT NOT NULL DEFAULT '',
                Reference           TEXT NOT NULL DEFAULT '',
                PartyLedger         TEXT NOT NULL DEFAULT '',
                DebitLedgers        TEXT NOT NULL DEFAULT '',
                CreditLedgers       TEXT NOT NULL DEFAULT '',
                Amount              REAL NOT NULL DEFAULT 0,
                Narration           TEXT NOT NULL DEFAULT '',
                ContentHash         TEXT NOT NULL DEFAULT '',
                LastSeenUtc         TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_TallyVoucherIndex_CompanyReference
            ON TallyVoucherIndex(CompanyKey, Reference COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS IX_TallyVoucherIndex_CompanyVoucher
            ON TallyVoucherIndex(CompanyKey, VoucherType COLLATE NOCASE, VoucherNumber COLLATE NOCASE, VoucherDate);

            CREATE TABLE IF NOT EXISTS TallyVoucherXmlAuthority
            (
                CompanyKey          TEXT NOT NULL COLLATE NOCASE,
                FinancialYear       TEXT NOT NULL DEFAULT '',
                MasterId            TEXT NOT NULL COLLATE NOCASE,
                AlterId             TEXT NOT NULL DEFAULT '',
                Guid                TEXT NOT NULL DEFAULT '',
                VoucherDate         TEXT NOT NULL DEFAULT '',
                VoucherType         TEXT NOT NULL DEFAULT '',
                OriginalXml         TEXT NOT NULL DEFAULT '',
                ContentHash         TEXT NOT NULL DEFAULT '',
                LastSeenUtc         TEXT NOT NULL,
                PRIMARY KEY (CompanyKey, FinancialYear, MasterId)
            );

            CREATE INDEX IF NOT EXISTS IX_TallyVoucherXmlAuthority_Guid
            ON TallyVoucherXmlAuthority(CompanyKey, FinancialYear, Guid COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS TallyReportAuthority
            (
                CompanyKey          TEXT NOT NULL COLLATE NOCASE,
                FinancialYear       TEXT NOT NULL DEFAULT '',
                ReportName          TEXT NOT NULL COLLATE NOCASE,
                FromDate            TEXT NOT NULL DEFAULT '',
                ToDate              TEXT NOT NULL DEFAULT '',
                AuthorityAlterId    INTEGER NOT NULL DEFAULT 0,
                SnapshotJson        TEXT NOT NULL DEFAULT '',
                RefreshedUtc        TEXT NOT NULL,
                PRIMARY KEY (CompanyKey, FinancialYear, ReportName, FromDate, ToDate)
            );

            CREATE TABLE IF NOT EXISTS TallyImportFingerprints
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                CompanyKey          TEXT NOT NULL COLLATE NOCASE,
                EntityType          TEXT NOT NULL COLLATE NOCASE,
                SourceKey           TEXT NOT NULL COLLATE NOCASE,
                ContentHash         TEXT NOT NULL COLLATE NOCASE,
                Status              TEXT NOT NULL DEFAULT 'Imported',
                ResponseSummary     TEXT NOT NULL DEFAULT '',
                FirstSeenUtc        TEXT NOT NULL,
                LastSeenUtc         TEXT NOT NULL,
                SeenCount           INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_TallyImportFingerprints_SourceHash
            ON TallyImportFingerprints(ClientId, FinancialYear, CompanyKey, EntityType, SourceKey, ContentHash);

            CREATE INDEX IF NOT EXISTS IX_TallyImportFingerprints_Source
            ON TallyImportFingerprints(ClientId, FinancialYear, CompanyKey, EntityType, SourceKey);

            CREATE TABLE IF NOT EXISTS TallySourceBindings
            (
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                CompanyKey          TEXT NOT NULL COLLATE NOCASE,
                EntityType          TEXT NOT NULL COLLATE NOCASE,
                SourceAnchor        TEXT NOT NULL COLLATE NOCASE,
                SourceKey           TEXT NOT NULL COLLATE NOCASE,
                TallyMasterId       TEXT NOT NULL DEFAULT '',
                TallyGuid           TEXT NOT NULL DEFAULT '',
                LastContentHash     TEXT NOT NULL DEFAULT '',
                PendingContentHash  TEXT NOT NULL DEFAULT '',
                LastVerifiedVersion TEXT NOT NULL DEFAULT '',
                PendingVersion      TEXT NOT NULL DEFAULT '',
                LastVerifiedAlterId INTEGER NOT NULL DEFAULT 0,
                LastVerifiedUtc     TEXT NOT NULL DEFAULT '',
                State               TEXT NOT NULL DEFAULT 'Verified',
                LastResponse        TEXT NOT NULL DEFAULT '',
                UpdatedUtc          TEXT NOT NULL,
                PRIMARY KEY (ClientId, FinancialYear, CompanyKey, EntityType, SourceAnchor),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_TallySourceBindings_MasterId
            ON TallySourceBindings(CompanyKey, FinancialYear, TallyMasterId COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS TallyObjectVersionHistory
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                CompanyKey          TEXT NOT NULL COLLATE NOCASE,
                EntityType          TEXT NOT NULL,
                SourceAnchor        TEXT NOT NULL COLLATE NOCASE,
                SourceKey           TEXT NOT NULL DEFAULT '',
                TallyMasterId       TEXT NOT NULL DEFAULT '',
                PreviousContentHash TEXT NOT NULL DEFAULT '',
                PreviousVersion     TEXT NOT NULL DEFAULT '',
                DesiredContentHash  TEXT NOT NULL DEFAULT '',
                DesiredVersion      TEXT NOT NULL DEFAULT '',
                Operation           TEXT NOT NULL DEFAULT '',
                Status              TEXT NOT NULL DEFAULT 'Pending',
                Response            TEXT NOT NULL DEFAULT '',
                CreatedUtc          TEXT NOT NULL,
                VerifiedUtc         TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_TallyObjectVersionHistory_Source
            ON TallyObjectVersionHistory(ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,Id DESC);

            CREATE TABLE IF NOT EXISTS TallyBankStatementLinks
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId                INTEGER NOT NULL,
                FinancialYear           TEXT NOT NULL,
                CompanyKey              TEXT NOT NULL COLLATE NOCASE,
                TransferMatchId         TEXT NOT NULL,
                SourceTransactionId     INTEGER NOT NULL,
                TargetTransactionId     INTEGER NOT NULL,
                TransactionDate         TEXT NOT NULL DEFAULT '',
                TallyMasterId           TEXT NOT NULL DEFAULT '',
                TallyAlterId            TEXT NOT NULL DEFAULT '',
                PostingSequence         INTEGER NOT NULL DEFAULT 0,
                ConvertedBankLedgerMasterId TEXT NOT NULL DEFAULT '',
                ConvertedBankLedgerGuid TEXT NOT NULL DEFAULT '',
                ConvertedBankLedgerAlterId INTEGER NOT NULL DEFAULT 0,
                SourceBankLedgerMasterId TEXT NOT NULL DEFAULT '',
                TargetBankLedgerMasterId TEXT NOT NULL DEFAULT '',
                OriginalVoucherType     TEXT NOT NULL DEFAULT '',
                OriginalReference       TEXT NOT NULL DEFAULT '',
                OriginalNarration       TEXT NOT NULL DEFAULT '',
                OriginalDebitLedgers    TEXT NOT NULL DEFAULT '',
                OriginalCreditLedgers   TEXT NOT NULL DEFAULT '',
                OriginalAmount          NUMERIC NOT NULL DEFAULT 0,
                FinalVoucherType        TEXT NOT NULL DEFAULT '',
                FinalDebitLedger        TEXT NOT NULL DEFAULT '',
                FinalCreditLedger       TEXT NOT NULL DEFAULT '',
                MatchEvidence           TEXT NOT NULL DEFAULT '',
                Status                  TEXT NOT NULL DEFAULT '',
                CreatedUtc              TEXT NOT NULL,
                UpdatedUtc              TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_TallyBankStatementLinks_Transfer
            ON TallyBankStatementLinks(ClientId, FinancialYear, CompanyKey, TransferMatchId);
            CREATE INDEX IF NOT EXISTS IX_TallyBankStatementLinks_Master
            ON TallyBankStatementLinks(CompanyKey, TallyMasterId COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS TallyBankSyncState
            (
                ClientId                    INTEGER NOT NULL,
                FinancialYear               TEXT NOT NULL,
                CompanyKey                  TEXT NOT NULL COLLATE NOCASE,
                LastTallyChangeToken         TEXT NOT NULL DEFAULT '',
                LastVoucherCount             INTEGER NOT NULL DEFAULT 0,
                LastFyVoucherCount           INTEGER NOT NULL DEFAULT -1,
                LastAlterId                  INTEGER NOT NULL DEFAULT 0,
                LastBankInputToken           TEXT NOT NULL DEFAULT '',
                LastFullSyncUtc              TEXT NOT NULL DEFAULT '',
                LastIncrementalSyncUtc       TEXT NOT NULL DEFAULT '',
                LastStatus                   TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (ClientId, FinancialYear, CompanyKey),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "TallyVoucherIndex", "DebitLedgers", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallyVoucherIndex", "CreditLedgers", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallyBankStatementLinks", "OriginalAmount", "NUMERIC NOT NULL DEFAULT 0");
        EnsureColumn(connection, "TallyBankStatementLinks", "TransactionDate", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallySourceBindings", "PendingContentHash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallySourceBindings", "LastVerifiedVersion", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallySourceBindings", "PendingVersion", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallySourceBindings", "LastVerifiedAlterId", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "TallySourceBindings", "LastVerifiedUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallyBankStatementLinks", "PostingSequence", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "TallyBankStatementLinks", "ConvertedBankLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallyBankStatementLinks", "ConvertedBankLedgerGuid", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallyBankStatementLinks", "ConvertedBankLedgerAlterId", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "TallyBankStatementLinks", "SourceBankLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallyBankStatementLinks", "TargetBankLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "TallyBankSyncState", "LastFyVoucherCount", "INTEGER NOT NULL DEFAULT -1");
        EnsureColumn(connection, "TallyBankSyncState", "LastStockItemCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "TallyBankSyncState", "LastStockAuthorityUtc", "TEXT NOT NULL DEFAULT ''");
    }

    public void UpsertReportAuthority(
        string companyKey,
        string financialYear,
        long authorityAlterId,
        TallyReportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TallyReportAuthority
            (CompanyKey,FinancialYear,ReportName,FromDate,ToDate,AuthorityAlterId,SnapshotJson,RefreshedUtc)
            VALUES
            ($CompanyKey,$FinancialYear,$ReportName,$FromDate,$ToDate,$AlterId,$Json,$Utc)
            ON CONFLICT(CompanyKey,FinancialYear,ReportName,FromDate,ToDate) DO UPDATE SET
                AuthorityAlterId=excluded.AuthorityAlterId,
                SnapshotJson=excluded.SnapshotJson,
                RefreshedUtc=excluded.RefreshedUtc;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$ReportName", snapshot.DisplayName.Trim());
        command.Parameters.AddWithValue("$FromDate", snapshot.FromDate.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$ToDate", snapshot.ToDate.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$AlterId", authorityAlterId);
        command.Parameters.AddWithValue("$Json", System.Text.Json.JsonSerializer.Serialize(snapshot));
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public TallyReportSnapshot? GetReportAuthority(
        string companyKey,
        string financialYear,
        string reportName,
        DateTime fromDate,
        DateTime toDate,
        long minimumAuthorityAlterId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SnapshotJson,AuthorityAlterId
            FROM TallyReportAuthority
            WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear
              AND ReportName=$ReportName COLLATE NOCASE AND FromDate=$FromDate AND ToDate=$ToDate
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$ReportName", reportName.Trim());
        command.Parameters.AddWithValue("$FromDate", fromDate.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$ToDate", toDate.Date.ToString("yyyy-MM-dd"));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(1) < minimumAuthorityAlterId) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<TallyReportSnapshot>(reader.GetString(0));
        }
        catch
        {
            return null;
        }
    }

    public int CountReportAuthorityAtAlterId(
        string companyKey, string financialYear, DateTime fromDate, DateTime toDate, long authorityAlterId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM TallyReportAuthority
            WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear
              AND FromDate=$FromDate AND ToDate=$ToDate AND AuthorityAlterId >= $AlterId;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$FromDate", fromDate.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$ToDate", toDate.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$AlterId", authorityAlterId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    public TallyBankSyncState? GetBankSyncState(long clientId, string financialYear, string companyKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ClientId,FinancialYear,CompanyKey,LastTallyChangeToken,LastVoucherCount,LastFyVoucherCount,LastAlterId,
                   LastBankInputToken,LastStockItemCount,LastStockAuthorityUtc,LastFullSyncUtc,LastIncrementalSyncUtc,LastStatus
            FROM TallyBankSyncState
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new TallyBankSyncState
        {
            ClientId = reader.GetInt64(0),
            FinancialYear = reader.GetString(1),
            CompanyKey = reader.GetString(2),
            LastTallyChangeToken = reader.GetString(3),
            LastVoucherCount = reader.GetInt32(4),
            LastFyVoucherCount = reader.GetInt32(5),
            LastAlterId = reader.GetInt64(6),
            LastBankInputToken = reader.GetString(7),
            LastStockItemCount = reader.GetInt32(8),
            LastStockAuthorityUtc = ParseDate(reader.GetString(9)),
            LastFullSyncUtc = ParseDate(reader.GetString(10)),
            LastIncrementalSyncUtc = ParseDate(reader.GetString(11)),
            LastStatus = reader.GetString(12)
        };
    }

    public void UpsertBankSyncState(TallyBankSyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TallyBankSyncState
            (ClientId,FinancialYear,CompanyKey,LastTallyChangeToken,LastVoucherCount,LastFyVoucherCount,LastAlterId,
             LastBankInputToken,LastStockItemCount,LastStockAuthorityUtc,LastFullSyncUtc,LastIncrementalSyncUtc,LastStatus)
            VALUES
            ($ClientId,$FinancialYear,$CompanyKey,$Token,$VoucherCount,$FyVoucherCount,$AlterId,$BankToken,$StockItemCount,$StockAuthorityUtc,$FullUtc,$IncrementalUtc,$Status)
            ON CONFLICT(ClientId,FinancialYear,CompanyKey) DO UPDATE SET
                LastTallyChangeToken=excluded.LastTallyChangeToken,
                LastVoucherCount=excluded.LastVoucherCount,
                LastFyVoucherCount=CASE WHEN excluded.LastFyVoucherCount < 0 THEN TallyBankSyncState.LastFyVoucherCount ELSE excluded.LastFyVoucherCount END,
                LastAlterId=excluded.LastAlterId,
                LastBankInputToken=excluded.LastBankInputToken,
                LastStockItemCount=CASE WHEN excluded.LastStockAuthorityUtc='' THEN TallyBankSyncState.LastStockItemCount ELSE excluded.LastStockItemCount END,
                LastStockAuthorityUtc=CASE WHEN excluded.LastStockAuthorityUtc='' THEN TallyBankSyncState.LastStockAuthorityUtc ELSE excluded.LastStockAuthorityUtc END,
                LastFullSyncUtc=excluded.LastFullSyncUtc,
                LastIncrementalSyncUtc=excluded.LastIncrementalSyncUtc,
                LastStatus=excluded.LastStatus;
            """;
        command.Parameters.AddWithValue("$ClientId", state.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", state.FinancialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", state.CompanyKey.Trim());
        command.Parameters.AddWithValue("$Token", state.LastTallyChangeToken.Trim());
        command.Parameters.AddWithValue("$VoucherCount", state.LastVoucherCount);
        command.Parameters.AddWithValue("$FyVoucherCount", state.LastFyVoucherCount);
        command.Parameters.AddWithValue("$AlterId", state.LastAlterId);
        command.Parameters.AddWithValue("$BankToken", state.LastBankInputToken.Trim());
        command.Parameters.AddWithValue("$StockItemCount", Math.Max(0, state.LastStockItemCount));
        command.Parameters.AddWithValue("$StockAuthorityUtc", state.LastStockAuthorityUtc == default ? string.Empty : state.LastStockAuthorityUtc.ToString("O"));
        command.Parameters.AddWithValue("$FullUtc", state.LastFullSyncUtc == default ? string.Empty : state.LastFullSyncUtc.ToString("O"));
        command.Parameters.AddWithValue("$IncrementalUtc", state.LastIncrementalSyncUtc == default ? string.Empty : state.LastIncrementalSyncUtc.ToString("O"));
        command.Parameters.AddWithValue("$Status", state.LastStatus.Trim());
        command.ExecuteNonQuery();
    }

    // FIXED41: Bank/Inter-Bank workflows share this row for their input fingerprint,
    // but they must never advance or roll back the company-wide Tally authority fields.
    // This atomic narrow update leaves ALTERID, Master/Voucher counts and authority
    // timestamps exclusively owned by TallyLiveConnectionCoordinator.
    public void UpsertBankInputCheckpoint(
        long clientId,
        string financialYear,
        string companyKey,
        string bankInputToken,
        string status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TallyBankSyncState
            (ClientId,FinancialYear,CompanyKey,LastBankInputToken,LastStatus)
            VALUES ($ClientId,$FinancialYear,$CompanyKey,$BankToken,$Status)
            ON CONFLICT(ClientId,FinancialYear,CompanyKey) DO UPDATE SET
                LastBankInputToken=excluded.LastBankInputToken,
                LastStatus=excluded.LastStatus;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$BankToken", bankInputToken.Trim());
        command.Parameters.AddWithValue("$Status", status.Trim());
        command.ExecuteNonQuery();
    }

    public void DeleteConvertedBankStatementLinksForMasterIds(
        string companyKey,
        long clientId,
        string financialYear,
        IEnumerable<string> masterIds)
    {
        var ids = masterIds.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM TallyBankStatementLinks
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND TallyMasterId=$MasterId COLLATE NOCASE
                  AND TransferMatchId LIKE 'TALLYBTX|%';
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            command.Parameters.AddWithValue("$MasterId", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, table, column, definition);
    }

    public TallyClientLink? GetLinkForClient(long clientId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = LinkSelectSql + " WHERE l.ClientId = $ClientId LIMIT 1;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadLink(reader) : null;
    }

    public TallyClientLink? GetLinkByCompanyKey(string companyKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = LinkSelectSql + " WHERE l.CompanyKey = $CompanyKey LIMIT 1;";
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadLink(reader) : null;
    }

    public IReadOnlyList<TallyClientLink> GetAllLinks()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = LinkSelectSql + " ORDER BY c.ClientCode COLLATE NOCASE, l.CompanyName COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var result = new List<TallyClientLink>();
        while (reader.Read())
        {
            result.Add(ReadLink(reader));
        }
        return result;
    }

    public TallyClientLink ConfirmLink(
        long clientId,
        TallyCompanyInfo company)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (clientId <= 0)
        {
            throw new InvalidOperationException("Open a client before confirming a Tally company.");
        }
        if (string.IsNullOrWhiteSpace(company.CompanyKey))
        {
            throw new InvalidOperationException("The selected Tally company does not have a stable identity.");
        }

        // R4 R9: Accounting and Audit Tally ownership are independent. Accounting never
        // reads Audit links when confirming its own Tally company.

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText =
                """
                SELECT CompanyKey, CompanyName
                FROM TallyClientLinks
                WHERE ClientId = $ClientId
                LIMIT 1;
                """;
            current.Parameters.AddWithValue("$ClientId", clientId);
            using var currentReader = current.ExecuteReader();
            if (currentReader.Read())
            {
                var currentKey = currentReader.GetString(0);
                var currentName = currentReader.GetString(1);
                if (!currentKey.Equals(company.CompanyKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"This client is already linked to {currentName}. Unlink it before confirming another Tally company.");
                }
            }
        }

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText =
                """
                SELECT l.ClientId, COALESCE(c.ClientCode, ''), COALESCE(c.ClientName, '')
                FROM TallyClientLinks l
                LEFT JOIN Clients c ON c.Id = l.ClientId
                WHERE l.CompanyKey = $CompanyKey
                  AND l.ClientId <> $ClientId
                LIMIT 1;
                """;
            check.Parameters.AddWithValue("$CompanyKey", company.CompanyKey.Trim());
            check.Parameters.AddWithValue("$ClientId", clientId);

            using var reader = check.ExecuteReader();
            if (reader.Read())
            {
                var code = reader.GetString(1);
                var name = reader.GetString(2);
                var linkedTo = string.Join(
                    " - ",
                    new[] { code, name }.Where(value => value.Length > 0));
                throw new InvalidOperationException(
                    linkedTo.Length > 0
                        ? $"This Tally company is already linked to {linkedTo}. Unlink it there before assigning it to another client."
                        : "This Tally company is already linked to another client.");
            }
        }

        var now = DateTime.UtcNow.ToString("O");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO TallyClientLinks
                (
                    ClientId, CompanyKey, CompanyName, CompanyNumber, Gstin,
                    StateName, BooksFrom, StartingFrom, FinancialYear,
                    LinkedUtc, UpdatedUtc, LastSeenUtc, LastChangeToken,
                    GroupCount, LedgerCount, VoucherTypeCount, StockItemCount,
                    UnitCount, GodownCount, VoucherCount, LastSyncStatus, LastError
                )
                VALUES
                (
                    $ClientId, $CompanyKey, $CompanyName, $CompanyNumber, $Gstin,
                    $StateName, $BooksFrom, $StartingFrom, $FinancialYear,
                    $LinkedUtc, $UpdatedUtc, $LastSeenUtc, $LastChangeToken,
                    $GroupCount, $LedgerCount, $VoucherTypeCount, $StockItemCount,
                    $UnitCount, $GodownCount, $VoucherCount, 'Link confirmed. Synchronization pending.', ''
                )
                ON CONFLICT(ClientId)
                DO UPDATE SET
                    CompanyKey = excluded.CompanyKey,
                    CompanyName = excluded.CompanyName,
                    CompanyNumber = excluded.CompanyNumber,
                    Gstin = excluded.Gstin,
                    StateName = excluded.StateName,
                    BooksFrom = excluded.BooksFrom,
                    StartingFrom = excluded.StartingFrom,
                    FinancialYear = excluded.FinancialYear,
                    UpdatedUtc = excluded.UpdatedUtc,
                    LastSeenUtc = excluded.LastSeenUtc,
                    LastChangeToken = excluded.LastChangeToken,
                    GroupCount = excluded.GroupCount,
                    LedgerCount = excluded.LedgerCount,
                    VoucherTypeCount = excluded.VoucherTypeCount,
                    StockItemCount = excluded.StockItemCount,
                    UnitCount = excluded.UnitCount,
                    GodownCount = excluded.GodownCount,
                    VoucherCount = excluded.VoucherCount,
                    LastSyncStatus = excluded.LastSyncStatus,
                    LastError = '';
                """;
            AddCompanyParameters(command, clientId, company, now);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        // R4 R9: do not synchronize this Accounting link into any Audit client.
        return GetLinkForClient(clientId)
            ?? throw new InvalidOperationException("The Tally company link could not be saved.");
    }

    private void ValidateAuditOwnershipForAccountingClient(long clientId, string companyKey)
    {
        var key = (companyKey ?? string.Empty).Trim();
        if (key.Length == 0) return;
        try
        {
            using var connection = new SqliteConnection(_auditConnectionString);
            connection.Open();

            using var tableProbe = connection.CreateCommand();
            tableProbe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='AuditClients' LIMIT 1;";
            if (tableProbe.ExecuteScalar() is null) return;

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT ClientCode, ClientName, LinkedAccountingClientId
                    FROM AuditClients
                    WHERE IsArchived=0
                      AND LinkedTallyCompanyKey=$key COLLATE NOCASE;
                    """;
                command.Parameters.AddWithValue("$key", key);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var auditCode = reader.GetString(0);
                    var auditName = reader.GetString(1);
                    var linkedAccountingId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                    if (linkedAccountingId == clientId) continue;

                    var auditOwner = string.Join(" - ", new[] { auditCode, auditName }.Where(value => value.Length > 0));
                    throw new InvalidOperationException(
                        linkedAccountingId is null
                            ? $"This Tally company is reserved by Audit client {auditOwner}. Link that Audit client to this Accounting client before sharing the Tally authority."
                            : $"This Tally company is already owned by Audit client {auditOwner} and its different Accounting client. Unlink the conflicting relationship first.");
                }
            }

            // Fix110 R1: a linked Audit client is the same business authority, not a
            // second independent Tally link. Do not let Accounting switch to a different
            // CompanyKey while an explicitly related Audit client owns another one.
            using (var related = connection.CreateCommand())
            {
                related.CommandText =
                    """
                    SELECT ClientCode, ClientName, LinkedTallyCompanyName
                    FROM AuditClients
                    WHERE IsArchived=0
                      AND LinkedAccountingClientId=$clientId
                      AND LinkedTallyCompanyKey<>''
                      AND LinkedTallyCompanyKey<>$key COLLATE NOCASE
                    LIMIT 1;
                    """;
                related.Parameters.AddWithValue("$clientId", clientId);
                related.Parameters.AddWithValue("$key", key);
                using var reader = related.ExecuteReader();
                if (reader.Read())
                {
                    var auditCode = reader.GetString(0);
                    var auditName = reader.GetString(1);
                    var tallyName = reader.GetString(2);
                    var auditOwner = string.Join(" - ", new[] { auditCode, auditName }.Where(value => value.Length > 0));
                    throw new InvalidOperationException(
                        $"Linked Audit client {auditOwner} already owns Tally company {tallyName}. Unlink the shared Tally company before selecting a different Accounting company.");
                }
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 1 or 14)
        {
            // An Audit workspace/database may not have been created on this installation yet.
            // Absence of Audit storage is not a reason to block an ordinary Accounting link.
        }
    }

    private void SynchronizeAuditLinksForAccountingClient(long clientId, TallyCompanyInfo company)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(company.CompanyKey)) return;
        try
        {
            using var connection = new SqliteConnection(_auditConnectionString);
            connection.Open();
            using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='AuditClients' LIMIT 1;";
            if (probe.ExecuteScalar() is null) return;

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE AuditClients
                SET LinkedTallyCompanyKey=$key,
                    LinkedTallyCompanyName=$name,
                    UpdatedUtc=$updated
                WHERE IsArchived=0
                  AND LinkedAccountingClientId=$clientId
                  AND (LinkedTallyCompanyKey='' OR LinkedTallyCompanyKey=$key COLLATE NOCASE);
                """;
            command.Parameters.AddWithValue("$key", company.CompanyKey.Trim());
            command.Parameters.AddWithValue("$name", company.Name.Trim());
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$clientId", clientId);
            command.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 1 or 14)
        {
            // Audit storage is optional until the Audit workspace has been initialized.
        }
    }

    private void ClearAuditLinksForAccountingClient(long clientId, string companyKey)
    {
        if (clientId <= 0) return;
        try
        {
            using var connection = new SqliteConnection(_auditConnectionString);
            connection.Open();
            using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='AuditClients' LIMIT 1;";
            if (probe.ExecuteScalar() is null) return;

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE AuditClients
                SET LinkedTallyCompanyKey='',
                    LinkedTallyCompanyName='',
                    UpdatedUtc=$updated
                WHERE IsArchived=0
                  AND LinkedAccountingClientId=$clientId
                  AND ($key='' OR LinkedTallyCompanyKey=$key COLLATE NOCASE);
                """;
            command.Parameters.AddWithValue("$clientId", clientId);
            command.Parameters.AddWithValue("$key", (companyKey ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 1 or 14)
        {
            // Audit storage is optional until the Audit workspace has been initialized.
        }
    }

    public void UnlinkClient(long clientId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TallyClientLinks WHERE ClientId = $ClientId;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.ExecuteNonQuery();
        // R4 R9: Accounting unlink never changes independent Audit Tally links.
    }

    // Fix10: enrich only the existing confirmed link. Never re-link or reset
    // authority/history just because Tally reveals its physical company number.
    public bool SaveVerifiedLiveCompanyNumber(long clientId, string companyKey,
        string companyName, string numericCompanyNumber)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(companyKey) ||
            string.IsNullOrWhiteSpace(companyName) || string.IsNullOrWhiteSpace(numericCompanyNumber) ||
            !numericCompanyNumber.All(char.IsDigit)) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TallyClientLinks
            SET CompanyNumber=$number, UpdatedUtc=$updated
            WHERE ClientId=$clientId AND CompanyKey=$key COLLATE NOCASE
              AND CompanyName=$name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$number", numericCompanyNumber);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$clientId", clientId);
        command.Parameters.AddWithValue("$key", companyKey);
        command.Parameters.AddWithValue("$name", companyName);
        return command.ExecuteNonQuery() == 1;
    }

    public void UpdateCompanySeen(TallyCompanyInfo company)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TallyClientLinks
            SET CompanyName = $CompanyName,
                CompanyNumber = CASE WHEN length(trim($CompanyNumber)) > 0 THEN $CompanyNumber ELSE CompanyNumber END,
                Gstin = $Gstin,
                StateName = $StateName,
                BooksFrom = $BooksFrom,
                StartingFrom = $StartingFrom,
                FinancialYear = $FinancialYear,
                LastSeenUtc = $LastSeenUtc,
                GroupCount = CASE WHEN $GroupCount > 0 THEN $GroupCount ELSE GroupCount END,
                LedgerCount = CASE WHEN $LedgerCount > 0 THEN $LedgerCount ELSE LedgerCount END,
                VoucherTypeCount = CASE WHEN $VoucherTypeCount > 0 THEN $VoucherTypeCount ELSE VoucherTypeCount END,
                StockItemCount = CASE WHEN $StockItemCount > 0 THEN $StockItemCount ELSE StockItemCount END,
                UnitCount = CASE WHEN $UnitCount > 0 THEN $UnitCount ELSE UnitCount END,
                GodownCount = CASE WHEN $GodownCount > 0 THEN $GodownCount ELSE GodownCount END,
                VoucherCount = CASE WHEN $VoucherCount > 0 THEN $VoucherCount ELSE VoucherCount END,
                UpdatedUtc = $UpdatedUtc
            WHERE CompanyKey = $CompanyKey;
            """;
        command.Parameters.AddWithValue("$CompanyKey", company.CompanyKey.Trim());
        command.Parameters.AddWithValue("$CompanyName", company.Name.Trim());
        command.Parameters.AddWithValue("$CompanyNumber", company.CompanyNumber.Trim());
        command.Parameters.AddWithValue("$Gstin", company.Gstin.Trim());
        command.Parameters.AddWithValue("$StateName", company.StateName.Trim());
        command.Parameters.AddWithValue("$BooksFrom", company.BooksFrom.Trim());
        command.Parameters.AddWithValue("$StartingFrom", company.StartingFrom.Trim());
        command.Parameters.AddWithValue("$FinancialYear", company.FinancialYear.Trim());
        command.Parameters.AddWithValue("$LastSeenUtc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$GroupCount", company.GroupCount);
        command.Parameters.AddWithValue("$LedgerCount", company.LedgerCount);
        command.Parameters.AddWithValue("$VoucherTypeCount", company.VoucherTypeCount);
        command.Parameters.AddWithValue("$StockItemCount", company.StockItemCount);
        command.Parameters.AddWithValue("$UnitCount", company.UnitCount);
        command.Parameters.AddWithValue("$GodownCount", company.GodownCount);
        command.Parameters.AddWithValue("$VoucherCount", company.VoucherCount);
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpdateSynchronization(
        long clientId,
        TallyCompanyInfo company,
        TallyMasterSyncResult masterResult,
        int voucherCount,
        int duplicateVoucherCount,
        int conflictingVoucherCount,
        string changeToken,
        string status,
        string error = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TallyClientLinks
            SET LastMasterSyncUtc = $LastMasterSyncUtc,
                LastReportRefreshUtc = $LastReportRefreshUtc,
                LastChangeToken = $LastChangeToken,
                GroupCount = $GroupCount,
                LedgerCount = $LedgerCount,
                VoucherTypeCount = $VoucherTypeCount,
                StockItemCount = $StockItemCount,
                UnitCount = $UnitCount,
                GodownCount = $GodownCount,
                VoucherCount = $VoucherCount,
                MasterCount = $MasterCount,
                DuplicateMasterCount = $DuplicateMasterCount,
                ConflictingMasterCount = $ConflictingMasterCount,
                DuplicateVoucherCount = $DuplicateVoucherCount,
                ConflictingVoucherCount = $ConflictingVoucherCount,
                LastSyncStatus = $LastSyncStatus,
                LastError = $LastError,
                UpdatedUtc = $UpdatedUtc
            WHERE ClientId = $ClientId
              AND CompanyKey = $CompanyKey;
            """;
        var now = DateTime.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$CompanyKey", company.CompanyKey.Trim());
        command.Parameters.AddWithValue("$LastMasterSyncUtc", now);
        command.Parameters.AddWithValue("$LastReportRefreshUtc", now);
        command.Parameters.AddWithValue("$LastChangeToken", changeToken.Trim());
        command.Parameters.AddWithValue("$GroupCount", masterResult.GroupCount);
        command.Parameters.AddWithValue("$LedgerCount", masterResult.LedgerCount);
        command.Parameters.AddWithValue("$VoucherTypeCount", masterResult.VoucherTypeCount);
        command.Parameters.AddWithValue("$StockItemCount", masterResult.StockItemCount);
        command.Parameters.AddWithValue("$UnitCount", masterResult.UnitCount);
        command.Parameters.AddWithValue("$GodownCount", masterResult.GodownCount);
        command.Parameters.AddWithValue("$VoucherCount", voucherCount);
        command.Parameters.AddWithValue("$MasterCount", masterResult.UniqueObjects);
        command.Parameters.AddWithValue("$DuplicateMasterCount", masterResult.DuplicateObjects);
        command.Parameters.AddWithValue("$ConflictingMasterCount", masterResult.ConflictingObjects);
        command.Parameters.AddWithValue("$DuplicateVoucherCount", duplicateVoucherCount);
        command.Parameters.AddWithValue("$ConflictingVoucherCount", conflictingVoucherCount);
        command.Parameters.AddWithValue("$LastSyncStatus", status.Trim());
        command.Parameters.AddWithValue("$LastError", error.Trim());
        command.Parameters.AddWithValue("$UpdatedUtc", now);
        command.ExecuteNonQuery();
    }

    public void UpdateAuthorityCheckpoint(
        long clientId,
        TallyCompanyInfo company,
        TallyVoucherChangeMarker marker,
        string status,
        string error = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TallyClientLinks
            SET LastSeenUtc=$Now, LastMasterSyncUtc=$Now, LastChangeToken=$Token,
                GroupCount=CASE WHEN $GroupCount>0 THEN $GroupCount ELSE GroupCount END,
                LedgerCount=CASE WHEN $LedgerCount>0 THEN $LedgerCount ELSE LedgerCount END,
                VoucherTypeCount=CASE WHEN $VoucherTypeCount>0 THEN $VoucherTypeCount ELSE VoucherTypeCount END,
                StockItemCount=CASE WHEN $StockItemCount>0 THEN $StockItemCount ELSE StockItemCount END,
                UnitCount=CASE WHEN $UnitCount>0 THEN $UnitCount ELSE UnitCount END,
                GodownCount=CASE WHEN $GodownCount>0 THEN $GodownCount ELSE GodownCount END,
                VoucherCount=$VoucherCount, LastSyncStatus=$Status, LastError=$Error, UpdatedUtc=$Now
            WHERE ClientId=$ClientId AND CompanyKey=$CompanyKey;
            """;
        var now = DateTime.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$Now", now);
        command.Parameters.AddWithValue("$Token", marker.Token.Trim());
        command.Parameters.AddWithValue("$GroupCount", marker.GroupCount);
        command.Parameters.AddWithValue("$LedgerCount", marker.LedgerCount);
        command.Parameters.AddWithValue("$VoucherTypeCount", marker.VoucherTypeCount);
        command.Parameters.AddWithValue("$StockItemCount", marker.StockItemCount);
        command.Parameters.AddWithValue("$UnitCount", marker.UnitCount);
        command.Parameters.AddWithValue("$GodownCount", marker.GodownCount);
        command.Parameters.AddWithValue("$VoucherCount", marker.VoucherCount);
        command.Parameters.AddWithValue("$Status", status.Trim());
        command.Parameters.AddWithValue("$Error", error.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$CompanyKey", company.CompanyKey.Trim());
        command.ExecuteNonQuery();
    }

    public void UpdateErp9CoreLiveStatus(
        long clientId,
        string companyKey,
        string status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TallyClientLinks
            SET LastSyncStatus = $LastSyncStatus,
                LastError = '',
                LastSeenUtc = $LastSeenUtc,
                UpdatedUtc = $UpdatedUtc
            WHERE ClientId = $ClientId AND CompanyKey = $CompanyKey;
            """;
        var now = DateTime.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$LastSyncStatus", status.Trim());
        command.Parameters.AddWithValue("$LastSeenUtc", now);
        command.Parameters.AddWithValue("$UpdatedUtc", now);
        command.ExecuteNonQuery();
    }

    public void UpdateSyncError(
        long clientId,
        string companyKey,
        string error)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TallyClientLinks
            SET LastSyncStatus = 'Synchronization failed.',
                LastError = $LastError,
                UpdatedUtc = $UpdatedUtc
            WHERE ClientId = $ClientId AND CompanyKey = $CompanyKey;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$LastError", error.Trim());
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void SaveBankImportPeriod(
        long clientId,
        DateTime fromDate,
        DateTime toDate)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TallyClientLinks
            SET BankImportFrom = $FromDate,
                BankImportTo = $ToDate,
                UpdatedUtc = $UpdatedUtc
            WHERE ClientId = $ClientId;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FromDate", fromDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$ToDate", toDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void ReplaceVoucherIndex(
        string companyKey,
        string financialYear,
        IReadOnlyCollection<TallyVoucherIndexRecord> records)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM TallyVoucherIndex WHERE CompanyKey = $CompanyKey AND FinancialYear = $FinancialYear;";
            delete.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            delete.ExecuteNonQuery();
        }

        foreach (var record in records)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO TallyVoucherIndex
                (
                    CompanyKey, FinancialYear, MasterId, AlterId, Guid, VoucherDate,
                    VoucherType, VoucherNumber, Reference, PartyLedger, DebitLedgers, CreditLedgers, Amount,
                    Narration, ContentHash, LastSeenUtc
                )
                VALUES
                (
                    $CompanyKey, $FinancialYear, $MasterId, $AlterId, $Guid, $VoucherDate,
                    $VoucherType, $VoucherNumber, $Reference, $PartyLedger, $DebitLedgers, $CreditLedgers, $Amount,
                    $Narration, $ContentHash, $LastSeenUtc
                );
                """;
            insert.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            insert.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            insert.Parameters.AddWithValue("$MasterId", record.MasterId.Trim());
            insert.Parameters.AddWithValue("$AlterId", record.AlterId.Trim());
            insert.Parameters.AddWithValue("$Guid", record.Guid.Trim());
            insert.Parameters.AddWithValue("$VoucherDate", record.VoucherDate?.ToString("yyyy-MM-dd") ?? string.Empty);
            insert.Parameters.AddWithValue("$VoucherType", record.VoucherType.Trim());
            insert.Parameters.AddWithValue("$VoucherNumber", record.VoucherNumber.Trim());
            insert.Parameters.AddWithValue("$Reference", record.Reference.Trim());
            insert.Parameters.AddWithValue("$PartyLedger", record.PartyLedger.Trim());
            insert.Parameters.AddWithValue("$DebitLedgers", record.DebitLedgers.Trim());
            insert.Parameters.AddWithValue("$CreditLedgers", record.CreditLedgers.Trim());
            insert.Parameters.AddWithValue("$Amount", (double)record.Amount);
            insert.Parameters.AddWithValue("$Narration", record.Narration.Trim());
            insert.Parameters.AddWithValue("$ContentHash", record.ContentHash.Trim());
            insert.Parameters.AddWithValue("$LastSeenUtc", (record.LastSeenUtc == default ? DateTime.UtcNow : record.LastSeenUtc).ToString("O"));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Refreshes only the selected live Tally date range. This is used immediately
    /// before/after voucher export so duplicate decisions are based on what exists in
    /// Tally now, without discarding the cached index for the rest of the FY.
    /// </summary>
    public void ReplaceVoucherIndexRange(
        string companyKey,
        string financialYear,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyCollection<TallyVoucherIndexRecord> records)
    {
        fromDate = fromDate.Date;
        toDate = toDate.Date;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM TallyVoucherIndex
                WHERE CompanyKey = $CompanyKey
                  AND FinancialYear = $FinancialYear
                  AND VoucherDate >= $FromDate
                  AND VoucherDate <= $ToDate;
                """;
            delete.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            delete.Parameters.AddWithValue("$FromDate", fromDate.ToString("yyyy-MM-dd"));
            delete.Parameters.AddWithValue("$ToDate", toDate.ToString("yyyy-MM-dd"));
            delete.ExecuteNonQuery();
        }

        foreach (var record in records)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO TallyVoucherIndex
                (
                    CompanyKey, FinancialYear, MasterId, AlterId, Guid, VoucherDate,
                    VoucherType, VoucherNumber, Reference, PartyLedger, DebitLedgers, CreditLedgers, Amount,
                    Narration, ContentHash, LastSeenUtc
                )
                VALUES
                (
                    $CompanyKey, $FinancialYear, $MasterId, $AlterId, $Guid, $VoucherDate,
                    $VoucherType, $VoucherNumber, $Reference, $PartyLedger, $DebitLedgers, $CreditLedgers, $Amount,
                    $Narration, $ContentHash, $LastSeenUtc
                );
                """;
            insert.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            insert.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            insert.Parameters.AddWithValue("$MasterId", record.MasterId.Trim());
            insert.Parameters.AddWithValue("$AlterId", record.AlterId.Trim());
            insert.Parameters.AddWithValue("$Guid", record.Guid.Trim());
            insert.Parameters.AddWithValue("$VoucherDate", record.VoucherDate?.ToString("yyyy-MM-dd") ?? string.Empty);
            insert.Parameters.AddWithValue("$VoucherType", record.VoucherType.Trim());
            insert.Parameters.AddWithValue("$VoucherNumber", record.VoucherNumber.Trim());
            insert.Parameters.AddWithValue("$Reference", record.Reference.Trim());
            insert.Parameters.AddWithValue("$PartyLedger", record.PartyLedger.Trim());
            insert.Parameters.AddWithValue("$DebitLedgers", record.DebitLedgers.Trim());
            insert.Parameters.AddWithValue("$CreditLedgers", record.CreditLedgers.Trim());
            insert.Parameters.AddWithValue("$Amount", (double)record.Amount);
            insert.Parameters.AddWithValue("$Narration", record.Narration.Trim());
            insert.Parameters.AddWithValue("$ContentHash", record.ContentHash.Trim());
            insert.Parameters.AddWithValue("$LastSeenUtc", (record.LastSeenUtc == default ? DateTime.UtcNow : record.LastSeenUtc).ToString("O"));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Applies only changed/new live Voucher rows to the durable index. Existing rows for
    /// the same MASTERID/GUID are replaced; untouched FY vouchers remain cached.
    /// </summary>
    public void UpsertVoucherIndexRecords(
        string companyKey,
        string financialYear,
        IReadOnlyCollection<TallyVoucherIndexRecord> records)
    {
        if (records.Count == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var record in records)
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    """
                    DELETE FROM TallyVoucherIndex
                    WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear
                      AND (( $MasterId<>'' AND MasterId=$MasterId COLLATE NOCASE)
                           OR ($MasterId='' AND $Guid<>'' AND Guid=$Guid COLLATE NOCASE));
                    """;
                delete.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
                delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
                delete.Parameters.AddWithValue("$MasterId", record.MasterId.Trim());
                delete.Parameters.AddWithValue("$Guid", record.Guid.Trim());
                delete.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO TallyVoucherIndex
                (CompanyKey,FinancialYear,MasterId,AlterId,Guid,VoucherDate,VoucherType,VoucherNumber,Reference,PartyLedger,
                 DebitLedgers,CreditLedgers,Amount,Narration,ContentHash,LastSeenUtc)
                VALUES
                ($CompanyKey,$FinancialYear,$MasterId,$AlterId,$Guid,$VoucherDate,$VoucherType,$VoucherNumber,$Reference,$PartyLedger,
                 $DebitLedgers,$CreditLedgers,$Amount,$Narration,$ContentHash,$LastSeenUtc);
                """;
            insert.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            insert.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            insert.Parameters.AddWithValue("$MasterId", record.MasterId.Trim());
            insert.Parameters.AddWithValue("$AlterId", record.AlterId.Trim());
            insert.Parameters.AddWithValue("$Guid", record.Guid.Trim());
            insert.Parameters.AddWithValue("$VoucherDate", record.VoucherDate?.ToString("yyyy-MM-dd") ?? string.Empty);
            insert.Parameters.AddWithValue("$VoucherType", record.VoucherType.Trim());
            insert.Parameters.AddWithValue("$VoucherNumber", record.VoucherNumber.Trim());
            insert.Parameters.AddWithValue("$Reference", record.Reference.Trim());
            insert.Parameters.AddWithValue("$PartyLedger", record.PartyLedger.Trim());
            insert.Parameters.AddWithValue("$DebitLedgers", record.DebitLedgers.Trim());
            insert.Parameters.AddWithValue("$CreditLedgers", record.CreditLedgers.Trim());
            insert.Parameters.AddWithValue("$Amount", (double)record.Amount);
            insert.Parameters.AddWithValue("$Narration", record.Narration.Trim());
            insert.Parameters.AddWithValue("$ContentHash", record.ContentHash.Trim());
            insert.Parameters.AddWithValue("$LastSeenUtc", (record.LastSeenUtc == default ? DateTime.UtcNow : record.LastSeenUtc).ToString("O"));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void DeleteVoucherIndexByMasterIds(
        string companyKey, string financialYear, IEnumerable<string> masterIds)
    {
        var ids = masterIds.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var masterId in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM TallyVoucherIndex WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear AND MasterId=$MasterId COLLATE NOCASE;";
            command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$MasterId", masterId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void ReplaceVoucherXmlAuthority(
        string companyKey,
        string financialYear,
        IReadOnlyCollection<TallyVoucherXmlSnapshot> snapshots)
    {
        companyKey = companyKey.Trim();
        financialYear = financialYear.Trim();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM TallyVoucherXmlAuthority WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear;";
            delete.Parameters.AddWithValue("$CompanyKey", companyKey);
            delete.Parameters.AddWithValue("$FinancialYear", financialYear);
            delete.ExecuteNonQuery();
        }
        UpsertVoucherXmlAuthorityCore(connection, transaction, companyKey, financialYear, snapshots);
        transaction.Commit();
    }

    public void UpsertVoucherXmlAuthority(
        string companyKey,
        string financialYear,
        IReadOnlyCollection<TallyVoucherXmlSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        UpsertVoucherXmlAuthorityCore(connection, transaction, companyKey.Trim(), financialYear.Trim(), snapshots);
        transaction.Commit();
    }

    private static void UpsertVoucherXmlAuthorityCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string companyKey,
        string financialYear,
        IReadOnlyCollection<TallyVoucherXmlSnapshot> snapshots)
    {
        var now = DateTime.UtcNow.ToString("O");
        foreach (var snapshot in snapshots)
        {
            var masterId = snapshot.MasterId.Trim();
            if (masterId.Length == 0 || string.IsNullOrWhiteSpace(snapshot.OriginalXml)) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO TallyVoucherXmlAuthority
                (CompanyKey,FinancialYear,MasterId,AlterId,Guid,VoucherDate,VoucherType,OriginalXml,ContentHash,LastSeenUtc)
                VALUES
                ($CompanyKey,$FinancialYear,$MasterId,$AlterId,$Guid,$VoucherDate,$VoucherType,$OriginalXml,$ContentHash,$LastSeenUtc)
                ON CONFLICT(CompanyKey,FinancialYear,MasterId) DO UPDATE SET
                    AlterId=excluded.AlterId,
                    Guid=excluded.Guid,
                    VoucherDate=excluded.VoucherDate,
                    VoucherType=excluded.VoucherType,
                    OriginalXml=excluded.OriginalXml,
                    ContentHash=excluded.ContentHash,
                    LastSeenUtc=excluded.LastSeenUtc;
                """;
            command.Parameters.AddWithValue("$CompanyKey", companyKey);
            command.Parameters.AddWithValue("$FinancialYear", financialYear);
            command.Parameters.AddWithValue("$MasterId", masterId);
            command.Parameters.AddWithValue("$AlterId", snapshot.AlterId.Trim());
            command.Parameters.AddWithValue("$Guid", snapshot.Guid.Trim());
            command.Parameters.AddWithValue("$VoucherDate", snapshot.VoucherDate is DateTime d ? d.Date.ToString("yyyy-MM-dd") : string.Empty);
            command.Parameters.AddWithValue("$VoucherType", snapshot.VoucherType.Trim());
            command.Parameters.AddWithValue("$OriginalXml", snapshot.OriginalXml);
            command.Parameters.AddWithValue("$ContentHash", ComputeXmlHash(snapshot.OriginalXml));
            command.Parameters.AddWithValue("$LastSeenUtc", now);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<TallyVoucherXmlSnapshot> GetVoucherXmlAuthorityByMasterIds(
        string companyKey,
        string financialYear,
        IEnumerable<string> masterIds)
    {
        var ids = masterIds.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) return Array.Empty<TallyVoucherXmlSnapshot>();
        var result = new List<TallyVoucherXmlSnapshot>();
        using var connection = OpenConnection();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT MasterId,AlterId,Guid,VoucherDate,VoucherType,OriginalXml
                FROM TallyVoucherXmlAuthority
                WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear AND MasterId=$MasterId COLLATE NOCASE
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$MasterId", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) continue;
            result.Add(new TallyVoucherXmlSnapshot(
                reader.GetString(0),
                reader.GetString(2),
                reader.GetString(4),
                ParseDateNullable(reader.GetString(3)),
                reader.GetString(5))
            {
                AlterId = reader.GetString(1)
            });
        }
        return result;
    }

    public bool HasCompleteVoucherXmlAuthority(string companyKey, string financialYear, int expectedVoucherCount = 0)
    {
        using var connection = OpenConnection();

        // FIXED40: an empty local Voucher index is not proof of completeness when Tally's
        // live marker says vouchers exist. The previous LEFT JOIN-only test returned true
        // vacuously for zero rows and could leave the prepared authority permanently empty.
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText =
                "SELECT COUNT(*) FROM TallyVoucherIndex WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear AND TRIM(MasterId)<>'';";
            countCommand.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            countCommand.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            var indexCount = Convert.ToInt32(countCommand.ExecuteScalar() ?? 0);
            if (expectedVoucherCount > 0 && indexCount == 0) return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM TallyVoucherIndex i
            LEFT JOIN TallyVoucherXmlAuthority x
              ON x.CompanyKey=i.CompanyKey COLLATE NOCASE
             AND x.FinancialYear=i.FinancialYear
             AND x.MasterId=i.MasterId COLLATE NOCASE
            WHERE i.CompanyKey=$CompanyKey
              AND i.FinancialYear=$FinancialYear
              AND TRIM(i.MasterId)<>''
              AND (x.MasterId IS NULL OR TRIM(x.OriginalXml)='');
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) == 0L;
    }

    public int GetVoucherXmlAuthorityCount(string companyKey, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TallyVoucherXmlAuthority WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear;";
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    public void DeleteVoucherXmlAuthorityByMasterIds(string companyKey, string financialYear, IEnumerable<string> masterIds)
    {
        var ids = masterIds.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM TallyVoucherXmlAuthority WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear AND MasterId=$MasterId COLLATE NOCASE;";
            command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$MasterId", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static string ComputeXmlHash(string xml)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml));
        return Convert.ToHexString(bytes);
    }

    private static DateTime? ParseDateNullable(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, out var date)) return date;
        return null;
    }

    public IReadOnlyList<TallyVoucherIndexRecord> GetVoucherIndexRange(
        string companyKey,
        string financialYear,
        DateTime fromDate,
        DateTime toDate)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CompanyKey,FinancialYear,MasterId,AlterId,Guid,VoucherDate,VoucherType,VoucherNumber,Reference,
                   PartyLedger,DebitLedgers,CreditLedgers,Amount,Narration,ContentHash,LastSeenUtc
            FROM TallyVoucherIndex
            WHERE CompanyKey = $CompanyKey
              AND FinancialYear = $FinancialYear
              AND VoucherDate >= $FromDate
              AND VoucherDate <= $ToDate
            ORDER BY VoucherDate,VoucherType,VoucherNumber,Id;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$FromDate", fromDate.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$ToDate", toDate.Date.ToString("yyyy-MM-dd"));

        using var reader = command.ExecuteReader();
        var result = new List<TallyVoucherIndexRecord>();
        while (reader.Read()) result.Add(ReadVoucher(reader));
        return result;
    }

    public TallyVoucherIndexRecord? FindVoucherByReference(
        string companyKey,
        string reference)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CompanyKey, FinancialYear, MasterId, AlterId, Guid, VoucherDate,
                   VoucherType, VoucherNumber, Reference, PartyLedger, DebitLedgers, CreditLedgers, Amount,
                   Narration, ContentHash, LastSeenUtc
            FROM TallyVoucherIndex
            WHERE CompanyKey = $CompanyKey
              AND Reference = $Reference COLLATE NOCASE
            ORDER BY Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$Reference", reference.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadVoucher(reader) : null;
    }


    public IReadOnlyList<TallyVoucherIndexRecord> FindVoucherCandidates(
        string companyKey,
        string financialYear,
        DateTime voucherDate,
        string voucherType,
        string reference)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CompanyKey, FinancialYear, MasterId, AlterId, Guid, VoucherDate,
                   VoucherType, VoucherNumber, Reference, PartyLedger, DebitLedgers, CreditLedgers, Amount,
                   Narration, ContentHash, LastSeenUtc
            FROM TallyVoucherIndex
            WHERE CompanyKey = $CompanyKey
              AND FinancialYear = $FinancialYear
              AND VoucherDate = $VoucherDate
              AND VoucherType = $VoucherType COLLATE NOCASE
            ORDER BY Id DESC;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$VoucherDate", voucherDate.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$VoucherType", voucherType.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<TallyVoucherIndexRecord>();
        var wantedReference = NormalizeVoucherReference(reference);
        while (reader.Read())
        {
            var record = ReadVoucher(reader);
            if (NormalizeVoucherReference(record.Reference).Equals(wantedReference, StringComparison.OrdinalIgnoreCase))
                result.Add(record);
        }
        return result;
    }

    public IReadOnlyList<TallyVoucherIndexRecord> FindVoucherCandidatesByReference(
        string companyKey, string financialYear, string voucherType, string reference)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CompanyKey, FinancialYear, MasterId, AlterId, Guid, VoucherDate,
                   VoucherType, VoucherNumber, Reference, PartyLedger, DebitLedgers, CreditLedgers, Amount,
                   Narration, ContentHash, LastSeenUtc
            FROM TallyVoucherIndex
            WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear
              AND VoucherType=$VoucherType COLLATE NOCASE
            ORDER BY VoucherDate,Id;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$VoucherType", voucherType.Trim());
        var wanted = NormalizeVoucherReference(reference);
        using var reader = command.ExecuteReader();
        var rows = new List<TallyVoucherIndexRecord>();
        while (reader.Read())
        {
            var row = ReadVoucher(reader);
            if (NormalizeVoucherReference(row.Reference).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                rows.Add(row);
        }
        return rows;
    }

    private static string NormalizeVoucherReference(string value) =>
        string.Join(" ", (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private IReadOnlyList<string> GetVoucherHashesByReference(
        string companyKey,
        string reference)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ContentHash
            FROM TallyVoucherIndex
            WHERE CompanyKey = $CompanyKey
              AND Reference = $Reference COLLATE NOCASE
            ORDER BY Id DESC;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$Reference", reference.Trim());

        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var hash = reader.GetString(0);
            if (hash.Length > 0 &&
                !result.Contains(hash, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(hash);
            }
        }
        return result;
    }

    public bool HasImportedContentFingerprint(
        long clientId,
        string financialYear,
        string companyKey,
        string entityType,
        string sourceKey,
        string contentHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM TallyImportFingerprints
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND CompanyKey = $CompanyKey
              AND EntityType = $EntityType
              AND SourceKey = $SourceKey COLLATE NOCASE
              AND ContentHash = $ContentHash COLLATE NOCASE
              AND Status = 'Imported' COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$EntityType", entityType.Trim());
        command.Parameters.AddWithValue("$SourceKey", sourceKey.Trim());
        command.Parameters.AddWithValue("$ContentHash", contentHash.Trim());
        return command.ExecuteScalar() is not null;
    }

    public bool HasImportedSourceFingerprint(
        long clientId,
        string financialYear,
        string companyKey,
        string entityType,
        string sourceKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM TallyImportFingerprints
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND CompanyKey = $CompanyKey
              AND EntityType = $EntityType
              AND SourceKey = $SourceKey COLLATE NOCASE
              AND Status = 'Imported' COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$EntityType", entityType.Trim());
        command.Parameters.AddWithValue("$SourceKey", sourceKey.Trim());
        return command.ExecuteScalar() is not null;
    }

    public TallyDuplicateDecision EvaluateImportFingerprint(
        long clientId,
        string financialYear,
        string companyKey,
        string entityType,
        string sourceKey,
        string contentHash)
    {
        var tallyHashes = entityType.Equals("Bank Voucher", StringComparison.OrdinalIgnoreCase)
            ? GetVoucherHashesByReference(companyKey, sourceKey)
            : [];

        if (tallyHashes.Count > 0)
        {
            var conflictingHash = tallyHashes.FirstOrDefault(hash =>
                !hash.Equals(contentHash, StringComparison.OrdinalIgnoreCase));
            var isConflict = conflictingHash is not null;
            return new TallyDuplicateDecision
            {
                Kind = isConflict
                    ? TallyDuplicateKind.Conflict
                    : TallyDuplicateKind.ExactDuplicate,
                SourceKey = sourceKey,
                ExistingHash = isConflict
                    ? conflictingHash!
                    : tallyHashes[0],
                IncomingHash = contentHash,
                Reason = isConflict
                    ? "The voucher reference already exists in Tally with different content."
                    : "The same voucher reference and content already exist in Tally."
            };
        }

        // Local fingerprint rows are an audit trail, not proof that a voucher still
        // exists in Tally. A voucher can be deleted/rolled back in Tally after an
        // earlier response was recorded. Only the freshly-refreshed live voucher
        // index above is allowed to block a new export as an exact duplicate/conflict.
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Status
            FROM TallyImportFingerprints
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND CompanyKey = $CompanyKey
              AND EntityType = $EntityType
              AND SourceKey = $SourceKey COLLATE NOCASE
            ORDER BY Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$EntityType", entityType.Trim());
        command.Parameters.AddWithValue("$SourceKey", sourceKey.Trim());
        var priorStatus = command.ExecuteScalar()?.ToString()?.Trim() ?? string.Empty;

        return new TallyDuplicateDecision
        {
            Kind = TallyDuplicateKind.New,
            SourceKey = sourceKey,
            IncomingHash = contentHash,
            Reason = priorStatus.Length == 0
                ? "No matching voucher exists in the live Tally index."
                : $"No matching voucher exists in live Tally. Prior local status '{priorStatus}' is advisory only."
        };
    }

    public IReadOnlySet<string> GetImportedSourceKeys(
        long clientId,
        string financialYear,
        string companyKey,
        string entityType)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT SourceKey
            FROM TallyImportFingerprints
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND CompanyKey = $CompanyKey
              AND EntityType = $EntityType
              AND Status = 'Imported' COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$EntityType", entityType.Trim());

        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var sourceKey = reader.GetString(0).Trim();
            if (sourceKey.Length > 0)
            {
                result.Add(sourceKey);
            }
        }
        return result;
    }

    public (string MasterId, string Guid, string LastContentHash, string State)? GetSourceBinding(
        long clientId, string financialYear, string companyKey, string entityType, string sourceAnchor)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor)) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TallyMasterId,TallyGuid,LastContentHash,State
            FROM TallySourceBindings
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
              AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$EntityType", entityType.Trim());
        command.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0).Trim(), reader.GetString(1).Trim(), reader.GetString(2).Trim(), reader.GetString(3).Trim())
            : null;
    }

    // Fix85: classify missing previously-bound Voucher MASTERIDs against one complete
    // fresh active-FY authority BEFORE any module is allowed to reinterpret the source
    // as a CREATE. A small/stable set of missing identities is resolved per source as
    // external Tally deletion vs exact replacement. A widespread identity loss is a
    // company-generation/rebuild recovery condition and may rebind exact live Vouchers,
    // but it is never automatic permission to CREATE. Legacy Fix68 StaleMissingLive
    // rows are upgraded to the same fail-closed RebindRequired state.
    public (int Bound, int Missing, int MissingCandidates, int RebindRequired, int Conflicts, bool GenerationRecovery)
        ReconcileMissingVoucherBindingAuthority(
            long clientId, string financialYear, string companyKey, IEnumerable<string> liveMasterIds)
    {
        var live = (liveMasterIds ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Old Fix68 rows already lost the MASTERID, so their absence can no longer be
        // safely distinguished from a restore/rebuild. Keep them recoverable by exact
        // live identity only and permanently remove their former CREATE permission.
        using (var legacy = connection.CreateCommand())
        {
            legacy.Transaction = transaction;
            legacy.CommandText =
                """
                UPDATE TallySourceBindings
                SET State='RebindRequired',
                    LastResponse='Fix85 upgraded legacy StaleMissingLive to fail-closed RebindRequired; exact live MASTERID recovery is allowed, automatic CREATE is forbidden.',
                    UpdatedUtc=$Utc
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND State='StaleMissingLive' COLLATE NOCASE;
                """;
            legacy.Parameters.AddWithValue("$ClientId", clientId);
            legacy.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            legacy.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            legacy.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
            legacy.ExecuteNonQuery();
        }

        var rows = new List<(string Anchor, string SourceKey, string MasterId, string State, string PendingVersion)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT SourceAnchor,SourceKey,TallyMasterId,State,PendingVersion
                FROM TallySourceBindings
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND TRIM(TallyMasterId)<>''
                  AND State COLLATE NOCASE NOT IN ('DeletedInTally','ExternalDeleteConflict');
                """;
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            read.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0).Trim(),
                    reader.GetString(1).Trim(),
                    reader.GetString(2).Trim(),
                    reader.GetString(3).Trim(),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
            }
        }

        var bound = rows.Count;
        var missingRows = rows.Where(row => !live.Contains(row.MasterId)).ToArray();
        var missing = missingRows.Length;
        if (missing == 0)
        {
            transaction.Commit();
            return (bound, 0, 0, 0, 0, false);
        }

        var survivors = bound - missing;
        // Deliberately conservative. This signal never authorizes CREATE; it only keeps
        // a widespread identity reset in exact-rebind recovery instead of mislabelling
        // hundreds of rows as individual user deletions.
        var generationRecovery = bound >= 20 && missing >= 10 &&
                                 missing * 100 >= bound * 35 &&
                                 survivors * 100 <= bound * 65;
        var candidateCount = 0;
        var rebindCount = 0;
        var conflictCount = 0;
        var now = DateTime.UtcNow.ToString("O");

        foreach (var row in missingRows)
        {
            if (row.State.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase) ||
                row.State.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase) ||
                row.State.Equals("BankMigrationReconciliation", StringComparison.OrdinalIgnoreCase))
                continue;

            var hasPendingAahChange = row.PendingVersion.Length > 0 ||
                                      row.State.Equals("ModifyPending", StringComparison.OrdinalIgnoreCase) ||
                                      row.State.Equals("AcceptedAwaitingVerification", StringComparison.OrdinalIgnoreCase) ||
                                      row.State.Equals("CreatePending", StringComparison.OrdinalIgnoreCase);
            var nextState = hasPendingAahChange
                ? "ExternalDeleteConflict"
                : generationRecovery ? "RebindRequired" : "MissingLiveCandidate";
            var response = hasPendingAahChange
                ? $"Fresh FY authority no longer contains bound Voucher MASTERID {row.MasterId}, but AAH also has an unverified/pending desired version. External deletion and local change conflict; CREATE/ALTER are both blocked."
                : generationRecovery
                    ? $"Fresh FY authority no longer contains bound Voucher MASTERID {row.MasterId} during widespread identity loss. Company-generation recovery requires exact live rebind; automatic CREATE is forbidden."
                    : $"Fresh FY authority no longer contains bound Voucher MASTERID {row.MasterId}. Resolve an exact replacement first; if none exists, adopt this as DeletedInTally. Automatic CREATE is forbidden.";

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE TallySourceBindings
                SET State=$State,LastResponse=$Response,UpdatedUtc=$Utc
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND SourceAnchor=$SourceAnchor COLLATE NOCASE AND TallyMasterId=$MasterId COLLATE NOCASE;
                """;
            update.Parameters.AddWithValue("$ClientId", clientId);
            update.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            update.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            update.Parameters.AddWithValue("$SourceAnchor", row.Anchor);
            update.Parameters.AddWithValue("$MasterId", row.MasterId);
            update.Parameters.AddWithValue("$State", nextState);
            update.Parameters.AddWithValue("$Response", response);
            update.Parameters.AddWithValue("$Utc", now);
            update.ExecuteNonQuery();

            if (nextState == "MissingLiveCandidate") candidateCount++;
            else if (nextState == "RebindRequired") rebindCount++;
            else conflictCount++;
        }

        transaction.Commit();
        return (bound, missing, candidateCount, rebindCount, conflictCount, generationRecovery);
    }

    // R37: client/FY Tally Recovery Center. Surface only previously-bound AAH Voucher
    // sources whose current Tally identity is missing or under deletion/rebind review.
    // This is read-only authority; no row returned here is recreated automatically.
    public IReadOnlyList<TallyVoucherRecoveryCandidate> GetVoucherRecoveryCandidates(
        long clientId, string financialYear, string companyKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT B.EntityType,B.SourceAnchor,B.SourceKey,
                   COALESCE(NULLIF(TRIM(B.TallyMasterId),''),(
                       SELECT H.TallyMasterId FROM TallyObjectVersionHistory H
                       WHERE H.ClientId=B.ClientId AND H.FinancialYear=B.FinancialYear AND H.CompanyKey=B.CompanyKey
                         AND H.EntityType=B.EntityType AND H.SourceAnchor=B.SourceAnchor COLLATE NOCASE
                         AND TRIM(H.TallyMasterId)<>''
                       ORDER BY H.Id DESC LIMIT 1
                   ),''),
                   B.State,B.LastVerifiedVersion,B.LastResponse,B.UpdatedUtc
            FROM TallySourceBindings B
            WHERE B.ClientId=$ClientId AND B.FinancialYear=$FinancialYear AND B.CompanyKey=$CompanyKey
              AND B.State COLLATE NOCASE IN
                  ('MissingLiveCandidate','DeletedInTally','ExternalDeleteConflict','RebindRequired','RestoreRequested')
            ORDER BY B.UpdatedUtc DESC, B.EntityType COLLATE NOCASE, B.SourceKey COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        using var reader = command.ExecuteReader();
        var rows = new List<TallyVoucherRecoveryCandidate>();
        while (reader.Read())
        {
            var version = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            var dateText = ReadRecoveryVersionField(version, "DATE");
            DateTime? voucherDate = DateTime.TryParseExact(
                dateText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate.Date : null;
            var amountText = ReadRecoveryVersionField(version, "AMT");
            if (amountText.Length == 0) amountText = ReadRecoveryVersionField(version, "AMOUNT");
            _ = decimal.TryParse(amountText, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var amount);
            var updatedText = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            _ = DateTime.TryParse(updatedText, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var updatedUtc);
            rows.Add(new TallyVoucherRecoveryCandidate
            {
                EntityType = reader.GetString(0).Trim(),
                SourceAnchor = reader.GetString(1).Trim(),
                SourceKey = reader.GetString(2).Trim(),
                PreviousMasterId = reader.GetString(3).Trim(),
                State = reader.GetString(4).Trim(),
                VerifiedVersion = version,
                LastResponse = reader.IsDBNull(6) ? string.Empty : reader.GetString(6).Trim(),
                UpdatedUtc = updatedUtc,
                VoucherDate = voucherDate,
                VoucherType = ReadRecoveryVersionField(version, "TYPE"),
                Reference = ReadRecoveryVersionField(version, "REF"),
                Amount = Math.Abs(amount),
                Narration = ReadRecoveryVersionField(version, "NARR")
            });
        }
        return rows;
    }

    // R38 explicit generation-recovery review. RebindRequired still cannot flow directly
    // into CREATE. The caller must first perform a fresh full-FY Tally authority refresh and
    // obtain explicit user approval. This method preserves the previous MASTERID/version in
    // audit history and only promotes the reviewed row to DeletedInTally. The existing R37
    // RestoreRequested barrier and live duplicate/rebind classifier remain authoritative.
    public bool ConfirmRebindRequiredAsDeletedForExplicitRestore(
        long clientId, string financialYear, string companyKey, string entityType,
        string sourceAnchor, string reason)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor)) return false;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        string sourceKey;
        string masterId;
        string contentHash;
        string verifiedVersion;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT SourceKey,TallyMasterId,LastContentHash,LastVerifiedVersion,PendingVersion,State
                FROM TallySourceBindings
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
                LIMIT 1;
                """;
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            read.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            read.Parameters.AddWithValue("$EntityType", entityType.Trim());
            read.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return false;
            sourceKey = reader.GetString(0).Trim();
            masterId = reader.GetString(1).Trim();
            contentHash = reader.GetString(2).Trim();
            verifiedVersion = reader.GetString(3);
            var pendingVersion = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var currentState = reader.GetString(5).Trim();
            if (!currentState.Equals("RebindRequired", StringComparison.OrdinalIgnoreCase) ||
                masterId.Length == 0 || verifiedVersion.Length == 0 || pendingVersion.Length > 0)
                return false;
        }

        var response = string.IsNullOrWhiteSpace(reason)
            ? $"User explicitly reviewed missing prior MASTERID {masterId} after fresh full-FY authority and requested duplicate-safe recovery."
            : reason.Trim();
        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText =
                """
                INSERT INTO TallyObjectVersionHistory
                (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,SourceKey,TallyMasterId,PreviousContentHash,PreviousVersion,DesiredContentHash,DesiredVersion,Operation,Status,Response,CreatedUtc,VerifiedUtc)
                VALUES
                ($ClientId,$FinancialYear,$CompanyKey,$EntityType,$SourceAnchor,$SourceKey,$MasterId,$PreviousHash,$PreviousVersion,'','',
                 'EXPLICIT_REBIND_REVIEW','DeletedInTally',$Response,$Utc,'');
                """;
            history.Parameters.AddWithValue("$ClientId", clientId);
            history.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            history.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            history.Parameters.AddWithValue("$EntityType", entityType.Trim());
            history.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            history.Parameters.AddWithValue("$SourceKey", sourceKey);
            history.Parameters.AddWithValue("$MasterId", masterId);
            history.Parameters.AddWithValue("$PreviousHash", contentHash);
            history.Parameters.AddWithValue("$PreviousVersion", verifiedVersion);
            history.Parameters.AddWithValue("$Response", response);
            history.Parameters.AddWithValue("$Utc", now);
            history.ExecuteNonQuery();
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE TallySourceBindings
                SET State='DeletedInTally',LastResponse=$Response,UpdatedUtc=$Utc
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
                  AND TallyMasterId=$MasterId COLLATE NOCASE AND State='RebindRequired' COLLATE NOCASE
                  AND COALESCE(PendingVersion,'')='';
                """;
            update.Parameters.AddWithValue("$ClientId", clientId);
            update.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            update.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            update.Parameters.AddWithValue("$EntityType", entityType.Trim());
            update.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            update.Parameters.AddWithValue("$MasterId", masterId);
            update.Parameters.AddWithValue("$Response", response);
            update.Parameters.AddWithValue("$Utc", now);
            if (update.ExecuteNonQuery() != 1) return false;
        }
        transaction.Commit();
        return true;
    }

    // R37 explicit permission barrier. Only a stable isolated missing/deleted Voucher can
    // be moved to RestoreRequested. The old MASTERID/version are copied to history first and
    // then cleared so the existing full-authority duplicate classifier can either rebind an
    // exact replacement or CREATE one new Voucher. RebindRequired and conflict states remain blocked.
    public bool RequestVoucherRestoreToTally(
        long clientId, string financialYear, string companyKey, string entityType,
        string sourceAnchor, string expectedState, string reason,
        bool allowExplicitReviewedLocalChanges = false)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor)) return false;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        string sourceKey;
        string oldMasterId;
        string previousHash;
        string previousVersion;
        string pendingHash;
        string pendingVersion;
        string state;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT SourceKey,TallyMasterId,LastContentHash,LastVerifiedVersion,State,PendingVersion,PendingContentHash
                FROM TallySourceBindings
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
                LIMIT 1;
                """;
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            read.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            read.Parameters.AddWithValue("$EntityType", entityType.Trim());
            read.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return false;
            sourceKey = reader.GetString(0).Trim();
            oldMasterId = reader.GetString(1).Trim();
            previousHash = reader.GetString(2).Trim();
            previousVersion = reader.GetString(3);
            state = reader.GetString(4).Trim();
            pendingVersion = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            pendingHash = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            if (previousVersion.Length == 0 || oldMasterId.Length == 0)
                return false;
            // A user-approved ExternalDeleteConflict can be restored only after the
            // Tally page proved a complete fresh current-FY authority and confirmed
            // the old MASTERID is still absent. Its pending AAH edit is saved to
            // durable history rather than silently lost when staging the new CREATE.
            if (state.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase) &&
                !allowExplicitReviewedLocalChanges)
                return false;
            if (pendingVersion.Length > 0 && !state.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!state.Equals(expectedState?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                (state is not "DeletedInTally" and not "MissingLiveCandidate" and not "ExternalDeleteConflict"))
                return false;
        }

        var response = string.IsNullOrWhiteSpace(reason)
            ? $"User explicitly requested Restore-to-Tally for missing Voucher MASTERID {oldMasterId}."
            : reason.Trim();
        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText =
                """
                INSERT INTO TallyObjectVersionHistory
                (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,SourceKey,TallyMasterId,PreviousContentHash,PreviousVersion,DesiredContentHash,DesiredVersion,Operation,Status,Response,CreatedUtc,VerifiedUtc)
                VALUES
                ($ClientId,$FinancialYear,$CompanyKey,$EntityType,$SourceAnchor,$SourceKey,$MasterId,$PreviousHash,$PreviousVersion,$PendingHash,$PendingVersion,
                 'RESTORE_REQUEST','RestoreRequested',$Response,$Utc,'');
                """;
            history.Parameters.AddWithValue("$ClientId", clientId);
            history.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            history.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            history.Parameters.AddWithValue("$EntityType", entityType.Trim());
            history.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            history.Parameters.AddWithValue("$SourceKey", sourceKey);
            history.Parameters.AddWithValue("$MasterId", oldMasterId);
            history.Parameters.AddWithValue("$PreviousHash", previousHash);
            history.Parameters.AddWithValue("$PreviousVersion", previousVersion);
            history.Parameters.AddWithValue("$PendingHash", pendingHash);
            history.Parameters.AddWithValue("$PendingVersion", pendingVersion);
            history.Parameters.AddWithValue("$Response", response);
            history.Parameters.AddWithValue("$Utc", now);
            history.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE TallySourceBindings
                SET TallyMasterId='',TallyGuid='',PendingContentHash='',PendingVersion='',
                    State='RestoreRequested',LastResponse=$Response,UpdatedUtc=$Utc
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
                  AND TallyMasterId=$MasterId COLLATE NOCASE AND State=$ExpectedState COLLATE NOCASE;
                """;
            update.Parameters.AddWithValue("$ClientId", clientId);
            update.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            update.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            update.Parameters.AddWithValue("$EntityType", entityType.Trim());
            update.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            update.Parameters.AddWithValue("$MasterId", oldMasterId);
            update.Parameters.AddWithValue("$ExpectedState", state);
            update.Parameters.AddWithValue("$Response", response);
            update.Parameters.AddWithValue("$Utc", now);
            if (update.ExecuteNonQuery() != 1) return false;
        }
        transaction.Commit();
        return true;
    }

    // Fix20: Auto Recreate OFF cannot inherit an unsubmitted RestoreRequested
    // permission from an earlier interrupted automatic export. Only the exact
    // AUTO_RECREATE-owned pending state is reversible. This leaves explicit manual
    // Restore-to-Tally reviews, accepted writes, and all Tally data untouched.
    // Call under the existing serialized Tally export gate after fresh FY authority.
    public int CancelQueuedAutoRecreateRequests(
        long clientId, string financialYear, string companyKey)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rows = new List<(string EntityType, string Anchor, string Response, string MasterId)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT B.EntityType,B.SourceAnchor,B.LastResponse,
                       COALESCE((
                           SELECT H.TallyMasterId FROM TallyObjectVersionHistory H
                           WHERE H.ClientId=B.ClientId AND H.FinancialYear=B.FinancialYear
                             AND H.CompanyKey=B.CompanyKey AND H.EntityType=B.EntityType
                             AND H.SourceAnchor=B.SourceAnchor COLLATE NOCASE
                             AND H.Operation='RESTORE_REQUEST' AND H.Status='RestoreRequested'
                             AND H.Response LIKE 'AUTO_RECREATE:%'
                             AND TRIM(H.TallyMasterId)<>''
                           ORDER BY H.Id DESC LIMIT 1
                       ),'')
                FROM TallySourceBindings B
                WHERE B.ClientId=$ClientId AND B.FinancialYear=$FinancialYear
                  AND B.CompanyKey=$CompanyKey AND B.State='RestoreRequested' COLLATE NOCASE
                  AND B.LastResponse LIKE 'AUTO_RECREATE:%';
                """;
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            read.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var oldMasterId = reader.GetString(3).Trim();
                if (oldMasterId.Length > 0)
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), oldMasterId));
            }
        }
        var count = 0;
        foreach (var row in rows)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE TallySourceBindings
                SET TallyMasterId=$OldMasterId,State='DeletedInTally',
                    LastResponse='AUTO_RECREATE_CANCELLED: disabled before verified send; original MASTERID retained for reconciliation.',
                    UpdatedUtc=$Utc
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
                  AND State='RestoreRequested' COLLATE NOCASE
                  AND LastResponse=$PreviousResponse AND COALESCE(PendingVersion,'')='';
                """;
            update.Parameters.AddWithValue("$OldMasterId", row.MasterId);
            update.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$ClientId", clientId);
            update.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            update.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            update.Parameters.AddWithValue("$EntityType", row.EntityType);
            update.Parameters.AddWithValue("$SourceAnchor", row.Anchor);
            update.Parameters.AddWithValue("$PreviousResponse", row.Response);
            count += update.ExecuteNonQuery();
        }
        transaction.Commit();
        return count;
    }

    private static string ReadRecoveryVersionField(string version, string key)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(key)) return string.Empty;
        var prefix = key.Trim() + "=";
        foreach (var part in version.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return part[prefix.Length..].Trim();
        }
        return string.Empty;
    }

    // Fix85 source-level resolution after strict current authority found no exact
    // replacement for one missing previously-verified MASTERID. Preserve the old
    // MASTERID/version as audit evidence, clear no AAH source data, and make the state
    // permanently non-CREATE until a separate explicit Restore-to-Tally action exists.
    public bool MarkSourceBindingDeletedInTally(
        long clientId, string financialYear, string companyKey, string entityType,
        string sourceAnchor, string expectedMasterId, string response)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor) || string.IsNullOrWhiteSpace(expectedMasterId)) return false;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        string sourceKey = string.Empty;
        string previousHash = string.Empty;
        string previousVersion = string.Empty;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT SourceKey,LastContentHash,LastVerifiedVersion,PendingVersion,State
                FROM TallySourceBindings
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
                  AND TallyMasterId=$MasterId COLLATE NOCASE LIMIT 1;
                """;
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            read.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            read.Parameters.AddWithValue("$EntityType", entityType.Trim());
            read.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            read.Parameters.AddWithValue("$MasterId", expectedMasterId.Trim());
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return false;
            sourceKey = reader.GetString(0).Trim();
            previousHash = reader.GetString(1).Trim();
            previousVersion = reader.GetString(2);
            var pendingVersion = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var state = reader.GetString(4).Trim();
            if (pendingVersion.Length > 0 ||
                state.Equals("ModifyPending", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("AcceptedAwaitingVerification", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("CreatePending", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE TallySourceBindings
                SET PendingContentHash='',PendingVersion='',State='DeletedInTally',
                    LastResponse=$Response,UpdatedUtc=$Utc
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                  AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
                  AND TallyMasterId=$MasterId COLLATE NOCASE;
                """;
            update.Parameters.AddWithValue("$ClientId", clientId);
            update.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            update.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            update.Parameters.AddWithValue("$EntityType", entityType.Trim());
            update.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            update.Parameters.AddWithValue("$MasterId", expectedMasterId.Trim());
            update.Parameters.AddWithValue("$Response", response.Trim());
            update.Parameters.AddWithValue("$Utc", now);
            if (update.ExecuteNonQuery() == 0) return false;
        }

        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText =
                """
                INSERT INTO TallyObjectVersionHistory
                (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,SourceKey,TallyMasterId,PreviousContentHash,PreviousVersion,DesiredContentHash,DesiredVersion,Operation,Status,Response,CreatedUtc,VerifiedUtc)
                VALUES
                ($ClientId,$FinancialYear,$CompanyKey,$EntityType,$SourceAnchor,$SourceKey,$MasterId,$PreviousHash,$PreviousVersion,'','',
                 'EXTERNAL_DELETE','DeletedInTally',$Response,$Utc,$Utc);
                """;
            history.Parameters.AddWithValue("$ClientId", clientId);
            history.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            history.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            history.Parameters.AddWithValue("$EntityType", entityType.Trim());
            history.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            history.Parameters.AddWithValue("$SourceKey", sourceKey);
            history.Parameters.AddWithValue("$MasterId", expectedMasterId.Trim());
            history.Parameters.AddWithValue("$PreviousHash", previousHash);
            history.Parameters.AddWithValue("$PreviousVersion", previousVersion);
            history.Parameters.AddWithValue("$Response", response.Trim());
            history.Parameters.AddWithValue("$Utc", now);
            history.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    public TallySourceBindingVersionState? GetSourceBindingVersionState(
        long clientId, string financialYear, string companyKey, string entityType, string sourceAnchor)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor)) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TallyMasterId,TallyGuid,LastContentHash,PendingContentHash,State,
                   LastVerifiedVersion,PendingVersion,LastVerifiedAlterId,LastVerifiedUtc
            FROM TallySourceBindings
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
              AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$EntityType", entityType.Trim());
        command.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var verifiedUtc = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
        return new TallySourceBindingVersionState
        {
            MasterId = reader.GetString(0).Trim(),
            Guid = reader.GetString(1).Trim(),
            VerifiedContentHash = reader.GetString(2).Trim(),
            PendingContentHash = reader.GetString(3).Trim(),
            State = reader.GetString(4).Trim(),
            VerifiedVersion = reader.GetString(5),
            PendingVersion = reader.GetString(6),
            LastVerifiedAlterId = reader.IsDBNull(7) ? 0 : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
            LastVerifiedUtc = DateTime.TryParse(verifiedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTime.MinValue
        };
    }

    public void StageSourceBindingChange(
        long clientId, string financialYear, string companyKey, string entityType,
        string sourceAnchor, string sourceKey, string masterId, string guid,
        string desiredContentHash, string desiredVersion, string operation, string state, string response)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor)) return;
        var prior = GetSourceBindingVersionState(clientId, financialYear, companyKey, entityType, sourceAnchor);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO TallySourceBindings
                (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,SourceKey,TallyMasterId,TallyGuid,LastContentHash,PendingContentHash,LastVerifiedVersion,PendingVersion,LastVerifiedAlterId,LastVerifiedUtc,State,LastResponse,UpdatedUtc)
                VALUES
                ($ClientId,$FinancialYear,$CompanyKey,$EntityType,$SourceAnchor,$SourceKey,$MasterId,$Guid,'',$DesiredHash,'',$DesiredVersion,0,'',$State,$Response,$Now)
                ON CONFLICT(ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor) DO UPDATE SET
                  SourceKey=excluded.SourceKey,
                  TallyMasterId=CASE WHEN excluded.TallyMasterId<>'' THEN excluded.TallyMasterId ELSE TallySourceBindings.TallyMasterId END,
                  TallyGuid=CASE WHEN excluded.TallyGuid<>'' THEN excluded.TallyGuid ELSE TallySourceBindings.TallyGuid END,
                  PendingContentHash=excluded.PendingContentHash,
                  PendingVersion=excluded.PendingVersion,
                  State=excluded.State,
                  LastResponse=excluded.LastResponse,
                  UpdatedUtc=excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            command.Parameters.AddWithValue("$EntityType", entityType.Trim());
            command.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            command.Parameters.AddWithValue("$SourceKey", sourceKey.Trim());
            command.Parameters.AddWithValue("$MasterId", masterId?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$Guid", guid?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$DesiredHash", desiredContentHash?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$DesiredVersion", desiredVersion ?? string.Empty);
            command.Parameters.AddWithValue("$State", state?.Trim() ?? "Pending");
            command.Parameters.AddWithValue("$Response", response?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$Now", now);
            command.ExecuteNonQuery();
        }
        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText =
                """
                INSERT INTO TallyObjectVersionHistory
                (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,SourceKey,TallyMasterId,PreviousContentHash,PreviousVersion,DesiredContentHash,DesiredVersion,Operation,Status,Response,CreatedUtc,VerifiedUtc)
                VALUES
                ($ClientId,$FinancialYear,$CompanyKey,$EntityType,$SourceAnchor,$SourceKey,$MasterId,$PreviousHash,$PreviousVersion,$DesiredHash,$DesiredVersion,$Operation,$State,$Response,$Now,'');
                """;
            history.Parameters.AddWithValue("$ClientId", clientId);
            history.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            history.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            history.Parameters.AddWithValue("$EntityType", entityType.Trim());
            history.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            history.Parameters.AddWithValue("$SourceKey", sourceKey.Trim());
            history.Parameters.AddWithValue("$MasterId", masterId?.Trim() ?? prior?.MasterId ?? string.Empty);
            history.Parameters.AddWithValue("$PreviousHash", prior?.VerifiedContentHash ?? string.Empty);
            history.Parameters.AddWithValue("$PreviousVersion", prior?.VerifiedVersion ?? string.Empty);
            history.Parameters.AddWithValue("$DesiredHash", desiredContentHash?.Trim() ?? string.Empty);
            history.Parameters.AddWithValue("$DesiredVersion", desiredVersion ?? string.Empty);
            history.Parameters.AddWithValue("$Operation", operation?.Trim() ?? string.Empty);
            history.Parameters.AddWithValue("$State", state?.Trim() ?? "Pending");
            history.Parameters.AddWithValue("$Response", response?.Trim() ?? string.Empty);
            history.Parameters.AddWithValue("$Now", now);
            history.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void ConfirmSourceBindingVersion(
        long clientId, string financialYear, string companyKey, string entityType,
        string sourceAnchor, string sourceKey, string masterId, string guid,
        string verifiedContentHash, string verifiedVersion, long verifiedAlterId, string response)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor)) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO TallySourceBindings
                (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,SourceKey,TallyMasterId,TallyGuid,LastContentHash,PendingContentHash,LastVerifiedVersion,PendingVersion,LastVerifiedAlterId,LastVerifiedUtc,State,LastResponse,UpdatedUtc)
                VALUES
                ($ClientId,$FinancialYear,$CompanyKey,$EntityType,$SourceAnchor,$SourceKey,$MasterId,$Guid,$Hash,'',$Version,'',$AlterId,$Now,'Verified',$Response,$Now)
                ON CONFLICT(ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor) DO UPDATE SET
                  SourceKey=excluded.SourceKey,TallyMasterId=excluded.TallyMasterId,TallyGuid=excluded.TallyGuid,
                  LastContentHash=excluded.LastContentHash,PendingContentHash='',LastVerifiedVersion=excluded.LastVerifiedVersion,PendingVersion='',
                  LastVerifiedAlterId=excluded.LastVerifiedAlterId,LastVerifiedUtc=excluded.LastVerifiedUtc,
                  State='Verified',LastResponse=excluded.LastResponse,UpdatedUtc=excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            command.Parameters.AddWithValue("$EntityType", entityType.Trim());
            command.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            command.Parameters.AddWithValue("$SourceKey", sourceKey.Trim());
            command.Parameters.AddWithValue("$MasterId", masterId?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$Guid", guid?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$Hash", verifiedContentHash?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$Version", verifiedVersion ?? string.Empty);
            command.Parameters.AddWithValue("$AlterId", verifiedAlterId);
            command.Parameters.AddWithValue("$Now", now);
            command.Parameters.AddWithValue("$Response", response?.Trim() ?? string.Empty);
            command.ExecuteNonQuery();
        }
        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText =
                """
                UPDATE TallyObjectVersionHistory
                SET TallyMasterId=$MasterId,Status='Verified',Response=$Response,VerifiedUtc=$Now
                WHERE Id=(
                    SELECT Id FROM TallyObjectVersionHistory
                    WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
                      AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE
                      AND DesiredContentHash=$Hash AND DesiredVersion=$Version
                    ORDER BY Id DESC LIMIT 1
                );
                """;
            history.Parameters.AddWithValue("$MasterId", masterId?.Trim() ?? string.Empty);
            history.Parameters.AddWithValue("$Response", response?.Trim() ?? string.Empty);
            history.Parameters.AddWithValue("$Now", now);
            history.Parameters.AddWithValue("$ClientId", clientId);
            history.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            history.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            history.Parameters.AddWithValue("$EntityType", entityType.Trim());
            history.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
            history.Parameters.AddWithValue("$Hash", verifiedContentHash?.Trim() ?? string.Empty);
            history.Parameters.AddWithValue("$Version", verifiedVersion ?? string.Empty);
            history.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // Fix68: a source binding is durable evidence, but its Tally identity belongs to
    // the live Tally company generation. When one complete fresh FY authority proves
    // that the bound Voucher MASTERID no longer exists, release only the stale Tally
    // identity so the same source can be reclassified against current live Vouchers.
    // This is the recovery path for a restored/rebuilt Tally company; it never deletes
    // the AAH source key or its audit history.
    public void ClearSourceBindingTallyIdentity(
        long clientId, string financialYear, string companyKey, string entityType,
        string sourceAnchor, string state, string response)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor)) return;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TallySourceBindings
            SET TallyMasterId='',
                TallyGuid='',
                State=$State,
                LastResponse=$Response,
                UpdatedUtc=$UpdatedUtc
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
              AND EntityType=$EntityType AND SourceAnchor=$SourceAnchor COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$EntityType", entityType.Trim());
        command.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
        command.Parameters.AddWithValue("$State", state.Trim());
        command.Parameters.AddWithValue("$Response", response.Trim());
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpsertSourceBinding(
        long clientId, string financialYear, string companyKey, string entityType,
        string sourceAnchor, string sourceKey, string masterId, string guid,
        string contentHash, string state, string response)
    {
        if (string.IsNullOrWhiteSpace(sourceAnchor)) return;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TallySourceBindings
            (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,SourceKey,TallyMasterId,TallyGuid,LastContentHash,PendingContentHash,LastVerifiedUtc,State,LastResponse,UpdatedUtc)
            VALUES
            ($ClientId,$FinancialYear,$CompanyKey,$EntityType,$SourceAnchor,$SourceKey,$MasterId,$Guid,
             CASE WHEN $State='Verified' COLLATE NOCASE THEN $ContentHash ELSE '' END,
             CASE WHEN $State='Verified' COLLATE NOCASE THEN '' ELSE $ContentHash END,
             CASE WHEN $State='Verified' COLLATE NOCASE THEN $UpdatedUtc ELSE '' END,
             $State,$Response,$UpdatedUtc)
            ON CONFLICT(ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor)
            DO UPDATE SET SourceKey=excluded.SourceKey,
                          TallyMasterId=CASE WHEN excluded.TallyMasterId<>'' THEN excluded.TallyMasterId ELSE TallySourceBindings.TallyMasterId END,
                          TallyGuid=CASE WHEN excluded.TallyGuid<>'' THEN excluded.TallyGuid ELSE TallySourceBindings.TallyGuid END,
                          LastContentHash=CASE WHEN excluded.State='Verified' COLLATE NOCASE THEN excluded.LastContentHash ELSE TallySourceBindings.LastContentHash END,
                          PendingContentHash=CASE WHEN excluded.State='Verified' COLLATE NOCASE THEN '' ELSE excluded.PendingContentHash END,
                          LastVerifiedUtc=CASE WHEN excluded.State='Verified' COLLATE NOCASE THEN excluded.UpdatedUtc ELSE TallySourceBindings.LastVerifiedUtc END,
                          State=excluded.State,
                          LastResponse=excluded.LastResponse,
                          UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$EntityType", entityType.Trim());
        command.Parameters.AddWithValue("$SourceAnchor", sourceAnchor.Trim());
        command.Parameters.AddWithValue("$SourceKey", sourceKey.Trim());
        command.Parameters.AddWithValue("$MasterId", masterId.Trim());
        command.Parameters.AddWithValue("$Guid", guid.Trim());
        command.Parameters.AddWithValue("$ContentHash", contentHash.Trim());
        command.Parameters.AddWithValue("$State", state.Trim());
        command.Parameters.AddWithValue("$Response", response.Trim());
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public TallyVoucherIndexRecord? FindVoucherByMasterId(string companyKey, string financialYear, string masterId)
    {
        if (string.IsNullOrWhiteSpace(masterId)) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CompanyKey, FinancialYear, MasterId, AlterId, Guid, VoucherDate,
                   VoucherType, VoucherNumber, Reference, PartyLedger, DebitLedgers, CreditLedgers, Amount,
                   Narration, ContentHash, LastSeenUtc
            FROM TallyVoucherIndex
            WHERE CompanyKey=$CompanyKey AND FinancialYear=$FinancialYear AND MasterId=$MasterId COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$MasterId", masterId.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadVoucher(reader) : null;
    }

    public IReadOnlyList<TallyImportFingerprintAuditRow> GetImportFingerprintAudit(
        long clientId,
        string financialYear,
        string companyKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EntityType, SourceKey, ContentHash, Status, ResponseSummary,
                   FirstSeenUtc, LastSeenUtc, SeenCount
            FROM TallyImportFingerprints
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND CompanyKey = $CompanyKey
            ORDER BY LastSeenUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        using var reader = command.ExecuteReader();
        var rows = new List<TallyImportFingerprintAuditRow>();
        while (reader.Read())
        {
            rows.Add(new TallyImportFingerprintAuditRow
            {
                EntityType = reader.GetString(0),
                SourceKey = reader.GetString(1),
                ContentHash = reader.GetString(2),
                Status = reader.GetString(3),
                ResponseSummary = reader.GetString(4),
                FirstSeenUtc = DateTime.TryParse(reader.GetString(5), out var first) ? first : DateTime.MinValue,
                LastSeenUtc = DateTime.TryParse(reader.GetString(6), out var last) ? last : DateTime.MinValue,
                SeenCount = reader.GetInt32(7)
            });
        }
        return rows;
    }

    public void RecordImportFingerprints(
        long clientId,
        string financialYear,
        string companyKey,
        string entityType,
        IEnumerable<(string SourceKey, string ContentHash)> fingerprints,
        string status,
        string responseSummary)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        foreach (var item in fingerprints)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO TallyImportFingerprints
                (
                    ClientId, FinancialYear, CompanyKey, EntityType, SourceKey,
                    ContentHash, Status, ResponseSummary, FirstSeenUtc, LastSeenUtc, SeenCount
                )
                VALUES
                (
                    $ClientId, $FinancialYear, $CompanyKey, $EntityType, $SourceKey,
                    $ContentHash, $Status, $ResponseSummary, $Now, $Now, 1
                )
                ON CONFLICT(ClientId, FinancialYear, CompanyKey, EntityType, SourceKey, ContentHash)
                DO UPDATE SET
                    Status = excluded.Status,
                    ResponseSummary = excluded.ResponseSummary,
                    LastSeenUtc = excluded.LastSeenUtc,
                    SeenCount = TallyImportFingerprints.SeenCount + 1;
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
            command.Parameters.AddWithValue("$EntityType", entityType.Trim());
            command.Parameters.AddWithValue("$SourceKey", item.SourceKey.Trim());
            command.Parameters.AddWithValue("$ContentHash", item.ContentHash.Trim());
            command.Parameters.AddWithValue("$Status", status.Trim());
            command.Parameters.AddWithValue("$ResponseSummary", responseSummary.Trim());
            command.Parameters.AddWithValue("$Now", now);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static readonly string LinkSelectSql =
        """
        SELECT l.Id, l.ClientId, COALESCE(c.ClientCode, ''), COALESCE(c.ClientName, ''),
               l.CompanyKey, l.CompanyName, l.CompanyNumber, l.Gstin, l.StateName,
               l.BooksFrom, l.StartingFrom, l.FinancialYear, l.LinkedUtc, l.UpdatedUtc,
               l.LastSeenUtc, l.LastMasterSyncUtc, l.LastReportRefreshUtc, l.LastChangeToken,
               l.GroupCount, l.LedgerCount, l.VoucherTypeCount, l.StockItemCount,
               l.UnitCount, l.GodownCount, l.VoucherCount, l.MasterCount,
               l.DuplicateMasterCount, l.ConflictingMasterCount,
               l.DuplicateVoucherCount, l.ConflictingVoucherCount,
               l.BankImportFrom, l.BankImportTo, l.LastSyncStatus, l.LastError
        FROM TallyClientLinks l
        LEFT JOIN Clients c ON c.Id = l.ClientId
        """;

    private static TallyClientLink ReadLink(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            ClientId = reader.GetInt64(1),
            ClientCode = reader.GetString(2),
            ClientName = reader.GetString(3),
            CompanyKey = reader.GetString(4),
            CompanyName = reader.GetString(5),
            CompanyNumber = reader.GetString(6),
            Gstin = reader.GetString(7),
            StateName = reader.GetString(8),
            BooksFrom = reader.GetString(9),
            StartingFrom = reader.GetString(10),
            FinancialYear = reader.GetString(11),
            LinkedUtc = ParseDate(reader.GetString(12)),
            UpdatedUtc = ParseDate(reader.GetString(13)),
            LastSeenUtc = ParseDate(reader.GetString(14)),
            LastMasterSyncUtc = ParseDate(reader.GetString(15)),
            LastReportRefreshUtc = ParseDate(reader.GetString(16)),
            LastChangeToken = reader.GetString(17),
            GroupCount = reader.GetInt32(18),
            LedgerCount = reader.GetInt32(19),
            VoucherTypeCount = reader.GetInt32(20),
            StockItemCount = reader.GetInt32(21),
            UnitCount = reader.GetInt32(22),
            GodownCount = reader.GetInt32(23),
            VoucherCount = reader.GetInt32(24),
            MasterCount = reader.GetInt32(25),
            DuplicateMasterCount = reader.GetInt32(26),
            ConflictingMasterCount = reader.GetInt32(27),
            DuplicateVoucherCount = reader.GetInt32(28),
            ConflictingVoucherCount = reader.GetInt32(29),
            BankImportFrom = ParseNullableDate(reader.GetString(30)),
            BankImportTo = ParseNullableDate(reader.GetString(31)),
            LastSyncStatus = reader.GetString(32),
            LastError = reader.GetString(33)
        };

    public int DeleteLegacyTallyConvertedBankStatementLinks(string companyKey, long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM TallyBankStatementLinks
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
              AND TransferMatchId LIKE 'TALLYBTX|%'
              AND Status='Matched - imported Bank Statement transaction retired; Tally retained';
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        return command.ExecuteNonQuery();
    }

    public TallyBankStatementLinkRecord? GetBankStatementLink(string companyKey, long clientId, string financialYear, string transferMatchId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ClientId,FinancialYear,CompanyKey,TransferMatchId,SourceTransactionId,TargetTransactionId,TransactionDate,
                   TallyMasterId,TallyAlterId,PostingSequence,ConvertedBankLedgerMasterId,ConvertedBankLedgerGuid,ConvertedBankLedgerAlterId,
                   SourceBankLedgerMasterId,TargetBankLedgerMasterId,OriginalVoucherType,OriginalReference,OriginalNarration,
                   OriginalDebitLedgers,OriginalCreditLedgers,OriginalAmount,FinalVoucherType,FinalDebitLedger,FinalCreditLedger,
                   MatchEvidence,Status,CreatedUtc,UpdatedUtc
            FROM TallyBankStatementLinks
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey AND TransferMatchId=$TransferMatchId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$TransferMatchId", transferMatchId.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBankStatementLink(reader) : null;
    }

    public IReadOnlyList<TallyBankStatementLinkRecord> GetBankStatementLinks(string companyKey, long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ClientId,FinancialYear,CompanyKey,TransferMatchId,SourceTransactionId,TargetTransactionId,TransactionDate,
                   TallyMasterId,TallyAlterId,PostingSequence,ConvertedBankLedgerMasterId,ConvertedBankLedgerGuid,ConvertedBankLedgerAlterId,
                   SourceBankLedgerMasterId,TargetBankLedgerMasterId,OriginalVoucherType,OriginalReference,OriginalNarration,
                   OriginalDebitLedgers,OriginalCreditLedgers,OriginalAmount,FinalVoucherType,FinalDebitLedger,FinalCreditLedger,
                   MatchEvidence,Status,CreatedUtc,UpdatedUtc
            FROM TallyBankStatementLinks
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CompanyKey=$CompanyKey
            ORDER BY UpdatedUtc DESC, TransferMatchId;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        using var reader = command.ExecuteReader();
        var rows = new List<TallyBankStatementLinkRecord>();
        while (reader.Read()) rows.Add(ReadBankStatementLink(reader));
        return rows;
    }

    public void UpsertBankStatementLink(TallyBankStatementLinkRecord record)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TallyBankStatementLinks
            (ClientId,FinancialYear,CompanyKey,TransferMatchId,SourceTransactionId,TargetTransactionId,TransactionDate,TallyMasterId,TallyAlterId,
             PostingSequence,ConvertedBankLedgerMasterId,ConvertedBankLedgerGuid,ConvertedBankLedgerAlterId,SourceBankLedgerMasterId,TargetBankLedgerMasterId,
             OriginalVoucherType,OriginalReference,OriginalNarration,OriginalDebitLedgers,OriginalCreditLedgers,OriginalAmount,
             FinalVoucherType,FinalDebitLedger,FinalCreditLedger,MatchEvidence,Status,CreatedUtc,UpdatedUtc)
            VALUES
            ($ClientId,$FinancialYear,$CompanyKey,$TransferMatchId,$SourceTransactionId,$TargetTransactionId,$TransactionDate,$TallyMasterId,$TallyAlterId,
             $PostingSequence,$ConvertedBankLedgerMasterId,$ConvertedBankLedgerGuid,$ConvertedBankLedgerAlterId,$SourceBankLedgerMasterId,$TargetBankLedgerMasterId,
             $OriginalVoucherType,$OriginalReference,$OriginalNarration,$OriginalDebitLedgers,$OriginalCreditLedgers,$OriginalAmount,
             $FinalVoucherType,$FinalDebitLedger,$FinalCreditLedger,$MatchEvidence,$Status,$CreatedUtc,$UpdatedUtc)
            ON CONFLICT(ClientId,FinancialYear,CompanyKey,TransferMatchId) DO UPDATE SET
              SourceTransactionId=excluded.SourceTransactionId,TargetTransactionId=excluded.TargetTransactionId,
              TransactionDate=CASE WHEN TRIM(TransactionDate)='' AND TRIM(excluded.TransactionDate)<>'' THEN excluded.TransactionDate ELSE TransactionDate END,
              TallyMasterId=excluded.TallyMasterId,TallyAlterId=excluded.TallyAlterId,
              PostingSequence=CASE WHEN excluded.PostingSequence>0 THEN excluded.PostingSequence ELSE PostingSequence END,
              ConvertedBankLedgerMasterId=CASE WHEN TRIM(excluded.ConvertedBankLedgerMasterId)<>'' THEN excluded.ConvertedBankLedgerMasterId ELSE ConvertedBankLedgerMasterId END,
              ConvertedBankLedgerGuid=CASE WHEN TRIM(excluded.ConvertedBankLedgerGuid)<>'' THEN excluded.ConvertedBankLedgerGuid ELSE ConvertedBankLedgerGuid END,
              ConvertedBankLedgerAlterId=CASE WHEN excluded.ConvertedBankLedgerAlterId>0 THEN excluded.ConvertedBankLedgerAlterId ELSE ConvertedBankLedgerAlterId END,
              SourceBankLedgerMasterId=CASE WHEN TRIM(excluded.SourceBankLedgerMasterId)<>'' THEN excluded.SourceBankLedgerMasterId ELSE SourceBankLedgerMasterId END,
              TargetBankLedgerMasterId=CASE WHEN TRIM(excluded.TargetBankLedgerMasterId)<>'' THEN excluded.TargetBankLedgerMasterId ELSE TargetBankLedgerMasterId END,
              -- Preserve the first captured pre-AAH Tally voucher snapshot permanently. Completion 29
              -- may safely fill only fields that older builds captured as blank/zero; a populated
              -- original field is never overwritten by a later already-altered Tally voucher.
              OriginalNarration=CASE WHEN TRIM(OriginalNarration)='' AND TRIM(excluded.OriginalNarration)<>'' THEN excluded.OriginalNarration ELSE OriginalNarration END,
              OriginalDebitLedgers=CASE WHEN TRIM(OriginalDebitLedgers)='' AND TRIM(excluded.OriginalDebitLedgers)<>'' THEN excluded.OriginalDebitLedgers ELSE OriginalDebitLedgers END,
              OriginalCreditLedgers=CASE WHEN TRIM(OriginalCreditLedgers)='' AND TRIM(excluded.OriginalCreditLedgers)<>'' THEN excluded.OriginalCreditLedgers ELSE OriginalCreditLedgers END,
              OriginalAmount=CASE WHEN ABS(OriginalAmount)<0.000001 AND ABS(excluded.OriginalAmount)>=0.000001 THEN excluded.OriginalAmount ELSE OriginalAmount END,
              FinalVoucherType=excluded.FinalVoucherType,FinalDebitLedger=excluded.FinalDebitLedger,
              FinalCreditLedger=excluded.FinalCreditLedger,MatchEvidence=excluded.MatchEvidence,
              Status=excluded.Status,UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", record.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", record.FinancialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", record.CompanyKey.Trim());
        command.Parameters.AddWithValue("$TransferMatchId", record.TransferMatchId.Trim());
        command.Parameters.AddWithValue("$SourceTransactionId", record.SourceTransactionId);
        command.Parameters.AddWithValue("$TargetTransactionId", record.TargetTransactionId);
        command.Parameters.AddWithValue("$TransactionDate", record.TransactionDate == default ? string.Empty : record.TransactionDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$TallyMasterId", record.TallyMasterId.Trim());
        command.Parameters.AddWithValue("$TallyAlterId", record.TallyAlterId.Trim());
        command.Parameters.AddWithValue("$PostingSequence", record.PostingSequence);
        command.Parameters.AddWithValue("$ConvertedBankLedgerMasterId", record.ConvertedBankLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$ConvertedBankLedgerGuid", record.ConvertedBankLedgerGuid.Trim());
        command.Parameters.AddWithValue("$ConvertedBankLedgerAlterId", record.ConvertedBankLedgerAlterId);
        command.Parameters.AddWithValue("$SourceBankLedgerMasterId", record.SourceBankLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$TargetBankLedgerMasterId", record.TargetBankLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$OriginalVoucherType", record.OriginalVoucherType.Trim());
        command.Parameters.AddWithValue("$OriginalReference", record.OriginalReference.Trim());
        command.Parameters.AddWithValue("$OriginalNarration", record.OriginalNarration.Trim());
        command.Parameters.AddWithValue("$OriginalDebitLedgers", record.OriginalDebitLedgers.Trim());
        command.Parameters.AddWithValue("$OriginalCreditLedgers", record.OriginalCreditLedgers.Trim());
        command.Parameters.AddWithValue("$OriginalAmount", record.OriginalAmount);
        command.Parameters.AddWithValue("$FinalVoucherType", record.FinalVoucherType.Trim());
        command.Parameters.AddWithValue("$FinalDebitLedger", record.FinalDebitLedger.Trim());
        command.Parameters.AddWithValue("$FinalCreditLedger", record.FinalCreditLedger.Trim());
        command.Parameters.AddWithValue("$MatchEvidence", record.MatchEvidence.Trim());
        command.Parameters.AddWithValue("$Status", record.Status.Trim());
        command.Parameters.AddWithValue("$CreatedUtc", (record.CreatedUtc == default ? DateTime.UtcNow : record.CreatedUtc).ToString("O"));
        command.Parameters.AddWithValue("$UpdatedUtc", (record.UpdatedUtc == default ? DateTime.UtcNow : record.UpdatedUtc).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static TallyBankStatementLinkRecord ReadBankStatementLink(SqliteDataReader reader) => new()
    {
        ClientId=reader.GetInt64(0), FinancialYear=reader.GetString(1), CompanyKey=reader.GetString(2), TransferMatchId=reader.GetString(3),
        SourceTransactionId=reader.GetInt64(4), TargetTransactionId=reader.GetInt64(5), TransactionDate=ParseNullableDate(reader.GetString(6)) ?? DateTime.MinValue,
        TallyMasterId=reader.GetString(7), TallyAlterId=reader.GetString(8), PostingSequence=reader.GetInt32(9),
        ConvertedBankLedgerMasterId=reader.GetString(10), ConvertedBankLedgerGuid=reader.GetString(11), ConvertedBankLedgerAlterId=reader.GetInt64(12),
        SourceBankLedgerMasterId=reader.GetString(13), TargetBankLedgerMasterId=reader.GetString(14), OriginalVoucherType=reader.GetString(15), OriginalReference=reader.GetString(16), OriginalNarration=reader.GetString(17),
        OriginalDebitLedgers=reader.GetString(18), OriginalCreditLedgers=reader.GetString(19), OriginalAmount=Convert.ToDecimal(reader.GetValue(20)), FinalVoucherType=reader.GetString(21),
        FinalDebitLedger=reader.GetString(22), FinalCreditLedger=reader.GetString(23), MatchEvidence=reader.GetString(24), Status=reader.GetString(25),
        CreatedUtc=ParseDate(reader.GetString(26)), UpdatedUtc=ParseDate(reader.GetString(27))
    };

    private static TallyVoucherIndexRecord ReadVoucher(SqliteDataReader reader) =>
        new()
        {
            CompanyKey = reader.GetString(0),
            FinancialYear = reader.GetString(1),
            MasterId = reader.GetString(2),
            AlterId = reader.GetString(3),
            Guid = reader.GetString(4),
            VoucherDate = ParseNullableDate(reader.GetString(5)),
            VoucherType = reader.GetString(6),
            VoucherNumber = reader.GetString(7),
            Reference = reader.GetString(8),
            PartyLedger = reader.GetString(9),
            DebitLedgers = reader.GetString(10),
            CreditLedgers = reader.GetString(11),
            Amount = Convert.ToDecimal(reader.GetDouble(12)),
            Narration = reader.GetString(13),
            ContentHash = reader.GetString(14),
            LastSeenUtc = ParseDate(reader.GetString(15))
        };

    private static void AddCompanyParameters(
        SqliteCommand command,
        long clientId,
        TallyCompanyInfo company,
        string now)
    {
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$CompanyKey", company.CompanyKey.Trim());
        command.Parameters.AddWithValue("$CompanyName", company.Name.Trim());
        command.Parameters.AddWithValue("$CompanyNumber", company.CompanyNumber.Trim());
        command.Parameters.AddWithValue("$Gstin", company.Gstin.Trim());
        command.Parameters.AddWithValue("$StateName", company.StateName.Trim());
        command.Parameters.AddWithValue("$BooksFrom", company.BooksFrom.Trim());
        command.Parameters.AddWithValue("$StartingFrom", company.StartingFrom.Trim());
        command.Parameters.AddWithValue("$FinancialYear", company.FinancialYear.Trim());
        command.Parameters.AddWithValue("$LinkedUtc", now);
        command.Parameters.AddWithValue("$UpdatedUtc", now);
        command.Parameters.AddWithValue("$LastSeenUtc", now);
        command.Parameters.AddWithValue("$LastChangeToken", company.ChangeToken.Trim());
        command.Parameters.AddWithValue("$GroupCount", company.GroupCount);
        command.Parameters.AddWithValue("$LedgerCount", company.LedgerCount);
        command.Parameters.AddWithValue("$VoucherTypeCount", company.VoucherTypeCount);
        command.Parameters.AddWithValue("$StockItemCount", company.StockItemCount);
        command.Parameters.AddWithValue("$UnitCount", company.UnitCount);
        command.Parameters.AddWithValue("$GodownCount", company.GodownCount);
        command.Parameters.AddWithValue("$VoucherCount", company.VoucherCount);
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, out var parsed)
            ? parsed
            : DateTime.MinValue;

    private static DateTime? ParseNullableDate(string value) =>
        DateTime.TryParse(value, out var parsed)
            ? parsed
            : null;
}
