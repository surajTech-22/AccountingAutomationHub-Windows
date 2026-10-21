using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AccountingAutomationHub.Infrastructure.Persistence;

public static class ApplicationPerformanceTrace
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static int _flushScheduled;

    public static string LogPath => Path.Combine(ApplicationDataPaths.RootFolder, "Logs", "performance-trace.csv");

    public static IDisposable Measure(string operation, string detail = "") =>
        new Measurement(operation, detail);

    public static void Record(string operation, double durationMs, string detail = "")
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var line = string.Join(",", new[]
            {
                Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                Csv(operation),
                durationMs.ToString("0.0", CultureInfo.InvariantCulture),
                Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture),
                process.WorkingSet64.ToString(CultureInfo.InvariantCulture),
                process.PrivateMemorySize64.ToString(CultureInfo.InvariantCulture),
                process.Threads.Count.ToString(CultureInfo.InvariantCulture),
                Csv(detail)
            });
            Pending.Enqueue(line);
            ScheduleFlush();
        }
        catch { }
    }

    private static void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(80).ConfigureAwait(false);
                Flush();
            }
            finally
            {
                Interlocked.Exchange(ref _flushScheduled, 0);
                if (!Pending.IsEmpty) ScheduleFlush();
            }
        });
    }

    private static void Flush()
    {
        try
        {
            var folder = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(folder);
            var newFile = !File.Exists(LogPath);
            var builder = new StringBuilder();
            if (newFile) builder.AppendLine("Timestamp,Operation,DurationMs,ThreadId,WorkingSetBytes,PrivateBytes,ProcessThreads,Detail");
            while (Pending.TryDequeue(out var line)) builder.AppendLine(line);
            if (builder.Length > 0) File.AppendAllText(LogPath, builder.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    private static string Csv(string value)
    {
        var text = (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal);
        return "\"" + text + "\"";
    }

    private sealed class Measurement(string operation, string detail) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stopwatch.Stop();
            Record(operation, _stopwatch.Elapsed.TotalMilliseconds, detail);
        }
    }
}
