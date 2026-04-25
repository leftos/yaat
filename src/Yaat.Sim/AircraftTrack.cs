using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Track-ownership and handoff state. Owner is the controlling sector
/// (null = no one tracking); HandoffPeer is the receiving side of an in-flight
/// handoff; the H-state booleans mirror CRC's display semantics.
/// </summary>
public class AircraftTrack
{
    public TrackOwner? Owner { get; set; }
    public TrackOwner? HandoffPeer { get; set; }
    public TrackOwner? HandoffRedirectedBy { get; set; }
    public bool OnHandoff { get; set; }
    public bool HandoffAccepted { get; set; }
    public double? HandoffInitiatedAt { get; set; }
    public StarsPointout? Pointout { get; set; }

    public AircraftTrackDto ToSnapshot() =>
        new()
        {
            Owner = Owner?.ToSnapshot(),
            HandoffPeer = HandoffPeer?.ToSnapshot(),
            HandoffRedirectedBy = HandoffRedirectedBy?.ToSnapshot(),
            OnHandoff = OnHandoff,
            HandoffAccepted = HandoffAccepted,
            HandoffInitiatedAt = HandoffInitiatedAt,
            Pointout = Pointout?.ToSnapshot(),
        };

    public static AircraftTrack FromSnapshot(AircraftTrackDto dto) =>
        new()
        {
            Owner = dto.Owner is not null ? TrackOwner.FromSnapshot(dto.Owner) : null,
            HandoffPeer = dto.HandoffPeer is not null ? TrackOwner.FromSnapshot(dto.HandoffPeer) : null,
            HandoffRedirectedBy = dto.HandoffRedirectedBy is not null ? TrackOwner.FromSnapshot(dto.HandoffRedirectedBy) : null,
            OnHandoff = dto.OnHandoff,
            HandoffAccepted = dto.HandoffAccepted,
            HandoffInitiatedAt = dto.HandoffInitiatedAt,
            Pointout = dto.Pointout is not null ? StarsPointout.FromSnapshot(dto.Pointout) : null,
        };
}
