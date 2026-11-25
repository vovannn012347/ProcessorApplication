using System.Net;
using System.Net.Sockets;

namespace ProcessorApplication.Policy;

/// <summary>
/// Which computers may use the admin pages: always this computer (loopback, or a request to this machine's own address),
/// plus the addresses and networks in SecuritySettings.AdminNetworks, e.g. "192.168.1.0/24", "10.0.0.5", "fd00::/8".
/// Empty list = this computer only. Requests through the Cloudflare tunnel carry the visitor's real address
/// (X-Forwarded-For from the local cloudflared), so they only match if that public address is listed.
/// </summary>
public static class AdminNetworkRules
{
    /// <summary>This computer: loopback, or the browser runs on this machine and uses its own LAN address.</summary>
    public static bool IsSelf(IPAddress? remote, IPAddress? local)
    {
        if (remote == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;
        if (local == null) return false;
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        return remote.Equals(local);
    }

    public static bool IsAllowed(IPAddress? remote, IPAddress? local, IEnumerable<string>? rules)
    {
        if (remote == null) return false;
        if (IsSelf(remote, local)) return true;
        if (rules == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();

        foreach (var rule in rules)
        {
            if (!string.IsNullOrWhiteSpace(rule) && TryParse(rule, out var network, out int prefix) && Matches(remote, network, prefix)) return true;
        }
        return false;
    }

    /// <summary>Entries that are neither an IP address nor a network ("address/prefix").</summary>
    public static List<string> InvalidEntries(IEnumerable<string>? rules)
    {
        var bad = new List<string>();
        if (rules == null) return bad;
        foreach (var rule in rules)
        {
            if (!string.IsNullOrWhiteSpace(rule) && !TryParse(rule, out _, out _)) bad.Add(rule.Trim());
        }
        return bad;
    }

    /// <summary>Trimmed, without empty entries and duplicates (case-insensitive), in the given order.</summary>
    public static List<string> Clean(IEnumerable<string>? rules) =>
        (rules ?? Enumerable.Empty<string>())
            .Select(r => (r ?? string.Empty).Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    private static bool TryParse(string rule, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        string text = rule.Trim();
        string? prefixText = null;

        int slash = text.IndexOf('/');
        if (slash >= 0)
        {
            prefixText = text[(slash + 1)..];
            text = text[..slash];
        }

        if (!IPAddress.TryParse(text, out var parsed)) return false;
        if (parsed.IsIPv4MappedToIPv6) parsed = parsed.MapToIPv4();

        int maxBits = parsed.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixText == null) prefix = maxBits;
        else if (!int.TryParse(prefixText, out prefix) || prefix < 0 || prefix > maxBits) return false;

        network = parsed;
        return true;
    }

    private static bool Matches(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily) return false;

        byte[] a = address.GetAddressBytes();
        byte[] n = network.GetAddressBytes();
        int fullBytes = prefix / 8, restBits = prefix % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (a[i] != n[i]) return false;
        }
        if (restBits == 0) return true;

        int mask = 0xFF << (8 - restBits) & 0xFF;
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }
}
