using System.Diagnostics;
using System.IO;
using System.Text;

namespace AccountingAutomationHub.Desktop.Services;

public static class NetworkClientStartupLog
{
    private static readonly object Gate = new();

    public static string LogRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Accounting Automation Hub LAN");

    public static string LogPath => Path.Combine(LogRoot, "AAH-LAN-STARTUP.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(LogRoot);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(LogPath, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Startup diagnostics must never become a startup dependency.
        }
    }

    public static void WriteProcessStart()
    {
        var version = typeof(NetworkClientStartupLog).Assembly.GetName().Version?.ToString(4) ?? "unknown";
        var exe = Environment.ProcessPath ?? string.Empty;
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        Write($"Process start. Version={version}; PID={Environment.ProcessId}; User={user}; Path={exe}; Args={string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
    }

    public static void WriteException(string stage, Exception exception)
    {
        Write($"{stage} FAILED: {exception.GetType().Name}: {exception.Message}");
        try
        {
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                Write($"{stage} STACK: {exception.StackTrace.Replace(Environment.NewLine, " | ")}");
        }
        catch { }
    }
}
