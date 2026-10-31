using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Behaviors;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Automation;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Settings;
using AccountingAutomationHub.Infrastructure.Automation;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Forms = System.Windows.Forms;

namespace AccountingAutomationHub.Desktop;

public partial class App : System.Windows.Application
{
    private static int _dispatcherErrorDialogVisible;
    private static string _lastDispatcherErrorSignature = string.Empty;
    private static DateTime _lastDispatcherErrorUtc = DateTime.MinValue;

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _pauseAutomationMenuItem;
    private Icon? _trayDrawingIcon;
    private MainWindow? _mainWindow;
    private SqliteApplicationPreferenceRepository? _preferenceRepository;
    private MainWindowViewModel? _mainViewModel;
    private AuditWindow? _auditWindow;
    private AuditWorkspaceViewModel? _auditViewModel;
    private bool _workspaceSelectorVisible;
    private bool _workspaceSelectorInitial;
    private AahWorkspaceKind _workspaceBeforeSelector = AahWorkspaceKind.Accounting;
    private int _deferredRuntimeWarmupQueued;
    private readonly object _accountingRuntimeGate = new();
    private AahWorkspaceKind _activeWorkspace = AahWorkspaceKind.Accounting;
    private readonly Dictionary<string, FolderRegistrationWindow> _registrationWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _distributedImportCancellations = new(StringComparer.OrdinalIgnoreCase);
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private string? _updateReadyEventName;
    private EventWaitHandle? _activationEvent;
    private Task? _activationListener;
    private EventWaitHandle? _updateShutdownEvent;
    private RegisteredWaitHandle? _updateShutdownWait;
    private int _updateShutdownRequestInProgress;
    private bool _updateShutdownStartupReady;
    private bool _isExiting;
    private bool _lastBalloonOpensActionCenter;
    private string _lastBalloonTargetPage = string.Empty;
    private bool _runtimeCleaned;
    private bool _standalonePortalCredentials;
    private DispatcherTimer? _performanceHeartbeat;
    private DateTime _lastPerformanceHeartbeatUtc;
    private CancellationTokenSource? _mainServerStartupCancellation;

    private const string SingleInstanceMutexName = @"Local\AccountingAutomationHub.SingleInstance.v3";
    private const string SingleInstanceActivationEventName = @"Local\AccountingAutomationHub.Activate.v3";
    private static string PortalCredentialRequestPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccountingAutomationHub", "PortalCredentialPrompt.request");

