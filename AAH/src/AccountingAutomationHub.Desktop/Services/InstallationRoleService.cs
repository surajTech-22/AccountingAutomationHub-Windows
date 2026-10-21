using System.IO;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Immutable installation-channel identity. Main Server and LAN client are different
/// installed products even though they share the proven WPF application code.
/// A loose legacy network marker is accepted only as a migration fallback when this
/// role marker is absent.
/// </summary>
public static class InstallationRoleService
{
    public const string MarkerFileName = "AAH_INSTALLATION_ROLE";
    public const string MainServer = "MainServer";
    public const string LanClient = "LanClient";

    public static string ReadRole(string? baseDirectory = null)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory!;
            var path = Path.Combine(root, MarkerFileName);
            if (!File.Exists(path)) return string.Empty;
            var role = File.ReadAllText(path).Trim();
            if (role.Equals(MainServer, StringComparison.OrdinalIgnoreCase)) return MainServer;
            if (role.Equals(LanClient, StringComparison.OrdinalIgnoreCase)) return LanClient;
        }
        catch { }
        return string.Empty;
    }

    public static bool IsMainServerInstallation(string? baseDirectory = null) =>
        ReadRole(baseDirectory).Equals(MainServer, StringComparison.OrdinalIgnoreCase);

    public static bool IsLanClientInstallation(string? baseDirectory = null) =>
        ReadRole(baseDirectory).Equals(LanClient, StringComparison.OrdinalIgnoreCase);
}
