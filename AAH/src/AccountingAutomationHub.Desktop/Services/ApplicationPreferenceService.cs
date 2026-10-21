using System.Collections.Concurrent;
using AccountingAutomationHub.Application.Settings;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class ApplicationPreferenceService
{
    private readonly IApplicationPreferenceRepository _repository;
    private readonly ConcurrentDictionary<string, string> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public ApplicationPreferenceService(IApplicationPreferenceRepository repository)
    {
        using var startupMeasurement = ApplicationPerformanceTrace.Measure("Startup.ApplicationPreferences");
        _repository = repository;
        _repository.SettingChanged += OnRepositorySettingChanged;
        _repository.Initialize();

        // Startup previously opened SQLite once for every known setting and then again for
        // every default write. Apply defaults in one transaction and load the full settings
        // snapshot once; normal reads are memory-only after this point.
        _repository.EnsureDefaults(ApplicationSettingKeys.Defaults);
        foreach (var pair in _repository.GetAll())
            _cache[pair.Key] = pair.Value;
    }

    private void OnRepositorySettingChanged(string key, string value) =>
        _cache[key] = value;

    public string Get(string key)
    {
        if (_cache.TryGetValue(key, out var value) && value.Length > 0)
            return value;
        return ApplicationSettingKeys.Defaults.TryGetValue(key, out var fallback)
            ? fallback
            : string.Empty;
    }

    public bool GetBool(string key) =>
        bool.TryParse(Get(key), out var value) && value;

    public int GetInt(string key, int fallback)
    {
        var raw = Get(key);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    public void Set(string key, string value)
    {
        var normalized = value ?? string.Empty;
        _repository.Set(key, normalized);
        _cache[key] = normalized.Trim();
    }

    public void Set(string key, bool value) =>
        Set(key, value ? "True" : "False");

    public void Set(string key, int value) =>
        Set(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public IReadOnlyDictionary<string, string> ExportKnownSettings() =>
        ApplicationSettingKeys.Defaults.Keys.ToDictionary(
            key => key,
            Get,
            StringComparer.OrdinalIgnoreCase);

    public void ImportKnownSettings(IReadOnlyDictionary<string, string> values)
    {
        var accepted = values
            .Where(pair => ApplicationSettingKeys.Defaults.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        if (accepted.Count > 0)
        {
            _repository.SetMany(accepted);
            foreach (var pair in accepted)
                _cache[pair.Key] = pair.Value.Trim();
        }

        _repository.EnsureDefaults(ApplicationSettingKeys.Defaults);
        foreach (var pair in _repository.GetAll())
            _cache[pair.Key] = pair.Value;
    }

    public void RestoreDefaults()
    {
        _repository.SetMany(ApplicationSettingKeys.Defaults);
        foreach (var pair in ApplicationSettingKeys.Defaults)
            _cache[pair.Key] = pair.Value;
    }
}
