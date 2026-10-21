using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Process-owned, bounded, read-only projections. A window never owns a loader.
/// Every replacement is generation checked and the last good payload survives failures.
/// SQLite remains authoritative; cached objects are private presentation copies.
/// </summary>
public sealed class PersistentProjectionCache<T> : IDisposable where T : class
{
    private sealed class WorkState
    {
        public readonly object Gate = new();
        public Func<T>? Loader;
        public long Generation;
        public bool Queued, Running, Again, RestoreAttempted, RetryScheduled;
        public int Failures;
    }

    private sealed record Entry(long Revision, byte[] Payload);

    private sealed class Envelope
    {
        public Envelope() { }
        public int Schema { get; set; }
        public string Key { get; set; } = string.Empty;
        public long Revision { get; set; }
        public T? Value { get; set; }
    }

    private readonly string _folder;
    private readonly WorkspaceCacheRevisionStore _revisions;
    private readonly ConcurrentDictionary<string, WorkState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _priority = new();
    private readonly ConcurrentQueue<string> _background = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _workers;
    private readonly int _maxResidentEntries;
    private readonly ConcurrentDictionary<string, long> _lastAccess = new(StringComparer.OrdinalIgnoreCase);
    private long _accessClock;
    private int _stopping;

    public event Action<string, long>? Ready;
    public event Action<string, string>? Failed;

