using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Desktop.Views;

namespace AccountingAutomationHub.Desktop;

public partial class MainWindow : Window
{
    static MainWindow()
    {
        // Fix137 R46 R9: Ctrl+F/Ctrl+G are application-level workspace shortcuts, not
        // Dashboard-only gestures. Register once for every AAH Window so module-owned
        // dialogs and secondary windows participate too.
        EventManager.RegisterClassHandler(
            typeof(Window),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(GlobalWindow_PreviewKeyDown),
            true);
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private HwndSource? _windowSource;
    private readonly ApplicationPreferenceService _settings;
    private readonly MainWindowViewModel _viewModel;
    private UniversalCalculatorWindow? _calculatorWindow;
    private int _calculatorSelectionUpdateVersion;
    private readonly ConditionalWeakTable<DataGrid, CalculatorSelectionOrderState> _calculatorSelectionOrders = new();
    private System.Windows.Threading.DispatcherTimer? _networkLiveTimer;
    private System.Windows.Threading.DispatcherTimer? _networkSessionTimer;
    private int _networkLiveTickRunning;
    private EventHandler? _networkApplicationActivatedHandler;
    private bool _networkForceUpdate;
    private AuditWorkspaceView? _auditWorkspaceView;
    private WorkspaceSelectorView? _workspaceSelectorView;

    private void NavigationToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.DataContext is not NavigationItem item || !item.ShowToggle) return;
        e.Handled = true;
        item.ToggleValue = !item.ToggleValue;
    }

    public MainWindow(
        MainWindowViewModel viewModel,
        ApplicationPreferenceService settings)
    {
        _settings = settings;
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Fix12: DataGrid.SelectedCellsChanged itself is not routed, so listen to the
        // bubbling DataGridCell/DataGridRow selection events at the shell. This covers
        // every current/future module grid without wiring each view separately. Updates
        // are coalesced until WPF has finished changing the complete selection.
        AddHandler(DataGridCell.SelectedEvent, new RoutedEventHandler(OnCalculatorGridSelectionChanged), true);
        AddHandler(DataGridCell.UnselectedEvent, new RoutedEventHandler(OnCalculatorGridSelectionChanged), true);
        AddHandler(DataGridRow.SelectedEvent, new RoutedEventHandler(OnCalculatorGridSelectionChanged), true);
        AddHandler(DataGridRow.UnselectedEvent, new RoutedEventHandler(OnCalculatorGridSelectionChanged), true);
        if (NetworkClientRuntime.IsNetworkClient)
        {
            MinWidth = 900;
            MinHeight = 560;
            TallyTopPanel.Visibility = Visibility.Collapsed;
            TitleBrandColumn.Width = new GridLength(205);
            TopToolColumn.Width = GridLength.Auto;
            NetworkModeBanner.Visibility = Visibility.Visible;
            AutomaticGstSyncBadge.Visibility = Visibility.Collapsed;
            UndoButton.Visibility = Visibility.Collapsed;
            RemoteAssistConsentCenter.Changed += OnRemoteAssistConsentChanged;
            RemoteAssistLocalStreamState.Changed += OnRemoteAssistConsentChanged;
            RemoteAssistPrivilegeContext.Changed += OnRemoteAssistConsentChanged;
            UpdateNetworkChrome();
            Loaded += async (_, _) =>
            {
                _ = Dispatcher.BeginInvoke(new Action(ApplyNetworkGuardToOpenWindows));
                await RunNetworkLiveTickAsync();
                _networkLiveTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _networkLiveTimer.Tick += async (_, _) => await RunNetworkLiveTickAsync();
                _networkLiveTimer.Start();
                _networkSessionTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
                _networkSessionTimer.Tick += async (_, _) => await RevalidateNetworkSessionAsync(closeOnRevoked: true, refreshSnapshotOnPermissionChange: false);
                _networkSessionTimer.Start();
            };
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.CurrentPage))
                    _ = Dispatcher.BeginInvoke(new Action(ApplyNetworkGuardToOpenWindows), System.Windows.Threading.DispatcherPriority.ContextIdle);
            };
            _networkApplicationActivatedHandler = (_, _) => { _ = Dispatcher.BeginInvoke(new Action(ApplyNetworkGuardToOpenWindows), System.Windows.Threading.DispatcherPriority.ContextIdle); _ = RunNetworkLiveTickAsync(); if(System.Windows.Application.Current is App app&&app.RemoteAahAgent is not null)_ = app.RemoteAahAgent.PollNowAsync(); };
            System.Windows.Application.Current.Activated += _networkApplicationActivatedHandler;
        }
        Loaded += (_, _) =>
        {
            RestoreWindowPlacement();
            if (NetworkClientRuntime.IsNetworkClient) FitWindowToWorkingArea();
        };
    }

    private MainWindowViewModel? ViewModel =>
        DataContext as MainWindowViewModel;

    private void AuditWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app) app.LogoutWorkspace();
    }

    private void WorkspaceLogout_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app) app.LogoutWorkspace();
    }

    // Fix96 R1: one persistent WPF HWND hosts Accounting, Audit and the workspace selector.
    // Workspace changes swap only this in-window overlay; the MainWindow itself is never hidden/reopened.
    internal void ShowAccountingWorkspaceContent()
    {
        WorkspaceOverlayHost.Content = null;
        WorkspaceOverlayHost.Visibility = Visibility.Collapsed;
        _ = Dispatcher.BeginInvoke(new Action(() => PageContent.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    internal void ShowAuditWorkspaceContent(AuditWorkspaceViewModel viewModel)
    {
        if (_auditWorkspaceView is null || !ReferenceEquals(_auditWorkspaceView.DataContext, viewModel))
            _auditWorkspaceView = new AuditWorkspaceView(viewModel);
        WorkspaceOverlayHost.Content = _auditWorkspaceView;
        WorkspaceOverlayHost.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(new Action(() => _auditWorkspaceView.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    internal void ShowWorkspaceSelectorContent()
    {
        _workspaceSelectorView ??= new WorkspaceSelectorView();
        WorkspaceOverlayHost.Content = _workspaceSelectorView;
        WorkspaceOverlayHost.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(new Action(() => _workspaceSelectorView.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    internal bool IsAuditWorkspaceVisible => WorkspaceOverlayHost.Content is AuditWorkspaceView && WorkspaceOverlayHost.Visibility == Visibility.Visible;
    internal bool IsWorkspaceSelectorVisible => WorkspaceOverlayHost.Content is WorkspaceSelectorView && WorkspaceOverlayHost.Visibility == Visibility.Visible;


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo)
        {
            ApplyMonitorWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ApplyMonitorWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = monitorInfo.WorkArea;
        var monitorArea = monitorInfo.MonitorArea;
        minMax.MaxPosition.X = Math.Abs(work.Left - monitorArea.Left);
        minMax.MaxPosition.Y = Math.Abs(work.Top - monitorArea.Top);
        minMax.MaxSize.X = Math.Abs(work.Right - work.Left);
        minMax.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
        minMax.MaxTrackSize = minMax.MaxSize;
        Marshal.StructureToPtr(minMax, lParam, false);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPlacement();
        if (System.Windows.Application.Current is App app)
            app.HandleMainWindowClosing(e);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _auditWorkspaceView?.CloseOwnedUtilityWindows(); } catch { }
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
        _networkLiveTimer?.Stop();
        _networkLiveTimer = null;
        _networkSessionTimer?.Stop();
        _networkSessionTimer = null;
        if (NetworkClientRuntime.IsNetworkClient){RemoteAssistConsentCenter.Changed -= OnRemoteAssistConsentChanged;RemoteAssistLocalStreamState.Changed -= OnRemoteAssistConsentChanged;RemoteAssistPrivilegeContext.Changed -= OnRemoteAssistConsentChanged;}
        if (_networkApplicationActivatedHandler is not null)
        {
            System.Windows.Application.Current.Activated -= _networkApplicationActivatedHandler;
            _networkApplicationActivatedHandler = null;
        }
        ViewModel?.Dispose();
        base.OnClosed(e);
    }

    private void NetworkRefresh_Click(object sender, RoutedEventArgs e) => RefreshCurrentPage();

    private async void NetworkUpdate_Click(object sender, RoutedEventArgs e)
    {
        await InstallNetworkUpdateAsync(_networkForceUpdate);
    }

    private static void ApplyNetworkGuardToOpenWindows()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        foreach (Window window in System.Windows.Application.Current.Windows)
            NetworkReadOnlyUiGuard.Apply(window);
    }

    private void UpdateNetworkChrome()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        var display = string.IsNullOrWhiteSpace(NetworkClientRuntime.DisplayName)
            ? NetworkClientRuntime.UserName
            : NetworkClientRuntime.DisplayName;
        NetworkIdentityText.Text = NetworkClientRuntime.AccountingAccessSuspended
            ? $"LAN · {display} · {NetworkClientRuntime.RoleName} · ACCESS SUSPENDED"
            : NetworkClientRuntime.DeviceHealthState.Equals(AccountingAutomationHub.Domain.Governance.DeviceHealthStates.Healthy, StringComparison.OrdinalIgnoreCase)
                ? $"LAN · {display} · {NetworkClientRuntime.RoleName}"
                : $"LAN · {display} · {NetworkClientRuntime.RoleName} · {NetworkClientRuntime.DeviceHealthState}";
        NetworkUploadButton.Visibility = NetworkClientRuntime.HasAnyPermission(
            AccountingAutomationHub.Domain.Governance.GovernancePermissions.UploadFiles,
            AccountingAutomationHub.Domain.Governance.GovernancePermissions.Import)
            ? Visibility.Visible
            : Visibility.Collapsed;
        Title = $"Accounting Automation Hub · LAN · {NetworkClientRuntime.RoleName}";
        RefreshRemoteAssistBanner();
    }

    private void OnRemoteAssistConsentChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(RefreshRemoteAssistBanner));
            return;
        }
        RefreshRemoteAssistBanner();
    }

    public void RefreshRemoteAssistBanner()
    {
        if(!NetworkClientRuntime.IsNetworkClient)
        {
            Title="Accounting Automation Hub";
            RemoteAssistText.Visibility=Visibility.Collapsed;RemoteAssistReviewButton.Visibility=Visibility.Collapsed;RemoteAssistActiveBar.Visibility=Visibility.Collapsed;return;
        }
        var pending=RemoteAssistConsentCenter.Pending;
        var active=RemoteAssistPrivilegeContext.IsActive;
        var text=RemoteAssistPrivilegeContext.Describe();
        RemoteAssistText.Text=active?"REMOTE ASSIST ACTIVE":pending is not null?"REMOTE ASSIST REQUEST":"";
        RemoteAssistText.Visibility=active||pending is not null?Visibility.Visible:Visibility.Collapsed;
        RemoteAssistReviewButton.Visibility=pending is null?Visibility.Collapsed:Visibility.Visible;
        RemoteAssistActiveBar.Visibility=active||pending is not null?Visibility.Visible:Visibility.Collapsed;
        if(active)
        {
            Title="Accounting Automation Hub — REMOTE ASSIST ACTIVE";
            RemoteAssistActiveBar.Background=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#B42318")!;
            var control=RemoteAssistLocalStreamState.AllowAdministratorControl?"CONTROL ALLOWED":"VIEW ONLY";
            var share=RemoteAssistLocalStreamState.ShareMyAah?"AAH WINDOW SHARED":"SHARING PAUSED";
            RemoteAssistActiveBarTitle.Text="REMOTE ASSIST ACTIVE — MAIN SERVER ADMINISTRATOR CONNECTED";
            RemoteAssistActiveBarText.Text=$"Helper: {RemoteAssistPrivilegeContext.HelperUser} · {share} · {control} · {NetworkClientRuntime.RemoteAssistTransportLabel} · Ends {RemoteAssistPrivilegeContext.ExpiresUtc.ToLocalTime():HH:mm}. Only the AAH window is shared. Use End Access Now at any time.";
        }
        else if(pending is not null)
        {
            Title="Accounting Automation Hub — Remote Assist Request";
            RemoteAssistActiveBar.Background=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#9A6700")!;
            RemoteAssistActiveBarTitle.Text="REMOTE ASSIST REQUEST WAITING";
            RemoteAssistActiveBarText.Text=$"{pending.HelperUser} requests {pending.Scope} support. Review the request to Accept or Decline. Expires {pending.ExpiresUtc.ToLocalTime():HH:mm}.";
        }
        else
        {
            Title="Accounting Automation Hub";
        }
    }

    private void RemoteAssistReview_Click(object sender, RoutedEventArgs e) => ViewModel?.NavigateTo("Remote Assist");
    private void RemoteAssistOpen_Click(object sender, RoutedEventArgs e) => ViewModel?.NavigateTo("Remote Assist");

    private async void RemoteAssistEndNow_Click(object sender, RoutedEventArgs e)
    {
        var sessionId=RemoteAssistPrivilegeContext.SessionId;
        if(string.IsNullOrWhiteSpace(sessionId)){ViewModel?.NavigateTo("Remote Assist");return;}
        try
        {
            using var api=CreateNetworkApi();
            api.Server=NetworkClientRuntime.ResolveServerEndpoint();
            await api.EndRemoteAssistByUserAsync(sessionId);
            RemoteAssistPrivilegeContext.End();
            RemoteAssistConsentCenter.EndSession(sessionId);
            RemoteAssistLocalStreamState.End();
            NotificationHistoryService.Record(string.Empty,"Remote Assist","Ended","Remote Assist ended by AAH user","The AAH user ended Remote Assist from the persistent active-session banner.");
        }
        catch(Exception exception)
        {
            if(System.Windows.Application.Current is App app)app.ShowCollaborationNotification("Remote Assist","Could not end Remote Assist: "+exception.Message,true,"Remote Assist");
        }
        RefreshRemoteAssistBanner();
    }

    private async Task<bool> RevalidateNetworkSessionAsync(bool closeOnRevoked, bool refreshSnapshotOnPermissionChange)
    {
        if (!NetworkClientRuntime.IsNetworkClient || string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken)) return false;
        var oldFingerprint = NetworkClientRuntime.UserName + "|" + NetworkClientRuntime.DisplayName + "|" + NetworkClientRuntime.RoleName + "|" + string.Join("|", NetworkClientRuntime.Permissions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        try
        {
            using var api = CreateNetworkApi();
            var identity = await api.RestoreSessionAsync(NetworkClientRuntime.SessionToken);
            NetworkClientRuntime.ApplyIdentity(identity.UserId, identity.DisplayName, identity.RoleName, identity.ExpiresUtc, identity.Permissions);
            NetworkClientRuntime.SessionToken = identity.SessionToken;
            NetworkClientCredentialStore.SaveSession(new NetworkSavedSession(
                NetworkClientRuntime.ResolveServerEndpoint(),
                NetworkClientRuntime.DeviceId,
                NetworkClientRuntime.UserName,
                NetworkClientRuntime.DisplayName,
                NetworkClientRuntime.RoleName,
                identity.SessionToken,
                identity.ExpiresUtc,
                identity.Permissions));
            var newFingerprint = NetworkClientRuntime.UserName + "|" + NetworkClientRuntime.DisplayName + "|" + NetworkClientRuntime.RoleName + "|" + string.Join("|", NetworkClientRuntime.Permissions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            var changed = !oldFingerprint.Equals(newFingerprint, StringComparison.OrdinalIgnoreCase);
            UpdateNetworkChrome();
            if (changed)
            {
                if (refreshSnapshotOnPermissionChange)
                {
                    api.SessionToken = NetworkClientRuntime.SessionToken;
                    await api.DownloadReadOnlySnapshotAsync();
                    ViewModel?.ApplyNetworkLiveSnapshotRefresh();
                }
                else
                {
                    ViewModel?.ApplyNetworkIdentityRefresh();
                }
                _ = Dispatcher.BeginInvoke(new Action(ApplyNetworkGuardToOpenWindows), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            return true;
        }
        catch (NetworkApiException exception) when (exception.StatusCode is 401 or 403)
        {
            RemoteAssistPrivilegeContext.End();
            RemoteAssistConsentCenter.ClearAll();
            NetworkClientCredentialStore.DeleteSession();
            NetworkClientRuntime.ClearSession();
            if (closeOnRevoked)
            {
                MessageBox.Show(this,
                    "The current LAN login session ended. Reopen AAH; a registered PC signs in without OTP. Registration OTP is required only after 30 days of inactivity or an Administrator registration reset.",
                    "LAN Session Ended", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            return false;
        }
        catch
        {
            // A temporary network/server failure does not erase an otherwise unexpired saved registered-device
            // session. The user can continue viewing the already downloaded read-only snapshot and retry.
            return true;
        }
    }

    private async Task RunNetworkLiveTickAsync()
    {
        if(!NetworkClientRuntime.IsNetworkClient||string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken))return;
        if(Interlocked.Exchange(ref _networkLiveTickRunning,1)==1)return;
        try
        {
            await CheckNetworkUpdateAsync(closeOnForcedFailure:true);
        }
        finally{Interlocked.Exchange(ref _networkLiveTickRunning,0);}
    }

    private async Task CheckNetworkUpdateAsync(bool closeOnForcedFailure)
    {
        if (!NetworkClientRuntime.IsNetworkClient || string.IsNullOrWhiteSpace(NetworkClientRuntime.DeviceId)) return;
        try
        {
            using var api = CreateNetworkApi();
            var status = await api.GetUpdateStatusAsync(_viewModel.CurrentClientId,_viewModel.CurrentFinancialYear,_viewModel.CurrentPage?.Title);
            _networkForceUpdate = status.ForceUpdate;

            var hadPolicyRevision = NetworkClientRuntime.AuthorizationRevision > 0 || NetworkClientRuntime.ClientAccessRevision > 0 ||
                                    NetworkClientRuntime.DevicePolicyRevision > 0 || NetworkClientRuntime.SecurityPolicyRevision > 0;
            var authorizationChanged = hadPolicyRevision && status.AuthorizationRevision != NetworkClientRuntime.AuthorizationRevision;
            var clientAccessChanged = hadPolicyRevision && status.ClientAccessRevision != NetworkClientRuntime.ClientAccessRevision;
            var devicePolicyChanged = hadPolicyRevision && status.DevicePolicyRevision != NetworkClientRuntime.DevicePolicyRevision;
            var securityPolicyChanged = hadPolicyRevision && status.SecurityPolicyRevision != NetworkClientRuntime.SecurityPolicyRevision;
            var hadAccountingRevision = NetworkClientRuntime.AccountingDataRevision > 0;
            var accountingDataChanged = hadAccountingRevision && status.AccountingDataRevision != NetworkClientRuntime.AccountingDataRevision;

            NetworkClientRuntime.AuthorizationRevision = status.AuthorizationRevision;
            NetworkClientRuntime.DevicePolicyRevision = status.DevicePolicyRevision;
            NetworkClientRuntime.ClientAccessRevision = status.ClientAccessRevision;
            NetworkClientRuntime.SecurityPolicyRevision = status.SecurityPolicyRevision;
            NetworkClientRuntime.AccountingDataRevision = status.AccountingDataRevision;
            NetworkClientRuntime.DeviceHealthState = status.HealthState;
            NetworkClientRuntime.DeviceRiskScore = status.RiskScore;

            var suspended = status.Quarantined || status.EmergencyLockdown;
            var suspensionReason = status.EmergencyLockdown
                ? $"Emergency Lockdown: {(string.IsNullOrWhiteSpace(status.LockdownReason) ? "Main Server administrator policy" : status.LockdownReason)}"
                : status.Quarantined
                    ? $"Device quarantined: {(string.IsNullOrWhiteSpace(status.QuarantineReason) ? "Main Server device posture" : status.QuarantineReason)}"
                    : string.Empty;
            var wasSuspended = NetworkClientRuntime.AccountingAccessSuspended;
            NetworkClientRuntime.AccountingAccessSuspended = suspended;
            NetworkClientRuntime.AccountingAccessSuspensionReason = suspensionReason;
            ViewModel?.ApplyNetworkLiveStatusRefresh();

            if (suspended)
            {
                if (!wasSuspended || devicePolicyChanged || securityPolicyChanged)
                {
                    ViewModel?.ApplyNetworkAccessSuspension(suspensionReason);
                    CloseNetworkAccountingChildWindows();
                    NotificationHistoryService.Record(string.Empty, "Security", "AccessSuspended", "AAH accounting access suspended", suspensionReason);
                }
            }
            else
            {
                if (wasSuspended) ViewModel?.ClearNetworkAccessSuspension();
                if (authorizationChanged)
                    await RevalidateNetworkSessionAsync(closeOnRevoked: true, refreshSnapshotOnPermissionChange: false);
                if (authorizationChanged || clientAccessChanged || accountingDataChanged)
                {
                    api.SessionToken = NetworkClientRuntime.SessionToken;
                    await api.DownloadReadOnlySnapshotAsync();
                    ViewModel?.ApplyNetworkLiveSnapshotRefresh();
                    if(authorizationChanged||clientAccessChanged)
                        NotificationHistoryService.Record(string.Empty, "Security", "PolicyRefresh", "Main Server access policy refreshed", $"Authorization revision {status.AuthorizationRevision}; Client access revision {status.ClientAccessRevision}.");
                }
            }

            NetworkUpdateButton.Visibility = status.UpdateRequired ? Visibility.Visible : Visibility.Collapsed;
            NetworkUpdateButton.Content = status.ForceUpdate ? "Required Update" : "Update from Main Server";
            NetworkUpdateButton.ToolTip = status.UpdateRequired ? $"Installed {status.CurrentVersion}; Main Server {status.ApprovedVersion}." : null;
            UpdateNetworkChrome();
            if (status.ForceUpdate)
                await InstallNetworkUpdateAsync(true);
        }
        catch (NetworkApiException exception) when (exception.RequiresReEnrollment)
        {
            RemoteAssistPrivilegeContext.End();
            RemoteAssistConsentCenter.ClearAll();
            NetworkClientCredentialStore.DeleteDevice();
            NetworkClientCredentialStore.DeleteSession();
            NetworkClientRuntime.ClearSession();
            NetworkClientRuntime.DeviceId=string.Empty;
            NetworkClientRuntime.DeviceToken=string.Empty;
            NotificationHistoryService.Record(string.Empty, "Security", "DeviceRegistrationRemoved", "LAN device registration removed", "Main Server no longer accepts this PC registration; fresh enrollment is required.");
            MessageBox.Show(this,
                "This PC registration was removed or rejected on the Main Server. The stale local device trust has been cleared. Reopen AAH, enter a new one-use enrollment code, enroll this PC again, then complete one Registration OTP.",
                "AAH LAN Re-enrollment Required", MessageBoxButton.OK, MessageBoxImage.Information);
            if (System.Windows.Application.Current is App app) app.ExitApplication(); else System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            if (_networkForceUpdate && closeOnForcedFailure)
            {
                NotificationHistoryService.Record(string.Empty, "Updates", "RequiredUpdateFailed", "Required LAN update failed", exception.Message);
                MessageBox.Show(this, $"This LAN application is blocked until the required Main Server update succeeds.\n\n{exception.Message}", "AAH LAN Update Required", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }
    }

    private void CloseNetworkAccountingChildWindows()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            if (ReferenceEquals(window, this)) continue;
            try { window.Close(); } catch { }
        }
    }

    private async Task InstallNetworkUpdateAsync(bool forced)
    {
        try
        {
            using var api = CreateNetworkApi();
            await NetworkClientSelfUpdateService.DownloadVerifyAndScheduleAsync(api, this, forced);
            if (System.Windows.Application.Current is App app) app.ExitApplication(); else System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"The Main Server update could not be installed.\n\n{exception.Message}", forced ? "Required LAN Update Failed" : "LAN Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            if (forced) Close();
        }
    }

    private static FullUiNetworkClientApi CreateNetworkApi() => new()
    {
        Server = NetworkClientRuntime.ResolveServerEndpoint(),
        DeviceId = NetworkClientRuntime.DeviceId,
        DeviceToken = NetworkClientRuntime.DeviceToken,
        SessionToken = NetworkClientRuntime.SessionToken
    };

    private async void NetworkUpload_Click(object sender, RoutedEventArgs e)
    {
        if (!NetworkClientRuntime.IsNetworkClient || ViewModel is null || ViewModel.CurrentClientId <= 0)
        {
            MessageBox.Show("Open an allowed client first, then upload the source file.", "LAN Upload", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Upload source file to AAH Main Server", Filter = "Accounting files|*.xlsx;*.xlsm;*.xls;*.csv;*.txt;*.pdf;*.json;*.zip;*.jpg;*.jpeg;*.png;*.webp;*.ofx;*.qbo;*.qif;*.mt940;*.xml|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var api = new FullUiNetworkClientApi { Server=NetworkClientRuntime.ResolveServerEndpoint(),DeviceId=NetworkClientRuntime.DeviceId,DeviceToken=NetworkClientRuntime.DeviceToken,SessionToken=NetworkClientRuntime.SessionToken };
            await api.UploadAsync(ViewModel.CurrentClientId, dialog.FileName);
            MessageBox.Show("The file was sent to the Main Server for the current client. The server keeps its retained copy before normal processing.", "LAN Upload", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "LAN Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoCurrentEdit();

    private void Calculator_Click(object sender, RoutedEventArgs e) => OpenCalculator();

    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.NavigateTo("Team") == true) return;
        MessageBox.Show(this, "Team & Chat is not available for the current AAH permissions.", "Team & Chat", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    internal void OpenSharedCalculator() => OpenCalculator();

    private void OpenCalculator()
    {
        var seed = TryGetCalculatorSeed();
        if (_calculatorWindow is { IsLoaded: true })
        {
            if (!string.IsNullOrWhiteSpace(seed.Expression))
                _calculatorWindow.SetSeed(seed.Expression, seed.SelectedValues);
            if (_calculatorWindow.WindowState == WindowState.Minimized) _calculatorWindow.WindowState = WindowState.Normal;
            _calculatorWindow.Activate();
            return;
        }
        _calculatorWindow = new UniversalCalculatorWindow(seed.Expression, seed.SelectedValues) { Owner = this };
        _calculatorWindow.Closed += (_, _) =>
        {
            Interlocked.Increment(ref _calculatorSelectionUpdateVersion);
            _calculatorWindow = null;
        };
        _calculatorWindow.Show();
    }

    private void OnCalculatorGridSelectionChanged(object sender, RoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject ?? e.Source as DependencyObject;
        var grid = FindVisualParent<DataGrid>(source);
        if (grid is null) return;

        // Fix13: record selection chronology even before Calculator is opened. WPF's
        // SelectedItems/SelectedCells collections are view ordered, not click ordered,
        // which made subtraction/division use the wrong operand order. A deselected item
        // is removed; selecting it again appends it at the end, matching user intent.
        var order = _calculatorSelectionOrders.GetValue(grid, static _ => new CalculatorSelectionOrderState());
        order.RecordSelectionEvent(grid, source);

        if (_calculatorWindow is not { IsLoaded: true }) return;

        var version = Interlocked.Increment(ref _calculatorSelectionUpdateVersion);
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (version != Volatile.Read(ref _calculatorSelectionUpdateVersion) ||
                _calculatorWindow is not { IsLoaded: true }) return;

            // Read after the selection routed-event burst has completed so Ctrl/Shift
            // selection produces one stable expression in actual selection order.
            _calculatorWindow.SetLiveSelection(GetLiveCalculatorSelectionValues(grid));
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private IReadOnlyList<decimal> GetLiveCalculatorSelectionValues(DataGrid grid)
    {
        var selectedValues = GetSelectedCalculatorValues(grid);
        if (selectedValues.Count >= 2) return selectedValues;

        var cell = grid.CurrentCell;
        if (cell.Item is not null &&
            !ReferenceEquals(cell.Item, CollectionView.NewItemPlaceholder) &&
            cell.Column is not null)
        {
            var path = GetCalculatorColumnPath(cell.Column);
            if (!string.IsNullOrWhiteSpace(path) &&
                TryGetCalculatorDecimal(ReadCalculatorPathValue(cell.Item, path), out var number))
                return [number];
        }

        return [];
    }

    private CalculatorSeed TryGetCalculatorSeed()
    {
        try
        {
            // Prefer the owning grid over an in-cell TextBox. This keeps a multi-cell
            // selection authoritative even when the current cell happens to be in edit mode.
            if (Keyboard.FocusedElement is DependencyObject focused && FindVisualParent<DataGrid>(focused) is { } grid)
            {
                var selectedValues = GetSelectedCalculatorValues(grid);
                if (selectedValues.Count >= 2)
                {
                    return new CalculatorSeed(
                        BuildCalculatorExpression(selectedValues, '+'),
                        selectedValues);
                }

                var cell = grid.CurrentCell;
                if (cell.Item is not null && !ReferenceEquals(cell.Item, CollectionView.NewItemPlaceholder) && cell.Column is not null)
                {
                    var path = cell.Column.SortMemberPath?.Trim();
                    if (string.IsNullOrWhiteSpace(path) && cell.Column is DataGridBoundColumn bound && bound.Binding is Binding binding)
                        path = binding.Path?.Path;
                    object? value = cell.Item;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        foreach (var segment in path.Split('.'))
                        {
                            if (value is null) break;
                            value = value.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(value);
                        }
                    }
                    var numeric = ExtractNumeric(Convert.ToString(value, CultureInfo.CurrentCulture));
                    if (numeric is not null) return new CalculatorSeed(numeric, []);
                }
            }

            if (Keyboard.FocusedElement is TextBox textBox)
            {
                var selected = textBox.SelectionLength > 0 ? textBox.SelectedText : textBox.Text;
                var numeric = ExtractNumeric(selected);
                if (numeric is not null) return new CalculatorSeed(numeric, []);
            }
        }
        catch
        {
            // Calculator must always remain available even if a third-party/editor control
            // does not expose its current value in the expected shape.
        }
        return new CalculatorSeed(null, []);
    }

    private IReadOnlyList<decimal> GetSelectedCalculatorValues(DataGrid grid)
    {
        var order = _calculatorSelectionOrders.GetValue(grid, static _ => new CalculatorSelectionOrderState());
        order.Prune(grid);

        // Full-row selection uses the numeric current column but preserves the order in
        // which rows were selected (A, C, B stays A, C, B rather than grid row order).
        if (grid.SelectionUnit == DataGridSelectionUnit.FullRow &&
            grid.SelectedItems.Count >= 2 &&
            grid.CurrentCell.Column is { } currentColumn)
        {
            var currentPath = GetCalculatorColumnPath(currentColumn);
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                var selected = grid.SelectedItems.Cast<object>()
                    .Where(item => !ReferenceEquals(item, CollectionView.NewItemPlaceholder))
                    .ToArray();
                var rowValues = new List<decimal>();
                foreach (var item in order.OrderRows(selected))
                {
                    if (TryGetCalculatorDecimal(ReadCalculatorPathValue(item, currentPath), out var number))
                        rowValues.Add(number);
                }
                if (rowValues.Count >= 2) return rowValues;
            }
        }

        if (grid.SelectedCells.Count < 2) return [];

        var selectedCells = grid.SelectedCells
            .Where(cell => cell.Item is not null &&
                           !ReferenceEquals(cell.Item, CollectionView.NewItemPlaceholder) &&
                           cell.Column is not null)
            .ToArray();

        var result = new List<decimal>();
        foreach (var entry in order.OrderCells(selectedCells))
        {
            var path = GetCalculatorColumnPath(entry.Column);
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (TryGetCalculatorDecimal(ReadCalculatorPathValue(entry.Item, path), out var number))
                result.Add(number);
        }

        return result;
    }

    private static string GetCalculatorColumnPath(DataGridColumn column)
    {
        var path = column.SortMemberPath?.Trim();
        if (!string.IsNullOrWhiteSpace(path)) return path;
        if (column is DataGridBoundColumn bound &&
            bound.Binding is Binding binding &&
            !string.IsNullOrWhiteSpace(binding.Path?.Path))
            return binding.Path.Path;
        return string.Empty;
    }

    private static object? ReadCalculatorPathValue(object? item, string path)
    {
        object? value = item;
        foreach (var segment in path.Split('.'))
        {
            if (value is null) break;
            value = value.GetType()
                .GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?.GetValue(value);
        }
        return value;
    }

    private static bool TryGetCalculatorDecimal(object? value, out decimal number)
    {
        number = 0m;
        if (value is null || value is DateTime || value is DateTimeOffset) return false;

        try
        {
            if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string BuildCalculatorExpression(IReadOnlyList<decimal> values, char operation) =>
        string.Join(
            operation.ToString(),
            values.Select(value => value < 0
                ? $"({value.ToString("0.############################", CultureInfo.InvariantCulture)})"
                : value.ToString("0.############################", CultureInfo.InvariantCulture)));

    private static string? ExtractNumeric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var matches = Regex.Matches(text, @"[-+]?\d[\d,]*(?:\.\d+)?");
        if (matches.Count == 0) return null;
        var raw = matches[^1].Value.Replace(",", string.Empty);
        return decimal.TryParse(raw, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.############################", CultureInfo.InvariantCulture)
            : null;
    }

    private sealed class CalculatorSelectionOrderState
    {
        private readonly List<object> _rowOrder = [];
        private readonly List<CalculatorCellSelection> _cellOrder = [];

        public void RecordSelectionEvent(DataGrid grid, DependencyObject? source)
        {
            if (FindVisualParent<DataGridCell>(source) is { } cell &&
                FindVisualParent<DataGridRow>(cell) is { } row &&
                cell.Column is { } column &&
                row.Item is { } item &&
                !ReferenceEquals(item, CollectionView.NewItemPlaceholder))
            {
                SetCell(item, column, cell.IsSelected);
            }

            if (FindVisualParent<DataGridRow>(source) is { } dataRow &&
                dataRow.Item is { } rowItem &&
                !ReferenceEquals(rowItem, CollectionView.NewItemPlaceholder))
            {
                SetRow(rowItem, dataRow.IsSelected);
            }

            // Do not prune inside the routed event itself: WPF may still be updating
            // SelectedCells/SelectedItems during that event burst. The stable background
            // read calls Prune after selection has settled, preserving true click order.
        }

        public void Prune(DataGrid grid)
        {
            var selectedRows = grid.SelectedItems.Cast<object>().ToArray();
            _rowOrder.RemoveAll(item => !selectedRows.Any(selected => ReferenceEquals(selected, item)));

            var selectedCells = grid.SelectedCells.ToArray();
            _cellOrder.RemoveAll(entry => !selectedCells.Any(cell =>
                ReferenceEquals(cell.Item, entry.Item) && ReferenceEquals(cell.Column, entry.Column)));
        }

        public IEnumerable<object> OrderRows(IReadOnlyList<object> selected)
        {
            foreach (var item in _rowOrder)
                if (selected.Any(candidate => ReferenceEquals(candidate, item)))
                    yield return item;

            foreach (var item in selected)
                if (!_rowOrder.Any(candidate => ReferenceEquals(candidate, item)))
                    yield return item;
        }

        public IEnumerable<CalculatorCellSelection> OrderCells(IReadOnlyList<DataGridCellInfo> selected)
        {
            foreach (var entry in _cellOrder)
                if (selected.Any(cell => ReferenceEquals(cell.Item, entry.Item) && ReferenceEquals(cell.Column, entry.Column)))
                    yield return entry;

            foreach (var cell in selected)
            {
                if (cell.Column is null || cell.Item is null) continue;
                if (_cellOrder.Any(entry => ReferenceEquals(entry.Item, cell.Item) && ReferenceEquals(entry.Column, cell.Column))) continue;
                yield return new CalculatorCellSelection(cell.Item, cell.Column);
            }
        }

        private void SetRow(object item, bool selected)
        {
            _rowOrder.RemoveAll(existing => ReferenceEquals(existing, item));
            if (selected) _rowOrder.Add(item);
        }

        private void SetCell(object item, DataGridColumn column, bool selected)
        {
            _cellOrder.RemoveAll(existing => ReferenceEquals(existing.Item, item) && ReferenceEquals(existing.Column, column));
            if (selected) _cellOrder.Add(new CalculatorCellSelection(item, column));
        }
    }

    private sealed record CalculatorCellSelection(object Item, DataGridColumn Column);

    private sealed record CalculatorSeed(
        string? Expression,
        IReadOnlyList<decimal> SelectedValues);

    private static void UndoCurrentEdit()
    {
        if (Keyboard.FocusedElement is not IInputElement target) return;
        if (ApplicationCommands.Undo.CanExecute(null, target))
        {
            ApplicationCommands.Undo.Execute(null, target);
            return;
        }
        if (target is DependencyObject dependency && FindVisualParent<DataGrid>(dependency) is { } grid)
        {
            grid.CancelEdit(DataGridEditingUnit.Cell);
            grid.CancelEdit(DataGridEditingUnit.Row);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(
        object sender,
        RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(
        object sender,
        RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void Close_Click(
        object sender,
        RoutedEventArgs e) =>
        Close();

    private void ExitCompletely_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.RequestExitCompletely();
    }

    private void ToggleMaximizeRestore() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void MainWindow_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Handled) return;

        if (WorkspaceOverlayHost.Visibility == Visibility.Visible)
        {
            if (WorkspaceOverlayHost.Content is AuditWorkspaceView auditView)
                auditView.HandlePreviewKeyDown(e);
            // The selector intentionally owns no accounting shortcuts.
            if (e.Handled || WorkspaceOverlayHost.Content is WorkspaceSelectorView) return;
        }

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (control && !shift && !alt && e.Key == Key.F)
        {
            FocusCurrentModuleSearch();
            e.Handled = true;
            return;
        }
        if (control && !shift && !alt && e.Key == Key.G)
        {
            ShowGoToNavigator();
            e.Handled = true;
            return;
        }
        if (control && !shift && e.Key == Key.Z) { UndoCurrentEdit(); e.Handled = true; return; }
        if (control && alt && e.Key == Key.C) { OpenCalculator(); e.Handled = true; return; }

        if (ShortcutGestureMatcher.Matches(
                e,
                _settings.Get(ApplicationSettingKeys.ShortcutHelp),
                "Ctrl+H"))
        {
            ShowKeyboardShortcuts(); e.Handled = true; return;
        }
        if (ShortcutGestureMatcher.Matches(
                e,
                _settings.Get(ApplicationSettingKeys.ShortcutRefresh),
                "Ctrl+R"))
        {
            RefreshCurrentPage(); e.Handled = true; return;
        }
        if (ShortcutGestureMatcher.Matches(
                e,
                _settings.Get(ApplicationSettingKeys.ShortcutSave),
                "Ctrl+S"))
        {
            SaveCurrentPage(); e.Handled = true; return;
        }
        if (ShortcutGestureMatcher.Matches(
                e,
                _settings.Get(ApplicationSettingKeys.ShortcutClientSearch),
                "Ctrl+K"))
        {
            Navigate("Clients");
            _ = Dispatcher.BeginInvoke(new Action(FocusClientSearch));
            e.Handled = true; return;
        }
        if (ShortcutGestureMatcher.Matches(
                e,
                _settings.Get(ApplicationSettingKeys.ShortcutNewClient),
                "Ctrl+N"))
        {
            if (NetworkClientRuntime.IsNetworkClient) { e.Handled = true; return; }
            Navigate("Clients");
            if (ViewModel?.CurrentPage is ClientManagementViewModel clients)
            {
                Execute(clients.NewCommand);
                _ = Dispatcher.BeginInvoke(new Action(FocusClientCodeEditor));
            }
            e.Handled = true; return;
        }

        if (ViewModel?.CurrentPage is DashboardViewModel dashboard &&
            TryGetFinancialYearMonthIndex(e.Key, out var monthIndex) &&
            !IsTextEntryFocused())
        {
            var scope = Keyboard.IsKeyDown(Key.B) ? "Bank" :
                        Keyboard.IsKeyDown(Key.P) ? "Purchase" :
                        Keyboard.IsKeyDown(Key.S) ? "Sales" : null;
            dashboard.ToggleExportMonthShortcut(monthIndex, scope, shift);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (TryHandleSearchEscape(this)) { e.Handled = true; return; }
            CancelCurrentOperation(); e.Handled = true; return;
        }

        if (!control) return;

        if (shift && ViewModel?.CurrentPage is DashboardViewModel)
        {
            switch (e.Key)
            {
                case Key.B: NavigateAccountingActivity("Banking"); e.Handled = true; return;
                case Key.P: NavigateAccountingActivity("Purchase"); e.Handled = true; return;
                case Key.S: NavigateAccountingActivity("Sales"); e.Handled = true; return;
                case Key.J: NavigateAccountingActivity("Journal"); e.Handled = true; return;
                case Key.C: NavigateAccountingActivity("Cash Entry"); e.Handled = true; return;
                case Key.G: NavigateAccountingActivity("GST"); e.Handled = true; return;
            }
        }

        switch (e.Key)
        {
            case Key.D1:
            case Key.NumPad1: Navigate("Dashboard"); e.Handled = true; break;
            case Key.D2:
            case Key.NumPad2: Navigate("Clients"); e.Handled = true; break;
            case Key.D3:
            case Key.NumPad3: Navigate("Purchase"); e.Handled = true; break;
            case Key.D4:
            case Key.NumPad4: Navigate("Sales"); e.Handled = true; break;
            case Key.D5:
            case Key.NumPad5: Navigate("Ledger Master"); e.Handled = true; break;
            case Key.D6:
            case Key.NumPad6: Navigate("Journal"); e.Handled = true; break;
            case Key.D7:
            case Key.NumPad7: Navigate("GST"); e.Handled = true; break;
            case Key.D8:
            case Key.NumPad8: Navigate("Banking"); e.Handled = true; break;
            case Key.D9:
            case Key.NumPad9: Navigate("Settings"); e.Handled = true; break;
        }
    }

    private static bool TryGetFinancialYearMonthIndex(Key key, out int index)
    {
        index = key switch
        {
            Key.F1 => 0, Key.F2 => 1, Key.F3 => 2, Key.F4 => 3,
            Key.F5 => 4, Key.F6 => 5, Key.F7 => 6, Key.F8 => 7,
            Key.F9 => 8, Key.F10 => 9, Key.F11 => 10, Key.F12 => 11,
            _ => -1
        };
        return index >= 0;
    }

    private static bool IsTextEntryFocused()
    {
        var focused = Keyboard.FocusedElement;
        if (focused is TextBoxBase or DatePickerTextBox) return true;
        if (focused is ComboBox combo && combo.IsEditable) return true;
        if (focused is DataGridCell) return true;
        return false;
    }

    private void Navigate(string title) =>
        ViewModel?.NavigateTo(title);

    private void NavigateAccountingActivity(string title) =>
        ViewModel?.NavigateFromAccountingActivity(title);

    private void SaveCurrentPage()
    {
        if (NetworkClientRuntime.IsNetworkClient) return;
        // Move focus away from a DataGrid cell so its latest editor value is
        // committed to the bound record before the repository save runs.
        PageContent.Focus();

        switch (ViewModel?.CurrentPage)
        {
            case ClientManagementViewModel clients:
                Execute(clients.SaveCommand);
                break;

            case PurchaseImportViewModel purchase:
                Execute(purchase.SaveChangesCommand);
                break;

            case SalesImportViewModel sales:
                Execute(sales.SaveChangesCommand);
                break;

            case JournalViewModel journal:
                Execute(journal.SaveVoucherCommand);
                break;

            case BankingViewModel banking:
                Execute(banking.SaveTransactionCommand);
                break;

            case SettingsViewModel settings:
                Execute(settings.SaveCommand);
                break;
        }
    }

    private async void RefreshCurrentPage()
    {
        if (NetworkClientRuntime.IsNetworkClient)
        {
            try
            {
                if (!await RevalidateNetworkSessionAsync(closeOnRevoked: true, refreshSnapshotOnPermissionChange: false)) return;
                using var api = new FullUiNetworkClientApi
                {
                    Server = NetworkClientRuntime.ResolveServerEndpoint(), DeviceId = NetworkClientRuntime.DeviceId, DeviceToken = NetworkClientRuntime.DeviceToken, SessionToken = NetworkClientRuntime.SessionToken
                };
                await api.DownloadReadOnlySnapshotAsync();
                ViewModel?.ApplyNetworkLiveSnapshotRefresh();
                UpdateNetworkChrome();
                _ = Dispatcher.BeginInvoke(new Action(ApplyNetworkGuardToOpenWindows), System.Windows.Threading.DispatcherPriority.ContextIdle);
                return;
            }
            catch (Exception exception)
            {
                MessageBox.Show($"Could not refresh the Main Server read-only data snapshot.\n\n{exception.Message}", "LAN Refresh", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        switch (ViewModel?.CurrentPage)
        {
            case DashboardViewModel dashboard:
                Execute(dashboard.RefreshCommand);
                break;

            case ClientManagementViewModel clients:
                Execute(
                    clients.IsEditorOpen
                        ? clients.RefreshProfileCommand
                        : clients.ReloadCommand);
                break;

            case PurchaseImportViewModel purchase:
                Execute(purchase.RefreshCommand);
                break;

            case SalesImportViewModel sales:
                Execute(sales.RefreshCommand);
                break;

            case MasterCentreViewModel masters:
                Execute(masters.RefreshCommand);
                break;

            case JournalViewModel journal:
                Execute(journal.RefreshCommand);
                break;

            case AccountingStatementViewModel statement:
                Execute(statement.RefreshCommand);
                break;

            case GstReconciliationViewModel gst:
                Execute(gst.RefreshCommand);
                break;

            case BankingViewModel banking:
                Execute(banking.RefreshCommand);
                break;

            case SettingsViewModel settings:
                if (NetworkClientRuntime.IsNetworkClient) Execute(settings.CheckNetworkUpdateCommand);
                else Execute(settings.RefreshFoldersCommand);
                break;
        }
        if (NetworkClientRuntime.IsNetworkClient) _ = Dispatcher.BeginInvoke(new Action(ApplyNetworkGuardToOpenWindows), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void CancelCurrentOperation()
    {
        switch (ViewModel?.CurrentPage)
        {
            case PurchaseImportViewModel purchase:
                Execute(purchase.CancelCommand);
                break;

            case SalesImportViewModel sales:
                Execute(sales.CancelCommand);
                break;
        }
    }

    // Fix137 R46 R9: Ctrl+F is strictly scoped to the active module visual tree.
    // CachedPageHost retains light pages, so walking PageContent itself can discover the
    // hidden Dashboard All Search and steal focus from the current module.
    private bool FocusCurrentModuleSearch(string? searchText = null)
    {
        var root = PageContent.ActiveVisualRoot ?? PageContent;

        if (ViewModel?.CurrentPage is DashboardViewModel)
        {
            var dashboard = FindVisibleVisualChild<DashboardView>(root);
            if (dashboard is null) return false;
            dashboard.FocusAllSearchBox(searchText);
            return true;
        }

        return FocusSearchInside(root, searchText);
    }

    private bool FocusSearchForWindow(Window sourceWindow, string? searchText = null)
    {
        if (ReferenceEquals(sourceWindow, this))
            return FocusCurrentModuleSearch(searchText);

        return FocusSearchInside(sourceWindow, searchText);
    }

    private static bool FocusSearchInside(DependencyObject root, string? searchText)
    {
        var multiSearch = FindVisibleVisualChild<MultiSearchFilterBar>(root);
        if (multiSearch is not null)
        {
            multiSearch.FocusSearchBox(searchText);
            return true;
        }

        var searchBox = FindNamedSearchTextBox(root);
        if (searchBox is not null)
        {
            if (searchText is not null) searchBox.Text = searchText;
            searchBox.Focus();
            searchBox.SelectAll();
            return true;
        }

        var searchCombo = FindNamedSearchComboBox(root);
        if (searchCombo is not null)
        {
            if (searchText is not null) searchCombo.Text = searchText;
            searchCombo.Focus();
            searchCombo.IsDropDownOpen = true;
            if (searchCombo.Template.FindName("PART_EditableTextBox", searchCombo) is TextBox editor)
                editor.SelectAll();
            return true;
        }

        return false;
    }

    private static T? FindVisibleVisualChild<T>(DependencyObject parent)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result && result.IsVisible && result.IsEnabled) return result;
            var nested = FindVisibleVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static TextBox? FindNamedSearchTextBox(DependencyObject parent)
    {
        TextBox? best = null;
        var bestScore = 0;

        void Visit(DependencyObject node)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            {
                var child = VisualTreeHelper.GetChild(node, index);
                if (child is TextBox box && box.IsVisible && box.IsEnabled && !box.IsReadOnly)
                {
                    var score = ModuleSearchTextBoxScore(box);
                    if (score > bestScore)
                    {
                        best = box;
                        bestScore = score;
                    }
                }
                Visit(child);
            }
        }

        Visit(parent);
        return best;
    }

    private static int ModuleSearchTextBoxScore(TextBox box)
    {
        if (box.Name.Contains("Search", StringComparison.OrdinalIgnoreCase)) return 120;

        var binding = BindingOperations.GetBinding(box, TextBox.TextProperty);
        var path = binding?.Path?.Path ?? string.Empty;
        if (path.Equals("SearchText", StringComparison.OrdinalIgnoreCase)) return 115;
        if (path.EndsWith("SearchText", StringComparison.OrdinalIgnoreCase)) return 110;
        if (path.Contains("Search", StringComparison.OrdinalIgnoreCase)) return 100;

        var toolTip = box.ToolTip?.ToString() ?? string.Empty;
        if (toolTip.StartsWith("Search", StringComparison.OrdinalIgnoreCase) ||
            toolTip.Contains(" search", StringComparison.OrdinalIgnoreCase)) return 90;

        if (path.Contains("FilterText", StringComparison.OrdinalIgnoreCase)) return 80;
        return 0;
    }

    private static ComboBox? FindNamedSearchComboBox(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ComboBox combo && combo.IsVisible && combo.IsEnabled && combo.IsEditable &&
                combo.Name.Contains("Search", StringComparison.OrdinalIgnoreCase))
                return combo;
            var nested = FindNamedSearchComboBox(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    // Fix137 R46 R14: Escape belongs to the focused search surface before it belongs
    // to page-level cancellation. This applies to Ctrl+F module search, Dashboard All Search,
    // and search controls inside AAH-owned secondary windows.
    private bool TryHandleSearchEscape(Window sourceWindow)
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return false;

        var multiSearch = FindVisualAncestor<MultiSearchFilterBar>(focused);
        if (multiSearch is not null && multiSearch.IsVisible && multiSearch.IsEnabled)
        {
            multiSearch.ClearSearchFromEscape();
            return true;
        }

        var combo = FindVisualAncestor<ComboBox>(focused);
        if (combo is not null && combo.IsVisible && combo.IsEnabled && combo.IsEditable &&
            combo.Name.Contains("Search", StringComparison.OrdinalIgnoreCase))
        {
            if (combo.Name.Equals("AllSearchBox", StringComparison.Ordinal) &&
                FindVisualAncestor<DashboardView>(combo) is DashboardView dashboard)
            {
                dashboard.ClearAllSearchFromEscape();
                return true;
            }
            combo.SelectedItem = null;
            combo.Text = string.Empty;
            combo.IsDropDownOpen = false;
            combo.Focus();
            return true;
        }

        if (focused is TextBox box && box.IsVisible && box.IsEnabled && !box.IsReadOnly && ModuleSearchTextBoxScore(box) > 0)
        {
            box.Clear();
            box.Focus();
            return true;
        }

        return false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void GlobalWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Window sourceWindow) return;
        if (sourceWindow.Tag is string tag && tag.Equals("AAH-GoTo", StringComparison.Ordinal))
        {
            if (e.Key == Key.Escape)
            {
                sourceWindow.Close();
                e.Handled = true;
            }
            return;
        }
        if (sourceWindow is RemoteAssistViewerWindow) return;

        if (System.Windows.Application.Current?.MainWindow is not MainWindow main || !main.IsLoaded) return;
        if (!main.OwnsWorkspaceWindow(sourceWindow)) return;

        if (e.Key == Key.Escape && main.TryHandleSearchEscape(sourceWindow))
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (!modifiers.HasFlag(ModifierKeys.Control) ||
            modifiers.HasFlag(ModifierKeys.Shift) ||
            modifiers.HasFlag(ModifierKeys.Alt))
            return;

        if (e.Key == Key.F)
        {
            _ = main.FocusSearchForWindow(sourceWindow);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.G)
        {
            main.ShowGoToNavigator(sourceWindow);
            e.Handled = true;
        }
    }

    private bool OwnsWorkspaceWindow(Window window)
    {
        for (Window? current = window; current is not null; current = current.Owner)
            if (ReferenceEquals(current, this)) return true;
        return false;
    }

    // Fix137 R46 R9: Ctrl+G is a true application-level Go To surface. The source
    // window can be any AAH module/owned window; navigation still goes through the shell's
    // permission-aware MainWindowViewModel.
    private void ShowGoToNavigator(Window? sourceWindow = null)
    {
        if (ViewModel is null) return;
        var destinations = ViewModel.GetGoToDestinations();

        var picker = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = true,
            ItemsSource = destinations,
            MinWidth = 390,
            Margin = new Thickness(0, 8, 0, 12)
        };
        var go = new Button { Content = "Go To", MinWidth = 90, Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Go To · search any available module/page", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(picker);
        panel.Children.Add(go);
        var owner = sourceWindow is { IsVisible: true } && !ReferenceEquals(sourceWindow, this)
            ? sourceWindow
            : this;
        var window = new Window
        {
            Title = "AAH Go To",
            Tag = "AAH-GoTo",
            Owner = owner,
            Width = 520,
            MinWidth = 440,
            MaxWidth = 680,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 320,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };

        void NavigateSelection()
        {
            var query = (picker.Text ?? picker.SelectedItem?.ToString() ?? string.Empty).Trim();
            if (query.Length == 0) return;
            var target = destinations.FirstOrDefault(x => x.Equals(query, StringComparison.OrdinalIgnoreCase))
                ?? destinations.FirstOrDefault(x => x.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                ?? destinations.FirstOrDefault(x => x.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;
            if (ViewModel.NavigateAnywhere(target)) window.Close();
        }
        go.Click += (_, _) => NavigateSelection();
        picker.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                window.Close();
                args.Handled = true;
                return;
            }
            if (args.Key != Key.Enter) return;
            NavigateSelection();
            args.Handled = true;
        };
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            window.Close();
            args.Handled = true;
        };
        window.Loaded += (_, _) => { picker.Focus(); picker.IsDropDownOpen = true; };
        window.ShowDialog();
    }

    internal void NavigateGlobalSearchResult(GlobalSearchResult result, string query)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(result.ModuleTitle)) return;
        if (!ViewModel.NavigateAnywhere(result.ModuleTitle)) return;

        // Module results are navigation-only. Record results carry the Dashboard query into
        // the destination module's own search bar so the exact row/record remains visible.
        if (result.Category.Equals("Module", StringComparison.OrdinalIgnoreCase)) return;
        var filter = string.IsNullOrWhiteSpace(result.SearchText) ? query : result.SearchText;
        _ = Dispatcher.BeginInvoke(
            new Action(() => FocusCurrentModuleSearch(filter)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FocusClientSearch()
    {
        var view = FindVisualChild<ClientManagementView>(PageContent);
        view?.FocusSearchBox();
    }

    private void FocusClientCodeEditor()
    {
        var view = FindVisualChild<ClientManagementView>(PageContent);
        view?.FocusClientCodeBox();
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);

            if (child is T result)
            {
                return result;
            }

            var nested = FindVisualChild<T>(child);

            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void ShowKeyboardShortcuts()
    {
        var lines = new[]
        {
            "WORKSPACE NAVIGATION",
            "Ctrl+1..9   Dashboard, Clients, Purchase, Sales, Ledger Master, Journal, GST, Banking or Settings",
            $"{_settings.Get(ApplicationSettingKeys.ShortcutClientSearch),-12} Search clients",
            $"{_settings.Get(ApplicationSettingKeys.ShortcutNewClient),-12} Add new client",
            $"{_settings.Get(ApplicationSettingKeys.ShortcutSave),-12} Save current edits",
            $"{_settings.Get(ApplicationSettingKeys.ShortcutRefresh),-12} Refresh the current page",
            $"{_settings.Get(ApplicationSettingKeys.ShortcutHelp),-12} Show this shortcut list",
            "Ctrl+F       Focus search in the active module (Dashboard: All Search)",
            "Ctrl+G       Go To any module/page available in the current workspace",
            "Ctrl+Z       Undo the current editor/grid change",
            "Ctrl+Alt+C   Open Universal Calculator",
            "Esc          Cancel the active import operation",
            string.Empty,
            "DASHBOARD ACTIVITY",
            "Ctrl+Shift+B/P/S/J/C/G   Banking, Purchase, Sales, Journal, Cash Entry or GST",
            string.Empty,
            "TALLY EXPORT MONTHS (fixed)",
            "F1..F12      April through March in every available export section",
            "Hold B/P/S + F1..F12     Bank, Purchase or Sales only",
            "Shift+Fn     Range selection/deselection",
            string.Empty,
            "Configurable shortcuts can be changed under Settings → Shortcuts."
        };
        MessageBox.Show(string.Join(Environment.NewLine, lines),
            "Accounting Automation Hub — Keyboard Shortcuts",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void FitWindowToWorkingArea()
    {
        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(640d, work.Width);
        var maxHeight = Math.Max(420d, work.Height);
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        if (NetworkClientRuntime.IsNetworkClient && WindowState == WindowState.Normal && (work.Width < 1440d || work.Height < 900d))
        {
            WindowState = WindowState.Maximized;
            return;
        }
        if (WindowState == WindowState.Normal)
        {
            Width = Math.Min(Math.Max(MinWidth, Width), maxWidth);
            Height = Math.Min(Math.Max(MinHeight, Height), maxHeight);
            if (Left < work.Left) Left = work.Left;
            if (Top < work.Top) Top = work.Top;
            if (Left + Width > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            if (Top + Height > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
        }
    }

    private void RestoreWindowPlacement()
    {
        if (!_settings.GetBool(ApplicationSettingKeys.RememberWindowPlacement)) return;
        if (TryGetDouble(ApplicationSettingKeys.WindowWidth, out var width) && width >= MinWidth) Width = width;
        if (TryGetDouble(ApplicationSettingKeys.WindowHeight, out var height) && height >= MinHeight) Height = height;
        if (TryGetDouble(ApplicationSettingKeys.WindowLeft, out var left) && TryGetDouble(ApplicationSettingKeys.WindowTop, out var top))
        {
            var visible = left + 80 >= SystemParameters.VirtualScreenLeft
                          && top + 60 >= SystemParameters.VirtualScreenTop
                          && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
                          && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        if (_settings.Get(ApplicationSettingKeys.WindowState).Equals("Maximized", StringComparison.OrdinalIgnoreCase))
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        if (!_settings.GetBool(ApplicationSettingKeys.RememberWindowPlacement)) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            _settings.Set(ApplicationSettingKeys.WindowLeft, bounds.Left.ToString(CultureInfo.InvariantCulture));
            _settings.Set(ApplicationSettingKeys.WindowTop, bounds.Top.ToString(CultureInfo.InvariantCulture));
            _settings.Set(ApplicationSettingKeys.WindowWidth, bounds.Width.ToString(CultureInfo.InvariantCulture));
            _settings.Set(ApplicationSettingKeys.WindowHeight, bounds.Height.ToString(CultureInfo.InvariantCulture));
        }
        _settings.Set(ApplicationSettingKeys.WindowState, WindowState == WindowState.Maximized ? "Maximized" : "Normal");
    }

    private bool TryGetDouble(string key, out double value) =>
        double.TryParse(_settings.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

}
