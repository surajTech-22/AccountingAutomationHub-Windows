using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// LAN workstation self-update path. Only a package returned by the enrolled Main Server is accepted.
/// SHA-256 is verified before extraction and path traversal is rejected before the running application exits.
/// The canonical LAN binary location is per-user, so update/relaunch stays under the same Windows identity
/// and does not require a UAC/admin-token handoff.
/// </summary>
public static class NetworkClientSelfUpdateService
{
    public static async Task<bool> DownloadVerifyAndScheduleAsync(FullUiNetworkClientApi api, Window? owner, bool forced)
    {
        var metadata = await api.GetWindowsPackageMetadataAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(metadata.Sha256) || string.IsNullOrWhiteSpace(metadata.Version))
            throw new InvalidOperationException("The Main Server did not return a valid approved Windows update.");

        var updateRoot = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub-LanUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateRoot);
        var zipPath = Path.Combine(updateRoot, "AAHNetworkClient.zip");
        await api.DownloadWindowsPackageAsync(zipPath).ConfigureAwait(true);

        var actualHash = ComputeSha256(zipPath);
        if (!actualHash.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Main Server update verification failed. Expected SHA-256 {metadata.Sha256}, received {actualHash}.");

        var stage = Path.Combine(updateRoot, "stage");
        ExtractSafe(zipPath, stage);
        var stagedExe = Path.Combine(stage, "AccountingAutomationHub.exe");
        var networkMarker = Path.Combine(stage, "AAH_NETWORK_CLIENT_MODE");
        var roleMarker = Path.Combine(stage, InstallationRoleService.MarkerFileName);
        var serverMarker = Path.Combine(stage, "AAH_MAIN_SERVER_ENDPOINT");
        var role = File.Exists(roleMarker) ? File.ReadAllText(roleMarker).Trim() : string.Empty;
        if (!File.Exists(stagedExe) || !File.Exists(networkMarker) || !File.Exists(serverMarker) ||
            !role.Equals(InstallationRoleService.LanClient, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The approved update is not a valid role-bound AAH LAN package.");

        var target = NetworkClientInstallManager.CanonicalInstallPath;
        Directory.CreateDirectory(target);
        if (!CanWrite(target))
            throw new UnauthorizedAccessException($"AAH LAN cannot update its per-user application folder: {target}. Run INSTALL_OR_REPAIR_AAH_LAN.bat from the latest Main Server LAN package.");

        var updater = Path.Combine(updateRoot, "apply-lan-update.cmd");
        var relauncher = Path.Combine(updateRoot, "relaunch-lan-update.cmd");
        var completeMarker = Path.Combine(updateRoot, "update-complete.ok");
        var failedMarker = Path.Combine(updateRoot, "update-failed.txt");
        var pid = Environment.ProcessId;
        var exePath = Path.Combine(target, "AccountingAutomationHub.exe");
        var updateLogRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Accounting Automation Hub LAN");
        Directory.CreateDirectory(updateLogRoot);
        var updateLog = Path.Combine(updateLogRoot, "AAH-LAN-UPDATE.log");
        File.AppendAllText(updateLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Scheduling verified LAN {metadata.Version} update. Target={target}; CurrentPID={pid}.{Environment.NewLine}", new UTF8Encoding(false));
        File.WriteAllText(updater, BuildUpdaterScript(pid, stage, target, completeMarker, failedMarker, updateLog), new UTF8Encoding(false));
        File.WriteAllText(relauncher, BuildRelaunchScript(pid, target, exePath, completeMarker, failedMarker, updateRoot, updateLog), new UTF8Encoding(false));

        // Both helpers inherit the current LAN user's token. No UAC elevation is needed because the
        // canonical application lives under this user's LocalAppData\Programs directory.
        var relaunchStart = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = updateRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        relaunchStart.ArgumentList.Add("/d");
        relaunchStart.ArgumentList.Add("/c");
        relaunchStart.ArgumentList.Add(relauncher);
        var relaunchProcess = Process.Start(relaunchStart);
        if (relaunchProcess is null)
            throw new InvalidOperationException("Could not start the LAN update relaunch watcher.");

        var updateStart = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = updateRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        updateStart.ArgumentList.Add("/d");
        updateStart.ArgumentList.Add("/c");
        updateStart.ArgumentList.Add(updater);
        try
        {
            var updaterProcess = Process.Start(updateStart);
            if (updaterProcess is null)
                throw new InvalidOperationException("Could not start the LAN application updater.");
        }
        catch
        {
            try { File.WriteAllText(failedMarker, "updater-start-failed", Encoding.ASCII); } catch { }
            throw;
        }

        if (owner is not null)
        {
            MessageBox.Show(owner,
                forced
                    ? $"The Main Server requires AAH LAN version {metadata.Version}. The verified update has been downloaded. AAH will close, update in your per-user application folder and reopen automatically."
                    : $"AAH LAN version {metadata.Version} was downloaded and verified. The application will close, update and reopen automatically.",
                forced ? "AAH LAN Update Required" : "AAH LAN Update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        return true;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ExtractSafe(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Main Server update contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(output);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, true);
        }
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".aah-lan-update-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "test", Encoding.ASCII);
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string BuildUpdaterScript(int pid, string stage, string target, string completeMarker, string failedMarker, string updateLog)
    {
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"del /q {Q(completeMarker)} {Q(failedMarker)} >NUL 2>NUL");
        sb.AppendLine($">>{Q(updateLog)} echo [%date% %time%] Same-user LAN update helper started; waiting for PID {pid}.");
        sb.AppendLine($":wait_{pid}");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL");
        sb.AppendLine($"if not errorlevel 1 (timeout /t 1 /nobreak >NUL & goto wait_{pid})");
        sb.AppendLine($"robocopy {Q(stage)} {Q(target)} /E /COPY:DAT /DCOPY:DAT /R:3 /W:1 /NFL /NDL /NJH /NJS >NUL");
        sb.AppendLine("set RC=%ERRORLEVEL%");
        sb.AppendLine("if %RC% GEQ 8 goto failed");
        sb.AppendLine($">{Q(completeMarker)} echo OK");
        sb.AppendLine($">>{Q(updateLog)} echo [%date% %time%] LAN application files replaced successfully; robocopy=%RC%.");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":failed");
        sb.AppendLine($">{Q(failedMarker)} echo robocopy=%RC%");
        sb.AppendLine($">>{Q(updateLog)} echo [%date% %time%] LAN application replacement FAILED; robocopy=%RC%.");
        sb.AppendLine("exit /b %RC%");
        return sb.ToString();
    }

    private static string BuildRelaunchScript(int pid, string target, string exePath, string completeMarker, string failedMarker, string updateRoot, string updateLog)
    {
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions EnableDelayedExpansion");
        sb.AppendLine("set /a WAITCOUNT=0");
        sb.AppendLine($":wait_process_{pid}");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL");
        sb.AppendLine($"if not errorlevel 1 (timeout /t 1 /nobreak >NUL & goto wait_process_{pid})");
        sb.AppendLine(":wait_result");
        sb.AppendLine($"if exist {Q(completeMarker)} goto launch");
        sb.AppendLine($"if exist {Q(failedMarker)} goto failed");
        sb.AppendLine("set /a WAITCOUNT+=1");
        sb.AppendLine("if !WAITCOUNT! GEQ 180 goto timedout");
        sb.AppendLine("timeout /t 1 /nobreak >NUL");
        sb.AppendLine("goto wait_result");
        sb.AppendLine(":launch");
        sb.AppendLine($"cd /d {Q(target)}");
        sb.AppendLine($">>{Q(updateLog)} echo [%date% %time%] Relaunching AAH LAN under the same signed-in Windows user.");
        sb.AppendLine($"start \"\" {Q(exePath)} --network-client");
        sb.AppendLine("timeout /t 3 /nobreak >NUL");
        sb.AppendLine($"rmdir /s /q {Q(updateRoot)} 2>NUL");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":failed");
        sb.AppendLine($">>{Q(updateLog)} echo [%date% %time%] Relaunch was not attempted because file replacement failed.");
        sb.AppendLine("exit /b 8");
        sb.AppendLine(":timedout");
        sb.AppendLine($">>{Q(updateLog)} echo [%date% %time%] Relaunch watcher timed out waiting for updater result.");
        sb.AppendLine("exit /b 1460");
        return sb.ToString();
    }
}
