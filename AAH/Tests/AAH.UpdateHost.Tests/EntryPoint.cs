using System.IO.Compression;
using Aah.UpdateHost;

namespace Aah.UpdateHost.Tests;

internal static class EntryPoint
{
    private static void Assert(bool ok, string explanation)
    {
        if (!ok) throw new InvalidOperationException(explanation);
    }

    private static void Test(string root)
    {
        // The updater must display PC-local time with an offset, preserving
        // the original UTC instant used to correlate release event timestamps.
        var utcEvent = new DateTimeOffset(2026, 10, 31, 6, 19, 33, TimeSpan.Zero);
        var displayedTime = Program.FormatLocalLogTimestamp(utcEvent);
        Assert(DateTimeOffset.TryParse(displayedTime,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind, out var parsedTime) &&
               parsedTime.ToUniversalTime() == utcEvent &&
               parsedTime.Offset == TimeZoneInfo.Local.GetUtcOffset(utcEvent.UtcDateTime),
            "UpdateHost log timestamp did not retain the UTC instant and PC-local offset.");
        Console.WriteLine("PASS: updater host logs PC-local offset timestamps without UTC instant loss.");

        // An update request must never be able to stop an unrelated process.
        // The production host only signals the already-verified AAH PID.
        var unrelatedShutdownRejected = false;
        try
        {
            Program.RequestGracefulUpdateExitAsync(
                System.Diagnostics.Process.GetCurrentProcess(),
                TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        }
        catch (InvalidDataException) { unrelatedShutdownRejected = true; }
        Assert(unrelatedShutdownRejected,
            "Graceful-update protocol permitted shutdown signaling to an unrelated process.");

        // The updater owns the only verified shutdown request. The running
        // desktop merely quiesces auto-discovery, drains real accounting/Tally
        // operations and exits. A failed attempt must resume watchers.
        var sourceRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (sourceRoot is not null &&
               !File.Exists(Path.Combine(sourceRoot.FullName, "AAH", "src",
                   "AccountingAutomationHub.Desktop", "App.xaml.cs")))
            sourceRoot = sourceRoot.Parent;
        if (sourceRoot is null)
            throw new InvalidOperationException("AAH solution root missing for updater-only shutdown regression.");
        // November 3 v578 stalled updater: the AAH_Update.exe mutex stayed busy
        // for 13+ minutes without ONE v578 host log entry after DNS recovery.
        // HttpClient ResponseHeadersRead does not bound streaming body reads.
        // Require both total/idle timeouts and a preflight log before download.
        Assert(Program.AppArchiveDownloadDeadline <= TimeSpan.FromMinutes(20) &&
               Program.AppArchiveDownloadDeadline > Program.AppArchiveIdleTimeout &&
               Program.AppArchiveIdleTimeout <= TimeSpan.FromMinutes(2),
            "Application download can hold updater mutex too long after network failure.");
        var hostSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
            "AAH.UpdateHost", "Program.cs"));
        Assert(hostSource.Contains("src.ReadAsync(buffer.AsMemory(), stalled.Token)", StringComparison.Ordinal) &&
               hostSource.Contains("deadline.Token", StringComparison.Ordinal) &&
               hostSource.Contains("UpdateHost acquired exclusive mutex", StringComparison.Ordinal) &&
               hostSource.Contains("No installed files changed; updater lock will release.", StringComparison.Ordinal) &&
               hostSource.IndexOf("Downloading \" +", StringComparison.Ordinal) <
               hostSource.IndexOf("await DownloadVerifiedAsync(archiveUrl", StringComparison.Ordinal),
            "UpdateHost release preflight lacks bounded streaming reads or early progress logs.");
        Console.WriteLine("PASS: update host preflight and archive body cannot hold installation lock indefinitely.");

