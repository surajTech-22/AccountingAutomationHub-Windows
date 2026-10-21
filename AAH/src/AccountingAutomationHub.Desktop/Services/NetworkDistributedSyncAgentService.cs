using System.Security.Cryptography;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Silent all-network Windows execution/synchronization agent. It runs only on an enrolled
/// LAN/outside-LAN AAH client, processes explicit Android/import jobs on that Windows host,
/// and synchronizes app-private learned registries entry-by-entry with the Main Server.
/// It never copies or replaces the user's external live Python runtime.
/// </summary>
public sealed class NetworkDistributedSyncAgentService : IDisposable
{
    private readonly BackgroundAutomationService _automation;
    private readonly ImportRegistrySyncService _registries = new();
    private readonly ClientAutoImportPolicyService _autoImportPolicies = new();
    private readonly NetworkPendingImportSyncService _pendingSources = new();
    private readonly Timer _timer;
    private int _running;
    private bool _disposed;

    public NetworkDistributedSyncAgentService(BackgroundAutomationService automation)
    {
        _automation = automation;
        AppPrivateImportRuntimePaths.EnsureFolders();
        _timer = new Timer(async _ => await TickAsync().ConfigureAwait(false), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    private async Task TickAsync()
    {
        if (_disposed || !NetworkClientRuntime.IsNetworkClient || string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken) || Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            using var api = CreateApi();
            await SynchronizeAutoImportPoliciesAsync(api).ConfigureAwait(false);
            await SynchronizeRegistriesAsync(api).ConfigureAwait(false);
            await SynchronizePendingLocalImportsAsync(api).ConfigureAwait(false);
            var jobs = await api.GetDistributedImportJobsAsync().ConfigureAwait(false);
            foreach (var job in jobs.Take(3))
            {
                if (_disposed) break;
                await ExecuteJobAsync(api, job).ConfigureAwait(false);
            }
        }
        catch
        {
            // Silent background sync by design. Connectivity/auth problems are retried on the next heartbeat.
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public void RegisterLocalImport(AutomationAccountingDataChangedEventArgs change)
    {
        if (_disposed || !NetworkClientRuntime.IsNetworkClient) return;
        var source = change.SourcePath ?? string.Empty;
        var remoteRoot = Path.GetFullPath(Path.Combine(AppPrivateImportRuntimePaths.Staging, "RemoteJobs"));
        try
        {
            if (source.Length > 0 && Path.GetFullPath(source).StartsWith(remoteRoot, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch { }
        _pendingSources.Register(change);
    }

    private async Task SynchronizeAutoImportPoliciesAsync(FullUiNetworkClientApi api)
    {
        var policies=await api.GetAutoImportPoliciesAsync().ConfigureAwait(false);
        _autoImportPolicies.ApplyAuthoritative(policies);
    }

    private async Task SynchronizePendingLocalImportsAsync(FullUiNetworkClientApi api)
    {
        foreach(var source in _pendingSources.GetPending(5))
        {
            if (_disposed) return;
            try
            {
                var result=await api.UploadCompletedLocalImportAsync(source).ConfigureAwait(false);
                if(!result.Accepted||string.IsNullOrWhiteSpace(result.JobId)) return;
                var status=await api.GetCompletedLocalImportStatusAsync(result.JobId).ConfigureAwait(false);
                if(status.Completed)_pendingSources.MarkSynchronized(source.Id,status.Detail);
                else if(status.Failed)return;
                else return;
            }
            catch
            {
                // Leave the retained source pending. A later connection retries silently.
                return;
            }
        }
    }

    private async Task SynchronizeRegistriesAsync(FullUiNetworkClientApi api)
    {
        var deltas = _registries.BuildLocalDeltas(NetworkClientRuntime.DeviceId);
        if (deltas.Count > 0)
        {
            var merge = await api.PushRegistryDeltasAsync(deltas).ConfigureAwait(false);
            // A genuine conflict must remain local for Administrator resolution; never overwrite it silently.
            if (merge.Conflicts == 0) _registries.ApplyAuthoritativeSnapshot(merge.Revision, merge.AuthoritativeEntries);
            return;
        }
        var snapshot = await api.GetRegistrySnapshotAsync().ConfigureAwait(false);
        if (snapshot.Revision != _registries.Revision) _registries.ApplyAuthoritativeSnapshot(snapshot.Revision, snapshot.Entries);
    }

    private async Task ExecuteJobAsync(FullUiNetworkClientApi api, NetworkDistributedImportJob job)
    {
        string? localPath = null;
        try
        {
            var download = await api.DownloadDistributedImportJobAsync(job.JobId).ConfigureAwait(false);
            var safeName = SafeFileName(string.IsNullOrWhiteSpace(download.FileName) ? job.OriginalName : download.FileName);
            var jobFolder = Path.Combine(AppPrivateImportRuntimePaths.Staging, "RemoteJobs", SafeSegment(job.JobId));
            Directory.CreateDirectory(jobFolder);
            localPath = Path.Combine(jobFolder, safeName);
            await File.WriteAllBytesAsync(localPath, download.Bytes).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(download.Bytes)).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(job.Sha256) && !hash.Equals(job.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded import source failed its SHA-256 check.");

            var result = await _automation.ProcessExplicitImportAsync(job.ClientId, job.FinancialYear, job.Module, localPath, job.PasswordCandidates).ConfigureAwait(false);
            await api.CompleteDistributedImportJobAsync(job.JobId, result.WasSuccessful, result.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { await api.CompleteDistributedImportJobAsync(job.JobId, false, ex.Message).ConfigureAwait(false); } catch { }
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath)) File.Delete(localPath);
                var folder = string.IsNullOrWhiteSpace(localPath) ? string.Empty : Path.GetDirectoryName(localPath);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            catch { }
        }
    }

    private static FullUiNetworkClientApi CreateApi() => new()
    {
        Server = NetworkClientRuntime.ResolveServerEndpoint(),
        DeviceId = NetworkClientRuntime.DeviceId,
        DeviceToken = NetworkClientRuntime.DeviceToken,
        SessionToken = NetworkClientRuntime.SessionToken
    };

    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value ?? string.Empty);
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(name) ? $"AAH_Import_{DateTime.UtcNow:yyyyMMddHHmmss}.bin" : name;
    }
    private static string SafeSegment(string value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(64).ToArray());

    public void Dispose()
    {
        _disposed = true;
        try { _timer.Dispose(); } catch { }
    }
}
