using System.Windows.Media.Imaging;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class RemoteAssistViewModel : PageViewModel, IDisposable
{
    private string _status = "Remote Assist ready. No request is waiting.";

    public RemoteAssistViewModel()
        : base("Remote Assist", "Live two-way AAH-window support, consent, sharing and session control")
    {
        AcceptCommand = new RelayCommand(Accept, () => HasPendingRequest);
        DeclineCommand = new RelayCommand(Decline, () => HasPendingRequest);
        EndCommand = new AsyncRelayCommand(EndAsync, () => HasActiveSession);
        RefreshCommand = new RelayCommand(Refresh);
        RemoteAssistConsentCenter.Changed += OnCenterChanged;
        RemoteAssistLocalStreamState.Changed += OnStreamChanged;
        RemoteAssistLocalStreamState.FrameChanged += OnFrameChanged;
        Refresh();
    }

    public RelayCommand AcceptCommand { get; }
    public RelayCommand DeclineCommand { get; }
    public AsyncRelayCommand EndCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public bool HasPendingRequest => RemoteAssistConsentCenter.Pending is not null;
    public bool HasActiveSession => RemoteAssistConsentCenter.Active is not null || RemoteAssistPrivilegeContext.IsActive;
    public bool HasNoRequest => !HasPendingRequest && !HasActiveSession;

    public string PendingTitle => HasPendingRequest ? "Remote Assist request waiting for your approval" : "No pending Remote Assist request";
    public string PendingHelper => RemoteAssistConsentCenter.Pending is { } p ? $"Helper: {p.HelperUser} · Requested by: {p.RequestedBy}" : string.Empty;
    public string PendingMode => RemoteAssistConsentCenter.Pending is { } p ? $"Permission mode: {p.PermissionMode}" : string.Empty;
    public string PendingScope => RemoteAssistConsentCenter.Pending is { } p ? $"Scope: {p.Scope}" : string.Empty;
    public string PendingReason => RemoteAssistConsentCenter.Pending is { } p ? $"Reason: {(string.IsNullOrWhiteSpace(p.Reason) ? "Support" : p.Reason)}" : string.Empty;
    public string PendingExpiry => RemoteAssistConsentCenter.Pending is { } p ? $"Request expires: {p.ExpiresUtc.ToLocalTime():dd-MM-yyyy HH:mm:ss}" : string.Empty;

    public string ActiveTitle => HasActiveSession ? "Remote Assist is active" : "No active Remote Assist session";
    public string ActiveSummary
    {
        get
        {
            var active = RemoteAssistConsentCenter.Active;
            if (active is not null)
                return $"Helper {active.HelperUser} · Mode {active.PermissionMode} · Scope {active.Scope} · Expires {active.ExpiresUtc.ToLocalTime():dd-MM-yyyy HH:mm:ss}";
            return RemoteAssistPrivilegeContext.Describe();
        }
    }

    public bool ShareMyAah
    {
        get => RemoteAssistLocalStreamState.ShareMyAah;
        set { RemoteAssistLocalStreamState.ShareMyAah=value; OnPropertyChanged(); OnPropertyChanged(nameof(StreamSummary)); }
    }
    public bool AllowAdministratorControl
    {
        get => RemoteAssistLocalStreamState.AllowAdministratorControl;
        set { RemoteAssistLocalStreamState.AllowAdministratorControl=value; OnPropertyChanged(); OnPropertyChanged(nameof(StreamSummary)); }
    }
    public bool AdministratorSharing => RemoteAssistLocalStreamState.AdministratorSharing;
    public bool AdministratorAllowsRemoteControl => RemoteAssistLocalStreamState.AdministratorAllowsRemoteControl;
    public BitmapImage? AdministratorFrame => RemoteAssistLocalStreamState.AdministratorFrame;
    public string StreamSummary => $"My AAH: {(ShareMyAah?"Sharing":"Paused")} · Administrator control {(AllowAdministratorControl?"Allowed":"View only")} · Main Server AAH: {(AdministratorSharing?"Shared":"Not shared")} · My control {(AdministratorAllowsRemoteControl?"Allowed":"View only")} · Transport {NetworkClientRuntime.RemoteAssistTransportLabel}.";
    public string TransportSummary => $"Control/session authority: {NetworkClientRuntime.ResolveServerEndpoint()} · Visual/input preferred route: {NetworkClientRuntime.ResolveRemoteAssistEndpoint()} · automatic fallback to control route.";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private void Accept()
    {
        if (!RemoteAssistConsentCenter.AcceptPending()) return;
        Status = "Remote Assist accepted. Live AAH-window sharing starts automatically after the Main Server finalises consent.";
        Refresh();
    }

    private void Decline()
    {
        if (!RemoteAssistConsentCenter.DeclinePending()) return;
        Status = "Remote Assist request declined.";
        Refresh();
    }

    private async Task EndAsync()
    {
        var active = RemoteAssistConsentCenter.Active;
        var sessionId = active?.SessionId ?? RemoteAssistPrivilegeContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        try
        {
            using var api = CreateControlApi();
            await api.EndRemoteAssistByUserAsync(sessionId).ConfigureAwait(true);
            RemoteAssistPrivilegeContext.End();
            RemoteAssistConsentCenter.EndSession(sessionId);
            RemoteAssistLocalStreamState.End();
            if (System.Windows.Application.Current?.MainWindow is MainWindow window) window.RefreshRemoteAssistBanner();
            NotificationHistoryService.Record(string.Empty, "Remote Assist", "Ended", "Remote Assist ended by AAH user", "The AAH user ended Remote Assist. Visual sharing, control and helper privileges were removed immediately.");
            Status = "Remote Assist ended. Normal LAN permissions remain unchanged.";
        }
        catch (Exception exception) { Status = "Could not end Remote Assist on the Main Server: " + exception.Message; }
        Refresh();
    }

    public async Task SendAdministratorInputAsync(string kind,double x,double y,int delta=0,int key=0,string text="",bool control=false,bool shift=false,bool alt=false)
    {
        var sessionId=RemoteAssistPrivilegeContext.SessionId;
        if(string.IsNullOrWhiteSpace(sessionId)||!AdministratorSharing||!AdministratorAllowsRemoteControl)return;
        var input=new RemoteAssistInputRecord{Kind=kind,X=Math.Clamp(x,0,1),Y=Math.Clamp(y,0,1),Delta=delta,Key=key,Text=text,Control=control,Shift=shift,Alt=alt,Direction=RemoteAssistStreamDirections.RemoteToAdministrator};
        var preferredEndpoint=NetworkClientRuntime.ResolveRemoteAssistEndpoint();
        var primaryEndpoint=NetworkClientRuntime.ResolveServerEndpoint();
        try
        {
            using var api=CreateStreamApi();
            await api.SendRemoteAssistAdministratorInputAsync(sessionId,input);
        }
        catch(Exception first) when(!preferredEndpoint.Equals(primaryEndpoint,StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var fallback=CreateControlApi();
                await fallback.SendRemoteAssistAdministratorInputAsync(sessionId,input);
            }
            catch(Exception second){Status="Could not send control input to the Main Server AAH: "+second.Message+" (preferred route also failed: "+first.Message+")";}
        }
        catch(Exception ex){Status="Could not send control input to the Main Server AAH: "+ex.Message;}
    }

    private static FullUiNetworkClientApi CreateControlApi() => new()
    {
        Server = NetworkClientRuntime.ResolveServerEndpoint(), DeviceId = NetworkClientRuntime.DeviceId,
        DeviceToken = NetworkClientRuntime.DeviceToken, SessionToken = NetworkClientRuntime.SessionToken
    };

    private static FullUiNetworkClientApi CreateStreamApi() => new()
    {
        Server = NetworkClientRuntime.ResolveRemoteAssistEndpoint(), DeviceId = NetworkClientRuntime.DeviceId,
        DeviceToken = NetworkClientRuntime.DeviceToken, SessionToken = NetworkClientRuntime.SessionToken
    };

    private void OnCenterChanged(object? sender, EventArgs e)
    {
        if (HasNoRequest)
            Status = "No Remote Assist request or active access remains on this PC.";
        Refresh();
    }
    private void OnStreamChanged(object? sender, EventArgs e) => System.Windows.Application.Current?.Dispatcher.Invoke(Refresh);
    private void OnFrameChanged(object? sender, EventArgs e) => System.Windows.Application.Current?.Dispatcher.Invoke(() => OnPropertyChanged(nameof(AdministratorFrame)));

    public void Refresh()
    {
        OnPropertyChanged(nameof(HasPendingRequest));OnPropertyChanged(nameof(HasActiveSession));OnPropertyChanged(nameof(HasNoRequest));
        OnPropertyChanged(nameof(PendingTitle));OnPropertyChanged(nameof(PendingHelper));OnPropertyChanged(nameof(PendingMode));OnPropertyChanged(nameof(PendingScope));OnPropertyChanged(nameof(PendingReason));OnPropertyChanged(nameof(PendingExpiry));
        OnPropertyChanged(nameof(ActiveTitle));OnPropertyChanged(nameof(ActiveSummary));OnPropertyChanged(nameof(ShareMyAah));OnPropertyChanged(nameof(AllowAdministratorControl));OnPropertyChanged(nameof(AdministratorSharing));OnPropertyChanged(nameof(AdministratorAllowsRemoteControl));OnPropertyChanged(nameof(AdministratorFrame));OnPropertyChanged(nameof(StreamSummary));OnPropertyChanged(nameof(TransportSummary));
        AcceptCommand.RaiseCanExecuteChanged();DeclineCommand.RaiseCanExecuteChanged();EndCommand.RaiseCanExecuteChanged();
    }

    public void Dispose(){RemoteAssistConsentCenter.Changed-=OnCenterChanged;RemoteAssistLocalStreamState.Changed-=OnStreamChanged;RemoteAssistLocalStreamState.FrameChanged-=OnFrameChanged;}
}