    public PersistentProjectionCache(string folder, Func<string> sourceStamp, int workerCount = 2, int maxResidentEntries = 6)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        _revisions = new WorkspaceCacheRevisionStore(Path.Combine(folder, "Revisions"), sourceStamp);
        _maxResidentEntries = Math.Clamp(maxResidentEntries, 1, 24);
        _workers = Enumerable.Range(0, Math.Clamp(workerCount, 1, 4))
            .Select(_ => Task.Run(WorkerAsync)).ToArray();
    }

    public bool TryGet(string key, out T value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            try
            {
                var copy = JsonSerializer.Deserialize<T>(entry.Payload);
                if (copy is not null)
                {
                    _lastAccess[key] = Interlocked.Increment(ref _accessClock);
                    value = copy;
                    return true;
                }
            }
            catch (Exception exception)
            {
                PipelineRuntimeDiagnostics.NavigationFailed("Projection presentation/" + key, exception);
            }
        }
        value = default!;
        return false;
    }

    public bool IsAuthoritative(string key) =>
        _entries.TryGetValue(key, out var entry) &&
        _revisions.CanRestore(key, entry.Revision);

    public bool IsCurrent(string key, long revision) => _revisions.IsCurrent(key, revision);
    public long CurrentRevision(string key) => _revisions.Current(key);
    public bool HasPendingWork => _states.Values.Any(state =>
    {
        lock (state.Gate) return state.Queued || state.Running || state.Again;
    });

    public void Register(string key, Func<T> loader, bool invalidate = false, bool priority = false)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A projection key is required.", nameof(key));
        var state = _states.GetOrAdd(key, _ => new WorkState());
        if (invalidate) _revisions.MarkDirty(key);
        lock (state.Gate)
        {
            state.Loader = loader;
            state.Generation = _revisions.Current(key);
            if (!invalidate && state.RestoreAttempted && IsAuthoritative(key)) return;
            if (state.Running || state.Queued)
            {
                if (invalidate) state.Again = true;
                return;
            }
            state.Queued = true;
        }
        Enqueue(key, priority);
    }

    public void Invalidate(string key, Func<T> loader) => Register(key, loader, invalidate: true, priority: true);

    private void Enqueue(string key, bool priority)
    {
        if (priority) _priority.Enqueue(key); else _background.Enqueue(key);
        _signal.Release();
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            try { await _signal.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            if (!_priority.TryDequeue(out var key) && !_background.TryDequeue(out key)) continue;
            if (!_states.TryGetValue(key, out var state)) continue;
            Func<T>? loader;
            long revision;
            bool restore;
            lock (state.Gate)
            {
                if (!state.Queued || state.Running) continue;
                state.Queued = false;
                state.Running = true;
                restore = !state.RestoreAttempted;
                state.RestoreAttempted = true;
                loader = state.Loader;
                revision = state.Generation;
            }
            var retry = false;
            var failed = false;
            try
            {
                if (restore) Restore(key);
                if (loader is not null && !IsAuthoritative(key))
                {
                    // The loader and its revision were captured together. A newer
                    // registration cannot accidentally publish an older loader's result.
                    if (!_revisions.IsCurrent(key, revision)) retry = true;
                    else
                    {
                    var sourceStamp = _revisions.CaptureSourceStamp();
                    var value = loader();
                    if (!_revisions.IsCurrent(key, revision)) retry = true;
                    else if (!sourceStamp.Equals(_revisions.CaptureSourceStamp(), StringComparison.Ordinal))
                    {
                        _revisions.RunIfCurrent(key, revision, () =>
                        {
                            _revisions.MarkUncertain(key);
                            return true;
                        });
                        retry = true;
                    }
                    else
                    {
                        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
                        var path = PathFor(key);
                        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                                    JsonSerializer.Serialize(gzip, new Envelope { Schema = 1, Key = key, Revision = revision, Value = value });
                                file.Flush(flushToDisk: true);
                            }
                            if (!_revisions.CommitIfCurrent(key, revision, sourceStamp, () =>
                            {
                                if (File.Exists(path)) File.Copy(path, path + ".previous", true);
                                File.Move(temporary, path, true);
                                _entries[key] = new Entry(revision, payload);
                                _lastAccess[key] = Interlocked.Increment(ref _accessClock);
                                TrimResidentEntries(key);
                            })) retry = true;
                            else
                            {
                                state.Failures = 0;
                                NotifyReady(key, revision);
                            }
                        }
                        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
                    }
                    }
                }
            }
            catch (Exception exception)
            {
                // A failed old loader must not invalidate a newer successful commit.
                // Check and advance the failed generation under the revision lock.
                try
                {
                    if (!_revisions.RunIfCurrent(key, revision, () =>
                    {
                        _revisions.MarkUncertain(key);
                        return true;
                    })) retry = true;
                    else
                    {
                        failed = true;
                        state.Failures++;
                        PipelineRuntimeDiagnostics.NavigationFailed("Projection preparation/" + key, exception);
                        NotifyFailed(key, exception.Message);
                    }
                }
                catch (Exception checkpointError)
                {
                    failed = true;
                    state.Failures++;
                    PipelineRuntimeDiagnostics.NavigationFailed("Projection checkpoint", checkpointError);
                    NotifyFailed(key, exception.Message);
                }
            }
            finally
            {
                bool again;
                lock (state.Gate)
                {
                    state.Running = false;
                    again = state.Again || retry;
                    state.Again = false;
                    // A source watermark mismatch advances the durable revision.
                    // Retry with that new revision, not the rejected loader generation.
                    if (again) state.Generation = _revisions.Current(key);
                    if (again && Volatile.Read(ref _stopping) == 0) state.Queued = true;
                }
                if (again && Volatile.Read(ref _stopping) == 0) Enqueue(key, priority: true);
                else if (failed && Volatile.Read(ref _stopping) == 0) ScheduleRetry(key, state);
            }
        }
    }

    private void ScheduleRetry(string key, WorkState state)
    {
        lock (state.Gate)
        {
            if (state.RetryScheduled || state.Queued || state.Running) return;
            state.RetryScheduled = true;
        }
        var delay = Math.Min(60000, 500 * (1 << Math.Min(state.Failures, 6)));
        _ = RetryLaterAsync(key, state, delay);
    }

    private async Task RetryLaterAsync(string key, WorkState state, int delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
            Func<T>? loader;
            lock (state.Gate) { state.RetryScheduled = false; loader = state.Loader; }
            if (loader is not null) Register(key, loader);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void Restore(string key)
    {
        foreach (var path in new[] { PathFor(key), PathFor(key) + ".previous" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                var envelope = JsonSerializer.Deserialize<Envelope>(gzip);
                if (envelope?.Value is null || envelope.Schema != 1 ||
                    !envelope.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                var entry = new Entry(envelope.Revision, JsonSerializer.SerializeToUtf8Bytes(envelope.Value));
                var accepted = false;
                _entries.AddOrUpdate(key, _ => { accepted = true; return entry; }, (_, old) =>
                {
                    if (old.Revision >= entry.Revision) return old;
                    accepted = true;
                    return entry;
                });
                if (accepted)
                {
                    _lastAccess[key] = Interlocked.Increment(ref _accessClock);
                    TrimResidentEntries(key);
                    NotifyReady(key, envelope.Revision);
                }
                return;
            }
            catch (Exception exception) { PipelineRuntimeDiagnostics.NavigationFailed("Projection restore/" + key, exception); }
        }
    }

    private string PathFor(string key) => Path.Combine(_folder,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))) + ".json.gz");

    private void NotifyReady(string key, long revision)
    {
        var handlers = Ready;
        if (handlers is null) return;
        foreach (Action<string, long> handler in handlers.GetInvocationList())
            try { handler(key, revision); }
            catch (Exception ex) { PipelineRuntimeDiagnostics.NavigationFailed("Projection subscriber", ex); }
    }

    private void NotifyFailed(string key, string message)
    {
        var handlers = Failed;
        if (handlers is null) return;
        foreach (Action<string, string> handler in handlers.GetInvocationList())
            try { handler(key, message); }
            catch (Exception ex) { PipelineRuntimeDiagnostics.NavigationFailed("Projection failure subscriber", ex); }
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && HasPendingWork)
            await Task.Delay(50).ConfigureAwait(false);
        // Unfinished work remains dirty for the next launch. Never promote an incomplete
        // projection or dispose synchronization resources under an abandoned reader.
        foreach (var pair in _states)
        {
            lock (pair.Value.Gate)
            {
                if (!pair.Value.Queued && !pair.Value.Running && !pair.Value.Again) continue;
                try { _revisions.MarkUncertain(pair.Key); }
                catch (Exception ex) { PipelineRuntimeDiagnostics.NavigationFailed("Projection shutdown checkpoint", ex); }
            }
        }
        _lifetime.Cancel();
        _signal.Release(_workers.Length);
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    private void TrimResidentEntries(string keepKey)
    {
        if (_entries.Count <= _maxResidentEntries) return;
        foreach (var candidate in _lastAccess
                     .Where(pair => !pair.Key.Equals(keepKey, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(pair => pair.Value)
                     .Take(Math.Max(0, _entries.Count - _maxResidentEntries))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.TryRemove(candidate, out _);
            _lastAccess.TryRemove(candidate, out _);
        }
    }

    public void Dispose()
    {
        try { StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
        catch (Exception exception) { PipelineRuntimeDiagnostics.NavigationFailed("Projection shutdown", exception); }
    }
}
