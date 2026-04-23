using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Builds a safe ingress onto the taxi graph from an aircraft's current
/// position. When an aircraft spawns at Coordinates (lat/lon, not a graph
/// node) or is otherwise displaced off the taxiway graph, the subsequent
/// TAXI command's pathfinder starts at the nearest graph node — leaving a
/// gap between the aircraft's actual position and the route. Without an
/// ingress segment, <c>GroundNavigator</c> has no explicit geometry to
/// follow between the two, and the aircraft's path across that gap depends
/// on steering heuristics rather than a specified line.
///
/// <para>
/// The resolver computes an ordered list of virtual segments that chain
/// <c>aircraft → ingressTarget → startNode</c>. The ingress target is the
/// nearest graph point that the aircraft can reach via a straight line
/// without crossing any other graph edges (taxiway or runway). Candidates
/// include the nearest node itself and the foot-of-perpendicular on every
/// edge incident to the nearest node — so an aircraft slightly off a long
/// taxiway joins that taxiway at its closest point, not at the far end.
/// </para>
///
/// <para>
/// When no safe ingress exists (pathological placement — aircraft on the
/// far side of a runway from its nearest node, etc.) the resolver returns
/// an empty list and the caller falls back to the existing behaviour (the
/// pathfinder's route starts at the nearest node, and the navigator steers
/// toward it with whatever angle the pure-pursuit computes).
/// </para>
/// </summary>
public static class TaxiIngressResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("TaxiIngressResolver");

    /// <summary>
    /// Below this distance (feet) between aircraft and the start node, no
    /// ingress segment is created — the aircraft is essentially at the node
    /// and the existing pathfinder behaviour is adequate.
    /// </summary>
    public const double IngressThresholdFt = 10.0;

    /// <summary>
    /// Compute the ingress segments prepending the taxi route. Returns an
    /// empty list when no ingress is needed (aircraft already at / within
    /// <see cref="IngressThresholdFt"/> of the start node) or when no safe
    /// candidate exists.
    /// </summary>
    /// <param name="layout">Airport ground graph.</param>
    /// <param name="startNode">
    /// The pathfinder's starting node (today's behaviour: nearest node to
    /// the aircraft). The resolver's ingress chain always terminates at this
    /// node so the existing route can be appended unchanged.
    /// </param>
    /// <param name="acLat">Aircraft latitude.</param>
    /// <param name="acLon">Aircraft longitude.</param>
    /// <param name="firstRouteSegment">
    /// The first segment of the pathfinder's route (the one leaving
    /// <paramref name="startNode"/>). Used to restrict mid-edge ingress
    /// candidates to the edge the route is about to traverse — otherwise a
    /// foot projection onto an incident-but-orthogonal edge would force the
    /// aircraft to backtrack. Null for routes without a next segment (e.g.
    /// when ingress is the only thing).
    /// </param>
    public static IngressPlan Resolve(
        AirportGroundLayout layout,
        GroundNode startNode,
        double acLat,
        double acLon,
        TaxiRouteSegment? firstRouteSegment = null
    )
    {
        double distToStartFt = GeoMath.DistanceNm(acLat, acLon, startNode.Latitude, startNode.Longitude) * GeoMath.FeetPerNm;
        if (distToStartFt < IngressThresholdFt)
        {
            return IngressPlan.None;
        }

        // Candidate 1: ingress straight to the nearest node.
        // Candidate 2: perpendicular projection onto the first route edge (if
        // that edge is straight and the foot lies on its interior closer to
        // the aircraft than the node itself). Restricted to the first route
        // edge so the continuation (foot → startNode via that edge) never
        // backtracks — otherwise the aircraft would be rotated toward the
        // foot and then have to reverse to follow the route.
        var candidates = new List<Candidate> { new(startNode.Latitude, startNode.Longitude, distToStartFt, Node: startNode, Edge: null) };

        if (firstRouteSegment is not null && firstRouteSegment.Edge.Edge is GroundEdge routeEdge)
        {
            var other = firstRouteSegment.Edge.ToNode;
            if (other is not null && other.Id != startNode.Id)
            {
                var foot = GeoMath.FootOfPerpendicular(acLat, acLon, startNode.Latitude, startNode.Longitude, other.Latitude, other.Longitude);
                if (!foot.Clamped)
                {
                    double distFt = GeoMath.DistanceNm(acLat, acLon, foot.FootLat, foot.FootLon) * GeoMath.FeetPerNm;
                    if (distFt < distToStartFt - 1e-6)
                    {
                        candidates.Add(new Candidate(foot.FootLat, foot.FootLon, distFt, Node: null, Edge: routeEdge));
                    }
                }
            }
        }

        candidates.Sort(static (a, b) => a.DistFt.CompareTo(b.DistFt));

        foreach (var c in candidates)
        {
            if (IngressLineCrossesAnyEdge(layout, acLat, acLon, c.Lat, c.Lon, startNode, c.Edge))
            {
                continue;
            }

            var virtualAc = VirtualNode.Create(acLat, acLon);

            if (c.Node is not null)
            {
                // Straight ingress to the start node. Pathfinder's first
                // segment stays — the aircraft reaches startNode first.
                Log.LogDebug(
                    "[Ingress] straight ingress: aircraft({AcLat:F6},{AcLon:F6}) -> node {NodeId}, {DistFt:F1}ft",
                    acLat,
                    acLon,
                    startNode.Id,
                    c.DistFt
                );
                var prepend = VirtualNode.CreateSegment(virtualAc, startNode, "ingress");
                return new IngressPlan([prepend], ReplaceFirstRouteSegment: false);
            }

            // Mid-edge ingress: aircraft → virtualFoot. The continuation from
            // virtualFoot to the route's target node replaces the pathfinder's
            // first segment (same underlying edge, starting from the foot
            // instead of startNode). Without the replacement the aircraft
            // would traverse the ingress foot → startNode segment (backward
            // on the same edge) before heading forward again.
            var virtualFoot = VirtualNode.Create(c.Lat, c.Lon);
            var ingressSeg = VirtualNode.CreateSegment(virtualAc, virtualFoot, "ingress");
            var routeTarget = firstRouteSegment!.Edge.ToNode;
            var replacementSeg = VirtualNode.CreateSegment(virtualFoot, routeTarget, firstRouteSegment.TaxiwayName);
            Log.LogDebug(
                "[Ingress] mid-edge ingress: aircraft({AcLat:F6},{AcLon:F6}) -> foot({FLat:F6},{FLon:F6})[{IngFt:F1}ft] -> node {NodeId} via {Twy} (replaces first route segment)",
                acLat,
                acLon,
                c.Lat,
                c.Lon,
                c.DistFt,
                routeTarget.Id,
                firstRouteSegment.TaxiwayName
            );
            return new IngressPlan([ingressSeg, replacementSeg], ReplaceFirstRouteSegment: true);
        }

        Log.LogDebug(
            "[Ingress] no safe ingress from ({AcLat:F6},{AcLon:F6}) — every candidate line crosses another edge. Falling back to direct route from node {NodeId}",
            acLat,
            acLon,
            startNode.Id
        );
        return IngressPlan.None;
    }

    /// <summary>
    /// Apply an <see cref="IngressPlan"/> to a <see cref="TaxiRoute"/> —
    /// remove the route's current first segment when the plan requests it,
    /// then prepend the plan's segments. Call after resolving a route from
    /// the pathfinder and before hold-short annotation.
    /// </summary>
    public static void Apply(TaxiRoute route, IngressPlan plan)
    {
        if (plan.Segments.Count == 0)
        {
            return;
        }
        if (plan.ReplaceFirstRouteSegment && route.Segments.Count > 0)
        {
            route.Segments.RemoveAt(0);
        }
        for (int i = plan.Segments.Count - 1; i >= 0; i--)
        {
            route.Segments.Insert(0, plan.Segments[i]);
        }
    }

    /// <summary>
    /// Returns true if the straight line from <c>(fromLat, fromLon)</c> to
    /// <c>(toLat, toLon)</c> crosses any layout edge other than those we
    /// expect to share an endpoint with the candidate (edges incident to
    /// <paramref name="targetNode"/>) or the edge the candidate sits on
    /// (<paramref name="excludeEdge"/>, for mid-edge foot candidates).
    /// </summary>
    private static bool IngressLineCrossesAnyEdge(
        AirportGroundLayout layout,
        double fromLat,
        double fromLon,
        double toLat,
        double toLon,
        GroundNode targetNode,
        IGroundEdge? excludeEdge
    )
    {
        foreach (var edge in layout.AllEdges)
        {
            if (excludeEdge is not null && ReferenceEquals(edge, excludeEdge))
            {
                continue;
            }

            // An edge that shares the candidate node as an endpoint will
            // trivially "touch" at that endpoint; exclude from crossing logic.
            if (edge.Nodes[0].Id == targetNode.Id || edge.Nodes[1].Id == targetNode.Id)
            {
                continue;
            }

            var hit = GeoMath.SegmentsIntersect(
                fromLat,
                fromLon,
                toLat,
                toLon,
                edge.Nodes[0].Latitude,
                edge.Nodes[0].Longitude,
                edge.Nodes[1].Latitude,
                edge.Nodes[1].Longitude
            );
            if (hit is not null)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct Candidate(double Lat, double Lon, double DistFt, GroundNode? Node, IGroundEdge? Edge);
}

/// <summary>
/// Result of <see cref="TaxiIngressResolver.Resolve"/>: the ingress segments
/// to prepend and a flag indicating whether the caller should also remove
/// the route's original first segment (the mid-edge optimisation reuses the
/// same edge but starting from a virtual foot, making the original first
/// segment redundant).
/// </summary>
public sealed record IngressPlan(List<TaxiRouteSegment> Segments, bool ReplaceFirstRouteSegment)
{
    /// <summary>Empty ingress — no segments to prepend and no replacement needed.</summary>
    public static IngressPlan None { get; } = new([], ReplaceFirstRouteSegment: false);
}
