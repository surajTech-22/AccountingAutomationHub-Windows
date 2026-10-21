using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class FullUiNetworkClientApi : IDisposable
{
    private readonly HttpClient _http;

    public FullUiNetworkClientApi()
    {
        _http = CreatePinnedHttpClient();
    }
    public string Server { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceToken { get; set; } = string.Empty;
    public string SessionToken { get; set; } = string.Empty;
    public string AppVersion => typeof(FullUiNetworkClientApi).Assembly.GetName().Version?.ToString(4) ?? "4.0.0.410";

    public async Task EnrollAsync(string code,string user,string password,string deviceName)
    {
        using var response = await _http.PostAsJsonAsync(Url("/api/v1/enroll"), new { enrollmentCode=code,userName=user,password,totpCode="",deviceName,deviceType="Windows",appVersion=AppVersion });
        await EnsureAsync(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        DeviceId=json.RootElement.GetProperty("deviceId").GetString()??string.Empty;
        DeviceToken=json.RootElement.GetProperty("deviceToken").GetString()??string.Empty;
        NetworkClientCredentialStore.SaveDevice(user,Server,DeviceId,DeviceToken);
    }

    public async Task<(string ChallengeId,string Message,string OtpType,string NetworkScope)> RequestOtpAsync(string user,string password)
    {
        using var response=await _http.PostAsJsonAsync(Url("/api/v1/auth/challenge"),new{deviceId=DeviceId,deviceToken=DeviceToken,userName=user,password,appVersion=AppVersion});
        await EnsureAsync(response);using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return(
            json.RootElement.GetProperty("challengeId").GetString()??string.Empty,
            json.RootElement.GetProperty("message").GetString()??"OTP sent to Main Server.",
            json.RootElement.TryGetProperty("otpType",out var otpType)?otpType.GetString()??"Registration OTP":"Registration OTP",
            json.RootElement.TryGetProperty("networkScope",out var scope)?scope.GetString()??string.Empty:string.Empty);
    }

    public async Task<NetworkSessionIdentity> LoginWithOtpAsync(string challengeId,string otp)
    {
        using var response=await _http.PostAsJsonAsync(Url("/api/v1/auth/login"),new{deviceId=DeviceId,deviceToken=DeviceToken,challengeId,otpCode=otp,appVersion=AppVersion});
        await EnsureAsync(response);using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        SessionToken=json.RootElement.GetProperty("sessionToken").GetString()??string.Empty;
        return ParseSessionIdentity(json.RootElement, SessionToken);
    }

    public async Task<NetworkSessionIdentity> TrustedLoginAsync(string user,string password)
    {
        using var response=await _http.PostAsJsonAsync(Url("/api/v1/auth/trusted-login"),new{deviceId=DeviceId,deviceToken=DeviceToken,userName=user,password,appVersion=AppVersion});
        await EnsureAsync(response);using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        SessionToken=json.RootElement.GetProperty("sessionToken").GetString()??string.Empty;
        return ParseSessionIdentity(json.RootElement,SessionToken);
    }

    public async Task<NetworkMobileUserCode> GetCurrentUserMobileCodeAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/api/v1/auth/mobile-code"));
        AddSession(request);
        using var response = await _http.SendAsync(request);
        await EnsureAsync(response);
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<NetworkMobileUserCode>(body, JsonOptions) ?? throw new InvalidDataException("Main Server returned an invalid mobile user code.");
    }

    public async Task<NetworkMobileUserCode> RotateCurrentUserMobileCodeAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/v1/auth/mobile-code/refresh"));
        AddSession(request);
        using var response = await _http.SendAsync(request);
        await EnsureAsync(response);
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<NetworkMobileUserCode>(body, JsonOptions) ?? throw new InvalidDataException("Main Server did not return a fresh mobile user code.");
    }

    public async Task<NetworkSessionIdentity> RestoreSessionAsync(string sessionToken)
    {
        SessionToken = sessionToken?.Trim() ?? string.Empty;
        if (SessionToken.Length == 0) throw new InvalidOperationException("No saved LAN session is available.");
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/api/v1/auth/session"));
        AddSession(request);
        using var response = await _http.SendAsync(request);
        await EnsureAsync(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseSessionIdentity(json.RootElement, SessionToken);
    }

    public async Task LogoutAsync()
    {
        if (string.IsNullOrWhiteSpace(SessionToken)) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/v1/auth/logout"));
        AddSession(request);
        try { using var response = await _http.SendAsync(request); } catch { }
        SessionToken = string.Empty;
    }

    private static NetworkSessionIdentity ParseSessionIdentity(JsonElement root, string token)
    {
        var permissions = root.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();
        var expiresUtc = root.TryGetProperty("expiresUtc", out var expires) && expires.TryGetDateTime(out var parsed) ? parsed : DateTime.UtcNow.AddDays(30);
        return new NetworkSessionIdentity(
            root.TryGetProperty("userId", out var userId) ? userId.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("user", out var user) ? user.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("role", out var role) ? role.GetString() ?? string.Empty : string.Empty,
            token,
            expiresUtc,
            permissions);
    }

    public async Task DownloadReadOnlySnapshotAsync()
    {
        // Completion 23: never replace the local processing-node snapshot while a
        // locally completed import is still waiting for authoritative Main Server replay.
        // The retained source is uploaded first; until the replay reports Completed,
        // this method deliberately leaves the current local accounting snapshot intact.
        if(!await CanSafelyAcceptAuthoritativeSnapshotAsync().ConfigureAwait(false)) return;

        using var request=new HttpRequestMessage(HttpMethod.Get,Url("/api/v1/full-ui/snapshot"));AddSession(request);
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);await EnsureAsync(response);
        Directory.CreateDirectory(ApplicationDataPaths.DataFolder);

        var databasePath = ApplicationDataPaths.DatabasePath;
        var localAutomationState = NetworkLocalAutomationStateService.Capture();
        var temp = databasePath + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            await using(var input=await response.Content.ReadAsStreamAsync())
            {
                await using var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous|FileOptions.WriteThrough);
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }

            if(new FileInfo(temp).Length<4096)
                throw new InvalidDataException("Main Server returned an invalid read-only data snapshot.");

            await ReplaceDisposableLocalSnapshotAsync(temp,databasePath);
            NetworkLocalIdentityService.ApplyToDisposableSnapshot();
            NetworkLocalAutomationStateService.Restore(localAutomationState);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static async Task ReplaceDisposableLocalSnapshotAsync(string downloadedPath,string databasePath)
    {
        Exception? last = null;
        var walPath = databasePath + "-wal";
        var shmPath = databasePath + "-shm";
        var backupPath = databasePath + ".previous";

        for(var attempt=1;attempt<=12;attempt++)
        {
            try
            {
                // Refresh happens while the LAN desktop is already open. Microsoft.Data.Sqlite
                // pools disposed connections by default, so the disposable local snapshot can
                // still have Windows handles even though repository methods have returned.
                // Clear every idle pool before touching the database or its WAL/SHM sidecars.
                SqliteConnection.ClearAllPools();

                if(attempt>1)
                    await Task.Delay(Math.Min(1200,attempt*150));

                ClearReadOnlyAttribute(databasePath);
                ClearReadOnlyAttribute(walPath);
                ClearReadOnlyAttribute(shmPath);
                ClearReadOnlyAttribute(backupPath);

                // The old local snapshot is disposable. Never let its WAL/SHM files be replayed
                // into a newly downloaded Main Server snapshot.
                DeleteIfExists(walPath);
                DeleteIfExists(shmPath);
                DeleteIfExists(backupPath);

                if(File.Exists(databasePath))
                {
                    File.Replace(downloadedPath,databasePath,backupPath,ignoreMetadataErrors:true);
                    TryDeleteFile(backupPath);
                }
                else
                {
                    File.Move(downloadedPath,databasePath);
                }

                SqliteConnection.ClearAllPools();
                TryDeleteFile(databasePath+"-wal");
                TryDeleteFile(databasePath+"-shm");
                return;
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
            {
                last=exception;
            }
        }

        throw new IOException(
            $"The Main Server snapshot was downloaded successfully, but AAH could not replace the disposable LAN database at '{databasePath}'. " +
            "AAH cleared SQLite connection pools and retried the safe database/WAL/SHM swap. Close any extra Accounting Automation Hub LAN windows and retry Refresh Server Data. " +
            $"Windows reported: {last?.Message}",
            last);
    }

    private static void DeleteIfExists(string path)
    {
        if(File.Exists(path)) File.Delete(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if(File.Exists(path))
            {
                ClearReadOnlyAttribute(path);
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only. A later refresh uses a unique download name.
        }
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        if(!File.Exists(path)) return;
        var attributes=File.GetAttributes(path);
        if((attributes&FileAttributes.ReadOnly)!=0)
            File.SetAttributes(path,attributes&~FileAttributes.ReadOnly);
    }

    public async Task<string> UploadAsync(long clientId,string path)
    {
        using var form=new MultipartFormDataContent();await using var stream=File.OpenRead(path);var content=new StreamContent(stream);form.Add(content,"file",Path.GetFileName(path));
        using var request=new HttpRequestMessage(HttpMethod.Post,Url($"/api/v1/client/{clientId}/upload")){Content=form};AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);return await response.Content.ReadAsStringAsync();
    }

    public async Task<NetworkUpdateStatus> GetUpdateStatusAsync(long clientId=0,string? financialYear=null,string? module=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Url("/api/v1/heartbeat"));
        if(clientId>0||!string.IsNullOrWhiteSpace(financialYear)||!string.IsNullOrWhiteSpace(module))request.Content=JsonContent.Create(new{clientId,financialYear=financialYear??string.Empty,module=module??string.Empty});
        // Once signed in, carry the authenticated AAH user on the heartbeat as well as the
        // enrolled device identity. This keeps Main Server presence/Remote Assist bound to the
        // user who is actually using AAH now, rather than permanently to the enrollment owner.
        if (!string.IsNullOrWhiteSpace(SessionToken)) AddSession(request); else AddDevice(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var root=json.RootElement;
        return new NetworkUpdateStatus(
            root.TryGetProperty("appVersion",out var current)?current.GetString()??AppVersion:AppVersion,
            root.TryGetProperty("approvedVersion",out var approved)?approved.GetString()??string.Empty:string.Empty,
            root.TryGetProperty("approvedSha256",out var sha)?sha.GetString()??string.Empty:string.Empty,
            root.TryGetProperty("updateRequired",out var update)&&update.GetBoolean(),
            root.TryGetProperty("forceUpdate",out var force)&&force.GetBoolean(),
            root.TryGetProperty("registrationOtpRequired",out var otpRequired)&&otpRequired.GetBoolean(),
            root.TryGetProperty("healthState",out var health)?health.GetString()??DeviceHealthStates.Healthy:DeviceHealthStates.Healthy,
            root.TryGetProperty("riskScore",out var risk)?risk.GetInt32():0,
            root.TryGetProperty("quarantined",out var quarantined)&&quarantined.GetBoolean(),
            root.TryGetProperty("quarantineReason",out var quarantineReason)?quarantineReason.GetString()??string.Empty:string.Empty,
            root.TryGetProperty("emergencyLockdown",out var lockdown)&&lockdown.GetBoolean(),
            root.TryGetProperty("lockdownReason",out var lockdownReason)?lockdownReason.GetString()??string.Empty:string.Empty,
            root.TryGetProperty("authorizationRevision",out var authRev)?authRev.GetInt64():0,
            root.TryGetProperty("devicePolicyRevision",out var deviceRev)?deviceRev.GetInt64():0,
            root.TryGetProperty("clientAccessRevision",out var clientRev)?clientRev.GetInt64():0,
            root.TryGetProperty("securityPolicyRevision",out var securityRev)?securityRev.GetInt64():0,
            root.TryGetProperty("accountingDataRevision",out var dataRev)?dataRev.GetInt64():0,
            root.TryGetProperty("updateRing",out var updateRing)?updateRing.GetString()??"Production":"Production",
            !root.TryGetProperty("updateRingEnabled",out var ringEnabled)||ringEnabled.GetBoolean(),
            root.TryGetProperty("updateForceAfterUtc",out var forceAfter)&&forceAfter.ValueKind!=JsonValueKind.Null&&DateTime.TryParse(forceAfter.GetString(),out var forceAfterUtc)?forceAfterUtc:(DateTime?)null);
    }

    public async Task<NetworkPackageMetadata> GetWindowsPackageMetadataAsync()
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url("/api/v1/packages/windows/metadata"));AddDevice(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var root=json.RootElement;
        return new NetworkPackageMetadata(
            root.GetProperty("version").GetString()??string.Empty,
            root.GetProperty("sha256").GetString()??string.Empty,
            root.TryGetProperty("fileName",out var name)?name.GetString()??"AAHNetworkClient.zip":"AAHNetworkClient.zip",
            root.TryGetProperty("size",out var size)?size.GetInt64():0,
            root.TryGetProperty("forceUpdate",out var force)&&force.GetBoolean());
    }

    public async Task DownloadWindowsPackageAsync(string destinationPath)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url("/api/v1/packages/windows/download"));AddDevice(request);
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);await EnsureAsync(response);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)??Path.GetTempPath());
        await using var input=await response.Content.ReadAsStreamAsync();
        await using var output=new FileStream(destinationPath,FileMode.Create,FileAccess.Write,FileShare.None);
        await input.CopyToAsync(output);
    }

    public async Task<NetworkEnterpriseStatus> GetEnterpriseStatusAsync()
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url("/api/v1/control/status"));AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var root=json.RootElement;
        var permissions=root.TryGetProperty("permissions",out var p)&&p.ValueKind==JsonValueKind.Array?p.EnumerateArray().Select(x=>x.GetString()??string.Empty).Where(x=>x.Length>0).ToArray():Array.Empty<string>();
        var posture=root.TryGetProperty("posture",out var po)?JsonSerializer.Deserialize<DevicePostureRecord>(po.GetRawText(),JsonOptions):new DevicePostureRecord{DeviceId=DeviceId};
        var revisions=root.TryGetProperty("revisions",out var re)?JsonSerializer.Deserialize<SecurityPolicySnapshot>(re.GetRawText(),JsonOptions):new SecurityPolicySnapshot();
        return new NetworkEnterpriseStatus(root.TryGetProperty("userId",out var uid)?uid.GetString()??string.Empty:string.Empty,root.TryGetProperty("user",out var un)?un.GetString()??string.Empty:string.Empty,root.TryGetProperty("role",out var role)?role.GetString()??string.Empty:string.Empty,permissions,posture??new DevicePostureRecord{DeviceId=DeviceId},revisions??new SecurityPolicySnapshot());
    }

    public async Task<IReadOnlyList<RemoteAahCommandRecord>> GetRemoteCommandsAsync(int waitSeconds=0)
        => await GetJsonListAsync<RemoteAahCommandRecord>($"/api/v1/remote/commands?waitSeconds={Math.Clamp(waitSeconds,0,25)}");

    public async Task CompleteRemoteCommandAsync(string commandId,bool success,string detail)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Url($"/api/v1/remote/commands/{Uri.EscapeDataString(commandId)}/complete")){Content=JsonContent.Create(new{success,detail})};AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
    }

    public async Task<IReadOnlyList<NetworkDirectoryUser>> GetDirectoryUsersAsync()
        => await GetJsonListAsync<NetworkDirectoryUser>("/api/v1/directory/users");

    public async Task<IReadOnlyList<CollaborationMessageRecord>> GetMessagesAsync()
        => await GetJsonListAsync<CollaborationMessageRecord>("/api/v1/messages");

    public async Task<CollaborationMessageRecord> SendMessageAsync(CollaborationMessageRecord message)
        => await PostJsonAsync<CollaborationMessageRecord>("/api/v1/messages",message);

    public async Task MarkConversationReadAsync(string conversationKey)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Url("/api/v1/messages/read")){Content=JsonContent.Create(new{conversationKey})};AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
    }

    public async Task<CollaborationMessageRecord> EditMessageAsync(long id,string body)
        => await PostJsonResultAsync<CollaborationMessageRecord>($"/api/v1/messages/{id}/edit",new{body});

    public async Task<CollaborationMessageRecord> DeleteMessageAsync(long id)
        => await PostJsonResultAsync<CollaborationMessageRecord>($"/api/v1/messages/{id}/delete",new{});

    public async Task<CollaborationMessageRecord> SetMessagePinnedAsync(long id,bool pinned)
        => await PostJsonResultAsync<CollaborationMessageRecord>($"/api/v1/messages/{id}/pin",new{pinned});

    public async Task<CollaborationMessageRecord> ToggleMessageReactionAsync(long id,string reaction)
        => await PostJsonResultAsync<CollaborationMessageRecord>($"/api/v1/messages/{id}/reaction",new{reaction});

    public async Task<UserChatStatusRecord> SetUserChatStatusAsync(string availability,string statusMessage)
        => await PostJsonResultAsync<UserChatStatusRecord>("/api/v1/directory/status",new{availability,statusMessage});

    public async Task<IReadOnlyList<CollaborationFileRecord>> GetSharedFilesAsync()
        => await GetJsonListAsync<CollaborationFileRecord>("/api/v1/shared-files");

    public async Task<CollaborationFileUploadResult> UploadSharedFileAsync(string path,long clientId,string financialYear,long messageId=0)
    {
        using var form=new MultipartFormDataContent();
        await using var stream=File.OpenRead(path);var file=new StreamContent(stream);file.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");form.Add(file,"file",Path.GetFileName(path));
        form.Add(new StringContent(clientId.ToString()),"clientId");form.Add(new StringContent(financialYear??string.Empty),"financialYear");form.Add(new StringContent(messageId.ToString()),"messageId");
        using var request=new HttpRequestMessage(HttpMethod.Post,Url("/api/v1/shared-files")){Content=form};AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);var body=await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<CollaborationFileUploadResult>(body,JsonOptions)??throw new InvalidDataException("Main Server returned an invalid shared-file response.");
    }

    public async Task DownloadSharedFileAsync(string fileId,string destinationPath)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url($"/api/v1/shared-files/{Uri.EscapeDataString(fileId)}"));AddSession(request);
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);await EnsureAsync(response);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)??Path.GetTempPath());await using var input=await response.Content.ReadAsStreamAsync();await using var output=new FileStream(destinationPath,FileMode.Create,FileAccess.Write,FileShare.None);await input.CopyToAsync(output);
    }

    public async Task<IReadOnlyList<WorkTaskRecord>> GetTasksAsync()
        => await GetJsonListAsync<WorkTaskRecord>("/api/v1/tasks");

    public async Task<WorkTaskRecord> CreateTaskAsync(WorkTaskRecord task)
        => await PostJsonAsync<WorkTaskRecord>("/api/v1/tasks",task);

    public async Task<WorkTaskRecord> UpdateTaskAsync(long id,string assignedTo,string status,string priority,DateTime? dueUtc,string description)
        => await PostJsonAsync<WorkTaskRecord>($"/api/v1/tasks/{id}/update",new WorkTaskRecord{AssignedTo=assignedTo,Status=status,Priority=priority,DueUtc=dueUtc,Description=description});

    public async Task<IReadOnlyList<ApprovalRequestRecord>> GetApprovalsAsync()
        => await GetJsonListAsync<ApprovalRequestRecord>("/api/v1/approvals");

    public async Task<ApprovalRequestRecord> CreateApprovalAsync(ApprovalRequestRecord approval)
        => await PostJsonAsync<ApprovalRequestRecord>("/api/v1/approvals",approval);

    public async Task DecideApprovalAsync(long id,bool approve,string reason)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Url($"/api/v1/approvals/{id}/decision")){Content=JsonContent.Create(new{approve,reason})};AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
    }

    public async Task<RemoteAssistSessionRecord?> GetCurrentRemoteAssistSessionAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/api/v1/remote/assist/current"));
        AddSession(request);
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        await EnsureAsync(response);
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<RemoteAssistSessionRecord>(body, JsonOptions);
    }

    public async Task SetRemoteAssistConsentAsync(string sessionId, bool accepted)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Remote Assist session id is required.", nameof(sessionId));
        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId.Trim())}/consent"))
        {
            Content = JsonContent.Create(new { accepted })
        };
        AddSession(request);
        using var response = await _http.SendAsync(request);
        await EnsureAsync(response);
    }

    public async Task<RemoteAssistAuthorityState> GetRemoteAssistSessionStatusAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Remote Assist session id is required.", nameof(sessionId));
        using var request = new HttpRequestMessage(HttpMethod.Get, Url($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId.Trim())}/status"));
        AddSession(request);
        using var response = await _http.SendAsync(request);
        await EnsureAsync(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var known = root.TryGetProperty("known", out var k) && k.ValueKind == JsonValueKind.True;
        var active = root.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
        var consented = root.TryGetProperty("userConsented", out var c) && c.ValueKind == JsonValueKind.True;
        DateTime? expiresUtc = null;
        if (root.TryGetProperty("expiresUtc", out var e) && e.ValueKind == JsonValueKind.String && DateTime.TryParse(e.GetString(), out var parsed)) expiresUtc = parsed.ToUniversalTime();
        return new RemoteAssistAuthorityState(sessionId.Trim(), known, active, consented, expiresUtc);
    }

    public async Task EndRemoteAssistByUserAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Remote Assist session id is required.", nameof(sessionId));
        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId.Trim())}/end"))
        {
            Content = JsonContent.Create(new { reason = "AAH user ended Remote Assist" })
        };
        AddSession(request);
        using var response = await _http.SendAsync(request);
        await EnsureAsync(response);
    }

    public async Task<RemoteAssistStreamStateRecord> GetRemoteAssistStreamStateAsync(string sessionId)
        => await GetJsonAsync<RemoteAssistStreamStateRecord>($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId)}/stream/state");

    public async Task<RemoteAssistStreamStateRecord> SetRemoteAssistRemoteStateAsync(string sessionId,bool shareMyAah,bool allowAdministratorControl)
        => await PostJsonResultAsync<RemoteAssistStreamStateRecord>($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId)}/stream/remote-state",new{shareMyAah,allowAdministratorControl});

    public async Task UploadRemoteAssistFrameAsync(string sessionId,RemoteAssistFrameRecord frame)
    {
        using var content=new ByteArrayContent(frame.JpegBytes);content.Headers.ContentType=new MediaTypeHeaderValue("image/jpeg");
        using var request=new HttpRequestMessage(HttpMethod.Post,Url($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId)}/frame/remote?width={frame.PixelWidth}&height={frame.PixelHeight}")){Content=content};AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
    }

    public async Task<RemoteAssistFrameRecord?> GetAdministratorRemoteAssistFrameAsync(string sessionId,long afterSequence)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId)}/frame/administrator?after={afterSequence}"));AddSession(request);
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        if(response.StatusCode==System.Net.HttpStatusCode.NoContent)return null;await EnsureAsync(response);
        var bytes=await response.Content.ReadAsByteArrayAsync();
        static long ReadLong(HttpResponseMessage r,string n)=>r.Headers.TryGetValues(n,out var v)&&long.TryParse(v.FirstOrDefault(),out var x)?x:0;
        static int ReadInt(HttpResponseMessage r,string n)=>r.Headers.TryGetValues(n,out var v)&&int.TryParse(v.FirstOrDefault(),out var x)?x:0;
        var captured=DateTime.UtcNow;if(response.Headers.TryGetValues("X-AAH-Frame-Captured-Utc",out var dates)&&DateTime.TryParse(dates.FirstOrDefault(),out var parsed))captured=parsed.ToUniversalTime();
        return new RemoteAssistFrameRecord{SessionId=sessionId,Direction=RemoteAssistStreamDirections.AdministratorToRemote,Sequence=ReadLong(response,"X-AAH-Frame-Sequence"),PixelWidth=ReadInt(response,"X-AAH-Frame-Width"),PixelHeight=ReadInt(response,"X-AAH-Frame-Height"),CapturedUtc=captured,JpegBytes=bytes};
    }

    public async Task<IReadOnlyList<RemoteAssistInputRecord>> GetRemoteAssistRemoteInputsAsync(string sessionId,long afterSequence)
        => await GetJsonListAsync<RemoteAssistInputRecord>($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId)}/input/remote?after={afterSequence}");

    public async Task<RemoteAssistInputRecord> SendRemoteAssistAdministratorInputAsync(string sessionId,RemoteAssistInputRecord input)
        => await PostJsonResultAsync<RemoteAssistInputRecord>($"/api/v1/remote/assist/{Uri.EscapeDataString(sessionId)}/input/administrator",input);

    public async Task HeartbeatAsync() => _ = await GetUpdateStatusAsync();

    public async Task<IReadOnlyList<NetworkDistributedImportJob>> GetDistributedImportJobsAsync()
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url("/api/v1/sync/import-jobs"));AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if(!json.RootElement.TryGetProperty("jobs",out var jobs)||jobs.ValueKind!=JsonValueKind.Array)return Array.Empty<NetworkDistributedImportJob>();
        return JsonSerializer.Deserialize<List<NetworkDistributedImportJob>>(jobs.GetRawText(),JsonOptions)??new List<NetworkDistributedImportJob>();
    }

    public async Task<(string FileName,byte[] Bytes)> DownloadDistributedImportJobAsync(string jobId)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url($"/api/v1/sync/import-jobs/{Uri.EscapeDataString(jobId)}/file"));AddSession(request);
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);await EnsureAsync(response);
        var name=response.Content.Headers.ContentDisposition?.FileNameStar??response.Content.Headers.ContentDisposition?.FileName??"AAH_Import_Source";
        return(name.Trim('"'),await response.Content.ReadAsByteArrayAsync());
    }

    public async Task CompleteDistributedImportJobAsync(string jobId,bool success,string detail)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Url($"/api/v1/sync/import-jobs/{Uri.EscapeDataString(jobId)}/complete")){Content=JsonContent.Create(new{success,detail})};AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
    }

    public async Task<NetworkRegistrySnapshot> GetRegistrySnapshotAsync()
        => await GetJsonAsync<NetworkRegistrySnapshot>("/api/v1/sync/registries");

    public async Task<NetworkRegistryMergeResult> PushRegistryDeltasAsync(IReadOnlyList<RegistryDeltaRecord> deltas)
        => await PostJsonResultAsync<NetworkRegistryMergeResult>("/api/v1/sync/registries",new{deltas});

    public async Task<IReadOnlyList<ClientAutoImportPolicy>> GetAutoImportPoliciesAsync()
        => await GetJsonListAsync<ClientAutoImportPolicy>("/api/v1/sync/auto-import-policies");

    private async Task<bool> CanSafelyAcceptAuthoritativeSnapshotAsync()
    {
        if(!NetworkClientRuntime.IsNetworkClient) return true;
        var pending=new NetworkPendingImportSyncService();
        var sources=pending.GetPending(25);
        if(sources.Count==0) return true;
        foreach(var source in sources)
        {
            NetworkImportSourceSyncResult upload;
            try { upload=await UploadCompletedLocalImportAsync(source).ConfigureAwait(false); }
            catch { return false; }
            if(!upload.Accepted||string.IsNullOrWhiteSpace(upload.JobId)) return false;
            NetworkImportSourceStatus status;
            try { status=await GetCompletedLocalImportStatusAsync(upload.JobId).ConfigureAwait(false); }
            catch { return false; }
            if(status.Completed) pending.MarkSynchronized(source.Id,status.Detail);
            else return false;
        }
        return pending.GetPending(1).Count==0;
    }

    public async Task<NetworkImportSourceSyncResult> UploadCompletedLocalImportAsync(PendingNetworkImportSource source)
    {
        await using var stream=File.OpenRead(source.RetainedPath);
        using var form=new MultipartFormDataContent();
        form.Add(new StringContent(source.FinancialYear??string.Empty),"financialYear");
        form.Add(new StringContent(source.Module??string.Empty),"module");
        form.Add(new StringContent(source.Sha256??string.Empty),"sha256");
        form.Add(new StringContent(source.Id??string.Empty),"sourceSyncId");
        form.Add(new StringContent(source.RecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),"recordCount");
        var content=new StreamContent(stream);
        form.Add(content,"file",source.OriginalName);
        using var request=new HttpRequestMessage(HttpMethod.Post,Url($"/api/v1/sync/import-source/{source.ClientId}")){Content=form};AddSession(request);
        using var response=await _http.SendAsync(request);await EnsureAsync(response);
        var body=await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<NetworkImportSourceSyncResult>(body,JsonOptions)??throw new InvalidDataException("Main Server returned an invalid import-source synchronization response.");
    }

    public Task<NetworkImportSourceStatus> GetCompletedLocalImportStatusAsync(string jobId) =>
        GetJsonAsync<NetworkImportSourceStatus>($"/api/v1/sync/import-source-status/{Uri.EscapeDataString(jobId??string.Empty)}");

    private async Task<T> GetJsonAsync<T>(string path)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url(path));AddSession(request);using var response=await _http.SendAsync(request);await EnsureAsync(response);var body=await response.Content.ReadAsStringAsync();return JsonSerializer.Deserialize<T>(body,JsonOptions)??throw new InvalidDataException("Main Server returned an invalid response.");
    }

    private async Task<IReadOnlyList<T>> GetJsonListAsync<T>(string path)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Url(path));AddSession(request);using var response=await _http.SendAsync(request);await EnsureAsync(response);var body=await response.Content.ReadAsStringAsync();return JsonSerializer.Deserialize<List<T>>(body,JsonOptions)??new List<T>();
    }

    private async Task<T> PostJsonAsync<T>(string path,T payload)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Url(path)){Content=JsonContent.Create(payload)};AddSession(request);using var response=await _http.SendAsync(request);await EnsureAsync(response);var body=await response.Content.ReadAsStringAsync();return JsonSerializer.Deserialize<T>(body,JsonOptions)??throw new InvalidDataException("Main Server returned an invalid response.");
    }

    private async Task<TResult> PostJsonResultAsync<TResult>(string path,object payload)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Url(path)){Content=JsonContent.Create(payload)};AddSession(request);using var response=await _http.SendAsync(request);await EnsureAsync(response);var body=await response.Content.ReadAsStringAsync();return JsonSerializer.Deserialize<TResult>(body,JsonOptions)??throw new InvalidDataException("Main Server returned an invalid response.");
    }

    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};


    private static HttpClient CreatePinnedHttpClient()
    {
        var caPath = Path.Combine(AppContext.BaseDirectory, "AAH_MAIN_SERVER_CA.cer");
        if (!File.Exists(caPath))
            throw new InvalidOperationException("This LAN application does not contain the Main Server security certificate. Re-share the LAN application from Settings -> Security & LAN on the Main Server.");

        var expectedHashPath = Path.Combine(AppContext.BaseDirectory, "AAH_MAIN_SERVER_CA_SHA256");
        using var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);
        var caHash = ca.GetCertHashString(HashAlgorithmName.SHA256);
        if (File.Exists(expectedHashPath))
        {
            var expectedHash = File.ReadAllText(expectedHashPath).Trim().Replace(" ", string.Empty);
            if (expectedHash.Length > 0 && !caHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The bundled Main Server security certificate failed SHA-256 verification. Re-share the LAN application from the Main Server.");
        }

        var pinnedCaRaw = ca.RawData.ToArray();
        var serverTlsPath = Path.Combine(AppContext.BaseDirectory, "AAH_MAIN_SERVER_TLS.cer");
        if (!File.Exists(serverTlsPath))
            throw new InvalidOperationException("This LAN application does not contain the Main Server HTTPS certificate. Re-share the LAN application from Settings -> Security & LAN on the Main Server.");
        using var pinnedServer = X509CertificateLoader.LoadCertificateFromFile(serverTlsPath);
        var expectedServerHashPath = Path.Combine(AppContext.BaseDirectory, "AAH_MAIN_SERVER_TLS_SHA256");
        var serverHash = pinnedServer.GetCertHashString(HashAlgorithmName.SHA256);
        if (File.Exists(expectedServerHashPath))
        {
            var expectedServerHash = File.ReadAllText(expectedServerHashPath).Trim().Replace(" ", string.Empty);
            if (expectedServerHash.Length > 0 && !serverHash.Equals(expectedServerHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The bundled Main Server HTTPS certificate failed SHA-256 verification. Re-share the LAN application from the Main Server.");
        }

        var pinnedServerRaw = pinnedServer.RawData.ToArray();
        var handler = new HttpClientHandler
        {
            CheckCertificateRevocationList = false
        };
        handler.ServerCertificateCustomValidationCallback = (_, serverCertificate, _, sslErrors) =>
            ValidatePinnedServerCertificate(serverCertificate, sslErrors, pinnedServerRaw, pinnedCaRaw);
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(5) };
    }

    private static bool ValidatePinnedServerCertificate(X509Certificate2? serverCertificate, SslPolicyErrors sslErrors, byte[] pinnedServerRaw, byte[] pinnedCaRaw)
    {
        if (serverCertificate is null) return false;
        if ((sslErrors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
        // Keep .NET's endpoint host/IP validation. The server-specific package pins the exact
        // public TLS certificate, so Windows does not need to trust/build AAH's private CA chain.
        if ((sslErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        if (serverCertificate.RawData.AsSpan().SequenceEqual(pinnedServerRaw)) return true;

        // CA fallback permits a normally renewed server leaf under the same AAH CA. Exact leaf
        // pinning above is the primary path and avoids Windows private-chain construction issues.
        using var pinnedCa = X509CertificateLoader.LoadCertificate(pinnedCaRaw);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(pinnedCa);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        if (!chain.Build(serverCertificate)) return false;
        if (chain.ChainElements.Count == 0) return false;
        var root = chain.ChainElements[^1].Certificate;
        return root.RawData.AsSpan().SequenceEqual(pinnedCa.RawData);
    }
    private string Url(string path)
    {
        var value = Server.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            value = NetworkClientRuntime.ResolveServerEndpoint();
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The LAN application could not recover its HTTPS Main Server endpoint. Re-run the latest Main Server LAN share package once to repair this workstation connection.");
            Server = value;
        }
        return value.TrimEnd('/') + path;
    }
    private void AddDevice(HttpRequestMessage request){request.Headers.Add("X-AAH-Device",DeviceId);request.Headers.Add("X-AAH-Device-Token",DeviceToken);request.Headers.Add("X-AAH-App-Version",AppVersion);}
    private void AddSession(HttpRequestMessage request){request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",SessionToken);AddDevice(request);}
    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;
        var body=await response.Content.ReadAsStringAsync();
        var detail=body;
        var codeValue=string.Empty;
        try
        {
            using var json=JsonDocument.Parse(body);
            if(json.RootElement.TryGetProperty("error",out var error)&&!string.IsNullOrWhiteSpace(error.GetString())) detail=error.GetString()!;
            if(json.RootElement.TryGetProperty("code",out var code)&&!string.IsNullOrWhiteSpace(code.GetString()))
            {
                codeValue=code.GetString()??string.Empty;
                detail += $" [{codeValue}]";
            }
        }
        catch { }
        throw new NetworkApiException((int)response.StatusCode, $"Main Server {(int)response.StatusCode}: {detail}", codeValue);
    }
    public void Dispose()=>_http.Dispose();
}

public sealed record NetworkDistributedImportJob(string JobId,long ClientId,string FinancialYear,string Module,string OriginalName,string Sha256,long FileSize,string Source,string RequestedBy,string SourceDeviceId,string RequestedExecutionDeviceId,bool SharedContextPresent,int PasswordCandidateCount,string[] PasswordCandidates,DateTime CreatedUtc);
public sealed record NetworkRegistrySnapshot(long Revision,RegistryEntrySnapshot[] Entries);
public sealed record NetworkRegistryMergeResult(long Revision,int Applied,int Identical,int Conflicts,RegistryEntrySnapshot[] AuthoritativeEntries);
public sealed record NetworkImportSourceSyncResult(bool Accepted,string JobId,string Detail);
public sealed record NetworkImportSourceStatus(string JobId,string Status,bool Completed,bool Failed,string Detail,DateTime UpdatedUtc);

public sealed class NetworkApiException(int statusCode, string message, string code = "") : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code?.Trim() ?? string.Empty;
    public bool RequiresReEnrollment => StatusCode == 401 &&
        (Code.Equals("DEVICE_REGISTRATION_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
         Code.Equals("DEVICE_REGISTRATION_REJECTED", StringComparison.OrdinalIgnoreCase));
}

public sealed record NetworkMobileUserCode(string Code,DateTime ExpiresUtc,string UserId,string Payload,string Purpose);
public sealed record NetworkSessionIdentity(string UserId,string DisplayName,string RoleName,string SessionToken,DateTime ExpiresUtc,string[] Permissions);
public sealed record NetworkUpdateStatus(
    string CurrentVersion,string ApprovedVersion,string ApprovedSha256,bool UpdateRequired,bool ForceUpdate,bool RegistrationOtpRequired,
    string HealthState,int RiskScore,bool Quarantined,string QuarantineReason,bool EmergencyLockdown,string LockdownReason,
    long AuthorizationRevision,long DevicePolicyRevision,long ClientAccessRevision,long SecurityPolicyRevision,long AccountingDataRevision,
    string UpdateRing,bool UpdateRingEnabled,DateTime? UpdateForceAfterUtc);
public sealed record NetworkPackageMetadata(string Version,string Sha256,string FileName,long Size,bool ForceUpdate);

public sealed record NetworkEnterpriseStatus(string UserId,string DisplayName,string RoleName,string[] Permissions,DevicePostureRecord Posture,SecurityPolicySnapshot Revisions);
public sealed class CollaborationFileUploadResult{public long Id{get;set;}public string FileId{get;set;}=string.Empty;public string OriginalName{get;set;}=string.Empty;public string Sha256{get;set;}=string.Empty;public long FileSize{get;set;}public long ClientId{get;set;}public string FinancialYear{get;set;}=string.Empty;public DateTime CreatedUtc{get;set;}}

public sealed class NetworkDirectoryUser
{
    public string UserId{get;set;}=string.Empty;
    public string DisplayName{get;set;}=string.Empty;
    public string Role{get;set;}=string.Empty;
    public string Presence{get;set;}="Offline";
    public string Availability{get;set;}="Available";
    public string StatusMessage{get;set;}=string.Empty;
    public DateTime? LastSeenUtc{get;set;}
    public int ActiveSessions{get;set;}
    public string Devices{get;set;}=string.Empty;
    public long CurrentClientId{get;set;}
    public string CurrentClient{get;set;}=string.Empty;
    public string CurrentFinancialYear{get;set;}=string.Empty;
    public string CurrentModule{get;set;}=string.Empty;
    public string DisplayWithPresence=>$"{DisplayName} · {Presence}{(Availability.Equals("Available",StringComparison.OrdinalIgnoreCase)||Availability.Equals("Offline",StringComparison.OrdinalIgnoreCase)?string.Empty:$" · {Availability}")}";
    public string Detail=>$"{Role} · {(string.IsNullOrWhiteSpace(CurrentClient)?"No client open":CurrentClient)}{(string.IsNullOrWhiteSpace(CurrentFinancialYear)?string.Empty:$" · FY {CurrentFinancialYear}")}{(string.IsNullOrWhiteSpace(CurrentModule)?string.Empty:$" · {CurrentModule}")}";
    public string LastSeenText=>Presence.Equals("Online",StringComparison.OrdinalIgnoreCase)?"Active now":LastSeenUtc.HasValue?$"Last seen {LastSeenUtc.Value.ToLocalTime():g}":"No recent activity";
    public string SessionText=>ActiveSessions>0?$"{ActiveSessions} active session(s){(string.IsNullOrWhiteSpace(Devices)?string.Empty:$" · {Devices}")}":string.IsNullOrWhiteSpace(Devices)?"No active device session":Devices;
}

public sealed record RemoteAssistAuthorityState(string SessionId,bool Known,bool Active,bool UserConsented,DateTime? ExpiresUtc);
