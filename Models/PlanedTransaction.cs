namespace Coflnet.Payments.Models
{
    /// <summary>
    /// A empheral transaction that can still be changed. 
    /// Intended for invoices that may still be canceled or bidding 
    /// </summary>
    public class PlanedTransaction : Transaction
    {

    }

    /// <summary>
    /// Finite transaction can't be changed and usually coresponds to some <see cref="OwnerShip"/>
    /// </summary>
    public class FiniteTransaction : Transaction
    {
        [System.ComponentModel.DataAnnotations.MaxLength(64)]
        public string SubscriptionId { get; set; }

    }
}