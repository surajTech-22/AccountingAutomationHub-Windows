using System.Collections.Concurrent;
using System.Text;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Lightweight runtime visibility for the import -> persistence -> refresh pipeline.
/// It intentionally records metadata/timing only; accounting values are not copied.
/// </summary>
internal static class PipelineRuntimeDiagnostics
{
    private sealed record State(DateTime QueuedUtc, DateTime? StartedUtc, string Path);
    private static readonly ConcurrentDictionary<string, State> Imports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> RefreshRequested = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> RefreshCoalesced = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> RefreshStarted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> RefreshDurationMs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> LiveNotifications = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> RefreshFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> DashboardProjectionFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileGate = new();
    private static DateTime _lastWriteUtc = DateTime.MinValue;

    public static string ReportPath
    {
        get
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Accounting Automation Hub", "Diagnostics");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "AAH_PIPELINE_RUNTIME_REPORT.txt");
        }
    }

    private static string Key(long clientId, string fy) => $"{clientId}|{fy}";
    private static string ImportKey(long clientId, string fy, string path) => $"{Key(clientId, fy)}|{path}";

    public static void ImportWaiting(long clientId, string fy, string path) =>
        Imports[ImportKey(clientId, fy, path)] = new State(DateTime.UtcNow, null, path);

    public static void ImportStarted(long clientId, string fy, string path)
    {
        var key = ImportKey(clientId, fy, path);
        Imports.AddOrUpdate(key,
            _ => new State(DateTime.UtcNow, DateTime.UtcNow, path),
            (_, old) => old with { StartedUtc = DateTime.UtcNow });
    }

    public static void ImportFinished(long clientId, string fy, string path) => Imports.TryRemove(ImportKey(clientId, fy, path), out _);
    public static void RefreshQueued(long clientId, string fy) => RefreshRequested.AddOrUpdate(Key(clientId, fy), 1, (_, v) => v + 1);
    public static void RefreshMerged(long clientId, string fy) => RefreshCoalesced.AddOrUpdate(Key(clientId, fy), 1, (_, v) => v + 1);
    public static void RefreshBegin(long clientId, string fy) => RefreshStarted[Key(clientId, fy)] = DateTime.UtcNow;
    public static void RefreshEnd(long clientId, string fy)
    {
        var key = Key(clientId, fy);
        if (RefreshStarted.TryRemove(key, out var started))
            RefreshDurationMs[key] = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds);
    }
    public static void LiveNotification(long clientId, string fy, string module) =>
        LiveNotifications.AddOrUpdate($"{Key(clientId, fy)}|{module}", 1, (_, v) => v + 1);

    public static void RefreshFailed(long clientId, string fyModule, Exception exception)
    {
        var key = $"{clientId}|{fyModule}";
        RefreshFailures[key] = $"{DateTime.UtcNow:O} | {exception.GetType().Name}: {exception.Message}";
        WriteReportIfDue();
    }

    public static void DashboardProjectionFailed(long clientId, string fy, string module, Exception exception)
    {
        var key = $"{Key(clientId, fy)}|{module}";
        DashboardProjectionFailures[key] = $"{DateTime.UtcNow:O} | {exception.GetType().Name}: {exception.Message}";
        WriteReportIfDue();
    }

    public static void NavigationFailed(string page, Exception exception)
    {
        // Capture the full exception and stack rather than only the outer message.
        // This is local diagnostic metadata; no voucher or accounting values are copied.
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(ReportPath)!, "AAH_NAVIGATION_FAILURES.log");
            lock (FileGate)
                File.AppendAllText(path, $"{DateTime.UtcNow:O} | {page}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* Diagnostic I/O must not bring down the desktop. */ }
    }

    public static void WriteReportIfDue(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastWriteUtc < TimeSpan.FromSeconds(10)) return;
        lock (FileGate)
        {
            if (!force && DateTime.UtcNow - _lastWriteUtc < TimeSpan.FromSeconds(10)) return;
            _lastWriteUtc = DateTime.UtcNow;
            var now = DateTime.UtcNow;
            var sb = new StringBuilder();
            sb.AppendLine("Accounting Automation Hub - Import / Refresh Runtime Diagnostic");
            sb.AppendLine($"Generated UTC: {now:O}");
            sb.AppendLine("Purpose: identify imports waiting for execution, imports started but not completed, repeated/coalesced refresh calls and duplicate live notifications.");
            sb.AppendLine();
            sb.AppendLine("IN-FLIGHT / HALF-COMPLETED IMPORT CALLS");
            var inflight = Imports.OrderBy(x => x.Value.QueuedUtc).ToArray();
            if (inflight.Length == 0) sb.AppendLine("None.");
            foreach (var row in inflight)
            {
                var state = row.Value;
                var status = state.StartedUtc.HasValue ? "STARTED_NOT_FINISHED" : "WAITING_FOR_CLIENT_GATE";
                var age = now - (state.StartedUtc ?? state.QueuedUtc);
                sb.AppendLine($"{status} | age={age.TotalSeconds:0.0}s | file={Path.GetFileName(state.Path)} | key={row.Key.Split('|')[0]}|{row.Key.Split('|')[1]}");
            }
            sb.AppendLine();
            sb.AppendLine("REFRESH CALLS / REPEATED CALLS");
            foreach (var key in RefreshRequested.Keys.Union(RefreshCoalesced.Keys).OrderBy(x => x))
            {
                RefreshRequested.TryGetValue(key, out var requested);
                RefreshCoalesced.TryGetValue(key, out var merged);
                RefreshDurationMs.TryGetValue(key, out var duration);
                sb.AppendLine($"clientFY={key} requested={requested} coalescedRepeated={merged} lastDurationMs={duration}");
            }
            if (RefreshRequested.IsEmpty && RefreshCoalesced.IsEmpty) sb.AppendLine("No refresh calls recorded yet.");
            sb.AppendLine();
            sb.AppendLine("LIVE DATA NOTIFICATIONS");
            foreach (var row in LiveNotifications.OrderBy(x => x.Key)) sb.AppendLine($"{row.Key} calls={row.Value}");
            if (LiveNotifications.IsEmpty) sb.AppendLine("No live notifications recorded yet.");
            sb.AppendLine();
            sb.AppendLine("MODULE REFRESH FAILURES");
            foreach (var row in RefreshFailures.OrderBy(x => x.Key)) sb.AppendLine($"{row.Key} | {row.Value}");
            if (RefreshFailures.IsEmpty) sb.AppendLine("None.");
            sb.AppendLine();
            sb.AppendLine("DASHBOARD PROJECTION FAILURES");
            foreach (var row in DashboardProjectionFailures.OrderBy(x => x.Key)) sb.AppendLine($"{row.Key} | {row.Value}");
            if (DashboardProjectionFailures.IsEmpty) sb.AppendLine("None.");
            sb.AppendLine();
            sb.AppendLine("INTERPRETATION");
            sb.AppendLine("WAITING_FOR_CLIENT_GATE: another import for the same Client/FY is committing; this prevents mixed repository writes.");
            sb.AppendLine("STARTED_NOT_FINISHED: import entered the processor but has not returned; long ages identify a bottleneck or blocked dependency.");
            sb.AppendLine("coalescedRepeated: refresh was requested while the same Client/FY refresh was already running; it was merged into one follow-up instead of starting another database snapshot.");
            sb.AppendLine("High live-notification counts relative to imported files indicate repeated publishers/subscribers worth reviewing.");
            sb.AppendLine("MODULE REFRESH FAILURES are authoritative repository/cache reads that failed; the cache retries them and never treats the failure as an empty module.");
            sb.AppendLine("DASHBOARD PROJECTION FAILURES are presentation-only metric errors; the underlying module is still published so pages do not remain stuck at 0/preparing.");
            File.WriteAllText(ReportPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
