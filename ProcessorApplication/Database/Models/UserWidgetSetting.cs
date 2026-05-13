using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ProcessorApplication.Database.Models
{
    public class UserWidgetSetting
    {
        [Key, Column(Order = 0)]
        public string UserId { get; set; }

        [Key, Column(Order = 1)]
        public string WidgetId { get; set; }

        /// <summary>
        /// JSON string for functional settings specific to the widget logic 
        /// Expected keys: isHidden, isCollapsed.
        /// (e.g., refresh rates, filter values, display thresholds).
        /// </summary>
        public string GeneralSettingsJson { get; set; }

        /// <summary>
        /// JSON string for layout on small screens (Mobile/Portrait Tablets).
        /// Expected keys: order, width (in tiles), height (in tiles)
        /// </summary>
        public string SmallScreenSettingsJson { get; set; }

        /// <summary>
        /// JSON string for layout on large screens (Desktop/Large Tablets).
        /// Expected keys: order, width (in tiles), height (in tiles)
        /// </summary>
        public string LargeScreenSettingsJson { get; set; }
    }
}
