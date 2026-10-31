using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Infrastructure.Imports;

/// <summary>
/// Fix38 shared Sales/Purchase source index. Source discovery is global and read-only;
/// actual accounting writes remain owned by Auto Import / the normal module import services.
/// </summary>
public static class SalesPurchaseSourceCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SalesPurchaseSourceCacheEntry> Entries =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static int _persistScheduled;
    private static long _persistVersion;
    private static readonly Dictionary<string, SalesPurchaseHandledSetState> HandledSets =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _handledSetsLoaded;

    private static string CacheFolder => Path.Combine(ApplicationDataPaths.RootFolder, "Cache");
    private static string CachePath => Path.Combine(CacheFolder, "SalesPurchaseSourcesV1.json.gz");
    private static string HandledSetsPath => Path.Combine(CacheFolder, "SalesPurchaseHandledSetsV1.json.gz");

    public static void Record(
        string path,
        string module,
        string gstin,
        string financialYear,
        string fileHash,
        long size,
        DateTime lastWriteUtc,
        string sourceRole = "")
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var normalizedPath = Path.GetFullPath(path);
        var normalizedModule = NormalizeModule(module);
        var normalizedGstin = NormalizeGstin(gstin);
        var normalizedFy = NormalizeFinancialYear(financialYear);
        if (normalizedModule.Length == 0 || normalizedGstin.Length != 15) return;

        lock (Gate)
        {
            EnsureLoaded();
            var key = BuildKey(normalizedPath, normalizedModule, normalizedGstin, normalizedFy);
            Entries[key] = new SalesPurchaseSourceCacheEntry
            {
                Path = normalizedPath,
                Module = normalizedModule,
                Gstin = normalizedGstin,
                FinancialYear = normalizedFy,
                FileHash = fileHash ?? string.Empty,
                SourceRole = NormalizeSourceRole(normalizedModule, sourceRole, normalizedPath),
                Size = size,
                LastWriteUtc = lastWriteUtc,
                IndexedUtc = DateTime.UtcNow
            };
            Interlocked.Increment(ref _persistVersion);
        }
        QueuePersist();
    }

    /// <summary>
    /// FIXED41 R8: moving/renaming a linked Automation Folder is a location change, not a
    /// Sales/Purchase source change. Rebase cached source paths in-place so the previously
    /// handled GSTIN+FY content fingerprint survives the move and the grouped Auto Import
    /// gate does not rebuild the set one file at a time from the new physical path.
    /// </summary>
    public static void RebaseRootPaths(IReadOnlyList<(string OldRoot, string NewRoot)> rebases)
    {
        if (rebases is null || rebases.Count == 0) return;

        var normalized = new List<(string OldRoot, string NewRoot)>();
        foreach (var item in rebases)
        {
            try
            {
                var oldRoot = Path.GetFullPath(item.OldRoot);
                var newRoot = Path.GetFullPath(item.NewRoot);
                if (oldRoot.Length == 0 || newRoot.Length == 0 ||
                    oldRoot.Equals(newRoot, StringComparison.OrdinalIgnoreCase)) continue;
                normalized.Add((oldRoot, newRoot));
            }
            catch
            {
                // Cache acceleration only; the Automation Folder repository remains authority.
            }
        }
        if (normalized.Count == 0) return;

        var changed = false;
        lock (Gate)
        {
            EnsureLoaded();
            foreach (var pair in Entries.ToArray())
            {
                var rebasedPath = string.Empty;
                foreach (var root in normalized)
                {
                    if (TryRebaseChildPath(pair.Value.Path, root.OldRoot, root.NewRoot, out rebasedPath)) break;
                }
                if (rebasedPath.Length == 0 || rebasedPath.Equals(pair.Value.Path, StringComparison.OrdinalIgnoreCase)) continue;

                Entries.Remove(pair.Key);
                pair.Value.Path = rebasedPath;
                pair.Value.IndexedUtc = DateTime.UtcNow;
                var newKey = BuildKey(rebasedPath, pair.Value.Module, pair.Value.Gstin, pair.Value.FinancialYear);

                // A duplicate copy at the destination must not create a second content identity.
                // Prefer the entry whose source hash is proved, otherwise retain the newest one.
                if (Entries.TryGetValue(newKey, out var existing))
                {
                    var incomingProved = !string.IsNullOrWhiteSpace(pair.Value.FileHash);
                    var existingProved = !string.IsNullOrWhiteSpace(existing.FileHash);
                    if ((incomingProved && !existingProved) ||
                        (incomingProved == existingProved && pair.Value.IndexedUtc >= existing.IndexedUtc))
                        Entries[newKey] = pair.Value;
                }
                else
                {
                    Entries[newKey] = pair.Value;
                }
                changed = true;
            }

            if (changed) Interlocked.Increment(ref _persistVersion);
        }

        if (changed) QueuePersist();
    }

    private static bool TryRebaseChildPath(string candidate, string oldRoot, string newRoot, out string rebased)
    {
        rebased = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        string full;
        try { full = Path.GetFullPath(candidate); }
        catch { return false; }

        if (full.Equals(oldRoot, StringComparison.OrdinalIgnoreCase))
        {
            rebased = newRoot;
            return true;
        }

        var prefix = oldRoot.EndsWith(Path.DirectorySeparatorChar) || oldRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? oldRoot
            : oldRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            rebased = Path.GetFullPath(Path.Combine(newRoot, full[prefix.Length..]));
            return true;
        }
        catch { return false; }
    }

    public static IReadOnlyList<string> GetPaths(string module, string gstin, string financialYear)
    {
        var normalizedModule = NormalizeModule(module);
        var normalizedGstin = NormalizeGstin(gstin);
        var normalizedFy = NormalizeFinancialYear(financialYear);
        lock (Gate)
        {
            EnsureLoaded();
            PruneMissingUnsafe();
            return Entries.Values
                .Where(entry => entry.Module.Equals(normalizedModule, StringComparison.OrdinalIgnoreCase))
                .Where(entry => entry.Gstin.Equals(normalizedGstin, StringComparison.OrdinalIgnoreCase))
                .Where(entry => entry.FinancialYear.Length == 0 || normalizedFy.Length == 0 ||
                                entry.FinancialYear.Equals(normalizedFy, StringComparison.OrdinalIgnoreCase))
                .Where(entry => File.Exists(entry.Path))
                .OrderByDescending(entry => entry.LastWriteUtc)
                .Select(entry => entry.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public static string GetSetFingerprint(string module, string gstin, string financialYear)
    {
        var normalizedModule = NormalizeModule(module);
        var normalizedGstin = NormalizeGstin(gstin);
        var normalizedFy = NormalizeFinancialYear(financialYear);
        if (normalizedModule.Length == 0 || normalizedGstin.Length != 15 || normalizedFy.Length == 0) return string.Empty;

        lock (Gate)
        {
            EnsureLoaded();
            PruneMissingUnsafe();
            return GetSetFingerprintUnsafe(normalizedModule, normalizedGstin, normalizedFy);
        }
    }

    public static bool IsSetHandled(string module, string gstin, string financialYear, string setFingerprint, long ownerClientId = 0)
    {
        if (string.IsNullOrWhiteSpace(setFingerprint)) return false;
        var key = BuildHandledSetKey(module, gstin, financialYear, ownerClientId);
        if (key.Length == 0) return false;
        lock (Gate)
        {
            EnsureHandledSetsLoadedUnsafe();
            return HandledSets.TryGetValue(key, out var state) &&
                   (state.Status.Equals("Current", StringComparison.OrdinalIgnoreCase) ||
                    state.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase)) &&
                   state.SetFingerprint.Equals(setFingerprint.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    // A GSTIN can be detached from one client and registered as a separate
    // client. Its old "Imported" flag must not suppress the new owner's work.
    public static void InvalidateHandledSet(string module, string gstin, string financialYear)
    {
        var key = BuildHandledSetKey(module, gstin, financialYear);
        if (key.Length == 0) return;
        lock (Gate)
        {
            EnsureHandledSetsLoadedUnsafe();
            if (HandledSets.Remove(key)) PersistHandledSetsUnsafe();
        }
    }

    public static void MarkSetHandled(
        string module,
        string gstin,
        string financialYear,
        string setFingerprint,
        string accountingSourceFingerprint,
        string status,
        long ownerClientId = 0)
    {
        if (string.IsNullOrWhiteSpace(setFingerprint)) return;
        var key = BuildHandledSetKey(module, gstin, financialYear, ownerClientId);
        if (key.Length == 0) return;
        lock (Gate)
        {
            EnsureHandledSetsLoadedUnsafe();
            HandledSets[key] = new SalesPurchaseHandledSetState
            {
                Module = NormalizeModule(module),
                Gstin = NormalizeGstin(gstin),
                FinancialYear = NormalizeFinancialYear(financialYear),
                OwnerClientId = ownerClientId,
                SetFingerprint = setFingerprint.Trim(),
                AccountingSourceFingerprint = (accountingSourceFingerprint ?? string.Empty).Trim(),
                Status = (status ?? string.Empty).Trim(),
                HandledUtc = DateTime.UtcNow
            };
            PersistHandledSetsUnsafe();
        }
    }

    public static bool IsCurrent(
        string path,
        string module,
        string gstin,
        string financialYear,
        long size,
        DateTime lastWriteUtc)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalizedPath = Path.GetFullPath(path);
        var key = BuildKey(normalizedPath, NormalizeModule(module), NormalizeGstin(gstin), NormalizeFinancialYear(financialYear));
        lock (Gate)
        {
            EnsureLoaded();
            return Entries.TryGetValue(key, out var entry) &&
                   entry.Size == size &&
                   entry.LastWriteUtc == lastWriteUtc;
        }
    }

    /// <summary>
    /// Fix131 startup fast-path. If this exact file metadata is already indexed and every
    /// Client/FY source-set identity represented by that cached path is still marked Current
    /// or Imported for the same content fingerprint, the historical startup scan can skip
    /// reopening/classifying/hashing the file. Live file-system events do not use this shortcut.
    /// </summary>
    public static bool IsPathMetadataCurrentAndHandled(string path, long size, DateTime lastWriteUtc)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalizedPath;
        try { normalizedPath = Path.GetFullPath(path); }
        catch { return false; }

        lock (Gate)
        {
            EnsureLoaded();
            EnsureHandledSetsLoadedUnsafe();

            var matches = Entries.Values
                .Where(entry => entry.Path.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                .Where(entry => entry.Size == size && entry.LastWriteUtc == lastWriteUtc)
                .Where(entry => NormalizeModule(entry.Module).Length > 0)
                .Where(entry => NormalizeGstin(entry.Gstin).Length == 15)
                .Where(entry => NormalizeFinancialYear(entry.FinancialYear).Length > 0)
                .ToArray();
            if (matches.Length == 0) return false;

            foreach (var identity in matches
                         .Select(entry => new
                         {
                             Module = NormalizeModule(entry.Module),
                             Gstin = NormalizeGstin(entry.Gstin),
                             FinancialYear = NormalizeFinancialYear(entry.FinancialYear)
                         })
                         .DistinctBy(item => $"{item.Module}|{item.Gstin}|{item.FinancialYear}", StringComparer.OrdinalIgnoreCase))
            {
                var fingerprint = GetSetFingerprintUnsafe(identity.Module, identity.Gstin, identity.FinancialYear);
                if (fingerprint.Length == 0) return false;
                var handledKey = BuildHandledSetKey(identity.Module, identity.Gstin, identity.FinancialYear);
                if (!HandledSets.TryGetValue(handledKey, out var state) ||
                    !(state.Status.Equals("Current", StringComparison.OrdinalIgnoreCase) ||
                      state.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase)) ||
                    !state.SetFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(CachePath)) return;
            using var file = File.OpenRead(CachePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var loaded = JsonSerializer.Deserialize<List<SalesPurchaseSourceCacheEntry>>(gzip) ?? [];
            foreach (var entry in loaded)
            {
                if (string.IsNullOrWhiteSpace(entry.Path)) continue;
                var key = BuildKey(entry.Path, NormalizeModule(entry.Module), NormalizeGstin(entry.Gstin), NormalizeFinancialYear(entry.FinancialYear));
                Entries[key] = entry;
            }
        }
        catch
        {
            Entries.Clear();
        }
    }

    private static void QueuePersist()
    {
        if (Interlocked.Exchange(ref _persistScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            long persistedVersion = -1;
            try
            {
                await Task.Delay(750).ConfigureAwait(false);
                lock (Gate)
                {
                    PruneMissingUnsafe();
                    PersistUnsafe();
                    persistedVersion = Volatile.Read(ref _persistVersion);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _persistScheduled, 0);
                if (persistedVersion != Volatile.Read(ref _persistVersion)) QueuePersist();
            }
        });
    }

    private static void PersistUnsafe()
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var temp = CachePath + ".tmp";
            using (var file = File.Create(temp))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, Entries.Values.OrderByDescending(entry => entry.IndexedUtc).Take(20000).ToArray());
            File.Move(temp, CachePath, true);
        }
        catch
        {
            // Read-only acceleration only. Failure never blocks the normal import path.
        }
    }

    private static void PruneMissingUnsafe()
    {
        if (Entries.Count == 0) return;
        foreach (var key in Entries
                     .Where(pair => !File.Exists(pair.Value.Path))
                     .Select(pair => pair.Key)
                     .Take(500)
                     .ToArray())
            Entries.Remove(key);
    }

    private static void EnsureHandledSetsLoadedUnsafe()
    {
        if (_handledSetsLoaded) return;
        _handledSetsLoaded = true;
        try
        {
            if (!File.Exists(HandledSetsPath)) return;
            using var file = File.OpenRead(HandledSetsPath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var loaded = JsonSerializer.Deserialize<List<SalesPurchaseHandledSetState>>(gzip) ?? [];
            foreach (var state in loaded)
            {
                var key = BuildHandledSetKey(state.Module, state.Gstin, state.FinancialYear, state.OwnerClientId);
                if (key.Length == 0 || string.IsNullOrWhiteSpace(state.SetFingerprint)) continue;
                HandledSets[key] = state;
            }
        }
        catch
        {
            HandledSets.Clear();
        }
    }

    private static void PersistHandledSetsUnsafe()
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var temp = HandledSetsPath + ".tmp";
            using (var file = File.Create(temp))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, HandledSets.Values.OrderByDescending(state => state.HandledUtc).Take(5000).ToArray());
            File.Move(temp, HandledSetsPath, true);
        }
        catch
        {
            // Optimization state only. Failure never changes accounting correctness.
        }
    }

    private static string GetSetFingerprintUnsafe(string normalizedModule, string normalizedGstin, string normalizedFy)
    {
        var selected = Entries.Values
            .Where(entry => entry.Module.Equals(normalizedModule, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.Gstin.Equals(normalizedGstin, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.FinancialYear.Length == 0 || entry.FinancialYear.Equals(normalizedFy, StringComparison.OrdinalIgnoreCase))
            .Where(entry => File.Exists(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.FileHash, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selected.Length == 0) return string.Empty;

        // Fix39 duplicate authority is source CONTENT, not location/timestamp. A touched,
        // renamed or copied duplicate with the same payload must not re-enter Auto Import.
        // SourceRole keeps Purchase 2A/2B semantics distinct while duplicate copies of the
        // same role+content collapse to one identity.
        var identities = selected
            .Select(entry => BuildContentIdentity(entry))
            .Where(identity => identity.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (identities.Length == 0) return string.Empty;

        var builder = new StringBuilder();
        foreach (var identity in identities) builder.AppendLine(identity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string BuildContentIdentity(SalesPurchaseSourceCacheEntry entry)
    {
        var role = NormalizeSourceRole(entry.Module, entry.SourceRole, entry.Path);
        var hash = (entry.FileHash ?? string.Empty).Trim().ToUpperInvariant();
        if (hash.Length > 0) return $"{role}|{hash}|{entry.Size}";

        // Hash should normally be available. If it is not, stay conservative: do not
        // collapse two unproved files merely because their size happens to match.
        string path;
        try { path = Path.GetFullPath(entry.Path).Trim().ToUpperInvariant(); }
        catch { path = (entry.Path ?? string.Empty).Trim().ToUpperInvariant(); }
        return $"{role}|UNPROVED|{path}|{entry.Size}|{entry.LastWriteUtc.ToUniversalTime().Ticks}";
    }

    private static string NormalizeSourceRole(string module, string? sourceRole, string? path)
    {
        var explicitRole = (sourceRole ?? string.Empty).Trim().ToUpperInvariant();
        if (explicitRole.Length > 0) return explicitRole;

        var text = (path ?? string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase)) return "GSTR1";
        if (text.Contains("GSTR2B", StringComparison.Ordinal)) return "GSTR2B";
        if (text.Contains("GSTR2A", StringComparison.Ordinal)) return "GSTR2A";
        return "PURCHASE";
    }

    private static string BuildHandledSetKey(string module, string gstin, string financialYear, long ownerClientId = 0)
    {
        var normalizedModule = NormalizeModule(module);
        var normalizedGstin = NormalizeGstin(gstin);
        var normalizedFy = NormalizeFinancialYear(financialYear);
        if (normalizedModule.Length == 0 || normalizedGstin.Length != 15 || normalizedFy.Length == 0)
            return string.Empty;
        // Preserve existing V1 keys for normal clients. Separate branch books
        // must not inherit the parent's already-handled marker for the same GSTIN.
        var oldKey = $"{normalizedModule}|{normalizedGstin}|{normalizedFy}";
        return ownerClientId > 0 ? oldKey + "|CLIENT:" + ownerClientId : oldKey;
    }

    private static string BuildKey(string path, string module, string gstin, string financialYear) =>
        $"{module}|{gstin}|{financialYear}|{Path.GetFullPath(path)}";

    private static string NormalizeModule(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Equals("Sales", StringComparison.OrdinalIgnoreCase)) return "Sales";
        if (normalized.Equals("Purchase", StringComparison.OrdinalIgnoreCase)) return "Purchase";
        return string.Empty;
    }

    private static string NormalizeGstin(string? value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalizeFinancialYear(string? value)
    {
        var text = (value ?? string.Empty).Trim().Replace('_', '-').Replace('/', '-');
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(?<start>20\d{2})\s*-\s*(?<end>\d{2}|20\d{2})(?!\d)");
        if (!match.Success) return text;
        var start = match.Groups["start"].Value;
        var end = match.Groups["end"].Value;
        if (end.Length == 4) end = end[^2..];
        return $"{start}-{end}";
    }
}

public sealed class SalesPurchaseSourceCacheEntry
{
    public string Path { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string SourceRole { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime IndexedUtc { get; set; }
}

public sealed class SalesPurchaseHandledSetState
{
    public long OwnerClientId { get; set; }
    public string Module { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string SetFingerprint { get; set; } = string.Empty;
    public string AccountingSourceFingerprint { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime HandledUtc { get; set; }
}
