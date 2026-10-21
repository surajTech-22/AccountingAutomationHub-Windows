using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>App-wide Work notifier for tasks, access requests and approvals. It runs independently
/// of WorkView so assigned/new work can notify the user while another page is open or AAH is in tray.</summary>
public sealed class WorkNotificationAgentService : IDisposable
{
    private readonly App _app;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<long, string> _taskStates = [];
    private readonly Dictionary<long, string> _approvalStates = [];
    private int _running;
    private bool _disposed;
    private bool _primed;

    public WorkNotificationAgentService(App app)
    {
        _app = app;
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    private async Task PollAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            IReadOnlyList<WorkTaskRecord> tasks;
            IReadOnlyList<ApprovalRequestRecord> approvals;
            var current = NetworkClientRuntime.IsNetworkClient ? NetworkClientRuntime.UserName : Environment.UserName;
            if (NetworkClientRuntime.IsNetworkClient)
            {
                if (string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken)) return;
                using var api = new FullUiNetworkClientApi
                {
                    Server = NetworkClientRuntime.ResolveServerEndpoint(),
                    DeviceId = NetworkClientRuntime.DeviceId,
                    DeviceToken = NetworkClientRuntime.DeviceToken,
                    SessionToken = NetworkClientRuntime.SessionToken
                };
                tasks = await api.GetTasksAsync().ConfigureAwait(false);
                approvals = await api.GetApprovalsAsync().ConfigureAwait(false);
                if (!NetworkLocalPreferenceService.Load().WorkNotificationsEnabled)
                {
                    foreach (var t in tasks) _taskStates[t.Id] = Fingerprint(t);
                    foreach (var a in approvals) _approvalStates[a.Id] = Fingerprint(a);
                    _primed = true;
                    return;
                }
            }
            else
            {
                var repo = new CollaborationRepository(); repo.Initialize();
                var governance = new SqliteGovernanceRepository(); governance.Initialize();
                var canTasks = governance.HasPermission(Environment.UserName, GovernancePermissions.TasksManage);
                var canApprovals = governance.HasPermission(Environment.UserName, GovernancePermissions.ApprovalsManage);
                tasks = repo.GetTasks(current, canTasks);
                approvals = repo.GetApprovals(current, canApprovals);
            }

            if (!_primed)
            {
                foreach (var t in tasks) _taskStates[t.Id] = Fingerprint(t);
                foreach (var a in approvals) _approvalStates[a.Id] = Fingerprint(a);
                _primed = true;
                return;
            }

            foreach (var task in tasks.OrderBy(x => x.UpdatedUtc))
            {
                var fp = Fingerprint(task);
                if (_taskStates.TryGetValue(task.Id, out var old) && old == fp) continue;
                var isAssigned = task.AssignedTo.Equals(current, StringComparison.OrdinalIgnoreCase);
                var isNew = !_taskStates.ContainsKey(task.Id);
                _taskStates[task.Id] = fp;
                if (!isAssigned) continue;
                var verb = isNew ? "assigned" : "updated";
                _app.ShowCollaborationNotification($"AAH {task.ItemType} {verb}", $"{task.Title} · {task.Status} · {task.Priority}", task.Priority.Equals("Urgent", StringComparison.OrdinalIgnoreCase), "Work");
            }

            foreach (var approval in approvals.OrderBy(x => x.CreatedUtc))
            {
                var fp = Fingerprint(approval);
                if (_approvalStates.TryGetValue(approval.Id, out var old) && old == fp) continue;
                var isNew = !_approvalStates.ContainsKey(approval.Id);
                _approvalStates[approval.Id] = fp;
                if (approval.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                    _app.ShowCollaborationNotification(isNew ? "AAH approval request" : "AAH approval updated", $"{approval.RequestType} · Requested by {approval.RequestedBy}", false, "Approvals");
            }

            Trim(_taskStates, tasks.Select(x => x.Id));
            Trim(_approvalStates, approvals.Select(x => x.Id));
        }
        catch { /* Work notification failure must never block AAH. */ }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static string Fingerprint(WorkTaskRecord x) => $"{x.UpdatedUtc:O}|{x.Status}|{x.Priority}|{x.AssignedTo}|{x.Title}";
    private static string Fingerprint(ApprovalRequestRecord x) => $"{x.Status}|{x.DecidedUtc:O}|{x.ApprovedBy}|{x.RequestType}|{x.RequestedBy}";
    private static void Trim(Dictionary<long, string> map, IEnumerable<long> active)
    {
        if (map.Count < 5000) return;
        var keep = active.ToHashSet();
        foreach (var key in map.Keys.Where(x => !keep.Contains(x)).ToArray()) map.Remove(key);
    }

    public void Dispose() { _disposed = true; _timer.Dispose(); }
}
