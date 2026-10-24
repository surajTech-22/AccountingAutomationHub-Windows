using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aah.UpdateHost;

// This executable is only eligible for unattended installation once Authenticode
// signed and distributed alongside an approved signed AAH application release.
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
            if (publisher is null || publisher != installedPublisher)
                throw new InvalidDataException("Publisher verification failed: both updater and AAH must have a valid matching Authenticode signature.");

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
                if (!File.Exists(stagedExe) || await VerifiedPublisherAsync(stagedExe) != publisher)
                    throw new InvalidDataException("The release contains no matching signed AAH application.");
                var installedVersion = FileVersionInfo.GetVersionInfo(target).FileVersion;
                var newVersion = FileVersionInfo.GetVersionInfo(stagedExe).FileVersion;
                if (!Version.TryParse(installedVersion, out var oldVer) ||
                    !Version.TryParse(newVersion, out var nextVer) || nextVer <= oldVer ||
                    nextVer.ToString() != tag.TrimStart('v'))
                    throw new InvalidDataException("Application binary version does not match an upgrade release.");

                // Intentionally never terminate an AAH process during accounting work.
                // Download/verify now, install only after AAH exits normally. This
                // preserves Tally queues and outstanding import/export operations.
                Log("Release " + tag + " verified. Waiting for AAH to exit normally; no forced shutdown.");
                if (!await WaitForOriginalExitAsync(parent, TimeSpan.FromHours(12)))
                {
                    Log("AAH did not close within the update window. No files changed.");
                    return 0;
                }

                var transaction = StageTransaction(staged, install);
                try
                {
                    ApplyTransaction(transaction, install);
                    if (!await StartAndVerifyAsync(target, install))
                    {
                        Log("New AAH did not signal readiness; restoring previous application files.");
                        RollbackTransaction(transaction, install);
                        StartWithoutReadiness(target, install);
                        throw new InvalidOperationException("Update rolled back after failed AAH startup.");
                    }
                    File.WriteAllText(Path.Combine(transaction, "committed"), "OK");
                    Directory.Delete(transaction, true);
                    Log("SUCCESS: AAH " + tag + " restarted and application transaction committed.");
                }
                catch (UpdateRuntimeBusyException)
                {
                    // A running app can hold open DLLs. Keep the transaction for
                    // explicit recovery; never rewrite binaries underneath it.
                    Log("ROLLBACK DEFERRED: new AAH remains running; recovery journal retained.");
                    throw;
                }
                catch
                {
                    if (Directory.Exists(transaction) &&
                        !File.Exists(Path.Combine(transaction, "committed")))
                    {
                        try { RollbackTransaction(transaction, install); }
                        catch (Exception rollbackError) { Log("CRITICAL rollback needs attention: " + rollbackError); }
                    }
                    throw;
                }
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

    private sealed class UpdateRuntimeBusyException(string message) : Exception(message);
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
        var limit = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < limit)
        {
            if (ready.WaitOne(300))
            {
                // A transient startup signal is not enough; process must remain alive.
                await Task.Delay(2000);
                return !launched.HasExited;
            }
            if (launched.HasExited) return false;
            await Task.Delay(100);
        }
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
