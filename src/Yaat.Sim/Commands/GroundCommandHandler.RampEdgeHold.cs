using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Commands;

/// <summary>
/// A taxi to a parking area whose stands that fit the type are all taken (<see cref="TaxiCommand.HoldAtAreaRampEdge"/>): the
/// route toward the routing stand is cut where it leaves the movement area for the area's ramp, then carried along the ramp lane
/// only until the aircraft's tail is clear of every movement-area centreline, and never deeper into the ramp than that. The route
/// holds no stand and keeps the area pending (<see cref="TaxiRoute.DestinationParkingArea"/>).
/// </summary>
public static partial class GroundCommandHandler
{
    /// <summary>The most ramp-lane nodes the cut walks past the ramp edge looking for one where the tail is clear.</summary>
    private const int MaxRampLaneWalkNodes = 64;

    /// <summary>
    /// What the tail-clear test at a ramp-lane node reads: the layout and its movement-area verdicts, how far back from the node
    /// the tail is (half the aircraft's length), and the clearance from a movement-area centreline the tail must have.
    /// </summary>
    private sealed record RampEdgeClearance(AirportGroundLayout Layout, MovementAreaClassification Classification, double TailBackFt, double FloorFt);

    /// <summary>
    /// <paramref name="route"/> cut at the parking area's ramp edge when <paramref name="asCleared"/> carries
    /// <see cref="TaxiCommand.HoldAtAreaRampEdge"/> and the route reaches the routing stand; the route unchanged otherwise. The
    /// edge is the end of the last segment on a movement-area taxiway. From there the route follows the ramp taxilane it leads
    /// onto (the route's own segments while they stay on it, then the lane's edges), or the route's own segments short of the
    /// stand when it leads onto no single lane, to the first node where the tail clears the movement area by the wingtip floor.
    /// A route that never leaves the ramp, or has no segment, holds where the aircraft is: never deeper into the ramp.
    /// </summary>
    /// <param name="route">The resolved route toward the routing stand.</param>
    /// <param name="layout">The layout the route is on.</param>
    /// <param name="asCleared">The command as cleared, which says whether to cut and names the area.</param>
    /// <param name="aircraftLengthFt">The aircraft's length, half of which trails the hold node.</param>
    private static TaxiRoute CutAtAreaRampEdge(TaxiRoute route, AirportGroundLayout layout, TaxiCommand asCleared, double aircraftLengthFt)
    {
        if (
            !asCleared.HoldAtAreaRampEdge
            || !string.Equals(route.DestinationParking, asCleared.DestinationParking, StringComparison.OrdinalIgnoreCase)
        )
        {
            return route;
        }

        RampEdgeClearance clearance = RampEdgeClearanceFor(layout, aircraftLengthFt);
        int edgeIndex = LastMovementAreaSegment(route, clearance.Classification);
        if (edgeIndex < 0)
        {
            Log.LogDebug("[TryTaxi] ramp-edge cut: the route ({Count} segment(s)) never leaves the ramp; holding where it is", route.Segments.Count);
            return HeldAtRampEdge(route, [], asCleared.DestinationArea);
        }

        List<TaxiRouteSegment> kept = [.. route.Segments.Take(edgeIndex + 1)];
        if (RampLaneAfter(route, edgeIndex, clearance.Classification) is { } lane)
        {
            ExtendAlongRampLane(kept, route, edgeIndex, lane, clearance);
        }
        else
        {
            ExtendAlongRouteShortOfStand(kept, route, edgeIndex, clearance);
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[TryTaxi] ramp-edge cut: edge node {Edge}, hold node {Hold}, tail {TailFt:F0} ft from the movement area (floor {FloorFt:F0} ft)",
                route.Segments[edgeIndex].ToNodeId,
                kept[^1].ToNodeId,
                TailToMovementAreaFt(kept, clearance),
                clearance.FloorFt
            );
        }

