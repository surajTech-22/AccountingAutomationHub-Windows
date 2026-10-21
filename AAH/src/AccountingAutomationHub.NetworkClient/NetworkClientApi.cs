using System.Net.Http;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;

namespace AccountingAutomationHub.NetworkClient;

internal sealed class NetworkClientApi : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    public string Server { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceToken { get; set; } = string.Empty;
    public string SessionToken { get; set; } = string.Empty;
    public string AppVersion => typeof(NetworkClientApi).Assembly.GetName().Version?.ToString(4) ?? "4.0.0.301";

    public async Task EnrollAsync(string code,string user,string password,string totp,string deviceName)
    {
        var response=await _http.PostAsJsonAsync(Url("/api/v1/enroll"),new{enrollmentCode=code,userName=user,password,totpCode=totp,deviceName,deviceType="Windows",appVersion=AppVersion});
        await EnsureAsync(response); using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        DeviceId=json.RootElement.GetProperty("deviceId").GetString()??string.Empty; DeviceToken=json.RootElement.GetProperty("deviceToken").GetString()??string.Empty;
        WindowsCredentialStore.Save(user,$"{DeviceId}\n{DeviceToken}");
    }
    public void LoadStoredDevice()
    {
        var stored=WindowsCredentialStore.Load().Split('\n',2); if(stored.Length==2){DeviceId=stored[0].Trim();DeviceToken=stored[1].Trim();}
    }
    public async Task LoginAsync(string user,string password,string totp)
    {
        var response=await _http.PostAsJsonAsync(Url("/api/v1/auth/login"),new{deviceId=DeviceId,deviceToken=DeviceToken,userName=user,password,totpCode=totp,appVersion=AppVersion});
        await EnsureAsync(response); using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); SessionToken=json.RootElement.GetProperty("sessionToken").GetString()??string.Empty;
    }
    public async Task<IReadOnlyList<ClientInfo>> GetClientsAsync()
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,Url("/api/v1/clients")); AddSession(req); using var response=await _http.SendAsync(req); await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<List<ClientInfo>>()??[];
    }
    public async Task<string> GetSummaryAsync(long clientId)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,Url($"/api/v1/client/{clientId}/summary")); AddSession(req); using var response=await _http.SendAsync(req); await EnsureAsync(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return JsonSerializer.Serialize(json.RootElement,new JsonSerializerOptions{WriteIndented=true});
    }
    public async Task<string> UploadAsync(long clientId,string path,string? linkId=null,string? folderToken=null)
    {
        using var form=new MultipartFormDataContent(); await using var stream=File.OpenRead(path); var file=new StreamContent(stream); form.Add(file,"file",Path.GetFileName(path));
        var url=linkId is null?$"/api/v1/client/{clientId}/upload":$"/api/v1/folder-links/{Uri.EscapeDataString(linkId)}/upload";
        using var req=new HttpRequestMessage(HttpMethod.Post,Url(url)){Content=form};
        if(linkId is null) AddSession(req); else AddDevice(req);
        if(folderToken is not null)req.Headers.Add("X-AAH-Folder-Token",folderToken);
        using var response=await _http.SendAsync(req); await EnsureAsync(response); return await response.Content.ReadAsStringAsync();
    }
    public async Task<string> ExportAsync(long clientId,string kind,string destinationFolder)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,Url($"/api/v1/client/{clientId}/export/{Uri.EscapeDataString(kind)}")); AddSession(req); using var response=await _http.SendAsync(req); await EnsureAsync(response);
        var fileName=response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')??response.Content.Headers.ContentDisposition?.FileName?.Trim('"')??$"AAH_{kind}_{DateTime.Now:yyyyMMddHHmmss}.csv";
        Directory.CreateDirectory(destinationFolder); var path=Path.Combine(destinationFolder,Path.GetFileName(fileName)); await using var output=File.Create(path); await response.Content.CopyToAsync(output); return path;
    }
    public async Task HeartbeatFolderLinkAsync(string linkId,string folderToken)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,Url($"/api/v1/folder-links/{Uri.EscapeDataString(linkId)}/heartbeat")); AddDevice(req); req.Headers.Add("X-AAH-Folder-Token",folderToken);
        using var response=await _http.SendAsync(req); await EnsureAsync(response);
    }
    public async Task<(string LinkId,string Token)> CreateFolderLinkAsync(long clientId,string label)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,Url("/api/v1/folder-links")){Content=JsonContent.Create(new{clientId,localLabel=label})}; AddSession(req); using var response=await _http.SendAsync(req); await EnsureAsync(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return(json.RootElement.GetProperty("linkId").GetString()??string.Empty,json.RootElement.GetProperty("folderToken").GetString()??string.Empty);
    }
    public async Task<string> HeartbeatAsync()
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,Url("/api/v1/heartbeat")); AddDevice(req); using var response=await _http.SendAsync(req); await EnsureAsync(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var update=json.RootElement.TryGetProperty("updateRequired",out var u)&&u.GetBoolean();
        var approved=json.RootElement.TryGetProperty("approvedVersion",out var a)?a.GetString()??string.Empty:string.Empty;
        return update?$"Connected · approved server version {approved} is available; download/update from the Main Server.":"Connected · device lease validated.";
    }

    public async Task<string> DownloadApprovedUpdateAsync(string destinationFolder)
    {
        using var metaReq=new HttpRequestMessage(HttpMethod.Get,Url("/api/v1/packages/windows/metadata"));AddDevice(metaReq);
        using var metaResponse=await _http.SendAsync(metaReq);await EnsureAsync(metaResponse);
        using var metadata=JsonDocument.Parse(await metaResponse.Content.ReadAsStringAsync());
        var expected=metadata.RootElement.GetProperty("sha256").GetString()??string.Empty;
        var fileName=metadata.RootElement.GetProperty("fileName").GetString()??"AAHNetworkClient.zip";
        var version=metadata.RootElement.GetProperty("version").GetString()??string.Empty;
        using var req=new HttpRequestMessage(HttpMethod.Get,Url("/api/v1/packages/windows/download"));AddDevice(req);
        using var response=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead);await EnsureAsync(response);
        Directory.CreateDirectory(destinationFolder);var path=Path.Combine(destinationFolder,Path.GetFileName(fileName));
        await using(var input=await response.Content.ReadAsStreamAsync()){await using var output=File.Create(path);await input.CopyToAsync(output);}
        await using var check=File.OpenRead(path);var actual=Convert.ToHexString(await SHA256.HashDataAsync(check)).ToLowerInvariant();
        if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase)){File.Delete(path);throw new InvalidDataException("Main Server package verification failed: downloaded SHA-256 does not match the approved registry.");}
        return $"Verified Main Server Network Client {version} downloaded to {path}. Close AAH Network Client before replacing the current files.";
    }
    private string Url(string path)
    {
        var value=Server.Trim();
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||!uri.Scheme.Equals(Uri.UriSchemeHttps,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AAH Network Client requires an HTTPS Main Server endpoint.");
        return value.TrimEnd('/')+path;
    }
    private void AddDevice(HttpRequestMessage req){req.Headers.Add("X-AAH-Device",DeviceId);req.Headers.Add("X-AAH-Device-Token",DeviceToken);req.Headers.Add("X-AAH-App-Version",AppVersion);}
    private void AddSession(HttpRequestMessage req){req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",SessionToken);AddDevice(req);}
    private static async Task EnsureAsync(HttpResponseMessage response){if(response.IsSuccessStatusCode)return;var body=await response.Content.ReadAsStringAsync();throw new InvalidOperationException($"Server {(int)response.StatusCode}: {body}");}
    public void Dispose()=>_http.Dispose();
    public sealed class ClientInfo{public long Id{get;set;}public string ClientCode{get;set;}="";public string ClientName{get;set;}="";public string FinancialYear{get;set;}="";public override string ToString()=> $"{ClientCode} · {ClientName} · FY {FinancialYear}";}
}
