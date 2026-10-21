namespace AccountingAutomationHub.Domain.Governance;

public static class GovernancePermissions
{
    public const string Import = "Import";
    public const string UndoImport = "UndoImport";
    public const string EditAccounting = "EditAccounting";
    // Legacy coarse permission retained for upgrade compatibility only. New UI/service gates use the explicit view/edit pair below.
    public const string LedgerMapping = "LedgerMapping";
    public const string LedgerMappingView = "LedgerMapping.View";
    public const string LedgerMappingEdit = "LedgerMapping.Edit";
    public const string NativePythonMappingUi = "Import.NativePythonMappingUi";
    public const string CreateMaster = "CreateMaster";
    public const string TallyExport = "TallyExport";
    public const string UnlockPeriod = "UnlockPeriod";
    public const string DeleteAccounting = "DeleteAccounting";
    public const string RestoreBackup = "RestoreBackup";
    public const string ManageUsers = "ManageUsers";
    public const string Approve = "Approve";
    public const string ViewAudit = "ViewAudit";
    public const string ReadData = "ReadData";
    public const string ExportData = "ExportData";
    public const string UploadFiles = "UploadFiles";
    public const string ManageServer = "ManageServer";
    public const string ManageDevices = "ManageDevices";
    public const string ManageRoles = "ManageRoles";
    public const string ManageClientAccess = "ManageClientAccess";
    public const string ResetDeviceTrust = "ResetDeviceTrust";
    public const string ManageLanUpdates = "ManageLanUpdates";
    public const string ManageBackup = "ManageBackup";
    public const string ManageIntegrations = "ManageIntegrations";
    public const string RemoteAah = "RemoteAah";
    public const string RemoteAssist = "RemoteAssist";
    public const string MessagesSend = "Messages.Send";
    public const string FilesShare = "Files.Share";
    public const string TasksManage = "Tasks.Manage";
    public const string ApprovalsManage = "Approvals.Manage";
    public const string BankView = "Bank.View";
    public const string BankMap = "Bank.Map";
    public const string BankExport = "Bank.Export";
    public const string PurchaseView = "Purchase.View";
    public const string PurchaseEdit = "Purchase.Edit";
    public const string PurchaseDelete = "Purchase.Delete";
    public const string PurchaseExport = "Purchase.Export";
    public const string SalesView = "Sales.View";
    public const string SalesEdit = "Sales.Edit";
    public const string JournalView = "Journal.View";
    public const string JournalCreate = "Journal.Create";
    public const string JournalApprove = "Journal.Approve";
    public const string MasterView = "Master.View";
    public const string MasterMerge = "Master.Merge";
    public const string StockView = "Stock.View";
    public const string StockEdit = "Stock.Edit";
    public const string GstView = "GST.View";
    public const string GstImport = "GST.Import";
    public const string GstControl = "GST.Control";
    public const string TallyView = "Tally.View";
    public const string TallyAlter = "Tally.Alter";

    public static IReadOnlyList<string> All { get; } =
    [
        Import, UndoImport, EditAccounting, LedgerMapping, LedgerMappingView, LedgerMappingEdit, NativePythonMappingUi, CreateMaster,
        TallyExport, UnlockPeriod, DeleteAccounting, RestoreBackup,
        ManageUsers, Approve, ViewAudit, ReadData, ExportData, UploadFiles, ManageServer, ManageDevices,
        ManageRoles, ManageClientAccess, ResetDeviceTrust, ManageLanUpdates, ManageBackup, ManageIntegrations,
        RemoteAah, RemoteAssist, MessagesSend, FilesShare, TasksManage, ApprovalsManage,
        BankView, BankMap, BankExport, PurchaseView, PurchaseEdit, PurchaseDelete, PurchaseExport,
        SalesView, SalesEdit, JournalView, JournalCreate, JournalApprove, MasterView, MasterMerge, StockView, StockEdit,
        GstView, GstImport, GstControl, TallyView, TallyAlter
    ];
}

public static class GovernanceRoles
{
    public const string Administrator = "Administrator";
    public const string Accountant = "Accountant";
    public const string User = "User";
    public const string ImportOnly = "Import Only";
    public const string Reviewer = "Reviewer";
    public const string DataEntry = "Data Entry";
    public const string ReadOnly = "Read Only";
    public const string SecurityAdministrator = "Security Administrator";
    public const string UserAdministrator = "User Administrator";
    public const string DeviceAdministrator = "Device Administrator";
    public const string ClientAccessAdministrator = "Client Access Administrator";
    public const string UpdateAdministrator = "Update Administrator";
    public const string BackupAdministrator = "Backup Administrator";
    public const string Auditor = "Auditor";
    public const string HelpDesk = "Help Desk";
    public const string Custom = "Custom";

