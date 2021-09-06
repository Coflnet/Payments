namespace Coflnet.Payments.Models
{
    public class TokenSettings
    {
        /// <summary>
        /// The secret token this object represents
        /// </summary>
        public string Token;
        /// <summary>
        /// The name/context who this token coresponds to
        /// </summary>
        public string Name;
        /// <summary>
        /// Is this token scoped to be readonly
        /// </summary>
        public bool Readonly;
        /// <summary>
        /// The id of this token (used in db)
        /// </summary>
        public ushort Id;
    }
}