using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.Services;

public enum NetworkClientStartupMode
{
    SilentReady,
    PasswordOnly,
    RegistrationOtp,
    Enrollment,
    UpdateRequired
}

public sealed record NetworkClientStartupResult(NetworkClientStartupMode Mode, string Status, bool ForceUpdate = false);

/// <summary>
/// Resolves LAN startup before any login window is shown. All network work stays asynchronous so
/// the WPF dispatcher can continue pumping messages; a saved registered-device session/login can
/// therefore open AAH directly without an OTP/login flash or a UI-thread async deadlock.
/// </summary>
public static class NetworkClientStartupCoordinator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromMinutes(5);

    public static async Task<NetworkClientStartupResult> PrepareAsync(Action<string>? reportStatus = null)
    {
        static void Report(Action<string>? callback, string text)
        {
            NetworkClientStartupLog.Write(text);
            callback?.Invoke(text);
        }

        var device = NetworkClientCredentialStore.LoadDevice();
        if (string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(device.Token))
        {
            Report(reportStatus, "This Windows PC has not been enrolled with the Main Server yet.");
            return new(NetworkClientStartupMode.Enrollment, "This Windows PC has not been enrolled with the Main Server yet.");
        }

        FullUiNetworkClientApi? api = null;
        try
        {
            Report(reportStatus, "Checking Main Server over the preferred Internet/private route, with LAN fallback...");
            NetworkUpdateStatus? update = null;
            Exception? lastConnectionError = null;
            foreach (var endpoint in NetworkClientRuntime.GetServerEndpointCandidates(device.Server))
            {
                var candidate = new FullUiNetworkClientApi
                {
                    Server = endpoint,
                    DeviceId = device.DeviceId,
                    DeviceToken = device.Token
                };
                try
                {
                    update = await WithTimeoutAsync(candidate.GetUpdateStatusAsync(), ProbeTimeout, $"Main Server startup check ({endpoint})");
                    api = candidate;
                    NetworkClientRuntime.Server = endpoint;
                    NetworkClientStartupLog.Write($"Main Server route selected: {endpoint}. Remote Assist transport: {NetworkClientRuntime.RemoteAssistTransportLabel}.");
                    break;
                }
                catch (NetworkApiException exception) when (exception.RequiresReEnrollment || exception.StatusCode is 401 or 403)
                {
                    candidate.Dispose();
                    throw;
                }
                catch (Exception exception)
                {
                    lastConnectionError = exception;
                    NetworkClientStartupLog.WriteException($"Main Server route failed: {endpoint}", exception);
                    candidate.Dispose();
                }
            }
            if (api is null || update is null)
                throw new InvalidOperationException("No configured private Main Server route could be reached. Check the Internet/private-VPN route or office LAN connection.", lastConnectionError);
            if (update.ForceUpdate)
                return new(NetworkClientStartupMode.UpdateRequired, $"Main Server requires AAH LAN {update.ApprovedVersion} before login.", true);
            if (update.RegistrationOtpRequired)
                return new(NetworkClientStartupMode.RegistrationOtp, "This PC registration requires one-time verification before normal sign-in.");
        }
        catch (NetworkApiException exception) when (exception.RequiresReEnrollment)
        {
            ResetStaleLocalRegistration();
            Report(reportStatus, "The Main Server no longer has this Windows PC registration. Fresh enrollment is required.");
            return new(NetworkClientStartupMode.Enrollment, "This PC was removed or its registration was rejected by the Main Server. Enter a new one-use enrollment code, enroll this PC again, then complete one Registration OTP.");
        }
        catch (NetworkApiException exception) when (exception.StatusCode is 401 or 403)
        {
            return new(NetworkClientStartupMode.PasswordOnly, exception.Message);
        }
        catch (Exception exception)
        {
            NetworkClientStartupLog.WriteException("Main Server startup check", exception);
            return new(NetworkClientStartupMode.PasswordOnly, $"Main Server could not be contacted for silent startup. {Friendly(exception)}");
        }

        if (api is null)
            return new(NetworkClientStartupMode.PasswordOnly, "Main Server route could not be established.");
        using var apiLifetime = api;

        var saved = NetworkClientCredentialStore.LoadSession();
        if (saved is not null && saved.ExpiresUtc > DateTime.UtcNow.AddMinutes(1) &&
            saved.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase) &&
            saved.Server.Equals(api.Server, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Report(reportStatus, "Restoring saved sign-in...");
                var identity = await WithTimeoutAsync(api.RestoreSessionAsync(saved.SessionToken), AuthenticationTimeout, "Saved LAN session restore");
                ApplyIdentity(api, identity);
                Report(reportStatus, "Refreshing authorised read-only data. A large first sync can take a few minutes...");
                await WithTimeoutAsync(api.DownloadReadOnlySnapshotAsync(), SnapshotTimeout, "Authorised LAN data refresh");
                Report(reportStatus, "Saved LAN session restored. Opening workspace...");
                return new(NetworkClientStartupMode.SilentReady, "Saved LAN session restored.");
            }
            catch (NetworkApiException exception) when (exception.StatusCode is 401 or 403)
            {
                NetworkClientCredentialStore.DeleteSession();
                NetworkClientRuntime.ClearSession();
                NetworkClientStartupLog.Write("Saved LAN session was rejected; falling back to saved password or password entry.");
            }
            catch (Exception exception)
            {
                NetworkClientStartupLog.WriteException("Saved LAN session startup", exception);
                return new(NetworkClientStartupMode.PasswordOnly, $"Saved sign-in remains stored, but the latest Main Server data could not be refreshed. {Friendly(exception)}");
            }
        }

        var login = NetworkClientCredentialStore.LoadLogin();
        if (!string.IsNullOrWhiteSpace(login.User) && !string.IsNullOrWhiteSpace(login.Password))
        {
            try
            {
                Report(reportStatus, "Authenticating saved User ID/password...");
                var identity = await WithTimeoutAsync(api.TrustedLoginAsync(login.User, login.Password), AuthenticationTimeout, "Saved LAN sign-in");
                ApplyIdentity(api, identity);
                Report(reportStatus, "Refreshing authorised read-only data. A large first sync can take a few minutes...");
                await WithTimeoutAsync(api.DownloadReadOnlySnapshotAsync(), SnapshotTimeout, "Authorised LAN data refresh");
                Report(reportStatus, "Saved sign-in authenticated. Opening workspace...");
                return new(NetworkClientStartupMode.SilentReady, "Saved User ID/password authenticated silently.");
            }
            catch (NetworkApiException exception) when (exception.RequiresReEnrollment)
            {
                ResetStaleLocalRegistration();
                return new(NetworkClientStartupMode.Enrollment, "The Main Server no longer accepts this PC registration. Enter a new enrollment code, then complete one Registration OTP.");
            }
            catch (NetworkApiException exception) when (exception.StatusCode == 403 && exception.Code.Equals("REGISTRATION_OTP_REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                return new(NetworkClientStartupMode.RegistrationOtp, "Registration trust requires one-time verification.");
            }
            catch (NetworkApiException exception) when (exception.StatusCode is 401 or 403)
            {
                return new(NetworkClientStartupMode.PasswordOnly, "Saved sign-in could not be authenticated. Enter the AAH User ID/password again.");
            }
            catch (Exception exception)
            {
                NetworkClientStartupLog.WriteException("Saved password startup", exception);
                return new(NetworkClientStartupMode.PasswordOnly, $"Saved sign-in could not complete because the Main Server is unavailable. {Friendly(exception)}");
            }
        }

        return new(NetworkClientStartupMode.PasswordOnly, "Enter the AAH User ID/password for this already registered PC.");
    }

    private static void ResetStaleLocalRegistration()
    {
        NetworkClientCredentialStore.DeleteDevice();
        NetworkClientCredentialStore.DeleteSession();
        NetworkClientRuntime.ClearSession();
        NetworkClientRuntime.DeviceId=string.Empty;
        NetworkClientRuntime.DeviceToken=string.Empty;
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string stage)
    {
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"{stage} timed out after {FormatTimeout(timeout)}.", exception);
        }
    }

    private static async Task WithTimeoutAsync(Task task, TimeSpan timeout, string stage)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"{stage} timed out after {FormatTimeout(timeout)}.", exception);
        }
    }

    private static string FormatTimeout(TimeSpan timeout)
        => timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0} minute(s)" : $"{timeout.TotalSeconds:0} seconds";

    private static string Friendly(Exception exception)
        => exception is TimeoutException ? exception.Message : exception.Message.Trim();

    private static void ApplyIdentity(FullUiNetworkClientApi api, NetworkSessionIdentity identity)
    {
        NetworkClientRuntime.Server = api.Server;
        NetworkClientRuntime.DeviceId = api.DeviceId;
        NetworkClientRuntime.DeviceToken = api.DeviceToken;
        NetworkClientRuntime.SessionToken = identity.SessionToken;
        NetworkClientRuntime.ApplyIdentity(identity.UserId, identity.DisplayName, identity.RoleName, identity.ExpiresUtc, identity.Permissions);
        NetworkClientCredentialStore.SaveSession(new NetworkSavedSession(
            api.Server, api.DeviceId, NetworkClientRuntime.UserName, NetworkClientRuntime.DisplayName,
            NetworkClientRuntime.RoleName, identity.SessionToken, identity.ExpiresUtc, identity.Permissions));
    }

}