    public static IReadOnlyList<string> All { get; } =
    [Administrator, User, ImportOnly, Accountant, Reviewer, DataEntry, ReadOnly,
     SecurityAdministrator, UserAdministrator, DeviceAdministrator, ClientAccessAdministrator,
     UpdateAdministrator, BackupAdministrator, Auditor, HelpDesk, Custom];

    public static bool IsFixedProfile(string roleName) => !roleName.Equals(Custom,StringComparison.OrdinalIgnoreCase);
    public static string Description(string roleName) => roleName switch
    {
        Administrator => "Full Main Server authority. All application, accounting, security, LAN, remote and administration capabilities.",
        Accountant => "Full day-to-day accounting role: view/edit accounting, Ledger Mapping, imports, approvals and explicit Tally export. No server/user/device administration.",
        DataEntry => "Accounting data-entry role: imports and routine accounting edits including Ledger Mapping, but no Tally export or administration.",
        User => "General operational user: read, export, upload/import, collaboration and view-only Ledger Mapping. No accounting edit authority by default.",
        ImportOnly => "File intake role: upload/import and collaboration only. No accounting editing, Ledger Mapping editing or Tally send.",
        Reviewer => "Review/approval role: view/export/audit/approvals with view-only Ledger Mapping. No accounting editing.",
        ReadOnly => "Strict read-only accounting role. May navigate, view, filter, report, collaborate and use permitted exports; no accounting/master/mapping mutation or Tally send.",
        SecurityAdministrator => "Security policy, device trust, roles, audit and remote-security administration. No implicit accounting edit authority.",
        UserAdministrator => "Users, roles and Client/FY access administration. No implicit accounting edit authority.",
        DeviceAdministrator => "Device lifecycle, trust, Client access and remote-device administration. No implicit accounting edit authority.",
        ClientAccessAdministrator => "Client/FY access administration and user access review only.",
        UpdateAdministrator => "LAN update/ring/device update administration and remote update commands.",
        BackupAdministrator => "Verified backup/restore administration and audit visibility.",
        Auditor => "Strict audit/read/export profile with no mutation authority.",
        HelpDesk => "Device troubleshooting, trust reset and consent-based remote support without accounting mutation authority.",
        Custom => "Explicit administrator-defined permission profile. Use this role when the fixed built-in roles do not match the user's job.",
        _ => roleName
    };
}

public static class ImportJobStates
{
    public const string Detected = "Detected";
    public const string Validating = "Validating";
    public const string Parsing = "Parsing";
    public const string ReviewRequired = "Review Required";
    public const string Ready = "Ready";
    public const string Importing = "Importing";
    public const string Imported = "Imported";
    public const string Verified = "Verified";
    public const string Duplicate = "Duplicate";
    public const string Failed = "Failed";
    public const string Ignored = "Ignored";
    public const string RolledBack = "Rolled Back";

    public static bool IsTerminal(string? state) =>
        state is not null && (state.Equals(Verified, StringComparison.OrdinalIgnoreCase)
                              || state.Equals(Duplicate, StringComparison.OrdinalIgnoreCase)
                              || state.Equals(Failed, StringComparison.OrdinalIgnoreCase)
                              || state.Equals(Ignored, StringComparison.OrdinalIgnoreCase)
                              || state.Equals(RolledBack, StringComparison.OrdinalIgnoreCase));
}

public sealed class ApplicationUserRecord
{
    public long Id { get; set; }
    public string WindowsUserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RoleName { get; set; } = GovernanceRoles.ReadOnly;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    /// <summary>How often an enrolled Android phone must satisfy a Main Server Mobile Login Code or live user QR verification. Default: 30 days.</summary>
    public int MobileVerificationIntervalDays { get; set; } = 30;
    public string RoleDescription { get; set; } = string.Empty;
}

