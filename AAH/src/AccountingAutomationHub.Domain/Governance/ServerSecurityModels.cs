namespace AccountingAutomationHub.Domain.Governance;

public static class ServerDeviceTypes
{
    public const string Windows = "Windows";
    public const string Android = "Android";
}

public static class ServerEnrollmentStates
{
    public const string Active = "Active";
    public const string VerificationRequired = "Verification Required";
    public const string Expired = "Expired";
    public const string Revoked = "Revoked";
    public const string Disabled = "Disabled";
    public const string Retired = "Retired";
    public const string Lost = "Lost / Stolen";
}

public sealed class NetworkCredentialRecord
{
    public string UserName { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int PasswordIterations { get; set; }
    public string TotpSecretCipher { get; set; } = string.Empty;
    public bool MfaEnabled { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? LockoutUntilUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ServerDeviceRecord
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = ServerDeviceTypes.Windows;
    public string UserName { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string RoleName { get; set; } = GovernanceRoles.ReadOnly;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    /// <summary>Privacy-preserving Android physical-app binding used only for duplicate-safe enrollment recovery.</summary>
    public string DeviceBindingKey { get; set; } = string.Empty;
    public string EnrollmentState { get; set; } = ServerEnrollmentStates.Active;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LastValidatedUtc { get; set; }
    public DateTime? RegistrationOtpVerifiedUtc { get; set; }
    /// <summary>Last successful Android periodic verification by Mobile Login Code or live user QR.</summary>
    public DateTime? MobileMfaVerifiedUtc { get; set; }
    public DateTime? ExpiredUtc { get; set; }
}

public sealed class NetworkLoginChallengeRecord
{
    public string ChallengeId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string NetworkScope { get; set; } = "LAN";
    public string Code { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class NetworkSessionRecord
{
    public string SessionHash { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

public sealed class FolderLinkRecord
{
    public long Id { get; set; }
    public string LinkId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string LocalLabel { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? ExpiredUtc { get; set; }
}

public sealed class DistributionTicketRecord
{
    public long Id { get; set; }
    public string TicketHash { get; set; } = string.Empty;
    public string PackageKind { get; set; } = string.Empty;
    public string PackageSha256 { get; set; } = string.Empty;
    public string PackageVersion { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ServerPackageRecord
{
    public string PackageKind { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ServerArchivedFileRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class ServerSecurityEventRecord
{
    public long Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool Success { get; set; }
    public DateTime CreatedUtc { get; set; }
}
