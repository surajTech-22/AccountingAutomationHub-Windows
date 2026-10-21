using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Presentation guard for an authorised Windows AAH processing node. Historic LAN releases
/// forced the complete accounting surface read-only; distributed-processing mode is instead
/// permission driven. The guard is only a presentation aid: ViewModels/server APIs remain the
/// authority. Tally execution and Main-Server administration stay blocked on network nodes.
/// </summary>
public static class NetworkReadOnlyUiGuard
{
    private static readonly string[] NonMutatingTerms =
    [
        "refresh","search","filter","back","next","previous","calculator","report","open","view","details",
        "groups","ledger","statement","share lan application","update from main server","required update","check for update",
        "install update","sign out","download","export","status"
    ];

    public static void Apply(DependencyObject root)
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        Walk(root);
    }

    private static void Walk(DependencyObject node)
    {
        // These surfaces already use explicit collaboration/settings/notification/Remote Assist
        // permission checks and are not accounting mutation surfaces.
        if (node is SettingsView or TeamView or WorkView or NotificationCenterView or RemoteAssistView) return;

        if (node is DataGrid grid)
        {
            if (!HasAnyAccountingMutationPermission())
            {
                grid.IsReadOnly = true;
                grid.CanUserAddRows = false;
                grid.CanUserDeleteRows = false;
            }
        }
        else if (node is Button button)
        {
            var text = ReadText(button.Content).Trim().ToLowerInvariant();
            if (ShouldDisable(text, out var reason))
            {
                button.IsEnabled = false;
                button.ToolTip = reason;
            }
        }
        else if (node is MenuItem menuItem)
        {
            var text = ReadText(menuItem.Header).Trim().ToLowerInvariant();
            if (ShouldDisable(text, out _)) menuItem.IsEnabled = false;
        }
        // Do not globally disable CheckBox/editor controls. Their bound command/ViewModel
        // permissions decide whether the current user may commit the value.

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
    }

    private static bool ShouldDisable(string text, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (NetworkClientRuntime.AccountingAccessSuspended)
        {
            if (IsMutationText(text))
            {
                reason = string.IsNullOrWhiteSpace(NetworkClientRuntime.AccountingAccessSuspensionReason)
                    ? "Accounting changes are suspended by Main Server security policy."
                    : NetworkClientRuntime.AccountingAccessSuspensionReason;
                return true;
            }
            return false;
        }

        // Actual Tally process control/import remains Main-Server/Tally-host authority for this phase.
        if (text.Contains("send to tally", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("tally export", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("import to tally", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("alter tally", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("open tally", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Tally execution remains available only on the validated Main Server/Tally host.";
            return true;
        }

        if (text.Contains("approve", StringComparison.OrdinalIgnoreCase) || text.Contains("reject", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Accounting approval remains a Main Server authority in this phase.";
            return true;
        }

        if (text.Contains("upload", StringComparison.OrdinalIgnoreCase))
            return DenyUnless(NetworkClientRuntime.HasAnyPermission(GovernancePermissions.UploadFiles, GovernancePermissions.Import), "Upload/import permission is required.", out reason);

        if (text.Contains("export", StringComparison.OrdinalIgnoreCase) || text.Contains("download", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("update", StringComparison.OrdinalIgnoreCase)) return false;
            return DenyUnless(NetworkClientRuntime.HasAnyPermission(GovernancePermissions.ExportData, GovernancePermissions.BankExport, GovernancePermissions.PurchaseExport), "Export permission is required.", out reason);
        }

        if (text.Contains("import", StringComparison.OrdinalIgnoreCase) || text.Contains("reimport", StringComparison.OrdinalIgnoreCase) || text.Contains("browse", StringComparison.OrdinalIgnoreCase))
            return DenyUnless(NetworkClientRuntime.HasPermission(GovernancePermissions.Import), "Import permission is required on this Windows processing node.", out reason);

        if (text.Contains("map", StringComparison.OrdinalIgnoreCase) || text.Contains("mapping", StringComparison.OrdinalIgnoreCase))
            return DenyUnless(NetworkClientRuntime.HasAnyPermission(GovernancePermissions.LedgerMappingEdit, GovernancePermissions.BankMap, GovernancePermissions.LedgerMapping, GovernancePermissions.EditAccounting), "Ledger Mapping edit permission is required.", out reason);

        if (text.Contains("delete", StringComparison.OrdinalIgnoreCase) || text.Contains("remove", StringComparison.OrdinalIgnoreCase) || text.Contains("undo", StringComparison.OrdinalIgnoreCase) || text.Contains("reverse", StringComparison.OrdinalIgnoreCase))
            return DenyUnless(NetworkClientRuntime.HasAnyPermission(GovernancePermissions.DeleteAccounting, GovernancePermissions.PurchaseDelete, GovernancePermissions.UndoImport), "Delete/undo permission is required.", out reason);

        if (NonMutatingTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))) return false;

        if (IsMutationText(text))
            return DenyUnless(HasAnyAccountingMutationPermission(), "The current role does not permit accounting changes on this Windows processing node.", out reason);

        return false;
    }

    private static bool HasAnyAccountingMutationPermission() =>
        NetworkClientRuntime.HasAnyPermission(
            GovernancePermissions.Import,
            GovernancePermissions.EditAccounting,
            GovernancePermissions.LedgerMappingEdit,
            GovernancePermissions.BankMap,
            GovernancePermissions.PurchaseEdit,
            GovernancePermissions.PurchaseDelete,
            GovernancePermissions.SalesEdit,
            GovernancePermissions.JournalCreate,
            GovernancePermissions.CreateMaster,
            GovernancePermissions.MasterMerge,
            GovernancePermissions.StockEdit,
            GovernancePermissions.GstImport,
            GovernancePermissions.GstControl);

    private static bool IsMutationText(string text) =>
        new[] { "save", "delete", "remove", "new ", "new", "create", "add ", "edit", "update", "import", "reimport", "map ", "map all", "merge", "apply", "alter", "post", "undo", "reverse", "clone", "divide", "rebuild", "restore defaults", "browse", "link ", "set / reset", "approve", "reject", "archive", "unarchive", "clear ", "write", "sync now" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool DenyUnless(bool allowed, string deniedReason, out string reason)
    {
        reason = allowed ? string.Empty : deniedReason;
        return !allowed;
    }

    private static string ReadText(object? content) => content switch
    {
        string s => s,
        TextBlock t => t.Text ?? string.Empty,
        _ => content?.ToString() ?? string.Empty
    };
}
