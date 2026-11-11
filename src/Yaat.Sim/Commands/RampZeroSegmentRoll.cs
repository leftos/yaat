using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Commands;

/// <summary>
/// A plain <c>TAXI $spot</c> / <c>TAXI @gate</c> kept inside the ramp whose taxi starts on the destination itself while the
/// aircraft stands <see cref="AtDestinationThresholdFt"/> or more from it. On the destination's own lane facing it
/// (<see cref="TryPlanRoll"/>) the aircraft rolls onto it: straight along a straight lane, along the curve on a fillet arc.
/// Otherwise it is re-routed inside the ramp from the destination's graph neighbour nearest it, or refused when no such route
/// plans. Never a re-route over the movement area.
/// </summary>
internal static class RampZeroSegmentRoll
{
    private static readonly ILogger Log = SimLog.CreateLogger("RampZeroSegmentRoll");

    /// <summary>
    /// Distance (ft) from the destination under which an aircraft whose taxi starts on the destination node is at it: it
    /// holds or parks where it stands, with no route to drive.
    /// </summary>
    internal const double AtDestinationThresholdFt = 50.0;

    /// <summary>
    /// Largest angle (°) the heading may lie off the lane's bearing into the destination, and off the bearing to it, for the
    /// aircraft to count as facing it.
    /// </summary>
    internal const double MaxHeadingOffDeg = 45.0;

    /// <summary>Step (ft) along a fillet arc over which its tangent bearing at the aircraft's point is taken.</summary>
    private const double ArcTangentStepFt = 5.0;

    /// <summary>What a zero-segment ramp TAXI needs to plan: the aircraft, its own confined request, and how the route is finished.</summary>
    /// <param name="Aircraft">The aircraft cleared to taxi.</param>
    /// <param name="Confined">The ramp-confined request the TAXI planned with; its start node is the destination.</param>
    /// <param name="WithDestination">Sets the clearance's destination on a planned route.</param>
    internal sealed record Request(AircraftState Aircraft, RampConfinedRouteRequest Confined, Func<TaxiRoute, TaxiRoute> WithDestination);

    /// <summary>The planned route, null when refused, and whether it is the roll onto the destination.</summary>
    internal readonly record struct Outcome(TaxiRoute? Route, bool IsRoll);

    /// <summary>The lane edge into the destination an aircraft stands on: the edge, its other node, cross-track and bearing in.</summary>
    private sealed record LaneFit(IGroundEdge Edge, GroundNode Other, double CrossFt, double LaneBearingDeg);

    /// <summary>
    /// The roll's lane test, one-way flow aside: <paramref name="position"/> lies on a lane edge or fillet arc into
    /// <paramref name="destination"/>, foot strictly inside it, and <paramref name="headingDeg"/> faces the destination along it.
    /// </summary>
    internal static bool IsOnLaneFacing(GroundNode destination, LatLon position, double headingDeg)
    {
        if (NearestLane(destination, position) is not { } lane)
        {
            return false;
        }

        double offLaneDeg = GeoMath.AbsBearingDifference(headingDeg, lane.LaneBearingDeg);
        double offBearingDeg = GeoMath.AbsBearingDifference(headingDeg, GeoMath.BearingTo(position, destination.Position));
        return Faces(lane, offLaneDeg, offBearingDeg);
    }

    /// <summary>
    /// The lane fit is within <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/> and both angles within
    /// <see cref="MaxHeadingOffDeg"/>.
    /// </summary>
    private static bool Faces(LaneFit lane, double offLaneDeg, double offBearingDeg) =>
        (lane.CrossFt <= AirportGroundLayout.OnTaxiEdgeMaxOffsetFt) && (offLaneDeg <= MaxHeadingOffDeg) && (offBearingDeg <= MaxHeadingOffDeg);

