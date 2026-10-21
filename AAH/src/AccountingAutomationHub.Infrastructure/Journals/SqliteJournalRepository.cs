using AccountingAutomationHub.Application.Journals;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Journals;

public sealed class SqliteJournalRepository : IJournalRepository
{
    private readonly string _connectionString;

    public SqliteJournalRepository()
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

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteJournalRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS GstJournalSettings
                (
                    ClientId                    INTEGER NOT NULL,
                    FinancialYear               TEXT NOT NULL,
                    FilingFrequency             TEXT NOT NULL DEFAULT 'Monthly',
                    SettlementLedgerMode        TEXT NOT NULL DEFAULT 'Single Ledger',
                    BalanceType                  TEXT NOT NULL DEFAULT 'Payable',
                    SingleSettlementLedger      TEXT NOT NULL DEFAULT 'GST Payable / Receivable',
                    IgstSettlementLedger        TEXT NOT NULL DEFAULT 'IGST Payable / Receivable',
                    CgstSettlementLedger        TEXT NOT NULL DEFAULT 'CGST Payable / Receivable',
                    SgstSettlementLedger        TEXT NOT NULL DEFAULT 'SGST Payable / Receivable',
                    TemporaryRcmIgstLedger      TEXT NOT NULL DEFAULT 'Input IGST RCM',
                    TemporaryRcmCgstLedger      TEXT NOT NULL DEFAULT 'Input CGST RCM',
                    TemporaryRcmSgstLedger      TEXT NOT NULL DEFAULT 'Input SGST RCM',
                    RcmExpenseIgstLedger        TEXT NOT NULL DEFAULT 'RCM Input Expenses',
                    RcmExpenseCgstLedger        TEXT NOT NULL DEFAULT 'RCM Input Expenses',
                    RcmExpenseSgstLedger        TEXT NOT NULL DEFAULT 'RCM Input Expenses',
                    NilSalesPeriods             TEXT NOT NULL DEFAULT '|',
                    NilSalesConflicts           TEXT NOT NULL DEFAULT '|',
                    OpeningBooksBalance         REAL NOT NULL DEFAULT 0,
                    UpdatedUtc                  TEXT NOT NULL,
                    PRIMARY KEY(ClientId, FinancialYear),
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS JournalVouchers
                (
                    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClientId            INTEGER NOT NULL,
                    FinancialYear       TEXT NOT NULL,
                    PeriodKey           TEXT NOT NULL,
                    PeriodLabel         TEXT NOT NULL DEFAULT '',
                    VoucherDate         TEXT NOT NULL,
                    VoucherNumber       TEXT NOT NULL DEFAULT '',
                    VoucherType         TEXT NOT NULL DEFAULT 'Journal',
                    Narration           TEXT NOT NULL DEFAULT '',
                    GenerationMode      TEXT NOT NULL DEFAULT 'Automatic',
                    IsUserEdited        INTEGER NOT NULL DEFAULT 0,
                    OutputIgst          REAL NOT NULL DEFAULT 0,
                    OutputCgst          REAL NOT NULL DEFAULT 0,
                    OutputSgst          REAL NOT NULL DEFAULT 0,
                    RcmIgst             REAL NOT NULL DEFAULT 0,
                    RcmCgst             REAL NOT NULL DEFAULT 0,
                    RcmSgst             REAL NOT NULL DEFAULT 0,
                    InputIgst           REAL NOT NULL DEFAULT 0,
                    InputCgst           REAL NOT NULL DEFAULT 0,
                    InputSgst           REAL NOT NULL DEFAULT 0,
                    NormalInputIgst     REAL NOT NULL DEFAULT 0,
                    NormalInputCgst     REAL NOT NULL DEFAULT 0,
                    NormalInputSgst     REAL NOT NULL DEFAULT 0,
                    RcmClaimIgst        REAL NOT NULL DEFAULT 0,
                    RcmClaimCgst        REAL NOT NULL DEFAULT 0,
                    RcmClaimSgst        REAL NOT NULL DEFAULT 0,
                    RcmReverseIgst      REAL NOT NULL DEFAULT 0,
                    RcmReverseCgst      REAL NOT NULL DEFAULT 0,
                    RcmReverseSgst      REAL NOT NULL DEFAULT 0,
                    CreatedUtc          TEXT NOT NULL,
                    UpdatedUtc          TEXT NOT NULL,
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
                );

                DROP INDEX IF EXISTS UX_JournalVouchers_ClientFyPeriodMode;

                CREATE UNIQUE INDEX IF NOT EXISTS UX_JournalVouchers_AutomaticPeriod
                ON JournalVouchers(ClientId, FinancialYear, PeriodKey)
                WHERE GenerationMode = 'Automatic';

                CREATE TABLE IF NOT EXISTS JournalLines
                (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    VoucherId   INTEGER NOT NULL,
                    LineNumber  INTEGER NOT NULL,
                    LedgerName  TEXT NOT NULL,
                    Amount      REAL NOT NULL DEFAULT 0,
                    DrCr        TEXT NOT NULL DEFAULT 'Dr',
                    TaxHead     TEXT NOT NULL DEFAULT '',
                    LineRole    TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY (VoucherId) REFERENCES JournalVouchers(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_JournalLines_Voucher
                ON JournalLines(VoucherId, LineNumber);

                CREATE TABLE IF NOT EXISTS JournalLedgerMappings
                (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClientId        INTEGER NOT NULL,
                    FinancialYear   TEXT NOT NULL,
                    LedgerName      TEXT NOT NULL,
                    MasterGroup     TEXT NOT NULL DEFAULT '',
                    Status          TEXT NOT NULL DEFAULT 'Pending',
                    Source          TEXT NOT NULL DEFAULT 'Journal',
                    UpdatedUtc      TEXT NOT NULL,
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE,
                    UNIQUE(ClientId, FinancialYear, LedgerName COLLATE NOCASE)
                );

                CREATE INDEX IF NOT EXISTS IX_JournalLedgerMappings_ClientFyStatus
                ON JournalLedgerMappings(ClientId, FinancialYear, Status, LedgerName COLLATE NOCASE);
                """;
            command.ExecuteNonQuery();
        }

        // Additive migration for databases created by v3.0.x.
        EnsureColumn(connection, "GstJournalSettings", "BalanceType", "TEXT NOT NULL DEFAULT 'Payable'");
        EnsureColumn(connection, "GstJournalSettings", "IgstSettlementLedger", "TEXT NOT NULL DEFAULT 'IGST Payable / Receivable'");
        EnsureColumn(connection, "GstJournalSettings", "CgstSettlementLedger", "TEXT NOT NULL DEFAULT 'CGST Payable / Receivable'");
        EnsureColumn(connection, "GstJournalSettings", "SgstSettlementLedger", "TEXT NOT NULL DEFAULT 'SGST Payable / Receivable'");
        EnsureColumn(connection, "GstJournalSettings", "TemporaryRcmIgstLedger", "TEXT NOT NULL DEFAULT 'Input IGST RCM'");
        EnsureColumn(connection, "GstJournalSettings", "TemporaryRcmCgstLedger", "TEXT NOT NULL DEFAULT 'Input CGST RCM'");
        EnsureColumn(connection, "GstJournalSettings", "TemporaryRcmSgstLedger", "TEXT NOT NULL DEFAULT 'Input SGST RCM'");
        EnsureColumn(connection, "GstJournalSettings", "RcmExpenseIgstLedger", "TEXT NOT NULL DEFAULT 'RCM Input Expenses'");
        EnsureColumn(connection, "GstJournalSettings", "RcmExpenseCgstLedger", "TEXT NOT NULL DEFAULT 'RCM Input Expenses'");
        EnsureColumn(connection, "GstJournalSettings", "RcmExpenseSgstLedger", "TEXT NOT NULL DEFAULT 'RCM Input Expenses'");
        EnsureColumn(connection, "GstJournalSettings", "NilSalesPeriods", "TEXT NOT NULL DEFAULT '|'");
        EnsureColumn(connection, "GstJournalSettings", "NilSalesConflicts", "TEXT NOT NULL DEFAULT '|'");

        EnsureColumn(connection, "JournalVouchers", "NormalInputIgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "NormalInputCgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "NormalInputSgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "RcmClaimIgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "RcmClaimCgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "RcmClaimSgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "RcmReverseIgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "RcmReverseCgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "RcmReverseSgst", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "JournalVouchers", "WorkflowStatus", "TEXT NOT NULL DEFAULT 'Draft'");
        EnsureColumn(connection, "JournalVouchers", "ReferenceNumber", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "JournalVouchers", "AttachmentPath", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "JournalVouchers", "ReversalOfId", "INTEGER NULL");
        EnsureColumn(connection, "JournalVouchers", "ClonedFromId", "INTEGER NULL");
        EnsureColumn(connection, "JournalVouchers", "RecurringProfileId", "INTEGER NULL");
        EnsureColumn(connection, "JournalVouchers", "SourceRunId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "JournalVouchers", "SourceJobId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "JournalVouchers", "CreatedBy", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "JournalVouchers", "ApprovedBy", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "JournalVouchers", "ApprovedUtc", "TEXT NOT NULL DEFAULT ''");

        using (var migration = connection.CreateCommand())
        {
            migration.CommandText =
                """
                UPDATE GstJournalSettings
                SET TemporaryRcmIgstLedger = 'Input IGST RCM'
                WHERE TemporaryRcmIgstLedger LIKE '%Temporary%';
                UPDATE GstJournalSettings
                SET TemporaryRcmCgstLedger = 'Input CGST RCM'
                WHERE TemporaryRcmCgstLedger LIKE '%Temporary%';
                UPDATE GstJournalSettings
                SET TemporaryRcmSgstLedger = 'Input SGST RCM'
                WHERE TemporaryRcmSgstLedger LIKE '%Temporary%';
                UPDATE JournalLines
                SET LedgerName = REPLACE(LedgerName, 'Temporary Input ', 'Input ')
                WHERE LedgerName LIKE 'Temporary Input % RCM';
                UPDATE JournalLines
                SET LineRole = 'RCM Input Transfer'
                WHERE LineRole = 'RCM Temporary Transfer';
                UPDATE GstJournalSettings
                SET RcmExpenseIgstLedger = 'RCM Input Expenses',
                    RcmExpenseCgstLedger = 'RCM Input Expenses',
                    RcmExpenseSgstLedger = 'RCM Input Expenses'
                WHERE TRIM(RcmExpenseIgstLedger) <> 'RCM Input Expenses'
                   OR TRIM(RcmExpenseCgstLedger) <> 'RCM Input Expenses'
                   OR TRIM(RcmExpenseSgstLedger) <> 'RCM Input Expenses';

                UPDATE JournalLines
                SET LedgerName = REPLACE(LedgerName, ' RCM Input Pending ', ' RCM Input Receivable ')
                WHERE LedgerName LIKE '% RCM Input Pending @ %';

                UPDATE JournalLines
                SET LedgerName = 'RCM Input Expenses',
                    TaxHead = CASE WHEN LineRole LIKE 'RCM%Expense%' THEN 'RCM' ELSE TaxHead END
                WHERE LedgerName LIKE 'RCM % Expense%'
                   OR UPPER(TRIM(LedgerName)) IN ('RCM EXPENSE IGST', 'RCM EXPENSE CGST', 'RCM EXPENSE SGST');
                """;
            migration.ExecuteNonQuery();
        }
    }

    public bool HasRecords(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1 FROM JournalVouchers
                WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public JournalGenerationSettings GetSettings(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT FilingFrequency, SettlementLedgerMode, BalanceType,
                   SingleSettlementLedger, IgstSettlementLedger,
                   CgstSettlementLedger, SgstSettlementLedger,
                   TemporaryRcmIgstLedger, TemporaryRcmCgstLedger,
                   TemporaryRcmSgstLedger, RcmExpenseIgstLedger,
                   RcmExpenseCgstLedger, RcmExpenseSgstLedger,
                   NilSalesPeriods, NilSalesConflicts, OpeningBooksBalance, UpdatedUtc
            FROM GstJournalSettings
            WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new JournalGenerationSettings
            {
                ClientId = clientId,
                FinancialYear = financialYear.Trim()
            };
        }

        var settings = new JournalGenerationSettings
        {
            ClientId = clientId,
            FinancialYear = financialYear.Trim(),
            FilingFrequency = reader.GetString(0),
            SettlementLedgerMode = reader.GetString(1),
            BalanceType = reader.GetString(2),
            SingleSettlementLedger = reader.GetString(3),
            IgstSettlementLedger = reader.GetString(4),
            CgstSettlementLedger = reader.GetString(5),
            SgstSettlementLedger = reader.GetString(6),
            TemporaryRcmIgstLedger = reader.GetString(7),
            TemporaryRcmCgstLedger = reader.GetString(8),
            TemporaryRcmSgstLedger = reader.GetString(9),
            RcmExpenseIgstLedger = reader.GetString(10),
            RcmExpenseCgstLedger = reader.GetString(11),
            RcmExpenseSgstLedger = reader.GetString(12),
            NilSalesPeriods = NormalizePipeList(reader.GetString(13)),
            NilSalesConflicts = NormalizePipeList(reader.GetString(14)),
            OpeningBooksBalance = ReadDecimal(reader, 15),
            UpdatedUtc = ParseDate(reader.GetString(16))
        };
        // Fix82 name migration is one-way only for Receivable mode. The old generic
        // Payable / Receivable defaults are converted to the asset-side receivable names;
        // user-entered custom names and all Payable-side settings are left untouched.
        GstMasterClassificationPolicy.NormalizeReceivableSettlementNames(settings);
        return settings;
    }

    public void SaveSettings(JournalGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        GstMasterClassificationPolicy.NormalizeReceivableSettlementNames(settings);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GstJournalSettings
            (
                ClientId, FinancialYear, FilingFrequency, SettlementLedgerMode,
                BalanceType, SingleSettlementLedger, IgstSettlementLedger,
                CgstSettlementLedger, SgstSettlementLedger,
                TemporaryRcmIgstLedger, TemporaryRcmCgstLedger,
                TemporaryRcmSgstLedger, RcmExpenseIgstLedger,
                RcmExpenseCgstLedger, RcmExpenseSgstLedger,
                NilSalesPeriods, NilSalesConflicts, OpeningBooksBalance, UpdatedUtc
            )
            VALUES
            (
                $ClientId, $FinancialYear, $FilingFrequency, $SettlementLedgerMode,
                $BalanceType, $SingleSettlementLedger, $IgstSettlementLedger,
                $CgstSettlementLedger, $SgstSettlementLedger,
                $TemporaryRcmIgstLedger, $TemporaryRcmCgstLedger,
                $TemporaryRcmSgstLedger, $RcmExpenseIgstLedger,
                $RcmExpenseCgstLedger, $RcmExpenseSgstLedger,
                $NilSalesPeriods, $NilSalesConflicts, $OpeningBooksBalance, $UpdatedUtc
            )
            ON CONFLICT(ClientId, FinancialYear)
            DO UPDATE SET
                FilingFrequency = excluded.FilingFrequency,
                SettlementLedgerMode = excluded.SettlementLedgerMode,
                BalanceType = excluded.BalanceType,
                SingleSettlementLedger = excluded.SingleSettlementLedger,
                IgstSettlementLedger = excluded.IgstSettlementLedger,
                CgstSettlementLedger = excluded.CgstSettlementLedger,
                SgstSettlementLedger = excluded.SgstSettlementLedger,
                TemporaryRcmIgstLedger = excluded.TemporaryRcmIgstLedger,
                TemporaryRcmCgstLedger = excluded.TemporaryRcmCgstLedger,
                TemporaryRcmSgstLedger = excluded.TemporaryRcmSgstLedger,
                RcmExpenseIgstLedger = excluded.RcmExpenseIgstLedger,
                RcmExpenseCgstLedger = excluded.RcmExpenseCgstLedger,
                RcmExpenseSgstLedger = excluded.RcmExpenseSgstLedger,
                NilSalesPeriods = excluded.NilSalesPeriods,
                NilSalesConflicts = excluded.NilSalesConflicts,
                OpeningBooksBalance = excluded.OpeningBooksBalance,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", settings.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", settings.FinancialYear.Trim());
        command.Parameters.AddWithValue("$FilingFrequency", NormalizeFrequency(settings.FilingFrequency));
        command.Parameters.AddWithValue("$SettlementLedgerMode", settings.SettlementLedgerMode.Trim());
        command.Parameters.AddWithValue("$BalanceType", NormalizeBalanceType(settings.BalanceType));
        command.Parameters.AddWithValue("$SingleSettlementLedger", settings.SingleSettlementLedger.Trim());
        command.Parameters.AddWithValue("$IgstSettlementLedger", settings.IgstSettlementLedger.Trim());
        command.Parameters.AddWithValue("$CgstSettlementLedger", settings.CgstSettlementLedger.Trim());
        command.Parameters.AddWithValue("$SgstSettlementLedger", settings.SgstSettlementLedger.Trim());
        command.Parameters.AddWithValue("$TemporaryRcmIgstLedger", settings.TemporaryRcmIgstLedger.Trim());
        command.Parameters.AddWithValue("$TemporaryRcmCgstLedger", settings.TemporaryRcmCgstLedger.Trim());
        command.Parameters.AddWithValue("$TemporaryRcmSgstLedger", settings.TemporaryRcmSgstLedger.Trim());
        command.Parameters.AddWithValue("$RcmExpenseIgstLedger", settings.RcmExpenseIgstLedger.Trim());
        command.Parameters.AddWithValue("$RcmExpenseCgstLedger", settings.RcmExpenseCgstLedger.Trim());
        command.Parameters.AddWithValue("$RcmExpenseSgstLedger", settings.RcmExpenseSgstLedger.Trim());
        command.Parameters.AddWithValue("$NilSalesPeriods", NormalizePipeList(settings.NilSalesPeriods));
        command.Parameters.AddWithValue("$NilSalesConflicts", NormalizePipeList(settings.NilSalesConflicts));
        command.Parameters.AddWithValue("$OpeningBooksBalance", settings.OpeningBooksBalance);
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<JournalVoucher> GetVouchers(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientId, FinancialYear, PeriodKey, PeriodLabel, VoucherDate,
                   VoucherNumber, VoucherType, Narration, GenerationMode, IsUserEdited,
                   OutputIgst, OutputCgst, OutputSgst, RcmIgst, RcmCgst, RcmSgst,
                   InputIgst, InputCgst, InputSgst,
                   NormalInputIgst, NormalInputCgst, NormalInputSgst,
                   RcmClaimIgst, RcmClaimCgst, RcmClaimSgst,
                   RcmReverseIgst, RcmReverseCgst, RcmReverseSgst,
                   CreatedUtc, UpdatedUtc, WorkflowStatus, ReferenceNumber, AttachmentPath,
                   ReversalOfId, ClonedFromId, RecurringProfileId, SourceRunId, SourceJobId, CreatedBy, ApprovedBy, ApprovedUtc
            FROM JournalVouchers
            WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear
            ORDER BY PeriodKey, VoucherDate, Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());

        using var reader = command.ExecuteReader();
        var vouchers = new List<JournalVoucher>();
        while (reader.Read())
        {
            vouchers.Add(ReadVoucher(reader));
        }
        reader.Close();

        foreach (var voucher in vouchers)
        {
            voucher.Lines = ReadLines(connection, voucher.PersistentId);
        }
        return vouchers;
    }

    public IReadOnlySet<string> GetProtectedPeriods(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT PeriodKey
            FROM JournalVouchers
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND GenerationMode = 'Automatic'
              AND IsUserEdited = 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());

        using var reader = command.ExecuteReader();
        var periods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            periods.Add(reader.GetString(0));
        }
        return periods;
    }

    public int ReplaceGenerated(
        long clientId,
        string financialYear,
        IEnumerable<JournalVoucher> vouchers)
    {
        var fy = financialYear.Trim();
        var protectedPeriods = GetProtectedPeriods(clientId, fy);
        var incoming = vouchers
            .Where(voucher => !string.IsNullOrWhiteSpace(voucher.PeriodKey))
            .GroupBy(voucher => voucher.PeriodKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        // IMPORTANT: automatic Journals are period records, not row-sequence records.
        // Keep the same voucher Id for an unchanged GST period and update only the
        // period whose accounting content actually changed. This prevents deleting one
        // Purchase row from recreating every automatic Journal voucher.
        var existing = GetVouchers(clientId, fy)
            .Where(voucher =>
                voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase) &&
                !voucher.IsUserEdited)
            .GroupBy(voucher => voucher.PeriodKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var incomingPeriods = incoming
            .Select(voucher => voucher.PeriodKey.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var voucher in incoming)
        {
            var periodKey = voucher.PeriodKey.Trim();
            if (protectedPeriods.Contains(periodKey))
                continue;

            voucher.ClientId = clientId;
            voucher.FinancialYear = fy;
            voucher.PeriodKey = periodKey;
            voucher.GenerationMode = "Automatic";
            voucher.IsUserEdited = false;

            if (!existing.TryGetValue(periodKey, out var current))
            {
                InsertVoucher(connection, transaction, voucher);
                continue;
            }

            voucher.PersistentId = current.PersistentId;
            voucher.CreatedUtc = current.CreatedUtc;
            // Voucher number is assigned when a GST period is first created and is
            // never renumbered because another period is inserted/deleted.
            voucher.VoucherNumber = current.VoucherNumber;
            if (AutomaticAccountingEquivalent(current, voucher))
                continue;

            UpdateAutomaticVoucher(connection, transaction, current.PersistentId, voucher);
        }

        // A period that genuinely disappeared (for example its last GST-Date source row
        // was deleted) is the only automatic period removed. Unaffected periods retain
        // their original voucher rows/Ids and user-edited automatic periods are protected.
        foreach (var current in existing.Values.Where(voucher =>
                     !incomingPeriods.Contains(voucher.PeriodKey) &&
                     !protectedPeriods.Contains(voucher.PeriodKey)))
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM JournalVouchers WHERE Id=$Id AND GenerationMode='Automatic' AND IsUserEdited=0;";
            delete.Parameters.AddWithValue("$Id", current.PersistentId);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return incoming.Count(voucher => !protectedPeriods.Contains(voucher.PeriodKey));
    }

    private static void UpdateAutomaticVoucher(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long voucherId,
        JournalVoucher voucher)
    {
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE JournalVouchers
                SET PeriodLabel = $PeriodLabel,
                    VoucherDate = $VoucherDate,
                    VoucherNumber = $VoucherNumber,
                    VoucherType = $VoucherType,
                    Narration = $Narration,
                    OutputIgst = $OutputIgst,
                    OutputCgst = $OutputCgst,
                    OutputSgst = $OutputSgst,
                    RcmIgst = $RcmIgst,
                    RcmCgst = $RcmCgst,
                    RcmSgst = $RcmSgst,
                    InputIgst = $InputIgst,
                    InputCgst = $InputCgst,
                    InputSgst = $InputSgst,
                    NormalInputIgst = $NormalInputIgst,
                    NormalInputCgst = $NormalInputCgst,
                    NormalInputSgst = $NormalInputSgst,
                    RcmClaimIgst = $RcmClaimIgst,
                    RcmClaimCgst = $RcmClaimCgst,
                    RcmClaimSgst = $RcmClaimSgst,
                    RcmReverseIgst = $RcmReverseIgst,
                    RcmReverseCgst = $RcmReverseCgst,
                    RcmReverseSgst = $RcmReverseSgst,
                    UpdatedUtc = $UpdatedUtc
                WHERE Id = $Id
                  AND GenerationMode = 'Automatic'
                  AND IsUserEdited = 0;
                """;
            AddVoucherParameters(update, voucher);
            update.Parameters.AddWithValue("$Id", voucherId);
            update.ExecuteNonQuery();
        }

        using (var deleteLines = connection.CreateCommand())
        {
            deleteLines.Transaction = transaction;
            deleteLines.CommandText = "DELETE FROM JournalLines WHERE VoucherId = $VoucherId;";
            deleteLines.Parameters.AddWithValue("$VoucherId", voucherId);
            deleteLines.ExecuteNonQuery();
        }

        InsertLines(connection, transaction, voucherId, voucher.Lines);
    }

    private static bool AutomaticAccountingEquivalent(JournalVoucher left, JournalVoucher right)
    {
        if (!string.Equals(left.PeriodLabel.Trim(), right.PeriodLabel.Trim(), StringComparison.Ordinal) ||
            left.VoucherDate.Date != right.VoucherDate.Date ||
            !string.Equals(left.VoucherNumber.Trim(), right.VoucherNumber.Trim(), StringComparison.Ordinal) ||
            !string.Equals(left.VoucherType.Trim(), right.VoucherType.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.Narration.Trim(), right.Narration.Trim(), StringComparison.Ordinal) ||
            left.OutputIgst != right.OutputIgst || left.OutputCgst != right.OutputCgst || left.OutputSgst != right.OutputSgst ||
            left.RcmIgst != right.RcmIgst || left.RcmCgst != right.RcmCgst || left.RcmSgst != right.RcmSgst ||
            left.InputIgst != right.InputIgst || left.InputCgst != right.InputCgst || left.InputSgst != right.InputSgst ||
            left.NormalInputIgst != right.NormalInputIgst || left.NormalInputCgst != right.NormalInputCgst || left.NormalInputSgst != right.NormalInputSgst ||
            left.RcmClaimIgst != right.RcmClaimIgst || left.RcmClaimCgst != right.RcmClaimCgst || left.RcmClaimSgst != right.RcmClaimSgst ||
            left.RcmReverseIgst != right.RcmReverseIgst || left.RcmReverseCgst != right.RcmReverseCgst || left.RcmReverseSgst != right.RcmReverseSgst)
        {
            return false;
        }

        var leftLines = left.Lines.OrderBy(line => line.LineNumber).ToArray();
        var rightLines = right.Lines.OrderBy(line => line.LineNumber).ToArray();
        if (leftLines.Length != rightLines.Length) return false;

        for (var index = 0; index < leftLines.Length; index++)
        {
            var a = leftLines[index];
            var b = rightLines[index];
            if (a.LineNumber != b.LineNumber ||
                !string.Equals(a.LedgerName.Trim(), b.LedgerName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                a.Amount != b.Amount ||
                !string.Equals(a.DrCr.Trim(), b.DrCr.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.TaxHead.Trim(), b.TaxHead.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.LineRole.Trim(), b.LineRole.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public void SaveVoucher(JournalVoucher voucher)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (voucher.PersistentId <= 0)
        {
            voucher.GenerationMode = "Manual";
            voucher.IsUserEdited = true;
            InsertVoucher(connection, transaction, voucher);
            transaction.Commit();
            return;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE JournalVouchers
                SET PeriodKey = $PeriodKey,
                    PeriodLabel = $PeriodLabel,
                    VoucherDate = $VoucherDate,
                    VoucherNumber = $VoucherNumber,
                    VoucherType = $VoucherType,
                    Narration = $Narration,
                    GenerationMode = CASE
                        WHEN GenerationMode = 'Manual' THEN 'Manual'
                        ELSE GenerationMode
                    END,
                    IsUserEdited = 1,
                    OutputIgst = $OutputIgst,
                    OutputCgst = $OutputCgst,
                    OutputSgst = $OutputSgst,
                    RcmIgst = $RcmIgst,
                    RcmCgst = $RcmCgst,
                    RcmSgst = $RcmSgst,
                    InputIgst = $InputIgst,
                    InputCgst = $InputCgst,
                    InputSgst = $InputSgst,
                    NormalInputIgst = $NormalInputIgst,
                    NormalInputCgst = $NormalInputCgst,
                    NormalInputSgst = $NormalInputSgst,
                    RcmClaimIgst = $RcmClaimIgst,
                    RcmClaimCgst = $RcmClaimCgst,
                    RcmClaimSgst = $RcmClaimSgst,
                    RcmReverseIgst = $RcmReverseIgst,
                    RcmReverseCgst = $RcmReverseCgst,
                    RcmReverseSgst = $RcmReverseSgst,
                    WorkflowStatus = $WorkflowStatus,
                    ReferenceNumber = $ReferenceNumber,
                    AttachmentPath = $AttachmentPath,
                    ReversalOfId = $ReversalOfId,
                    ClonedFromId = $ClonedFromId,
                    RecurringProfileId = $RecurringProfileId,
                    SourceRunId = $SourceRunId,
                    SourceJobId = $SourceJobId,
                    CreatedBy = $CreatedBy,
                    ApprovedBy = $ApprovedBy,
                    ApprovedUtc = $ApprovedUtc,
                    UpdatedUtc = $UpdatedUtc
                WHERE Id = $Id;
                """;
            AddVoucherParameters(update, voucher);
            update.Parameters.AddWithValue("$Id", voucher.PersistentId);
            update.ExecuteNonQuery();
        }

        using (var deleteLines = connection.CreateCommand())
        {
            deleteLines.Transaction = transaction;
            deleteLines.CommandText = "DELETE FROM JournalLines WHERE VoucherId = $VoucherId;";
            deleteLines.Parameters.AddWithValue("$VoucherId", voucher.PersistentId);
            deleteLines.ExecuteNonQuery();
        }

        InsertLines(connection, transaction, voucher.PersistentId, voucher.Lines);
        transaction.Commit();
    }

    public void DeleteVoucher(long voucherId)
    {
        if (voucherId <= 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM JournalVouchers WHERE Id = $Id;";
        command.Parameters.AddWithValue("$Id", voucherId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<JournalLedgerMapping> GetLedgerMappings(
        long clientId,
        string financialYear,
        bool pendingOnly = true)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientId, FinancialYear, LedgerName, MasterGroup,
                   Status, Source, UpdatedUtc
            FROM JournalLedgerMappings
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND ($PendingOnly = 0 OR Status = 'Pending')
            ORDER BY Status, LedgerName COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$PendingOnly", pendingOnly ? 1 : 0);

        using var reader = command.ExecuteReader();
        var result = new List<JournalLedgerMapping>();
        while (reader.Read())
        {
            result.Add(new JournalLedgerMapping
            {
                PersistentId = reader.GetInt64(0),
                ClientId = reader.GetInt64(1),
                FinancialYear = reader.GetString(2),
                LedgerName = reader.GetString(3),
                MasterGroup = reader.GetString(4),
                Status = reader.GetString(5),
                Source = reader.GetString(6),
                UpdatedUtc = ParseDate(reader.GetString(7))
            });
        }
        return result;
    }

    public void UpsertPendingLedgerMappings(
        long clientId,
        string financialYear,
        IEnumerable<string> ledgerNames)
    {
        var names = ledgerNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO JournalLedgerMappings
                (ClientId, FinancialYear, LedgerName, MasterGroup, Status, Source, UpdatedUtc)
                VALUES
                ($ClientId, $FinancialYear, $LedgerName, '', 'Pending', 'Manual Journal', $UpdatedUtc)
                ON CONFLICT(ClientId, FinancialYear, LedgerName)
                DO UPDATE SET
                    Status = CASE
                        WHEN JournalLedgerMappings.Status = 'Resolved' THEN 'Resolved'
                        ELSE 'Pending'
                    END,
                    UpdatedUtc = excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$LedgerName", name);
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void ResolveLedgerMapping(long mappingId, string masterGroup)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE JournalLedgerMappings
            SET MasterGroup = $MasterGroup,
                Status = 'Resolved',
                UpdatedUtc = $UpdatedUtc
            WHERE Id = $Id;
            """;
        command.Parameters.AddWithValue("$MasterGroup", masterGroup.Trim());
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", mappingId);
        command.ExecuteNonQuery();
    }

    public void ReopenLedgerMapping(long mappingId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE JournalLedgerMappings
            SET Status = 'Pending', UpdatedUtc = $UpdatedUtc
            WHERE Id = $Id;
            """;
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", mappingId);
        command.ExecuteNonQuery();
    }

    internal static long InsertRecurringVoucherInTransaction(
        SqliteConnection connection, SqliteTransaction transaction, JournalVoucher voucher)
    {
        if (voucher.PersistentId > 0 || !voucher.GenerationMode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a new manual Journal may be inserted through recurring generation.");
        return InsertVoucher(connection, transaction, voucher);
    }

    private static long InsertVoucher(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JournalVoucher voucher)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO JournalVouchers
            (
                ClientId, FinancialYear, PeriodKey, PeriodLabel, VoucherDate,
                VoucherNumber, VoucherType, Narration, GenerationMode, IsUserEdited,
                OutputIgst, OutputCgst, OutputSgst, RcmIgst, RcmCgst, RcmSgst,
                InputIgst, InputCgst, InputSgst,
                NormalInputIgst, NormalInputCgst, NormalInputSgst,
                RcmClaimIgst, RcmClaimCgst, RcmClaimSgst,
                RcmReverseIgst, RcmReverseCgst, RcmReverseSgst,
                CreatedUtc, UpdatedUtc, WorkflowStatus, ReferenceNumber, AttachmentPath,
                ReversalOfId, ClonedFromId, RecurringProfileId, SourceRunId, SourceJobId, CreatedBy, ApprovedBy, ApprovedUtc
            )
            VALUES
            (
                $ClientId, $FinancialYear, $PeriodKey, $PeriodLabel, $VoucherDate,
                $VoucherNumber, $VoucherType, $Narration, $GenerationMode, $IsUserEdited,
                $OutputIgst, $OutputCgst, $OutputSgst, $RcmIgst, $RcmCgst, $RcmSgst,
                $InputIgst, $InputCgst, $InputSgst,
                $NormalInputIgst, $NormalInputCgst, $NormalInputSgst,
                $RcmClaimIgst, $RcmClaimCgst, $RcmClaimSgst,
                $RcmReverseIgst, $RcmReverseCgst, $RcmReverseSgst,
                $CreatedUtc, $UpdatedUtc, $WorkflowStatus, $ReferenceNumber, $AttachmentPath,
                $ReversalOfId, $ClonedFromId, $RecurringProfileId, $SourceRunId, $SourceJobId, $CreatedBy, $ApprovedBy, $ApprovedUtc
            );
            SELECT last_insert_rowid();
            """;
        AddVoucherParameters(insert, voucher);
        var id = Convert.ToInt64(insert.ExecuteScalar());
        voucher.PersistentId = id;
        InsertLines(connection, transaction, id, voucher.Lines);
        return id;
    }

    private static void AddVoucherParameters(SqliteCommand command, JournalVoucher voucher)
    {
        var now = DateTime.UtcNow;
        command.Parameters.AddWithValue("$ClientId", voucher.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", voucher.FinancialYear.Trim());
        command.Parameters.AddWithValue("$PeriodKey", voucher.PeriodKey.Trim());
        command.Parameters.AddWithValue("$PeriodLabel", voucher.PeriodLabel.Trim());
        command.Parameters.AddWithValue("$VoucherDate", voucher.VoucherDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$VoucherNumber", voucher.VoucherNumber.Trim());
        command.Parameters.AddWithValue("$VoucherType", voucher.VoucherType.Trim());
        command.Parameters.AddWithValue("$Narration", voucher.Narration.Trim());
        command.Parameters.AddWithValue("$GenerationMode", voucher.GenerationMode.Trim());
        command.Parameters.AddWithValue("$IsUserEdited", voucher.IsUserEdited ? 1 : 0);
        AddDecimal(command, "$OutputIgst", voucher.OutputIgst);
        AddDecimal(command, "$OutputCgst", voucher.OutputCgst);
        AddDecimal(command, "$OutputSgst", voucher.OutputSgst);
        AddDecimal(command, "$RcmIgst", voucher.RcmIgst);
        AddDecimal(command, "$RcmCgst", voucher.RcmCgst);
        AddDecimal(command, "$RcmSgst", voucher.RcmSgst);
        AddDecimal(command, "$InputIgst", voucher.InputIgst);
        AddDecimal(command, "$InputCgst", voucher.InputCgst);
        AddDecimal(command, "$InputSgst", voucher.InputSgst);
        AddDecimal(command, "$NormalInputIgst", voucher.NormalInputIgst);
        AddDecimal(command, "$NormalInputCgst", voucher.NormalInputCgst);
        AddDecimal(command, "$NormalInputSgst", voucher.NormalInputSgst);
        AddDecimal(command, "$RcmClaimIgst", voucher.RcmClaimIgst);
        AddDecimal(command, "$RcmClaimCgst", voucher.RcmClaimCgst);
        AddDecimal(command, "$RcmClaimSgst", voucher.RcmClaimSgst);
        AddDecimal(command, "$RcmReverseIgst", voucher.RcmReverseIgst);
        AddDecimal(command, "$RcmReverseCgst", voucher.RcmReverseCgst);
        AddDecimal(command, "$RcmReverseSgst", voucher.RcmReverseSgst);
        command.Parameters.AddWithValue("$WorkflowStatus", string.IsNullOrWhiteSpace(voucher.WorkflowStatus) ? "Draft" : voucher.WorkflowStatus.Trim());
        command.Parameters.AddWithValue("$ReferenceNumber", voucher.ReferenceNumber?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$AttachmentPath", voucher.AttachmentPath?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$ReversalOfId", (object?)voucher.ReversalOfId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ClonedFromId", (object?)voucher.ClonedFromId ?? DBNull.Value);
        command.Parameters.AddWithValue("$RecurringProfileId", (object?)voucher.RecurringProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$SourceRunId", voucher.SourceRunId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$SourceJobId", voucher.SourceJobId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$CreatedBy", string.IsNullOrWhiteSpace(voucher.CreatedBy) ? Environment.UserName : voucher.CreatedBy.Trim());
        command.Parameters.AddWithValue("$ApprovedBy", voucher.ApprovedBy?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$ApprovedUtc", voucher.ApprovedUtc?.ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue(
            "$CreatedUtc",
            (voucher.CreatedUtc == default ? now : voucher.CreatedUtc).ToString("O"));
        command.Parameters.AddWithValue("$UpdatedUtc", now.ToString("O"));
    }

    private static void InsertLines(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long voucherId,
        IEnumerable<JournalLine> lines)
    {
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO JournalLines
                (VoucherId, LineNumber, LedgerName, Amount, DrCr, TaxHead, LineRole)
                VALUES
                ($VoucherId, $LineNumber, $LedgerName, $Amount, $DrCr, $TaxHead, $LineRole);
                """;
            insert.Parameters.AddWithValue("$VoucherId", voucherId);
            insert.Parameters.AddWithValue(
                "$LineNumber",
                line.LineNumber > 0 ? line.LineNumber : lineNumber);
            insert.Parameters.AddWithValue("$LedgerName", line.LedgerName.Trim());
            AddDecimal(insert, "$Amount", Math.Abs(line.Amount));
            insert.Parameters.AddWithValue("$DrCr", line.DrCr.Trim());
            insert.Parameters.AddWithValue("$TaxHead", line.TaxHead.Trim());
            insert.Parameters.AddWithValue("$LineRole", line.LineRole.Trim());
            insert.ExecuteNonQuery();
        }
    }

    private static List<JournalLine> ReadLines(SqliteConnection connection, long voucherId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, VoucherId, LineNumber, LedgerName, Amount, DrCr, TaxHead, LineRole
            FROM JournalLines
            WHERE VoucherId = $VoucherId
            ORDER BY LineNumber, Id;
            """;
        command.Parameters.AddWithValue("$VoucherId", voucherId);
        using var reader = command.ExecuteReader();
        var lines = new List<JournalLine>();
        while (reader.Read())
        {
            lines.Add(new JournalLine
            {
                PersistentId = reader.GetInt64(0),
                VoucherId = reader.GetInt64(1),
                LineNumber = reader.GetInt32(2),
                LedgerName = reader.GetString(3),
                Amount = ReadDecimal(reader, 4),
                DrCr = reader.GetString(5),
                TaxHead = reader.GetString(6),
                LineRole = reader.GetString(7)
            });
        }
        return lines;
    }

    private static JournalVoucher ReadVoucher(SqliteDataReader reader) =>
        new()
        {
            PersistentId = reader.GetInt64(0),
            ClientId = reader.GetInt64(1),
            FinancialYear = reader.GetString(2),
            PeriodKey = reader.GetString(3),
            PeriodLabel = reader.GetString(4),
            VoucherDate = ParseDate(reader.GetString(5)),
            VoucherNumber = reader.GetString(6),
            VoucherType = reader.GetString(7),
            Narration = reader.GetString(8),
            GenerationMode = reader.GetString(9),
            IsUserEdited = reader.GetInt32(10) == 1,
            OutputIgst = ReadDecimal(reader, 11),
            OutputCgst = ReadDecimal(reader, 12),
            OutputSgst = ReadDecimal(reader, 13),
            RcmIgst = ReadDecimal(reader, 14),
            RcmCgst = ReadDecimal(reader, 15),
            RcmSgst = ReadDecimal(reader, 16),
            InputIgst = ReadDecimal(reader, 17),
            InputCgst = ReadDecimal(reader, 18),
            InputSgst = ReadDecimal(reader, 19),
            NormalInputIgst = ReadDecimal(reader, 20),
            NormalInputCgst = ReadDecimal(reader, 21),
            NormalInputSgst = ReadDecimal(reader, 22),
            RcmClaimIgst = ReadDecimal(reader, 23),
            RcmClaimCgst = ReadDecimal(reader, 24),
            RcmClaimSgst = ReadDecimal(reader, 25),
            RcmReverseIgst = ReadDecimal(reader, 26),
            RcmReverseCgst = ReadDecimal(reader, 27),
            RcmReverseSgst = ReadDecimal(reader, 28),
            CreatedUtc = ParseDate(reader.GetString(29)),
            UpdatedUtc = ParseDate(reader.GetString(30)),
            WorkflowStatus = reader.GetString(31),
            ReferenceNumber = reader.GetString(32),
            AttachmentPath = reader.GetString(33),
            ReversalOfId = reader.IsDBNull(34) ? null : reader.GetInt64(34),
            ClonedFromId = reader.IsDBNull(35) ? null : reader.GetInt64(35),
            RecurringProfileId = reader.IsDBNull(36) ? null : reader.GetInt64(36),
            SourceRunId = reader.GetString(37),
            SourceJobId = reader.GetString(38),
            CreatedBy = reader.GetString(39),
            ApprovedBy = reader.GetString(40),
            ApprovedUtc = string.IsNullOrWhiteSpace(reader.GetString(41)) ? null : ParseDate(reader.GetString(41))
        };

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, table, column, definition);
    }

    private static void AddDecimal(SqliteCommand command, string name, decimal value) =>
        command.Parameters.AddWithValue(name, Convert.ToDouble(value));

    private static decimal ReadDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal));

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;

    private static string NormalizeFrequency(string value) =>
        value.Contains("Quarter", StringComparison.OrdinalIgnoreCase) ? "Quarterly" : "Monthly";

    private static string NormalizeBalanceType(string value) =>
        value.Contains("Receiv", StringComparison.OrdinalIgnoreCase) ? "Receivable" : "Payable";

    private static string NormalizePipeList(string? value)
    {
        var keys = (value ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(key => key.Length == 6 && key.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        return keys.Length == 0 ? "|" : $"|{string.Join("|", keys)}|";
    }
}
