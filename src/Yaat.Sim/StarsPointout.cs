namespace Yaat.Sim;

public class StarsPointout(Tcp recipient, Tcp sender)
{
    public Tcp Recipient { get; set; } = recipient;
    public Tcp Sender { get; set; } = sender;
    public StarsPointoutStatus Status { get; set; } = StarsPointoutStatus.Pending;

    public bool IsPending => Status == StarsPointoutStatus.Pending;
    public bool IsAccepted => Status == StarsPointoutStatus.Accepted;
    public bool IsRejected => Status == StarsPointoutStatus.Rejected;
}
