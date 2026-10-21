using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.Services;

public static class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValue = "Accounting Automation Hub";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            key?.DeleteValue(AppValue, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        key?.SetValue(AppValue, $"\"{executable}\" --background");
    }
}
