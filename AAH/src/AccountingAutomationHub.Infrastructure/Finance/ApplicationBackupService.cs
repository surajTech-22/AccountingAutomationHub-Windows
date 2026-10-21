using System.IO.Compression;
using System.Text.Json;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Finance;

/// <summary>
/// Creates transactionally consistent portable backups of Accounting Automation Hub application data only.
/// External Python runtime files and Python-owned Registry/Cache folders are deliberately excluded.
/// Restore is staged and applied on the next application start before any repository opens SQLite.
/// </summary>
public sealed class ApplicationBackupService
{
    private const string DatabaseEntry = "Data/accounting-hub.db";
    private const string AuditDatabaseEntry = "Data/audit-hub.db";
    private const string ManifestEntry = "manifest.json";
    private static string PendingRestorePath => Path.Combine(ApplicationDataPaths.RootFolder, "pending-restore.aahbackup");
    private static string RestoreBackupRoot => Path.Combine(ApplicationDataPaths.RootFolder, "RestoreBackups");

    public string CreateBackup(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A backup destination is required.", nameof(destinationPath));

        ApplicationDataPaths.EnsureFolders();
        destinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? Environment.CurrentDirectory);

        var tempRoot = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "Backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var snapshot = Path.Combine(tempRoot, "accounting-hub.db");
            CreateDatabaseSnapshot(ApplicationDataPaths.DatabasePath, snapshot);
            var auditSnapshot = Path.Combine(tempRoot, "audit-hub.db");
            var hasAuditDatabase = File.Exists(ApplicationDataPaths.AuditDatabasePath);
            if (hasAuditDatabase) CreateDatabaseSnapshot(ApplicationDataPaths.AuditDatabasePath, auditSnapshot);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);

            using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(snapshot, DatabaseEntry, CompressionLevel.Optimal);
            if (hasAuditDatabase) archive.CreateEntryFromFile(auditSnapshot, AuditDatabaseEntry, CompressionLevel.Optimal);
            var manifest = new
            {
                Product = "Accounting Automation Hub",
                Schema = 3,
                CreatedUtc = DateTime.UtcNow,
                Machine = Environment.MachineName,
                Scope = "ApplicationDataOnly",
                ContainsPdfRegistry = false,
                ContainsAuditDatabase = hasAuditDatabase,
                ContainsExternalPythonRuntime = false
            };
            var entry = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return destinationPath;
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    public string StageRestore(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            throw new FileNotFoundException("The selected backup file was not found.", backupPath);

        using (var archive = ZipFile.OpenRead(backupPath))
        {
            if (archive.GetEntry(DatabaseEntry) is null)
                throw new InvalidDataException("This is not a valid Accounting Automation Hub backup: database snapshot is missing.");
        }

        Directory.CreateDirectory(ApplicationDataPaths.RootFolder);
        File.Copy(backupPath, PendingRestorePath, true);
        return PendingRestorePath;
    }

    public static string ApplyPendingRestoreIfAny()
    {
        if (!File.Exists(PendingRestorePath)) return string.Empty;

        ApplicationDataPaths.EnsureFolders();
        Directory.CreateDirectory(RestoreBackupRoot);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var rollback = Path.Combine(RestoreBackupRoot, $"PreRestore-{stamp}");
        Directory.CreateDirectory(rollback);

        if (File.Exists(ApplicationDataPaths.DatabasePath))
            File.Copy(ApplicationDataPaths.DatabasePath, Path.Combine(rollback, "accounting-hub.db"), true);
        if (File.Exists(ApplicationDataPaths.AuditDatabasePath))
            File.Copy(ApplicationDataPaths.AuditDatabasePath, Path.Combine(rollback, "audit-hub.db"), true);

        var tempRoot = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "Restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var archive = ZipFile.OpenRead(PendingRestorePath);
            var database = archive.GetEntry(DatabaseEntry)
                           ?? throw new InvalidDataException("Backup database entry is missing.");
            var restoredDb = Path.Combine(tempRoot, "accounting-hub.db");
            database.ExtractToFile(restoredDb, true);
            ValidateDatabase(restoredDb);

            var replacement = Path.Combine(ApplicationDataPaths.DataFolder, "accounting-hub.restore.db");
            File.Copy(restoredDb, replacement, true);
            ValidateDatabase(replacement);

            if (File.Exists(ApplicationDataPaths.DatabasePath))
            {
                var replaceRollback = Path.Combine(rollback, "accounting-hub-replace-backup.db");
                File.Replace(replacement, ApplicationDataPaths.DatabasePath, replaceRollback, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(replacement, ApplicationDataPaths.DatabasePath);
            }

            var auditDatabase = archive.GetEntry(AuditDatabaseEntry);
            if (auditDatabase is not null)
            {
                var restoredAuditDb = Path.Combine(tempRoot, "audit-hub.db");
                auditDatabase.ExtractToFile(restoredAuditDb, true);
                ValidateAuditDatabase(restoredAuditDb);
                var auditReplacement = Path.Combine(ApplicationDataPaths.DataFolder, "audit-hub.restore.db");
                File.Copy(restoredAuditDb, auditReplacement, true);
                ValidateAuditDatabase(auditReplacement);
                if (File.Exists(ApplicationDataPaths.AuditDatabasePath))
                {
                    var auditReplaceRollback = Path.Combine(rollback, "audit-hub-replace-backup.db");
                    File.Replace(auditReplacement, ApplicationDataPaths.AuditDatabasePath, auditReplaceRollback, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(auditReplacement, ApplicationDataPaths.AuditDatabasePath);
                }
            }

            // Backups from older Hub versions may contain PdfRegistry/* entries.
            // They are intentionally ignored: external Python-owned files are never restored by the Hub.
            File.Delete(PendingRestorePath);
            return rollback;
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void CreateDatabaseSnapshot(string sourcePath, string snapshotPath)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite backup integrity check failed: {result}");
    }

    private static void ValidateDatabase(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(integrity.ExecuteScalar()) ?? string.Empty;
            if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The backup database failed SQLite integrity validation.");
        }
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Clients';";
            if (Convert.ToInt32(schema.ExecuteScalar()) != 1)
                throw new InvalidDataException("The backup database is not an Accounting Automation Hub database.");
        }
    }
    private static void ValidateAuditDatabase(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(integrity.ExecuteScalar()) ?? string.Empty;
            if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Audit backup database failed SQLite integrity validation.");
        }
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AuditClients';";
            if (Convert.ToInt32(schema.ExecuteScalar()) != 1)
                throw new InvalidDataException("The backup Audit database is not an Accounting Automation Hub Audit database.");
        }
    }

}
