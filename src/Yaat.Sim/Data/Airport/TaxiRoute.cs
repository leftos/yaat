using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A resolved taxi route: an ordered sequence of segments with hold-short points.
/// </summary>
public sealed class TaxiRoute
{
    public required List<TaxiRouteSegment> Segments { get; init; }
    public required List<HoldShortPoint> HoldShortPoints { get; init; }
    public List<string> Warnings { get; init; } = [];

    /// <summary>Parking destination name (@ prefix), if any.</summary>
    public string? DestinationParking { get; init; }

    /// <summary>Spot destination name ($ prefix), if any.</summary>
    public string? DestinationSpot { get; init; }

    public double TotalDistanceNm => Segments.Sum(s => s.Edge.DistanceNm);

    /// <summary>
    /// Returns a shallow copy of this route truncated to end at the segment whose
    /// ToNodeId matches <paramref name="nodeId"/>. If the node is not found, returns this route.
    /// </summary>
    public TaxiRoute TruncateAt(int nodeId)
    {
        for (int i = 0; i < Segments.Count; i++)
        {
            if (Segments[i].ToNodeId == nodeId)
            {
                return new TaxiRoute
                {
                    Segments = Segments.Take(i + 1).ToList(),
                    HoldShortPoints = HoldShortPoints.Where(hs => Segments.Take(i + 1).Any(s => s.ToNodeId == hs.NodeId)).ToList(),
                    Warnings = Warnings,
                };
            }
        }

        return this;
    }

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
        int? previousNodeId = null;
        for (int segIdx = CurrentSegmentIndex; segIdx < Segments.Count; segIdx++)
        {
            var seg = Segments[segIdx];
            int nodeId = seg.ToNodeId;

            if (GetHoldShortAt(nodeId) is not null)
            {
                previousNodeId = nodeId;
                continue;
            }

            if (!layout.Nodes.TryGetValue(nodeId, out var node))
            {
                previousNodeId = nodeId;
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

            // Check if any adjacent edge has a matching taxiway name.
            // Use the previous node so the aircraft stops before the intersection.
            foreach (var edge in node.Edges)
            {
                if (string.Equals(edge.TaxiwayName, target, StringComparison.OrdinalIgnoreCase))
                {
                    int holdNodeId = previousNodeId ?? nodeId;
                    if (GetHoldShortAt(holdNodeId) is null)
                    {
                        HoldShortPoints.Add(
                            new HoldShortPoint
                            {
                                NodeId = holdNodeId,
                                Reason = HoldShortReason.ExplicitHoldShort,
                                TargetName = target.ToUpperInvariant(),
                            }
                        );
                    }
                    return true;
                }
            }
            previousNodeId = nodeId;
        }

        return false;
    }

    /// <summary>
    /// Build a human-readable taxi route summary (e.g., "S T U W W1 HS 28L, RWY 30").
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

        // Append destination runway assignment
        foreach (var hs in HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.DestinationRunway && hs.TargetName is not null)
            {
                parts.Add("RWY");
                parts.Add(hs.TargetName);
                break;
            }
        }

        // Append parking or spot destination
        if (DestinationParking is not null)
        {
            parts.Add($"@{DestinationParking}");
        }
        else if (DestinationSpot is not null)
        {
            parts.Add($"${DestinationSpot}");
        }

        return string.Join(" ", parts);
    }

    public TaxiRouteDto ToSnapshot() =>
        new()
        {
            Segments = Segments
                .Select(s => new TaxiSegmentDto
                {
                    FromNodeId = s.FromNodeId,
                    ToNodeId = s.ToNodeId,
                    TaxiwayName = s.TaxiwayName,
                })
                .ToList(),
            CurrentSegmentIndex = CurrentSegmentIndex,
            HoldShortPoints = HoldShortPoints
                .Select(hs => new HoldShortPointDto
                {
                    NodeId = hs.NodeId,
                    RunwayId = hs.TargetName ?? "",
                    IsSatisfied = hs.IsCleared,
                })
                .ToList(),
            Description = ToSummary(),
        };

    public static TaxiRoute? FromSnapshot(TaxiRouteDto dto, AirportGroundLayout? layout)
    {
        if (layout is null)
        {
            return null;
        }

        var segments = new List<TaxiRouteSegment>();
        foreach (var seg in dto.Segments)
        {
            if (!layout.Nodes.TryGetValue(seg.FromNodeId, out var fromNode))
            {
                return null;
            }

            GroundEdge? edge = null;
            foreach (var e in fromNode.Edges)
            {
                if (e.ToNodeId == seg.ToNodeId)
                {
                    edge = e;
                    break;
                }
            }

            if (edge is null)
            {
                return null;
            }

            segments.Add(
                new TaxiRouteSegment
                {
                    FromNodeId = seg.FromNodeId,
                    ToNodeId = seg.ToNodeId,
                    TaxiwayName = seg.TaxiwayName ?? edge.TaxiwayName,
                    Edge = edge,
                }
            );
        }

        var holdShorts = new List<HoldShortPoint>();
        if (dto.HoldShortPoints is not null)
        {
            foreach (var hs in dto.HoldShortPoints)
            {
                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = hs.NodeId,
                        Reason = HoldShortReason.ExplicitHoldShort,
                        TargetName = hs.RunwayId,
                        IsCleared = hs.IsSatisfied,
                    }
                );
            }
        }

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
            CurrentSegmentIndex = dto.CurrentSegmentIndex,
        };
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
