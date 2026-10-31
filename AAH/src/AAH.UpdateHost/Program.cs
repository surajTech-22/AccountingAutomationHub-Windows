using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aah.UpdateHost;

// This executable follows the owner's GitHub HTTPS + SHA256 release channel.
// If Authenticode is present, it MUST match the already installed publisher.
// Unsigned binaries receive no Windows publisher trust and require explicit
// user enrollment of the GitHub release channel first.
internal static class Program
{
    private const string Owner = "surajTech-22";
    private const string Repository = "AccountingAutomationHub-Windows";
    private const string ReleaseApi = "https://api.github.com/repos/" + Owner + "/" + Repository + "/releases/latest";
    private const string AssetName = "AAH_App-win-x64.zip";
    private const string ProtocolName = "AAH_AutoUpdate.json";
    private static readonly HttpClient Http = CreateClient();
    private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccountingAutomationHub", "AutoUpdateLogs", "github-update-host.log");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!args.Contains("--aah-auto-update-v1", StringComparer.Ordinal))
                throw new InvalidOperationException("Requires the signed AAH automatic-update invocation protocol.");
            var target = Arg(args, "--target-exe=");
            var tag = Arg(args, "--release=");
            if (!int.TryParse(Arg(args, "--parent-pid="), out var parentId) || parentId <= 0)
                throw new InvalidDataException("Invalid original AAH process ID.");
            if (!Regex.IsMatch(tag, @"\Av?\d+\.\d+\.\d+(?:\.\d+)?\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid stable release tag.");
            if (!Path.IsPathFullyQualified(target) || Path.GetFileName(target) != "AccountingAutomationHub.exe")
                throw new InvalidDataException("Updater target must be the installed AAH executable.");
            target = Path.GetFullPath(target);
            var install = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Invalid installation.");
            if (new DirectoryInfo(install).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Reparse-point installation not supported.");
            if (!File.Exists(target) || File.Exists(Path.Combine(install, "AAH_NETWORK_CLIENT_MODE")) ||
                (File.Exists(Path.Combine(install, "AAH_INSTALLATION_ROLE")) &&
                 File.ReadAllText(Path.Combine(install, "AAH_INSTALLATION_ROLE")).Trim()
                    .Equals("LanClient", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("GitHub update cannot target an AAH LAN client or missing install.");

            using var oneUpdate = new Mutex(true, @"Local\AccountingAutomationHub.GitHubUpdate.v1", out var created);
            if (!created) throw new InvalidOperationException("Another AAH GitHub updater is running.");
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Updater process path missing.");
            var publisher = await VerifiedPublisherAsync(self);
            var installedPublisher = await VerifiedPublisherAsync(target);
            if (!PublisherIdentityCompatible(publisher, installedPublisher))
                throw new InvalidDataException("Updater and installed AAH have incompatible Authenticode signing identities.");

            using var parent = Process.GetProcessById(parentId);
            if (!SameProcessPath(parent, target))
                throw new InvalidDataException("Specified AAH process does not match the installed application path.");

            using var response = await Http.GetAsync(ReleaseApi);
            response.EnsureSuccessStatusCode();
            using var releaseDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var release = releaseDoc.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
                release.GetProperty("tag_name").GetString() != tag)
                throw new InvalidDataException("The requested stable GitHub release is not current.");
            var manifestUrl = GetAsset(release, ProtocolName, tag);
            var archiveUrl = GetAsset(release, AssetName, tag);
            using var manifestDoc = JsonDocument.Parse(await Http.GetStringAsync(manifestUrl));
            var manifest = manifestDoc.RootElement;
            if (manifest.GetProperty("protocol").GetString() != "AAH-GITHUB-AUTOUPDATE-v1" ||
                manifest.GetProperty("release").GetString() != tag ||
                manifest.GetProperty("repository").GetString() != Owner + "/" + Repository ||
                manifest.GetProperty("verification").GetString() != "github-release-sha256-v1" ||
                manifest.GetProperty("automatic_install_allowed").GetBoolean() != true ||
                manifest.GetProperty("app_asset").GetString() != AssetName)
                throw new InvalidDataException("The GitHub release is not authorized for automatic installation.");
            var expectedHash = manifest.GetProperty("app_sha256").GetString();
            if (expectedHash is null || !Regex.IsMatch(expectedHash, @"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("Missing verified application archive SHA256.");

            var scratch = Path.Combine(Path.GetTempPath(), "AAH-GitHub-Release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var archivePath = Path.Combine(scratch, AssetName);
                await DownloadVerifiedAsync(archiveUrl, archivePath, expectedHash);
                var staged = Path.Combine(scratch, "publish");
                ExtractStrict(archivePath, staged);
                var stagedExe = Path.Combine(staged, "AccountingAutomationHub.exe");
                if (!File.Exists(stagedExe) ||
                    !PublisherIdentityCompatible(installedPublisher, await VerifiedPublisherAsync(stagedExe)))
                    throw new InvalidDataException("Release application and installed AAH have incompatible signing identities.");
                var installedVersion = FileVersionInfo.GetVersionInfo(target).FileVersion;
                var newVersion = FileVersionInfo.GetVersionInfo(stagedExe).FileVersion;
                if (!Version.TryParse(installedVersion, out var oldVer) ||
                    !Version.TryParse(newVersion, out var nextVer) ||
                    !IsUpgradeOrRestartRecovery(oldVer, nextVer, tag))
                    throw new InvalidDataException("Application binary version does not match an authorized upgrade or same-version restart recovery.");
                // If a previous update committed new application files but the
                // old AAH process still holds the in-memory older UI, never retry
                // copying the same release. Instead finish its stale-process
                // shutdown and restart the ALREADY INSTALLED verified executable.
                var restartOnly = nextVer == oldVer;

                // The previously running AAH may have two process instances for the
                // SAME executable (legacy restart/old owner still alive). Previously
                // we stopped only the caller PID, copied files and launched a new AAH;
                // the surviving sibling owned the single-instance mutex, making the
                // new process exit without publishing readiness. Never repeat that.
                // All exact-path AAH instances must complete their own graceful
                // Tally/import drain and exit BEFORE any application file is replaced.
                Log("Release " + tag + " verified. Requesting graceful shutdown of EVERY AAH process from this installation.");
                if (!await ShutdownAllVerifiedInstallProcessesAsync(target))
                {
                    Log("AAH instances could not all exit safely. No program files changed, processes killed or restart attempted.");
                    return 2;
                }

                if (!restartOnly)
                {
                    // Preserve the original transactional updater. There must be
                    // no second live C# file replacement or data migration route.
                    var nativeScript = Path.Combine(scratch, "TransactionalPublish.ps1");
                    WriteEmbeddedManualUpdater(nativeScript);
                    await InvokeOriginalManualInstallerAsync(nativeScript, staged, install);
                    Log("Manual Install-AAHPublish replaced AAH application files and removed its completed swap journal.");
                }
                else
                {
                    Log("Repairing earlier partial update: verified " + tag +
                        " already exists on disk; no files will be overwritten.");
                }
                if (!await StartAndVerifyAsync(target, install))
                {
                    // The original manual updater has already committed verified
                    // bytes. Never claim health-check rollback without restoration.
                    Log("Updated AAH startup readiness was not confirmed. The original manual updater committed installed bytes; intervention may be needed.");
                    throw new InvalidOperationException("AAH updated, but the restart readiness check failed.");
                }
                Log(restartOnly
                    ? "SUCCESS: AAH " + tag + " already installed; all stale instances exited and existing executable restarted."
                    : "SUCCESS: AAH " + tag + " replaced in place by original manual updater and restarted.");
                return 0;
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch { }
            }
        }
        catch (Exception exception)
        {
            Log("FAILED (no success claimed): " + exception);
            return 1;
        }
    }

    private static void WriteEmbeddedManualUpdater(string path)
    {
        using var input = typeof(Program).Assembly.GetManifestResourceStream(
            "AAH.NativeTransactionalPublish.ps1")
            ?? throw new FileNotFoundException(
                "The original manual AAH transactional updater was not embedded.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    internal static async Task InvokeOriginalManualInstallerAsync(
        string manualUpdaterScript, string publishDir, string existingInstallDir)
    {
        // The original AAH PowerShell source is embedded, not downloaded
        // independently from the untrusted release manifest.
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = existingInstallDir
        };
        psi.Environment["AAH_MANUAL_SCRIPT"] = Path.GetFullPath(manualUpdaterScript);
        psi.Environment["AAH_PUBLISH_DIR"] = Path.GetFullPath(publishDir);
        psi.Environment["AAH_INSTALL_DIR"] = Path.GetFullPath(existingInstallDir);
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            "$ErrorActionPreference='Stop'; " +
            ". $env:AAH_MANUAL_SCRIPT; " +
            "Install-AAHPublish $env:AAH_PUBLISH_DIR $env:AAH_INSTALL_DIR; exit 0");
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Original AAH manual updater could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "Original manual AAH update failed with code " + process.ExitCode +
                ". " + errors.Trim());
        if (!output.Contains("Application replacement verified.", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Original AAH manual updater returned without its success marker.");
    }

    private static string Arg(string[] args, string prefix)
    {
        var all = args.Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (all.Length != 1 || all[0].Length == prefix.Length)
            throw new InvalidDataException("Required update argument missing or repeated: " + prefix);
        return all[0][prefix.Length..];
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AAH-UpdateHost", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static Uri GetAsset(JsonElement release, string filename, string tag)
    {
        var prefix = "/surajTech-22/AccountingAutomationHub-Windows/releases/download/";
        foreach (var item in release.GetProperty("assets").EnumerateArray())
        {
            if (item.GetProperty("name").GetString() != filename) continue;
            var value = item.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                uri.AbsolutePath != prefix + Uri.EscapeDataString(tag) + "/" + filename)
                throw new InvalidDataException("Untrusted update release asset URL.");
            return uri;
        }
        throw new FileNotFoundException("Required release asset missing: " + filename);
    }

    private static async Task DownloadVerifiedAsync(Uri uri, string destination, string sha)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 650_000_000)
            throw new InvalidDataException("Excessive application update size.");
        await using (var src = await response.Content.ReadAsStreamAsync())
        await using (var dest = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024]; long count = 0; int n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                if ((count += n) > 650_000_000) throw new InvalidDataException("Excessive application update size.");
                await dest.WriteAsync(buffer.AsMemory(0, n));
            }
        }
        if (!Hash(destination).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded application archive SHA256 mismatch.");
    }

    internal static void ExtractStrict(string zipPath, string dest)
    {
        Directory.CreateDirectory(dest);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 2500) throw new InvalidDataException("Too many release files.");
        long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;
            var relative = entry.FullName.Replace('\\','/');
            if (Path.IsPathRooted(relative) || relative.Contains(':') ||
                relative.Split('/').Any(s => s is "" or "." or ".."))
                throw new InvalidDataException("Unsafe relative ZIP path.");
            if (new[] {".db",".sqlite",".sqlite3",".bak",".pfx",".key",".pem"}.Contains(Path.GetExtension(relative).ToLowerInvariant()) ||
                new[] {"passwords.json",".env","credentials.json"}.Contains(Path.GetFileName(relative).ToLowerInvariant()))
                throw new InvalidDataException("Private/accounting data refused in application update.");
            // Unix symlink external attributes are not permitted, even in ZIPs built elsewhere.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("ZIP symlinks are not allowed.");
            if ((bytes += entry.Length) > 1_500_000_000L)
                throw new InvalidDataException("Excessive extracted payload size.");
            var absolute = Path.GetFullPath(Path.Combine(dest, relative));
            if (!absolute.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP path traversal.");
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            entry.ExtractToFile(absolute, overwrite: false);
        }
    }

