using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Commands;

/// <summary>
/// The RPO warning a TAXI or TAXIAUTO earns when the facing a push left makes it turn about: <c>THY9WC pushed facing 212 on B and
/// turns about to taxi; PUSH B B5 or PUSH B FACE NE would have faced the route</c>. The push's facing stays the RPO's to give,
/// so the taxi is made as cleared and the warning names the pushes that would have faced the route, unchecked by the planner.
/// </summary>
public static partial class GroundCommandHandler
{
    /// <summary>
    /// How far, in degrees, the nose may lie off the axis of the edge the aircraft stands on and still count as lined up
    /// along it, either way.
    /// </summary>
    private const double AlongLaneToleranceDeg = 45.0;

    /// <summary>What the turn-about warning reads from the TAXI it is for.</summary>
    /// <param name="Layout">The airport the aircraft is on.</param>
    /// <param name="Route">The resolved route, its approach leg included.</param>
    /// <param name="AsCleared">The clearance as the controller worded it.</param>
    private sealed record PushTurnAboutInputs(AirportGroundLayout Layout, TaxiRoute Route, TaxiCommand AsCleared);

    /// <summary>
    /// The turn-about warning for the TAXI's response line, or null. Only an aircraft still holding after a push
    /// (<see cref="HoldingAfterPushbackPhase"/>) that the route turns about is warned (<see cref="TurnsAboutOffThePush"/>). A
    /// response that reaches no controller (a scripted TAXI, a deferred one firing, a queued block the queue fires) carries
    /// no warning: it goes to <see cref="AircraftState.PendingWarnings"/>, an amber terminal line, and null is returned. The
    /// amber line drops the leading callsign, which the client prefixes to every warning entry (<c>THY9WC: pushed facing …</c>).
    /// Read before the phases are cleared, while the hold after the push is still the current phase.
    /// </summary>
    /// <param name="aircraft">The aircraft cleared to taxi.</param>
    /// <param name="inputs">The TAXI the warning is for.</param>
    /// <param name="resultReachesController">The response line reaches the controller who issued the TAXI.</param>
    /// <returns>The warning to append to the response line, or null.</returns>
    private static string? PushTurnAboutResultNote(AircraftState aircraft, PushTurnAboutInputs inputs, bool resultReachesController)
    {
        if (PushTurnAboutWarning(aircraft, inputs) is not { } warning)
        {
            return null;
        }

        if (resultReachesController)
        {
            return $"{aircraft.Callsign} {warning}";
        }

        aircraft.PendingWarnings.Add(warning);
        return null;
    }

    /// <summary>
    /// The warning sentence after the callsign (<c>pushed facing …</c>), or null when the aircraft is not held after a push or the
    /// route does not turn it about.
    /// </summary>
    private static string? PushTurnAboutWarning(AircraftState aircraft, PushTurnAboutInputs inputs)
    {
        if (
            (aircraft.Phases?.CurrentPhase is not HoldingAfterPushbackPhase)
            || (FirstLegDepartureBearing(aircraft.Position, inputs.Route) is not { } firstLegBearing)
            || !TurnsAboutOffThePush(aircraft, inputs, firstLegBearing)
        )
        {
            return null;
        }

        // The lane is named only while the aircraft still stands on it: a current taxiway it has left is stale.
        LaneStand? onCurrent = aircraft.Ground.CurrentTaxiway is { Length: > 0 } current ? StandsOnTaxiway(inputs.Layout, aircraft, current) : null;
        string? lane = onCurrent?.Taxiway;
        MagneticHeading routeFacing = RouteFacing(aircraft, onCurrent?.Edge, firstLegBearing);
        string alternatives = string.Join(" or ", FacingPushes(inputs, lane, routeFacing).Select(CommandDescriber.DescribeCommand));
        string onLane = lane is null ? "" : $" on {lane}";
        return $"pushed facing {aircraft.MagneticHeading.ToDisplayString()}{onLane} and turns about to taxi; "
            + $"{alternatives} would have faced the route";
    }