        var desktopSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "App.xaml.cs"));
        var automationSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "Services", "BackgroundAutomationService.cs"));
        Assert(desktopSource.Contains("StartUpdateShutdownListener();", StringComparison.Ordinal) &&
               desktopSource.Contains(@"Local\AccountingAutomationHub.UpdateShutdown.v1.", StringComparison.Ordinal) &&
               desktopSource.Contains("AutomationService?.BeginVerifiedUpdateQuiescence();", StringComparison.Ordinal) &&
               desktopSource.Contains("AutomationService?.EndVerifiedUpdateQuiescence();", StringComparison.Ordinal) &&
               desktopSource.Contains("ExitForVerifiedUpdateWhenIdleAsync()", StringComparison.Ordinal) &&
               desktopSource.Contains("if (tallyBusy || automationBusy || distributedBusy)", StringComparison.Ordinal),
            "Verified updater shutdown is no longer PID-scoped, drains incorrectly or does not restore monitoring.");
        // Early PID listener registration and deferred destruction mean an
        // updater cannot mistake normal startup/cleanup for an unsupported old app.
        var startupRegistration = desktopSource.IndexOf("StartUpdateShutdownListener();", StringComparison.Ordinal);
        var lateUi = desktopSource.IndexOf("InitializeTrayIcon();", StringComparison.Ordinal);
        var onExit = desktopSource.IndexOf("protected override void OnExit(", StringComparison.Ordinal);
        var cleanup = desktopSource.IndexOf("private void CleanupRuntime()", StringComparison.Ordinal);
        var unregister = desktopSource.IndexOf("_updateShutdownWait?.Unregister(null);", StringComparison.Ordinal);
        Assert(startupRegistration > 0 && lateUi > startupRegistration &&
               cleanup > 0 && onExit > cleanup && unregister > onExit &&
               desktopSource.Contains("Volatile.Read(ref _updateShutdownStartupReady)", StringComparison.Ordinal) &&
               desktopSource.Contains("System.Diagnostics.Stopwatch.StartNew()", StringComparison.Ordinal),
            "Update listener absent during startup, disposed before process exit or uses a wall-clock deadline.");
        Console.WriteLine("PASS: listener covers startup and cleanup; update waits for safe initialized idle.");

        // v580 regression missed that Environment.Exit followed base.OnExit.
        // The observed 15912 and 17296 processes logged final mutex release
        // yet remained alive for >10 minutes while the updater held its mutex.
        // Force the verified-updater-only exit BEFORE WPF base.OnExit, and only
        // after a cleanup-COMPLETED marker, not the cleanup-entered flag.
        var verifiedRequest = desktopSource.IndexOf("_verifiedUpdaterExit = true;", StringComparison.Ordinal);
        var idleSamples = desktopSource.LastIndexOf("quietSamples >= 3", verifiedRequest, StringComparison.Ordinal);
        var cleanupDone = desktopSource.IndexOf("_verifiedUpdaterCleanupCompleted = true;", StringComparison.Ordinal);
        var cleanupGuard = desktopSource.IndexOf(
            "if ((_verifiedUpdaterExit || _explicitSafeUserExit) &&", StringComparison.Ordinal);
        var ownerRelease = desktopSource.IndexOf("Process exit releasing final single-instance ownership.", StringComparison.Ordinal);
        var finalExit = desktopSource.IndexOf("Environment.Exit(0);", cleanupGuard, StringComparison.Ordinal);
        var normalWpfExit = desktopSource.IndexOf("base.OnExit(e);", finalExit, StringComparison.Ordinal);
        Assert(verifiedRequest > idleSamples && idleSamples > 0 &&
               cleanupDone > cleanup && cleanupDone < onExit &&
               cleanupGuard > ownerRelease && ownerRelease > verifiedRequest &&
               finalExit > cleanupGuard && normalWpfExit > finalExit &&
               desktopSource.Contains("!_ownsSingleInstanceMutex)", StringComparison.Ordinal) &&
               desktopSource.Contains("_explicitSafeUserExit = true;", StringComparison.Ordinal) &&
               desktopSource.IndexOf("User requested Exit Completely: no in-flight accounting/Tally work.", StringComparison.Ordinal) <
                   desktopSource.IndexOf("_explicitSafeUserExit = true;", StringComparison.Ordinal) &&
               desktopSource.Contains("finally { Shutdown(); }", StringComparison.Ordinal),
            "Verified update can remain stuck inside WPF base.OnExit after safe cleanup, or exit before accounting/Tally safety gates.");
        Console.WriteLine("PASS: verified updater ends cleaned process before WPF exit can block; normal exits unchanged.");


        Assert(automationSource.Contains("if (IsQuiescingForVerifiedUpdate && !manualReimport) return false;", StringComparison.Ordinal) &&
               automationSource.Contains("if (IsQuiescingForVerifiedUpdate && !item.ManualReimport)", StringComparison.Ordinal) &&
               automationSource.Contains("_queue.Any(item => item.ManualReimport)", StringComparison.Ordinal) &&
               automationSource.Contains("DisposeCentralSalesPurchaseWatcher();", StringComparison.Ordinal) &&
               automationSource.Contains("ReloadSettings(scanExisting: true, scheduleGstBootstrap: true);", StringComparison.Ordinal),
            "Verified update may wait on repeatable scans, interrupt manual jobs or fail to restore watchers.");
        Console.WriteLine("PASS: updater-exclusive graceful shutdown quiesces repeatable work, protects active accounting and restores deferred watchers.");

        // The October 31 failure left two processes from the same installation
        // alive after the binary had been replaced. These identity and version
        // contracts must be tested independently of a live Tally installation.
        var originalExe = Path.GetFullPath(Path.Combine(root, "AAH-Installed",
            "AccountingAutomationHub.exe"));
        var secondInstall = Path.GetFullPath(Path.Combine(root, "AAH-Other",
            "AccountingAutomationHub.exe"));
        Assert(Program.IsExactInstallationProcess("AccountingAutomationHub", originalExe, originalExe),
            "Exact-install AAH process not recognized.");
        Assert(!Program.IsExactInstallationProcess("AccountingAutomationHub", secondInstall, originalExe),
            "Different-install AAH process was selected for graceful shutdown.");
        Assert(!Program.IsExactInstallationProcess("explorer", originalExe, originalExe),
            "Non-AAH application could be targeted for shutdown.");
        Assert(Program.LauncherExitedOrBelongsToInstallation(int.MaxValue, originalExe),
            "Updater blocked a verified release when the launcher already exited before preflight.");
        Assert(!Program.LauncherExitedOrBelongsToInstallation(
                System.Diagnostics.Process.GetCurrentProcess().Id, originalExe),
            "Updater accepted an active, unrelated/reused launcher PID for a different executable.");
        Assert(!Program.LauncherExitedOrBelongsToInstallation(0, originalExe),
            "Updater accepted an invalid launcher PID.");
        var mainWindowSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "MainWindow.xaml.cs"));
        var closingStart = desktopSource.IndexOf("public void HandleMainWindowClosing(CancelEventArgs e)", StringComparison.Ordinal);
        var cancelBeforePreferences = desktopSource.IndexOf("e.Cancel = true;", closingStart, StringComparison.Ordinal);
        var settingsGuard = desktopSource.IndexOf("if (Preferences is null) return;", closingStart, StringComparison.Ordinal);
        var closeOverride = mainWindowSource.IndexOf("protected override void OnClosing(CancelEventArgs e)", StringComparison.Ordinal);
        var closeIntercept = mainWindowSource.IndexOf("app.HandleMainWindowClosing(e);", closeOverride, StringComparison.Ordinal);
        var placementSave = mainWindowSource.IndexOf("SaveWindowPlacement();", closeOverride, StringComparison.Ordinal);
        Assert(closingStart >= 0 && cancelBeforePreferences > closingStart &&
               settingsGuard > cancelBeforePreferences &&
               desktopSource.Contains("_trayIcon.Visible = true;", StringComparison.Ordinal) &&
               closeOverride >= 0 && closeIntercept > closeOverride &&
               placementSave > closeIntercept &&
               mainWindowSource.Contains("catch (Exception exception)", StringComparison.Ordinal),
            "MainWindow X/Close may still destroy its HWND rather than hide to tray, or fail before close interception.");
        Console.WriteLine("PASS: close-to-tray cancels before preference/placement work; updater tolerates exited launcher but rejects unrelated live PID.");
        var selectorViewSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "Views", "WorkspaceSelectorView.xaml.cs"));
        var selectorClose = desktopSource.IndexOf("public void CancelWorkspaceSelectorFromHost()", StringComparison.Ordinal);
        var selectorNext = desktopSource.IndexOf("private void ShowActiveWorkspace()", selectorClose, StringComparison.Ordinal);
        var selectorBody = selectorClose > 0 && selectorNext > selectorClose
            ? desktopSource.Substring(selectorClose, selectorNext - selectorClose) : "";
        var trayOpen = desktopSource.IndexOf("private void ShowActiveWorkspace()", StringComparison.Ordinal);
        var trayWindowHandler = desktopSource.IndexOf("public void HandleMainWindowClosing(CancelEventArgs e)", trayOpen, StringComparison.Ordinal);
        var trayOpenBody = trayOpen > 0 && trayWindowHandler > trayOpen
            ? desktopSource.Substring(trayOpen, trayWindowHandler - trayOpen) : "";
        Assert(selectorViewSource.Contains("app.CancelWorkspaceSelectorFromHost();", StringComparison.Ordinal) &&
               selectorBody.Contains("if (_workspaceSelectorInitial)", StringComparison.Ordinal) &&
               selectorBody.Contains("HandleMainWindowClosing(new CancelEventArgs());", StringComparison.Ordinal) &&
               !selectorBody.Contains("ExitApplication();", StringComparison.Ordinal) &&
               trayOpenBody.Contains("_workspaceSelectorVisible && _mainWindow is", StringComparison.Ordinal) &&
               trayOpenBody.Contains("selectorHost.Show();", StringComparison.Ordinal),
            "Initial Workspace Selector X still terminates AAH instead of preserving the tray listener and reopening its selector.");
        Console.WriteLine("PASS: initial selector Close and native window X both hide to tray; selector restored from existing tray menu.");

        var version533 = new Version(4, 0, 0, 533);
        var version534 = new Version(4, 0, 0, 534);
        Assert(Program.IsUpgradeOrRestartRecovery(version533, version534, "v4.0.0.534"),
            "Normal newer signed/checksummed release rejected.");
        Assert(Program.IsUpgradeOrRestartRecovery(version534, version534, "v4.0.0.534"),
            "Already-copied verified release cannot complete its restart.");
        Assert(!Program.IsUpgradeOrRestartRecovery(version534, version533, "v4.0.0.533"),
            "Updater accepted a downgrade.");
        Assert(!Program.IsUpgradeOrRestartRecovery(version533, version534, "v4.0.0.999"),
            "Updater accepted a binary whose version differs from the stable release.");
        Assert(desktopSource.Contains("DispatcherPriority.Loaded, new Action(() =>", StringComparison.Ordinal) &&
               desktopSource.Contains("ConfirmStartupReady();", StringComparison.Ordinal),
            "WPF startup readiness still depends solely on ContextIdle and can be starved.");
        Console.WriteLine("PASS: exact-install-only multi-process closure, no unrelated processes, newer/same-version recovery, downgrade rejection and Loaded readiness.");

        // A verified PID lacking a shutdown listener may exit in parallel
        // with another same-path sibling's cleanup. Old v575 treated the
        // first false result as final even if no AAH process remained and
        // refused the verified installer. A NEW exact-path process census,
        // not the stale request result, determines whether it is safe.
        Assert(!Program.ShouldDeferAfterFreshProcessCensus(
                new[] { false, true }, 0) &&
               Program.ShouldDeferAfterFreshProcessCensus(
                   new[] { false, true }, 1) &&
               !Program.ShouldDeferAfterFreshProcessCensus(
                   new[] { true }, 0),
            "Updater abandoned replacement after all AAH processes exited, or ignored an active one.");
        var updaterSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
            "AAH.UpdateHost", "Program.cs"));
        Assert(updaterSource.Contains("GetSameInstallationProcesses(installedExe)", StringComparison.Ordinal) &&
               updaterSource.Contains("ShouldDeferAfterFreshProcessCensus(results, stillRunning.Count)", StringComparison.Ordinal) &&
               updaterSource.Contains("foreach (var process in stillRunning) process.Dispose();", StringComparison.Ordinal),
            "Updater does not recensus verified live processes after a listener-timeout race.");
        Console.WriteLine("PASS: after listener timeout, fresh same-install PID census decides installer gate.");

        // v582 regression from 2026-11-03 installed v579: PIDs 15912 and
        // 17296 logged safe verified-update completion but remained alive;
        // a one-time recovery script then found a NEW PID 2432. No fixed PID
        // list can solve that. Only the signed/verified update host, checking
        // each exact executable, process start time and complete audit, may
        // retire a process that has already finished ALL accounting cleanup.
        var finishedAt = new DateTimeOffset(2026, 11, 03, 07, 03, 09, TimeSpan.Zero);
        var processStartedAt = finishedAt.AddMinutes(-7);
        var shutdownMarkers = new[]
        {
            "Verified updater requested graceful exit; new automation intake quiesced.",
            "Verified updater: in-flight accounting/Tally idle for 3 checks; exiting.",
            "Application shutdown started; stopping Accounting/Tally runtime.",
            "Tally coordinator disposal completed.",
            "Workspace cache disposal completed.",
            "Runtime cleanup finished; instance lock remains owned until OnExit completes.",
            "Process exit releasing final single-instance ownership."
        };
        static string EvidenceLine(DateTimeOffset when, int pid, string message) =>
            when.ToString("O", System.Globalization.CultureInfo.InvariantCulture) +
            " PID " + pid + ": " + message;
        var knownGood = string.Join("\n", shutdownMarkers.Select((message, index) =>
            EvidenceLine(finishedAt.AddSeconds(index - shutdownMarkers.Length + 1), 17296, message)));
        Assert(Program.HasCompletedVerifiedExitEvidence(
                knownGood, 17296, processStartedAt, finishedAt.AddSeconds(15), out var completed) &&
               completed == finishedAt,
            "A real complete PID-scoped safe-shutdown history was rejected.");
        Assert(!Program.HasCompletedVerifiedExitEvidence(
                knownGood, 2432, processStartedAt, finishedAt.AddSeconds(15), out _),
            "A newly launched unknown PID was authorized from an old process log.");
        Assert(!Program.HasCompletedVerifiedExitEvidence(
                knownGood, 17296, finishedAt.AddMinutes(1), finishedAt.AddMinutes(2), out _),
            "Reused PID accepted shutdown evidence from before its own process start.");
        Assert(!Program.HasCompletedVerifiedExitEvidence(
                knownGood, 17296, processStartedAt, finishedAt.AddSeconds(-15), out _),
            "Future-dated shutdown evidence authorized a termination.");
        foreach (var omitted in Enumerable.Range(0, shutdownMarkers.Length))
        {
            var incomplete = string.Join("\n", shutdownMarkers
                .Where((_, index) => index != omitted)
                .Select((message, index) => EvidenceLine(
                    finishedAt.AddSeconds(index - shutdownMarkers.Length + 1), 17296, message)));
            Assert(!Program.HasCompletedVerifiedExitEvidence(
                    incomplete, 17296, processStartedAt, finishedAt.AddSeconds(15), out _),
                "Updater accepted partial Tally/runtime/OnExit cleanup evidence: stage " + omitted);
        }
        // AAH v579 user Exit Completely also left PID 8896 alive despite
        // confirmed no Tally/import work and completed runtime/WAL/OnExit.
        // Never treat normal X-to-tray as a request for process termination.
        var explicitUserExitMarkers = new[]
        {
            "User requested Exit Completely: no in-flight accounting/Tally work.",
            "Application shutdown started; stopping Accounting/Tally runtime.",
            "Tally coordinator disposal completed.",
            "Workspace cache disposal completed.",
            "Runtime cleanup finished; instance lock remains owned until OnExit completes.",
            "Process exit releasing final single-instance ownership."
        };
        var manualExit = string.Join("\n", explicitUserExitMarkers.Select((message, index) =>
            EvidenceLine(finishedAt.AddSeconds(index - explicitUserExitMarkers.Length + 1),
                8896, message)));
        Assert(Program.HasCompletedSafeUserExitEvidence(
                   manualExit, 8896, processStartedAt, finishedAt.AddSeconds(15), out var manualCompleted) &&
               manualCompleted == finishedAt,
            "AAH user Exit Completely ghost not recognized after full safe cleanup.");
        Assert(!Program.HasCompletedSafeUserExitEvidence(
                   manualExit, 17952, processStartedAt, finishedAt.AddSeconds(15), out _),
            "AAH live tray PID accepted another PID's safe manual exit.");
        Assert(!Program.HasCompletedSafeUserExitEvidence(
                   manualExit, 8896, finishedAt.AddSeconds(10), finishedAt.AddSeconds(15), out _),
            "Recycled manual-exit PID reused an earlier process's cleanup.");
        foreach (var omitted in Enumerable.Range(0, explicitUserExitMarkers.Length))
        {
            var incompleteManual = string.Join("\n", explicitUserExitMarkers
                .Where((_, index) => index != omitted)
                .Select((message, index) => EvidenceLine(
                    finishedAt.AddSeconds(index - explicitUserExitMarkers.Length + 1),
                    8896, message)));
            Assert(!Program.HasCompletedSafeUserExitEvidence(
                    incompleteManual, 8896, processStartedAt, finishedAt.AddSeconds(15), out _),
                "Partial manual exit cleanup authorized old PID retirement: stage " + omitted);
        }
        var trayOnly = EvidenceLine(finishedAt, 17952,
            "Main window close intercepted; AAH remains alive in tray for background workers and auto-update.");
        Assert(!Program.HasCompletedSafeUserExitEvidence(
                   trayOnly, 17952, processStartedAt, finishedAt.AddSeconds(15), out _),
            "Ordinary X-to-tray authorized running AAH termination.");
        var manualDeferred = EvidenceLine(finishedAt.AddSeconds(-6), 8896,
                explicitUserExitMarkers[0]) + "\n" +
            EvidenceLine(finishedAt.AddSeconds(-5), 8896,
                "User exit deferred: tally=True automation=False distributed=False") + "\n" +
            string.Join("\n", explicitUserExitMarkers.Skip(1).Select((message, index) =>
                EvidenceLine(finishedAt.AddSeconds(index - 4), 8896, message)));
        Assert(!Program.HasCompletedSafeUserExitEvidence(
                   manualDeferred, 8896, processStartedAt, finishedAt.AddSeconds(15), out _),
            "Manual Exit deferred for active Tally must not authorize process retirement.");

        // A forged/recycled PID history with later log lines but earlier
        // timestamps cannot authorize retirement of a different process.
        var reversedClock = string.Join("\n", shutdownMarkers.Select((message, index) =>
            EvidenceLine(finishedAt.AddSeconds(-index), 17296, message)));
        Assert(!Program.HasCompletedVerifiedExitEvidence(
                reversedClock, 17296, processStartedAt, finishedAt.AddSeconds(15), out _),
            "Out-of-order timestamp audit authorized retirement of a live PID.");
        var outOfOrder = shutdownMarkers.ToArray();
        (outOfOrder[3], outOfOrder[4]) = (outOfOrder[4], outOfOrder[3]);
        var mixed = string.Join("\n", outOfOrder.Select((message, index) =>
            EvidenceLine(finishedAt.AddSeconds(index - shutdownMarkers.Length + 1), 17296, message)));
        Assert(!Program.HasCompletedVerifiedExitEvidence(
                mixed, 17296, processStartedAt, finishedAt.AddSeconds(15), out _),
            "Out-of-order shutdown markers incorrectly authorized process retirement.");
        var deferred = knownGood.Replace(
            shutdownMarkers[3],
            "Verified updater deferred: active accounting/Tally did not finish.\n" + shutdownMarkers[3],
            StringComparison.Ordinal);
        Assert(!Program.HasCompletedVerifiedExitEvidence(
                deferred, 17296, processStartedAt, finishedAt.AddSeconds(15), out _),
            "Updater accepted a cancelled/deferred verified shutdown.");
        Assert(updaterSource.Contains(
                   "await RetireVerifiedCleanedGhostAsync(parent, verifiedInstalledExe)", StringComparison.Ordinal) &&
               updaterSource.Contains(
                   "HasCompletedVerifiedExitEvidence(", StringComparison.Ordinal) &&
               updaterSource.Contains(
                   "HasCompletedSafeUserExitEvidence(", StringComparison.Ordinal) &&
               updaterSource.Contains(
                   "IsExactInstallationProcess(process.ProcessName, actualPath, verifiedInstalledExe)", StringComparison.Ordinal) &&
               updaterSource.Contains("SameProcessPath(process, verifiedInstalledExe)", StringComparison.Ordinal) &&
               updaterSource.Contains("process.Kill(entireProcessTree: false);", StringComparison.Ordinal) &&
               updaterSource.Contains("GetSameInstallationProcesses(installedExe)", StringComparison.Ordinal),
            "Updater does not apply strict same-install/evidence-gated ghost retirement in the existing host.");
        Console.WriteLine(
            "PASS: host automatically retires only exact-install completed-verified-shutdown ghosts; " +
            "active Tally, unproven and reused PIDs remain untouched.");

        Assert(Program.PublisherIdentityCompatible(null, null),
            "Two unsigned checksummed GitHub releases were rejected.");
        Assert(Program.PublisherIdentityCompatible("AAA", "aaa"),
            "Identical trusted signed publishers should match.");
        Assert(!Program.PublisherIdentityCompatible("AAA", "BBB") &&
               !Program.PublisherIdentityCompatible(null, "AAA") &&
               !Program.PublisherIdentityCompatible("AAA", null),
            "Mismatch or partial signing was permitted.");
        Console.WriteLine("PASS: signed publishers must match; DeskSpace-style unsigned channel is consistent.");

        var install = Path.Combine(root, "installed");
        var publish = Path.Combine(root, "publish");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(publish);
        var target = Path.Combine(install, "AccountingAutomationHub.exe");
        File.WriteAllText(target, "ORIGINAL EXE");
        File.WriteAllText(Path.Combine(install, "plugin.dll"), "ORIGINAL DLL");
        var records = Path.Combine(install, "private-accounts.sqlite");
        File.WriteAllText(records, "KEEP BOOKS");
        File.WriteAllText(Path.Combine(publish, "AccountingAutomationHub.exe"), "NEW EXE");
        File.WriteAllText(Path.Combine(publish, "plugin.dll"), "NEW DLL");
        File.WriteAllText(Path.Combine(publish, "new-support.dll"), "NEW FILE");
        var tx = Program.StageTransaction(publish, install);
        Program.ApplyTransaction(tx, install);
        Assert(File.ReadAllText(target) == "NEW EXE", "Application was not upgraded.");
        Assert(File.ReadAllText(Path.Combine(install, "new-support.dll")) == "NEW FILE", "New files missing.");
        Assert(File.ReadAllText(records) == "KEEP BOOKS", "Financial data changed.");
        Program.RollbackTransaction(tx, install);
        Assert(File.ReadAllText(target) == "ORIGINAL EXE", "Rollback did not restore prior EXE.");
        Assert(File.ReadAllText(Path.Combine(install, "plugin.dll")) == "ORIGINAL DLL", "Rollback did not restore prior DLL.");
        Assert(!File.Exists(Path.Combine(install, "new-support.dll")), "Rollback left newly created DLL.");
        Assert(File.ReadAllText(records) == "KEEP BOOKS", "Rollback removed books.");
        Console.WriteLine("PASS: application replacement and transactional rollback preserve accounting data.");

        tx = Program.StageTransaction(publish, install);
        Program.ApplyTransaction(tx, install);
        Program.RecoverPending(install);
        Assert(File.ReadAllText(target) == "ORIGINAL EXE", "Interrupted transaction was not recovered.");
        Assert(File.ReadAllText(records) == "KEEP BOOKS", "Interrupted update changed financial data.");
        Console.WriteLine("PASS: interrupted update journal recovers original binaries.");

        var preJournal = Path.Combine(install, ".aah-github-swap-orphaned");
        Directory.CreateDirectory(preJournal);
        File.WriteAllText(Path.Combine(preJournal,"staging.tmp"), "DISPOSABLE");
        Program.RecoverPending(install);
        Assert(!Directory.Exists(preJournal), "Pre-journal staging directory not reclaimed.");
        Console.WriteLine("PASS: orphaned staging is cleaned without altering installed files.");

        var badFile = Path.Combine(publish,"accounts.db");
        File.WriteAllText(badFile,"PRIVATE");
        var wasBlocked = false;
        try { Program.StageTransaction(publish, install); }
        catch (InvalidDataException) { wasBlocked = true; }
        Assert(wasBlocked, "Accounting data was allowed into release payload.");
        File.Delete(badFile);
        Console.WriteLine("PASS: accounting database excluded from application update.");

        var zip = Path.Combine(root,"bad.zip");
        using (var f = File.Open(zip,FileMode.Create))
        using (var archive = new ZipArchive(f,ZipArchiveMode.Create))
        {
            var evil = archive.CreateEntry("../outside.txt");
            using var stream = new StreamWriter(evil.Open());
            stream.Write("ESCAPE");
        }
        wasBlocked = false;
        try { Program.ExtractStrict(zip,Path.Combine(root,"extract")); }
        catch (InvalidDataException) { wasBlocked=true; }
        Assert(wasBlocked,"ZIP traversal was not prevented.");
        Assert(!File.Exists(Path.Combine(root,"outside.txt")),"ZIP wrote outside extraction directory.");
        Console.WriteLine("PASS: ZIP traversal rejected before application replacement.");

        // Repeat two successful automatic releases on the SAME installed app.
        // Both application binary and updater binary must be replaced in place,
        // and no second install folder or old transaction directory may remain.
        var updaterPath = Path.Combine(install, "AAH_Update.exe");
        File.WriteAllText(updaterPath, "PREVIOUS UPDATER");
        File.WriteAllText(Path.Combine(publish, "AAH_Update.exe"), "RELEASE 1 UPDATER");
        File.WriteAllText(target, "ORIGINAL EXE");
        var firstRelease = Program.StageTransaction(publish, install);
        Program.ApplyTransaction(firstRelease, install);
        Assert(File.ReadAllText(updaterPath) == "RELEASE 1 UPDATER",
            "First release did not replace old installed updater.");
        Program.CompleteTransaction(firstRelease);
        Assert(!Directory.Exists(firstRelease), "Committed release retained old app files.");

        File.WriteAllText(Path.Combine(publish, "AccountingAutomationHub.exe"), "RELEASE 2 EXE");
        File.WriteAllText(Path.Combine(publish, "AAH_Update.exe"), "RELEASE 2 UPDATER");
        var nextRelease = Program.StageTransaction(publish, install);
        Program.ApplyTransaction(nextRelease, install);
        Assert(File.ReadAllText(target) == "RELEASE 2 EXE" &&
               File.ReadAllText(updaterPath) == "RELEASE 2 UPDATER",
            "Second release left an older application or updater executable.");
        Assert(File.ReadAllText(records) == "KEEP BOOKS",
            "Repeated releases changed accounting data.");
        Program.CompleteTransaction(nextRelease);
        Assert(!Directory.Exists(nextRelease) &&
               Directory.GetDirectories(install, ".aah-github-swap-*").Length == 0,
            "Repeated releases retained prior app/update folders.");
        Console.WriteLine("PASS: repeated auto-updates replace old AAH_Update.exe and binaries without duplicate install or retained swap folders.");

        var maliciousJournal = Program.StageTransaction(publish, install);
        Program.ApplyTransaction(maliciousJournal,install);
        File.WriteAllText(target, "EXTERNALLY MODIFIED");
        wasBlocked=false;
        try { Program.RollbackTransaction(maliciousJournal,install); }
        catch (InvalidDataException) { wasBlocked=true; }
        Assert(wasBlocked,"Rollback overwrote an externally changed executable.");
        Console.WriteLine("PASS: rollback refuses unexpected externally modified application bytes.");
    }

    internal static int Main()
    {
        var temp=Path.Combine(Path.GetTempPath(),"AAH-UpdateHost-Test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Test(temp);
            Console.WriteLine("PASS: 8/8 AAH UpdateHost transactional safety scenarios.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(temp,true); } catch { }
        }
    }
}
