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
    /// Build a <see cref="PathPrimitiveSlowTurn"/> whose exit tangent runs <em>through</em> the point
    /// (<paramref name="targetLat"/>, <paramref name="targetLon"/>): the Dubins arc-then-tangent-line
    /// solution, an arc of <paramref name="radiusFt"/> off the entry pose that rolls out on the line to the
    /// point. Aiming at a bearing instead leaves the roll-out laterally offset from that line by as much as
    /// the arc diameter — fine when the segment has a painted centerline for pure pursuit to re-acquire, wrong
    /// for a free-space leg whose "line" is defined only by its two endpoints.
    ///
    /// <para>
    /// Both turn directions are solved (<see cref="SlowTurnToPointDirected"/>); the one with the smaller sweep
    /// wins, and the arc is built with that direction and sweep explicitly — not through
    /// <see cref="SlowTurn"/>'s short-way rotation, which cannot express the over-half turn a point behind the
    /// aircraft needs. Returns null when the point lies inside a turning circle (no tangent through it exists)
    /// and when both solutions sweep past <see cref="MaxAimSweepDeg"/> — the caller falls back to aiming at the
    /// segment's bearing.
    /// </para>
    /// </summary>
    /// <param name="fromLat">Entry-point latitude (degrees).</param>
    /// <param name="fromLon">Entry-point longitude (degrees).</param>
    /// <param name="fromHdgDeg">Tangent heading at entry (degrees true, 0–360).</param>
    /// <param name="radiusFt">Turn radius in feet. Typically <see cref="CategoryPerformance.NoseWheelTurnRadiusFt"/>.</param>
    /// <param name="targetLat">Latitude of the point the exit tangent must run through.</param>
    /// <param name="targetLon">Longitude of the point the exit tangent must run through.</param>
    /// <param name="maxSpeedKts">Target-speed cap in knots.</param>
    /// <param name="toNodeId">Synthetic end-of-primitive node id for arrival detection.</param>
    public static PathPrimitiveSlowTurn? SlowTurnToPoint(
        double fromLat,
        double fromLon,
        double fromHdgDeg,
        double radiusFt,
        double targetLat,
        double targetLon,
        double maxSpeedKts,
        int toNodeId
    )
    {
        var right = SlowTurnToPointDirected(fromLat, fromLon, fromHdgDeg, radiusFt, targetLat, targetLon, maxSpeedKts, toNodeId, rightTurn: true);
        var left = SlowTurnToPointDirected(fromLat, fromLon, fromHdgDeg, radiusFt, targetLat, targetLon, maxSpeedKts, toNodeId, rightTurn: false);

        if (right is null)
        {
            return left;
        }

        return ((left is { } l) && (l.SweepDeg < right.SweepDeg)) ? l : right;
    }

    /// <summary>
    /// The one-sided <see cref="SlowTurnToPoint"/>: the arc-then-tangent-line solution restricted to the turn
    /// direction <paramref name="rightTurn"/> names, however far round that side has to sweep. Null when that
    /// side has no tangent at all (the point lies inside its turning circle) or reaching it would sweep past
    /// <see cref="MaxAimSweepDeg"/> — the caller falls back to aiming at a bearing.
    ///
    /// <para>
    /// The direction is the caller's to choose when something outside the geometry has already settled it: a
    /// reversal turned against its short way so the route's next turn unwinds it rather than compounding with
    /// it still has to roll out on the line to its node, and the smaller-sweep side <see cref="SlowTurnToPoint"/>
    /// would pick is exactly the side that was ruled out.
    /// </para>
    /// </summary>
    /// <param name="fromLat">Entry-point latitude (degrees).</param>
    /// <param name="fromLon">Entry-point longitude (degrees).</param>
    /// <param name="fromHdgDeg">Tangent heading at entry (degrees true, 0–360).</param>
    /// <param name="radiusFt">Turn radius in feet. Typically <see cref="CategoryPerformance.NoseWheelTurnRadiusFt"/>.</param>
    /// <param name="targetLat">Latitude of the point the exit tangent must run through.</param>
    /// <param name="targetLon">Longitude of the point the exit tangent must run through.</param>
    /// <param name="maxSpeedKts">Target-speed cap in knots.</param>
    /// <param name="toNodeId">Synthetic end-of-primitive node id for arrival detection.</param>
    /// <param name="rightTurn">True to sweep clockwise from the entry tangent onto the line, false counter-clockwise.</param>
    public static PathPrimitiveSlowTurn? SlowTurnToPointDirected(
        double fromLat,
        double fromLon,
        double fromHdgDeg,
        double radiusFt,
        double targetLat,
        double targetLon,
        double maxSpeedKts,
        int toNodeId,
        bool rightTurn
    )
    {
        if (TangentExit(fromLat, fromLon, fromHdgDeg, radiusFt, targetLat, targetLon, rightTurn) is not { } exit || (exit.SweepDeg > MaxAimSweepDeg))
        {
            Log.LogDebug(
                "[PathPrimitive] SlowTurnToPointDirected: no {Sense} tangent to ({TargetLat:F6},{TargetLon:F6}) at r={R:F0}ft from hdg {Hdg:F0}",
                rightTurn ? "right" : "left",
                targetLat,
                targetLon,
                radiusFt,
                fromHdgDeg
            );
            return null;
        }

        return BuildSlowTurn(fromLat, fromLon, fromHdgDeg, radiusFt, rightTurn, exit.SweepDeg, exit.ExitHeadingDeg, maxSpeedKts, toNodeId);
    }

    /// <summary>
    /// The most a point-aimed alignment arc (<see cref="SlowTurnToPoint"/>) may sweep. A reversal on open apron
    /// legitimately over-rotates past a half turn to line up on the point — the tangent to a node 100 ft behind
    /// the tail wants ~196° at a jet's nose-wheel radius — so the cap is not 180°; it keeps headroom under the
    /// navigator's 360° orbit invariant, which would otherwise see a legitimate aim as a pure-pursuit orbit.
    /// </summary>
    public const double MaxAimSweepDeg = 270.0;

    /// <summary>
    /// The tangent solution for one turn direction: the arc of <paramref name="radiusFt"/> on that side of the
    /// entry pose, swept until its tangent points at the target. Null when the target lies inside the circle.
    /// </summary>
    private static (double SweepDeg, double ExitHeadingDeg)? TangentExit(
        double fromLat,
        double fromLon,
        double fromHdgDeg,
        double radiusFt,
        double targetLat,
        double targetLon,
        bool rightTurn
    )
    {
        double perpHdgDeg = fromHdgDeg + (rightTurn ? 90.0 : -90.0);
        var (centerLat, centerLon) = GeoMath.ProjectPoint(fromLat, fromLon, new TrueHeading(perpHdgDeg), radiusFt / GeoMath.FeetPerNm);

        double centerToTargetFt = GeoMath.DistanceNm(centerLat, centerLon, targetLat, targetLon) * GeoMath.FeetPerNm;
        if (centerToTargetFt <= radiusFt)
        {
            return null;
        }

        // Tangent-point radial: the centre-to-target bearing rotated back by the half-angle of the tangent
        // triangle, on the side the turn runs toward. The exit tangent is perpendicular to that radial.
        double halfAngleDeg = Math.Acos(radiusFt / centerToTargetFt) * 180.0 / Math.PI;
        double centerToTargetDeg = GeoMath.BearingTo(centerLat, centerLon, targetLat, targetLon);
        double tangentRadialDeg = rightTurn ? centerToTargetDeg - halfAngleDeg : centerToTargetDeg + halfAngleDeg;
        double exitHeadingDeg = Normalise360(tangentRadialDeg + (rightTurn ? 90.0 : -90.0));
        double sweepDeg = rightTurn ? Normalise360(exitHeadingDeg - fromHdgDeg) : Normalise360(fromHdgDeg - exitHeadingDeg);

        return (sweepDeg, exitHeadingDeg);
    }

    private static double Normalise360(double degrees) => ((degrees % 360.0) + 360.0) % 360.0;

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
        return SlowTurnDirected(fromLat, fromLon, fromHdgDeg, toHdgDeg, radiusFt, maxSpeedKts, toNodeId, dthetaDeg > 0);
    }

    /// <summary>
    /// Build a <see cref="PathPrimitiveSlowTurn"/> from entry pose to exit heading turning the way the caller
    /// names, however far round that is: the sweep is the whole rotation on that side, so a caller that names
    /// the long way round gets an arc of more than a half turn rather than the short-way arc in the opposite
    /// sense. <see cref="SlowTurn"/> is this with the short way chosen for it.
    ///
    /// <para>
    /// The direction is the caller's to choose when the two sides are equivalent — a reversal, where the short
    /// way is a coin flip — or when the route's next turn makes one side cheaper than the other.
    /// </para>
    /// </summary>
    /// <param name="fromLat">Entry-point latitude (degrees).</param>
    /// <param name="fromLon">Entry-point longitude (degrees).</param>
    /// <param name="fromHdgDeg">Tangent heading at entry (degrees true, 0–360).</param>
    /// <param name="toHdgDeg">Tangent heading at exit (degrees true, 0–360).</param>
    /// <param name="radiusFt">Turn radius in feet. Typically <see cref="CategoryPerformance.NoseWheelTurnRadiusFt"/>.</param>
    /// <param name="maxSpeedKts">Target-speed cap in knots. Typically <see cref="CategoryPerformance.SlowTurnSpeedKts"/>.</param>
    /// <param name="toNodeId">Synthetic end-of-primitive node id for arrival detection.</param>
    /// <param name="rightTurn">True to sweep clockwise from entry to exit heading, false to sweep counter-clockwise.</param>
    public static PathPrimitiveSlowTurn SlowTurnDirected(
        double fromLat,
        double fromLon,
        double fromHdgDeg,
        double toHdgDeg,
        double radiusFt,
        double maxSpeedKts,
        int toNodeId,
        bool rightTurn
    )
    {
        double sweepDeg = rightTurn ? Normalise360(toHdgDeg - fromHdgDeg) : Normalise360(fromHdgDeg - toHdgDeg);
        return BuildSlowTurn(fromLat, fromLon, fromHdgDeg, radiusFt, rightTurn, sweepDeg, toHdgDeg, maxSpeedKts, toNodeId);
    }

    /// <summary>
    /// The circle geometry every slow-turn shares: the centre is perpendicular-inward from the entry point at
    /// <paramref name="radiusFt"/> (90° clockwise of the entry tangent on a right turn, counter-clockwise on a left),
    /// the entry point sits on the opposite radial, and the arc length is the sweep along that radius. Direction
    /// and sweep are the caller's: <see cref="SlowTurn"/> derives them from the short way between two headings,
    /// <see cref="SlowTurnDirected"/> takes the direction as given, and <see cref="SlowTurnToPoint"/> derives it
    /// from the tangent to a point — the last two may sweep the long way round.
    /// </summary>
    private static PathPrimitiveSlowTurn BuildSlowTurn(
        double fromLat,
        double fromLon,
        double fromHdgDeg,
        double radiusFt,
        bool rightTurn,
        double sweepDeg,
        double exitHdgDeg,
        double maxSpeedKts,
        int toNodeId
    )
    {
        double perpHdgDeg = Normalise360(fromHdgDeg + (rightTurn ? 90.0 : -90.0));
        double radiusNm = radiusFt / GeoMath.FeetPerNm;
        var (centerLat, centerLon) = GeoMath.ProjectPoint(fromLat, fromLon, new TrueHeading(perpHdgDeg), radiusNm);

        double startBearingFromCenterDeg = Normalise360(perpHdgDeg + 180.0);
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
            EntryTangentBearingDeg = Normalise360(fromHdgDeg),
            ExitTangentBearingDeg = Normalise360(exitHdgDeg),
            MaxSpeedKts = maxSpeedKts,
        };
    }
}
