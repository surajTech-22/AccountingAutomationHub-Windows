using System.Text.Json;
using AccountingAutomationHub.Application.Gst;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Gst;

public sealed class SqliteGstRepository : IGstRepository
{
    private readonly string _connectionString;

    public SqliteGstRepository() : this(ApplicationDataPaths.DatabasePath) { }

    // Disposable isolated SQLite target for migration and multi-registration
    // regression tests. Production still uses the existing database path.
    public SqliteGstRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        ApplicationDataPaths.EnsureFolders();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteGstRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS Gstr3bReturns
                (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClientId        INTEGER NOT NULL,
                    FinancialYear   TEXT NOT NULL,
                    Gstin           TEXT NOT NULL DEFAULT '',
                    LegalName       TEXT NOT NULL DEFAULT '',
                    TradeName       TEXT NOT NULL DEFAULT '',
                    PeriodKey       TEXT NOT NULL,
                    PeriodLabel     TEXT NOT NULL DEFAULT '',
                    FilingFrequency TEXT NOT NULL DEFAULT 'Monthly',
                    Arn             TEXT NOT NULL DEFAULT '',
                    ArnDate         TEXT,
                    OutwardTaxable  REAL NOT NULL DEFAULT 0,
                    OutwardIgst     REAL NOT NULL DEFAULT 0,
                    OutwardCgst     REAL NOT NULL DEFAULT 0,
                    OutwardSgst     REAL NOT NULL DEFAULT 0,
                    OutwardCess     REAL NOT NULL DEFAULT 0,
                    RcmTaxable      REAL NOT NULL DEFAULT 0,
                    RcmIgst         REAL NOT NULL DEFAULT 0,
                    RcmCgst         REAL NOT NULL DEFAULT 0,
                    RcmSgst         REAL NOT NULL DEFAULT 0,
                    RcmCess         REAL NOT NULL DEFAULT 0,
                    NetItcIgst      REAL NOT NULL DEFAULT 0,
                    NetItcCgst      REAL NOT NULL DEFAULT 0,
                    NetItcSgst      REAL NOT NULL DEFAULT 0,
                    NetItcCess      REAL NOT NULL DEFAULT 0,
                    CompositionExemptNilInterState REAL NOT NULL DEFAULT 0,
                    CompositionExemptNilIntraState REAL NOT NULL DEFAULT 0,
                    NonGstInterState REAL NOT NULL DEFAULT 0,
                    NonGstIntraState REAL NOT NULL DEFAULT 0,
                    SystemInterestIgst REAL NOT NULL DEFAULT 0,
                    SystemInterestCgst REAL NOT NULL DEFAULT 0,
                    SystemInterestSgst REAL NOT NULL DEFAULT 0,
                    SystemInterestCess REAL NOT NULL DEFAULT 0,
                    InterestPaidIgst REAL NOT NULL DEFAULT 0,
                    InterestPaidCgst REAL NOT NULL DEFAULT 0,
                    InterestPaidSgst REAL NOT NULL DEFAULT 0,
                    InterestPaidCess REAL NOT NULL DEFAULT 0,
                    LateFeeIgst REAL NOT NULL DEFAULT 0,
                    LateFeeCgst REAL NOT NULL DEFAULT 0,
                    LateFeeSgst REAL NOT NULL DEFAULT 0,
                    LateFeeCess REAL NOT NULL DEFAULT 0,
                    PaymentRowsJson TEXT NOT NULL DEFAULT '[]',
                    LiabilityBreakupJson TEXT NOT NULL DEFAULT '[]',
                    RawFieldsJson TEXT NOT NULL DEFAULT '{}',
                    IsNilReturn     INTEGER NOT NULL DEFAULT 0,
                    ParseValid      INTEGER NOT NULL DEFAULT 0,
                    ParseNotes      TEXT NOT NULL DEFAULT '',
                    SourcePath      TEXT NOT NULL DEFAULT '',
                    RawText         TEXT NOT NULL DEFAULT '',
                    ImportedUtc     TEXT NOT NULL,
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS GstLedgerPeriods
                (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClientId        INTEGER NOT NULL,
                    FinancialYear   TEXT NOT NULL,
                    LedgerType      TEXT NOT NULL,
                    PeriodKey       TEXT NOT NULL,
                    PeriodLabel     TEXT NOT NULL DEFAULT '',
                    OpeningIgst     REAL NOT NULL DEFAULT 0,
                    OpeningCgst     REAL NOT NULL DEFAULT 0,
                    OpeningSgst     REAL NOT NULL DEFAULT 0,
                    OpeningCess     REAL NOT NULL DEFAULT 0,
                    ClosingIgst     REAL NOT NULL DEFAULT 0,
                    ClosingCgst     REAL NOT NULL DEFAULT 0,
                    ClosingSgst     REAL NOT NULL DEFAULT 0,
                    ClosingCess     REAL NOT NULL DEFAULT 0,
                    SourcePath      TEXT NOT NULL DEFAULT '',
                    ImportedUtc     TEXT NOT NULL,
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS GstPortalSourceState
                (
                    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClientId           INTEGER NOT NULL,
                    FinancialYear      TEXT NOT NULL,
                    Gstin              TEXT NOT NULL DEFAULT '',
                    SourceType         TEXT NOT NULL,
                    SourcePath         TEXT NOT NULL COLLATE NOCASE,
                    PeriodKey          TEXT NOT NULL DEFAULT '',
                    SourceSize         INTEGER NOT NULL DEFAULT 0,
                    SourceLastWriteUtc TEXT NOT NULL DEFAULT '',
                    MetadataFingerprint TEXT NOT NULL DEFAULT '',
                    ContentHash        TEXT NOT NULL DEFAULT '',
                    LastCheckedUtc     TEXT NOT NULL DEFAULT '',
                    LastImportedUtc    TEXT NOT NULL DEFAULT '',
                    LastEditedUtc      TEXT NOT NULL DEFAULT '',
                    Status             TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
                );

                CREATE UNIQUE INDEX IF NOT EXISTS UX_GstPortalSourceState_ClientFyTypePath
                ON GstPortalSourceState(ClientId, FinancialYear, SourceType, SourcePath);

                CREATE INDEX IF NOT EXISTS IX_GstPortalSourceState_ClientFyTypePeriod
                ON GstPortalSourceState(ClientId, FinancialYear, SourceType, PeriodKey);

                CREATE TABLE IF NOT EXISTS GstBootstrapState
                (
                    ClientId             INTEGER NOT NULL,
                    FinancialYear        TEXT NOT NULL,
                    Gstin                TEXT NOT NULL DEFAULT '',
                    DiscoveryFingerprint TEXT NOT NULL DEFAULT '',
                    LastCheckedUtc       TEXT NOT NULL DEFAULT '',
                    LastImportedUtc      TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (ClientId, FinancialYear, Gstin),
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS GstReconciliation
                (
                    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClientId            INTEGER NOT NULL,
                    FinancialYear       TEXT NOT NULL,
                    PeriodKey           TEXT NOT NULL,
                    PeriodLabel         TEXT NOT NULL DEFAULT '',
                    Status              TEXT NOT NULL DEFAULT '',
                    BooksOutput         REAL NOT NULL DEFAULT 0,
                    PortalOutput        REAL NOT NULL DEFAULT 0,
                    BooksItc            REAL NOT NULL DEFAULT 0,
                    PortalItc           REAL NOT NULL DEFAULT 0,
                    BooksNet            REAL NOT NULL DEFAULT 0,
                    PortalNet           REAL NOT NULL DEFAULT 0,
                    BooksClosing        REAL NOT NULL DEFAULT 0,
                    PortalOpening       REAL NOT NULL DEFAULT 0,
                    PortalClosing       REAL NOT NULL DEFAULT 0,
                    BooksRcmTaxable     REAL NOT NULL DEFAULT 0,
                    PortalRcmTaxable    REAL NOT NULL DEFAULT 0,
                    BooksRcmLiabilityGst REAL NOT NULL DEFAULT 0,
                    PortalRcmGst        REAL NOT NULL DEFAULT 0,
                    BooksNetWithOpening REAL NOT NULL DEFAULT 0,
                    PortalLedgerBalance REAL NOT NULL DEFAULT 0,
                    BooksPosition       TEXT NOT NULL DEFAULT '',
                    TaxPayable          REAL NOT NULL DEFAULT 0,
                    ItcUsed             REAL NOT NULL DEFAULT 0,
                    CashPaid            REAL NOT NULL DEFAULT 0,
                    Interest            REAL NOT NULL DEFAULT 0,
                    LateFee             REAL NOT NULL DEFAULT 0,
                    RcmCash             REAL NOT NULL DEFAULT 0,
                    OffsetTotal         REAL NOT NULL DEFAULT 0,
                    PaymentDifference   REAL NOT NULL DEFAULT 0,
                    HasJournal          INTEGER NOT NULL DEFAULT 0,
                    HasReturn           INTEGER NOT NULL DEFAULT 0,
                    HasCreditLedger     INTEGER NOT NULL DEFAULT 0,
                    HasCashLedger       INTEGER NOT NULL DEFAULT 0,
                    ReviewNotes         TEXT NOT NULL DEFAULT '',
                    RefreshedUtc        TEXT NOT NULL,
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
                );

                CREATE UNIQUE INDEX IF NOT EXISTS UX_GstReconciliation_ClientFyPeriod
                ON GstReconciliation(ClientId, FinancialYear, PeriodKey);
                """;
            command.ExecuteNonQuery();
        }

        EnsureReturnColumns(connection);
        EnsureReconciliationColumns(connection);
        EnsureRegistrationScopedKeys(connection);
    }

    private static void EnsureRegistrationScopedKeys(SqliteConnection connection)
    {
        // Re-index rather than rebuilding GST data tables, preserving all
        // existing records, source paths, and historical import timestamps.
        using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "PRAGMA table_info(GstLedgerPeriods);";
        var hasGstin = false;
        using (var reader = check.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(1).Equals("Gstin", StringComparison.OrdinalIgnoreCase)) hasGstin = true;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (!hasGstin)
        {
            command.CommandText = "ALTER TABLE GstLedgerPeriods ADD COLUMN Gstin TEXT NOT NULL DEFAULT '';";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            UPDATE GstLedgerPeriods SET Gstin=COALESCE(
                (SELECT Gstin FROM Clients WHERE Clients.Id=GstLedgerPeriods.ClientId),'')
            WHERE Gstin='';
            DROP INDEX IF EXISTS UX_Gstr3bReturns_ClientFyPeriod;
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Gstr3bReturns_ClientFyGstinPeriod
              ON Gstr3bReturns(ClientId,FinancialYear,Gstin,PeriodKey);
            DROP INDEX IF EXISTS UX_GstLedgerPeriods_ClientFyTypePeriod;
            CREATE UNIQUE INDEX IF NOT EXISTS UX_GstLedgerPeriods_ClientFyGstinTypePeriod
              ON GstLedgerPeriods(ClientId,FinancialYear,Gstin,LedgerType,PeriodKey);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public bool HasRecords(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                EXISTS(SELECT 1 FROM Gstr3bReturns
                    WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear LIMIT 1)
                OR
                EXISTS(SELECT 1 FROM GstLedgerPeriods
                    WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear LIMIT 1)
                OR
                EXISTS(SELECT 1 FROM GstReconciliation
                    WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear LIMIT 1);
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public GstPortalSourceState? GetPortalSourceState(long clientId, string financialYear, string sourceType, string sourcePath)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM GstPortalSourceState
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND SourceType=$SourceType AND SourcePath=$SourcePath
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$SourceType", sourceType.Trim());
        command.Parameters.AddWithValue("$SourcePath", Path.GetFullPath(sourcePath));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPortalSourceState(reader) : null;
    }

    public GstPortalSourceState? GetLatestPortalPeriodState(long clientId, string financialYear, string sourceType, string periodKey, string gstin = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM GstPortalSourceState
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND SourceType=$SourceType AND PeriodKey=$PeriodKey
              AND ($Gstin='' OR Gstin=$Gstin) AND Status='Imported'
            ORDER BY SourceLastWriteUtc DESC, LastImportedUtc DESC, Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$SourceType", sourceType.Trim());
        command.Parameters.AddWithValue("$PeriodKey", periodKey.Trim());
        command.Parameters.AddWithValue("$Gstin", gstin.Trim().ToUpperInvariant());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPortalSourceState(reader) : null;
    }

    public void UpsertPortalSourceState(GstPortalSourceState state)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GstPortalSourceState
            (ClientId, FinancialYear, Gstin, SourceType, SourcePath, PeriodKey,
             SourceSize, SourceLastWriteUtc, MetadataFingerprint, ContentHash,
             LastCheckedUtc, LastImportedUtc, LastEditedUtc, Status)
            VALUES
            ($ClientId, $FinancialYear, $Gstin, $SourceType, $SourcePath, $PeriodKey,
             $SourceSize, $SourceLastWriteUtc, $MetadataFingerprint, $ContentHash,
             $LastCheckedUtc, $LastImportedUtc, $LastEditedUtc, $Status)
            ON CONFLICT(ClientId, FinancialYear, SourceType, SourcePath)
            DO UPDATE SET
              Gstin=excluded.Gstin, PeriodKey=excluded.PeriodKey,
              SourceSize=excluded.SourceSize, SourceLastWriteUtc=excluded.SourceLastWriteUtc,
              MetadataFingerprint=excluded.MetadataFingerprint,
              ContentHash=CASE WHEN excluded.ContentHash<>'' THEN excluded.ContentHash ELSE GstPortalSourceState.ContentHash END,
              LastCheckedUtc=excluded.LastCheckedUtc,
              LastImportedUtc=CASE WHEN excluded.LastImportedUtc<>'' THEN excluded.LastImportedUtc ELSE GstPortalSourceState.LastImportedUtc END,
              LastEditedUtc=CASE WHEN excluded.LastEditedUtc<>'' THEN excluded.LastEditedUtc ELSE GstPortalSourceState.LastEditedUtc END,
              Status=excluded.Status;
            """;
        command.Parameters.AddWithValue("$ClientId", state.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", state.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Gstin", state.Gstin.Trim());
        command.Parameters.AddWithValue("$SourceType", state.SourceType.Trim());
        command.Parameters.AddWithValue("$SourcePath", Path.GetFullPath(state.SourcePath));
        command.Parameters.AddWithValue("$PeriodKey", state.PeriodKey.Trim());
        command.Parameters.AddWithValue("$SourceSize", state.SourceSize);
        command.Parameters.AddWithValue("$SourceLastWriteUtc", FormatDate(state.SourceLastWriteUtc));
        command.Parameters.AddWithValue("$MetadataFingerprint", state.MetadataFingerprint.Trim());
        command.Parameters.AddWithValue("$ContentHash", state.ContentHash.Trim());
        command.Parameters.AddWithValue("$LastCheckedUtc", FormatDate(state.LastCheckedUtc));
        command.Parameters.AddWithValue("$LastImportedUtc", FormatDate(state.LastImportedUtc));
        command.Parameters.AddWithValue("$LastEditedUtc", FormatDate(state.LastEditedUtc));
        command.Parameters.AddWithValue("$Status", state.Status.Trim());
        command.ExecuteNonQuery();
    }

    public GstBootstrapState? GetBootstrapState(long clientId, string financialYear, string gstin)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM GstBootstrapState
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Gstin=$Gstin LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$Gstin", gstin.Trim().ToUpperInvariant());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new GstBootstrapState(
            ReadInt64(reader, "ClientId"), ReadString(reader, "FinancialYear"),
            ReadString(reader, "Gstin"), ReadString(reader, "DiscoveryFingerprint"),
            ParseDate(ReadString(reader, "LastCheckedUtc")), ParseDate(ReadString(reader, "LastImportedUtc")));
    }

