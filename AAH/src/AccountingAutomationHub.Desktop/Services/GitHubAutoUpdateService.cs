using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
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
/// A version is never installed unless a signed installed AAH and its signed updater
/// share the same trusted Authenticode signer, release metadata is complete,
/// the asset hash matches, and the updater supports the AAH automatic-update protocol.
/// </summary>
internal static class GitHubAutoUpdateService
{
    internal const string ReleaseApi =
        "https://api.github.com/repos/surajTech-22/AccountingAutomationHub-Windows/releases/latest";
    internal const string UpdateAssetName = "AAH_Update.exe";
    internal const string ChecksumAssetName = "AAH_Update.exe.sha256";
    internal const string ProtocolAssetName = "AAH_AutoUpdate.json";
    internal const string Protocol = "AAH-GITHUB-AUTOUPDATE-v1";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly HttpClient Client = CreateClient();
    private static int _started;
    private static int _updateLaunched;

    internal static void Start(bool isLanClient)
    {
        if (isLanClient || Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(CheckLoopAsync);
    }

    private static async Task CheckLoopAsync()
    {
        // Always run outside the UI dispatcher; failures never block AAH startup or imports.
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try { await CheckOnceAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log("check failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false));
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
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
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
        // The current executable must have an independently valid publisher signature;
        // an unsigned legacy install needs its one-time trusted installer bootstrap.
        var installedExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(installedExe) || !File.Exists(installedExe)) return;
        var signer = await GetVerifiedPublisherAsync(installedExe).ConfigureAwait(false);
        if (signer is null) { Log("disabled until the installed AAH executable is Authenticode-signed"); return; }

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
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccountingAutomationHub", "GitHubAutoUpdate", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, UpdateAssetName);
            using (var download = await Client.GetAsync(updateUrl!, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                download.EnsureSuccessStatusCode();
                if (download.Content.Headers.ContentLength is > 400_000_000)
                    throw new InvalidDataException("Updater exceeds the 400MB maximum.");
                await using var source = await download.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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
            await using (var stream = File.OpenRead(path))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
                if (!hash.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded updater SHA256 verification failed.");
            }
            var updateSigner = await GetVerifiedPublisherAsync(path).ConfigureAwait(false);
            if (!string.Equals(signer, updateSigner, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Updater signing publisher differs from installed AAH.");
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = root
            };
            // The *signed* first-party updater must implement this exact protocol.
            // It owns the safe accounting-queue exit, application-only file swap,
            // process-specific shutdown/restart, health check and rollback.
            psi.ArgumentList.Add("--aah-auto-update-v1");
            psi.ArgumentList.Add("--target-exe=" + installedExe);
            psi.ArgumentList.Add("--parent-pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--release=" + tag);
            if (Process.Start(psi) is null)
                throw new InvalidOperationException("Could not launch the verified updater.");
            Log("launched signed AAH updater for " + tag);
        }
        catch
        {
            Interlocked.Exchange(ref _updateLaunched, 0);
            throw;
        }
    }

    private static async Task<string?> GetVerifiedPublisherAsync(string file)
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
