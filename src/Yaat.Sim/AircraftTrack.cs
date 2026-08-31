using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Track-ownership and handoff state. Owner is the controlling sector
/// (null = no one tracking); HandoffPeer is the receiving side of an in-flight
/// handoff; the H-state booleans mirror CRC's display semantics.
/// </summary>
public class AircraftTrack
{
    private TrackOwner? _owner;

    /// <summary>
    /// Any ordinary write is a command write and clears <see cref="OwnerFromLiveFeed"/> — the feed yields
    /// silently to a controller's TRACK and only re-applies after a DROP (owner back to null). The feed
    /// itself writes through <see cref="SetOwnerFromLiveFeed"/>.
    /// </summary>
    public TrackOwner? Owner
    {
        get => _owner;
        set
        {
            _owner = value;
            OwnerFromLiveFeed = false;
        }
    }

    /// <summary>The current <see cref="Owner"/> was written from the live-traffic feed, not by a controller.</summary>
    public bool OwnerFromLiveFeed { get; private set; }

    /// <summary>Feed-side owner write: marks the ownership as the real world's so a later sample may move it.</summary>
    public void SetOwnerFromLiveFeed(TrackOwner? owner)
    {
        _owner = owner;
        OwnerFromLiveFeed = owner is not null;
    }

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
            OwnerFromLiveFeed = OwnerFromLiveFeed,
            HandoffPeer = HandoffPeer?.ToSnapshot(),
            HandoffRedirectedBy = HandoffRedirectedBy?.ToSnapshot(),
            OnHandoff = OnHandoff,
            HandoffAccepted = HandoffAccepted,
            HandoffInitiatedAt = HandoffInitiatedAt,
            Pointout = Pointout?.ToSnapshot(),
        };

    public static AircraftTrack FromSnapshot(AircraftTrackDto dto)
    {
        var track = new AircraftTrack
        {
            Owner = dto.Owner is not null ? TrackOwner.FromSnapshot(dto.Owner) : null,
            HandoffPeer = dto.HandoffPeer is not null ? TrackOwner.FromSnapshot(dto.HandoffPeer) : null,
            HandoffRedirectedBy = dto.HandoffRedirectedBy is not null ? TrackOwner.FromSnapshot(dto.HandoffRedirectedBy) : null,
            OnHandoff = dto.OnHandoff,
            HandoffAccepted = dto.HandoffAccepted,
            HandoffInitiatedAt = dto.HandoffInitiatedAt,
            Pointout = dto.Pointout is not null ? StarsPointout.FromSnapshot(dto.Pointout) : null,
        };
        if (dto.OwnerFromLiveFeed)
        {
            track.SetOwnerFromLiveFeed(track.Owner);
        }

        return track;
    }
}
