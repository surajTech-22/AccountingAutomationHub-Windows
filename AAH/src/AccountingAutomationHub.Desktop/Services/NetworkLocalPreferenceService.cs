using System.Text.Json;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// LAN-only user preferences. These are deliberately outside the disposable accounting snapshot,
/// so Main Server data refreshes cannot overwrite local appearance/startup/notification choices.
/// </summary>
public static class NetworkLocalPreferenceService
{
    private static string FilePath => Path.Combine(NetworkClientRuntime.RootFolder, "Preferences", "network-local-settings.json");

    public static NetworkLocalPreferences Load()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return new NetworkLocalPreferences();
        try
        {
            if (!File.Exists(FilePath)) return new NetworkLocalPreferences();
            return JsonSerializer.Deserialize<NetworkLocalPreferences>(File.ReadAllText(FilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive=true }) ?? new NetworkLocalPreferences();
        }
        catch { return new NetworkLocalPreferences(); }
    }

    public static void Save(NetworkLocalPreferences value)
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp=FilePath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,FilePath,true);
    }


    public static void SetRemoteAssistPreference(string key,string value)
    {
        if(!NetworkClientRuntime.IsNetworkClient)throw new InvalidOperationException("Remote Assist local preferences are available only in the LAN application.");
        var p=Load();switch((key??string.Empty).Trim())
        {
            case nameof(NetworkLocalPreferences.StartWithWindows): p.StartWithWindows=ParseBool(value); break;
            case nameof(NetworkLocalPreferences.CloseToTray): p.CloseToTray=ParseBool(value); break;
            case nameof(NetworkLocalPreferences.NotificationsEnabled): p.NotificationsEnabled=ParseBool(value); break;
            case nameof(NetworkLocalPreferences.RememberWindowPlacement): p.RememberWindowPlacement=ParseBool(value); break;
            case nameof(NetworkLocalPreferences.Theme): p.Theme=(value??string.Empty).Trim(); break;
            case nameof(NetworkLocalPreferences.AccentColor): p.AccentColor=(value??string.Empty).Trim(); break;
            case nameof(NetworkLocalPreferences.GridDensity): p.GridDensity=(value??string.Empty).Trim(); break;
            default: throw new InvalidOperationException("That local preference is not allow-listed for Remote Assist.");
        }
        Save(p);
    }

    private static bool ParseBool(string value)=>bool.TryParse(value,out var parsed)&&parsed;

    public static void ApplyTo(ApplicationPreferenceService settings)
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        var p=Load();
        settings.Set(ApplicationSettingKeys.StartWithWindows,p.StartWithWindows);
        settings.Set(ApplicationSettingKeys.CloseToTray,p.CloseToTray);
        settings.Set(ApplicationSettingKeys.NotificationsEnabled,p.NotificationsEnabled);
        settings.Set(ApplicationSettingKeys.Theme,p.Theme);
        settings.Set(ApplicationSettingKeys.AccentColor,p.AccentColor);
        settings.Set(ApplicationSettingKeys.GridDensity,p.GridDensity);
        settings.Set(ApplicationSettingKeys.RememberWindowPlacement,p.RememberWindowPlacement);
        settings.Set(ApplicationSettingKeys.ShortcutHelp,p.ShortcutHelp);
        settings.Set(ApplicationSettingKeys.ShortcutRefresh,p.ShortcutRefresh);
        settings.Set(ApplicationSettingKeys.ShortcutSave,p.ShortcutSave);
        settings.Set(ApplicationSettingKeys.ShortcutClientSearch,p.ShortcutClientSearch);
        settings.Set(ApplicationSettingKeys.ShortcutNewClient,p.ShortcutNewClient);
    }
}

public sealed class NetworkLocalPreferences
{
    public bool StartWithWindows{get;set;}
    public bool CloseToTray{get;set;}
    public bool NotificationsEnabled{get;set;}=true;
    public bool MessageNotificationsEnabled{get;set;}=true;
    public bool WorkNotificationsEnabled{get;set;}=true;
    public string Theme{get;set;}="Light";
    public string AccentColor{get;set;}="#2563EB";
    public string GridDensity{get;set;}="Comfortable";
    public bool RememberWindowPlacement{get;set;}=true;
    public string ShortcutHelp{get;set;}="Ctrl+H";
    public string ShortcutRefresh{get;set;}="Ctrl+R";
    public string ShortcutSave{get;set;}="Ctrl+S";
    public string ShortcutClientSearch{get;set;}="Ctrl+K";
    public string ShortcutNewClient{get;set;}="Ctrl+N";
}
