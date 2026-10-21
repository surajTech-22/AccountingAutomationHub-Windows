using System.Windows;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.Views;

public partial class NetworkFullUiLoginWindow : Window
{
    private readonly FullUiNetworkClientApi _api = new();
    private string _challengeId = string.Empty;
    private bool _forceUpdate;
    private bool _enrollmentAttemptFailed;
    private NetworkClientStartupMode _startupMode;
    public string StatusText { get; private set; } = "Enter the AAH User ID/password created by the Administrator.";

    public NetworkFullUiLoginWindow(NetworkClientStartupMode startupMode = NetworkClientStartupMode.PasswordOnly, string startupStatus = "")
    {
        _startupMode = startupMode;
        InitializeComponent(); DataContext=this;
        if (!string.IsNullOrWhiteSpace(startupStatus)) StatusText = startupStatus;
        var device=NetworkClientCredentialStore.LoadDevice();
        var login=NetworkClientCredentialStore.LoadLogin();
        var embedded=NetworkClientRuntime.DefaultServerEndpoint;
        var server=embedded.Length>0?embedded:device.Server;
        ServerBox.Text=server;ServerBox.IsReadOnly=embedded.Length>0;
        _api.Server=server;_api.DeviceId=device.DeviceId;_api.DeviceToken=device.Token;
        if(login.User.Length>0){UserBox.Text=login.User;PasswordBox.Password=login.Password;SaveLoginBox.IsChecked=true;}
        EnrollmentBox.IsEnabled = startupMode == NetworkClientStartupMode.Enrollment || device.DeviceId.Length == 0;
        ApplyStartupMode(startupMode);
        Loaded += async (_,_) => await InitializeLoginAsync();
    }

