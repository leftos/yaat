using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// A resolved graph route for lining up onto a runway by following the taxiway
/// centerline to the runway edge and curving onto the runway centerline via the
/// baked junction fillet arc. Produced by <see cref="LineUpGraphRoute.TryPlan"/>,
/// played back by <see cref="LineUpPhase"/>'s graph-taxi state through a
/// <see cref="GroundNavigator"/>.
/// </summary>
public sealed record LineUpArcFollowPlan
{
    /// <summary>Taxi route: virtual [aircraft pose → nearest node] → taxiway edges → junction fillet arc → centerline node.</summary>
    public required TaxiRoute Route { get; init; }

    /// <summary>Runway heading the fillet arc ends tangent to (departure direction).</summary>
    public required double RunwayHeadingDeg { get; init; }

    /// <summary>Forward-speed cap for the graph-taxi playback.</summary>
    public required double MaxSpeedKts { get; init; }

    /// <summary>The runway-centerline node the fillet arc ends on, aligned with the departure heading.</summary>
    public required GroundNode CenterlineNode { get; init; }
}

/// <summary>
/// Builds a ground-graph route that lines an aircraft up onto a runway by
/// following the real taxiway pavement to the runway edge and curving onto the
/// centerline via the junction fillet arc — instead of the graph-blind pivot in
/// <see cref="LineUpGeometry"/> that cut a straight diagonal from a set-back,
/// oblique hold-short across to the centerline (issue #239).
///
/// <para>
/// The route walks taxiway edges from the node nearest the aircraft toward the
/// runway (decreasing cross-track) until a node carrying a runway fillet arc
/// whose exit tangent aligns with the departure heading is reached, then appends
/// that arc. Returns null when no such departure-aligned onto-runway arc route
/// exists (aircraft not at a graph hold-short, no junction arc, wrong-direction
/// prong) — the caller falls back to the synthetic <see cref="LineUpGeometry"/>
/// pivot, preserving its handling of parallel-taxiway / shallow-angle poses.
/// </para>
/// </summary>
public static class LineUpGraphRoute
{
    private static readonly ILogger Log = SimLog.CreateLogger("LineUpGraphRoute");

    /// <summary>Maximum taxiway hops from the nearest node to the junction fillet arc before giving up.</summary>
    private const int MaxHops = 8;

    /// <summary>
    /// Cross-track margin (ft) by which the start node may sit behind the aircraft. An aircraft that
    /// crept a few feet past its hold-short node is closest to a node fractionally farther from the
    /// runway than it is; starting there would send the virtual approach segment backward and spin
    /// the navigator. The start node must be within this margin of, or ahead of, the aircraft.
    /// </summary>
    private const double StartNodeBackMarginFt = 5.0;

    /// <summary>
    /// Maximum deviation (deg) between a fillet arc's exit tangent and the departure
    /// runway heading for the arc to count as "lines up in the departure direction".
    /// Rejects the reciprocal-end prong (an arc curving onto the centerline pointing
    /// the wrong way) so the aircraft never lines up facing the opposite direction.
    /// </summary>
    private const double TangentAlignToleranceDeg = 45.0;

    /// <summary>
    /// Attempt to build a taxiway-following lineup route onto <paramref name="runway"/>
    /// from the aircraft's current pose. Returns null when no departure-aligned
    /// onto-runway fillet arc route can be resolved.
    /// </summary>
    public static LineUpArcFollowPlan? TryPlan(AirportGroundLayout layout, LatLon acPos, RunwayInfo runway, AircraftCategory category)
    {
        double rwyHdgDeg = runway.TrueHeading.Degrees;

        var start = NearestForwardNode(layout, acPos, runway);
        if (start is null)
        {
            return null;
        }

        // Greedy walk toward the runway (decreasing cross-track) recording the node
        // path, until a node carrying a departure-aligned runway fillet arc is found.
        var path = new List<GroundNode> { start };
        var visited = new HashSet<int> { start.Id };
        var cur = start;

        GroundNode? junction = null;
        GroundArc? filletArc = null;
        GroundNode? centerlineNode = null;

        for (int hop = 0; hop <= MaxHops; hop++)
        {
            if (TryFindDepartureAlignedFilletArc(cur, runway, rwyHdgDeg, out filletArc, out centerlineNode))
            {
                junction = cur;
                break;
            }

            var next = NextTowardRunway(cur, runway, visited);
            if (next is null)
            {
                return null;
            }

            visited.Add(next.Id);
            path.Add(next);
            cur = next;
        }

        if (junction is null || filletArc is null || centerlineNode is null)
        {
            return null;
        }

        var route = BuildRoute(acPos, path, filletArc, junction, centerlineNode, runway);
        if (route is null)
        {
            return null;
        }

        double maxSpeed = CategoryPerformance.TaxiCornerSpeed(category);
        Log.LogDebug(
            "[LineUpGraphRoute] onto {Rwy}: start node {Start}, junction {Junction}, centerline {Center}, {Segs} segments",
            runway.Designator,
            start.Id,
            junction.Id,
            centerlineNode.Id,
            route.Segments.Count
        );

        return new LineUpArcFollowPlan
        {
            Route = route,
            RunwayHeadingDeg = rwyHdgDeg,
            MaxSpeedKts = maxSpeed,
            CenterlineNode = centerlineNode,
        };
    }

