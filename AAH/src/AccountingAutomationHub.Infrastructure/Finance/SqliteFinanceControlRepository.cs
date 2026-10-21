using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Domain.Finance;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Sales;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Finance;

/// <summary>
/// Cross-module accounting controls that intentionally sit above the individual
/// import screens: audit trail, period locks, month-end close, cash planning,
/// document ageing, fixed assets, bank rules, budgets, approvals, inventory
/// policies, document inbox and global search.
/// </summary>
public sealed class SqliteFinanceControlRepository
{
    private readonly string _connectionString;
    private readonly SqliteSalesRecordRepository _salesRepository = new();

    public SqliteFinanceControlRepository()
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

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteFinanceControlRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS FinanceAuditEvents
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL DEFAULT 0,
                FinancialYear   TEXT NOT NULL DEFAULT '',
                Module          TEXT NOT NULL DEFAULT '',
                EntityType      TEXT NOT NULL DEFAULT '',
                EntityKey       TEXT NOT NULL DEFAULT '',
                Operation       TEXT NOT NULL DEFAULT '',
                OldValue        TEXT NOT NULL DEFAULT '',
                NewValue        TEXT NOT NULL DEFAULT '',
                Source          TEXT NOT NULL DEFAULT 'Database Trigger',
                ChangedUtc      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_FinanceAudit_ClientFyUtc
            ON FinanceAuditEvents(ClientId,FinancialYear,ChangedUtc DESC);

