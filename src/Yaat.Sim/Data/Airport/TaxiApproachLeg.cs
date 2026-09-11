using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Bridges the gap between where an aircraft actually stands and where its resolved taxi route starts.
/// The pathfinder only walks graph edges, so a route begins at the node nearest the aircraft — which can be
/// a hundred feet away: a plain <c>PUSH</c> leaves an aircraft out on the apron ahead of the ramp node its
/// route picks up from, and the first segment is then a fillet arc the aircraft is not on. Nothing drove it
/// there, so the navigator's arc playback writes it onto the arc start in one tick.
///
/// <para>The leg is the drive the pilot would actually make: apron transit is the pilot's discretion in
/// coordination with ramp control (7110.65 §3-7-2 NOTE 2 — ATC approval is required only to enter the
/// movement area, AIM 4-3-18.a.1) and the apron is aircraft-usable pavement (AIM 2-3-4.c.2). It is a
/// <see cref="VirtualNode"/> segment named RAMP, exactly as <see cref="RampLaneReposition"/> builds its cut, so
/// the navigator, hold-short annotation, snapshots and the client overlay need nothing special and the
/// clearance readback is unchanged.</para>
/// </summary>
public static class TaxiApproachLeg
{
    private static readonly ILogger Log = SimLog.CreateLogger("TaxiApproachLeg");

    /// <summary>Beyond this the node is not "on the way" — a node abeam or behind is left to pure pursuit.</summary>
    private const double MaxOffRouteBearingDeg = 90.0;

    /// <summary>
    /// How close (deg) the aircraft's own heading must be to the runway centerline it is standing on — in
    /// either direction of that edge — for the drive to count as a roll along the runway rather than a move
    /// across the pavement beside it.
    /// </summary>
    private const double AlongRunwayToleranceDeg = 15.0;

    /// <summary>
    /// Runway width (ft) used when the layout's runway record carries none — the same 150 ft default
    /// <see cref="GeoJsonParser"/> falls back to when nav data supplies no width for the runway.
    /// </summary>
    private const double DefaultRunwayWidthFt = 150.0;

    /// <summary>
    /// The route with a free-space leg from <paramref name="position"/> to its start node prepended, or
    /// <paramref name="route"/> unchanged when any guard refuses:
    /// <list type="number">
    /// <item>the route has segments to start from at all;</item>
    /// <item>the aircraft is more than <see cref="AirportGroundLayout.AtNodeToleranceFt"/> from that node — closer
    /// than that it is standing on it and there is nothing to drive;</item>
    /// <item>the node is not a runway holding position, and the route holds short at it. An aircraft waiting at
    /// one sits half a fuselage behind the bar node, and driving up to the node would put its nose past the
    /// holding-position marking (AIM 2-3-5.a.1); <see cref="Phases.Ground.TaxiingPhase"/> also needs the real
    /// node id to hold at the bar;</item>
    /// <item>the node lies within <see cref="MaxOffRouteBearingDeg"/> of the route's own departure bearing — a node
    /// behind the aircraft with the route continuing ahead means it has already driven past the start, and pure
    /// pursuit converges onto the line from where it is;</item>
    /// <item>the drive is short enough (<see cref="RampLaneReposition.MaxCrossingFt"/>) and crosses no runway
    /// centerline — a free-space leg follows no painted line and is not obstacle-aware. A roll ALONG the
    /// runway the aircraft is ON (<see cref="AlongRunwayBoundFt"/>) is the exception: it is bounded by that
    /// runway's own length instead, and the centerline rule does not apply because the aircraft is not crossing
    /// a runway, it is on one, with the centerline as its guide.</item>
    /// </list>
    /// </summary>
    public static TaxiRoute Prepend(AirportGroundLayout layout, LatLon position, TrueHeading heading, TaxiRoute route)
    {
        if (route.Segments.Count == 0)
        {
            Log.LogDebug("[ApproachLeg] no leg: the route has no segments");
            return route;
        }

        var from = route.Segments[0].Edge.FromNode;
        double distFt = GeoMath.DistanceNm(position, from.Position) * GeoMath.FeetPerNm;

        string? refusal = Refusal(layout, position, heading, route, from, distFt);
        if (refusal is not null)
        {
            Log.LogDebug("[ApproachLeg] no leg to node {NodeId} ({DistFt:F0} ft): {Refusal}", from.Id, distFt, refusal);
            return route;
        }

        var leg = VirtualNode.CreateSegment(VirtualNode.Create(position.Lat, position.Lon), from, "RAMP");
        Log.LogDebug("[ApproachLeg] prepended {DistFt:F0} ft free-space leg to node {NodeId}", distFt, from.Id);

        return new TaxiRoute
        {
            Segments = [leg, .. route.Segments],
            HoldShortPoints = route.HoldShortPoints,
            Warnings = route.Warnings,
            MandatoryConnectorCount = route.MandatoryConnectorCount,
            DestinationParking = route.DestinationParking,
            DestinationSpot = route.DestinationSpot,
        };
    }

