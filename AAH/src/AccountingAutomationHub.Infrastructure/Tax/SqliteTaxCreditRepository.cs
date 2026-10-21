using AccountingAutomationHub.Domain.Tax;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Tax;

public sealed class SqliteTaxCreditRepository
{
    private readonly string _connectionString;

    public SqliteTaxCreditRepository()
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
        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteTaxCreditRepository), _connectionString, InitializeCore);

    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS TaxCreditStatementImports
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                StatementType   TEXT NOT NULL DEFAULT '26AS',
                Pan             TEXT NOT NULL DEFAULT '',
                AssesseeName    TEXT NOT NULL DEFAULT '',
                AssessmentYear  TEXT NOT NULL DEFAULT '',
                DataUpdatedTill TEXT NOT NULL DEFAULT '',
                SourcePath      TEXT NOT NULL DEFAULT '',
                SourceHash      TEXT NOT NULL DEFAULT '',
                ParseNotes      TEXT NOT NULL DEFAULT '',
                RawText         TEXT NOT NULL DEFAULT '',
                IsCurrent       INTEGER NOT NULL DEFAULT 1,
                IsCompleteAuthority INTEGER NOT NULL DEFAULT 1,
                ImportedUtc     TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_TaxCreditStatementImports_ClientFyTypeHash
            ON TaxCreditStatementImports(ClientId, FinancialYear, StatementType, SourceHash);

            CREATE INDEX IF NOT EXISTS IX_TaxCreditStatementImports_Current
            ON TaxCreditStatementImports(ClientId, FinancialYear, StatementType, IsCurrent, ImportedUtc);

            CREATE TABLE IF NOT EXISTS TaxCreditStatementRows
            (
                Id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                ImportId              INTEGER NOT NULL,
                ClientId              INTEGER NOT NULL,
                FinancialYear         TEXT NOT NULL,
                StatementType         TEXT NOT NULL DEFAULT '26AS',
                Part                  TEXT NOT NULL DEFAULT '',
                TaxKind               TEXT NOT NULL DEFAULT 'TDS',
                DeductorName          TEXT NOT NULL DEFAULT '',
                DeductorTan           TEXT NOT NULL DEFAULT '',
                DeductorPan           TEXT NOT NULL DEFAULT '',
                Section               TEXT NOT NULL DEFAULT '',
                TransactionDate       TEXT NOT NULL DEFAULT '',
                BookingStatus         TEXT NOT NULL DEFAULT '',
                BookingDate           TEXT NOT NULL DEFAULT '',
                Remarks               TEXT NOT NULL DEFAULT '',
                AcknowledgementNumber TEXT NOT NULL DEFAULT '',
                CertificateNumber     TEXT NOT NULL DEFAULT '',
                AmountPaidPaise       INTEGER NOT NULL DEFAULT 0,
                TaxDeductedPaise      INTEGER NOT NULL DEFAULT 0,
                TaxDepositedPaise     INTEGER NOT NULL DEFAULT 0,
                RowKey                TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ImportId) REFERENCES TaxCreditStatementImports(Id) ON DELETE CASCADE,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_TaxCreditStatementRows_ImportRowKey
            ON TaxCreditStatementRows(ImportId, RowKey);

            CREATE INDEX IF NOT EXISTS IX_TaxCreditStatementRows_ClientFy
            ON TaxCreditStatementRows(ClientId, FinancialYear, StatementType, Part, TransactionDate);
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "TaxCreditStatementImports", "IsCompleteAuthority", "INTEGER NOT NULL DEFAULT 1");
    }

    public TaxCreditStatementImport? FindByHash(long clientId, string financialYear, string statementType, string sourceHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM TaxCreditStatementImports
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StatementType=$StatementType AND SourceHash=$SourceHash
            ORDER BY ImportedUtc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear);
        command.Parameters.AddWithValue("$StatementType", statementType);
        command.Parameters.AddWithValue("$SourceHash", sourceHash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImport(reader) : null;
    }

    public long ReplaceCurrent(TaxCreditStatementImport import, IReadOnlyCollection<TaxCreditStatementRow> rows)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText =
                """
                UPDATE TaxCreditStatementImports
                SET IsCurrent=0
                WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StatementType=$StatementType AND IsCurrent=1;
                """;
            clear.Parameters.AddWithValue("$ClientId", import.ClientId);
            clear.Parameters.AddWithValue("$FinancialYear", import.FinancialYear);
            clear.Parameters.AddWithValue("$StatementType", import.StatementType);
            clear.ExecuteNonQuery();
        }

        long importId;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO TaxCreditStatementImports
                (ClientId, FinancialYear, StatementType, Pan, AssesseeName, AssessmentYear, DataUpdatedTill,
                 SourcePath, SourceHash, ParseNotes, RawText, IsCurrent, IsCompleteAuthority, ImportedUtc)
                VALUES
                ($ClientId, $FinancialYear, $StatementType, $Pan, $AssesseeName, $AssessmentYear, $DataUpdatedTill,
                 $SourcePath, $SourceHash, $ParseNotes, $RawText, 1, $IsCompleteAuthority, $ImportedUtc);
                SELECT last_insert_rowid();
                """;
            AddImportParameters(insert, import);
            importId = Convert.ToInt64(insert.ExecuteScalar());
        }

        foreach (var row in rows)
        {
            using var insertRow = connection.CreateCommand();
            insertRow.Transaction = transaction;
            insertRow.CommandText =
                """
                INSERT INTO TaxCreditStatementRows
                (ImportId, ClientId, FinancialYear, StatementType, Part, TaxKind, DeductorName, DeductorTan, DeductorPan,
                 Section, TransactionDate, BookingStatus, BookingDate, Remarks, AcknowledgementNumber, CertificateNumber,
                 AmountPaidPaise, TaxDeductedPaise, TaxDepositedPaise, RowKey)
                VALUES
                ($ImportId, $ClientId, $FinancialYear, $StatementType, $Part, $TaxKind, $DeductorName, $DeductorTan, $DeductorPan,
                 $Section, $TransactionDate, $BookingStatus, $BookingDate, $Remarks, $AcknowledgementNumber, $CertificateNumber,
                 $AmountPaidPaise, $TaxDeductedPaise, $TaxDepositedPaise, $RowKey);
                """;
            insertRow.Parameters.AddWithValue("$ImportId", importId);
            insertRow.Parameters.AddWithValue("$ClientId", import.ClientId);
            insertRow.Parameters.AddWithValue("$FinancialYear", import.FinancialYear);
            insertRow.Parameters.AddWithValue("$StatementType", import.StatementType);
            insertRow.Parameters.AddWithValue("$Part", row.Part);
            insertRow.Parameters.AddWithValue("$TaxKind", row.TaxKind);
            insertRow.Parameters.AddWithValue("$DeductorName", row.DeductorName);
            insertRow.Parameters.AddWithValue("$DeductorTan", row.DeductorTan);
            insertRow.Parameters.AddWithValue("$DeductorPan", row.DeductorPan);
            insertRow.Parameters.AddWithValue("$Section", row.Section);
            insertRow.Parameters.AddWithValue("$TransactionDate", FormatDate(row.TransactionDate));
            insertRow.Parameters.AddWithValue("$BookingStatus", row.BookingStatus);
            insertRow.Parameters.AddWithValue("$BookingDate", FormatDate(row.BookingDate));
            insertRow.Parameters.AddWithValue("$Remarks", row.Remarks);
            insertRow.Parameters.AddWithValue("$AcknowledgementNumber", row.AcknowledgementNumber);
            insertRow.Parameters.AddWithValue("$CertificateNumber", row.CertificateNumber);
            insertRow.Parameters.AddWithValue("$AmountPaidPaise", row.AmountPaidPaise);
            insertRow.Parameters.AddWithValue("$TaxDeductedPaise", row.TaxDeductedPaise);
            insertRow.Parameters.AddWithValue("$TaxDepositedPaise", row.TaxDepositedPaise);
            insertRow.Parameters.AddWithValue("$RowKey", row.RowKey);
            insertRow.ExecuteNonQuery();
        }

        transaction.Commit();
        return importId;
    }

    public TaxCreditStatementImport? GetCurrentImport(long clientId, string financialYear, string statementType = "26AS")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM TaxCreditStatementImports
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND StatementType=$StatementType AND IsCurrent=1
            ORDER BY ImportedUtc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear);
        command.Parameters.AddWithValue("$StatementType", statementType);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImport(reader) : null;
    }

    public IReadOnlyList<TaxCreditStatementRow> GetCurrentRows(long clientId, string financialYear, string statementType = "26AS")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.*
            FROM TaxCreditStatementRows r
            INNER JOIN TaxCreditStatementImports i ON i.Id=r.ImportId
            WHERE r.ClientId=$ClientId AND r.FinancialYear=$FinancialYear AND r.StatementType=$StatementType AND i.IsCurrent=1
            ORDER BY COALESCE(NULLIF(r.TransactionDate,''),'9999-12-31'), r.DeductorName, r.Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear);
        command.Parameters.AddWithValue("$StatementType", statementType);
        using var reader = command.ExecuteReader();
        var rows = new List<TaxCreditStatementRow>();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    public bool HasCurrentRows(long clientId, string financialYear) => GetCurrentRows(clientId, financialYear).Count > 0;

    private static void AddImportParameters(SqliteCommand command, TaxCreditStatementImport import)
    {
        command.Parameters.AddWithValue("$ClientId", import.ClientId);
        command.Parameters.AddWithValue("$FinancialYear", import.FinancialYear);
        command.Parameters.AddWithValue("$StatementType", import.StatementType);
        command.Parameters.AddWithValue("$Pan", import.Pan);
        command.Parameters.AddWithValue("$AssesseeName", import.AssesseeName);
        command.Parameters.AddWithValue("$AssessmentYear", import.AssessmentYear);
        command.Parameters.AddWithValue("$DataUpdatedTill", FormatDate(import.DataUpdatedTill));
        command.Parameters.AddWithValue("$SourcePath", import.SourcePath);
        command.Parameters.AddWithValue("$SourceHash", import.SourceHash);
        command.Parameters.AddWithValue("$ParseNotes", import.ParseNotes);
        command.Parameters.AddWithValue("$RawText", import.RawText);
        command.Parameters.AddWithValue("$IsCompleteAuthority", import.IsCompleteAuthority ? 1 : 0);
        command.Parameters.AddWithValue("$ImportedUtc", import.ImportedUtc.ToString("O"));
    }

    private static TaxCreditStatementImport ReadImport(SqliteDataReader reader) => new()
    {
        Id = ReadLong(reader, "Id"),
        ClientId = ReadLong(reader, "ClientId"),
        FinancialYear = ReadString(reader, "FinancialYear"),
        StatementType = ReadString(reader, "StatementType"),
        Pan = ReadString(reader, "Pan"),
        AssesseeName = ReadString(reader, "AssesseeName"),
        AssessmentYear = ReadString(reader, "AssessmentYear"),
        DataUpdatedTill = ParseNullableDate(ReadString(reader, "DataUpdatedTill")),
        SourcePath = ReadString(reader, "SourcePath"),
        SourceHash = ReadString(reader, "SourceHash"),
        ParseNotes = ReadString(reader, "ParseNotes"),
        RawText = ReadString(reader, "RawText"),
        IsCurrent = ReadLong(reader, "IsCurrent") == 1,
        IsCompleteAuthority = ReadLong(reader, "IsCompleteAuthority") == 1,
        ImportedUtc = ParseNullableDate(ReadString(reader, "ImportedUtc")) ?? DateTime.MinValue
    };

    private static TaxCreditStatementRow ReadRow(SqliteDataReader reader) => new()
    {
        Id = ReadLong(reader, "Id"),
        ImportId = ReadLong(reader, "ImportId"),
        ClientId = ReadLong(reader, "ClientId"),
        FinancialYear = ReadString(reader, "FinancialYear"),
        StatementType = ReadString(reader, "StatementType"),
        Part = ReadString(reader, "Part"),
        TaxKind = ReadString(reader, "TaxKind"),
        DeductorName = ReadString(reader, "DeductorName"),
        DeductorTan = ReadString(reader, "DeductorTan"),
        DeductorPan = ReadString(reader, "DeductorPan"),
        Section = ReadString(reader, "Section"),
        TransactionDate = ParseNullableDate(ReadString(reader, "TransactionDate")),
        BookingStatus = ReadString(reader, "BookingStatus"),
        BookingDate = ParseNullableDate(ReadString(reader, "BookingDate")),
        Remarks = ReadString(reader, "Remarks"),
        AcknowledgementNumber = ReadString(reader, "AcknowledgementNumber"),
        CertificateNumber = ReadString(reader, "CertificateNumber"),
        AmountPaidPaise = ReadLong(reader, "AmountPaidPaise"),
        TaxDeductedPaise = ReadLong(reader, "TaxDeductedPaise"),
        TaxDepositedPaise = ReadLong(reader, "TaxDepositedPaise"),
        RowKey = ReadString(reader, "RowKey")
    };

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection() => SqliteConnectionPolicy.Open(_connectionString);
    private static string ReadString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty;
    }
    private static long ReadLong(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0L : Convert.ToInt64(reader.GetValue(ordinal));
    }
    private static DateTime? ParseNullableDate(string value) => DateTime.TryParse(value, out var parsed) ? parsed : null;
    private static string FormatDate(DateTime? value) => value.HasValue ? value.Value.ToString("O") : string.Empty;
}
