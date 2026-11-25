using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Common.Interfaces;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace P2PModule.Services.Discovery;

/// <summary>
/// Offers this node's LAN addresses ("ip:port", the port the app actually listens on) in the P2P settings
/// "Add Endpoints" picker, one source per address, e.g. "Local_Network 192.168.1.10".
/// Picker-only (IManualEndpointProvider): the endpoint advertiser never announces them on its own.
/// </summary>
public sealed class LocalNetworkEndpointProvider : IManualEndpointProvider
{
    private const string SourcePrefix = "Local_Network ";

    private readonly IServer _server;

    public LocalNetworkEndpointProvider(IServer server)
    {
        _server = server;
    }

    public IEnumerable<string> GetEndpointSources()
    {
        if (ListenPorts().Count == 0) return Array.Empty<string>();
        return LocalAddresses().Select(ip => SourcePrefix + ip).ToList();
    }

    public IEnumerable<string> ProvideEndpoints(string source)
    {
        if (string.IsNullOrEmpty(source) || !source.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        if (!IPAddress.TryParse(source.Substring(SourcePrefix.Length).Trim(), out var ip))
            return Array.Empty<string>();

        string host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        return ListenPorts().Select(port => $"{host}:{port}").ToList();
    }

    /// <summary>Ports of the addresses Kestrel listens on that other machines can reach (not loopback-only).</summary>
    private List<int> ListenPorts()
    {
        var ports = new List<int>();
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses == null) return ports;

        foreach (var address in addresses)
        {
            // Kestrel reports wildcard bindings as "http://[::]:5000", "http://0.0.0.0:5000" or "http://+:5000"
            string normalized = address.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0");
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Port <= 0) continue;

            string host = uri.Host.Trim('[', ']');
            bool reachable = host == "0.0.0.0" || host == "::"
                || (IPAddress.TryParse(host, out var bound) && !IPAddress.IsLoopback(bound));
            if (reachable && !ports.Contains(uri.Port)) ports.Add(uri.Port);
        }
        return ports;
    }

    /// <summary>Up, non-loopback, non-virtual adapters' IPv4 and IPv6 addresses (link-local skipped).</summary>
    private static List<IPAddress> LocalAddresses()
    {
        var result = new List<IPAddress>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return result; }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (LooksVirtual(nic)) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var unicast in props.UnicastAddresses)
            {
                var ip = unicast.Address;
                if (IPAddress.IsLoopback(ip)) continue;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (ip.GetAddressBytes()[0] == 169) continue; // 169.254.x.x APIPA
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) continue;
                }
                else continue;

                if (!result.Contains(ip)) result.Add(ip);
            }
        }

        // IPv4 first, it's what people usually type
        return result.OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
    }

    private static readonly string[] VirtualMarkers =
    {
        "hyper-v", "vethernet", "virtualbox", "vmware", "docker", "wsl", "vpn", "tap-", "wireguard", "tailscale", "zerotier"
    };

    private static bool LooksVirtual(NetworkInterface nic)
    {
        string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        if (VirtualMarkers.Any(text.Contains)) return true;
        // Linux bridge/container interfaces
        string name = nic.Name.ToLowerInvariant();
        return name.StartsWith("docker") || name.StartsWith("veth") || name.StartsWith("br-") || name.StartsWith("virbr");
    }
}
