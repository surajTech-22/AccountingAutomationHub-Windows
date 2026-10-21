using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Governance;

public sealed class VerifiedBackupService(SqliteGovernanceRepository repository) : IDisposable
{
    // Backup scheduling can be reached by more than one UI/service lifetime. The gate must therefore
    // be process-wide, otherwise multiple 20-second startup timers can all create the same Daily copy.
    private static readonly SemaphoreSlim GlobalGate = new(1, 1);
    private static readonly Regex BackupNamePattern = new(
        @"^AAH-(?<class>Daily|Weekly|Monthly|Manual)-(?<stamp>\d{8}-\d{6})(?:-\d{2})?\.(?<ext>db|aahbackup)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, int> RetentionCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Daily"] = 7,
            ["Weekly"] = 4,
            ["Monthly"] = 12,
            ["Manual"] = 5
        };

    private Timer? _timer;

    public void Start()
    {
        _timer ??= new Timer(_ => _ = RunScheduledBackupAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));
    }

    public async Task<VerifiedBackupRecord?> RunScheduledBackupAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ApplicationDataPaths.DatabasePath)) return null;
        await GlobalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(ApplicationDataPaths.VerifiedBackupFolder);

            // Filesystem retention is authoritative. Older releases relied only on rows in the
            // VerifiedBackups table; orphaned physical .db copies could therefore survive forever
            // and were observed consuming tens of GB on C:.
            ApplyRetention();

            var now = DateTime.Now;
            var scheduleClass = DetermineClass(now);
            if (scheduleClass.Length == 0)
                return repository.GetBackups(250).FirstOrDefault(item => File.Exists(item.BackupPath));

            var result = CreatePortableVerifiedBackup(scheduleClass, now);
            repository.SaveBackupRecord(result);
            if (!result.IsVerified)
            {
                try { File.Delete(result.BackupPath); } catch { }
                return result;
            }
            ApplyRetention();
            return result;
        }
        finally
        {
            GlobalGate.Release();
        }
    }

    public VerifiedBackupRecord CreateVerifiedBackup(string scheduleClass = "Manual")
    {
        GlobalGate.Wait();
        try
        {
            scheduleClass = NormalizeScheduleClass(scheduleClass);
            Directory.CreateDirectory(ApplicationDataPaths.VerifiedBackupFolder);
            ApplyRetention();

            var result = CreatePortableVerifiedBackup(scheduleClass, DateTime.Now);
            repository.SaveBackupRecord(result);
            if (!result.IsVerified)
            {
                try { File.Delete(result.BackupPath); } catch { }
                return result;
            }
            ApplyRetention();
            return result;
        }
        finally
        {
            GlobalGate.Release();
        }
    }

    private static string DetermineClass(DateTime now)
    {
        var physical = EnumeratePhysicalBackups();
        if (!physical.Any(item => item.ScheduleClass.Equals("Daily", StringComparison.OrdinalIgnoreCase) && item.CreatedLocal.Date == now.Date))
            return "Daily";

        var week = ISOWeek.GetWeekOfYear(now);
        var weekYear = ISOWeek.GetYear(now);
        if (!physical.Any(item => item.ScheduleClass.Equals("Weekly", StringComparison.OrdinalIgnoreCase) && ISOWeek.GetYear(item.CreatedLocal) == weekYear && ISOWeek.GetWeekOfYear(item.CreatedLocal) == week))
            return "Weekly";

        if (!physical.Any(item => item.ScheduleClass.Equals("Monthly", StringComparison.OrdinalIgnoreCase) && item.CreatedLocal.Year == now.Year && item.CreatedLocal.Month == now.Month))
            return "Monthly";

        return string.Empty;
    }

    private static VerifiedBackupRecord CreatePortableVerifiedBackup(string scheduleClass, DateTime now)
    {
        var target = BuildUniqueBackupPath(scheduleClass, now);
        var backupService = new ApplicationBackupService();
        backupService.CreateBackup(target);
        return VerifyPortableBackup(target, scheduleClass);
    }

    private static string BuildUniqueBackupPath(string scheduleClass, DateTime now)
    {
        var folder = ApplicationDataPaths.VerifiedBackupFolder;
        var stem = $"AAH-{scheduleClass}-{now:yyyyMMdd-HHmmss}";
        var target = Path.Combine(folder, stem + ".aahbackup");
        if (!File.Exists(target)) return target;

        for (var index = 1; index <= 99; index++)
        {
            target = Path.Combine(folder, $"{stem}-{index:00}.aahbackup");
            if (!File.Exists(target)) return target;
        }

        throw new IOException("Could not allocate a unique verified-backup filename.");
    }

    private static VerifiedBackupRecord VerifyPortableBackup(string path, string scheduleClass)
    {
        string hash;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        using (var sha256 = SHA256.Create())
            hash = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();

        var integrity = "unknown";
        var tempRoot = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "VerifiedBackupCheck", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var database = archive.GetEntry("Data/accounting-hub.db")
                           ?? throw new InvalidDataException("Verified backup database entry is missing.");
            var extracted = Path.Combine(tempRoot, "accounting-hub.db");
            database.ExtractToFile(extracted, true);

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = extracted,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                integrity = Convert.ToString(command.ExecuteScalar()) ?? "unknown";
            }

            var auditEntry = archive.GetEntry("Data/audit-hub.db");
            if (auditEntry is not null && integrity.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                var auditExtracted = Path.Combine(tempRoot, "audit-hub.db");
                auditEntry.ExtractToFile(auditExtracted, true);
                using var auditConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = auditExtracted,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString());
                auditConnection.Open();
                using var auditCommand = auditConnection.CreateCommand();
                auditCommand.CommandText = "PRAGMA integrity_check;";
                var auditIntegrity = Convert.ToString(auditCommand.ExecuteScalar()) ?? "unknown";
                if (!auditIntegrity.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    integrity = "AUDIT FAILED: " + auditIntegrity;
            }
        }
        catch (Exception exception)
        {
            integrity = "FAILED: " + exception.Message;
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }

        var ok = integrity.Equals("ok", StringComparison.OrdinalIgnoreCase);
        var info = new FileInfo(path);
        return new VerifiedBackupRecord
        {
            BackupPath = path,
            Sha256 = hash,
            FileSize = info.Length,
            IntegrityResult = integrity,
            ScheduleClass = scheduleClass,
            CreatedUtc = info.LastWriteTimeUtc == default ? DateTime.UtcNow : info.LastWriteTimeUtc,
            VerifiedUtc = DateTime.UtcNow,
            IsVerified = ok
        };
    }

    private void ApplyRetention()
    {
        PruneMissingRepositoryRecords();
        var deleted = new List<string>();
        foreach (var policy in RetentionCounts)
        {
            foreach (var old in EnumeratePhysicalBackups()
                         .Where(item => item.ScheduleClass.Equals(policy.Key, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(item => item.CreatedLocal)
                         .ThenByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase)
                         .Skip(policy.Value))
            {
                try
                {
                    if (File.Exists(old.Path)) File.Delete(old.Path);
                    if (!File.Exists(old.Path)) deleted.Add(old.Path);
                }
                catch
                {
                    // A failed cleanup must never invalidate a newly created verified recovery copy.
                }
            }
        }

        if (deleted.Count > 0)
        {
            try { repository.DeleteBackupRecordsByPath(deleted); } catch { }
        }
        PruneMissingRepositoryRecords();
    }

    private void PruneMissingRepositoryRecords()
    {
        try
        {
            var missing = repository.GetBackups(1000)
                .Where(item => !string.IsNullOrWhiteSpace(item.BackupPath) && !File.Exists(item.BackupPath))
                .Select(item => item.BackupPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length > 0) repository.DeleteBackupRecordsByPath(missing);
        }
        catch
        {
            // Filesystem recovery points remain authoritative; stale metadata cleanup is best effort.
        }
    }

    private static IReadOnlyList<PhysicalBackup> EnumeratePhysicalBackups()
    {
        if (!Directory.Exists(ApplicationDataPaths.VerifiedBackupFolder)) return Array.Empty<PhysicalBackup>();

        var result = new List<PhysicalBackup>();
        foreach (var path in Directory.EnumerateFiles(ApplicationDataPaths.VerifiedBackupFolder, "AAH-*", SearchOption.TopDirectoryOnly))
        {
            var match = BackupNamePattern.Match(Path.GetFileName(path));
            if (!match.Success) continue;

            var scheduleClass = NormalizeScheduleClass(match.Groups["class"].Value);
            DateTime createdLocal;
            if (!DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out createdLocal))
                createdLocal = File.GetLastWriteTime(path);

            result.Add(new PhysicalBackup(path, scheduleClass, createdLocal));
        }
        return result;
    }

    private static string NormalizeScheduleClass(string? value)
    {
        if (value is null) return "Manual";
        foreach (var known in RetentionCounts.Keys)
            if (known.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) return known;
        return "Manual";
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private sealed record PhysicalBackup(string Path, string ScheduleClass, DateTime CreatedLocal);
}