    public void UpsertBootstrapState(GstBootstrapState state)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GstBootstrapState
            (ClientId, FinancialYear, Gstin, DiscoveryFingerprint, LastCheckedUtc, LastImportedUtc)
            VALUES ($ClientId, $FinancialYear, $Gstin, $DiscoveryFingerprint, $LastCheckedUtc, $LastImportedUtc)
            ON CONFLICT(ClientId, FinancialYear, Gstin)
            DO UPDATE SET DiscoveryFingerprint=excluded.DiscoveryFingerprint,
                          LastCheckedUtc=excluded.LastCheckedUtc,
                          LastImportedUtc=CASE WHEN excluded.LastImportedUtc<>'' THEN excluded.LastImportedUtc ELSE GstBootstrapState.LastImportedUtc END;
            """;
        command.Parameters.AddWithValue("$ClientId", state.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", state.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Gstin", state.Gstin.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$DiscoveryFingerprint", state.DiscoveryFingerprint.Trim());
        command.Parameters.AddWithValue("$LastCheckedUtc", FormatDate(state.LastCheckedUtc));
        command.Parameters.AddWithValue("$LastImportedUtc", FormatDate(state.LastImportedUtc));
        command.ExecuteNonQuery();
    }

    public DateTime? UpdateReturnPeriod(long recordId, string periodKey, string periodLabel, string filingFrequency)
    {
        periodKey = periodKey.Trim();
        periodLabel = periodLabel.Trim();
        filingFrequency = filingFrequency.Trim();
        using var connection = OpenConnection();

        long clientId;
        string financialYear, currentGstin, sourcePath, currentKey, currentLabel, currentFrequency;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT ClientId, FinancialYear, Gstin, SourcePath, PeriodKey, PeriodLabel, FilingFrequency
                FROM Gstr3bReturns WHERE Id=$Id LIMIT 1;
                """;
            read.Parameters.AddWithValue("$Id", recordId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The selected GSTR-3B period no longer exists.");
            clientId = reader.GetInt64(0);
            financialYear = reader.GetString(1);
            currentGstin = reader.GetString(2);
            sourcePath = reader.GetString(3);
            currentKey = reader.GetString(4);
            currentLabel = reader.GetString(5);
            currentFrequency = reader.GetString(6);
        }

