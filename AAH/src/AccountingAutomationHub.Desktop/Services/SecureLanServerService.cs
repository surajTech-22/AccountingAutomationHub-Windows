using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Native-client server for the main AAH computer. No browser accounting interface is exposed.
/// SQLite, Tally, OCR and Python remain private to the main computer. Remote devices use this
/// HTTPS API only after one-use enrollment, one Registration OTP when Windows-device trust requires verification, Administrator-created LAN credentials and device lease validation. Routine registered-device login does not use OTP.
/// </summary>
public sealed class SecureLanServerService : IDisposable
{
    private readonly ApplicationPreferenceService _settings;
    private readonly SqliteGovernanceRepository _governance = new();
    private readonly ServerSecurityRepository _serverSecurity = new();
    private readonly SqliteClientRepository _clients = new();
    private readonly object _gate = new();
    private readonly object _repositoryInitializationGate = new();
    private bool _repositoriesInitialized;
    private readonly ConcurrentDictionary<string, RequestWindow> _requestWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LoginChallenge> _loginChallenges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AndroidLoginQrWindow> _androidLoginQrs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AndroidUserQrWindow> _androidUserQrs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _remoteCommandSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly NetworkReadOnlySnapshotService _snapshotService = new();
    private readonly EnterpriseControlRepository _enterpriseControl = new();
    private readonly CollaborationRepository _collaboration = new();
    private readonly RemoteAssistRelayService _remoteAssistRelay = new();
    private readonly EnterprisePolicyService _enterprisePolicy;
    private WebApplication? _application;
    // FIXED35: Main Server recovery is background-owned. UI refresh timers must never repeatedly
    // rebuild repositories/certificates/Kestrel after a failed bind. A failed automatic start
    // gets one bounded delayed retry away from the WPF dispatcher.
    private Timer? _automaticStartRetryTimer;
    private int _automaticStartRetryScheduled;
    private const int AutomaticStartRetrySeconds = 5;
    private const int ApplicationExitStopBudgetMilliseconds = 750;
    // Fix137 R46 R14: the QR pin must come from the exact certificate object handed to Kestrel,
    // never from a potentially stale exported .cer file while the server is live.
    private string _activeServerCertificateQrPin = string.Empty;
    private PairingWindow? _pairing;
    private Timer? _leaseTimer;
    private Timer? _androidQrRotationTimer;
    private Timer? _replicationTimer;
    private Timer? _dataReplicationDebounceTimer;
    private volatile bool _disposed;
    private NetworkIdentityService? _identity;
    private ServerStorageService? _storage;
    private ServerReadModelService? _readModel;
    private readonly NetworkSecretProtector _secretProtector;
    private readonly MobileAccountingActionService _mobileAccounting = new();
    private DistributedImportJobService _distributedJobs = null!;
    private readonly ImportRegistrySyncService _registrySync = new();
    private readonly MappingSessionBrokerService _mappingBroker = new();
    private readonly TallyLinkFoundationService _tallyLinks = new();
    private readonly ClientAutoImportPolicyService _autoImportPolicies = new();
    // Fix137 R46 R26: Client/FY-specific phone-to-Windows invoice bridge. Invoice recognition payloads
    // stay in memory only and are handed directly into the normal Sales/Purchase repositories.
    // No per-invoice JSON evidence file is persisted.
    private readonly SqliteMasterRepository _invoiceMasterRepository = new();
    private readonly SqliteStockMasterRepository _invoiceStockRepository = new();
    private readonly object _mobileLinkGate = new();
    private readonly ConcurrentDictionary<string, MobileInvoiceQueueItem> _mobileInvoiceQueue = new(StringComparer.OrdinalIgnoreCase);
    // R79: one recognition attempt per authenticated Client/FY scan ID; retry never reuploads or reposts.
    private readonly ConcurrentDictionary<string, byte> _mobileInvoiceOcrInFlight = new(StringComparer.OrdinalIgnoreCase);
    // R97: optional Stage 1 backfill for a previously received Phone Link scan.
    // This does NOT rerun ONNX, alter its mapped payload, or create accounting entries.
    private readonly ConcurrentDictionary<string, byte> _mobileInvoiceChildOnlyInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SqlitePurchaseRecordRepository _mobilePurchaseRepository = new();
    private readonly SqliteSalesRecordRepository _mobileSalesRepository = new();
    // R46 R29: reuse the production Bank PaddleOCR-VL raw-recognition engine for invoice
    // document/table layout. Bank-statement verification is never invoked for invoices.
    private readonly MobileInvoiceDocumentOcrService _mobileInvoiceDocumentOcr = new();
    private readonly CancellationTokenSource _mobileInvoiceOcrCancellation = new();
    public event EventHandler? MobileLinkChanged;
    public event EventHandler? MobilePresenceChanged;


    public SecureLanServerService(ApplicationPreferenceService settings)
    {
        _settings = settings;
        // Database/schema work is intentionally deferred until Main Server startup (or until
        // Settings/Administration is opened) so construction cannot block first-window paint.
        _enterprisePolicy = new EnterprisePolicyService(_governance, _enterpriseControl);
        _identity = new NetworkIdentityService(_settings, _governance, _serverSecurity);
        _storage = new ServerStorageService(_settings, _serverSecurity);
        _distributedJobs = new DistributedImportJobService(_storage.RootFolder);
        _secretProtector = new NetworkSecretProtector(_settings);
        _readModel = new ServerReadModelService();
    }

    public string StatusText { get; private set; } = "AAH Main Server is stopped.";
    public string Endpoint { get; private set; } = string.Empty;
    public string RemoteAssistInternetEndpoint { get; private set; } = string.Empty;
    public bool IsRunning => _application is not null;
    public string PublicCertificatePath => Path.Combine(ApplicationDataPaths.RootFolder, "Security", "AAH-LAN-Root-CA.cer");
    public string PublicServerCertificatePath => Path.Combine(ApplicationDataPaths.RootFolder, "Security", "AAH-LAN-Server.cer");
    public string ServerCertificateQrPin => GetServerCertificateQrPin();
    public string ServerRootFolder => _storage?.RootFolder ?? Path.Combine(ApplicationDataPaths.RootFolder, "ServerAuthority");
    public string RuntimeVersion => typeof(SecureLanServerService).Assembly.GetName().Version?.ToString(4) ?? "4.0.0.410";
    public string RuntimeExecutablePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;
    public DateTime RuntimeProcessStartedLocal => Process.GetCurrentProcess().StartTime;
    public NetworkSnapshotDiagnosticInfo? LastSnapshotDiagnostic => _snapshotService.LastDiagnostic;
    public event EventHandler<NetworkMfaChallengeEventArgs>? MfaChallengeCreated;
    public event EventHandler<AndroidEnrollmentApprovalEventArgs>? AndroidEnrollmentApprovalRequested;
    public event Action<DistributedImportJobRecord>? DistributedImportJobQueued;
    public event Action<string>? DistributedImportJobCancelled;
    public event Action<string>? AndroidUserQrRotated;
    public event Action? RunningStateChanged;
    public IReadOnlyList<NetworkMfaChallengeInfo> GetPendingMfaChallenges()
    {
        var persisted = _serverSecurity.GetPendingLoginChallenges();
        foreach (var item in persisted)
            _loginChallenges.TryAdd(item.ChallengeId, new LoginChallenge(item.ChallengeId, item.UserName, item.DeviceId, item.DeviceName, item.NetworkScope, item.Code, item.CreatedUtc, item.ExpiresUtc, false));
        return persisted
            .Select(x => new NetworkMfaChallengeInfo(x.ChallengeId, x.UserName, x.DeviceName, x.NetworkScope, x.Code, x.CreatedUtc, x.ExpiresUtc))
            .ToList();
    }

    public IReadOnlyList<ApplicationUserRecord> GetAdministrationUsers() => _governance.GetUsers();
    public IReadOnlyList<ServerDeviceRecord> GetAdministrationDevices() => _serverSecurity.GetServerDevices();
    public IReadOnlyList<NetworkSessionRecord> GetAdministrationSessions() => _serverSecurity.GetSessions();
    public IReadOnlyList<ServerSecurityEventRecord> GetAdministrationSecurityEvents() => _serverSecurity.GetRecentSecurityEvents();
    public IReadOnlyList<RemoteAahCommandRecord> GetAdministrationRemoteCommands() => _enterpriseControl.GetRecentRemoteCommands();
    public SecurityPolicySnapshot GetAdministrationPolicyRevisions() => _enterpriseControl.GetRevisions();
    public DevicePostureRecord GetAdministrationDevicePosture(string deviceId) => _enterpriseControl.GetPosture(deviceId);
    public bool VerifyAdministrationAuditChain(out string detail) => _enterpriseControl.VerifyAuditChain(out detail);
    public IReadOnlyList<RolePermissionRecord> GetAdministrationRolePermissions(string roleName) => _governance.GetRolePermissions(roleName);
    public IReadOnlyList<DeviceTagRecord> GetAdministrationDeviceTags(string deviceId) => _enterpriseControl.GetDeviceTags(deviceId);
    public EmergencyLockdownRecord GetAdministrationEmergencyLockdown() => _enterpriseControl.GetEmergencyLockdown();
    public IReadOnlyList<RemoteAssistSessionRecord> GetAdministrationRemoteAssistSessions() => _enterpriseControl.GetRemoteAssistSessions();
    public IReadOnlyList<AdminAuditRecord> GetAdministrationAudit(int limit=500) => _enterpriseControl.GetAdminAudit(limit);
    public IReadOnlyList<string> GetAdministrationDeviceGroups() => _enterpriseControl.GetDeviceGroups();
    public IReadOnlyList<DevicePresenceRecord> GetAdministrationDevicePresence() => _enterpriseControl.GetDevicePresence();
    public NetworkCredentialRecord? GetAdministrationCredential(string userName) => _serverSecurity.GetCredential(userName);
    public IReadOnlyList<string> GetAdministrationDeviceGroupMembers(string groupName) => _enterpriseControl.GetDeviceGroupMembers(groupName);
    public IReadOnlyList<DeploymentRingRecord> GetAdministrationDeploymentRings() => _enterpriseControl.GetDeploymentRings();
    public string GetAdministrationDeviceUpdateRing(string deviceId) => _enterpriseControl.GetDeviceUpdateRing(deviceId);
    public RemoteAssistDevicePolicyRecord GetAdministrationRemoteAssistDevicePolicy(string deviceId) => _enterpriseControl.GetRemoteAssistDevicePolicy(deviceId);
    public RemoteAssistStreamStateRecord GetAdministrationRemoteAssistStreamState(string sessionId) => _remoteAssistRelay.GetState(sessionId);
    public RemoteAssistFrameRecord? GetAdministrationRemoteAssistFrame(string sessionId,long afterSequence=0) => _remoteAssistRelay.GetFrame(sessionId,RemoteAssistStreamDirections.RemoteToAdministrator,afterSequence);

    public void SetAdministrationRemoteAssistAutoAccept(string deviceId,bool enabled)
    {
        DemandPrimaryAdministrator();
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Auto Accept is available only for enrolled Windows LAN devices.");
        _enterpriseControl.SaveRemoteAssistDevicePolicy(new RemoteAssistDevicePolicyRecord{DeviceId=device.DeviceId,AutoAcceptAdministratorAssist=enabled,UpdatedBy=Environment.UserName});
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","RemoteAssistAutoAcceptChanged",device.DeviceId,$"AutoAccept={enabled}","REMOTE_ASSIST_AUTO_ACCEPT",true);
    }

    public void SetAdministrationRemoteAssistShareState(string sessionId,bool shareAdministratorAah,bool allowRemoteControl)
    {
        DemandPermission(GovernancePermissions.RemoteAssist);
        if(allowRemoteControl)DemandPrimaryAdministrator();
        var session=RequireAdministrationRemoteAssistSession(sessionId);
        if(_remoteAssistRelay.SetAdministratorState(session.SessionId,shareAdministratorAah,allowRemoteControl))
            _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","RemoteAssistAdministratorShareChanged",session.SessionId,$"ShareAdministratorAah={shareAdministratorAah}; RemoteMayControlAdministrator={shareAdministratorAah&&allowRemoteControl}","REMOTE_ASSIST_TWO_WAY_SHARE",true);
    }

    public RemoteAssistFrameRecord PublishAdministrationRemoteAssistFrame(string sessionId,byte[] jpegBytes,int pixelWidth,int pixelHeight)
    {
        DemandPermission(GovernancePermissions.RemoteAssist);
        var session=RequireAdministrationRemoteAssistSession(sessionId);
        return _remoteAssistRelay.PublishFrame(session.SessionId,RemoteAssistStreamDirections.AdministratorToRemote,jpegBytes,pixelWidth,pixelHeight);
    }

    public RemoteAssistInputRecord QueueAdministrationRemoteAssistInput(string sessionId,RemoteAssistInputRecord input)
    {
        DemandPermission(GovernancePermissions.RemoteAssist);
        var session=RequireAdministrationRemoteAssistSession(sessionId);
        var state=_remoteAssistRelay.GetState(session.SessionId);
        if(!session.UserConsented||!state.RemoteSharing||!state.AdministratorMayControlRemote||state.RemoteFrameSequence<=0)throw new InvalidOperationException("The remote AAH window is not yet live/shared for administrator control.");
        input.Direction=RemoteAssistStreamDirections.AdministratorToRemote;
        return _remoteAssistRelay.QueueInput(session.SessionId,input);
    }

    public IReadOnlyList<RemoteAssistInputRecord> DrainAdministrationRemoteAssistInputs(string sessionId,long afterSequence)
    {
        DemandPermission(GovernancePermissions.RemoteAssist);
        var session=RequireAdministrationRemoteAssistSession(sessionId);
        var state=_remoteAssistRelay.GetState(session.SessionId);
        if(!state.AdministratorSharing||!state.RemoteMayControlAdministrator)return Array.Empty<RemoteAssistInputRecord>();
        return _remoteAssistRelay.DrainInputs(session.SessionId,RemoteAssistStreamDirections.RemoteToAdministrator,afterSequence);
    }


    public void SaveAdministrationDeploymentRing(string ringName,bool enabled,DateTime? forceAfterUtc)
    {
        DemandPermission(GovernancePermissions.ManageLanUpdates);
        var ring=new DeploymentRingRecord{RingName=ringName,IsEnabled=enabled,ForceAfterUtc=forceAfterUtc?.ToUniversalTime(),UpdatedBy=Environment.UserName};
        _enterpriseControl.SaveDeploymentRing(ring);
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","DeploymentRingChanged",ringName,$"Enabled={enabled}; ForceAfter={forceAfterUtc:O}","UPDATE_RING",true);
    }

    public void SetAdministrationDeviceUpdateRing(string deviceId,string ringName)
    {
        DemandPermission(GovernancePermissions.ManageLanUpdates);
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        _enterpriseControl.SetDeviceUpdateRing(device.DeviceId,ringName);
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","DeviceUpdateRingChanged",device.DeviceId,ringName,"UPDATE_RING_ASSIGN",true);
    }

    public void UpdateAdministrationUser(string userName,string displayName,string roleName,bool isActive)
    {
        DemandPermission(GovernancePermissions.ManageUsers);
        var existing=GetApplicationUser(userName)??throw new InvalidOperationException("The selected user no longer exists.");
        existing.DisplayName=string.IsNullOrWhiteSpace(displayName)?existing.WindowsUserName:displayName.Trim();
        existing.RoleName=roleName.Trim();existing.IsActive=isActive;existing.UpdatedUtc=DateTime.UtcNow;_governance.SaveUser(existing);
        if(!isActive)_serverSecurity.DeleteSessionsForUser(existing.WindowsUserName);
        _enterpriseControl.BumpRevision("authorization");
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","UserAccessChanged",existing.WindowsUserName,$"Role={existing.RoleName}; Active={existing.IsActive}","USER_ACCESS",true);
    }

    public void SetAdministrationUserActive(string userName,bool active)
    {
        DemandPermission(GovernancePermissions.ManageUsers);
        var existing=GetApplicationUser(userName)??throw new InvalidOperationException("The selected user no longer exists.");
        if(existing.WindowsUserName.Equals(Environment.UserName,StringComparison.OrdinalIgnoreCase)&&!active)throw new InvalidOperationException("You cannot disable your own currently signed-in Main Server administrator identity.");
        existing.IsActive=active;existing.UpdatedUtc=DateTime.UtcNow;_governance.SaveUser(existing);
        if(!active)_serverSecurity.DeleteSessionsForUser(existing.WindowsUserName);
        _enterpriseControl.BumpRevision("authorization");
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER",active?"UserEnabled":"UserDisabled",existing.WindowsUserName,$"Active={active}","USER_STATUS",true);
    }

    public void UnlockAdministrationUser(string userName)
    {
        DemandPermission(GovernancePermissions.ManageUsers);
        var existing=GetApplicationUser(userName)??throw new InvalidOperationException("The selected user no longer exists.");
        _serverSecurity.ClearAuthenticationFailures(existing.WindowsUserName);
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","UserLoginUnlocked",existing.WindowsUserName,"Failed login counter and lockout were cleared; password/device trust were not changed.","USER_UNLOCK",true);
    }

    public void RevokeAdministrationUserSessions(string userName,string reason)
    {
        DemandPermission(GovernancePermissions.ManageUsers);
        var existing=GetApplicationUser(userName)??throw new InvalidOperationException("The selected user no longer exists.");
        _serverSecurity.DeleteSessionsForUser(existing.WindowsUserName);_enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","UserSessionsRevoked",existing.WindowsUserName,reason,"USER_SESSION_REVOKE",true);
    }

    public void SaveAdministrationRolePermissions(string roleName,IEnumerable<RolePermissionRecord> permissions)
    {
        DemandPermission(GovernancePermissions.ManageRoles);
        _governance.SetRolePermissions(roleName,permissions);
        _enterpriseControl.BumpRevision("authorization");
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","RolePermissionsChanged",roleName,"Role permission matrix updated.","RBAC_CHANGE",true);
    }

    public void SetAdministrationDeviceTag(string deviceId,string tag,bool enabled)
    {
        DemandPermission(GovernancePermissions.ManageDevices);
        _enterpriseControl.SetDeviceTag(deviceId,tag,enabled);
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER",enabled?"DeviceTagAdded":"DeviceTagRemoved",deviceId,tag,"DEVICE_TAG",true);
    }