    /// <summary>
    /// The node nearest the aircraft that is not behind it relative to the runway (its cross-track is
    /// at most <see cref="StartNodeBackMarginFt"/> greater than the aircraft's). Starting the route at a
    /// node farther from the runway than the aircraft would drive the virtual approach segment backward.
    /// </summary>
    private static GroundNode? NearestForwardNode(AirportGroundLayout layout, LatLon pos, RunwayInfo runway)
    {
        double acCross = CrossFt(pos, runway);
        GroundNode? best = null;
        double bestDist = double.MaxValue;
        foreach (var n in layout.Nodes.Values)
        {
            if (CrossFt(n.Position, runway) > acCross + StartNodeBackMarginFt)
            {
                continue;
            }

            double d = GeoMath.DistanceNm(pos, n.Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = n;
            }
        }

        return best;
    }

    private static double CrossFt(LatLon p, RunwayInfo runway) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(p.Lat, p.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading))
        * GeoMath.FeetPerNm;

    /// <summary>
    /// The unvisited neighbor of <paramref name="cur"/> with the smallest cross-track
    /// from the runway centerline (i.e. closest toward the runway), excluding runway
    /// centerline edges so the walk never leaves the taxiway onto the runway before
    /// the junction arc.
    /// </summary>
    private static GroundNode? NextTowardRunway(GroundNode cur, RunwayInfo runway, HashSet<int> visited)
    {
        GroundNode? next = null;
        double bestCross = CrossFt(cur.Position, runway);

        foreach (var e in cur.Edges)
        {
            if (e.IsRunwayCenterline)
            {
                continue;
            }

            var o = e.OtherNode(cur);
            if (visited.Contains(o.Id))
            {
                continue;
            }

            double oc = CrossFt(o.Position, runway);
            if (oc < bestCross)
            {
                bestCross = oc;
                next = o;
            }
        }

        return next;
    }

    /// <summary>
    /// Find a runway fillet arc adjacent to <paramref name="node"/> whose far end is
    /// on the runway centerline and whose exit tangent aligns with the departure
    /// heading. A fillet arc is a junction <see cref="GroundArc"/> (not a centerline
    /// segment) whose combined taxiway name names the runway.
    /// </summary>
    private static bool TryFindDepartureAlignedFilletArc(
        GroundNode node,
        RunwayInfo runway,
        double rwyHdgDeg,
        out GroundArc? filletArc,
        out GroundNode? centerlineNode
    )
    {
        foreach (var e in node.Edges)
        {
            if (e is not GroundArc arc || arc.IsRunwayCenterline)
            {
                continue;
            }

            if (!arc.TaxiwayName.Contains("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var other = arc.OtherNode(node);
            if (!IsOnRunwayCenterline(other, runway))
            {
                continue;
            }

            double exitTangentDeg = ArcExitTangentTowardDeg(arc, other);
            if (GeoMath.AbsBearingDifference(exitTangentDeg, rwyHdgDeg) > TangentAlignToleranceDeg)
            {
                continue;
            }

            filletArc = arc;
            centerlineNode = other;
            return true;
        }

        filletArc = null;
        centerlineNode = null;
        return false;
    }

    private static bool IsOnRunwayCenterline(GroundNode node, RunwayInfo runway)
    {
        foreach (var e in node.Edges)
        {
            if (e.IsRunwayCenterline && e.MatchesRunway(runway.Designator))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Exit tangent (degrees true) of a fillet arc arriving at <paramref name="toNode"/>.
    /// The arc is a cubic Bézier P0=Nodes[0], P1, P2, P3=Nodes[1]; the tangent at an
    /// endpoint runs from the adjacent control point to that endpoint.
    /// </summary>
    private static double ArcExitTangentTowardDeg(GroundArc arc, GroundNode toNode)
    {
        if (toNode.Id == arc.Nodes[1].Id)
        {
            return GeoMath.BearingTo(arc.P2Lat, arc.P2Lon, toNode.Position.Lat, toNode.Position.Lon);
        }

        return GeoMath.BearingTo(arc.P1Lat, arc.P1Lon, toNode.Position.Lat, toNode.Position.Lon);
    }

    /// <summary>
    /// Build the taxi route: a virtual segment from the aircraft pose to the nearest
    /// node, the taxiway edges along <paramref name="path"/> to the junction, the
    /// fillet arc onto the centerline node, then a virtual straight rollout segment
    /// down the runway heading past the arc exit. The rollout straight is what the
    /// navigator brakes to a stop on (LUAW) or flows through (rolling) — braking to
    /// a stop on the fillet arc itself would deadlock the closed-form arc playback
    /// (it cannot advance below the arc speed floor). It is projected along the
    /// runway departure heading rather than walked off the graph, because at a
    /// runway-crossing taxiway the centerline node's graph continuation can point
    /// the wrong way (toward the reciprocal end or across the runway). Returns null
    /// if any taxiway edge is missing (defensive; the walk built the path from real
    /// adjacencies).
    /// </summary>
    private static TaxiRoute? BuildRoute(
        LatLon acPos,
        List<GroundNode> path,
        GroundArc filletArc,
        GroundNode junction,
        GroundNode centerlineNode,
        RunwayInfo runway
    )
    {
        var segments = new List<TaxiRouteSegment>();

        var start = path[0];
        string leadTaxiway = start.Edges.FirstOrDefault(e => !e.IsRunwayCenterline)?.TaxiwayName ?? filletArc.TaxiwayName;
        var virtualStart = VirtualNode.Create(acPos.Lat, acPos.Lon);
        var approachEdge = new GroundEdge
        {
            Nodes = [virtualStart, start],
            TaxiwayName = leadTaxiway,
            DistanceNm = Math.Max(GeoMath.DistanceNm(acPos, start.Position), 0.0001),
        };
        segments.Add(new TaxiRouteSegment { TaxiwayName = leadTaxiway, Edge = approachEdge.Directed(virtualStart, start) });

        for (int i = 0; i < path.Count - 1; i++)
        {
            var fromNode = path[i];
            var toNode = path[i + 1];
            var edge = FindEdgeBetween(fromNode, toNode.Id);
            if (edge is null)
            {
                return null;
            }

            segments.Add(new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(fromNode, toNode) });
        }

        segments.Add(new TaxiRouteSegment { TaxiwayName = filletArc.TaxiwayName, Edge = filletArc.Directed(junction, centerlineNode) });

        string centerlineTaxiway = centerlineNode.Edges.FirstOrDefault(e => e.IsRunwayCenterline)?.TaxiwayName ?? filletArc.TaxiwayName;
        var (rolloutLat, rolloutLon) = GeoMath.ProjectPoint(
            centerlineNode.Position.Lat,
            centerlineNode.Position.Lon,
            runway.TrueHeading,
            LineUpGeometry.RolloutLengthFt / GeoMath.FeetPerNm
        );
        var rolloutTarget = VirtualNode.Create(rolloutLat, rolloutLon);
        segments.Add(VirtualNode.CreateSegment(centerlineNode, rolloutTarget, centerlineTaxiway));

        return new TaxiRoute { Segments = segments, HoldShortPoints = [] };
    }

    private static IGroundEdge? FindEdgeBetween(GroundNode fromNode, int toNodeId)
    {
        foreach (var edge in fromNode.Edges)
        {
            if (edge.OtherNodeId(fromNode.Id) == toNodeId)
            {
                return edge;
            }
        }

        return null;
    }
}
