using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Database.Models
{
    public class DecipherAccess
    {
        public int Id { get; set; }

        public DateTime When { get; set; }
        public string Area { get; set; }
        public string UserKey { get; set; }
        public string DecipherDataHashKey { get; set; }
    }
}