    public void SetAdministrationEmergencyLockdown(bool active,string reason)
    {
        DemandPermission(GovernancePermissions.ManageServer);
        if(string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required for Emergency Lockdown changes.");
        _enterpriseControl.SetEmergencyLockdown(active,reason,Environment.UserName);
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER",active?"EmergencyLockdownEnabled":"EmergencyLockdownDisabled","MainServer",reason,"FOUR_EYES_SENSITIVE",true);
    }

    public void RevokeAdministrationSession(string sessionHash,string reason)
    {
        DemandPermission(GovernancePermissions.ManageUsers);
        if(string.IsNullOrWhiteSpace(sessionHash)) throw new InvalidOperationException("Select an active session.");
        _serverSecurity.DeleteSession(sessionHash);
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","SessionRevoked",sessionHash,reason,"SESSION_REVOKE",true);
    }

    public AccessDecisionRecord SimulateAdministrationAccess(string userName,string deviceId,long clientId,string action,bool remoteRoute)
    {
        DemandPermission(GovernancePermissions.ViewAudit);
        var user=GetApplicationUser(userName)??throw new InvalidOperationException("The selected user no longer exists.");
        var device=string.IsNullOrWhiteSpace(deviceId)
            ? _serverSecurity.GetServerDevices().Where(x=>x.IsActive&&x.UserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.LastSeenUtc??x.CreatedUtc).FirstOrDefault()
            : _serverSecurity.FindDeviceById(deviceId);
        var client=clientId>0?_clients.GetById(clientId):null;
        var fy=client?.FinancialYear??string.Empty;
        if(device is null)return new AccessDecisionRecord{Allowed=false,UserName=user.WindowsUserName,DeviceId=string.Empty,ClientId=clientId,FinancialYear=fy,Action=action,RiskScore=100,Explanation="User access is assigned, but no active trusted device is currently enrolled for this user."};
        var clientAllowed=clientId<=0||NetworkHasClientAccess(user,device,clientId,fy);
        return _enterprisePolicy.Evaluate(user,device,clientId,fy,action,clientAllowed,remoteRoute);
    }

    public RemoteAahCommandRecord QueueRemoteAahCommand(string deviceId,string action,string argumentsJson="{}",TimeSpan? lifetime=null)
    {
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        if(!device.IsActive)throw new InvalidOperationException("The selected device is not active.");
        if(!RemoteAahActions.Allowed.Contains(action))throw new InvalidOperationException("That Remote AAH action is not in the server allow-list.");
        var requiredPermission = action.Equals(RemoteAahActions.RequestRemoteAssist,StringComparison.OrdinalIgnoreCase) ||
                                 action.Equals(RemoteAahActions.RemoteAssistNavigate,StringComparison.OrdinalIgnoreCase) ||
                                 action.Equals(RemoteAahActions.RemoteAssistSetLocalPreference,StringComparison.OrdinalIgnoreCase) ||
                                 action.Equals(RemoteAahActions.RemoteAssistEnd,StringComparison.OrdinalIgnoreCase)
            ? GovernancePermissions.RemoteAssist
            : GovernancePermissions.RemoteAah;
        DemandPermission(requiredPermission);
        var record=_enterpriseControl.QueueRemoteCommand(new RemoteAahCommandRecord{DeviceId=device.DeviceId,Action=action,ArgumentsJson=argumentsJson,RequestedBy=Environment.UserName,RequiredPermission=requiredPermission,ExpiresUtc=DateTime.UtcNow.Add(lifetime??TimeSpan.FromMinutes(10))});
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","RemoteCommandQueued",device.DeviceId,$"{action}; CommandId={record.CommandId}","REMOTE_AAH",true);
        SignalRemoteCommand(device.DeviceId);
        return record;
    }

    public void RequireDeviceRegistrationOtp(string deviceId,string reason="Administrator reset")
    {
        DemandPermission(GovernancePermissions.ResetDeviceTrust);
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        _serverSecurity.RequireRegistrationOtp(device.Id);
        _serverSecurity.DeleteSessionsForUser(device.UserName);
        _enterpriseControl.BumpRevision("device");
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","RegistrationTrustReset",device.DeviceId,reason,"DEVICE_TRUST_RESET",true);
    }

    public void RevokeAdministrationDevice(string deviceId,string reason="Administrator revoked device")
    {
        DemandPermission(GovernancePermissions.ManageDevices);
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        _serverSecurity.RevokeDevice(device.Id);
        _serverSecurity.DeleteSessionsForUser(device.UserName);
        _enterpriseControl.BumpRevision("device");
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","DeviceRevoked",device.DeviceId,reason,"DEVICE_REVOKE",true);
    }

    public void SetAdministrationDeviceLifecycle(string deviceId,string state,bool active)
    {
        DemandPermission(GovernancePermissions.ManageDevices);
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        var allowed=new[]{ServerEnrollmentStates.Active,ServerEnrollmentStates.Disabled,ServerEnrollmentStates.Retired,ServerEnrollmentStates.Lost,ServerEnrollmentStates.Revoked};
        if(!allowed.Contains(state,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Unsupported device lifecycle state.");
        _serverSecurity.SetDeviceLifecycleState(device.Id,state,active);_enterpriseControl.BumpRevision("device");
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","DeviceLifecycleChanged",device.DeviceId,$"State={state}; Active={active}","DEVICE_LIFECYCLE",true);
    }

    public void DeleteAdministrationDeviceRegistration(string deviceId,string reason)
    {
        DemandPermission(GovernancePermissions.ManageDevices);
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        var auditDetail=$"DeviceName={device.DeviceName}; User={device.UserName}; Type={device.DeviceType}; LastSeen={device.LastSeenUtc:O}; Reason={reason}";
        _serverSecurity.DeleteDeviceRegistration(device.Id);_enterpriseControl.DeleteDeviceState(device.DeviceId);_enterpriseControl.BumpRevision("client");
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","DeviceRegistrationPermanentlyDeleted",device.DeviceId,auditDetail,"DEVICE_DELETE_TOMBSTONE",true);
    }

    public int DeleteAdministrationAndroidDeviceRegistrations(string reason)
    {
        DemandPermission(GovernancePermissions.ManageDevices);
        var devices = _serverSecurity.GetServerDevices()
            .Where(x => x.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var device in devices)
        {
            var auditDetail = $"DeviceName={device.DeviceName}; User={device.UserName}; Type={device.DeviceType}; LastSeen={device.LastSeenUtc:O}; Reason={reason}";
            _serverSecurity.DeleteDeviceRegistration(device.Id);
            _enterpriseControl.DeleteDeviceState(device.DeviceId);
            _enterpriseControl.AppendAdminAudit("Administrator", Environment.UserName, "SERVER", "AndroidDeviceRegistrationPermanentlyDeleted", device.DeviceId, auditDetail, "ANDROID_DEVICE_RESET", true);
        }
        if (devices.Length > 0) _enterpriseControl.BumpRevision("client");
        return devices.Length;
    }

    public RemoteAssistSessionRecord BeginAdministrationRemoteAssist(string deviceId,string permissionMode,int durationMinutes,string reason,string scope="AAH Only")
    {
        DemandPermission(GovernancePermissions.RemoteAssist);
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        if(!device.IsActive)throw new InvalidOperationException("The selected device is not active.");
        if(!RemoteAssistPrivilegeModes.All.Contains(permissionMode,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Choose a valid Remote Assist permission mode.");
        durationMinutes=Math.Clamp(durationMinutes,5,60);
        if(!permissionMode.Equals(RemoteAssistPrivilegeModes.RemoteUser,StringComparison.OrdinalIgnoreCase)&&string.IsNullOrWhiteSpace(reason))throw new InvalidOperationException("A support reason is required before using helper permissions.");
        var existing=_enterpriseControl.GetRemoteAssistSessions().FirstOrDefault(x=>x.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&x.IsActive&&x.ExpiresUtc>DateTime.UtcNow);
        if(existing is not null)throw new InvalidOperationException(existing.UserConsented?"Remote Assist is already active for this device. End the current session before starting another.":"A Remote Assist request is already waiting for this device. End or decline the current request before starting another.");
        var helper=GetApplicationUser(Environment.UserName)??throw new InvalidOperationException("The helper identity is not registered in AAH governance.");
        var permissions=helper.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase)?GovernancePermissions.All.ToArray():_governance.GetRolePermissions(helper.RoleName).Where(x=>x.IsAllowed).Select(x=>x.PermissionName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Bind Remote Assist to the user who is actually live on this enrolled PC, not merely
        // the historical registration owner. Presence is refreshed by the authenticated heartbeat;
        // an active network session is the fallback if presence has not arrived yet.
        var now=DateTime.UtcNow;
        var livePresence=_enterpriseControl.GetDevicePresence()
            .FirstOrDefault(x=>x.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&x.LastSeenUtc>now.AddMinutes(-2));
        var liveSession=_serverSecurity.GetSessions()
            .Where(x=>x.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&x.ExpiresUtc>now)
            .OrderByDescending(x=>x.LastSeenUtc)
            .ThenByDescending(x=>x.CreatedUtc)
            .FirstOrDefault();
        var remoteUserName=livePresence?.UserName??liveSession?.UserName??device.UserName;
        var remote=GetApplicationUser(remoteUserName);
        if(remote is null||!remote.IsActive)throw new InvalidOperationException("The selected PC does not have an active AAH user who can receive Remote Assist.");
        var policy=_enterpriseControl.GetRemoteAssistDevicePolicy(device.DeviceId);
        var autoAccept=helper.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase)&&policy.AutoAcceptAdministratorAssist;
        var session=_enterpriseControl.CreateRemoteAssistSession(new RemoteAssistSessionRecord{DeviceId=device.DeviceId,RemoteUserName=remote.WindowsUserName,HelperUserName=helper.WindowsUserName,PermissionMode=permissionMode,GrantedPermissionsJson=JsonSerializer.Serialize(permissions),Scope=scope,Reason=reason?.Trim()??string.Empty,UserConsented=autoAccept,ExpiresUtc=DateTime.UtcNow.AddMinutes(durationMinutes)});
        _remoteAssistRelay.SetRemoteState(session.SessionId,true,true);
        var args=JsonSerializer.Serialize(new{sessionId=session.SessionId,permissionMode=session.PermissionMode,helperUser=session.HelperUserName,remoteUser=session.RemoteUserName,helperPermissions=permissions,scope=session.Scope,reason=session.Reason,expiresUtc=session.ExpiresUtc,autoAccept});
        _enterpriseControl.QueueRemoteCommand(new RemoteAahCommandRecord{DeviceId=device.DeviceId,Action=RemoteAahActions.RequestRemoteAssist,ArgumentsJson=args,RequestedBy=helper.WindowsUserName,RequiredPermission=GovernancePermissions.RemoteAssist,ExpiresUtc=DateTime.UtcNow.AddMinutes(10)});
        SignalRemoteCommand(device.DeviceId);
        _enterpriseControl.AppendAdminAudit("Administrator",helper.WindowsUserName,"SERVER","RemoteAssistRequested",device.DeviceId,$"Session={session.SessionId}; Mode={session.PermissionMode}; Scope={scope}; Duration={durationMinutes}m; AutoAccept={autoAccept}; Reason={session.Reason}","REMOTE_ASSIST",true);
        return session;
    }

    public void EndAdministrationRemoteAssist(string sessionId,string reason="Administrator ended Remote Assist")
    {
        DemandPermission(GovernancePermissions.RemoteAssist);
        var session=_enterpriseControl.GetRemoteAssistSessions().FirstOrDefault(x=>x.SessionId.Equals(sessionId,StringComparison.OrdinalIgnoreCase));
        if(session is not null)
        {
            var args=JsonSerializer.Serialize(new{sessionId=session.SessionId,reason});
            _enterpriseControl.QueueRemoteCommand(new RemoteAahCommandRecord{DeviceId=session.DeviceId,Action=RemoteAahActions.RemoteAssistEnd,ArgumentsJson=args,RequestedBy=Environment.UserName,RequiredPermission=GovernancePermissions.RemoteAssist,ExpiresUtc=DateTime.UtcNow.AddMinutes(5)});
            SignalRemoteCommand(session.DeviceId);
        }
        _enterpriseControl.EndRemoteAssistSession(sessionId);
        _remoteAssistRelay.RemoveSession(sessionId);
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER","RemoteAssistEnded",sessionId,reason,"REMOTE_ASSIST_END",true);
    }

    public void SetDeviceQuarantine(string deviceId,bool quarantined,string reason)
    {
        DemandPermission(GovernancePermissions.ManageDevices);
        var device=_serverSecurity.FindDeviceById(deviceId)??throw new InvalidOperationException("The selected device no longer exists.");
        var posture=_enterpriseControl.GetPosture(device.DeviceId);posture.IsQuarantined=quarantined;posture.QuarantineReason=quarantined?reason.Trim():string.Empty;posture.HealthState=quarantined?DeviceHealthStates.Quarantined:DeviceHealthStates.Healthy;posture.EvaluatedUtc=DateTime.UtcNow;_enterpriseControl.SavePosture(posture);_enterpriseControl.BumpRevision("device");
        _enterpriseControl.AppendAdminAudit("Administrator",Environment.UserName,"SERVER",quarantined?"DeviceQuarantined":"DeviceQuarantineCleared",device.DeviceId,reason,"DEVICE_POSTURE",true);
    }

    public string GetLatestMfaRequestStatus()
    {
        var latest = _serverSecurity.GetLatestSecurityEvent("NetworkMfaChallenge");
        if (latest is null) return "No network OTP request has reached this Main Server yet.";
        var state = latest.Success ? "accepted" : "rejected";
        var user = string.IsNullOrWhiteSpace(latest.UserName) ? "unknown user" : latest.UserName;
        return $"Last OTP request {state}: {user} · {latest.Detail} · {latest.CreatedUtc.ToLocalTime():HH:mm:ss}";
    }

    public NetworkSnapshotDiagnosticInfo RunSnapshotDiagnostic()
    {
        var started = DateTime.UtcNow;
        try
        {
            var device = _serverSecurity.GetServerDevices()
                .Where(x => x.IsActive)
                .OrderByDescending(x => x.LastSeenUtc ?? x.UpdatedUtc)
                .FirstOrDefault();

            if (device is null)
            {
                var zero = _snapshotService.Diagnose(Array.Empty<long>());
                SafeLogEvent("NetworkSnapshotDiagnostic", Environment.UserName, string.Empty, "Manual diagnostic without enrolled device. " + zero.ToLogText(), zero.Success);
                return zero;
            }

            var user = GetApplicationUser(device.UserName);
            if (user is null)
                throw new InvalidOperationException($"The most recently active enrolled device '{device.DeviceName}' refers to User ID '{device.UserName}', but that User ID does not exist in AppUsers.");

            var allowed = _readModel!.GetClientsForUser(user.WindowsUserName, (u, c, fy) => NetworkHasClientAccess(user, device, c, fy))
                .Select(x => x.Id)
                .Distinct()
                .ToArray();
            var result = _snapshotService.Diagnose(allowed);
            SafeLogEvent("NetworkSnapshotDiagnostic", user.WindowsUserName, device.DeviceId, $"Manual diagnostic for {device.DeviceName}. " + result.ToLogText(), result.Success);
            return result;
        }
        catch (Exception exception)
        {
            var failed = new NetworkSnapshotDiagnosticInfo
            {
                StartedUtc = started, CompletedUtc = DateTime.UtcNow, AllowedClientCount = 0,
                Branch = "Pre-snapshot access calculation", LastStage = "Resolve enrolled device/user/client access", FailureStage = "Resolve enrolled device/user/client access", Success = false,
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name, ExceptionMessage = exception.Message,
                InnerExceptionMessage = exception.InnerException?.Message ?? string.Empty,
                DatabaseExists = File.Exists(ApplicationDataPaths.DatabasePath),
                DatabaseSize = File.Exists(ApplicationDataPaths.DatabasePath) ? new FileInfo(ApplicationDataPaths.DatabasePath).Length : 0
            };
            failed.Stages.Add($"{DateTime.Now:HH:mm:ss.fff} · FAILED before snapshot build · {failed.ExceptionType}: {failed.ExceptionMessage}");
            SafeLogEvent("NetworkSnapshotDiagnostic", Environment.UserName, string.Empty, failed.ToLogText(), false);
            return failed;
        }
    }

    public MobileLinkClientState GetMobileLinkClientState(long clientId, string financialYear)
    {
        var state = GetMobileLinkPresenceState(clientId, financialYear);
        state.PendingCount = GetMobileInvoiceQueue(clientId, financialYear).Count(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));
        return state;
    }

    public MobileLinkClientState GetMobileLinkPresenceState(long clientId, string financialYear)
    {
        EnsureRepositoriesInitialized();
        var config = LoadMobileLinkConfiguration(clientId, financialYear);
        var now = DateTime.UtcNow;

        // R46 R22: the authenticated Android heartbeat is the Mobile Link identity authority.
        // Do not require a second, manually typed User ID link. A presence row is eligible only
        // when its active Android device and authenticated AAH user still have access to this
        // exact Client/FY. This keeps the existing authorization barrier while removing stale
        // manual link state as a cause of false OFFLINE status.
        var androidDevices = new Dictionary<string, ServerDeviceRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in _serverSecurity.GetServerDevices().Where(x => x.IsActive && x.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase)))
            androidDevices[candidate.DeviceId] = candidate;

        var liveAuthorized = new List<DevicePresenceRecord>();
        foreach (var presence in _enterpriseControl.GetDevicePresence().Where(x => x.LastSeenUtc >= now.AddSeconds(-12)).OrderByDescending(x => x.LastSeenUtc))
        {
            if (!androidDevices.TryGetValue(presence.DeviceId, out var device)) continue;
            var user = GetApplicationUser(presence.UserName);
            if (user is null || !user.IsActive) continue;
            if (!NetworkHasClientAccess(user, device, clientId, financialYear)) continue;
            liveAuthorized.Add(presence);
        }

        var same = liveAuthorized.FirstOrDefault(x => x.ClientId == clientId && x.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase));
        var other = same is null ? liveAuthorized.FirstOrDefault() : null;
        var liveIdentity = same?.UserName ?? other?.UserName ?? string.Empty;
        return new MobileLinkClientState
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            LinkedUserId = liveIdentity,
            ShowMappingHere = config.ShowMappingHere,
            SameClientOnline = same is not null,
            MobileOnline = liveAuthorized.Count > 0,
            PresenceText = same is not null ? $"Online · {same.UserName}" : other is not null ? $"Online · {other.UserName} · different Client/FY ({other.ClientId} · {other.FinancialYear})" : "Offline / Waiting",
            CurrentMobileModule = same?.Module ?? other?.Module ?? string.Empty,
            LastSeenUtc = same?.LastSeenUtc ?? other?.LastSeenUtc
        };
    }

    public void SaveMobileLinkRoutingConfiguration(long clientId, string financialYear, bool showMappingHere)
    {
        EnsureRepositoriesInitialized();
        var client = _clients.GetById(clientId) ?? throw new InvalidOperationException("The selected Client/FY no longer exists.");
        if (!client.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mobile Link routing can only be changed for the currently selected Client/FY.");
        var config = new MobileLinkConfiguration { ClientId = clientId, FinancialYear = financialYear, LinkedUserId = string.Empty, ShowMappingHere = showMappingHere, UpdatedUtc = DateTime.UtcNow };
        lock (_mobileLinkGate) WriteMobileJson(MobileLinkConfigurationPath(clientId, financialYear), config);
        MobileLinkChanged?.Invoke(this, EventArgs.Empty);
    }

    // Compatibility signature retained for older callers/packages. R46 R22 intentionally
    // ignores linkedUserId because authenticated heartbeat + Client/FY authorization is now
    // the sole mobile identity authority.
    public void SaveMobileLinkConfiguration(long clientId, string financialYear, string linkedUserId, bool showMappingHere)
        => SaveMobileLinkRoutingConfiguration(clientId, financialYear, showMappingHere);

    public IReadOnlyList<string> GetInvoiceLedgerNames(long clientId, string financialYear)
    {
        EnsureRepositoriesInitialized();
        return _invoiceMasterRepository.Get(clientId, financialYear, false, string.Empty)
            .Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !x.IsArchived)
            .Select(x => x.Name.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> GetInvoiceStockItemNames(long clientId, string financialYear)
    {
        EnsureRepositoriesInitialized();
        return _invoiceStockRepository.GetStockItems(clientId, financialYear, false, string.Empty)
            .Where(x => !x.IsArchived).Select(x => x.Name.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> GetInvoiceUnitNames(long clientId, string financialYear)
    {
        EnsureRepositoriesInitialized();
        return _invoiceStockRepository.GetUnits(clientId, financialYear, false, string.Empty)
            .Where(x => !x.IsArchived).Select(x => x.Name.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string GetMobileInvoiceLearning(long clientId, string financialYear)
    {
        var path = Path.Combine(MobileLinkRoot(clientId, financialYear), "learning.json");
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "{}"; }
        catch { return "{}"; }
    }

    public void SaveMobileInvoiceLearning(long clientId, string financialYear, string relationshipsJson)
    {
        if (string.IsNullOrWhiteSpace(relationshipsJson)) return;
        var incoming = JsonNode.Parse(relationshipsJson)?.AsObject() ?? throw new InvalidOperationException("Mobile invoice learning must be a JSON object.");
        var path = Path.Combine(MobileLinkRoot(clientId, financialYear), "learning.json");
        lock (_mobileLinkGate)
        {
            JsonObject merged;
            try { merged = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsObject() ?? new JsonObject() : new JsonObject(); }
            catch { merged = new JsonObject(); }
            foreach (var property in incoming)
            {
                if (property.Value is not JsonArray incomingLabels) continue;
                var labels = new List<string>();
                if (merged[property.Key] is JsonArray existingLabels)
                    labels.AddRange(existingLabels.Select(x => x?.ToString().Trim() ?? string.Empty));
                labels.AddRange(incomingLabels.Select(x => x?.ToString().Trim() ?? string.Empty));
                var unique = labels.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
                merged[property.Key] = new JsonArray(unique.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        MobileLinkChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MobileInvoiceQueueItem> GetMobileInvoiceQueue(long clientId, string financialYear) =>
        _mobileInvoiceQueue.Values
            .Where(x => x.ClientId == clientId && x.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ReceivedUtc).ThenBy(x => x.ScanId, StringComparer.OrdinalIgnoreCase).ToArray();

    public MobileInvoiceQueueItem? GetMobileInvoiceQueueItem(long clientId, string financialYear, string scanId) =>
        _mobileInvoiceQueue.TryGetValue(MobileQueueKey(clientId, financialYear, scanId), out var row) ? row : null;

    // R79: Retry an EXISTING queued scan only after the local Paddle runtime is ready.
    // No new invoice record or accounting import is created by this method.
    public string RetryMobileInvoiceDocumentOcr(long clientId, string financialYear, string scanId)
    {
        var key = MobileQueueKey(clientId, financialYear, scanId);
        if (!_mobileInvoiceQueue.TryGetValue(key, out var row) ||
            !row.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase))
            return "The selected scan is not pending in this Client/FY queue.";
        if (row.DocumentOcrState.Equals("Ready", StringComparison.OrdinalIgnoreCase))
            return "The selected scan already has verified Windows document OCR.";
        if (_mobileInvoiceOcrInFlight.ContainsKey(key))
            return "Windows document OCR is already processing this scan.";
        if (row.ImagePaths.Count == 0 || row.ImagePaths.Any(path => !File.Exists(path)))
            return "The original phone invoice image is missing. No OCR retry was started.";
        var client = _clients.GetById(clientId);
        if (client is null || client.IsArchived ||
            !client.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            return "The original Client/FY is no longer available for this scan.";
        var issue = InvoiceOnnxDocumentOcrRecognitionService.GetRuntimeReadinessIssue();
        if (issue.Length > 0) return "Cannot retry OCR: " + issue;
        _ = RunMobileInvoiceDocumentOcrAsync(row, client);
        return "OCR retry submitted for the existing scan. No new upload or accounting entry was created.";
    }

    // R97: Backfill/retry Child Stage 1 on the selected EXISTING scan, independently of
    // DocumentOcrState=Ready. Never re-upload, duplicate or alter ONNX accounting data.
    public string RetryMobileInvoiceChildStage1(long clientId, string financialYear, string scanId)
    {
        var key = MobileQueueKey(clientId, financialYear, scanId);
        if (!_mobileInvoiceQueue.TryGetValue(key, out var row) ||
            !row.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase))
            return "The selected scan is no longer in this Client/FY review queue.";
        var client = _clients.GetById(clientId);
        if (client is null || client.IsArchived ||
            !client.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            return "The selected Client/FY is not available.";
        if (row.ImagePaths.Count == 0 || row.ImagePaths.Any(p => !File.Exists(p)))
            return "The original Phone Link photo is missing; Child Stage 1 was not started.";
        if (_mobileInvoiceOcrInFlight.ContainsKey(key))
            return "The original OCR pipeline is already processing this scan; wait for it to finish.";
        if (!_mobileInvoiceChildOnlyInFlight.TryAdd(key, 0))
            return "Child Stage 1 is already processing this scan.";
        row.ChildStage1Status = "Queued existing Phone Link photo for Child Stage 1; accounting OCR untouched.";
        row.ChildStage1Archive = string.Empty;
        MobileLinkChanged?.Invoke(this, EventArgs.Empty);
        _ = RunExistingChildStage1Async(row, key);
        return "Existing photo queued for Invoice OCR Child Stage 1. No upload or accounting OCR retry.";
    }

    private async Task RunExistingChildStage1Async(MobileInvoiceQueueItem row, string key)
    {
        try {
            var result = await InvoiceOcrChildBridge.TryStage1Async(row.ImagePaths,
                row.ClientId, row.FinancialYear, row.ScanId, row.EvidenceFolder,
                _mobileInvoiceOcrCancellation.Token).ConfigureAwait(false);
            if (_mobileInvoiceQueue.TryGetValue(key, out var current) && ReferenceEquals(current, row) &&
                row.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)) {
                row.ChildStage1Status = result.Message;
                row.ChildStage1Archive = result.Stage1Succeeded ? result.Stage1Archive : string.Empty;
                MobileLinkChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) {
            row.ChildStage1Status = "Child Stage 1 stopped with AAH; accounting data unchanged.";
            MobileLinkChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) {
            row.ChildStage1Status = "Child Stage 1 failed: " + ex.Message + "; AAH OCR data unchanged.";
            MobileLinkChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _mobileInvoiceChildOnlyInFlight.TryRemove(key, out _); }
    }

    public void CompleteMobileInvoiceReview(long clientId, string financialYear, string scanId, bool confirmed, MobileInvoiceMappedResult? mappedResult)
    {
        var key = MobileQueueKey(clientId, financialYear, scanId);
        if (!_mobileInvoiceQueue.TryGetValue(key, out var row))
            throw new InvalidOperationException("The selected mobile scan no longer exists in this Client/FY queue.");

        if (confirmed)
        {
            if (mappedResult is null) throw new InvalidOperationException("Confirmed mobile invoice mapping is missing.");
            // R90 server-side, not only WPF, source-photo / Client-FY / accounting gates.
            if (!row.DocumentOcrState.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Windows document OCR has not completed; no Phone Link import is permitted.");
            using (var sourceDocument = System.Text.Json.JsonDocument.Parse(row.PayloadJson))
            {
                var sourceRoot = sourceDocument.RootElement;
                if (sourceRoot.TryGetProperty("ocrEngine", out var engine) &&
                    engine.GetString() == "AAH-RAPIDOCR-ONNX-R90")
                {
                    if (!sourceRoot.TryGetProperty("ocrR89Preview", out var review) ||
                        review.GetProperty("schema").GetString() != "AAH-R89-ACCOUNTING-BLOCK-PREVIEW-V1" ||
                        review.GetProperty("import_allowed").GetBoolean())
                        throw new InvalidOperationException("Original R89 spatial accounting verification is missing.");
                    var originalSha=sourceRoot.GetProperty("ocrSourceSha256").GetString() ?? "";
                    if (row.ImagePaths.Count != 1 || !File.Exists(row.ImagePaths[0]) ||
                        !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(row.ImagePaths[0])))
                            .Equals(originalSha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Source invoice photograph changed or disappeared after ONNX recognition.");
                    if (mappedResult.SourceImageSha256 != originalSha)
                        throw new InvalidOperationException("The confirmed invoice did not preserve its source-photo SHA-256.");
                    var flags=sourceRoot.GetProperty("ocrBlockingFlags");
                    if (flags.ValueKind == System.Text.Json.JsonValueKind.Array && flags.GetArrayLength() > 0 &&
                        !mappedResult.SourceDiscrepanciesReviewed)
                        throw new InvalidOperationException("Review and acknowledge the original source discrepancies before confirming.");
                    if (mappedResult.Lines.Count == 0 ||
                        mappedResult.Lines.Sum(line => line.Taxable) != mappedResult.TaxableTotal)
                        throw new InvalidOperationException("Original goods lines and taxable summary do not exactly reconcile; no posting is permitted.");
                    if (mappedResult.InvoiceTotal != mappedResult.TaxableTotal + mappedResult.CgstTotal +
                        mappedResult.SgstTotal + mappedResult.IgstTotal + mappedResult.CessTotal + mappedResult.RoundOff)
                        throw new InvalidOperationException("Source-linked invoice totals do not exactly reconcile.");
                    var account=_clients.GetById(clientId);
                    var activeGstin=new string((account?.Gstin ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
                    var seller=sourceRoot.GetProperty("seller").GetProperty("gstin").GetString() ?? "";
                    var buyer=sourceRoot.GetProperty("buyer").GetProperty("gstin").GetString() ?? "";
                    if (activeGstin.Length!=15 ||
                        !(mappedResult.Direction.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? activeGstin==buyer :
                          mappedResult.Direction.Equals("Sales", StringComparison.OrdinalIgnoreCase) && activeGstin==seller))
                        throw new InvalidOperationException("Client GSTIN and Purchase/Sales direction disagree with the source document.");
                    var sourceOpposite = mappedResult.Direction.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? seller : buyer;
                    if (!MobileInvoicePartyIdentityResolver.ValidGstin(sourceOpposite) ||
                        !MobileInvoicePartyIdentityResolver.NormalizeGstin(mappedResult.CounterpartyGstin)
                            .Equals(sourceOpposite, StringComparison.Ordinal))
                        throw new InvalidOperationException("Verified counterparty GSTIN must match the opposite invoice party before Sales/Purchase handoff.");
                }
            }
            HandOffMobileInvoiceDirect(clientId, financialYear, scanId, mappedResult);
        }

        _mobileInvoiceQueue.TryRemove(key, out _);
        TryDeleteMobileEvidence(row.EvidenceFolder);
        MobileLinkChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void CleanupLegacyMobileInvoiceQueueFiles()
    {
        // R26: historical R42-R25 queue folders contained per-invoice JSON evidence.
        // Invoice payloads are now transient/in-memory, so remove those old queue artifacts.
        var root = Path.Combine(ApplicationDataPaths.RootFolder, "MobileLink");
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var queue in Directory.EnumerateDirectories(root, "Queue", SearchOption.AllDirectories))
                try { Directory.Delete(queue, true); } catch { }
        }
        catch { }
    }

    private static string MobileQueueKey(long clientId, string financialYear, string scanId) =>
        $"{clientId}|{financialYear.Trim().ToUpperInvariant()}|{SafeMobileSegment(scanId)}";

    private static void TryDeleteMobileEvidence(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try { Directory.Delete(folder, true); } catch { /* temporary evidence cleanup is best-effort */ }
    }

    private void HandOffMobileInvoiceDirect(long clientId, string financialYear, string scanId, MobileInvoiceMappedResult mapped)
    {
        if (mapped.Direction.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
        {
            var records = BuildMobilePurchaseRecords(mapped);
            var result = new PurchaseManualImportResult
            {
                SourcePath = $"MobileLink:{scanId}",
                HeaderSignature = $"MOBILE:{clientId}:{financialYear}:{mapped.InvoiceNumber}:{mapped.InvoiceDate:yyyyMMdd}",
                Records = records,
                PurchaseRows = records.Count
            };
            _mobilePurchaseRepository.SaveManualImport(clientId, financialYear, result, false);
        }
        else if (mapped.Direction.Equals("Sales", StringComparison.OrdinalIgnoreCase))
        {
            var records = BuildMobileSalesRecords(mapped);
            var result = new SalesManualImportResult
            {
                SourcePath = $"MobileLink:{scanId}",
                HeaderSignature = $"MOBILE:{clientId}:{financialYear}:{mapped.InvoiceNumber}:{mapped.InvoiceDate:yyyyMMdd}",
                Records = records,
                SalesRows = records.Count
            };
            _mobileSalesRepository.SaveManualImport(clientId, financialYear, result, false);
        }
        else throw new InvalidOperationException("Mobile invoice direction must be Sales or Purchase before confirmation.");
    }

    private static IReadOnlyList<PurchaseImportRecord> BuildMobilePurchaseRecords(MobileInvoiceMappedResult mapped)
    {
        if (mapped.Lines.Count == 0)
            return new[]
            {
                new PurchaseImportRecord
                {
                    Destination = "Purchase", ReconciliationStatus = "Manual", GstApplicability = PurchaseTreatmentPolicy.NotShownInTwoB,
                    SupplierName = mapped.CounterpartyName, SupplierGstin = mapped.CounterpartyGstin, DocumentType = mapped.DocumentType,
                    DocumentNumber = mapped.InvoiceNumber, DocumentDate = mapped.InvoiceDate, GstDate = mapped.InvoiceDate,
                    SourceDocumentValue = mapped.InvoiceTotal, DocumentValue = mapped.InvoiceTotal, TaxableValue = mapped.TaxableTotal,
                    Cgst = mapped.CgstTotal, Sgst = mapped.SgstTotal, Igst = mapped.IgstTotal, Cess = mapped.CessTotal,
                    RoundOff = mapped.RoundOff, SourceSection = "Mobile Link", ItemSequence = 1
                }
            };
        return mapped.Lines.Select((r, i) => new PurchaseImportRecord
        {
            Destination = "Purchase", ReconciliationStatus = "Manual", GstApplicability = PurchaseTreatmentPolicy.NotShownInTwoB,
            SupplierName = mapped.CounterpartyName, SupplierGstin = mapped.CounterpartyGstin, DocumentType = mapped.DocumentType,
            DocumentNumber = mapped.InvoiceNumber, DocumentDate = mapped.InvoiceDate, GstDate = mapped.InvoiceDate,
            SourceDocumentValue = mapped.InvoiceTotal, DocumentValue = mapped.InvoiceTotal, Rate = r.GstRate,
            TaxableValue = r.Taxable, Cgst = r.Cgst, Sgst = r.Sgst, Igst = r.Igst, Cess = r.Cess,
            RoundOff = i == 0 ? mapped.RoundOff : 0m, SourceSection = "Mobile Link", ItemSequence = i + 1,
            ItemName = r.Description, HsnCode = r.HsnSac, ItemAmount = r.LineTotal, ItemRate = r.Rate,
            // R46 R27: Mobile discount is recognition/review evidence only. TaxableValue is
            // already post-discount accounting authority, so do not post a second discount to Tally.
            ActualQuantity = r.Quantity, BilledQuantity = r.Quantity, Unit = r.Unit, DiscountAmount = 0m
        }).ToArray();
    }

    private static IReadOnlyList<SalesImportRecord> BuildMobileSalesRecords(MobileInvoiceMappedResult mapped)
    {
        if (mapped.Lines.Count == 0)
            return new[]
            {
                new SalesImportRecord
                {
                    Destination = "Sales", CustomerName = mapped.CounterpartyName, OriginalCustomerName = mapped.CounterpartyName,
                    CustomerNameSource = "Mobile Link", CustomerGstin = mapped.CounterpartyGstin, DocumentType = mapped.DocumentType,
                    DocumentNumber = mapped.InvoiceNumber, DocumentDate = mapped.InvoiceDate, GstDate = mapped.InvoiceDate,
                    SourceDocumentValue = mapped.InvoiceTotal, DocumentValue = mapped.InvoiceTotal, TaxableValue = mapped.TaxableTotal,
                    Cgst = mapped.CgstTotal, Sgst = mapped.SgstTotal, Igst = mapped.IgstTotal, Cess = mapped.CessTotal,
                    RoundOff = mapped.RoundOff, SourceSection = "Mobile Link", SourceOrigin = "Mobile Link", ItemSequence = 1
                }
            };
        return mapped.Lines.Select((r, i) => new SalesImportRecord
        {
            Destination = "Sales", CustomerName = mapped.CounterpartyName, OriginalCustomerName = mapped.CounterpartyName,
            CustomerNameSource = "Mobile Link", CustomerGstin = mapped.CounterpartyGstin, DocumentType = mapped.DocumentType,
            DocumentNumber = mapped.InvoiceNumber, DocumentDate = mapped.InvoiceDate, GstDate = mapped.InvoiceDate,
            SourceDocumentValue = mapped.InvoiceTotal, DocumentValue = mapped.InvoiceTotal, Rate = r.GstRate,
            TaxableValue = r.Taxable, Cgst = r.Cgst, Sgst = r.Sgst, Igst = r.Igst, Cess = r.Cess,
            RoundOff = i == 0 ? mapped.RoundOff : 0m, SourceSection = "Mobile Link", SourceOrigin = "Mobile Link", ItemSequence = i + 1,
            ItemName = r.Description, OriginalItemName = r.Description, HsnCode = r.HsnSac, ItemAmount = r.LineTotal,
            ItemRate = r.Rate, ActualQuantity = r.Quantity, BilledQuantity = r.Quantity, Unit = r.Unit, OriginalUnit = r.Unit,
            // R46 R27: Mobile discount is recognition/review evidence only. TaxableValue is
            // already post-discount accounting authority, so do not post a second discount to Tally.
            DiscountAmount = 0m
        }).ToArray();
    }

    public void EnsureInitializedForUi() => EnsureRepositoriesInitialized();

    private void EnsureRepositoriesInitialized()
    {
        if (_repositoriesInitialized) return;
        lock (_repositoryInitializationGate)
        {
            if (_repositoriesInitialized) return;
            _governance.Initialize();
            _serverSecurity.Initialize();
            _clients.Initialize();
            _enterpriseControl.Initialize();
            _collaboration.Initialize();
            _mobileAccounting.Initialize();
            _invoiceMasterRepository.Initialize();
            _invoiceStockRepository.Initialize();
            _mobilePurchaseRepository.Initialize();
            _mobileSalesRepository.Initialize();
            CleanupLegacyMobileInvoiceQueueFiles();
            _repositoriesInitialized = true;
        }
    }

    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRunning) await ApplySettingsCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!IsRunning) throw new InvalidOperationException(StatusText);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ApplySettingsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ApplySettingsCoreAsync(CancellationToken cancellationToken)
    {
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        if (!_settings.GetBool(ApplicationSettingKeys.SecurityServerMode))
        {
            StatusText = "Main Server mode is disabled. Local accounting remains available.";
            NotifyRunningStateChanged();
            return;
        }

        if (!_settings.GetBool(ApplicationSettingKeys.SecurityLanEnabled))
        {
            EnsureRepositoriesInitialized();
            StatusText = "Main Server mode is enabled, but LAN/WireGuard HTTPS access is disabled.";
            _storage?.EnsureServerFolders();
            StartBackgroundServerJobs();
            NotifyRunningStateChanged();
            return;
        }

        var bindText = _settings.Get(ApplicationSettingKeys.SecurityLanBindAddress);
        if (!SecurityNetworkPolicy.TryResolvePrivateBindAddress(bindText, out var bindAddress, out var validationError))
        {
            StatusText = validationError;
            NotifyRunningStateChanged();
            return;
        }

        var port = Math.Clamp(_settings.GetInt(ApplicationSettingKeys.SecurityLanPort, 47831), 1024, 65535);
        var listenAddresses = SecurityNetworkPolicy.GetServerListenAddresses(bindAddress);
        if (!CanBindHttpsPort(listenAddresses, port))
        {
            StatusText = $"AAH Main Server is waiting for https://{bindAddress}:{port}; that port is currently in use. The configured port is kept stable for enrolled mobile devices and AAH will retry automatically in {AutomaticStartRetrySeconds} seconds.";
            ScheduleAutomaticStartRetry();
            NotifyRunningStateChanged();
            return;
        }

        // Do not perform the heavier server repository/certificate work until the configured
        // endpoint is actually bindable. This keeps transient port conflicts out of startup I/O.
        EnsureRepositoriesInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ExpireInactiveTrust();
        _storage?.EnsureServerFolders();
        _storage?.RegisterConfiguredPackages();

        var certificate = EnsureCertificate(bindAddress, listenAddresses);
        _activeServerCertificateQrPin = ComputeCertificateQrPin(certificate);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 260L * 1024 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            foreach (var listenAddress in listenAddresses)
                options.Listen(listenAddress, port, listen => listen.UseHttps(certificate));
        });
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (!AllowRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new { error = "Too many requests. Try again shortly." }, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            await next().ConfigureAwait(false);
        });

        // Native clients only. The server does not expose an accounting web interface.
        app.MapGet("/", () => Results.NotFound(new { product = "Accounting Automation Hub", access = "Native Windows/Android clients only" }));
        app.MapGet("/health", () => Results.Json(new { status = "ok", transport = "https", server = Environment.MachineName, nativeClientsOnly = true, windowsAuthentication = "RegistrationOtpOnceThenPassword", remoteControl = "AllowListedAahCommands" }));
        app.MapPost("/api/v1/enroll", (Func<HttpContext, Task<IResult>>)EnrollDeviceAsync);
        app.MapPost("/api/v1/android/qr/enroll", (Func<HttpContext, Task<IResult>>)EnrollAndroidWithQrAsync);
        app.MapPost("/api/v1/android/qr/login", (Func<HttpContext, Task<IResult>>)LoginAndroidWithQrAsync);
        app.MapPost("/api/v1/android/qr/user", (Func<HttpContext, Task<IResult>>)UseAndroidUserQrAsync);
        app.MapGet("/api/v1/android/admin/users", GetAndroidAdminUsers);
        app.MapPost("/api/v1/android/admin/users/{userName}", (Func<HttpContext,string,Task<IResult>>)UpdateAndroidAdminUserAsync);
        app.MapPost("/api/v1/auth/challenge", (Func<HttpContext, Task<IResult>>)CreateLoginChallengeAsync);
        app.MapPost("/api/v1/auth/password-preflight", (Func<HttpContext, Task<IResult>>)PasswordPreflightAsync);
        app.MapPost("/api/v1/auth/login", (Func<HttpContext, Task<IResult>>)LoginAsync);
        app.MapPost("/api/v1/auth/trusted-login", (Func<HttpContext, Task<IResult>>)TrustedLoginAsync);
        app.MapPost("/api/v1/auth/device-resume", (Func<HttpContext, Task<IResult>>)ResumeAndroidDeviceSessionAsync);
        app.MapGet("/api/v1/auth/session", GetSessionIdentity);
        app.MapGet("/api/v1/auth/mobile-code", GetCurrentUserMobileCode);
        app.MapPost("/api/v1/auth/mobile-code/refresh", RefreshCurrentUserMobileCode);
        app.MapPost("/api/v1/auth/logout", (Func<HttpContext, Task<IResult>>)LogoutAsync);
        app.MapPost("/api/v1/heartbeat", (Func<HttpContext, Task<IResult>>)HeartbeatAsync);
        app.MapGet("/api/v1/clients", GetClients);
        app.MapGet("/api/v1/full-ui/snapshot", DownloadFullUiSnapshot);
        app.MapGet("/api/v1/client/{clientId:long}/summary", GetClientSummary);
        app.MapPost("/api/v1/client/{clientId:long}/upload", UploadFileAsync);
        app.MapGet("/api/v1/client/{clientId:long}/export/{kind}", ExportAsync);
        app.MapGet("/api/v1/client/{clientId:long}/ledger-mappings", GetMobileLedgerMappings);
        app.MapPost("/api/v1/client/{clientId:long}/ledger-mappings/{mappingId:long}", (Func<HttpContext,long,long,Task<IResult>>)SaveMobileLedgerMappingAsync);
        app.MapGet("/api/v1/client/{clientId:long}/suspense/export/{format}", ExportMobileSuspense);
        app.MapGet("/api/v1/client/{clientId:long}/cash", GetMobileCashEntries);
        app.MapPost("/api/v1/client/{clientId:long}/cash", (Func<HttpContext,long,Task<IResult>>)SaveMobileCashAsync);
        app.MapGet("/api/v1/client/{clientId:long}/journal", GetMobileJournalEntries);
        app.MapPost("/api/v1/client/{clientId:long}/journal", (Func<HttpContext,long,Task<IResult>>)SaveMobileJournalAsync);
        app.MapGet("/api/v1/client/{clientId:long}/auto-import", GetClientAutoImportPolicy);
        app.MapPost("/api/v1/client/{clientId:long}/auto-import", (Func<HttpContext,long,Task<IResult>>)SetClientAutoImportPolicyAsync);
        app.MapGet("/api/v1/client/{clientId:long}/tally-link", GetClientTallyLinkFoundation);
        app.MapGet("/api/v1/client/{clientId:long}/mobile-link", GetMobileLinkForAndroid);
        app.MapGet("/api/v1/client/{clientId:long}/invoice-masters", GetInvoiceMasterOptions);
        app.MapGet("/api/v1/client/{clientId:long}/mobile-learning", GetMobileInvoiceLearningForAndroid);
        app.MapPost("/api/v1/client/{clientId:long}/mobile-invoices", (Func<HttpContext,long,Task<IResult>>)ReceiveMobileInvoiceAsync);
        app.MapGet("/api/v1/sync/import-jobs", GetDistributedImportJobs);
        app.MapGet("/api/v1/android/import-jobs/{jobId}", GetAndroidImportJobStatus);
        app.MapPost("/api/v1/android/import-jobs/{jobId}/cancel", (Func<HttpContext,string,Task<IResult>>)CancelAndroidImportJobAsync);
        app.MapGet("/api/v1/sync/import-jobs/{jobId}/file", DownloadDistributedImportJobFile);
        app.MapPost("/api/v1/sync/import-jobs/{jobId}/complete", (Func<HttpContext,string,Task<IResult>>)CompleteDistributedImportJobAsync);
        app.MapGet("/api/v1/sync/registries", GetRegistrySyncSnapshot);
        app.MapPost("/api/v1/sync/registries", (Func<HttpContext,Task<IResult>>)MergeRegistryDeltasAsync);
        app.MapGet("/api/v1/sync/auto-import-policies", GetAutoImportPoliciesForNode);
        app.MapPost("/api/v1/sync/import-source/{clientId:long}", (Func<HttpContext,long,Task<IResult>>)ReceiveCompletedNodeImportAsync);
        app.MapGet("/api/v1/sync/import-source-status/{jobId}", GetCompletedNodeImportStatus);
        app.MapGet("/api/v1/mapping-sessions/{sessionId}", GetMappingSession);
        app.MapPost("/api/v1/mapping-sessions/{sessionId}/claim", (Func<HttpContext,string,Task<IResult>>)ClaimMappingSessionAsync);
        app.MapPost("/api/v1/folder-links", (Func<HttpContext, Task<IResult>>)CreateFolderLinkAsync);
        app.MapPost("/api/v1/folder-links/{linkId}/upload", UploadFolderFileAsync);
        app.MapPost("/api/v1/folder-links/{linkId}/heartbeat", HeartbeatFolderLink);
        app.MapGet("/api/v1/packages/{kind}/metadata", GetPackageMetadata);
        app.MapGet("/api/v1/packages/{kind}/download", DownloadPackage);
        app.MapGet("/api/v1/control/status", GetEnterpriseClientStatus);
        app.MapGet("/api/v1/remote/commands", (Func<HttpContext,Task<IResult>>)GetRemoteCommandsAsync);
        app.MapPost("/api/v1/remote/commands/{commandId}/complete", (Func<HttpContext,string,Task<IResult>>)CompleteRemoteCommandAsync);
        app.MapGet("/api/v1/remote/assist/current", GetCurrentRemoteAssistSession);
        app.MapPost("/api/v1/remote/assist/{sessionId}/consent", (Func<HttpContext,string,Task<IResult>>)SetRemoteAssistConsentByUserAsync);
        app.MapPost("/api/v1/remote/assist/{sessionId}/end", (Func<HttpContext,string,Task<IResult>>)EndRemoteAssistByUserAsync);
        app.MapGet("/api/v1/remote/assist/{sessionId}/status", GetRemoteAssistSessionStatus);
        app.MapGet("/api/v1/remote/assist/{sessionId}/stream/state", GetRemoteAssistStreamState);
        app.MapPost("/api/v1/remote/assist/{sessionId}/stream/remote-state", (Func<HttpContext,string,Task<IResult>>)SetRemoteAssistRemoteStateAsync);
        app.MapPost("/api/v1/remote/assist/{sessionId}/frame/remote", (Func<HttpContext,string,Task<IResult>>)UploadRemoteAssistFrameAsync);
        app.MapGet("/api/v1/remote/assist/{sessionId}/frame/administrator", GetRemoteAssistAdministratorFrame);
        app.MapGet("/api/v1/remote/assist/{sessionId}/input/remote", GetRemoteAssistRemoteInput);
        app.MapPost("/api/v1/remote/assist/{sessionId}/input/administrator", (Func<HttpContext,string,Task<IResult>>)PostRemoteAssistAdministratorInputAsync);
        app.MapGet("/api/v1/directory/users", GetDirectoryUsers);
        app.MapGet("/api/v1/messages", GetMessages);
        app.MapPost("/api/v1/messages", (Func<HttpContext,Task<IResult>>)SendMessageAsync);
        app.MapPost("/api/v1/messages/read", (Func<HttpContext,Task<IResult>>)MarkMessagesReadAsync);
        app.MapPost("/api/v1/messages/{id:long}/edit", (Func<HttpContext,long,Task<IResult>>)EditMessageAsync);
        app.MapPost("/api/v1/messages/{id:long}/delete", (Func<HttpContext,long,Task<IResult>>)DeleteMessageAsync);
        app.MapPost("/api/v1/messages/{id:long}/pin", (Func<HttpContext,long,Task<IResult>>)PinMessageAsync);
        app.MapPost("/api/v1/messages/{id:long}/reaction", (Func<HttpContext,long,Task<IResult>>)ReactMessageAsync);
        app.MapPost("/api/v1/directory/status", (Func<HttpContext,Task<IResult>>)SetDirectoryStatusAsync);
        app.MapGet("/api/v1/shared-files", GetSharedFiles);
        app.MapPost("/api/v1/shared-files", (Func<HttpContext,Task<IResult>>)UploadSharedFileAsync);
        app.MapGet("/api/v1/shared-files/{fileId}", DownloadSharedFile);
        app.MapGet("/api/v1/tasks", GetTasks);
        app.MapPost("/api/v1/tasks", (Func<HttpContext,Task<IResult>>)CreateTaskAsync);
        app.MapPost("/api/v1/tasks/{id:long}/update", (Func<HttpContext,long,Task<IResult>>)UpdateTaskAsync);
        app.MapGet("/api/v1/approvals", GetApprovals);
        app.MapPost("/api/v1/approvals", (Func<HttpContext,Task<IResult>>)CreateApprovalAsync);
        app.MapPost("/api/v1/approvals/{id:long}/decision", (Func<HttpContext,long,Task<IResult>>)DecideApprovalAsync);
        app.MapGet("/bootstrap/{kind}/{ticket}", DownloadBootstrapPackage);

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _application = app;
            Endpoint = $"https://{bindAddress}:{port}";
            var overlay = listenAddresses.FirstOrDefault(x => !x.Equals(bindAddress));
            RemoteAssistInternetEndpoint = overlay is null ? string.Empty : $"https://{overlay}:{port}";
            _storage?.RegisterConfiguredPackages(Endpoint, RemoteAssistInternetEndpoint);
            var routeText = string.IsNullOrWhiteSpace(RemoteAssistInternetEndpoint)
                ? "No private Internet/VPN overlay endpoint is currently detected; Remote Assist will use the normal private Main Server route."
                : $"Remote Assist Internet/private-VPN endpoint: {RemoteAssistInternetEndpoint}.";
            StatusText = $"AAH Main Server live at {Endpoint}. {routeText} Device registration trust, policy-controlled native clients, remote AAH control, collaboration and server retention are active.";
            CancelAutomaticStartRetry();
            StartBackgroundServerJobs();
            NotifyRunningStateChanged();
        }
        catch (Exception exception)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            _application = null;
            Endpoint = string.Empty;
            RemoteAssistInternetEndpoint = string.Empty;
            StatusText = $"AAH Main Server could not start on the configured endpoint: {exception.Message}";
            ScheduleAutomaticStartRetry();
            NotifyRunningStateChanged();
        }
    }

    private static bool CanBindHttpsPort(IReadOnlyList<IPAddress> listenAddresses, int port)
    {
        var probes = new List<Socket>();
        try
        {
            foreach (var address in listenAddresses.Distinct())
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    ExclusiveAddressUse = true
                };
                socket.Bind(new IPEndPoint(address, port));
                probes.Add(socket);
            }
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            foreach (var socket in probes)
            {
                try { socket.Dispose(); } catch { }
            }
        }
    }

    private void NotifyRunningStateChanged()
    {
        try { RunningStateChanged?.Invoke(); }
        catch { /* UI refresh listeners must never break Main Server lifecycle. */ }
    }

    private void ScheduleAutomaticStartRetry()
    {
        if (_disposed || IsRunning || !_settings.GetBool(ApplicationSettingKeys.SecurityServerMode)) return;
        if (Interlocked.Exchange(ref _automaticStartRetryScheduled, 1) != 0) return;

        _automaticStartRetryTimer?.Dispose();
        _automaticStartRetryTimer = new Timer(_ =>
        {
            Interlocked.Exchange(ref _automaticStartRetryScheduled, 0);
            _ = Task.Run(async () =>
            {
                if (_disposed || IsRunning) return;
                try { await EnsureRunningAsync().ConfigureAwait(false); }
                catch { /* StatusText already carries the failure; another bounded retry is scheduled by ApplySettingsCoreAsync. */ }
            });
        }, null, TimeSpan.FromSeconds(AutomaticStartRetrySeconds), Timeout.InfiniteTimeSpan);
    }

    private void CancelAutomaticStartRetry()
    {
        Interlocked.Exchange(ref _automaticStartRetryScheduled, 0);
        var timer = Interlocked.Exchange(ref _automaticStartRetryTimer, null);
        try { timer?.Dispose(); } catch { }
    }

    public PairingCodeResult GeneratePairingCode(string? boundUserName = null, string deviceType = ServerDeviceTypes.Windows)
    {
        DemandPermission(GovernancePermissions.ManageDevices);
        if (!IsRunning) throw new InvalidOperationException(StatusText);
        var normalizedUser = (boundUserName ?? string.Empty).Trim();
        if (normalizedUser.Length > 0)
        {
            var user = _governance.GetUsers().FirstOrDefault(x => x.WindowsUserName.Equals(normalizedUser, StringComparison.OrdinalIgnoreCase));
            if (user is null || !user.IsActive) throw new InvalidOperationException("Choose an active AAH user before creating a user-bound mobile enrollment QR.");
            normalizedUser = user.WindowsUserName;
        }
        var normalizedDeviceType = deviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) ? ServerDeviceTypes.Android : ServerDeviceTypes.Windows;
        var code = RandomNumberGenerator.GetInt32(10000000, 100000000).ToString();
        var expires = DateTime.UtcNow.AddMinutes(normalizedDeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) ? 5 : 10);
        lock (_gate) _pairing = new PairingWindow(code, expires, normalizedUser, normalizedDeviceType);
        SafeLogEvent("EnrollmentGrantCreated", Environment.UserName, "SERVER", $"{normalizedDeviceType}; boundUser={(normalizedUser.Length == 0 ? "Any authorised user" : normalizedUser)}; expires={expires:O}", true);
        return new PairingCodeResult(code, expires, normalizedUser, normalizedDeviceType);
    }

    public AndroidQrCodeResult GenerateAndroidLoginQr(string boundUserName)
    {
        DemandPermission(GovernancePermissions.ManageDevices);
        if (!IsRunning) throw new InvalidOperationException(StatusText);
        var normalizedUser = (boundUserName ?? string.Empty).Trim();
        var user = _governance.GetUsers().FirstOrDefault(x => x.WindowsUserName.Equals(normalizedUser, StringComparison.OrdinalIgnoreCase));
        if (user is null || !user.IsActive) throw new InvalidOperationException("Choose an active AAH user before creating an Android login QR.");
        var hasEnrolledAndroid = _serverSecurity.GetServerDevices().Any(x =>
            x.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) &&
            x.UserName.Equals(user.WindowsUserName, StringComparison.OrdinalIgnoreCase) &&
            x.IsActive &&
            !x.EnrollmentState.Equals(ServerEnrollmentStates.Revoked, StringComparison.OrdinalIgnoreCase) &&
            !x.EnrollmentState.Equals(ServerEnrollmentStates.Retired, StringComparison.OrdinalIgnoreCase));
        if (!hasEnrolledAndroid) throw new InvalidOperationException("This user has no active enrolled Android device. Use First-time Enrollment QR first.");
        string code;
        do code = RandomNumberGenerator.GetInt32(10000000, 100000000).ToString();
        while (_androidLoginQrs.ContainsKey(code));
        var expires = DateTime.UtcNow.AddMinutes(2);
        _androidLoginQrs[code] = new AndroidLoginQrWindow(code, expires, user.WindowsUserName);
        SafeLogEvent("AndroidLoginQrCreated", Environment.UserName, "SERVER", $"boundUser={user.WindowsUserName}; expires={expires:O}", true);
        return new AndroidQrCodeResult(code, expires, user.WindowsUserName, "Login");
    }

    public AndroidUserQrCodeResult GetAndroidUserQrCode(string userName)
    {
        var requestedUser = (userName ?? string.Empty).Trim();
        var currentOperator = GetApplicationUser(Environment.UserName);
        var viewingOwnCode = currentOperator is not null && currentOperator.WindowsUserName.Equals(requestedUser, StringComparison.OrdinalIgnoreCase);
        var isAdministrator = currentOperator?.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase) == true;
        if (!viewingOwnCode && !isAdministrator) DemandPrimaryAdministrator();
        if (!IsRunning) throw new InvalidOperationException(StatusText);
        return EnsureAndroidUserQrCode(requestedUser);
    }

    // R77: Rotate an already registered user's temporary QR without changing enrollment or permissions.
    public AndroidUserQrCodeResult RotateAndroidUserQrCode(string userName)
    {
        var requestedUser = (userName ?? string.Empty).Trim();
        var currentOperator = GetApplicationUser(Environment.UserName);
        var viewingOwnCode = currentOperator is not null && currentOperator.WindowsUserName.Equals(requestedUser, StringComparison.OrdinalIgnoreCase);
        var isAdministrator = currentOperator?.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase) == true;
        if (!viewingOwnCode && !isAdministrator) DemandPrimaryAdministrator();
        if (!IsRunning) throw new InvalidOperationException(StatusText);
        return EnsureAndroidUserQrCode(requestedUser, forceRotate: true);
    }

    private AndroidUserQrCodeResult EnsureAndroidUserQrCode(string userName, bool forceRotate = false)
    {
        var normalizedUser = (userName ?? string.Empty).Trim();
        var user = GetApplicationUser(normalizedUser) ?? throw new InvalidOperationException("The selected AAH user no longer exists.");
        if (!user.IsActive) throw new InvalidOperationException("The selected AAH user is disabled.");
        lock (_gate)
        {
            if (!forceRotate && _androidUserQrs.TryGetValue(user.WindowsUserName, out var existing) && existing.ExpiresUtc > DateTime.UtcNow)
                return BuildAndroidUserQrResult(existing);
            string code;
            do code = RandomNumberGenerator.GetInt32(10000000, 100000000).ToString();
            while (_androidUserQrs.Values.Any(x => FixedEquals(x.Code, code)) || _androidLoginQrs.ContainsKey(code));
            var created = new AndroidUserQrWindow(code, DateTime.UtcNow.AddMinutes(5), user.WindowsUserName);
            _androidUserQrs[user.WindowsUserName] = created;
            return BuildAndroidUserQrResult(created);
        }
    }

    private AndroidUserQrCodeResult BuildAndroidUserQrResult(AndroidUserQrWindow qr)
    {
        if (string.IsNullOrWhiteSpace(Endpoint)) throw new InvalidOperationException("AAH Main Server HTTPS endpoint is not available.");
        var endpoint = Endpoint.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var pin = ServerCertificateQrPin;
        if (string.IsNullOrWhiteSpace(pin)) throw new InvalidOperationException("The Main Server TLS certificate identity is unavailable.");
        var user = Uri.EscapeDataString(qr.BoundUserName);
        var payload = $"AAH3|{endpoint}|U|{qr.Code}|{pin}|{user}";
        return new AndroidUserQrCodeResult(qr.Code, qr.ExpiresUtc, qr.BoundUserName, payload, "UserAccess");
    }

    private void EnsureAndroidUserQrCodes()
    {
        if (!IsRunning) return;
        foreach (var user in _governance.GetUsers().Where(x => x.IsActive))
        {
            try { EnsureAndroidUserQrCode(user.WindowsUserName); } catch { }
        }
        foreach (var key in _androidUserQrs.Keys)
        {
            var user = GetApplicationUser(key);
            if (user is null || !user.IsActive) _androidUserQrs.TryRemove(key, out _);
        }
    }

    private bool TryConsumeAndroidUserQr(string code, out AndroidUserQrWindow qr)
    {
        qr = null!;
        if (string.IsNullOrWhiteSpace(code)) return false;
        string rotatedUser;
        lock (_gate)
        {
            var current = _androidUserQrs.Values.FirstOrDefault(x => x.ExpiresUtc > DateTime.UtcNow && FixedEquals(x.Code, code.Trim()));
            if (current is null) return false;
            qr = current;
            // A valid scan is a use. Rotate immediately so screenshots/replays of the old QR cannot be reused.
            EnsureAndroidUserQrCode(current.BoundUserName, forceRotate: true);
            rotatedUser = current.BoundUserName;
        }
        try { AndroidUserQrRotated?.Invoke(rotatedUser); }
        catch (Exception exception) { SafeLogEvent("AndroidUserQrRotateNotify", rotatedUser, "SERVER", "Live QR rotated but UI refresh notification failed: " + exception.Message, false); }
        return true;
    }

    public DistributionLinkResult GenerateDistributionLink(string packageKind)
    {
        DemandPermission(GovernancePermissions.ManageLanUpdates);
        if (!IsRunning || Endpoint.Length == 0) throw new InvalidOperationException("AAH Main Server must be running before creating a network download ticket.");
        _storage?.RegisterConfiguredPackages(Endpoint, RemoteAssistInternetEndpoint);
        var package = _serverSecurity.GetPackage(packageKind);
        if (package is null || !File.Exists(package.FilePath)) throw new FileNotFoundException($"No approved {packageKind} package is registered in Security & LAN settings.");
        var token = _serverSecurity.CreateDistributionTicket(packageKind, package.Sha256, package.Version, Environment.UserName, TimeSpan.FromMinutes(30));
        var url = $"{Endpoint}/bootstrap/{Uri.EscapeDataString(packageKind)}/{Uri.EscapeDataString(token)}";
        return new DistributionLinkResult(packageKind, url, DateTime.UtcNow.AddMinutes(30), package.Sha256, package.Version);
    }

    public WindowsUpdatePublishResult PublishWindowsUpdate(bool force)
    {
        DemandPermission(GovernancePermissions.ManageLanUpdates);
        if (!IsRunning || Endpoint.Length == 0) throw new InvalidOperationException("AAH Main Server must be running before publishing a LAN application update.");
        _storage?.RegisterConfiguredPackages(Endpoint, RemoteAssistInternetEndpoint);
        var package = _serverSecurity.GetPackage("Windows");
        if (package is null || !File.Exists(package.FilePath)) throw new FileNotFoundException("The approved Windows LAN application package is not available.");
        _settings.Set(ApplicationSettingKeys.SecurityForceWindowsUpdateVersion, force ? package.Version : string.Empty);
        _serverSecurity.LogEvent(force ? "LanUpdateForced" : "LanUpdatePublished", Environment.UserName, "SERVER", $"Windows; {package.Version}; {package.Sha256}", true);
        return new WindowsUpdatePublishResult(package.Version, package.Sha256, force);
    }

    public async Task<string> RunServerReplicationNowAsync(CancellationToken cancellationToken = default)
    {
        DemandPermission(GovernancePermissions.ManageBackup);
        return _storage is null ? "Server storage is unavailable." : await _storage.RunReplicationAsync(cancellationToken).ConfigureAwait(false);
    }

    public void NotifyAutomationSourceCompleted(long clientId, string financialYear, string sourcePath, string module)
    {
        if (!_settings.GetBool(ApplicationSettingKeys.SecurityServerMode) || _disposed || _storage is null) return;
        NetworkLiveRevisionTracker.BumpAccountingData();
        _ = Task.Run(async () =>
        {
            try
            {
                var archived = await _storage.ArchiveAutomationSourceNowAsync(clientId, financialYear, sourcePath).ConfigureAwait(false);
                if (archived) _serverSecurity.LogEvent("AutomationSourceRetained", Environment.UserName, "SERVER", $"{clientId}/{financialYear}; {module}; {Path.GetFileName(sourcePath)}", true);
            }
            catch { }
        });
    }

    public void NotifyLocalDataChanged(long clientId, string financialYear, string reason)
    {
        if (!_settings.GetBool(ApplicationSettingKeys.SecurityServerMode) || _disposed) return;
        NetworkLiveRevisionTracker.BumpAccountingData();
        // Debounce bursts of row-level live events into one consistent online backup/retention pass.
        // The app never blocks the accounting write waiting for the server copy.
        _dataReplicationDebounceTimer ??= new Timer(async _ =>
        {
            try { if (_storage is not null) await _storage.RunReplicationAsync().ConfigureAwait(false); } catch { }
        });
        _dataReplicationDebounceTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        _serverSecurity.LogEvent("ServerReplicationQueued", Environment.UserName, "SERVER", $"Client={clientId}; FY={financialYear}; {reason}", true);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        CancelAutomaticStartRetry();
        _leaseTimer?.Dispose(); _leaseTimer = null;
        _androidQrRotationTimer?.Dispose(); _androidQrRotationTimer = null;
        _replicationTimer?.Dispose(); _replicationTimer = null;
        _dataReplicationDebounceTimer?.Dispose(); _dataReplicationDebounceTimer = null;
        var app = _application; _application = null; Endpoint = string.Empty; RemoteAssistInternetEndpoint = string.Empty; _activeServerCertificateQrPin = string.Empty;
        lock (_gate) _pairing = null;
        if (app is not null)
        {
            try { await app.StopAsync(cancellationToken).ConfigureAwait(false); } catch { }
            try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        StatusText = _settings.GetBool(ApplicationSettingKeys.SecurityServerMode) ? "AAH Main Server is stopped." : "Main Server mode is disabled.";
        NotifyRunningStateChanged();
    }

    private async Task<IResult> EnrollAndroidWithQrAsync(HttpContext context)
    {
        AndroidQrEnrollmentRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<AndroidQrEnrollmentRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error = "Invalid Android QR enrollment request.", code = "ANDROID_QR_ENROLLMENT_INVALID" }); }
        if (request is null) return Results.BadRequest(new { error = "Android QR enrollment request is required.", code = "ANDROID_QR_ENROLLMENT_REQUIRED" });

        PairingWindow? pairing;
        lock (_gate)
        {
            pairing = _pairing;
            if (pairing is not null && pairing.ExpiresUtc > DateTime.UtcNow && pairing.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) && FixedEquals(pairing.Code, request.Code))
                _pairing = null; // Android QR is consumed atomically before any registration work begins.
        }
        if (pairing is null || pairing.ExpiresUtc <= DateTime.UtcNow)
            return Results.Json(new { error = "This Android enrollment QR has expired or was already used. Generate a fresh one-time QR on the Main Server.", code = "ANDROID_ENROLLMENT_QR_EXPIRED" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!pairing.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) || !FixedEquals(pairing.Code, request.Code))
            return Results.Json(new { error = "This Android enrollment QR is invalid or was already used.", code = "ANDROID_ENROLLMENT_QR_INVALID" }, statusCode: StatusCodes.Status401Unauthorized);
        if (pairing.BoundUserName.Length == 0)
            return Results.Json(new { error = "This Android enrollment QR is not bound to an AAH user.", code = "ANDROID_ENROLLMENT_QR_USER_MISSING" }, statusCode: StatusCodes.Status403Forbidden);

        var user = GetApplicationUser(pairing.BoundUserName);
        if (user is null || !user.IsActive)
            return Results.Json(new { error = "The AAH user bound to this QR is no longer active.", code = "ANDROID_ENROLLMENT_USER_INACTIVE" }, statusCode: StatusCodes.Status403Forbidden);
        if (!user.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase) &&
            !_governance.HasPermission(user.WindowsUserName, GovernancePermissions.ReadData) &&
            !_governance.HasPermission(user.WindowsUserName, GovernancePermissions.UploadFiles) &&
            !_governance.HasPermission(user.WindowsUserName, GovernancePermissions.Import))
            return Results.Json(new { error = "This AAH user has no Android/network access permission.", code = "ANDROID_ENROLLMENT_PERMISSION_DENIED" }, statusCode: StatusCodes.Status403Forbidden);

        var rawToken = ServerSecurityRepository.NewToken();
        ServerDeviceRecord device;
        var registrationRecovered = false;
        try
        {
            var registration = _serverSecurity.RegisterOrRecoverAndroidDevice(
                string.IsNullOrWhiteSpace(request.DeviceName) ? "Android device" : request.DeviceName.Trim(),
                user.WindowsUserName,
                ServerSecurityRepository.HashOpaqueToken(rawToken),
                user.RoleName,
                request.AppVersion ?? string.Empty,
                request.DeviceBindingKey ?? string.Empty);
            device = registration.Device;
            registrationRecovered = registration.Recovered;
            _serverSecurity.SetDeviceLifecycleState(device.Id, ServerEnrollmentStates.Active, true);
            device.EnrollmentState = ServerEnrollmentStates.Active;
            _serverSecurity.TouchDevice(device.Id, request.AppVersion ?? string.Empty, validated: true);
            _serverSecurity.MarkMobileMfaVerified(device.Id);
            device.MobileMfaVerifiedUtc = DateTime.UtcNow;
        }
        catch (Exception exception)
        {
            SafeLogEvent("AndroidQrEnrollment", user.WindowsUserName, string.Empty, $"Registration failed before QR consumption: {exception.Message}", false);
            return Results.Json(new { error = "The Main Server could not register this Android device. The QR has been consumed for security. Generate a fresh Enrollment QR and retry.", code = "ANDROID_QR_ENROLLMENT_SERVER_ERROR" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        // Completion 20: Enrollment and login are deliberately separate states.
        // The first-time QR establishes device trust only; it must never create a user session.
        // After enrollment Android shows the normal User ID + Password login screen, while
        // a fresh one-time Login QR remains an optional secondary sign-in method.
        SafeLogEvent("AndroidQrEnrollment", user.WindowsUserName, device.DeviceId, registrationRecovered
            ? "One-time Android enrollment QR consumed; existing physical phone registration recovered with a rotated credential. Login is still required."
            : "One-time Android enrollment QR consumed; device enrolled. Login is still required.", true);
        return Results.Json(new
        {
            enrolled = true,
            deviceId = device.DeviceId,
            deviceToken = rawToken,
            userId = user.WindowsUserName,
            user = user.DisplayName,
            role = user.RoleName,
            serverFingerprint = GetServerCertificateQrPin(),
            qrConsumed = true,
            loginRequired = true,
            primaryLogin = "UserIdPassword",
            secondaryLogin = "OneTimeLoginQr",
            registrationRecovered
        });
    }

    private async Task<IResult> LoginAndroidWithQrAsync(HttpContext context)
    {
        AndroidQrLoginRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<AndroidQrLoginRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error = "Invalid Android QR login request.", code = "ANDROID_QR_LOGIN_INVALID" }); }
        if (request is null) return Results.BadRequest(new { error = "Android QR login request is required.", code = "ANDROID_QR_LOGIN_REQUIRED" });

        var device = AuthenticateDevice(request.DeviceId, request.DeviceToken, request.AppVersion ?? string.Empty, touch: false);
        if (device is null || !device.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = "This Android device is not enrolled or is no longer trusted. Generate a new enrollment QR.", code = "ANDROID_DEVICE_NOT_ENROLLED", reEnrollmentRequired = true }, statusCode: StatusCodes.Status401Unauthorized);
        var loginCode = request.Code?.Trim() ?? string.Empty;
        if (!_androidLoginQrs.TryRemove(loginCode, out var qr) || qr.ExpiresUtc <= DateTime.UtcNow)
            return Results.Json(new { error = "This Android login QR has expired or was already used. Generate a fresh one-time login QR.", code = "ANDROID_LOGIN_QR_EXPIRED" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!device.UserName.Equals(qr.BoundUserName, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = "This login QR is not valid for the user enrolled on this Android device.", code = "ANDROID_LOGIN_QR_USER_MISMATCH" }, statusCode: StatusCodes.Status403Forbidden);

        var user = GetApplicationUser(qr.BoundUserName);
        if (user is null || !user.IsActive)
            return Results.Json(new { error = "The AAH user bound to this login QR is no longer active.", code = "ANDROID_LOGIN_USER_INACTIVE" }, statusCode: StatusCodes.Status403Forbidden);

        string session;
        try
        {
            session = _serverSecurity.CreateSession(user.WindowsUserName, device.DeviceId, TimeSpan.FromHours(12));
            _serverSecurity.TouchDevice(device.Id, request.AppVersion ?? string.Empty, validated: true);
            _serverSecurity.MarkMobileMfaVerified(device.Id);
            device.MobileMfaVerifiedUtc = DateTime.UtcNow;
        }
        catch (Exception exception)
        {
            SafeLogEvent("AndroidQrLogin", user.WindowsUserName, device.DeviceId, $"Session creation failed before QR consumption: {exception.Message}", false);
            return Results.Json(new { error = "The Login QR has been consumed. Generate a fresh Login QR and retry.", code = "ANDROID_QR_LOGIN_SERVER_ERROR" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        SafeLogEvent("AndroidQrLogin", user.WindowsUserName, device.DeviceId, "One-time Android login QR consumed; signed in.", true);
        var created = _serverSecurity.FindSession(session);
        return Results.Json(new
        {
            sessionToken = session,
            expiresUtc = created?.ExpiresUtc ?? DateTime.UtcNow.AddHours(12),
            userId = user.WindowsUserName,
            user = user.DisplayName,
            role = user.RoleName,
            permissions = GetEffectivePermissions(user),
            qrConsumed = true,
            mfa = true,
            mfaMode = "OneTimeLoginQr"
        });
    }

    private async Task<IResult> UseAndroidUserQrAsync(HttpContext context)
    {
        AndroidUserQrRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<AndroidUserQrRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error = "Invalid mobile user QR request.", code = "ANDROID_USER_QR_INVALID" }); }
        if (request is null) return Results.BadRequest(new { error = "Mobile user QR request is required.", code = "ANDROID_USER_QR_REQUIRED" });
        if (request.RequireExistingDevice && (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.DeviceToken)))
            return Results.Json(new { error = "This TLS identity refresh requires the existing enrolled-phone credential. Unlock the phone and scan the current live user QR again.", code = "ANDROID_PIN_ROTATION_DEVICE_CREDENTIAL_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!TryConsumeAndroidUserQr(request.Code, out var qr))
            return Results.Json(new { error = "This user QR expired, was already used, or has rotated. Scan the current live code again.", code = "ANDROID_USER_QR_EXPIRED" }, statusCode: StatusCodes.Status401Unauthorized);

        var user = GetApplicationUser(qr.BoundUserName);
        if (user is null || !user.IsActive)
            return Results.Json(new { error = "The AAH user linked to this QR is disabled or no longer exists.", code = "ANDROID_USER_QR_USER_INACTIVE" }, statusCode: StatusCodes.Status403Forbidden);

        var suppliedDevice = string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.DeviceToken)
            ? null
            : AuthenticateDevice(request.DeviceId, request.DeviceToken, request.AppVersion ?? string.Empty, touch: false);
        if (request.RequireExistingDevice && suppliedDevice is null)
        {
            SafeLogEvent("AndroidTlsPinRotation", qr.BoundUserName, request.DeviceId ?? string.Empty, "Fresh QR reached this Main Server, but the supplied enrolled-device credential was not recognised; TLS pin was not authorised for replacement.", false);
            return Results.Json(new { error = "This Main Server does not recognise the existing phone enrollment, so AAH did not authorise a TLS pin replacement. Confirm that the QR is from the same Main Server and try again.", code = "ANDROID_PIN_ROTATION_DEVICE_NOT_RECOGNIZED", reEnrollmentRequired = false }, statusCode: StatusCodes.Status401Unauthorized);
        }
        if (suppliedDevice is not null)
        {
            if (!suppliedDevice.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "This mobile QR can be used only by the Android application.", code = "ANDROID_USER_QR_DEVICE_TYPE" }, statusCode: StatusCodes.Status403Forbidden);
            if (!suppliedDevice.UserName.Equals(user.WindowsUserName, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = $"This phone is enrolled to '{suppliedDevice.UserName}', but the scanned code belongs to '{user.WindowsUserName}'.", code = "ANDROID_USER_QR_USER_MISMATCH" }, statusCode: StatusCodes.Status403Forbidden);

            var sessionToken = _serverSecurity.CreateSession(user.WindowsUserName, suppliedDevice.DeviceId, TimeSpan.FromHours(12));
            _serverSecurity.TouchDevice(suppliedDevice.Id, request.AppVersion ?? string.Empty, validated: true);
            _serverSecurity.MarkMobileMfaVerified(suppliedDevice.Id);
            suppliedDevice.MobileMfaVerifiedUtc = DateTime.UtcNow;
            var created = _serverSecurity.FindSession(sessionToken);
            SafeLogEvent(request.RequireExistingDevice ? "AndroidTlsPinRotation" : "AndroidUserQrLogin", user.WindowsUserName, suppliedDevice.DeviceId,
                request.RequireExistingDevice ? "Live user QR verified the existing enrolled device and authorised the current Main Server TLS pin." : "Live user QR consumed; enrolled phone signed in automatically.", true);
            return Results.Json(new
            {
                mode = "login",
                authenticated = true,
                enrolled = true,
                sessionToken,
                expiresUtc = created?.ExpiresUtc ?? DateTime.UtcNow.AddHours(12),
                userId = user.WindowsUserName,
                user = user.DisplayName,
                role = user.RoleName,
                permissions = GetEffectivePermissions(user),
                serverFingerprint = GetServerCertificateQrPin(),
                pinRotationAccepted = request.RequireExistingDevice,
                credentialMode = request.RequireExistingDevice ? "ExistingDeviceQrTlsRefresh" : "ExistingDeviceQrLogin",
                qrConsumed = true,
                nextQrAfterUse = true
            });
        }

        var localOperator = GetApplicationUser(Environment.UserName);
        if (localOperator is null || !localOperator.IsActive || !localOperator.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = "A Main Server Administrator must be active on the Main Server to approve first-time phone enrollment.", code = "ANDROID_ENROLLMENT_ADMIN_REQUIRED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        var approvalHandler = AndroidEnrollmentApprovalRequested;
        if (approvalHandler is null)
            return Results.Json(new { error = "The Main Server approval UI is not ready. Open Accounting Automation Hub on the Main Server and scan the refreshed QR again.", code = "ANDROID_ENROLLMENT_APPROVAL_UI_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var approval = new AndroidEnrollmentApprovalEventArgs(
            Guid.NewGuid().ToString("N"),
            user.WindowsUserName,
            user.DisplayName,
            string.IsNullOrWhiteSpace(request.DeviceName) ? "Android device" : request.DeviceName.Trim(),
            context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            DateTime.UtcNow.AddSeconds(90));
        try { approvalHandler.Invoke(this, approval); }
        catch (Exception exception)
        {
            SafeLogEvent("AndroidEnrollmentApproval", user.WindowsUserName, string.Empty, "Could not show Main Server approval: " + exception.Message, false);
            return Results.Json(new { error = "The Main Server could not show the enrollment approval notification.", code = "ANDROID_ENROLLMENT_APPROVAL_UI_FAILED" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        var approved = await approval.WaitForDecisionAsync(context.RequestAborted).ConfigureAwait(false);
        if (!approved)
        {
            SafeLogEvent("AndroidEnrollmentApproval", user.WindowsUserName, string.Empty, $"First-time Android enrollment denied/timed out; device={approval.DeviceName}; request={approval.RequestId}", false);
            return Results.Json(new { error = "First-time phone enrollment was declined or the Main Server approval timed out. Scan the refreshed user QR and try again.", code = "ANDROID_ENROLLMENT_NOT_APPROVED" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var rawToken = ServerSecurityRepository.NewToken();
        ServerDeviceRecord device;
        var registrationRecovered = false;
        try
        {
            var registration = _serverSecurity.RegisterOrRecoverAndroidDevice(
                approval.DeviceName,
                user.WindowsUserName,
                ServerSecurityRepository.HashOpaqueToken(rawToken),
                user.RoleName,
                request.AppVersion ?? string.Empty,
                request.DeviceBindingKey ?? string.Empty);
            device = registration.Device;
            registrationRecovered = registration.Recovered;
            _serverSecurity.SetDeviceLifecycleState(device.Id, ServerEnrollmentStates.Active, true);
            device.EnrollmentState = ServerEnrollmentStates.Active;
            _serverSecurity.TouchDevice(device.Id, request.AppVersion ?? string.Empty, validated: true);
            _serverSecurity.MarkMobileMfaVerified(device.Id);
            device.MobileMfaVerifiedUtc = DateTime.UtcNow;
        }
        catch (Exception exception)
        {
            SafeLogEvent("AndroidUserQrEnrollment", user.WindowsUserName, string.Empty, "Registration failed after approval: " + exception.Message, false);
            return Results.Json(new { error = "Enrollment was approved, but the Main Server could not register this phone. Scan the refreshed QR and retry.", code = "ANDROID_USER_QR_ENROLLMENT_SERVER_ERROR" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        // The live QR is the one-time authentication grant. Never embed or recover the user's
        // plaintext password in the QR. After explicit Main Server Administrator approval, first-time
        // enrollment completes the same authenticated session atomically so Android does not need a
        // second password step. The consumed QR rotates immediately and cannot be replayed.
        var enrollmentSessionToken = _serverSecurity.CreateSession(user.WindowsUserName, device.DeviceId, TimeSpan.FromHours(12));
        var enrollmentSession = _serverSecurity.FindSession(enrollmentSessionToken);
        SafeLogEvent("AndroidUserQrEnrollment", user.WindowsUserName, device.DeviceId, registrationRecovered
            ? "Live user QR consumed; Main Server Administrator approved recovery of the same physical phone registration, rotated its credential and signed in without creating a duplicate device row."
            : "Live user QR consumed; Main Server Administrator approved first-time phone enrollment and one-time authenticated login.", true);
        return Results.Json(new
        {
            mode = "login",
            authenticated = true,
            enrolled = true,
            deviceId = device.DeviceId,
            deviceToken = rawToken,
            sessionToken = enrollmentSessionToken,
            expiresUtc = enrollmentSession?.ExpiresUtc ?? DateTime.UtcNow.AddHours(12),
            userId = user.WindowsUserName,
            user = user.DisplayName,
            role = user.RoleName,
            permissions = GetEffectivePermissions(user),
            serverFingerprint = GetServerCertificateQrPin(),
            loginRequired = false,
            qrConsumed = true,
            nextQrAfterUse = true,
            credentialMode = registrationRecovered ? "RecoveredPhysicalDeviceQrGrant" : "OneTimeQrGrant",
            registrationRecovered
        });
    }

    private IResult GetCurrentUserMobileCode(HttpContext context)
    {
        var session = AuthenticateSession(context, out var user, out var device);
        if (session is null || user is null || device is null) return Results.Unauthorized();
        var qr = EnsureAndroidUserQrCode(user.WindowsUserName);
        return Results.Json(new { code = qr.Code, expiresUtc = qr.ExpiresUtc, userId = qr.BoundUserName, payload = qr.Payload, purpose = qr.Purpose });
    }

    private IResult RefreshCurrentUserMobileCode(HttpContext context)
    {
        // Only the authenticated Windows session can rotate its own QR.
        var session = AuthenticateSession(context, out var user, out var device);
        if (session is null || user is null || device is null) return Results.Unauthorized();
        var qr = EnsureAndroidUserQrCode(user.WindowsUserName, forceRotate: true);
        return Results.Json(new { code = qr.Code, expiresUtc = qr.ExpiresUtc, userId = qr.BoundUserName, payload = qr.Payload, purpose = qr.Purpose });
    }

    private IResult GetAndroidAdminUsers(HttpContext context)
    {
        var session = AuthenticateSession(context, out var currentUser, out var device);
        if (session is null || currentUser is null || device is null) return Results.Unauthorized();
        if (!device.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) ||
            !currentUser.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return Results.Json(_governance.GetUsers().Select(x => new
        {
            userId = x.WindowsUserName,
            displayName = x.DisplayName,
            role = x.RoleName,
            active = x.IsActive,
            lastSeenUtc = x.LastSeenUtc,
            mobileVerificationIntervalDays = Math.Clamp(x.MobileVerificationIntervalDays <= 0 ? 30 : x.MobileVerificationIntervalDays, 1, 365)
        }));
    }

    private async Task<IResult> UpdateAndroidAdminUserAsync(HttpContext context, string userName)
    {
        var session = AuthenticateSession(context, out var currentUser, out var device);
        if (session is null || currentUser is null || device is null) return Results.Unauthorized();
        if (!device.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) ||
            !currentUser.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        AndroidAdminUserUpdateRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<AndroidAdminUserUpdateRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error = "Invalid user edit request." }); }
        if (request is null) return Results.BadRequest(new { error = "User edit request is required." });

        var target = GetApplicationUser(userName);
        if (target is null) return Results.NotFound(new { error = "The selected AAH user no longer exists." });
        var requestedRole = (request.RoleName ?? target.RoleName).Trim();
        if (!GovernanceRoles.All.Contains(requestedRole, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Select a valid AAH role." });
        if (!target.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase) && requestedRole.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Android user editing cannot promote another account to Administrator. Use Main Server Administration for that change." });

        if (target.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
        {
            requestedRole = GovernanceRoles.Administrator;
            request.IsActive = true;
        }

        target.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? target.WindowsUserName : request.DisplayName.Trim();
        target.RoleName = requestedRole;
        target.IsActive = request.IsActive;
        target.MobileVerificationIntervalDays = Math.Clamp(request.MobileVerificationIntervalDays <= 0 ? (target.MobileVerificationIntervalDays <= 0 ? 30 : target.MobileVerificationIntervalDays) : request.MobileVerificationIntervalDays, 1, 365);
        target.UpdatedUtc = DateTime.UtcNow;
        _governance.SaveUser(target);
        if (!target.WindowsUserName.Equals(currentUser.WindowsUserName, StringComparison.OrdinalIgnoreCase)) _serverSecurity.DeleteSessionsForUser(target.WindowsUserName);
        _enterpriseControl.BumpRevision("authorization");
        SafeLogEvent("AndroidAdminUserEdit", currentUser.WindowsUserName, device.DeviceId, $"Edited {target.WindowsUserName}; role={target.RoleName}; active={target.IsActive}; mobileVerificationIntervalDays={target.MobileVerificationIntervalDays}; other sessions revoked", true);
        return Results.Json(new { saved = true, userId = target.WindowsUserName, displayName = target.DisplayName, role = target.RoleName, active = target.IsActive, mobileVerificationIntervalDays = target.MobileVerificationIntervalDays });
    }

    private async Task<IResult> EnrollDeviceAsync(HttpContext context)
    {
        EnrollmentRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<EnrollmentRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error = "Invalid enrollment request.", code = "INVALID_ENROLLMENT_REQUEST" }); }
        if (request is null) return Results.BadRequest(new { error = "Enrollment request is required.", code = "ENROLLMENT_REQUEST_REQUIRED" });

        PairingWindow? pairing; lock (_gate) pairing = _pairing;
        if (pairing is null || pairing.ExpiresUtc <= DateTime.UtcNow)
            return Results.Json(new { error = "The enrollment code has expired or there is no active enrollment code. Create a new code on the Main Server.", code = "ENROLLMENT_CODE_EXPIRED" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!FixedEquals(pairing.Code, request.EnrollmentCode))
            return Results.Json(new { error = "The enrollment code is incorrect. Use the current code shown by the Main Server.", code = "ENROLLMENT_CODE_INCORRECT" }, statusCode: StatusCodes.Status401Unauthorized);
        var requestedDeviceType = request.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) ? ServerDeviceTypes.Android : ServerDeviceTypes.Windows;
        if (!pairing.DeviceType.Equals(requestedDeviceType, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = $"This enrollment grant is for {pairing.DeviceType}, not {requestedDeviceType}.", code = "ENROLLMENT_DEVICE_TYPE_MISMATCH" }, statusCode: StatusCodes.Status403Forbidden);
        if (pairing.BoundUserName.Length > 0 && !pairing.BoundUserName.Equals(request.UserName?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = $"This enrollment grant is bound to User ID '{pairing.BoundUserName}'. Sign in with that user or create a new QR/code for another user.", code = "ENROLLMENT_USER_MISMATCH" }, statusCode: StatusCodes.Status403Forbidden);

        var deviceType = requestedDeviceType;
        NetworkIdentityService.AuthenticationResult auth;
        try { auth = _identity!.AuthenticatePassword(request.UserName, request.Password); }
        catch (Exception exception)
        {
            SafeLogEvent("DeviceEnrollment", request.UserName, string.Empty, $"Credential validation failed before pairing code consumption: {exception.Message}", false);
            return Results.Json(new { error = "The Main Server could not validate the AAH User ID/password. The enrollment code was not consumed; retry after the server is ready.", code = "ENROLLMENT_AUTH_SERVER_ERROR" }, statusCode: StatusCodes.Status500InternalServerError);
        }
        if (!auth.Success || auth.User is null)
            return Results.Json(new { error = "AAH User ID or password is incorrect.", code = "ENROLLMENT_CREDENTIALS_REJECTED" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!auth.User.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase) &&
            !_governance.HasPermission(auth.User.WindowsUserName, GovernancePermissions.ReadData) &&
            !_governance.HasPermission(auth.User.WindowsUserName, GovernancePermissions.UploadFiles) &&
            !_governance.HasPermission(auth.User.WindowsUserName, GovernancePermissions.Import))
            return Results.Json(new { error = "This AAH user has no network access permission.", code = "ENROLLMENT_PERMISSION_DENIED" }, statusCode: StatusCodes.Status403Forbidden);

        var rawToken = ServerSecurityRepository.NewToken();
        ServerDeviceRecord device;
        try
        {
            Exception? last = null;
            device = null!;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    // Keep the legacy pairing-code path in its historical Verification Required state.
                    // Android sends the binding here so a second same-phone legacy registration fails the
                    // unique binding barrier rather than creating another row; exact credential recovery is
                    // performed only by the live QR + Administrator-approved Android recovery paths above.
                    device = _serverSecurity.RegisterDevice(
                        request.DeviceName,
                        deviceType,
                        auth.User.WindowsUserName,
                        ServerSecurityRepository.HashOpaqueToken(rawToken),
                        auth.User.RoleName,
                        0,
                        string.Empty,
                        request.AppVersion,
                        deviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) ? request.DeviceBindingKey : string.Empty);
                    last = null;
                    break;
                }
                catch (Exception exception)
                {
                    last = exception;
                    if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), context.RequestAborted).ConfigureAwait(false);
                }
            }
            if (last is not null) throw last;
        }
        catch (Exception exception)
        {
            SafeLogEvent("DeviceEnrollment", auth.User.WindowsUserName, string.Empty, $"Registration failed before pairing code consumption: {exception.Message}", false);
            return Results.Json(new { error = "The Main Server could not register this PC. The pairing code was NOT consumed; retry the same code. If it repeats, open Security Diagnostics on the Main Server.", code = "ENROLLMENT_SERVER_ERROR" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        // Audit is deliberately non-fatal. A logging failure must never convert a successful
        // device registration into HTTP 500 after consuming the user's one-use enrollment code.
        SafeLogEvent("DeviceEnrollment", auth.User.WindowsUserName, device.DeviceId, $"{device.DeviceType}; system enrolled; client access controlled by Client Directory", true);
        lock (_gate)
        {
            // Consume only the exact pairing window that authorised this successful registration.
            if (ReferenceEquals(_pairing, pairing)) _pairing = null;
        }
        var expiryDays = GetDeviceExpiryDays(device.DeviceType);
        return Results.Json(new
        {
            deviceId=device.DeviceId, deviceToken=rawToken, deviceType=device.DeviceType, role=device.RoleName,
            initialClientId=0, financialYear=string.Empty, expiresAfterInactiveDays=expiryDays,
            mfaRequired=true, mfaMode=device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)?"RegistrationOtpOnce":"MainServerChallengeOtp", serverFingerprint=GetServerCaFingerprint(),
            note="System enrolled. Complete the one-time registration OTP now. After registration verification, normal LAN sign-in uses the enrolled device plus User ID/password and does not request OTP again unless this PC is inactive for 30 days or an Administrator forces registration verification again."
        });
    }

    private async Task<IResult> CreateLoginChallengeAsync(HttpContext context)
    {
        LoginChallengeRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<LoginChallengeRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error = "Invalid login request." }); }
        if (request is null) return Results.BadRequest(new { error = "Login request is required." });
        var route = SecurityNetworkPolicy.ClassifyNetworkRoute(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
        if (route.IsBlocked)
        {
            SafeLogEvent("NetworkMfaChallenge", request.UserName, request.DeviceId, $"OTP request rejected: public/unknown network route is not allowed. Remote={route.RemoteAddress}; Local={route.LocalAddress}", false);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        var otpLabel = SecurityNetworkPolicy.GetOtpLabel(route.Scope);
        var routeDetail = $"{otpLabel}; Remote={route.RemoteAddress}; Local={route.LocalAddress}";
        var device = AuthenticateDevice(request.DeviceId, request.DeviceToken, request.AppVersion, touch:false);
        if (device is null)
        {
            SafeLogEvent("NetworkMfaChallenge", request.UserName, request.DeviceId, $"OTP request rejected: this PC is not enrolled/active on this Main Server, or its device credential is invalid. {routeDetail}", false);
            return DeviceRegistrationFailure(request.DeviceId);
        }
        if (device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase) && device.RegistrationOtpVerifiedUtc.HasValue)
        {
            SafeLogEvent("NetworkRegistrationOtp", request.UserName, device.DeviceId, "Registration OTP request ignored because this enrolled PC is already verified. Normal sign-in does not use OTP.", true);
            return Results.Json(new { error = "This PC has already completed its one-time registration OTP. Use normal Open AAH sign-in; OTP is not used for routine login.", code = "REGISTRATION_OTP_ALREADY_VERIFIED" }, statusCode: StatusCodes.Status409Conflict);
        }
        if (!device.UserName.Equals(request.UserName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            SafeLogEvent("NetworkMfaChallenge", request.UserName, device.DeviceId, $"OTP request rejected: this enrolled PC belongs to AAH user '{device.UserName}'. {routeDetail}", false);
            return Results.Unauthorized();
        }
        var policy = GetWindowsUpdatePolicy(device, request.AppVersion);
        if (policy.ForceUpdate)
        {
            SafeLogEvent("NetworkMfaChallenge", request.UserName, device.DeviceId, $"OTP request rejected: required LAN application update {policy.ForcedVersion} must be installed first. {routeDetail}", false);
            return Results.Json(new { error = "A Main Server administrator requires this LAN application to update before login.", policy.ApprovedVersion, policy.ApprovedSha256, forceUpdate = true }, statusCode: StatusCodes.Status426UpgradeRequired);
        }
        var auth = _identity!.AuthenticatePassword(request.UserName, request.Password);
        if (!auth.Success || auth.User is null)
        {
            SafeLogEvent("NetworkMfaChallenge", request.UserName, device.DeviceId, $"OTP request rejected: AAH User ID/password was not accepted. {routeDetail}", false);
            return Results.Unauthorized();
        }

        var challengeId = Guid.NewGuid().ToString("N");
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var challenge = new LoginChallenge(challengeId, auth.User.WindowsUserName, device.DeviceId, device.DeviceName, route.Scope, code, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5), false);
        try
        {
            _serverSecurity.SaveLoginChallenge(new NetworkLoginChallengeRecord
            {
                ChallengeId = challenge.ChallengeId, UserName = challenge.UserName, DeviceId = challenge.DeviceId, DeviceName = challenge.DeviceName, NetworkScope = challenge.NetworkScope, Code = challenge.Code,
                CreatedUtc = challenge.CreatedUtc, ExpiresUtc = challenge.ExpiresUtc, IsActive = true
            });
        }
        catch (Exception exception)
        {
            SafeLogEvent("NetworkMfaChallenge", auth.User.WindowsUserName, device.DeviceId, $"OTP challenge persistence failed: {exception.Message}", false);
            return Results.Json(new { error = "The Main Server could not store the OTP challenge. No OTP was consumed; request a new OTP after the server is ready.", code = "OTP_CHALLENGE_STORE_FAILED" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        _loginChallenges[challengeId] = challenge;
        foreach (var stale in _loginChallenges.Values.Where(x => x.ExpiresUtc <= DateTime.UtcNow || x.Used).ToArray()) _loginChallenges.TryRemove(stale.ChallengeId, out _);
        var isWindowsRegistrationOtp = device.DeviceType.Equals(ServerDeviceTypes.Windows, StringComparison.OrdinalIgnoreCase);
        var challengePurpose = isWindowsRegistrationOtp ? "Registration OTP" : "Mobile Login Code";
        var challengeMessage = isWindowsRegistrationOtp
            ? $"One-time registration {otpLabel} sent to the Accounting Automation Hub Main Server application. This OTP is only for Windows device registration verification, not routine LAN login."
            : $"6-digit Android mobile login code sent to the Accounting Automation Hub Main Server notification center. Enter it on the enrolled phone to complete sign-in.";
        SafeLogEvent(isWindowsRegistrationOtp ? "NetworkRegistrationOtp" : "NetworkMfaChallenge", auth.User.WindowsUserName, device.DeviceId, $"{challengePurpose} generated and queued for Administrator notification; Remote={route.RemoteAddress}; Local={route.LocalAddress}; expires {challenge.ExpiresUtc:o}", true);
        try { MfaChallengeCreated?.Invoke(this, new NetworkMfaChallengeEventArgs(challengeId, auth.User.WindowsUserName, device.DeviceName, device.DeviceType, route.Scope, code, challenge.ExpiresUtc)); }
        catch (Exception exception) { SafeLogEvent("NetworkMfaChallenge", auth.User.WindowsUserName, device.DeviceId, $"OTP generated but Admin notification event failed: {exception.Message}", false); }
        return Results.Json(new { challengeId, expiresUtc = challenge.ExpiresUtc, networkScope = route.Scope, otpType = challengePurpose, detectedRemoteAddress = route.RemoteAddress, message = challengeMessage });
    }

    private async Task<IResult> LoginAsync(HttpContext context)
    {
        ChallengeLoginRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<ChallengeLoginRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error="Invalid login request." }); }
        if (request is null) return Results.BadRequest(new { error="Login request is required." });
        var device = AuthenticateDevice(request.DeviceId, request.DeviceToken, request.AppVersion, touch:false);
        if (device is null) return DeviceRegistrationFailure(request.DeviceId);
        var policy = GetWindowsUpdatePolicy(device, request.AppVersion);
        if (policy.ForceUpdate) return Results.Json(new { error = "A Main Server administrator requires this LAN application to update before login.", policy.ApprovedVersion, policy.ApprovedSha256, forceUpdate = true }, statusCode: StatusCodes.Status426UpgradeRequired);
        if (!_loginChallenges.TryGetValue(request.ChallengeId.Trim(), out var challenge))
        {
            var persistedChallenge = _serverSecurity.FindLoginChallenge(request.ChallengeId.Trim());
            if (persistedChallenge is null) return Results.Unauthorized();
            challenge = new LoginChallenge(persistedChallenge.ChallengeId, persistedChallenge.UserName, persistedChallenge.DeviceId, persistedChallenge.DeviceName, persistedChallenge.NetworkScope, persistedChallenge.Code, persistedChallenge.CreatedUtc, persistedChallenge.ExpiresUtc, !persistedChallenge.IsActive || persistedChallenge.UsedUtc.HasValue);
            _loginChallenges[challenge.ChallengeId] = challenge;
        }
        if (challenge.Used || challenge.ExpiresUtc <= DateTime.UtcNow || !challenge.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
        var loginRoute = SecurityNetworkPolicy.ClassifyNetworkRoute(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
        if (loginRoute.IsBlocked || !challenge.NetworkScope.Equals(loginRoute.Scope, StringComparison.OrdinalIgnoreCase))
        {
            SafeLogEvent("NetworkLogin", challenge.UserName, device.DeviceId, $"OTP route mismatch/rejected. Challenge={challenge.NetworkScope}; Current={loginRoute.Scope}; Remote={loginRoute.RemoteAddress}", false);
            return Results.Unauthorized();
        }
        if (!FixedEquals(challenge.Code, request.OtpCode)) return Results.Unauthorized();
        var user = GetApplicationUser(challenge.UserName);
        if (user is null || !user.IsActive || !user.WindowsUserName.Equals(device.UserName, StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
        string session;
        try
        {
            // Session creation and one-use OTP consumption are one atomic database transaction.
            // A transient 500 therefore leaves the OTP challenge retryable instead of causing a misleading 401 on the next click.
            var registrationSessionLifetime = device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromDays(30) : TimeSpan.FromHours(12);
            session = _serverSecurity.CreateSessionAndConsumeLoginChallenge(challenge.ChallengeId, user.WindowsUserName, device.DeviceId, registrationSessionLifetime);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("no longer active", StringComparison.OrdinalIgnoreCase))
        {
            SafeLogEvent("NetworkLogin", user.WindowsUserName, device.DeviceId, "OTP challenge was already consumed by another login attempt.", false);
            return Results.Unauthorized();
        }
        catch (Exception exception)
        {
            SafeLogEvent("NetworkLogin", user.WindowsUserName, device.DeviceId, $"Session creation failed before OTP consumption: {exception.Message}", false);
            return Results.Json(new { error = "The Main Server authenticated the OTP but could not create the LAN session. The OTP was not consumed; retry Open AAH. If this repeats, check Main Server Security Diagnostics.", code = "LOGIN_SESSION_CREATE_FAILED" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        _loginChallenges[challenge.ChallengeId] = challenge with { Used = true };
        if(device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase))
        {
            try { _serverSecurity.MarkRegistrationOtpVerified(device.Id); }
            catch (Exception exception)
            {
                _serverSecurity.DeleteSession(ServerSecurityRepository.HashOpaqueToken(session));
                SafeLogEvent("NetworkRegistrationOtp", user.WindowsUserName, device.DeviceId, $"Registration OTP was correct but trusted-device verification could not be persisted: {exception.Message}", false);
                return Results.Json(new { error = "The OTP was correct, but the Main Server could not finish registration verification. Retry with a new registration OTP.", code = "REGISTRATION_OTP_VERIFY_STORE_FAILED" }, statusCode: StatusCodes.Status500InternalServerError);
            }
        }
        else if(device.DeviceType.Equals(ServerDeviceTypes.Android,StringComparison.OrdinalIgnoreCase))
        {
            try { _serverSecurity.MarkMobileMfaVerified(device.Id); device.MobileMfaVerifiedUtc = DateTime.UtcNow; }
            catch (Exception exception)
            {
                _serverSecurity.DeleteSession(ServerSecurityRepository.HashOpaqueToken(session));
                SafeLogEvent("AndroidPeriodicMfa", user.WindowsUserName, device.DeviceId, $"Mobile Login Code was correct but periodic verification could not be persisted: {exception.Message}", false);
                return Results.Json(new { error = "The Mobile Login Code was correct, but AAH could not save the monthly verification. Retry with a new code.", code = "MOBILE_PERIODIC_VERIFY_STORE_FAILED" }, statusCode: StatusCodes.Status500InternalServerError);
            }
        }
        try { _serverSecurity.TouchDevice(device.Id, request.AppVersion, validated: true); }
        catch (Exception exception) { SafeLogEvent("NetworkLogin", user.WindowsUserName, device.DeviceId, $"Login succeeded but device lease touch failed: {exception.Message}", false); }
        if(device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase))
            SafeLogEvent("NetworkRegistrationOtp", user.WindowsUserName, device.DeviceId, $"One-time registration {SecurityNetworkPolicy.GetOtpLabel(challenge.NetworkScope)} verified successfully. Routine LAN sign-in no longer requires OTP.", true);
        else
            SafeLogEvent("NetworkLogin", user.WindowsUserName, device.DeviceId, $"{SecurityNetworkPolicy.GetOtpLabel(challenge.NetworkScope)} authentication successful.", true);
        var createdSession = _serverSecurity.FindSession(session);
        var expiresUtc = createdSession?.ExpiresUtc ?? (device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)?DateTime.UtcNow.AddDays(30):DateTime.UtcNow.AddHours(12));
        return Results.Json(new{sessionToken=session,expiresInHours=device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)?720:12,expiresUtc,userId=user.WindowsUserName,user=user.DisplayName,role=user.RoleName,permissions=GetEffectivePermissions(user),mfa=true,mfaMode=device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)?"RegistrationOtpOnce":"MainServerChallengeOtp",deviceLeaseDays=GetDeviceExpiryDays(device.DeviceType)});
    }

    // R46 R15: after Android has completed its local phone-security gate, an existing enrolled
    // device may resume its bound AAH user without re-sending the account password. The durable
    // device token and the Main Server periodic-mobile-verification lease remain mandatory.
    private async Task<IResult> ResumeAndroidDeviceSessionAsync(HttpContext context)
    {
        AndroidDeviceResumeRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<AndroidDeviceResumeRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error = "Invalid verified-phone resume request.", code = "ANDROID_DEVICE_RESUME_INVALID" }); }
        if (request is null) return Results.BadRequest(new { error = "Verified-phone resume request is required.", code = "ANDROID_DEVICE_RESUME_REQUIRED" });

        var route = SecurityNetworkPolicy.ClassifyNetworkRoute(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
        if (route.IsBlocked) return Results.Json(new { error = "This network route is not allowed.", code = "ANDROID_DEVICE_RESUME_ROUTE_BLOCKED" }, statusCode: StatusCodes.Status403Forbidden);

        var device = AuthenticateDevice(request.DeviceId, request.DeviceToken, request.AppVersion, touch: false);
        if (device is null) return DeviceRegistrationFailure(request.DeviceId);
        if (!device.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = "Verified-phone resume is available only to an enrolled Android device.", code = "ANDROID_DEVICE_RESUME_DEVICE_TYPE" }, statusCode: StatusCodes.Status403Forbidden);

        var user = GetApplicationUser(device.UserName);
        if (user is null || !user.IsActive)
            return Results.Json(new { error = "The AAH user linked to this phone is disabled or no longer exists.", code = "ANDROID_DEVICE_RESUME_USER_INACTIVE" }, statusCode: StatusCodes.Status403Forbidden);

        var policy = GetWindowsUpdatePolicy(device, request.AppVersion);
        if (policy.ForceUpdate)
            return Results.Json(new { error = "A Main Server administrator requires this Android application to update before login.", policy.ApprovedVersion, policy.ApprovedSha256, forceUpdate = true }, statusCode: StatusCodes.Status426UpgradeRequired);

        var intervalDays = Math.Clamp(user.MobileVerificationIntervalDays <= 0 ? 30 : user.MobileVerificationIntervalDays, 1, 365);
        var verifiedUtc = device.MobileMfaVerifiedUtc;
        var nextVerificationUtc = verifiedUtc?.AddDays(intervalDays);
        if (!verifiedUtc.HasValue || !nextVerificationUtc.HasValue || nextVerificationUtc.Value <= DateTime.UtcNow)
        {
            SafeLogEvent("AndroidVerifiedPhoneResume", user.WindowsUserName, device.DeviceId, $"Verified phone resume requires periodic mobile verification. Interval={intervalDays} day(s); Last={verifiedUtc:o}", true);
            return Results.Json(new
            {
                error = $"Your AAH Mobile Access QR / Mobile Login Code verification is due. This user is configured for verification every {intervalDays} day(s).",
                code = "MOBILE_PERIODIC_VERIFICATION_REQUIRED",
                verificationRequired = true,
                mobileVerificationIntervalDays = intervalDays,
                lastVerifiedUtc = verifiedUtc,
                nextVerificationUtc
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        var session = _serverSecurity.CreateSession(user.WindowsUserName, device.DeviceId, TimeSpan.FromHours(12));
        _serverSecurity.TouchDevice(device.Id, request.AppVersion, validated: true);
        var created = _serverSecurity.FindSession(session);
        SafeLogEvent("AndroidVerifiedPhoneResume", user.WindowsUserName, device.DeviceId, $"Verified enrolled phone resumed automatically. Next periodic verification={nextVerificationUtc:o}", true);
        return Results.Json(new
        {
            sessionToken = session,
            expiresInHours = 12,
            expiresUtc = created?.ExpiresUtc ?? DateTime.UtcNow.AddHours(12),
            userId = user.WindowsUserName,
            user = user.DisplayName,
            role = user.RoleName,
            permissions = GetEffectivePermissions(user),
            mfa = true,
            mfaMode = "VerifiedEnrolledPhone",
            mobileVerificationIntervalDays = intervalDays,
            lastVerifiedUtc = verifiedUtc,
            nextVerificationUtc,
            deviceLeaseDays = GetDeviceExpiryDays(device.DeviceType)
        });
    }

    private async Task<IResult> PasswordPreflightAsync(HttpContext context)
    {
        LoginChallengeRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<LoginChallengeRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { valid = false, error = "Invalid password verification request." }); }
        if (request is null) return Results.BadRequest(new { valid = false, error = "Password verification request is required." });

        var route = SecurityNetworkPolicy.ClassifyNetworkRoute(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
        if (route.IsBlocked) return Results.Json(new { valid = false, error = "This network route is not allowed." }, statusCode: StatusCodes.Status403Forbidden);

        var device = AuthenticateDevice(request.DeviceId, request.DeviceToken, request.AppVersion, touch:false);
        if (device is null) return DeviceRegistrationFailure(request.DeviceId);
        if (!device.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { valid = false, error = "Password preflight is available only to an enrolled Android device." }, statusCode: StatusCodes.Status403Forbidden);
        if (!device.UserName.Equals(request.UserName.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { valid = false, error = "The saved AAH user does not match this enrolled phone." }, statusCode: StatusCodes.Status401Unauthorized);

        var auth = _identity!.AuthenticatePassword(request.UserName, request.Password);
        if (!auth.Success || auth.User is null || !auth.User.IsActive)
        {
            SafeLogEvent("AndroidPasswordPreflight", request.UserName, device.DeviceId, "Saved password preflight was not accepted.", false);
            // The phone is already enrolled to this user, so an explicit valid=false is safe and
            // lets Android discard only the stale saved password instead of breaking enrollment.
            return Results.Json(new { valid = false, error = "Saved AAH password is no longer valid." });
        }

        var intervalDays = Math.Clamp(auth.User.MobileVerificationIntervalDays <= 0 ? 30 : auth.User.MobileVerificationIntervalDays, 1, 365);
        var lastVerifiedUtc = device.MobileMfaVerifiedUtc;
        var nextVerificationUtc = lastVerifiedUtc?.AddDays(intervalDays);
        var verificationRequired = !lastVerifiedUtc.HasValue || !nextVerificationUtc.HasValue || nextVerificationUtc.Value <= DateTime.UtcNow;
        SafeLogEvent("AndroidPasswordPreflight", auth.User.WindowsUserName, device.DeviceId,
            verificationRequired ? "Saved password verified; periodic mobile verification is due." : "Saved password verified in background for direct sign-in.", true);
        return Results.Json(new
        {
            valid = true,
            verificationRequired,
            mobileVerificationIntervalDays = intervalDays,
            lastVerifiedUtc,
            nextVerificationUtc
        });
    }

    private async Task<IResult> TrustedLoginAsync(HttpContext context)
    {
        LoginChallengeRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<LoginChallengeRequest>(context.RequestAborted).ConfigureAwait(false); }
        catch { return Results.BadRequest(new { error = "Invalid login request." }); }
        if (request is null) return Results.BadRequest(new { error = "Login request is required." });
        var route = SecurityNetworkPolicy.ClassifyNetworkRoute(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
        if (route.IsBlocked) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var device = AuthenticateDevice(request.DeviceId, request.DeviceToken, request.AppVersion, touch:false);
        if (device is null) return DeviceRegistrationFailure(request.DeviceId);
        if (!device.UserName.Equals(request.UserName.Trim(), StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
        var policy = GetWindowsUpdatePolicy(device, request.AppVersion);
        if (policy.ForceUpdate) return Results.Json(new { error = "A Main Server administrator requires this LAN application to update before login.", policy.ApprovedVersion, policy.ApprovedSha256, forceUpdate = true }, statusCode: StatusCodes.Status426UpgradeRequired);
        var auth = _identity!.AuthenticatePassword(request.UserName, request.Password);
        if (!auth.Success || auth.User is null)
        {
            SafeLogEvent("NetworkLogin", request.UserName, device.DeviceId, "Trusted-device AAH User ID/password was not accepted.", false);
            return Results.Unauthorized();
        }

        if (device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase))
        {
            if (!device.RegistrationOtpVerifiedUtc.HasValue)
                return Results.Json(new { error = "This enrolled PC requires its one-time registration OTP before routine login can continue.", code = "REGISTRATION_OTP_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            var session = _serverSecurity.CreateSession(auth.User.WindowsUserName, device.DeviceId, TimeSpan.FromDays(30));
            _serverSecurity.TouchDevice(device.Id, request.AppVersion, validated:true);
            SafeLogEvent("NetworkLogin", auth.User.WindowsUserName, device.DeviceId, "Trusted registered-device login successful; no OTP used.", true);
            var created = _serverSecurity.FindSession(session);
            var expiresUtc = created?.ExpiresUtc ?? DateTime.UtcNow.AddDays(30);
            return Results.Json(new{sessionToken=session,expiresInHours=720,expiresUtc,userId=auth.User.WindowsUserName,user=auth.User.DisplayName,role=auth.User.RoleName,permissions=GetEffectivePermissions(auth.User),mfa=true,mfaMode="RegistrationOtpOnce",deviceLeaseDays=30});
        }

        if (!device.DeviceType.Equals(ServerDeviceTypes.Android,StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(StatusCodes.Status404NotFound);
        var intervalDays = Math.Clamp(auth.User.MobileVerificationIntervalDays <= 0 ? 30 : auth.User.MobileVerificationIntervalDays, 1, 365);
        var verifiedUtc = device.MobileMfaVerifiedUtc;
        var nextVerificationUtc = verifiedUtc?.AddDays(intervalDays);
        if (!verifiedUtc.HasValue || !nextVerificationUtc.HasValue || nextVerificationUtc.Value <= DateTime.UtcNow)
        {
            SafeLogEvent("AndroidPeriodicMfa", auth.User.WindowsUserName, device.DeviceId, $"Password accepted; periodic mobile verification required. Interval={intervalDays} day(s); Last={verifiedUtc:o}", true);
            return Results.Json(new
            {
                error = $"Your AAH Mobile Login Code / QR verification is due. This user is configured for verification every {intervalDays} day(s).",
                code = "MOBILE_PERIODIC_VERIFICATION_REQUIRED",
                verificationRequired = true,
                mobileVerificationIntervalDays = intervalDays,
                lastVerifiedUtc = verifiedUtc,
                nextVerificationUtc
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        var mobileSession = _serverSecurity.CreateSession(auth.User.WindowsUserName, device.DeviceId, TimeSpan.FromHours(12));
        _serverSecurity.TouchDevice(device.Id, request.AppVersion, validated:true);
        SafeLogEvent("AndroidPeriodicMfa", auth.User.WindowsUserName, device.DeviceId, $"Password login accepted under current periodic mobile verification. Next verification={nextVerificationUtc:o}", true);
        var mobileCreated = _serverSecurity.FindSession(mobileSession);
        return Results.Json(new
        {
            sessionToken = mobileSession,
            expiresInHours = 12,
            expiresUtc = mobileCreated?.ExpiresUtc ?? DateTime.UtcNow.AddHours(12),
            userId = auth.User.WindowsUserName,
            user = auth.User.DisplayName,
            role = auth.User.RoleName,
            permissions = GetEffectivePermissions(auth.User),
            mfa = true,
            mfaMode = "PeriodicMobileVerification",
            mobileVerificationIntervalDays = intervalDays,
            lastVerifiedUtc = verifiedUtc,
            nextVerificationUtc,
            deviceLeaseDays = GetDeviceExpiryDays(device.DeviceType)
        });
    }

    private IResult GetSessionIdentity(HttpContext context)
    {
        var session = AuthenticateSession(context, out var user, out var device);
        if (session is null || user is null || device is null) return Results.Unauthorized();
        return Results.Json(new
        {
            active = true,
            userId = user.WindowsUserName,
            user = user.DisplayName,
            role = user.RoleName,
            permissions = GetEffectivePermissions(user),
            expiresUtc = session.ExpiresUtc,
            deviceId = device.DeviceId,
            mobileVerificationIntervalDays = device.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) ? Math.Clamp(user.MobileVerificationIntervalDays <= 0 ? 30 : user.MobileVerificationIntervalDays, 1, 365) : 0,
            mobileLastVerifiedUtc = device.MobileMfaVerifiedUtc,
            mobileNextVerificationUtc = device.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) && device.MobileMfaVerifiedUtc.HasValue ? (DateTime?)device.MobileMfaVerifiedUtc.Value.AddDays(Math.Clamp(user.MobileVerificationIntervalDays <= 0 ? 30 : user.MobileVerificationIntervalDays, 1, 365)) : null
        });
    }

    private Task<IResult> LogoutAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out _,out _);
        if(session is not null)_serverSecurity.DeleteSession(session.SessionHash);
        return Task.FromResult<IResult>(Results.Ok(new{loggedOut=true}));
    }

    private async Task<IResult> HeartbeatAsync(HttpContext context)
    {
        ExpireInactiveTrust();
        var deviceId=context.Request.Headers["X-AAH-Device"].ToString();
        ApplicationUserRecord? liveUser=null;
        ServerDeviceRecord? device=null;
        var authorization=context.Request.Headers.Authorization.ToString();
        if(authorization.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))
        {
            // A signed-in heartbeat is authoritative for who is currently using this enrolled PC.
            // Keep device-only heartbeat support for enrollment/login/update recovery before a
            // user session exists, but never overwrite live presence with the registration owner
            // while a valid authenticated AAH session is already running.
            _=AuthenticateSession(context,out liveUser,out device);
        }
        device??=AuthenticateDeviceFromHeaders(context,touch:true);
        if(device is null)return DeviceRegistrationFailure(deviceId);
        liveUser??=GetApplicationUser(device.UserName);
        if(liveUser is null||!liveUser.IsActive)return Results.Json(new{error="The AAH User using this device is disabled or no longer exists.",code="LAN_USER_DISABLED"},statusCode:StatusCodes.Status403Forbidden);
        HeartbeatContextRequest? heartbeat=null;
        if(context.Request.ContentLength.GetValueOrDefault()>0)
        {
            try{heartbeat=await context.Request.ReadFromJsonAsync<HeartbeatContextRequest>(context.RequestAborted).ConfigureAwait(false);}catch{heartbeat=null;}
        }
        _enterpriseControl.TouchDevicePresence(new DevicePresenceRecord{DeviceId=device.DeviceId,UserName=liveUser.WindowsUserName,ClientId=heartbeat?.ClientId??0,FinancialYear=heartbeat?.FinancialYear??string.Empty,Module=heartbeat?.Module??string.Empty,AppVersion=device.AppVersion,LastSeenUtc=DateTime.UtcNow});
        // R42 Mobile Link status is event-driven from the existing authenticated heartbeat.
        // Presence is a separate lightweight notification: heartbeat must never reload the
        // invoice queue or Client/FY Master lists.
        MobilePresenceChanged?.Invoke(this, EventArgs.Empty);
        _storage?.RegisterConfiguredPackages(Endpoint, RemoteAssistInternetEndpoint);
        var package=_serverSecurity.GetPackage(device.DeviceType.Equals(ServerDeviceTypes.Android,StringComparison.OrdinalIgnoreCase)?"Android":"Windows");
        var policy=GetWindowsUpdatePolicy(device);
        var isAndroid=device.DeviceType.Equals(ServerDeviceTypes.Android,StringComparison.OrdinalIgnoreCase);
        var updatePackageAvailable=package is not null&&File.Exists(package.FilePath)&&
            (!isAndroid||package.Version.Equals(ServerStorageService.CurrentAndroidReleaseVersion,StringComparison.OrdinalIgnoreCase));
        var approvedVersion=device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)
            ? policy.ApprovedVersion
            : package?.Version??ServerStorageService.CurrentAndroidReleaseVersion;
        if(isAndroid&&!approvedVersion.Equals(ServerStorageService.CurrentAndroidReleaseVersion,StringComparison.OrdinalIgnoreCase))
            approvedVersion=ServerStorageService.CurrentAndroidReleaseVersion;
        var approvedSha256=device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)
            ? policy.ApprovedSha256
            : updatePackageAvailable ? package!.Sha256 : string.Empty;
        var updateRequired=approvedVersion.Length>0&&!approvedVersion.Equals(device.AppVersion,StringComparison.OrdinalIgnoreCase);
        var posture=EvaluateDevicePosture(device,approvedVersion,updateRequired);
        var revisions=_enterpriseControl.GetRevisions();
        var lockdown=_enterpriseControl.GetEmergencyLockdown();
        return Results.Json(new{active=!posture.IsQuarantined&&!lockdown.IsActive,deviceId=device.DeviceId,serverUtc=DateTime.UtcNow,expiryDays=GetDeviceExpiryDays(device.DeviceType),registrationOtpRequired=device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&!device.RegistrationOtpVerifiedUtc.HasValue,registrationOtpVerifiedUtc=device.RegistrationOtpVerifiedUtc,appVersion=device.AppVersion,serverVersion=RuntimeVersion,serverExecutable=RuntimeExecutablePath,approvedVersion,approvedSha256,updateRequired,updatePackageAvailable,forceUpdate=policy.ForceUpdate,forcedVersion=policy.ForcedVersion,healthState=posture.HealthState,riskScore=posture.RiskScore,quarantined=posture.IsQuarantined,quarantineReason=posture.QuarantineReason,authorizationRevision=revisions.AuthorizationRevision,devicePolicyRevision=revisions.DevicePolicyRevision,clientAccessRevision=revisions.ClientAccessRevision,securityPolicyRevision=revisions.SecurityPolicyRevision,accountingDataRevision=NetworkLiveRevisionTracker.AccountingDataRevision,emergencyLockdown=lockdown.IsActive,lockdownReason=lockdown.Reason,updateRing=policy.RingName,updateRingEnabled=policy.RingEnabled,updateForceAfterUtc=policy.ForceAfterUtc});
    }

    private IResult GetClients(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);
        if(session is null || user is null || device is null)return Results.Unauthorized();
        if (DenyAccountingWhenQuarantined(user, device, "ClientDirectory") is { } quarantineBlock) return quarantineBlock;
        if(!Can(user,GovernancePermissions.ReadData) && !Can(user,GovernancePermissions.UploadFiles) && !Can(user,GovernancePermissions.Import) && !Can(user,GovernancePermissions.ExportData))return Results.Forbid();
        // Import Only users may see only the allowed Client/FY identity list needed to choose an upload destination; no accounting summary endpoint is granted.
        var rows=_readModel!.GetClientsForUser(user.WindowsUserName,(u,c,fy)=>NetworkHasClientAccess(user,device,c,fy));
        return Results.Json(rows);
    }

    private IResult GetClientSummary(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);
        if(session is null || user is null || device is null)return Results.Unauthorized();
        if (DenyAccountingWhenQuarantined(user, device, "ClientSummary") is { } quarantineBlock) return quarantineBlock;
        if(!Can(user,GovernancePermissions.ReadData))return Results.Forbid();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        var summary=_readModel!.GetClientSummary(clientId,client.FinancialYear);return summary is null?Results.NotFound():Results.Json(summary);
    }

    private IResult DownloadFullUiSnapshot(HttpContext context)
    {
        var session = AuthenticateSession(context, out var user, out var device);
        if (session is null || user is null || device is null) return Results.Unauthorized();
        if (DenyAccountingWhenQuarantined(user, device, "SnapshotDownload") is { } quarantineBlock) return quarantineBlock;
        var canReadData = Can(user, GovernancePermissions.ReadData);
        var canUseNetwork = canReadData || Can(user, GovernancePermissions.UploadFiles) || Can(user, GovernancePermissions.Import) || Can(user, GovernancePermissions.ExportData);
        if (!canUseNetwork) return Results.Forbid();
        var allowed = _readModel!.GetClientsForUser(user.WindowsUserName, (u,c,fy) => NetworkHasClientAccess(user,device,c,fy)).Select(x => x.Id).Distinct().ToArray();
        // Repair 18: authentication/enrollment and client assignment are intentionally separate stages.
        // A newly enrolled LAN system is allowed to open AAH before any client is assigned. In that
        // state the snapshot service returns a standalone zero-data SQLite bootstrap without touching the authoritative accounting database.
        // The enrolled system is already visible to the Main Server Client Directory, where the
        // Administrator can enable LAN Available and grant client access. A later LAN Refresh then
        // replaces the empty shell with only the newly-authorised client data.
        try
        {
            var bytes = canReadData
                ? _snapshotService.Build(allowed)
                : _snapshotService.BuildIdentityOnly(allowed.Select(id => _clients.GetById(id)).Where(x => x is not null).Select(x => x!).ToArray());
            var diagnostic = _snapshotService.LastDiagnostic;
            var summary = diagnostic?.ToLogText() ?? (allowed.Length == 0 ? "Standalone bootstrap database; Clients=0; awaiting Client Directory access." : $"Full desktop read-only snapshot; Clients={allowed.Length}");
            SafeLogEvent("NetworkReadOnlySnapshot", user.WindowsUserName, device.DeviceId, summary, true);
            return Results.File(bytes, "application/octet-stream", $"AAH-ReadOnly-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        }
        catch (NetworkSnapshotBuildException exception)
        {
            var diagnostic = exception.Diagnostic;
            SafeLogEvent("NetworkReadOnlySnapshot", user.WindowsUserName, device.DeviceId, diagnostic.ToLogText(), false);
            var failureStage = string.IsNullOrWhiteSpace(diagnostic.FailureStage) ? diagnostic.LastStage : diagnostic.FailureStage;
            return Results.Json(new { error = $"LAN authentication succeeded, but the Main Server snapshot failed at stage: {failureStage}. Check Settings -> Security & LAN -> Main Server Control Panel. Retry Open AAH; the existing authenticated session remains valid.", code = "SNAPSHOT_BUILD_FAILED", stage = failureStage }, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception exception)
        {
            SafeLogEvent("NetworkReadOnlySnapshot", user.WindowsUserName, device.DeviceId, $"Unexpected snapshot failure: {exception.GetType().Name}: {exception.Message}; Inner={exception.InnerException?.Message}", false);
            return Results.Json(new { error = "Registration verification/sign-in succeeded, but the Main Server snapshot failed before detailed stage diagnostics were available. Check Settings -> Security & LAN -> Main Server Control Panel.", code = "SNAPSHOT_BUILD_FAILED", stage = "Unexpected server exception" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<IResult> UploadFileAsync(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if (DenyAccountingWhenQuarantined(user, device, "ClientUpload") is { } quarantineBlock) return quarantineBlock;
        if(!Can(user,GovernancePermissions.UploadFiles) && !Can(user,GovernancePermissions.Import))return Results.Forbid();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        return await ReceiveUploadAsync(context,clientId,client.FinancialYear,device,user.WindowsUserName).ConfigureAwait(false);
    }

    private async Task<IResult> CreateFolderLinkAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if (DenyAccountingWhenQuarantined(user, device, "FolderLinkCreate") is { } quarantineBlock) return quarantineBlock;
        if(!Can(user,GovernancePermissions.UploadFiles)&&!Can(user,GovernancePermissions.Import))return Results.Forbid();
        FolderLinkRequest? request;try{request=await context.Request.ReadFromJsonAsync<FolderLinkRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid folder-link request."});}
        if(request is null)return Results.BadRequest();var client=_clients.GetById(request.ClientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,client.Id,client.FinancialYear))return Results.Forbid();
        var created=_serverSecurity.CreateFolderLink(device,client.Id,client.FinancialYear,request.LocalLabel);
        return Results.Json(new{linkId=created.Record.LinkId,folderToken=created.RawToken,clientId=client.Id,financialYear=client.FinancialYear,expiresAfterInactiveDays=GetFolderExpiryDays(),note="Local deletion never removes the retained server copy."});
    }

    private IResult HeartbeatFolderLink(HttpContext context,string linkId)
    {
        var device=AuthenticateDeviceFromHeaders(context,touch:true);if(device is null)return Results.Unauthorized();
        var folderToken=context.Request.Headers["X-AAH-Folder-Token"].ToString().Trim();var link=_serverSecurity.FindFolderLink(linkId,folderToken);
        if(link is null||!link.IsActive||!link.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase))return Results.Unauthorized();
        var owner=GetApplicationUser(device.UserName);if(owner is null||!owner.IsActive)return Results.Unauthorized();
        var basis=link.LastSeenUtc??link.CreatedUtc;if(basis<DateTime.UtcNow.AddDays(-GetFolderExpiryDays())){_serverSecurity.ExpireFolderLink(link.Id);return Results.StatusCode(StatusCodes.Status410Gone);}
        _serverSecurity.TouchFolderLink(link.Id);return Results.Json(new{active=true,linkId,expiresAfterInactiveDays=GetFolderExpiryDays(),serverUtc=DateTime.UtcNow});
    }

    private async Task<IResult> UploadFolderFileAsync(HttpContext context,string linkId)
    {
        var device=AuthenticateDeviceFromHeaders(context,touch:true);if(device is null)return Results.Unauthorized();
        var folderToken=context.Request.Headers["X-AAH-Folder-Token"].ToString().Trim();var link=_serverSecurity.FindFolderLink(linkId,folderToken);
        if(link is null||!link.IsActive||!link.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase))return Results.Unauthorized();
        var basis=link.LastSeenUtc??link.CreatedUtc;if(basis<DateTime.UtcNow.AddDays(-GetFolderExpiryDays())){_serverSecurity.ExpireFolderLink(link.Id);return Results.StatusCode(StatusCodes.Status410Gone);}
        var user=GetApplicationUser(device.UserName);if(user is null||!user.IsActive)return Results.Unauthorized();
        if (DenyAccountingWhenQuarantined(user, device, "FolderUpload") is { } quarantineBlock) return quarantineBlock;
        if(!Can(user,GovernancePermissions.UploadFiles)&&!Can(user,GovernancePermissions.Import))return Results.Forbid();
        if(!NetworkHasClientAccess(user,device,link.ClientId,link.FinancialYear))return Results.Forbid();
        var result=await ReceiveUploadAsync(context,link.ClientId,link.FinancialYear,device,user.WindowsUserName).ConfigureAwait(false);_serverSecurity.TouchFolderLink(link.Id);return result;
    }

    private async Task<IResult> ExportAsync(HttpContext context,long clientId,string kind)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if (DenyAccountingWhenQuarantined(user, device, "ClientExport") is { } quarantineBlock) return quarantineBlock;
        if(!Can(user,GovernancePermissions.ExportData))return Results.Forbid();var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        try
        {
            var bytes=_readModel!.BuildCsvExport(clientId,client.FinancialYear,kind);var fileName=$"AAH_{ServerStorageService.SanitizeFileName(client.ClientCode)}_{kind}_{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            var stored=_storage!.SaveExport(clientId,client.FinancialYear,fileName,bytes,user.WindowsUserName,device.DeviceId);
            _serverSecurity.LogEvent("RemoteExport",user.WindowsUserName,device.DeviceId,$"{clientId}/{client.FinancialYear}; {kind}; {Path.GetFileName(stored)}",true);
            return Results.File(bytes,"text/csv; charset=utf-8",fileName);
        }
        catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private RemoteAssistSessionRecord RequireAdministrationRemoteAssistSession(string sessionId)
    {
        var session=_enterpriseControl.GetRemoteAssistSessions(500).FirstOrDefault(x=>x.SessionId.Equals(sessionId?.Trim(),StringComparison.OrdinalIgnoreCase));
        if(session is null||!session.IsActive||session.ExpiresUtc<=DateTime.UtcNow)
        {
            if(!string.IsNullOrWhiteSpace(sessionId))_remoteAssistRelay.RemoveSession(sessionId);
            throw new InvalidOperationException("Remote Assist session is not active.");
        }
        return session;
    }

    private bool TryGetRemoteAssistClientSession(HttpContext context,string sessionId,out ApplicationUserRecord? user,out ServerDeviceRecord? device,out RemoteAssistSessionRecord? remote)
    {
        var auth=AuthenticateSession(context,out user,out device);remote=null;
        if(auth is null||user is null||device is null)return false;
        remote=_enterpriseControl.GetRemoteAssistSessions(500).FirstOrDefault(x=>x.SessionId.Equals(sessionId?.Trim(),StringComparison.OrdinalIgnoreCase));
        if(remote is null||!remote.IsActive||!remote.UserConsented||remote.ExpiresUtc<=DateTime.UtcNow)
        {
            if(remote is not null)_remoteAssistRelay.RemoveSession(remote.SessionId);
            return false;
        }
        return remote.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&remote.RemoteUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase);
    }

    private IResult GetRemoteAssistStreamState(HttpContext context,string sessionId)
    {
        if(!TryGetRemoteAssistClientSession(context,sessionId,out _,out _,out var remote)||remote is null)return Results.Unauthorized();
        return Results.Json(_remoteAssistRelay.GetState(remote.SessionId));
    }

    private async Task<IResult> SetRemoteAssistRemoteStateAsync(HttpContext context,string sessionId)
    {
        if(!TryGetRemoteAssistClientSession(context,sessionId,out _,out _,out var remote)||remote is null)return Results.Unauthorized();
        RemoteAssistRemoteStateRequest? request;try{request=await context.Request.ReadFromJsonAsync<RemoteAssistRemoteStateRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid Remote Assist state."});}
        if(request is null)return Results.BadRequest();
        _remoteAssistRelay.SetRemoteState(remote.SessionId,request.ShareMyAah,request.AllowAdministratorControl);
        return Results.Json(_remoteAssistRelay.GetState(remote.SessionId));
    }

    private async Task<IResult> UploadRemoteAssistFrameAsync(HttpContext context,string sessionId)
    {
        if(!TryGetRemoteAssistClientSession(context,sessionId,out _,out _,out var remote)||remote is null)return Results.Unauthorized();
        var state=_remoteAssistRelay.GetState(remote.SessionId);if(!state.RemoteSharing)return Results.Conflict(new{error="Remote AAH sharing is paused."});
        if(context.Request.ContentLength is > 2_500_000)return Results.BadRequest(new{error="Remote Assist frame exceeds the 2.5 MB limit."});
        if(!int.TryParse(context.Request.Query["width"].ToString(),out var width)||!int.TryParse(context.Request.Query["height"].ToString(),out var height))return Results.BadRequest(new{error="Frame dimensions are required."});
        using var memory=new MemoryStream();await context.Request.Body.CopyToAsync(memory,context.RequestAborted).ConfigureAwait(false);
        var frame=_remoteAssistRelay.PublishFrame(remote.SessionId,RemoteAssistStreamDirections.RemoteToAdministrator,memory.ToArray(),width,height);
        return Results.Json(new{sequence=frame.Sequence,capturedUtc=frame.CapturedUtc});
    }

    private IResult GetRemoteAssistAdministratorFrame(HttpContext context,string sessionId)
    {
        if(!TryGetRemoteAssistClientSession(context,sessionId,out _,out _,out var remote)||remote is null)return Results.Unauthorized();
        var state=_remoteAssistRelay.GetState(remote.SessionId);if(!state.AdministratorSharing)return Results.NoContent();
        _=long.TryParse(context.Request.Query["after"].ToString(),out var after);var frame=_remoteAssistRelay.GetFrame(remote.SessionId,RemoteAssistStreamDirections.AdministratorToRemote,after);if(frame is null)return Results.NoContent();
        context.Response.Headers["X-AAH-Frame-Sequence"]=frame.Sequence.ToString();context.Response.Headers["X-AAH-Frame-Width"]=frame.PixelWidth.ToString();context.Response.Headers["X-AAH-Frame-Height"]=frame.PixelHeight.ToString();context.Response.Headers["X-AAH-Frame-Captured-Utc"]=frame.CapturedUtc.ToString("O");
        return Results.File(frame.JpegBytes,"image/jpeg");
    }

    private IResult GetRemoteAssistRemoteInput(HttpContext context,string sessionId)
    {
        if(!TryGetRemoteAssistClientSession(context,sessionId,out _,out _,out var remote)||remote is null)return Results.Unauthorized();
        var state=_remoteAssistRelay.GetState(remote.SessionId);if(!state.RemoteSharing||!state.AdministratorMayControlRemote)return Results.Json(Array.Empty<RemoteAssistInputRecord>());
        _=long.TryParse(context.Request.Query["after"].ToString(),out var after);return Results.Json(_remoteAssistRelay.DrainInputs(remote.SessionId,RemoteAssistStreamDirections.AdministratorToRemote,after));
    }

    private async Task<IResult> PostRemoteAssistAdministratorInputAsync(HttpContext context,string sessionId)
    {
        if(!TryGetRemoteAssistClientSession(context,sessionId,out _,out _,out var remote)||remote is null)return Results.Unauthorized();
        var state=_remoteAssistRelay.GetState(remote.SessionId);if(!state.AdministratorSharing||!state.RemoteMayControlAdministrator||state.AdministratorFrameSequence<=0)return Results.Forbid();
        RemoteAssistInputRecord? input;try{input=await context.Request.ReadFromJsonAsync<RemoteAssistInputRecord>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid Remote Assist input."});}
        if(input is null)return Results.BadRequest();input.Direction=RemoteAssistStreamDirections.RemoteToAdministrator;var queued=_remoteAssistRelay.QueueInput(remote.SessionId,input);return Results.Json(queued);
    }

    private IResult GetEnterpriseClientStatus(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);
        if(session is null||user is null||device is null)return Results.Unauthorized();
        var revisions=_enterpriseControl.GetRevisions();
        var posture=_enterpriseControl.GetPosture(device.DeviceId);
        var lockdown=_enterpriseControl.GetEmergencyLockdown();
        return Results.Json(new
        {
            userId=user.WindowsUserName,user=user.DisplayName,role=user.RoleName,permissions=GetEffectivePermissions(user),
            deviceId=device.DeviceId,deviceName=device.DeviceName,deviceType=device.DeviceType,registrationOtpVerifiedUtc=device.RegistrationOtpVerifiedUtc,
            posture,revisions,lockdown,serverUtc=DateTime.UtcNow,serverVersion=RuntimeVersion
        });
    }

    private async Task<IResult> GetRemoteCommandsAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);
        if(session is null||user is null||device is null)return Results.Unauthorized();
        if(device.DeviceType.Equals(ServerDeviceTypes.Android,StringComparison.OrdinalIgnoreCase))return Results.Json(Array.Empty<RemoteAahCommandRecord>());

        var commands=_enterpriseControl.GetPendingRemoteCommands(device.DeviceId);
        if(commands.Count==0)
        {
            var waitSeconds=context.Request.Query.TryGetValue("waitSeconds",out var waitRaw)&&int.TryParse(waitRaw.FirstOrDefault(),out var parsed)
                ?Math.Clamp(parsed,0,25):0;
            if(waitSeconds>0)
            {
                var signal=_remoteCommandSignals.GetOrAdd(device.DeviceId,_=>new SemaphoreSlim(0,1));
                try{await signal.WaitAsync(TimeSpan.FromSeconds(waitSeconds),context.RequestAborted).ConfigureAwait(false);}
                catch(OperationCanceledException){return Results.Json(Array.Empty<RemoteAahCommandRecord>());}
                commands=_enterpriseControl.GetPendingRemoteCommands(device.DeviceId);
            }
        }

        foreach(var command in commands)
        {
            // Delivered-but-uncompleted commands are intentionally returned again after a LAN app
            // restart so a pending consent request can recover. Do not flood the audit log on every
            // delivery while the same request is waiting for the user.
            if(!command.DeliveredUtc.HasValue || DateTime.UtcNow-command.DeliveredUtc.Value < TimeSpan.FromSeconds(5))
                _enterpriseControl.AppendAdminAudit("RemoteAgent",user.WindowsUserName,device.DeviceId,"RemoteCommandDelivered",command.Action,command.CommandId,string.Empty,true);
        }
        return Results.Json(commands);
    }

    private void SignalRemoteCommand(string deviceId)
    {
        if(string.IsNullOrWhiteSpace(deviceId))return;
        var signal=_remoteCommandSignals.GetOrAdd(deviceId.Trim(),_=>new SemaphoreSlim(0,1));
        try{if(signal.CurrentCount==0)signal.Release();}catch(SemaphoreFullException){}
    }

    private async Task<IResult> CompleteRemoteCommandAsync(HttpContext context,string commandId)
    {
        var session=AuthenticateSession(context,out var user,out var device);
        if(session is null||user is null||device is null)return Results.Unauthorized();
        RemoteCommandCompletion? request;try{request=await context.Request.ReadFromJsonAsync<RemoteCommandCompletion>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid command completion."});}
        if(request is null)return Results.BadRequest();
        var completedCommand=_enterpriseControl.GetRemoteCommand(commandId);
        _enterpriseControl.CompleteRemoteCommand(device.DeviceId,commandId,request.Success,request.Detail??string.Empty);
        if(completedCommand is not null&&completedCommand.Action.Equals(RemoteAahActions.RequestRemoteAssist,StringComparison.OrdinalIgnoreCase))
        {
            try{using var doc=JsonDocument.Parse(completedCommand.ArgumentsJson);var sid=doc.RootElement.TryGetProperty("sessionId",out var id)?id.GetString()??string.Empty:string.Empty;if(sid.Length>0){var consented=request.Success&&!(request.Detail??string.Empty).Contains("declined",StringComparison.OrdinalIgnoreCase)&&!(request.Detail??string.Empty).Contains("expired",StringComparison.OrdinalIgnoreCase);_enterpriseControl.SetRemoteAssistConsent(sid,consented);if(!consented){_enterpriseControl.EndRemoteAssistSession(sid);_remoteAssistRelay.RemoveSession(sid);}}}catch{}
        }
        _enterpriseControl.AppendAdminAudit("RemoteAgent",user.WindowsUserName,device.DeviceId,"RemoteCommandCompleted",commandId,request.Detail??string.Empty,string.Empty,request.Success);
        return Results.Ok(new{completed=true});
    }

    private IResult GetCurrentRemoteAssistSession(HttpContext context)
    {
        var networkSession=AuthenticateSession(context,out var user,out var device);
        if(networkSession is null||user is null||device is null)return Results.Unauthorized();
        var now=DateTime.UtcNow;
        var remote=_enterpriseControl.GetRemoteAssistSessions(500)
            .FirstOrDefault(x=>x.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&x.IsActive&&x.ExpiresUtc>now);
        if(remote is null)return Results.NoContent();
        if(!remote.RemoteUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        return Results.Json(remote);
    }

    private async Task<IResult> SetRemoteAssistConsentByUserAsync(HttpContext context,string sessionId)
    {
        var networkSession=AuthenticateSession(context,out var user,out var device);
        if(networkSession is null||user is null||device is null)return Results.Unauthorized();
        var remote=_enterpriseControl.GetRemoteAssistSessions(500).FirstOrDefault(x=>x.SessionId.Equals(sessionId,StringComparison.OrdinalIgnoreCase));
        if(remote is null)return Results.NotFound();
        if(!remote.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)||!remote.RemoteUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        bool accepted;
        try
        {
            using var json=await JsonDocument.ParseAsync(context.Request.Body,cancellationToken:context.RequestAborted).ConfigureAwait(false);
            accepted=json.RootElement.TryGetProperty("accepted",out var value)&&value.ValueKind==JsonValueKind.True;
        }
        catch{return Results.BadRequest(new{error="Invalid Remote Assist consent response."});}
        if(!remote.IsActive||remote.ExpiresUtc<=DateTime.UtcNow)return Results.Conflict(new{error="Remote Assist request is no longer active."});
        _enterpriseControl.SetRemoteAssistConsent(remote.SessionId,accepted);
        if(!accepted)
        {
            // Decline is terminal for this exact request. Leaving the authority row active would
            // make the LAN recovery watcher rediscover the same request after the user declined it.
            _enterpriseControl.EndRemoteAssistSession(remote.SessionId);
            _remoteAssistRelay.RemoveSession(remote.SessionId);
        }

        // The session-authority recovery path may satisfy consent without the original durable
        // RequestRemoteAssist command handler owning the request. Complete that queued/delivered
        // command here too so Main Server delivery state cannot remain stuck on DELIVERED.
        var requestCommand=_enterpriseControl.GetRecentRemoteCommands(500).FirstOrDefault(command=>
            command.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&
            command.Action.Equals(RemoteAahActions.RequestRemoteAssist,StringComparison.OrdinalIgnoreCase)&&
            command.ArgumentsJson.Contains(remote.SessionId,StringComparison.OrdinalIgnoreCase)&&
            (command.State.Equals(RemoteAahCommandStates.Pending,StringComparison.OrdinalIgnoreCase)||
             command.State.Equals(RemoteAahCommandStates.Delivered,StringComparison.OrdinalIgnoreCase)||
             command.State.Equals(RemoteAahCommandStates.Running,StringComparison.OrdinalIgnoreCase)));
        if(requestCommand is not null)
            _enterpriseControl.CompleteRemoteCommand(device.DeviceId,requestCommand.CommandId,accepted,accepted?
                $"Remote Assist session {remote.SessionId} accepted through live session authority.":
                $"Remote Assist session {remote.SessionId} declined through live session authority.");

        _enterpriseControl.AppendAdminAudit("User",user.WindowsUserName,device.DeviceId,accepted?"RemoteAssistConsentAccepted":"RemoteAssistConsentDeclined",remote.SessionId,accepted?"AAH user accepted the live Remote Assist request.":"AAH user declined the live Remote Assist request.","REMOTE_ASSIST_CONSENT",true);
        await Task.CompletedTask;
        return Results.Ok(new{sessionId=remote.SessionId,accepted});
    }

    private IResult GetRemoteAssistSessionStatus(HttpContext context,string sessionId)
    {
        var networkSession=AuthenticateSession(context,out var user,out var device);
        if(networkSession is null||user is null||device is null)return Results.Unauthorized();
        var remote=_enterpriseControl.GetRemoteAssistSessions(500).FirstOrDefault(x=>x.SessionId.Equals(sessionId,StringComparison.OrdinalIgnoreCase));
        if(remote is null)return Results.Ok(new{sessionId,known=false,active=false,userConsented=false,expiresUtc=(DateTime?)null});
        if(!remote.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        var active=remote.IsActive&&remote.ExpiresUtc>DateTime.UtcNow;
        return Results.Ok(new{sessionId=remote.SessionId,known=true,active,userConsented=active&&remote.UserConsented,expiresUtc=remote.ExpiresUtc});
    }

    private async Task<IResult> EndRemoteAssistByUserAsync(HttpContext context,string sessionId)
    {
        var session=AuthenticateSession(context,out var user,out var device);
        if(session is null||user is null||device is null)return Results.Unauthorized();
        var remote=_enterpriseControl.GetRemoteAssistSessions(500).FirstOrDefault(x=>x.SessionId.Equals(sessionId,StringComparison.OrdinalIgnoreCase));
        if(remote is null)return Results.NotFound();
        if(!remote.DeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)||!remote.RemoteUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        _enterpriseControl.EndRemoteAssistSession(remote.SessionId);
        _remoteAssistRelay.RemoveSession(remote.SessionId);
        _enterpriseControl.AppendAdminAudit("User",user.WindowsUserName,device.DeviceId,"RemoteAssistEndedByRemoteUser",remote.SessionId,"AAH user ended consented Remote Assist from the Remote Assist workspace.","REMOTE_ASSIST_END_USER",true);
        await Task.CompletedTask;
        return Results.Ok(new{ended=true,sessionId=remote.SessionId});
    }

    private IResult GetDirectoryUsers(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out _);if(session is null||user is null)return Results.Unauthorized();
        var now=DateTime.UtcNow;var sessions=_serverSecurity.GetSessions();var devices=_serverSecurity.GetServerDevices();var presenceRows=_enterpriseControl.GetDevicePresence();
        var chatStatuses=_collaboration.GetUserChatStatuses().ToDictionary(x=>x.UserName,StringComparer.OrdinalIgnoreCase);
        var clientNames=_clients.GetAll(includeArchived:false).ToDictionary(x=>x.Id,x=>$"{x.ClientCode} · {x.ClientName}");
        var rows=_governance.GetUsers().Where(x=>x.IsActive).OrderBy(x=>x.DisplayName).Select(x=>
        {
            var active=sessions.Where(s=>s.UserName.Equals(x.WindowsUserName,StringComparison.OrdinalIgnoreCase)&&s.ExpiresUtc>now).OrderByDescending(s=>s.LastSeenUtc).ToArray();
            var latest=active.FirstOrDefault();var ids=active.Select(s=>s.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();var names=devices.Where(d=>ids.Contains(d.DeviceId,StringComparer.OrdinalIgnoreCase)).Select(d=>d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var live=presenceRows.Where(p=>p.UserName.Equals(x.WindowsUserName,StringComparison.OrdinalIgnoreCase)&&ids.Contains(p.DeviceId,StringComparer.OrdinalIgnoreCase)).OrderByDescending(p=>p.LastSeenUtc).FirstOrDefault();
            var effectiveLastSeen=live?.LastSeenUtc??latest?.LastSeenUtc??x.LastSeenUtc;
            var presence=active.Length==0?"Offline":effectiveLastSeen.HasValue&&effectiveLastSeen.Value>=now.AddMinutes(-3)?"Online":"Away";
            chatStatuses.TryGetValue(x.WindowsUserName,out var chat);
            var availability=chat?.Availability??"Available";if(presence.Equals("Offline",StringComparison.OrdinalIgnoreCase))availability="Offline";
            var clientText=live is null||live.ClientId<=0?string.Empty:clientNames.TryGetValue(live.ClientId,out var name)?name:$"Client {live.ClientId}";
            return new{userId=x.WindowsUserName,displayName=x.DisplayName,role=x.RoleName,presence,lastSeenUtc=effectiveLastSeen,activeSessions=active.Length,devices=string.Join(", ",names),availability,statusMessage=chat?.StatusMessage??string.Empty,currentClientId=live?.ClientId??0,currentClient=clientText,currentFinancialYear=live?.FinancialYear??string.Empty,currentModule=live?.Module??string.Empty};
        }).ToArray();
        return Results.Json(rows);
    }

    private IResult GetMessages(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);
        if(session is null||user is null||device is null)return Results.Unauthorized();
        var rows=_collaboration.GetInbox(user.WindowsUserName,300)
            .Where(message=>CanAccessCollaborationMessage(user,device,message))
            .ToArray();
        return Results.Json(rows);
    }

    private async Task<IResult> SendMessageAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);
        if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!Can(user,GovernancePermissions.MessagesSend))return Results.Forbid();
        CollaborationMessageRecord? request;try{request=await context.Request.ReadFromJsonAsync<CollaborationMessageRecord>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid message."});}
        if(request is null||string.IsNullOrWhiteSpace(request.Body))return Results.BadRequest(new{error="Message text is required."});
        var allowedConversationTypes=new[]{"Private","Global","Client","Announcement","Direct","Team"};
        request.ConversationType=string.IsNullOrWhiteSpace(request.ConversationType)?"Private":request.ConversationType.Trim();
        if(!allowedConversationTypes.Contains(request.ConversationType,StringComparer.OrdinalIgnoreCase))return Results.BadRequest(new{error="Unsupported conversation type."});
        if(request.ConversationType.Equals("Direct",StringComparison.OrdinalIgnoreCase))request.ConversationType="Private";
        if(request.ConversationType.Equals("Team",StringComparison.OrdinalIgnoreCase))request.ConversationType="Global";
        if(request.ConversationType.Equals("Announcement",StringComparison.OrdinalIgnoreCase)&&!Can(user,GovernancePermissions.ManageUsers)&&!Can(user,GovernancePermissions.ManageServer))return Results.Forbid();
        if(request.ConversationType.Equals("Private",StringComparison.OrdinalIgnoreCase)&&string.IsNullOrWhiteSpace(request.RecipientUserName))return Results.BadRequest(new{error="Select a recipient for a private message."});
        if(request.ConversationType.Equals("Private",StringComparison.OrdinalIgnoreCase))
        {
            var recipient=GetApplicationUser(request.RecipientUserName);if(recipient is null||!recipient.IsActive)return Results.BadRequest(new{error="The selected private-chat recipient is unavailable."});
        }
        if(request.ConversationType.Equals("Global",StringComparison.OrdinalIgnoreCase)||request.ConversationType.Equals("Announcement",StringComparison.OrdinalIgnoreCase))request.RecipientUserName=string.Empty;
        if(request.ClientId>0)
        {
            var client=_clients.GetById(request.ClientId);if(client is null||client.IsArchived)return Results.NotFound();
            request.FinancialYear=string.IsNullOrWhiteSpace(request.FinancialYear)?client.FinancialYear:request.FinancialYear;
            if(!NetworkHasClientAccess(user,device,client.Id,request.FinancialYear))return Results.Forbid();
        }
        request.SenderUserName=user.WindowsUserName;
        request.Body=request.Body.Trim().Length>8000?request.Body.Trim()[..8000]:request.Body.Trim();
        // Conversation identity is always rebuilt on the Main Server from the authenticated sender,
        // recipient and Client/FY. Never trust a caller-supplied conversation key.
        request.ConversationKey=string.Empty;
        var expectedConversationKey=CollaborationRepository.GetConversationKey(request);
        if(request.ReplyToMessageId>0)
        {
            var parent=_collaboration.GetMessage(request.ReplyToMessageId);
            if(parent is null||!CanAccessCollaborationMessage(user,device,parent))return Results.BadRequest(new{error="Reply target is unavailable."});
            if(!parent.ConversationKey.Equals(expectedConversationKey,StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="A reply must stay inside the same conversation."});
        }
        var added=_collaboration.AddMessage(request);
        _enterpriseControl.AppendAdminAudit("User",user.WindowsUserName,device.DeviceId,"MessageSent",added.ConversationKey,$"MessageId={added.Id}",string.Empty,true);
        return Results.Json(added);
    }

    private async Task<IResult> MarkMessagesReadAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out _);if(session is null||user is null)return Results.Unauthorized();
        ConversationReadRequest? request;try{request=await context.Request.ReadFromJsonAsync<ConversationReadRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest();}
        if(request is null||string.IsNullOrWhiteSpace(request.ConversationKey))return Results.BadRequest();
        _collaboration.MarkConversationRead(user.WindowsUserName,request.ConversationKey);return Results.Ok(new{read=true});
    }

    private async Task<IResult> EditMessageAsync(HttpContext context,long id)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();if(!Can(user,GovernancePermissions.MessagesSend))return Results.Forbid();
        var existing=_collaboration.GetMessage(id);if(existing is null)return Results.NotFound();if(!CanAccessCollaborationMessage(user,device,existing))return Results.Forbid();if(!existing.SenderUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        MessageTextRequest? request;try{request=await context.Request.ReadFromJsonAsync<MessageTextRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid message edit."});}if(request is null)return Results.BadRequest();
        try{var updated=_collaboration.EditMessage(id,user.WindowsUserName,request.Body??string.Empty);_enterpriseControl.AppendAdminAudit("User",user.WindowsUserName,device.DeviceId,"MessageEdited",id.ToString(),updated.ConversationKey,string.Empty,true);return Results.Json(updated);}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private Task<IResult> DeleteMessageAsync(HttpContext context,long id)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Task.FromResult<IResult>(Results.Unauthorized());
        var existing=_collaboration.GetMessage(id);if(existing is null)return Task.FromResult<IResult>(Results.NotFound());if(!CanAccessCollaborationMessage(user,device,existing))return Task.FromResult<IResult>(Results.Forbid());
        var own=existing.SenderUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase);var administrative=Can(user,GovernancePermissions.ManageUsers)||Can(user,GovernancePermissions.ManageServer);if(!own&&!administrative)return Task.FromResult<IResult>(Results.Forbid());
        try{var updated=_collaboration.DeleteMessage(id,user.WindowsUserName,!own&&administrative);_enterpriseControl.AppendAdminAudit(own?"User":"Administrator",user.WindowsUserName,device.DeviceId,"MessageDeleted",id.ToString(),updated.ConversationKey,own?"SELF":"ADMIN",true);return Task.FromResult<IResult>(Results.Json(updated));}catch(Exception ex){return Task.FromResult<IResult>(Results.BadRequest(new{error=ex.Message}));}
    }

    private async Task<IResult> PinMessageAsync(HttpContext context,long id)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();if(!Can(user,GovernancePermissions.MessagesSend))return Results.Forbid();var existing=_collaboration.GetMessage(id);if(existing is null)return Results.NotFound();if(!CanAccessCollaborationMessage(user,device,existing))return Results.Forbid();
        if((existing.ConversationType.Equals("Global",StringComparison.OrdinalIgnoreCase)||existing.ConversationType.Equals("Announcement",StringComparison.OrdinalIgnoreCase))&&!Can(user,GovernancePermissions.ManageUsers)&&!Can(user,GovernancePermissions.ManageServer))return Results.Forbid();
        MessagePinRequest? request;try{request=await context.Request.ReadFromJsonAsync<MessagePinRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest();}if(request is null)return Results.BadRequest();try{var updated=_collaboration.SetMessagePinned(id,user.WindowsUserName,request.Pinned);return Results.Json(updated);}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private async Task<IResult> ReactMessageAsync(HttpContext context,long id)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();if(!Can(user,GovernancePermissions.MessagesSend))return Results.Forbid();var existing=_collaboration.GetMessage(id);if(existing is null)return Results.NotFound();if(!CanAccessCollaborationMessage(user,device,existing))return Results.Forbid();MessageReactionRequest? request;try{request=await context.Request.ReadFromJsonAsync<MessageReactionRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest();}if(request is null)return Results.BadRequest();try{return Results.Json(_collaboration.ToggleReaction(id,user.WindowsUserName,request.Reaction??string.Empty));}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private async Task<IResult> SetDirectoryStatusAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out _);if(session is null||user is null)return Results.Unauthorized();UserStatusRequest? request;try{request=await context.Request.ReadFromJsonAsync<UserStatusRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest();}if(request is null)return Results.BadRequest();return Results.Json(_collaboration.SetUserChatStatus(user.WindowsUserName,request.Availability??"Available",request.StatusMessage??string.Empty));
    }

    private IResult GetSharedFiles(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var rows=_collaboration.GetRecentFiles()
            .Where(file=>CanAccessSharedFile(user,device,file))
            .Select(file=>new CollaborationFileRecord{Id=file.Id,MessageId=file.MessageId,FileId=file.FileId,OriginalName=file.OriginalName,Sha256=file.Sha256,FileSize=file.FileSize,MimeType=file.MimeType,UploadedBy=file.UploadedBy,ClientId=file.ClientId,FinancialYear=file.FinancialYear,CreatedUtc=file.CreatedUtc}).ToArray();
        return Results.Json(rows);
    }

    private async Task<IResult> UploadSharedFileAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!Can(user,GovernancePermissions.FilesShare))return Results.Forbid();
        if(!context.Request.HasFormContentType)return Results.BadRequest(new{error="Use multipart/form-data."});
        const long maxSharedFileBytes=250L*1024*1024;
        if(context.Request.ContentLength is > maxSharedFileBytes)return Results.Json(new{error="Shared files are limited to 250 MB per file."},statusCode:StatusCodes.Status413PayloadTooLarge);
        var form=await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);if(form.Files.Count!=1)return Results.BadRequest(new{error="Upload exactly one shared file."});
        var file=form.Files[0];if(file.Length>maxSharedFileBytes)return Results.Json(new{error="Shared files are limited to 250 MB per file."},statusCode:StatusCodes.Status413PayloadTooLarge);
        long.TryParse(form["clientId"].ToString(),out var clientId);var fy=form["financialYear"].ToString();long.TryParse(form["messageId"].ToString(),out var messageId);
        if(messageId>0)
        {
            var message=_collaboration.GetMessage(messageId);if(message is null)return Results.NotFound();
            if(!CanAccessCollaborationMessage(user,device,message))return Results.Forbid();
            if(!message.SenderUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="Attach files through your own message in the selected conversation."});
            if(message.ClientId>0){clientId=message.ClientId;fy=message.FinancialYear;}
        }
        if(clientId>0){var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();fy=string.IsNullOrWhiteSpace(fy)?client.FinancialYear:fy;if(!NetworkHasClientAccess(user,device,clientId,fy))return Results.Forbid();}
        var error=ValidateSharedFileName(file.FileName);if(error.Length>0)return Results.BadRequest(new{error});
        await using var input=file.OpenReadStream();var saved=_collaboration.SaveFile(input,file.FileName,user.WindowsUserName,clientId,fy,messageId,file.ContentType??"application/octet-stream");
        _enterpriseControl.AppendAdminAudit("User",user.WindowsUserName,device.DeviceId,"SharedFileUploaded",saved.FileId,$"{saved.OriginalName}; {saved.FileSize} bytes; SHA256={saved.Sha256}",string.Empty,true);
        return Results.Json(new{saved.Id,saved.FileId,saved.OriginalName,saved.Sha256,saved.FileSize,saved.ClientId,saved.FinancialYear,saved.CreatedUtc});
    }

    private IResult DownloadSharedFile(HttpContext context,string fileId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!Can(user,GovernancePermissions.FilesShare)&&!Can(user,GovernancePermissions.ReadData))return Results.Forbid();
        var file=_collaboration.GetFile(fileId);if(file is null||!File.Exists(file.StoredPath))return Results.NotFound();
        if(!CanAccessSharedFile(user,device,file))return Results.Forbid();
        _enterpriseControl.AppendAdminAudit("User",user.WindowsUserName,device.DeviceId,"SharedFileDownloaded",file.FileId,file.OriginalName,string.Empty,true);
        return Results.File(file.StoredPath,string.IsNullOrWhiteSpace(file.MimeType)?"application/octet-stream":file.MimeType,file.OriginalName,enableRangeProcessing:true);
    }

    private IResult GetTasks(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var rows=_collaboration.GetTasks(user.WindowsUserName,Can(user,GovernancePermissions.TasksManage))
            .Where(task=>task.ClientId<=0||NetworkHasClientAccess(user,device,task.ClientId,task.FinancialYear)).ToArray();
        return Results.Json(rows);
    }

    private async Task<IResult> CreateTaskAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!Can(user,GovernancePermissions.TasksManage)&&!Can(user,GovernancePermissions.MessagesSend))return Results.Forbid();
        WorkTaskRecord? request;try{request=await context.Request.ReadFromJsonAsync<WorkTaskRecord>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid task."});}
        if(request is null||string.IsNullOrWhiteSpace(request.Title))return Results.BadRequest(new{error="Task title is required."});
        if(request.ClientId>0){var client=_clients.GetById(request.ClientId);if(client is null||client.IsArchived)return Results.NotFound();request.FinancialYear=string.IsNullOrWhiteSpace(request.FinancialYear)?client.FinancialYear:request.FinancialYear;if(!NetworkHasClientAccess(user,device,request.ClientId,request.FinancialYear))return Results.Forbid();}
        request.CreatedBy=user.WindowsUserName;if(string.IsNullOrWhiteSpace(request.AssignedTo))request.AssignedTo=user.WindowsUserName;
        var added=_collaboration.AddTask(request);return Results.Json(added);
    }

    private async Task<IResult> UpdateTaskAsync(HttpContext context,long id)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var existing=_collaboration.GetTask(id);if(existing is null)return Results.NotFound();
        if(existing.ClientId>0&&!NetworkHasClientAccess(user,device,existing.ClientId,existing.FinancialYear))return Results.Forbid();
        var canManage=Can(user,GovernancePermissions.TasksManage);
        var isParticipant=existing.AssignedTo.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase)||existing.CreatedBy.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase);
        if(!canManage&&!isParticipant)return Results.Forbid();
        WorkTaskUpdateRequest? request;try{request=await context.Request.ReadFromJsonAsync<WorkTaskUpdateRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid work-item update."});}
        if(request is null)return Results.BadRequest(new{error="Work-item update is required."});
        var assigned=string.IsNullOrWhiteSpace(request.AssignedTo)?existing.AssignedTo:request.AssignedTo.Trim();
        if(!canManage&&!assigned.Equals(existing.AssignedTo,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        var allowedStatuses=new[]{"Open","In Progress","Waiting","Escalated","Completed","Rejected"};
        var status=string.IsNullOrWhiteSpace(request.Status)?existing.Status:request.Status.Trim();
        if(!allowedStatuses.Contains(status,StringComparer.OrdinalIgnoreCase))return Results.BadRequest(new{error="Unsupported work-item status."});
        var allowedPriorities=new[]{"Normal","High","Urgent"};
        var priority=string.IsNullOrWhiteSpace(request.Priority)?existing.Priority:request.Priority.Trim();
        if(!allowedPriorities.Contains(priority,StringComparer.OrdinalIgnoreCase))return Results.BadRequest(new{error="Unsupported priority."});
        var updated=_collaboration.UpdateTask(id,user.WindowsUserName,assigned,status,priority,request.DueUtc,request.Description??existing.Description);
        _enterpriseControl.AppendAdminAudit("User",user.WindowsUserName,device.DeviceId,"WorkItemUpdated",id.ToString(),$"{updated.ItemType}; {updated.Status}; assigned={updated.AssignedTo}; priority={updated.Priority}",string.Empty,true);
        return Results.Json(updated);
    }

    private IResult GetApprovals(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var rows=_collaboration.GetApprovals(user.WindowsUserName,Can(user,GovernancePermissions.ApprovalsManage))
            .Where(approval=>CanAccessApprovalResource(user,device,approval)).ToArray();
        return Results.Json(rows);
    }

    private async Task<IResult> CreateApprovalAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        ApprovalRequestRecord? request;try{request=await context.Request.ReadFromJsonAsync<ApprovalRequestRecord>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid approval request."});}
        if(request is null||string.IsNullOrWhiteSpace(request.RequestType)||string.IsNullOrWhiteSpace(request.ResourceKey))return Results.BadRequest(new{error="Approval type and resource are required."});
        if(!CanAccessApprovalResource(user,device,request))return Results.Forbid();
        request.RequestedBy=user.WindowsUserName;var added=_collaboration.AddApproval(request);return Results.Json(added);
    }

    private async Task<IResult> DecideApprovalAsync(HttpContext context,long id)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!Can(user,GovernancePermissions.ApprovalsManage))return Results.Forbid();
        ApprovalDecisionRequest? request;try{request=await context.Request.ReadFromJsonAsync<ApprovalDecisionRequest>(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest();}
        if(request is null)return Results.BadRequest();
        var approval=_collaboration.GetApproval(id);if(approval is null)return Results.NotFound();if(!CanAccessApprovalResource(user,device,approval))return Results.Forbid();
        try
        {
            _collaboration.DecideApproval(id,user.WindowsUserName,request.Approve,request.Reason??string.Empty);
            if(request.Approve&&approval.RequestType.Equals("Client Access Request",StringComparison.OrdinalIgnoreCase)&&approval.ResourceKey.StartsWith("ClientAccess:",StringComparison.OrdinalIgnoreCase))
            {
                var parts=approval.ResourceKey.Split(':',6,StringSplitOptions.TrimEntries);
                if(parts.Length>=4&&long.TryParse(parts[1],out var clientId)&&clientId>0&&!string.IsNullOrWhiteSpace(parts[3]))
                {
                    var hours=parts.Length>4&&int.TryParse(parts[4],out var parsedHours)?Math.Clamp(parsedHours,1,168):8;
                    _governance.SetClientLanAvailable(clientId,true);_governance.SetUserClientAccess(parts[3],clientId,parts[2],true,DateTime.UtcNow.AddHours(hours));_enterpriseControl.BumpRevision("client");
                    _enterpriseControl.AppendAdminAudit("Administrator",user.WindowsUserName,device.DeviceId,"ClientAccessRequestApproved",approval.ResourceKey,$"Temporary access {hours} hour(s)","ACCESS_REQUEST",true);
                }
            }
            _enterpriseControl.AppendAdminAudit("User",user.WindowsUserName,device.DeviceId,request.Approve?"ApprovalApproved":"ApprovalRejected",id.ToString(),request.Reason??string.Empty,"FOUR_EYES",true);return Results.Ok(new{decided=true});
        }
        catch(Exception exception){return Results.BadRequest(new{error=exception.Message});}
    }

    private bool CanAccessCollaborationMessage(ApplicationUserRecord user,ServerDeviceRecord device,CollaborationMessageRecord message)
    {
        if(message.ClientId>0&&!NetworkHasClientAccess(user,device,message.ClientId,message.FinancialYear))return false;
        if(string.IsNullOrWhiteSpace(message.RecipientUserName))return true;
        return message.RecipientUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase)||message.SenderUserName.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase);
    }

    private bool CanAccessSharedFile(ApplicationUserRecord user,ServerDeviceRecord device,CollaborationFileRecord file)
    {
        if(file.ClientId>0&&!NetworkHasClientAccess(user,device,file.ClientId,file.FinancialYear))return false;
        if(file.MessageId>0)
        {
            var message=_collaboration.GetMessage(file.MessageId);
            return message is not null&&CanAccessCollaborationMessage(user,device,message);
        }
        if(file.ClientId>0)return true;
        return file.UploadedBy.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase);
    }

    private bool CanAccessApprovalResource(ApplicationUserRecord user,ServerDeviceRecord device,ApprovalRequestRecord approval)
    {
        if(approval.ResourceKey.StartsWith("ClientAccess:",StringComparison.OrdinalIgnoreCase))
        {
            var accessParts=approval.ResourceKey.Split(':',6,StringSplitOptions.TrimEntries);
            if(accessParts.Length<4||!long.TryParse(accessParts[1],out var accessClientId)||accessClientId<=0)return false;
            if(Can(user,GovernancePermissions.ApprovalsManage)||Can(user,GovernancePermissions.ManageClientAccess))return true;
            return approval.RequestedBy.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase);
        }
        if(!approval.ResourceKey.StartsWith("Client:",StringComparison.OrdinalIgnoreCase))return true;
        var parts=approval.ResourceKey.Split(':',4,StringSplitOptions.TrimEntries);
        if(parts.Length<3||!long.TryParse(parts[1],out var clientId)||clientId<=0)return false;
        return NetworkHasClientAccess(user,device,clientId,parts[2]);
    }

    private IResult? DenyAccountingWhenQuarantined(ApplicationUserRecord user, ServerDeviceRecord device, string action)
    {
        var lockdown = _enterpriseControl.GetEmergencyLockdown();
        if (lockdown.IsActive)
        {
            _enterpriseControl.AppendAdminAudit("Policy", user.WindowsUserName, device.DeviceId, "EmergencyLockdownBlocked", action, lockdown.Reason, "EMERGENCY_LOCKDOWN", false);
            return Results.Json(new
            {
                error = "AAH Main Server Emergency Lockdown is active. New LAN/remote accounting access is temporarily blocked; update, diagnostics, session sign-out and Remote AAH remediation remain available.",
                code = "EMERGENCY_LOCKDOWN",
                lockdown.Reason,
                lockdown.ChangedBy,
                lockdown.ChangedUtc
            }, statusCode: StatusCodes.Status423Locked);
        }

        var posture = _enterpriseControl.GetPosture(device.DeviceId);
        if (!posture.IsQuarantined) return null;
        _enterpriseControl.AppendAdminAudit("Policy", user.WindowsUserName, device.DeviceId, "QuarantineBlocked", action, posture.QuarantineReason, "DEVICE_QUARANTINE", false);
        return Results.Json(new
        {
            error = "This AAH device is quarantined by the Main Server. Accounting data is unavailable until an Administrator clears the device posture. Update, diagnostics, session sign-out and Remote AAH remediation remain available.",
            code = "DEVICE_QUARANTINED",
            posture.HealthState,
            posture.RiskScore,
            posture.QuarantineReason
        }, statusCode: StatusCodes.Status423Locked);
    }

    private DevicePostureRecord EvaluateDevicePosture(ServerDeviceRecord device,string approvedVersion,bool updateRequired)
    {
        var posture=_enterpriseControl.GetPosture(device.DeviceId);
        posture.DeviceId=device.DeviceId;
        posture.AppVersionCurrent=!updateRequired;
        posture.RegistrationTrusted=!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)||device.RegistrationOtpVerifiedUtc.HasValue;
        posture.CertificateValid=true;
        posture.SnapshotHealthy=true;
        var reasons=new List<string>();var risk=0;
        if(!posture.AppVersionCurrent){risk+=20;reasons.Add($"Application {device.AppVersion} differs from approved {approvedVersion}.");}
        if(!posture.RegistrationTrusted){risk+=50;reasons.Add("Registration OTP verification is required.");}
        posture.RiskScore=Math.Clamp(risk,0,100);
        posture.RiskReasons=string.Join(" ",reasons);
        if(posture.IsQuarantined)posture.HealthState=DeviceHealthStates.Quarantined;
        else if(posture.RiskScore>=60)posture.HealthState=DeviceHealthStates.Noncompliant;
        else if(posture.RiskScore>=20)posture.HealthState=DeviceHealthStates.Warning;
        else posture.HealthState=DeviceHealthStates.Healthy;
        posture.EvaluatedUtc=DateTime.UtcNow;_enterpriseControl.SavePosture(posture);return posture;
    }

    private static string ValidateSharedFileName(string fileName)
    {
        var safe=Path.GetFileName(fileName??string.Empty);if(safe.Length==0)return "File name is required.";
        var blocked=new HashSet<string>(StringComparer.OrdinalIgnoreCase){".exe",".dll",".com",".bat",".cmd",".ps1",".psm1",".vbs",".js",".jse",".wsf",".msi",".msp",".scr",".lnk",".url",".hta",".reg",".py",".pyw",".jar",".apk"};
        return blocked.Contains(Path.GetExtension(safe))?"Executable/script/application packages cannot be shared through AAH Messages.":string.Empty;
    }

    private IResult GetPackageMetadata(HttpContext context,string kind)
    {
        var device=AuthenticateDeviceFromHeaders(context,touch:true);if(device is null)return Results.Unauthorized();
        var packageKind=NormalizePackageKind(kind);
        _storage?.RegisterConfiguredPackages(Endpoint, RemoteAssistInternetEndpoint);var package=_serverSecurity.GetPackage(packageKind);
        if(package is null||!File.Exists(package.FilePath))
        {
            if(packageKind.Equals("Android",StringComparison.OrdinalIgnoreCase))
                return Results.Json(new{error=$"Android update {ServerStorageService.CurrentAndroidReleaseVersion} is required, but the matching signed APK has not been published on this Main Server. Administrator: Settings > Security & LAN > Android APK, select the APK signed with the established AAH Android certificate.",code="ANDROID_UPDATE_PACKAGE_NOT_PUBLISHED",requestedVersion=ServerStorageService.CurrentAndroidReleaseVersion},statusCode:StatusCodes.Status409Conflict);
            return Results.NotFound();
        }
        if(packageKind.Equals("Android",StringComparison.OrdinalIgnoreCase)&&!package.Version.Equals(ServerStorageService.CurrentAndroidReleaseVersion,StringComparison.OrdinalIgnoreCase))
            return Results.Json(new{error=$"Android update {ServerStorageService.CurrentAndroidReleaseVersion} is required, but this Main Server currently has only Android package {package.Version}. Publish the matching signed APK from Settings > Security & LAN > Android APK.",code="ANDROID_UPDATE_PACKAGE_OUTDATED",requestedVersion=ServerStorageService.CurrentAndroidReleaseVersion,publishedVersion=package.Version},statusCode:StatusCodes.Status409Conflict);
        var policy=GetWindowsUpdatePolicy(device);
        return Results.Json(new{packageKind=package.PackageKind,version=package.Version,sha256=package.Sha256,fileName=Path.GetFileName(package.FilePath),size=new FileInfo(package.FilePath).Length,forceUpdate=policy.ForceUpdate});
    }

    private IResult DownloadPackage(HttpContext context,string kind)
    {
        var device=AuthenticateDeviceFromHeaders(context,touch:true);if(device is null)return Results.Unauthorized();
        var packageKind=NormalizePackageKind(kind);
        _storage?.RegisterConfiguredPackages(Endpoint, RemoteAssistInternetEndpoint);var package=_serverSecurity.GetPackage(packageKind);
        if(package is null||!File.Exists(package.FilePath))
        {
            if(packageKind.Equals("Android",StringComparison.OrdinalIgnoreCase))
                return Results.Json(new{error=$"Android update {ServerStorageService.CurrentAndroidReleaseVersion} is required, but the matching signed APK has not been published on this Main Server. Administrator: Settings > Security & LAN > Android APK, select the APK signed with the established AAH Android certificate.",code="ANDROID_UPDATE_PACKAGE_NOT_PUBLISHED",requestedVersion=ServerStorageService.CurrentAndroidReleaseVersion},statusCode:StatusCodes.Status409Conflict);
            return Results.NotFound();
        }
        if(packageKind.Equals("Android",StringComparison.OrdinalIgnoreCase)&&!package.Version.Equals(ServerStorageService.CurrentAndroidReleaseVersion,StringComparison.OrdinalIgnoreCase))
            return Results.Json(new{error=$"Android update {ServerStorageService.CurrentAndroidReleaseVersion} is required, but this Main Server currently has only Android package {package.Version}. Publish the matching signed APK from Settings > Security & LAN > Android APK.",code="ANDROID_UPDATE_PACKAGE_OUTDATED",requestedVersion=ServerStorageService.CurrentAndroidReleaseVersion,publishedVersion=package.Version},statusCode:StatusCodes.Status409Conflict);
        var contentType=package.PackageKind.Equals("Android",StringComparison.OrdinalIgnoreCase)?"application/vnd.android.package-archive":"application/octet-stream";
        return Results.File(package.FilePath,contentType,Path.GetFileName(package.FilePath),enableRangeProcessing:true);
    }

    private IResult DownloadBootstrapPackage(string kind,string ticket)
    {
        var packageKind=NormalizePackageKind(kind);var consumed=_serverSecurity.ConsumeDistributionTicket(ticket,packageKind);if(consumed is null)return Results.Unauthorized();
        _storage?.RegisterConfiguredPackages(Endpoint, RemoteAssistInternetEndpoint);var package=_serverSecurity.GetPackage(packageKind);if(package is null||!File.Exists(package.FilePath))return Results.NotFound();
        if(!package.Sha256.Equals(consumed.PackageSha256,StringComparison.OrdinalIgnoreCase)||!package.Version.Equals(consumed.PackageVersion,StringComparison.OrdinalIgnoreCase))
        {
            _serverSecurity.LogEvent("PackageBootstrapDownload",consumed.CreatedBy,string.Empty,$"{packageKind}; approved package changed after ticket creation",false);
            return Results.Conflict(new{error="The approved Main Server package changed after this ticket was created. Ask the Administrator for a new download ticket."});
        }
        _serverSecurity.LogEvent("PackageBootstrapDownload",consumed.CreatedBy,string.Empty,$"{packageKind}; {package.Version}; {package.Sha256}; {Path.GetFileName(package.FilePath)}",true);
        var contentType=package.PackageKind.Equals("Android",StringComparison.OrdinalIgnoreCase)?"application/vnd.android.package-archive":"application/octet-stream";
        return Results.File(package.FilePath,contentType,Path.GetFileName(package.FilePath),enableRangeProcessing:false);
    }

    public string[] GetDistributedImportPasswordCandidates(DistributedImportJobRecord job) => UnprotectPasswordCandidates(job.ProtectedPasswordCandidates);

    public void CompleteMainServerDistributedImport(string jobId, AutomationCommandResult result)
    {
        var current=_distributedJobs.Find(jobId);
        if(current is null || current.Status.Equals("Cancelled",StringComparison.OrdinalIgnoreCase)) return;
        var updated=_distributedJobs.Complete(jobId,result.WasSuccessful,result.Message,result.CommitVerified,result.CommittedRecordCount,result.IssueCode);
        if(updated is null) return;

        if (!string.IsNullOrWhiteSpace(updated.ParentJobId))
        {
            var parent=_distributedJobs.Find(updated.ParentJobId);
            if(parent is not null)
            {
                _distributedJobs.Update(parent with
                {
                    Status=updated.Status,
                    ResultDetail=updated.ResultDetail,
                    CommitVerified=updated.CommitVerified,
                    CommittedRecordCount=updated.CommittedRecordCount,
                    IssueCode=updated.IssueCode,
                    AuthoritativeJobId=updated.JobId
                });
            }
        }

        if(result.WasSuccessful) NetworkLiveRevisionTracker.BumpAccountingData();
        SafeLogEvent("DistributedImportMainServer",updated.RequestedBy,updated.SourceDeviceId,$"{jobId}; Success={result.WasSuccessful}; CommitVerified={result.CommitVerified}; Records={result.CommittedRecordCount}; Issue={result.IssueCode}; {result.Message}",result.WasSuccessful);
    }

    private MobileLinkConfiguration LoadMobileLinkConfiguration(long clientId, string financialYear)
    {
        var path = MobileLinkConfigurationPath(clientId, financialYear);
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<MobileLinkConfiguration>(File.ReadAllText(path, Encoding.UTF8)) ?? new MobileLinkConfiguration { ClientId = clientId, FinancialYear = financialYear };
        }
        catch { }
        return new MobileLinkConfiguration { ClientId = clientId, FinancialYear = financialYear };
    }

    private static string MobileLinkRoot(long clientId, string financialYear) => Path.Combine(ApplicationDataPaths.RootFolder, "MobileLink", clientId.ToString(System.Globalization.CultureInfo.InvariantCulture), SafeMobileSegment(financialYear));
    private static string MobileLinkConfigurationPath(long clientId, string financialYear) => Path.Combine(MobileLinkRoot(clientId, financialYear), "link.json");
    private static string MobileInvoiceQueueRoot(long clientId, string financialYear) => Path.Combine(MobileLinkRoot(clientId, financialYear), "Queue");
    private static string SafeMobileSegment(string value)
    {
        var safe = string.Concat((value ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'));
        return safe.Length == 0 ? "UNKNOWN" : safe[..Math.Min(safe.Length, 96)];
    }
    private static void WriteMobileJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
    private static MobileInvoiceSummary ParseMobileInvoiceSummary(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson); var root = document.RootElement;
            static string S(JsonElement r, string n) => r.TryGetProperty(n, out var v) ? v.ToString() : string.Empty;
            var party = root.TryGetProperty("counterparty", out var counterparty) && counterparty.ValueKind == JsonValueKind.Object ? S(counterparty, "name") : string.Empty;
            return new MobileInvoiceSummary(S(root, "invoiceNumber"), S(root, "invoiceDate"), party, S(root, "direction"), S(root, "recognitionMode"), S(root, "invoiceTotal"));
        }
        catch { return new MobileInvoiceSummary(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty); }
    }
    private sealed record MobileInvoiceSummary(string InvoiceNumber, string InvoiceDate, string Counterparty, string Direction, string RecognitionMode, string InvoiceTotal);

    private void SafeLogEvent(string eventType, string userName, string deviceId, string detail, bool success)
    {
        try { _serverSecurity.LogEvent(eventType, userName, deviceId, detail, success); }
        catch { /* Security audit must not break enrollment/auth transport. */ }
    }

    private IResult GetMobileLinkForAndroid(HttpContext context, long clientId)
    {
        var session = AuthenticateSession(context, out var user, out var device); if (session is null || user is null || device is null) return Results.Unauthorized();
        var client = _clients.GetById(clientId); if (client is null || client.IsArchived) return Results.NotFound();
        if (!NetworkHasClientAccess(user, device, clientId, client.FinancialYear)) return Results.Forbid();
        var state = GetMobileLinkClientState(clientId, client.FinancialYear);
        // Authenticated + authorised Android session is the link; there is no separate manual User-ID pairing.
        return Results.Json(new { state.ClientId, state.FinancialYear, linkedUserId = user.WindowsUserName, linked = true, state.ShowMappingHere, state.SameClientOnline, state.MobileOnline, state.PresenceText, state.PendingCount });
    }

    private IResult GetInvoiceMasterOptions(HttpContext context, long clientId)
    {
        var session = AuthenticateSession(context, out var user, out var device); if (session is null || user is null || device is null) return Results.Unauthorized();
        var client = _clients.GetById(clientId); if (client is null || client.IsArchived) return Results.NotFound();
        if (!NetworkHasClientAccess(user, device, clientId, client.FinancialYear)) return Results.Forbid();
        EnsureRepositoriesInitialized();
        var ledgers = _invoiceMasterRepository.Get(clientId, client.FinancialYear, false, string.Empty)
            .Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !x.IsArchived)
            .Select(x => new { x.Name, x.Parent, masterId = x.TallyMasterId, source = x.Source })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var stockItems = _invoiceStockRepository.GetStockItems(clientId, client.FinancialYear, false, string.Empty)
            .Where(x => !x.IsArchived).Select(x => new { x.Name, x.StockGroup, x.BaseUnit, x.HsnSac, masterId = x.TallyMasterId })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var units = _invoiceStockRepository.GetUnits(clientId, client.FinancialYear, false, string.Empty)
            .Where(x => !x.IsArchived).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return Results.Json(new { clientId, financialYear = client.FinancialYear, ledgers, stockItems, units });
    }

    private IResult GetMobileInvoiceLearningForAndroid(HttpContext context, long clientId)
    {
        var session = AuthenticateSession(context, out var user, out var device); if (session is null || user is null || device is null) return Results.Unauthorized();
        var client = _clients.GetById(clientId); if (client is null || client.IsArchived) return Results.NotFound();
        if (!NetworkHasClientAccess(user, device, clientId, client.FinancialYear)) return Results.Forbid();
        return Results.Text(GetMobileInvoiceLearning(clientId, client.FinancialYear), "application/json", Encoding.UTF8);
    }

    private async Task<IResult> ReceiveMobileInvoiceAsync(HttpContext context, long clientId)
    {
        var session = AuthenticateSession(context, out var user, out var device); if (session is null || user is null || device is null) return Results.Unauthorized();
        var client = _clients.GetById(clientId); if (client is null || client.IsArchived) return Results.NotFound();
        if (!NetworkHasClientAccess(user, device, clientId, client.FinancialYear)) return Results.Forbid();
        if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "Use multipart/form-data for a mobile invoice scan." });
        var state = GetMobileLinkClientState(clientId, client.FinancialYear);
        if (!state.ShowMappingHere) return Results.Json(new { accepted = false, showMappingHere = false, action = "ReviewOnMobile" });
        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var payloadJson = form["payloadJson"].ToString();
        var rawOcr = form["rawOcr"].ToString();
        var candidatesJson = form["candidatesJson"].ToString();
        var relationshipsJson = form["relationshipsJson"].ToString();
        var requestedScanId = form["scanId"].ToString();
        if (string.IsNullOrWhiteSpace(payloadJson)) return Results.BadRequest(new { error = "Structured invoice recognition payload is required." });
        JsonDocument payload;
        try { payload = JsonDocument.Parse(payloadJson); } catch { return Results.BadRequest(new { error = "Invoice recognition payload JSON is invalid." }); }
        using (payload)
        {
            var root = payload.RootElement;
            if (root.TryGetProperty("clientId", out var clientElement) && clientElement.TryGetInt64(out var payloadClient) && payloadClient != clientId)
                return Results.BadRequest(new { error = "Mobile invoice Client ID does not match the selected Windows Client." });
            if (root.TryGetProperty("financialYear", out var fyElement) && !string.Equals(fyElement.GetString(), client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Mobile invoice Financial Year does not match the selected Windows Client/FY." });
        }
        var normalizedCandidates = string.IsNullOrWhiteSpace(candidatesJson) ? "{}" : candidatesJson;
        var normalizedRelationships = string.IsNullOrWhiteSpace(relationshipsJson) ? "{}" : relationshipsJson;
        try
        {
            using var candidatesDocument = JsonDocument.Parse(normalizedCandidates);
            using var relationshipsDocument = JsonDocument.Parse(normalizedRelationships);
            if (candidatesDocument.RootElement.ValueKind != JsonValueKind.Object || relationshipsDocument.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Invoice OCR candidates and relationships must be JSON objects." });
        }
        catch { return Results.BadRequest(new { error = "Invoice OCR candidates or relationship JSON is invalid." }); }
        var scanId = SafeMobileSegment(string.IsNullOrWhiteSpace(requestedScanId) ? $"SCAN-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}" : requestedScanId);
        var existing = GetMobileInvoiceQueueItem(clientId, client.FinancialYear, scanId);
        if (existing is not null)
        {
            var pending = GetMobileInvoiceQueue(clientId, client.FinancialYear).Count(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));
            return Results.Json(new { accepted = true, duplicate = true, showMappingHere = true, action = "ScanNext", scanId, queuePosition = Math.Max(1, pending), pendingCount = pending });
        }
        var queueRoot = MobileInvoiceQueueRoot(clientId, client.FinancialYear);
        var folder = Path.Combine(queueRoot, scanId);
        Directory.CreateDirectory(folder);
        var imagePaths = new List<string>();
        var index = 0;
        foreach (var file in form.Files)
        {
            index++;
            var extension = Path.GetExtension(file.FileName); if (extension.Length > 8 || extension.Any(ch => !char.IsLetterOrDigit(ch) && ch != '.')) extension = ".jpg";
            if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";
            var imagePath = Path.Combine(folder, $"page-{index:000}{extension.ToLowerInvariant()}");
            await using var source = file.OpenReadStream(); await using var target = File.Create(imagePath); await source.CopyToAsync(target, context.RequestAborted).ConfigureAwait(false);
            imagePaths.Add(imagePath);
        }
        var summary = ParseMobileInvoiceSummary(payloadJson);
        var row = new MobileInvoiceQueueItem
        {
            ScanId = scanId, ClientId = clientId, FinancialYear = client.FinancialYear, UserId = user.WindowsUserName, DeviceId = device.DeviceId,
            ReceivedUtc = DateTime.UtcNow, Status = "Queued", PayloadJson = payloadJson, RawOcr = rawOcr ?? string.Empty, PhoneRawOcr = rawOcr ?? string.Empty, CandidatesJson = normalizedCandidates, RelationshipsJson = normalizedRelationships,
            EvidenceFolder = folder, ImagePaths = imagePaths, InvoiceNumber = summary.InvoiceNumber, InvoiceDate = summary.InvoiceDate,
            Counterparty = summary.Counterparty, Direction = summary.Direction, RecognitionMode = summary.RecognitionMode, InvoiceTotal = summary.InvoiceTotal,
            DocumentOcrState = imagePaths.Count > 0 ? "Processing" : "NeedsAction",
            // R46 historic gate: AAH Paddle Invoice OCR + spatial document reconstruction queued.
            DocumentOcrMessage = imagePaths.Count > 0 ? "Independent ONNX Invoice OCR + polygon reconstruction queued." : "No original invoice image was uploaded for ONNX OCR.",
            DocumentOcrAuthority = imagePaths.Count > 0 ? "AAH ONNX invoice review pending" : "Unavailable"
        };
        _mobileInvoiceQueue[MobileQueueKey(clientId, client.FinancialYear, scanId)] = row;
        MobileLinkChanged?.Invoke(this, EventArgs.Empty);
        if (imagePaths.Count > 0) _ = RunMobileInvoiceDocumentOcrAsync(row, client);
        var position = GetMobileInvoiceQueue(clientId, client.FinancialYear).Count(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));
        return Results.Json(new { accepted = true, showMappingHere = true, action = "ScanNext", scanId, queuePosition = position, pendingCount = position, windowsDocumentOcr = row.DocumentOcrState });
    }

    private async Task RunMobileInvoiceDocumentOcrAsync(MobileInvoiceQueueItem row, AccountingAutomationHub.Domain.Clients.ClientProfile client)
    {
        var attemptKey = MobileQueueKey(row.ClientId, row.FinancialYear, row.ScanId);
        if (!_mobileInvoiceOcrInFlight.TryAdd(attemptKey, 0)) return;
        try
        {
            row.DocumentOcrState = "Processing";
            row.ChildStage1Status = "Original photo received by AAH; sending source-bound Stage 1 request to Child...";
            row.DocumentOcrMessage = "AAH ONNX Phone Link OCR is detecting bounded text polygons, classifying document semantics and preparing R89 accounting review evidence.";
            row.DocumentOcrAuthority = "AAH ONNX source-linked spatial OCR pending";
            MobileLinkChanged?.Invoke(this, EventArgs.Empty);

            // R96: AAH first passes its ORIGINAL, authenticated Client/FY photograph to
            // the standalone Child in a source-SHA-bound local handoff. Child returns only
            // provisional Stage 1 evidence until Stages 2–4 are independently certified.
            // Existing ONNX stays the accounting-review fallback; NEVER post Child geometry.
            var childStage1 = await InvoiceOcrChildBridge.TryStage1Async(
                row.ImagePaths, row.ClientId, row.FinancialYear, row.ScanId,
                row.EvidenceFolder, _mobileInvoiceOcrCancellation.Token).ConfigureAwait(false);
            row.ChildStage1Status = childStage1.Message;
            row.ChildStage1Archive = childStage1.Stage1Archive;
            row.DocumentOcrMessage = childStage1.Message;
            MobileLinkChanged?.Invoke(this, EventArgs.Empty);

            // R46 R37: Mobile Link owns a dedicated invoice OCR pipeline. It uses Windows OCR
            // for word boxes and AAH geometry/table reconstruction; Bank OCR is not involved.
            var runRoot = Path.Combine(row.EvidenceFolder, "InvoiceDocumentOcr");
            var result = await _mobileInvoiceDocumentOcr.RecognizeAsync(
                row.ImagePaths,
                client,
                row.PayloadJson,
                row.PhoneRawOcr,
                runRoot,
                message => row.DocumentOcrMessage = message,
                _mobileInvoiceOcrCancellation.Token).ConfigureAwait(false);

            var key = MobileQueueKey(row.ClientId, row.FinancialYear, row.ScanId);
            if (!_mobileInvoiceQueue.ContainsKey(key)) return;

            if (!result.WasSuccessful)
            {
                row.DocumentOcrState = "NeedsAction";
                row.DocumentOcrMessage = row.ChildStage1Status + " " + (result.FailureMessage.Length > 0
                    ? result.FailureMessage
                    : "Independent ONNX Phone Link OCR did not produce usable spatial evidence.");
                row.DocumentOcrAuthority = "Phone ML Kit preview only - confirmation blocked";
                MobileLinkChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            row.PayloadJson = result.PayloadJson;
            row.CandidatesJson = MergeMobileInvoiceCandidateJson(row.CandidatesJson, result.CandidatesJson);
            row.RawOcr = result.RawOcrText + (row.PhoneRawOcr.Length > 0
                ? Environment.NewLine + Environment.NewLine + "===== PHONE ML KIT PREVIEW (NON-AUTHORITATIVE) =====" + Environment.NewLine + row.PhoneRawOcr
                : string.Empty);
            row.InvoiceNumber = result.InvoiceNumber;
            row.InvoiceDate = result.InvoiceDate;
            row.Counterparty = result.CounterpartyName;
            row.Direction = result.Direction;
            row.RecognitionMode = result.RecognitionMode;
            row.InvoiceTotal = result.InvoiceTotal;
            row.DocumentOcrState = "Ready";
            row.DocumentOcrCompletedUtc = DateTime.UtcNow;
            row.DocumentOcrAuthority = result.Authority;
            row.DocumentOcrMessage = $"{row.ChildStage1Status} ONNX fallback recognized {result.ItemRowCount:N0} provisional item row(s) and {result.SummarySlabCount:N0} GST slab(s). R89 discrepancies need human review before import.";
            MobileLinkChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (_mobileInvoiceOcrCancellation.IsCancellationRequested)
        {
            row.DocumentOcrState = "NeedsAction";
            row.DocumentOcrMessage = "Windows document OCR was cancelled because Main Server is stopping.";
            row.DocumentOcrAuthority = "Cancelled";
        }
        catch (Exception exception)
        {
            row.DocumentOcrState = "NeedsAction";
            row.DocumentOcrMessage = "Windows document OCR failed: " + exception.Message;
            row.DocumentOcrAuthority = "Phone ML Kit preview only - confirmation blocked";
            MobileLinkChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _mobileInvoiceOcrInFlight.TryRemove(attemptKey, out _);
            MobileLinkChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string MergeMobileInvoiceCandidateJson(string originalJson, string documentJson)
    {
        JsonObject merged;
        try { merged = JsonNode.Parse(originalJson)?.AsObject() ?? new JsonObject(); }
        catch { merged = new JsonObject(); }
        try
        {
            var incoming = JsonNode.Parse(documentJson)?.AsObject();
            if (incoming is null) return merged.ToJsonString();
            foreach (var property in incoming)
            {
                if (property.Value is not JsonArray incomingArray) continue;
                var target = merged[property.Key] as JsonArray ?? new JsonArray();
                var seen = target.Select(x => x?["value"]?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in incomingArray)
                {
                    var value = candidate?["value"]?.ToString() ?? string.Empty;
                    if (value.Length > 0 && seen.Add(value)) target.Add(candidate?.DeepClone());
                }
                merged[property.Key] = target;
            }
        }
        catch { }
        return merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private IResult GetMobileLedgerMappings(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.LedgerMappingView)||Can(user,GovernancePermissions.LedgerMapping)||Can(user,GovernancePermissions.BankView)))return Results.Forbid();
        var pendingOnly=!string.Equals(context.Request.Query["all"].ToString(),"true",StringComparison.OrdinalIgnoreCase);
        var rows=_mobileAccounting.GetLedgerMappings(clientId,client.FinancialYear,pendingOnly);
        return Results.Json(new{clientId,financialYear=client.FinancialYear,pendingOnly,rows});
    }

    private async Task<IResult> SaveMobileLedgerMappingAsync(HttpContext context,long clientId,long mappingId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.LedgerMappingEdit)||Can(user,GovernancePermissions.LedgerMapping)||Can(user,GovernancePermissions.BankMap)))return Results.Forbid();
        MobileLedgerMappingRequest? request;try{request=await context.Request.ReadFromJsonAsync<MobileLedgerMappingRequest>(cancellationToken:context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid Ledger Mapping request."});}
        if(request is null)return Results.BadRequest(new{error="Ledger Mapping request is required."});
        try{var saved=_mobileAccounting.SaveLedgerMapping(clientId,client.FinancialYear,mappingId,request.LedgerName,request.MasterGroup);SafeLogEvent("MobileLedgerMapping",user.WindowsUserName,device.DeviceId,$"{clientId}/{client.FinancialYear}; MappingId={mappingId}",true);return Results.Json(saved);}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private IResult ExportMobileSuspense(HttpContext context,long clientId,string format)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.BankExport)||Can(user,GovernancePermissions.ExportData)))return Results.Forbid();
        var normalized=(format??string.Empty).Trim().ToLowerInvariant();if(normalized is not ("xlsx" or "pdf"))return Results.BadRequest(new{error="Suspense export format must be xlsx or pdf."});
        try{var export=_mobileAccounting.ExportSuspense(client,normalized);return Results.File(export.Bytes,export.ContentType,export.FileName);}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private IResult GetMobileCashEntries(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.ReadData)||Can(user,GovernancePermissions.BankView)))return Results.Forbid();
        return Results.Json(new{clientId,financialYear=client.FinancialYear,rows=_mobileAccounting.GetCashEntries(clientId,client.FinancialYear)});
    }

    private async Task<IResult> SaveMobileCashAsync(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.EditAccounting)||Can(user,GovernancePermissions.Import)))return Results.Forbid();
        MobileCashRequest? request;try{request=await context.Request.ReadFromJsonAsync<MobileCashRequest>(cancellationToken:context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid Cash Entry request."});}
        if(request is null)return Results.BadRequest(new{error="Cash Entry request is required."});
        try{var saved=_mobileAccounting.SaveManualCash(clientId,client.FinancialYear,request.EntryDate,request.EntryLedger,request.DrCr,request.Amount,request.CommonLedger,request.Narration,user.WindowsUserName);SafeLogEvent("MobileCashEntry",user.WindowsUserName,device.DeviceId,$"{clientId}/{client.FinancialYear}; {saved.Id}",true);return Results.Json(saved);}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private IResult GetMobileJournalEntries(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.JournalView)||Can(user,GovernancePermissions.ReadData)))return Results.Forbid();
        return Results.Json(new{clientId,financialYear=client.FinancialYear,rows=_mobileAccounting.GetJournalVouchers(clientId,client.FinancialYear)});
    }

    private async Task<IResult> SaveMobileJournalAsync(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.JournalCreate)||Can(user,GovernancePermissions.EditAccounting)))return Results.Forbid();
        MobileJournalRequest? request;try{request=await context.Request.ReadFromJsonAsync<MobileJournalRequest>(cancellationToken:context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid Journal request."});}
        if(request is null)return Results.BadRequest(new{error="Journal request is required."});
        try{var saved=_mobileAccounting.SaveManualJournal(clientId,client.FinancialYear,request.VoucherDate,request.DebitLedger,request.CreditLedger,request.Amount,request.Narration,request.Reference,user.WindowsUserName);SafeLogEvent("MobileJournalEntry",user.WindowsUserName,device.DeviceId,$"{clientId}/{client.FinancialYear}; {saved.PersistentId}",true);return Results.Json(saved);}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}
    }

    private IResult GetClientAutoImportPolicy(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.Import)||Can(user,GovernancePermissions.ManageClientAccess)))return Results.Forbid();
        return Results.Json(_autoImportPolicies.Get(clientId));
    }

    private async Task<IResult> SetClientAutoImportPolicyAsync(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.ManageClientAccess)||Can(user,GovernancePermissions.ManageServer)))return Results.Forbid();
        ClientAutoImportRequest? request;try{request=await context.Request.ReadFromJsonAsync<ClientAutoImportRequest>(cancellationToken:context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid Auto Import policy."});}
        if(request is null)return Results.BadRequest(new{error="Auto Import policy is required."});
        var saved=_autoImportPolicies.Set(clientId,request.Enabled);SafeLogEvent("ClientAutoImportPolicy",user.WindowsUserName,device.DeviceId,$"Client={clientId}; Enabled={saved.Enabled}",true);return Results.Json(saved);
    }

    private IResult GetClientTallyLinkFoundation(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!(Can(user,GovernancePermissions.TallyView)||Can(user,GovernancePermissions.ManageIntegrations)))return Results.Forbid();
        var link=_tallyLinks.Get(clientId);return Results.Json(new{clientId,linked=link?.Validated==true,autoOpenEnabled=link?.AutoOpenEnabled==true,companyName=link?.CompanyName??string.Empty,dataPath=link?.DataPath??string.Empty,hostDeviceId=link?.HostDeviceId??string.Empty,backupState=link?.BackupState??string.Empty,backupProductFamily=link?.BackupProductFamily??string.Empty,backupClassificationEvidence=link?.BackupClassificationEvidence??string.Empty,lastValidatedUtc=link?.LastValidatedUtc});
    }

    private IResult GetDistributedImportJobs(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        if(!Can(user,GovernancePermissions.Import))return Results.Forbid();
        var jobs=_distributedJobs.ClaimPending(device.DeviceId,user.WindowsUserName,25)
            .Where(job=>{var c=_clients.GetById(job.ClientId);return c is not null&&!c.IsArchived&&NetworkHasClientAccess(user,device,job.ClientId,job.FinancialYear);})
            .Select(job=>new{job.JobId,job.ClientId,job.FinancialYear,job.Module,job.OriginalName,job.Sha256,job.FileSize,job.Source,job.RequestedBy,job.SourceDeviceId,job.RequestedExecutionDeviceId,job.SharedContextPresent,job.PasswordCandidateCount,passwordCandidates=UnprotectPasswordCandidates(job.ProtectedPasswordCandidates),job.Status,job.ClaimedUtc,job.CreatedUtc}).ToArray();
        return Results.Json(new{jobs});
    }

    private IResult GetAndroidImportJobStatus(HttpContext context,string jobId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var job=_distributedJobs.Find(jobId);if(job is null)return Results.NotFound();
        var ownsJob=job.SourceDeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&job.RequestedBy.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase);
        var isAdministrator=user.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase);
        if(!ownsJob&&!isAdministrator)return Results.Forbid();
        var client=_clients.GetById(job.ClientId);if(client is null||(!isAdministrator&&!NetworkHasClientAccess(user,device,job.ClientId,job.FinancialYear)))return Results.Forbid();
        var executionHost=job.RequestedExecutionDeviceId.Equals("MAIN_SERVER",StringComparison.OrdinalIgnoreCase)||string.IsNullOrWhiteSpace(job.RequestedExecutionDeviceId)
            ? "Main Server"
            : (_serverSecurity.FindDeviceById(job.RequestedExecutionDeviceId)?.DeviceName??"Windows processing node");
        var serverRetained=!string.IsNullOrWhiteSpace(job.StoredPath)&&File.Exists(job.StoredPath);
        var bankingNeedsCommitProof=job.Module.Equals(DistributedImportModules.Banking,StringComparison.OrdinalIgnoreCase);
        var legacyUnverifiedCompletion=bankingNeedsCommitProof&&job.Status.Equals("Completed",StringComparison.OrdinalIgnoreCase)&&!job.CommitVerified;
        var effectiveStatus=legacyUnverifiedCompletion?"Review Required":job.Status;
        var effectiveIssueCode=legacyUnverifiedCompletion&&string.IsNullOrWhiteSpace(job.IssueCode)?"BANK_COMMIT_NOT_VERIFIED":job.IssueCode;
        var effectiveDetail=legacyUnverifiedCompletion&&string.IsNullOrWhiteSpace(job.ResultDetail)
            ? "This Banking job was marked Completed by an older server path, but no authoritative commit proof was recorded. Re-submit the file after the Main Server update."
            : job.ResultDetail;
        var serverStage=effectiveStatus.Equals("Completed",StringComparison.OrdinalIgnoreCase)
            ? (job.CommitVerified?"Authoritative commit verified":"Completed")
            : effectiveStatus.Equals("Awaiting Main Server",StringComparison.OrdinalIgnoreCase)?"Waiting for Main Server authoritative replay"
            : effectiveStatus.Equals("Assigned",StringComparison.OrdinalIgnoreCase)?"Windows processing node assigned"
            : effectiveStatus.Equals("Failed",StringComparison.OrdinalIgnoreCase)||effectiveStatus.Equals("Review Required",StringComparison.OrdinalIgnoreCase)?"Import failed / review required"
            : effectiveStatus.Equals("Cancelled",StringComparison.OrdinalIgnoreCase)?"Cancelled before authoritative commit"
            : "Queued for accounting import";
        return Results.Json(new{job.JobId,job.ClientId,job.FinancialYear,job.Module,job.OriginalName,status=effectiveStatus,detail=effectiveDetail,executionHost,job.RequestedExecutionDeviceId,job.ParentJobId,job.AuthoritativeJobId,job.Sha256,job.FileSize,serverRetained,serverStage,job.CommitVerified,job.CommittedRecordCount,issueCode=effectiveIssueCode,job.ClaimedUtc,job.CreatedUtc,job.UpdatedUtc});
    }

    private Task<IResult> CancelAndroidImportJobAsync(HttpContext context,string jobId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Task.FromResult<IResult>(Results.Unauthorized());
        var job=_distributedJobs.Find(jobId);if(job is null)return Task.FromResult<IResult>(Results.NotFound());
        var ownsJob=job.SourceDeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&job.RequestedBy.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase);
        var isAdministrator=user.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase);
        if(!ownsJob&&!isAdministrator)return Task.FromResult<IResult>(Results.Forbid());
        var client=_clients.GetById(job.ClientId);
        if(client is null||(!isAdministrator&&!NetworkHasClientAccess(user,device,job.ClientId,job.FinancialYear)))return Task.FromResult<IResult>(Results.Forbid());
        if(job.CommitVerified||job.Status.Equals("Completed",StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IResult>(Results.Conflict(new{error="This import is already committed and cannot be cancelled."}));

        var updated=_distributedJobs.Cancel(job.JobId,"Import cancelled from Android before authoritative commit.");
        if(updated is null)return Task.FromResult<IResult>(Results.NotFound());
        if(!string.IsNullOrWhiteSpace(job.AuthoritativeJobId)) _distributedJobs.Cancel(job.AuthoritativeJobId,"Parent Android import was cancelled before authoritative commit.");
        try { DistributedImportJobCancelled?.Invoke(job.JobId); } catch { }
        if(!string.IsNullOrWhiteSpace(job.AuthoritativeJobId)) try { DistributedImportJobCancelled?.Invoke(job.AuthoritativeJobId); } catch { }
        SafeLogEvent("DistributedImportCancel",user.WindowsUserName,device.DeviceId,$"{job.JobId}; {job.Module}; {job.OriginalName}",true);
        return Task.FromResult<IResult>(Results.Json(new{job=updated}));
    }

    private IResult DownloadDistributedImportJobFile(HttpContext context,string jobId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)||!Can(user,GovernancePermissions.Import))return Results.Forbid();
        var job=_distributedJobs.Find(jobId);if(job is null)return Results.NotFound();
        if(string.IsNullOrWhiteSpace(job.RequestedExecutionDeviceId)||!job.RequestedExecutionDeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        var client=_clients.GetById(job.ClientId);if(client is null||!NetworkHasClientAccess(user,device,job.ClientId,job.FinancialYear))return Results.Forbid();
        if(!File.Exists(job.StoredPath))return Results.NotFound(new{error="The retained import source file is unavailable."});
        return Results.File(job.StoredPath,"application/octet-stream",Path.GetFileName(job.OriginalName),enableRangeProcessing:false);
    }

    private async Task<IResult> CompleteDistributedImportJobAsync(HttpContext context,string jobId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)||!Can(user,GovernancePermissions.Import))return Results.Forbid();
        var job=_distributedJobs.Find(jobId);if(job is null)return Results.NotFound();
        if(job.Status.Equals("Cancelled",StringComparison.OrdinalIgnoreCase))return Results.Conflict(new{error="This import job was cancelled and cannot be completed."});
        if(string.IsNullOrWhiteSpace(job.RequestedExecutionDeviceId)||!job.RequestedExecutionDeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        DistributedJobCompletionRequest? request;try{request=await context.Request.ReadFromJsonAsync<DistributedJobCompletionRequest>(cancellationToken:context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid import-job completion."});}
        if(request is null)return Results.BadRequest(new{error="Import-job completion is required."});
        DistributedImportJobRecord? updated;
        string replayJobId=string.Empty;
        if(request.Success&&job.Source.Equals("Android",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(job.RequestedExecutionDeviceId)&&!job.RequestedExecutionDeviceId.Equals("MAIN_SERVER",StringComparison.OrdinalIgnoreCase))
        {
            // A Windows processing node is only an execution host. Android must not be told
            // Completed until the retained source is replayed into and verified against the
            // authoritative Main Server accounting repository.
            var replay=_distributedJobs.Enqueue(job with
            {
                JobId=Guid.NewGuid().ToString("N"),
                ParentJobId=job.JobId,
                AuthoritativeJobId=string.Empty,
                Source="WindowsNodeResultReplay",
                SourceDeviceId=device.DeviceId,
                RequestedExecutionDeviceId="MAIN_SERVER",
                Status="Pending",
                ResultDetail=string.Empty,
                CommitVerified=false,
                CommittedRecordCount=0,
                IssueCode=string.Empty,
                ClaimedByDeviceId=string.Empty,
                ClaimedUtc=null,
                CreatedUtc=DateTime.UtcNow,
                UpdatedUtc=DateTime.UtcNow
            });
            replayJobId=replay.JobId;
            updated=_distributedJobs.Update(job with
            {
                Status="Awaiting Main Server",
                ResultDetail="Windows processing completed. Waiting for Main Server authoritative commit verification.",
                AuthoritativeJobId=replayJobId,
                CommitVerified=false,
                CommittedRecordCount=0,
                IssueCode=string.Empty
            });
            try { DistributedImportJobQueued?.Invoke(replay); } catch(Exception replayError) { SafeLogEvent("DistributedImportReplay",user.WindowsUserName,device.DeviceId,$"{replay.JobId}; {replayError.Message}",false); }
        }
        else
        {
            updated=_distributedJobs.Complete(jobId,request.Success,request.Detail,false,0,request.Success?string.Empty:"WINDOWS_NODE_IMPORT_FAILED");
        }
        SafeLogEvent("DistributedImportJob",user.WindowsUserName,device.DeviceId,$"{jobId}; Success={request.Success}; {request.Detail}; Replay={replayJobId}",request.Success);
        return Results.Json(new{job=updated,replayJobId});
    }

    private IResult GetRegistrySyncSnapshot(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)||!Can(user,GovernancePermissions.Import))return Results.Forbid();
        return Results.Json(new{revision=_registrySync.Revision,entries=_registrySync.SnapshotAll()});
    }

    private async Task<IResult> MergeRegistryDeltasAsync(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)||!Can(user,GovernancePermissions.Import))return Results.Forbid();
        RegistryDeltaBatchRequest? request;try{request=await context.Request.ReadFromJsonAsync<RegistryDeltaBatchRequest>(cancellationToken:context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid registry synchronization payload."});}
        var safe=(request?.Deltas??[]).Where(x=>x.SourceDeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)||string.IsNullOrWhiteSpace(x.SourceDeviceId)).Take(1000)
            .Select(x=>x with{SourceDeviceId=device.DeviceId}).ToArray();
        var result=_registrySync.MergeAuthoritative(safe);SafeLogEvent("ImportRegistrySync",user.WindowsUserName,device.DeviceId,$"Applied={result.Applied}; Identical={result.Identical}; Conflicts={result.Conflicts}; Revision={result.Revision}",result.Conflicts==0);
        if(result.Conflicts>0)
            NotificationHistoryService.Record(string.Empty,"Import Registry","Action Required","Import registry conflict requires Administrator review",$"{result.Conflicts} conflicting learned/reference registry change(s) from {device.DeviceName} ({device.DeviceId}) were retained without overwriting the Main Server. Open Notifications to Keep Main or Accept Windows Change.");
        return Results.Json(result);
    }

    private IResult GetAutoImportPoliciesForNode(HttpContext context)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        var clients=_readModel!.GetClientsForUser(user.WindowsUserName,(u,c,fy)=>NetworkHasClientAccess(user,device,c,fy));
        var policies=clients.Select(c=>_autoImportPolicies.Get(c.Id)).OrderBy(x=>x.ClientId).ToArray();
        return Results.Json(policies);
    }

    private async Task<IResult> ReceiveCompletedNodeImportAsync(HttpContext context,long clientId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)||!Can(user,GovernancePermissions.Import))return Results.Forbid();
        var client=_clients.GetById(clientId);if(client is null||client.IsArchived)return Results.NotFound();
        if(!NetworkHasClientAccess(user,device,clientId,client.FinancialYear))return Results.Forbid();
        if(!context.Request.HasFormContentType)return Results.BadRequest(new{error="A completed Windows-node import source must be submitted as multipart form data."});
        IFormCollection form;try{form=await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="The import-source synchronization form could not be read."});}
        var file=form.Files.GetFile("file");if(file is null||file.Length<=0)return Results.BadRequest(new{error="The completed import source file is required."});
        var financialYear=form["financialYear"].ToString().Trim();if(!client.FinancialYear.Equals(financialYear,StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="The synchronized source Financial Year does not match the selected Client."});
        var module=form["module"].ToString().Trim();if(!DistributedImportModules.IsAllowed(module)||module.Equals(DistributedImportModules.TallyBackup,StringComparison.OrdinalIgnoreCase)||module.Equals(DistributedImportModules.DocumentsOnly,StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="The synchronized source module is not eligible for accounting replay."});
        if(_storage is null)return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        ServerArchivedFileRecord archived;
        await using(var input=file.OpenReadStream()) archived=await _storage.ArchiveUploadAsync(clientId,financialYear,file.FileName,input,device.DeviceId,user.WindowsUserName,context.RequestAborted,forwardToAutomationFolder:false).ConfigureAwait(false);
        var storedPath=archived.StoredPath;
        if(string.IsNullOrWhiteSpace(storedPath))
        {
            var existing=_serverSecurity.GetArchivedFile(clientId,financialYear,"Upload",archived.Sha256);
            storedPath=existing?.StoredPath??string.Empty;
        }
        if(string.IsNullOrWhiteSpace(storedPath)||!File.Exists(storedPath))return Results.Conflict(new{error="The Main Server retained the source identity but could not locate its archived file for replay."});
        var existingJob=_distributedJobs.ReadAll().FirstOrDefault(x=>x.Source.Equals("WindowsNodeSync",StringComparison.OrdinalIgnoreCase)&&x.ClientId==clientId&&x.FinancialYear.Equals(financialYear,StringComparison.OrdinalIgnoreCase)&&x.Sha256.Equals(archived.Sha256,StringComparison.OrdinalIgnoreCase)&&!x.Status.Equals("Failed",StringComparison.OrdinalIgnoreCase));
        if(existingJob is not null)return Results.Json(new{accepted=true,jobId=existingJob.JobId,detail=$"Source already retained for Main Server replay ({existingJob.Status})."});
        var job=_distributedJobs.Enqueue(new DistributedImportJobRecord
        {
            ClientId=clientId,FinancialYear=financialYear,Module=module,Source="WindowsNodeSync",OriginalName=Path.GetFileName(file.FileName),StoredPath=storedPath,Sha256=archived.Sha256,FileSize=archived.FileSize,RequestedBy=user.WindowsUserName,SourceDeviceId=device.DeviceId,RequestedExecutionDeviceId="MAIN_SERVER",Status="Pending"
        });
        SafeLogEvent("WindowsNodeImportSync",user.WindowsUserName,device.DeviceId,$"Client={clientId}; Module={module}; Job={job.JobId}; Hash={job.Sha256}",true);
        DistributedImportJobQueued?.Invoke(job);
        return Results.Json(new{accepted=true,jobId=job.JobId,detail="Main Server retained the completed Windows-node source and queued authoritative replay."});
    }

    private IResult GetCompletedNodeImportStatus(HttpContext context,string jobId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        if(!device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        var job=_distributedJobs.Find(jobId);if(job is null)return Results.NotFound();
        if(!job.Source.Equals("WindowsNodeSync",StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        if(!job.SourceDeviceId.Equals(device.DeviceId,StringComparison.OrdinalIgnoreCase)&&!job.RequestedBy.Equals(user.WindowsUserName,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
        return Results.Json(new{jobId=job.JobId,status=job.Status,completed=job.Status.Equals("Completed",StringComparison.OrdinalIgnoreCase),failed=job.Status.Equals("Failed",StringComparison.OrdinalIgnoreCase),detail=job.ResultDetail,updatedUtc=job.UpdatedUtc});
    }

    private IResult GetMappingSession(HttpContext context,string sessionId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var mapping=_mappingBroker.Get(sessionId);if(mapping is null)return Results.NotFound();
        var client=_clients.GetById(mapping.ClientId);if(client is null||!NetworkHasClientAccess(user,device,mapping.ClientId,mapping.FinancialYear))return Results.Forbid();
        return Results.Json(mapping);
    }

    private async Task<IResult> ClaimMappingSessionAsync(HttpContext context,string sessionId)
    {
        var session=AuthenticateSession(context,out var user,out var device);if(session is null||user is null||device is null)return Results.Unauthorized();
        var mapping=_mappingBroker.Get(sessionId);if(mapping is null)return Results.NotFound();
        var client=_clients.GetById(mapping.ClientId);if(client is null||!NetworkHasClientAccess(user,device,mapping.ClientId,mapping.FinancialYear))return Results.Forbid();
        MappingClaimRequest? request;try{request=await context.Request.ReadFromJsonAsync<MappingClaimRequest>(cancellationToken:context.RequestAborted).ConfigureAwait(false);}catch{return Results.BadRequest(new{error="Invalid mapping-session claim."});}
        var presenter=(request?.Presenter??device.DeviceType).Trim();
        var claimed=(request?.Transfer??false)?_mappingBroker.Transfer(sessionId,presenter,device.DeviceId):_mappingBroker.Claim(sessionId,presenter,device.DeviceId);
        return claimed is null?Results.Conflict(new{error="This mapping session is currently presented on another authorized device. Transfer it to continue here."}):Results.Json(claimed);
    }

    private string[] UnprotectPasswordCandidates(string protectedCandidates)
    {
        if(string.IsNullOrWhiteSpace(protectedCandidates))return [];
        try{return (JsonSerializer.Deserialize<string[]>(_secretProtector.Unprotect(protectedCandidates))??[]).Take(12).ToArray();}catch{return [];}
    }

    private async Task<IResult> ReceiveUploadAsync(HttpContext context,long clientId,string financialYear,ServerDeviceRecord device,string userName)
    {
        if(!context.Request.HasFormContentType)return Results.BadRequest(new{error="Upload must use multipart/form-data with one file."});
        IFormCollection form;try{form=await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);}catch(Exception ex){return Results.BadRequest(new{error=$"Upload could not be read: {ex.Message}"});}
        if(form.Files.Count!=1)return Results.BadRequest(new{error="Upload exactly one file per request."});var file=form.Files[0];
        var error=ValidateUploadName(file.FileName);if(error.Length>0)return Results.BadRequest(new{error});

        var source=(form["source"].ToString()??string.Empty).Trim();
        var isAndroid=source.Equals("Android",StringComparison.OrdinalIgnoreCase)||device.DeviceType.Equals(ServerDeviceTypes.Android,StringComparison.OrdinalIgnoreCase);
        var requestedModule=(form["module"].ToString()??string.Empty).Trim();
        if(isAndroid&&!DistributedImportModules.IsAllowed(requestedModule))
            return Results.BadRequest(new{error="Choose the AAH import module before uploading from Android. The server will not auto-detect the accounting module."});
        var module=DistributedImportModules.IsAllowed(requestedModule)?DistributedImportModules.Allowed.First(x=>x.Equals(requestedModule,StringComparison.OrdinalIgnoreCase)):DistributedImportModules.DocumentsOnly;
        string[] passwordCandidates=[];
        try
        {
            var raw=form["passwordCandidatesJson"].ToString();
            if(!string.IsNullOrWhiteSpace(raw)) passwordCandidates=(JsonSerializer.Deserialize<string[]>(raw)??[])
                .Select(x=>(x??string.Empty).Trim()).Where(x=>x.Length is >0 and <=64).Distinct(StringComparer.Ordinal).Take(12).ToArray();
        }
        catch { passwordCandidates=[]; }
        var sharedContextPresent=bool.TryParse(form["sharedContextPresent"].ToString(),out var shared)&&shared;

        try
        {
            // Android/manual module uploads must never enter the generic Automatic Folder classifier.
            // Their chosen module is preserved as durable import-job metadata instead.
            await using var input=file.OpenReadStream();
            var archived=await _storage!.ArchiveUploadAsync(clientId,financialYear,file.FileName,input,device.DeviceId,userName,context.RequestAborted,forwardToAutomationFolder:!isAndroid).ConfigureAwait(false);
            var duplicate=string.IsNullOrWhiteSpace(archived.StoredPath);
            string jobId=string.Empty;string executionDeviceId=string.Empty;
            var serverRetained=!duplicate&&!string.IsNullOrWhiteSpace(archived.StoredPath)&&File.Exists(archived.StoredPath);
            var uploadIssueCode=string.Empty;
            if(isAndroid)
            {
                // A repeated Android Share/Select commonly uploads the exact same retained source after
                // an earlier job failed or the app was closed before submission completed. Storage-level
                // deduplication must never turn that retry into "stored only" with no Import Job.
                var retained=duplicate?_serverSecurity.GetArchivedFile(clientId,financialYear,"Upload",archived.Sha256):archived;
                serverRetained=retained is not null&&!string.IsNullOrWhiteSpace(retained.StoredPath)&&File.Exists(retained.StoredPath);
                if(!serverRetained) uploadIssueCode="SERVER_RETAINED_FILE_MISSING";
                var existingJob=_distributedJobs.ReadAll().FirstOrDefault(x=>
                    x.Source.Equals("Android",StringComparison.OrdinalIgnoreCase)&&
                    x.ClientId==clientId&&x.FinancialYear.Equals(financialYear,StringComparison.OrdinalIgnoreCase)&&
                    x.Module.Equals(module,StringComparison.OrdinalIgnoreCase)&&
                    x.Sha256.Equals(archived.Sha256,StringComparison.OrdinalIgnoreCase)&&
                    (x.Status.Equals("Pending",StringComparison.OrdinalIgnoreCase)||
                     x.Status.Equals("Assigned",StringComparison.OrdinalIgnoreCase)||
                     x.Status.Equals("Processing",StringComparison.OrdinalIgnoreCase)||
                     x.Status.Equals("Awaiting Main Server",StringComparison.OrdinalIgnoreCase)));
                if(existingJob is not null)
                {
                    jobId=existingJob.JobId;
                    executionDeviceId=existingJob.RequestedExecutionDeviceId;
                }
                else if(retained is not null&&!string.IsNullOrWhiteSpace(retained.StoredPath)&&File.Exists(retained.StoredPath))
                {
                    executionDeviceId=SelectPreferredWindowsExecutionDevice(userName,clientId,financialYear);
                    var protectedCandidates=passwordCandidates.Length==0?string.Empty:_secretProtector.Protect(JsonSerializer.Serialize(passwordCandidates));
                    var job=_distributedJobs.Enqueue(new DistributedImportJobRecord
                    {
                        ClientId=clientId,FinancialYear=financialYear,Module=module,Source="Android",OriginalName=file.FileName,StoredPath=retained.StoredPath,Sha256=archived.Sha256,FileSize=archived.FileSize,
                        RequestedBy=userName,SourceDeviceId=device.DeviceId,RequestedExecutionDeviceId=executionDeviceId,ProtectedPasswordCandidates=protectedCandidates,PasswordCandidateCount=passwordCandidates.Length,SharedContextPresent=sharedContextPresent
                    });
                    jobId=job.JobId;
                    if(string.IsNullOrWhiteSpace(executionDeviceId))
                    {
                        try { DistributedImportJobQueued?.Invoke(job); }
                        catch(Exception eventError) { SafeLogEvent("DistributedImportDispatch",userName,device.DeviceId,$"Main Server fallback dispatch failed: {eventError.Message}",false); }
                    }
                    else ScheduleDistributedImportFallback(job);
                }
            }
            if(!duplicate&&module.Equals(DistributedImportModules.TallyBackup,StringComparison.OrdinalIgnoreCase))
            {
                var classification=TallyBackupClassifier.Classify(archived.StoredPath,file.FileName);
                var backupState=$"Uploaded {classification.StatusLabel} - pending validation/link: {Path.GetFileName(archived.StoredPath)}";
                var existing=_tallyLinks.Get(clientId);
                _tallyLinks.Save(existing is null
                    ? new ClientTallyLinkRecord { ClientId=clientId, BackupState=backupState, BackupProductFamily=classification.ProductFamily, BackupClassificationEvidence=classification.Evidence }
                    : existing with { BackupState=backupState, BackupProductFamily=classification.ProductFamily, BackupClassificationEvidence=classification.Evidence, Validated=false, LastValidatedUtc=null });
            }
            var uploadAccepted=!isAndroid||serverRetained;
            var serverStage=!uploadAccepted?"File Server retention failed":jobId.Length>0?"Queued for accounting import":"Stored on Main Server";
            var serverStatus=!uploadAccepted?"Review Required":jobId.Length>0?"Queued":duplicate?"Duplicate retained":"Stored";
            _serverSecurity.LogEvent("RemoteUpload",userName,device.DeviceId,$"{clientId}/{financialYear}; {file.FileName}; Module={module}; Status={serverStatus}; Retained={serverRetained}; Job={jobId}; Issue={uploadIssueCode}",uploadAccepted);
            var uploadDetail=!uploadAccepted
                ? "The Main Server could not verify a retained File Server copy, so no accounting import was accepted."
                : jobId.Length>0
                    ? "The File Server retained the source and created a fresh accounting import job. Completion will be reported only after authoritative commit verification."
                    : "The File Server retained the source.";
            return Results.Json(new{accepted=uploadAccepted,uploadAccepted,duplicate,jobId,module,requestedExecutionDeviceId=executionDeviceId,archivedName=duplicate?file.FileName:Path.GetFileName(archived.StoredPath),sha256=archived.Sha256,fileSize=archived.FileSize,serverRetained,serverStage,serverStatus,commitVerified=false,committedRecordCount=0,issueCode=uploadIssueCode,detail=uploadDetail,localDeletionDoesNotDeleteServerCopy=serverRetained,moduleWasExplicit=isAndroid});
        }
        catch(Exception ex){_serverSecurity.LogEvent("RemoteUpload",userName,device.DeviceId,ex.Message,false);return Results.BadRequest(new{error=ex.Message});}
    }

    private void ScheduleDistributedImportFallback(DistributedImportJobRecord job)
    {
        _=Task.Run(async()=>
        {
            try
            {
                // Heartbeat alone does not prove that a Windows node is actually running the
                // distributed-import agent. If the job is not claimed promptly, send the same
                // retained source to Main Server instead of leaving Android stuck at Queued.
                await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                var current=_distributedJobs.Find(job.JobId);
                if(current is null||current.Status is "Completed" or "Failed"||string.IsNullOrWhiteSpace(current.RequestedExecutionDeviceId)||current.RequestedExecutionDeviceId.Equals("MAIN_SERVER",StringComparison.OrdinalIgnoreCase))return;
                if(current.Status.Equals("Assigned",StringComparison.OrdinalIgnoreCase)&&current.ClaimedUtc.HasValue)
                {
                    // A claimed job may legitimately take time for OCR/import. Only fail it over
                    // later if the processing node itself has gone offline; never run two copies
                    // concurrently while that node is still alive.
                    await Task.Delay(TimeSpan.FromMinutes(8)).ConfigureAwait(false);
                    current=_distributedJobs.Find(job.JobId);
                    if(current is null||current.Status is "Completed" or "Failed"||current.RequestedExecutionDeviceId.Equals("MAIN_SERVER",StringComparison.OrdinalIgnoreCase))return;
                    var claimedDevice=_serverSecurity.FindDeviceById(current.RequestedExecutionDeviceId);
                    if(claimedDevice is not null&&claimedDevice.IsActive&&claimedDevice.LastSeenUtc.HasValue&&claimedDevice.LastSeenUtc.Value>=DateTime.UtcNow.AddMinutes(-2))return;
                }
                var fallback=_distributedJobs.Enqueue(current with
                {
                    RequestedExecutionDeviceId="MAIN_SERVER",Status="Pending",ClaimedByDeviceId=string.Empty,ClaimedUtc=null,ResultDetail="",UpdatedUtc=DateTime.UtcNow
                });
                SafeLogEvent("DistributedImportFallback",fallback.RequestedBy,fallback.SourceDeviceId,$"{fallback.JobId}; assigned Windows node did not claim the job; Main Server fallback queued.",true);
                DistributedImportJobQueued?.Invoke(fallback);
            }
            catch(Exception ex){SafeLogEvent("DistributedImportFallback",job.RequestedBy,job.SourceDeviceId,$"{job.JobId}; {ex.Message}",false);}
        });
    }

    private string SelectPreferredWindowsExecutionDevice(string userName,long clientId,string financialYear)
    {
        var candidate=_serverSecurity.GetServerDevices()
            .Where(x=>x.IsActive&&x.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&x.UserName.Equals(userName,StringComparison.OrdinalIgnoreCase))
            .Where(x=>x.LastSeenUtc.HasValue&&x.LastSeenUtc.Value>=DateTime.UtcNow.AddMinutes(-3))
            .Where(x=>IsDistributedImportCapableVersion(x.AppVersion))
            .OrderByDescending(x=>x.LastSeenUtc).FirstOrDefault();
        if(candidate is null)return string.Empty;
        var user=GetApplicationUser(userName);
        return user is not null&&NetworkHasClientAccess(user,candidate,clientId,financialYear)?candidate.DeviceId:string.Empty;
    }

    private static bool IsDistributedImportCapableVersion(string? appVersion)
    {
        var raw=(appVersion??string.Empty).Trim();
        var match=System.Text.RegularExpressions.Regex.Match(raw,@"\d+\.\d+\.\d+\.\d+");
        return match.Success&&Version.TryParse(match.Value,out var parsed)&&parsed.CompareTo(new Version(4,0,0,354))>=0;
    }

    private IResult DeviceRegistrationFailure(string? deviceId)
    {
        var normalized=(deviceId??string.Empty).Trim();
        var known=normalized.Length>0?_serverSecurity.FindDeviceById(normalized):null;
        if(known is null)
            return Results.Json(new{error="This device registration no longer exists on the Main Server. Enroll the device again with a fresh one-time enrollment grant.",code="DEVICE_REGISTRATION_NOT_FOUND",reEnrollmentRequired=true},statusCode:StatusCodes.Status401Unauthorized);
        return Results.Json(new{error="This device registration is no longer accepted by the Main Server. Enroll the device again after an Administrator issues a fresh one-time enrollment grant.",code="DEVICE_REGISTRATION_REJECTED",reEnrollmentRequired=true},statusCode:StatusCodes.Status401Unauthorized);
    }

    private NetworkSessionRecord? AuthenticateSession(HttpContext context,out ApplicationUserRecord? user,out ServerDeviceRecord? device)
    {
        user=null;device=null;var header=context.Request.Headers.Authorization.ToString();if(!header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))return null;
        var raw=header[7..].Trim();var session=_serverSecurity.FindSession(raw);if(session is null||session.ExpiresUtc<=DateTime.UtcNow){if(session is not null)_serverSecurity.DeleteSession(session.SessionHash);return null;}
        device=_serverSecurity.FindDeviceById(session.DeviceId);if(device is null||!ValidateDeviceLease(device,touch:true))return null;if(device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)&&!device.RegistrationOtpVerifiedUtc.HasValue)return null;user=GetApplicationUser(session.UserName);if(user is null||!user.IsActive)return null;
        if(device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)) session.ExpiresUtc=_serverSecurity.TouchSession(session.SessionHash,TimeSpan.FromDays(30)); else _serverSecurity.TouchSession(session.SessionHash);return session;
    }

    private ServerDeviceRecord? AuthenticateDeviceFromHeaders(HttpContext context,bool touch)
    {
        return AuthenticateDevice(context.Request.Headers["X-AAH-Device"].ToString(),context.Request.Headers["X-AAH-Device-Token"].ToString(),context.Request.Headers["X-AAH-App-Version"].ToString(),touch);
    }

    private ServerDeviceRecord? AuthenticateDevice(string deviceId,string rawToken,string appVersion,bool touch)
    {
        if(string.IsNullOrWhiteSpace(deviceId)||string.IsNullOrWhiteSpace(rawToken))return null;var device=_serverSecurity.FindDeviceByTokenHash(ServerSecurityRepository.HashOpaqueToken(rawToken));
        if(device is null||!device.DeviceId.Equals(deviceId.Trim(),StringComparison.OrdinalIgnoreCase))return null;if(!ValidateDeviceLease(device,touch))return null;
        if(touch){_serverSecurity.TouchDevice(device.Id,appVersion,validated:false);if(!string.IsNullOrWhiteSpace(appVersion))device.AppVersion=appVersion.Trim();}
        return device;
    }

    private bool ValidateDeviceLease(ServerDeviceRecord device,bool touch)
    {
        if(!device.IsActive||device.EnrollmentState.Equals(ServerEnrollmentStates.Revoked,StringComparison.OrdinalIgnoreCase)||device.EnrollmentState.Equals(ServerEnrollmentStates.Expired,StringComparison.OrdinalIgnoreCase))return false;
        var basis=device.LastSeenUtc??device.CreatedUtc;
        if(basis<DateTime.UtcNow.AddDays(-GetDeviceExpiryDays(device.DeviceType)))
        {
            if(device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase))
            {
                _serverSecurity.RequireRegistrationOtp(device.Id);
                device.RegistrationOtpVerifiedUtc=null;
                device.EnrollmentState=ServerEnrollmentStates.VerificationRequired;
            }
            else
            {
                _serverSecurity.ExpireDevice(device.Id);
                return false;
            }
        }
        if(touch)_serverSecurity.TouchDevice(device.Id,device.AppVersion,false);return true;
    }

    private bool NetworkHasClientAccess(ApplicationUserRecord user,ServerDeviceRecord device,long clientId,string financialYear)
    {
        if(!user.IsActive || !device.IsActive)return false;
        // Completion 21: Administrator is the Main Server authority and must never be locked out
        // by the ordinary-user Remote Available switch or an explicit Client/FY assignment.
        // Those two gates continue to apply to every non-Administrator account.
        if(user.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase))return true;
        if(!_governance.IsClientLanAvailable(clientId))return false;
        if(!_governance.HasExplicitClientAccess(user.WindowsUserName,clientId,financialYear))return false;
        return true;
    }

    private ApplicationUserRecord? GetApplicationUser(string userName)=>_governance.GetUsers().FirstOrDefault(x=>x.WindowsUserName.Equals(userName,StringComparison.OrdinalIgnoreCase));
    private static int GetMobileVerificationIntervalDays(ApplicationUserRecord user)=>Math.Clamp(user.MobileVerificationIntervalDays<=0?30:user.MobileVerificationIntervalDays,1,365);
    private bool Can(ApplicationUserRecord user,string permission)=>user.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase)||_governance.HasPermission(user.WindowsUserName,permission);
    private string[] GetEffectivePermissions(ApplicationUserRecord user) => GovernancePermissions.All.Where(permission => Can(user, permission)).ToArray();

    private void ExpireInactiveTrust()
    {
        var now=DateTime.UtcNow;
        foreach(var device in _serverSecurity.GetServerDevices())
        {
            if(!device.IsActive)continue;
            var basis=device.LastSeenUtc??device.CreatedUtc;
            if(basis<now.AddDays(-GetDeviceExpiryDays(device.DeviceType)))
            {
                if(device.DeviceType.Equals(ServerDeviceTypes.Windows,StringComparison.OrdinalIgnoreCase)) _serverSecurity.RequireRegistrationOtp(device.Id);
                else _serverSecurity.ExpireDevice(device.Id);
            }
        }
        foreach(var link in _serverSecurity.GetFolderLinks())
        {
            if(!link.IsActive)continue;var basis=link.LastSeenUtc??link.CreatedUtc;if(basis<now.AddDays(-GetFolderExpiryDays()))_serverSecurity.ExpireFolderLink(link.Id);
        }
    }

    private int GetDeviceExpiryDays(string deviceType)=>deviceType.Equals(ServerDeviceTypes.Android,StringComparison.OrdinalIgnoreCase)?Math.Clamp(_settings.GetInt(ApplicationSettingKeys.SecurityAndroidExpiryDays,15),1,365):30;
    private int GetFolderExpiryDays()=>Math.Clamp(_settings.GetInt(ApplicationSettingKeys.SecurityFolderLinkExpiryDays,15),1,365);

    private void StartBackgroundServerJobs()
    {
        EnsureAndroidUserQrCodes();
        _androidQrRotationTimer??=new Timer(_=>{try{EnsureAndroidUserQrCodes();}catch{}},null,TimeSpan.FromMinutes(1),TimeSpan.FromMinutes(1));
        _leaseTimer??=new Timer(_=>{try{ExpireInactiveTrust();}catch{}},null,TimeSpan.FromMinutes(5),TimeSpan.FromHours(1));
        _replicationTimer??=new Timer(async _=>{try{if(_storage is not null)await _storage.RunReplicationAsync().ConfigureAwait(false);}catch{}},null,TimeSpan.FromMinutes(2),TimeSpan.FromHours(6));
    }

    private bool AllowRequest(HttpContext context)
    {
        var key=context.Connection.RemoteIpAddress?.ToString()??"unknown";var now=DateTime.UtcNow;var window=_requestWindows.GetOrAdd(key,_=>new RequestWindow(now));
        lock(window)
        {
            if(now-window.WindowStartedUtc>TimeSpan.FromMinutes(1)){window.WindowStartedUtc=now;window.Count=0;}
            window.Count++;var limit=context.Request.Path.StartsWithSegments("/api/v1/auth")||context.Request.Path.StartsWithSegments("/api/v1/enroll")||context.Request.Path.StartsWithSegments("/api/v1/android/qr")?20:context.Request.Path.StartsWithSegments("/api/v1/remote/assist")?900:180;return window.Count<=limit;
        }
    }

    private void DemandPermission(string permission)
    {
        var current=_governance.GetCurrentUser();
        if(current.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase))return;
        if(!_governance.HasPermission(Environment.UserName,permission))throw new InvalidOperationException($"This operation requires the '{permission}' permission.");
    }

    private void DemandServerAdministrator()
    {
        if(!_governance.HasPermission(Environment.UserName,GovernancePermissions.ManageUsers)&&!_governance.HasPermission(Environment.UserName,GovernancePermissions.ManageServer))
            throw new InvalidOperationException("Only an Administrator/Manage Server user can perform this action.");
    }

    private void DemandPrimaryAdministrator()
    {
        var current=_governance.GetCurrentUser();
        if(!current.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Only the Main Server Administrator can change Remote Assist Auto Accept policy.");
    }

    private sealed class RemoteAssistRemoteStateRequest
    {
        public bool ShareMyAah { get; set; } = true;
        public bool AllowAdministratorControl { get; set; } = true;
    }

    private static string ValidateUploadName(string fileName)
    {
        var safe=Path.GetFileName(fileName??string.Empty);if(safe.Length==0)return "File name is required.";var ext=Path.GetExtension(safe).ToLowerInvariant();
        var blocked=new HashSet<string>(StringComparer.OrdinalIgnoreCase){".exe",".dll",".com",".bat",".cmd",".ps1",".psm1",".vbs",".js",".jse",".wsf",".msi",".msp",".scr",".lnk",".url",".hta",".reg",".py",".pyw",".jar",".apk"};
        if(blocked.Contains(ext))return "Executable/script/application packages cannot be uploaded into an accounting Automatic Folder.";
        var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase){".xlsx",".xlsm",".xls",".csv",".txt",".pdf",".json",".zip",".jpg",".jpeg",".png",".webp",".ofx",".qbo",".qif",".mt940",".xml"};
        return allowed.Contains(ext)?string.Empty:$"File type '{ext}' is not enabled for remote accounting upload.";
    }

    private static bool FixedEquals(string left,string right)
    {
        var a=Encoding.UTF8.GetBytes(left??string.Empty);var b=Encoding.UTF8.GetBytes((right??string.Empty).Trim());return a.Length==b.Length&&CryptographicOperations.FixedTimeEquals(a,b);
    }
    private WindowsUpdatePolicy GetWindowsUpdatePolicy(ServerDeviceRecord device, string reportedVersion = "")
    {
        if (!device.DeviceType.Equals(ServerDeviceTypes.Windows, StringComparison.OrdinalIgnoreCase)) return new WindowsUpdatePolicy(false,string.Empty,string.Empty,string.Empty,"Production",true,null);
        _storage?.RegisterConfiguredPackages(Endpoint, RemoteAssistInternetEndpoint);
        var package=_serverSecurity.GetPackage("Windows");
        var ringName=_enterpriseControl.GetDeviceUpdateRing(device.DeviceId);
        var ring=_enterpriseControl.GetDeploymentRing(ringName);
        var globalForcedVersion=_settings.Get(ApplicationSettingKeys.SecurityForceWindowsUpdateVersion).Trim();
        var currentVersion=string.IsNullOrWhiteSpace(reportedVersion)?device.AppVersion:reportedVersion.Trim();
        var packageVersion=package?.Version??string.Empty;
        var globalForced=globalForcedVersion.Length>0&&IsVersionOlder(currentVersion,globalForcedVersion);
        // An explicit Main Server Force Update is an emergency/global policy and deliberately overrides a paused deployment ring.
        var ringOfferedVersion=(ring.IsEnabled||globalForced)?packageVersion:string.Empty;
        var ringForced=ring.IsEnabled&&ring.ForceAfterUtc.HasValue&&ring.ForceAfterUtc.Value<=DateTime.UtcNow&&ringOfferedVersion.Length>0&&IsVersionOlder(currentVersion,ringOfferedVersion);
        var forcedVersion=globalForced?globalForcedVersion:ringForced?ringOfferedVersion:string.Empty;
        var offeredSha=(ring.IsEnabled||globalForced)?package?.Sha256??string.Empty:string.Empty;
        return new WindowsUpdatePolicy(globalForced||ringForced,forcedVersion,ringOfferedVersion,offeredSha,ring.RingName,ring.IsEnabled,ring.ForceAfterUtc);
    }

    private static bool IsVersionOlder(string current, string required)
    {
        if (Version.TryParse(current, out var currentVersion) && Version.TryParse(required, out var requiredVersion)) return currentVersion.CompareTo(requiredVersion) < 0;
        return !current.Equals(required, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePackageKind(string value)=>value.Equals("android",StringComparison.OrdinalIgnoreCase)?"Android":"Windows";

    private string GetServerCertificateQrPin()
    {
        if (!string.IsNullOrWhiteSpace(_activeServerCertificateQrPin)) return _activeServerCertificateQrPin;
        try
        {
            if (!File.Exists(PublicServerCertificatePath)) return string.Empty;
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(PublicServerCertificatePath);
            return ComputeCertificateQrPin(certificate);
        }
        catch { return string.Empty; }
    }

    private static string ComputeCertificateQrPin(X509Certificate2 certificate)
    {
        var hash = certificate.GetCertHash(HashAlgorithmName.SHA256);
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private string GetServerCaFingerprint()
    {
        try
        {
            if (!File.Exists(PublicCertificatePath)) return string.Empty;
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(PublicCertificatePath);
            return certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }
        catch { return string.Empty; }
    }

    // Fix137 R46: the Android QR pins the exact Main Server leaf certificate.
    // Adding or removing a VPN/overlay adapter must not rotate that identity while the configured primary
    // private address is unchanged. Prefer the configured certificate, then the oldest still-valid
    // AAH certificate for the same primary address. Only create a new identity when no safe prior
    // certificate can serve that primary address.
    private X509Certificate2 EnsureCertificate(IPAddress primaryBindAddress, IReadOnlyList<IPAddress> bindAddresses)
    {
        if (bindAddresses.Count == 0) throw new InvalidOperationException("AAH Main Server has no private HTTPS bind address.");
        var configuredThumbprint = _settings.Get(ApplicationSettingKeys.SecurityLanCertificateThumbprint).Replace(" ", string.Empty);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var caCertificate = EnsureLocalCertificateAuthority(store);

        static bool IsUsable(X509Certificate2 certificate) =>
            certificate.HasPrivateKey && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30);

        // Recover the oldest still-usable identity for this same primary address. This deliberately
        // runs before the currently configured thumbprint: older R42-R45 builds could rotate the
        // configured leaf merely because the listener set changed, leaving the phone pinned to the
        // earlier certificate that is still present in CurrentUser\My.
        var stable = store.Certificates.OfType<X509Certificate2>()
            .Where(certificate => IsUsable(certificate))
            .Where(certificate => certificate.Subject.Equals("CN=Accounting Automation Hub Main Server", StringComparison.OrdinalIgnoreCase))
            .Where(certificate => certificate.Issuer.Equals(caCertificate.Subject, StringComparison.OrdinalIgnoreCase))
            .Where(certificate => CertificateCoversAddress(certificate, primaryBindAddress))
            .OrderBy(certificate => certificate.NotBefore)
            .FirstOrDefault();
        if (stable is not null)
        {
            _settings.Set(ApplicationSettingKeys.SecurityLanCertificateThumbprint, stable.Thumbprint ?? configuredThumbprint);
            _settings.Set(ApplicationSettingKeys.SecurityLanCertificateBindAddress, primaryBindAddress.ToString());
            ExportPublicCertificates(caCertificate, stable);
            return stable;
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Accounting Automation Hub Main Server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var address in bindAddresses.Distinct()) san.AddIpAddress(address);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(san.Build());
        using var issued = request.Create(caCertificate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(rsa);
        var certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        store.Add(certificate);
        _settings.Set(ApplicationSettingKeys.SecurityLanCertificateThumbprint, certificate.Thumbprint ?? string.Empty);
        _settings.Set(ApplicationSettingKeys.SecurityLanCertificateBindAddress, primaryBindAddress.ToString());
        ExportPublicCertificates(caCertificate, certificate);
        return certificate;
    }

    private static bool CertificateCoversAddress(X509Certificate2 certificate, IPAddress address)
    {
        try
        {
            var san = certificate.Extensions["2.5.29.17"]?.Format(false) ?? string.Empty;
            return san.Contains(address.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private X509Certificate2 EnsureLocalCertificateAuthority(X509Store store)
    {
        var thumbprint=_settings.Get(ApplicationSettingKeys.SecurityLanCaCertificateThumbprint).Replace(" ",string.Empty);
        if(thumbprint.Length>0){var existing=store.Certificates.Find(X509FindType.FindByThumbprint,thumbprint,false).OfType<X509Certificate2>().FirstOrDefault(x=>x.HasPrivateKey&&x.NotAfter.ToUniversalTime()>DateTime.UtcNow.AddYears(1));if(existing is not null)return existing;}
        using var rsa=RSA.Create(3072);var request=new CertificateRequest("CN=Accounting Automation Hub Local Server Root CA",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign|X509KeyUsageFlags.DigitalSignature,true));request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey,false));
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(10));var certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),(string?)null,X509KeyStorageFlags.UserKeySet|X509KeyStorageFlags.PersistKeySet);store.Add(certificate);_settings.Set(ApplicationSettingKeys.SecurityLanCaCertificateThumbprint,certificate.Thumbprint??string.Empty);return certificate;
    }
    private void ExportPublicCertificates(X509Certificate2 caCertificate, X509Certificate2 serverCertificate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PublicCertificatePath)!);
        File.WriteAllBytes(PublicCertificatePath, caCertificate.Export(X509ContentType.Cert));
        File.WriteAllBytes(PublicServerCertificatePath, serverCertificate.Export(X509ContentType.Cert));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mobileInvoiceOcrCancellation.Cancel(); } catch { }
        CancelAutomaticStartRetry();
        _leaseTimer?.Dispose(); _leaseTimer = null;
        _androidQrRotationTimer?.Dispose(); _androidQrRotationTimer = null;
        _replicationTimer?.Dispose(); _replicationTimer = null;
        _dataReplicationDebounceTimer?.Dispose(); _dataReplicationDebounceTimer = null;

        var app = _application;
        _application = null;
        Endpoint = string.Empty;
        RemoteAssistInternetEndpoint = string.Empty;
        _activeServerCertificateQrPin = string.Empty;
        lock (_gate) _pairing = null;
        StatusText = "AAH Main Server is stopping.";
        NotifyRunningStateChanged();

        if (app is not null)
        {
            // Application exit must not hold the WPF dispatcher waiting for Kestrel clients to
            // drain. Signal a bounded graceful stop on a worker; the process owns the listener
            // and Windows will close any remaining socket when the process exits.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var stopBudget = new CancellationTokenSource(TimeSpan.FromMilliseconds(ApplicationExitStopBudgetMilliseconds));
                    await app.StopAsync(stopBudget.Token).ConfigureAwait(false);
                }
                catch { }

                try
                {
                    var disposeTask = app.DisposeAsync().AsTask();
                    await Task.WhenAny(disposeTask, Task.Delay(ApplicationExitStopBudgetMilliseconds)).ConfigureAwait(false);
                }
                catch { }
            });
        }
    }

    private sealed class PairingWindow
    {
        public PairingWindow(string code,DateTime expiresUtc,string boundUserName,string deviceType){Code=code;ExpiresUtc=expiresUtc;BoundUserName=boundUserName;DeviceType=deviceType;}
        public string Code{get;} public DateTime ExpiresUtc{get;} public string BoundUserName{get;} public string DeviceType{get;}
    }
    private sealed class AndroidLoginQrWindow
    {
        public AndroidLoginQrWindow(string code,DateTime expiresUtc,string boundUserName){Code=code;ExpiresUtc=expiresUtc;BoundUserName=boundUserName;}
        public string Code{get;} public DateTime ExpiresUtc{get;} public string BoundUserName{get;}
    }
    private sealed class AndroidUserQrWindow
    {
        public AndroidUserQrWindow(string code,DateTime expiresUtc,string boundUserName){Code=code;ExpiresUtc=expiresUtc;BoundUserName=boundUserName;}
        public string Code{get;} public DateTime ExpiresUtc{get;} public string BoundUserName{get;}
    }
    private sealed class RequestWindow(DateTime started){public DateTime WindowStartedUtc{get;set;}=started;public int Count{get;set;}}
    private sealed class EnrollmentRequest{public string EnrollmentCode{get;set;}=string.Empty;public string UserName{get;set;}=string.Empty;public string Password{get;set;}=string.Empty;public string TotpCode{get;set;}=string.Empty;public string DeviceName{get;set;}=string.Empty;public string DeviceType{get;set;}=ServerDeviceTypes.Windows;public string DeviceBindingKey{get;set;}=string.Empty;public string AppVersion{get;set;}=string.Empty;}
    private sealed class AndroidQrEnrollmentRequest{public string Code{get;set;}=string.Empty;public string DeviceName{get;set;}=string.Empty;public string DeviceBindingKey{get;set;}=string.Empty;public string AppVersion{get;set;}=string.Empty;}
    private sealed class AndroidQrLoginRequest{public string Code{get;set;}=string.Empty;public string DeviceId{get;set;}=string.Empty;public string DeviceToken{get;set;}=string.Empty;public string AppVersion{get;set;}=string.Empty;}
    private sealed class AndroidUserQrRequest{public string Code{get;set;}=string.Empty;public string DeviceId{get;set;}=string.Empty;public string DeviceToken{get;set;}=string.Empty;public string DeviceName{get;set;}=string.Empty;public string DeviceBindingKey{get;set;}=string.Empty;public string AppVersion{get;set;}=string.Empty;public bool RequireExistingDevice{get;set;}}
    private sealed class AndroidDeviceResumeRequest{public string DeviceId{get;set;}=string.Empty;public string DeviceToken{get;set;}=string.Empty;public string AppVersion{get;set;}=string.Empty;}
    private sealed class AndroidAdminUserUpdateRequest{public string DisplayName{get;set;}=string.Empty;public string RoleName{get;set;}=string.Empty;public bool IsActive{get;set;}=true;public int MobileVerificationIntervalDays{get;set;}=30;}
    private sealed class LoginRequest{public string DeviceId{get;set;}=string.Empty;public string DeviceToken{get;set;}=string.Empty;public string UserName{get;set;}=string.Empty;public string Password{get;set;}=string.Empty;public string TotpCode{get;set;}=string.Empty;public string AppVersion{get;set;}=string.Empty;}
    public sealed record NetworkMfaChallengeInfo(string ChallengeId,string UserName,string DeviceName,string NetworkScope,string Code,DateTime CreatedUtc,DateTime ExpiresUtc);
    public sealed class NetworkMfaChallengeEventArgs : EventArgs
    {
        public NetworkMfaChallengeEventArgs(string challengeId,string userName,string deviceName,string deviceType,string networkScope,string code,DateTime expiresUtc){ChallengeId=challengeId;UserName=userName;DeviceName=deviceName;DeviceType=deviceType;NetworkScope=networkScope;Code=code;ExpiresUtc=expiresUtc;}
        public string ChallengeId { get; } public string UserName { get; } public string DeviceName { get; } public string DeviceType { get; } public string NetworkScope { get; } public string Code { get; } public DateTime ExpiresUtc { get; }
    }
    public sealed class AndroidEnrollmentApprovalEventArgs : EventArgs
    {
        private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AndroidEnrollmentApprovalEventArgs(string requestId,string userName,string displayName,string deviceName,string remoteAddress,DateTime expiresUtc){RequestId=requestId;UserName=userName;DisplayName=displayName;DeviceName=deviceName;RemoteAddress=remoteAddress;ExpiresUtc=expiresUtc;}
        public string RequestId { get; } public string UserName { get; } public string DisplayName { get; } public string DeviceName { get; } public string RemoteAddress { get; } public DateTime ExpiresUtc { get; }
        public void Complete(bool approved)=>_decision.TrySetResult(approved);
        public async Task<bool> WaitForDecisionAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(TimeSpan.FromSeconds(Math.Max(1,(ExpiresUtc-DateTime.UtcNow).TotalSeconds)), timeout.Token);
            var completed = await Task.WhenAny(_decision.Task, delay).ConfigureAwait(false);
            if (completed != _decision.Task) return false;
            timeout.Cancel();
            return await _decision.Task.ConfigureAwait(false);
        }
    }
    private sealed record LoginChallenge(string ChallengeId,string UserName,string DeviceId,string DeviceName,string NetworkScope,string Code,DateTime CreatedUtc,DateTime ExpiresUtc,bool Used);

    private sealed class RemoteCommandCompletion{public bool Success{get;set;}public string Detail{get;set;}=string.Empty;}
    private sealed class ConversationReadRequest{public string ConversationKey{get;set;}=string.Empty;}
    private sealed class MessageTextRequest{public string Body{get;set;}=string.Empty;}
    private sealed class MessagePinRequest{public bool Pinned{get;set;}}
    private sealed class MessageReactionRequest{public string Reaction{get;set;}=string.Empty;}
    private sealed class UserStatusRequest{public string Availability{get;set;}="Available";public string StatusMessage{get;set;}=string.Empty;}
    private sealed class ApprovalDecisionRequest{public bool Approve{get;set;}public string Reason{get;set;}=string.Empty;}
    private sealed class FolderLinkRequest{public long ClientId{get;set;}public string LocalLabel{get;set;}=string.Empty;}
    public sealed record PairingCodeResult(string Code,DateTime ExpiresUtc,string BoundUserName,string DeviceType);
    public sealed record AndroidQrCodeResult(string Code,DateTime ExpiresUtc,string BoundUserName,string Purpose);
    public sealed record AndroidUserQrCodeResult(string Code,DateTime ExpiresUtc,string BoundUserName,string Payload,string Purpose);
    public sealed record DistributionLinkResult(string PackageKind,string Url,DateTime ExpiresUtc,string Sha256,string Version);
    public sealed record WindowsUpdatePublishResult(string Version,string Sha256,bool Forced);
    private sealed record WindowsUpdatePolicy(bool ForceUpdate,string ForcedVersion,string ApprovedVersion,string ApprovedSha256,string RingName,bool RingEnabled,DateTime? ForceAfterUtc);

    private sealed class MobileLedgerMappingRequest{public string LedgerName{get;set;}=string.Empty;public string MasterGroup{get;set;}=string.Empty;}
    private sealed class MobileCashRequest{public DateTime EntryDate{get;set;}public string EntryLedger{get;set;}=string.Empty;public string DrCr{get;set;}="Dr";public decimal Amount{get;set;}public string CommonLedger{get;set;}=string.Empty;public string Narration{get;set;}=string.Empty;}
    private sealed class MobileJournalRequest{public DateTime VoucherDate{get;set;}public string DebitLedger{get;set;}=string.Empty;public string CreditLedger{get;set;}=string.Empty;public decimal Amount{get;set;}public string Narration{get;set;}=string.Empty;public string Reference{get;set;}=string.Empty;}
    private sealed class ClientAutoImportRequest{public bool Enabled{get;set;}}
    private sealed class DistributedJobCompletionRequest{public bool Success{get;set;}public string Detail{get;set;}=string.Empty;}
    private sealed class RegistryDeltaBatchRequest{public List<RegistryDeltaRecord> Deltas{get;set;}=[];}
    private sealed class MappingClaimRequest{public string Presenter{get;set;}=string.Empty;public bool Transfer{get;set;}}
    private sealed record LoginChallengeRequest(string DeviceId,string DeviceToken,string UserName,string Password,string AppVersion);
    private sealed record ChallengeLoginRequest(string DeviceId,string DeviceToken,string ChallengeId,string OtpCode,string AppVersion);
    private sealed record HeartbeatContextRequest(long ClientId,string? FinancialYear,string? Module);
    private sealed record WorkTaskUpdateRequest(string? AssignedTo,string? Status,string? Priority,DateTime? DueUtc,string? Description);
}