    /// <summary>The lane the aircraft stands on, and the straight edge of it that it stands on or at; null when it has none there.</summary>
    /// <param name="Taxiway">The lane's name.</param>
    /// <param name="Edge">The lane edge, or null when it stands at a node of the lane with no straight lane edge.</param>
    internal sealed record LaneStand(string Taxiway, GroundEdge? Edge);

    /// <summary>
    /// Where the aircraft stands on <paramref name="taxiway"/>, its end nodes included, or null when it does not: within
    /// <see cref="AirportGroundLayout.AtNodeToleranceFt"/> of a node on it, the lane edge being the straight one at that node
    /// lying most nearly along the heading or its reciprocal; else within <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/>
    /// of a straight edge of it, the perpendicular falling inside the edge.
    /// </summary>
    internal static LaneStand? StandsOnTaxiway(AirportGroundLayout layout, AircraftState aircraft, string taxiway)
    {
        if (layout.FindNearestNodeOnTaxiway(aircraft.Position, taxiway, AirportGroundLayout.AtNodeToleranceFt) is { } node)
        {
            return new LaneStand(taxiway, LaneEdgeAlong(node, taxiway, aircraft.TrueHeading.Degrees));
        }

        foreach (GroundNode laneNode in layout.GetNodesOnTaxiway(taxiway))
        {
            foreach (GroundEdge edge in LaneEdges(laneNode, taxiway))
            {
                if (LiesAlongEdge(aircraft.Position, edge))
                {
                    return new LaneStand(taxiway, edge);
                }
            }
        }

        return null;
    }

    /// <summary>The straight edges of <paramref name="taxiway"/> at <paramref name="node"/>: no runway centreline, no ramp connector.</summary>
    private static IEnumerable<GroundEdge> LaneEdges(GroundNode node, string taxiway) =>
        node.Edges.OfType<GroundEdge>().Where(edge => !edge.IsRunwayCenterline && !edge.IsRamp && edge.MatchesTaxiway(taxiway));

