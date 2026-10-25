using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;

namespace ProcessorApplication.Database.Models
{
    /// <summary>How a stamp's server key is kept.</summary>
    public enum StampKeyMode
    {
        /// <summary>Old rows not yet converted: the key is stored in plaintext. Converted to <see cref="LegacyWrapped"/> at startup.</summary>
        LegacyPlain = 0,

        /// <summary>Old random key, stored encrypted with the node root key.</summary>
        LegacyWrapped = 1,

        /// <summary>Key is not stored: HMAC(node root key, stamp time). Recomputed whenever it's needed.</summary>
        Derived = 2
    }

    /// <summary>
    /// One link of the server key chain. A new stamp (and key) is made every
    /// <c>SecuritySettings.HashStampGenerationPeriod</c> hours.
    /// The plaintext key is never stored for new stamps; see <c>HashStampService</c>.
    /// </summary>
    public class ServerHashStamp
    {
        public int Id { get; set; }

        public DateTime StampTime { get; set; } //datetime in !UTC!

        /// <summary>
        /// Column "MasterKey": plaintext key for <see cref="StampKeyMode.LegacyPlain"/> rows only; empty otherwise.
        /// </summary>
        [Required]
        [Column("MasterKey")]
        public string StoredKey { get; set; } = string.Empty;

        [Required]
        [StringLength(64)]
        public string PreviousHash { get; set; }

        public StampKeyMode KeyMode { get; set; } = StampKeyMode.LegacyPlain;

        /// <summary>Legacy key encrypted with the node root key (<see cref="StampKeyMode.LegacyWrapped"/>).</summary>
        public string? WrappedKey { get; set; }

        /// <summary>SHA-256 of the key: lets backups and verification check a recovered key without storing it.</summary>
        [StringLength(64)]
        public string? KeyCheck { get; set; }

        /// <summary>
        /// The plaintext 64-hex server key. Filled by <c>HashStampService</c> when a stamp is read; never persisted.
        /// </summary>
        [NotMapped]
        public string MasterKey { get; set; } = string.Empty;

        /// <summary>Original content format (legacy chain links). Needs <see cref="MasterKey"/>.</summary>
        public string CalculateHashableContent()
        {
            return $"{StampTime:yyyy-MM-ddTHH:mm:ss.fff}|{MasterKey}|{PreviousHash}";
        }

        /// <summary>Exact, culture-independent text of the stamp time; also the input of key derivation.</summary>
        public static string FormatStampTime(DateTime time) =>
            DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        /// <summary>Content hashed into the next link (current format). Uses only non-secret fields.</summary>
        public string CalculateBlockContent()
        {
            return $"{FormatStampTime(StampTime)}|{(int)KeyMode}|{KeyCheck}|{PreviousHash}";
        }
    }
}
