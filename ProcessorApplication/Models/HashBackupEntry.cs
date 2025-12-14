using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Models;

public class HashBackupEntry
{
    public int Id { get; set; }
    public DateTime StampTime { get; set; }

    [Required]
    [StringLength(64)]
    public string BlockHash { get; set; }
    [Required]
    [StringLength(64)]
    public string PreviousBlockHash { get; set; }
    public string CalculateHashableContent()
    {
        return $"{StampTime:yyyy-MM-ddTHH:mm:ss.fff}|{BlockHash}|{PreviousBlockHash}";
    }
}
