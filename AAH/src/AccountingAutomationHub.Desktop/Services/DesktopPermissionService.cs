using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// One desktop permission evaluator for Main Server and authorised Windows processing-node mode.
/// The Main Server database is authoritative when connected; a LAN/outside-LAN workstation
/// uses the authenticated permission snapshot issued by the Main Server and may perform
/// accounting mutations locally when that role explicitly grants the required capability.
/// Quarantine/emergency-lockdown still suspends accounting mutation immediately.
/// </summary>
public static class DesktopPermissionService
{
    public static bool HasPermission(SqliteGovernanceRepository repository, string permission)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(permission)) return false;

        return NetworkClientRuntime.IsNetworkClient
            ? NetworkClientRuntime.HasPermission(permission)
            : repository.HasPermission(Environment.UserName, permission);
    }

    public static bool CanMutateAccounting(SqliteGovernanceRepository repository, string permission) =>
        (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended) &&
        HasPermission(repository, permission);

    public static string EffectiveRoleName(SqliteGovernanceRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (NetworkClientRuntime.IsNetworkClient)
            return string.IsNullOrWhiteSpace(NetworkClientRuntime.RoleName)
                ? GovernanceRoles.ReadOnly
                : NetworkClientRuntime.RoleName.Trim();

        return repository.GetCurrentUser().RoleName;
    }

    public static string DeniedMessage(SqliteGovernanceRepository repository, string permission, string action)
    {
        var accountingMutation = permission is GovernancePermissions.EditAccounting
            or GovernancePermissions.DeleteAccounting
            or GovernancePermissions.LedgerMappingEdit
            or GovernancePermissions.BankMap
            or GovernancePermissions.PurchaseEdit
            or GovernancePermissions.PurchaseDelete
            or GovernancePermissions.SalesEdit
            or GovernancePermissions.JournalCreate
            or GovernancePermissions.JournalApprove
            or GovernancePermissions.CreateMaster
            or GovernancePermissions.MasterMerge
            or GovernancePermissions.StockEdit
            or GovernancePermissions.GstImport
            or GovernancePermissions.GstControl
            or GovernancePermissions.TallyExport
            or GovernancePermissions.TallyAlter;

        if (!HasPermission(repository, permission))
            return DeniedMessage(repository, action, accountingMutation);

        if (accountingMutation && NetworkClientRuntime.IsNetworkClient && NetworkClientRuntime.AccountingAccessSuspended)
            return NetworkClientRuntime.AccountingAccessSuspensionReason.Length > 0
                ? $"'{action}' is unavailable while accounting access is suspended: {NetworkClientRuntime.AccountingAccessSuspensionReason}"
                : $"'{action}' is unavailable while this Windows AAH node is suspended by Main Server security policy.";

        return $"'{action}' is currently unavailable even though the effective role '{EffectiveRoleName(repository)}' includes '{permission}'. Refresh permissions or review the module-specific guard.";
    }

    public static string DeniedMessage(SqliteGovernanceRepository repository, string action, bool accountingMutation = false)
    {
        var role = EffectiveRoleName(repository);
        if (accountingMutation && NetworkClientRuntime.IsNetworkClient && NetworkClientRuntime.AccountingAccessSuspended)
            return NetworkClientRuntime.AccountingAccessSuspensionReason.Length > 0
                ? $"'{action}' is unavailable while accounting access is suspended: {NetworkClientRuntime.AccountingAccessSuspensionReason}"
                : $"'{action}' is unavailable while this Windows AAH node is suspended by Main Server security policy.";

        return $"Your effective role '{role}' does not permit '{action}'. Ask an Administrator to assign the correct fixed role or a Custom role with the required capability.";
    }
}
