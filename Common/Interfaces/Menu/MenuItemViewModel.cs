namespace Common.Interfaces.Menu;
public class MenuItemViewModel
{
    public string Name { get; set; }
    public string IconClass { get; set; } // e.g., "fa-solid fa-gauge-high"
    public string Url { get; set; }       // e.g., "/P2P/Status"
}