using Common.Infrastructure;

namespace Common.Interfaces;

/// <summary>
/// Implemented by modules that store user-specific data.
/// Handles the export and import logic for that specific module's domain.
/// </summary>
//public interface IModuleDataPortability
//{
//    string DataKey { get; }

//    /// <summary>
//    /// Exports user data to the specified directory.
//    /// User data exported depends on direct implementation of 
//    /// Usually writes a JSON file (e.g., {destinationFolder}/{DataKey}.json).
//    /// </summary>
//    Task<string> ExportUserDataAsync(string userName, string userKey, string destinationFolder);

//    /// <summary>
//    /// Imports user data from the specified directory.
//    /// Reads the expected file and restores records to the database.
//    /// </summary>
//    Task<string> ImportUserDataAsync(string userName, string userKey, string sourceFolder);
//}

public interface IPortabilityHandler
{
    /// <summary>
    /// High-level identifier for the module (e.g., "Processing", "Identity")
    /// A unique key for the folder/file name in the export archive (e.g., "MedicalRecords", "P2PLogs").
    /// </summary>
    string ModuleIdentifier { get; }

    // Returns a summary of what this module can export without loading heavy data
    Task<ModuleExportSummary> GetExportSummaryAsync(string userId);

    // Fetches the granular list of items (e.g., specific Job IDs) with support for pagination
    Task<PaginatedList<ExportableItem>> GetExportableItemsAsync(string userId, int pageIndex, int pageSize);

    // Bundles the selected items into the destination folder
    // Returns a list of relative file paths produced
    Task<List<string>> ProcessExportAsync(string userId, List<string> selectedItemIds, string userKey, string destinationFolder);

    // Re-imports data from a provided folder
    Task<bool> ProcessImportAsync(string userId, string sourceFolder);
}

public class ModuleExportSummary
{
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int TotalItemCount { get; set; }
}

public class ExportableItem
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
}