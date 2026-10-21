using System.Diagnostics;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Infrastructure.Persistence
{
    internal static class ApplicationDataPaths
    {
        public static string DatabasePath { get; set; } = string.Empty;
        public static string AuditDatabasePath { get; set; } = string.Empty;
    }
}

namespace AccountingAutomationHub.Desktop.Services
{
    internal static class PipelineRuntimeDiagnostics
    {
        public static void NavigationFailed(string stage, Exception exception) =>
            Console.Error.WriteLine(stage + ": " + exception.Message);
    }
}

internal sealed class TestDocument
{
    public TestDocument() { }
    public string Value { get; set; } = string.Empty;
    public List<string> Rows { get; set; } = [];
}

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "AAH-CacheLifecycle-" + Guid.NewGuid().ToString("N"));
    private static int _passed;

    private static async Task Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            await Run("Cold preparation and isolated presentation copies", ColdAndCopies);
            await Run("Page reopen reuses the current projection", ReopenDoesNotReload);
            await Run("A newer edit defeats an older running job", NewerGenerationWins);
            await Run("Failed reads retain the last good snapshot", FailureRetainsSnapshot);
            await Run("Restart restores the latest committed snapshot", RestartRestores);
            await Run("Interrupted preparation is recoverable", InterruptedPreparation);
            await Run("Client/FY partitions remain independent", IndependentPartitions);
            await Run("External source change invalidates stale authority", ExternalSourceChange);
            await Run("Source changes during a read retry at the new generation", SourceChangesDuringRead);
            await Run("A failing notification cannot stop preparation", SubscriberIsolation);
            await Run("Source monitoring stops independently of a page", SourceMonitorLifecycle);
            await Run("Closed presentation continues receiving committed source changes", ClosedPresentationKeepsCurrent);
            Console.WriteLine($"PASS: {_passed}/12 cache lifecycle scenarios.");
        }
        finally
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static async Task Run(string title, Func<Task> test)
    {
        await test();
        Interlocked.Increment(ref _passed);
        Console.WriteLine("PASS: " + title);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<bool> condition, string message, int milliseconds = 8000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < milliseconds)
            await Task.Delay(20);
        Check(condition(), message);
    }

    private static TestDocument Document(string value) => new() { Value = value, Rows = [value] };

    private static async Task ColdAndCopies()
    {
        var folder = Path.Combine(Root, "cold");
        using var cache = new PersistentProjectionCache<TestDocument>(folder, () => "source-1");
        cache.Register("1|2025-26|Stock", () => Document("original"));
        await Until(() => cache.IsAuthoritative("1|2025-26|Stock"), "Cold preparation did not finish.");
        Check(cache.TryGet("1|2025-26|Stock", out var first), "Missing prepared snapshot.");
        first.Rows.Add("presentation-only");
        Check(cache.TryGet("1|2025-26|Stock", out var second) && second.Rows.Count == 1,
            "A page mutated the shared authority.");
    }

    private static async Task ReopenDoesNotReload()
    {
        using var cache = new PersistentProjectionCache<TestDocument>(Path.Combine(Root, "reopen"), () => "source-1");
        var reads = 0;
        TestDocument Load() { Interlocked.Increment(ref reads); return Document("saved"); }
        cache.Register("1|2025-26|Masters", Load);
        await Until(() => cache.IsAuthoritative("1|2025-26|Masters"), "Initial load failed.");
        for (var i = 0; i < 20; i++) cache.Register("1|2025-26|Masters", Load, priority: true);
        await Task.Delay(150);
        Check(Volatile.Read(ref reads) == 1, "Opening the same page caused another repository read.");
    }

    private static async Task NewerGenerationWins()
    {
        using var cache = new PersistentProjectionCache<TestDocument>(Path.Combine(Root, "race"), () => "source-1");
        cache.Register("1|2025-26|Sales", () => Document("old"));
        await Until(() => cache.IsAuthoritative("1|2025-26|Sales"), "Initial load failed.");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        cache.Register("1|2025-26|Sales", () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); return Document("stale"); }, invalidate: true);
        await Until(() => started.IsSet, "Blocked loader did not start.");
        cache.Register("1|2025-26|Sales", () => Document("new"), invalidate: true);
        release.Set();
        await Until(() => cache.IsAuthoritative("1|2025-26|Sales") && cache.TryGet("1|2025-26|Sales", out var value) && value.Value == "new",
            "An older job replaced a newer committed generation.");
    }

    private static async Task FailureRetainsSnapshot()
    {
        using var cache = new PersistentProjectionCache<TestDocument>(Path.Combine(Root, "failure"), () => "source-1");
        cache.Register("1|2025-26|Stock", () => Document("last-good"));
        await Until(() => cache.IsAuthoritative("1|2025-26|Stock"), "Initial load failed.");
        var attempts = 0;
        cache.Register("1|2025-26|Stock", () =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Simulated read failure");
            return Document("recovered");
        }, invalidate: true);
        await Until(() => Volatile.Read(ref attempts) > 0, "Failure was not exercised.");
        Check(cache.TryGet("1|2025-26|Stock", out var old) && old.Value == "last-good", "The failure erased the last good data.");
        await Until(() => cache.IsAuthoritative("1|2025-26|Stock") && cache.TryGet("1|2025-26|Stock", out var value) && value.Value == "recovered",
            "Automatic recovery failed.");
    }

    private static async Task RestartRestores()
    {
        var folder = Path.Combine(Root, "restart");
        using (var cache = new PersistentProjectionCache<TestDocument>(folder, () => "source-1"))
        {
            cache.Register("1|2025-26|Journal", () => Document("committed"));
            await Until(() => cache.IsAuthoritative("1|2025-26|Journal"), "Initial load failed.");
        }
        using var restored = new PersistentProjectionCache<TestDocument>(folder, () => "source-1");
        var reads = 0;
        restored.Register("1|2025-26|Journal", () => { Interlocked.Increment(ref reads); return Document("unnecessary"); });
        await Until(() => restored.IsAuthoritative("1|2025-26|Journal"), "Saved cache was not restored.");
        Check(restored.TryGet("1|2025-26|Journal", out var value) && value.Value == "committed", "Restart lost the committed value.");
        Check(Volatile.Read(ref reads) == 0, "Restart unnecessarily rebuilt a valid cache.");
    }

    private static async Task InterruptedPreparation()
    {
        var folder = Path.Combine(Root, "interrupted");
        using var cache = new PersistentProjectionCache<TestDocument>(folder, () => "source-1");
        cache.Register("1|2025-26|Cash", () => Document("last-good"));
        await Until(() => cache.IsAuthoritative("1|2025-26|Cash"), "Initial load failed.");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        cache.Register("1|2025-26|Cash", () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); return Document("unfinished"); }, invalidate: true);
        await Until(() => started.IsSet, "Interrupted loader did not start.");
        await cache.StopAsync(TimeSpan.FromMilliseconds(100));
        release.Set();
        using var recovered = new PersistentProjectionCache<TestDocument>(folder, () => "source-1");
        recovered.Register("1|2025-26|Cash", () => Document("recovered"));
        await Until(() => recovered.IsAuthoritative("1|2025-26|Cash") && recovered.TryGet("1|2025-26|Cash", out var value) && value.Value == "recovered",
            "Interrupted work was not recovered on restart.");
    }

    private static async Task IndependentPartitions()
    {
        using var cache = new PersistentProjectionCache<TestDocument>(Path.Combine(Root, "partitions"), () => "source-1");
        cache.Register("1|2025-26|Stock", () => Document("client-one"));
        cache.Register("2|2025-26|Stock", () => Document("client-two"));
        await Until(() => cache.IsAuthoritative("1|2025-26|Stock") && cache.IsAuthoritative("2|2025-26|Stock"), "Both clients were not prepared.");
        cache.Register("1|2025-26|Stock", () => Document("client-one-new"), invalidate: true);
        await Until(() => cache.IsAuthoritative("1|2025-26|Stock") && cache.TryGet("1|2025-26|Stock", out var value) && value.Value == "client-one-new", "First client did not update.");
        Check(cache.TryGet("2|2025-26|Stock", out var other) && other.Value == "client-two", "A different Client/FY was overwritten.");
    }

    private static async Task ExternalSourceChange()
    {
        var source = "version-one";
        using var cache = new PersistentProjectionCache<TestDocument>(Path.Combine(Root, "external"), () => Volatile.Read(ref source));
        cache.Register("1|2025-26|Purchase", () => Document("old"));
        await Until(() => cache.IsAuthoritative("1|2025-26|Purchase"), "Initial load failed.");
        Volatile.Write(ref source, "version-two");
        cache.Register("1|2025-26|Purchase", () => Document("new"));
        await Until(() => cache.IsAuthoritative("1|2025-26|Purchase") && cache.TryGet("1|2025-26|Purchase", out var value) && value.Value == "new", "Source watermark did not invalidate the old projection.");
    }

    private static async Task SourceChangesDuringRead()
    {
        var source = "one";
        using var cache = new PersistentProjectionCache<TestDocument>(Path.Combine(Root, "source-race"), () => Volatile.Read(ref source));
        cache.Register("1|2025-26|Stock", () => Document("last-good"));
        await Until(() => cache.IsAuthoritative("1|2025-26|Stock"), "Initial load failed.");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        cache.Register("1|2025-26|Stock", () =>
        {
            var read = Interlocked.Increment(ref reads);
            if (read == 1) { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
            return Document(Volatile.Read(ref source));
        }, invalidate: true);
        await Until(() => started.IsSet, "Source-race loader did not start.");
        Volatile.Write(ref source, "two");
        release.Set();
        await Until(() => cache.IsAuthoritative("1|2025-26|Stock") &&
            cache.TryGet("1|2025-26|Stock", out var value) && value.Value == "two",
            "A source change stranded the cache on a rejected generation.");
        Check(Volatile.Read(ref reads) >= 2, "A source change did not retry the read.");
    }

    private static async Task SubscriberIsolation()
    {
        using var cache = new PersistentProjectionCache<TestDocument>(Path.Combine(Root, "subscriber"), () => "source-1");
        cache.Ready += (_, _) => throw new InvalidOperationException("Simulated UI listener failure");
        cache.Register("1|2025-26|GST", () => Document("ready"));
        await Until(() => cache.IsAuthoritative("1|2025-26|GST"), "A throwing listener blocked the cache worker.");
    }

    private static async Task SourceMonitorLifecycle()
    {
        var source = "one";
        var changes = 0;
        using var monitor = new WorkspaceSourceChangeMonitor(() => Volatile.Read(ref source), () => Interlocked.Increment(ref changes), TimeSpan.FromMilliseconds(30));
        await Task.Delay(100);
        Volatile.Write(ref source, "two");
        await Until(() => Volatile.Read(ref changes) > 0, "External source change was not detected.");
        await monitor.StopAsync(TimeSpan.FromSeconds(2));
        var before = Volatile.Read(ref changes);
        Volatile.Write(ref source, "three");
        await Task.Delay(100);
        Check(Volatile.Read(ref changes) == before, "Source monitoring continued after shutdown.");
    }
    private static async Task ClosedPresentationKeepsCurrent()
    {
        var source = "one";
        var folder = Path.Combine(Root, "closed-presentation");
        using var cache = new PersistentProjectionCache<TestDocument>(folder, () => Volatile.Read(ref source));
        var key = "1|2025-26|Masters";
        TestDocument Load() => Document(Volatile.Read(ref source));
        cache.Register(key, Load);
        await Until(() => cache.IsAuthoritative(key), "Initial closed-presentation load failed.");

        // No presentation TryGet/Refresh calls occur after this point. The application-owned
        // source watcher alone invalidates and rebuilds the projection after the commit.
        using var monitor = new WorkspaceSourceChangeMonitor(
            () => Volatile.Read(ref source),
            () => cache.Register(key, Load, invalidate: true, priority: true),
            TimeSpan.FromMilliseconds(30));
        await Task.Delay(100);
        Volatile.Write(ref source, "two");
        await Until(() => cache.IsAuthoritative(key) && cache.TryGet(key, out var current) && current.Value == "two",
            "A closed page was required for the projection to become current.");
    }

}
