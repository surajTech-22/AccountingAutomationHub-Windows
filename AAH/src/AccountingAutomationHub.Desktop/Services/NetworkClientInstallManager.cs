using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Keeps one canonical per-user AAH LAN application under LocalAppData\Programs. Shared packages,
/// repair and self-update therefore stay under the signed-in Windows user without routine UAC elevation.
/// Older verified LAN application folders/shortcuts are conservatively redirected/retired without
/// touching LAN data, credentials, unrelated downloads, or the Main Server application.
/// </summary>
public static class NetworkClientInstallManager
{
    private const string RegistryPath = @"Software\Accounting Automation Hub\LAN";
    private const string InstallPathValue = "InstallPath";
    private const string VersionValue = "Version";
    private const string PackageMarker = "AAH_LAN_SHARE_PACKAGE";
    private const string ModeMarker = "AAH_NETWORK_CLIENT_MODE";
    private const string RoleMarker = InstallationRoleService.MarkerFileName;
    private const string PackageFiles = "AAH_LAN_PACKAGE_FILES.txt";
    private const string CleanupLogName = "AAH-LAN-CLEANUP.log";

    /// <summary>
    /// Per-user Windows-standard LAN binary location. The user's profile normally resides on C:, but
    /// unlike a root C:\ folder this location is writable by that signed-in user and does not require
    /// a UAC/admin-token handoff for routine install, repair or self-update.
    /// </summary>
    public static string CanonicalInstallPath => NormalizeDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Accounting Automation Hub LAN", "Application"));

    public static string LegacyMachineInstallPath => NormalizeDirectory(@"C:\Accounting Automation Hub LAN\Application");

    public static bool EnsureCanonicalInstallAndRelaunchIfNeeded()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return false;

        var current = NormalizeDirectory(AppContext.BaseDirectory);
        var canonical = CanonicalInstallPath;
        NetworkClientStartupLog.Write($"Install check. Current={current}; Canonical={canonical}; Legacy={LegacyMachineInstallPath}");

        if (PathsEqual(current, canonical))
        {
            StopRunningLegacyLanProcessIfPresent();
            SaveRegistration(canonical);
            EnsureCanonicalShortcuts(canonical);
            return false;
        }

        var isSharePackage = File.Exists(Path.Combine(current, PackageMarker));

        // A previously-installed machine-root copy can migrate itself to the per-user Programs
        // location without UAC as long as it still has the LAN application manifest. This keeps old
        // registered PCs recoverable even before the Administrator publishes a fresh share package.
        if (!isSharePackage && IsVerifiedLanApplicationFolder(current) && ReadPackageFiles(current).Count > 0)
        {
            StopRunningCanonicalForPackageUpdate(canonical);
            MirrorPackageFiles(current, canonical);
            SaveRegistration(canonical);
            EnsureCanonicalShortcuts(canonical);
            NetworkClientStartupLog.Write($"Migrated verified LAN installation from {current} to {canonical}.");
            StartCanonical(canonical);
            return true;
        }

        // A legacy/extracted LAN copy that is not a share package should redirect to the installed
        // canonical copy if one already exists. Do not copy arbitrary folders that have no manifest.
        if (!isSharePackage)
        {
            if (IsCompleteLanApplicationFolder(canonical))
            {
                SaveRegistration(canonical);
                EnsureCanonicalShortcuts(canonical);
                NetworkClientStartupLog.Write($"Redirecting legacy LAN copy to installed canonical application: {canonical}");
                StartCanonical(canonical);
                return true;
            }

            return false;
        }

        // A Main-Server shared package is an explicit install/update request. Close only verified AAH
        // LAN processes from the new per-user canonical folder or the old legacy machine-root folder.
        StopRunningCanonicalForPackageUpdate(canonical);

