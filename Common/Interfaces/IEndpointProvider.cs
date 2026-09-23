using System.Collections;

namespace Common.Interfaces;

public interface IEndpointProvider
{
    IEnumerable<string> GetEndpointSources();

    IEnumerable<string> ProvideEndpoints(string source);
}