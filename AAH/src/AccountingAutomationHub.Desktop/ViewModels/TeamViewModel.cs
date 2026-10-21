using System.Collections.ObjectModel;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class TeamViewModel : PageViewModel
{
    private readonly ActiveClientContext _clientContext;
    private readonly CollaborationRepository? _local;
    private readonly SqliteGovernanceRepository? _governance;
    private readonly EnterpriseControlRepository? _enterpriseControl;
    private readonly ServerSecurityRepository? _serverSecurity;
    private readonly DispatcherTimer _refreshTimer;
    private NetworkDirectoryUser? _selectedRecipient;
    private CollaborationMessageRecord? _selectedMessage;
    private CollaborationFileRecord? _selectedSharedFile;
    private ChatConversationRow? _selectedConversation;
    private string _messageText=string.Empty,_priority="Normal",_status="Collaboration workspace ready.",_searchText=string.Empty;
    private string _myAvailability="Available",_myStatusMessage=string.Empty,_composeContextText=string.Empty;
    private long _replyToMessageId,_editingMessageId;
    private bool _refreshing;
    private int _onlineCount,_awayCount,_offlineCount,_unreadCount;

    public TeamViewModel(ActiveClientContext clientContext):base("Team","Private and Global chat, live presence, Client/FY discussions, announcements and controlled Main Server file sharing")
    {
        _clientContext=clientContext;
        if(!NetworkClientRuntime.IsNetworkClient){_local=new CollaborationRepository();_local.Initialize();_governance=new SqliteGovernanceRepository();_governance.Initialize();_enterpriseControl=new EnterpriseControlRepository();_enterpriseControl.Initialize();_serverSecurity=new ServerSecurityRepository();_serverSecurity.Initialize();}
        Messages=[];VisibleMessages=[];Users=[];Conversations=[];SharedFiles=[];
        Priorities=["Normal","High","Urgent"];AvailabilityOptions=["Available","Busy","Do Not Disturb","Away"];
        RefreshCommand=new AsyncRelayCommand(RefreshAsync);
        SendCommand=new AsyncRelayCommand(SendAsync,()=>CanSend&&MessageText.Trim().Length>0&&CanSendSelectedConversation);
        StartPrivateChatCommand=new RelayCommand(StartPrivateChat,()=>SelectedRecipient is not null);
        OpenGlobalChatCommand=new RelayCommand(()=>SelectConversation("global:all"));
        OpenClientChatCommand=new RelayCommand(OpenClientChat,()=>_clientContext.CurrentClient is not null);
        OpenAnnouncementsCommand=new RelayCommand(()=>SelectConversation("announcement:all"));
        ReplySelectedCommand=new RelayCommand(ReplySelected,()=>SelectedMessage is not null&&!SelectedMessage.IsDeleted);
        EditSelectedCommand=new RelayCommand(EditSelected,()=>CanEditSelectedMessage);
        DeleteSelectedCommand=new AsyncRelayCommand(DeleteSelectedAsync,()=>CanDeleteSelectedMessage);
        PinSelectedCommand=new AsyncRelayCommand(PinSelectedAsync,()=>CanPinSelectedMessage);
        ReactLikeCommand=new AsyncRelayCommand(()=>ReactSelectedAsync("Like"),()=>CanReactSelectedMessage);
        ReactDoneCommand=new AsyncRelayCommand(()=>ReactSelectedAsync("Done"),()=>CanReactSelectedMessage);
        ReactThanksCommand=new AsyncRelayCommand(()=>ReactSelectedAsync("Thanks"),()=>CanReactSelectedMessage);
        CancelComposeContextCommand=new RelayCommand(CancelComposeContext,()=>_replyToMessageId>0||_editingMessageId>0);
        SaveMyStatusCommand=new AsyncRelayCommand(SaveMyStatusAsync);
        AttachFileCommand=new AsyncRelayCommand(AttachFileAsync,()=>CanShareFiles&&CanSend&&SelectedConversation is not null&&CanSendSelectedConversation);
        DownloadSharedFileCommand=new AsyncRelayCommand(DownloadSharedFileAsync,()=>SelectedSharedFile is not null);
        OpenSharedFolderCommand=new RelayCommand(OpenSharedFolder,()=>!NetworkClientRuntime.IsNetworkClient);
        _clientContext.PropertyChanged+=(_,_)=>{OnPropertyChanged(nameof(ContextText));OpenClientChatCommand.RaiseCanExecuteChanged();BuildConversations();};
        _refreshTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(2)};
        _refreshTimer.Tick+=async (_,_)=>await RefreshAsync(silent:true);
        _refreshTimer.Start();
        _=RefreshAsync();
    }

    public ObservableCollection<CollaborationMessageRecord> Messages{get;}
    public ObservableCollection<CollaborationMessageRecord> VisibleMessages{get;}
    public ObservableCollection<NetworkDirectoryUser> Users{get;}
    public ObservableCollection<ChatConversationRow> Conversations{get;}
    public ObservableCollection<CollaborationFileRecord> SharedFiles{get;}
    public IReadOnlyList<string> Priorities{get;}
    public IReadOnlyList<string> AvailabilityOptions{get;}

    public AsyncRelayCommand RefreshCommand{get;} public AsyncRelayCommand SendCommand{get;}
    public RelayCommand StartPrivateChatCommand{get;} public RelayCommand OpenGlobalChatCommand{get;} public RelayCommand OpenClientChatCommand{get;} public RelayCommand OpenAnnouncementsCommand{get;}
    public RelayCommand ReplySelectedCommand{get;} public RelayCommand EditSelectedCommand{get;} public AsyncRelayCommand DeleteSelectedCommand{get;} public AsyncRelayCommand PinSelectedCommand{get;}
    public AsyncRelayCommand ReactLikeCommand{get;} public AsyncRelayCommand ReactDoneCommand{get;} public AsyncRelayCommand ReactThanksCommand{get;} public RelayCommand CancelComposeContextCommand{get;}
    public AsyncRelayCommand SaveMyStatusCommand{get;} public AsyncRelayCommand AttachFileCommand{get;} public AsyncRelayCommand DownloadSharedFileCommand{get;} public RelayCommand OpenSharedFolderCommand{get;}

    public bool CanSend=>NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.HasPermission(GovernancePermissions.MessagesSend):LocalHasPermission(GovernancePermissions.MessagesSend);
    public bool CanShareFiles=>NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.HasPermission(GovernancePermissions.FilesShare):LocalHasPermission(GovernancePermissions.FilesShare);
    public bool CanSendSelectedConversation=>SelectedConversation is not null&&(!SelectedConversation.Kind.Equals("Announcement",StringComparison.OrdinalIgnoreCase)||CanManageAnnouncements);
    public bool CanManageAnnouncements=>NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.HasAnyPermission(GovernancePermissions.ManageUsers,GovernancePermissions.ManageServer):LocalHasAnyPermission(GovernancePermissions.ManageUsers,GovernancePermissions.ManageServer);
    public bool CanEditSelectedMessage=>SelectedMessage is not null&&!SelectedMessage.IsDeleted&&SelectedMessage.SenderUserName.Equals(CurrentUser,StringComparison.OrdinalIgnoreCase)&&CanSend;
    public bool CanDeleteSelectedMessage=>SelectedMessage is not null&&!SelectedMessage.IsDeleted&&(SelectedMessage.SenderUserName.Equals(CurrentUser,StringComparison.OrdinalIgnoreCase)||CanManageAnnouncements);
    public bool CanPinSelectedMessage=>SelectedMessage is not null&&!SelectedMessage.IsDeleted&&CanSend&&(!(SelectedMessage.ConversationType.Equals("Global",StringComparison.OrdinalIgnoreCase)||SelectedMessage.ConversationType.Equals("Announcement",StringComparison.OrdinalIgnoreCase))||CanManageAnnouncements);
    public bool CanReactSelectedMessage=>SelectedMessage is not null&&!SelectedMessage.IsDeleted&&CanSend;
    public string ContextText=>_clientContext.CurrentClient is null?"No Client/FY currently open":$"Client context: {_clientContext.CurrentClient.ClientCode} · FY {_clientContext.CurrentClient.FinancialYear}";
    public string CurrentUserText=>$"{(NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.DisplayName:Environment.UserName)} · {(NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.RoleName:_governance?.GetCurrentUser().RoleName??GovernanceRoles.Administrator)}";
    public int OnlineCount{get=>_onlineCount;private set=>SetProperty(ref _onlineCount,value);} public int AwayCount{get=>_awayCount;private set=>SetProperty(ref _awayCount,value);} public int OfflineCount{get=>_offlineCount;private set=>SetProperty(ref _offlineCount,value);} public int UnreadCount{get=>_unreadCount;private set=>SetProperty(ref _unreadCount,value);}

    public NetworkDirectoryUser? SelectedRecipient{get=>_selectedRecipient;set{if(SetProperty(ref _selectedRecipient,value))StartPrivateChatCommand.RaiseCanExecuteChanged();}}
    public ChatConversationRow? SelectedConversation{get=>_selectedConversation;set{if(!SetProperty(ref _selectedConversation,value))return;ApplyMessageFilter();OnPropertyChanged(nameof(SelectedConversationTitle));OnPropertyChanged(nameof(SelectedConversationSubtitle));OnPropertyChanged(nameof(CanSendSelectedConversation));SendCommand.RaiseCanExecuteChanged();AttachFileCommand.RaiseCanExecuteChanged();_ = MarkSelectedConversationReadAsync();}}
    public string SelectedConversationTitle=>SelectedConversation?.Title??"Select a conversation";
    public string SelectedConversationSubtitle=>SelectedConversation?.Subtitle??"Private and Global AAH collaboration";
    public CollaborationMessageRecord? SelectedMessage{get=>_selectedMessage;set{if(!SetProperty(ref _selectedMessage,value))return;RaiseMessageActionCommands();}}
    public CollaborationFileRecord? SelectedSharedFile{get=>_selectedSharedFile;set{if(SetProperty(ref _selectedSharedFile,value))DownloadSharedFileCommand.RaiseCanExecuteChanged();}}
    public string MessageText{get=>_messageText;set{if(SetProperty(ref _messageText,value))SendCommand.RaiseCanExecuteChanged();}}
    public string Priority{get=>_priority;set=>SetProperty(ref _priority,value);}
    public string SearchText{get=>_searchText;set{if(SetProperty(ref _searchText,value))ApplyMessageFilter();}}
    public string MyAvailability{get=>_myAvailability;set=>SetProperty(ref _myAvailability,value);}
    public string MyStatusMessage{get=>_myStatusMessage;set=>SetProperty(ref _myStatusMessage,value);}
    public string ComposeContextText{get=>_composeContextText;private set=>SetProperty(ref _composeContextText,value);}
    public bool HasComposeContext=>_replyToMessageId>0||_editingMessageId>0;
    public string Status{get=>_status;private set=>SetProperty(ref _status,value);}

    private string CurrentUser=>NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.UserName:Environment.UserName;

    public Task RefreshAsync()=>RefreshAsync(silent:false);
    private async Task RefreshAsync(bool silent)
    {
        if(_refreshing)return;_refreshing=true;
        try
        {
            var currentUser=CurrentUser;var directory=new List<NetworkDirectoryUser>();
            if(NetworkClientRuntime.IsNetworkClient)
            {
                using var api=CreateApi();directory.AddRange(await api.GetDirectoryUsersAsync());Messages.ReplaceWith(await api.GetMessagesAsync());SharedFiles.ReplaceWith(await api.GetSharedFilesAsync());
            }
            else
            {
                var now=DateTime.UtcNow;var statusMap=(_local?.GetUserChatStatuses()??[]).ToDictionary(x=>x.UserName,StringComparer.OrdinalIgnoreCase);var liveRows=_enterpriseControl?.GetDevicePresence()??[];
                var activeSessions=(_serverSecurity?.GetSessions()??[]).Where(x=>x.ExpiresUtc>now).ToArray();var devices=_serverSecurity?.GetServerDevices()??[];
                foreach(var x in _governance?.GetUsers()??[])
                {
                    if(!x.IsActive)continue;statusMap.TryGetValue(x.WindowsUserName,out var chat);var userSessions=activeSessions.Where(s=>s.UserName.Equals(x.WindowsUserName,StringComparison.OrdinalIgnoreCase)).OrderByDescending(s=>s.LastSeenUtc).ToArray();var sessionIds=userSessions.Select(s=>s.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();var deviceNames=devices.Where(d=>sessionIds.Contains(d.DeviceId,StringComparer.OrdinalIgnoreCase)).Select(d=>d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();var live=liveRows.Where(p=>p.UserName.Equals(x.WindowsUserName,StringComparison.OrdinalIgnoreCase)&&sessionIds.Contains(p.DeviceId,StringComparer.OrdinalIgnoreCase)).OrderByDescending(p=>p.LastSeenUtc).FirstOrDefault();var sessionLast=userSessions.FirstOrDefault()?.LastSeenUtc;var lastSeen=live?.LastSeenUtc??sessionLast??x.LastSeenUtc;var presence=userSessions.Length==0?"Offline":lastSeen.HasValue&&lastSeen.Value>=now.AddMinutes(-3)?"Online":"Away";
                    directory.Add(new NetworkDirectoryUser{UserId=x.WindowsUserName,DisplayName=x.DisplayName,Role=x.RoleName,Presence=presence,Availability=presence=="Offline"?"Offline":chat?.Availability??"Available",StatusMessage=chat?.StatusMessage??string.Empty,LastSeenUtc=lastSeen,ActiveSessions=userSessions.Length,Devices=string.Join(", ",deviceNames),CurrentClientId=live?.ClientId??0,CurrentClient=live is null||live.ClientId<=0?string.Empty:$"Client {live.ClientId}",CurrentFinancialYear=live?.FinancialYear??string.Empty,CurrentModule=live?.Module??string.Empty});
                }
                Messages.ReplaceWith(_local!.GetInbox(currentUser,1000).Where(CanAccessLocalMessage));
                SharedFiles.ReplaceWith(_local.GetRecentFiles().Where(CanAccessLocalFile));
            }

            var me=directory.FirstOrDefault(x=>x.UserId.Equals(currentUser,StringComparison.OrdinalIgnoreCase));
            if(me is not null){MyAvailability=me.Availability.Equals("Offline",StringComparison.OrdinalIgnoreCase)?"Available":me.Availability;MyStatusMessage=me.StatusMessage;}
            Users.ReplaceWith(directory.Where(x=>!x.UserId.Equals(currentUser,StringComparison.OrdinalIgnoreCase)).OrderBy(x=>PresenceRank(x.Presence)).ThenBy(x=>x.DisplayName));
            OnlineCount=directory.Count(x=>x.Presence.Equals("Online",StringComparison.OrdinalIgnoreCase));AwayCount=directory.Count(x=>x.Presence.Equals("Away",StringComparison.OrdinalIgnoreCase));OfflineCount=directory.Count(x=>x.Presence.Equals("Offline",StringComparison.OrdinalIgnoreCase));
            BuildConversations();ApplyMessageFilter();UnreadCount=Messages.Count(x=>x.IsUnreadForCurrentUser);
            if(!silent)Status=$"{Messages.Count:N0} message(s) · {UnreadCount:N0} unread · {OnlineCount:N0} online · {SharedFiles.Count:N0} shared file(s). Auto-refresh is active.";
        }
        catch(Exception ex){if(!silent)Status="Collaboration refresh failed: "+ex.Message;}
        finally{_refreshing=false;}
    }

    private static int PresenceRank(string value)=>value.Equals("Online",StringComparison.OrdinalIgnoreCase)?0:value.Equals("Away",StringComparison.OrdinalIgnoreCase)?1:2;

    private void BuildConversations()
    {
        var preserved=SelectedConversation?.Key;var rows=new List<ChatConversationRow>();
        rows.Add(BuildConversation("global:all","Global","Global Chat","Everyone with AAH collaboration access"));
        rows.Add(BuildConversation("announcement:all","Announcement","Announcements","Administrator announcements"));
        var client=_clientContext.CurrentClient;if(client is not null)rows.Add(BuildConversation($"client:{client.Id}:{client.FinancialYear.Trim().ToUpperInvariant()}","Client",$"{client.ClientCode} · {client.ClientName}",$"Client/FY discussion · {client.FinancialYear}"));
        foreach(var user in Users)rows.Add(BuildConversation(BuildPrivateKey(CurrentUser,user.UserId),"Private",user.DisplayName,$"{user.Presence} · {user.Role}{(string.IsNullOrWhiteSpace(user.StatusMessage)?string.Empty:$" · {user.StatusMessage}")}",user.UserId));
        foreach(var group in Messages.GroupBy(x=>x.ConversationKey,StringComparer.OrdinalIgnoreCase))
        {
            if(rows.Any(x=>x.Key.Equals(group.Key,StringComparison.OrdinalIgnoreCase)))continue;var last=group.OrderByDescending(x=>x.CreatedUtc).First();
            rows.Add(BuildConversation(group.Key,last.ConversationType,last.ConversationType,$"Historical {last.ConversationType} conversation"));
        }
        Conversations.ReplaceWith(rows.OrderBy(x=>x.Kind.Equals("Global",StringComparison.OrdinalIgnoreCase)?0:x.Kind.Equals("Announcement",StringComparison.OrdinalIgnoreCase)?1:x.Kind.Equals("Client",StringComparison.OrdinalIgnoreCase)?2:3).ThenByDescending(x=>x.LastUtc).ThenBy(x=>x.Title));
        SelectedConversation=Conversations.FirstOrDefault(x=>preserved is not null&&x.Key.Equals(preserved,StringComparison.OrdinalIgnoreCase))??Conversations.FirstOrDefault(x=>x.Key.Equals("global:all",StringComparison.OrdinalIgnoreCase))??Conversations.FirstOrDefault();
    }

    private ChatConversationRow BuildConversation(string key,string kind,string title,string subtitle,string recipientUserId="")
    {
        var rows=Messages.Where(x=>x.ConversationKey.Equals(key,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.CreatedUtc).ToArray();var last=rows.FirstOrDefault();
        return new ChatConversationRow{Key=key,Kind=kind,Title=title,Subtitle=subtitle,RecipientUserId=recipientUserId,UnreadCount=rows.Count(x=>x.IsUnreadForCurrentUser),LastMessage=last is null?"No messages yet":last.DisplayBody,LastUtc=last?.CreatedUtc,IsPinned=rows.Any(x=>x.IsPinned)};
    }

    private void ApplyMessageFilter()
    {
        if(SelectedConversation is null){VisibleMessages.Clear();return;}var q=SearchText.Trim();var rows=Messages.Where(x=>x.ConversationKey.Equals(SelectedConversation.Key,StringComparison.OrdinalIgnoreCase));
        if(q.Length>0)rows=rows.Where(x=>(x.DisplayBody?.Contains(q,StringComparison.OrdinalIgnoreCase)??false)||(x.SenderUserName?.Contains(q,StringComparison.OrdinalIgnoreCase)??false)||(x.Priority?.Contains(q,StringComparison.OrdinalIgnoreCase)??false)||(x.ReactionSummary?.Contains(q,StringComparison.OrdinalIgnoreCase)??false));
        VisibleMessages.ReplaceWith(rows.OrderBy(x=>x.CreatedUtc));
    }

    private void StartPrivateChat(){if(SelectedRecipient is null)return;SelectConversation(BuildPrivateKey(CurrentUser,SelectedRecipient.UserId));}
    private void OpenClientChat(){var c=_clientContext.CurrentClient;if(c is null)return;SelectConversation($"client:{c.Id}:{c.FinancialYear.Trim().ToUpperInvariant()}");}
    private void SelectConversation(string key){var row=Conversations.FirstOrDefault(x=>x.Key.Equals(key,StringComparison.OrdinalIgnoreCase));if(row is not null)SelectedConversation=row;}
    private static string BuildPrivateKey(string a,string b)=>"private:"+string.Join("|",new[]{a.Trim(),b.Trim()}.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();

    private CollaborationMessageRecord BuildOutgoingMessage(string body)
    {
        if(SelectedConversation is null)throw new InvalidOperationException("Select a conversation first.");
        var client=_clientContext.CurrentClient;var kind=SelectedConversation.Kind;var recipient=kind.Equals("Private",StringComparison.OrdinalIgnoreCase)?SelectedConversation.RecipientUserId:string.Empty;
        if(kind.Equals("Client",StringComparison.OrdinalIgnoreCase)&&client is null)throw new InvalidOperationException("Open the Client/FY before sending to its conversation.");
        return new CollaborationMessageRecord{ConversationKey=SelectedConversation.Key,ConversationType=kind,SenderUserName=CurrentUser,RecipientUserName=recipient,ClientId=kind.Equals("Client",StringComparison.OrdinalIgnoreCase)?client?.Id??0:0,FinancialYear=kind.Equals("Client",StringComparison.OrdinalIgnoreCase)?client?.FinancialYear??string.Empty:string.Empty,Body=body.Trim(),Priority=Priority,ReplyToMessageId=_replyToMessageId};
    }

    private async Task SendAsync()
    {
        try
        {
            if(SelectedConversation is null)throw new InvalidOperationException("Select a conversation first.");if(!CanSendSelectedConversation)throw new InvalidOperationException("Your role cannot send to this conversation.");
            if(_editingMessageId>0)
            {
                if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.EditMessageAsync(_editingMessageId,MessageText.Trim());}else _local!.EditMessage(_editingMessageId,CurrentUser,MessageText.Trim());
                Status="Message edited.";CancelComposeContext();await RefreshAsync();return;
            }
            var message=BuildOutgoingMessage(MessageText);
            if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.SendMessageAsync(message);}else _local!.AddMessage(message);
            MessageText=string.Empty;CancelComposeContext();Status="Message sent through the AAH Main Server collaboration service.";await RefreshAsync();
        }
        catch(Exception ex){Status="Message could not be sent: "+ex.Message;}
    }

    private void ReplySelected(){if(SelectedMessage is null||SelectedMessage.IsDeleted)return;_editingMessageId=0;_replyToMessageId=SelectedMessage.Id;ComposeContextText=$"Replying to {SelectedMessage.SenderUserName}: {TrimPreview(SelectedMessage.DisplayBody)}";OnPropertyChanged(nameof(HasComposeContext));CancelComposeContextCommand.RaiseCanExecuteChanged();}
    private void EditSelected(){if(!CanEditSelectedMessage||SelectedMessage is null)return;_replyToMessageId=0;_editingMessageId=SelectedMessage.Id;MessageText=SelectedMessage.Body;ComposeContextText=$"Editing your message from {SelectedMessage.CreatedUtc.ToLocalTime():g}";OnPropertyChanged(nameof(HasComposeContext));CancelComposeContextCommand.RaiseCanExecuteChanged();}
    private void CancelComposeContext(){_replyToMessageId=0;_editingMessageId=0;ComposeContextText=string.Empty;OnPropertyChanged(nameof(HasComposeContext));CancelComposeContextCommand.RaiseCanExecuteChanged();}
    private static string TrimPreview(string value)=>value.Length>90?value[..90]+"...":value;

    private async Task DeleteSelectedAsync()
    {
        if(SelectedMessage is null)return;try{if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.DeleteMessageAsync(SelectedMessage.Id);}else _local!.DeleteMessage(SelectedMessage.Id,CurrentUser,!SelectedMessage.SenderUserName.Equals(CurrentUser,StringComparison.OrdinalIgnoreCase)&&CanManageAnnouncements);Status="Message deleted. Audit/history retention remains on the Main Server.";await RefreshAsync();}catch(Exception ex){Status="Message delete failed: "+ex.Message;}
    }
    private async Task PinSelectedAsync(){if(SelectedMessage is null)return;try{var pinned=!SelectedMessage.IsPinned;if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.SetMessagePinnedAsync(SelectedMessage.Id,pinned);}else _local!.SetMessagePinned(SelectedMessage.Id,CurrentUser,pinned);Status=pinned?"Message pinned.":"Message unpinned.";await RefreshAsync();}catch(Exception ex){Status="Pin change failed: "+ex.Message;}}
    private async Task ReactSelectedAsync(string reaction){if(SelectedMessage is null)return;try{if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.ToggleMessageReactionAsync(SelectedMessage.Id,reaction);}else _local!.ToggleReaction(SelectedMessage.Id,CurrentUser,reaction);await RefreshAsync(silent:true);ApplyMessageFilter();}catch(Exception ex){Status="Reaction failed: "+ex.Message;}}

    private async Task MarkSelectedConversationReadAsync()
    {
        if(SelectedConversation is null)return;try{if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.MarkConversationReadAsync(SelectedConversation.Key);}else _local?.MarkConversationRead(CurrentUser,SelectedConversation.Key);foreach(var m in Messages.Where(x=>x.ConversationKey.Equals(SelectedConversation.Key,StringComparison.OrdinalIgnoreCase)))m.IsUnreadForCurrentUser=false;UnreadCount=Messages.Count(x=>x.IsUnreadForCurrentUser);var row=Conversations.FirstOrDefault(x=>x.Key.Equals(SelectedConversation.Key,StringComparison.OrdinalIgnoreCase));if(row is not null)row.UnreadCount=0;}catch{ }
    }

    private async Task SaveMyStatusAsync()
    {
        try{UserChatStatusRecord status;if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();status=await api.SetUserChatStatusAsync(MyAvailability,MyStatusMessage);}else status=_local!.SetUserChatStatus(CurrentUser,MyAvailability,MyStatusMessage);MyAvailability=status.Availability;MyStatusMessage=status.StatusMessage;Status="Your collaboration status was updated.";await RefreshAsync(silent:true);}catch(Exception ex){Status="Status update failed: "+ex.Message;}
    }

    private async Task AttachFileAsync()
    {
        if(SelectedConversation is null||!CanShareFiles||!CanSend||!CanSendSelectedConversation)return;
        var dialog=new OpenFileDialog{Title=$"Share file to {SelectedConversation.Title}",Multiselect=false,CheckFileExists=true};if(dialog.ShowDialog()!=true)return;
        CollaborationMessageRecord? attachmentMessage=null;
        try
        {
            var fileName=Path.GetFileName(dialog.FileName);
            attachmentMessage=BuildOutgoingMessage($"Shared file: {fileName}");
            attachmentMessage.ReplyToMessageId=0;
            if(NetworkClientRuntime.IsNetworkClient)
            {
                using var api=CreateApi();
                attachmentMessage=await api.SendMessageAsync(attachmentMessage);
                await api.UploadSharedFileAsync(dialog.FileName,attachmentMessage.ClientId,attachmentMessage.FinancialYear,attachmentMessage.Id);
            }
            else
            {
                attachmentMessage=_local!.AddMessage(attachmentMessage);
                await using var input=File.OpenRead(dialog.FileName);
                _local.SaveFile(input,fileName,CurrentUser,attachmentMessage.ClientId,attachmentMessage.FinancialYear,attachmentMessage.Id);
            }
            Status=$"{fileName} shared in {SelectedConversation.Title}. The Main Server retains the file separately from accounting data.";
            await RefreshAsync();
        }
        catch(Exception ex)
        {
            if(attachmentMessage is { Id: >0 })
            {
                try
                {
                    if(NetworkClientRuntime.IsNetworkClient){using var cleanup=CreateApi();await cleanup.DeleteMessageAsync(attachmentMessage.Id);}
                    else _local?.DeleteMessage(attachmentMessage.Id,CurrentUser);
                }
                catch { }
            }
            Status="File share failed: "+ex.Message;
        }
    }

    private async Task DownloadSharedFileAsync()
    {
        if(SelectedSharedFile is null)return;var dialog=new SaveFileDialog{Title="Download AAH shared file",FileName=SelectedSharedFile.OriginalName,OverwritePrompt=true};if(dialog.ShowDialog()!=true)return;
        try{if(NetworkClientRuntime.IsNetworkClient){using var api=CreateApi();await api.DownloadSharedFileAsync(SelectedSharedFile.FileId,dialog.FileName);}else{if(string.IsNullOrWhiteSpace(SelectedSharedFile.StoredPath)||!File.Exists(SelectedSharedFile.StoredPath))throw new FileNotFoundException("The retained Main Server shared file is unavailable.");File.Copy(SelectedSharedFile.StoredPath,dialog.FileName,true);}Status=$"Downloaded {SelectedSharedFile.OriginalName}.";}
        catch(Exception ex){Status="Shared file download failed: "+ex.Message;}
    }

    private void OpenSharedFolder(){try{if(_local is null)return;System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName=_local.RootFolder,UseShellExecute=true});}catch(Exception ex){Status=ex.Message;}}
    private bool CanAccessLocalMessage(CollaborationMessageRecord message)
    {
        if(_governance is null)return false;
        if(message.ClientId>0&&!_governance.HasClientAccess(CurrentUser,message.ClientId,message.FinancialYear))return false;
        if(string.IsNullOrWhiteSpace(message.RecipientUserName))return true;
        return message.RecipientUserName.Equals(CurrentUser,StringComparison.OrdinalIgnoreCase)||message.SenderUserName.Equals(CurrentUser,StringComparison.OrdinalIgnoreCase);
    }
    private bool CanAccessLocalFile(CollaborationFileRecord file)
    {
        if(_governance is null||_local is null)return false;
        if(file.ClientId>0&&!_governance.HasClientAccess(CurrentUser,file.ClientId,file.FinancialYear))return false;
        if(file.MessageId<=0)return file.ClientId>0||file.UploadedBy.Equals(CurrentUser,StringComparison.OrdinalIgnoreCase);
        var message=_local.GetMessage(file.MessageId);return message is not null&&CanAccessLocalMessage(message);
    }
    private bool LocalHasPermission(string permission){if(_governance is null)return false;var user=_governance.GetCurrentUser();return user.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase)||_governance.HasPermission(Environment.UserName,permission);}
    private bool LocalHasAnyPermission(params string[] permissions)=>permissions.Any(LocalHasPermission);
    private void RaiseMessageActionCommands(){ReplySelectedCommand.RaiseCanExecuteChanged();EditSelectedCommand.RaiseCanExecuteChanged();DeleteSelectedCommand.RaiseCanExecuteChanged();PinSelectedCommand.RaiseCanExecuteChanged();ReactLikeCommand.RaiseCanExecuteChanged();ReactDoneCommand.RaiseCanExecuteChanged();ReactThanksCommand.RaiseCanExecuteChanged();}
    private static FullUiNetworkClientApi CreateApi()=>new(){Server=NetworkClientRuntime.ResolveServerEndpoint(),DeviceId=NetworkClientRuntime.DeviceId,DeviceToken=NetworkClientRuntime.DeviceToken,SessionToken=NetworkClientRuntime.SessionToken};
}

public sealed class ChatConversationRow : ViewModelBase
{
    private int _unreadCount;
    public string Key{get;set;}=string.Empty;
    public string Kind{get;set;}=string.Empty;
    public string Title{get;set;}=string.Empty;
    public string Subtitle{get;set;}=string.Empty;
    public string RecipientUserId{get;set;}=string.Empty;
    public int UnreadCount{get=>_unreadCount;set{if(SetProperty(ref _unreadCount,value)){OnPropertyChanged(nameof(UnreadText));OnPropertyChanged(nameof(HasUnread));}}}
    public string LastMessage{get;set;}=string.Empty;
    public DateTime? LastUtc{get;set;}
    public bool IsPinned{get;set;}
    public string UnreadText=>UnreadCount>0?$"{UnreadCount} unread":string.Empty;
    public bool HasUnread=>UnreadCount>0;
}