public sealed class RolePermissionRecord
{
    public string RoleName { get; set; } = string.Empty;
    public string PermissionName { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public string Category => GovernancePermissionCatalog.Category(PermissionName);
    public string Description => GovernancePermissionCatalog.Description(PermissionName);
    public string Risk => GovernancePermissionCatalog.Risk(PermissionName);
    public bool IsEditable => RoleName.Equals(GovernanceRoles.Custom,StringComparison.OrdinalIgnoreCase);
    public string LockReason => IsEditable ? "Custom role permission" : "Fixed built-in role profile";
}

public static class GovernancePermissionCatalog
{
    public static string Category(string permission) => permission switch
    {
        GovernancePermissions.LedgerMappingView or GovernancePermissions.LedgerMappingEdit or GovernancePermissions.LedgerMapping or GovernancePermissions.NativePythonMappingUi or GovernancePermissions.BankView or GovernancePermissions.BankMap or GovernancePermissions.BankExport => "Banking / Ledger Mapping",
        GovernancePermissions.PurchaseView or GovernancePermissions.PurchaseEdit or GovernancePermissions.PurchaseDelete or GovernancePermissions.PurchaseExport => "Purchase",
        GovernancePermissions.SalesView or GovernancePermissions.SalesEdit => "Sales",
        GovernancePermissions.JournalView or GovernancePermissions.JournalCreate or GovernancePermissions.JournalApprove => "Journal",
        GovernancePermissions.MasterView or GovernancePermissions.MasterMerge or GovernancePermissions.CreateMaster => "Masters",
        GovernancePermissions.StockView or GovernancePermissions.StockEdit => "Stock / Inventory",
        GovernancePermissions.GstView or GovernancePermissions.GstImport or GovernancePermissions.GstControl => "GST / Returns",
        GovernancePermissions.TallyView or GovernancePermissions.TallyAlter or GovernancePermissions.TallyExport => "Tally",
        GovernancePermissions.MessagesSend or GovernancePermissions.FilesShare or GovernancePermissions.TasksManage or GovernancePermissions.ApprovalsManage => "Collaboration",
        GovernancePermissions.ManageUsers or GovernancePermissions.ManageRoles or GovernancePermissions.ManageClientAccess => "Users / Access",
        GovernancePermissions.ManageDevices or GovernancePermissions.ResetDeviceTrust or GovernancePermissions.RemoteAah or GovernancePermissions.RemoteAssist or GovernancePermissions.ManageLanUpdates => "LAN / Remote",
        GovernancePermissions.ManageServer or GovernancePermissions.ManageBackup or GovernancePermissions.RestoreBackup or GovernancePermissions.ManageIntegrations => "Server / Security",
        _ => "General"
    };

    public static string Risk(string permission) => permission switch
    {
        GovernancePermissions.ReadData or GovernancePermissions.ExportData or GovernancePermissions.ViewAudit or GovernancePermissions.BankView or GovernancePermissions.PurchaseView or GovernancePermissions.SalesView or GovernancePermissions.JournalView or GovernancePermissions.MasterView or GovernancePermissions.StockView or GovernancePermissions.GstView or GovernancePermissions.TallyView or GovernancePermissions.LedgerMappingView => "Read",
        GovernancePermissions.MessagesSend or GovernancePermissions.FilesShare or GovernancePermissions.UploadFiles or GovernancePermissions.TasksManage => "Collaborate",
        GovernancePermissions.Import or GovernancePermissions.EditAccounting or GovernancePermissions.LedgerMappingEdit or GovernancePermissions.LedgerMapping or GovernancePermissions.BankMap or GovernancePermissions.PurchaseEdit or GovernancePermissions.SalesEdit or GovernancePermissions.JournalCreate or GovernancePermissions.CreateMaster or GovernancePermissions.StockEdit or GovernancePermissions.GstImport or GovernancePermissions.GstControl => "Change",
        _ => "Privileged"
    };

