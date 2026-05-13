namespace Common.Interfaces;

public interface IDashboardWidget
{
    WidgetManifest Manifest { get; }
    Task<object> GetUpdateAsync();
}