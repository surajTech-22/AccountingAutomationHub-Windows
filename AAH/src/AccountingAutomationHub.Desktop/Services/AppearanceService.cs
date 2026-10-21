using System.Windows;
using System.Windows.Media;

namespace AccountingAutomationHub.Desktop.Services;

public static class AppearanceService
{
    public static void Apply(ApplicationPreferenceService settings)
    {
        var application = System.Windows.Application.Current;
        if (application is null) return;

        var theme = settings.Get(ApplicationSettingKeys.Theme);
        var dark = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        if (theme.Equals("System", StringComparison.OrdinalIgnoreCase))
            dark = IsWindowsDarkMode();

        SetBrush(application, "AppBackgroundBrush", dark ? "#111827" : "#F4F7FB");
        SetBrush(application, "SurfaceBrush", dark ? "#172033" : "#FFFFFF");
        SetBrush(application, "SidebarBrush", dark ? "#090F1C" : "#0F172A");
        SetBrush(application, "SidebarHoverBrush", dark ? "#1F2937" : "#1E293B");
        SetBrush(application, "TextPrimaryBrush", dark ? "#F8FAFC" : "#0F172A");
        SetBrush(application, "TextSecondaryBrush", dark ? "#A7B4C8" : "#64748B");
        SetBrush(application, "BorderBrush", dark ? "#334155" : "#E2E8F0");

        var accent = NormalizeColor(settings.Get(ApplicationSettingKeys.AccentColor), "#2563EB");
        var action = NormalizeColor(settings.Get(ApplicationSettingKeys.ActionRequiredColor), "#EF4444");
        var tallySelected = NormalizeColor(settings.Get(ApplicationSettingKeys.TallySelectedPeriodColor), "#16A34A");
        SetBrush(application, "AccentBrush", accent);
        SetBrush(application, "AccentSoftBrush", WithOpacity(accent, 0.15));
        SetBrush(application, "ActionRequiredBrush", action);
        SetBrush(application, "ActionRequiredSoftBrush", WithOpacity(action, 0.14));
        SetBrush(application, "TallySelectedPeriodBrush", WithOpacity(tallySelected, 0.24));
        SetBrush(application, "TallySelectedPeriodBorderBrush", tallySelected);
        application.Resources["GridRowHeight"] = settings.Get(ApplicationSettingKeys.GridDensity)
            .Equals("Compact", StringComparison.OrdinalIgnoreCase) ? 28d : 34d;
    }

    private static void SetBrush(System.Windows.Application app, string key, string colorText)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorText)!;
        app.Resources[key] = new SolidColorBrush(color);
    }

    private static string NormalizeColor(string value, string fallback)
    {
        try
        {
            _ = (Color)ColorConverter.ConvertFromString(value)!;
            return value;
        }
        catch
        {
            return fallback;
        }
    }

    private static string WithOpacity(string value, double opacity)
    {
        var color = (Color)ColorConverter.ConvertFromString(value)!;
        return $"#{(byte)Math.Clamp(opacity * 255d, 0d, 255d):X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int number && number == 0;
        }
        catch
        {
            return false;
        }
    }
}
