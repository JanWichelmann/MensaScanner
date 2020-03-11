using System;
using System.Collections.Generic;
using System.Text;

namespace MensaScanner
{
    /// <summary>
    /// Represents one menu entry.
    /// </summary>
    class MenuEntry
    {
        /// <summary>
        /// The entry name / description.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Price information.
        /// </summary>
        public string Price { get; set; }
        
        /// <summary>
        /// Additional information about the food.
        /// </summary>
        public string Properties { get; set; }
    }
}