            CREATE TABLE IF NOT EXISTS AccountingPeriodLocks
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Module          TEXT NOT NULL DEFAULT 'All' COLLATE NOCASE,
                LockDate        TEXT NOT NULL,
                Reason          TEXT NOT NULL DEFAULT '',
                IsActive        INTEGER NOT NULL DEFAULT 1,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PeriodLocks_ClientFyModule
            ON AccountingPeriodLocks(ClientId,FinancialYear,Module COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS FinanceSettlements
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Side            TEXT NOT NULL COLLATE NOCASE,
                Party           TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                DocumentNumber  TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                DocumentDate    TEXT NOT NULL DEFAULT '',
                SettledAmount   REAL NOT NULL DEFAULT 0,
                SettlementDate  TEXT NOT NULL,
                Reference       TEXT NOT NULL DEFAULT '',
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_FinanceSettlements_Document
            ON FinanceSettlements(ClientId,FinancialYear,Side,Party COLLATE NOCASE,DocumentNumber COLLATE NOCASE,DocumentDate);

            CREATE TABLE IF NOT EXISTS FixedAssets
            (
                Id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId                    INTEGER NOT NULL,
                FinancialYear               TEXT NOT NULL,
                AssetCode                   TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                AssetName                   TEXT NOT NULL COLLATE NOCASE,
                AssetGroup                  TEXT NOT NULL DEFAULT 'Fixed Assets',
                PurchaseDate                TEXT NOT NULL DEFAULT '',
                PutToUseDate                TEXT NOT NULL DEFAULT '',
                PurchaseCost                REAL NOT NULL DEFAULT 0,
                ResidualValue               REAL NOT NULL DEFAULT 0,
                DepreciationMethod          TEXT NOT NULL DEFAULT 'SLM',
                DepreciationRate            REAL NOT NULL DEFAULT 0,
                OpeningAccumulatedDep       REAL NOT NULL DEFAULT 0,
                DisposalDate                TEXT NOT NULL DEFAULT '',
                DisposalValue               REAL NULL,
                Location                    TEXT NOT NULL DEFAULT '',
                SerialNumber                TEXT NOT NULL DEFAULT '',
                Notes                       TEXT NOT NULL DEFAULT '',
                UpdatedUtc                  TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_FixedAssets_ClientFyCodeName
            ON FixedAssets(ClientId,FinancialYear,AssetCode COLLATE NOCASE,AssetName COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS FixedAssetDepreciationSettings
            (
                ClientId                    INTEGER NOT NULL,
                FinancialYear               TEXT NOT NULL,
                DepreciationPolicy          TEXT NOT NULL DEFAULT 'Fixed/Common',
                DepreciationMethod          TEXT NOT NULL DEFAULT 'SLM',
                CommonDepreciationRate      REAL NOT NULL DEFAULT 0,
                ExistingAssetCalculation    TEXT NOT NULL DEFAULT 'Yearly',
                AdditionCalculation         TEXT NOT NULL DEFAULT 'Day-wise',
                AdditionalDepreciationEnabled INTEGER NOT NULL DEFAULT 0,
                AdditionalDepreciationRate  REAL NOT NULL DEFAULT 0,
                DepreciationPosting         TEXT NOT NULL DEFAULT 'Direct to Fixed Asset',
                ExpenseLedgerName           TEXT NOT NULL DEFAULT '',
                ExpenseLedgerMasterId       TEXT NOT NULL DEFAULT '',
                VoucherRoundOffMode         TEXT NOT NULL DEFAULT 'No Round Off',
                UpdatedUtc                  TEXT NOT NULL,
                PRIMARY KEY(ClientId, FinancialYear),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS FixedAssetPostingLedgerMappings
            (
                ClientId                    INTEGER NOT NULL,
                FinancialYear               TEXT NOT NULL,
                AssetLedgerName             TEXT NOT NULL DEFAULT '',
                AssetLedgerMasterId         TEXT NOT NULL DEFAULT '',
                AccumulatedLedgerName       TEXT NOT NULL DEFAULT '',
                AccumulatedLedgerMasterId   TEXT NOT NULL DEFAULT '',
                UpdatedUtc                  TEXT NOT NULL,
                PRIMARY KEY(ClientId, FinancialYear, AssetLedgerMasterId, AssetLedgerName),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS BankRules
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                Priority            INTEGER NOT NULL DEFAULT 100,
                RuleName            TEXT NOT NULL COLLATE NOCASE,
                BankContains        TEXT NOT NULL DEFAULT '',
                Mode                TEXT NOT NULL DEFAULT '',
                NarrationContains   TEXT NOT NULL DEFAULT '',
                NarrationStartsWith TEXT NOT NULL DEFAULT '',
                ReferenceContains   TEXT NOT NULL DEFAULT '',
                NarrationRegex      TEXT NOT NULL DEFAULT '',
                MinAmount           REAL NULL,
                MaxAmount           REAL NULL,
                LedgerName          TEXT NOT NULL DEFAULT '',
                LedgerGroup         TEXT NOT NULL DEFAULT '',
                IsActive            INTEGER NOT NULL DEFAULT 1,
                AppliedCount        INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BankRules_ClientFyName
            ON BankRules(ClientId,FinancialYear,RuleName COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS FinanceBudgets
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Period          TEXT NOT NULL,
                Category        TEXT NOT NULL COLLATE NOCASE,
                BudgetAmount    REAL NOT NULL DEFAULT 0,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_FinanceBudgets_Key
            ON FinanceBudgets(ClientId,FinancialYear,Period,Category COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS RecurringCashFlowTemplates
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Name            TEXT NOT NULL COLLATE NOCASE,
                Direction       TEXT NOT NULL DEFAULT 'Outflow',
                Amount          REAL NOT NULL DEFAULT 0,
                DayOfMonth      INTEGER NOT NULL DEFAULT 1,
                StartDate       TEXT NOT NULL,
                EndDate         TEXT NOT NULL DEFAULT '',
                LedgerName      TEXT NOT NULL DEFAULT '',
                IsActive        INTEGER NOT NULL DEFAULT 1,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_RecurringCashFlow_Key
            ON RecurringCashFlowTemplates(ClientId,FinancialYear,Name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS FinanceApprovals
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Module          TEXT NOT NULL DEFAULT '',
                ActionType      TEXT NOT NULL DEFAULT '',
                Reference       TEXT NOT NULL DEFAULT '',
                Amount          REAL NOT NULL DEFAULT 0,
                RequestedBy     TEXT NOT NULL DEFAULT '',
                Status          TEXT NOT NULL DEFAULT 'Pending',
                DecisionNote    TEXT NOT NULL DEFAULT '',
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_FinanceApprovals_ClientFyStatus
            ON FinanceApprovals(ClientId,FinancialYear,Status);

            CREATE TABLE IF NOT EXISTS InventoryPolicies
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                StockItemName       TEXT NOT NULL COLLATE NOCASE,
                OpeningQuantity     REAL NOT NULL DEFAULT 0,
                ReorderLevel        REAL NOT NULL DEFAULT 0,
                MinimumStock        REAL NOT NULL DEFAULT 0,
                MaximumStock        REAL NOT NULL DEFAULT 0,
                ExpiryWarningDays   INTEGER NOT NULL DEFAULT 60,
                UpdatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_InventoryPolicies_Item
            ON InventoryPolicies(ClientId,FinancialYear,StockItemName COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "FixedAssets", "TallyLedgerName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FixedAssets", "TallyLedgerMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FixedAssets", "SupplierName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FixedAssets", "OpeningBalance", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "FixedAssets", "OpeningSource", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FixedAssetDepreciationSettings", "VoucherRoundOffMode", "TEXT NOT NULL DEFAULT 'No Round Off'");
        EnsureColumn(connection, "BankRules", "NarrationStartsWith", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "BankRules", "ReferenceContains", "TEXT NOT NULL DEFAULT ''");
        CreateAuditTriggers(connection);
        CreatePeriodLockTriggers(connection);
        _salesRepository.Initialize();
    }

    public IReadOnlyList<FinanceAuditEvent> GetAudit(long clientId, string financialYear, int limit = 500)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,Module,EntityType,EntityKey,Operation,OldValue,NewValue,Source,ChangedUtc
            FROM FinanceAuditEvents
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY Id DESC LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 25, 5000));
        using var reader = command.ExecuteReader();
        var rows = new List<FinanceAuditEvent>();
        while (reader.Read())
        {
            rows.Add(new FinanceAuditEvent
            {
                Id = reader.GetInt64(0), ClientId = reader.GetInt64(1), FinancialYear = reader.GetString(2),
                Module = reader.GetString(3), EntityType = reader.GetString(4), EntityKey = reader.GetString(5), Operation = reader.GetString(6),
                OldValue = reader.GetString(7), NewValue = reader.GetString(8), Source = reader.GetString(9), ChangedUtc = ParseDateTime(reader.GetString(10))
            });
        }
        return rows;
    }

    public IReadOnlyList<AccountingPeriodLock> GetPeriodLocks(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,Module,LockDate,Reason,IsActive,UpdatedUtc
            FROM AccountingPeriodLocks
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY CASE WHEN Module='All' THEN 0 ELSE 1 END,Module COLLATE NOCASE;
            """;
        AddScope(command, clientId, financialYear);
        using var reader = command.ExecuteReader();
        var result = new List<AccountingPeriodLock>();
        while (reader.Read()) result.Add(new AccountingPeriodLock
        {
            Id=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),Module=reader.GetString(3),
            LockDate=ParseDate(reader.GetString(4)) ?? DateTime.MinValue,Reason=reader.GetString(5),IsActive=reader.GetInt32(6)==1,UpdatedUtc=ParseDateTime(reader.GetString(7))
        });
        return result;
    }

    public void SavePeriodLock(AccountingPeriodLock item)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AccountingPeriodLocks(ClientId,FinancialYear,Module,LockDate,Reason,IsActive,UpdatedUtc)
            VALUES($ClientId,$FinancialYear,$Module,$LockDate,$Reason,$Active,$Now)
            ON CONFLICT(ClientId,FinancialYear,Module)
            DO UPDATE SET LockDate=excluded.LockDate,Reason=excluded.Reason,IsActive=excluded.IsActive,UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", item.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", item.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Module", string.IsNullOrWhiteSpace(item.Module) ? "All" : item.Module.Trim());
        command.Parameters.AddWithValue("$LockDate", item.LockDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$Reason", item.Reason.Trim());
        command.Parameters.AddWithValue("$Active", item.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UnlockPeriod(long clientId, string financialYear, string module, string reason)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AccountingPeriodLocks SET IsActive=0,Reason=$Reason,UpdatedUtc=$Now WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Module=$Module COLLATE NOCASE;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$Module", module.Trim());
        command.Parameters.AddWithValue("$Reason", reason.Trim());
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public bool IsDateLocked(long clientId, string financialYear, string module, DateTime date)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
              SELECT 1 FROM AccountingPeriodLocks
              WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND IsActive=1
                AND (Module='All' COLLATE NOCASE OR Module=$Module COLLATE NOCASE)
                AND date($Date) <= date(LockDate));
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$Module", module.Trim());
        command.Parameters.AddWithValue("$Date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public IReadOnlyList<MonthEndCloseCheck> GetCloseChecks(long clientId, string financialYear, DateTime throughDate)
    {
        using var connection = OpenConnection();
        var dateText = throughDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var checks = new List<MonthEndCloseCheck>
        {
            CheckCount(connection, clientId, financialYear, "Banking", "Unreconciled bank statements",
                "SELECT COUNT(*) FROM BankStatements WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND date(COALESCE(NULLIF(PeriodTo,''),ImportedUtc))<=date($Date) AND ReconciliationStatus<>'Reconciled' COLLATE NOCASE;",
                dateText, "Bank statements still need reconciliation."),
            CheckCount(connection, clientId, financialYear, "Banking", "Unmapped bank transactions",
                "SELECT COUNT(*) FROM BankTransactions WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND date(TransactionDate)<=date($Date) AND RecordStatus='Active' COLLATE NOCASE AND (TRIM(LedgerName)='' OR MappingStatus<>'Mapped' COLLATE NOCASE);",
                dateText, "Bank transactions still require final ledger mapping."),
            CheckCount(connection, clientId, financialYear, "Purchase", "Purchase reconciliation differences",
                "SELECT COUNT(*) FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND TRIM(GstDate)<>'' AND date(GstDate)<=date($Date) AND TRIM(ReconciliationStatus)<>'' AND ReconciliationStatus NOT IN ('Matched','Exact','Reconciled');",
                dateText, "Purchase/2A/2B differences remain."),
            CheckCount(connection, clientId, financialYear, "GST", "Open GST reconciliation differences",
                "SELECT COUNT(*) FROM GstReconciliation WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND TRIM(Status)<>'' AND Status NOT IN ('Matched','Reconciled','Ready');",
                dateText, "GST reconciliation has unresolved rows."),
            CheckCount(connection, clientId, financialYear, "Action Center", "Open blocked imports",
                "SELECT COUNT(*) FROM ActionRequired WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Open' COLLATE NOCASE;",
                dateText, "Action Required still contains unresolved import failures."),
            CheckCount(connection, clientId, financialYear, "Masters", "Duplicate active ledgers",
                "SELECT COUNT(*) FROM (SELECT UPPER(TRIM(Name)) K,COUNT(*) C FROM Masters WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND IsArchived=0 AND Type='Ledger' COLLATE NOCASE GROUP BY UPPER(TRIM(Name)) HAVING C>1);",
                dateText, "Duplicate active ledger masters require cleanup."),
            CheckCount(connection, clientId, financialYear, "Approvals", "Pending approvals",
                "SELECT COUNT(*) FROM FinanceApprovals WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Pending' COLLATE NOCASE;",
                dateText, "Maker-checker approvals remain pending.")
        };
        return checks;
    }

    public IReadOnlyList<FinanceCashFlowRow> GetCashFlow(long clientId, string financialYear, int monthsForward = 3)
    {
        var start = FinancialYearStart(financialYear) ?? new DateTime(DateTime.Today.Year, 4, 1);
        var end = start.AddYears(1).AddMonths(Math.Clamp(monthsForward, 0, 12));
        var actual = new Dictionary<string, (decimal Inflow, decimal Outflow)>(StringComparer.OrdinalIgnoreCase);
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT substr(TransactionDate,1,7) Period,COALESCE(SUM(Credit),0),COALESCE(SUM(Debit),0)
                FROM BankTransactions
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND RecordStatus='Active' COLLATE NOCASE
                GROUP BY substr(TransactionDate,1,7);
                """;
            AddScope(command, clientId, financialYear);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                actual[reader.GetString(0)] = (ToDecimal(reader.GetValue(1)), ToDecimal(reader.GetValue(2)));
        }

        var templates = GetRecurringTemplates(clientId, financialYear).Where(x => x.IsActive).ToArray();
        var rows = new List<FinanceCashFlowRow>();
        decimal running = 0m;
        for (var cursor = new DateTime(start.Year, start.Month, 1); cursor < end; cursor = cursor.AddMonths(1))
        {
            var key = cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            actual.TryGetValue(key, out var a);
            decimal plannedIn = 0m, plannedOut = 0m;
            foreach (var template in templates)
            {
                var monthEnd = cursor.AddMonths(1).AddDays(-1);
                if (template.StartDate > monthEnd || (template.EndDate.HasValue && template.EndDate.Value < cursor)) continue;
                if (template.Direction.Equals("Inflow", StringComparison.OrdinalIgnoreCase)) plannedIn += template.Amount;
                else plannedOut += template.Amount;
            }
            running += a.Inflow - a.Outflow + plannedIn - plannedOut;
            rows.Add(new FinanceCashFlowRow
            {
                Period = cursor.ToString("MMM yyyy", CultureInfo.InvariantCulture), PeriodStart = cursor,
                ActualInflow=a.Inflow,ActualOutflow=a.Outflow,PlannedInflow=plannedIn,PlannedOutflow=plannedOut,Closing=running
            });
        }
        return rows;
    }

    public IReadOnlyList<FinanceAgingRow> GetAging(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        var result = new List<FinanceAgingRow>();
        ReadAgingSide(connection, clientId, financialYear, "Receivable", "SalesRecords", "CustomerName", result);
        ReadAgingSide(connection, clientId, financialYear, "Payable", "PurchaseRecords", "SupplierName", result);
        return result.OrderByDescending(x => x.AgeDays).ThenBy(x => x.Party, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void SaveSettlement(FinanceSettlement item)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO FinanceSettlements(ClientId,FinancialYear,Side,Party,DocumentNumber,DocumentDate,SettledAmount,SettlementDate,Reference,UpdatedUtc)
            VALUES($ClientId,$FinancialYear,$Side,$Party,$Document,$DocumentDate,$Amount,$SettlementDate,$Reference,$Now);
            """;
        command.Parameters.AddWithValue("$ClientId", item.ClientId); command.Parameters.AddWithValue("$FinancialYear", item.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Side", item.Side.Trim()); command.Parameters.AddWithValue("$Party", item.Party.Trim()); command.Parameters.AddWithValue("$Document", item.DocumentNumber.Trim());
        command.Parameters.AddWithValue("$DocumentDate", item.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("$Amount", item.SettledAmount); command.Parameters.AddWithValue("$SettlementDate", item.SettlementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$Reference", item.Reference.Trim()); command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<FixedAssetRecord> GetFixedAssets(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM FixedAssets WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY AssetGroup COLLATE NOCASE,AssetName COLLATE NOCASE;";
        AddScope(command, clientId, financialYear);
        using var reader = command.ExecuteReader();
        var result = new List<FixedAssetRecord>();
        while (reader.Read())
        {
            var asset = new FixedAssetRecord
            {
                Id=ReadLong(reader,"Id"),ClientId=clientId,FinancialYear=financialYear,AssetCode=ReadString(reader,"AssetCode"),AssetName=ReadString(reader,"AssetName"),AssetGroup=ReadString(reader,"AssetGroup"),
                PurchaseDate=ReadDate(reader,"PurchaseDate"),PutToUseDate=ReadDate(reader,"PutToUseDate"),PurchaseCost=ReadDecimal(reader,"PurchaseCost"),ResidualValue=ReadDecimal(reader,"ResidualValue"),
                DepreciationMethod=ReadString(reader,"DepreciationMethod"),DepreciationRate=ReadDecimal(reader,"DepreciationRate"),OpeningAccumulatedDepreciation=ReadDecimal(reader,"OpeningAccumulatedDep"),
                DisposalDate=ReadDate(reader,"DisposalDate"),DisposalValue=ReadNullableDecimal(reader,"DisposalValue"),Location=ReadString(reader,"Location"),SerialNumber=ReadString(reader,"SerialNumber"),Notes=ReadString(reader,"Notes"),UpdatedUtc=ParseDateTime(ReadString(reader,"UpdatedUtc")),
                TallyLedgerName=ReadString(reader,"TallyLedgerName"),TallyLedgerMasterId=ReadString(reader,"TallyLedgerMasterId"),SupplierName=ReadString(reader,"SupplierName"),
                OpeningBalance=ReadDecimal(reader,"OpeningBalance"),OpeningSource=ReadString(reader,"OpeningSource")
            };
            asset.CurrentBookValue = CalculateBookValue(asset, DateTime.Today);
            result.Add(asset);
        }
        return result;
    }

    public void SaveFixedAsset(FixedAssetRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.AssetName)) throw new InvalidOperationException("Asset Name is required.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO FixedAssets
            (
                ClientId,FinancialYear,AssetCode,AssetName,AssetGroup,PurchaseDate,PutToUseDate,
                PurchaseCost,ResidualValue,DepreciationMethod,DepreciationRate,OpeningAccumulatedDep,
                DisposalDate,DisposalValue,Location,SerialNumber,Notes,UpdatedUtc,
                TallyLedgerName,TallyLedgerMasterId,SupplierName,OpeningBalance,OpeningSource
            )
            VALUES
            (
                $ClientId,$FinancialYear,$Code,$Name,$Group,$PurchaseDate,$PutToUseDate,
                $Cost,$Residual,$Method,$Rate,$OpeningDep,$DisposalDate,$DisposalValue,$Location,$Serial,$Notes,$Now,
                $TallyLedgerName,$TallyLedgerMasterId,$SupplierName,$OpeningBalance,$OpeningSource
            )
            ON CONFLICT(ClientId,FinancialYear,AssetCode,AssetName)
            DO UPDATE SET
                AssetGroup=excluded.AssetGroup,
                PurchaseDate=excluded.PurchaseDate,
                PutToUseDate=excluded.PutToUseDate,
                PurchaseCost=excluded.PurchaseCost,
                ResidualValue=excluded.ResidualValue,
                DepreciationMethod=excluded.DepreciationMethod,
                DepreciationRate=excluded.DepreciationRate,
                OpeningAccumulatedDep=excluded.OpeningAccumulatedDep,
                DisposalDate=excluded.DisposalDate,
                DisposalValue=excluded.DisposalValue,
                Location=excluded.Location,
                SerialNumber=excluded.SerialNumber,
                Notes=excluded.Notes,
                TallyLedgerName=excluded.TallyLedgerName,
                TallyLedgerMasterId=excluded.TallyLedgerMasterId,
                SupplierName=excluded.SupplierName,
                OpeningBalance=excluded.OpeningBalance,
                OpeningSource=excluded.OpeningSource,
                UpdatedUtc=excluded.UpdatedUtc;
            """;
        AddScope(command,item.ClientId,item.FinancialYear);
        command.Parameters.AddWithValue("$Code",item.AssetCode.Trim());
        command.Parameters.AddWithValue("$Name",item.AssetName.Trim());
        command.Parameters.AddWithValue("$Group",item.AssetGroup.Trim());
        command.Parameters.AddWithValue("$PurchaseDate",item.PurchaseDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??string.Empty);
        command.Parameters.AddWithValue("$PutToUseDate",item.PutToUseDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??string.Empty);
        command.Parameters.AddWithValue("$Cost",item.PurchaseCost);
        command.Parameters.AddWithValue("$Residual",item.ResidualValue);
        command.Parameters.AddWithValue("$Method",item.DepreciationMethod.Trim());
        command.Parameters.AddWithValue("$Rate",item.DepreciationRate);
        command.Parameters.AddWithValue("$OpeningDep",item.OpeningAccumulatedDepreciation);
        command.Parameters.AddWithValue("$DisposalDate",item.DisposalDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??string.Empty);
        command.Parameters.AddWithValue("$DisposalValue",(object?)item.DisposalValue??DBNull.Value);
        command.Parameters.AddWithValue("$Location",item.Location.Trim());
        command.Parameters.AddWithValue("$Serial",item.SerialNumber.Trim());
        command.Parameters.AddWithValue("$Notes",item.Notes.Trim());
        command.Parameters.AddWithValue("$TallyLedgerName",item.TallyLedgerName.Trim());
        command.Parameters.AddWithValue("$TallyLedgerMasterId",item.TallyLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$SupplierName",item.SupplierName.Trim());
        command.Parameters.AddWithValue("$OpeningBalance",item.OpeningBalance);
        command.Parameters.AddWithValue("$OpeningSource",item.OpeningSource.Trim());
        command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public FixedAssetDepreciationSettingsRecord GetFixedAssetDepreciationSettings(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DepreciationPolicy,DepreciationMethod,CommonDepreciationRate,
                   ExistingAssetCalculation,AdditionCalculation,
                   AdditionalDepreciationEnabled,AdditionalDepreciationRate,
                   DepreciationPosting,ExpenseLedgerName,ExpenseLedgerMasterId,VoucherRoundOffMode,UpdatedUtc
            FROM FixedAssetDepreciationSettings
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;
            """;
        AddScope(command,clientId,financialYear);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new FixedAssetDepreciationSettingsRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear.Trim()
            };
        }
        return new FixedAssetDepreciationSettingsRecord
        {
            ClientId=clientId,FinancialYear=financialYear.Trim(),
            DepreciationPolicy=reader.GetString(0),
            DepreciationMethod=reader.GetString(1),
            CommonDepreciationRate=ToDecimal(reader.GetValue(2)),
            ExistingAssetCalculation=reader.GetString(3),
            AdditionCalculation=reader.GetString(4),
            AdditionalDepreciationEnabled=reader.GetInt32(5)==1,
            AdditionalDepreciationRate=ToDecimal(reader.GetValue(6)),
            DepreciationPosting=reader.GetString(7),
            ExpenseLedgerName=reader.GetString(8),
            ExpenseLedgerMasterId=reader.GetString(9),
            VoucherRoundOffMode=reader.GetString(10),
            UpdatedUtc=ParseDateTime(reader.GetString(11))
        };
    }

    public void SaveFixedAssetDepreciationSettings(FixedAssetDepreciationSettingsRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO FixedAssetDepreciationSettings
            (
                ClientId,FinancialYear,DepreciationPolicy,DepreciationMethod,CommonDepreciationRate,
                ExistingAssetCalculation,AdditionCalculation,AdditionalDepreciationEnabled,
                AdditionalDepreciationRate,DepreciationPosting,ExpenseLedgerName,ExpenseLedgerMasterId,VoucherRoundOffMode,UpdatedUtc
            )
            VALUES
            (
                $ClientId,$FinancialYear,$Policy,$Method,$Rate,$ExistingCalc,$AdditionCalc,$AdditionalEnabled,
                $AdditionalRate,$Posting,$ExpenseLedger,$ExpenseMasterId,$RoundOffMode,$Now
            )
            ON CONFLICT(ClientId,FinancialYear) DO UPDATE SET
                DepreciationPolicy=excluded.DepreciationPolicy,
                DepreciationMethod=excluded.DepreciationMethod,
                CommonDepreciationRate=excluded.CommonDepreciationRate,
                ExistingAssetCalculation=excluded.ExistingAssetCalculation,
                AdditionCalculation=excluded.AdditionCalculation,
                AdditionalDepreciationEnabled=excluded.AdditionalDepreciationEnabled,
                AdditionalDepreciationRate=excluded.AdditionalDepreciationRate,
                DepreciationPosting=excluded.DepreciationPosting,
                ExpenseLedgerName=excluded.ExpenseLedgerName,
                ExpenseLedgerMasterId=excluded.ExpenseLedgerMasterId,
                VoucherRoundOffMode=excluded.VoucherRoundOffMode,
                UpdatedUtc=excluded.UpdatedUtc;
            """;
        AddScope(command,item.ClientId,item.FinancialYear);
        command.Parameters.AddWithValue("$Policy",item.DepreciationPolicy.Trim());
        command.Parameters.AddWithValue("$Method",item.DepreciationMethod.Trim());
        command.Parameters.AddWithValue("$Rate",item.CommonDepreciationRate);
        command.Parameters.AddWithValue("$ExistingCalc",item.ExistingAssetCalculation.Trim());
        command.Parameters.AddWithValue("$AdditionCalc",item.AdditionCalculation.Trim());
        command.Parameters.AddWithValue("$AdditionalEnabled",item.AdditionalDepreciationEnabled?1:0);
        command.Parameters.AddWithValue("$AdditionalRate",item.AdditionalDepreciationRate);
        command.Parameters.AddWithValue("$Posting",item.DepreciationPosting.Trim());
        command.Parameters.AddWithValue("$ExpenseLedger",item.ExpenseLedgerName.Trim());
        command.Parameters.AddWithValue("$ExpenseMasterId",item.ExpenseLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$RoundOffMode",string.IsNullOrWhiteSpace(item.VoucherRoundOffMode)?"No Round Off":item.VoucherRoundOffMode.Trim());
        command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<FixedAssetPostingLedgerMappingRecord> GetFixedAssetPostingLedgerMappings(long clientId, string financialYear)
    {
        using var connection=OpenConnection();
        using var command=connection.CreateCommand();
        command.CommandText=
            """
            SELECT AssetLedgerName,AssetLedgerMasterId,AccumulatedLedgerName,AccumulatedLedgerMasterId,UpdatedUtc
            FROM FixedAssetPostingLedgerMappings
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY AssetLedgerName COLLATE NOCASE;
            """;
        AddScope(command,clientId,financialYear);
        using var reader=command.ExecuteReader();
        var rows=new List<FixedAssetPostingLedgerMappingRecord>();
        while(reader.Read())
        {
            rows.Add(new FixedAssetPostingLedgerMappingRecord
            {
                ClientId=clientId,FinancialYear=financialYear.Trim(),
                AssetLedgerName=reader.GetString(0),AssetLedgerMasterId=reader.GetString(1),
                AccumulatedLedgerName=reader.GetString(2),AccumulatedLedgerMasterId=reader.GetString(3),
                UpdatedUtc=ParseDateTime(reader.GetString(4))
            });
        }
        return rows;
    }

    public void SaveFixedAssetPostingLedgerMapping(FixedAssetPostingLedgerMappingRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var connection=OpenConnection();
        using var command=connection.CreateCommand();
        command.CommandText=
            """
            INSERT INTO FixedAssetPostingLedgerMappings
            (ClientId,FinancialYear,AssetLedgerName,AssetLedgerMasterId,AccumulatedLedgerName,AccumulatedLedgerMasterId,UpdatedUtc)
            VALUES($ClientId,$FinancialYear,$AssetLedger,$AssetMasterId,$AccumLedger,$AccumMasterId,$Now)
            ON CONFLICT(ClientId,FinancialYear,AssetLedgerMasterId,AssetLedgerName)
            DO UPDATE SET
                AccumulatedLedgerName=excluded.AccumulatedLedgerName,
                AccumulatedLedgerMasterId=excluded.AccumulatedLedgerMasterId,
                UpdatedUtc=excluded.UpdatedUtc;
            """;
        AddScope(command,item.ClientId,item.FinancialYear);
        command.Parameters.AddWithValue("$AssetLedger",item.AssetLedgerName.Trim());
        command.Parameters.AddWithValue("$AssetMasterId",item.AssetLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$AccumLedger",item.AccumulatedLedgerName.Trim());
        command.Parameters.AddWithValue("$AccumMasterId",item.AccumulatedLedgerMasterId.Trim());
        command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BankRuleRecord> GetBankRules(long clientId, string financialYear)
    {
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="SELECT * FROM BankRules WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY Priority,RuleName COLLATE NOCASE;"; AddScope(command,clientId,financialYear);
        using var reader=command.ExecuteReader(); var result=new List<BankRuleRecord>();
        while(reader.Read()) result.Add(new BankRuleRecord{Id=ReadLong(reader,"Id"),ClientId=clientId,FinancialYear=financialYear,Priority=ReadInt(reader,"Priority"),RuleName=ReadString(reader,"RuleName"),BankContains=ReadString(reader,"BankContains"),Mode=ReadString(reader,"Mode"),NarrationContains=ReadString(reader,"NarrationContains"),NarrationStartsWith=ReadString(reader,"NarrationStartsWith"),ReferenceContains=ReadString(reader,"ReferenceContains"),NarrationRegex=ReadString(reader,"NarrationRegex"),MinAmount=ReadNullableDecimal(reader,"MinAmount"),MaxAmount=ReadNullableDecimal(reader,"MaxAmount"),LedgerName=ReadString(reader,"LedgerName"),LedgerGroup=ReadString(reader,"LedgerGroup"),IsActive=ReadInt(reader,"IsActive")==1,AppliedCount=ReadInt(reader,"AppliedCount"),UpdatedUtc=ParseDateTime(ReadString(reader,"UpdatedUtc"))});
        return result;
    }

    public void SaveBankRule(BankRuleRecord item)
    {
        if(string.IsNullOrWhiteSpace(item.RuleName)||string.IsNullOrWhiteSpace(item.LedgerName)) throw new InvalidOperationException("Rule Name and Final Ledger are required.");
        if(!string.IsNullOrWhiteSpace(item.NarrationRegex)) _=new Regex(item.NarrationRegex,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(200));
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="""
        INSERT INTO BankRules(ClientId,FinancialYear,Priority,RuleName,BankContains,Mode,NarrationContains,NarrationStartsWith,ReferenceContains,NarrationRegex,MinAmount,MaxAmount,LedgerName,LedgerGroup,IsActive,AppliedCount,UpdatedUtc)
        VALUES($ClientId,$FinancialYear,$Priority,$RuleName,$Bank,$Mode,$Contains,$Starts,$Reference,$Regex,$Min,$Max,$Ledger,$Group,$Active,0,$Now)
        ON CONFLICT(ClientId,FinancialYear,RuleName) DO UPDATE SET Priority=excluded.Priority,BankContains=excluded.BankContains,Mode=excluded.Mode,NarrationContains=excluded.NarrationContains,NarrationStartsWith=excluded.NarrationStartsWith,ReferenceContains=excluded.ReferenceContains,NarrationRegex=excluded.NarrationRegex,MinAmount=excluded.MinAmount,MaxAmount=excluded.MaxAmount,LedgerName=excluded.LedgerName,LedgerGroup=excluded.LedgerGroup,IsActive=excluded.IsActive,UpdatedUtc=excluded.UpdatedUtc;
        """;
        AddScope(command,item.ClientId,item.FinancialYear); command.Parameters.AddWithValue("$Priority",item.Priority);command.Parameters.AddWithValue("$RuleName",item.RuleName.Trim());command.Parameters.AddWithValue("$Bank",item.BankContains.Trim());command.Parameters.AddWithValue("$Mode",item.Mode.Trim());command.Parameters.AddWithValue("$Contains",item.NarrationContains.Trim());command.Parameters.AddWithValue("$Starts",item.NarrationStartsWith.Trim());command.Parameters.AddWithValue("$Reference",item.ReferenceContains.Trim());command.Parameters.AddWithValue("$Regex",item.NarrationRegex.Trim());command.Parameters.AddWithValue("$Min",(object?)item.MinAmount??DBNull.Value);command.Parameters.AddWithValue("$Max",(object?)item.MaxAmount??DBNull.Value);command.Parameters.AddWithValue("$Ledger",item.LedgerName.Trim());command.Parameters.AddWithValue("$Group",item.LedgerGroup.Trim());command.Parameters.AddWithValue("$Active",item.IsActive?1:0);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }

    public int ApplyBankRules(long clientId,string financialYear,bool onlyUnmapped=true)
    {
        var rules=GetBankRules(clientId,financialYear).Where(x=>x.IsActive).OrderBy(x=>x.Priority).ThenBy(x=>x.Id).ToArray();
        if(rules.Length==0)return 0;
        using var connection=OpenConnection();
        var txs=new List<(long Id,string Bank,string Mode,string Narration,string Reference,decimal Amount,string Ledger)>();
        using(var command=connection.CreateCommand())
        {
            command.CommandText="""
            SELECT t.Id,a.BankName,t.Mode,t.Narration,t.ReferenceNumber,CASE WHEN t.Debit<>0 THEN ABS(t.Debit) ELSE ABS(t.Credit) END Amount,t.LedgerName
            FROM BankTransactions t JOIN BankAccounts a ON a.Id=t.BankAccountId
            WHERE t.ClientId=$ClientId AND t.FinancialYear=$FinancialYear AND t.RecordStatus='Active' COLLATE NOCASE;
            """;
            AddScope(command,clientId,financialYear);
            using var reader=command.ExecuteReader();
            while(reader.Read())txs.Add((reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?string.Empty:reader.GetString(4),ToDecimal(reader.GetValue(5)),reader.GetString(6)));
        }
        var applied=0; using var transaction=connection.BeginTransaction();
        foreach(var tx in txs)
        {
            if(onlyUnmapped&&!string.IsNullOrWhiteSpace(tx.Ledger))continue;
            var rule=rules.FirstOrDefault(r=>RuleMatches(r,tx.Bank,tx.Mode,tx.Narration,tx.Reference,tx.Amount)); if(rule is null)continue;
            using var update=connection.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE BankTransactions SET LedgerName=$Ledger,MappingStatus='Mapped' WHERE Id=$Id;";update.Parameters.AddWithValue("$Ledger",rule.LedgerName);update.Parameters.AddWithValue("$Id",tx.Id);applied+=update.ExecuteNonQuery();
            using var touch=connection.CreateCommand();touch.Transaction=transaction;touch.CommandText="UPDATE BankRules SET AppliedCount=AppliedCount+1,UpdatedUtc=$Now WHERE Id=$Id;";touch.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));touch.Parameters.AddWithValue("$Id",rule.Id);touch.ExecuteNonQuery();
        }
        transaction.Commit(); return applied;
    }

    public IReadOnlyList<FinanceBudgetRecord> GetBudgets(long clientId,string financialYear)
    {
        using var connection = OpenConnection();
        var basics = new List<(long Id,string Period,string Category,decimal BudgetAmount,DateTime UpdatedUtc)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id,Period,Category,BudgetAmount,UpdatedUtc FROM FinanceBudgets WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY Period,Category COLLATE NOCASE;";
            AddScope(command,clientId,financialYear);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                basics.Add((reader.GetInt64(0),reader.GetString(1),reader.GetString(2),ToDecimal(reader.GetValue(3)),ParseDateTime(reader.GetString(4))));
        }

        var result = new List<FinanceBudgetRecord>(basics.Count);
        foreach (var row in basics)
            result.Add(new FinanceBudgetRecord
            {
                Id=row.Id,ClientId=clientId,FinancialYear=financialYear,Period=row.Period,Category=row.Category,
                BudgetAmount=row.BudgetAmount,ActualAmount=GetBudgetActual(connection,clientId,financialYear,row.Period,row.Category),UpdatedUtc=row.UpdatedUtc
            });
        return result;
    }

    public void SaveBudget(FinanceBudgetRecord item)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="""INSERT INTO FinanceBudgets(ClientId,FinancialYear,Period,Category,BudgetAmount,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Period,$Category,$Amount,$Now) ON CONFLICT(ClientId,FinancialYear,Period,Category) DO UPDATE SET BudgetAmount=excluded.BudgetAmount,UpdatedUtc=excluded.UpdatedUtc;""";AddScope(command,item.ClientId,item.FinancialYear);command.Parameters.AddWithValue("$Period",item.Period.Trim());command.Parameters.AddWithValue("$Category",item.Category.Trim());command.Parameters.AddWithValue("$Amount",item.BudgetAmount);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }

    public IReadOnlyList<RecurringCashFlowTemplate> GetRecurringTemplates(long clientId,string financialYear)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="SELECT * FROM RecurringCashFlowTemplates WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY Name COLLATE NOCASE;";AddScope(command,clientId,financialYear);using var reader=command.ExecuteReader();var result=new List<RecurringCashFlowTemplate>();while(reader.Read())result.Add(new RecurringCashFlowTemplate{Id=ReadLong(reader,"Id"),ClientId=clientId,FinancialYear=financialYear,Name=ReadString(reader,"Name"),Direction=ReadString(reader,"Direction"),Amount=ReadDecimal(reader,"Amount"),DayOfMonth=ReadInt(reader,"DayOfMonth"),StartDate=ReadDate(reader,"StartDate")??DateTime.Today,EndDate=ReadDate(reader,"EndDate"),LedgerName=ReadString(reader,"LedgerName"),IsActive=ReadInt(reader,"IsActive")==1,UpdatedUtc=ParseDateTime(ReadString(reader,"UpdatedUtc"))});return result;
    }

    public void SaveRecurringTemplate(RecurringCashFlowTemplate item)
    {
        if(string.IsNullOrWhiteSpace(item.Name))throw new InvalidOperationException("Recurring item Name is required.");using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="""INSERT INTO RecurringCashFlowTemplates(ClientId,FinancialYear,Name,Direction,Amount,DayOfMonth,StartDate,EndDate,LedgerName,IsActive,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Name,$Direction,$Amount,$Day,$Start,$End,$Ledger,$Active,$Now) ON CONFLICT(ClientId,FinancialYear,Name) DO UPDATE SET Direction=excluded.Direction,Amount=excluded.Amount,DayOfMonth=excluded.DayOfMonth,StartDate=excluded.StartDate,EndDate=excluded.EndDate,LedgerName=excluded.LedgerName,IsActive=excluded.IsActive,UpdatedUtc=excluded.UpdatedUtc;""";AddScope(command,item.ClientId,item.FinancialYear);command.Parameters.AddWithValue("$Name",item.Name.Trim());command.Parameters.AddWithValue("$Direction",item.Direction.Trim());command.Parameters.AddWithValue("$Amount",item.Amount);command.Parameters.AddWithValue("$Day",Math.Clamp(item.DayOfMonth,1,28));command.Parameters.AddWithValue("$Start",item.StartDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));command.Parameters.AddWithValue("$End",item.EndDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??string.Empty);command.Parameters.AddWithValue("$Ledger",item.LedgerName.Trim());command.Parameters.AddWithValue("$Active",item.IsActive?1:0);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }

    public IReadOnlyList<FinanceApprovalItem> GetApprovals(long clientId,string financialYear)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="SELECT * FROM FinanceApprovals WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY CASE Status WHEN 'Pending' THEN 0 ELSE 1 END,Id DESC;";AddScope(command,clientId,financialYear);using var reader=command.ExecuteReader();var result=new List<FinanceApprovalItem>();while(reader.Read())result.Add(new FinanceApprovalItem{Id=ReadLong(reader,"Id"),ClientId=clientId,FinancialYear=financialYear,Module=ReadString(reader,"Module"),ActionType=ReadString(reader,"ActionType"),Reference=ReadString(reader,"Reference"),Amount=ReadDecimal(reader,"Amount"),RequestedBy=ReadString(reader,"RequestedBy"),Status=ReadString(reader,"Status"),DecisionNote=ReadString(reader,"DecisionNote"),CreatedUtc=ParseDateTime(ReadString(reader,"CreatedUtc")),UpdatedUtc=ParseDateTime(ReadString(reader,"UpdatedUtc"))});return result;
    }

    public void SaveApproval(FinanceApprovalItem item)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText=item.Id<=0?"""INSERT INTO FinanceApprovals(ClientId,FinancialYear,Module,ActionType,Reference,Amount,RequestedBy,Status,DecisionNote,CreatedUtc,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Module,$Action,$Reference,$Amount,$RequestedBy,$Status,$Note,$Now,$Now);""":"""UPDATE FinanceApprovals SET Status=$Status,DecisionNote=$Note,UpdatedUtc=$Now WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear;""";AddScope(command,item.ClientId,item.FinancialYear);command.Parameters.AddWithValue("$Module",item.Module.Trim());command.Parameters.AddWithValue("$Action",item.ActionType.Trim());command.Parameters.AddWithValue("$Reference",item.Reference.Trim());command.Parameters.AddWithValue("$Amount",item.Amount);command.Parameters.AddWithValue("$RequestedBy",item.RequestedBy.Trim());command.Parameters.AddWithValue("$Status",string.IsNullOrWhiteSpace(item.Status)?"Pending":item.Status.Trim());command.Parameters.AddWithValue("$Note",item.DecisionNote.Trim());command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));if(item.Id>0)command.Parameters.AddWithValue("$Id",item.Id);command.ExecuteNonQuery();
    }

    public IReadOnlyList<InventoryPolicyRecord> GetInventoryPolicies(long clientId,string financialYear)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="SELECT * FROM InventoryPolicies WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY StockItemName COLLATE NOCASE;";AddScope(command,clientId,financialYear);using var reader=command.ExecuteReader();var result=new List<InventoryPolicyRecord>();while(reader.Read())result.Add(new InventoryPolicyRecord{Id=ReadLong(reader,"Id"),ClientId=clientId,FinancialYear=financialYear,StockItemName=ReadString(reader,"StockItemName"),OpeningQuantity=ReadDecimal(reader,"OpeningQuantity"),ReorderLevel=ReadDecimal(reader,"ReorderLevel"),MinimumStock=ReadDecimal(reader,"MinimumStock"),MaximumStock=ReadDecimal(reader,"MaximumStock"),ExpiryWarningDays=ReadInt(reader,"ExpiryWarningDays"),UpdatedUtc=ParseDateTime(ReadString(reader,"UpdatedUtc"))});return result;
    }

