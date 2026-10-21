using System;
using System.Threading;
using System.Threading.Tasks;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// A process-lifetime safety net for writes from other connections/processes, restore,
/// network replacement and older builds that do not publish the normal commit event.
/// It never reads or writes accounting rows and does not depend on a Window.
/// </summary>
public sealed class WorkspaceSourceChangeMonitor : IDisposable
{
    private readonly Func<string> _sourceStamp;
    private readonly Action _changed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly TimeSpan _interval;
    private readonly object _stampGate = new();
    private string? _lastStamp;
    private int _disposed;

    public WorkspaceSourceChangeMonitor(Func<string> sourceStamp, Action changed, TimeSpan? interval = null)
    {
        _sourceStamp = sourceStamp;
        _changed = changed;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        try { _lastStamp = _sourceStamp(); } catch { _lastStamp = null; }
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var current = _sourceStamp();
                string? last;
                lock (_stampGate) last = _lastStamp;
                if (last is not null && !current.Equals(last, StringComparison.Ordinal))
                {
                    // Coalesce a burst of WAL writes into one application-lifetime invalidation.
                    // Normal in-process commits acknowledge their already-published source stamp,
                    // so this safety net does not schedule an all-client rebuild for the same write.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _stop.Token).ConfigureAwait(false);
                    var settled = _sourceStamp();
                    var notify = false;
                    lock (_stampGate)
                    {
                        if (_lastStamp is null || !_lastStamp.Equals(settled, StringComparison.Ordinal))
                        {
                            _lastStamp = settled;
                            notify = true;
                        }
                    }
                    if (notify) _changed();
                    current = settled;
                }
                lock (_stampGate) _lastStamp = current;
                await Task.Delay(_interval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                PipelineRuntimeDiagnostics.NavigationFailed("Source change monitor", ex);
                try { await Task.Delay(_interval, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            }
        }
    }

    /// <summary>
    /// Marks the current SQLite/WAL watermark as already handled by the normal in-process
    /// live-change pipeline. This keeps the one-second external-change safety net from
    /// replaying the same commit as an all-client cache invalidation.
    /// </summary>
    public void AcknowledgeCurrentSourceStamp()
    {
        try
        {
            var current = _sourceStamp();
            lock (_stampGate) _lastStamp = current;
        }
        catch (Exception ex)
        {
            PipelineRuntimeDiagnostics.NavigationFailed("Source change acknowledgement", ex);
        }
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { await _worker.WaitAsync(timeout).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    public void Dispose()
    {
        try { StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (Exception ex) { PipelineRuntimeDiagnostics.NavigationFailed("Source monitor shutdown", ex); }
    }
}
