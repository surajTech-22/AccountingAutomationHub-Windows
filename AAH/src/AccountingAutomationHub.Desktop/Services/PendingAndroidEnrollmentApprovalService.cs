using System.Collections.Concurrent;

namespace AccountingAutomationHub.Desktop.Services;

public sealed record PendingAndroidEnrollmentApproval(
    string RequestId,
    string UserName,
    string DisplayName,
    string DeviceName,
    string RemoteAddress,
    DateTime ExpiresUtc);

/// <summary>
/// Non-modal Main Server authority for first-time Android enrollment.
/// Requests are surfaced through normal AAH/Windows notifications and are approved or denied
/// from Notifications; no blocking MessageBox owns the server request lifecycle.
/// </summary>
public static class PendingAndroidEnrollmentApprovalService
{
    private sealed record PendingEntry(
        PendingAndroidEnrollmentApproval Display,
        SecureLanServerService.AndroidEnrollmentApprovalEventArgs Request);

    private static readonly ConcurrentDictionary<string, PendingEntry> Pending = new(StringComparer.OrdinalIgnoreCase);
    public static event EventHandler? Changed;

    public static void Add(SecureLanServerService.AndroidEnrollmentApprovalEventArgs request)
    {
        CleanupExpired();
        var display = new PendingAndroidEnrollmentApproval(
            request.RequestId,
            request.UserName,
            request.DisplayName,
            request.DeviceName,
            request.RemoteAddress,
            request.ExpiresUtc);
        Pending[request.RequestId] = new PendingEntry(display, request);
        RaiseChanged();
    }

    public static IReadOnlyList<PendingAndroidEnrollmentApproval> Read()
    {
        CleanupExpired();
        return Pending.Values
            .Select(x => x.Display)
            .OrderBy(x => x.ExpiresUtc)
            .ToArray();
    }

    public static bool Complete(string requestId, bool approved)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return false;
        CleanupExpired();
        if (!Pending.TryRemove(requestId.Trim(), out var entry)) return false;
        if (entry.Display.ExpiresUtc <= DateTime.UtcNow)
        {
            entry.Request.Complete(false);
            RaiseChanged();
            return false;
        }

        entry.Request.Complete(approved);
        NotificationHistoryService.Record(
            string.Empty,
            "Security",
            approved ? "Approved" : "Denied",
            "Phone enrollment approval",
            $"{entry.Display.DeviceName} · {entry.Display.UserName} · request {entry.Display.RequestId}");
        RaiseChanged();
        return true;
    }

    private static void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in Pending.ToArray())
        {
            if (pair.Value.Display.ExpiresUtc > now) continue;
            if (Pending.TryRemove(pair.Key, out var expired)) expired.Request.Complete(false);
        }
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
