using System.Text;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Durable, human-readable Auto Import trace. The database remains the accounting
/// source of truth; this log is only for diagnostics when a watcher/classifier/
/// background worker problem needs to be inspected outside the application.
/// Logging failures are deliberately swallowed so they can never break imports.
/// </summary>
internal static class AutomationDiagnosticLog
{
    private static readonly object Gate = new();
    public static string LogFolder => Path.Combine(ApplicationDataPaths.RootFolder, "Logs");
    public static string LogPath => Path.Combine(LogFolder, "AutoImport.log");

    public static void Write(string stage, string message, string? path = null)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{stage}] {message}";
            if (!string.IsNullOrWhiteSpace(path)) line += $" | {path}";
            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never become an import failure.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath)) return;
        var info = new FileInfo(LogPath);
        if (info.Length < 5 * 1024 * 1024) return;
        var archive = Path.Combine(LogFolder, $"AutoImport-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.Move(LogPath, archive, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(LogFolder, "AutoImport-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(5))
        {
            try { File.Delete(old); } catch { }
        }
    }
}
