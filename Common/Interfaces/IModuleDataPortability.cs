namespace ProcessorApplication.Models.User;

/// <summary>
/// Implemented by modules that store user-specific data.
/// Handles the export and import logic for that specific module's domain.
/// </summary>
public interface IModuleDataPortability
{
    /// <summary>
    /// A unique key for the folder/file name in the export archive (e.g., "MedicalRecords", "P2PLogs").
    /// </summary>
    string DataKey { get; }

    /// <summary>
    /// Exports user data to the specified directory.
    /// User data exported depends on direct implementation of 
    /// Usually writes a JSON file (e.g., {destinationFolder}/{DataKey}.json).
    /// </summary>
    Task<string> ExportUserDataAsync(string userName, string userKey, string destinationFolder);

    /// <summary>
    /// Imports user data from the specified directory.
    /// Reads the expected file and restores records to the database.
    /// </summary>
    Task<string> ImportUserDataAsync(string userName, string userKey, string sourceFolder);
}