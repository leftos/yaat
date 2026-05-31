using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Converts <see cref="TaxiRouteSegment"/>s into <see cref="PathPrimitive"/>s
/// for consumption by <c>GroundNavigator</c>. The conversion is a pure
/// function of the segment's <see cref="DirectionalEdge"/>; no
/// <see cref="PhaseContext"/> is needed, which makes the builder trivially
/// unit-testable with synthesised fixtures.
///
/// <para>
/// Two routes through the builder:
/// </para>
///
/// <list type="bullet">
///   <item><b>Straight (<see cref="GroundEdge"/>):</b> bearing is
///         <c>DepartureBearing</c> (which equals <c>BearingTo(from, to)</c>
///         for straights), length is the edge's <c>DistanceNm</c> × feet
///         conversion. From/to lat/lon come from the directional from/to
///         nodes.</item>
///   <item><b>Arc (<see cref="GroundArc"/>):</b> compiles to a
///         <see cref="PathPrimitiveBezier"/> that plays the fillet's actual
///         cubic Bézier. The curve is oriented for traversal so it terminates
///         exactly on the segment's to-node; entry and exit tangent bearings
///         come from <see cref="DirectionalEdge.DepartureBearing"/> and
///         <see cref="DirectionalEdge.ArrivalBearing"/> — which already handle
///         forward/backward traversal of the underlying
///         <c>GroundArc</c>.</item>
/// </list>
/// </summary>
public static class PathPrimitiveBuilder
{
    private static readonly ILogger Log = SimLog.CreateLogger("PathPrimitiveBuilder");

    /// <summary>
    /// Build a <see cref="PathPrimitive"/> for a single <see cref="TaxiRouteSegment"/>.
    /// Straight edges produce a <see cref="PathPrimitiveStraight"/>; <see cref="GroundArc"/>
    /// fillets compile to a <see cref="PathPrimitiveBezier"/> that plays the fillet's actual
    /// cubic Bézier (terminating exactly on the segment's to-node).
    /// </summary>
    public static PathPrimitive FromSegment(TaxiRouteSegment segment)
    {
        var edge = segment.Edge;
        double lengthFt = edge.DistanceNm * GeoMath.FeetPerNm;

        if (edge.Edge is GroundArc arc)
        {
            return BuildBezier(edge, arc, lengthFt);
        }

        return BuildStraight(edge, lengthFt);
    }

    private static PathPrimitiveStraight BuildStraight(DirectionalEdge edge, double lengthFt)
    {
        var from = edge.FromNode;
        var to = edge.ToNode;
        return new PathPrimitiveStraight
        {
            Kind = PathPrimitiveKind.Straight,
            LengthFt = lengthFt,
            ToNodeId = edge.ToNodeId,
            FromLat = from.Position.Lat,
            FromLon = from.Position.Lon,
            ToLat = to.Position.Lat,
            ToLon = to.Position.Lon,
            BearingDeg = edge.DepartureBearing,
        };
    }

    /// <summary>
    /// Compile a <see cref="GroundArc"/> into a <see cref="PathPrimitiveBezier"/> that plays the
    /// fillet's true cubic Bézier. The curve is oriented for traversal: when the route enters the
    /// arc at <c>Nodes[0]</c> the stored Bézier is used as-is (t=0 at the from-node); when it enters
    /// at <c>Nodes[1]</c> the control points are reversed so t still runs from-node → to-node. Because
    /// the endpoints are the graph nodes, playback terminates exactly on the to-node.
    /// </summary>
    private static PathPrimitiveBezier BuildBezier(DirectionalEdge edge, GroundArc arc, double lengthFt)
    {
        var stored = arc.ToBezier();
        bool forward = edge.FromNode.Id == arc.Nodes[0].Id;
        var oriented = forward
            ? stored
            : new CubicBezier(stored.P3Lat, stored.P3Lon, stored.P2Lat, stored.P2Lon, stored.P1Lat, stored.P1Lon, stored.P0Lat, stored.P0Lon);

        return new PathPrimitiveBezier
        {
            Kind = PathPrimitiveKind.Bezier,
            LengthFt = lengthFt,
            ToNodeId = edge.ToNodeId,
            Curve = oriented,
            EntryTangentBearingDeg = edge.DepartureBearing,
            ExitTangentBearingDeg = edge.ArrivalBearing,
        };
    }

