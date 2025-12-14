using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Database.Models
{
    public class ServerHashStamp
    {
        public int Id { get; set; }

        public DateTime StampTime { get; set; } //datetime in !UTC!

        [Required]
        [StringLength(64)]
        public string MasterKey { get; set; }

        [Required]
        [StringLength(64)]
        public string PreviousHash { get; set; }

        public string CalculateHashableContent()
        {
            return $"{StampTime:yyyy-MM-ddTHH:mm:ss.fff}|{MasterKey}|{PreviousHash}";
        }
    }
}
