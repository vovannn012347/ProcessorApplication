using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Services.HashStamps;

public interface IAuditHashKeyProvider
{
    ServerHashStamp GetCurrentServerHash();
    ServerHashStamp GetHashByTime(DateTime time);
}

// Concrete service implementation
public class ServerSecurityHashProvider : IAuditHashKeyProvider
{
    private readonly IHashStampService _hashStampService;

    public ServerSecurityHashProvider(IHashStampService hashStampService)
    {
        _hashStampService = hashStampService;
    }

    public ServerHashStamp GetCurrentServerHash()
    {
        // WARNING: Since this is synchronous, it must be carefully called or converted.
        // For simplicity in a web request context, we block temporarily.
        // In production, methods that rely on the latest hash should be async.
        return _hashStampService.GetLatestHashAsync().Result;
    }

    public ServerHashStamp GetHashByTime(DateTime time)
    {
        // Same warning: blocking call.
        return _hashStampService.GetHashByTimeAsync(time).Result;
    }
}