using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Scenarios;

public sealed class ResolvedAtcPosition
{
    public required ScenarioAtc Source { get; init; }
    public required TrackOwner Owner { get; init; }
    public Tcp? Tcp { get; init; }

    public AtcPositionDto ToSnapshot() =>
        new()
        {
            Id = Source.Id,
            ArtccId = Source.ArtccId,
            FacilityId = Source.FacilityId,
            PositionId = Source.PositionId,
            AutoConnect = Source.AutoConnect,
            AutoTrackAirportIds = [.. Source.AutoTrackAirportIds],
            Owner = Owner.ToSnapshot(),
            Tcp = Tcp?.ToSnapshot(),
        };

    /// <summary>
    /// Rebuilds the entry with a <b>fresh</b> <see cref="ScenarioAtc"/> and a fresh airport list every call: a client
    /// scrub replays one <c>SessionRecording</c> many times and a rewind restores one snapshot repeatedly, so a shared
    /// list would let a later <c>.AUTOTRACK</c> rewrite the source everything else restores from.
    /// </summary>
    public static ResolvedAtcPosition FromSnapshot(AtcPositionDto dto) =>
        new()
        {
            Source = new ScenarioAtc
            {
                Id = dto.Id,
                ArtccId = dto.ArtccId,
                FacilityId = dto.FacilityId,
                PositionId = dto.PositionId,
                AutoConnect = dto.AutoConnect,
                AutoTrackAirportIds = [.. dto.AutoTrackAirportIds],
            },
            Owner = TrackOwner.FromSnapshot(dto.Owner),
            Tcp = dto.Tcp is not null ? Sim.Tcp.FromSnapshot(dto.Tcp) : null,
        };
}
