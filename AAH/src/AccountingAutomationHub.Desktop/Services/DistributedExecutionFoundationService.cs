using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Shared execution/synchronization contracts for Main Server, LAN and outside-LAN AAH Windows nodes.
/// Android is a controller/bridge only and never executes an accounting importer.
/// </summary>
public static class DistributedImportModules
{
    public const string Banking = "Banking";
    public const string Purchase = "Purchase";
    public const string Sales = "Sales";
    public const string Journal = "Journal";
    public const string CashEntry = "Cash Entry";
    public const string TallyBackup = "Tally Backup";
    public const string DocumentsOnly = "Documents / Store Only";

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Banking, Purchase, Sales, Journal, CashEntry, TallyBackup, DocumentsOnly
    };

    public static bool IsAllowed(string? module) => module is not null && Allowed.Contains(module.Trim());
}

public sealed record DistributedImportJobRecord
{
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");
    public long ClientId { get; init; }
    public string FinancialYear { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string OriginalName { get; init; } = string.Empty;
    public string StoredPath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public string RequestedBy { get; init; } = string.Empty;
    public string SourceDeviceId { get; init; } = string.Empty;
    public string RequestedExecutionDeviceId { get; init; } = string.Empty;
    public string ParentJobId { get; init; } = string.Empty;
    public string AuthoritativeJobId { get; init; } = string.Empty;
    public string ProtectedPasswordCandidates { get; init; } = string.Empty;
    public int PasswordCandidateCount { get; init; }
    public bool SharedContextPresent { get; init; }
    public string Status { get; init; } = "Pending";
    public string ResultDetail { get; init; } = string.Empty;
    public bool CommitVerified { get; init; }
    public int CommittedRecordCount { get; init; }
    public string IssueCode { get; init; } = string.Empty;
    public string ClaimedByDeviceId { get; init; } = string.Empty;
    public DateTime? ClaimedUtc { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Durable import-job queue. Jobs contain only allow-listed AAH import intent; never arbitrary shell commands.
/// The queue is append/update based so an offline Windows execution node can resume after reconnect.
/// </summary>
public sealed class DistributedImportJobService
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DistributedImportJobService(string? authorityRoot = null)
    {
        _root = Path.Combine(string.IsNullOrWhiteSpace(authorityRoot) ? ApplicationDataPaths.RootFolder : authorityRoot, "DistributedSync", "ImportJobs");
        Directory.CreateDirectory(_root);
    }

    public DistributedImportJobRecord Enqueue(DistributedImportJobRecord record)
    {
        if (record.ClientId <= 0) throw new InvalidOperationException("A valid Client is required for a distributed import job.");
        if (!DistributedImportModules.IsAllowed(record.Module)) throw new InvalidOperationException("The requested import module is not allowed.");
        var safe = record with
        {
            Module = DistributedImportModules.Allowed.First(x => x.Equals(record.Module.Trim(), StringComparison.OrdinalIgnoreCase)),
            PasswordCandidateCount = Math.Clamp(record.PasswordCandidateCount, 0, 12),
            Status = string.IsNullOrWhiteSpace(record.Status) ? "Pending" : record.Status.Trim(),
            UpdatedUtc = DateTime.UtcNow
        };
        lock (_gate) AtomicWrite(PathFor(safe.JobId), JsonSerializer.Serialize(safe, _json));
        return safe;
    }

    public IReadOnlyList<DistributedImportJobRecord> ClaimPending(string deviceId, string userName, int max = 25)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return Array.Empty<DistributedImportJobRecord>();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var candidates = Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(TryRead).Where(x => x is not null).Cast<DistributedImportJobRecord>()
                .Where(x => x.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase) || x.Status.Equals("Assigned", StringComparison.OrdinalIgnoreCase))
                .Where(x => x.RequestedExecutionDeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(userName) || x.RequestedBy.Equals(userName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.CreatedUtc).Take(Math.Clamp(max, 1, 100)).ToArray();
            var claimed = new List<DistributedImportJobRecord>(candidates.Length);
            foreach (var candidate in candidates)
            {
                if (candidate.Status.Equals("Assigned", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(candidate.ClaimedByDeviceId) &&
                    !candidate.ClaimedByDeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var updated = candidate with
                {
                    Status = "Assigned",
                    ClaimedByDeviceId = deviceId,
                    ClaimedUtc = candidate.ClaimedUtc ?? now,
                    UpdatedUtc = now
                };
                AtomicWrite(PathFor(updated.JobId), JsonSerializer.Serialize(updated, _json));
                claimed.Add(updated);
            }
            return claimed;
        }
    }

    public IReadOnlyList<DistributedImportJobRecord> GetPending(string deviceId, string userName, int max = 25)
    {
        var all = ReadAll();
        return all.Where(x => x.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase) || x.Status.Equals("Assigned", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(deviceId) && x.RequestedExecutionDeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(userName) || x.RequestedBy.Equals(userName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.CreatedUtc).Take(Math.Clamp(max, 1, 100)).ToArray();
    }

    public DistributedImportJobRecord? Find(string jobId) => ReadOne(jobId);

    public DistributedImportJobRecord? Complete(
        string jobId,
        bool success,
        string detail,
        bool commitVerified = false,
        int committedRecordCount = 0,
        string issueCode = "")
    {
        lock (_gate)
        {
            var current = ReadOne(jobId);
            if (current is null) return null;
            if (current.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return current;
            var updated = current with
            {
                Status = success ? "Completed" : "Failed",
                ResultDetail = (detail ?? string.Empty).Trim(),
                CommitVerified = commitVerified,
                CommittedRecordCount = Math.Max(0, committedRecordCount),
                IssueCode = (issueCode ?? string.Empty).Trim(),
                UpdatedUtc = DateTime.UtcNow
            };
            AtomicWrite(PathFor(jobId), JsonSerializer.Serialize(updated, _json));
            return updated;
        }
    }

    public DistributedImportJobRecord? Cancel(string jobId, string detail)
    {
        lock (_gate)
        {
            var current = ReadOne(jobId);
            if (current is null) return null;
            if (current.CommitVerified || current.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) return current;
            if (current.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return current;
            var updated = current with
            {
                Status = "Cancelled",
                ResultDetail = string.IsNullOrWhiteSpace(detail) ? "Import cancelled by user before authoritative commit." : detail.Trim(),
                CommitVerified = false,
                CommittedRecordCount = 0,
                IssueCode = "IMPORT_CANCELLED_BY_USER",
                UpdatedUtc = DateTime.UtcNow
            };
            AtomicWrite(PathFor(jobId), JsonSerializer.Serialize(updated, _json));
            return updated;
        }
    }

    public DistributedImportJobRecord? Update(DistributedImportJobRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.JobId)) return null;
        lock (_gate)
        {
            var current = ReadOne(record.JobId);
            if (current is null) return null;
            if (current.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) &&
                !record.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return current;
            var updated = record with { UpdatedUtc = DateTime.UtcNow };
            AtomicWrite(PathFor(updated.JobId), JsonSerializer.Serialize(updated, _json));
            return updated;
        }
    }

    public IReadOnlyList<DistributedImportJobRecord> ReadAll()
    {
        lock (_gate)
        {
            return Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => TryRead(path)).Where(x => x is not null).Cast<DistributedImportJobRecord>()
                .OrderByDescending(x => x.CreatedUtc).ToArray();
        }
    }

    private DistributedImportJobRecord? ReadOne(string jobId)
    {
        var safe = SafeId(jobId);
        if (safe.Length == 0) return null;
        return TryRead(PathFor(safe));
    }

    private DistributedImportJobRecord? TryRead(string path)
    {
        try { return JsonSerializer.Deserialize<DistributedImportJobRecord>(File.ReadAllText(path), _json); }
        catch { return null; }
    }

    private string PathFor(string jobId) => Path.Combine(_root, SafeId(jobId) + ".json");
    private static string SafeId(string? value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(64).ToArray());
    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

public static class AppPrivateImportRuntimePaths
{
    public static string Root => Path.Combine(ApplicationDataPaths.RootFolder, "AppPrivateRuntime", "Import");
    public static string Runtime => Path.Combine(Root, "Runtime");
    public static string Registries => Path.Combine(Root, "Registries");
    public static string Staging => Path.Combine(Root, "Staging");
    public static string MappingSessions => Path.Combine(Root, "MappingSessions");

    public static void EnsureFolders()
    {
        Directory.CreateDirectory(Runtime);
        Directory.CreateDirectory(Registries);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(MappingSessions);
    }
}

public static class ActiveImportRegistryLocator
{
    private const string ExternalRegistryRoot = @"C:\BankPDFImporter\Registry";

    /// <summary>
    /// Registry synchronization must operate on the registry set the active importer actually uses.
    /// Existing Main Server/live-Python installations therefore keep using their authoritative external
    /// registry data. A future AAH-private runtime activates its own registry root by creating the private
    /// runtime marker; the updater itself never copies or replaces the external Python runtime.
    /// </summary>
    public static string Resolve()
    {
        AppPrivateImportRuntimePaths.EnsureFolders();
        var privateMarker = Path.Combine(AppPrivateImportRuntimePaths.Runtime, ".aah-private-runtime-active");
        if (File.Exists(privateMarker)) return AppPrivateImportRuntimePaths.Registries;
        if (Directory.Exists(ExternalRegistryRoot)) return ExternalRegistryRoot;
        return AppPrivateImportRuntimePaths.Registries;
    }
}

public sealed record RegistryEntrySnapshot(string RegistryName, string EntryKey, string Hash, string Payload);
public sealed record RegistryDeltaRecord(string RegistryName, string EntryKey, string BaseHash, string NewHash, string Payload, string SourceDeviceId, DateTime CreatedUtc, bool IsDeleted = false);
public sealed record RegistryMergeResult(long Revision, int Applied, int Identical, int Conflicts, IReadOnlyList<RegistryEntrySnapshot> AuthoritativeEntries);
public sealed record RegistryConflictRecord
{
    public string ConflictId { get; init; } = Guid.NewGuid().ToString("N");
    public RegistryDeltaRecord Incoming { get; init; } = new("", "", "", "", "", "", DateTime.UtcNow);
    public RegistryEntrySnapshot Current { get; init; } = new("", "", "", "");
    public DateTime DetectedUtc { get; init; } = DateTime.UtcNow;
    public string Status { get; init; } = "Pending";
    public string Decision { get; init; } = string.Empty;
    public string ResolvedBy { get; init; } = string.Empty;
    public DateTime? ResolvedUtc { get; init; }
}
public sealed record RegistryConflictResolution(bool Resolved, bool AppliedIncoming, long Revision, string Detail, RegistryConflictRecord? Conflict);

/// <summary>
/// Entry-level learned-registry synchronization. Whole registry files are never copied over another node.
/// Supports the normal/tabular cache+fingerprint family and the non-tabular line-plan family.
/// </summary>
public sealed class ImportRegistrySyncService
{
    public static IReadOnlyList<string> LearnedRegistryNames { get; } = new[]
    {
        "account_format_cache.json", "fingerprint_registry.json", "non_tabular_line_registry.json"
    };

    public static IReadOnlyList<string> ReferenceRegistryNames { get; } = new[]
    {
        "bank_registry.json", "bank_intelligence_master.json"
    };

    private readonly object _gate = new();
    private readonly string _registryRoot;
    private readonly string _statePath;
    private readonly string _conflictRoot;

    public ImportRegistrySyncService(string? registryRoot = null)
    {
        AppPrivateImportRuntimePaths.EnsureFolders();
        _registryRoot = registryRoot ?? ActiveImportRegistryLocator.Resolve();
        Directory.CreateDirectory(_registryRoot);
        _statePath = Path.Combine(_registryRoot, ".aah-registry-sync-state.json");
        _conflictRoot = Path.Combine(_registryRoot, ".conflicts");
        Directory.CreateDirectory(_conflictRoot);
    }

    public long Revision => ReadState().Revision;

    public IReadOnlyList<RegistryEntrySnapshot> SnapshotAll()
    {
        lock (_gate)
        {
            return LearnedRegistryNames.Concat(ReferenceRegistryNames)
                .SelectMany(SnapshotRegistry).OrderBy(x => x.RegistryName).ThenBy(x => x.EntryKey).ToArray();
        }
    }

    public IReadOnlyList<RegistryDeltaRecord> BuildLocalDeltas(string sourceDeviceId)
    {
        lock (_gate)
        {
            var state = ReadState();
            var current = SnapshotAll();
            var previous = state.Hashes;
            var currentKeys = current.ToDictionary(x => StateKey(x.RegistryName, x.EntryKey), StringComparer.Ordinal);
            var deltas = current.Where(x => !previous.TryGetValue(StateKey(x.RegistryName, x.EntryKey), out var hash) || !hash.Equals(x.Hash, StringComparison.OrdinalIgnoreCase))
                .Select(x => new RegistryDeltaRecord(x.RegistryName, x.EntryKey,
                    previous.TryGetValue(StateKey(x.RegistryName, x.EntryKey), out var baseHash) ? baseHash : string.Empty,
                    x.Hash, x.Payload, sourceDeviceId ?? string.Empty, DateTime.UtcNow)).ToList();
            foreach (var prior in previous)
            {
                if (currentKeys.ContainsKey(prior.Key)) continue;
                var separator = prior.Key.IndexOf('|');
                if (separator <= 0 || separator >= prior.Key.Length - 1) continue;
                var registry = prior.Key[..separator];
                var entryKey = prior.Key[(separator + 1)..];
                if (!IsKnownRegistry(registry)) continue;
                deltas.Add(new RegistryDeltaRecord(registry, entryKey, prior.Value, string.Empty, string.Empty, sourceDeviceId ?? string.Empty, DateTime.UtcNow, IsDeleted: true));
            }
            return deltas.ToArray();
        }
    }

    public RegistryMergeResult MergeAuthoritative(IEnumerable<RegistryDeltaRecord> deltas)
    {
        lock (_gate)
        {
            var applied = 0; var identical = 0; var conflicts = 0;
            foreach (var delta in deltas ?? Array.Empty<RegistryDeltaRecord>())
            {
                if (!IsKnownRegistry(delta.RegistryName) || string.IsNullOrWhiteSpace(delta.EntryKey)) continue;
                var current = SnapshotRegistry(delta.RegistryName).FirstOrDefault(x => x.EntryKey.Equals(delta.EntryKey, StringComparison.Ordinal));
                if (delta.IsDeleted)
                {
                    if (current is null) { identical++; continue; }
                    if (string.IsNullOrWhiteSpace(delta.BaseHash) || !current.Hash.Equals(delta.BaseHash, StringComparison.OrdinalIgnoreCase))
                    {
                        if (WasPreviouslyRejected(delta, current)) { identical++; continue; }
                        SaveConflict(delta, current); conflicts++; continue;
                    }
                    RemoveEntry(delta.RegistryName, delta.EntryKey);
                    applied++;
                    continue;
                }
                if (current is not null && current.Hash.Equals(delta.NewHash, StringComparison.OrdinalIgnoreCase)) { identical++; continue; }
                if (current is not null && !string.IsNullOrWhiteSpace(delta.BaseHash) && !current.Hash.Equals(delta.BaseHash, StringComparison.OrdinalIgnoreCase))
                {
                    if (WasPreviouslyRejected(delta, current)) { identical++; continue; }
                    SaveConflict(delta, current); conflicts++; continue;
                }
                if (current is not null && string.IsNullOrWhiteSpace(delta.BaseHash))
                {
                    if (WasPreviouslyRejected(delta, current)) { identical++; continue; }
                    SaveConflict(delta, current); conflicts++; continue;
                }
                ApplyEntry(delta.RegistryName, delta.EntryKey, delta.Payload);
                applied++;
            }
            var state = ReadState();
            var revision = state.Revision + (applied > 0 ? 1 : 0);
            var snapshot = SnapshotAll();
            SaveState(revision, snapshot);
            return new RegistryMergeResult(revision, applied, identical, conflicts, snapshot);
        }
    }

    public void ApplyAuthoritativeSnapshot(long revision, IEnumerable<RegistryEntrySnapshot> entries)
    {
        lock (_gate)
        {
            var authoritative = (entries ?? Array.Empty<RegistryEntrySnapshot>())
                .Where(entry => IsKnownRegistry(entry.RegistryName) && !string.IsNullOrWhiteSpace(entry.EntryKey)).ToArray();
            var authoritativeKeys = authoritative.Select(x => StateKey(x.RegistryName, x.EntryKey)).ToHashSet(StringComparer.Ordinal);
            foreach (var current in SnapshotAll())
                if (!authoritativeKeys.Contains(StateKey(current.RegistryName, current.EntryKey))) RemoveEntry(current.RegistryName, current.EntryKey);
            foreach (var entry in authoritative) ApplyEntry(entry.RegistryName, entry.EntryKey, entry.Payload);
            SaveState(revision, SnapshotAll());
        }
    }

    private IReadOnlyList<RegistryEntrySnapshot> SnapshotRegistry(string registryName)
    {
        var path = Path.Combine(_registryRoot, Path.GetFileName(registryName));
        if (!File.Exists(path)) return Array.Empty<RegistryEntrySnapshot>();
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is JsonObject obj)
                return obj.Select(pair => MakeSnapshot(registryName, pair.Key, pair.Value)).Where(x => x is not null).Cast<RegistryEntrySnapshot>().ToArray();
            if (node is JsonArray array)
            {
                var result = new List<RegistryEntrySnapshot>();
                for (var i = 0; i < array.Count; i++) if (array[i] is not null) result.Add(MakeSnapshot(registryName, DeriveArrayKey(array[i]!, i), array[i])!);
                return result;
            }
            return Array.Empty<RegistryEntrySnapshot>();
        }
        catch { return Array.Empty<RegistryEntrySnapshot>(); }
    }

    private static RegistryEntrySnapshot? MakeSnapshot(string registryName, string key, JsonNode? node)
    {
        if (node is null) return null;
        var payload = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return new RegistryEntrySnapshot(registryName, key, Hash(payload), payload);
    }

    private void ApplyEntry(string registryName, string entryKey, string payload)
    {
        var path = Path.Combine(_registryRoot, Path.GetFileName(registryName));
        JsonNode? value = JsonNode.Parse(payload);
        JsonNode root;
        try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject() : new JsonObject(); }
        catch { root = new JsonObject(); }

        if (root is JsonObject obj) obj[entryKey] = value?.DeepClone();
        else if (root is JsonArray array)
        {
            var found = -1;
            for (var i = 0; i < array.Count; i++) if (array[i] is not null && DeriveArrayKey(array[i]!, i).Equals(entryKey, StringComparison.Ordinal)) { found = i; break; }
            if (found >= 0) array[found] = value?.DeepClone(); else array.Add(value?.DeepClone());
        }
        else root = new JsonObject { [entryKey] = value?.DeepClone() };
        AtomicWrite(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RemoveEntry(string registryName, string entryKey)
    {
        var path = Path.Combine(_registryRoot, Path.GetFileName(registryName));
        if (!File.Exists(path)) return;
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); } catch { return; }
        if (root is JsonObject obj) obj.Remove(entryKey);
        else if (root is JsonArray array)
        {
            for (var i = array.Count - 1; i >= 0; i--)
                if (array[i] is not null && DeriveArrayKey(array[i]!, i).Equals(entryKey, StringComparison.Ordinal)) array.RemoveAt(i);
        }
        else return;
        AtomicWrite(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string DeriveArrayKey(JsonNode node, int index)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in new[] { "format_id", "formatId", "account_number", "accountNumber", "id", "key", "name", "bank" })
                if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return name + ":" + text.Trim();
        }
        return "entry:" + Hash(node.ToJsonString()).Substring(0, 20);
    }

    public IReadOnlyList<RegistryConflictRecord> ReadConflicts(bool pendingOnly = true)
    {
        lock (_gate)
        {
            var rows = new List<RegistryConflictRecord>();
            foreach (var path in Directory.EnumerateFiles(_conflictRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var row = JsonSerializer.Deserialize<RegistryConflictRecord>(File.ReadAllText(path));
                    if (row is null) continue;
                    if (pendingOnly && !row.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase)) continue;
                    rows.Add(row);
                }
                catch { }
            }
            return rows.OrderBy(x => x.DetectedUtc).ToArray();
        }
    }

    public RegistryConflictResolution ResolveConflict(string conflictId, bool acceptIncoming, string resolvedBy)
    {
        lock (_gate)
        {
            var safeId = SafeSegment(conflictId);
            if (safeId.Length == 0) return new(false, false, Revision, "Registry conflict ID is required.", null);
            var pair = Directory.EnumerateFiles(_conflictRoot, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => (path, row: TryReadConflict(path)))
                .FirstOrDefault(x => x.row is not null && x.row.ConflictId.Equals(conflictId, StringComparison.OrdinalIgnoreCase));
            var currentConflict = pair.row;
            if (currentConflict is null) return new(false, false, Revision, "That registry conflict is no longer pending.", null);
            if (!currentConflict.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                return new(true, currentConflict.Decision.Equals("AcceptIncoming", StringComparison.OrdinalIgnoreCase), Revision, "That registry conflict was already resolved.", currentConflict);

            if (acceptIncoming)
            {
                if (currentConflict.Incoming.IsDeleted) RemoveEntry(currentConflict.Incoming.RegistryName, currentConflict.Incoming.EntryKey);
                else ApplyEntry(currentConflict.Incoming.RegistryName, currentConflict.Incoming.EntryKey, currentConflict.Incoming.Payload);
            }
            var snapshot = SnapshotAll();
            var state = ReadState();
            var revision = state.Revision + (acceptIncoming ? 1 : 0);
            SaveState(revision, snapshot);
            var resolved = currentConflict with
            {
                Status = "Resolved",
                Decision = acceptIncoming ? "AcceptIncoming" : "KeepAuthoritative",
                ResolvedBy = (resolvedBy ?? string.Empty).Trim(),
                ResolvedUtc = DateTime.UtcNow
            };
            AtomicWrite(pair.path, JsonSerializer.Serialize(resolved, new JsonSerializerOptions { WriteIndented = true }));
            return new(true, acceptIncoming, revision, acceptIncoming ? "LAN/outside-LAN registry change accepted and merged into the authoritative registry." : "Authoritative Main Server registry entry kept; incoming conflicting change rejected.", resolved);
        }
    }

    private RegistryConflictRecord? TryReadConflict(string path)
    {
        try { return JsonSerializer.Deserialize<RegistryConflictRecord>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private bool WasPreviouslyRejected(RegistryDeltaRecord incoming, RegistryEntrySnapshot current)
    {
        return ReadConflicts(false).Any(x =>
            x.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase) &&
            x.Decision.Equals("KeepAuthoritative", StringComparison.OrdinalIgnoreCase) &&
            x.Incoming.RegistryName.Equals(incoming.RegistryName, StringComparison.OrdinalIgnoreCase) &&
            x.Incoming.EntryKey.Equals(incoming.EntryKey, StringComparison.Ordinal) &&
            x.Incoming.NewHash.Equals(incoming.NewHash, StringComparison.OrdinalIgnoreCase) &&
            x.Incoming.IsDeleted == incoming.IsDeleted &&
            x.Current.Hash.Equals(current.Hash, StringComparison.OrdinalIgnoreCase));
    }

    private void SaveConflict(RegistryDeltaRecord incoming, RegistryEntrySnapshot current)
    {
        var existing = ReadConflicts(false).FirstOrDefault(x =>
            x.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase) &&
            x.Incoming.RegistryName.Equals(incoming.RegistryName, StringComparison.OrdinalIgnoreCase) &&
            x.Incoming.EntryKey.Equals(incoming.EntryKey, StringComparison.Ordinal) &&
            x.Incoming.NewHash.Equals(incoming.NewHash, StringComparison.OrdinalIgnoreCase) &&
            x.Current.Hash.Equals(current.Hash, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return;
        var conflict = new RegistryConflictRecord { Incoming = incoming, Current = current, DetectedUtc = DateTime.UtcNow };
        var payload = JsonSerializer.Serialize(conflict, new JsonSerializerOptions { WriteIndented = true });
        AtomicWrite(Path.Combine(_conflictRoot, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{SafeSegment(conflict.ConflictId)}_{SafeSegment(incoming.RegistryName)}_{SafeSegment(incoming.EntryKey)}.json"), payload);
    }

    private RegistrySyncState ReadState()
    {
        try { return File.Exists(_statePath) ? JsonSerializer.Deserialize<RegistrySyncState>(File.ReadAllText(_statePath)) ?? new RegistrySyncState() : new RegistrySyncState(); }
        catch { return new RegistrySyncState(); }
    }

    private void SaveState(long revision, IEnumerable<RegistryEntrySnapshot> entries)
    {
        var state = new RegistrySyncState { Revision = revision, Hashes = entries.ToDictionary(x => StateKey(x.RegistryName, x.EntryKey), x => x.Hash, StringComparer.Ordinal) };
        AtomicWrite(_statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsKnownRegistry(string name) => LearnedRegistryNames.Concat(ReferenceRegistryNames).Any(x => x.Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));
    private static string StateKey(string registry, string key) => registry.ToLowerInvariant() + "|" + key;
    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    private static string SafeSegment(string value) => new string((value ?? string.Empty).Select(c => char.IsLetterOrDigit(c) ? c : '_').Take(48).ToArray());
    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private sealed class RegistrySyncState
    {
        public long Revision { get; set; }
        public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.Ordinal);
    }
}

public sealed record MappingSessionRecord
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public long ClientId { get; init; }
    public string FinancialYear { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string ImportJobId { get; init; } = string.Empty;
    public string State { get; init; } = "WaitingForMapping";
    public string Presenter { get; init; } = string.Empty;
    public string PresenterDeviceId { get; init; } = string.Empty;
    public string PythonRequestJson { get; init; } = string.Empty;
    public string UserResponseJson { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>One mapping engine session, one active presenter. Presenter may transfer Android <-> Windows without restarting Python.</summary>
public sealed class MappingSessionBrokerService
{
    private readonly object _gate = new();
    private readonly string _root;
    public MappingSessionBrokerService() { AppPrivateImportRuntimePaths.EnsureFolders(); _root = AppPrivateImportRuntimePaths.MappingSessions; }

    public MappingSessionRecord Save(MappingSessionRecord session)
    {
        lock (_gate)
        {
            var value = session with { UpdatedUtc = DateTime.UtcNow };
            File.WriteAllText(Path.Combine(_root, Safe(value.SessionId) + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return value;
        }
    }

    public MappingSessionRecord? Get(string sessionId)
    {
        lock (_gate)
        {
            var path = Path.Combine(_root, Safe(sessionId) + ".json");
            try { return File.Exists(path) ? JsonSerializer.Deserialize<MappingSessionRecord>(File.ReadAllText(path)) : null; } catch { return null; }
        }
    }

    public MappingSessionRecord? Claim(string sessionId, string presenter, string presenterDeviceId)
    {
        lock (_gate)
        {
            var current = Get(sessionId); if (current is null) return null;
            if (!string.IsNullOrWhiteSpace(current.PresenterDeviceId) && !current.PresenterDeviceId.Equals(presenterDeviceId, StringComparison.OrdinalIgnoreCase))
                return null;
            return Save(current with { Presenter = presenter.Trim(), PresenterDeviceId = presenterDeviceId.Trim() });
        }
    }

    public MappingSessionRecord? Transfer(string sessionId, string presenter, string presenterDeviceId)
    {
        lock (_gate)
        {
            var current = Get(sessionId); if (current is null) return null;
            return Save(current with { Presenter = presenter.Trim(), PresenterDeviceId = presenterDeviceId.Trim() });
        }
    }

    private static string Safe(string value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(64).ToArray());
}

public sealed record ClientTallyLinkRecord
{
    public long ClientId { get; init; }
    public string HostDeviceId { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string DataPath { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string CompanyGuid { get; init; } = string.Empty;
    public string CompanyKey { get; init; } = string.Empty;
    public string CompanyNumber { get; init; } = string.Empty;
    // Product family belongs to the verified running executable, not the company data file.
    // Independent from BackupProductFamily (which classifies archived backup files).
    public string ApplicationProductFamily { get; init; } = string.Empty;
    public string ApplicationProductVersion { get; init; } = string.Empty;
    public string ApplicationFileVersion { get; init; } = string.Empty;
    public string ApplicationVersionEvidence { get; init; } = string.Empty;
    public DateTime? ApplicationVersionConfirmedUtc { get; init; }
    public bool Validated { get; init; }
    public bool AutoOpenEnabled => Validated && ClientId > 0 && !string.IsNullOrWhiteSpace(ExecutablePath) && !string.IsNullOrWhiteSpace(CompanyNumber);
    public DateTime? LastValidatedUtc { get; init; }
    public string BackupState { get; init; } = string.Empty;
    public string BackupProductFamily { get; init; } = string.Empty;
    public string BackupClassificationEvidence { get; init; } = string.Empty;
}

/// <summary>Persisted local Tally launch metadata for a confirmed Client/FY link.</summary>
public sealed class TallyLinkFoundationService
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(ApplicationDataPaths.RootFolder, "DistributedSync", "TallyLinks.json");
    public ClientTallyLinkRecord? Get(long clientId)
    {
        lock (_gate) return ReadAll().FirstOrDefault(x => x.ClientId == clientId);
    }
    public void Save(ClientTallyLinkRecord record)
    {
        lock (_gate)
        {
            var all = ReadAll().Where(x => x.ClientId != record.ClientId).Append(record).OrderBy(x => x.ClientId).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
    }
    private IReadOnlyList<ClientTallyLinkRecord> ReadAll()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<ClientTallyLinkRecord>>(File.ReadAllText(_path)) ?? [] : []; } catch { return []; }
    }
}

public sealed record ClientAutoImportPolicy(long ClientId, bool Enabled, DateTime UpdatedUtc);
public sealed class ClientAutoImportPolicyService
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(ApplicationDataPaths.RootFolder, "DistributedSync", "ClientAutoImportPolicies.json");
    public ClientAutoImportPolicy Get(long clientId)
    {
        lock (_gate) return ReadAll().FirstOrDefault(x => x.ClientId == clientId) ?? new ClientAutoImportPolicy(clientId, true, DateTime.MinValue);
    }
    public ClientAutoImportPolicy Set(long clientId, bool enabled)
    {
        lock (_gate)
        {
            var value = new ClientAutoImportPolicy(clientId, enabled, DateTime.UtcNow);
            SaveAll(ReadAll().Where(x => x.ClientId != clientId).Append(value));
            return value;
        }
    }
    public IReadOnlyList<ClientAutoImportPolicy> GetAll()
    {
        lock (_gate) return ReadAll().OrderBy(x => x.ClientId).ToArray();
    }
    public void ApplyAuthoritative(IEnumerable<ClientAutoImportPolicy> policies)
    {
        lock (_gate)
        {
            var local = ReadAll().ToDictionary(x => x.ClientId);
            foreach (var incoming in policies ?? Array.Empty<ClientAutoImportPolicy>())
            {
                if (incoming.ClientId <= 0) continue;
                if (!local.TryGetValue(incoming.ClientId, out var current) || incoming.UpdatedUtc >= current.UpdatedUtc)
                    local[incoming.ClientId] = incoming;
            }
            SaveAll(local.Values);
        }
    }
    private void SaveAll(IEnumerable<ClientAutoImportPolicy> values)
    {
        var all = values.Where(x => x.ClientId > 0).GroupBy(x => x.ClientId).Select(x => x.OrderByDescending(v => v.UpdatedUtc).First()).OrderBy(x => x.ClientId).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
    private IReadOnlyList<ClientAutoImportPolicy> ReadAll()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<ClientAutoImportPolicy>>(File.ReadAllText(_path)) ?? [] : []; } catch { return []; }
    }
}