    public void SaveInventoryPolicy(InventoryPolicyRecord item)
    {
        if(string.IsNullOrWhiteSpace(item.StockItemName))throw new InvalidOperationException("Stock Item is required.");using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="""INSERT INTO InventoryPolicies(ClientId,FinancialYear,StockItemName,OpeningQuantity,ReorderLevel,MinimumStock,MaximumStock,ExpiryWarningDays,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Item,$Opening,$Reorder,$Min,$Max,$Expiry,$Now) ON CONFLICT(ClientId,FinancialYear,StockItemName) DO UPDATE SET OpeningQuantity=excluded.OpeningQuantity,ReorderLevel=excluded.ReorderLevel,MinimumStock=excluded.MinimumStock,MaximumStock=excluded.MaximumStock,ExpiryWarningDays=excluded.ExpiryWarningDays,UpdatedUtc=excluded.UpdatedUtc;""";AddScope(command,item.ClientId,item.FinancialYear);command.Parameters.AddWithValue("$Item",item.StockItemName.Trim());command.Parameters.AddWithValue("$Opening",item.OpeningQuantity);command.Parameters.AddWithValue("$Reorder",item.ReorderLevel);command.Parameters.AddWithValue("$Min",item.MinimumStock);command.Parameters.AddWithValue("$Max",item.MaximumStock);command.Parameters.AddWithValue("$Expiry",Math.Clamp(item.ExpiryWarningDays,0,3650));command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }

