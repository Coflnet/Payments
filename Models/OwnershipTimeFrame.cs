namespace Coflnet.Payments.Models;

public class OwnershipTimeFrame
{
    public string UserId { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    public OwnershipTimeFrame(string userId, DateTime start, DateTime end)
    {
        UserId = userId;
        Start = start;
        End = end;
    }
}