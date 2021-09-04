using System.Collections.Generic;

namespace Coflnet.Payments.Models
{
    public class User
    {
        public int Id { get; set; }
        /// <summary>
        /// The identifier of the account system
        /// </summary>
        /// <value></value>
        public string ExternalId { get; set; }
        /// <summary>
        /// Balance of this user
        /// </summary>
        /// <value></value>
        public decimal Balance { get; set; }
        /// <summary>
        /// Things this user owns
        /// </summary>
        /// <value></value>
        public List<OwnerShip> Owns { get; set; }
    }
}