    public IReadOnlyList<InventoryIntelligenceRow> GetInventoryIntelligence(long clientId,string financialYear)
    {
        var policies=GetInventoryPolicies(clientId,financialYear).ToDictionary(x=>x.StockItemName,StringComparer.OrdinalIgnoreCase);
        var sales=_salesRepository.GetRecords(clientId,financialYear);
        var groups=sales.Where(x=>!string.IsNullOrWhiteSpace(x.ItemName)).GroupBy(x=>x.ItemName,StringComparer.OrdinalIgnoreCase);
        var result=new List<InventoryIntelligenceRow>();
        foreach(var group in groups)
        {
            policies.TryGetValue(group.Key,out var policy);
            var sold=group.Sum(x=>Math.Abs(x.ActualQuantity));
            var opening=policy?.OpeningQuantity??0m;var available=opening-sold;
            var expiry=group.Where(x=>x.ExpiryDate.HasValue).Select(x=>x.ExpiryDate!.Value).OrderBy(x=>x).FirstOrDefault();
            DateTime? nearest=expiry==default?null:expiry;
            var alert=string.Empty;
            if(policy is not null&&policy.ReorderLevel>0&&available<=policy.ReorderLevel)alert="Reorder";
            if(nearest.HasValue&&nearest.Value<=DateTime.Today.AddDays(policy?.ExpiryWarningDays??60))alert=alert.Length==0?"Expiry warning":alert+" + Expiry";
            result.Add(new InventoryIntelligenceRow{StockItemName=group.Key,Unit=group.Select(x=>x.Unit).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x))??string.Empty,OpeningQuantity=opening,SalesQuantity=sold,EstimatedAvailable=available,ReorderLevel=policy?.ReorderLevel??0m,Alert=alert,NearestExpiry=nearest});
        }
        foreach(var policy in policies.Values.Where(p=>!result.Any(r=>r.StockItemName.Equals(p.StockItemName,StringComparison.OrdinalIgnoreCase))))result.Add(new InventoryIntelligenceRow{StockItemName=policy.StockItemName,OpeningQuantity=policy.OpeningQuantity,EstimatedAvailable=policy.OpeningQuantity,ReorderLevel=policy.ReorderLevel,Alert=policy.ReorderLevel>0&&policy.OpeningQuantity<=policy.ReorderLevel?"Reorder":string.Empty});
        return result.OrderByDescending(x=>x.Alert.Length>0).ThenBy(x=>x.StockItemName,StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<DocumentInboxRow> GetDocumentInbox(long clientId,string financialYear,int limit=500)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="""SELECT FilePath,FileType,Status,Detail,ProcessedUtc FROM AutomationFileHistory WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY Id DESC LIMIT $Limit;""";AddScope(command,clientId,financialYear);command.Parameters.AddWithValue("$Limit",Math.Clamp(limit,25,5000));using var reader=command.ExecuteReader();var result=new List<DocumentInboxRow>();while(reader.Read()){var path=reader.GetString(0);result.Add(new DocumentInboxRow{FilePath=path,FileName=Path.GetFileName(path),FileType=reader.GetString(1),Status=reader.GetString(2),Detail=reader.GetString(3),ProcessedUtc=ParseDateTime(reader.GetString(4))});}return result;
    }

    public IReadOnlyList<FinanceSearchResult> Search(long clientId,string financialYear,string search,int limit=250)
    {
        search=(search??string.Empty).Trim();if(search.Length<2)return Array.Empty<FinanceSearchResult>();var pattern=$"%{search}%";
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="""
        SELECT 'Banking','Transaction',substr(TransactionDate,1,10),Narration,COALESCE(NULLIF(ReferenceNumber,''),TransactionId),CASE WHEN Debit<>0 THEN Debit ELSE Credit END FROM BankTransactions WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (Narration LIKE $P OR ReferenceNumber LIKE $P OR LedgerName LIKE $P)
        UNION ALL SELECT 'Sales','Invoice',substr(DocumentDate,1,10),CustomerName,DocumentNumber,DocumentValue FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (CustomerName LIKE $P OR CustomerGstin LIKE $P OR DocumentNumber LIKE $P OR SalesLedger LIKE $P)
        UNION ALL SELECT 'Purchase','Invoice',substr(DocumentDate,1,10),SupplierName,DocumentNumber,DocumentValue FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (SupplierName LIKE $P OR SupplierGstin LIKE $P OR DocumentNumber LIKE $P OR PurchaseLedger LIKE $P)
        UNION ALL SELECT 'Masters',Type,'',Name,Parent,NULL FROM Masters WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (Name LIKE $P OR Parent LIKE $P OR Type LIKE $P)
        UNION ALL SELECT 'Stock Master','Stock Item','',Name,StockGroup,NULL FROM StockItems WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (Name LIKE $P OR StockGroup LIKE $P OR HsnSac LIKE $P)
        UNION ALL SELECT 'Cash Entry','Cash',substr(EntryDate,1,10),Narration,EntryLedger,CASE WHEN Debit<>0 THEN Debit ELSE Credit END FROM CashEntries WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (Narration LIKE $P OR EntryLedger LIKE $P OR MappedMasterLedger LIKE $P)
        UNION ALL SELECT 'Journal','Voucher',substr(VoucherDate,1,10),Narration,VoucherNumber,(SELECT COALESCE(SUM(ABS(L.Amount)),0)/2 FROM JournalLines L WHERE L.VoucherId=JournalVouchers.Id) FROM JournalVouchers WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (Narration LIKE $P OR VoucherNumber LIKE $P OR PeriodLabel LIKE $P OR EXISTS(SELECT 1 FROM JournalLines L WHERE L.VoucherId=JournalVouchers.Id AND (L.LedgerName LIKE $P OR L.TaxHead LIKE $P)))
        UNION ALL SELECT 'GST','GSTR-3B',COALESCE(substr(ArnDate,1,10),''),COALESCE(NULLIF(TradeName,''),NULLIF(LegalName,''),Gstin),COALESCE(NULLIF(Arn,''),PeriodLabel),OutwardTaxable FROM Gstr3bReturns WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (Gstin LIKE $P OR LegalName LIKE $P OR TradeName LIKE $P OR Arn LIKE $P OR PeriodLabel LIKE $P OR SourcePath LIKE $P)
        UNION ALL SELECT 'Action Center','Exception',substr(CreatedUtc,1,10),Title,ActionType,NULL FROM ActionRequired WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND (Title LIKE $P OR ActionType LIKE $P OR SourceKey LIKE $P OR Detail LIKE $P)
        LIMIT $Limit;
        """;
        AddScope(command,clientId,financialYear);
        command.Parameters.AddWithValue("$P",pattern);
        command.Parameters.AddWithValue("$Limit",Math.Clamp(limit,25,1000));
        using var reader=command.ExecuteReader();
        var result=new List<FinanceSearchResult>();
        while(reader.Read())result.Add(new FinanceSearchResult{Module=reader.GetString(0),Type=reader.GetString(1),Date=ParseDate(reader.GetString(2)),PrimaryText=reader.GetString(3),Reference=reader.GetString(4),SecondaryText=reader.GetString(4),Amount=reader.IsDBNull(5)?null:ToDecimal(reader.GetValue(5))});
        return result;
    }

    public IReadOnlyList<FinanceKpiRow> GetKpis(long clientId,string financialYear)
    {
        using var connection=OpenConnection();
        var result=new List<FinanceKpiRow>();
        result.Add(Kpi(connection,"Bank exceptions",ScalarInt(connection,"SELECT COUNT(*) FROM BankTransactions WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND RecordStatus='Active' AND (TRIM(LedgerName)='' OR MappingStatus<>'Mapped');",clientId,financialYear),"Unmapped active bank transactions"));
        result.Add(Kpi(connection,"Open actions",ScalarInt(connection,"SELECT COUNT(*) FROM ActionRequired WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Open' COLLATE NOCASE;",clientId,financialYear),"Blocked/failed imports needing attention"));
        result.Add(Kpi(connection,"Pending approvals",ScalarInt(connection,"SELECT COUNT(*) FROM FinanceApprovals WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Pending';",clientId,financialYear),"Maker-checker queue"));
        result.Add(Kpi(connection,"Active period locks",ScalarInt(connection,"SELECT COUNT(*) FROM AccountingPeriodLocks WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND IsActive=1;",clientId,financialYear),"Closed accounting modules/periods"));
        result.Add(Kpi(connection,"Fixed assets",ScalarInt(connection,"SELECT COUNT(*) FROM FixedAssets WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;",clientId,financialYear),"Assets in register"));
        return result;
    }



    public IReadOnlyList<BankRulePreviewRow> PreviewBankRule(BankRuleRecord rule, int limit = 250)
    {
        if (rule.ClientId <= 0 || string.IsNullOrWhiteSpace(rule.FinancialYear)) return Array.Empty<BankRulePreviewRow>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Id,t.TransactionDate,a.BankName,t.Mode,t.Narration,t.ReferenceNumber,
                   CASE WHEN t.Debit<>0 THEN ABS(t.Debit) ELSE ABS(t.Credit) END Amount,t.LedgerName
            FROM BankTransactions t
            JOIN BankAccounts a ON a.Id=t.BankAccountId
            WHERE t.ClientId=$ClientId AND t.FinancialYear=$FinancialYear AND t.RecordStatus='Active' COLLATE NOCASE
            ORDER BY t.TransactionDate,t.Id;
            """;
        AddScope(command, rule.ClientId, rule.FinancialYear);
        using var reader = command.ExecuteReader();
        var result = new List<BankRulePreviewRow>();
        while (reader.Read() && result.Count < Math.Clamp(limit, 1, 5000))
        {
            var bank = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var mode = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var narration = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var reference = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            var amount = reader.IsDBNull(6) ? 0m : ToDecimal(reader.GetValue(6));
            if (!RuleMatches(rule, bank, mode, narration, reference, amount)) continue;
            result.Add(new BankRulePreviewRow
            {
                TransactionId=reader.GetInt64(0),Date=ParseDate(reader.IsDBNull(1)?string.Empty:reader.GetString(1)),Bank=bank,Mode=mode,
                Narration=narration,Amount=amount,CurrentLedger=reader.IsDBNull(7)?string.Empty:reader.GetString(7),ProposedLedger=rule.LedgerName
            });
        }
        return result;
    }

    public IReadOnlyList<FinanceIntegrityCheckRow> GetIntegrityChecks(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        var rows = new List<FinanceIntegrityCheckRow>();
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(integrity.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
            rows.Add(new FinanceIntegrityCheckRow { Check="SQLite database",Status=result.Equals("ok",StringComparison.OrdinalIgnoreCase)?"Ready":"Review",Detail=result });
        }
        using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_key_check;";
            using var reader = fk.ExecuteReader();
            rows.Add(new FinanceIntegrityCheckRow { Check="Foreign keys",Status=reader.Read()?"Review":"Ready",Detail=reader.HasRows?"One or more orphan references were found.":"No orphan references found." });
        }
        var duplicateLedgers = ScalarInt(connection,"SELECT COUNT(*) FROM (SELECT UPPER(TRIM(Name)) K,COUNT(*) C FROM Masters WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND IsArchived=0 AND Type='Ledger' COLLATE NOCASE GROUP BY UPPER(TRIM(Name)) HAVING C>1);",clientId,financialYear);
        rows.Add(new FinanceIntegrityCheckRow{Check="Duplicate ledgers",Status=duplicateLedgers==0?"Ready":"Review",Detail=duplicateLedgers==0?"No duplicate active ledgers.":$"{duplicateLedgers:N0} duplicate ledger identity group(s)."});
        var duplicateStock = ScalarInt(connection,"SELECT COUNT(*) FROM (SELECT UPPER(TRIM(Name)) K,COUNT(*) C FROM StockItems WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND IsArchived=0 GROUP BY UPPER(TRIM(Name)) HAVING C>1);",clientId,financialYear);
        rows.Add(new FinanceIntegrityCheckRow{Check="Duplicate stock items",Status=duplicateStock==0?"Ready":"Review",Detail=duplicateStock==0?"No duplicate active stock items.":$"{duplicateStock:N0} duplicate stock identity group(s)."});
        var processing = ScalarInt(connection,"SELECT COUNT(*) FROM AutomationFileHistory WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Processing' COLLATE NOCASE AND datetime(ProcessedUtc)<datetime('now','-10 minutes');",clientId,financialYear);
        rows.Add(new FinanceIntegrityCheckRow{Check="Stale Auto Import jobs",Status=processing==0?"Ready":"Review",Detail=processing==0?"No stale processing entries.":$"{processing:N0} file(s) have been Processing for more than 10 minutes."});
        return rows;
    }

    public void RecordAudit(long clientId,string financialYear,string module,string entityType,string entityKey,string operation,string oldValue,string newValue,string source="Manual")
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="""
        INSERT INTO FinanceAuditEvents(ClientId,FinancialYear,Module,EntityType,EntityKey,Operation,OldValue,NewValue,Source,ChangedUtc)
        VALUES($ClientId,$FinancialYear,$Module,$Type,$Key,$Operation,$Old,$New,$Source,$Now);
        """;
        AddScope(command,clientId,financialYear);
        command.Parameters.AddWithValue("$Module",module.Trim());
        command.Parameters.AddWithValue("$Type",entityType.Trim());
        command.Parameters.AddWithValue("$Key",entityKey.Trim());
        command.Parameters.AddWithValue("$Operation",operation.Trim());
        command.Parameters.AddWithValue("$Old",oldValue??string.Empty);
        command.Parameters.AddWithValue("$New",newValue??string.Empty);
        command.Parameters.AddWithValue("$Source",source??string.Empty);
        command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeleteFixedAsset(long id,long clientId,string financialYear)=>DeleteById("FixedAssets",id,clientId,financialYear);
    public void DeleteBankRule(long id,long clientId,string financialYear)=>DeleteById("BankRules",id,clientId,financialYear);
    public void DeleteBudget(long id,long clientId,string financialYear)=>DeleteById("FinanceBudgets",id,clientId,financialYear);
    public void DeleteRecurringTemplate(long id,long clientId,string financialYear)=>DeleteById("RecurringCashFlowTemplates",id,clientId,financialYear);
    public void DeleteApproval(long id,long clientId,string financialYear)=>DeleteById("FinanceApprovals",id,clientId,financialYear);
    public void DeleteInventoryPolicy(long id,long clientId,string financialYear)=>DeleteById("InventoryPolicies",id,clientId,financialYear);

    private void DeleteById(string table,long id,long clientId,string financialYear)
    {
        if(id<=0)return;
        var allowed = new HashSet<string>(StringComparer.Ordinal){"FixedAssets","BankRules","FinanceBudgets","RecurringCashFlowTemplates","FinanceApprovals","InventoryPolicies"};
        if(!allowed.Contains(table))throw new InvalidOperationException("Unsupported finance control table.");
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText=$"DELETE FROM {table} WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear;";command.Parameters.AddWithValue("$Id",id);AddScope(command,clientId,financialYear);command.ExecuteNonQuery();
    }
    private static FinanceKpiRow Kpi(SqliteConnection _,string name,int count,string detail)=>new(){Name=name,Value=count.ToString("N0",CultureInfo.InvariantCulture),Detail=detail};

    private static MonthEndCloseCheck CheckCount(SqliteConnection connection,long clientId,string fy,string area,string check,string sql,string date,string detail)
    {
        try{using var command=connection.CreateCommand();command.CommandText=sql;AddScope(command,clientId,fy);command.Parameters.AddWithValue("$Date",date);var count=Convert.ToInt32(command.ExecuteScalar(),CultureInfo.InvariantCulture);return new MonthEndCloseCheck{Area=area,Check=check,Count=count,Status=count==0?"Ready":"Review",Detail=count==0?"No exception found.":detail};}
        catch(Exception ex){return new MonthEndCloseCheck{Area=area,Check=check,Count=0,Status="Unavailable",Detail=ex.Message};}
    }

    private static void ReadAgingSide(SqliteConnection connection,long clientId,string fy,string side,string table,string partyColumn,List<FinanceAgingRow> result)
    {
        using var command=connection.CreateCommand();command.CommandText=$"SELECT {partyColumn},DocumentNumber,MIN(DocumentDate),SUM(ABS(DocumentValue)) FROM {table} WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear GROUP BY {partyColumn},DocumentNumber ORDER BY MIN(DocumentDate);";AddScope(command,clientId,fy);using var reader=command.ExecuteReader();var docs=new List<(string Party,string Number,DateTime? Date,decimal Amount)>();while(reader.Read())docs.Add((reader.IsDBNull(0)?string.Empty:reader.GetString(0),reader.IsDBNull(1)?string.Empty:reader.GetString(1),ParseDate(reader.IsDBNull(2)?string.Empty:reader.GetString(2)),reader.IsDBNull(3)?0m:ToDecimal(reader.GetValue(3))));reader.Close();
        foreach(var doc in docs){var settled=GetSettled(connection,clientId,fy,side,doc.Party,doc.Number,doc.Date);var outstanding=Math.Max(0m,doc.Amount-settled);if(outstanding<=0.005m)continue;var age=doc.Date.HasValue?Math.Max(0,(DateTime.Today-doc.Date.Value.Date).Days):0;result.Add(new FinanceAgingRow{Side=side,Party=doc.Party,DocumentNumber=doc.Number,DocumentDate=doc.Date,OriginalAmount=doc.Amount,SettledAmount=settled,AgeDays=age,Bucket=AgeBucket(age),Status=settled>0?"Part Settled":"Open"});}
    }

    private static decimal GetSettled(SqliteConnection connection,long clientId,string fy,string side,string party,string number,DateTime? date)
    {
        using var command=connection.CreateCommand();command.CommandText="SELECT COALESCE(SUM(SettledAmount),0) FROM FinanceSettlements WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Side=$Side COLLATE NOCASE AND Party=$Party COLLATE NOCASE AND DocumentNumber=$Document COLLATE NOCASE AND ($Date='' OR DocumentDate=$Date);";AddScope(command,clientId,fy);command.Parameters.AddWithValue("$Side",side);command.Parameters.AddWithValue("$Party",party);command.Parameters.AddWithValue("$Document",number);command.Parameters.AddWithValue("$Date",date?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??string.Empty);return ToDecimal(command.ExecuteScalar());
    }

    private static string AgeBucket(int days)=>days<=30?"0-30":days<=60?"31-60":days<=90?"61-90":days<=180?"91-180":"180+";

    private static decimal CalculateBookValue(FixedAssetRecord asset,DateTime asOf)
    {
        if(asset.PurchaseCost<=0)return 0m;var start=asset.PutToUseDate??asset.PurchaseDate;if(!start.HasValue||start.Value>asOf)return asset.PurchaseCost;var end=asset.DisposalDate.HasValue&&asset.DisposalDate.Value<asOf?asset.DisposalDate.Value:asOf;var years=Math.Max(0d,(end-start.Value).TotalDays/365.25d);decimal dep;
        if(asset.DepreciationMethod.Equals("WDV",StringComparison.OrdinalIgnoreCase)){var factor=(double)(1m-Math.Clamp(asset.DepreciationRate,0m,100m)/100m);var book=(decimal)((double)asset.PurchaseCost*Math.Pow(Math.Max(0d,factor),years));dep=asset.PurchaseCost-book;}
        else dep=(asset.PurchaseCost-asset.ResidualValue)*Math.Clamp(asset.DepreciationRate,0m,100m)/100m*(decimal)years;
        dep+=asset.OpeningAccumulatedDepreciation;return Math.Max(asset.ResidualValue,asset.PurchaseCost-dep);
    }

    private static bool RuleMatches(BankRuleRecord rule,string bank,string mode,string narration,string reference,decimal amount)
    {
        if(rule.BankContains.Length>0&&!bank.Contains(rule.BankContains,StringComparison.OrdinalIgnoreCase))return false;
        if(rule.Mode.Length>0&&!mode.Equals(rule.Mode,StringComparison.OrdinalIgnoreCase))return false;
        if(rule.NarrationContains.Length>0&&!narration.Contains(rule.NarrationContains,StringComparison.OrdinalIgnoreCase))return false;
        if(rule.NarrationStartsWith.Length>0&&!narration.TrimStart().StartsWith(rule.NarrationStartsWith,StringComparison.OrdinalIgnoreCase))return false;
        if(rule.ReferenceContains.Length>0&&!reference.Contains(rule.ReferenceContains,StringComparison.OrdinalIgnoreCase))return false;
        if(rule.MinAmount.HasValue&&amount<rule.MinAmount.Value)return false;if(rule.MaxAmount.HasValue&&amount>rule.MaxAmount.Value)return false;
        if(rule.NarrationRegex.Length>0){try{if(!Regex.IsMatch(narration,rule.NarrationRegex,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(200)))return false;}catch{return false;}}
        return true;
    }

    private static decimal GetBudgetActual(SqliteConnection connection,long clientId,string fy,string period,string category)
    {
        var month=ParseBudgetPeriod(period);if(month is null)return 0m;var start=month.Value.ToString("yyyy-MM-01",CultureInfo.InvariantCulture);var end=month.Value.AddMonths(1).ToString("yyyy-MM-01",CultureInfo.InvariantCulture);string sql=category.Trim().ToUpperInvariant() switch{"SALES" or "REVENUE"=>"SELECT COALESCE(SUM(DocumentValue),0) FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND date(DocumentDate)>=date($Start) AND date(DocumentDate)<date($End);","PURCHASE" or "PURCHASES" or "EXPENSE" or "EXPENSES"=>"SELECT COALESCE(SUM(DocumentValue),0) FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND date(DocumentDate)>=date($Start) AND date(DocumentDate)<date($End);","BANK INFLOW"=>"SELECT COALESCE(SUM(Credit),0) FROM BankTransactions WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND RecordStatus='Active' AND date(TransactionDate)>=date($Start) AND date(TransactionDate)<date($End);","BANK OUTFLOW"=>"SELECT COALESCE(SUM(Debit),0) FROM BankTransactions WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND RecordStatus='Active' AND date(TransactionDate)>=date($Start) AND date(TransactionDate)<date($End);",_=>string.Empty};if(sql.Length==0)return 0m;using var command=connection.CreateCommand();command.CommandText=sql;AddScope(command,clientId,fy);command.Parameters.AddWithValue("$Start",start);command.Parameters.AddWithValue("$End",end);return ToDecimal(command.ExecuteScalar());
    }

    private static DateTime? ParseBudgetPeriod(string value)
    {
        foreach(var format in new[]{"yyyy-MM","MMM yyyy","MMMM yyyy"})if(DateTime.TryParseExact(value.Trim(),format,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out var date))return new DateTime(date.Year,date.Month,1);return null;
    }

    private static DateTime? FinancialYearStart(string fy)
    {
        var match=Regex.Match(fy??string.Empty,@"(?<y>20\d{2})");if(!match.Success)return null;return int.TryParse(match.Groups["y"].Value,out var year)?new DateTime(year,4,1):null;
    }

    private static int ScalarInt(SqliteConnection connection,string sql,long clientId,string fy){using var command=connection.CreateCommand();command.CommandText=sql;AddScope(command,clientId,fy);return Convert.ToInt32(command.ExecuteScalar(),CultureInfo.InvariantCulture);}

    private static void CreateAuditTriggers(SqliteConnection connection)
    {
        var specs=new[]{
            (Table:"BankTransactions",Module:"Banking",Key:"COALESCE(NEW.TransactionId,CAST(NEW.Id AS TEXT))",Old:"COALESCE(OLD.LedgerName,'')||'|'||COALESCE(OLD.MappingStatus,'')",New:"COALESCE(NEW.LedgerName,'')||'|'||COALESCE(NEW.MappingStatus,'')"),
            (Table:"CashEntries",Module:"Cash Entry",Key:"COALESCE(NEW.EntryId,CAST(NEW.Id AS TEXT))",Old:"COALESCE(OLD.EntryLedger,'')||'|'||COALESCE(OLD.MappedMasterLedger,'')",New:"COALESCE(NEW.EntryLedger,'')||'|'||COALESCE(NEW.MappedMasterLedger,'')"),
            (Table:"SalesRecords",Module:"Sales",Key:"COALESCE(NEW.DocumentNumber,CAST(NEW.Id AS TEXT))",Old:"COALESCE(OLD.CustomerName,'')||'|'||OLD.DocumentValue",New:"COALESCE(NEW.CustomerName,'')||'|'||NEW.DocumentValue"),
            (Table:"PurchaseRecords",Module:"Purchase",Key:"COALESCE(NEW.DocumentNumber,CAST(NEW.Id AS TEXT))",Old:"COALESCE(OLD.SupplierName,'')||'|'||OLD.DocumentValue",New:"COALESCE(NEW.SupplierName,'')||'|'||NEW.DocumentValue"),
            (Table:"Masters",Module:"Ledger Master",Key:"COALESCE(NEW.Name,CAST(NEW.Id AS TEXT))",Old:"COALESCE(OLD.Name,'')||'|'||COALESCE(OLD.Parent,'')",New:"COALESCE(NEW.Name,'')||'|'||COALESCE(NEW.Parent,'')"),
            (Table:"StockItems",Module:"Stock Master",Key:"COALESCE(NEW.Name,CAST(NEW.Id AS TEXT))",Old:"COALESCE(OLD.Name,'')||'|'||COALESCE(OLD.StockGroup,'')",New:"COALESCE(NEW.Name,'')||'|'||COALESCE(NEW.StockGroup,'')"),
            (Table:"JournalVouchers",Module:"Journal",Key:"CAST(NEW.Id AS TEXT)",Old:"COALESCE(OLD.Narration,'')",New:"COALESCE(NEW.Narration,'')")
        };
        foreach(var s in specs)
        {
            using var command=connection.CreateCommand();
            command.CommandText=$"""
            CREATE TRIGGER IF NOT EXISTS TR_Audit_{s.Table}_AI AFTER INSERT ON {s.Table}
            BEGIN INSERT INTO FinanceAuditEvents(ClientId,FinancialYear,Module,EntityType,EntityKey,Operation,OldValue,NewValue,ChangedUtc) VALUES(NEW.ClientId,NEW.FinancialYear,'{s.Module}','{s.Table}',{s.Key},'Insert','',{s.New},strftime('%Y-%m-%dT%H:%M:%fZ','now')); END;
            CREATE TRIGGER IF NOT EXISTS TR_Audit_{s.Table}_AU AFTER UPDATE ON {s.Table}
            BEGIN INSERT INTO FinanceAuditEvents(ClientId,FinancialYear,Module,EntityType,EntityKey,Operation,OldValue,NewValue,ChangedUtc) VALUES(NEW.ClientId,NEW.FinancialYear,'{s.Module}','{s.Table}',{s.Key},'Update',{s.Old},{s.New},strftime('%Y-%m-%dT%H:%M:%fZ','now')); END;
            CREATE TRIGGER IF NOT EXISTS TR_Audit_{s.Table}_BD BEFORE DELETE ON {s.Table}
            BEGIN INSERT INTO FinanceAuditEvents(ClientId,FinancialYear,Module,EntityType,EntityKey,Operation,OldValue,NewValue,ChangedUtc) VALUES(OLD.ClientId,OLD.FinancialYear,'{s.Module}','{s.Table}',CAST(OLD.Id AS TEXT),'Delete',{s.Old},'',strftime('%Y-%m-%dT%H:%M:%fZ','now')); END;
            """;
            try{command.ExecuteNonQuery();}catch(SqliteException){/* older database missing optional table/column: feature remains isolated */}
        }
    }

    private static void CreatePeriodLockTriggers(SqliteConnection connection)
    {
        var specs=new[]{
            (Table:"BankTransactions",Module:"Banking",Date:"NEW.TransactionDate"),
            (Table:"CashEntries",Module:"Cash Entry",Date:"NEW.EntryDate"),
            (Table:"SalesRecords",Module:"Sales",Date:"COALESCE(NULLIF(NEW.GstDate,''),NEW.DocumentDate)"),
            (Table:"PurchaseRecords",Module:"Purchase",Date:"COALESCE(NULLIF(NEW.GstDate,''),NEW.DocumentDate)"),
            (Table:"JournalVouchers",Module:"Journal",Date:"NEW.VoucherDate")
        };
        foreach(var s in specs)
        {
            // The UPDATE guard checks BOTH the original and replacement dates.  Without the
            // OLD-side check a locked transaction could be moved into an unlocked date and
            // thereby escape a finalized accounting period.  Drop/recreate keeps trigger
            // definitions current for clients upgraded from earlier releases.
            var oldDate = s.Date.Replace("NEW.", "OLD.", StringComparison.Ordinal);
            using var command=connection.CreateCommand();command.CommandText=$"""
            DROP TRIGGER IF EXISTS TR_Lock_{s.Table}_BI;
            DROP TRIGGER IF EXISTS TR_Lock_{s.Table}_BU;
            DROP TRIGGER IF EXISTS TR_Lock_{s.Table}_BD;
            CREATE TRIGGER TR_Lock_{s.Table}_BI BEFORE INSERT ON {s.Table}
            WHEN EXISTS(SELECT 1 FROM AccountingPeriodLocks L WHERE L.ClientId=NEW.ClientId AND L.FinancialYear=NEW.FinancialYear AND L.IsActive=1 AND (L.Module='All' COLLATE NOCASE OR L.Module='{s.Module}' COLLATE NOCASE) AND date({s.Date})<=date(L.LockDate))
            BEGIN SELECT RAISE(ABORT,'ACCOUNTING_PERIOD_LOCKED'); END;
            CREATE TRIGGER TR_Lock_{s.Table}_BU BEFORE UPDATE ON {s.Table}
            WHEN EXISTS(SELECT 1 FROM AccountingPeriodLocks L WHERE L.ClientId=NEW.ClientId AND L.FinancialYear=NEW.FinancialYear AND L.IsActive=1 AND (L.Module='All' COLLATE NOCASE OR L.Module='{s.Module}' COLLATE NOCASE) AND date({s.Date})<=date(L.LockDate))
              OR EXISTS(SELECT 1 FROM AccountingPeriodLocks L WHERE L.ClientId=OLD.ClientId AND L.FinancialYear=OLD.FinancialYear AND L.IsActive=1 AND (L.Module='All' COLLATE NOCASE OR L.Module='{s.Module}' COLLATE NOCASE) AND date({oldDate})<=date(L.LockDate))
            BEGIN SELECT RAISE(ABORT,'ACCOUNTING_PERIOD_LOCKED'); END;
            CREATE TRIGGER TR_Lock_{s.Table}_BD BEFORE DELETE ON {s.Table}
            WHEN EXISTS(SELECT 1 FROM AccountingPeriodLocks L WHERE L.ClientId=OLD.ClientId AND L.FinancialYear=OLD.FinancialYear AND L.IsActive=1 AND (L.Module='All' COLLATE NOCASE OR L.Module='{s.Module}' COLLATE NOCASE) AND date({oldDate})<=date(L.LockDate))
            BEGIN SELECT RAISE(ABORT,'ACCOUNTING_PERIOD_LOCKED'); END;
            """;
            try{command.ExecuteNonQuery();}catch(SqliteException){ }
        }
    }

    private static void EnsureColumn(SqliteConnection connection,string table,string column,string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, table, column, definition);
    }

    // Fix81: use the shared connection policy so WAL mode is requested once per database
    // rather than re-running journal_mode on every Finance connection while background
    // accounting/Tally work may already be active.
    private SqliteConnection OpenConnection() => SqliteConnectionPolicy.Open(_connectionString);
    private static void AddScope(SqliteCommand command,long clientId,string fy){command.Parameters.AddWithValue("$ClientId",clientId);command.Parameters.AddWithValue("$FinancialYear",fy.Trim());}
    private static long ReadLong(SqliteDataReader r,string n)=>Convert.ToInt64(r[n],CultureInfo.InvariantCulture);private static int ReadInt(SqliteDataReader r,string n)=>Convert.ToInt32(r[n],CultureInfo.InvariantCulture);private static string ReadString(SqliteDataReader r,string n)=>r[n] is DBNull?string.Empty:Convert.ToString(r[n],CultureInfo.InvariantCulture)??string.Empty;private static decimal ReadDecimal(SqliteDataReader r,string n)=>r[n] is DBNull?0m:ToDecimal(r[n]);private static decimal? ReadNullableDecimal(SqliteDataReader r,string n)=>r[n] is DBNull?null:ToDecimal(r[n]);
    private static decimal ToDecimal(object? value){if(value is null||value is DBNull)return 0m;try{return Convert.ToDecimal(value,CultureInfo.InvariantCulture);}catch{return 0m;}}
    private static DateTime? ReadDate(SqliteDataReader r,string n)=>ReadDate(ReadString(r,n));private static DateTime? ReadDate(string? value){if(string.IsNullOrWhiteSpace(value))return null;if(DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces|DateTimeStyles.AssumeLocal,out var date))return date.Date;return null;}private static DateTime? ParseDate(string? value)=>ReadDate(value);private static DateTime ParseDateTime(string value)=>DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var date)?date:DateTime.MinValue;
}
