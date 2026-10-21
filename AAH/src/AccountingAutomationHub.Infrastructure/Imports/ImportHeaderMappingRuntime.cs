using System.Collections.Concurrent;
using System.Threading;

namespace AccountingAutomationHub.Infrastructure.Imports;

public sealed record ImportHeaderResolution(string CanonicalField, string MatchType)
{
    public bool IsMatched => CanonicalField.Length > 0;
}

public static class ImportHeaderMappingRuntime
{
    private sealed record OneShotMapping(string Module, IReadOnlyDictionary<string, string> ByNormalizedSource);
    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentSource.Value = previous;
        }
    }

    private static readonly object Sync = new();
    private static readonly SqliteGlobalHeaderMappingRepository Repository = new();
    private static readonly AsyncLocal<string?> CurrentSource = new();
    private static readonly ConcurrentDictionary<string, OneShotMapping> OneShot = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, Dictionary<string, string>> _globalByModule = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, Dictionary<string, List<string>>> _globalAliasesByCanonical = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static void Initialize() => EnsureLoaded();

    public static IDisposable BeginSource(string? sourcePath)
    {
        var previous = CurrentSource.Value;
        CurrentSource.Value = NormalizePath(sourcePath);
        return new Scope(previous);
    }

    public static void SetOneShotMapping(string sourcePath, string module, IReadOnlyDictionary<string, string> sourceToCanonical)
    {
        var path = NormalizePath(sourcePath);
        if (path.Length == 0) return;
        var normalizedModule = ImportHeaderMappingCatalog.NormalizeModule(module);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in sourceToCanonical)
        {
            var source = ImportHeaderMappingCatalog.NormalizeHeader(pair.Key);
            var canonical = pair.Value?.Trim() ?? string.Empty;
            if (source.Length == 0 || canonical.Length == 0 || ImportHeaderMappingCatalog.FindField(normalizedModule, canonical) is null) continue;
            map[source] = canonical;
        }
        if (map.Count == 0) OneShot.TryRemove(path, out _);
        else OneShot[path] = new OneShotMapping(normalizedModule, map);
    }

    public static void ClearOneShotMapping(string? sourcePath)
    {
        var path = NormalizePath(sourcePath);
        if (path.Length > 0) OneShot.TryRemove(path, out _);
    }

    public static ImportHeaderResolution ResolveDetailed(string module, string? sourceHeader)
    {
        var normalizedModule = ImportHeaderMappingCatalog.NormalizeModule(module);
        var normalized = ImportHeaderMappingCatalog.NormalizeHeader(sourceHeader);
        if (normalizedModule.Length == 0 || normalized.Length == 0) return new(string.Empty, "Unmatched");

        var source = CurrentSource.Value;
        if (!string.IsNullOrWhiteSpace(source) && OneShot.TryGetValue(source, out var oneShot) &&
            oneShot.Module.Equals(normalizedModule, StringComparison.OrdinalIgnoreCase) &&
            oneShot.ByNormalizedSource.TryGetValue(normalized, out var temporaryCanonical))
            return new(temporaryCanonical, "This file only");

        EnsureLoaded();
        lock (Sync)
        {
            if (_globalByModule.TryGetValue(normalizedModule, out var map) && map.TryGetValue(normalized, out var globalCanonical))
                return new(globalCanonical, "Global mapping");
        }

        var builtIn = ImportHeaderMappingCatalog.ResolveBuiltIn(normalizedModule, sourceHeader ?? string.Empty);
        return builtIn.Length > 0 ? new(builtIn, "Built-in") : new(string.Empty, "Unmatched");
    }

    public static string Resolve(string module, string? sourceHeader) => ResolveDetailed(module, sourceHeader).CanonicalField;

    public static IReadOnlyList<string> ExpandAliases(string module, string canonicalField, IEnumerable<string> builtInAliases)
    {
        EnsureLoaded();
        var values = new List<string>(builtInAliases.Where(value => !string.IsNullOrWhiteSpace(value)));
        var normalizedModule = ImportHeaderMappingCatalog.NormalizeModule(module);
        lock (Sync)
        {
            if (_globalAliasesByCanonical.TryGetValue(normalizedModule, out var canonicalMap) &&
                canonicalMap.TryGetValue(canonicalField, out var aliases)) values.AddRange(aliases);
        }
        var source = CurrentSource.Value;
        if (!string.IsNullOrWhiteSpace(source) && OneShot.TryGetValue(source, out var oneShot) && oneShot.Module.Equals(normalizedModule, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in oneShot.ByNormalizedSource.Where(pair => pair.Value.Equals(canonicalField, StringComparison.OrdinalIgnoreCase))) values.Add(pair.Key);
        }
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static GlobalHeaderMappingAddResult AddGlobalMapping(string module, string canonicalField, string sourceHeader)
    {
        var result = Repository.Add(module, canonicalField, sourceHeader);
        if (result.Added) Reload();
        return result;
    }

    public static bool IsRequiredComplete(string module, IEnumerable<string> canonicalFields) =>
        ImportHeaderMappingCatalog.RequiredFieldsSatisfied(module, canonicalFields);

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Sync)
        {
            if (_loaded) return;
            ReloadCore();
            _loaded = true;
        }
    }

    private static void Reload()
    {
        lock (Sync)
        {
            ReloadCore();
            _loaded = true;
        }
    }

    private static void ReloadCore()
    {
        Repository.Initialize();
        var byModule = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var byCanonical = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Repository.GetAll())
        {
            if (!byModule.TryGetValue(row.Module, out var sourceMap)) byModule[row.Module] = sourceMap = new(StringComparer.OrdinalIgnoreCase);
            sourceMap[row.NormalizedSourceHeader] = row.CanonicalField;
            if (!byCanonical.TryGetValue(row.Module, out var canonicalMap)) byCanonical[row.Module] = canonicalMap = new(StringComparer.OrdinalIgnoreCase);
            if (!canonicalMap.TryGetValue(row.CanonicalField, out var aliases)) canonicalMap[row.CanonicalField] = aliases = [];
            aliases.Add(row.SourceHeader);
        }
        _globalByModule = byModule;
        _globalAliasesByCanonical = byCanonical;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return path.Trim(); }
    }
}
