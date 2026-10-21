using System.Globalization;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class ClientSchedulePreferenceService
{
    private readonly ApplicationPreferenceService _settings;
    public ClientSchedulePreferenceService(ApplicationPreferenceService settings) => _settings = settings;

    private static string Scope(long clientId, string financialYear) => $"ClientSchedule.{clientId}.{financialYear.Trim()}";
    private string Key(long clientId, string financialYear, string suffix) => $"{Scope(clientId, financialYear)}.{suffix}";

    public bool GetOverrideAdvancedRules(long clientId, string financialYear) =>
        _settings.GetBool(Key(clientId, financialYear, "OverrideAdvancedRules"));

    public void SetOverrideAdvancedRules(long clientId, string financialYear, bool value) =>
        _settings.Set(Key(clientId, financialYear, "OverrideAdvancedRules"), value);

    public IReadOnlyList<DateTime> GetManualHolidays(long clientId, string financialYear)
    {
        var raw = _settings.Get(Key(clientId, financialYear, "ManualHolidays"));
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Date : (DateTime?)null)
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .Distinct()
            .OrderBy(date => date)
            .ToArray();
    }

    public void SaveManualHolidays(long clientId, string financialYear, IEnumerable<DateTime> dates) =>
        _settings.Set(Key(clientId, financialYear, "ManualHolidays"), string.Join(';', dates.Select(date => date.Date).Distinct().OrderBy(date => date).Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));

    public void AddManualHoliday(long clientId, string financialYear, DateTime date)
    {
        var dates = GetManualHolidays(clientId, financialYear).Append(date.Date).Distinct().OrderBy(value => value).ToArray();
        SaveManualHolidays(clientId, financialYear, dates);
    }

    public void RemoveManualHoliday(long clientId, string financialYear, DateTime date)
    {
        var dates = GetManualHolidays(clientId, financialYear).Where(value => value.Date != date.Date).ToArray();
        SaveManualHolidays(clientId, financialYear, dates);
    }
}
