using System;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Creator code that can be used to get a discount and attribute purchases to a creator
    /// </summary>
    public class CreatorCode
    {
        /// <summary>
        /// The unique identifier for the creator code
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The actual code that users will enter (e.g., "TECHNO", "DREAM")
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Code { get; set; }

        /// <summary>
        /// The creator's user ID who owns this code
        /// </summary>
        [Required]
        [MaxLength(32)]
        public string CreatorUserId { get; set; }

        /// <summary>
        /// The discount percentage to apply (e.g., 5 for 5%, 10 for 10%)
        /// </summary>
        public decimal DiscountPercent { get; set; }

        /// <summary>
        /// The revenue share percentage for the creator (e.g., 5 for 5%)
        /// </summary>
        public decimal RevenueSharePercent { get; set; }

        /// <summary>
        /// Whether this code is currently active
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// When this creator code was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When this creator code was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Optional: When this code expires
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Optional: Maximum number of times this code can be used
        /// </summary>
        public int? MaxUses { get; set; }

        /// <summary>
        /// Current number of times this code has been used
        /// </summary>
        public int TimesUsed { get; set; }
    }
}
