namespace Yaat.Sim;

public record CoordinationReceiver(Tcp Tcp, bool AutoAcknowledge)
{
    public bool AutoAcknowledge { get; set; } = AutoAcknowledge;
}

public sealed class CoordinationChannel
{
    public required string Id { get; init; }
    public required string ListId { get; init; }
    public required string Title { get; init; }
    public List<Tcp> SendingTcps { get; init; } = [];
    public List<CoordinationReceiver> Receivers { get; init; } = [];
    public List<CoordinationItem> Items { get; } = [];
    public int NextSequence { get; set; } = 1;
}
