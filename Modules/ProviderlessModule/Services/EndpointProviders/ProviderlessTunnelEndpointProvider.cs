using Common.Interfaces;

using Microsoft.AspNetCore.Hosting.Server;

using ProviderlessModule.Configuration;

namespace ProviderlessModule.Services.EndpointProviders;


public class ProviderlessTunnelEndpointProvider : IEndpointProvider
{
    private readonly PortalState _portalState;
    //private readonly IServer _server;

    public ProviderlessTunnelEndpointProvider(PortalState portalState
        //, IServer server
        )
    {
        _portalState = portalState ?? throw new ArgumentNullException(nameof(portalState));
        //_server = server ?? throw new ArgumentNullException(nameof(server));
    }

    public IEnumerable<string> GetEndpointSources() => new string[] { "Public_Tunnel" };

    public IEnumerable<string> ProvideEndpoints(string source)
    {
        var results = new List<string>();
        //int activePort = DiscoverRunningKestrelPort();

        switch (source)
        {
            case "Public_Tunnel":
                // Grabs the live running Cloudflare or Ngrok URL from the Orchestrator's active state
                if (!string.IsNullOrEmpty(_portalState.CurrentTunnelUrl))
                {
                    results.Add(StripProtocol(_portalState.CurrentTunnelUrl));
                }
                break;
        }

        return results;
    }

    private string StripProtocol(string rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl)) return string.Empty;

        // Explicitly removes http:// or https:// wrappers
        int index = rawUrl.IndexOf("://");
        if (index >= 0)
        {
            return rawUrl.Substring(index + 3).TrimEnd('/');
        }
        return rawUrl.TrimEnd('/');
    }

    //private int DiscoverRunningKestrelPort()
    //{
    //    var addressesFeature = _server.Features.Get<IServerAddressesFeature>();
    //    if (addressesFeature != null)
    //    {
    //        foreach (var address in addressesFeature.Addresses)
    //        {
    //            if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
    //            {
    //                return uri.Port;
    //            }
    //        }
    //    }
    //    return 5000; // Fallback configuration default boundary
    //}

    //private string GetLocalIpAddressClean()
    //{
    //    foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
    //    {
    //        if (netInterface.OperationalStatus == OperationalStatus.Up &&
    //            netInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    //        {
    //            var ipProps = netInterface.GetIPProperties();
    //            foreach (var addr in ipProps.UnicastAddresses)
    //            {
    //                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
    //                {
    //                    return addr.Address.ToString();
    //                }
    //            }
    //        }
    //    }
    //    return string.Empty;
    //}
}