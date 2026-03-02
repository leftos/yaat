namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A resolved taxi route: an ordered sequence of segments with hold-short points.
/// </summary>
public sealed class TaxiRoute
{
    public required List<TaxiRouteSegment> Segments { get; init; }
    public required List<HoldShortPoint> HoldShortPoints { get; init; }

    public double TotalDistanceNm => Segments.Sum(s => s.Edge.DistanceNm);

    /// <summary>Current segment index being traversed.</summary>
    public int CurrentSegmentIndex { get; set; }

    public TaxiRouteSegment? CurrentSegment =>
        CurrentSegmentIndex >= 0 && CurrentSegmentIndex < Segments.Count ? Segments[CurrentSegmentIndex] : null;

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
    /// Scans remaining segments for the first intersection with the given target
    /// (taxiway name or runway ID) and inserts a hold-short point there.
    /// Returns true if a hold-short was added.
    /// </summary>
    public bool AddHoldShortAtIntersection(string target, AirportGroundLayout layout)
    {
        for (int segIdx = CurrentSegmentIndex; segIdx < Segments.Count; segIdx++)
        {
            var seg = Segments[segIdx];
            int nodeId = seg.ToNodeId;

            if (GetHoldShortAt(nodeId) is not null)
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            // Check if this is a RunwayHoldShort node matching the target
            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } rwyId && rwyId.Contains(target))
            {
                HoldShortPoints.Add(
                    new HoldShortPoint
                    {
                        NodeId = nodeId,
                        Reason = HoldShortReason.ExplicitHoldShort,
                        TargetName = rwyId.ToString(),
                    }
                );
                return true;
            }

            // Check if any adjacent edge has a matching taxiway name
            foreach (var edge in node.Edges)
            {
                if (string.Equals(edge.TaxiwayName, target, StringComparison.OrdinalIgnoreCase))
                {
                    HoldShortPoints.Add(
                        new HoldShortPoint
                        {
                            NodeId = nodeId,
                            Reason = HoldShortReason.ExplicitHoldShort,
                            TargetName = target.ToUpperInvariant(),
                        }
                    );
                    return true;
                }
            }
        }

        return false;
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
            if (hs.Reason == HoldShortReason.ExplicitHoldShort && hs.TargetName is not null)
            {
                parts.Add("HS");
                parts.Add(hs.TargetName);
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

    /// <summary>Runway ID or taxiway name this hold-short protects.</summary>
    public string? TargetName { get; init; }

    /// <summary>Whether this hold-short has been cleared (e.g., CROSS command issued).</summary>
    public bool IsCleared { get; set; }
}
