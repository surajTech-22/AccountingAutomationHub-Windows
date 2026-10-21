using AccountingAutomationHub.Domain.Common;
using System.Globalization;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Banking;

public sealed class SqliteBankingRepository : IBankingRepository
{
    private readonly string _connectionString;

    public SqliteBankingRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public SqliteBankingRepository()
    {
        ApplicationDataPaths.EnsureFolders();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteBankingRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS BankAccounts
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                BankName        TEXT NOT NULL DEFAULT '',
                AccountNumber   TEXT NOT NULL DEFAULT '',
                AccountType     TEXT NOT NULL DEFAULT '',
                BranchName      TEXT NOT NULL DEFAULT '',
                IfscCode        TEXT NOT NULL DEFAULT '',
                OdLimit         REAL NULL,
                DrawingPower    REAL NULL,
                LedgerName      TEXT NOT NULL DEFAULT '',
                TallyLedgerMasterId TEXT NOT NULL DEFAULT '',
                TallyLedgerGuid TEXT NOT NULL DEFAULT '',
                TallyLedgerAlterId INTEGER NOT NULL DEFAULT 0,
                TallyLedgerVerifiedName TEXT NOT NULL DEFAULT '',
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BankAccounts_ClientFyAccount
            ON BankAccounts(ClientId, FinancialYear, BankName COLLATE NOCASE, AccountNumber COLLATE NOCASE, AccountType COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS BankStatements
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId                INTEGER NOT NULL,
                FinancialYear           TEXT NOT NULL,
                BankAccountId           INTEGER NOT NULL,
                SourceFile              TEXT NOT NULL DEFAULT '',
                SourcePath              TEXT NOT NULL DEFAULT '',
                SourceSheet             TEXT NOT NULL DEFAULT '',
                FileHash                TEXT NOT NULL DEFAULT '',
                PeriodFrom              TEXT NOT NULL DEFAULT '',
                PeriodTo                TEXT NOT NULL DEFAULT '',
                OpeningBalance          REAL NULL,
                OpeningBalanceSource    TEXT NOT NULL DEFAULT '',
                SourceOpeningBalance     REAL NULL,
                SourceClosingBalance     REAL NULL,
                ClosingBalanceSource    TEXT NOT NULL DEFAULT '',
                ClosingBalance          REAL NULL,
                TotalDebit              REAL NOT NULL DEFAULT 0,
                TotalCredit             REAL NOT NULL DEFAULT 0,
                TransactionCount        INTEGER NOT NULL DEFAULT 0,
                ReconciliationStatus    TEXT NOT NULL DEFAULT 'Pending',
                Difference              REAL NOT NULL DEFAULT 0,
                OpenSeconds             REAL NOT NULL DEFAULT 0,
                ProcessSeconds          REAL NOT NULL DEFAULT 0,
                ImportedUtc             TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE,
                FOREIGN KEY (BankAccountId) REFERENCES BankAccounts(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BankStatements_ClientFyHash
            ON BankStatements(ClientId, FinancialYear, FileHash);
            CREATE INDEX IF NOT EXISTS IX_BankStatements_ClientFyPeriod
            ON BankStatements(ClientId, FinancialYear, BankAccountId, PeriodFrom, PeriodTo);

            CREATE TABLE IF NOT EXISTS BankTransactions
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId                INTEGER NOT NULL,
                FinancialYear           TEXT NOT NULL,
                StatementId             INTEGER NOT NULL,
                BankAccountId           INTEGER NOT NULL,
                TransactionDate         TEXT NOT NULL,
                ValueDate               TEXT NOT NULL DEFAULT '',
                Narration               TEXT NOT NULL DEFAULT '',
                NormalizedNarration     TEXT NOT NULL DEFAULT '',
                ReferenceNumber         TEXT NOT NULL DEFAULT '',
                Debit                   REAL NOT NULL DEFAULT 0,
                Credit                  REAL NOT NULL DEFAULT 0,
                RunningBalance          REAL NULL,
                RunningBalanceSource    TEXT NOT NULL DEFAULT '',
                Mode                    TEXT NOT NULL DEFAULT '',
                LedgerName              TEXT NOT NULL DEFAULT '',
                MappingStatus           TEXT NOT NULL DEFAULT 'Mapping Required',
                TransactionId           TEXT NOT NULL DEFAULT '',
                Fingerprint             TEXT NOT NULL DEFAULT '',
                RecordStatus            TEXT NOT NULL DEFAULT 'Active',
                MatchId                 TEXT NOT NULL DEFAULT '',
                MatchedTransactionId    INTEGER NULL,
                SourceFile              TEXT NOT NULL DEFAULT '',
                SourceSheet             TEXT NOT NULL DEFAULT '',
                SourceRow               INTEGER NOT NULL DEFAULT 0,
                ImportedUtc             TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE,
                FOREIGN KEY (StatementId) REFERENCES BankStatements(Id) ON DELETE CASCADE,
                FOREIGN KEY (BankAccountId) REFERENCES BankAccounts(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BankTransactions_ClientFyFingerprint
            ON BankTransactions(ClientId, FinancialYear, Fingerprint);
            CREATE INDEX IF NOT EXISTS IX_BankTransactions_ClientFyDate
            ON BankTransactions(ClientId, FinancialYear, TransactionDate, Id);
            CREATE INDEX IF NOT EXISTS IX_BankTransactions_Statement
            ON BankTransactions(StatementId, SourceRow);

            CREATE TABLE IF NOT EXISTS BankStatementImportAttempts
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId                INTEGER NOT NULL,
                FinancialYear           TEXT NOT NULL,
                BankAccountId           INTEGER NOT NULL DEFAULT 0,
                StatementId             INTEGER NULL,
                DuplicateOfStatementId  INTEGER NULL,
                SourceFile              TEXT NOT NULL DEFAULT '',
                SourcePath              TEXT NOT NULL DEFAULT '',
                SourceSheet             TEXT NOT NULL DEFAULT '',
                FileHash                TEXT NOT NULL DEFAULT '',
                PeriodFrom              TEXT NULL,
                PeriodTo                TEXT NULL,
                OpeningBalance          REAL NULL,
                ClosingBalance          REAL NULL,
                TotalDebit              REAL NOT NULL DEFAULT 0,
                TotalCredit             REAL NOT NULL DEFAULT 0,
                TransactionCount        INTEGER NOT NULL DEFAULT 0,
                Status                  TEXT NOT NULL DEFAULT '',
                Notes                   TEXT NOT NULL DEFAULT '',
                ImportedUtc             TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_BankStatementImportAttempts_ClientFy
            ON BankStatementImportAttempts(ClientId, FinancialYear, ImportedUtc, Id);

            CREATE TABLE IF NOT EXISTS BankingMaintenanceState
            (
                RepairKey       TEXT PRIMARY KEY,
                CompletedUtc    TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS BankTallyExportLinks
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                TransactionId       INTEGER NOT NULL,
                SourceKey           TEXT NOT NULL DEFAULT '',
                CompanyKey          TEXT NOT NULL DEFAULT '',
                TallyMasterId       TEXT NOT NULL DEFAULT '',
                LastVoucherType     TEXT NOT NULL DEFAULT '',
                LastBankLedger      TEXT NOT NULL DEFAULT '',
                LastCounterpartLedger TEXT NOT NULL DEFAULT '',
                VerifiedUtc         TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BankTallyExportLinks_TransactionCompany
            ON BankTallyExportLinks(ClientId, FinancialYear, TransactionId, CompanyKey COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS BankPendingTallyDuplicateDeletes
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId                INTEGER NOT NULL,
                FinancialYear           TEXT NOT NULL,
                RemovedTransactionId    INTEGER NOT NULL,
                RetainedTransactionId   INTEGER NOT NULL,
                PhysicalIdentityKey     TEXT NOT NULL DEFAULT '',
                TransactionDate         TEXT NOT NULL,
                BankAccountId           INTEGER NOT NULL DEFAULT 0,
                Mode                    TEXT NOT NULL DEFAULT '',
                ReferenceNumber         TEXT NOT NULL DEFAULT '',
                Narration               TEXT NOT NULL DEFAULT '',
                Debit                   REAL NOT NULL DEFAULT 0,
                Credit                  REAL NOT NULL DEFAULT 0,
                RunningBalance          REAL NULL,
                RemovedSourceKey        TEXT NOT NULL DEFAULT '',
                RetainedSourceKey       TEXT NOT NULL DEFAULT '',
                KnownTallyMasterId      TEXT NOT NULL DEFAULT '',
                RetainedTallyMasterId   TEXT NOT NULL DEFAULT '',
                Status                  TEXT NOT NULL DEFAULT 'Pending',
                Notes                   TEXT NOT NULL DEFAULT '',
                CreatedUtc              TEXT NOT NULL,
                UpdatedUtc              TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BankPendingTallyDuplicateDeletes_Removed
            ON BankPendingTallyDuplicateDeletes(ClientId, FinancialYear, RemovedTransactionId);
            CREATE INDEX IF NOT EXISTS IX_BankPendingTallyDuplicateDeletes_Pending
            ON BankPendingTallyDuplicateDeletes(ClientId, FinancialYear, Status, TransactionDate, Id);

            CREATE TABLE IF NOT EXISTS BankTransferMatches
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId                INTEGER NOT NULL,
                FinancialYear           TEXT NOT NULL,
                SourceTransactionId     INTEGER NOT NULL,
                TargetTransactionId     INTEGER NOT NULL,
                MatchId                 TEXT NOT NULL,
                MatchDate               TEXT NOT NULL,
                Amount                  REAL NOT NULL DEFAULT 0,
                ChargeAmount            REAL NOT NULL DEFAULT 0,
                ChargeRule              TEXT NOT NULL DEFAULT '',
                ReferenceNumber         TEXT NOT NULL DEFAULT '',
                Status                  TEXT NOT NULL DEFAULT 'Matched',
                Reason                  TEXT NOT NULL DEFAULT '',
                MatchOrigin             TEXT NOT NULL DEFAULT 'Bank Statement',
                TallyConversionId       TEXT NOT NULL DEFAULT '',
                TallyMasterId           TEXT NOT NULL DEFAULT '',
                TallyAlterId            TEXT NOT NULL DEFAULT '',
                TallyVoucherType        TEXT NOT NULL DEFAULT '',
                TallyVoucherNumber      TEXT NOT NULL DEFAULT '',
                TallyBankLedger         TEXT NOT NULL DEFAULT '',
                TallyBankLedgerMasterId TEXT NOT NULL DEFAULT '',
                ImportedBankLedgerMasterId TEXT NOT NULL DEFAULT '',
                SourceBankLedgerMasterId TEXT NOT NULL DEFAULT '',
                TargetBankLedgerMasterId TEXT NOT NULL DEFAULT '',
                TallyNarration          TEXT NOT NULL DEFAULT '',
                TallyReference          TEXT NOT NULL DEFAULT '',
                TallySignedAmount       REAL NOT NULL DEFAULT 0,
                TallyIsDebit            INTEGER NOT NULL DEFAULT 0,
                CreatedUtc              TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE,
                FOREIGN KEY (SourceTransactionId) REFERENCES BankTransactions(Id) ON DELETE CASCADE,
                FOREIGN KEY (TargetTransactionId) REFERENCES BankTransactions(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BankTransferMatches_ClientFyPair
            ON BankTransferMatches(ClientId, FinancialYear, SourceTransactionId, TargetTransactionId);

            CREATE TABLE IF NOT EXISTS BankLedgerMappings
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                BankFilter          TEXT NOT NULL DEFAULT '',
                VoucherType         TEXT NOT NULL DEFAULT '',
                SummaryName         TEXT NOT NULL DEFAULT '',
                NarrationPattern    TEXT NOT NULL DEFAULT '',
                SourceLedgerName    TEXT NOT NULL DEFAULT '',
                LedgerName          TEXT NOT NULL DEFAULT '',
                MasterGroup         TEXT NOT NULL DEFAULT '',
                MatchCount          INTEGER NOT NULL DEFAULT 0,
                PaymentMatchCount   INTEGER NOT NULL DEFAULT 0,
                ReceiptMatchCount   INTEGER NOT NULL DEFAULT 0,
                Status              TEXT NOT NULL DEFAULT 'Pending',
                UpdatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS BankLedgerMappingMembers
            (
                MappingId       INTEGER NOT NULL,
                TransactionId   INTEGER NOT NULL,
                PRIMARY KEY(MappingId, TransactionId),
                FOREIGN KEY (MappingId) REFERENCES BankLedgerMappings(Id) ON DELETE CASCADE,
                FOREIGN KEY (TransactionId) REFERENCES BankTransactions(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_BankLedgerMappingMembers_Transaction
            ON BankLedgerMappingMembers(TransactionId, MappingId);

            CREATE TABLE IF NOT EXISTS CashEntrySettings
            (
                ClientId                INTEGER NOT NULL,
                FinancialYear           TEXT NOT NULL,
                LayoutMode              TEXT NOT NULL DEFAULT 'Debit-Credit',
                DefaultCommonLedger     TEXT NOT NULL DEFAULT 'Cash',
                UpdatedUtc              TEXT NOT NULL,
                PRIMARY KEY(ClientId, FinancialYear),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS CashEntries
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                EntryDate           TEXT NOT NULL,
                EntryLedger         TEXT NOT NULL DEFAULT '',
                Debit               REAL NOT NULL DEFAULT 0,
                Credit              REAL NOT NULL DEFAULT 0,
                Narration           TEXT NOT NULL DEFAULT '',
                CommonLedger        TEXT NOT NULL DEFAULT '',
                MappedMasterLedger  TEXT NOT NULL DEFAULT '',
                VoucherType         TEXT NOT NULL DEFAULT '',
                Status              TEXT NOT NULL DEFAULT 'Review Required',
                EntryId             TEXT NOT NULL DEFAULT '',
                IsUserEdited        INTEGER NOT NULL DEFAULT 1,
                UpdatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_CashEntries_ClientFyEntryId
            ON CashEntries(ClientId, FinancialYear, EntryId);
            CREATE INDEX IF NOT EXISTS IX_CashEntries_ClientFyDate
            ON CashEntries(ClientId, FinancialYear, EntryDate, Id);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "BankAccounts", "BranchName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankAccounts", "IfscCode", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankAccounts", "OdLimit", "REAL NULL");
        EnsureColumn(connection, "BankAccounts", "DrawingPower", "REAL NULL");
        EnsureColumn(connection, "BankAccounts", "TallyLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankAccounts", "TallyLedgerGuid", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankAccounts", "TallyLedgerAlterId", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "BankAccounts", "TallyLedgerVerifiedName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankStatements", "OpenSeconds", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "BankStatements", "ProcessSeconds", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "BankTransferMatches", "ChargeAmount", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "BankTransferMatches", "ChargeRule", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "MatchOrigin", "TEXT NOT NULL DEFAULT 'Bank Statement'");
        EnsureColumn(connection, "BankTransferMatches", "TallyConversionId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallyMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallyAlterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallyVoucherType", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallyVoucherNumber", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallyBankLedger", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallyBankLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "ImportedBankLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "SourceBankLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TargetBankLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallyNarration", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallyReference", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransferMatches", "TallySignedAmount", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "BankTransferMatches", "TallyIsDebit", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "BankStatements", "OpeningBalanceSource", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTransactions", "RunningBalanceSource", "TEXT NOT NULL DEFAULT ''");
        // Never guess transaction running-balance provenance for legacy rows. BR4 could
        // calculate missing running balances before persistence, so a non-null old value
        // is not proof that the number appeared in the source statement. New/reprocessed
        // imports set SOURCE explicitly; manual edits set MANUAL; cache fills set CALCULATED.
        EnsureColumn(connection, "BankStatements", "SourceOpeningBalance", "REAL NULL");
        EnsureColumn(connection, "BankStatements", "SourceClosingBalance", "REAL NULL");
        EnsureColumn(connection, "BankStatements", "ClosingBalanceSource", "TEXT NOT NULL DEFAULT ''");
        // Do not backfill source-evidence columns from resolved balances. Historical rows
        // without explicit provenance remain unresolved until running-balance evidence,
        // manual verification or source reprocessing establishes authority.
        EnsureColumn(connection, "BankLedgerMappings", "SourceLedgerName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankLedgerMappings", "PaymentMatchCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "BankLedgerMappings", "ReceiptMatchCount", "INTEGER NOT NULL DEFAULT 0");
        // Fix72: remember the last VERIFIED Bank voucher treatment separately from
        // physical duplicate identity. A later user Ledger change can then prove that
        // live Tally still matches the previously saved treatment before ALTERing that
        // exact MASTERID to the new current treatment.
        EnsureColumn(connection, "BankTallyExportLinks", "LastVoucherType", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTallyExportLinks", "LastBankLedger", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankTallyExportLinks", "LastCounterpartLedger", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "CashEntries", "WorkflowStatus", "TEXT NOT NULL DEFAULT 'Draft'");
        EnsureColumn(connection, "CashEntries", "ReferenceNumber", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "CashEntries", "AttachmentPath", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "CashEntries", "ReversalOfId", "INTEGER NULL");
        EnsureColumn(connection, "CashEntries", "ClonedFromId", "INTEGER NULL");
        EnsureColumn(connection, "CashEntries", "RecurringProfileId", "INTEGER NULL");
        EnsureColumn(connection, "CashEntries", "SourceRunId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "CashEntries", "SourceJobId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "CashEntries", "CreatedBy", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "CashEntries", "ApprovedBy", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "CashEntries", "ApprovedUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "CashEntrySettings", "OpeningBalance", "REAL NOT NULL DEFAULT 0");
        using (var mappingIndex = connection.CreateCommand())
        {
            // v3.8.0 Excel-parity migration: Voucher Type no longer splits a party,
            // while the current Bank Statement ledger remains part of the grouping key.
            mappingIndex.CommandText =
                """
                DROP INDEX IF EXISTS UX_BankLedgerMappings_ClientFyPattern;
                CREATE UNIQUE INDEX IF NOT EXISTS UX_BankLedgerMappings_ClientFyPattern
                ON BankLedgerMappings(ClientId, FinancialYear, BankFilter COLLATE NOCASE, VoucherType COLLATE NOCASE, NarrationPattern COLLATE NOCASE, SourceLedgerName COLLATE NOCASE);
                """;
            mappingIndex.ExecuteNonQuery();
        }

        // Fix22: repair historical aliases before any Banking screen/reconciliation reads.
        // A full unmasked account number is the physical-account authority, so records such
        // as "Punjab National Bank - 18431132000724" and
        // "Punjab National Bank SB - 18431132000724" become one account/balance chain.
        RepairDuplicateBankAccountsByStrongNumber(connection);

        // Fix24: first physical Bank import wins. Historical later duplicates are removed
        // even when their Ledger/Mapping treatment differs; the earlier transaction is never
        // overwritten by the later mapping. Every removed row is queued durably for Tally
        // MASTERID cleanup on the next Bank export.
        const string fix24FirstImportWinsKey = "FIX24-FIRST-IMPORT-WINS-BANK-DUPLICATES-V1";
        if (!HasBankingMaintenanceMarker(connection, fix24FirstImportWinsKey))
        {
            RepairFirstImportWinsTransactionDuplicates(connection);
            SetBankingMaintenanceMarker(connection, fix24FirstImportWinsKey);
        }

        // Fix23 balance-boundary stitching remains authoritative for non-identical overlaps.
        // Fix24 only removes strongly proven physical duplicates; unresolved/partially parsed
        // overlap still remains non-destructive and is handled by BankBalanceAuthority.
        const string fix23OverlapAuthorityKey = "FIX23-NONDESTRUCTIVE-BALANCE-STITCH-V1";
        if (!HasBankingMaintenanceMarker(connection, fix23OverlapAuthorityKey))
            SetBankingMaintenanceMarker(connection, fix23OverlapAuthorityKey);
    }

    public bool HasRecords(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1 FROM BankTransactions WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                UNION ALL
                SELECT 1 FROM CashEntries WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ) THEN 1 ELSE 0 END;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public bool HasCashEntries(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM CashEntries WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear LIMIT 1);";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public void ClearClientYear(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "BankPendingTallyDuplicateDeletes", "BankTallyExportLinks", "BankTransferMatches", "BankLedgerMappings", "BankStatementImportAttempts", "BankTransactions", "BankStatements", "BankAccounts" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<BankAccountRecord> GetAccounts(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientId, FinancialYear, BankName, AccountNumber, AccountType,
                   BranchName, IfscCode, OdLimit, DrawingPower, LedgerName,
                   TallyLedgerMasterId, TallyLedgerGuid, TallyLedgerAlterId, TallyLedgerVerifiedName,
                   CreatedUtc, UpdatedUtc
            FROM BankAccounts WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY BankName COLLATE NOCASE, AccountNumber COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<BankAccountRecord>();
        while (reader.Read()) result.Add(ReadAccount(reader));
        return result;
    }

    public BankAccountRecord UpsertAccount(BankAccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var connection = OpenConnection();
        var now = DateTime.UtcNow.ToString("O");
        var existingId = FindAccountIdByIdentity(connection, null, account);

        if (existingId.HasValue)
        {
            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE BankAccounts
                SET BankName=CASE WHEN BankName='' AND $BankName<>'' THEN $BankName ELSE BankName END,
                    AccountNumber=CASE WHEN AccountNumber='' AND $AccountNumber<>'' THEN $AccountNumber ELSE AccountNumber END,
                    AccountType=CASE WHEN AccountType='' AND $AccountType<>'' THEN $AccountType ELSE AccountType END,
                    BranchName=CASE WHEN $BranchName<>'' THEN $BranchName ELSE BranchName END,
                    IfscCode=CASE WHEN $IfscCode<>'' THEN $IfscCode ELSE IfscCode END,
                    OdLimit=COALESCE($OdLimit,OdLimit),
                    DrawingPower=COALESCE($DrawingPower,DrawingPower),
                    LedgerName=CASE WHEN $TallyLedgerMasterId<>'' AND $LedgerName<>'' THEN $LedgerName WHEN LedgerName='' AND $LedgerName<>'' THEN $LedgerName ELSE LedgerName END,
                    TallyLedgerMasterId=CASE WHEN $TallyLedgerMasterId<>'' THEN $TallyLedgerMasterId ELSE TallyLedgerMasterId END,
                    TallyLedgerGuid=CASE WHEN $TallyLedgerGuid<>'' THEN $TallyLedgerGuid ELSE TallyLedgerGuid END,
                    TallyLedgerAlterId=CASE WHEN $TallyLedgerAlterId>0 THEN $TallyLedgerAlterId ELSE TallyLedgerAlterId END,
                    TallyLedgerVerifiedName=CASE WHEN $TallyLedgerVerifiedName<>'' THEN $TallyLedgerVerifiedName ELSE TallyLedgerVerifiedName END,
                    UpdatedUtc=$Now
                WHERE Id=$Id;
                """;
            AddAccountMergeParameters(update, account, now);
            update.Parameters.AddWithValue("$Id", existingId.Value);
            update.ExecuteNonQuery();
            return ReadAccountById(connection, existingId.Value);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO BankAccounts(ClientId,FinancialYear,BankName,AccountNumber,AccountType,BranchName,IfscCode,OdLimit,DrawingPower,LedgerName,TallyLedgerMasterId,TallyLedgerGuid,TallyLedgerAlterId,TallyLedgerVerifiedName,CreatedUtc,UpdatedUtc)
                VALUES($ClientId,$FinancialYear,$BankName,$AccountNumber,$AccountType,$BranchName,$IfscCode,$OdLimit,$DrawingPower,$LedgerName,$TallyLedgerMasterId,$TallyLedgerGuid,$TallyLedgerAlterId,$TallyLedgerVerifiedName,$Now,$Now)
                ON CONFLICT(ClientId,FinancialYear,BankName,AccountNumber,AccountType)
                DO UPDATE SET BranchName=CASE WHEN excluded.BranchName<>'' THEN excluded.BranchName ELSE BankAccounts.BranchName END,
                              IfscCode=CASE WHEN excluded.IfscCode<>'' THEN excluded.IfscCode ELSE BankAccounts.IfscCode END,
                              OdLimit=COALESCE(excluded.OdLimit,BankAccounts.OdLimit),
                              DrawingPower=COALESCE(excluded.DrawingPower,BankAccounts.DrawingPower),
                              LedgerName=CASE WHEN excluded.TallyLedgerMasterId<>'' AND excluded.LedgerName<>'' THEN excluded.LedgerName WHEN BankAccounts.LedgerName='' AND excluded.LedgerName<>'' THEN excluded.LedgerName ELSE BankAccounts.LedgerName END,
                              TallyLedgerMasterId=CASE WHEN excluded.TallyLedgerMasterId<>'' THEN excluded.TallyLedgerMasterId ELSE BankAccounts.TallyLedgerMasterId END,
                              TallyLedgerGuid=CASE WHEN excluded.TallyLedgerGuid<>'' THEN excluded.TallyLedgerGuid ELSE BankAccounts.TallyLedgerGuid END,
                              TallyLedgerAlterId=CASE WHEN excluded.TallyLedgerAlterId>0 THEN excluded.TallyLedgerAlterId ELSE BankAccounts.TallyLedgerAlterId END,
                              TallyLedgerVerifiedName=CASE WHEN excluded.TallyLedgerVerifiedName<>'' THEN excluded.TallyLedgerVerifiedName ELSE BankAccounts.TallyLedgerVerifiedName END,
                              UpdatedUtc=excluded.UpdatedUtc;
                """;
            AddAccountMergeParameters(command, account, now);
            command.ExecuteNonQuery();
        }

        var insertedId = FindAccountIdByIdentity(connection, null, account);
        if (!insertedId.HasValue) throw new InvalidOperationException("The bank account could not be saved.");
        return ReadAccountById(connection, insertedId.Value);
    }

    public void SaveAccount(BankAccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Id <= 0) throw new InvalidOperationException("Select a saved bank account before updating it.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankAccounts
            SET BankName=$BankName, AccountNumber=$AccountNumber, AccountType=$AccountType,
                BranchName=$BranchName, IfscCode=$IfscCode, OdLimit=$OdLimit, DrawingPower=$DrawingPower,
                LedgerName=$LedgerName,
                TallyLedgerMasterId=$TallyLedgerMasterId, TallyLedgerGuid=$TallyLedgerGuid,
                TallyLedgerAlterId=$TallyLedgerAlterId, TallyLedgerVerifiedName=$TallyLedgerVerifiedName,
                UpdatedUtc=$UpdatedUtc
            WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear;
            """;
        command.Parameters.AddWithValue("$Id", account.Id);
        command.Parameters.AddWithValue("$ClientId", account.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", account.FinancialYear.Trim());
        command.Parameters.AddWithValue("$BankName", account.BankName.Trim());
        command.Parameters.AddWithValue("$AccountNumber", account.AccountNumber.Trim());
        command.Parameters.AddWithValue("$AccountType", account.AccountType.Trim());
        command.Parameters.AddWithValue("$BranchName", account.BranchName.Trim());
        command.Parameters.AddWithValue("$IfscCode", account.IfscCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$OdLimit", account.OdLimit.HasValue ? (object)(double)account.OdLimit.Value : DBNull.Value);
        command.Parameters.AddWithValue("$DrawingPower", account.DrawingPower.HasValue ? (object)(double)account.DrawingPower.Value : DBNull.Value);
        command.Parameters.AddWithValue("$LedgerName", account.LedgerName.Trim());
        command.Parameters.AddWithValue("$TallyLedgerMasterId", account.TallyLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$TallyLedgerGuid", account.TallyLedgerGuid.Trim());
        command.Parameters.AddWithValue("$TallyLedgerAlterId", account.TallyLedgerAlterId);
        command.Parameters.AddWithValue("$TallyLedgerVerifiedName", account.TallyLedgerVerifiedName.Trim());
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The selected bank account could not be updated.");
    }

    public bool StatementHashExists(long clientId, string financialYear, string fileHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM BankStatements WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND FileHash=$FileHash LIMIT 1);";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$FileHash", fileHash.Trim());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public bool StatementPeriodExists(long clientId, string financialYear, long bankAccountId, DateTime? periodFrom, DateTime? periodTo)
    {
        if (!periodFrom.HasValue || !periodTo.HasValue) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1 FROM BankStatements
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND BankAccountId=$BankAccountId
                  AND PeriodFrom=$PeriodFrom AND PeriodTo=$PeriodTo
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$BankAccountId", bankAccountId);
        command.Parameters.AddWithValue("$PeriodFrom", periodFrom.Value.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$PeriodTo", periodTo.Value.ToString("yyyy-MM-dd"));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public long SaveStatement(BankStatementRecord statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BankStatements(ClientId,FinancialYear,BankAccountId,SourceFile,SourcePath,SourceSheet,FileHash,
                PeriodFrom,PeriodTo,OpeningBalance,OpeningBalanceSource,SourceOpeningBalance,SourceClosingBalance,ClosingBalanceSource,ClosingBalance,TotalDebit,TotalCredit,TransactionCount,ReconciliationStatus,Difference,OpenSeconds,ProcessSeconds,ImportedUtc)
            VALUES($ClientId,$FinancialYear,$BankAccountId,$SourceFile,$SourcePath,$SourceSheet,$FileHash,
                $PeriodFrom,$PeriodTo,$OpeningBalance,$OpeningBalanceSource,$SourceOpeningBalance,$SourceClosingBalance,$ClosingBalanceSource,$ClosingBalance,$TotalDebit,$TotalCredit,$TransactionCount,$Status,$Difference,$OpenSeconds,$ProcessSeconds,$ImportedUtc);
            SELECT last_insert_rowid();
            """;
        AddStatementParameters(command, statement);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<BankStatementRecord> GetStatements(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,BankAccountId,SourceFile,SourcePath,SourceSheet,FileHash,PeriodFrom,PeriodTo,
                   OpeningBalance,OpeningBalanceSource,SourceOpeningBalance,SourceClosingBalance,ClosingBalanceSource,ClosingBalance,TotalDebit,TotalCredit,TransactionCount,ReconciliationStatus,Difference,OpenSeconds,ProcessSeconds,ImportedUtc
            FROM BankStatements WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY COALESCE(PeriodFrom,''), ImportedUtc, Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<BankStatementRecord>();
        while (reader.Read()) result.Add(ReadStatement(reader));
        return result;
    }

    public void SaveStatementImportAttempt(BankStatementImportAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BankStatementImportAttempts(
                ClientId,FinancialYear,BankAccountId,StatementId,DuplicateOfStatementId,
                SourceFile,SourcePath,SourceSheet,FileHash,PeriodFrom,PeriodTo,OpeningBalance,ClosingBalance,
                TotalDebit,TotalCredit,TransactionCount,Status,Notes,ImportedUtc)
            VALUES(
                $ClientId,$FinancialYear,$BankAccountId,$StatementId,$DuplicateOfStatementId,
                $SourceFile,$SourcePath,$SourceSheet,$FileHash,$PeriodFrom,$PeriodTo,$OpeningBalance,$ClosingBalance,
                $TotalDebit,$TotalCredit,$TransactionCount,$Status,$Notes,$ImportedUtc);
            """;
        command.Parameters.AddWithValue("$ClientId", attempt.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", attempt.FinancialYear.Trim());
        command.Parameters.AddWithValue("$BankAccountId", attempt.BankAccountId);
        command.Parameters.AddWithValue("$StatementId", attempt.StatementId.HasValue ? attempt.StatementId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$DuplicateOfStatementId", attempt.DuplicateOfStatementId.HasValue ? attempt.DuplicateOfStatementId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$SourceFile", attempt.SourceFile ?? string.Empty);
        command.Parameters.AddWithValue("$SourcePath", attempt.SourcePath ?? string.Empty);
        command.Parameters.AddWithValue("$SourceSheet", attempt.SourceSheet ?? string.Empty);
        command.Parameters.AddWithValue("$FileHash", attempt.FileHash ?? string.Empty);
        command.Parameters.AddWithValue("$PeriodFrom", attempt.PeriodFrom.HasValue ? attempt.PeriodFrom.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        command.Parameters.AddWithValue("$PeriodTo", attempt.PeriodTo.HasValue ? attempt.PeriodTo.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        command.Parameters.AddWithValue("$OpeningBalance", attempt.OpeningBalance.HasValue ? (object)(double)attempt.OpeningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$ClosingBalance", attempt.ClosingBalance.HasValue ? (object)(double)attempt.ClosingBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$TotalDebit", (double)attempt.TotalDebit);
        command.Parameters.AddWithValue("$TotalCredit", (double)attempt.TotalCredit);
        command.Parameters.AddWithValue("$TransactionCount", attempt.TransactionCount);
        command.Parameters.AddWithValue("$Status", attempt.Status ?? string.Empty);
        command.Parameters.AddWithValue("$Notes", attempt.Notes ?? string.Empty);
        command.Parameters.AddWithValue("$ImportedUtc", (attempt.ImportedUtc == default ? DateTime.UtcNow : attempt.ImportedUtc).ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BankStatementImportAttemptRecord> GetStatementImportAttempts(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,BankAccountId,StatementId,DuplicateOfStatementId,SourceFile,SourcePath,SourceSheet,FileHash,
                   PeriodFrom,PeriodTo,OpeningBalance,ClosingBalance,TotalDebit,TotalCredit,TransactionCount,Status,Notes,ImportedUtc
            FROM BankStatementImportAttempts
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY ImportedUtc, Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var rows = new List<BankStatementImportAttemptRecord>();
        while (reader.Read())
        {
            rows.Add(new BankStatementImportAttemptRecord
            {
                Id = reader.GetInt64(0), ClientId = reader.GetInt64(1), FinancialYear = reader.GetString(2), BankAccountId = reader.GetInt64(3),
                StatementId = reader.IsDBNull(4) ? null : reader.GetInt64(4), DuplicateOfStatementId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                SourceFile = reader.GetString(6), SourcePath = reader.GetString(7), SourceSheet = reader.GetString(8), FileHash = reader.GetString(9),
                PeriodFrom = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                PeriodTo = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                OpeningBalance = reader.IsDBNull(12) ? null : reader.GetDecimal(12), ClosingBalance = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                TotalDebit = reader.GetDecimal(14), TotalCredit = reader.GetDecimal(15), TransactionCount = reader.GetInt32(16),
                Status = reader.GetString(17), Notes = reader.GetString(18), ImportedUtc = DateTime.Parse(reader.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }
        return rows;
    }

    public long RecoverStatementRegistry(BankStatementRecord statement, string sourceFile, string sourceSheet)
    {
        ArgumentNullException.ThrowIfNull(statement);
        sourceFile = (sourceFile ?? string.Empty).Trim();
        sourceSheet = (sourceSheet ?? string.Empty).Trim();
        if (statement.ClientId <= 0 || statement.BankAccountId <= 0 || sourceFile.Length == 0)
            throw new InvalidOperationException("A source file and Bank account are required to recover statement registry authority.");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        long statementId = 0;
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText =
                """
                SELECT Id
                FROM BankStatements
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND BankAccountId=$BankAccountId
                  AND SourceFile=$SourceFile COLLATE NOCASE
                  AND ($SourceSheet='' OR SourceSheet='' OR SourceSheet=$SourceSheet COLLATE NOCASE)
                ORDER BY Id DESC
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$ClientId", statement.ClientId);
            existing.Parameters.AddWithValue("$FinancialYear", statement.FinancialYear.Trim());
            existing.Parameters.AddWithValue("$BankAccountId", statement.BankAccountId);
            existing.Parameters.AddWithValue("$SourceFile", sourceFile);
            existing.Parameters.AddWithValue("$SourceSheet", sourceSheet);
            var existingId = existing.ExecuteScalar();
            if (existingId is not null && existingId is not DBNull)
                statementId = Convert.ToInt64(existingId, CultureInfo.InvariantCulture);
        }

        if (statementId == 0)
        {
            statement.SourceFile = sourceFile;
            statement.SourceSheet = sourceSheet;
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO BankStatements(ClientId,FinancialYear,BankAccountId,SourceFile,SourcePath,SourceSheet,FileHash,
                    PeriodFrom,PeriodTo,OpeningBalance,OpeningBalanceSource,SourceOpeningBalance,SourceClosingBalance,ClosingBalanceSource,ClosingBalance,TotalDebit,TotalCredit,TransactionCount,ReconciliationStatus,Difference,OpenSeconds,ProcessSeconds,ImportedUtc)
                VALUES($ClientId,$FinancialYear,$BankAccountId,$SourceFile,$SourcePath,$SourceSheet,$FileHash,
                    $PeriodFrom,$PeriodTo,$OpeningBalance,$OpeningBalanceSource,$SourceOpeningBalance,$SourceClosingBalance,$ClosingBalanceSource,$ClosingBalance,$TotalDebit,$TotalCredit,$TransactionCount,$Status,$Difference,$OpenSeconds,$ProcessSeconds,$ImportedUtc);
                SELECT last_insert_rowid();
                """;
            AddStatementParameters(insert, statement);
            statementId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var relink = connection.CreateCommand())
        {
            relink.Transaction = transaction;
            relink.CommandText =
                """
                UPDATE BankTransactions
                SET StatementId=$StatementId
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND BankAccountId=$BankAccountId
                  AND SourceFile=$SourceFile COLLATE NOCASE
                  AND ($SourceSheet='' OR SourceSheet='' OR SourceSheet=$SourceSheet COLLATE NOCASE);
                """;
            relink.Parameters.AddWithValue("$StatementId", statementId);
            relink.Parameters.AddWithValue("$ClientId", statement.ClientId);
            relink.Parameters.AddWithValue("$FinancialYear", statement.FinancialYear.Trim());
            relink.Parameters.AddWithValue("$BankAccountId", statement.BankAccountId);
            relink.Parameters.AddWithValue("$SourceFile", sourceFile);
            relink.Parameters.AddWithValue("$SourceSheet", sourceSheet);
            relink.ExecuteNonQuery();
        }

        transaction.Commit();
        return statementId;
    }

    public void SaveStatementBalances(long statementId, decimal? openingBalance, decimal? closingBalance, string status, decimal difference, string openingBalanceSource = "", string closingBalanceSource = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankStatements
            SET OpeningBalance=$OpeningBalance, OpeningBalanceSource=CASE WHEN $OpeningBalanceSource<>'' THEN $OpeningBalanceSource ELSE OpeningBalanceSource END,
                ClosingBalance=$ClosingBalance, ClosingBalanceSource=CASE WHEN $ClosingBalanceSource<>'' THEN $ClosingBalanceSource ELSE ClosingBalanceSource END,
                ReconciliationStatus=$Status, Difference=$Difference
            WHERE Id=$Id;
            """;
        command.Parameters.AddWithValue("$Id", statementId);
        command.Parameters.AddWithValue("$OpeningBalance", openingBalance.HasValue ? (object)(double)openingBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$ClosingBalance", closingBalance.HasValue ? (object)(double)closingBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$OpeningBalanceSource", openingBalanceSource.Trim());
        command.Parameters.AddWithValue("$ClosingBalanceSource", closingBalanceSource.Trim());
        command.Parameters.AddWithValue("$Status", status.Trim());
        command.Parameters.AddWithValue("$Difference", (double)difference);
        command.ExecuteNonQuery();
    }

    public void RepairStatementBalanceEvidence(
        long statementId,
        DateTime? periodFrom,
        DateTime? periodTo,
        decimal? openingBalance,
        string openingBalanceSource,
        decimal? sourceOpeningBalance,
        decimal? sourceClosingBalance,
        decimal? closingBalance,
        string closingBalanceSource,
        string sourceFile,
        string sourceSheet,
        IReadOnlyDictionary<int, decimal> runningBalances)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var pair in runningBalances)
        {
            using var updateRow = connection.CreateCommand();
            updateRow.Transaction = transaction;
            updateRow.CommandText =
                """
                UPDATE BankTransactions
                SET RunningBalance=$RunningBalance,
                    RunningBalanceSource=$RunningBalanceSource
                WHERE StatementId=$StatementId AND SourceRow=$SourceRow
                  AND SourceFile=$SourceFile COLLATE NOCASE
                  AND ($SourceSheet='' OR SourceSheet='' OR SourceSheet=$SourceSheet COLLATE NOCASE);
                """;
            updateRow.Parameters.AddWithValue("$StatementId", statementId);
            updateRow.Parameters.AddWithValue("$SourceRow", pair.Key);
            updateRow.Parameters.AddWithValue("$SourceFile", (sourceFile ?? string.Empty).Trim());
            updateRow.Parameters.AddWithValue("$SourceSheet", (sourceSheet ?? string.Empty).Trim());
            updateRow.Parameters.AddWithValue("$RunningBalance", (double)pair.Value);
            updateRow.Parameters.AddWithValue("$RunningBalanceSource", "SOURCE");
            updateRow.ExecuteNonQuery();
        }

        using (var updateStatement = connection.CreateCommand())
        {
            updateStatement.Transaction = transaction;
            updateStatement.CommandText =
                """
                UPDATE BankStatements
                SET PeriodFrom=$PeriodFrom,
                    PeriodTo=$PeriodTo,
                    OpeningBalance=CASE WHEN UPPER(COALESCE(OpeningBalanceSource,''))='MANUAL' THEN OpeningBalance ELSE $OpeningBalance END,
                    OpeningBalanceSource=CASE WHEN UPPER(COALESCE(OpeningBalanceSource,''))='MANUAL' THEN OpeningBalanceSource ELSE $OpeningBalanceSource END,
                    SourceOpeningBalance=$SourceOpeningBalance,
                    SourceClosingBalance=$SourceClosingBalance,
                    ClosingBalance=CASE WHEN UPPER(COALESCE(ClosingBalanceSource,''))='MANUAL' THEN ClosingBalance ELSE $ClosingBalance END,
                    ClosingBalanceSource=CASE WHEN UPPER(COALESCE(ClosingBalanceSource,''))='MANUAL' THEN ClosingBalanceSource ELSE $ClosingBalanceSource END
                WHERE Id=$Id;
                """;
            updateStatement.Parameters.AddWithValue("$Id", statementId);
            updateStatement.Parameters.AddWithValue("$PeriodFrom", periodFrom.HasValue ? (object)periodFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
            updateStatement.Parameters.AddWithValue("$PeriodTo", periodTo.HasValue ? (object)periodTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
            updateStatement.Parameters.AddWithValue("$OpeningBalance", openingBalance.HasValue ? (object)(double)openingBalance.Value : DBNull.Value);
            updateStatement.Parameters.AddWithValue("$OpeningBalanceSource", openingBalanceSource.Trim());
            updateStatement.Parameters.AddWithValue("$SourceOpeningBalance", sourceOpeningBalance.HasValue ? (object)(double)sourceOpeningBalance.Value : DBNull.Value);
            updateStatement.Parameters.AddWithValue("$SourceClosingBalance", sourceClosingBalance.HasValue ? (object)(double)sourceClosingBalance.Value : DBNull.Value);
            updateStatement.Parameters.AddWithValue("$ClosingBalance", closingBalance.HasValue ? (object)(double)closingBalance.Value : DBNull.Value);
            updateStatement.Parameters.AddWithValue("$ClosingBalanceSource", closingBalanceSource.Trim());
            updateStatement.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void DeleteStatement(long statementId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        long accountId;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT BankAccountId FROM BankStatements WHERE Id=$Id;";
            lookup.Parameters.AddWithValue("$Id", statementId);
            var value = lookup.ExecuteScalar();
            if (value is null || value is DBNull) { transaction.Rollback(); return; }
            accountId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM BankStatements WHERE Id=$Id;";
            delete.Parameters.AddWithValue("$Id", statementId);
            delete.ExecuteNonQuery();
        }
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM BankAccounts WHERE Id=$AccountId AND NOT EXISTS(SELECT 1 FROM BankStatements WHERE BankAccountId=$AccountId);";
            cleanup.Parameters.AddWithValue("$AccountId", accountId);
            cleanup.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlySet<string> GetTransactionFingerprints(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Fingerprint FROM BankTransactions WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public BankStatementBatchCommitResult CommitBankCacheBatch(
        IReadOnlyList<BankStatementBatchCommitItem> items,
        IReadOnlyList<BankStatementRecord> existingStatementUpdates)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(existingStatementUpdates);
        var result = new BankStatementBatchCommitResult();
        if (items.Count == 0) return result;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        var accountIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        static string AccountKey(BankAccountRecord account) => BankAccountIdentity.BuildKey(account);

        foreach (var item in items)
        {
            var account = item.Account;
            var key = AccountKey(account);
            if (accountIds.ContainsKey(key)) continue;

            var existingId = FindAccountIdByIdentity(connection, transaction, account);
            if (existingId.HasValue)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE BankAccounts
                    SET BankName=CASE WHEN BankName='' AND $BankName<>'' THEN $BankName ELSE BankName END,
                        AccountNumber=CASE WHEN AccountNumber='' AND $AccountNumber<>'' THEN $AccountNumber ELSE AccountNumber END,
                        AccountType=CASE WHEN AccountType='' AND $AccountType<>'' THEN $AccountType ELSE AccountType END,
                        BranchName=CASE WHEN $BranchName<>'' THEN $BranchName ELSE BranchName END,
                        IfscCode=CASE WHEN $IfscCode<>'' THEN $IfscCode ELSE IfscCode END,
                        OdLimit=COALESCE($OdLimit,OdLimit),
                        DrawingPower=COALESCE($DrawingPower,DrawingPower),
                        LedgerName=CASE WHEN LedgerName='' AND $LedgerName<>'' THEN $LedgerName ELSE LedgerName END,
                        UpdatedUtc=$Now
                    WHERE Id=$Id;
                    """;
                AddAccountMergeParameters(update, account, now);
                update.Parameters.AddWithValue("$Id", existingId.Value);
                update.ExecuteNonQuery();
                accountIds[key] = existingId.Value;
                continue;
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO BankAccounts(ClientId,FinancialYear,BankName,AccountNumber,AccountType,BranchName,IfscCode,OdLimit,DrawingPower,LedgerName,CreatedUtc,UpdatedUtc)
                    VALUES($ClientId,$FinancialYear,$BankName,$AccountNumber,$AccountType,$BranchName,$IfscCode,$OdLimit,$DrawingPower,$LedgerName,$Now,$Now);
                    SELECT last_insert_rowid();
                    """;
                AddAccountMergeParameters(insert, account, now);
                accountIds[key] = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        // Whole-file replacement is source-path authoritative. If a changed source is
        // reclassified to another Bank/account, its prior contribution must still disappear.
        var replacedByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in items.Select(item => item.SourcePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM BankStatements
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND SourcePath=$SourcePath COLLATE NOCASE;
                """;
            delete.Parameters.AddWithValue("$ClientId", items[0].Statement.ClientId);
            delete.Parameters.AddWithValue("$FinancialYear", items[0].Statement.FinancialYear.Trim());
            delete.Parameters.AddWithValue("$SourcePath", sourcePath.Trim());
            replacedByPath[sourcePath] = delete.ExecuteNonQuery();
        }

        // Existing neighbouring statements can receive new CHAIN/MISSING/ACCOUNT status
        // because one source in their bank chain changed. Persist those finalized cache
        // results inside this same transaction.
        var replacedIds = items.Select(item => item.Statement.Id).Where(id => id > 0).ToHashSet();
        foreach (var statement in existingStatementUpdates.Where(statement => statement.Id > 0 && !replacedIds.Contains(statement.Id)))
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE BankStatements
                SET OpeningBalance=$OpeningBalance,
                    OpeningBalanceSource=$OpeningBalanceSource,
                    ClosingBalanceSource=$ClosingBalanceSource,
                    ClosingBalance=$ClosingBalance,
                    ReconciliationStatus=$Status,
                    Difference=$Difference
                WHERE Id=$Id;
                """;
            update.Parameters.AddWithValue("$OpeningBalance", statement.OpeningBalance.HasValue ? (object)(double)statement.OpeningBalance.Value : DBNull.Value);
            update.Parameters.AddWithValue("$OpeningBalanceSource", statement.OpeningBalanceSource ?? string.Empty);
            update.Parameters.AddWithValue("$ClosingBalanceSource", statement.ClosingBalanceSource ?? string.Empty);
            update.Parameters.AddWithValue("$ClosingBalance", statement.ClosingBalance.HasValue ? (object)(double)statement.ClosingBalance.Value : DBNull.Value);
            update.Parameters.AddWithValue("$Status", statement.ReconciliationStatus ?? string.Empty);
            update.Parameters.AddWithValue("$Difference", (double)statement.Difference);
            update.Parameters.AddWithValue("$Id", statement.Id);
            update.ExecuteNonQuery();
        }

        foreach (var item in items)
        {
            var account = item.Account;
            var statement = item.Statement;
            var accountId = accountIds[AccountKey(account)];
            account.Id = accountId;
            account.UpdatedUtc = DateTime.UtcNow;
            statement.BankAccountId = accountId;

            long statementId;
            using (var insertStatement = connection.CreateCommand())
            {
                insertStatement.Transaction = transaction;
                insertStatement.CommandText =
                    """
                    INSERT INTO BankStatements(ClientId,FinancialYear,BankAccountId,SourceFile,SourcePath,SourceSheet,FileHash,
                        PeriodFrom,PeriodTo,OpeningBalance,OpeningBalanceSource,SourceOpeningBalance,SourceClosingBalance,ClosingBalanceSource,ClosingBalance,TotalDebit,TotalCredit,TransactionCount,ReconciliationStatus,Difference,OpenSeconds,ProcessSeconds,ImportedUtc)
                    VALUES($ClientId,$FinancialYear,$BankAccountId,$SourceFile,$SourcePath,$SourceSheet,$FileHash,
                        $PeriodFrom,$PeriodTo,$OpeningBalance,$OpeningBalanceSource,$SourceOpeningBalance,$SourceClosingBalance,$ClosingBalanceSource,$ClosingBalance,$TotalDebit,$TotalCredit,$TransactionCount,$Status,$Difference,$OpenSeconds,$ProcessSeconds,$ImportedUtc);
                    SELECT last_insert_rowid();
                    """;
                AddStatementParameters(insertStatement, statement);
                statementId = Convert.ToInt64(insertStatement.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            statement.Id = statementId;

            var inserted = 0;
            foreach (var row in item.Transactions)
            {
                row.BankAccountId = accountId;
                row.StatementId = statementId;
                using var insertTransaction = connection.CreateCommand();
                insertTransaction.Transaction = transaction;
                insertTransaction.CommandText =
                    """
                    INSERT OR IGNORE INTO BankTransactions(ClientId,FinancialYear,StatementId,BankAccountId,TransactionDate,ValueDate,
                        Narration,NormalizedNarration,ReferenceNumber,Debit,Credit,RunningBalance,RunningBalanceSource,Mode,LedgerName,MappingStatus,
                        TransactionId,Fingerprint,RecordStatus,MatchId,MatchedTransactionId,SourceFile,SourceSheet,SourceRow,ImportedUtc)
                    VALUES($ClientId,$FinancialYear,$StatementId,$BankAccountId,$TransactionDate,$ValueDate,$Narration,$NormalizedNarration,
                        $ReferenceNumber,$Debit,$Credit,$RunningBalance,$RunningBalanceSource,$Mode,$LedgerName,$MappingStatus,$TransactionId,$Fingerprint,
                        $RecordStatus,$MatchId,$MatchedTransactionId,$SourceFile,$SourceSheet,$SourceRow,$ImportedUtc);
                    """;
                AddTransactionParameters(insertTransaction, row);
                inserted += insertTransaction.ExecuteNonQuery();
            }

            result.Items.Add(new BankStatementAtomicCommitResult
            {
                Account = account,
                StatementId = statementId,
                TransactionsInserted = inserted,
                ReplacedStatements = replacedByPath.TryGetValue(item.SourcePath, out var replaced) ? replaced : 0
            });
        }

        transaction.Commit();
        return result;
    }

    public int InsertTransactions(IEnumerable<BankTransactionRecord> transactions)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        foreach (var item in transactions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO BankTransactions(ClientId,FinancialYear,StatementId,BankAccountId,TransactionDate,ValueDate,
                    Narration,NormalizedNarration,ReferenceNumber,Debit,Credit,RunningBalance,RunningBalanceSource,Mode,LedgerName,MappingStatus,
                    TransactionId,Fingerprint,RecordStatus,MatchId,MatchedTransactionId,SourceFile,SourceSheet,SourceRow,ImportedUtc)
                VALUES($ClientId,$FinancialYear,$StatementId,$BankAccountId,$TransactionDate,$ValueDate,$Narration,$NormalizedNarration,
                    $ReferenceNumber,$Debit,$Credit,$RunningBalance,$RunningBalanceSource,$Mode,$LedgerName,$MappingStatus,$TransactionId,$Fingerprint,
                    $RecordStatus,$MatchId,$MatchedTransactionId,$SourceFile,$SourceSheet,$SourceRow,$ImportedUtc);
                """;
            AddTransactionParameters(command, item);
            count += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return count;
    }

    /// <summary>
    /// Makes only still-pending pre-import rows visible to Banking. Rows that the linked
    /// Tally workflow already changed to Archived Tally Reconciled are deliberately left
    /// archived, so a proven Tally Inter-Bank transaction never becomes an active import.
    /// </summary>
    public int FinalizePreImportStaging(long statementId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankTransactions
            SET RecordStatus='Active'
            WHERE StatementId=$StatementId
              AND RecordStatus='Archived Pre-Import Staging';
            """;
        command.Parameters.AddWithValue("$StatementId", statementId);
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<BankTransactionRecord> GetTransactions(long clientId, string financialYear, bool includeArchived = true)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,StatementId,BankAccountId,TransactionDate,ValueDate,Narration,NormalizedNarration,
                   ReferenceNumber,Debit,Credit,RunningBalance,RunningBalanceSource,Mode,LedgerName,MappingStatus,TransactionId,Fingerprint,RecordStatus,
                   MatchId,MatchedTransactionId,SourceFile,SourceSheet,SourceRow,ImportedUtc
            FROM BankTransactions
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND ($IncludeArchived=1 OR RecordStatus NOT LIKE 'Archived%')
            ORDER BY TransactionDate, Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$IncludeArchived", includeArchived ? 1 : 0);
        using var reader = command.ExecuteReader();
        var result = new List<BankTransactionRecord>();
        while (reader.Read()) result.Add(ReadTransaction(reader));
        return result;
    }

    public BankTransactionDuplicateCleanupResult CleanupExactTransactionDuplicates(
        long clientId,
        string financialYear,
        IReadOnlySet<long>? protectedAutoImportStatementIds = null)
    {
        // Fix24: physical Bank identity is decided before Ledger Mapping. The first
        // imported row is authoritative; later copies are removed even if the Ledger,
        // Mapping Status or Tally treatment differs. The protected-statement argument is
        // retained for API compatibility, but it cannot make a later physical duplicate
        // survive.
        var result = new BankTransactionDuplicateCleanupResult();
        var rows = GetTransactions(clientId, financialYear, includeArchived: true)
            .Where(row => !row.RecordStatus.Equals("Generated Transfer Charge", StringComparison.OrdinalIgnoreCase) &&
                          !row.RecordStatus.Equals("Archived Pre-Import Staging", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var groups = rows
            .Select(row => new { Row = row, Key = BuildFirstImportWinsDuplicateKey(row) })
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, item => item.Row, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();
        result.DuplicateGroupsFound = groups.Length;
        if (groups.Length == 0) return result;

        var removals = new List<(BankTransactionRecord Keep, BankTransactionRecord Remove)>();
        var touchedStatementIds = new HashSet<long>();
        foreach (var duplicateGroup in groups)
        {
            var ordered = duplicateGroup.OrderBy(row => row.Id).ToArray();
            if (HasContradictoryIndependentRunningBalances(ordered))
            {
                result.ConflictingGroupsSkipped++;
                continue;
            }
            var keep = ordered[0];
            foreach (var row in ordered.Skip(1))
            {
                removals.Add((keep, row));
                touchedStatementIds.Add(keep.StatementId);
                touchedStatementIds.Add(row.StatementId);
            }
        }
        if (removals.Count == 0) return result;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var removal in removals.OrderBy(item => item.Remove.Id))
        {
            QueuePendingTallyDuplicateDelete(connection, transaction, removal.Keep, removal.Remove);
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM BankTransactions WHERE Id=$Id;";
            delete.Parameters.AddWithValue("$Id", removal.Remove.Id);
            result.TransactionsRemoved += delete.ExecuteNonQuery();
        }

        foreach (var statementId in touchedStatementIds)
        {
            UpdateStatementTotalsAfterDuplicateRemoval(connection, transaction, statementId);
        }
        transaction.Commit();
        return result;
    }

    private static string BuildFirstImportWinsDuplicateKey(BankTransactionRecord row)
    {
        var reference = (row.ReferenceNumber ?? string.Empty).Trim().ToUpperInvariant();
        var amountKey = FormattableString.Invariant(
            $"{row.TransactionDate:yyyyMMdd}|{row.Debit:0.00}|{row.Credit:0.00}");

        // A non-empty Bank reference is the strongest physical identity and deliberately
        // ignores Ledger/Group/source filename. Running balance strengthens it when the
        // source supplied one, but is not mandatory when the exact Bank reference exists.
        if (reference.Length > 0)
        {
            return $"{row.ClientId}|{row.FinancialYear.Trim().ToUpperInvariant()}|{row.BankAccountId}|{amountKey}|REF:{reference}";
        }

        // Without a Bank reference, destructive cleanup requires independent running-
        // balance evidence in addition to normalized narration/date/amount. This keeps
        // legitimate repeated same-day cash rows distinct.
        if (!BankBalanceAuthority.IsIndependentRunningBalanceEvidence(row)) return string.Empty;
        var narration = BankNarrationParser.Normalize(row.Narration ?? string.Empty).Trim().ToUpperInvariant();
        if (narration.Length == 0) return string.Empty;
        return FormattableString.Invariant(
            $"{row.ClientId}|{row.FinancialYear.Trim().ToUpperInvariant()}|{row.BankAccountId}|{amountKey}|NAR:{narration}|BAL:{row.RunningBalance!.Value:0.00}");
    }

    private static bool HasContradictoryIndependentRunningBalances(IEnumerable<BankTransactionRecord> rows)
    {
        return rows
            .Where(BankBalanceAuthority.IsIndependentRunningBalanceEvidence)
            .Select(row => decimal.Round(row.RunningBalance!.Value, 2, MidpointRounding.AwayFromZero))
            .Distinct()
            .Take(2)
            .Count() > 1;
    }

    private static void QueuePendingTallyDuplicateDelete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BankTransactionRecord keep,
        BankTransactionRecord remove)
    {
        static string LatestMasterId(SqliteConnection connection, SqliteTransaction transaction, long clientId, string financialYear, long transactionId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT TallyMasterId FROM BankTallyExportLinks " +
                "WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND TransactionId=$TransactionId " +
                "AND TRIM(TallyMasterId)<>'' ORDER BY VerifiedUtc DESC, Id DESC LIMIT 1;";
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$TransactionId", transactionId);
            return command.ExecuteScalar()?.ToString()?.Trim() ?? string.Empty;
        }

        var physicalKey = BuildFirstImportWinsDuplicateKey(remove);
        if (physicalKey.Length == 0) return;
        var removedSourceKey = BuildBankTallySourceKey(remove);
        var retainedSourceKey = BuildBankTallySourceKey(keep);
        var knownMasterId = LatestMasterId(connection, transaction, remove.ClientId, remove.FinancialYear, remove.Id);
        var retainedMasterId = LatestMasterId(connection, transaction, keep.ClientId, keep.FinancialYear, keep.Id);
        var now = DateTime.UtcNow.ToString("O");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO BankPendingTallyDuplicateDeletes
            (
                ClientId,FinancialYear,RemovedTransactionId,RetainedTransactionId,PhysicalIdentityKey,
                TransactionDate,BankAccountId,Mode,ReferenceNumber,Narration,Debit,Credit,RunningBalance,
                RemovedSourceKey,RetainedSourceKey,KnownTallyMasterId,RetainedTallyMasterId,Status,Notes,CreatedUtc,UpdatedUtc
            )
            VALUES
            (
                $ClientId,$FinancialYear,$RemovedTransactionId,$RetainedTransactionId,$PhysicalIdentityKey,
                $TransactionDate,$BankAccountId,$Mode,$ReferenceNumber,$Narration,$Debit,$Credit,$RunningBalance,
                $RemovedSourceKey,$RetainedSourceKey,$KnownTallyMasterId,$RetainedTallyMasterId,'Pending',$Notes,$Now,$Now
            )
            ON CONFLICT(ClientId,FinancialYear,RemovedTransactionId)
            DO UPDATE SET
                RetainedTransactionId=excluded.RetainedTransactionId,
                PhysicalIdentityKey=excluded.PhysicalIdentityKey,
                TransactionDate=excluded.TransactionDate,
                BankAccountId=excluded.BankAccountId,
                Mode=excluded.Mode,
                ReferenceNumber=excluded.ReferenceNumber,
                Narration=excluded.Narration,
                Debit=excluded.Debit,
                Credit=excluded.Credit,
                RunningBalance=excluded.RunningBalance,
                RemovedSourceKey=excluded.RemovedSourceKey,
                RetainedSourceKey=excluded.RetainedSourceKey,
                KnownTallyMasterId=CASE WHEN TRIM(excluded.KnownTallyMasterId)<>'' THEN excluded.KnownTallyMasterId ELSE BankPendingTallyDuplicateDeletes.KnownTallyMasterId END,
                RetainedTallyMasterId=CASE WHEN TRIM(excluded.RetainedTallyMasterId)<>'' THEN excluded.RetainedTallyMasterId ELSE BankPendingTallyDuplicateDeletes.RetainedTallyMasterId END,
                Status='Pending',
                Notes=excluded.Notes,
                UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", remove.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", remove.FinancialYear.Trim());
        command.Parameters.AddWithValue("$RemovedTransactionId", remove.Id);
        command.Parameters.AddWithValue("$RetainedTransactionId", keep.Id);
        command.Parameters.AddWithValue("$PhysicalIdentityKey", physicalKey);
        command.Parameters.AddWithValue("$TransactionDate", remove.TransactionDate.ToString("O"));
        command.Parameters.AddWithValue("$BankAccountId", remove.BankAccountId);
        command.Parameters.AddWithValue("$Mode", remove.Mode ?? string.Empty);
        command.Parameters.AddWithValue("$ReferenceNumber", remove.ReferenceNumber?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$Narration", remove.Narration?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$Debit", (double)remove.Debit);
        command.Parameters.AddWithValue("$Credit", (double)remove.Credit);
        command.Parameters.AddWithValue("$RunningBalance", remove.RunningBalance.HasValue ? (object)(double)remove.RunningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$RemovedSourceKey", removedSourceKey);
        command.Parameters.AddWithValue("$RetainedSourceKey", retainedSourceKey);
        command.Parameters.AddWithValue("$KnownTallyMasterId", knownMasterId);
        command.Parameters.AddWithValue("$RetainedTallyMasterId", retainedMasterId);
        command.Parameters.AddWithValue("$Notes", $"Fix24 first-import-wins: retained Bank transaction {keep.Id}; removed later duplicate {remove.Id}. BankAccountId is part of physical identity; selected/top Ledger '{remove.LedgerName}' is treatment and did not influence identity.");
        command.Parameters.AddWithValue("$Now", now);
        command.ExecuteNonQuery();
    }

    private static string BuildBankTallySourceKey(BankTransactionRecord row)
    {
        var identity = string.IsNullOrWhiteSpace(row.TransactionId)
            ? row.Id.ToString(CultureInfo.InvariantCulture)
            : row.TransactionId.Trim();
        return $"AAH-BANK:{row.ClientId}:{row.FinancialYear.Trim().ToUpperInvariant()}:{identity}";
    }

    private static void UpdateStatementTotalsAfterDuplicateRemoval(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long statementId)
    {
        using var totals = connection.CreateCommand();
        totals.Transaction = transaction;
        totals.CommandText =
            """
            UPDATE BankStatements
            SET TotalDebit=COALESCE((SELECT SUM(Debit) FROM BankTransactions WHERE StatementId=$StatementId AND RecordStatus<>'Generated Transfer Charge'),0),
                TotalCredit=COALESCE((SELECT SUM(Credit) FROM BankTransactions WHERE StatementId=$StatementId AND RecordStatus<>'Generated Transfer Charge'),0),
                TransactionCount=COALESCE((SELECT COUNT(*) FROM BankTransactions WHERE StatementId=$StatementId AND RecordStatus<>'Generated Transfer Charge'),0),
                ReconciliationStatus=CASE
                    WHEN NOT EXISTS(SELECT 1 FROM BankTransactions WHERE StatementId=$StatementId AND RecordStatus<>'Generated Transfer Charge')
                    THEN 'Duplicate Source' ELSE ReconciliationStatus END,
                Difference=CASE
                    WHEN NOT EXISTS(SELECT 1 FROM BankTransactions WHERE StatementId=$StatementId AND RecordStatus<>'Generated Transfer Charge')
                    THEN 0 ELSE Difference END
            WHERE Id=$StatementId;
            """;
        totals.Parameters.AddWithValue("$StatementId", statementId);
        totals.ExecuteNonQuery();
    }

    public IReadOnlyList<BankPendingTallyDuplicateDeleteRecord> GetPendingTallyDuplicateDeletes(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,RemovedTransactionId,RetainedTransactionId,PhysicalIdentityKey,
                   TransactionDate,BankAccountId,Mode,ReferenceNumber,Narration,Debit,Credit,RunningBalance,
                   RemovedSourceKey,RetainedSourceKey,KnownTallyMasterId,RetainedTallyMasterId,Status,Notes,CreatedUtc,UpdatedUtc
            FROM BankPendingTallyDuplicateDeletes
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Pending' COLLATE NOCASE
            ORDER BY TransactionDate, Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<BankPendingTallyDuplicateDeleteRecord>();
        while (reader.Read())
        {
            result.Add(new BankPendingTallyDuplicateDeleteRecord
            {
                Id = reader.GetInt64(0),
                ClientId = reader.GetInt64(1),
                FinancialYear = reader.GetString(2),
                RemovedTransactionId = reader.GetInt64(3),
                RetainedTransactionId = reader.GetInt64(4),
                PhysicalIdentityKey = reader.GetString(5),
                TransactionDate = ParseDateTime(reader.GetString(6)),
                BankAccountId = reader.GetInt64(7),
                Mode = reader.GetString(8),
                ReferenceNumber = reader.GetString(9),
                Narration = reader.GetString(10),
                Debit = ReadDecimal(reader, 11),
                Credit = ReadDecimal(reader, 12),
                RunningBalance = reader.IsDBNull(13) ? null : ReadDecimal(reader, 13),
                RemovedSourceKey = reader.GetString(14),
                RetainedSourceKey = reader.GetString(15),
                KnownTallyMasterId = reader.GetString(16),
                RetainedTallyMasterId = reader.GetString(17),
                Status = reader.GetString(18),
                Notes = reader.GetString(19),
                CreatedUtc = ParseDateTime(reader.GetString(20)),
                UpdatedUtc = ParseDateTime(reader.GetString(21))
            });
        }
        return result;
    }

    public void UpdatePendingTallyDuplicateDelete(
        long queueId,
        string knownTallyMasterId,
        string retainedTallyMasterId,
        string status,
        string notes)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankPendingTallyDuplicateDeletes
            SET KnownTallyMasterId=CASE WHEN TRIM($KnownTallyMasterId)<>'' THEN $KnownTallyMasterId ELSE KnownTallyMasterId END,
                RetainedTallyMasterId=CASE WHEN TRIM($RetainedTallyMasterId)<>'' THEN $RetainedTallyMasterId ELSE RetainedTallyMasterId END,
                Status=$Status,Notes=$Notes,UpdatedUtc=$UpdatedUtc
            WHERE Id=$Id;
            """;
        command.Parameters.AddWithValue("$Id", queueId);
        command.Parameters.AddWithValue("$KnownTallyMasterId", knownTallyMasterId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$RetainedTallyMasterId", retainedTallyMasterId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$Status", string.IsNullOrWhiteSpace(status) ? "Pending" : status.Trim());
        command.Parameters.AddWithValue("$Notes", notes?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpsertBankTallyExportLink(
        long clientId,
        string financialYear,
        long transactionId,
        string sourceKey,
        string companyKey,
        string tallyMasterId,
        string voucherType = "",
        string bankLedger = "",
        string counterpartLedger = "")
    {
        if (transactionId <= 0 || string.IsNullOrWhiteSpace(tallyMasterId)) return;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BankTallyExportLinks
            (ClientId,FinancialYear,TransactionId,SourceKey,CompanyKey,TallyMasterId,LastVoucherType,LastBankLedger,LastCounterpartLedger,VerifiedUtc)
            VALUES
            ($ClientId,$FinancialYear,$TransactionId,$SourceKey,$CompanyKey,$TallyMasterId,$VoucherType,$BankLedger,$CounterpartLedger,$VerifiedUtc)
            ON CONFLICT(ClientId,FinancialYear,TransactionId,CompanyKey)
            DO UPDATE SET
                SourceKey=excluded.SourceKey,
                TallyMasterId=excluded.TallyMasterId,
                LastVoucherType=CASE WHEN excluded.LastVoucherType<>'' THEN excluded.LastVoucherType ELSE BankTallyExportLinks.LastVoucherType END,
                LastBankLedger=CASE WHEN excluded.LastBankLedger<>'' THEN excluded.LastBankLedger ELSE BankTallyExportLinks.LastBankLedger END,
                LastCounterpartLedger=CASE WHEN excluded.LastCounterpartLedger<>'' THEN excluded.LastCounterpartLedger ELSE BankTallyExportLinks.LastCounterpartLedger END,
                VerifiedUtc=excluded.VerifiedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$TransactionId", transactionId);
        command.Parameters.AddWithValue("$SourceKey", sourceKey?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$CompanyKey", companyKey?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$TallyMasterId", tallyMasterId.Trim());
        command.Parameters.AddWithValue("$VoucherType", voucherType?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$BankLedger", bankLedger?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$CounterpartLedger", counterpartLedger?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$VerifiedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public string GetBankTallyExportMasterId(long clientId, string financialYear, long transactionId, string companyKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TallyMasterId
            FROM BankTallyExportLinks
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND TransactionId=$TransactionId
              AND CompanyKey=$CompanyKey COLLATE NOCASE AND TRIM(TallyMasterId)<>''
            ORDER BY VerifiedUtc DESC, Id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$TransactionId", transactionId);
        command.Parameters.AddWithValue("$CompanyKey", companyKey?.Trim() ?? string.Empty);
        return command.ExecuteScalar()?.ToString()?.Trim() ?? string.Empty;
    }

    public (string TallyMasterId, string VoucherType, string BankLedger, string CounterpartLedger)? GetBankTallyExportTreatment(
        long clientId, string financialYear, long transactionId, string companyKey)
    {
        if (transactionId <= 0) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TallyMasterId,LastVoucherType,LastBankLedger,LastCounterpartLedger
            FROM BankTallyExportLinks
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND TransactionId=$TransactionId
              AND CompanyKey=$CompanyKey COLLATE NOCASE AND TRIM(TallyMasterId)<>''
            ORDER BY VerifiedUtc DESC, Id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$TransactionId", transactionId);
        command.Parameters.AddWithValue("$CompanyKey", companyKey?.Trim() ?? string.Empty);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0).Trim(), reader.GetString(1).Trim(), reader.GetString(2).Trim(), reader.GetString(3).Trim())
            : null;
    }

    public void SaveTransaction(BankTransactionRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE BankTransactions SET TransactionDate=$TransactionDate,ValueDate=$ValueDate,Narration=$Narration,
                    NormalizedNarration=$NormalizedNarration,ReferenceNumber=$ReferenceNumber,Debit=$Debit,Credit=$Credit,
                    RunningBalance=$RunningBalance,RunningBalanceSource=$RunningBalanceSource,Mode=$Mode,LedgerName=$LedgerName,MappingStatus=$MappingStatus,
                    RecordStatus=$RecordStatus,MatchId=$MatchId,MatchedTransactionId=$MatchedTransactionId
                WHERE Id=$Id;
                """;
            command.Parameters.AddWithValue("$Id", item.Id);
            AddTransactionParameters(command, item, includeCoreKeys: false);
            command.ExecuteNonQuery();
        }
        using (var totals = connection.CreateCommand())
        {
            totals.Transaction = transaction;
            totals.CommandText =
                """
                UPDATE BankStatements
                SET TotalDebit=COALESCE((SELECT SUM(Debit) FROM BankTransactions WHERE StatementId=$StatementId AND RecordStatus<>'Generated Transfer Charge'),0),
                    TotalCredit=COALESCE((SELECT SUM(Credit) FROM BankTransactions WHERE StatementId=$StatementId AND RecordStatus<>'Generated Transfer Charge'),0),
                    TransactionCount=COALESCE((SELECT COUNT(*) FROM BankTransactions WHERE StatementId=$StatementId AND RecordStatus<>'Generated Transfer Charge'),0)
                WHERE Id=$StatementId;
                """;
            totals.Parameters.AddWithValue("$StatementId", item.StatementId);
            totals.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SaveStatementReconciliation(long statementId, string status, decimal difference)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BankStatements SET ReconciliationStatus=$Status,Difference=$Difference WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Id", statementId);
        command.Parameters.AddWithValue("$Status", status.Trim());
        command.Parameters.AddWithValue("$Difference", (double)difference);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BankReconciliationSummary> GetReconciliation(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.Id,s.BankAccountId,
                   TRIM(a.BankName || CASE WHEN a.AccountType<>'' THEN ' '||a.AccountType ELSE '' END ||
                        CASE WHEN a.AccountNumber<>'' THEN ' - '||a.AccountNumber ELSE '' END),
                   s.PeriodFrom,s.PeriodTo,s.OpeningBalance,s.TotalDebit,s.TotalCredit,
                   s.ClosingBalance,s.Difference,s.ReconciliationStatus
            FROM BankStatements s JOIN BankAccounts a ON a.Id=s.BankAccountId
            WHERE s.ClientId=$ClientId AND s.FinancialYear=$FinancialYear
            ORDER BY s.PeriodFrom,s.Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<BankReconciliationSummary>();
        while (reader.Read())
        {
            decimal? opening = reader.IsDBNull(5) ? null : ReadDecimal(reader, 5);
            var debit = ReadDecimal(reader, 6);
            var credit = ReadDecimal(reader, 7);
            decimal? closing = reader.IsDBNull(8) ? null : ReadDecimal(reader, 8);
            result.Add(new BankReconciliationSummary
            {
                StatementId = reader.GetInt64(0),
                BankAccountId = reader.GetInt64(1),
                AccountName = reader.GetString(2),
                PeriodLabel = BuildPeriodLabel(ParseDate(reader.GetString(3)), ParseDate(reader.GetString(4))),
                OpeningBalance = opening,
                TotalDebit = debit,
                TotalCredit = credit,
                ExpectedClosingBalance = opening.HasValue ? opening.Value + credit - debit : null,
                StatementClosingBalance = closing,
                Difference = opening.HasValue && closing.HasValue ? ReadDecimal(reader, 9) : null,
                Status = !opening.HasValue || !closing.HasValue ? "Missing Balance Evidence" : reader.GetString(10)
            });
        }
        return result;
    }

    public int ReplaceTransferMatches(long clientId, string financialYear, IEnumerable<BankTransferMatch> matches)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM BankTransferMatches WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
            clear.Parameters.AddWithValue("$ClientId", clientId);
            clear.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            clear.ExecuteNonQuery();
        }
        using (var deleteGeneratedCharges = connection.CreateCommand())
        {
            deleteGeneratedCharges.Transaction = transaction;
            deleteGeneratedCharges.CommandText =
                "DELETE FROM BankTransactions WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND RecordStatus='Generated Transfer Charge';";
            deleteGeneratedCharges.Parameters.AddWithValue("$ClientId", clientId);
            deleteGeneratedCharges.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            deleteGeneratedCharges.ExecuteNonQuery();
        }
        using (var reset = connection.CreateCommand())
        {
            reset.Transaction = transaction;
            reset.CommandText =
                """
                UPDATE BankTransactions
                SET Mode=CASE WHEN MatchId<>'' THEN CASE WHEN Debit>0 THEN 'Payment' ELSE 'Receipt' END ELSE Mode END,
                    LedgerName=CASE WHEN MatchId<>'' THEN '' ELSE LedgerName END,
                    MappingStatus=CASE WHEN MatchId<>'' THEN 'Mapping Required' ELSE MappingStatus END,
                    MatchId='', MatchedTransactionId=NULL,
                    RecordStatus=CASE WHEN RecordStatus='Archived Interbank Counterpart' THEN 'Active' ELSE RecordStatus END
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND RecordStatus<>'Archived Tally Reconciled';
                """;
            reset.Parameters.AddWithValue("$ClientId", clientId);
            reset.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            reset.ExecuteNonQuery();
        }
        var count = 0;
        foreach (var item in matches)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var isTallyMatch = item.MatchOrigin.Equals("Tally Converted Bank Transaction", StringComparison.OrdinalIgnoreCase) ||
                               item.MatchId.StartsWith("TALLYTRF|", StringComparison.OrdinalIgnoreCase);
            var persistedTargetId = isTallyMatch ? item.SourceTransactionId : item.TargetTransactionId;
            insert.CommandText =
                """
                INSERT INTO BankTransferMatches
                (ClientId,FinancialYear,SourceTransactionId,TargetTransactionId,MatchId,MatchDate,Amount,ChargeAmount,ChargeRule,ReferenceNumber,Status,Reason,
                 MatchOrigin,TallyConversionId,TallyMasterId,TallyAlterId,TallyVoucherType,TallyVoucherNumber,TallyBankLedger,
                 TallyBankLedgerMasterId,ImportedBankLedgerMasterId,SourceBankLedgerMasterId,TargetBankLedgerMasterId,
                 TallyNarration,TallyReference,TallySignedAmount,TallyIsDebit,CreatedUtc)
                VALUES
                ($ClientId,$FinancialYear,$Source,$Target,$MatchId,$Date,$Amount,$ChargeAmount,$ChargeRule,$Reference,$Status,$Reason,
                 $MatchOrigin,$TallyConversionId,$TallyMasterId,$TallyAlterId,$TallyVoucherType,$TallyVoucherNumber,$TallyBankLedger,
                 $TallyBankLedgerMasterId,$ImportedBankLedgerMasterId,$SourceBankLedgerMasterId,$TargetBankLedgerMasterId,
                 $TallyNarration,$TallyReference,$TallySignedAmount,$TallyIsDebit,$CreatedUtc);
                """;
            insert.Parameters.AddWithValue("$ClientId", item.ClientId);
            insert.Parameters.AddWithValue("$FinancialYear", item.FinancialYear.Trim());
            insert.Parameters.AddWithValue("$Source", item.SourceTransactionId);
            insert.Parameters.AddWithValue("$Target", persistedTargetId);
            insert.Parameters.AddWithValue("$MatchId", item.MatchId);
            insert.Parameters.AddWithValue("$Date", item.MatchDate.ToString("yyyy-MM-dd"));
            insert.Parameters.AddWithValue("$Amount", (double)item.Amount);
            insert.Parameters.AddWithValue("$ChargeAmount", (double)item.ChargeAmount);
            insert.Parameters.AddWithValue("$ChargeRule", item.ChargeRule.Trim());
            insert.Parameters.AddWithValue("$Reference", item.ReferenceNumber.Trim());
            insert.Parameters.AddWithValue("$Status", item.Status.Trim());
            insert.Parameters.AddWithValue("$Reason", item.Reason.Trim());
            insert.Parameters.AddWithValue("$MatchOrigin", item.MatchOrigin.Trim());
            insert.Parameters.AddWithValue("$TallyConversionId", item.TallyConversionId.Trim());
            insert.Parameters.AddWithValue("$TallyMasterId", item.TallyMasterId.Trim());
            insert.Parameters.AddWithValue("$TallyAlterId", item.TallyAlterId.Trim());
            insert.Parameters.AddWithValue("$TallyVoucherType", item.TallyVoucherType.Trim());
            insert.Parameters.AddWithValue("$TallyVoucherNumber", item.TallyVoucherNumber.Trim());
            insert.Parameters.AddWithValue("$TallyBankLedger", item.TallyBankLedger.Trim());
            insert.Parameters.AddWithValue("$TallyBankLedgerMasterId", item.TallyBankLedgerMasterId.Trim());
            insert.Parameters.AddWithValue("$ImportedBankLedgerMasterId", item.ImportedBankLedgerMasterId.Trim());
            insert.Parameters.AddWithValue("$SourceBankLedgerMasterId", item.SourceBankLedgerMasterId.Trim());
            insert.Parameters.AddWithValue("$TargetBankLedgerMasterId", item.TargetBankLedgerMasterId.Trim());
            insert.Parameters.AddWithValue("$TallyNarration", item.TallyNarration.Trim());
            insert.Parameters.AddWithValue("$TallyReference", item.TallyReference.Trim());
            insert.Parameters.AddWithValue("$TallySignedAmount", (double)item.TallySignedAmount);
            insert.Parameters.AddWithValue("$TallyIsDebit", item.TallyIsDebit ? 1 : 0);
            insert.Parameters.AddWithValue("$CreatedUtc", (item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc).ToString("O"));
            count += insert.ExecuteNonQuery();

            if (isTallyMatch)
            {
                // Matching is only the accounting decision. Completion 33 deliberately
                // does NOT retire the imported row here. The Tally execution layer must
                // first ALTER/verify the exact live voucher as Contra. Imported retirement
                // is executed by the Tally layer and exact closing-balance reconciliation is
                // the final step; a failed final gate restores the imported row.
                continue;
            }

            using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText =
                """
                UPDATE BankTransactions
                SET MatchId=$MatchId,MatchedTransactionId=$Target,Mode='Contra',
                    LedgerName=COALESCE((SELECT a.LedgerName FROM BankTransactions t JOIN BankAccounts a ON a.Id=t.BankAccountId WHERE t.Id=$Target),''),
                    MappingStatus='Mapped'
                WHERE Id=$Source;
                """;
            source.Parameters.AddWithValue("$MatchId", item.MatchId);
            source.Parameters.AddWithValue("$Target", item.TargetTransactionId);
            source.Parameters.AddWithValue("$Source", item.SourceTransactionId);
            source.ExecuteNonQuery();

            using var target = connection.CreateCommand();
            target.Transaction = transaction;
            target.CommandText =
                """
                UPDATE BankTransactions
                SET MatchId=$MatchId,MatchedTransactionId=$Source,Mode='Contra',
                    LedgerName=COALESCE((SELECT a.LedgerName FROM BankTransactions t JOIN BankAccounts a ON a.Id=t.BankAccountId WHERE t.Id=$Source),''),
                    MappingStatus='Mapped',RecordStatus='Archived Interbank Counterpart'
                WHERE Id=$Target;
                """;
            target.Parameters.AddWithValue("$MatchId", item.MatchId);
            target.Parameters.AddWithValue("$Source", item.SourceTransactionId);
            target.Parameters.AddWithValue("$Target", item.TargetTransactionId);
            target.ExecuteNonQuery();

            if (item.ChargeAmount > 0.01m)
            {
                using var charge = connection.CreateCommand();
                charge.Transaction = transaction;
                charge.CommandText =
                    """
                    INSERT OR IGNORE INTO BankTransactions
                    (
                        ClientId,FinancialYear,StatementId,BankAccountId,TransactionDate,ValueDate,Narration,
                        NormalizedNarration,ReferenceNumber,Debit,Credit,RunningBalance,RunningBalanceSource,Mode,LedgerName,MappingStatus,
                        TransactionId,Fingerprint,RecordStatus,MatchId,MatchedTransactionId,SourceFile,SourceSheet,SourceRow,ImportedUtc
                    )
                    SELECT s.ClientId,s.FinancialYear,t.StatementId,t.BankAccountId,s.TransactionDate,s.ValueDate,s.Narration,
                           s.NormalizedNarration,s.ReferenceNumber,$ChargeAmount,0,NULL,'','Payment','Bank Charges','Mapped',
                           $ChargeTransactionId,$ChargeFingerprint,'Generated Transfer Charge',$MatchId,$Target,
                           'Transfer Reconciliation','Generated Charge',0,$CreatedUtc
                    FROM BankTransactions s
                    JOIN BankTransactions t ON t.Id=$Target
                    WHERE s.Id=$Source;
                    """;
                charge.Parameters.AddWithValue("$ChargeAmount", (double)item.ChargeAmount);
                charge.Parameters.AddWithValue("$ChargeTransactionId", $"BTX-CHARGE-{item.MatchId}");
                charge.Parameters.AddWithValue("$ChargeFingerprint", $"TRANSFER-CHARGE|{item.MatchId}|{item.ChargeAmount:0.00}");
                charge.Parameters.AddWithValue("$MatchId", item.MatchId);
                charge.Parameters.AddWithValue("$Source", item.SourceTransactionId);
                charge.Parameters.AddWithValue("$Target", item.TargetTransactionId);
                charge.Parameters.AddWithValue("$CreatedUtc", DateTime.UtcNow.ToString("O"));
                charge.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        return count;
    }

    public int UpdateTallyTransferMatchResult(
        long clientId,
        string financialYear,
        string matchId,
        string status,
        string reason,
        string tallyVoucherType = "Contra")
    {
        if (string.IsNullOrWhiteSpace(matchId)) return 0;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankTransferMatches
            SET Status=$Status,
                Reason=$Reason,
                TallyVoucherType=CASE WHEN TRIM($TallyVoucherType)<>'' THEN $TallyVoucherType ELSE TallyVoucherType END
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND MatchId=$MatchId;
            """;
        command.Parameters.AddWithValue("$Status", (status ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Reason", (reason ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$TallyVoucherType", (tallyVoucherType ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$MatchId", matchId.Trim());
        return command.ExecuteNonQuery();
    }

    public int ArchiveImportedTransactionMatchedToTally(
        long clientId,
        string financialYear,
        long transactionId,
        string matchId,
        string interBankCounterpartLedger)
    {
        if (transactionId <= 0 || string.IsNullOrWhiteSpace(matchId)) return 0;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankTransactions
            SET RecordStatus='Archived Tally Reconciled',
                Mode='Contra',
                LedgerName=$InterBankCounterpartLedger,
                MappingStatus='Mapped',
                MatchId=$MatchId,
                MatchedTransactionId=NULL
            WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND RecordStatus<>'Archived Tally Reconciled'
              AND RecordStatus NOT LIKE 'Duplicate%';
            """;
        command.Parameters.AddWithValue("$MatchId", matchId.Trim());
        command.Parameters.AddWithValue("$InterBankCounterpartLedger", (interBankCounterpartLedger ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Id", transactionId);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return command.ExecuteNonQuery();
    }

    public int RestoreImportedTransactionAfterTallyBalanceFailure(long clientId, string financialYear, long transactionId, string matchId)
    {
        if (transactionId <= 0 || string.IsNullOrWhiteSpace(matchId)) return 0;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankTransactions
            SET RecordStatus='Active',
                MappingStatus=CASE WHEN TRIM(LedgerName)<>'' THEN 'Mapped' ELSE 'Mapping Required' END,
                MatchId='',
                MatchedTransactionId=NULL
            WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND RecordStatus='Archived Tally Reconciled'
              AND MatchId=$MatchId;
            """;
        command.Parameters.AddWithValue("$Id", transactionId);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$MatchId", matchId.Trim());
        return command.ExecuteNonQuery();
    }

    public int ResetLegacyTallyConvertedArchives(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankTransactions
            SET RecordStatus='Active',
                MappingStatus=CASE WHEN TRIM(LedgerName)<>'' THEN 'Mapped' ELSE 'Mapping Required' END,
                MatchId='',
                MatchedTransactionId=NULL
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND RecordStatus='Archived Tally Reconciled'
              AND
              (
                  MatchId LIKE 'TALLYBTX|%'
                  OR
                  (
                      MatchId LIKE 'TALLYTRF|%'
                      AND NOT EXISTS
                      (
                          SELECT 1 FROM BankTransferMatches M
                          WHERE M.ClientId=BankTransactions.ClientId
                            AND M.FinancialYear=BankTransactions.FinancialYear
                            AND M.MatchId=BankTransactions.MatchId
                      )
                  )
              );
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// A live Tally voucher whose ALTERID changed invalidates only its own prior Tally
    /// Inter-Bank result. Restore that imported source row as active fallback and remove the
    /// old transfer match so the changed voucher can be reconciled again. Unchanged Tally
    /// matches are untouched.
    /// </summary>
    public int ResetChangedTallyTransferMatches(
        long clientId,
        string financialYear,
        IEnumerable<string> tallyMasterIds)
    {
        var ids = tallyMasterIds.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) return 0;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var restored = 0;
        foreach (var masterId in ids)
        {
            var rows = new List<(long SourceId, string MatchId)>();
            using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText =
                    """
                    SELECT SourceTransactionId,MatchId
                    FROM BankTransferMatches
                    WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                      AND TallyMasterId=$MasterId COLLATE NOCASE
                      AND (MatchOrigin='Tally Converted Bank Transaction' OR MatchId LIKE 'TALLYTRF|%');
                    """;
                find.Parameters.AddWithValue("$ClientId", clientId);
                find.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
                find.Parameters.AddWithValue("$MasterId", masterId);
                using var reader = find.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            foreach (var row in rows)
            {
                using var restore = connection.CreateCommand();
                restore.Transaction = transaction;
                restore.CommandText =
                    """
                    UPDATE BankTransactions
                    SET RecordStatus='Active',
                        -- Fix76: preserve the last verified Tally treatment until the
                        -- changed live MASTERID has been read. Clearing Mode/Ledger here
                        -- destroyed the previous-version evidence needed to decide whether
                        -- the change came from Tally or from a concurrent AAH edit.
                        MappingStatus=CASE WHEN TRIM(LedgerName)<>'' THEN 'Mapped' ELSE 'Mapping Required' END,
                        MatchId='', MatchedTransactionId=NULL
                    WHERE Id=$TransactionId AND ClientId=$ClientId AND FinancialYear=$FinancialYear
                      AND RecordStatus='Archived Tally Reconciled';
                    """;
                restore.Parameters.AddWithValue("$TransactionId", row.SourceId);
                restore.Parameters.AddWithValue("$ClientId", clientId);
                restore.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
                restored += restore.ExecuteNonQuery();
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM BankTransferMatches
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND TallyMasterId=$MasterId COLLATE NOCASE
                  AND (MatchOrigin='Tally Converted Bank Transaction' OR MatchId LIKE 'TALLYTRF|%');
                """;
            delete.Parameters.AddWithValue("$ClientId", clientId);
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            delete.Parameters.AddWithValue("$MasterId", masterId);
            delete.ExecuteNonQuery();
        }
        transaction.Commit();
        return restored;
    }

    /// <summary>
    /// Fix84 one-time/runtime recovery for externally-adopted Bank Charges vouchers.
    /// Earlier Fix80 adoption correctly kept Payment + Bank Charges, but archived the
    /// source row before a separately-created Bank Ledger MASTERID migration could run.
    /// Reactivate only rows whose linked previous Bank Ledger resolves to a different
    /// live Tally MASTERID than the Bank Account's current MASTERID.
    /// </summary>
    public int ReactivateArchivedBankChargesForBankMasterMigration(
        long clientId, string financialYear, string companyKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankTransactions
            SET RecordStatus='Active',
                MappingStatus=CASE WHEN TRIM(LedgerName)<>'' THEN 'Mapped' ELSE 'Mapping Required' END,
                MatchId='',MatchedTransactionId=NULL
            WHERE ClientId=$ClientId AND FinancialYear=$Fy
              AND RecordStatus='Archived Tally Reconciled'
              AND EXISTS
              (
                  SELECT 1
                  FROM BankTallyExportLinks L
                  JOIN BankAccounts A ON A.Id=BankTransactions.BankAccountId
                  JOIN Masters M
                    ON M.ClientId=BankTransactions.ClientId
                   AND M.FinancialYear=BankTransactions.FinancialYear
                   AND M.Type='Ledger' COLLATE NOCASE
                   AND M.Name=L.LastBankLedger COLLATE NOCASE
                  WHERE L.ClientId=BankTransactions.ClientId
                    AND L.FinancialYear=BankTransactions.FinancialYear
                    AND L.TransactionId=BankTransactions.Id
                    AND L.CompanyKey=$CompanyKey COLLATE NOCASE
                    AND L.LastCounterpartLedger='Bank Charges' COLLATE NOCASE
                    AND TRIM(A.TallyLedgerMasterId)<>''
                    AND TRIM(M.TallyMasterId)<>''
                    AND M.TallyMasterId<>A.TallyLedgerMasterId COLLATE NOCASE
              );
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey?.Trim() ?? string.Empty);
        return command.ExecuteNonQuery();
    }

    public int AdoptBoundTallyVoucherTreatment(
        long clientId,
        string financialYear,
        string companyKey,
        TallyConvertedBankTransaction changed,
        string counterpartLedger)
    {
        if (changed is null || string.IsNullOrWhiteSpace(changed.TallyMasterId)) return 0;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var candidates = new List<(long TransactionId,string Mode,string Ledger,string PreviousType,string PreviousBank,string PreviousCounterpart,string AccountMasterId,string AccountLedger)>();
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
                """
                SELECT T.Id,T.Mode,T.LedgerName,L.LastVoucherType,L.LastBankLedger,L.LastCounterpartLedger,
                       A.TallyLedgerMasterId,A.LedgerName
                FROM BankTallyExportLinks L
                JOIN BankTransactions T ON T.Id=L.TransactionId AND T.ClientId=L.ClientId AND T.FinancialYear=L.FinancialYear
                JOIN BankAccounts A ON A.Id=T.BankAccountId
                WHERE L.ClientId=$ClientId AND L.FinancialYear=$Fy
                  AND L.CompanyKey=$CompanyKey COLLATE NOCASE
                  AND L.TallyMasterId=$MasterId COLLATE NOCASE;
                """;
            find.Parameters.AddWithValue("$ClientId", clientId);
            find.Parameters.AddWithValue("$Fy", financialYear.Trim());
            find.Parameters.AddWithValue("$CompanyKey", companyKey?.Trim() ?? string.Empty);
            find.Parameters.AddWithValue("$MasterId", changed.TallyMasterId.Trim());
            using var reader = find.ExecuteReader();
            while (reader.Read())
                candidates.Add((reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7)));
        }

        static string Key(string? value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var changedBankMasterId = changed.BankLedgerMasterId.Trim();
        var changedBankKey = Key(changed.BankLedgerName);
        var adopted = 0;
        foreach (var row in candidates)
        {
            // Fix80: the Bank Account may itself have moved to a newly-created Ledger
            // MASTERID after this Voucher was last verified. Ownership is already proven
            // by the exact Voucher MASTERID link above, so accept either the current Bank
            // Account MASTERID or the saved previous Bank Ledger name as the bank side.
            // This lets a genuine Tally-side Contra->Payment/Receipt change be adopted
            // before a later same-Voucher MASTERID Bank-ledger migration is staged.
            var exactBank =
                (changedBankMasterId.Length > 0 && row.AccountMasterId.Trim().Length > 0 &&
                 changedBankMasterId.Equals(row.AccountMasterId.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                (changedBankKey.Length > 0 &&
                 (changedBankKey.Equals(Key(row.PreviousBank), StringComparison.OrdinalIgnoreCase) ||
                  changedBankKey.Equals(Key(row.AccountLedger), StringComparison.OrdinalIgnoreCase)));
            if (!exactBank) continue;

            // Previous-version gate. If AAH itself changed the stored treatment after the
            // last verified Tally link, do not overwrite it with an external Tally change;
            // that is a concurrent-change review case.
            var previousTypeMatches = row.PreviousType.Trim().Length == 0 ||
                                      row.Mode.Trim().Equals(row.PreviousType.Trim(), StringComparison.OrdinalIgnoreCase);
            var previousLedgerMatches = row.PreviousCounterpart.Trim().Length == 0 ||
                                        Key(row.Ledger).Equals(Key(row.PreviousCounterpart), StringComparison.OrdinalIgnoreCase);
            if (!previousTypeMatches || !previousLedgerMatches) continue;

            var liveType = changed.VoucherType.Trim();
            if (!(liveType.Equals("Payment", StringComparison.OrdinalIgnoreCase) ||
                  liveType.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ||
                  liveType.Equals("Contra", StringComparison.OrdinalIgnoreCase) ||
                  liveType.Equals("Journal", StringComparison.OrdinalIgnoreCase)))
                liveType = changed.Mode;
            var liveCounterpart = (counterpartLedger ?? string.Empty).Trim();
            var needsBankMasterMigration =
                changedBankMasterId.Length > 0 && row.AccountMasterId.Trim().Length > 0 &&
                !changedBankMasterId.Equals(row.AccountMasterId.Trim(), StringComparison.OrdinalIgnoreCase);

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE BankTransactions
                    SET Mode=$Mode,
                        LedgerName=$Ledger,
                        MappingStatus=CASE WHEN TRIM($Ledger)<>'' THEN 'Mapped' ELSE 'Mapping Required' END,
                        RecordStatus=$RecordStatus,
                        MatchId='',MatchedTransactionId=NULL
                    WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy;
                    """;
                update.Parameters.AddWithValue("$Mode", liveType);
                update.Parameters.AddWithValue("$Ledger", liveCounterpart);
                update.Parameters.AddWithValue("$RecordStatus", needsBankMasterMigration ? "Active" : "Archived Tally Reconciled");
                update.Parameters.AddWithValue("$Id", row.TransactionId);
                update.Parameters.AddWithValue("$ClientId", clientId);
                update.Parameters.AddWithValue("$Fy", financialYear.Trim());
                adopted += update.ExecuteNonQuery();
            }

            using (var link = connection.CreateCommand())
            {
                link.Transaction = transaction;
                link.CommandText =
                    """
                    UPDATE BankTallyExportLinks
                    SET LastVoucherType=$Type,LastBankLedger=$Bank,LastCounterpartLedger=$Counterpart,VerifiedUtc=$Utc
                    WHERE ClientId=$ClientId AND FinancialYear=$Fy AND TransactionId=$TransactionId
                      AND CompanyKey=$CompanyKey COLLATE NOCASE AND TallyMasterId=$MasterId COLLATE NOCASE;
                    """;
                link.Parameters.AddWithValue("$Type", liveType);
                link.Parameters.AddWithValue("$Bank", changed.BankLedgerName.Trim());
                link.Parameters.AddWithValue("$Counterpart", liveCounterpart);
                link.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
                link.Parameters.AddWithValue("$ClientId", clientId);
                link.Parameters.AddWithValue("$Fy", financialYear.Trim());
                link.Parameters.AddWithValue("$TransactionId", row.TransactionId);
                link.Parameters.AddWithValue("$CompanyKey", companyKey?.Trim() ?? string.Empty);
                link.Parameters.AddWithValue("$MasterId", changed.TallyMasterId.Trim());
                link.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        return adopted;
    }

    public int ResetPreCompletion33UnappliedTallyArchives(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BankTransactions
            SET RecordStatus='Active',
                MappingStatus=CASE WHEN TRIM(LedgerName)<>'' THEN 'Mapped' ELSE 'Mapping Required' END,
                MatchId='',
                MatchedTransactionId=NULL
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND RecordStatus='Archived Tally Reconciled'
              AND MatchId LIKE 'TALLYTRF|%'
              AND EXISTS
              (
                  SELECT 1 FROM BankTransferMatches M
                  WHERE M.ClientId=BankTransactions.ClientId
                    AND M.FinancialYear=BankTransactions.FinancialYear
                    AND M.MatchId=BankTransactions.MatchId
                    AND M.MatchOrigin='Tally Converted Bank Transaction'
                    AND M.Status NOT IN
                    (
                        'Tally Contra Applied - Balance Verified',
                        'Tally Contra Already Correct - Balance Verified',
                        'Tally Contra Applied - Balance Review Required',
                        'Tally Contra Already Correct - Balance Review Required'
                    )
              );
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<BankTransferMatch> GetTransferMatches(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ClientId,FinancialYear,SourceTransactionId,TargetTransactionId,MatchId,MatchDate,Amount,ChargeAmount,ChargeRule,ReferenceNumber,Status,Reason,MatchOrigin,TallyConversionId,TallyMasterId,TallyAlterId,TallyVoucherType,TallyVoucherNumber,TallyBankLedger,TallyBankLedgerMasterId,ImportedBankLedgerMasterId,SourceBankLedgerMasterId,TargetBankLedgerMasterId,TallyNarration,TallyReference,TallySignedAmount,TallyIsDebit,CreatedUtc FROM BankTransferMatches WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY MatchDate,Id;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<BankTransferMatch>();
        while (reader.Read())
        {
            result.Add(new BankTransferMatch
            {
                Id=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),SourceTransactionId=reader.GetInt64(3),
                TargetTransactionId=reader.GetString(13).Equals("Tally Converted Bank Transaction", StringComparison.OrdinalIgnoreCase) ? 0 : reader.GetInt64(4),
                MatchId=reader.GetString(5),MatchDate=ParseDate(reader.GetString(6)) ?? DateTime.MinValue,Amount=ReadDecimal(reader,7),ChargeAmount=ReadDecimal(reader,8),ChargeRule=reader.GetString(9),ReferenceNumber=reader.GetString(10),Status=reader.GetString(11),Reason=reader.GetString(12),
                MatchOrigin=reader.GetString(13),TallyConversionId=reader.GetString(14),TallyMasterId=reader.GetString(15),TallyAlterId=reader.GetString(16),TallyVoucherType=reader.GetString(17),TallyVoucherNumber=reader.GetString(18),TallyBankLedger=reader.GetString(19),
                TallyBankLedgerMasterId=reader.GetString(20),ImportedBankLedgerMasterId=reader.GetString(21),SourceBankLedgerMasterId=reader.GetString(22),TargetBankLedgerMasterId=reader.GetString(23),
                TallyNarration=reader.GetString(24),TallyReference=reader.GetString(25),TallySignedAmount=ReadDecimal(reader,26),TallyIsDebit=reader.GetInt32(27)!=0,CreatedUtc=ParseDateTime(reader.GetString(28))
            });
        }
        return result;
    }


    public IReadOnlyList<BankLedgerMapping> GetLedgerMappings(long clientId, string financialYear, bool pendingOnly = false)
    {
        using var connection = OpenConnection();
        var result = new List<BankLedgerMapping>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT Id,ClientId,FinancialYear,BankFilter,VoucherType,SummaryName,NarrationPattern,SourceLedgerName,LedgerName,MasterGroup,MatchCount,PaymentMatchCount,ReceiptMatchCount,Status,UpdatedUtc
                FROM BankLedgerMappings WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND ($PendingOnly=0 OR Status<>'Mapped')
                ORDER BY Status,MatchCount DESC,SummaryName COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$PendingOnly", pendingOnly ? 1 : 0);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new BankLedgerMapping
                {
                    Id=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),BankFilter=reader.GetString(3),VoucherType=reader.GetString(4),SummaryName=reader.GetString(5),NarrationPattern=reader.GetString(6),SourceLedgerName=reader.GetString(7),LedgerName=reader.GetString(8),MasterGroup=reader.GetString(9),MatchCount=reader.GetInt32(10),PaymentMatchCount=reader.GetInt32(11),ReceiptMatchCount=reader.GetInt32(12),Status=reader.GetString(13),UpdatedUtc=ParseDateTime(reader.GetString(14))
                });
            }
        }

        if (result.Count == 0) return result;
        var byId = result.ToDictionary(item => item.Id);
        using (var members = connection.CreateCommand())
        {
            members.CommandText =
                """
                SELECT M.MappingId,M.TransactionId
                FROM BankLedgerMappingMembers M
                INNER JOIN BankLedgerMappings L ON L.Id=M.MappingId
                WHERE L.ClientId=$ClientId AND L.FinancialYear=$FinancialYear
                ORDER BY M.MappingId,M.TransactionId;
                """;
            members.Parameters.AddWithValue("$ClientId", clientId);
            members.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            using var reader = members.ExecuteReader();
            while (reader.Read())
            {
                if (byId.TryGetValue(reader.GetInt64(0), out var mapping))
                    mapping.TransactionIds.Add(reader.GetInt64(1));
            }
        }
        return result;
    }

    public void UpsertLedgerMapping(BankLedgerMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO BankLedgerMappings(ClientId,FinancialYear,BankFilter,VoucherType,SummaryName,NarrationPattern,SourceLedgerName,LedgerName,MasterGroup,MatchCount,PaymentMatchCount,ReceiptMatchCount,Status,UpdatedUtc)
                VALUES($ClientId,$FinancialYear,$BankFilter,$VoucherType,$SummaryName,$Pattern,$SourceLedgerName,$LedgerName,$MasterGroup,$MatchCount,$PaymentMatchCount,$ReceiptMatchCount,$Status,$UpdatedUtc)
                ON CONFLICT(ClientId,FinancialYear,BankFilter,VoucherType,NarrationPattern,SourceLedgerName)
                DO UPDATE SET SummaryName=excluded.SummaryName,LedgerName=CASE WHEN excluded.LedgerName<>'' THEN excluded.LedgerName ELSE BankLedgerMappings.LedgerName END,
                              MasterGroup=CASE WHEN excluded.MasterGroup<>'' THEN excluded.MasterGroup ELSE BankLedgerMappings.MasterGroup END,
                              MatchCount=excluded.MatchCount,PaymentMatchCount=excluded.PaymentMatchCount,ReceiptMatchCount=excluded.ReceiptMatchCount,Status=excluded.Status,UpdatedUtc=excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$ClientId", mapping.ClientId);
            command.Parameters.AddWithValue("$FinancialYear", mapping.FinancialYear.Trim());
            command.Parameters.AddWithValue("$BankFilter", mapping.BankFilter.Trim());
            command.Parameters.AddWithValue("$VoucherType", mapping.VoucherType.Trim());
            command.Parameters.AddWithValue("$SummaryName", mapping.SummaryName.Trim());
            command.Parameters.AddWithValue("$Pattern", mapping.NarrationPattern.Trim());
            command.Parameters.AddWithValue("$SourceLedgerName", mapping.SourceLedgerName.Trim());
            command.Parameters.AddWithValue("$LedgerName", mapping.LedgerName.Trim());
            command.Parameters.AddWithValue("$MasterGroup", mapping.MasterGroup.Trim());
            command.Parameters.AddWithValue("$MatchCount", mapping.MatchCount);
            command.Parameters.AddWithValue("$PaymentMatchCount", mapping.PaymentMatchCount);
            command.Parameters.AddWithValue("$ReceiptMatchCount", mapping.ReceiptMatchCount);
            command.Parameters.AddWithValue("$Status", mapping.Status.Trim());
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        using (var selectId = connection.CreateCommand())
        {
            selectId.Transaction = transaction;
            selectId.CommandText =
                """
                SELECT Id FROM BankLedgerMappings
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND BankFilter=$BankFilter COLLATE NOCASE
                  AND VoucherType=$VoucherType COLLATE NOCASE AND NarrationPattern=$Pattern COLLATE NOCASE
                  AND SourceLedgerName=$SourceLedgerName COLLATE NOCASE LIMIT 1;
                """;
            selectId.Parameters.AddWithValue("$ClientId", mapping.ClientId);
            selectId.Parameters.AddWithValue("$FinancialYear", mapping.FinancialYear.Trim());
            selectId.Parameters.AddWithValue("$BankFilter", mapping.BankFilter.Trim());
            selectId.Parameters.AddWithValue("$VoucherType", mapping.VoucherType.Trim());
            selectId.Parameters.AddWithValue("$Pattern", mapping.NarrationPattern.Trim());
            selectId.Parameters.AddWithValue("$SourceLedgerName", mapping.SourceLedgerName.Trim());
            mapping.Id = Convert.ToInt64(selectId.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        ReplaceMappingMembers(connection, transaction, mapping.Id, mapping.TransactionIds);
        transaction.Commit();
    }

    public void ClearLedgerMapping(BankLedgerMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Id <= 0) return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var previousLedger = mapping.LedgerName?.Trim() ?? string.Empty;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE BankLedgerMappings SET LedgerName='', MasterGroup='', Status='Pending', UpdatedUtc=$UpdatedUtc WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear;";
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$Id", mapping.Id);
            command.Parameters.AddWithValue("$ClientId", mapping.ClientId);
            command.Parameters.AddWithValue("$FinancialYear", mapping.FinancialYear.Trim());
            command.ExecuteNonQuery();
        }

        var ids = mapping.TransactionIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length > 0)
        {
            const int batchSize = 400;
            for (var offset = 0; offset < ids.Length; offset += batchSize)
            {
                var batch = ids.Skip(offset).Take(batchSize).ToArray();
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                var names = new List<string>(batch.Length);
                for (var index = 0; index < batch.Length; index++)
                {
                    var name = $"$Id{index}";
                    names.Add(name);
                    update.Parameters.AddWithValue(name, batch[index]);
                }
                update.CommandText = $"UPDATE BankTransactions SET LedgerName='', MappingStatus='Mapping Required' WHERE Id IN ({string.Join(',', names)}) AND ClientId=$ClientId AND FinancialYear=$FinancialYear AND ($PreviousLedger='' OR LedgerName=$PreviousLedger COLLATE NOCASE);";
                update.Parameters.AddWithValue("$ClientId", mapping.ClientId);
                update.Parameters.AddWithValue("$FinancialYear", mapping.FinancialYear.Trim());
                update.Parameters.AddWithValue("$PreviousLedger", previousLedger);
                update.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        mapping.LedgerName = string.Empty;
        mapping.MasterGroup = string.Empty;
        mapping.Status = "Pending";
        mapping.UpdatedUtc = DateTime.UtcNow;
    }


    public int ClearLedgerMappingsForTransactions(long clientId, string financialYear, IReadOnlyCollection<long> transactionIds)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);
        var ids = transactionIds.Where(id => id > 0).Distinct().ToArray();
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || ids.Length == 0) return 0;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var affectedMappingIds = new HashSet<long>();
        const int batchSize = 350;

        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            var names = new List<string>(batch.Length);
            for (var index = 0; index < batch.Length; index++)
            {
                var name = $"$Id{index}";
                names.Add(name);
                select.Parameters.AddWithValue(name, batch[index]);
            }
            select.CommandText = $"""
                SELECT DISTINCT m.MappingId
                FROM BankLedgerMappingMembers m
                INNER JOIN BankLedgerMappings b ON b.Id=m.MappingId
                WHERE b.ClientId=$ClientId AND b.FinancialYear=$FinancialYear
                  AND m.TransactionId IN ({string.Join(',', names)});
                """;
            select.Parameters.AddWithValue("$ClientId", clientId);
            select.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            using var reader = select.ExecuteReader();
            while (reader.Read()) affectedMappingIds.Add(reader.GetInt64(0));
        }

        // A saved summary rule must not immediately re-apply a ledger that the user has
        // explicitly removed from part of its transaction membership. Neutralize every
        // affected saved rule; remaining transactions keep their own LedgerName and the
        // subsequent canonical rebuild recreates only the mappings that are still valid.
        foreach (var mappingBatch in affectedMappingIds.Chunk(batchSize))
        {
            using var updateMappings = connection.CreateCommand();
            updateMappings.Transaction = transaction;
            var names = new List<string>();
            var index = 0;
            foreach (var mappingId in mappingBatch)
            {
                var name = $"$MappingId{index++}";
                names.Add(name);
                updateMappings.Parameters.AddWithValue(name, mappingId);
            }
            if (names.Count == 0) continue;
            updateMappings.CommandText = $"""
                UPDATE BankLedgerMappings
                SET LedgerName='', MasterGroup='', Status='Pending', UpdatedUtc=$UpdatedUtc
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND Id IN ({string.Join(',', names)});
                """;
            updateMappings.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            updateMappings.Parameters.AddWithValue("$ClientId", clientId);
            updateMappings.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            updateMappings.ExecuteNonQuery();
        }

        var cleared = 0;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            using var updateTransactions = connection.CreateCommand();
            updateTransactions.Transaction = transaction;
            var names = new List<string>(batch.Length);
            for (var index = 0; index < batch.Length; index++)
            {
                var name = $"$Id{index}";
                names.Add(name);
                updateTransactions.Parameters.AddWithValue(name, batch[index]);
            }
            updateTransactions.CommandText = $"""
                UPDATE BankTransactions
                SET LedgerName='', MappingStatus='Mapping Required'
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND Id IN ({string.Join(',', names)});
                """;
            updateTransactions.Parameters.AddWithValue("$ClientId", clientId);
            updateTransactions.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            cleared += updateTransactions.ExecuteNonQuery();
        }

        transaction.Commit();
        return cleared;
    }

    public void ReplaceLedgerMappings(long clientId, string financialYear, IEnumerable<BankLedgerMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var rows = mappings.ToList();
        var existingRows = GetLedgerMappings(clientId, financialYear).ToList();

        static string MemberKey(BankLedgerMapping row) =>
            string.Join(",", row.TransactionIds.Where(id => id > 0).Distinct().OrderBy(id => id));

        static string FallbackKey(BankLedgerMapping row) => string.Join("|",
            row.BankFilter.Trim().ToUpperInvariant(),
            row.VoucherType.Trim().ToUpperInvariant(),
            row.NarrationPattern.Trim().ToUpperInvariant(),
            row.SourceLedgerName.Trim().ToUpperInvariant());

        static bool SameMembers(BankLedgerMapping left, BankLedgerMapping right) =>
            left.TransactionIds.Where(id => id > 0).Distinct().OrderBy(id => id)
                .SequenceEqual(right.TransactionIds.Where(id => id > 0).Distinct().OrderBy(id => id));

        static bool SameAuthority(BankLedgerMapping left, BankLedgerMapping right) =>
            left.BankFilter.Equals(right.BankFilter, StringComparison.OrdinalIgnoreCase) &&
            left.VoucherType.Equals(right.VoucherType, StringComparison.OrdinalIgnoreCase) &&
            left.SummaryName.Equals(right.SummaryName, StringComparison.OrdinalIgnoreCase) &&
            left.NarrationPattern.Equals(right.NarrationPattern, StringComparison.OrdinalIgnoreCase) &&
            left.SourceLedgerName.Equals(right.SourceLedgerName, StringComparison.OrdinalIgnoreCase) &&
            left.LedgerName.Equals(right.LedgerName, StringComparison.OrdinalIgnoreCase) &&
            left.MasterGroup.Equals(right.MasterGroup, StringComparison.OrdinalIgnoreCase) &&
            left.MatchCount == right.MatchCount &&
            left.PaymentMatchCount == right.PaymentMatchCount &&
            left.ReceiptMatchCount == right.ReceiptMatchCount &&
            left.Status.Equals(right.Status, StringComparison.OrdinalIgnoreCase) &&
            SameMembers(left, right);

        // R45: rebuilds are changed-only. Preserve the durable SQLite Id whenever the exact
        // transaction membership still represents the same logical mapping. This prevents a
        // normal Tally/Master refresh from turning every mapping into a new row identity and
        // forcing WPF to recycle the active Final Ledger editor.
        var unmatchedExisting = existingRows.ToDictionary(row => row.Id);
        var existingByMembers = existingRows
            .Select(row => new { Row = row, Key = MemberKey(row) })
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Row, StringComparer.Ordinal);
        var existingByFallback = existingRows
            .GroupBy(FallbackKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        var matches = new Dictionary<BankLedgerMapping, BankLedgerMapping>();
        foreach (var row in rows)
        {
            BankLedgerMapping? match = null;
            var memberKey = MemberKey(row);
            if (memberKey.Length > 0 && existingByMembers.TryGetValue(memberKey, out var byMembers) && unmatchedExisting.ContainsKey(byMembers.Id))
                match = byMembers;
            if (match is null && existingByFallback.TryGetValue(FallbackKey(row), out var byFallback) && unmatchedExisting.ContainsKey(byFallback.Id))
                match = byFallback;
            if (match is null) continue;
            matches[row] = match;
            unmatchedExisting.Remove(match.Id);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Remove only mappings that genuinely disappeared. Delete them before updates so an
        // obsolete unique-key row cannot conflict with a canonical replacement row.
        if (unmatchedExisting.Count > 0)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            var idParameters = unmatchedExisting.Keys.Select((id, index) => new { id, name = "$DeleteId" + index }).ToArray();
            delete.CommandText = $"DELETE FROM BankLedgerMappings WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Id IN ({string.Join(',', idParameters.Select(item => item.name))});";
            delete.Parameters.AddWithValue("$ClientId", clientId);
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            foreach (var item in idParameters) delete.Parameters.AddWithValue(item.name, item.id);
            delete.ExecuteNonQuery();
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE BankLedgerMappings SET
                BankFilter=$BankFilter,
                VoucherType=$VoucherType,
                SummaryName=$SummaryName,
                NarrationPattern=$Pattern,
                SourceLedgerName=$SourceLedgerName,
                LedgerName=$LedgerName,
                MasterGroup=$MasterGroup,
                MatchCount=$MatchCount,
                PaymentMatchCount=$PaymentMatchCount,
                ReceiptMatchCount=$ReceiptMatchCount,
                Status=$Status,
                UpdatedUtc=$UpdatedUtc
            WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear;
            """;
        var updateId = update.Parameters.Add("$Id", SqliteType.Integer);
        var updateClient = update.Parameters.Add("$ClientId", SqliteType.Integer);
        var updateYear = update.Parameters.Add("$FinancialYear", SqliteType.Text);
        var updateBank = update.Parameters.Add("$BankFilter", SqliteType.Text);
        var updateVoucher = update.Parameters.Add("$VoucherType", SqliteType.Text);
        var updateSummary = update.Parameters.Add("$SummaryName", SqliteType.Text);
        var updatePattern = update.Parameters.Add("$Pattern", SqliteType.Text);
        var updateSource = update.Parameters.Add("$SourceLedgerName", SqliteType.Text);
        var updateLedger = update.Parameters.Add("$LedgerName", SqliteType.Text);
        var updateGroup = update.Parameters.Add("$MasterGroup", SqliteType.Text);
        var updateCount = update.Parameters.Add("$MatchCount", SqliteType.Integer);
        var updatePayment = update.Parameters.Add("$PaymentMatchCount", SqliteType.Integer);
        var updateReceipt = update.Parameters.Add("$ReceiptMatchCount", SqliteType.Integer);
        var updateStatus = update.Parameters.Add("$Status", SqliteType.Text);
        var updateUtc = update.Parameters.Add("$UpdatedUtc", SqliteType.Text);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO BankLedgerMappings
                (ClientId,FinancialYear,BankFilter,VoucherType,SummaryName,NarrationPattern,SourceLedgerName,LedgerName,MasterGroup,MatchCount,PaymentMatchCount,ReceiptMatchCount,Status,UpdatedUtc)
            VALUES
                ($ClientId,$FinancialYear,$BankFilter,$VoucherType,$SummaryName,$Pattern,$SourceLedgerName,$LedgerName,$MasterGroup,$MatchCount,$PaymentMatchCount,$ReceiptMatchCount,$Status,$UpdatedUtc);
            SELECT last_insert_rowid();
            """;
        var insertClient = insert.Parameters.Add("$ClientId", SqliteType.Integer);
        var insertYear = insert.Parameters.Add("$FinancialYear", SqliteType.Text);
        var insertBank = insert.Parameters.Add("$BankFilter", SqliteType.Text);
        var insertVoucher = insert.Parameters.Add("$VoucherType", SqliteType.Text);
        var insertSummary = insert.Parameters.Add("$SummaryName", SqliteType.Text);
        var insertPattern = insert.Parameters.Add("$Pattern", SqliteType.Text);
        var insertSource = insert.Parameters.Add("$SourceLedgerName", SqliteType.Text);
        var insertLedger = insert.Parameters.Add("$LedgerName", SqliteType.Text);
        var insertGroup = insert.Parameters.Add("$MasterGroup", SqliteType.Text);
        var insertCount = insert.Parameters.Add("$MatchCount", SqliteType.Integer);
        var insertPayment = insert.Parameters.Add("$PaymentMatchCount", SqliteType.Integer);
        var insertReceipt = insert.Parameters.Add("$ReceiptMatchCount", SqliteType.Integer);
        var insertStatus = insert.Parameters.Add("$Status", SqliteType.Text);
        var insertUtc = insert.Parameters.Add("$UpdatedUtc", SqliteType.Text);

        foreach (var row in rows)
        {
            if (matches.TryGetValue(row, out var existing))
            {
                row.Id = existing.Id;
                if (SameAuthority(existing, row))
                {
                    row.UpdatedUtc = existing.UpdatedUtc;
                    continue;
                }

                row.UpdatedUtc = DateTime.UtcNow;
                updateId.Value = row.Id;
                updateClient.Value = clientId;
                updateYear.Value = financialYear.Trim();
                updateBank.Value = row.BankFilter.Trim();
                updateVoucher.Value = row.VoucherType.Trim();
                updateSummary.Value = row.SummaryName.Trim();
                updatePattern.Value = row.NarrationPattern.Trim();
                updateSource.Value = row.SourceLedgerName.Trim();
                updateLedger.Value = row.LedgerName.Trim();
                updateGroup.Value = row.MasterGroup.Trim();
                updateCount.Value = row.MatchCount;
                updatePayment.Value = row.PaymentMatchCount;
                updateReceipt.Value = row.ReceiptMatchCount;
                updateStatus.Value = row.Status.Trim();
                updateUtc.Value = row.UpdatedUtc.ToString("O");
                update.ExecuteNonQuery();
                if (!SameMembers(existing, row)) ReplaceMappingMembers(connection, transaction, row.Id, row.TransactionIds);
                continue;
            }

            row.UpdatedUtc = DateTime.UtcNow;
            insertClient.Value = clientId;
            insertYear.Value = financialYear.Trim();
            insertBank.Value = row.BankFilter.Trim();
            insertVoucher.Value = row.VoucherType.Trim();
            insertSummary.Value = row.SummaryName.Trim();
            insertPattern.Value = row.NarrationPattern.Trim();
            insertSource.Value = row.SourceLedgerName.Trim();
            insertLedger.Value = row.LedgerName.Trim();
            insertGroup.Value = row.MasterGroup.Trim();
            insertCount.Value = row.MatchCount;
            insertPayment.Value = row.PaymentMatchCount;
            insertReceipt.Value = row.ReceiptMatchCount;
            insertStatus.Value = row.Status.Trim();
            insertUtc.Value = row.UpdatedUtc.ToString("O");
            var insertedId = insert.ExecuteScalar();
            row.Id = insertedId is null || insertedId is DBNull
                ? 0L
                : Convert.ToInt64(insertedId, CultureInfo.InvariantCulture);
            ReplaceMappingMembers(connection, transaction, row.Id, row.TransactionIds);
        }

        transaction.Commit();
    }

    private static void ReplaceMappingMembers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long mappingId,
        IEnumerable<long> transactionIds)
    {
        if (mappingId <= 0) return;
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM BankLedgerMappingMembers WHERE MappingId=$MappingId;";
            delete.Parameters.AddWithValue("$MappingId", mappingId);
            delete.ExecuteNonQuery();
        }

        var ids = transactionIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return;
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO BankLedgerMappingMembers(MappingId,TransactionId) VALUES($MappingId,$TransactionId);";
        var mappingParameter = insert.Parameters.Add("$MappingId", SqliteType.Integer);
        var transactionParameter = insert.Parameters.Add("$TransactionId", SqliteType.Integer);
        foreach (var transactionId in ids)
        {
            mappingParameter.Value = mappingId;
            transactionParameter.Value = transactionId;
            insert.ExecuteNonQuery();
        }
    }

    public int ApplyLedgerMapping(long clientId, string financialYear, BankLedgerMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        UpsertLedgerMapping(mapping);

        if (mapping.TransactionIds.Count > 0)
            return ApplyLedgerAssignments(mapping.TransactionIds.ToDictionary(id => id, _ => mapping.LedgerName.Trim()));

        var accounts = GetAccounts(clientId, financialYear).ToDictionary(item => item.Id);
        var allTransactions = GetTransactions(clientId, financialYear, false).ToList();
        var transactions = allTransactions
            .Where(item => !ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(item.Mode))
            .ToList();
        var parsed = transactions
            .Select(item => new
            {
                Transaction = item,
                ExactSummary = ExcelLedgerMappingParser.BuildNormalizedSummary(item.Narration, item.Mode)
            })
            .Where(item => item.ExactSummary.Length > 0)
            .ToList();
        var counts = parsed
            .GroupBy(item => item.ExactSummary, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var groupMap = ExcelLedgerMappingParser.BuildFastGroupMap(counts);

        var targetSummary = mapping.SummaryName.Trim();
        var targetBank = mapping.BankFilter.Trim();
        var targetVoucher = mapping.VoucherType.Trim();
        var matchingIds = new List<long>();

        foreach (var item in parsed)
        {
            var transaction = item.Transaction;
            if (!VoucherMatches(transaction.Mode, targetVoucher)) continue;
            if (!AccountMatches(accounts.GetValueOrDefault(transaction.BankAccountId), targetBank)) continue;
            if (!string.Equals(transaction.LedgerName?.Trim() ?? string.Empty, mapping.SourceLedgerName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

            var canonical = groupMap.TryGetValue(item.ExactSummary, out var representative)
                ? representative
                : item.ExactSummary;
            // Workbook parity: Map All writes only the exact cached Summary Name +
            // current Bank Statement ledger group. Never widen with fuzzy party or
            // narration Contains matching at apply time.
            if (string.Equals(canonical, targetSummary, StringComparison.OrdinalIgnoreCase))
                matchingIds.Add(transaction.Id);
        }

        if (matchingIds.Count == 0) return 0;
        return ApplyLedgerAssignments(matchingIds.ToDictionary(id => id, _ => mapping.LedgerName.Trim()));
    }

    public int ApplyLedgerMappings(long clientId, string financialYear, IEnumerable<BankLedgerMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var activeMappings = mappings
            .Where(item => item.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.LedgerName))
            .ToList();
        if (activeMappings.Count == 0) return 0;

        var assignments = new Dictionary<long, string>();
        foreach (var mapping in activeMappings.Where(item => item.TransactionIds.Count > 0))
        {
            foreach (var transactionId in mapping.TransactionIds.Where(id => id > 0))
                assignments[transactionId] = mapping.LedgerName.Trim();
        }
        var legacyMappings = activeMappings.Where(item => item.TransactionIds.Count == 0).ToList();
        if (legacyMappings.Count == 0) return ApplyLedgerAssignments(assignments);

        var accounts = GetAccounts(clientId, financialYear).ToDictionary(item => item.Id);
        var allTransactions = GetTransactions(clientId, financialYear, false).ToList();
        var transactions = allTransactions
            .Where(item => !ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(item.Mode))
            .ToList();

        var parsed = transactions
            .Select(item => new
            {
                Transaction = item,
                ExactSummary = ExcelLedgerMappingParser.BuildNormalizedSummary(item.Narration, item.Mode)
            })
            .Where(item => item.ExactSummary.Length > 0)
            .ToList();
        var counts = parsed
            .GroupBy(item => item.ExactSummary, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var groupMap = ExcelLedgerMappingParser.BuildFastGroupMap(counts);

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in legacyMappings)
        {
            var summary = mapping.SummaryName.Trim();
            if (summary.Length == 0) continue;
            lookup[BuildMappingLookupKey(mapping.BankFilter, mapping.VoucherType, summary, mapping.SourceLedgerName)] = mapping.LedgerName.Trim();
        }

        foreach (var item in parsed)
        {
            var canonical = groupMap.TryGetValue(item.ExactSummary, out var representative) ? representative : item.ExactSummary;
            if (canonical.Length == 0) continue;
            var transaction = item.Transaction;
            var account = accounts.GetValueOrDefault(transaction.BankAccountId);
            if (account is null) continue;

            var bankCandidates = new[] { account.AuthorityName, account.LedgerName, account.BankName, "ALL BANKS" }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var voucherCandidates = new[] { transaction.Mode, "ALL TYPES", "Mixed" }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);

            string? ledger = null;
            foreach (var bank in bankCandidates)
            {
                foreach (var voucher in voucherCandidates)
                {
                    if (lookup.TryGetValue(BuildMappingLookupKey(bank, voucher, canonical, transaction.LedgerName), out ledger)) break;
                }
                if (!string.IsNullOrWhiteSpace(ledger)) break;
            }
            if (!string.IsNullOrWhiteSpace(ledger)) assignments[transaction.Id] = ledger;
        }

        return ApplyLedgerAssignments(assignments);
    }

    private int ApplyLedgerAssignments(IReadOnlyDictionary<long, string> assignments)
    {
        if (assignments.Count == 0) return 0;
        using var connection = OpenConnection();
        using var transactionScope = connection.BeginTransaction();
        var affected = 0;
        foreach (var ledgerGroup in assignments.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase))
        {
            var ids = ledgerGroup.Select(item => item.Key).Where(id => id > 0).Distinct().ToArray();
            foreach (var batch in ids.Chunk(400))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transactionScope;
                var parameterNames = new List<string>();
                for (var index = 0; index < batch.Length; index++)
                {
                    var name = $"$Id{index}";
                    parameterNames.Add(name);
                    command.Parameters.AddWithValue(name, batch[index]);
                }
                command.CommandText =
                    $"UPDATE BankTransactions SET LedgerName=$LedgerName,MappingStatus='Mapped' " +
                    $"WHERE Id IN ({string.Join(',', parameterNames)}) " +
                    "AND (COALESCE(LedgerName,'') <> $LedgerName OR COALESCE(MappingStatus,'') <> 'Mapped');";
                command.Parameters.AddWithValue("$LedgerName", ledgerGroup.Key);
                affected += command.ExecuteNonQuery();
            }
        }
        transactionScope.Commit();
        return affected;
    }

    private static string BuildMappingLookupKey(string bank, string voucher, string summary, string? sourceLedgerName) =>
        $"{bank.Trim()}\u001f{voucher.Trim()}\u001f{summary.Trim()}\u001f{(sourceLedgerName ?? string.Empty).Trim()}";

    private static bool VoucherMatches(string transactionMode, string targetVoucher) =>
        string.IsNullOrWhiteSpace(targetVoucher) || targetVoucher.Equals("All Types", StringComparison.OrdinalIgnoreCase) ||
        targetVoucher.Equals("Mixed", StringComparison.OrdinalIgnoreCase) || transactionMode.Equals(targetVoucher, StringComparison.OrdinalIgnoreCase);

    private static bool AccountMatches(BankAccountRecord? account, string targetBank)
    {
        if (string.IsNullOrWhiteSpace(targetBank) || targetBank.Equals("ALL BANKS", StringComparison.OrdinalIgnoreCase)) return true;
        if (account is null) return false;
        return string.Equals(account.AuthorityName, targetBank, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(account.LedgerName, targetBank, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(account.BankName, targetBank, StringComparison.OrdinalIgnoreCase);
    }

    public CashEntrySettings GetCashSettings(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LayoutMode,DefaultCommonLedger,OpeningBalance,UpdatedUtc FROM CashEntrySettings WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new CashEntrySettings { ClientId=clientId,FinancialYear=financialYear.Trim() };
        return new CashEntrySettings { ClientId=clientId,FinancialYear=financialYear.Trim(),LayoutMode=reader.GetString(0),DefaultCommonLedger=reader.GetString(1),OpeningBalance=ReadDecimal(reader,2),UpdatedUtc=ParseDateTime(reader.GetString(3)) };
    }

    public void SaveCashSettings(CashEntrySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CashEntrySettings(ClientId,FinancialYear,LayoutMode,DefaultCommonLedger,OpeningBalance,UpdatedUtc)
            VALUES($ClientId,$FinancialYear,$LayoutMode,$DefaultCommonLedger,$OpeningBalance,$UpdatedUtc)
            ON CONFLICT(ClientId,FinancialYear) DO UPDATE SET LayoutMode=excluded.LayoutMode,DefaultCommonLedger=excluded.DefaultCommonLedger,OpeningBalance=excluded.OpeningBalance,UpdatedUtc=excluded.UpdatedUtc
            WHERE CashEntrySettings.LayoutMode IS NOT excluded.LayoutMode
               OR CashEntrySettings.DefaultCommonLedger IS NOT excluded.DefaultCommonLedger
               OR CashEntrySettings.OpeningBalance IS NOT excluded.OpeningBalance;
            """;
        command.Parameters.AddWithValue("$ClientId", settings.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", settings.FinancialYear.Trim());
        command.Parameters.AddWithValue("$LayoutMode", settings.LayoutMode.Trim());
        command.Parameters.AddWithValue("$DefaultCommonLedger", settings.DefaultCommonLedger.Trim());
        command.Parameters.AddWithValue("$OpeningBalance", (double)settings.OpeningBalance);
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<CashEntryRecord> GetCashEntries(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ClientId,FinancialYear,EntryDate,EntryLedger,Debit,Credit,Narration,CommonLedger,MappedMasterLedger,VoucherType,Status,EntryId,IsUserEdited,UpdatedUtc,WorkflowStatus,ReferenceNumber,AttachmentPath,ReversalOfId,ClonedFromId,RecurringProfileId,SourceRunId,SourceJobId,CreatedBy,ApprovedBy,ApprovedUtc FROM CashEntries WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY EntryDate,Id;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<CashEntryRecord>();
        while (reader.Read())
        {
            result.Add(new CashEntryRecord
            {
                Id=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),EntryDate=ParseDate(reader.GetString(3)) ?? DateTime.MinValue,EntryLedger=reader.GetString(4),Debit=ReadDecimal(reader,5),Credit=ReadDecimal(reader,6),Narration=reader.GetString(7),CommonLedger=reader.GetString(8),MappedMasterLedger=reader.GetString(9),VoucherType=reader.GetString(10),Status=reader.GetString(11),EntryId=reader.GetString(12),IsUserEdited=reader.GetInt32(13)==1,UpdatedUtc=ParseDateTime(reader.GetString(14)),WorkflowStatus=reader.GetString(15),ReferenceNumber=reader.GetString(16),AttachmentPath=reader.GetString(17),ReversalOfId=reader.IsDBNull(18)?null:reader.GetInt64(18),ClonedFromId=reader.IsDBNull(19)?null:reader.GetInt64(19),RecurringProfileId=reader.IsDBNull(20)?null:reader.GetInt64(20),SourceRunId=reader.GetString(21),SourceJobId=reader.GetString(22),CreatedBy=reader.GetString(23),ApprovedBy=reader.GetString(24),ApprovedUtc=string.IsNullOrWhiteSpace(reader.GetString(25))?null:ParseDateTime(reader.GetString(25))
            });
        }
        return result;
    }

    public void SaveCashEntry(CashEntryRecord entry)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        SaveCashEntryInTransaction(entry, connection, transaction);
        transaction.Commit();
    }

    internal void SaveCashEntryInTransaction(CashEntryRecord entry, SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.EntryId.Length == 0) entry.EntryId = $"CE-{entry.EntryDate:yyyyMMdd}-{Guid.NewGuid():N}";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (entry.Id <= 0)
        {
            command.CommandText =
                """
                INSERT INTO CashEntries(ClientId,FinancialYear,EntryDate,EntryLedger,Debit,Credit,Narration,CommonLedger,MappedMasterLedger,VoucherType,Status,EntryId,IsUserEdited,UpdatedUtc,WorkflowStatus,ReferenceNumber,AttachmentPath,ReversalOfId,ClonedFromId,RecurringProfileId,SourceRunId,SourceJobId,CreatedBy,ApprovedBy,ApprovedUtc)
                VALUES($ClientId,$FinancialYear,$EntryDate,$EntryLedger,$Debit,$Credit,$Narration,$CommonLedger,$MappedMasterLedger,$VoucherType,$Status,$EntryId,1,$UpdatedUtc,$WorkflowStatus,$ReferenceNumber,$AttachmentPath,$ReversalOfId,$ClonedFromId,$RecurringProfileId,$SourceRunId,$SourceJobId,$CreatedBy,$ApprovedBy,$ApprovedUtc);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE CashEntries SET EntryDate=$EntryDate,EntryLedger=$EntryLedger,Debit=$Debit,Credit=$Credit,Narration=$Narration,CommonLedger=$CommonLedger,
                    MappedMasterLedger=$MappedMasterLedger,VoucherType=$VoucherType,Status=$Status,WorkflowStatus=$WorkflowStatus,ReferenceNumber=$ReferenceNumber,AttachmentPath=$AttachmentPath,ReversalOfId=$ReversalOfId,ClonedFromId=$ClonedFromId,RecurringProfileId=$RecurringProfileId,SourceRunId=$SourceRunId,SourceJobId=$SourceJobId,CreatedBy=$CreatedBy,ApprovedBy=$ApprovedBy,ApprovedUtc=$ApprovedUtc,IsUserEdited=1,UpdatedUtc=$UpdatedUtc WHERE Id=$Id;
                SELECT $Id;
                """;
            command.Parameters.AddWithValue("$Id", entry.Id);
        }
        command.Parameters.AddWithValue("$ClientId", entry.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", entry.FinancialYear.Trim());
        command.Parameters.AddWithValue("$EntryDate", entry.EntryDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$EntryLedger", entry.EntryLedger.Trim());
        command.Parameters.AddWithValue("$Debit", (double)entry.Debit);
        command.Parameters.AddWithValue("$Credit", (double)entry.Credit);
        command.Parameters.AddWithValue("$Narration", entry.Narration.Trim());
        command.Parameters.AddWithValue("$CommonLedger", entry.CommonLedger.Trim());
        command.Parameters.AddWithValue("$MappedMasterLedger", entry.MappedMasterLedger.Trim());
        command.Parameters.AddWithValue("$VoucherType", entry.VoucherType.Trim());
        command.Parameters.AddWithValue("$Status", entry.Status.Trim());
        command.Parameters.AddWithValue("$EntryId", entry.EntryId.Trim());
        command.Parameters.AddWithValue("$WorkflowStatus", string.IsNullOrWhiteSpace(entry.WorkflowStatus) ? "Draft" : entry.WorkflowStatus.Trim());
        command.Parameters.AddWithValue("$ReferenceNumber", entry.ReferenceNumber?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$AttachmentPath", entry.AttachmentPath?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$ReversalOfId", (object?)entry.ReversalOfId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ClonedFromId", (object?)entry.ClonedFromId ?? DBNull.Value);
        command.Parameters.AddWithValue("$RecurringProfileId", (object?)entry.RecurringProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$SourceRunId", entry.SourceRunId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$SourceJobId", entry.SourceJobId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$CreatedBy", string.IsNullOrWhiteSpace(entry.CreatedBy) ? Environment.UserName : entry.CreatedBy.Trim());
        command.Parameters.AddWithValue("$ApprovedBy", entry.ApprovedBy?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$ApprovedUtc", entry.ApprovedUtc?.ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        entry.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void DeleteCashEntry(long entryId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CashEntries WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Id", entryId);
        command.ExecuteNonQuery();
    }

    public int DeleteCashEntries(long clientId, string financialYear, IReadOnlyCollection<long> entryIds)
    {
        var ids = (entryIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct().ToArray();
        if (clientId <= 0 || ids.Length == 0) return 0;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM CashEntries WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear;";
        var idParameter = command.Parameters.Add("$Id", SqliteType.Integer);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        var deleted = 0;
        foreach (var id in ids)
        {
            idParameter.Value = id;
            deleted += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return deleted;
    }

    private static bool HasBankingMaintenanceMarker(SqliteConnection connection, string repairKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM BankingMaintenanceState WHERE RepairKey=$RepairKey LIMIT 1);";
        command.Parameters.AddWithValue("$RepairKey", repairKey);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static void SetBankingMaintenanceMarker(SqliteConnection connection, string repairKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO BankingMaintenanceState(RepairKey,CompletedUtc) VALUES($RepairKey,$CompletedUtc) " +
            "ON CONFLICT(RepairKey) DO UPDATE SET CompletedUtc=excluded.CompletedUtc;";
        command.Parameters.AddWithValue("$RepairKey", repairKey);
        command.Parameters.AddWithValue("$CompletedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void RepairFirstImportWinsTransactionDuplicates(SqliteConnection connection)
    {
        using var read = connection.CreateCommand();
        read.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,StatementId,BankAccountId,TransactionDate,ValueDate,Narration,NormalizedNarration,
                   ReferenceNumber,Debit,Credit,RunningBalance,RunningBalanceSource,Mode,LedgerName,MappingStatus,TransactionId,Fingerprint,RecordStatus,
                   MatchId,MatchedTransactionId,SourceFile,SourceSheet,SourceRow,ImportedUtc
            FROM BankTransactions
            WHERE RecordStatus<>'Generated Transfer Charge'
              AND RecordStatus<>'Archived Pre-Import Staging'
            ORDER BY Id;
            """;
        var rows = new List<BankTransactionRecord>();
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read()) rows.Add(ReadTransaction(reader));
        }
        if (rows.Count < 2) return;

        var groups = rows
            .Select(row => new { Row = row, Key = BuildFirstImportWinsDuplicateKey(row) })
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, item => item.Row, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (groups.Length == 0) return;

        using var transaction = connection.BeginTransaction();
        var touchedStatements = new HashSet<long>();
        foreach (var duplicateGroup in groups)
        {
            var ordered = duplicateGroup.OrderBy(row => row.Id).ToArray();
            if (HasContradictoryIndependentRunningBalances(ordered)) continue;
            var keep = ordered[0];
            foreach (var remove in ordered.Skip(1))
            {
                QueuePendingTallyDuplicateDelete(connection, transaction, keep, remove);
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM BankTransactions WHERE Id=$Id;";
                delete.Parameters.AddWithValue("$Id", remove.Id);
                delete.ExecuteNonQuery();
                touchedStatements.Add(keep.StatementId);
                touchedStatements.Add(remove.StatementId);
            }
        }

        foreach (var statementId in touchedStatements)
            UpdateStatementTotalsAfterDuplicateRemoval(connection, transaction, statementId);
        transaction.Commit();
    }

    private static void RepairDuplicateBankAccountsByStrongNumber(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        var accounts = new List<BankAccountRepairRow>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT a.Id,a.ClientId,a.FinancialYear,a.BankName,a.AccountNumber,a.AccountType,
                       a.BranchName,a.IfscCode,a.OdLimit,a.DrawingPower,a.LedgerName,
                       a.TallyLedgerMasterId,a.TallyLedgerGuid,a.TallyLedgerAlterId,a.TallyLedgerVerifiedName,
                       a.CreatedUtc,a.UpdatedUtc,
                       (SELECT COUNT(*) FROM BankTransactions t WHERE t.BankAccountId=a.Id),
                       (SELECT COUNT(*) FROM BankStatements s WHERE s.BankAccountId=a.Id)
                FROM BankAccounts a;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var account = new BankAccountRecord
                {
                    Id = reader.GetInt64(0),
                    ClientId = reader.GetInt64(1),
                    FinancialYear = reader.GetString(2),
                    BankName = reader.GetString(3),
                    AccountNumber = reader.GetString(4),
                    AccountType = reader.GetString(5),
                    BranchName = reader.GetString(6),
                    IfscCode = reader.GetString(7),
                    OdLimit = reader.IsDBNull(8) ? null : ReadDecimal(reader, 8),
                    DrawingPower = reader.IsDBNull(9) ? null : ReadDecimal(reader, 9),
                    LedgerName = reader.GetString(10),
                    TallyLedgerMasterId = reader.GetString(11),
                    TallyLedgerGuid = reader.GetString(12),
                    TallyLedgerAlterId = reader.GetInt64(13),
                    TallyLedgerVerifiedName = reader.GetString(14),
                    CreatedUtc = ParseDateTime(reader.GetString(15)),
                    UpdatedUtc = ParseDateTime(reader.GetString(16))
                };
                accounts.Add(new BankAccountRepairRow(account, reader.GetInt64(17), reader.GetInt64(18)));
            }
        }

        var groups = accounts
            .Where(row => BankAccountIdentity.StrongAccountNumber(row.Account.AccountNumber).Length > 0)
            .GroupBy(row => BankAccountIdentity.BuildKey(row.Account), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();

        foreach (var group in groups)
        {
            var distinctBoundMasterIds = group.Select(row => row.Account.TallyLedgerMasterId.Trim())
                .Where(masterId => masterId.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // Fix74: never collapse two Bank Accounts that are already bound to different
            // live Tally Ledger MASTERIDs merely because their statement account number
            // normalizes the same. MASTERID identity outranks local repair heuristics.
            if (distinctBoundMasterIds.Length > 1) continue;

            var ordered = group
                .OrderByDescending(row => row.Account.HasTallyLedgerIdentity)
                .ThenByDescending(row => IsCustomLedgerName(row.Account))
                .ThenByDescending(row => row.TransactionCount)
                .ThenByDescending(row => row.StatementCount)
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.Account.AccountType))
                .ThenByDescending(row => MetadataScore(row.Account))
                .ThenByDescending(row => row.Account.UpdatedUtc)
                .ThenBy(row => row.Account.Id)
                .ToArray();
            if (ordered.Length < 2) continue;

            var survivor = ordered[0].Account;
            var aliases = ordered.Skip(1).Select(row => row.Account).ToArray();

            foreach (var alias in aliases)
            {
                foreach (var table in new[] { "BankStatements", "BankTransactions", "BankStatementImportAttempts" })
                {
                    using var relink = connection.CreateCommand();
                    relink.Transaction = transaction;
                    relink.CommandText = $"UPDATE {table} SET BankAccountId=$Survivor WHERE BankAccountId=$Alias;";
                    relink.Parameters.AddWithValue("$Survivor", survivor.Id);
                    relink.Parameters.AddWithValue("$Alias", alias.Id);
                    relink.ExecuteNonQuery();
                }

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM BankAccounts WHERE Id=$Id;";
                delete.Parameters.AddWithValue("$Id", alias.Id);
                delete.ExecuteNonQuery();
            }

            var merged = MergeAccountMetadata(survivor, aliases);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE BankAccounts
                SET BankName=$BankName,AccountNumber=$AccountNumber,AccountType=$AccountType,
                    BranchName=$BranchName,IfscCode=$IfscCode,OdLimit=$OdLimit,DrawingPower=$DrawingPower,
                    LedgerName=$LedgerName,TallyLedgerMasterId=$TallyLedgerMasterId,TallyLedgerGuid=$TallyLedgerGuid,
                    TallyLedgerAlterId=$TallyLedgerAlterId,TallyLedgerVerifiedName=$TallyLedgerVerifiedName,
                    UpdatedUtc=$UpdatedUtc
                WHERE Id=$Id;
                """;
            update.Parameters.AddWithValue("$Id", survivor.Id);
            update.Parameters.AddWithValue("$BankName", merged.BankName.Trim());
            update.Parameters.AddWithValue("$AccountNumber", merged.AccountNumber.Trim());
            update.Parameters.AddWithValue("$AccountType", merged.AccountType.Trim());
            update.Parameters.AddWithValue("$BranchName", merged.BranchName.Trim());
            update.Parameters.AddWithValue("$IfscCode", merged.IfscCode.Trim().ToUpperInvariant());
            update.Parameters.AddWithValue("$OdLimit", merged.OdLimit.HasValue ? (object)(double)merged.OdLimit.Value : DBNull.Value);
            update.Parameters.AddWithValue("$DrawingPower", merged.DrawingPower.HasValue ? (object)(double)merged.DrawingPower.Value : DBNull.Value);
            update.Parameters.AddWithValue("$LedgerName", merged.LedgerName.Trim());
            update.Parameters.AddWithValue("$TallyLedgerMasterId", merged.TallyLedgerMasterId.Trim());
            update.Parameters.AddWithValue("$TallyLedgerGuid", merged.TallyLedgerGuid.Trim());
            update.Parameters.AddWithValue("$TallyLedgerAlterId", merged.TallyLedgerAlterId);
            update.Parameters.AddWithValue("$TallyLedgerVerifiedName", merged.TallyLedgerVerifiedName.Trim());
            update.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static BankAccountRecord MergeAccountMetadata(BankAccountRecord survivor, IReadOnlyList<BankAccountRecord> aliases)
    {
        var all = new[] { survivor }.Concat(aliases).ToArray();
        var bankName = survivor.BankName.Trim();
        if (bankName.Length == 0) bankName = all.Select(x => x.BankName.Trim()).FirstOrDefault(x => x.Length > 0) ?? string.Empty;

        var accountNumber = survivor.AccountNumber.Trim();
        if (accountNumber.Length == 0) accountNumber = all.Select(x => x.AccountNumber.Trim()).FirstOrDefault(x => x.Length > 0) ?? string.Empty;

        var accountType = survivor.AccountType.Trim();
        if (accountType.Length == 0)
        {
            accountType = all.Select(x => x.AccountType.Trim()).FirstOrDefault(x =>
                x.Length > 0 && !BankNameAlreadyContainsType(bankName, x)) ?? string.Empty;
        }

        var boundAuthority = all.Where(account => account.HasTallyLedgerIdentity)
            .OrderByDescending(account => account.TallyLedgerAlterId)
            .ThenBy(account => account.Id)
            .FirstOrDefault();
        var customLedger = all.FirstOrDefault(IsCustomLedgerName)?.LedgerName.Trim() ?? string.Empty;
        var ledgerName = boundAuthority is not null
            ? (boundAuthority.LedgerName.Trim().Length > 0 ? boundAuthority.LedgerName.Trim() : boundAuthority.TallyLedgerVerifiedName.Trim())
            : customLedger.Length > 0
                ? customLedger
                : survivor.LedgerName.Trim().Length > 0
                    ? survivor.LedgerName.Trim()
                    : all.Select(x => x.LedgerName.Trim()).FirstOrDefault(x => x.Length > 0) ?? string.Empty;

        return new BankAccountRecord
        {
            Id = survivor.Id,
            ClientId = survivor.ClientId,
            FinancialYear = survivor.FinancialYear,
            BankName = bankName,
            AccountNumber = accountNumber,
            AccountType = accountType,
            BranchName = survivor.BranchName.Trim().Length > 0 ? survivor.BranchName : all.Select(x => x.BranchName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            IfscCode = survivor.IfscCode.Trim().Length > 0 ? survivor.IfscCode : all.Select(x => x.IfscCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            OdLimit = survivor.OdLimit ?? all.Select(x => x.OdLimit).FirstOrDefault(x => x.HasValue),
            DrawingPower = survivor.DrawingPower ?? all.Select(x => x.DrawingPower).FirstOrDefault(x => x.HasValue),
            LedgerName = ledgerName,
            TallyLedgerMasterId = boundAuthority?.TallyLedgerMasterId ?? string.Empty,
            TallyLedgerGuid = boundAuthority?.TallyLedgerGuid ?? string.Empty,
            TallyLedgerAlterId = boundAuthority?.TallyLedgerAlterId ?? 0,
            TallyLedgerVerifiedName = boundAuthority?.TallyLedgerVerifiedName ?? string.Empty,
            CreatedUtc = survivor.CreatedUtc,
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private static bool BankNameAlreadyContainsType(string bankName, string accountType)
    {
        var bankTokens = bankName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return bankTokens.Any(token => token.Equals(accountType.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCustomLedgerName(BankAccountRecord account)
    {
        var ledger = account.LedgerName.Trim();
        return ledger.Length > 0 && !ledger.Equals(account.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static int MetadataScore(BankAccountRecord account)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(account.AccountType)) score += 4;
        if (!string.IsNullOrWhiteSpace(account.IfscCode)) score += 3;
        if (!string.IsNullOrWhiteSpace(account.BranchName)) score += 2;
        if (account.OdLimit.HasValue) score++;
        if (account.DrawingPower.HasValue) score++;
        return score;
    }

    private static long? FindAccountIdByIdentity(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        BankAccountRecord account)
    {
        var strongNumber = BankAccountIdentity.StrongAccountNumber(account.AccountNumber);
        if (strongNumber.Length > 0)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT Id,BankName,AccountNumber,AccountType,IfscCode FROM BankAccounts
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;
                """;
            command.Parameters.AddWithValue("$ClientId", account.ClientId);
            command.Parameters.AddWithValue("$FinancialYear", account.FinancialYear.Trim());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var candidate = new BankAccountRecord
                {
                    Id = reader.GetInt64(0),
                    ClientId = account.ClientId,
                    FinancialYear = account.FinancialYear,
                    BankName = reader.GetString(1),
                    AccountNumber = reader.GetString(2),
                    AccountType = reader.GetString(3),
                    IfscCode = reader.GetString(4)
                };
                if (BankAccountIdentity.Same(candidate, account))
                    return candidate.Id;
            }
            return null;
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT Id FROM BankAccounts
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND BankName=$BankName COLLATE NOCASE
                  AND AccountNumber=$AccountNumber COLLATE NOCASE
                  AND AccountType=$AccountType COLLATE NOCASE
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$ClientId", account.ClientId);
            command.Parameters.AddWithValue("$FinancialYear", account.FinancialYear.Trim());
            command.Parameters.AddWithValue("$BankName", account.BankName.Trim());
            command.Parameters.AddWithValue("$AccountNumber", account.AccountNumber.Trim());
            command.Parameters.AddWithValue("$AccountType", account.AccountType.Trim());
            var value = command.ExecuteScalar();
            return value is null || value is DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    private static BankAccountRecord ReadAccountById(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientId, FinancialYear, BankName, AccountNumber, AccountType,
                   BranchName, IfscCode, OdLimit, DrawingPower, LedgerName,
                   TallyLedgerMasterId, TallyLedgerGuid, TallyLedgerAlterId, TallyLedgerVerifiedName,
                   CreatedUtc, UpdatedUtc
            FROM BankAccounts WHERE Id=$Id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The bank account could not be loaded.");
        return ReadAccount(reader);
    }

    private static void AddAccountMergeParameters(SqliteCommand command, BankAccountRecord account, string now)
    {
        command.Parameters.AddWithValue("$ClientId", account.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", account.FinancialYear.Trim());
        command.Parameters.AddWithValue("$BankName", account.BankName.Trim());
        command.Parameters.AddWithValue("$AccountNumber", account.AccountNumber.Trim());
        command.Parameters.AddWithValue("$AccountType", account.AccountType.Trim());
        command.Parameters.AddWithValue("$BranchName", account.BranchName.Trim());
        command.Parameters.AddWithValue("$IfscCode", account.IfscCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$OdLimit", account.OdLimit.HasValue ? (object)(double)account.OdLimit.Value : DBNull.Value);
        command.Parameters.AddWithValue("$DrawingPower", account.DrawingPower.HasValue ? (object)(double)account.DrawingPower.Value : DBNull.Value);
        command.Parameters.AddWithValue("$LedgerName", account.LedgerName.Trim());
        command.Parameters.AddWithValue("$TallyLedgerMasterId", account.TallyLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$TallyLedgerGuid", account.TallyLedgerGuid.Trim());
        command.Parameters.AddWithValue("$TallyLedgerAlterId", account.TallyLedgerAlterId);
        command.Parameters.AddWithValue("$TallyLedgerVerifiedName", account.TallyLedgerVerifiedName.Trim());
        command.Parameters.AddWithValue("$Now", now);
    }

    private sealed record BankAccountRepairRow(BankAccountRecord Account, long TransactionCount, long StatementCount);

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string declaration)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, tableName, columnName, declaration);
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static void AddStatementParameters(SqliteCommand command, BankStatementRecord item)
    {
        command.Parameters.AddWithValue("$ClientId", item.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", item.FinancialYear.Trim());
        command.Parameters.AddWithValue("$BankAccountId", item.BankAccountId);
        command.Parameters.AddWithValue("$SourceFile", item.SourceFile.Trim());
        command.Parameters.AddWithValue("$SourcePath", item.SourcePath.Trim());
        command.Parameters.AddWithValue("$SourceSheet", item.SourceSheet.Trim());
        command.Parameters.AddWithValue("$FileHash", item.FileHash.Trim());
        command.Parameters.AddWithValue("$PeriodFrom", FormatDate(item.PeriodFrom));
        command.Parameters.AddWithValue("$PeriodTo", FormatDate(item.PeriodTo));
        command.Parameters.AddWithValue("$OpeningBalance", item.OpeningBalance.HasValue ? (object)(double)item.OpeningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$OpeningBalanceSource", item.OpeningBalanceSource.Trim());
        command.Parameters.AddWithValue("$SourceOpeningBalance", item.SourceOpeningBalance.HasValue ? (object)(double)item.SourceOpeningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$SourceClosingBalance", item.SourceClosingBalance.HasValue ? (object)(double)item.SourceClosingBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$ClosingBalanceSource", item.ClosingBalanceSource.Trim());
        command.Parameters.AddWithValue("$ClosingBalance", item.ClosingBalance.HasValue ? (object)(double)item.ClosingBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$TotalDebit", (double)item.TotalDebit);
        command.Parameters.AddWithValue("$TotalCredit", (double)item.TotalCredit);
        command.Parameters.AddWithValue("$TransactionCount", item.TransactionCount);
        command.Parameters.AddWithValue("$Status", item.ReconciliationStatus.Trim());
        command.Parameters.AddWithValue("$Difference", (double)item.Difference);
        command.Parameters.AddWithValue("$OpenSeconds", item.OpenSeconds);
        command.Parameters.AddWithValue("$ProcessSeconds", item.ProcessSeconds);
        command.Parameters.AddWithValue("$ImportedUtc", (item.ImportedUtc == default ? DateTime.UtcNow : item.ImportedUtc).ToString("O"));
    }

    private static void AddTransactionParameters(SqliteCommand command, BankTransactionRecord item, bool includeCoreKeys = true)
    {
        if (includeCoreKeys)
        {
            command.Parameters.AddWithValue("$ClientId", item.ClientId);
            command.Parameters.AddWithValue("$FinancialYear", item.FinancialYear.Trim());
            command.Parameters.AddWithValue("$StatementId", item.StatementId);
            command.Parameters.AddWithValue("$BankAccountId", item.BankAccountId);
            command.Parameters.AddWithValue("$TransactionId", item.TransactionId.Trim());
            command.Parameters.AddWithValue("$Fingerprint", item.Fingerprint.Trim());
            command.Parameters.AddWithValue("$SourceFile", item.SourceFile.Trim());
            command.Parameters.AddWithValue("$SourceSheet", item.SourceSheet.Trim());
            command.Parameters.AddWithValue("$SourceRow", item.SourceRow);
            command.Parameters.AddWithValue("$ImportedUtc", (item.ImportedUtc == default ? DateTime.UtcNow : item.ImportedUtc).ToString("O"));
        }
        command.Parameters.AddWithValue("$TransactionDate", item.TransactionDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$ValueDate", FormatDate(item.ValueDate));
        command.Parameters.AddWithValue("$Narration", item.Narration);
        command.Parameters.AddWithValue("$NormalizedNarration", item.NormalizedNarration);
        command.Parameters.AddWithValue("$ReferenceNumber", item.ReferenceNumber.Trim());
        command.Parameters.AddWithValue("$Debit", (double)item.Debit);
        command.Parameters.AddWithValue("$Credit", (double)item.Credit);
        command.Parameters.AddWithValue("$RunningBalance", item.RunningBalance.HasValue ? (object)(double)item.RunningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$RunningBalanceSource", item.RunningBalanceSource.Trim());
        command.Parameters.AddWithValue("$Mode", item.Mode.Trim());
        command.Parameters.AddWithValue("$LedgerName", item.LedgerName.Trim());
        command.Parameters.AddWithValue("$MappingStatus", item.MappingStatus.Trim());
        command.Parameters.AddWithValue("$RecordStatus", item.RecordStatus.Trim());
        command.Parameters.AddWithValue("$MatchId", item.MatchId.Trim());
        command.Parameters.AddWithValue("$MatchedTransactionId", item.MatchedTransactionId.HasValue ? (object)item.MatchedTransactionId.Value : DBNull.Value);
    }

    private static BankAccountRecord ReadAccount(SqliteDataReader reader) => new()
    {
        Id=reader.GetInt64(reader.GetOrdinal("Id")),
        ClientId=reader.GetInt64(reader.GetOrdinal("ClientId")),
        FinancialYear=ReadString(reader,"FinancialYear"),
        BankName=ReadString(reader,"BankName"),
        AccountNumber=ReadString(reader,"AccountNumber"),
        AccountType=ReadString(reader,"AccountType"),
        BranchName=ReadString(reader,"BranchName"),
        IfscCode=ReadString(reader,"IfscCode"),
        OdLimit=ReadNullableDecimal(reader,"OdLimit"),
        DrawingPower=ReadNullableDecimal(reader,"DrawingPower"),
        LedgerName=ReadString(reader,"LedgerName"),
        TallyLedgerMasterId=ReadString(reader,"TallyLedgerMasterId"),
        TallyLedgerGuid=ReadString(reader,"TallyLedgerGuid"),
        TallyLedgerAlterId=reader.IsDBNull(reader.GetOrdinal("TallyLedgerAlterId"))?0:Convert.ToInt64(reader.GetValue(reader.GetOrdinal("TallyLedgerAlterId")), CultureInfo.InvariantCulture),
        TallyLedgerVerifiedName=ReadString(reader,"TallyLedgerVerifiedName"),
        CreatedUtc=ReadDateTime(reader,reader.GetOrdinal("CreatedUtc")),
        UpdatedUtc=ReadDateTime(reader,reader.GetOrdinal("UpdatedUtc"))
    };

    private static BankStatementRecord ReadStatement(SqliteDataReader reader) => new()
    {
        Id=reader.GetInt64(reader.GetOrdinal("Id")),ClientId=reader.GetInt64(reader.GetOrdinal("ClientId")),FinancialYear=ReadString(reader,"FinancialYear"),
        BankAccountId=reader.GetInt64(reader.GetOrdinal("BankAccountId")),SourceFile=ReadString(reader,"SourceFile"),SourcePath=ReadString(reader,"SourcePath"),SourceSheet=ReadString(reader,"SourceSheet"),FileHash=ReadString(reader,"FileHash"),
        PeriodFrom=ReadNullableDate(reader,"PeriodFrom"),PeriodTo=ReadNullableDate(reader,"PeriodTo"),OpeningBalance=ReadNullableDecimal(reader,"OpeningBalance"),OpeningBalanceSource=ReadString(reader,"OpeningBalanceSource"),SourceOpeningBalance=ReadNullableDecimal(reader,"SourceOpeningBalance"),SourceClosingBalance=ReadNullableDecimal(reader,"SourceClosingBalance"),ClosingBalanceSource=ReadString(reader,"ClosingBalanceSource"),ClosingBalance=ReadNullableDecimal(reader,"ClosingBalance"),
        TotalDebit=ReadDecimal(reader,reader.GetOrdinal("TotalDebit")),TotalCredit=ReadDecimal(reader,reader.GetOrdinal("TotalCredit")),TransactionCount=reader.GetInt32(reader.GetOrdinal("TransactionCount")),ReconciliationStatus=ReadString(reader,"ReconciliationStatus"),Difference=ReadDecimal(reader,reader.GetOrdinal("Difference")),
        OpenSeconds=reader.IsDBNull(reader.GetOrdinal("OpenSeconds"))?0d:reader.GetDouble(reader.GetOrdinal("OpenSeconds")),ProcessSeconds=reader.IsDBNull(reader.GetOrdinal("ProcessSeconds"))?0d:reader.GetDouble(reader.GetOrdinal("ProcessSeconds")),ImportedUtc=ReadDateTime(reader,reader.GetOrdinal("ImportedUtc"))
    };

    private static BankTransactionRecord ReadTransaction(SqliteDataReader reader) => new()
    {
        Id=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),StatementId=reader.GetInt64(3),BankAccountId=reader.GetInt64(4),TransactionDate=ParseDate(reader.GetString(5))??DateTime.MinValue,ValueDate=ParseDate(reader.GetString(6)),Narration=reader.GetString(7),NormalizedNarration=reader.GetString(8),ReferenceNumber=reader.GetString(9),Debit=ReadDecimal(reader,10),Credit=ReadDecimal(reader,11),RunningBalance=reader.IsDBNull(12)?null:ReadDecimal(reader,12),RunningBalanceSource=reader.GetString(13),Mode=reader.GetString(14),LedgerName=reader.GetString(15),MappingStatus=reader.GetString(16),TransactionId=reader.GetString(17),Fingerprint=reader.GetString(18),RecordStatus=reader.GetString(19),MatchId=reader.GetString(20),MatchedTransactionId=reader.IsDBNull(21)?null:reader.GetInt64(21),SourceFile=reader.GetString(22),SourceSheet=reader.GetString(23),SourceRow=reader.GetInt32(24),ImportedUtc=ParseDateTime(reader.GetString(25))
    };

    private static string ReadString(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static DateTime? ReadNullableDate(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal)) return null;
        return ParseDate(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTime ReadDateTime(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return DateTime.MinValue;
        var value = reader.GetValue(ordinal);
        if (value is DateTime dateTime) return AppTime.NormalizeUtc(dateTime);
        return ParseDateTime(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static decimal ReadDecimal(SqliteDataReader reader, int ordinal) => Convert.ToDecimal(reader.GetDouble(ordinal), CultureInfo.InvariantCulture);
    private static DateTime? ParseDate(string text) => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value.Date : null;
    private static DateTime ParseDateTime(string text) => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? AppTime.NormalizeUtc(value) : DateTime.MinValue;
    private static string FormatDate(DateTime? value) => value?.ToString("yyyy-MM-dd") ?? string.Empty;
    private static string BuildPeriodLabel(DateTime? from, DateTime? to) => from.HasValue && to.HasValue ? $"{from:dd-MMM-yyyy} to {to:dd-MMM-yyyy}" : from?.ToString("dd-MMM-yyyy") ?? to?.ToString("dd-MMM-yyyy") ?? string.Empty;
}