    private sealed class UpdateRuntimeBusyException(string message) : Exception(message) { }
    private sealed record Entry(string Relative, bool Existed, string OldHash, string NewHash);
    private sealed record Journal(string Install, List<Entry> Entries);

    internal static string StageTransaction(string source, string install)
    {
        RecoverPending(install);
        var transaction = Path.Combine(install, ".aah-github-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        try
        {
        var entries = new List<Entry>();
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, f);
            if (new[] { ".db", ".sqlite", ".sqlite3", ".bak", ".pfx", ".key", ".pem" }
                .Contains(Path.GetExtension(relative).ToLowerInvariant()))
                throw new InvalidDataException("Accounting or private data cannot be installed as application payload.");
            if (File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Application release may not contain file links.");
            var target = Guard(install, relative);
            var stage = Guard(Path.Combine(transaction,"new"), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
            File.Copy(f, stage, false);
            if (Hash(f) != Hash(stage)) throw new InvalidDataException("Staged application file hash mismatch.");
            var existed = File.Exists(target);
            entries.Add(new Entry(relative, existed, existed ? Hash(target) : "", Hash(stage)));
        }
        if (entries.Count == 0) throw new InvalidDataException("Empty application update.");
        File.WriteAllText(Path.Combine(transaction,"journal.json"), JsonSerializer.Serialize(new Journal(
            Path.GetFullPath(install), entries)), Encoding.UTF8);
        return transaction;
        }
        catch
        {
            // Staging failures happen before any installed file is replaced.
            try { Directory.Delete(transaction, true); } catch { }
            throw;
        }
    }

    private static string Guard(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd('\\','/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Transaction escapes the application directory.");
        var walk = Path.GetDirectoryName(full);
        while (walk is not null && walk.Length >= prefix.Length)
        {
            if (File.Exists(walk) || Directory.Exists(walk))
                if (File.GetAttributes(walk).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Application path contains a reparse point.");
            walk = Path.GetDirectoryName(walk);
        }
        return full;
    }

    internal static void ApplyTransaction(string tx, string install)
    {
        var journal = ReadJournal(tx, install);
        foreach (var row in journal.Entries)
        {
            var target = Guard(install, row.Relative);
            var old = Guard(Path.Combine(tx,"old"), row.Relative);
            var staged = Guard(Path.Combine(tx,"new"), row.Relative);
            if (!File.Exists(staged) || Hash(staged) != row.NewHash)
                throw new InvalidDataException("Staged application bytes were changed.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (row.Existed)
            {
                if (!File.Exists(target) || Hash(target) != row.OldHash)
                    throw new InvalidDataException("Installed AAH was modified while an update was staged.");
                Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                File.Move(target, old);
            }
            else if (File.Exists(target))
                throw new InvalidDataException("Unexpected installed file exists.");
            File.Move(staged, target);
        }
    }

    private static Journal ReadJournal(string tx, string install)
    {
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(Path.Combine(tx,"journal.json")))
                      ?? throw new InvalidDataException("Transaction journal absent.");
        if (journal.Install != Path.GetFullPath(install) || journal.Entries.Count == 0)
            throw new InvalidDataException("Invalid pending AAH GitHub transaction.");
        if (journal.Entries.Select(e => e.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Count)
            throw new InvalidDataException("Repeated application transaction entry.");
        return journal;
    }

    internal static void RollbackTransaction(string tx, string install)
    {
        var journal = ReadJournal(tx,install);
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            var target = Guard(install, entry.Relative);
            var old = Guard(Path.Combine(tx,"old"), entry.Relative);
            var staged = Guard(Path.Combine(tx,"new"), entry.Relative);
            if (File.Exists(old))
            {
                if (Hash(old) != entry.OldHash) throw new InvalidDataException("Saved original hash mismatch.");
                if (File.Exists(target))
                {
                    if (Hash(target) != entry.NewHash)
                        throw new InvalidDataException("Rollback blocked by external file modification.");
                    File.Delete(target);
                }
                File.Move(old,target);
            }
            else if (!entry.Existed && !File.Exists(staged) && File.Exists(target))
            {
                if (Hash(target) != entry.NewHash)
                    throw new InvalidDataException("Rollback blocked by unexpected new file content.");
                File.Delete(target);
            }
        }
        File.WriteAllText(Path.Combine(tx,"rolled-back"),"OK");
        Directory.Delete(tx,true);
    }

    internal static void CompleteTransaction(string transaction)
    {
        // Matches manual Install-AAHPublish: after verified startup the previous
        // installed DLL/EXE bytes are no longer retained as a second old copy.
        // First record completion durably, then clean up the rollback journal.
        File.WriteAllText(Path.Combine(transaction, "committed"), "OK");
        try { Directory.Delete(transaction, recursive: true); }
        catch (IOException) { Log("Committed update cleanup deferred (locked file)."); }
        catch (UnauthorizedAccessException) { Log("Committed update cleanup deferred (permission)."); }
    }

    internal static void RecoverPending(string install)
    {
        foreach (var tx in Directory.GetDirectories(install, ".aah-github-swap-*"))
        {
            if (new DirectoryInfo(tx).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Unsafe prior transaction directory.");
            if (File.Exists(Path.Combine(tx,"committed")) || File.Exists(Path.Combine(tx,"rolled-back")))
            {
                Directory.Delete(tx,true);
                continue;
            }
            if (!File.Exists(Path.Combine(tx, "journal.json")))
            {
                // Before journal creation no installed bytes have changed.
                Directory.Delete(tx, true);
                continue;
            }
            RollbackTransaction(tx,install);
        }
    }

    /// <summary>
    /// A repeated-update race used to leave the original AAH PID or its sibling
    /// alive, then a new process failed single-instance startup readiness.
    /// Enumerate and verify the FULL executable path of every AAH process.
    /// Never signal other installation paths or unknown identities, never kill.
    /// A process that cannot be inspected causes an explicit safe block.
    /// </summary>
    internal static async Task<bool> ShutdownAllVerifiedInstallProcessesAsync(string installedExe)
    {
        for (var pass = 1; pass <= 4; pass++)
        {
            var processes = GetSameInstallationProcesses(installedExe);
            try
            {
                if (processes.Count == 0)
                {
                    Log("All exact-install AAH processes exited; replacement permitted.");
                    return true;
                }
                Log("Update shutdown pass " + pass + ": gracefully closing " +
                    processes.Count + " exact-install AAH process(es): " +
                    string.Join(",", processes.Select(x => x.Id)));
                var results = await Task.WhenAll(processes.Select(async process =>
                    await RequestVerifiedUpdateExitWithRetryAsync(process)));
                if (results.Any(x => !x))
                {
                    Log("Shutdown deferred: at least one AAH instance remains busy.");
                    return false;
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            // A previous window can briefly spawn another same-path process.
            // Recheck before entering the manual transaction; never rely on a
            // prior PID snapshot.
            await Task.Delay(500);
        }
        var remaining = GetSameInstallationProcesses(installedExe);
        try
        {
            if (remaining.Count == 0) return true;
            Log("Cannot replace files: same-install AAH processes remain: " +
                string.Join(",", remaining.Select(x => x.Id)));
            return false;
        }
        finally { foreach (var p in remaining) p.Dispose(); }
    }

    internal static bool IsExactInstallationProcess(string processName, string fullProcessExe, string installedExe) =>
        processName.Equals("AccountingAutomationHub", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(fullProcessExe).Equals(Path.GetFullPath(installedExe),
            StringComparison.OrdinalIgnoreCase);

    private static List<Process> GetSameInstallationProcesses(string installedExe)
    {
        var selected = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcessesByName("AccountingAutomationHub"))
            {
                try
                {
                    if (process.HasExited) { process.Dispose(); continue; }
                    var fullPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(fullPath))
                        throw new InvalidDataException("Cannot verify path for AAH PID " + process.Id);
                    if (IsExactInstallationProcess(process.ProcessName, fullPath, installedExe))
                        selected.Add(process);
                    else process.Dispose();
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    process.Dispose(); // Raced with a normal process exit.
                }
                catch
                {
                    process.Dispose();
                    throw; // Refuse to overwrite an uninspectable AAH process.
                }
            }
            return selected;
        }
        catch
        {
            foreach (var process in selected) process.Dispose();
            throw;
        }
    }

    internal static bool IsUpgradeOrRestartRecovery(Version installedOnDisk,
        Version verifiedReleaseBinary, string stableTag) =>
        verifiedReleaseBinary >= installedOnDisk &&
        verifiedReleaseBinary.ToString().Equals(stableTag.TrimStart('v'),
            StringComparison.Ordinal);

    // Older AAH versions do not quiesce repeatable background discovery when
    // a verified update is requested. A single 11-minute wait can therefore
    // miss the moment an import finishes. The updater owns one bounded retry;
    // it never sends a force-kill, closes unrelated processes or overwrites a
    // running application's files.
    internal static async Task<bool> RequestVerifiedUpdateExitWithRetryAsync(Process parent)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (await RequestGracefulUpdateExitAsync(parent, TimeSpan.FromMinutes(11)))
                return true;
            if (attempt < 2)
                Log("Original AAH has not exited after verified update request; resending the same PID-targeted graceful signal once.");
        }
        return false;
    }

    internal static async Task<bool> RequestGracefulUpdateExitAsync(Process parent, TimeSpan timeout)
    {
        // Verify the process-specific, locally created event. Do NOT create
        // the event here: an absent listener is not permission to kill AAH.
        // In particular do not signal a different PID or process name.
        if (parent.HasExited) return true;
        if (!parent.ProcessName.Equals("AccountingAutomationHub", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Graceful update shutdown target is not AAH.");
        try
        {
            using var exitSignal = EventWaitHandle.OpenExisting(
                @"Local\AccountingAutomationHub.UpdateShutdown.v1." + parent.Id);
            exitSignal.Set();
            Log("Sent graceful update shutdown to original AAH PID " + parent.Id +
                "; waiting for its own import/export/Tally cleanup.");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Log("AAH update-shutdown event absent. Update deferred; no forced termination.");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Log("AAH update-shutdown signal access denied; no forced termination.");
            return false;
        }
        return await WaitForOriginalExitAsync(parent, timeout);
    }

    private static async Task<bool> WaitForOriginalExitAsync(Process parent, TimeSpan timeout)
    {
        try
        {
            using var stop = new CancellationTokenSource(timeout);
            await parent.WaitForExitAsync(stop.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task<bool> StartAndVerifyAsync(string exe, string install)
    {
        var token = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
            @"Local\AccountingAutomationHub.Ready." + token);
        using var launched = Process.Start(new ProcessStartInfo(exe) {
            WorkingDirectory = install, UseShellExecute = false,
            ArgumentList = { "--update-ready=" + token }
        });
        if (launched is null) return false;
        var limit = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < limit)
        {
            if (ready.WaitOne(300))
            {
                // A transient startup signal is not enough; process must remain alive.
                await Task.Delay(2000);
                if (!launched.HasExited)
                {
                    Log("Verified restart ready: new AAH PID " + launched.Id +
                        " signaled loaded UI and remained active.");
                    return true;
                }
                Log("Restart readiness signal arrived but the new AAH process exited.");
                return false;
            }
            if (launched.HasExited)
            {
                Log("Restart process exited before sending ready signal (PID " + launched.Id +
                    "); another stale instance may own the single-instance mutex.");
                return false;
            }
            await Task.Delay(100);
        }
        Log("New AAH did not signal startup readiness within 3 minutes; requesting its own graceful shutdown.");
        // Do not overwrite a still-running binary. Require a safe exit to roll back.
        if (!launched.HasExited)
        {
            Log("New AAH remained running without readiness; attempting process-targeted shutdown.");
            try
            {
                using var stop = EventWaitHandle.OpenExisting(
                    @"Local\AccountingAutomationHub.UpdateShutdown.v1." + launched.Id);
                stop.Set();
                await WaitForOriginalExitAsync(launched, TimeSpan.FromSeconds(30));
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        if (!launched.HasExited)
            throw new UpdateRuntimeBusyException("New AAH did not exit; rollback deferred to protect running process.");
        return false;
    }

    private static void StartWithoutReadiness(string exe, string install)
    {
        try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = install, UseShellExecute = false }); }
        catch (Exception e) { Log("Could not reopen original application after rollback: " + e.Message); }
    }

    private static bool SameProcessPath(Process process, string expected)
    {
        try { return Path.GetFullPath(process.MainModule?.FileName ?? "").
            Equals(expected, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    internal static bool PublisherIdentityCompatible(string? expected, string? actual) =>
        (expected is null && actual is null) ||
        (expected is not null && actual is not null &&
         expected.Equals(actual, StringComparison.OrdinalIgnoreCase));

    private static async Task<string?> VerifiedPublisherAsync(string path)
    {
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("$s=Get-AuthenticodeSignature -LiteralPath $env:AAH_UPDATE_VERIFY;" +
            "if($s.Status -eq 'Valid' -and $s.SignerCertificate){" +
            "[Console]::Out.Write($s.SignerCertificate.Thumbprint);exit 0};exit 1");
        info.Environment["AAH_UPDATE_VERIFY"]=path;
        using var proc = Process.Start(info);
        if (proc is null) return null;
        var result = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var output = result.Trim();
        return proc.ExitCode == 0 && Regex.IsMatch(output, @"\A[a-fA-F0-9]{40}\z")
            ? output.ToUpperInvariant() : null;
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}
