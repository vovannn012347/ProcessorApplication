namespace Common.Interfaces;

public interface IWidgetStatusModel
{
    string WidgetId { get; }      // ex "access-connectivity-status"
    string DisplayName { get; }  // localizable display name key, for example WidStat_Access_Gateway"
    string IconClass { get; }    // "fa-solid fa-signal", etc
    string ContentUrl { get; }   // the ajax endpoint for the partial view
    int DefaultOrder { get; }
}