    /// <summary>The guard that refuses the leg, or null when the drive to the route's start node is a legitimate one.</summary>
    private static string? Refusal(AirportGroundLayout layout, LatLon position, TrueHeading heading, TaxiRoute route, GroundNode from, double distFt)
    {
        if (distFt <= AirportGroundLayout.AtNodeToleranceFt)
        {
            return "the aircraft is already at the node";
        }

        if ((route.GetHoldShortAt(from.Id) is not null) || (from.Type == GroundNodeType.RunwayHoldShort))
        {
            return "the route starts at a holding position";
        }

        double offRouteDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, from.Position), route.Segments[0].Edge.DepartureBearing);
        if (offRouteDeg > MaxOffRouteBearingDeg)
        {
            return $"the node is {offRouteDeg:F0}° off the route's departure bearing — the aircraft is past it";
        }

        if (AlongRunwayBoundFt(layout, position, heading, from) is { } runwayLengthFt)
        {
            return distFt > runwayLengthFt ? $"the roll along the runway is {distFt:F0} ft, beyond the runway's own {runwayLengthFt:F0} ft" : null;
        }

        if (distFt > RampLaneReposition.MaxCrossingFt)
        {
            return $"the drive is {distFt:F0} ft, beyond the {RampLaneReposition.MaxCrossingFt:F0} ft free-space bound";
        }

        return layout.RunwayCenterlineBetween(position, from.Position) ? "a runway centerline lies between" : null;
    }

    /// <summary>
    /// The length bound (ft) for a roll along the runway the aircraft is ON, or null when the drive to
    /// <paramref name="from"/> is not one. Three tests, all of which must hold: the node carries a
    /// runway-centerline edge whose runway the layout knows, the aircraft lies within half that runway's width
    /// of the centerline's own line, and its <paramref name="heading"/> is within
    /// <see cref="AlongRunwayToleranceDeg"/> of that centerline in either direction.
    ///
    /// <para>This is the landing rollout — the route starts at the runway-exit fillet ahead and the aircraft is
    /// still on the runway behind it, rolling out to exit it (7110.65 §3-10-9 RUNWAY EXITING a and NOTE 1;
    /// AIM 4-3-21.a and b) — so the apron-length bound and the no-crossing rule, both written for a drive across
    /// open pavement, would refuse the one leg the aircraft is certain to be able to make. Bearing to the node is
    /// not enough on its own: a 15° cone is 800 ft wide 3,000 ft out, which is a parallel taxiway, and the leg
    /// from there would run across that taxiway's holding position marking and onto the runway (AIM 4-3-18.a.5,
    /// AIM 2-3-5.a.1).</para>
    ///
    /// <para>The bound is the runway's own length, read from the layout's runway record: a rollout can only be
    /// short of the far end. That record's width is what places the aircraft on the pavement, falling back to
    /// <see cref="DefaultRunwayWidthFt"/> when it carries none.</para>
    /// </summary>
    private static double? AlongRunwayBoundFt(AirportGroundLayout layout, LatLon position, TrueHeading heading, GroundNode from)
    {
        if (!AirportGroundLayout.HasRunwayCenterlineEdge(from))
        {
            return null;
        }

        foreach (var edge in from.Edges)
        {
            if (!edge.IsRunwayCenterline || (RunwayForEdge(layout, edge) is not { } runway))
            {
                continue;
            }

            double edgeDeg = GeoMath.BearingTo(from.Position, edge.OtherNode(from).Position);
            double offHeadingDeg = Math.Min(
                GeoMath.AbsBearingDifference(heading.Degrees, edgeDeg),
                GeoMath.AbsBearingDifference(heading.Degrees, (edgeDeg + 180.0) % 360.0)
            );
            if (offHeadingDeg > AlongRunwayToleranceDeg)
            {
                continue;
            }

            double widthFt = runway.WidthFt > 0.0 ? runway.WidthFt : DefaultRunwayWidthFt;
            double crossTrackFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, from.Position, new TrueHeading(edgeDeg))) * GeoMath.FeetPerNm;
            if (crossTrackFt > (widthFt / 2.0))
            {
                continue;
            }

            return RunwayLengthFt(runway);
        }

        return null;
    }

    /// <summary>
    /// The layout's record for the runway <paramref name="edge"/> is a centerline of, or null when no runway
    /// claims the edge's designators — without the record there is neither a width to place the aircraft on the
    /// pavement with nor a length to bound the roll, so the ordinary apron rules decide.
    /// </summary>
    private static GroundRunway? RunwayForEdge(AirportGroundLayout layout, IGroundEdge edge)
    {
        foreach (var runway in layout.Runways)
        {
            if (edge.MatchesRunway(runway.Id.End1) || edge.MatchesRunway(runway.Id.End2))
            {
                return runway;
            }
        }

        return null;
    }

    /// <summary>Length (ft) of the runway's painted centerline, walked as the polyline the layout stores for it.</summary>
    private static double RunwayLengthFt(GroundRunway runway)
    {
        double lengthFt = 0.0;
        for (int i = 1; i < runway.Coordinates.Count; i++)
        {
            var a = new LatLon(runway.Coordinates[i - 1].Lat, runway.Coordinates[i - 1].Lon);
            var b = new LatLon(runway.Coordinates[i].Lat, runway.Coordinates[i].Lon);
            lengthFt += GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;
        }

        return lengthFt;
    }
}
