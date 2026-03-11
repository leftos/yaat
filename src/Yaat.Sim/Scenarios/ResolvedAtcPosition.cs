namespace Yaat.Sim.Scenarios;

public sealed class ResolvedAtcPosition
{
    public required ScenarioAtc Source { get; init; }
    public required TrackOwner Owner { get; init; }
    public Tcp? Tcp { get; init; }
}
