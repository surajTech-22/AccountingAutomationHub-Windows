using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Sales;

public sealed class SqliteSalesRecordRepository :
    ISalesRecordRepository
{
    private readonly string _connectionString;

    public SqliteSalesRecordRepository() : this(ApplicationDataPaths.DatabasePath) { }

    // Allows real isolated SQLite registration regressions without touching a
    // Windows user's accounting database.
    public SqliteSalesRecordRepository(string databasePath)
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

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteSalesRecordRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS SalesImportBatches
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                ClientGstin         TEXT NOT NULL DEFAULT '',
                FinancialYear       TEXT NOT NULL,
                ImportMode          TEXT NOT NULL,
                Status              TEXT NOT NULL,
                EReturnsRoot        TEXT NOT NULL DEFAULT '',
                VerificationJson    TEXT NOT NULL DEFAULT '',
                SourceManifestJson  TEXT NOT NULL DEFAULT '',
                SourceFingerprint   TEXT NOT NULL DEFAULT '',
                SalesRows           INTEGER NOT NULL DEFAULT 0,
                CreditNoteRows      INTEGER NOT NULL DEFAULT 0,
                CreatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId)
                    REFERENCES Clients(Id)
                    ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SalesRecords
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                BatchId                 INTEGER NOT NULL,
                ClientId                INTEGER NOT NULL,
                ClientGstin             TEXT NOT NULL DEFAULT '',
                CompanyName             TEXT NOT NULL DEFAULT '',
                FinancialYear           TEXT NOT NULL DEFAULT '',
                ImportMode              TEXT NOT NULL DEFAULT '',
                Destination             TEXT NOT NULL DEFAULT '',
                CustomerGstin           TEXT NOT NULL DEFAULT '',
                CustomerName            TEXT NOT NULL DEFAULT '',
                CustomerNameSource      TEXT NOT NULL DEFAULT '',
                DocumentType            TEXT NOT NULL DEFAULT '',
                DocumentNumber          TEXT NOT NULL DEFAULT '',
                DocumentDate            TEXT NOT NULL DEFAULT '',
                GstDate                 TEXT NOT NULL DEFAULT '',
                SourceDocumentValue     REAL NOT NULL DEFAULT 0,
                DocumentValue           REAL NOT NULL DEFAULT 0,
                Rate                    REAL NOT NULL DEFAULT 0,
                TaxableValue            REAL NOT NULL DEFAULT 0,
                Igst                    REAL NOT NULL DEFAULT 0,
                Cgst                    REAL NOT NULL DEFAULT 0,
                Sgst                    REAL NOT NULL DEFAULT 0,
                Cess                    REAL NOT NULL DEFAULT 0,
                RoundOff                REAL NOT NULL DEFAULT 0,
                SalesLedger             TEXT NOT NULL DEFAULT '',
                IgstLedger              TEXT NOT NULL DEFAULT '',
                CgstLedger              TEXT NOT NULL DEFAULT '',
                SgstLedger              TEXT NOT NULL DEFAULT '',
                SourceSection           TEXT NOT NULL DEFAULT '',
                IsAmendment             INTEGER NOT NULL DEFAULT 0,
                OriginalDocumentNumber  TEXT NOT NULL DEFAULT '',
                OriginalDocumentDate    TEXT NOT NULL DEFAULT '',
                PlaceOfSupply           TEXT NOT NULL DEFAULT '',
                ReturnPeriod            TEXT NOT NULL DEFAULT '',
                SourceFile              TEXT NOT NULL DEFAULT '',
                SourcePath              TEXT NOT NULL DEFAULT '',
                SourceOrigin            TEXT NOT NULL DEFAULT '',
                SourceBranch            TEXT NOT NULL DEFAULT '',
                SourceScope             TEXT NOT NULL DEFAULT '',
                SourceHash              TEXT NOT NULL DEFAULT '',
                ResponseTimestamp       TEXT NOT NULL DEFAULT '',
                SalesMasterFile         TEXT NOT NULL DEFAULT '',
                ItemSequence            INTEGER NOT NULL DEFAULT 0,
                CreatedUtc              TEXT NOT NULL,
                UpdatedUtc              TEXT NOT NULL DEFAULT '',
                IsUserEdited            INTEGER NOT NULL DEFAULT 0,
                RecordJson              TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (BatchId)
                    REFERENCES SalesImportBatches(Id)
                    ON DELETE CASCADE,
                FOREIGN KEY (ClientId)
                    REFERENCES Clients(Id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS
                IX_SalesRecords_ClientFyMode
            ON SalesRecords
            (
                ClientId,
                FinancialYear,
                ImportMode
            );

            CREATE INDEX IF NOT EXISTS
                IX_SalesRecords_Document
            ON SalesRecords
            (
                ClientId,
                CustomerGstin,
                DocumentNumber,
                DocumentDate
            );

            CREATE TABLE IF NOT EXISTS SalesB2BCustomerNameOverrides
            (
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                CustomerGstin   TEXT NOT NULL,
                CustomerName    TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                PRIMARY KEY (ClientId, FinancialYear, CustomerGstin),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SalesB2CCustomerNameOverrides
            (
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                SourceKey       TEXT NOT NULL,
                CustomerName    TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                PRIMARY KEY (ClientId, FinancialYear, SourceKey),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SalesTallyRateMappingEvidence
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '',
                SourcePath      TEXT NOT NULL DEFAULT '',
                SourceHash      TEXT NOT NULL DEFAULT '',
                SalesDate       TEXT NOT NULL DEFAULT '',
                VoucherFrom     INTEGER NOT NULL DEFAULT 0,
                VoucherTo       INTEGER NOT NULL DEFAULT 0,
                RatesJson       TEXT NOT NULL DEFAULT '[]',
                UpdatedUtc      TEXT NOT NULL,
                UNIQUE(ClientId, FinancialYear, SourceHash, SalesDate, VoucherFrom, VoucherTo),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_SalesTallyRateMappingEvidence_ClientFyDate
            ON SalesTallyRateMappingEvidence(ClientId, FinancialYear, SalesDate);

            CREATE TABLE IF NOT EXISTS SalesMaintenanceMarkers
            (
                MarkerKey       TEXT PRIMARY KEY,
                AppliedUtc      TEXT NOT NULL
            );
            """;

        command.ExecuteNonQuery();
        SqliteSchemaColumnCache.EnsureColumn(connection, "SalesImportBatches", "ClientGstin", "TEXT NOT NULL DEFAULT ''");

        // Fix53: older GSTR-1 imports could persist the portal's complete GST-rate
        // template as zero-value Sales rows beside the one real populated rate bucket.
        // Repair only untouched Auto Import placeholders that have a substantive sibling
        // for the exact same source document. Genuine exempt/0% rows have taxable value
        // and are therefore preserved. Mark the affected batch non-current so the normal
        // automatic source route rebuilds Journal/GST/Masters when the source is available.
        RepairHistoricalAutomaticRateTemplatePlaceholders(connection);
        RepairAccountingOnlySalesAccountingAmount(connection);
        RepairHistoricalTallyXmlEffectiveRates(connection);
    }

    // Fix137 R23: ledger-only Sales must carry their authoritative taxable amount in
    // AccountingAmount as well as in the immutable source/document fields. Older rows
    // such as Sales 35 could display AccountingAmount=0 even though the valid ledger
    // voucher is Dr Customer / Cr Sales for the nonzero TaxableValue. Repair only rows
    // with no Stock Item, zero AccountingAmount and a nonzero TaxableValue. JsonNode is
    // used so unknown/new source metadata in RecordJson is preserved byte-for-byte in
    // meaning rather than being dropped by model reserialization.
    private static void RepairAccountingOnlySalesAccountingAmount(SqliteConnection connection)
    {
        const string marker = "FIX137-R23-ACCOUNTING-ONLY-SALES-AMOUNT-V1";
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM SalesMaintenanceMarkers WHERE MarkerKey=$K;";
            check.Parameters.AddWithValue("$K", marker);
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }

        using var transaction = connection.BeginTransaction();
        var candidates = new List<(long Id, decimal TaxableValue, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Id,TaxableValue,RecordJson FROM SalesRecords WHERE IsUserEdited=0 AND ABS(TaxableValue)>0.005 AND TRIM(COALESCE(RecordJson,''))<>'' ORDER BY Id;";
            using var reader = read.ExecuteReader();
            while (reader.Read()) candidates.Add((reader.GetInt64(0), ReadDecimal(reader, "TaxableValue"), reader.GetString(2)));
        }

        foreach (var candidate in candidates)
        {
            JsonObject? record;
            try { record = JsonNode.Parse(candidate.Json) as JsonObject; }
            catch { continue; }
            if (record is null) continue;
            string itemName;
            decimal accounting;
            try
            {
                if (record["IsUserEdited"]?.GetValue<bool>() == true) continue;
                itemName = record["ItemName"]?.GetValue<string>()?.Trim() ?? string.Empty;
                accounting = record["AccountingAmount"]?.GetValue<decimal>() ?? 0m;
            }
            catch { continue; } // Malformed legacy field types cannot stop repository startup.
            if (itemName.Length > 0) continue;
            if (Math.Abs(accounting) > 0.005m) continue;
            record["AccountingAmount"] = decimal.Round(candidate.TaxableValue, 2, MidpointRounding.AwayFromZero);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE SalesRecords SET RecordJson=$J,UpdatedUtc=$U WHERE Id=$I AND IsUserEdited=0 AND RecordJson=$Old;";
            update.Parameters.AddWithValue("$J", record.ToJsonString());
            update.Parameters.AddWithValue("$U", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$I", candidate.Id);
            update.Parameters.AddWithValue("$Old", candidate.Json);
            update.ExecuteNonQuery();
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT OR IGNORE INTO SalesMaintenanceMarkers(MarkerKey,AppliedUtc) VALUES($K,$U);";
            mark.Parameters.AddWithValue("$K", marker);
            mark.Parameters.AddWithValue("$U", DateTime.UtcNow.ToString("O"));
            mark.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public bool IsAutomaticImportCurrent(
        long clientId,
        string financialYear,
        string sourceFingerprint,
        string? registrationGstin = null)
    {
        if (string.IsNullOrWhiteSpace(
                sourceFingerprint))
        {
            return false;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT SourceFingerprint
            FROM SalesImportBatches
            WHERE
                ClientId = $ClientId
                AND FinancialYear = $FinancialYear
                AND ($RegistrationGstin='' OR ClientGstin=$RegistrationGstin COLLATE NOCASE)
                AND ImportMode = 'Auto Import'
                AND Status = 'Imported'
            ORDER BY Id DESC
            LIMIT 1;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$FinancialYear",
            financialYear.Trim());
        command.Parameters.AddWithValue("$RegistrationGstin", (registrationGstin ?? string.Empty).Trim().ToUpperInvariant());

        var current = Convert.ToString(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture)
            ?? string.Empty;

        return string.Equals(
            current,
            sourceFingerprint,
            StringComparison.OrdinalIgnoreCase);
    }

    public int ClearModule(long clientId, string financialYear, string? registrationGstin = null)
    {
        var target = (registrationGstin ?? string.Empty).Trim().ToUpperInvariant();
        if (target.Length > 0 && target.Length != 15)
            throw new InvalidOperationException("Select an exact 15-character registered GSTIN for a scoped module clear.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear" +
            (target.Length == 15 ? " AND ClientGstin=$Gstin COLLATE NOCASE;" : ";");
        count.Parameters.AddWithValue("$ClientId", clientId);
        count.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        if (target.Length == 15) count.Parameters.AddWithValue("$Gstin", target);
        var rows = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (target.Length == 15)
        {
            using var records = connection.CreateCommand();
            records.Transaction = transaction;
            records.CommandText = "DELETE FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND ClientGstin=$Gstin COLLATE NOCASE;";
            records.Parameters.AddWithValue("$ClientId", clientId);
            records.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            records.Parameters.AddWithValue("$Gstin", target);
            records.ExecuteNonQuery();
        }
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM SalesImportBatches WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear" +
            (target.Length == 15
                ? " AND ClientGstin=$Gstin COLLATE NOCASE;"
                : ";");
        delete.Parameters.AddWithValue("$ClientId", clientId);
        delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        if (target.Length == 15) delete.Parameters.AddWithValue("$Gstin", target);
        delete.ExecuteNonQuery();
        transaction.Commit();
        return rows;
    }

    public string GetLatestAutomaticImportContentFingerprint(
        long clientId,
        string financialYear,
        string? registrationGstin = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT VerificationJson, SourceManifestJson
            FROM SalesImportBatches
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND ($RegistrationGstin='' OR ClientGstin=$RegistrationGstin COLLATE NOCASE)
                AND ImportMode = 'Auto Import'
              AND Status = 'Imported'
            ORDER BY Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$RegistrationGstin", (registrationGstin ?? string.Empty).Trim().ToUpperInvariant());

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return string.Empty;
        try
        {
            var verification = JsonSerializer.Deserialize<SalesSourceVerification>(reader.GetString(0));
            var manifest = JsonSerializer.Deserialize<List<SalesSourceManifestItem>>(reader.GetString(1)) ?? [];
            return SalesPurchaseAccountingContentFingerprint.BuildSales(
                manifest, verification?.CustomerMasterSourceHash ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    public bool HasRecords(
        long clientId,
        string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1
                FROM SalesRecords
                WHERE
                    ClientId = $ClientId
                    AND FinancialYear = $FinancialYear
                LIMIT 1
            );
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$FinancialYear",
            financialYear.Trim());

        return Convert.ToInt32(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) == 1;
    }

    public IReadOnlyList<SalesImportRecord> GetAutomaticRecords(
        long clientId,
        string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT *
            FROM SalesRecords
            WHERE
                ClientId = $ClientId
                AND FinancialYear = $FinancialYear
                AND ImportMode = 'Auto Import'
            ORDER BY
                Destination COLLATE NOCASE,
                GstDate,
                DocumentDate,
                CustomerGstin COLLATE NOCASE,
                DocumentNumber COLLATE NOCASE,
                ItemSequence,
                Id;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$FinancialYear",
            financialYear.Trim());

        using var reader = command.ExecuteReader();

        var records = new List<SalesImportRecord>();

        while (reader.Read())
        {
            records.Add(
                ReadRecord(
                    reader));
        }

        return records;
    }

    public IReadOnlyList<SalesImportRecord> GetRecords(
        long clientId,
        string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT *
            FROM SalesRecords
            WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear
            ORDER BY
                Destination COLLATE NOCASE,
                COALESCE(NULLIF(GstDate,''), DocumentDate),
                DocumentDate,
                CustomerGstin COLLATE NOCASE,
                DocumentNumber COLLATE NOCASE,
                ItemSequence,
                Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var records = new List<SalesImportRecord>();
        while (reader.Read()) records.Add(ReadRecord(reader));
        return records;
    }

    // R9 FIXED16: repair already-imported Tally Voucher XML rows so the Sales grid
    // no longer displays 0% merely because one voucher contains a weighted mix of GST
    // slabs. Exact GST amounts and ledger names remain authoritative. This migration
    // is presentation/evidence-only and never marks rows as user-edited.
    private static void RepairHistoricalTallyXmlEffectiveRates(SqliteConnection connection)
    {
        const string marker = "R9-FIXED16-TALLY-XML-EFFECTIVE-RATE-V1";
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM SalesMaintenanceMarkers WHERE MarkerKey=$K;";
            check.Parameters.AddWithValue("$K", marker);
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }

        using var transaction = connection.BeginTransaction();
        var candidates = new List<(long Id, decimal Rate, decimal Taxable, decimal Igst, decimal Cgst, decimal Sgst, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Id,Rate,TaxableValue,Igst,Cgst,Sgst,RecordJson FROM SalesRecords WHERE IsUserEdited=0 AND UPPER(TRIM(COALESCE(SourceOrigin,'')))='TALLY VOUCHER XML' AND ABS(TaxableValue)>0.005 AND (ABS(Igst)+ABS(Cgst)+ABS(Sgst))>0.005 AND TRIM(COALESCE(RecordJson,''))<>'' ORDER BY Id;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((
                    reader.GetInt64(0),
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    reader.GetString(6)));
            }
        }

        foreach (var candidate in candidates)
        {
            JsonObject? json;
            try { json = JsonNode.Parse(candidate.Json) as JsonObject; }
            catch { continue; }
            if (json is null) continue;

            decimal rate;
            var mixed = false;
            string evidence;
            if (GstRateCatalog.TryDeriveFromTaxAmounts(candidate.Taxable, candidate.Igst, candidate.Cgst, candidate.Sgst, out var proved))
            {
                rate = proved;
                evidence = "GST rate proved from exact Tally XML tax ledgers.";
            }
            else if (TryEffectiveRate(candidate.Taxable, candidate.Igst, candidate.Cgst, candidate.Sgst, out var effective))
            {
                rate = effective;
                mixed = true;
                evidence = "Weighted GST rate derived from exact Tally XML tax ledgers; companion Sales Register can confirm the day/range slab mix.";
            }
            else continue;

            json["Rate"] = rate;
            json["IsEffectiveMixedGstRate"] = mixed;
            json["GstRateEvidence"] = evidence;
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE SalesRecords SET Rate=$R,RecordJson=$J,UpdatedUtc=$U WHERE Id=$I AND IsUserEdited=0;";
            update.Parameters.AddWithValue("$R", rate);
            update.Parameters.AddWithValue("$J", json.ToJsonString());
            update.Parameters.AddWithValue("$U", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$I", candidate.Id);
            update.ExecuteNonQuery();
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT OR IGNORE INTO SalesMaintenanceMarkers(MarkerKey,AppliedUtc) VALUES($K,$U);";
            mark.Parameters.AddWithValue("$K", marker);
            mark.Parameters.AddWithValue("$U", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            mark.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public int ApplyTallySalesRegisterRateMappings(
        long clientId,
        string financialYear,
        IReadOnlyList<SalesRateSummaryMapping> mappings,
        string sourcePath,
        string sourceHash)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || mappings.Count == 0) return 0;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var fy = financialYear.Trim();
        var fullSourcePath = SafeFullPath(sourcePath);
        var hash = (sourceHash ?? string.Empty).Trim();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM SalesTallyRateMappingEvidence WHERE ClientId=$C AND FinancialYear=$F AND (SourceHash=$H OR SourcePath=$P);";
            delete.Parameters.AddWithValue("$C", clientId);
            delete.Parameters.AddWithValue("$F", fy);
            delete.Parameters.AddWithValue("$H", hash);
            delete.Parameters.AddWithValue("$P", fullSourcePath);
            delete.ExecuteNonQuery();
        }

        foreach (var mapping in mappings)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR REPLACE INTO SalesTallyRateMappingEvidence(ClientId,FinancialYear,SourcePath,SourceHash,SalesDate,VoucherFrom,VoucherTo,RatesJson,UpdatedUtc) VALUES($C,$F,$P,$H,$D,$VF,$VT,$R,$U);";
            insert.Parameters.AddWithValue("$C", clientId);
            insert.Parameters.AddWithValue("$F", fy);
            insert.Parameters.AddWithValue("$P", fullSourcePath);
            insert.Parameters.AddWithValue("$H", hash);
            insert.Parameters.AddWithValue("$D", mapping.SalesDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$VF", mapping.VoucherFrom);
            insert.Parameters.AddWithValue("$VT", mapping.VoucherTo);
            insert.Parameters.AddWithValue("$R", JsonSerializer.Serialize(mapping.Rates.Distinct().OrderBy(rate => rate).ToArray()));
            insert.Parameters.AddWithValue("$U", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        var updated = ApplyRateMappingsToTallyXmlRows(connection, transaction, clientId, fy, mappings, fullSourcePath);
        transaction.Commit();
        return updated;
    }

    public int SaveManualImport(
        long clientId,
        string financialYear,
        SalesManualImportResult result,
        bool replaceExisting,
        string? replaceSourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (replaceExisting)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND ImportMode='Manual Import';";
            delete.Parameters.AddWithValue("$ClientId", clientId);
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            delete.ExecuteNonQuery();
        }
        else if (!string.IsNullOrWhiteSpace(replaceSourcePath))
        {
            var sourceBatchIds = FindManualSalesBatchIdsForSource(
                connection, transaction, clientId, financialYear, replaceSourcePath);
            foreach (var batchIdToReplace in sourceBatchIds)
            {
                using var deleteRows = connection.CreateCommand();
                deleteRows.Transaction = transaction;
                deleteRows.CommandText =
                    "DELETE FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND BatchId=$BatchId;";
                deleteRows.Parameters.AddWithValue("$ClientId", clientId);
                deleteRows.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
                deleteRows.Parameters.AddWithValue("$BatchId", batchIdToReplace);
                deleteRows.ExecuteNonQuery();

                using var deleteBatch = connection.CreateCommand();
                deleteBatch.Transaction = transaction;
                deleteBatch.CommandText =
                    "DELETE FROM SalesImportBatches WHERE Id=$BatchId AND ClientId=$ClientId AND FinancialYear=$FinancialYear AND ImportMode='Manual Import';";
                deleteBatch.Parameters.AddWithValue("$BatchId", batchIdToReplace);
                deleteBatch.Parameters.AddWithValue("$ClientId", clientId);
                deleteBatch.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
                deleteBatch.ExecuteNonQuery();
            }
        }

        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText =
                "SELECT CustomerGstin,CustomerName,DocumentNumber,DocumentDate,Destination,TaxableValue,Igst,Cgst,Sgst,Cess,RecordJson FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
            existing.Parameters.AddWithValue("$ClientId", clientId);
            existing.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            using var reader = existing.ExecuteReader();
            while (reader.Read())
            {
                SalesImportRecord? row = null;
                var json = reader.IsDBNull(10) ? string.Empty : reader.GetString(10);
                if (json.Length > 0)
                {
                    try { row = JsonSerializer.Deserialize<SalesImportRecord>(json); } catch { row = null; }
                }
                row ??= new SalesImportRecord
                {
                    CustomerGstin = reader.GetString(0), CustomerName = reader.GetString(1), DocumentNumber = reader.GetString(2),
                    DocumentDate = ReadDate(reader,"DocumentDate"), Destination = reader.GetString(4), TaxableValue = Convert.ToDecimal(reader.GetValue(5),CultureInfo.InvariantCulture),
                    Igst=Convert.ToDecimal(reader.GetValue(6),CultureInfo.InvariantCulture), Cgst=Convert.ToDecimal(reader.GetValue(7),CultureInfo.InvariantCulture),
                    Sgst=Convert.ToDecimal(reader.GetValue(8),CultureInfo.InvariantCulture), Cess=Convert.ToDecimal(reader.GetValue(9),CultureInfo.InvariantCulture)
                };
                existingKeys.Add(ManualDuplicateKey(row));
            }
        }

        long batchId;
        using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO SalesImportBatches
                (ClientId,FinancialYear,ImportMode,Status,EReturnsRoot,VerificationJson,SourceManifestJson,SourceFingerprint,SalesRows,CreditNoteRows,CreatedUtc)
                VALUES($ClientId,$FinancialYear,'Manual Import','Imported','', '', '', $Fingerprint,$SalesRows,$CreditRows,$CreatedUtc);
                SELECT last_insert_rowid();
                """;
            insertBatch.Parameters.AddWithValue("$ClientId", clientId);
            insertBatch.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            insertBatch.Parameters.AddWithValue("$Fingerprint", result.HeaderSignature ?? string.Empty);
            insertBatch.Parameters.AddWithValue("$SalesRows", result.SalesRows);
            insertBatch.Parameters.AddWithValue("$CreditRows", result.CreditNoteRows);
            insertBatch.Parameters.AddWithValue("$CreatedUtc", DateTime.UtcNow.ToString("O"));
            batchId = Convert.ToInt64(insertBatch.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var inserted = 0;
        foreach (var record in result.Records)
        {
            var key = ManualDuplicateKey(record);
            if (!existingKeys.Add(key)) continue;
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO SalesRecords
                (BatchId,ClientId,ClientGstin,CompanyName,FinancialYear,ImportMode,Destination,CustomerGstin,CustomerName,CustomerNameSource,DocumentType,DocumentNumber,DocumentDate,GstDate,SourceDocumentValue,DocumentValue,Rate,TaxableValue,Igst,Cgst,Sgst,Cess,RoundOff,SalesLedger,IgstLedger,CgstLedger,SgstLedger,SourceSection,IsAmendment,OriginalDocumentNumber,OriginalDocumentDate,PlaceOfSupply,ReturnPeriod,SourceFile,SourcePath,SourceOrigin,SourceBranch,SourceScope,SourceHash,ResponseTimestamp,SalesMasterFile,ItemSequence,CreatedUtc,UpdatedUtc,IsUserEdited,RecordJson)
                VALUES
                ($BatchId,$ClientId,$ClientGstin,$CompanyName,$FinancialYear,'Manual Import',$Destination,$CustomerGstin,$CustomerName,$CustomerNameSource,$DocumentType,$DocumentNumber,$DocumentDate,$GstDate,$SourceDocumentValue,$DocumentValue,$Rate,$TaxableValue,$Igst,$Cgst,$Sgst,$Cess,$RoundOff,$SalesLedger,$IgstLedger,$CgstLedger,$SgstLedger,$SourceSection,$IsAmendment,$OriginalDocumentNumber,$OriginalDocumentDate,$PlaceOfSupply,$ReturnPeriod,$SourceFile,$SourcePath,$SourceOrigin,$SourceBranch,$SourceScope,$SourceHash,$ResponseTimestamp,$SalesMasterFile,$ItemSequence,$CreatedUtc,'',0,$RecordJson);
                SELECT last_insert_rowid();
                """;
            AddParameters(insert, batchId, clientId, financialYear, record);
            insert.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
            var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            record.PersistentId=id; record.BatchId=batchId; record.ImportMode="Manual Import"; record.IsUserEdited=false; inserted++;
        }
        // If the Tally rate-summary sheets were mapped before the XML was imported,
        // enrich the newly inserted XML rows in the same commit.
        ApplyStoredRateMappingsToTallyXmlRows(connection, transaction, clientId, financialYear.Trim());
        transaction.Commit();
        return inserted;
    }

    private static IReadOnlyList<long> FindManualSalesBatchIdsForSource(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string sourcePath)
    {
        var matches = new List<long>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT DISTINCT BatchId,SourcePath FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND ImportMode='Manual Import' AND BatchId>0;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var savedSource = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (SameManualSalesSource(savedSource, sourcePath)) matches.Add(reader.GetInt64(0));
        }
        return matches.Distinct().ToArray();
    }

    private static bool SameManualSalesSource(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim()),
                Path.GetFullPath(right.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HashSet<string> LoadSalesDuplicateKeys(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText =
            "SELECT CustomerGstin,CustomerName,DocumentNumber,DocumentDate,Destination,TaxableValue,Igst,Cgst,Sgst,Cess,RecordJson FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
        existing.Parameters.AddWithValue("$ClientId", clientId);
        existing.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = existing.ExecuteReader();
        while (reader.Read())
        {
            SalesImportRecord? row = null;
            var json = reader.IsDBNull(10) ? string.Empty : reader.GetString(10);
            if (json.Length > 0)
            {
                try { row = JsonSerializer.Deserialize<SalesImportRecord>(json); } catch { row = null; }
            }
            row ??= new SalesImportRecord
            {
                CustomerGstin = reader.GetString(0),
                CustomerName = reader.GetString(1),
                DocumentNumber = reader.GetString(2),
                DocumentDate = ReadDate(reader, "DocumentDate"),
                Destination = reader.GetString(4),
                TaxableValue = Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                Igst = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                Cgst = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                Sgst = Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                Cess = Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture)
            };
            keys.Add(ManualDuplicateKey(row));
        }
        return keys;
    }

    private static string ManualDuplicateKey(SalesImportRecord row) =>
        string.Join("|",
            row.ClientGstin.Trim().ToUpperInvariant(),
            row.Destination.Trim().ToUpperInvariant(),
            row.DocumentDate?.ToString("yyyyMMdd",CultureInfo.InvariantCulture) ?? string.Empty,
            row.DocumentNumber.Trim().ToUpperInvariant(),
            row.CustomerGstin.Trim().ToUpperInvariant(),
            row.CustomerName.Trim().ToUpperInvariant(),
            row.ItemName.Trim().ToUpperInvariant(),
            row.HsnCode.Trim().ToUpperInvariant(),
            row.Unit.Trim().ToUpperInvariant(),
            row.ActualQuantity.ToString("0.######",CultureInfo.InvariantCulture),
            row.BilledQuantity.ToString("0.######",CultureInfo.InvariantCulture),
            row.ItemRate.ToString("0.######",CultureInfo.InvariantCulture),
            row.ItemAmount.ToString("0.00",CultureInfo.InvariantCulture),
            row.Rate.ToString("0.####",CultureInfo.InvariantCulture),
            row.TaxableValue.ToString("0.00",CultureInfo.InvariantCulture),
            row.Igst.ToString("0.00",CultureInfo.InvariantCulture),
            row.Cgst.ToString("0.00",CultureInfo.InvariantCulture),
            row.Sgst.ToString("0.00",CultureInfo.InvariantCulture),
            row.Cess.ToString("0.00",CultureInfo.InvariantCulture));

    private static int ApplyStoredRateMappingsToTallyXmlRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear)
    {
        var grouped = new Dictionary<string, List<SalesRateSummaryMapping>>(StringComparer.OrdinalIgnoreCase);
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT SourcePath,SalesDate,VoucherFrom,VoucherTo,RatesJson FROM SalesTallyRateMappingEvidence WHERE ClientId=$C AND FinancialYear=$F ORDER BY SalesDate,VoucherFrom;";
        read.Parameters.AddWithValue("$C", clientId);
        read.Parameters.AddWithValue("$F", financialYear);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var source = reader.GetString(0);
            if (!DateTime.TryParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            decimal[] rates;
            try { rates = JsonSerializer.Deserialize<decimal[]>(reader.GetString(4)) ?? []; }
            catch { rates = []; }
            if (!grouped.TryGetValue(source, out var list)) grouped[source] = list = [];
            list.Add(new SalesRateSummaryMapping
            {
                SalesDate = date.Date,
                VoucherFrom = reader.GetInt32(2),
                VoucherTo = reader.GetInt32(3),
                Rates = rates
            });
        }

        var updated = 0;
        foreach (var pair in grouped)
            updated += ApplyRateMappingsToTallyXmlRows(connection, transaction, clientId, financialYear, pair.Value, pair.Key);
        return updated;
    }

    private static int ApplyRateMappingsToTallyXmlRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        IReadOnlyList<SalesRateSummaryMapping> mappings,
        string evidenceSourcePath)
    {
        if (mappings.Count == 0) return 0;
        var byDate = mappings.GroupBy(mapping => mapping.SalesDate.Date)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var candidates = new List<(long Id, string DocumentNumber, DateTime? DocumentDate, DateTime? GstDate, decimal Taxable, decimal Igst, decimal Cgst, decimal Sgst, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Id,DocumentNumber,DocumentDate,GstDate,TaxableValue,Igst,Cgst,Sgst,RecordJson FROM SalesRecords WHERE ClientId=$C AND FinancialYear=$F AND IsUserEdited=0 AND UPPER(TRIM(COALESCE(SourceOrigin,'')))='TALLY VOUCHER XML' AND TRIM(COALESCE(RecordJson,''))<>'' ORDER BY Id;";
            read.Parameters.AddWithValue("$C", clientId);
            read.Parameters.AddWithValue("$F", financialYear);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    ParseStoredDate(reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                    ParseStoredDate(reader.IsDBNull(3) ? string.Empty : reader.GetString(3)),
                    Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    reader.GetString(8)));
            }
        }

        var updated = 0;
        foreach (var candidate in candidates)
        {
            var date = (candidate.GstDate ?? candidate.DocumentDate)?.Date;
            if (!date.HasValue || !byDate.TryGetValue(date.Value, out var dayMappings)) continue;
            var voucherNumber = ParseVoucherNumber(candidate.DocumentNumber);
            var applicable = dayMappings.Where(mapping =>
                mapping.VoucherFrom <= 0 || mapping.VoucherTo <= 0 || voucherNumber <= 0 ||
                (voucherNumber >= mapping.VoucherFrom && voucherNumber <= mapping.VoucherTo)).ToArray();
            if (applicable.Length == 0) continue;
            var rates = applicable.SelectMany(mapping => mapping.Rates).Distinct().OrderBy(rate => rate).ToArray();

            JsonObject? json;
            try { json = JsonNode.Parse(candidate.Json) as JsonObject; }
            catch { continue; }
            if (json is null) continue;

            decimal rate;
            var mixed = false;
            string calculation;
            if (GstRateCatalog.TryDeriveFromTaxAmounts(candidate.Taxable, candidate.Igst, candidate.Cgst, candidate.Sgst, out var proved))
            {
                rate = proved;
                calculation = $"XML tax arithmetic proves {FormatRate(proved)}%.";
            }
            else if (TryEffectiveRate(candidate.Taxable, candidate.Igst, candidate.Cgst, candidate.Sgst, out var effective))
            {
                rate = effective;
                mixed = true;
                calculation = $"Voucher is mixed; {FormatRate(effective)}% is the weighted effective rate only.";
            }
            else
            {
                rate = 0m;
                calculation = "No output GST amount is present.";
            }

            var mappedRateText = rates.Length == 0
                ? "rate buckets not printed for this range"
                : string.Join(", ", rates.Select(value => value == 0m ? "Exempted" : $"{FormatRate(value)}%"));
            var rangeText = applicable.Any(mapping => mapping.VoucherFrom > 0 && mapping.VoucherTo > 0)
                ? string.Join(", ", applicable.Where(mapping => mapping.VoucherFrom > 0 && mapping.VoucherTo > 0).Select(mapping => $"{mapping.VoucherFrom}-{mapping.VoucherTo}").Distinct())
                : "date-level";
            var evidence = $"Tally Sales Register '{Path.GetFileName(evidenceSourcePath)}' maps {date:dd-MM-yyyy} ({rangeText}) to {mappedRateText}. {calculation} Exact XML GST amounts/ledger names retained.";

            json["Rate"] = rate;
            json["IsEffectiveMixedGstRate"] = mixed;
            json["GstRateEvidence"] = evidence;
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE SalesRecords SET Rate=$R,RecordJson=$J,UpdatedUtc=$U WHERE Id=$I AND ClientId=$C AND IsUserEdited=0;";
            update.Parameters.AddWithValue("$R", rate);
            update.Parameters.AddWithValue("$J", json.ToJsonString());
            update.Parameters.AddWithValue("$U", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$I", candidate.Id);
            update.Parameters.AddWithValue("$C", clientId);
            updated += update.ExecuteNonQuery();
        }
        // R9 FIXED16 R15: if a later Tally XML import now fully covers a printed
        // voucher range, remove the non-exportable statement summary for that range so
        // Dashboard/Sales totals cannot double-count the same business activity.
        foreach (var mapping in mappings.Where(mapping => mapping.VoucherFrom > 0 && mapping.VoucherTo >= mapping.VoucherFrom))
        {
            var expected = mapping.VoucherTo - mapping.VoucherFrom + 1;
            var covered = candidates
                .Where(candidate => (candidate.GstDate ?? candidate.DocumentDate)?.Date == mapping.SalesDate.Date)
                .Select(candidate => ParseVoucherNumber(candidate.DocumentNumber))
                .Where(number => number >= mapping.VoucherFrom && number <= mapping.VoucherTo)
                .Distinct()
                .Count();
            if (covered < expected) continue;

            using var deleteSummary = connection.CreateCommand();
            deleteSummary.Transaction = transaction;
            deleteSummary.CommandText =
                "DELETE FROM SalesRecords WHERE ClientId=$C AND FinancialYear=$F AND UPPER(TRIM(COALESCE(SourceOrigin,'')))='TALLY SALES REGISTER SUMMARY' AND DocumentDate=$D AND DocumentNumber=$N;";
            deleteSummary.Parameters.AddWithValue("$C", clientId);
            deleteSummary.Parameters.AddWithValue("$F", financialYear);
            deleteSummary.Parameters.AddWithValue("$D", mapping.SalesDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            deleteSummary.Parameters.AddWithValue("$N", $"Vouchers {mapping.VoucherFrom}-{mapping.VoucherTo}");
            deleteSummary.ExecuteNonQuery();
        }

        return updated;
    }

    private static bool TryEffectiveRate(decimal taxable, decimal igst, decimal cgst, decimal sgst, out decimal rate)
    {
        rate = 0m;
        var baseAmount = Math.Abs(taxable);
        var tax = Math.Abs(igst) + Math.Abs(cgst) + Math.Abs(sgst);
        if (baseAmount <= 0.005m || tax <= 0.005m) return false;
        rate = decimal.Round(tax * 100m / baseAmount, 4, MidpointRounding.AwayFromZero);
        return rate > 0m;
    }

    private static string FormatRate(decimal rate) =>
        rate.ToString(rate == decimal.Truncate(rate) ? "0" : "0.####", CultureInfo.InvariantCulture);

    private static int ParseVoucherNumber(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct)) return direct;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static DateTime? ParseStoredDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Date : null;

    private static string SafeFullPath(string value)
    {
        try { return Path.GetFullPath((value ?? string.Empty).Trim()); }
        catch { return (value ?? string.Empty).Trim(); }
    }

    public int ReplaceAutomaticImport(
        long clientId,
        string financialYear,
        SalesAutoImportResult result)
    {
        ArgumentNullException.ThrowIfNull(
            result);

        // Manual names entered in the interactive B2B panel are not Masters.
        // They are durable Client/FY/GSTIN overrides and must survive every GSTR-1 reimport.
        var targetGstin = (result.ClientGstin ?? string.Empty).Trim().ToUpperInvariant();
        if (targetGstin.Length != 15)
            throw new InvalidOperationException("Automatic Sales replacement requires an exact registered GSTIN.");

        var b2bNameOverrides = GetB2BCustomerNameOverrides(clientId, financialYear);
        var b2cNameOverrides = GetB2CCustomerNameOverrides(clientId, financialYear);
        var recordsToPersist = RemoveAutomaticRateTemplatePlaceholders(result.Records).ToList();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM SalesRecords
                WHERE
                    ClientId = $ClientId
                    AND FinancialYear = $FinancialYear
                    AND ImportMode = 'Auto Import'
                    AND ClientGstin = $TargetGstin COLLATE NOCASE;
                """;

            delete.Parameters.AddWithValue(
                "$ClientId",
                clientId);

            delete.Parameters.AddWithValue(
                "$FinancialYear",
                financialYear.Trim());
            delete.Parameters.AddWithValue("$TargetGstin", targetGstin);

            delete.ExecuteNonQuery();
        }

        // FIXED29: Auto Import uses the same transaction duplicate identity as
        // Manual Import after source discovery. Retained Manual/other rows seed
        // the gate; the same HashSet also removes duplicates inside this auto set.
        var autoDuplicateKeys = LoadSalesDuplicateKeys(connection, transaction, clientId, financialYear);
        recordsToPersist = recordsToPersist
            .Where(record => autoDuplicateKeys.Add(ManualDuplicateKey(record)))
            .ToList();

        long batchId;

        using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO SalesImportBatches
                (
                    ClientId,
                    ClientGstin,
                    FinancialYear,
                    ImportMode,
                    Status,
                    EReturnsRoot,
                    VerificationJson,
                    SourceManifestJson,
                    SourceFingerprint,
                    SalesRows,
                    CreditNoteRows,
                    CreatedUtc
                )
                VALUES
                (
                    $ClientId,
                    $ClientGstin,
                    $FinancialYear,
                    'Auto Import',
                    'Imported',
                    $EReturnsRoot,
                    $VerificationJson,
                    $SourceManifestJson,
                    $SourceFingerprint,
                    $SalesRows,
                    $CreditNoteRows,
                    $CreatedUtc
                );

                SELECT last_insert_rowid();
                """;

            insertBatch.Parameters.AddWithValue(
                "$ClientId",
                clientId);
            insertBatch.Parameters.AddWithValue("$ClientGstin", targetGstin);
            insertBatch.Parameters.AddWithValue(
                "$FinancialYear",
                financialYear.Trim());
            insertBatch.Parameters.AddWithValue(
                "$EReturnsRoot",
                result.Verification.EReturnsRoot);
            insertBatch.Parameters.AddWithValue(
                "$VerificationJson",
                JsonSerializer.Serialize(
                    result.Verification));
            insertBatch.Parameters.AddWithValue(
                "$SourceManifestJson",
                JsonSerializer.Serialize(
                    result.SourceManifest));
            insertBatch.Parameters.AddWithValue(
                "$SourceFingerprint",
                result.SourceFingerprint);
            insertBatch.Parameters.AddWithValue(
                "$SalesRows",
                recordsToPersist.Count(record =>
                    !record.Destination.Contains("Credit", StringComparison.OrdinalIgnoreCase)));
            insertBatch.Parameters.AddWithValue(
                "$CreditNoteRows",
                recordsToPersist.Count(record =>
                    record.Destination.Contains("Credit", StringComparison.OrdinalIgnoreCase)));
            insertBatch.Parameters.AddWithValue(
                "$CreatedUtc",
                DateTime.UtcNow.ToString("O"));

            batchId = Convert.ToInt64(
                insertBatch.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        var inserted = 0;

        foreach (var record in recordsToPersist)
        {
            var normalizedCustomerGstin = NormalizeGstin(record.CustomerGstin);
            if (IsRegisteredB2BSection(record.SourceSection) &&
                normalizedCustomerGstin.Length == 15 &&
                b2bNameOverrides.TryGetValue(normalizedCustomerGstin, out var manualB2BName) &&
                !string.IsNullOrWhiteSpace(manualB2BName))
            {
                record.CustomerName = manualB2BName.Trim();
                record.CustomerNameSource = "Manual B2B Panel";
            }
            var b2cKey = BuildB2CSourceKey(record);
            if (b2cKey.Length > 0 && b2cNameOverrides.TryGetValue(b2cKey, out var manualB2CName) && !string.IsNullOrWhiteSpace(manualB2CName))
            {
                record.CustomerName = manualB2CName.Trim();
                record.CustomerNameSource = "Manual B2C Panel";
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO SalesRecords
                (
                    BatchId,
                    ClientId,
                    ClientGstin,
                    CompanyName,
                    FinancialYear,
                    ImportMode,
                    Destination,
                    CustomerGstin,
                    CustomerName,
                    CustomerNameSource,
                    DocumentType,
                    DocumentNumber,
                    DocumentDate,
                    GstDate,
                    SourceDocumentValue,
                    DocumentValue,
                    Rate,
                    TaxableValue,
                    Igst,
                    Cgst,
                    Sgst,
                    Cess,
                    RoundOff,
                    SalesLedger,
                    IgstLedger,
                    CgstLedger,
                    SgstLedger,
                    SourceSection,
                    IsAmendment,
                    OriginalDocumentNumber,
                    OriginalDocumentDate,
                    PlaceOfSupply,
                    ReturnPeriod,
                    SourceFile,
                    SourcePath,
                    SourceOrigin,
                    SourceBranch,
                    SourceScope,
                    SourceHash,
                    ResponseTimestamp,
                    SalesMasterFile,
                    ItemSequence,
                    CreatedUtc,
                    UpdatedUtc,
                    IsUserEdited,
                    RecordJson
                )
                VALUES
                (
                    $BatchId,
                    $ClientId,
                    $ClientGstin,
                    $CompanyName,
                    $FinancialYear,
                    'Auto Import',
                    $Destination,
                    $CustomerGstin,
                    $CustomerName,
                    $CustomerNameSource,
                    $DocumentType,
                    $DocumentNumber,
                    $DocumentDate,
                    $GstDate,
                    $SourceDocumentValue,
                    $DocumentValue,
                    $Rate,
                    $TaxableValue,
                    $Igst,
                    $Cgst,
                    $Sgst,
                    $Cess,
                    $RoundOff,
                    $SalesLedger,
                    $IgstLedger,
                    $CgstLedger,
                    $SgstLedger,
                    $SourceSection,
                    $IsAmendment,
                    $OriginalDocumentNumber,
                    $OriginalDocumentDate,
                    $PlaceOfSupply,
                    $ReturnPeriod,
                    $SourceFile,
                    $SourcePath,
                    $SourceOrigin,
                    $SourceBranch,
                    $SourceScope,
                    $SourceHash,
                    $ResponseTimestamp,
                    $SalesMasterFile,
                    $ItemSequence,
                    $CreatedUtc,
                    '',
                    0,
                    $RecordJson
                );

                SELECT last_insert_rowid();
                """;

            AddParameters(
                insert,
                batchId,
                clientId,
                financialYear,
                record);

            insert.Parameters.AddWithValue(
                "$RecordJson",
                JsonSerializer.Serialize(
                    record));

            var recordId = Convert.ToInt64(
                insert.ExecuteScalar(),
                CultureInfo.InvariantCulture);

            record.PersistentId = recordId;
            record.BatchId = batchId;
            record.IsUserEdited = false;
            inserted++;
        }

        transaction.Commit();

        return inserted;
    }

    public int ReplaceAutomaticImportMonth(
        long clientId,
        string financialYear,
        SalesAutoImportResult result,
        DateTime month)
    {
        ArgumentNullException.ThrowIfNull(result);

        var targetGstin = (result.ClientGstin ?? string.Empty).Trim().ToUpperInvariant();
        if (targetGstin.Length != 15) throw new InvalidOperationException("Monthly reimport requires a registered GSTIN.");
        var selectedMonth = new DateTime(month.Year, month.Month, 1);
        var existingMonthRecords = GetAutomaticRecords(clientId, financialYear)
            .Where(record => record.ClientGstin.Equals(targetGstin, StringComparison.OrdinalIgnoreCase) && IsRecordInReimportMonth(record, selectedMonth))
            .ToArray();
        var incomingRecords = RemoveAutomaticRateTemplatePlaceholders(result.Records)
            .Where(record => record.ClientGstin.Equals(targetGstin, StringComparison.OrdinalIgnoreCase) && IsRecordInReimportMonth(record, selectedMonth))
            .ToList();

        // Fix13 Sales selected-month contract: only automatic rows belonging to the
        // selected GSTR-1 return month are replaced. Manual Sales Register rows and all
        // other automatic months keep their existing IDs, BatchIds and edit state.
        var b2bNameOverrides = GetB2BCustomerNameOverrides(clientId, financialYear);
        var b2cNameOverrides = GetB2CCustomerNameOverrides(clientId, financialYear);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var existing in existingMonthRecords)
        {
            if (existing.PersistentId <= 0) continue;
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM SalesRecords WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear AND ImportMode='Auto Import';";
            delete.Parameters.AddWithValue("$Id", existing.PersistentId);
            delete.Parameters.AddWithValue("$ClientId", clientId);
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            delete.ExecuteNonQuery();
        }

        var monthDuplicateKeys = LoadSalesDuplicateKeys(connection, transaction, clientId, financialYear);
        incomingRecords = incomingRecords
            .Where(record => monthDuplicateKeys.Add(ManualDuplicateKey(record)))
            .ToList();

        var salesRows = incomingRecords.Count(record =>
            !record.Destination.Contains("Credit", StringComparison.OrdinalIgnoreCase));
        var creditNoteRows = incomingRecords.Count - salesRows;

        long batchId;
        using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO SalesImportBatches
                (ClientId,FinancialYear,ImportMode,Status,EReturnsRoot,VerificationJson,SourceManifestJson,SourceFingerprint,SalesRows,CreditNoteRows,CreatedUtc)
                VALUES
                ($ClientId,$FinancialYear,'Auto Reimport Month','Imported',$EReturnsRoot,$VerificationJson,$SourceManifestJson,$SourceFingerprint,$SalesRows,$CreditNoteRows,$CreatedUtc);
                SELECT last_insert_rowid();
                """;
            insertBatch.Parameters.AddWithValue("$ClientId", clientId);
            insertBatch.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            insertBatch.Parameters.AddWithValue("$EReturnsRoot", result.Verification.EReturnsRoot);
            insertBatch.Parameters.AddWithValue("$VerificationJson", JsonSerializer.Serialize(result.Verification));
            insertBatch.Parameters.AddWithValue("$SourceManifestJson", JsonSerializer.Serialize(result.SourceManifest));
            insertBatch.Parameters.AddWithValue("$SourceFingerprint", $"MONTH:{selectedMonth:yyyyMM}|{result.SourceFingerprint}");
            insertBatch.Parameters.AddWithValue("$SalesRows", salesRows);
            insertBatch.Parameters.AddWithValue("$CreditNoteRows", creditNoteRows);
            insertBatch.Parameters.AddWithValue("$CreatedUtc", DateTime.UtcNow.ToString("O"));
            batchId = Convert.ToInt64(insertBatch.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var inserted = 0;
        foreach (var record in incomingRecords)
        {
            var normalizedCustomerGstin = NormalizeGstin(record.CustomerGstin);
            if (IsRegisteredB2BSection(record.SourceSection) &&
                normalizedCustomerGstin.Length == 15 &&
                b2bNameOverrides.TryGetValue(normalizedCustomerGstin, out var manualB2BName) &&
                !string.IsNullOrWhiteSpace(manualB2BName))
            {
                record.CustomerName = manualB2BName.Trim();
                record.CustomerNameSource = "Manual B2B Panel";
            }

            var b2cKey = BuildB2CSourceKey(record);
            if (b2cKey.Length > 0 &&
                b2cNameOverrides.TryGetValue(b2cKey, out var manualB2CName) &&
                !string.IsNullOrWhiteSpace(manualB2CName))
            {
                record.CustomerName = manualB2CName.Trim();
                record.CustomerNameSource = "Manual B2C Panel";
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO SalesRecords
                (
                    BatchId,ClientId,ClientGstin,CompanyName,FinancialYear,ImportMode,Destination,
                    CustomerGstin,CustomerName,CustomerNameSource,DocumentType,DocumentNumber,DocumentDate,GstDate,
                    SourceDocumentValue,DocumentValue,Rate,TaxableValue,Igst,Cgst,Sgst,Cess,RoundOff,
                    SalesLedger,IgstLedger,CgstLedger,SgstLedger,SourceSection,IsAmendment,OriginalDocumentNumber,
                    OriginalDocumentDate,PlaceOfSupply,ReturnPeriod,SourceFile,SourcePath,SourceOrigin,SourceBranch,
                    SourceScope,SourceHash,ResponseTimestamp,SalesMasterFile,ItemSequence,CreatedUtc,UpdatedUtc,
                    IsUserEdited,RecordJson
                )
                VALUES
                (
                    $BatchId,$ClientId,$ClientGstin,$CompanyName,$FinancialYear,'Auto Import',$Destination,
                    $CustomerGstin,$CustomerName,$CustomerNameSource,$DocumentType,$DocumentNumber,$DocumentDate,$GstDate,
                    $SourceDocumentValue,$DocumentValue,$Rate,$TaxableValue,$Igst,$Cgst,$Sgst,$Cess,$RoundOff,
                    $SalesLedger,$IgstLedger,$CgstLedger,$SgstLedger,$SourceSection,$IsAmendment,$OriginalDocumentNumber,
                    $OriginalDocumentDate,$PlaceOfSupply,$ReturnPeriod,$SourceFile,$SourcePath,$SourceOrigin,$SourceBranch,
                    $SourceScope,$SourceHash,$ResponseTimestamp,$SalesMasterFile,$ItemSequence,$CreatedUtc,'',0,$RecordJson
                );
                SELECT last_insert_rowid();
                """;

            AddParameters(insert, batchId, clientId, financialYear, record);
            insert.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
            var recordId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            record.PersistentId = recordId;
            record.BatchId = batchId;
            record.ImportMode = "Auto Import";
            record.IsUserEdited = false;
            inserted++;
        }

        transaction.Commit();
        return inserted;
    }

    private static IReadOnlyList<SalesImportRecord> RemoveAutomaticRateTemplatePlaceholders(
        IEnumerable<SalesImportRecord> records)
    {
        var source = records.ToArray();
        if (source.Length <= 1) return source;

        var substantiveDocuments = source
            .Where(HasSubstantiveSalesRateEvidence)
            .Select(AutomaticSourceDocumentIdentity)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (substantiveDocuments.Count == 0) return source;

        return source
            .Where(record =>
                !IsZeroValueRateTemplateBucket(record) ||
                !substantiveDocuments.Contains(AutomaticSourceDocumentIdentity(record)))
            .ToArray();
    }

    private static bool IsZeroValueRateTemplateBucket(SalesImportRecord record) =>
        !record.IsUserEdited &&
        Math.Abs(record.TaxableValue) <= 0.005m &&
        Math.Abs(record.Igst) <= 0.005m &&
        Math.Abs(record.Cgst) <= 0.005m &&
        Math.Abs(record.Sgst) <= 0.005m &&
        Math.Abs(record.Cess) <= 0.005m;

    private static bool HasSubstantiveSalesRateEvidence(SalesImportRecord record) =>
        Math.Abs(record.TaxableValue) > 0.005m ||
        Math.Abs(record.Igst) > 0.005m ||
        Math.Abs(record.Cgst) > 0.005m ||
        Math.Abs(record.Sgst) > 0.005m ||
        Math.Abs(record.Cess) > 0.005m;

    private static string AutomaticSourceDocumentIdentity(SalesImportRecord record)
    {
        var documentNumber = (record.DocumentNumber ?? string.Empty).Trim();
        var sourceHash = (record.SourceHash ?? string.Empty).Trim();
        var sourcePath = (record.SourcePath ?? string.Empty).Trim();
        if (documentNumber.Length == 0 && sourceHash.Length == 0 && sourcePath.Length == 0)
            return string.Empty;

        return string.Join("|",
            (record.Destination ?? string.Empty).Trim().ToUpperInvariant(),
            (record.SourceSection ?? string.Empty).Trim().ToUpperInvariant(),
            (record.DocumentType ?? string.Empty).Trim().ToUpperInvariant(),
            documentNumber.ToUpperInvariant(),
            record.DocumentDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            (record.CustomerGstin ?? string.Empty).Trim().ToUpperInvariant(),
            (record.OriginalDocumentNumber ?? string.Empty).Trim().ToUpperInvariant(),
            (record.PlaceOfSupply ?? string.Empty).Trim().ToUpperInvariant(),
            (record.ReturnPeriod ?? string.Empty).Trim().ToUpperInvariant(),
            sourceHash.Length > 0 ? sourceHash.ToUpperInvariant() : sourcePath.ToUpperInvariant());
    }

    private static void RepairHistoricalAutomaticRateTemplatePlaceholders(SqliteConnection connection)
    {
        const string placeholderPredicate = """
            P.ImportMode='Auto Import'
            AND P.IsUserEdited=0
            AND ABS(P.TaxableValue)<=0.005
            AND ABS(P.Igst)<=0.005
            AND ABS(P.Cgst)<=0.005
            AND ABS(P.Sgst)<=0.005
            AND ABS(P.Cess)<=0.005
            AND EXISTS
            (
                SELECT 1
                FROM SalesRecords A
                WHERE A.ClientId=P.ClientId
                  AND A.FinancialYear=P.FinancialYear
                  AND A.BatchId=P.BatchId
                  AND UPPER(TRIM(A.Destination))=UPPER(TRIM(P.Destination))
                  AND UPPER(TRIM(A.SourceSection))=UPPER(TRIM(P.SourceSection))
                  AND UPPER(TRIM(A.DocumentType))=UPPER(TRIM(P.DocumentType))
                  AND UPPER(TRIM(A.DocumentNumber))=UPPER(TRIM(P.DocumentNumber))
                  AND COALESCE(A.DocumentDate,'')=COALESCE(P.DocumentDate,'')
                  AND UPPER(TRIM(A.CustomerGstin))=UPPER(TRIM(P.CustomerGstin))
                  AND UPPER(TRIM(A.OriginalDocumentNumber))=UPPER(TRIM(P.OriginalDocumentNumber))
                  AND UPPER(TRIM(A.PlaceOfSupply))=UPPER(TRIM(P.PlaceOfSupply))
                  AND UPPER(TRIM(A.ReturnPeriod))=UPPER(TRIM(P.ReturnPeriod))
                  AND UPPER(TRIM(CASE WHEN TRIM(P.SourceHash)<>'' THEN A.SourceHash ELSE A.SourcePath END))
                      =UPPER(TRIM(CASE WHEN TRIM(P.SourceHash)<>'' THEN P.SourceHash ELSE P.SourcePath END))
                  AND
                  (
                      ABS(A.TaxableValue)>0.005
                      OR ABS(A.Igst)>0.005
                      OR ABS(A.Cgst)>0.005
                      OR ABS(A.Sgst)>0.005
                      OR ABS(A.Cess)>0.005
                  )
            )
            """;

        using (var mark = connection.CreateCommand())
        {
            mark.CommandText = $"""
                UPDATE SalesImportBatches
                SET Status='Repair Pending Reimport'
                WHERE Status='Imported'
                  AND Id IN
                  (
                      SELECT DISTINCT P.BatchId
                      FROM SalesRecords P
                      WHERE {placeholderPredicate}
                  );
                """;
            mark.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM SalesRecords AS P WHERE {placeholderPredicate};";
            delete.ExecuteNonQuery();
        }

        using var recount = connection.CreateCommand();
        recount.CommandText = """
            UPDATE SalesImportBatches
            SET
                SalesRows=(SELECT COUNT(*) FROM SalesRecords R WHERE R.BatchId=SalesImportBatches.Id AND UPPER(R.Destination) NOT LIKE '%CREDIT%'),
                CreditNoteRows=(SELECT COUNT(*) FROM SalesRecords R WHERE R.BatchId=SalesImportBatches.Id AND UPPER(R.Destination) LIKE '%CREDIT%')
            WHERE Status='Repair Pending Reimport';
            """;
        recount.ExecuteNonQuery();
    }

    private static bool IsRecordInReimportMonth(SalesImportRecord record, DateTime selectedMonth)
    {
        if (PurchaseImportUtilities.TryParseReturnPeriod(record.ReturnPeriod, out var returnPeriod))
        {
            return returnPeriod.Year == selectedMonth.Year &&
                   returnPeriod.Month == selectedMonth.Month;
        }

        var fallback = record.GstDate ?? record.DocumentDate;
        return fallback.HasValue &&
               fallback.Value.Year == selectedMonth.Year &&
               fallback.Value.Month == selectedMonth.Month;
    }

    public bool HasAutomaticCustomerFallbacks(long clientId, string financialYear)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM SalesRecords
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND IsUserEdited=0
                  AND (UPPER(TRIM(SourceSection)) LIKE 'B2B%' OR UPPER(TRIM(SourceSection)) LIKE 'CDNR%')
                  AND (TRIM(COALESCE(CustomerNameSource,''))=''
                       OR UPPER(COALESCE(CustomerNameSource,'')) LIKE '%FALLBACK%'
                       OR TRIM(COALESCE(CustomerName,''))=''
                       OR UPPER(TRIM(CustomerName))=UPPER(TRIM(CustomerGstin))
                       OR UPPER(TRIM(CustomerName)) LIKE 'CUSTOMER - %'
                       OR UPPER(TRIM(CustomerName)) LIKE 'CUSTOMER %')
                LIMIT 1);
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public int BackfillAutomaticCustomerNames(
        long clientId,
        string financialYear,
        IReadOnlyDictionary<string, string> names)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || names.Count == 0) return 0;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var updated = 0;
        foreach (var pair in names)
        {
            var gstin = (pair.Key ?? string.Empty).Trim().ToUpperInvariant();
            var name = (pair.Value ?? string.Empty).Trim();
            if (gstin.Length != 15 || name.Length == 0) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE SalesRecords
                SET CustomerName=$Name, CustomerNameSource='GST Recipient Master', UpdatedUtc=$UpdatedUtc
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
                  AND UPPER(TRIM(CustomerGstin))=$Gstin
                  AND IsUserEdited=0
                  AND (UPPER(TRIM(SourceSection)) LIKE 'B2B%' OR UPPER(TRIM(SourceSection)) LIKE 'CDNR%')
                  AND (TRIM(COALESCE(CustomerNameSource,''))=''
                       OR UPPER(COALESCE(CustomerNameSource,'')) LIKE '%FALLBACK%'
                       OR TRIM(COALESCE(CustomerName,''))=''
                       OR UPPER(TRIM(CustomerName))=$Gstin
                       OR UPPER(TRIM(CustomerName)) LIKE 'CUSTOMER - %'
                       OR UPPER(TRIM(CustomerName)) LIKE 'CUSTOMER %');
                """;
            command.Parameters.AddWithValue("$Name", name);
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear);
            command.Parameters.AddWithValue("$Gstin", gstin);
            updated += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return updated;
    }

    public IReadOnlyDictionary<string, string> GetB2CCustomerNameOverrides(long clientId, string financialYear)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return result;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceKey,CustomerName FROM SalesB2CCustomerNameOverrides WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read()) { var key=reader.GetString(0).Trim(); var name=reader.GetString(1).Trim(); if(key.Length>0&&name.Length>0) result[key]=name; }
        return result;
    }

    public int SaveB2CCustomerNameOverride(long clientId, string financialYear, string sourceKey, string customerName)
    {
        var key=(sourceKey??string.Empty).Trim(); var name=(customerName??string.Empty).Trim();
        if(clientId<=0||string.IsNullOrWhiteSpace(financialYear)||key.Length==0||name.Length==0) return 0;
        using(var connection=OpenConnection()) using(var command=connection.CreateCommand())
        {
            command.CommandText="INSERT INTO SalesB2CCustomerNameOverrides(ClientId,FinancialYear,SourceKey,CustomerName,UpdatedUtc) VALUES($ClientId,$FinancialYear,$SourceKey,$CustomerName,$UpdatedUtc) ON CONFLICT(ClientId,FinancialYear,SourceKey) DO UPDATE SET CustomerName=excluded.CustomerName,UpdatedUtc=excluded.UpdatedUtc;";
            command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$SourceKey",key); command.Parameters.AddWithValue("$CustomerName",name); command.Parameters.AddWithValue("$UpdatedUtc",DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
        }
        var affected=GetRecords(clientId,financialYear).Where(r=>BuildB2CSourceKey(r).Equals(key,StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach(var record in affected)
        {
            // Fix125: freeze the Manual Sales source name before replacing CustomerName,
            // otherwise the override would change its own grouping key on the next load.
            if(string.IsNullOrWhiteSpace(record.OriginalCustomerName)) record.OriginalCustomerName=record.CustomerName;
            record.CustomerName=name;
            record.CustomerNameSource="Manual B2C Panel";
        }
        return affected.Length==0?0:UpdateRecords(clientId,affected);
    }

    public static string BuildB2CSourceKey(SalesImportRecord record)
    {
        var section=(record.SourceSection??string.Empty).Trim().ToUpperInvariant();
        var pos=(record.PlaceOfSupply??string.Empty).Trim().ToUpperInvariant();
        if(section.StartsWith("B2C",StringComparison.Ordinal))
        {
            // GSTR-1 aggregate B2C identity remains section + place of supply.
            return string.Join("|",section,pos);
        }

        // Fix125: Manual Sales has no GSTR-1 SourceSection. Its B2C identity is the
        // immutable imported party name within the source sheet + POS. This survives a
        // B2C panel rename because OriginalCustomerName is never overwritten.
        var destination=(record.Destination??string.Empty).Trim();
        var gstin=new string((record.CustomerGstin??string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if(!destination.Equals("Sales",StringComparison.OrdinalIgnoreCase) || gstin.Length==15 ||
           section.StartsWith("EXP",StringComparison.OrdinalIgnoreCase) || section.StartsWith("B2B",StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var sourceName=(string.IsNullOrWhiteSpace(record.OriginalCustomerName)?record.CustomerName:record.OriginalCustomerName).Trim().ToUpperInvariant();
        if(sourceName.Length==0) sourceName="B2C SALES";
        var branch=(record.SourceBranch??string.Empty).Trim().ToUpperInvariant();
        return string.Join("|","MANUAL",branch,pos,sourceName);
    }

    public IReadOnlyDictionary<string, string> GetB2BCustomerNameOverrides(
        long clientId,
        string financialYear)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return result;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CustomerGstin, CustomerName
            FROM SalesB2BCustomerNameOverrides
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var gstin = NormalizeGstin(reader.GetString(0));
            var name = reader.GetString(1).Trim();
            if (gstin.Length == 15 && name.Length > 0) result[gstin] = name;
        }
        return result;
    }

    public IReadOnlyList<SalesB2BCustomerNameOverride> GetB2BCustomerNameOverrideEntries(
        long clientId,
        string financialYear)
    {
        var result = new List<SalesB2BCustomerNameOverride>();
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return result;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CustomerGstin, CustomerName, UpdatedUtc
            FROM SalesB2BCustomerNameOverrides
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY UpdatedUtc, CustomerGstin;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var gstin = NormalizeGstin(reader.GetString(0));
            var name = reader.GetString(1).Trim();
            var updated = DateTime.TryParse(
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTime.MinValue;
            if (gstin.Length == 15 && name.Length > 0)
            {
                result.Add(new SalesB2BCustomerNameOverride
                {
                    Gstin = gstin,
                    CustomerName = name,
                    UpdatedUtc = updated
                });
            }
        }
        return result;
    }

    public int SaveB2BCustomerNameOverride(
        long clientId,
        string financialYear,
        string customerGstin,
        string customerName)
    {
        var gstin = NormalizeGstin(customerGstin);
        var name = (customerName ?? string.Empty).Trim();
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || gstin.Length != 15 || name.Length == 0)
            return 0;

        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO SalesB2BCustomerNameOverrides
                (ClientId, FinancialYear, CustomerGstin, CustomerName, UpdatedUtc)
                VALUES ($ClientId, $FinancialYear, $CustomerGstin, $CustomerName, $UpdatedUtc)
                ON CONFLICT(ClientId, FinancialYear, CustomerGstin) DO UPDATE SET
                    CustomerName=excluded.CustomerName,
                    UpdatedUtc=excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$CustomerGstin", gstin);
            command.Parameters.AddWithValue("$CustomerName", name);
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        var affected = GetRecords(clientId, financialYear)
            .Where(record => IsRegisteredB2BSection(record.SourceSection) &&
                             NormalizeGstin(record.CustomerGstin).Equals(gstin, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var record in affected)
        {
            record.CustomerName = name;
            record.CustomerNameSource = "Manual B2B Panel";
        }

        return affected.Length == 0 ? 0 : UpdateRecords(clientId, affected);
    }

    public int DeleteB2BCustomerNameOverride(
        long clientId,
        string financialYear,
        string customerGstin,
        string replacementName,
        string replacementSource)
    {
        var gstin = NormalizeGstin(customerGstin);
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || gstin.Length != 15)
            return 0;

        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                DELETE FROM SalesB2BCustomerNameOverrides
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND CustomerGstin=$CustomerGstin;
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$CustomerGstin", gstin);
            command.ExecuteNonQuery();
        }

        var name = (replacementName ?? string.Empty).Trim();
        var source = (replacementSource ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = $"Customer - {gstin}";
            source = "GSTIN Fallback";
        }
        else if (source.Length == 0)
        {
            source = "GST Recipient Master";
        }

        var affected = GetRecords(clientId, financialYear)
            .Where(record => IsRegisteredB2BSection(record.SourceSection) &&
                             NormalizeGstin(record.CustomerGstin).Equals(gstin, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var record in affected)
        {
            record.CustomerName = name;
            record.CustomerNameSource = source;
        }

        return affected.Length == 0 ? 0 : UpdateRecords(clientId, affected);
    }

    private static bool IsRegisteredB2BSection(string? section)
    {
        var value = (section ?? string.Empty).Trim().ToUpperInvariant();
        return value.StartsWith("B2B", StringComparison.Ordinal) ||
               value.StartsWith("CDNR", StringComparison.Ordinal);
    }

    private static string NormalizeGstin(string? value) =>
        new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    public IReadOnlyList<StockProjectionSnapshot<SalesImportRecord>> GetStockProjectionSnapshots(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,BatchId,IsUserEdited,UpdatedUtc,RecordJson FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$FY ORDER BY Id;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FY", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<StockProjectionSnapshot<SalesImportRecord>>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var batchId = reader.GetInt64(1);
            var userEdited = reader.GetInt64(2) != 0;
            var updatedUtc = reader.GetString(3);
            var original = reader.GetString(4);
            SalesImportRecord? record = null;
            try { record = JsonSerializer.Deserialize<SalesImportRecord>(original); }
            catch (JsonException) { /* Retain unreadable source, never reconstruct it. */ }
            if (record is not null)
            {
                record.PersistentId = id;
                record.BatchId = batchId;
                record.IsUserEdited = userEdited;
            }
            result.Add(new(record, original, updatedUtc, batchId, userEdited));
        }
        return result;
    }

    public StockProjectionWriteResult ApplyStockProjections(long clientId, string financialYear, IEnumerable<StockProjectionChange> changes)
    {
        using var connection = OpenConnection();
        return StockProjectionPersistence.Apply(connection, "SalesRecords", clientId, financialYear, changes);
    }

    public int UpdateStockIdentityReferences(
        long clientId,
        IEnumerable<SalesImportRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var materialized = records.Where(record => record.PersistentId > 0)
            .GroupBy(record => record.PersistentId).Select(group => group.First()).ToArray();
        if (materialized.Length == 0) return 0;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var updated = 0;
        foreach (var record in materialized)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE SalesRecords SET RecordJson=$RecordJson,UpdatedUtc=$UpdatedUtc WHERE Id=$Id AND ClientId=$ClientId;";
            command.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$Id", record.PersistentId);
            command.Parameters.AddWithValue("$ClientId", clientId);
            updated += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return updated;
    }

    public int UpdateCustomerGstinReferences(
        long clientId,
        IEnumerable<SalesImportRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var materialized = records.Where(record => record.PersistentId > 0)
            .GroupBy(record => record.PersistentId).Select(group => group.First()).ToArray();
        if (materialized.Length == 0) return 0;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var updated = 0;
        foreach (var record in materialized)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE SalesRecords SET CustomerGstin=$CustomerGstin,RecordJson=$RecordJson,UpdatedUtc=$UpdatedUtc WHERE Id=$Id AND ClientId=$ClientId;";
            command.Parameters.AddWithValue("$CustomerGstin", record.CustomerGstin);
            command.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$Id", record.PersistentId);
            command.Parameters.AddWithValue("$ClientId", clientId);
            updated += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return updated;
    }

    public int UpdateRecords(
        long clientId,
        IEnumerable<SalesImportRecord> records)
    {
        ArgumentNullException.ThrowIfNull(
            records);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var updated = 0;

        foreach (var record in records
                     .Where(record =>
                         record.PersistentId > 0))
        {
            EnforceTallySalesRegisterSummaryNonExportable(record);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE SalesRecords
                SET
                    CustomerGstin = $CustomerGstin,
                    CustomerName = $CustomerName,
                    CustomerNameSource = $CustomerNameSource,
                    DocumentType = $DocumentType,
                    DocumentNumber = $DocumentNumber,
                    DocumentDate = $DocumentDate,
                    GstDate = $GstDate,
                    DocumentValue = $DocumentValue,
                    Rate = $Rate,
                    TaxableValue = $TaxableValue,
                    Igst = $Igst,
                    Cgst = $Cgst,
                    Sgst = $Sgst,
                    Cess = $Cess,
                    RoundOff = $RoundOff,
                    SalesLedger = $SalesLedger,
                    IgstLedger = $IgstLedger,
                    CgstLedger = $CgstLedger,
                    SgstLedger = $SgstLedger,
                    SourceSection = $SourceSection,
                    IsAmendment = $IsAmendment,
                    OriginalDocumentNumber = $OriginalDocumentNumber,
                    OriginalDocumentDate = $OriginalDocumentDate,
                    PlaceOfSupply = $PlaceOfSupply,
                    ReturnPeriod = $ReturnPeriod,
                    UpdatedUtc = $UpdatedUtc,
                    IsUserEdited = 1,
                    RecordJson = $RecordJson
                WHERE
                    Id = $Id
                    AND ClientId = $ClientId;
                """;

            command.Parameters.AddWithValue(
                "$CustomerGstin",
                record.CustomerGstin);
            command.Parameters.AddWithValue(
                "$CustomerName",
                record.CustomerName);
            command.Parameters.AddWithValue(
                "$CustomerNameSource",
                record.CustomerNameSource);
            command.Parameters.AddWithValue(
                "$DocumentType",
                record.DocumentType);
            command.Parameters.AddWithValue(
                "$DocumentNumber",
                record.DocumentNumber);
            command.Parameters.AddWithValue(
                "$DocumentDate",
                FormatDate(record.DocumentDate));
            command.Parameters.AddWithValue(
                "$GstDate",
                FormatDate(record.GstDate));
            command.Parameters.AddWithValue(
                "$DocumentValue",
                record.DocumentValue);
            command.Parameters.AddWithValue(
                "$Rate",
                record.Rate);
            command.Parameters.AddWithValue(
                "$TaxableValue",
                record.TaxableValue);
            command.Parameters.AddWithValue(
                "$Igst",
                record.Igst);
            command.Parameters.AddWithValue(
                "$Cgst",
                record.Cgst);
            command.Parameters.AddWithValue(
                "$Sgst",
                record.Sgst);
            command.Parameters.AddWithValue(
                "$Cess",
                record.Cess);
            command.Parameters.AddWithValue(
                "$RoundOff",
                record.RoundOff);
            command.Parameters.AddWithValue(
                "$SalesLedger",
                record.SalesLedger);
            command.Parameters.AddWithValue(
                "$IgstLedger",
                record.IgstLedger);
            command.Parameters.AddWithValue(
                "$CgstLedger",
                record.CgstLedger);
            command.Parameters.AddWithValue(
                "$SgstLedger",
                record.SgstLedger);
            command.Parameters.AddWithValue(
                "$SourceSection",
                record.SourceSection);
            command.Parameters.AddWithValue(
                "$IsAmendment",
                record.IsAmendment ? 1 : 0);
            command.Parameters.AddWithValue(
                "$OriginalDocumentNumber",
                record.OriginalDocumentNumber);
            command.Parameters.AddWithValue(
                "$OriginalDocumentDate",
                FormatDate(record.OriginalDocumentDate));
            command.Parameters.AddWithValue(
                "$PlaceOfSupply",
                record.PlaceOfSupply);
            command.Parameters.AddWithValue(
                "$ReturnPeriod",
                record.ReturnPeriod);
            command.Parameters.AddWithValue(
                "$UpdatedUtc",
                DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue(
                "$RecordJson",
                JsonSerializer.Serialize(record));
            command.Parameters.AddWithValue(
                "$Id",
                record.PersistentId);
            command.Parameters.AddWithValue(
                "$ClientId",
                clientId);

            updated += command.ExecuteNonQuery();
            record.IsUserEdited = true;
        }

        transaction.Commit();

        return updated;
    }

    private static void EnforceTallySalesRegisterSummaryNonExportable(SalesImportRecord record)
    {
        if (!record.SourceOrigin.Equals("Tally Sales Register Summary", StringComparison.OrdinalIgnoreCase)) return;

        // R9 FIXED16 R16: the supplied Crystal/Tally statement rows are aggregate
        // daily evidence, not voucher-level accounting documents. Keep the amounts
        // visible in Sales, but make persistence incapable of turning the aggregate
        // range into a real Tally party/ledger/inventory voucher, even after edits.
        record.CustomerGstin = string.Empty;
        record.CustomerName = string.Empty;
        record.CustomerNameSource = string.Empty;
        record.SalesLedger = string.Empty;
        record.IgstLedger = string.Empty;
        record.CgstLedger = string.Empty;
        record.SgstLedger = string.Empty;
        record.ItemName = string.Empty;
        record.ItemSalesLedger = string.Empty;
        record.StockItemPersistentId = 0;
        record.TallyStockMasterId = string.Empty;
        record.ActualQuantity = 0m;
        record.BilledQuantity = 0m;
        record.ItemRate = 0m;
        record.ItemAmount = 0m;
        record.AccountingAmount = 0m;
    }

    private static void AddParameters(
        SqliteCommand command,
        long batchId,
        long clientId,
        string financialYear,
        SalesImportRecord record)
    {
        EnforceTallySalesRegisterSummaryNonExportable(record);
        command.Parameters.AddWithValue(
            "$BatchId",
            batchId);
        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);
        command.Parameters.AddWithValue(
            "$ClientGstin",
            record.ClientGstin);
        command.Parameters.AddWithValue(
            "$CompanyName",
            record.CompanyName);
        command.Parameters.AddWithValue(
            "$FinancialYear",
            financialYear.Trim());
        command.Parameters.AddWithValue(
            "$Destination",
            record.Destination);
        command.Parameters.AddWithValue(
            "$CustomerGstin",
            record.CustomerGstin);
        command.Parameters.AddWithValue(
            "$CustomerName",
            record.CustomerName);
        command.Parameters.AddWithValue(
            "$CustomerNameSource",
            record.CustomerNameSource);
        command.Parameters.AddWithValue(
            "$DocumentType",
            record.DocumentType);
        command.Parameters.AddWithValue(
            "$DocumentNumber",
            record.DocumentNumber);
        command.Parameters.AddWithValue(
            "$DocumentDate",
            FormatDate(record.DocumentDate));
        command.Parameters.AddWithValue(
            "$GstDate",
            FormatDate(record.GstDate));
        command.Parameters.AddWithValue(
            "$SourceDocumentValue",
            record.SourceDocumentValue);
        command.Parameters.AddWithValue(
            "$DocumentValue",
            record.DocumentValue);
        command.Parameters.AddWithValue(
            "$Rate",
            record.Rate);
        command.Parameters.AddWithValue(
            "$TaxableValue",
            record.TaxableValue);
        command.Parameters.AddWithValue(
            "$Igst",
            record.Igst);
        command.Parameters.AddWithValue(
            "$Cgst",
            record.Cgst);
        command.Parameters.AddWithValue(
            "$Sgst",
            record.Sgst);
        command.Parameters.AddWithValue(
            "$Cess",
            record.Cess);
        command.Parameters.AddWithValue(
            "$RoundOff",
            record.RoundOff);
        command.Parameters.AddWithValue(
            "$SalesLedger",
            record.SalesLedger);
        command.Parameters.AddWithValue(
            "$IgstLedger",
            record.IgstLedger);
        command.Parameters.AddWithValue(
            "$CgstLedger",
            record.CgstLedger);
        command.Parameters.AddWithValue(
            "$SgstLedger",
            record.SgstLedger);
        command.Parameters.AddWithValue(
            "$SourceSection",
            record.SourceSection);
        command.Parameters.AddWithValue(
            "$IsAmendment",
            record.IsAmendment ? 1 : 0);
        command.Parameters.AddWithValue(
            "$OriginalDocumentNumber",
            record.OriginalDocumentNumber);
        command.Parameters.AddWithValue(
            "$OriginalDocumentDate",
            FormatDate(record.OriginalDocumentDate));
        command.Parameters.AddWithValue(
            "$PlaceOfSupply",
            record.PlaceOfSupply);
        command.Parameters.AddWithValue(
            "$ReturnPeriod",
            record.ReturnPeriod);
        command.Parameters.AddWithValue(
            "$SourceFile",
            record.SourceFile);
        command.Parameters.AddWithValue(
            "$SourcePath",
            record.SourcePath);
        command.Parameters.AddWithValue(
            "$SourceOrigin",
            record.SourceOrigin);
        command.Parameters.AddWithValue(
            "$SourceBranch",
            record.SourceBranch);
        command.Parameters.AddWithValue(
            "$SourceScope",
            record.SourceScope);
        command.Parameters.AddWithValue(
            "$SourceHash",
            record.SourceHash);
        command.Parameters.AddWithValue(
            "$ResponseTimestamp",
            record.ResponseTimestamp);
        command.Parameters.AddWithValue(
            "$SalesMasterFile",
            record.SalesMasterFile);
        command.Parameters.AddWithValue(
            "$ItemSequence",
            record.ItemSequence);
        command.Parameters.AddWithValue(
            "$CreatedUtc",
            DateTime.UtcNow.ToString("O"));
    }

    private static SalesImportRecord ReadRecord(
        SqliteDataReader reader)
    {
        var json = ReadString(
            reader,
            "RecordJson");

        SalesImportRecord? record = null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                record =
                    JsonSerializer.Deserialize<SalesImportRecord>(
                        json);
            }
            catch
            {
                record = null;
            }
        }

        record ??=
            new SalesImportRecord
            {
                ClientGstin =
                    ReadString(reader, "ClientGstin"),
                CompanyName =
                    ReadString(reader, "CompanyName"),
                ImportMode =
                    ReadString(reader, "ImportMode"),
                Destination =
                    ReadString(reader, "Destination"),
                CustomerGstin =
                    ReadString(reader, "CustomerGstin"),
                CustomerName =
                    ReadString(reader, "CustomerName"),
                CustomerNameSource =
                    ReadString(reader, "CustomerNameSource"),
                DocumentType =
                    ReadString(reader, "DocumentType"),
                DocumentNumber =
                    ReadString(reader, "DocumentNumber"),
                DocumentDate =
                    ReadDate(reader, "DocumentDate"),
                GstDate =
                    ReadDate(reader, "GstDate"),
                SourceDocumentValue =
                    ReadDecimal(reader, "SourceDocumentValue"),
                DocumentValue =
                    ReadDecimal(reader, "DocumentValue"),
                Rate =
                    ReadDecimal(reader, "Rate"),
                TaxableValue =
                    ReadDecimal(reader, "TaxableValue"),
                Igst =
                    ReadDecimal(reader, "Igst"),
                Cgst =
                    ReadDecimal(reader, "Cgst"),
                Sgst =
                    ReadDecimal(reader, "Sgst"),
                Cess =
                    ReadDecimal(reader, "Cess"),
                RoundOff =
                    ReadDecimal(reader, "RoundOff"),
                SalesLedger =
                    ReadString(reader, "SalesLedger"),
                IgstLedger =
                    ReadString(reader, "IgstLedger"),
                CgstLedger =
                    ReadString(reader, "CgstLedger"),
                SgstLedger =
                    ReadString(reader, "SgstLedger"),
                SourceSection =
                    ReadString(reader, "SourceSection"),
                IsAmendment =
                    ReadInt64(reader, "IsAmendment") != 0,
                OriginalDocumentNumber =
                    ReadString(reader, "OriginalDocumentNumber"),
                OriginalDocumentDate =
                    ReadDate(reader, "OriginalDocumentDate"),
                PlaceOfSupply =
                    ReadString(reader, "PlaceOfSupply"),
                ReturnPeriod =
                    ReadString(reader, "ReturnPeriod"),
                SourceFile =
                    ReadString(reader, "SourceFile"),
                SourcePath =
                    ReadString(reader, "SourcePath"),
                SourceOrigin =
                    ReadString(reader, "SourceOrigin"),
                SourceBranch =
                    ReadString(reader, "SourceBranch"),
                SourceScope =
                    ReadString(reader, "SourceScope"),
                SourceHash =
                    ReadString(reader, "SourceHash"),
                ResponseTimestamp =
                    ReadString(reader, "ResponseTimestamp"),
                SalesMasterFile =
                    ReadString(reader, "SalesMasterFile"),
                ItemSequence =
                    (int)ReadInt64(reader, "ItemSequence")
            };

        // These identity fields are authoritative SQL columns. Customer Master backfill
        // updates them directly; do not let an older serialized RecordJson fallback mask
        // the newly resolved B2B customer name on the next read.
        record.CustomerGstin = ReadString(reader, "CustomerGstin");
        record.CustomerName = ReadString(reader, "CustomerName");
        record.CustomerNameSource = ReadString(reader, "CustomerNameSource");

        record.PersistentId =
            ReadInt64(reader, "Id");

        record.BatchId =
            ReadInt64(reader, "BatchId");

        record.IsUserEdited =
            ReadInt64(reader, "IsUserEdited") != 0;

        return record;
    }

    private static string FormatDate(
        DateTime? value) =>
        value?.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture)
        ?? string.Empty;

    private static string ReadString(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(
            columnName);

        return reader.IsDBNull(
            ordinal)
            ? string.Empty
            : reader.GetString(
                ordinal);
    }

    private static long ReadInt64(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(
            columnName);

        return reader.IsDBNull(
            ordinal)
            ? 0L
            : reader.GetInt64(
                ordinal);
    }

    private static decimal ReadDecimal(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(
            columnName);

        return reader.IsDBNull(
            ordinal)
            ? 0m
            : Convert.ToDecimal(
                reader.GetValue(ordinal),
                CultureInfo.InvariantCulture);
    }

    private static DateTime? ReadDate(
        SqliteDataReader reader,
        string columnName)
    {
        var text = ReadString(
            reader,
            columnName);

        return DateTime.TryParseExact(
            text,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? value.Date
            : null;
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }
}
