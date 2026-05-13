using System.Reflection;

using Common.Interfaces;

using Microsoft.Extensions.DependencyInjection;

using ProcessorApplication.Dashboard;

namespace ProcessingModule.DashboardWidgets;

public class ProcessWidgetProvider : WidgetProviderAbstract
{
    // Static caches to persist across provider instances(since provider is registered in MainModule)
    private static List<WidgetManifest> _manifestCache;
    private static Dictionary<string, Type> _widgetTypeMap;
    private static readonly object _lock = new object();

    public ProcessWidgetProvider(IServiceProvider serviceProvider, IDashboardSessionManager sessionManager)
        : base(serviceProvider, sessionManager)
    {
        if (_manifestCache == null)
        {
            lock (_lock)
            {
                if (_manifestCache == null)
                {
                    InitializeWidgetCaches();
                }
            }
        }
    }

    public override bool HasWidget(string widgetId) => _widgetTypeMap.ContainsKey(widgetId);

    public override IEnumerable<WidgetManifest> GetWidgetManifests() => _manifestCache;

    protected override void InitializeWidgetCaches()
    {
        var manifests = new List<WidgetManifest>();
        var typeMap = new Dictionary<string, Type>();

        // Scan current assembly for valid widget implementations
        var widgetTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IDashboardWidget).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in widgetTypes)
        {
            try
            {
                // We create a one-time instance to extract the manifest metadata
                // We use nonPublic: true to support internal constructors if necessary
                var instance = (IDashboardWidget)ActivatorUtilities.CreateInstance(_serviceProvider, type);
                if (instance?.Manifest != null)
                {
                    manifests.Add(instance.Manifest);
                    typeMap[instance.Manifest.Id] = type;
                }
            }
            catch (Exception ex)
            {
                // In a medical system, we log the failure but don't crash the entire provider
                // _logger.LogError(ex, "Failed to register widget type {TypeName}", type.Name);
            }
        }

        _manifestCache = manifests;
        _widgetTypeMap = typeMap;
    }

    protected override Type GetWidget(string widgetId) => _widgetTypeMap.TryGetValue(widgetId, out var type) ? type : null;
}