using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.Services;

public static class SecurityNetworkPolicy
{
    public const string LocalLanScope = "LAN";
    public const string PrivateNetworkScope = "PrivateNetwork";
    public const string BlockedNetworkScope = "Blocked";

    public static bool IsPrivateOrLoopback(IPAddress address)
    {
        address = NormalizeAddress(address);
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes6 = address.GetAddressBytes();
            return bytes6.Length == 16 && (bytes6[0] & 0xFE) == 0xFC; // RFC4193 unique-local IPv6.
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) // RFC6598 shared/private overlay space, commonly usable by private VPNs
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168);
    }

    public static NetworkRouteClassification ClassifyNetworkRoute(IPAddress? remoteAddress, IPAddress? localAddress)
    {
        if (remoteAddress is null)
            return new NetworkRouteClassification(BlockedNetworkScope, "Blocked network", string.Empty, localAddress?.ToString() ?? string.Empty);

        var remote = NormalizeAddress(remoteAddress);
        var local = localAddress is null ? null : NormalizeAddress(localAddress);
        if (IPAddress.IsLoopback(remote))
            return new NetworkRouteClassification(LocalLanScope, "LAN OTP", remote.ToString(), local?.ToString() ?? string.Empty);
        if (!IsPrivateOrLoopback(remote))
            return new NetworkRouteClassification(BlockedNetworkScope, "Blocked network", remote.ToString(), local?.ToString() ?? string.Empty);
        if (local is not null && IsSameLocalSubnet(remote, local))
            return new NetworkRouteClassification(LocalLanScope, "LAN OTP", remote.ToString(), local.ToString());

        // A private/routed source that is not on the server interface's local subnet is treated as
        // WireGuard/private-network access. It receives a separate OTP scope, bound to that route.
        return new NetworkRouteClassification(PrivateNetworkScope, "Private Network OTP", remote.ToString(), local?.ToString() ?? string.Empty);
    }

    public static string GetOtpLabel(string? networkScope) =>
        string.Equals(networkScope, LocalLanScope, StringComparison.OrdinalIgnoreCase) ? "LAN OTP" : "Private Network OTP";

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsSameLocalSubnet(IPAddress remote, IPAddress local)
    {
        remote = NormalizeAddress(remote);
        local = NormalizeAddress(local);
        if (remote.AddressFamily != local.AddressFamily) return false;
        if (remote.Equals(local)) return true;

        if (remote.AddressFamily == AddressFamily.InterNetwork)
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        var candidate = NormalizeAddress(unicast.Address);
                        if (!candidate.Equals(local) || unicast.IPv4Mask is null) continue;
                        var remoteBytes = remote.GetAddressBytes();
                        var localBytes = local.GetAddressBytes();
                        var maskBytes = unicast.IPv4Mask.GetAddressBytes();
                        if (maskBytes.Length != remoteBytes.Length) continue;
                        for (var i = 0; i < maskBytes.Length; i++)
                            if ((remoteBytes[i] & maskBytes[i]) != (localBytes[i] & maskBytes[i])) return false;
                        return true;
                    }
                }
            }
            catch { }

            // Fail-safe compatibility fallback for normal office LANs if Windows does not expose the mask.
            var rb = remote.GetAddressBytes();
            var lb = local.GetAddressBytes();
            return rb.Length == 4 && lb.Length == 4 && rb[0] == lb[0] && rb[1] == lb[1] && rb[2] == lb[2];
        }

        // The server currently binds an explicit IPv4 interface, but keep ULA IPv6 classification conservative.
        var r6 = remote.GetAddressBytes();
        var l6 = local.GetAddressBytes();
        return r6.Length == 16 && l6.Length == 16 && r6.Take(8).SequenceEqual(l6.Take(8));
    }

    public sealed record NetworkRouteClassification(string Scope, string OtpLabel, string RemoteAddress, string LocalAddress)
    {
        public bool IsBlocked => string.Equals(Scope, BlockedNetworkScope, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryResolvePrivateBindAddress(string value, out IPAddress address, out string error)
    {
        address = IPAddress.None;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Select a private LAN or loopback address before enabling Secure LAN.";
            return false;
        }
        if (!IPAddress.TryParse(value.Trim(), out address!))
        {
            error = "Secure LAN bind address must be a concrete IPv4 address, not a host name or wildcard.";
            return false;
        }
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            error = "0.0.0.0 / wildcard binding is blocked. AAH binds only to one explicit private interface.";
            return false;
        }
        if (!IsPrivateOrLoopback(address))
        {
            error = "Public/non-private bind addresses are blocked. Use loopback, RFC1918 LAN, RFC6598 private-overlay, or a private VPN interface.";
            return false;
        }
        return true;
    }

    public static IReadOnlyList<string> GetPrivateBindAddresses()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && IsPrivateOrLoopback(address))
                        values.Add(address.ToString());
                }
            }
        }
        catch { }
        return values.OrderBy(x => x.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenBy(x => x).ToArray();
    }

    /// <summary>
    /// Returns active private overlay/VPN IPv4 addresses that can carry AAH over the Internet
    /// without exposing the Main Server on a public/wildcard socket. WireGuard/Wintun/Tailscale/
    /// ZeroTier/VPN-labelled interfaces are recognised, together with RFC6598 overlay addresses.
    /// </summary>
    public static IReadOnlyList<string> GetPrivateOverlayBindAddresses()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var identity = (nic.Name + " " + nic.Description).ToLowerInvariant();
                var overlayNic = nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel ||
                                 identity.Contains("wireguard", StringComparison.Ordinal) ||
                                 identity.Contains("wintun", StringComparison.Ordinal) ||
                                 identity.Contains("tailscale", StringComparison.Ordinal) ||
                                 identity.Contains("zerotier", StringComparison.Ordinal) ||
                                 identity.Contains("vpn", StringComparison.Ordinal);
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = NormalizeAddress(unicast.Address);
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || !IsPrivateOrLoopback(address)) continue;
                    var bytes = address.GetAddressBytes();
                    var rfc6598 = bytes.Length == 4 && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
                    if (overlayNic || rfc6598) values.Add(address.ToString());
                }
            }
        }
        catch { }
        return values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<IPAddress> GetServerListenAddresses(IPAddress primary)
    {
        var values = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase)
        {
            [primary.ToString()] = primary
        };
        foreach (var text in GetPrivateOverlayBindAddresses())
            if (IPAddress.TryParse(text, out var address) && IsPrivateOrLoopback(address)) values[address.ToString()] = address;
        return values.Values.ToArray();
    }

    public static string BuildDiagnosticText(ApplicationPreferenceService settings)
    {
        var lanEnabled = settings.GetBool(ApplicationSettingKeys.SecurityLanEnabled);
        var bind = settings.Get(ApplicationSettingKeys.SecurityLanBindAddress);
        var port = settings.GetInt(ApplicationSettingKeys.SecurityLanPort, 47831);
        var tallyHost = settings.Get(ApplicationSettingKeys.TallyHost);
        var lines = new List<string>
        {
            "SQLite authority: local application-data database only; network/UNC database sharing is blocked by architecture.",
            "Imported documents: AAH Document Vault never shell-executes source files; it only selects them in File Explorer.",
            "LAN transport: HTTPS native-client API only; browser/PWA accounting access is not exposed.",
            "Identity: Administrator-created AAH User ID/password + one Registration OTP when Windows-device trust is first established, expires after 30 days of AAH inactivity, or is reset by an Administrator. Routine registered-device login does not use OTP. Same-subnet registration requests are labelled LAN; routed private/WireGuard registration requests are separately labelled Private Network. Device enrollment tokens are stored only as hashes.",
            "Authorization: role permissions plus explicit Client/FY scope; Phase 1 network accounting views are read-only with controlled uploads/basic exports.",
            "Retention: the Main Server keeps independent retained copies; deleting a local source does not delete its server archive.",
            "Public exposure: wildcard/public binds are rejected; AAH does not publish Tally, SQLite, OCR or Python endpoints.",
            $"Remote access: use a private Internet/VPN overlay. Detected overlay address(es): {(GetPrivateOverlayBindAddresses().Count==0?"none":string.Join(", ",GetPrivateOverlayBindAddresses()))}. AAH classifies routed private/VPN requests separately from same-subnet LAN requests; public/wildcard port exposure remains blocked.",
            "Distribution: Windows network client and Android APK are approved/distributed by the Main Server with one-use expiring tickets.",
            "Endpoint protection: AAH does not disable Windows Firewall, Microsoft Defender/antivirus, or create antivirus exclusions."
        };

        if (lanEnabled)
        {
            lines.Add(TryResolvePrivateBindAddress(bind, out var ip, out var error)
                ? $"Secure LAN requested on https://{ip}:{port}."
                : $"Secure LAN configuration blocked: {error}");
        }
        else lines.Add(settings.GetBool(ApplicationSettingKeys.SecurityServerMode) ? "Main Server mode: enabled; LAN/WireGuard HTTPS listener is currently disabled." : "Main Server mode / LAN server: disabled (Local Only mode)." );

        if (settings.GetBool(ApplicationSettingKeys.SecurityServerMode))
        {
            lines.Add($"Trust leases: Windows={settings.GetInt(ApplicationSettingKeys.SecurityPcExpiryDays,30)} day(s), Android={settings.GetInt(ApplicationSettingKeys.SecurityAndroidExpiryDays,15)} day(s), folder links={settings.GetInt(ApplicationSettingKeys.SecurityFolderLinkExpiryDays,15)} day(s).");
            lines.Add("MFA policy: required for native network/Android enrollment and login.");
        }

        if (IPAddress.TryParse(tallyHost, out var tallyAddress) && !IsPrivateOrLoopback(tallyAddress))
            lines.Add("WARNING: Tally host is not loopback/private. Keep Tally local/private; never expose port 9000 to the internet.");
        else
            lines.Add("Tally endpoint: local/private." );

        lines.Add(Path.IsPathRooted(ApplicationDataPaths.DatabasePath) && !ApplicationDataPaths.DatabasePath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "Database path: local Windows profile storage."
            : "WARNING: database path appears non-local." );
        lines.Add(GetWindowsFirewallDiagnostic());
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetWindowsFirewallDiagnostic()
    {
        if (!OperatingSystem.IsWindows()) return "Windows Firewall status: unavailable on this platform.";
        try
        {
            const string basePath = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
            var profiles = new[] { "DomainProfile", "StandardProfile", "PublicProfile" };
            var states = new List<string>();
            var allEnabled = true;
            foreach (var profile in profiles)
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{basePath}\{profile}", writable: false);
                var enabled = Convert.ToInt32(key?.GetValue("EnableFirewall", 1) ?? 1) != 0;
                states.Add($"{profile.Replace("Profile", string.Empty)}={(enabled ? "On" : "OFF")}");
                allEnabled &= enabled;
            }
            return allEnabled
                ? $"Windows Firewall profiles: {string.Join(", ", states)}. AAH creates no broad public inbound rule."
                : $"WARNING: Windows Firewall profile disabled: {string.Join(", ", states)}. Re-enable Windows Firewall before using LAN mode.";
        }
        catch
        {
            return "Windows Firewall status could not be read. Keep Windows Firewall enabled, especially on Public networks.";
        }
    }
}
