using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Central explainable authorization/risk evaluation for Main Server and remote AAH actions.
/// UI visibility is never treated as security; server endpoints call the same policy service.
/// </summary>
public sealed class EnterprisePolicyService
{
    private readonly SqliteGovernanceRepository _governance;
    private readonly EnterpriseControlRepository _control;

    public EnterprisePolicyService(SqliteGovernanceRepository governance, EnterpriseControlRepository control)
    {
        _governance = governance;
        _control = control;
    }

    public AccessDecisionRecord Evaluate(ApplicationUserRecord user, ServerDeviceRecord device, long clientId, string financialYear, string action, bool clientAllowed, bool outsideOffice = false)
    {
        var explanations = new List<string>();
        var risk = 0;
        var permission = NormalizePermission(action);
        var permissionAllowed = user.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase) || _governance.HasPermission(user.WindowsUserName, permission);
        if (permissionAllowed) explanations.Add($"Permission {permission}: allowed.");
        else explanations.Add($"Permission {permission}: denied.");

        if (clientId > 0)
            explanations.Add(clientAllowed ? $"Client/FY {clientId}/{financialYear}: assigned." : $"Client/FY {clientId}/{financialYear}: not assigned.");

        var posture = _control.GetPosture(device.DeviceId);
        if (posture.IsQuarantined)
        {
            explanations.Add($"Device is quarantined: {posture.QuarantineReason}");
            risk = Math.Max(risk, 100);
        }
        else
        {
            if (!posture.AppVersionCurrent) { risk += 20; explanations.Add("Device application is not current (+20 risk)."); }
            if (!posture.RegistrationTrusted) { risk += 50; explanations.Add("Registration trust is not valid (+50 risk)."); }
            if (!posture.CertificateValid) { risk += 60; explanations.Add("Server certificate validation failed (+60 risk)."); }
            if (!posture.SnapshotHealthy) { risk += 15; explanations.Add("Latest snapshot health is warning (+15 risk)."); }
        }
        if (outsideOffice) { risk += 15; explanations.Add("Private remote/WireGuard route (+15 risk)."); }
        if (action.Contains("Delete", StringComparison.OrdinalIgnoreCase) || action.Contains("Alter", StringComparison.OrdinalIgnoreCase) || action.Contains("Restore", StringComparison.OrdinalIgnoreCase))
        {
            risk += 20;
            explanations.Add("Sensitive operation (+20 risk).");
        }
        risk = Math.Clamp(risk, 0, 100);
        explanations.Add(risk >= 80 ? "Risk policy: block or second-administrator approval." : risk >= 60 ? "Risk policy: step-up/approval required." : risk >= 30 ? "Risk policy: warning and enhanced audit." : "Risk policy: normal.");

        var allowed = permissionAllowed && (clientId <= 0 || clientAllowed) && !posture.IsQuarantined && risk < 80;
        return new AccessDecisionRecord
        {
            Allowed = allowed,
            UserName = user.WindowsUserName,
            DeviceId = device.DeviceId,
            ClientId = clientId,
            FinancialYear = financialYear,
            Action = action,
            RiskScore = risk,
            Explanation = string.Join(Environment.NewLine, explanations)
        };
    }

    private static string NormalizePermission(string action)
    {
        if (GovernancePermissions.All.Contains(action, StringComparer.OrdinalIgnoreCase))
            return GovernancePermissions.All.First(x => x.Equals(action, StringComparison.OrdinalIgnoreCase));
        return action switch
        {
            "RemoteAah" => GovernancePermissions.RemoteAah,
            "RemoteAssist" => GovernancePermissions.RemoteAssist,
            "Messages.Send" => GovernancePermissions.MessagesSend,
            "Files.Share" => GovernancePermissions.FilesShare,
            "Tasks.Manage" => GovernancePermissions.TasksManage,
            "Approvals.Manage" => GovernancePermissions.ApprovalsManage,
            _ => action
        };
    }
}
