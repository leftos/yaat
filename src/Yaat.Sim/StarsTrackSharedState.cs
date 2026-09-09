using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

public sealed class StarsTrackSharedState
{
    public bool ForceFdb { get; set; }
    public bool IsHighlighted { get; set; }
    public int LeaderDirection { get; set; } = 5; // LeaderDirection enum, 5=Default

    /// <summary>
    /// Session-elapsed second the STARS query flash stops: CRC computes the expiry on its own clock and sends
    /// the absolute instant, which is normalised to session time at receipt and back to an absolute instant at
    /// each broadcast — so a replay or a reconstruction flashes for what is left of it rather than re-sending a
    /// long-past instant. Null when the track is not flashing.
    /// </summary>
    public double? QueriedUntilElapsedSeconds { get; set; }
    public bool WasPreviouslyOwned { get; set; }
    public int TpaType { get; set; } // StarsTpaType enum, 0=None
    public double TpaSize { get; set; }
    public bool IsRecentlyAcceptedIncomingPointout { get; set; }

    public SharedStateDto ToSnapshot() =>
        new()
        {
            ForceFdb = ForceFdb,
            IsHighlighted = IsHighlighted,
            LeaderDirection = LeaderDirection,
            QueriedUntilElapsedSeconds = QueriedUntilElapsedSeconds,
            WasPreviouslyOwned = WasPreviouslyOwned,
            TpaType = TpaType,
            TpaSize = TpaSize,
            IsRecentlyAcceptedIncomingPointout = IsRecentlyAcceptedIncomingPointout,
        };

    public static StarsTrackSharedState FromSnapshot(SharedStateDto dto) =>
        new()
        {
            ForceFdb = dto.ForceFdb,
            IsHighlighted = dto.IsHighlighted,
            LeaderDirection = dto.LeaderDirection,
            QueriedUntilElapsedSeconds = dto.QueriedUntilElapsedSeconds,
            WasPreviouslyOwned = dto.WasPreviouslyOwned,
            TpaType = dto.TpaType,
            TpaSize = dto.TpaSize,
            IsRecentlyAcceptedIncomingPointout = dto.IsRecentlyAcceptedIncomingPointout,
        };
}
