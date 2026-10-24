using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Public GitHub stable-release updater for the local MainServer/standalone desktop.
/// LAN clients must keep using their separately enrolled Main Server update channel.
/// Uses the same public-GitHub release + SHA256 distribution model as DeskSpace.
/// If *both* executables are Authenticode signed they must share the same
/// trusted certificate; two unsigned executables use pinned GitHub HTTPS
/// release URLs and SHA256-verified owner-controlled release assets.
/// This is weaker than code signing and requires protecting repository releases.
/// </summary>
internal static class GitHubAutoUpdateService
{
    internal const string ReleaseApi =
        "https://api.github.com/repos/surajTech-22/AccountingAutomationHub-Windows/releases/latest";
    internal const string UpdateAssetName = "AAH_Update.exe";
    internal const string ChecksumAssetName = "AAH_Update.exe.sha256";
    internal const string ProtocolAssetName = "AAH_AutoUpdate.json";
    internal const string Protocol = "AAH-GITHUB-AUTOUPDATE-v1";

    // A signal is only a wakeup hint; every update must still pass the exact
    // GitHub release, protocol, publisher and SHA256 checks below.
    internal const string ReleaseSignalUrl =
        "https://ntfy.sh/aah-surajtech22-windows-release-signal-v1/sse?since=now";
    private static readonly HttpClient Client = CreateClient();
    private static int _started;
    private static int _updateLaunched;
    private static int _duplicateInstallationNotified;

