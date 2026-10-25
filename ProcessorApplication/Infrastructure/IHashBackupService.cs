using ProcessorApplication.Database.Models;
using ProcessorApplication.Models;
using ProcessorApplication.Services;

namespace ProcessorApplication.Infrastructure
{
    public interface IHashBackupService
    {
        /// <summary>Writes the stamp's backup entry (never the key) locally and, if enabled, to the secure mirror.</summary>
        void BackupServerBlock(ServerHashStamp backupEntry);

        /// <summary>Reads a stamp's backup entry: local copy first, then the mirror.</summary>
        HashBackupEntry GetBackupServerBlock(int id);

        /// <summary>All backup entries found (local and mirror, newest copy wins), ordered by stamp time.</summary>
        List<HashBackupEntry> GetAllBackupEntries();
    }
}