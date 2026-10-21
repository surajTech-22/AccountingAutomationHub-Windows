using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

public static class NetworkClientRuntime
{
    private static readonly HashSet<string> PermissionSet = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsNetworkClient { get; private set; }
    public static string Server { get; set; } = string.Empty;
    public static string UserName { get; set; } = string.Empty;
    public static string DisplayName { get; set; } = string.Empty;
    public static string DeviceId { get; set; } = string.Empty;
    public static string DeviceToken { get; set; } = string.Empty;
    public static string SessionToken { get; set; } = string.Empty;
    public static DateTime SessionExpiresUtc { get; set; }
    public static string RoleName { get; set; } = string.Empty;
    public static string RootFolder { get; private set; } = string.Empty;
    public static string DefaultServerEndpoint { get; private set; } = string.Empty;
    public static string DefaultRemoteAssistEndpoint { get; private set; } = string.Empty;
    public static IReadOnlyCollection<string> Permissions => PermissionSet.ToArray();
    public static long AuthorizationRevision { get; set; }
    public static long DevicePolicyRevision { get; set; }
    public static long ClientAccessRevision { get; set; }
    public static long SecurityPolicyRevision { get; set; }
    public static long AccountingDataRevision { get; set; }
    public static bool AccountingAccessSuspended { get; set; }
    public static string AccountingAccessSuspensionReason { get; set; } = string.Empty;
    public static string DeviceHealthState { get; set; } = DeviceHealthStates.Healthy;
    public static int DeviceRiskScore { get; set; }

    public static bool Detect(string[] args)
    {
        var installationRole = InstallationRoleService.ReadRole();
        if (installationRole.Equals(InstallationRoleService.MainServer, StringComparison.OrdinalIgnoreCase))
        {
            // A Main Server installation can never be turned into a LAN client by a stale
            // migration marker accidentally copied beside the executable.
            IsNetworkClient = false;
            return false;
        }

        IsNetworkClient = installationRole.Equals(InstallationRoleService.LanClient, StringComparison.OrdinalIgnoreCase) ||
                          (installationRole.Length == 0 &&
                           (args.Any(x => x.Equals("--network-client", StringComparison.OrdinalIgnoreCase)) ||
                            File.Exists(Path.Combine(AppContext.BaseDirectory, "AAH_NETWORK_CLIENT_MODE"))));
        if (!IsNetworkClient) return false;
        Environment.SetEnvironmentVariable("AAH_NETWORK_CLIENT_ACTIVE", "1", EnvironmentVariableTarget.Process);
        RootFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Accounting Automation Hub Network");
        var endpointFile = Path.Combine(AppContext.BaseDirectory, "AAH_MAIN_SERVER_ENDPOINT");
        if (File.Exists(endpointFile))
        {
            var configured = File.ReadAllText(endpointFile).Trim();
            if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                DefaultServerEndpoint = configured.TrimEnd('/');
        }
        var remoteAssistEndpointFile = Path.Combine(AppContext.BaseDirectory, "AAH_REMOTE_ASSIST_ENDPOINT");
        if (File.Exists(remoteAssistEndpointFile))
        {
            var configured = File.ReadAllText(remoteAssistEndpointFile).Trim();
            if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                DefaultRemoteAssistEndpoint = configured.TrimEnd('/');
        }
        // Resolve the endpoint immediately. The embedded package marker is authoritative, but an
        // already-enrolled PC can recover from its Windows Credential Manager device/session record
        // if that dynamic marker was lost during an older install/update. This prevents LAN Settings,
        // Remote AAH and self-update from depending on a transient in-memory Server value.
        Server = ResolveServerEndpoint();

        ApplicationDataPaths.ConfigureRootOverride(RootFolder);
        Directory.CreateDirectory(Path.Combine(RootFolder, "Data"));
        return true;
    }