    internal static void Start(bool isLanClient)
    {
        if (isLanClient || Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(CheckLoopAsync);
    }

    private static async Task CheckLoopAsync()
    {
        // One startup check catches releases published while this PC was offline.
        // Afterwards subscribe to release-published events: NO timed GitHub polling.
        try { await CheckOnceAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log("startup check failed: " + ex.Message); }
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseSignalUrl);
                using var response = await Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    try
                    {
                        using var notice = JsonDocument.Parse(line.Substring(6));
                        var payload = notice.RootElement;
                        if (!payload.TryGetProperty("event", out var kind) ||
                            kind.GetString() != "message" ||
                            !payload.TryGetProperty("message", out var message) ||
                            ParseStableVersion(message.GetString()) is null)
                            continue;
                        await CheckOnceAsync().ConfigureAwait(false);
                    }
                    catch (JsonException) { /* ignore malformed relay notifications */ }
                }
            }
            catch (Exception ex)
            {
                Log("release event connection interrupted: " + ex.Message);
            }
            // Reconnect only after network disruption. Re-check once to recover
            // a release that arrived while the connection was unavailable.
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            try { await CheckOnceAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log("reconnect check failed: " + ex.Message); }
        }
    }

    internal static Version? ParseStableVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) ||
            !Regex.IsMatch(tag, @"^v?[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant))
            return null;
        return Version.TryParse(tag.TrimStart('v'), out var version) ? version : null;
    }

    internal static bool IsNewer(string? candidate, Version installed) =>
        ParseStableVersion(candidate) is { } version && version > installed;

    internal static bool IsValidSha256(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);

    internal static string? ParseChecksum(string content)
    {
        // sha256sum's one-file format, or a bare digest. Never accept unrelated entries.
        var match = Regex.Match(content.Trim(),
            @"\A([a-fA-F0-9]{64})(?:[ \t]+\*?AAH_Update\.exe)?\s*\z",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    internal static bool IsTrustedAssetUrl(string? value, string tag, string filename)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        // Only download the exact asset for the selected release; no untrusted host/path.
        return uri.AbsolutePath.Equals(
            "/surajTech-22/AccountingAutomationHub-Windows/releases/download/" +
            Uri.EscapeDataString(tag) + "/" + filename, StringComparison.Ordinal);
    }

    internal static bool IsProtocolAccepted(string json, string tag, string sha256)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("protocol", out var p) && p.GetString() == Protocol &&
                root.TryGetProperty("release", out var r) && r.GetString() == tag &&
                root.TryGetProperty("asset", out var a) && a.GetString() == UpdateAssetName &&
                root.TryGetProperty("sha256", out var h) &&
                h.GetString()?.Equals(sha256, StringComparison.OrdinalIgnoreCase) == true &&
                root.TryGetProperty("repository", out var repo) &&
                repo.GetString() == "surajTech-22/AccountingAutomationHub-Windows" &&
                root.TryGetProperty("verification", out var verification) &&
                verification.GetString() == "github-release-sha256-v1" &&
                root.TryGetProperty("automatic_install_allowed", out var allowed) &&
                allowed.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AAH-Windows-AutoUpdate/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task CheckOnceAsync()
    {
        // The initial GitHub bootstrap must be installed once; existing legacy
        // installs cannot acquire this checker without replacing the old app.
        var installedExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(installedExe) || !File.Exists(installedExe)) return;
        if (HasAnotherAahInstallation())
        {
            if (Interlocked.Exchange(ref _duplicateInstallationNotified, 1) == 0)
                Log("Auto-update paused: another AAH installer identity exists. Keep both copies and data intact pending supervised migration.");
            return;
        }
        Interlocked.Exchange(ref _duplicateInstallationNotified, 0);
        var signer = await GetVerifiedPublisherAsync(installedExe).ConfigureAwait(false);

        using var response = await Client.GetAsync(ReleaseApi).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return; // no stable release yet
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var release = doc.RootElement;
        if (release.GetProperty("draft").GetBoolean() ||
            release.GetProperty("prerelease").GetBoolean()) return;
        var tag = release.GetProperty("tag_name").GetString();
        var installedVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        if (tag is null || installedVersion is null || !IsNewer(tag, installedVersion)) return;

        var assets = release.GetProperty("assets");
        if (assets.ValueKind != JsonValueKind.Array) return;
        string? FindAsset(string name)
        {
            foreach (var asset in assets.EnumerateArray())
                if (asset.GetProperty("name").GetString() == name)
                    return asset.GetProperty("browser_download_url").GetString();
            return null;
        }
        var updateUrl = FindAsset(UpdateAssetName);
        var checksumUrl = FindAsset(ChecksumAssetName);
        var protocolUrl = FindAsset(ProtocolAssetName);
        if (!IsTrustedAssetUrl(updateUrl, tag, UpdateAssetName) ||
            !IsTrustedAssetUrl(checksumUrl, tag, ChecksumAssetName) ||
            !IsTrustedAssetUrl(protocolUrl, tag, ProtocolAssetName))
        {
            Log("release " + tag + " lacks approved automatic-update protocol assets");
            return;
        }

        var checksum = ParseChecksum(await Client.GetStringAsync(checksumUrl!).ConfigureAwait(false));
        if (!IsValidSha256(checksum)) { Log("invalid SHA256 checksum asset"); return; }
        var protocolJson = await Client.GetStringAsync(protocolUrl!).ConfigureAwait(false);
        if (!IsProtocolAccepted(protocolJson, tag, checksum!))
        {
            Log("release protocol validation failed: " + tag);
            return;
        }
        if (Interlocked.CompareExchange(ref _updateLaunched, 1, 0) != 0) return;
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccountingAutomationHub", "GitHubAutoUpdate");
        // Keep only one updater per release, rather than accumulating a new
        // random GUID folder each time the application checks for an update.
        var root = GetReleaseCacheDirectory(cacheRoot, tag);
        var incoming = Path.Combine(root, UpdateAssetName + ".pending.exe");
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, UpdateAssetName);
            // Download to a separate file, so an interrupted download never
            // replaces the previously verified updater executable.
            using (var download = await Client.GetAsync(updateUrl!, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                download.EnsureSuccessStatusCode();
                if (download.Content.Headers.ContentLength is > 400_000_000)
                    throw new InvalidDataException("Updater exceeds the 400MB maximum.");
                await using var source = await download.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var destination = new FileStream(incoming, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > 400_000_000) throw new InvalidDataException("Updater exceeds 400MB maximum.");
                    await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
            }
            await using (var stream = File.OpenRead(incoming))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
                if (!hash.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded updater SHA256 verification failed.");
            }
            var updateSigner = await GetVerifiedPublisherAsync(incoming).ConfigureAwait(false);
            if (!PublisherIdentityCompatible(signer, updateSigner))
                throw new InvalidDataException("Updater has an unexpected Authenticode signing identity.");
            // As in manual update mode, replace the OLD updater bytes only after
            // verifying the complete incoming file. A running previous updater is
            // left untouched; Windows will refuse overwriting its executable.
            File.Move(incoming, path, overwrite: true);
            CleanupStaleUpdaterFolders(cacheRoot, tag);
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = root
            };
            psi.ArgumentList.Add("--aah-auto-update-v1");
            psi.ArgumentList.Add("--target-exe=" + installedExe);
            psi.ArgumentList.Add("--parent-pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--release=" + tag);
            if (Process.Start(psi) is null)
                throw new InvalidOperationException("Could not launch the verified updater.");
            Log("launched GitHub SHA256-verified AAH updater for " + tag);
        }
        catch
        {
            try { if (File.Exists(incoming)) File.Delete(incoming); } catch { }
            Interlocked.Exchange(ref _updateLaunched, 0);
            throw;
        }
    }

    // The same identity guard exists in AAH_GitHubSetup.iss. Two separately
    // registered installations mean no in-place update is safe until their
    // registry AppIds and actual executable/data paths are reconciled.
    internal static bool IsForeignAahRegistration(string? displayName, string? registryId) =>
        !string.IsNullOrWhiteSpace(displayName) &&
        (displayName.StartsWith("Accounting Automation H", StringComparison.OrdinalIgnoreCase) ||
         displayName.Equals("AAH", StringComparison.OrdinalIgnoreCase) ||
         displayName.StartsWith("AAH ", StringComparison.OrdinalIgnoreCase)) &&
        (registryId?.Contains("D8E4758C-09F1-45BF-831A-59C22B79DC04",
            StringComparison.OrdinalIgnoreCase) != true);

    private static bool HasAnotherAahInstallation()
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var id in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(id);
                    var display = entry?.GetValue("DisplayName") as string;
                    // Existing AAH v4.0.0.502 was registered under the original
                    // AccountingAutomationHub ID, not the later Inno AppId.
                    // After a verified IN-PLACE upgrade, the original registry
                    // ID is still correct. Compare EXE paths, never AppId alone.
                    var isOriginalId = id.Equals("AccountingAutomationHub", StringComparison.OrdinalIgnoreCase);
                    var isGitHubId = id.Contains("D8E4758C-09F1-45BF-831A-59C22B79DC04", StringComparison.OrdinalIgnoreCase);
                    if (isOriginalId || isGitHubId)
                    {
                        var folder = entry?.GetValue("InstallLocation") as string;
                        var currentExe = Environment.ProcessPath;
                        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(currentExe) ||
                            !Path.GetFullPath(Path.Combine(folder, "AccountingAutomationHub.exe"))
                                .Equals(Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase))
                            return true; // another directory: actual duplicate
                        continue; // same original install, no second application
                    }
                    if (IsForeignAahRegistration(display, id))
                        return true;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log("Cannot verify AAH installation uniqueness: " + ex.GetType().Name);
                return true; // Fail closed; do not change accounting application files.
            }
        }
        return false;
    }

    internal static string GetReleaseCacheDirectory(string cacheRoot, string releaseTag)
    {
        if (ParseStableVersion(releaseTag) is null || !releaseTag.StartsWith("v", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid GitHub update-cache version.");
        return Path.Combine(cacheRoot, releaseTag);
    }

    internal static int CleanupStaleUpdaterFolders(string cacheRoot, string keepTag)
    {
        if (ParseStableVersion(keepTag) is null || !Directory.Exists(cacheRoot)) return 0;
        int removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(cacheRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals(keepTag, StringComparison.OrdinalIgnoreCase))
                continue;
            // Older revisions used GUID names; current revisions use release tags.
            // Ignore unrelated directories (which may contain private workspace data).
            if (ParseStableVersion(name) is null &&
                !Regex.IsMatch(name, @"\A[a-fA-F0-9]{32}\z", RegexOptions.CultureInvariant))
                continue;
            try
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    continue;
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (IOException) { /* Previous updater still running or files in use. */ }
            catch (UnauthorizedAccessException) { /* Never force-delete a locked updater. */ }
        }
        return removed;
    }

    internal static bool PublisherIdentityCompatible(string? installed, string? updater) =>
        (installed is null && updater is null) ||
        (installed is not null && updater is not null &&
         installed.Equals(updater, StringComparison.OrdinalIgnoreCase));

    internal static async Task<string?> GetVerifiedPublisherAsync(string file)
    {
        // OS Authenticode trust must be valid, not merely a certificate attached to a file.
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            "$s=Get-AuthenticodeSignature -LiteralPath $env:AAH_VERIFY_EXE;" +
            "if ($s.Status -eq 'Valid' -and $null -ne $s.SignerCertificate) {" +
            "[Console]::Out.Write($s.SignerCertificate.Thumbprint);exit 0};exit 1");
        psi.Environment["AAH_VERIFY_EXE"] = file;
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        var thumbprint = output.Trim();
        return proc.ExitCode == 0 && Regex.IsMatch(thumbprint, @"\A[A-Fa-f0-9]{40}\z")
            ? thumbprint.ToUpperInvariant() : null;
    }

    private static void Log(string line)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccountingAutomationHub", "AutoUpdateLogs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "github-update.log"),
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { /* Update check failures never affect accounting runtime. */ }
    }
}