        // Never downgrade a newer installed binary. Same-version packages are still mirrored because
        // endpoint/TLS files may legitimately change without an application version change.
        var incomingVersion = ReadApplicationVersion(current);
        var installedVersion = ReadApplicationVersion(canonical);
        var canonicalIsNewer = incomingVersion is not null && installedVersion is not null && installedVersion.CompareTo(incomingVersion) > 0;

        if (!canonicalIsNewer)
            MirrorPackageFiles(current, canonical);

        if (!IsCompleteLanApplicationFolder(canonical))
            throw new InvalidDataException("The canonical AAH LAN application is incomplete after installation.");

        SaveRegistration(canonical);
        EnsureCanonicalShortcuts(canonical);
        // Keep the extracted Main-Server share package intact as recovery media. Previous builds
        // retired package files immediately after handoff; if the canonical child then failed to
        // start, the user could lose the very repair scripts needed to recover it.
        NetworkClientStartupLog.Write($"Shared LAN package installed successfully at {canonical}; source package retained as recovery media; launching under the same signed-in user.");
        StartCanonical(canonical);
        return true;
    }

    private static void SaveRegistration(string canonical)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            key?.SetValue(InstallPathValue, canonical, RegistryValueKind.String);
            key?.SetValue(VersionValue, typeof(NetworkClientInstallManager).Assembly.GetName().Version?.ToString(4) ?? string.Empty, RegistryValueKind.String);
            EnsureLanUninstallRegistration(canonical);
        }
        catch { }
    }

    private static void EnsureLanUninstallRegistration(string canonical)
    {
        try
        {
            var uninstallBat = Path.Combine(canonical, "UNINSTALL_AAH_LAN.bat");
            if (!File.Exists(uninstallBat)) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHubLAN", writable: true);
            if (key is null) return;
            var version = typeof(NetworkClientInstallManager).Assembly.GetName().Version?.ToString(4) ?? string.Empty;
            key.SetValue("DisplayName", "Accounting Automation Hub LAN", RegistryValueKind.String);
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("Publisher", "Accounting Automation Hub", RegistryValueKind.String);
            key.SetValue("InstallLocation", canonical, RegistryValueKind.String);
            key.SetValue("DisplayIcon", Path.Combine(canonical, "AccountingAutomationHub.exe"), RegistryValueKind.String);
            key.SetValue("UninstallString", $"cmd.exe /d /c \"\"{uninstallBat}\"\"", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void MirrorPackageFiles(string source, string target)
    {
        Directory.CreateDirectory(target);
        var incoming = ReadPackageFiles(source);
        if (incoming.Count == 0) throw new InvalidDataException("This LAN share package does not contain its application file manifest. Share the LAN application again from the Main Server.");

        var previous = ReadPackageFiles(target);
        foreach (var relative in incoming)
        {
            var from = SafeCombine(source, relative);
            var to = SafeCombine(target, relative);
            if (!File.Exists(from)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
        }
        foreach (var dynamicName in DynamicServerFiles)
        {
            var from = Path.Combine(source, dynamicName);
            if (!File.Exists(from)) continue;
            File.Copy(from, Path.Combine(target, dynamicName), true);
        }

        foreach (var stale in previous.Except(incoming, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var path = SafeCombine(target, stale);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        if (!IsCompleteLanApplicationFolder(target))
            throw new InvalidDataException("The canonical LAN application update is incomplete.");
    }

    private static HashSet<string> ReadPackageFiles(string folder)
    {
        var path = Path.Combine(folder, PackageFiles);
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return File.ReadAllLines(path)
            .Select(x => x.Trim().Replace('/', Path.DirectorySeparatorChar))
            .Where(x => x.Length > 0 && !x.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathFullyQualified(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string SafeCombine(string root, string relative)
    {
        var normalizedRoot = NormalizeDirectory(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe LAN package path was rejected.");
        return full;
    }

    private static Process? FindRunningCanonicalProcess(string canonical)
    {
        foreach (var process in Process.GetProcessesByName("AccountingAutomationHub"))
        {
            if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && PathsEqual(Path.GetDirectoryName(path) ?? string.Empty, canonical)) return process;
            }
            catch { }
            process.Dispose();
        }
        return null;
    }


    private static void StopRunningLegacyLanProcessIfPresent()
    {
        var legacy = LegacyMachineInstallPath;
        foreach (var process in Process.GetProcessesByName("AccountingAutomationHub"))
        {
            if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
            try
            {
                var path = process.MainModule?.FileName;
                var folder = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
                if (folder.Length == 0 || !PathsEqual(folder, legacy) || !IsVerifiedLanApplicationFolder(folder))
                {
                    process.Dispose();
                    continue;
                }

                var pid = process.Id;
                NetworkClientStartupLog.Write($"A verified legacy root-C LAN process PID {pid} is still running; closing it before the per-user canonical instance acquires the single-instance mutex.");
                try { process.CloseMainWindow(); } catch { }
                try { if (process.WaitForExit(2000)) { process.Dispose(); continue; } } catch { }
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { NetworkClientStartupLog.WriteException("Legacy LAN process shutdown", ex); continue; }
                try { process.WaitForExit(8000); } catch { }
            }
            finally { process.Dispose(); }
        }
    }

    private static void StopRunningCanonicalForPackageUpdate(string canonical)
    {
        var actions = new List<string>();
        var allowedFolders = new[] { canonical, LegacyMachineInstallPath };
        foreach (var process in Process.GetProcessesByName("AccountingAutomationHub"))
        {
            if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
            try
            {
                var path = process.MainModule?.FileName;
                var folder = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
                if (folder.Length == 0 || !allowedFolders.Any(x => PathsEqual(folder, x)))
                {
                    process.Dispose();
                    continue;
                }

                // Require LAN identity markers beside the executable before terminating anything.
                if (!IsVerifiedLanApplicationFolder(folder))
                {
                    process.Dispose();
                    continue;
                }

                var pid = process.Id;
                actions.Add($"LAN install/update requested verified shutdown for PID {pid} at {folder}.");
                try { process.CloseMainWindow(); } catch { }
                try { if (process.WaitForExit(2500)) { process.Dispose(); continue; } } catch { }

                // Close-to-Tray can intentionally cancel CloseMainWindow. The user explicitly started
                // a LAN repair/update, so terminate only the verified AAH LAN process in a known LAN path.
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { throw new InvalidOperationException($"AAH LAN PID {pid} could not be closed for the update. {ex.Message}", ex); }
                if (!process.WaitForExit(10000))
                    throw new InvalidOperationException($"AAH LAN PID {pid} did not exit in time for the update.");
                actions.Add($"Verified LAN PID {pid} was closed automatically so installation/update could continue.");
            }
            finally { process.Dispose(); }
        }
        WriteCleanupLog(canonical, actions);
    }

    private static int? FindRunningProcessInFolder(string folder)
    {
        var normalized = NormalizeDirectory(folder) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("AccountingAutomationHub"))
        {
            if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && Path.GetFullPath(path).StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    var pid = process.Id;
                    process.Dispose();
                    return pid;
                }
            }
            catch { }
            process.Dispose();
        }
        return null;
    }

    private static void EnsureCanonicalShortcuts(string canonical)
    {
        try
        {
            var exe = Path.Combine(canonical, "AccountingAutomationHub.exe");
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Accounting Automation Hub LAN.lnk");
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var startMenu = Path.Combine(programs, "Accounting Automation Hub LAN.lnk");
            foreach (var shortcut in new[] { desktop, startMenu })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
                var escShortcut = shortcut.Replace("'", "''");
                var escExe = exe.Replace("'", "''");
                var escWork = canonical.Replace("'", "''");
                var script = $"$w=New-Object -ComObject WScript.Shell;$s=$w.CreateShortcut('{escShortcut}');$s.TargetPath='{escExe}';$s.WorkingDirectory='{escWork}';$s.Description='Accounting Automation Hub LAN';$s.Save()";
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit(10000);
            }
            RemoveObsoleteLanShortcuts(desktop, startMenu);
        }
        catch { }
    }

    private static void RemoveObsoleteLanShortcuts(string canonicalDesktopShortcut, string canonicalStartShortcut)
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs)
                 })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> shortcuts;
            try { shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.TopDirectoryOnly).ToArray(); }
            catch { continue; }

            foreach (var shortcut in shortcuts)
            {
                if (shortcut.Equals(canonicalDesktopShortcut, StringComparison.OrdinalIgnoreCase) ||
                    shortcut.Equals(canonicalStartShortcut, StringComparison.OrdinalIgnoreCase)) continue;

                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (!name.Contains("Accounting Automation Hub LAN", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("AAH Network Client", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Accounting Automation Hub Network Client", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(shortcut); } catch { }
            }
        }
    }

    /// <summary>
    /// Removes only folders that prove they are AAH LAN application copies by containing
    /// AAH_NETWORK_CLIENT_MODE plus an AAH executable/retired executable. The local read-only
    /// data root (Accounting Automation Hub Network), credentials, user repair packages and the
    /// Main Server application are not eligible for deletion.
    /// </summary>
    private static int _backgroundCleanupScheduled;

    /// <summary>
    /// Historical-copy discovery performs file-system work and must never run on the WPF startup
    /// thread. Schedule it only after the main LAN window is visible; failures are diagnostic-only.
    /// </summary>
    public static void ScheduleBackgroundStaleCopyCleanup()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        if (Interlocked.Exchange(ref _backgroundCleanupScheduled, 1) != 0) return;
        var canonical = CanonicalInstallPath;
        var current = NormalizeDirectory(AppContext.BaseDirectory);
        _ = Task.Run(() =>
        {
            try
            {
                NetworkClientStartupLog.Write("Background stale LAN-copy cleanup started after UI became available.");
                CleanupStaleLanCopies(canonical, current);
                NetworkClientStartupLog.Write("Background stale LAN-copy cleanup finished.");
            }
            catch (Exception exception)
            {
                NetworkClientStartupLog.WriteException("Background stale LAN-copy cleanup", exception);
            }
        });
    }

    private static void CleanupStaleLanCopies(string canonical, string current)
    {
        var actions = new List<string>();
        foreach (var folder in DiscoverVerifiedLanApplicationFolders())
        {
            var normalized = NormalizeDirectory(folder);
            if (PathsEqual(normalized, canonical) || PathsEqual(normalized, current)) continue;
            if (PathsEqual(normalized, NetworkClientRuntime.RootFolder) || IsAncestorOrSame(normalized, NetworkClientRuntime.RootFolder)) continue;
            if (IsAncestorOrSame(normalized, canonical) || IsAncestorOrSame(normalized, current)) continue;

            try
            {
                var runningPid = FindRunningProcessInFolder(normalized);
                if (runningPid is not null)
                {
                    ScheduleApplicationRemoval(normalized, canonical, runningPid.Value, "running stale LAN application");
                    actions.Add($"Scheduled stale running copy for removal after PID {runningPid.Value}: {normalized}");
                    continue;
                }

                RetireVerifiedLanApplicationFolder(normalized);
                actions.Add($"Removed stale LAN application files: {normalized}");
            }
            catch (Exception ex)
            {
                actions.Add($"Could not remove stale LAN application copy: {normalized} :: {ex.Message}");
            }
        }

        WriteCleanupLog(canonical, actions);
    }

    private static IEnumerable<string> DiscoverVerifiedLanApplicationFolders()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacyLocalRoot = Path.Combine(localAppData, "Accounting Automation Hub LAN");
        var legacyMachineRoot = Path.GetDirectoryName(LegacyMachineInstallPath) ?? @"C:\Accounting Automation Hub LAN";

        // Only inspect known historical installation roots. Do not recursively scan Desktop or
        // Downloads: a Main-Server share package is legitimate repair media and must not be deleted
        // merely because a successfully-installed canonical copy exists elsewhere.
        foreach (var folder in EnumerateDirectoriesLimited(legacyLocalRoot, 4))
            if (IsVerifiedLanApplicationFolder(folder)) found.Add(NormalizeDirectory(folder));
        foreach (var folder in EnumerateDirectoriesLimited(legacyMachineRoot, 3))
            if (IsVerifiedLanApplicationFolder(folder)) found.Add(NormalizeDirectory(folder));

        return found;
    }

    private static IEnumerable<string> EnumerateDirectoriesLimited(string root, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        var queue = new Queue<(string Folder, int Depth)>();
        queue.Enqueue((NormalizeDirectory(root), 0));
        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            yield return item.Folder;
            if (item.Depth >= maxDepth) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(item.Folder, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch { continue; }
            foreach (var child in children) queue.Enqueue((child, item.Depth + 1));
        }
    }

    private static bool IsVerifiedLanApplicationFolder(string folder)
    {
        try
        {
            if (!File.Exists(Path.Combine(folder, ModeMarker))) return false;
            var rolePath = Path.Combine(folder, RoleMarker);
            if (File.Exists(rolePath) &&
                !File.ReadAllText(rolePath).Trim().Equals(InstallationRoleService.LanClient, StringComparison.OrdinalIgnoreCase))
                return false;
            return File.Exists(Path.Combine(folder, "AccountingAutomationHub.exe")) ||
                   File.Exists(Path.Combine(folder, "AccountingAutomationHub.legacy-disabled.exe")) ||
                   File.Exists(Path.Combine(folder, "AccountingAutomationHub.package-used.exe"));
        }
        catch { return false; }
    }

    private static bool IsCompleteLanApplicationFolder(string folder)
    {
        try
        {
            var rolePath = Path.Combine(folder, RoleMarker);
            return Directory.Exists(folder) &&
                   File.Exists(Path.Combine(folder, ModeMarker)) &&
                   File.Exists(rolePath) &&
                   File.ReadAllText(rolePath).Trim().Equals(InstallationRoleService.LanClient, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(Path.Combine(folder, "AccountingAutomationHub.exe"));
        }
        catch { return false; }
    }

    private static Version? ReadApplicationVersion(string folder)
    {
        try
        {
            var exe = Path.Combine(folder, "AccountingAutomationHub.exe");
            if (!File.Exists(exe)) return null;
            var text = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (Version.TryParse(text, out var version)) return version;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var numeric = new string(text.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()).TrimEnd('.');
                if (Version.TryParse(numeric, out version)) return version;
            }
        }
        catch { }
        return null;
    }

    private static void StartCanonical(string canonical)
    {
        var exe = Path.Combine(canonical, "AccountingAutomationHub.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("The canonical AAH LAN executable is missing.", exe);
        NetworkClientStartupLog.Write($"Launching canonical LAN application: {exe}");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = canonical,
            UseShellExecute = true,
            Arguments = "--network-client"
        });
        if (process is null) throw new InvalidOperationException("The canonical AAH LAN application could not be started after installation/consolidation.");
    }

    private static void RetireVerifiedLanApplicationFolder(string folder)
    {
        if (!IsVerifiedLanApplicationFolder(folder)) return;
        foreach (var file in BuildRemovalFileList(folder))
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
        RemoveEmptyApplicationDirectories(folder);
    }

    private static IReadOnlyList<string> BuildRemovalFileList(string folder)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in ReadPackageFiles(folder))
        {
            try { files.Add(SafeCombine(folder, relative)); } catch { }
        }

        foreach (var name in DynamicServerFiles.Concat(new[]
                 {
                     PackageMarker,
                     ModeMarker,
                     RoleMarker,
                     PackageFiles,
                     "AccountingAutomationHub.exe",
                     "AccountingAutomationHub.legacy-disabled.exe",
                     "AccountingAutomationHub.package-used.exe",
                     "AAH-LAN-MOVED.txt"
                 }))
        {
            try { files.Add(Path.Combine(folder, name)); } catch { }
        }
        return files.OrderByDescending(x => x.Length).ToArray();
    }

    private static void RemoveEmptyApplicationDirectories(string folder)
    {
        try
        {
            var children = Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToArray()
                : Array.Empty<string>();
            foreach (var child in children)
            {
                try { Directory.Delete(child, recursive: false); } catch { }
            }
            try { Directory.Delete(folder, recursive: false); } catch { }
        }
        catch { }
    }

    private static void ScheduleApplicationRemoval(string folder, string canonical, int waitPid, string reason)
    {
        try
        {
            var normalized = NormalizeDirectory(folder);
            if (PathsEqual(normalized, canonical) || IsAncestorOrSame(normalized, canonical)) return;
            if (PathsEqual(normalized, NetworkClientRuntime.RootFolder) || IsAncestorOrSame(normalized, NetworkClientRuntime.RootFolder)) return;
            if (!IsVerifiedLanApplicationFolder(normalized)) return;

            var removalFiles = BuildRemovalFileList(normalized);
            var directories = removalFiles
                .Select(Path.GetDirectoryName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => NormalizeDirectory(x!))
                .Where(x => IsAncestorOrSame(normalized, x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Length)
                .ToArray();

            var cleanupTempFolder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "LanCleanup");
            Directory.CreateDirectory(cleanupTempFolder);
            var script = Path.Combine(cleanupTempFolder, "AAH-LAN-Cleanup-" + Guid.NewGuid().ToString("N") + ".cmd");
            static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            var lines = new List<string>
            {
                "@echo off",
                "setlocal",
                $":wait_{waitPid}",
                $"tasklist /FI \"PID eq {waitPid}\" 2>NUL | find \"{waitPid}\" >NUL",
                $"if not errorlevel 1 (timeout /t 1 /nobreak >NUL & goto wait_{waitPid})"
            };
            foreach (var file in removalFiles) lines.Add($"del /Q {Q(file)} >NUL 2>NUL");
            foreach (var directory in directories) lines.Add($"rmdir {Q(directory)} >NUL 2>NUL");
            lines.Add($"rmdir {Q(normalized)} >NUL 2>NUL");
            lines.Add($">>{Q(Path.Combine(canonical, CleanupLogName))} echo [%date% %time%] Retired {reason}: {normalized}");
            lines.Add($"del /Q {Q(script)} >NUL 2>NUL");
            lines.Add("endlocal");

            File.WriteAllLines(script, lines, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo
            {
                FileName = script,
                WorkingDirectory = cleanupTempFolder,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private static void WriteCleanupLog(string canonical, IReadOnlyCollection<string> actions)
    {
        if (actions.Count == 0) return;
        try
        {
            Directory.CreateDirectory(canonical);
            using var writer = new StreamWriter(Path.Combine(canonical, CleanupLogName), true, new UTF8Encoding(false));
            foreach (var action in actions) writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {action}");
        }
        catch { }
    }

    private static bool IsAncestorOrSame(string possibleAncestor, string child)
    {
        var ancestor = NormalizeDirectory(possibleAncestor);
        var normalizedChild = NormalizeDirectory(child);
        if (ancestor.Equals(normalizedChild, StringComparison.OrdinalIgnoreCase)) return true;
        return (normalizedChild + Path.DirectorySeparatorChar).StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string value) => Path.GetFullPath(value.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool PathsEqual(string left, string right) => NormalizeDirectory(left).Equals(NormalizeDirectory(right), StringComparison.OrdinalIgnoreCase);

    private static readonly string[] DynamicServerFiles =
    {
        "AAH_MAIN_SERVER_ENDPOINT",
        "AAH_MAIN_SERVER_CA.cer",
        "AAH_MAIN_SERVER_CA_SHA256",
        "AAH_MAIN_SERVER_TLS.cer",
        "AAH_MAIN_SERVER_TLS_SHA256"
    };
}
