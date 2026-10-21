using System;
using System.Collections.Generic;

namespace AccountingAutomationHub.Application.Settings;

public interface IApplicationPreferenceRepository
{
    event Action<string, string>? SettingChanged;

    void Initialize();

    string Get(string settingKey);

    IReadOnlyDictionary<string, string> GetAll();

    void Set(
        string settingKey,
        string settingValue);

    void SetMany(IReadOnlyDictionary<string, string> values);

    void EnsureDefaults(IReadOnlyDictionary<string, string> defaults);
}
