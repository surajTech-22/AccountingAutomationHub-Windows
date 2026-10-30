using System.Globalization;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Imports;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Clients;

public sealed class SqliteClientRepository :
    IClientRepository
{
    private const string CurrentClientSetting =
        "CurrentClientId";

    private readonly string _connectionString;

    public SqliteClientRepository() : this(ApplicationDataPaths.DatabasePath) { }

    // Test-only/diagnostic alternate database target. Production remains on
    // the existing ApplicationDataPaths.DatabasePath by default.
    public SqliteClientRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A SQLite database path is required.", nameof(databasePath));
        ApplicationDataPaths.EnsureFolders();

        var builder =
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    databasePath,
                Mode =
                    SqliteOpenMode.ReadWriteCreate,
                Cache =
                    SqliteCacheMode.Private
            };

        _connectionString =
            builder.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteClientRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS Clients
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientCode      TEXT NOT NULL COLLATE NOCASE,
                ClientName      TEXT NOT NULL,
                LegalName       TEXT NOT NULL DEFAULT '',
                TradeName       TEXT NOT NULL DEFAULT '',
                LegalNameIsManual INTEGER NOT NULL DEFAULT 0,
                TradeNameIsManual INTEGER NOT NULL DEFAULT 0,
                Gstin           TEXT NOT NULL DEFAULT '',
                Pan             TEXT NOT NULL DEFAULT '',
                State           TEXT NOT NULL DEFAULT '',
                FinancialYear   TEXT NOT NULL DEFAULT '',
                Address         TEXT NOT NULL DEFAULT '',
                Phone           TEXT NOT NULL DEFAULT '',
                Email           TEXT NOT NULL DEFAULT '',
                Notes           TEXT NOT NULL DEFAULT '',
                GstProfileSourceType TEXT NOT NULL DEFAULT '',
                GstProfileSourcePath TEXT NOT NULL DEFAULT '',
                LatestGstr3bPeriod TEXT NOT NULL DEFAULT '',
                LatestGstr3bPeriodEndKey TEXT NOT NULL DEFAULT '',
                LatestGstr3bArn TEXT NOT NULL DEFAULT '',
                LatestGstr3bArnDate TEXT NOT NULL DEFAULT '',
                GstProfileUpdatedUtc TEXT NOT NULL DEFAULT '',
                IsArchived      INTEGER NOT NULL DEFAULT 0,
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS
                UX_Clients_ClientCode
            ON Clients(ClientCode COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS
                IX_Clients_ClientName
            ON Clients(ClientName COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS
                IX_Clients_Gstin
            ON Clients(Gstin COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS ClientAdditionalGstins
            (
                ClientId    INTEGER NOT NULL,
                Gstin       TEXT NOT NULL COLLATE NOCASE,
                CreatedUtc  TEXT NOT NULL,
                PRIMARY KEY (ClientId, Gstin),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_ClientAdditionalGstins_Gstin
            ON ClientAdditionalGstins(Gstin COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS AppSettings
            (
                SettingKey      TEXT PRIMARY KEY COLLATE NOCASE,
                SettingValue    TEXT NOT NULL DEFAULT ''
            );
            """;

        command.ExecuteNonQuery();

        var manualFlagAdded = EnsureColumn(
            connection,
            "Clients",
            "LegalNameIsManual",
            "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Clients", "TradeName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Clients", "TradeNameIsManual", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Clients", "GstProfileSourceType", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Clients", "GstProfileSourcePath", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Clients", "LatestGstr3bPeriod", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Clients", "LatestGstr3bPeriodEndKey", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Clients", "LatestGstr3bArn", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Clients", "LatestGstr3bArnDate", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Clients", "GstProfileUpdatedUtc", "TEXT NOT NULL DEFAULT ''");

        if (manualFlagAdded)
        {
            using var protectExisting = connection.CreateCommand();
            protectExisting.CommandText =
                "UPDATE Clients SET LegalNameIsManual = 1 WHERE TRIM(LegalName) <> '';";
            protectExisting.ExecuteNonQuery();
        }
    }


    private const string WorkspaceLastOpenedPrefix = "Workspace.LastOpened.";
    private const string WorkspaceValidationPrefix = "Workspace.CacheValidation.";
    private const string WorkspaceFrozenPrefix = "Workspace.FrozenSnapshot.";

    public DateTime GetWorkspaceLastOpenedUtc(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        return ParseUtc(ReadSetting(connection, WorkspaceSettingKey(WorkspaceLastOpenedPrefix, clientId, financialYear)));
    }

    public void RecordWorkspaceOpened(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        WriteSetting(connection, transaction, WorkspaceSettingKey(WorkspaceLastOpenedPrefix, clientId, financialYear), DateTime.UtcNow.ToString("O"));
        WriteSetting(connection, transaction, WorkspaceSettingKey(WorkspaceValidationPrefix, clientId, financialYear), "1");
        DeleteSetting(connection, transaction, WorkspaceSettingKey(WorkspaceFrozenPrefix, clientId, financialYear));
        transaction.Commit();
    }

    public bool IsWorkspaceCacheValidationActive(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        var value = ReadSetting(connection, WorkspaceSettingKey(WorkspaceValidationPrefix, clientId, financialYear));
        return !value.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    public void SetWorkspaceCacheValidationActive(long clientId, string financialYear, bool active)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        WriteSetting(connection, transaction, WorkspaceSettingKey(WorkspaceValidationPrefix, clientId, financialYear), active ? "1" : "0");
        if (!active)
            WriteSetting(connection, transaction, WorkspaceSettingKey(WorkspaceFrozenPrefix, clientId, financialYear), DateTime.UtcNow.ToString("O"));
        transaction.Commit();
    }

    public DateTime GetWorkspaceFrozenSnapshotUtc(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        return ParseUtc(ReadSetting(connection, WorkspaceSettingKey(WorkspaceFrozenPrefix, clientId, financialYear)));
    }

    private static string WorkspaceSettingKey(string prefix, long clientId, string financialYear) =>
        $"{prefix}{clientId}.{(financialYear ?? string.Empty).Trim().ToUpperInvariant()}";

    private static string ReadSetting(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey = $SettingKey;";
        command.Parameters.AddWithValue("$SettingKey", key);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void WriteSetting(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO AppSettings(SettingKey, SettingValue) VALUES($SettingKey, $SettingValue)
            ON CONFLICT(SettingKey) DO UPDATE SET SettingValue = excluded.SettingValue;
            """;
        command.Parameters.AddWithValue("$SettingKey", key);
        command.Parameters.AddWithValue("$SettingValue", value ?? string.Empty);
        command.ExecuteNonQuery();
    }


    private static void DeleteSetting(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM AppSettings WHERE SettingKey = $SettingKey;";
        command.Parameters.AddWithValue("$SettingKey", key);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ClientProfile> GetAll(
        bool includeArchived)
    {
        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                Id,
                ClientCode,
                ClientName,
                LegalName,
                TradeName,
                LegalNameIsManual,
                TradeNameIsManual,
                Gstin,
                Pan,
                State,
                FinancialYear,
                Address,
                Phone,
                Email,
                Notes,
                GstProfileSourceType,
                GstProfileSourcePath,
                LatestGstr3bPeriod,
                LatestGstr3bPeriodEndKey,
                LatestGstr3bArn,
                LatestGstr3bArnDate,
                GstProfileUpdatedUtc,
                IsArchived,
                CreatedUtc,
                UpdatedUtc
            FROM Clients
            WHERE
                $IncludeArchived = 1
                OR IsArchived = 0
            ORDER BY
                IsArchived,
                ClientCode COLLATE NOCASE,
                ClientName COLLATE NOCASE;
            """;

        command.Parameters.AddWithValue(
            "$IncludeArchived",
            includeArchived
                ? 1
                : 0);

        var currentClientId =
            GetCurrentClientId(
                connection);

        using var reader =
            command.ExecuteReader();

        var clients =
            new List<ClientProfile>();

        while (reader.Read())
        {
            var client =
                ReadClient(reader);

            client.IsCurrent =
                currentClientId.HasValue &&
                client.Id ==
                currentClientId.Value;

            clients.Add(client);
        }

        reader.Close();
        // The Client Directory enumerates every Client/FY. Read GSTIN rows in
        // one ordered query rather than issuing N queries per directory refresh.
        var allRegistrations = ReadAllAdditionalGstins(connection);
        foreach (var client in clients)
            client.AdditionalGstins = allRegistrations.TryGetValue(client.Id, out var values)
                ? values : Array.Empty<string>();
        return clients;
    }

    public ClientProfile? GetById(
        long clientId)
    {
        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                Id,
                ClientCode,
                ClientName,
                LegalName,
                TradeName,
                LegalNameIsManual,
                TradeNameIsManual,
                Gstin,
                Pan,
                State,
                FinancialYear,
                Address,
                Phone,
                Email,
                Notes,
                GstProfileSourceType,
                GstProfileSourcePath,
                LatestGstr3bPeriod,
                LatestGstr3bPeriodEndKey,
                LatestGstr3bArn,
                LatestGstr3bArnDate,
                GstProfileUpdatedUtc,
                IsArchived,
                CreatedUtc,
                UpdatedUtc
            FROM Clients
            WHERE Id = $ClientId;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        using var reader =
            command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        var client =
            ReadClient(reader);

        reader.Close();

        var currentClientId =
            GetCurrentClientId(
                connection);

        client.IsCurrent =
            currentClientId.HasValue &&
            client.Id ==
            currentClientId.Value;

        client.AdditionalGstins = ReadAdditionalGstins(connection, client.Id);
        return client;
    }

    public ClientProfile? GetCurrent()
    {
        using var connection =
            OpenConnection();

        var currentClientId =
            GetCurrentClientId(
                connection);

        if (!currentClientId.HasValue)
        {
            return null;
        }

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                Id,
                ClientCode,
                ClientName,
                LegalName,
                TradeName,
                LegalNameIsManual,
                TradeNameIsManual,
                Gstin,
                Pan,
                State,
                FinancialYear,
                Address,
                Phone,
                Email,
                Notes,
                GstProfileSourceType,
                GstProfileSourcePath,
                LatestGstr3bPeriod,
                LatestGstr3bPeriodEndKey,
                LatestGstr3bArn,
                LatestGstr3bArnDate,
                GstProfileUpdatedUtc,
                IsArchived,
                CreatedUtc,
                UpdatedUtc
            FROM Clients
            WHERE
                Id = $ClientId
                AND IsArchived = 0;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            currentClientId.Value);

        using var reader =
            command.ExecuteReader();

        if (!reader.Read())
        {
            reader.Close();

            ClearCurrentClient(
                connection);

            return null;
        }

        var client =
            ReadClient(reader);

        client.IsCurrent = true;
        reader.Close();
        client.AdditionalGstins = ReadAdditionalGstins(connection, client.Id);
        return client;
    }

    public ClientProfile Save(
        ClientProfile client)
    {
        ArgumentNullException.ThrowIfNull(
            client);

        var normalized =
            Normalize(client);

        // Only explicit client-editor edits replace the registrations.
        // All legacy/background updates preserve the existing secondary list.
        var additional = normalized.AdditionalGstins;
        if (additional is not null)
            ValidateGstinRegistrationSet(normalized, additional);

        using var connection =
            OpenConnection();

        using var transaction =
            connection.BeginTransaction();

        var now =
            DateTime.UtcNow;
        // Within a financial year one GSTIN must have exactly one Client Profile
        // owner. Otherwise the shared source watcher would double-import it.
        if (additional is not null || normalized.Id == 0)
            EnsureUniqueRegistrationOwnership(connection, transaction, normalized, additional);

        var registrationsToInvalidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (normalized.Id == 0 && normalized.Gstin.Length == 15)
            registrationsToInvalidate.Add(normalized.Gstin);

        if (normalized.Id == 0)
        {
            using var insert =
                connection.CreateCommand();

            insert.Transaction =
                transaction;

            insert.CommandText =
                """
                INSERT INTO Clients
                (
                    ClientCode,
                    ClientName,
                    LegalName,
                    TradeName,
                    LegalNameIsManual,
                    TradeNameIsManual,
                    Gstin,
                    Pan,
                    State,
                    FinancialYear,
                    Address,
                    Phone,
                    Email,
                    Notes,
                    GstProfileSourceType,
                    GstProfileSourcePath,
                    LatestGstr3bPeriod,
                    LatestGstr3bPeriodEndKey,
                    LatestGstr3bArn,
                    LatestGstr3bArnDate,
                    GstProfileUpdatedUtc,
                    IsArchived,
                    CreatedUtc,
                    UpdatedUtc
                )
                VALUES
                (
                    $ClientCode,
                    $ClientName,
                    $LegalName,
                    $TradeName,
                    $LegalNameIsManual,
                    $TradeNameIsManual,
                    $Gstin,
                    $Pan,
                    $State,
                    $FinancialYear,
                    $Address,
                    $Phone,
                    $Email,
                    $Notes,
                    $GstProfileSourceType,
                    $GstProfileSourcePath,
                    $LatestGstr3bPeriod,
                    $LatestGstr3bPeriodEndKey,
                    $LatestGstr3bArn,
                    $LatestGstr3bArnDate,
                    $GstProfileUpdatedUtc,
                    $IsArchived,
                    $CreatedUtc,
                    $UpdatedUtc
                );

                SELECT last_insert_rowid();
                """;

            AddClientParameters(
                insert,
                normalized,
                now,
                now);

            normalized.Id =
                Convert.ToInt64(
                    insert.ExecuteScalar(),
                    CultureInfo.InvariantCulture);

            normalized.CreatedUtc =
                now;

            normalized.UpdatedUtc =
                now;
        }
        else
        {
            using var update =
                connection.CreateCommand();

            update.Transaction =
                transaction;

            update.CommandText =
                """
                UPDATE Clients
                SET
                    ClientCode = $ClientCode,
                    ClientName = $ClientName,
                    LegalName = $LegalName,
                    TradeName = $TradeName,
                    LegalNameIsManual = $LegalNameIsManual,
                    TradeNameIsManual = $TradeNameIsManual,
                    Gstin = $Gstin,
                    Pan = $Pan,
                    State = $State,
                    FinancialYear = $FinancialYear,
                    Address = $Address,
                    Phone = $Phone,
                    Email = $Email,
                    Notes = $Notes,
                    GstProfileSourceType = $GstProfileSourceType,
                    GstProfileSourcePath = $GstProfileSourcePath,
                    LatestGstr3bPeriod = $LatestGstr3bPeriod,
                    LatestGstr3bPeriodEndKey = $LatestGstr3bPeriodEndKey,
                    LatestGstr3bArn = $LatestGstr3bArn,
                    LatestGstr3bArnDate = $LatestGstr3bArnDate,
                    GstProfileUpdatedUtc = $GstProfileUpdatedUtc,
                    IsArchived = $IsArchived,
                    UpdatedUtc = $UpdatedUtc
                WHERE Id = $ClientId;
                """;

            AddClientParameters(
                update,
                normalized,
                normalized.CreatedUtc == default
                    ? now
                    : normalized.CreatedUtc,
                now);

            update.Parameters.AddWithValue(
                "$ClientId",
                normalized.Id);

            var affected =
                update.ExecuteNonQuery();

            if (affected == 0)
            {
                throw new InvalidOperationException(
                    "The selected client no longer exists.");
            }

            normalized.UpdatedUtc =
                now;
        }

        if (additional is not null)
        {
            // A Client Profile removal is authoritative for the registration.
            // Delete only records belonging to detached secondary GSTINs, in
            // the same SQLite transaction as the registration list update.
            // Physical GST evidence folders/PDFs are deliberately untouched.
            var previousRegistrations = ReadAdditionalGstins(connection, normalized.Id);
            var detached = previousRegistrations
                .Except(additional, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var removedGstin in detached)
                DeleteDetachedRegistrationData(connection, transaction,
                    normalized.Id, normalized.FinancialYear, removedGstin);

            foreach (var gstin in detached.Concat(
                additional.Except(previousRegistrations, StringComparer.OrdinalIgnoreCase)))
                registrationsToInvalidate.Add(gstin);

            using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText =
                "DELETE FROM ClientAdditionalGstins WHERE ClientId=$ClientId;";
            remove.Parameters.AddWithValue("$ClientId", normalized.Id);
            remove.ExecuteNonQuery();
            foreach (var registration in additional
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var insertGstin = connection.CreateCommand();
                insertGstin.Transaction = transaction;
                insertGstin.CommandText =
                    "INSERT INTO ClientAdditionalGstins (ClientId,Gstin,CreatedUtc) VALUES ($ClientId,$Gstin,$Utc);";
                insertGstin.Parameters.AddWithValue("$ClientId", normalized.Id);
                insertGstin.Parameters.AddWithValue("$Gstin", registration);
                insertGstin.Parameters.AddWithValue("$Utc", now.ToString("O"));
                insertGstin.ExecuteNonQuery();
            }
        }

        transaction.Commit();

        // Invalidation happens only after successful database commit. The
        // cache is shared across clients, so a new owner must not inherit the
        // previous owner's "already imported" flag.
        foreach (var gstin in registrationsToInvalidate)
        {
            SalesPurchaseSourceCache.InvalidateHandledSet("Sales", gstin, normalized.FinancialYear);
            SalesPurchaseSourceCache.InvalidateHandledSet("Purchase", gstin, normalized.FinancialYear);
        }

        normalized.AdditionalGstins = additional ?? ReadAdditionalGstins(connection, normalized.Id);
        return normalized;
    }


    private static void EnsureUniqueRegistrationOwnership(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClientProfile proposed,
        IReadOnlyList<string>? additional)
    {
        foreach (var gstin in new[] { proposed.Gstin }.Concat(additional ?? [])
                     .Where(value => value.Length == 15)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText =
                """
                SELECT 1 FROM Clients AS c
                WHERE c.Id <> $Client AND c.FinancialYear = $FY COLLATE NOCASE
                  AND (c.Gstin = $Gstin COLLATE NOCASE OR EXISTS (
                    SELECT 1 FROM ClientAdditionalGstins AS a
                    WHERE a.ClientId = c.Id AND a.Gstin = $Gstin COLLATE NOCASE))
                LIMIT 1;
                """;
            check.Parameters.AddWithValue("$Client", proposed.Id);
            check.Parameters.AddWithValue("$FY", proposed.FinancialYear);
            check.Parameters.AddWithValue("$Gstin", gstin);
            if (check.ExecuteScalar() is not null)
                throw new InvalidOperationException(
                    $"GSTIN {gstin} is already registered to a different client for FY {proposed.FinancialYear}. Remove it from the original Client Profile before creating the separate client.");
        }
    }

    // Delete only registration-owned rows. No calls to File.Delete/Directory.Delete:
    // GSTR-3B PDFs remain in the original GST/<GSTIN>/GSTR-3B directory
    // until a new client with the exact GSTIN gets a linked Automation Folder.
    private static void DeleteDetachedRegistrationData(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string removedGstin)
    {
        var tables = new[]
        {
            ("SalesRecords", "ClientGstin"),
            ("PurchaseRecords", "ClientGstin"),
            ("SalesImportBatches", "ClientGstin"),
            ("PurchaseImportBatches", "ClientGstin"),
            ("Gstr3bReturns", "Gstin"),
            ("GstLedgerPeriods", "Gstin"),
            ("GstPortalSourceState", "Gstin"),
            ("GstBootstrapState", "Gstin")
        };
        foreach (var (table, gstinColumn) in tables)
        {
            using var inspect = connection.CreateCommand();
            inspect.Transaction = transaction;
            inspect.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$Table;";
            inspect.Parameters.AddWithValue("$Table", table);
            if (inspect.ExecuteScalar() is null) continue;

            var hasGstinColumn = false;
            using (var columns = connection.CreateCommand())
            {
                columns.Transaction = transaction;
                columns.CommandText = $"PRAGMA table_info({table});";
                using var reader = columns.ExecuteReader();
                while (reader.Read())
                    if (reader.GetString(1).Equals(gstinColumn, StringComparison.OrdinalIgnoreCase))
                        hasGstinColumn = true;
            }
            if (!hasGstinColumn) continue;
            using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText =
                $"DELETE FROM {table} WHERE ClientId=$Client AND FinancialYear=$FY AND {gstinColumn}=$Gstin COLLATE NOCASE;";
            remove.Parameters.AddWithValue("$Client", clientId);
            remove.Parameters.AddWithValue("$FY", financialYear);
            remove.Parameters.AddWithValue("$Gstin", removedGstin);
            remove.ExecuteNonQuery();
        }
    }

    public ClientProfile ApplyImportedGstProfile(
        long clientId,
        GstinProfileLookupResult profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var client = GetById(clientId)
            ?? throw new InvalidOperationException(
                "The selected client no longer exists.");

        var changed = false;

        var preferredClientName = AccountingText.DisplayName(
            profile.TradeName.Length > 0 ? profile.TradeName : profile.LegalName);
        if (GstClientNameRecognitionPolicy.HasSourceBusinessName(preferredClientName, client.Gstin) &&
            GstClientNameRecognitionPolicy.IsUnresolvedName(client.ClientName, client.Gstin))
        {
            client.ClientName = preferredClientName;
            changed = true;
        }

        // Legacy saves could mark a raw GSTIN fallback as a manual name.
        // That is not a meaningful manual business name; verified evidence
        // may repair it while genuine user-entered names stay protected.
        if ((!client.LegalNameIsManual ||
             GstClientNameRecognitionPolicy.IsUnresolvedName(client.LegalName, client.Gstin)) &&
            GstClientNameRecognitionPolicy.HasSourceBusinessName(profile.LegalName, client.Gstin) &&
            (!client.LegalName.Equals(profile.LegalName, StringComparison.Ordinal) ||
             client.LegalNameIsManual))
        {
            client.LegalName = profile.LegalName;
            client.LegalNameIsManual = false;
            changed = true;
        }

        if ((!client.TradeNameIsManual ||
             GstClientNameRecognitionPolicy.IsUnresolvedName(client.TradeName, client.Gstin)) &&
            GstClientNameRecognitionPolicy.HasSourceBusinessName(profile.TradeName, client.Gstin) &&
            (!client.TradeName.Equals(profile.TradeName, StringComparison.Ordinal) ||
             client.TradeNameIsManual))
        {
            client.TradeName = profile.TradeName;
            client.TradeNameIsManual = false;
            changed = true;
        }

        if (profile.Pan.Length > 0 && client.Pan.Length == 0)
        {
            client.Pan = profile.Pan;
            changed = true;
        }

        if (profile.State.Length > 0 && client.State.Length == 0)
        {
            client.State = profile.State;
            changed = true;
        }

        if (profile.SourcePath.Length > 0)
        {
            changed |= !client.GstProfileSourceType.Equals(profile.SourceType, StringComparison.Ordinal);
            changed |= !client.GstProfileSourcePath.Equals(profile.SourcePath, StringComparison.Ordinal);
            changed |= !client.LatestGstr3bPeriod.Equals(profile.Gstr3bPeriod, StringComparison.Ordinal);
            changed |= !client.LatestGstr3bPeriodEndKey.Equals(profile.PeriodEndKey, StringComparison.Ordinal);
            changed |= !client.LatestGstr3bArn.Equals(profile.Arn, StringComparison.Ordinal);
            changed |= !client.LatestGstr3bArnDate.Equals(profile.ArnDate, StringComparison.Ordinal);

            client.GstProfileSourceType = profile.SourceType;
            client.GstProfileSourcePath = profile.SourcePath;
            client.LatestGstr3bPeriod = profile.Gstr3bPeriod;
            client.LatestGstr3bPeriodEndKey = profile.PeriodEndKey;
            client.LatestGstr3bArn = profile.Arn;
            client.LatestGstr3bArnDate = profile.ArnDate;
            client.GstProfileUpdatedUtc = DateTime.UtcNow;
        }

        if (!changed) return client;
        // GST profile refresh can update the primary name/state but must not
        // replace or reorder secondary GSTINs saved by the Client Editor.
        client.AdditionalGstins = null;
        return Save(client);
    }

    public void SetCurrent(
        long clientId)
    {
        using var connection =
            OpenConnection();

        using var transaction =
            connection.BeginTransaction();

        using var verify =
            connection.CreateCommand();

        verify.Transaction =
            transaction;

        verify.CommandText =
            """
            SELECT COUNT(*)
            FROM Clients
            WHERE
                Id = $ClientId
                AND IsArchived = 0;
            """;

        verify.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        var exists =
            Convert.ToInt32(
                verify.ExecuteScalar(),
                CultureInfo.InvariantCulture) >
            0;

        if (!exists)
        {
            throw new InvalidOperationException(
                "Only a saved, non-archived client can be selected.");
        }

        using var setting =
            connection.CreateCommand();

        setting.Transaction =
            transaction;

        setting.CommandText =
            """
            INSERT INTO AppSettings
            (
                SettingKey,
                SettingValue
            )
            VALUES
            (
                $SettingKey,
                $SettingValue
            )
            ON CONFLICT(SettingKey)
            DO UPDATE SET
                SettingValue = excluded.SettingValue;
            """;

        setting.Parameters.AddWithValue(
            "$SettingKey",
            CurrentClientSetting);

        setting.Parameters.AddWithValue(
            "$SettingValue",
            clientId.ToString(
                CultureInfo.InvariantCulture));

        setting.ExecuteNonQuery();

        transaction.Commit();
    }

    public void ClearCurrent()
    {
        using var connection =
            OpenConnection();

        ClearCurrentClient(connection);
    }

    public void Archive(
        long clientId)
    {
        using var connection =
            OpenConnection();

        using var transaction =
            connection.BeginTransaction();

        using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            UPDATE Clients
            SET
                IsArchived = 1,
                UpdatedUtc = $UpdatedUtc
            WHERE Id = $ClientId;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$UpdatedUtc",
            DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();

        if (GetCurrentClientId(
                connection,
                transaction) ==
            clientId)
        {
            ClearCurrentClient(
                connection,
                transaction);
        }

        transaction.Commit();
    }

    public void Restore(
        long clientId)
    {
        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            UPDATE Clients
            SET
                IsArchived = 0,
                UpdatedUtc = $UpdatedUtc
            WHERE Id = $ClientId;
            """;

        command.Parameters.AddWithValue(
            "$ClientId",
            clientId);

        command.Parameters.AddWithValue(
            "$UpdatedUtc",
            DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();
    }

    public void Delete(
        long clientId)
    {
        if (clientId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientId));
        }

        using var connection = OpenConnection();

        using (var verify = connection.CreateCommand())
        {
            verify.CommandText =
                "SELECT IsArchived FROM Clients WHERE Id = $ClientId;";
            verify.Parameters.AddWithValue("$ClientId", clientId);

            var value = verify.ExecuteScalar();
            if (value is null || value is DBNull)
            {
                throw new InvalidOperationException("The selected client no longer exists.");
            }

            if (Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0)
            {
                throw new InvalidOperationException("Archive the client before permanent deletion.");
            }
        }

        // R39: keep SQLite foreign keys enabled so the existing ON DELETE CASCADE
        // contracts remove indirect child rows (for example JournalLines and mapping members).
        // Only legacy/client-owned tables that expose ClientId without an ON DELETE CASCADE
        // link to Clients are cleared explicitly before the archived Client/FY row is deleted.
        using var transaction = connection.BeginTransaction();
        var directCleanupTables = new List<string>();

        using (var list = connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                var table = reader.GetString(0);
                if (table.Equals("Clients", StringComparison.OrdinalIgnoreCase) ||
                    table.Equals("AppSettings", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                directCleanupTables.Add(table);
            }
        }

        foreach (var table in directCleanupTables)
        {
            var quoted = '"' + table.Replace("\"", "\"\"") + '"';
            var ownsClient = false;

            using (var columns = connection.CreateCommand())
            {
                columns.Transaction = transaction;
                columns.CommandText = $"PRAGMA table_info({quoted});";

                using var reader = columns.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetString(1).Equals("ClientId", StringComparison.OrdinalIgnoreCase))
                    {
                        ownsClient = true;
                        break;
                    }
                }
            }

            if (!ownsClient)
            {
                continue;
            }

            var cascadesFromClient = false;
            using (var foreignKeys = connection.CreateCommand())
            {
                foreignKeys.Transaction = transaction;
                foreignKeys.CommandText = $"PRAGMA foreign_key_list({quoted});";

                using var reader = foreignKeys.ExecuteReader();
                while (reader.Read())
                {
                    var targetTable = reader.GetString(2);
                    var sourceColumn = reader.GetString(3);
                    var targetColumn = reader.GetString(4);
                    var onDelete = reader.GetString(6);
                    if (targetTable.Equals("Clients", StringComparison.OrdinalIgnoreCase) &&
                        sourceColumn.Equals("ClientId", StringComparison.OrdinalIgnoreCase) &&
                        targetColumn.Equals("Id", StringComparison.OrdinalIgnoreCase) &&
                        onDelete.Equals("CASCADE", StringComparison.OrdinalIgnoreCase))
                    {
                        cascadesFromClient = true;
                        break;
                    }
                }
            }

            if (cascadesFromClient)
            {
                continue;
            }

            using var deleteRows = connection.CreateCommand();
            deleteRows.Transaction = transaction;
            deleteRows.CommandText = $"DELETE FROM {quoted} WHERE ClientId = $ClientId;";
            deleteRows.Parameters.AddWithValue("$ClientId", clientId);
            deleteRows.ExecuteNonQuery();
        }

        using (var deleteWorkspaceSettings = connection.CreateCommand())
        {
            deleteWorkspaceSettings.Transaction = transaction;
            deleteWorkspaceSettings.CommandText =
                "DELETE FROM AppSettings WHERE SettingKey LIKE $WorkspacePattern;";
            deleteWorkspaceSettings.Parameters.AddWithValue(
                "$WorkspacePattern",
                $"Workspace.%.{clientId}.%");
            deleteWorkspaceSettings.ExecuteNonQuery();
        }

        using (var clearCurrent = connection.CreateCommand())
        {
            clearCurrent.Transaction = transaction;
            clearCurrent.CommandText =
                "DELETE FROM AppSettings WHERE SettingKey = $Key AND SettingValue = $ClientIdText;";
            clearCurrent.Parameters.AddWithValue("$Key", CurrentClientSetting);
            clearCurrent.Parameters.AddWithValue(
                "$ClientIdText",
                clientId.ToString(CultureInfo.InvariantCulture));
            clearCurrent.ExecuteNonQuery();
        }

        using (var deleteClient = connection.CreateCommand())
        {
            deleteClient.Transaction = transaction;
            deleteClient.CommandText =
                "DELETE FROM Clients WHERE Id = $ClientId AND IsArchived = 1;";
            deleteClient.Parameters.AddWithValue("$ClientId", clientId);

            if (deleteClient.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Client deletion did not remove exactly one archived Client/FY record.");
            }
        }

        transaction.Commit();
    }

    public bool ClientCodeExists(
        string clientCode,
        long excludingClientId = 0)
    {
        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM Clients
            WHERE
                ClientCode = $ClientCode
                AND Id <> $ExcludingClientId;
            """;

        command.Parameters.AddWithValue(
            "$ClientCode",
            clientCode.Trim());

        command.Parameters.AddWithValue(
            "$ExcludingClientId",
            excludingClientId);

        return Convert.ToInt32(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) >
            0;
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static Dictionary<long, string[]> ReadAllAdditionalGstins(SqliteConnection connection)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT ClientId, Gstin FROM ClientAdditionalGstins ORDER BY ClientId, Gstin;";
        using var reader = read.ExecuteReader();
        var values = new Dictionary<long, List<string>>();
        while (reader.Read())
        {
            var clientId = reader.GetInt64(0);
            if (!values.TryGetValue(clientId, out var list))
            {
                list = [];
                values.Add(clientId, list);
            }
            list.Add(reader.GetString(1));
        }
        return values.ToDictionary(item => item.Key, item => item.Value.ToArray());
    }

    private static string[] ReadAdditionalGstins(SqliteConnection connection, long clientId)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Gstin FROM ClientAdditionalGstins WHERE ClientId=$ClientId ORDER BY Gstin;";
        read.Parameters.AddWithValue("$ClientId", clientId);
        using var reader = read.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static void ValidateGstinRegistrationSet(ClientProfile client, IReadOnlyList<string> registrations)
    {
        if (registrations.Count > 100)
            throw new InvalidOperationException("A client cannot have more than 100 additional GSTIN registrations.");
        var primary = client.Gstin;
        if (registrations.Count > 0 && primary.Length != 15)
            throw new InvalidOperationException("Save a primary GSTIN before adding secondary registrations.");
        var expectedPan = primary.Length == 15 ? primary.Substring(2, 10) : client.Pan;
        if (client.Pan.Length > 0 && expectedPan.Length > 0 &&
            !client.Pan.Equals(expectedPan, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Primary GSTIN and client PAN do not match.");
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in registrations)
        {
            if (value.Length != 15 || !char.IsDigit(value[0]) || !char.IsDigit(value[1]) ||
                !value.Skip(2).All(char.IsLetterOrDigit))
                throw new InvalidOperationException("Every additional GSTIN must contain 15 alphanumeric characters starting with a State code.");
            if (!known.Add(value) || value.Equals(primary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The same GSTIN cannot be registered more than once for a client.");
            if (expectedPan.Length == 10 && !value.Substring(2, 10).Equals(expectedPan, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("All GSTINs for one client must belong to the same PAN.");
        }
    }

    private static ClientProfile Normalize(
        ClientProfile source) =>
        new()
        {
            Id = source.Id,
            ClientCode =
                source.ClientCode
                    .Trim()
                    .ToUpperInvariant(),
            ClientName =
                source.ClientName.Trim(),
            LegalName =
                source.LegalName.Trim(),
            TradeName =
                source.TradeName.Trim(),
            LegalNameIsManual =
                source.LegalNameIsManual,
            TradeNameIsManual =
                source.TradeNameIsManual,
            Gstin =
                string.Concat(
                        source.Gstin.Where(
                            char.IsLetterOrDigit))
                    .ToUpperInvariant(),
            AdditionalGstins = source.AdditionalGstins?.Select(value =>
                    string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit))
                        .ToUpperInvariant())
                .ToArray(),
            Pan =
                string.Concat(
                        source.Pan.Where(
                            char.IsLetterOrDigit))
                    .ToUpperInvariant(),
            State =
                source.State.Trim(),
            FinancialYear =
                source.FinancialYear.Trim(),
            Address =
                source.Address.Trim(),
            Phone =
                source.Phone.Trim(),
            Email =
                source.Email
                    .Trim()
                    .ToLowerInvariant(),
            Notes =
                source.Notes.Trim(),
            GstProfileSourceType =
                source.GstProfileSourceType.Trim(),
            GstProfileSourcePath =
                source.GstProfileSourcePath.Trim(),
            LatestGstr3bPeriod =
                source.LatestGstr3bPeriod.Trim(),
            LatestGstr3bPeriodEndKey =
                source.LatestGstr3bPeriodEndKey.Trim(),
            LatestGstr3bArn =
                source.LatestGstr3bArn.Trim(),
            LatestGstr3bArnDate =
                source.LatestGstr3bArnDate.Trim(),
            GstProfileUpdatedUtc =
                source.GstProfileUpdatedUtc,
            IsArchived =
                source.IsArchived,
            CreatedUtc =
                source.CreatedUtc,
            UpdatedUtc =
                source.UpdatedUtc
        };

    private static void AddClientParameters(
        SqliteCommand command,
        ClientProfile client,
        DateTime createdUtc,
        DateTime updatedUtc)
    {
        command.Parameters.AddWithValue(
            "$ClientCode",
            client.ClientCode);

        command.Parameters.AddWithValue(
            "$ClientName",
            client.ClientName);

        command.Parameters.AddWithValue(
            "$LegalName",
            client.LegalName);

        command.Parameters.AddWithValue(
            "$TradeName",
            client.TradeName);

        command.Parameters.AddWithValue(
            "$LegalNameIsManual",
            client.LegalNameIsManual ? 1 : 0);

        command.Parameters.AddWithValue(
            "$TradeNameIsManual",
            client.TradeNameIsManual ? 1 : 0);

        command.Parameters.AddWithValue(
            "$Gstin",
            client.Gstin);

        command.Parameters.AddWithValue(
            "$Pan",
            client.Pan);

        command.Parameters.AddWithValue(
            "$State",
            client.State);

        command.Parameters.AddWithValue(
            "$FinancialYear",
            client.FinancialYear);

        command.Parameters.AddWithValue(
            "$Address",
            client.Address);

        command.Parameters.AddWithValue(
            "$Phone",
            client.Phone);

        command.Parameters.AddWithValue(
            "$Email",
            client.Email);

        command.Parameters.AddWithValue(
            "$Notes",
            client.Notes);

        command.Parameters.AddWithValue(
            "$GstProfileSourceType",
            client.GstProfileSourceType);

        command.Parameters.AddWithValue(
            "$GstProfileSourcePath",
            client.GstProfileSourcePath);

        command.Parameters.AddWithValue(
            "$LatestGstr3bPeriod",
            client.LatestGstr3bPeriod);

        command.Parameters.AddWithValue(
            "$LatestGstr3bPeriodEndKey",
            client.LatestGstr3bPeriodEndKey);

        command.Parameters.AddWithValue(
            "$LatestGstr3bArn",
            client.LatestGstr3bArn);

        command.Parameters.AddWithValue(
            "$LatestGstr3bArnDate",
            client.LatestGstr3bArnDate);

        command.Parameters.AddWithValue(
            "$GstProfileUpdatedUtc",
            client.GstProfileUpdatedUtc == DateTime.MinValue
                ? string.Empty
                : client.GstProfileUpdatedUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$IsArchived",
            client.IsArchived
                ? 1
                : 0);

        command.Parameters.AddWithValue(
            "$CreatedUtc",
            createdUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$UpdatedUtc",
            updatedUtc.ToString("O"));
    }

    private static ClientProfile ReadClient(
        SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            ClientCode = reader.GetString(1),
            ClientName = reader.GetString(2),
            LegalName = reader.GetString(3),
            TradeName = reader.GetString(4),
            LegalNameIsManual = reader.GetInt64(5) != 0,
            TradeNameIsManual = reader.GetInt64(6) != 0,
            Gstin = reader.GetString(7),
            Pan = reader.GetString(8),
            State = reader.GetString(9),
            FinancialYear = reader.GetString(10),
            Address = reader.GetString(11),
            Phone = reader.GetString(12),
            Email = reader.GetString(13),
            Notes = reader.GetString(14),
            GstProfileSourceType = reader.GetString(15),
            GstProfileSourcePath = reader.GetString(16),
            LatestGstr3bPeriod = reader.GetString(17),
            LatestGstr3bPeriodEndKey = reader.GetString(18),
            LatestGstr3bArn = reader.GetString(19),
            LatestGstr3bArnDate = reader.GetString(20),
            GstProfileUpdatedUtc = ParseUtc(reader.GetString(21)),
            IsArchived = reader.GetInt64(22) != 0,
            CreatedUtc = ParseUtc(reader.GetString(23)),
            UpdatedUtc = ParseUtc(reader.GetString(24))
        };

    private static DateTime ParseUtc(
        string value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var result)
            ? result
            : DateTime.MinValue;

    private static bool EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = inspect.ExecuteReader();

        while (reader.Read())
        {
            if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        reader.Close();

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
        return true;
    }

    private static long? GetCurrentClientId(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            SELECT SettingValue
            FROM AppSettings
            WHERE SettingKey = $SettingKey;
            """;

        command.Parameters.AddWithValue(
            "$SettingKey",
            CurrentClientSetting);

        var value =
            Convert.ToString(
                command.ExecuteScalar(),
                CultureInfo.InvariantCulture);

        return long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var clientId)
            ? clientId
            : null;
    }

    private static void ClearCurrentClient(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            DELETE FROM AppSettings
            WHERE SettingKey = $SettingKey;
            """;

        command.Parameters.AddWithValue(
            "$SettingKey",
            CurrentClientSetting);

        command.ExecuteNonQuery();
    }
}
