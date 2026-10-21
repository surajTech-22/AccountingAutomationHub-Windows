using System.Globalization;
using AccountingAutomationHub.Domain.Audit;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Audit;

public sealed class SqliteAuditRepository
{
    private const string CurrentAuditClientSetting = "CurrentAuditClientId";
    private readonly string _auditConnectionString;
    private readonly string _accountingConnectionString;

    public SqliteAuditRepository()
    {
        ApplicationDataPaths.EnsureFolders();
        _auditConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.AuditDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        _accountingConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public void Initialize() =>
        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteAuditRepository), _auditConnectionString, InitializeCore);

    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS AuditClients
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientCode              TEXT NOT NULL COLLATE NOCASE,
                ClientName              TEXT NOT NULL,
                LegalName               TEXT NOT NULL DEFAULT '',
                TradeName               TEXT NOT NULL DEFAULT '',
                FinancialYear           TEXT NOT NULL DEFAULT '',
                Gstin                   TEXT NOT NULL DEFAULT '',
                Pan                     TEXT NOT NULL DEFAULT '',
                State                   TEXT NOT NULL DEFAULT '',
                Address                 TEXT NOT NULL DEFAULT '',
                Phone                   TEXT NOT NULL DEFAULT '',
                Email                   TEXT NOT NULL DEFAULT '',
                Notes                   TEXT NOT NULL DEFAULT '',
                LinkedAccountingClientId INTEGER NULL,
                LinkedTallyCompanyKey   TEXT NOT NULL DEFAULT '',
                LinkedTallyCompanyName  TEXT NOT NULL DEFAULT '',
                IsArchived              INTEGER NOT NULL DEFAULT 0,
                CreatedUtc              TEXT NOT NULL,
                UpdatedUtc              TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_AuditClients_ClientCode
            ON AuditClients(ClientCode COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS IX_AuditClients_ClientName
            ON AuditClients(ClientName COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS AuditWorkspaceSettings
            (
                SettingKey      TEXT PRIMARY KEY COLLATE NOCASE,
                SettingValue    TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS AuditRecords
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                AuditClientId   INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '',
                Module          TEXT NOT NULL DEFAULT '',
                RecordDate      TEXT NOT NULL DEFAULT '',
                Reference       TEXT NOT NULL DEFAULT '',
                VoucherType     TEXT NOT NULL DEFAULT '',
                AuthorityId     TEXT NOT NULL DEFAULT '',
                PartyOrLedger   TEXT NOT NULL DEFAULT '',
                Details         TEXT NOT NULL DEFAULT '',
                Debit           TEXT NOT NULL DEFAULT '0',
                Credit          TEXT NOT NULL DEFAULT '0',
                Amount          TEXT NOT NULL DEFAULT '0',
                Status          TEXT NOT NULL DEFAULT '',
                Source          TEXT NOT NULL DEFAULT '',
                OriginKind      TEXT NOT NULL DEFAULT 'Audit',
                OriginKey       TEXT NOT NULL DEFAULT '',
                PayloadJson     TEXT NOT NULL DEFAULT '',
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY(AuditClientId) REFERENCES AuditClients(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AuditRecords_ClientFyModule
            ON AuditRecords(AuditClientId, FinancialYear, Module COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS AuditEntryAdjustments
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                AuditClientId   INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '',
                Module          TEXT NOT NULL DEFAULT '',
                SourceRecordId  INTEGER NOT NULL DEFAULT 0,
                Source          TEXT NOT NULL DEFAULT '',
                SourceIdentity  TEXT NOT NULL DEFAULT '',
                TallyMasterId   TEXT NOT NULL DEFAULT '',
                TallyGuid       TEXT NOT NULL DEFAULT '',
                Reason          TEXT NOT NULL DEFAULT '',
                Action          TEXT NOT NULL DEFAULT '',
                Status          TEXT NOT NULL DEFAULT 'Suggested',
                OriginalJson    TEXT NOT NULL DEFAULT '',
                ProposedJson    TEXT NOT NULL DEFAULT '',
                Failure         TEXT NOT NULL DEFAULT '',
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY(AuditClientId) REFERENCES AuditClients(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AuditEntryAdjustments_ClientFyModule
            ON AuditEntryAdjustments(AuditClientId, FinancialYear, Module COLLATE NOCASE, UpdatedUtc DESC);

            CREATE UNIQUE INDEX IF NOT EXISTS UX_AuditRecords_Origin
            ON AuditRecords(AuditClientId, FinancialYear, Module COLLATE NOCASE, OriginKind COLLATE NOCASE, OriginKey COLLATE NOCASE)
            WHERE OriginKey <> '';

            CREATE TABLE IF NOT EXISTS AuditTallyLedgers
            (
                AuditClientId   INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '',
                CompanyKey      TEXT NOT NULL DEFAULT '',
                ExactName       TEXT NOT NULL DEFAULT '',
                DisplayName     TEXT NOT NULL DEFAULT '',
                Parent          TEXT NOT NULL DEFAULT '',
                Gstin           TEXT NOT NULL DEFAULT '',
                OpeningBalance  TEXT NOT NULL DEFAULT '',
                ClosingBalance  TEXT NOT NULL DEFAULT '',
                MasterId        TEXT NOT NULL DEFAULT '',
                Guid            TEXT NOT NULL DEFAULT '',
                AlterId         INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc      TEXT NOT NULL,
                PRIMARY KEY(AuditClientId, FinancialYear, CompanyKey, DisplayName COLLATE NOCASE),
                FOREIGN KEY(AuditClientId) REFERENCES AuditClients(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AuditTallyLedgers_Group
            ON AuditTallyLedgers(AuditClientId, FinancialYear, CompanyKey, Parent COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS AuditBankStatements
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                AuditClientId   INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '',
                AccountName     TEXT NOT NULL DEFAULT '',
                AccountCategory TEXT NOT NULL DEFAULT 'Bank',
                AccountIdentity TEXT NOT NULL DEFAULT '',
                SourcePath      TEXT NOT NULL DEFAULT '',
                SourceFile      TEXT NOT NULL DEFAULT '',
                SourceSheet     TEXT NOT NULL DEFAULT '',
                FileHash        TEXT NOT NULL DEFAULT '',
                PeriodFrom      TEXT NOT NULL DEFAULT '',
                PeriodTo        TEXT NOT NULL DEFAULT '',
                OpeningBalance  TEXT NOT NULL DEFAULT '',
                ClosingBalance  TEXT NOT NULL DEFAULT '',
                TotalDebit      TEXT NOT NULL DEFAULT '0',
                TotalCredit     TEXT NOT NULL DEFAULT '0',
                TransactionCount INTEGER NOT NULL DEFAULT 0,
                ParseStatus     TEXT NOT NULL DEFAULT '',
                Notes           TEXT NOT NULL DEFAULT '',
                ImportedUtc     TEXT NOT NULL,
                FOREIGN KEY(AuditClientId) REFERENCES AuditClients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_AuditBankStatements_Source
            ON AuditBankStatements(AuditClientId, FinancialYear, FileHash COLLATE NOCASE, SourceSheet COLLATE NOCASE, AccountName COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS IX_AuditBankStatements_Account
            ON AuditBankStatements(AuditClientId, FinancialYear, AccountName COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS AuditBankStatementTransactions
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                StatementId     INTEGER NOT NULL,
                AuditClientId   INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '',
                AccountName     TEXT NOT NULL DEFAULT '',
                AccountCategory TEXT NOT NULL DEFAULT 'Bank',
                TransactionDate TEXT NOT NULL DEFAULT '',
                Reference       TEXT NOT NULL DEFAULT '',
                Narration       TEXT NOT NULL DEFAULT '',
                Debit           TEXT NOT NULL DEFAULT '0',
                Credit          TEXT NOT NULL DEFAULT '0',
                RunningBalance  TEXT NOT NULL DEFAULT '',
                SourceRow       INTEGER NOT NULL DEFAULT 0,
                SourcePage      INTEGER NOT NULL DEFAULT 0,
                SourceLocation  TEXT NOT NULL DEFAULT '',
                OriginKey       TEXT NOT NULL DEFAULT '',
                FOREIGN KEY(StatementId) REFERENCES AuditBankStatements(Id) ON DELETE CASCADE,
                FOREIGN KEY(AuditClientId) REFERENCES AuditClients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_AuditBankStatementTransactions_Origin
            ON AuditBankStatementTransactions(AuditClientId, FinancialYear, OriginKey COLLATE NOCASE)
            WHERE OriginKey <> '';

            CREATE INDEX IF NOT EXISTS IX_AuditBankStatementTransactions_AccountDate
            ON AuditBankStatementTransactions(AuditClientId, FinancialYear, AccountName COLLATE NOCASE, TransactionDate);

            CREATE TABLE IF NOT EXISTS AuditTallyGroups
            (
                AuditClientId   INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '',
                CompanyKey      TEXT NOT NULL DEFAULT '',
                Name            TEXT NOT NULL DEFAULT '',
                Parent          TEXT NOT NULL DEFAULT '',
                UpdatedUtc      TEXT NOT NULL,
                PRIMARY KEY(AuditClientId, FinancialYear, CompanyKey, Name COLLATE NOCASE),
                FOREIGN KEY(AuditClientId) REFERENCES AuditClients(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS AuditTallySyncState
            (
                AuditClientId    INTEGER NOT NULL,
                FinancialYear    TEXT NOT NULL DEFAULT '',
                CompanyKey       TEXT NOT NULL DEFAULT '',
                LastChangeToken  TEXT NOT NULL DEFAULT '',
                LastVoucherCount INTEGER NOT NULL DEFAULT 0,
                LastLedgerCount  INTEGER NOT NULL DEFAULT 0,
                LastGroupCount   INTEGER NOT NULL DEFAULT 0,
                LastAlterId      INTEGER NOT NULL DEFAULT 0,
                LastFullSyncUtc  TEXT NOT NULL DEFAULT '',
                LastDeltaSyncUtc TEXT NOT NULL DEFAULT '',
                LastPulseUtc     TEXT NOT NULL DEFAULT '',
                LastStatus       TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(AuditClientId, FinancialYear, CompanyKey),
                FOREIGN KEY(AuditClientId) REFERENCES AuditClients(Id) ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "AuditClients", "LegalName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditClients", "TradeName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditClients", "State", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditClients", "Address", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditClients", "Phone", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditClients", "Email", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditTallyLedgers", "OpeningBalance", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditTallyLedgers", "ClosingBalance", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditTallyLedgers", "Gstin", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AuditRecords", "PayloadJson", "TEXT NOT NULL DEFAULT ''");
    }

    public IReadOnlyList<AuditClientProfile> GetClients(bool includeArchived = false)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientCode, ClientName, LegalName, TradeName, FinancialYear, Gstin, Pan, State, Address, Phone, Email, Notes,
                   LinkedAccountingClientId, LinkedTallyCompanyKey, LinkedTallyCompanyName,
                   IsArchived, CreatedUtc, UpdatedUtc
            FROM AuditClients
            WHERE $includeArchived = 1 OR IsArchived = 0
            ORDER BY ClientName COLLATE NOCASE, FinancialYear COLLATE NOCASE, ClientCode COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        using var reader = command.ExecuteReader();
        var result = new List<AuditClientProfile>();
        while (reader.Read()) result.Add(ReadClient(reader));
        return result;
    }

    public AuditClientProfile? GetClient(long id)
    {
        if (id <= 0) return null;
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientCode, ClientName, LegalName, TradeName, FinancialYear, Gstin, Pan, State, Address, Phone, Email, Notes,
                   LinkedAccountingClientId, LinkedTallyCompanyKey, LinkedTallyCompanyName,
                   IsArchived, CreatedUtc, UpdatedUtc
            FROM AuditClients WHERE Id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadClient(reader) : null;
    }

    public AuditClientProfile? GetCurrentClient()
    {
        var id = GetCurrentClientId();
        return id > 0 ? GetClient(id) : null;
    }

    public long GetCurrentClientId()
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettingValue FROM AuditWorkspaceSettings WHERE SettingKey = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", CurrentAuditClientSetting);
        var value = command.ExecuteScalar()?.ToString();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    public void SetCurrentClient(long id)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AuditWorkspaceSettings(SettingKey, SettingValue)
            VALUES($key, $value)
            ON CONFLICT(SettingKey) DO UPDATE SET SettingValue = excluded.SettingValue;
            """;
        command.Parameters.AddWithValue("$key", CurrentAuditClientSetting);
        command.Parameters.AddWithValue("$value", id > 0 ? id.ToString(CultureInfo.InvariantCulture) : string.Empty);
        command.ExecuteNonQuery();
    }

    public string GetWorkspaceSetting(string key, string fallback = "")
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettingValue FROM AuditWorkspaceSettings WHERE SettingKey = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", (key ?? string.Empty).Trim());
        return command.ExecuteScalar()?.ToString() ?? fallback;
    }

    public void SaveWorkspaceSetting(string key, string value)
    {
        Initialize();
        var normalizedKey = (key ?? string.Empty).Trim();
        if (normalizedKey.Length == 0) return;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AuditWorkspaceSettings(SettingKey, SettingValue)
            VALUES($key, $value)
            ON CONFLICT(SettingKey) DO UPDATE SET SettingValue = excluded.SettingValue;
            """;
        command.Parameters.AddWithValue("$key", normalizedKey);
        command.Parameters.AddWithValue("$value", value ?? string.Empty);
        command.ExecuteNonQuery();
    }

    public AuditClientProfile SaveClient(AuditClientProfile client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var code = (client.ClientCode ?? string.Empty).Trim();
        var name = (client.ClientName ?? string.Empty).Trim();
        var fy = (client.FinancialYear ?? string.Empty).Trim();
        if (code.Length == 0) throw new InvalidOperationException("Audit Client Code is required.");
        if (name.Length == 0) throw new InvalidOperationException("Audit Client Name is required.");
        if (fy.Length == 0) throw new InvalidOperationException("Audit Financial Year is required.");

        Initialize();
        if (!string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey))
            ValidateSharedTallyOwnership(client, client.LinkedTallyCompanyKey);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (client.Id <= 0)
        {
            command.CommandText =
                """
                INSERT INTO AuditClients
                (ClientCode, ClientName, LegalName, TradeName, FinancialYear, Gstin, Pan, State, Address, Phone, Email, Notes,
                 LinkedAccountingClientId, LinkedTallyCompanyKey, LinkedTallyCompanyName,
                 IsArchived, CreatedUtc, UpdatedUtc)
                VALUES
                ($code, $name, $legalName, $tradeName, $fy, $gstin, $pan, $state, $address, $phone, $email, $notes,
                 $accountingId, $tallyKey, $tallyName, 0, $now, $now);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE AuditClients SET
                    ClientCode = $code,
                    ClientName = $name,
                    LegalName = $legalName,
                    TradeName = $tradeName,
                    FinancialYear = $fy,
                    Gstin = $gstin,
                    Pan = $pan,
                    State = $state,
                    Address = $address,
                    Phone = $phone,
                    Email = $email,
                    Notes = $notes,
                    LinkedAccountingClientId = $accountingId,
                    LinkedTallyCompanyKey = $tallyKey,
                    LinkedTallyCompanyName = $tallyName,
                    IsArchived = $archived,
                    UpdatedUtc = $now
                WHERE Id = $id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", client.Id);
            command.Parameters.AddWithValue("$archived", client.IsArchived ? 1 : 0);
        }
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$legalName", (client.LegalName ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$tradeName", (client.TradeName ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$fy", fy);
        command.Parameters.AddWithValue("$gstin", (client.Gstin ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$pan", (client.Pan ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$state", (client.State ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$address", (client.Address ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$phone", (client.Phone ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$email", (client.Email ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$notes", (client.Notes ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$accountingId", client.LinkedAccountingClientId.HasValue ? client.LinkedAccountingClientId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$tallyKey", (client.LinkedTallyCompanyKey ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$tallyName", (client.LinkedTallyCompanyName ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$now", now);
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return GetClient(id) ?? throw new InvalidOperationException("Audit client could not be read after save.");
    }

    public void ArchiveClient(long id, bool archived)
    {
        if (id <= 0) return;
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AuditClients SET IsArchived = $archived, UpdatedUtc = $now WHERE Id = $id;";
        command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        if (archived && GetCurrentClientId() == id) SetCurrentClient(0);
    }


    public IReadOnlyList<AuditAccountingClientOption> GetAccountingClientOptions()
    {
        Initialize();
        using var connection = OpenAccountingConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientCode, ClientName, FinancialYear, Gstin
            FROM Clients
            WHERE IsArchived = 0
            ORDER BY ClientName COLLATE NOCASE, FinancialYear COLLATE NOCASE, ClientCode COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<AuditAccountingClientOption>();
        while (reader.Read())
        {
            result.Add(new AuditAccountingClientOption
            {
                Id = reader.GetInt64(0),
                ClientCode = reader.GetString(1),
                ClientName = reader.GetString(2),
                FinancialYear = reader.GetString(3),
                Gstin = reader.GetString(4)
            });
        }
        return result;
    }

    public void ValidateSharedTallyOwnership(AuditClientProfile client, string companyKey)
    {
        ArgumentNullException.ThrowIfNull(client);
        var key = (companyKey ?? string.Empty).Trim();
        if (key.Length == 0) return;
        Initialize();

        // R4 R9 independent Audit authority: a live Tally CompanyKey is owned only within
        // the Audit client directory. Accounting Tally links are neither read nor changed.
        // This keeps Audit independent while still preventing two unrelated Audit clients
        // from consuming the same live Tally authority accidentally.
        using var auditConnection = OpenConnection();
        using var auditCommand = auditConnection.CreateCommand();
        auditCommand.CommandText =
            """
            SELECT ClientCode, ClientName
            FROM AuditClients
            WHERE IsArchived = 0
              AND LinkedTallyCompanyKey = $key COLLATE NOCASE
              AND Id <> $id
            LIMIT 1;
            """;
        auditCommand.Parameters.AddWithValue("$key", key);
        auditCommand.Parameters.AddWithValue("$id", client.Id);
        using var reader = auditCommand.ExecuteReader();
        if (reader.Read())
        {
            var code = reader.GetString(0);
            var name = reader.GetString(1);
            throw new InvalidOperationException(
                $"This Tally company is already linked to Audit client {code} - {name}. Unlink it there first.");
        }
    }

    public IReadOnlyList<AuditTallyCompanyOption> GetTallyCompanyOptions()
    {
        Initialize();
        using var connection = OpenAccountingConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CompanyKey, CompanyName, FinancialYear, Gstin
            FROM TallyClientLinks
            WHERE CompanyKey <> ''
            GROUP BY CompanyKey, CompanyName, FinancialYear, Gstin
            ORDER BY CompanyName COLLATE NOCASE, FinancialYear COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<AuditTallyCompanyOption>();
        while (reader.Read())
        {
            result.Add(new AuditTallyCompanyOption
            {
                CompanyKey = reader.GetString(0),
                CompanyName = reader.GetString(1),
                FinancialYear = reader.GetString(2),
                Gstin = reader.GetString(3)
            });
        }
        return result;
    }

    public AuditTallySyncState? GetTallySyncState(long auditClientId, string financialYear, string companyKey)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT AuditClientId,FinancialYear,CompanyKey,LastChangeToken,LastVoucherCount,LastLedgerCount,LastGroupCount,LastAlterId,
                   LastFullSyncUtc,LastDeltaSyncUtc,LastPulseUtc,LastStatus
            FROM AuditTallySyncState
            WHERE AuditClientId=$clientId AND FinancialYear=$financialYear AND CompanyKey=$companyKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$clientId", auditClientId);
        command.Parameters.AddWithValue("$financialYear", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$companyKey", (companyKey ?? string.Empty).Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        static DateTime ParseUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTime.MinValue;
        return new AuditTallySyncState
        {
            AuditClientId = reader.GetInt64(0),
            FinancialYear = reader.GetString(1),
            CompanyKey = reader.GetString(2),
            LastChangeToken = reader.GetString(3),
            LastVoucherCount = reader.GetInt32(4),
            LastLedgerCount = reader.GetInt32(5),
            LastGroupCount = reader.GetInt32(6),
            LastAlterId = reader.GetInt64(7),
            LastFullSyncUtc = ParseUtc(reader.GetString(8)),
            LastDeltaSyncUtc = ParseUtc(reader.GetString(9)),
            LastPulseUtc = ParseUtc(reader.GetString(10)),
            LastStatus = reader.GetString(11)
        };
    }

    public void UpsertTallySyncState(AuditTallySyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AuditTallySyncState
            (AuditClientId,FinancialYear,CompanyKey,LastChangeToken,LastVoucherCount,LastLedgerCount,LastGroupCount,LastAlterId,
             LastFullSyncUtc,LastDeltaSyncUtc,LastPulseUtc,LastStatus)
            VALUES
            ($clientId,$financialYear,$companyKey,$token,$voucherCount,$ledgerCount,$groupCount,$alterId,$fullUtc,$deltaUtc,$pulseUtc,$status)
            ON CONFLICT(AuditClientId,FinancialYear,CompanyKey) DO UPDATE SET
                LastChangeToken=excluded.LastChangeToken,
                LastVoucherCount=excluded.LastVoucherCount,
                LastLedgerCount=excluded.LastLedgerCount,
                LastGroupCount=excluded.LastGroupCount,
                LastAlterId=excluded.LastAlterId,
                LastFullSyncUtc=excluded.LastFullSyncUtc,
                LastDeltaSyncUtc=excluded.LastDeltaSyncUtc,
                LastPulseUtc=excluded.LastPulseUtc,
                LastStatus=excluded.LastStatus;
            """;
        command.Parameters.AddWithValue("$clientId", state.AuditClientId);
        command.Parameters.AddWithValue("$financialYear", (state.FinancialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$companyKey", (state.CompanyKey ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$token", (state.LastChangeToken ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$voucherCount", state.LastVoucherCount);
        command.Parameters.AddWithValue("$ledgerCount", state.LastLedgerCount);
        command.Parameters.AddWithValue("$groupCount", state.LastGroupCount);
        command.Parameters.AddWithValue("$alterId", state.LastAlterId);
        command.Parameters.AddWithValue("$fullUtc", state.LastFullSyncUtc == DateTime.MinValue ? string.Empty : state.LastFullSyncUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$deltaUtc", state.LastDeltaSyncUtc == DateTime.MinValue ? string.Empty : state.LastDeltaSyncUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$pulseUtc", state.LastPulseUtc == DateTime.MinValue ? string.Empty : state.LastPulseUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", (state.LastStatus ?? string.Empty).Trim());
        command.ExecuteNonQuery();
    }

    public void DeleteTallySyncState(long auditClientId, string financialYear, string companyKey)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AuditTallySyncState WHERE AuditClientId=$clientId AND FinancialYear=$financialYear AND CompanyKey=$companyKey;";
        command.Parameters.AddWithValue("$clientId", auditClientId);
        command.Parameters.AddWithValue("$financialYear", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$companyKey", (companyKey ?? string.Empty).Trim());
        command.ExecuteNonQuery();
    }

    public void ReplaceTallyMasterAuthority(
        long auditClientId,
        string financialYear,
        string companyKey,
        IEnumerable<AuditTallyLedgerAuthority> ledgers,
        IReadOnlyDictionary<string, string> groupParents)
    {
        if (auditClientId <= 0) throw new ArgumentOutOfRangeException(nameof(auditClientId));
        var fy = (financialYear ?? string.Empty).Trim();
        var key = (companyKey ?? string.Empty).Trim();
        if (fy.Length == 0 || key.Length == 0) throw new InvalidOperationException("Audit Client/FY and Tally CompanyKey are required for Master authority.");

        Initialize();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM AuditTallyLedgers WHERE AuditClientId=$clientId AND FinancialYear=$fy AND CompanyKey=$companyKey; " +
                "DELETE FROM AuditTallyGroups WHERE AuditClientId=$clientId AND FinancialYear=$fy AND CompanyKey=$companyKey;";
            delete.Parameters.AddWithValue("$clientId", auditClientId);
            delete.Parameters.AddWithValue("$fy", fy);
            delete.Parameters.AddWithValue("$companyKey", key);
            delete.ExecuteNonQuery();
        }

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var ledger in ledgers
                     .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
                     .GroupBy(item => item.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(item => item.AlterId).First()))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO AuditTallyLedgers(AuditClientId,FinancialYear,CompanyKey,ExactName,DisplayName,Parent,Gstin,OpeningBalance,ClosingBalance,MasterId,Guid,AlterId,UpdatedUtc) " +
                "VALUES($clientId,$fy,$companyKey,$exact,$display,$parent,$gstin,$opening,$closing,$masterId,$guid,$alterId,$updated);";
            command.Parameters.AddWithValue("$clientId", auditClientId);
            command.Parameters.AddWithValue("$fy", fy);
            command.Parameters.AddWithValue("$companyKey", key);
            command.Parameters.AddWithValue("$exact", (ledger.ExactName ?? string.Empty));
            command.Parameters.AddWithValue("$display", (ledger.DisplayName ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$parent", (ledger.Parent ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$gstin", TallyCompanyInfo.NormalizeGstin(ledger.Gstin));
            command.Parameters.AddWithValue("$opening", ledger.OpeningBalance.HasValue ? ledger.OpeningBalance.Value.ToString("0.################", CultureInfo.InvariantCulture) : string.Empty);
            command.Parameters.AddWithValue("$closing", ledger.ClosingBalance.HasValue ? ledger.ClosingBalance.Value.ToString("0.################", CultureInfo.InvariantCulture) : string.Empty);
            command.Parameters.AddWithValue("$masterId", (ledger.MasterId ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$guid", (ledger.Guid ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$alterId", ledger.AlterId);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
        }

        foreach (var pair in groupParents.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO AuditTallyGroups(AuditClientId,FinancialYear,CompanyKey,Name,Parent,UpdatedUtc) " +
                "VALUES($clientId,$fy,$companyKey,$name,$parent,$updated);";
            command.Parameters.AddWithValue("$clientId", auditClientId);
            command.Parameters.AddWithValue("$fy", fy);
            command.Parameters.AddWithValue("$companyKey", key);
            command.Parameters.AddWithValue("$name", pair.Key.Trim());
            command.Parameters.AddWithValue("$parent", (pair.Value ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<AuditTallyLedgerAuthority> GetTallyLedgerAuthority(long auditClientId, string financialYear, string companyKey)
    {
        if (auditClientId <= 0) return Array.Empty<AuditTallyLedgerAuthority>();
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ExactName,DisplayName,Parent,Gstin,OpeningBalance,ClosingBalance,MasterId,Guid,AlterId,UpdatedUtc FROM AuditTallyLedgers " +
            "WHERE AuditClientId=$clientId AND FinancialYear=$fy AND CompanyKey=$companyKey ORDER BY DisplayName COLLATE NOCASE;";
        command.Parameters.AddWithValue("$clientId", auditClientId);
        command.Parameters.AddWithValue("$fy", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$companyKey", (companyKey ?? string.Empty).Trim());
        using var reader = command.ExecuteReader();
        var result = new List<AuditTallyLedgerAuthority>();
        while (reader.Read())
        {
            result.Add(new AuditTallyLedgerAuthority
            {
                AuditClientId = auditClientId,
                FinancialYear = (financialYear ?? string.Empty).Trim(),
                CompanyKey = (companyKey ?? string.Empty).Trim(),
                ExactName = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Parent = reader.GetString(2),
                Gstin = reader.GetString(3),
                OpeningBalance = ParseNullableDecimal(reader.GetString(4)),
                ClosingBalance = ParseNullableDecimal(reader.GetString(5)),
                MasterId = reader.GetString(6),
                Guid = reader.GetString(7),
                AlterId = reader.GetInt64(8),
                UpdatedUtc = ParseDate(reader.GetString(9)) ?? DateTime.MinValue
            });
        }
        return result;
    }

    public IReadOnlyDictionary<string, string> GetTallyGroupParents(long auditClientId, string financialYear, string companyKey)
    {
        if (auditClientId <= 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Name,Parent FROM AuditTallyGroups WHERE AuditClientId=$clientId AND FinancialYear=$fy AND CompanyKey=$companyKey;";
        command.Parameters.AddWithValue("$clientId", auditClientId);
        command.Parameters.AddWithValue("$fy", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$companyKey", (companyKey ?? string.Empty).Trim());
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public long SaveBankStatementEvidence(AuditBankStatementEvidence statement, IEnumerable<AuditBankStatementTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.AuditClientId <= 0) throw new InvalidOperationException("Audit Client is required for statement evidence.");
        var fy = (statement.FinancialYear ?? string.Empty).Trim();
        if (fy.Length == 0) throw new InvalidOperationException("Audit Financial Year is required for statement evidence.");
        var account = (statement.AccountName ?? string.Empty).Trim();
        if (account.Length == 0) throw new InvalidOperationException("Select the Bank/OD account before importing statement evidence.");
        var hash = (statement.FileHash ?? string.Empty).Trim();
        if (hash.Length == 0) throw new InvalidOperationException("Statement source hash is required.");

        Initialize();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        long statementId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT Id FROM AuditBankStatements WHERE AuditClientId=$clientId AND FinancialYear=$fy AND FileHash=$hash COLLATE NOCASE AND SourceSheet=$sheet COLLATE NOCASE AND AccountName=$account COLLATE NOCASE LIMIT 1;";
            command.Parameters.AddWithValue("$clientId", statement.AuditClientId);
            command.Parameters.AddWithValue("$fy", fy);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$sheet", (statement.SourceSheet ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$account", account);
            var existing = command.ExecuteScalar();
            if (existing is not null && existing != DBNull.Value)
            {
                statementId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                transaction.Rollback();
                return statementId;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO AuditBankStatements(AuditClientId,FinancialYear,AccountName,AccountCategory,AccountIdentity,SourcePath,SourceFile,SourceSheet,FileHash,PeriodFrom,PeriodTo,OpeningBalance,ClosingBalance,TotalDebit,TotalCredit,TransactionCount,ParseStatus,Notes,ImportedUtc) " +
                "VALUES($clientId,$fy,$account,$category,$identity,$path,$file,$sheet,$hash,$from,$to,$opening,$closing,$debit,$credit,$count,$status,$notes,$utc); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$clientId", statement.AuditClientId);
            command.Parameters.AddWithValue("$fy", fy);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$category", (statement.AccountCategory ?? "Bank").Trim());
            command.Parameters.AddWithValue("$identity", (statement.AccountIdentity ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$path", (statement.SourcePath ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$file", (statement.SourceFile ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$sheet", (statement.SourceSheet ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$from", statement.PeriodFrom?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            command.Parameters.AddWithValue("$to", statement.PeriodTo?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            command.Parameters.AddWithValue("$opening", statement.OpeningBalance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            command.Parameters.AddWithValue("$closing", statement.ClosingBalance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            command.Parameters.AddWithValue("$debit", statement.TotalDebit.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$credit", statement.TotalCredit.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$count", statement.TransactionCount);
            command.Parameters.AddWithValue("$status", (statement.ParseStatus ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$notes", (statement.Notes ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$utc", statement.ImportedUtc.ToString("O", CultureInfo.InvariantCulture));
            statementId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in transactions)
        {
            var origin = (row.OriginKey ?? string.Empty).Trim();
            if (origin.Length == 0 || !seen.Add(origin)) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT OR IGNORE INTO AuditBankStatementTransactions(StatementId,AuditClientId,FinancialYear,AccountName,AccountCategory,TransactionDate,Reference,Narration,Debit,Credit,RunningBalance,SourceRow,SourcePage,SourceLocation,OriginKey) " +
                "VALUES($statementId,$clientId,$fy,$account,$category,$date,$reference,$narration,$debit,$credit,$balance,$sourceRow,$sourcePage,$location,$origin);";
            command.Parameters.AddWithValue("$statementId", statementId);
            command.Parameters.AddWithValue("$clientId", statement.AuditClientId);
            command.Parameters.AddWithValue("$fy", fy);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$category", (statement.AccountCategory ?? "Bank").Trim());
            command.Parameters.AddWithValue("$date", row.TransactionDate.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$reference", (row.Reference ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$narration", (row.Narration ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$debit", row.Debit.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$credit", row.Credit.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$balance", row.RunningBalance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            command.Parameters.AddWithValue("$sourceRow", row.SourceRow);
            command.Parameters.AddWithValue("$sourcePage", row.SourcePage);
            command.Parameters.AddWithValue("$location", (row.SourceLocation ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$origin", origin);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return statementId;
    }

    public IReadOnlyList<AuditBankStatementEvidence> GetBankStatements(long auditClientId, string financialYear)
    {
        if (auditClientId <= 0) return Array.Empty<AuditBankStatementEvidence>();
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id,AccountName,AccountCategory,AccountIdentity,SourcePath,SourceFile,SourceSheet,FileHash,PeriodFrom,PeriodTo,OpeningBalance,ClosingBalance,TotalDebit,TotalCredit,TransactionCount,ParseStatus,Notes,ImportedUtc " +
            "FROM AuditBankStatements WHERE AuditClientId=$clientId AND FinancialYear=$fy ORDER BY AccountName COLLATE NOCASE, PeriodFrom, Id;";
        command.Parameters.AddWithValue("$clientId", auditClientId);
        command.Parameters.AddWithValue("$fy", (financialYear ?? string.Empty).Trim());
        using var reader = command.ExecuteReader();
        var result = new List<AuditBankStatementEvidence>();
        while (reader.Read())
        {
            result.Add(new AuditBankStatementEvidence
            {
                Id = reader.GetInt64(0), AuditClientId = auditClientId, FinancialYear = (financialYear ?? string.Empty).Trim(),
                AccountName = reader.GetString(1), AccountCategory = reader.GetString(2), AccountIdentity = reader.GetString(3),
                SourcePath = reader.GetString(4), SourceFile = reader.GetString(5), SourceSheet = reader.GetString(6), FileHash = reader.GetString(7),
                PeriodFrom = ParseDate(reader.GetString(8)), PeriodTo = ParseDate(reader.GetString(9)),
                OpeningBalance = ParseNullableDecimal(reader.GetString(10)), ClosingBalance = ParseNullableDecimal(reader.GetString(11)),
                TotalDebit = ParseDecimal(reader.GetString(12)), TotalCredit = ParseDecimal(reader.GetString(13)), TransactionCount = reader.GetInt32(14),
                ParseStatus = reader.GetString(15), Notes = reader.GetString(16), ImportedUtc = ParseDate(reader.GetString(17)) ?? DateTime.MinValue
            });
        }
        return result;
    }

    public IReadOnlyList<AuditBankStatementTransaction> GetBankStatementTransactions(long auditClientId, string financialYear)
    {
        if (auditClientId <= 0) return Array.Empty<AuditBankStatementTransaction>();
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id,StatementId,AccountName,AccountCategory,TransactionDate,Reference,Narration,Debit,Credit,RunningBalance,SourceRow,SourcePage,SourceLocation,OriginKey " +
            "FROM AuditBankStatementTransactions WHERE AuditClientId=$clientId AND FinancialYear=$fy ORDER BY TransactionDate, Id;";
        command.Parameters.AddWithValue("$clientId", auditClientId);
        command.Parameters.AddWithValue("$fy", (financialYear ?? string.Empty).Trim());
        using var reader = command.ExecuteReader();
        var result = new List<AuditBankStatementTransaction>();
        while (reader.Read())
        {
            var date = ParseDate(reader.GetString(4));
            if (!date.HasValue) continue;
            result.Add(new AuditBankStatementTransaction
            {
                Id = reader.GetInt64(0), StatementId = reader.GetInt64(1), AuditClientId = auditClientId, FinancialYear = (financialYear ?? string.Empty).Trim(),
                AccountName = reader.GetString(2), AccountCategory = reader.GetString(3), TransactionDate = date.Value,
                Reference = reader.GetString(5), Narration = reader.GetString(6), Debit = ParseDecimal(reader.GetString(7)), Credit = ParseDecimal(reader.GetString(8)),
                RunningBalance = ParseNullableDecimal(reader.GetString(9)), SourceRow = reader.GetInt32(10), SourcePage = reader.GetInt32(11), SourceLocation = reader.GetString(12), OriginKey = reader.GetString(13)
            });
        }
        return result;
    }

    public IReadOnlyList<AuditStoredRecord> GetRecords(long auditClientId, string financialYear, string module)
    {
        if (auditClientId <= 0) return Array.Empty<AuditStoredRecord>();
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, AuditClientId, FinancialYear, Module, RecordDate, Reference, VoucherType,
                   AuthorityId, PartyOrLedger, Details, Debit, Credit, Amount, Status, Source, OriginKind, OriginKey, PayloadJson
            FROM AuditRecords
            WHERE AuditClientId = $clientId AND FinancialYear = $fy AND Module = $module COLLATE NOCASE
            ORDER BY RecordDate, Id;
            """;
        command.Parameters.AddWithValue("$clientId", auditClientId);
        command.Parameters.AddWithValue("$fy", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$module", (module ?? string.Empty).Trim());
        using var reader = command.ExecuteReader();
        var result = new List<AuditStoredRecord>();
        while (reader.Read())
        {
            result.Add(new AuditStoredRecord
            {
                Id = reader.GetInt64(0),
                AuditClientId = reader.GetInt64(1),
                FinancialYear = reader.GetString(2),
                Module = reader.GetString(3),
                RecordDate = ParseDate(reader.GetString(4)),
                Reference = reader.GetString(5),
                VoucherType = reader.GetString(6),
                AuthorityId = reader.GetString(7),
                PartyOrLedger = reader.GetString(8),
                Details = reader.GetString(9),
                Debit = ParseDecimal(reader.GetString(10)),
                Credit = ParseDecimal(reader.GetString(11)),
                Amount = ParseDecimal(reader.GetString(12)),
                Status = reader.GetString(13),
                Source = reader.GetString(14),
                OriginKind = reader.GetString(15),
                OriginKey = reader.GetString(16),
                PayloadJson = reader.GetString(17)
            });
        }
        return result;
    }

    public AuditStoredRecord SaveRecord(AuditStoredRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.AuditClientId <= 0) throw new InvalidOperationException("Audit Client is required.");
        Initialize();
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (item.Id <= 0)
        {
            command.CommandText =
                """
                INSERT INTO AuditRecords
                (AuditClientId,FinancialYear,Module,RecordDate,Reference,VoucherType,AuthorityId,PartyOrLedger,Details,Debit,Credit,Amount,Status,Source,OriginKind,OriginKey,PayloadJson,CreatedUtc,UpdatedUtc)
                VALUES
                ($ClientId,$Fy,$Module,$Date,$Reference,$VoucherType,$AuthorityId,$Party,$Details,$Debit,$Credit,$Amount,$Status,$Source,$OriginKind,$OriginKey,$PayloadJson,$Now,$Now);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE AuditRecords SET
                    FinancialYear=$Fy,Module=$Module,RecordDate=$Date,Reference=$Reference,VoucherType=$VoucherType,
                    AuthorityId=$AuthorityId,PartyOrLedger=$Party,Details=$Details,Debit=$Debit,Credit=$Credit,Amount=$Amount,
                    Status=$Status,Source=$Source,OriginKind=$OriginKind,OriginKey=$OriginKey,PayloadJson=$PayloadJson,UpdatedUtc=$Now
                WHERE Id=$Id AND AuditClientId=$ClientId;
                SELECT $Id;
                """;
            command.Parameters.AddWithValue("$Id", item.Id);
        }
        command.Parameters.AddWithValue("$ClientId", item.AuditClientId);
        command.Parameters.AddWithValue("$Fy", (item.FinancialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Module", (item.Module ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Date", item.RecordDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("$Reference", (item.Reference ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$VoucherType", (item.VoucherType ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$AuthorityId", (item.AuthorityId ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Party", (item.PartyOrLedger ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Details", item.Details ?? string.Empty);
        command.Parameters.AddWithValue("$Debit", item.Debit.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$Credit", item.Credit.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$Amount", item.Amount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$Status", (item.Status ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Source", (item.Source ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$OriginKind", string.IsNullOrWhiteSpace(item.OriginKind) ? "Audit" : item.OriginKind.Trim());
        command.Parameters.AddWithValue("$OriginKey", (item.OriginKey ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$PayloadJson", item.PayloadJson ?? string.Empty);
        command.Parameters.AddWithValue("$Now", now);
        item.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return item;
    }

    public void DeleteRecord(long auditClientId, long id)
    {
        if (auditClientId <= 0 || id <= 0) return;
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AuditRecords WHERE AuditClientId=$ClientId AND Id=$Id;";
        command.Parameters.AddWithValue("$ClientId", auditClientId);
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    public AuditEntryAdjustment SaveEntryAdjustment(AuditEntryAdjustment item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.AuditClientId <= 0) throw new InvalidOperationException("Audit Client is required.");
        Initialize();
        var now = DateTime.UtcNow;
        if (item.CreatedUtc == default) item.CreatedUtc = now;
        item.UpdatedUtc = now;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (item.Id <= 0)
        {
            command.CommandText =
                """
                INSERT INTO AuditEntryAdjustments
                (AuditClientId,FinancialYear,Module,SourceRecordId,Source,SourceIdentity,TallyMasterId,TallyGuid,Reason,Action,Status,OriginalJson,ProposedJson,Failure,CreatedUtc,UpdatedUtc)
                VALUES
                ($ClientId,$Fy,$Module,$SourceRecordId,$Source,$SourceIdentity,$TallyMasterId,$TallyGuid,$Reason,$Action,$Status,$OriginalJson,$ProposedJson,$Failure,$CreatedUtc,$UpdatedUtc);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE AuditEntryAdjustments SET
                    Reason=$Reason,Action=$Action,Status=$Status,ProposedJson=$ProposedJson,Failure=$Failure,UpdatedUtc=$UpdatedUtc
                WHERE Id=$Id AND AuditClientId=$ClientId;
                SELECT $Id;
                """;
            command.Parameters.AddWithValue("$Id", item.Id);
        }
        command.Parameters.AddWithValue("$ClientId", item.AuditClientId);
        command.Parameters.AddWithValue("$Fy", item.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Module", item.Module.Trim());
        command.Parameters.AddWithValue("$SourceRecordId", item.SourceRecordId);
        command.Parameters.AddWithValue("$Source", item.Source.Trim());
        command.Parameters.AddWithValue("$SourceIdentity", item.SourceIdentity.Trim());
        command.Parameters.AddWithValue("$TallyMasterId", item.TallyMasterId.Trim());
        command.Parameters.AddWithValue("$TallyGuid", item.TallyGuid.Trim());
        command.Parameters.AddWithValue("$Reason", item.Reason.Trim());
        command.Parameters.AddWithValue("$Action", item.Action.Trim());
        command.Parameters.AddWithValue("$Status", item.Status.Trim());
        command.Parameters.AddWithValue("$OriginalJson", item.OriginalJson ?? string.Empty);
        command.Parameters.AddWithValue("$ProposedJson", item.ProposedJson ?? string.Empty);
        command.Parameters.AddWithValue("$Failure", item.Failure ?? string.Empty);
        command.Parameters.AddWithValue("$CreatedUtc", item.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedUtc", item.UpdatedUtc.ToString("O"));
        item.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return item;
    }

    public IReadOnlyList<AuditEntryAdjustment> GetEntryAdjustments(long auditClientId, string financialYear, string module)
    {
        if (auditClientId <= 0) return Array.Empty<AuditEntryAdjustment>();
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,AuditClientId,FinancialYear,Module,SourceRecordId,Source,SourceIdentity,TallyMasterId,TallyGuid,Reason,Action,Status,OriginalJson,ProposedJson,Failure,CreatedUtc,UpdatedUtc
            FROM AuditEntryAdjustments
            WHERE AuditClientId=$ClientId AND FinancialYear=$Fy AND Module=$Module COLLATE NOCASE
            ORDER BY UpdatedUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$ClientId", auditClientId);
        command.Parameters.AddWithValue("$Fy", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Module", (module ?? string.Empty).Trim());
        using var reader = command.ExecuteReader();
        var rows = new List<AuditEntryAdjustment>();
        while (reader.Read())
        {
            rows.Add(new AuditEntryAdjustment
            {
                Id=reader.GetInt64(0), AuditClientId=reader.GetInt64(1), FinancialYear=reader.GetString(2), Module=reader.GetString(3),
                SourceRecordId=reader.GetInt64(4), Source=reader.GetString(5), SourceIdentity=reader.GetString(6), TallyMasterId=reader.GetString(7), TallyGuid=reader.GetString(8),
                Reason=reader.GetString(9), Action=reader.GetString(10), Status=reader.GetString(11), OriginalJson=reader.GetString(12), ProposedJson=reader.GetString(13), Failure=reader.GetString(14),
                CreatedUtc=ParseDate(reader.GetString(15)) ?? DateTime.MinValue, UpdatedUtc=ParseDate(reader.GetString(16)) ?? DateTime.MinValue
            });
        }
        return rows;
    }

    public IReadOnlyDictionary<string, int> GetModuleCounts(long auditClientId, string financialYear)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Module, COUNT(*)
            FROM AuditRecords
            WHERE AuditClientId = $clientId AND FinancialYear = $fy
              AND OriginKind NOT IN ('AccountingWorkbenchOverride','AccountingWorkbenchDelete')
            GROUP BY Module;
            """;
        command.Parameters.AddWithValue("$clientId", auditClientId);
        command.Parameters.AddWithValue("$fy", (financialYear ?? string.Empty).Trim());
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_auditConnectionString);
        connection.Open();
        return connection;
    }

    private SqliteConnection OpenAccountingConnection()
    {
        var connection = new SqliteConnection(_accountingConnectionString);
        connection.Open();
        return connection;
    }

    private static AuditClientProfile ReadClient(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ClientCode = reader.GetString(1),
        ClientName = reader.GetString(2),
        LegalName = reader.GetString(3),
        TradeName = reader.GetString(4),
        FinancialYear = reader.GetString(5),
        Gstin = reader.GetString(6),
        Pan = reader.GetString(7),
        State = reader.GetString(8),
        Address = reader.GetString(9),
        Phone = reader.GetString(10),
        Email = reader.GetString(11),
        Notes = reader.GetString(12),
        LinkedAccountingClientId = reader.IsDBNull(13) ? null : reader.GetInt64(13),
        LinkedTallyCompanyKey = reader.GetString(14),
        LinkedTallyCompanyName = reader.GetString(15),
        IsArchived = reader.GetInt32(16) != 0,
        CreatedUtc = ParseDate(reader.GetString(17)) ?? DateTime.MinValue,
        UpdatedUtc = ParseDate(reader.GetString(18)) ?? DateTime.MinValue
    };

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = $"PRAGMA table_info({table});";
        using var reader = probe.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    private static decimal? ParseNullableDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
}
