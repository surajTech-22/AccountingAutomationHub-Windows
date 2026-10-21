using System.IO;
using System.Text.Json;

namespace AccountingAutomationHub.NetworkClient;

internal sealed class NetworkClientSettings
{
    public string Server { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public List<FolderLinkConfig> FolderLinks { get; set; } = [];

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Accounting Automation Hub", "NetworkClient");
    private static string PathName => Path.Combine(Folder, "settings.json");

    public static NetworkClientSettings Load()
    {
        try { return File.Exists(PathName) ? JsonSerializer.Deserialize<NetworkClientSettings>(File.ReadAllText(PathName)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class FolderLinkConfig
{
    public string LinkId { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string LocalFolder { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
