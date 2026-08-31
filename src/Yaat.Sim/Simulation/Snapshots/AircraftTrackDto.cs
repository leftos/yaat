namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftTrackDto
{
    public TrackOwnerDto? Owner { get; init; }

    // Optional — snapshots that predate feed ownership restore with the owner treated as controller-written.
    public bool OwnerFromLiveFeed { get; init; }
    public TrackOwnerDto? HandoffPeer { get; init; }
    public TrackOwnerDto? HandoffRedirectedBy { get; init; }
    public required bool OnHandoff { get; init; }
    public required bool HandoffAccepted { get; init; }
    public double? HandoffInitiatedAt { get; init; }
    public PointoutDto? Pointout { get; init; }
}
