using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Main Server enterprise administration surface. The LAN client never instantiates this view model.
/// All destructive or privileged mutations are re-authorized by SecureLanServerService.
/// </summary>
public sealed class AdministrationViewModel : PageViewModel
{
    private readonly SecureLanServerService? _server;
    private readonly ApplicationPreferenceService _settings;
    private readonly SqliteGovernanceRepository _governance = new();
    private readonly SqliteClientRepository _clients = new();
    private AdminDeviceRow? _selectedDevice;
    private ClientAccessMatrixRow? _selectedAccess;
    private ApplicationUserRecord? _selectedUser;
    private AdminActiveUserRow? _selectedActiveUser;
    private NetworkSessionRecord? _selectedSession;
    private RemoteAssistSessionRecord? _selectedRemoteAssistSession;
    private string _selectedRemoteAction = RemoteAahActions.RefreshSnapshot;
    private string _remoteMessage = string.Empty;
    private string _status = "Administration control plane ready.";
    private string _pairingCode = "No enrollment code generated.";
    private string _policySummary = string.Empty;
    private string _auditSummary = string.Empty;
    private string _selectedRole = GovernanceRoles.ReadOnly;
    private string _deviceTag = string.Empty;
    private string _newDeviceGroup = string.Empty;
    private string _selectedDeviceGroup = string.Empty;
    private string _lockdownReason = string.Empty;
    private bool _emergencyLockdown;
    private string _emergencyLockdownSummary = "Emergency Lockdown is off.";
    private string _deploymentSummary = "Deployment status not loaded.";
    private string _snapshotDiagnostic = "Snapshot diagnostic not run in this Administration session.";
    private string _selectedSimulationAction = GovernancePermissions.ReadData;
    private bool _simulationRemoteRoute;
    private string _accessSimulationResult = "Select a Client Access row, choose an action, then simulate the effective policy.";
    private string _selectedAccessDuration = "Permanent";
    private string _remoteAssistMode = RemoteAssistPrivilegeModes.RemoteUser;
    private int _remoteAssistDurationMinutes = 30;
    private string _remoteAssistScope = "AAH Only";
    private string _remoteAssistReason = string.Empty;
    private string _remoteAssistPage = "Settings";
    private string _remotePreferenceKey = nameof(NetworkLocalPreferences.NotificationsEnabled);
    private string _remotePreferenceValue = "true";
    private bool _remotePerActionUseHelperPermission = true;
    private string _troubleshootingSummary = "Select a device to inspect its registration, posture, sessions, assignments and recent events.";
    private string _selectedUserLoginSummary = "Select a user to inspect login/lockout status.";
    private DeploymentRingRecord? _selectedDeploymentRing;
    private string _selectedDeviceUpdateRing = "Production";
    private bool _deploymentRingEnabled = true;
    private DateTime? _deploymentRingForceAfterDate;
    private bool _remoteNotificationUrgent;
    private BitmapImage? _remoteAssistRemoteFrame;
    private long _remoteAssistRemoteFrameSequence;
    private long _remoteAssistAdministratorInputSequence;
    private string _lastAdministratorFrameHash = string.Empty;
    private DateTime _lastRemoteAssistRuntimeRefreshUtc = DateTime.MinValue;
    private string _remoteAssistStreamStatus = "No active visual Remote Assist stream.";
    private bool _shareAdministratorAah;
    private bool _allowRemoteControlOfAdministrator;
    private bool _remoteAssistAutoAccept;
    private long _liveAuthorizationRevision;
    private long _liveDevicePolicyRevision;
    private long _liveClientAccessRevision;
    private long _liveSecurityPolicyRevision;
    private DateTime _lastAdministrationAuditLiveRefreshUtc = DateTime.MinValue;
    private readonly DispatcherTimer _liveRefreshTimer;
    private readonly DispatcherTimer _remoteAssistStreamTimer;
    private RemoteAssistViewerWindow? _remoteAssistViewerWindow;

    public AdministrationViewModel(ApplicationPreferenceService settings, SecureLanServerService? server)
        : base("Administration", "Main Server control plane for identities, active users, devices, client access, policy, security, deployments, Remote AAH, audit and diagnostics")
    {
        _settings = settings;
        _server = server;
        _governance.Initialize();
        _clients.Initialize();

        Users=[];ActiveUsers=[];Devices=[];RemoteTargets=[];Sessions=[];ClientAccess=[];RegistrationOtps=[];SecurityEvents=[];RemoteCommands=[];RemoteAssistSessions=[];RolePermissions=[];AuditRecords=[];SelectedDeviceAudit=[];DeviceGroups=[];DeploymentRings=[];
        RemoteActions=RemoteAahActions.Allowed
            .Where(x=>!x.Equals(RemoteAahActions.RequestRemoteAssist,StringComparison.OrdinalIgnoreCase)
                      &&!x.Equals(RemoteAahActions.RemoteAssistNavigate,StringComparison.OrdinalIgnoreCase)
                      &&!x.Equals(RemoteAahActions.RemoteAssistSetLocalPreference,StringComparison.OrdinalIgnoreCase)
                      &&!x.Equals(RemoteAahActions.RemoteAssistEnd,StringComparison.OrdinalIgnoreCase))
            .OrderBy(x=>x).ToArray();
        RoleNames=GovernanceRoles.All.ToArray();
        SimulationActions=GovernancePermissions.All.OrderBy(x=>x).ToArray();
        AccessDurations=["Permanent","1 hour","4 hours","8 hours","24 hours","7 days"];
        RemoteAssistModes=RemoteAssistPrivilegeModes.All.ToArray();
        RemoteAssistDurations=[10,30,60];
        RemotePreferenceKeys=[nameof(NetworkLocalPreferences.StartWithWindows),nameof(NetworkLocalPreferences.CloseToTray),nameof(NetworkLocalPreferences.NotificationsEnabled),nameof(NetworkLocalPreferences.RememberWindowPlacement),nameof(NetworkLocalPreferences.Theme),nameof(NetworkLocalPreferences.AccentColor),nameof(NetworkLocalPreferences.GridDensity)];

        RefreshCommand=new RelayCommand(Refresh);
        CreateUserCommand=new RelayCommand(CreateUser,()=>Can(GovernancePermissions.ManageUsers));
        EditUserCommand=new RelayCommand(EditUser,()=>SelectedUser is not null&&Can(GovernancePermissions.ManageUsers));
        ApplyUserRoleCommand=new RelayCommand(ApplyUserRole,()=>SelectedUser is not null&&Can(GovernancePermissions.ManageUsers));
        EnableUserCommand=new RelayCommand(()=>SetUserActive(true),()=>SelectedUser is not null&&!SelectedUser.IsActive&&Can(GovernancePermissions.ManageUsers));
        DisableUserCommand=new RelayCommand(()=>SetUserActive(false),()=>SelectedUser is not null&&SelectedUser.IsActive&&Can(GovernancePermissions.ManageUsers));
        RevokeUserSessionsCommand=new RelayCommand(RevokeUserSessions,()=>SelectedUser is not null&&Can(GovernancePermissions.ManageUsers));
        UnlockUserCommand=new RelayCommand(UnlockUser,()=>SelectedUser is not null&&Can(GovernancePermissions.ManageUsers));
        SaveRolePermissionsCommand=new RelayCommand(SaveRolePermissions,()=>Can(GovernancePermissions.ManageRoles)&&SelectedRole.Equals(GovernanceRoles.Custom,StringComparison.OrdinalIgnoreCase));
        GeneratePairingCommand=new RelayCommand(GeneratePairing,()=>Can(GovernancePermissions.ManageDevices)||Can(GovernancePermissions.ManageServer));
        ResetTrustCommand=new RelayCommand(ResetTrust,()=>SelectedDevice is not null&&Can(GovernancePermissions.ResetDeviceTrust));
        RevokeDeviceCommand=new RelayCommand(RevokeDevice,()=>SelectedDevice is not null&&Can(GovernancePermissions.ManageDevices));
        DisableDeviceCommand=new RelayCommand(()=>SetDeviceLifecycle(ServerEnrollmentStates.Disabled,false),()=>SelectedDevice is not null&&SelectedDevice.IsActive&&Can(GovernancePermissions.ManageDevices));
        EnableDeviceCommand=new RelayCommand(()=>SetDeviceLifecycle(ServerEnrollmentStates.Active,true),()=>SelectedDevice is not null&&!SelectedDevice.IsActive&&Can(GovernancePermissions.ManageDevices));
        RetireDeviceCommand=new RelayCommand(()=>SetDeviceLifecycle(ServerEnrollmentStates.Retired,false),()=>SelectedDevice is not null&&Can(GovernancePermissions.ManageDevices));
        MarkLostDeviceCommand=new RelayCommand(()=>SetDeviceLifecycle(ServerEnrollmentStates.Lost,false),()=>SelectedDevice is not null&&Can(GovernancePermissions.ManageDevices));
        DeleteDeviceCommand=new RelayCommand(DeleteDevicePermanently,()=>SelectedDevice is not null&&Can(GovernancePermissions.ManageDevices));
        DeleteAllAndroidDevicesCommand=new RelayCommand(DeleteAllAndroidDevicesPermanently,()=>Can(GovernancePermissions.ManageDevices));
        QuarantineCommand=new RelayCommand(()=>SetQuarantine(true),()=>SelectedDevice is not null&&Can(GovernancePermissions.ManageDevices));
        ClearQuarantineCommand=new RelayCommand(()=>SetQuarantine(false),()=>SelectedDevice is not null&&Can(GovernancePermissions.ManageDevices));
        AddDeviceTagCommand=new RelayCommand(()=>SetDeviceTag(true),()=>SelectedDevice is not null&&!string.IsNullOrWhiteSpace(DeviceTag)&&Can(GovernancePermissions.ManageDevices));
        RemoveDeviceTagCommand=new RelayCommand(()=>SetDeviceTag(false),()=>SelectedDevice is not null&&!string.IsNullOrWhiteSpace(DeviceTag)&&Can(GovernancePermissions.ManageDevices));
        CreateDeviceGroupCommand=new RelayCommand(CreateDeviceGroup,()=>!string.IsNullOrWhiteSpace(NewDeviceGroup)&&Can(GovernancePermissions.ManageDevices));
        AddToDeviceGroupCommand=new RelayCommand(()=>SetDeviceGroupMembership(true),()=>SelectedDevice is not null&&!string.IsNullOrWhiteSpace(SelectedDeviceGroup)&&Can(GovernancePermissions.ManageDevices));
        RemoveFromDeviceGroupCommand=new RelayCommand(()=>SetDeviceGroupMembership(false),()=>SelectedDevice is not null&&!string.IsNullOrWhiteSpace(SelectedDeviceGroup)&&Can(GovernancePermissions.ManageDevices));
        QueueRemoteCommand=new RelayCommand(QueueRemote,()=>SelectedDevice is not null&&SelectedDevice.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&SelectedDevice.IsActive&&CanQueueRemoteAction());
        SendRemoteNotificationCommand=new RelayCommand(SendRemoteNotification,()=>SelectedDevice is not null&&SelectedDevice.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&SelectedDevice.IsActive&&!string.IsNullOrWhiteSpace(RemoteMessage)&&Can(GovernancePermissions.RemoteAah));
        ForceOpenAahCommand=new RelayCommand(()=>QueueTroubleshooting(RemoteAahActions.ForceOpenAah),()=>SelectedDevice is not null&&SelectedDevice.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&SelectedDevice.IsActive&&Can(GovernancePermissions.RemoteAah));
        QueueGroupRemoteCommand=new RelayCommand(QueueGroupRemote,()=>!string.IsNullOrWhiteSpace(SelectedDeviceGroup)&&Can(GovernancePermissions.RemoteAah));
        StartRemoteAssistCommand=new RelayCommand(StartRemoteAssist,()=>SelectedDevice is not null&&SelectedDevice.RemoteAssistEligible&&!HasSelectedActiveRemoteAssist&&Can(GovernancePermissions.RemoteAssist));
        EndRemoteAssistCommand=new RelayCommand(EndRemoteAssist,()=>SelectedRemoteAssistSession is not null&&SelectedRemoteAssistSession.IsActive&&Can(GovernancePermissions.RemoteAssist));
        RemoteAssistNavigateCommand=new RelayCommand(RemoteAssistNavigate,()=>SelectedDevice is not null&&SelectedRemoteAssistSession is not null&&SelectedRemoteAssistSession.IsActive&&SelectedRemoteAssistSession.UserConsented&&SelectedRemoteAssistSession.DeviceId.Equals(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase)&&Can(GovernancePermissions.RemoteAssist));
        RemoteAssistPreferenceCommand=new RelayCommand(RemoteAssistSetPreference,()=>SelectedDevice is not null&&SelectedRemoteAssistSession is not null&&SelectedRemoteAssistSession.IsActive&&SelectedRemoteAssistSession.UserConsented&&SelectedRemoteAssistSession.DeviceId.Equals(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase)&&Can(GovernancePermissions.RemoteAssist));
        GrantAccessCommand=new RelayCommand(()=>SetClientAccess(true),()=>SelectedAccess is not null&&Can(GovernancePermissions.ManageClientAccess));
        RevokeAccessCommand=new RelayCommand(()=>SetClientAccess(false),()=>SelectedAccess is not null&&Can(GovernancePermissions.ManageClientAccess));
        SimulateAccessCommand=new RelayCommand(SimulateAccess,()=>SelectedAccess is not null&&(Can(GovernancePermissions.ViewAudit)||Can(GovernancePermissions.ManageServer)));
        RevokeSessionCommand=new RelayCommand(RevokeSession,()=>SelectedSession is not null&&Can(GovernancePermissions.ManageUsers));
        RevokeActiveUserSessionsCommand=new RelayCommand(RevokeActiveUserSessions,()=>SelectedActiveUser is not null&&Can(GovernancePermissions.ManageUsers));
        EnableLockdownCommand=new RelayCommand(()=>SetLockdown(true),()=>Can(GovernancePermissions.ManageServer));
        DisableLockdownCommand=new RelayCommand(()=>SetLockdown(false),()=>Can(GovernancePermissions.ManageServer));
        PublishUpdateCommand=new RelayCommand(()=>PublishUpdate(false),()=>Can(GovernancePermissions.ManageLanUpdates));
        ForceUpdateCommand=new RelayCommand(()=>PublishUpdate(true),()=>Can(GovernancePermissions.ManageLanUpdates));
        SaveDeploymentRingCommand=new RelayCommand(SaveDeploymentRing,()=>SelectedDeploymentRing is not null&&Can(GovernancePermissions.ManageLanUpdates));
        AssignDeviceRingCommand=new RelayCommand(AssignDeviceRing,()=>SelectedDevice is not null&&!string.IsNullOrWhiteSpace(SelectedDeviceUpdateRing)&&Can(GovernancePermissions.ManageLanUpdates));
        RunSnapshotDiagnosticCommand=new RelayCommand(RunSnapshotDiagnostic,()=>Can(GovernancePermissions.ViewAudit)||Can(GovernancePermissions.ManageServer));
        RunBackupCommand=new AsyncRelayCommand(RunBackupAsync,()=>Can(GovernancePermissions.ManageBackup));
        VerifyAuditCommand=new RelayCommand(VerifyAudit,()=>Can(GovernancePermissions.ViewAudit)||Can(GovernancePermissions.ManageServer));
        QueueDiagnosticsCommand=new RelayCommand(()=>QueueTroubleshooting(RemoteAahActions.CollectDiagnostics),()=>SelectedDevice is not null&&SelectedDevice.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&SelectedDevice.IsActive&&Can(GovernancePermissions.RemoteAah));
        QueueRefreshCommand=new RelayCommand(()=>QueueTroubleshooting(RemoteAahActions.RefreshSnapshot),()=>SelectedDevice is not null&&SelectedDevice.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&SelectedDevice.IsActive&&Can(GovernancePermissions.RemoteAah));
        SaveRemoteAssistAutoAcceptCommand=new RelayCommand(SaveRemoteAssistAutoAccept,()=>SelectedDevice is not null&&SelectedDevice.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&IsPrimaryAdministrator);
        OpenRemoteAssistViewerCommand=new RelayCommand(OpenRemoteAssistViewer,()=>SelectedRemoteAssistSession is not null&&SelectedRemoteAssistSession.IsActive&&SelectedRemoteAssistSession.UserConsented);

        _remoteAssistStreamTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(350)};
        _remoteAssistStreamTimer.Tick+=async (_,_)=>await RemoteAssistStreamTickAsync();
        _remoteAssistStreamTimer.Start();

