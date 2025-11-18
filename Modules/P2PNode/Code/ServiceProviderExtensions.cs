using Microsoft.Extensions.DependencyInjection;

namespace ProcessorApplication;

public static class ServiceProviderExtensions
{
    public static IEnumerable<T> GetAllServices<T>(this IServiceProvider serviceProvider)
    {
        if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));

        using var scope = serviceProvider.CreateScope();
        var serviceTypes = serviceProvider.GetServices<T>()
            .Select(s => s?.GetType())
            .Where(t => t != null && typeof(T).IsAssignableFrom(t))
            .Distinct();

        return serviceTypes.Select(t => scope.ServiceProvider.GetRequiredService(t))
                          .Cast<T>();
    }

    public static IEnumerable<Type> GetAllServiceTypes<T>(this IServiceProvider serviceProvider)
    {
        if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));

        return serviceProvider.GetServices<T>()
            .Select(s => s.GetType())
            .Where(t => t != null && typeof(T).IsAssignableFrom(t))
            .Distinct();
    }
}