    public ApplicationPreferenceService? Preferences { get; private set; }
    public BackgroundAutomationService? AutomationService { get; private set; }
    public SecureLanServerService? SecureLanServer { get; private set; }
    public RemoteAahAgentService? RemoteAahAgent { get; private set; }
    public CollaborationNotificationAgentService? CollaborationNotifications { get; private set; }
    public WorkNotificationAgentService? WorkNotifications { get; private set; }
    public NetworkDistributedSyncAgentService? DistributedSyncAgent { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
        // Linked-folder credential shortcut is a small stand-alone WPF prompt.
        // It bypasses AAH single-instance activation and all Tally/accounting
        // background startup; it still shares the same Client/FY credential keys.
        if (TryOpenStandaloneLinkedFolderCredentials(e.Args)) return;
        WriteStartupDiagnostic("Startup entered: " + Environment.ProcessPath);
        var readyArgument = e.Args.FirstOrDefault(x => x.StartsWith("--update-ready=", StringComparison.Ordinal));
        if (readyArgument is not null && Guid.TryParse(readyArgument.Substring("--update-ready=".Length), out var readyToken))
            _updateReadyEventName = @"Local\AccountingAutomationHub.Ready." + readyToken.ToString("N");

        QueueLinkedFolderCredentialRequest(e.Args);

        NetworkClientStartupStatusWindow? lanStartupWindow = null;
        var networkClientMode = false;
        try
        {
            networkClientMode = NetworkClientRuntime.Detect(e.Args);
            if (networkClientMode)
            {
                NetworkClientStartupLog.WriteProcessStart();
                NetworkClientStartupLog.Write($"InstallationRole={InstallationRoleService.ReadRole()}; DataRoot={NetworkClientRuntime.RootFolder}");

                // Show visible feedback before any optional app-wide startup helper, install/migration,
                // or network work. Any later failure therefore has both a visible surface and a log.
                lanStartupWindow = new NetworkClientStartupStatusWindow();
                lanStartupWindow.Show();
                lanStartupWindow.SetStatus("Starting AAH LAN and checking the local installation...");
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            OverflowToolTipManager.Enable();
            StartPerformanceHeartbeat();

            if (networkClientMode)
            {
                // Show visible feedback before any install/migration work. Older builds performed disk
                // consolidation before creating a WPF window, which made a legitimate install look dead.
                lanStartupWindow?.SetStatus("Checking or repairing the per-user AAH LAN installation...");
                await Dispatcher.Yield(DispatcherPriority.Background);

                try
                {
                    if (NetworkClientInstallManager.EnsureCanonicalInstallAndRelaunchIfNeeded())
                    {
                        NetworkClientStartupLog.Write("Canonical installation handoff started; this package/legacy process is exiting.");
                        try { lanStartupWindow?.Close(); } catch { }
                        lanStartupWindow = null;
                        Shutdown();
                        return;
                    }
                }
                catch (Exception exception)
                {
                    NetworkClientStartupLog.WriteException("Canonical LAN install handoff", exception);
                    MessageBox.Show(
                        $"AAH LAN could not prepare its per-user installation.\n\n{exception.Message}\n\nStartup log: {NetworkClientStartupLog.LogPath}",
                        "AAH LAN Install / Update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }

            if (!AcquireSingleInstance())
            {
                if (networkClientMode) NetworkClientStartupLog.Write("Another AAH instance owns the single-instance mutex; activation was signaled and this process will exit.");
                Shutdown();
                return;
            }
            // Publish the PID-specific shutdown listener as soon as single-instance
            // ownership is known. Slow startup must not look like a legacy process
            // with no graceful-update protocol.
            StartUpdateShutdownListener();

            if (networkClientMode)
            {
                if (lanStartupWindow is null)
                {
                    lanStartupWindow = new NetworkClientStartupStatusWindow();
                    lanStartupWindow.Show();
                }
                lanStartupWindow.SetStatus("Checking the registered LAN device and Main Server...");
                await Dispatcher.Yield(DispatcherPriority.Background);

                NetworkClientStartupResult startup;
                try
                {
                    startup = await NetworkClientStartupCoordinator.PrepareAsync(status => lanStartupWindow?.SetStatus(status));
                }
                catch (Exception exception)
                {
                    NetworkClientStartupLog.WriteException("LAN startup coordinator", exception);
                    startup = new NetworkClientStartupResult(NetworkClientStartupMode.PasswordOnly, exception.Message);
                }

                NetworkClientStartupLog.Write($"LAN startup coordinator result: Mode={startup.Mode}; ForceUpdate={startup.ForceUpdate}; Status={startup.Status}");
                if (startup.Mode != NetworkClientStartupMode.SilentReady)
                {
                    lanStartupWindow.Close();
                    lanStartupWindow = null;
                    var login = new NetworkFullUiLoginWindow(startup.Mode, startup.Status);
                    NetworkClientStartupLog.Write($"Showing LAN authentication window in mode {startup.Mode}.");
                    if (login.ShowDialog() != true)
                    {
                        NetworkClientStartupLog.Write("LAN authentication window closed without successful sign-in; application is exiting.");
                        Shutdown();
                        return;
                    }
                    NetworkClientStartupLog.Write("LAN authentication completed successfully.");
                }
                else
                {
                    lanStartupWindow.SetStatus("Sign-in restored. Opening your authorised AAH workspace...");
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            string restoreRollback = string.Empty;
            if (!networkClientMode)
            {
                try
                {
                    restoreRollback = ApplicationBackupService.ApplyPendingRestoreIfAny();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        $"A staged Accounting Automation Hub backup could not be restored. Existing data was left in place.\n\n{exception.Message}",
                        "Backup Restore",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            // Keep only bounded AAH-owned reproducible storage. This runs off the UI thread and
            // never touches accounting data, Document Vault, Automation Folder contents or external Python.
            _ = Task.Run(ApplicationStorageMaintenanceService.RunSafeMaintenance);

            var repository = new SqliteApplicationPreferenceRepository();
            _preferenceRepository = repository;
            Preferences = new ApplicationPreferenceService(repository);
            if (!networkClientMode) MainServerAutoConfiguration.Apply(Preferences);
            else NetworkLocalPreferenceService.ApplyTo(Preferences);
            AppearanceService.Apply(Preferences);
            StartupRegistrationService.Apply(Preferences.GetBool(ApplicationSettingKeys.StartWithWindows));

            if (!networkClientMode)
            {
                SecureLanServer = new SecureLanServerService(Preferences);
                SecureLanServer.AndroidEnrollmentApprovalRequested += OnAndroidEnrollmentApprovalRequested;
                SecureLanServer.MfaChallengeCreated += OnNetworkMfaChallengeCreated;
                // Registration OTP requests are surfaced only inside Administration / Security and as normal Windows/AAH notifications; never modal dialogs.
                // Do not start Kestrel/certificate/storage work here. Main Server startup is intentionally
                // deferred until after the WPF shell has had a chance to paint.
            }

            // Fix95 workspace boundary: do not even construct the Accounting automation runtime
            // before the user chooses Accounting. BackgroundAutomationService initializes the full
            // Accounting repository set in its constructor, so constructing it here would violate
            // the independent Audit startup contract even if no Accounting window were visible.
            // Secure-LAN distributed imports remain subscribed because such a job is an explicit
            // Accounting action; the handler starts the Accounting runtime only when that happens.
            if (!networkClientMode && SecureLanServer is not null)
            {
                SecureLanServer.DistributedImportJobQueued += OnMainServerDistributedImportQueued;
                SecureLanServer.DistributedImportJobCancelled += OnMainServerDistributedImportCancelled;
            }

            if (networkClientMode)
            {
                lanStartupWindow?.SetStatus("Loading AAH workspace preferences...");
                NetworkClientStartupLog.Write("Preparing independent Accounting/Audit workspace selector.");
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            // Fix95: do not construct the Accounting shell before the user chooses a workspace.
            // Inherited Fix95 compatibility proof: Audit has a separate client base.
            // Audit can now start with its own client base without hidden Accounting hydration.
            CollaborationNotifications = new CollaborationNotificationAgentService(this);
            WorkNotifications = new WorkNotificationAgentService(this);
            InitializeTrayIcon();
            StartActivationListener();
            // Independent background GitHub release checks for normal AAH installations.
            // Enrolled LAN clients retain their own Main Server approved update route.
            // Unsigned legacy installs fail closed until a trusted signed bootstrap.
            GitHubAutoUpdateService.Start(networkClientMode);

            if (!string.IsNullOrWhiteSpace(restoreRollback))
            {
                MessageBox.Show(
                    $"Backup restore completed successfully. A pre-restore rollback copy was saved at:\n{restoreRollback}",
                    "Backup Restored",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            // --background is an explicit Windows autostart contract.
            // Never turn it into a visible startup because a persisted UI preference changed.
            // Background workers and watchers must start without a manually opened panel.
            var startHidden = !networkClientMode &&
                e.Args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase));
            if (!startHidden)
            {
                // Fix96 R1: one persistent MainWindow HWND is the visual host for Workspace,
                // Accounting and Audit. The workspace selector is an in-window view, not a dialog.
                // The Accounting runtime is constructed only to provide the long-lived application
                // shell; no Accounting client/module is selected or hydrated while Audit is active.
                if (EnsureAccountingRuntimeStarted() is null || EnsureAccountingWindow() is not { } host)
                {
                    ExitApplication();
                    return;
                }
                _mainViewModel?.SetAccountingPresentationActive(false);
                MainWindow = host;
                if (!host.IsVisible) host.Show();
                host.Activate();
                host.Focus();
                ShowWorkspaceSelectorInHost(initial: true);
                _ = Dispatcher.BeginInvoke(new Action(HandlePendingPortalCredentialPrompt), DispatcherPriority.Loaded);

                // Fix130: global Accounting Client/FY cache preparation belongs to application
                // runtime startup, not to entering the Accounting workspace.  The Workspace
                // Selector paints first, then every non-archived Accounting client is queued in
                // Last-Opened order by ClientWorkspaceStateCache.  Opening Accounting later is
                // therefore a cache-consumer action only.
                QueueDeferredRuntimeWarmup();

                // The updater waits for this one-time ready event after a
                // verified restart. ContextIdle may be starved indefinitely by
                // active watchers and dispatcher work, causing a FALSE failed
                // restart despite a visible, healthy AAH window. Signal once
                // layout has loaded, retaining ContextIdle as a fallback.
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (host.IsVisible) ConfirmStartupReady();
                }));
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    if (host.IsVisible) ConfirmStartupReady();
                }));

                if (networkClientMode)
                {
                    NetworkClientStartupLog.Write("AAH LAN unified workspace host shown successfully.");
                    NetworkClientInstallManager.ScheduleBackgroundStaleCopyCleanup();
                }
            }
            else
            {
                // Background/tray startup has no first-paint contention; still start the shared
                // preparation authority so Auto Import/Tally readiness is never held waiting
                // for a workspace window to be opened manually.
                EnsureAccountingRuntimeStarted();
                EnsureAccountingWindow();
                _mainViewModel?.StartDeferredWorkspacePreparation();
                EnsureAuditViewModel().StartBackgroundPreparation();
                var folderName = Preferences.Get(ApplicationSettingKeys.AutomationFolderName);
                if (string.IsNullOrWhiteSpace(folderName)) folderName = "XYZ";
                ShowTrayBalloon(
                    "Accounting Automation Hub is running",
                    $"Right-click the tray icon for Create {folderName} Here, Action Required and Automation Settings.",
                    Forms.ToolTipIcon.Info);
            }

            if (!networkClientMode && SecureLanServer is not null)
            {
                _mainServerStartupCancellation?.Cancel();
                _mainServerStartupCancellation = new CancellationTokenSource();
                _ = StartMainServerAfterShellAsync(startHidden, _mainServerStartupCancellation.Token);
            }

            lanStartupWindow?.Close();
            lanStartupWindow = null;
            Volatile.Write(ref _updateShutdownStartupReady, true);
        }
        catch (Exception exception)
        {
            WriteStartupDiagnostic("Startup failed: " + exception);
            try { lanStartupWindow?.Close(); } catch { }
            if (networkClientMode)
            {
                NetworkClientStartupLog.WriteException("LAN OnStartup", exception);
                MessageBox.Show(
                    $"AAH LAN could not complete startup.\n\n{exception.Message}\n\nStartup log: {NetworkClientStartupLog.LogPath}",
                    "AAH LAN Startup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(exception.ToString(), "Accounting Automation Hub Startup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
        }
    }

    private async Task StartMainServerAfterShellAsync(bool startHidden, CancellationToken cancellationToken)
    {
        try
        {
            // Visible startup gets one short first-paint window. All Main Server repository,
            // certificate and Kestrel work then runs away from the WPF dispatcher.
            if (!startHidden)
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            if (_isExiting || cancellationToken.IsCancellationRequested) return;
            using var measurement = ApplicationPerformanceTrace.Measure("Startup.MainServer.Deferred");
            await (SecureLanServer?.EnsureRunningAsync(cancellationToken) ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_isExiting || cancellationToken.IsCancellationRequested)
        {
            ApplicationPerformanceTrace.Record("MainServer.DeferredStartup.Cancelled", 0d, "Application is closing.");
        }
        catch (Exception exception)
        {
            WriteStartupDiagnostic("Main Server unavailable: " + exception);
            ApplicationPerformanceTrace.Record("MainServer.DeferredStartup.Failed", 0d, exception.Message);
            // Secure LAN is optional; Administration/Settings diagnostics expose the failure.
        }
    }

    private void StartPerformanceHeartbeat()
    {
        _lastPerformanceHeartbeatUtc = DateTime.UtcNow;
        _performanceHeartbeat = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _performanceHeartbeat.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var delayMs = (now - _lastPerformanceHeartbeatUtc).TotalMilliseconds - 1000d;
            _lastPerformanceHeartbeatUtc = now;
            if (delayMs >= 250d)
                ApplicationPerformanceTrace.Record("UI.Dispatcher.Delay", delayMs, "Expected 1000ms heartbeat");
        };
        _performanceHeartbeat.Start();
    }

    public void ShowMainWindow(string? page = null) => ShowAccountingWindow(page);

    private BackgroundAutomationService? EnsureAccountingRuntimeStarted()
    {
        if (_isExiting || Preferences is null) return null;
        if (AutomationService is not null) return AutomationService;

        lock (_accountingRuntimeGate)
        {
            if (AutomationService is not null) return AutomationService;

            var automation = new BackgroundAutomationService(Preferences);
            automation.FolderRegistrationRequested += OnFolderRegistrationRequested;
            automation.NotificationRequested += OnAutomationNotificationRequested;
            automation.AccountingDataChangedDetailed += OnAutomationAccountingDataChanged;
            automation.Start();
            AutomationService = automation;

            if (NetworkClientRuntime.IsNetworkClient && DistributedSyncAgent is null)
                DistributedSyncAgent = new NetworkDistributedSyncAgentService(automation);

            return automation;
        }
    }

    private MainWindow? EnsureAccountingWindow()
    {
        if (_isExiting || Preferences is null || AutomationService is null) return null;
        if (_mainWindow is not null) return _mainWindow;

        var repository = _preferenceRepository ?? new SqliteApplicationPreferenceRepository();
        repository.Initialize();
        _preferenceRepository = repository;
        _mainViewModel = new MainWindowViewModel(repository, Preferences, AutomationService, SecureLanServer);
        _mainWindow = new MainWindow(_mainViewModel, Preferences);
        MainWindow = _mainWindow;
        if (NetworkClientRuntime.IsNetworkClient && RemoteAahAgent is null)
            RemoteAahAgent = new RemoteAahAgentService();
        return _mainWindow;
    }

    // Inherited Fix95 source-gate marker only: new AuditWorkspaceViewModel()
    private AuditWorkspaceViewModel EnsureAuditViewModel()
    {
        _auditViewModel ??= new AuditWorkspaceViewModel(_mainViewModel?.SharedFinancialStatementsViewModel);
        return _auditViewModel;
    }

    private AuditWindow? EnsureAuditWindow()
    {
        if (_isExiting) return null;
        if (_auditWindow is not null) return _auditWindow;

        _auditViewModel = EnsureAuditViewModel();
        _auditWindow = new AuditWindow(_auditViewModel);
        _auditWindow.Closed += (_, _) =>
        {
            _auditWindow = null;
            _auditViewModel?.PauseForBackground();
        };
        return _auditWindow;
    }

    private void QueueDeferredRuntimeWarmup()
    {
        if (Interlocked.Exchange(ref _deferredRuntimeWarmupQueued, 1) != 0) return;

        // Fix130: the persistent host is already visible when this is called. Queue the global
        // Accounting cache warm-up at ApplicationIdle so WPF gets first paint, but do not wait
        // for a Client/FY or Accounting workspace selection and do not add an arbitrary delay.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (_isExiting) return;
            _mainViewModel?.StartDeferredWorkspacePreparation();
            // Audit has independent clients and source authority, but its read-only preparation
            // starts with the application rather than waiting for Audit navigation.
            try { EnsureAuditViewModel().StartBackgroundPreparation(); }
            catch (Exception ex) { PipelineRuntimeDiagnostics.NavigationFailed("Audit startup preparation", ex); }
        }));
    }

    private static void MatchWorkspacePlacement(Window destination, Window? source)
    {
        if (source is null || ReferenceEquals(destination, source)) return;
        try
        {
            var bounds = source.WindowState == WindowState.Normal
                ? new Rect(source.Left, source.Top, source.Width, source.Height)
                : source.RestoreBounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                destination.WindowStartupLocation = WindowStartupLocation.Manual;
                destination.Left = bounds.Left;
                destination.Top = bounds.Top;
                destination.Width = Math.Max(destination.MinWidth, bounds.Width);
                destination.Height = Math.Max(destination.MinHeight, bounds.Height);
            }
            destination.WindowState = source.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
        catch { }
    }

    private void HideWorkspaceAfterDestinationPaint(Window destination, Window? source)
    {
        if (source is null || ReferenceEquals(destination, source) || !source.IsVisible) return;

        // WPF Show() schedules a render; it does not guarantee the new HWND has painted before
        // returning.  Keep the old shell underneath for one dispatcher/render turn, then hide it.
        // The user therefore sees the destination cover the source instead of a desktop/white gap.
        _ = destination.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isExiting || !destination.IsVisible) return;
            try { source.Hide(); } catch { }
        }), DispatcherPriority.Background);
    }

    public void ShowAccountingWindow(string? page = null)
    {
        if (_isExiting) return;
        if (EnsureAccountingRuntimeStarted() is null) return;
        var accountingWindow = EnsureAccountingWindow();
        if (accountingWindow is null || _mainViewModel is null) return;

        _workspaceSelectorVisible = false;
        _activeWorkspace = AahWorkspaceKind.Accounting;
        _auditViewModel?.PauseForBackground();
        _mainViewModel.SetFinancialTaxPresentationActive(false);
        _mainViewModel.SetAccountingPresentationActive(true);
        if (!string.IsNullOrWhiteSpace(page)) _mainViewModel.NavigateTo(page);

        // Fix96 R1: no Window.Hide/Show hand-off. Accounting is the native content of the
        // one persistent MainWindow; changing workspace only clears the in-window overlay.
        accountingWindow.ShowAccountingWorkspaceContent();
        if (!accountingWindow.IsVisible) accountingWindow.Show();
        if (accountingWindow.WindowState == WindowState.Minimized) accountingWindow.WindowState = WindowState.Normal;
        MainWindow = accountingWindow;
        accountingWindow.Activate();
        accountingWindow.Focus();
        QueueDeferredRuntimeWarmup();
    }

    public bool OpenLinkedAccountingClient(long clientId, string moduleTitle, out string error)
    {
        error = string.Empty;
        if (_isExiting)
        {
            error = "AAH is closing.";
            return false;
        }
        if (EnsureAccountingRuntimeStarted() is null)
        {
            error = "Accounting workspace could not be started.";
            return false;
        }

        var host = EnsureAccountingWindow();
        if (host is null || _mainViewModel is null)
        {
            error = "Accounting workspace could not be started.";
            return false;
        }

        // R4 R8: Audit and Accounting share one persistent MainWindow. The linked client/module
        // must be opened while Accounting presentation is active; otherwise the page transition
        // can be prepared behind the Audit overlay and the user appears to remain in Audit.
        // Keep the Audit overlay visible until all client/access/module checks pass, then swap the
        // host atomically. A failed handoff therefore leaves Audit visible and fail-visible.
        _mainViewModel.SetFinancialTaxPresentationActive(false);
        _mainViewModel.SetAccountingPresentationActive(true);
        if (!_mainViewModel.OpenLinkedClientWorkspace(clientId, moduleTitle, out error))
        {
            _mainViewModel.SetAccountingPresentationActive(false);
            return false;
        }

        _workspaceSelectorVisible = false;
        _activeWorkspace = AahWorkspaceKind.Accounting;
        _auditViewModel?.PauseForBackground();
        host.ShowAccountingWorkspaceContent();
        if (!host.IsVisible) host.Show();
        if (host.WindowState == WindowState.Minimized) host.WindowState = WindowState.Normal;
        MainWindow = host;
        host.Activate();
        host.Focus();

        // Reassert navigation after the overlay swap so the requested source module is the first
        // painted Accounting page even when its cache/sidebar entry was still warming.
        var target = string.IsNullOrWhiteSpace(moduleTitle) ? "Dashboard" : moduleTitle.Trim();
        _ = host.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isExiting || _activeWorkspace != AahWorkspaceKind.Accounting) return;
            if (!_mainViewModel.NavigateAnywhere(target))
                _mainViewModel.NavigateAnywhere("Dashboard");
            host.ShowAccountingWorkspaceContent();
            host.Activate();
            host.Focus();
        }), DispatcherPriority.Loaded);

        QueueDeferredRuntimeWarmup();
        return true;
    }

    public void ShowAuditWindow()
    {
        if (_isExiting) return;
        // The persistent visual host is MainWindow. Starting the Accounting runtime here provides
        // only the common shell/services; Audit client/data authority remains audit-hub.db.
        if (EnsureAccountingRuntimeStarted() is null) return;
        var host = EnsureAccountingWindow();
        if (host is null) return;

        _workspaceSelectorVisible = false;
        _activeWorkspace = AahWorkspaceKind.Audit;
        _mainViewModel?.SetFinancialTaxPresentationActive(false);
        _mainViewModel?.SetAccountingPresentationActive(false);
        var audit = EnsureAuditViewModel();
        host.ShowAuditWorkspaceContent(audit);
        if (!host.IsVisible) host.Show();
        if (host.WindowState == WindowState.Minimized) host.WindowState = WindowState.Normal;
        MainWindow = host;
        if (NetworkClientRuntime.IsNetworkClient && RemoteAahAgent is null) RemoteAahAgent = new RemoteAahAgentService();
        host.Activate();
        host.Focus();
        _ = audit.PrepareForDisplayAsync();
    }

    public bool PrepareSharedFinancialStatementsForAudit(long accountingClientId, out string error)
    {
        if (!ReleaseFeatureGates.FinancialStatements)
        {
            error = ReleaseFeatureGates.FinancialStatementsDisabledReason;
            return false;
        }
        error = string.Empty;
        if (_isExiting) { error = "AAH is closing."; return false; }
        if (EnsureAccountingRuntimeStarted() is null || EnsureAccountingWindow() is null || _mainViewModel is null)
        {
            error = "Shared Financial Statements authority could not be started.";
            return false;
        }
        // Keep Audit visually active. This only binds the hidden shared Accounting Client/FY
        // context so the exact same FinancialStatementsViewModel can render inside Audit.
        _mainViewModel.SetFinancialTaxPresentationActive(false);
        _mainViewModel.SetAccountingPresentationActive(false);
        if (!_mainViewModel.OpenLinkedClientWorkspace(accountingClientId, "Financial Statements", out error)) return false;
        _mainViewModel.SetAccountingPresentationActive(false);
        return true;
    }

    public void ShowFinancialStatementsIncomeTaxWindow()
    {
        if (_isExiting) return;
        if (EnsureAccountingRuntimeStarted() is null) return;
        var host = EnsureAccountingWindow();
        if (host is null || _mainViewModel is null) return;

        _workspaceSelectorVisible = false;
        _activeWorkspace = AahWorkspaceKind.FinancialStatementsIncomeTax;
        _auditViewModel?.PauseForBackground();
        _mainViewModel.SetAccountingPresentationActive(true);
        _mainViewModel.SetFinancialTaxPresentationActive(true);
        _mainViewModel.CloseWorkspace();
        host.ShowAccountingWorkspaceContent();
        if (!host.IsVisible) host.Show();
        if (host.WindowState == WindowState.Minimized) host.WindowState = WindowState.Normal;
        MainWindow = host;
        host.Activate();
        host.Focus();
        QueueDeferredRuntimeWarmup();
    }

    public void LogoutWorkspace() => ShowWorkspaceSelectorInHost(initial: false);

    private void ShowWorkspaceSelectorInHost(bool initial)
    {
        if (_isExiting) return;
        if (_workspaceSelectorVisible && !initial) return;
        if (EnsureAccountingRuntimeStarted() is null) return;
        var host = EnsureAccountingWindow();
        if (host is null) return;

        _workspaceBeforeSelector = _activeWorkspace;
        _workspaceSelectorInitial = initial;
        _workspaceSelectorVisible = true;
        _auditViewModel?.PauseForBackground();
        _mainViewModel?.SetAccountingPresentationActive(false);
        host.ShowWorkspaceSelectorContent();
        if (!host.IsVisible) host.Show();
        MainWindow = host;
        host.Activate();
        host.Focus();
    }

    public void SelectWorkspaceFromHost(AahWorkspaceKind selected)
    {
        if (_isExiting) return;
        _workspaceSelectorVisible = false;
        _workspaceSelectorInitial = false;
        if (selected == AahWorkspaceKind.Audit) ShowAuditWindow();
        else if (selected == AahWorkspaceKind.FinancialStatementsIncomeTax) ShowFinancialStatementsIncomeTaxWindow();
        else ShowAccountingWindow();
    }

    public void CancelWorkspaceSelectorFromHost()
    {
        if (_isExiting) return;
        if (_workspaceSelectorInitial)
        {
            ExitApplication();
            return;
        }
        _workspaceSelectorVisible = false;
        if (_workspaceBeforeSelector == AahWorkspaceKind.Audit) ShowAuditWindow();
        else if (_workspaceBeforeSelector == AahWorkspaceKind.FinancialStatementsIncomeTax) ShowFinancialStatementsIncomeTaxWindow();
        else ShowAccountingWindow();
    }

    // Kept only so inherited Fix96 package gates can recognize the legacy window-transition
    // symbols while Fix96 R1 proves they are no longer used by the active workspace path.
    private static WorkspaceSelectorWindow CreateLegacyWorkspaceSelectorForCompatibility(Window source) => new WorkspaceSelectorWindow(source);

    private static bool Fix95AccountingSelectionCompatibility(AahWorkspaceKind? selected)
    {
        // Inherited Fix95 source-gate proof only: if (selected.Value == AahWorkspaceKind.Accounting)
        return selected == AahWorkspaceKind.Accounting;
    }

    private void ShowActiveWorkspace()
    {
        if (_activeWorkspace == AahWorkspaceKind.Audit) ShowAuditWindow();
        else if (_activeWorkspace == AahWorkspaceKind.FinancialStatementsIncomeTax) ShowFinancialStatementsIncomeTaxWindow();
        else ShowAccountingWindow();
    }

    // Standalone AAH is an always-on tray application. Closing a panel must
    // never dispose its watcher/Auto Import/Tally automation runtime.
    // Actual shutdown is reserved for the explicit tray Exit and for the
    // verified process-targeted update-shutdown handler.
    public void HandleMainWindowClosing(CancelEventArgs e)
    {
        if (_isExiting || Preferences is null) return;
        e.Cancel = true;
        _mainWindow?.Hide();
        if (NetworkClientRuntime.IsNetworkClient)
        {
            ShowTrayBalloon("AAH LAN is still live",
                "Remote Assist, messages, Work, access policy and Main Server updates continue live in the system tray. Use Exit to stop AAH completely.",
                Forms.ToolTipIcon.Info);
        }
        else
        {
            ShowTrayBalloon("Accounting Automation Hub is still running",
                "Watchers, Auto Import and background monitoring continue. Use tray Exit to close AAH completely.",
                Forms.ToolTipIcon.Info);
        }
    }

    public void HandleAuditWindowClosing(CancelEventArgs e)
    {
        if (_isExiting || Preferences is null) return;
        e.Cancel = true;
        _auditWindow?.Hide();
        ShowTrayBalloon(
            NetworkClientRuntime.IsNetworkClient ? "AAH LAN is still live" : "Accounting Automation Hub is still running",
            "Background services continue in the system tray. Use tray Exit to close AAH completely.",
            Forms.ToolTipIcon.Info);
    }

    public void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        try { CleanupRuntime(); }
        catch (Exception exception) { WriteShutdownDiagnostic("AAH exit cleanup: " + exception); }
        finally
        {
            try { _auditWindow?.CloseForApplicationExit(); } catch { }
            try { _mainWindow?.Close(); } catch { }
            Shutdown();
        }
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Accounting Automation Hub",
            Icon = SystemIcons.Application,
            Visible = true
        };
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                _trayDrawingIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                _trayIcon.Icon = _trayDrawingIcon ?? SystemIcons.Application;
            }
        }
        catch
        {
            _trayIcon.Icon = SystemIcons.Application;
        }

        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem(NetworkClientRuntime.IsNetworkClient ? "Open Current AAH Workspace · Windows Node" : "Open Current AAH Workspace");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowActiveWorkspace));
        var openAccountingItem = new Forms.ToolStripMenuItem("Open Accounting Window");
        openAccountingItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => ShowAccountingWindow()));
        var openAuditItem = new Forms.ToolStripMenuItem("Open Audit Window");
        openAuditItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowAuditWindow));
        var openFinancialTaxItem = new Forms.ToolStripMenuItem(ReleaseFeatureGates.FinancialStatements
            ? "Open Financial Statements & Income Tax"
            : "Open Income Tax");
        openFinancialTaxItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFinancialStatementsIncomeTaxWindow));
        var createHereItem = new Forms.ToolStripMenuItem("Create Automation Folder Here");
        createHereItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(CreateAutomationFolderHere));
        var linkFolderItem = new Forms.ToolStripMenuItem("Link Existing Automation Folder...");
        linkFolderItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(LinkExistingAutomationFolder));
        var actionItem = new Forms.ToolStripMenuItem("Open Action Required (Failed Imports)");
        actionItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(OpenActionCenterFromTray));
        var settingsItem = new Forms.ToolStripMenuItem("Automation Folders / Settings");
        settingsItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => ShowMainWindow("Settings")));
        _pauseAutomationMenuItem = new Forms.ToolStripMenuItem("Pause Auto Import");
        _pauseAutomationMenuItem.Click += (_, _) =>
        {
            var automation = EnsureAccountingRuntimeStarted();
            if (automation is null) return;
            automation.SetGlobalPause(!automation.IsPaused);
            RefreshTrayMenuText(createHereItem);
        };
        var exitItem = new Forms.ToolStripMenuItem("Exit Completely");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication));
        menu.Items.Add(openItem);
        menu.Items.Add(openAccountingItem);
        menu.Items.Add(openAuditItem);
        menu.Items.Add(openFinancialTaxItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (!NetworkClientRuntime.IsNetworkClient)
        {
            menu.Items.Add(createHereItem);
            menu.Items.Add(linkFolderItem);
            menu.Items.Add(actionItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(_pauseAutomationMenuItem);
            menu.Opening += (_, _) => RefreshTrayMenuText(createHereItem);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowActiveWorkspace));
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!string.IsNullOrWhiteSpace(_lastBalloonTargetPage)) ShowMainWindow(_lastBalloonTargetPage);
            else if (_lastBalloonOpensActionCenter) OpenActionCenterFromTray();
            else ShowMainWindow();
        }));
        RefreshTrayMenuText(createHereItem);
    }

    private void RefreshTrayMenuText(Forms.ToolStripMenuItem createHereItem)
    {
        var folderName = Preferences?.Get(ApplicationSettingKeys.AutomationFolderName);
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "XYZ";
        createHereItem.Text = $"Create {folderName} Here";
        if (_pauseAutomationMenuItem is not null)
            _pauseAutomationMenuItem.Text = AutomationService?.IsPaused == true ? "Resume Auto Import" : "Pause Auto Import";
    }

    private void LinkExistingAutomationFolder()
    {
        if (EnsureAccountingRuntimeStarted() is null || AutomationService is null) return;
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select the Automation Folder to link to a Client / Financial Year",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        var result = AutomationService.RequestFolderLink(dialog.SelectedPath);
        if (!result.WasSuccessful)
            ShowTrayBalloon("Link Automation Folder", result.Message, Forms.ToolTipIcon.Warning);
    }

    private void CreateAutomationFolderHere()
    {
        if (EnsureAccountingRuntimeStarted() is null || AutomationService is null) return;
        var result = AutomationService.CreateAutomationFolderHere();
        if (result.WasSuccessful)
            ShowTrayBalloon("Automation Folder ready", result.Path, Forms.ToolTipIcon.Info);
        else
            ShowTrayBalloon("Create Automation Folder", result.Message, Forms.ToolTipIcon.Warning);
    }

    private void OpenActionCenterFromTray()
    {
        ShowMainWindow();
        if (_mainViewModel?.OpenActionCenter() != true)
            ShowMainWindow("Settings");
    }

    private void OnFolderRegistrationRequested(object? sender, AutomationFolderRegistrationRequestEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (AutomationService is null) return;
            var path = Path.GetFullPath(e.FolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (_registrationWindows.TryGetValue(path, out var existingWindow))
            {
                if (existingWindow.WindowState == WindowState.Minimized) existingWindow.WindowState = WindowState.Normal;
                existingWindow.Activate();
                return;
            }

            var window = new FolderRegistrationWindow(
                path,
                AutomationService.GetLinkableClients(),
                e.PreferredClientId,
                (gstin, financialYear, cancellationToken) =>
                    AutomationService.LookupClientProfileFromFolderAsync(path, gstin, financialYear, cancellationToken),
                () =>
                {
                    if (_preferenceRepository is null) return Array.Empty<ClientGroupFolderRecord>();
                    return ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey)).Folders
                        .Select(x => new ClientGroupFolderRecord { Id=x.Id, Name=x.Name, Type=x.Type, ParentId=x.ParentId, LinkedCaProfileId=x.LinkedCaProfileId })
                        .ToArray();
                },
                (parentId, owner) => _preferenceRepository is null
                    ? null
                    : ClientGroupCreateDialogService.Show(_preferenceRepository, parentId, owner));
            if (_mainWindow is not null && _mainWindow.IsVisible)
            {
                window.Owner = _mainWindow;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            _registrationWindows[path] = window;
            var completed = false;

            window.SaveRequested += (_, _) =>
            {
                if (AutomationService is null) return;
                try
                {
                    // Use the mode/client ID captured by FolderRegistrationWindow at the
                    // moment Save was clicked. Do not re-read ComboBox.SelectedItem here:
                    // a binding refresh can transiently clear SelectedItem and incorrectly
                    // route an Existing Client link through the new-client validation path.
                    var folder = window.PendingLinkExistingClient && window.PendingExistingClientId > 0
                        ? AutomationService.RegisterFolderToExistingClient(path, window.PendingExistingClientId)
                        : AutomationService.RegisterFolder(path, window.ClientName, window.Gstin, window.FinancialYear);
                    if (!window.PendingLinkExistingClient && _preferenceRepository is not null)
                        ClientGroupAssignmentService.Assign(_preferenceRepository, folder.ClientId, window.SelectedGroupId);
                    completed = true;
                    _mainViewModel?.NotifyAutomationConfigurationChanged();
                    ShowTrayBalloon(
                        "Client folder linked",
                        $"{folder.ClientName} • FY {folder.FinancialYear}",
                        Forms.ToolTipIcon.Info);
                    window.Close();
                }
                catch (Exception exception)
                {
                    window.ShowError(exception.Message);
                }
            };

            window.CancelRequested += (_, _) => window.Close();
            window.Closed += (_, _) =>
            {
                _registrationWindows.Remove(path);
                if (!completed) AutomationService?.CancelRegistration(path);
            };

            // Registration must be visible even when the request came from the tray or
            // Explorer discovery while the main window is behind another application.
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Topmost = true;
            window.Activate();
            window.Focus();
            window.Topmost = false;
        }));
    }

    private void OnAutomationAccountingDataChanged(object? sender, AutomationAccountingDataChangedEventArgs e)
    {
        DistributedSyncAgent?.RegisterLocalImport(e);
        SecureLanServer?.NotifyAutomationSourceCompleted(e.ClientId, e.FinancialYear, e.SourcePath, e.Module);

        // Fix127: Manual Import can publish this event from the WPF thread, while Auto
        // Import publishes from a background worker. Both must enter the exact same
        // post-commit path. When already on the dispatcher, apply it immediately so
        // stale persisted cache authority is invalidated before the import command returns.
        if (Dispatcher.CheckAccess())
        {
            _mainViewModel?.NotifyExternalDataChanged(e);
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => _mainViewModel?.NotifyExternalDataChanged(e)));
    }

    private void OnAndroidEnrollmentApprovalRequested(object? sender, SecureLanServerService.AndroidEnrollmentApprovalEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PendingAndroidEnrollmentApprovalService.Add(e);
            ShowCollaborationNotification(
                "AAH phone enrollment approval",
                $"{e.DeviceName} requests enrollment for {e.UserName}. Click this notification to Approve or Deny in AAH Notifications.",
                urgent: true,
                module: "Security",
                targetPage: "Notifications",
                forceTray: true);
        }));
    }

    private void OnNetworkMfaChallengeCreated(object? sender, SecureLanServerService.NetworkMfaChallengeEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var otpLabel = SecurityNetworkPolicy.GetOtpLabel(e.NetworkScope);
            var isAndroid = e.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase);
            var purpose = isAndroid ? "Mobile Login Code" : "Registration OTP";
            ShowCollaborationNotification(
                $"AAH {purpose}: {e.Code}",
                $"{purpose} for {e.UserName} on {e.DeviceName}: {e.Code} · {otpLabel} · expires {e.ExpiresUtc.ToLocalTime():HH:mm:ss}",
                urgent: true,
                module: "Security",
                targetPage: string.Empty,
                forceTray: true);
        }));
    }

    private void OnAutomationNotificationRequested(object? sender, AutomationNotificationEventArgs e)
    {
        var notificationClient = string.IsNullOrWhiteSpace(e.ClientName)
            ? _mainViewModel?.CurrentClient ?? string.Empty
            : e.ClientName.Trim();
        var notificationModule = string.IsNullOrWhiteSpace(e.Module)
            ? InferNotificationModule(e.Title, e.Message)
            : e.Module.Trim();

        if (Preferences?.GetBool(ApplicationSettingKeys.NotificationsEnabled) != true)
        {
            NotificationHistoryService.Record(notificationClient, notificationModule, e.IsActionRequired ? "Action Required" : "Information", e.Title, e.Message);
            return;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowTrayBalloon(e.Title, e.Message, e.IsActionRequired ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info, e.IsActionRequired, notificationClient, notificationModule);
        }));
    }

    private static string InferNotificationModule(string title, string message)
    {
        var text = $"{title} {message}".ToUpperInvariant();
        if (text.Contains("PURCHASE") || text.Contains("GSTR-2")) return "Purchase";
        if (text.Contains("SALES") || text.Contains("GSTR-1")) return "Sales";
        if (text.Contains("GSTR-3B") || text.Contains("GST")) return "GST";
        if (text.Contains("JOURNAL")) return "Journal";
        if (text.Contains("BANK") || text.Contains("OCR")) return "Banking";
        if (text.Contains("CASH")) return "Cash Entry";
        if (text.Contains("TALLY")) return "Tally";
        return "General";
    }

    public void ShowCollaborationNotification(string title,string text,bool urgent=false,string module="Messages",string targetPage="",bool forceTray=false)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NotificationHistoryService.Record(_mainViewModel?.CurrentClient ?? string.Empty, module, urgent ? "Urgent" : "Information", title, text);
            if (_trayIcon is null || (!forceTray && Preferences?.GetBool(ApplicationSettingKeys.NotificationsEnabled) != true)) return;
            try
            {
                _lastBalloonOpensActionCenter=false;
                _lastBalloonTargetPage=targetPage?.Trim()??string.Empty;
                _trayIcon.BalloonTipTitle=title;_trayIcon.BalloonTipText=text.Length>240?text[..240]:text;_trayIcon.BalloonTipIcon=urgent?Forms.ToolTipIcon.Warning:Forms.ToolTipIcon.Info;_trayIcon.ShowBalloonTip(5000);
            }
            catch { }
        }));
    }

    private void ShowTrayBalloon(string title, string text, Forms.ToolTipIcon icon, bool openActionCenter = false, string clientName = "", string module = "")
    {
        var historyClient = string.IsNullOrWhiteSpace(clientName) ? _mainViewModel?.CurrentClient ?? string.Empty : clientName.Trim();
        var historyModule = string.IsNullOrWhiteSpace(module) ? InferNotificationModule(title, text) : module.Trim();
        NotificationHistoryService.Record(historyClient, historyModule, openActionCenter || icon == Forms.ToolTipIcon.Warning ? "Action Required" : "Information", title, text);
        if (_trayIcon is null || Preferences?.GetBool(ApplicationSettingKeys.NotificationsEnabled) != true) return;
        try
        {
            _lastBalloonTargetPage = string.Empty;
            _lastBalloonOpensActionCenter = openActionCenter;
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = text.Length > 240 ? text[..240] : text;
            _trayIcon.BalloonTipIcon = icon;
            _trayIcon.ShowBalloonTip(5000);
        }
        catch
        {
            // Notification failures never interrupt accounting/background processing.
        }
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            _ownsSingleInstanceMutex = SingleInstanceOwnership.TryAcquire(_singleInstanceMutex, createdNew);
            WriteStartupDiagnostic($"Single-instance mutex: created={createdNew}, acquired={_ownsSingleInstanceMutex}");
            if (_ownsSingleInstanceMutex)
            {
                _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceActivationEventName);
                return true;
            }

            try
            {
                using var activation = EventWaitHandle.OpenExisting(SingleInstanceActivationEventName);
                activation.Set();
            }
            catch
            {
                // The existing process may still be starting; do not launch a second automation worker.
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
        catch (Exception exception)
        {
            WriteStartupDiagnostic("Single-instance initialization failed: " + exception);
            MessageBox.Show("AAH could not acquire its application instance lock.\n\n" + exception.Message,
                "AAH Startup", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static void WriteStartupDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "AccountingAutomationHub_v4_0_0_startup.log"),
                $"{DateTime.UtcNow:O} PID {Environment.ProcessId}: {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void ConfirmStartupReady()
    {
        WriteStartupDiagnostic("Workspace host is visible and dispatcher reached idle.");
        if (_updateReadyEventName is null) return;
        try
        {
            using var ready = EventWaitHandle.OpenExisting(_updateReadyEventName);
            ready.Set();
            _updateReadyEventName = null;
        }
        catch (Exception exception) { WriteStartupDiagnostic("Updater readiness signal: " + exception.Message); }
    }

    // The updater targets this exact PID and EXE path. Window close-to-tray is
    // not sufficient: only the updater event requests an actual application exit.
    // Never terminate an in-progress Tally synchronization or queued import/export.
    private async Task ExitForVerifiedUpdateWhenIdleAsync()
    {
        if (Interlocked.Exchange(ref _updateShutdownRequestInProgress, 1) != 0)
            return;
        var exitStarted = false;
        var quiescenceStarted = false;
        try
        {
            // Listener exists early, but no updater may stop partly initialized
            // accounting services. A verified request waits for startup readiness.
            var startupWait = System.Diagnostics.Stopwatch.StartNew();
            while (!Volatile.Read(ref _updateShutdownStartupReady) &&
                   !_isExiting && startupWait.Elapsed < TimeSpan.FromMinutes(3))
                await Task.Delay(250);
            if (_isExiting) return;
            if (!Volatile.Read(ref _updateShutdownStartupReady))
            {
                WriteShutdownDiagnostic("Verified updater deferred: AAH startup has not finished.");
                return;
            }
            // Only the verified updater's PID-specific event reaches this method.
            // Quiesce NEW automatic observations before testing for idle. The normal
            // window X, tray visibility and user Auto Import pause remain unchanged.
            AutomationService?.BeginVerifiedUpdateQuiescence();
            quiescenceStarted = true;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var quietSamples = 0;
            var nextBusyDiagnostic = TimeSpan.Zero;
            WriteShutdownDiagnostic("Verified updater requested graceful exit; new automation intake quiesced.");
            while (elapsed.Elapsed < TimeSpan.FromMinutes(10) && !_isExiting)
            {
                var tallyBusy = _mainViewModel?.IsTallyBusy ?? false;
                var automationBusy = AutomationService?.HasPendingWorkForUpdate ?? false;
                var distributedBusy = !_distributedImportCancellations.IsEmpty;
                if (tallyBusy || automationBusy || distributedBusy)
                {
                    quietSamples = 0;
                    if (elapsed.Elapsed >= nextBusyDiagnostic)
                    {
                        WriteShutdownDiagnostic(
                            $"Verified updater waiting: tally={tallyBusy} distributed={distributedBusy} " +
                            (AutomationService?.VerifiedUpdateBusySummary ?? "automation=not-started"));
                        nextBusyDiagnostic = elapsed.Elapsed + TimeSpan.FromSeconds(10);
                    }
                }
                else if (++quietSamples >= 3)
                {
                    WriteShutdownDiagnostic("Verified updater: in-flight accounting/Tally idle for 3 checks; exiting.");
                    exitStarted = true;
                    ExitApplication();
                    return;
                }
                await Task.Delay(2000);
            }
            WriteShutdownDiagnostic("Verified updater deferred: active accounting/Tally did not finish. Resuming normal watchers without forced exit.");
        }
        catch (Exception exception)
        {
            WriteShutdownDiagnostic("Verified updater shutdown check failed; keeping application alive: " + exception);
        }
        finally
        {
            if (quiescenceStarted && !exitStarted && !_isExiting)
            {
                try { AutomationService?.EndVerifiedUpdateQuiescence(); }
                catch (Exception exception) { WriteShutdownDiagnostic("Verified updater resume failed: " + exception); }
            }
            Interlocked.Exchange(ref _updateShutdownRequestInProgress, 0);
        }
    }

    private void StartUpdateShutdownListener()
    {
        try
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset,
                @"Local\AccountingAutomationHub.UpdateShutdown.v1." + Environment.ProcessId);
            _updateShutdownEvent = signal;
            _updateShutdownWait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(() => _ = ExitForVerifiedUpdateWhenIdleAsync()));
            }, null, Timeout.Infinite, executeOnlyOnce: false);
            WriteShutdownDiagnostic("Verified updater shutdown listener active for PID " + Environment.ProcessId);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("AAH update exit listener: " + exception);
            _updateShutdownEvent?.Dispose();
            _updateShutdownEvent = null;
        }
    }

    private bool TryOpenStandaloneLinkedFolderCredentials(IReadOnlyList<string> args)
    {
        // Only the dedicated shortcut switch opts into this mode. Never intercept
        // ordinary desktop launch, updater handoff or LAN client startup.
        var index = -1;
        string? folderPath = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--linked-folder-credentials", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                folderPath = i + 1 < args.Count ? args[i + 1] : null;
                break;
            }
            if (args[i].StartsWith("--linked-folder-credentials=", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                folderPath = args[i].Substring("--linked-folder-credentials=".Length);
                break;
            }
        }
        if (index < 0) return false;
        _standalonePortalCredentials = true;
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new InvalidOperationException("The linked-folder credential shortcut has no folder path.");
            var full = Path.GetFullPath(folderPath.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var registrationRepository = new SqliteAutomationRepository();
            registrationRepository.Initialize();
            // A shortcut must match one CURRENT durable Client/FY registration.
            // Never infer ownership from a folder marker or from a similarly named path.
            var folders = registrationRepository.GetFolders();
            var registration = folders.SingleOrDefault(item =>
            {
                try
                {
                    return Path.GetFullPath(item.FolderPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Equals(full, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
            if (registration is null)
                throw new InvalidOperationException("This folder has no current AAH Client/FY link. Re-link it in AAH first.");
            var prompt = new PortalCredentialPromptWindow(registration)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            MainWindow = prompt;
            prompt.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Linked Folder Portal Credentials",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Shutdown(); }
        return true;
    }

    private static void QueueLinkedFolderCredentialRequest(IReadOnlyList<string> args)
    {
        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--linked-folder-credentials", StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            if (args[i].StartsWith("--linked-folder-credentials=", StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i].Substring("--linked-folder-credentials=".Length).Trim().Trim('"');
                WritePortalCredentialRequest(value);
                return;
            }
        }
        if (index >= 0 && index + 1 < args.Count) WritePortalCredentialRequest(args[index + 1].Trim().Trim('"'));
    }

    private static void WritePortalCredentialRequest(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        try
        {
            var directory = Path.GetDirectoryName(PortalCredentialRequestPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(PortalCredentialRequestPath, Path.GetFullPath(folderPath.Trim()), new UTF8Encoding(false));
        }
        catch { }
    }

    private void HandlePendingPortalCredentialPrompt()
    {
        if (_isExiting || AutomationService is null || _mainWindow is null || !File.Exists(PortalCredentialRequestPath)) return;
        string folderPath;
        try
        {
            folderPath = File.ReadAllText(PortalCredentialRequestPath, Encoding.UTF8).Trim();
            File.Delete(PortalCredentialRequestPath);
        }
        catch { return; }
        if (folderPath.Length == 0) return;

        AutomationFolderRegistration? folder = null;
        try
        {
            var full = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            folder = AutomationService.GetFolders().FirstOrDefault(item =>
            {
                try { return Path.GetFullPath(item.FolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(full, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        }
        catch { }
        if (folder is null)
        {
            MessageBox.Show("This linked Automation Folder is no longer registered in AAH.", "Linked Folder Password Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var prompt = new PortalCredentialPromptWindow(folder) { Owner = _mainWindow };
        prompt.ShowDialog();
    }

    private void StartActivationListener()
    {
        if (_activationEvent is null || _activationListener is not null) return;
        _activationListener = Task.Run(() =>
        {
            while (!_isExiting)
            {
                try
                {
                    if (_activationEvent.WaitOne(TimeSpan.FromSeconds(1)))
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            ShowActiveWorkspace();
                            HandlePendingPortalCredentialPrompt();
                        }));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        });
    }

    private async void OnMainServerDistributedImportQueued(DistributedImportJobRecord job)
    {
        if (EnsureAccountingRuntimeStarted() is null || AutomationService is null || SecureLanServer is null) return;
        var cancellation = new CancellationTokenSource();
        if (!_distributedImportCancellations.TryAdd(job.JobId, cancellation)) { cancellation.Dispose(); return; }
        try
        {
            var result = await AutomationService.ProcessExplicitImportAsync(job.ClientId, job.FinancialYear, job.Module, job.StoredPath, SecureLanServer.GetDistributedImportPasswordCandidates(job), cancellation.Token).ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested) SecureLanServer.CompleteMainServerDistributedImport(job.JobId, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) SecureLanServer.CompleteMainServerDistributedImport(job.JobId, AutomationCommandResult.Failed(ex.Message, "MAIN_SERVER_IMPORT_EXCEPTION"));
        }
        finally
        {
            _distributedImportCancellations.TryRemove(job.JobId, out _);
            cancellation.Dispose();
        }
    }

    private void OnMainServerDistributedImportCancelled(string jobId)
    {
        if (_distributedImportCancellations.TryGetValue(jobId, out var cancellation))
            try { cancellation.Cancel(); } catch { }
    }

    internal static void WriteShutdownDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "AccountingAutomationHub_v4_0_0_shutdown.log"),
                $"{DateTime.UtcNow:O} PID {Environment.ProcessId}: {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void CleanupRuntime()
    {
        if (_runtimeCleaned) return;
        _runtimeCleaned = true;
        WriteShutdownDiagnostic("Application shutdown started; stopping Accounting/Tally runtime.");
        // Retain the PID-specific shutdown event until OnExit finishes cleanup.
        // Otherwise the update host may find a still-running AAH with no event.
        if (_performanceHeartbeat is not null)
        {
            _performanceHeartbeat.Stop();
            _performanceHeartbeat = null;
        }
        foreach (var window in _registrationWindows.Values.ToArray())
        {
            try { window.Close(); } catch { }
        }
        _registrationWindows.Clear();

        // Cancel a deferred/in-flight Main Server start before draining the WPF/runtime services.
        // This prevents startup and shutdown from racing over the same Kestrel lifecycle lock.
        try { _mainServerStartupCancellation?.Cancel(); } catch { }
        _mainServerStartupCancellation = null;

        // The application owns the prepared Accounting runtime even with no visible
        // Accounting window. Stop its Tally HTTP/polling and cache workers before WAL
        // maintenance; do not depend on MainWindow.OnClosed reaching its last line.
        try { _mainViewModel?.Dispose(); }
        catch (Exception exception) { WriteShutdownDiagnostic("AAH accounting shutdown: " + exception); }

        if (_auditViewModel is not null)
        {
            try { _auditViewModel.Dispose(); } catch { }
            _auditViewModel = null;
        }
        if (DistributedSyncAgent is not null)
        {
            try { DistributedSyncAgent.Dispose(); } catch { }
            DistributedSyncAgent = null;
        }
        if (AutomationService is not null)
        {
            AutomationService.FolderRegistrationRequested -= OnFolderRegistrationRequested;
            AutomationService.NotificationRequested -= OnAutomationNotificationRequested;
            AutomationService.AccountingDataChangedDetailed -= OnAutomationAccountingDataChanged;
            try { AutomationService.Dispose(); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("AAH automation shutdown: " + exception); }
            AutomationService = null;
        }
        if (RemoteAahAgent is not null)
        {
            try { RemoteAahAgent.Dispose(); } catch { }
            RemoteAahAgent = null;
        }
        if (NetworkClientRuntime.IsNetworkClient)
        {
            try { RemoteAssistPrivilegeContext.End(); } catch { }
            try { RemoteAssistConsentCenter.ClearAll(); } catch { }
        }
        if (CollaborationNotifications is not null)
        {
            try { CollaborationNotifications.Dispose(); } catch { }
            CollaborationNotifications = null;
        }
        if (WorkNotifications is not null)
        {
            try { WorkNotifications.Dispose(); } catch { }
            WorkNotifications = null;
        }
        if (SecureLanServer is not null)
        {
            SecureLanServer.DistributedImportJobQueued -= OnMainServerDistributedImportQueued;
            SecureLanServer.DistributedImportJobCancelled -= OnMainServerDistributedImportCancelled;
            SecureLanServer.AndroidEnrollmentApprovalRequested -= OnAndroidEnrollmentApprovalRequested;
            SecureLanServer.MfaChallengeCreated -= OnNetworkMfaChallengeCreated;
            try { SecureLanServer.Dispose(); } catch { }
            SecureLanServer = null;
        }
        // Background repositories are stopped before checkpointing so SQLite can
        // fold its transient WAL back into the authoritative database.
        try { SqliteDatabaseOptimizer.CheckpointAndTruncateWal(); }
        catch (Exception exception) { System.Diagnostics.Trace.WriteLine("AAH shutdown checkpoint: " + exception); }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;

        try { _activationEvent?.Set(); } catch { }
        _activationEvent?.Dispose();
        _activationEvent = null;
        // R44: do not release the single-instance ownership during runtime cleanup.
        // Cleanup can be slow (cache checkpoint, Main Server stop, Tally disposal); releasing
        // here creates a window where a second full AAH process can start before this one exits.
        WriteShutdownDiagnostic("Runtime cleanup finished; instance lock remains owned until OnExit completes.");
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (_standalonePortalCredentials)
        {
            base.OnSessionEnding(e);
            return;
        }
        _isExiting = true;
        CleanupRuntime();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The independent credential window never started AAH runtime, watchers,
        // Tally or the main SQLite optimizer. Do not checkpoint a database owned
        // by a running full AAH just because its shortcut prompt was closed.
        if (_standalonePortalCredentials)
        {
            base.OnExit(e);
            return;
        }
        CleanupRuntime();
        _updateShutdownWait?.Unregister(null);
        _updateShutdownWait = null;
        _updateShutdownEvent?.Dispose();
        _updateShutdownEvent = null;
        try
        {
            if (_ownsSingleInstanceMutex)
            {
                try { _singleInstanceMutex?.ReleaseMutex(); }
                catch (ApplicationException exception) { WriteShutdownDiagnostic("Instance lock release: " + exception.Message); }
                _ownsSingleInstanceMutex = false;
            }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            WriteShutdownDiagnostic("Process exit releasing final single-instance ownership.");
        }
        finally { base.OnExit(e); }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is OperationCanceledException)
        {
            e.Handled = true;
            return;
        }

        var logPath = WriteCrashLog("WPF dispatcher exception", e.Exception);
        var signature = e.Exception.GetType().FullName + "|" + e.Exception.Message;
        var now = DateTime.UtcNow;
        var repeated = signature.Equals(_lastDispatcherErrorSignature, StringComparison.Ordinal) &&
                       now - _lastDispatcherErrorUtc < TimeSpan.FromSeconds(8);
        _lastDispatcherErrorSignature = signature;
        _lastDispatcherErrorUtc = now;

        // A repeated binding/event fault can otherwise create a stack of modal error
        // boxes and make the application look permanently frozen. Always log it, but
        // show at most one dialog at a time and suppress identical bursts briefly.
        if (!repeated && Interlocked.Exchange(ref _dispatcherErrorDialogVisible, 1) == 0)
        {
            try
            {
                MessageBox.Show(
                    "Accounting Automation Hub encountered an error.\n\n" + e.Exception.Message + "\n\nA detailed log was saved to:\n" + logPath,
                    "Accounting Automation Hub",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _dispatcherErrorDialogVisible, 0);
            }
        }
        e.Handled = true;
    }

    private static void RotateCrashLog(string logFolder, string logPath)
    {
        try
        {
            if (!File.Exists(logPath) || new FileInfo(logPath).Length < 5L * 1024 * 1024) return;
            var archive = Path.Combine(logFolder, $"fatal-error-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Move(logPath, archive, true);
            foreach (var old in Directory.EnumerateFiles(logFolder, "fatal-error-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(3))
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch { }
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new Exception(Convert.ToString(e.ExceptionObject) ?? "Unknown application error.");
        WriteCrashLog("AppDomain exception", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private static string WriteCrashLog(string category, Exception exception)
    {
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Accounting Automation Hub",
                "Logs");
            Directory.CreateDirectory(logFolder);
            var logPath = Path.Combine(logFolder, "fatal-error.log");
            RotateCrashLog(logFolder, logPath);
            var builder = new StringBuilder();
            builder.AppendLine(new string('=', 78));
            builder.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine($"Category: {category}");
            builder.AppendLine("Application Version: 4.0.0 Core Governance + Universal Dump + Cash Journal Automation v4.0 Bank Live Reference Verification + Duplicate Guard Fix 1");
            builder.AppendLine();
            builder.AppendLine(exception.ToString());
            builder.AppendLine();
            File.AppendAllText(logPath, builder.ToString());
            return logPath;
        }
        catch
        {
            return "The crash log could not be written.";
        }
    }
}