        _liveRefreshTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(2)};
        _liveRefreshTimer.Tick+=(_,_)=>{try{RefreshLiveRuntime();}catch{/* live Administration reconciliation must never interrupt Remote Assist */}};
        _liveRefreshTimer.Start();
        Refresh();
    }

    public ObservableCollection<ApplicationUserRecord> Users{get;}
    public ObservableCollection<AdminActiveUserRow> ActiveUsers{get;}
    public ObservableCollection<AdminDeviceRow> Devices{get;}
    public ObservableCollection<AdminDeviceRow> RemoteTargets{get;}
    public ObservableCollection<NetworkSessionRecord> Sessions{get;}
    public ObservableCollection<ClientAccessMatrixRow> ClientAccess{get;}
    public ObservableCollection<SecureLanServerService.NetworkMfaChallengeInfo> RegistrationOtps{get;}
    public ObservableCollection<ServerSecurityEventRecord> SecurityEvents{get;}
    public ObservableCollection<RemoteAahCommandRecord> RemoteCommands{get;}
    public ObservableCollection<RemoteAssistSessionRecord> RemoteAssistSessions{get;}
    public ObservableCollection<RolePermissionRecord> RolePermissions{get;}
    public ObservableCollection<AdminAuditRecord> AuditRecords{get;}
    public ObservableCollection<AdminAuditRecord> SelectedDeviceAudit{get;}
    public ObservableCollection<string> DeviceGroups{get;}
    public ObservableCollection<DeploymentRingRecord> DeploymentRings{get;}
    public IReadOnlyList<string> RemoteActions{get;}
    public IReadOnlyList<string> RoleNames{get;}
    public IReadOnlyList<string> SimulationActions{get;}
    public IReadOnlyList<string> AccessDurations{get;}
    public IReadOnlyList<string> RemoteAssistModes{get;}
    public IReadOnlyList<int> RemoteAssistDurations{get;}
    public IReadOnlyList<string> RemotePreferenceKeys{get;}

    public RelayCommand RefreshCommand{get;}public RelayCommand CreateUserCommand{get;}public RelayCommand EditUserCommand{get;}public RelayCommand ApplyUserRoleCommand{get;}public RelayCommand EnableUserCommand{get;}public RelayCommand DisableUserCommand{get;}public RelayCommand RevokeUserSessionsCommand{get;}public RelayCommand UnlockUserCommand{get;}public RelayCommand SaveRolePermissionsCommand{get;}public RelayCommand GeneratePairingCommand{get;}public RelayCommand ResetTrustCommand{get;}public RelayCommand RevokeDeviceCommand{get;}public RelayCommand DisableDeviceCommand{get;}public RelayCommand EnableDeviceCommand{get;}public RelayCommand RetireDeviceCommand{get;}public RelayCommand MarkLostDeviceCommand{get;}public RelayCommand DeleteDeviceCommand{get;}public RelayCommand DeleteAllAndroidDevicesCommand{get;}public RelayCommand QuarantineCommand{get;}public RelayCommand ClearQuarantineCommand{get;}public RelayCommand AddDeviceTagCommand{get;}public RelayCommand RemoveDeviceTagCommand{get;}public RelayCommand CreateDeviceGroupCommand{get;}public RelayCommand AddToDeviceGroupCommand{get;}public RelayCommand RemoveFromDeviceGroupCommand{get;}public RelayCommand QueueRemoteCommand{get;}
    public RelayCommand SendRemoteNotificationCommand{get;}public RelayCommand ForceOpenAahCommand{get;}public RelayCommand QueueGroupRemoteCommand{get;}public RelayCommand StartRemoteAssistCommand{get;}public RelayCommand EndRemoteAssistCommand{get;}public RelayCommand RemoteAssistNavigateCommand{get;}public RelayCommand RemoteAssistPreferenceCommand{get;}public RelayCommand OpenRemoteAssistViewerCommand{get;}public RelayCommand GrantAccessCommand{get;}public RelayCommand RevokeAccessCommand{get;}public RelayCommand SimulateAccessCommand{get;}public RelayCommand RevokeSessionCommand{get;}public RelayCommand RevokeActiveUserSessionsCommand{get;}public RelayCommand EnableLockdownCommand{get;}public RelayCommand DisableLockdownCommand{get;}public RelayCommand PublishUpdateCommand{get;}public RelayCommand ForceUpdateCommand{get;}public RelayCommand SaveDeploymentRingCommand{get;}public RelayCommand AssignDeviceRingCommand{get;}public RelayCommand RunSnapshotDiagnosticCommand{get;}public AsyncRelayCommand RunBackupCommand{get;}public RelayCommand VerifyAuditCommand{get;}public RelayCommand QueueDiagnosticsCommand{get;}public RelayCommand QueueRefreshCommand{get;}public RelayCommand SaveRemoteAssistAutoAcceptCommand{get;}

    public AdminDeviceRow? SelectedDevice{get=>_selectedDevice;set{var targetChanged=!string.Equals(_selectedDevice?.DeviceId,value?.DeviceId,StringComparison.OrdinalIgnoreCase);if(!SetProperty(ref _selectedDevice,value))return;if(targetChanged){_remoteAssistRemoteFrameSequence=0;_remoteAssistAdministratorInputSequence=0;_lastAdministratorFrameHash=string.Empty;RemoteAssistRemoteFrame=null;ShareAdministratorAah=false;AllowRemoteControlOfAdministrator=false;}OnPropertyChanged(nameof(SelectedDeviceSummary));OnPropertyChanged(nameof(SelectedDeviceTags));OnPropertyChanged(nameof(RemoteTargetSummary));OnPropertyChanged(nameof(RemoteAssistAccessSummary));OnPropertyChanged(nameof(RemoteAssistDeliverySummary));OnPropertyChanged(nameof(RemoteAssistTransportSummary));SelectRemoteAssistSessionForTarget();BuildTroubleshootingSummary();BuildSelectedDeviceAudit();RefreshSelectedDeviceUpdateRing();RefreshRemoteAssistDevicePolicy();RaiseCommands();}}
    public ClientAccessMatrixRow? SelectedAccess{get=>_selectedAccess;set{if(!SetProperty(ref _selectedAccess,value))return;AccessSimulationResult="Choose an action and run Access Simulator for this User + Client/FY. Device posture is taken from that user's most recent active trusted device.";RaiseCommands();}}
    public ApplicationUserRecord? SelectedUser{get=>_selectedUser;set{if(!SetProperty(ref _selectedUser,value))return;if(value is not null)SelectedRole=value.RoleName;RefreshSelectedUserLoginSummary();OnPropertyChanged(nameof(SelectedUserSummary));RaiseCommands();}}
    public AdminActiveUserRow? SelectedActiveUser{get=>_selectedActiveUser;set{if(SetProperty(ref _selectedActiveUser,value))RaiseCommands();}}
    public NetworkSessionRecord? SelectedSession{get=>_selectedSession;set{if(SetProperty(ref _selectedSession,value))RaiseCommands();}}
    public RemoteAssistSessionRecord? SelectedRemoteAssistSession{get=>_selectedRemoteAssistSession;set{var sessionChanged=!string.Equals(_selectedRemoteAssistSession?.SessionId,value?.SessionId,StringComparison.OrdinalIgnoreCase);if(!SetProperty(ref _selectedRemoteAssistSession,value))return;if(sessionChanged){_remoteAssistRemoteFrameSequence=0;_remoteAssistAdministratorInputSequence=0;_lastAdministratorFrameHash=string.Empty;RemoteAssistRemoteFrame=null;}OnPropertyChanged(nameof(RemoteAssistAccessSummary));OnPropertyChanged(nameof(RemoteAssistDeliverySummary));OnPropertyChanged(nameof(HasSelectedActiveRemoteAssist));RaiseCommands();}}
    public string SelectedRemoteAction{get=>_selectedRemoteAction;set{if(SetProperty(ref _selectedRemoteAction,value))QueueRemoteCommand.RaiseCanExecuteChanged();}}
    public string RemoteMessage{get=>_remoteMessage;set{if(SetProperty(ref _remoteMessage,value))SendRemoteNotificationCommand.RaiseCanExecuteChanged();}}
    public bool RemoteNotificationUrgent{get=>_remoteNotificationUrgent;set=>SetProperty(ref _remoteNotificationUrgent,value);}
    public BitmapImage? RemoteAssistRemoteFrame{get=>_remoteAssistRemoteFrame;private set=>SetProperty(ref _remoteAssistRemoteFrame,value);}
    public string RemoteAssistStreamStatus{get=>_remoteAssistStreamStatus;private set=>SetProperty(ref _remoteAssistStreamStatus,value);}
    public bool ShareAdministratorAah{get=>_shareAdministratorAah;set{if(SetProperty(ref _shareAdministratorAah,value))ApplyAdministratorShareState();}}
    public bool AllowRemoteControlOfAdministrator{get=>_allowRemoteControlOfAdministrator;set{if(SetProperty(ref _allowRemoteControlOfAdministrator,value))ApplyAdministratorShareState();}}
    public bool RemoteAssistAutoAccept{get=>_remoteAssistAutoAccept;set=>SetProperty(ref _remoteAssistAutoAccept,value);}
    public bool IsPrimaryAdministrator=>_governance.GetCurrentUser().RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase);
    public string Status{get=>_status;private set=>SetProperty(ref _status,value);}public string PairingCode{get=>_pairingCode;private set=>SetProperty(ref _pairingCode,value);}public string PolicySummary{get=>_policySummary;private set=>SetProperty(ref _policySummary,value);}public string AuditSummary{get=>_auditSummary;private set=>SetProperty(ref _auditSummary,value);}public string SelectedRole{get=>_selectedRole;set{if(SetProperty(ref _selectedRole,value)){LoadRolePermissions();OnPropertyChanged(nameof(SelectedRoleDescription));OnPropertyChanged(nameof(CanEditSelectedRolePermissions));RaiseCommands();}}}
    public string SelectedRoleDescription=>GovernanceRoles.Description(SelectedRole);
    public bool CanEditSelectedRolePermissions=>SelectedRole.Equals(GovernanceRoles.Custom,StringComparison.OrdinalIgnoreCase)&&Can(GovernancePermissions.ManageRoles);public string DeviceTag{get=>_deviceTag;set{if(SetProperty(ref _deviceTag,value))RaiseCommands();}}public string NewDeviceGroup{get=>_newDeviceGroup;set{if(SetProperty(ref _newDeviceGroup,value))CreateDeviceGroupCommand.RaiseCanExecuteChanged();}}public string SelectedDeviceGroup{get=>_selectedDeviceGroup;set{if(SetProperty(ref _selectedDeviceGroup,value)){AddToDeviceGroupCommand.RaiseCanExecuteChanged();RemoveFromDeviceGroupCommand.RaiseCanExecuteChanged();}}}public DeploymentRingRecord? SelectedDeploymentRing{get=>_selectedDeploymentRing;set{if(!SetProperty(ref _selectedDeploymentRing,value))return;if(value is not null){DeploymentRingEnabled=value.IsEnabled;DeploymentRingForceAfterDate=value.ForceAfterUtc?.ToLocalTime();}SaveDeploymentRingCommand.RaiseCanExecuteChanged();}}public string SelectedDeviceUpdateRing{get=>_selectedDeviceUpdateRing;set{if(SetProperty(ref _selectedDeviceUpdateRing,value))AssignDeviceRingCommand.RaiseCanExecuteChanged();}}public bool DeploymentRingEnabled{get=>_deploymentRingEnabled;set=>SetProperty(ref _deploymentRingEnabled,value);}public DateTime? DeploymentRingForceAfterDate{get=>_deploymentRingForceAfterDate;set=>SetProperty(ref _deploymentRingForceAfterDate,value);}public string LockdownReason{get=>_lockdownReason;set=>SetProperty(ref _lockdownReason,value);}public bool EmergencyLockdown{get=>_emergencyLockdown;private set=>SetProperty(ref _emergencyLockdown,value);}public string EmergencyLockdownSummary{get=>_emergencyLockdownSummary;private set=>SetProperty(ref _emergencyLockdownSummary,value);}public string DeploymentSummary{get=>_deploymentSummary;private set=>SetProperty(ref _deploymentSummary,value);}public string SnapshotDiagnostic{get=>_snapshotDiagnostic;private set=>SetProperty(ref _snapshotDiagnostic,value);}public string SelectedSimulationAction{get=>_selectedSimulationAction;set=>SetProperty(ref _selectedSimulationAction,value);}public bool SimulationRemoteRoute{get=>_simulationRemoteRoute;set=>SetProperty(ref _simulationRemoteRoute,value);}public string AccessSimulationResult{get=>_accessSimulationResult;private set=>SetProperty(ref _accessSimulationResult,value);}public string SelectedAccessDuration{get=>_selectedAccessDuration;set=>SetProperty(ref _selectedAccessDuration,value);}public string RemoteAssistMode{get=>_remoteAssistMode;set=>SetProperty(ref _remoteAssistMode,value);}public int RemoteAssistDurationMinutes{get=>_remoteAssistDurationMinutes;set=>SetProperty(ref _remoteAssistDurationMinutes,value);}public string RemoteAssistScope{get=>_remoteAssistScope;set=>SetProperty(ref _remoteAssistScope,value);}public string RemoteAssistReason{get=>_remoteAssistReason;set=>SetProperty(ref _remoteAssistReason,value);}public string RemoteAssistPage{get=>_remoteAssistPage;set=>SetProperty(ref _remoteAssistPage,value);}public string RemotePreferenceKey{get=>_remotePreferenceKey;set=>SetProperty(ref _remotePreferenceKey,value);}public string RemotePreferenceValue{get=>_remotePreferenceValue;set=>SetProperty(ref _remotePreferenceValue,value);}public bool RemotePerActionUseHelperPermission{get=>_remotePerActionUseHelperPermission;set=>SetProperty(ref _remotePerActionUseHelperPermission,value);}public string TroubleshootingSummary{get=>_troubleshootingSummary;private set=>SetProperty(ref _troubleshootingSummary,value);}public string SelectedUserLoginSummary{get=>_selectedUserLoginSummary;private set=>SetProperty(ref _selectedUserLoginSummary,value);}

    public string ServerSummary=>_server is null?"Main Server control service unavailable.":$"{_server.StatusText}\nRuntime {_server.RuntimeVersion} · {_server.RuntimeExecutablePath}\nServer root {_server.ServerRootFolder}";
    public string SelectedDeviceSummary=>SelectedDevice is null?"Select a device for posture, lifecycle, trust, tags and Remote AAH actions.":$"{SelectedDevice.DeviceName} · {SelectedDevice.DeviceId}\n{SelectedDevice.UserName} · {SelectedDevice.DeviceType} · {SelectedDevice.AppVersion} · Update ring {SelectedDevice.UpdateRing}\nHealth {SelectedDevice.HealthState} · Risk {SelectedDevice.RiskScore} · State {SelectedDevice.EnrollmentState} · Registration {(SelectedDevice.RegistrationTrusted?"Trusted":"Verification Required")}";
    public string RemoteTargetSummary=>SelectedDevice is null?"Select a Windows LAN target below. Remote Assist requests cannot be sent until a target is selected.":$"Target: {SelectedDevice.DeviceName} · {SelectedDevice.UserName} · {SelectedDevice.RoleName} · {SelectedDevice.PresenceStatus}\nClient {SelectedDevice.CurrentClient} · FY {SelectedDevice.CurrentFinancialYear} · Module {SelectedDevice.CurrentModule} · {SelectedDevice.RemoteReadyStatus}";
    public string RemoteAssistAccessSummary=>SelectedDevice is null?"No remote target selected.":SelectedRemoteAssistSession is null||!SelectedRemoteAssistSession.DeviceId.Equals(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase)?"No Remote Assist request for the selected target.":!SelectedRemoteAssistSession.IsActive||SelectedRemoteAssistSession.ExpiresUtc<=DateTime.UtcNow?"Remote Assist session ended or expired.":!SelectedRemoteAssistSession.UserConsented?$"Waiting for {SelectedRemoteAssistSession.RemoteUserName} to approve Remote Assist request {SelectedRemoteAssistSession.SessionId}.":$"ACCESS GRANTED · {SelectedRemoteAssistSession.RemoteUserName} approved · Mode {SelectedRemoteAssistSession.PermissionMode} · Expires {SelectedRemoteAssistSession.ExpiresUtc.ToLocalTime():g}";
    public string RemoteAssistDeliverySummary
    {
        get
        {
            if(SelectedDevice is null)return "Request delivery: no target selected.";
            var sessionId=SelectedRemoteAssistSession?.SessionId??string.Empty;
            var command=RemoteCommands
                .Where(x=>x.DeviceId.Equals(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase)&&x.Action.Equals(RemoteAahActions.RequestRemoteAssist,StringComparison.OrdinalIgnoreCase))
                .Where(x=>sessionId.Length==0||x.ArgumentsJson.Contains(sessionId,StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x=>x.CreatedUtc)
                .FirstOrDefault();
            if(SelectedRemoteAssistSession is { IsActive:true, UserConsented:true } active&&active.ExpiresUtc>DateTime.UtcNow)
                return "Request delivery: ACCEPTED · LAN consent/session authority confirmed live.";
            if(command is null)return SelectedRemoteAssistSession is { IsActive:true }?"Request delivery: server session exists; LAN authority watcher is waiting for consent.":"Request delivery: no request has been queued for this target.";
            var detail=string.IsNullOrWhiteSpace(command.ResultDetail)?string.Empty:$" · {command.ResultDetail}";
            return command.State switch
            {
                RemoteAahCommandStates.Pending => $"Request delivery: QUEUED at {command.CreatedUtc.ToLocalTime():HH:mm:ss} · waiting for the live LAN agent/authority watcher.",
                RemoteAahCommandStates.Delivered => $"Request delivery: DELIVERED to LAN at {command.DeliveredUtc?.ToLocalTime():HH:mm:ss} · waiting for Accept/Decline{detail}.",
                RemoteAahCommandStates.Succeeded => $"Request delivery: COMPLETED by LAN{detail}.",
                RemoteAahCommandStates.Failed => $"Request delivery: FAILED{detail}.",
                RemoteAahCommandStates.Expired => $"Request delivery: EXPIRED{detail}.",
                _ => $"Request delivery: {command.State}{detail}."
            };
        }
    }
    public bool HasSelectedActiveRemoteAssist=>SelectedRemoteAssistSession is { IsActive:true } active&&active.ExpiresUtc>DateTime.UtcNow;
    public string RemoteAssistTransportSummary=>_server is null?"Remote Assist transport unavailable.":string.IsNullOrWhiteSpace(_server.RemoteAssistInternetEndpoint)?$"Control/session authority: {_server.Endpoint} · Visual/input stream: same private Main Server route. No separate private Internet/VPN overlay was detected.":$"Control/session authority: {_server.Endpoint} · Visual/input stream prefers {_server.RemoteAssistInternetEndpoint} and falls back automatically to the Main Server route if the preferred transport is unavailable.";
    public string SelectedDeviceTags=>SelectedDevice is null?"Tags: none":$"Tags: {string.Join(", ",_server?.GetAdministrationDeviceTags(SelectedDevice.DeviceId).Select(x=>x.Tag)??Array.Empty<string>())}";
    public string SelectedUserSummary=>SelectedUser is null?"Select a user to manage role and active status.":$"{SelectedUser.DisplayName} · {SelectedUser.WindowsUserName} · Role {SelectedUser.RoleName} · {(SelectedUser.IsActive?"Active":"Disabled")}";
    public int OnlineUserCount=>ActiveUsers.Count(x=>x.ActiveStatus=="Online");public int AwayUserCount=>ActiveUsers.Count(x=>x.ActiveStatus=="Away");public int OfflineUserCount=>ActiveUsers.Count(x=>x.ActiveStatus=="Offline");public int DisabledUserCount=>ActiveUsers.Count(x=>x.ActiveStatus=="Disabled");

    public void Refresh()
    {
        try
        {
            var selectedDeviceId=SelectedDevice?.DeviceId;
            Users.ReplaceWith(_server?.GetAdministrationUsers()??_governance.GetUsers());
            var presenceRows=_server?.GetAdministrationDevicePresence()??Array.Empty<DevicePresenceRecord>();
            var remoteAssistRows=(_server?.GetAdministrationRemoteAssistSessions()??Array.Empty<RemoteAssistSessionRecord>()).Take(250).ToArray();
            RemoteAssistSessions.ReplaceWith(remoteAssistRows);
            var clientNames=_clients.GetAll(includeArchived:true).ToDictionary(x=>x.Id,x=>$"{x.ClientCode} · {x.ClientName}");
            var now=DateTime.UtcNow;
            Devices.Clear();foreach(var d in _server?.GetAdministrationDevices()??Array.Empty<ServerDeviceRecord>()){var p=_server!.GetAdministrationDevicePosture(d.DeviceId);var row=AdminDeviceRow.From(d,p);row.UpdateRing=_server.GetAdministrationDeviceUpdateRing(d.DeviceId);var live=presenceRows.Where(x=>x.DeviceId.Equals(d.DeviceId,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.LastSeenUtc).FirstOrDefault();row.PresenceStatus=!row.IsActive?"Disabled":live is null?"Offline":live.LastSeenUtc>=now.AddSeconds(-8)?"Online":live.LastSeenUtc>=now.AddMinutes(-2)?"Away":"Offline";row.CurrentClient=live is null||live.ClientId<=0?"—":clientNames.TryGetValue(live.ClientId,out var clientName)?clientName:$"Client {live.ClientId}";row.CurrentFinancialYear=string.IsNullOrWhiteSpace(live?.FinancialYear)?"—":live!.FinancialYear;row.CurrentModule=string.IsNullOrWhiteSpace(live?.Module)?"—":live!.Module;var assist=remoteAssistRows.FirstOrDefault(x=>x.DeviceId.Equals(d.DeviceId,StringComparison.OrdinalIgnoreCase)&&x.IsActive&&x.ExpiresUtc>now);if(assist is not null){row.RemoteAssistActive=true;row.RemoteAssistConsented=assist.UserConsented;row.RemoteAssistHelper=assist.HelperUserName;row.RemoteAssistExpiresUtc=assist.ExpiresUtc;}Devices.Add(row);}
            RemoteTargets.ReplaceWith(Devices.Where(x=>x.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)).OrderBy(x=>x.PresenceSort).ThenBy(x=>x.UserName,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.DeviceName,StringComparer.OrdinalIgnoreCase));
            Sessions.ReplaceWith(_server?.GetAdministrationSessions()??Array.Empty<NetworkSessionRecord>());
            BuildActiveUsers();
            RegistrationOtps.ReplaceWith(_server?.GetPendingMfaChallenges()??Array.Empty<SecureLanServerService.NetworkMfaChallengeInfo>());
            SecurityEvents.ReplaceWith((_server?.GetAdministrationSecurityEvents()??Array.Empty<ServerSecurityEventRecord>()).Take(500));
            RemoteCommands.ReplaceWith((_server?.GetAdministrationRemoteCommands()??Array.Empty<RemoteAahCommandRecord>()).Take(500));
            if(!string.IsNullOrWhiteSpace(selectedDeviceId))SelectedDevice=RemoteTargets.FirstOrDefault(x=>x.DeviceId.Equals(selectedDeviceId,StringComparison.OrdinalIgnoreCase))??Devices.FirstOrDefault(x=>x.DeviceId.Equals(selectedDeviceId,StringComparison.OrdinalIgnoreCase));
            else if(SelectedDevice is null&&RemoteTargets.Count==1)SelectedDevice=RemoteTargets[0];
            SelectRemoteAssistSessionForTarget();
            AuditRecords.ReplaceWith((_server?.GetAdministrationAudit(750)??Array.Empty<AdminAuditRecord>()));
            DeviceGroups.ReplaceWith(_server?.GetAdministrationDeviceGroups()??Array.Empty<string>());if(string.IsNullOrWhiteSpace(SelectedDeviceGroup)&&DeviceGroups.Count>0)SelectedDeviceGroup=DeviceGroups[0];
            var selectedRingName=SelectedDeploymentRing?.RingName;
            DeploymentRings.ReplaceWith(_server?.GetAdministrationDeploymentRings()??Array.Empty<DeploymentRingRecord>());
            SelectedDeploymentRing=DeploymentRings.FirstOrDefault(x=>x.RingName.Equals(selectedRingName??string.Empty,StringComparison.OrdinalIgnoreCase))??DeploymentRings.FirstOrDefault();
            BuildAccessMatrix();
            var r=_server?.GetAdministrationPolicyRevisions()??new SecurityPolicySnapshot();_liveAuthorizationRevision=r.AuthorizationRevision;_liveDevicePolicyRevision=r.DevicePolicyRevision;_liveClientAccessRevision=r.ClientAccessRevision;_liveSecurityPolicyRevision=r.SecurityPolicyRevision;PolicySummary=$"Authorization rev {r.AuthorizationRevision} · Device rev {r.DevicePolicyRevision} · Client access rev {r.ClientAccessRevision} · Security rev {r.SecurityPolicyRevision} · {r.UpdatedUtc.ToLocalTime():g}";
            var lockdown=_server?.GetAdministrationEmergencyLockdown()??new EmergencyLockdownRecord();EmergencyLockdown=lockdown.IsActive;EmergencyLockdownSummary=lockdown.IsActive?$"ACTIVE · {lockdown.Reason} · by {lockdown.ChangedBy} · {lockdown.ChangedUtc.ToLocalTime():g}":$"OFF · last change {lockdown.ChangedUtc.ToLocalTime():g}";
            DeploymentSummary=_server is null?"Main Server unavailable.":$"Main Server {_server.RuntimeVersion} · {_server.Endpoint}\nLAN binary target: %LOCALAPPDATA%\\Programs\\Accounting Automation Hub LAN\\Application\nUpdate rings: {string.Join(", ",DeploymentRings.Select(x=>$"{x.RingName}={(x.IsEnabled?"Active":"Paused")}"))}.";
            LoadRolePermissions();BuildTroubleshootingSummary();BuildSelectedDeviceAudit();RefreshSelectedUserLoginSummary();
            OnPropertyChanged(nameof(ServerSummary));OnPropertyChanged(nameof(SelectedDeviceTags));OnPropertyChanged(nameof(RemoteTargetSummary));OnPropertyChanged(nameof(RemoteAssistAccessSummary));OnPropertyChanged(nameof(RemoteAssistDeliverySummary));OnPropertyChanged(nameof(RemoteAssistTransportSummary));OnPropertyChanged(nameof(OnlineUserCount));OnPropertyChanged(nameof(AwayUserCount));OnPropertyChanged(nameof(OfflineUserCount));OnPropertyChanged(nameof(DisabledUserCount));
            Status=$"Loaded {Users.Count:N0} user(s), {OnlineUserCount:N0} online, {Devices.Count:N0} device(s), {Sessions.Count:N0} active session(s), {RegistrationOtps.Count:N0} pending Registration OTP(s), {ClientAccess.Count:N0} client/user assignment row(s).";RaiseCommands();
        }
        catch(Exception ex){Status="Administration refresh failed: "+ex.Message;}
    }

    private void RefreshLiveRuntime()
    {
        if(_server is null)return;
        var now=DateTime.UtcNow;
        var serverDevices=_server.GetAdministrationDevices();
        var knownIds=Devices.Select(x=>x.DeviceId).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
        var serverIds=serverDevices.Select(x=>x.DeviceId).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
        if(!knownIds.SequenceEqual(serverIds,StringComparer.OrdinalIgnoreCase)){Refresh();return;}
        var revisions=_server.GetAdministrationPolicyRevisions();
        var authorizationChanged=revisions.AuthorizationRevision!=_liveAuthorizationRevision;
        var devicePolicyChanged=revisions.DevicePolicyRevision!=_liveDevicePolicyRevision;
        var clientAccessChanged=revisions.ClientAccessRevision!=_liveClientAccessRevision;
        var securityPolicyChanged=revisions.SecurityPolicyRevision!=_liveSecurityPolicyRevision;
        if(devicePolicyChanged||securityPolicyChanged){Refresh();return;}
        if(authorizationChanged)
        {
            var selectedUserName=SelectedUser?.WindowsUserName;
            Users.ReplaceWith(_server.GetAdministrationUsers());
            if(!string.IsNullOrWhiteSpace(selectedUserName))SelectedUser=Users.FirstOrDefault(x=>x.WindowsUserName.Equals(selectedUserName,StringComparison.OrdinalIgnoreCase));
            LoadRolePermissions();
        }
        if(clientAccessChanged)
        {
            var accessKey=SelectedAccess is null?string.Empty:$"{SelectedAccess.UserName}|{SelectedAccess.ClientId}|{SelectedAccess.FinancialYear}";
            BuildAccessMatrix();
            if(accessKey.Length>0)SelectedAccess=ClientAccess.FirstOrDefault(x=>$"{x.UserName}|{x.ClientId}|{x.FinancialYear}".Equals(accessKey,StringComparison.OrdinalIgnoreCase));
        }
        _liveAuthorizationRevision=revisions.AuthorizationRevision;_liveDevicePolicyRevision=revisions.DevicePolicyRevision;_liveClientAccessRevision=revisions.ClientAccessRevision;_liveSecurityPolicyRevision=revisions.SecurityPolicyRevision;
        PolicySummary=$"Authorization rev {revisions.AuthorizationRevision} · Device rev {revisions.DevicePolicyRevision} · Client access rev {revisions.ClientAccessRevision} · Security rev {revisions.SecurityPolicyRevision} · {revisions.UpdatedUtc.ToLocalTime():g}";
        var presenceRows=_server.GetAdministrationDevicePresence();
        var clientNames=_clients.GetAll(includeArchived:true).ToDictionary(x=>x.Id,x=>$"{x.ClientCode} · {x.ClientName}");
        var remoteAssistRows=_server.GetAdministrationRemoteAssistSessions().Take(250).ToArray();
        RemoteAssistSessions.ReplaceWith(remoteAssistRows);
        Sessions.ReplaceWith(_server.GetAdministrationSessions());
        RegistrationOtps.ReplaceWith(_server.GetPendingMfaChallenges());
        RemoteCommands.ReplaceWith(_server.GetAdministrationRemoteCommands().Take(500));

        foreach(var row in Devices)
        {
            var live=presenceRows.Where(x=>x.DeviceId.Equals(row.DeviceId,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.LastSeenUtc).FirstOrDefault();
            row.PresenceStatus=!row.IsActive?"Disabled":live is null?"Offline":live.LastSeenUtc>=now.AddSeconds(-8)?"Online":live.LastSeenUtc>=now.AddMinutes(-2)?"Away":"Offline";
            row.CurrentClient=live is null||live.ClientId<=0?"—":clientNames.TryGetValue(live.ClientId,out var clientName)?clientName:$"Client {live.ClientId}";
            row.CurrentFinancialYear=string.IsNullOrWhiteSpace(live?.FinancialYear)?"—":live!.FinancialYear;
            row.CurrentModule=string.IsNullOrWhiteSpace(live?.Module)?"—":live!.Module;
            var assist=remoteAssistRows.FirstOrDefault(x=>x.DeviceId.Equals(row.DeviceId,StringComparison.OrdinalIgnoreCase)&&x.IsActive&&x.ExpiresUtc>now);
            row.RemoteAssistActive=assist is not null;row.RemoteAssistConsented=assist?.UserConsented==true;row.RemoteAssistHelper=assist?.HelperUserName??string.Empty;row.RemoteAssistExpiresUtc=assist?.ExpiresUtc;
        }
        if(now-_lastAdministrationAuditLiveRefreshUtc>=TimeSpan.FromSeconds(6))
        {
            _lastAdministrationAuditLiveRefreshUtc=now;
            SecurityEvents.ReplaceWith(_server.GetAdministrationSecurityEvents().Take(500));
            AuditRecords.ReplaceWith(_server.GetAdministrationAudit(750));
            BuildSelectedDeviceAudit();
        }
        BuildActiveUsers();
        SelectRemoteAssistSessionForTarget();
        RefreshRemoteAssistRuntimeState();
        OnPropertyChanged(nameof(OnlineUserCount));OnPropertyChanged(nameof(AwayUserCount));OnPropertyChanged(nameof(OfflineUserCount));OnPropertyChanged(nameof(DisabledUserCount));OnPropertyChanged(nameof(RemoteTargetSummary));OnPropertyChanged(nameof(RemoteAssistAccessSummary));OnPropertyChanged(nameof(RemoteAssistDeliverySummary));RaiseCommands();
    }

    private void BuildActiveUsers()
    {
        var now=DateTime.UtcNow;ActiveUsers.Clear();
        var presence=_server?.GetAdministrationDevicePresence()??Array.Empty<DevicePresenceRecord>();
        var clientNames=_clients.GetAll(includeArchived:true).ToDictionary(x=>x.Id,x=>$"{x.ClientCode} · {x.ClientName}");
        foreach(var user in Users.OrderBy(x=>x.DisplayName,StringComparer.OrdinalIgnoreCase))
        {
            var sessions=Sessions.Where(x=>x.UserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase)&&x.ExpiresUtc>now).OrderByDescending(x=>x.LastSeenUtc).ToArray();
            var latest=sessions.FirstOrDefault();var deviceIds=sessions.Select(x=>x.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();var names=Devices.Where(d=>deviceIds.Contains(d.DeviceId,StringComparer.OrdinalIgnoreCase)).Select(d=>d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var live=presence.Where(x=>x.UserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase)&&deviceIds.Contains(x.DeviceId,StringComparer.OrdinalIgnoreCase)).OrderByDescending(x=>x.LastSeenUtc).FirstOrDefault();
            var lastSeen=live?.LastSeenUtc??latest?.LastSeenUtc??user.LastSeenUtc;var status=!user.IsActive?"Disabled":sessions.Length==0?"Offline":lastSeen.HasValue&&lastSeen.Value>=now.AddSeconds(-8)?"Online":lastSeen.HasValue&&lastSeen.Value>=now.AddMinutes(-2)?"Away":"Offline";
            var versions=Devices.Where(d=>deviceIds.Contains(d.DeviceId,StringComparer.OrdinalIgnoreCase)).Select(d=>d.AppVersion).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
            var clientText=live is null||live.ClientId<=0?"—":clientNames.TryGetValue(live.ClientId,out var clientName)?clientName:$"Client {live.ClientId}";
            ActiveUsers.Add(new AdminActiveUserRow{UserName=user.WindowsUserName,DisplayName=user.DisplayName,RoleName=user.RoleName,IsAccountActive=user.IsActive,ActiveStatus=status,SessionCount=sessions.Length,DeviceNames=names.Length==0?"—":string.Join(", ",names),AppVersions=versions.Length==0?"—":string.Join(", ",versions),LastSeenUtc=lastSeen,SessionSinceUtc=sessions.Length==0?null:sessions.Min(x=>x.CreatedUtc),ExpiresUtc=latest?.ExpiresUtc,CurrentClient=clientText,CurrentFinancialYear=live?.FinancialYear??string.Empty,CurrentModule=live?.Module??string.Empty});
        }
    }

    private void BuildAccessMatrix(){_governance.CleanupExpiredUserClientAccess();ClientAccess.Clear();foreach(var client in _clients.GetAll(includeArchived:false))foreach(var user in Users.Where(x=>x.IsActive).OrderBy(x=>x.DisplayName,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.WindowsUserName,StringComparer.OrdinalIgnoreCase))ClientAccess.Add(new ClientAccessMatrixRow{ClientId=client.Id,ClientCode=client.ClientCode,ClientName=client.ClientName,FinancialYear=client.FinancialYear,UserName=user.WindowsUserName,DisplayName=user.DisplayName,RoleName=user.RoleName,LanAvailable=_governance.IsClientLanAvailable(client.Id),IsAllowed=_governance.HasExplicitClientAccess(user.WindowsUserName,client.Id,client.FinancialYear),AccessExpiryUtc=_governance.GetUserClientAccessExpiry(user.WindowsUserName,client.Id,client.FinancialYear)});}
    private void LoadRolePermissions(){try{RolePermissions.ReplaceWith(_server?.GetAdministrationRolePermissions(SelectedRole)??_governance.GetRolePermissions(SelectedRole));}catch{RolePermissions.Clear();}}
    private void CreateUser(){try{var dialog=new QuickLanUserWindow(_settings){Owner=System.Windows.Application.Current?.MainWindow};if(dialog.ShowDialog()==true){new EnterpriseControlRepository().BumpRevision("authorization");Status="AAH user created or updated.";Refresh();}}catch(Exception ex){Status="Could not create user: "+ex.Message;}}
    private void EditUser(){try{var selected=SelectedUser;if(selected is null)return;var dialog=new QuickLanUserWindow(_settings,selected){Owner=System.Windows.Application.Current?.MainWindow};if(dialog.ShowDialog()==true){new EnterpriseControlRepository().BumpRevision("authorization");Status=$"User '{selected.WindowsUserName}' updated.";Refresh();}}catch(Exception ex){Status="Could not edit user: "+ex.Message;}}
    private void ApplyUserRole(){try{if(SelectedUser is null||_server is null)return;_server.UpdateAdministrationUser(SelectedUser.WindowsUserName,SelectedUser.DisplayName,SelectedRole,SelectedUser.IsActive);Status=$"{SelectedUser.WindowsUserName} now uses role {SelectedRole}.";Refresh();}catch(Exception ex){Status="Could not update user access: "+ex.Message;}}
    private void SetUserActive(bool active){try{if(SelectedUser is null||_server is null)return;_server.SetAdministrationUserActive(SelectedUser.WindowsUserName,active);Status=active?$"{SelectedUser.WindowsUserName} enabled.":$"{SelectedUser.WindowsUserName} disabled and all active sessions revoked.";Refresh();}catch(Exception ex){Status="Could not change user status: "+ex.Message;}}
    private void RevokeUserSessions(){try{if(SelectedUser is null)return;_server!.RevokeAdministrationUserSessions(SelectedUser.WindowsUserName,"Administrator revoked all sessions");Status=$"All sessions revoked for {SelectedUser.WindowsUserName}. Device registration trust was preserved.";Refresh();}catch(Exception ex){Status="Could not revoke user sessions: "+ex.Message;}}
    private void UnlockUser(){try{if(SelectedUser is null)return;_server!.UnlockAdministrationUser(SelectedUser.WindowsUserName);Status=$"Login lockout cleared for {SelectedUser.WindowsUserName}.";RefreshSelectedUserLoginSummary();}catch(Exception ex){Status="Could not unlock user login: "+ex.Message;}}
    private void RefreshSelectedUserLoginSummary(){if(SelectedUser is null){SelectedUserLoginSummary="Select a user to inspect login/lockout status.";return;}var credential=_server?.GetAdministrationCredential(SelectedUser.WindowsUserName);if(credential is null){SelectedUserLoginSummary="No network password credential is registered for this identity.";return;}var locked=credential.LockoutUntilUtc.HasValue&&credential.LockoutUntilUtc.Value>DateTime.UtcNow;SelectedUserLoginSummary=$"Failed logins {credential.FailedAttempts} · {(locked?$"LOCKED until {credential.LockoutUntilUtc!.Value.ToLocalTime():g}":"Not locked")} · Credential updated {credential.UpdatedUtc.ToLocalTime():g}";}

    private void SaveRolePermissions(){try{if(_server is null)throw new InvalidOperationException("Main Server is unavailable.");if(!SelectedRole.Equals(GovernanceRoles.Custom,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Built-in roles are fixed. Use Custom for an explicit permission mix.");_server.SaveAdministrationRolePermissions(SelectedRole,RolePermissions);Status=$"Custom permission matrix saved.";Refresh();}catch(Exception ex){Status="Could not save role permissions: "+ex.Message;}}
    private async void GeneratePairing(){try{if(_server is null)throw new InvalidOperationException("Main Server is not running.");await _server.EnsureRunningAsync();var p=_server.GeneratePairingCode();PairingCode=$"Enrollment code {p.Code} · expires {p.ExpiresUtc.ToLocalTime():HH:mm:ss} · one use";Status="Enrollment code ready. Registration OTP is required once after enrollment.";}catch(Exception ex){Status="Could not create enrollment code: "+ex.Message;}}
    private void ResetTrust(){try{_server!.RequireDeviceRegistrationOtp(SelectedDevice!.DeviceId,"Administrator requested registration verification");Status="Registration trust reset. This PC will require one Registration OTP on its next sign-in.";Refresh();}catch(Exception ex){Status=ex.Message;}}
    private void RevokeDevice(){try{_server!.RevokeAdministrationDevice(SelectedDevice!.DeviceId);Status="Device revoked. Registration record retained for audit/history.";Refresh();}catch(Exception ex){Status=ex.Message;}}
    private void SetDeviceLifecycle(string state,bool active){try{_server!.SetAdministrationDeviceLifecycle(SelectedDevice!.DeviceId,state,active);Status=$"Device state changed to {state}.";Refresh();}catch(Exception ex){Status="Device lifecycle change failed: "+ex.Message;}}
    private void DeleteDevicePermanently()
    {
        try
        {
            var d=SelectedDevice;if(d is null)return;var owner=System.Windows.Application.Current?.MainWindow;var message=$"Permanently delete registration for {d.DeviceName} ({d.DeviceId})?\n\nThis removes device registration, sessions, client assignments, folder links, pending OTPs, posture/tags and queued remote access state. The security audit tombstone is retained. The device must enroll again to use AAH.";var answer=owner is null?MessageBox.Show(message,"Delete Device Registration",MessageBoxButton.YesNo,MessageBoxImage.Warning):MessageBox.Show(owner,message,"Delete Device Registration",MessageBoxButton.YesNo,MessageBoxImage.Warning);if(answer!=MessageBoxResult.Yes)return;
            _server!.DeleteAdministrationDeviceRegistration(d.DeviceId,"Administrator permanent delete");Status=$"Device registration permanently deleted: {d.DeviceName}.";SelectedDevice=null;Refresh();
        }
        catch(Exception ex){Status="Permanent device deletion failed: "+ex.Message;}
    }
    private void DeleteAllAndroidDevicesPermanently()
    {
        try
        {
            if (_server is null) return;
            var count = Devices.Count(x => x.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase));
            if (count == 0) { Status = "No Android registrations are present."; return; }
            var owner = System.Windows.Application.Current?.MainWindow;
            var message = $"Permanently delete all {count} Android registration(s)?\n\nUse this after clearing/reinstalling the mobile app or when duplicate phone registrations must be reset. Windows registrations are not touched. Every Android phone must scan the current Mobile Access QR again after this action.";
            var answer = owner is null
                ? MessageBox.Show(message, "Delete All Android Registrations", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(owner, message, "Delete All Android Registrations", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            var deleted = _server.DeleteAdministrationAndroidDeviceRegistrations("Administrator cleared all Android registrations before clean re-enrollment");
            SelectedDevice = null;
            Status = $"Deleted {deleted} Android registration(s). Windows devices were preserved. Register each phone once with the current Mobile Access QR.";
            Refresh();
        }
        catch (Exception ex) { Status = "Android registration reset failed: " + ex.Message; }
    }

    private void SetQuarantine(bool value){try{_server!.SetDeviceQuarantine(SelectedDevice!.DeviceId,value,value?"Administrator quarantine":"Administrator cleared quarantine");Status=value?"Device quarantined. Accounting access is blocked; remediation remains available.":"Device quarantine cleared.";Refresh();}catch(Exception ex){Status=ex.Message;}}
    private void SetDeviceTag(bool enabled){try{_server!.SetAdministrationDeviceTag(SelectedDevice!.DeviceId,DeviceTag,enabled);Status=$"Device tag {(enabled?"added":"removed")}: {DeviceTag.Trim()}.";DeviceTag=string.Empty;Refresh();}catch(Exception ex){Status="Device tag change failed: "+ex.Message;}}
    private void CreateDeviceGroup(){try{var repo=new EnterpriseControlRepository();repo.Initialize();repo.SaveDeviceGroup(NewDeviceGroup,"AAH Administration device group");Status=$"Device group created: {NewDeviceGroup.Trim()}.";NewDeviceGroup=string.Empty;Refresh();}catch(Exception ex){Status="Could not create device group: "+ex.Message;}}
    private void SetDeviceGroupMembership(bool enabled){try{var repo=new EnterpriseControlRepository();repo.Initialize();repo.SetDeviceGroupMember(SelectedDeviceGroup,SelectedDevice!.DeviceId,enabled);Status=$"{(enabled?"Added to":"Removed from")} device group {SelectedDeviceGroup}.";Refresh();}catch(Exception ex){Status="Device group change failed: "+ex.Message;}}

    private void SelectRemoteAssistSessionForTarget()
    {
        if(SelectedDevice is null){SelectedRemoteAssistSession=null;return;}
        SelectedRemoteAssistSession=RemoteAssistSessions.FirstOrDefault(x=>x.DeviceId.Equals(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase)&&x.IsActive)
            ??RemoteAssistSessions.FirstOrDefault(x=>x.DeviceId.Equals(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(RemoteAssistAccessSummary));
        OnPropertyChanged(nameof(RemoteAssistDeliverySummary));
    }

    private void SendRemoteNotification()
    {
        try
        {
            if(SelectedDevice is null||string.IsNullOrWhiteSpace(RemoteMessage))return;
            var args=JsonSerializer.Serialize(new{message=RemoteMessage.Trim(),urgent=RemoteNotificationUrgent,category="Administration"});
            var c=_server!.QueueRemoteAahCommand(SelectedDevice.DeviceId,RemoteAahActions.ShowMessage,args,TimeSpan.FromMinutes(15));
            Status=$"Visible AAH notification queued for {SelectedDevice.DeviceName} ({SelectedDevice.UserName}). Command {c.CommandId}.";
            RemoteMessage=string.Empty;RemoteNotificationUrgent=false;Refresh();
        }
        catch(Exception ex){Status="Could not send remote notification: "+ex.Message;}
    }

    private void QueueRemote(){try{var args=SelectedRemoteAction.Equals(RemoteAahActions.ShowMessage,StringComparison.OrdinalIgnoreCase)?JsonSerializer.Serialize(new{message=RemoteMessage}):"{}";var c=_server!.QueueRemoteAahCommand(SelectedDevice!.DeviceId,SelectedRemoteAction,args);Status=$"Remote AAH command {c.Action} queued for {SelectedDevice.DeviceName}.";RemoteMessage=string.Empty;Refresh();}catch(Exception ex){Status="Could not queue remote action: "+ex.Message;}}
    private void QueueGroupRemote(){try{if(string.IsNullOrWhiteSpace(SelectedDeviceGroup))return;var members=_server!.GetAdministrationDeviceGroupMembers(SelectedDeviceGroup);var queued=0;foreach(var deviceId in members){var device=Devices.FirstOrDefault(x=>x.DeviceId.Equals(deviceId,StringComparison.OrdinalIgnoreCase));if(device is null||!device.IsActive)continue;var args=SelectedRemoteAction.Equals(RemoteAahActions.ShowMessage,StringComparison.OrdinalIgnoreCase)?JsonSerializer.Serialize(new{message=RemoteMessage}):"{}";_server.QueueRemoteAahCommand(device.DeviceId,SelectedRemoteAction,args);queued++;}Status=$"Queued {SelectedRemoteAction} for {queued:N0} active device(s) in group {SelectedDeviceGroup}.";RemoteMessage=string.Empty;Refresh();}catch(Exception ex){Status="Could not queue group Remote AAH action: "+ex.Message;}}
    private void StartRemoteAssist(){try{if(SelectedDevice is null)return;if(!SelectedDevice.RemoteAssistEligible){Status=$"Remote Assist cannot be requested for {SelectedDevice.DeviceName}: {SelectedDevice.RemoteReadyStatus}";return;}var session=_server!.BeginAdministrationRemoteAssist(SelectedDevice.DeviceId,RemoteAssistMode,RemoteAssistDurationMinutes,RemoteAssistReason,RemoteAssistScope);Status=session.UserConsented?$"Remote Assist {session.SessionId} auto-connected to {SelectedDevice.DeviceName} under the Administrator Auto Accept policy.":$"Remote Assist request {session.SessionId} sent to {SelectedDevice.DeviceName} / {SelectedDevice.UserName}. Waiting for user consent. Mode: {session.PermissionMode}.";Refresh();SelectedRemoteAssistSession=RemoteAssistSessions.FirstOrDefault(x=>x.SessionId==session.SessionId);OnPropertyChanged(nameof(RemoteAssistAccessSummary));OnPropertyChanged(nameof(RemoteAssistDeliverySummary));if(session.UserConsented)OpenRemoteAssistViewer();}catch(Exception ex){Status="Could not request Remote Assist: "+ex.Message;}}
    private void RefreshRemoteAssistDevicePolicy()
    {
        try{RemoteAssistAutoAccept=SelectedDevice is not null&&_server is not null&&_server.GetAdministrationRemoteAssistDevicePolicy(SelectedDevice.DeviceId).AutoAcceptAdministratorAssist;}catch{RemoteAssistAutoAccept=false;}
        SaveRemoteAssistAutoAcceptCommand.RaiseCanExecuteChanged();
    }

    private void SaveRemoteAssistAutoAccept()
    {
        try{if(SelectedDevice is null||_server is null)return;_server.SetAdministrationRemoteAssistAutoAccept(SelectedDevice.DeviceId,RemoteAssistAutoAccept);Status=$"Remote Assist Auto Accept {(RemoteAssistAutoAccept?"enabled":"disabled")} for {SelectedDevice.DeviceName}. Only a Main Server Administrator can change this policy.";Refresh();}
        catch(Exception ex){Status="Could not save Remote Assist Auto Accept policy: "+ex.Message;RefreshRemoteAssistDevicePolicy();}
    }

    private void ApplyAdministratorShareState()
    {
        try{if(_server is null||SelectedRemoteAssistSession is null||!SelectedRemoteAssistSession.IsActive||!SelectedRemoteAssistSession.UserConsented)return;_server.SetAdministrationRemoteAssistShareState(SelectedRemoteAssistSession.SessionId,ShareAdministratorAah,AllowRemoteControlOfAdministrator);}
        catch(Exception ex){RemoteAssistStreamStatus="Could not update administrator sharing state: "+ex.Message;}
    }

    private void RefreshRemoteAssistRuntimeState(bool force=false)
    {
        if (_server is null || SelectedDevice is null) return;
        var now = DateTime.UtcNow;
        if (!force && now - _lastRemoteAssistRuntimeRefreshUtc < TimeSpan.FromSeconds(1)) return;
        _lastRemoteAssistRuntimeRefreshUtc = now;
        try
        {
            var deviceId = SelectedDevice.DeviceId;
            var wasConsented = SelectedRemoteAssistSession is not null && SelectedRemoteAssistSession.IsActive && SelectedRemoteAssistSession.UserConsented;
            var sessions = _server.GetAdministrationRemoteAssistSessions();
            var current = sessions.FirstOrDefault(x => x.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase) && x.IsActive && x.ExpiresUtc > now);
            var latest = current ?? sessions.FirstOrDefault(x => x.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            SelectedRemoteAssistSession = latest;
            SelectedDevice.RemoteAssistActive = current is not null;
            SelectedDevice.RemoteAssistConsented = current is not null && current.UserConsented;
            SelectedDevice.RemoteAssistHelper = current?.HelperUserName ?? string.Empty;
            SelectedDevice.RemoteAssistExpiresUtc = current?.ExpiresUtc;

            if (current is null)
            {
                RemoteAssistRemoteFrame=null;
                _remoteAssistRemoteFrameSequence=0;
                _remoteAssistAdministratorInputSequence=0;
                _lastAdministratorFrameHash=string.Empty;
                if (_shareAdministratorAah){_shareAdministratorAah=false;OnPropertyChanged(nameof(ShareAdministratorAah));}
                if (_allowRemoteControlOfAdministrator){_allowRemoteControlOfAdministrator=false;OnPropertyChanged(nameof(AllowRemoteControlOfAdministrator));}
                if (_remoteAssistViewerWindow is not null)
                {
                    var viewer=_remoteAssistViewerWindow;
                    _remoteAssistViewerWindow=null;
                    viewer.Close();
                }
                RemoteAssistStreamStatus="No active visual Remote Assist stream.";
            }

            OnPropertyChanged(nameof(RemoteAssistAccessSummary));
            OnPropertyChanged(nameof(RemoteAssistDeliverySummary));
            OnPropertyChanged(nameof(HasSelectedActiveRemoteAssist));
            OnPropertyChanged(nameof(RemoteTargetSummary));
            RaiseCommands();
            if (!wasConsented && current is not null && current.UserConsented)
                _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(OpenRemoteAssistViewer), DispatcherPriority.ContextIdle);
        }
        catch
        {
            // Streaming continues with the last known session state; the slow full reconciliation remains the fallback.
        }
    }

    private async Task RemoteAssistStreamTickAsync()
    {
        try
        {
            RefreshRemoteAssistRuntimeState();
            var session=SelectedRemoteAssistSession;if(_server is null||session is null||!session.IsActive||!session.UserConsented||session.ExpiresUtc<=DateTime.UtcNow){RemoteAssistStreamStatus="No consented visual Remote Assist session is active for the selected target.";return;}
            var state=_server.GetAdministrationRemoteAssistStreamState(session.SessionId);
            if(!state.RemoteSharing)RemoteAssistRemoteFrame=null;
            var remoteFrame=_server.GetAdministrationRemoteAssistFrame(session.SessionId,_remoteAssistRemoteFrameSequence);
            if(remoteFrame is not null){RemoteAssistRemoteFrame=ToBitmap(remoteFrame.JpegBytes);_remoteAssistRemoteFrameSequence=remoteFrame.Sequence;}
            if(ShareAdministratorAah)
            {
                var frame=await RemoteAssistWindowService.CaptureMainWindowAsync(session.SessionId,RemoteAssistStreamDirections.AdministratorToRemote);
                if(frame is not null)
                {
                    var hash=Convert.ToHexString(SHA256.HashData(frame.JpegBytes));
                    if(!hash.Equals(_lastAdministratorFrameHash,StringComparison.OrdinalIgnoreCase))
                    {
                        _server.PublishAdministrationRemoteAssistFrame(session.SessionId,frame.JpegBytes,frame.PixelWidth,frame.PixelHeight);
                        _lastAdministratorFrameHash=hash;
                    }
                }
            }
            if(ShareAdministratorAah&&AllowRemoteControlOfAdministrator)
            {
                var inputs=_server.DrainAdministrationRemoteAssistInputs(session.SessionId,_remoteAssistAdministratorInputSequence);
                foreach(var input in inputs.OrderBy(x=>x.Sequence)){await RemoteAssistWindowService.ApplyInputToMainWindowAsync(input);_remoteAssistAdministratorInputSequence=Math.Max(_remoteAssistAdministratorInputSequence,input.Sequence);}
            }
            RemoteAssistStreamStatus=$"REMOTE AAH: {(state.RemoteSharing?"Sharing":"Paused")} · Admin control {(state.AdministratorMayControlRemote?"Allowed":"View only")} · ADMIN AAH: {(state.AdministratorSharing?"Sharing":"Not shared")} · Remote control {(state.RemoteMayControlAdministrator?"Allowed":"View only")} · Remote frame #{state.RemoteFrameSequence} · Admin frame #{state.AdministratorFrameSequence} · {(string.IsNullOrWhiteSpace(_server.RemoteAssistInternetEndpoint)?"Private route":"Internet private route")}";
        }
        catch(Exception ex){RemoteAssistStreamStatus="Visual Remote Assist waiting/retrying: "+ex.Message;}
    }

    public void SendRemoteAssistInput(string kind,double x,double y,int delta=0,int key=0,string text="",bool control=false,bool shift=false,bool alt=false)
    {
        try{var session=SelectedRemoteAssistSession;if(_server is null||session is null||!session.IsActive||!session.UserConsented)return;_server.QueueAdministrationRemoteAssistInput(session.SessionId,new RemoteAssistInputRecord{Kind=kind,X=Math.Clamp(x,0,1),Y=Math.Clamp(y,0,1),Delta=delta,Key=key,Text=text,Control=control,Shift=shift,Alt=alt});}
        catch(Exception ex){RemoteAssistStreamStatus="Remote input was not sent: "+ex.Message;}
    }

    private static BitmapImage ToBitmap(byte[] bytes)
    {
        using var stream=new MemoryStream(bytes,false);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();return image;
    }

    private void OpenRemoteAssistViewer()
    {
        if(SelectedRemoteAssistSession is null||!SelectedRemoteAssistSession.IsActive||!SelectedRemoteAssistSession.UserConsented)return;
        if(_remoteAssistViewerWindow is not null){if(_remoteAssistViewerWindow.WindowState==WindowState.Minimized)_remoteAssistViewerWindow.WindowState=WindowState.Normal;_remoteAssistViewerWindow.Activate();return;}
        _remoteAssistViewerWindow=new RemoteAssistViewerWindow(this);
        if(System.Windows.Application.Current?.MainWindow is Window owner)_remoteAssistViewerWindow.Owner=owner;
        _remoteAssistViewerWindow.Closed+=(_,_)=>_remoteAssistViewerWindow=null;
        _remoteAssistViewerWindow.Show();
    }

    private void EndRemoteAssist(){try{if(SelectedRemoteAssistSession is null)return;var sid=SelectedRemoteAssistSession.SessionId;ShareAdministratorAah=false;AllowRemoteControlOfAdministrator=false;_server!.EndAdministrationRemoteAssist(sid);Status=$"Remote Assist {sid} ended. The Main Server state is closed and the remote AAH client is being cleared.";RefreshRemoteAssistRuntimeState(force:true);}catch(Exception ex){Status="Could not end Remote Assist: "+ex.Message;}}
    private void RemoteAssistNavigate(){try{if(SelectedRemoteAssistSession is null)return;var args=JsonSerializer.Serialize(new{sessionId=SelectedRemoteAssistSession.SessionId,page=RemoteAssistPage});_server!.QueueRemoteAahCommand(SelectedRemoteAssistSession.DeviceId,RemoteAahActions.RemoteAssistNavigate,args);Status=$"Remote Assist navigation queued: {RemoteAssistPage}.";Refresh();}catch(Exception ex){Status="Remote Assist navigation failed: "+ex.Message;}}
    private void RemoteAssistSetPreference(){try{if(SelectedRemoteAssistSession is null)return;var args=JsonSerializer.Serialize(new{sessionId=SelectedRemoteAssistSession.SessionId,key=RemotePreferenceKey,value=RemotePreferenceValue,useHelperPermission=RemotePerActionUseHelperPermission});_server!.QueueRemoteAahCommand(SelectedRemoteAssistSession.DeviceId,RemoteAahActions.RemoteAssistSetLocalPreference,args);Status=$"Remote Assist local preference change queued: {RemotePreferenceKey}.";Refresh();}catch(Exception ex){Status="Remote Assist preference change failed: "+ex.Message;}}

    private void SetClientAccess(bool allowed){try{var row=SelectedAccess!;if(allowed&&!row.LanAvailable)_governance.SetClientLanAvailable(row.ClientId,true);var expiry=allowed?ResolveAccessExpiry():null;_governance.SetUserClientAccess(row.UserName,row.ClientId,row.FinancialYear,allowed,expiry);new EnterpriseControlRepository().BumpRevision("client");Status=allowed?$"Granted {row.ClientCode} / FY {row.FinancialYear} to user {row.UserName} on every trusted device · {(expiry.HasValue?$"until {expiry.Value.ToLocalTime():g}":"permanent")}.":$"Revoked {row.ClientCode} / FY {row.FinancialYear} from user {row.UserName} on every trusted device.";Refresh();}catch(Exception ex){Status="Client user-access change failed: "+ex.Message;}}
    private DateTime? ResolveAccessExpiry()=>SelectedAccessDuration switch{"1 hour"=>DateTime.UtcNow.AddHours(1),"4 hours"=>DateTime.UtcNow.AddHours(4),"8 hours"=>DateTime.UtcNow.AddHours(8),"24 hours"=>DateTime.UtcNow.AddHours(24),"7 days"=>DateTime.UtcNow.AddDays(7),_=>null};
    private void SimulateAccess(){try{if(SelectedAccess is null||_server is null)return;var deviceId=Devices.Where(x=>x.IsActive&&x.UserName.Equals(SelectedAccess.UserName,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.LastSeenUtc??DateTime.MinValue).Select(x=>x.DeviceId).FirstOrDefault()??string.Empty;var decision=_server.SimulateAdministrationAccess(SelectedAccess.UserName,deviceId,SelectedAccess.ClientId,SelectedSimulationAction,SimulationRemoteRoute);AccessSimulationResult=$"{(decision.Allowed?"ALLOWED":"DENIED")} · Risk {decision.RiskScore}/100\n{decision.Explanation}";Status=$"Access Simulator: {(decision.Allowed?"allowed":"denied")} {SelectedSimulationAction} for user {SelectedAccess.UserName}/{SelectedAccess.ClientCode}.";}catch(Exception ex){AccessSimulationResult="ERROR · "+ex.Message;Status=AccessSimulationResult;}}
    private void RevokeSession(){try{_server!.RevokeAdministrationSession(SelectedSession!.SessionHash,"Administrator revoked active session");Status=$"Session revoked for {SelectedSession.UserName}.";Refresh();}catch(Exception ex){Status="Could not revoke session: "+ex.Message;}}
    private void RevokeActiveUserSessions(){try{if(SelectedActiveUser is null)return;_server!.RevokeAdministrationUserSessions(SelectedActiveUser.UserName,"Administrator revoked from Active Users");Status=$"All active sessions revoked for {SelectedActiveUser.UserName}.";Refresh();}catch(Exception ex){Status="Could not revoke active-user sessions: "+ex.Message;}}
    private void SetLockdown(bool active){try{if(string.IsNullOrWhiteSpace(LockdownReason)){Status="Enter a reason before changing Emergency Lockdown.";return;}_server!.SetAdministrationEmergencyLockdown(active,LockdownReason);Status=active?"Emergency Lockdown enabled.":"Emergency Lockdown disabled.";LockdownReason=string.Empty;Refresh();}catch(Exception ex){Status="Emergency Lockdown change failed: "+ex.Message;}}
    private void SaveDeploymentRing(){try{if(SelectedDeploymentRing is null)return;var ringName=SelectedDeploymentRing.RingName;_server!.SaveAdministrationDeploymentRing(ringName,DeploymentRingEnabled,DeploymentRingForceAfterDate);Status=$"Deployment ring {ringName} saved.";Refresh();SelectedDeploymentRing=DeploymentRings.FirstOrDefault(x=>x.RingName.Equals(ringName,StringComparison.OrdinalIgnoreCase))??DeploymentRings.FirstOrDefault();}catch(Exception ex){Status="Could not save deployment ring: "+ex.Message;}}
    private void AssignDeviceRing(){try{if(SelectedDevice is null)return;_server!.SetAdministrationDeviceUpdateRing(SelectedDevice.DeviceId,SelectedDeviceUpdateRing);Status=$"{SelectedDevice.DeviceName} assigned to {SelectedDeviceUpdateRing} update ring.";Refresh();}catch(Exception ex){Status="Could not assign update ring: "+ex.Message;}}
    private void RefreshSelectedDeviceUpdateRing(){SelectedDeviceUpdateRing=SelectedDevice?.UpdateRing??"Production";}

    private void PublishUpdate(bool force){try{var published=_server!.PublishWindowsUpdate(force);DeploymentSummary=$"Published {published.Version} · SHA-256 {published.Sha256}\n{(published.Forced?"Mandatory for all outdated LAN clients":"Available update; not forced")}";Status=force?"LAN update published as mandatory.":"LAN update published.";}catch(Exception ex){Status="Could not publish LAN update: "+ex.Message;}}
    private void RunSnapshotDiagnostic(){try{var result=_server!.RunSnapshotDiagnostic();SnapshotDiagnostic=result.ToLogText();Status=result.Success?"Snapshot diagnostic passed.":$"Snapshot diagnostic failed at {result.FailureStage}.";}catch(Exception ex){SnapshotDiagnostic="ERROR · "+ex.Message;Status=SnapshotDiagnostic;}}
    private async Task RunBackupAsync(){try{Status="Running verified Main Server replication/backup...";DeploymentSummary=await _server!.RunServerReplicationNowAsync();Status=DeploymentSummary;}catch(Exception ex){Status="Backup/replication failed: "+ex.Message;}}
    private void VerifyAudit(){try{if(_server is null)throw new InvalidOperationException("Main Server unavailable.");AuditSummary=_server.VerifyAdministrationAuditChain(out var detail)?"PASS · "+detail:"FAIL · "+detail;Status=AuditSummary;Refresh();}catch(Exception ex){AuditSummary="ERROR · "+ex.Message;Status=AuditSummary;}}
    private void QueueTroubleshooting(string action){try{if(SelectedDevice is null)return;var c=_server!.QueueRemoteAahCommand(SelectedDevice.DeviceId,action);Status=$"Troubleshooting action {c.Action} queued for {SelectedDevice.DeviceName}.";Refresh();}catch(Exception ex){Status="Troubleshooting action failed: "+ex.Message;}}
    private void BuildSelectedDeviceAudit(){if(SelectedDevice is null){SelectedDeviceAudit.Clear();return;}SelectedDeviceAudit.ReplaceWith(AuditRecords.Where(x=>x.DeviceId.Equals(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase)||x.Resource.Equals(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase)||x.Detail.Contains(SelectedDevice.DeviceId,StringComparison.OrdinalIgnoreCase)).Take(250));}
    private void BuildTroubleshootingSummary(){if(SelectedDevice is null){TroubleshootingSummary="Select a device to inspect its registration, posture, sessions, assignments and recent events.";return;}var d=SelectedDevice;var sessions=Sessions.Count(x=>x.DeviceId.Equals(d.DeviceId,StringComparison.OrdinalIgnoreCase));var assignments=ClientAccess.Count(x=>x.UserName.Equals(d.UserName,StringComparison.OrdinalIgnoreCase)&&x.IsAllowed);var events=SecurityEvents.Where(x=>x.DeviceId.Equals(d.DeviceId,StringComparison.OrdinalIgnoreCase)).Take(5).Select(x=>$"{x.CreatedUtc.ToLocalTime():g} {x.EventType}: {x.Detail}");TroubleshootingSummary=$"{d.DeviceName} · {d.DeviceId}\nState {d.EnrollmentState} · Active {d.IsActive} · Health {d.HealthState} · Risk {d.RiskScore}\nRegistration {(d.RegistrationTrusted?"Trusted":"Verification Required")} · Sessions {sessions} · Authorised clients for user {assignments}\nLast seen {d.LastSeenUtc?.ToLocalTime():g}\nRecent events:\n{string.Join("\n",events)}";}
    private bool CanQueueRemoteAction(){return Can(GovernancePermissions.RemoteAah);}
    private bool Can(string p){var u=_governance.GetCurrentUser();return u.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase)||_governance.HasPermission(Environment.UserName,p);}
    private void RaiseCommands(){foreach(var command in new[]{CreateUserCommand,EditUserCommand,ApplyUserRoleCommand,EnableUserCommand,DisableUserCommand,RevokeUserSessionsCommand,UnlockUserCommand,SaveRolePermissionsCommand,GeneratePairingCommand,ResetTrustCommand,RevokeDeviceCommand,DisableDeviceCommand,EnableDeviceCommand,RetireDeviceCommand,MarkLostDeviceCommand,DeleteDeviceCommand,DeleteAllAndroidDevicesCommand,QuarantineCommand,ClearQuarantineCommand,AddDeviceTagCommand,RemoveDeviceTagCommand,CreateDeviceGroupCommand,AddToDeviceGroupCommand,RemoveFromDeviceGroupCommand,QueueRemoteCommand,SendRemoteNotificationCommand,ForceOpenAahCommand,QueueGroupRemoteCommand,StartRemoteAssistCommand,EndRemoteAssistCommand,RemoteAssistNavigateCommand,RemoteAssistPreferenceCommand,GrantAccessCommand,RevokeAccessCommand,SimulateAccessCommand,RevokeSessionCommand,RevokeActiveUserSessionsCommand,EnableLockdownCommand,DisableLockdownCommand,PublishUpdateCommand,ForceUpdateCommand,SaveDeploymentRingCommand,AssignDeviceRingCommand,RunSnapshotDiagnosticCommand,VerifyAuditCommand,QueueDiagnosticsCommand,QueueRefreshCommand,SaveRemoteAssistAutoAcceptCommand,OpenRemoteAssistViewerCommand})command.RaiseCanExecuteChanged();RunBackupCommand.RaiseCanExecuteChanged();}
}

public sealed class AdminDeviceRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string property)=>PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(property));
    public string DeviceId{get;set;}=string.Empty;public string DeviceName{get;set;}=string.Empty;public string DeviceType{get;set;}=string.Empty;public string UserName{get;set;}=string.Empty;public string RoleName{get;set;}=string.Empty;public string AppVersion{get;set;}=string.Empty;public string UpdateRing{get;set;}="Production";public string EnrollmentState{get;set;}=string.Empty;public bool IsActive{get;set;}public DateTime? LastSeenUtc{get;set;}public DateTime? RegistrationOtpVerifiedUtc{get;set;}public string HealthState{get;set;}=string.Empty;public int RiskScore{get;set;}public bool RegistrationTrusted{get;set;}public bool IsQuarantined{get;set;}public string QuarantineReason{get;set;}=string.Empty;private string _presenceStatus="Offline";private string _currentClient="—";private string _currentFinancialYear="—";private string _currentModule="—";public string PresenceStatus{get=>_presenceStatus;set{value??="Offline";if(_presenceStatus==value)return;_presenceStatus=value;Raise(nameof(PresenceStatus));Raise(nameof(RemoteReadyStatus));Raise(nameof(PresenceSort));}}public string CurrentClient{get=>_currentClient;set{value??="—";if(_currentClient==value)return;_currentClient=value;Raise(nameof(CurrentClient));}}public string CurrentFinancialYear{get=>_currentFinancialYear;set{value??="—";if(_currentFinancialYear==value)return;_currentFinancialYear=value;Raise(nameof(CurrentFinancialYear));}}public string CurrentModule{get=>_currentModule;set{value??="—";if(_currentModule==value)return;_currentModule=value;Raise(nameof(CurrentModule));}}private bool _remoteAssistActive;private bool _remoteAssistConsented;private string _remoteAssistHelper=string.Empty;private DateTime? _remoteAssistExpiresUtc;public bool RemoteAssistActive{get=>_remoteAssistActive;set{if(_remoteAssistActive==value)return;_remoteAssistActive=value;Raise(nameof(RemoteAssistActive));Raise(nameof(RemoteReadyStatus));}}public bool RemoteAssistConsented{get=>_remoteAssistConsented;set{if(_remoteAssistConsented==value)return;_remoteAssistConsented=value;Raise(nameof(RemoteAssistConsented));Raise(nameof(RemoteReadyStatus));}}public string RemoteAssistHelper{get=>_remoteAssistHelper;set{value??=string.Empty;if(_remoteAssistHelper==value)return;_remoteAssistHelper=value;Raise(nameof(RemoteAssistHelper));Raise(nameof(RemoteReadyStatus));}}public DateTime? RemoteAssistExpiresUtc{get=>_remoteAssistExpiresUtc;set{if(_remoteAssistExpiresUtc==value)return;_remoteAssistExpiresUtc=value;Raise(nameof(RemoteAssistExpiresUtc));Raise(nameof(RemoteReadyStatus));}}
    public bool RemoteAssistEligible=>DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&IsActive&&!IsQuarantined&&EnrollmentState.Equals(ServerEnrollmentStates.Active,StringComparison.OrdinalIgnoreCase);
    public string RemoteReadyStatus=>RemoteAssistActive?(RemoteAssistConsented?$"ACTIVE · {RemoteAssistHelper} connected · expires {RemoteAssistExpiresUtc?.ToLocalTime():HH:mm}":$"REQUEST PENDING · waiting for {UserName} consent"):!DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)?"Not a Windows LAN target":!IsActive?$"Device disabled ({EnrollmentState})":IsQuarantined?$"Quarantined · {QuarantineReason}":!EnrollmentState.Equals(ServerEnrollmentStates.Active,StringComparison.OrdinalIgnoreCase)?$"Enrollment state {EnrollmentState}":PresenceStatus.Equals("Offline",StringComparison.OrdinalIgnoreCase)?"Ready to queue request when AAH reconnects":"Ready for Remote Assist request";
    public int PresenceSort=>PresenceStatus switch{"Online"=>0,"Away"=>1,"Offline"=>2,_=>3};
    public static AdminDeviceRow From(ServerDeviceRecord d,DevicePostureRecord p)=>new(){DeviceId=d.DeviceId,DeviceName=d.DeviceName,DeviceType=d.DeviceType,UserName=d.UserName,RoleName=d.RoleName,AppVersion=d.AppVersion,EnrollmentState=d.EnrollmentState,IsActive=d.IsActive,LastSeenUtc=d.LastSeenUtc,RegistrationOtpVerifiedUtc=d.RegistrationOtpVerifiedUtc,HealthState=p.IsQuarantined?DeviceHealthStates.Quarantined:p.HealthState,RiskScore=p.RiskScore,RegistrationTrusted=!d.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)||d.RegistrationOtpVerifiedUtc.HasValue,IsQuarantined=p.IsQuarantined,QuarantineReason=p.QuarantineReason};
}
public sealed class AdminActiveUserRow{public string UserName{get;set;}=string.Empty;public string DisplayName{get;set;}=string.Empty;public string RoleName{get;set;}=string.Empty;public bool IsAccountActive{get;set;}public string ActiveStatus{get;set;}="Offline";public int SessionCount{get;set;}public string DeviceNames{get;set;}=string.Empty;public string AppVersions{get;set;}=string.Empty;public DateTime? LastSeenUtc{get;set;}public DateTime? SessionSinceUtc{get;set;}public DateTime? ExpiresUtc{get;set;}public string CurrentClient{get;set;}="—";public string CurrentFinancialYear{get;set;}=string.Empty;public string CurrentModule{get;set;}=string.Empty;}
public sealed class ClientAccessMatrixRow{public long ClientId{get;set;}public string ClientCode{get;set;}=string.Empty;public string ClientName{get;set;}=string.Empty;public string FinancialYear{get;set;}=string.Empty;public string UserName{get;set;}=string.Empty;public string DisplayName{get;set;}=string.Empty;public string RoleName{get;set;}=string.Empty;public bool LanAvailable{get;set;}public bool IsAllowed{get;set;}public DateTime? AccessExpiryUtc{get;set;}public string AccessExpiryText=>!IsAllowed?"—":AccessExpiryUtc.HasValue?AccessExpiryUtc.Value.ToLocalTime().ToString("g"):"Permanent";}
internal static class ObservableCollectionReplaceExtensions{public static void ReplaceWith<T>(this ObservableCollection<T> collection,IEnumerable<T> values){collection.Clear();foreach(var value in values)collection.Add(value);}}
