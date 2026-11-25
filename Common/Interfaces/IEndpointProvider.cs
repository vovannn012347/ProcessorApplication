using System.Collections;

namespace Common.Interfaces;

public interface IEndpointProvider
{
    IEnumerable<string> GetEndpointSources();

    IEnumerable<string> ProvideEndpoints(string source);
}

/// <summary>
/// Marker for an IEndpointProvider whose endpoints are offered in the settings picker only (e.g. the node's LAN
/// addresses): the user adds them by hand. The endpoint advertiser never auto-advertises them.
/// </summary>
public interface IManualEndpointProvider : IEndpointProvider
{
}

/// <summary>
/// Implemented by whoever advertises the node's endpoints (P2PModule). A module whose IEndpointProvider output changed
/// (e.g. a tunnel came up, changed its URL or went down) calls NotifyEndpointsChanged; the advertiser then re-reads all
/// providers and re-announces the node only if its endpoint list actually changed.
/// Resolve it as IEnumerable&lt;IEndpointChangeNotifier&gt;: it's empty when no advertiser module is installed.
/// </summary>
public interface IEndpointChangeNotifier
{
    void NotifyEndpointsChanged();
}