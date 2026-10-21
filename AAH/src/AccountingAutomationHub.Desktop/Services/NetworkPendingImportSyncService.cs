using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Durable source-replay queue for accounting imports completed on an authorised Windows node.
/// A source is copied into AAH-private storage immediately after a successful local import so the
/// Main Server can retain/replay the same source when connectivity returns. This is deliberately
/// source-based rather than a database-file mirror: local SQLite files are never copied over the
/// Main Server database.
/// </summary>
public sealed class NetworkPendingImportSyncService
{
    private readonly object _gate = new();
    private readonly string _root = Path.Combine(AppPrivateImportRuntimePaths.Staging, "OutboundImports");
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public NetworkPendingImportSyncService() => Directory.CreateDirectory(_root);

    public PendingNetworkImportSource? Register(AutomationAccountingDataChangedEventArgs change)
    {
        if (!NetworkClientRuntime.IsNetworkClient || change.ClientId <= 0 || string.IsNullOrWhiteSpace(change.SourcePath) || !File.Exists(change.SourcePath)) return null;

        // A distributed job downloaded from the Main Server/Android is already tracked by
        // DistributedImportJobService. Re-registering its temporary RemoteJobs copy as a new
        // locally-originated source would create a second replay path and possible duplicate.
        try
        {
            var sourceFullPath = Path.GetFullPath(change.SourcePath);
            var remoteJobsRoot = Path.GetFullPath(Path.Combine(AppPrivateImportRuntimePaths.Staging, "RemoteJobs"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (sourceFullPath.StartsWith(remoteJobsRoot, StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { }

        var module = NormalizeModule(change.Module);
        if (module.Length == 0) return null;
        try
        {
            var bytes = File.ReadAllBytes(change.SourcePath);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var existing = GetPending().FirstOrDefault(x => x.ClientId == change.ClientId &&
                x.FinancialYear.Equals(change.FinancialYear, StringComparison.OrdinalIgnoreCase) &&
                x.Module.Equals(module, StringComparison.OrdinalIgnoreCase) &&
                x.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;

            var id = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(_root, id);
            Directory.CreateDirectory(folder);
            var originalName = SafeFileName(Path.GetFileName(change.SourcePath));
            var retainedPath = Path.Combine(folder, originalName);
            File.WriteAllBytes(retainedPath, bytes);
            var record = new PendingNetworkImportSource
            {
                Id = id,
                ClientId = change.ClientId,
                FinancialYear = change.FinancialYear?.Trim() ?? string.Empty,
                Module = module,
                OriginalName = originalName,
                RetainedPath = retainedPath,
                Sha256 = hash,
                FileSize = bytes.LongLength,
                RecordCount = Math.Max(0, change.RecordCount),
                SourceDeviceId = NetworkClientRuntime.DeviceId,
                RequestedBy = NetworkClientRuntime.UserName,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                Status = "Pending"
            };
            Write(record);
            return record;
        }
        catch { return null; }
    }

    public IReadOnlyList<PendingNetworkImportSource> GetPending(int max = 50)
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root)) return [];
            var rows = new List<PendingNetworkImportSource>();
            foreach (var metadata in Directory.EnumerateFiles(_root, "job.json", SearchOption.AllDirectories))
            {
                try
                {
                    var row = JsonSerializer.Deserialize<PendingNetworkImportSource>(File.ReadAllText(metadata), _json);
                    if (row is not null && row.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase) && File.Exists(row.RetainedPath)) rows.Add(row);
                }
                catch { }
            }
            return rows.OrderBy(x => x.CreatedUtc).Take(Math.Clamp(max, 1, 250)).ToArray();
        }
    }

    public void MarkSynchronized(string id, string detail = "")
    {
        lock (_gate)
        {
            var row = Read(id);
            if (row is null) return;
            row = row with { Status = "Synchronized", ResultDetail = detail ?? string.Empty, UpdatedUtc = DateTime.UtcNow };
            Write(row);
            try { if (File.Exists(row.RetainedPath)) File.Delete(row.RetainedPath); } catch { }
            try
            {
                var folder = Path.GetDirectoryName(PathFor(id));
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Count() == 1)
                    Directory.Delete(folder, true);
            }
            catch { }
        }
    }

    private PendingNetworkImportSource? Read(string id)
    {
        try
        {
            var path = PathFor(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<PendingNetworkImportSource>(File.ReadAllText(path), _json) : null;
        }
        catch { return null; }
    }

    private void Write(PendingNetworkImportSource row)
    {
        lock (_gate)
        {
            var path = PathFor(row.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(row, _json), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
    }

    private string PathFor(string id) => Path.Combine(_root, SafeSegment(id), "job.json");

    private static string NormalizeModule(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Contains("bank", StringComparison.OrdinalIgnoreCase) || text.Contains("ocr", StringComparison.OrdinalIgnoreCase)) return DistributedImportModules.Banking;
        if (text.Contains("purchase", StringComparison.OrdinalIgnoreCase) || text.Contains("2a", StringComparison.OrdinalIgnoreCase) || text.Contains("2b", StringComparison.OrdinalIgnoreCase)) return DistributedImportModules.Purchase;
        if (text.Contains("sales", StringComparison.OrdinalIgnoreCase) || text.Contains("credit note", StringComparison.OrdinalIgnoreCase) || text.Contains("gstr-1", StringComparison.OrdinalIgnoreCase)) return DistributedImportModules.Sales;
        if (text.Contains("journal", StringComparison.OrdinalIgnoreCase)) return DistributedImportModules.Journal;
        if (text.Contains("cash", StringComparison.OrdinalIgnoreCase)) return DistributedImportModules.CashEntry;
        return DistributedImportModules.IsAllowed(text) ? DistributedImportModules.Allowed.First(x => x.Equals(text, StringComparison.OrdinalIgnoreCase)) : string.Empty;
    }

    private static string SafeSegment(string value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(64).ToArray());
    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((value ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "AAH_Import_Source.bin" : clean;
    }
}

public sealed record PendingNetworkImportSource
{
    public string Id { get; init; } = string.Empty;
    public long ClientId { get; init; }
    public string FinancialYear { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string OriginalName { get; init; } = string.Empty;
    public string RetainedPath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public int RecordCount { get; init; }
    public string SourceDeviceId { get; init; } = string.Empty;
    public string RequestedBy { get; init; } = string.Empty;
    public string Status { get; init; } = "Pending";
    public string ResultDetail { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
}
