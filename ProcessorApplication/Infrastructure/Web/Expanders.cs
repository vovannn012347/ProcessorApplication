using Microsoft.AspNetCore.Mvc.Razor;

namespace ProcessorApplication.Infrastructure.Web;
public static class Expanders
{
    public static IServiceCollection TryAddHttpClient(this IServiceCollection services)
    {
        // Check if the primary HttpClient factory anchor interface is already in the DI container
        bool isAlreadyRegistered = services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHttpClientFactory));

        if (!isAlreadyRegistered)
        {
            services.AddHttpClient();
        }

        return services;
    }
}