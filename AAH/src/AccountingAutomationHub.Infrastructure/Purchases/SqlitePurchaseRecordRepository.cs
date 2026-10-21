using System.Globalization;
using System.Text.Json;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Imports;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Purchases;

public sealed class SqlitePurchaseRecordRepository :
    IPurchaseRecordRepository
{
    private readonly string _connectionString;

    public SqlitePurchaseRecordRepository()
    {
        ApplicationDataPaths.EnsureFolders();

        _connectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource = ApplicationDataPaths.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqlitePurchaseRecordRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS PurchaseImportBatches
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                ImportMode          TEXT NOT NULL,
                Status              TEXT NOT NULL,
                EReturnsRoot        TEXT NOT NULL DEFAULT '',
                VerificationJson    TEXT NOT NULL DEFAULT '',
                SourceManifestJson  TEXT NOT NULL DEFAULT '',
                SourceFingerprint   TEXT NOT NULL DEFAULT '',
                PurchaseRows        INTEGER NOT NULL DEFAULT 0,
                RcmRows             INTEGER NOT NULL DEFAULT 0,
                DebitNoteRows       INTEGER NOT NULL DEFAULT 0,
                DebitNoteReverseRows INTEGER NOT NULL DEFAULT 0,
                CreatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId)
                    REFERENCES Clients(Id)
                    ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS PurchaseRecords
            (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                BatchId                 INTEGER NOT NULL,
                ClientId                INTEGER NOT NULL,
                ClientGstin             TEXT NOT NULL DEFAULT '',
                CompanyName             TEXT NOT NULL DEFAULT '',
                FinancialYear           TEXT NOT NULL DEFAULT '',
                ImportMode              TEXT NOT NULL DEFAULT '',
                Destination             TEXT NOT NULL DEFAULT '',
                ReconciliationStatus    TEXT NOT NULL DEFAULT '',
                GstApplicability        TEXT NOT NULL DEFAULT '',
                SupplierGstin           TEXT NOT NULL DEFAULT '',
                SupplierName            TEXT NOT NULL DEFAULT '',
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
                PurchaseLedger          TEXT NOT NULL DEFAULT '',
                IgstLedger              TEXT NOT NULL DEFAULT '',
                CgstLedger              TEXT NOT NULL DEFAULT '',
                SgstLedger              TEXT NOT NULL DEFAULT '',
                SourceSection           TEXT NOT NULL DEFAULT '',
                IsAmendment             INTEGER NOT NULL DEFAULT 0,
                OriginalDocumentNumber  TEXT NOT NULL DEFAULT '',
                OriginalDocumentDate    TEXT NOT NULL DEFAULT '',
                PlaceOfSupply           TEXT NOT NULL DEFAULT '',
                ReverseCharge           TEXT NOT NULL DEFAULT '',
                InvoiceType             TEXT NOT NULL DEFAULT '',
                ItcAvailability         TEXT NOT NULL DEFAULT '',
                ImsStatus               TEXT NOT NULL DEFAULT '',
                TwoBReason              TEXT NOT NULL DEFAULT '',
                TwoAReturnPeriod        TEXT NOT NULL DEFAULT '',
                TwoBReturnPeriod        TEXT NOT NULL DEFAULT '',
                TwoBAcceptance          TEXT NOT NULL DEFAULT '',
                Source2AFile            TEXT NOT NULL DEFAULT '',
                Source2APath            TEXT NOT NULL DEFAULT '',
                Source2BFile            TEXT NOT NULL DEFAULT '',
                Source2BPath            TEXT NOT NULL DEFAULT '',
                TwoBInvoiceValue        REAL,
                TwoBTaxableValue        REAL,
                TwoBIgst                REAL,
                TwoBCgst                REAL,
                TwoBSgst                REAL,
                DifferenceInvoiceValue  REAL,
                DifferenceTaxableValue  REAL,
                DifferenceIgst          REAL,
                DifferenceCgst          REAL,
                DifferenceSgst          REAL,
                DifferenceDetails       TEXT NOT NULL DEFAULT '',
                ItemSequence            INTEGER NOT NULL DEFAULT 0,
                CreatedUtc              TEXT NOT NULL,
                UpdatedUtc              TEXT NOT NULL DEFAULT '',
                IsUserEdited            INTEGER NOT NULL DEFAULT 0,
                RecordJson              TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (BatchId)
                    REFERENCES PurchaseImportBatches(Id)
                    ON DELETE CASCADE,
                FOREIGN KEY (ClientId)
                    REFERENCES Clients(Id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS
                IX_PurchaseRecords_ClientFyMode
            ON PurchaseRecords(ClientId, FinancialYear, ImportMode);

            CREATE INDEX IF NOT EXISTS
                IX_PurchaseRecords_Document
            ON PurchaseRecords
            (
                ClientId,
                SupplierGstin,
                DocumentNumber,
                DocumentDate
            );
            """;

        command.ExecuteNonQuery();

        EnsureColumn(
            connection,
            "PurchaseImportBatches",
            "SourceFingerprint",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(
            connection,
            "PurchaseImportBatches",
            "DebitNoteReverseRows",
            "INTEGER NOT NULL DEFAULT 0");

        EnsureColumn(
            connection,
            "PurchaseRecords",
            "UpdatedUtc",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(
            connection,
            "PurchaseRecords",
            "IsUserEdited",
            "INTEGER NOT NULL DEFAULT 0");

        EnsureColumn(
            connection,
            "PurchaseRecords",
            "RecordJson",
            "TEXT NOT NULL DEFAULT ''");
    }

    public bool IsAutomaticImportCurrent(
        long clientId,
        string financialYear,
        string sourceFingerprint)
    {
        if (string.IsNullOrWhiteSpace(sourceFingerprint))
        {
            return false;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT SourceFingerprint
            FROM PurchaseImportBatches
            WHERE
                ClientId = $ClientId
                AND FinancialYear = $FinancialYear
                AND ImportMode = 'Auto Import'
                AND Status = 'Imported'
            ORDER BY Id DESC
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());

        var stored = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;

        return string.Equals(
            stored,
            sourceFingerprint,
            StringComparison.OrdinalIgnoreCase);
    }

    public int ClearModule(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
        count.Parameters.AddWithValue("$ClientId", clientId);
        count.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        var rows = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM PurchaseImportBatches WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
        delete.Parameters.AddWithValue("$ClientId", clientId);
        delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        delete.ExecuteNonQuery();
        transaction.Commit();
        return rows;
    }

    public string GetLatestAutomaticImportContentFingerprint(
        long clientId,
        string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SourceManifestJson
            FROM PurchaseImportBatches
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND ImportMode = 'Auto Import'
              AND Status = 'Imported'
            ORDER BY Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());

        var json = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        if (json.Length == 0) return string.Empty;
        try
        {
            var manifest = JsonSerializer.Deserialize<List<PurchaseSourceManifestItem>>(json) ?? [];
            return SalesPurchaseAccountingContentFingerprint.BuildPurchase(manifest);
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
                FROM PurchaseRecords
                WHERE
                    ClientId = $ClientId
                    AND FinancialYear = $FinancialYear
                LIMIT 1
            );
            """;

        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());

        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public IReadOnlyList<PurchaseImportRecord> GetAutomaticRecords(
        long clientId,
        string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT *
            FROM PurchaseRecords
            WHERE
                ClientId = $ClientId
                AND FinancialYear = $FinancialYear
                AND ImportMode = 'Auto Import'
            ORDER BY
                Destination COLLATE NOCASE,
                DocumentDate,
                SupplierGstin COLLATE NOCASE,
                DocumentNumber COLLATE NOCASE,
                ItemSequence,
                Id;
            """;

        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());

        using var reader = command.ExecuteReader();
        var records = new List<PurchaseImportRecord>();
        var repaired = new List<PurchaseImportRecord>();

        while (reader.Read())
        {
            // Capture persisted values before the Completion 52 read-normalizer so
            // legacy 2B evidence repairs are written back, not only fixed in memory.
            var beforeDestination = ReadString(reader, "Destination");
            var beforeTreatment = ReadString(reader, "GstApplicability");
            var beforePurchaseLedger = ReadString(reader, "PurchaseLedger");
            var beforeIgstLedger = ReadString(reader, "IgstLedger");
            var beforeCgstLedger = ReadString(reader, "CgstLedger");
            var beforeSgstLedger = ReadString(reader, "SgstLedger");
            var storedJson = ReadString(reader, "RecordJson");
            var record = ReadPurchaseRecord(reader);
            var rateKnownNeedsPersist = !record.RateKnown &&
                                        !storedJson.Contains("\"RateKnown\":false", StringComparison.OrdinalIgnoreCase);
            var rateBeforeRepair = record.Rate;
            var rateKnownBeforeRepair = record.RateKnown;

            // Completion 55 deliberately does not recalculate GST Date from the saved
            // TwoBReturnPeriod during an ordinary database read. Older builds stored the
            // supplier tax period there and Completion 54 treated that period as the GST
            // month, which is exactly the stale-date defect this release repairs. The
            // authoritative GST Date is rebuilt from fresh GSTR-2B supplier filing-date
            // evidence during Reimport Purchase and then persisted normally.

            // Fix46: self-heal every saved Purchase/2A/2B accounting row, not only
            // retained 2B-only evidence. A literal 0% with non-zero GST is a source
            // placeholder/legacy defect, never a genuine zero-rated line. The strict
            // reverse calculation changes the rate only when taxable + GST amounts prove
            // one supported standard rate. This also repairs existing databases without
            // requiring the user to reimport the source files.
            RepairRateFromTaxEvidence(record);
            var rateInferenceNeedsPersist =
                rateKnownBeforeRepair != record.RateKnown || rateBeforeRepair != record.Rate;

            // Self-heal stale UI-era treatment values on every read. Source 2B
            // reconciliation remains authoritative for Shown/Not shown; a deliberate
            // GST-included-in-cost treatment is retained. This also guarantees Journal
            // and GSTR-3B calculations never consume a contradictory persisted row.
            PurchaseTreatmentPolicy.ApplyAccountingShape(record);

            if (!string.Equals(beforeDestination, record.Destination, StringComparison.Ordinal) ||
                !string.Equals(beforeTreatment, record.GstApplicability, StringComparison.Ordinal) ||
                rateKnownNeedsPersist ||
                rateInferenceNeedsPersist ||
                !string.Equals(beforePurchaseLedger, record.PurchaseLedger, StringComparison.Ordinal) ||
                !string.Equals(beforeIgstLedger, record.IgstLedger, StringComparison.Ordinal) ||
                !string.Equals(beforeCgstLedger, record.CgstLedger, StringComparison.Ordinal) ||
                !string.Equals(beforeSgstLedger, record.SgstLedger, StringComparison.Ordinal))
            {
                repaired.Add(record);
            }

            records.Add(record);
        }

        reader.Close();
        PersistAccountingShapeRepairs(clientId, repaired);
        return records;
    }

    // Fix91: Audit is a read-only projection over the complete shared Purchase authority,
    // including manual and automatic imports. Keep this query free of any second import path.
    public IReadOnlyList<PurchaseImportRecord> GetRecords(
        long clientId,
        string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT *
            FROM PurchaseRecords
            WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear
            ORDER BY
                Destination COLLATE NOCASE,
                COALESCE(NULLIF(GstDate,''), DocumentDate),
                DocumentDate,
                SupplierGstin COLLATE NOCASE,
                DocumentNumber COLLATE NOCASE,
                ItemSequence,
                Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var records = new List<PurchaseImportRecord>();
        while (reader.Read())
            records.Add(ReadPurchaseRecord(reader));
        return records;
    }


    public int SaveManualImport(
        long clientId,
        string financialYear,
        PurchaseManualImportResult result,
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
                "DELETE FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND ImportMode='Manual Import';";
            delete.Parameters.AddWithValue("$ClientId", clientId);
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            delete.ExecuteNonQuery();
        }
        else if (!string.IsNullOrWhiteSpace(replaceSourcePath))
        {
            // Fix116 R1 Remap is a source reimport, not a profile-only edit. Remove only
            // the previous Manual Import batches created from this exact source file so
            // unrelated Manual Purchase files for the Client/FY remain untouched.
            var sourceBatchIds = FindManualBatchIdsForSource(
                connection, transaction, clientId, financialYear, replaceSourcePath);
            foreach (var batchIdToReplace in sourceBatchIds)
            {
                using var deleteRows = connection.CreateCommand();
                deleteRows.Transaction = transaction;
                deleteRows.CommandText =
                    "DELETE FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND BatchId=$BatchId;";
                deleteRows.Parameters.AddWithValue("$ClientId", clientId);
                deleteRows.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
                deleteRows.Parameters.AddWithValue("$BatchId", batchIdToReplace);
                deleteRows.ExecuteNonQuery();

                using var deleteBatch = connection.CreateCommand();
                deleteBatch.Transaction = transaction;
                deleteBatch.CommandText =
                    "DELETE FROM PurchaseImportBatches WHERE Id=$BatchId AND ClientId=$ClientId AND FinancialYear=$FinancialYear AND ImportMode='Manual Import';";
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
                "SELECT * FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
            existing.Parameters.AddWithValue("$ClientId", clientId);
            existing.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            using var reader = existing.ExecuteReader();
            while (reader.Read())
                existingKeys.Add(ManualDuplicateKey(ReadPurchaseRecord(reader)));
        }

        long batchId;
        using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO PurchaseImportBatches
                (ClientId,FinancialYear,ImportMode,Status,EReturnsRoot,VerificationJson,SourceManifestJson,SourceFingerprint,PurchaseRows,RcmRows,DebitNoteRows,DebitNoteReverseRows,CreatedUtc)
                VALUES($ClientId,$FinancialYear,'Manual Import','Imported','', '', $SourceManifestJson,$Fingerprint,$PurchaseRows,$RcmRows,$DebitNoteRows,0,$CreatedUtc);
                SELECT last_insert_rowid();
                """;
            insertBatch.Parameters.AddWithValue("$ClientId", clientId);
            insertBatch.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            insertBatch.Parameters.AddWithValue("$SourceManifestJson", JsonSerializer.Serialize(new
            {
                Mode = "Manual Purchase Register",
                SourcePath = result.SourcePath,
                HeaderSignature = result.HeaderSignature
            }));
            insertBatch.Parameters.AddWithValue("$Fingerprint", result.HeaderSignature ?? string.Empty);
            insertBatch.Parameters.AddWithValue("$PurchaseRows", result.PurchaseRows);
            insertBatch.Parameters.AddWithValue("$RcmRows", result.RcmRows);
            insertBatch.Parameters.AddWithValue("$DebitNoteRows", result.DebitNoteRows);
            insertBatch.Parameters.AddWithValue("$CreatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            batchId = Convert.ToInt64(insertBatch.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var inserted = 0;
        foreach (var record in result.Records)
        {
            RepairRateFromTaxEvidence(record);
            PurchaseTreatmentPolicy.ApplyAccountingShape(record);
            var key = ManualDuplicateKey(record);
            if (!existingKeys.Add(key)) continue;

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO PurchaseRecords
                (
                    BatchId,ClientId,ClientGstin,CompanyName,FinancialYear,ImportMode,Destination,ReconciliationStatus,GstApplicability,
                    SupplierGstin,SupplierName,DocumentType,DocumentNumber,DocumentDate,GstDate,SourceDocumentValue,DocumentValue,Rate,
                    TaxableValue,Igst,Cgst,Sgst,Cess,RoundOff,PurchaseLedger,IgstLedger,CgstLedger,SgstLedger,SourceSection,IsAmendment,
                    OriginalDocumentNumber,OriginalDocumentDate,PlaceOfSupply,ReverseCharge,InvoiceType,ItcAvailability,ImsStatus,TwoBReason,
                    TwoAReturnPeriod,TwoBReturnPeriod,TwoBAcceptance,Source2AFile,Source2APath,Source2BFile,Source2BPath,TwoBInvoiceValue,
                    TwoBTaxableValue,TwoBIgst,TwoBCgst,TwoBSgst,DifferenceInvoiceValue,DifferenceTaxableValue,DifferenceIgst,DifferenceCgst,
                    DifferenceSgst,DifferenceDetails,ItemSequence,CreatedUtc,UpdatedUtc,IsUserEdited,RecordJson
                )
                VALUES
                (
                    $BatchId,$ClientId,$ClientGstin,$CompanyName,$FinancialYear,'Manual Import',$Destination,$ReconciliationStatus,$GstApplicability,
                    $SupplierGstin,$SupplierName,$DocumentType,$DocumentNumber,$DocumentDate,$GstDate,$SourceDocumentValue,$DocumentValue,$Rate,
                    $TaxableValue,$Igst,$Cgst,$Sgst,$Cess,$RoundOff,$PurchaseLedger,$IgstLedger,$CgstLedger,$SgstLedger,$SourceSection,$IsAmendment,
                    $OriginalDocumentNumber,$OriginalDocumentDate,$PlaceOfSupply,$ReverseCharge,$InvoiceType,$ItcAvailability,$ImsStatus,$TwoBReason,
                    $TwoAReturnPeriod,$TwoBReturnPeriod,$TwoBAcceptance,$Source2AFile,$Source2APath,$Source2BFile,$Source2BPath,$TwoBInvoiceValue,
                    $TwoBTaxableValue,$TwoBIgst,$TwoBCgst,$TwoBSgst,$DifferenceInvoiceValue,$DifferenceTaxableValue,$DifferenceIgst,$DifferenceCgst,
                    $DifferenceSgst,$DifferenceDetails,$ItemSequence,$CreatedUtc,'',0,$RecordJson
                );
                SELECT last_insert_rowid();
                """;
            AddParameters(insert, batchId, clientId, financialYear, record);
            insert.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
            var recordId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            record.PersistentId = recordId;
            record.BatchId = batchId;
            record.ImportMode = "Manual Import";
            record.IsUserEdited = false;
            inserted++;
        }

        transaction.Commit();
        return inserted;
    }

    private static IReadOnlyList<long> FindManualBatchIdsForSource(
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
            "SELECT Id,SourceManifestJson FROM PurchaseImportBatches WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND ImportMode='Manual Import';";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var manifest = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (!TryReadManualSourcePath(manifest, out var savedSource)) continue;
            if (SameManualSource(savedSource, sourcePath)) matches.Add(reader.GetInt64(0));
        }
        return matches;
    }

    private static bool TryReadManualSourcePath(string manifestJson, out string sourcePath)
    {
        sourcePath = string.Empty;
        if (string.IsNullOrWhiteSpace(manifestJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (!document.RootElement.TryGetProperty("SourcePath", out var sourceElement)) return false;
            sourcePath = sourceElement.GetString() ?? string.Empty;
            return sourcePath.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameManualSource(string left, string right)
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

    private static HashSet<string> LoadPurchaseDuplicateKeys(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT * FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear;";
        existing.Parameters.AddWithValue("$ClientId", clientId);
        existing.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        using var reader = existing.ExecuteReader();
        while (reader.Read()) keys.Add(ManualDuplicateKey(ReadPurchaseRecord(reader)));
        return keys;
    }

    private static string ManualDuplicateKey(PurchaseImportRecord row) =>
        string.Join("|",
            row.Destination.Trim().ToUpperInvariant(),
            row.DocumentDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            PurchaseImportUtilities.NormalizeDocumentNumber(row.DocumentNumber),
            row.SupplierGstin.Trim().ToUpperInvariant(),
            row.SupplierName.Trim().ToUpperInvariant(),
            row.ItemName.Trim().ToUpperInvariant(),
            row.HsnCode.Trim().ToUpperInvariant(),
            row.Unit.Trim().ToUpperInvariant(),
            row.ActualQuantity.ToString("0.######", CultureInfo.InvariantCulture),
            row.BilledQuantity.ToString("0.######", CultureInfo.InvariantCulture),
            row.ItemRate.ToString("0.######", CultureInfo.InvariantCulture),
            row.ItemAmount.ToString("0.00", CultureInfo.InvariantCulture),
            row.Rate.ToString("0.####", CultureInfo.InvariantCulture),
            row.TaxableValue.ToString("0.00", CultureInfo.InvariantCulture),
            row.Igst.ToString("0.00", CultureInfo.InvariantCulture),
            row.Cgst.ToString("0.00", CultureInfo.InvariantCulture),
            row.Sgst.ToString("0.00", CultureInfo.InvariantCulture),
            row.Cess.ToString("0.00", CultureInfo.InvariantCulture));


    public int ReplaceAutomaticImport(
        long clientId,
        string financialYear,
        PurchaseAutoImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.WasBlocked)
        {
            return 0;
        }

        // Manual Purchase/RCM edits are authoritative over a repeated automatic
        // source refresh.  The old implementation deleted every Auto Import row
        // first, which meant a user-selected RCM treatment (for example
        // "Claimed in Next Month") silently returned to the source default on
        // the next E-returns scan.  Capture edited rows before replacement and
        // overlay only the user-editable fields onto their matching fresh rows.
        var preservedUserEdits = GetAutomaticRecords(
                clientId,
                financialYear)
            .Where(record => record.IsUserEdited)
            .ToArray();

        var incomingRecords = result.Records.ToList();
        AppendManualTwoBRateLines(incomingRecords, preservedUserEdits);
        PreserveUserEdits(
            incomingRecords,
            preservedUserEdits);

        // GST Treatment is a required accounting classification. A legacy blank
        // user edit must never survive a fresh source rebuild, and no fresh row may
        // be persisted before its source/RCM treatment has been resolved.
        foreach (var record in incomingRecords)
        {
            RepairRateFromTaxEvidence(record);
            PurchaseTreatmentPolicy.ApplyAccountingShape(record);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM PurchaseRecords
                WHERE
                    ClientId = $ClientId
                    AND FinancialYear = $FinancialYear
                    AND ImportMode = 'Auto Import';
                """;

            delete.Parameters.AddWithValue(
                "$ClientId",
                clientId);

            delete.Parameters.AddWithValue(
                "$FinancialYear",
                financialYear.Trim());

            delete.ExecuteNonQuery();
        }

        // FIXED29: Auto Import uses the exact Manual Import transaction duplicate
        // identity after source discovery. Retained rows seed the gate and repeated
        // records inside the automatic source set are suppressed in the same pass.
        var autoDuplicateKeys = LoadPurchaseDuplicateKeys(connection, transaction, clientId, financialYear);
        incomingRecords = incomingRecords
            .Where(record => autoDuplicateKeys.Add(ManualDuplicateKey(record)))
            .ToList();

        long batchId;

        using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO PurchaseImportBatches
                (
                    ClientId,
                    FinancialYear,
                    ImportMode,
                    Status,
                    EReturnsRoot,
                    VerificationJson,
                    SourceManifestJson,
                    SourceFingerprint,
                    PurchaseRows,
                    RcmRows,
                    DebitNoteRows,
                    DebitNoteReverseRows,
                    CreatedUtc
                )
                VALUES
                (
                    $ClientId,
                    $FinancialYear,
                    'Auto Import',
                    'Imported',
                    $EReturnsRoot,
                    $VerificationJson,
                    $SourceManifestJson,
                    $SourceFingerprint,
                    $PurchaseRows,
                    $RcmRows,
                    $DebitNoteRows,
                    $DebitNoteReverseRows,
                    $CreatedUtc
                );

                SELECT last_insert_rowid();
                """;

            insertBatch.Parameters.AddWithValue(
                "$ClientId",
                clientId);
            insertBatch.Parameters.AddWithValue(
                "$FinancialYear",
                financialYear.Trim());
            insertBatch.Parameters.AddWithValue(
                "$EReturnsRoot",
                result.Verification.EReturnsRoot);
            insertBatch.Parameters.AddWithValue(
                "$VerificationJson",
                JsonSerializer.Serialize(result.Verification));
            insertBatch.Parameters.AddWithValue(
                "$SourceManifestJson",
                JsonSerializer.Serialize(result.SourceManifest));
            insertBatch.Parameters.AddWithValue(
                "$SourceFingerprint",
                result.SourceFingerprint);
            insertBatch.Parameters.AddWithValue(
                "$PurchaseRows",
                incomingRecords.Count(record => record.Destination == "Purchase"));
            insertBatch.Parameters.AddWithValue(
                "$RcmRows",
                incomingRecords.Count(record => record.Destination == "RCM"));
            insertBatch.Parameters.AddWithValue(
                "$DebitNoteRows",
                incomingRecords.Count(record => record.Destination == PurchaseTreatmentPolicy.DebitNoteDestination));
            insertBatch.Parameters.AddWithValue(
                "$DebitNoteReverseRows",
                incomingRecords.Count(record => record.Destination == PurchaseTreatmentPolicy.DebitNoteReverseDestination));
            insertBatch.Parameters.AddWithValue(
                "$CreatedUtc",
                DateTime.UtcNow.ToString("O"));

            batchId = Convert.ToInt64(
                insertBatch.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        var inserted = 0;

        foreach (var record in incomingRecords)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO PurchaseRecords
                (
                    BatchId,
                    ClientId,
                    ClientGstin,
                    CompanyName,
                    FinancialYear,
                    ImportMode,
                    Destination,
                    ReconciliationStatus,
                    GstApplicability,
                    SupplierGstin,
                    SupplierName,
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
                    PurchaseLedger,
                    IgstLedger,
                    CgstLedger,
                    SgstLedger,
                    SourceSection,
                    IsAmendment,
                    OriginalDocumentNumber,
                    OriginalDocumentDate,
                    PlaceOfSupply,
                    ReverseCharge,
                    InvoiceType,
                    ItcAvailability,
                    ImsStatus,
                    TwoBReason,
                    TwoAReturnPeriod,
                    TwoBReturnPeriod,
                    TwoBAcceptance,
                    Source2AFile,
                    Source2APath,
                    Source2BFile,
                    Source2BPath,
                    TwoBInvoiceValue,
                    TwoBTaxableValue,
                    TwoBIgst,
                    TwoBCgst,
                    TwoBSgst,
                    DifferenceInvoiceValue,
                    DifferenceTaxableValue,
                    DifferenceIgst,
                    DifferenceCgst,
                    DifferenceSgst,
                    DifferenceDetails,
                    ItemSequence,
                    CreatedUtc
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
                    $ReconciliationStatus,
                    $GstApplicability,
                    $SupplierGstin,
                    $SupplierName,
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
                    $PurchaseLedger,
                    $IgstLedger,
                    $CgstLedger,
                    $SgstLedger,
                    $SourceSection,
                    $IsAmendment,
                    $OriginalDocumentNumber,
                    $OriginalDocumentDate,
                    $PlaceOfSupply,
                    $ReverseCharge,
                    $InvoiceType,
                    $ItcAvailability,
                    $ImsStatus,
                    $TwoBReason,
                    $TwoAReturnPeriod,
                    $TwoBReturnPeriod,
                    $TwoBAcceptance,
                    $Source2AFile,
                    $Source2APath,
                    $Source2BFile,
                    $Source2BPath,
                    $TwoBInvoiceValue,
                    $TwoBTaxableValue,
                    $TwoBIgst,
                    $TwoBCgst,
                    $TwoBSgst,
                    $DifferenceInvoiceValue,
                    $DifferenceTaxableValue,
                    $DifferenceIgst,
                    $DifferenceCgst,
                    $DifferenceSgst,
                    $DifferenceDetails,
                    $ItemSequence,
                    $CreatedUtc
                );

                SELECT last_insert_rowid();
                """;

            AddParameters(
                insert,
                batchId,
                clientId,
                financialYear,
                record);

            var recordId = Convert.ToInt64(
                insert.ExecuteScalar(),
                CultureInfo.InvariantCulture);

            record.PersistentId = recordId;
            record.BatchId = batchId;

            using var updateJson = connection.CreateCommand();
            updateJson.Transaction = transaction;
            updateJson.CommandText =
                """
                UPDATE PurchaseRecords
                SET
                    RecordJson = $RecordJson,
                    UpdatedUtc = $UpdatedUtc,
                    IsUserEdited = $IsUserEdited
                WHERE Id = $Id;
                """;
            updateJson.Parameters.AddWithValue(
                "$RecordJson",
                JsonSerializer.Serialize(record));
            updateJson.Parameters.AddWithValue(
                "$UpdatedUtc",
                DateTime.UtcNow.ToString("O"));
            updateJson.Parameters.AddWithValue(
                "$IsUserEdited",
                record.IsUserEdited ? 1 : 0);
            updateJson.Parameters.AddWithValue("$Id", recordId);
            updateJson.ExecuteNonQuery();

            inserted++;
        }

        transaction.Commit();
        return inserted;
    }

    public int ReplaceAutomaticImportMonth(
        long clientId,
        string financialYear,
        PurchaseAutoImportResult result,
        DateTime month)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.WasBlocked)
        {
            return 0;
        }

        var selectedMonth = new DateTime(month.Year, month.Month, 1);
        var useAnnualTwoAMonthExtraction =
            ShouldUseAnnualTwoAMonthExtraction(result.Verification.TwoA, selectedMonth);
        var useAnnualTwoBMonthExtraction =
            ShouldUseAnnualTwoAMonthExtraction(result.Verification.TwoB, selectedMonth);
        var existingRecords = GetAutomaticRecords(clientId, financialYear);
        var existingMonthRecords = existingRecords
            .Where(record => IsRecordInReimportMonth(
                record,
                selectedMonth,
                result.Verification,
                useAnnualTwoAMonthExtraction,
                useAnnualTwoBMonthExtraction))
            .ToArray();

        var preservedUserEdits = existingMonthRecords
            .Where(record => record.IsUserEdited)
            .ToArray();

        var incomingRecords = result.Records
            .Where(record => IsRecordInReimportMonth(
                record,
                selectedMonth,
                result.Verification,
                useAnnualTwoAMonthExtraction,
                useAnnualTwoBMonthExtraction))
            .ToList();

        // Month reimport follows the same edit-preservation rule as the full-FY
        // replacement, but only inside the selected source month. Rows in every
        // other month keep their existing IDs, BatchIds, edit flags and timestamps.
        AppendManualTwoBRateLines(incomingRecords, preservedUserEdits);
        PreserveUserEdits(incomingRecords, preservedUserEdits);

        foreach (var record in incomingRecords)
        {
            RepairRateFromTaxEvidence(record);
            PurchaseTreatmentPolicy.ApplyAccountingShape(record);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var existing in existingMonthRecords)
        {
            if (existing.PersistentId <= 0)
            {
                continue;
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM PurchaseRecords WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear AND ImportMode='Auto Import';";
            delete.Parameters.AddWithValue("$Id", existing.PersistentId);
            delete.Parameters.AddWithValue("$ClientId", clientId);
            delete.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            delete.ExecuteNonQuery();
        }

        var monthDuplicateKeys = LoadPurchaseDuplicateKeys(connection, transaction, clientId, financialYear);
        incomingRecords = incomingRecords
            .Where(record => monthDuplicateKeys.Add(ManualDuplicateKey(record)))
            .ToList();

        var monthSummary = new PurchaseImportSummary
        {
            PurchaseRows = incomingRecords.Count(record => record.Destination == "Purchase"),
            RcmRows = incomingRecords.Count(record => record.Destination == "RCM"),
            DebitNoteRows = incomingRecords.Count(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteDestination),
            DebitNoteReverseRows = incomingRecords.Count(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteReverseDestination),
            MatchedRows = incomingRecords.Count(record =>
                record.ReconciliationStatus.Contains("Matched", StringComparison.OrdinalIgnoreCase) &&
                !record.ReconciliationStatus.Contains("Difference", StringComparison.OrdinalIgnoreCase)),
            TwoAOnlyRows = incomingRecords.Count(record =>
                record.ReconciliationStatus.Equals("Not Found in 2B", StringComparison.OrdinalIgnoreCase) ||
                record.ReconciliationStatus.Equals("2A Only", StringComparison.OrdinalIgnoreCase)),
            TwoBOnlyRows = incomingRecords.Count(record =>
                record.ReconciliationStatus.Equals("2B Only", StringComparison.OrdinalIgnoreCase)),
            DifferenceRows = incomingRecords.Count(record =>
                record.ReconciliationStatus.Contains("Difference", StringComparison.OrdinalIgnoreCase) ||
                record.ReconciliationStatus.Equals("Rejected in 2B", StringComparison.OrdinalIgnoreCase))
        };

        long batchId;

        using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO PurchaseImportBatches
                (
                    ClientId,
                    FinancialYear,
                    ImportMode,
                    Status,
                    EReturnsRoot,
                    VerificationJson,
                    SourceManifestJson,
                    SourceFingerprint,
                    PurchaseRows,
                    RcmRows,
                    DebitNoteRows,
                    DebitNoteReverseRows,
                    CreatedUtc
                )
                VALUES
                (
                    $ClientId,
                    $FinancialYear,
                    'Auto Reimport Month',
                    'Imported',
                    $EReturnsRoot,
                    $VerificationJson,
                    $SourceManifestJson,
                    $SourceFingerprint,
                    $PurchaseRows,
                    $RcmRows,
                    $DebitNoteRows,
                    $DebitNoteReverseRows,
                    $CreatedUtc
                );

                SELECT last_insert_rowid();
                """;

            insertBatch.Parameters.AddWithValue("$ClientId", clientId);
            insertBatch.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            insertBatch.Parameters.AddWithValue("$EReturnsRoot", result.Verification.EReturnsRoot);
            insertBatch.Parameters.AddWithValue("$VerificationJson", JsonSerializer.Serialize(result.Verification));
            insertBatch.Parameters.AddWithValue("$SourceManifestJson", JsonSerializer.Serialize(result.SourceManifest));
            insertBatch.Parameters.AddWithValue(
                "$SourceFingerprint",
                $"MONTH:{selectedMonth:yyyyMM}|{result.SourceFingerprint}");
            insertBatch.Parameters.AddWithValue("$PurchaseRows", monthSummary.PurchaseRows);
            insertBatch.Parameters.AddWithValue("$RcmRows", monthSummary.RcmRows);
            insertBatch.Parameters.AddWithValue("$DebitNoteRows", monthSummary.DebitNoteRows);
            insertBatch.Parameters.AddWithValue("$DebitNoteReverseRows", monthSummary.DebitNoteReverseRows);
            insertBatch.Parameters.AddWithValue("$CreatedUtc", DateTime.UtcNow.ToString("O"));

            batchId = Convert.ToInt64(
                insertBatch.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        var inserted = 0;

        foreach (var record in incomingRecords)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO PurchaseRecords
                (
                    BatchId,
                    ClientId,
                    ClientGstin,
                    CompanyName,
                    FinancialYear,
                    ImportMode,
                    Destination,
                    ReconciliationStatus,
                    GstApplicability,
                    SupplierGstin,
                    SupplierName,
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
                    PurchaseLedger,
                    IgstLedger,
                    CgstLedger,
                    SgstLedger,
                    SourceSection,
                    IsAmendment,
                    OriginalDocumentNumber,
                    OriginalDocumentDate,
                    PlaceOfSupply,
                    ReverseCharge,
                    InvoiceType,
                    ItcAvailability,
                    ImsStatus,
                    TwoBReason,
                    TwoAReturnPeriod,
                    TwoBReturnPeriod,
                    TwoBAcceptance,
                    Source2AFile,
                    Source2APath,
                    Source2BFile,
                    Source2BPath,
                    TwoBInvoiceValue,
                    TwoBTaxableValue,
                    TwoBIgst,
                    TwoBCgst,
                    TwoBSgst,
                    DifferenceInvoiceValue,
                    DifferenceTaxableValue,
                    DifferenceIgst,
                    DifferenceCgst,
                    DifferenceSgst,
                    DifferenceDetails,
                    ItemSequence,
                    CreatedUtc
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
                    $ReconciliationStatus,
                    $GstApplicability,
                    $SupplierGstin,
                    $SupplierName,
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
                    $PurchaseLedger,
                    $IgstLedger,
                    $CgstLedger,
                    $SgstLedger,
                    $SourceSection,
                    $IsAmendment,
                    $OriginalDocumentNumber,
                    $OriginalDocumentDate,
                    $PlaceOfSupply,
                    $ReverseCharge,
                    $InvoiceType,
                    $ItcAvailability,
                    $ImsStatus,
                    $TwoBReason,
                    $TwoAReturnPeriod,
                    $TwoBReturnPeriod,
                    $TwoBAcceptance,
                    $Source2AFile,
                    $Source2APath,
                    $Source2BFile,
                    $Source2BPath,
                    $TwoBInvoiceValue,
                    $TwoBTaxableValue,
                    $TwoBIgst,
                    $TwoBCgst,
                    $TwoBSgst,
                    $DifferenceInvoiceValue,
                    $DifferenceTaxableValue,
                    $DifferenceIgst,
                    $DifferenceCgst,
                    $DifferenceSgst,
                    $DifferenceDetails,
                    $ItemSequence,
                    $CreatedUtc
                );

                SELECT last_insert_rowid();
                """;

            AddParameters(
                insert,
                batchId,
                clientId,
                financialYear,
                record);

            var recordId = Convert.ToInt64(
                insert.ExecuteScalar(),
                CultureInfo.InvariantCulture);

            record.PersistentId = recordId;
            record.BatchId = batchId;

            using var updateJson = connection.CreateCommand();
            updateJson.Transaction = transaction;
            updateJson.CommandText =
                """
                UPDATE PurchaseRecords
                SET
                    RecordJson = $RecordJson,
                    UpdatedUtc = $UpdatedUtc,
                    IsUserEdited = $IsUserEdited
                WHERE Id = $Id;
                """;
            updateJson.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
            updateJson.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            updateJson.Parameters.AddWithValue("$IsUserEdited", record.IsUserEdited ? 1 : 0);
            updateJson.Parameters.AddWithValue("$Id", recordId);
            updateJson.ExecuteNonQuery();

            inserted++;
        }

        transaction.Commit();
        return inserted;
    }

    private static bool IsRecordInReimportMonth(
        PurchaseImportRecord record,
        DateTime selectedMonth,
        PurchaseSourceVerification verification,
        bool useAnnualTwoAMonthExtraction,
        bool useAnnualTwoBMonthExtraction)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(verification);

        // Fix 11: when a dedicated monthly 2A payload is not available but a verified
        // annual 2A payload is available, the annual payload itself is the source of the
        // selected-month rebuild. Some GST annual exports stamp their FY-end wrapper
        // period (for example 032026) on every 2A document. Treating that wrapper as a
        // real row-level month collapses April-February into March and produces the
        // observed "0 rows rebuilt" result for May. In annual-fallback mode, a matched
        // 2B period remains stronger evidence because it is row/supplier-specific; an
        // annual 2A-only row falls back to its actual document month.
        if (useAnnualTwoBMonthExtraction &&
            IsAnnualSourceRecord(record.Source2BPath, record.Source2BFile, verification.TwoB.AnnualSource) &&
            (!string.IsNullOrWhiteSpace(record.TwoBAcceptance) || record.IsTwoBOnlyEvidence))
        {
            var annualTwoBDate = record.GstDate ?? record.DocumentDate;
            return annualTwoBDate.HasValue &&
                   annualTwoBDate.Value.Year == selectedMonth.Year &&
                   annualTwoBDate.Value.Month == selectedMonth.Month;
        }

        if (useAnnualTwoAMonthExtraction &&
            IsAnnualSourceRecord(record.Source2APath, record.Source2AFile, verification.TwoA.AnnualSource))
        {
            if (!string.IsNullOrWhiteSpace(record.Source2BPath) &&
                PurchaseImportUtilities.TryParseReturnPeriod(record.TwoBReturnPeriod, out var matchedTwoBPeriod))
            {
                return matchedTwoBPeriod.Year == selectedMonth.Year &&
                       matchedTwoBPeriod.Month == selectedMonth.Month;
            }

            var annualFallbackDate = record.DocumentDate ?? record.GstDate;
            return annualFallbackDate.HasValue &&
                   annualFallbackDate.Value.Year == selectedMonth.Year &&
                   annualFallbackDate.Value.Month == selectedMonth.Month;
        }

        // Purchase month reimport is source-period based, not merely DocumentDate based.
        // Matched/2A-only rows belong to their primary GSTR-2A return period; 2B-only
        // evidence belongs to its GSTR-2B return period. This is stable even when a
        // repaired GST Date legitimately moves to another month after reimport.
        if (PurchaseImportUtilities.TryParseReturnPeriod(record.TwoAReturnPeriod, out var twoAPeriod))
        {
            return twoAPeriod.Year == selectedMonth.Year &&
                   twoAPeriod.Month == selectedMonth.Month;
        }

        if (PurchaseImportUtilities.TryParseReturnPeriod(record.TwoBReturnPeriod, out var twoBPeriod))
        {
            return twoBPeriod.Year == selectedMonth.Year &&
                   twoBPeriod.Month == selectedMonth.Month;
        }

        var fallback = record.DocumentDate ?? record.GstDate;
        return fallback.HasValue &&
               fallback.Value.Year == selectedMonth.Year &&
               fallback.Value.Month == selectedMonth.Month;
    }

    private static bool ShouldUseAnnualTwoAMonthExtraction(
        PurchaseFormVerification verification,
        DateTime selectedMonth)
    {
        if (string.IsNullOrWhiteSpace(verification.AnnualSource))
        {
            return false;
        }

        var monthKey = selectedMonth.ToString("yyyyMM", CultureInfo.InvariantCulture);
        return !verification.MonthlyMonthKeys.Any(key =>
            string.Equals(
                new string((key ?? string.Empty).Where(char.IsDigit).ToArray()),
                monthKey,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameSource(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

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

    private static bool IsAnnualSourceRecord(
        string? savedPath,
        string? savedFile,
        string? annualSource)
    {
        if (IsSameSource(savedPath, annualSource)) return true;
        if (string.IsNullOrWhiteSpace(annualSource)) return false;

        var annualName = Path.GetFileName(annualSource.Trim());
        if (annualName.Length == 0) return false;
        if (string.Equals((savedFile ?? string.Empty).Trim(), annualName, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return string.Equals(
                Path.GetFileName((savedPath ?? string.Empty).Trim()),
                annualName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public long InsertManualRateLine(
        long clientId,
        string financialYear,
        PurchaseImportRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.BatchId <= 0)
            throw new InvalidOperationException("A saved Purchase batch is required before adding a GST rate line.");

        PurchaseTreatmentPolicy.ApplyAccountingShape(record);
        record.IsUserEdited = true;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO PurchaseRecords
            (
                BatchId, ClientId, ClientGstin, CompanyName, FinancialYear, ImportMode,
                Destination, ReconciliationStatus, GstApplicability, SupplierGstin, SupplierName,
                DocumentType, DocumentNumber, DocumentDate, GstDate, SourceDocumentValue,
                DocumentValue, Rate, TaxableValue, Igst, Cgst, Sgst, Cess, RoundOff,
                PurchaseLedger, IgstLedger, CgstLedger, SgstLedger, SourceSection, IsAmendment,
                OriginalDocumentNumber, OriginalDocumentDate, PlaceOfSupply, ReverseCharge,
                InvoiceType, ItcAvailability, ImsStatus, TwoBReason, TwoAReturnPeriod,
                TwoBReturnPeriod, TwoBAcceptance, Source2AFile, Source2APath, Source2BFile,
                Source2BPath, TwoBInvoiceValue, TwoBTaxableValue, TwoBIgst, TwoBCgst, TwoBSgst,
                DifferenceInvoiceValue, DifferenceTaxableValue, DifferenceIgst, DifferenceCgst,
                DifferenceSgst, DifferenceDetails, ItemSequence, CreatedUtc
            )
            VALUES
            (
                $BatchId, $ClientId, $ClientGstin, $CompanyName, $FinancialYear, 'Auto Import',
                $Destination, $ReconciliationStatus, $GstApplicability, $SupplierGstin, $SupplierName,
                $DocumentType, $DocumentNumber, $DocumentDate, $GstDate, $SourceDocumentValue,
                $DocumentValue, $Rate, $TaxableValue, $Igst, $Cgst, $Sgst, $Cess, $RoundOff,
                $PurchaseLedger, $IgstLedger, $CgstLedger, $SgstLedger, $SourceSection, $IsAmendment,
                $OriginalDocumentNumber, $OriginalDocumentDate, $PlaceOfSupply, $ReverseCharge,
                $InvoiceType, $ItcAvailability, $ImsStatus, $TwoBReason, $TwoAReturnPeriod,
                $TwoBReturnPeriod, $TwoBAcceptance, $Source2AFile, $Source2APath, $Source2BFile,
                $Source2BPath, $TwoBInvoiceValue, $TwoBTaxableValue, $TwoBIgst, $TwoBCgst, $TwoBSgst,
                $DifferenceInvoiceValue, $DifferenceTaxableValue, $DifferenceIgst, $DifferenceCgst,
                $DifferenceSgst, $DifferenceDetails, $ItemSequence, $CreatedUtc
            );
            SELECT last_insert_rowid();
            """;

        AddParameters(insert, record.BatchId, clientId, financialYear, record);
        var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        record.PersistentId = id;

        using var updateJson = connection.CreateCommand();
        updateJson.Transaction = transaction;
        updateJson.CommandText =
            """
            UPDATE PurchaseRecords
            SET RecordJson=$RecordJson, UpdatedUtc=$UpdatedUtc, IsUserEdited=1
            WHERE Id=$Id AND ClientId=$ClientId;
            """;
        updateJson.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
        updateJson.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        updateJson.Parameters.AddWithValue("$Id", id);
        updateJson.Parameters.AddWithValue("$ClientId", clientId);
        updateJson.ExecuteNonQuery();

        transaction.Commit();
        return id;
    }

    public int DeleteRecords(
        long clientId,
        string financialYear,
        IEnumerable<long> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        var ids = recordIds.Where(id => id > 0).Distinct().ToArray();
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || ids.Length == 0) return 0;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var deleted = 0;
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "DELETE FROM PurchaseRecords WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FinancialYear;";
            command.Parameters.AddWithValue("$Id", id);
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            deleted += command.ExecuteNonQuery();
        }

        // Remove batch shells only when every row in that batch has been deleted.
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM PurchaseImportBatches
                WHERE ClientId=$ClientId
                  AND FinancialYear=$FinancialYear
                  AND NOT EXISTS
                      (SELECT 1 FROM PurchaseRecords WHERE PurchaseRecords.BatchId=PurchaseImportBatches.Id);
                """;
            cleanup.Parameters.AddWithValue("$ClientId", clientId);
            cleanup.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            cleanup.ExecuteNonQuery();
        }

        transaction.Commit();
        return deleted;
    }

    public IReadOnlyList<StockProjectionSnapshot<PurchaseImportRecord>> GetStockProjectionSnapshots(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,BatchId,IsUserEdited,UpdatedUtc,RecordJson FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$FY ORDER BY Id;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FY", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<StockProjectionSnapshot<PurchaseImportRecord>>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var batchId = reader.GetInt64(1);
            var userEdited = reader.GetInt64(2) != 0;
            var updatedUtc = reader.GetString(3);
            var original = reader.GetString(4);
            PurchaseImportRecord? record = null;
            try { record = JsonSerializer.Deserialize<PurchaseImportRecord>(original); }
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
        return StockProjectionPersistence.Apply(connection, "PurchaseRecords", clientId, financialYear, changes);
    }

    public int UpdateStockIdentityReferences(
        long clientId,
        IEnumerable<PurchaseImportRecord> records)
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
            command.CommandText = "UPDATE PurchaseRecords SET RecordJson=$RecordJson,UpdatedUtc=$UpdatedUtc WHERE Id=$Id AND ClientId=$ClientId;";
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
        IEnumerable<PurchaseImportRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var materialized = records
            .Where(record => record.PersistentId > 0)
            .GroupBy(record => record.PersistentId)
            .Select(group => group.First())
            .ToArray();

        if (materialized.Length == 0)
        {
            return 0;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var updated = 0;

        foreach (var record in materialized)
        {
            // Persist only a fully-resolved treatment. This protects the database
            // from a transient blank produced while an editable selector is changing
            // focus/options and keeps Journal/GSTR-3B consumers on the same invariant.
            RepairRateFromTaxEvidence(record);
            PurchaseTreatmentPolicy.ApplyAccountingShape(record);
            record.IsUserEdited = true;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE PurchaseRecords
                SET
                    Destination = $Destination,
                    ReconciliationStatus = $ReconciliationStatus,
                    GstApplicability = $GstApplicability,
                    SupplierGstin = $SupplierGstin,
                    SupplierName = $SupplierName,
                    DocumentType = $DocumentType,
                    DocumentNumber = $DocumentNumber,
                    DocumentDate = $DocumentDate,
                    GstDate = $GstDate,
                    SourceDocumentValue = $SourceDocumentValue,
                    DocumentValue = $DocumentValue,
                    Rate = $Rate,
                    TaxableValue = $TaxableValue,
                    Igst = $Igst,
                    Cgst = $Cgst,
                    Sgst = $Sgst,
                    Cess = $Cess,
                    RoundOff = $RoundOff,
                    PurchaseLedger = $PurchaseLedger,
                    IgstLedger = $IgstLedger,
                    CgstLedger = $CgstLedger,
                    SgstLedger = $SgstLedger,
                    SourceSection = $SourceSection,
                    IsAmendment = $IsAmendment,
                    OriginalDocumentNumber = $OriginalDocumentNumber,
                    OriginalDocumentDate = $OriginalDocumentDate,
                    PlaceOfSupply = $PlaceOfSupply,
                    ReverseCharge = $ReverseCharge,
                    DifferenceDetails = $DifferenceDetails,
                    RecordJson = $RecordJson,
                    UpdatedUtc = $UpdatedUtc,
                    IsUserEdited = 1
                WHERE
                    Id = $Id
                    AND ClientId = $ClientId
                    AND ImportMode = 'Auto Import';
                """;

            command.Parameters.AddWithValue("$Destination", record.Destination);
            command.Parameters.AddWithValue("$ReconciliationStatus", record.ReconciliationStatus);
            command.Parameters.AddWithValue("$GstApplicability", record.GstApplicability);
            command.Parameters.AddWithValue("$SupplierGstin", record.SupplierGstin);
            command.Parameters.AddWithValue("$SupplierName", record.SupplierName);
            command.Parameters.AddWithValue("$DocumentType", record.DocumentType);
            command.Parameters.AddWithValue("$DocumentNumber", record.DocumentNumber);
            command.Parameters.AddWithValue("$DocumentDate", FormatDate(record.DocumentDate));
            command.Parameters.AddWithValue("$GstDate", FormatDate(record.GstDate));
            command.Parameters.AddWithValue("$SourceDocumentValue", record.SourceDocumentValue);
            command.Parameters.AddWithValue("$DocumentValue", record.DocumentValue);
            command.Parameters.AddWithValue("$Rate", record.Rate);
            command.Parameters.AddWithValue("$TaxableValue", record.TaxableValue);
            command.Parameters.AddWithValue("$Igst", record.Igst);
            command.Parameters.AddWithValue("$Cgst", record.Cgst);
            command.Parameters.AddWithValue("$Sgst", record.Sgst);
            command.Parameters.AddWithValue("$Cess", record.Cess);
            command.Parameters.AddWithValue("$RoundOff", record.RoundOff);
            command.Parameters.AddWithValue("$PurchaseLedger", record.PurchaseLedger);
            command.Parameters.AddWithValue("$IgstLedger", record.IgstLedger);
            command.Parameters.AddWithValue("$CgstLedger", record.CgstLedger);
            command.Parameters.AddWithValue("$SgstLedger", record.SgstLedger);
            command.Parameters.AddWithValue("$SourceSection", record.SourceSection);
            command.Parameters.AddWithValue("$IsAmendment", record.IsAmendment ? 1 : 0);
            command.Parameters.AddWithValue("$OriginalDocumentNumber", record.OriginalDocumentNumber);
            command.Parameters.AddWithValue("$OriginalDocumentDate", FormatDate(record.OriginalDocumentDate));
            command.Parameters.AddWithValue("$PlaceOfSupply", record.PlaceOfSupply);
            command.Parameters.AddWithValue("$ReverseCharge", record.ReverseCharge);
            command.Parameters.AddWithValue("$DifferenceDetails", record.DifferenceDetails);
            command.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$Id", record.PersistentId);
            command.Parameters.AddWithValue("$ClientId", clientId);

            updated += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return updated;
    }


    private static void AppendManualTwoBRateLines(
        ICollection<PurchaseImportRecord> incomingRecords,
        IReadOnlyCollection<PurchaseImportRecord> preservedRecords)
    {
        var existing = incomingRecords
            .Where(IsTwoBOnlyEvidence)
            .Select(BuildManualTwoBLineIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var saved in preservedRecords.Where(record =>
                     IsTwoBOnlyEvidence(record) &&
                     record.DifferenceDetails.Contains("Manual additional GST rate line", StringComparison.OrdinalIgnoreCase)))
        {
            var identity = BuildManualTwoBLineIdentity(saved);
            if (existing.Contains(identity)) continue;

            var clone = JsonSerializer.Deserialize<PurchaseImportRecord>(JsonSerializer.Serialize(saved));
            if (clone is null) continue;
            clone.PersistentId = 0;
            clone.BatchId = 0;
            clone.IsUserEdited = true;
            incomingRecords.Add(clone);
            existing.Add(identity);
        }
    }

    private static string BuildManualTwoBLineIdentity(PurchaseImportRecord row) =>
        string.Join(
            "\u001f",
            NormalizeIdentityText(!string.IsNullOrWhiteSpace(row.Source2BPath) ? row.Source2BPath : row.Source2BFile),
            NormalizeIdentityText(row.SupplierGstin),
            NormalizeIdentityText(row.DocumentNumber),
            row.DocumentDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            row.ItemSequence.ToString(CultureInfo.InvariantCulture));

    private static void PreserveUserEdits(
        IEnumerable<PurchaseImportRecord> incomingRecords,
        IReadOnlyCollection<PurchaseImportRecord> preservedRecords)
    {
        if (preservedRecords.Count == 0)
        {
            return;
        }

        var strong = BuildUniqueEditMap(
            preservedRecords,
            includeSourceIdentity: true);

        var fallback = BuildUniqueEditMap(
            preservedRecords,
            includeSourceIdentity: false);

        foreach (var incoming in incomingRecords)
        {
            PurchaseImportRecord? preserved = null;

            var strongKey = BuildEditIdentity(
                incoming,
                includeSourceIdentity: true);

            if (strong.TryGetValue(strongKey, out var strongMatch))
            {
                preserved = strongMatch;
            }
            else
            {
                var fallbackKey = BuildEditIdentity(
                    incoming,
                    includeSourceIdentity: false);

                fallback.TryGetValue(
                    fallbackKey,
                    out preserved);
            }

            if (preserved is null)
            {
                continue;
            }

            OverlayUserEditableFields(
                incoming,
                preserved);
        }
    }

    private static Dictionary<string, PurchaseImportRecord> BuildUniqueEditMap(
        IEnumerable<PurchaseImportRecord> records,
        bool includeSourceIdentity) =>
        records
            .GroupBy(
                record => BuildEditIdentity(
                    record,
                    includeSourceIdentity),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);

    private static string BuildEditIdentity(
        PurchaseImportRecord record,
        bool includeSourceIdentity)
    {
        var isTwoBOnly = IsTwoBOnlyEvidence(record);
        var sourceIdentity = includeSourceIdentity
            ? string.Join(
                "\u001f",
                NormalizeIdentityText(isTwoBOnly ? record.Source2BPath : record.Source2APath),
                NormalizeIdentityText(isTwoBOnly ? record.Source2BFile : record.Source2AFile),
                NormalizeIdentityText(record.SourceSection))
            : string.Empty;

        return string.Join(
            "\u001f",
            NormalizeIdentityText(isTwoBOnly ? "2BONLY" : record.Destination),
            NormalizeIdentityText(record.SupplierGstin),
            NormalizeIdentityText(record.DocumentNumber),
            record.DocumentDate?.ToString(
                "yyyyMMdd",
                CultureInfo.InvariantCulture) ?? string.Empty,
            isTwoBOnly
                ? string.Empty
                : record.Rate.ToString(
                    "0.################",
                    CultureInfo.InvariantCulture),
            record.ItemSequence.ToString(
                CultureInfo.InvariantCulture),
            sourceIdentity);
    }

    private static string NormalizeIdentityText(string? value) =>
        (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant();

    private static bool IsTwoBOnlyEvidence(PurchaseImportRecord record) =>
        record.ReconciliationStatus.Equals("2B Only", StringComparison.OrdinalIgnoreCase) ||
        (string.IsNullOrWhiteSpace(record.Source2APath) &&
         string.IsNullOrWhiteSpace(record.Source2AFile) &&
         (!string.IsNullOrWhiteSpace(record.Source2BPath) || !string.IsNullOrWhiteSpace(record.Source2BFile)));

    private static void OverlayUserEditableFields(
        PurchaseImportRecord target,
        PurchaseImportRecord saved)
    {
        if (IsTwoBOnlyEvidence(target) && IsTwoBOnlyEvidence(saved) &&
            !saved.Destination.Equals("2B Only", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(saved.Destination))
        {
            target.Destination = saved.Destination;
            if (!saved.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase))
                target.GstApplicability = PurchaseTreatmentPolicy.IncludedInPurchase;
        }

        // Preserve only a meaningful user treatment. Blank legacy values are invalid
        // and must not overwrite the deterministic treatment from the fresh source.
        // For non-RCM rows, Shown/Not shown is source evidence; the only deliberate
        // user override is GST included in cost. RCM timing remains user-authoritative.
        if (PurchaseTreatmentPolicy.IsRcm(target))
        {
            if (!string.IsNullOrWhiteSpace(saved.GstApplicability))
                target.GstApplicability =
                    PurchaseTreatmentPolicy.NormalizeRcmTreatment(saved.GstApplicability);
        }
        else if (PurchaseTreatmentPolicy.IsGstIncludedInCost(saved.GstApplicability))
        {
            target.GstApplicability = PurchaseTreatmentPolicy.GstIncludedInCost;
        }
        target.SupplierGstin = saved.SupplierGstin;
        target.SupplierName = saved.SupplierName;
        target.DocumentType = saved.DocumentType;
        target.DocumentNumber = saved.DocumentNumber;
        target.DocumentDate = saved.DocumentDate;
        // Fresh GSTR-2B-only GST Date is source-authoritative on reimport because it
        // is rebuilt from supplier supfildt + supprd. Do not overwrite that corrected
        // date with Completion 54's stale saved value. If the source cannot establish
        // any GST Date, keep an existing saved/manual value rather than erasing it.
        if (IsTwoBOnlyEvidence(target))
        {
            if (!target.GstDate.HasValue && saved.GstDate.HasValue)
                target.GstDate = saved.GstDate;
        }
        else if (!string.IsNullOrWhiteSpace(target.Source2BPath) ||
                 !string.IsNullOrWhiteSpace(target.Source2BFile))
        {
            // Completion 58: matched 2B filing evidence is source-authoritative for
            // GST Date during a forced Purchase reimport. Older annual imports could
            // persist 01-March-of-FY-close on user-edited rows because the broad
            // wrapper period was overlaid back from SQLite after a fresh parse. Keep
            // the freshly recalculated matched-2B GST Date; only use the saved value
            // when the current source genuinely cannot establish one.
            if (!target.GstDate.HasValue && saved.GstDate.HasValue)
                target.GstDate = saved.GstDate;
        }
        else
        {
            target.GstDate = saved.GstDate;
        }
        target.DocumentValue = saved.DocumentValue;
        target.Rate = saved.Rate;
        target.RateKnown = saved.RateKnown;
        target.TaxableValue = saved.TaxableValue;
        target.Igst = saved.Igst;
        target.Cgst = saved.Cgst;
        target.Sgst = saved.Sgst;
        target.Cess = saved.Cess;
        target.RoundOff = saved.RoundOff;
        target.PurchaseLedger = saved.PurchaseLedger;
        target.IgstLedger = saved.IgstLedger;
        target.CgstLedger = saved.CgstLedger;
        target.SgstLedger = saved.SgstLedger;
        target.SourceSection = saved.SourceSection;
        target.IsAmendment = saved.IsAmendment;
        target.OriginalDocumentNumber = saved.OriginalDocumentNumber;
        target.OriginalDocumentDate = saved.OriginalDocumentDate;
        target.PlaceOfSupply = saved.PlaceOfSupply;
        target.ReverseCharge = saved.ReverseCharge;
        target.IsUserEdited = true;
    }

    private void PersistAccountingShapeRepairs(
        long clientId,
        IReadOnlyCollection<PurchaseImportRecord> repaired)
    {
        if (repaired.Count == 0) return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var record in repaired.Where(item => item.PersistentId > 0))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE PurchaseRecords
                SET
                    Destination = $Destination,
                    GstApplicability = $GstApplicability,
                    Rate = $Rate,
                    GstDate = $GstDate,
                    PurchaseLedger = $PurchaseLedger,
                    IgstLedger = $IgstLedger,
                    CgstLedger = $CgstLedger,
                    SgstLedger = $SgstLedger,
                    RecordJson = $RecordJson,
                    UpdatedUtc = $UpdatedUtc
                WHERE
                    Id = $Id
                    AND ClientId = $ClientId
                    AND ImportMode = 'Auto Import';
                """;

            command.Parameters.AddWithValue("$Destination", record.Destination);
            command.Parameters.AddWithValue("$GstApplicability", record.GstApplicability);
            command.Parameters.AddWithValue("$Rate", record.Rate);
            command.Parameters.AddWithValue("$GstDate", FormatDate(record.GstDate));
            command.Parameters.AddWithValue("$PurchaseLedger", record.PurchaseLedger);
            command.Parameters.AddWithValue("$IgstLedger", record.IgstLedger);
            command.Parameters.AddWithValue("$CgstLedger", record.CgstLedger);
            command.Parameters.AddWithValue("$SgstLedger", record.SgstLedger);
            command.Parameters.AddWithValue("$RecordJson", JsonSerializer.Serialize(record));
            command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$Id", record.PersistentId);
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void AddParameters(
        SqliteCommand command,
        long batchId,
        long clientId,
        string financialYear,
        PurchaseImportRecord record)
    {
        command.Parameters.AddWithValue("$BatchId", batchId);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$ClientGstin", record.ClientGstin);
        command.Parameters.AddWithValue("$CompanyName", record.CompanyName);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$Destination", record.Destination);
        command.Parameters.AddWithValue("$ReconciliationStatus", record.ReconciliationStatus);
        command.Parameters.AddWithValue("$GstApplicability", record.GstApplicability);
        command.Parameters.AddWithValue("$SupplierGstin", record.SupplierGstin);
        command.Parameters.AddWithValue("$SupplierName", record.SupplierName);
        command.Parameters.AddWithValue("$DocumentType", record.DocumentType);
        command.Parameters.AddWithValue("$DocumentNumber", record.DocumentNumber);
        command.Parameters.AddWithValue("$DocumentDate", FormatDate(record.DocumentDate));
        command.Parameters.AddWithValue("$GstDate", FormatDate(record.GstDate));
        command.Parameters.AddWithValue("$SourceDocumentValue", record.SourceDocumentValue);
        command.Parameters.AddWithValue("$DocumentValue", record.DocumentValue);
        command.Parameters.AddWithValue("$Rate", record.Rate);
        command.Parameters.AddWithValue("$TaxableValue", record.TaxableValue);
        command.Parameters.AddWithValue("$Igst", record.Igst);
        command.Parameters.AddWithValue("$Cgst", record.Cgst);
        command.Parameters.AddWithValue("$Sgst", record.Sgst);
        command.Parameters.AddWithValue("$Cess", record.Cess);
        command.Parameters.AddWithValue("$RoundOff", record.RoundOff);
        command.Parameters.AddWithValue("$PurchaseLedger", record.PurchaseLedger);
        command.Parameters.AddWithValue("$IgstLedger", record.IgstLedger);
        command.Parameters.AddWithValue("$CgstLedger", record.CgstLedger);
        command.Parameters.AddWithValue("$SgstLedger", record.SgstLedger);
        command.Parameters.AddWithValue("$SourceSection", record.SourceSection);
        command.Parameters.AddWithValue("$IsAmendment", record.IsAmendment ? 1 : 0);
        command.Parameters.AddWithValue("$OriginalDocumentNumber", record.OriginalDocumentNumber);
        command.Parameters.AddWithValue("$OriginalDocumentDate", FormatDate(record.OriginalDocumentDate));
        command.Parameters.AddWithValue("$PlaceOfSupply", record.PlaceOfSupply);
        command.Parameters.AddWithValue("$ReverseCharge", record.ReverseCharge);
        command.Parameters.AddWithValue("$InvoiceType", record.InvoiceType);
        command.Parameters.AddWithValue("$ItcAvailability", record.ItcAvailability);
        command.Parameters.AddWithValue("$ImsStatus", record.ImsStatus);
        command.Parameters.AddWithValue("$TwoBReason", record.TwoBReason);
        command.Parameters.AddWithValue("$TwoAReturnPeriod", record.TwoAReturnPeriod);
        command.Parameters.AddWithValue("$TwoBReturnPeriod", record.TwoBReturnPeriod);
        command.Parameters.AddWithValue("$TwoBAcceptance", record.TwoBAcceptance);
        command.Parameters.AddWithValue("$Source2AFile", record.Source2AFile);
        command.Parameters.AddWithValue("$Source2APath", record.Source2APath);
        command.Parameters.AddWithValue("$Source2BFile", record.Source2BFile);
        command.Parameters.AddWithValue("$Source2BPath", record.Source2BPath);
        AddNullable(command, "$TwoBInvoiceValue", record.TwoBInvoiceValue);
        AddNullable(command, "$TwoBTaxableValue", record.TwoBTaxableValue);
        AddNullable(command, "$TwoBIgst", record.TwoBIgst);
        AddNullable(command, "$TwoBCgst", record.TwoBCgst);
        AddNullable(command, "$TwoBSgst", record.TwoBSgst);
        AddNullable(command, "$DifferenceInvoiceValue", record.DifferenceInvoiceValue);
        AddNullable(command, "$DifferenceTaxableValue", record.DifferenceTaxableValue);
        AddNullable(command, "$DifferenceIgst", record.DifferenceIgst);
        AddNullable(command, "$DifferenceCgst", record.DifferenceCgst);
        AddNullable(command, "$DifferenceSgst", record.DifferenceSgst);
        command.Parameters.AddWithValue("$DifferenceDetails", record.DifferenceDetails);
        command.Parameters.AddWithValue("$ItemSequence", record.ItemSequence);
        command.Parameters.AddWithValue("$CreatedUtc", DateTime.UtcNow.ToString("O"));
    }

    private static void AddNullable(
        SqliteCommand command,
        string parameterName,
        decimal? value) =>
        command.Parameters.AddWithValue(
            parameterName,
            value.HasValue
                ? value.Value
                : DBNull.Value);

    private static string FormatDate(
        DateTime? value) =>
        value?.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture)
        ?? string.Empty;

    private static PurchaseImportRecord ReadPurchaseRecord(
        SqliteDataReader reader)
    {
        var json = ReadString(reader, "RecordJson");
        PurchaseImportRecord? record = null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                record = JsonSerializer.Deserialize<PurchaseImportRecord>(json);
            }
            catch
            {
                record = null;
            }
        }

        record ??= new PurchaseImportRecord
        {
            ClientGstin = ReadString(reader, "ClientGstin"),
            CompanyName = ReadString(reader, "CompanyName"),
            ImportMode = ReadString(reader, "ImportMode"),
            Destination = ReadString(reader, "Destination"),
            ReconciliationStatus = ReadString(reader, "ReconciliationStatus"),
            GstApplicability = ReadString(reader, "GstApplicability"),
            SupplierGstin = ReadString(reader, "SupplierGstin"),
            SupplierName = ReadString(reader, "SupplierName"),
            DocumentType = ReadString(reader, "DocumentType"),
            DocumentNumber = ReadString(reader, "DocumentNumber"),
            DocumentDate = ReadDate(reader, "DocumentDate"),
            GstDate = ReadDate(reader, "GstDate"),
            SourceDocumentValue = ReadDecimal(reader, "SourceDocumentValue"),
            DocumentValue = ReadDecimal(reader, "DocumentValue"),
            Rate = ReadDecimal(reader, "Rate"),
            TaxableValue = ReadDecimal(reader, "TaxableValue"),
            Igst = ReadDecimal(reader, "Igst"),
            Cgst = ReadDecimal(reader, "Cgst"),
            Sgst = ReadDecimal(reader, "Sgst"),
            Cess = ReadDecimal(reader, "Cess"),
            RoundOff = ReadDecimal(reader, "RoundOff"),
            PurchaseLedger = ReadString(reader, "PurchaseLedger"),
            IgstLedger = ReadString(reader, "IgstLedger"),
            CgstLedger = ReadString(reader, "CgstLedger"),
            SgstLedger = ReadString(reader, "SgstLedger"),
            SourceSection = ReadString(reader, "SourceSection"),
            IsAmendment = ReadInt64(reader, "IsAmendment") != 0,
            OriginalDocumentNumber = ReadString(reader, "OriginalDocumentNumber"),
            OriginalDocumentDate = ReadDate(reader, "OriginalDocumentDate"),
            PlaceOfSupply = ReadString(reader, "PlaceOfSupply"),
            ReverseCharge = ReadString(reader, "ReverseCharge"),
            InvoiceType = ReadString(reader, "InvoiceType"),
            ItcAvailability = ReadString(reader, "ItcAvailability"),
            ImsStatus = ReadString(reader, "ImsStatus"),
            TwoBReason = ReadString(reader, "TwoBReason"),
            TwoAReturnPeriod = ReadString(reader, "TwoAReturnPeriod"),
            TwoBReturnPeriod = ReadString(reader, "TwoBReturnPeriod"),
            TwoBAcceptance = ReadString(reader, "TwoBAcceptance"),
            Source2AFile = ReadString(reader, "Source2AFile"),
            Source2APath = ReadString(reader, "Source2APath"),
            Source2BFile = ReadString(reader, "Source2BFile"),
            Source2BPath = ReadString(reader, "Source2BPath"),
            TwoBInvoiceValue = ReadNullableDecimal(reader, "TwoBInvoiceValue"),
            TwoBTaxableValue = ReadNullableDecimal(reader, "TwoBTaxableValue"),
            TwoBIgst = ReadNullableDecimal(reader, "TwoBIgst"),
            TwoBCgst = ReadNullableDecimal(reader, "TwoBCgst"),
            TwoBSgst = ReadNullableDecimal(reader, "TwoBSgst"),
            DifferenceInvoiceValue = ReadNullableDecimal(reader, "DifferenceInvoiceValue"),
            DifferenceTaxableValue = ReadNullableDecimal(reader, "DifferenceTaxableValue"),
            DifferenceIgst = ReadNullableDecimal(reader, "DifferenceIgst"),
            DifferenceCgst = ReadNullableDecimal(reader, "DifferenceCgst"),
            DifferenceSgst = ReadNullableDecimal(reader, "DifferenceSgst"),
            DifferenceDetails = ReadString(reader, "DifferenceDetails"),
            ItemSequence = (int)ReadInt64(reader, "ItemSequence")
        };

        record.PersistentId = ReadInt64(reader, "Id");
        record.BatchId = ReadInt64(reader, "BatchId");
        record.IsUserEdited = ReadInt64(reader, "IsUserEdited") != 0;

        // Completion 52 migration for retained GSTR-2B-only evidence. Earlier builds
        // stored the source-evidence wording ("Shown in 2B") and could leave the
        // accounting destination as Purchase even before explicit inclusion. Keep any
        // deliberate prior promotion (IsUserEdited + non-2B destination), but normalize
        // untouched evidence to the new Not/Included state machine.
        if (IsTwoBOnlyEvidence(record))
        {
            if (!record.IsUserEdited || record.Destination.Equals("2B Only", StringComparison.OrdinalIgnoreCase))
            {
                record.Destination = "2B Only";
                record.GstApplicability = PurchaseTreatmentPolicy.NotIncludedInPurchase;
            }
            else if (!PurchaseTreatmentPolicy.IsRcm(record))
            {
                record.GstApplicability = PurchaseTreatmentPolicy.IncludedInPurchase;
            }

            if (record.Rate == 0m &&
                (record.Igst != 0m || record.Cgst != 0m || record.Sgst != 0m))
            {
                // A true 0% GST line cannot carry non-zero GST tax. Treat legacy/source
                // zero placeholders as unknown even when the 2B row was user-edited,
                // then let the strict reverse-rate repair prove the actual rate on load.
                record.RateKnown = false;
            }

            RepairRateFromTaxEvidence(record);
            PurchaseTreatmentPolicy.ApplyAccountingShape(record);
        }

        return record;
    }

    private static bool RepairRateFromTaxEvidence(PurchaseImportRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.RateKnown && record.Rate != 0m) return false;
        if ((Math.Abs(record.Igst) + Math.Abs(record.Cgst) + Math.Abs(record.Sgst)) <= 0.005m) return false;
        if (!PurchaseImportUtilities.TryDeriveSupportedRate(
                record.TaxableValue, record.Igst, record.Cgst, record.Sgst, out var derivedRate))
            return false;

        record.Rate = derivedRate;
        record.RateKnown = true;
        return true;
    }

    private static string ReadString(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? string.Empty
            : reader.GetString(ordinal);
    }

    private static long ReadInt64(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? 0L
            : reader.GetInt64(ordinal);
    }

    private static decimal ReadDecimal(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? 0m
            : Convert.ToDecimal(
                reader.GetValue(ordinal),
                CultureInfo.InvariantCulture);
    }

    private static decimal? ReadNullableDecimal(
        SqliteDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDecimal(
                reader.GetValue(ordinal),
                CultureInfo.InvariantCulture);
    }

    private static DateTime? ReadDate(
        SqliteDataReader reader,
        string columnName)
    {
        var text = ReadString(reader, columnName);

        return DateTime.TryParseExact(
            text,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? value
            : null;
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, tableName, columnName, columnDefinition);
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }
}
