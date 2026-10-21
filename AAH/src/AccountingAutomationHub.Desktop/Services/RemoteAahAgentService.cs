using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Constrained LAN remote-management agent. It accepts only the explicit RemoteAahActions allow-list
/// from the enrolled Main Server. It deliberately exposes no terminal, PowerShell, process launcher,
/// arbitrary file execution or unrestricted desktop-control endpoint.
/// </summary>
public sealed class RemoteAahAgentService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly System.Threading.Timer _streamTimer;
    private readonly System.Threading.Timer _remoteAssistAuthorityTimer;
    private int _running;
    private int _streamRunning;
    private int _remoteAssistAuthorityRunning;
    private long _lastRemoteInputSequence;
    private DateTime _lastStreamStatePushUtc;
    private string _lastRemoteFrameHash = string.Empty;
    private bool _disposed;
    private static DateTime _remoteAssistApprovedUntilUtc;
    private readonly ConcurrentDictionary<string, byte> _inFlightDeferredCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlightRemoteAssistSessions = new(StringComparer.OrdinalIgnoreCase);

    public RemoteAahAgentService()
    {
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        _streamTimer = new System.Threading.Timer(_ => _ = PollStreamAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(350));
        // Independent authority watcher: a Remote Assist request must be discoverable even if the
        // command wake/long-poll path is delayed, a command was already marked delivered, or the LAN
        // app restarted while a request/session remained active on the Main Server.
        _remoteAssistAuthorityTimer = new System.Threading.Timer(_ => _ = WatchRemoteAssistAuthorityAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public async Task PollNowAsync() => await PollAsync().ConfigureAwait(false);

    private async Task PollAsync()
    {
        if (_disposed || !NetworkClientRuntime.IsNetworkClient || string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken)) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            using var api = CreateApi();
            IReadOnlyList<RemoteAahCommandRecord> commands;
            try { commands = await api.GetRemoteCommandsAsync(waitSeconds:20).ConfigureAwait(false); }
            catch { return; }

            foreach (var command in commands)
            {
                // Consent may take several minutes. Keep it as a deferred command so Remote AAH
                // notifications, refresh/update and diagnostics are not blocked while the user
                // reviews the persistent Remote Assist request in the LAN workspace.
                if (command.Action.Equals(RemoteAahActions.RequestRemoteAssist, StringComparison.OrdinalIgnoreCase))
                {
                    var remoteAssistSessionId = ReadArgument(command.ArgumentsJson, "sessionId", string.Empty);
                    if (_inFlightDeferredCommands.TryAdd(command.CommandId, 0))
                    {
                        if (string.IsNullOrWhiteSpace(remoteAssistSessionId) || _inFlightRemoteAssistSessions.TryAdd(remoteAssistSessionId, 0))
                            _ = CompleteDeferredCommandAsync(command, remoteAssistSessionId);
                        else
                            _inFlightDeferredCommands.TryRemove(command.CommandId, out _);
                    }
                    continue;
                }

                var success = false;
                string detail;
                try
                {
                    if (!RemoteAahActions.Allowed.Contains(command.Action))
                        throw new InvalidOperationException("The Main Server requested a Remote AAH action that is not in this application build's allow-list.");
                    detail = await ExecuteAsync(api, command).ConfigureAwait(false);
                    success = true;
                }
                catch (Exception exception)
                {
                    detail = $"{exception.GetType().Name}: {exception.Message}";
                }

                try { await api.CompleteRemoteCommandAsync(command.CommandId, success, detail).ConfigureAwait(false); }
                catch { /* Delivered commands remain recoverable on the Main Server and are retried after reconnect/restart. */ }

                if (command.Action.Equals(RemoteAahActions.SignOut, StringComparison.OrdinalIgnoreCase) && success)
                    break;
            }

            // The Main Server is the authority for Remote Assist lifecycle.  This lightweight check
            // closes stale pending/active UI even if an End command races a deferred consent request
            // or a command completion is delayed on the network.
            try { await ReconcileRemoteAssistAuthorityAsync().ConfigureAwait(false); } catch { }
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private async Task WatchRemoteAssistAuthorityAsync()
    {
        if (_disposed || !NetworkClientRuntime.IsNetworkClient || string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken)) return;
        if (Interlocked.Exchange(ref _remoteAssistAuthorityRunning, 1) == 1) return;
        try
        {
            using var api = CreateApi();
            RemoteAssistSessionRecord? authority;
            try { authority = await api.GetCurrentRemoteAssistSessionAsync().ConfigureAwait(false); }
            catch { return; }

            if (authority is null || !authority.IsActive || authority.ExpiresUtc <= DateTime.UtcNow)
            {
                var localSessionId = RemoteAssistPrivilegeContext.SessionId;
                if (string.IsNullOrWhiteSpace(localSessionId))
                    localSessionId = RemoteAssistConsentCenter.Active?.SessionId ?? RemoteAssistConsentCenter.Pending?.SessionId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(localSessionId))
                    try { await ReconcileRemoteAssistAuthorityAsync().ConfigureAwait(false); } catch { }
                return;
            }

            var alreadyActive = RemoteAssistPrivilegeContext.IsActive && RemoteAssistPrivilegeContext.SessionId.Equals(authority.SessionId, StringComparison.OrdinalIgnoreCase);
            if (alreadyActive || RemoteAssistConsentCenter.IsKnownSession(authority.SessionId)) return;
            if (!_inFlightRemoteAssistSessions.TryAdd(authority.SessionId, 0)) return;

            NetworkClientStartupLog.Write($"Remote Assist authority discovered live. Session={authority.SessionId}; Helper={authority.HelperUserName}; Consented={authority.UserConsented}; Expires={authority.ExpiresUtc:O}.");
            _ = CompleteAuthorityRemoteAssistAsync(authority);
        }
        finally { Interlocked.Exchange(ref _remoteAssistAuthorityRunning, 0); }
    }

    private async Task CompleteAuthorityRemoteAssistAsync(RemoteAssistSessionRecord authority)
    {
        try
        {
            await HandleRemoteAssistSessionAsync(authority, authority.UserConsented, authority.HelperUserName).ConfigureAwait(false);
        }
        catch
        {
            // The 1-second authority watcher will retry while the server session remains active.
        }
        finally
        {
            _inFlightRemoteAssistSessions.TryRemove(authority.SessionId, out _);
        }
    }

    private static async Task ReconcileRemoteAssistAuthorityAsync()
    {
        var pending = RemoteAssistConsentCenter.Pending;
        var active = RemoteAssistConsentCenter.Active;
        var sessionId = active?.SessionId ?? pending?.SessionId ?? RemoteAssistPrivilegeContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        using var api = CreateApi();
        var authority = await api.GetRemoteAssistSessionStatusAsync(sessionId).ConfigureAwait(false);
        if (authority.Active && (!authority.ExpiresUtc.HasValue || authority.ExpiresUtc.Value > DateTime.UtcNow)) return;

        if (RemoteAssistPrivilegeContext.ValidateCommand(sessionId)) RemoteAssistPrivilegeContext.End();
        RemoteAssistConsentCenter.EndSession(sessionId);
        if (RemoteAssistLocalStreamState.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) RemoteAssistLocalStreamState.End();
        _remoteAssistApprovedUntilUtc = default;
        await OnUiAsync(_ => RefreshRemoteAssistBanner()).ConfigureAwait(false);
    }

    private async Task PollStreamAsync()
    {
        if(_disposed||!NetworkClientRuntime.IsNetworkClient||string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken))return;
        var sessionId=RemoteAssistPrivilegeContext.SessionId;
        if(string.IsNullOrWhiteSpace(sessionId)||!RemoteAssistPrivilegeContext.IsActive)
        {
            if(!string.IsNullOrWhiteSpace(RemoteAssistLocalStreamState.SessionId))RemoteAssistLocalStreamState.End();
            return;
        }
        if(Interlocked.Exchange(ref _streamRunning,1)==1)return;
        try
        {
            if(!RemoteAssistLocalStreamState.SessionId.Equals(sessionId,StringComparison.OrdinalIgnoreCase))
            {
                RemoteAssistLocalStreamState.Begin(sessionId);_lastRemoteInputSequence=0;_lastStreamStatePushUtc=default;_lastRemoteFrameHash=string.Empty;
            }

            var preferredEndpoint=NetworkClientRuntime.ResolveRemoteAssistEndpoint();
            var primaryEndpoint=NetworkClientRuntime.ResolveServerEndpoint();
            try
            {
                using var api=CreateApi(remoteAssist:true);
                await PollRemoteAssistStreamViaApiAsync(api,sessionId).ConfigureAwait(false);
            }
            catch when(!preferredEndpoint.Equals(primaryEndpoint,StringComparison.OrdinalIgnoreCase))
            {
                // Visual/input traffic prefers the private Remote Assist route, but it must remain
                // usable when that overlay endpoint changes or temporarily drops. Fall back to the
                // already-authenticated Main Server route that carries the live control plane.
                using var fallbackApi=CreateApi();
                await PollRemoteAssistStreamViaApiAsync(fallbackApi,sessionId).ConfigureAwait(false);
            }
        }
        catch
        {
            // Screen sharing is best-effort and must never terminate the LAN accounting process.
        }
        finally{Interlocked.Exchange(ref _streamRunning,0);}
    }

    private async Task PollRemoteAssistStreamViaApiAsync(FullUiNetworkClientApi api,string sessionId)
    {
        if(DateTime.UtcNow-_lastStreamStatePushUtc>TimeSpan.FromSeconds(3))
        {
            // Share/control state changes are small and need not consume one HTTPS request per video frame.
            // The Main Server returns the complete two-way state when this periodic state sync runs.
            var pushed=await api.SetRemoteAssistRemoteStateAsync(sessionId,RemoteAssistLocalStreamState.ShareMyAah,RemoteAssistLocalStreamState.AllowAdministratorControl).ConfigureAwait(false);
            RemoteAssistLocalStreamState.ApplyServerState(pushed);_lastStreamStatePushUtc=DateTime.UtcNow;
        }

        if(RemoteAssistLocalStreamState.ShareMyAah)
        {
            var frame=await RemoteAssistWindowService.CaptureMainWindowAsync(sessionId,RemoteAssistStreamDirections.RemoteToAdministrator).ConfigureAwait(false);
            if(frame is not null)
            {
                var hash = Convert.ToHexString(SHA256.HashData(frame.JpegBytes));
                if(!hash.Equals(_lastRemoteFrameHash,StringComparison.OrdinalIgnoreCase))
                {
                    await api.UploadRemoteAssistFrameAsync(sessionId,frame).ConfigureAwait(false);
                    _lastRemoteFrameHash = hash;
                    RemoteAssistLocalStreamState.MarkRemoteFrameSent(frame.CapturedUtc);
                }
            }
        }

        if(RemoteAssistLocalStreamState.AllowAdministratorControl)
        {
            var inputs=await api.GetRemoteAssistRemoteInputsAsync(sessionId,_lastRemoteInputSequence).ConfigureAwait(false);
            foreach(var input in inputs.OrderBy(x=>x.Sequence))
            {
                await RemoteAssistWindowService.ApplyInputToMainWindowAsync(input).ConfigureAwait(false);
                _lastRemoteInputSequence=Math.Max(_lastRemoteInputSequence,input.Sequence);
            }
        }

        if(RemoteAssistLocalStreamState.AdministratorSharing)
        {
            var adminFrame=await api.GetAdministratorRemoteAssistFrameAsync(sessionId,RemoteAssistLocalStreamState.AdministratorFrameSequence).ConfigureAwait(false);
            if(adminFrame is not null)RemoteAssistLocalStreamState.ApplyAdministratorFrame(adminFrame);
        }
    }

    private async Task CompleteDeferredCommandAsync(RemoteAahCommandRecord command, string remoteAssistSessionId = "")
    {
        var success = false;
        string detail;
        try
        {
            if (!RemoteAahActions.Allowed.Contains(command.Action))
                throw new InvalidOperationException("The Main Server requested a Remote AAH action that is not in this application build's allow-list.");
            using var api = CreateApi();
            detail = await ExecuteAsync(api, command).ConfigureAwait(false);
            success = true;
            try { await api.CompleteRemoteCommandAsync(command.CommandId, success, detail).ConfigureAwait(false); } catch { }
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            try { using var api = CreateApi(); await api.CompleteRemoteCommandAsync(command.CommandId, false, detail).ConfigureAwait(false); } catch { }
        }
        finally
        {
            _inFlightDeferredCommands.TryRemove(command.CommandId, out _);
            if (!string.IsNullOrWhiteSpace(remoteAssistSessionId)) _inFlightRemoteAssistSessions.TryRemove(remoteAssistSessionId, out _);
        }
    }

    private static FullUiNetworkClientApi CreateApi(bool remoteAssist = false) => new()
    {
        Server = remoteAssist ? NetworkClientRuntime.ResolveRemoteAssistEndpoint() : NetworkClientRuntime.ResolveServerEndpoint(),
        DeviceId = NetworkClientRuntime.DeviceId,
        DeviceToken = NetworkClientRuntime.DeviceToken,
        SessionToken = NetworkClientRuntime.SessionToken
    };

    private static async Task<string> HandleRemoteAssistSessionAsync(RemoteAssistSessionRecord authority, bool autoAccept, string requestedBy)
    {
        var sessionId=authority.SessionId?.Trim()??string.Empty;
        if(sessionId.Length==0)throw new InvalidOperationException("Remote Assist request is missing its Main Server session identity.");
        if(!authority.IsActive||authority.ExpiresUtc<=DateTime.UtcNow)
        {
            RemoteAssistConsentCenter.EndSession(sessionId);
            return $"Remote Assist request {sessionId} is no longer active.";
        }
        // Never trust a durable command by itself as proof that the request is still live. A direct
        // decline/end may have closed the authoritative session while the original command remains
        // in Delivered state waiting to be completed. Verify server authority before asking again.
        using(var preflightApi=CreateApi())
        {
            var preflight=await preflightApi.GetRemoteAssistSessionStatusAsync(sessionId).ConfigureAwait(false);
            if(!preflight.Active||(preflight.ExpiresUtc.HasValue&&preflight.ExpiresUtc.Value<=DateTime.UtcNow))
            {
                RemoteAssistConsentCenter.EndSession(sessionId);
                return $"Remote Assist request {sessionId} is no longer active on the Main Server.";
            }
            if(preflight.UserConsented)autoAccept=true;
        }
        if(RemoteAssistPrivilegeContext.IsActive&&RemoteAssistPrivilegeContext.SessionId.Equals(sessionId,StringComparison.OrdinalIgnoreCase))
            return $"Remote Assist session {sessionId} is already active on this PC.";

        var helperUser=string.IsNullOrWhiteSpace(authority.HelperUserName)?requestedBy:authority.HelperUserName;
        var remoteUser=string.IsNullOrWhiteSpace(authority.RemoteUserName)?NetworkClientRuntime.UserName:authority.RemoteUserName;
        var mode=string.IsNullOrWhiteSpace(authority.PermissionMode)?RemoteAssistPrivilegeModes.RemoteUser:authority.PermissionMode;
        var scope=string.IsNullOrWhiteSpace(authority.Scope)?"AAH Only":authority.Scope;
        var reason=authority.Reason??string.Empty;
        var expiresUtc=authority.ExpiresUtc.ToUniversalTime();
        var helperPermissions=ParseGrantedPermissions(authority.GrantedPermissionsJson);
        var consentRequest=new RemoteAssistConsentRequest(sessionId,string.IsNullOrWhiteSpace(requestedBy)?helperUser:requestedBy,helperUser,remoteUser,mode,scope,reason,expiresUtc);

        bool accepted;
        if(autoAccept||authority.UserConsented||RemoteAssistConsentCenter.WasAccepted(sessionId))
        {
            accepted=true;
        }
        else
        {
            // Register the pending state BEFORE bringing the app forward so the first frame the LAN
            // user sees is the real Accept/Decline request, never a stale "No session" surface.
            var consentTask=RemoteAssistConsentCenter.RegisterAsync(consentRequest);
            if(!RemoteAssistConsentCenter.IsKnownSession(sessionId)&&consentTask.IsCompleted)
            {
                // This exact session was already ended/declined locally. Do not resurrect a stale
                // Accept/Decline surface just because a delayed authority read still sees it.
                accepted=await consentTask.ConfigureAwait(false);
            }
            else
            {
                NetworkClientStartupLog.Write($"Remote Assist consent registered before activation. Session={sessionId}; Helper={helperUser}; Scope={scope}.");
                await OnUiAsync(vm=>
                {
                    if(System.Windows.Application.Current is App app)
                    {
                        app.ShowMainWindow();
                        vm.NavigateTo("Remote Assist");
                        if(System.Windows.Application.Current.MainWindow is Window window)
                        {
                            window.Show();
                            if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;
                            window.Activate();
                            window.Topmost=true;
                            window.Topmost=false;
                            window.Focus();
                        }
                        RefreshRemoteAssistBanner();
                        app.ShowCollaborationNotification("AAH Remote Assist request",$"{helperUser} requests {scope} Remote Assist. Choose Accept or Decline in the Remote Assist page.",urgent:true,module:"Remote Assist");
                    }
                }).ConfigureAwait(false);
                accepted=await consentTask.ConfigureAwait(false);
            }
        }

        using(var consentApi=CreateApi())
        {
            try{await consentApi.SetRemoteAssistConsentAsync(sessionId,accepted).ConfigureAwait(false);}
            catch when(!accepted){/* local decline still clears immediately; server command completion/authority expiry remains a second cleanup path */}
        }

        if(!accepted)
        {
            RemoteAssistConsentCenter.EndSession(sessionId);
            NetworkClientStartupLog.Write($"Remote Assist declined/expired locally. Session={sessionId}; Helper={helperUser}.");
            NotificationHistoryService.Record(string.Empty,"Remote Assist","Declined","AAH Remote Assist declined",$"Request {sessionId} from {helperUser} was declined or expired.");
            await OnUiAsync(_=>RefreshRemoteAssistBanner()).ConfigureAwait(false);
            return $"Remote Assist request {sessionId} was declined by the AAH user or expired.";
        }

        using(var authorityApi=CreateApi())
        {
            var state=await authorityApi.GetRemoteAssistSessionStatusAsync(sessionId).ConfigureAwait(false);
            if(!state.Active||!state.UserConsented||(state.ExpiresUtc.HasValue&&state.ExpiresUtc.Value<=DateTime.UtcNow))
            {
                RemoteAssistConsentCenter.EndSession(sessionId);
                return $"Remote Assist session {sessionId} ended on the Main Server before local activation.";
            }
        }
        if(!RemoteAssistConsentCenter.MarkActive(consentRequest))
            return $"Remote Assist session {sessionId} was ended before local activation.";
        _remoteAssistApprovedUntilUtc=expiresUtc;
        RemoteAssistPrivilegeContext.Begin(sessionId,helperUser,remoteUser,mode,helperPermissions,scope,reason,expiresUtc);
        RemoteAssistLocalStreamState.Begin(sessionId);
        NetworkClientStartupLog.Write($"Remote Assist activated locally. Session={sessionId}; Helper={helperUser}; AutoAccept={autoAccept}; Expires={expiresUtc:O}.");
        await OnUiAsync(vm=>
        {
            vm.NavigateTo("Remote Assist");
            RefreshRemoteAssistBanner();
            if(System.Windows.Application.Current is App app)
                app.ShowCollaborationNotification(autoAccept?"Administrator Remote Assist connected":"AAH Remote Assist active",autoAccept?$"{helperUser} connected using the Administrator Auto Accept policy. You can end access at any time.":$"{helperUser} is connected to your AAH window. You can pause sharing or end access at any time.",urgent:true,module:"Remote Assist");
        }).ConfigureAwait(false);
        NotificationHistoryService.Record(string.Empty,"Remote Assist",autoAccept?"AutoAccept":"Consent",autoAccept?"Administrator Remote Assist auto-accepted":"AAH Remote Assist approved",$"Helper {helperUser}; mode {mode}; scope {scope}; until {expiresUtc.ToLocalTime():g}. Only the AAH window is shared. The AAH user can end the session at any time.");
        return $"Remote Assist session {sessionId} {(autoAccept?"auto-accepted by Main Server Administrator policy":"approved by AAH user")} until {expiresUtc:O}; mode={mode}.";
    }

    private static async Task<string> ExecuteAsync(FullUiNetworkClientApi api, RemoteAahCommandRecord command)
    {
        if (command.Action.Equals(RemoteAahActions.RefreshSnapshot, StringComparison.OrdinalIgnoreCase))
        {
            await api.DownloadReadOnlySnapshotAsync().ConfigureAwait(false);
            await OnUiAsync(vm => vm.ApplyNetworkLiveSnapshotRefresh()).ConfigureAwait(false);
            return "Authorised Main Server snapshot refreshed and activated.";
        }
        if (command.Action.Equals(RemoteAahActions.ReloadPermissions, StringComparison.OrdinalIgnoreCase))
        {
            var status = await api.GetEnterpriseStatusAsync().ConfigureAwait(false);
            NetworkClientRuntime.ApplyIdentity(status.UserId, status.DisplayName, status.RoleName, NetworkClientRuntime.SessionExpiresUtc, status.Permissions);
            NetworkLocalIdentityService.ApplyToDisposableSnapshot();
            await OnUiAsync(vm => vm.ApplyNetworkIdentityRefresh()).ConfigureAwait(false);
            return $"Permissions reloaded. Authorization revision {status.Revisions.AuthorizationRevision}.";
        }
        if (command.Action.Equals(RemoteAahActions.ClearDisposableSnapshot, StringComparison.OrdinalIgnoreCase))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var db = ApplicationDataPaths.DatabasePath;
            TryDelete(db + "-wal"); TryDelete(db + "-shm"); TryDelete(db);
            return "Disposable LAN snapshot cleared. A fresh Main Server snapshot is required.";
        }
        if (command.Action.Equals(RemoteAahActions.TestMainServer, StringComparison.OrdinalIgnoreCase))
        {
            var status = await api.GetEnterpriseStatusAsync().ConfigureAwait(false);
            return $"Main Server reachable. Server {api.Server}; role {status.RoleName}; device health {status.Posture.HealthState}; risk {status.Posture.RiskScore}.";
        }
        if (command.Action.Equals(RemoteAahActions.CheckUpdate, StringComparison.OrdinalIgnoreCase))
        {
            var update = await api.GetUpdateStatusAsync().ConfigureAwait(false);
            return $"Installed {api.AppVersion}; approved {update.ApprovedVersion}; updateRequired={update.UpdateRequired}; forced={update.ForceUpdate}.";
        }
        if (command.Action.Equals(RemoteAahActions.InstallUpdate, StringComparison.OrdinalIgnoreCase))
        {
            var update = await api.GetUpdateStatusAsync().ConfigureAwait(false);
            if (!update.UpdateRequired) return "No AAH LAN update is required.";
            var scheduled = await NetworkClientSelfUpdateService.DownloadVerifyAndScheduleAsync(api, owner: null, forced: update.ForceUpdate).ConfigureAwait(false);
            if (scheduled)
            {
                _ = ExitAfterDelayAsync(TimeSpan.FromSeconds(2));
                return $"Verified AAH LAN update {update.ApprovedVersion} scheduled.";
            }
            return "Update was not scheduled.";
        }
        if (command.Action.Equals(RemoteAahActions.CollectDiagnostics, StringComparison.OrdinalIgnoreCase))
            return BuildDiagnosticsBundle();
        if (command.Action.Equals(RemoteAahActions.RequestRemoteAssist, StringComparison.OrdinalIgnoreCase))
        {
            var requestedBy = string.IsNullOrWhiteSpace(command.RequestedBy) ? "Main Server Administrator" : command.RequestedBy;
            var sessionId=ReadArgument(command.ArgumentsJson,"sessionId",string.Empty);
            if(string.IsNullOrWhiteSpace(sessionId))throw new InvalidOperationException("Remote Assist request is missing its Main Server session identity.");
            var expiresText=ReadArgument(command.ArgumentsJson,"expiresUtc",DateTime.UtcNow.AddMinutes(60).ToString("O"));
            var authority=new RemoteAssistSessionRecord
            {
                SessionId=sessionId,
                DeviceId=NetworkClientRuntime.DeviceId,
                RemoteUserName=ReadArgument(command.ArgumentsJson,"remoteUser",NetworkClientRuntime.UserName),
                HelperUserName=ReadArgument(command.ArgumentsJson,"helperUser",requestedBy),
                PermissionMode=ReadArgument(command.ArgumentsJson,"permissionMode",RemoteAssistPrivilegeModes.RemoteUser),
                GrantedPermissionsJson=JsonSerializer.Serialize(ReadStringArrayArgument(command.ArgumentsJson,"helperPermissions")),
                Scope=ReadArgument(command.ArgumentsJson,"scope","AAH Only"),
                Reason=ReadArgument(command.ArgumentsJson,"reason",string.Empty),
                UserConsented=ReadBoolArgument(command.ArgumentsJson,"autoAccept"),
                IsActive=true,
                ExpiresUtc=DateTime.TryParse(expiresText,out var parsedExpiry)?parsedExpiry.ToUniversalTime():DateTime.UtcNow.AddMinutes(60)
            };
            return await HandleRemoteAssistSessionAsync(authority, authority.UserConsented, requestedBy).ConfigureAwait(false);
        }
        if (command.Action.Equals(RemoteAahActions.RemoteAssistNavigate, StringComparison.OrdinalIgnoreCase))
        {
            var sessionId=ReadArgument(command.ArgumentsJson,"sessionId",string.Empty);
            if (_remoteAssistApprovedUntilUtc <= DateTime.UtcNow || !RemoteAssistPrivilegeContext.ValidateCommand(sessionId))
                throw new InvalidOperationException("Remote Assist has not been approved, the session id does not match, or the consent window expired.");
            var page = ReadArgument(command.ArgumentsJson, "page", string.Empty);
            if (string.IsNullOrWhiteSpace(page)) throw new InvalidOperationException("Remote Assist Navigate requires a page argument.");
            var result = string.Empty;
            var ok = false;
            await OnUiAsync(vm => { ok = vm.TryRemoteAssistNavigate(page, out result); }).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException(result);
            return result;
        }
        if(command.Action.Equals(RemoteAahActions.RemoteAssistSetLocalPreference,StringComparison.OrdinalIgnoreCase))
        {
            var sessionId=ReadArgument(command.ArgumentsJson,"sessionId",string.Empty);
            if(!RemoteAssistPrivilegeContext.ValidateCommand(sessionId))throw new InvalidOperationException("Remote Assist session is not active on this PC.");
            var perAction=ReadBoolArgument(command.ArgumentsJson,"useHelperPermission");
            if(!RemoteAssistPrivilegeContext.CanUseHelperPermission(GovernancePermissions.RemoteAssist,perAction))throw new InvalidOperationException("This Remote Assist session is using the remote user's permissions. Ask for per-action or temporary helper elevation first.");
            var key=ReadArgument(command.ArgumentsJson,"key",string.Empty);var value=ReadArgument(command.ArgumentsJson,"value",string.Empty);
            NetworkLocalPreferenceService.SetRemoteAssistPreference(key,value);
            await OnUiAsync(vm=>vm.ApplyNetworkIdentityRefresh()).ConfigureAwait(false);
            return $"Remote Assist changed allow-listed LAN local preference '{key}'.";
        }
        if(command.Action.Equals(RemoteAahActions.RemoteAssistEnd,StringComparison.OrdinalIgnoreCase))
        {
            var sessionId=ReadArgument(command.ArgumentsJson,"sessionId",string.Empty);
            if(string.IsNullOrWhiteSpace(sessionId))return "Remote Assist end was already satisfied; no session id remained on this PC.";
            var activeMatches=RemoteAssistPrivilegeContext.ValidateCommand(sessionId);
            var pendingOrActiveMatches=RemoteAssistConsentCenter.IsKnownSession(sessionId);
            var streamMatches=RemoteAssistLocalStreamState.SessionId.Equals(sessionId,StringComparison.OrdinalIgnoreCase);
            if(activeMatches)RemoteAssistPrivilegeContext.End();
            RemoteAssistConsentCenter.EndSession(sessionId);
            if(streamMatches)RemoteAssistLocalStreamState.End();
            _remoteAssistApprovedUntilUtc=default;
            await OnUiAsync(_=>RefreshRemoteAssistBanner()).ConfigureAwait(false);
            NotificationHistoryService.Record(string.Empty,"Remote Assist","Ended","AAH Remote Assist ended",pendingOrActiveMatches||activeMatches||streamMatches?"Remote Assist request/session state was cleared immediately on this PC.":"A delayed idempotent End command confirmed that this Remote Assist session was already cleared on this PC.");
            return "Remote Assist ended locally; pending consent, visual sharing, control and helper privileges are cleared for the matching session.";
        }
        if (command.Action.Equals(RemoteAahActions.ShowMessage, StringComparison.OrdinalIgnoreCase))
        {
            var text = ReadArgument(command.ArgumentsJson, "message", "Main Server sent an AAH administration notice.");
            var urgent = ReadBoolArgument(command.ArgumentsJson, "urgent");
            await OnUiAsync(_ => { if (System.Windows.Application.Current is App app) app.ShowCollaborationNotification("AAH Main Server", text, urgent, "Administration"); }).ConfigureAwait(false);
            return "Administration notice delivered to AAH Notification History and the user notification surface.";
        }
        if (command.Action.Equals(RemoteAahActions.ForceOpenAah, StringComparison.OrdinalIgnoreCase))
        {
            await OnUiAsync(_ =>
            {
                if (System.Windows.Application.Current is App app) app.ShowMainWindow();
            }).ConfigureAwait(false);
            return "AAH LAN main window was brought to the foreground by the Main Server Administrator.";
        }
        if (command.Action.Equals(RemoteAahActions.RestartAah, StringComparison.OrdinalIgnoreCase))
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AccountingAutomationHub.exe");
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "--network-client", UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
            _ = ExitAfterDelayAsync(TimeSpan.FromSeconds(2));
            return "AAH restart requested.";
        }
        if (command.Action.Equals(RemoteAahActions.SignOut, StringComparison.OrdinalIgnoreCase))
        {
            try { await api.LogoutAsync().ConfigureAwait(false); } catch { }
            NetworkClientCredentialStore.DeleteSession();
            NetworkClientCredentialStore.DeleteLogin();
            NetworkClientRuntime.ClearSession();
            _ = ExitAfterDelayAsync(TimeSpan.FromSeconds(2));
            return "User session signed out by Main Server Administrator. Device registration trust was preserved.";
        }
        throw new InvalidOperationException("Unsupported Remote AAH action.");
    }

    private static string BuildDiagnosticsBundle()
    {
        var root = Path.Combine(NetworkClientRuntime.RootFolder, "Diagnostics");
        Directory.CreateDirectory(root);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stage = Path.Combine(root, "AAH-Diagnostic-" + stamp);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "runtime.txt"),
            $"AAH LAN Diagnostic\r\nCreated: {DateTime.Now:O}\r\nVersion: {typeof(RemoteAahAgentService).Assembly.GetName().Version}\r\nServer: {NetworkClientRuntime.ResolveServerEndpoint()}\r\nDevice: {NetworkClientRuntime.DeviceId}\r\nUser: {NetworkClientRuntime.UserName}\r\nRole: {NetworkClientRuntime.RoleName}\r\nSession expires UTC: {NetworkClientRuntime.SessionExpiresUtc:O}\r\nDatabase exists: {File.Exists(ApplicationDataPaths.DatabasePath)}\r\nDatabase size: {(File.Exists(ApplicationDataPaths.DatabasePath) ? new FileInfo(ApplicationDataPaths.DatabasePath).Length : 0)}\r\n");

        var logs = Path.Combine(NetworkClientRuntime.RootFolder, "Logs");
        if (Directory.Exists(logs))
        {
            var target = Path.Combine(stage, "Logs"); Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(logs).OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
            {
                try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true); } catch { }
            }
        }
        var cleanupCandidates = new[]
        {
            Path.Combine(NetworkClientInstallManager.CanonicalInstallPath, "AAH-LAN-CLEANUP.log"),
            Path.Combine(NetworkClientInstallManager.LegacyMachineInstallPath, "AAH-LAN-CLEANUP.log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Accounting Automation Hub LAN", "AAH-LAN-CLEANUP.log")
        };
        var cleanup = cleanupCandidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(cleanup)) try { File.Copy(cleanup, Path.Combine(stage, "AAH-LAN-CLEANUP.log"), true); } catch { }
        var lanDiagRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Accounting Automation Hub LAN");
        foreach (var name in new[] { "AAH-LAN-STARTUP.log", "AAH-LAN-RECOVERY.log", "AAH-LAN-UPDATE.log", "AAH-LAN-DIAGNOSTICS.txt" })
        {
            var source = Path.Combine(lanDiagRoot, name);
            if (File.Exists(source)) try { File.Copy(source, Path.Combine(stage, name), true); } catch { }
        }
        var zip = stage + ".zip"; if (File.Exists(zip)) File.Delete(zip); ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Fastest, includeBaseDirectory: false); Directory.Delete(stage, true);
        return $"Diagnostics bundle created locally: {zip}. Accounting database contents, passwords, device token and session token were not included.";
    }


    private static async Task ExitAfterDelayAsync(TimeSpan delay)
    {
        try { await Task.Delay(delay).ConfigureAwait(false); await OnUiAsync(_ => ((App)System.Windows.Application.Current).ExitApplication()).ConfigureAwait(false); }
        catch { }
    }

    private static IReadOnlyList<string> ParseGrantedPermissions(string? json)
    {
        try{return JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(json)?"[]":json)??Array.Empty<string>();}
        catch{return Array.Empty<string>();}
    }

    private static IReadOnlyList<string> ReadStringArrayArgument(string json,string name)
    {
        try{using var doc=JsonDocument.Parse(string.IsNullOrWhiteSpace(json)?"{}":json);if(!doc.RootElement.TryGetProperty(name,out var p)||p.ValueKind!=JsonValueKind.Array)return Array.Empty<string>();return p.EnumerateArray().Select(x=>x.GetString()??string.Empty).Where(x=>x.Length>0).ToArray();}catch{return Array.Empty<string>();}
    }
    private static bool ReadBoolArgument(string json,string name)
    {
        try{using var doc=JsonDocument.Parse(string.IsNullOrWhiteSpace(json)?"{}":json);return doc.RootElement.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.True;}catch{return false;}
    }
    private static void RefreshRemoteAssistBanner()
    {
        if(System.Windows.Application.Current?.MainWindow is MainWindow window)window.RefreshRemoteAssistBanner();
    }

    private static string ReadArgument(string json, string name, string fallback)
    {
        try { using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); return doc.RootElement.TryGetProperty(name, out var p) ? p.GetString() ?? fallback : fallback; }
        catch { return fallback; }
    }

    private static Task OnUiAsync(Action<MainWindowViewModel> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.CompletedTask;
        return dispatcher.InvokeAsync(() =>
        {
            if (System.Windows.Application.Current?.MainWindow?.DataContext is MainWindowViewModel vm) action(vm);
        }).Task;
    }

    private static void TryDelete(string path){try{if(File.Exists(path)){var a=File.GetAttributes(path);if((a&FileAttributes.ReadOnly)!=0)File.SetAttributes(path,a&~FileAttributes.ReadOnly);File.Delete(path);}}catch{}}
    public void Dispose(){if(_disposed)return;_disposed=true;_timer.Dispose();_streamTimer.Dispose();_remoteAssistAuthorityTimer.Dispose();RemoteAssistLocalStreamState.End();}
}
