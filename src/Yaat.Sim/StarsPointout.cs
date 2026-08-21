using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

public class StarsPointout(Tcp recipient, Tcp sender)
{
    public Tcp Recipient { get; set; } = recipient;
    public Tcp Sender { get; set; } = sender;
    public StarsPointoutStatus Status { get; set; } = StarsPointoutStatus.Pending;

    /// <summary>Sim-elapsed seconds when the pointout was created — drives the auto-acknowledge timer.</summary>
    public double? InitiatedAt { get; set; }

    public bool IsPending => Status == StarsPointoutStatus.Pending;
    public bool IsAccepted => Status == StarsPointoutStatus.Accepted;
    public bool IsRejected => Status == StarsPointoutStatus.Rejected;

    public PointoutDto ToSnapshot() =>
        new()
        {
            Recipient = Recipient.ToSnapshot(),
            Sender = Sender.ToSnapshot(),
            Status = (int)Status,
            InitiatedAt = InitiatedAt,
        };

    public static StarsPointout FromSnapshot(PointoutDto dto) =>
        new(Tcp.FromSnapshot(dto.Recipient), Tcp.FromSnapshot(dto.Sender))
        {
            Status = (StarsPointoutStatus)dto.Status,
            InitiatedAt = dto.InitiatedAt,
        };
}
