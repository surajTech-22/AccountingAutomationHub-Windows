namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>
/// Bounded cleanup for application-owned, reproducible storage only.
/// Never touches accounting data, Document Vault, Automation Folder contents,
/// external Python/OCR runtime paths, or user files outside AAH-owned temp/log locations.
/// </summary>
public static class ApplicationStorageMaintenanceService
{
    private static readonly TimeSpan TempRetention = TimeSpan.FromHours(2);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(30);
    private const long LogSoftLimitBytes = 150L * 1024 * 1024;
    private const long LogTargetBytes = 100L * 1024 * 1024;

    public static void RunSafeMaintenance()
    {
        try { PruneUpdateBackups(); } catch { }
        try { PrunePreparedCaches(); } catch { }
        try { PruneDiagnostics(); } catch { }
        try { PruneLogs(); } catch { }
        try { PruneAahTemp(); } catch { }
    }

    private static void PruneUpdateBackups()
    {
        var root = Path.Combine(ApplicationDataPaths.RootFolder, "UpdateBackups");
        if (!Directory.Exists(root)) return;

        var backups = new DirectoryInfo(root)
            .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(item => item.LastWriteTimeUtc)
            .ToArray();

        foreach (var old in backups.Skip(2))
            TryDeleteDirectory(old.FullName);
    }

    private static void PrunePreparedCaches()
    {
        var cacheRoot = Path.Combine(ApplicationDataPaths.RootFolder, "Cache");
        var current = Path.Combine(cacheRoot, "WorkspaceStateV7");
        var frozen = Path.Combine(cacheRoot, "WorkspaceStateV7-Frozen");
        if (!Directory.Exists(cacheRoot)) return;

        foreach (var legacyRoot in new[]
                 {
                     Path.Combine(cacheRoot, "WorkspaceStateV6"),
                     Path.Combine(cacheRoot, "WorkspaceStateV6-Frozen"),
                     Path.Combine(cacheRoot, "WorkspaceStateV5"),
                     Path.Combine(cacheRoot, "WorkspaceStateV5-Frozen")
                 })
        {
            if (!Directory.Exists(legacyRoot)) continue;
            foreach (var file in EnumerateFilesSafe(legacyRoot).Where(file => file.Name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)))
            {
                var currentPath = Path.Combine(current, file.Name);
                var frozenPath = Path.Combine(frozen, file.Name);
                if (File.Exists(currentPath) || File.Exists(frozenPath))
                {
                    try { file.Delete(); } catch { }
                }
            }
            RemoveEmptyDirectories(legacyRoot);
        }

        if (!Directory.Exists(current)) return;

        // R43: the current V7 snapshot is the first restore authority. A same-name frozen
        // V7 file is therefore a reproducible duplicate; retain frozen only when current
        // is missing so inactive-client recovery remains available.
        if (Directory.Exists(frozen))
        {
            foreach (var frozenFile in EnumerateFilesSafe(frozen).Where(file => file.Name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)))
            {
                if (File.Exists(Path.Combine(current, frozenFile.Name)))
                {
                    try { frozenFile.Delete(); } catch { }
                }
            }
            RemoveEmptyDirectories(frozen);
        }

        var files = EnumerateFilesSafe(current).ToList();
        long total = files.Sum(SafeLength);
        const long soft = 256L * 1024 * 1024;
        const long target = 192L * 1024 * 1024;
        if (total <= soft) return;
        foreach (var file in files.Where(file => file.Name.EndsWith(".previous", StringComparison.OrdinalIgnoreCase)).OrderBy(file => file.LastWriteTimeUtc))
        {
            if (total <= target) break;
            var length = SafeLength(file);
            try { file.Delete(); total = Math.Max(0, total - length); } catch { }
        }
    }

    private static void PruneDiagnostics()
    {
        var root = Path.Combine(ApplicationDataPaths.RootFolder, "Diagnostics");
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(90);
        foreach (var file in EnumerateFilesSafe(root))
        {
            try { if (file.LastWriteTimeUtc < cutoff) file.Delete(); } catch { }
        }
        var remaining = EnumerateFilesSafe(root).OrderBy(file => file.LastWriteTimeUtc).ToList();
        long total = remaining.Sum(SafeLength);
        const long soft = 80L * 1024 * 1024;
        const long target = 50L * 1024 * 1024;
        if (total > soft)
        {
            foreach (var file in remaining)
            {
                if (total <= target) break;
                var length = SafeLength(file);
                try { file.Delete(); total = Math.Max(0, total - length); } catch { }
            }
        }
        RemoveEmptyDirectories(root);
    }

    private static void PruneLogs()
    {
        var root = Path.Combine(ApplicationDataPaths.RootFolder, "Logs");
        if (!Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow - LogRetention;
        foreach (var file in EnumerateFilesSafe(root))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff) file.Delete();
            }
            catch { }
        }

        var remaining = EnumerateFilesSafe(root)
            .OrderBy(item => item.LastWriteTimeUtc)
            .ToList();
        var total = remaining.Sum(item => SafeLength(item));
        if (total <= LogSoftLimitBytes) return;

        foreach (var file in remaining)
        {
            if (total <= LogTargetBytes) break;
            var length = SafeLength(file);
            try
            {
                file.Delete();
                total = Math.Max(0, total - length);
            }
            catch { }
        }

        RemoveEmptyDirectories(root);
    }

    private static void PruneAahTemp()
    {
        var temp = Path.GetTempPath();
        var cutoff = DateTime.UtcNow - TempRetention;

        // AAH's structured temp roots. Only stale children are removed so an update/import
        // running in another process is not disturbed.
        foreach (var root in new[]
                 {
                     Path.Combine(temp, "AccountingAutomationHub"),
                     Path.Combine(temp, "AccountingAutomationHub-LanUpdate")
                 })
        {
            if (!Directory.Exists(root)) continue;
            PruneStaleTree(root, cutoff);
        }

        foreach (var pattern in new[]
                 {
                     "AAH_Bank_Recovery_*",
                     "AAH_Bank_*.xlsx",
                     "AAH_BankOCR_PROD_*",
                     "AAH_MT940_*",
                     "AAH_AutoClassify_*",
                     "AAH_PreExisting_*",
                     "AAH-LAN-RO-*.db",
                     "AAH-LAN-IDENTITY-*.db",
                     "AAH-LAN-BOOTSTRAP-*.db",
                     "AAH-LAN-Cleanup-*.cmd"
                 })
        {
            IEnumerable<string> matches;
            try { matches = Directory.EnumerateFiles(temp, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
            catch { continue; }

            foreach (var path in matches)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch { }
            }
        }
    }

    private static void PruneStaleTree(string root, DateTime cutoff)
    {
        foreach (var file in EnumerateFilesSafe(root))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff) file.Delete();
            }
            catch { }
        }
        RemoveEmptyDirectories(root);
    }

    private static IEnumerable<FileInfo> EnumerateFilesSafe(string root)
    {
        try
        {
            return new DirectoryInfo(root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .ToArray();
        }
        catch
        {
            return Array.Empty<FileInfo>();
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch { return 0; }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length)
                .ToArray();
        }
        catch { return; }

        foreach (var directory in directories)
        {
            try { Directory.Delete(directory, recursive: false); } catch { }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
