using System.Collections.ObjectModel;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class WorkViewModel : PageViewModel
{
    private readonly ActiveClientContext _clientContext;
    private readonly CollaborationRepository? _local;
    private readonly SqliteGovernanceRepository? _governance;
    private WorkTaskRecord? _selectedTask;
    private ApprovalRequestRecord? _selectedApproval;
    private string _newType="Task",_newTitle=string.Empty,_newDescription=string.Empty,_assignedTo=string.Empty,_priority="Normal",_approvalReason=string.Empty,_status="Work queue ready.";
    private DateTime? _newDueDate;
    private string _editAssignedTo=string.Empty,_editStatus="Open",_editPriority="Normal",_editDescription=string.Empty;
    private DateTime? _editDueDate;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshing;

    public WorkViewModel(ActiveClientContext clientContext):base("Work","Tasks, queries, approvals, access requests and maker-checker work queues")
    {
        _clientContext=clientContext;
        if(!NetworkClientRuntime.IsNetworkClient){_local=new CollaborationRepository();_local.Initialize();_governance=new SqliteGovernanceRepository();_governance.Initialize();}
        Tasks=[];Approvals=[];AssignableUsers=[];
        ItemTypes=["Task","Query","Access Request"];Priorities=["Normal","High","Urgent"];Statuses=["Open","In Progress","Waiting","Escalated","Completed","Rejected"];
        RefreshCommand=new AsyncRelayCommand(RefreshAsync);
        CreateTaskCommand=new AsyncRelayCommand(CreateTaskAsync,()=>NewTitle.Trim().Length>0);
        CreateApprovalCommand=new AsyncRelayCommand(CreateApprovalAsync,()=>NewTitle.Trim().Length>0);
        SaveTaskCommand=new AsyncRelayCommand(SaveTaskAsync,()=>SelectedTask is not null&&CanUpdateSelectedTask);
        CompleteTaskCommand=new AsyncRelayCommand(()=>SetTaskStatusAsync("Completed"),()=>SelectedTask is not null&&CanUpdateSelectedTask);
        ReopenTaskCommand=new AsyncRelayCommand(()=>SetTaskStatusAsync("Open"),()=>SelectedTask is not null&&CanUpdateSelectedTask);
        EscalateTaskCommand=new AsyncRelayCommand(()=>SetTaskStatusAsync("Escalated"),()=>SelectedTask is not null&&CanUpdateSelectedTask);
        ApproveCommand=new AsyncRelayCommand(()=>DecideAsync(true),()=>SelectedApproval is not null&&CanApprove);
        RejectCommand=new AsyncRelayCommand(()=>DecideAsync(false),()=>SelectedApproval is not null&&CanApprove);
        _refreshTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(2)};
        _refreshTimer.Tick+=async (_,_)=>await RefreshAsync(silent:true);
        _refreshTimer.Start();
        _=RefreshAsync();
    }

    public ObservableCollection<WorkTaskRecord> Tasks{get;}
    public ObservableCollection<ApprovalRequestRecord> Approvals{get;}
    public ObservableCollection<string> AssignableUsers{get;}
    public IReadOnlyList<string> ItemTypes{get;}
    public IReadOnlyList<string> Priorities{get;}
    public IReadOnlyList<string> Statuses{get;}
    public AsyncRelayCommand RefreshCommand{get;}
    public AsyncRelayCommand CreateTaskCommand{get;}
    public AsyncRelayCommand CreateApprovalCommand{get;}
    public AsyncRelayCommand SaveTaskCommand{get;}
    public AsyncRelayCommand CompleteTaskCommand{get;}
    public AsyncRelayCommand ReopenTaskCommand{get;}
    public AsyncRelayCommand EscalateTaskCommand{get;}
    public AsyncRelayCommand ApproveCommand{get;}
    public AsyncRelayCommand RejectCommand{get;}

    public bool CanApprove=>NetworkClientRuntime.IsNetworkClient
        ? NetworkClientRuntime.HasAnyPermission(GovernancePermissions.ApprovalsManage,GovernancePermissions.Approve)
        : LocalHasAnyPermission(GovernancePermissions.ApprovalsManage,GovernancePermissions.Approve);
    public bool CanUpdateSelectedTask=>SelectedTask is not null&&(NetworkClientRuntime.IsNetworkClient
        ? NetworkClientRuntime.HasPermission(GovernancePermissions.TasksManage)||SelectedTask.AssignedTo.Equals(NetworkClientRuntime.UserName,StringComparison.OrdinalIgnoreCase)||SelectedTask.CreatedBy.Equals(NetworkClientRuntime.UserName,StringComparison.OrdinalIgnoreCase)
        : LocalHasPermission(GovernancePermissions.TasksManage)||SelectedTask.AssignedTo.Equals(Environment.UserName,StringComparison.OrdinalIgnoreCase)||SelectedTask.CreatedBy.Equals(Environment.UserName,StringComparison.OrdinalIgnoreCase));
    public bool CanReassignSelectedTask=>SelectedTask is not null&&(NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.HasPermission(GovernancePermissions.TasksManage):LocalHasPermission(GovernancePermissions.TasksManage));

    public WorkTaskRecord? SelectedTask
    {
        get=>_selectedTask;
        set
        {
            if(!SetProperty(ref _selectedTask,value))return;
            if(value is null){EditAssignedTo=string.Empty;EditStatus="Open";EditPriority="Normal";EditDueDate=null;EditDescription=string.Empty;}
            else{EditAssignedTo=value.AssignedTo;EditStatus=value.Status;EditPriority=value.Priority;EditDueDate=value.DueUtc?.ToLocalTime();EditDescription=value.Description;}
            RaiseTaskCommands();
            OnPropertyChanged(nameof(CanUpdateSelectedTask));
            OnPropertyChanged(nameof(CanReassignSelectedTask));
        }
    }
    public ApprovalRequestRecord? SelectedApproval{get=>_selectedApproval;set{if(SetProperty(ref _selectedApproval,value)){ApproveCommand.RaiseCanExecuteChanged();RejectCommand.RaiseCanExecuteChanged();}}}
    public string NewType{get=>_newType;set=>SetProperty(ref _newType,value);}
    public string NewTitle{get=>_newTitle;set{if(SetProperty(ref _newTitle,value)){CreateTaskCommand.RaiseCanExecuteChanged();CreateApprovalCommand.RaiseCanExecuteChanged();}}}
    public string NewDescription{get=>_newDescription;set=>SetProperty(ref _newDescription,value);}
    public string AssignedTo{get=>_assignedTo;set=>SetProperty(ref _assignedTo,value);}
    public string Priority{get=>_priority;set=>SetProperty(ref _priority,value);}
    public DateTime? NewDueDate{get=>_newDueDate;set=>SetProperty(ref _newDueDate,value);}
    public string ApprovalReason{get=>_approvalReason;set=>SetProperty(ref _approvalReason,value);}
    public string EditAssignedTo{get=>_editAssignedTo;set=>SetProperty(ref _editAssignedTo,value);}
    public string EditStatus{get=>_editStatus;set=>SetProperty(ref _editStatus,value);}
    public string EditPriority{get=>_editPriority;set=>SetProperty(ref _editPriority,value);}
    public DateTime? EditDueDate{get=>_editDueDate;set=>SetProperty(ref _editDueDate,value);}
    public string EditDescription{get=>_editDescription;set=>SetProperty(ref _editDescription,value);}
    public string Status{get=>_status;private set=>SetProperty(ref _status,value);}

    public Task RefreshAsync()=>RefreshAsync(silent:false);
    private async Task RefreshAsync(bool silent)
    {
        if(_refreshing)return;_refreshing=true;
        try
        {
            var selectedTaskId=SelectedTask?.Id??0;
            var selectedApprovalId=SelectedApproval?.Id??0;
            if(NetworkClientRuntime.IsNetworkClient)
            {
                using var api=CreateApi();
                var tasks=await api.GetTasksAsync();
                var approvals=await api.GetApprovalsAsync();
                var users=await api.GetDirectoryUsersAsync();
                Tasks.ReplaceWith(tasks);Approvals.ReplaceWith(approvals);
                AssignableUsers.ReplaceWith(users.Select(x=>x.UserId).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                var user=Environment.UserName;
                var canAll=LocalHasPermission(GovernancePermissions.TasksManage);
                Tasks.ReplaceWith(_local!.GetTasks(user,canAll));
                var canApprove=CanApprove;
                Approvals.ReplaceWith(_local.GetApprovals(user,canApprove));
                AssignableUsers.ReplaceWith(_governance!.GetUsers().Where(x=>x.IsActive).Select(x=>x.WindowsUserName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));
            }
            if(selectedTaskId>0)SelectedTask=Tasks.FirstOrDefault(x=>x.Id==selectedTaskId);
            if(selectedApprovalId>0)SelectedApproval=Approvals.FirstOrDefault(x=>x.Id==selectedApprovalId);
            if(string.IsNullOrWhiteSpace(AssignedTo))AssignedTo=NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.UserName:Environment.UserName;
            if(!silent)Status=$"{Tasks.Count:N0} task/query/access item(s) · {Approvals.Count:N0} approval request(s).";
            RaiseTaskCommands();
        }
        catch(Exception ex){if(!silent)Status="Work refresh failed: "+ex.Message;}
        finally{_refreshing=false;}
    }

    private async Task CreateTaskAsync()
    {
        try
        {
            var c=_clientContext.CurrentClient;
            var itemType=NewType;
            var taskStatus=itemType.Equals("Access Request",StringComparison.OrdinalIgnoreCase)?"Waiting":"Open";
            var t=new WorkTaskRecord{ItemType=itemType,Title=NewTitle.Trim(),Description=NewDescription.Trim(),AssignedTo=string.IsNullOrWhiteSpace(AssignedTo)?(NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.UserName:Environment.UserName):AssignedTo.Trim(),ClientId=c?.Id??0,FinancialYear=c?.FinancialYear??string.Empty,Priority=Priority,Status=taskStatus,DueUtc=NewDueDate?.ToUniversalTime()};
            if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.CreateTaskAsync(t);}else{t.CreatedBy=Environment.UserName;_local!.AddTask(t);}
            if(itemType.Equals("Access Request",StringComparison.OrdinalIgnoreCase)&&c is not null)
            {
                var requestUser=NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.UserName:Environment.UserName;
                var a=new ApprovalRequestRecord{RequestType="Client Access Request",ResourceKey=$"ClientAccess:{c.Id}:{c.FinancialYear}:{requestUser}:8",Reason=string.IsNullOrWhiteSpace(NewDescription)?NewTitle.Trim():NewDescription.Trim(),ApproverRole=GovernanceRoles.Administrator,Status="Pending"};
                if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.CreateApprovalAsync(a);}else{a.RequestedBy=Environment.UserName;_local!.AddApproval(a);}
            }
            var createdType=NewType;ClearComposer();Status=$"{createdType} created.";await RefreshAsync();
        }
        catch(Exception ex){Status="Could not create work item: "+ex.Message;}
    }

    private async Task SaveTaskAsync()
    {
        try
        {
            if(SelectedTask is null)return;
            var assigned=CanReassignSelectedTask&&!string.IsNullOrWhiteSpace(EditAssignedTo)?EditAssignedTo.Trim():SelectedTask.AssignedTo;
            var dueUtc=EditDueDate?.ToUniversalTime();
            WorkTaskRecord updated;
            if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();updated=await api.UpdateTaskAsync(SelectedTask.Id,assigned,EditStatus,EditPriority,dueUtc,EditDescription);}
            else{updated=_local!.UpdateTask(SelectedTask.Id,Environment.UserName,assigned,EditStatus,EditPriority,dueUtc,EditDescription);}
            Status=$"{updated.ItemType} updated: {updated.Status}.";await RefreshAsync();SelectedTask=Tasks.FirstOrDefault(x=>x.Id==updated.Id);
        }
        catch(Exception ex){Status="Could not update work item: "+ex.Message;}
    }

    private async Task SetTaskStatusAsync(string status)
    {
        if(SelectedTask is null)return;EditStatus=status;await SaveTaskAsync();
    }

    private async Task CreateApprovalAsync()
    {
        try
        {
            var c=_clientContext.CurrentClient;
            var a=new ApprovalRequestRecord{RequestType=NewType+" Approval",ResourceKey=c is null?NewTitle.Trim():$"Client:{c.Id}:{c.FinancialYear}:{NewTitle.Trim()}",Reason=string.IsNullOrWhiteSpace(ApprovalReason)?NewDescription.Trim():ApprovalReason.Trim(),ApproverRole=GovernanceRoles.Administrator,Status="Pending"};
            if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.CreateApprovalAsync(a);}else{a.RequestedBy=Environment.UserName;_local!.AddApproval(a);}
            ClearComposer();Status="Maker-checker approval request created.";await RefreshAsync();
        }
        catch(Exception ex){Status="Could not create approval: "+ex.Message;}
    }

    private async Task DecideAsync(bool approve)
    {
        try{if(SelectedApproval is null)return;if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.DecideApprovalAsync(SelectedApproval.Id,approve,ApprovalReason);}else{_local!.DecideApproval(SelectedApproval.Id,Environment.UserName,approve,ApprovalReason);if(approve)ApplyLocalApprovedAction(SelectedApproval);}Status=approve?"Approval authorised.":"Approval rejected.";await RefreshAsync();}
        catch(Exception ex){Status="Approval decision failed: "+ex.Message;}
    }


    private static void ApplyLocalApprovedAction(ApprovalRequestRecord approval)
    {
        if(!approval.RequestType.Equals("Client Access Request",StringComparison.OrdinalIgnoreCase)||!approval.ResourceKey.StartsWith("ClientAccess:",StringComparison.OrdinalIgnoreCase))return;
        var parts=approval.ResourceKey.Split(':',6,StringSplitOptions.TrimEntries);
        if(parts.Length<5||!long.TryParse(parts[1],out var clientId)||clientId<=0||string.IsNullOrWhiteSpace(parts[3]))return;
        var hours=parts.Length>4&&int.TryParse(parts[4],out var parsedHours)?Math.Clamp(parsedHours,1,168):8;
        var governance=new SqliteGovernanceRepository();governance.Initialize();governance.SetClientLanAvailable(clientId,true);governance.SetUserClientAccess(parts[3],clientId,parts[2],true,DateTime.UtcNow.AddHours(hours));var enterprise=new EnterpriseControlRepository();enterprise.Initialize();enterprise.BumpRevision("client");
    }

    private bool LocalHasPermission(string permission)
    {
        if(_governance is null)return false;
        var user=_governance.GetCurrentUser();
        return user.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase)||_governance.HasPermission(Environment.UserName,permission);
    }
    private bool LocalHasAnyPermission(params string[] permissions)=>permissions.Any(LocalHasPermission);

    private void ClearComposer(){NewTitle=string.Empty;NewDescription=string.Empty;ApprovalReason=string.Empty;NewDueDate=null;}
    private void RaiseTaskCommands(){SaveTaskCommand.RaiseCanExecuteChanged();CompleteTaskCommand.RaiseCanExecuteChanged();ReopenTaskCommand.RaiseCanExecuteChanged();EscalateTaskCommand.RaiseCanExecuteChanged();}
    private static FullUiNetworkClientApi CreateApi()=>new(){Server=NetworkClientRuntime.ResolveServerEndpoint(),DeviceId=NetworkClientRuntime.DeviceId,DeviceToken=NetworkClientRuntime.DeviceToken,SessionToken=NetworkClientRuntime.SessionToken};
}
