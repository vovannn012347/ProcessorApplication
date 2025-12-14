using ProcessorApplication.Database.Models;
using ProcessorApplication.Models;
using ProcessorApplication.Services;

namespace ProcessorApplication.Infrastructure;
public interface IHashStampService
{
    /// <summary>
    /// Generates a new, cryptographically secure hash stamp, calculates its PreviousHash, 
    /// and saves it to the database.
    /// </summary>
    Task<ServerHashStamp> GenerateAndSaveNewStampAsync();

    /// <summary>
    /// Retrieves the hash (key) that was active at or immediately before the specified time.
    /// </summary>
    Task<ServerHashStamp> GetHashByTimeAsync(DateTime time);

    /// <summary>
    /// Retrieves the most recently generated hash (key).
    /// </summary>
    Task<ServerHashStamp> GetLatestHashAsync();

    /// <summary>
    /// Verifies the entire HashStamp chain by recalculating hashes and comparing them.
    /// </summary>
    Task<List<CorruptionReport>> VerifyChainIntegrityAsync();
}