    private async void Enroll_Click(object sender,RoutedEventArgs e)
    {
        _enrollmentAttemptFailed = true;
        await Run(async()=>
        {
            _challengeId = string.Empty;
            NetworkClientCredentialStore.DeleteSession();
            NetworkClientRuntime.ClearSession();
            _api.Server=ResolveServer();
            await _api.EnrollAsync(EnrollmentBox.Text.Trim(),UserBox.Text.Trim(),PasswordBox.Password,Environment.MachineName);
            _enrollmentAttemptFailed = false;
            ApplyStartupMode(NetworkClientStartupMode.RegistrationOtp);
            StatusText="This PC is enrolled. Click Request Registration OTP, enter the one-time code shown on the Main Server, then click Open AAH. After verification, normal login will not request OTP again unless this PC is inactive for 30 days or an Administrator resets its registration trust.";Refresh();
            RequestOtpButton.Focus();
            await CheckUpdateAsync(autoInstallForced:true);
        });
    }
    private async void RequestOtp_Click(object sender,RoutedEventArgs e)
    {
        await Run(async()=>
        {
            if(await CheckUpdateAsync(autoInstallForced:true))return;
            if(_enrollmentAttemptFailed) throw new InvalidOperationException("The last enrollment attempt failed. Retry Enroll This PC successfully before requesting an OTP; AAH will not fall back to an older saved device credential after a failed enrollment.");
            _api.Server=ResolveServer();
            if(string.IsNullOrWhiteSpace(_api.DeviceId)){var d=NetworkClientCredentialStore.LoadDevice();_api.DeviceId=d.DeviceId;_api.DeviceToken=d.Token;}
            if(string.IsNullOrWhiteSpace(_api.DeviceId)||string.IsNullOrWhiteSpace(_api.DeviceToken)) throw new InvalidOperationException("This LAN PC is not enrolled yet. Enter the one-use enrollment code and click Enroll This PC before requesting an OTP.");
            var result=await _api.RequestOtpAsync(UserBox.Text.Trim(),PasswordBox.Password);
            _challengeId=result.ChallengeId;
            StatusText=$"{result.OtpType}: {result.Message} The Main Server panel refreshes automatically.";
            OtpTypeText.Text=result.OtpType;
            Refresh();
        });
    }
    private async void Login_Click(object sender,RoutedEventArgs e)
    {
        await Run(async()=>
        {
            if(await CheckUpdateAsync(autoInstallForced:true))return;

            if (string.IsNullOrWhiteSpace(_api.SessionToken))
            {
                NetworkSessionIdentity result;
                if(_challengeId.Length>0)
                {
                    StatusText="Verifying the one-time Registration OTP and activating trusted-device login...";Refresh();
                    result=await _api.LoginWithOtpAsync(_challengeId,OtpBox.Text.Trim());
                    _challengeId=string.Empty;
                    OtpBox.Clear();
                    StatusText="Registration OTP verified. This PC will now use normal User ID/password login without OTP.";Refresh();
                }
                else
                {
                    StatusText="Signing in from this registered PC without OTP...";Refresh();
                    try { result=await _api.TrustedLoginAsync(UserBox.Text.Trim(),PasswordBox.Password); }
                    catch(NetworkApiException exception) when(exception.RequiresReEnrollment)
                    {
                        ResetForFreshEnrollment("The Main Server no longer accepts this PC registration. Enter a new one-use enrollment code and enroll this PC again. Registration OTP will appear immediately after enrollment.");
                        return;
                    }
                    catch(NetworkApiException exception) when(exception.StatusCode==403 && exception.Code.Equals("REGISTRATION_OTP_REQUIRED",StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyStartupMode(NetworkClientStartupMode.RegistrationOtp);
                        throw new InvalidOperationException("This PC requires its one-time Registration OTP. Click Request Registration OTP, enter the code shown on the Main Server, then click Open AAH. Routine logins will not use OTP after that.",exception);
                    }
                }
                if(SaveLoginBox.IsChecked==true)NetworkClientCredentialStore.SaveLogin(UserBox.Text.Trim(),PasswordBox.Password);else NetworkClientCredentialStore.DeleteLogin();
                ApplyAuthenticatedSession(result);
            }

            StatusText="Authenticated. Downloading the latest read-only Main Server data...";Refresh();
            try
            {
                await _api.DownloadReadOnlySnapshotAsync();
                DialogResult=true;
            }
            catch (Exception exception)
            {
                // Do not submit the already-consumed OTP a second time. The authenticated session is retained
                // so Open AAH retries only the disposable read-only snapshot download.
                throw new InvalidOperationException($"Login succeeded, but the read-only Main Server data could not be loaded. {exception.Message} Click Open AAH again to retry the data download; registration OTP is not required again.", exception);
            }
        });
    }
    private async void InstallUpdate_Click(object sender,RoutedEventArgs e)
    {
        await Run(async()=>
        {
            if(string.IsNullOrWhiteSpace(_api.DeviceId))throw new InvalidOperationException("Enroll this LAN PC first so the Main Server can authorise its update download.");
            await NetworkClientSelfUpdateService.DownloadVerifyAndScheduleAsync(_api,this,_forceUpdate);
            if (System.Windows.Application.Current is AccountingAutomationHub.Desktop.App app) app.ExitApplication(); else System.Windows.Application.Current.Shutdown();
        });
    }

    private void ApplyStartupMode(NetworkClientStartupMode mode)
    {
        _startupMode = mode;
        var enrolled = !string.IsNullOrWhiteSpace(_api.DeviceId) && !string.IsNullOrWhiteSpace(_api.DeviceToken);
        EnrollmentPanel.Visibility = mode == NetworkClientStartupMode.Enrollment || !enrolled ? Visibility.Visible : Visibility.Collapsed;
        EnrollmentBox.IsEnabled = mode == NetworkClientStartupMode.Enrollment || !enrolled;
        EnrollButton.IsEnabled = mode == NetworkClientStartupMode.Enrollment || !enrolled;
        RegistrationOtpPanel.Visibility = mode == NetworkClientStartupMode.RegistrationOtp ? Visibility.Visible : Visibility.Collapsed;
        if (mode == NetworkClientStartupMode.PasswordOnly && enrolled)
        {
            EnrollmentPanel.Visibility = Visibility.Collapsed;
            RegistrationOtpPanel.Visibility = Visibility.Collapsed;
        }
        if (mode == NetworkClientStartupMode.UpdateRequired)
        {
            EnrollmentPanel.Visibility = Visibility.Collapsed;
            RegistrationOtpPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task InitializeLoginAsync()
    {
        FitToWorkingArea();
        try
        {
            if (await CheckUpdateAsync(autoInstallForced:true)) return;
            await TryRestoreSavedSessionAsync();
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            Refresh();
        }
    }

    private void FitToWorkingArea()
    {
        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(360d, work.Width - 16d);
        var maxHeight = Math.Max(360d, work.Height - 16d);
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Math.Max(MinWidth, Width), maxWidth);
        Height = Math.Min(Math.Max(MinHeight, Height), maxHeight);
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
    }

    private async Task TryRestoreSavedSessionAsync()
    {
        var saved = NetworkClientCredentialStore.LoadSession();
        if (saved is null) return;
        var server = ResolveServer();
        var device = NetworkClientCredentialStore.LoadDevice();
        if (!saved.Server.Equals(server, StringComparison.OrdinalIgnoreCase) ||
            !saved.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase) ||
            saved.ExpiresUtc <= DateTime.UtcNow.AddMinutes(1))
        {
            NetworkClientCredentialStore.DeleteSession();
            return;
        }

        NetworkSessionIdentity identity;
        try
        {
            StatusText = "Restoring the existing registered-device LAN session..."; Refresh();
            _api.Server = server;
            _api.DeviceId = device.DeviceId;
            _api.DeviceToken = device.Token;
            identity = await _api.RestoreSessionAsync(saved.SessionToken);
        }
        catch (NetworkApiException exception) when (exception.StatusCode is 401 or 403)
        {
            // Only a definite Main Server authentication/revocation response invalidates the saved
            // registered-device session. Network/server availability errors retain the session so a reconnect does
            // not unnecessarily force another OTP.
            NetworkClientCredentialStore.DeleteSession();
            NetworkClientRuntime.ClearSession();
            _api.SessionToken = string.Empty;
            var login = NetworkClientCredentialStore.LoadLogin();
            if(login.User.Length>0 && login.Password.Length>0)
            {
                try
                {
                    StatusText="The previous short login session ended. Re-signing in from this registered PC without OTP...";Refresh();
                    identity=await _api.TrustedLoginAsync(login.User,login.Password);
                    ApplyAuthenticatedSession(identity);
                    await _api.DownloadReadOnlySnapshotAsync();
                    DialogResult=true;
                    return;
                }
                catch(NetworkApiException trustedException) when(trustedException.RequiresReEnrollment)
                {
                    ResetForFreshEnrollment("This PC registration was removed or rejected by the Main Server. Enter a new one-use enrollment code and enroll this PC again; the Registration OTP control will appear immediately afterward.");
                    return;
                }
                catch(NetworkApiException trustedException) when(trustedException.StatusCode==403 && trustedException.Code.Equals("REGISTRATION_OTP_REQUIRED",StringComparison.OrdinalIgnoreCase))
                {
                    ApplyStartupMode(NetworkClientStartupMode.RegistrationOtp);
                    StatusText="This PC needs Registration OTP verification because its registration trust expired or an Administrator reset it. Request the one-time Registration OTP to continue.";Refresh();
                    return;
                }
                catch { }
            }
            StatusText = "The previous login session ended. Enter your AAH User ID/password and click Open AAH. OTP is required only if device registration verification has expired or was reset by an Administrator.";
            Refresh();
            return;
        }
        catch (Exception exception)
        {
            StatusText = $"AAH could not validate the saved registered-device session with the Main Server. The saved session was retained. {exception.Message} Retry when the Main Server is reachable.";
            Refresh();
            return;
        }

        ApplyAuthenticatedSession(identity);
        StatusText = "Existing registered-device session is valid. Refreshing Main Server data..."; Refresh();
        try
        {
            await _api.DownloadReadOnlySnapshotAsync();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            // Keep the valid saved session. Open AAH retries only the data refresh and does not
            // request another OTP unless the Main Server later expires/revokes this session.
            StatusText = $"Your login session is still valid, but Main Server data could not be refreshed. {exception.Message} Click Open AAH to retry; Registration OTP is not required again.";
            Refresh();
        }
    }

    private void ApplyAuthenticatedSession(NetworkSessionIdentity identity)
    {
        NetworkClientRuntime.Server = _api.Server;
        NetworkClientRuntime.DeviceId = _api.DeviceId;
        NetworkClientRuntime.DeviceToken = _api.DeviceToken;
        NetworkClientRuntime.SessionToken = identity.SessionToken;
        NetworkClientRuntime.ApplyIdentity(
            string.IsNullOrWhiteSpace(identity.UserId) ? UserBox.Text.Trim() : identity.UserId,
            identity.DisplayName,
            identity.RoleName,
            identity.ExpiresUtc,
            identity.Permissions);
        NetworkClientCredentialStore.SaveSession(new NetworkSavedSession(
            _api.Server, _api.DeviceId, NetworkClientRuntime.UserName, NetworkClientRuntime.DisplayName,
            NetworkClientRuntime.RoleName, identity.SessionToken, identity.ExpiresUtc, identity.Permissions));
        if (string.IsNullOrWhiteSpace(UserBox.Text)) UserBox.Text = NetworkClientRuntime.UserName;
    }

    private async Task<bool> CheckUpdateAsync(bool autoInstallForced)
    {
        if(string.IsNullOrWhiteSpace(_api.DeviceId)||string.IsNullOrWhiteSpace(_api.DeviceToken)||string.IsNullOrWhiteSpace(ResolveServer()))return false;
        _api.Server=ResolveServer();
        NetworkUpdateStatus status;
        try { status=await _api.GetUpdateStatusAsync(); }
        catch(NetworkApiException exception) when(exception.RequiresReEnrollment)
        {
            ResetForFreshEnrollment("The Main Server no longer has or accepts this PC registration. Enter a new one-use enrollment code and enroll this PC again. Registration OTP will be available immediately after enrollment.");
            return false;
        }
        if(status.RegistrationOtpRequired && _challengeId.Length==0)
        {
            ApplyStartupMode(NetworkClientStartupMode.RegistrationOtp);
            StatusText="This PC requires its one-time Registration OTP before normal login. This happens after initial enrollment, 30 days of inactivity, or an Administrator registration reset.";
        }
        _forceUpdate=status.ForceUpdate;
        InstallUpdateButton.Visibility=status.UpdateRequired?Visibility.Visible:Visibility.Collapsed;
        UpdateStatusText.Text=status.UpdateRequired
            ? $"Main Server version {status.ApprovedVersion} is available. Installed: {status.CurrentVersion}."+(status.ForceUpdate?" Administrator has marked this update REQUIRED.":" You may update now.")
            : $"This LAN application is current ({status.CurrentVersion}).";
        SetLoginEnabled(!status.ForceUpdate);
        if(status.ForceUpdate&&autoInstallForced)
        {
            StatusText=$"Main Server requires version {status.ApprovedVersion}. Updating before login...";Refresh();
            await NetworkClientSelfUpdateService.DownloadVerifyAndScheduleAsync(_api,this,true);
            if (System.Windows.Application.Current is AccountingAutomationHub.Desktop.App app) app.ExitApplication(); else System.Windows.Application.Current.Shutdown();
            return true;
        }
        return status.ForceUpdate;
    }

    private void ResetForFreshEnrollment(string status)
    {
        NetworkClientCredentialStore.DeleteDevice();
        NetworkClientCredentialStore.DeleteSession();
        NetworkClientRuntime.ClearSession();
        NetworkClientRuntime.DeviceId=string.Empty;
        NetworkClientRuntime.DeviceToken=string.Empty;
        _api.DeviceId=string.Empty;
        _api.DeviceToken=string.Empty;
        _api.SessionToken=string.Empty;
        _challengeId=string.Empty;
        _forceUpdate=false;
        EnrollmentBox.Clear();
        OtpBox.Clear();
        ApplyStartupMode(NetworkClientStartupMode.Enrollment);
        StatusText=status;
        UpdateStatusText.Text="Fresh device enrollment is required before update/login status can be checked.";
        SetLoginEnabled(true);
        Refresh();
        EnrollmentBox.Focus();
    }

    private string ResolveServer()
    {
        var embedded=NetworkClientRuntime.DefaultServerEndpoint;
        var value=(embedded.Length>0?embedded:ServerBox.Text).Trim();
        ServerBox.Text=value;return value;
    }
    private void SetLoginEnabled(bool enabled){LoginButton.IsEnabled=enabled;RequestOtpButton.IsEnabled=enabled;OtpBox.IsEnabled=enabled;PasswordBox.IsEnabled=enabled;UserBox.IsEnabled=enabled;}
    private async Task Run(Func<Task> action){try{IsEnabled=false;await action();}catch(Exception ex){StatusText=ex.Message;Refresh();}finally{IsEnabled=true;if(_forceUpdate)SetLoginEnabled(false);}}
    private void Refresh(){DataContext=null;DataContext=this;}
    protected override void OnClosed(EventArgs e){_api.Dispose();base.OnClosed(e);}
}