        return HeldAtRampEdge(route, kept, asCleared.DestinationArea);
    }

    /// <summary>The tail-clear inputs for <paramref name="layout"/>: the wingtip floor of its widest runway (0 with none).</summary>
    private static RampEdgeClearance RampEdgeClearanceFor(AirportGroundLayout layout, double aircraftLengthFt)
    {
        double floorFt = (layout.Runways.Count > 0) ? HoldShortAnnotator.WingtipClearanceFloorFt(layout.Runways.Max(r => r.WidthFt)) : 0.0;
        return new RampEdgeClearance(layout, MovementAreaClassification.For(layout), aircraftLengthFt / 2.0, floorFt);
    }

    /// <summary>The index of the route's last segment whose edge carries a movement-area taxiway name, or -1 when none does.</summary>
    private static int LastMovementAreaSegment(TaxiRoute route, MovementAreaClassification classification)
    {
        var nothingCleared = new HashSet<string>();
        for (int i = route.Segments.Count - 1; i >= 0; i--)
        {
            if (
                SegmentExpander
                    .EdgeNames(route.Segments[i].Edge.Edge)
                    .Any(n => SegmentExpander.IsUnclearedMovementAreaName(n, nothingCleared, classification))
            )
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The ramp taxilane the segment after the ramp edge (segment <paramref name="edgeIndex"/>) drives, when it carries that one
    /// name and is no runway; null when there is no such segment, or it is apron, a junction arc or anything but a ramp taxilane.
    /// </summary>
    private static string? RampLaneAfter(TaxiRoute route, int edgeIndex, MovementAreaClassification classification)
    {
        if (edgeIndex + 1 >= route.Segments.Count)
        {
            return null;
        }

        IGroundEdge edge = route.Segments[edgeIndex + 1].Edge.Edge;
        return (!edge.IsRunwayCenterline && (SingleName(edge) is { } name) && classification.IsRampTaxilane(name)) ? name : null;
    }

    /// <summary>
    /// Extends <paramref name="kept"/>, which ends at the ramp edge (segment <paramref name="edgeIndex"/> of
    /// <paramref name="route"/>), along <paramref name="lane"/> until the tail clears: the route's own segments while they stay on
    /// the lane, then the lane's edges, never a node twice. Stops at the last lane node reached when the lane runs out.
    /// </summary>
    private static void ExtendAlongRampLane(List<TaxiRouteSegment> kept, TaxiRoute route, int edgeIndex, string lane, RampEdgeClearance clearance)
    {
        HashSet<int> visited = [kept[0].FromNodeId, .. kept.Select(s => s.ToNodeId)];
        int next = edgeIndex + 1;
        for (int step = 0; step < MaxRampLaneWalkNodes; step++)
        {
            if (NextRampLaneSegment(route, ref next, kept[^1].Edge, lane, visited) is not { } segment)
            {
                Log.LogDebug("[TryTaxi] ramp-edge cut: lane {Lane} ran out at node {Node} before the tail cleared", lane, kept[^1].ToNodeId);
                return;
            }

            kept.Add(segment);
            visited.Add(segment.ToNodeId);
            if (TailToMovementAreaFt(kept, clearance) >= clearance.FloorFt)
            {
                return;
            }
        }

        Log.LogDebug(
            "[TryTaxi] ramp-edge cut: lane {Lane} walk hit its {Cap}-node cap at node {Node} before the tail cleared",
            lane,
            MaxRampLaneWalkNodes,
            kept[^1].ToNodeId
        );
    }

    /// <summary>
    /// Extends <paramref name="kept"/>, which ends at the ramp edge (segment <paramref name="edgeIndex"/> of
    /// <paramref name="route"/>), along the route's own following segments until the tail clears, never onto the final segment
    /// into the stand: the ramp edge leads onto apron or a junction arc, not a single ramp taxilane. When they run out first the
    /// aircraft holds at the node before the stand.
    /// </summary>
    private static void ExtendAlongRouteShortOfStand(List<TaxiRouteSegment> kept, TaxiRoute route, int edgeIndex, RampEdgeClearance clearance)
    {
        Log.LogDebug("[TryTaxi] ramp-edge cut: no single ramp taxilane after node {Edge}; following the route short of the stand", kept[^1].ToNodeId);
        for (int i = edgeIndex + 1; i < route.Segments.Count - 1; i++)
        {
            kept.Add(route.Segments[i]);
            if (TailToMovementAreaFt(kept, clearance) >= clearance.FloorFt)
            {
                return;
            }
        }

        Log.LogDebug("[TryTaxi] ramp-edge cut: reached node {Node}, short of the stand, before the tail cleared", kept[^1].ToNodeId);
    }

    /// <summary>
    /// The next segment along <paramref name="lane"/>: the route's own segment <paramref name="next"/> while it stays on the lane,
    /// else the lane's edge on from the end of <paramref name="arrived"/>; once off the route's segments the walk never rejoins
    /// them. Null when the lane runs out.
    /// </summary>
    private static TaxiRouteSegment? NextRampLaneSegment(TaxiRoute route, ref int next, DirectionalEdge arrived, string lane, HashSet<int> visited)
    {
        if ((next < route.Segments.Count) && IsRampLaneStep(route.Segments[next].Edge.Edge, route.Segments[next].Edge.ToNode, lane, visited))
        {
            return route.Segments[next++];
        }

        next = int.MaxValue;
        return NextLaneSegment(arrived, lane, visited);
    }

    /// <summary>
    /// The lane's edge on from the end of <paramref name="arrived"/> to a node not yet <paramref name="visited"/>, the one turning
    /// least from the arrival direction at a fork; null when there is none.
    /// </summary>
    private static TaxiRouteSegment? NextLaneSegment(DirectionalEdge arrived, string lane, HashSet<int> visited)
    {
        GroundNode node = arrived.ToNode;
        var arrival = new TrueHeading(GeoMath.BearingTo(arrived.FromNode.Position, node.Position));
        TaxiRouteSegment? best = null;
        double bestTurnDeg = double.PositiveInfinity;
        foreach (IGroundEdge edge in node.Edges)
        {
            GroundNode other = edge.OtherNode(node);
            double turnDeg = arrival.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(node.Position, other.Position)));
            if (IsRampLaneStep(edge, other, lane, visited) && (turnDeg < bestTurnDeg))
            {
                best = new TaxiRouteSegment { TaxiwayName = lane, Edge = edge.Directed(node, other) };
                bestTurnDeg = turnDeg;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether the walk may drive <paramref name="edge"/> onto <paramref name="to"/>: an edge of <paramref name="lane"/> alone, not
    /// a runway centreline, onto a node not yet <paramref name="visited"/> that is neither a runway hold-short nor on a runway.
    /// </summary>
    private static bool IsRampLaneStep(IGroundEdge edge, GroundNode to, string lane, HashSet<int> visited) =>
        IsLaneOnlyEdge(edge, lane)
        && !visited.Contains(to.Id)
        && (to.Type != GroundNodeType.RunwayHoldShort)
        && !to.Edges.Any(e => e.IsRunwayCenterline);

    /// <summary>
    /// Whether <paramref name="edge"/> carries <paramref name="lane"/> and no other name, and is no runway centreline: never RAMP,
    /// never an arc onto another name.
    /// </summary>
    private static bool IsLaneOnlyEdge(IGroundEdge edge, string lane) =>
        !edge.IsRunwayCenterline && (SingleName(edge) is { } name) && string.Equals(name, lane, StringComparison.OrdinalIgnoreCase);

    /// <summary>The edge's one name when it carries exactly one and it is not RAMP; null otherwise.</summary>
    private static string? SingleName(IGroundEdge edge) =>
        ((SegmentExpander.EdgeNames(edge) is [var name]) && !name.Equals("RAMP", StringComparison.OrdinalIgnoreCase)) ? name : null;

    /// <summary>
    /// The distance from the tail (<see cref="RampEdgeClearance.TailBackFt"/> back along <paramref name="kept"/> from its end) to the
    /// nearest straight movement-area centreline, in feet; infinity when the layout has none.
    /// </summary>
    private static double TailToMovementAreaFt(List<TaxiRouteSegment> kept, RampEdgeClearance clearance) =>
        NearestMovementAreaCentrelineFt(clearance.Layout, TailPoint(kept, clearance.TailBackFt), clearance.Classification);

    /// <summary>The point <paramref name="backFt"/> back from the end of <paramref name="kept"/>, along its node-to-node chords.</summary>
    private static LatLon TailPoint(List<TaxiRouteSegment> kept, double backFt)
    {
        double remainingFt = backFt;
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            LatLon to = kept[i].Edge.ToNode.Position;
            LatLon from = kept[i].Edge.FromNode.Position;
            double lengthFt = GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
            if (remainingFt <= lengthFt)
            {
                return GeoMath.ProjectPoint(to, new TrueHeading(GeoMath.BearingTo(to, from)), remainingFt / GeoMath.FeetPerNm);
            }

            remainingFt -= lengthFt;
        }

        return kept[0].Edge.FromNode.Position;
    }

    /// <summary>
    /// The distance from <paramref name="position"/> to the nearest straight centreline of a movement-area taxiway, in feet; ramp
    /// taxilanes and apron (RAMP) edges never count. Infinity when there is none. The movement-area sibling of
    /// <see cref="RampLaneReposition.NearestOtherCentrelineFt"/>.
    /// </summary>
    private static double NearestMovementAreaCentrelineFt(AirportGroundLayout layout, LatLon position, MovementAreaClassification classification)
    {
        var nothingCleared = new HashSet<string>();
        double nearestFt = double.PositiveInfinity;
        foreach (GroundEdge edge in layout.Edges)
        {
            if (SegmentExpander.EdgeNames(edge).Any(n => SegmentExpander.IsUnclearedMovementAreaName(n, nothingCleared, classification)))
            {
                nearestFt = Math.Min(nearestFt, GeoMath.DistanceToSegmentFt(position, edge.Nodes[0].Position, edge.Nodes[1].Position));
            }
        }

        return nearestFt;
    }

    /// <summary>
    /// <paramref name="source"/> re-cut to <paramref name="kept"/>, its bars at the kept start node and at every kept node and its
    /// clearance facts carried, holding no stand or spot and keeping <paramref name="area"/> pending. An empty
    /// <paramref name="kept"/> holds the aircraft where it is.
    /// </summary>
    private static TaxiRoute HeldAtRampEdge(TaxiRoute source, List<TaxiRouteSegment> kept, string? area)
    {
        HashSet<int> keptNodeIds = (kept.Count == 0) ? [] : [kept[0].FromNodeId, .. kept.Select(s => s.ToNodeId)];
        return new TaxiRoute
        {
            Segments = kept,
            HoldShortPoints = [.. source.HoldShortPoints.Where(h => keptNodeIds.Contains(h.NodeId))],
            Warnings = source.Warnings,
            ImpliedLanes = source.ImpliedLanes,
            ClearanceTaxiways = source.ClearanceTaxiways,
            MandatoryConnectorCount = source.MandatoryConnectorCount,
            TurnAboutShape = source.TurnAboutShape,
            TurnAboutTargetNodeId = source.TurnAboutTargetNodeId,
            DestinationParking = null,
            DestinationSpot = null,
            DestinationParkingArea = area,
        };
    }
}
