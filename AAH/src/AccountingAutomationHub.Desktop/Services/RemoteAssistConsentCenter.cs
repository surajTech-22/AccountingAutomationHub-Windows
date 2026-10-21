namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Persistent in-process Remote Assist consent state for the LAN desktop. A request remains visible
/// in the Remote Assist workspace until the signed-in AAH user accepts, declines or it expires.
/// This avoids relying on a transient modal MessageBox that can be hidden behind the main window.
/// </summary>
public static class RemoteAssistConsentCenter
{
    private static readonly object Sync = new();
    private static TaskCompletionSource<bool>? _pendingResponse;
    private static RemoteAssistConsentRequest? _pending;
    private static RemoteAssistActiveState? _active;
    private static readonly HashSet<string> EndedSessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AcceptedSessions = new(StringComparer.OrdinalIgnoreCase);

    public static event EventHandler? Changed;

    public static RemoteAssistConsentRequest? Pending
    {
        get { lock (Sync) return _pending; }
    }

    public static RemoteAssistActiveState? Active
    {
        get { lock (Sync) return _active; }
    }

    public static Task<bool> RegisterAsync(RemoteAssistConsentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskCompletionSource<bool> response;
        lock (Sync)
        {
            if (EndedSessions.Contains(request.SessionId)) return Task.FromResult(false);
            if (AcceptedSessions.Contains(request.SessionId)) return Task.FromResult(true);
            if (_active is not null && _active.SessionId.Equals(request.SessionId, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(true);
            if (_pending is not null && _pending.SessionId.Equals(request.SessionId, StringComparison.OrdinalIgnoreCase) && _pendingResponse is not null)
                return _pendingResponse.Task;
            _pendingResponse?.TrySetResult(false);
            _pending = request;
            response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponse = response;
        }
        RaiseChanged();
        _ = ExpireAsync(request.SessionId, request.ExpiresUtc);
        return response.Task;
    }

    public static bool AcceptPending()
    {
        TaskCompletionSource<bool>? response;
        lock (Sync)
        {
            if (_pending is null || _pending.ExpiresUtc <= DateTime.UtcNow) return false;
            AcceptedSessions.Add(_pending.SessionId);
            response = _pendingResponse;
            _pending = null;
            _pendingResponse = null;
        }
        response?.TrySetResult(true);
        RaiseChanged();
        return true;
    }

    public static bool DeclinePending()
    {
        TaskCompletionSource<bool>? response;
        lock (Sync)
        {
            if (_pending is null) return false;
            AcceptedSessions.Remove(_pending.SessionId);
            response = _pendingResponse;
            _pending = null;
            _pendingResponse = null;
        }
        response?.TrySetResult(false);
        RaiseChanged();
        return true;
    }

    public static bool MarkActive(RemoteAssistConsentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (Sync)
        {
            if (EndedSessions.Contains(request.SessionId)) return false;
            _active = new RemoteAssistActiveState(
                request.SessionId,
                request.HelperUser,
                request.RemoteUser,
                request.PermissionMode,
                request.Scope,
                request.Reason,
                request.ExpiresUtc);
        }
        RaiseChanged();
        return true;
    }

    public static void EndActive(string? sessionId = null)
    {
        lock (Sync)
        {
            if (_active is null) return;
            if (!string.IsNullOrWhiteSpace(sessionId) && !_active.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) return;
            _active = null;
        }
        RaiseChanged();
    }

    /// <summary>
    /// Ends exactly one Remote Assist session whether it is still waiting for consent or already active.
    /// This is intentionally idempotent so a delayed Main Server End command cannot leave a stale
    /// Accept/Decline or End Access surface on the LAN PC, and cannot clear a newer unrelated session.
    /// </summary>
    public static bool EndSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        TaskCompletionSource<bool>? response = null;
        var changed = false;
        lock (Sync)
        {
            EndedSessions.Add(sessionId);
            AcceptedSessions.Remove(sessionId);
            if (_pending is not null && _pending.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                response = _pendingResponse;
                _pendingResponse = null;
                _pending = null;
                changed = true;
            }
            if (_active is not null && _active.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                _active = null;
                changed = true;
            }
        }
        response?.TrySetResult(false);
        if (changed) RaiseChanged();
        return changed;
    }

    public static bool WasAccepted(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (Sync) return AcceptedSessions.Contains(sessionId);
    }

    public static bool IsKnownSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (Sync)
            return (_pending is not null && _pending.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                || (_active is not null && _active.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
    }

    public static void ClearAll()
    {
        TaskCompletionSource<bool>? response;
        lock (Sync)
        {
            response = _pendingResponse;
            _pendingResponse = null;
            _pending = null;
            _active = null;
            AcceptedSessions.Clear();
        }
        response?.TrySetResult(false);
        RaiseChanged();
    }

    private static async Task ExpireAsync(string sessionId, DateTime expiresUtc)
    {
        var delay = expiresUtc - DateTime.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            try { await Task.Delay(delay).ConfigureAwait(false); }
            catch { return; }
        }

        TaskCompletionSource<bool>? response = null;
        lock (Sync)
        {
            AcceptedSessions.Remove(sessionId);
            if (_pending is not null && _pending.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                response = _pendingResponse;
                _pendingResponse = null;
                _pending = null;
            }
            if (_active is not null && _active.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                _active = null;
        }
        response?.TrySetResult(false);
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) handler(null, EventArgs.Empty);
        else _ = dispatcher.BeginInvoke(new Action(() => handler(null, EventArgs.Empty)));
    }
}

public sealed record RemoteAssistConsentRequest(
    string SessionId,
    string RequestedBy,
    string HelperUser,
    string RemoteUser,
    string PermissionMode,
    string Scope,
    string Reason,
    DateTime ExpiresUtc);

public sealed record RemoteAssistActiveState(
    string SessionId,
    string HelperUser,
    string RemoteUser,
    string PermissionMode,
    string Scope,
    string Reason,
    DateTime ExpiresUtc);
