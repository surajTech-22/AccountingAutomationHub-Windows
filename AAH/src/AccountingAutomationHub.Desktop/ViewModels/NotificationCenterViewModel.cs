using System.Collections.ObjectModel;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class NotificationCenterViewModel : PageViewModel
{
    private string _status = "Notification Center ready.";
    private string _search = string.Empty;
    private readonly ImportRegistrySyncService _registrySync = new();
    private readonly SqliteGovernanceRepository _governance = new();

    public NotificationCenterViewModel()
        : base("Notifications", "Non-blocking AAH history for security, messages, tasks, imports, Tally, updates, backup and system events")
    {
        Items=[];
        PendingEnrollmentApprovals=[];
        PendingRegistryConflicts=[];
        _governance.Initialize();
        RefreshCommand=new RelayCommand(Refresh);
        ClearCommand=new RelayCommand(Clear);
        ApproveEnrollmentCommand=new ParameterRelayCommand(parameter=>CompleteEnrollment(parameter,true));
        DenyEnrollmentCommand=new ParameterRelayCommand(parameter=>CompleteEnrollment(parameter,false));
        AcceptRegistryConflictCommand=new ParameterRelayCommand(parameter=>CompleteRegistryConflict(parameter,true));
        KeepRegistryConflictCommand=new ParameterRelayCommand(parameter=>CompleteRegistryConflict(parameter,false));
        NotificationHistoryService.Changed+=OnHistoryChanged;
        PendingAndroidEnrollmentApprovalService.Changed+=OnPendingApprovalChanged;
        Refresh();
    }

    public ObservableCollection<NotificationHistoryRow> Items{get;}
    public ObservableCollection<PendingAndroidEnrollmentApproval> PendingEnrollmentApprovals{get;}
    public ObservableCollection<RegistryConflictRecord> PendingRegistryConflicts{get;}
    public RelayCommand RefreshCommand{get;}
    public RelayCommand ClearCommand{get;}
    public ParameterRelayCommand ApproveEnrollmentCommand{get;}
    public ParameterRelayCommand DenyEnrollmentCommand{get;}
    public ParameterRelayCommand AcceptRegistryConflictCommand{get;}
    public ParameterRelayCommand KeepRegistryConflictCommand{get;}
    public string Status{get=>_status;private set=>SetProperty(ref _status,value);}
    public string Search{get=>_search;set{if(SetProperty(ref _search,value))Refresh();}}
    public bool HasPendingEnrollmentApprovals=>PendingEnrollmentApprovals.Count>0;
    public bool HasPendingRegistryConflicts=>PendingRegistryConflicts.Count>0;

    public void Refresh()
    {
        var rows=NotificationHistoryService.Read(2000);
        if(!string.IsNullOrWhiteSpace(Search))
        {
            var q=Search.Trim();
            rows=rows.Where(x=>x.Client.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Module.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Type.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Title.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Message.Contains(q,StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        Items.ReplaceWith(rows);
        PendingEnrollmentApprovals.ReplaceWith(PendingAndroidEnrollmentApprovalService.Read());
        var canResolveRegistryConflicts=!NetworkClientRuntime.IsNetworkClient&&_governance.GetCurrentUser().RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase);
        PendingRegistryConflicts.ReplaceWith(canResolveRegistryConflicts?_registrySync.ReadConflicts():Array.Empty<RegistryConflictRecord>());
        OnPropertyChanged(nameof(HasPendingEnrollmentApprovals));
        OnPropertyChanged(nameof(HasPendingRegistryConflicts));
        var waiting=new List<string>();
        if(PendingEnrollmentApprovals.Count>0)waiting.Add($"{PendingEnrollmentApprovals.Count:N0} phone enrollment approval(s)");
        if(PendingRegistryConflicts.Count>0)waiting.Add($"{PendingRegistryConflicts.Count:N0} import-registry conflict(s)");
        Status=waiting.Count>0
            ? $"{Items.Count:N0} notification(s) · {string.Join(" · ",waiting)} waiting. Approval is non-modal and does not interrupt the current workspace."
            : $"{Items.Count:N0} notification(s). Security/OTP and background-sync events use normal notifications and never force a blocking popup.";
    }

    private void OnHistoryChanged(object? sender,EventArgs e)=>Refresh();
    private void OnPendingApprovalChanged(object? sender,EventArgs e)=>Refresh();
    private void Clear(){NotificationHistoryService.Clear();Status="Notification history cleared on this installation.";}

    private void CompleteEnrollment(object? parameter,bool approved)
    {
        var requestId=parameter?.ToString()?.Trim()??string.Empty;
        if(requestId.Length==0)return;
        var completed=PendingAndroidEnrollmentApprovalService.Complete(requestId,approved);
        Refresh();
        Status=completed
            ? approved?"Phone enrollment approved. The Android phone can complete enrollment now.":"Phone enrollment denied."
            : "That phone enrollment request has already expired or was completed.";
    }

    private void CompleteRegistryConflict(object? parameter,bool acceptIncoming)
    {
        if(NetworkClientRuntime.IsNetworkClient||!_governance.GetCurrentUser().RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase))
        {
            Status="Only the Main Server Administrator can resolve import-registry conflicts.";
            return;
        }
        var conflictId=parameter?.ToString()?.Trim()??string.Empty;
        if(conflictId.Length==0)return;
        var result=_registrySync.ResolveConflict(conflictId,acceptIncoming,_governance.GetCurrentUser().WindowsUserName);
        if(result.Resolved)
        {
            NotificationHistoryService.Record(string.Empty,"Import Registry",acceptIncoming?"ConflictAccepted":"ConflictRejected","Import registry conflict resolved",result.Detail);
        }
        Refresh();
        Status=result.Detail;
    }
}
