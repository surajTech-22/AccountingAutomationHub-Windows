namespace AccountingAutomationHub.Domain.Governance;

public static class DeviceHealthStates
{
    public const string Healthy = "Healthy";
    public const string Warning = "Warning";
    public const string Noncompliant = "Noncompliant";
    public const string Quarantined = "Quarantined";
}

public static class RemoteAahCommandStates
{
    public const string Pending = "Pending";
    public const string Delivered = "Delivered";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
    public const string Rejected = "Rejected";
}

public static class RemoteAahActions
{
    public const string RefreshSnapshot = "RefreshSnapshot";
    public const string RestartAah = "RestartAah";
    public const string ForceOpenAah = "ForceOpenAah";
    public const string CheckUpdate = "CheckUpdate";
    public const string InstallUpdate = "InstallUpdate";
    public const string CollectDiagnostics = "CollectDiagnostics";
    public const string ReloadPermissions = "ReloadPermissions";
    public const string ClearDisposableSnapshot = "ClearDisposableSnapshot";
    public const string SignOut = "SignOut";
    public const string TestMainServer = "TestMainServer";
    public const string ShowMessage = "ShowMessage";
    public const string RequestRemoteAssist = "RequestRemoteAssist";
    public const string RemoteAssistNavigate = "RemoteAssistNavigate";
    public const string RemoteAssistSetLocalPreference = "RemoteAssistSetLocalPreference";
    public const string RemoteAssistEnd = "RemoteAssistEnd";

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        RefreshSnapshot, RestartAah, ForceOpenAah, CheckUpdate, InstallUpdate, CollectDiagnostics,
        ReloadPermissions, ClearDisposableSnapshot, SignOut, TestMainServer, ShowMessage,
        RequestRemoteAssist, RemoteAssistNavigate, RemoteAssistSetLocalPreference, RemoteAssistEnd
    };
}


public static class RemoteAssistPrivilegeModes
{
    public const string RemoteUser = "Remote User Permissions";
    public const string PerActionHelper = "Use Helper Permission Per Action";
    public const string TemporaryHelper = "Temporary Helper Elevation";
    public static IReadOnlyList<string> All { get; } = [RemoteUser, PerActionHelper, TemporaryHelper];
}

public static class RemoteAssistStreamDirections
{
    public const string RemoteToAdministrator = "RemoteToAdministrator";
    public const string AdministratorToRemote = "AdministratorToRemote";
}

public sealed class RemoteAssistDevicePolicyRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public bool AutoAcceptAdministratorAssist { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class RemoteAssistStreamStateRecord
{
    public string SessionId { get; set; } = string.Empty;
    public bool RemoteSharing { get; set; }
    public bool AdministratorMayControlRemote { get; set; }
    public bool AdministratorSharing { get; set; }
    public bool RemoteMayControlAdministrator { get; set; }
    public long RemoteFrameSequence { get; set; }
    public long AdministratorFrameSequence { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class RemoteAssistFrameRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string Direction { get; set; } = RemoteAssistStreamDirections.RemoteToAdministrator;
    public long Sequence { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public DateTime CapturedUtc { get; set; }
    public byte[] JpegBytes { get; set; } = Array.Empty<byte>();
}

public sealed class RemoteAssistInputRecord
{
    public long Sequence { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Direction { get; set; } = RemoteAssistStreamDirections.AdministratorToRemote;
    public string Kind { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public int Delta { get; set; }
    public int Key { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Control { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class RemoteAssistSessionRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string RemoteUserName { get; set; } = string.Empty;
    public string HelperUserName { get; set; } = string.Empty;
    public string PermissionMode { get; set; } = RemoteAssistPrivilegeModes.RemoteUser;
    public string GrantedPermissionsJson { get; set; } = "[]";
    public string Scope { get; set; } = "AAH Only";
    public string Reason { get; set; } = string.Empty;
    public bool UserConsented { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
}

public sealed class AdminAuditRecord
{
    public long Id { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string RecordHash { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}
public sealed class DeploymentRingRecord
{
    public string RingName { get; set; } = "Production";
    public bool IsEnabled { get; set; } = true;
    public DateTime? ForceAfterUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class DeviceUpdateRingRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public string RingName { get; set; } = "Production";
    public DateTime UpdatedUtc { get; set; }
}

public sealed class DevicePresenceRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
}

public sealed class DevicePostureRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public string HealthState { get; set; } = DeviceHealthStates.Healthy;
    public int RiskScore { get; set; }
    public string RiskReasons { get; set; } = string.Empty;
    public bool AppVersionCurrent { get; set; } = true;
    public bool RegistrationTrusted { get; set; } = true;
    public bool CertificateValid { get; set; } = true;
    public bool SnapshotHealthy { get; set; } = true;
    public bool IsQuarantined { get; set; }
    public string QuarantineReason { get; set; } = string.Empty;
    public DateTime EvaluatedUtc { get; set; }
}

public sealed class RemoteAahCommandRecord
{
    public string CommandId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public string RequestedBy { get; set; } = string.Empty;
    public string RequiredPermission { get; set; } = GovernancePermissions.RemoteAah;
    public string State { get; set; } = RemoteAahCommandStates.Pending;
    public string ResultDetail { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? DeliveredUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public sealed class CollaborationMessageRecord
{
    public long Id { get; set; }
    public string ConversationKey { get; set; } = string.Empty;
    public string ConversationType { get; set; } = "Private";
    public string SenderUserName { get; set; } = string.Empty;
    public string RecipientUserName { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Priority { get; set; } = "Normal";
    public long ReplyToMessageId { get; set; }
    public string ReplyPreview { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? ReadUtc { get; set; }
    public DateTime? EditedUtc { get; set; }
    public DateTime? DeletedUtc { get; set; }
    public bool IsPinned { get; set; }
    public string PinnedBy { get; set; } = string.Empty;
    public string ReactionSummary { get; set; } = string.Empty;
    public bool IsUnreadForCurrentUser { get; set; }
    public bool IsDeleted => DeletedUtc.HasValue;
    public bool HasReply => ReplyToMessageId > 0;
    public string DisplayBody => IsDeleted ? "[Message deleted]" : Body;
    public string EditedText => EditedUtc.HasValue ? $"Edited {EditedUtc.Value.ToLocalTime():g}" : string.Empty;
}

public sealed class UserChatStatusRecord
{
    public string UserName { get; set; } = string.Empty;
    public string Availability { get; set; } = "Available";
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CollaborationFileRecord
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public string FileId { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string UploadedBy { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class WorkTaskRecord
{
    public long Id { get; set; }
    public string ItemType { get; set; } = "Task";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AssignedTo { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;
    public string Priority { get; set; } = "Normal";
    public string Status { get; set; } = "Open";
    public DateTime? DueUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ApprovalRequestRecord
{
    public long Id { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public string ResourceKey { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string ApproverRole { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string Reason { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? DecidedUtc { get; set; }
}

public sealed class AccessDecisionRecord
{
    public bool Allowed { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public string Explanation { get; set; } = string.Empty;
}

public sealed class SecurityPolicySnapshot
{
    public long AuthorizationRevision { get; set; }
    public long DevicePolicyRevision { get; set; }
    public long ClientAccessRevision { get; set; }
    public long SecurityPolicyRevision { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class EmergencyLockdownRecord
{
    public bool IsActive { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public DateTime ChangedUtc { get; set; }
}

public sealed class DeviceTagRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
