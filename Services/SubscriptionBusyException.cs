namespace Coflnet.Payments.Services;

/// <summary>
/// Another request or replica holds the subscription's change lease (<see cref="SubscriptionService"/>); retry shortly
/// </summary>
public class SubscriptionBusyException : ApiException
{
    public SubscriptionBusyException() : base("A subscription change is already being processed. Please retry shortly.")
    {
    }
}
