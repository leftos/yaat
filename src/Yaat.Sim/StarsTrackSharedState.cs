using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

public sealed class StarsTrackSharedState
{
    public bool ForceFdb { get; set; }
    public bool IsHighlighted { get; set; }
    public int LeaderDirection { get; set; } = 5; // LeaderDirection enum, 5=Default
    public DateTime? IsQueriedUntil { get; set; }
    public bool WasPreviouslyOwned { get; set; }
    public int TpaType { get; set; } // StarsTpaType enum, 0=None
    public double TpaSize { get; set; }

    public SharedStateDto ToSnapshot() =>
        new()
        {
            ForceFdb = ForceFdb,
            IsHighlighted = IsHighlighted,
            LeaderDirection = LeaderDirection,
            IsQueriedUntil = IsQueriedUntil,
            WasPreviouslyOwned = WasPreviouslyOwned,
            TpaType = TpaType,
            TpaSize = TpaSize,
        };

    public static StarsTrackSharedState FromSnapshot(SharedStateDto dto) =>
        new()
        {
            ForceFdb = dto.ForceFdb,
            IsHighlighted = dto.IsHighlighted,
            LeaderDirection = dto.LeaderDirection,
            IsQueriedUntil = dto.IsQueriedUntil,
            WasPreviouslyOwned = dto.WasPreviouslyOwned,
            TpaType = dto.TpaType,
            TpaSize = dto.TpaSize,
        };
}
