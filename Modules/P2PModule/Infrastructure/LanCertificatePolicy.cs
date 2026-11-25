using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace P2PModule.Infrastructure;

/// <summary>
/// HTTPS for node-to-node requests: certificates are checked normally, except that a node addressed by a PRIVATE IP
/// literal (LAN: 10/8, 172.16/12, 192.168/16, link-local, IPv6 ULA/link-local, loopback) may use a self-signed one.
/// The certificate isn't what proves a node's identity: the challenge handshake does that with the node keys
/// (and drops a node that answers as someone else). Public hosts (the Cloudflare tunnel etc.) are still validated.
/// </summary>
public static class LanCertificatePolicy
{
    public const string HttpClientName = "P2P";

    public static HttpClientHandler CreateHandler() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = Validate
    };

    public static bool Validate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        string? host = request.RequestUri?.Host;
        return host != null && IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsPrivate(ip);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte first = ip.GetAddressBytes()[0];
            return ip.IsIPv6LinkLocal || (first & 0xFE) == 0xFC; // fe80::/10, fc00::/7
        }
        return false;
    }
}
