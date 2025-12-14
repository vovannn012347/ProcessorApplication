using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Configuration;

public interface IDbConfigurationProvider : IConfigurationProvider
{
    void TriggerReload();
}
