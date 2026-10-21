using System.Globalization;
using System.Text;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class NotificationHistoryRow
{
    public DateTime Timestamp { get; init; }
    public string Client { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public static class NotificationHistoryService
{
    private static readonly object Gate = new();
    public static event EventHandler? Changed;
    private static string PathValue => System.IO.Path.Combine(System.IO.Path.Combine(ApplicationDataPaths.RootFolder, "Logs"), "notification-history.tsv");

    public static void Record(string client, string module, string type, string title, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.Combine(ApplicationDataPaths.RootFolder, "Logs"));
                var line = string.Join('\t',
                    DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                    Clean(client), Clean(module), Clean(type), Clean(title), Clean(message));
                File.AppendAllText(PathValue, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch { }
        RaiseChanged();
    }

    public static IReadOnlyList<NotificationHistoryRow> Read(int maxRows = 1000)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(PathValue)) return [];
                var parsed = File.ReadLines(PathValue)
                    .Reverse()
                    .Select(Parse)
                    .Where(row => row is not null)
                    .Cast<NotificationHistoryRow>();

                // Action Required is a state, not a repeating alarm. Older Auto Import builds
                // could write the same unresolved action again after watcher/startup recovery
                // scans. Collapse exact repeated Action Required rows in the visible history
                // while leaving normal informational/security event chronology untouched.
                var seenOpenActionNotifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var visible = new List<NotificationHistoryRow>();
                foreach (var row in parsed)
                {
                    if (row.Type.Equals("Action Required", StringComparison.OrdinalIgnoreCase))
                    {
                        // Older builds attributed Auto Import background notifications to whichever
                        // workspace happened to be open. For that known title, Client is therefore
                        // not a trustworthy part of the historical duplicate key; the message itself
                        // contains the source Client/file identity. New records carry the correct Client.
                        var key = row.Title.Equals("Auto Import needs Client verification", StringComparison.OrdinalIgnoreCase)
                            ? string.Join("|", row.Type.Trim(), row.Title.Trim(), row.Message.Trim())
                            : string.Join("|", row.Client.Trim(), row.Module.Trim(), row.Type.Trim(), row.Title.Trim(), row.Message.Trim());
                        if (!seenOpenActionNotifications.Add(key)) continue;
                    }
                    visible.Add(row);
                    if (visible.Count >= Math.Max(1, maxRows)) break;
                }
                return visible;
            }
        }
        catch { return []; }
    }

    public static void Clear()
    {
        try { lock (Gate) { if (File.Exists(PathValue)) File.Delete(PathValue); } } catch { }
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        var handler=Changed;if(handler is null)return;
        var dispatcher=System.Windows.Application.Current?.Dispatcher;
        if(dispatcher is null||dispatcher.CheckAccess())handler(null,EventArgs.Empty);
        else _=dispatcher.BeginInvoke(new Action(()=>handler(null,EventArgs.Empty)));
    }

    private static NotificationHistoryRow? Parse(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 6 || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)) return null;
        return new NotificationHistoryRow { Timestamp = timestamp, Client = parts[1], Module = parts[2], Type = parts[3], Title = parts[4], Message = parts[5] };
    }

    private static string Clean(string? value) => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