        if (currentKey.Equals(periodKey, StringComparison.OrdinalIgnoreCase) &&
            currentLabel.Equals(periodLabel, StringComparison.Ordinal) &&
            currentFrequency.Equals(filingFrequency, StringComparison.OrdinalIgnoreCase))
            return null;

        using (var conflict = connection.CreateCommand())
        {
            conflict.CommandText = """
                SELECT COUNT(*) FROM Gstr3bReturns
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND Gstin=$Gstin AND PeriodKey=$PeriodKey AND Id<>$Id;
                """;
            conflict.Parameters.AddWithValue("$ClientId", clientId);
            conflict.Parameters.AddWithValue("$FinancialYear", financialYear);
            conflict.Parameters.AddWithValue("$Gstin", currentGstin);
            conflict.Parameters.AddWithValue("$PeriodKey", periodKey);
            conflict.Parameters.AddWithValue("$Id", recordId);
            if (Convert.ToInt32(conflict.ExecuteScalar()) > 0)
                throw new InvalidOperationException($"Another GSTR-3B record already uses period {periodKey}.");
        }

        var editedUtc = DateTime.UtcNow;
        using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE Gstr3bReturns
                SET PeriodKey=$PeriodKey, PeriodLabel=$PeriodLabel,
                    FilingFrequency=$FilingFrequency, LastEditedUtc=$LastEditedUtc
                WHERE Id=$Id;
                """;
            update.Parameters.AddWithValue("$PeriodKey", periodKey);
            update.Parameters.AddWithValue("$PeriodLabel", periodLabel);
            update.Parameters.AddWithValue("$FilingFrequency", filingFrequency);
            update.Parameters.AddWithValue("$LastEditedUtc", editedUtc.ToString("O"));
            update.Parameters.AddWithValue("$Id", recordId);
            update.ExecuteNonQuery();
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            foreach (var source in sourcePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string normalizedSource;
                try { normalizedSource = Path.GetFullPath(source); }
                catch { continue; }

                using var stateUpdate = connection.CreateCommand();
                stateUpdate.CommandText = """
                    UPDATE GstPortalSourceState
                    SET PeriodKey=$PeriodKey, LastEditedUtc=$LastEditedUtc
                    WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                      AND SourceType='GSTR-3B' AND SourcePath=$SourcePath;
                    """;
                stateUpdate.Parameters.AddWithValue("$PeriodKey", periodKey);
                stateUpdate.Parameters.AddWithValue("$LastEditedUtc", editedUtc.ToString("O"));
                stateUpdate.Parameters.AddWithValue("$ClientId", clientId);
                stateUpdate.Parameters.AddWithValue("$FinancialYear", financialYear);
                stateUpdate.Parameters.AddWithValue("$SourcePath", normalizedSource);
                stateUpdate.ExecuteNonQuery();
            }
        }
        return editedUtc;
    }

    public void ReplaceReturn(Gstr3bReturnRecord record)
    {
        using var connection = OpenConnection();

        // Preserve the audit timestamp when a newer/different source version replaces
        // a period that the user had manually corrected earlier.
        if (!record.LastEditedUtc.HasValue)
        {
            using var lastEdit = connection.CreateCommand();
            lastEdit.CommandText =
                """
                SELECT LastEditedUtc
                FROM Gstr3bReturns
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND Gstin=$Gstin AND LastEditedUtc<>''
                  AND (PeriodKey=$PeriodKey OR (SourcePath<>'' AND SourcePath=$SourcePath) OR ($Arn<>'' AND Arn=$Arn))
                ORDER BY ImportedUtc DESC
                LIMIT 1;
                """;
            lastEdit.Parameters.AddWithValue("$ClientId", record.ClientId);
            lastEdit.Parameters.AddWithValue("$FinancialYear", record.FinancialYear.Trim());
            lastEdit.Parameters.AddWithValue("$Gstin", record.Gstin.Trim().ToUpperInvariant());
            lastEdit.Parameters.AddWithValue("$PeriodKey", record.PeriodKey.Trim());
            lastEdit.Parameters.AddWithValue("$SourcePath", record.SourcePath?.Trim() ?? string.Empty);
            lastEdit.Parameters.AddWithValue("$Arn", record.Arn?.Trim() ?? string.Empty);
            var value = Convert.ToString(lastEdit.ExecuteScalar());
            if (DateTime.TryParse(value, out var parsedLastEdit))
                record.LastEditedUtc = parsedLastEdit;
        }

        // A filed GSTR-3B source/ARN owns one return period. Remove an older
        // misclassified row for that same evidence before inserting the corrected
        // identity (for example a historical 202709 tax-value false period).
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText =
                """
                DELETE FROM Gstr3bReturns
                WHERE ClientId=$ClientId
                  AND FinancialYear=$FinancialYear AND Gstin=$Gstin
                  AND PeriodKey<>$PeriodKey
                  AND (
                        (SourcePath<>'' AND SourcePath=$SourcePath)
                        OR ($Arn<>'' AND Arn=$Arn)
                      );
                """;
            cleanup.Parameters.AddWithValue("$ClientId", record.ClientId);
            cleanup.Parameters.AddWithValue("$FinancialYear", record.FinancialYear.Trim());
            cleanup.Parameters.AddWithValue("$Gstin", record.Gstin.Trim().ToUpperInvariant());
            cleanup.Parameters.AddWithValue("$PeriodKey", record.PeriodKey.Trim());
            cleanup.Parameters.AddWithValue("$SourcePath", record.SourcePath?.Trim() ?? string.Empty);
            cleanup.Parameters.AddWithValue("$Arn", record.Arn?.Trim() ?? string.Empty);
            cleanup.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Gstr3bReturns
            (
                ClientId, FinancialYear, Gstin, LegalName, TradeName, PeriodKey,
                PeriodLabel, FilingFrequency, Arn, ArnDate,
                OutwardTaxable, OutwardIgst, OutwardCgst, OutwardSgst, OutwardCess,
                RcmTaxable, RcmIgst, RcmCgst, RcmSgst, RcmCess,
                NetItcIgst, NetItcCgst, NetItcSgst, NetItcCess,
                CompositionExemptNilInterState, CompositionExemptNilIntraState,
                NonGstInterState, NonGstIntraState,
                SystemInterestIgst, SystemInterestCgst, SystemInterestSgst, SystemInterestCess,
                InterestPaidIgst, InterestPaidCgst, InterestPaidSgst, InterestPaidCess,
                LateFeeIgst, LateFeeCgst, LateFeeSgst, LateFeeCess,
                PaymentRowsJson, LiabilityBreakupJson, RawFieldsJson,
                IsNilReturn, ParseValid, ParseNotes, SourcePath, RawText, ImportedUtc, LastEditedUtc
            )
            VALUES
            (
                $ClientId, $FinancialYear, $Gstin, $LegalName, $TradeName, $PeriodKey,
                $PeriodLabel, $FilingFrequency, $Arn, $ArnDate,
                $OutwardTaxable, $OutwardIgst, $OutwardCgst, $OutwardSgst, $OutwardCess,
                $RcmTaxable, $RcmIgst, $RcmCgst, $RcmSgst, $RcmCess,
                $NetItcIgst, $NetItcCgst, $NetItcSgst, $NetItcCess,
                $CompositionExemptNilInterState, $CompositionExemptNilIntraState,
                $NonGstInterState, $NonGstIntraState,
                $SystemInterestIgst, $SystemInterestCgst, $SystemInterestSgst, $SystemInterestCess,
                $InterestPaidIgst, $InterestPaidCgst, $InterestPaidSgst, $InterestPaidCess,
                $LateFeeIgst, $LateFeeCgst, $LateFeeSgst, $LateFeeCess,
                $PaymentRowsJson, $LiabilityBreakupJson, $RawFieldsJson,
                $IsNilReturn, $ParseValid, $ParseNotes, $SourcePath, $RawText, $ImportedUtc, $LastEditedUtc
            )
            ON CONFLICT(ClientId, FinancialYear, Gstin, PeriodKey)
            DO UPDATE SET
                Gstin = excluded.Gstin,
                LegalName = excluded.LegalName,
                TradeName = excluded.TradeName,
                PeriodLabel = excluded.PeriodLabel,
                FilingFrequency = excluded.FilingFrequency,
                Arn = excluded.Arn,
                ArnDate = excluded.ArnDate,
                OutwardTaxable = excluded.OutwardTaxable,
                OutwardIgst = excluded.OutwardIgst,
                OutwardCgst = excluded.OutwardCgst,
                OutwardSgst = excluded.OutwardSgst,
                OutwardCess = excluded.OutwardCess,
                RcmTaxable = excluded.RcmTaxable,
                RcmIgst = excluded.RcmIgst,
                RcmCgst = excluded.RcmCgst,
                RcmSgst = excluded.RcmSgst,
                RcmCess = excluded.RcmCess,
                NetItcIgst = excluded.NetItcIgst,
                NetItcCgst = excluded.NetItcCgst,
                NetItcSgst = excluded.NetItcSgst,
                NetItcCess = excluded.NetItcCess,
                CompositionExemptNilInterState = excluded.CompositionExemptNilInterState,
                CompositionExemptNilIntraState = excluded.CompositionExemptNilIntraState,
                NonGstInterState = excluded.NonGstInterState,
                NonGstIntraState = excluded.NonGstIntraState,
                SystemInterestIgst = excluded.SystemInterestIgst,
                SystemInterestCgst = excluded.SystemInterestCgst,
                SystemInterestSgst = excluded.SystemInterestSgst,
                SystemInterestCess = excluded.SystemInterestCess,
                InterestPaidIgst = excluded.InterestPaidIgst,
                InterestPaidCgst = excluded.InterestPaidCgst,
                InterestPaidSgst = excluded.InterestPaidSgst,
                InterestPaidCess = excluded.InterestPaidCess,
                LateFeeIgst = excluded.LateFeeIgst,
                LateFeeCgst = excluded.LateFeeCgst,
                LateFeeSgst = excluded.LateFeeSgst,
                LateFeeCess = excluded.LateFeeCess,
                PaymentRowsJson = excluded.PaymentRowsJson,
                LiabilityBreakupJson = excluded.LiabilityBreakupJson,
                RawFieldsJson = excluded.RawFieldsJson,
                IsNilReturn = excluded.IsNilReturn,
                ParseValid = excluded.ParseValid,
                ParseNotes = excluded.ParseNotes,
                SourcePath = excluded.SourcePath,
                RawText = excluded.RawText,
                ImportedUtc = excluded.ImportedUtc,
                LastEditedUtc = CASE
                    WHEN excluded.LastEditedUtc<>'' THEN excluded.LastEditedUtc
                    ELSE Gstr3bReturns.LastEditedUtc
                END;
            """;
        AddReturnParameters(command, record);
        command.ExecuteNonQuery();
    }

    public void ReplaceLedgerPeriods(
        long clientId, string financialYear, string ledgerType,
        IEnumerable<GstLedgerPeriodBalance> periods)
    {
        // Preserve the existing four-parameter IGstRepository contract for
        // all old consumers. Legacy single-GSTIN calls resolve their normal
        // primary registration; multi-GSTIN flows use the explicit overload.
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Gstin FROM Clients WHERE Id=$ClientId LIMIT 1;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        var primaryGstin = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        foreach (var row in periods)
            row.Gstin = primaryGstin.Trim().ToUpperInvariant();
        ReplaceLedgerPeriods(clientId, financialYear, ledgerType, periods, primaryGstin);
    }

    public void ReplaceLedgerPeriods(
        long clientId,
        string financialYear,
        string ledgerType,
        IEnumerable<GstLedgerPeriodBalance> periods,
        string gstin)
    {
        gstin = gstin.Trim().ToUpperInvariant();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM GstLedgerPeriods
                WHERE ClientId = $ClientId
                  AND FinancialYear = $FinancialYear
                  AND LedgerType = $LedgerType AND Gstin=$Gstin;
                """;
            delete.Parameters.AddWithValue("$Gstin", gstin);
            delete.Parameters.AddWithValue("$ClientId", clientId);
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            delete.Parameters.AddWithValue("$LedgerType", ledgerType.Trim());
            delete.ExecuteNonQuery();
        }

        foreach (var period in periods)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO GstLedgerPeriods
                (
                    ClientId, FinancialYear, Gstin, LedgerType, PeriodKey, PeriodLabel,
                    OpeningIgst, OpeningCgst, OpeningSgst, OpeningCess,
                    ClosingIgst, ClosingCgst, ClosingSgst, ClosingCess,
                    SourcePath, ImportedUtc
                )
                VALUES
                (
                    $ClientId, $FinancialYear, $Gstin, $LedgerType, $PeriodKey, $PeriodLabel,
                    $OpeningIgst, $OpeningCgst, $OpeningSgst, $OpeningCess,
                    $ClosingIgst, $ClosingCgst, $ClosingSgst, $ClosingCess,
                    $SourcePath, $ImportedUtc
                );
                """;
            AddLedgerParameters(insert, period);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<Gstr3bReturnRecord> GetReturns(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT *
            FROM Gstr3bReturns
            WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear
            ORDER BY PeriodKey, ImportedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var rows = new List<Gstr3bReturnRecord>();
        while (reader.Read())
        {
            rows.Add(ReadReturn(reader));
        }
        return rows;
    }

    public IReadOnlyList<GstLedgerPeriodBalance> GetLedgerPeriods(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT *
            FROM GstLedgerPeriods
            WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear
            ORDER BY PeriodKey, LedgerType;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var rows = new List<GstLedgerPeriodBalance>();
        while (reader.Read())
        {
            rows.Add(ReadLedger(reader));
        }
        return rows;
    }

    public void ReplaceReconciliation(
        long clientId,
        string financialYear,
        IEnumerable<GstReconciliationRow> rows)
    {
        var fy = financialYear.Trim();
        var incoming = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.PeriodKey))
            .GroupBy(row => row.PeriodKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        // GSTR-3B reconciliation is period-based. Keep unchanged periods in-place so
        // deleting/editing one Purchase GST-Date row cannot recreate every period row.
        var existing = GetReconciliation(clientId, fy)
            .GroupBy(row => row.PeriodKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var incomingPeriods = incoming
            .Select(row => row.PeriodKey.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var row in incoming)
        {
            row.ClientId = clientId;
            row.FinancialYear = fy;
            row.PeriodKey = row.PeriodKey.Trim();

            if (existing.TryGetValue(row.PeriodKey, out var current) &&
                ReconciliationEquivalent(current, row))
            {
                row.PersistentId = current.PersistentId;
                row.RefreshedUtc = current.RefreshedUtc;
                continue;
            }

            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO GstReconciliation
                (
                    ClientId, FinancialYear, PeriodKey, PeriodLabel, Status,
                    BooksOutput, PortalOutput, BooksItc, PortalItc,
                    BooksNet, PortalNet, BooksClosing, PortalOpening, PortalClosing,
                    BooksRcmTaxable, PortalRcmTaxable,
                    BooksRcmLiabilityGst, PortalRcmGst,
                    BooksNetWithOpening, PortalLedgerBalance, BooksPosition,
                    TaxPayable, ItcUsed, CashPaid, Interest, LateFee, RcmCash,
                    OffsetTotal, PaymentDifference,
                    HasJournal, HasReturn, HasCreditLedger, HasCashLedger,
                    ReviewNotes, RefreshedUtc
                )
                VALUES
                (
                    $ClientId, $FinancialYear, $PeriodKey, $PeriodLabel, $Status,
                    $BooksOutput, $PortalOutput, $BooksItc, $PortalItc,
                    $BooksNet, $PortalNet, $BooksClosing, $PortalOpening, $PortalClosing,
                    $BooksRcmTaxable, $PortalRcmTaxable,
                    $BooksRcmLiabilityGst, $PortalRcmGst,
                    $BooksNetWithOpening, $PortalLedgerBalance, $BooksPosition,
                    $TaxPayable, $ItcUsed, $CashPaid, $Interest, $LateFee, $RcmCash,
                    $OffsetTotal, $PaymentDifference,
                    $HasJournal, $HasReturn, $HasCreditLedger, $HasCashLedger,
                    $ReviewNotes, $RefreshedUtc
                )
                ON CONFLICT(ClientId, FinancialYear, PeriodKey)
                DO UPDATE SET
                    PeriodLabel = excluded.PeriodLabel,
                    Status = excluded.Status,
                    BooksOutput = excluded.BooksOutput,
                    PortalOutput = excluded.PortalOutput,
                    BooksItc = excluded.BooksItc,
                    PortalItc = excluded.PortalItc,
                    BooksNet = excluded.BooksNet,
                    PortalNet = excluded.PortalNet,
                    BooksClosing = excluded.BooksClosing,
                    PortalOpening = excluded.PortalOpening,
                    PortalClosing = excluded.PortalClosing,
                    BooksRcmTaxable = excluded.BooksRcmTaxable,
                    PortalRcmTaxable = excluded.PortalRcmTaxable,
                    BooksRcmLiabilityGst = excluded.BooksRcmLiabilityGst,
                    PortalRcmGst = excluded.PortalRcmGst,
                    BooksNetWithOpening = excluded.BooksNetWithOpening,
                    PortalLedgerBalance = excluded.PortalLedgerBalance,
                    BooksPosition = excluded.BooksPosition,
                    TaxPayable = excluded.TaxPayable,
                    ItcUsed = excluded.ItcUsed,
                    CashPaid = excluded.CashPaid,
                    Interest = excluded.Interest,
                    LateFee = excluded.LateFee,
                    RcmCash = excluded.RcmCash,
                    OffsetTotal = excluded.OffsetTotal,
                    PaymentDifference = excluded.PaymentDifference,
                    HasJournal = excluded.HasJournal,
                    HasReturn = excluded.HasReturn,
                    HasCreditLedger = excluded.HasCreditLedger,
                    HasCashLedger = excluded.HasCashLedger,
                    ReviewNotes = excluded.ReviewNotes,
                    RefreshedUtc = excluded.RefreshedUtc;
                """;
            AddReconciliationParameters(upsert, row);
            upsert.ExecuteNonQuery();
        }

        foreach (var current in existing.Values.Where(row => !incomingPeriods.Contains(row.PeriodKey)))
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM GstReconciliation WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear;";
            delete.Parameters.AddWithValue("$Id", current.PersistentId);
            delete.Parameters.AddWithValue("$ClientId", clientId);
            delete.Parameters.AddWithValue("$FinancialYear", fy);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static bool ReconciliationEquivalent(GstReconciliationRow left, GstReconciliationRow right) =>
        string.Equals(left.PeriodLabel.Trim(), right.PeriodLabel.Trim(), StringComparison.Ordinal) &&
        string.Equals(left.Status.Trim(), right.Status.Trim(), StringComparison.Ordinal) &&
        left.BooksOutput == right.BooksOutput && left.PortalOutput == right.PortalOutput &&
        left.BooksItc == right.BooksItc && left.PortalItc == right.PortalItc &&
        left.PortalNet == right.PortalNet && left.BooksNetWithOpening == right.BooksNetWithOpening &&
        left.PortalOpening == right.PortalOpening && left.PortalLedgerBalance == right.PortalLedgerBalance &&
        left.BooksRcmTaxable == right.BooksRcmTaxable && left.PortalRcmTaxable == right.PortalRcmTaxable &&
        left.BooksRcmLiabilityGst == right.BooksRcmLiabilityGst && left.PortalRcmGst == right.PortalRcmGst &&
        string.Equals(left.BooksPosition.Trim(), right.BooksPosition.Trim(), StringComparison.Ordinal) &&
        left.TaxPayable == right.TaxPayable && left.ItcUsed == right.ItcUsed && left.CashPaid == right.CashPaid &&
        left.Interest == right.Interest && left.LateFee == right.LateFee && left.RcmCash == right.RcmCash &&
        left.OffsetTotal == right.OffsetTotal && left.PaymentDifference == right.PaymentDifference &&
        left.HasJournal == right.HasJournal && left.HasReturn == right.HasReturn &&
        left.HasCreditLedger == right.HasCreditLedger && left.HasCashLedger == right.HasCashLedger &&
        string.Equals(left.ReviewNotes.Trim(), right.ReviewNotes.Trim(), StringComparison.Ordinal);

    public IReadOnlyList<GstReconciliationRow> GetReconciliation(
        long clientId,
        string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT *
            FROM GstReconciliation
            WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear
            ORDER BY PeriodKey;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var rows = new List<GstReconciliationRow>();
        while (reader.Read())
        {
            rows.Add(new GstReconciliationRow
            {
                PersistentId = ReadInt64(reader, "Id"),
                ClientId = ReadInt64(reader, "ClientId"),
                FinancialYear = ReadString(reader, "FinancialYear"),
                PeriodKey = ReadString(reader, "PeriodKey"),
                PeriodLabel = ReadString(reader, "PeriodLabel"),
                Status = ReadString(reader, "Status"),
                BooksOutput = ReadDecimal(reader, "BooksOutput"),
                PortalOutput = ReadDecimal(reader, "PortalOutput"),
                BooksItc = ReadDecimal(reader, "BooksItc"),
                PortalItc = ReadDecimal(reader, "PortalItc"),
                PortalNet = ReadDecimal(reader, "PortalNet"),
                BooksRcmTaxable = ReadDecimal(reader, "BooksRcmTaxable"),
                PortalRcmTaxable = ReadDecimal(reader, "PortalRcmTaxable"),
                BooksRcmLiabilityGst = ReadDecimal(reader, "BooksRcmLiabilityGst"),
                PortalRcmGst = ReadDecimal(reader, "PortalRcmGst"),
                BooksNetWithOpening = ReadDecimal(reader, "BooksNetWithOpening"),
                PortalOpening = ReadDecimal(reader, "PortalOpening"),
                PortalLedgerBalance = ReadDecimal(reader, "PortalLedgerBalance"),
                BooksPosition = ReadString(reader, "BooksPosition"),
                TaxPayable = ReadDecimal(reader, "TaxPayable"),
                ItcUsed = ReadDecimal(reader, "ItcUsed"),
                CashPaid = ReadDecimal(reader, "CashPaid"),
                Interest = ReadDecimal(reader, "Interest"),
                LateFee = ReadDecimal(reader, "LateFee"),
                RcmCash = ReadDecimal(reader, "RcmCash"),
                OffsetTotal = ReadDecimal(reader, "OffsetTotal"),
                PaymentDifference = ReadDecimal(reader, "PaymentDifference"),
                HasJournal = ReadInt64(reader, "HasJournal") == 1,
                HasReturn = ReadInt64(reader, "HasReturn") == 1,
                HasCreditLedger = ReadInt64(reader, "HasCreditLedger") == 1,
                HasCashLedger = ReadInt64(reader, "HasCashLedger") == 1,
                ReviewNotes = ReadString(reader, "ReviewNotes"),
                RefreshedUtc = ParseDate(ReadString(reader, "RefreshedUtc"))
            });
        }
        return rows;
    }

    private static void EnsureReturnColumns(SqliteConnection connection)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CompositionExemptNilInterState"] = "REAL NOT NULL DEFAULT 0",
            ["CompositionExemptNilIntraState"] = "REAL NOT NULL DEFAULT 0",
            ["NonGstInterState"] = "REAL NOT NULL DEFAULT 0",
            ["NonGstIntraState"] = "REAL NOT NULL DEFAULT 0",
            ["SystemInterestIgst"] = "REAL NOT NULL DEFAULT 0",
            ["SystemInterestCgst"] = "REAL NOT NULL DEFAULT 0",
            ["SystemInterestSgst"] = "REAL NOT NULL DEFAULT 0",
            ["SystemInterestCess"] = "REAL NOT NULL DEFAULT 0",
            ["InterestPaidIgst"] = "REAL NOT NULL DEFAULT 0",
            ["InterestPaidCgst"] = "REAL NOT NULL DEFAULT 0",
            ["InterestPaidSgst"] = "REAL NOT NULL DEFAULT 0",
            ["InterestPaidCess"] = "REAL NOT NULL DEFAULT 0",
            ["LateFeeIgst"] = "REAL NOT NULL DEFAULT 0",
            ["LateFeeCgst"] = "REAL NOT NULL DEFAULT 0",
            ["LateFeeSgst"] = "REAL NOT NULL DEFAULT 0",
            ["LateFeeCess"] = "REAL NOT NULL DEFAULT 0",
            ["PaymentRowsJson"] = "TEXT NOT NULL DEFAULT '[]'",
            ["LiabilityBreakupJson"] = "TEXT NOT NULL DEFAULT '[]'",
            ["RawFieldsJson"] = "TEXT NOT NULL DEFAULT '{}'",
            ["RawText"] = "TEXT NOT NULL DEFAULT ''",
            ["LastEditedUtc"] = "TEXT NOT NULL DEFAULT ''"
        };
        foreach (var column in columns)
        {
            EnsureColumn(connection, "Gstr3bReturns", column.Key, column.Value);
        }
    }

    private static void EnsureReconciliationColumns(SqliteConnection connection)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BooksRcmTaxable"] = "REAL NOT NULL DEFAULT 0",
            ["PortalRcmTaxable"] = "REAL NOT NULL DEFAULT 0",
            ["BooksRcmLiabilityGst"] = "REAL NOT NULL DEFAULT 0",
            ["PortalRcmGst"] = "REAL NOT NULL DEFAULT 0",
            ["BooksNetWithOpening"] = "REAL NOT NULL DEFAULT 0",
            ["PortalLedgerBalance"] = "REAL NOT NULL DEFAULT 0",
            ["BooksPosition"] = "TEXT NOT NULL DEFAULT ''",
            ["TaxPayable"] = "REAL NOT NULL DEFAULT 0",
            ["ItcUsed"] = "REAL NOT NULL DEFAULT 0",
            ["CashPaid"] = "REAL NOT NULL DEFAULT 0",
            ["Interest"] = "REAL NOT NULL DEFAULT 0",
            ["LateFee"] = "REAL NOT NULL DEFAULT 0",
            ["RcmCash"] = "REAL NOT NULL DEFAULT 0",
            ["OffsetTotal"] = "REAL NOT NULL DEFAULT 0",
            ["PaymentDifference"] = "REAL NOT NULL DEFAULT 0"
        };
        foreach (var column in columns)
        {
            EnsureColumn(connection, "GstReconciliation", column.Key, column.Value);
        }
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, tableName, columnName, definition);
    }

    private static void AddReturnParameters(SqliteCommand command, Gstr3bReturnRecord row)
    {
        command.Parameters.AddWithValue("$ClientId", row.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", row.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Gstin", row.Gstin.Trim());
        command.Parameters.AddWithValue("$LegalName", row.LegalName.Trim());
        command.Parameters.AddWithValue("$TradeName", row.TradeName.Trim());
        command.Parameters.AddWithValue("$PeriodKey", row.PeriodKey.Trim());
        command.Parameters.AddWithValue("$PeriodLabel", row.PeriodLabel.Trim());
        command.Parameters.AddWithValue("$FilingFrequency", row.FilingFrequency.Trim());
        command.Parameters.AddWithValue("$Arn", row.Arn.Trim());
        object arnDateValue = row.ArnDate.HasValue
            ? row.ArnDate.Value.ToString("yyyy-MM-dd")
            : DBNull.Value;
        command.Parameters.AddWithValue("$ArnDate", arnDateValue);
        AddDecimal(command, "$OutwardTaxable", row.OutwardTaxable);
        AddDecimal(command, "$OutwardIgst", row.OutwardIgst);
        AddDecimal(command, "$OutwardCgst", row.OutwardCgst);
        AddDecimal(command, "$OutwardSgst", row.OutwardSgst);
        AddDecimal(command, "$OutwardCess", row.OutwardCess);
        AddDecimal(command, "$RcmTaxable", row.RcmTaxable);
        AddDecimal(command, "$RcmIgst", row.RcmIgst);
        AddDecimal(command, "$RcmCgst", row.RcmCgst);
        AddDecimal(command, "$RcmSgst", row.RcmSgst);
        AddDecimal(command, "$RcmCess", row.RcmCess);
        AddDecimal(command, "$NetItcIgst", row.NetItcIgst);
        AddDecimal(command, "$NetItcCgst", row.NetItcCgst);
        AddDecimal(command, "$NetItcSgst", row.NetItcSgst);
        AddDecimal(command, "$NetItcCess", row.NetItcCess);
        AddDecimal(command, "$CompositionExemptNilInterState", row.CompositionExemptNilInterState);
        AddDecimal(command, "$CompositionExemptNilIntraState", row.CompositionExemptNilIntraState);
        AddDecimal(command, "$NonGstInterState", row.NonGstInterState);
        AddDecimal(command, "$NonGstIntraState", row.NonGstIntraState);
        AddDecimal(command, "$SystemInterestIgst", row.SystemInterestIgst);
        AddDecimal(command, "$SystemInterestCgst", row.SystemInterestCgst);
        AddDecimal(command, "$SystemInterestSgst", row.SystemInterestSgst);
        AddDecimal(command, "$SystemInterestCess", row.SystemInterestCess);
        AddDecimal(command, "$InterestPaidIgst", row.InterestPaidIgst);
        AddDecimal(command, "$InterestPaidCgst", row.InterestPaidCgst);
        AddDecimal(command, "$InterestPaidSgst", row.InterestPaidSgst);
        AddDecimal(command, "$InterestPaidCess", row.InterestPaidCess);
        AddDecimal(command, "$LateFeeIgst", row.LateFeeIgst);
        AddDecimal(command, "$LateFeeCgst", row.LateFeeCgst);
        AddDecimal(command, "$LateFeeSgst", row.LateFeeSgst);
        AddDecimal(command, "$LateFeeCess", row.LateFeeCess);
        command.Parameters.AddWithValue("$PaymentRowsJson", JsonSerializer.Serialize(row.PaymentRows));
        command.Parameters.AddWithValue("$LiabilityBreakupJson", JsonSerializer.Serialize(row.LiabilityBreakup));
        command.Parameters.AddWithValue("$RawFieldsJson", JsonSerializer.Serialize(row.RawFields));
        command.Parameters.AddWithValue("$IsNilReturn", row.IsNilReturn ? 1 : 0);
        command.Parameters.AddWithValue("$ParseValid", row.ParseValid ? 1 : 0);
        command.Parameters.AddWithValue("$ParseNotes", row.ParseNotes.Trim());
        command.Parameters.AddWithValue("$SourcePath", row.SourcePath.Trim());
        command.Parameters.AddWithValue("$RawText", row.RawText);
        command.Parameters.AddWithValue("$ImportedUtc", (row.ImportedUtc == default ? DateTime.UtcNow : row.ImportedUtc).ToString("O"));
        command.Parameters.AddWithValue("$LastEditedUtc", FormatDate(row.LastEditedUtc));
    }

    private static void AddLedgerParameters(SqliteCommand command, GstLedgerPeriodBalance row)
    {
        command.Parameters.AddWithValue("$ClientId", row.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", row.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Gstin", row.Gstin.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$LedgerType", row.LedgerType.Trim());
        command.Parameters.AddWithValue("$PeriodKey", row.PeriodKey.Trim());
        command.Parameters.AddWithValue("$PeriodLabel", row.PeriodLabel.Trim());
        AddDecimal(command, "$OpeningIgst", row.OpeningIgst);
        AddDecimal(command, "$OpeningCgst", row.OpeningCgst);
        AddDecimal(command, "$OpeningSgst", row.OpeningSgst);
        AddDecimal(command, "$OpeningCess", row.OpeningCess);
        AddDecimal(command, "$ClosingIgst", row.ClosingIgst);
        AddDecimal(command, "$ClosingCgst", row.ClosingCgst);
        AddDecimal(command, "$ClosingSgst", row.ClosingSgst);
        AddDecimal(command, "$ClosingCess", row.ClosingCess);
        command.Parameters.AddWithValue("$SourcePath", row.SourcePath.Trim());
        command.Parameters.AddWithValue("$ImportedUtc", (row.ImportedUtc == default ? DateTime.UtcNow : row.ImportedUtc).ToString("O"));
    }

    private static void AddReconciliationParameters(SqliteCommand command, GstReconciliationRow row)
    {
        command.Parameters.AddWithValue("$ClientId", row.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", row.FinancialYear.Trim());
        command.Parameters.AddWithValue("$PeriodKey", row.PeriodKey.Trim());
        command.Parameters.AddWithValue("$PeriodLabel", row.PeriodLabel.Trim());
        command.Parameters.AddWithValue("$Status", row.Status.Trim());
        AddDecimal(command, "$BooksOutput", row.BooksOutput);
        AddDecimal(command, "$PortalOutput", row.PortalOutput);
        AddDecimal(command, "$BooksItc", row.BooksItc);
        AddDecimal(command, "$PortalItc", row.PortalItc);
        AddDecimal(command, "$BooksNet", row.BooksNetWithOpening);
        AddDecimal(command, "$PortalNet", row.PortalNet);
        AddDecimal(command, "$BooksClosing", row.BooksNetWithOpening);
        AddDecimal(command, "$PortalOpening", row.PortalOpening);
        AddDecimal(command, "$PortalClosing", row.PortalLedgerBalance);
        AddDecimal(command, "$BooksRcmTaxable", row.BooksRcmTaxable);
        AddDecimal(command, "$PortalRcmTaxable", row.PortalRcmTaxable);
        AddDecimal(command, "$BooksRcmLiabilityGst", row.BooksRcmLiabilityGst);
        AddDecimal(command, "$PortalRcmGst", row.PortalRcmGst);
        AddDecimal(command, "$BooksNetWithOpening", row.BooksNetWithOpening);
        AddDecimal(command, "$PortalLedgerBalance", row.PortalLedgerBalance);
        command.Parameters.AddWithValue("$BooksPosition", row.BooksPosition.Trim());
        AddDecimal(command, "$TaxPayable", row.TaxPayable);
        AddDecimal(command, "$ItcUsed", row.ItcUsed);
        AddDecimal(command, "$CashPaid", row.CashPaid);
        AddDecimal(command, "$Interest", row.Interest);
        AddDecimal(command, "$LateFee", row.LateFee);
        AddDecimal(command, "$RcmCash", row.RcmCash);
        AddDecimal(command, "$OffsetTotal", row.OffsetTotal);
        AddDecimal(command, "$PaymentDifference", row.PaymentDifference);
        command.Parameters.AddWithValue("$HasJournal", row.HasJournal ? 1 : 0);
        command.Parameters.AddWithValue("$HasReturn", row.HasReturn ? 1 : 0);
        command.Parameters.AddWithValue("$HasCreditLedger", row.HasCreditLedger ? 1 : 0);
        command.Parameters.AddWithValue("$HasCashLedger", row.HasCashLedger ? 1 : 0);
        command.Parameters.AddWithValue("$ReviewNotes", row.ReviewNotes.Trim());
        command.Parameters.AddWithValue("$RefreshedUtc", (row.RefreshedUtc == default ? DateTime.UtcNow : row.RefreshedUtc).ToString("O"));
    }

    private static Gstr3bReturnRecord ReadReturn(SqliteDataReader reader)
    {
        List<Gstr3bPaymentRow> payments;
        try
        {
            payments = JsonSerializer.Deserialize<List<Gstr3bPaymentRow>>(
                ReadString(reader, "PaymentRowsJson")) ?? [];
        }
        catch
        {
            payments = [];
        }

        List<Gstr3bLiabilityBreakupRow> liabilityBreakup;
        try
        {
            liabilityBreakup = JsonSerializer.Deserialize<List<Gstr3bLiabilityBreakupRow>>(
                ReadString(reader, "LiabilityBreakupJson")) ?? [];
        }
        catch
        {
            liabilityBreakup = [];
        }

        Dictionary<string, string> rawFields;
        try
        {
            rawFields = JsonSerializer.Deserialize<Dictionary<string, string>>(
                ReadString(reader, "RawFieldsJson")) ?? [];
        }
        catch
        {
            rawFields = [];
        }

        return new Gstr3bReturnRecord
        {
            PersistentId = ReadInt64(reader, "Id"),
            ClientId = ReadInt64(reader, "ClientId"),
            FinancialYear = ReadString(reader, "FinancialYear"),
            Gstin = ReadString(reader, "Gstin"),
            LegalName = ReadString(reader, "LegalName"),
            TradeName = ReadString(reader, "TradeName"),
            PeriodKey = ReadString(reader, "PeriodKey"),
            PeriodLabel = ReadString(reader, "PeriodLabel"),
            FilingFrequency = ReadString(reader, "FilingFrequency"),
            Arn = ReadString(reader, "Arn"),
            ArnDate = ParseNullableDate(ReadString(reader, "ArnDate")),
            OutwardTaxable = ReadDecimal(reader, "OutwardTaxable"),
            OutwardIgst = ReadDecimal(reader, "OutwardIgst"),
            OutwardCgst = ReadDecimal(reader, "OutwardCgst"),
            OutwardSgst = ReadDecimal(reader, "OutwardSgst"),
            OutwardCess = ReadDecimal(reader, "OutwardCess"),
            RcmTaxable = ReadDecimal(reader, "RcmTaxable"),
            RcmIgst = ReadDecimal(reader, "RcmIgst"),
            RcmCgst = ReadDecimal(reader, "RcmCgst"),
            RcmSgst = ReadDecimal(reader, "RcmSgst"),
            RcmCess = ReadDecimal(reader, "RcmCess"),
            NetItcIgst = ReadDecimal(reader, "NetItcIgst"),
            NetItcCgst = ReadDecimal(reader, "NetItcCgst"),
            NetItcSgst = ReadDecimal(reader, "NetItcSgst"),
            NetItcCess = ReadDecimal(reader, "NetItcCess"),
            CompositionExemptNilInterState = ReadDecimal(reader, "CompositionExemptNilInterState"),
            CompositionExemptNilIntraState = ReadDecimal(reader, "CompositionExemptNilIntraState"),
            NonGstInterState = ReadDecimal(reader, "NonGstInterState"),
            NonGstIntraState = ReadDecimal(reader, "NonGstIntraState"),
            SystemInterestIgst = ReadDecimal(reader, "SystemInterestIgst"),
            SystemInterestCgst = ReadDecimal(reader, "SystemInterestCgst"),
            SystemInterestSgst = ReadDecimal(reader, "SystemInterestSgst"),
            SystemInterestCess = ReadDecimal(reader, "SystemInterestCess"),
            InterestPaidIgst = ReadDecimal(reader, "InterestPaidIgst"),
            InterestPaidCgst = ReadDecimal(reader, "InterestPaidCgst"),
            InterestPaidSgst = ReadDecimal(reader, "InterestPaidSgst"),
            InterestPaidCess = ReadDecimal(reader, "InterestPaidCess"),
            LateFeeIgst = ReadDecimal(reader, "LateFeeIgst"),
            LateFeeCgst = ReadDecimal(reader, "LateFeeCgst"),
            LateFeeSgst = ReadDecimal(reader, "LateFeeSgst"),
            LateFeeCess = ReadDecimal(reader, "LateFeeCess"),
            PaymentRows = payments,
            LiabilityBreakup = liabilityBreakup,
            RawFields = rawFields,
            IsNilReturn = ReadInt64(reader, "IsNilReturn") == 1,
            ParseValid = ReadInt64(reader, "ParseValid") == 1,
            ParseNotes = ReadString(reader, "ParseNotes"),
            SourcePath = ReadString(reader, "SourcePath"),
            RawText = ReadString(reader, "RawText"),
            ImportedUtc = ParseDate(ReadString(reader, "ImportedUtc")),
            LastEditedUtc = ParseNullableDate(ReadString(reader, "LastEditedUtc"))
        };
    }

    private static GstLedgerPeriodBalance ReadLedger(SqliteDataReader reader) =>
        new()
        {
            PersistentId = ReadInt64(reader, "Id"),
            ClientId = ReadInt64(reader, "ClientId"),
            FinancialYear = ReadString(reader, "FinancialYear"),
            LedgerType = ReadString(reader, "LedgerType"),
            Gstin = ReadString(reader, "Gstin"),
            PeriodKey = ReadString(reader, "PeriodKey"),
            PeriodLabel = ReadString(reader, "PeriodLabel"),
            OpeningIgst = ReadDecimal(reader, "OpeningIgst"),
            OpeningCgst = ReadDecimal(reader, "OpeningCgst"),
            OpeningSgst = ReadDecimal(reader, "OpeningSgst"),
            OpeningCess = ReadDecimal(reader, "OpeningCess"),
            ClosingIgst = ReadDecimal(reader, "ClosingIgst"),
            ClosingCgst = ReadDecimal(reader, "ClosingCgst"),
            ClosingSgst = ReadDecimal(reader, "ClosingSgst"),
            ClosingCess = ReadDecimal(reader, "ClosingCess"),
            SourcePath = ReadString(reader, "SourcePath"),
            ImportedUtc = ParseDate(ReadString(reader, "ImportedUtc"))
        };

    private static GstPortalSourceState ReadPortalSourceState(SqliteDataReader reader) =>
        new(ReadInt64(reader, "ClientId"), ReadString(reader, "FinancialYear"),
            ReadString(reader, "Gstin"), ReadString(reader, "SourceType"),
            ReadString(reader, "SourcePath"), ReadString(reader, "PeriodKey"),
            ReadInt64(reader, "SourceSize"), ParseDate(ReadString(reader, "SourceLastWriteUtc")),
            ReadString(reader, "MetadataFingerprint"), ReadString(reader, "ContentHash"),
            ParseDate(ReadString(reader, "LastCheckedUtc")), ParseDate(ReadString(reader, "LastImportedUtc")),
            ParseNullableDate(ReadString(reader, "LastEditedUtc")), ReadString(reader, "Status"));

    private static string FormatDate(DateTime value) => value == default ? string.Empty : value.ToString("O");
    private static string FormatDate(DateTime? value) => value.HasValue ? value.Value.ToString("O") : string.Empty;

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static void AddDecimal(SqliteCommand command, string name, decimal value) =>
        command.Parameters.AddWithValue(name, value);

    private static string ReadString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static long ReadInt64(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0L : Convert.ToInt64(reader.GetValue(ordinal));
    }

    private static decimal ReadDecimal(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal));
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;

    private static DateTime? ParseNullableDate(string value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;
}

public sealed record GstPortalSourceState(
    long ClientId, string FinancialYear, string Gstin, string SourceType, string SourcePath,
    string PeriodKey, long SourceSize, DateTime SourceLastWriteUtc, string MetadataFingerprint,
    string ContentHash, DateTime LastCheckedUtc, DateTime LastImportedUtc, DateTime? LastEditedUtc,
    string Status);

public sealed record GstBootstrapState(
    long ClientId, string FinancialYear, string Gstin, string DiscoveryFingerprint,
    DateTime LastCheckedUtc, DateTime LastImportedUtc);

