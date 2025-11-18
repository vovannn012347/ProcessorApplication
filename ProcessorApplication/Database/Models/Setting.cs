namespace ProcessorApplication.Sqlite.Models;

public class Setting
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty; // e.g., "GossipIntervalSeconds"
    public string Area { get; set; } = string.Empty; // e.g., "P2P"
    public string? Value { get; set; } = string.Empty; // e.g., "10"
    public bool IsSensitive { get; set; } // For encryption of settings
}
