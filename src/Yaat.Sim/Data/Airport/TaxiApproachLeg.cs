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
///
/// <para>A free-space leg is bounded by <see cref="RampLaneReposition.MaxCrossingFt"/>, except where it runs along the
/// straight taxiway the aircraft is on to one of that edge's nodes: an aircraft stopped mid-way along a long taxiway edge
/// follows the centreline it is on, so the leg there carries no length bound.</para>
/// </summary>
public static class TaxiApproachLeg
{
    private static readonly ILogger Log = SimLog.CreateLogger("TaxiApproachLeg");

    /// <summary>Beyond this the node is not "on the way" — a node abeam or behind is left to pure pursuit.</summary>
    private const double MaxOffRouteBearingDeg = 90.0;

    /// <summary>
    /// Cross-track over remaining along-track: up to this ratio the line is intercepted at atan(0.5) ≈ 27°, so
    /// pure pursuit closes onto it before the segment ends.
    /// </summary>
    private const double MaxOffLineRatio = 0.5;

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
    /// <item>the node lies within <see cref="MaxOffRouteBearingDeg"/> of the route's own departure bearing, or the
    /// aircraft sits close enough to the first segment's own line for pure pursuit to converge onto it —
    /// <see cref="MaxOffLineRatio"/> of the segment's length still ahead of the aircraft. A node behind the aircraft
    /// with the route continuing ahead means it has already driven past the start, and pure pursuit converges onto
    /// the line from where it is — but only while the line is within reach: an aircraft stopped well abeam a short
    /// lane's start cannot close the cross-track inside that segment, so the leg is prepended and driven instead;</item>
    /// <item>the drive is short enough (<see cref="RampLaneReposition.MaxCrossingFt"/>) and crosses no runway
    /// centerline — a free-space leg follows no painted line and is not obstacle-aware. A roll ALONG the
    /// runway the aircraft is ON (<see cref="AlongRunwayBoundFt"/>) is the exception: it is bounded by that
    /// runway's own length instead, and the centerline rule does not apply because the aircraft is not crossing
    /// a runway, it is on one, with the centerline as its guide. A leg along the taxiway the aircraft is ON
    /// (<see cref="OccupiedTaxiEdgeLeadingTo"/>) is the other: it follows that taxiway's painted centreline to the
    /// start node, so the length bound is waived, while the runway-centerline rule still applies.</item>
    /// </list>
    /// </summary>
    public static TaxiRoute Prepend(AirportGroundLayout layout, LatLon position, TrueHeading heading, TaxiRoute route)
    {
        if (route.Segments.Count == 0)
        {
            Log.LogDebug("[ApproachLeg] no leg: the route has no segments");
            return route;
        }

        GroundNode from = route.Segments[0].Edge.FromNode;
        double distFt = GeoMath.DistanceNm(position, from.Position) * GeoMath.FeetPerNm;

        string? refusal = Refusal(layout, position, heading, route, from, distFt);
        if (refusal is not null)
        {
            Log.LogDebug("[ApproachLeg] no leg to node {NodeId} ({DistFt:F0} ft): {Refusal}", from.Id, distFt, refusal);
            return route;
        }

        TaxiRouteSegment leg = VirtualNode.CreateSegment(VirtualNode.Create(position.Lat, position.Lon), from, "RAMP");
        Log.LogDebug("[ApproachLeg] prepended {DistFt:F0} ft free-space leg to node {NodeId}", distFt, from.Id);

        return new TaxiRoute
        {
            Segments = [leg, .. route.Segments],
            HoldShortPoints = route.HoldShortPoints,
            Warnings = route.Warnings,
            ImpliedLanes = route.ImpliedLanes,
            MandatoryConnectorCount = route.MandatoryConnectorCount,
            DestinationParking = route.DestinationParking,
            DestinationSpot = route.DestinationSpot,
            // The leg is one more segment ahead of the spot line-up's pull.
            SpotLineUpPullFromSegment = route.SpotLineUpPullFromSegment + 1,
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

        // An aircraft mid-way along a straight taxi edge has passed neither end of it, so a route leaving an end of that edge
        // by another edge is driven to, however sharply it turns there. A route running along the edge itself is the
        // past-the-start case: the aircraft is already on its first segment, and pure pursuit closes onto it.
        GroundEdge? occupied = OccupiedTaxiEdgeLeadingTo(layout, position, from);
        bool leavesOccupiedEdgeEnd = (occupied is not null) && !ReferenceEquals(route.Segments[0].Edge.Edge, occupied);
        if (!leavesOccupiedEdgeEnd && (PastStartRefusal(position, route, from) is { } pastStart))
        {
            return pastStart;
        }

        if (AlongRunwayBoundFt(layout, position, heading, from) is { } runwayLengthFt)
        {
            return distFt > runwayLengthFt ? $"the roll along the runway is {distFt:F0} ft, beyond the runway's own {runwayLengthFt:F0} ft" : null;
        }

        if (FreeSpaceBoundRefusal(occupied, from, distFt) is { } beyondBound)
        {
            return beyondBound;
        }

        return layout.RunwayCenterlineBetween(position, from.Position) ? "a runway centerline lies between" : null;
    }

    /// <summary>
    /// The refusal for a drive beyond <see cref="RampLaneReposition.MaxCrossingFt"/>, or null when the drive is inside
    /// that bound or runs along <paramref name="occupied"/>, the taxiway edge the aircraft is on that leads to
    /// <paramref name="from"/> (<see cref="OccupiedTaxiEdgeLeadingTo"/>), which waives it.
    /// </summary>
    private static string? FreeSpaceBoundRefusal(GroundEdge? occupied, GroundNode from, double distFt)
    {
        if (distFt <= RampLaneReposition.MaxCrossingFt)
        {
            return null;
        }

        if (occupied is null)
        {
            return $"the drive is {distFt:F0} ft, beyond the {RampLaneReposition.MaxCrossingFt:F0} ft free-space bound";
        }

        Log.LogDebug(
            "[ApproachLeg] {DistFt:F0} ft leg past the {BoundFt:F0} ft bound runs along {Taxiway} edge {First}-{Second} to its node {NodeId}",
            distFt,
            RampLaneReposition.MaxCrossingFt,
            occupied.TaxiwayName,
            occupied.Nodes[0].Id,
            occupied.Nodes[1].Id,
            from.Id
        );
        return null;
    }

    /// <summary>
    /// The straight taxi edge the aircraft is on (<see cref="AirportGroundLayout.FindOccupiedTaxiEdge"/>) when
    /// <paramref name="from"/> is one of that edge's endpoints, else null. The leg to <paramref name="from"/> then follows
    /// the painted centreline the aircraft is already on, however long the edge (issue #880: an aircraft mid-B at SFO,
    /// 600 ft from the next node). The endpoint is usually the one ahead; it is the one behind when the route from the
    /// node ahead would reverse back over this edge, and the aircraft turns about on the taxiway instead.
    /// </summary>
    private static GroundEdge? OccupiedTaxiEdgeLeadingTo(AirportGroundLayout layout, LatLon position, GroundNode from)
    {
        if (layout.FindOccupiedTaxiEdge(position) is not { } occupied)
        {
            return null;
        }

        bool endsAtFrom = (occupied.Nodes[0] == from) || (occupied.Nodes[1] == from);
        return endsAtFrom ? occupied : null;
    }

    /// <summary>
    /// The refusal for an aircraft that has already driven past the route's start node, or null when it has not.
    /// A node more than <see cref="MaxOffRouteBearingDeg"/> off the route's departure bearing is behind the
    /// aircraft; it is refused as "past it" while the aircraft is beyond the first segment's end, or close enough
    /// to the segment's line — within <see cref="MaxOffLineRatio"/> of the length still ahead of it — for pure
    /// pursuit to converge onto it. Further abeam than that, the leg is driven instead.
    /// </summary>
    /// <param name="position">Where the aircraft stands.</param>
    /// <param name="route">The resolved graph route.</param>
    /// <param name="from">The route's start node.</param>
    /// <returns>The refusal, or null.</returns>
    private static string? PastStartRefusal(LatLon position, TaxiRoute route, GroundNode from)
    {
        double departureBearingDeg = route.Segments[0].Edge.DepartureBearing;
        double offRouteDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, from.Position), departureBearingDeg);
        if (offRouteDeg <= MaxOffRouteBearingDeg)
        {
            return null;
        }

        var departureHeading = new TrueHeading(departureBearingDeg);
        double crossTrackFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, from.Position, departureHeading)) * GeoMath.FeetPerNm;
        double alongTrackFt = GeoMath.AlongTrackDistanceNm(position, from.Position, departureHeading) * GeoMath.FeetPerNm;
        double remainingAlongFt = (route.Segments[0].Edge.DistanceNm * GeoMath.FeetPerNm) - alongTrackFt;
        if (remainingAlongFt <= 0.0)
        {
            return $"the node is {offRouteDeg:F0}° off the route's departure bearing — the aircraft is past it, "
                + $"{-remainingAlongFt:F0} ft beyond the first segment's end";
        }

        if (crossTrackFt <= (MaxOffLineRatio * remainingAlongFt))
        {
            return $"the node is {offRouteDeg:F0}° off the route's departure bearing — the aircraft is past it, "
                + $"{crossTrackFt:F0} ft abeam the line with {remainingAlongFt:F0} ft of the first segment left";
        }

        return null;
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

        foreach (IGroundEdge edge in from.Edges)
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
        foreach (GroundRunway runway in layout.Runways)
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
