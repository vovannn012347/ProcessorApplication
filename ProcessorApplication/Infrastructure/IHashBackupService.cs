using ProcessorApplication.Database.Models;
using ProcessorApplication.Models;
using ProcessorApplication.Services;

namespace ProcessorApplication.Infrastructure
{
    public interface IHashBackupService
    {
        void BackupServerBlock(ServerHashStamp backupEntry);
        HashBackupEntry GetBackupServerBlock(int id);
    }
}