    public static string ResolveServerEndpoint(string? fallback = null)
    {
        var candidates = new List<string>();
        // Server is the route that startup actually proved reachable. Keep that live route first.
        // Older builds put the package's office-LAN marker ahead of Server; a PC could therefore
        // authenticate over the private Internet/VPN endpoint and then every background service
        // (Remote Assist, Team, Work, policy/update heartbeat) silently switched back to an
        // unreachable office address. The package marker is a recovery fallback, not an override
        // for a route that already answered in this running session.
        if (!string.IsNullOrWhiteSpace(Server)) candidates.Add(Server);
        if (!string.IsNullOrWhiteSpace(fallback)) candidates.Add(fallback);
        if (!string.IsNullOrWhiteSpace(DefaultServerEndpoint)) candidates.Add(DefaultServerEndpoint);

        try
        {
            var device = NetworkClientCredentialStore.LoadDevice();
            if (!string.IsNullOrWhiteSpace(device.Server)) candidates.Add(device.Server);
        }
        catch { }

        try
        {
            var session = NetworkClientCredentialStore.LoadSession();
            if (session is not null && !string.IsNullOrWhiteSpace(session.Server)) candidates.Add(session.Server);
        }
        catch { }

        foreach (var candidate in candidates)
        {
            var value = candidate.Trim().TrimEnd('/');
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) continue;

            Server = value;

            // When the package marker is missing but the enrolled-device credential still contains
            // the HTTPS endpoint, repair the per-user canonical installation opportunistically.
            // This writes only the dynamic Main Server endpoint marker; credentials/tokens are not
            // written to the application folder.
            if (string.IsNullOrWhiteSpace(DefaultServerEndpoint))
            {
                DefaultServerEndpoint = value;
                try
                {
                    var endpointFile = Path.Combine(AppContext.BaseDirectory, "AAH_MAIN_SERVER_ENDPOINT");
                    File.WriteAllText(endpointFile, value, new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            return value;
        }

        return string.Empty;
    }


    public static IReadOnlyList<string> GetServerEndpointCandidates(string? fallback = null)
    {
        var values = new List<string>();
        void Add(string? raw)
        {
            var value=(raw??string.Empty).Trim().TrimEnd('/');
            if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||!uri.Scheme.Equals(Uri.UriSchemeHttps,StringComparison.OrdinalIgnoreCase))return;
            if(!values.Any(x=>x.Equals(value,StringComparison.OrdinalIgnoreCase)))values.Add(value);
        }
        // Prefer the explicit Internet/private-VPN Remote Assist route when the Main Server package provides one.
        Add(DefaultRemoteAssistEndpoint);
        Add(DefaultServerEndpoint);
        Add(Server);
        Add(fallback);
        try{var device=NetworkClientCredentialStore.LoadDevice();Add(device.Server);}catch{}
        try{var session=NetworkClientCredentialStore.LoadSession();Add(session?.Server);}catch{}
        return values;
    }

    public static string ResolveRemoteAssistEndpoint()
    {
        // Startup probes the private Internet/VPN endpoint first and records the first route that
        // actually answered in Server. Remote Assist must follow that proven route instead of
        // blindly returning an unreachable package marker.
        var selected = (Server ?? string.Empty).Trim().TrimEnd('/');
        if (Uri.TryCreate(selected, UriKind.Absolute, out var selectedUri) && selectedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return selected;
        var candidate = DefaultRemoteAssistEndpoint?.Trim().TrimEnd('/') ?? string.Empty;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var remote) && remote.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return candidate;
        return ResolveServerEndpoint();
    }

    public static string RemoteAssistTransportLabel
    {
        get
        {
            var selected = ResolveRemoteAssistEndpoint();
            if (string.IsNullOrWhiteSpace(selected)) return "Remote Assist route unavailable";
            return !string.IsNullOrWhiteSpace(DefaultRemoteAssistEndpoint) &&
                   selected.Equals(DefaultRemoteAssistEndpoint.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                   !DefaultRemoteAssistEndpoint.Equals(DefaultServerEndpoint, StringComparison.OrdinalIgnoreCase)
                ? "Internet Private Network"
                : "Private Main Server Route";
        }
    }

    public static void ApplyIdentity(string userName, string displayName, string roleName, DateTime expiresUtc, IEnumerable<string>? permissions)
    {
        UserName = userName?.Trim() ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? UserName : displayName.Trim();
        RoleName = roleName?.Trim() ?? string.Empty;
        SessionExpiresUtc = expiresUtc;
        PermissionSet.Clear();
        if (permissions is null) return;
        foreach (var permission in permissions.Where(x => !string.IsNullOrWhiteSpace(x))) PermissionSet.Add(permission.Trim());
    }

    public static bool HasPermission(string permission) => PermissionSet.Contains(permission);
    public static bool HasAnyPermission(params string[] permissions) => permissions.Any(HasPermission);

    public static void ClearSession()
    {
        SessionToken = string.Empty;
        SessionExpiresUtc = default;
        RoleName = string.Empty;
        DisplayName = string.Empty;
        PermissionSet.Clear();
        AccountingDataRevision = 0;
    }
}
