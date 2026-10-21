using AccountingAutomationHub.Domain.Common;
using System.Globalization;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Imports;

public sealed class SqliteImportActivityRepository :
    IImportActivityRepository
{
    private readonly string _connectionString;

    public SqliteImportActivityRepository()
    {
        ApplicationDataPaths.EnsureFolders();

        _connectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    ApplicationDataPaths.DatabasePath,
                Mode =
                    SqliteOpenMode.ReadWriteCreate,
                Cache =
                    SqliteCacheMode.Private
            }.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteImportActivityRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ImportActivities
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                ImportType      TEXT NOT NULL COLLATE NOCASE,
                PeriodFrom      TEXT NOT NULL DEFAULT '',
                PeriodTo        TEXT NOT NULL DEFAULT '',
                PeriodLabel     TEXT NOT NULL DEFAULT '',
                FilingStatus    TEXT NOT NULL DEFAULT '',
                RecordCount     INTEGER NOT NULL DEFAULT 0,
                BankCount       INTEGER NOT NULL DEFAULT 0,
                SourceSummary   TEXT NOT NULL DEFAULT '',
                Status          TEXT NOT NULL DEFAULT '',
                Details         TEXT NOT NULL DEFAULT '',
                ImportedUtc     TEXT NOT NULL,
                FOREIGN KEY (ClientId)
                    REFERENCES Clients(Id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS
                IX_ImportActivities_ClientTypeDate
            ON ImportActivities
            (
                ClientId,
                ImportType COLLATE NOCASE,
                ImportedUtc DESC
            );
            """;

        command.ExecuteNonQuery();
    }

    public ImportActivity Record(
        ImportActivity activity)
    {
        ArgumentNullException.ThrowIfNull(
            activity);

        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        var importedUtc =
            activity.ImportedUtc == default
                ? AppTime.UtcNow
                : AppTime.NormalizeUtc(activity.ImportedUtc);

        command.CommandText =
            """
            INSERT INTO ImportActivities
            (
                ClientId,
                ImportType,
                PeriodFrom,
                PeriodTo,
                PeriodLabel,
                FilingStatus,
                RecordCount,
                BankCount,
                SourceSummary,
                Status,
                Details,
                ImportedUtc
            )
            VALUES
            (
                $ClientId,
                $ImportType,
                $PeriodFrom,
                $PeriodTo,
                $PeriodLabel,
                $FilingStatus,
                $RecordCount,
                $BankCount,
                $SourceSummary,
                $Status,
                $Details,
                $ImportedUtc
            );

            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            activity.ClientId);

        command.Parameters.AddWithValue(
            "$ImportType",
            activity.ImportType.Trim());

        command.Parameters.AddWithValue(
            "$PeriodFrom",
            FormatDate(
                activity.PeriodFrom));

        command.Parameters.AddWithValue(
            "$PeriodTo",
            FormatDate(
                activity.PeriodTo));

        command.Parameters.AddWithValue(
            "$PeriodLabel",
            activity.PeriodLabel.Trim());

        command.Parameters.AddWithValue(
            "$FilingStatus",
            activity.FilingStatus.Trim());

        command.Parameters.AddWithValue(
            "$RecordCount",
            activity.RecordCount);

        command.Parameters.AddWithValue(
            "$BankCount",
            activity.BankCount);

        command.Parameters.AddWithValue(
            "$SourceSummary",
            activity.SourceSummary.Trim());

        command.Parameters.AddWithValue(
            "$Status",
            activity.Status.Trim());

        command.Parameters.AddWithValue(
            "$Details",
            activity.Details.Trim());

        command.Parameters.AddWithValue(
            "$ImportedUtc",
            importedUtc.ToString("O"));

        activity.Id =
            Convert.ToInt64(
                command.ExecuteScalar(),
                CultureInfo.InvariantCulture);

        activity.ImportedUtc =
            importedUtc;

        return activity;
    }

    public ImportModuleSnapshot GetSnapshot(
        long clientId,
        string importType)
    {
        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                PeriodFrom,
                PeriodTo,
                PeriodLabel,
                FilingStatus,
                RecordCount,
                BankCount,
                Status,
                Details,
                ImportedUtc
            FROM ImportActivities
            WHERE
                ClientId = $ClientId
                AND ImportType = $ImportType
            ORDER BY
                ImportedUtc DESC,
                Id DESC
            LIMIT 1;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$ImportType",
            importType.Trim());

        using var reader =
            command.ExecuteReader();

        if (!reader.Read())
        {
            return new ImportModuleSnapshot
            {
                ImportType =
                    importType
            };
        }

        var periodFrom =
            ParseDate(
                reader.GetString(0));

        var periodTo =
            ParseDate(
                reader.GetString(1));

        var periodLabel =
            reader.GetString(2);

        if (periodLabel.Length == 0)
        {
            periodLabel =
                BuildPeriodLabel(
                    periodFrom,
                    periodTo);
        }

        var filingStatus =
            reader.GetString(3);

        return new ImportModuleSnapshot
        {
            ImportType =
                importType,
            IsImported =
                true,
            Status =
                EmptyToDefault(
                    reader.GetString(6),
                    "Imported"),
            PeriodLabel =
                EmptyToDefault(
                    periodLabel,
                    "Period not available"),
            FilingStatus =
                EmptyToDefault(
                    filingStatus,
                    "Not checked"),
            RecordCount =
                reader.GetInt32(4),
            BankCount =
                reader.GetInt32(5),
            Details =
                reader.GetString(7),
            LastImportedUtc =
                ParseUtc(
                    reader.GetString(8))
        };
    }

    public IReadOnlyList<ImportActivity> GetRecent(
        long clientId,
        int maximumRows = 10)
    {
        var safeMaximum =
            Math.Clamp(
                maximumRows,
                1,
                100);

        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                Id,
                ClientId,
                ImportType,
                PeriodFrom,
                PeriodTo,
                PeriodLabel,
                FilingStatus,
                RecordCount,
                BankCount,
                SourceSummary,
                Status,
                Details,
                ImportedUtc
            FROM ImportActivities
            WHERE ClientId = $ClientId
            ORDER BY
                ImportedUtc DESC,
                Id DESC
            LIMIT $MaximumRows;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$MaximumRows",
            safeMaximum);

        using var reader =
            command.ExecuteReader();

        var rows =
            new List<ImportActivity>();

        while (reader.Read())
        {
            rows.Add(
                new ImportActivity
                {
                    Id =
                        reader.GetInt64(0),
                    ClientId =
                        reader.GetInt64(1),
                    ImportType =
                        reader.GetString(2),
                    PeriodFrom =
                        ParseDate(
                            reader.GetString(3)),
                    PeriodTo =
                        ParseDate(
                            reader.GetString(4)),
                    PeriodLabel =
                        reader.GetString(5),
                    FilingStatus =
                        reader.GetString(6),
                    RecordCount =
                        reader.GetInt32(7),
                    BankCount =
                        reader.GetInt32(8),
                    SourceSummary =
                        reader.GetString(9),
                    Status =
                        reader.GetString(10),
                    Details =
                        reader.GetString(11),
                    ImportedUtc =
                        ParseUtc(
                            reader.GetString(12))
                        ?? DateTime.MinValue
                });
        }

        return rows;
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static string FormatDate(
        DateTime? value) =>
        value?.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture)
        ?? string.Empty;

    private static DateTime? ParseDate(
        string value) =>
        DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.Date
            : null;

    private static DateTime? ParseUtc(
        string value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? AppTime.NormalizeUtc(parsed)
            : null;

    private static string EmptyToDefault(
        string value,
        string fallback) =>
        string.IsNullOrWhiteSpace(
            value)
            ? fallback
            : value;

    private static string BuildPeriodLabel(
        DateTime? periodFrom,
        DateTime? periodTo)
    {
        if (periodFrom is null &&
            periodTo is null)
        {
            return "Period not available";
        }

        if (periodFrom is not null &&
            periodTo is not null)
        {
            if (periodFrom.Value.Year ==
                    periodTo.Value.Year &&
                periodFrom.Value.Month ==
                    periodTo.Value.Month)
            {
                return periodFrom.Value.ToString(
                    "MMM yyyy",
                    CultureInfo.InvariantCulture);
            }

            return
                $"{periodFrom.Value:MMM yyyy} – {periodTo.Value:MMM yyyy}";
        }

        return (periodFrom ?? periodTo)!.Value.ToString(
            "MMM yyyy",
            CultureInfo.InvariantCulture);
    }
}
