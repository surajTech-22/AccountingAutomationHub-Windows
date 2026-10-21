using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AccountingAutomationHub.Desktop;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>App-wide collaboration notifier. It runs independently of TeamView so unread Private, Global,
/// Client/FY and Announcement messages can notify the user while working elsewhere or while AAH LAN is in the tray.</summary>
public sealed class CollaborationNotificationAgentService : IDisposable
{
    private readonly App _app; private readonly Timer _timer; private int _running; private bool _disposed; private bool _primed; private readonly HashSet<long> _seen=[];
    public CollaborationNotificationAgentService(App app){_app=app;_timer=new Timer(_=>_=PollAsync(),null,TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(3));}
    private async Task PollAsync(){if(_disposed||Interlocked.Exchange(ref _running,1)==1)return;try{IReadOnlyList<CollaborationMessageRecord> rows;var current=NetworkClientRuntime.IsNetworkClient?NetworkClientRuntime.UserName:Environment.UserName;if(NetworkClientRuntime.IsNetworkClient){if(string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken))return;using var api=new FullUiNetworkClientApi{Server=NetworkClientRuntime.ResolveServerEndpoint(),DeviceId=NetworkClientRuntime.DeviceId,DeviceToken=NetworkClientRuntime.DeviceToken,SessionToken=NetworkClientRuntime.SessionToken};rows=await api.GetMessagesAsync().ConfigureAwait(false);if(!NetworkLocalPreferenceService.Load().MessageNotificationsEnabled){foreach(var x in rows)_seen.Add(x.Id);_primed=true;return;}}else{var repo=new CollaborationRepository();repo.Initialize();rows=repo.GetInbox(current,400);}
        if(!_primed){foreach(var x in rows)_seen.Add(x.Id);_primed=true;return;}
        foreach(var m in rows.Where(x=>x.IsUnreadForCurrentUser&&!x.SenderUserName.Equals(current,StringComparison.OrdinalIgnoreCase)&&!_seen.Contains(x.Id)).OrderBy(x=>x.CreatedUtc)){_seen.Add(m.Id);var channel=m.ConversationType.Equals("Private",StringComparison.OrdinalIgnoreCase)?"Private message":m.ConversationType.Equals("Announcement",StringComparison.OrdinalIgnoreCase)?"Announcement":m.ConversationType.Equals("Client",StringComparison.OrdinalIgnoreCase)?"Client/FY message":"Global message";var body=m.DisplayBody.Length>180?m.DisplayBody[..180]+"…":m.DisplayBody;_app.ShowCollaborationNotification($"AAH {channel}",$"{m.SenderUserName}: {body}",m.Priority.Equals("Urgent",StringComparison.OrdinalIgnoreCase));}
        if(_seen.Count>5000){var keep=rows.Select(x=>x.Id).ToHashSet();_seen.RemoveWhere(x=>!keep.Contains(x));}}catch{/* Collaboration notification failure never blocks AAH. */}finally{Interlocked.Exchange(ref _running,0);}}
    public void Dispose(){_disposed=true;_timer.Dispose();}
}
