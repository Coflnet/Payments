using System.Collections.Generic;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// A group is a collection of one or more <see cref="Product"/>
    /// </summary>
    public class Group
    {
        /// <summary>
        /// Primary Key
        /// </summary>
        /// <value></value>
        public int Id { get; set; }
        /// <summary>
        /// Identifier of this group
        /// </summary>
        /// <value></value>
        public string Slug { get; set; }
        /// <summary>
        /// Products in this group
        /// </summary>
        /// <value></value>
        public List<Product> Products { get; set; }
    }
}