    /// <summary>
    /// Build a <see cref="PathPrimitiveSlowTurn"/> from entry pose + desired exit
    /// heading. The turn direction is the short-way rotation from
    /// <paramref name="fromHdgDeg"/> to <paramref name="toHdgDeg"/>; the arc
    /// centre sits at <paramref name="radiusFt"/> perpendicular-inward from the
    /// entry point on the turn side.
    ///
    /// <para>
    /// Used by callers that need a programmatic tight turn (not derived from a
    /// <see cref="GroundArc"/>) — e.g. <c>LineUpPhase</c>'s pivot states.
    /// </para>
    /// </summary>
    /// <param name="fromLat">Entry-point latitude (degrees).</param>
    /// <param name="fromLon">Entry-point longitude (degrees).</param>
    /// <param name="fromHdgDeg">Tangent heading at entry (degrees true, 0–360).</param>
    /// <param name="toHdgDeg">Tangent heading at exit (degrees true, 0–360).</param>
    /// <param name="radiusFt">Turn radius in feet. Typically <see cref="CategoryPerformance.NoseWheelTurnRadiusFt"/>.</param>
    /// <param name="maxSpeedKts">Target-speed cap in knots. Typically <see cref="CategoryPerformance.SlowTurnSpeedKts"/>.</param>
    /// <param name="toNodeId">Synthetic end-of-primitive node id for arrival detection.</param>
    public static PathPrimitiveSlowTurn SlowTurn(
        double fromLat,
        double fromLon,
        double fromHdgDeg,
        double toHdgDeg,
        double radiusFt,
        double maxSpeedKts,
        int toNodeId
    )
    {
        // Short-way signed turn angle, normalised to (-180, 180].
        double dthetaDeg = (((toHdgDeg - fromHdgDeg) + 540.0) % 360.0) - 180.0;
        double sweepDeg = Math.Abs(dthetaDeg);
        bool rightTurn = dthetaDeg > 0;

        // Centre is perpendicular-inward from entry at radius distance.
        // Right turn: centre 90° clockwise of entry tangent; left: 90° CCW.
        double perpHdgDeg = ((fromHdgDeg + (rightTurn ? 90.0 : -90.0)) + 360.0) % 360.0;
        double radiusNm = radiusFt / GeoMath.FeetPerNm;
        var (centerLat, centerLon) = GeoMath.ProjectPoint(fromLat, fromLon, new TrueHeading(perpHdgDeg), radiusNm);

        // Entry point relative to centre sits on the radial opposite perpHdg.
        double startBearingFromCenterDeg = ((perpHdgDeg + 180.0) % 360.0 + 360.0) % 360.0;
        double lengthFt = sweepDeg * radiusFt * Math.PI / 180.0;

        return new PathPrimitiveSlowTurn
        {
            Kind = PathPrimitiveKind.SlowTurn,
            LengthFt = lengthFt,
            ToNodeId = toNodeId,
            CenterLat = centerLat,
            CenterLon = centerLon,
            RadiusFt = radiusFt,
            StartBearingFromCenterDeg = startBearingFromCenterDeg,
            SweepDeg = sweepDeg,
            RightTurn = rightTurn,
            EntryTangentBearingDeg = ((fromHdgDeg % 360.0) + 360.0) % 360.0,
            ExitTangentBearingDeg = ((toHdgDeg % 360.0) + 360.0) % 360.0,
            MaxSpeedKts = maxSpeedKts,
        };
    }
}
