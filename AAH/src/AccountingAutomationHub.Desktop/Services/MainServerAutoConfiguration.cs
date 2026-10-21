using System.IO;
using System.Linq;
using System.Net;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// One-time migration for the designated Main Server PC. It makes the secure Phase 1
/// server usable without asking the operator to fill every core field manually while
/// deliberately leaving the off-machine/second backup destination user-selectable.
/// </summary>
public static class MainServerAutoConfiguration
{
    public const string CurrentVersion = "3";
    public const string DefaultServerRoot = @"C:\Accounting Automation Hub Server";
    public const int DefaultHttpsPort = 47831;
    public const int DefaultPcExpiryDays = 30;
    public const int DefaultAndroidExpiryDays = 15;
    public const int DefaultFolderLinkExpiryDays = 15;

    public static void Apply(ApplicationPreferenceService settings)
    {
        if (!OperatingSystem.IsWindows()) return;

        // This full desktop installation is the designated Main Server authority. Keep
        // its core server role and C-drive authority path deterministic on every startup.
        // The second/off-machine backup destination is deliberately never overwritten here.
        settings.Set(ApplicationSettingKeys.SecurityServerMode, true);
        settings.Set(ApplicationSettingKeys.SecurityLanEnabled, true);
        settings.Set(ApplicationSettingKeys.SecurityRequireMfa, true);
        settings.Set(ApplicationSettingKeys.SecurityServerRootFolder, DefaultServerRoot);

        // The Windows LAN application is built by the updater and lives only inside the
        // Main Server installation. Register it automatically so the Administrator sees
        // a ready-to-share LAN application without browsing for a package manually.
        var bundledNetworkClient = Path.Combine(AppContext.BaseDirectory, "NetworkClient", "AAHNetworkClient.zip");
        if (File.Exists(bundledNetworkClient))
            settings.Set(ApplicationSettingKeys.SecurityNetworkClientPackagePath, bundledNetworkClient);

        var configuredVersion = settings.Get(ApplicationSettingKeys.SecurityMainServerAutoConfigVersion).Trim();
        if (!configuredVersion.Equals(CurrentVersion, StringComparison.Ordinal))
        {
            settings.Set(ApplicationSettingKeys.SecurityLanPort, DefaultHttpsPort);
            settings.Set(ApplicationSettingKeys.SecurityPcExpiryDays, DefaultPcExpiryDays);
            settings.Set(ApplicationSettingKeys.SecurityAndroidExpiryDays, DefaultAndroidExpiryDays);
            settings.Set(ApplicationSettingKeys.SecurityFolderLinkExpiryDays, DefaultFolderLinkExpiryDays);

            // Preserve an explicitly chosen second/off-machine backup destination. It is
            // the only Main Server path the operator is expected to choose manually.
            settings.Set(ApplicationSettingKeys.SecurityLanBindAddress, SelectBestActivePrivateAddress(settings.Get(ApplicationSettingKeys.SecurityLanBindAddress)));
            settings.Set(ApplicationSettingKeys.SecurityMainServerAutoConfigVersion, CurrentVersion);
        }
        else
        {
            // Keep the Main Server usable after DHCP/VPN adapter changes. Preserve the
            // current private address only while it is still active; otherwise select the
            // best active office-LAN/private address automatically.
            var current = settings.Get(ApplicationSettingKeys.SecurityLanBindAddress).Trim();
            var selected = SelectBestActivePrivateAddress(current);
            if (!selected.Equals(current, StringComparison.OrdinalIgnoreCase))
                settings.Set(ApplicationSettingKeys.SecurityLanBindAddress, selected);
        }

        try { Directory.CreateDirectory(DefaultServerRoot); }
        catch { /* ApplySettingsAsync reports any real path/access problem in Settings diagnostics. */ }
    }

    private static string SelectBestActivePrivateAddress(string current)
    {
        var available = SecurityNetworkPolicy.GetPrivateBindAddresses()
            .Where(value => !value.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (available.Any(value => value.Equals(current?.Trim(), StringComparison.OrdinalIgnoreCase)))
            return current.Trim();

        return available
            .OrderBy(AddressPreference)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "127.0.0.1";
    }

    private static int AddressPreference(string value)
    {
        if (!IPAddress.TryParse(value, out var address)) return 99;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return 99;
        if (bytes[0] == 192 && bytes[1] == 168) return 0; // normal office LAN first
        if (bytes[0] == 10) return 1;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return 2;
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return 3; // private overlay / VPN
        return 50;
    }
}
