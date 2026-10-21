using System.IO;
using System.Text;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Domain.Clients;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class NetworkSnapshotDiagnosticInfo
{
    public DateTime StartedUtc { get; init; }
    public DateTime CompletedUtc { get; set; }
    public int AllowedClientCount { get; init; }
    public string Branch { get; set; } = string.Empty;
    public string LastStage { get; set; } = "Starting";
    public string FailureStage { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string ExceptionType { get; set; } = string.Empty;
    public string ExceptionMessage { get; set; } = string.Empty;
    public string InnerExceptionMessage { get; set; } = string.Empty;
    public string DatabasePath { get; init; } = ApplicationDataPaths.DatabasePath;
    public bool DatabaseExists { get; set; }
    public long DatabaseSize { get; set; }
    public List<string> Stages { get; } = [];

    public string ToDisplayText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Result: {(Success ? "PASS" : "FAIL")}");
        builder.AppendLine($"Branch: {Branch}");
        builder.AppendLine($"Allowed clients: {AllowedClientCount}");
        builder.AppendLine($"Last stage: {LastStage}");
        if (!string.IsNullOrWhiteSpace(FailureStage)) builder.AppendLine($"Failure stage: {FailureStage}");
        builder.AppendLine($"Started: {StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (CompletedUtc != default) builder.AppendLine($"Completed: {CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Authoritative DB: {DatabasePath}");
        builder.AppendLine($"DB exists: {DatabaseExists} · Size: {DatabaseSize:N0} bytes");
        if (!string.IsNullOrWhiteSpace(ExceptionType)) builder.AppendLine($"Exception: {ExceptionType}");
        if (!string.IsNullOrWhiteSpace(ExceptionMessage)) builder.AppendLine($"Message: {ExceptionMessage}");
        if (!string.IsNullOrWhiteSpace(InnerExceptionMessage)) builder.AppendLine($"Inner: {InnerExceptionMessage}");
        builder.AppendLine("Stages:");
        foreach (var stage in Stages) builder.AppendLine("  " + stage);
        return builder.ToString().TrimEnd();
    }

    public string ToLogText()
    {
        var result = Success ? "PASS" : "FAIL";
        var detail = $"{result}; Branch={Branch}; Clients={AllowedClientCount}; LastStage={LastStage}; FailureStage={FailureStage}; DBExists={DatabaseExists}; DBSize={DatabaseSize}";
        if (!string.IsNullOrWhiteSpace(ExceptionMessage)) detail += $"; Error={ExceptionType}: {ExceptionMessage}";
        if (!string.IsNullOrWhiteSpace(InnerExceptionMessage)) detail += $"; Inner={InnerExceptionMessage}";
        return detail;
    }
}

public sealed class NetworkSnapshotBuildException : Exception
{
    public NetworkSnapshotBuildException(NetworkSnapshotDiagnosticInfo diagnostic, Exception innerException)
        : base($"Snapshot failed at stage '{(string.IsNullOrWhiteSpace(diagnostic.FailureStage) ? diagnostic.LastStage : diagnostic.FailureStage)}': {innerException.Message}", innerException)
    {
        Diagnostic = diagnostic;
    }

    public NetworkSnapshotDiagnosticInfo Diagnostic { get; }
}

/// <summary>
/// Produces a disposable, device-scoped read-only working snapshot for the full desktop LAN UI.
/// The snapshot contains only Clients granted to that enrolled device. Main-server security tables,
/// network credentials, package tickets, and local machine paths are removed before download.
/// </summary>
public sealed class NetworkReadOnlySnapshotService
{
    private readonly object _diagnosticSync = new();
    private NetworkSnapshotDiagnosticInfo? _lastDiagnostic;

    public NetworkSnapshotDiagnosticInfo? LastDiagnostic
    {
        get { lock (_diagnosticSync) return _lastDiagnostic; }
    }

    public byte[] Build(IReadOnlyCollection<long> allowedClientIds)
    {
        var ids = allowedClientIds.Distinct().OrderBy(x => x).ToArray();
        var diagnostic = NewDiagnostic(ids.Length);
        try
        {
            Stage(diagnostic, "Resolve client access", $"Resolved {ids.Length:N0} authorised client(s).");

            // Repair 18: a newly enrolled LAN PC must be able to open AAH before Client Directory
            // grants any client. Do not copy, filter or even open the authoritative Main Server
            // accounting database for this zero-client state. Return a standalone valid SQLite
            // bootstrap database with zero application rows instead.
            if (ids.Length == 0)
            {
                diagnostic.Branch = "Zero-client standalone bootstrap";
                Stage(diagnostic, "Create standalone bootstrap", "Authoritative accounting database is intentionally not opened for this branch.");
                var bootstrap = BuildStandaloneBootstrap(diagnostic);
                Complete(diagnostic, true);
                return bootstrap;
            }

            diagnostic.Branch = "Authorised client-filtered snapshot";
            var temp = CreateAahLanTempPath("AAH-LAN-RO", ".db");
            try
            {
                Stage(diagnostic, "Check authoritative database", ApplicationDataPaths.DatabasePath);
                UpdateDatabaseFacts(diagnostic);
                if (!diagnostic.DatabaseExists)
                    throw new FileNotFoundException("The authoritative Main Server accounting database does not exist.", ApplicationDataPaths.DatabasePath);

                Stage(diagnostic, "Copy authoritative database", "SQLite online backup to disposable LAN snapshot.");
                BackupAuthoritativeDatabase(temp, diagnostic);

                Stage(diagnostic, "Open disposable snapshot", temp);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = temp,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                }.ToString()))
                {
                    connection.Open();
                    using (var pragma = connection.CreateCommand())
                    {
                        pragma.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA recursive_triggers=OFF; PRAGMA busy_timeout=8000;";
                        pragma.ExecuteNonQuery();
                    }

                    Stage(diagnostic, "Remove copied triggers", "Audit and period-lock triggers are removed only from the disposable snapshot.");
                    DropAllTriggers(connection);

                    Stage(diagnostic, "Filter unauthorised client rows", $"Keeping Client IDs: {string.Join(",", ids)}");
                    using var transaction = connection.BeginTransaction();
                    var list = string.Join(",", ids);
                    foreach (var table in GetTables(connection))
                    {
                        var columns = GetColumns(connection, table);
                        if (table.Equals("Clients", StringComparison.OrdinalIgnoreCase))
                            Execute(connection, transaction, $"DELETE FROM [Clients] WHERE Id NOT IN ({list});");
                        else if (columns.Contains("ClientId"))
                            Execute(connection, transaction, $"DELETE FROM [{Escape(table)}] WHERE ClientId NOT IN ({list});");
                    }

                    Stage(diagnostic, "Scrub Main Server security tables", "Removing users, credentials, sessions, devices, package registry and server audit data from LAN copy.");
                    foreach (var table in new[]
                    {
                        "AppUsers","RolePermissions","UserClientAccess","LanDevices","DeviceClientAccess","NetworkCredentials",
                        "NetworkSessions","NetworkLoginChallenges","FolderLinks","DistributionTickets","ServerPackageRegistry","ServerSecurityEvents","ServerArchivedFiles"
                    })
                        if (TableExists(connection, table)) Execute(connection, transaction, $"DELETE FROM [{Escape(table)}];");

                    Stage(diagnostic, "Scrub machine-specific settings and paths", "Removing Tally, automation, OCR/Python and local path settings.");
                    if (TableExists(connection, "AppSettings"))
                        Execute(connection, transaction, "DELETE FROM AppSettings WHERE SettingKey LIKE 'Security%' OR SettingKey LIKE 'Tally%' OR SettingKey LIKE 'Automation%' OR SettingKey LIKE 'AutoImport%' OR SettingKey LIKE 'Pdf%' OR SettingKey LIKE 'BankOcr%' OR SettingKey LIKE '%Python%' OR SettingKey LIKE '%OCR%';");
                    if (TableExists(connection, "AutomationFolders"))
                        Execute(connection, transaction, "UPDATE AutomationFolders SET FolderPath='';");
                    if (TableExists(connection, "DocumentVault"))
                    {
                        var c = GetColumns(connection, "DocumentVault");
                        if (c.Contains("OriginalPath")) Execute(connection, transaction, "UPDATE DocumentVault SET OriginalPath='';");
                        if (c.Contains("VaultPath")) Execute(connection, transaction, "UPDATE DocumentVault SET VaultPath='';");
                    }
                    transaction.Commit();

                    Stage(diagnostic, "Compact disposable snapshot", "VACUUM");
                    using var vacuum = connection.CreateCommand();
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();
                }

                Stage(diagnostic, "Read snapshot payload", temp);
                var bytes = ReadSnapshotPayloadWithRetry(temp, diagnostic, "snapshot");
                Stage(diagnostic, "Snapshot ready", $"Payload {bytes.Length:N0} bytes.");
                Complete(diagnostic, true);
                return bytes;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
        catch (NetworkSnapshotBuildException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Fail(diagnostic, exception);
            throw new NetworkSnapshotBuildException(diagnostic, exception);
        }
    }

    public byte[] BuildIdentityOnly(IReadOnlyCollection<ClientProfile> clients)
    {
        var allowed = clients.Where(x => x.Id > 0 && !x.IsArchived).GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.ClientCode).ToArray();
        var diagnostic = NewDiagnostic(allowed.Length);
        diagnostic.Branch = allowed.Length == 0 ? "Zero-client standalone bootstrap" : "Client identity-only bootstrap";
        try
        {
            Stage(diagnostic, "Resolve client access", $"Resolved {allowed.Length:N0} authorised client identity row(s); accounting data is not included.");
            if (allowed.Length == 0)
            {
                var zero = BuildStandaloneBootstrap(diagnostic);
                Complete(diagnostic, true);
                return zero;
            }

            var temp = CreateAahLanTempPath("AAH-LAN-IDENTITY", ".db");
            try
            {
                Stage(diagnostic, "Create client identity bootstrap", temp);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        CREATE TABLE IF NOT EXISTS AAHNetworkBootstrap (Id INTEGER PRIMARY KEY);
                        CREATE TABLE IF NOT EXISTS Clients
                        (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT, ClientCode TEXT NOT NULL COLLATE NOCASE, ClientName TEXT NOT NULL,
                            LegalName TEXT NOT NULL DEFAULT '', TradeName TEXT NOT NULL DEFAULT '', LegalNameIsManual INTEGER NOT NULL DEFAULT 0,
                            TradeNameIsManual INTEGER NOT NULL DEFAULT 0, Gstin TEXT NOT NULL DEFAULT '', Pan TEXT NOT NULL DEFAULT '', State TEXT NOT NULL DEFAULT '',
                            FinancialYear TEXT NOT NULL DEFAULT '', Address TEXT NOT NULL DEFAULT '', Phone TEXT NOT NULL DEFAULT '', Email TEXT NOT NULL DEFAULT '',
                            Notes TEXT NOT NULL DEFAULT '', GstProfileSourceType TEXT NOT NULL DEFAULT '', GstProfileSourcePath TEXT NOT NULL DEFAULT '',
                            LatestGstr3bPeriod TEXT NOT NULL DEFAULT '', LatestGstr3bPeriodEndKey TEXT NOT NULL DEFAULT '', LatestGstr3bArn TEXT NOT NULL DEFAULT '',
                            LatestGstr3bArnDate TEXT NOT NULL DEFAULT '', GstProfileUpdatedUtc TEXT NOT NULL DEFAULT '', IsArchived INTEGER NOT NULL DEFAULT 0,
                            CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL
                        );
                        CREATE UNIQUE INDEX IF NOT EXISTS UX_Clients_ClientCode ON Clients(ClientCode COLLATE NOCASE);
                        """;
                    command.ExecuteNonQuery();

                    foreach (var client in allowed)
                    {
                        using var insert = connection.CreateCommand();
                        insert.CommandText =
                            """
                            INSERT INTO Clients(Id,ClientCode,ClientName,LegalName,TradeName,LegalNameIsManual,TradeNameIsManual,Gstin,Pan,State,FinancialYear,Address,Phone,Email,Notes,GstProfileSourceType,GstProfileSourcePath,LatestGstr3bPeriod,LatestGstr3bPeriodEndKey,LatestGstr3bArn,LatestGstr3bArnDate,GstProfileUpdatedUtc,IsArchived,CreatedUtc,UpdatedUtc)
                            VALUES($Id,$Code,$Name,$Legal,$Trade,$LegalManual,$TradeManual,$Gstin,$Pan,$State,$Fy,'','','','', '', '', '', '', '', '', '',0,$Created,$Updated);
                            """;
                        insert.Parameters.AddWithValue("$Id", client.Id);
                        insert.Parameters.AddWithValue("$Code", client.ClientCode);
                        insert.Parameters.AddWithValue("$Name", client.ClientName);
                        insert.Parameters.AddWithValue("$Legal", client.LegalName ?? string.Empty);
                        insert.Parameters.AddWithValue("$Trade", client.TradeName ?? string.Empty);
                        insert.Parameters.AddWithValue("$LegalManual", client.LegalNameIsManual ? 1 : 0);
                        insert.Parameters.AddWithValue("$TradeManual", client.TradeNameIsManual ? 1 : 0);
                        insert.Parameters.AddWithValue("$Gstin", client.Gstin ?? string.Empty);
                        insert.Parameters.AddWithValue("$Pan", client.Pan ?? string.Empty);
                        insert.Parameters.AddWithValue("$State", client.State ?? string.Empty);
                        insert.Parameters.AddWithValue("$Fy", client.FinancialYear ?? string.Empty);
                        insert.Parameters.AddWithValue("$Created", (client.CreatedUtc == default ? DateTime.UtcNow : client.CreatedUtc).ToString("O"));
                        insert.Parameters.AddWithValue("$Updated", (client.UpdatedUtc == default ? DateTime.UtcNow : client.UpdatedUtc).ToString("O"));
                        insert.ExecuteNonQuery();
                    }
                }
                Stage(diagnostic, "Read client identity payload", temp);
                var bytes = ReadSnapshotPayloadWithRetry(temp, diagnostic, "identity");
                Stage(diagnostic, "Client identity bootstrap ready", $"{allowed.Length:N0} client(s); {bytes.Length:N0} bytes; no accounting rows.");
                Complete(diagnostic, true);
                return bytes;
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
        catch (Exception exception)
        {
            Fail(diagnostic, exception);
            throw new NetworkSnapshotBuildException(diagnostic, exception);
        }
    }

    public NetworkSnapshotDiagnosticInfo Diagnose(IReadOnlyCollection<long> allowedClientIds)
    {
        try
        {
            _ = Build(allowedClientIds);
            return LastDiagnostic ?? NewDiagnostic(allowedClientIds.Count);
        }
        catch (NetworkSnapshotBuildException exception)
        {
            return exception.Diagnostic;
        }
    }

    private byte[] BuildStandaloneBootstrap(NetworkSnapshotDiagnosticInfo diagnostic)
    {
        var temp = CreateAahLanTempPath("AAH-LAN-BOOTSTRAP", ".db");
        try
        {
            Stage(diagnostic, "Open bootstrap SQLite file", temp);
            // Repair 19 Build Repair 4: temporary LAN databases must never enter the
            // Microsoft.Data.Sqlite connection pool. A pooled handle kept the bootstrap
            // file open after Dispose(), so File.ReadAllBytes failed with a Windows
            // sharing violation even though schema creation had completed successfully.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temp,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                connection.Open();
                Stage(diagnostic, "Create bootstrap schema marker", "AAHNetworkBootstrap");
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE IF NOT EXISTS AAHNetworkBootstrap (Id INTEGER PRIMARY KEY); PRAGMA user_version=0;";
                command.ExecuteNonQuery();
            }

            Stage(diagnostic, "Release bootstrap SQLite handle", "Non-pooled temporary SQLite connection disposed before payload read.");
            Stage(diagnostic, "Read bootstrap payload", temp);
            var bytes = ReadSnapshotPayloadWithRetry(temp, diagnostic, "bootstrap");
            if (bytes.Length < 4096)
                throw new InvalidDataException("The standalone LAN bootstrap database was not created correctly.");
            Stage(diagnostic, "Bootstrap ready", $"Payload {bytes.Length:N0} bytes; zero application rows.");
            return bytes;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static byte[] ReadSnapshotPayloadWithRetry(string path, NetworkSnapshotDiagnosticInfo diagnostic, string payloadName)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (attempt > 1)
                    Stage(diagnostic, $"Read {payloadName} payload retry {attempt}/5", path);

                // Read-only payload capture deliberately permits other read/write/delete
                // handles. The temporary SQLite writer has already been disposed and is
                // non-pooled; this sharing mode also prevents antivirus/indexer timing
                // from turning a completed snapshot into a false HTTP 500.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (IOException exception)
            {
                last = exception;
                Stage(diagnostic, $"Read {payloadName} payload attempt {attempt}/5 blocked", $"{exception.GetType().Name}: {exception.Message}");
                if (attempt < 5) System.Threading.Thread.Sleep(75 * attempt);
            }
        }

        throw new IOException($"The completed LAN {payloadName} database could not be read after five attempts.", last);
    }

    private static void BackupAuthoritativeDatabase(string destination, NetworkSnapshotDiagnosticInfo diagnostic)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Stage(diagnostic, $"SQLite backup attempt {attempt}/3", ApplicationDataPaths.DatabasePath);
                if (File.Exists(destination)) File.Delete(destination);
                using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ApplicationDataPaths.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
                using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = destination,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString());
                source.Open(); target.Open();
                using (var sourceBusy = source.CreateCommand()) { sourceBusy.CommandText = "PRAGMA busy_timeout=8000;"; sourceBusy.ExecuteNonQuery(); }
                using (var targetBusy = target.CreateCommand()) { targetBusy.CommandText = "PRAGMA busy_timeout=8000;"; targetBusy.ExecuteNonQuery(); }
                source.BackupDatabase(target);
                Stage(diagnostic, "SQLite backup complete", destination);
                return;
            }
            catch (Exception exception)
            {
                last = exception;
                Stage(diagnostic, $"SQLite backup attempt {attempt}/3 failed", $"{exception.GetType().Name}: {exception.Message}");
                System.Threading.Thread.Sleep(150 * attempt);
            }
        }
        throw new InvalidOperationException("The Main Server accounting database could not be copied for read-only LAN access after three attempts.", last);
    }

    private NetworkSnapshotDiagnosticInfo NewDiagnostic(int clientCount)
    {
        var diagnostic = new NetworkSnapshotDiagnosticInfo
        {
            StartedUtc = DateTime.UtcNow,
            AllowedClientCount = clientCount,
            DatabaseExists = File.Exists(ApplicationDataPaths.DatabasePath),
            DatabaseSize = SafeFileSize(ApplicationDataPaths.DatabasePath)
        };
        SetLastDiagnostic(diagnostic);
        return diagnostic;
    }

    private void Complete(NetworkSnapshotDiagnosticInfo diagnostic, bool success)
    {
        diagnostic.Success = success;
        diagnostic.CompletedUtc = DateTime.UtcNow;
        UpdateDatabaseFacts(diagnostic);
        SetLastDiagnostic(diagnostic);
    }

    private void Fail(NetworkSnapshotDiagnosticInfo diagnostic, Exception exception)
    {
        // Preserve the actual operation that failed. Earlier Repair 19 diagnostics called
        // Stage(..., "FAILED", ...), which overwrote LastStage and hid the useful stage from
        // both the LAN 500 response and the Main Server control panel.
        var failedStage = diagnostic.LastStage;
        diagnostic.Success = false;
        diagnostic.CompletedUtc = DateTime.UtcNow;
        diagnostic.FailureStage = failedStage;
        diagnostic.ExceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        diagnostic.ExceptionMessage = exception.Message;
        diagnostic.InnerExceptionMessage = FlattenInnerExceptions(exception.InnerException);
        UpdateDatabaseFacts(diagnostic);
        diagnostic.Stages.Add($"{DateTime.Now:HH:mm:ss.fff} · FAILED at {failedStage} · {diagnostic.ExceptionType}: {diagnostic.ExceptionMessage}");
        SetLastDiagnostic(diagnostic);
    }

    private void SetLastDiagnostic(NetworkSnapshotDiagnosticInfo diagnostic)
    {
        lock (_diagnosticSync) _lastDiagnostic = diagnostic;
    }

    private static void Stage(NetworkSnapshotDiagnosticInfo diagnostic, string stage, string detail)
    {
        diagnostic.LastStage = stage;
        diagnostic.Stages.Add($"{DateTime.Now:HH:mm:ss.fff} · {stage}" + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" · {detail}"));
    }

    private static void UpdateDatabaseFacts(NetworkSnapshotDiagnosticInfo diagnostic)
    {
        diagnostic.DatabaseExists = File.Exists(diagnostic.DatabasePath);
        diagnostic.DatabaseSize = SafeFileSize(diagnostic.DatabasePath);
    }

    private static long SafeFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static string FlattenInnerExceptions(Exception? exception)
    {
        if (exception is null) return string.Empty;
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" -> ", parts);
    }

    private static string CreateAahLanTempPath(string prefix, string extension)
    {
        var folder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "LanSnapshots");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{prefix}-{Guid.NewGuid():N}{extension}");
    }

    private static void DropAllTriggers(SqliteConnection connection)
    {
        var names = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        foreach (var name in names)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP TRIGGER IF EXISTS [{Escape(name)}];";
            drop.ExecuteNonQuery();
        }
    }

    private static List<string> GetTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        using var reader = cmd.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }

    private static HashSet<string> GetColumns(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = $"PRAGMA table_info([{Escape(table)}]);";
        using var reader = cmd.ExecuteReader(); var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); while (reader.Read()) result.Add(reader.GetString(1)); return result;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$Name LIMIT 1;"; cmd.Parameters.AddWithValue("$Name", table); return cmd.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static string Escape(string value) => value.Replace("]", "]]", StringComparison.Ordinal);
}
