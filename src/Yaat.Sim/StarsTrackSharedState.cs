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
}