    /// <summary>
    /// The straight edge of <paramref name="taxiway"/> at <paramref name="node"/> whose bearing from the node lies nearest
    /// <paramref name="headingDeg"/> or its reciprocal; null when the node has none.
    /// </summary>
    private static GroundEdge? LaneEdgeAlong(GroundNode node, string taxiway, double headingDeg)
    {
        double AxisOffDeg(GroundEdge edge)
        {
            double offDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, edge.OtherNode(node).Position), headingDeg);
            return Math.Min(offDeg, 180.0 - offDeg);
        }

        return LaneEdges(node, taxiway).MinBy(AxisOffDeg);
    }

    /// <summary>
    /// Whether <paramref name="position"/> lies within <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/> of
    /// <paramref name="edge"/>, the perpendicular from it falling inside the edge.
    /// </summary>
    private static bool LiesAlongEdge(LatLon position, GroundEdge edge)
    {
        (LatLon foot, _, bool clamped) = GeoMath.FootOfPerpendicular(position, edge.Nodes[0].Position, edge.Nodes[1].Position);
        return !clamped && ((GeoMath.DistanceNm(position, foot) * GeoMath.FeetPerNm) <= AirportGroundLayout.OnTaxiEdgeMaxOffsetFt);
    }

    /// <summary>
    /// Whether the route turns the aircraft about. When the start logic turned it about toward the end of the edge it stands
    /// mid-way along (<see cref="TaxiRoute.TurnAboutTargetNodeId"/>), the verdict is the start logic's own: that end lies at
    /// least <see cref="GroundNavigator.ReversalEntryThresholdDeg"/> off the nose (<see cref="OffBearingDeg"/>). Otherwise the
    /// route's first leg, its approach leg included, departs at least that far off the nose.
    /// </summary>
    private static bool TurnsAboutOffThePush(AircraftState aircraft, PushTurnAboutInputs inputs, double firstLegBearing)
    {
        int? targetId = inputs.Route.TurnAboutTargetNodeId;
        double offRouteDeg = targetId is { } id
            ? OffBearingDeg(aircraft, inputs.Layout.Nodes[id])
            : GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, firstLegBearing);
        Log.LogDebug(
            "[TryTaxi] {Callsign}: held after a push, nose {Heading:F1} is {OffRouteDeg:F1}° off {Toward}",
            aircraft.Callsign,
            aircraft.TrueHeading.Degrees,
            offRouteDeg,
            targetId is { } turnAboutTarget ? $"turn-about target node {turnAboutTarget}" : $"the route's first leg {firstLegBearing:F1}"
        );
        return offRouteDeg >= GroundNavigator.ReversalEntryThresholdDeg;
    }

    /// <summary>
    /// The magnetic facing that would have faced the route. On a lane the push lined up along (the nose within
    /// <see cref="AlongLaneToleranceDeg"/> of the axis of <paramref name="laneEdge"/>, the lane edge that named the lane), it is
    /// the reciprocal of the heading, the lane's other direction: the heading, not the edge's bearing, because a push that ends
    /// where the lane bends (SFO B off G10) lines up on the bend. Otherwise (no lane edge, or a push that left the nose across the
    /// lane) it is the route's first-leg bearing.
    /// </summary>
    internal static MagneticHeading RouteFacing(AircraftState aircraft, GroundEdge? laneEdge, double firstLegBearing) =>
        (laneEdge is not null) && NoseAlongEdge(aircraft, laneEdge)
            ? aircraft.MagneticHeading.ToReciprocal()
            : new TrueHeading(firstLegBearing).ToMagnetic(aircraft.Declination);

    /// <summary>Whether the nose lies within <see cref="AlongLaneToleranceDeg"/> of the axis of <paramref name="edge"/>.</summary>
    private static bool NoseAlongEdge(AircraftState aircraft, GroundEdge edge)
    {
        double offAxisDeg = GeoMath.AbsBearingDifference(
            aircraft.TrueHeading.Degrees,
            GeoMath.BearingTo(edge.Nodes[0].Position, edge.Nodes[1].Position)
        );
        return (offAxisDeg <= AlongLaneToleranceDeg) || (offAxisDeg >= 180.0 - AlongLaneToleranceDeg);
    }

    /// <summary>
    /// The pushes that would have faced the route: <c>PUSH {lane} {next taxiway}</c> when the clearance's first taxiway after the
    /// lane differs from it and meets it, then <c>PUSH {lane} FACE {compass}</c> (<c>PUSH FACE {compass}</c> with no lane).
    /// </summary>
    private static IEnumerable<PushbackCommand> FacingPushes(PushTurnAboutInputs inputs, string? lane, MagneticHeading routeFacing)
    {
        if (
            (lane is not null)
            && (NextTaxiwayAfter(lane, inputs.AsCleared.Path) is { } next)
            && (inputs.Layout.FindIntersectionNode(lane, next) is not null)
        )
        {
            yield return new PushbackCommand(null, lane, next, null, false);
        }

        yield return new PushbackCommand(routeFacing, lane, null, null, false);
    }

    /// <summary>
    /// The first taxiway <paramref name="path"/> names after <paramref name="lane"/>, or its first one when it never names the
    /// lane.
    /// </summary>
    private static string? NextTaxiwayAfter(string lane, IReadOnlyList<string> path)
    {
        int laneIndex = -1;
        for (int i = 0; i < path.Count; i++)
        {
            if (string.Equals(path[i], lane, StringComparison.OrdinalIgnoreCase))
            {
                laneIndex = i;
                break;
            }
        }

        return path.Skip(laneIndex + 1).FirstOrDefault(token => !string.Equals(token, lane, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The true bearing the route sets off on from <paramref name="position"/>: the first segment's departure bearing when the
    /// aircraft stands at its start, else the bearing to the first segment's far end. Null for a route with no segments.
    /// </summary>
    private static double? FirstLegDepartureBearing(LatLon position, TaxiRoute route)
    {
        if (route.Segments.Count == 0)
        {
            return null;
        }

        DirectionalEdge first = route.Segments[0].Edge;
        double fromStartFt = GeoMath.DistanceNm(position, first.FromNode.Position) * GeoMath.FeetPerNm;
        return fromStartFt <= AirportGroundLayout.AtNodeToleranceFt
            ? GeometricAdmissibility.GetDepartureBearing(first.Edge, first.FromNode, first.ToNode)
            : GeoMath.BearingTo(position, first.ToNode.Position);
    }
}
