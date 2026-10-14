using System.Net;
using System.Net.Sockets;

namespace Aegis.Agent.Services;

/// <summary>
/// Pure classification of a TCP endpoint: where the peer is (this PC, local
/// network, provider NAT, Internet) and which well-known service the port means.
/// </summary>
public static class ConnectionClassifier
{
    public const string Loopback = "loopback";
    public const string Lan = "lan";
    public const string ProviderNat = "provider";
    public const string Internet = "internet";
    public const string Multicast = "multicast";
    public const string Unspecified = "none";

    private static readonly Dictionary<int, string> Services = new()
    {
        [20] = "FTP-data", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP",
        [53] = "DNS", [80] = "HTTP", [110] = "POP3", [123] = "NTP", [135] = "RPC",
        [139] = "NetBIOS", [143] = "IMAP", [389] = "LDAP", [443] = "HTTPS", [445] = "SMB",
        [465] = "SMTPS", [554] = "RTSP", [587] = "SMTP-submit", [636] = "LDAPS",
        [853] = "DNS-over-TLS", [993] = "IMAPS", [995] = "POP3S", [1433] = "MS SQL",
        [1883] = "MQTT", [3306] = "MySQL", [3389] = "RDP", [3478] = "STUN/TURN",
        [5060] = "SIP", [5222] = "XMPP", [5228] = "Google Play", [5353] = "mDNS",
        [5432] = "PostgreSQL", [5900] = "VNC", [6379] = "Redis", [8080] = "HTTP-alt",
        [8443] = "HTTPS-alt", [8765] = "AEGIS", [8766] = "AETHER relay",
        [9001] = "Tor", [27017] = "MongoDB", [27015] = "Steam", [27036] = "Steam",
        [54329] = "PostgreSQL (AETHER)", [54331] = "PostgreSQL (demo)"
    };

    public static string Scope(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
            return Unspecified;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return Unspecified;
        if (IPAddress.IsLoopback(ip))
            return Loopback;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] >= 224 && b[0] <= 239) return Multicast;
            if (b[0] == 10) return Lan;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return Lan;
            if (b[0] == 192 && b[1] == 168) return Lan;
            if (b[0] == 169 && b[1] == 254) return Lan;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return ProviderNat;
            return Internet;
        }

        if (ip.IsIPv6Multicast) return Multicast;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return Lan;
        var first = ip.GetAddressBytes()[0];
        if ((first & 0xFE) == 0xFC) return Lan; // fc00::/7 unique local
        return Internet;
    }

    public static string? Service(int port) =>
        Services.TryGetValue(port, out var name) ? name : null;

    /// <summary>
    /// Service of a connection: the well-known side wins (remote for outbound,
    /// local for inbound), otherwise whichever port is known.
    /// </summary>
    public static string? Service(int localPort, int remotePort, string direction) =>
        direction == "inbound"
            ? Service(localPort) ?? Service(remotePort)
            : Service(remotePort) ?? Service(localPort);

    public static string Direction(string state, int localPort, IReadOnlySet<int> listeningPorts) =>
        state.Equals("Listen", StringComparison.OrdinalIgnoreCase)
            ? "listen"
            : listeningPorts.Contains(localPort) ? "inbound" : "outbound";
}
