namespace Common.Models.Database;

public class UserDashboardSetting
{
    public string Id { get; set; }
    public string UserId { get; set; }
    public string WidgetId { get; set; }
    public int Order { get; set; }
    public bool IsHidden { get; set; }
    public bool IsCollapsed { get; set; }
}
