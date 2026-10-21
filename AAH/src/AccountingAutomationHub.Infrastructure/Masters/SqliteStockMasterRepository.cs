using System.Globalization;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Masters;

public sealed class SqliteStockMasterRepository : IStockMasterRepository
{
    private readonly string _connectionString;

    public SqliteStockMasterRepository()
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

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteStockMasterRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS StockItems
            (
                Id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId              INTEGER NOT NULL,
                FinancialYear         TEXT NOT NULL,
                Name                  TEXT NOT NULL COLLATE NOCASE,
                StockGroup            TEXT NOT NULL DEFAULT 'Primary',
                StockCategory         TEXT NOT NULL DEFAULT '',
                BaseUnit              TEXT NOT NULL DEFAULT '',
                AdditionalUnit        TEXT NOT NULL DEFAULT '',
                HsnSac                TEXT NOT NULL DEFAULT '',
                GstRate               REAL NULL,
                GstTypeOfSupply       TEXT NOT NULL DEFAULT '',
                BatchWise             INTEGER NOT NULL DEFAULT 0,
                Perishable            INTEGER NOT NULL DEFAULT 0,
                HasManufacturingDate  INTEGER NOT NULL DEFAULT 0,
                CostingMethod         TEXT NOT NULL DEFAULT '',
                ValuationMethod       TEXT NOT NULL DEFAULT '',
                Company               TEXT NOT NULL DEFAULT '',
                Source                TEXT NOT NULL DEFAULT '',
                IsArchived            INTEGER NOT NULL DEFAULT 0,
                IsUserEdited          INTEGER NOT NULL DEFAULT 0,
                CreatedUtc            TEXT NOT NULL,
                UpdatedUtc            TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_StockItems_ClientFyName
            ON StockItems(ClientId, FinancialYear, Name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS StockUnits
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Name            TEXT NOT NULL COLLATE NOCASE,
                FormalName      TEXT NOT NULL DEFAULT '',
                DecimalPlaces   INTEGER NOT NULL DEFAULT 0,
                ReportingUqc    TEXT NOT NULL DEFAULT '',
                Conversion      REAL NULL,
                Company         TEXT NOT NULL DEFAULT '',
                Source          TEXT NOT NULL DEFAULT '',
                IsArchived      INTEGER NOT NULL DEFAULT 0,
                IsUserEdited    INTEGER NOT NULL DEFAULT 0,
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_StockUnits_ClientFyName
            ON StockUnits(ClientId, FinancialYear, Name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS StockSimpleMasters
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Type            TEXT NOT NULL COLLATE NOCASE,
                Name            TEXT NOT NULL COLLATE NOCASE,
                Parent          TEXT NOT NULL DEFAULT 'Primary',
                Company         TEXT NOT NULL DEFAULT '',
                Source          TEXT NOT NULL DEFAULT '',
                IsArchived      INTEGER NOT NULL DEFAULT 0,
                IsUserEdited    INTEGER NOT NULL DEFAULT 0,
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_StockSimpleMasters_ClientFyTypeName
            ON StockSimpleMasters(ClientId, FinancialYear, Type COLLATE NOCASE, Name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS StockItemAliases
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                ImportedAlias   TEXT NOT NULL COLLATE NOCASE,
                StockItemName   TEXT NOT NULL,
                Source          TEXT NOT NULL DEFAULT '',
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_StockItemAliases_ClientFyAlias
            ON StockItemAliases(ClientId, FinancialYear, ImportedAlias COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS StockUnitMappings
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                StockItemName   TEXT NOT NULL COLLATE NOCASE,
                ImportedUnit    TEXT NOT NULL COLLATE NOCASE,
                FinalUnit       TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_StockUnitMappings_ClientFyItemUnit
            ON StockUnitMappings(ClientId, FinancialYear, StockItemName COLLATE NOCASE, ImportedUnit COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS QuantityConversionRules
            (
                Id                INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId          INTEGER NOT NULL,
                FinancialYear     TEXT NOT NULL,
                StockItemName     TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                ImportedUnit      TEXT NOT NULL COLLATE NOCASE,
                FinalUnit         TEXT NOT NULL COLLATE NOCASE,
                Factor            REAL NOT NULL,
                IsUniversal       INTEGER NOT NULL DEFAULT 0,
                RememberForClient INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc        TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_QuantityConversionRules_Key
            ON QuantityConversionRules(ClientId, FinancialYear, StockItemName COLLATE NOCASE, ImportedUnit COLLATE NOCASE, FinalUnit COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS StockItemUnitProfiles
            (
                Id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId              INTEGER NOT NULL,
                FinancialYear         TEXT NOT NULL,
                StockItemId           INTEGER NOT NULL,
                StockItemName         TEXT NOT NULL DEFAULT '',
                UnitCode              TEXT NOT NULL COLLATE NOCASE,
                FactorToBase          REAL NOT NULL DEFAULT 1,
                IsAllowed             INTEGER NOT NULL DEFAULT 1,
                IsTallyAlternateUnit  INTEGER NOT NULL DEFAULT 0,
                IsPackageConversion   INTEGER NOT NULL DEFAULT 0,
                Source                TEXT NOT NULL DEFAULT '',
                UpdatedUtc            TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE,
                FOREIGN KEY (StockItemId) REFERENCES StockItems(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_StockItemUnitProfiles_ItemUnit
            ON StockItemUnitProfiles(ClientId, FinancialYear, StockItemId, UnitCode COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS GstHsnSacDirectory
            (
                Code        TEXT NOT NULL COLLATE NOCASE,
                Description TEXT NOT NULL DEFAULT '',
                Kind        TEXT NOT NULL DEFAULT '',
                Source      TEXT NOT NULL DEFAULT '',
                UpdatedUtc  TEXT NOT NULL,
                PRIMARY KEY(Code, Kind)
            );

            CREATE INDEX IF NOT EXISTS IX_GstHsnSacDirectory_Description
            ON GstHsnSacDirectory(Description COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();

        // Fix117 additive migration for databases created by earlier cumulative builds.
        EnsureColumn(connection, "StockItems", "PartNumberSku", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "DefaultPurchaseUnit", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "DefaultSalesUnit", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "RateUnit", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "DefaultGodown", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "DefaultPurchaseLedger", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "DefaultSalesLedger", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "ReorderPoint", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "StockItems", "MinimumStock", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "StockItems", "MaximumStock", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "StockItems", "TallyMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "TallyGuid", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StockItems", "TallyAlterId", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "StockUnits", "FormalName", "TEXT NOT NULL DEFAULT ''");
        // Fix123: old aliases did not distinguish explicit user Rename/Mapping from
        // automatic SKU/compact suggestions. The additive Source column preserves
        // that authority without discarding any existing mapping.
        EnsureColumn(connection, "StockItemAliases", "Source", "TEXT NOT NULL DEFAULT ''");

        // Fix118: Purchase and Sales do not own separate default units. One Stock Item
        // Inventory/Transaction Unit (BaseUnit) is authoritative for both paths. Preserve
        // the old columns only for cumulative-schema compatibility and force them to match.
        using (var unitAuthority = connection.CreateCommand())
        {
            unitAuthority.CommandText =
                "UPDATE StockItems SET BaseUnit=CASE WHEN TRIM(BaseUnit)='' THEN COALESCE(NULLIF(TRIM(DefaultPurchaseUnit),''),NULLIF(TRIM(DefaultSalesUnit),''),BaseUnit) ELSE BaseUnit END;" +
                "UPDATE StockItems SET DefaultPurchaseUnit=BaseUnit,DefaultSalesUnit=BaseUnit WHERE DefaultPurchaseUnit<>BaseUnit OR DefaultSalesUnit<>BaseUnit;";
            unitAuthority.ExecuteNonQuery();
        }

        using var identityIndex = connection.CreateCommand();
        identityIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UX_StockItems_ClientFyTallyMasterId ON StockItems(ClientId,FinancialYear,TallyMasterId) WHERE TallyMasterId<>'';";
        identityIndex.ExecuteNonQuery();
    }

    public bool HasRecords(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // Sidebar visibility is based on real Stock Item data. Supporting Units,
        // Groups or Godowns alone do not make the Stock Master a populated module;
        // the Dashboard still provides manual access whenever setup is required.
        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1 FROM StockItems
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND IsArchived=0
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public IReadOnlyList<StockItemRecord> GetStockItems(long clientId, string financialYear, bool includeArchived = false, string searchText = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM StockItems
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND ($IncludeArchived=1 OR IsArchived=0)
              AND ($Search='' OR Name LIKE $Pattern OR StockGroup LIKE $Pattern OR StockCategory LIKE $Pattern OR BaseUnit LIKE $Pattern OR HsnSac LIKE $Pattern)
            ORDER BY StockGroup COLLATE NOCASE, Name COLLATE NOCASE;
            """;
        AddScope(command, clientId, financialYear, includeArchived, searchText);
        using var reader = command.ExecuteReader();
        var result = new List<StockItemRecord>();
        while (reader.Read()) result.Add(ReadStockItem(reader));
        return result;
    }

    public IReadOnlyList<StockUnitRecord> GetUnits(long clientId, string financialYear, bool includeArchived = false, string searchText = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM StockUnits
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
              AND ($IncludeArchived=1 OR IsArchived=0)
              AND ($Search='' OR Name LIKE $Pattern OR FormalName LIKE $Pattern OR ReportingUqc LIKE $Pattern)
            ORDER BY Name COLLATE NOCASE;
            """;
        AddScope(command, clientId, financialYear, includeArchived, searchText);
        using var reader = command.ExecuteReader();
        var result = new List<StockUnitRecord>();
        while (reader.Read()) result.Add(ReadUnit(reader));
        return result;
    }

    public IReadOnlyList<StockSimpleMasterRecord> GetSimpleMasters(long clientId, string financialYear, string type, bool includeArchived = false, string searchText = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM StockSimpleMasters
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Type=$Type
              AND ($IncludeArchived=1 OR IsArchived=0)
              AND ($Search='' OR Name LIKE $Pattern OR Parent LIKE $Pattern)
            ORDER BY Parent COLLATE NOCASE, Name COLLATE NOCASE;
            """;
        AddScope(command, clientId, financialYear, includeArchived, searchText);
        command.Parameters.AddWithValue("$Type", type.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<StockSimpleMasterRecord>();
        while (reader.Read()) result.Add(ReadSimple(reader));
        return result;
    }

    public int UpsertStockItems(long clientId, string financialYear, IEnumerable<StockItemRecord> records, bool markUserEdited = false)
    {
        var incomingRecords = records.ToArray();
        var allExisting = GetStockItems(clientId, financialYear, includeArchived: true).ToArray();
        var existingByKey = allExisting
            .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.IsUserEdited).ThenBy(item => item.PersistentId).First(), StringComparer.OrdinalIgnoreCase);
        var existingByMasterId = allExisting.Where(item => item.TallyMasterId.Trim().Length > 0)
            .GroupBy(item => item.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item=>item.TallyAlterId).ThenBy(item=>item.PersistentId).First(), StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        // Fix123 ERP9/Prime Stock authority: when a live Tally Stock Item is not under
        // an explicit unsent user edit, its MASTERID-backed Base Unit is authoritative.
        // Older AAH auto-created items must not keep a stale PCS/QTL guess merely because
        // BaseUnit was already nonblank before the live Tally Master was first adopted.
        foreach (var source in incomingRecords.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                     .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
        {
            source.BaseUnit = NormalizeCanonicalUnit(source.BaseUnit);
            source.DefaultPurchaseUnit = source.BaseUnit;
            source.DefaultSalesUnit = source.BaseUnit;
            if (string.IsNullOrWhiteSpace(source.RateUnit)) source.RateUnit = source.BaseUnit;
            var duplicateKey = AccountingText.DuplicateKey(source.Name);
            if (source.TallyMasterId.Trim().Length > 0 && existingByMasterId.TryGetValue(source.TallyMasterId.Trim(), out var identityExisting))
            {
                var conflictingName = allExisting.FirstOrDefault(item => item.PersistentId != identityExisting.PersistentId && AccountingText.DuplicateKey(item.Name) == duplicateKey);
                if (conflictingName is not null)
                    throw new InvalidOperationException($"Tally Stock MASTERID {source.TallyMasterId} now names '{source.Name}', but that name already belongs to AAH Stock Item ID {conflictingName.PersistentId}. Resolve the Stock Master identity conflict before continuing.");
                using var identityUpdate=connection.CreateCommand(); identityUpdate.Transaction=transaction;
                identityUpdate.CommandText="UPDATE StockItems SET Name=$Name,StockGroup=CASE WHEN IsUserEdited=1 THEN StockGroup ELSE $StockGroup END,StockCategory=CASE WHEN IsUserEdited=1 THEN StockCategory ELSE $StockCategory END,BaseUnit=CASE WHEN IsUserEdited=1 THEN BaseUnit ELSE COALESCE(NULLIF($BaseUnit,''),BaseUnit) END,AdditionalUnit=CASE WHEN IsUserEdited=1 THEN AdditionalUnit ELSE COALESCE(NULLIF($AdditionalUnit,''),AdditionalUnit) END,PartNumberSku=COALESCE(NULLIF($PartNumberSku,''),PartNumberSku),DefaultPurchaseUnit=CASE WHEN IsUserEdited=1 THEN DefaultPurchaseUnit ELSE COALESCE(NULLIF($DefaultPurchaseUnit,''),DefaultPurchaseUnit) END,DefaultSalesUnit=CASE WHEN IsUserEdited=1 THEN DefaultSalesUnit ELSE COALESCE(NULLIF($DefaultSalesUnit,''),DefaultSalesUnit) END,RateUnit=CASE WHEN IsUserEdited=1 THEN RateUnit ELSE COALESCE(NULLIF($RateUnit,''),RateUnit) END,DefaultGodown=COALESCE(NULLIF($DefaultGodown,''),DefaultGodown),DefaultPurchaseLedger=COALESCE(NULLIF($DefaultPurchaseLedger,''),DefaultPurchaseLedger),DefaultSalesLedger=COALESCE(NULLIF($DefaultSalesLedger,''),DefaultSalesLedger),ReorderPoint=CASE WHEN $ReorderPoint<>0 THEN $ReorderPoint ELSE ReorderPoint END,MinimumStock=CASE WHEN $MinimumStock<>0 THEN $MinimumStock ELSE MinimumStock END,MaximumStock=CASE WHEN $MaximumStock<>0 THEN $MaximumStock ELSE MaximumStock END,TallyMasterId=$TallyMasterId,TallyGuid=COALESCE(NULLIF($TallyGuid,''),TallyGuid),TallyAlterId=CASE WHEN $TallyAlterId>0 THEN $TallyAlterId ELSE TallyAlterId END,HsnSac=COALESCE(NULLIF($HsnSac,''),HsnSac),GstRate=COALESCE($GstRate,GstRate),GstTypeOfSupply=COALESCE(NULLIF($GstTypeOfSupply,''),GstTypeOfSupply),BatchWise=CASE WHEN $BatchWise=1 THEN 1 ELSE BatchWise END,Perishable=CASE WHEN $Perishable=1 THEN 1 ELSE Perishable END,HasManufacturingDate=CASE WHEN $HasManufacturingDate=1 THEN 1 ELSE HasManufacturingDate END,CostingMethod=COALESCE(NULLIF($CostingMethod,''),CostingMethod),ValuationMethod=COALESCE(NULLIF($ValuationMethod,''),ValuationMethod),Company=COALESCE(NULLIF($Company,''),Company),Source=$Source,IsArchived=0,UpdatedUtc=$Now WHERE Id=$Id;";
                AddStockItemParameters(identityUpdate,clientId,financialYear,source,markUserEdited:false); identityUpdate.Parameters.AddWithValue("$Id",identityExisting.PersistentId); count+=identityUpdate.ExecuteNonQuery();
                continue;
            }
            if (existingByKey.TryGetValue(duplicateKey, out var existing)) source.Name = existing.Name; else existingByKey[duplicateKey] = source;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO StockItems
                (ClientId,FinancialYear,Name,StockGroup,StockCategory,BaseUnit,AdditionalUnit,PartNumberSku,DefaultPurchaseUnit,DefaultSalesUnit,RateUnit,DefaultGodown,DefaultPurchaseLedger,DefaultSalesLedger,ReorderPoint,MinimumStock,MaximumStock,TallyMasterId,TallyGuid,TallyAlterId,HsnSac,GstRate,GstTypeOfSupply,BatchWise,Perishable,HasManufacturingDate,CostingMethod,ValuationMethod,Company,Source,IsArchived,IsUserEdited,CreatedUtc,UpdatedUtc)
                VALUES
                ($ClientId,$FinancialYear,$Name,$StockGroup,$StockCategory,$BaseUnit,$AdditionalUnit,$PartNumberSku,$DefaultPurchaseUnit,$DefaultSalesUnit,$RateUnit,$DefaultGodown,$DefaultPurchaseLedger,$DefaultSalesLedger,$ReorderPoint,$MinimumStock,$MaximumStock,$TallyMasterId,$TallyGuid,$TallyAlterId,$HsnSac,$GstRate,$GstTypeOfSupply,$BatchWise,$Perishable,$HasManufacturingDate,$CostingMethod,$ValuationMethod,$Company,$Source,0,$IsUserEdited,$Now,$Now)
                ON CONFLICT(ClientId,FinancialYear,Name) DO UPDATE SET
                    StockGroup=CASE WHEN StockItems.IsUserEdited=1 THEN StockItems.StockGroup ELSE excluded.StockGroup END,
                    StockCategory=CASE WHEN StockItems.IsUserEdited=1 THEN StockItems.StockCategory ELSE excluded.StockCategory END,
                    BaseUnit=CASE WHEN StockItems.IsUserEdited=1 THEN StockItems.BaseUnit ELSE COALESCE(NULLIF(excluded.BaseUnit,''),StockItems.BaseUnit) END,
                    AdditionalUnit=CASE WHEN StockItems.IsUserEdited=1 THEN StockItems.AdditionalUnit ELSE COALESCE(NULLIF(excluded.AdditionalUnit,''),StockItems.AdditionalUnit) END,
                    PartNumberSku=COALESCE(NULLIF(excluded.PartNumberSku,''),StockItems.PartNumberSku),
                    DefaultPurchaseUnit=CASE WHEN StockItems.IsUserEdited=1 THEN StockItems.DefaultPurchaseUnit ELSE COALESCE(NULLIF(excluded.DefaultPurchaseUnit,''),StockItems.DefaultPurchaseUnit) END,
                    DefaultSalesUnit=CASE WHEN StockItems.IsUserEdited=1 THEN StockItems.DefaultSalesUnit ELSE COALESCE(NULLIF(excluded.DefaultSalesUnit,''),StockItems.DefaultSalesUnit) END,
                    RateUnit=CASE WHEN StockItems.IsUserEdited=1 THEN StockItems.RateUnit ELSE COALESCE(NULLIF(excluded.RateUnit,''),StockItems.RateUnit) END,
                    DefaultGodown=COALESCE(NULLIF(excluded.DefaultGodown,''),StockItems.DefaultGodown),
                    DefaultPurchaseLedger=COALESCE(NULLIF(excluded.DefaultPurchaseLedger,''),StockItems.DefaultPurchaseLedger),
                    DefaultSalesLedger=COALESCE(NULLIF(excluded.DefaultSalesLedger,''),StockItems.DefaultSalesLedger),
                    ReorderPoint=CASE WHEN excluded.ReorderPoint<>0 THEN excluded.ReorderPoint ELSE StockItems.ReorderPoint END,
                    MinimumStock=CASE WHEN excluded.MinimumStock<>0 THEN excluded.MinimumStock ELSE StockItems.MinimumStock END,
                    MaximumStock=CASE WHEN excluded.MaximumStock<>0 THEN excluded.MaximumStock ELSE StockItems.MaximumStock END,
                    TallyMasterId=COALESCE(NULLIF(excluded.TallyMasterId,''),StockItems.TallyMasterId),
                    TallyGuid=COALESCE(NULLIF(excluded.TallyGuid,''),StockItems.TallyGuid),
                    TallyAlterId=CASE WHEN excluded.TallyAlterId>0 THEN excluded.TallyAlterId ELSE StockItems.TallyAlterId END,
                    HsnSac=COALESCE(NULLIF(excluded.HsnSac,''),StockItems.HsnSac),
                    GstRate=COALESCE(excluded.GstRate,StockItems.GstRate),
                    GstTypeOfSupply=COALESCE(NULLIF(excluded.GstTypeOfSupply,''),StockItems.GstTypeOfSupply),
                    BatchWise=CASE WHEN excluded.BatchWise=1 THEN 1 ELSE StockItems.BatchWise END,
                    Perishable=CASE WHEN excluded.Perishable=1 THEN 1 ELSE StockItems.Perishable END,
                    HasManufacturingDate=CASE WHEN excluded.HasManufacturingDate=1 THEN 1 ELSE StockItems.HasManufacturingDate END,
                    CostingMethod=COALESCE(NULLIF(excluded.CostingMethod,''),StockItems.CostingMethod),
                    ValuationMethod=COALESCE(NULLIF(excluded.ValuationMethod,''),StockItems.ValuationMethod),
                    Company=COALESCE(NULLIF(excluded.Company,''),StockItems.Company),
                    Source=excluded.Source,
                    IsArchived=0,
                    IsUserEdited=CASE WHEN $IsUserEdited=1 THEN 1 ELSE StockItems.IsUserEdited END,
                    UpdatedUtc=$Now;
                """;
            AddStockItemParameters(command, clientId, financialYear, source, markUserEdited);
            count += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return count;
    }

    public StockItemRecord UpsertStockItemAndGet(long clientId, string financialYear, StockItemRecord record, bool markUserEdited = false)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        var requestedName = record.Name?.Trim() ?? string.Empty;
        if (requestedName.Length == 0) throw new InvalidOperationException("Stock Item name is required.");
        var requestedKey = AccountingText.DuplicateKey(requestedName);
        var requestedMasterId = record.TallyMasterId?.Trim() ?? string.Empty;

        // UpsertStockItems may intentionally adopt the spelling of an existing duplicate-key
        // Stock Item. Therefore the durable identity must never be read back by the originally
        // typed display string alone. MASTERID wins when available; otherwise duplicate-key
        // identity is authoritative for this local save.
        UpsertStockItems(clientId, financialYear, [record], markUserEdited);
        var saved = GetStockItems(clientId, financialYear, includeArchived: true)
            .Where(item => requestedMasterId.Length > 0
                ? item.TallyMasterId.Trim().Equals(requestedMasterId, StringComparison.OrdinalIgnoreCase)
                : AccountingText.DuplicateKey(item.Name).Equals(requestedKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.IsUserEdited)
            .ThenByDescending(item => item.TallyAlterId)
            .ThenBy(item => item.PersistentId)
            .FirstOrDefault();
        if (saved is null)
            throw new InvalidOperationException($"Stock Item '{requestedName}' was committed but its durable identity could not be resolved. Refresh Stock Master diagnostics and retry.");
        return saved;
    }

    public int UpsertUnits(long clientId, string financialYear, IEnumerable<StockUnitRecord> records, bool markUserEdited = false)
    {
        var incomingRecords = records.ToArray();
        var existingByKey = GetUnits(clientId, financialYear, includeArchived: true)
            .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.IsUserEdited).ThenBy(item => item.PersistentId).First(), StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        foreach (var source in incomingRecords.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                     .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
        {
            var duplicateKey = AccountingText.DuplicateKey(source.Name);
            if (existingByKey.TryGetValue(duplicateKey, out var existing))
                source.Name = existing.Name;
            else
                existingByKey[duplicateKey] = source;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO StockUnits(ClientId,FinancialYear,Name,FormalName,DecimalPlaces,ReportingUqc,Conversion,Company,Source,IsArchived,IsUserEdited,CreatedUtc,UpdatedUtc)
                VALUES($ClientId,$FinancialYear,$Name,$FormalName,$DecimalPlaces,$ReportingUqc,$Conversion,$Company,$Source,0,$IsUserEdited,$Now,$Now)
                ON CONFLICT(ClientId,FinancialYear,Name) DO UPDATE SET
                    FormalName=CASE WHEN StockUnits.IsUserEdited=1 THEN StockUnits.FormalName ELSE COALESCE(NULLIF(excluded.FormalName,''),StockUnits.FormalName) END,
                    DecimalPlaces=CASE WHEN StockUnits.IsUserEdited=1 THEN StockUnits.DecimalPlaces ELSE excluded.DecimalPlaces END,
                    ReportingUqc=COALESCE(NULLIF(excluded.ReportingUqc,''),StockUnits.ReportingUqc),
                    Conversion=COALESCE(excluded.Conversion,StockUnits.Conversion),
                    Company=COALESCE(NULLIF(excluded.Company,''),StockUnits.Company),
                    Source=excluded.Source, IsArchived=0,
                    IsUserEdited=CASE WHEN $IsUserEdited=1 THEN 1 ELSE StockUnits.IsUserEdited END,
                    UpdatedUtc=$Now;
                """;
            AddUnitParameters(command, clientId, financialYear, source, markUserEdited);
            count += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return count;
    }

    public int UpsertSimpleMasters(long clientId, string financialYear, IEnumerable<StockSimpleMasterRecord> records, bool markUserEdited = false)
    {
        var incomingRecords = records.ToArray();
        var existingByKey = incomingRecords
            .Where(item => !string.IsNullOrWhiteSpace(item.Type))
            .Select(item => item.Type.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(type => GetSimpleMasters(clientId, financialYear, type, includeArchived: true))
            .GroupBy(item => AccountingText.DuplicateKey(item.Type)+"\u001f"+AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.IsUserEdited).ThenBy(item => item.PersistentId).First(), StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        foreach (var source in incomingRecords.Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Type))
                     .GroupBy(item => AccountingText.DuplicateKey(item.Type)+"\u001f"+AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
        {
            var duplicateKey = AccountingText.DuplicateKey(source.Type)+"\u001f"+AccountingText.DuplicateKey(source.Name);
            if (existingByKey.TryGetValue(duplicateKey, out var existing))
                source.Name = existing.Name;
            else
                existingByKey[duplicateKey] = source;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO StockSimpleMasters(ClientId,FinancialYear,Type,Name,Parent,Company,Source,IsArchived,IsUserEdited,CreatedUtc,UpdatedUtc)
                VALUES($ClientId,$FinancialYear,$Type,$Name,$Parent,$Company,$Source,0,$IsUserEdited,$Now,$Now)
                ON CONFLICT(ClientId,FinancialYear,Type,Name) DO UPDATE SET
                    Parent=CASE WHEN StockSimpleMasters.IsUserEdited=1 THEN StockSimpleMasters.Parent ELSE excluded.Parent END,
                    Company=COALESCE(NULLIF(excluded.Company,''),StockSimpleMasters.Company),
                    Source=excluded.Source, IsArchived=0,
                    IsUserEdited=CASE WHEN $IsUserEdited=1 THEN 1 ELSE StockSimpleMasters.IsUserEdited END,
                    UpdatedUtc=$Now;
                """;
            AddSimpleParameters(command, clientId, financialYear, source, markUserEdited);
            count += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return count;
    }

    public void SaveStockItem(StockItemRecord record)
    {
        record.Name = AccountingText.DisplayName(record.Name);
        record.StockGroup = string.Equals(record.StockGroup?.Trim(), "Primary", StringComparison.OrdinalIgnoreCase) ? "Primary" : AccountingText.DisplayName(record.StockGroup);
        record.StockCategory = AccountingText.DisplayName(record.StockCategory);
        record.BaseUnit = NormalizeCanonicalUnit(record.BaseUnit);
        record.AdditionalUnit = NormalizeCanonicalUnit(record.AdditionalUnit);
        record.DefaultPurchaseUnit = record.BaseUnit; // Fix118 legacy compatibility: one transaction Unit for Purchase + Sales.
        record.DefaultSalesUnit = record.BaseUnit;
        record.RateUnit = NormalizeCanonicalUnit(record.RateUnit);
        record.PartNumberSku = Clean(record.PartNumberSku);
        record.Company = AccountingText.DisplayName(record.Company);
        EnsureNoStockItemDuplicate(record);
        StockItemRecord? before = null;
        var baseUnitChanged = false;
        if (record.PersistentId > 0)
        {
            before = GetStockItems(record.ClientId, record.FinancialYear, includeArchived: true)
                .FirstOrDefault(item => item.PersistentId == record.PersistentId);
            baseUnitChanged = before is not null && !UnitEquivalent(before.BaseUnit, record.BaseUnit);
            if (before is not null && before.BaseUnit.Trim().Length > 0 &&
                baseUnitChanged && before.TallyMasterId.Trim().Length > 0)
                throw new InvalidOperationException($"Base/Inventory Unit for Tally-backed Stock Item '{before.Name}' (MASTERID {before.TallyMasterId}) cannot be changed silently. Use an allowed/alternate Stock Item Unit Profile instead.");

            // Fix123: editing the Stock Master Base Unit creates a pending conversion
            // difference; it does not invent a factor. Keep the Rate Unit aligned when
            // it was merely following the previous Base Unit. Quantity Conversion will
            // later confirm/re-enable the imported-unit factor.
            if (baseUnitChanged && before is not null &&
                (record.RateUnit.Trim().Length == 0 || UnitEquivalent(record.RateUnit, before.BaseUnit)))
                record.RateUnit = record.BaseUnit;
        }
        if (record.PersistentId <= 0)
        {
            UpsertStockItems(record.ClientId, record.FinancialYear, [record], true);
            return;
        }
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE StockItems SET Name=$Name,StockGroup=$StockGroup,StockCategory=$StockCategory,BaseUnit=$BaseUnit,AdditionalUnit=$AdditionalUnit,PartNumberSku=$PartNumberSku,DefaultPurchaseUnit=$DefaultPurchaseUnit,DefaultSalesUnit=$DefaultSalesUnit,RateUnit=$RateUnit,DefaultGodown=$DefaultGodown,DefaultPurchaseLedger=$DefaultPurchaseLedger,DefaultSalesLedger=$DefaultSalesLedger,ReorderPoint=$ReorderPoint,MinimumStock=$MinimumStock,MaximumStock=$MaximumStock,TallyMasterId=$TallyMasterId,TallyGuid=$TallyGuid,TallyAlterId=$TallyAlterId,HsnSac=$HsnSac,GstRate=$GstRate,GstTypeOfSupply=$GstTypeOfSupply,BatchWise=$BatchWise,Perishable=$Perishable,HasManufacturingDate=$HasManufacturingDate,CostingMethod=$CostingMethod,ValuationMethod=$ValuationMethod,Company=$Company,Source=$Source,IsArchived=$IsArchived,IsUserEdited=1,UpdatedUtc=$Now WHERE Id=$Id;
            """;
        AddStockItemParameters(command, record.ClientId, record.FinancialYear, record, true);
        command.Parameters.AddWithValue("$Id", record.PersistentId);
        command.Parameters.AddWithValue("$IsArchived", record.IsArchived ? 1 : 0);
        command.ExecuteNonQuery();

        if (before is not null && !before.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase))
            RetargetStockItemNameReferences(connection, transaction, record.ClientId, record.FinancialYear, before.Name, record.Name);

        // A direct Base-Unit edit is deliberately left pending until a conversion is
        // confirmed. Existing non-base profiles are disabled so an old factor=1 profile
        // cannot silently convert QTL -> PCS (or any other changed unit). The confirmed
        // Quantity Conversion path immediately re-enables the selected imported profile
        // with the user's/deterministic factor through ChangeStockItemInventoryUnit.
        if (baseUnitChanged && record.BaseUnit.Trim().Length > 0)
            MarkUnitProfilesPendingConfirmation(connection, transaction, record);

        transaction.Commit();
    }

    private static void RetargetStockItemNameReferences(
        SqliteConnection connection, SqliteTransaction transaction, long clientId, string financialYear, string oldName, string newName)
    {
        foreach (var sql in new[]
        {
            "UPDATE StockItemAliases SET StockItemName=$NewName,UpdatedUtc=$Now WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StockItemName=$OldName COLLATE NOCASE;",
            "UPDATE OR REPLACE StockUnitMappings SET StockItemName=$NewName,UpdatedUtc=$Now WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StockItemName=$OldName COLLATE NOCASE;",
            "UPDATE OR REPLACE QuantityConversionRules SET StockItemName=$NewName,UpdatedUtc=$Now WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StockItemName=$OldName COLLATE NOCASE;",
            "UPDATE StockItemUnitProfiles SET StockItemName=$NewName,UpdatedUtc=$Now WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StockItemName=$OldName COLLATE NOCASE;"
        })
        {
            using var retarget = connection.CreateCommand();
            retarget.Transaction = transaction;
            retarget.CommandText = sql;
            retarget.Parameters.AddWithValue("$ClientId", clientId);
            retarget.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            retarget.Parameters.AddWithValue("$OldName", oldName.Trim());
            retarget.Parameters.AddWithValue("$NewName", newName.Trim());
            retarget.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
            retarget.ExecuteNonQuery();
        }
    }

    private static void MarkUnitProfilesPendingConfirmation(
        SqliteConnection connection, SqliteTransaction transaction, StockItemRecord item)
    {
        using (var disable = connection.CreateCommand())
        {
            disable.Transaction = transaction;
            disable.CommandText =
                "UPDATE StockItemUnitProfiles SET IsAllowed=CASE WHEN UnitCode=$Base COLLATE NOCASE THEN 1 ELSE 0 END," +
                "FactorToBase=CASE WHEN UnitCode=$Base COLLATE NOCASE THEN 1 ELSE FactorToBase END," +
                "IsTallyAlternateUnit=0,Source=CASE WHEN UnitCode=$Base COLLATE NOCASE THEN 'Fix123 Base/Inventory Unit' ELSE 'Fix123 Pending Conversion Confirmation' END,UpdatedUtc=$Now " +
                "WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StockItemId=$StockItemId;";
            disable.Parameters.AddWithValue("$ClientId", item.ClientId);
            disable.Parameters.AddWithValue("$FinancialYear", item.FinancialYear.Trim());
            disable.Parameters.AddWithValue("$StockItemId", item.PersistentId);
            disable.Parameters.AddWithValue("$Base", item.BaseUnit.Trim());
            disable.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
            disable.ExecuteNonQuery();
        }
        using var ensureBase = connection.CreateCommand();
        ensureBase.Transaction = transaction;
        ensureBase.CommandText =
            "INSERT INTO StockItemUnitProfiles(ClientId,FinancialYear,StockItemId,StockItemName,UnitCode,FactorToBase,IsAllowed,IsTallyAlternateUnit,IsPackageConversion,Source,UpdatedUtc) " +
            "VALUES($ClientId,$FinancialYear,$StockItemId,$StockItemName,$Base,1,1,0,0,'Fix123 Base/Inventory Unit',$Now) " +
            "ON CONFLICT(ClientId,FinancialYear,StockItemId,UnitCode) DO UPDATE SET StockItemName=excluded.StockItemName,FactorToBase=1,IsAllowed=1,IsTallyAlternateUnit=0,Source=excluded.Source,UpdatedUtc=excluded.UpdatedUtc;";
        ensureBase.Parameters.AddWithValue("$ClientId", item.ClientId);
        ensureBase.Parameters.AddWithValue("$FinancialYear", item.FinancialYear.Trim());
        ensureBase.Parameters.AddWithValue("$StockItemId", item.PersistentId);
        ensureBase.Parameters.AddWithValue("$StockItemName", item.Name);
        ensureBase.Parameters.AddWithValue("$Base", item.BaseUnit.Trim());
        ensureBase.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        ensureBase.ExecuteNonQuery();
    }

    public void ChangeStockItemInventoryUnit(
        long clientId,
        string financialYear,
        string stockItemName,
        string finalUnit,
        string importedUnit,
        decimal factorToFinal,
        string source)
    {
        var finalCanonical = NormalizeCanonicalUnit(finalUnit);
        if (!AahUnitCatalog.TryGetDefinition(finalCanonical, out _))
            throw new InvalidOperationException($"'{finalUnit}' is not an AAH standard Unit.");
        if (factorToFinal <= 0m)
            throw new InvalidOperationException("Quantity Conversion factor must be greater than zero.");

        var item = GetStockItems(clientId, financialYear, includeArchived: true)
            .FirstOrDefault(row => row.Name.Equals(stockItemName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Stock Item '{stockItemName}' was not found.");

        var oldBase = NormalizeCanonicalUnit(item.BaseUnit);
        if (!UnitEquivalent(oldBase, finalCanonical) && item.TallyMasterId.Trim().Length > 0)
            throw new InvalidOperationException($"'{item.Name}' already exists in Tally as MASTERID {item.TallyMasterId}. Its Base/Inventory Unit cannot be replaced automatically. Configure an allowed/alternate Unit Profile instead.");

        if (!GetUnits(clientId, financialYear, includeArchived: false)
            .Any(unit => UnitEquivalent(unit.Name, finalCanonical)))
        {
            UpsertUnits(clientId, financialYear,
                [AahUnitCatalog.CreateUnitRecord(clientId, financialYear, finalCanonical, item.Company)],
                markUserEdited: true);
        }

        if (!UnitEquivalent(oldBase, finalCanonical))
        {
            var oldRateUnit = NormalizeCanonicalUnit(item.RateUnit);
            item.BaseUnit = finalCanonical;
            item.DefaultPurchaseUnit = finalCanonical;
            item.DefaultSalesUnit = finalCanonical;
            if (oldRateUnit.Length == 0 || UnitEquivalent(oldRateUnit, oldBase))
                item.RateUnit = finalCanonical;
            item.Source = Clean(source).Length == 0 ? "Manual Quantity Conversion" : Clean(source);
            item.IsUserEdited = true;
            SaveStockItem(item);
        }

        // Unit-profile factors are defined relative to BaseUnit, so changing BaseUnit
        // requires rewriting the existing profile factors. Deterministic physical/count
        // relationships are recalculated exactly. The explicitly selected imported unit
        // uses the user's factor. Any unrelated non-deterministic profile that cannot be
        // translated safely is retained as evidence but disabled until reviewed.
        var sourceCanonical = NormalizeCanonicalUnit(importedUnit);
        var profiles = GetStockItemUnitProfiles(clientId, financialYear, item.PersistentId).ToArray();
        // Fix121 R2 compile repair: oldBase can short-circuit the TryGetFactor call.
        // Give the factor a definite value before the conditional call so the later
        // hasOldToNew branch is valid under C# definite-assignment analysis.
        decimal oldToNew = 0m;
        var hasOldToNew = oldBase.Length > 0 &&
            AahUnitCatalog.TryGetFactor(oldBase, finalCanonical, out oldToNew);
        foreach (var profile in profiles)
        {
            var profileUnit = NormalizeCanonicalUnit(profile.UnitCode);
            if (UnitEquivalent(profileUnit, finalCanonical))
            {
                profile.FactorToBase = 1m;
                profile.IsAllowed = true;
            }
            else if (sourceCanonical.Length > 0 && UnitEquivalent(profileUnit, sourceCanonical))
            {
                profile.FactorToBase = factorToFinal;
                profile.IsAllowed = true;
            }
            else if (AahUnitCatalog.TryGetFactor(profileUnit, finalCanonical, out var direct))
            {
                profile.FactorToBase = direct;
            }
            else if (hasOldToNew && profile.FactorToBase > 0m)
            {
                profile.FactorToBase *= oldToNew;
            }
            else
            {
                profile.IsAllowed = false;
            }
            profile.IsTallyAlternateUnit = false;
            profile.Source = profile.IsAllowed ? "Fix121 Inventory Unit rebase" : "Fix121 Inventory Unit rebase - review required";
            profile.UpdatedUtc = DateTime.UtcNow;
            SaveStockItemUnitProfile(profile);
        }

        SaveStockItemUnitProfile(new StockItemUnitProfileRecord
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            StockItemId = item.PersistentId,
            StockItemName = item.Name,
            UnitCode = finalCanonical,
            FactorToBase = 1m,
            IsAllowed = true,
            Source = "Fix121 Base/Inventory Unit",
            UpdatedUtc = DateTime.UtcNow
        });

        if (sourceCanonical.Length > 0 && !UnitEquivalent(sourceCanonical, finalCanonical))
        {
            if (!GetUnits(clientId, financialYear, includeArchived: false).Any(unit => UnitEquivalent(unit.Name, sourceCanonical)) &&
                AahUnitCatalog.TryGetDefinition(sourceCanonical, out _))
            {
                UpsertUnits(clientId, financialYear,
                    [AahUnitCatalog.CreateUnitRecord(clientId, financialYear, sourceCanonical, item.Company)],
                    markUserEdited: true);
            }
            SaveStockItemUnitProfile(new StockItemUnitProfileRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                StockItemId = item.PersistentId,
                StockItemName = item.Name,
                UnitCode = sourceCanonical,
                FactorToBase = factorToFinal,
                IsAllowed = true,
                Source = "Fix121 Explicit Quantity Conversion",
                UpdatedUtc = DateTime.UtcNow
            });
        }
    }

    public void SaveUnit(StockUnitRecord record)
    {
        record.Name = AccountingText.UnitName(record.Name);
        record.FormalName = AccountingText.DisplayName(record.FormalName);
        record.ReportingUqc = AccountingText.UnitName(record.ReportingUqc);
        if (AahUnitCatalog.TryGetDefinition(record.Name, out var standardUnit))
        {
            if (string.IsNullOrWhiteSpace(record.FormalName)) record.FormalName = standardUnit.Description;
            if (string.IsNullOrWhiteSpace(record.ReportingUqc)) record.ReportingUqc = standardUnit.ReportingUqc;
        }
        record.Company = AccountingText.DisplayName(record.Company);
        record.DecimalPlaces = Math.Clamp(record.DecimalPlaces, 0, 4);
        if (record.PersistentId > 0)
        {
            var previous = GetUnits(record.ClientId, record.FinancialYear, includeArchived: true)
                .FirstOrDefault(unit => unit.PersistentId == record.PersistentId);
            if (previous is not null &&
                record.DecimalPlaces < previous.DecimalPlaces &&
                HasUnitInventoryUsage(record.ClientId, record.FinancialYear, previous.Name))
            {
                throw new InvalidOperationException($"Unit '{previous.Name}' already has inventory history. Tally permits increasing Decimal Places after use, but not reducing them.");
            }
        }
        EnsureNoUnitDuplicate(record);
        if (record.PersistentId <= 0)
        {
            UpsertUnits(record.ClientId, record.FinancialYear, [record], true);
            return;
        }
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE StockUnits SET Name=$Name,FormalName=$FormalName,DecimalPlaces=$DecimalPlaces,ReportingUqc=$ReportingUqc,Conversion=$Conversion,Company=$Company,Source=$Source,IsArchived=$IsArchived,IsUserEdited=1,UpdatedUtc=$Now WHERE Id=$Id;";
        AddUnitParameters(command, record.ClientId, record.FinancialYear, record, true);
        command.Parameters.AddWithValue("$Id", record.PersistentId);
        command.Parameters.AddWithValue("$IsArchived", record.IsArchived ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void SaveSimpleMaster(StockSimpleMasterRecord record)
    {
        record.Name = AccountingText.DisplayName(record.Name);
        record.Parent = string.Equals(record.Parent?.Trim(), "Primary", StringComparison.OrdinalIgnoreCase) ? "Primary" : AccountingText.DisplayName(record.Parent);
        record.Company = AccountingText.DisplayName(record.Company);
        EnsureNoSimpleDuplicate(record);
        if (record.PersistentId <= 0)
        {
            UpsertSimpleMasters(record.ClientId, record.FinancialYear, [record], true);
            return;
        }
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE StockSimpleMasters SET Type=$Type,Name=$Name,Parent=$Parent,Company=$Company,Source=$Source,IsArchived=$IsArchived,IsUserEdited=1,UpdatedUtc=$Now WHERE Id=$Id;";
        AddSimpleParameters(command, record.ClientId, record.FinancialYear, record, true);
        command.Parameters.AddWithValue("$Id", record.PersistentId);
        command.Parameters.AddWithValue("$IsArchived", record.IsArchived ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private void EnsureNoStockItemDuplicate(StockItemRecord record)
    {
        var key = AccountingText.DuplicateKey(record.Name);
        var duplicate = GetStockItems(record.ClientId, record.FinancialYear, includeArchived: true)
            .FirstOrDefault(item => item.PersistentId != record.PersistentId && AccountingText.DuplicateKey(item.Name) == key);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate Stock Item '{record.Name}' already exists as '{duplicate.Name}'.");
    }

    private void EnsureNoUnitDuplicate(StockUnitRecord record)
    {
        var key = AccountingText.DuplicateKey(record.Name);
        var duplicate = GetUnits(record.ClientId, record.FinancialYear, includeArchived: true)
            .FirstOrDefault(item => item.PersistentId != record.PersistentId && AccountingText.DuplicateKey(item.Name) == key);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate Unit '{record.Name}' already exists as '{duplicate.Name}'.");
    }

    private void EnsureNoSimpleDuplicate(StockSimpleMasterRecord record)
    {
        var typeKey = AccountingText.DuplicateKey(record.Type);
        var key = AccountingText.DuplicateKey(record.Name);
        var duplicate = GetSimpleMasters(record.ClientId, record.FinancialYear, record.Type, includeArchived: true)
            .FirstOrDefault(item => item.PersistentId != record.PersistentId && AccountingText.DuplicateKey(item.Type) == typeKey && AccountingText.DuplicateKey(item.Name) == key);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate {record.Type} '{record.Name}' already exists as '{duplicate.Name}'.");
    }

    public void ArchiveStockItem(long persistentId, bool archived) => SetArchive("StockItems", persistentId, archived);
    public void ArchiveUnit(long persistentId, bool archived) => SetArchive("StockUnits", persistentId, archived);
    public void ArchiveSimpleMaster(long persistentId, bool archived) => SetArchive("StockSimpleMasters", persistentId, archived);

    public IReadOnlyList<StockItemAliasRecord> GetItemAliases(long clientId, string financialYear)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ClientId,FinancialYear,ImportedAlias,StockItemName,Source,UpdatedUtc FROM StockItemAliases WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY ImportedAlias COLLATE NOCASE;";
        command.Parameters.AddWithValue("$ClientId", clientId); command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader(); var result = new List<StockItemAliasRecord>();
        while (reader.Read()) result.Add(new StockItemAliasRecord { PersistentId=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),ImportedAlias=reader.GetString(3),StockItemName=reader.GetString(4),Source=reader.GetString(5),UpdatedUtc=ReadDateTime(reader,6) });
        return result;
    }

    public void SaveItemAlias(long clientId, string financialYear, string importedAlias, string stockItemName, string source = "")
    {
        var alias = AccountingText.CleanWhitespace(importedAlias); var item = ResolveStockItemDisplayName(clientId,financialYear,stockItemName); if (alias.Length==0 || item.Length==0) return;
        var aliasSource = Clean(source);
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO StockItemAliases(ClientId,FinancialYear,ImportedAlias,StockItemName,Source,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Alias,$Item,$Source,$Now) ON CONFLICT(ClientId,FinancialYear,ImportedAlias) DO UPDATE SET StockItemName=excluded.StockItemName,Source=CASE WHEN excluded.Source<>'' THEN excluded.Source ELSE StockItemAliases.Source END,UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$Alias",alias); command.Parameters.AddWithValue("$Item",item); command.Parameters.AddWithValue("$Source",aliasSource); command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public IReadOnlyList<StockUnitMappingRecord> GetUnitMappings(long clientId, string financialYear)
    {
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="SELECT Id,ClientId,FinancialYear,StockItemName,ImportedUnit,FinalUnit,UpdatedUtc FROM StockUnitMappings WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY StockItemName COLLATE NOCASE,ImportedUnit COLLATE NOCASE;";
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim());
        using var reader=command.ExecuteReader(); var result=new List<StockUnitMappingRecord>();
        while(reader.Read()) result.Add(new StockUnitMappingRecord{PersistentId=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),StockItemName=reader.GetString(3),ImportedUnit=reader.GetString(4),FinalUnit=reader.GetString(5),UpdatedUtc=ReadDateTime(reader,6)});
        return result;
    }

    public void SaveUnitMapping(long clientId, string financialYear, string stockItemName, string importedUnit, string finalUnit)
    {
        var item=ResolveStockItemDisplayName(clientId,financialYear,stockItemName); var imported=AccountingText.UnitName(importedUnit); var final=ResolveUnitDisplayName(clientId,financialYear,finalUnit); if(item.Length==0||imported.Length==0||final.Length==0) return;
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO StockUnitMappings(ClientId,FinancialYear,StockItemName,ImportedUnit,FinalUnit,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Item,$Imported,$Final,$Now) ON CONFLICT(ClientId,FinancialYear,StockItemName,ImportedUnit) DO UPDATE SET FinalUnit=excluded.FinalUnit,UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$Item",item); command.Parameters.AddWithValue("$Imported",imported); command.Parameters.AddWithValue("$Final",final); command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public IReadOnlyList<QuantityConversionRule> GetQuantityConversionRules(long clientId, string financialYear)
    {
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="SELECT Id,ClientId,FinancialYear,StockItemName,ImportedUnit,FinalUnit,Factor,IsUniversal,RememberForClient,UpdatedUtc FROM QuantityConversionRules WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear ORDER BY StockItemName COLLATE NOCASE,ImportedUnit COLLATE NOCASE,FinalUnit COLLATE NOCASE;";
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim());
        using var reader=command.ExecuteReader(); var result=new List<QuantityConversionRule>();
        while(reader.Read()) result.Add(new QuantityConversionRule{PersistentId=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),StockItemName=reader.GetString(3),ImportedUnit=reader.GetString(4),FinalUnit=reader.GetString(5),Factor=Convert.ToDecimal(reader.GetValue(6),CultureInfo.InvariantCulture),IsUniversal=reader.GetInt64(7)!=0,RememberForClient=reader.GetInt64(8)!=0,UpdatedUtc=ReadDateTime(reader,9)});
        return result;
    }

    public void SaveQuantityConversionRule(QuantityConversionRule rule)
    {
        if(rule.Factor<=0m || string.IsNullOrWhiteSpace(rule.ImportedUnit) || string.IsNullOrWhiteSpace(rule.FinalUnit)) return;
        rule.StockItemName = ResolveStockItemDisplayName(rule.ClientId, rule.FinancialYear, rule.StockItemName);
        rule.ImportedUnit = AccountingText.UnitName(rule.ImportedUnit);
        rule.FinalUnit = ResolveUnitDisplayName(rule.ClientId, rule.FinancialYear, rule.FinalUnit);
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO QuantityConversionRules(ClientId,FinancialYear,StockItemName,ImportedUnit,FinalUnit,Factor,IsUniversal,RememberForClient,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Item,$Imported,$Final,$Factor,$Universal,$Remember,$Now) ON CONFLICT(ClientId,FinancialYear,StockItemName,ImportedUnit,FinalUnit) DO UPDATE SET Factor=excluded.Factor,IsUniversal=excluded.IsUniversal,RememberForClient=excluded.RememberForClient,UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$ClientId",rule.ClientId); command.Parameters.AddWithValue("$FinancialYear",rule.FinancialYear.Trim()); command.Parameters.AddWithValue("$Item",rule.StockItemName); command.Parameters.AddWithValue("$Imported",rule.ImportedUnit); command.Parameters.AddWithValue("$Final",rule.FinalUnit); command.Parameters.AddWithValue("$Factor",(double)rule.Factor); command.Parameters.AddWithValue("$Universal",rule.IsUniversal?1:0); command.Parameters.AddWithValue("$Remember",rule.RememberForClient?1:0); command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public IReadOnlyList<StockItemUnitProfileRecord> GetStockItemUnitProfiles(long clientId, string financialYear, long stockItemId = 0)
    {
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="SELECT Id,ClientId,FinancialYear,StockItemId,StockItemName,UnitCode,FactorToBase,IsAllowed,IsTallyAlternateUnit,IsPackageConversion,Source,UpdatedUtc FROM StockItemUnitProfiles WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND ($StockItemId=0 OR StockItemId=$StockItemId) ORDER BY StockItemName COLLATE NOCASE,UnitCode COLLATE NOCASE;";
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$StockItemId",stockItemId);
        using var reader=command.ExecuteReader(); var rows=new List<StockItemUnitProfileRecord>();
        while(reader.Read()) rows.Add(new StockItemUnitProfileRecord{PersistentId=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),StockItemId=reader.GetInt64(3),StockItemName=reader.GetString(4),UnitCode=reader.GetString(5),FactorToBase=Convert.ToDecimal(reader.GetValue(6),CultureInfo.InvariantCulture),IsAllowed=reader.GetInt64(7)!=0,IsTallyAlternateUnit=reader.GetInt64(8)!=0,IsPackageConversion=reader.GetInt64(9)!=0,Source=reader.GetString(10),UpdatedUtc=ReadDateTime(reader,11)});
        return rows;
    }

    public void SaveStockItemUnitProfile(StockItemUnitProfileRecord profile)
    {
        var item=GetStockItems(profile.ClientId,profile.FinancialYear,includeArchived:true).FirstOrDefault(x=>x.PersistentId==profile.StockItemId) ?? throw new InvalidOperationException("Select a saved Stock Item before adding its Unit Profile.");
        var unit=NormalizeCanonicalUnit(profile.UnitCode);
        if(unit.Length==0) throw new InvalidOperationException("Select a valid Unit for the Stock Item Unit Profile.");
        if(!AahUnitCatalog.TryGetDefinition(unit,out _) && !GetUnits(profile.ClientId,profile.FinancialYear,includeArchived:false).Any(saved=>saved.Name.Equals(unit,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Unit '{unit}' is neither an AAH standard Unit nor a saved Tally/AAH Unit Master.");
        if(profile.FactorToBase<=0m) throw new InvalidOperationException("Factor to Base Unit must be greater than zero.");
        var isBase=UnitEquivalent(unit,item.BaseUnit);
        if(isBase) profile.FactorToBase=1m;
        else if(AahUnitCatalog.TryGetFactor(unit,item.BaseUnit,out var deterministic)) profile.FactorToBase=deterministic;
        profile.IsPackageConversion=!isBase && !AahUnitCatalog.TryGetFactor(unit,item.BaseUnit,out _);
        if(profile.IsTallyAlternateUnit && isBase)
            throw new InvalidOperationException("Tally Alternate Unit must be different from the Stock Item Base/Inventory Unit.");
        if(profile.IsTallyAlternateUnit) profile.IsAllowed=true;

        using var connection=OpenConnection(); using var transaction=connection.BeginTransaction();
        using(var command=connection.CreateCommand())
        {
            command.Transaction=transaction;
            command.CommandText="INSERT INTO StockItemUnitProfiles(ClientId,FinancialYear,StockItemId,StockItemName,UnitCode,FactorToBase,IsAllowed,IsTallyAlternateUnit,IsPackageConversion,Source,UpdatedUtc) VALUES($ClientId,$FinancialYear,$StockItemId,$StockItemName,$UnitCode,$Factor,$Allowed,$Alternate,$Package,$Source,$Now) ON CONFLICT(ClientId,FinancialYear,StockItemId,UnitCode) DO UPDATE SET StockItemName=excluded.StockItemName,FactorToBase=excluded.FactorToBase,IsAllowed=excluded.IsAllowed,IsTallyAlternateUnit=excluded.IsTallyAlternateUnit,IsPackageConversion=excluded.IsPackageConversion,Source=excluded.Source,UpdatedUtc=excluded.UpdatedUtc;";
            command.Parameters.AddWithValue("$ClientId",profile.ClientId); command.Parameters.AddWithValue("$FinancialYear",profile.FinancialYear.Trim()); command.Parameters.AddWithValue("$StockItemId",profile.StockItemId); command.Parameters.AddWithValue("$StockItemName",item.Name); command.Parameters.AddWithValue("$UnitCode",unit); command.Parameters.AddWithValue("$Factor",(double)profile.FactorToBase); command.Parameters.AddWithValue("$Allowed",profile.IsAllowed?1:0); command.Parameters.AddWithValue("$Alternate",profile.IsTallyAlternateUnit?1:0); command.Parameters.AddWithValue("$Package",profile.IsPackageConversion?1:0); command.Parameters.AddWithValue("$Source",Clean(profile.Source)); command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
        }
        if(profile.IsTallyAlternateUnit)
        {
            using var clearOthers=connection.CreateCommand(); clearOthers.Transaction=transaction;
            clearOthers.CommandText="UPDATE StockItemUnitProfiles SET IsTallyAlternateUnit=0 WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StockItemId=$StockItemId AND UnitCode<>$UnitCode COLLATE NOCASE;";
            clearOthers.Parameters.AddWithValue("$ClientId",profile.ClientId); clearOthers.Parameters.AddWithValue("$FinancialYear",profile.FinancialYear.Trim()); clearOthers.Parameters.AddWithValue("$StockItemId",profile.StockItemId); clearOthers.Parameters.AddWithValue("$UnitCode",unit); clearOthers.ExecuteNonQuery();
        }
        using(var syncItem=connection.CreateCommand())
        {
            syncItem.Transaction=transaction;
            syncItem.CommandText="UPDATE StockItems SET AdditionalUnit=COALESCE((SELECT UnitCode FROM StockItemUnitProfiles WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StockItemId=$StockItemId AND IsTallyAlternateUnit=1 ORDER BY UpdatedUtc DESC LIMIT 1),''),UpdatedUtc=$Now WHERE Id=$StockItemId;";
            syncItem.Parameters.AddWithValue("$ClientId",profile.ClientId); syncItem.Parameters.AddWithValue("$FinancialYear",profile.FinancialYear.Trim()); syncItem.Parameters.AddWithValue("$StockItemId",profile.StockItemId); syncItem.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O")); syncItem.ExecuteNonQuery();
        }
        transaction.Commit();
        profile.UnitCode=unit; profile.StockItemName=item.Name; profile.UpdatedUtc=DateTime.UtcNow;
    }

    public void DeleteStockItemUnitProfile(long persistentId)
    {
        using var connection=OpenConnection(); using var transaction=connection.BeginTransaction();
        long stockItemId=0; long clientId=0; var financialYear=string.Empty;
        using(var read=connection.CreateCommand())
        {
            read.Transaction=transaction; read.CommandText="SELECT ClientId,FinancialYear,StockItemId FROM StockItemUnitProfiles WHERE Id=$Id;"; read.Parameters.AddWithValue("$Id",persistentId);
            using var reader=read.ExecuteReader(); if(reader.Read()){clientId=reader.GetInt64(0);financialYear=reader.GetString(1);stockItemId=reader.GetInt64(2);}
        }
        if(stockItemId>0)
        {
            using var profileRead=connection.CreateCommand(); profileRead.Transaction=transaction;
            profileRead.CommandText="SELECT p.UnitCode,s.BaseUnit,s.Name FROM StockItemUnitProfiles p JOIN StockItems s ON s.Id=p.StockItemId WHERE p.Id=$Id;";
            profileRead.Parameters.AddWithValue("$Id",persistentId);
            using var profileReader=profileRead.ExecuteReader();
            if(profileReader.Read() && UnitEquivalent(profileReader.GetString(0),profileReader.GetString(1)))
                throw new InvalidOperationException($"Base Unit profile for '{profileReader.GetString(2)}' is mandatory and cannot be removed.");
        }
        using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="DELETE FROM StockItemUnitProfiles WHERE Id=$Id;";command.Parameters.AddWithValue("$Id",persistentId);command.ExecuteNonQuery();}
        if(stockItemId>0)
        {
            using var syncItem=connection.CreateCommand(); syncItem.Transaction=transaction;
            syncItem.CommandText="UPDATE StockItems SET AdditionalUnit=COALESCE((SELECT UnitCode FROM StockItemUnitProfiles WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StockItemId=$StockItemId AND IsTallyAlternateUnit=1 ORDER BY UpdatedUtc DESC LIMIT 1),''),UpdatedUtc=$Now WHERE Id=$StockItemId;";
            syncItem.Parameters.AddWithValue("$ClientId",clientId);syncItem.Parameters.AddWithValue("$FinancialYear",financialYear);syncItem.Parameters.AddWithValue("$StockItemId",stockItemId);syncItem.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));syncItem.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private bool HasUnitInventoryUsage(long clientId, string financialYear, string unitName)
    {
        var normalized = AccountingText.UnitName(unitName);
        if (normalized.Length == 0) return false;

        foreach (var item in GetStockItems(clientId, financialYear, includeArchived: true)
                     .Where(item => UnitEquivalent(item.BaseUnit, normalized) ||
                                    UnitEquivalent(item.AdditionalUnit, normalized) ||
                                    UnitEquivalent(item.RateUnit, normalized)))
        {
            if (HasStockItemInventoryUsage(clientId, financialYear, item.PersistentId, item.Name))
                return true;
        }

        using var connection = OpenConnection();
        var unitNeedle = $"\"Unit\":\"{EscapeJsonNeedle(normalized)}\"";
        var rateNeedle = $"\"RateUnit\":\"{EscapeJsonNeedle(normalized)}\"";
        foreach (var table in new[] { "PurchaseRecords", "SalesRecords" })
        {
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$Table LIMIT 1;";
            exists.Parameters.AddWithValue("$Table", table);
            if (exists.ExecuteScalar() is null) continue;

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT RecordJson FROM {table} WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND RecordJson<>'';";
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (json.Contains(unitNeedle, StringComparison.OrdinalIgnoreCase) ||
                    json.Contains(rateNeedle, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    public bool HasStockItemInventoryUsage(long clientId,string financialYear,long stockItemId,string stockItemName)
    {
        // RecordJson is the durable inventory payload for both repositories. Historical rows
        // pre-dating Fix117 are covered by the item-name fallback; new rows carry StockItemPersistentId.
        using var connection=OpenConnection();
        foreach(var table in new[]{"PurchaseRecords","SalesRecords"})
        {
            using var exists=connection.CreateCommand(); exists.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$Table LIMIT 1;"; exists.Parameters.AddWithValue("$Table",table); if(exists.ExecuteScalar() is null) continue;
            using var command=connection.CreateCommand(); command.CommandText=$"SELECT RecordJson FROM {table} WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND RecordJson<>'';"; command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim());
            using var reader=command.ExecuteReader(); var idNeedle=$"\"StockItemPersistentId\":{stockItemId}"; var nameNeedle=$"\"ItemName\":\"{EscapeJsonNeedle(stockItemName)}\""; while(reader.Read()){var json=reader.IsDBNull(0)?string.Empty:reader.GetString(0);if(stockItemId>0&&json.Contains(idNeedle,StringComparison.Ordinal)||json.Contains(nameNeedle,StringComparison.OrdinalIgnoreCase))return true;}
        }
        return false;
    }

    private string ResolveStockItemDisplayName(long clientId, string financialYear, string? value)
    {
        var key = AccountingText.DuplicateKey(value);
        if (key.Length == 0) return string.Empty;
        var matches = GetStockItems(clientId, financialYear, includeArchived: false)
            .Where(item => AccountingText.DuplicateKey(item.Name) == key)
            .Take(2)
            .ToArray();
        if (matches.Length > 1) throw new InvalidOperationException($"Stock Item '{value}' matches duplicate Stock Masters.");
        return matches.Length == 1 ? matches[0].Name.Trim() : AccountingText.DisplayName(value);
    }

    private string ResolveUnitDisplayName(long clientId, string financialYear, string? value)
    {
        var key = AccountingText.DuplicateKey(value);
        if (key.Length == 0) return string.Empty;
        var matches = GetUnits(clientId, financialYear, includeArchived: false)
            .Where(item => AccountingText.DuplicateKey(item.Name) == key)
            .Take(2)
            .ToArray();
        if (matches.Length > 1) throw new InvalidOperationException($"Unit '{value}' matches duplicate Stock Masters.");
        return matches.Length == 1 ? matches[0].Name.Trim() : AccountingText.UnitName(value);
    }

    private void SetArchive(string table, long id, bool archived)
    {
        var allowed = table is "StockItems" or "StockUnits" or "StockSimpleMasters" ? table : throw new ArgumentOutOfRangeException(nameof(table));
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText=$"UPDATE {allowed} SET IsArchived=$Archived,IsUserEdited=1,UpdatedUtc=$Now WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Archived",archived?1:0); command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$Id",id); command.ExecuteNonQuery();
    }


    public IReadOnlyList<GstHsnSacDirectoryRecord> GetGstHsnSacDirectory(string searchText = "", int limit = 5000)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Code,Description,Kind,Source,UpdatedUtc
            FROM GstHsnSacDirectory
            WHERE $Search='' OR Code LIKE $Pattern OR Description LIKE $Pattern OR Kind LIKE $Pattern
            ORDER BY CASE Kind WHEN 'HSN' THEN 0 ELSE 1 END, LENGTH(Code), Code
            LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$Search", Clean(searchText));
        command.Parameters.AddWithValue("$Pattern", "%" + Clean(searchText) + "%");
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 50000));
        using var reader = command.ExecuteReader();
        var rows = new List<GstHsnSacDirectoryRecord>();
        while (reader.Read())
            rows.Add(new GstHsnSacDirectoryRecord
            {
                Code = reader.GetString(0),
                Description = reader.GetString(1),
                Kind = reader.GetString(2),
                Source = reader.GetString(3),
                UpdatedUtc = ReadDateTime(reader, 4)
            });
        return rows;
    }

    public int ReplaceGstHsnSacDirectory(IEnumerable<GstHsnSacDirectoryRecord> records, string source)
    {
        var incoming = records
            .Where(row => !string.IsNullOrWhiteSpace(row.Code))
            .Select(row => new GstHsnSacDirectoryRecord
            {
                Code = string.Concat(row.Code.Where(char.IsLetterOrDigit)).Trim().ToUpperInvariant(),
                Description = Clean(row.Description),
                Kind = row.Kind.Trim().ToUpperInvariant() is "SAC" ? "SAC" : "HSN",
                Source = Clean(string.IsNullOrWhiteSpace(row.Source) ? source : row.Source),
                UpdatedUtc = DateTime.UtcNow
            })
            .Where(row => row.Code.Length > 0)
            .GroupBy(row => row.Kind + "\u001f" + row.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(row => row.Description.Length).First())
            .ToArray();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM GstHsnSacDirectory;";
            clear.ExecuteNonQuery();
        }

        var count = 0;
        foreach (var row in incoming)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO GstHsnSacDirectory(Code,Description,Kind,Source,UpdatedUtc)
                VALUES($Code,$Description,$Kind,$Source,$Now);
                """;
            command.Parameters.AddWithValue("$Code", row.Code);
            command.Parameters.AddWithValue("$Description", row.Description);
            command.Parameters.AddWithValue("$Kind", row.Kind);
            command.Parameters.AddWithValue("$Source", row.Source);
            command.Parameters.AddWithValue("$Now", row.UpdatedUtc.ToString("O"));
            count += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return count;
    }

    private static void AddScope(SqliteCommand command,long clientId,string financialYear,bool includeArchived,string searchText)
    {
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$IncludeArchived",includeArchived?1:0); command.Parameters.AddWithValue("$Search",searchText.Trim()); command.Parameters.AddWithValue("$Pattern","%"+searchText.Trim()+"%");
    }

    private static void AddStockItemParameters(SqliteCommand command,long clientId,string financialYear,StockItemRecord item,bool markUserEdited)
    {
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$Name",Clean(item.Name)); command.Parameters.AddWithValue("$StockGroup",string.IsNullOrWhiteSpace(item.StockGroup)?"Primary":Clean(item.StockGroup)); command.Parameters.AddWithValue("$StockCategory",Clean(item.StockCategory)); command.Parameters.AddWithValue("$BaseUnit",NormalizeCanonicalUnit(item.BaseUnit)); command.Parameters.AddWithValue("$AdditionalUnit",NormalizeCanonicalUnit(item.AdditionalUnit)); command.Parameters.AddWithValue("$PartNumberSku",Clean(item.PartNumberSku)); command.Parameters.AddWithValue("$DefaultPurchaseUnit",NormalizeCanonicalUnit(item.DefaultPurchaseUnit)); command.Parameters.AddWithValue("$DefaultSalesUnit",NormalizeCanonicalUnit(item.DefaultSalesUnit)); command.Parameters.AddWithValue("$RateUnit",NormalizeCanonicalUnit(item.RateUnit)); command.Parameters.AddWithValue("$DefaultGodown",Clean(item.DefaultGodown)); command.Parameters.AddWithValue("$DefaultPurchaseLedger",Clean(item.DefaultPurchaseLedger)); command.Parameters.AddWithValue("$DefaultSalesLedger",Clean(item.DefaultSalesLedger)); command.Parameters.AddWithValue("$ReorderPoint",(double)item.ReorderPoint); command.Parameters.AddWithValue("$MinimumStock",(double)item.MinimumStock); command.Parameters.AddWithValue("$MaximumStock",(double)item.MaximumStock); command.Parameters.AddWithValue("$TallyMasterId",Clean(item.TallyMasterId)); command.Parameters.AddWithValue("$TallyGuid",Clean(item.TallyGuid)); command.Parameters.AddWithValue("$TallyAlterId",item.TallyAlterId); command.Parameters.AddWithValue("$HsnSac",Clean(item.HsnSac)); command.Parameters.AddWithValue("$GstRate",item.GstRate.HasValue?(object)(double)item.GstRate.Value:DBNull.Value); command.Parameters.AddWithValue("$GstTypeOfSupply",Clean(item.GstTypeOfSupply)); command.Parameters.AddWithValue("$BatchWise",item.BatchWise?1:0); command.Parameters.AddWithValue("$Perishable",item.Perishable?1:0); command.Parameters.AddWithValue("$HasManufacturingDate",item.HasManufacturingDate?1:0); command.Parameters.AddWithValue("$CostingMethod",Clean(item.CostingMethod)); command.Parameters.AddWithValue("$ValuationMethod",Clean(item.ValuationMethod)); command.Parameters.AddWithValue("$Company",Clean(item.Company)); command.Parameters.AddWithValue("$Source",Clean(item.Source)); command.Parameters.AddWithValue("$IsUserEdited",markUserEdited||item.IsUserEdited?1:0); command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
    }

    private static void AddUnitParameters(SqliteCommand command,long clientId,string financialYear,StockUnitRecord item,bool markUserEdited)
    {
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$Name",Clean(item.Name)); command.Parameters.AddWithValue("$FormalName",Clean(item.FormalName)); command.Parameters.AddWithValue("$DecimalPlaces",Math.Clamp(item.DecimalPlaces,0,4)); command.Parameters.AddWithValue("$ReportingUqc",Clean(item.ReportingUqc)); command.Parameters.AddWithValue("$Conversion",item.Conversion.HasValue?(object)(double)item.Conversion.Value:DBNull.Value); command.Parameters.AddWithValue("$Company",Clean(item.Company)); command.Parameters.AddWithValue("$Source",Clean(item.Source)); command.Parameters.AddWithValue("$IsUserEdited",markUserEdited||item.IsUserEdited?1:0); command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
    }

    private static void AddSimpleParameters(SqliteCommand command,long clientId,string financialYear,StockSimpleMasterRecord item,bool markUserEdited)
    {
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$Type",Clean(item.Type)); command.Parameters.AddWithValue("$Name",Clean(item.Name)); command.Parameters.AddWithValue("$Parent",string.IsNullOrWhiteSpace(item.Parent)?"Primary":Clean(item.Parent)); command.Parameters.AddWithValue("$Company",Clean(item.Company)); command.Parameters.AddWithValue("$Source",Clean(item.Source)); command.Parameters.AddWithValue("$IsUserEdited",markUserEdited||item.IsUserEdited?1:0); command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
    }

    private static StockItemRecord ReadStockItem(SqliteDataReader r)=>new(){PersistentId=r.GetInt64(r.GetOrdinal("Id")),ClientId=r.GetInt64(r.GetOrdinal("ClientId")),FinancialYear=ReadString(r,"FinancialYear"),Name=ReadString(r,"Name"),StockGroup=ReadString(r,"StockGroup"),StockCategory=ReadString(r,"StockCategory"),BaseUnit=ReadString(r,"BaseUnit"),AdditionalUnit=ReadString(r,"AdditionalUnit"),PartNumberSku=ReadString(r,"PartNumberSku"),DefaultPurchaseUnit=ReadString(r,"DefaultPurchaseUnit"),DefaultSalesUnit=ReadString(r,"DefaultSalesUnit"),RateUnit=ReadString(r,"RateUnit"),DefaultGodown=ReadString(r,"DefaultGodown"),DefaultPurchaseLedger=ReadString(r,"DefaultPurchaseLedger"),DefaultSalesLedger=ReadString(r,"DefaultSalesLedger"),ReorderPoint=ReadDecimal(r,"ReorderPoint"),MinimumStock=ReadDecimal(r,"MinimumStock"),MaximumStock=ReadDecimal(r,"MaximumStock"),TallyMasterId=ReadString(r,"TallyMasterId"),TallyGuid=ReadString(r,"TallyGuid"),TallyAlterId=ReadLong(r,"TallyAlterId"),HsnSac=ReadString(r,"HsnSac"),GstRate=ReadNullableDecimal(r,"GstRate"),GstTypeOfSupply=ReadString(r,"GstTypeOfSupply"),BatchWise=ReadBool(r,"BatchWise"),Perishable=ReadBool(r,"Perishable"),HasManufacturingDate=ReadBool(r,"HasManufacturingDate"),CostingMethod=ReadString(r,"CostingMethod"),ValuationMethod=ReadString(r,"ValuationMethod"),Company=ReadString(r,"Company"),Source=ReadString(r,"Source"),IsArchived=ReadBool(r,"IsArchived"),IsUserEdited=ReadBool(r,"IsUserEdited"),CreatedUtc=ReadDateTime(r,r.GetOrdinal("CreatedUtc")),UpdatedUtc=ReadDateTime(r,r.GetOrdinal("UpdatedUtc"))};
    private static StockUnitRecord ReadUnit(SqliteDataReader r)=>new(){PersistentId=r.GetInt64(r.GetOrdinal("Id")),ClientId=r.GetInt64(r.GetOrdinal("ClientId")),FinancialYear=ReadString(r,"FinancialYear"),Name=ReadString(r,"Name"),FormalName=ReadString(r,"FormalName"),DecimalPlaces=(int)r.GetInt64(r.GetOrdinal("DecimalPlaces")),ReportingUqc=ReadString(r,"ReportingUqc"),Conversion=ReadNullableDecimal(r,"Conversion"),Company=ReadString(r,"Company"),Source=ReadString(r,"Source"),IsArchived=ReadBool(r,"IsArchived"),IsUserEdited=ReadBool(r,"IsUserEdited"),CreatedUtc=ReadDateTime(r,r.GetOrdinal("CreatedUtc")),UpdatedUtc=ReadDateTime(r,r.GetOrdinal("UpdatedUtc"))};
    private static StockSimpleMasterRecord ReadSimple(SqliteDataReader r)=>new(){PersistentId=r.GetInt64(r.GetOrdinal("Id")),ClientId=r.GetInt64(r.GetOrdinal("ClientId")),FinancialYear=ReadString(r,"FinancialYear"),Type=ReadString(r,"Type"),Name=ReadString(r,"Name"),Parent=ReadString(r,"Parent"),Company=ReadString(r,"Company"),Source=ReadString(r,"Source"),IsArchived=ReadBool(r,"IsArchived"),IsUserEdited=ReadBool(r,"IsUserEdited"),CreatedUtc=ReadDateTime(r,r.GetOrdinal("CreatedUtc")),UpdatedUtc=ReadDateTime(r,r.GetOrdinal("UpdatedUtc"))};
    private static string ReadString(SqliteDataReader r,string name){var o=r.GetOrdinal(name);return r.IsDBNull(o)?string.Empty:Convert.ToString(r.GetValue(o),CultureInfo.InvariantCulture)??string.Empty;}
    private static bool ReadBool(SqliteDataReader r,string name){var o=r.GetOrdinal(name);return !r.IsDBNull(o)&&Convert.ToInt64(r.GetValue(o),CultureInfo.InvariantCulture)!=0;}
    private static decimal? ReadNullableDecimal(SqliteDataReader r,string name){var o=r.GetOrdinal(name);return r.IsDBNull(o)?null:Convert.ToDecimal(r.GetValue(o),CultureInfo.InvariantCulture);}
    private static DateTime ReadDateTime(SqliteDataReader r,int ordinal)=>DateTime.TryParse(r.IsDBNull(ordinal)?string.Empty:r.GetString(ordinal),null,DateTimeStyles.RoundtripKind,out var value)?value:DateTime.MinValue;
    private static void EnsureColumn(SqliteConnection connection,string table,string column,string definition)
    {
        using var info=connection.CreateCommand(); info.CommandText=$"PRAGMA table_info({table});"; using var reader=info.ExecuteReader(); while(reader.Read()) if(string.Equals(reader.GetString(1),column,StringComparison.OrdinalIgnoreCase)) return; reader.Close(); using var alter=connection.CreateCommand(); alter.CommandText=$"ALTER TABLE {table} ADD COLUMN {column} {definition};"; alter.ExecuteNonQuery();
    }
    private static string NormalizeCanonicalUnit(string? value)=>AahUnitCatalog.TryGetDefinition(value??string.Empty,out var d)?d.Code:AccountingText.UnitName(value);
    private static bool UnitEquivalent(string? a,string? b)=>AccountingText.DuplicateKey(a)==AccountingText.DuplicateKey(b)||(AahUnitCatalog.TryGetFactor(a??string.Empty,b??string.Empty,out var factor)&&factor==1m);
    private static long ReadLong(SqliteDataReader r,string name){var o=r.GetOrdinal(name);return r.IsDBNull(o)?0L:Convert.ToInt64(r.GetValue(o),CultureInfo.InvariantCulture);}
    private static decimal ReadDecimal(SqliteDataReader r,string name){var o=r.GetOrdinal(name);return r.IsDBNull(o)?0m:Convert.ToDecimal(r.GetValue(o),CultureInfo.InvariantCulture);}
    private static string EscapeJsonNeedle(string? value)=>(value??string.Empty).Replace("\\","\\\\",StringComparison.Ordinal).Replace("\"","\\\"",StringComparison.Ordinal);
    private static string Clean(string? value)=>string.Join(" ",(value??string.Empty).Replace('\r',' ').Replace('\n',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries)).Trim();

    private SqliteConnection OpenConnection(){
        return SqliteConnectionPolicy.Open(_connectionString);
    }
}