    /// <summary>
    /// True when <paramref name="position"/> lies <see cref="AtDestinationThresholdFt"/> or more from
    /// <paramref name="destination"/>.
    /// </summary>
    internal static bool IsAwayFrom(LatLon position, GroundNode destination) =>
        DistanceFt(position, destination.Position) >= AtDestinationThresholdFt;

    /// <summary>
    /// The route of a zero-segment ramp TAXI: the roll onto the destination when the aircraft is on its lane facing it, else the
    /// ramp-confined route from the destination's neighbour nearest the aircraft with the approach leg onto it, else null (refused).
    /// </summary>
    internal static Outcome Plan(AirportGroundLayout layout, Request request)
    {
        AircraftState aircraft = request.Aircraft;
        GroundNode destination = request.Confined.Destination;
        if (TryPlanRoll(layout, aircraft, request.Confined) is { } roll)
        {
            return new Outcome(request.WithDestination(roll), IsRoll: true);
        }

        GroundNode? neighbour = NearestNeighbour(destination, aircraft.Position);
        TaxiRoute? confined = neighbour is null
            ? null
            : RampLaneReposition.TryPlanRampConfinedRoute(layout, request.Confined with { StartNodeId = neighbour.Id });
        if (confined is not { Segments.Count: > 0 })
        {
            Log.LogDebug(
                "[RampRoll] {Callsign} to {Dest}: refuse, no route inside the ramp from neighbour #{Neighbour}",
                aircraft.Callsign,
                destination.Name,
                neighbour?.Id
            );
            return new Outcome(null, IsRoll: false);
        }

        Log.LogDebug(
            "[RampRoll] {Callsign} to {Dest}: confined neighbour route from #{Neighbour} ({Summary})",
            aircraft.Callsign,
            destination.Name,
            neighbour!.Id,
            confined.ToSummary()
        );
        TaxiRoute withLeg = TaxiApproachLeg.Prepend(
            layout,
            aircraft.Position,
            aircraft.TrueHeading,
            request.WithDestination(confined),
            request.Confined.AircraftLengthFt
        );
        return new Outcome(withLeg, IsRoll: false);
    }

    /// <summary>
    /// The roll onto <paramref name="destination"/>, or null when the aircraft is not on its lane facing it. The lane is an edge
    /// into the destination (straight or a fillet arc, never a runway centreline) whose foot from the aircraft falls strictly
    /// inside it, within <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/> of it; the heading lies within
    /// <see cref="MaxHeadingOffDeg"/> of both the lane's bearing into the destination and the bearing to it; and the move into
    /// the destination along the lane is not against a one-way flow.
    /// </summary>
    private static TaxiRoute? TryPlanRoll(AirportGroundLayout layout, AircraftState aircraft, RampConfinedRouteRequest confined)
    {
        GroundNode destination = confined.Destination;
        LatLon position = aircraft.Position;
        if (NearestLane(destination, position) is not { } lane)
        {
            Log.LogDebug("[RampRoll] {Callsign} to {Dest}: no roll, on no lane into it", aircraft.Callsign, destination.Name);
            return null;
        }

        double headingDeg = aircraft.TrueHeading.Degrees;
        double offLaneDeg = GeoMath.AbsBearingDifference(headingDeg, lane.LaneBearingDeg);
        double offBearingDeg = GeoMath.AbsBearingDifference(headingDeg, GeoMath.BearingTo(position, destination.Position));
        bool withTheFlow = !OneWayResolver.GetForbiddenMoves(layout, confined.WakeClass).Contains((lane.Other.Id, destination.Id));
        bool admitted = Faces(lane, offLaneDeg, offBearingDeg) && withTheFlow;
        Log.LogDebug(
            "[RampRoll] {Callsign} to {Dest}: {Verdict} on {Lane} {Kind} #{Other}-#{DestId}: {Cross:F1} ft off it, {OffLane:F1}° off its "
                + "bearing in, {OffBearing:F1}° off the bearing to it, with the flow {WithFlow}",
            aircraft.Callsign,
            destination.Name,
            admitted ? (lane.Edge is GroundArc ? "arc roll" : "roll") : "no roll",
            lane.Edge.TaxiwayName,
            lane.Edge is GroundArc ? "arc" : "edge",
            lane.Other.Id,
            destination.Id,
            lane.CrossFt,
            offLaneDeg,
            offBearingDeg,
            withTheFlow
        );
        if (!admitted)
        {
            return null;
        }

        return lane.Edge is GroundArc arc ? ArcRoll(arc, lane.Other, destination) : StraightRoll(position, destination);
    }

