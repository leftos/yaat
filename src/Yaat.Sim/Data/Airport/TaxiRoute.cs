namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A resolved taxi route: an ordered sequence of segments with hold-short points.
/// </summary>
public sealed class TaxiRoute
{
    public required List<TaxiRouteSegment> Segments { get; init; }
    public required List<HoldShortPoint> HoldShortPoints { get; init; }

    /// <summary>Current segment index being traversed.</summary>
    public int CurrentSegmentIndex { get; set; }

    public TaxiRouteSegment? CurrentSegment =>
        CurrentSegmentIndex >= 0 && CurrentSegmentIndex < Segments.Count
            ? Segments[CurrentSegmentIndex]
            : null;

    public bool IsComplete => CurrentSegmentIndex >= Segments.Count;

    /// <summary>
    /// Check if the given node is a hold-short point in this route.
    /// </summary>
    public HoldShortPoint? GetHoldShortAt(int nodeId)
    {
        foreach (var hs in HoldShortPoints)
        {
            if (hs.NodeId == nodeId)
            {
                return hs;
            }
        }

        return null;
    }

    /// <summary>
    /// Build a human-readable taxi route summary (e.g., "S T U W W1 HS 28L").
    /// </summary>
    public string ToSummary()
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seg in Segments)
        {
            if (seen.Add(seg.TaxiwayName))
            {
                parts.Add(seg.TaxiwayName);
            }
        }

        foreach (var hs in HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.ExplicitHoldShort && hs.RunwayId is not null)
            {
                parts.Add("HS");
                parts.Add(hs.RunwayId);
            }
        }

        return string.Join(" ", parts);
    }
}

public sealed class TaxiRouteSegment
{
    public required int FromNodeId { get; init; }
    public required int ToNodeId { get; init; }
    public required string TaxiwayName { get; init; }
    public required GroundEdge Edge { get; init; }
}

public enum HoldShortReason
{
    RunwayCrossing,
    ExplicitHoldShort,
    DestinationRunway,
}

public sealed class HoldShortPoint
{
    public required int NodeId { get; init; }
    public required HoldShortReason Reason { get; init; }
    public string? RunwayId { get; init; }

    /// <summary>Whether this hold-short has been cleared (e.g., CROSS command issued).</summary>
    public bool IsCleared { get; set; }
}
