namespace Common.Interfaces;

public interface IWidgetProvider
{
    // Returns metadata for the Catalog
    IEnumerable<WidgetManifest> GetWidgetManifests();
    bool HasWidget(string widgetId);
    IDashboardWidget GetWidget(string userId, string widgetId);

    // Returns the data update for specific widgets
    Task<Dictionary<string, object>> GetUpdatesAsync(string userId, IEnumerable<string> widgetIds);
}

public class WidgetManifest
{
    public string Id { get; set; }
    public string Name { get; set; } //localization string for widget name
    public string IconClass { get; set; } //widget icon to use
    public double DefaultOrder { get; set; } //default preferred order in other widgets
    public string Roles { get; set; } // requires any of these roles, "Admin,Manager"
    public string ViewPath { get; set; } // default vew path, "~/Views/Access/Widgets/_Status.cshtml"
    public string ScriptPath { get; set; } // e.g., "/_content/AccessModule/js/widget.js"
}