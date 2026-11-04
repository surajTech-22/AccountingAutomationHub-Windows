using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
    // PIDs of terminated WPF applications can remain in Windows process
    // enumeration (CL019: 15308, 2652, 2888), even after HasExited=true.
    // Do NOT rely on a process name/census entry alone as proof of a live
    // process. A signaled native process handle proves the kernel process
    // has terminated, regardless of a stale CIM/process-name listing.
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;

    private const uint SynchronizeAccess = 0x00100000;
    private const uint QueryLimitedInformationAccess = 0x1000;
    private const int ErrorInvalidParameter = 87;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // The v595 CL019 failure was Process.Handle throwing
    // InvalidOperationException AFTER Process.HasExited == true, while the
    // process-name snapshot still listed PID 15308. OpenProcess is a native
    // independent observation and does not call .NET Process.Handle.
    //
    // Signaled kernel handle OR a native PID-not-found result (87) can prove
    // a PID already exited, but ONLY when .NET separately reports HasExited.
    // Access denied, a still-live handle and unknown errors are all blockers.
    internal static bool IsConfirmedKernelTermination(bool managedHasExited, uint nativeWaitResult) =>
        managedHasExited && nativeWaitResult == WaitObject0;

    internal static bool IsConfirmedKernelTermination(
        bool managedHasExited, bool nativeOpenSucceeded, uint nativeWaitResult, int nativeOpenError) =>
        managedHasExited &&
        (nativeOpenSucceeded
            ? nativeWaitResult == WaitObject0
            : nativeOpenError == ErrorInvalidParameter);

    internal static bool IsConfirmedKernelTermination(Process process)
    {
        if (!OperatingSystem.IsWindows() || !process.HasExited) return false;
        var pid = process.Id;
        // Do not use process.Handle: its getter explicitly refuses to return a
        // kernel handle for a Process already reported as exited on .NET 10.
        var handle = OpenProcess(SynchronizeAccess | QueryLimitedInformationAccess, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (IsConfirmedKernelTermination(true, false, WaitFailed, error))
            {
                Log("AAH PID " + pid +
                    " is an already-exited managed process and Windows OpenProcess " +
                    "confirms the PID no longer exists. Excluding stale enumeration.");
                return true;
            }
            throw new Win32Exception(error,
                "Native OpenProcess could not verify AAH PID " + pid +
                "; refusing to treat an inaccessible or unknown process as exited");
        }
        try
        {
            var result = WaitForSingleObject(handle, 0);
            if (result == WaitFailed)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Native wait could not verify AAH PID " + pid);
            if (result != WaitObject0 && result != WaitTimeout)
                throw new InvalidDataException(
                    "Unexpected native process wait state " + result + " for AAH PID " + pid);
            return IsConfirmedKernelTermination(true, true, result, 0);
        }
        finally { CloseHandle(handle); }
    }
    private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccountingAutomationHub", "AutoUpdateLogs", "github-update-host.log");

    /// <summary>
    /// Cross-process exclusive lease for the entire ASYNC updater lifetime.
    /// A named mutex can EXIST without being OWNED; creation status is not
    /// lock status. Only a WaitOne(0) result (or abandoned ownership) decides.
    /// The owner thread stays alive across all awaited download / extraction /
    /// Tally drainage / transactional install phases, and always releases
    /// the mutex from that SAME thread on disposal.
    /// </summary>
    internal static IDisposable? TryAcquireUpdateOwnership(string mutexName)
    {
        var lease = new UpdateOwnershipLease(mutexName);
        if (lease.Acquired) return lease;
        lease.Dispose();
        return null;
    }

    private sealed class UpdateOwnershipLease : IDisposable
    {
        private readonly string _name;
        private readonly ManualResetEventSlim _started = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _owner;
        private bool _acquired;
        private Exception? _error;
        private int _disposed;

        internal bool Acquired => _acquired;

        internal UpdateOwnershipLease(string mutexName)
        {
            _name = mutexName;
            _owner = new Thread(OwnForLifetime)
            {
                IsBackground = true,
                Name = "AAH verified updater mutex owner"
            };
            _owner.Start();
            if (!_started.Wait(TimeSpan.FromSeconds(10)))
            {
                _release.Set();
                throw new TimeoutException(
                    "Could not check actual updater mutex ownership in 10 seconds.");
            }
            if (_error is not null)
            {
                _release.Set();
                _owner.Join(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException(
                    "Could not verify the updater lock safely.", _error);
            }
        }

        private void OwnForLifetime()
        {
            try
            {
                using var mutex = new Mutex(false, _name);
                var owns = false;
                try { owns = mutex.WaitOne(0); }
                catch (AbandonedMutexException)
                {
                    // Prior updater exited without releasing its owned lock.
                    // .NET transfers ownership to this exact thread; this is
                    // safe and does NOT authorize terminating another PID.
                    owns = true;
                }
                _acquired = owns;
                _started.Set();
                if (!owns) return;
                try { _release.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex)
            {
                _error = ex;
                _started.Set();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _release.Set();
            if (!_owner.Join(TimeSpan.FromSeconds(15)))
                throw new TimeoutException(
                    "Updater mutex-owner thread did not release within 15 seconds.");
            _started.Dispose();
            _release.Dispose();
        }
    }

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

            // v592 local recovery exposed a long-standing mutex bug:
            // createdNew=false only means the named kernel object already
            // EXISTS. A lingering/cleaned updater can leave that object alive
            // WITHOUT owning it. Rejecting on createdNew prevented v592 from
            // installing even though a read-only WaitOne(0) proved it free.
            //
            // A Mutex is also owned by a THREAD, not an async Task. Do not
            // acquire it here and later release on a different thread after
            // an await. The lifetime lease below owns/releases on ONE
            // dedicated thread, while this async installer runs normally.
            using var oneUpdate = TryAcquireUpdateOwnership(
                @"Local\AccountingAutomationHub.GitHubUpdate.v1");
            if (oneUpdate is null)
                throw new InvalidOperationException(
                    "Another AAH updater currently OWNS the update mutex; " +
                    "no competing installer will start.");
            // Always log the FIRST preflight stage. Previously no host message
            // appeared until after the entire application archive had arrived,
            // making a stalled download indistinguishable from a shutdown hang.
            Log("UpdateHost acquired exclusive mutex for " + tag +
                ". Starting verified release preflight; installed files unchanged.");
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Updater process path missing.");
            var publisher = await VerifiedPublisherAsync(self);
            var installedPublisher = await VerifiedPublisherAsync(target);
            if (!PublisherIdentityCompatible(publisher, installedPublisher))
                throw new InvalidDataException("Updater and installed AAH have incompatible Authenticode signing identities.");

            // AAH can close normally between spawning its verified updater
            // and the updater finishing its launch/HTTPS preflight. The PID is
            // a launch-time provenance hint, NOT a perpetual lifetime lock:
            // do not abandon a validated release merely because that PID
            // has already exited. A PID that still exists but belongs to an
            // unrelated executable MUST remain a hard failure. Every running
            // exact-install AAH instance is independently path-verified and
            // asked to exit before touching program files.
            if (!LauncherExitedOrBelongsToInstallation(parentId, target))
                throw new InvalidDataException(
                    "Updater launcher PID is active but no longer belongs to this AAH installation.");

            Log("UpdateHost querying latest stable GitHub release for " + tag +
                "; installed files unchanged.");
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
            Log("UpdateHost verified " + tag + " release manifest. Downloading " +
                AssetName + " with absolute and idle timeouts; application still running.");

            var scratch = Path.Combine(Path.GetTempPath(), "AAH-GitHub-Release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var archivePath = Path.Combine(scratch, AssetName);
                await DownloadVerifiedAsync(archiveUrl, archivePath, expectedHash);
                // All of these stages happen BEFORE the first request to
                // close AAH. Bound each one and record start/finish so a slow
                // disk, antivirus or PowerShell signature check cannot hold
                // the exclusive updater mutex silently and indefinitely.
                var staged = Path.Combine(scratch, "publish");
                Log("Release " + tag + " SHA256 verified; starting bounded ZIP extraction.");
                using (var extractDeadline = new CancellationTokenSource(AppArchiveExtractionDeadline))
                {
                    try { await ExtractStrictAsync(archivePath, staged, extractDeadline.Token); }
                    catch (OperationCanceledException) when (extractDeadline.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            "Updater ZIP extraction exceeded " + AppArchiveExtractionDeadline +
                            " before any application shutdown. Installed AAH unchanged.");
                    }
                }
                Log("Release " + tag + " extraction complete; validating staged EXE publisher/version.");
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
                var restartedPid = await StartAndVerifyAsync(target, install);
                if (restartedPid is null)
                {
                    // The original manual updater has already committed verified
                    // bytes. Never claim health-check rollback without restoration.
                    Log("Updated AAH startup readiness was not confirmed. The original manual updater committed installed bytes; intervention may be needed.");
                    throw new InvalidOperationException("AAH updated, but the restart readiness check failed.");
                }
                // A startup ready signal alone did NOT prove a clean update on CL019:
                // v593 PID 2652 was ready, while v589 PID 15308 remained listed.
                // Retire ONLY the existing strictly-audited, completed-cleanup
                // ghost; never kill an active app or Tally to obtain a green result.
                if (!await VerifySingleRestartedInstallationAsync(target, restartedPid.Value))
                {
                    Log("Updated AAH is on disk but post-restart exact-install process census failed. " +
                        "Do not claim a healthy single-instance update; accounting programs remain untouched.");
                    throw new InvalidOperationException(
                        "AAH updated, but multiple/stale AAH processes or missing restarted PID were detected.");
                }
                // With the new release verified and the real AAH restarted, retire
                // obsolete *live* updater hosts from this exact GitHub cache.
                // Terminated Windows/CIM ghosts are already dead and cannot be killed.
                if (!await RetireObsoleteUpdateHostsAfterVerifiedRestartAsync(target))
                    Log("WARNING: at least one obsolete updater host was not safely retired; installed AAH is healthy.");
                Log(restartOnly
                    ? "SUCCESS: AAH " + tag + " already installed; all stale instances exited and existing executable restarted."
                    : "SUCCESS: AAH " + tag + " replaced in place by original manual updater and restarted.");
                return 0;
            }
            finally
            {
                // This is temporary preflight staging only, never a Tally/
                // accounting directory. An antivirus/disk filter can also
                // hang recursive cleanup; don't keep the updater mutex alive
                // forever AFTER a failed bounded verification.
                await CleanupScratchBoundedAsync(scratch);
            }
        }
        catch (Exception exception)
        {
            Log("FAILED (no success claimed): " + exception);
            return 1;
        }
    }

    internal static async Task CleanupScratchBoundedAsync(string scratch)
    {
        try
        {
            if (!Directory.Exists(scratch)) return;
            await Task.Run(() => Directory.Delete(scratch, true))
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            // Stale random temp staging is safer than indefinitely holding the
            // exclusive update mutex. Next update will use a fresh GUID path.
            Log("Preflight temp cleanup exceeded 30 seconds; deferring only " +
                "the disposable scratch directory; application data untouched.");
        }
        catch (IOException ex)
        {
            Log("Preflight temp cleanup deferred (locked scratch file): " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log("Preflight temp cleanup deferred (access denied): " + ex.Message);
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

    // ResponseHeadersRead bypasses HttpClient's normal response-body timeout.
    // Every socket read must be bounded: never hold the updater mutex forever
    // when a router/DNS/content-host connection stalls after the HTTP headers.
    // A failed download is only a staging failure; no installed files are touched.
    internal static readonly TimeSpan AppArchiveDownloadDeadline = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan AppArchiveIdleTimeout = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan AppArchiveSha256Deadline = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan AppArchiveExtractionDeadline = TimeSpan.FromMinutes(8);
    internal static readonly TimeSpan PublisherVerificationDeadline = TimeSpan.FromSeconds(90);

    private static async Task DownloadVerifiedAsync(Uri uri, string destination, string sha)
    {
        using var deadline = new CancellationTokenSource(AppArchiveDownloadDeadline);
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        stalled.CancelAfter(AppArchiveIdleTimeout);
        var received = 0L;
        var lastLogged = 0L;
        try
        {
            using var response = await Http.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 650_000_000)
                throw new InvalidDataException("Excessive application update size.");
            var expectedSize = response.Content.Headers.ContentLength;
            Log("Updater archive response accepted. Expected bytes: " +
                (expectedSize?.ToString(CultureInfo.InvariantCulture) ?? "unknown") +
                "; downloaded bytes initially zero.");
            await using (var src = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var dest = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                int n;
                while (true)
                {
                    stalled.CancelAfter(AppArchiveIdleTimeout);
                    n = await src.ReadAsync(buffer.AsMemory(), stalled.Token);
                    if (n == 0) break;
                    received += n;
                    if (received > 650_000_000)
                        throw new InvalidDataException("Excessive application update size.");
                    await dest.WriteAsync(buffer.AsMemory(0, n), deadline.Token);
                    if (received - lastLogged >= 16L * 1024 * 1024)
                    {
                        Log("Updater application archive progress " +
                            received.ToString(CultureInfo.InvariantCulture) + "/" +
                            (expectedSize?.ToString(CultureInfo.InvariantCulture) ?? "?") + " bytes.");
                        lastLogged = received;
                    }
                }
            }
            if (expectedSize is long exact && received != exact)
                throw new InvalidDataException(
                    "Incomplete application download: " + received + " of " + exact + " bytes.");
            Log("Updater application archive received " + received +
                " bytes. Verifying SHA256 before any application shutdown.");
            await VerifyDownloadedArchiveSha256Async(destination, sha);
            Log("PASS: update application ZIP SHA256 verified (" +
                received.ToString(CultureInfo.InvariantCulture) + " bytes); installed app untouched.");
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "AAH release archive download exceeded the 15-minute absolute limit at " +
                received + " bytes. No installed files changed; updater lock will release.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(
                "AAH release archive download made no progress for 90 seconds at " +
                received + " bytes. No installed files changed; updater lock will release.", ex);
        }
    }

    internal static async Task VerifyDownloadedArchiveSha256Async(
        string path, string expectedSha, TimeSpan? maximumDuration = null)
    {
        var limit = maximumDuration ?? AppArchiveSha256Deadline;
        using var deadline = new CancellationTokenSource(limit);
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(file, deadline.Token)).ToLowerInvariant();
            if (!actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded application archive SHA256 mismatch.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Updater SHA256 verification exceeded " + limit +
                " before any application shutdown. Installed AAH unchanged.");
        }
    }

    // Keep the strict synchronous form for existing safety contract tests.
    // Production ALWAYS calls the bounded async version.
    internal static void ExtractStrict(string zipPath, string dest) =>
        ExtractStrictAsync(zipPath, dest, CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task ExtractStrictAsync(
        string zipPath, string dest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dest);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 2500) throw new InvalidDataException("Too many release files.");
        long bytes = 0;
        long extracted = 0;
        long lastLoggedBytes = 0;
        var processed = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            await using (var input = entry.Open())
            await using (var output = new FileStream(absolute, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                long fileBytes = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var n = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (n == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                    fileBytes += n;
                    extracted += n;
                    if (extracted - lastLoggedBytes >= 64L * 1024 * 1024)
                    {
                        Log("Update preflight extraction progress: " + processed +
                            " complete files, " +
                            extracted.ToString(CultureInfo.InvariantCulture) +
                            " bytes (including current file). Installed AAH untouched.");
                        lastLoggedBytes = extracted;
                    }
                }
                await output.FlushAsync(cancellationToken);
                if (fileBytes != entry.Length)
                    throw new InvalidDataException(
                        "Incomplete updater ZIP member " + relative);
            }
            processed++;
            if (processed % 100 == 0)
                Log("Update preflight extraction progress: " + processed +
                    " files, " + extracted.ToString(CultureInfo.InvariantCulture) +
                    " bytes. No installed files touched.");
        }
        Log("PASS: updater ZIP extracted " + processed + " files / " +
            extracted.ToString(CultureInfo.InvariantCulture) +
            " bytes; no application shutdown yet.");
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
    /// User-selected v597 update policy:
    /// - ONE real active AAH process (prefer its registered shutdown listener)
    ///   closes gracefully, preserving its normal import/Tally drain.
    /// - Other LIVE processes of the exact SAME installation are forced closed.
    /// - Already-exited .NET process objects are NOT alive. Windows may retain
    ///   their one-thread PID records for hours; never treat those as blockers
    ///   or attempt to kill them.
    /// - Foreign installations, Tally and updater hosts are never targeted.
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
                    Log("All live exact-install AAH processes exited; replacement permitted.");
                    return true;
                }
                var primaryPid = SelectPrimaryGracefulPid(processes.Select(p =>
                    (pid: p.Id, hasListener: HasRegisteredShutdownListener(p.Id),
                     hasMainWindow: p.MainWindowHandle != IntPtr.Zero,
                     startedTicks: p.StartTime.ToUniversalTime().Ticks)).ToArray());
                Log("Update shutdown pass " + pass + ": primary AAH PID " + primaryPid +
                    " will close gracefully; forcing ONLY " +
                    string.Join(",", processes.Where(p => p.Id != primaryPid).Select(p => p.Id)) +
                    " secondary exact-install AAH process(es).");

                // Force secondary processes FIRST, so they cannot interfere with
                // the real primary's own verified graceful accounting shutdown.
                foreach (var process in processes.Where(p => p.Id != primaryPid))
                {
                    if (!await ForceCloseVerifiedSecondaryAahAsync(process, installedExe))
                    {
                        Log("Secondary AAH PID " + process.Id +
                            " could not be safely identity-verified and ended. No files changed.");
                        return false;
                    }
                }

                var primary = processes.Single(p => p.Id == primaryPid);
                if (!primary.HasExited &&
                    !await RequestGracefulUpdateExitAsync(
                        primary, TimeSpan.FromSeconds(90), installedExe))
                {
                    if (!primary.HasExited)
                    {
                        Log("Real primary AAH PID " + primary.Id +
                            " did not finish graceful accounting/Tally shutdown. " +
                            "User policy protects the real instance from forced termination.");
                        return false;
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            await Task.Delay(350);
        }

        var remaining = GetSameInstallationProcesses(installedExe);
        try
        {
            if (remaining.Count == 0)
            {
                Log("PASS: no live AAH exact-install processes remain after user-selected shutdown.");
                return true;
            }
            Log("Cannot replace files: LIVE exact-install AAH processes remain: " +
                string.Join(",", remaining.Select(p => p.Id)));
            return false;
        }
        finally { foreach (var process in remaining) process.Dispose(); }
    }

    // Deterministic authority rule. A registered update listener is a better
    // indication of the real, initialized AAH than thread count, PID order or
    // stale Task Manager rows. A visible window breaks ties, then newest start.
    internal static int SelectPrimaryGracefulPid(
        IReadOnlyCollection<(int pid, bool hasListener, bool hasMainWindow, long startedTicks)> candidates)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("No active AAH candidates.", nameof(candidates));
        return candidates.OrderByDescending(x => x.hasListener)
            .ThenByDescending(x => x.hasMainWindow)
            .ThenByDescending(x => x.startedTicks)
            .ThenByDescending(x => x.pid)
            .First().pid;
    }

    private static bool HasRegisteredShutdownListener(int pid)
    {
        try
        {
            using var listener = EventWaitHandle.OpenExisting(
                @"Local\AccountingAutomationHub.UpdateShutdown.v1." + pid);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException)
        {
            // An uninspectable listener must not be chosen over a known real
            // primary. The verified exact-path process remains in the census.
            Log("Could not inspect AAH shutdown listener for PID " + pid + ".");
            return false;
        }
    }

    private static async Task<bool> ForceCloseVerifiedSecondaryAahAsync(
        Process candidate, string installedExe)
    {
        if (candidate.HasExited) return true;
        try
        {
            // Check identity TWICE before force-close, including lifetime.
            // A reused PID / different installation is not a valid target.
            var expectedStart = candidate.StartTime.ToUniversalTime();
            var expectedPath = candidate.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(expectedPath) ||
                !IsExactInstallationProcess(candidate.ProcessName, expectedPath, installedExe))
                throw new InvalidDataException(
                    "Secondary PID " + candidate.Id + " does not belong to the exact AAH installation.");
            using var fresh = Process.GetProcessById(candidate.Id);
            if (fresh.HasExited) return true;
            if (fresh.StartTime.ToUniversalTime() != expectedStart ||
                !SameProcessPath(fresh, installedExe))
                throw new InvalidDataException(
                    "Secondary AAH PID identity changed before force close.");
            Log("FORCE-CLOSE user-selected SECONDARY exact-install AAH PID " + candidate.Id +
                "; the real AAH primary still uses graceful Tally shutdown.");
            fresh.Kill(entireProcessTree: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await fresh.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (ArgumentException) when (candidate.HasExited) { return true; }
        catch (InvalidOperationException) when (candidate.HasExited) { return true; }
        catch (Exception exception)
        {
            Log("Secondary AAH PID " + candidate.Id + " force-close failed safely: " +
                exception.GetType().Name + ": " + exception.Message);
            return candidate.HasExited;
        }
    }

    /// <summary>
    /// A listener timeout is an observation, not the ultimate process state.
    /// Only a new exact-installation census may override a stale timeout.
    /// Never proceed while any matching AAH process remains running.
    /// </summary>
    internal static bool ShouldDeferAfterFreshProcessCensus(
        IReadOnlyCollection<bool> shutdownResults, int verifiedRemainingProcessCount)
    {
        if (verifiedRemainingProcessCount < 0)
            throw new ArgumentOutOfRangeException(nameof(verifiedRemainingProcessCount));
        return shutdownResults.Any(success => !success) &&
               verifiedRemainingProcessCount > 0;
    }

    internal static bool LauncherExitedOrBelongsToInstallation(int launcherPid, string installedExe)
    {
        if (launcherPid <= 0) return false;
        try
        {
            using var launcher = Process.GetProcessById(launcherPid);
            if (launcher.HasExited)
            {
                Log("Launching AAH PID " + launcherPid + " exited before updater preflight; validating remaining exact-install PIDs.");
                return true;
            }
            return SameProcessPath(launcher, installedExe);
        }
        catch (ArgumentException)
        {
            // GetProcessById throws when the original launcher legitimately
            // exited. This is not evidence that another process can be killed;
            // ShutdownAllVerifiedInstallProcessesAsync independently verifies
            // the FULL path of every remaining process before file replacement.
            Log("Launching AAH PID " + launcherPid + " already exited; continuing with exact-install process verification.");
            return true;
        }
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
                    // A dead AAH process is harmless to replacement, even if
                    // Windows still lists an obsolete one-thread PID. Test this
                    // native condition BEFORE MainModule: .NET can refuse to
                    // provide its executable or Process.Handle after HasExited.
                    // This branch NEVER kills a process; it only stops counting
                    // one verified dead PID as a live installation blocker.
                    if (process.HasExited)
                    {
                        Log("AAH PID " + process.Id +
                            " is already exited per the original .NET process object. " +
                            "Excluding a stale OS PID record without accessing MainModule, " +
                            "Process.Handle or calling Kill.");
                        process.Dispose();
                        continue;
                    }

                    // Anything still running or unverified MUST be exact-path
                    // verified and then sent through original graceful Tally /
                    // accounting drain. No broad taskkill or new exit protocol.
                    var fullPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(fullPath))
                        throw new InvalidDataException("Cannot verify path for AAH PID " + process.Id);
                    if (IsExactInstallationProcess(process.ProcessName, fullPath, installedExe))
                    {
                        if (process.HasExited)
                        {
                            // Exit can race the earlier observation.
                            process.Dispose();
                            continue;
                        }
                        selected.Add(process);
                    }
                    else process.Dispose();
                }
                catch (InvalidOperationException exception)
                {
                    var pid = process.Id;
                    process.Dispose();
                    // A process can legitimately disappear between enumeration
                    // and MainModule. Skip only when a SECOND independent
                    // enumeration proves it absent; otherwise fail closed.
                    if (IsPidStillEnumeratedAsAah(pid))
                    {
                        // A name-only listing does NOT establish running state.
                        // Windows can retain terminated PIDs with a single
                        // thread after their .NET Process.HasExited is true.
                        // Reopen to distinguish a dead PID from a still-live
                        // uninspectable process before any update transaction.
                        using var retry = Process.GetProcessesByName("AccountingAutomationHub")
                            .FirstOrDefault(p => p.Id == pid);
                        if (retry is not null && !retry.HasExited)
                            throw new InvalidDataException(
                                "AAH PID " + pid + " is live but its executable " +
                                "cannot be verified. Refusing an unsafe replacement.", exception);
                    }
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

    private static bool IsPidStillEnumeratedAsAah(int pid)
    {
        foreach (var candidate in Process.GetProcessesByName("AccountingAutomationHub"))
        {
            using (candidate)
            {
                if (candidate.Id == pid) return true;
            }
        }
        return false;
    }

    internal static bool IsUpgradeOrRestartRecovery(Version installedOnDisk,
        Version verifiedReleaseBinary, string stableTag) =>
        verifiedReleaseBinary >= installedOnDisk &&
        verifiedReleaseBinary.ToString().Equals(stableTag.TrimStart('v'),
            StringComparison.Ordinal);

    // Verified v582 updater recovery: old v579/v580 desktop builds may finish
    // OnExit cleanup but retain a foreground thread. Their PID listener is
    // already unregistered, so resending an event cannot finish the process.
    // Retry normal graceful exit, but recognize logged COMPLETED shutdown
    // as a separate, strictly checked terminal state. No broad PID killing.
    internal static async Task<bool> RequestVerifiedUpdateExitWithRetryAsync(
        Process parent, string? verifiedInstalledExe = null)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (await RequestGracefulUpdateExitAsync(
                parent, TimeSpan.FromMinutes(11), verifiedInstalledExe))
                return true;
            if (attempt < 2)
                Log("AAH not yet exited: retrying PID-targeted graceful shutdown; active accounting jobs are never interrupted.");
        }
        return false;
    }

    internal static async Task<bool> RequestGracefulUpdateExitAsync(
        Process parent, TimeSpan timeout, string? verifiedInstalledExe = null)
    {
        if (IsConfirmedKernelTermination(parent)) return true;
        if (!parent.ProcessName.Equals("AccountingAutomationHub", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Graceful update shutdown target is not AAH.");

        // A completed ghost has no listener, but its ordered, post-start
        // verified shutdown audit log is sufficient to retire only that exact
        // process. All unverified, active and unknown PIDs remain untouched.
        if (await RetireVerifiedCleanedGhostAsync(parent, verifiedInstalledExe))
            return true;
        var listenerDiscovery = Stopwatch.StartNew();
        while (listenerDiscovery.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (IsConfirmedKernelTermination(parent)) return true;
            if (await RetireVerifiedCleanedGhostAsync(parent, verifiedInstalledExe))
                return true;
            try
            {
                using var exitSignal = EventWaitHandle.OpenExisting(
                    @"Local\AccountingAutomationHub.UpdateShutdown.v1." + parent.Id);
                exitSignal.Set();
                Log("Sent graceful update shutdown to original AAH PID " + parent.Id +
                    "; waiting for its own import/export/Tally cleanup.");
                return await WaitForOriginalExitAsync(parent, timeout, verifiedInstalledExe);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                await Task.Delay(500);
            }
            catch (UnauthorizedAccessException)
            {
                Log("AAH update-shutdown signal access denied; no process termination.");
                return false;
            }
        }
        if (IsConfirmedKernelTermination(parent)) return true;
        Log("AAH PID " + parent.Id + " has no registered shutdown listener and no complete " +
            "verified-cleanup evidence; preserving process and accounting data.");
        return false;
    }

    // Pure deterministic evidence gates: neither a PID alone nor an idle
    // signal authorizes process termination. The complete ordered shutdown
    // evidence must match this exact process lifetime and executable path.
    internal static bool HasCompletedVerifiedExitEvidence(
        string log, int pid, DateTimeOffset startedUtc, DateTimeOffset observedUtc,
        out DateTimeOffset completedUtc) =>
        HasOrderedCompletedExitEvidence(log, pid, startedUtc, observedUtc,
            new[]
            {
                "Verified updater requested graceful exit; new automation intake quiesced.",
                "Verified updater: in-flight accounting/Tally idle for 3 checks; exiting.",
                "Application shutdown started; stopping Accounting/Tally runtime.",
                "Tally coordinator disposal completed.",
                "Workspace cache disposal completed.",
                "Runtime cleanup finished; instance lock remains owned until OnExit completes.",
                "Process exit releasing final single-instance ownership."
            }, out completedUtc);

    // Legacy explicit Exit Completely may also strand foreground helper threads
    // after its original Tally/automation/distributed idle gate, WAL cleanup and
    // final OnExit lock release. This is NOT ordinary X-to-tray.
    internal static bool HasCompletedSafeUserExitEvidence(
        string log, int pid, DateTimeOffset startedUtc, DateTimeOffset observedUtc,
        out DateTimeOffset completedUtc) =>
        HasOrderedCompletedExitEvidence(log, pid, startedUtc, observedUtc,
            new[]
            {
                "User requested Exit Completely: no in-flight accounting/Tally work.",
                "Application shutdown started; stopping Accounting/Tally runtime.",
                "Tally coordinator disposal completed.",
                "Workspace cache disposal completed.",
                "Runtime cleanup finished; instance lock remains owned until OnExit completes.",
                "Process exit releasing final single-instance ownership."
            }, out completedUtc);

    private static bool HasOrderedCompletedExitEvidence(
        string log, int pid, DateTimeOffset startedUtc, DateTimeOffset observedUtc,
        string[] markers, out DateTimeOffset completedUtc)
    {
        completedUtc = default;
        if (pid <= 0 || startedUtc > observedUtc || markers.Length == 0) return false;
        var stage = 0;
        var previousStageTimestamp = DateTimeOffset.MinValue;
        var prefix = " PID " + pid.ToString(CultureInfo.InvariantCulture) + ": ";
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var pos = line.IndexOf(prefix, StringComparison.Ordinal);
            if (pos <= 0) continue;
            if (!DateTimeOffset.TryParse(line[..pos], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var when)) continue;
            when = when.ToUniversalTime();
            // Do not accept evidence from before this OS process began, even
            // if Windows recycled a PID shortly after a previous exit.
            if (when < startedUtc || when > observedUtc) continue;
            var message = line[(pos + prefix.Length)..];
            if (message.Contains("Verified updater deferred:", StringComparison.Ordinal) ||
                message.Contains("Verified updater shutdown check failed", StringComparison.Ordinal) ||
                message.StartsWith("User exit deferred:", StringComparison.Ordinal) ||
                message.StartsWith("Exit Completely validation failed", StringComparison.Ordinal))
            {
                stage = 0;
                previousStageTimestamp = DateTimeOffset.MinValue;
                completedUtc = default;
                continue;
            }
            if (message.Equals(markers[stage], StringComparison.Ordinal))
            {
                if (when < previousStageTimestamp)
                {
                    stage = 0;
                    return false; // Audit timestamps went backwards.
                }
                previousStageTimestamp = when;
                stage++;
                if (stage == markers.Length)
                {
                    completedUtc = when;
                    return true;
                }
            }
        }
        return false;
    }

    private static string ReadBoundedShutdownLog()
    {
        var path = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub_v4_0_0_shutdown.log");
        if (!File.Exists(path)) return string.Empty;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        // AAH appends diagnostics; never scan an unbounded history every 2s.
        var skip = Math.Max(0L, file.Length - 1024L * 1024L);
        file.Seek(skip, SeekOrigin.Begin);
        using var reader = new StreamReader(file, Encoding.UTF8, true);
        var tail = reader.ReadToEnd();
        if (skip > 0)
        {
            var firstBreak = tail.IndexOf('\n');
            return firstBreak >= 0 ? tail[(firstBreak + 1)..] : string.Empty;
        }
        return tail;
    }

    private static async Task<bool> RetireVerifiedCleanedGhostAsync(
        Process process, string? verifiedInstalledExe)
    {
        if (IsConfirmedKernelTermination(process)) return true;
        if (string.IsNullOrWhiteSpace(verifiedInstalledExe)) return false;
        try
        {
            // Process objects hold the original OS handle: PID reuse after a
            // scan cannot turn this into a kill of some later process.
            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath) ||
                !IsExactInstallationProcess(process.ProcessName, actualPath, verifiedInstalledExe))
                return false;
            var processStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
            var now = DateTimeOffset.UtcNow;
            var audit = ReadBoundedShutdownLog();
            var verifiedUpdaterComplete = HasCompletedVerifiedExitEvidence(
                audit, process.Id, processStart, now, out var completedAt);
            if (!verifiedUpdaterComplete &&
                !HasCompletedSafeUserExitEvidence(
                    audit, process.Id, processStart, now, out completedAt))
                return false;
            if (now - completedAt < TimeSpan.FromSeconds(5))
                return false; // Give WPF/foreground helpers a chance to exit.
            if (IsConfirmedKernelTermination(process)) return true;
            // Re-verify exact process identity before taking the sole narrow
            // process-ending action. This is NOT an active-app force-close:
            // verified updater-triggered idle + completed accounting/WAL
            // cleanup and owner release were already logged by THIS PID.
            if (!SameProcessPath(process, verifiedInstalledExe))
                return false;
            Log("AAH PID " + process.Id + " completed safe Tally/runtime cleanup and " +
                "final instance release, but remained alive. Retiring this proven-cleaned ghost only.");
            process.Kill(entireProcessTree: false);
            using var boundedWait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(boundedWait.Token);
            Log("Proven-cleaned ghost AAH PID " + process.Id + " exited; fresh process census required.");
            return true;
        }
        catch (InvalidOperationException) when (IsConfirmedKernelTermination(process)) { return true; }
        catch (Exception ex)
        {
            Log("AAH cleaned-ghost verification/exit deferred for PID " + process.Id +
                ": " + ex.GetType().Name + ": " + ex.Message);
            return IsConfirmedKernelTermination(process);
        }
    }

    private static async Task<bool> WaitForOriginalExitAsync(
        Process parent, TimeSpan timeout, string? verifiedInstalledExe)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (IsConfirmedKernelTermination(parent)) return true;
            if (await RetireVerifiedCleanedGhostAsync(parent, verifiedInstalledExe))
                return true;
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            using var sample = new CancellationTokenSource(
                remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2));
            try
            {
                await parent.WaitForExitAsync(sample.Token);
                return true;
            }
            catch (OperationCanceledException) { /* Recheck recorded cleanup. */ }
        }
        return IsConfirmedKernelTermination(parent);
    }

    /// <summary>
    /// After ready, require a fresh path-verified census containing ONLY the
    /// newly launched PID. The pre-shutdown census is not enough: a stale old
    /// process can survive/reappear after its WPF cleanup was logged complete.
    /// Existing PID-start-time and complete ordered shutdown evidence are the
    /// sole authority to retire a ghost. No broad kill or process-tree kill.
    /// </summary>
    internal static bool IsOnlyExpectedRestartPid(
        IReadOnlyCollection<int> exactInstallPids, int restartedPid) =>
        restartedPid > 0 && exactInstallPids.Count == 1 &&
        exactInstallPids.Contains(restartedPid);

    private static async Task<bool> VerifySingleRestartedInstallationAsync(
        string installedExe, int restartedPid)
    {
        // Prove stability across multiple independent Windows process censuses.
        // A previous false "all exited" result must not be repeated after ready.
        for (var pass = 1; pass <= 3; pass++)
        {
            var candidates = GetSameInstallationProcesses(installedExe);
            try
            {
                var pids = candidates.Select(process => process.Id).ToArray();
                if (!IsOnlyExpectedRestartPid(pids, restartedPid))
                {
                    Log("Post-restart census " + pass +
                        ": expected only new PID " + restartedPid + ", observed " +
                        (pids.Length == 0 ? "(none)" : string.Join(",", pids)) + ".");
                    foreach (var process in candidates)
                    {
                        if (process.Id != restartedPid && !IsConfirmedKernelTermination(process))
                            await RetireVerifiedCleanedGhostAsync(process, installedExe);
                    }
                }
                else
                {
                    Log("Post-restart census " + pass + ": only verified new AAH PID " +
                        restartedPid + " remains.");
                }
            }
            finally
            {
                foreach (var process in candidates) process.Dispose();
            }
            if (pass < 3) await Task.Delay(1000);
        }
        // Final independent census after ghost retirement; never trust a stale
        // snapshot or count a ready signal as proof that other PIDs are gone.
        var final = GetSameInstallationProcesses(installedExe);
        try
        {
            var pids = final.Select(process => process.Id).ToArray();
            var clean = IsOnlyExpectedRestartPid(pids, restartedPid);
            Log(clean
                ? "PASS: post-restart single-instance gate: exactly one restarted " +
                  "AAH process from the verified installation exists, PID " + restartedPid + "."
                : "FAILED post-restart single-instance gate: expected PID " +
                  restartedPid + ", found " + string.Join(",", pids) +
                  ". Never terminate unknown/live accounting processes.");
            return clean;
        }
        finally
        {
            foreach (var process in final) process.Dispose();
        }
    }

    // Only updater hosts from an older four-component release, launched from
    // our immutable attempt cache, can be retired. The real desktop process
    // is named AccountingAutomationHub and is NEVER a candidate here.
    // This purely lexical helper is also exercised against unrelated paths
    // and same/newer release versions by the Windows regression suite.
    internal static bool IsObsoleteUpdaterFromCache(
        string updaterPath, string cacheRoot, Version installedVersion)
    {
        if (string.IsNullOrWhiteSpace(updaterPath) ||
            string.IsNullOrWhiteSpace(cacheRoot)) return false;
        var relative = Path.GetRelativePath(
            Path.GetFullPath(cacheRoot), Path.GetFullPath(updaterPath));
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length != 3 ||
            !Regex.IsMatch(segments[0], @"\Av\d+\.\d+\.\d+\.\d+\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(segments[1], @"\Aattempt-[0-9a-f]{32}\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            !segments[2].Equals("AAH_Update.exe", StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(segments[0][1..], out var cachedVersion))
            return false;
        return cachedVersion < installedVersion;
    }

    // This runs ONLY after the new installed binary and restarted genuine AAH
    // passed their existing readiness + single-instance checks, while THIS
    // updater still owns the exclusive cross-process installation mutex.
    // Older hosts may have blocked indefinitely in a historical PowerShell
    // child wait. They are no longer allowed to remain live after a newer
    // release has committed. Do not kill a process-name match: validate the
    // cache release, EXE path and unchanging PID/start-time identity TWICE.
    private static async Task<bool> RetireObsoleteUpdateHostsAfterVerifiedRestartAsync(
        string installedExe)
    {
        var installedVersionText = FileVersionInfo.GetVersionInfo(installedExe).FileVersion;
        if (!Version.TryParse(installedVersionText, out var installedVersion))
        {
            Log("WARNING: cannot verify installed version for obsolete updater cleanup.");
            return false;
        }
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccountingAutomationHub", "GitHubAutoUpdate");
        var clean = true;
        foreach (var candidate in Process.GetProcessesByName("AAH_Update"))
        {
            using (candidate)
            {
                if (candidate.Id == Environment.ProcessId) continue;
                try
                {
                    if (candidate.HasExited)
                    {
                        // CL019 v590 PID 8968: CIM can continue listing a dead
                        // executable for hours. No live process remains to kill.
                        Log("SKIP: old updater PID " + candidate.Id +
                            " has already exited; Windows may retain its process record.");
                        continue;
                    }
                    var path = candidate.MainModule?.FileName ?? string.Empty;
                    if (!IsObsoleteUpdaterFromCache(path, cacheRoot, installedVersion))
                        continue; // Other installations and same/newer hosts untouched.
                    var expectedStart = candidate.StartTime.ToUniversalTime();
                    if (!File.Exists(path) || IsUpdaterCachePathReparsed(cacheRoot, path))
                    {
                        Log("WARNING: obsolete updater PID " + candidate.Id +
                            " has missing/reparsed cache path; refusing to terminate blindly.");
                        clean = false;
                        continue;
                    }

                    using var fresh = Process.GetProcessById(candidate.Id);
                    if (fresh.HasExited) continue;
                    var freshPath = fresh.MainModule?.FileName ?? string.Empty;
                    if (!fresh.ProcessName.Equals("AAH_Update", StringComparison.OrdinalIgnoreCase) ||
                        fresh.StartTime.ToUniversalTime() != expectedStart ||
                        !Path.GetFullPath(freshPath).Equals(
                            Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
                        !IsObsoleteUpdaterFromCache(freshPath, cacheRoot, installedVersion))
                    {
                        Log("WARNING: updater PID " + candidate.Id +
                            " changed identity during verification; not killed.");
                        clean = false;
                        continue;
                    }

                    Log("Retiring VERIFIED obsolete updater PID " + candidate.Id +
                        " from " + path + " after successful AAH " + installedVersion + " restart.");
                    fresh.Kill(entireProcessTree: false); // Never kill AAH, Tally or child trees.
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await fresh.WaitForExitAsync(deadline.Token);
                    Log("PASS: obsolete updater PID " + candidate.Id + " exited.");
                }
                catch (ArgumentException) when (candidate.HasExited)
                {
                    Log("SKIP: updater PID " + candidate.Id + " exited during verification.");
                }
                catch (InvalidOperationException) when (candidate.HasExited)
                {
                    Log("SKIP: updater PID " + candidate.Id + " exited during verification.");
                }
                catch (Exception exception)
                {
                    clean = false;
                    Log("WARNING: cannot safely retire old updater PID " + candidate.Id +
                        ": " + exception.GetType().Name + ": " + exception.Message);
                }
            }
        }
        return clean;
    }

    // A reparse point in any cache segment could redirect an otherwise correct
    // path into an unrelated process location. Refuse cleanup in that case.
    private static bool IsUpdaterCachePathReparsed(string cacheRoot, string updaterPath)
    {
        var root = Path.GetFullPath(cacheRoot);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(updaterPath));
        var current = root;
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
        }
        return false;
    }

    private static async Task<int?> StartAndVerifyAsync(string exe, string install)
    {
        var token = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
            @"Local\AccountingAutomationHub.Ready." + token);
        using var launched = Process.Start(new ProcessStartInfo(exe) {
            WorkingDirectory = install, UseShellExecute = false,
            ArgumentList = { "--update-ready=" + token }
        });
        if (launched is null) return null;
        // Use a monotonic elapsed timer: Windows clock corrections, DST and
        // timezone changes must not lengthen or shorten the restart budget.
        var readinessTimer = Stopwatch.StartNew();
        while (readinessTimer.Elapsed < TimeSpan.FromMinutes(3))
        {
            if (ready.WaitOne(300))
            {
                // A transient startup signal is not enough; process must remain alive.
                await Task.Delay(2000);
                if (!launched.HasExited)
                {
                    Log("Verified restart ready: new AAH PID " + launched.Id +
                        " signaled loaded UI and remained active.");
                    return launched.Id;
                }
                Log("Restart readiness signal arrived but the new AAH process exited.");
                return null;
            }
            if (launched.HasExited)
            {
                Log("Restart process exited before sending ready signal (PID " + launched.Id +
                    "); another stale instance may own the single-instance mutex.");
                return null;
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
                await WaitForOriginalExitAsync(launched, TimeSpan.FromSeconds(30), exe);
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        if (!launched.HasExited)
            throw new UpdateRuntimeBusyException("New AAH did not exit; rollback deferred to protect running process.");
        return null;
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
        Log("Update preflight starting bounded Authenticode verification of " +
            Path.GetFileName(path) + "; installed files untouched.");
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("$s=Get-AuthenticodeSignature -LiteralPath $env:AAH_UPDATE_VERIFY;" +
            "if($s.Status -eq 'Valid' -and $s.SignerCertificate){" +
            "[Console]::Out.Write($s.SignerCertificate.Thumbprint);exit 0};exit 1");
        info.Environment["AAH_UPDATE_VERIFY"]=path;
        var result = await RunVerifierChildAsync(info, PublisherVerificationDeadline);
        var output = result.Output.Trim();
        Log("PASS: bounded Authenticode verification finished for " +
            Path.GetFileName(path) + " (trusted signature " +
            (result.ExitCode == 0 ? "present" : "absent") + ").");
        return result.ExitCode == 0 && Regex.IsMatch(output, @"\A[a-fA-F0-9]{40}\z")
            ? output.ToUpperInvariant() : null;
    }

    internal static async Task<(int ExitCode, string Output)> RunVerifierChildAsync(
        ProcessStartInfo info, TimeSpan timeout)
    {
        using var proc = Process.Start(info) ??
            throw new InvalidOperationException("Could not start isolated Authenticode verifier.");
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            // Read stdout concurrently with waiting for process exit; neither
            // may wait forever. This process is ONLY the verifier helper.
            var output = await proc.StandardOutput.ReadToEndAsync(deadline.Token);
            await proc.WaitForExitAsync(deadline.Token);
            return (proc.ExitCode, output);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Never terminate AAH or the UpdateHost: only our own spawned
            // preflight signature-checking PowerShell child is disposable.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* timed-out verifier is still a hard failure */ }
            throw new TimeoutException(
                "Updater Authenticode helper exceeded " + timeout +
                " before application shutdown; installed AAH unchanged.");
        }
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // Show the local wall clock and its UTC offset, without changing the
    // release verifier's UTC semantics or imposing a particular timezone.
    internal static string FormatLocalLogTimestamp(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, FormatLocalLogTimestamp(DateTimeOffset.UtcNow) + " " + message + Environment.NewLine);
        }
        catch { }
    }
}