    public static string Description(string permission) => permission switch
    {
        GovernancePermissions.LedgerMappingView => "Open Ledger Mapping, inspect mappings, filters, suggestions and transaction membership without changing mappings.",
        GovernancePermissions.LedgerMappingEdit => "Change Final Ledger/Group, bulk map, apply suggestions, rebuild/apply mappings and import suspense mapping changes.",
        GovernancePermissions.LedgerMapping => "Legacy coarse Ledger Mapping permission. Kept only for upgrade compatibility; new roles should use LedgerMapping.View/Edit.",
        GovernancePermissions.NativePythonMappingUi => "Allow the native external Python mapping/password/line-confirmation window on this Windows AAH processing node. Administrator-only by default; AAH-native mapping UI remains preferred.",
        GovernancePermissions.ReadData => "View authorised accounting data and reports.",
        GovernancePermissions.ExportData => "Export authorised data where the module also permits export.",
        GovernancePermissions.UploadFiles => "Upload supported files to an authorised Client/FY.",
        GovernancePermissions.Import => "Import supported source files into authorised accounting workspaces.",
        GovernancePermissions.EditAccounting => "Edit accounting rows in modules that do not have a more specific edit permission.",
        GovernancePermissions.StockView => "View Stock Items, Units, Groups, Godowns and inventory mappings without changing them.",
        GovernancePermissions.StockEdit => "Create, edit, archive and map local Stock/Inventory Masters. Does not grant Tally export by itself.",
        GovernancePermissions.GstView => "View GSTR-3B reconciliation, portal evidence, ledgers and GST reporting details.",
        GovernancePermissions.GstImport => "Import or refresh GSTR-3B and GST portal ledger evidence for an authorised Client/FY.",
        GovernancePermissions.GstControl => "Change GST control selections such as Nil Sales periods and other explicit GST reconciliation controls.",
        GovernancePermissions.MessagesSend => "Send private/global/client messages and participate in collaboration.",
        GovernancePermissions.FilesShare => "Share and download collaboration files allowed by Client/FY access.",
        GovernancePermissions.RemoteAssist => "Start or participate in consent-based AAH Remote Assist.",
        GovernancePermissions.RemoteAah => "Send allow-listed administrative AAH commands to enrolled devices.",
        GovernancePermissions.ManageRoles => "Change role permission matrices. High impact: can alter effective authority for many users.",
        GovernancePermissions.ManageClientAccess => "Grant/revoke permanent or temporary Client/FY access.",
        GovernancePermissions.ManageDevices => "Change enrolled-device lifecycle, quarantine, tags and administrative device controls.",
        GovernancePermissions.ResetDeviceTrust => "Require a device to repeat Registration OTP verification.",
        GovernancePermissions.TallyExport => "Explicitly send eligible masters/vouchers to Tally after preflight.",
        GovernancePermissions.TallyAlter => "Permit supported ALTER operations against Tally-owned targets where the workflow explicitly allows it.",
        _ => permission.Replace('.', ' ')
    };
}

public sealed class ImportRunRecord
{
    public string RunId { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string Status { get; set; } = ImportJobStates.Detected;
    public int TotalFiles { get; set; }
    public int ImportedFiles { get; set; }
    public int DuplicateFiles { get; set; }
    public int ReviewFiles { get; set; }
    public int FailedFiles { get; set; }
    public int RecordCount { get; set; }
    public bool IsUndoable { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class ImportJobRecord
{
    public string JobId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;
    public string VaultPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string DetectedModule { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string State { get; set; } = ImportJobStates.Detected;
    public string Detail { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public int AttemptCount { get; set; }
    public bool IsUndoable { get; set; }
    public string UndoStatus { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class DocumentVaultRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string VaultPath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string MimeHint { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public DateTime AddedUtc { get; set; }
}

public sealed class EntityVersionRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime ChangedUtc { get; set; }
}

public sealed class VerifiedBackupRecord
{
    public long Id { get; set; }
    public string BackupPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string IntegrityResult { get; set; } = string.Empty;
    public string ScheduleClass { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? VerifiedUtc { get; set; }
    public bool IsVerified { get; set; }
}

public sealed class ImportUndoPreview
{
    public string RunId { get; set; } = string.Empty;
    public int BankStatements { get; set; }
    public int BankTransactions { get; set; }
    public int PurchaseRecords { get; set; }
    public int SalesRecords { get; set; }
    public int CashEntries { get; set; }
    public int JournalVouchers { get; set; }
    public int TotalRecords => BankTransactions + PurchaseRecords + SalesRecords + CashEntries + JournalVouchers;
    public string Warning { get; set; } = string.Empty;
}

public sealed class RecurringTransactionProfile
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string Frequency { get; set; } = "Monthly";
    public int Interval { get; set; } = 1;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime NextRunDate { get; set; }
    public bool AutoCreateAsDraft { get; set; } = true;
    public string TemplateJson { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class TransactionRelationship
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string SourceEntityKey { get; set; } = string.Empty;
    public string TargetEntityKey { get; set; } = string.Empty;
    public string RelationshipType { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class LedgerSuggestionEvidence
{
    public string SummaryName { get; set; } = string.Empty;
    public string SuggestedLedger { get; set; } = string.Empty;
    public string SuggestedGroup { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int PreviousAcceptCount { get; set; }
}

public sealed class AdvancedReconciliationPreview
{
    public string MatchId { get; set; } = string.Empty;
    public string MatchType { get; set; } = string.Empty;
    public IReadOnlyList<long> BankTransactionIds { get; set; } = [];
    public IReadOnlyList<string> BookReferences { get; set; } = [];
    public decimal BankAmount { get; set; }
    public decimal BookAmount { get; set; }
    public decimal Difference => BankAmount - BookAmount;
    public decimal AdjustmentAmount { get; set; }
    public string AdjustmentLedger { get; set; } = string.Empty;
    public string Status { get; set; } = "Preview";
    public string Detail { get; set; } = string.Empty;
}

public sealed class UserClientAccessRecord
{
    public long Id { get; set; }
    public string WindowsUserName { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public bool IsAllowed { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
}

public sealed class LanDeviceRecord
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string RoleName { get; set; } = GovernanceRoles.ReadOnly;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string DeviceType { get; set; } = ServerDeviceTypes.Windows;
    public string UserName { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string EnrollmentState { get; set; } = ServerEnrollmentStates.Active;
    public DateTime? LastValidatedUtc { get; set; }
    public DateTime? ExpiredUtc { get; set; }
}