public sealed class MobileLinkConfiguration
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    // Legacy JSON field retained only so older link.json files deserialize safely.
    // R46 R22 does not use it for identity or authorization.
    public string LinkedUserId { get; set; } = string.Empty;
    public bool ShowMappingHere { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class MobileLinkClientState
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string LinkedUserId { get; set; } = string.Empty;
    public bool ShowMappingHere { get; set; }
    public bool SameClientOnline { get; set; }
    public bool MobileOnline { get; set; }
    public string PresenceText { get; set; } = "Offline / Waiting";
    public string CurrentMobileModule { get; set; } = string.Empty;
    public DateTime? LastSeenUtc { get; set; }
    public int PendingCount { get; set; }
}

public sealed class MobileInvoiceMappedResult
{
    public string Direction { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime? InvoiceDate { get; set; }
    public string CounterpartyName { get; set; } = string.Empty;
    public string CounterpartyGstin { get; set; } = string.Empty;
    public decimal InvoiceTotal { get; set; }
    public decimal TaxableTotal { get; set; }
    public decimal CgstTotal { get; set; }
    public decimal SgstTotal { get; set; }
    public decimal IgstTotal { get; set; }
    public decimal CessTotal { get; set; }
    public decimal RoundOff { get; set; }
    public bool SourceDiscrepanciesReviewed { get; set; }
    public string SourceImageSha256 { get; set; } = string.Empty;
    public List<MobileInvoiceMappedLine> Lines { get; set; } = new();
}

public sealed class MobileInvoiceMappedLine
{
    public string Description { get; set; } = string.Empty;
    public string HsnSac { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public decimal Discount { get; set; }
    public decimal Taxable { get; set; }
    public decimal GstRate { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
    public decimal LineTotal { get; set; }
    public string StockItem { get; set; } = string.Empty;
}

public sealed class MobileInvoiceQueueItem
{
    public string ScanId { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Status { get; set; } = "Queued";
    public string InvoiceNumber { get; set; } = string.Empty;
    public string InvoiceDate { get; set; } = string.Empty;
    public string Counterparty { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string RecognitionMode { get; set; } = string.Empty;
    public string InvoiceTotal { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string RawOcr { get; set; } = string.Empty;
    public string PhoneRawOcr { get; set; } = string.Empty;
    public string DocumentOcrState { get; set; } = "Pending";
    public string DocumentOcrMessage { get; set; } = string.Empty;
    public string DocumentOcrAuthority { get; set; } = string.Empty;
    public DateTime? DocumentOcrCompletedUtc { get; set; }
    public string CandidatesJson { get; set; } = "{}";
    public string RelationshipsJson { get; set; } = "{}";
    public string EvidenceFolder { get; set; } = string.Empty;
    public string ChildStage1Status { get; set; } = "Not yet sent to Invoice OCR Child";
    public string ChildStage1Archive { get; set; } = "";
    public List<string> ImagePaths { get; set; } = new();
    public string DisplayName => $"{(InvoiceNumber.Length == 0 ? ScanId : InvoiceNumber)} · {Counterparty} · {InvoiceTotal}" + (DocumentOcrState.Equals("Ready", StringComparison.OrdinalIgnoreCase) ? string.Empty : $" · OCR {DocumentOcrState}");
}

