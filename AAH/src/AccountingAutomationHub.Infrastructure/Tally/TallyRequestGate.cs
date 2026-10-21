using System.Collections.Generic;
using System.Threading;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Serializes access to the Tally HTTP/XML endpoint. The main/server process uses Local priority;
/// future LAN-originated Tally commands can opt into Remote priority. Local waiters are served before
/// remote waiters; within each priority class requests are FIFO. Only one HTTP request owns Tally at a time.
/// </summary>
public static class TallyRequestGate
{
    private static readonly object Sync = new();
    private static readonly Queue<Waiter> LocalQueue = new();
    private static readonly Queue<Waiter> RemoteQueue = new();
    private static readonly AsyncLocal<TallyRequestPriority?> CurrentPriority = new();
    private static bool _held;

    public static TallyRequestPriority Priority => CurrentPriority.Value ?? TallyRequestPriority.Local;

    public static IDisposable UsePriority(TallyRequestPriority priority)
    {
        var previous = CurrentPriority.Value;
        CurrentPriority.Value = priority;
        return new PriorityScope(previous);
    }

    public static Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(Priority, cancellationToken);

    public static Task<IDisposable> AcquireAsync(TallyRequestPriority priority, CancellationToken cancellationToken = default)
    {
        lock (Sync)
        {
            if (!_held && LocalQueue.Count == 0 && RemoteQueue.Count == 0)
            {
                _held = true;
                return Task.FromResult<IDisposable>(new Lease());
            }

            var waiter = new Waiter(priority);
            if (priority == TallyRequestPriority.Local) LocalQueue.Enqueue(waiter);
            else if (priority == TallyRequestPriority.Remote) RemoteQueue.Enqueue(waiter);
            else throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown Tally request priority.");
            if (cancellationToken.CanBeCanceled)
                waiter.Registration = cancellationToken.Register(() => Cancel(waiter, cancellationToken));
            return waiter.Completion.Task;
        }
    }

    private static void Cancel(Waiter waiter, CancellationToken token)
    {
        lock (Sync)
        {
            if (waiter.Completed) return;
            waiter.Cancelled = true;
            waiter.Completed = true;
            waiter.Registration.Dispose();
            waiter.Completion.TrySetCanceled(token);
            // Cancelled waiters remain in the queue and are skipped by Release; removing an
            // arbitrary Queue element would disturb FIFO order for the remaining commands.
        }
    }

    private static void Release()
    {
        Waiter? next = null;
        lock (Sync)
        {
            while (LocalQueue.Count > 0 && next is null)
            {
                var candidate = LocalQueue.Dequeue();
                if (!candidate.Cancelled) next = candidate;
            }
            while (RemoteQueue.Count > 0 && next is null)
            {
                var candidate = RemoteQueue.Dequeue();
                if (!candidate.Cancelled) next = candidate;
            }

            if (next is null)
            {
                _held = false;
                return;
            }

            next.Completed = true;
            next.Registration.Dispose();
        }
        next.Completion.TrySetResult(new Lease());
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Release();
        }
    }

    private sealed class PriorityScope(TallyRequestPriority? previous) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) CurrentPriority.Value = previous;
        }
    }

    private sealed class Waiter(TallyRequestPriority priority)
    {
        public TallyRequestPriority Priority { get; } = priority;
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
        public bool Cancelled { get; set; }
        public bool Completed { get; set; }
    }
}

public enum TallyRequestPriority
{
    Local = 0,
    Remote = 1
}
