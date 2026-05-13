using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;

namespace ProviderlessModule.Infrastructure.Interfaces;

public interface ITunnelSelector
{
    ITunnelProvider GetActiveProvider();
}
