using System.Text.Json;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// In-memory Remote Assist privilege context for the LAN process. Helper permissions are never
/// written into the disposable accounting database, Windows Credential Manager, or the remote user's
/// normal NetworkClientRuntime permission set. They apply only while executing Remote Assist commands
/// carrying the matching Main-Server-issued session id.
/// </summary>
public static class RemoteAssistPrivilegeContext
{
    private static readonly object Gate = new();
    private static string _sessionId = string.Empty;
    private static string _helperUser = string.Empty;
    private static string _remoteUser = string.Empty;
    private static string _mode = RemoteAssistPrivilegeModes.RemoteUser;
    private static string _scope = "AAH Only";
    private static string _reason = string.Empty;
    private static DateTime _expiresUtc;
    private static HashSet<string> _helperPermissions = new(StringComparer.OrdinalIgnoreCase);

    public static event EventHandler? Changed;

    public static bool IsActive
    {
        get { lock (Gate) return _sessionId.Length > 0 && _expiresUtc > DateTime.UtcNow; }
    }
    public static string SessionId { get { lock (Gate) return IsActive ? _sessionId : string.Empty; } }
    public static string HelperUser { get { lock (Gate) return _helperUser; } }
    public static string RemoteUser { get { lock (Gate) return _remoteUser; } }
    public static string PermissionMode { get { lock (Gate) return _mode; } }
    public static string Scope { get { lock (Gate) return _scope; } }
    public static string Reason { get { lock (Gate) return _reason; } }
    public static DateTime ExpiresUtc { get { lock (Gate) return _expiresUtc; } }

    public static void Begin(string sessionId,string helperUser,string remoteUser,string mode,IEnumerable<string> helperPermissions,string scope,string reason,DateTime expiresUtc)
    {
        lock (Gate)
        {
            _sessionId=sessionId.Trim();_helperUser=helperUser.Trim();_remoteUser=remoteUser.Trim();_mode=mode.Trim();_scope=scope?.Trim()??"AAH Only";_reason=reason?.Trim()??string.Empty;_expiresUtc=expiresUtc.ToUniversalTime();
            _helperPermissions=new HashSet<string>(helperPermissions.Where(x=>!string.IsNullOrWhiteSpace(x)),StringComparer.OrdinalIgnoreCase);
        }
        Changed?.Invoke(null,EventArgs.Empty);
    }

    public static void End()
    {
        lock(Gate){_sessionId=string.Empty;_helperUser=string.Empty;_remoteUser=string.Empty;_mode=RemoteAssistPrivilegeModes.RemoteUser;_scope="AAH Only";_reason=string.Empty;_expiresUtc=default;_helperPermissions.Clear();}
        Changed?.Invoke(null,EventArgs.Empty);
    }

    public static bool ValidateCommand(string sessionId)
    {
        lock(Gate)return _sessionId.Length>0&&_expiresUtc>DateTime.UtcNow&&_sessionId.Equals(sessionId?.Trim(),StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanUseHelperPermission(string permission,bool perActionRequested)
    {
        lock(Gate)
        {
            if(_sessionId.Length==0||_expiresUtc<=DateTime.UtcNow)return false;
            if(_mode.Equals(RemoteAssistPrivilegeModes.RemoteUser,StringComparison.OrdinalIgnoreCase))return false;
            if(_mode.Equals(RemoteAssistPrivilegeModes.PerActionHelper,StringComparison.OrdinalIgnoreCase)&&!perActionRequested)return false;
            return _helperPermissions.Contains(permission)||_helperPermissions.Contains(GovernancePermissions.ManageServer);
        }
    }

    public static string Describe()
    {
        lock(Gate)
        {
            if(_sessionId.Length==0||_expiresUtc<=DateTime.UtcNow)return string.Empty;
            return $"REMOTE ASSIST · {_helperUser} · {_mode} · ends {_expiresUtc.ToLocalTime():HH:mm}";
        }
    }
}