    /// <summary>One free-space segment from the aircraft to the destination node.</summary>
    private static TaxiRoute StraightRoll(LatLon position, GroundNode destination) =>
        new() { Segments = [VirtualNode.CreateSegment(VirtualNode.Create(position.Lat, position.Lon), destination, "RAMP")], HoldShortPoints = [] };

    /// <summary>
    /// The fillet arc driven into the destination, alone: an admitted pose is already along the arc, so pure pursuit closes onto
    /// it with no approach leg, which would send the aircraft back to the arc's start.
    /// </summary>
    private static TaxiRoute ArcRoll(GroundArc arc, GroundNode other, GroundNode destination) =>
        new() { Segments = [new TaxiRouteSegment { TaxiwayName = arc.TaxiwayName, Edge = arc.Directed(other, destination) }], HoldShortPoints = [] };

    /// <summary>The lane into <paramref name="destination"/> the position lies on with the least cross-track, or null.</summary>
    private static LaneFit? NearestLane(GroundNode destination, LatLon position)
    {
        LaneFit? best = null;
        foreach (IGroundEdge edge in destination.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            GroundNode other = edge.OtherNode(destination);
            LaneFit? fit = edge is GroundArc arc ? FitArc(arc, other, destination, position) : FitStraight(edge, other, destination, position);
            if ((fit is not null) && ((best is null) || (fit.CrossFt < best.CrossFt)))
            {
                best = fit;
            }
        }

        return best;
    }

    /// <summary>The fit on a straight edge, or null when the foot from the position falls outside it.</summary>
    private static LaneFit? FitStraight(IGroundEdge edge, GroundNode other, GroundNode destination, LatLon position)
    {
        if (GeoMath.FootOfPerpendicular(position, other.Position, destination.Position).Clamped)
        {
            return null;
        }

        double crossFt = GeoMath.DistanceToSegmentFt(position, other.Position, destination.Position);
        return new LaneFit(edge, other, crossFt, GeoMath.BearingTo(other.Position, destination.Position));
    }

    /// <summary>
    /// The fit on a fillet arc (<see cref="GroundArc.Locate"/>), or null when the position's nearest point is not strictly
    /// inside it. The bearing in is the arc's tangent at that point, toward the destination.
    /// </summary>
    private static LaneFit? FitArc(GroundArc arc, GroundNode other, GroundNode destination, LatLon position)
    {
        (double alongFt, double offFt) = arc.Locate(destination, position);
        if ((alongFt <= 0.0) || (alongFt >= (arc.DistanceNm * GeoMath.FeetPerNm)) || (arc.Locate(other, position).AlongFt <= 0.0))
        {
            return null;
        }

        LatLon at = arc.PointAtFt(destination, alongFt);
        LatLon ahead = arc.PointAtFt(destination, Math.Max(0.0, alongFt - ArcTangentStepFt));
        return new LaneFit(arc, other, offFt, GeoMath.BearingTo(at, ahead));
    }

    /// <summary>The node at the other end of an edge of <paramref name="destination"/> nearest <paramref name="position"/>.</summary>
    private static GroundNode? NearestNeighbour(GroundNode destination, LatLon position) =>
        destination.Edges.Select(e => e.OtherNode(destination)).MinBy(n => GeoMath.DistanceNm(position, n.Position));

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;
}
